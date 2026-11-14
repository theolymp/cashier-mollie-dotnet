using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// The 0.6.0 MollieNative billing: a priced subscription is registered at Mollie, charged per
/// 30-day cycle, swapped at the cycle's end, repriced on demand and upgraded with credit.
/// Prices: pro 9.99, team 24.99, module 3.99, suite 9.99, all 24.99, all net at 19 %.
/// </summary>
public sealed class NativeBillingTests : IDisposable
{
    private const string WebhookUrl = "https://example.test/cashier/webhook";
    private static readonly string[] ModulePlans = ["module.a", "module.b", "module.c", "module.d", "module.e"];

    private readonly CashierDbContext<string> _db = TestDbContextFactory.Create();
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>().WithRecurringDefaults();
    private readonly ICashierEventDispatcher _events = Substitute.For<ICashierEventDispatcher>();
    private readonly IRefundService<string> _refunds = Substitute.For<IRefundService<string>>();
    private readonly FakePlanPriceResolver<string> _prices = new FakePlanPriceResolver<string>()
        .With("pro", 9.99m).With("team", 24.99m)
        .With("module.a", 3.99m).With("module.b", 3.99m).With("module.c", 3.99m)
        .With("module.d", 3.99m).With("module.e", 3.99m)
        .With("suite", 9.99m).With("all", 24.99m);
    private readonly CashierMollieOptions _options = new() { WebhookUrl = WebhookUrl };
    private readonly MollieBillingEngine<string> _engine;
    private readonly WebhookService<string> _webhook;

    public NativeBillingTests()
    {
        var options = Options.Create(_options);
        _engine = new MollieBillingEngine<string>(_db, _mollie, _events, options,
            new CouponService<string>(_db, Substitute.For<ICouponRepository>(), _events),
            _prices, _refunds, NullLogger<MollieBillingEngine<string>>.Instance);
        _webhook = new WebhookService<string>(_db, _mollie, _events, options,
            NullLogger<WebhookService<string>>.Instance);

        _mollie.CreateFirstPaymentAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(MolliePayment("tr_first", "open")));
    }

    public void Dispose() => _db.Dispose();

    private static PaymentResponse MolliePayment(string id, string status, string? mandateId = null,
        string? subscriptionId = null, decimal? amount = null)
    {
        var p = Substitute.For<PaymentResponse>();
        p.Id = id;
        p.Status = status;
        p.MandateId = mandateId;
        p.SubscriptionId = subscriptionId;
        if (amount.HasValue)
            p.Amount = new Amount(Currency.EUR, amount.Value);
        return p;
    }

    private Task CreateSubscriptionCalled(decimal gross, string description, DateTimeOffset startNear) =>
        _mollie.Received(1).CreateSubscriptionAsync("cst_1", gross, "EUR", "30 days", description,
            WebhookUrl, Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Is<DateTimeOffset?>(d => d.HasValue && Math.Abs((d.Value - startNear).TotalMinutes) < 5),
            Arg.Any<CancellationToken>());

    // ── Create ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithoutMandate_ChargesTheFullFirstCycleThroughCheckout()
    {
        var owner = new TestBillable("u1", "cst_1");

        var result = await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions());

        await _mollie.Received(1).CreateFirstPaymentAsync("cst_1", 11.89m, "EUR", "Plan pro",
            Arg.Any<string>(), WebhookUrl, Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
        Assert.True(result.RequiresAction);
        Assert.Equal(SubscriptionStatus.Pending, result.Subscription.Status);
        Assert.Equal(9.99m, result.Subscription.PriceNet);
        Assert.Equal(19m, result.Subscription.TaxRatePercent);

        var payment = await _db.Payments.SingleAsync();
        Assert.Equal(11.89m, payment.Amount);
        Assert.Equal(9.99m, payment.AmountNet);
        Assert.Equal(19m, payment.TaxRatePercent);
        Assert.Null(result.Subscription.MollieSubscriptionId);
    }

    [Fact]
    public async Task FirstPaymentPaid_ActivatesAndRegistersAtMollieOneCycleLater()
    {
        var owner = new TestBillable("u1", "cst_1");
        var created = await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions());
        _mollie.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_first", "paid", "mdt_1"));

        await _webhook.HandlePaymentAsync("tr_first");

        var sub = await _db.Subscriptions.SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(9.99m, sub.CycleValueNet);
        Assert.NotNull(sub.CycleStartedAt);
        Assert.Equal("sub_auto_1", sub.MollieSubscriptionId);
        await CreateSubscriptionCalled(11.89m, "Plan pro", DateTimeOffset.UtcNow.AddDays(30));
        await _mollie.Received(1).CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "mdt_1",
            Arg.Is<IReadOnlyDictionary<string, string>?>(m =>
                m != null && m["cashier_subscription_id"] == created.Subscription.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    // Mollie retries a webhook we answered with an error. The status was saved before the
    // registration failed, so the retry sees "nothing new" -- and must still register.
    public async Task FirstPaymentPaid_RegistrationThatFailed_IsCompletedByTheWebhookRetry()
    {
        var owner = new TestBillable("u1", "cst_1");
        await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions());
        _mollie.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_first", "paid", "mdt_1"));
        _mollie.CreateSubscriptionAsync(default!, default, default!, default!, default!, default!, default,
                default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Mollie.Api.Models.Subscription.Response.SubscriptionResponse>(
                new HttpRequestException("Mollie down")));

        await Assert.ThrowsAsync<HttpRequestException>(() => _webhook.HandlePaymentAsync("tr_first"));
        Assert.Null((await _db.Subscriptions.SingleAsync()).MollieSubscriptionId);

        _mollie.WithRecurringDefaults();
        await _webhook.HandlePaymentAsync("tr_first");

        Assert.Equal("sub_auto_1", (await _db.Subscriptions.SingleAsync()).MollieSubscriptionId);
    }

    [Fact]
    public async Task Create_WithMandate_IsActiveAtOnce_ChargesTheFirstCycle_AndRegisters()
    {
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions());

        Assert.False(result.RequiresAction);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        await _mollie.Received(1).CreateRecurringPaymentAsync("cst_1", "mdt_1", 11.89m, "EUR", "Plan pro",
            WebhookUrl, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        await CreateSubscriptionCalled(11.89m, "Plan pro", DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal(9.99m, (await _db.Payments.SingleAsync()).AmountNet);
        await _events.Received(1).DispatchAsync(Arg.Any<SubscriptionCreated<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_OnTrialWithMandate_ChargesNothingAndMollieStartsAtTheTrialEnd()
    {
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions { TrialDays = 7 });

        await _mollie.DidNotReceiveWithAnyArgs().CreateRecurringPaymentAsync(
            default!, default!, default, default!, default!, default!, default, default);
        await CreateSubscriptionCalled(11.89m, "Plan pro", DateTimeOffset.UtcNow.AddDays(7));
        Assert.Null(result.Subscription.CycleStartedAt);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
    }

    [Fact]
    public async Task Create_OnTrialWithoutMandate_TakesOnlyTheMandateCheck_AndStartsAtTheTrialEnd()
    {
        var owner = new TestBillable("u1", "cst_1");
        await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions { TrialDays = 7 });

        var payment = await _db.Payments.SingleAsync();
        Assert.Equal(_options.PaymentMethodUpdateAmount, payment.Amount);
        Assert.Null(payment.AmountNet);

        _mollie.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_first", "paid", "mdt_1"));
        await _webhook.HandlePaymentAsync("tr_first");

        var sub = await _db.Subscriptions.SingleAsync();
        Assert.Null(sub.CycleStartedAt);
        Assert.Null(sub.CycleValueNet);
        await CreateSubscriptionCalled(11.89m, "Plan pro", DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task Create_RejectsAForeignCurrency_AndWritesNothing()
    {
        _prices.Currency = "USD";
        _prices.With("usd-plan", 9.99m);
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var ex = await Assert.ThrowsAsync<CashierException>(() =>
            _engine.CreateSubscriptionAsync(owner, "x", "usd-plan", new SubscriptionOptions()));

        Assert.Contains("USD", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Subscriptions);
        await _mollie.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(
            default!, default, default!, default!, default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Create_MandateOnly_StaysLocal_WithoutAPrice()
    {
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.CreateSubscriptionAsync(owner, "x", "pro", new SubscriptionOptions { MandateOnly = true });

        Assert.Empty(_prices.Calls);
        Assert.Null(result.Subscription.PriceNet);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        await _mollie.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(
            default!, default, default!, default!, default!, default!, default, default, default, default);
    }

    // ── Cycles Mollie charges on its own ────────────────────────────────

    private async Task<Subscription<string>> SeedRegistered(string plan, decimal net, double startedDaysAgo,
        string mollieId = "sub_reg", string? name = null, decimal paidGross = 0m)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = name ?? plan, Plan = plan, MollieCustomerId = "cst_1",
            MollieSubscriptionId = mollieId, Status = SubscriptionStatus.Active,
            PriceNet = net, TaxRatePercent = 19m, Description = $"Plan {plan}",
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-startedDaysAgo), CycleValueNet = net,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = $"tr_paid_{sub.Id}",
            Status = "paid", PaidAt = sub.CycleStartedAt, Amount = paidGross == 0m ? Money.Gross(net, 19m) : paidGross,
            AmountNet = net, TaxRatePercent = 19m, MollieMandateId = "mdt_1",
        });
        await _db.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task ACyclePaymentMollieCreated_IsRecordedAtTheStoredNet_AndStartsTheCycle()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        _mollie.GetPaymentAsync("tr_cycle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_cycle", "paid", "mdt_1", "sub_reg", 11.89m));

        await _webhook.HandlePaymentAsync("tr_cycle");

        var payment = await _db.Payments.SingleAsync(p => p.MolliePaymentId == "tr_cycle");
        Assert.Equal("paid", payment.Status);
        Assert.Equal(11.89m, payment.Amount);
        Assert.Equal(9.99m, payment.AmountNet);
        Assert.Equal(19m, payment.TaxRatePercent);
        Assert.Equal(sub.Id, payment.SubscriptionId);
        Assert.True(sub.CycleStartedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        await _events.Received(1).DispatchAsync(Arg.Any<OrderPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACyclePaymentThatFails_IsRecorded_AndReportedAsFailed()
    {
        await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        _mollie.GetPaymentAsync("tr_cycle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_cycle", "failed", "mdt_1", "sub_reg", 11.89m));

        await _webhook.HandlePaymentAsync("tr_cycle");

        Assert.Equal("failed", (await _db.Payments.SingleAsync(p => p.MolliePaymentId == "tr_cycle")).Status);
        await _events.Received(1).DispatchAsync(Arg.Any<OrderPaymentFailed<string>>(), Arg.Any<CancellationToken>());
    }

    // ── Swap = downgrade at the cycle's end ─────────────────────────────

    [Fact]
    public async Task Downgrade_ChargesTheLowerPriceFromTheNextCycle_AndThePlanFollowsWhenPaid()
    {
        var sub = await SeedRegistered("team", 24.99m, startedDaysAgo: 10);

        await _engine.SwapPlanAsync(sub, "pro");

        await _mollie.Received(1).UpdateSubscriptionAsync("cst_1", "sub_reg", 11.89m, "EUR", "Plan pro",
            Arg.Any<CancellationToken>());
        Assert.Equal("team", sub.Plan);
        Assert.Equal("pro", sub.NextPlan);
        await _refunds.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default, default);

        _mollie.GetPaymentAsync("tr_cycle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_cycle", "paid", "mdt_1", "sub_reg", 11.89m));
        await _webhook.HandlePaymentAsync("tr_cycle");

        Assert.Equal("pro", sub.Plan);
        Assert.Null(sub.NextPlan);
        Assert.Equal(9.99m, sub.CycleValueNet);
    }

    // ── Cancel and resume ───────────────────────────────────────────────

    [Fact]
    public async Task Cancel_EndsWithThePaidCycle_AndStopsMollie_ResumeRegistersAgain()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10);
        var cycleEnd = sub.CycleStartedAt!.Value.AddDays(30);

        await _engine.CancelSubscriptionAsync(sub, immediately: false);

        Assert.Equal(cycleEnd, sub.EndsAt);
        // Kept, so a payment already under way at Mollie still finds the subscription.
        Assert.Equal("sub_reg", sub.MollieSubscriptionId);
        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_reg", Arg.Any<CancellationToken>());

        await _engine.ResumeSubscriptionAsync(sub);

        await CreateSubscriptionCalled(11.89m, "Plan pro", cycleEnd);
        Assert.Equal("sub_auto_1", sub.MollieSubscriptionId);
        Assert.Null(sub.EndsAt);
    }

    // ── Reprice ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reprice_SendsAChangedAmount_AndLeavesAnUnchangedOneAlone()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3, mollieId: "sub_pro");
        await SeedRegistered("team", 24.99m, startedDaysAgo: 3, mollieId: "sub_team");
        _prices.With("pro", 9.99m, rate: 0m); // the owner's VAT id was verified: reverse charge

        int changed = await _engine.RepriceAsync("u1");

        Assert.Equal(1, changed);
        await _mollie.Received(1).UpdateSubscriptionAsync("cst_1", "sub_pro", 9.99m, "EUR", "Plan pro",
            Arg.Any<CancellationToken>());
        await _mollie.DidNotReceive().UpdateSubscriptionAsync("cst_1", "sub_team", Arg.Any<decimal?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(0m, pro.TaxRatePercent);
    }

    // ── Upgrade ─────────────────────────────────────────────────────────

    [Fact]
    // pro 9.99 started 10.4 days ago: 19 whole days left, worth 9.99 x 19 / 30 = 6.327 -> 6.33.
    // team 24.99 - 6.33 = 18.66 net, x 1.19 = 22.2054 -> 22.21 charged now.
    public async Task Upgrade_CreditsTheUnusedDaysNet_AndChargesTheRestGross()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.UpgradeAsync(owner, "team", "team", [pro]);

        Assert.Equal(19, result.Replaced.Single().RemainingDays);
        Assert.Equal(6.33m, result.Replaced.Single().ValueNet);
        Assert.Equal(6.33m, result.CreditAppliedNet);
        Assert.Equal(18.66m, result.ChargedNet);
        Assert.Equal(22.21m, result.ChargedGross);
        await _mollie.Received(1).CreateRecurringPaymentAsync("cst_1", "mdt_1", 22.21m, "EUR", "Plan team",
            WebhookUrl, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(18.66m, result.Payment!.AmountNet);
        Assert.Equal(6.33m, result.Payment.CreditNet);

        // The replaced one ends now, at Mollie too; the new one is active with a fresh cycle and
        // Mollie charges its full price one cycle from now.
        Assert.Equal(SubscriptionStatus.Cancelled, pro.Status);
        Assert.Equal("sub_reg", pro.MollieSubscriptionId);
        Assert.Empty(result.Incomplete);
        Assert.Equal(result.Subscription.Id, pro.ReplacedBySubscriptionId);
        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_reg", Arg.Any<CancellationToken>());
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        Assert.Equal(24.99m, result.Subscription.CycleValueNet);
        await CreateSubscriptionCalled(29.74m, "Plan team", DateTimeOffset.UtcNow.AddDays(30));

        await _refunds.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default, default);
        await _events.Received(1).DispatchAsync(
            Arg.Is<SubscriptionUpgraded<string>>(e => e.Result == result), Arg.Any<CancellationToken>());
    }

    [Fact]
    // Five modules at 3.99, a day and a half into their cycle: 28 whole days left each, 3.99 x 28 / 30 = 3.724
    // -> 3.72, five of them 18.60. The suite costs 9.99, so 8.61 net is surplus. It is refunded,
    // not held: 3.72 net from module a and b each (x 1.19 = 4.4268 -> 4.43), then 1.17 from c
    // (x 1.19 = 1.3923 -> 1.39). Nothing is charged.
    public async Task Upgrade_ACreditAboveThePrice_RefundsTheSurplus_AndChargesNothing()
    {
        var modules = new List<Subscription<string>>();
        foreach (var (plan, i) in ModulePlans.Select((p, i) => (p, i)))
            modules.Add(await SeedRegistered(plan, 3.99m, startedDaysAgo: 1.5, mollieId: $"sub_m{i}"));
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.UpgradeAsync(owner, "suite", "suite", modules);

        Assert.Equal(18.60m, result.Replaced.Sum(r => r.ValueNet));
        Assert.Equal(9.99m, result.CreditAppliedNet);
        Assert.Equal(0m, result.ChargedGross);
        Assert.Null(result.Payment);
        await _mollie.DidNotReceiveWithAnyArgs().CreateRecurringPaymentAsync(
            default!, default!, default, default!, default!, default!, default, default);

        await _refunds.Received(1).RefundAsync(Arg.Is<Payment<string>>(p => p.SubscriptionId == modules[0].Id),
            4.43m, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _refunds.Received(1).RefundAsync(Arg.Is<Payment<string>>(p => p.SubscriptionId == modules[1].Id),
            4.43m, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _refunds.Received(1).RefundAsync(Arg.Is<Payment<string>>(p => p.SubscriptionId == modules[2].Id),
            1.39m, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(3, _refunds.ReceivedCalls().Count());
        Assert.Equal(0m, result.UnrefundedNet);

        // Even with nothing charged, Mollie takes over one cycle from now at the full price.
        await CreateSubscriptionCalled(11.89m, "Plan suite", DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    // A tax treatment that changed since the old invoice: no offset across two treatments. The
    // whole value 6.33 net is refunded at the 19 % it was paid at (7.5327 -> 7.53) and the new
    // plan is charged in full, here at 0 % reverse charge.
    public async Task Upgrade_WithoutOffset_RefundsTheWholeValue_AndChargesTheFullPrice()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        _prices.With("team", 24.99m, rate: 0m);
        var owner = new TestBillable("u1", "cst_1", "mdt_1");

        var result = await _engine.UpgradeAsync(owner, "team", "team", [pro],
            new UpgradeOptions { OffsetCredit = false });

        Assert.Equal(0m, result.CreditAppliedNet);
        Assert.Equal(24.99m, result.ChargedGross);
        await _refunds.Received(1).RefundAsync(Arg.Any<Payment<string>>(), 7.53m,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    // The suite above was paid by credit alone and has no payment of its own. Its value is still
    // sitting in the module payments it replaced, so a later refund of it is taken from those.
    public async Task Upgrade_ACreditOnlyCycle_IsRefundedFromThePaymentsBehindIt()
    {
        var module = await SeedRegistered("module.a", 3.99m, startedDaysAgo: 1, mollieId: "sub_m");
        var owner = new TestBillable("u1", "cst_1", "mdt_1");
        _prices.With("suite", 3.00m);    // cheaper than the module's value: credit-only suite
        var first = await _engine.UpgradeAsync(owner, "suite", "suite", [module]);
        Assert.Null(first.Payment);
        _refunds.ClearReceivedCalls();

        var second = await _engine.UpgradeAsync(owner, "all", "all", [first.Subscription],
            new UpgradeOptions { OffsetCredit = false });

        // The suite started moments ago, so 29 whole days are left (the current day is used):
        // 3.00 x 29 / 30 = 2.90 net, x 1.19 = 3.451 -> 3.45, refunded onto the module's payment,
        // the only money behind it.
        Assert.Equal(2.90m, second.Replaced.Single().ValueNet);
        await _refunds.Received(1).RefundAsync(Arg.Is<Payment<string>>(p => p.SubscriptionId == module.Id),
            3.45m, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(0m, second.UnrefundedNet);
    }

    [Fact]
    public async Task Upgrade_RefusesBeforeAnythingMoves()
    {
        var mine = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3);
        var owner = new TestBillable("u1", "cst_1", "mdt_1");
        var foreign = new Subscription<string> { Id = 999, OwnerId = "u2", Plan = "pro", Status = SubscriptionStatus.Active, PriceNet = 1m };
        var unpriced = new Subscription<string> { Id = 998, OwnerId = "u1", Plan = "pro", Status = SubscriptionStatus.Active };

        await Assert.ThrowsAsync<CashierException>(() => _engine.UpgradeAsync(owner, "team", "team", []));
        await Assert.ThrowsAsync<CashierException>(() => _engine.UpgradeAsync(owner, "team", "team", [foreign]));
        await Assert.ThrowsAsync<CashierException>(() => _engine.UpgradeAsync(owner, "team", "team", [unpriced]));
        await Assert.ThrowsAsync<CashierException>(() => _engine.UpgradeAsync(owner, "team", "team", [mine, mine]));
        await SeedRegistered("team", 24.99m, startedDaysAgo: 1, mollieId: "sub_other", name: "team");
        await Assert.ThrowsAsync<CashierException>(() => _engine.UpgradeAsync(owner, "team", "team", [mine]));

        await _mollie.DidNotReceiveWithAnyArgs().CreateRecurringPaymentAsync(
            default!, default!, default, default!, default!, default!, default, default);
        await _mollie.DidNotReceiveWithAnyArgs().CancelSubscriptionAsync(default!, default!, default);
        Assert.Equal(SubscriptionStatus.Active, mine.Status);
    }

    [Fact]
    public async Task Upgrade_WithoutAnyMandate_Refuses()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3);
        foreach (var p in _db.Payments)
            p.MollieMandateId = null;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<CashierException>(() =>
            _engine.UpgradeAsync(new TestBillable("u1", "cst_1"), "team", "team", [pro]));
        Assert.Equal(SubscriptionStatus.Active, pro.Status);
    }

    // ── Review findings, 2026-11-14: failures after the charge, races, late payments ──

    [Fact]
    // (OwnerId, Name) is unique over every row. Reusing the replaced subscription's name would
    // pass any "live subscriptions only" check and then fail in the database -- after the charge.
    public async Task Upgrade_ReusingTheReplacedName_IsRefusedBeforeTheCharge()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3, name: "modcockpit");

        await Assert.ThrowsAsync<CashierException>(() =>
            _engine.UpgradeAsync(new TestBillable("u1", "cst_1", "mdt_1"), "modcockpit", "team", [pro]));

        await _mollie.DidNotReceiveWithAnyArgs().CreateRecurringPaymentAsync(
            default!, default!, default, default!, default!, default!, default, default);
        Assert.Equal(SubscriptionStatus.Active, pro.Status);
    }

    [Fact]
    // Once the customer is charged, a failing Mollie step must not lose the charge or claim the
    // upgrade failed. Everything local is recorded; the failure is reported in Incomplete; and the
    // webhook of the upgrade payment completes the registration.
    public async Task Upgrade_RegistrationFailingAfterTheCharge_IsReported_AndCompletedByTheWebhook()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        _mollie.CreateSubscriptionAsync(default!, default, default!, default!, default!, default!, default,
                default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Mollie.Api.Models.Subscription.Response.SubscriptionResponse>(
                new HttpRequestException("Mollie down")));

        var result = await _engine.UpgradeAsync(new TestBillable("u1", "cst_1", "mdt_1"), "team", "team", [pro]);

        Assert.Single(result.Incomplete);
        Assert.Null(result.Subscription.MollieSubscriptionId);
        var payment = await _db.Payments.SingleAsync(p => p.MolliePaymentId == result.Payment!.MolliePaymentId);
        Assert.Equal(result.Subscription.Id, payment.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Cancelled, pro.Status);

        _mollie.WithRecurringDefaults();
        _mollie.GetPaymentAsync(payment.MolliePaymentId, Arg.Any<CancellationToken>())
            .Returns(MolliePayment(payment.MolliePaymentId, "paid", "mdt_1"));
        await _webhook.HandlePaymentAsync(payment.MolliePaymentId);

        Assert.Equal("sub_auto_1", result.Subscription.MollieSubscriptionId);
    }

    [Fact]
    public async Task Upgrade_ACancelAndARefundThatFail_AreReported_NotThrown()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        _mollie.CancelSubscriptionAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new HttpRequestException("Mollie down")));
        _refunds.RefundAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Refund<string>>(new HttpRequestException("refund refused")));

        var result = await _engine.UpgradeAsync(new TestBillable("u1", "cst_1", "mdt_1"), "team", "team", [pro],
            new UpgradeOptions { OffsetCredit = false });

        Assert.Equal(2, result.Incomplete.Count);
        Assert.Equal(6.33m, result.UnrefundedNet);
        Assert.Equal(SubscriptionStatus.Cancelled, pro.Status);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
    }

    [Fact]
    public async Task Upgrade_CreditOnly_WithAFailedRegistration_IsRepairedByReprice()
    {
        var module = await SeedRegistered("module.a", 3.99m, startedDaysAgo: 1.5, mollieId: "sub_m");
        _prices.With("suite", 1.00m);
        _mollie.CreateSubscriptionAsync(default!, default, default!, default!, default!, default!, default,
                default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Mollie.Api.Models.Subscription.Response.SubscriptionResponse>(
                new HttpRequestException("Mollie down")));
        var result = await _engine.UpgradeAsync(new TestBillable("u1", "cst_1", "mdt_1"), "suite", "suite", [module]);
        Assert.Null(result.Payment);  // nothing charged, so no webhook will ever retry
        Assert.Single(result.Incomplete);

        _mollie.WithRecurringDefaults();
        int changed = await _engine.RepriceAsync("u1");

        Assert.Equal(1, changed);
        Assert.Equal("sub_auto_1", result.Subscription.MollieSubscriptionId);
        // Twice with the same arguments: the attempt that failed during the upgrade, and the repair.
        await _mollie.Received(2).CreateSubscriptionAsync("cst_1", 1.19m, "EUR", "30 days", "Plan suite",
            WebhookUrl, "mdt_1", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithMandate_RegistrationFailing_KeepsTheCharge_AndTheWebhookRegisters()
    {
        _mollie.CreateSubscriptionAsync(default!, default, default!, default!, default!, default!, default,
                default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Mollie.Api.Models.Subscription.Response.SubscriptionResponse>(
                new HttpRequestException("Mollie down")));

        var result = await _engine.CreateSubscriptionAsync(new TestBillable("u1", "cst_1", "mdt_1"), "pro", "pro",
            new SubscriptionOptions());

        var payment = await _db.Payments.SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);
        Assert.Equal(result.Subscription.Id, payment.SubscriptionId);

        _mollie.WithRecurringDefaults();
        _mollie.GetPaymentAsync(payment.MolliePaymentId, Arg.Any<CancellationToken>())
            .Returns(MolliePayment(payment.MolliePaymentId, "paid", "mdt_1"));
        await _webhook.HandlePaymentAsync(payment.MolliePaymentId);

        Assert.Equal("sub_auto_1", result.Subscription.MollieSubscriptionId);
    }

    [Fact]
    // Two webhooks for the same paid payment of an active subscription that still has to be
    // registered. Both contexts read it unregistered; the second one's claim loses on the
    // concurrency token, and Mollie is asked exactly once. (A first version of this test let the
    // second context read the subscription while it was still pending, so it returned early for
    // another reason and stayed green with the claim removed. Measured by that sabotage.)
    public async Task ConcurrentWebhooks_RegisterOneMollieSubscription()
    {
        string dbName = Guid.NewGuid().ToString();
        using var first = TestDbContextFactory.Create(dbName);
        using var second = TestDbContextFactory.Create(dbName);
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "pro", Plan = "pro", MollieCustomerId = "cst_1",
            Status = SubscriptionStatus.Active, PriceNet = 9.99m, TaxRatePercent = 19m,
            Description = "Plan pro", CycleStartedAt = DateTimeOffset.UtcNow, CycleValueNet = 9.99m,
        };
        first.Subscriptions.Add(sub);
        await first.SaveChangesAsync();
        first.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow, Amount = 11.89m, AmountNet = 9.99m, TaxRatePercent = 19m,
            MollieMandateId = "mdt_1",
        });
        await first.SaveChangesAsync();
        _mollie.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_first", "paid", "mdt_1"));
        var options = Options.Create(_options);

        var stale = await second.Subscriptions.FindAsync(sub.Id);
        Assert.Null(stale!.MollieSubscriptionId);
        await new WebhookService<string>(first, _mollie, _events, options,
            NullLogger<WebhookService<string>>.Instance).HandlePaymentAsync("tr_first");
        await new WebhookService<string>(second, _mollie, _events, options,
            NullLogger<WebhookService<string>>.Instance).HandlePaymentAsync("tr_first");

        await _mollie.ReceivedWithAnyArgs(1).CreateSubscriptionAsync(
            default!, default, default!, default!, default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task AFreshClaim_IsLeftAlone_AStaleOneIsTakenOver()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 0.1, mollieId: "registering");
        var payment = await _db.Payments.SingleAsync();
        _mollie.GetPaymentAsync(payment.MolliePaymentId, Arg.Any<CancellationToken>())
            .Returns(MolliePayment(payment.MolliePaymentId, "paid", "mdt_1"));

        await _webhook.HandlePaymentAsync(payment.MolliePaymentId);
        await _mollie.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(
            default!, default, default!, default!, default!, default!, default, default, default, default);

        // SaveChanges stamps UpdatedAt itself, so the age is set on the tracked entity the
        // webhook will read, without saving.
        sub.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        await _webhook.HandlePaymentAsync(payment.MolliePaymentId);

        Assert.Equal("sub_auto_1", sub.MollieSubscriptionId);
    }

    [Fact]
    // The subscription was cancelled while a payment was under way at Mollie. Before the review
    // the cancel cleared the Mollie id and this payment was silently dropped.
    public async Task APaymentUnderWayAtCancel_IsStillRecorded()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        await _engine.CancelSubscriptionAsync(sub, immediately: true);
        var inFlight = MolliePayment("tr_late", "paid", "mdt_1", "sub_reg", 11.89m);
        inFlight.CreatedAt = sub.EndsAt!.Value.UtcDateTime.AddMinutes(-1);
        _mollie.GetPaymentAsync("tr_late", Arg.Any<CancellationToken>()).Returns(inFlight);
        _mollie.ClearReceivedCalls();

        await _webhook.HandlePaymentAsync("tr_late");

        Assert.True(await _db.Payments.AnyAsync(p => p.MolliePaymentId == "tr_late"));
        await _mollie.DidNotReceiveWithAnyArgs().CancelSubscriptionAsync(default!, default!, default);
    }

    [Fact]
    // Mollie charged after the end date: the cancellation at Mollie had failed. The webhook
    // records the payment and cancels again.
    public async Task APaymentCreatedAfterTheEnd_CancelsAtMollieAgain()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        sub.Status = SubscriptionStatus.Cancelled;
        sub.EndsAt = DateTimeOffset.UtcNow.AddDays(-2);
        await _db.SaveChangesAsync();
        var late = MolliePayment("tr_after", "paid", "mdt_1", "sub_reg", 11.89m);
        late.CreatedAt = DateTime.UtcNow;
        _mollie.GetPaymentAsync("tr_after", Arg.Any<CancellationToken>()).Returns(late);

        await _webhook.HandlePaymentAsync("tr_after");

        Assert.True(await _db.Payments.AnyAsync(p => p.MolliePaymentId == "tr_after"));
        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_reg", Arg.Any<CancellationToken>());
    }

    // ── Payment default (AGB §6/§7, compliance (c)): lock, settle, unlock ──

    private async Task<Payment<string>> FailCycle(Subscription<string> sub, string paymentId = "tr_cycle")
    {
        _mollie.GetPaymentAsync(paymentId, Arg.Any<CancellationToken>())
            .Returns(MolliePayment(paymentId, "failed", "mdt_1", "sub_reg", 11.89m));
        await _webhook.HandlePaymentAsync(paymentId);
        return await _db.Payments.SingleAsync(p => p.MolliePaymentId == paymentId);
    }

    [Fact]
    public async Task AFailedCyclePayment_PutsTheSubscriptionPastDue_SoItIsNoLongerActive()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);

        var failed = await FailCycle(sub);

        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.False(sub.IsActive());
        await _events.Received(1).DispatchAsync(
            Arg.Is<SubscriptionPastDue<string>>(e => e.FailedPayment.Id == failed.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PayOutstanding_ChecksOutTheFailedAmount_AndUnlocksWhenPaid_OnTheNewMandate()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        var failed = await FailCycle(sub);
        var checkout = MolliePayment("tr_settle", "open");
        var links = Substitute.For<PaymentResponseLinks>();
        var href = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        href.Href = "https://mollie.test/checkout/settle";
        links.Checkout = href;
        checkout.Links = links;
        _mollie.CreateFirstPaymentAsync(default!, default, default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(checkout));

        var result = await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1", "mdt_1"), sub);

        Assert.Equal("https://mollie.test/checkout/settle", result.CheckoutUrl);
        Assert.Equal(11.89m, result.Amount);
        Assert.Equal(0, result.RemainingAfterThis);
        await _mollie.Received(1).CreateFirstPaymentAsync("cst_1", 11.89m, "EUR", Arg.Any<string>(),
            Arg.Any<string>(), WebhookUrl, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        var settlement = await _db.Payments.SingleAsync(p => p.MolliePaymentId == "tr_settle");
        Assert.Equal(failed.Id, settlement.SettlesPaymentId);

        _mollie.GetPaymentAsync("tr_settle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_settle", "paid", "mdt_new"));
        await _webhook.HandlePaymentAsync("tr_settle");

        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        await _mollie.Received(1).UpdateSubscriptionMandateAsync("cst_1", "sub_reg", "mdt_new", Arg.Any<CancellationToken>());
        await _events.Received(1).DispatchAsync(Arg.Any<SubscriptionPaymentRecovered<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    // A later cycle Mollie charged successfully does not pay the one that failed: the lock stays
    // until every failed cycle is settled. And a failed settlement attempt is not a second debt.
    public async Task TheLockHoldsUntilEveryFailedCycleIsSettled()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        await FailCycle(sub, "tr_c1");

        _mollie.GetPaymentAsync("tr_c2", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_c2", "paid", "mdt_1", "sub_reg", 11.89m));
        await _webhook.HandlePaymentAsync("tr_c2");
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);

        _mollie.CreateFirstPaymentAsync(default!, default, default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                var p = MolliePayment("tr_s1", "open");
                p.Links = Substitute.For<PaymentResponseLinks>();
                p.Links.Checkout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
                p.Links.Checkout.Href = "https://mollie.test/c";
                return Task.FromResult(p);
            });
        await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub);
        _mollie.GetPaymentAsync("tr_s1", Arg.Any<CancellationToken>()).Returns(MolliePayment("tr_s1", "failed"));
        await _webhook.HandlePaymentAsync("tr_s1");

        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        // Through the library's own query, not a re-implementation of it.
        Assert.Single(await Cashier().GetOutstandingPaymentsAsync(new TestBillable("u1", "cst_1"), "pro"));
    }

    private CashierService<string> Cashier() => new(_db, _engine, _mollie, _events, Options.Create(_options),
        NullLogger<CashierService<string>>.Instance);

    private void CheckoutReturns(string id, string href) =>
        _mollie.CreateFirstPaymentAsync(default!, default, default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                var p = MolliePayment(id, "open");
                p.Links = Substitute.For<PaymentResponseLinks>();
                p.Links.Checkout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
                p.Links.Checkout.Href = href;
                return Task.FromResult(p);
            });

    [Fact]
    // Review F2: a SEPA debit reported paid and failing days later. It must count as outstanding
    // (payable), not lock the subscription with nothing to pay.
    public async Task APaymentThatIsPaidAndThenFails_IsOutstandingAndPayable()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        _mollie.GetPaymentAsync("tr_sepa", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_sepa", "paid", "mdt_1", "sub_reg", 11.89m));
        await _webhook.HandlePaymentAsync("tr_sepa");
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        _mollie.GetPaymentAsync("tr_sepa", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_sepa", "failed", "mdt_1", "sub_reg", 11.89m));
        await _webhook.HandlePaymentAsync("tr_sepa");

        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        var payment = await _db.Payments.SingleAsync(p => p.MolliePaymentId == "tr_sepa");
        Assert.Null(payment.PaidAt);
        CheckoutReturns("tr_settle", "https://mollie.test/c");
        var result = await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub);
        Assert.Equal(11.89m, result.Amount);
    }

    [Fact]
    // Review F1: the lock is derived from stored state, so a webhook retry that sees no status
    // change still locks. Simulated by a failure whose status was saved but whose lock was not.
    public async Task AWebhookRetry_StillLocks_WhenTheFirstAttemptSavedOnlyTheStatus()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_half", Status = "failed",
            FailedAt = DateTimeOffset.UtcNow, Amount = 11.89m, AmountNet = 9.99m, TaxRatePercent = 19m,
        });
        await _db.SaveChangesAsync();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        _mollie.GetPaymentAsync("tr_half", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_half", "failed", "mdt_1", "sub_reg", 11.89m));

        await _webhook.HandlePaymentAsync("tr_half");

        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
    }

    [Fact]
    // Review F3: a second call hands out the open checkout again instead of a second one.
    public async Task PayOutstanding_Twice_ReusesTheOpenCheckout()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        await FailCycle(sub);
        CheckoutReturns("tr_settle", "https://mollie.test/first");
        var first = await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub);
        var open = MolliePayment("tr_settle", "open");
        open.Links = Substitute.For<PaymentResponseLinks>();
        open.Links.Checkout = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
        open.Links.Checkout.Href = "https://mollie.test/first";
        _mollie.GetPaymentAsync("tr_settle", Arg.Any<CancellationToken>()).Returns(open);

        var second = await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub);

        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        await _mollie.ReceivedWithAnyArgs(1).CreateFirstPaymentAsync(
            default!, default, default!, default!, default!, default!, default, default);
    }

    [Fact]
    // Review F6: the engine checks that the subscription is the owner's.
    public async Task PayOutstanding_ForAnotherOwnersSubscription_Refuses()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 30);
        await FailCycle(sub);
        await Assert.ThrowsAsync<CashierException>(() =>
            _engine.PayOutstandingAsync(new TestBillable("u2", "cst_2"), sub));
    }

    [Fact]
    // A failed checkout while the subscription is still pending is not a debt: it never started,
    // and a later successful checkout must not be locked by it.
    public async Task AFailedFirstCheckout_IsNotADebt()
    {
        var owner = new TestBillable("u1", "cst_1");
        await _engine.CreateSubscriptionAsync(owner, "pro", "pro", new SubscriptionOptions());
        _mollie.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>()).Returns(MolliePayment("tr_first", "expired"));
        await _webhook.HandlePaymentAsync("tr_first");

        var sub = await _db.Subscriptions.SingleAsync();
        sub.Status = SubscriptionStatus.Active; // as after a second, successful checkout
        await _db.SaveChangesAsync();
        Assert.Empty(await Cashier().GetOutstandingPaymentsAsync(owner, "pro"));
    }

    [Fact]
    // Review F5: a settled cycle is the paid cycle afterwards, so an early end refunds from it.
    public async Task AfterASettlement_AnEarlyEndRefundsTheSettledCycle()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 40);
        var failed = await FailCycle(sub);
        CheckoutReturns("tr_settle", "https://mollie.test/c");
        await _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub);
        _mollie.GetPaymentAsync("tr_settle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_settle", "paid", "mdt_new"));
        await _webhook.HandlePaymentAsync("tr_settle");

        Assert.Equal(failed.CreatedAt, sub.CycleStartedAt);
        var result = await _engine.CancelImmediatelyWithRefundAsync(sub);
        Assert.True(result.RemainingDays >= 29);
        Assert.Equal(0m, result.UnrefundedNet);
    }

    [Fact]
    // Review F7: a refund that fails after the end is reported, not thrown, and not lost.
    public async Task CancelImmediatelyWithRefund_AFailedRefund_IsReportedAsOwed()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        _refunds.RefundAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<Refund<string>>(new HttpRequestException("refused")));

        var result = await _engine.CancelImmediatelyWithRefundAsync(sub);

        Assert.Equal(6.33m, result.UnrefundedNet);
        Assert.Equal(0m, result.RefundedGross);
        await Assert.ThrowsAsync<CashierException>(() => _engine.CancelImmediatelyWithRefundAsync(sub));
    }

    [Fact]
    // The outstanding query on a relational provider. InMemory accepts what SQLite refuses, such
    // as ordering by a DateTimeOffset.
    public async Task OutstandingPayments_TranslateOnSqlite()
    {
        using var database = new SqliteTestDatabase<string>();
        using var db = database.NewContext();
        var sub = new Subscription<string> { OwnerId = "u1", Name = "pro", Plan = "pro", Status = SubscriptionStatus.PastDue, PriceNet = 9.99m };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        db.Payments.AddRange(
            new Payment<string> { OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "f1", Status = "failed", Amount = 1m, AmountNet = 1m },
            new Payment<string> { OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "f2", Status = "expired", Amount = 2m, AmountNet = 2m });
        await db.SaveChangesAsync();
        var f1 = await db.Payments.SingleAsync(p => p.MolliePaymentId == "f1");
        db.Payments.Add(new Payment<string> { OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "s1", Status = "paid", Amount = 1m, AmountNet = 1m, SettlesPaymentId = f1.Id });
        await db.SaveChangesAsync();
        var cashier = new CashierService<string>(db, _engine, _mollie, _events, Options.Create(_options),
            NullLogger<CashierService<string>>.Instance);

        var outstanding = await cashier.GetOutstandingPaymentsAsync(new TestBillable("u1"), "pro");

        Assert.Equal("f2", Assert.Single(outstanding).MolliePaymentId);
    }

    [Fact]
    public async Task PayOutstanding_OnASubscriptionThatIsNotPastDue_Refuses()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3);
        await Assert.ThrowsAsync<CashierException>(() =>
            _engine.PayOutstandingAsync(new TestBillable("u1", "cst_1"), sub));
    }

    // ── Termination for good cause: end now, refund the unused days ──

    [Fact]
    // 10.4 days into a 9.99 cycle: 19 whole days left, 6.33 net, x 1.19 = 7.5327 -> 7.53 refunded.
    public async Task CancelImmediatelyWithRefund_StopsMollie_AndRefundsTheUnusedDays()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);

        var result = await _engine.CancelImmediatelyWithRefundAsync(sub);

        Assert.Equal(19, result.RemainingDays);
        Assert.Equal(6.33m, result.ValueNet);
        Assert.Equal(7.53m, result.RefundedGross);
        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_reg", Arg.Any<CancellationToken>());
        await _refunds.Received(1).RefundAsync(Arg.Is<Payment<string>>(p => p.SubscriptionId == sub.Id), 7.53m,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    // An ordinary cancellation refunds nothing; it runs to the end of the paid cycle (AGB §7).
    public async Task AnOrdinaryCancel_RefundsNothing()
    {
        var sub = await SeedRegistered("pro", 9.99m, startedDaysAgo: 10.4);
        await _engine.CancelSubscriptionAsync(sub, immediately: false);
        await _refunds.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default, default);
    }

    [Fact]
    // A refused mandate must leave the replaced subscription running: the charge is the first
    // call to Mollie, before anything is ended.
    public async Task Upgrade_AChargeMollieRefuses_EndsNothing()
    {
        var pro = await SeedRegistered("pro", 9.99m, startedDaysAgo: 3);
        _mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromException<PaymentResponse>(new HttpRequestException("mandate invalid")));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _engine.UpgradeAsync(new TestBillable("u1", "cst_1", "mdt_1"), "team", "team", [pro]));

        Assert.Equal(SubscriptionStatus.Active, pro.Status);
        Assert.Equal("sub_reg", pro.MollieSubscriptionId);
        Assert.Single(_db.Subscriptions);
    }
}
