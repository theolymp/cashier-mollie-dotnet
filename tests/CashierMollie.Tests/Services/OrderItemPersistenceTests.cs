using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

/// <summary>
/// What an order item keeps, and what it passes on.
/// </summary>
/// <remarks>
/// Two columns that existed and did nothing until 0.5.0, for the same reason in both cases: the
/// row is where a later reader looks, and neither the discount that was subtracted nor the
/// metadata a subscription was created with was there. <c>Discount</c> had five mentions in the
/// model and <b>zero</b> read or write sites in the library; metadata reached the first payment
/// and nothing after it, because recurring charges are built from items.
///
/// These tests mock the Mollie client and nothing between it and the engine, following the rule
/// <see cref="CouponAppliedToChargeTests"/> had to learn: a test about what reaches Mollie that
/// substitutes the engine only asserts that a mock does nothing.
/// </remarks>
public class OrderItemPersistenceTests : IDisposable
{
    private const decimal UnitPrice = 29.00m;

    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private readonly ICouponRepository _repo = Substitute.For<ICouponRepository>();
    private readonly ICashierEventDispatcher _dispatcher = Substitute.For<ICashierEventDispatcher>();
    private readonly ManagedBillingEngine<string> _managed;
    private readonly TestBillable _owner = new("u1", "cst_test", "mdt_test");

    public OrderItemPersistenceTests()
    {
        _db = TestDbContextFactory.Create();
        var coupons = new CouponService<string>(_db, _repo, _dispatcher);
        var options = Options.Create(new CashierMollieOptions { Currency = "EUR" });
        _managed = new ManagedBillingEngine<string>(_db, _mollie, _dispatcher, options,
            NullLogger<ManagedBillingEngine<string>>.Instance, coupons);

        var paid = Substitute.For<PaymentResponse>();
        paid.Id = "tr_recurring";
        paid.Status = "pending";
        _mollie.CreateRecurringPaymentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(paid);
    }

    private void OfferPercentageCoupon(string code, string percent) =>
        _repo.FindByCodeAsync(code, Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = code,
            HandlerType = "percentage",
            Times = 3,
            Context = { ["percentage"] = percent },
        });

    private async Task<Subscription<string>> SeedBillableSubscriptionAsync(string? metadata = null)
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
            UnitPrice = UnitPrice, Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
            Metadata = metadata,
        });
        await _db.SaveChangesAsync();
        return sub;
    }

    private async Task<OrderItem<string>> ChargedItemAsync() =>
        await _db.OrderItems.AsNoTracking().SingleAsync(i => i.ProcessedAt != null);

    private async Task<OrderItem<string>> NextItemAsync() =>
        await _db.OrderItems.AsNoTracking().SingleAsync(i => i.ProcessedAt == null);

    // --- the discount is recorded where the amount can be explained from ---

    [Fact]
    public async Task KnownPositive_TheDiscountThatWasSubtractedIsStoredOnTheItem()
    {
        OfferPercentageCoupon("SAVE10", "10");
        await SeedBillableSubscriptionAsync();
        await new CouponService<string>(_db, _repo, _dispatcher).RedeemAsync(_owner, "SAVE10");

        await _managed.ProcessDueItemsAsync();

        var charged = await ChargedItemAsync();
        // 29.00 charged as 26.10, so the row has to say 2.90 for the two to reconcile.
        Assert.Equal(2.90m, charged.Discount);
        Assert.Equal(26.10m, (UnitPrice * charged.Quantity) - charged.Discount);
    }

    [Fact]
    public async Task KnownNegative_WithoutACouponTheStoredDiscountStaysZero()
    {
        await SeedBillableSubscriptionAsync();

        await _managed.ProcessDueItemsAsync();

        Assert.Equal(0m, (await ChargedItemAsync()).Discount);
    }

    [Fact]
    public async Task TheNextCycleStartsWithNoDiscount()
    {
        OfferPercentageCoupon("SAVE10", "10");
        await SeedBillableSubscriptionAsync();
        await new CouponService<string>(_db, _repo, _dispatcher).RedeemAsync(_owner, "SAVE10");

        await _managed.ProcessDueItemsAsync();

        // Discount records what a past charge subtracted. Copying it forward would assert a
        // reduction the next cycle has not been granted yet.
        Assert.Equal(0m, (await NextItemAsync()).Discount);
    }

    // --- metadata survives past the first payment --------------------------

    [Fact]
    public async Task KnownPositive_TheItemsMetadataReachesTheRecurringCharge()
    {
        await SeedBillableSubscriptionAsync("""{"tenant":"acme","plan":"pro"}""");

        await _managed.ProcessDueItemsAsync();

        await _mollie.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", UnitPrice, "EUR", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m =>
                m != null && m["tenant"] == "acme" && m["plan"] == "pro"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KnownNegative_AnItemWithoutMetadataSendsNone()
    {
        await SeedBillableSubscriptionAsync();

        await _managed.ProcessDueItemsAsync();

        await _mollie.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", UnitPrice, "EUR", Arg.Any<string>(), Arg.Any<string>(),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MetadataIsInheritedByTheNextCycle_WhichIsWhatMakesItSurvive()
    {
        const string stored = """{"tenant":"acme"}""";
        await SeedBillableSubscriptionAsync(stored);

        await _managed.ProcessDueItemsAsync();

        Assert.Equal(stored, (await NextItemAsync()).Metadata);
    }

    [Fact]
    public async Task MetadataThatCannotBeReadBack_StillCharges_WithoutIt()
    {
        // Only this library writes the column, so this state means something outside it changed
        // the row. The money is still owed; losing the charge over unreadable metadata would be
        // the worse failure. It is logged rather than swallowed.
        await SeedBillableSubscriptionAsync("{not json");

        await _managed.ProcessDueItemsAsync();

        await _mollie.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", UnitPrice, "EUR", Arg.Any<string>(), Arg.Any<string>(),
            null, Arg.Any<CancellationToken>());
        Assert.NotNull((await ChargedItemAsync()).ProcessedAt);
    }

    [Fact]
    public async Task ASubscriptionsMetadataIsStoredOnTheFirstScheduledItem()
    {
        // The seam between "reaches the first payment" and "reaches every cycle": without this the
        // dictionary would have nowhere to live once the create call returns.
        var paid = Substitute.For<PaymentResponse>();
        paid.Id = "tr_first";
        paid.Status = "paid";
        _mollie.CreateFirstPaymentAsync(
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(paid);

        await _managed.CreateSubscriptionAsync(_owner, "default", "pro", new SubscriptionOptions
        {
            Metadata = new Dictionary<string, string> { ["tenant"] = "acme" },
        });

        var scheduled = await _db.OrderItems.AsNoTracking().SingleAsync();
        Assert.Equal("""{"tenant":"acme"}""", scheduled.Metadata);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
