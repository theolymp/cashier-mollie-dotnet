# Dunning: design proposal (2026-11-23, not decided, no code)

Status: **proposal for a decision.** Nothing here is built. This file goes away or turns into a
"decided" note once a decision is made; the open entry in `BACKLOG.md` ("No dunning or retry
policy") points here.

## What happens today (measured on `main`, 0.6.0-beta.6)

**MollieNative (the default, and the engine the first consumer runs):**

- A failed priced payment makes the subscription `past_due` at once (`WebhookService.ReconcileDefaultAsync`)
  and dispatches `SubscriptionPastDue` (`NeverPaid` tells a failed first debit apart).
  `Subscription.IsActive()` is false from that moment, so **access ends at the first failure**
  unless the consumer adds a grace period itself (`Payment.FailedAt` is stored for that).
- Mollie does **not** charge the failed amount again. The Mollie subscription keeps running, so the
  next cycle is charged as normal from the same mandate. If that one fails too, the debt grows by
  one cycle.
- The customer settles through `PayOutstandingAsync` (a checkout, any payment method). Paying
  everything outstanding makes the subscription active again and moves the Mollie subscription to
  the mandate that just paid. A subscription cancelled while `past_due` stays cancelled after paying.
- Nothing ever gives up: no retry cap, no automatic cancellation, no e-mail. All of it is the
  consumer's today (README, "Who retries a failed payment?").

**Managed:** a failed order item stays unconsumed and is picked up every cycle, without limit.
Out of scope below, because no consumer runs it; the chosen option should cover it in a later
release.

**Not measured, needed before any of this is built (test-mode run):** whether Mollie cancels a
subscription itself when its mandate becomes invalid, and how long a failed SEPA debit takes to be
reported (it is not immediate, unlike a card).

## The three questions

1. **Retry:** does anything charge the failed amount again automatically, how often, how far apart?
2. **Access:** when does the customer lose access: at the first failure, or after a grace period?
3. **E-mails:** who tells the customer, when, and with what?

## Option A: no library change, policy in the consumer

The library stays as it is. The consumer runs its own scheduled job over `past_due` subscriptions,
reads `FailedAt` of the oldest outstanding payment, sends its e-mails with a `PayOutstandingAsync`
link, keeps access during its own grace period, and calls `CancelImmediatelyAsync` (allowed on
`past_due` since beta.6; the debt stays payable) when it gives up.

- Pro: zero library work; e-mails, wording, languages and the access rule stay where the customer
  relationship is.
- Con: every consumer rebuilds the same timer and the same day arithmetic; the library's
  `past_due` still means "no access", so a grace period means the consumer must not use
  `IsActive()` for its access check, which is easy to get wrong.
- Effort: library 0; consumer about 2 days (the consumer's own estimate is not in yet).

## Option B: the library retries the debit and gives up on its own

New `DunningOptions` (for example: retry after 3 and 7 days, cancel after 14). A background job,
which MollieNative does not have today, charges the outstanding amount again from the stored
mandate, dispatches a stage event per attempt, and cancels at the end. E-mails stay with the
consumer, driven by the events.

- Pro: recovers the common case (a temporarily empty account) without the customer doing anything.
- Con: every failed SEPA retry is another returned debit (bank fees and the customer's own bank
  charging them; Mollie's fee for it not measured); retrying makes no sense for every failure
  reason (closed account, revoked mandate), so the failure reason has to be read and classified;
  a second payment can collide with the customer paying through the checkout at the same time, so
  it needs the claim pattern again. Largest change, and it is behaviour the customer sees on their
  bank statement.
- Effort: library about 4 days plus a test-mode run per failure reason.

## Option C (recommended): the library keeps the clock, the consumer acts on it

No automatic re-debit. The customer pays the debt through the checkout, with any method, which
also replaces a broken mandate. The library adds the timing and the end state:

- `DunningOptions { GraceDays, ReminderDays[], CancelAfterDays }` (defaults off, so existing
  consumers see no change).
- A small background job on MollieNative dispatches `DunningReminderDue` (stage, days overdue,
  amount, a checkout link from `PayOutstandingAsync`) on the configured days, and cancels
  (as `CancelImmediatelyAsync` does today, debt stays payable) after `CancelAfterDays`.
- A query helper `HasAccess(subscription)` that is true while `active`, or `past_due` within the
  grace period, so consumers stop hand-rolling the grace check.
- The consumer sends the e-mails from the events (its templates, its language).

Suggested defaults to put to the decision (all configurable):

| Day after the failure | What happens |
|---|---|
| 0 | `SubscriptionPastDue` (exists). E-mail 1: payment failed, pay here. Access continues. |
| 3 | Reminder event. E-mail 2. |
| 7 | Grace period ends: access stops. Reminder event. E-mail 3: access paused until paid. |
| 14 | Subscription cancelled at Mollie, no further cycles charged. E-mail 4: cancelled, debt still open. |

Debt that remains after day 14 is the consumer's (manual collection or write-off); the library
keeps it payable through `PayOutstandingAsync` indefinitely.

- Pro: no extra bank debits and fees; a broken mandate is replaced by the customer's own choice;
  the timer and the day arithmetic live in one tested place; e-mails stay with the consumer;
  defaults off, so it is a minor release.
- Con: recovery depends on the customer reacting to an e-mail, so it recovers less than an
  automatic retry in the "empty account for two days" case; needs a background job on MollieNative
  (the existing `CashierBackgroundService` is registered for Managed only).
- Effort: library about 2 days (job, options, events, `HasAccess`, tests against the fake Mollie
  client); consumer about 1 day (four e-mails, the access check through `HasAccess`).

## Recommendation

**C**, with the day table above as defaults. It answers all three questions with the least
customer-visible risk, and it keeps a later automatic retry (B) possible as an added stage if the
recovery rate turns out to be too low once real numbers exist.

Decisions needed:

1. A, B or C.
2. If C (or B): the day values (grace 7, reminders 3 and 7, cancel at 14 proposed).
3. Whether access continues during the grace period at all, or ends at the first failure as today
   (C then only adds reminders and the cancellation).
