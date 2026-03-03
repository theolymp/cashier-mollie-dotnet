using CashierMollie.Data;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.TestHelpers;

public static class TestDbContextFactory
{
    public static CashierDbContext Create(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<CashierDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new CashierDbContext(options);
    }
}
