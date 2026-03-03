using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

public class SubscriptionBuilder<TKey> : ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly IBillable<TKey> _owner;
    private readonly string _name;
    private readonly string _plan;

    private string? _coupon;
    private int _trialDays;
    private bool _mandateOnly;
    private Dictionary<string, string>? _metadata;

    public SubscriptionBuilder(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        CashierMollieOptions options,
        IBillable<TKey> owner,
        string name,
        string plan)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options;
        _owner = owner;
        _name = name;
        _plan = plan;
    }

    public ISubscriptionBuilder<TKey> WithCoupon(string coupon) { _coupon = coupon; return this; }
    public ISubscriptionBuilder<TKey> TrialDays(int days) { _trialDays = days; return this; }
    public ISubscriptionBuilder<TKey> WithProration() { return this; }
    public ISubscriptionBuilder<TKey> WithMandateOnly() { _mandateOnly = true; return this; }
    public ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata) { _metadata = metadata; return this; }

    public async Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default)
    {
        // Ensure Mollie customer exists
        var customerId = _owner.MollieCustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            var customer = await _mollieClient.CreateCustomerAsync(
                _owner.Name ?? "Customer", _owner.Email, ct);
            customerId = customer.Id;
            _owner.MollieCustomerId = customerId;
        }

        // Create local subscription record
        var subscription = new Subscription<TKey>
        {
            OwnerId = _owner.Id,
            Name = _name,
            Plan = _plan,
            MollieCustomerId = customerId,
            Status = SubscriptionStatus.Pending,
        };

        if (_trialDays > 0)
            subscription.TrialEndsAt = DateTimeOffset.UtcNow.AddDays(_trialDays);

        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);

        // Check if owner has a valid mandate
        var hasMandate = !string.IsNullOrEmpty(_owner.MollieMandateId);
        string? checkoutUrl = null;
        var requiresAction = false;

        if (!hasMandate)
        {
            // No mandate — need first payment to acquire one
            var description = _mandateOnly
                ? $"Authorization for {_plan}"
                : $"First payment for {_plan}";

            var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                customerId, 0.01m, _options.Currency,
                description,
                _options.FirstPaymentRedirectUrl, _options.WebhookUrl, ct);

            // Create local payment record so the webhook can find and process it
            var localPayment = new Payment<TKey>
            {
                OwnerId = _owner.Id,
                SubscriptionId = subscription.Id,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "open",
                Amount = 0.01m,
                Currency = _options.Currency,
            };
            _db.Payments.Add(localPayment);
            await _db.SaveChangesAsync(ct);

            checkoutUrl = molliePayment.Links?.Checkout?.Href;
            requiresAction = true;
        }
        else
        {
            // Has mandate — activate subscription immediately
            subscription.Status = SubscriptionStatus.Active;
            subscription.CycleStartedAt = DateTimeOffset.UtcNow;
            subscription.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _eventDispatcher.DispatchAsync(
                new SubscriptionCreated<TKey>(subscription, _owner.Id), ct);
        }

        return new SubscriptionResult<TKey>(subscription, checkoutUrl, requiresAction);
    }
}
