namespace CashierMollie;

/// <summary>
/// Configuration options for CashierMollie.
/// Bound from the "CashierMollie" section in appsettings.json.
/// </summary>
public class CashierMollieOptions
{
    /// <summary>The configuration section name in appsettings.json.</summary>
    public const string SectionName = "CashierMollie";

    /// <summary>Mollie API key (live_xxx or test_xxx).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Locale for Mollie checkout pages (e.g. "de_DE", "en_US", "nl_NL").</summary>
    public string Locale { get; set; } = "de_DE";

    /// <summary>Default currency in ISO 4217 format (e.g. "EUR", "USD", "GBP").</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>URL to redirect the user to after completing the first payment (mandate creation).</summary>
    public string FirstPaymentRedirectUrl { get; set; } = "/billing/success";

    /// <summary>Local path for the webhook middleware to match (e.g. "/cashier/webhook").</summary>
    public string WebhookPath { get; set; } = "/cashier/webhook";

    /// <summary>Full URL sent to Mollie for webhook callbacks (e.g. "https://example.com/cashier/webhook"). If empty, WebhookPath is used as fallback.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Resolves the webhook URL to send to Mollie. Returns <see cref="WebhookUrl"/> if set, otherwise falls back to <see cref="WebhookPath"/>.</summary>
    public string EffectiveWebhookUrl => string.IsNullOrEmpty(WebhookUrl) ? WebhookPath : WebhookUrl;

    /// <summary>Number of days for the grace period after cancellation. Default is 30.</summary>
    public int GracePeriodDays { get; set; } = 30;

    /// <summary>The billing engine to use. MollieNative uses Mollie's built-in recurring; Managed handles billing logic locally.</summary>
    public BillingEngineType BillingEngine { get; set; } = BillingEngineType.MollieNative;

    /// <summary>Interval between managed billing engine processing runs. Only used with <see cref="BillingEngineType.Managed"/>.</summary>
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Format string for order numbers. {0} is the auto-increment order ID.</summary>
    public string OrderNumberFormat { get; set; } = "ORD-{0:D6}";

    /// <summary>Amount charged for payment method update verification (zero-value mandate check). Default: 0.01 EUR.</summary>
    public decimal PaymentMethodUpdateAmount { get; set; } = 0.01m;

    /// <summary>URL to redirect the user to after updating their payment method.</summary>
    public string PaymentMethodUpdateRedirectUrl { get; set; } = "/billing/payment-method-updated";
}

/// <summary>
/// Determines which billing engine is used for subscription management.
/// </summary>
public enum BillingEngineType
{
    /// <summary>Use Mollie's built-in recurring payments API. Mollie manages the billing cycle.</summary>
    MollieNative,

    /// <summary>Manage billing cycles locally. The library creates on-demand payments via Mollie.</summary>
    Managed
}
