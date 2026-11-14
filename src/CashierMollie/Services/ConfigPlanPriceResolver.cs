using System.Globalization;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Default <see cref="IPlanPriceResolver{TKey}"/>: prices from the <c>CashierMollie:Plans</c>
/// configuration section, the same for every owner.
/// </summary>
/// <remarks>
/// <code>
/// "CashierMollie": {
///   "DefaultTaxRatePercent": 19,
///   "Plans": {
///     "encore": { "Amount": "9.99", "Description": "Encore Suite, monthly" },
///     "shield": { "Amount": "7.99", "Description": "Shield Suite, monthly", "TaxRatePercent": 0 }
///   }
/// }
/// </code>
/// <c>Amount</c> is net and parsed with the invariant culture, so the decimal separator is a dot
/// whatever the server's locale. A plan that is missing, or has no positive amount or no
/// description, throws: charging a guessed price is worse than not charging. A per-owner tax
/// treatment needs a resolver of your own.
/// </remarks>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public class ConfigPlanPriceResolver<TKey> : IPlanPriceResolver<TKey> where TKey : IEquatable<TKey>
{
    private readonly IConfiguration _config;
    private readonly CashierMollieOptions _options;

    /// <summary>Initializes a new instance of <see cref="ConfigPlanPriceResolver{TKey}"/>.</summary>
    public ConfigPlanPriceResolver(IConfiguration config, IOptions<CashierMollieOptions> options)
    {
        _config = config;
        _options = options.Value;
    }

    /// <inheritdoc />
    public Task<PlanPrice> ResolveAsync(TKey ownerId, string plan, CancellationToken ct = default)
    {
        var section = _config.GetSection($"{CashierMollieOptions.SectionName}:Plans").GetChildren()
            .FirstOrDefault(c => string.Equals(c.Key, plan, StringComparison.Ordinal))
            ?? throw new CashierException($"Plan '{plan}' is not configured under CashierMollie:Plans.");

        if (!decimal.TryParse(section["Amount"], NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal net) || net <= 0m)
        {
            throw new CashierException($"Plan '{plan}' has no positive Amount.");
        }

        string? description = section["Description"];
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new CashierException($"Plan '{plan}' has no Description.");
        }

        decimal rate = _options.DefaultTaxRatePercent;
        string? rateText = section["TaxRatePercent"];
        if (!string.IsNullOrWhiteSpace(rateText)
            && !decimal.TryParse(rateText, NumberStyles.Number, CultureInfo.InvariantCulture, out rate))
        {
            throw new CashierException($"Plan '{plan}' has an unreadable TaxRatePercent.");
        }

        return Task.FromResult(new PlanPrice(net, _options.Currency, rate, description));
    }
}
