# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed -- dependencies
- **Mollie.Api 4.18.0 -> 4.20.0**, which reaches you transitively. Its assembly is strong-named
  from 4.20.0 on; the API this library calls is unchanged. Measured before the bump, across
  Mollie.Api 4.18.0 and 4.20.0 on net10.0 and net11.0: the amount string sent to Mollie is the
  same in all four, rounded half away from zero to the currency's own places (`10.005` EUR becomes
  `10.01`, JPY has none). That is now a test, so a later update cannot move a charge by a cent
  unnoticed.
- EF Core 10.0.3 -> 10.0.15. EF Core stays on 10.x while the PostgreSQL provider has no stable 11.

### Fixed -- documentation that ships in the package
- **Four summaries claimed capabilities this library refuses.** Found in the artifact downloaded
  back from nuget.org minutes after 0.5.0 went out: `IBillingEngine.UpdateQuantityAsync` promised
  "seat-based billing" and `SwapPlanAsync` a plan swap without saying the amount is unchanged --
  that interface is public and DI-registered, so it is a reachable entrance with the wrong sign on
  it -- while `ISubscriptionBuilder.WithProration` and `SubscriptionOptions.Prorate` still promised
  proration, although both throw `NotSupportedException`. All four now state the refusal. A test
  pins the class rather than the sentences: a capability phrase may appear in a shipped summary
  only where that summary also names the refusal. 0.5.0 is immutable and keeps the old text.

## [0.5.0] - 2026-11-09

### Changed -- behaviour, breaking on one path
- **The Managed engine's repair path no longer carries a seat count into a charge.** When a
  subscription had no order item at all, the engine built one from the subscription, quantity
  included -- the only path on which `Subscription.Quantity` reached a charge, while three shipped
  XML summaries said no billing path read it. The item is created unpriced, so it looked inert;
  pricing it, the step those summaries prescribe for seats, billed `UnitPrice * seat count`
  (measured: quantity 7, price 9.99, Mollie asked for 69.93). The repair now writes a quantity of
  one, like every other path, so the documented claim is true rather than nearly true.
  **No compatibility switch**, deliberately: seat-based billing is not a feature of this library
  and MollieNative cannot have it, so a silent path into it was an inconsistency. If you relied on
  it, set the quantity on the pending order item together with its price.

### Changed -- supply chain
- **The published assembly no longer carries this repository's absolute build path.** `Pack` now
  passes `-p:PathMap=$GITHUB_WORKSPACE=.`, with the root read from the environment rather than
  typed. Measured: the carrier was exactly one string, the PDB path in the PE debug directory, and
  it travelled into a consumer's release image as `/home/runner/work/cashier-mollie-dotnet/...`.
  CI proves the rewrite on every push with two numbers from one pair of packs -- no absolute path
  *and* a rewritten relative one, because a zero alone cannot tell a mapped path from a debug
  directory that stopped being emitted -- and blocks the release if the package about to be pushed
  still carries one. 0.3.0 and 0.4.0 are published and immutable, so this takes effect from the
  next release.

### Changed -- documentation
- **The README's two set counts are pinned to the code.** It ships inside the package and states
  "23 domain events" and "7 entities across 7 tables", the second also spelled out as "seven
  tables". All five claims were measured correct; they are now compared against the declared event
  set and the EF model on every run, in digits and in words, so the consumer-facing copy cannot
  drift the way the backlog's counts had. No consumer-visible change today.
- **`BACKLOG.md`'s two "declared but inert" sets are pinned by tests.** Both were stale: it called
  `OrderNumberFormat` unread and `OrderProcessed` reserved, under headings counting "Three" and
  "Seven", months after the code, the XML summaries, the README rows and the architecture tests had
  all been corrected together. Each entry now names its set on one paragraph with a fixed prefix,
  and the tests compare that paragraph with the set the code pins -- so the file a contributor
  reads to find open work can no longer disagree with the code. No consumer-visible change.

### Changed -- package metadata
- **The rights holder is named as "the Olymp (Robin Parker)"** in `LICENSE`, in `Authors`, in the
  packed README's credits and, newly, in a `Copyright` field -- the nuspec carried no `copyright`
  element at all before, so that field was empty rather than wrong. `Company` is set to the same
  string, which is where a consumer inspecting the assembly reads it. Verified out of a built
  `.nupkg` rather than off the source. 0.3.0 and 0.4.0 are published and immutable, so this takes
  effect from the next release on. The remaining `TheOlymp` spellings in the package are URLs
  (`projectUrl`, `repository url`, SourceLink), not holder claims, and are left alone.

### Changed -- supply chain
- **The weekly scan timer is checked, not assumed.** A scheduled workflow re-scans the published
  package against an advisory database that moves while the code stands still -- and GitHub
  disables scheduled workflows in public repositories after 60 days without repository activity,
  with nothing going red. CI now reads this workflow's `state` from the Actions API, where
  `disabled_inactivity` is one of its values, and fails when the timer is off; the run's age is
  reported beside it without a verdict, because scheduled runs are delayed and dropped under load
  (measured: a `17 4 * * 1` cron fired at 12:28 UTC). Consumer-visible only in that a release is
  cut from a repository whose dependency scan is known to still be running.

### Changed -- schema, migration required
- **Every `decimal` column now declares `decimal(18,4)`.** Until 0.4.0 none of the 13 money columns
  set a precision, so each provider chose one: unconstrained `numeric` on PostgreSQL, `TEXT` on
  SQLite, `decimal(18,2)` on SQL Server. Measured against a real SQL Server, `11.3862` came back as
  `11.39` while PostgreSQL returned it whole -- the same code storing two different amounts
  depending on where it ran. Four decimal places rather than two: Mollie exchanges amounts with two
  and a calculation needs room beneath them, a proration of 29.99 over 11 days being 11.3862 before
  it is rounded to something chargeable. Set per entity rather than by sweeping the model, because
  `ApplyCashierMollie` runs against the consumer's `ModelBuilder` and a loop over every decimal
  property in it would silently re-type their columns too. Widening on SQL Server, so nothing
  stored there is lost; narrowing an unbounded column on PostgreSQL, which only touches values with
  more than four decimal places -- nothing in 0.4.0 could produce one. **A migration is required**;
  see "Upgrading to 0.5.0" in the README for the per-provider effect.

### Changed -- supply chain
- The licence check now blocks, in three tiers rather than two. The exit code is the tier: `0` free
  (permissive plus `MPL-2.0`), `1` case-by-case (LGPL, EPL, CDDL, MS-PL, MPL-1.x, unknown) reported
  without failing the build, `2` not measured, `3` stop (GPL, AGPL, SSPL, BUSL, proprietary). Only
  `2` and `3` fail. Two tiers was the defect: an LGPL dependency that wants documenting produced
  the same signal as an AGPL one that wants stopping, and a signal that cannot tell those apart
  stops being read. `2` and `3` are deliberately distinct, so "the licence is refused" cannot be
  confused with "the gate knows nothing". Scope is unchanged: only shipped components decide the
  code, and a test-only stop licence is reported loudly without failing.
- The licence gate's self-test asserts the **verdict** its scope guard returns, not just whether
  the guard refused. A shipped component absent from the solution BOM is `2`; an honest subset
  still reaches `0`, `1` or `3` on its own merits; a stop licence that is only in the solution
  stays `0`. The case that was missing is the second one: a guard that answers "not measured" to
  every subset is never wrong and never useful, and the old assertion -- did an exception come out
  -- could not tell the two apart.
- The same self-test now runs those cases through the script's **entry point** rather than through
  `report()`, and covers the argument handling. `report()` raises; the translation into exit 2 is
  `main()`'s, so nothing had ever exercised it. Sabotaging that handler to return 0 -- every
  unmeasurable BOM reported as a pass -- left the previous self-test green.
- Every licence-gate case now pins the run's **output** as well as its exit code, and a
  mis-invocation must print usage. An exit code cannot separate two branches that return the same
  number: deleting the warning that reports a stop-tier licence in the test tree left the self-test
  green, and that warning is the only signal that a vendor EULA is present at all. Eight mutations
  are caught, none of them tripping every check.
- The licence gate's output is now pinned branch by branch, found by deleting each `print` in turn:
  12 of 18 were removable without any test noticing, including the per-component itemisation, the
  component counts a zero has to be read against, and the reason a tier does or does not fail the
  build. One deliberate exception remains, a blank line for spacing. Cases also pin phrases the run
  must **not** emit, and the hand-resolution table is now exercised end to end rather than only per
  component -- including the vendor EULA in the test tree, which passes solely on scope and now has
  to say so.
- The licence gate's never-executed paths are now covered, found by tracing the self-test rather
  than by deleting branches -- the two instruments answer different questions. The three loader
  guards for a missing, empty or unparseable BOM had never run, and each now has to name its own
  cause; the UNKNOWN severity branch had never run either. One guard in the expression splitter is
  unreachable by construction and is marked as such instead of being described as covered.
- The licence gate asserts that exactly one project is packable, the one it scopes to. Adding a
  second packable project now stops the run as "not measured" rather than silently judging the
  first project's dependencies and saying nothing about the second's -- an all-clear over half of
  what ships.

### Changed -- release plumbing
- Publishing uses NuGet trusted publishing (OIDC) instead of a long-lived API key: the job
  exchanges a short-lived credential through `NuGet/login` and runs in a `release` environment,
  where a required reviewer can gate it. `packages: write` is gone -- nothing here pushes to GitHub
  Packages, only to api.nuget.org. **A trusted-publishing policy has to exist on nuget.org before a
  tag is cut**; without one the publish fails loudly, where the API-key path it replaced would have
  produced a green run that published nothing whenever the secret was absent.

### Added
- **The `Managed` engine now writes an order header when it bills an item.** Until now the only
  `new Order<TKey>` was in `ChargeBuilder`, so this engine created order *items* and never a
  header: `Number` never set, `ProcessedAt` never set, `OrderProcessed` never dispatched, and the
  invoice seam with nothing to receive. Additive -- `Order` already had every column and
  `OrderItem.OrderId` was already nullable, so there is **no schema change and no migration**; new
  rows only, nothing rewritten.
  One order per charge rather than per run, because each item is charged with its own Mollie
  payment and an order spanning several could not name the payment that settles it. The money
  columns mirror what was charged: `Subtotal` gross, `Total` net (the amount Mollie was asked
  for), `TotalDue` the same until a webhook reports it paid, and `Total = Subtotal - Discount`.
  `Tax` is 0 deliberately -- no billing path here adds tax to a charge -- and so is `CreditUsed`,
  because credit is a ledger nothing spends yet.
- **`OrderProcessed` is dispatched** when an order has been billed, carrying the order id. It was
  previously declared and raised by nothing; a handler registered before 0.5.0 never ran.
- **`OrderNumberFormat` is read**, filling `Order.Number`, which was previously always null. A
  value that is not a usable format string, or one longer than the 50-character column, leaves the
  number null and logs it rather than failing a billing run -- the charge has already happened by
  then, and an order without a number is the smaller problem. Set it empty to omit numbers.

### Fixed
- **A subscription created through the first-payment checkout flow never billed on the `Managed`
  engine.** Order items were created in one place and only on the direct-activation branch, so a
  subscription activated by the webhook was `Active` with no order item and nothing to bill --
  silently, for as long as it existed. `ProcessDueItemsAsync` now reconciles first: an active
  subscription with no end date and **zero** order items gets one scheduled, priced at 0 like the
  direct path, with `ProcessAt` derived from `CycleStartedAt` so the cycle is measured from the
  payment that started it. Idempotent by construction, and it repairs subscriptions already in that
  state rather than only new ones. Zero items is the condition, not zero *unprocessed* items: a
  subscription billing normally always holds a processed item and its successor.
- **An unpriced order item is no longer sent to Mollie as `0.00`.** `UnitPrice` starts at 0 by
  design, and such an item was charged anyway: Mollie rejected it, the engine dispatched
  `OrderPaymentFailed`, and the item -- never consumed -- returned every cycle forever, reporting a
  failed payment for something that was never a payment. It is now skipped with a stated reason.
  A *priced* item discounted to zero by a coupon is deliberately unchanged.

### Added
- **`OrderItem.Discount` is now written when an item is charged**, so a discounted amount
  reconciles from the row (`UnitPrice * Quantity - Discount`) instead of only from the Mollie
  payment and a log line. The column shipped in earlier versions with **zero** read or write sites
  in the library. Zero on an unprocessed item means "not charged yet", not "no discount", and the
  next cycle's item deliberately does not inherit it.
- **`OrderItem.Metadata` carries a subscription's metadata into every billing cycle.** Recurring
  charges are built from order items, so until now `WithMetadata()` reached the first payment and
  nothing after it. The column holds the serialised JSON the payment carries rather than a second
  representation of it, and the single serialiser is shared with the Mollie client so the two
  cannot drift. Each cycle's item inherits it from the one it replaces. A value that cannot be read
  back as JSON is logged with the item id and the charge goes ahead without it -- the money is
  still owed, and only this library writes that column, so an unreadable value means something
  outside it changed the row. **Note the limit:** an item is scheduled on direct activation, so a
  subscription that goes through the first-payment checkout flow has no item yet and nothing to
  hang metadata on.
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
- CI generates a CycloneDX SBOM and classifies the third-party licenses in it. Two BOMs: the whole
  solution, uploaded as the artifact because an inventory is asked for complete, and the shipped
  library alone, which is the scope the license verdict is formed on -- a copyleft test framework
  is not redistributed by this package. Measured before it was built: CycloneDX does populate the
  `licenses` field for NuGet, 33 of 36 components resolving to an SPDX identifier, but presence is
  not knowledge -- the other three declare the legacy NuGet `licenseUrl` and arrive as
  `"Unknown - See URL"`, a populated field saying nothing. Those are reported as unknown rather
  than folded into permissive. The license verdict is advisory and says on every clean run what
  would have to hold for it to block; failing to reach a verdict at all -- no BOM, an empty or
  unparseable one, zero components, or a failed classifier self-test -- is a hard failure, because
  "I read nothing" must not be able to look like "nothing found".
- Tests now also run against a real SQL Server, the provider where the same model resolves
  `decimal` to `decimal(18,2)` and rounds. No consumer deploys on it, which is why it was the gap:
  the provider nobody runs is the provider nobody notices, and it is the one that silently changes
  an amount. Six tests pin the harness really reaching SQL Server, the schema, the one decimal
  comparison the library performs in SQL, optimistic concurrency, that all 13 mapped decimal
  columns resolve to `decimal(18,2)` -- read out of `INFORMATION_SCHEMA`, with the column count
  itself asserted so a new decimal property cannot go unchecked -- and that `11.3862` comes back as
  `11.39` where PostgreSQL returns it whole. Configured exactly like the PostgreSQL harness:
  `CASHIERMOLLIE_TEST_SQLSERVER`, no Testcontainers, skip with a stated reason when absent, and CI
  asserts the slice ran. The precision tests are expected to change when explicit precision is set
  on those columns; that is what they are for.

### Added
- **A coupon now changes the amount.** Given a code, both engines validate it and record the
  redemption when the subscription is created — an unknown code throws instead of being dropped —
  and the `Managed` engine subtracts the discount from every recurring charge until the coupon's
  cycles are spent. The cycle is consumed **only after** the discounted payment succeeded, so a
  failed charge no longer costs the customer one. A discount never produces a negative amount.
  Pinned by tests that assert the amount handed to Mollie: 29.00 with a 10% coupon charges 26.10,
  without one charges 29.00, and a quantity of three discounts 3.00 rather than 9.00 — both shipped
  handlers multiply by quantity themselves, so passing a gross amount would apply it twice.
  Verified by sabotage: removing the subtraction fails three of the ten coupon tests and leaves the
  known-negative passing; passing the gross instead of the unit price fails only the quantity test,
  which is the only one that can see it.
  On `MollieNative` the redemption is recorded and reduces nothing, because that engine makes no
  recurring charge at all — it logs a warning naming the code rather than staying silent.
- **`ICouponService` gained `CalculateActiveDiscountAsync` and `ConsumeRedemptionAsync`**, and both
  engines now take an `ICouponService<TKey>`. Required rather than optional: a nullable dependency
  would mean an engine that silently cannot apply a coupon, which is the defect being removed.
  Neither known consumer constructs an engine directly (measured: no reference to `IBillingEngine`
  in either), so the constructor change is inert for them.
- **Metadata reaches Mollie.** `WithMetadata()` on the subscription builder and on the charge builder
  now send their dictionary as JSON on the payment request; an absent or empty dictionary sends no
  metadata field rather than `{}`. Asserted on the request object handed to the Mollie client, which
  is the last point this library controls. **Not covered:** a run against Mollie's test mode — the
  credential for that is held in the secret store, and a simulated call would be worth less than the
  gap it hides. Scope: the **first** payment only; recurring charges come from order items, which
  have no metadata column.
- `CouponDiscount` and `CouponDiscountStatus`: a discount carries why it is what it is, so a
  redemption whose definition has been removed from configuration is distinguishable from no coupon
  at all. The first charges full price and logs a warning; collapsing them would charge full price in
  silence.

### Changed
- **The default engine's documentation no longer claims to delegate to Mollie's Subscription API.**
  Measured 2026-10-26: `MollieSubscriptionId` has no assignment anywhere in this library and
  `IMollieClientService.CreateSubscriptionAsync` has no caller, so `MollieNative` registers no
  recurring subscription with Mollie and nothing is charged after the mandate payment; the
  cancel-at-Mollie and update-at-Mollie branches are unreachable. Corrected in the engine's own
  summary, in the README's Billing Engines section, and in the dunning table, whose `MollieNative`
  row described Mollie retrying a subscription that is never created. The gap itself is now the
  top item in `BACKLOG.md`, with options rather than a chosen solution.
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
