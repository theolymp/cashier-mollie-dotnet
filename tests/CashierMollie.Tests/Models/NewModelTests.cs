using CashierMollie.Models;

namespace CashierMollie.Tests.Models;

public class NewModelTests
{
    // --- Order<TKey> ---

    [Fact]
    public void Order_DefaultConstruction_HasCorrectDefaults()
    {
        var order = new Order<string>();

        Assert.Equal(0L, order.Id);
        Assert.Equal("EUR", order.Currency);
        Assert.Equal(0m, order.Subtotal);
        Assert.Equal(0m, order.Tax);
        Assert.Equal(0m, order.Total);
        Assert.Equal(0m, order.TotalDue);
        Assert.Equal(0m, order.CreditUsed);
        Assert.Null(order.Number);
        Assert.Null(order.MolliePaymentId);
        Assert.Null(order.MolliePaymentStatus);
        Assert.Null(order.ProcessedAt);
        Assert.NotNull(order.Items);
        Assert.Empty(order.Items);
    }

    [Fact]
    public void Order_CanSetProperties()
    {
        var order = new Order<string>
        {
            Id = 42,
            OwnerId = "user-1",
            Number = "ORD-000042",
            Currency = "USD",
            Subtotal = 100m,
            Tax = 19m,
            Total = 119m,
            TotalDue = 109m,
            CreditUsed = 10m,
            MolliePaymentId = "tr_test",
            MolliePaymentStatus = "paid"
        };

        Assert.Equal(42L, order.Id);
        Assert.Equal("user-1", order.OwnerId);
        Assert.Equal("ORD-000042", order.Number);
        Assert.Equal("USD", order.Currency);
        Assert.Equal(100m, order.Subtotal);
        Assert.Equal(19m, order.Tax);
        Assert.Equal(119m, order.Total);
        Assert.Equal(109m, order.TotalDue);
        Assert.Equal(10m, order.CreditUsed);
        Assert.Equal("tr_test", order.MolliePaymentId);
        Assert.Equal("paid", order.MolliePaymentStatus);
    }

    [Fact]
    public void Order_Items_CanBePopulated()
    {
        var order = new Order<string> { OwnerId = "user-1" };
        var item = new OrderItem<string> { OwnerId = "user-1", Description = "Pro plan" };
        order.Items.Add(item);

        Assert.Single(order.Items);
    }

    [Fact]
    public void Order_WithIntKey_WorksCorrectly()
    {
        var order = new Order<int> { OwnerId = 42 };
        Assert.Equal(42, order.OwnerId);
    }

    // --- Credit<TKey> ---

    [Fact]
    public void Credit_DefaultConstruction_HasCorrectDefaults()
    {
        var credit = new Credit<string>();

        Assert.Equal(0L, credit.Id);
        Assert.Equal("EUR", credit.Currency);
        Assert.Equal(0m, credit.Balance);
    }

    [Fact]
    public void Credit_CanSetProperties()
    {
        var credit = new Credit<string>
        {
            Id = 1,
            OwnerId = "user-1",
            Currency = "USD",
            Balance = 25.50m
        };

        Assert.Equal(1L, credit.Id);
        Assert.Equal("user-1", credit.OwnerId);
        Assert.Equal("USD", credit.Currency);
        Assert.Equal(25.50m, credit.Balance);
    }

    [Fact]
    public void Credit_WithIntKey_WorksCorrectly()
    {
        var credit = new Credit<int> { OwnerId = 7, Balance = 100m };
        Assert.Equal(7, credit.OwnerId);
        Assert.Equal(100m, credit.Balance);
    }

    // --- Refund<TKey> ---

    [Fact]
    public void Refund_DefaultConstruction_HasCorrectDefaults()
    {
        var refund = new Refund<string>();

        Assert.Equal(0L, refund.Id);
        Assert.Equal("pending", refund.Status);
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal(0m, refund.Amount);
        Assert.Null(refund.Description);
    }

    [Fact]
    public void Refund_CanSetProperties()
    {
        var payment = new Payment<string> { OwnerId = "user-1", MolliePaymentId = "tr_abc" };
        var refund = new Refund<string>
        {
            Id = 5,
            OwnerId = "user-1",
            PaymentId = 10,
            MollieRefundId = "re_xxx",
            Status = "refunded",
            Amount = 9.99m,
            Currency = "EUR",
            Description = "Customer requested refund",
            Payment = payment
        };

        Assert.Equal(5L, refund.Id);
        Assert.Equal("user-1", refund.OwnerId);
        Assert.Equal(10L, refund.PaymentId);
        Assert.Equal("re_xxx", refund.MollieRefundId);
        Assert.Equal("refunded", refund.Status);
        Assert.Equal(9.99m, refund.Amount);
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal("Customer requested refund", refund.Description);
        Assert.Same(payment, refund.Payment);
    }

    [Fact]
    public void Refund_WithIntKey_WorksCorrectly()
    {
        var refund = new Refund<int> { OwnerId = 3, MollieRefundId = "re_int" };
        Assert.Equal(3, refund.OwnerId);
    }

    // --- RedeemedCoupon<TKey> ---

    [Fact]
    public void RedeemedCoupon_DefaultConstruction_HasCorrectDefaults()
    {
        var coupon = new RedeemedCoupon<string>();

        Assert.Equal(0L, coupon.Id);
        Assert.Null(coupon.SubscriptionName);
        Assert.Equal(0, coupon.TimesLeft);
    }

    [Fact]
    public void RedeemedCoupon_CanSetProperties()
    {
        var coupon = new RedeemedCoupon<string>
        {
            Id = 1,
            OwnerId = "user-1",
            Code = "LAUNCH10",
            SubscriptionName = "default",
            TimesLeft = 3
        };

        Assert.Equal(1L, coupon.Id);
        Assert.Equal("user-1", coupon.OwnerId);
        Assert.Equal("LAUNCH10", coupon.Code);
        Assert.Equal("default", coupon.SubscriptionName);
        Assert.Equal(3, coupon.TimesLeft);
    }

    [Fact]
    public void RedeemedCoupon_WithIntKey_WorksCorrectly()
    {
        var coupon = new RedeemedCoupon<int> { OwnerId = 5, Code = "SAVE20" };
        Assert.Equal(5, coupon.OwnerId);
        Assert.Equal("SAVE20", coupon.Code);
    }

    // --- Coupon (non-generic, non-entity) ---

    [Fact]
    public void Coupon_DefaultConstruction_HasCorrectDefaults()
    {
        var coupon = new Coupon();

        Assert.Equal("percentage", coupon.HandlerType);
        Assert.Equal(0, coupon.Times);
        Assert.NotNull(coupon.Context);
        Assert.Empty(coupon.Context);
    }

    [Fact]
    public void Coupon_CanSetProperties()
    {
        var coupon = new Coupon
        {
            Code = "LAUNCH10",
            HandlerType = "fixed_amount",
            Times = 6,
            Context = new Dictionary<string, string>
            {
                ["amount"] = "5.00",
                ["currency"] = "EUR"
            }
        };

        Assert.Equal("LAUNCH10", coupon.Code);
        Assert.Equal("fixed_amount", coupon.HandlerType);
        Assert.Equal(6, coupon.Times);
        Assert.Equal(2, coupon.Context.Count);
        Assert.Equal("5.00", coupon.Context["amount"]);
        Assert.Equal("EUR", coupon.Context["currency"]);
    }

    // --- Payment<TKey> new properties ---

    [Fact]
    public void Payment_NewProperties_DefaultToZero()
    {
        var payment = new Payment<string>();

        Assert.Equal(0m, payment.AmountRefunded);
        Assert.Equal(0m, payment.AmountChargedBack);
    }

    [Fact]
    public void Payment_NewProperties_CanBeSet()
    {
        var payment = new Payment<string>
        {
            OwnerId = "user-1",
            MolliePaymentId = "tr_test",
            Amount = 100m,
            AmountRefunded = 25m,
            AmountChargedBack = 10m
        };

        Assert.Equal(25m, payment.AmountRefunded);
        Assert.Equal(10m, payment.AmountChargedBack);
    }

    // --- OrderItem<TKey> new properties ---

    [Fact]
    public void OrderItem_NewProperties_HaveCorrectDefaults()
    {
        var item = new OrderItem<string>();

        Assert.Null(item.OrderId);
        Assert.Equal(0m, item.Discount);
        Assert.Null(item.Order);
    }

    [Fact]
    public void OrderItem_NewProperties_CanBeSet()
    {
        var order = new Order<string> { Id = 42, OwnerId = "user-1" };
        var item = new OrderItem<string>
        {
            OwnerId = "user-1",
            Description = "Pro plan",
            OrderId = 42,
            Discount = 5.00m,
            Order = order
        };

        Assert.Equal(42L, item.OrderId);
        Assert.Equal(5.00m, item.Discount);
        Assert.Same(order, item.Order);
    }

    // --- Subscription<TKey>.NextPlan ---

    [Fact]
    public void Subscription_NextPlan_DefaultsToNull()
    {
        var sub = new Subscription<string>();

        Assert.Null(sub.NextPlan);
    }

    [Fact]
    public void Subscription_NextPlan_CanBeSet()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Plan = "pro-monthly",
            NextPlan = "team-monthly"
        };

        Assert.Equal("team-monthly", sub.NextPlan);
    }

    // --- ChargeResult<TKey> ---

    [Fact]
    public void ChargeResult_StoresAllProperties()
    {
        var payment = new Payment<string> { OwnerId = "user-1", MolliePaymentId = "tr_test" };
        var order = new Order<string> { OwnerId = "user-1" };

        var result = new ChargeResult<string>(payment, order, "https://checkout.mollie.com/xxx", true);

        Assert.Same(payment, result.Payment);
        Assert.Same(order, result.Order);
        Assert.Equal("https://checkout.mollie.com/xxx", result.CheckoutUrl);
        Assert.True(result.RequiresAction);
    }

    [Fact]
    public void ChargeResult_NullCheckoutUrl_WhenNoActionRequired()
    {
        var payment = new Payment<string> { OwnerId = "user-1", MolliePaymentId = "tr_test" };
        var order = new Order<string> { OwnerId = "user-1" };

        var result = new ChargeResult<string>(payment, order, null, false);

        Assert.Null(result.CheckoutUrl);
        Assert.False(result.RequiresAction);
    }

    [Fact]
    public void ChargeResult_RecordEquality_WorksCorrectly()
    {
        var payment = new Payment<string> { OwnerId = "user-1", MolliePaymentId = "tr_test" };
        var order = new Order<string> { OwnerId = "user-1" };

        var result1 = new ChargeResult<string>(payment, order, null, false);
        var result2 = new ChargeResult<string>(payment, order, null, false);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void ChargeResult_WithIntKey_WorksCorrectly()
    {
        var payment = new Payment<int> { OwnerId = 1, MolliePaymentId = "tr_int" };
        var order = new Order<int> { OwnerId = 1 };

        var result = new ChargeResult<int>(payment, order, null, false);

        Assert.Equal(1, result.Payment.OwnerId);
    }

    // --- PaymentMethodUpdateResult ---

    [Fact]
    public void PaymentMethodUpdateResult_StoresCheckoutUrl()
    {
        var result = new PaymentMethodUpdateResult("https://checkout.mollie.com/update");

        Assert.Equal("https://checkout.mollie.com/update", result.CheckoutUrl);
    }

    [Fact]
    public void PaymentMethodUpdateResult_RecordEquality_WorksCorrectly()
    {
        var result1 = new PaymentMethodUpdateResult("https://checkout.mollie.com/update");
        var result2 = new PaymentMethodUpdateResult("https://checkout.mollie.com/update");
        var result3 = new PaymentMethodUpdateResult("https://checkout.mollie.com/other");

        Assert.Equal(result1, result2);
        Assert.NotEqual(result1, result3);
    }
}
