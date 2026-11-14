using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// The default engine: Mollie owns the recurring cycle. Since 0.6.0 a subscription with a price
/// is registered at Mollie, which charges its gross amount every
/// <see cref="CashierMollieOptions.CycleDays"/> days; before that this engine registered nothing
/// and nothing recurring was ever charged.
/// </summary>
/// <remarks>
/// <para>The price comes from <see cref="IPlanPriceResolver{TKey}"/> when a subscription is
/// created. Without a mandate the first cycle is paid through a Mollie checkout and the webhook
/// registers the subscription once that payment is paid. With a mandate the subscription is
/// active at once, the first cycle is charged from the mandate, and Mollie takes over one cycle
/// later. A trial charges nothing up front; Mollie's first charge falls on the trial's end.</para>
/// <para>Mandate-only subscriptions and subscriptions created before 0.6.0 carry no price and stay
/// purely local, exactly as before.</para>
/// <para>Coupons are still recorded without reducing anything: Mollie charges one constant amount,
/// and a discount on it is not built.</para>
/// </remarks>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public partial class MollieBillingEngine<TKey> : IBillingEngine<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly ICouponService<TKey> _coupons;
    private readonly IPlanPriceResolver<TKey> _prices;
    private readonly IRefundService<TKey> _refunds;
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
    /// <param name="prices">Resolves a plan id to the price Mollie charges.</param>
    /// <param name="refunds">Refunds the surplus credit of an upgrade.</param>
    /// <param name="logger">The logger instance.</param>
    public MollieBillingEngine(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options,
        ICouponService<TKey> coupons,
        IPlanPriceResolver<TKey> prices,
        IRefundService<TKey> refunds,
        ILogger<MollieBillingEngine<TKey>> logger)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
        _coupons = coupons;
        _prices = prices;
        _refunds = refunds;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool RequiresBackgroundProcessing => false;

    // Not a startup banner: it fires on the one call where someone handed this engine a coupon,
    // and it names the code. A startup warning about a feature nobody used would be wallpaper.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Coupon {Code} was redeemed for subscription {SubscriptionName}, but this engine "
            + "charges Mollie's constant amount, so the redemption cannot reduce anything. "
            + "Use the Managed engine for discounted recurring billing.")]
    private partial void LogCouponRecordedButNoRecurringCharge(string code, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Upgrade to subscription {SubscriptionId}: a surplus of {Amount} (net) could not be "
            + "refunded because the payments behind it have no refundable amount left. Refund it manually.")]
    private partial void LogUnrefundedSurplus(long subscriptionId, decimal amount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Subscription {SubscriptionId} was charged its first cycle but could not be registered "
            + "at Mollie yet; the webhook retries when that payment is reported paid.")]
    private partial void LogRegistrationDeferred(long subscriptionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Upgrade to subscription {SubscriptionId}: could not {Step} after the customer was "
            + "charged. The local state is recorded; the step is listed in UpgradeResult.Incomplete.")]
    private partial void LogUpgradeStepFailed(long subscriptionId, string step, Exception exception);

    /// <inheritdoc />
    public async Task<SubscriptionResult<TKey>> CreateSubscriptionAsync(
        IBillable<TKey> owner, string name, string plan,
        SubscriptionOptions options, CancellationToken ct = default)
    {
        ProrationSupport.ThrowIfRequested(options.Prorate);

        // Resolved before anything is written, so an unknown plan or a foreign currency leaves
        // no half-created subscription behind. A mandate-only subscription has no price.
        PlanPrice? price = options.MandateOnly
            ? null
            : await NativeSubscriptions.ResolveCheckedAsync(_prices, _options, owner.Id, plan, ct);

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

        if (price != null)
            NativeSubscriptions.ApplyPrice(subscription, price);

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

            // Said at the moment it bites, not only in the documentation: Mollie charges one
            // constant amount, so there is nothing for the redemption to reduce.
            LogCouponRecordedButNoRecurringCharge(options.CouponCode, name);
        }

        bool hasMandate = !string.IsNullOrEmpty(owner.MollieMandateId);

        if (price == null)
        {
            // Mandate-only: unchanged since 0.5.0. Local and active, nothing registered at Mollie.
            await ActivateAsync(subscription, owner.Id, ct);
            return new SubscriptionResult<TKey>(subscription, null, false);
        }

        bool onTrial = subscription.TrialEndsAt.HasValue;

        if (!hasMandate)
        {
            // Checkout. On a trial it only acquires the mandate; otherwise it pays the first
            // cycle. Either way the webhook activates the subscription and registers it at
            // Mollie once this payment is paid.
            decimal amount = onTrial ? _options.PaymentMethodUpdateAmount : price.Gross;
            var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
                owner.MollieCustomerId, amount, _options.Currency,
                onTrial ? $"Payment method for {price.Description}" : price.Description,
                _options.FirstPaymentRedirectUrl,
                _options.EffectiveWebhookUrl, options.Metadata, ct);

            _db.Payments.Add(new Payment<TKey>
            {
                OwnerId = owner.Id,
                SubscriptionId = subscription.Id,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "open",
                Amount = amount,
                Currency = _options.Currency,
                AmountNet = onTrial ? null : price.Net,
                TaxRatePercent = onTrial ? null : price.TaxRatePercent,
            });
            await _db.SaveChangesAsync(ct);

            return new SubscriptionResult<TKey>(subscription, molliePayment.Links?.Checkout?.Href, true);
        }

        string mandateId = owner.MollieMandateId!;
        var now = DateTimeOffset.UtcNow;

        if (onTrial)
        {
            // Nothing to charge now; Mollie's first charge is the trial's end.
            await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription,
                mandateId, subscription.TrialEndsAt!.Value, ct);
        }
        else
        {
            // The first cycle from the mandate, now; Mollie charges from the next cycle on.
            // Active at once rather than when the payment settles: a SEPA debit can take days,
            // and a failure arrives as OrderPaymentFailed through the webhook like any other.
            var molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                owner.MollieCustomerId, mandateId, price.Gross, _options.Currency,
                price.Description, _options.EffectiveWebhookUrl, options.Metadata, ct);

            _db.Payments.Add(new Payment<TKey>
            {
                OwnerId = owner.Id,
                SubscriptionId = subscription.Id,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "pending",
                Amount = price.Gross,
                Currency = _options.Currency,
                AmountNet = price.Net,
                TaxRatePercent = price.TaxRatePercent,
                MollieMandateId = mandateId,
            });

            subscription.CycleStartedAt = now;
            subscription.CycleValueNet = price.Net;
            subscription.Status = SubscriptionStatus.Active;

            // The charge is saved before Mollie is asked to register anything, so a failure there
            // cannot lose it. A failed registration is retried by the webhook once this payment
            // is reported paid.
            await _db.SaveChangesAsync(ct);
            await _eventDispatcher.DispatchAsync(new SubscriptionCreated<TKey>(subscription, owner.Id), ct);
            try
            {
                await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription,
                    mandateId, now.AddDays(_options.CycleDays), ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogRegistrationDeferred(subscription.Id, ex);
            }

            return new SubscriptionResult<TKey>(subscription, null, false);
        }

        subscription.Status = SubscriptionStatus.Active;
        await _db.SaveChangesAsync(ct);
        await _eventDispatcher.DispatchAsync(new SubscriptionCreated<TKey>(subscription, owner.Id), ct);

        return new SubscriptionResult<TKey>(subscription, null, false);
    }

    private async Task ActivateAsync(Subscription<TKey> subscription, TKey ownerId, CancellationToken ct)
    {
        subscription.Status = SubscriptionStatus.Active;
        subscription.CycleStartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _eventDispatcher.DispatchAsync(new SubscriptionCreated<TKey>(subscription, ownerId), ct);
    }

    /// <inheritdoc />
    public async Task CancelSubscriptionAsync(Subscription<TKey> subscription, bool immediately,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (immediately)
        {
            subscription.EndsAt = now;
        }
        else if (NativeSubscriptions.IsPriced(subscription))
        {
            // A priced subscription ends with what was paid for: the running cycle, or the trial.
            subscription.EndsAt = subscription.CycleStartedAt?.AddDays(_options.CycleDays)
                ?? subscription.TrialEndsAt
                ?? now;
        }
        else
        {
            int graceDays = _options.GracePeriodDays;
            subscription.EndsAt = subscription.CycleStartedAt?.AddDays(graceDays)
                ?? now.AddDays(graceDays);
        }

        // At Mollie first: if that call fails, nothing local claims a cancellation that Mollie
        // would keep charging through.
        await CancelAtMollieAsync(subscription, ct);

        subscription.Status = SubscriptionStatus.Cancelled;
        await _db.SaveChangesAsync(ct);

        await _eventDispatcher.DispatchAsync(
            new SubscriptionCancelled<TKey>(subscription, subscription.OwnerId), ct);
    }

    private async Task CancelAtMollieAsync(Subscription<TKey> subscription, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) &&
            NativeSubscriptions.IsRegistered(subscription))
        {
            await _mollieClient.CancelSubscriptionAsync(
                subscription.MollieCustomerId, subscription.MollieSubscriptionId!, ct);
            // The id is kept: a payment already under way at Mollie still has to find this
            // subscription. A cancelled Mollie subscription cannot be revived; resuming
            // registers a new one and replaces the id.
        }
    }

    /// <inheritdoc />
    public async Task ResumeSubscriptionAsync(Subscription<TKey> subscription,
        CancellationToken ct = default)
    {
        if (NativeSubscriptions.IsPriced(subscription)
            && subscription.Status == SubscriptionStatus.Cancelled
            && subscription.EndsAt > DateTimeOffset.UtcNow)
        {
            // Mollie's next charge is the day the cancellation would have taken effect.
            string mandateId = await NativeSubscriptions.LatestMandateAsync(_db, subscription.OwnerId, ct)
                ?? throw new CashierException(
                    "Cannot resume: the owner has no mandate for Mollie to charge the next cycle from.");
            await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription,
                mandateId, subscription.EndsAt.Value, ct);
        }

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

        if (!NativeSubscriptions.IsPriced(subscription))
        {
            // No price, nothing at Mollie: the label is all there is, as before 0.6.0.
            subscription.Plan = newPlan;
        }
        else
        {
            var price = await NativeSubscriptions.ResolveCheckedAsync(
                _prices, _options, subscription.OwnerId, newPlan, ct);

            if (NativeSubscriptions.IsRegistered(subscription))
            {
                // Mollie charges at the start of a cycle, so a new amount sent now is the next
                // charge: the change takes effect at the end of the paid cycle, without a refund.
                // The plan follows when that charge is paid (WebhookService).
                await _mollieClient.UpdateSubscriptionAsync(
                    subscription.MollieCustomerId!, subscription.MollieSubscriptionId!,
                    price.Gross, _options.Currency, price.Description, ct);
                subscription.NextPlan = newPlan == subscription.Plan ? null : newPlan;
            }
            else
            {
                // Not registered at Mollie yet (checkout open): nothing has been charged at the
                // new price's expense, so the plan changes at once.
                subscription.Plan = newPlan;
            }

            NativeSubscriptions.ApplyPrice(subscription, price);
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
    public async Task<UpgradeResult<TKey>> UpgradeAsync(IBillable<TKey> owner, string name,
        string newPlan, IReadOnlyCollection<Subscription<TKey>> replaces, UpgradeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replaces);
        options ??= new UpgradeOptions();
        var now = DateTimeOffset.UtcNow;

        // ── Refuse before anything moves ────────────────────────────────
        if (replaces.Count == 0)
            throw new CashierException("An upgrade replaces at least one subscription.");
        if (replaces.Select(r => r.Id).Distinct().Count() != replaces.Count)
            throw new CashierException("An upgrade lists the same subscription twice.");
        foreach (var r in replaces)
        {
            if (!r.OwnerId.Equals(owner.Id))
                throw new CashierException($"Subscription {r.Id} belongs to another owner.");
            if (r.Status != SubscriptionStatus.Active || r.EndsAt.HasValue)
                throw new CashierException($"Subscription {r.Id} is not active and cannot be replaced.");
            if (!NativeSubscriptions.IsPriced(r))
                throw new CashierException($"Subscription {r.Id} has no price, so its unused value is unknown.");
        }

        // (OwnerId, Name) is unique over every row, ended ones included, so a replaced
        // subscription keeps its name and the new one needs a name never used by this owner.
        // Checked here because the database would refuse it only after the charge.
        bool nameTaken = await _db.Subscriptions.AnyAsync(s => s.OwnerId!.Equals(owner.Id) && s.Name == name, ct);
        if (nameTaken)
            throw new CashierException(
                $"The owner already has a subscription named '{name}' (names are unique per owner, "
                + "ended subscriptions included). Use a new name for the upgraded subscription.");

        string customerId = owner.MollieCustomerId
            ?? throw new CashierException("Owner has no Mollie customer ID.");
        string mandateId = owner.MollieMandateId
            ?? await NativeSubscriptions.LatestMandateAsync(_db, owner.Id, ct)
            ?? throw new CashierException("Owner has no mandate to charge the upgrade from.");

        var price = await NativeSubscriptions.ResolveCheckedAsync(_prices, _options, owner.Id, newPlan, ct);

        // ── The arithmetic, all of it before the first call to Mollie ──
        var values = replaces
            .Select(r =>
            {
                int days = r.CycleStartedAt.HasValue && r.CycleValueNet.HasValue
                    ? Money.RemainingWholeDays(r.CycleStartedAt.Value, _options.CycleDays, now)
                    : 0;
                decimal value = days == 0 ? 0m : Money.Prorate(r.CycleValueNet!.Value, days, _options.CycleDays);
                return (Sub: r, Days: days, Value: value);
            })
            .ToList();

        decimal credit = values.Sum(v => v.Value);
        decimal creditApplied = options.OffsetCredit ? Math.Min(credit, price.Net) : 0m;
        decimal chargedNet = price.Net - creditApplied;
        decimal chargedGross = chargedNet > 0m ? Money.Gross(chargedNet, price.TaxRatePercent) : 0m;
        decimal toRefundNet = credit - creditApplied;

        // ── Charge first: if the mandate is refused, nothing else has happened ──
        Mollie.Api.Models.Payment.Response.PaymentResponse? molliePayment = null;
        if (chargedGross > 0m)
        {
            molliePayment = await _mollieClient.CreateRecurringPaymentAsync(
                customerId, mandateId, chargedGross, _options.Currency, price.Description,
                _options.EffectiveWebhookUrl, metadata: null, ct);
        }

        // ── Record everything the charge implies, in one save, before any further call ──
        // From here on a failure must not lose the charge: the payment, the new subscription and
        // the end of the replaced ones are written before Mollie is asked for anything else.
        foreach (var r in replaces)
        {
            r.Status = SubscriptionStatus.Cancelled;
            r.EndsAt = now;
        }

        var subscription = new Subscription<TKey>
        {
            OwnerId = owner.Id,
            Name = name,
            Plan = newPlan,
            MollieCustomerId = customerId,
            Status = SubscriptionStatus.Active,
            CycleStartedAt = now,
            CycleValueNet = price.Net,
        };
        NativeSubscriptions.ApplyPrice(subscription, price);
        _db.Subscriptions.Add(subscription);

        Payment<TKey>? payment = null;
        if (molliePayment != null)
        {
            payment = new Payment<TKey>
            {
                OwnerId = owner.Id,
                Subscription = subscription,
                MolliePaymentId = molliePayment.Id,
                Status = molliePayment.Status ?? "pending",
                Amount = chargedGross,
                Currency = _options.Currency,
                AmountNet = chargedNet,
                TaxRatePercent = price.TaxRatePercent,
                CreditNet = creditApplied,
                MollieMandateId = mandateId,
            };
            _db.Payments.Add(payment);
        }

        await _db.SaveChangesAsync(ct);

        foreach (var r in replaces)
            r.ReplacedBySubscriptionId = subscription.Id;
        await _db.SaveChangesAsync(ct);

        // ── The Mollie side. Each step is attempted, and a failure is reported, not thrown ──
        // Throwing now would tell the caller the upgrade failed although the customer was charged.
        var incomplete = new List<string>();

        foreach (var r in replaces)
        {
            try
            {
                await CancelAtMollieAsync(r, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Mollie may charge it once more; the webhook cancels it again when that payment
                // arrives after the end date.
                LogUpgradeStepFailed(subscription.Id, "cancel replaced subscription at Mollie", ex);
                incomplete.Add($"Cancel subscription {r.Id} at Mollie");
            }
        }

        try
        {
            await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription, mandateId,
                now.AddDays(_options.CycleDays), ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Retried by the webhook when the upgrade payment is reported paid.
            LogUpgradeStepFailed(subscription.Id, "register the new subscription at Mollie", ex);
            incomplete.Add($"Register subscription {subscription.Id} at Mollie");
        }

        // ── Refund a surplus last: a refund that fails leaves money owed, not service missing ──
        var refunded = new Dictionary<long, decimal>();
        decimal unrefunded = 0m;
        foreach (var (sub, _, value) in values)
        {
            // Without an offset every value is refunded; with one only what the price did not
            // absorb, taken from the replaced subscriptions in the order given.
            decimal portion = Math.Min(value, toRefundNet);
            if (portion <= 0m)
                continue;
            toRefundNet -= portion;
            try
            {
                var (gross, left) = await RefundNetAsync(sub, portion, ct);
                refunded[sub.Id] = gross;
                unrefunded += left;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Refunds already made are recorded by RefundService; the rest is reported.
                LogUpgradeStepFailed(subscription.Id, "refund the surplus", ex);
                unrefunded += portion;
                incomplete.Add($"Refund for subscription {sub.Id}");
            }
        }

        if (unrefunded > 0m)
            LogUnrefundedSurplus(subscription.Id, unrefunded);

        var result = new UpgradeResult<TKey>(
            subscription,
            price,
            values.Select(v => new ReplacedSubscriptionCredit(
                v.Sub.Id, v.Sub.Plan, v.Days, v.Value, refunded.GetValueOrDefault(v.Sub.Id))).ToList(),
            creditApplied,
            chargedNet,
            chargedGross,
            payment,
            unrefunded,
            incomplete);

        foreach (var r in replaces)
            await _eventDispatcher.DispatchAsync(new SubscriptionCancelled<TKey>(r, owner.Id), ct);
        await _eventDispatcher.DispatchAsync(new SubscriptionCreated<TKey>(subscription, owner.Id), ct);
        await _eventDispatcher.DispatchAsync(new SubscriptionUpgraded<TKey>(result, owner.Id), ct);

        return result;
    }

    /// <summary>
    /// Refunds a net amount of a replaced subscription's value onto the payments that paid for it:
    /// its own, then those of the subscriptions it replaced in turn, most recent first. Each part
    /// is refunded at the tax rate that payment was charged at.
    /// </summary>
    /// <returns>The gross amount refunded and the net amount that found no refundable payment.</returns>
    private async Task<(decimal Gross, decimal LeftNet)> RefundNetAsync(Subscription<TKey> replaced,
        decimal net, CancellationToken ct)
    {
        var ownerSubs = await _db.Subscriptions
            .Where(s => s.OwnerId!.Equals(replaced.OwnerId))
            .Select(s => new { s.Id, s.ReplacedBySubscriptionId })
            .ToListAsync(ct);

        // The replaced subscription plus every one that was replaced into it, transitively. A
        // credit-only cycle has no payment of its own; its value is still sitting in these.
        var lineage = new HashSet<long> { replaced.Id };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var s in ownerSubs)
            {
                if (s.ReplacedBySubscriptionId is long into && lineage.Contains(into) && lineage.Add(s.Id))
                    grew = true;
            }
        }

        var lineageIds = lineage.ToList();
        var payments = await _db.Payments
            .Where(p => p.SubscriptionId != null && lineageIds.Contains(p.SubscriptionId.Value)
                && p.Status == "paid" && p.AmountNet != null)
            .ToListAsync(ct);

        decimal grossTotal = 0m;
        decimal left = net;
        foreach (var p in payments.OrderByDescending(p => p.PaidAt))
        {
            if (left <= 0m)
                break;
            decimal refundable = p.Amount - p.AmountRefunded - p.AmountChargedBack;
            if (refundable <= 0m)
                continue;

            decimal rate = p.TaxRatePercent ?? 0m;
            decimal gross = Money.Gross(left, rate);
            decimal coveredNet = left;
            if (gross > refundable)
            {
                gross = refundable;
                coveredNet = Money.RoundCents(refundable / (1m + (rate / 100m)));
            }

            await _refunds.RefundAsync(p, gross, $"Unused days of {replaced.Plan}", ct);
            grossTotal += gross;
            left -= coveredNet;
        }

        return (grossTotal, Math.Max(0m, left));
    }

    /// <inheritdoc />
    public async Task<int> RepriceAsync(TKey ownerId, CancellationToken ct = default)
    {
        var subscriptions = await _db.Subscriptions
            .Where(s => s.OwnerId!.Equals(ownerId) && s.Status == SubscriptionStatus.Active
                && s.EndsAt == null && s.PriceNet != null)
            .ToListAsync(ct);

        int changed = 0;
        foreach (var subscription in subscriptions)
        {
            // A pending downgrade is what Mollie charges next, so that is what gets repriced.
            var price = await NativeSubscriptions.ResolveCheckedAsync(
                _prices, _options, ownerId, subscription.NextPlan ?? subscription.Plan, ct);

            if (!NativeSubscriptions.IsRegistered(subscription))
            {
                // Active and priced but not at Mollie: a registration that failed after the
                // customer paid (or a claim left by a process that died). Repaired here at the new
                // price, from the end of what was paid for. A claim younger than StaleClaim is a
                // webhook still at work and is left alone.
                bool claimFresh = subscription.MollieSubscriptionId == NativeSubscriptions.RegisteringMarker
                    && subscription.UpdatedAt > DateTimeOffset.UtcNow - NativeSubscriptions.StaleClaim;
                string? mandateId = await NativeSubscriptions.LatestMandateAsync(_db, ownerId, ct);
                if (claimFresh || mandateId == null || (subscription.CycleStartedAt == null && subscription.TrialEndsAt == null))
                    continue;

                NativeSubscriptions.ApplyPrice(subscription, price);
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset start = subscription.TrialEndsAt > now
                    ? subscription.TrialEndsAt.Value
                    : subscription.CycleStartedAt?.AddDays(_options.CycleDays) ?? now;
                await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription,
                    mandateId, start < now ? now : start, ct);
                changed++;
                continue;
            }

            bool amountChanged = price.Gross != NativeSubscriptions.GrossPerCycle(subscription);
            bool textChanged = !string.Equals(price.Description, subscription.Description, StringComparison.Ordinal);
            if (amountChanged || textChanged)
            {
                await _mollieClient.UpdateSubscriptionAsync(
                    subscription.MollieCustomerId!, subscription.MollieSubscriptionId!,
                    price.Gross, _options.Currency, price.Description, ct);
                changed++;
            }

            NativeSubscriptions.ApplyPrice(subscription, price);
        }

        await _db.SaveChangesAsync(ct);
        return changed;
    }

    /// <inheritdoc />
    public async Task<OutstandingPaymentResult> PayOutstandingAsync(IBillable<TKey> owner,
        Subscription<TKey> subscription, string? redirectUrl = null, CancellationToken ct = default)
    {
        if (!owner.Id.Equals(subscription.OwnerId))
            throw new CashierException($"Subscription {subscription.Id} belongs to another owner.");
        if (subscription.Status != SubscriptionStatus.PastDue)
            throw new CashierException($"Subscription {subscription.Id} is not past due.");

        var outstanding = await NativeSubscriptions.OutstandingAsync(_db, subscription.Id, ct);
        if (outstanding.Count == 0)
            throw new CashierException($"Subscription {subscription.Id} has no outstanding payment.");

        string customerId = subscription.MollieCustomerId ?? owner.MollieCustomerId
            ?? throw new CashierException("Owner has no Mollie customer ID.");
        var oldest = outstanding[0];

        // An open settlement for the same payment is handed out again rather than a second one
        // created: two checkouts for one debt could both be paid.
        var openSettlements = await _db.Payments
            .Where(p => p.SettlesPaymentId == oldest.Id && (p.Status == "open" || p.Status == "pending"))
            .ToListAsync(ct);
        foreach (var open in openSettlements.OrderByDescending(p => p.Id))
        {
            var atMollie = await _mollieClient.GetPaymentAsync(open.MolliePaymentId, ct);
            string? href = atMollie.Links?.Checkout?.Href;
            if (atMollie.Status is "open" && href != null)
                return new OutstandingPaymentResult(href, oldest.Amount, outstanding.Count - 1);
        }

        // A first payment, not a recurring one: the mandate behind the failure may be the reason
        // for it, and the checkout lets the customer choose a payment method that works.
        var molliePayment = await _mollieClient.CreateFirstPaymentAsync(
            customerId, oldest.Amount, oldest.Currency,
            $"{subscription.Description ?? subscription.Plan}: outstanding payment",
            redirectUrl ?? _options.FirstPaymentRedirectUrl, _options.EffectiveWebhookUrl, metadata: null, ct);

        _db.Payments.Add(new Payment<TKey>
        {
            OwnerId = subscription.OwnerId,
            SubscriptionId = subscription.Id,
            MolliePaymentId = molliePayment.Id,
            Status = molliePayment.Status ?? "open",
            Amount = oldest.Amount,
            Currency = oldest.Currency,
            AmountNet = oldest.AmountNet,
            TaxRatePercent = oldest.TaxRatePercent,
            CreditNet = oldest.CreditNet,
            SettlesPaymentId = oldest.Id,
        });
        await _db.SaveChangesAsync(ct);

        string checkoutUrl = molliePayment.Links?.Checkout?.Href
            ?? throw new CashierException("Mollie did not return a checkout URL for the outstanding payment.");
        return new OutstandingPaymentResult(checkoutUrl, oldest.Amount, outstanding.Count - 1);
    }

    /// <inheritdoc />
    public async Task<EarlyEndResult> CancelImmediatelyWithRefundAsync(Subscription<TKey> subscription,
        CancellationToken ct = default)
    {
        if (!NativeSubscriptions.IsPriced(subscription))
            throw new CashierException($"Subscription {subscription.Id} has no price, so its unused value is unknown.");
        if (subscription.Status != SubscriptionStatus.Active || subscription.EndsAt.HasValue)
            throw new CashierException($"Subscription {subscription.Id} is not active; nothing is left to end or refund.");

        var now = DateTimeOffset.UtcNow;
        int days = subscription.CycleStartedAt.HasValue && subscription.CycleValueNet.HasValue
            ? Money.RemainingWholeDays(subscription.CycleStartedAt.Value, _options.CycleDays, now)
            : 0;
        decimal value = days == 0 ? 0m : Money.Prorate(subscription.CycleValueNet!.Value, days, _options.CycleDays);

        // Stop the charging first: a refund that fails afterwards leaves money owed, while the
        // opposite order could refund a cycle and then charge the next one.
        await CancelSubscriptionAsync(subscription, immediately: true, ct);

        if (value <= 0m)
            return new EarlyEndResult(days, 0m, 0m, 0m);

        try
        {
            var (gross, left) = await RefundNetAsync(subscription, value, ct);
            if (left > 0m)
                LogUnrefundedSurplus(subscription.Id, left);
            return new EarlyEndResult(days, value, gross, left);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The subscription is ended either way; what could not be refunded is reported, not
            // thrown, so the caller knows exactly what is still owed.
            LogUpgradeStepFailed(subscription.Id, "refund the unused days after ending", ex);
            return new EarlyEndResult(days, value, 0m, value);
        }
    }

    /// <inheritdoc />
    public Task ProcessDueItemsAsync(CancellationToken ct = default)
        => Task.CompletedTask; // No-op for Mollie native engine
}
