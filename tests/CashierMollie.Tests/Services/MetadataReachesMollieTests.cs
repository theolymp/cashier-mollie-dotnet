using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Subscription.Request;
using Mollie.Api.Models.Subscription.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

/// <summary>
/// Metadata used to be stored in a field nothing read. These tests assert it on the REQUEST OBJECT
/// handed to Mollie.Api — the last point this library controls, and the strongest proof available
/// without a live key. A run against Mollie's test mode is still outstanding and is not simulated
/// here: the key is held in the secret store, and a test that pretended to make the call would be
/// worth less than the gap it hides.
///
/// Known-negatives matter as much as the positives: an absent dictionary must send NO metadata
/// field, not an empty JSON object. "{}" is a value, and a value that means nothing is what someone
/// later reads as intent.
/// </summary>
public class MetadataReachesMollieTests : IDisposable
{
    private readonly IPaymentClient _payments = Substitute.For<IPaymentClient>();
    private readonly ISubscriptionClient _subscriptions = Substitute.For<ISubscriptionClient>();
    private readonly IMollieClientService _client;
    private readonly CashierDbContext<string> _db;

    public MetadataReachesMollieTests()
    {
        _db = TestDbContextFactory.Create();
        _client = new MollieClientService(
            Substitute.For<ICustomerClient>(), _payments, _subscriptions,
            Substitute.For<IMandateClient>(), Substitute.For<IRefundClient>());

        _payments.CreatePaymentAsync(Arg.Any<PaymentRequest>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Substitute.For<PaymentResponse>());
        _subscriptions.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<SubscriptionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Substitute.For<SubscriptionResponse>());
    }

    private PaymentRequest CapturedPayment() =>
        (PaymentRequest)_payments.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPaymentClient.CreatePaymentAsync))
            .GetArguments()[0]!;

    private static readonly Dictionary<string, string> Two = new()
    {
        ["tenant"] = "acme",
        ["plan"] = "pro",
    };

    [Fact]
    public async Task FirstPayment_CarriesTheMetadataAsJson()
    {
        await _client.CreateFirstPaymentAsync("cst_1", 1m, "EUR", "d", "https://r.test",
            "https://w.test", Two);

        string? json = CapturedPayment().Metadata;
        Assert.NotNull(json);
        Assert.Contains("\"tenant\":\"acme\"", json, StringComparison.Ordinal);
        Assert.Contains("\"plan\":\"pro\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecurringPayment_CarriesTheMetadataAsJson()
    {
        await _client.CreateRecurringPaymentAsync("cst_1", "mdt_1", 1m, "EUR", "d",
            "https://w.test", Two);

        Assert.Contains("\"tenant\":\"acme\"", CapturedPayment().Metadata!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MollieSubscription_CarriesTheMetadataAsJson()
    {
        await _client.CreateSubscriptionAsync("cst_1", 1m, "EUR", "1 month", "d",
            "https://w.test", "mdt_1", Two);

        var request = (SubscriptionRequest)_subscriptions.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ISubscriptionClient.CreateSubscriptionAsync))
            .GetArguments()[1]!;
        Assert.Contains("\"tenant\":\"acme\"", request.Metadata!, StringComparison.Ordinal);
    }

    // --- known-negatives: absent means absent, not empty -------------------

    [Fact]
    public async Task NoMetadata_SendsNoMetadataField()
    {
        await _client.CreateFirstPaymentAsync("cst_1", 1m, "EUR", "d", "https://r.test",
            "https://w.test", metadata: null);

        Assert.Null(CapturedPayment().Metadata);
    }

    [Fact]
    public async Task AnEmptyDictionary_SendsNoMetadataField()
    {
        await _client.CreateFirstPaymentAsync("cst_1", 1m, "EUR", "d", "https://r.test",
            "https://w.test", new Dictionary<string, string>());

        Assert.Null(CapturedPayment().Metadata);
    }

    // --- and the whole way through, from the consumer's call ---------------

    [Fact]
    public async Task WithMetadata_OnTheSubscriptionBuilder_ReachesTheFirstPayment()
    {
        var mollie = Substitute.For<IMollieClientService>();
        var created = Substitute.For<PaymentResponse>();
        created.Id = "tr_first";          // required on the local Payment row
        created.Status = "open";
        mollie.CreateFirstPaymentAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(created);
        var dispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = new CashierMollieOptions { Currency = "EUR" };
        var engine = new MollieBillingEngine<string>(_db, mollie, dispatcher,
            Options.Create(options),
            new CouponService<string>(_db, Substitute.For<ICouponRepository>(), dispatcher),
            NullLogger<MollieBillingEngine<string>>.Instance);

        // No mandate on this owner, so the first-payment flow runs.
        var owner = new TestBillable("u1", "cst_test");
        var builder = new SubscriptionBuilder<string>(_db, engine, mollie, dispatcher, options,
            owner, "default", "pro");

        await builder.WithMetadata(new Dictionary<string, string> { ["tenant"] = "acme" })
            .CreateAsync();

        await mollie.Received(1).CreateFirstPaymentAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m => m != null && m["tenant"] == "acme"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithMetadata_OnAOneOffCharge_ReachesThePayment()
    {
        var mollie = Substitute.For<IMollieClientService>();
        var charged = Substitute.For<PaymentResponse>();
        charged.Id = "tr_oneoff";
        charged.Status = "paid";
        mollie.CreateRecurringPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(charged);
        var owner = new TestBillable("u1", "cst_test", "mdt_test");
        var builder = new ChargeBuilder<string>(_db, mollie, Substitute.For<ICashierEventDispatcher>(),
            new CashierMollieOptions { Currency = "EUR" }, owner, 10m);

        await builder.WithDescription("one-off").WithMetadata(
            new Dictionary<string, string> { ["invoice"] = "2026-1" }).CreateAsync();

        await mollie.Received(1).CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m => m != null && m["invoice"] == "2026-1"),
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
