using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

public class Subscription<TKey> where TKey : IEquatable<TKey>
{
    [Key]
    public long Id { get; set; }

    [Required]
    public TKey OwnerId { get; set; } = default!;

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = "default";

    [Required]
    [MaxLength(255)]
    public string Plan { get; set; } = default!;

    [MaxLength(255)]
    public string? MollieSubscriptionId { get; set; }

    [MaxLength(255)]
    public string? MollieCustomerId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = SubscriptionStatus.Active;

    public decimal? Quantity { get; set; }

    public DateTimeOffset? TrialEndsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset? CycleStartedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<OrderItem<TKey>> OrderItems { get; set; } = new List<OrderItem<TKey>>();

    // Computed
    public bool IsActive() =>
        Status == SubscriptionStatus.Active && !IsEnded();

    public bool IsCancelled() =>
        EndsAt.HasValue;

    public bool OnGracePeriod() =>
        IsCancelled() && EndsAt > DateTimeOffset.UtcNow;

    public bool OnTrial() =>
        TrialEndsAt.HasValue && TrialEndsAt > DateTimeOffset.UtcNow;

    public bool IsEnded() =>
        EndsAt.HasValue && EndsAt <= DateTimeOffset.UtcNow;
}

public static class SubscriptionStatus
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string PastDue = "past_due";
    public const string Pending = "pending";
    public const string Paused = "paused";
}
