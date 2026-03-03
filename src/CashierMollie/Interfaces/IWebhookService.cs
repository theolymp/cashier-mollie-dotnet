namespace CashierMollie.Interfaces;

public interface IWebhookService
{
    Task HandlePaymentAsync(string molliePaymentId, CancellationToken ct = default);
}
