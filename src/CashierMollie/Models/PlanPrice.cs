namespace CashierMollie.Models;

/// <summary>
/// What one cycle of a plan costs a given owner, as decided by the consumer's
/// <see cref="Interfaces.IPlanPriceResolver{TKey}"/>.
/// </summary>
/// <param name="Net">Net amount per cycle, before tax. Must be positive.</param>
/// <param name="Currency">ISO 4217 currency. Must equal <see cref="CashierMollieOptions.Currency"/>;
/// anything else is rejected rather than passed to Mollie.</param>
/// <param name="TaxRatePercent">Tax rate in percent (19 for 19 %, 0 for reverse charge). Which rate
/// applies is the consumer's decision: this library contains no tax logic.</param>
/// <param name="Description">Text Mollie shows on the customer's statement. Mollie requires it to be
/// unique among one customer's subscriptions.</param>
public sealed record PlanPrice(decimal Net, string Currency, decimal TaxRatePercent, string Description)
{
    /// <summary>The amount Mollie charges: <see cref="Money.Gross"/> of <see cref="Net"/>.</summary>
    public decimal Gross => Money.Gross(Net, TaxRatePercent);
}
