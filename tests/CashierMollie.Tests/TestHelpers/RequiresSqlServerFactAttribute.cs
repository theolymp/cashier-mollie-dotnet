namespace CashierMollie.Tests.TestHelpers;

/// <summary>
/// A <see cref="FactAttribute"/> that skips with a stated reason when SQL Server is not configured.
/// </summary>
/// <remarks>
/// The reason matters: a skipped test and a passing one are indistinguishable in a summary line, so
/// the skip says why out loud, and CI asserts separately that these tests actually ran rather than
/// trusting a green summary that silently measured nothing.
/// </remarks>
public sealed class RequiresSqlServerFactAttribute : FactAttribute
{
    public RequiresSqlServerFactAttribute()
    {
        if (SqlServerTestServer.AdminConnectionString is null)
        {
            Skip = "SQL Server not configured: set CASHIERMOLLIE_TEST_SQLSERVER to a throwaway "
                + "server (include TrustServerCertificate=True for a self-signed certificate).";
        }
    }
}
