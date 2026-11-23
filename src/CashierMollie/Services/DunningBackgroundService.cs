using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Runs <see cref="DunningService{TKey}.ProcessAsync"/> every <see cref="DunningOptions.CheckInterval"/>.
/// Registered with the MollieNative engine; does nothing unless <see cref="DunningOptions.Enabled"/> is true.
/// </summary>
/// <typeparam name="TKey">The type of the owner's primary key.</typeparam>
public partial class DunningBackgroundService<TKey> : BackgroundService where TKey : IEquatable<TKey>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DunningOptions _options;
    private readonly ILogger<DunningBackgroundService<TKey>> _logger;

    /// <summary>Initializes a new instance of <see cref="DunningBackgroundService{TKey}"/>.</summary>
    public DunningBackgroundService(IServiceScopeFactory scopeFactory, IOptions<CashierMollieOptions> options,
        ILogger<DunningBackgroundService<TKey>> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value.Dunning;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DunningService<TKey>>().ProcessAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPassFailed(_logger, ex);
            }

            await Task.Delay(_options.CheckInterval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dunning pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception ex);
}
