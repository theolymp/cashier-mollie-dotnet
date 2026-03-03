using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CashierMollie.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers CashierMollie services in the DI container.
    /// Call AddMollieApi() from Mollie.Api separately to register Mollie API clients.
    /// </summary>
    public static IServiceCollection AddCashierMollie<TKey>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DbContextOptionsBuilder>? dbContextOptions = null)
        where TKey : IEquatable<TKey>
    {
        // Configuration
        services.Configure<CashierMollieOptions>(
            configuration.GetSection(CashierMollieOptions.SectionName));

        // EF Core DbContext
        if (dbContextOptions != null)
            services.AddDbContext<CashierDbContext<TKey>>(dbContextOptions);

        // CashierMollie services
        services.AddScoped<IMollieClientService, MollieClientService>();
        services.AddScoped<IBillingEngine<TKey>, MollieBillingEngine<TKey>>();
        services.AddScoped<ICashierService<TKey>, CashierService<TKey>>();
        services.AddScoped<IWebhookService, WebhookService<TKey>>();

        // Default no-op event dispatcher (consumer can replace)
        services.TryAddScoped<ICashierEventDispatcher, NullCashierEventDispatcher>();

        return services;
    }
}
