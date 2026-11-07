using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// The order header the Managed engine writes when it bills an item.
/// </summary>
/// <remarks>
/// Until 0.5.0 the library's only <c>new Order&lt;TKey&gt;</c> was in <c>ChargeBuilder</c>, so this
/// engine created order <i>items</i> and never a header. The consequences were all visible and all
/// unasserted: <c>Number</c> never set, <c>ProcessedAt</c> never set, <c>OrderProcessed</c> never
/// dispatched, and <c>IInvoiceGenerator</c> with nothing to receive.
///
/// The money columns mirror what was <b>charged</b>, because that is the only way an order and its
/// payment reconcile. <c>Tax</c> is 0 on purpose: no billing path in this library adds tax to a
/// charge, so a non-zero value would describe money that was never collected.
/// </remarks>
public class OrderHeaderTests : IDisposable
{
    private const decimal UnitPrice = 29.00m;

    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private readonly ICouponRepository _repo = Substitute.For<ICouponRepository>();
    private readonly ICashierEventDispatcher _dispatcher = Substitute.For<ICashierEventDispatcher>();
    private readonly TestBillable _owner = new("u1", "cst_test", "mdt_test");
    private CashierMollieOptions _options = new() { Currency = "EUR" };

    public OrderHeaderTests()
    {
        _db = TestDbContextFactory.Create();
        var paid = Substitute.For<PaymentResponse>();
        paid.Id = "tr_recurring";
        paid.Status = "pending";
        _mollie.CreateRecurringPaymentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(paid);
    }

    private ManagedBillingEngine<string> Engine() =>
        new(_db, _mollie, _dispatcher, Options.Create(_options),
            NullLogger<ManagedBillingEngine<string>>.Instance,
            new CouponService<string>(_db, _repo, _dispatcher));

    private async Task<Subscription<string>> SeedDueItemAsync(
        decimal unitPrice = UnitPrice, int quantity = 1, int items = 1)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", MollieCustomerId = "cst_test",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = "mdt_test", Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        for (int i = 0; i < items; i++)
        {
            _db.OrderItems.Add(new OrderItem<string>
            {
                SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan",
                Currency = "EUR", UnitPrice = unitPrice, Quantity = quantity,
                ProcessAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
        }
        await _db.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task KnownPositive_AChargeWritesAnOrderHeaderAndLinksTheItem()
    {
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        var order = Assert.Single(_db.Orders);
        var item = await _db.OrderItems.AsNoTracking().SingleAsync(i => i.ProcessedAt != null);

        Assert.Equal(item.OrderId, order.Id);
        Assert.Equal("u1", order.OwnerId);
        Assert.Equal("EUR", order.Currency);
        Assert.NotNull(order.ProcessedAt);
        Assert.Equal("tr_recurring", order.MolliePaymentId);
        Assert.Equal("pending", order.MolliePaymentStatus);
    }

    [Fact]
    public async Task TheMoneyColumnsReconcileWithWhatWasCharged()
    {
        _repo.FindByCodeAsync("SAVE10", Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = "SAVE10", HandlerType = "percentage", Times = 3,
            Context = { ["percentage"] = "10" },
        });
        await SeedDueItemAsync();
        await new CouponService<string>(_db, _repo, _dispatcher).RedeemAsync(_owner, "SAVE10");

        await Engine().ProcessDueItemsAsync();

        var order = Assert.Single(_db.Orders);
        var item = await _db.OrderItems.AsNoTracking().SingleAsync(i => i.ProcessedAt != null);

        Assert.Equal(29.00m, order.Subtotal);   // gross, before the discount
        Assert.Equal(26.10m, order.Total);      // net, which is what Mollie was asked for
        Assert.Equal(26.10m, order.TotalDue);   // still due: the webhook settles the payment
        Assert.Equal(order.Subtotal - item.Discount, order.Total);

        // Zero on purpose, both of them: nothing in this library adds tax to a charge, and credit
        // is a ledger that no billing path spends.
        Assert.Equal(0m, order.Tax);
        Assert.Equal(0m, order.CreditUsed);

        await _mollie.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", order.Total, "EUR", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneOrderPerCharge_BecauseEachItemHasItsOwnPayment()
    {
        await SeedDueItemAsync(items: 2);

        await Engine().ProcessDueItemsAsync();

        // Not one order spanning both: an order covering two items could not name the single
        // payment that settles it, because there is no such payment.
        Assert.Equal(2, await _db.Orders.CountAsync());
        var linked = await _db.OrderItems.Where(i => i.OrderId != null).Select(i => i.OrderId)
            .ToListAsync();
        Assert.Equal(2, linked.Distinct().Count());
    }

    [Fact]
    public async Task OrderProcessedIsDispatchedWithTheOrdersId()
    {
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        var order = Assert.Single(_db.Orders);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<OrderProcessed<string>>(e => e.OrderId == order.Id && e.OwnerId == "u1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KnownNegative_AFailedChargeWritesNoOrder()
    {
        await SeedDueItemAsync();
        _mollie.CreateRecurringPaymentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mollie said no"));

        await Engine().ProcessDueItemsAsync();

        // An order header asserts that something was billed. Writing one for a charge that threw
        // would make the orders table a record of attempts rather than of money.
        Assert.Empty(_db.Orders);
    }

    [Fact]
    public async Task KnownNegative_AnUnpricedItemIsSkippedAndWritesNoOrder()
    {
        await SeedDueItemAsync(unitPrice: 0m);

        await Engine().ProcessDueItemsAsync();

        Assert.Empty(_db.Orders);
    }

    // --- the number, and the ways a configured format can be wrong --------

    [Fact]
    public async Task TheNumberComesFromTheConfiguredFormat()
    {
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        var order = Assert.Single(_db.Orders);
        Assert.Equal(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "ORD-{0:D6}", order.Id),
            order.Number);
    }

    [Theory]
    [InlineData("{1}")]                     // an index the call does not supply
    [InlineData("ORD-{0:D6")]               // unbalanced brace
    public async Task AnUnusableFormatLeavesTheNumberNull_AndDoesNotFailTheRun(string format)
    {
        // The charge has already happened by the time the number is built. Throwing here would be
        // caught by the loop's handler and reported as a FAILED payment for a successful charge.
        _options = new CashierMollieOptions { Currency = "EUR", OrderNumberFormat = format };
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        var order = Assert.Single(_db.Orders);
        Assert.Null(order.Number);
        Assert.NotNull(order.ProcessedAt);
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<OrderPaymentFailed<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFormatLongerThanTheColumnLeavesTheNumberNull()
    {
        _options = new CashierMollieOptions
        {
            Currency = "EUR",
            OrderNumberFormat = new string('X', 60) + "{0}",
        };
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        Assert.Null(Assert.Single(_db.Orders).Number);
    }

    [Fact]
    public async Task AnEmptyFormatOmitsTheNumberEntirely()
    {
        _options = new CashierMollieOptions { Currency = "EUR", OrderNumberFormat = "" };
        await SeedDueItemAsync();

        await Engine().ProcessDueItemsAsync();

        Assert.Null(Assert.Single(_db.Orders).Number);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
