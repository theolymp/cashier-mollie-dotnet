using System.Text.Json;
using System.Text.RegularExpressions;
using CashierMollie.Data;
using CashierMollie.Interfaces;
using CashierMollie.Models;
using CashierMollie.Services;
using CashierMollie.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// The README's coupon section was wrong in three ways at once: the C# example called
/// <c>ValidateAsync</c> and <c>RedeemAsync</c> with the wrong arity and argument order, the
/// configuration was shown as a JSON array when the reader treats it as an object keyed by code,
/// and the documented fields (<c>Type</c>, <c>Value</c>, <c>MaxRedemptions</c>) are read nowhere.
/// As published, the example could not compile and the configuration could not load.
///
/// Prose cannot be compiled, so this class does the two things that can be checked: it feeds the
/// README's own JSON block through the real <see cref="ConfigCouponRepository"/>, and it executes
/// the documented call sequence against real objects while asserting the README still shows those
/// exact calls. Change the API and the file below stops compiling; change the README and the
/// signature assertions fail. Either way the two cannot drift apart silently again.
/// </summary>
public class ReadmeCouponDocsTests : IDisposable
{
    private readonly CashierDbContext<string> _db = TestDbContextFactory.Create();

    private static string Readme() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "README.md"));

    /// <summary>The fenced json block that follows the coupon-configuration paragraph.</summary>
    private static string DocumentedCouponJson()
    {
        string readme = Readme();
        int anchor = readme.IndexOf("## Coupons", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "No '## Coupons' section in README.md");

        Match block = Regex.Match(readme[anchor..], "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline);
        Assert.True(block.Success, "No json block in the Coupons section");
        return block.Groups[1].Value;
    }

    [Fact]
    public async Task TheDocumentedCouponConfiguration_Loads()
    {
        string json = DocumentedCouponJson();

        // Known-positive for the extraction itself: an empty or mis-sliced block would make every
        // assertion below vacuous, and a lookup that finds nothing looks the same as a clean miss.
        Assert.Contains("LAUNCH10", json, StringComparison.Ordinal);

        IConfiguration config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();
        var repo = new ConfigCouponRepository(config);

        Coupon? coupon = await repo.FindByCodeAsync("LAUNCH10");

        Assert.NotNull(coupon);
        Assert.Equal("percentage", coupon!.HandlerType);
        Assert.Equal(3, coupon.Times);
        Assert.Equal("10", coupon.Context["percentage"]);

        // And the documented coupon actually computes the documented discount.
        decimal discount = new PercentageDiscountHandler().CalculateDiscount(coupon, 29.00m, 1);
        Assert.Equal(2.90m, discount);

        // Known-negative: a code that is not in the block must not resolve.
        Assert.Null(await repo.FindByCodeAsync("QqzzzNichtDokumentiert"));
    }

    [Fact]
    public async Task TheDocumentedFixedCoupon_Loads()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(DocumentedCouponJson())))
            .Build();

        Coupon? coupon = await new ConfigCouponRepository(config).FindByCodeAsync("FLAT5");

        Assert.NotNull(coupon);
        Assert.Equal("fixed", coupon!.HandlerType);
        Assert.Equal("5.00", coupon.Context["amount"]);
        Assert.Equal(5.00m, new FixedDiscountHandler().CalculateDiscount(coupon, 29.00m, 1));
    }

    [Fact]
    public void TheDocumentedJson_UsesOnlyKeysTheReaderReads()
    {
        // The previous version documented Type, Value and MaxRedemptions. Nothing reads them, so a
        // reader following the README configured three fields and got a coupon with defaults.
        using JsonDocument doc = JsonDocument.Parse(DocumentedCouponJson());
        JsonElement coupons = doc.RootElement.GetProperty("CashierMollie").GetProperty("Coupons");

        Assert.Equal(JsonValueKind.Object, coupons.ValueKind);   // not an array

        string[] read = ["HandlerType", "Times", "Context"];
        foreach (JsonProperty entry in coupons.EnumerateObject())
        {
            foreach (JsonProperty field in entry.Value.EnumerateObject())
            {
                Assert.True(read.Contains(field.Name),
                    $"README documents '{field.Name}' for coupon '{entry.Name}', which "
                        + "ConfigCouponRepository never reads. Document only what is read.");
            }
        }
    }

    [Fact]
    public async Task TheDocumentedCallSequence_Runs()
    {
        // This is the README example, executed. If the API changes, this file stops compiling; the
        // assertions below then check that the README was updated to match.
        var repo = Substitute.For<ICouponRepository>();
        repo.FindByCodeAsync("LAUNCH10", Arg.Any<CancellationToken>()).Returns(new Coupon
        {
            Code = "LAUNCH10", HandlerType = "percentage", Times = 3,
            Context = { ["percentage"] = "10" },
        });
        var couponService = new CouponService<string>(_db, repo, Substitute.For<ICashierEventDispatcher>());
        var user = new TestBillable("u1", "cst_test");
        _db.Subscriptions.Add(new Subscription<string>
        {
            OwnerId = "u1", Name = "default", Plan = "pro", Status = SubscriptionStatus.Active,
        });
        await _db.SaveChangesAsync();

        Coupon coupon = await couponService.ValidateAsync("LAUNCH10", user);
        await couponService.RedeemAsync(user, "LAUNCH10", "default");
        decimal discount = couponService.GetHandler(coupon.HandlerType)
            .CalculateDiscount(coupon, amount: 29.00m, quantity: 1);

        Assert.Equal(2.90m, discount);
        Assert.Single(_db.RedeemedCoupons);

        // ... and the README shows these calls, in this shape.
        string readme = Readme();
        foreach (string call in new[]
                 {
                     "ValidateAsync(\"LAUNCH10\", user)",
                     "RedeemAsync(user, \"LAUNCH10\", \"default\")",
                     "GetHandler(coupon.HandlerType)",
                     "CalculateDiscount(coupon, amount:",
                 })
        {
            Assert.Contains(call, readme, StringComparison.Ordinal);
        }

        // Known-negative: the old, non-compiling form must be gone, not merely joined by the new.
        Assert.DoesNotContain("RedeemAsync(\"LAUNCH10\", subscription, user.Id)", readme, StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CashierMollie.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "CashierMollie.sln not found above " + AppContext.BaseDirectory);
        return dir!;
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
