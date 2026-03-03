using CashierMollie.Middleware;
using Microsoft.AspNetCore.Builder;

namespace CashierMollie.Extensions;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the CashierMollie webhook middleware that automatically handles
    /// Mollie payment notifications at the configured webhook URL.
    /// </summary>
    public static IApplicationBuilder UseCashierWebhook(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CashierWebhookMiddleware>();
    }
}
