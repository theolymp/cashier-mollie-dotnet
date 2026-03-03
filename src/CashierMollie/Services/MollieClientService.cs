using CashierMollie.Interfaces;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models;
using Mollie.Api.Models.Customer.Request;
using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.List.Response;
using Mollie.Api.Models.Mandate.Response;
using Mollie.Api.Models.Payment;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Subscription.Request;
using Mollie.Api.Models.Subscription.Response;

namespace CashierMollie.Services;

public class MollieClientService : IMollieClientService
{
    private readonly ICustomerClient _customerClient;
    private readonly IPaymentClient _paymentClient;
    private readonly ISubscriptionClient _subscriptionClient;
    private readonly IMandateClient _mandateClient;

    public MollieClientService(
        ICustomerClient customerClient,
        IPaymentClient paymentClient,
        ISubscriptionClient subscriptionClient,
        IMandateClient mandateClient)
    {
        _customerClient = customerClient;
        _paymentClient = paymentClient;
        _subscriptionClient = subscriptionClient;
        _mandateClient = mandateClient;
    }

    public async Task<CustomerResponse> CreateCustomerAsync(string name, string? email, CancellationToken ct)
    {
        var request = new CustomerRequest { Name = name, Email = email };
        return await _customerClient.CreateCustomerAsync(request, ct);
    }

    public Task<CustomerResponse> GetCustomerAsync(string customerId, CancellationToken ct)
        => _customerClient.GetCustomerAsync(customerId, testmode: false, cancellationToken: ct);

    public async Task<PaymentResponse> CreateFirstPaymentAsync(string customerId, decimal amount,
        string currency, string description, string redirectUrl, string webhookUrl, CancellationToken ct)
    {
        var request = new PaymentRequest
        {
            Amount = new Amount(currency, amount),
            Description = description,
            RedirectUrl = redirectUrl,
            WebhookUrl = webhookUrl,
            CustomerId = customerId,
            SequenceType = SequenceType.First,
        };
        return await _paymentClient.CreatePaymentAsync(request, cancellationToken: ct);
    }

    public async Task<PaymentResponse> CreateRecurringPaymentAsync(string customerId, string mandateId,
        decimal amount, string currency, string description, string webhookUrl, CancellationToken ct)
    {
        var request = new PaymentRequest
        {
            Amount = new Amount(currency, amount),
            Description = description,
            WebhookUrl = webhookUrl,
            CustomerId = customerId,
            MandateId = mandateId,
            SequenceType = SequenceType.Recurring,
        };
        return await _paymentClient.CreatePaymentAsync(request, cancellationToken: ct);
    }

    public Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct)
        => _paymentClient.GetPaymentAsync(paymentId, cancellationToken: ct);

    public async Task<SubscriptionResponse> CreateSubscriptionAsync(string customerId, decimal amount,
        string currency, string interval, string description, string webhookUrl, string? mandateId, CancellationToken ct)
    {
        var request = new SubscriptionRequest
        {
            Amount = new Amount(currency, amount),
            Interval = interval,
            Description = description,
            WebhookUrl = webhookUrl,
            MandateId = mandateId,
        };
        return await _subscriptionClient.CreateSubscriptionAsync(customerId, request, ct);
    }

    public Task<SubscriptionResponse> GetSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct)
        => _subscriptionClient.GetSubscriptionAsync(customerId, subscriptionId, testmode: false, cancellationToken: ct);

    public Task CancelSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct)
        => _subscriptionClient.CancelSubscriptionAsync(customerId, subscriptionId, testmode: false, cancellationToken: ct);

    public async Task<SubscriptionResponse> UpdateSubscriptionAsync(string customerId, string subscriptionId,
        decimal? amount, string? currency, string? description, CancellationToken ct)
    {
        var request = new SubscriptionUpdateRequest();
        if (amount.HasValue) request.Amount = new Amount(currency ?? Currency.EUR, amount.Value);
        if (description != null) request.Description = description;
        return await _subscriptionClient.UpdateSubscriptionAsync(customerId, subscriptionId, request, ct);
    }

    public Task<MandateResponse> GetMandateAsync(string customerId, string mandateId, CancellationToken ct)
        => _mandateClient.GetMandateAsync(customerId, mandateId, cancellationToken: ct);

    public Task<ListResponse<MandateResponse>> GetMandateListAsync(string customerId, CancellationToken ct)
        => _mandateClient.GetMandateListAsync(customerId, cancellationToken: ct);

    public Task RevokeMandateAsync(string customerId, string mandateId, CancellationToken ct)
        => _mandateClient.RevokeMandate(customerId, mandateId, cancellationToken: ct);
}
