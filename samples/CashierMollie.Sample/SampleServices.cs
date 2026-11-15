using CashierMollie.Events;
using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using Microsoft.EntityFrameworkCore;
using Mollie.Api;

namespace CashierMollie.Sample;

/// <summary>The registration a real application needs, shared by the web app and the test-mode run.</summary>
public static class SampleServices
{
    public static IServiceCollection AddSampleBilling(this IServiceCollection services, IConfiguration config,
        string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContext<SampleDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "sample.db")}"));

        // Mollie.Api's own registration: CashierMollie never reads the key itself.
        services.AddMollieApi(o => o.ApiKey = config["CashierMollie:ApiKey"]
            ?? throw new InvalidOperationException("CashierMollie:ApiKey is not set."));

        // Registered before AddCashierMollie, which only adds its no-op dispatcher if none exists.
        services.AddScoped<ICashierEventDispatcher, LoggingEventDispatcher>();

        services.AddCashierMollie<int>(config, o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "billing.db")}"));
        return services;
    }

    /// <summary>Creates both databases. A real application uses EF migrations instead.</summary>
    public static async Task EnsureDatabasesAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<CashierMollie.Data.CashierDbContext<int>>()
            .Database.EnsureCreatedAsync();
    }
}

/// <summary>
/// Where an application reacts to billing. The three that matter most on MollieNative: a failed
/// cycle (lock features, tell the customer), the recovery, and an upgrade (invoice it).
/// </summary>
public sealed partial class LoggingEventDispatcher(ILogger<LoggingEventDispatcher> logger) : ICashierEventDispatcher
{
    public Task DispatchAsync<T>(T domainEvent, CancellationToken ct = default) where T : notnull
    {
        switch (domainEvent)
        {
            case SubscriptionPastDue<int> e:
                LogPastDue(e.Subscription.Name, e.OwnerId, e.FailedPayment.Amount);
                break;
            case SubscriptionPaymentRecovered<int> e:
                LogRecovered(e.Subscription.Name, e.OwnerId);
                break;
            default:
                LogEvent(typeof(T).Name);
                break;
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription {Name} of customer {OwnerId} is past due: {Amount} failed")]
    private partial void LogPastDue(string name, int ownerId, decimal amount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {Name} of customer {OwnerId} is paid up again")]
    private partial void LogRecovered(string name, int ownerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Billing event {Event}")]
    private partial void LogEvent(string @event);
}
