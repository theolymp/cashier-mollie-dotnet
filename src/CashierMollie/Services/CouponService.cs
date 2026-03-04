using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Services;

/// <summary>
/// Service for validating, redeeming, and revoking coupons.
/// Ships with built-in "fixed" and "percentage" discount handlers.
/// </summary>
public class CouponService<TKey> : ICouponService<TKey> where TKey : IEquatable<TKey>
{
    private readonly CashierDbContext<TKey> _db;
    private readonly ICouponRepository _repo;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly Dictionary<string, ICouponHandler> _handlers;

    /// <summary>Creates a new CouponService.</summary>
    public CouponService(CashierDbContext<TKey> db, ICouponRepository repo, ICashierEventDispatcher dispatcher)
    {
        _db = db;
        _repo = repo;
        _dispatcher = dispatcher;
        _handlers = new Dictionary<string, ICouponHandler>(StringComparer.OrdinalIgnoreCase)
        {
            ["fixed"] = new FixedDiscountHandler(),
            ["percentage"] = new PercentageDiscountHandler(),
        };
    }

    /// <inheritdoc />
    public async Task<Coupon> ValidateAsync(string code, IBillable<TKey> owner, CancellationToken ct = default)
    {
        var coupon = await _repo.FindByCodeAsync(code, ct)
            ?? throw new CashierException($"Coupon '{code}' not found.");
        return coupon;
    }

    /// <inheritdoc />
    public async Task<RedeemedCoupon<TKey>> RedeemAsync(IBillable<TKey> owner, string code,
        string subscriptionName = "default", CancellationToken ct = default)
    {
        var coupon = await ValidateAsync(code, owner, ct);

        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.OwnerId.Equals(owner.Id) && s.Name == subscriptionName, ct)
            ?? throw new CashierException($"Subscription '{subscriptionName}' not found.");

        var alreadyRedeemed = await _db.RedeemedCoupons
            .AnyAsync(c => c.OwnerId!.Equals(owner.Id) && c.Code == code
                && c.SubscriptionName == subscriptionName, ct);
        if (alreadyRedeemed)
            throw new CashierException($"Coupon '{code}' already redeemed for this subscription.");

        var redeemed = new RedeemedCoupon<TKey>
        {
            OwnerId = owner.Id,
            Code = coupon.Code,
            SubscriptionName = subscriptionName,
            TimesLeft = coupon.Times,
        };
        _db.RedeemedCoupons.Add(redeemed);
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(new CouponApplied<TKey>(code, sub, owner.Id), ct);
        return redeemed;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(IBillable<TKey> owner, string subscriptionName = "default",
        CancellationToken ct = default)
    {
        var coupons = await _db.RedeemedCoupons
            .Where(c => c.OwnerId.Equals(owner.Id) && c.SubscriptionName == subscriptionName)
            .ToListAsync(ct);
        _db.RedeemedCoupons.RemoveRange(coupons);
        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public ICouponHandler GetHandler(string handlerType)
    {
        if (_handlers.TryGetValue(handlerType, out var handler))
            return handler;
        throw new CashierException($"Unknown coupon handler type: '{handlerType}'");
    }
}
