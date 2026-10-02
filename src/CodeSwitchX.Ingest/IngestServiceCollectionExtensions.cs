using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Ingest;

/// <summary>
/// Two registrations rather than one, because hosted services start in registration order and the two halves belong in
/// different places: the Event API opens the hook pipe as soon as it starts and must not wait behind a slow start, while
/// the indexer's first scan publishes usage and must start after every consumer of it.
/// </summary>
public static class IngestServiceCollectionExtensions
{
    /// <summary>The Event API (the hook pipe and its token), the stops it hands the hooks, the asks it holds, and the hook installer.</summary>
    public static IServiceCollection AddCodeSwitchXEventApi(this IServiceCollection services, Action<EventApiOptions>? configure = null)
    {
        var options = new EventApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<AccessTokenStore>();
        services.AddSingleton<TurnStops>();
        services.AddSingleton<ChatAsks>();
        services.AddSingleton<EventApiService>();
        services.AddHostedService(sp => sp.GetRequiredService<EventApiService>());
        services.AddSingleton<ClaudeHookInstaller>();
        return services;
    }

    /// <summary>The transcript indexer. Register it after every hosted consumer of <c>TranscriptUpdated</c>.</summary>
    public static IServiceCollection AddCodeSwitchXTranscriptIndexer(this IServiceCollection services)
    {
        services.AddSingleton<TranscriptIndexerOptions>();
        services.AddSingleton<TranscriptIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<TranscriptIndexer>());
        return services;
    }
}
