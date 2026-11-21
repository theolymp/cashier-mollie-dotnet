using CashierMollie.Data;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// Resuming registers a new subscription at Mollie. It claims the row first, so two resumes at
/// once register one subscription, and a registration whose id cannot be stored is cancelled
/// again instead of charging unseen.
/// </summary>
public class ResumeClaimTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>().WithRecurringDefaults();
    private readonly CashierMollieOptions _options = new() { WebhookUrl = "https://shop.example/webhooks/mollie" };
    private readonly DateTimeOffset _endsAt = DateTimeOffset.UtcNow.AddDays(12);

    private MollieBillingEngine<string> Engine(CashierDbContext<string> db) =>
        new(db, _mollie, Substitute.For<ICashierEventDispatcher>(), Options.Create(_options),
            new CouponService<string>(db, Substitute.For<ICouponRepository>(), Substitute.For<ICashierEventDispatcher>()),
            new FakePlanPriceResolver<string>().With("pro", 9.99m), Substitute.For<IRefundService<string>>(),
            NullLogger<MollieBillingEngine<string>>.Instance);

    private async Task SeedCancelled()
    {
        await using var db = TestDbContextFactory.Create(_dbName);
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "main", Plan = "pro", MollieCustomerId = "cst_1",
            MollieSubscriptionId = "sub_old", Status = SubscriptionStatus.Cancelled, EndsAt = _endsAt,
            PriceNet = 9.99m, TaxRatePercent = 19m, Description = "Plan pro",
            CycleStartedAt = _endsAt.AddMonths(-1), CycleValueNet = 9.99m,
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_paid", Status = "paid",
            Amount = 11.89m, AmountNet = 9.99m, TaxRatePercent = 19m, MollieMandateId = "mdt_1",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Subscription<string>> Stored()
    {
        await using var db = TestDbContextFactory.Create(_dbName);
        return await db.Subscriptions.SingleAsync();
    }

    private Task Registered(int times) => _mollie.Received(times).CreateSubscriptionAsync(
        Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
        Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task TwoResumesAtOnce_RegisterOneSubscriptionAtMollie()
    {
        await SeedCancelled();
        await using var dbA = TestDbContextFactory.Create(_dbName);
        await using var dbB = TestDbContextFactory.Create(_dbName);
        var subA = await dbA.Subscriptions.SingleAsync();
        var subB = await dbB.Subscriptions.SingleAsync();

        await Engine(dbA).ResumeSubscriptionAsync(subA);
        await Assert.ThrowsAsync<CashierException>(() => Engine(dbB).ResumeSubscriptionAsync(subB));

        await Registered(1);
        var stored = await Stored();
        Assert.Equal("sub_auto_1", stored.MollieSubscriptionId);
        Assert.Equal(SubscriptionStatus.Active, stored.Status);
    }

    [Fact]
    public async Task AFailedRegistration_LeavesItCancelledWithTheOldId()
    {
        await SeedCancelled();
        _mollie.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Mollie down"));
        await using var db = TestDbContextFactory.Create(_dbName);

        var sub = await db.Subscriptions.SingleAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => Engine(db).ResumeSubscriptionAsync(sub));

        var stored = await Stored();
        Assert.Equal(SubscriptionStatus.Cancelled, stored.Status);
        Assert.Equal("sub_old", stored.MollieSubscriptionId);
        Assert.Equal(_endsAt, stored.EndsAt);
    }

    [Fact]
    // The row changes between the call to Mollie and the save (here: a concurrent writer). The id
    // Mollie returned cannot be stored, so that Mollie subscription is cancelled again.
    public async Task ASaveThatFailsAfterMollie_CancelsTheNewMollieSubscription()
    {
        await SeedCancelled();
        _mollie.When(m => m.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                using var other = TestDbContextFactory.Create(_dbName);
                other.Subscriptions.Single().Description = "changed meanwhile";
                other.SaveChanges();
            });
        await using var db = TestDbContextFactory.Create(_dbName);

        var sub = await db.Subscriptions.SingleAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Engine(db).ResumeSubscriptionAsync(sub));

        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_auto_1", Arg.Any<CancellationToken>());
    }
}
