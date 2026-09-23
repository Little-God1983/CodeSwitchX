using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryService>());
        return services;
    }
}
