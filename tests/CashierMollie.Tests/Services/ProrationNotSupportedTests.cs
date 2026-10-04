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

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
