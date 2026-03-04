using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mollie.Api.Models.Payment.Response;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

public class ManagedEngineIntegrationTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierService<string> _cashier;
    private readonly ManagedBillingEngine<string> _engine;
    private readonly IOptions<CashierMollieOptions> _options;

    public ManagedEngineIntegrationTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        _options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        });
        _engine = new ManagedBillingEngine<string>(_db, _mollieClient, _eventDispatcher, _options, NullLogger<ManagedBillingEngine<string>>.Instance);
        _cashier = new CashierService<string>(_db, _engine, _mollieClient, _eventDispatcher, _options, NullLogger<CashierService<string>>.Instance);
    }

    [Fact]
    public async Task FullLifecycle_ManagedEngine_CreateCancelResume()
    {
        var owner = new TestBillable("managed-user-1", "cst_managed1", "mdt_managed1");

        // 1. Create subscription (owner has mandate, so direct activation)
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.False(result.RequiresAction);
        Assert.Null(result.CheckoutUrl);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        Assert.NotNull(result.Subscription.CycleStartedAt);

        // Verify an OrderItem was scheduled for billing
        var orderItems = await _db.OrderItems
            .Where(i => i.OwnerId == "managed-user-1")
            .ToListAsync();
        Assert.Single(orderItems);
        Assert.NotNull(orderItems[0].ProcessAt);
        Assert.Null(orderItems[0].ProcessedAt);

        // 2. Verify subscription status
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default"));
        Assert.False(await _cashier.OnGracePeriodAsync(owner, "default"));

        // 3. Cancel (enters grace period)
        await _cashier.CancelAsync(owner, "default");
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default")); // still subscribed during grace
        Assert.True(await _cashier.OnGracePeriodAsync(owner, "default"));

        // Verify SubscriptionCancelled event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCancelled<string>>(), Arg.Any<CancellationToken>());

        // 4. Resume (exits grace period)
        await _cashier.ResumeAsync(owner, "default");
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default"));
        Assert.False(await _cashier.OnGracePeriodAsync(owner, "default"));

        // Verify SubscriptionResumed event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionResumed<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueItems_CreatesRecurringPayment()
    {
        var owner = new TestBillable("managed-user-2", "cst_managed2", "mdt_managed2");

        // 1. Create subscription (direct activation)
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);

        // 2. Create a "paid" payment record so the engine can look up a mandate
        var mandatePayment = new Payment<string>
        {
            OwnerId = "managed-user-2",
            MolliePaymentId = "tr_mandate_source",
            Status = "paid",
            Amount = 0.01m,
            Currency = "EUR",
            MollieMandateId = "mdt_managed2",
        };
        _db.Payments.Add(mandatePayment);
        await _db.SaveChangesAsync();

        // 3. Make the initial OrderItem due NOW by backdating ProcessAt
        var dueItem = await _db.OrderItems
            .FirstAsync(i => i.OwnerId == "managed-user-2" && i.ProcessedAt == null);
        dueItem.ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        dueItem.UnitPrice = 9.99m;
        await _db.SaveChangesAsync();

        // 4. Mock the recurring payment creation
        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_recurring_1";
        mockPayment.Status = "pending";

        _mollieClient.CreateRecurringPaymentAsync(
            "cst_managed2", "mdt_managed2", Arg.Any<decimal>(),
            "EUR", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        // 5. Process due items
        await _engine.ProcessDueItemsAsync();

        // 6. Verify the item was processed
        var processedItem = await _db.OrderItems.FindAsync(dueItem.Id);
        Assert.NotNull(processedItem!.ProcessedAt);
        Assert.Equal("tr_recurring_1", processedItem.MolliePaymentId);
        Assert.Equal("pending", processedItem.MolliePaymentStatus);

        // 7. Verify a NEXT OrderItem was scheduled
        var allItems = await _db.OrderItems
            .Where(i => i.OwnerId == "managed-user-2")
            .ToListAsync();
        Assert.Equal(2, allItems.Count);

        var nextItem = allItems.First(i => i.ProcessedAt == null);
        Assert.NotNull(nextItem.ProcessAt);
        Assert.True(nextItem.ProcessAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SwapPlan_SetsNextPlan_AppliedOnProcessing()
    {
        var owner = new TestBillable("managed-user-3", "cst_managed3", "mdt_managed3");

        // 1. Create subscription
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.Equal("pro", result.Subscription.Plan);

        // 2. Swap plan — ManagedEngine sets NextPlan (deferred swap)
        var swapped = await _cashier.SwapAsync(owner, "default", "team");
        Assert.Equal("pro", swapped.Plan);     // Plan stays until next billing cycle
        Assert.Equal("team", swapped.NextPlan); // NextPlan is set

        // Verify SubscriptionPlanSwapped event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionPlanSwapped<string>>(), Arg.Any<CancellationToken>());

        // 3. Create a mandate payment for recurring billing lookup
        var mandatePayment = new Payment<string>
        {
            OwnerId = "managed-user-3",
            MolliePaymentId = "tr_mandate_swap",
            Status = "paid",
            Amount = 0.01m,
            Currency = "EUR",
            MollieMandateId = "mdt_managed3",
        };
        _db.Payments.Add(mandatePayment);
        await _db.SaveChangesAsync();

        // 4. Backdate the OrderItem to make it due
        var dueItem = await _db.OrderItems
            .FirstAsync(i => i.OwnerId == "managed-user-3" && i.ProcessedAt == null);
        dueItem.ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        dueItem.UnitPrice = 9.99m;
        await _db.SaveChangesAsync();

        // 5. Mock recurring payment
        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_recurring_swap";
        mockPayment.Status = "pending";

        _mollieClient.CreateRecurringPaymentAsync(
            "cst_managed3", "mdt_managed3", Arg.Any<decimal>(),
            "EUR", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        // 6. Process due items — should apply the pending plan swap
        await _engine.ProcessDueItemsAsync();

        // 7. Verify Plan was swapped and NextPlan cleared
        var sub = await _db.Subscriptions.FindAsync(result.Subscription.Id);
        Assert.Equal("team", sub!.Plan);
        Assert.Null(sub.NextPlan);
    }

    public void Dispose() => _db.Dispose();
}
