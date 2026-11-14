using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// Prices for tests: explicit entries win, any other plan costs <see cref="DefaultNet"/> net at
/// <see cref="DefaultRate"/> percent, so tests that are not about prices need no setup.
/// </summary>
public sealed class FakePlanPriceResolver<TKey> : IPlanPriceResolver<TKey> where TKey : IEquatable<TKey>
{
    public const decimal DefaultNet = 10m;
    public const decimal DefaultRate = 19m;

    private readonly Dictionary<string, PlanPrice> _prices = new(StringComparer.Ordinal);

    public string Currency { get; set; } = "EUR";

    public List<(TKey Owner, string Plan)> Calls { get; } = [];

    public FakePlanPriceResolver<TKey> With(string plan, decimal net, decimal rate = 19m, string? description = null)
    {
        _prices[plan] = new PlanPrice(net, Currency, rate, description ?? $"Plan {plan}");
        return this;
    }

    public Task<PlanPrice> ResolveAsync(TKey ownerId, string plan, CancellationToken ct = default)
    {
        Calls.Add((ownerId, plan));
        return Task.FromResult(_prices.TryGetValue(plan, out var p)
            ? p
            : new PlanPrice(DefaultNet, Currency, DefaultRate, $"Plan {plan}"));
    }
}
