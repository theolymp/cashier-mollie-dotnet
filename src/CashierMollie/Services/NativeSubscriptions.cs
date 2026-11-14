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

    /// <summary>The Mollie interval for one cycle.</summary>
    internal static string Interval(CashierMollieOptions options) => $"{options.CycleDays} days";

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

    /// <summary>The mandate of the owner's most recent payment that carries one, or null.</summary>
    internal static Task<string?> LatestMandateAsync<TKey>(CashierDbContext<TKey> db, TKey ownerId,
        CancellationToken ct) where TKey : IEquatable<TKey> =>
        db.Payments
            .Where(p => p.OwnerId!.Equals(ownerId) && p.MollieMandateId != null)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => p.MollieMandateId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Registers the subscription at Mollie: its gross price every <c>CycleDays</c> days, the
    /// first charge on <paramref name="startDate"/>. Stores the Mollie id on the subscription;
    /// the caller saves.
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
    }
}
