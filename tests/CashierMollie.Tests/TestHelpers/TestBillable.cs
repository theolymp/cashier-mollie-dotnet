using CashierMollie.Interfaces;

namespace CashierMollie.Tests.TestHelpers;

public class TestBillable : IBillable<string>
{
    public TestBillable(string id, string? mollieCustomerId = null, string? mollieMandateId = null)
    {
        Id = id;
        MollieCustomerId = mollieCustomerId;
        MollieMandateId = mollieMandateId;
    }

    public string Id { get; }
    public string? MollieCustomerId { get; set; }
    public string? MollieMandateId { get; set; }
    public string? Email => "test@example.com";
    public string? Name => "Test User";
}
