using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Testing;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

/// <summary>
/// A Managed charge is sent with an Idempotency-Key derived from its order item, so repeating it
/// (a retry after a timeout, the pass after a crash before the save) returns the first payment.
/// </summary>
public sealed class ManagedIdempotencyTests : IDisposable
{
    private readonly CashierDbContext<string> _db = TestDbContextFactory.Create();
    private readonly ICashierEventDispatcher _events = Substitute.For<ICashierEventDispatcher>();
    private readonly CashierMollieOptions _options = new()
    {
        Currency = "EUR",
        BillingEngine = BillingEngineType.Managed,
        WebhookUrl = "https://shop.example/cashier/webhook",
    };

    public void Dispose() => _db.Dispose();

    private ManagedBillingEngine<string> Engine(IMollieClientService mollie) =>
        new(_db, mollie, _events, Options.Create(_options),
            NullLogger<ManagedBillingEngine<string>>.Instance,
            new CouponService<string>(_db, Substitute.For<ICouponRepository>(), _events));

    private async Task<Subscription<string>> SeedDueAsync(string customerId, string mandateId)
    {
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", MollieCustomerId = customerId,
            Status = SubscriptionStatus.Active, CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        _db.Payments.Add(new Payment<string>
        {
            OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_first",
            MollieMandateId = mandateId, Status = "paid", Amount = 0.01m, Currency = "EUR",
        });
        _db.OrderItems.Add(new OrderItem<string>
        {
            SubscriptionId = sub.Id, OwnerId = "u1", Description = "Pro plan", Currency = "EUR",
            UnitPrice = 29m, Quantity = 1, ProcessAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await _db.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task APassAfterACrashBeforeTheSave_GetsTheSamePayment_NotASecondCharge()
    {
        var mollie = new FakeMollieClient();
        var customer = await mollie.CreateCustomerAsync("Customer");
        await SeedDueAsync(customer.Id, mollie.AddMandate(customer.Id));

        await Engine(mollie).ProcessDueItemsAsync();
        var charged = Assert.Single(mollie.Payments, p => p.SequenceType == "recurring");

        // The process died after Mollie accepted the charge: nothing of the pass was saved.
        _db.Payments.Remove(await _db.Payments.SingleAsync(p => p.MolliePaymentId == charged.Id));
        _db.Orders.RemoveRange(_db.Orders);
        var items = await _db.OrderItems.ToListAsync();
        _db.OrderItems.RemoveRange(items.Where(i => i.ProcessedAt == null));
        var item = items.Single(i => i.ProcessedAt != null);
        item.ProcessedAt = null;
        item.MolliePaymentId = null;
        item.MolliePaymentStatus = null;
        await _db.SaveChangesAsync();

        await Engine(mollie).ProcessDueItemsAsync();

        Assert.Single(mollie.Payments, p => p.SequenceType == "recurring");
        Assert.Single(_db.Payments, p => p.MolliePaymentId == charged.Id);
    }

    [Fact]
    public async Task ATimeoutIsRetriedOnce_AndTheAnswerIsRecorded()
    {
        var mollie = Substitute.For<IMollieClientService>();
        var sub = await SeedDueAsync("cst_1", "mdt_1");
        var ok = Substitute.For<PaymentResponse>();
        ok.Id = "tr_cycle";
        ok.Status = "pending";
        mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(_ => throw new HttpRequestException("timeout"), _ => Task.FromResult(ok));

        await Engine(mollie).ProcessDueItemsAsync();

        Assert.Equal(2, mollie.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMollieClientService.CreateRecurringPaymentAsync)));
        Assert.Single(_db.Payments, p => p.MolliePaymentId == "tr_cycle");
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task TheRealClientSendsTheKeyToMollie_DerivedFromTheItem()
    {
        var payments = Substitute.For<Mollie.Api.Client.Abstract.IPaymentClient>();
        var ok = Substitute.For<PaymentResponse>();
        ok.Id = "tr_cycle";
        ok.Status = "pending";
        payments.CreatePaymentAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(ok));
        var client = new MollieClientService(
            Substitute.For<Mollie.Api.Client.Abstract.ICustomerClient>(), payments,
            Substitute.For<Mollie.Api.Client.Abstract.ISubscriptionClient>(),
            Substitute.For<Mollie.Api.Client.Abstract.IMandateClient>(),
            Substitute.For<Mollie.Api.Client.Abstract.IRefundClient>());
        await SeedDueAsync("cst_1", "mdt_1");
        var item = await _db.OrderItems.SingleAsync();
        var prefix = $"cashier-item-{item.Id}-{item.CreatedAt.ToUnixTimeMilliseconds()}-";

        await Engine(client).ProcessDueItemsAsync();

        payments.Received(1).WithIdempotencyKey(Arg.Is<string>(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Length == prefix.Length + 16));
        Assert.Single(_db.Payments, p => p.MolliePaymentId == "tr_cycle");
    }

    [Fact]
    public async Task A409IsNotARefusal_TheFirstRequestMayStillCreateThePayment()
    {
        var mollie = Substitute.For<IMollieClientService>();
        var sub = await SeedDueAsync("cst_1", "mdt_1");
        mollie.CreateRecurringPaymentAsync(default!, default!, default, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new FakeMollieException("in progress", 409));

        await Engine(mollie).ProcessDueItemsAsync();

        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Empty(_db.Payments.Where(p => p.MolliePaymentId.StartsWith("refused_")));
        Assert.Single(mollie.ReceivedCalls()); // not retried at once either
    }

    [Fact]
    public async Task AChargeThatChangedIsNotAnsweredWithTheEarlierPayment()
    {
        var mollie = new FakeMollieClient();
        var customer = await mollie.CreateCustomerAsync("Customer");
        await SeedDueAsync(customer.Id, mollie.AddMandate(customer.Id));
        await Engine(mollie).ProcessDueItemsAsync();

        // Lost before the save, and the price changed before the next pass.
        var charged = mollie.Payments.Single(p => p.SequenceType == "recurring");
        _db.Payments.Remove(await _db.Payments.SingleAsync(p => p.MolliePaymentId == charged.Id));
        _db.Orders.RemoveRange(_db.Orders);
        var items = await _db.OrderItems.ToListAsync();
        _db.OrderItems.RemoveRange(items.Where(i => i.ProcessedAt == null));
        var item = items.Single(i => i.ProcessedAt != null);
        item.ProcessedAt = null;
        item.MolliePaymentId = null;
        item.MolliePaymentStatus = null;
        item.UnitPrice = 19m;
        await _db.SaveChangesAsync();

        await Engine(mollie).ProcessDueItemsAsync();

        var recorded = await _db.Payments.SingleAsync(p => p.SubscriptionId == item.SubscriptionId && p.MolliePaymentId != "tr_first");
        var atMollie = mollie.Payments.Single(p => p.Id == recorded.MolliePaymentId);
        Assert.Equal(19m, recorded.Amount);
        Assert.Equal("19.00", atMollie.Amount.Value);
    }

    [Fact]
    public async Task TheFakeHonoursTheKey_KnownNegativeForTheTestAbove()
    {
        // Without a key the fake charges twice, so the single payment above is the key's doing.
        var mollie = new FakeMollieClient();
        var customer = await mollie.CreateCustomerAsync("Customer");
        var mandate = mollie.AddMandate(customer.Id);

        await mollie.CreateRecurringPaymentAsync(customer.Id, mandate, 1m, "EUR", "x", _options.WebhookUrl);
        await mollie.CreateRecurringPaymentAsync(customer.Id, mandate, 1m, "EUR", "x", _options.WebhookUrl);

        Assert.Equal(2, mollie.Payments.Count(p => p.SequenceType == "recurring"));
    }
}
