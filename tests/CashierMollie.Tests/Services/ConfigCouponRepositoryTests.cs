using CashierMollie.Services;
using Microsoft.Extensions.Configuration;

namespace CashierMollie.Tests.Services;

public class ConfigCouponRepositoryTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> data)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }

    private static IConfiguration BuildDefaultConfig()
    {
        return BuildConfig(new Dictionary<string, string?>
        {
            ["CashierMollie:Coupons:SAVE10:HandlerType"] = "percentage",
            ["CashierMollie:Coupons:SAVE10:Times"] = "3",
            ["CashierMollie:Coupons:SAVE10:Context:percentage"] = "10",
            ["CashierMollie:Coupons:FLAT5:HandlerType"] = "fixed",
            ["CashierMollie:Coupons:FLAT5:Times"] = "0",
            ["CashierMollie:Coupons:FLAT5:Context:amount"] = "5.00",
            ["CashierMollie:Coupons:FLAT5:Context:currency"] = "EUR",
        });
    }

    [Fact]
    public async Task FindByCodeAsync_ExistingCoupon_ReturnsCouponWithCorrectProperties()
    {
        var config = BuildDefaultConfig();
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("SAVE10");

        Assert.NotNull(coupon);
        Assert.Equal("SAVE10", coupon.Code);
        Assert.Equal("percentage", coupon.HandlerType);
        Assert.Equal(3, coupon.Times);
        Assert.Single(coupon.Context);
        Assert.Equal("10", coupon.Context["percentage"]);
    }

    [Fact]
    public async Task FindByCodeAsync_UnknownCode_ReturnsNull()
    {
        var config = BuildDefaultConfig();
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("DOESNOTEXIST");

        Assert.Null(coupon);
    }

    [Fact]
    public async Task FindByCodeAsync_IsCaseInsensitive()
    {
        var config = BuildDefaultConfig();
        var repo = new ConfigCouponRepository(config);

        var lower = await repo.FindByCodeAsync("save10");
        var mixed = await repo.FindByCodeAsync("Save10");
        var upper = await repo.FindByCodeAsync("SAVE10");

        Assert.NotNull(lower);
        Assert.NotNull(mixed);
        Assert.NotNull(upper);
        Assert.Equal("save10", lower.Code);
        Assert.Equal("Save10", mixed.Code);
        Assert.Equal("SAVE10", upper.Code);
    }

    [Fact]
    public async Task FindByCodeAsync_PercentageCoupon_ParsesContext()
    {
        var config = BuildDefaultConfig();
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("SAVE10");

        Assert.NotNull(coupon);
        Assert.Equal("percentage", coupon.HandlerType);
        Assert.True(coupon.Context.ContainsKey("percentage"));
        Assert.Equal("10", coupon.Context["percentage"]);
    }

    [Fact]
    public async Task FindByCodeAsync_FixedCoupon_ParsesContext()
    {
        var config = BuildDefaultConfig();
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("FLAT5");

        Assert.NotNull(coupon);
        Assert.Equal("fixed", coupon.HandlerType);
        Assert.Equal(0, coupon.Times);
        Assert.Equal(2, coupon.Context.Count);
        Assert.Equal("5.00", coupon.Context["amount"]);
        Assert.Equal("EUR", coupon.Context["currency"]);
    }

    [Fact]
    public async Task FindByCodeAsync_MissingHandlerType_DefaultsToPercentage()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CashierMollie:Coupons:NOHANDLER:Times"] = "1",
            ["CashierMollie:Coupons:NOHANDLER:Context:percentage"] = "15",
        });
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("NOHANDLER");

        Assert.NotNull(coupon);
        Assert.Equal("percentage", coupon.HandlerType);
    }

    [Fact]
    public async Task FindByCodeAsync_MissingTimes_DefaultsToZero()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CashierMollie:Coupons:NOTIMES:HandlerType"] = "percentage",
            ["CashierMollie:Coupons:NOTIMES:Context:percentage"] = "10",
        });
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("NOTIMES");

        Assert.NotNull(coupon);
        Assert.Equal(0, coupon.Times);
    }

    [Fact]
    public async Task FindByCodeAsync_InvalidTimes_DefaultsToZero()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CashierMollie:Coupons:BADTIMES:HandlerType"] = "percentage",
            ["CashierMollie:Coupons:BADTIMES:Times"] = "not-a-number",
            ["CashierMollie:Coupons:BADTIMES:Context:percentage"] = "10",
        });
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("BADTIMES");

        Assert.NotNull(coupon);
        Assert.Equal(0, coupon.Times);
    }

    [Fact]
    public async Task FindByCodeAsync_EmptyConfig_ReturnsNull()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var repo = new ConfigCouponRepository(config);

        var coupon = await repo.FindByCodeAsync("ANYTHING");

        Assert.Null(coupon);
    }
}
