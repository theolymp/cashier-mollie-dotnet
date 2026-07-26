using CashierMollie.Middleware;
using Microsoft.AspNetCore.Builder;

namespace CashierMollie.Extensions;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the CashierMollie webhook middleware that automatically handles
    /// Mollie payment notifications at the configured webhook URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Security invariant -- Mollie webhooks are not signed.</b> There is no HMAC and no shared
    /// secret, and the endpoint must be publicly reachable, so any caller can post arbitrary data
    /// to it. This middleware is safe because it treats the request body as untrusted: it reads
    /// only the <c>id</c> form field and uses it purely as a lookup key, then re-fetches the
    /// authoritative payment state from the Mollie API before changing anything locally.
    /// </para>
    /// <para>
    /// Do not "optimise" this by trusting values from the payload (status, amount, refunds). Doing
    /// so turns a public, unauthenticated endpoint into a way to mutate payment state from the
    /// outside. The same rule applies to any custom handler built on
    /// <see cref="Interfaces.IWebhookService"/> -- there, the caller owns this invariant.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseCashierWebhook(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CashierWebhookMiddleware>();
    }
}
