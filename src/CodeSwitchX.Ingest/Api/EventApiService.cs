using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Ingest.Hooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Api;

/// <summary>In-process Kestrel endpoint that receives relayed hook payloads and publishes them on the bus.</summary>
public sealed class EventApiService : IHostedService
{
    private readonly AppPaths _paths;
    private readonly IEventBus _bus;
    private readonly AccessTokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly EventApiOptions _options;
    private WebApplication? _app;

    public EventApiService(AppPaths paths, IEventBus bus, AccessTokenStore tokens, TimeProvider time,
        ILoggerFactory loggerFactory, EventApiOptions options)
    {
        _paths = paths;
        _bus = bus;
        _tokens = tokens;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EventApiService>();
        _options = options;
    }

    public EndpointDescriptor? Endpoint { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var token = _tokens.GetOrCreate();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "CodeSwitchX.Ingest" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        // Only the current user may connect to the pipe; the relay verifies the server's owner the same way.
        builder.WebHost.UseNamedPipes(pipes => pipes.CurrentUserOnly = true);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = _options.MaxBodyBytes;
            if (_options.EnableNamedPipe)
            {
                kestrel.ListenNamedPipe(_options.PipeName);
            }

            if (_options.EnableLoopback)
            {
                // 127.0.0.1 only (never the "localhost" name): the spec forbids any other binding and Kestrel
                // only supports dynamic ports on explicit loopback addresses.
                kestrel.Listen(System.Net.IPAddress.Loopback, _options.LoopbackPort);
            }
        });

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { pid = Environment.ProcessId, product = AppPaths.ProductFolderName }));

        app.MapPost("/events", async (HttpContext context) =>
        {
            if (!IsAuthorized(context, token))
            {
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var hookEvent = HookEnvelopeParser.Parse(body, _time.GetUtcNow());
            if (hookEvent is null)
            {
                _logger.LogWarning("Rejected hook payload of {Length} bytes: not a recognised envelope", body.Length);
                return Results.BadRequest(new { error = "payload is not a hook envelope with a session_id" });
            }

            _bus.Publish(new HookEventReceived(hookEvent));
            return Results.Accepted();
        });

        await app.StartAsync(cancellationToken);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var port = addresses
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) && uri.Host != "pipe" ? uri.Port : 0)
            .FirstOrDefault(p => p > 0);

        Endpoint = new EndpointDescriptor(_options.EnableNamedPipe ? _options.PipeName : string.Empty, port, Environment.ProcessId, _time.GetUtcNow());
        Endpoint.Write(_paths.EndpointFile);
        _logger.LogInformation("Event API listening on pipe {Pipe} and port {Port}", Endpoint.PipeName, Endpoint.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            File.Delete(_paths.EndpointFile);
        }
        catch (IOException)
        {
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    private static bool IsAuthorized(HttpContext context, string token)
    {
        string? candidate = null;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = authorization["Bearer ".Length..].Trim();
        }
        else if (context.Request.Headers.TryGetValue("X-CodeSwitchX-Token", out var header))
        {
            candidate = header.ToString().Trim();
        }

        return AccessTokenStore.Matches(token, candidate);
    }
}
