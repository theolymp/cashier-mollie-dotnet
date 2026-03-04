using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class ManagedBillingEngineTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly ManagedBillingEngine<string> _engine;

    public ManagedBillingEngineTests()
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
        _engine = new ManagedBillingEngine<string>(_db, _mollieClient, _dispatcher, options, NullLogger<ManagedBillingEngine<string>>.Instance);
    }

    [Fact]
    public void RequiresBackgroundProcessing_ReturnsTrue()
    {
        Assert.True(_engine.RequiresBackgroundProcessing);
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
    public async Task CreateSubscriptionAsync_WithMandate_SchedulesFirstOrderItem()
    {
        var owner = new TestBillable("u1", "cst_test", "mdt_test");
        var options = new SubscriptionOptions();

        var result = await _engine.CreateSubscriptionAsync(owner, "default", "pro", options);

        var orderItems = _db.OrderItems.Where(i => i.SubscriptionId == result.Subscription.Id).ToList();
        Assert.Single(orderItems);
        Assert.NotNull(orderItems[0].ProcessAt);
        // Should be approximately 30 days from now
        var daysUntilProcess = (orderItems[0].ProcessAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(daysUntilProcess, 29, 31);
        Assert.Null(orderItems[0].ProcessedAt);
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
    public async Task CancelSubscriptionAsync_DoesNotCallMollie()
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

        await _mollieClient.DidNotReceive().CancelSubscriptionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task SwapPlanAsync_SetsNextPlan()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await _engine.SwapPlanAsync(sub, "team");

        // Managed engine sets NextPlan for next-cycle swap by default
        Assert.Equal("team", result.NextPlan);
        Assert.Equal("pro", result.Plan); // Plan stays unchanged until next cycle
    }

    [Fact]
    public async Task ProcessDueItemsAsync_ProcessesDueItems()
    {
        // Create a subscription and a due order item
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        // Seed a payment with mandate so the engine can find it
        var payment = new Payment<string>
        {
            OwnerId = "u1",
            SubscriptionId = sub.Id,
            MolliePaymentId = "tr_initial",
            MollieMandateId = "mdt_test",
            Status = "paid",
            Amount = 9.99m,
            Currency = "EUR",
        };
        _db.Payments.Add(payment);

        var orderItem = new OrderItem<string>
        {
            SubscriptionId = sub.Id,
            OwnerId = "u1",
            Description = "Pro plan",
            Currency = "EUR",
            UnitPrice = 9.99m,
            Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1), // Due in the past
        };
        _db.OrderItems.Add(orderItem);
        await _db.SaveChangesAsync();

        // Mock the recurring payment response
        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_recurring";
        mockPayment.Status = "pending";
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        await _engine.ProcessDueItemsAsync();

        // Verify the item was processed
        await _db.Entry(orderItem).ReloadAsync();
        Assert.NotNull(orderItem.ProcessedAt);
        Assert.Equal("tr_recurring", orderItem.MolliePaymentId);
        Assert.Equal("pending", orderItem.MolliePaymentStatus);

        // Verify Mollie was called with the mandate from the seeded payment
        await _mollieClient.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueItemsAsync_SchedulesNextOrderItem()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        // Seed a payment with mandate
        var payment = new Payment<string>
        {
            OwnerId = "u1",
            SubscriptionId = sub.Id,
            MolliePaymentId = "tr_initial",
            MollieMandateId = "mdt_test",
            Status = "paid",
            Amount = 9.99m,
            Currency = "EUR",
        };
        _db.Payments.Add(payment);

        var orderItem = new OrderItem<string>
        {
            SubscriptionId = sub.Id,
            OwnerId = "u1",
            Description = "Pro plan",
            Currency = "EUR",
            UnitPrice = 9.99m,
            Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        _db.OrderItems.Add(orderItem);
        await _db.SaveChangesAsync();

        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_recurring";
        mockPayment.Status = "pending";
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        await _engine.ProcessDueItemsAsync();

        // A new OrderItem should be scheduled for the next cycle
        var allItems = _db.OrderItems
            .Where(i => i.SubscriptionId == sub.Id)
            .OrderBy(i => i.Id)
            .ToList();
        Assert.Equal(2, allItems.Count);

        var nextItem = allItems[1];
        Assert.Null(nextItem.ProcessedAt);
        Assert.NotNull(nextItem.ProcessAt);
        var daysUntilNext = (nextItem.ProcessAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(daysUntilNext, 29, 31);
    }

    [Fact]
    public async Task ProcessDueItemsAsync_AppliesPendingPlanSwap()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            NextPlan = "team",
            MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var payment = new Payment<string>
        {
            OwnerId = "u1",
            SubscriptionId = sub.Id,
            MolliePaymentId = "tr_initial",
            MollieMandateId = "mdt_test",
            Status = "paid",
            Amount = 9.99m,
            Currency = "EUR",
        };
        _db.Payments.Add(payment);

        var orderItem = new OrderItem<string>
        {
            SubscriptionId = sub.Id,
            OwnerId = "u1",
            Description = "Pro plan",
            Currency = "EUR",
            UnitPrice = 9.99m,
            Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        _db.OrderItems.Add(orderItem);
        await _db.SaveChangesAsync();

        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_recurring";
        mockPayment.Status = "pending";
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        await _engine.ProcessDueItemsAsync();

        // Plan should now be "team" and NextPlan should be cleared
        await _db.Entry(sub).ReloadAsync();
        Assert.Equal("team", sub.Plan);
        Assert.Null(sub.NextPlan);
    }

    [Fact]
    public async Task ProcessDueItemsAsync_SkipsFutureItems()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var futureItem = new OrderItem<string>
        {
            SubscriptionId = sub.Id,
            OwnerId = "u1",
            Description = "Pro plan",
            Currency = "EUR",
            UnitPrice = 9.99m,
            Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddDays(15), // Future -- not yet due
        };
        _db.OrderItems.Add(futureItem);
        await _db.SaveChangesAsync();

        await _engine.ProcessDueItemsAsync();

        // Item should NOT be processed
        await _db.Entry(futureItem).ReloadAsync();
        Assert.Null(futureItem.ProcessedAt);
        Assert.Null(futureItem.MolliePaymentId);

        // Mollie should NOT be called
        await _mollieClient.DidNotReceive().CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateQuantityAsync_UpdatesSubscription()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            Quantity = 1,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await _engine.UpdateQuantityAsync(sub, 5);

        Assert.Equal(5, result.Quantity);
    }

    public void Dispose() => _db.Dispose();
}
