using System.ComponentModel.DataAnnotations;

namespace CashierMollie.Models;

/// <summary>
/// Tracks a coupon that has been redeemed by a billable owner.
/// Mapped to the "cashier_redeemed_coupons" table.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class RedeemedCoupon<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Auto-increment primary key.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Foreign key to the billable owner.</summary>
    [Required]
    public TKey OwnerId { get; set; } = default!;

    /// <summary>The coupon code that was redeemed.</summary>
    [Required]
    [MaxLength(100)]
    public string Code { get; set; } = default!;

    /// <summary>Name of the subscription this coupon is applied to, or null if global.</summary>
    [MaxLength(255)]
    public string? SubscriptionName { get; set; }

    /// <summary>Number of billing cycles remaining for this coupon (0 = exhausted).</summary>
    public int TimesLeft { get; set; }

    /// <summary>When this coupon was redeemed.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
