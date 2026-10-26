# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Tests now run against a real PostgreSQL, the provider both known consumers deploy on. SQLite
  covers relational schema creation and SQL translation, which InMemory does not; it does not cover
  translation against the deployed engine, and the same model resolves `decimal` to `numeric` on
  PostgreSQL, `TEXT` on SQLite and `decimal(18,2)` on SQL Server. Five tests pin the schema, that
  the harness really reached PostgreSQL, the one decimal comparison the library performs in SQL,
  optimistic concurrency, and that a `decimal` survives a round trip with more than two decimal
  places -- measured, not inferred from the column type. No Testcontainers and no Docker socket:
  the connection string comes from `CASHIERMOLLIE_TEST_POSTGRES`, CI supplies it from a service
  container, and without it the tests skip with a stated reason so a contributor without Docker
  still gets a green suite. CI asserts they actually ran by reading the passed count, because
  `dotnet test` exits `0` for passed, for all-skipped and for zero matching tests alike -- the same
  reason `scan-vulnerable.sh` reads findings instead of a status.
- CI fails on dependencies with known advisories (`dotnet list package --vulnerable`, direct and
  transitive). The check reads the output rather than the exit code, which is `0` either way, and
  it also fails when the output is unrecognised -- otherwise a scan that did not run is
  indistinguishable from a clean one.

### Changed
- **Proration now fails at every entry point, not just the documented two.** Both billing engines
  guard `CreateSubscriptionAsync` and `SwapPlanAsync`, so a caller who injects the public,
  DI-registered `IBillingEngine` no longer gets the old silence. Create is guarded as well as swap
  for the same reason `WithProration()` throws on the create path: the flag lives on
  `SubscriptionOptions`, and a caller who sets it believes proration is enabled for that
  subscription. The message and the condition now live in one place, called from five sites, so
  neither can drift. Measured before the change: neither known consumer calls an engine directly,
  so this completes the previous decision rather than taking a new one.
- **The coupon and metadata paths are documented as inert, in the package itself.** Measured
  2026-10-26: `SubscriptionOptions.CouponCode` and `SubscriptionOptions.Metadata` each have exactly
  one site in production code and it is the builder *writing* the value; nothing reads either.
  `WithCoupon()` therefore creates the subscription at full price with no redemption, no event and
  no error, `WithMetadata()` drops the dictionary (no request this library sends carries metadata),
  and `IChargeBuilder.WithMetadata` stores into a field nothing reads -- its previous summary said
  "for your own reference", and there was nowhere to refer to it from. `ICouponHandler.CalculateDiscount`
  has no call site either: it is a calculator the consumer invokes, so no coupon reduces any amount.
  All of this is now stated on the members, which ship in the package and so appear in IntelliSense.
  **Not changed:** these methods still accept and ignore their arguments. Making them throw, as
  `WithProration()` does, is a breaking change on a published API and therefore a decision, not a
  repair -- the same reason the swap and quantity no-ops are still accepted.
- `SwapOptions.Prorate` no longer documents the credit it stopped computing: the summary still read
  "calculates proration credit for unused time on the current plan" one commit after the property
  began to throw.

### Fixed
- **The options detector counted a write as a read, and had certified two silent no-ops for
  months.** `OptionsSurfaceTests` exists to catch settable options nothing honours; its scan matched
  every mention outside a comment, so the builder's `CouponCode = _coupon` -- a *write*, on its way
  to an engine that ignores the field -- passed as evidence that something read it. The pattern now
  requires an instance dot and rejects a following single `=`, keeping `==` and compound assignment
  as reads, and a new test pins the distinction against constructed input. Verified by sabotage:
  with the old pattern the suite stays green while `CouponCode` is undeclared, with the new one it
  fails -- same test, two patterns. The comment stripper earned its keep in the same change: the
  sentences added to say that these options are unread *mention both names with a dot*, and without
  stripping they would have credited them as read.
- The coupon documentation in the README did not match the code in three ways: the example called
  `ValidateAsync`/`RedeemAsync` with the wrong arity and argument order, the `Coupons` configuration
  was shown as a JSON array when the reader treats it as an object keyed by coupon code, and the
  documented fields (`Type`, `Value`, `MaxRedemptions`) are read nowhere -- the reader looks for
  `HandlerType`, `Times` and `Context`. The example as published could not compile and the
  configuration as published could not load.
- `ConfigCouponRepository` no longer uses `?? "percentage"` on a configuration key. `IConfiguration`
  returns an empty string for a key that is present and empty, so the coalesce never fired and the
  handler lookup then threw on `""`; an empty value is now treated as a missing one.

- **Breaking: `WithProration()` and a swap with `SwapOptions { Prorate = true }` now throw
  `NotSupportedException` instead of being accepted and silently ignored.** Measured 2026-10-04:
  `Prorate` had five write sites and **zero** read sites -- neither billing engine ever looked at
  it -- while the XML doc on `SwapOptions.Prorate` stated it "calculates proration credit for
  unused time on the current plan". A caller therefore received no credit and no error, which is
  the one state indistinguishable from a working implementation. Proration itself is still not
  implemented; this change only stops the library from claiming otherwise.
  **Closed since:** the engines guard it too -- see "proration now fails at every entry point"
  below. The guards no longer sit only at the documented consumer entry points.
  **Before proration is built,** the 13 `decimal` columns need an explicit precision: without
  `HasPrecision` they resolve per provider -- `numeric` on PostgreSQL (exact), `TEXT` on SQLite,
  `decimal(18,2)` on SQL Server. Proration produces fractional cents, so the same code would
  otherwise yield different money on different providers.
- Restores are now locked: `RestorePackagesWithLockFile` is enabled and `packages.lock.json` is
  committed for both projects. Restores resolve to recorded versions rather than to whatever is
  newest, and a dependency scan has an exact transitive list to work from instead of producing an
  empty result against a green build. Contributors: a deliberate dependency change now also updates
  the lock file, and `dotnet restore --locked-mode` verifies it.

### Deprecated
- `CashierMollieOptions.OrderNumberFormat` is slated for removal in the next major version. It was
  never read: no code in this library builds an order number at all, so a value here has never done
  anything. The removal waits for a major because the property is published API, and until then its
  documentation says so rather than describing an effect. Recorded here rather than in a comment
  because a major version bump is decided while reading this file -- a "remove it later" note
  elsewhere is a condition with nothing to make anyone read it.

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
