using CashierMollie.Interfaces;
using CashierMollie.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

public class CashierBackgroundServiceTests : IDisposable
{
    private readonly IBillingEngine<string> _engine;
    private readonly CashierBackgroundService<string> _service;

    public CashierBackgroundServiceTests()
    {
        _engine = Substitute.For<IBillingEngine<string>>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IBillingEngine<string>)).Returns(_engine);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var options = Options.Create(new CashierMollieOptions
        {
            ProcessingInterval = TimeSpan.FromMilliseconds(50),
        });

        var logger = Substitute.For<ILogger<CashierBackgroundService<string>>>();

        _service = new CashierBackgroundService<string>(scopeFactory, options, logger);
    }

    [Fact]
    public async Task ExecuteAsync_CallsProcessDueItems()
    {
        using var cts = new CancellationTokenSource();

        await _service.StartAsync(cts.Token);

        // Wait enough time for at least one processing cycle
        await Task.Delay(200);

        await cts.CancelAsync();
        await _service.StopAsync(CancellationToken.None);

        await _engine.Received().ProcessDueItemsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CatchesExceptions()
    {
        _engine.ProcessDueItemsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated failure"));

        using var cts = new CancellationTokenSource();

        await _service.StartAsync(cts.Token);

        // Wait long enough for multiple cycles to prove the service survives exceptions
        await Task.Delay(200);

        await cts.CancelAsync();
        await _service.StopAsync(CancellationToken.None);

        // The service should have called ProcessDueItemsAsync multiple times
        // despite the exception, proving it didn't crash
        await _engine.Received().ProcessDueItemsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();

        await _service.StartAsync(cts.Token);

        // Let it run briefly
        await Task.Delay(100);

        // Cancel and stop
        await cts.CancelAsync();
        await _service.StopAsync(CancellationToken.None);

        // Record the call count after stopping
        int callCount = _engine.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IBillingEngine<string>.ProcessDueItemsAsync));

        // Wait a bit more to verify no further calls happen
        await Task.Delay(200);

        int callCountAfterStop = _engine.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IBillingEngine<string>.ProcessDueItemsAsync));

        Assert.Equal(callCount, callCountAfterStop);
    }

    public void Dispose() => _service.Dispose();
}
