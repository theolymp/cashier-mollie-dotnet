using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Services;

public class WebhookService<TKey> : IWebhookService where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;

    public WebhookService(
        CashierDbContext<TKey> db,
        IMollieClientService mollieClient,
        ICashierEventDispatcher eventDispatcher)
    {
        _db = db;
        _mollieClient = mollieClient;
        _eventDispatcher = eventDispatcher;
    }

    public async Task HandlePaymentAsync(string molliePaymentId, CancellationToken ct = default)
    {
        // Fetch payment details from Mollie
        var molliePayment = await _mollieClient.GetPaymentAsync(molliePaymentId, ct);

        // Find local payment record
        var localPayment = await _db.Payments
            .FirstOrDefaultAsync(p => p.MolliePaymentId == molliePaymentId, ct);

        if (localPayment == null)
            return; // Unknown payment, ignore

        // Check if there's anything new to process
        bool statusChanged = localPayment.Status != molliePayment.Status;
        bool hasNewChargeback = molliePayment.AmountChargedBack != null
            && decimal.Parse(molliePayment.AmountChargedBack.Value,
                System.Globalization.CultureInfo.InvariantCulture) > localPayment.AmountChargedBack;

        if (!statusChanged && !hasNewChargeback)
            return;

        // Update local record
        localPayment.Status = molliePayment.Status ?? localPayment.Status;
        localPayment.MollieMandateId = molliePayment.MandateId ?? localPayment.MollieMandateId;

        if (molliePayment.Status == "paid")
        {
            localPayment.PaidAt = DateTimeOffset.UtcNow;
        }
        else if (molliePayment.Status is "failed" or "canceled" or "expired")
        {
            localPayment.FailedAt = DateTimeOffset.UtcNow;
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
            subscription.CycleStartedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _eventDispatcher.DispatchAsync(
                new SubscriptionCreated<TKey>(subscription, localPayment.OwnerId), ct);
        }

        // Check for chargebacks
        if (molliePayment.AmountChargedBack != null)
        {
            decimal chargebackAmount = decimal.Parse(molliePayment.AmountChargedBack.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (chargebackAmount > localPayment.AmountChargedBack)
            {
                decimal newChargeback = chargebackAmount - localPayment.AmountChargedBack;
                localPayment.AmountChargedBack = chargebackAmount;
                await _db.SaveChangesAsync(ct);

                await _eventDispatcher.DispatchAsync(
                    new ChargebackReceived<TKey>(localPayment, newChargeback,
                        localPayment.Currency, localPayment.OwnerId), ct);
            }
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
}
