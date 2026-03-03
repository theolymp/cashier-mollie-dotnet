using CashierMollie.Data;
using CashierMollie.Models;
using CashierMollie.Tests.TestHelpers;

namespace CashierMollie.Tests.Data;

public class SchemaTests
{
    [Fact]
    public void DbContext_HasAllDbSets()
    {
        using var db = TestDbContextFactory.Create();
        Assert.NotNull(db.Subscriptions);
        Assert.NotNull(db.OrderItems);
        Assert.NotNull(db.Payments);
        Assert.NotNull(db.Orders);
        Assert.NotNull(db.Credits);
        Assert.NotNull(db.Refunds);
        Assert.NotNull(db.RedeemedCoupons);
    }

    [Fact]
    public async Task Order_CanBePersisted()
    {
        using var db = TestDbContextFactory.Create();
        var order = new Order<string> { OwnerId = "u1", Currency = "EUR", Total = 9.99m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        Assert.True(order.Id > 0);
    }

    [Fact]
    public async Task Credit_CanBePersisted()
    {
        using var db = TestDbContextFactory.Create();
        var credit = new Credit<string> { OwnerId = "u1", Currency = "EUR", Balance = 10m };
        db.Credits.Add(credit);
        await db.SaveChangesAsync();
        Assert.True(credit.Id > 0);
    }

    [Fact]
    public async Task Refund_CanBePersisted()
    {
        using var db = TestDbContextFactory.Create();
        var payment = new Payment<string> { OwnerId = "u1", MolliePaymentId = "tr_test" };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var refund = new Refund<string>
        {
            OwnerId = "u1", PaymentId = payment.Id,
            MollieRefundId = "re_test", Amount = 5m, Currency = "EUR"
        };
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        Assert.True(refund.Id > 0);
    }

    [Fact]
    public async Task RedeemedCoupon_CanBePersisted()
    {
        using var db = TestDbContextFactory.Create();
        var rc = new RedeemedCoupon<string>
        {
            OwnerId = "u1", Code = "SAVE10", SubscriptionName = "default", TimesLeft = 3
        };
        db.RedeemedCoupons.Add(rc);
        await db.SaveChangesAsync();
        Assert.True(rc.Id > 0);
    }

    [Fact]
    public async Task OrderItem_OrderId_Nullable()
    {
        using var db = TestDbContextFactory.Create();
        var sub = new Subscription<string> { OwnerId = "u1", Plan = "pro" };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();

        var item = new OrderItem<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id,
            Description = "Test", OrderId = null
        };
        db.OrderItems.Add(item);
        await db.SaveChangesAsync();
        Assert.Null(item.OrderId);
    }
}
