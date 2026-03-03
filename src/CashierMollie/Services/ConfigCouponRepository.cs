using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.Extensions.Configuration;

namespace CashierMollie.Services;

/// <summary>
/// Loads coupon definitions from the "CashierMollie:Coupons" section in appsettings.json.
/// </summary>
/// <remarks>
/// Expected configuration format:
/// <code>
/// "CashierMollie": {
///   "Coupons": {
///     "LAUNCH10": {
///       "HandlerType": "percentage",
///       "Times": 3,
///       "Context": { "percentage": "10" }
///     },
///     "FLAT5": {
///       "HandlerType": "fixed",
///       "Times": 0,
///       "Context": { "amount": "5.00", "currency": "EUR" }
///     }
///   }
/// }
/// </code>
/// </remarks>
public class ConfigCouponRepository : ICouponRepository
{
    private readonly IConfiguration _config;

    /// <summary>Creates a new ConfigCouponRepository.</summary>
    public ConfigCouponRepository(IConfiguration config)
    {
        _config = config;
    }

    /// <inheritdoc />
    public Task<Coupon?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var section = _config.GetSection("CashierMollie:Coupons");
        var couponSection = section.GetChildren()
            .FirstOrDefault(c => string.Equals(c.Key, code, StringComparison.OrdinalIgnoreCase));

        if (couponSection == null)
            return Task.FromResult<Coupon?>(null);

        var coupon = new Coupon
        {
            Code = code,
            HandlerType = couponSection["HandlerType"] ?? "percentage",
            Times = int.TryParse(couponSection["Times"], out int times) ? times : 0,
            Context = couponSection.GetSection("Context").GetChildren()
                .ToDictionary(c => c.Key, c => c.Value ?? ""),
        };

        return Task.FromResult<Coupon?>(coupon);
    }
}
