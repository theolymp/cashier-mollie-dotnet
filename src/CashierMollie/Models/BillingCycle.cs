namespace CashierMollie.Models;

/// <summary>
/// Where a billing cycle ends and how much of it is left, for the interval configured in
/// <see cref="CashierMollieOptions.Interval"/>. All dates are UTC calendar dates, as Mollie's are.
/// </summary>
/// <remarks>
/// A monthly cycle ends on the subscription's anchor day of a later month: the first such date
/// after the day the cycle started, or that month's last day if the month is shorter. The unused
/// value of a monthly cycle is prorated over the days of that cycle (28 to 31), not over a fixed 30.
/// </remarks>
public static class BillingCycle
{
    /// <summary>The interval sent to Mollie: <c>"1 month"</c> or <c>"{CycleDays} days"</c>.</summary>
    public static string MollieInterval(CashierMollieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Interval == BillingInterval.Month ? "1 month" : $"{options.CycleDays} days";
    }

    /// <summary>One cycle after <paramref name="from"/>: the start date of Mollie's first charge.</summary>
    public static DateTimeOffset After(DateTimeOffset from, CashierMollieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Interval == BillingInterval.Month ? AddMonth(from, from.UtcDateTime.Day) : from.AddDays(options.CycleDays);
    }

    /// <summary>
    /// When the cycle that started at <paramref name="cycleStartedAt"/> ends. For a monthly cycle,
    /// <paramref name="anchorDay"/> is the subscription's <see cref="Subscription{TKey}.CycleAnchorDay"/>;
    /// null takes the start day.
    /// </summary>
    public static DateTimeOffset End(DateTimeOffset cycleStartedAt, int? anchorDay, CashierMollieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Interval == BillingInterval.Days)
            return cycleStartedAt.AddDays(options.CycleDays);

        DateTime start = cycleStartedAt.UtcDateTime;
        int anchor = Math.Clamp(anchorDay ?? start.Day, 1, 31);
        DateTime sameMonth = OnDay(start.Year, start.Month, anchor).Add(start.TimeOfDay);
        DateTime end = sameMonth.Date > start.Date ? sameMonth : AddMonth(start, anchor);
        return new DateTimeOffset(end, TimeSpan.Zero);
    }

    /// <summary>
    /// Whole days left of the cycle and its length in days (from the start date to the end date:
    /// 28 for a first cycle from 31 January, 31 for 1 to 31 January), with the current day counted
    /// as used: the time left is truncated, never rounded up, and clamped to <c>0..length</c>.
    /// A cycle starts on the day it was due: the activation, or the day Mollie created the charge,
    /// not the day a late debit arrived.
    /// </summary>
    public static (int Remaining, int Length) RemainingDays(DateTimeOffset cycleStartedAt, int? anchorDay,
        CashierMollieOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        DateTimeOffset end = End(cycleStartedAt, anchorDay, options);
        int length = options.Interval == BillingInterval.Days
            ? options.CycleDays
            : (end.UtcDateTime.Date - cycleStartedAt.UtcDateTime.Date).Days;
        TimeSpan left = end - now;
        int remaining = left <= TimeSpan.Zero ? 0 : Math.Min(length, (int)Math.Floor(left.TotalDays));
        return (remaining, length);
    }

    private static DateTime AddMonth(DateTime from, int anchor)
    {
        DateTime next = from.AddMonths(1);
        return OnDay(next.Year, next.Month, Math.Clamp(anchor, 1, 31)).Add(from.TimeOfDay);
    }

    private static DateTimeOffset AddMonth(DateTimeOffset from, int anchor) =>
        new(AddMonth(from.UtcDateTime, anchor), TimeSpan.Zero);

    private static DateTime OnDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);
}
