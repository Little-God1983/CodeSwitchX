using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Ingest.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Api;

/// <summary>
/// In-process Kestrel endpoint that receives relayed hook payloads and publishes them on the bus. Given the Yard, it also
/// serves the read-only MCP tools Raven's brain looks at it through (<see cref="YardTools"/>), on the loopback port under
/// <see cref="YardMcp.Route"/>, behind the same token, and writes <c>mcp.json</c> for Claude Code to find them.
/// </summary>
public sealed class EventApiService : IHostedService
{
    private readonly AppPaths _paths;
    private readonly IEventBus _bus;
    private readonly AccessTokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly EventApiOptions _options;
    private readonly IYardDirectory? _yard;
    private WebApplication? _app;

    public EventApiService(AppPaths paths, IEventBus bus, AccessTokenStore tokens, TimeProvider time,
        ILoggerFactory loggerFactory, EventApiOptions options, IYardDirectory? yard = null)
    {
        _paths = paths;
        _bus = bus;
        _tokens = tokens;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EventApiService>();
        _options = options;
        _yard = yard;
    }

    public EndpointDescriptor? Endpoint { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var token = _tokens.GetOrCreate();

        // An empty builder reads no configuration. The defaults parse appsettings.json in the current directory (an ASP.NET
        // Core project, when started from its terminal) and the environment: a malformed file failed the start, and Kestrel
        // bound every Kestrel:Endpoints entry they held next to the code's listeners.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "CodeSwitchX.Ingest" });
        builder.WebHost.UseKestrelCore();
        builder.Services.AddRoutingCore();
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

        if (_yard is not null)
        {
            // Stateless: every request stands alone, so a restarted brain or app needs no session to be re-established.
            builder.Services.AddSingleton(_yard);
            builder.Services.AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "CodeSwitchX", Version = AppVersion.Current })
                .WithHttpTransport(http => http.Stateless = true)
                .WithTools<YardTools>();
        }

        var app = builder.Build();

        // Before routing picks an endpoint: the MCP tools answer only with the token, like /events.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(YardMcp.Route) && !IsAuthorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

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

        if (_yard is not null)
        {
            app.MapMcp(YardMcp.Route);
        }

        await app.StartAsync(cancellationToken);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var port = addresses
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) && uri.Host != "pipe" ? uri.Port : 0)
            .FirstOrDefault(p => p > 0);

        Endpoint = new EndpointDescriptor(_options.EnableNamedPipe ? _options.PipeName : string.Empty, port, Environment.ProcessId, _time.GetUtcNow(),
            EndpointDescriptor.CurrentProcessStartedAtUtc());
        Endpoint.Write(_paths.EndpointFile);
        _logger.LogInformation("Event API listening on pipe {Pipe} and port {Port}", Endpoint.PipeName, Endpoint.Port);
        if (_yard is not null && port > 0)
        {
            WriteMcpConfig(port, token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        foreach (var file in new[] { _paths.EndpointFile, _paths.McpConfigFile })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stopping goes on: the server is what must stop, and a file left behind is replaced at the next start.
            }
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    /// <summary>
    /// Raven is optional, the hook pipe is not: a file that cannot be written (held open by a scanner, an ACL that will
    /// not take) leaves Raven without the Yard, and the app runs on. A file left from an earlier run is removed, so the
    /// brain says the Yard cannot be seen rather than call an address that is gone.
    /// </summary>
    private void WriteMcpConfig(int port, string token)
    {
        try
        {
            McpConfigFile.Write(_paths.McpConfigFile, port, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not write {File}; Raven cannot see the Yard", _paths.McpConfigFile);
            try
            {
                File.Delete(_paths.McpConfigFile);
            }
            catch (Exception deleteFailed) when (deleteFailed is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(deleteFailed, "Could not remove the stale {File} either", _paths.McpConfigFile);
            }
        }
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
