using CashierMollie.Models;
using CashierMollie.Services;

namespace CashierMollie.Tests.Services;

public class NullInvoiceGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsCompletedTask()
    {
        var generator = new NullInvoiceGenerator<string>();
        var order = new Order<string> { OwnerId = "user-1", Currency = "EUR" };

        var task = generator.GenerateAsync(order);

        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task GenerateAsync_DoesNotThrow()
    {
        var generator = new NullInvoiceGenerator<string>();
        var order = new Order<string>
        {
            OwnerId = "user-1",
            Currency = "EUR",
            Subtotal = 9.99m,
            Tax = 1.90m,
            Total = 11.89m,
            TotalDue = 11.89m,
            Number = "ORD-000001"
        };

        var exception = await Record.ExceptionAsync(() => generator.GenerateAsync(order));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GenerateAsync_WithCancellationToken_ReturnsCompletedTask()
    {
        var generator = new NullInvoiceGenerator<string>();
        var order = new Order<string> { OwnerId = "user-1", Currency = "EUR" };
        using var cts = new CancellationTokenSource();

        var task = generator.GenerateAsync(order, cts.Token);

        Assert.True(task.IsCompleted);
        await task;
    }
}
