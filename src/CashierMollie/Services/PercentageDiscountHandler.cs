using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Percentage discount handler.
/// Expects context key: "percentage" (decimal string, e.g. "20" for 20%).
/// The percentage is clamped to the range [0, 100].
/// </summary>
public class PercentageDiscountHandler : ICouponHandler
{
    /// <inheritdoc />
    public decimal CalculateDiscount(Coupon coupon, decimal amount, int quantity)
    {
        if (!coupon.Context.TryGetValue("percentage", out string? pctStr) ||
            !decimal.TryParse(pctStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out decimal percentage))
            return 0m;

        percentage = Math.Clamp(percentage, 0, 100);
        return amount * quantity * (percentage / 100m);
    }
}
