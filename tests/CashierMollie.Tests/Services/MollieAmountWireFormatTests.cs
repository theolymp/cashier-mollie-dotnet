using System.Globalization;
using CashierMollie.Interfaces;
using CashierMollie.Services;
using Mollie.Api.Client.Abstract;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using NSubstitute;

namespace CashierMollie.Tests.Services;

/// <summary>
/// The string Mollie receives for an amount -- the one place where our four decimal places become
/// the currency's two (or none).
/// </summary>
/// <remarks>
/// Every money column holds four places (<c>decimal(18,4)</c>), so a discounted price such as
/// <c>8.4915</c> is an ordinary value, and the rounding to the wire happens inside Mollie.Api's
/// <c>Amount</c>, not in this library. Nothing pinned it: a dependency update or a framework change
/// could move a charge by a cent without a test going red. Measured 2026-11-12 with a probe over
/// Mollie.Api 4.18.0 and 4.20.0 on net10.0 and net11.0: identical in all four cells -- half away
/// from zero, invariant culture, the currency's own number of places. These cases are that
/// measurement, and they run on every target framework the test project builds for.
/// <para>
/// The midpoints are the cases that tell rounding modes apart: banker's rounding would give
/// <c>10.00</c> for <c>10.005</c> and <c>0.12</c> for <c>0.125</c>. The current culture is set to
/// de-DE because a comma decimal separator is the failure a culture-sensitive formatter produces.
/// </para>
/// </remarks>
public class MollieAmountWireFormatTests
{
    [Theory]
    [InlineData("EUR", "8.4915", "8.49")]
    [InlineData("EUR", "10.005", "10.01")]
    [InlineData("EUR", "10.015", "10.02")]
    [InlineData("EUR", "0.125", "0.13")]
    [InlineData("EUR", "0.135", "0.14")]
    [InlineData("EUR", "0.005", "0.01")]
    [InlineData("EUR", "0.004", "0.00")]
    [InlineData("EUR", "-1.005", "-1.01")]
    [InlineData("EUR", "1", "1.00")]
    [InlineData("EUR", "69.93", "69.93")]
    [InlineData("EUR", "11.3862", "11.39")]
    [InlineData("EUR", "1234567.891", "1234567.89")]
    [InlineData("JPY", "69.93", "70")]
    [InlineData("JPY", "10.5", "11")]
    public async Task TheChargedAmountReachesMollieRoundedHalfAwayFromZero(
        string currency, string amount, string wire)
    {
        // decimal cannot be an attribute argument, and a double literal would be the very
        // conversion net11 changed; the string is parsed invariantly instead.
        decimal value = decimal.Parse(amount, CultureInfo.InvariantCulture);

        IPaymentClient paymentClient = Substitute.For<IPaymentClient>();
        paymentClient.CreatePaymentAsync(Arg.Any<PaymentRequest>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<PaymentResponse>());
        IMollieClientService sut = new MollieClientService(
            Substitute.For<ICustomerClient>(), paymentClient, Substitute.For<ISubscriptionClient>(),
            Substitute.For<IMandateClient>(), Substitute.For<IRefundClient>());

        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            await sut.CreateFirstPaymentAsync(
                "cst_test", value, currency, "Test", "https://redirect", "https://webhook");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        await paymentClient.Received(1).CreatePaymentAsync(
            Arg.Is<PaymentRequest>(r => r.Amount.Currency == currency && r.Amount.Value == wire),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
