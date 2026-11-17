using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Testing;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CashierMollie.Tests.Testing;

/// <summary>
/// The library end to end through DI, with <see cref="FakeMollieClient"/> in place of Mollie:
/// what a consumer's own tests look like, and proof that the fake behaves as the flows need.
/// Every webhook call goes through a fresh scope, as a request would.
/// </summary>
public sealed class FakeMollieFlowTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly FakeMollieClient _mollie;
    private readonly List<object> _events = [];
    private readonly TestBillable _owner = new("u1");

    public FakeMollieFlowTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CashierMollie:WebhookUrl"] = "https://shop.example/webhooks/mollie",
            ["CashierMollie:DefaultTaxRatePercent"] = "19",
            ["CashierMollie:Plans:starter:Amount"] = "4.99",
            ["CashierMollie:Plans:starter:Description"] = "Starter",
            ["CashierMollie:Plans:pro:Amount"] = "9.99",
            ["CashierMollie:Plans:pro:Description"] = "Pro",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ICashierEventDispatcher>(new Recorder(_events));
        string db = Guid.NewGuid().ToString();
        services.AddCashierMollie<string>(config, o => o.UseInMemoryDatabase(db));
        _mollie = services.AddFakeMollie();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

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

    private Task<bool> Webhook(string paymentId) => With(async sp =>
    {
        await sp.GetRequiredService<IWebhookService>().HandlePaymentAsync(paymentId);
        return true;
    });

    private Task<Subscription<string>> Sub(string name) => With(async sp =>
        (await sp.GetRequiredService<ICashierService<string>>().GetSubscriptionAsync(_owner, name))!);

    private Task<List<Payment<string>>> Payments() => With(sp =>
        sp.GetRequiredService<CashierDbContext<string>>().Payments.OrderBy(p => p.Id).ToListAsync());

    /// <summary>A customer with a mandate and a paid first cycle of <paramref name="plan"/>.</summary>
    private async Task<Subscription<string>> PaidSubscription(string plan)
    {
        await With(sp => sp.GetRequiredService<ICashierService<string>>().GetOrCreateMollieCustomerAsync(_owner));
        _owner.MollieMandateId = _mollie.AddMandate(_owner.MollieCustomerId!);
        await With(sp => sp.GetRequiredService<ICashierService<string>>().NewSubscription(_owner, "main", plan).CreateAsync());
        var first = _mollie.Payments[^1];
        _mollie.SetPaymentStatus(first.Id, "paid");
        await Webhook(first.Id);
        return await Sub("main");
    }

    [Fact]
    public async Task Checkout_Paid_ActivatesAndRegistersAtMollie()
    {
        var result = await With(sp => sp.GetRequiredService<ICashierService<string>>()
            .NewSubscription(_owner, "main", "pro").CreateAsync());
        Assert.Equal(SubscriptionStatus.Pending, result.Subscription.Status);
        var checkout = _mollie.Payments.Single();
        Assert.Equal(result.CheckoutUrl, FakeMollieClient.CheckoutUrl(checkout));
        Assert.Equal("11.89", checkout.Amount.Value);

        _mollie.SetPaymentStatus(checkout.Id, "paid");
        await Webhook(checkout.Id);

        var sub = await Sub("main");
        Assert.True(sub.IsActive());
        var atMollie = Assert.Single(_mollie.Subscriptions);
        Assert.Equal(sub.MollieSubscriptionId, atMollie.Id);
        Assert.Equal("11.89", atMollie.Amount.Value);
        Assert.Equal("30 days", atMollie.Interval);
        Assert.Equal(checkout.MandateId, atMollie.MandateId);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(30), atMollie.StartDate!.Value.Date);
    }

    [Fact]
    public async Task Downgrade_TheNextCycleIsChargedAndBookedAtTheNewAmount()
    {
        await PaidSubscription("pro");
        await With(sp => sp.GetRequiredService<ICashierService<string>>().SwapAsync(_owner, "main", "starter"));
        Assert.Equal("5.94", _mollie.Subscriptions.Single().Amount.Value);
        Assert.Equal("pro", (await Sub("main")).Plan);

        var cycle = _mollie.ChargeSubscription(_mollie.Subscriptions.Single().Id);
        await Webhook(cycle.Id);

        var sub = await Sub("main");
        Assert.Equal("starter", sub.Plan);
        Assert.Null(sub.NextPlan);
        var booked = (await Payments()).Single(p => p.MolliePaymentId == cycle.Id);
        Assert.Equal(5.94m, booked.Amount);
        Assert.Equal(4.99m, booked.AmountNet);
    }

    [Fact]
    public async Task Upgrade_CreditsUnusedDays_ChargesTheRest_AndSwitchesTheMollieSubscription()
    {
        var starter = await PaidSubscription("starter");

        var result = await With(sp => sp.GetRequiredService<ICashierService<string>>()
            .UpgradeAsync(_owner, "pro", "pro", [starter]));

        decimal credit = Money.Prorate(4.99m, 29, 30);
        Assert.Equal(credit, result.CreditAppliedNet);
        Assert.Equal(Money.Gross(9.99m - credit, 19m), result.ChargedGross);
        Assert.Empty(result.Incomplete);
        Assert.Equal("canceled", _mollie.Subscriptions.Single(s => s.Id == starter.MollieSubscriptionId).Status);
        Assert.Equal("11.89", _mollie.Subscriptions.Single(s => s.Id == result.Subscription.MollieSubscriptionId).Amount.Value);
        var charge = _mollie.Payments[^1];
        Assert.Equal(result.ChargedGross.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), charge.Amount.Value);
        Assert.Contains(_events, e => e is SubscriptionUpgraded<string>);
    }

    [Fact]
    public async Task PaymentDefault_LocksAndThePaidCheckoutUnlocksOnANewMandate()
    {
        await PaidSubscription("pro");
        var atMollie = _mollie.Subscriptions.Single();
        var failed = _mollie.ChargeSubscription(atMollie.Id, "failed");
        await Webhook(failed.Id);

        Assert.Equal(SubscriptionStatus.PastDue, (await Sub("main")).Status);
        Assert.Contains(_events, e => e is SubscriptionPastDue<string>);

        var pay = await With(sp => sp.GetRequiredService<ICashierService<string>>().PayOutstandingAsync(_owner, "main"));
        Assert.Equal(11.89m, pay.Amount);
        var settlement = _mollie.Payments.Single(p => FakeMollieClient.CheckoutUrl(p) == pay.CheckoutUrl);
        _mollie.SetPaymentStatus(settlement.Id, "paid");
        await Webhook(settlement.Id);

        Assert.Equal(SubscriptionStatus.Active, (await Sub("main")).Status);
        Assert.Contains(_events, e => e is SubscriptionPaymentRecovered<string>);
        Assert.Equal(settlement.MandateId, atMollie.MandateId);
        Assert.NotEqual(failed.MandateId, atMollie.MandateId);
    }

    [Fact]
    public async Task ALaterPaidCycle_DoesNotUnlockWhileAnEarlierOneIsOutstanding()
    {
        await PaidSubscription("pro");
        var atMollie = _mollie.Subscriptions.Single();
        await Webhook(_mollie.ChargeSubscription(atMollie.Id, "failed").Id);
        await Webhook(_mollie.ChargeSubscription(atMollie.Id, "paid").Id);

        Assert.Equal(SubscriptionStatus.PastDue, (await Sub("main")).Status);
    }

    [Fact]
    public async Task EarlyEnd_RefundsTheUnusedDaysAtMollie()
    {
        await PaidSubscription("pro");

        var result = await With(sp => sp.GetRequiredService<ICashierService<string>>()
            .CancelImmediatelyWithRefundAsync(_owner, "main"));

        Assert.Equal(29, result.RemainingDays);
        Assert.Equal(0m, result.UnrefundedNet);
        var refund = Assert.Single(_mollie.Refunds);
        Assert.Equal(result.RefundedGross.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), refund.Amount.Value);
        Assert.Equal("canceled", _mollie.Subscriptions.Single().Status);
    }

    [Fact]
    public async Task TheFakeRefusesWhatMollieRefuses()
    {
        var customer = await _mollie.CreateCustomerAsync("x");
        await Assert.ThrowsAsync<FakeMollieException>(() => _mollie.CreateRecurringPaymentAsync(
            customer.Id, "mdt_none", 1m, "EUR", "x", "https://shop.example/hook"));
        await Assert.ThrowsAsync<FakeMollieException>(() => _mollie.CreateFirstPaymentAsync(
            customer.Id, 1m, "EUR", "x", "https://shop.example/back", "/relative/hook"));

        string mandate = _mollie.AddMandate(customer.Id);
        var paid = await _mollie.CreateRecurringPaymentAsync(customer.Id, mandate, 5m, "EUR", "x", "https://shop.example/hook");
        await Assert.ThrowsAsync<FakeMollieException>(() => _mollie.CreateRefundAsync(paid.Id, 1m, "EUR"));
        _mollie.SetPaymentStatus(paid.Id, "paid");
        await _mollie.CreateRefundAsync(paid.Id, 4m, "EUR");
        await Assert.ThrowsAsync<FakeMollieException>(() => _mollie.CreateRefundAsync(paid.Id, 1.01m, "EUR"));

        await _mollie.CreateSubscriptionAsync(customer.Id, 5m, "EUR", "30 days", "Same", "https://shop.example/hook");
        await Assert.ThrowsAsync<FakeMollieException>(() =>
            _mollie.CreateSubscriptionAsync(customer.Id, 5m, "EUR", "30 days", "Same", "https://shop.example/hook"));
        await Assert.ThrowsAsync<FakeMollieException>(() =>
            _mollie.CreateSubscriptionAsync(customer.Id, 5m, "EUR", "monthly", "Other", "https://shop.example/hook"));
    }
}


public class FakeMollieStatusTests
{
    [Fact]
    // Callers branch on Mollie's status (404 gone, 422 refused), not on message text.
    public async Task UnknownIs404_RefusedIs422_AsMollieAnswers()
    {
        var mollie = new FakeMollieClient();
        var missing = await Assert.ThrowsAsync<FakeMollieException>(() => mollie.GetPaymentAsync("tr_none"));
        Assert.Equal(404, missing.Status);

        var customer = await mollie.CreateCustomerAsync("x");
        var gone = await Assert.ThrowsAsync<FakeMollieException>(() => mollie.CancelSubscriptionAsync(customer.Id, "sub_none"));
        Assert.Equal(404, gone.Status);

        await mollie.CreateSubscriptionAsync(customer.Id, 5m, "EUR", "30 days", "x", "https://shop.example/hook",
            mollie.AddMandate(customer.Id));
        string id = mollie.Subscriptions.Single().Id;
        await mollie.CancelSubscriptionAsync(customer.Id, id);
        var twice = await Assert.ThrowsAsync<FakeMollieException>(() => mollie.CancelSubscriptionAsync(customer.Id, id));
        Assert.Equal(422, twice.Status);
    }
}
