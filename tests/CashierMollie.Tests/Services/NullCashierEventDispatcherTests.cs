using CashierMollie.Events;
using CashierMollie.Models;
using CashierMollie.Services;

namespace CashierMollie.Tests.Services;

public class NullCashierEventDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_DoesNotThrow()
    {
        var dispatcher = new NullCashierEventDispatcher();
        var sub = new Subscription<string> { OwnerId = "user-1", Plan = "pro" };
        var evt = new SubscriptionCreated<string>(sub, "user-1");

        await dispatcher.DispatchAsync(evt);
        // No exception = pass
    }

    [Fact]
    public async Task DispatchAsync_WithCancellationToken_DoesNotThrow()
    {
        var dispatcher = new NullCashierEventDispatcher();
        using var cts = new CancellationTokenSource();
        var payment = new Payment<string> { OwnerId = "user-1", MolliePaymentId = "tr_test" };
        var evt = new OrderPaymentPaid<string>(payment, null, "user-1");

        await dispatcher.DispatchAsync(evt, cts.Token);
    }
}
