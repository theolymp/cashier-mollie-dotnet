using System.Globalization;
using CashierMollie.Interfaces;
using Mollie.Api.Models;
using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.List.Response;
using Mollie.Api.Models.Mandate.Response;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Refund.Response;
using Mollie.Api.Models.Subscription.Response;
using Mollie.Api.Models.Url;

namespace CashierMollie.Testing;

/// <summary>
/// An in-memory Mollie behind <see cref="IMollieClientService"/>, for testing an application
/// built on CashierMollie without the Mollie API. It keeps customers, mandates, payments,
/// subscriptions and refunds, and enforces the rules Mollie enforces where a test could otherwise
/// pass for the wrong reason: a recurring charge needs a valid mandate, a refund cannot exceed
/// what was paid, a subscription description is unique per customer among active ones.
/// </summary>
/// <remarks>
/// Mollie decides payment outcomes and runs subscriptions on its own; here the test does both:
/// <see cref="SetPaymentStatus"/> settles a payment, <see cref="ChargeSubscription"/> makes the
/// charge Mollie would make on the next payment date. Neither calls the webhook: hand the payment
/// id to <see cref="IWebhookService.HandlePaymentAsync"/> (or post it to your webhook endpoint),
/// as Mollie would. Register it with
/// <see cref="FakeMollieServiceCollectionExtensions.AddFakeMollie"/>. Thread-safe.
/// </remarks>
public sealed class FakeMollieClient : IMollieClientService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CustomerResponse> _customers = [];
    private readonly Dictionary<string, MandateResponse> _mandates = [];
    private readonly Dictionary<string, PaymentResponse> _payments = [];
    private readonly Dictionary<string, SubscriptionResponse> _subscriptions = [];
    private readonly List<RefundResponse> _refunds = [];
    private int _sequence;

    /// <summary>Base of the checkout links handed out for first payments.</summary>
    public Uri CheckoutBase { get; init; } = new("https://fake-mollie.test/checkout/");

    /// <summary>Clock for created and paid times; replace it to control time in tests.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Every payment, in creation order.</summary>
    public IReadOnlyList<PaymentResponse> Payments
    {
        get { lock (_gate) return [.. _payments.Values]; }
    }

    /// <summary>Every subscription, in creation order, including cancelled ones.</summary>
    public IReadOnlyList<SubscriptionResponse> Subscriptions
    {
        get { lock (_gate) return [.. _subscriptions.Values]; }
    }

    /// <summary>Every refund, in creation order.</summary>
    public IReadOnlyList<RefundResponse> Refunds
    {
        get { lock (_gate) return [.. _refunds]; }
    }

    // ── Controls: what Mollie and the customer would do ──────────────────────────────

    /// <summary>
    /// Gives the customer a valid mandate, as a completed first payment or the Mandates API
    /// would. Returns its id. Use it for a customer that "already has a payment method".
    /// </summary>
    public string AddMandate(string customerId, string method = "directdebit")
    {
        lock (_gate)
        {
            RequireCustomer(customerId);
            return NewMandate(customerId, method).Id;
        }
    }

    /// <summary>
    /// Settles a payment: <c>paid</c>, <c>failed</c>, <c>canceled</c> or <c>expired</c>. A paid
    /// first payment creates a valid mandate (method <paramref name="mandateMethod"/>), as the
    /// checkout does; pass null to model a method that creates none. Setting a paid payment to
    /// <c>failed</c> models a SEPA debit that bounces after it was reported paid.
    /// </summary>
    public PaymentResponse SetPaymentStatus(string paymentId, string status, string? mandateMethod = "creditcard")
    {
        if (status is not ("paid" or "failed" or "canceled" or "expired" or "open" or "pending"))
            throw new ArgumentException($"Mollie has no payment status '{status}'.", nameof(status));

        lock (_gate)
        {
            var payment = RequirePayment(paymentId);
            payment.Status = status;
            switch (status)
            {
                case "paid":
                    payment.PaidAt = Now().UtcDateTime;
                    if (payment.SequenceType == "first" && payment.MandateId == null && mandateMethod != null)
                        payment.MandateId = NewMandate(payment.CustomerId!, mandateMethod).Id;
                    break;
                case "failed":
                    payment.FailedAt = Now().UtcDateTime;
                    break;
                case "canceled":
                    payment.CanceledAt = Now().UtcDateTime;
                    break;
                case "expired":
                    payment.ExpiredAt = Now().UtcDateTime;
                    break;
            }

            return payment;
        }
    }

    /// <summary>
    /// Makes the charge Mollie makes on a subscription's next payment date: a recurring payment
    /// at the subscription's current amount, from its mandate, linked to it, with status
    /// <paramref name="status"/> (default <c>paid</c>). The next payment date moves one interval.
    /// Throws for a subscription that is not active, as Mollie would not charge it.
    /// </summary>
    public PaymentResponse ChargeSubscription(string subscriptionId, string status = "paid")
    {
        PaymentResponse payment;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
                throw new InvalidOperationException($"Fake Mollie has no subscription {subscriptionId}.");
            if (subscription.Status != "active")
                throw new InvalidOperationException($"Subscription {subscriptionId} is {subscription.Status}; Mollie charges active subscriptions only.");

            payment = NewPayment(subscription.CustomerId!, ParseAmount(subscription.Amount), subscription.Amount.Currency,
                subscription.Description, "recurring", "pending");
            payment.SubscriptionId = subscription.Id;
            payment.MandateId = subscription.MandateId;
            subscription.TimesRemaining = subscription.TimesRemaining is > 0 ? subscription.TimesRemaining - 1 : subscription.TimesRemaining;
            subscription.NextPaymentDate = NextDate(subscription.NextPaymentDate ?? Today(), subscription.Interval);
        }

        return status == "pending" ? payment : SetPaymentStatus(payment.Id, status);
    }

    /// <summary>Records a chargeback of <paramref name="amount"/> on a paid payment.</summary>
    public PaymentResponse ChargeBack(string paymentId, decimal amount)
    {
        lock (_gate)
        {
            var payment = RequirePayment(paymentId);
            decimal already = payment.AmountChargedBack == null ? 0m : ParseAmount(payment.AmountChargedBack);
            payment.AmountChargedBack = new Amount(payment.Amount.Currency, already + amount);
            return payment;
        }
    }

    /// <summary>The URL a customer would open for a payment's checkout, or null for a recurring one.</summary>
    public static string? CheckoutUrl(PaymentResponse payment) => payment.Links?.Checkout?.Href;

    // ── IMollieClientService ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<CustomerResponse> CreateCustomerAsync(string name, string? email = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var customer = new CustomerResponse
            {
                Resource = "customer", Id = NextId("cst"), Mode = Mode.Test, Name = name, Email = email,
                CreatedAt = Now().UtcDateTime, Links = null!,
            };
            _customers[customer.Id] = customer;
            return Task.FromResult(customer);
        }
    }

    /// <inheritdoc />
    public Task<CustomerResponse> GetCustomerAsync(string customerId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(RequireCustomer(customerId));
    }

    /// <inheritdoc />
    public Task<CustomerResponse> UpdateCustomerAsync(string customerId, string? name = null, string? email = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var customer = RequireCustomer(customerId);
            customer.Name = name ?? customer.Name;
            customer.Email = email ?? customer.Email;
            return Task.FromResult(customer);
        }
    }

    /// <inheritdoc />
    public Task<PaymentResponse> CreateFirstPaymentAsync(string customerId, decimal amount, string currency,
        string description, string redirectUrl, string webhookUrl, IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireCustomer(customerId);
            RequireAbsoluteWebhook(webhookUrl);
            var payment = NewPayment(customerId, amount, currency, description, "first", "open");
            payment.Links.Checkout = new UrlLink { Href = new Uri(CheckoutBase, payment.Id).ToString(), Type = "text/html" };
            return Task.FromResult(payment);
        }
    }

    /// <inheritdoc />
    public Task<PaymentResponse> CreateRecurringPaymentAsync(string customerId, string mandateId, decimal amount,
        string currency, string description, string webhookUrl, IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireValidMandate(customerId, mandateId);
            RequireAbsoluteWebhook(webhookUrl);
            var payment = NewPayment(customerId, amount, currency, description, "recurring", "pending");
            payment.MandateId = mandateId;
            return Task.FromResult(payment);
        }
    }

    /// <inheritdoc />
    public Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(RequirePayment(paymentId));
    }

    /// <inheritdoc />
    public Task<SubscriptionResponse> CreateSubscriptionAsync(string customerId, decimal amount, string currency,
        string interval, string description, string webhookUrl, string? mandateId = null,
        IReadOnlyDictionary<string, string>? metadata = null, DateTimeOffset? startDate = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireCustomer(customerId);
            RequireAbsoluteWebhook(webhookUrl);
            string mandate = mandateId ?? _mandates.Values.LastOrDefault(m => m.CustomerId == customerId && m.Status == "valid")?.Id
                ?? throw Refused($"Customer {customerId} has no valid mandate; Mollie creates no subscription without one.");
            RequireValidMandate(customerId, mandate);
            if (_subscriptions.Values.Any(s => s.CustomerId == customerId && s.Status == "active" && s.Description == description))
                throw Refused($"Customer {customerId} already has an active subscription with the description '{description}'.");
            NextDate(Today(), interval); // validates the interval

            DateTime start = (startDate?.UtcDateTime ?? Now().UtcDateTime).Date;
            var subscription = new SubscriptionResponse
            {
                Resource = "subscription", Id = NextId("sub"), Mode = Mode.Test, CreatedAt = Now().UtcDateTime,
                Status = "active", Amount = new Amount(currency, amount), Interval = interval, Description = description,
                StartDate = start, NextPaymentDate = start, MandateId = mandate, CustomerId = customerId,
                WebhookUrl = webhookUrl, Links = null!,
            };
            _subscriptions[subscription.Id] = subscription;
            return Task.FromResult(subscription);
        }
    }

    /// <inheritdoc />
    public Task<SubscriptionResponse> GetSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(RequireSubscription(customerId, subscriptionId));
    }

    /// <inheritdoc />
    public Task CancelSubscriptionAsync(string customerId, string subscriptionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var subscription = RequireSubscription(customerId, subscriptionId);
            if (subscription.Status == "canceled")
                throw Refused($"Subscription {subscriptionId} is already canceled.");
            subscription.Status = "canceled";
            subscription.CanceledAt = Now().UtcDateTime;
            subscription.NextPaymentDate = null;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<SubscriptionResponse> UpdateSubscriptionAsync(string customerId, string subscriptionId, decimal? amount = null,
        string? currency = null, string? description = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var subscription = RequireActiveSubscription(customerId, subscriptionId);
            if (amount.HasValue)
                subscription.Amount = new Amount(currency ?? subscription.Amount.Currency, amount.Value);
            if (description != null)
                subscription.Description = description;
            return Task.FromResult(subscription);
        }
    }

    /// <inheritdoc />
    public Task<SubscriptionResponse> UpdateSubscriptionMandateAsync(string customerId, string subscriptionId, string mandateId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var subscription = RequireActiveSubscription(customerId, subscriptionId);
            RequireValidMandate(customerId, mandateId);
            subscription.MandateId = mandateId;
            return Task.FromResult(subscription);
        }
    }

    /// <inheritdoc />
    public Task<MandateResponse> GetMandateAsync(string customerId, string mandateId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_mandates.TryGetValue(mandateId, out var mandate) || mandate.CustomerId != customerId)
                throw Refused($"Customer {customerId} has no mandate {mandateId}.");
            return Task.FromResult(mandate);
        }
    }

    /// <inheritdoc />
    public Task<ListResponse<MandateResponse>> GetMandateListAsync(string customerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireCustomer(customerId);
            var items = _mandates.Values.Where(m => m.CustomerId == customerId).ToList();
            return Task.FromResult(new ListResponse<MandateResponse> { Count = items.Count, Items = items, Links = null! });
        }
    }

    /// <inheritdoc />
    public Task RevokeMandateAsync(string customerId, string mandateId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireValidMandate(customerId, mandateId);
            _mandates.Remove(mandateId);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<RefundResponse> CreateRefundAsync(string paymentId, decimal amount, string currency, string? description = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var payment = RequirePayment(paymentId);
            if (payment.Status != "paid")
                throw Refused($"Payment {paymentId} is {payment.Status}; only a paid payment can be refunded.");
            if (amount <= 0m)
                throw Refused("A refund amount must be positive.");
            decimal refunded = payment.AmountRefunded == null ? 0m : ParseAmount(payment.AmountRefunded);
            decimal paid = ParseAmount(payment.Amount);
            if (refunded + amount > paid)
                throw Refused($"Refund of {amount} exceeds the {paid - refunded} left on payment {paymentId}.");

            payment.AmountRefunded = new Amount(currency, refunded + amount);
            payment.AmountRemaining = new Amount(currency, paid - refunded - amount);
            var refund = new RefundResponse
            {
                Resource = "refund", Id = NextId("re"), Amount = new Amount(currency, amount), Status = "pending",
                CreatedAt = Now().UtcDateTime, Description = description ?? "", PaymentId = paymentId, Links = null!,
            };
            _refunds.Add(refund);
            return Task.FromResult(refund);
        }
    }

    /// <inheritdoc />
    public Task<RefundResponse> GetRefundAsync(string paymentId, string refundId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_refunds.SingleOrDefault(r => r.Id == refundId && r.PaymentId == paymentId)
                ?? throw Refused($"Payment {paymentId} has no refund {refundId}."));
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────────

    private PaymentResponse NewPayment(string customerId, decimal amount, string currency, string description,
        string sequenceType, string status)
    {
        var payment = new PaymentResponse
        {
            Resource = "payment", Id = NextId("tr"), Mode = Mode.Test, CreatedAt = Now().UtcDateTime,
            Status = status, Amount = new Amount(currency, amount), Description = description,
            CustomerId = customerId, SequenceType = sequenceType, ProfileId = "pfl_fake",
            Links = new PaymentResponseLinks { Self = null!, Documentation = null!, Dashboard = null! },
        };
        _payments[payment.Id] = payment;
        return payment;
    }

    private MandateResponse NewMandate(string customerId, string method)
    {
        var mandate = new MandateResponse
        {
            Resource = "mandate", Id = NextId("mdt"), Mode = Mode.Test, Status = "valid", Method = method,
            CustomerId = customerId, CreatedAt = Now().UtcDateTime, Links = null!,
        };
        _mandates[mandate.Id] = mandate;
        return mandate;
    }

    private CustomerResponse RequireCustomer(string customerId) =>
        _customers.TryGetValue(customerId, out var customer) ? customer : throw Refused($"Fake Mollie has no customer {customerId}.");

    private PaymentResponse RequirePayment(string paymentId) =>
        _payments.TryGetValue(paymentId, out var payment) ? payment : throw Refused($"Fake Mollie has no payment {paymentId}.");

    private SubscriptionResponse RequireSubscription(string customerId, string subscriptionId) =>
        _subscriptions.TryGetValue(subscriptionId, out var s) && s.CustomerId == customerId
            ? s
            : throw Refused($"Customer {customerId} has no subscription {subscriptionId}.");

    private SubscriptionResponse RequireActiveSubscription(string customerId, string subscriptionId)
    {
        var subscription = RequireSubscription(customerId, subscriptionId);
        return subscription.Status == "active"
            ? subscription
            : throw Refused($"Subscription {subscriptionId} is {subscription.Status}; Mollie updates active subscriptions only.");
    }

    private void RequireValidMandate(string customerId, string mandateId)
    {
        if (!_mandates.TryGetValue(mandateId, out var mandate) || mandate.CustomerId != customerId || mandate.Status != "valid")
            throw Refused($"Customer {customerId} has no valid mandate {mandateId}.");
    }

    private static void RequireAbsoluteWebhook(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw Refused($"The webhook URL '{webhookUrl}' is not an absolute http(s) URL; Mollie refuses it.");
    }

    /// <summary>Mollie's interval format: "&lt;n&gt; days|weeks|months".</summary>
    private static DateTime NextDate(DateTime from, string interval)
    {
        string[] parts = interval.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1)
            throw Refused($"Interval '{interval}' is not '<n> days|weeks|months'.");
        return parts[1] switch
        {
            "day" or "days" => from.AddDays(n),
            "week" or "weeks" => from.AddDays(7 * n),
            "month" or "months" => from.AddMonths(n),
            _ => throw Refused($"Interval '{interval}' is not '<n> days|weeks|months'."),
        };
    }

    private static decimal ParseAmount(Amount amount) =>
        decimal.Parse(amount.Value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private string NextId(string prefix) =>
        $"{prefix}_fake{Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture)}";

    private DateTimeOffset Now() => Time.GetUtcNow();

    private DateTime Today() => Now().UtcDateTime.Date;

    private static FakeMollieException Refused(string message) => new(message);
}

/// <summary>What the fake throws where Mollie would answer with an error.</summary>
public sealed class FakeMollieException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FakeMollieException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public FakeMollieException() { }

    /// <summary>Creates the exception.</summary>
    public FakeMollieException(string message, Exception innerException) : base(message, innerException) { }
}
