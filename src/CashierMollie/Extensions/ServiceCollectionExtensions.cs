using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CashierMollie.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers CashierMollie services in the DI container.
    /// </summary>
    public static IServiceCollection AddCashierMollie<TKey>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TKey : IEquatable<TKey>
    {
        services.Configure<CashierMollieOptions>(
            configuration.GetSection(CashierMollieOptions.SectionName));

        // TODO: Register MollieClientService, SubscriptionService, WebhookService

        return services;
    }
}
