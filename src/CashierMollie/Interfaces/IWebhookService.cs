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
    Task HandlePaymentAsync(string molliePaymentId, CancellationToken ct = default);
}
