using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

public class Payment<TKey> where TKey : IEquatable<TKey>
{
    [Key]
    public long Id { get; set; }

    [Required]
    public TKey OwnerId { get; set; } = default!;

    public long? SubscriptionId { get; set; }

    [Required]
    [MaxLength(255)]
    public string MolliePaymentId { get; set; } = default!;

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "open";

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    public decimal Amount { get; set; }

    [MaxLength(255)]
    public string? MollieMandateId { get; set; }

    [MaxLength(50)]
    public string? Method { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Subscription<TKey>? Subscription { get; set; }
}
