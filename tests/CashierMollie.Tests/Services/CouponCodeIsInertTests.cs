using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using NSubstitute;

namespace CashierMollie.Tests.Services;

/// <summary>
/// <c>WithCoupon()</c> records a code on the options object and nothing else. Measured 2026-10-26:
/// <c>SubscriptionOptions.CouponCode</c> has one write site (the builder) and zero read sites, and
/// neither billing engine mentions coupons at all -- so creating a subscription with a coupon
/// validates nothing, redeems nothing, discounts nothing, and reports success.
///
/// THIS CLASS PINS A GAP, NOT AN INTENTION. A test that freezes a defect as a promise is worse than
/// no test -- it reads as a decision to whoever finds it next. So: when the coupon path is wired up,
/// these tests MUST go red, and the fix is to rewrite them along with the XML summaries on
/// <c>WithCoupon</c> and <c>SubscriptionOptions.CouponCode</c> in the same commit. They exist
/// because the gap is otherwise invisible: there is no exception, no log line and no event to
/// notice, and the detector built to catch exactly this missed it for months by counting the
/// builder's write as a read.
///
/// Why not simply make <c>WithCoupon</c> throw, as <c>WithProration()</c> does? Because that is a
/// breaking change on a published API and therefore a decision rather than a repair -- the same
/// reason the swap and quantity no-ops are still accepted. Raised, not taken here.
/// </summary>
public class CouponCodeIsInertTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IBillingEngine<string> _engine;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly TestBillable _owner;

    public CouponCodeIsInertTests()
    {
        _db = TestDbContextFactory.Create();
        _engine = Substitute.For<IBillingEngine<string>>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        _owner = new TestBillable("user-1", "cst_test", "mdt_test");
    }

    private SubscriptionBuilder<string> Builder() =>
        new(_db, _engine, Substitute.For<IMollieClientService>(), _dispatcher,
            new CashierMollieOptions { Currency = "EUR" }, _owner, "default", "pro");

    [Fact]
    public async Task Builder_HandsTheCodeToTheEngine_ButRecordsNoRedemption()
    {
        await Builder().WithCoupon("SAVE50").CreateAsync();

        // The code does travel: the builder is not where it is dropped.
        await _engine.Received(1).CreateSubscriptionAsync(
            _owner, "default", "pro",
            Arg.Is<SubscriptionOptions>(o => o.CouponCode == "SAVE50"),
            Arg.Any<CancellationToken>());

        // And it stops there. Both engines ignore the field, so nothing is recorded anywhere.
        Assert.Empty(_db.RedeemedCoupons);
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<CouponApplied<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KnownPositive_RedeemingThroughTheServiceDoesRecordAndDispatch()
    {
        // Without this, the two zeros above are worthless: an empty table and a silent dispatcher
        // are exactly what a test looking in the wrong place would also see. The service path
        // writes the row and raises the event, so the instrument works and the gap is real.
        _db.Subscriptions.Add(new Subscription<string>
        {
            OwnerId = "user-1", Name = "default", Plan = "pro", Status = SubscriptionStatus.Active,
        });
        await _db.SaveChangesAsync();

        var repo = Substitute.For<ICouponRepository>();
        repo.FindByCodeAsync("SAVE50", Arg.Any<CancellationToken>())
            .Returns(new Coupon { Code = "SAVE50", HandlerType = "percentage", Times = 1 });
        var service = new CouponService<string>(_db, repo, _dispatcher);

        await service.RedeemAsync(_owner, "SAVE50");

        Assert.Single(_db.RedeemedCoupons);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<CouponApplied<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void KnownPositive_AHandlerDoesComputeADiscount_NobodyCallsIt()
    {
        // The calculation is correct and unreachable from any billing path: CalculateDiscount has
        // zero call sites in src/. Pinned here so "coupons do nothing" is not misread as "the
        // discount maths is broken" -- it is the wiring that is absent, not the arithmetic.
        decimal discount = new PercentageDiscountHandler().CalculateDiscount(
            new Coupon { Code = "SAVE50", HandlerType = "percentage", Context = { ["percentage"] = "50" } },
            amount: 10m, quantity: 1);

        Assert.Equal(5m, discount);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
