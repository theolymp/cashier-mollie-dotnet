using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Handles coupon validation and discount calculation.</summary>
public interface ICouponHandler
{
    /// <summary>
    /// Calculates the discount for a given coupon, base amount and quantity.
    /// <para>
    /// Called by <c>ICouponService.CalculateActiveDiscountAsync</c>, which the Managed engine uses
    /// on every recurring charge. Note the contract both shipped handlers follow: <c>amount</c> is
    /// the UNIT price and the handler multiplies by <paramref name="quantity"/> itself, so a
    /// replacement must do the same or the quantity is applied twice.
    /// </para>
    /// </summary>
    decimal CalculateDiscount(Coupon coupon, decimal amount, int quantity);
}
