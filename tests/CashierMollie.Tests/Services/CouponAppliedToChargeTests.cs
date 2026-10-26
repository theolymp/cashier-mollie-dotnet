using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// A coupon has to change a number, or it is decoration. These tests assert the amount handed to
/// Mollie, not that a method was called: up to and including 0.4.0 the code travelled from
/// <c>WithCoupon()</c> into the engine and was dropped, and every test passed because none of them
/// looked at the money.
///
/// The predecessor of this class (<c>CouponCodeIsInertTests</c>) was written to go red when the path
/// was wired. It did not — it substituted <see cref="IBillingEngine{TKey}"/>, which is exactly the
/// layer the wiring went into, so it kept asserting that a mock does nothing. Nothing caught that
/// except the author expecting red and seeing green. Hence the rule this class follows: a test about
/// what reaches Mollie mocks the Mollie client and nothing in between.
/// </summary>
public class CouponAppliedToChargeTests : IDisposable
{
    private const decimal UnitPrice = 29.00m;

    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private readonly ICouponRepository _repo = Substitute.For<ICouponRepository>();
    private readonly ICashierEventDispatcher _dispatcher = Substitute.For<ICashierEventDispatcher>();
    private readonly CouponService<string> _coupons;
    private readonly ManagedBillingEngine<string> _managed;
    private readonly TestBillable _owner = new("u1", "cst_test", "mdt_test");

    public CouponAppliedToChargeTests()
    {
        _db = TestDbContextFactory.Create();
        _coupons = new CouponService<string>(_db, _repo, _dispatcher);
        var options = Options.Create(new CashierMollieOptions { Currency = "EUR" });
        _managed = new ManagedBillingEngine<string>(_db, _mollie, _dispatcher, options,
            NullLogger<ManagedBillingEngine<string>>.Instance, _coupons);

        var paid = Substitute.For<PaymentResponse>();
        paid.Id = "tr_recurring";
        paid.Status = "pending";
        _mollie.CreateRecurringPaymentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(paid);
    }

    private void OfferPercentageCoupon(string code, string percent, int times = 3) =>
        _repo.FindByCodeAsync(code, Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = code,
            HandlerType = "percentage",
            Times = times,
            Context = { ["percentage"] = percent },
        });

    private async Task<Subscription<string>> SeedBillableSubscriptionAsync(int quantity = 1)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        // The engine finds the mandate through the most recent payment of this owner.
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = "mdt_test", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = UnitPrice, Quantity = quantity,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await _db.SaveChangesAsync();
        return sub;
    }

    private async Task AssertChargedAsync(decimal expected) =>
        await _mollie.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", expected, "EUR", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());

    // --- the pair Robin asked for: one lowers, one does not -----------------

    [Fact]
    public async Task KnownPositive_ACouponLowersTheRecurringCharge()
    {
        OfferPercentageCoupon("SAVE10", "10");
        await SeedBillableSubscriptionAsync();
        await _coupons.RedeemAsync(_owner, "SAVE10");

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(26.10m);   // 29.00 - 10%
    }

    [Fact]
    public async Task KnownNegative_WithoutACouponTheChargeIsUnchanged()
    {
        await SeedBillableSubscriptionAsync();

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(UnitPrice);
    }

    [Fact]
    public async Task QuantityIsNotAppliedTwice()
    {
        // The handlers take the UNIT price and multiply by quantity themselves. Passing the gross
        // would discount 9.00 instead of 3.00 here -- a bug I had written before reading them.
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = "SAVE10", HandlerType = "percentage", Times = 3,
            Context = { ["percentage"] = "10" },
        });
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MollieMandateId = "mdt_test",
            MolliePaymentId = "tr_first", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 10.00m, Quantity = 3, ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await _db.SaveChangesAsync();
        await _coupons.RedeemAsync(_owner, "SAVE10");

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(27.00m);   // 30.00 gross - 3.00, not - 9.00
    }

    // --- the cycle is spent only when the money actually moved --------------

    [Fact]
    public async Task ASuccessfulChargeConsumesOneCycle()
    {
        OfferPercentageCoupon("SAVE10", "10", times: 3);
        await SeedBillableSubscriptionAsync();
        await _coupons.RedeemAsync(_owner, "SAVE10");

        await _managed.ProcessDueItemsAsync();

        var redeemed = Assert.Single(_db.RedeemedCoupons);
        await _db.Entry(redeemed).ReloadAsync();
        Assert.Equal(2, redeemed.TimesLeft);
    }

    [Fact]
    public async Task AFailedChargeDoesNotConsumeACycle()
    {
        // Consuming first would spend a billing cycle of the customer's coupon on a payment that
        // never happened -- and nothing would ever give it back.
        OfferPercentageCoupon("SAVE10", "10", times: 3);
        await SeedBillableSubscriptionAsync();
        await _coupons.RedeemAsync(_owner, "SAVE10");
        _mollie.CreateRecurringPaymentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Mollie said no"));

        await _managed.ProcessDueItemsAsync();   // swallows per item, by design

        var redeemed = Assert.Single(_db.RedeemedCoupons);
        await _db.Entry(redeemed).ReloadAsync();
        Assert.Equal(3, redeemed.TimesLeft);
    }

    // --- the third state: redeemed, definition gone -------------------------

    [Fact]
    public async Task ARedemptionWhoseDefinitionVanishedChargesFullPrice()
    {
        OfferPercentageCoupon("SAVE10", "10");
        await SeedBillableSubscriptionAsync();
        await _coupons.RedeemAsync(_owner, "SAVE10");

        // Somebody removed the coupon from configuration after it was redeemed.
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns((Coupon?)null);

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(UnitPrice);

        // And the cycle is NOT spent: nothing was discounted, so nothing was used up.
        var redeemed = Assert.Single(_db.RedeemedCoupons);
        await _db.Entry(redeemed).ReloadAsync();
        Assert.Equal(3, redeemed.TimesLeft);
    }

    [Fact]
    public async Task ADiscountLargerThanTheChargeNeverProducesANegativeAmount()
    {
        _repo.FindByCodeAsync("FLAT100", Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = "FLAT100", HandlerType = "fixed", Times = 1,
            Context = { ["amount"] = "100.00", ["currency"] = "EUR" },
        });
        await SeedBillableSubscriptionAsync();
        await _coupons.RedeemAsync(_owner, "FLAT100");

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(0m);
    }

    [Fact]
    public async Task AnExhaustedCouponStopsDiscounting()
    {
        OfferPercentageCoupon("SAVE10", "10", times: 0);
        await SeedBillableSubscriptionAsync();
        _db.RedeemedCoupons.Add(new RedeemedCoupon<string>
        {
            OwnerId = "u1", Code = "SAVE10", SubscriptionName = "default", TimesLeft = 0,
        });
        await _db.SaveChangesAsync();

        await _managed.ProcessDueItemsAsync();

        await AssertChargedAsync(UnitPrice);
    }

    // --- the create path --------------------------------------------------

    [Fact]
    public async Task CreatingWithACouponRecordsTheRedemption()
    {
        OfferPercentageCoupon("SAVE10", "10");

        await _managed.CreateSubscriptionAsync(_owner, "default", "pro",
            new SubscriptionOptions { CouponCode = "SAVE10" });

        var redeemed = Assert.Single(_db.RedeemedCoupons);
        Assert.Equal("SAVE10", redeemed.Code);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<CouponApplied<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatingWithAnUnknownCouponFailsInsteadOfIgnoringIt()
    {
        _repo.FindByCodeAsync("NOPE", Arg.Any<CancellationToken>()).Returns((Coupon?)null);

        await Assert.ThrowsAsync<CashierMollie.Exceptions.CashierException>(() =>
            _managed.CreateSubscriptionAsync(_owner, "default", "pro",
                new SubscriptionOptions { CouponCode = "NOPE" }));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
