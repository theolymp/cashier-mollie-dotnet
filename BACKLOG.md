# Backlog

Known open work, with the measurement behind each item. Nothing here is a bug report against a
released version unless it says so: most entries are *absent* features that the shipped
documentation already describes as absent.

**Every number in this file goes stale.** Where an item cites a count, treat it as the value at
the date given and re-measure before relying on it. Items are grouped by what they would change,
not by priority.

---

## Billing correctness

### Proration is not implemented, and says so loudly

`WithProration()` and a swap with `SwapOptions { Prorate = true }` throw `NotSupportedException`.
Until `eb8cd7f` the flag was accepted and ignored — five write sites, zero read sites — while its
XML documentation promised a credit for unused time that nothing computed. Failing loudly is the
interim state, not the destination.

Open: either implement proration (needs plan→price resolution, below) or remove the flag at the
next major version.

**Not covered by the guards:** `IBillingEngine` is public and registered in DI. A caller who
invokes an engine directly with `Prorate = true` still gets silence; the guards sit at the two
documented consumer entry points (the subscription builder and `ICashierService.SwapAsync`).

### A plan swap changes the label, not the amount

`SwapPlanAsync` on the MollieNative engine assigns the new plan name locally and sends an **empty**
update to Mollie — the client guards on `amount.HasValue` and `description != null`, both of which
are false at that one call site. Mollie therefore keeps charging the previous amount. On the Managed
engine the swap takes effect on the next cycle, and the next order item copies the previous item's
unit price.

Open: whether these methods should throw like `WithProration()` does. That is a breaking change on
a published API, so it is a decision rather than a fix.

### Quantity management is not seat-based billing

`UpdateQuantityAsync` / increment / decrement update `Subscription.Quantity`. No billing path reads
that field. MollieNative sends nothing, so the charge is unchanged; Managed charges
`UnitPrice * Quantity` **of the order item**, and the next item copies the previous item's quantity.
To bill a new seat count, set the quantity on the pending order item.

Open: same decision as the swap above.

### No dunning or retry policy

There is no retry cap, no backoff, no auto-suspend and no customer notification. MollieNative
leaves retries to Mollie. Managed retries **by omission**: a failed order item is never consumed,
so the next cycle picks it up again, without limit.

Open: a bounded policy, or an explicit statement that dunning belongs to the consumer. The second
is a documentation task and would already be an improvement — the gap has been misread as "the
library handles retries" at least once.

### Credits are a ledger, not a settlement

`ICreditService` stores and applies balance, but nothing in either billing engine, the charge
builder or the webhook service reads it, and `Order.CreditUsed` is never set. Computing a remainder
and applying it to the next charge is the consumer's job today.

Open on MollieNative this cannot be closed as-is: Mollie charges its own fixed amount, so a credit
could only be realised as a refund afterwards or as a Mollie-side amount change. On Managed the seam
exists — the consumer owns `OrderItem.UnitPrice`.

---

## API surface

### Seven declared events are dispatched by nothing

`BalanceTurnedStale`, `MandateUpdated`, `OrderInvoiceAvailable`,
`OrderPaymentFailedDueToInvalidMandate`, `OrderProcessed`, `RefundFailed`, `RefundProcessed`.
Each now opens its XML summary with `RESERVED — no service dispatches this event`, which ships
inside the package, so the text appears in IntelliSense. Architecture tests fail in both
directions: a new undispatched event, or a wired event still listed as reserved.

Open: remove them at the next major version. Breaking, hence deferred rather than done.

### Three options are read by nothing in this library

- `ApiKey` — the Mollie client is authenticated by the consumer's own `AddMollieApi()` call, not
  from here. **This is a declared configuration slot, not a dead one:** consumers bind this section
  and read the key themselves. Do not drop it from your configuration on the strength of this line.
- `Locale` — never sent to Mollie. Mis-shaped as configuration: a checkout locale is per-request
  data, so a single configured value contradicts any per-user language choice. If it is ever wired,
  the default must change to "unset sends nothing" **in the same commit** — it currently defaults to
  `de_DE`, so naive wiring would force German checkout pages on every consumer.
- `OrderNumberFormat` — never read.

Each is marked `NOT READ BY THIS LIBRARY` in its XML summary and pinned by tests.
Open: remove `Locale` and `OrderNumberFormat` at the next major, or build `Locale` properly as a
per-request parameter.

### The invoice hook is never invoked

`IInvoiceGenerator<TKey>.GenerateAsync` has zero call sites; its only reference outside the
interface is the null implementation. It is a **callback** — the library owns orders and would hand
one over — so replacing it in DI with a real implementation produces no calls and no way to notice.
Marked as such on both the interface and the null implementation.

This is downstream of the order-header gap below: there is nothing to hand over.

### The order header is only created by one-off charges

The library's only `new Order<TKey>` is in `ChargeBuilder`. `ManagedBillingEngine` creates the only
order **items** and never sets `OrderId` nor creates a header, which the nullable `long? OrderId`
permits by design — items are scheduled, then grouped into an order when billed, and nothing does
the grouping. Consequences: `Number` never set, `ProcessedAt` never set, the order-processed event
never dispatched, and the invoice hook with nothing to receive.

Open: build the grouping. That is a feature, not a repair. Managed engine only — the default engine
creates no order items at all.

### Plan price resolution

There is no plan→price mapping. Consumers price items themselves; the first order item is created
with `UnitPrice = 0m` by design, with a comment saying so. Proration depends on this.

---

## Schema

### `HasPrecision` is not set on the decimal columns

13 decimal properties have no explicit precision, so the provider decides. On SQL Server the model
resolves to `decimal(18,2)`; measured, `numeric(18,2)` rounds `11.3862` to `11.39` while an
unconstrained `numeric` holds the value. Harmless while no money arithmetic exists in the library —
proration throws — but the same code would otherwise produce provider-dependent amounts.

Open: set explicit precision **before** any computation lands. Note this changes the generated
schema, so consumers would need a migration.

---

## CI and supply chain

### SBOM and license checks are absent

Vulnerability scanning exists and **gates**: a step in the build job runs `dotnet list package
--vulnerable`, treats an unrecognised verdict as failure, and fails the build for the shipped
project while reporting the test project without failing. `NuGetAuditMode=all` and
`TreatWarningsAsErrors` sit in the root `Directory.Build.props`, so the test project inherits both.

Missing entirely: SBOM generation and a license check (measured 2026-10-26: zero references in
`.github/`, with a known-positive confirming the scan reached those files).

### Scheduled scanning was added, and can be disabled silently

A weekly `schedule:` trigger now runs build, test and scan without a push, because the advisory
database moves while the code stands still. **Caveat:** GitHub disables scheduled workflows in
public repositories after 60 days without repository activity, and does so quietly — a silent timer
and a clean scan look identical from outside. Do not read "no alert" as "scanned".

---

## Test coverage

Green means the internal logic holds and translates to SQL. It does not mean the product works.

- **No runs against real Mollie.** The Mollie client is mocked throughout. The "integration" and
  "E2E" names in the suite are misleading in that respect.
- **SQL Server is not covered.** PostgreSQL is (skipped unless `CASHIERMOLLIE_TEST_POSTGRES` is
  set, and the skips state why), and SQLite is covered against a real file-less database.
- Test counts are deliberately not recorded here or in the contributor docs: a number in a
  document goes stale silently, usually within a day. Run the suite.
