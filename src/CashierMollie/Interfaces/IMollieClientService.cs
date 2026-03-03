using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.List.Response;
using Mollie.Api.Models.Mandate.Response;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Subscription.Response;

namespace CashierMollie.Interfaces;

/// <summary>
/// Facade over Mollie.Api clients. Provides only the operations
/// needed by CashierMollie services.
/// </summary>
public interface IMollieClientService
{
    // Customers
    Task<CustomerResponse> CreateCustomerAsync(string name, string? email = null, CancellationToken ct = default);
    Task<CustomerResponse> GetCustomerAsync(string customerId, CancellationToken ct = default);

    // Payments
    Task<PaymentResponse> CreateFirstPaymentAsync(string customerId, decimal amount, string currency,
        string description, string redirectUrl, string webhookUrl, CancellationToken ct = default);
    Task<PaymentResponse> CreateRecurringPaymentAsync(string customerId, string mandateId, decimal amount,
        string currency, string description, string webhookUrl, CancellationToken ct = default);
    Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct = default);

    // Subscriptions
    Task<SubscriptionResponse> CreateSubscriptionAsync(string customerId, decimal amount, string currency,
        string interval, string description, string webhookUrl, string? mandateId = null, CancellationToken ct = default);
    Task<SubscriptionResponse> GetSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default);
    Task CancelSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default);
    Task<SubscriptionResponse> UpdateSubscriptionAsync(string customerId, string subscriptionId,
        decimal? amount = null, string? currency = null, string? description = null, CancellationToken ct = default);

    // Mandates
    Task<MandateResponse> GetMandateAsync(string customerId, string mandateId, CancellationToken ct = default);
    Task<ListResponse<MandateResponse>> GetMandateListAsync(string customerId, CancellationToken ct = default);
    Task RevokeMandateAsync(string customerId, string mandateId, CancellationToken ct = default);
}
