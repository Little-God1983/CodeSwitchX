using CodeSwitchX.Ingest.Api;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Ingest;

public static class IngestServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXIngest(this IServiceCollection services, Action<EventApiOptions>? configure = null)
    {
        var options = new EventApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<AccessTokenStore>();
        services.AddSingleton<EventApiService>();
        services.AddHostedService(sp => sp.GetRequiredService<EventApiService>());
        services.AddSingleton<TranscriptIndexerOptions>();
        services.AddSingleton<TranscriptIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<TranscriptIndexer>());
        services.AddSingleton<ClaudeHookInstaller>();
        return services;
    }
}
