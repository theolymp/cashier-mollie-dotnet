using System.Globalization;
using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;

namespace CashierMollie.Services;

/// <summary>
/// Processes Mollie's payment webhooks: status, activation, chargebacks, and since 0.6.0 the
/// payments Mollie creates on its own for a registered subscription.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public partial class WebhookService<TKey> : IWebhookService where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierMollieOptions _options;
    private readonly ILogger<WebhookService<TKey>> _logger;
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>Initializes a new instance of <see cref="WebhookService{TKey}"/>.</summary>
    public WebhookService(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options,
        ILogger<WebhookService<TKey>> logger)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// As the constructor without it, with the clock of the dunning schedule: when a subscription
    /// becomes <c>past_due</c>, the reminder and cancel days, and the grace period of
    /// <c>HasAccess</c>. Billing cycles still read the system clock.
    /// </summary>
    public WebhookService(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher,
        IOptions<CashierMollieOptions> options,
        ILogger<WebhookService<TKey>> logger,
        TimeProvider time)
        : this(db, mollieClient, eventDispatcher, options, logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Mollie charged {Charged} for subscription {SubscriptionId}, but its price is {Expected} "
            + "gross. The payment is recorded at the stored net and tax rate; check the amount at Mollie.")]
    private partial void LogCycleAmountMismatch(long subscriptionId, decimal charged, decimal expected);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Mollie charged ended subscription {SubscriptionId} with payment {PaymentId}; cancelling "
            + "it at Mollie again. The payment is recorded and may need a refund.")]
    private partial void LogChargedAfterEnd(long subscriptionId, string paymentId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Subscription {SubscriptionId}: the outstanding amount was paid, but the Mollie "
            + "subscription could not be moved to the new mandate; its next charge may fail again.")]
    private partial void LogMandateSwitchFailed(long subscriptionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not cancel ended subscription {SubscriptionId} at Mollie again.")]
    private partial void LogCancelAgainFailed(long subscriptionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Webhook for payment {PaymentId}, which Mollie does not know (404); ignored.")]
    private partial void LogUnknownPayment(string paymentId);

    /// <inheritdoc />
    public async Task HandlePaymentAsync(string molliePaymentId, CancellationToken ct = default)
    {
        // Fetch payment details from Mollie. The webhook is reachable by anyone, so an id Mollie
        // does not know is someone else's guess, not a failure: nothing to do, and nothing for
        // Mollie to retry. Every other error (401, 5xx, timeout) still throws, so Mollie retries.
        PaymentResponse molliePayment;
        try
        {
            molliePayment = await _mollieClient.GetPaymentAsync(molliePaymentId, ct);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            LogUnknownPayment(molliePaymentId);
            return;
        }

        // Find local payment record
        var localPayment = await _db.Payments
            .FirstOrDefaultAsync(p => p.MolliePaymentId == molliePaymentId, ct);

        // A payment Mollie created by itself for a registered subscription is unknown locally
        // until its first webhook. Before 0.6.0 every such payment was ignored here; no library
        // path registered a subscription at Mollie, so none ever arrived.
        bool isCyclePayment = false;
        if (localPayment == null && !string.IsNullOrEmpty(molliePayment.SubscriptionId))
        {
            localPayment = await RecordCyclePaymentAsync(molliePayment, ct);
            isCyclePayment = localPayment != null;
        }
        else if (localPayment != null && !string.IsNullOrEmpty(molliePayment.SubscriptionId))
        {
            isCyclePayment = true;
        }

        if (localPayment == null)
            return; // Unknown payment, ignore

        // Parse chargeback amount once (reused in idempotency check and processing)
        decimal? mollieChargebackAmount = molliePayment.AmountChargedBack != null
            ? decimal.Parse(molliePayment.AmountChargedBack.Value, CultureInfo.InvariantCulture)
            : null;

        // Check if there's anything new to process
        bool statusChanged = localPayment.Status != molliePayment.Status;
        bool hasNewChargeback = mollieChargebackAmount > localPayment.AmountChargedBack;

        if (!statusChanged && !hasNewChargeback)
        {
            // A repeat of a paid webhook still finishes a registration that failed the first
            // time: Mollie retries the webhook when we answer with an error, and the status was
            // already saved by then.
            if (molliePayment.Status == "paid")
                await EnsureRegisteredAtMollieAsync(localPayment, ct);

            // The same goes for the payment-default state: it is derived from what is stored, so a
            // retry after a failure between the two saves still locks or unlocks correctly.
            if (localPayment.SubscriptionId.HasValue
                && await _db.Subscriptions.FindAsync([localPayment.SubscriptionId.Value], ct) is { } retried)
            {
                await ReconcileDefaultAsync(retried, localPayment, ct);
            }

            return;
        }

        // Update local record
        localPayment.Status = molliePayment.Status ?? localPayment.Status;
        localPayment.MollieMandateId = molliePayment.MandateId ?? localPayment.MollieMandateId;

        if (molliePayment.Status == "paid")
        {
            localPayment.PaidAt = DateTimeOffset.UtcNow;
        }
        else if (NativeSubscriptions.IsFailedStatus(molliePayment.Status))
        {
            localPayment.FailedAt = _time.GetUtcNow();
            // A SEPA debit can be reported paid and fail days later. It brought in no money, so it
            // must not keep counting as paid anywhere.
            localPayment.PaidAt = null;
        }

        await _db.SaveChangesAsync(ct);

        // Load related subscription if exists
        Subscription<TKey>? subscription = null;
        if (localPayment.SubscriptionId.HasValue)
            subscription = await _db.Subscriptions.FindAsync([localPayment.SubscriptionId.Value], ct);

        // Capture whether the subscription was pending before we potentially activate it
        bool wasPending = subscription?.Status == SubscriptionStatus.Pending;

        // Activate pending subscription on successful first payment
        if (molliePayment.Status == "paid" && subscription != null
            && subscription.Status == SubscriptionStatus.Pending)
        {
            subscription.Status = SubscriptionStatus.Active;
            bool onTrial = subscription.TrialEndsAt > DateTimeOffset.UtcNow;
            if (!onTrial)
            {
                subscription.CycleStartedAt = DateTimeOffset.UtcNow;
                subscription.CycleValueNet = localPayment.AmountNet;
            }

            await _db.SaveChangesAsync(ct);
            await EnsureRegisteredAtMollieAsync(localPayment, ct);

            await _eventDispatcher.DispatchAsync(
                new SubscriptionCreated<TKey>(subscription, localPayment.OwnerId), ct);
        }
        else if (molliePayment.Status == "paid" && subscription != null && !isCyclePayment)
        {
            // A first cycle charged from a mandate (create or upgrade): the subscription is already
            // active, and this completes its registration at Mollie if that failed at the time.
            await EnsureRegisteredAtMollieAsync(localPayment, ct);
        }
        else if (molliePayment.Status == "paid" && subscription != null && isCyclePayment && statusChanged)
        {
            // A new cycle Mollie charged on its own. A downgrade sent earlier takes effect here:
            // this is the first charge at the new price. The cycle starts on the day it was due
            // (Mollie creates the charge on its date), not on the day a late SEPA debit arrived.
            // Only a due date after the previous cycle start and not in the future is believed.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            subscription.CycleStartedAt = DueDate(molliePayment.CreatedAt) is { } due
                && due <= now && (subscription.CycleStartedAt is not { } previous || due > previous)
                ? due
                : now;
            subscription.CycleValueNet = localPayment.AmountNet;
            if (subscription.NextPlan != null)
            {
                subscription.Plan = subscription.NextPlan;
                subscription.NextPlan = null;
            }

            await _db.SaveChangesAsync(ct);
        }

        // A first payment that fails while the subscription is still pending is not a debt: the
        // subscription never started. Its net is cleared so it is never counted as outstanding.
        if (subscription?.Status == SubscriptionStatus.Pending && NativeSubscriptions.IsFailedStatus(molliePayment.Status)
            && localPayment.AmountNet != null)
        {
            localPayment.AmountNet = null;
            localPayment.TaxRatePercent = null;
            await _db.SaveChangesAsync(ct);
        }

        if (subscription != null)
            await ReconcileDefaultAsync(subscription, localPayment, ct);

        // Check for chargebacks
        if (mollieChargebackAmount != null && mollieChargebackAmount > localPayment.AmountChargedBack)
        {
            decimal newChargeback = mollieChargebackAmount.Value - localPayment.AmountChargedBack;
            localPayment.AmountChargedBack = mollieChargebackAmount.Value;
            await _db.SaveChangesAsync(ct);

            await _eventDispatcher.DispatchAsync(
                new ChargebackReceived<TKey>(localPayment, newChargeback,
                    localPayment.Currency, localPayment.OwnerId), ct);
        }

        // Dispatch payment events
        if (molliePayment.Status == "paid")
        {
            await _eventDispatcher.DispatchAsync(
                new OrderPaymentPaid<TKey>(localPayment, subscription, localPayment.OwnerId), ct);
        }
        else if (molliePayment.Status is "failed" or "canceled" or "expired")
        {
            await _eventDispatcher.DispatchAsync(
                new OrderPaymentFailed<TKey>(localPayment, subscription, localPayment.OwnerId), ct);
        }

        // Dispatch FirstPaymentPaid/Failed for subscriptions that were pending
        if (subscription != null && wasPending)
        {
            if (molliePayment.Status == "paid")
            {
                await _eventDispatcher.DispatchAsync(
                    new FirstPaymentPaid<TKey>(localPayment, localPayment.OwnerId), ct);
            }
            else if (molliePayment.Status is "failed" or "canceled" or "expired")
            {
                await _eventDispatcher.DispatchAsync(
                    new FirstPaymentFailed<TKey>(localPayment, localPayment.OwnerId), ct);
            }
        }
    }

    /// <summary>
    /// Locks or unlocks a priced subscription from its stored payments, never from the status
    /// change alone: <c>past_due</c> while a failed priced payment is not settled, active again once
    /// none is. A paid settlement also moves the paid cycle forward to the one it settles, and moves
    /// the Mollie subscription to the mandate it was paid with.
    /// </summary>
    private async Task ReconcileDefaultAsync(Subscription<TKey> subscription, Payment<TKey> payment, CancellationToken ct)
    {
        // On Managed the price lives on the order item, not the subscription; its charges carry
        // their net on the payment row, which is what OutstandingAsync counts.
        if ((!NativeSubscriptions.IsPriced(subscription) && _options.BillingEngine != BillingEngineType.Managed)
            || (subscription.Status != SubscriptionStatus.Active && subscription.Status != SubscriptionStatus.PastDue))
        {
            return;
        }

        if (payment.Status == "paid" && payment.SettlesPaymentId.HasValue)
        {
            var settled = await _db.Payments.FindAsync([payment.SettlesPaymentId.Value], ct);
            if (settled?.AmountNet != null
                && (subscription.CycleStartedAt == null || settled.CreatedAt > subscription.CycleStartedAt))
            {
                subscription.CycleStartedAt = settled.CreatedAt;
                subscription.CycleValueNet = settled.AmountNet + settled.CreditNet;
                await _db.SaveChangesAsync(ct);
            }

            if (subscription.Status == SubscriptionStatus.PastDue && !subscription.EndsAt.HasValue
                && !string.IsNullOrEmpty(payment.MollieMandateId)
                && NativeSubscriptions.IsRegistered(subscription) && !string.IsNullOrEmpty(subscription.MollieCustomerId))
            {
                try
                {
                    await _mollieClient.UpdateSubscriptionMandateAsync(subscription.MollieCustomerId,
                        subscription.MollieSubscriptionId!, payment.MollieMandateId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LogMandateSwitchFailed(subscription.Id, ex);
                }
            }
        }

        var outstanding = await NativeSubscriptions.OutstandingAsync(_db, subscription.Id, ct);
        if (outstanding.Count > 0 && subscription.Status == SubscriptionStatus.Active)
        {
            subscription.Status = SubscriptionStatus.PastDue;
            subscription.PastDueSince = _time.GetUtcNow();
            subscription.DunningRemindersSent = 0;
            await _db.SaveChangesAsync(ct);
            bool neverPaid = !await _db.Payments.AnyAsync(
                p => p.SubscriptionId == subscription.Id && p.Status == "paid", ct);
            await _eventDispatcher.DispatchAsync(
                new SubscriptionPastDue<TKey>(subscription, outstanding[^1], subscription.OwnerId) { NeverPaid = neverPaid }, ct);
        }
        else if (outstanding.Count == 0 && subscription.Status == SubscriptionStatus.PastDue)
        {
            // Cancelled while past_due: settling the debt does not bring it back.
            subscription.Status = subscription.EndsAt.HasValue ? SubscriptionStatus.Cancelled : SubscriptionStatus.Active;
            subscription.PastDueSince = null;
            subscription.DunningRemindersSent = 0;
            await _db.SaveChangesAsync(ct);
            await _eventDispatcher.DispatchAsync(
                new SubscriptionPaymentRecovered<TKey>(subscription, payment, subscription.OwnerId), ct);
        }
    }

    /// <summary>
    /// Creates the local record of a payment Mollie made for a registered subscription, at the
    /// net and tax rate stored on the subscription. Null when no local subscription has that
    /// Mollie id.
    /// </summary>
    private async Task<Payment<TKey>?> RecordCyclePaymentAsync(
        Mollie.Api.Models.Payment.Response.PaymentResponse molliePayment, CancellationToken ct)
    {
        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.MollieSubscriptionId == molliePayment.SubscriptionId, ct);
        if (subscription == null)
            return null;

        // Mollie created this payment after the subscription ended: a cancellation at Mollie that
        // failed (an upgrade reports it in Incomplete). Cancel again; the payment is still
        // recorded, and OrderPaymentPaid carries the ended subscription for the consumer to refund.
        if (subscription.EndsAt.HasValue && molliePayment.CreatedAt > subscription.EndsAt.Value.UtcDateTime
            && !string.IsNullOrEmpty(subscription.MollieCustomerId))
        {
            LogChargedAfterEnd(subscription.Id, molliePayment.Id);
            try
            {
                await _mollieClient.CancelSubscriptionAsync(subscription.MollieCustomerId,
                    molliePayment.SubscriptionId!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogCancelAgainFailed(subscription.Id, ex);
            }
        }

        decimal charged = decimal.Parse(molliePayment.Amount.Value, CultureInfo.InvariantCulture);
        if (subscription.PriceNet.HasValue)
        {
            decimal expected = NativeSubscriptions.GrossPerCycle(subscription);
            if (charged != expected)
                LogCycleAmountMismatch(subscription.Id, charged, expected);
        }

        var payment = new Payment<TKey>
        {
            OwnerId = subscription.OwnerId,
            SubscriptionId = subscription.Id,
            MolliePaymentId = molliePayment.Id,
            // Recorded as open so that the status comparison below sees the real status as new.
            Status = "open",
            Amount = charged,
            Currency = molliePayment.Amount.Currency,
            AmountNet = subscription.PriceNet,
            TaxRatePercent = subscription.TaxRatePercent,
            MollieMandateId = molliePayment.MandateId,
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>
    /// Registers a priced MollieNative subscription at Mollie once the payment that activated it
    /// is paid. Idempotent: it does nothing for a subscription that is already registered, has
    /// no price, or runs on the Managed engine.
    /// </summary>
    /// <summary>Mollie answered 404: from the API client, or from the in-memory fake.</summary>
    private static bool IsNotFound(Exception ex) => ex switch
    {
        Mollie.Api.Client.MollieApiException { Details.Status: 404 } => true,
        Testing.FakeMollieException { Status: 404 } => true,
        _ => false,
    };

    /// <summary>Mollie's creation time as UTC, whatever kind the deserialiser gave it.</summary>
    private static DateTimeOffset? DueDate(DateTime? createdAt) => createdAt switch
    {
        null => null,
        { Kind: DateTimeKind.Local } d => new DateTimeOffset(d.ToUniversalTime(), TimeSpan.Zero),
        { } d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc), TimeSpan.Zero),
    };

    private async Task EnsureRegisteredAtMollieAsync(Payment<TKey> payment, CancellationToken ct)
    {
        if (_options.BillingEngine != BillingEngineType.MollieNative || !payment.SubscriptionId.HasValue)
            return;

        var subscription = await _db.Subscriptions.FindAsync([payment.SubscriptionId.Value], ct);
        var now = DateTimeOffset.UtcNow;
        bool staleClaim = subscription?.MollieSubscriptionId == NativeSubscriptions.RegisteringMarker
            && subscription.UpdatedAt <= now - NativeSubscriptions.StaleClaim;
        if (subscription == null
            || subscription.Status != SubscriptionStatus.Active
            || subscription.EndsAt.HasValue
            || !NativeSubscriptions.IsPriced(subscription)
            || (!string.IsNullOrEmpty(subscription.MollieSubscriptionId) && !staleClaim))
        {
            return;
        }

        string? mandateId = payment.MollieMandateId
            ?? await NativeSubscriptions.LatestMandateAsync(_db, subscription.OwnerId, ct);
        if (string.IsNullOrEmpty(mandateId))
            return;

        // Claim first. Two webhooks for the same payment can arrive together; the one whose save
        // loses on the concurrency token stops here, before Mollie is asked for anything, instead
        // of registering a second subscription that would charge the customer twice per cycle.
        subscription.MollieSubscriptionId = NativeSubscriptions.RegisteringMarker;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return;
        }

        DateTimeOffset start = subscription.TrialEndsAt > now
            ? subscription.TrialEndsAt.Value
            : subscription.CycleStartedAt is { } started
                ? NativeSubscriptions.CycleEnd(subscription, started, _options)
                : BillingCycle.After(now, _options);

        try
        {
            await NativeSubscriptions.StartAtMollieAsync(_mollieClient, _options, subscription, mandateId, start, ct);
        }
        catch
        {
            // Release the claim so Mollie's retry of this webhook can try again.
            subscription.MollieSubscriptionId = null;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        await _db.SaveChangesAsync(ct);
    }
}
