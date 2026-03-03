using CashierMollie.Events;
using CashierMollie.Models;

namespace CashierMollie.Tests.Models;

public class EventTests
{
    private static Subscription<string> CreateSubscription(string plan = "pro-monthly") =>
        new() { OwnerId = "user-1", Plan = plan, Name = "default" };

    private static Payment<string> CreatePayment() =>
        new() { OwnerId = "user-1", MolliePaymentId = "tr_test123", Amount = 9.99m };

    [Fact]
    public void SubscriptionQuantityUpdated_StoresProperties()
    {
        var sub = CreateSubscription();
        var evt = new SubscriptionQuantityUpdated<string>(sub, 1, 5, "user-1");

        Assert.Same(sub, evt.Subscription);
        Assert.Equal(1, evt.OldQuantity);
        Assert.Equal(5, evt.NewQuantity);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void OrderPaymentFailedDueToInvalidMandate_StoresProperties()
    {
        var payment = CreatePayment();
        var sub = CreateSubscription();
        var evt = new OrderPaymentFailedDueToInvalidMandate<string>(payment, sub, "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Same(sub, evt.Subscription);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void OrderPaymentFailedDueToInvalidMandate_NullSubscription()
    {
        var payment = CreatePayment();
        var evt = new OrderPaymentFailedDueToInvalidMandate<string>(payment, null, "user-1");

        Assert.Null(evt.Subscription);
    }

    [Fact]
    public void FirstPaymentPaid_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new FirstPaymentPaid<string>(payment, "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void FirstPaymentFailed_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new FirstPaymentFailed<string>(payment, "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void MandateUpdated_StoresProperties()
    {
        var evt = new MandateUpdated<string>("mdt_old", "mdt_new", "user-1");

        Assert.Equal("mdt_old", evt.OldMandateId);
        Assert.Equal("mdt_new", evt.NewMandateId);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void MandateUpdated_NullOldMandateId()
    {
        var evt = new MandateUpdated<string>(null, "mdt_new", "user-1");

        Assert.Null(evt.OldMandateId);
    }

    [Fact]
    public void MandateCleared_StoresProperties()
    {
        var evt = new MandateCleared<string>("mdt_123", "user-1");

        Assert.Equal("mdt_123", evt.MandateId);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void CouponApplied_StoresProperties()
    {
        var sub = CreateSubscription();
        var evt = new CouponApplied<string>("LAUNCH10", sub, "user-1");

        Assert.Equal("LAUNCH10", evt.CouponCode);
        Assert.Same(sub, evt.Subscription);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void CreditAdded_StoresProperties()
    {
        var evt = new CreditAdded<string>(25.00m, "EUR", "Bonus credit", "user-1");

        Assert.Equal(25.00m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("Bonus credit", evt.Description);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void CreditAdded_NullDescription()
    {
        var evt = new CreditAdded<string>(10.00m, "EUR", null, "user-1");

        Assert.Null(evt.Description);
    }

    [Fact]
    public void CreditApplied_StoresProperties()
    {
        var evt = new CreditApplied<string>(5.00m, "EUR", "user-1");

        Assert.Equal(5.00m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void BalanceTurnedStale_StoresProperties()
    {
        var evt = new BalanceTurnedStale<string>(15.50m, "EUR", "user-1");

        Assert.Equal(15.50m, evt.Balance);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void RefundInitiated_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new RefundInitiated<string>(payment, 9.99m, "EUR", "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal(9.99m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void RefundProcessed_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new RefundProcessed<string>(payment, 9.99m, "EUR", "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal(9.99m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void RefundFailed_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new RefundFailed<string>(payment, 9.99m, "EUR", "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal(9.99m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void ChargebackReceived_StoresProperties()
    {
        var payment = CreatePayment();
        var evt = new ChargebackReceived<string>(payment, 9.99m, "EUR", "user-1");

        Assert.Same(payment, evt.Payment);
        Assert.Equal(9.99m, evt.Amount);
        Assert.Equal("EUR", evt.Currency);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void OrderCreated_StoresProperties()
    {
        var evt = new OrderCreated<string>(42L, "user-1");

        Assert.Equal(42L, evt.OrderId);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void OrderProcessed_StoresProperties()
    {
        var evt = new OrderProcessed<string>(42L, "user-1");

        Assert.Equal(42L, evt.OrderId);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void OrderInvoiceAvailable_StoresProperties()
    {
        var evt = new OrderInvoiceAvailable<string>(42L, "user-1");

        Assert.Equal(42L, evt.OrderId);
        Assert.Equal("user-1", evt.OwnerId);
    }

    [Fact]
    public void Events_WithIntKey_WorkCorrectly()
    {
        var sub = new Subscription<int> { OwnerId = 1, Plan = "pro", Name = "default" };
        var payment = new Payment<int> { OwnerId = 1, MolliePaymentId = "tr_int" };

        var evt1 = new SubscriptionQuantityUpdated<int>(sub, 1, 3, 1);
        var evt2 = new FirstPaymentPaid<int>(payment, 1);
        var evt3 = new MandateUpdated<int>("old", "new", 1);
        var evt4 = new CreditAdded<int>(10m, "EUR", null, 1);
        var evt5 = new RefundInitiated<int>(payment, 5m, "EUR", 1);
        var evt6 = new ChargebackReceived<int>(payment, 5m, "EUR", 1);
        var evt7 = new OrderCreated<int>(1L, 1);

        Assert.Equal(1, evt1.OwnerId);
        Assert.Equal(1, evt2.OwnerId);
        Assert.Equal(1, evt3.OwnerId);
        Assert.Equal(1, evt4.OwnerId);
        Assert.Equal(1, evt5.OwnerId);
        Assert.Equal(1, evt6.OwnerId);
        Assert.Equal(1, evt7.OwnerId);
    }

    [Fact]
    public void Events_RecordEquality_WorksCorrectly()
    {
        var evt1 = new OrderCreated<string>(42L, "user-1");
        var evt2 = new OrderCreated<string>(42L, "user-1");
        var evt3 = new OrderCreated<string>(99L, "user-1");

        Assert.Equal(evt1, evt2);
        Assert.NotEqual(evt1, evt3);
    }
}
