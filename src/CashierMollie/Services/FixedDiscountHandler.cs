using CashierMollie.Interfaces;
using CashierMollie.Models;

namespace CashierMollie.Services;

/// <summary>
/// Fixed-amount discount handler.
/// Expects context keys: "amount" (decimal string, e.g. "5.00"), optionally "currency".
/// </summary>
public class FixedDiscountHandler : ICouponHandler
{
    /// <inheritdoc />
    public decimal CalculateDiscount(Coupon coupon, decimal amount, int quantity)
    {
        if (!coupon.Context.TryGetValue("amount", out string? discountStr) ||
            !decimal.TryParse(discountStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out decimal discount))
            return 0m;

        decimal totalAmount = amount * quantity;
        return Math.Min(discount, totalAmount); // Cap at total
    }
}
