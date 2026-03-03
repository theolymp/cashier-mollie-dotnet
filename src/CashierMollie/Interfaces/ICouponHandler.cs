using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Handles coupon validation and discount calculation.</summary>
public interface ICouponHandler
{
    /// <summary>Calculates the discount amount for a given coupon, base amount, and quantity.</summary>
    decimal CalculateDiscount(Coupon coupon, decimal amount, int quantity);
}
