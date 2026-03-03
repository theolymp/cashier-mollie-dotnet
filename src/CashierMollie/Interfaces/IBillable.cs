namespace CashierMollie.Interfaces;

/// <summary>
/// Interface for user models that support Mollie subscriptions.
/// Implement this on your User/AppUser entity.
/// </summary>
public interface IBillable
{
    string Id { get; }
    string? MollieCustomerId { get; set; }
    string? MollieMandateId { get; set; }
    string? Email { get; }
    string? Name { get; }
}
