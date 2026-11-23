using CashierMollie.Data;
using CashierMollie.Events;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SubscriptionResponse = Mollie.Api.Models.Subscription.Response.SubscriptionResponse;
using NSubstitute.ExceptionExtensions;

namespace CashierMollie.Tests.Services;

public sealed class DunningServiceTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly CashierDbContext<string> _db;
    private readonly IMollieClientService _mollie = Substitute.For<IMollieClientService>();
    private string _mollieStatus = "active";
    private readonly ICashierEventDispatcher _dispatcher = Substitute.For<ICashierEventDispatcher>();
    private readonly List<object> _events = [];
    private readonly CashierMollieOptions _options = new() { Dunning = { Enabled = true } };

    public DunningServiceTests()
    {
        _db = TestDbContextFactory.Create(_dbName);
        _dispatcher.DispatchAsync(Arg.Do<object>(e => _events.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _mollie.GetSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var r = Substitute.For<SubscriptionResponse>();
                r.Status = _mollieStatus;
                return Task.FromResult(r);
            });
    }

    public void Dispose() => _db.Dispose();

    private DunningService<string> Service(CashierDbContext<string>? db = null) =>
        new(db ?? _db, _mollie, _dispatcher, Options.Create(_options), NullLogger<DunningService<string>>.Instance);

    private async Task<Subscription<string>> PastDue(double daysAgo, bool withDate = true, bool outstanding = true)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Name = "main", Plan = "pro", PriceNet = 10m,
            MollieCustomerId = "cst_1", MollieSubscriptionId = "sub_1",
            Status = SubscriptionStatus.PastDue, PastDueSince = withDate ? since : null,
        };
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();
        if (outstanding)
        {
            _db.Payments.Add(new Payment<string>
            {
                OwnerId = "u1", SubscriptionId = sub.Id, MolliePaymentId = "tr_failed", Status = "failed",
                Amount = 11.9m, AmountNet = 10m, Currency = "EUR", FailedAt = since,
            });
            await _db.SaveChangesAsync();
        }
        return sub;
    }

    [Fact]
    public async Task BeforeTheFirstReminderDay_NothingIsDispatched()
    {
        await PastDue(2.5);
        Assert.Equal(0, await Service().ProcessAsync());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task OnDayThree_OneReminderIsDispatched_AndOnlyOnce()
    {
        var sub = await PastDue(3.1);

        Assert.Equal(1, await Service().ProcessAsync());
        Assert.Equal(0, await Service().ProcessAsync());

        var reminder = Assert.IsType<DunningReminderDue<string>>(Assert.Single(_events));
        Assert.Equal(1, reminder.ReminderNumber);
        Assert.Equal(3, reminder.DaysPastDue);
        Assert.Equal(11.9m, reminder.OutstandingTotal);
        Assert.Equal("tr_failed", reminder.OldestOutstanding.MolliePaymentId);
        Assert.False(reminder.AccessEnded);
        Assert.Equal("u1", reminder.OwnerId);
        Assert.Equal(1, sub.DunningRemindersSent);
    }

    [Fact]
    public async Task OnDaySeven_TheSecondReminderSaysAccessEnded()
    {
        var sub = await PastDue(7.1);
        sub.DunningRemindersSent = 1;
        await _db.SaveChangesAsync();

        await Service().ProcessAsync();

        var reminder = Assert.IsType<DunningReminderDue<string>>(Assert.Single(_events));
        Assert.Equal(2, reminder.ReminderNumber);
        Assert.True(reminder.AccessEnded);
    }

    [Fact]
    public async Task OnDayFourteen_CancelsAtMollieOnce_AndDispatchesTheEvent()
    {
        var sub = await PastDue(14.1);
        sub.DunningRemindersSent = 2;
        await _db.SaveChangesAsync();

        Assert.Equal(1, await Service().ProcessAsync());
        Assert.Equal(0, await Service().ProcessAsync());

        await _mollie.Received(1).CancelSubscriptionAsync("cst_1", "sub_1", Arg.Any<CancellationToken>());
        Assert.Equal(2, _events.Count);
        Assert.IsType<SubscriptionCancelled<string>>(_events[0]);
        var cancelled = Assert.IsType<SubscriptionCancelledForNonPayment<string>>(_events[1]);
        Assert.Equal(11.9m, cancelled.OutstandingTotal);
        Assert.NotNull(sub.EndsAt);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status); // the debt stays payable
    }

    [Fact]
    public async Task WhenMollieRefusesTheCancellation_NoEndDateStays_AndNoEventIsSent()
    {
        var sub = await PastDue(14.1);
        sub.DunningRemindersSent = 2;
        await _db.SaveChangesAsync();
        _mollie.CancelSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mollie down"));

        Assert.Equal(0, await Service().ProcessAsync());

        Assert.Null(sub.EndsAt);
        Assert.Empty(_events);
        using var fresh = TestDbContextFactory.Create(_dbName);
        Assert.Null(fresh.Subscriptions.Single().EndsAt);
    }

    [Fact]
    public async Task AfterACancellationMollieTookButWasNeverSaved_TheNextPassFinishesWithoutCancellingAgain()
    {
        var sub = await PastDue(14.1);
        sub.DunningRemindersSent = 2;
        await _db.SaveChangesAsync();
        _mollieStatus = "canceled";

        Assert.Equal(1, await Service().ProcessAsync());

        await _mollie.DidNotReceiveWithAnyArgs().CancelSubscriptionAsync(default!, default!, default);
        Assert.NotNull(sub.EndsAt);
        Assert.Single(_events.OfType<SubscriptionCancelledForNonPayment<string>>());
    }

    [Fact]
    public async Task APastDueRowWithoutDate_IsBackfilled_WithoutRetroactiveReminders()
    {
        var sub = await PastDue(5, withDate: false);

        Assert.Equal(0, await Service().ProcessAsync());

        Assert.NotNull(sub.PastDueSince);
        Assert.Equal(1, sub.DunningRemindersSent); // day 3 has passed: counted, not sent
        Assert.Empty(_events);
    }

    [Fact]
    public async Task NothingOutstanding_NothingHappens()
    {
        await PastDue(20, outstanding: false);
        Assert.Equal(0, await Service().ProcessAsync());
        Assert.Empty(_events);
        await _mollie.DidNotReceiveWithAnyArgs().CancelSubscriptionAsync(default!, default!, default);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _options.Dunning.Enabled = false;
        await PastDue(20);
        Assert.Equal(0, await Service().ProcessAsync());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task TwoInstances_OnTheSameRow_DispatchOnce()
    {
        var sub = await PastDue(3.1);
        using var other = TestDbContextFactory.Create(_dbName);
        // The second instance already holds the row as it was before the first one claimed it.
        Assert.NotNull(await other.Subscriptions.FindAsync(sub.Id));

        Assert.Equal(1, await Service().ProcessAsync());
        Assert.Equal(0, await Service(other).ProcessAsync());

        Assert.Single(_events);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, null, null, true)]
    [InlineData(SubscriptionStatus.Cancelled, null, 5d, true)]
    [InlineData(SubscriptionStatus.Cancelled, null, -1d, false)]
    [InlineData(SubscriptionStatus.PastDue, 6.9d, null, true)]
    [InlineData(SubscriptionStatus.PastDue, 7.1d, null, false)]
    [InlineData(SubscriptionStatus.PastDue, 1d, -0.1d, false)]
    [InlineData(SubscriptionStatus.Pending, null, null, false)]
    public void HasAccess(string status, double? pastDueDaysAgo, double? endsInDays, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var sub = new Subscription<string>
        {
            OwnerId = "u1", Plan = "pro", Status = status,
            PastDueSince = pastDueDaysAgo is { } d ? now.AddDays(-d) : null,
            EndsAt = endsInDays is { } e ? now.AddDays(e) : null,
        };
        Assert.Equal(expected, DunningPolicy.HasAccess(sub, new DunningOptions(), now));
    }

    [Fact]
    public void HasAccess_PastDueWithoutDate_IsFailClosed()
    {
        var sub = new Subscription<string> { OwnerId = "u1", Plan = "pro", Status = SubscriptionStatus.PastDue };
        Assert.False(DunningPolicy.HasAccess(sub, new DunningOptions(), DateTimeOffset.UtcNow));
    }
}
