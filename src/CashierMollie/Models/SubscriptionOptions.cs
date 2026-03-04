namespace CashierMollie.Models;

/// <summary>Options for subscription creation, passed from builder to engine.</summary>
public record SubscriptionOptions
{
    /// <summary>Number of trial days before billing starts.</summary>
    public int TrialDays { get; init; }

    /// <summary>Coupon code to apply.</summary>
    public string? CouponCode { get; init; }

    /// <summary>If true, creates a mandate-only authorization (no redirect).</summary>
    public bool MandateOnly { get; init; }

    /// <summary>If true, enables proration on plan swaps.</summary>
    public bool Prorate { get; init; }

    /// <summary>Custom metadata to attach to the subscription.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Number of units for quantity-based billing.</summary>
    public int Quantity { get; init; } = 1;
}
