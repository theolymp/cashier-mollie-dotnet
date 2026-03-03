using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class ChargeBuilderTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _dispatcher;
    private readonly CashierMollieOptions _options;
    private readonly TestBillable _ownerWithMandate;
    private readonly TestBillable _ownerWithoutMandate;

    public ChargeBuilderTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _dispatcher = Substitute.For<ICashierEventDispatcher>();
        _options = new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        };
        _ownerWithMandate = new TestBillable("user-1", "cst_test", "mdt_test");
        _ownerWithoutMandate = new TestBillable("user-2", "cst_test2");
    }

    private static PaymentResponse CreateMockPaymentResponse(string id, string status, string? checkoutUrl = null)
    {
        var response = Substitute.For<PaymentResponse>();
        response.Id = id;
        response.Status = status;
        if (checkoutUrl != null)
        {
            var links = Substitute.For<PaymentResponseLinks>();
            var checkoutLink = Substitute.For<Mollie.Api.Models.Url.UrlLink>();
            checkoutLink.Href = checkoutUrl;
            links.Checkout = checkoutLink;
            response.Links = links;
        }
        return response;
    }

    [Fact]
    public async Task CreateAsync_WithMandate_CreatesDirectPayment()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_direct", "pending"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 9.99m);

        var result = await builder.CreateAsync();

        Assert.NotNull(result.Payment);
        Assert.Equal("tr_direct", result.Payment.MolliePaymentId);
        Assert.Null(result.CheckoutUrl);
        Assert.False(result.RequiresAction);
    }

    [Fact]
    public async Task CreateAsync_WithoutMandate_RequiresCheckout()
    {
        _mollieClient.CreateFirstPaymentAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_first", "open", "https://checkout.mollie.com/xxx"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithoutMandate, 9.99m);

        var result = await builder.CreateAsync();

        Assert.NotNull(result.Payment);
        Assert.Equal("tr_first", result.Payment.MolliePaymentId);
        Assert.Equal("https://checkout.mollie.com/xxx", result.CheckoutUrl);
        Assert.True(result.RequiresAction);
    }

    [Fact]
    public async Task CreateAsync_CreatesOrderRecord()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_order", "pending"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 15.00m);

        var result = await builder.CreateAsync();

        Assert.NotNull(result.Order);
        Assert.Equal("user-1", result.Order.OwnerId);
        Assert.Equal(15.00m, result.Order.Total);
        Assert.Equal(15.00m, result.Order.Subtotal);
        Assert.Equal(15.00m, result.Order.TotalDue);
        Assert.Equal("EUR", result.Order.Currency);
        Assert.Equal("tr_order", result.Order.MolliePaymentId);

        // Verify order persisted in DB
        var savedOrder = await _db.Orders.FindAsync(result.Order.Id);
        Assert.NotNull(savedOrder);
    }

    [Fact]
    public async Task CreateAsync_DispatchesOrderCreatedEvent()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_event", "pending"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 9.99m);

        var result = await builder.CreateAsync();

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<OrderCreated<string>>(e =>
                e.OrderId == result.Order.Id && e.OwnerId == "user-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithDescription_SetsDescription()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Is("Pro upgrade"), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_desc", "pending"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 9.99m);

        var result = await builder.WithDescription("Pro upgrade").CreateAsync();

        await _mollieClient.Received(1).CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", 9.99m, "EUR", "Pro upgrade", "/cashier/webhook",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_ThrowsOnZeroAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChargeBuilder<string>(
                _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 0m));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChargeBuilder<string>(
                _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, -5m));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOwner()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ChargeBuilder<string>(
                _db, _mollieClient, _dispatcher, _options, null!, 10m));
    }

    [Fact]
    public async Task CreateAsync_PersistsPaymentRecord()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_persist", "pending"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 12.50m);

        var result = await builder.CreateAsync();

        var savedPayment = await _db.Payments.FindAsync(result.Payment.Id);
        Assert.NotNull(savedPayment);
        Assert.Equal("tr_persist", savedPayment.MolliePaymentId);
        Assert.Equal(12.50m, savedPayment.Amount);
        Assert.Equal("EUR", savedPayment.Currency);
        Assert.Equal("user-1", savedPayment.OwnerId);
    }

    [Fact]
    public async Task CreateAsync_WithoutMandate_CreatesCustomerIfMissing()
    {
        var ownerNoCustomer = new TestBillable("user-3"); // no MollieCustomerId, no mandate
        var mockCustomer = Substitute.For<Mollie.Api.Models.Customer.Response.CustomerResponse>();
        mockCustomer.Id = "cst_new";

        _mollieClient.CreateCustomerAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(mockCustomer);

        _mollieClient.CreateFirstPaymentAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_newcust", "open", "https://checkout.mollie.com/new"));

        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, ownerNoCustomer, 5.00m);

        var result = await builder.CreateAsync();

        Assert.Equal("cst_new", ownerNoCustomer.MollieCustomerId);
        Assert.True(result.RequiresAction);
    }

    [Fact]
    public async Task WithMetadata_SetsMetadata()
    {
        _mollieClient.CreateRecurringPaymentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(CreateMockPaymentResponse("tr_meta", "pending"));

        var metadata = new Dictionary<string, string> { { "product", "lifetime-pass" } };
        var builder = new ChargeBuilder<string>(
            _db, _mollieClient, _dispatcher, _options, _ownerWithMandate, 49.99m);

        // WithMetadata should return the builder for fluent chaining
        var result = builder.WithMetadata(metadata);
        Assert.IsAssignableFrom<IChargeBuilder<string>>(result);
    }

    public void Dispose() => _db.Dispose();
}
