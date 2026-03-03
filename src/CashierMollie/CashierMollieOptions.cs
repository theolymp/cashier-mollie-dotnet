namespace CashierMollie;

public class CashierMollieOptions
{
    public const string SectionName = "CashierMollie";

    /// <summary>Mollie API Key (live_xxx or test_xxx)</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Locale for Mollie checkout pages (e.g. "de_DE")</summary>
    public string Locale { get; set; } = "de_DE";

    /// <summary>Default currency (ISO 4217)</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>URL to redirect after first payment (mandate creation)</summary>
    public string FirstPaymentRedirectUrl { get; set; } = "/billing/success";

    /// <summary>Webhook URL for Mollie payment notifications</summary>
    public string WebhookUrl { get; set; } = "/cashier/webhook";
}
