using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Service for creating and managing refunds.</summary>
public interface IRefundService<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Creates a refund for a payment. Defaults to full amount if amount is null.</summary>
    Task<Refund<TKey>> RefundAsync(Payment<TKey> payment, decimal? amount = null,
        string? description = null, CancellationToken ct = default);

    /// <summary>Refunds the complete payment amount.</summary>
    Task<Refund<TKey>> RefundCompletelyAsync(Payment<TKey> payment, CancellationToken ct = default);

    /// <summary>Gets a refund by its local ID.</summary>
    Task<Refund<TKey>?> GetRefundAsync(long refundId, CancellationToken ct = default);

    /// <summary>Gets all refunds for a payment.</summary>
    Task<List<Refund<TKey>>> GetRefundsForPaymentAsync(long paymentId, CancellationToken ct = default);
}
