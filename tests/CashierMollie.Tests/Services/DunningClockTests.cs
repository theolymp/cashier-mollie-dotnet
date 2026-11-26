using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CashierMollie.Tests.Services;

/// <summary>
/// A TimeProvider registered by the consumer drives the dunning clock: when past_due starts, the
/// reminder and cancel days, and HasAccess. The clock here is set in 2000, so a service still
/// reading the system clock sees 26 years of debt and cancels instead of reminding.
/// </summary>
public sealed class DunningClockTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2000, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly List<object> _events = [];
    private readonly ServiceProvider _services;
    private readonly FakeMollieClient _mollie;

    public DunningClockTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CashierMollie:BillingEngine"] = "Managed",
            ["CashierMollie:WebhookUrl"] = "https://shop.example/cashier/webhook",
            ["CashierMollie:Dunning:Enabled"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton<ICashierEventDispatcher>(new Recorder(_events));
        string db = Guid.NewGuid().ToString();
        services.AddCashierMollie<string>(config, o => o.UseInMemoryDatabase(db));
        _mollie = services.AddFakeMollie();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Recorder(List<object> events) : ICashierEventDispatcher
    {
        public Task DispatchAsync<T>(T domainEvent, CancellationToken ct = default) where T : notnull
        {
            lock (events)
                events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<long> SeedDueAsync(string mandateId) => With(async sp =>
    {
        var db = sp.GetRequiredService<CashierDbContext<string>>();
        var customer = await _mollie.CreateCustomerAsync("Customer");
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", MollieCustomerId = customer.Id,
            Status = SubscriptionStatus.Active, CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = mandateId == "valid" ? _mollie.AddMandate(customer.Id) : mandateId,
            Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 29m, Quantity = 1, ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        return sub.Id;
    });

    private Task<int> ChargeDueAsync() => With(async sp =>
    {
        await ((ManagedBillingEngine<string>)sp.GetRequiredService<IBillingEngine<string>>()).ProcessDueItemsAsync();
        return 0;
    });

    private Task<Subscription<string>> LoadAsync(long id) => With(sp =>
        sp.GetRequiredService<CashierDbContext<string>>().Subscriptions.SingleAsync(s => s.Id == id));

    [Fact]
    public async Task ARefusedChargeIsPastDueFromTheClock()
    {
        var id = await SeedDueAsync("mdt_unknown"); // the fake refuses it with a 422

        await ChargeDueAsync();

        var sub = await LoadAsync(id);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.Equal(Now, sub.PastDueSince);
    }

    [Fact]
    public async Task AFailureReportedByWebhookIsPastDueFromTheClock()
    {
        var id = await SeedDueAsync("valid");
        await ChargeDueAsync();
        var charge = _mollie.Payments.Single(p => p.SequenceType == "recurring");
        _mollie.SetPaymentStatus(charge.Id, "failed");

        await With(async sp =>
        {
            await sp.GetRequiredService<IWebhookService>().HandlePaymentAsync(charge.Id);
            return 0;
        });

        var sub = await LoadAsync(id);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.Equal(Now, sub.PastDueSince);
    }

    [Fact]
    public async Task TheScheduleAndAccessCountDaysOnTheClock()
    {
        var id = await SeedDueAsync("mdt_unknown");
        await ChargeDueAsync();
        await With(async sp =>
        {
            var db = sp.GetRequiredService<CashierDbContext<string>>();
            var sub = await db.Subscriptions.SingleAsync(s => s.Id == id);
            sub.PastDueSince = Now.AddDays(-3);
            return await db.SaveChangesAsync();
        });

        var acted = await With(sp => sp.GetRequiredService<DunningService<string>>().ProcessAsync());
        var access = await With(async sp =>
            sp.GetRequiredService<ICashierService<string>>().HasAccess(await LoadAsync(id)));

        Assert.Equal(1, acted);
        Assert.Single(_events.OfType<DunningReminderDue<string>>());
        Assert.Empty(_events.OfType<SubscriptionCancelledForNonPayment<string>>());
        Assert.True(access);
    }
}
