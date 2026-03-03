using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class MollieBillingEngineTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly MollieBillingEngine<string> _engine;

    public MollieBillingEngineTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        });
        _engine = new MollieBillingEngine<string>(_db, _mollieClient, _dispatcher, options);
    }

    [Fact]
    public void RequiresBackgroundProcessing_ReturnsFalse()
    {
        Assert.False(_engine.RequiresBackgroundProcessing);
    }

    [Fact]
    public async Task ProcessDueItemsAsync_IsNoOp()
    {
        // Should complete without error — no-op for Mollie native engine
        await _engine.ProcessDueItemsAsync();
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithMandate_CreatesActiveSubscription()
    {
        var owner = new TestBillable("u1", "cst_test", "mdt_test");
        var options = new SubscriptionOptions();

        var result = await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        Assert.False(result.RequiresAction);
        Assert.Null(result.CheckoutUrl);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        Assert.NotNull(result.Subscription.CycleStartedAt);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithMandate_DispatchesCreatedEvent()
    {
        var owner = new TestBillable("u1", "cst_test", "mdt_test");
        var options = new SubscriptionOptions();

        await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCreated<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithoutMandate_RequiresCheckout()
    {
        var owner = new TestBillable("u2", "cst_new", null);
        var options = new SubscriptionOptions();

        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_first";
        mockPayment.Status = "open";
        var mockLinks = Substitute.For<PaymentResponseLinks>();
        var mockCheckout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        mockCheckout.Href = "https://checkout.mollie.com/xxx";
        mockLinks.Checkout = mockCheckout;
        mockPayment.Links = mockLinks;

        _mollieClient.CreateFirstPaymentAsync(
            "cst_new", Arg.Any<decimal>(), "EUR",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        var result = await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        Assert.True(result.RequiresAction);
        Assert.Equal("https://checkout.mollie.com/xxx", result.CheckoutUrl);
        Assert.Equal(SubscriptionStatus.Pending, result.Subscription.Status);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithoutCustomerId_CreatesCustomer()
    {
        var owner = new TestBillable("u3", null, null);
        var options = new SubscriptionOptions();

        var mockCustomer = Substitute.For<CustomerResponse>();
        mockCustomer.Id = "cst_created";
        _mollieClient.CreateCustomerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockCustomer);

        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_new";
        mockPayment.Status = "open";
        var mockLinks = Substitute.For<PaymentResponseLinks>();
        var mockCheckout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        mockCheckout.Href = "https://checkout.mollie.com/new";
        mockLinks.Checkout = mockCheckout;
        mockPayment.Links = mockLinks;

        _mollieClient.CreateFirstPaymentAsync(
            "cst_created", Arg.Any<decimal>(), "EUR",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        Assert.Equal("cst_created", owner.MollieCustomerId);
        await _mollieClient.Received(1).CreateCustomerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithTrialDays_SetsTrialEndsAt()
    {
        var owner = new TestBillable("u4", "cst_trial", "mdt_trial");
        var options = new SubscriptionOptions { TrialDays = 14 };

        var result = await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        Assert.NotNull(result.Subscription.TrialEndsAt);
        var daysUntilTrialEnd = (result.Subscription.TrialEndsAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(daysUntilTrialEnd, 13, 15);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithQuantity_SetsQuantity()
    {
        var owner = new TestBillable("u5", "cst_qty", "mdt_qty");
        var options = new SubscriptionOptions { Quantity = 5 };

        var result = await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        Assert.Equal(5, result.Subscription.Quantity);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_WithGracePeriod_SetsEndsAtInFuture()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-15),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.CancelSubscriptionAsync(sub, immediately: false);

        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(sub.EndsAt);
        Assert.True(sub.EndsAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Immediately_SetsEndsAtToNow()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.CancelSubscriptionAsync(sub, immediately: true);

        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(sub.EndsAt);
        Assert.True(sub.EndsAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_DispatchesEvent()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.CancelSubscriptionAsync(sub, immediately: false);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCancelled<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelSubscriptionAsync_CancelsOnMollie_WhenSubscriptionIdExists()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test", MollieSubscriptionId = "sub_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.CancelSubscriptionAsync(sub, immediately: true);

        await _mollieClient.Received(1).CancelSubscriptionAsync(
            "cst_test", "sub_test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_ClearsEndsAtAndSetsActive()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.ResumeSubscriptionAsync(sub);

        Assert.Null(sub.EndsAt);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_DispatchesEvent()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.ResumeSubscriptionAsync(sub);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionResumed<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwapPlanAsync_UpdatesPlan()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test", MollieSubscriptionId = "sub_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await _engine.SwapPlanAsync(sub, "team");

        Assert.Equal("team", result.Plan);
    }

    [Fact]
    public async Task SwapPlanAsync_DispatchesEvent()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.SwapPlanAsync(sub, "team");

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionPlanSwapped<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwapPlanAsync_UpdatesMollie_WhenSubscriptionIdExists()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test", MollieSubscriptionId = "sub_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.SwapPlanAsync(sub, "team");

        await _mollieClient.Received(1).UpdateSubscriptionAsync(
            "cst_test", "sub_test", ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateQuantityAsync_UpdatesSubscription()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test", MollieSubscriptionId = "sub_test",
            Quantity = 1,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await _engine.UpdateQuantityAsync(sub, 5);

        Assert.Equal(5, result.Quantity);
    }

    [Fact]
    public async Task UpdateQuantityAsync_DispatchesEvent()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Quantity = 1,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _engine.UpdateQuantityAsync(sub, 3);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionQuantityUpdated<string>>(), Arg.Any<CancellationToken>());
    }

    public void Dispose() => _db.Dispose();
}
