namespace CashierMollie.Models;

/// <summary>
/// Options for swapping a subscription to a different plan.
/// </summary>
public record SwapOptions
{
    /// <summary>
    /// THROWS when true; use <see cref="Interfaces.ICashierService{TKey}.UpgradeAsync"/> for an
    /// immediate change with credit. This flag itself is not implemented. This summary used to read "calculates
    /// proration credit for unused time on the current plan", which is the credit nothing
    /// computed — the sentence outlived the behaviour it described by one commit.
    /// Intended meaning: prorate the unused remainder of the current plan on a swap.
    /// </summary>
    public bool Prorate { get; init; }
}
