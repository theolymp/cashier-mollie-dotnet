using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CashierMollie.Models;

/// <summary>
/// Represents a line item in a subscription order.
/// Mapped to the "cashier_order_items" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class OrderItem<TKey> : IHasTimestamps where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the subscription.</summary>
    public long SubscriptionId { get; set; }

    /// <summary>Foreign key to the parent order (null for legacy items without an order).</summary>
    public long? OrderId { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>Line item description.</summary>
    [Required]
    [MaxLength(255)]
    public string Description { get; set; } = default!;

    /// <summary>ISO 4217 currency code (e.g. "EUR").</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>Price per unit.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Number of units.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Tax rate as a percentage (e.g. 19.0 for 19% VAT).</summary>
    public decimal TaxPercentage { get; set; }

    /// <summary>
    /// Discount actually subtracted when this item was charged, in the item's currency.
    /// </summary>
    /// <remarks>
    /// Written by the <c>Managed</c> engine when it charges the item, so the charged amount is
    /// explainable from the row alone: it is <c>UnitPrice * Quantity</c> minus this. Until 0.5.0
    /// the column existed and no code in the library read or wrote it, which made a discounted
    /// charge traceable only through the Mollie payment and a log line. Zero on an unprocessed
    /// item means "not charged yet", not "no discount".
    /// </remarks>
    public decimal Discount { get; set; }

    /// <summary>
    /// Metadata to attach to every charge created from this item, as the JSON Mollie stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what carries a subscription's metadata into its later billing cycles. Recurring
    /// charges are created from order items, so before 0.5.0 the dictionary reached the first
    /// payment and nothing after it.
    /// </para>
    /// <para>
    /// It holds the serialised form rather than a dictionary on purpose: the column then contains
    /// exactly the string the payment carries, and there is no second representation to drift.
    /// Only this library writes it. Nothing downstream parses it except the charge that sends it,
    /// and a value that cannot be read back is reported rather than silently dropped.
    /// </para>
    /// </remarks>
    public string? Metadata { get; set; }

    /// <summary>Associated Mollie payment ID.</summary>
    [MaxLength(255)]
    public string? MolliePaymentId { get; set; }

    /// <summary>Status of the associated Mollie payment.</summary>
    [MaxLength(50)]
    public string? MolliePaymentStatus { get; set; }

    /// <summary>When this item was processed.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>When this item should be processed (scheduled billing date).</summary>
    public DateTimeOffset? ProcessAt { get; set; }

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Navigation property to the parent subscription.</summary>
    public Subscription<TKey> Subscription { get; set; } = default!;

    /// <summary>Navigation property to the parent order (optional).</summary>
    public Order<TKey>? Order { get; set; }

    /// <summary>Total before tax (UnitPrice * Quantity).</summary>
    [NotMapped]
    public decimal Total => UnitPrice * Quantity;

    /// <summary>Tax amount based on the total and tax percentage.</summary>
    [NotMapped]
    public decimal TaxAmount => Total * (TaxPercentage / 100m);

    /// <summary>Total including tax.</summary>
    [NotMapped]
    public decimal TotalWithTax => Total + TaxAmount;
}
