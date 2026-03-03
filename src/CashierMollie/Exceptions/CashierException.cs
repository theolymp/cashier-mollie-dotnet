namespace CashierMollie.Exceptions;

public class CashierException : Exception
{
    public CashierException(string message) : base(message) { }
    public CashierException(string message, Exception inner) : base(message, inner) { }
}
