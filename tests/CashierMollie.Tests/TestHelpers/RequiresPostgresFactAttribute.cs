namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// A <see cref="FactAttribute"/> that skips with a stated reason when PostgreSQL is not configured.
/// </summary>
/// <remarks>
/// The reason matters: a skipped test and a passing one are indistinguishable in a summary line, so
/// the skip says why out loud, and CI asserts separately that these tests actually ran rather than
/// trusting a green summary that silently measured nothing.
/// </remarks>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (PostgresTestServer.AdminConnectionString is null)
        {
            Skip = "PostgreSQL not configured: set CASHIERMOLLIE_TEST_POSTGRES to a throwaway server.";
        }
    }
}
