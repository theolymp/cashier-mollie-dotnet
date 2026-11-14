using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>
/// Turns a plan id into what one cycle costs a given owner. The default,
/// <see cref="Services.ConfigPlanPriceResolver{TKey}"/>, reads prices from configuration;
/// replace it in DI to price from a catalogue and to apply the owner's tax treatment.
/// </summary>
/// <remarks>
/// Called when a subscription is created, upgraded or swapped, and for every subscription of an
/// owner by <see cref="ICashierService{TKey}.RepriceAsync"/>. It is not called before each
/// recurring charge: on the MollieNative engine Mollie charges on its own schedule, so a change in
/// price or tax treatment reaches Mollie only through one of those calls.
/// </remarks>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public interface IPlanPriceResolver<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>Resolves the price of <paramref name="plan"/> for <paramref name="ownerId"/>.</summary>
    /// <exception cref="Exceptions.CashierException">The plan is unknown.</exception>
    Task<PlanPrice> ResolveAsync(TKey ownerId, string plan, CancellationToken ct = default);
}
