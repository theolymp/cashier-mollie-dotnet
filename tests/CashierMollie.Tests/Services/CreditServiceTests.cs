using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class CreditServiceTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CreditService<string> _service;

    public CreditServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions());
        _service = new CreditService<string>(_db, _dispatcher, options);
    }

    [Fact]
    public async Task GetBalanceAsync_NoCredit_ReturnsZero()
    {
        var owner = new TestBillable("u1", "cst_test");
        decimal balance = await _service.GetBalanceAsync(owner);
        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task AddCreditAsync_CreatesNewRecord()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m);
        decimal balance = await _service.GetBalanceAsync(owner);
        Assert.Equal(10.00m, balance);
    }

    [Fact]
    public async Task AddCreditAsync_IncrementsExisting()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m);
        await _service.AddCreditAsync(owner, 5.00m);
        decimal balance = await _service.GetBalanceAsync(owner);
        Assert.Equal(15.00m, balance);
    }

    [Fact]
    public async Task ApplyCreditAsync_DeductsBalance()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m);
        decimal used = await _service.ApplyCreditAsync(owner, 7.00m);
        Assert.Equal(7.00m, used);
        decimal remaining = await _service.GetBalanceAsync(owner);
        Assert.Equal(3.00m, remaining);
    }

    [Fact]
    public async Task ApplyCreditAsync_CapsAtBalance()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 5.00m);
        decimal used = await _service.ApplyCreditAsync(owner, 10.00m);
        Assert.Equal(5.00m, used);
        decimal remaining = await _service.GetBalanceAsync(owner);
        Assert.Equal(0m, remaining);
    }

    [Fact]
    public async Task ApplyCreditAsync_NoCredit_ReturnsZero()
    {
        var owner = new TestBillable("u1", "cst_test");
        decimal used = await _service.ApplyCreditAsync(owner, 10.00m);
        Assert.Equal(0m, used);
    }

    [Fact]
    public async Task HasCreditAsync_ReturnsCorrectly()
    {
        var owner = new TestBillable("u1", "cst_test");
        Assert.False(await _service.HasCreditAsync(owner));
        await _service.AddCreditAsync(owner, 5.00m);
        Assert.True(await _service.HasCreditAsync(owner));
    }

    [Fact]
    public async Task MultiCurrency_BalancesAreIndependent()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m, "EUR");
        await _service.AddCreditAsync(owner, 20.00m, "USD");
        Assert.Equal(10.00m, await _service.GetBalanceAsync(owner, "EUR"));
        Assert.Equal(20.00m, await _service.GetBalanceAsync(owner, "USD"));
    }

    [Fact]
    public async Task AddCreditAsync_DispatchesCreditAddedEvent()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m, "EUR", "bonus");
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Events.CreditAdded<string>>(e =>
                e.Amount == 10.00m && e.Currency == "EUR" && e.Description == "bonus" && e.OwnerId == "u1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCreditAsync_DispatchesCreditAppliedEvent()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.AddCreditAsync(owner, 10.00m);
        await _service.ApplyCreditAsync(owner, 7.00m);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Events.CreditApplied<string>>(e =>
                e.Amount == 7.00m && e.Currency == "EUR" && e.OwnerId == "u1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCreditAsync_NoCredit_DoesNotDispatchEvent()
    {
        var owner = new TestBillable("u1", "cst_test");
        await _service.ApplyCreditAsync(owner, 10.00m);
        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Events.CreditApplied<string>>(),
            Arg.Any<CancellationToken>());
    }

    public void Dispose() => _db.Dispose();
}
