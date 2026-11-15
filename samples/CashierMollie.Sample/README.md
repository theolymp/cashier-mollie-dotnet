# CashierMollie sample

A minimal ASP.NET Core API that sells three plans on the `MollieNative` engine. It shows each call
an application makes, in [`Program.cs`](Program.cs). It also contains the scripted run against
Mollie's test mode, described in [`TESTMODE.md`](TESTMODE.md).

> **Demo only.** No endpoint is authenticated: the customer id comes from the route. The app listens
> on `localhost` and is not meant to be deployed.

## Run it

```bash
cd samples/CashierMollie.Sample
dotnet user-secrets set CashierMollie:ApiKey test_...      # a Mollie test key
dotnet user-secrets set CashierMollie:WebhookUrl https://<public host>/webhooks/mollie
dotnet run                                                 # http://localhost:5080
```

Mollie reports payment status changes by calling `WebhookUrl`, and it cannot reach `localhost`.
For the web app, put a tunnel in front of the one path `/webhooks/mollie`. Without one,
subscriptions created through a checkout stay `pending`. The test-mode run does not have this
problem, because it fetches the payments itself.

Data is kept in two SQLite files under `sample-data/`. `sample.db` holds the application's
customers and `billing.db` holds the CashierMollie tables. Both are created on start. A real
application uses migrations instead (see the root README, "Database setup").

## Plans

The plans are in `appsettings.json` under `CashierMollie:Plans`. Each amount is net, and
`DefaultTaxRatePercent` (19) applies to all of them. Changing a price needs no code.

| Plan | Net | Gross at 19 % |
|---|---|---|
| `starter` | 4.99 | 5.94 |
| `pro` | 9.99 | 11.89 |
| `team` | 19.99 | 23.79 |

## Calls

| Request | What it does |
|---|---|
| `POST /customers` `{"email","name"}` | Creates the application's customer, which implements `IBillable<int>` |
| `POST /customers/{id}/subscriptions` `{"name","plan","trialDays"?}` | Returns a checkout URL if the customer has no mandate yet. With a mandate, the subscription is active at once |
| `GET /customers/{id}/subscriptions` | Lists plan, next plan, status and price |
| `POST /customers/{id}/subscriptions/{name}/upgrade` `{"plan","replaces":[...]}` | Upgrades now. The unused days of the replaced subscriptions are credited, and any surplus is refunded |
| `POST /customers/{id}/subscriptions/{name}/swap` `{"plan"}` | Downgrades at the end of the paid cycle |
| `POST /customers/{id}/subscriptions/{name}/cancel` | Cancels at the end of the paid cycle, with no refund |
| `POST /customers/{id}/subscriptions/{name}/end-with-refund` | Termination for good cause: ends now and refunds the unused days |
| `GET /customers/{id}/subscriptions/{name}/outstanding` | Lists the failed payments that keep a subscription `past_due` |
| `POST /customers/{id}/subscriptions/{name}/pay-outstanding` | Returns a checkout for the oldest failed amount. Paying it also renews the mandate |

The events an application reacts to are handled in `LoggingEventDispatcher`
([`SampleServices.cs`](SampleServices.cs)). `SubscriptionPastDue` means you lock access and tell the
customer. `SubscriptionPaymentRecovered` means you unlock again.
