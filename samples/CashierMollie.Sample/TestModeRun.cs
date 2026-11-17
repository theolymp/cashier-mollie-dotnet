using System.Globalization;
using System.Text;
using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using Microsoft.EntityFrameworkCore;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models.Mandate.Request.PaymentSpecificParameters;

namespace CashierMollie.Sample;

/// <summary>
/// The scripted run against Mollie's test mode (see TESTMODE.md). Each scenario is a customer
/// that walks through steps; <c>sync</c> feeds Mollie's payments to the webhook handler, checks
/// what the step expects, records the result as evidence and takes the next step. The only
/// human part is Mollie's own page that decides a test payment's outcome.
/// </summary>
internal static class TestModeRun
{
    private const string Sub = "main";

    /// <summary>Scenario, its step count when done, and what the first cycle payment must end as.</summary>
    private static readonly Dictionary<string, (int Done, string FirstPayment, string Plan)> Scenarios = new()
    {
        ["upgrade"] = (3, "paid", "starter"),   // credit for unused days, charged on the upgrade
        ["surplus"] = (2, "paid", "team"),      // credit larger than the new price: refunded
        ["downgrade"] = (3, "paid", "pro"),     // at period end; proves Mollie books the new amount
        ["earlyend"] = (2, "paid", "pro"),      // termination for good cause: unused days refunded
        ["pastdue"] = (2, "failed", "starter"), // lock on a failed cycle, unlock by paying it
    };

    private static readonly List<string> HumanActions = [];

    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddUserSecrets(typeof(TestModeRun).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("dotnet run -- testmode check | start <scenario...|all> | sync | report | cleanup");
            Console.WriteLine($"Scenarios: {string.Join(", ", Scenarios.Keys)}. See TESTMODE.md.");
            return 0;
        }

        string? problem = Preflight(config);
        if (problem != null)
        {
            Console.Error.WriteLine(problem);
            return 2;
        }

        string dataDirectory = config["TestMode:DataDirectory"] ?? "testmode-data";
        using var normal = Build(config, dataDirectory, cycleDays: null); // monthly, as configured
        using var shortCycle = Build(config, dataDirectory, int.Parse(config["TestMode:ShortCycleDays"] ?? "1", CultureInfo.InvariantCulture));
        await normal.EnsureDatabasesAsync();
        ServiceProvider For(Customer c) => c.Scenario == "downgrade" ? shortCycle : normal;

        string command = args.Length > 0 ? args[0] : "help";
        try
        {
            return await DispatchAsync(command, args, config, dataDirectory, normal, shortCycle, For);
        }
        catch (Mollie.Api.Client.MollieApiException ex)
        {
            Console.Error.WriteLine($"Mollie refused: {ex.Message}");
            return 3;
        }
    }

    private static async Task<int> DispatchAsync(string command, string[] args, IConfiguration config, string dataDirectory,
        ServiceProvider normal, ServiceProvider shortCycle, Func<Customer, ServiceProvider> For)
    {
        switch (command)
        {
            case "check":
                Console.WriteLine("Test key, webhook URL and data directory are fine.");
                return 0;
            case "start":
                var names = args.Length > 1 && args[1] != "all" ? args[1..] : [.. Scenarios.Keys];
                foreach (string name in names)
                {
                    if (!Scenarios.ContainsKey(name))
                    {
                        Console.Error.WriteLine($"Unknown scenario '{name}'. Known: {string.Join(", ", Scenarios.Keys)}");
                        return 2;
                    }

                    await StartAsync(name == "downgrade" ? shortCycle : normal, name, config);
                }

                await SyncAsync(normal, For);
                break;
            case "sync":
                await SyncAsync(normal, For);
                break;
            case "cleanup":
                return await CleanupAsync(normal, For);
            case "report":
                await SyncAsync(normal, For);
                return await ReportAsync(normal, dataDirectory);
            default:
                Console.WriteLine("dotnet run -- testmode check | start <scenario...|all> | sync | report | cleanup");
                Console.WriteLine($"Scenarios: {string.Join(", ", Scenarios.Keys)}");
                return command == "help" ? 0 : 2;
        }

        PrintHumanActions();
        return 0;
    }

    /// <summary>Refuses anything that could touch real money or report a false result.</summary>
    private static string? Preflight(IConfiguration config)
    {
        string key = config["CashierMollie:ApiKey"] ?? "";
        if (!key.StartsWith("test_", StringComparison.Ordinal))
            return "CashierMollie:ApiKey must be a Mollie TEST key (test_...). This run never uses a live key.";
        if (!Uri.TryCreate(config["CashierMollie:WebhookUrl"], UriKind.Absolute, out var hook)
            || hook.Scheme != Uri.UriSchemeHttps)
        {
            return "CashierMollie:WebhookUrl must be an absolute https URL Mollie can reach (see TESTMODE.md). "
                + "The run does not depend on it: it fetches the payments itself.";
        }

        return null;
    }

    /// <summary>The services, monthly as configured, or with cycles of <paramref name="cycleDays"/> days.</summary>
    private static ServiceProvider Build(IConfiguration config, string dataDirectory, int? cycleDays)
    {
        var overrides = cycleDays is { } days
            ? new Dictionary<string, string?>
            {
                ["CashierMollie:Interval"] = nameof(BillingInterval.Days),
                ["CashierMollie:CycleDays"] = days.ToString(CultureInfo.InvariantCulture),
            }
            : [];
        var effective = new ConfigurationBuilder()
            .AddConfiguration(config)
            .AddInMemoryCollection(overrides)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(effective);
        services.AddSampleBilling(effective, dataDirectory);
        return services.BuildServiceProvider();
    }

    private static async Task StartAsync(ServiceProvider services, string scenario, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SampleDbContext>();
        var cashier = sp.GetRequiredService<ICashierService<int>>();

        // A new start replaces earlier attempts of the scenario: they and their evidence stop
        // counting (they stay in the database, marked).
        foreach (var old in await db.Customers.Where(x => x.Scenario == scenario).ToListAsync())
            old.Scenario = $"{scenario} (superseded)";
        foreach (var old in await db.Evidence.Where(x => x.Scenario == scenario).ToListAsync())
            old.Superseded = true;
        await db.SaveChangesAsync();

        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        // Scenario is set only once the subscription exists, so a start that fails half-way leaves
        // no customer for sync to trip over.
        var customer = new Customer
        {
            Email = $"{scenario}-{stamp}@example.com", Name = $"Testmode {scenario} {stamp}",
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await cashier.GetOrCreateMollieCustomerAsync(customer);

        // A SEPA mandate created through the API: no checkout needed to get a customer that
        // Mollie can charge. In test mode the resulting debits wait for their outcome to be set.
        var mandate = await sp.GetRequiredService<IMandateClient>().CreateMandateAsync(customer.MollieCustomerId!,
            new SepaDirectDebitMandateRequest
            {
                Method = Mollie.Api.Models.Payment.PaymentMethod.DirectDebit,
                ConsumerName = customer.Name,
                ConsumerAccount = config["TestMode:ConsumerAccount"] ?? "NL55INGB0000000000",
            });
        customer.MollieMandateId = mandate.Id;
        await db.SaveChangesAsync();

        var result = await cashier.NewSubscription(customer, Sub, Scenarios[scenario].Plan).CreateAsync();
        customer.Scenario = scenario;
        await db.SaveChangesAsync();
        await CheckAsync(db, scenario, "created with a mandate: active at once", SubscriptionStatus.Active,
            result.Subscription.Status);
        // The library logs a failed registration and retries it later; here it must be visible at once
        // (for example Mollie refusing the short interval of 'downgrade').
        string? mollieId = result.Subscription.MollieSubscriptionId;
        await CheckAsync(db, scenario, "registered at Mollie", true, mollieId is { Length: > 0 } && mollieId != "registering");
        Console.WriteLine($"[{scenario}] customer {customer.Id} ({customer.MollieCustomerId}), mandate {mandate.Id}, plan {Scenarios[scenario].Plan}");
    }

    private static async Task SyncAsync(ServiceProvider any, Func<Customer, ServiceProvider> providerFor)
    {
        List<Customer> customers;
        using (var scope = any.CreateScope())
        {
            customers = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Customers
                .Where(c => c.Scenario != null && c.MollieCustomerId != null).AsNoTracking().ToListAsync();
        }

        var byMollieId = customers.ToDictionary(c => c.MollieCustomerId!);

        // What the webhook would have delivered: every payment of our customers, newest first.
        // HandlePaymentAsync is idempotent, so feeding a payment again changes nothing.
        using (var scope = any.CreateScope())
        {
            var list = await scope.ServiceProvider.GetRequiredService<IPaymentClient>().GetPaymentListAsync(limit: 250);
            foreach (var payment in list.Items.Where(p => p.CustomerId != null && byMollieId.ContainsKey(p.CustomerId)).Reverse())
            {
                using var inner = providerFor(byMollieId[payment.CustomerId!]).CreateScope();
                try
                {
                    await inner.ServiceProvider.GetRequiredService<IWebhookService>().HandlePaymentAsync(payment.Id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{byMollieId[payment.CustomerId!].Scenario}] webhook handling of {payment.Id} failed: {ex.Message}");
                }
            }
        }

        foreach (var customer in customers.Where(c => Scenarios.ContainsKey(c.Scenario!)))
        {
            using var scope = providerFor(customer).CreateScope();
            try
            {
                await AdvanceAsync(scope.ServiceProvider, customer.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{customer.Scenario}] step {customer.Step} failed: {ex.Message}");
            }
        }
    }

    private static async Task AdvanceAsync(IServiceProvider sp, int customerId)
    {
        var db = sp.GetRequiredService<SampleDbContext>();
        var billing = sp.GetRequiredService<CashierDbContext<int>>();
        var cashier = sp.GetRequiredService<ICashierService<int>>();
        var mollie = sp.GetRequiredService<IMollieClientService>();
        var prices = sp.GetRequiredService<IPlanPriceResolver<int>>();
        var cycle = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CashierMollieOptions>>().Value;

        var c = await db.Customers.SingleAsync(x => x.Id == customerId);
        string s = c.Scenario!;
        if (c.Step >= Scenarios[s].Done)
            return;

        var main = await cashier.GetSubscriptionAsync(c, Sub);
        if (main == null)
        {
            Console.WriteLine($"[{s}] no subscription '{Sub}' -- start failed; start the scenario again.");
            return;
        }

        var first = await billing.Payments.Where(p => p.SubscriptionId == main.Id).OrderBy(p => p.Id).FirstOrDefaultAsync();
        if (first == null)
        {
            Console.WriteLine($"[{s}] the first cycle payment is missing locally.");
            return;
        }

        // Step 0, shared: the first cycle payment, charged from the mandate, reaches its outcome.
        if (c.Step == 0)
        {
            if (!await ReachedAsync(mollie, s, first, Scenarios[s].FirstPayment))
                return;
            await CheckAsync(db, s, "first cycle charged at the plan's gross",
                Money.Gross((await prices.ResolveAsync(c.Id, Scenarios[s].Plan)).Net, first.TaxRatePercent ?? 0m), first.Amount);
            c.Step = 1;
            await db.SaveChangesAsync();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (s, c.Step)
        {
            case ("upgrade", 1):
            {
                var pro = await prices.ResolveAsync(c.Id, "pro");
                var (days, length) = BillingCycle.RemainingDays(main.CycleStartedAt!.Value, main.CycleAnchorDay, cycle, now);
                decimal credit = Money.Prorate(main.CycleValueNet ?? main.PriceNet!.Value, days, length);
                var r = await cashier.UpgradeAsync(c, "pro", "pro", [main]);
                c.Step = 2; // saved before any check: the money has moved, never repeat this
                await db.SaveChangesAsync();
                await CheckAsync(db, s, $"credit for {days} unused days", credit, r.CreditAppliedNet);
                await CheckAsync(db, s, "charged net = new price - credit", pro.Net - credit, r.ChargedNet);
                await CheckAsync(db, s, "charged gross", Money.Gross(pro.Net - credit, pro.TaxRatePercent), r.ChargedGross);
                await CheckAsync(db, s, "no incomplete Mollie step", "", string.Join("; ", r.Incomplete));
                await CheckMollieSubscriptionAsync(mollie, db, s, "replaced subscription at Mollie", c, main.MollieSubscriptionId, "canceled", null);
                await CheckMollieSubscriptionAsync(mollie, db, s, "new subscription at Mollie", c, r.Subscription.MollieSubscriptionId, "active", pro.Gross);
                if (r.Payment != null)
                {
                    var atMollie = await mollie.GetPaymentAsync(r.Payment.MolliePaymentId);
                    await CheckAsync(db, s, "upgrade payment amount at Mollie", r.ChargedGross, ParseAmount(atMollie.Amount.Value));
                }

                c.Step = 2;
                await db.SaveChangesAsync();
                break;
            }
            case ("upgrade", 2):
            {
                var pro = await cashier.GetSubscriptionAsync(c, "pro");
                var charge = await billing.Payments.Where(p => p.SubscriptionId == pro!.Id).OrderBy(p => p.Id).FirstOrDefaultAsync();
                if (charge == null || !await ReachedAsync(mollie, s, charge, "paid"))
                    return;
                await CheckAsync(db, s, "invoice data: gross = net at rate", Money.Gross(charge.AmountNet ?? 0m, charge.TaxRatePercent ?? 0m), charge.Amount);
                await CheckAsync(db, s, "invoice data: credit line recorded", true, charge.CreditNet > 0m);
                await CheckAsync(db, s, "upgraded subscription active", true, pro!.IsActive());
                c.Step = 3;
                await db.SaveChangesAsync();
                break;
            }
            case ("surplus", 1):
            {
                var starter = await prices.ResolveAsync(c.Id, "starter");
                var (days, length) = BillingCycle.RemainingDays(main.CycleStartedAt!.Value, main.CycleAnchorDay, cycle, now);
                decimal credit = Money.Prorate(main.CycleValueNet ?? main.PriceNet!.Value, days, length);
                var r = await cashier.UpgradeAsync(c, "starter", "starter", [main]);
                c.Step = 2;
                await db.SaveChangesAsync();
                await CheckAsync(db, s, $"credit for {days} unused days", credit, r.Replaced.Sum(x => x.ValueNet));
                await CheckAsync(db, s, "nothing charged", 0m, r.ChargedGross);
                decimal refundGross = Money.Gross(credit - starter.Net, main.TaxRatePercent ?? 0m);
                await CheckAsync(db, s, "surplus refunded (gross)", refundGross, r.Replaced.Sum(x => x.RefundedGross));
                await CheckAsync(db, s, "nothing left unrefunded", 0m, r.UnrefundedNet);
                await CheckRefundsAtMollieAsync(mollie, billing, db, s, first, refundGross);
                await CheckMollieSubscriptionAsync(mollie, db, s, "new subscription at Mollie", c, r.Subscription.MollieSubscriptionId, "active", starter.Gross);
                c.Step = 2;
                await db.SaveChangesAsync();
                break;
            }
            case ("downgrade", 1):
            {
                var starter = await prices.ResolveAsync(c.Id, "starter");
                var swapped = await cashier.SwapAsync(c, Sub, "starter");
                c.Step = 2;
                await db.SaveChangesAsync();
                await CheckAsync(db, s, "plan unchanged until the cycle ends", "pro", swapped.Plan);
                await CheckAsync(db, s, "next plan recorded", "starter", swapped.NextPlan ?? "");
                await CheckMollieSubscriptionAsync(mollie, db, s, "Mollie amount changed for the next charge", c, swapped.MollieSubscriptionId, "active", starter.Gross);
                c.Step = 2;
                await db.SaveChangesAsync();
                break;
            }
            case ("downgrade", 2):
            {
                var next = await billing.Payments.Where(p => p.SubscriptionId == main.Id && p.Id != first.Id).OrderBy(p => p.Id).FirstOrDefaultAsync();
                if (next == null)
                {
                    var atMollie = await mollie.GetSubscriptionAsync(c.MollieCustomerId!, main.MollieSubscriptionId!);
                    Console.WriteLine($"[{s}] waiting for Mollie's next charge (nextPaymentDate {atMollie.NextPaymentDate}).");
                    return;
                }

                if (!await ReachedAsync(mollie, s, next, "paid"))
                    return;
                var starter = await prices.ResolveAsync(c.Id, "starter");
                var after = await cashier.GetSubscriptionAsync(c, Sub);
                await CheckAsync(db, s, "Mollie booked the new amount", starter.Gross, next.Amount);
                await CheckAsync(db, s, "recorded at the new net", starter.Net, next.AmountNet ?? 0m);
                await CheckAsync(db, s, "plan switched with the paid cycle", "starter", after!.Plan);
                await CheckAsync(db, s, "no next plan left", "", after.NextPlan ?? "");
                c.Step = 3;
                await db.SaveChangesAsync();
                break;
            }
            case ("earlyend", 1):
            {
                var (days, length) = BillingCycle.RemainingDays(main.CycleStartedAt!.Value, main.CycleAnchorDay, cycle, now);
                decimal value = Money.Prorate(main.CycleValueNet ?? main.PriceNet!.Value, days, length);
                var r = await cashier.CancelImmediatelyWithRefundAsync(c, Sub);
                c.Step = 2;
                await db.SaveChangesAsync();
                await CheckAsync(db, s, "remaining whole days", days, r.RemainingDays);
                await CheckAsync(db, s, "their net value", value, r.ValueNet);
                await CheckAsync(db, s, "refunded gross", Money.Gross(value, main.TaxRatePercent ?? 0m), r.RefundedGross);
                await CheckAsync(db, s, "nothing left unrefunded", 0m, r.UnrefundedNet);
                await CheckRefundsAtMollieAsync(mollie, billing, db, s, first, r.RefundedGross);
                await CheckMollieSubscriptionAsync(mollie, db, s, "subscription at Mollie", c, main.MollieSubscriptionId, "canceled", null);
                await CheckAsync(db, s, "no longer active", false, (await cashier.GetSubscriptionAsync(c, Sub))!.IsActive());
                c.Step = 2;
                await db.SaveChangesAsync();
                break;
            }
            case ("pastdue", 1):
            {
                var settlement = await billing.Payments.Where(p => p.SettlesPaymentId == first.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync();
                if (settlement == null)
                {
                    await CheckAsync(db, s, "locked: past_due", SubscriptionStatus.PastDue, main.Status);
                    await CheckAsync(db, s, "locked: not subscribed", false, await cashier.IsSubscribedAsync(c, Sub));
                    var owed = await cashier.GetOutstandingPaymentsAsync(c, Sub);
                    await CheckAsync(db, s, "outstanding = the failed cycle", first.MolliePaymentId, string.Join(",", owed.Select(p => p.MolliePaymentId)));
                    var pay = await cashier.PayOutstandingAsync(c, Sub);
                    await CheckAsync(db, s, "checkout for the failed amount", first.Amount, pay.Amount);
                    HumanActions.Add($"[{s}] pay the outstanding amount (choose 'paid'): {pay.CheckoutUrl}");
                    return;
                }

                if (!await ReachedAsync(mollie, s, settlement, "paid"))
                    return;
                var after = await cashier.GetSubscriptionAsync(c, Sub);
                await CheckAsync(db, s, "unlocked: active", SubscriptionStatus.Active, after!.Status);
                await CheckAsync(db, s, "nothing outstanding", 0, (await cashier.GetOutstandingPaymentsAsync(c, Sub)).Count);
                var atMollie = await mollie.GetSubscriptionAsync(c.MollieCustomerId!, after.MollieSubscriptionId!);
                await CheckAsync(db, s, "Mollie charges the new mandate", settlement.MollieMandateId ?? "(none)", atMollie.MandateId ?? "(none)");
                c.Step = 2;
                await db.SaveChangesAsync();
                break;
            }
        }
    }

    /// <summary>
    /// True once the local payment has <paramref name="status"/>. A different final status is a
    /// failed check; anything else queues the human action that decides it.
    /// </summary>
    private static async Task<bool> ReachedAsync(IMollieClientService mollie, string scenario, Payment<int> payment, string status)
    {
        if (payment.Status == status)
            return true;
        if (payment.Status is "paid" or "failed" or "canceled" or "expired")
        {
            Console.WriteLine($"[{scenario}] {payment.MolliePaymentId} ended '{payment.Status}', the scenario needs '{status}': start it again.");
            return false;
        }

        var atMollie = await mollie.GetPaymentAsync(payment.MolliePaymentId);
        string? url = atMollie.Links?.ChangePaymentState?.Href ?? atMollie.Links?.Checkout?.Href;
        HumanActions.Add($"[{scenario}] set {payment.MolliePaymentId} ({payment.Amount:0.00}) to '{status}': {url ?? "(no link from Mollie yet; sync again)"}");
        return false;
    }

    private static async Task CheckMollieSubscriptionAsync(IMollieClientService mollie, SampleDbContext db, string scenario,
        string check, Customer c, string? subscriptionId, string status, decimal? gross)
    {
        if (string.IsNullOrEmpty(subscriptionId))
        {
            await CheckAsync(db, scenario, check, "registered at Mollie", "no Mollie subscription id");
            return;
        }

        var atMollie = await mollie.GetSubscriptionAsync(c.MollieCustomerId!, subscriptionId);
        await CheckAsync(db, scenario, $"{check}: status", status, atMollie.Status);
        if (gross.HasValue)
            await CheckAsync(db, scenario, $"{check}: amount", gross.Value, ParseAmount(atMollie.Amount.Value));
    }

    private static async Task CheckRefundsAtMollieAsync(IMollieClientService mollie, CashierDbContext<int> billing,
        SampleDbContext db, string scenario, Payment<int> payment, decimal expectedGross)
    {
        var refunds = await billing.Refunds.Where(r => r.PaymentId == payment.Id).ToListAsync();
        decimal atMollie = 0m;
        foreach (var refund in refunds)
        {
            var response = await mollie.GetRefundAsync(payment.MolliePaymentId, refund.MollieRefundId);
            if (response.Status is not ("failed" or "canceled"))
                atMollie += ParseAmount(response.Amount.Value);
        }

        await CheckAsync(db, scenario, "refund booked at Mollie", expectedGross, atMollie);
    }

    private static decimal ParseAmount(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static async Task CheckAsync<T>(SampleDbContext db, string scenario, string check, T expected, T actual)
    {
        string e = Format(expected), a = Format(actual);
        bool pass = EqualityComparer<T>.Default.Equals(expected, actual);
        db.Evidence.Add(new Evidence { Scenario = scenario, Check = check, Expected = e, Actual = a, Pass = pass });
        await db.SaveChangesAsync();
        Console.WriteLine($"[{scenario}] {(pass ? "PASS" : "FAIL")} {check}: expected {e}, got {a}");
    }

    private static string Format<T>(T value) => value switch
    {
        decimal d => d.ToString("0.00##", CultureInfo.InvariantCulture),
        null => "(null)",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static void PrintHumanActions()
    {
        if (HumanActions.Count == 0)
            return;
        Console.WriteLine();
        Console.WriteLine("Waiting for you (open each link, choose the outcome, then run 'sync' again):");
        foreach (string action in HumanActions)
            Console.WriteLine("  " + action);
    }

    /// <summary>Stops every subscription of the run at Mollie, so the test account stops charging.</summary>
    private static async Task<int> CleanupAsync(ServiceProvider any, Func<Customer, ServiceProvider> providerFor)
    {
        List<Customer> customers;
        using (var scope = any.CreateScope())
        {
            customers = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Customers
                .Where(c => c.Scenario != null).AsNoTracking().ToListAsync();
        }

        foreach (var customer in customers)
        {
            using var scope = providerFor(customer).CreateScope();
            var sp = scope.ServiceProvider;
            var c = await sp.GetRequiredService<SampleDbContext>().Customers.SingleAsync(x => x.Id == customer.Id);
            var cashier = sp.GetRequiredService<ICashierService<int>>();
            foreach (var sub in (await cashier.GetSubscriptionsAsync(c)).Where(s => s.IsActive() || s.Status == SubscriptionStatus.PastDue))
            {
                try
                {
                    await cashier.CancelImmediatelyAsync(c, sub.Name);
                    Console.WriteLine($"[{c.Scenario}] stopped {sub.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{c.Scenario}] could not stop {sub.Name}: {ex.Message}");
                }
            }
        }

        return 0;
    }

    /// <summary>Invoice data of every paid priced payment, plus all evidence, as Markdown.</summary>
    private static async Task<int> ReportAsync(ServiceProvider services, string dataDirectory)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<CashierDbContext<int>>();
        var mollie = scope.ServiceProvider.GetRequiredService<IMollieClientService>();

        var customers = (await db.Customers.Where(c => c.Scenario != null).ToListAsync())
            .Where(c => Scenarios.ContainsKey(c.Scenario!)).ToList();
        db.Evidence.RemoveRange(await db.Evidence.Where(e => e.Scenario == "invoice").ToListAsync());
        await db.SaveChangesAsync();
        var ids = customers.Select(c => c.Id).ToList();
        var paid = (await billing.Payments.Where(p => ids.Contains(p.OwnerId) && p.Status == "paid" && p.AmountNet != null).ToListAsync())
            .OrderBy(p => p.Id).ToList();
        foreach (var p in paid)
        {
            await CheckAsync(db, "invoice", $"{p.MolliePaymentId}: gross = net + tax", Money.Gross(p.AmountNet!.Value, p.TaxRatePercent ?? 0m), p.Amount);
            await CheckAsync(db, "invoice", $"{p.MolliePaymentId}: amount at Mollie", p.Amount,
                ParseAmount((await mollie.GetPaymentAsync(p.MolliePaymentId)).Amount.Value));
        }

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Test-mode run, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC").AppendLine();
        md.AppendLine("| Scenario | Step | Done |").AppendLine("|---|---|---|");
        foreach (var c in customers)
            md.AppendLine(CultureInfo.InvariantCulture, $"| {c.Scenario} | {c.Step} | {(c.Step >= Scenarios[c.Scenario!].Done ? "yes" : "no")} |");
        md.AppendLine().AppendLine("## Invoice data (paid priced payments)").AppendLine();
        md.AppendLine("| Payment | Net | Credit | Rate % | Gross | Refunded |").AppendLine("|---|---|---|---|---|---|");
        foreach (var p in paid)
            md.AppendLine(CultureInfo.InvariantCulture, $"| {p.MolliePaymentId} | {p.AmountNet:0.00##} | {p.CreditNet:0.00##} | {p.TaxRatePercent:0.##} | {p.Amount:0.00} | {p.AmountRefunded:0.00} |");
        md.AppendLine().AppendLine("## Evidence").AppendLine();
        md.AppendLine("| Scenario | Check | Expected | Actual | Result |").AppendLine("|---|---|---|---|---|");
        var evidence = await db.Evidence.Where(e => !e.Superseded).OrderBy(e => e.Id).ToListAsync();
        foreach (var e in evidence)
            md.AppendLine(CultureInfo.InvariantCulture, $"| {e.Scenario} | {e.Check} | {e.Expected} | {e.Actual} | {(e.Pass ? "PASS" : "**FAIL**")} |");

        string path = Path.Combine(dataDirectory, "report.md");
        await File.WriteAllTextAsync(path, md.ToString());
        int failed = evidence.Count(e => !e.Pass);
        bool allDone = customers.Count > 0 && customers.All(c => c.Step >= Scenarios[c.Scenario!].Done);
        Console.WriteLine($"Report: {Path.GetFullPath(path)} -- {evidence.Count} checks, {failed} failed, all scenarios done: {allDone}");
        PrintHumanActions();
        return failed == 0 && allDone ? 0 : 1;
    }
}
