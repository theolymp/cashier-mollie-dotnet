using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Integration;

public class CreditIntegrationTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CreditService<string> _creditService;

    public CreditIntegrationTests()
    {
        _db = TestDbContextFactory.Create();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions
        {
            Currency = "EUR",
        });
        _creditService = new CreditService<string>(_db, _eventDispatcher, options);
    }

    [Fact]
    public async Task AddCredit_IncreasesBalance()
    {
        var owner = new TestBillable("credit-user-1", "cst_cr1", "mdt_cr1");

        // Initially no balance
        var initialBalance = await _creditService.GetBalanceAsync(owner);
        Assert.Equal(0m, initialBalance);

        // Add 25 EUR credit
        await _creditService.AddCreditAsync(owner, 25.00m);

        var balance = await _creditService.GetBalanceAsync(owner);
        Assert.Equal(25.00m, balance);
        Assert.True(await _creditService.HasCreditAsync(owner));

        // Add more credit — should accumulate
        await _creditService.AddCreditAsync(owner, 10.00m);

        var updatedBalance = await _creditService.GetBalanceAsync(owner);
        Assert.Equal(35.00m, updatedBalance);

        // Verify CreditAdded events were dispatched
        await _eventDispatcher.Received(2).DispatchAsync(
            Arg.Any<CreditAdded<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCredit_DecreasesBalance()
    {
        var owner = new TestBillable("credit-user-2", "cst_cr2", "mdt_cr2");

        // Add 50 EUR credit
        await _creditService.AddCreditAsync(owner, 50.00m);
        Assert.Equal(50.00m, await _creditService.GetBalanceAsync(owner));

        // Apply 20 EUR
        var used = await _creditService.ApplyCreditAsync(owner, 20.00m);
        Assert.Equal(20.00m, used);

        // Remaining balance should be 30 EUR
        var remaining = await _creditService.GetBalanceAsync(owner);
        Assert.Equal(30.00m, remaining);
        Assert.True(await _creditService.HasCreditAsync(owner));

        // Verify CreditApplied event was dispatched
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<CreditApplied<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCredit_CapsAtBalance()
    {
        var owner = new TestBillable("credit-user-3", "cst_cr3", "mdt_cr3");

        // Add 15 EUR credit
        await _creditService.AddCreditAsync(owner, 15.00m);

        // Try to apply 100 EUR — should cap at 15 EUR
        var used = await _creditService.ApplyCreditAsync(owner, 100.00m);
        Assert.Equal(15.00m, used);

        // Balance should be zero
        var remaining = await _creditService.GetBalanceAsync(owner);
        Assert.Equal(0m, remaining);
        Assert.False(await _creditService.HasCreditAsync(owner));

        // Apply again with zero balance — should return 0
        var usedAgain = await _creditService.ApplyCreditAsync(owner, 10.00m);
        Assert.Equal(0m, usedAgain);
    }

    public void Dispose() => _db.Dispose();
}
