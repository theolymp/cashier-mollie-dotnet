using CashierMollie.Interfaces;
using CashierMollie.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Middleware;

public class CashierWebhookMiddlewareTests
{
    private const string WebhookPath = "/cashier/webhook";

    private readonly IWebhookService _webhookService;
    private readonly IOptions<CashierMollieOptions> _options;

    public CashierWebhookMiddlewareTests()
    {
        _webhookService = Substitute.For<IWebhookService>();
        _options = Options.Create(new CashierMollieOptions { WebhookUrl = WebhookPath });
    }

    private DefaultHttpContext CreateHttpContext(string method, string path, string? paymentId = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_webhookService);
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };

        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentType = "application/x-www-form-urlencoded";

        if (paymentId != null)
        {
            context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["id"] = paymentId,
            });
        }
        else
        {
            context.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
        }

        return context;
    }

    [Fact]
    public async Task InvokeAsync_PostToWebhookPath_WithValidId_CallsWebhookService_Returns200()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);
        var context = CreateHttpContext("POST", WebhookPath, "tr_test123");

        await middleware.InvokeAsync(context);

        await _webhookService.Received(1).HandlePaymentAsync("tr_test123", Arg.Any<CancellationToken>());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_PostToWebhookPath_WithMissingId_Returns400()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);

        // Form with no "id" key at all
        var context = CreateHttpContext("POST", WebhookPath);

        await middleware.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        await _webhookService.DidNotReceive().HandlePaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_PostToWebhookPath_WithEmptyId_Returns400()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);

        // Form with "id" key but empty value
        var context = CreateHttpContext("POST", WebhookPath, "");

        await middleware.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        await _webhookService.DidNotReceive().HandlePaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_PostToWebhookPath_ServiceThrows_Returns500()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);
        var context = CreateHttpContext("POST", WebhookPath, "tr_error456");

        _webhookService
            .HandlePaymentAsync("tr_error456", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated infrastructure error"));

        await middleware.InvokeAsync(context);

        // Infrastructure errors return 500 so Mollie retries
        Assert.Equal(500, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_GetToWebhookPath_CallsNextMiddleware()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);
        var context = CreateHttpContext("GET", WebhookPath, "tr_test123");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        await _webhookService.DidNotReceive().HandlePaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_PostToDifferentPath_CallsNextMiddleware()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);
        var context = CreateHttpContext("POST", "/some/other/path", "tr_test123");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        await _webhookService.DidNotReceive().HandlePaymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_PathMatchingIsCaseInsensitive()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new CashierWebhookMiddleware(next, _options);

        // Use uppercase path to test case-insensitive matching
        var context = CreateHttpContext("POST", "/CASHIER/WEBHOOK", "tr_case789");

        await middleware.InvokeAsync(context);

        await _webhookService.Received(1).HandlePaymentAsync("tr_case789", Arg.Any<CancellationToken>());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(nextCalled);
    }
}
