namespace CashierMollie.Models;

/// <summary>
/// Options for swapping a subscription to a different plan.
/// </summary>
public class SwapOptions
{
    /// <summary>
    /// When true, calculates proration credit for unused time on the current plan.
    /// </summary>
    public bool Prorate { get; set; }
}
