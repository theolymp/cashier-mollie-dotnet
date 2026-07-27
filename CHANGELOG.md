# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-07-27

Driven by the first real integration of this library into an application. Every item below is
something the published documentation got wrong or failed to convey, found because the integration
verified the docs against the source instead of taking them on trust.

### Added
- `CashierStartupDiagnostics` -- logs the effective billing engine at startup, and warns when the
  webhook URL handed to Mollie is not an absolute `http(s)` URL. Both failure modes were previously
  silent until real payments ran. Distinguishes an *unset* `WebhookUrl` (falls back to the relative
  `WebhookPath`) from a *set but unusable* one, because the fix differs.
- Tests covering the new diagnostics, including a regression test for the `file://` case below.
- `global.json` pinning the SDK, with `rollForward: latestPatch`. The CI workflow now installs from
  it (`global-json-file`) instead of carrying its own version, so the SDK is stated once.

### Fixed
- **Build, `main` was red:** `CashierStartupDiagnostics` passed `BillingEngine.ToString()` to a
  `LoggerMessage` method. Arguments are evaluated at the call site even when the level is disabled,
  which CA1873 flags; with repo-wide `TreatWarningsAsErrors` that is a build error. The enum is now
  passed directly and formatted lazily inside the generated method.
- **Build reproducibility:** the workflow requested SDK `10.0.x` and the repo had no `global.json`,
  so the runner resolved whatever was newest. Combined with `AnalysisLevel: latest-recommended` --
  itself "whatever the newest SDK recommends" -- a newly shipped analyzer rule could turn `main` red
  with no code change, which is exactly how CA1873 arrived. Two floating references, one pin fixes
  both: the analyzer set is now a property of the pinned SDK rather than of the calendar.
- **Docs, security-relevant:** the webhook trust invariant (Mollie does not sign webhooks; safety
  depends on never trusting the request body) existed only as a one-line footnote at the end of the
  Webhooks section. Our first consumer read the README and still had to derive the property from
  the source. Promoted to a dedicated Security section stating the invariant and the concrete
  failure mode, mirrored as XML docs on `UseCashierWebhook` and `IWebhookService.HandlePaymentAsync`.
- **Docs:** README claimed the middleware matches `WebhookUrl`; it matches `WebhookPath`. The config
  example omitted `WebhookPath` entirely, and the configuration reference listed `WebhookUrl` with
  the wrong default (`"/cashier/webhook"` instead of empty) while omitting `WebhookPath`. Following
  the README could leave Mollie calling a path the application does not serve.
- **Docs:** the quickstart called `AddMollieApi` without showing its `using`. It lives in
  `Mollie.Api`, not `Mollie.Api.Extensions` -- our first consumer had to decompile to find it.
- **Docs:** retry ownership was undocumented and inferred incorrectly as "the library handles it".
  It does not: there is no dunning logic at all. Now documented per engine.
- **Docs:** `Subscription.Name` (the subscription *slot*) vs `Plan` (the priced plan) had no stated
  semantics, so consumers invented their own.

### Known issues
- `OrderPaymentFailedDueToInvalidMandate<TKey>` is declared and unit-tested but never dispatched by
  any code path. Flagged in the README; do not build on it.

## [0.3.0] - 2026-03-06

### Added
- `IHasTimestamps` and `IHasConcurrencyToken` interfaces for type-safe automatic timestamp/concurrency handling in `SaveChangesAsync`
- `UpdateCustomerAsync` on `IMollieClientService` — update customer name/email at Mollie
- Exception logging in `ManagedBillingEngine.ProcessDueItemsAsync` catch block
- 283 tests (was 281)

### Fixed
- **Critical:** Webhook idempotency guard silently dropped chargebacks — Mollie sends chargebacks with status still "paid", so the `status == status` early return prevented chargeback detection
- `CashierService.UpdateMollieCustomerAsync` now actually updates the customer at Mollie (was read-only stub)
- `CreditService.ApplyCreditAsync` concurrency retry now reloads only conflicting entities via `ex.Entries` instead of all tracked entities

### Changed
- All models with `UpdatedAt`/`RowVersion` now implement `IHasTimestamps`/`IHasConcurrencyToken` (Subscription, Payment, OrderItem, Order, Credit, Refund)
- `SaveChangesAsync` override uses interface casts instead of string-based property lookup — faster and compile-time safe
- Removed 16 redundant `UpdatedAt` assignments across services (handled automatically by `SaveChangesAsync`)

## [0.2.0] - 2026-03-04

### Added
- Dual billing engine (Strategy Pattern): `MollieBillingEngine` (Mollie native) and `ManagedBillingEngine` (local billing cycle)
- `CashierBackgroundService` for timer-based processing of due OrderItems (Managed engine)
- One-off charges via `IChargeBuilder` (with or without existing mandate)
- Coupon system: `ICouponRepository`, `ICouponHandler`, `CouponService` with fixed/percentage discount handlers
- Credit/balance system: per-owner, per-currency credits with add/apply/check
- Refund system: partial and complete refunds via Mollie API with local tracking
- Chargeback detection via webhook (`AmountChargedBack`)
- Payment method update flow (mandate renewal)
- Mandate validation and revocation
- Quantity management (update, increment, decrement) on subscriptions
- Invoice interface (`IInvoiceGenerator<TKey>`) with `NullInvoiceGenerator` default
- 23 domain events via `ICashierEventDispatcher`
- Webhook middleware (`UseCashierWebhook()`) with idempotency, payment ID validation, and error differentiation
- 281 tests (unit + integration + end-to-end)
- Input validation across all services (guard clauses, amount ceilings)
- Source-generated logging (`[LoggerMessage]`) for CA1848 compliance
- Concurrency retry for credit balance operations (`DbUpdateConcurrencyException`)

### Changed
- `NullCashierEventDispatcher` registered as Singleton (was Scoped)
- `SubscriptionOptions` converted to record type
- `Subscription.Quantity` changed from `decimal?` to `int?`
- Webhook middleware returns HTTP 500 on infrastructure errors (was 200), HTTP 200 on business logic errors

## [0.1.0] - 2026-03-03

### Added
- Initial project scaffold
- `Subscription`, `Payment`, `OrderItem`, `Order` models
- `CashierDbContext` with EF Core configuration
- `IBillable` interface for owner entities
- `ICashierService` with subscription CRUD (create, cancel, resume, swap)
- `ISubscriptionBuilder` fluent API for subscription creation
- `MollieClientService` wrapping Mollie.Api
- Basic webhook handling
- DI registration via `AddCashierMollie<TKey>()`
- Grace period and trial support
- MIT License
