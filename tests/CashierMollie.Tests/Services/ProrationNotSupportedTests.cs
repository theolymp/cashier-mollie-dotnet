using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Services;

/// <summary>
/// Proration is declared across the public surface and computed nowhere: measured 2026-10-04,
/// <c>Prorate</c> had five write sites and zero read sites, and neither billing engine ever looked
/// at it. These tests pin the decision to fail loudly instead of accepting the request and
/// dropping it, because a silently inert money flag is the one state indistinguishable from a
/// working implementation -- and the XML doc on <see cref="SwapOptions.Prorate"/> promised a credit
/// that never materialised.
///
/// The guards first sat only at the two documented consumer entry points. That left the engines:
/// <c>IBillingEngine</c> is public and registered in DI, so a caller reaching one directly kept the
/// old silence, and CREATE was unguarded on both -- the flag lives on SubscriptionOptions, which is
/// the create options record. Measured before closing it: neither consumer in the fleet calls an
/// engine directly, so the new throw is inert today and the change is a completion of eb8cd7f
/// rather than a new decision.
/// </summary>
public class ProrationNotSupportedTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly CashierService<string> _cashier;
    private readonly SubscriptionBuilder<string> _builder;
    private readonly TestBillable _owner;

    public ProrationNotSupportedTests()
    {
        _db = TestDbContextFactory.Create();
        var mollieClient = Substitute.For<IMollieClientService>();
        var dispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "https://example.test/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        };
        var wrapped = Options.Create(options);
        var engine = new MollieBillingEngine<string>(_db, mollieClient, dispatcher, wrapped);

        _owner = new TestBillable("user-1", "cst_test", "mdt_test");
        _cashier = new CashierService<string>(_db, engine, mollieClient, dispatcher, wrapped,
            NullLogger<CashierService<string>>.Instance);
        _builder = new SubscriptionBuilder<string>(_db, engine, mollieClient, dispatcher, options,
            _owner, "default", "pro");
    }

    private async Task SeedActiveSubscriptionAsync()
    {
        _db.Subscriptions.Add(new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            MollieCustomerId = "cst_test",
            MollieSubscriptionId = "sub_test",
            Status = SubscriptionStatus.Active,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public void WithProration_Throws_RatherThanAcceptingARequestNothingHonours()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _builder.WithProration());
        Assert.Contains("proration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwapAsync_WithProrateTrue_Throws()
    {
        await SeedActiveSubscriptionAsync();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _cashier.SwapAsync(_owner, "default", "team", new SwapOptions { Prorate = true }));
        Assert.Contains("proration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- known-negatives: the guard must not swallow the working path ---

    [Fact]
    public async Task SwapAsync_WithProrateFalse_StillSwaps()
    {
        await SeedActiveSubscriptionAsync();

        var result = await _cashier.SwapAsync(_owner, "default", "team", new SwapOptions { Prorate = false });

        Assert.Equal("team", result.Plan);
    }

    [Fact]
    public async Task SwapAsync_WithNullOptions_StillSwaps()
    {
        await SeedActiveSubscriptionAsync();

        var result = await _cashier.SwapAsync(_owner, "default", "team", options: null);

        Assert.Equal("team", result.Plan);
    }

    // --- the engines themselves: public interface, registered in DI ---

    private MollieBillingEngine<string> NativeEngine() =>
        new(_db, Substitute.For<IMollieClientService>(), Substitute.For<ICashierEventDispatcher>(),
            Options.Create(new CashierMollieOptions { Currency = "EUR" }));

    private ManagedBillingEngine<string> ManagedEngine() =>
        new(_db, Substitute.For<IMollieClientService>(), Substitute.For<ICashierEventDispatcher>(),
            Options.Create(new CashierMollieOptions { Currency = "EUR" }),
            NullLogger<ManagedBillingEngine<string>>.Instance);

    [Fact]
    public async Task NativeEngine_CreateWithProrateTrue_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            NativeEngine().CreateSubscriptionAsync(_owner, "default", "pro",
                new SubscriptionOptions { Prorate = true }));
    }

    [Fact]
    public async Task ManagedEngine_CreateWithProrateTrue_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ManagedEngine().CreateSubscriptionAsync(_owner, "default", "pro",
                new SubscriptionOptions { Prorate = true }));
    }

    [Fact]
    public async Task NativeEngine_SwapWithProrateTrue_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            NativeEngine().SwapPlanAsync(new Subscription<string> { OwnerId = "user-1", Plan = "pro" },
                "team", new SwapOptions { Prorate = true }));
    }

    [Fact]
    public async Task ManagedEngine_SwapWithProrateTrue_Throws()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ManagedEngine().SwapPlanAsync(new Subscription<string> { OwnerId = "user-1", Plan = "pro" },
                "team", new SwapOptions { Prorate = true }));
    }

    [Fact]
    public async Task ManagedEngine_SwapWithProrateFalse_StillSwaps()
    {
        // Known-negative for the engine guard: it must not swallow the working path. Without this,
        // a guard that threw unconditionally would pass every test above.
        var sub = new Subscription<string>
        {
            OwnerId = "user-1", Name = "default", Plan = "pro", Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await ManagedEngine().SwapPlanAsync(sub, "team", new SwapOptions { Prorate = false });

        Assert.Equal("team", result.NextPlan);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
