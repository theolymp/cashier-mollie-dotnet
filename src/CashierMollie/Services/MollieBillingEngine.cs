using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// The default engine, named for Mollie's native Subscription API.
/// <para>
/// ⚠️ <b>It does not currently create a Mollie subscription.</b> Measured 2026-10-26:
/// <c>MollieSubscriptionId</c> has no assignment anywhere in this library and
/// <c>IMollieClientService.CreateSubscriptionAsync</c> has no caller, so the cancel-at-Mollie and
/// update-at-Mollie blocks below — both guarded on that field being set — are unreachable. What
/// happens today is: a local subscription row, a first payment for
/// <c>PaymentMethodUpdateAmount</c> to acquire the mandate, and activation by webhook. Nothing
/// recurring is registered with Mollie, so nothing recurring is charged. The previous sentence here
/// said this engine "delegates subscription CRUD to the Mollie API"; it does not.
/// </para>
/// <para>
/// Consequences for the features that need an amount: a coupon redemption is recorded but cannot
/// reduce a charge that is never made, and proration throws. Use the Managed engine where recurring
/// money has to move. This is tracked as the highest-priority item in BACKLOG.md.
/// </para>
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public partial class MollieBillingEngine<TKey> : IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly ICouponService<TKey> _coupons;
    private readonly ILogger<MollieBillingEngine<TKey>> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="MollieBillingEngine{TKey}"/>.
    /// </summary>
    /// <param name="db">The CashierMollie database context.</param>
    /// <param name="mollieClient">The Mollie API client facade.</param>
    /// <param name="eventDispatcher">The event dispatcher for lifecycle events.</param>
    /// <param name="options">CashierMollie configuration options.</param>
    /// <param name="coupons">Coupon validation and redemption. Required rather than optional: an
    /// engine that silently cannot apply a coupon is the defect this parameter exists to end.</param>
    /// <param name="logger">The logger instance.</param>
    public MollieBillingEngine(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options,
        ICouponService<TKey> coupons,
        ILogger<MollieBillingEngine<TKey>> logger)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
        _coupons = coupons;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool RequiresBackgroundProcessing => false;

    // Not a startup banner: it fires on the one call where someone handed this engine a coupon,
    // and it names the code. A startup warning about a feature nobody used would be wallpaper.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Coupon {Code} was redeemed for subscription {SubscriptionName}, but this engine "
            + "registers no recurring charge with Mollie, so the redemption cannot reduce anything. "
            + "Use the Managed engine for discounted recurring billing.")]
    private partial void LogCouponRecordedButNoRecurringCharge(string code, string subscriptionName);

    /// <inheritdoc />
    public async Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default)
    {
        ProrationSupport.ThrowIfRequested(options.Prorate);

        // Ensure Mollie customer exists
        if (string.IsNullOrEmpty(owner.MollieCustomerId))
        {
            var customer = await _mollieClient.CreateCustomerAsync(
                owner.Name ?? "Customer", owner.Email, ct);
            owner.MollieCustomerId = customer.Id;
        }

        var subscription = new Subscription<TKey>
        {
            OwnerId = owner.Id,
            Name = name,
            Plan = plan,
            MollieCustomerId = owner.MollieCustomerId,
            Status = SubscriptionStatus.Pending,
            Quantity = options.Quantity > 1 ? options.Quantity : null,
        };

        if (options.TrialDays > 0)
            subscription.TrialEndsAt = DateTimeOffset.UtcNow.AddDays(options.TrialDays);

        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);

        // A coupon code is a money instruction. Validate it and record the redemption here, or
        // reject it: until 2026-10-26 this field travelled from the builder into this method and
        // was dropped — full price, no redemption, no event, no error. ValidateAsync throws for an
        // unknown code, which is why an invalid coupon now fails the create instead of being
        // quietly ignored.
        if (!string.IsNullOrWhiteSpace(options.CouponCode))
        {
            await _coupons.RedeemAsync(owner, options.CouponCode, name, ct);
        }

        if (!string.IsNullOrWhiteSpace(options.CouponCode))
        {
            // Said at the moment it bites, not only in the documentation: this engine registers no
            // recurring charge, so there is nothing for the redemption to reduce.
            LogCouponRecordedButNoRecurringCharge(options.CouponCode, name);
        }

        string? checkoutUrl = null;
        bool requiresAction = false;

        if (string.IsNullOrEmpty(owner.MollieMandateId) && !options.MandateOnly)
        {
            // First payment flow — redirect to Mollie checkout to acquire a mandate
            var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                owner.MollieCustomerId, _options.PaymentMethodUpdateAmount, _options.Currency,
                $"First payment for {plan}",
                _options.FirstPaymentRedirectUrl,
                _options.EffectiveWebhookUrl, options.Metadata, ct);

            var localPayment = new Payment<TKey>
            {
                OwnerId = owner.Id,
                SubscriptionId = subscription.Id,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "open",
                Amount = _options.PaymentMethodUpdateAmount,
                Currency = _options.Currency,
            };
            _db.Payments.Add(localPayment);
            await _db.SaveChangesAsync(ct);

            checkoutUrl = molliePayment.Links?.Checkout?.Href;
            requiresAction = true;
        }
        else
        {
            // Direct activation — mandate exists or mandate-only mode
            subscription.Status = SubscriptionStatus.Active;
            subscription.CycleStartedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _eventDispatcher.DispatchAsync(
                new SubscriptionCreated<TKey>(subscription, owner.Id), ct);
        }

        return new SubscriptionResult<TKey>(subscription, checkoutUrl, requiresAction);
    }

    /// <inheritdoc />
    public async Task CancelSubscriptionAsync(Subscription<TKey> subscription, bool immediately,
        CancellationToken ct = default)
    {
        if (immediately)
        {
            subscription.EndsAt = DateTimeOffset.UtcNow;
        }
        else
        {
            int graceDays = _options.GracePeriodDays;
            subscription.EndsAt = subscription.CycleStartedAt?.AddDays(graceDays)
                ?? DateTimeOffset.UtcNow.AddDays(graceDays);
        }

        subscription.Status = SubscriptionStatus.Cancelled;
        await _db.SaveChangesAsync(ct);

        // Cancel at Mollie if a remote subscription exists
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) &&
            !string.IsNullOrEmpty(subscription.MollieSubscriptionId))
        {
            await _mollieClient.CancelSubscriptionAsync(
                subscription.MollieCustomerId, subscription.MollieSubscriptionId, ct);
        }

        await _eventDispatcher.DispatchAsync(
            new SubscriptionCancelled<TKey>(subscription, subscription.OwnerId), ct);
    }

    /// <inheritdoc />
    public async Task ResumeSubscriptionAsync(Subscription<TKey> subscription,
        CancellationToken ct = default)
    {
        subscription.EndsAt = null;
        subscription.Status = SubscriptionStatus.Active;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionResumed<TKey>(subscription, subscription.OwnerId), ct);
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> SwapPlanAsync(Subscription<TKey> subscription,
        string newPlan, SwapOptions? options = null, CancellationToken ct = default)
    {
        ProrationSupport.ThrowIfRequested(options?.Prorate == true);

        string oldPlan = subscription.Plan;
        subscription.Plan = newPlan;

        // Update Mollie subscription if one exists remotely
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) &&
            !string.IsNullOrEmpty(subscription.MollieSubscriptionId))
        {
            await _mollieClient.UpdateSubscriptionAsync(
                subscription.MollieCustomerId, subscription.MollieSubscriptionId,
                ct: ct);
        }

        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionPlanSwapped<TKey>(subscription, oldPlan, newPlan, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public async Task<Subscription<TKey>> UpdateQuantityAsync(Subscription<TKey> subscription,
        int quantity, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        int oldQuantity = (int)(subscription.Quantity ?? 1);
        subscription.Quantity = quantity;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionQuantityUpdated<TKey>(subscription, oldQuantity, quantity, subscription.OwnerId), ct);

        return subscription;
    }

    /// <inheritdoc />
    public Task ProcessDueItemsAsync(CancellationToken ct = default)
        => Task.CompletedTask; // No-op for Mollie native engine
}
