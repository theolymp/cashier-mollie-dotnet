# Test-mode run

The unit tests mock Mollie. This run uses the real Mollie API in test mode to show that what the
library computes is also what Mollie charges, refunds and books. It is scripted: each scenario is a
customer that steps forward on every `sync`, and every check is stored as evidence.

## Scenarios

| Scenario | Plan | What it proves |
|---|---|---|
| `upgrade` | starter → pro | Upgrade with credit. The unused days are credited and the difference is charged from the mandate. The old Mollie subscription is cancelled and the new one charges the new gross. The payment records the credit as an invoice line |
| `surplus` | team → starter | The credit exceeds the new price, so nothing is charged and the surplus is refunded. The refund at Mollie equals the computed amount |
| `downgrade` | pro → starter | Downgrade at the end of the period. The plan stays `pro` until the cycle ends, while Mollie's amount already changes. **Mollie's next charge is booked at the new amount**, and the plan switches with that paid cycle |
| `earlyend` | pro | Termination for good cause: the subscription ends now, at Mollie too, and the unused whole days are refunded |
| `pastdue` | starter | Payment default. A failed cycle locks the subscription (`past_due`, not subscribed, one payment outstanding). Paying the checkout unlocks it, and Mollie charges the new mandate from then on |
| *(report)* | all | Invoice data. For every paid priced payment, gross = net at its rate, and Mollie holds the same amount |

Every scenario starts the same way. It creates a Mollie customer and a SEPA mandate through the API,
so no checkout is needed. The subscription is then created with that mandate, which makes it active
at once and charges its first cycle from the mandate.

## What you need

- A Mollie **test** API key (`test_...`). The run refuses anything else.
- `CashierMollie:WebhookUrl`: an absolute `https` URL that Mollie can reach and that answers 2xx.
  Mollie rejects `localhost`. The run does not rely on the webhook, because it fetches the payments
  of its own customers and hands each one to the webhook handler.

Pass both as user secrets or environment variables (`CashierMollie__ApiKey`,
`CashierMollie__WebhookUrl`), never in `appsettings.json`.

## Commands

```bash
cd samples/CashierMollie.Sample
dotnet run -- testmode check          # validates key and webhook URL, calls nothing
dotnet run -- testmode start all      # or: start upgrade pastdue ...
dotnet run -- testmode sync           # fetch payments, check, take the next step
dotnet run -- testmode report         # sync, then write testmode-data/report.md
dotnet run -- testmode cleanup        # afterwards: stop every subscription of the run at Mollie
```

`report` exits 0 only when every scenario is done and no check failed. Run `cleanup` when you are
finished, or the test account keeps charging. The `downgrade` subscription charges every day.

## The human part

In test mode, a recurring payment (one charged from a mandate) has no checkout. It waits until its
outcome is chosen on Mollie's `changePaymentState` page. After each `sync`, the run prints what it
is waiting for:

```
Waiting for you (open each link, choose the outcome, then run 'sync' again):
  [upgrade] set tr_... (5.94) to 'paid': https://www.mollie.com/checkout/test-mode?...
  [pastdue] set tr_... (5.94) to 'failed': https://...
```

What each scenario waits for:

| Scenario | You decide |
|---|---|
| `upgrade` | first payment **paid**, then the upgrade payment **paid** |
| `surplus`, `earlyend` | first payment **paid** |
| `downgrade` | first payment **paid**, then (a day later) Mollie's next charge **paid** |
| `pastdue` | first payment **failed**, then pay the printed checkout with **a card or another method that creates a mandate**; a method without a mandate leaves the check "Mollie charges the new mandate" failed |

If a payment ends in the wrong state, start that scenario again. Each start creates a new customer
and marks the earlier attempt and its evidence as superseded, so the earlier attempt no longer
counts in the report.

## Timing

- `upgrade`, `surplus`, `earlyend` and `pastdue` run with `CycleDays` 30 and finish within
  minutes. The credit counts whole days, and the current day counts as used, so on the day of the
  first payment 29 days are left.
- `downgrade` runs with `TestMode:ShortCycleDays` (default 1), so Mollie's next charge comes one
  day later. Run `sync` after that charge appears, set it to paid, and `sync` once more. Whether
  Mollie accepts an interval of `1 days` has not been checked against the API yet. If Mollie
  refuses it, the check "registered at Mollie" of `downgrade` fails at start (the library logs the
  refusal and would retry later). Raise `ShortCycleDays` and start `downgrade` again.

## Evidence

Every check is written to `testmode-data/sample.db` (table `Evidence`) and to `report.md`, with the
expected and actual value. A failed check does not stop the run. It is reported and left for you to
look into. A step that moves money (upgrade, refund) is saved as taken before its checks run, so a
check that throws can never repeat the charge.
