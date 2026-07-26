namespace CashierMollie.Interfaces;

/// <summary>
/// Processes incoming Mollie payment webhooks.
/// Use via the built-in <see cref="Middleware.CashierWebhookMiddleware"/> or inject directly for manual handling.
/// </summary>
public interface IWebhookService
{
    /// <summary>
    /// Handles a Mollie payment webhook by fetching the payment status from Mollie,
    /// updating the local payment record, activating pending subscriptions on successful
    /// first payments, and dispatching appropriate events.
    /// </summary>
    /// <param name="molliePaymentId">The Mollie payment ID from the webhook (e.g. "tr_xxx").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <b>If you call this from your own endpoint, you own the webhook trust invariant.</b> Mollie
    /// does not sign webhooks, so the request body is attacker-controlled. Pass only the payment
    /// ID: this method re-fetches the authoritative state from Mollie itself. Never let other
    /// fields from the payload (status, amount) decide what happens -- treat the request as no
    /// more than a hint that something about this payment may have changed.
    /// </remarks>
    Task HandlePaymentAsync(string molliePaymentId, CancellationToken ct = default);
}
