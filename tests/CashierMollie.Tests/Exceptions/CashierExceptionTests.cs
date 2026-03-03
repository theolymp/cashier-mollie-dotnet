using CashierMollie.Exceptions;

namespace CashierMollie.Tests.Exceptions;

public class CashierExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_StoresMessage()
    {
        var message = "Subscription not found";

        var exception = new CashierException(message);

        Assert.Equal(message, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Constructor_WithMessageAndInner_StoresBoth()
    {
        var message = "Payment processing failed";
        var inner = new InvalidOperationException("Mollie API error");

        var exception = new CashierException(message, inner);

        Assert.Equal(message, exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void IsSubclassOfException()
    {
        Assert.True(typeof(CashierException).IsSubclassOf(typeof(Exception)));
    }

    [Fact]
    public void CanBeCaughtAsException()
    {
        Exception? caught = null;

        try
        {
            throw new CashierException("test error");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.IsType<CashierException>(caught);
        Assert.Equal("test error", caught.Message);
    }
}
