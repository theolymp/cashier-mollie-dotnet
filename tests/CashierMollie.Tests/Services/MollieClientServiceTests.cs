using CashierMollie.Interfaces;
using CashierMollie.Services;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models.Customer.Request;
using Mollie.Api.Models.Customer.Response;
using Mollie.Api.Models.Payment;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class MollieClientServiceTests
{
    private readonly ICustomerClient _customerClient = Substitute.For<ICustomerClient>();
    private readonly IPaymentClient _paymentClient = Substitute.For<IPaymentClient>();
    private readonly ISubscriptionClient _subscriptionClient = Substitute.For<ISubscriptionClient>();
    private readonly IMandateClient _mandateClient = Substitute.For<IMandateClient>();
    private readonly IMollieClientService _sut;

    public MollieClientServiceTests()
    {
        _sut = new MollieClientService(
            _customerClient, _paymentClient, _subscriptionClient, _mandateClient);
    }

    [Fact]
    public async Task CreateCustomerAsync_DelegatesToClient()
    {
        // Use NSubstitute mock to avoid required member issues
        var expected = Substitute.For<CustomerResponse>();
        expected.Id = "cst_test";
        _customerClient.CreateCustomerAsync(Arg.Any<CustomerRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.CreateCustomerAsync("Test User", "test@example.com");

        Assert.Equal("cst_test", result.Id);
    }

    [Fact]
    public async Task CreateFirstPaymentAsync_DelegatesToClient()
    {
        var expected = Substitute.For<PaymentResponse>();
        expected.Id = "tr_test";
        _paymentClient.CreatePaymentAsync(Arg.Any<PaymentRequest>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.CreateFirstPaymentAsync(
            "cst_test", 9.99m, "EUR", "Test", "https://redirect", "https://webhook");

        Assert.Equal("tr_test", result.Id);
        await _paymentClient.Received(1).CreatePaymentAsync(
            Arg.Is<PaymentRequest>(r => r.SequenceType == SequenceType.First),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRecurringPaymentAsync_DelegatesToClient()
    {
        var expected = Substitute.For<PaymentResponse>();
        expected.Id = "tr_recurring";
        _paymentClient.CreatePaymentAsync(Arg.Any<PaymentRequest>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.CreateRecurringPaymentAsync(
            "cst_test", "mdt_test", 9.99m, "EUR", "Test", "https://webhook");

        Assert.Equal("tr_recurring", result.Id);
        await _paymentClient.Received(1).CreatePaymentAsync(
            Arg.Is<PaymentRequest>(r => r.SequenceType == SequenceType.Recurring),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
