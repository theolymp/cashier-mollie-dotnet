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

    /// <summary>Webhook URL path where Mollie sends payment status notifications.</summary>
    public string WebhookUrl { get; set; } = "/cashier/webhook";

    /// <summary>Number of days for the grace period after cancellation. Default is 30.</summary>
    public int GracePeriodDays { get; set; } = 30;
}
