using CashierMollie;
using CashierMollie.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Tests.Services;

public class CashierStartupDiagnosticsTests
{
    /// <summary>Minimal recording logger -- the source-generated LoggerMessage methods call ILogger.Log.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (CashierStartupDiagnostics Sut, RecordingLogger<CashierStartupDiagnostics> Logger)
        Create(CashierMollieOptions options)
    {
        var logger = new RecordingLogger<CashierStartupDiagnostics>();
        return (new CashierStartupDiagnostics(Options.Create(options), logger), logger);
    }

    [Fact]
    public async Task Warns_When_WebhookUrl_Empty_So_EffectiveUrl_Is_Relative()
    {
        // This is the real-world misconfiguration: WebhookPath set, WebhookUrl left empty.
        // EffectiveWebhookUrl then falls back to the relative path, which Mollie cannot call.
        var (sut, logger) = Create(new CashierMollieOptions
        {
            WebhookPath = "/api/billing/mollie-webhook",
            WebhookUrl = string.Empty,
        });

        await sut.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("/api/billing/mollie-webhook", warning.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", warning.Message, StringComparison.OrdinalIgnoreCase);
        // "not configured" is the actionable half -- distinguishes an unset value from a bad one.
        Assert.Contains("not configured", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Does_Not_Warn_When_WebhookUrl_Is_Absolute()
    {
        // Known-negative: the detector must stay silent on a correct configuration.
        var (sut, logger) = Create(new CashierMollieOptions
        {
            WebhookPath = "/api/billing/mollie-webhook",
            WebhookUrl = "https://portal.example.com/api/billing/mollie-webhook",
        });

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Does_Not_Warn_For_The_Default_Options()
    {
        // Guards against the warning firing for everyone out of the box.
        var (sut, logger) = Create(new CashierMollieOptions
        {
            WebhookUrl = "https://example.com/cashier/webhook",
        });

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(BillingEngineType.MollieNative)]
    [InlineData(BillingEngineType.Managed)]
    public async Task Logs_The_Effective_Billing_Engine(BillingEngineType engine)
    {
        var (sut, logger) = Create(new CashierMollieOptions
        {
            BillingEngine = engine,
            WebhookUrl = "https://example.com/cashier/webhook",
        });

        await sut.StartAsync(CancellationToken.None);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains(engine.ToString(), info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_For_A_FileScheme_Url()
    {
        // Regression guard: on Unix, Uri.TryCreate(..., UriKind.Absolute) accepts a leading-slash
        // path as a file:/// URI. A check for "absolute" alone therefore never fired on Linux.
        // Mollie can only call http(s), so anything else must still warn.
        var (sut, logger) = Create(new CashierMollieOptions
        {
            WebhookUrl = "file:///api/billing/mollie-webhook",
        });

        await sut.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        // A set-but-unusable value must not be reported as "not configured" -- different fix.
        Assert.DoesNotContain("not configured", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file:///api/billing/mollie-webhook", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_Not_Warn_For_Plain_Http()
    {
        // http is callable (a tunnel in local development); only non-http(s) schemes are a problem.
        var (sut, logger) = Create(new CashierMollieOptions
        {
            WebhookUrl = "http://localhost:5000/cashier/webhook",
        });

        await sut.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StopAsync_Is_A_NoOp()
    {
        var (sut, logger) = Create(new CashierMollieOptions());

        await sut.StopAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }
}
