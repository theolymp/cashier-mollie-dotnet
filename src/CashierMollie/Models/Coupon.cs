namespace CashierMollie.Models;

/// <summary>
/// Defines a coupon with its discount handler and usage limits.
/// This is a configuration object, not a database entity.
/// </summary>
public class Coupon
{
    /// <summary>The unique coupon code (e.g. "LAUNCH10").</summary>
    public string Code { get; set; } = default!;

    /// <summary>
    /// The type of discount handler (e.g. "percentage", "fixed_amount").
    /// Used to resolve the appropriate coupon handler at runtime.
    /// </summary>
    public string HandlerType { get; set; } = "percentage";

    /// <summary>Number of billing cycles this coupon applies to.</summary>
    public int Times { get; set; }

    /// <summary>
    /// Handler-specific context (e.g. percentage value, fixed amount).
    /// Keys and values depend on the <see cref="HandlerType"/>.
    /// </summary>
    public Dictionary<string, string> Context { get; set; } = new();
}
