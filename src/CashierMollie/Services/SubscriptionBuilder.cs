using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Fluent builder for creating subscriptions. Collects configuration (trial days, coupons, etc.)
/// and delegates the actual creation to <see cref="IBillingEngine{TKey}"/> via <see cref="SubscriptionOptions"/>.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class SubscriptionBuilder<TKey> : ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IBillingEngine<TKey> _engine;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly IBillable<TKey> _owner;
    private readonly string _name;
    private readonly string _plan;

    private string? _coupon;
    private int _trialDays;
    private bool _mandateOnly;
    private bool _prorate;
    private Dictionary<string, string>? _metadata;

    /// <summary>
    /// Initializes a new instance of <see cref="SubscriptionBuilder{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="engine">The billing engine used for subscription creation.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    /// <param name="owner">The billable entity (user) who will own the subscription.</param>
    /// <param name="name">The subscription name (e.g. "default").</param>
    /// <param name="plan">The plan identifier.</param>
    public SubscriptionBuilder(
        CashierDbContext<TKey> db,
        IBillingEngine<TKey> engine,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        CashierMollieOptions options,
        IBillable<TKey> owner,
        string name,
        string plan)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);

        _db = db;
        _engine = engine;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options;
        _owner = owner;
        _name = name;
        _plan = plan;
    }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> WithCoupon(string coupon) { _coupon = coupon; return this; }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> TrialDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        _trialDays = days;
        return this;
    }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> WithProration() { _prorate = true; return this; }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> WithMandateOnly() { _mandateOnly = true; return this; }

    /// <inheritdoc />
    public ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata) { _metadata = metadata; return this; }

    /// <inheritdoc />
    public Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default)
    {
        var options = new SubscriptionOptions
        {
            TrialDays = _trialDays,
            CouponCode = _coupon,
            MandateOnly = _mandateOnly,
            Prorate = _prorate,
            Metadata = _metadata,
        };
        return _engine.CreateSubscriptionAsync(_owner, _name, _plan, options, ct);
    }
}
