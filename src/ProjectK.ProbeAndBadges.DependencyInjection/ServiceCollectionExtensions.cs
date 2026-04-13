using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProbeAndBadgesApi(
        this IServiceCollection services,
        Action<BadgesCatalogOptions>? configure = null)
    {
        services.AddOptions<BadgesCatalogOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<BadgesCatalog>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BadgesCatalogOptions>>().Value;
            var logger = sp.GetService<ILogger<BadgesCatalog>>();
            return new BadgesCatalog(options, logger);
        });

        services.TryAddSingleton<IBadgesCatalog>(sp => sp.GetRequiredService<BadgesCatalog>());
        services.TryAddSingleton<IBadgesAssetsStore>(sp => sp.GetRequiredService<BadgesCatalog>());

        return services;
    }
}
