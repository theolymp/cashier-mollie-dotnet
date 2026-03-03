using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

public class OrderItem
{
    [Key]
    public long Id { get; set; }

    public long SubscriptionId { get; set; }

    [Required]
    public string OwnerId { get; set; } = default!;

    [Required]
    [MaxLength(255)]
    public string Description { get; set; } = default!;

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; } = 1;

    public decimal TaxPercentage { get; set; }

    [MaxLength(255)]
    public string? MolliePaymentId { get; set; }

    [MaxLength(50)]
    public string? MolliePaymentStatus { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Subscription Subscription { get; set; } = default!;

    // Computed
    public decimal Total => UnitPrice * Quantity;

    public decimal TaxAmount => Total * (TaxPercentage / 100m);

    public decimal TotalWithTax => Total + TaxAmount;
}
