using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class CouponServiceTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly ICouponRepository _repo;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CouponService<string> _service;

    public CouponServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _repo = Substitute.For<ICouponRepository>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        _service = new CouponService<string>(_db, _repo, _dispatcher);
    }

    [Fact]
    public async Task ValidateAsync_ValidCoupon_ReturnsCoupon()
    {
        var coupon = new Coupon { Code = "SAVE10", HandlerType = "percentage", Times = 3 };
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns(coupon);
        var owner = new TestBillable("u1", "cst_test");

        var result = await _service.ValidateAsync("SAVE10", owner);
        Assert.Equal("SAVE10", result.Code);
    }

    [Fact]
    public async Task ValidateAsync_UnknownCoupon_ThrowsCashierException()
    {
        _repo.FindByCodeAsync("INVALID", Arg.Any<CancellationToken>()).Returns((Coupon?)null);
        var owner = new TestBillable("u1", "cst_test");

        await Assert.ThrowsAsync<CashierMollie.Exceptions.CashierException>(
            () => _service.ValidateAsync("INVALID", owner));
    }

    [Fact]
    public async Task RedeemAsync_CreatesRedeemedCoupon()
    {
        var coupon = new Coupon { Code = "SAVE10", HandlerType = "percentage", Times = 3 };
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns(coupon);
        var owner = new TestBillable("u1", "cst_test");
        var sub = new Subscription<string> { OwnerId = "u1", Name = "default", Plan = "pro" };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var redeemed = await _service.RedeemAsync(owner, "SAVE10", "default");

        Assert.Equal("SAVE10", redeemed.Code);
        Assert.Equal(3, redeemed.TimesLeft);
        Assert.Equal("default", redeemed.SubscriptionName);
    }

    [Fact]
    public async Task RedeemAsync_NoSubscription_ThrowsCashierException()
    {
        var coupon = new Coupon { Code = "SAVE10", HandlerType = "percentage", Times = 3 };
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns(coupon);
        var owner = new TestBillable("u1", "cst_test");

        await Assert.ThrowsAsync<CashierMollie.Exceptions.CashierException>(
            () => _service.RedeemAsync(owner, "SAVE10", "default"));
    }

    [Fact]
    public async Task RedeemAsync_DispatchesCouponAppliedEvent()
    {
        var coupon = new Coupon { Code = "LAUNCH20", HandlerType = "percentage", Times = 1 };
        _repo.FindByCodeAsync("LAUNCH20", Arg.Any<CancellationToken>()).Returns(coupon);
        var owner = new TestBillable("u1", "cst_test");
        var sub = new Subscription<string> { OwnerId = "u1", Name = "default", Plan = "pro" };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _service.RedeemAsync(owner, "LAUNCH20", "default");

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<CashierMollie.Events.CouponApplied<string>>(e =>
                e.CouponCode == "LAUNCH20" && e.OwnerId == "u1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_RemovesAllRedeemedCoupons()
    {
        _db.RedeemedCoupons.Add(new RedeemedCoupon<string>
            { OwnerId = "u1", Code = "C1", SubscriptionName = "default", TimesLeft = 2 });
        _db.RedeemedCoupons.Add(new RedeemedCoupon<string>
            { OwnerId = "u1", Code = "C2", SubscriptionName = "default", TimesLeft = 1 });
        await _db.SaveChangesAsync();
        var owner = new TestBillable("u1", "cst_test");

        await _service.RevokeAsync(owner, "default");

        Assert.Empty(_db.RedeemedCoupons.Where(c => c.OwnerId == "u1" && c.SubscriptionName == "default"));
    }

    [Fact]
    public async Task RevokeAsync_OnlyRemovesMatchingSubscription()
    {
        _db.RedeemedCoupons.Add(new RedeemedCoupon<string>
            { OwnerId = "u1", Code = "C1", SubscriptionName = "default", TimesLeft = 2 });
        _db.RedeemedCoupons.Add(new RedeemedCoupon<string>
            { OwnerId = "u1", Code = "C2", SubscriptionName = "premium", TimesLeft = 1 });
        await _db.SaveChangesAsync();
        var owner = new TestBillable("u1", "cst_test");

        await _service.RevokeAsync(owner, "default");

        Assert.Single(_db.RedeemedCoupons);
        Assert.Equal("premium", _db.RedeemedCoupons.Single().SubscriptionName);
    }

    [Fact]
    public void GetHandler_Fixed_ReturnsFixedDiscountHandler()
    {
        var handler = _service.GetHandler("fixed");
        Assert.IsType<FixedDiscountHandler>(handler);
    }

    [Fact]
    public void GetHandler_Percentage_ReturnsPercentageDiscountHandler()
    {
        var handler = _service.GetHandler("percentage");
        Assert.IsType<PercentageDiscountHandler>(handler);
    }

    [Fact]
    public void GetHandler_CaseInsensitive()
    {
        var handler = _service.GetHandler("FIXED");
        Assert.IsType<FixedDiscountHandler>(handler);
    }

    [Fact]
    public void GetHandler_UnknownType_ThrowsCashierException()
    {
        Assert.Throws<CashierMollie.Exceptions.CashierException>(
            () => _service.GetHandler("unknown"));
    }

    [Fact]
    public void FixedDiscountHandler_CalculatesDiscount()
    {
        var handler = new FixedDiscountHandler();
        var coupon = new Coupon
        {
            Code = "FLAT5", HandlerType = "fixed",
            Context = new() { ["amount"] = "5.00", ["currency"] = "EUR" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 10.00m, 1);
        Assert.Equal(5.00m, discount);
    }

    [Fact]
    public void FixedDiscountHandler_CapsAtAmount()
    {
        var handler = new FixedDiscountHandler();
        var coupon = new Coupon
        {
            Code = "FLAT20", HandlerType = "fixed",
            Context = new() { ["amount"] = "20.00", ["currency"] = "EUR" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 10.00m, 1);
        Assert.Equal(10.00m, discount);
    }

    [Fact]
    public void FixedDiscountHandler_WithQuantity()
    {
        var handler = new FixedDiscountHandler();
        var coupon = new Coupon
        {
            Code = "FLAT5", HandlerType = "fixed",
            Context = new() { ["amount"] = "5.00", ["currency"] = "EUR" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 10.00m, 3);
        // total is 30, discount is 5 (capped to total)
        Assert.Equal(5.00m, discount);
    }

    [Fact]
    public void FixedDiscountHandler_MissingAmount_ReturnsZero()
    {
        var handler = new FixedDiscountHandler();
        var coupon = new Coupon
        {
            Code = "BAD", HandlerType = "fixed",
            Context = new() { ["currency"] = "EUR" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 10.00m, 1);
        Assert.Equal(0m, discount);
    }

    [Fact]
    public void FixedDiscountHandler_InvalidAmount_ReturnsZero()
    {
        var handler = new FixedDiscountHandler();
        var coupon = new Coupon
        {
            Code = "BAD", HandlerType = "fixed",
            Context = new() { ["amount"] = "notanumber" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 10.00m, 1);
        Assert.Equal(0m, discount);
    }

    [Fact]
    public void PercentageDiscountHandler_CalculatesDiscount()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "PCT20", HandlerType = "percentage",
            Context = new() { ["percentage"] = "20" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 100.00m, 1);
        Assert.Equal(20.00m, discount);
    }

    [Fact]
    public void PercentageDiscountHandler_WithQuantity()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "PCT10", HandlerType = "percentage",
            Context = new() { ["percentage"] = "10" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 50.00m, 3);
        Assert.Equal(15.00m, discount);
    }

    [Fact]
    public void PercentageDiscountHandler_ClampedAt100()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "PCT200", HandlerType = "percentage",
            Context = new() { ["percentage"] = "200" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 50.00m, 1);
        Assert.Equal(50.00m, discount); // clamped to 100%
    }

    [Fact]
    public void PercentageDiscountHandler_ClampedAtZero()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "NEG", HandlerType = "percentage",
            Context = new() { ["percentage"] = "-10" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 50.00m, 1);
        Assert.Equal(0m, discount); // clamped to 0%
    }

    [Fact]
    public void PercentageDiscountHandler_MissingPercentage_ReturnsZero()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "BAD", HandlerType = "percentage",
            Context = new()
        };
        decimal discount = handler.CalculateDiscount(coupon, 50.00m, 1);
        Assert.Equal(0m, discount);
    }

    [Fact]
    public void PercentageDiscountHandler_InvalidPercentage_ReturnsZero()
    {
        var handler = new PercentageDiscountHandler();
        var coupon = new Coupon
        {
            Code = "BAD", HandlerType = "percentage",
            Context = new() { ["percentage"] = "abc" }
        };
        decimal discount = handler.CalculateDiscount(coupon, 50.00m, 1);
        Assert.Equal(0m, discount);
    }

    public void Dispose() => _db.Dispose();
}
