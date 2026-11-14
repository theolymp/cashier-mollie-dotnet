namespace CashierMollie.Models;

/// <summary>Result of <see cref="Interfaces.ICashierService{TKey}.PayOutstandingAsync"/>.</summary>
/// <param name="CheckoutUrl">Mollie checkout for the customer.</param>
/// <param name="Amount">Gross amount of the checkout: the failed cycle payment it settles.</param>
/// <param name="RemainingAfterThis">Outstanding cycle payments that remain once this one is paid.</param>
public record OutstandingPaymentResult(string CheckoutUrl, decimal Amount, int RemainingAfterThis);

/// <summary>Result of <see cref="Interfaces.ICashierService{TKey}.CancelImmediatelyWithRefundAsync"/>.</summary>
/// <param name="RemainingDays">Whole days of the paid cycle that were left; the current day counts as used.</param>
/// <param name="ValueNet">Their net value, rounded to the cent.</param>
/// <param name="RefundedGross">Gross amount refunded.</param>
/// <param name="UnrefundedNet">Net value that could not be refunded (no refundable payment left, or
/// the refund failed). 0 in the ordinary case; anything else is owed to the customer.</param>
public record EarlyEndResult(int RemainingDays, decimal ValueNet, decimal RefundedGross, decimal UnrefundedNet);
