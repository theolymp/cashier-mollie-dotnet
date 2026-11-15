// CashierMollie sample: a minimal API that sells three plans on the MollieNative engine.
//
//   dotnet run                      the web app (http://localhost:5080, see README.md)
//   dotnet run -- testmode <cmd>    the scripted run against Mollie's test mode (TESTMODE.md)
//
// No authentication: every endpoint takes the customer id from the route. It listens on
// localhost only and exists to show the calls, not to be deployed.

using CashierMollie.Extensions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Sample;
using Microsoft.EntityFrameworkCore;

if (args.Length > 0 && args[0] == "testmode")
{
    return await TestModeRun.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5080");
builder.Services.AddSampleBilling(builder.Configuration, builder.Configuration["DataDirectory"] ?? "sample-data");

var app = builder.Build();
await app.Services.EnsureDatabasesAsync();

// Mollie posts payment ids to CashierMollie:WebhookPath. Only this path has to be public.
app.UseCashierWebhook();

app.MapPost("/customers", async (NewCustomer body, SampleDbContext db) =>
{
    var customer = new Customer { Email = body.Email, Name = body.Name };
    db.Customers.Add(customer);
    await db.SaveChangesAsync();
    return Results.Created($"/customers/{customer.Id}", new { customer.Id });
});

// Without a mandate the answer is a checkout URL; the subscription turns active when it is paid.
app.MapPost("/customers/{id:int}/subscriptions", async (int id, NewSubscription body, SampleDbContext db,
    ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();

    var builder = cashier.NewSubscription(customer, body.Name, body.Plan);
    if (body.TrialDays is > 0)
        builder = builder.TrialDays(body.TrialDays.Value);
    var result = await builder.CreateAsync();
    await db.SaveChangesAsync(); // CashierMollie set MollieCustomerId on the customer
    return Results.Ok(new { result.Subscription.Status, result.CheckoutUrl });
});

app.MapGet("/customers/{id:int}/subscriptions", async (int id, SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    var subscriptions = await cashier.GetSubscriptionsAsync(customer);
    return Results.Ok(subscriptions.Select(s => new
    {
        s.Name, s.Plan, s.NextPlan, s.Status, Active = s.IsActive(), s.PriceNet, s.TaxRatePercent, s.EndsAt,
    }));
});

// Upgrade now, credited: the unused days of what it replaces are offset or refunded.
app.MapPost("/customers/{id:int}/subscriptions/{name}/upgrade", async (int id, string name, Upgrade body,
    SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    var all = await cashier.GetSubscriptionsAsync(customer);
    var replaces = all.Where(s => body.Replaces.Contains(s.Name)).ToList();
    var result = await cashier.UpgradeAsync(customer, name, body.Plan, replaces);
    return Results.Ok(new
    {
        result.CreditAppliedNet, result.ChargedNet, result.ChargedGross,
        Refunded = result.Replaced.Sum(r => r.RefundedGross), result.UnrefundedNet, result.Incomplete,
    });
});

// Downgrade: takes effect when the paid cycle ends.
app.MapPost("/customers/{id:int}/subscriptions/{name}/swap", async (int id, string name, Swap body,
    SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    var subscription = await cashier.SwapAsync(customer, name, body.Plan);
    return Results.Ok(new { subscription.Plan, subscription.NextPlan });
});

// Ordinary cancellation: runs to the end of the paid cycle, no refund.
app.MapPost("/customers/{id:int}/subscriptions/{name}/cancel", async (int id, string name, SampleDbContext db,
    ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    await cashier.CancelAsync(customer, name);
    return Results.NoContent();
});

// Termination for good cause: ends now and refunds the unused whole days.
app.MapPost("/customers/{id:int}/subscriptions/{name}/end-with-refund", async (int id, string name,
    SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    EarlyEndResult result = await cashier.CancelImmediatelyWithRefundAsync(customer, name);
    return Results.Ok(result);
});

// Payment default: what is owed, and a checkout to pay it (which also renews the mandate).
app.MapGet("/customers/{id:int}/subscriptions/{name}/outstanding", async (int id, string name,
    SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    var outstanding = await cashier.GetOutstandingPaymentsAsync(customer, name);
    return Results.Ok(outstanding.Select(p => new { p.MolliePaymentId, p.Amount, p.FailedAt }));
});

app.MapPost("/customers/{id:int}/subscriptions/{name}/pay-outstanding", async (int id, string name,
    SampleDbContext db, ICashierService<int> cashier) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
        return Results.NotFound();
    var result = await cashier.PayOutstandingAsync(customer, name);
    return Results.Ok(result);
});

await app.RunAsync();
return 0;

internal sealed record NewCustomer(string Email, string Name);
internal sealed record NewSubscription(string Name, string Plan, int? TrialDays);
internal sealed record Upgrade(string Plan, string[] Replaces);
internal sealed record Swap(string Plan);
