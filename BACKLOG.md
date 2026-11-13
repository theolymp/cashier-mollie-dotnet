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

### Shipped summaries are pinned against the capabilities we refuse (2026-11-09)

Found by downloading 0.5.0 back from nuget.org and reading its XML documentation, minutes after
publishing: four summaries promised what the library refuses.
`IBillingEngine.UpdateQuantityAsync` said "seat-based billing" and `SwapPlanAsync` described a plan
swap without the amount caveat -- and that interface is **public and DI-registered**, so it is a
reachable entrance carrying the opposite sign to `ICashierService`, whose summary beside it was
correct. `ISubscriptionBuilder.WithProration` and `SubscriptionOptions.Prorate` still promised
proration, though `WithProration()` is `=> throw new NotSupportedException(...)` -- the defect
`eb8cd7f` closed, surviving on the interface because the fix went to the implementation's
documentation.

`Architecture/ShippedClaimsTests` now pins the **class**: a phrase naming a capability we refuse
("seat-based billing", "proration") may appear in a shipped summary only where that summary also
names the refusal. Its first version demanded one fixed negation per phrase and reported
`ProrationSupport` -- the class holding the guard's message and throw -- as a false claim; that
summary was accurate and the matcher was not, so the rule became "a mention must name the refusal"
rather than an exception list growing one entry per honest sentence. Verified by restoring each of
the four old summaries (each caught) and by misspelling a phrase, which fails the base-set guard
instead of sweeping clean.

**0.5.0 carries the old text and always will**, published packages being immutable. The next
version carries the correction.

### A plan swap changes the label, not the amount

`SwapPlanAsync` on the MollieNative engine assigns the new plan name locally and sends an **empty**
update to Mollie — the client guards on `amount.HasValue` and `description != null`, both of which
are false at that one call site. Mollie therefore keeps charging the previous amount. On the Managed
engine the swap takes effect on the next cycle, and the next order item copies the previous item's
unit price.

Open: whether these methods should throw like `WithProration()` does. That is a breaking change on
a published API, so it is a decision rather than a fix.

### Quantity management is not seat-based billing (the one exception closed 2026-11-09)

`UpdateQuantityAsync` / increment / decrement update `Subscription.Quantity`. MollieNative sends
nothing, so the charge is unchanged; Managed charges `UnitPrice * Quantity` **of the order item**,
and in the ordinary cycle the next item copies the previous item's quantity. To bill a new seat
count, set the quantity on the pending order item.

**"No billing path reads that field" shipped as a false claim, and is now true instead.** It
stood in three XML summaries on `ICashierService` -- so a consumer read it in IntelliSense -- and
in a comment sitting on the line that contradicted it. The exception was the repair path: a Managed
subscription with no order item at all got one built from the **subscription**, seat count
included, and that item was charged `UnitPrice * Quantity` the moment the consumer priced it, which
is the step the same summary tells them to take. A test had asserted the transfer
(`Assert.Equal(4, item.Quantity)`) since the repair was built; nothing compared it with the
sentence.

Measured end to end before deciding anything, because an item created unpriced looks inert and that
is how this survived: seat count 7, repaired item priced at 9.99, Mollie asked for **69.93**.

**Fixed 2026-11-09** (maintainer: "fixen ohne grossartig exception zu handlen. nutzt niemand
bisher!"). The repair writes `Quantity = 1`, like every other path. The direction matters: seats
were made *not* to reach a charge, rather than made to work everywhere -- seat-based billing is a
feature this library does not have and MollieNative cannot have, so one silent path into it was an
inconsistency, not a head start. **Breaking, with no compatibility switch**, which is the decision
as given. After the fix every remaining read of `Subscription.Quantity` is an increment computing
its own new value or the `SubscriptionQuantityUpdated` payload -- measured, four sites, none
billing.

Pinned by `StrandedSubscriptionTests.TheRepairIgnoresSubscriptionQuantity_SoNoPathBillsASeatCount`,
which asserts the charge is exactly 9.99 **while the subscription still says 7**, plus a theory
over a null and a set quantity. The old test was not deleted but turned around: the direction is
what changed, and it is the assertion that would notice the old behaviour returning.

Open: the swap decision above is untouched by this.

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

**The scheduling half of that gap is closed (2026-11-07); the metadata half is not.**

The gap was worse than "nowhere to hang metadata": an order item was created in exactly one place,
`CreateSubscriptionAsync`, and only on its **direct-activation** branch. A subscription that went
through the first-payment checkout flow is activated by the webhook instead -- `Active`,
`CycleStartedAt`, `SubscriptionCreated` -- and **nothing ever scheduled anything for it.** On the
`Managed` engine that is an active subscription that never bills, silently, for as long as it
exists.

`ProcessDueItemsAsync` now reconciles first: an **active** subscription with `EndsAt == null` and
**zero order items** gets one scheduled, priced at 0 like the direct path, with `ProcessAt` derived
from `CycleStartedAt` so the cycle is measured from the payment that started it rather than from
whenever the timer noticed.

Put there rather than in the webhook on purpose: `WebhookService` is engine-agnostic and holds
neither the options nor a logger, so teaching it to schedule would change a **public constructor**
-- and it would only help subscriptions activated after the change. A pass on the timer that
already runs costs no new dependency and no public surface, and it **heals rows that are already
stuck**. It is idempotent by construction, acting only where the item count is zero.

**Zero items, not zero *unprocessed* items**, and that distinction is the whole correctness of it:
a subscription billing normally always holds a processed item plus its successor, so it never
matches. Pinned by a known-negative; widening the condition to "no unprocessed items" fails exactly
that test and would bolt a spare item onto every healthy subscription between cycles.

Required alongside it: an **unpriced** item (`gross <= 0`) is now skipped with a stated reason
instead of being sent to Mollie as `0.00`. Before, Mollie rejected it, the catch dispatched
`OrderPaymentFailed`, and the item -- never consumed -- came back every cycle forever, reporting a
*failed payment* for something that was never a payment. The reconciliation pass creates such items
deliberately, so the guard is part of this change and not an extra.

**Still open, metadata half:** a subscription created through checkout has no metadata carrier.
`SubscriptionOptions.Metadata` reaches the first payment and is not stored anywhere, so the
reconciled item has nothing to inherit. Fixing it means a column on the subscription -- the same
migration decision as the others, so it is Robin's.

### A cycle discounted to zero is undecided, and deliberately left alone

`gross > 0` with a coupon taking the whole amount is **not** the unpriced case and was not touched.
Today it still sends `0.00` to Mollie, pinned by `ADiscountLargerThanTheChargeNeverProducesANegativeAmount`.

The two defensible answers differ in who loses: treat the cycle as **fulfilled** (no payment, item
consumed, coupon cycle spent, next item scheduled -- the customer had their free month), or treat
it as **not chargeable** and wait. The second stalls the subscription forever on a 100% coupon,
because the item is never consumed. Narrowing the guard to `gross` rather than `net` was the
deliberate choice *not* to answer this as a side effect of a scheduling repair -- it would have
redefined coupon semantics silently, and the existing test would have been edited to match, which
is how that kind of change hides.

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

### Declared events that nothing dispatches

Reserved, measured and pinned by tests: `BalanceTurnedStale`, `MandateUpdated`,
`OrderInvoiceAvailable`, `OrderPaymentFailedDueToInvalidMandate`, `RefundFailed`,
`RefundProcessed`.

Each opens its XML summary with `RESERVED -- no service dispatches this event`, which ships inside
the package, so the text appears in IntelliSense. Architecture tests fail in both directions: a new
undispatched event, or a wired event still listed as reserved.

`OrderProcessed` was a seventh name here until 0.5.0 and **is dispatched now** by the Managed
engine's order header. Same lag as the options entry above, same cause: the pinned set in the test,
the XML summary and the README row were all corrected, and this file was not -- so the heading said
"Seven" and the list named a live event. Both lines are pinned as of 2026-11-07.

Open: remove the six above at the next major version. Breaking, hence deferred rather than done.

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

### Options this library reads nowhere

Unread, measured and pinned by tests: `ApiKey`, `Locale`.

- `ApiKey` — the Mollie client is authenticated by the consumer's own `AddMollieApi()` call, not
  from here. **This is a declared configuration slot, not a dead one:** consumers bind this section
  and read the key themselves. Do not drop it from your configuration on the strength of this line.
- `Locale` — never sent to Mollie. Mis-shaped as configuration: a checkout locale is per-request
  data, so a single configured value contradicts any per-user language choice. If it is ever wired,
  the default must change to "unset sends nothing" **in the same commit** — it currently defaults to
  `de_DE`, so naive wiring would force German checkout pages on every consumer.

`OrderNumberFormat` was the third name here until 0.5.0 and **is read now** -- the Managed engine
builds the order number from it (measured at `ManagedBillingEngine.cs:259` and `:268`), so it stays
rather than going at the next major. The XML summary, the README row and the architecture tests
were all corrected when it was wired; this entry was not, and said "never read" for a day beside
code that read it.

Each name in the line above is marked `NOT READ BY THIS LIBRARY` in its XML summary, carries a
README row marked as unread, and is pinned in both directions -- wiring one up fails a test, and so
does adding a settable option nothing reads. **Since 2026-11-07 the line above is pinned too**, so
this entry cannot lag the code again the way it just did.

The heading carries no count on purpose. This file's own first rule is that every number in it goes
stale, and the previous heading said "Three" while the set was two.

Open: remove `Locale` at the next major, or build it properly as a per-request parameter.

### The invoice hook is never invoked

`IInvoiceGenerator<TKey>.GenerateAsync` has zero call sites; its only reference outside the
interface is the null implementation. It is a **callback** — the library owns orders and would hand
one over — so replacing it in DI with a real implementation produces no calls and no way to notice.
Marked as such on both the interface and the null implementation.

This was downstream of the order-header gap below: there was nothing to hand over. Since
2026-11-07 there is -- the Managed engine writes a header and dispatches `OrderProcessed`. So the
gap is no longer "nothing to give it"; it is that **nothing will ever call it**.

**Decided 2026-11-07: do not call it, and remove the interface at the next major.** Calling
`GenerateAsync` needs the generator in `ManagedBillingEngine`'s constructor -- a parameter on a
public constructor, which is breaking, so it fell outside the approval that covered the header. Of
the three ways out, the chosen one is the third:

- an optional constructor parameter -- keeps the seam alive at the cost of a constructor overload
  nobody asked for, and an interface whose default implementation does nothing;
- a required one -- breaking, for a callback that no consumer has asked for;
- **don't call it.** `OrderProcessed<TKey>` already carries the order id and the owner, so a
  consumer who wants an invoice generates it from their own event handler, with their own
  dependencies, in their own lifetime. That is strictly more capable than the callback: the handler
  can see the whole order, the callback would have seen what we chose to pass.

So `IInvoiceGenerator<TKey>`, `NullInvoiceGenerator` and their DI registration go at the next
major. Until then they stay declared and marked as uninvoked, because **removing them is the
breaking change and shipping them while silently believing otherwise is the bug** -- the same
shape as `Prorate`. Grouped with the dispatchless events and `Locale` above; all of it waits
for one major, rather than spending a breaking release each.

### The order header: written by the Managed engine since 2026-11-07

The library's only `new Order<TKey>` is in `ChargeBuilder`. `ManagedBillingEngine` creates the only
order **items** and never sets `OrderId` nor creates a header, which the nullable `long? OrderId`
permits by design — items are scheduled, then grouped into an order when billed, and nothing does
the grouping. Consequences: `Number` never set, `ProcessedAt` never set, the order-processed event
never dispatched, and the invoice hook with nothing to receive.

**Built 2026-11-07, after the maintainer approved it on the condition that it be additive.** That
condition was measured before any code was written, not assumed: `Order` already carries every
column needed, `OrderItem.OrderId` was already nullable, and `OrderProcessed` and
`IInvoiceGenerator` were already declared. So **no schema change, no migration, no new public
member** -- new rows in `cashier_orders` and `OrderId` set on newly billed items only. Nothing
rewrites an existing row.

**One order per charge, not per run.** Each item is charged with its own Mollie payment, so an
order spanning several items could not name the payment that settles it. "Grouping" was the
aspirational word for what this engine does.

The money columns mirror what was **charged**, which is the only way an order and its payment
reconcile: `Subtotal` = gross, `Total` = net = the amount Mollie was asked for, `TotalDue` = the
same until a webhook reports it paid, and `Total` = `Subtotal - OrderItem.Discount`, asserted.
`Tax` is **0 deliberately** -- no billing path in this library adds tax to a charge, so a non-zero
value would describe money that was never collected. `CreditUsed` is 0 for a related reason: credit
is a ledger nothing spends yet, which is its own entry above.

`OrderNumberFormat` is **read now**, which took it out of the unread-options set, and
`OrderProcessed` is **dispatched**, which took it out of the reserved-events set. Both directions
were pinned, so wiring each one had to turn its architecture test red first -- and both did, along
with two further tests asserting the README carries exactly one row per option and per event,
marked *iff* unread or reserved. Four tests, three of which I had not remembered writing.

A configured format that is unusable (`{1}`, an unbalanced brace) or longer than the 50-character
column leaves `Number` null and logs it. The charge has already happened by then: throwing would be
caught by the loop's handler and reported as a **failed payment for a successful charge**.

**The invoice hook this left behind is decided**, in the entry above: it is not called, and it goes
at the next major. It could not simply be wired here because calling it needs the generator injected
into the engine -- a parameter on a **public constructor**, which is not additive.

### Plan price resolution

There is no plan→price mapping. Consumers price items themselves; the first order item is created
with `UnitPrice = 0m` by design, with a comment saying so. Proration depends on this.

---

## Schema

### `HasPrecision`: set on all 13 decimal columns (closed 2026-11-07)

Decided by the maintainer on 2026-11-07 for the next minor, together with the two order-item
columns above: *"Ja, im naechsten Minor"*. All three landed in the same change and
shipped in 0.5.0 on 2026-11-09.

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
- `Mollie.Api` 4.18.0 is **MIT, measured 2026-11-07** -- the last shipped unknown, now closed.
  `<license type="file">LICENSE</license>`, the same second case as SqlClient.SNI, so the
  placeholder was never evidence either way. Read from the file in the nupkg (1089 chars, sha256
  `13e3f30c8400e4f3`, "Copyright (c) 2023 Vincent Kok") and checked past recognising the text: all
  five MIT clauses present, so not a fragment, and **nothing after the closing clause**, so plain
  MIT rather than MIT-plus-a-condition. That last check is the one that would have caught
  SqlClient.SNI, where the comfortable guess was wrong. Measured twice independently -- elsewhere
  in the fleet from the network copy, here from the local package cache, same bytes. The nuspec
  pins repository commit `9c6f40725eee9f687e7d369a0bded64664ad38d2`, an immovable ref for
  re-checking, unlike `xunit.abstractions` whose declared URL points at a moving branch.
  **Re-read for 4.20.0 on 2026-11-12**, because the resolution is version-bound and the bump made
  it UNKNOWN again: same text apart from line endings (1068 chars, no CRLF), same five clauses,
  nothing after the closing one. The SQLite native build changed package id with SQLitePCLRaw
  3.0.5 (`SourceGear.sqlite3` became `SQLite` 3.53.4, same 61-character file) and was re-read too.
  Left UNKNOWN on purpose, both test-only and reported on every run: `SqlClient.SNI.runtime`
  6.0.3 (the EULA entry covers 6.0.2 only) and `Identity.Client.NativeInterop` 0.20.6.
- `Microsoft.Data.SqlClient.SNI.runtime` 6.0.2 is **not open source**, which is worse than
  unnameable and was nearly recorded as the same thing. Its nuspec declares
  `<license type="file">LICENSE.txt</license>` -- a file reference, which is the whole reason the
  BOM shows only the placeholder -- and the file inside the nupkg is "MICROSOFT SOFTWARE LICENSE
  TERMS / MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY". Section 3(a) grants redistribution only as
  Distributable Code: inside an application, "not as a standalone distribution", and distributors
  and end users must "agree to terms that protect it at least as much as this agreement". **That
  second condition is the one that matters to a library**: an MIT-licensed NuGet package cannot
  impose it on whoever installs it. Measured here by reading the file out of the cached nupkg,
  after the finding arrived from elsewhere -- it was not taken on trust.
  It is **test-only in this repository**, arriving with the SQL Server provider, so nothing is
  redistributed and it does not gate. It classifies as `PROPRIETARY` via the SPDX `LicenseRef-`
  prefix, and the hand resolution **names it without approving it**: the verdict stays a finding.
  Any package that *ships* SqlClient has this in its own verdict set.
- `SourceGear.sqlite3` is test-only and unnameable; nobody has read it yet.

A hand resolution can only ever replace UNKNOWN: where the BOM names a license, the BOM wins. An
override able to overrule a declared expression would be a way to silence a real finding, which is
the opposite of what the table is for. It may move an UNKNOWN in **either** direction, and both are
in use -- `xunit.abstractions` becomes permissive, SqlClient.SNI becomes a blocking finding.
"Resolved" means someone read the license, not that the component passed -- pinned in the self-test, along with both orderings of
`<unnameable> AND MIT`, a direction that broke the same classification elsewhere by falling through
to whichever side happened to be written first.

**The licence verdict now blocks, in three tiers (decided 2026-11-07).** The exit code *is* the
tier: `0` free (permissive plus `MPL-2.0`), `1` case-by-case (LGPL, EPL, CDDL, MS-PL, MPL-1.x,
UNKNOWN) reported without failing, `2` **not measured**, `3` **stop** (GPL, AGPL, SSPL, BUSL,
PROPRIETARY). Only `2` and `3` fail the build.

Two tiers were the structural defect this replaces: with permissive-or-finding, an LGPL dependency
that policy wants *documented* produced the same yellow as an AGPL one that policy wants *stopped*,
and a signal that cannot tell those apart stops being read.

**The gap between `2` and `3` is load-bearing.** Reusing `2` for a policy stop would make "red
because the licence is refused" indistinguishable from "red because the gate knows nothing" --
opposite problems with opposite fixes. Case-by-case keeps `1` so a caller that has not been updated
meets an unknown `3` on a stop licence and fails hard rather than reading it as yellow.

`MPL-2.0` is free while the MPL family is not, so the narrower rule is consulted first and anchored
at **both** ends: `MPL-2.0-no-copyleft-exception` is a different SPDX licence and stays
case-by-case. Same shape as the missing `\b` that would have read "MITRE" as MIT. All five cases
measured: `GPL-3.0-only`→3, `LGPL-2.1-only`→1, `MPL-2.0`→0, `MPL-1.1`→1,
`MPL-2.0-no-copyleft-exception`→1, and a truncated BOM→2.

Every class is asserted to belong to exactly one tier, so adding a class and forgetting to tier it
is a failed self-test rather than a finding no branch reports. The script announces that condition on every green run instead of leaving it
in a comment nobody reads on the day it comes true -- a date in exactly this area has already been
missed by three weeks once in this fleet.

**That wording is a correction, and the reason is worth keeping.** Until 2026-11-07 this said "every
component carries a license the classifier can name", which was the same set as permissive when it
was written and stopped being so the moment the `PROPRIETARY` class existed: a vendor EULA *is*
named. The sentence would then have read as satisfied with a proprietary dependency in the shipped
set -- a condition announcing itself as met while the thing it guards against is present. The
mechanism was never wrong; it keys off the exit code, which only reaches 0 when everything shipped
is permissive. Only the sentence was wrong, and that is the worse half, because the sentence is
what a reader acts on. Found by a peer describing a different gap in their own version of this
check, which is the argument for saying out loud what your gate's condition is.

**That policy question is answered (2026-11-07) and implemented above.** What follows is kept
because the shape of the question outlived it: a threshold that reads as a measurement can decide a
policy nobody chose, and "it costs nothing today" is how such a choice reaches whoever is standing
there when it finally bites.

~~**The flip also carries an undecided policy choice, and it is not this repository's to make.**~~
"Permissive" excludes weak copyleft -- MPL, LGPL, EPL, CDDL, MS-PL -- so making the verdict
blocking would simultaneously decide that an MPL-licensed *dependency* blocks a release of an
MIT-licensed library. Defensible for a package that merely consumes one without modifying it, but
it is a decision and nobody has made it. The classifier has two outcomes, permissive or a finding;
a policy would plausibly want three, with weak copyleft as a documented case rather than a stop.

Measured 2026-11-07, which is why this is a note rather than a defect: 58 components, 8 shipped,
and **zero in any weak-copyleft class** -- known-positive, the classifier does recognise `MPL-2.0`,
`LGPL-2.1-only` and `EPL-2.0`, so the zero is a real negative and not a blind spot. The choice
therefore costs nothing today, which is exactly how it would reach whoever cuts a tag: unnoticed
until the first MPL dependency arrives, then decided under time pressure by the owner of a red
build. **It belongs answered before the flip.** Raised with root; the same mismatch was found
independently across the fleet, between a policy with three tiers and a tool with two.

**A count where membership was needed, found by accident and worth the entry.** The report
iterates the *solution* BOM and marks each component shipped or test-only, so a component present
in the shipped BOM but absent from the solution one is never classified at all -- invisible, in
exactly the half of the verdict that gates. The guard against that compared **sizes**
(`len(solution) < len(shipped)`), which passes at 9 shipped against 58 in the solution while one of
those 9 is missing. A sabotage run produced that state by accident and an injected `GPL-3.0-only`
in the shipped BOM came back **exit 0**. Now a set difference: any shipped key absent from the
solution BOM is "not measured", because the size check passed the first of those cases.

The self-test now asserts the **exit code** of that guard, not merely whether it raised. Asserting
"did it refuse" would have passed the comfortable repair -- routing every subset to NOT MEASURED --
which never lies and also never decides anything. Five cases: the missing shipped key is `2`, an
all-permissive subset is `0`, a STOP licence inside an honest subset is still `3`, a case-by-case
one is still `1`, and a STOP licence that is only in the solution stays `0` because this package
does not ship it. Two sabotages, caught by disjoint sets of those cases: the count comparison trips
only the first, the route-everything-to-2 repair only the other four. The peer session that hit the
same defect reported the same shape, and their framing is the sharper one -- the guard *could* only
compare sizes, because the source list carried a count and no members, so the difference was not
expressible there at all. Two sets of equal size can be disjoint.

And one step further out, from the same peer exchange: those cases went through `report()`, but
`report()` **raises** -- the translation into exit 2 lives in `main()`. A test that catches the
exception and asserts "this means 2" asserts its own belief about `main()`. Measured rather than
argued: sabotaging `main()`'s `except NotMeasured` to `return 0` left the previous commit's
self-test **green**, and that is the worst mutation this file admits -- every unmeasurable BOM
reported as a pass, "I read nothing" wearing the face of "nothing found". The cases now call
`main()` (a reentrancy flag stops `main()`'s own self-test gate recursing), plus four cases for the
argument handling nothing else reaches, because a gate invoked with the wrong number of paths has
measured nothing and must not look like a pass. The lesson is not about this guard: **the boundary
of the tested region is itself a claim, and it was in the wrong place twice in two commits.**

**Third layer, and the one with a consequence for this repo: an exit code cannot tell two branches
apart that return the same number.** Deleting the test-only STOP warning *entirely* left the
self-test green -- measured, not supposed. That warning is the only thing that surfaces a vendor
EULA sitting in the test tree, which is precisely the state this repo is in
(`Microsoft.Data.SqlClient.SNI.runtime`). Lose it and the gate still exits 0, every check still
passes, and nobody learns that a proprietary licence is present. So every case now pins **what the
run says** as well as what it returns, and a mis-invocation has to print usage, because exiting 2
in silence is indistinguishable from the BOM guards and the two have different fixes. Eight
mutations, all caught, failed-check counts scattering 1 to 13: four of them trip exactly one check,
so each has its own assertion rather than riding on a neighbour's. The message assertions also
separated two mutations that until then produced the same signature.

**Then the same question asked mechanically, which is the only way it should have been asked.**
Deleting each `print` in `report()` and `main()` one at a time: **12 of 18 were removable with
nothing noticing** -- the per-component itemisation, the "Measured: N components, M shipped" base
set, both hand-resolution branches, the per-finding warning with licence and class, the reason a
STOP fails the build, the reason a case-by-case does not, and the free-tier verdict line for the
test-only path. A hard-red run could have stopped naming the packages it was red about. Now 1 of 18
survives and it is a bare `print("")` for spacing, named here so it is a decision rather than a
leftover. Required phrases per case, plus **forbidden** phrases, which is what catches a run saying
"resolved by hand" about a component whose licence the BOM declared.

Two things fell out of pinning those branches. Nothing had ever reached the hand-resolution table
end to end, so there are now cases for a resolution in the permissive direction, the vendor EULA as
it actually sits here (test-only, exit 0 **because of scope**, and it says so), the same component
shipped (exit 3), and a declared licence that the table must not override. And a comment I wrote in
the same commit claimed that last rule "lived in a comment and nothing tested it" -- **false**, it
was pinned at component level all along, twelve lines above, with its own comment saying so. Caught
before the commit by reading the mutation output instead of only its exit code: an assertion fired
that I had not added. Three commits spent on statements that stopped matching the code, and the
fix for the third nearly shipped a fourth.

**Then a second instrument, because deletion and coverage answer different questions.** Deleting a
branch finds what no test *distinguishes*; tracing which lines a run *executes* finds what no test
**reaches**, and a branch can be unreachable while every deletion test stays green. Traced: of 144
executable lines outside the self-test, 13 never ran. Four were real:

- **The three file-level loader guards** -- missing file, zero-byte file, unparseable JSON -- had
  never executed. These are the guards this gate leans on hardest, the ones that keep "I could not
  read it" from being reportable as "I found nothing", and this file's own entry above claimed they
  were "verified by sabotage in both directions". They had been: by hand, once, and never pinned.
  Now pinned with the cause each must **name**, because a missing, empty, truncated and non-object
  BOM have four different fixes and only the message separates them.
- **The UNKNOWN severity line**, so the tier that exists precisely for "nobody has read this yet"
  was being reported by an untested branch.
- **An unreachable guard in `classify()`.** `cleaned` is stripped and non-empty and the OR
  delimiter requires whitespace on both sides and consumes it greedily, so a split can never yield
  an empty piece. Kept, not deleted -- the line below it calls `min()` over a generator and would
  raise on an empty one, and the edit that would break the invariant is a regex change. Marked as
  unreachable rather than described as covered.

And a fourth instance of the same class, mine again: the comment introducing the new malformed-OR
cases said they reached that guard. **They do not.** A first probe appeared to confirm they did and
was wrong -- it matched a line number across frames, and filtering by function name reversed the
result. So the sequence now reads: a false claim about test coverage, caught; then a false claim
about what a new test covers, caught by distrusting the instrument that confirmed it. Both were
mine, in the same file, inside an hour, while fixing this exact class.

**And one limit of the build shape itself, turned into a guard rather than a note.** The verdict
scopes to one packable project, which is right today and silently wrong the moment a second
appears: the gate would judge this project's dependencies and say nothing about the other's, which
reads as an all-clear over half of what ships. A peer reached the same hole through a different
door -- their scope flag accepted repeats, `next(...)` took the first and dropped the rest, and a
repo with two shipping projects got a clean verdict over half its shipped set. Their summary is the
one worth keeping: **a tool that answers correctly and answers a different question.** I had argued
that morning that putting scope in the signature means it cannot be forgotten; it cannot be
forgotten, but it can be **halved**, and that is not a property of flags. So `IsPackable` is now
asserted (measured: true for the library, false for the test project) and a second packable project
stops the run as NOT MEASURED instead of quietly narrowing the scope. Verified both ways -- passes
today, fires with a throwaway packable project in the tree.

What is **hard red** is anything that produces no verdict at all: a missing, empty, unparseable or
zero-component BOM, a BOM that is not CycloneDX, a shipped component absent from the solution BOM, an
absent `python3`, or a failed classifier self-test. "I read nothing" must not be able to look like
"nothing found". Verified by sabotage in both directions: an injected `GPL-3.0-only` component in
the shipped BOM is reported as blocking copyleft (exit 1); a truncated BOM, a zero-component BOM
and a missing file each exit 2; and breaking the classifier's unknown branch so that everything
reads as permissive is caught by its own self-test, which then fails the step rather than the
step passing with a useless verdict.

The closing line leads with the **worst class present** rather than with a count, because a count
puts a vendor EULA and an unreadable placeholder at the same priority and they are not the same
kind of problem. Measured with four injected findings at once: it reports `PROPRIETARY` as the
worst and lists only the two blocking ones, leaving the weak-copyleft and unnameable ones out of
that list.

**Every licence in this repository has now been read** (2026-11-07). `SourceGear.sqlite3`
3.50.4.5, the last one, is **public domain** -- and it is the case that proved a caveat written two
hours earlier rather than a routine close.

Its `LICENSE.txt` is 61 characters: "SQLite is Public Domain" plus a link (sha256
`99464c3a88df7b70`). Thin enough that the linked authority was read too, which states the
dedication in terms. **No SPDX identifier fits:** `blessing` is SQLite's "do good and not evil"
licence and that text is absent, and CC0-1.0 is a legal instrument a plain dedication does not
invoke. So the only thing writable is a `LicenseRef-` -- which the `PROPRIETARY` pattern catches,
and would have classified a public-domain package as **the exact opposite of what it is.** The
caveat on that class said the prefix proves only "not an SPDX identifier"; this is what that
looked like in practice.

Hence a reserved marker, `LicenseRef-PublicDomain`, with one property worth keeping: **it is
honoured only in a hand resolution, never from a BOM.** A package declaring that marker about
itself stays `PROPRIETARY`, because the marker means "someone read the file" and only the
resolution table records that -- an exception a component can assert about itself is not a
measurement. Pinned in both directions, including `LicenseRef-PublicDomainish-Vendor` staying
proprietary, since an exception carved into a pattern is otherwise the one part nothing checks.

Caveat that travels with the entry: the file speaks about **SQLite**, not about the **SourceGear
build** of it -- compilation, patches and packaging are not addressed. Acceptable for a test-only
dependency, which this is. In the shipped set someone would have to establish that build's
provenance, and this entry would not be enough.

### Trusted publishing: in use since 0.5.0

Decided by the maintainer on 2026-11-07: *"Ja, umstellen"* -- OIDC instead of a long-lived
`NUGET_API_KEY`. **0.5.0 was the first release on this path** (2026-11-09, run 42607335823).

What a tag depends on, and these are the exact values the policy holds -- the workflow filename is
matched literally, so a near-miss reads as "no matching policy":

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
nuget.org **profile name** (not the e-mail address). The environment carries a required reviewer
(read from the environments API), which turns "publish only after a go-ahead" from a convention
into something the runner enforces, and a NuGet version number cannot be withdrawn.

The old `NUGET_API_KEY` secret was deliberately left in place, unused, until a release had gone
out on the new path. One now has, so the reason for keeping it is gone. Open: removing it, which is
a one-way door and the maintainer's to do.

Failure modes, so neither is mistaken for the other: no policy means `NuGet/login` fails and the
run goes red, which is correct. The path this replaced failed the other way -- it carried
`if: env.NUGET_API_KEY != ''`, so a tag with no secret configured produced a **green run that
published nothing**.

The red case happened on the first attempt at 0.5.0: `NUGET_USER` named an account nuget.org does
not have, `NuGet/login` failed, nothing was pushed. Correcting the secret and re-running the same
tag was enough (attempt 3 of that run published; attempt 2 was cancelled), so no version number was
spent. When checking such a name from outside, a 404 on the profile page means "no such account"
only because a profile without packages still returns 200.

### Renovate and the merge gate: config in the repository, both GitHub halves open (2026-11-12)

`renovate.json` exists since 2026-11-12 and validates (`renovate-config-validator --strict`).
**It does nothing yet, measured:** this repository lives on GitHub, the fleet's Renovate does not
reach it, and no Renovate app has ever acted here -- zero pull requests of any author, no
onboarding PR, no dependency-dashboard issue, one branch (`main`), read from the public API. So
until the first half below is done, nothing updates itself, and every version here moves only
when someone moves it by hand, which is the state this entry exists to end.

The config follows the fleet's rule set: patch, minor, digest and lock-file maintenance merge
automatically, **majors get a PR of their own without dashboard approval** (approval would create
no PR at all), and EF Core, Npgsql and the Pkcs CVE pin are held below 11 and the SDK below 12
(ADR-0016). Majors open today, measured 2026-11-12, all left out of the manual bump on purpose:
`Mollie.Api` 5.x (touches every amount we send, so the wire-format tests are its review),
`Microsoft.NET.Test.Sdk` 18, `coverlet.collector` 10, `NSubstitute` 6, `xunit.runner.visualstudio`
4.

**Automerge is only safe behind a gate, and the gate is not on.** `main` is unprotected, measured:
`protected: false`, no required status check. The PR pipeline itself is a real gate already --
`build-and-test` runs on `pull_request` and blocks on the vulnerability scan, the licence verdict,
the build and every test on both frameworks against real PostgreSQL and SQL Server -- but nothing
stops a merge while it is red. Both halves need repository admin rights, which this session does
not have, so they are the maintainer's:

1. **Gate first.** A ruleset (or branch protection) on `main` requiring the status check
   `build-and-test` -- that is the check name the jobs API reports. Decision inside it: a required
   check also applies to direct pushes, so either the pushing account is on the bypass list or
   work moves to PRs. Then the evidence, the GitHub counterpart of the fleet's 405: open a PR
   whose check is red and call `PUT /repos/theolymp/cashier-mollie-dotnet/pulls/<n>/merge`; the
   answer must be **405** naming the required check. A merge that succeeds there means the gate
   is not on, whatever the settings page shows.
2. **Then Renovate:** install the Renovate GitHub app for this repository, or add it to a
   self-hosted Renovate with a GitHub token. In this order, because the config already says
   `automerge`, and the app arriving before the gate would merge on green checks with nothing
   stopping a merge on red ones.

**State on 2026-11-13: the gate is on and proven; Renovate is not installed.** Ruleset 27213286
on the default branch requires `build-and-test`, bound to GitHub Actions as its source (integration
15368), so a status of the same name from anywhere else does not satisfy it; deletion and
force-push are refused too. **No bypass actors**, deliberately: a bypass for admins would also let
an admin merge a red PR, and every account that works here is an admin. The consequence is that
`main` takes changes only through pull requests -- a direct push has no status yet and is refused.
"Allow auto-merge" is on, which Renovate's `platformAutomerge` needs.

Evidence, from PR #1 (a deliberately failing test, closed unmerged, branch deleted):
`PUT /pulls/1/merge` returned **405** twice -- *"Required status check "build-and-test" is
queued"* while the run was pending, and *"... is failing"* once it was red. The known positive is
the pull request that carried this paragraph: green, and merged.

Still open, maintainer only: install the Renovate GitHub app for this repository (step 2 above).
Until then nothing updates itself.

### Absolute build paths: removed from the artifact, still live in published images (2026-11-08)

Pantheon found absolute build paths inside release images; the CRA evidence rests on image digest
plus release SBOM, so deterministic paths are what make two builds of one source comparable. This
repository is unusual in the rollout: **our** path showed up in **another** repository's image,
because `CashierMollie.dll` is published to nuget.org and consumed from there.

Measured, not assumed: the carrier is **one** string, the PDB path in the PE debug directory
(`<root>/src/CashierMollie/obj/Release/net10.0/CashierMollie.pdb`). No SourceLink entry, no
`AssemblyMetadata`, no UTF-16 copy -- ascii 1, utf16 0, which is this repository's base set and
not a constant: a sibling measured two UTF-16 copies that must stay two.

`-p:PathMap=$GITHUB_WORKSPACE=.` on the `Pack` step rewrites it. Evidence is two numbers from one
pair of packs: zero absolute paths **and** at least one rewritten relative path, because a zero
alone cannot distinguish a mapped path from a debug directory that stopped being emitted. Both
abort conditions are refusals rather than passes: a reference pack carrying no path proves
nothing, and an empty search set satisfies any expectation.

**What does NOT transfer from the rollout, measured here:**
- **Byte comparison against a reference build.** Two clean packs, same root, same flag, differ in
  72 of 199,168 bytes -- PE stamp, MVID, PDB signature in the debug directory. IL and metadata are
  identical, only identity GUIDs move. Byte equality would be a permanent false alarm here.
- **"One known case per side" for own-versus-foreign classification.** The package ships exactly
  one assembly, our own; dependencies are package references and are not redistributed. There is
  no foreign side to name, so `foreign == 0` is the shape of the artifact. What is bolted instead
  is the search set itself: exactly `lib/net10.0/CashierMollie.dll`, count one, or no verdict.
- A retracted measurement of mine, kept because it cost an hour: "same source, two different
  roots, byte-identical?" answered no, and that proved nothing -- the second root came from
  `git archive`, so it had no `.git`, hence no SourceLink data and no commit suffix. The
  comparison was contaminated. A peer hit the mirror image: there `.git` was missing and the flag
  did nothing, here it was missing and the comparison did too much.

**Open, and not ours to close:** published packages are immutable. 0.3.0 and 0.4.0 keep their
paths. 0.5.0 is the first version without one -- verified on the package downloaded back from
nuget.org: zero absolute paths, the rewritten path present. A consumer's image changes only when
that consumer upgrades to it. The state to report is "mechanism proven, image open".

### Scheduled scanning was added, and the timer is now checked (2026-11-07)

A weekly `schedule:` trigger runs build, test and scan without a push, because the advisory
database moves while the code stands still. GitHub disables scheduled workflows in public
repositories after 60 days without repository activity, so the timer can stop with nothing going
red, and from outside a dead timer and a clean scan look identical.

**That caveat is now measured instead of only written down.** `state` is a field on the workflow
API and `disabled_inactivity` is one of its values, so what the Actions UI does quietly is in fact
readable: `.github/schedule-liveness.sh` reads it on every run and fails the build when the timer
is off, naming the one command that fixes it. Two claims, deliberately kept apart, because they
are not the same kind of statement:

- **state** is measurement. Non-active is a hard failure, and each of the four states the API can
  return carries its own cause and its own fix rather than one shared "disabled".
- **age** is inference, and is reported without a verdict. Measured 2026-11-07: a `17 4 * * 1`
  cron produced a run at 12:28 UTC, eight hours late, because GitHub delays and drops scheduled
  runs under load. An age threshold would therefore be a flake generator, and the runs API only
  keeps a window, so "no scheduled run listed" cannot be told from "never fired".

The verdict lives in `.github/schedule-liveness.py` with a self-test that drives all four tiers
through `main()` and pins the phrase each one must say; five sabotages (always-alive, event filter
dropped, unknown state read as active, the four causes collapsed into one message, age window
ignored) each turn it red with disjoint signatures, against a baseline measured green first.

**Reading the verdict from outside, without a login:** the step is green for alive, for unclear
and for not-measured alike -- three tiers, one colour, which is the very confusion this entry is
about. The discriminator is annotations, which the API serves unauthenticated while job logs need
a token: tier 3 fails the job, tiers 1 and 2 emit `::warning::` and therefore appear in
`/repos/.../check-runs/<job-id>/annotations`, and tier 0 emits nothing. So green **plus no
liveness annotation** is "measured alive". Verified that way on `84e53e0`: four annotations, all
of them the licence gate's known two plus two runner notices, none from this check.

**Open, and structural:** the check runs inside the workflow it measures. GitHub's documentation
does not say whether automatic disabling also stops `push` and `pull_request`. If it does, no run
exists to report it — the symptom is then a commit with no checks at all, not a red one. The only
real fix is a watcher outside this repository, which is not this repository's to build. Until then
the check covers what it can reach (manual disable, fork default, deleted workflow, a timer that
is active but stopped producing runs) and cannot make the unreachable case worse.

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
