using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// One pass of the dunning schedule (<see cref="DunningOptions"/>) over every <c>past_due</c>
/// subscription: reminder events on the reminder days, and the cancellation on the cancel day (at
/// Mollie when the subscription is registered there, locally otherwise). It never charges anything. Run by <see cref="DunningBackgroundService{TKey}"/>;
/// public so a consumer with its own scheduler can call <see cref="ProcessAsync"/> instead.
/// </summary>
/// <remarks>
/// Safe with several instances running at once: each step is claimed by saving the row first
/// (its <c>RowVersion</c> is a concurrency token), and only the instance whose save went through
/// acts. A reminder is therefore dispatched at most once; if the dispatch itself fails it is
/// logged, not repeated.
/// </remarks>
public partial class DunningService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly IMollieClientService _mollie;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly DunningOptions _options;
    private readonly ILogger<DunningService<TKey>> _logger;
    private readonly TimeProvider _time = TimeProvider.System;

    /// <summary>Initializes a new instance of <see cref="DunningService{TKey}"/>.</summary>
    public DunningService(CashierDbContext<TKey> db, IMollieClientService mollie,
        ICashierEventDispatcher eventDispatcher, IOptions<CashierMollieOptions> options,
        ILogger<DunningService<TKey>> logger)
    {
        _db = db;
        _mollie = mollie;
        _eventDispatcher = eventDispatcher;
        _options = options.Value.Dunning;
        _logger = logger;
    }

    /// <summary>
    /// As the constructor without it, with the clock of the dunning schedule: when a subscription
    /// becomes <c>past_due</c>, the reminder and cancel days, and the grace period of
    /// <c>HasAccess</c>. Billing cycles still read the system clock.
    /// </summary>
    public DunningService(CashierDbContext<TKey> db, IMollieClientService mollie,
        ICashierEventDispatcher eventDispatcher, IOptions<CashierMollieOptions> options,
        ILogger<DunningService<TKey>> logger, TimeProvider time)
        : this(db, mollie, eventDispatcher, options, logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Runs one pass. Returns how many reminders and cancellations it dispatched.</summary>
    public async Task<int> ProcessAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return 0;

        var pastDue = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.PastDue)
            .ToListAsync(ct);

        int acted = 0;
        foreach (var subscription in pastDue)
        {
            try
            {
                if (await ProcessOneAsync(subscription, ct))
                    acted++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another instance (or a webhook) changed the row first. It will be looked at
                // again on the next pass with what is stored then.
                _db.Entry(subscription).State = EntityState.Detached;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSubscriptionFailed(subscription.Id, ex);
                _db.Entry(subscription).State = EntityState.Detached;
            }
        }

        return acted;
    }

    private async Task<bool> ProcessOneAsync(Subscription<TKey> subscription, CancellationToken ct)
    {
        var outstanding = await NativeSubscriptions.OutstandingAsync(_db, subscription.Id, ct);
        if (outstanding.Count == 0)
            return false; // nothing owed; the webhook that settled it reconciles the status

        var now = _time.GetUtcNow();
        var reminderDays = _options.ReminderDays.Order().ToList();

        if (subscription.PastDueSince is null)
        {
            // past_due from before 0.6.0: the date is taken from the oldest outstanding payment,
            // and reminders whose day has already passed count as sent, so enabling dunning does
            // not send a burst of them.
            var oldest = outstanding[0];
            subscription.PastDueSince = oldest.FailedAt ?? oldest.CreatedAt;
            subscription.DunningRemindersSent = reminderDays.Count(d => d <= DaysSince(subscription.PastDueSince.Value, now));
            await _db.SaveChangesAsync(ct);
        }

        int days = DaysSince(subscription.PastDueSince.Value, now);
        decimal total = outstanding.Sum(p => p.Amount);

        if (days >= _options.CancelAfterDays)
        {
            if (subscription.EndsAt.HasValue)
                return false; // already cancelled; the debt stays payable, nothing more to send
            return await CancelForNonPaymentAsync(subscription, total, now, ct);
        }

        int due = reminderDays.Count(d => d <= days);
        if (due <= subscription.DunningRemindersSent)
            return false;

        subscription.DunningRemindersSent = due;
        await _db.SaveChangesAsync(ct); // the claim: only one instance gets past this
        bool accessEnded = !DunningPolicy.HasAccess(subscription, _options, now);
        await DispatchAsync(new DunningReminderDue<TKey>(subscription, outstanding[0], total, days, due,
            accessEnded, subscription.OwnerId), subscription.Id, ct);
        return true;
    }

    private async Task<bool> CancelForNonPaymentAsync(Subscription<TKey> subscription, decimal total,
        DateTimeOffset now, CancellationToken ct)
    {
        // Mollie first, and nothing local before it: if the call fails or the process dies, the
        // row is unchanged and the next pass tries again. Mollie's state is checked first, so a
        // pass after a cancellation that went through but was never saved does not cancel twice.
        if (!string.IsNullOrEmpty(subscription.MollieCustomerId) && NativeSubscriptions.IsRegistered(subscription))
        {
            var atMollie = await _mollie.GetSubscriptionAsync(subscription.MollieCustomerId,
                subscription.MollieSubscriptionId!, ct);
            if (!string.Equals(atMollie.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                await _mollie.CancelSubscriptionAsync(subscription.MollieCustomerId, subscription.MollieSubscriptionId!, ct);
        }

        // As a cancellation of a past_due subscription does: it ends now and stays past_due, so
        // the debt is still payable. The save is the claim: a second instance gets a concurrency
        // conflict here and dispatches nothing.
        subscription.EndsAt = now;
        await _db.SaveChangesAsync(ct);

        await DispatchAsync(new SubscriptionCancelled<TKey>(subscription, subscription.OwnerId), subscription.Id, ct);
        await DispatchAsync(new SubscriptionCancelledForNonPayment<TKey>(subscription, total, subscription.OwnerId),
            subscription.Id, ct);
        return true;
    }

    private async Task DispatchAsync<TEvent>(TEvent @event, long subscriptionId, CancellationToken ct)
        where TEvent : notnull
    {
        try
        {
            await _eventDispatcher.DispatchAsync(@event, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already recorded as done; repeating it on the next pass could send the same mail twice.
            LogDispatchFailed(subscriptionId, ex);
        }
    }

    private static int DaysSince(DateTimeOffset since, DateTimeOffset now) =>
        (int)Math.Floor((now - since).TotalDays);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dunning failed for subscription {SubscriptionId}")]
    private partial void LogSubscriptionFailed(long subscriptionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Dunning event for subscription {SubscriptionId} was recorded but its dispatch failed")]
    private partial void LogDispatchFailed(long subscriptionId, Exception ex);
}

/// <summary>The access rule of the dunning schedule, shared by the job and <c>ICashierService.HasAccess</c>.</summary>
public static class DunningPolicy
{
    /// <summary>
    /// True while the customer should have access: an active subscription, one cancelled but not
    /// yet ended, or a <c>past_due</c> one within <see cref="DunningOptions.GraceDays"/> of
    /// <see cref="Subscription{TKey}.PastDueSince"/>. A <c>past_due</c> row without that date has
    /// no access (fail-closed). Use this instead of <see cref="Subscription{TKey}.IsActive"/> for
    /// access checks: <c>IsActive</c> is false from the first failed payment.
    /// </summary>
    public static bool HasAccess<TKey>(Subscription<TKey> subscription, DunningOptions options, DateTimeOffset now)
        where TKey : IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(options);
        if (subscription.EndsAt is { } ends && ends <= now)
            return false;

        return subscription.Status switch
        {
            SubscriptionStatus.Active or SubscriptionStatus.Cancelled => true,
            // Compared as a duration: AddDays would throw on an absurd GraceDays.
            SubscriptionStatus.PastDue => subscription.PastDueSince is { } since
                && (now - since).TotalDays < options.GraceDays,
            _ => false,
        };
    }
}
