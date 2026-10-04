using CashierMollie.Models;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.Integration;

/// <summary>
/// Translation and storage against PostgreSQL, the provider both consumers actually deploy on.
/// </summary>
/// <remarks>
/// Each test here pins something that was measured rather than assumed, and that neither the
/// InMemory nor the SQLite harness can establish. See <see cref="PostgresTestDatabase{TKey}"/> for
/// why a third harness exists at all.
/// </remarks>
public class PostgresTranslationTests
{
    /// <summary>
    /// The harness really reached PostgreSQL. Without this, a silent fallback to another provider
    /// would leave every other test in this class green while measuring the wrong engine -- the
    /// known-positive for the harness itself, not for the library.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Harness_ReallyUsesPostgres()
    {
        await using var db = await PostgresTestServer.CreateAsync<string>();
        await using var ctx = db.NewContext();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", ctx.Database.ProviderName);
    }

    /// <summary>
    /// The model is expressible as a real PostgreSQL schema. InMemory accepts models no relational
    /// schema can express, so this is not implied by a green unit suite.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Schema_IsCreatedWithAllSevenCashierTables()
    {
        await using var db = await PostgresTestServer.CreateAsync<string>();
        await using var ctx = db.NewContext();

        var expected = new[]
        {
            "cashier_subscriptions", "cashier_order_items", "cashier_payments",
            "cashier_orders", "cashier_credits", "cashier_refunds", "cashier_redeemed_coupons",
        };

        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'";
        var found = new List<string>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync()) found.Add(rd.GetString(0));
        }

        // Known-positive for the query itself: an empty result would make the assertion below
        // vacuous for any expectation, so prove rows came back before trusting the membership.
        Assert.NotEmpty(found);
        foreach (var table in expected)
        {
            Assert.Contains(table, found);
        }
    }

    /// <summary>
    /// <c>decimal</c> survives a round trip at full precision. Measured, not inferred from the
    /// column type: PostgreSQL resolves these columns to unconstrained <c>numeric</c>, whereas
    /// SQL Server would resolve the same model to <c>decimal(18,2)</c> and keep two places. A
    /// proration produces fractional cents, so this is the property that decides whether the same
    /// code yields the same money on both.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Decimal_RoundTripsWithMoreThanTwoDecimalPlaces()
    {
        await using var db = await PostgresTestServer.CreateAsync<string>();

        // 11 days of a 29.99 month -- the shape a proration intermediate actually has.
        const decimal awkward = 11.3862m;

        await using (var write = db.NewContext())
        {
            write.Set<Credit<string>>().Add(new Credit<string>
            {
                OwnerId = "user-1", Currency = "EUR", Balance = awkward,
            });
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var stored = await read.Set<Credit<string>>().SingleAsync();

        Assert.Equal(awkward, stored.Balance);
    }

    /// <summary>
    /// The one decimal comparison the library performs <i>in SQL</i> (<c>Balance &gt; 0</c>, from
    /// the credit lookup) translates and answers correctly on PostgreSQL. The same three cases
    /// measured correct on SQLite; this establishes it on the deployed engine.
    /// </summary>
    [RequiresPostgresFact]
    public async Task DecimalComparison_InSql_IsCorrectForNegativeZeroAndPositive()
    {
        await using var db = await PostgresTestServer.CreateAsync<string>();

        await using (var write = db.NewContext())
        {
            write.Set<Credit<string>>().AddRange(
                new Credit<string> { OwnerId = "neg", Currency = "EUR", Balance = -5.00m },
                new Credit<string> { OwnerId = "pos", Currency = "EUR", Balance = 5.00m },
                new Credit<string> { OwnerId = "zero", Currency = "EUR", Balance = 0.00m });
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        async Task<bool> HasCreditInSql(string owner) =>
            await read.Set<Credit<string>>()
                .AnyAsync(c => c.OwnerId == owner && c.Currency == "EUR" && c.Balance > 0);

        Assert.False(await HasCreditInSql("neg"));
        Assert.True(await HasCreditInSql("pos"));
        Assert.False(await HasCreditInSql("zero"));
    }

    /// <summary>
    /// Optimistic concurrency holds on PostgreSQL. The library sets <c>RowVersion</c> in its
    /// <c>SaveChangesAsync</c> override rather than relying on a provider-native rowversion, so
    /// whether that produces a real conflict is a per-provider question.
    /// </summary>
    [RequiresPostgresFact]
    public async Task ConcurrencyToken_DetectsAConflictBetweenTwoWriters()
    {
        await using var db = await PostgresTestServer.CreateAsync<string>();

        await using (var seed = db.NewContext())
        {
            seed.Subscriptions.Add(new Subscription<string>
            {
                OwnerId = "user-1", Name = "default", Plan = "pro",
                Status = SubscriptionStatus.Active,
            });
            await seed.SaveChangesAsync();
        }

        await using var first = db.NewContext();
        await using var second = db.NewContext();
        var a = await first.Subscriptions.SingleAsync();
        var b = await second.Subscriptions.SingleAsync();

        a.Plan = "team";
        await first.SaveChangesAsync();

        b.Plan = "enterprise";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }
}
