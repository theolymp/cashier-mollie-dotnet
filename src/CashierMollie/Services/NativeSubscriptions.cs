using CashierMollie.Data;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Services;

/// <summary>
/// The steps the MollieNative engine and the webhook share, so that a subscription registered
/// at Mollie after a checkout and one registered directly by an upgrade are built the same way.
/// </summary>
internal static class NativeSubscriptions
{
    /// <summary>Metadata key carrying the local subscription id on the Mollie subscription.</summary>
    internal const string SubscriptionIdMetadataKey = "cashier_subscription_id";

    /// <summary>
    /// Placeholder written to <c>MollieSubscriptionId</c> while one webhook registers the
    /// subscription, so that a concurrent webhook loses on the concurrency token instead of
    /// registering a second Mollie subscription.
    /// </summary>
    internal const string RegisteringMarker = "registering";

    /// <summary>A claim older than this is taken to be from a process that died mid-registration.</summary>
    internal static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(10);

    /// <summary>True when the subscription has a real Mollie id, not none and not a claim.</summary>
    internal static bool IsRegistered<TKey>(Subscription<TKey> subscription) where TKey : IEquatable<TKey> =>
        !string.IsNullOrEmpty(subscription.MollieSubscriptionId)
        && subscription.MollieSubscriptionId != RegisteringMarker;

    /// <summary>The Mollie interval for one cycle.</summary>
    internal static string Interval(CashierMollieOptions options) => BillingCycle.MollieInterval(options);

    /// <summary>
    /// True when the subscription carries a price and should therefore be charged by Mollie.
    /// Subscriptions from before 0.6.0, and mandate-only ones, have none and stay local.
    /// </summary>
    internal static bool IsPriced<TKey>(Subscription<TKey> subscription) where TKey : IEquatable<TKey> =>
        subscription.PriceNet.HasValue;

    /// <summary>The gross amount Mollie charges per cycle for the subscription's current price.</summary>
    internal static decimal GrossPerCycle<TKey>(Subscription<TKey> subscription) where TKey : IEquatable<TKey> =>
        Money.Gross(subscription.PriceNet ?? throw new InvalidOperationException("Subscription has no price."),
            subscription.TaxRatePercent ?? 0m);

    /// <summary>Copies a resolved price onto the subscription.</summary>
    internal static void ApplyPrice<TKey>(Subscription<TKey> subscription, PlanPrice price)
        where TKey : IEquatable<TKey>
    {
        subscription.PriceNet = price.Net;
        subscription.TaxRatePercent = price.TaxRatePercent;
        subscription.Description = price.Description;
    }

    /// <summary>
    /// Resolves a price and refuses one this library must not send: a currency other than the
    /// configured one, a non-positive amount, a tax rate outside 0..100 or an empty description.
    /// </summary>
    internal static async Task<PlanPrice> ResolveCheckedAsync<TKey>(IPlanPriceResolver<TKey> resolver,
        CashierMollieOptions options, TKey ownerId, string plan, CancellationToken ct)
        where TKey : IEquatable<TKey>
    {
        var price = await resolver.ResolveAsync(ownerId, plan, ct);
        if (!string.Equals(price.Currency, options.Currency, StringComparison.Ordinal))
        {
            throw new CashierException(
                $"Plan '{plan}' resolved to currency '{price.Currency}', but CashierMollie is configured "
                + $"for '{options.Currency}'. A different currency is rejected, not passed through.");
        }

        if (price.Net <= 0m)
        {
            throw new CashierException($"Plan '{plan}' resolved to a net amount of {price.Net}; it must be positive.");
        }

        if (price.TaxRatePercent is < 0m or > 100m)
        {
            throw new CashierException($"Plan '{plan}' resolved to a tax rate of {price.TaxRatePercent} %.");
        }

        if (string.IsNullOrWhiteSpace(price.Description))
        {
            throw new CashierException($"Plan '{plan}' resolved without a description.");
        }

        return price;
    }

    /// <summary>Mollie statuses after which a payment brought in no money.</summary>
    internal static bool IsFailedStatus(string? status) => status is "failed" or "canceled" or "expired";

    /// <summary>
    /// Priced payments of the subscription (first cycle, upgrade or Mollie's own charge) whose
    /// status is failed, canceled or expired and that no PAID settlement points at, oldest first.
    /// Decided from the stored status, not from timestamps: a SEPA debit can be reported paid and
    /// then fail, and its PaidAt says nothing about the money any more. A failed settlement attempt
    /// is not itself a debt; the payment it was meant to settle still is.
    /// </summary>
    internal static async Task<List<Payment<TKey>>> OutstandingAsync<TKey>(CashierDbContext<TKey> db,
        long subscriptionId, CancellationToken ct) where TKey : IEquatable<TKey>
    {
        var failed = (await db.Payments
                .Where(p => p.SubscriptionId == subscriptionId && p.AmountNet != null && p.SettlesPaymentId == null
                    && (p.Status == "failed" || p.Status == "canceled" || p.Status == "expired"))
                .ToListAsync(ct))
            // Ordered in memory by id: SQLite cannot ORDER BY a DateTimeOffset.
            .OrderBy(p => p.Id)
            .ToList();
        if (failed.Count == 0)
            return failed;

        var failedIds = failed.Select(f => f.Id).ToList();
        var settled = await db.Payments
            .Where(p => p.SettlesPaymentId != null && failedIds.Contains(p.SettlesPaymentId.Value) && p.Status == "paid")
            .Select(p => p.SettlesPaymentId!.Value)
            .ToListAsync(ct);
        return failed.Where(f => !settled.Contains(f.Id)).ToList();
    }

    /// <summary>The mandate of the owner's most recent payment that carries one, or null.</summary>
    internal static Task<string?> LatestMandateAsync<TKey>(CashierDbContext<TKey> db, TKey ownerId,
        CancellationToken ct) where TKey : IEquatable<TKey> =>
        db.Payments
            .Where(p => p.OwnerId!.Equals(ownerId) && p.MollieMandateId != null)
            // By id rather than CreatedAt: same order, and SQLite cannot sort a DateTimeOffset.
            .OrderByDescending(p => p.Id)
            .Select(p => p.MollieMandateId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Registers the subscription at Mollie: its gross price every cycle, the first charge on
    /// <paramref name="startDate"/>, whose day of the month becomes the anchor of monthly cycles.
    /// Stores the Mollie id and the anchor on the subscription; the caller saves.
    /// </summary>
    internal static async Task StartAtMollieAsync<TKey>(IMollieClientService mollie,
        CashierMollieOptions options, Subscription<TKey> subscription, string mandateId,
        DateTimeOffset startDate, CancellationToken ct) where TKey : IEquatable<TKey>
    {
        if (string.IsNullOrEmpty(subscription.MollieCustomerId))
        {
            throw new CashierException(
                $"Subscription {subscription.Id} has no Mollie customer; it cannot be registered at Mollie.");
        }

        var response = await mollie.CreateSubscriptionAsync(
            subscription.MollieCustomerId,
            GrossPerCycle(subscription),
            options.Currency,
            Interval(options),
            subscription.Description ?? subscription.Plan,
            options.EffectiveWebhookUrl,
            mandateId,
            new Dictionary<string, string>
            {
                [SubscriptionIdMetadataKey] = subscription.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            startDate,
            ct);

        subscription.MollieSubscriptionId = response.Id;
        subscription.CycleAnchorDay = startDate.UtcDateTime.Day;
    }

    /// <summary>When the paid cycle of <paramref name="subscription"/> ends, from its anchor.</summary>
    internal static DateTimeOffset CycleEnd<TKey>(Subscription<TKey> subscription, DateTimeOffset cycleStartedAt,
        CashierMollieOptions options) where TKey : IEquatable<TKey> =>
        BillingCycle.End(cycleStartedAt, subscription.CycleAnchorDay, options);

    /// <summary>The unused value of the paid cycle at <paramref name="now"/>, and its whole days left.</summary>
    internal static (int Days, decimal Value) UnusedValue<TKey>(Subscription<TKey> subscription,
        CashierMollieOptions options, DateTimeOffset now) where TKey : IEquatable<TKey>
    {
        if (!subscription.CycleStartedAt.HasValue || !subscription.CycleValueNet.HasValue)
            return (0, 0m);
        var (remaining, length) = BillingCycle.RemainingDays(subscription.CycleStartedAt.Value,
            subscription.CycleAnchorDay, options, now);
        return remaining == 0 ? (0, 0m) : (remaining, Money.Prorate(subscription.CycleValueNet.Value, remaining, length));
    }
}
