namespace CashierMollie.Models;

/// <summary>
/// Result of a payment method update request, containing the Mollie checkout URL
/// where the user can provide new payment details.
/// </summary>
/// <param name="CheckoutUrl">The Mollie-hosted URL where the user updates their payment method.</param>
public record PaymentMethodUpdateResult(string CheckoutUrl);
