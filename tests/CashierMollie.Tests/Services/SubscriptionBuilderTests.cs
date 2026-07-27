using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class SubscriptionBuilderTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IBillingEngine<string> _engine;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CashierMollieOptions _options;
    private readonly TestBillable _owner;

    public SubscriptionBuilderTests()
    {
        _db = TestDbContextFactory.Create();
        _engine = Substitute.For<IBillingEngine<string>>();
        _mollieClient = Substitute.For<IMollieClientService>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        _options = new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        };
        _owner = new TestBillable("user-1", "cst_test", "mdt_test");
    }

    private SubscriptionBuilder<string> CreateBuilder(
        IBillable<string>? owner = null, string name = "default", string plan = "MC-Pro-Monthly")
    {
        return new SubscriptionBuilder<string>(
            _db, _engine, _mollieClient, _dispatcher, _options,
            owner ?? _owner, name, plan);
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullOwner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SubscriptionBuilder<string>(
                _db, _engine, _mollieClient, _dispatcher, _options,
                null!, "default", "MC-Pro-Monthly"));
    }

    [Fact]
    public void Constructor_NullName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new SubscriptionBuilder<string>(
                _db, _engine, _mollieClient, _dispatcher, _options,
                _owner, null!, "MC-Pro-Monthly"));
    }

    [Fact]
    public void Constructor_EmptyName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SubscriptionBuilder<string>(
                _db, _engine, _mollieClient, _dispatcher, _options,
                _owner, "", "MC-Pro-Monthly"));
    }

    [Fact]
    public void Constructor_NullPlan_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new SubscriptionBuilder<string>(
                _db, _engine, _mollieClient, _dispatcher, _options,
                _owner, "default", null!));
    }

    [Fact]
    public void Constructor_EmptyPlan_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SubscriptionBuilder<string>(
                _db, _engine, _mollieClient, _dispatcher, _options,
                _owner, "default", ""));
    }

    // --- Fluent method return values ---

    [Fact]
    public void WithCoupon_ReturnsSameBuilder()
    {
        var builder = CreateBuilder();
        var result = builder.WithCoupon("LAUNCH10");
        Assert.Same(builder, result);
    }

    [Fact]
    public void TrialDays_ReturnsSameBuilder()
    {
        var builder = CreateBuilder();
        var result = builder.TrialDays(14);
        Assert.Same(builder, result);
    }

    [Fact]
    public void TrialDays_NegativeDays_ThrowsArgumentOutOfRangeException()
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.TrialDays(-1));
    }

    [Fact]
    public void WithProration_ReturnsSameBuilder()
    {
        var builder = CreateBuilder();
        var result = builder.WithProration();
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithMandateOnly_ReturnsSameBuilder()
    {
        var builder = CreateBuilder();
        var result = builder.WithMandateOnly();
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithMetadata_ReturnsSameBuilder()
    {
        var builder = CreateBuilder();
        var metadata = new Dictionary<string, string> { { "key", "value" } };
        var result = builder.WithMetadata(metadata);
        Assert.Same(builder, result);
    }

    // --- CreateAsync delegation ---

    [Fact]
    public async Task CreateAsync_DelegatesToEngine_WithCorrectOptions()
    {
        var expectedResult = new SubscriptionResult<string>(
            new Subscription<string>(), null, false);

        _engine.CreateSubscriptionAsync(
            Arg.Any<IBillable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SubscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var builder = CreateBuilder(name: "premium", plan: "MC-Team-Monthly");
        var result = await builder.CreateAsync();

        Assert.Same(expectedResult, result);

        await _engine.Received(1).CreateSubscriptionAsync(
            Arg.Is<IBillable<string>>(o => o.Id == "user-1"),
            Arg.Is("premium"),
            Arg.Is("MC-Team-Monthly"),
            Arg.Is<SubscriptionOptions>(opts =>
                opts.TrialDays == 0 &&
                opts.CouponCode == null &&
                !opts.MandateOnly &&
                !opts.Prorate &&
                opts.Metadata == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithAllOptions_PassesThrough()
    {
        var metadata = new Dictionary<string, string>
        {
            { "product", "acme-app" },
            { "tier", "pro" },
        };

        var expectedResult = new SubscriptionResult<string>(
            new Subscription<string>(), "https://checkout.mollie.com/xxx", true);

        _engine.CreateSubscriptionAsync(
            Arg.Any<IBillable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SubscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var builder = CreateBuilder();
        var result = await builder
            .WithCoupon("LAUNCH10")
            .TrialDays(30)
            .WithMandateOnly()
            .WithProration()
            .WithMetadata(metadata)
            .CreateAsync();

        Assert.Same(expectedResult, result);

        await _engine.Received(1).CreateSubscriptionAsync(
            Arg.Is<IBillable<string>>(o => o.Id == "user-1"),
            Arg.Is("default"),
            Arg.Is("MC-Pro-Monthly"),
            Arg.Is<SubscriptionOptions>(opts =>
                opts.TrialDays == 30 &&
                opts.CouponCode == "LAUNCH10" &&
                opts.MandateOnly &&
                opts.Prorate &&
                opts.Metadata != null &&
                opts.Metadata["product"] == "acme-app" &&
                opts.Metadata["tier"] == "pro"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DefaultOptions_HasZeroTrialDaysNoCoupon()
    {
        var expectedResult = new SubscriptionResult<string>(
            new Subscription<string>(), null, false);

        _engine.CreateSubscriptionAsync(
            Arg.Any<IBillable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SubscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var builder = CreateBuilder();
        await builder.CreateAsync();

        await _engine.Received(1).CreateSubscriptionAsync(
            Arg.Any<IBillable<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<SubscriptionOptions>(opts =>
                opts.TrialDays == 0 &&
                opts.CouponCode == null &&
                !opts.MandateOnly &&
                !opts.Prorate &&
                opts.Metadata == null),
            Arg.Any<CancellationToken>());
    }

    // --- Fluent chaining ---

    [Fact]
    public void FluentChaining_AllMethodsChainable()
    {
        var builder = CreateBuilder();
        var metadata = new Dictionary<string, string> { { "env", "test" } };

        // All fluent methods should be chainable in sequence without throwing
        var result = builder
            .WithCoupon("SAVE20")
            .TrialDays(7)
            .WithProration()
            .WithMandateOnly()
            .WithMetadata(metadata);

        Assert.IsAssignableFrom<ISubscriptionBuilder<string>>(result);
    }

    public void Dispose() => _db.Dispose();
}
