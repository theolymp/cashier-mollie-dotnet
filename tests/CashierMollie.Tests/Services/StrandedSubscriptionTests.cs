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
/// An active subscription with no order item never bills, and the engine now repairs that.
/// </summary>
/// <remarks>
/// Order items were created in exactly one place -- <c>CreateSubscriptionAsync</c>, and only on its
/// direct-activation branch. A subscription that went through the first-payment checkout flow is
/// activated by the webhook instead, which sets <c>Active</c> and <c>CycleStartedAt</c> and
/// dispatches <c>SubscriptionCreated</c>, and nothing scheduled anything for it. On this engine
/// that is an active subscription that never charges, silently, for as long as it exists.
///
/// The repair is a pass on the timer that already runs rather than a change to the webhook: the
/// webhook service is engine-agnostic and holds neither options nor a logger, so teaching it to
/// schedule would change a public constructor, and it would only help subscriptions activated
/// after the change. A reconciliation pass also heals rows that are already stuck.
/// </remarks>
public class StrandedSubscriptionTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private readonly ICashierEventDispatcher _dispatcher = Substitute.For<ICashierEventDispatcher>();
    private readonly ManagedBillingEngine<string> _managed;

    public StrandedSubscriptionTests()
    {
        _db = TestDbContextFactory.Create();
        var coupons = new CouponService<string>(_db, Substitute.For<ICouponRepository>(), _dispatcher);
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

    private async Task<Subscription<string>> SeedAsync(
        string status = SubscriptionStatus.Active,
        DateTimeOffset? cycleStartedAt = null,
        DateTimeOffset? endsAt = null,
        int? quantity = null)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro",
            MollieCustomerId = "cst_test",
            Status = status,
            CycleStartedAt = cycleStartedAt,
            EndsAt = endsAt,
            Quantity = quantity,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        return sub;
    }

    // --- the repair ---------------------------------------------------------

    [Fact]
    public async Task KnownPositive_AnActiveSubscriptionWithNoItemsGetsOneScheduled()
    {
        var activated = DateTimeOffset.UtcNow.AddDays(-3);
        var sub = await SeedAsync(cycleStartedAt: activated, quantity: 4);

        await _managed.ProcessDueItemsAsync();

        var item = Assert.Single(_db.OrderItems);
        Assert.Equal(sub.Id, item.SubscriptionId);
        Assert.Equal("u1", item.OwnerId);
        Assert.Equal("EUR", item.Currency);
        // One, not the subscription's 4: since 0.5.0 the repair does not carry a seat count.
        Assert.Equal(1, item.Quantity);
        // Priced by the consumer, not by the library -- same as the direct-activation path.
        Assert.Equal(0m, item.UnitPrice);
        Assert.Null(item.ProcessedAt);
        // Measured from the payment that started the cycle, not from whenever the timer noticed.
        Assert.Equal(activated.AddDays(30), item.ProcessAt);
    }

    [Fact]
    public async Task WithoutACycleStartTheScheduleFallsBackToNow_RatherThanNotScheduling()
    {
        await SeedAsync(cycleStartedAt: null);

        await _managed.ProcessDueItemsAsync();

        var item = Assert.Single(_db.OrderItems);
        Assert.NotNull(item.ProcessAt);
        Assert.True(item.ProcessAt > DateTimeOffset.UtcNow.AddDays(29));
    }

    // Both inputs, one expectation. This was "an absent quantity counts as one, not as zero",
    // which tested a conditional that no longer exists -- after the fix there is nothing to
    // distinguish null from 9, and a test whose name describes a branch that is gone misleads the
    // next reader. The claim worth keeping is that neither value reaches the item.
    [Theory]
    [InlineData(null)]
    [InlineData(9)]
    public async Task NoSubscriptionQuantityReachesTheRepairedItem(int? quantity)
    {
        await SeedAsync(quantity: quantity);

        await _managed.ProcessDueItemsAsync();

        Assert.Equal(1, Assert.Single(_db.OrderItems).Quantity);
    }

    [Fact]
    public async Task ItIsIdempotent_BecauseItActsOnlyWhereThereAreNoItemsAtAll()
    {
        await SeedAsync(cycleStartedAt: DateTimeOffset.UtcNow);

        await _managed.ProcessDueItemsAsync();
        await _managed.ProcessDueItemsAsync();
        await _managed.ProcessDueItemsAsync();

        Assert.Single(_db.OrderItems);
    }

    // --- known-negatives: the discriminator has to be narrow ---------------

    [Fact]
    public async Task KnownNegative_ASubscriptionThatAlreadyHasAnItemGetsNoSecondOne()
    {
        var sub = await SeedAsync();
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 29.00m, Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddDays(10),
        });
        await _db.SaveChangesAsync();

        await _managed.ProcessDueItemsAsync();

        Assert.Single(_db.OrderItems);
    }

    [Fact]
    public async Task KnownNegative_ZeroItemsIsTheTest_NotZeroUNPROCESSEDItems()
    {
        // A subscription billing normally always has at least a processed item. If the pass looked
        // for "no unprocessed items" it would bolt an extra item onto every healthy subscription
        // between cycles.
        var sub = await SeedAsync();
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 29.00m, Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddDays(-40),
            ProcessedAt = DateTimeOffset.UtcNow.AddDays(-40),
        });
        await _db.SaveChangesAsync();

        await _managed.ProcessDueItemsAsync();

        Assert.Single(_db.OrderItems);
    }

    // SubscriptionStatus is a static class of const strings, not an enum -- Status is a string
    // column, which is why these are usable as InlineData at all.
    [Theory]
    [InlineData(SubscriptionStatus.Pending)]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Paused)]
    [InlineData(SubscriptionStatus.PastDue)]
    public async Task KnownNegative_OnlyActiveSubscriptionsAreScheduled(string status)
    {
        await SeedAsync(status: status);

        await _managed.ProcessDueItemsAsync();

        Assert.Empty(_db.OrderItems);
    }

    [Fact]
    public async Task KnownNegative_ASubscriptionInItsGracePeriodIsNotGivenANewCycle()
    {
        // EndsAt set means cancelled or running out. Scheduling a cycle for it would bill someone
        // who has already left.
        await SeedAsync(endsAt: DateTimeOffset.UtcNow.AddDays(5));

        await _managed.ProcessDueItemsAsync();

        Assert.Empty(_db.OrderItems);
    }

    // --- the guard the repair needs ----------------------------------------

    [Fact]
    public async Task AnUnpricedDueItemIsSkipped_NotSentToMollieAsZero()
    {
        var sub = await SeedAsync();
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = "mdt_test", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 0m, Quantity = 1,
            ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await _db.SaveChangesAsync();

        await _managed.ProcessDueItemsAsync();

        // Before the guard this went to Mollie as 0.00, was rejected, and the catch dispatched
        // OrderPaymentFailed -- reporting a failed payment for something that was never a payment,
        // every cycle, forever.
        await _mollie.DidNotReceive().CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Events.OrderPaymentFailed<string>>(), Arg.Any<CancellationToken>());

        // And it stays due, so pricing it later still bills it.
        var item = await _db.OrderItems.AsNoTracking().SingleAsync();
        Assert.Null(item.ProcessedAt);
    }

    // --- Subscription.Quantity reaches no charge, including here --------------

    [Fact]
    public async Task TheRepairIgnoresSubscriptionQuantity_SoNoPathBillsASeatCount()
    {
        // This test was written on 2026-11-08 asserting the opposite, and it was right then:
        // the repair built its item from the SUBSCRIPTION, so this was the one path on which a
        // seat count reached a charge, while three shipped summaries said no billing path read
        // the field. Fixed on 2026-11-09 by making the claim true -- the repair now sets one, a
        // consumer sets a quantity on the item together with its price as on every other path.
        //
        // Kept pointed at the same mechanism rather than deleted: the direction is what changed,
        // and the assertion below is what would notice the old behaviour coming back.
        var sub = await SeedAsync(quantity: 7);

        // The repair runs at the top of ProcessDueItemsAsync, and it schedules the item 30 days
        // out, so this first call creates it and charges nothing.
        await _managed.ProcessDueItemsAsync();

        var repaired = await _db.OrderItems.SingleAsync();
        Assert.Equal(1, repaired.Quantity);          // NOT the subscription's 7
        Assert.Equal(0m, repaired.UnitPrice);        // priced by the consumer, as everywhere else

        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = "mdt_test", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        repaired.UnitPrice = 9.99m;                  // the documented step
        repaired.ProcessAt = DateTimeOffset.UtcNow.AddHours(-1);
        await _db.SaveChangesAsync();

        await _managed.ProcessDueItemsAsync();

        // 9.99, not 9.99 * 7. Asserted as the exact amount rather than "not 69.93": 69.93 is
        // the old behaviour returning, and any third number means neither story holds. The seat
        // count on the subscription is still 7 while this is charged, which is the whole claim --
        // the field is set, and no charge reads it.
        Assert.Equal(7, (await _db.Subscriptions.AsNoTracking().SingleAsync()).Quantity);
        await _mollie.Received(1).CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), 9.99m, "EUR",
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
