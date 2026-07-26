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
        services.Configure<CashierMollieOptions>(
            configuration.GetSection(CashierMollieOptions.SectionName));
        // Note: ApiKey is not validated here because consumers may set it after registration
        // (e.g. from a secret store). MollieClientService validates at runtime.

        if (dbContextOptions != null)
            services.AddDbContext<CashierDbContext<TKey>>(dbContextOptions);

        // Core services
        services.AddScoped<IMollieClientService, MollieClientService>();
        services.AddScoped<ICashierService<TKey>, CashierService<TKey>>();
        services.AddScoped<IWebhookService, WebhookService<TKey>>();

        // Billing engine (configured via options)
        var opts = configuration.GetSection(CashierMollieOptions.SectionName).Get<CashierMollieOptions>()
            ?? new CashierMollieOptions();
        if (opts.BillingEngine == BillingEngineType.Managed)
        {
            services.AddScoped<IBillingEngine<TKey>, ManagedBillingEngine<TKey>>();
            services.AddHostedService<CashierBackgroundService<TKey>>();
        }
        else
        {
            services.AddScoped<IBillingEngine<TKey>, MollieBillingEngine<TKey>>();
        }

        // Startup diagnostics: surfaces the effective billing engine and warns about a webhook URL
        // Mollie cannot call back. Both failure modes are otherwise silent until real payments run.
        services.AddHostedService<CashierStartupDiagnostics>();

        // Feature services
        services.AddScoped<ICouponService<TKey>, CouponService<TKey>>();
        services.AddScoped<ICreditService<TKey>, CreditService<TKey>>();
        services.AddScoped<IRefundService<TKey>, RefundService<TKey>>();

        // Replaceable defaults (NullCashierEventDispatcher is stateless — singleton is sufficient)
        services.TryAddSingleton<ICashierEventDispatcher, NullCashierEventDispatcher>();
        services.TryAddScoped<ICouponRepository, ConfigCouponRepository>();
        services.TryAddScoped<IInvoiceGenerator<TKey>, NullInvoiceGenerator<TKey>>();

        return services;
    }
}
