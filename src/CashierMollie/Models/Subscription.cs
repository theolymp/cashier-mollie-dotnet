using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

/// <summary>
/// Represents a subscription owned by a billable entity.
/// Mapped to the "cashier_subscriptions" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class Subscription<TKey> : IHasTimestamps, IHasConcurrencyToken where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>
    /// The subscription <i>slot</i> for this owner -- not a display name. It exists to let one owner
    /// hold several concurrent subscriptions side by side, and is what you pass to
    /// <c>Subscribed(name)</c> to ask about a specific one. Defaults to <c>"default"</c>.
    /// </summary>
    /// <remarks>
    /// Consumers typically map their <b>product</b> here and the <b>tier</b> to <see cref="Plan"/>
    /// (e.g. <c>Name = "modcockpit"</c>, <c>Plan = "pro-monthly"</c>), which matches the intent:
    /// one subscription per product per owner, priced by plan. Pick a stable machine identifier --
    /// this value is matched literally, so changing it orphans the existing subscription.
    /// </remarks>
    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = "default";

    /// <summary>
    /// The priced plan this subscription runs on (e.g. <c>"pro-monthly"</c>) -- the "what is being
    /// charged", as opposed to <see cref="Name"/>, which is the "which of my subscriptions".
    /// Changing it is what <c>Swap()</c> does.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Plan { get; set; } = default!;

    /// <summary>The next plan to swap to at the end of the current billing cycle (null if no swap pending).</summary>
    [MaxLength(255)]
    public string? NextPlan { get; set; }

    /// <summary>The Mollie subscription ID (e.g. "sub_xxx"), set after the subscription is created on Mollie.</summary>
    [MaxLength(255)]
    public string? MollieSubscriptionId { get; set; }

    /// <summary>The Mollie customer ID associated with this subscription.</summary>
    [MaxLength(255)]
    public string? MollieCustomerId { get; set; }

    /// <summary>Current status: active, cancelled, past_due, pending, or paused. See <see cref="SubscriptionStatus"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = SubscriptionStatus.Active;

    /// <summary>Subscription quantity (for quantity-based billing).</summary>
    public int? Quantity { get; set; }

    /// <summary>Optimistic concurrency token, incremented on each update.</summary>
    [ConcurrencyCheck]
    public uint RowVersion { get; set; }

    /// <summary>When the trial period ends, or null if no trial.</summary>
    public DateTimeOffset? TrialEndsAt { get; set; }

    /// <summary>When the subscription ends (set on cancellation), or null if active indefinitely.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>When the current billing cycle started.</summary>
    public DateTimeOffset? CycleStartedAt { get; set; }

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Order items associated with this subscription.</summary>
    public ICollection<OrderItem<TKey>> OrderItems { get; set; } = new List<OrderItem<TKey>>();

    /// <summary>Returns true if the subscription is active and has not ended.</summary>
    public bool IsActive() =>
        Status == SubscriptionStatus.Active && !IsEnded();

    /// <summary>Returns true if the subscription has an end date set (i.e. has been cancelled).</summary>
    public bool IsCancelled() =>
        EndsAt.HasValue;

    /// <summary>Returns true if the subscription is cancelled but the end date is still in the future.</summary>
    public bool OnGracePeriod() =>
        IsCancelled() && EndsAt > DateTimeOffset.UtcNow;

    /// <summary>Returns true if the subscription is currently within its trial period.</summary>
    public bool OnTrial() =>
        TrialEndsAt.HasValue && TrialEndsAt > DateTimeOffset.UtcNow;

    /// <summary>Returns true if the subscription's end date has passed.</summary>
    public bool IsEnded() =>
        EndsAt.HasValue && EndsAt <= DateTimeOffset.UtcNow;
}

/// <summary>
/// String constants for subscription statuses stored in the database.
/// </summary>
public static class SubscriptionStatus
{
    /// <summary>The subscription is active and billing normally.</summary>
    public const string Active = "active";
    /// <summary>The subscription has been cancelled (may still be in grace period).</summary>
    public const string Cancelled = "cancelled";
    /// <summary>The subscription payment is past due.</summary>
    public const string PastDue = "past_due";
    /// <summary>The subscription is pending (awaiting first payment / mandate creation).</summary>
    public const string Pending = "pending";
    /// <summary>The subscription is paused.</summary>
    public const string Paused = "paused";
}
