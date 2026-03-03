# CashierMollie for .NET

Mollie subscription management for ASP.NET Core -- a .NET port of [laravel/cashier-mollie](https://github.com/laravel/cashier-mollie).

CashierMollie provides a fluent API for managing Mollie recurring payments, subscriptions, and mandate-based billing in ASP.NET Core applications using Entity Framework Core.

## Features

- **Subscription lifecycle** -- create, cancel, resume, and swap subscriptions
- **First payment flow** -- mandate-based recurring payments via Mollie
- **Trial periods** -- built-in support for trial days
- **Coupons** -- apply discount coupons to subscriptions
- **Grace periods** -- subscriptions remain active until the billing period ends
- **Webhook handling** -- automatic processing of Mollie payment notifications
- **Event system** -- react to payment and subscription lifecycle events
- **EF Core integration** -- Subscription, OrderItem, and Payment entities with migrations
- **Generic owner key** -- supports `string`, `int`, `Guid`, or any key type for your user model

## Installation

```bash
dotnet add package CashierMollie
```

## Quick Start

### 1. Register services in Program.cs

```csharp
builder.Services.AddCashierMollie<string>(builder.Configuration);

var app = builder.Build();

app.UseCashierWebhook(); // Auto-handles POST /cashier/webhook
```

### 2. Add configuration

Add a `CashierMollie` section to your `appsettings.json`:

```json
{
  "CashierMollie": {
    "ApiKey": "test_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "Locale": "de_DE",
    "Currency": "EUR",
    "FirstPaymentRedirectUrl": "/billing/success",
    "WebhookUrl": "https://yourdomain.com/cashier/webhook"
  }
}
```

### 3. Implement IBillable on your user model

```csharp
using CashierMollie.Interfaces;
using Microsoft.AspNetCore.Identity;

public class AppUser : IdentityUser, IBillable<string>
{
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
}
```

### 4. Apply EF Core migrations

```csharp
// In your DbContext
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyCashierMollieModel<string>();
}
```

Then generate and apply migrations:

```bash
dotnet ef migrations add AddCashierMollieTables
dotnet ef database update
```

## Usage

### Creating a subscription

```csharp
var result = await cashier.NewSubscription(user, "default", "pro-monthly")
    .WithCoupon("LAUNCH10")
    .TrialDays(14)
    .CreateAsync();
```

The first argument is the billable user, the second is a subscription name (used to distinguish multiple subscriptions per user), and the third is the plan identifier matching your Mollie configuration.

### Cancelling a subscription

```csharp
await cashier.CancelAsync(user, "default");
```

Cancelled subscriptions enter a grace period. The user retains access until the current billing period ends.

### Resuming a cancelled subscription

```csharp
await cashier.ResumeAsync(user, "default");
```

A subscription can be resumed during its grace period. Once the grace period has expired, a new subscription must be created.

### Swapping plans

```csharp
await cashier.SwapAsync(user, "default", "team-monthly");
```

### Checking subscription status

```csharp
bool isSubscribed = await cashier.IsSubscribedAsync(user, "default");
bool onTrial = await cashier.OnTrialAsync(user, "default");
bool onGracePeriod = await cashier.OnGracePeriodAsync(user, "default");
bool isCancelled = await cashier.IsCancelledAsync(user, "default");
```

## Webhook Setup

CashierMollie includes a built-in webhook endpoint that processes Mollie payment notifications automatically. Register it with a single call:

```csharp
app.UseCashierWebhook(); // POST /cashier/webhook
```

The webhook URL must be publicly accessible and match the `WebhookUrl` in your configuration. For local development, use a tunnel service such as ngrok.

## Events

CashierMollie dispatches events throughout the subscription and payment lifecycle. Implement `ICashierEventDispatcher` to handle them:

```csharp
public class MyCashierEventHandler : ICashierEventDispatcher
{
    public Task DispatchAsync<TEvent>(TEvent evt, CancellationToken ct = default)
    {
        return evt switch
        {
            OrderPaymentPaid e => HandlePaymentPaid(e),
            OrderPaymentFailed e => HandlePaymentFailed(e),
            SubscriptionCreated e => HandleCreated(e),
            SubscriptionCancelled e => HandleCancelled(e),
            SubscriptionResumed e => HandleResumed(e),
            SubscriptionPlanSwapped e => HandleSwapped(e),
            _ => Task.CompletedTask
        };
    }
}
```

Register your handler in DI:

```csharp
builder.Services.AddSingleton<ICashierEventDispatcher, MyCashierEventHandler>();
```

### Available events

| Event | Description |
|-------|-------------|
| `OrderPaymentPaid` | A payment was successfully processed |
| `OrderPaymentFailed` | A payment attempt failed |
| `SubscriptionCreated` | A new subscription was created |
| `SubscriptionCancelled` | A subscription was cancelled |
| `SubscriptionResumed` | A cancelled subscription was resumed |
| `SubscriptionPlanSwapped` | A subscription was switched to a different plan |

## Configuration Reference

| Key | Default | Description |
|-----|---------|-------------|
| `ApiKey` | -- | Mollie API key (`test_xxx` or `live_xxx`) |
| `Locale` | `de_DE` | Locale for Mollie checkout pages |
| `Currency` | `EUR` | Default currency (ISO 4217) |
| `FirstPaymentRedirectUrl` | `/billing/success` | Redirect URL after first payment |
| `WebhookUrl` | `/cashier/webhook` | Webhook URL for Mollie notifications |

## Requirements

- .NET 10 or later
- Entity Framework Core 10
- A Mollie account with recurring payments enabled

## Contributing

Contributions are welcome. Please open an issue first to discuss your idea, then fork the repository, create a feature branch, and submit a pull request. Follow [Conventional Commits](https://www.conventionalcommits.org/) for commit messages.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
