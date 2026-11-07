# Backlog

Known open work, with the measurement behind each item. Nothing here is a bug report against a
released version unless it says so: most entries are *absent* features that the shipped
documentation already describes as absent.

**Every number in this file goes stale.** Where an item cites a count, treat it as the value at
the date given and re-measure before relying on it. Items are grouped by what they would change,
not by priority.

---

## Billing correctness

### The default engine registers no subscription with Mollie, so nothing recurring is charged

**The highest-priority item in this file.** Measured 2026-10-26:

- `MollieSubscriptionId` has **no assignment** anywhere in the library. Known-positive for the same
  search: `MollieCustomerId` has six, in four files.
- `IMollieClientService.CreateSubscriptionAsync` — the only method that registers a recurring
  subscription with Mollie — has **no caller** outside its own declaration and implementation.
- Both branches that cancel or update a subscription at Mollie are guarded on
  `!string.IsNullOrEmpty(subscription.MollieSubscriptionId)` and are therefore **unreachable**.

What happens today on `MollieNative`: a local subscription row, a first payment of
`PaymentMethodUpdateAmount` (default 0.01) to acquire the mandate, activation by webhook — and then
nothing. No recurring charge is ever made, by this library or by Mollie. The engine's own
documentation said it "delegates subscription CRUD to the Mollie API"; it did not, and that sentence
has been corrected at both places it appeared.

Everything downstream follows from this: a coupon cannot reduce a charge that is never made,
proration has no amount to prorate, and the dunning table's "Mollie retries" row described retries
of a subscription that does not exist.

**Options, with what each costs — not a decision:**

| | What it does | Cost / consequence |
|---|---|---|
| **A — create the Mollie subscription** | Call `CreateSubscriptionAsync` after the mandate is confirmed (in the webhook, where the mandate first exists) and store the id. Makes the engine what its name says. | Needs an amount and an interval, so it needs plan→price resolution, which does not exist either. Mollie then owns the cycle: amounts can only change through a subscription update, and a coupon becomes "update the Mollie amount", not "subtract at charge time". |
| **B — make `Managed` the default** | The engine that already charges becomes the one you get without configuring anything. | A breaking change in the failure semantics of every application that never set `BillingEngine` — which the README already calls part of the public contract. Also inherits the `UnitPrice = 0` gap: Managed charges what the consumer prices, and prices nothing by itself. |
| **C — fail closed** | `MollieNative` throws on create until A is built, so nobody stands on an engine that bills nothing. | Immediately breaks any consumer on the default, including those who only wanted mandate acquisition and bill elsewhere. Honest and loud; the harshest of the three. |
| **D — document only** | What this commit did. | The gap stays, and it stays discoverable only by reading. Acceptable only because nothing is in production billing yet. |

A and B both depend on plan price resolution (below), which is why neither is a small change. The
decision is not this repository's to take alone: it changes what a consumer's subscription means.

**Decision, 2026-10-26: A is the target — and it is not buildable yet.** The maintainer chose
"create the Mollie subscription at Mollie", with an explicit condition attached: *prices,
subscriptions and bundles are not settled.* Those are product decisions upstream of this library,
and A cannot be written without them — the call needs an **amount** and an **interval**, which is
the plan→price resolution below, which in turn needs a price list that does not exist.

So: **do not implement A on your own initiative either.** The gate is not a date and not a
judgement call to be made inside this repository; it is whether that pricing exists. B and C are
no longer directions anyone is holding open — recorded above so the next reader sees what was
weighed, not so they can be picked up again.

Until the gate opens the code stays exactly where D left it, documented and unchanged. **That is
an interim state, not the decision** — earlier the same day the standing position was D itself,
with no option chosen, and only that part has changed.

**When this stops being a backlog item:** it is documentation-only for exactly as long as no
consumer bills for real on the default engine. The moment one configures an API key and takes
money there, the same gap is a **release blocker** rather than an open item — and nothing in this
file will announce that change, because the code will not have moved. Re-read this entry whenever
billing is switched on anywhere, not when a date passes.


### Proration is not implemented, and says so loudly

`WithProration()` and a swap with `SwapOptions { Prorate = true }` throw `NotSupportedException`.
Until `eb8cd7f` the flag was accepted and ignored — five write sites, zero read sites — while its
XML documentation promised a credit for unused time that nothing computed. Failing loudly is the
interim state, not the destination.

Open: either implement proration (needs plan→price resolution, below) or remove the flag at the
next major version.

**Closed 2026-10-26:** both engines now guard `CreateSubscriptionAsync` and `SwapPlanAsync`, so
there is no path through the library that accepts the flag and ignores it. One condition and one
message, called from five sites. Measured first: neither known consumer calls an engine directly,
so the new throw is inert for the fleet today.

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

### Coupons: wired, and only the Managed engine can act on them

Closed the inert part on 2026-10-26. A coupon code given to the builder is now validated and
redeemed when the subscription is created — an unknown code throws instead of being dropped — and on
the `Managed` engine the discount is subtracted from every recurring charge until the coupon's
cycles are spent. A cycle is consumed only after the discounted payment succeeded.

On `MollieNative` the redemption is recorded and reduces nothing, because that engine makes no
recurring charge (top item). It logs a warning naming the code rather than staying silent.

**Still open, and both are deliberate:**

- ~~**The discount is not persisted.**~~ **Closed 2026-11-07.** `OrderItem.Discount` is written
  when the item is charged, so the amount reconciles from the row: `UnitPrice * Quantity -
  Discount`. The next cycle's item deliberately does not inherit it, and zero on an unprocessed
  item means "not charged yet" rather than "no discount".
  **Measured correction to this entry, which asked for a new `DiscountAmount` column:** the column
  already existed as `Discount`, with five mentions in the model and **zero** read or write sites
  anywhere in the library -- the same shape as the old silent `Prorate`. Adding `DiscountAmount`
  beside it would have shipped two columns for one concept, so the existing one was wired instead.
  The maintainer's decision named `DiscountAmount`; what it asked for was that the discount be
  persisted, and it is.
- **A redemption whose definition disappeared** (code removed from configuration after it was
  redeemed) charges the full amount and logs a warning. The third state is distinguished from "no
  coupon" in the API (`CouponDiscountStatus.DefinitionMissing`) rather than collapsed into it; what
  to *do* about it — honour a last known discount, suspend, notify — is unanswered.

### Metadata reaches the first payment, and only that one

Closed the inert part on 2026-10-26: `WithMetadata()` on the subscription builder and on the charge
builder now send metadata to Mollie as JSON, asserted on the request object handed to the Mollie
client. An absent or empty dictionary sends no metadata field rather than `{}`.

**Closed 2026-11-07.** `OrderItem.Metadata` holds the serialised JSON, each cycle's item inherits
it from the one it replaces, and the recurring charge sends it. One serialiser, shared with the
Mollie client, so the stored string is exactly what the payment carries and there is no second
representation to drift. A value that will not parse is logged with the item id and the charge goes
ahead without it: only this library writes that column, so an unreadable value means something
outside it changed the row, and losing the charge over metadata would be the worse failure.

Still open, and it is a different gap: **an item is only scheduled on direct activation.** A
subscription created through the first-payment checkout flow has no order item yet, so it has
nothing to hang metadata on and nothing scheduled either. That belongs with the first-payment flow,
not with metadata.

Not covered: a run against Mollie's test mode. The request object is verified here; that the field
survives the wire is not, and the credential for that lives in the secret store rather than
anywhere this repository can reach.

### No dunning or retry policy

There is no retry cap, no backoff, no auto-suspend and no customer notification. MollieNative
leaves retries to Mollie. Managed retries **by omission**: a failed order item is never consumed,
so the next cycle picks it up again, without limit.

Open: **a bounded policy.** The documentation half of this entry was already done when the entry
was written and the entry did not say so: the README carries a section "Who retries a failed
payment? (read before handling `OrderPaymentFailed`)" with a per-engine table and the explicit
sentence that there is no dunning logic in this library. Corrected 2026-10-26 — the marker was
unchanged while its justification had expired, which is the failure this file exists to avoid.

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

### The options detector counted writes as reads (fixed)

Recorded because the lesson outlives the fix. `OptionsSurfaceTests` was built to catch settable
options that nothing honours, and it had certified two of them as honoured: its scan matched every
mention outside a comment, so a *write* — the builder handing a value to an engine that ignores it —
proved that something read it. `CouponCode` and `Metadata` have exactly one such mention each, which
is why they passed.

The pattern now requires an instance dot and rejects a following single `=`. Verified by sabotage in
both directions on the same test: old pattern + undeclared option → passes, new pattern → fails.

**It then worked in the other direction within the same day.** When the coupon and metadata paths
were wired, the two options became read, and the test failed until their XML summaries were
rewritten — which is the whole point of pinning both directions. The entry below shrank from five
unread options back to three as a result.

Two things worth keeping from it: a detector whose criterion is "the name appears" cannot distinguish
reading from writing, and the sentences that document a gap **mention the gap's identifiers**, so the
comment stripper is what keeps the documentation from hiding what it documents.

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

### `HasPrecision`: set on all 13 decimal columns (closed 2026-11-07)

Decided by the maintainer on 2026-11-07 for the next minor, together with the two order-item
columns above: *"Ja, im naechsten Minor"*. All three landed in the same change, `main` now carries
version 0.5.0, and **nothing is published** -- the publish job is gated on `refs/tags/v*` and no tag
is cut, so a release still waits for an explicit go-ahead.

Every mapped decimal column declares `decimal(18,4)`. Before, the provider chose: unconstrained
`numeric` on PostgreSQL, `TEXT` on SQLite, `decimal(18,2)` on SQL Server -- measured there,
`11.3862` came back as `11.39` while PostgreSQL returned it whole.

Four places rather than two, because Mollie exchanges amounts with two and a calculation needs room
beneath them. Uniform across all 13, including the tax percentage: one rule has no per-column
judgement to get wrong, and it lets `Architecture/DecimalPrecisionTests` assert *every* mapped
decimal instead of a list that goes stale. Set per entity rather than by sweeping
`modelBuilder.Model` -- `ApplyCashierMollie` runs against the **consumer's** model, and a loop over
every decimal property in it would silently re-type their columns too.

The coverage came first, deliberately, and earned its place: `Integration/SqlServerTranslationTests`
asserted the *defect* (`(18, 2)`, and `11.39`), and setting the precision turned exactly those two
red. The flip is in the history. A fifth decimal place is still rounded away, asserted as the
known-negative, so "the value came back" cannot be satisfied by an unbounded column.

**Consumers need a migration.** The per-provider effect is in the README under "Upgrading to
0.5.0": widening on SQL Server, narrowing an unbounded column on PostgreSQL that only touches
values with more than four decimal places -- which nothing in 0.4.0 could produce -- and no change
on SQLite.

---

## CI and supply chain

### SBOM and license check: built, and the license verdict is advisory

Vulnerability scanning exists and **gates**: a step in the build job runs `dotnet list package
--vulnerable`, treats an unrecognised verdict as failure, and fails the build for the shipped
project while reporting the test project without failing. `NuGetAuditMode=all` and
`TreatWarningsAsErrors` sit in the root `Directory.Build.props`, so the test project inherits both.

**SBOM and license check closed 2026-11-07.** `.github/sbom-licenses.sh` runs on every build and
produces two CycloneDX BOMs -- the whole solution as the uploaded artifact, where completeness is
the point, and the shipped library alone as the scope the license verdict is formed on. The split
is the same property as the vulnerability scan's ("what ships"), not a list of exceptions, which
would go stale with the next package. `.github/license-classify.py` classifies what is in them.
Both BOMs are uploaded with 90-day retention, and the workflow records why that figure is a triage
window rather than a record anyone can rely on for a product's support period.

The data source was measured before the check was built, because the cheap route had an unmeasured
assumption in it: whether CycloneDX fills the `licenses` field for NuGet packages at all. It does
-- on the day it was built, 33 of 36 solution components resolved to an SPDX identifier.
**Presence is not knowledge, though:** the rest carry
`{"name": "Unknown - See URL", "url": ".../deprecateLicenseUrl"}`, a populated field that says
nothing, because those packages declare the legacy NuGet `licenseUrl` instead of an expression. A
check asking "does it have a licenses array" would have passed all 36.

**0 copyleft anywhere**, measured and still true. The unnameable ones are the live part of this
entry and the count moves with the dependency set -- adding the SQL Server provider the same day
took the solution from 36 components to 58 and brought `Microsoft.Data.SqlClient.SNI.runtime` in as
a fourth. Do not read a number off this page; the run prints the current set. What is settled:

- `xunit.abstractions` 2.0.3 is **Apache-2.0**, read at the package's own nuspec and shipped
  license text. It sits in `RESOLVED_BY_HAND` in the classifier, keyed by name **and exact
  version**, so a bump drops out of the table and goes back to UNKNOWN rather than inheriting a
  judgement nobody made about the new version. Every applied resolution is printed on every run,
  green or not. The caveat travels with it: the license URL the package declares points at a branch
  rather than a tag for 2.0.3, so the link does not establish it and the text does.
- `Mollie.Api` 4.18.0 is **still open and is the one that matters**, because it ships. It is also
  the only thing standing between the license verdict and being able to block.
- `SourceGear.sqlite3` and `Microsoft.Data.SqlClient.SNI.runtime` are test-only and do not gate.

A hand resolution can only ever replace UNKNOWN: where the BOM names a license, the BOM wins. An
override able to overrule a declared expression would be a way to silence a real finding, which is
the opposite of what the table is for -- pinned in the self-test, along with both orderings of
`<unnameable> AND MIT`, a direction that broke the same classification elsewhere by falling through
to whichever side happened to be written first.

**The license verdict is advisory, and the condition for making it blocking is a state, not a
date:** it flips when every component in the shipped BOM carries a license the classifier can
name, which today is one package short. The script announces that condition on every green run
instead of leaving it in a comment nobody reads on the day it comes true -- a date in exactly this
area has already been missed by three weeks once in this fleet.

What is **hard red** is anything that produces no verdict at all: a missing, empty, unparseable or
zero-component BOM, a BOM that is not CycloneDX, a solution BOM smaller than the shipped one, an
absent `python3`, or a failed classifier self-test. "I read nothing" must not be able to look like
"nothing found". Verified by sabotage in both directions: an injected `GPL-3.0-only` component in
the shipped BOM is reported as blocking copyleft (exit 1); a truncated BOM, a zero-component BOM
and a missing file each exit 2; and breaking the classifier's unknown branch so that everything
reads as permissive is caught by its own self-test, which then fails the step rather than the
step passing with a useless verdict.

Open, and not a tooling job: the three unnameable licenses have to be read off the packages
themselves. Nothing above replaces that, and the gate will keep naming them until someone does.

### Trusted publishing: workflow switched, policy not yet created (blocks the next release)

Decided by the maintainer on 2026-11-07: *"Ja, umstellen"* -- OIDC instead of a long-lived
`NUGET_API_KEY`. The workflow half is done. **The nuget.org half is not, and until it is, a tag
would fail to publish.**

What has to exist before a tag is cut, and these are the exact values the policy needs -- the
workflow filename is matched literally, so a near-miss reads as "no matching policy":

| Field | Value |
|---|---|
| Repository Owner | `theolymp` |
| Repository | `cashier-mollie-dotnet` |
| Workflow File | `ci.yml` |
| Environment | `release` |

`ci.yml` and not a separate `publish.yml`, deliberately: the publish job carries
`needs: build-and-test`, which is what keeps an untested commit from being released. Splitting it
into its own workflow would buy a tidier filename and lose that.

Also required: a GitHub Environment named `release` holding one secret, `NUGET_USER`, set to the
nuget.org **profile name** (not the e-mail address). Worth attaching a required reviewer to that
environment while creating it -- it turns "publish only after a go-ahead" from a convention into
something the runner enforces, and a NuGet version number cannot be withdrawn.

The old `NUGET_API_KEY` secret is deliberately left in place, unused. Removing it is a one-way
door and it costs nothing to keep until a release has gone out on the new path.

Failure modes, so neither is mistaken for the other: no policy means `NuGet/login` fails and the
run goes red, which is correct. The path this replaced failed the other way -- it carried
`if: env.NUGET_API_KEY != ''`, so a tag with no secret configured produced a **green run that
published nothing**.

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
- **SQL Server is covered since 2026-11-07** (6 tests, `Integration/SqlServerTranslationTests.cs`),
  which closes the last provider gap that could change an amount. Same shape as the PostgreSQL
  harness: connection string from `CASHIERMOLLIE_TEST_SQLSERVER`, no Testcontainers, skips with a
  stated reason when it is absent, and CI asserts the slice really ran. Measured there, against a
  real SQL Server 2022: all **13** mapped decimal columns resolve to `decimal(18,2)` -- read out of
  `INFORMATION_SCHEMA` rather than off the model -- and `11.3862` comes back as `11.39`, where
  PostgreSQL returns it whole. The column set is read from the catalog and its size asserted, so
  adding a decimal property turns the test red instead of silently going unchecked.
  PostgreSQL (skipped unless `CASHIERMOLLIE_TEST_POSTGRES` is set, and the skips state why) and
  SQLite (against a real file-less database) remain as they were.
- Test counts are deliberately not recorded here or in the contributor docs: a number in a
  document goes stale silently, usually within a day. Run the suite.
