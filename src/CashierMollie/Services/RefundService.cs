using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>Manages refund creation and tracking via Mollie.</summary>
public class RefundService<TKey> : IRefundService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CashierMollieOptions _options;
    private readonly ILogger<RefundService<TKey>> _logger;

    /// <summary>Creates a new RefundService.</summary>
    public RefundService(CashierDbContext<TKey> db, IMollieClientService mollieClient,
        ICashierEventDispatcher dispatcher, IOptions<CashierMollieOptions> options,
        ILogger<RefundService<TKey>> logger)
    {
        _db = db;
        _mollieClient = mollieClient;
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Refund<TKey>> RefundAsync(Payment<TKey> payment, decimal? amount = null,
        string? description = null, CancellationToken ct = default)
    {
        decimal refundAmount = amount ?? payment.Amount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refundAmount);
        if (payment.AmountRefunded + refundAmount > payment.Amount)
            throw new CashierException(
                $"Refund amount {refundAmount} exceeds maximum refundable amount {payment.Amount - payment.AmountRefunded}.");

        string currency = payment.Currency;

        var mollieRefund = await _mollieClient.CreateRefundAsync(
            payment.MolliePaymentId, refundAmount, currency, description, ct);

        var refund = new Refund<TKey>
        {
            OwnerId = payment.OwnerId,
            PaymentId = payment.Id,
            MollieRefundId = mollieRefund.Id,
            Status = mollieRefund.Status ?? "pending",
            Amount = refundAmount,
            Currency = currency,
            Description = description,
        };
        _db.Refunds.Add(refund);

        payment.AmountRefunded += refundAmount;
        payment.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(
            new RefundInitiated<TKey>(payment, refundAmount, currency, payment.OwnerId), ct);

        return refund;
    }

    /// <inheritdoc />
    public Task<Refund<TKey>> RefundCompletelyAsync(Payment<TKey> payment, CancellationToken ct = default)
        => RefundAsync(payment, payment.Amount, null, ct);

    /// <inheritdoc />
    public async Task<Refund<TKey>?> GetRefundAsync(long refundId, CancellationToken ct = default)
        => await _db.Refunds.FindAsync(new object[] { refundId }, ct);

    /// <inheritdoc />
    public async Task<List<Refund<TKey>>> GetRefundsForPaymentAsync(long paymentId, CancellationToken ct = default)
        => await _db.Refunds.Where(r => r.PaymentId == paymentId).ToListAsync(ct);
}
