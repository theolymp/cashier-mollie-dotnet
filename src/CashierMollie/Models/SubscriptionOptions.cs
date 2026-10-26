namespace CashierMollie.Models;

/// <summary>Options for subscription creation, passed from builder to engine.</summary>
public record SubscriptionOptions
{
    /// <summary>Number of trial days before billing starts.</summary>
    public int TrialDays { get; init; }

    /// <summary>
    /// The coupon code to apply to this subscription. Both engines now read it: the code is
    /// validated and the redemption recorded when the subscription is created, and an unknown code
    /// fails the create instead of being ignored.
    /// <para>
    /// On the Managed engine the redemption then reduces each recurring charge until its cycles are
    /// used up. On the default (MollieNative) engine it is recorded and reduces nothing, because
    /// that engine registers no recurring charge with Mollie at all — it logs a warning saying so.
    /// </para>
    /// </summary>
    public string? CouponCode { get; init; }

    /// <summary>If true, creates a mandate-only authorization (no redirect).</summary>
    public bool MandateOnly { get; init; }

    /// <summary>If true, enables proration on plan swaps.</summary>
    public bool Prorate { get; init; }

    /// <summary>
    /// Custom metadata attached to the first payment Mollie creates for this subscription, stored
    /// at Mollie as JSON and returned whenever that payment is fetched.
    /// <para>
    /// Scope, precisely: the FIRST payment only. Recurring charges are created from order items,
    /// which have no metadata column, so nothing carries it to them — that needs a schema change
    /// and is listed in BACKLOG.md.
    /// </para>
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Number of units for quantity-based billing.</summary>
    public int Quantity { get; init; } = 1;
}
