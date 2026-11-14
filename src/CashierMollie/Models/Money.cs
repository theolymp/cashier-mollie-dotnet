namespace CashierMollie.Models;

/// <summary>
/// The library's money arithmetic, in one place so that the consumer's invoice can use exactly
/// the same rules as the amount Mollie charges.
/// </summary>
/// <remarks>
/// Every rounding is to the cent, half away from zero, which is the commercial rounding agreed
/// for billing and the same rule Mollie.Api applies when it serialises an amount. Decimal only:
/// a binary floating-point value never enters these calculations.
/// </remarks>
public static class Money
{
    /// <summary>Rounds to the cent, half away from zero.</summary>
    public static decimal RoundCents(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Net plus tax, rounded once to the cent: <c>round(net x (1 + rate / 100))</c>.
    /// </summary>
    /// <param name="net">The net amount.</param>
    /// <param name="taxRatePercent">The tax rate in percent, 0 to 100.</param>
    public static decimal Gross(decimal net, decimal taxRatePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(taxRatePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taxRatePercent, 100m);
        return RoundCents(net * (1m + (taxRatePercent / 100m)));
    }

    /// <summary>
    /// Whole days left in a cycle, with the current day counted as used: the remaining time is
    /// truncated, never rounded up, and the result is clamped to <c>0..cycleDays</c>.
    /// </summary>
    /// <param name="cycleStartedAt">When the paid cycle began.</param>
    /// <param name="cycleDays">Length of a cycle in days.</param>
    /// <param name="now">The moment of the calculation.</param>
    public static int RemainingWholeDays(DateTimeOffset cycleStartedAt, int cycleDays, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleDays);
        TimeSpan left = cycleStartedAt.AddDays(cycleDays) - now;
        if (left <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Min(cycleDays, (int)Math.Floor(left.TotalDays));
    }

    /// <summary>
    /// The unused value of a cycle: <c>round(net x remainingDays / cycleDays)</c>, to the cent.
    /// </summary>
    /// <param name="net">Net amount the full cycle was worth.</param>
    /// <param name="remainingDays">Whole days left, see <see cref="RemainingWholeDays"/>.</param>
    /// <param name="cycleDays">Length of a cycle in days.</param>
    public static decimal Prorate(decimal net, int remainingDays, int cycleDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleDays);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remainingDays, cycleDays);
        return RoundCents(net * remainingDays / cycleDays);
    }
}
