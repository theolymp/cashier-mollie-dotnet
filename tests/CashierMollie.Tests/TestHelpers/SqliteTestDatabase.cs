using CashierMollie.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// A real relational database for tests, backed by in-memory SQLite.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over the InMemory provider, measured rather than assumed:</b> the schema is
/// really created relationally and every query is really translated to SQL. Neither is exercised
/// by InMemory, which executes LINQ in-process against object graphs -- a query it runs happily
/// can be untranslatable against any actual database, and a model it accepts can be one no
/// relational schema can express.
/// </para>
/// <para>
/// It is <i>not</i> true that InMemory skips constraint checks, which is the usual reason given
/// for a harness like this. Measured on EF Core 10: InMemory enforces concurrency tokens and
/// NOT NULL just as SQLite does. Two plausible justifications for this class turned out to be
/// folklore; the two above are what survived measurement.
/// </para>
/// <para>
/// The connection is held open deliberately: an in-memory SQLite database exists only as long as
/// a connection to it does, so closing it would discard the schema between contexts. Holding it
/// open is also what lets <see cref="NewContext"/> hand out several independent contexts over the
/// <i>same</i> database, which is what simulating two concurrent writers requires.
/// </para>
/// </remarks>
public sealed class SqliteTestDatabase<TKey> : IDisposable
    where TKey : IEquatable<TKey>
{
    private readonly SqliteConnection _connection;

    /// <summary>Creates the database and applies the CashierMollie schema.</summary>
    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    /// <summary>
    /// Returns a fresh context over the same database. Each has its own change tracker, so two of
    /// them model two independent writers.
    /// </summary>
    public CashierDbContext<TKey> NewContext()
    {
        var options = new DbContextOptionsBuilder<CashierDbContext<TKey>>()
            .UseSqlite(_connection)
            .Options;
        return new CashierDbContext<TKey>(options);
    }

    /// <summary>Closes the connection, which discards the in-memory database.</summary>
    public void Dispose() => _connection.Dispose();
}
