namespace CashierMollie.Exceptions;

/// <summary>
/// Exception thrown by CashierMollie when a business rule is violated
/// (e.g. resuming a subscription not on grace period, subscription not found).
/// </summary>
public class CashierException : Exception
{
    /// <summary>Creates a new CashierException with no message.</summary>
    public CashierException() : base() { }

    /// <summary>Creates a new CashierException with a message.</summary>
    public CashierException(string message) : base(message) { }

    /// <summary>Creates a new CashierException with a message and inner exception.</summary>
    public CashierException(string message, Exception inner) : base(message, inner) { }
}
