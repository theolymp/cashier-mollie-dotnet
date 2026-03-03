using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

/// <summary>
/// Represents an order aggregate that groups order items and tracks payment.
/// Mapped to the "cashier_orders" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class Order<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>Human-readable order number (e.g. "ORD-000042").</summary>
    [MaxLength(50)]
    public string? Number { get; set; }

    /// <summary>ISO 4217 currency code (e.g. "EUR").</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>Subtotal before tax.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Tax amount.</summary>
    public decimal Tax { get; set; }

    /// <summary>Total amount (subtotal + tax).</summary>
    public decimal Total { get; set; }

    /// <summary>Amount still due after credit has been applied.</summary>
    public decimal TotalDue { get; set; }

    /// <summary>Amount of credit used to (partially) pay this order.</summary>
    public decimal CreditUsed { get; set; }

    /// <summary>The Mollie payment ID associated with this order (e.g. "tr_xxx").</summary>
    [MaxLength(255)]
    public string? MolliePaymentId { get; set; }

    /// <summary>Status of the associated Mollie payment (e.g. "paid", "open", "failed").</summary>
    [MaxLength(50)]
    public string? MolliePaymentStatus { get; set; }

    /// <summary>When this order was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this order was processed (payment confirmed).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Line items belonging to this order.</summary>
    public ICollection<OrderItem<TKey>> Items { get; set; } = new List<OrderItem<TKey>>();
}
