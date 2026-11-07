using CashierMollie.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// Entry point for the SQL Server test harness: availability and database creation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fourth harness.</b> The other three cannot see the one difference that decides money.
/// InMemory runs LINQ in-process against object graphs and has no column types at all; SQLite
/// stores <c>decimal</c> as <c>TEXT</c>; PostgreSQL resolves the same model to unconstrained
/// <c>numeric</c> and keeps every digit. SQL Server resolves it to <c>decimal(18,2)</c> and
/// <i>rounds</i>. That is not a theoretical difference: the same library code then produces
/// different amounts on different providers, and the tests in
/// <see cref="Integration.SqlServerTranslationTests"/> measure it rather than quote it.
/// </para>
/// <para>
/// <b>No consumer deploys on SQL Server today</b>, which is exactly why this was the gap: the
/// provider nobody runs is the provider nobody notices. The coverage exists so that the explicit
/// precision about to be put on those columns can be shown to change something, and shown to
/// change it on the provider where it matters.
/// </para>
/// <para>
/// <b>Configuration is deliberately explicit</b>, as for PostgreSQL: the connection string comes
/// from <c>CASHIERMOLLIE_TEST_SQLSERVER</c>, there is no Testcontainers dependency and no Docker
/// socket requirement. When it is absent the tests <i>skip with a stated reason</i>, and CI asserts
/// separately that they really ran, because a skipped test and a passing one look the same in a
/// summary line. Note that <c>Microsoft.Data.SqlClient</c> encrypts by default, so a throwaway
/// server with a self-signed certificate needs <c>TrustServerCertificate=True</c> in that string --
/// without it every test here fails on the connection rather than on anything it measures.
/// </para>
/// </remarks>
public static class SqlServerTestServer
{
    /// <summary>The configured admin connection string, or null when SQL Server is not available.</summary>
    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable("CASHIERMOLLIE_TEST_SQLSERVER") is { Length: > 0 } s ? s : null;

    /// <summary>Creates a fresh database and applies the CashierMollie schema to it.</summary>
    public static async Task<SqlServerTestDatabase<TKey>> CreateAsync<TKey>()
        where TKey : IEquatable<TKey>
    {
        var admin = AdminConnectionString
            ?? throw new InvalidOperationException(
                "CASHIERMOLLIE_TEST_SQLSERVER is not set. Guard the test with [RequiresSqlServerFact].");

        // The name is generated here and never taken from input.
        var name = "cm_test_" + Guid.NewGuid().ToString("n")[..16];
        await using (var conn = new SqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{name}]";
            await cmd.ExecuteNonQueryAsync();
        }

        var target = new SqlConnectionStringBuilder(admin) { InitialCatalog = name }.ConnectionString;
        var instance = new SqlServerTestDatabase<TKey>(admin, target, name);
        await using var ctx = instance.NewContext();
        await ctx.Database.EnsureCreatedAsync();
        return instance;
    }
}

/// <summary>One throwaway SQL Server database, dropped on dispose.</summary>
public sealed class SqlServerTestDatabase<TKey> : IAsyncDisposable
    where TKey : IEquatable<TKey>
{
    private readonly string _admin;
    private readonly string _target;
    private readonly string _name;

    internal SqlServerTestDatabase(string admin, string target, string name)
    {
        _admin = admin;
        _target = target;
        _name = name;
    }

    /// <summary>A fresh context over the same database; each has its own change tracker.</summary>
    public CashierDbContext<TKey> NewContext()
    {
        var options = new DbContextOptionsBuilder<CashierDbContext<TKey>>()
            .UseSqlServer(_target)
            .Options;
        return new CashierDbContext<TKey>(options);
    }

    /// <summary>Every decimal column in the database, as the catalog reports it.</summary>
    /// <remarks>
    /// <para>
    /// Asked of the server, not of the model: the question these tests exist for is what the
    /// provider decided, and the model is the thing whose decision is in doubt.
    /// </para>
    /// <para>
    /// The set is read rather than enumerated by name on purpose. A hard-coded list of columns is
    /// an enumeration of members and goes stale the moment someone adds a decimal property, and it
    /// would go stale silently -- the new column would simply not be checked. Reading the catalog
    /// makes the count itself an assertion.
    /// </para>
    /// </remarks>
    public async Task<List<(string Table, string Column, string DataType, int Precision, int Scale)>>
        DecimalColumnsAsync()
    {
        await using var ctx = NewContext();
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, NUMERIC_PRECISION, NUMERIC_SCALE "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('decimal', 'numeric') "
            + "ORDER BY TABLE_NAME, COLUMN_NAME";

        var found = new List<(string, string, string, int, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3), provider: null),
                Convert.ToInt32(reader.GetValue(4), provider: null)));
        }

        return found;
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // SQL Server refuses to drop a database that still has sessions on it, and a pooled
        // connection from a test that already finished counts. SINGLE_USER WITH ROLLBACK IMMEDIATE
        // evicts them; without it the drop fails and the next run inherits the leftovers.
        cmd.CommandText =
            $"IF DB_ID('{_name}') IS NOT NULL BEGIN "
            + $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{_name}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}
