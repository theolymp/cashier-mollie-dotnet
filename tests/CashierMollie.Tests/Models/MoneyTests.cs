using CashierMollie.Models;

namespace CashierMollie.Tests.Models;

/// <summary>
/// The rounding matrix for every amount 0.6.0 computes. Each expected value is worked out by hand
/// in the comment beside it, not taken from the implementation.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("9.99", "19", "11.89")]    // 11.8881
    [InlineData("24.99", "19", "29.74")]   // 29.7381
    [InlineData("3.99", "19", "4.75")]     // 4.7481
    [InlineData("2.99", "19", "3.56")]     // 3.5581
    [InlineData("7.99", "19", "9.51")]     // 9.5081
    [InlineData("39.99", "19", "47.59")]   // 47.5881
    [InlineData("4.99", "19", "5.94")]     // 5.9381
    [InlineData("10", "19", "11.90")]      // 11.9 exactly
    [InlineData("0.42", "19", "0.50")]     // 0.4998
    [InlineData("0.05", "10", "0.06")]     // 0.055: the midpoint goes away from zero
    [InlineData("0.15", "10", "0.17")]     // 0.165: banker's rounding would give 0.16
    [InlineData("9.99", "21", "12.09")]    // 12.0879, an OSS destination rate
    [InlineData("9.99", "0", "9.99")]      // reverse charge
    [InlineData("18.66", "19", "22.21")]   // 22.2054, the upgrade charge in NativeBillingTests
    public void Gross_RoundsOnceToTheCentHalfAwayFromZero(string net, string rate, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            Money.Gross(D(net), D(rate)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("100.01")]
    public void Gross_RefusesARateOutsideZeroToHundred(string rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Gross(10m, D(rate)));
    }

    [Theory]
    [InlineData(0.0, 30)]     // exactly at the start: a full cycle is left
    [InlineData(0.5, 29)]     // half a day in: the current day is used
    [InlineData(5.3, 24)]     // 24.7 left, truncated
    [InlineData(10.4, 19)]    // the upgrade case in NativeBillingTests
    [InlineData(29.9, 0)]     // 0.1 left is not a day
    [InlineData(30.0, 0)]
    [InlineData(45.0, 0)]     // past the cycle: never negative
    [InlineData(-1.0, 30)]    // a start in the future: clamped to one cycle
    public void RemainingWholeDays_TruncatesAndClamps(double elapsedDays, int expected)
    {
        var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, Money.RemainingWholeDays(start, 30, start.AddDays(elapsedDays)));
    }

    [Theory]
    [InlineData("9.99", 24, "7.99")]     // 7.992
    [InlineData("24.99", 15, "12.50")]   // 12.495: midpoint, away from zero
    [InlineData("0.25", 15, "0.13")]     // 0.125: banker's rounding would give 0.12
    [InlineData("3.99", 1, "0.13")]      // 0.133
    [InlineData("3.99", 28, "3.72")]     // 3.724, the module value in the surplus test
    [InlineData("2.99", 29, "2.89")]     // 2.8903
    [InlineData("9.99", 19, "6.33")]     // 6.327, the upgrade case in NativeBillingTests
    [InlineData("9.99", 30, "9.99")]     // a full cycle is worth its price, not a cent more
    [InlineData("9.99", 0, "0")]
    public void Prorate_IsTheValueOfTheRemainingDaysToTheCent(string net, int days, string expected)
    {
        Assert.Equal(D(expected), Money.Prorate(D(net), days, 30));
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(31, 30)]
    [InlineData(1, 0)]
    public void Prorate_RefusesImpossibleDays(int days, int cycle)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Prorate(10m, days, cycle));
    }

    [Fact]
    // The sum a customer pays for a cycle can never exceed the price because of rounding: thirty
    // single days, each prorated and rounded, against the whole. Pinned for the price list.
    public void ProratedDays_NeverSumAboveTheCycleByMoreThanRounding()
    {
        foreach (decimal net in new[] { 2.99m, 3.99m, 4.99m, 7.99m, 9.99m, 24.99m, 39.99m })
        {
            decimal whole = Money.Prorate(net, 30, 30);
            Assert.Equal(net, whole);
            for (int d = 0; d <= 30; d++)
            {
                decimal part = Money.Prorate(net, d, 30);
                Assert.InRange(part, 0m, net);
            }
        }
    }

    private static decimal D(string s) =>
        decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}
