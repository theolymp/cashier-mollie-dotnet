using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// Dunning on the Managed engine (0.6.0-beta.8): its charges are payment rows, a failed one makes
/// the subscription past_due, it is settled through PayOutstandingAsync, and nothing is retried.
/// </summary>
public sealed class ManagedDunningTests : IDisposable
{
    private const decimal UnitPrice = 29.00m;

    private readonly CashierDbContext<string> _db = TestDbContextFactory.Create();
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private readonly ICashierEventDispatcher _events = Substitute.For<ICashierEventDispatcher>();
    private readonly CashierMollieOptions _options = new()
    {
        Currency = "EUR",
        BillingEngine = BillingEngineType.Managed,
        Dunning = new DunningOptions { Enabled = true },
    };

    public void Dispose() => _db.Dispose();

    private ManagedBillingEngine<string> Engine() =>
        new(_db, _mollie, _events, Options.Create(_options),
            NullLogger<ManagedBillingEngine<string>>.Instance,
            new CouponService<string>(_db, Substitute.For<ICouponRepository>(), _events));

    private WebhookService<string> Webhook() =>
        new(_db, _mollie, _events, Options.Create(_options), NullLogger<WebhookService<string>>.Instance);

    private DunningService<string> Dunning() =>
        new(_db, _mollie, _events, Options.Create(_options), NullLogger<DunningService<string>>.Instance);

    private static PaymentResponse MolliePayment(string id, string status, string? mandateId = null,
        string? checkoutUrl = null)
    {
        var p = Substitute.For<PaymentResponse>();
        p.Id = id;
        p.Status = status;
        p.MandateId = mandateId;
        if (checkoutUrl != null)
        {
            var href = Substitute.For<Mollie.Api.Models.Url.UrlObjectLink<PaymentResponse>>();
            href.Href = checkoutUrl;
            var links = Substitute.For<PaymentResponseLinks>();
            links.Checkout = href;
            p.Links = links;
        }

        return p;
    }

    private void ChargesReturn(PaymentResponse response) =>
        _mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(response));

    private void ChargesAreRefused() =>
        _mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new CashierMollie.Testing.FakeMollieException("The mandate is invalid", 422));

    private void ChargesTimeOut() =>
        _mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("timeout"));

    private Task<int> ChargeCalls() => Task.FromResult(_mollie.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(IMollieClientService.CreateRecurringPaymentAsync)));

    private async Task<Subscription<string>> SeedDueAsync(string status = SubscriptionStatus.Active)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", MollieCustomerId = "cst_1",
            Status = status, CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = "mdt_1", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = UnitPrice, Quantity = 1, ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await _db.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task AnAcceptedChargeIsRecordedAsAPaymentRow()
    {
        var sub = await SeedDueAsync();
        ChargesReturn(MolliePayment("tr_cycle", "pending"));

        await Engine().ProcessDueItemsAsync();

        var row = Assert.Single(_db.Payments, p => p.MolliePaymentId == "tr_cycle");
        Assert.Equal(sub.Id, row.SubscriptionId);
        Assert.Equal(UnitPrice, row.Amount);
        Assert.Equal(UnitPrice, row.AmountNet);
        Assert.Equal("mdt_1", row.MollieMandateId);
        Assert.Equal("pending", row.Status);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task ARefusedChargeIsADebt_NotARetry()
    {
        var sub = await SeedDueAsync();
        ChargesAreRefused();

        await Engine().ProcessDueItemsAsync();
        await Engine().ProcessDueItemsAsync();   // before beta.8 this charged again, every pass

        Assert.Equal(1, await ChargeCalls());
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.NotNull(sub.PastDueSince);
        var failed = Assert.Single(_db.Payments, p => p.Status == "failed");
        Assert.StartsWith("refused_", failed.MolliePaymentId, StringComparison.Ordinal);
        Assert.Equal(UnitPrice, failed.AmountNet);
        // The item is consumed and its successor waits; consuming it is what keeps a settled debt
        // from being charged again once the subscription is active.
        Assert.Single(_db.OrderItems, i => i.ProcessedAt != null && i.MolliePaymentStatus == "failed");
        Assert.Single(_db.OrderItems, i => i.ProcessedAt == null && i.ProcessAt > DateTimeOffset.UtcNow);
        await _events.Received(1).DispatchAsync(
            Arg.Is<OrderPaymentFailed<string>>(e => e.Payment.Id == failed.Id), Arg.Any<CancellationToken>());
        await _events.Received(1).DispatchAsync(
            Arg.Is<SubscriptionPastDue<string>>(e => e.FailedPayment.Id == failed.Id && !e.NeverPaid),
            Arg.Any<CancellationToken>());
        Assert.Empty(_db.Orders);   // nothing was charged, so there is no order to describe
    }

    [Fact]
    public async Task ATimeoutIsNotADebt_ThePaymentMayExist()
    {
        var sub = await SeedDueAsync();
        ChargesTimeOut();

        await Engine().ProcessDueItemsAsync();

        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.DoesNotContain(_db.Payments, p => p.Status == "failed");
        Assert.Single(_db.OrderItems, i => i.ProcessedAt == null && i.ProcessAt <= DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(422, true)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task OnlyARealMollie4xxIsARefusal(int status, bool refused)
    {
        var sub = await SeedDueAsync();
        _mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new Mollie.Api.Client.MollieApiException(
                new Mollie.Api.Models.Error.MollieErrorMessage { Status = status, Title = "t", Detail = "d" }));

        await Engine().ProcessDueItemsAsync();

        Assert.Equal(refused ? SubscriptionStatus.PastDue : SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task APastDueSubscriptionIsNotChargedForLaterCycles()
    {
        await SeedDueAsync(SubscriptionStatus.PastDue);
        ChargesReturn(MolliePayment("tr_cycle", "pending"));

        await Engine().ProcessDueItemsAsync();

        Assert.Equal(0, await ChargeCalls());
    }

    [Fact]
    public async Task ALaterFailureReportedByWebhookMakesItPastDue_AndSettlingRecoversIt()
    {
        var sub = await SeedDueAsync();
        ChargesReturn(MolliePayment("tr_cycle", "pending"));
        await Engine().ProcessDueItemsAsync();

        // Known-positive for the old blind spot: before beta.8 the webhook found no row and returned.
        _mollie.GetPaymentAsync("tr_cycle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_cycle", "failed", "mdt_1"));
        await Webhook().HandlePaymentAsync("tr_cycle");

        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.NotNull(sub.PastDueSince);
        await _events.Received(1).DispatchAsync(Arg.Any<SubscriptionPastDue<string>>(), Arg.Any<CancellationToken>());

        _mollie.CreateFirstPaymentAsync(default!, default, default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(MolliePayment("tr_settle", "open", checkoutUrl: "https://mollie.test/settle")));
        var result = await Engine().PayOutstandingAsync(new TestBillable("u1", "cst_1", "mdt_1"), sub);
        Assert.Equal("https://mollie.test/settle", result.CheckoutUrl);
        Assert.Equal(UnitPrice, result.Amount);

        _mollie.GetPaymentAsync("tr_settle", Arg.Any<CancellationToken>())
            .Returns(MolliePayment("tr_settle", "paid", "mdt_2"));
        await Webhook().HandlePaymentAsync("tr_settle");

        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Null(sub.PastDueSince);
        await _events.Received(1).DispatchAsync(Arg.Any<SubscriptionPaymentRecovered<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTheCancelDayItEndsLocally_AndCallsNothingAtMollie()
    {
        var sub = await SeedDueAsync();
        ChargesAreRefused();
        await Engine().ProcessDueItemsAsync();
        sub.PastDueSince = DateTimeOffset.UtcNow.AddDays(-15);
        await _db.SaveChangesAsync();
        _mollie.ClearReceivedCalls();

        await Dunning().ProcessAsync();

        Assert.NotNull(sub.EndsAt);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);   // the debt stays payable
        Assert.Empty(_mollie.ReceivedCalls());
        await _events.Received(1).DispatchAsync(
            Arg.Any<SubscriptionCancelledForNonPayment<string>>(), Arg.Any<CancellationToken>());
        Assert.False(DunningPolicy.HasAccess(sub, _options.Dunning, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheDunningJobIsRegisteredForTheManagedEngine()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CashierMollie:BillingEngine"] = "Managed",
        }).Build();
        services.AddCashierMollie<string>(config);

        Assert.Contains(services, d => d.ServiceType == typeof(DunningService<string>));
        Assert.Contains(services, d => d.ImplementationType == typeof(DunningBackgroundService<string>));
    }
}
