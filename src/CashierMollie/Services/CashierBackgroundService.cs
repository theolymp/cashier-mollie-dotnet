using CashierMollie.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Background service that periodically processes due billing items for the Managed billing engine.
/// Registered automatically when <see cref="BillingEngineType.Managed"/> is configured.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public partial class CashierBackgroundService<TKey> : BackgroundService where TKey : IEquatable<TKey>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<CashierBackgroundService<TKey>> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CashierBackgroundService{TKey}"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating DI scopes to resolve scoped services.</param>
    /// <param name="options">CashierMollie configuration options containing the processing interval.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public CashierBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<CashierMollieOptions> options,
        ILogger<CashierBackgroundService<TKey>> logger)
    {
        _scopeFactory = scopeFactory;
        _interval = options.Value.ProcessingInterval;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IBillingEngine<TKey>>();
                await engine.ProcessDueItemsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogProcessingError(_logger, ex);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing due billing items")]
    private static partial void LogProcessingError(ILogger logger, Exception ex);
}
