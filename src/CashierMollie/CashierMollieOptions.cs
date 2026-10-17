namespace CashierMollie;

/// <summary>
/// Configuration options for CashierMollie.
/// Bound from the "CashierMollie" section in appsettings.json.
/// </summary>
public class CashierMollieOptions
{
    /// <summary>The configuration section name in appsettings.json.</summary>
    public const string SectionName = "CashierMollie";

    /// <summary>
    /// NOT READ BY THIS LIBRARY — the Mollie client is authenticated by your own AddMollieApi()
    /// call, not from here. A declared configuration slot rather than a dead one: consumers bind
    /// this section and read the key themselves, and at least one uses it as the switch deciding
    /// whether billing is wired at all, fed from a secret store in deployed environments. Do not
    /// remove it from your configuration on the strength of this line — check your own wiring
    /// first; rotating it may be the only rotation there is.
    /// Intended meaning: the Mollie API key (live_xxx or test_xxx).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// NOT READ BY THIS LIBRARY — no request it sends carries a locale, so a value here changes
    /// nothing in its behaviour. A consumer binding this section could still read it itself.
    /// Intended meaning: locale for Mollie checkout pages (e.g. "de_DE", "en_US", "nl_NL").
    /// </summary>
    // Maintainers: if this is ever wired through to Mollie, change this default to string.Empty
    // in the SAME commit and send nothing when it is empty. Today the field is never sent, so
    // Mollie picks the locale itself; wiring it while this default stands would force German
    // checkout pages on every consumer who never configured anything. The default is the fallback
    // for the uninformed caller, not the desired state -- wiring without that change IS the
    // regression. Not in the XML summary above on purpose: this is addressed to whoever edits the
    // property, not to the consumers who read the shipped documentation.
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

    /// <summary>
    /// NOT READ BY THIS LIBRARY — it builds no order number at all, so a value here changes
    /// nothing in its behaviour. A consumer binding this section could still read it itself.
    /// Intended meaning: format string for order numbers, {0} being the auto-increment order ID.
    /// </summary>
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
