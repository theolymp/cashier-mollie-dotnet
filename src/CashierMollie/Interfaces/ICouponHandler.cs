using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Handles coupon validation and discount calculation.</summary>
public interface ICouponHandler
{
    /// <summary>
    /// Calculates the discount amount for a given coupon, base amount and quantity.
    /// NEVER CALLED BY THIS LIBRARY — the only two sites are the two implementations. No billing
    /// path reduces an amount by a coupon, so this is a calculator you invoke yourself (via
    /// <c>ICouponService.GetHandler</c>) and apply to your own pricing.
    /// </summary>
    decimal CalculateDiscount(Coupon coupon, decimal amount, int quantity);
}
