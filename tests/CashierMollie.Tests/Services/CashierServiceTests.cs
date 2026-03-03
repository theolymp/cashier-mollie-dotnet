using CashierMollie.Data;
using CashierMollie.Exceptions;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CashierMollie.Tests.Services;

public class CashierServiceTests : IDisposable
{
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollieClient;
    private readonly ICashierEventDispatcher _eventDispatcher;
    private readonly CashierService<string> _sut;
    private readonly TestBillable _owner;

    public CashierServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _mollieClient = Substitute.For<IMollieClientService>();
        _eventDispatcher = Substitute.For<ICashierEventDispatcher>();
        var options = Options.Create(new CashierMollieOptions
        {
            ApiKey = "test_xxx",
            Currency = "EUR",
            WebhookUrl = "/cashier/webhook",
            FirstPaymentRedirectUrl = "/billing/success",
        });
        _sut = new CashierService<string>(_db, _mollieClient, _eventDispatcher, options);
        _owner = new TestBillable("user-1", "cst_test", "mdt_test");
    }

    [Fact]
    public async Task CancelAsync_SetsEndsAtAndStatus()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
            CycleStartedAt = DateTimeOffset.UtcNow.AddDays(-15),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _sut.CancelAsync(_owner, "default");

        var updated = await _db.Subscriptions.FindAsync(sub.Id);
        Assert.NotNull(updated!.EndsAt);
        Assert.True(updated.OnGracePeriod());
        Assert.Equal(SubscriptionStatus.Cancelled, updated.Status);
    }

    [Fact]
    public async Task CancelImmediatelyAsync_SetsEndsAtToNow()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _sut.CancelImmediatelyAsync(_owner, "default");

        var updated = await _db.Subscriptions.FindAsync(sub.Id);
        Assert.NotNull(updated!.EndsAt);
        Assert.True(updated.EndsAt <= DateTimeOffset.UtcNow);
        Assert.True(updated.IsEnded());
    }

    [Fact]
    public async Task ResumeAsync_ClearsEndsAt_WhenOnGracePeriod()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _sut.ResumeAsync(_owner, "default");

        var updated = await _db.Subscriptions.FindAsync(sub.Id);
        Assert.Null(updated!.EndsAt);
        Assert.Equal(SubscriptionStatus.Active, updated.Status);
    }

    [Fact]
    public async Task ResumeAsync_ThrowsWhenNotOnGracePeriod()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<CashierException>(
            () => _sut.ResumeAsync(_owner, "default"));
    }

    [Fact]
    public async Task IsSubscribedAsync_ReturnsTrueForActiveSubscription()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        Assert.True(await _sut.IsSubscribedAsync(_owner, "default"));
    }

    [Fact]
    public async Task IsSubscribedAsync_ReturnsFalseWhenNoSubscription()
    {
        Assert.False(await _sut.IsSubscribedAsync(_owner, "default"));
    }

    [Fact]
    public async Task IsSubscribedAsync_ReturnsTrueOnGracePeriod()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        Assert.True(await _sut.IsSubscribedAsync(_owner, "default"));
    }

    [Fact]
    public async Task SwapAsync_UpdatesPlan()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            MollieCustomerId = "cst_test",
            MollieSubscriptionId = "sub_test",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var result = await _sut.SwapAsync(_owner, "default", "team");

        Assert.Equal("team", result.Plan);
    }

    [Fact]
    public async Task SwapAsync_DispatchesEvent()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        await _sut.SwapAsync(_owner, "default", "team");

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Any<Events.SubscriptionPlanSwapped<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsCancelledAsync_ReturnsTrueWhenCancelled()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Cancelled,
            EndsAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        Assert.True(await _sut.IsCancelledAsync(_owner, "default"));
    }

    [Fact]
    public async Task IsCancelledAsync_ReturnsFalseWhenActive()
    {
        var sub = new Subscription<string>
        {
            OwnerId = "user-1",
            Name = "default",
            Plan = "pro",
            Status = SubscriptionStatus.Active,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        Assert.False(await _sut.IsCancelledAsync(_owner, "default"));
    }

    [Fact]
    public async Task GetSubscriptionsAsync_ReturnsAllForOwner()
    {
        _db.Subscriptions.Add(new Subscription<string> { OwnerId = "user-1", Name = "default", Plan = "pro", Status = SubscriptionStatus.Active });
        _db.Subscriptions.Add(new Subscription<string> { OwnerId = "user-1", Name = "extra", Plan = "addon", Status = SubscriptionStatus.Active });
        _db.Subscriptions.Add(new Subscription<string> { OwnerId = "user-2", Name = "default", Plan = "pro", Status = SubscriptionStatus.Active });
        await _db.SaveChangesAsync();

        var subs = await _sut.GetSubscriptionsAsync(_owner);

        Assert.Equal(2, subs.Count);
    }

    public void Dispose() => _db.Dispose();
}
