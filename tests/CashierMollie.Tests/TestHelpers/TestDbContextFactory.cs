using CashierMollie.Data;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.TestHelpers;

public static class TestDbContextFactory
{
    public static CashierDbContext<TKey> Create<TKey>(string? dbName = null)
        where TKey : IEquatable<TKey>
    {
        var options = new DbContextOptionsBuilder<CashierDbContext<TKey>>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new CashierDbContext<TKey>(options);
    }

    /// <summary>Convenience: create context with string keys (most common)</summary>
    public static CashierDbContext<string> Create(string? dbName = null)
        => Create<string>(dbName);
}
