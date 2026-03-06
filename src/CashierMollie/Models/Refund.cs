using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

/// <summary>
/// Tracks a Mollie refund linked to a local payment.
/// Mapped to the "cashier_refunds" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class Refund<TKey> : IHasTimestamps where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>Foreign key to the associated local payment.</summary>
    public long PaymentId { get; set; }

    /// <summary>The Mollie refund ID (e.g. "re_xxx").</summary>
    [Required]
    [MaxLength(255)]
    public string MollieRefundId { get; set; } = default!;

    /// <summary>Refund status (e.g. "pending", "processing", "refunded", "failed").</summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    /// <summary>Refund amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code (e.g. "EUR").</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>Optional description for the refund reason.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Navigation property to the associated payment.</summary>
    public Payment<TKey> Payment { get; set; } = default!;
}
