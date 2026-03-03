using CashierMollie.Models;

namespace CashierMollie.Interfaces;

/// <summary>Repository for looking up coupon definitions.</summary>
public interface ICouponRepository
{
    /// <summary>Finds a coupon by its code.</summary>
    Task<Coupon?> FindByCodeAsync(string code, CancellationToken ct = default);
}
