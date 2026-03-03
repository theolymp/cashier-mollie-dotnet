using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Refund.Response;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

public class RefundIntegrationTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly RefundService<string> _refundService;

    public RefundIntegrationTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
        });
        _refundService = new RefundService<string>(_db, _mollieClient, _eventDispatcher, options);
    }

    private static RefundResponse CreateMockRefundResponse(string id, string status)
    {
        var response = Substitute.For<RefundResponse>();
        response.Id = id;
        response.Status = status;
        return response;
    }

    [Fact]
    public async Task RefundPayment_CreatesLocalRefund()
    {
        // Setup: Create a paid payment in the database
        var payment = new Payment<string>
        {
            OwnerId = "refund-user-1",
            MolliePaymentId = "tr_refund_test",
            Amount = 29.99m,
            Currency = "EUR",
            Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        // Mock Mollie refund API
        _mollieClient.CreateRefundAsync(
            "tr_refund_test", 10.00m, "EUR",
            "Partial refund", Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_partial", "pending"));

        // Execute: Refund 10 EUR
        var refund = await _refundService.RefundAsync(payment, 10.00m, "Partial refund");

        // Verify: Refund record created
        Assert.NotNull(refund);
        Assert.Equal("re_partial", refund.MollieRefundId);
        Assert.Equal(10.00m, refund.Amount);
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal("pending", refund.Status);
        Assert.Equal("Partial refund", refund.Description);
        Assert.Equal("refund-user-1", refund.OwnerId);

        // Verify: Refund persisted in database
        var savedRefund = await _db.Refunds
            .FirstOrDefaultAsync(r => r.MollieRefundId == "re_partial");
        Assert.NotNull(savedRefund);
        Assert.Equal(payment.Id, savedRefund.PaymentId);

        // Verify: Payment's AmountRefunded updated
        var updatedPayment = await _db.Payments.FindAsync(payment.Id);
        Assert.Equal(10.00m, updatedPayment!.AmountRefunded);

        // Verify: RefundInitiated event dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<RefundInitiated<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefundCompletely_RefundsFullAmount()
    {
        // Setup: Create a paid payment
        var payment = new Payment<string>
        {
            OwnerId = "refund-user-2",
            MolliePaymentId = "tr_full_refund",
            Amount = 49.99m,
            Currency = "EUR",
            Status = "paid",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        // Mock Mollie: expect full amount refund
        _mollieClient.CreateRefundAsync(
            "tr_full_refund", 49.99m, "EUR",
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CreateMockRefundResponse("re_full", "pending"));

        // Execute: Full refund
        var refund = await _refundService.RefundCompletelyAsync(payment);

        // Verify: Full amount refunded
        Assert.Equal(49.99m, refund.Amount);
        Assert.Equal("re_full", refund.MollieRefundId);

        // Verify: Payment's AmountRefunded matches original amount
        var updatedPayment = await _db.Payments.FindAsync(payment.Id);
        Assert.Equal(49.99m, updatedPayment!.AmountRefunded);

        // Verify: Refund can be retrieved by payment
        var refunds = await _refundService.GetRefundsForPaymentAsync(payment.Id);
        Assert.Single(refunds);
        Assert.Equal(49.99m, refunds[0].Amount);
    }

    public void Dispose() => _db.Dispose();
}
