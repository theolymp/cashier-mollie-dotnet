using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.List.Response;
using Mollie.Api.Models.Mandate.Response;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Refund.Response;
using Mollie.Api.Models.Subscription.Response;

namespace CashierMollie.Interfaces;

/// <summary>
/// Facade over Mollie.Api clients. Provides only the operations
/// needed by CashierMollie services. Can be replaced in DI for testing.
/// </summary>
public interface IMollieClientService
{
    /// <summary>Creates a new customer in Mollie.</summary>
    Task<CustomerResponse> CreateCustomerAsync(string name, string? email = null, CancellationToken ct = default);

    /// <summary>Retrieves a customer from Mollie by ID.</summary>
    Task<CustomerResponse> GetCustomerAsync(string customerId, CancellationToken ct = default);

    /// <summary>Creates a first payment (SequenceType.First) to acquire a mandate for recurring billing.</summary>
    Task<PaymentResponse> CreateFirstPaymentAsync(string customerId, decimal amount, string currency,
        string description, string redirectUrl, string webhookUrl, CancellationToken ct = default);

    /// <summary>Creates a recurring payment using an existing mandate.</summary>
    Task<PaymentResponse> CreateRecurringPaymentAsync(string customerId, string mandateId, decimal amount,
        string currency, string description, string webhookUrl, CancellationToken ct = default);

    /// <summary>Retrieves a payment from Mollie by ID.</summary>
    Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct = default);

    /// <summary>Creates a subscription in Mollie for recurring billing.</summary>
    Task<SubscriptionResponse> CreateSubscriptionAsync(string customerId, decimal amount, string currency,
        string interval, string description, string webhookUrl, string? mandateId = null, CancellationToken ct = default);

    /// <summary>Retrieves a subscription from Mollie.</summary>
    Task<SubscriptionResponse> GetSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default);

    /// <summary>Cancels a subscription in Mollie.</summary>
    Task CancelSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default);

    /// <summary>Updates a subscription's amount and/or description in Mollie.</summary>
    Task<SubscriptionResponse> UpdateSubscriptionAsync(string customerId, string subscriptionId,
        decimal? amount = null, string? currency = null, string? description = null, CancellationToken ct = default);

    /// <summary>Retrieves a specific mandate for a customer.</summary>
    Task<MandateResponse> GetMandateAsync(string customerId, string mandateId, CancellationToken ct = default);

    /// <summary>Lists all mandates for a customer.</summary>
    Task<ListResponse<MandateResponse>> GetMandateListAsync(string customerId, CancellationToken ct = default);

    /// <summary>Revokes a mandate for a customer.</summary>
    Task RevokeMandateAsync(string customerId, string mandateId, CancellationToken ct = default);

    /// <summary>Creates a refund for a payment.</summary>
    Task<RefundResponse> CreateRefundAsync(string paymentId, decimal amount, string currency,
        string? description = null, CancellationToken ct = default);

    /// <summary>Gets refund details.</summary>
    Task<RefundResponse> GetRefundAsync(string paymentId, string refundId,
        CancellationToken ct = default);
}
