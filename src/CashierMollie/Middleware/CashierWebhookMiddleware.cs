using CashierMollie.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Middleware;

/// <summary>
/// ASP.NET Core middleware that handles incoming Mollie payment webhooks.
/// Register with <c>app.UseCashierWebhook()</c>.
/// </summary>
public partial class CashierWebhookMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _webhookPath;

    /// <summary>Creates a new instance of the webhook middleware.</summary>
    public CashierWebhookMiddleware(RequestDelegate next, IOptions<CashierMollieOptions> options)
    {
        _next = next;
        _webhookPath = options.Value.WebhookPath;
    }

    /// <summary>Processes the HTTP request, handling webhook POSTs at the configured path.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == "POST" &&
            context.Request.Path.Equals(_webhookPath, StringComparison.OrdinalIgnoreCase))
        {
            var logger = context.RequestServices.GetService<ILogger<CashierWebhookMiddleware>>();

            var form = await context.Request.ReadFormAsync();
            var paymentId = form["id"].FirstOrDefault();

            if (string.IsNullOrEmpty(paymentId) || !paymentId.StartsWith("tr_", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                return;
            }

            try
            {
                var webhookService = context.RequestServices.GetRequiredService<IWebhookService>();
                await webhookService.HandlePaymentAsync(paymentId, context.RequestAborted);
                context.Response.StatusCode = 200;
            }
            catch (CashierMollie.Exceptions.CashierException)
            {
                // Business logic error — don't retry
                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                if (logger != null)
                    LogWebhookError(logger, paymentId, ex);
                // Infrastructure error — let Mollie retry
                context.Response.StatusCode = 500;
            }

            return;
        }

        await _next(context);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing Mollie webhook for payment {PaymentId}")]
    private static partial void LogWebhookError(ILogger logger, string paymentId, Exception ex);
}
