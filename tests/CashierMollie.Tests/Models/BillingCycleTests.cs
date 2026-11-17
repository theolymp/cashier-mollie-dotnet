using CashierMollie.Models;

namespace CashierMollie.Tests.Models;

/// <summary>
/// Monthly cycles with fixed dates: month ends, leap years, late payments. Every expectation is
/// written out by hand, not derived from the code under test.
/// </summary>
public class BillingCycleTests
{
    private static readonly CashierMollieOptions Monthly = new();
    private static readonly CashierMollieOptions TenDays = new() { Interval = BillingInterval.Days, CycleDays = 10 };

    private static DateTimeOffset Utc(int y, int m, int d, int h = 10) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDefaultIsACalendarMonth_SentAsOneMonth() =>
        Assert.Equal("1 month", BillingCycle.MollieInterval(Monthly));

    [Fact]
    public void DaysStayAvailable_ForShortTestCycles()
    {
        Assert.Equal("10 days", BillingCycle.MollieInterval(TenDays));
        Assert.Equal(Utc(2026, 1, 11), BillingCycle.After(Utc(2026, 1, 1), TenDays));
        Assert.Equal((9, 10), BillingCycle.RemainingDays(Utc(2026, 1, 1), null, TenDays, Utc(2026, 1, 1, 11)));
    }

    [Theory]
    // start            anchor  end
    [InlineData(2026, 1, 15, 15, 2026, 2, 15)]  // ordinary month
    [InlineData(2026, 1, 31, 31, 2026, 2, 28)]  // 31 Jan -> last day of February
    [InlineData(2028, 1, 31, 31, 2028, 2, 29)]  // leap year
    [InlineData(2026, 2, 28, 31, 2026, 3, 31)]  // back to the anchor after a short month
    [InlineData(2026, 4, 30, 31, 2026, 5, 31)]  // 30-day month, anchor 31
    [InlineData(2026, 12, 15, 15, 2027, 1, 15)] // year boundary
    [InlineData(2026, 1, 3, 1, 2026, 2, 1)]     // a debit due on 1 Jan, paid late on 3 Jan: still ends on 1 Feb
    [InlineData(2026, 1, 3, 20, 2026, 1, 20)]   // due 20 Dec, paid 3 Jan: the cycle ends on 20 Jan
    public void AMonthlyCycleEndsOnTheAnchorDayOfTheNextBoundary(int y, int m, int d, int anchor, int ey, int em, int ed) =>
        Assert.Equal(Utc(ey, em, ed), BillingCycle.End(Utc(y, m, d), anchor, Monthly));

    [Fact]
    public void WithoutAnAnchor_TheStartDayIsTheAnchor() =>
        Assert.Equal(Utc(2026, 3, 17), BillingCycle.End(Utc(2026, 2, 17), null, Monthly));

    [Fact]
    public void MollieStartsOneMonthLater_ClampedToTheMonth()
    {
        Assert.Equal(Utc(2026, 2, 17), BillingCycle.After(Utc(2026, 1, 17), Monthly));
        Assert.Equal(Utc(2026, 2, 28), BillingCycle.After(Utc(2026, 1, 31), Monthly));
    }

    [Theory]
    // The length is the running month's days; the current day counts as used.
    [InlineData(2026, 2, 1, 1, 2026, 2, 1, 27, 28)]    // February: 28 days, first day used
    [InlineData(2026, 1, 1, 1, 2026, 1, 1, 30, 31)]    // January: 31
    [InlineData(2026, 4, 1, 1, 2026, 4, 11, 19, 30)]   // April, ten days in
    [InlineData(2026, 1, 31, 31, 2026, 2, 27, 0, 28)]  // ends 28 Feb 10:00; on 27 Feb 10:00 one day is left, the day itself counts as used
    [InlineData(2026, 1, 31, 31, 2026, 3, 1, 0, 28)]   // past the end
    public void RemainingDaysAndLength(int y, int m, int d, int anchor, int ny, int nm, int nd, int remaining, int length) =>
        Assert.Equal((remaining, length), BillingCycle.RemainingDays(Utc(y, m, d), anchor, Monthly, Utc(ny, nm, nd, 11)));

    [Fact]
    // 9.99 net, January (31 days), upgraded on the 11th at 11:00: 20 whole days left.
    public void TheUnusedValueUsesTheDaysOfTheRunningMonth()
    {
        var (left, length) = BillingCycle.RemainingDays(Utc(2026, 1, 1), 1, Monthly, Utc(2026, 1, 11, 11));
        Assert.Equal((20, 31), (left, length));
        Assert.Equal(6.45m, Money.Prorate(9.99m, left, length)); // 9.99 x 20 / 31 = 6.4451...
    }
}
