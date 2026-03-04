using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Fluent builder for creating one-off charges (non-subscription payments).
/// If the owner has a valid mandate, a direct recurring payment is created.
/// Otherwise a first-payment checkout flow is initiated.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class ChargeBuilder<TKey> : IChargeBuilder<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly IBillable<TKey> _owner;
    private readonly decimal _amount;

    private string _description = "One-off charge";
    private Dictionary<string, string>? _metadata;

    /// <summary>
    /// Creates a new <see cref="ChargeBuilder{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    /// <param name="owner">The billable entity being charged.</param>
    /// <param name="amount">The charge amount (must be positive).</param>
    public ChargeBuilder(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        CashierMollieOptions options,
        IBillable<TKey> owner,
        decimal amount)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options;
        _owner = owner;
        _amount = amount;
    }

    /// <inheritdoc />
    public IChargeBuilder<TKey> WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <inheritdoc />
    public IChargeBuilder<TKey> WithMetadata(Dictionary<string, string> metadata)
    {
        _metadata = metadata;
        return this;
    }

    /// <inheritdoc />
    public async Task<ChargeResult<TKey>> CreateAsync(CancellationToken ct = default)
    {
        // Ensure Mollie customer exists
        if (string.IsNullOrEmpty(_owner.MollieCustomerId))
        {
            var customer = await _mollieClient.CreateCustomerAsync(
                _owner.Name ?? "", _owner.Email, ct);
            _owner.MollieCustomerId = customer.Id;
        }

        // Create local order
        var order = new Order<TKey>
        {
            OwnerId = _owner.Id,
            Currency = _options.Currency,
            Subtotal = _amount,
            Total = _amount,
            TotalDue = _amount,
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        // Create payment via Mollie
        string? checkoutUrl = null;
        bool requiresAction = false;
        Mollie.Api.Models.Payment.Response.PaymentResponse molliePayment;

        if (!string.IsNullOrEmpty(_owner.MollieMandateId))
        {
            // Has mandate — direct charge (recurring)
            molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                _owner.MollieCustomerId!, _owner.MollieMandateId,
                _amount, _options.Currency, _description, _options.EffectiveWebhookUrl, ct);
        }
        else
        {
            // No mandate — checkout flow (first payment)
            molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                _owner.MollieCustomerId!, _amount, _options.Currency,
                _description, _options.FirstPaymentRedirectUrl, _options.EffectiveWebhookUrl, ct);
            checkoutUrl = molliePayment.Links?.Checkout?.Href;
            requiresAction = true;
        }

        // Create local payment record
        var localPayment = new Payment<TKey>
        {
            OwnerId = _owner.Id,
            MolliePaymentId = molliePayment.Id,
            Status = molliePayment.Status ?? "open",
            Amount = _amount,
            Currency = _options.Currency,
        };
        _db.Payments.Add(localPayment);

        order.MolliePaymentId = molliePayment.Id;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new OrderCreated<TKey>(order.Id, _owner.Id), ct);

        return new ChargeResult<TKey>(localPayment, order, checkoutUrl, requiresAction);
    }
}
