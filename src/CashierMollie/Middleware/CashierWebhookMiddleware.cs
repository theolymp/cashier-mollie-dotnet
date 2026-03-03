using CashierMollie.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashierMollie.Middleware;

public class CashierWebhookMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _webhookPath;

    public CashierWebhookMiddleware(RequestDelegate next, IOptions<CashierMollieOptions> options)
    {
        _next = next;
        _webhookPath = options.Value.WebhookUrl;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == "POST" &&
            context.Request.Path.Equals(_webhookPath, StringComparison.OrdinalIgnoreCase))
        {
            var logger = context.RequestServices.GetService<ILogger<CashierWebhookMiddleware>>();

            var form = await context.Request.ReadFormAsync();
            var paymentId = form["id"].FirstOrDefault();

            if (string.IsNullOrEmpty(paymentId))
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
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error processing Mollie webhook for payment {PaymentId}", paymentId);
                context.Response.StatusCode = 200; // Always return 200 to Mollie to prevent retries
            }

            return;
        }

        await _next(context);
    }
}
