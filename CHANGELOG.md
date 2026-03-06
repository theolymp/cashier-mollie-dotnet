# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

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
