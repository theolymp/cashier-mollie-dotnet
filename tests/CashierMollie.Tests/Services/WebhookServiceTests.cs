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

    public void Dispose() => _db.Dispose();
}
