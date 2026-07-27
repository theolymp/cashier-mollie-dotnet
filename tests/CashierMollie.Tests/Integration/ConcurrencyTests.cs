using CashierMollie.Models;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.Integration;

/// <summary>
/// Optimistic concurrency on <see cref="Credit{TKey}"/> and <see cref="Subscription{TKey}"/>,
/// exercised against a real relational database.
/// </summary>
/// <remarks>
/// These entities carry <c>[ConcurrencyCheck]</c> on <c>RowVersion</c> and the context declares
/// <c>IsConcurrencyToken()</c>, so a lost update is meant to be rejected rather than silently
/// overwritten. On <c>Credit</c> that guard protects an account balance -- money -- which is why
/// it is worth a test that can actually fail.
/// </remarks>
public class ConcurrencyTests
{
    private static Credit<string> NewCredit() => new()
    {
        OwnerId = "owner-1",
        Currency = "EUR",
        Balance = 100m,
    };

    [Fact]
    public async Task Credit_ConcurrentUpdate_SecondWriterIsRejected()
    {
        using var db = new SqliteTestDatabase<string>();

        await using (var seed = db.NewContext())
        {
            seed.Credits.Add(NewCredit());
            await seed.SaveChangesAsync();
        }

        // Two writers read the same row, each unaware of the other.
        await using var writerA = db.NewContext();
        await using var writerB = db.NewContext();

        var creditA = await writerA.Credits.SingleAsync();
        var creditB = await writerB.Credits.SingleAsync();

        creditA.Balance -= 40m;
        await writerA.SaveChangesAsync();

        // B still holds the pre-update RowVersion, so its UPDATE must match no rows.
        creditB.Balance -= 70m;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => writerB.SaveChangesAsync());

        // The balance reflects A only: 100 - 40. Without the guard both writers would apply and
        // 110 would have been spent from a balance of 100.
        await using var verify = db.NewContext();
        var stored = await verify.Credits.SingleAsync();
        Assert.Equal(60m, stored.Balance);
    }

    [Fact]
    public async Task Credit_RowVersion_IsIncrementedOnEachSave()
    {
        using var db = new SqliteTestDatabase<string>();

        await using (var seed = db.NewContext())
        {
            seed.Credits.Add(NewCredit());
            await seed.SaveChangesAsync();
        }

        await using var context = db.NewContext();
        var credit = await context.Credits.SingleAsync();
        var initial = credit.RowVersion;

        credit.Balance += 10m;
        await context.SaveChangesAsync();

        Assert.Equal(initial + 1, credit.RowVersion);
    }

    [Fact]
    public async Task Subscription_ConcurrentUpdate_SecondWriterIsRejected()
    {
        using var db = new SqliteTestDatabase<string>();

        await using (var seed = db.NewContext())
        {
            seed.Subscriptions.Add(new Subscription<string>
            {
                OwnerId = "owner-1",
                Name = "default",
                Plan = "pro-monthly",
                Status = SubscriptionStatus.Active,
            });
            await seed.SaveChangesAsync();
        }

        await using var writerA = db.NewContext();
        await using var writerB = db.NewContext();

        var subA = await writerA.Subscriptions.SingleAsync();
        var subB = await writerB.Subscriptions.SingleAsync();

        subA.Quantity = 5;
        await writerA.SaveChangesAsync();

        subB.Status = SubscriptionStatus.Cancelled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => writerB.SaveChangesAsync());
    }

    /// <summary>
    /// Records a fact that is easy to get wrong: the InMemory provider <b>does</b> enforce
    /// concurrency tokens on EF Core 10, contrary to its widely repeated reputation.
    /// </summary>
    /// <remarks>
    /// This test was originally written to assert the opposite -- that InMemory silently accepts
    /// the lost update -- as the justification for adding a relational harness. It failed, which
    /// is how the assumption was caught. Kept, inverted, so nobody re-derives the folklore: if a
    /// future EF version drops this behaviour, this test goes red and says so explicitly rather
    /// than quietly weakening every concurrency assertion that relies on it.
    /// </remarks>
    [Fact]
    public async Task InMemoryProvider_DoesEnforceConcurrency_contraryToFolklore()
    {
        const string shared = "concurrency-inmemory";

        await using (var seed = TestDbContextFactory.Create(shared))
        {
            seed.Credits.Add(NewCredit());
            await seed.SaveChangesAsync();
        }

        await using var writerA = TestDbContextFactory.Create(shared);
        await using var writerB = TestDbContextFactory.Create(shared);

        var creditA = await writerA.Credits.SingleAsync();
        var creditB = await writerB.Credits.SingleAsync();

        creditA.Balance -= 40m;
        await writerA.SaveChangesAsync();

        creditB.Balance -= 70m;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => writerB.SaveChangesAsync());
    }
}
