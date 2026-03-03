using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class WebhookServiceTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly WebhookService<string> _sut;

    public WebhookServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        _sut = new WebhookService<string>(_db, _mollieClient, _eventDispatcher);
    }

    private static PaymentResponse MockPaymentResponse(string id, string status, string? mandateId = null)
    {
        var response = Substitute.For<PaymentResponse>();
        response.Id = id;
        response.Status = status;
        response.MandateId = mandateId;
        return response;
    }

    [Fact]
    public async Task HandlePaymentAsync_PaidPayment_UpdatesLocalRecord()
    {
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_test",
            Status = "open",
            Amount = 9.99m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_test", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_test", "paid", "mdt_test"));

        await _sut.HandlePaymentAsync("tr_test");

        var updated = await _db.Payments.FindAsync(localPayment.Id);
        Assert.Equal("paid", updated!.Status);
        Assert.Equal("mdt_test", updated.MollieMandateId);
        Assert.NotNull(updated.PaidAt);
    }

    [Fact]
    public async Task HandlePaymentAsync_FailedPayment_SetsFailedAt()
    {
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_fail",
            Status = "open",
            Amount = 9.99m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_fail", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_fail", "failed"));

        await _sut.HandlePaymentAsync("tr_fail");

        var updated = await _db.Payments.FindAsync(localPayment.Id);
        Assert.Equal("failed", updated!.Status);
        Assert.NotNull(updated.FailedAt);
    }

    [Fact]
    public async Task HandlePaymentAsync_PaidPayment_DispatchesEvent()
    {
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_evt",
            Status = "open",
            Amount = 9.99m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_evt", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_evt", "paid"));

        await _sut.HandlePaymentAsync("tr_evt");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<OrderPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_FailedPayment_DispatchesFailedEvent()
    {
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_fail_evt",
            Status = "open",
            Amount = 9.99m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_fail_evt", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_fail_evt", "failed"));

        await _sut.HandlePaymentAsync("tr_fail_evt");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<OrderPaymentFailed<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_UnknownPayment_DoesNothing()
    {
        _mollieClient.GetPaymentAsync("tr_unknown", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_unknown", "paid"));

        // Should not throw
        await _sut.HandlePaymentAsync("tr_unknown");

        await _eventDispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<OrderPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_WithChargeback_DispatchesChargebackEvent()
    {
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_cb",
            Status = "paid",
            Amount = 10.00m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        var response = MockPaymentResponse("tr_cb", "paid");
        response.AmountChargedBack = new Mollie.Api.Models.Amount(Mollie.Api.Models.Currency.EUR, 5.00m);
        _mollieClient.GetPaymentAsync("tr_cb", Arg.Any<CancellationToken>()).Returns(response);

        await _sut.HandlePaymentAsync("tr_cb");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<ChargebackReceived<string>>(), Arg.Any<CancellationToken>());

        var updated = await _db.Payments.FindAsync(localPayment.Id);
        Assert.Equal(5.00m, updated!.AmountChargedBack);
    }

    [Fact]
    public async Task HandlePaymentAsync_WithChargeback_NoNewChargeback_DoesNotDispatch()
    {
        // Payment already has 5.00 charged back, Mollie reports same amount
        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_cb_same",
            Status = "paid",
            Amount = 10.00m,
            AmountChargedBack = 5.00m,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        var response = MockPaymentResponse("tr_cb_same", "paid");
        response.AmountChargedBack = new Mollie.Api.Models.Amount(Mollie.Api.Models.Currency.EUR, 5.00m);
        _mollieClient.GetPaymentAsync("tr_cb_same", Arg.Any<CancellationToken>()).Returns(response);

        await _sut.HandlePaymentAsync("tr_cb_same");

        await _eventDispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<ChargebackReceived<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_PaidPayment_DispatchesFirstPaymentPaid_WhenPending()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Pending,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_first",
            Status = "open",
            Amount = 0.01m,
            SubscriptionId = sub.Id,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_first", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_first", "paid", "mdt_test"));

        await _sut.HandlePaymentAsync("tr_first");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<FirstPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_FailedPayment_DispatchesFirstPaymentFailed_WhenPending()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Pending,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_fail_first",
            Status = "open",
            Amount = 0.01m,
            SubscriptionId = sub.Id,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_fail_first", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_fail_first", "failed"));

        await _sut.HandlePaymentAsync("tr_fail_first");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<FirstPaymentFailed<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandlePaymentAsync_PaidPayment_DoesNotDispatchFirstPaymentPaid_WhenAlreadyActive()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var localPayment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_active",
            Status = "open",
            Amount = 9.99m,
            SubscriptionId = sub.Id,
        };
        _db.Payments.Add(localPayment);
        await _db.SaveChangesAsync();

        _mollieClient.GetPaymentAsync("tr_active", Arg.Any<CancellationToken>())
            .Returns(MockPaymentResponse("tr_active", "paid"));

        await _sut.HandlePaymentAsync("tr_active");

        await _eventDispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<FirstPaymentPaid<string>>(), Arg.Any<CancellationToken>());
    }

    public void Dispose() => _db.Dispose();
}
