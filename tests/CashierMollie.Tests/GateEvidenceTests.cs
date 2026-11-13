namespace CashierMollie.Tests;

// Deliberately red: this branch exists only to prove that main refuses a merge while
// build-and-test fails. The pull request is closed unmerged and the branch deleted afterwards.
public class GateEvidenceTests
{
    [Fact]
    public void FailsOnPurpose() => Assert.Fail("gate evidence: this pull request must not be mergeable");
}
