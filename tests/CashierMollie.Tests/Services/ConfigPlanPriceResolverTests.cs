using System.Globalization;
using CashierMollie.Exceptions;
using CashierMollie.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CashierMollie.Tests.Services;

public class ConfigPlanPriceResolverTests
{
    private static ConfigPlanPriceResolver<string> Resolver(Dictionary<string, string?> values,
        CashierMollieOptions? options = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ConfigPlanPriceResolver<string>(config, Options.Create(options ?? new CashierMollieOptions()));
    }

    [Fact]
    public async Task ReadsNetAmountDescriptionAndDefaultRate()
    {
        var sut = Resolver(new()
        {
            ["CashierMollie:Plans:encore:Amount"] = "9.99",
            ["CashierMollie:Plans:encore:Description"] = "Encore Suite, monthly",
        }, new CashierMollieOptions { DefaultTaxRatePercent = 19m });

        var price = await sut.ResolveAsync("u1", "encore");

        Assert.Equal(9.99m, price.Net);
        Assert.Equal(19m, price.TaxRatePercent);
        Assert.Equal("EUR", price.Currency);
        Assert.Equal("Encore Suite, monthly", price.Description);
        Assert.Equal(11.89m, price.Gross);
    }

    [Fact]
    // A German server must not read "9.99" as 999. Measured with the culture switched, because
    // the default test culture would pass either way.
    public async Task ParsesTheAmountWithTheInvariantCulture_WhateverTheServerLocale()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var sut = Resolver(new()
            {
                ["CashierMollie:Plans:pro:Amount"] = "9.99",
                ["CashierMollie:Plans:pro:Description"] = "Pro",
            });
            Assert.Equal(9.99m, (await sut.ResolveAsync("u1", "pro")).Net);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task APlanRateOverridesTheDefault_IncludingZero()
    {
        var sut = Resolver(new()
        {
            ["CashierMollie:Plans:pro:Amount"] = "9.99",
            ["CashierMollie:Plans:pro:Description"] = "Pro",
            ["CashierMollie:Plans:pro:TaxRatePercent"] = "0",
        }, new CashierMollieOptions { DefaultTaxRatePercent = 19m });

        Assert.Equal(0m, (await sut.ResolveAsync("u1", "pro")).TaxRatePercent);
    }

    [Theory]
    [InlineData(null, "Pro")]       // no amount
    [InlineData("0", "Pro")]        // not positive
    [InlineData("-1", "Pro")]
    [InlineData("abc", "Pro")]
    [InlineData("9.99", null)]      // no description
    [InlineData("9.99", " ")]
    public async Task RefusesAPlanItCannotChargeCorrectly(string? amount, string? description)
    {
        var sut = Resolver(new()
        {
            ["CashierMollie:Plans:pro:Amount"] = amount,
            ["CashierMollie:Plans:pro:Description"] = description,
            ["CashierMollie:Plans:pro:Present"] = "1", // keeps the section alive when both are null
        });

        await Assert.ThrowsAsync<CashierException>(() => sut.ResolveAsync("u1", "pro"));
    }

    [Fact]
    public async Task AnUnknownPlanThrows_RatherThanChargingAGuess()
    {
        var sut = Resolver(new() { ["CashierMollie:Plans:pro:Amount"] = "9.99" });
        var ex = await Assert.ThrowsAsync<CashierException>(() => sut.ResolveAsync("u1", "team"));
        Assert.Contains("team", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    // Plan ids are matched exactly: "Encore" is not "encore". Case-insensitive matching would let
    // two catalogue entries that differ in case resolve to one price.
    public async Task PlanIdsAreCaseSensitive()
    {
        var sut = Resolver(new()
        {
            ["CashierMollie:Plans:encore:Amount"] = "9.99",
            ["CashierMollie:Plans:encore:Description"] = "Encore",
        });
        await Assert.ThrowsAsync<CashierException>(() => sut.ResolveAsync("u1", "Encore"));
    }
}
