using CashierMollie.Models;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.Integration;

/// <summary>
/// Translation and storage against SQL Server, the provider where <c>decimal</c> loses digits.
/// </summary>
/// <remarks>
/// <para>
/// No consumer deploys on SQL Server, which is why this was the gap rather than despite it: the
/// provider nobody runs is the provider nobody notices. These tests exist so that the explicit
/// precision about to be put on the money columns can be shown to change something, on the
/// provider where it matters, instead of being asserted against a model that never reaches a
/// server.
/// </para>
/// <para>
/// See <see cref="SqlServerTestDatabase{TKey}"/> for why a fourth harness exists at all.
/// </para>
/// </remarks>
public class SqlServerTranslationTests
{
    /// <summary>
    /// The harness really reached SQL Server. Without this, a silent fallback to another provider
    /// would leave every other test in this class green while measuring the wrong engine -- the
    /// known-positive for the harness itself, not for the library.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Harness_ReallyUsesSqlServer()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();
        await using var ctx = db.NewContext();

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", ctx.Database.ProviderName);
    }

    /// <summary>
    /// The model is expressible as a real SQL Server schema. InMemory accepts models no relational
    /// schema can express, so this is not implied by a green unit suite.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Schema_IsCreatedWithAllSevenCashierTables()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();
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
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
        var found = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) found.Add(reader.GetString(0));
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
    /// Every money column resolves to the provider's default <c>decimal(18,2)</c>, because the
    /// model sets no explicit precision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserts a defect, deliberately, and is expected to change.</b> Thirteen mapped
    /// <c>decimal</c> properties carry no <c>HasPrecision</c>, so each provider picks: unconstrained
    /// <c>numeric</c> on PostgreSQL, <c>TEXT</c> on SQLite, <c>decimal(18,2)</c> here. The same code
    /// therefore stores different amounts depending on where it runs, which is tolerable only
    /// while the library performs no money arithmetic -- proration throws today.
    /// </para>
    /// <para>
    /// When explicit precision is set, this test goes red and the expected scale is the one line to
    /// change. That is the point of writing it now: the schema change has to be visible as a
    /// change, and on the provider that shows it.
    /// </para>
    /// <para>
    /// The columns are read from the catalog rather than listed here. A list of names is an
    /// enumeration of members: it goes stale when someone adds a decimal property, and silently,
    /// because the new column would simply not be checked. The count is therefore part of the
    /// assertion.
    /// </para>
    /// </remarks>
    [RequiresSqlServerFact]
    public async Task DecimalColumns_ResolveToTheProviderDefault_BecauseNoPrecisionIsSet()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();

        var columns = await db.DecimalColumnsAsync();

        // 13 mapped decimal properties across the five models that have them. Three further
        // decimal members (OrderItem.Total, TaxAmount, TotalWithTax) are computed and not mapped,
        // so a count of 16 here would mean EF had started persisting them.
        Assert.Equal(13, columns.Count);
        foreach (var (table, column, dataType, precision, scale) in columns)
        {
            Assert.Equal("decimal", dataType);
            Assert.Equal(18, precision);
            Assert.Equal(2, scale);
            Assert.StartsWith("cashier_", table, StringComparison.Ordinal);
            Assert.NotEmpty(column);
        }
    }

    /// <summary>
    /// A <c>decimal</c> with more than two places does not survive a round trip here, where it does
    /// on PostgreSQL. The same value, the same code, two different amounts.
    /// </summary>
    /// <remarks>
    /// This is the consequence of the column type above, stated as money rather than as metadata,
    /// and it is the reason the precision has to be set before any computation lands rather than
    /// after. The mirror of this test in <see cref="PostgresTranslationTests"/> asserts the value
    /// comes back whole; the contrast between the two is the finding.
    /// </remarks>
    [RequiresSqlServerFact]
    public async Task Decimal_IsRoundedToTwoPlaces_WherePostgresKeepsEveryDigit()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();

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

        Assert.Equal(11.39m, stored.Balance);
        Assert.NotEqual(awkward, stored.Balance);
    }

    /// <summary>
    /// The one decimal comparison the library performs <i>in SQL</i> (<c>Balance &gt; 0</c>, from
    /// the credit lookup) translates and answers correctly on SQL Server too.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task DecimalComparison_InSql_IsCorrectForNegativeZeroAndPositive()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();

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
    /// Optimistic concurrency holds on SQL Server. The library sets <c>RowVersion</c> in its
    /// <c>SaveChangesAsync</c> override rather than relying on the provider-native
    /// <c>rowversion</c> type, so whether that produces a real conflict is a per-provider question
    /// -- and this is the provider that has a native mechanism the library is not using.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task ConcurrencyToken_DetectsAConflictBetweenTwoWriters()
    {
        await using var db = await SqlServerTestServer.CreateAsync<string>();

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
