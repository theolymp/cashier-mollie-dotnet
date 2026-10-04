using CashierMollie.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// Entry point for the PostgreSQL test harness: availability and database creation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists on top of the SQLite harness.</b> SQLite covers relational schema creation
/// and SQL translation, which InMemory does not. It does not cover translation against the
/// provider the consumers actually run: measured 2026-10-04, both fleet consumers deploy on
/// PostgreSQL. The same model resolves <c>decimal</c> differently per provider -- <c>numeric</c>
/// on PostgreSQL, <c>TEXT</c> on SQLite, <c>decimal(18,2)</c> on SQL Server -- so a money
/// calculation verified only on SQLite is verified on the wrong engine.
/// </para>
/// <para>
/// <b>Configuration is deliberately explicit.</b> There is no Testcontainers dependency and no
/// Docker socket requirement: the connection string comes from
/// <c>CASHIERMOLLIE_TEST_POSTGRES</c>. CI supplies it from a service container; locally a
/// contributor can point it at any throwaway PostgreSQL. When it is absent the tests
/// <i>skip with a stated reason</i> rather than silently passing -- and CI asserts that they
/// actually ran, because a skipped test and a passing one look the same in a summary line.
/// </para>
/// </remarks>
public static class PostgresTestServer
{
    /// <summary>The configured admin connection string, or null when PostgreSQL is not available.</summary>
    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable("CASHIERMOLLIE_TEST_POSTGRES") is { Length: > 0 } s ? s : null;

    /// <summary>Creates a fresh database and applies the CashierMollie schema to it.</summary>
    public static async Task<PostgresTestDatabase<TKey>> CreateAsync<TKey>()
        where TKey : IEquatable<TKey>
    {
        var admin = AdminConnectionString
            ?? throw new InvalidOperationException(
                "CASHIERMOLLIE_TEST_POSTGRES is not set. Guard the test with [RequiresPostgresFact].");

        // The name is generated here and never taken from input.
        var name = "cm_test_" + Guid.NewGuid().ToString("n")[..16];
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var instance = new PostgresTestDatabase<TKey>(admin, target, name);
        await using var ctx = instance.NewContext();
        await ctx.Database.EnsureCreatedAsync();
        return instance;
    }
}

/// <summary>One throwaway PostgreSQL database, dropped on dispose.</summary>
public sealed class PostgresTestDatabase<TKey> : IAsyncDisposable
    where TKey : IEquatable<TKey>
{
    private readonly string _admin;
    private readonly string _target;
    private readonly string _name;

    internal PostgresTestDatabase(string admin, string target, string name)
    {
        _admin = admin;
        _target = target;
        _name = name;
    }

    /// <summary>A fresh context over the same database; each has its own change tracker.</summary>
    public CashierDbContext<TKey> NewContext()
    {
        var options = new DbContextOptionsBuilder<CashierDbContext<TKey>>()
            .UseNpgsql(_target)
            .Options;
        return new CashierDbContext<TKey>(options);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }
}
