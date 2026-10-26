namespace CashierMollie.Models;

/// <summary>Options for subscription creation, passed from builder to engine.</summary>
public record SubscriptionOptions
{
    /// <summary>Number of trial days before billing starts.</summary>
    public int TrialDays { get; init; }

    /// <summary>
    /// NOT READ BY THIS LIBRARY — set by <c>WithCoupon()</c> and read by nothing. Creating a
    /// subscription with a coupon code neither validates it, nor records a redemption, nor
    /// reduces any amount: both billing engines ignore this field, so the subscription is created
    /// at full price with no error and no event. The coupon system is reachable only through
    /// <c>ICouponService</c>, which you call yourself.
    /// Intended meaning: the coupon code to apply to this subscription.
    /// </summary>
    public string? CouponCode { get; init; }

    /// <summary>If true, creates a mandate-only authorization (no redirect).</summary>
    public bool MandateOnly { get; init; }

    /// <summary>If true, enables proration on plan swaps.</summary>
    public bool Prorate { get; init; }

    /// <summary>
    /// NOT READ BY THIS LIBRARY — set by <c>WithMetadata()</c> and read by nothing. No request
    /// this library sends to Mollie carries metadata, so a value here reaches neither Mollie nor
    /// any local row.
    /// Intended meaning: custom metadata to attach to the subscription.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Number of units for quantity-based billing.</summary>
    public int Quantity { get; init; } = 1;
}
