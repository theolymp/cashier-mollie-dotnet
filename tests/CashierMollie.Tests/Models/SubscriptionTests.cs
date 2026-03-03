using CashierMollie.Models;

namespace CashierMollie.Tests.Models;

public class SubscriptionTests
{
    [Fact]
    public void IsActive_WhenActiveAndNotEnded_ReturnsTrue()
    {
        var sub = new Subscription<string> { Status = SubscriptionStatus.Active };
        Assert.True(sub.IsActive());
    }

    [Fact]
    public void IsActive_WhenEnded_ReturnsFalse()
    {
        var sub = new Subscription<string>
        {
            Status = SubscriptionStatus.Active,
            EndsAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        Assert.False(sub.IsActive());
    }

    [Fact]
    public void IsActive_WhenCancelledStatus_ReturnsFalse()
    {
        var sub = new Subscription<string> { Status = SubscriptionStatus.Cancelled };
        Assert.False(sub.IsActive());
    }

    [Fact]
    public void OnGracePeriod_WhenCancelledButNotYetEnded_ReturnsTrue()
    {
        var sub = new Subscription<string>
        {
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        Assert.True(sub.OnGracePeriod());
    }

    [Fact]
    public void OnGracePeriod_WhenEndsAtInPast_ReturnsFalse()
    {
        var sub = new Subscription<string>
        {
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        Assert.False(sub.OnGracePeriod());
    }

    [Fact]
    public void OnGracePeriod_WhenNoEndsAt_ReturnsFalse()
    {
        var sub = new Subscription<string> { Status = SubscriptionStatus.Active };
        Assert.False(sub.OnGracePeriod());
    }

    [Fact]
    public void OnTrial_WhenTrialNotExpired_ReturnsTrue()
    {
        var sub = new Subscription<string>
        {
            TrialEndsAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        Assert.True(sub.OnTrial());
    }

    [Fact]
    public void OnTrial_WhenNoTrial_ReturnsFalse()
    {
        var sub = new Subscription<string>();
        Assert.False(sub.OnTrial());
    }

    [Fact]
    public void OnTrial_WhenTrialExpired_ReturnsFalse()
    {
        var sub = new Subscription<string>
        {
            TrialEndsAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        Assert.False(sub.OnTrial());
    }

    [Fact]
    public void IsCancelled_WhenEndsAtSet_ReturnsTrue()
    {
        var sub = new Subscription<string>
        {
            EndsAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        Assert.True(sub.IsCancelled());
    }

    [Fact]
    public void IsCancelled_WhenNoEndsAt_ReturnsFalse()
    {
        var sub = new Subscription<string>();
        Assert.False(sub.IsCancelled());
    }

    [Fact]
    public void IsEnded_WhenEndsAtInPast_ReturnsTrue()
    {
        var sub = new Subscription<string>
        {
            EndsAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        Assert.True(sub.IsEnded());
    }

    [Fact]
    public void IsEnded_WhenEndsAtInFuture_ReturnsFalse()
    {
        var sub = new Subscription<string>
        {
            EndsAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        Assert.False(sub.IsEnded());
    }
}
