namespace CashierMollie.Models;

/// <summary>Result of <see cref="Interfaces.ICashierService{TKey}.PayOutstandingAsync"/>.</summary>
/// <param name="CheckoutUrl">Mollie checkout for the customer.</param>
/// <param name="Amount">Gross amount of the checkout: the failed cycle payment it settles.</param>
/// <param name="RemainingAfterThis">Outstanding cycle payments that remain once this one is paid.</param>
public record OutstandingPaymentResult(string CheckoutUrl, decimal Amount, int RemainingAfterThis);
