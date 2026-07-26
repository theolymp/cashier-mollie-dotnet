using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Services;

/// <summary>
/// Emits one-off diagnostics at application startup so consumers can see which billing semantics
/// they are actually running, and are warned about a webhook URL Mollie cannot call back.
/// </summary>
/// <remarks>
/// Both checks exist because the failure modes are silent. The billing engine is chosen by a
/// default, so a consumer who never sets it runs semantics they never picked -- and the two engines
/// differ in how a failed payment is retried. The webhook URL falls back to the local
/// <see cref="CashierMollieOptions.WebhookPath"/>, which is relative; Mollie requires an absolute
/// URL, so that combination only fails once real payments start.
/// </remarks>
public sealed partial class CashierStartupDiagnostics : IHostedService
{
    private readonly CashierMollieOptions _options;
    private readonly ILogger<CashierStartupDiagnostics> _logger;

    /// <summary>Initializes a new instance of <see cref="CashierStartupDiagnostics"/>.</summary>
    /// <param name="options">CashierMollie configuration options.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public CashierStartupDiagnostics(
        IOptions<CashierMollieOptions> options,
        ILogger<CashierStartupDiagnostics> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs the startup diagnostics.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogBillingEngine(_options.BillingEngine.ToString());

        // Must be http(s), not merely "absolute": on Unix, Uri.TryCreate parses a leading-slash
        // path such as "/api/billing/webhook" as an absolute file:/// URI and returns true, so an
        // UriKind.Absolute check alone silently never fires on Linux -- exactly where this runs.
        var effective = _options.EffectiveWebhookUrl;
        var isCallableByMollie =
            Uri.TryCreate(effective, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (!isCallableByMollie)
        {
            // Two distinct causes with two distinct fixes: an unset WebhookUrl silently falls back
            // to the local (relative) WebhookPath, whereas a set-but-unusable value is a typo.
            if (string.IsNullOrEmpty(_options.WebhookUrl))
            {
                LogWebhookUrlMissing(effective);
            }
            else
            {
                LogWebhookUrlNotAbsolute(_options.WebhookUrl);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>No-op; diagnostics run at startup only.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CashierMollie billing engine: {BillingEngine}. This determines how failed payments are retried; set CashierMollie:BillingEngine explicitly to avoid depending on the default.")]
    private partial void LogBillingEngine(string billingEngine);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CashierMollie:WebhookUrl is not configured, so the relative path '{EffectiveWebhookUrl}' would be sent to Mollie instead. Mollie requires an absolute http(s) URL and cannot call back, so payment status updates will never arrive. Set CashierMollie:WebhookUrl to the full public URL; its path component must match CashierMollie:WebhookPath.")]
    private partial void LogWebhookUrlMissing(string effectiveWebhookUrl);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CashierMollie:WebhookUrl is set to '{WebhookUrl}', which is not an absolute http(s) URL. Mollie cannot call back, so payment status updates will never arrive. Use the full public URL, e.g. https://example.com/cashier/webhook.")]
    private partial void LogWebhookUrlNotAbsolute(string webhookUrl);
}
