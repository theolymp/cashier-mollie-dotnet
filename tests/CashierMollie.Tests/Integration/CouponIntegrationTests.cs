using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

public class CouponIntegrationTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierService<string> _cashier;
    private readonly CouponService<string> _couponService;
    private readonly ICouponRepository _couponRepo;

    public CouponIntegrationTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        _couponRepo = Substitute.For<ICouponRepository>();

        var options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        });

        var engine = new MollieBillingEngine<string>(_db, _mollieClient, _eventDispatcher, options);
        _cashier = new CashierService<string>(_db, engine, _mollieClient, _eventDispatcher, options, NullLogger<CashierService<string>>.Instance);
        _couponService = new CouponService<string>(_db, _couponRepo, _eventDispatcher);
    }

    [Fact]
    public async Task CreateSubscriptionWithCoupon_RedeemsCoupon()
    {
        var owner = new TestBillable("coupon-user-1", "cst_coupon1", "mdt_coupon1");

        // Setup coupon in repository
        var coupon = new Coupon
        {
            Code = "LAUNCH10",
            HandlerType = "percentage",
            Times = 3,
            Context = new Dictionary<string, string> { ["percentage"] = "10" },
        };
        _couponRepo.FindByCodeAsync("LAUNCH10", Arg.Any<CancellationToken>())
            .Returns(coupon);

        // 1. Create a subscription first (owner has mandate, so direct activation)
        var result = await _cashier.NewSubscription(owner, "default", "pro")
            .CreateAsync();

        Assert.False(result.RequiresAction);
        Assert.Equal(SubscriptionStatus.Active, result.Subscription.Status);

        // 2. Redeem the coupon on that subscription
        var redeemed = await _couponService.RedeemAsync(owner, "LAUNCH10", "default");

        Assert.NotNull(redeemed);
        Assert.Equal("LAUNCH10", redeemed.Code);
        Assert.Equal("coupon-user-1", redeemed.OwnerId);
        Assert.Equal("default", redeemed.SubscriptionName);
        Assert.Equal(3, redeemed.TimesLeft);

        // 3. Verify the redeemed coupon is persisted in the database
        var savedCoupon = await _db.RedeemedCoupons
            .FirstOrDefaultAsync(c => c.OwnerId == "coupon-user-1" && c.Code == "LAUNCH10");
        Assert.NotNull(savedCoupon);

        // 4. Verify CouponApplied event was dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<CouponApplied<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidCoupon_ThrowsException()
    {
        var owner = new TestBillable("coupon-user-2", "cst_coupon2", "mdt_coupon2");

        // Coupon does not exist in repository
        _couponRepo.FindByCodeAsync("INVALID", Arg.Any<CancellationToken>())
            .Returns((Coupon?)null);

        // Attempting to validate an invalid coupon should throw
        var ex = await Assert.ThrowsAsync<CashierException>(
            () => _couponService.ValidateAsync("INVALID", owner));
        Assert.Contains("INVALID", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    public void Dispose() => _db.Dispose();
}
