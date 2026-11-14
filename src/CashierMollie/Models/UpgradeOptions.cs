namespace CashierMollie.Models;

/// <summary>Options for <see cref="Interfaces.ICashierService{TKey}.UpgradeAsync"/>.</summary>
public record UpgradeOptions
{
    /// <summary>
    /// True (the default): the unused value of the replaced subscriptions is credited, net,
    /// against the new plan's first payment, and only a surplus is refunded. False: nothing is
    /// offset; the full unused value is refunded onto the old payments and the full price is
    /// charged. Set it to false when the owner's tax treatment changed since the replaced
    /// subscriptions were paid, for example because a VAT id was added, so that the old and the
    /// new amount are never netted across two tax treatments.
    /// </summary>
    public bool OffsetCredit { get; init; } = true;
}

/// <summary>The unused value of one subscription replaced by an upgrade.</summary>
/// <param name="SubscriptionId">The replaced subscription.</param>
/// <param name="Plan">Its plan at the time of the upgrade.</param>
/// <param name="RemainingDays">Whole days left in its paid cycle; the current day counts as used.</param>
/// <param name="ValueNet">Net value of those days, rounded to the cent.</param>
/// <param name="RefundedGross">Gross amount refunded onto its last payment; 0 when the value was
/// fully credited.</param>
public record ReplacedSubscriptionCredit(
    long SubscriptionId, string Plan, int RemainingDays, decimal ValueNet, decimal RefundedGross);

/// <summary>
/// Everything an invoice for an upgrade needs: the new plan's price, the credit as its own line,
/// and what was charged and refunded.
/// </summary>
/// <param name="Subscription">The new, active subscription.</param>
/// <param name="Price">The resolved price of the new plan.</param>
/// <param name="Replaced">One entry per replaced subscription.</param>
/// <param name="CreditAppliedNet">Net credit offset against the new plan's first cycle.</param>
/// <param name="ChargedNet">Net amount charged now: price minus credit applied.</param>
/// <param name="ChargedGross">Gross amount charged now; 0 when the credit covered the cycle.</param>
/// <param name="Payment">The local payment record of that charge, or null when nothing was charged.</param>
/// <param name="UnrefundedNet">Net surplus that could not be refunded because the payments behind
/// it had no refundable amount left. 0 in every ordinary case; anything else needs a manual refund
/// and is also logged as a warning.</param>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public record UpgradeResult<TKey>(
    Subscription<TKey> Subscription,
    PlanPrice Price,
    IReadOnlyList<ReplacedSubscriptionCredit> Replaced,
    decimal CreditAppliedNet,
    decimal ChargedNet,
    decimal ChargedGross,
    Payment<TKey>? Payment,
    decimal UnrefundedNet) where TKey : IEquatable<TKey>;
