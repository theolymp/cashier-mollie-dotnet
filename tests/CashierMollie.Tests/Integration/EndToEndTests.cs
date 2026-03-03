using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Refund.Response;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

/// <summary>
/// Comprehensive end-to-end integration tests that exercise multiple services together.
/// Each test scenario crosses service boundaries to verify real-world workflows.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly IOptions<CashierMollieOptions> _options;

    public EndToEndTests()
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
            GracePeriodDays = 30,
        });
    }

    /// <summary>
    /// Full subscription lifecycle using MollieBillingEngine:
    /// Create (active) -> Swap plan -> Cancel (grace) -> Resume -> Cancel immediately (ended).
    /// </summary>
    [Fact]
    public async Task FullSubscriptionLifecycle_CreateActivateSwapCancelResumeEnd()
    {
        // Arrange: owner with existing mandate (direct activation, no checkout)
        var owner = new TestBillable("e2e-user-1", "cst_e2e_1", "mdt_e2e_1");
        var engine = new MollieBillingEngine<string>(_db, _mollieClient, _eventDispatcher, _options);
        var cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, _options);

        // Step 1: Create subscription -> should be Active immediately
        var result = await cashier.NewSubscription(owner, "default", "pro-monthly")
            .CreateAsync();

        Assert.False(result.RequiresAction);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        Assert.NotNull(result.Subscription.CycleStartedAt);
        Assert.True(await cashier.IsSubscribedAsync(owner, "default"));

        // Step 2: Swap plan -> verify new plan
        var swapped = await cashier.SwapAsync(owner, "default", "team-monthly");
        Assert.Equal("team-monthly", swapped.Plan);

        // Verify SubscriptionPlanSwapped event was dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionPlanSwapped<string>>(), Arg.Any<CancellationToken>());

        // Step 3: Cancel (not immediately) -> verify grace period
        await cashier.CancelAsync(owner, "default");

        Assert.True(await cashier.OnGracePeriodAsync(owner, "default"));
        Assert.True(await cashier.IsSubscribedAsync(owner, "default")); // still accessible during grace

        var subAfterCancel = await cashier.GetSubscriptionAsync(owner, "default");
        Assert.NotNull(subAfterCancel);
        Assert.Equal(SubscriptionStatus.Cancelled, subAfterCancel!.Status);
        Assert.NotNull(subAfterCancel.EndsAt);
        Assert.True(subAfterCancel.EndsAt > DateTimeOffset.UtcNow); // grace period in future

        // Step 4: Resume -> verify Active again
        await cashier.ResumeAsync(owner, "default");

        Assert.True(await cashier.IsSubscribedAsync(owner, "default"));
        Assert.False(await cashier.OnGracePeriodAsync(owner, "default"));

        var subAfterResume = await cashier.GetSubscriptionAsync(owner, "default");
        Assert.Equal(SubscriptionStatus.Active, subAfterResume!.Status);
        Assert.Null(subAfterResume.EndsAt);

        // Step 5: Cancel immediately -> verify ended
        await cashier.CancelImmediatelyAsync(owner, "default");

        var subAfterImmediateCancel = await cashier.GetSubscriptionAsync(owner, "default");
        Assert.Equal(SubscriptionStatus.Cancelled, subAfterImmediateCancel!.Status);
        Assert.NotNull(subAfterImmediateCancel.EndsAt);
        Assert.True(subAfterImmediateCancel.EndsAt <= DateTimeOffset.UtcNow); // ended now
        Assert.False(subAfterImmediateCancel.OnGracePeriod()); // no grace — ended
        Assert.False(await cashier.IsSubscribedAsync(owner, "default")); // no longer subscribed
    }

    /// <summary>
    /// Full managed billing cycle: create subscription with mandate -> OrderItem scheduled ->
    /// backdate ProcessAt -> ProcessDueItemsAsync -> item processed, payment created, next item scheduled.
    /// </summary>
    [Fact]
    public async Task ManagedEngine_FullBillingCycle()
    {
        // Arrange
        var owner = new TestBillable("e2e-managed-1", "cst_mgd_1", "mdt_mgd_1");
        var engine = new ManagedBillingEngine<string>(_db, _mollieClient, _eventDispatcher, _options);
        var cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, _options);

        // Step 1: Create subscription (owner has mandate, direct activation)
        var result = await cashier.NewSubscription(owner, "default", "pro-monthly")
            .CreateAsync();

        Assert.False(result.RequiresAction);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);

        // Step 2: Verify OrderItem was scheduled in the future
        var initialItems = await _db.OrderItems
            .Where(i => i.OwnerId == "e2e-managed-1")
            .ToListAsync();
        Assert.Single(initialItems);
        var scheduledItem = initialItems[0];
        Assert.NotNull(scheduledItem.ProcessAt);
        Assert.True(scheduledItem.ProcessAt > DateTimeOffset.UtcNow);
        Assert.Null(scheduledItem.ProcessedAt);

        // Step 3: Create a "paid" payment so the engine can look up the mandate
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "e2e-managed-1",
            MolliePaymentId = "tr_mandate_e2e",
            Status = "paid",
            Amount = 0.01m,
            Currency = "EUR",
            MollieMandateId = "mdt_mgd_1",
        });
        await _db.SaveChangesAsync();

        // Step 4: Backdate ProcessAt to make the item due
        scheduledItem.ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        scheduledItem.UnitPrice = 9.99m;
        await _db.SaveChangesAsync();

        // Step 5: Mock Mollie recurring payment creation
        var mockRecurringPayment = Substitute.For<PaymentResponse>();
        mockRecurringPayment.Id = "tr_recurring_e2e_1";
        mockRecurringPayment.Status = "pending";

        _mollieClient.CreateRecurringPaymentAsync(
            "cst_mgd_1", "mdt_mgd_1", Arg.Any<decimal>(),
            "EUR", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockRecurringPayment);

        // Step 6: Process due items
        await engine.ProcessDueItemsAsync();

        // Step 7: Verify item was processed
        var processedItem = await _db.OrderItems.FindAsync(scheduledItem.Id);
        Assert.NotNull(processedItem!.ProcessedAt);
        Assert.Equal("tr_recurring_e2e_1", processedItem.MolliePaymentId);
        Assert.Equal("pending", processedItem.MolliePaymentStatus);

        // Step 8: Verify next OrderItem was scheduled
        var allItems = await _db.OrderItems
            .Where(i => i.OwnerId == "e2e-managed-1")
            .ToListAsync();
        Assert.Equal(2, allItems.Count);

        var nextItem = allItems.First(i => i.ProcessedAt == null);
        Assert.NotNull(nextItem.ProcessAt);
        Assert.True(nextItem.ProcessAt > DateTimeOffset.UtcNow);
        Assert.Equal(scheduledItem.UnitPrice, nextItem.UnitPrice);
        Assert.Equal(scheduledItem.Currency, nextItem.Currency);
    }

    /// <summary>
    /// Webhook handler processes a "paid" payment: updates local payment status,
    /// stores mandate ID, and activates a pending subscription.
    /// </summary>
    [Fact]
    public async Task WebhookPaymentPaid_UpdatesPaymentAndStoresMandate()
    {
        // Arrange: Create a pending subscription with an open payment in the DB
        var sub = new Subscription<string>
        {
            OwnerId = "e2e-webhook-1",
            Name = "default",
            Plan = "pro-monthly",
            MollieCustomerId = "cst_wh_1",
            Status = SubscriptionStatus.Pending,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var localPayment = new Payment<string>
        {
            OwnerId = "e2e-webhook-1",
            SubscriptionId = sub.Id,
            MolliePaymentId = "tr_webhook_test_1",
            Status = "open",
            Amount = 0.01m,
            Currency = "EUR",
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        // Mock Mollie API: return a "paid" payment with a mandate
        var molliePaymentResponse = Substitute.For<PaymentResponse>();
        molliePaymentResponse.Id = "tr_webhook_test_1";
        molliePaymentResponse.Status = "paid";
        molliePaymentResponse.MandateId = "mdt_acquired_1";

        _mollieClient.GetPaymentAsync("tr_webhook_test_1", Arg.Any<CancellationToken>())
            .Returns(molliePaymentResponse);

        var webhookService = new WebhookService<string>(_db, _mollieClient, _eventDispatcher);

        // Act
        await webhookService.HandlePaymentAsync("tr_webhook_test_1");

        // Assert: Payment record updated
        var updatedPayment = await _db.Payments
            .FirstAsync(p => p.MolliePaymentId == "tr_webhook_test_1");
        Assert.Equal("paid", updatedPayment.Status);
        Assert.Equal("mdt_acquired_1", updatedPayment.MollieMandateId);
        Assert.NotNull(updatedPayment.PaidAt);

        // Assert: Pending subscription activated
        var updatedSub = await _db.Subscriptions.FindAsync(sub.Id);
        Assert.Equal(SubscriptionStatus.Active, updatedSub!.Status);
        Assert.NotNull(updatedSub.CycleStartedAt);

        // Assert: SubscriptionCreated event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCreated<string>>(), Arg.Any<CancellationToken>());

        // Assert: OrderPaymentPaid event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<OrderPaymentPaid<string>>(), Arg.Any<CancellationToken>());

        // Assert: FirstPaymentPaid event dispatched (because subscription was pending)
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<FirstPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Credit workflow: add credit -> check balance -> apply partial -> check balance ->
    /// try to apply more than available -> verify capped to remaining balance.
    /// </summary>
    [Fact]
    public async Task CreditWorkflow_AddApplyVerifyBalance()
    {
        // Arrange
        var owner = new TestBillable("e2e-credit-1", "cst_credit_1", "mdt_credit_1");
        var creditService = new CreditService<string>(_db, _eventDispatcher, _options);

        // Step 1: Add 50 EUR credit
        await creditService.AddCreditAsync(owner, 50m, "EUR", "Welcome bonus");

        // Step 2: Verify balance is 50
        var balance1 = await creditService.GetBalanceAsync(owner, "EUR");
        Assert.Equal(50m, balance1);

        // Verify CreditAdded event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<CreditAdded<string>>(), Arg.Any<CancellationToken>());

        // Step 3: Apply 30 EUR
        var used1 = await creditService.ApplyCreditAsync(owner, 30m, "EUR");
        Assert.Equal(30m, used1);

        // Step 4: Verify balance is 20
        var balance2 = await creditService.GetBalanceAsync(owner, "EUR");
        Assert.Equal(20m, balance2);

        // Step 5: Try to apply 30 EUR (more than remaining balance)
        var used2 = await creditService.ApplyCreditAsync(owner, 30m, "EUR");
        Assert.Equal(20m, used2); // capped to available balance

        // Step 6: Verify balance is 0
        var balance3 = await creditService.GetBalanceAsync(owner, "EUR");
        Assert.Equal(0m, balance3);

        // Verify HasCredit returns false when balance is 0
        Assert.False(await creditService.HasCreditAsync(owner, "EUR"));
    }

    /// <summary>
    /// Charge and refund workflow: create charge via ChargeBuilder -> verify Order + Payment ->
    /// refund via RefundService -> verify Refund record.
    /// </summary>
    [Fact]
    public async Task ChargeAndRefund_CreateChargeRefundVerify()
    {
        // Arrange: owner with existing mandate
        var owner = new TestBillable("e2e-charge-1", "cst_charge_1", "mdt_charge_1");

        // Mock Mollie recurring payment (owner has mandate -> direct charge)
        var mockPaymentResponse = Substitute.For<PaymentResponse>();
        mockPaymentResponse.Id = "tr_charge_e2e_1";
        mockPaymentResponse.Status = "open";

        _mollieClient.CreateRecurringPaymentAsync(
            "cst_charge_1", "mdt_charge_1", 25.00m,
            "EUR", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPaymentResponse);

        // Step 1: Create charge via ChargeBuilder
        var chargeBuilder = new ChargeBuilder<string>(
            _db, _mollieClient, _eventDispatcher, _options.Value, owner, 25.00m);
        var chargeResult = await chargeBuilder
            .WithDescription("One-time setup fee")
            .CreateAsync();

        // Verify: charge result
        Assert.False(chargeResult.RequiresAction); // has mandate, no redirect
        Assert.Null(chargeResult.CheckoutUrl);
        Assert.Equal(25.00m, chargeResult.Payment.Amount);
        Assert.Equal("tr_charge_e2e_1", chargeResult.Payment.MolliePaymentId);

        // Verify: Order record created
        var order = await _db.Orders.FindAsync(chargeResult.Order.Id);
        Assert.NotNull(order);
        Assert.Equal("e2e-charge-1", order!.OwnerId);
        Assert.Equal(25.00m, order.Total);
        Assert.Equal("tr_charge_e2e_1", order.MolliePaymentId);

        // Verify: Payment record created
        var payment = await _db.Payments
            .FirstAsync(p => p.MolliePaymentId == "tr_charge_e2e_1");
        Assert.Equal("e2e-charge-1", payment.OwnerId);
        Assert.Equal(25.00m, payment.Amount);

        // Verify: OrderCreated event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<OrderCreated<string>>(), Arg.Any<CancellationToken>());

        // Step 2: Create refund for the payment
        var mockRefundResponse = Substitute.For<RefundResponse>();
        mockRefundResponse.Id = "re_refund_e2e_1";
        mockRefundResponse.Status = "pending";

        _mollieClient.CreateRefundAsync(
            "tr_charge_e2e_1", 25.00m, "EUR", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(mockRefundResponse);

        var refundService = new RefundService<string>(_db, _mollieClient, _eventDispatcher, _options);
        var refund = await refundService.RefundAsync(payment, 25.00m, "Customer requested refund");

        // Verify: Refund record created
        Assert.NotNull(refund);
        Assert.Equal("re_refund_e2e_1", refund.MollieRefundId);
        Assert.Equal("pending", refund.Status);
        Assert.Equal(25.00m, refund.Amount);
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal("Customer requested refund", refund.Description);

        // Verify: Payment.AmountRefunded updated
        var updatedPayment = await _db.Payments.FindAsync(payment.Id);
        Assert.Equal(25.00m, updatedPayment!.AmountRefunded);

        // Verify: RefundInitiated event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<RefundInitiated<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Coupon workflow: validate coupon -> redeem for a subscription -> verify RedeemedCoupon record -> revoke.
    /// </summary>
    [Fact]
    public async Task CouponRedemption_ValidateRedeemRevoke()
    {
        // Arrange: setup coupon repository mock
        var couponRepo = Substitute.For<ICouponRepository>();
        var coupon = new Coupon
        {
            Code = "LAUNCH20",
            HandlerType = "percentage",
            Times = 3,
            Context = new Dictionary<string, string> { ["percentage"] = "20" },
        };
        couponRepo.FindByCodeAsync("LAUNCH20", Arg.Any<CancellationToken>())
            .Returns(coupon);

        var couponService = new CouponService<string>(_db, couponRepo, _eventDispatcher);

        // Create an active subscription for the owner
        var owner = new TestBillable("e2e-coupon-1", "cst_coupon_1", "mdt_coupon_1");
        var engine = new MollieBillingEngine<string>(_db, _mollieClient, _eventDispatcher, _options);
        var cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, _options);

        await cashier.NewSubscription(owner, "default", "pro-monthly")
            .CreateAsync();

        // Step 1: Validate coupon -> success
        var validatedCoupon = await couponService.ValidateAsync("LAUNCH20", owner);
        Assert.Equal("LAUNCH20", validatedCoupon.Code);
        Assert.Equal("percentage", validatedCoupon.HandlerType);
        Assert.Equal(3, validatedCoupon.Times);

        // Step 2: Redeem coupon -> verify RedeemedCoupon record
        var redeemed = await couponService.RedeemAsync(owner, "LAUNCH20", "default");
        Assert.NotNull(redeemed);
        Assert.Equal("LAUNCH20", redeemed.Code);
        Assert.Equal("default", redeemed.SubscriptionName);
        Assert.Equal(3, redeemed.TimesLeft);
        Assert.Equal("e2e-coupon-1", redeemed.OwnerId);

        // Verify CouponApplied event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<CouponApplied<string>>(), Arg.Any<CancellationToken>());

        // Verify RedeemedCoupon is persisted in DB
        var dbRedeemed = await _db.RedeemedCoupons
            .FirstOrDefaultAsync(c => c.OwnerId == "e2e-coupon-1" && c.Code == "LAUNCH20");
        Assert.NotNull(dbRedeemed);

        // Step 3: Revoke coupon -> verify removed
        await couponService.RevokeAsync(owner, "default");

        var afterRevoke = await _db.RedeemedCoupons
            .FirstOrDefaultAsync(c => c.OwnerId == "e2e-coupon-1" && c.Code == "LAUNCH20");
        Assert.Null(afterRevoke);
    }

    /// <summary>
    /// Subscription with trial days: create with 14-day trial -> verify OnTrial -> verify TrialEndsAt.
    /// </summary>
    [Fact]
    public async Task SubscriptionWithTrialDays_StartsOnTrial()
    {
        // Arrange
        var owner = new TestBillable("e2e-trial-1", "cst_trial_1", "mdt_trial_1");
        var engine = new MollieBillingEngine<string>(_db, _mollieClient, _eventDispatcher, _options);
        var cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, _options);

        // Act: create subscription with 14-day trial
        var result = await cashier.NewSubscription(owner, "default", "pro-monthly")
            .TrialDays(14)
            .CreateAsync();

        // Assert: subscription is active (has mandate)
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);

        // Assert: trial is active
        Assert.True(result.Subscription.OnTrial());
        Assert.True(await cashier.OnTrialAsync(owner, "default"));

        // Assert: TrialEndsAt is approximately 14 days from now
        Assert.NotNull(result.Subscription.TrialEndsAt);
        var daysUntilTrialEnd = (result.Subscription.TrialEndsAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(daysUntilTrialEnd, 13.5, 14.5);

        // Assert: subscription is considered "subscribed" during trial
        Assert.True(await cashier.IsSubscribedAsync(owner, "default"));
    }

    public void Dispose() => _db.Dispose();
}
