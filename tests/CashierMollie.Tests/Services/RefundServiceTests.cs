using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Refund.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class RefundServiceTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly RefundService<string> _service;

    public RefundServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions());
        _service = new RefundService<string>(_db, _mollieClient, _dispatcher, options, NullLogger<RefundService<string>>.Instance);
    }

    private static RefundResponse CreateMockRefundResponse(string id, string status)
    {
        var response = Substitute.For<RefundResponse>();
        response.Id = id;
        response.Status = status;
        return response;
    }

    [Fact]
    public async Task RefundAsync_FullAmount_CreatesRefundRecord()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_test", Amount = 10.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_test", "pending"));

        var refund = await _service.RefundAsync(payment, 10.00m);

        Assert.Equal("re_test", refund.MollieRefundId);
        Assert.Equal("pending", refund.Status);
        Assert.Equal(10.00m, refund.Amount);
        Assert.Equal(10.00m, payment.AmountRefunded);
    }

    [Fact]
    public async Task RefundAsync_PartialAmount_CreatesRefundRecord()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_test2", Amount = 20.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_partial", "pending"));

        var refund = await _service.RefundAsync(payment, 5.00m, "partial refund");

        Assert.Equal("re_partial", refund.MollieRefundId);
        Assert.Equal(5.00m, refund.Amount);
        Assert.Equal("partial refund", refund.Description);
        Assert.Equal(5.00m, payment.AmountRefunded);
    }

    [Fact]
    public async Task RefundAsync_DefaultsToFullAmount_WhenAmountIsNull()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_null", Amount = 15.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_null", "pending"));

        var refund = await _service.RefundAsync(payment);

        Assert.Equal(15.00m, refund.Amount);
        Assert.Equal(15.00m, payment.AmountRefunded);
    }

    [Fact]
    public async Task RefundAsync_DispatchesRefundInitiatedEvent()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_event", Amount = 10.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_evt", "pending"));

        await _service.RefundAsync(payment, 10.00m);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Events.RefundInitiated<string>>(e =>
                e.Amount == 10.00m && e.Currency == "EUR" && e.OwnerId == "u1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefundAsync_CallsMollieWithCorrectParameters()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_mollie", Amount = 10.00m, Currency = "USD", Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_mollie", "pending"));

        await _service.RefundAsync(payment, 7.50m, "test refund");

        await _mollieClient.Received(1).CreateRefundAsync(
            "tr_mollie", 7.50m, "USD", "test refund", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefundCompletelyAsync_RefundsFullAmount()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_full", Amount = 25.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_full", "pending"));

        var refund = await _service.RefundCompletelyAsync(payment);

        Assert.Equal(25.00m, refund.Amount);
    }

    [Fact]
    public async Task GetRefundAsync_ReturnsRefundById()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_get", Amount = 10.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var refund = new Refund<string>
        {
            OwnerId = "u1", PaymentId = payment.Id, MollieRefundId = "re_get",
            Status = "pending", Amount = 10.00m,
        };
        _db.Refunds.Add(refund);
        await _db.SaveChangesAsync();

        var result = await _service.GetRefundAsync(refund.Id);

        Assert.NotNull(result);
        Assert.Equal("re_get", result.MollieRefundId);
    }

    [Fact]
    public async Task GetRefundAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _service.GetRefundAsync(999);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRefundsForPaymentAsync_ReturnsAllRefunds()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_list", Amount = 20.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _db.Refunds.AddRange(
            new Refund<string>
            {
                OwnerId = "u1", PaymentId = payment.Id, MollieRefundId = "re_1",
                Status = "pending", Amount = 5.00m,
            },
            new Refund<string>
            {
                OwnerId = "u1", PaymentId = payment.Id, MollieRefundId = "re_2",
                Status = "refunded", Amount = 3.00m,
            });
        await _db.SaveChangesAsync();

        var refunds = await _service.GetRefundsForPaymentAsync(payment.Id);

        Assert.Equal(2, refunds.Count);
    }

    [Fact]
    public async Task GetRefundsForPaymentAsync_ReturnsEmpty_WhenNoRefunds()
    {
        var refunds = await _service.GetRefundsForPaymentAsync(999);
        Assert.Empty(refunds);
    }

    [Fact]
    public async Task RefundAsync_PersistsRefundToDatabase()
    {
        var payment = new Payment<string>
        {
            OwnerId = "u1", MolliePaymentId = "tr_persist", Amount = 10.00m, Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        _mollieClient.CreateRefundAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_persist", "pending"));

        var refund = await _service.RefundAsync(payment, 10.00m);

        var saved = await _db.Refunds.FindAsync(refund.Id);
        Assert.NotNull(saved);
        Assert.Equal("re_persist", saved.MollieRefundId);
        Assert.Equal("u1", saved.OwnerId);
        Assert.Equal(payment.Id, saved.PaymentId);
    }

    public void Dispose() => _db.Dispose();
}
