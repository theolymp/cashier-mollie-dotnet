<p align="center">
  <img src="https://www.mollie.com/assets/images/branding/mollie-logo.svg" alt="Mollie" width="200">
</p>

<h1 align="center">CashierMollie for .NET</h1>

<p align="center">
  <strong>Mollie subscription management for ASP.NET Core</strong><br>
  A .NET port of the excellent <a href="https://github.com/mollie/laravel-cashier-mollie">mollie/laravel-cashier-mollie</a> package.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/CashierMollie"><img src="https://img.shields.io/nuget/v/CashierMollie.svg" alt="NuGet"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-10.0-purple.svg" alt=".NET 10">
</p>

---

CashierMollie provides a fluent, expressive API for managing [Mollie](https://www.mollie.com) recurring payments and subscriptions in ASP.NET Core applications. It handles the complexities of subscription billing so you can focus on your product.

> **Acknowledgement:** This project is heavily inspired by and aims for feature parity with the outstanding [laravel-cashier-mollie](https://github.com/mollie/laravel-cashier-mollie) package, originally created by [Sander van Hooft](https://github.com/sandervanhooft) and maintained by the Laravel/Mollie community. Their thoughtful API design and thorough approach to subscription billing formed the blueprint for this .NET implementation. Thank you for paving the way!

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Getting Started](#getting-started)
  - [Implement IBillable](#1-implement-ibillable-on-your-user-model)
  - [Register Services](#2-register-services)
  - [Database Setup](#3-database-setup)
- [Subscriptions](#subscriptions)
  - [Creating Subscriptions](#creating-subscriptions)
  - [Checking Subscription Status](#checking-subscription-status)
  - [Cancelling Subscriptions](#cancelling-subscriptions)
  - [Resuming Subscriptions](#resuming-subscriptions)
  - [Swapping Plans](#swapping-plans)
  - [Trial Periods](#trial-periods)
  - [Grace Periods](#grace-periods)
  - [Multiple Subscriptions](#multiple-subscriptions)
  - [Quantity Management](#quantity-management)
- [Billing Engines](#billing-engines)
- [Coupons](#coupons)
- [Credits](#credits)
- [One-off Charges](#one-off-charges)
- [Refunds](#refunds)
- [Payment Method Update](#payment-method-update)
- [First Payment & Mandates](#first-payment--mandates)
- [Webhooks](#webhooks)
  - [Middleware (Recommended)](#webhook-middleware-recommended)
  - [Manual Handling](#manual-webhook-handling)
- [Events](#events)
  - [Available Events](#available-events)
  - [Handling Events](#handling-events)
- [Database Schema](#database-schema)
- [API Reference](#api-reference)
  - [ICashierService](#icashierservicetkey)
  - [ISubscriptionBuilder](#isubscriptionbuildertkey)
  - [Subscription Model](#subscriptiontkey-model)
- [Configuration Reference](#configuration-reference)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [Credits](#credits)
- [License](#license)

## Features

- **Subscription lifecycle** -- create, cancel, resume, and swap subscriptions with a fluent API
- **Dual billing engine** -- choose between Mollie's native Subscription API (`MollieNative`) or a self-managed billing cycle (`Managed`) with background processing
- **First payment flow** -- automatic mandate creation via Mollie checkout or direct mandate-based activation
- **Trial periods** -- built-in support for trial days
- **Grace periods** -- cancelled subscriptions remain active until the billing period ends
- **Quantity management** -- update, increment, and decrement subscription quantities (seat-based billing)
- **Coupon system** -- fixed and percentage-based discounts with pluggable `ICouponRepository` and `ICouponHandler`
- **Credit / balance system** -- add, apply, and check account credit balances per currency
- **One-off charges** -- fluent `IChargeBuilder` for single payments with or without mandate
- **Refund system** -- partial and complete refunds via Mollie with local tracking
- **Payment method update** -- initiate payment method changes via first payment flow
- **Mandate management** -- check mandate validity and revoke mandates
- **Chargeback detection** -- automatic tracking and events for chargebacks
- **Invoice interface** -- pluggable `IInvoiceGenerator<TKey>` for custom invoice generation (sevdesk, PDF, etc.)
- **Webhook handling** -- opt-in middleware for automatic processing of Mollie payment notifications
- **Event system** -- 23 domain events via pluggable `ICashierEventDispatcher`
- **EF Core integration** -- 7 entities across 7 tables with a single `ModelBuilder` extension
- **Generic owner key** -- supports `string`, `int`, `Guid`, or any `IEquatable<TKey>` type for your user model
- **Dependency injection** -- first-class ASP.NET Core DI support with a single `AddCashierMollie<TKey>()` call

## Requirements

- [.NET 10](https://dotnet.microsoft.com/download) or later
- [Entity Framework Core 10](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore)
- [Mollie.Api 4.18+](https://www.nuget.org/packages/Mollie.Api)
- A [Mollie account](https://www.mollie.com/signup) with recurring payments enabled

## Installation

```bash
dotnet add package CashierMollie
dotnet add package Mollie.Api
```

CashierMollie wraps Mollie.Api but does not bundle it -- you register the Mollie API clients yourself, giving you full control over Mollie configuration.

## Configuration

Add a `CashierMollie` section to your `appsettings.json`:

```json
{
  "CashierMollie": {
    "ApiKey": "test_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "Currency": "EUR",
    "Locale": "de_DE",
    "WebhookUrl": "https://yourdomain.com/cashier/webhook",
    "FirstPaymentRedirectUrl": "https://yourdomain.com/billing/return"
  }
}
```

> **Important:** For production, use `live_xxx` API keys. The `WebhookUrl` must be a publicly accessible HTTPS URL. For local development, use a tunnel service like [ngrok](https://ngrok.com) or [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/).

## Getting Started

### 1. Implement IBillable on your user model

```csharp
using CashierMollie.Interfaces;
using Microsoft.AspNetCore.Identity;

public class AppUser : IdentityUser, IBillable<string>
{
    // These are required by IBillable<TKey>:
    // string Id           -- inherited from IdentityUser
    // string? Email       -- inherited from IdentityUser
    // string? Name        -- you may need to add this

    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
    public string? Name { get; set; }
}
```

`IBillable<TKey>` works with any key type. For example, with `int` keys:

```csharp
public class AppUser : IBillable<int>
{
    public int Id { get; set; }
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
}
```

### 2. Register services

In your `Program.cs`:

```csharp
using CashierMollie.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register Mollie API clients (from Mollie.Api package)
builder.Services.AddMollieApi(options =>
{
    options.ApiKey = builder.Configuration["CashierMollie:ApiKey"];
});

// Register CashierMollie services
builder.Services.AddCashierMollie<string>(builder.Configuration);

var app = builder.Build();

// Enable automatic webhook handling (optional -- see Webhooks section)
app.UseCashierWebhook();

app.Run();
```

### 3. Database setup

CashierMollie uses Entity Framework Core for data persistence. Apply the schema to your existing `DbContext`:

```csharp
using CashierMollie.Data;

public class AppDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Add CashierMollie tables (cashier_subscriptions, cashier_order_items, cashier_payments)
        modelBuilder.ApplyCashierMollie<string>();
    }
}
```

Then generate and apply migrations:

```bash
dotnet ef migrations add AddCashierMollieTables
dotnet ef database update
```

> **Note:** You can also provide a separate `DbContext` for CashierMollie via the `dbContextOptions` parameter in `AddCashierMollie<TKey>()` if you prefer to keep billing tables in a separate database.

## Subscriptions

### Creating subscriptions

Inject `ICashierService<TKey>` and use the fluent subscription builder:

```csharp
public class BillingController : Controller
{
    private readonly ICashierService<string> _cashier;

    public BillingController(ICashierService<string> cashier)
    {
        _cashier = cashier;
    }

    public async Task<IActionResult> Subscribe(AppUser user)
    {
        var result = await _cashier.NewSubscription(user, "default", "pro-monthly")
            .CreateAsync();

        if (result.RequiresAction)
        {
            // User needs to complete the first payment via Mollie checkout
            return Redirect(result.CheckoutUrl!);
        }

        // Subscription is active immediately (user already has a mandate)
        return RedirectToAction("Dashboard");
    }
}
```

The three arguments to `NewSubscription` are:

| Parameter | Description |
|-----------|-------------|
| `owner` | Your user model implementing `IBillable<TKey>` |
| `name` | A label for this subscription (e.g. `"default"`, `"extra"`) -- used to distinguish multiple subscriptions per user |
| `plan` | The plan identifier matching your Mollie configuration |

### Checking subscription status

```csharp
// Is the user subscribed to the "default" subscription?
bool isSubscribed = await _cashier.IsSubscribedAsync(user, "default");

// Is the subscription on a trial period?
bool onTrial = await _cashier.OnTrialAsync(user, "default");

// Is the subscription cancelled but still within the grace period?
bool onGracePeriod = await _cashier.OnGracePeriodAsync(user, "default");
bool isCancelled = await _cashier.IsCancelledAsync(user, "default");

// Get the full subscription object for detailed inspection
var subscription = await _cashier.GetSubscriptionAsync(user, "default");

if (subscription != null)
{
    bool isActive    = subscription.IsActive();
    bool isCancelled = subscription.IsCancelled();
    bool isEnded     = subscription.IsEnded();
    bool onTrial     = subscription.OnTrial();
}

// Get all subscriptions for a user
var allSubs = await _cashier.GetSubscriptionsAsync(user);
```

### Cancelling subscriptions

```csharp
// Cancel at end of billing period (grace period)
await _cashier.CancelAsync(user, "default");
```

When cancelled, the subscription enters a **grace period**. The user retains access until the current billing period ends. During this time:

- `IsSubscribedAsync()` returns `true`
- `OnGracePeriodAsync()` returns `true`
- `subscription.IsCancelled()` returns `true`

To cancel immediately without a grace period:

```csharp
await _cashier.CancelImmediatelyAsync(user, "default");
```

### Resuming subscriptions

A subscription can be resumed during its grace period:

```csharp
await _cashier.ResumeAsync(user, "default");
```

After resuming, the subscription is active again and `OnGracePeriodAsync()` returns `false`. Once the grace period has expired (`IsEnded()` returns `true`), a new subscription must be created.

### Swapping plans

```csharp
// Swap to a different plan
var updated = await _cashier.SwapAsync(user, "default", "team-monthly");
```

### Trial periods

```csharp
var result = await _cashier.NewSubscription(user, "default", "pro-monthly")
    .TrialDays(14)
    .CreateAsync();

// Check trial status
bool onTrial = await _cashier.OnTrialAsync(user, "default");
```

During the trial period, the user is considered subscribed (`IsSubscribedAsync()` returns `true`).

### Grace periods

When a subscription is cancelled, it enters a grace period until the end of the current billing cycle. During this time the user still has access to the subscribed features.

```csharp
await _cashier.CancelAsync(user, "default");

// Still has access
bool subscribed = await _cashier.IsSubscribedAsync(user, "default");   // true
bool onGrace    = await _cashier.OnGracePeriodAsync(user, "default");  // true

// Can resume during grace period
await _cashier.ResumeAsync(user, "default");
```

### Multiple subscriptions

A single user can hold multiple subscriptions, each identified by a unique name:

```csharp
// Primary subscription
await _cashier.NewSubscription(user, "default", "pro-monthly").CreateAsync();

// Add-on subscription
await _cashier.NewSubscription(user, "addons", "extra-storage").CreateAsync();

// Check independently
bool hasPro    = await _cashier.IsSubscribedAsync(user, "default");
bool hasAddons = await _cashier.IsSubscribedAsync(user, "addons");

// Cancel only the add-on
await _cashier.CancelAsync(user, "addons");
```

### Quantity management

Update subscription quantities for seat-based billing:

```csharp
// Set quantity to 10
await _cashier.UpdateQuantityAsync(user, "default", 10);

// Add 3 seats
await _cashier.IncrementQuantityAsync(user, "default", 3);

// Remove 2 seats (minimum 1)
await _cashier.DecrementQuantityAsync(user, "default", 2);
```

## Billing Engines

CashierMollie supports two billing strategies via the `IBillingEngine<TKey>` strategy pattern:

**MollieNative (default)** -- delegates billing to Mollie's Subscription API. Mollie manages the billing cycle and sends webhooks for each payment.

**Managed** -- runs its own billing cycle locally. Schedules `OrderItem` records with `ProcessAt` dates and creates on-demand recurring payments via Mollie mandates. Requires the `CashierBackgroundService` (registered automatically).

```json
{
  "CashierMollie": {
    "BillingEngine": "Managed",
    "ProcessingInterval": "01:00:00"
  }
}
```

## Coupons

CashierMollie includes a coupon system with fixed and percentage-based discounts.

```csharp
// Validate and redeem a coupon
var couponService = serviceProvider.GetRequiredService<ICouponService<string>>();
await couponService.ValidateAsync("LAUNCH10");
await couponService.RedeemAsync("LAUNCH10", subscription, user.Id);
```

Configure coupons in `appsettings.json` (default `ConfigCouponRepository`) or register your own `ICouponRepository`:

```json
{
  "CashierMollie": {
    "Coupons": [
      { "Code": "LAUNCH10", "Type": "percentage", "Value": 10, "MaxRedemptions": 100 },
      { "Code": "5OFF", "Type": "fixed", "Value": 5.00 }
    ]
  }
}
```

## Credits

Manage per-user account credit balances:

```csharp
var creditService = serviceProvider.GetRequiredService<ICreditService<string>>();

// Add credit
await creditService.AddCreditAsync(user.Id, 25.00m, "EUR", "Referral bonus");

// Check balance
decimal balance = await creditService.GetBalanceAsync(user.Id, "EUR");

// Apply credit (returns amount actually applied, capped at balance)
decimal applied = await creditService.ApplyCreditAsync(user.Id, 10.00m, "EUR");
```

## One-off Charges

Create single payments using the fluent `IChargeBuilder`:

```csharp
var result = await _cashier.NewCharge(user, 49.99m)
    .WithDescription("Premium add-on")
    .CreateAsync();

if (result.RequiresAction)
    return Redirect(result.CheckoutUrl!);
```

## Refunds

Issue partial or complete refunds for payments:

```csharp
var refundService = serviceProvider.GetRequiredService<IRefundService<string>>();

// Partial refund
var refund = await refundService.RefundAsync("tr_payment123", 10.00m, "EUR", "Partial refund");

// Full refund
var fullRefund = await refundService.RefundCompletelyAsync("tr_payment123");
```

## Payment Method Update

Allow users to update their payment method:

```csharp
var result = await _cashier.UpdatePaymentMethodAsync(user);
// Redirect user to result.CheckoutUrl to complete the update
```

## First Payment & Mandates

Mollie uses [mandates](https://docs.mollie.com/docs/mandates) for recurring payments. A mandate authorizes you to charge the customer's payment method on a recurring basis.

CashierMollie handles this automatically:

**If the user already has a mandate** (`MollieMandateId` is set on the `IBillable`):
- The subscription is activated immediately
- No checkout redirect needed

**If the user has no mandate**:
- A "first payment" is created via the Mollie API
- The user is redirected to Mollie's checkout to authorize recurring payments
- After successful payment, Mollie sends a webhook to activate the subscription

```csharp
var result = await _cashier.NewSubscription(user, "default", "pro")
    .CreateAsync();

if (result.RequiresAction)
{
    // Redirect to Mollie checkout for mandate creation
    return Redirect(result.CheckoutUrl!);
}

// Already has mandate -- subscription is active
```

You can force mandate-only mode (authorization without charging):

```csharp
var result = await _cashier.NewSubscription(user, "default", "pro")
    .WithMandateOnly()
    .CreateAsync();
```

## Webhooks

Mollie uses webhooks to notify your application about payment status changes. CashierMollie provides two approaches.

### Webhook middleware (recommended)

The simplest approach -- register the middleware and CashierMollie handles everything:

```csharp
app.UseCashierWebhook(); // Handles POST requests to /cashier/webhook
```

The middleware:
1. Intercepts POST requests to the configured `WebhookUrl` path
2. Reads the `id` form field (Mollie payment ID)
3. Fetches the payment status from Mollie's API
4. Updates the local payment record
5. Dispatches events
6. Returns HTTP 200 on success or business logic errors; HTTP 500 on infrastructure errors (so Mollie retries)

### Manual webhook handling

If you need more control, inject `IWebhookService` directly:

```csharp
[ApiController]
[Route("billing")]
public class BillingWebhookController : ControllerBase
{
    private readonly IWebhookService _webhook;

    public BillingWebhookController(IWebhookService webhook)
    {
        _webhook = webhook;
    }

    [HttpPost("mollie-webhook")]
    public async Task<IActionResult> HandleMollieWebhook([FromForm] string id)
    {
        await _webhook.HandlePaymentAsync(id);
        return Ok();
    }
}
```

> **Security note:** Mollie does not use webhook signatures. Instead, the webhook handler always fetches the payment details directly from the Mollie API, ensuring data integrity. Consider placing the webhook endpoint behind rate-limiting middleware in production.

## Events

CashierMollie dispatches domain events throughout the subscription and payment lifecycle via `ICashierEventDispatcher`.

### Available events

| Event | Description |
|-------|-------------|
| `SubscriptionCreated<TKey>` | A new subscription was activated |
| `SubscriptionCancelled<TKey>` | A subscription was cancelled |
| `SubscriptionResumed<TKey>` | A cancelled subscription was resumed |
| `SubscriptionPlanSwapped<TKey>` | A subscription plan was changed |
| `SubscriptionQuantityUpdated<TKey>` | Subscription quantity was updated |
| `OrderPaymentPaid<TKey>` | A payment was successfully processed |
| `OrderPaymentFailed<TKey>` | A payment attempt failed |
| `OrderPaymentFailedDueToInvalidMandate<TKey>` | Payment failed due to invalid mandate |
| `FirstPaymentPaid<TKey>` | First payment (mandate creation) succeeded |
| `FirstPaymentFailed<TKey>` | First payment (mandate creation) failed |
| `MandateUpdated<TKey>` | Owner's mandate was updated |
| `MandateCleared<TKey>` | Owner's mandate was removed |
| `CouponApplied<TKey>` | A coupon was redeemed on a subscription |
| `CreditAdded<TKey>` | Credit was added to owner's balance |
| `CreditApplied<TKey>` | Credit was consumed from owner's balance |
| `RefundInitiated<TKey>` | A refund was initiated |
| `RefundProcessed<TKey>` | A refund was processed successfully |
| `RefundFailed<TKey>` | A refund attempt failed |
| `ChargebackReceived<TKey>` | A chargeback was received |
| `OrderCreated<TKey>` | A new order was created |
| `OrderProcessed<TKey>` | An order was fully processed |
| `OrderInvoiceAvailable<TKey>` | An invoice is available for an order |
| `BalanceTurnedStale<TKey>` | Owner's credit balance became stale |

### Handling events

Implement `ICashierEventDispatcher` and register it in DI:

```csharp
using CashierMollie.Events;
using CashierMollie.Interfaces;

public class CashierEventHandler : ICashierEventDispatcher
{
    private readonly ILogger<CashierEventHandler> _logger;

    public CashierEventHandler(ILogger<CashierEventHandler> logger)
    {
        _logger = logger;
    }

    public Task DispatchAsync<T>(T @event, CancellationToken ct = default) where T : notnull
    {
        return @event switch
        {
            SubscriptionCreated<string> e =>
                OnSubscriptionCreated(e, ct),

            SubscriptionCancelled<string> e =>
                OnSubscriptionCancelled(e, ct),

            OrderPaymentPaid<string> e =>
                OnPaymentReceived(e, ct),

            OrderPaymentFailed<string> e =>
                OnPaymentFailed(e, ct),

            _ => Task.CompletedTask
        };
    }

    private async Task OnSubscriptionCreated(SubscriptionCreated<string> e, CancellationToken ct)
    {
        _logger.LogInformation("Subscription '{Name}' created for user {UserId}",
            e.Subscription.Name, e.OwnerId);

        // Send welcome email, provision resources, etc.
    }

    private async Task OnSubscriptionCancelled(SubscriptionCancelled<string> e, CancellationToken ct)
    {
        _logger.LogInformation("Subscription '{Name}' cancelled for user {UserId}",
            e.Subscription.Name, e.OwnerId);
    }

    private async Task OnPaymentReceived(OrderPaymentPaid<string> e, CancellationToken ct)
    {
        _logger.LogInformation("Payment {PaymentId} received: {Amount} {Currency}",
            e.Payment.MolliePaymentId, e.Payment.Amount, e.Payment.Currency);

        // Create invoice, send receipt, etc.
    }

    private async Task OnPaymentFailed(OrderPaymentFailed<string> e, CancellationToken ct)
    {
        _logger.LogWarning("Payment {PaymentId} failed for user {UserId}",
            e.Payment.MolliePaymentId, e.OwnerId);

        // Notify user, retry logic, etc.
    }
}
```

Register your handler in DI (before `AddCashierMollie` or using `Replace`):

```csharp
// Option A: Register before AddCashierMollie
builder.Services.AddScoped<ICashierEventDispatcher, CashierEventHandler>();
builder.Services.AddCashierMollie<string>(builder.Configuration);

// Option B: Or after -- TryAddSingleton won't overwrite your registration
builder.Services.AddCashierMollie<string>(builder.Configuration);
builder.Services.AddScoped<ICashierEventDispatcher, CashierEventHandler>();
```

If you don't register a handler, CashierMollie uses a built-in `NullCashierEventDispatcher` that silently discards all events.

## Database Schema

CashierMollie creates seven tables via the `ApplyCashierMollie<TKey>()` model builder extension:

### `cashier_subscriptions`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `Name` | `varchar(255)` | Subscription name (e.g. `"default"`) |
| `Plan` | `varchar(255)` | Plan identifier |
| `MollieSubscriptionId` | `varchar(255)?` | Mollie subscription ID |
| `MollieCustomerId` | `varchar(255)?` | Mollie customer ID |
| `Status` | `varchar(50)` | `active`, `cancelled`, `past_due`, `pending`, `paused` |
| `Quantity` | `int?` | Subscription quantity |
| `TrialEndsAt` | `datetimeoffset?` | End of trial period |
| `EndsAt` | `datetimeoffset?` | End of subscription / grace period |
| `CycleStartedAt` | `datetimeoffset?` | Start of current billing cycle |
| `CreatedAt` | `datetimeoffset` | Record creation timestamp |
| `UpdatedAt` | `datetimeoffset` | Last update timestamp |

### `cashier_payments`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `SubscriptionId` | `bigint?` | Optional FK to subscription |
| `MolliePaymentId` | `varchar(255)` UNIQUE | Mollie payment ID |
| `Status` | `varchar(50)` | Mollie payment status |
| `Currency` | `varchar(3)` | ISO 4217 currency code |
| `Amount` | `decimal` | Payment amount |
| `MollieMandateId` | `varchar(255)?` | Associated mandate ID |
| `Method` | `varchar(50)?` | Payment method used |
| `PaidAt` | `datetimeoffset?` | When payment was confirmed |
| `FailedAt` | `datetimeoffset?` | When payment failed |
| `CreatedAt` | `datetimeoffset` | Record creation timestamp |
| `UpdatedAt` | `datetimeoffset` | Last update timestamp |

### `cashier_order_items`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `SubscriptionId` | `bigint` | FK to subscription |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `Description` | `varchar(255)` | Line item description |
| `Currency` | `varchar(3)` | ISO 4217 currency code |
| `UnitPrice` | `decimal` | Price per unit |
| `Quantity` | `int` | Number of units |
| `TaxPercentage` | `decimal` | Tax rate (e.g. `19.0`) |
| `MolliePaymentId` | `varchar(255)?` | Associated Mollie payment |
| `MolliePaymentStatus` | `varchar(50)?` | Payment status |
| `ProcessedAt` | `datetimeoffset?` | When item was processed |
| `CreatedAt` | `datetimeoffset` | Record creation timestamp |
| `UpdatedAt` | `datetimeoffset` | Last update timestamp |

### `cashier_orders`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `Number` | `varchar(255)` | Order number |
| `MolliePaymentId` | `varchar(255)?` | Mollie payment ID |
| `Status` | `varchar(50)` | Order status |
| `Currency` | `varchar(3)` | ISO 4217 currency code |
| `Total` | `decimal` | Order total |

### `cashier_credits`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `Currency` | `varchar(3)` | ISO 4217 currency code |
| `Balance` | `decimal` | Current credit balance |

### `cashier_refunds`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `PaymentId` | `bigint` | FK to payment |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `MollieRefundId` | `varchar(255)` UNIQUE | Mollie refund ID |
| `Amount` | `decimal` | Refund amount |
| `Currency` | `varchar(3)` | ISO 4217 currency code |
| `Status` | `varchar(50)` | Refund status |

### `cashier_redeemed_coupons`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | `bigint` PK | Auto-increment ID |
| `OwnerId` | `TKey` | Foreign key to your user model |
| `Code` | `varchar(255)` | Coupon code |
| `SubscriptionName` | `varchar(255)` | Associated subscription |
| `TimesLeft` | `int?` | Remaining redemptions |

## API Reference

### `ICashierService<TKey>`

The main entry point for all subscription operations.

```csharp
public interface ICashierService<TKey> where TKey : IEquatable<TKey>
{
    // Subscription lifecycle
    ISubscriptionBuilder<TKey> NewSubscription(IBillable<TKey> owner, string name, string plan);
    Task CancelAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task CancelImmediatelyAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task ResumeAsync(IBillable<TKey> owner, string name, CancellationToken ct = default);
    Task<Subscription<TKey>> SwapAsync(IBillable<TKey> owner, string name, string newPlan, ...);

    // Status checks
    Task<bool> IsSubscribedAsync(IBillable<TKey> owner, string name, ...);
    Task<bool> OnGracePeriodAsync(IBillable<TKey> owner, string name, ...);
    Task<bool> OnTrialAsync(IBillable<TKey> owner, string name, ...);
    Task<bool> IsCancelledAsync(IBillable<TKey> owner, string name, ...);

    // Retrieve subscriptions
    Task<Subscription<TKey>?> GetSubscriptionAsync(IBillable<TKey> owner, string name, ...);
    Task<List<Subscription<TKey>>> GetSubscriptionsAsync(IBillable<TKey> owner, ...);

    // Quantity management
    Task<Subscription<TKey>> UpdateQuantityAsync(IBillable<TKey> owner, string name, int quantity, ...);
    Task<Subscription<TKey>> IncrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, ...);
    Task<Subscription<TKey>> DecrementQuantityAsync(IBillable<TKey> owner, string name, int count = 1, ...);

    // Customer management
    Task<string> GetOrCreateMollieCustomerAsync(IBillable<TKey> owner, ...);
    Task UpdateMollieCustomerAsync(IBillable<TKey> owner, ...);

    // One-off charges
    IChargeBuilder<TKey> NewCharge(IBillable<TKey> owner, decimal amount);

    // Payment method & mandate
    Task<PaymentMethodUpdateResult> UpdatePaymentMethodAsync(IBillable<TKey> owner, ...);
    Task<bool> HasValidMandateAsync(IBillable<TKey> owner, ...);
    Task RevokeMandateAsync(IBillable<TKey> owner, ...);
}
```

### `ISubscriptionBuilder<TKey>`

Fluent builder for creating subscriptions with optional configuration.

```csharp
public interface ISubscriptionBuilder<TKey> where TKey : IEquatable<TKey>
{
    ISubscriptionBuilder<TKey> WithCoupon(string coupon);
    ISubscriptionBuilder<TKey> TrialDays(int days);
    ISubscriptionBuilder<TKey> WithProration();
    ISubscriptionBuilder<TKey> WithMandateOnly();
    ISubscriptionBuilder<TKey> WithMetadata(Dictionary<string, string> metadata);
    Task<SubscriptionResult<TKey>> CreateAsync(CancellationToken ct = default);
}
```

### `Subscription<TKey>` model

The subscription entity with computed status methods:

```csharp
subscription.IsActive();      // Active and not ended
subscription.IsCancelled();   // Has an end date set
subscription.OnGracePeriod(); // Cancelled but end date is in the future
subscription.OnTrial();       // Trial period is active
subscription.IsEnded();       // End date has passed
```

## Configuration Reference

All settings are bound from the `CashierMollie` section in your configuration:

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `ApiKey` | `string` | `""` | Your Mollie API key (`test_xxx` for testing, `live_xxx` for production) |
| `Currency` | `string` | `"EUR"` | Default currency for payments ([ISO 4217](https://en.wikipedia.org/wiki/ISO_4217)) |
| `Locale` | `string` | `"de_DE"` | Locale for Mollie checkout pages (e.g. `"en_US"`, `"nl_NL"`, `"de_DE"`) |
| `WebhookUrl` | `string` | `"/cashier/webhook"` | URL path where Mollie sends payment status updates |
| `FirstPaymentRedirectUrl` | `string` | `"/billing/success"` | URL to redirect after the first payment / mandate authorization |
| `GracePeriodDays` | `int` | `30` | Number of days a cancelled subscription remains accessible |
| `BillingEngine` | `string` | `"MollieNative"` | Billing strategy: `"MollieNative"` or `"Managed"` |
| `ProcessingInterval` | `TimeSpan` | `"01:00:00"` | Interval between Managed engine processing runs |
| `OrderNumberFormat` | `string` | `"ORD-{0:D6}"` | Format string for generated order numbers |
| `PaymentMethodUpdateAmount` | `decimal` | `0.01` | Amount for payment method update verification |
| `PaymentMethodUpdateRedirectUrl` | `string` | `"/billing/payment-method-updated"` | Redirect URL after payment method update |

## Roadmap

CashierMollie v0.2.0 covers the full subscription lifecycle, dual billing engines, coupons, credits, refunds, charges, and more. The following features are planned for future releases:

- [x] **Subscription lifecycle** -- create, cancel, resume, swap with grace periods and trials
- [x] **Webhook subscription activation** -- activate pending subscriptions after successful first payment
- [x] **Dual billing engine** -- MollieNative and Managed with background processing
- [x] **Coupon / discount system** -- fixed and percentage-based discounts with configurable handlers
- [x] **Quantity management** -- increment, decrement, and update subscription quantities
- [x] **Credit / balance system** -- per-user account credit management
- [x] **One-off charges** -- single payments with fluent builder
- [x] **Refund system** -- partial and complete refunds via Mollie
- [x] **Payment method update** -- flow for customers to change their payment method
- [x] **Mandate management** -- check validity and revoke mandates
- [x] **Chargeback handling** -- tracking and events for chargebacks
- [x] **Invoice interface** -- pluggable IInvoiceGenerator for custom invoice generation
- [x] **Order model** -- full order pipeline with order items
- [ ] **Proration** -- credit calculation for mid-cycle plan changes
- [ ] **Invoice generation** -- built-in HTML/PDF invoice renderer
- [ ] **Plan pricing resolution** -- automatic price lookup for managed billing engine
- [ ] **Dunning / retry logic** -- automatic payment retry with escalation

## Contributing

Contributions are welcome! Here's how to get started:

1. **Open an issue** first to discuss your idea or bug report
2. **Fork** the repository and create a feature branch
3. **Write tests** for any new functionality
4. **Follow** [Conventional Commits](https://www.conventionalcommits.org/) for commit messages
5. **Submit** a pull request

```bash
# Clone and build
git clone https://github.com/TheOlymp/cashier-mollie-dotnet.git
cd cashier-mollie-dotnet
dotnet build

# Run tests
dotnet test
```

## Credits

- **[Sander van Hooft](https://github.com/sandervanhooft)** and the Laravel/Mollie community -- for creating and maintaining [laravel-cashier-mollie](https://github.com/mollie/laravel-cashier-mollie), the inspiration and reference implementation for this project
- **[Mollie](https://www.mollie.com)** -- for their excellent payment platform and API
- **[TheOlymp](https://github.com/TheOlymp)** -- creator and maintainer of this .NET port

## License

CashierMollie for .NET is open-sourced software licensed under the [MIT License](LICENSE).
