using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

/// <summary>
/// Represents a payment tracked locally, linked to a Mollie payment.
/// Mapped to the "cashier_payments" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class Payment<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>Optional foreign key to the associated subscription.</summary>
    public long? SubscriptionId { get; set; }

    /// <summary>The Mollie payment ID (e.g. "tr_xxx"). Unique index.</summary>
    [Required]
    [MaxLength(255)]
    public string MolliePaymentId { get; set; } = default!;

    /// <summary>Payment status (e.g. "open", "paid", "failed", "canceled", "expired").</summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "open";

    /// <summary>ISO 4217 currency code (e.g. "EUR").</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>Payment amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>The mandate ID acquired from this payment (set after successful first payment).</summary>
    [MaxLength(255)]
    public string? MollieMandateId { get; set; }

    /// <summary>The payment method used (e.g. "ideal", "creditcard").</summary>
    [MaxLength(50)]
    public string? Method { get; set; }

    /// <summary>When the payment was confirmed as paid.</summary>
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>When the payment failed.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Navigation property to the associated subscription (optional).</summary>
    public Subscription<TKey>? Subscription { get; set; }
}
