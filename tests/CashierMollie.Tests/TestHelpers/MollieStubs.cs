using CashierMollie.Interfaces;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Subscription.Response;
using NSubstitute;

namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// Default answers for the two Mollie calls a priced subscription makes: a recurring payment and
/// a subscription registration. Unique ids per call, because the payment id is a unique column.
/// A test that is about these calls configures its own answer on top.
/// </summary>
public static class MollieStubs
{
    public static IMollieClientService WithRecurringDefaults(this IMollieClientService mollie)
    {
        int payments = 0;
        int subscriptions = 0;

        mollie.CreateRecurringPaymentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var p = Substitute.For<PaymentResponse>();
                p.Id = $"tr_recurring_{Interlocked.Increment(ref payments)}";
                p.Status = "pending";
                return Task.FromResult(p);
            });

        mollie.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var s = Substitute.For<SubscriptionResponse>();
                s.Id = $"sub_auto_{Interlocked.Increment(ref subscriptions)}";
                return Task.FromResult(s);
            });

        return mollie;
    }
}
