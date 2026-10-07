using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
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
/// In-process Kestrel endpoint that receives relayed hook payloads and publishes them on the bus, and answers a tool event
/// with the stop asked for its chat's turn, if any (<see cref="TurnStops"/>). It holds a chat's question until the user
/// answers it here or leaves it to VS Code (<see cref="ChatAsks"/>). Given the Yard, it also
/// serves the MCP tools Raven's brain looks at it through (<see cref="YardTools"/>), and given what can be done on it,
/// those it acts through (<see cref="YardActionTools"/>), on the loopback port under <see cref="YardMcp.Route"/>, behind the
/// same token, and writes <c>mcp.json</c> for Claude Code to find them.
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
    private readonly IYardActions? _actions;
    private readonly TurnStops? _stops;
    private readonly ChatAsks? _asks;
    private readonly IAppSettings? _settings;
    private readonly RavenMessages? _ravenMessages;
    private WebApplication? _app;

    /// <summary>What a relay that hands a stop on sends along (CodeSwitchX.Hook's <c>Relay.StopsHeader</c>).</summary>
    internal const string RelayStopsHeader = "X-CodeSwitchX-Relay-Stops";

    /// <param name="stops">The stops asked for chats' turns, handed to their hook relay in its answer; null for none.</param>
    /// <param name="asks">Where what chats ask is held while the user answers it here; null leaves every ask to VS Code.</param>
    /// <param name="settings">The app's settings as the brain reads and changes them (#126); null for no settings tools.</param>
    /// <param name="ravenMessages">What a chat is told along with a message from Raven (#181); null tells it nothing.</param>
    public EventApiService(AppPaths paths, IEventBus bus, AccessTokenStore tokens, TimeProvider time,
        ILoggerFactory loggerFactory, EventApiOptions options, IYardDirectory? yard = null, IYardActions? actions = null, TurnStops? stops = null,
        ChatAsks? asks = null, IAppSettings? settings = null, RavenMessages? ravenMessages = null)
    {
        _ravenMessages = ravenMessages;
        _paths = paths;
        _bus = bus;
        _tokens = tokens;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EventApiService>();
        _options = options;
        _yard = yard;
        _actions = actions;
        _stops = stops;
        _asks = asks;
        _settings = settings;
        if (stops is not null && asks is not null)
        {
            // A held question holds the step a stop waits for: the stop goes back in the question's answer (/asks).
            stops.Requested += asks.Stop;
        }
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
            // The Raven chat a tool call comes from: a window's chat sends its window, which the tools act on by default.
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddTransient(sp => ChatScope.Of(sp.GetRequiredService<IHttpContextAccessor>().HttpContext));
            if (_asks is not null)
            {
                builder.Services.AddSingleton(_asks);
            }

            var mcp = builder.Services.AddMcpServer(mcp => mcp.ServerInfo = new() { Name = "CodeSwitchX", Version = AppVersion.Current })
                .WithHttpTransport(http => http.Stateless = true)
                .WithTools<YardTools>();
            if (_actions is not null)
            {
                builder.Services.AddSingleton(_actions);
                mcp.WithTools<YardActionTools>();
            }

            if (_settings is not null)
            {
                builder.Services.AddSingleton(_settings);
                mcp.WithTools<SettingsTools>();
            }
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

            // A prompt is no step, so it never carries a stop: what it may carry is what the chat is told along with it.
            if (_ravenMessages?.ContextFor(hookEvent) is { } told)
            {
                return Results.Ok(new { context = told });
            }

            // The relay waits for this answer anyway: a stop asked for the chat's turn travels in it, if the relay hands it on.
            if (_stops is null)
            {
                return Results.Accepted();
            }

            if (_stops.Take(hookEvent, context.Request.Headers.ContainsKey(RelayStopsHeader)) is not { } reason)
            {
                return Results.Accepted();
            }

            // Claude Code ends the turn on this answer and sends no Stop hook of its own: the Yard hears the end from here.
            _bus.Publish(new HookEventReceived(TurnStops.EndOf(hookEvent)));
            return Results.Ok(new { stop = reason });
        });

        // A chat's question, from its relay's Ask hook, or its permission prompt, from its Permit hook; either waits for the
        // answer: 200 with one answer per question or with the permit, 200 with a stop when the user stopped the chat
        // meanwhile, or 204 for VS Code to ask it in the chat's tab.
        app.MapPost("/asks", async (HttpContext context) =>
        {
            if (!IsAuthorized(context, token))
            {
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            if (_asks is null || ChatAskParser.Parse(body, _time.GetUtcNow()) is not { } ask)
            {
                return Results.NoContent();
            }

            var closed = await _asks.HoldAsync(ask, context.RequestAborted);
            if (closed is { Outcome: ChatAskOutcome.Answered, Answers: { } answers })
            {
                return Results.Ok(new { answers });
            }

            if (closed is { Outcome: ChatAskOutcome.Answered, Permit: { } permit })
            {
                // Allowed for good (#109): the suggestion the user clicked goes back as Claude Code sent it, and Claude Code
                // writes the rule itself.
                var updated = permit.Always is { } always ? new[] { System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(always.Json) } : null;
                return Results.Ok(new { permit = new { allow = permit.Allow, message = permit.Message, updatedPermissions = updated } });
            }

            // Only a relay with the Ask or Permit hook asks here, and every one of those hands a stop on. The step a permission
            // prompt holds is the tool use it asks for, which the stop keeps from running as on its PreToolUse.
            var step = ask.Kind == ChatAskKind.Permission ? ask.Step with { EventName = "PreToolUse" } : ask.Step;
            if (closed is { Outcome: ChatAskOutcome.Stopped } && _stops?.Take(step, relayHandsItOn: true) is { } reason)
            {
                // Ended now, not when it asked: the Yard counts the chat idle from here.
                _bus.Publish(new HookEventReceived(TurnStops.EndOf(ask.Step with { At = _time.GetUtcNow() })));
                return Results.Ok(new { stop = reason });
            }

            return Results.NoContent();
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
