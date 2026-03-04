using CashierMollie.Data;
using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using CashierMollie.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CashierMollie.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
    }

    private static IServiceCollection CreateServicesWithDefaults(
        Dictionary<string, string?>? configValues = null,
        Action<DbContextOptionsBuilder>? dbContextOptions = null)
    {
        var services = new ServiceCollection();
        var config = BuildConfig(configValues);
        services.AddCashierMollie<string>(config, dbContextOptions);
        return services;
    }

    // ----------------------------------------------------------------
    // Core service registrations
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_RegistersCashierService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ICashierService<string>) &&
            d.ImplementationType == typeof(CashierService<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_RegistersWebhookService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IWebhookService) &&
            d.ImplementationType == typeof(WebhookService<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_RegistersMollieClientService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IMollieClientService) &&
            d.ImplementationType == typeof(MollieClientService) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    // ----------------------------------------------------------------
    // Feature service registrations
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_RegistersCouponService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ICouponService<string>) &&
            d.ImplementationType == typeof(CouponService<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_RegistersCreditService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ICreditService<string>) &&
            d.ImplementationType == typeof(CreditService<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_RegistersRefundService()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IRefundService<string>) &&
            d.ImplementationType == typeof(RefundService<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    // ----------------------------------------------------------------
    // Billing engine selection
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_DefaultEngine_RegistersMollieBillingEngine()
    {
        // No BillingEngine config => defaults to MollieNative
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBillingEngine<string>) &&
            d.ImplementationType == typeof(MollieBillingEngine<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);

        // Should NOT register managed engine or background service
        Assert.DoesNotContain(services, d =>
            d.ImplementationType == typeof(ManagedBillingEngine<string>));
        Assert.DoesNotContain(services, d =>
            d.ImplementationType == typeof(CashierBackgroundService<string>));
    }

    [Fact]
    public void AddCashierMollie_ManagedEngine_RegistersManagedBillingEngine()
    {
        var services = CreateServicesWithDefaults(new Dictionary<string, string?>
        {
            ["CashierMollie:BillingEngine"] = "Managed",
        });

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IBillingEngine<string>) &&
            d.ImplementationType == typeof(ManagedBillingEngine<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);

        // Should NOT register the native engine
        Assert.DoesNotContain(services, d =>
            d.ImplementationType == typeof(MollieBillingEngine<string>));
    }

    [Fact]
    public void AddCashierMollie_ManagedEngine_RegistersBackgroundService()
    {
        var services = CreateServicesWithDefaults(new Dictionary<string, string?>
        {
            ["CashierMollie:BillingEngine"] = "Managed",
        });

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(CashierBackgroundService<string>));
    }

    // ----------------------------------------------------------------
    // Replaceable defaults (TryAddScoped)
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_RegistersDefaultEventDispatcher()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ICashierEventDispatcher) &&
            d.ImplementationType == typeof(NullCashierEventDispatcher) &&
            d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddCashierMollie_RegistersDefaultCouponRepository()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ICouponRepository) &&
            d.ImplementationType == typeof(ConfigCouponRepository) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_RegistersDefaultInvoiceGenerator()
    {
        var services = CreateServicesWithDefaults();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IInvoiceGenerator<string>) &&
            d.ImplementationType == typeof(NullInvoiceGenerator<string>) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddCashierMollie_CustomEventDispatcher_ReplacesDefault()
    {
        // Register a custom dispatcher BEFORE calling AddCashierMollie.
        // TryAddScoped should NOT override it.
        var services = new ServiceCollection();
        services.AddScoped<ICashierEventDispatcher, CustomTestEventDispatcher>();

        var config = BuildConfig();
        services.AddCashierMollie<string>(config);

        // Only our custom dispatcher should be registered for this interface
        var descriptors = services
            .Where(d => d.ServiceType == typeof(ICashierEventDispatcher))
            .ToList();

        Assert.Single(descriptors);
        Assert.Equal(typeof(CustomTestEventDispatcher), descriptors[0].ImplementationType);
    }

    // ----------------------------------------------------------------
    // DbContext registration
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_WithDbContextOptions_RegistersDbContext()
    {
        var services = CreateServicesWithDefaults(
            dbContextOptions: opts => opts.UseInMemoryDatabase("test-di"));

        Assert.Contains(services, d =>
            d.ServiceType == typeof(CashierDbContext<string>));
    }

    [Fact]
    public void AddCashierMollie_WithoutDbContextOptions_DoesNotRegisterDbContext()
    {
        // No dbContextOptions => no DbContext registration
        var services = CreateServicesWithDefaults(dbContextOptions: null);

        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(CashierDbContext<string>));
    }

    // ----------------------------------------------------------------
    // Fluent API
    // ----------------------------------------------------------------

    [Fact]
    public void AddCashierMollie_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        var result = services.AddCashierMollie<string>(config);

        Assert.Same(services, result);
    }

    // ----------------------------------------------------------------
    // Test helper: custom event dispatcher for TryAddScoped verification
    // ----------------------------------------------------------------

    private sealed class CustomTestEventDispatcher : ICashierEventDispatcher
    {
        public Task DispatchAsync<T>(T domainEvent, CancellationToken ct = default) where T : notnull
            => Task.CompletedTask;
    }
}
