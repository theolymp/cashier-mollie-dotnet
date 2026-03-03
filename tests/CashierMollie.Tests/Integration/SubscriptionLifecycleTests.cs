using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Mollie.Api.Models.Payment.Response;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

public class SubscriptionLifecycleTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierService<string> _cashier;

    public SubscriptionLifecycleTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        });
        var engine = new MollieBillingEngine<string>(_db, _mollieClient, _eventDispatcher, options);
        _cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, options);
    }

    [Fact]
    public async Task FullLifecycle_CreateCancelResumeSwap()
    {
        var owner = new TestBillable("user-1", "cst_test", "mdt_test");

        // 1. Create subscription (owner has mandate, so no redirect)
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.False(result.RequiresAction);
        Assert.Null(result.CheckoutUrl);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);

        // 2. Verify subscription is active
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default"));
        Assert.False(await _cashier.OnGracePeriodAsync(owner, "default"));

        // 3. Cancel (enters grace period)
        await _cashier.CancelAsync(owner, "default");
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default")); // still subscribed during grace
        Assert.True(await _cashier.OnGracePeriodAsync(owner, "default"));

        // 4. Resume (exits grace period)
        await _cashier.ResumeAsync(owner, "default");
        Assert.True(await _cashier.IsSubscribedAsync(owner, "default"));
        Assert.False(await _cashier.OnGracePeriodAsync(owner, "default"));

        // 5. Swap plan
        var swapped = await _cashier.SwapAsync(owner, "default", "team");
        Assert.Equal("team", swapped.Plan);
    }

    [Fact]
    public async Task CreateWithoutMandate_RequiresCheckoutRedirect()
    {
        var ownerNoMandate = new TestBillable("user-2", "cst_new", null);

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

        var result = await _cashier.NewSubscription(ownerNoMandate, "default", "pro")
            .CreateAsync();

        Assert.True(result.RequiresAction);
        Assert.Equal("https://checkout.mollie.com/xxx", result.CheckoutUrl);
        Assert.Equal(SubscriptionStatus.Pending, result.Subscription.Status);

        // Verify a local Payment record was created
        var localPayment = await _db.Payments
            .FirstOrDefaultAsync(p => p.MolliePaymentId == "tr_first");
        Assert.NotNull(localPayment);
        Assert.Equal(result.Subscription.Id, localPayment.SubscriptionId);
        Assert.Equal("user-2", localPayment.OwnerId);
    }

    [Fact]
    public async Task CreateWithTrial_SetsTrialEndsAt()
    {
        var owner = new TestBillable("user-3", "cst_trial", "mdt_trial");

        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .TrialDays(14)
            .CreateAsync();

        Assert.True(await _cashier.OnTrialAsync(owner, "default"));
        Assert.NotNull(result.Subscription.TrialEndsAt);
        var daysUntilTrialEnd = (result.Subscription.TrialEndsAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(daysUntilTrialEnd, 13, 15);
    }

    [Fact]
    public async Task CreateSubscription_DispatchesCreatedEvent()
    {
        var owner = new TestBillable("user-4", "cst_evt", "mdt_evt");

        await _cashier.NewSubscription(owner, "default", "pro").CreateAsync();

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCreated<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleSubscriptions_IndependentLifecycles()
    {
        var owner = new TestBillable("user-5", "cst_multi", "mdt_multi");

        // Create two subscriptions
        await _cashier.NewSubscription(owner, "default", "pro").CreateAsync();
        await _cashier.NewSubscription(owner, "extra", "addon").CreateAsync();

        // Cancel only default
        await _cashier.CancelAsync(owner, "default");

        // Default is on grace, extra is still active
        Assert.True(await _cashier.OnGracePeriodAsync(owner, "default"));
        Assert.True(await _cashier.IsSubscribedAsync(owner, "extra"));
        Assert.False(await _cashier.OnGracePeriodAsync(owner, "extra"));

        // Get all subscriptions
        var subs = await _cashier.GetSubscriptionsAsync(owner);
        Assert.Equal(2, subs.Count);
    }

    [Fact]
    public async Task FirstPaymentWebhook_ActivatesPendingSubscription()
    {
        var owner = new TestBillable("user-6", "cst_fp", null);

        // Setup first payment mock
        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_fp_activate";
        mockPayment.Status = "open";
        var mockLinks = Substitute.For<PaymentResponseLinks>();
        var mockCheckout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        mockCheckout.Href = "https://checkout.mollie.com/fp";
        mockLinks.Checkout = mockCheckout;
        mockPayment.Links = mockLinks;

        _mollieClient.CreateFirstPaymentAsync(
            "cst_fp", Arg.Any<decimal>(), "EUR",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        // 1. Create subscription (pending — no mandate)
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.Equal(SubscriptionStatus.Pending, result.Subscription.Status);
        Assert.True(result.RequiresAction);

        // 2. Simulate Mollie webhook: payment is now "paid" with mandate
        var webhookPaymentResponse = Substitute.For<PaymentResponse>();
        webhookPaymentResponse.Id = "tr_fp_activate";
        webhookPaymentResponse.Status = "paid";
        webhookPaymentResponse.MandateId = "mdt_new";

        _mollieClient.GetPaymentAsync("tr_fp_activate", Arg.Any<CancellationToken>())
            .Returns(webhookPaymentResponse);

        var webhookService = new WebhookService<string>(_db, _mollieClient, _eventDispatcher);
        await webhookService.HandlePaymentAsync("tr_fp_activate");

        // 3. Verify subscription is now active
        var sub = await _db.Subscriptions.FindAsync(result.Subscription.Id);
        Assert.Equal(SubscriptionStatus.Active, sub!.Status);
        Assert.NotNull(sub.CycleStartedAt);

        // 4. Verify SubscriptionCreated event was dispatched
        await _eventDispatcher.Received().DispatchAsync(
            Arg.Any<SubscriptionCreated<string>>(), Arg.Any<CancellationToken>());

        // 5. Verify payment record was updated
        var payment = await _db.Payments.FirstAsync(p => p.MolliePaymentId == "tr_fp_activate");
        Assert.Equal("paid", payment.Status);
        Assert.Equal("mdt_new", payment.MollieMandateId);
        Assert.NotNull(payment.PaidAt);
    }

    [Fact]
    public async Task FirstPaymentWebhook_FailedPayment_DoesNotActivate()
    {
        var owner = new TestBillable("user-7", "cst_fail", null);

        var mockPayment = Substitute.For<PaymentResponse>();
        mockPayment.Id = "tr_fp_fail";
        mockPayment.Status = "open";
        var mockLinks = Substitute.For<PaymentResponseLinks>();
        var mockCheckout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        mockCheckout.Href = "https://checkout.mollie.com/fail";
        mockLinks.Checkout = mockCheckout;
        mockPayment.Links = mockLinks;

        _mollieClient.CreateFirstPaymentAsync(
            "cst_fail", Arg.Any<decimal>(), "EUR",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockPayment);

        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        // Simulate failed payment webhook
        var failedPayment = Substitute.For<PaymentResponse>();
        failedPayment.Id = "tr_fp_fail";
        failedPayment.Status = "failed";

        _mollieClient.GetPaymentAsync("tr_fp_fail", Arg.Any<CancellationToken>())
            .Returns(failedPayment);

        var webhookService = new WebhookService<string>(_db, _mollieClient, _eventDispatcher);
        await webhookService.HandlePaymentAsync("tr_fp_fail");

        // Subscription should still be pending
        var sub = await _db.Subscriptions.FindAsync(result.Subscription.Id);
        Assert.Equal(SubscriptionStatus.Pending, sub!.Status);
    }

    public void Dispose() => _db.Dispose();
}
