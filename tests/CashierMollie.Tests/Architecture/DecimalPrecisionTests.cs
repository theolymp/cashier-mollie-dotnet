using CashierMollie.Data;
using CashierMollie.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// Every mapped <c>decimal</c> column carries explicit precision, so no provider gets to choose.
/// </summary>
/// <remarks>
/// <para>
/// Read off the EF model rather than off a database, which is why this runs on every machine
/// instead of only where a server is configured. The provider-specific consequence is measured in
/// <see cref="Integration.SqlServerTranslationTests"/>; this is the guard that keeps a new column
/// from slipping in without one.
/// </para>
/// <para>
/// That is the failure this class exists for. Before 0.5.0 the 13 money columns had no precision
/// and each provider picked its own, so the same value stored two different amounts depending on
/// where it ran. Setting it once fixes the 13; asserting the set keeps the fourteenth from
/// arriving unnoticed.
/// </para>
/// </remarks>
public class DecimalPrecisionTests
{
    [Fact]
    public void ReadmeEntityAndTableCounts_MatchTheModel()
    {
        // Read off the model, like the precision sweep below, rather than off the ToTable calls:
        // the question is what EF maps, not what the source says it maps.
        using var ctx = TestDbContextFactory.Create();

        List<string> tables = ctx.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => name is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList()!;

        // Known-positive for the sweep: an empty model would satisfy any claim below.
        Assert.NotEmpty(tables);
        Assert.Contains("cashier_subscriptions", tables);

        foreach (int claimed in RepositoryDocs.ClaimedCounts("tables"))
        {
            Assert.Equal(tables.Count, claimed);
        }

        // One entity per table here, which is what the README says; a split or an owned type would
        // make these two numbers diverge and should then be described rather than silently kept.
        int entities = ctx.Model.GetEntityTypes().Count();
        foreach (int claimed in RepositoryDocs.ClaimedCounts("entities"))
        {
            Assert.Equal(entities, claimed);
        }
    }

    [Fact]
    public void EveryMappedDecimalColumn_CarriesTheSameExplicitPrecision()
    {
        using var ctx = TestDbContextFactory.Create();

        var decimals = ctx.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(decimal)
                            || property.ClrType == typeof(decimal?))
            .ToList();

        // Known-positive for the sweep itself: an empty set would satisfy the loop below for any
        // expectation. 13 mapped properties, against 16 public decimal members -- Total, TaxAmount
        // and TotalWithTax on OrderItem are computed and [NotMapped]. A count of 16 here would mean
        // EF had started persisting them; anything else means a column arrived or left.
        Assert.Equal(13, decimals.Count);

        // Collected rather than asserted one by one, so a failure names the column instead of
        // printing "expected 18, actual null" about an unidentified property.
        var offenders = decimals
            .Where(property => property.GetPrecision() != CashierModelBuilderExtensions.MoneyPrecision
                            || property.GetScale() != CashierModelBuilderExtensions.MoneyScale)
            .Select(property => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}.{1} = ({2}, {3})",
                property.DeclaringType.ShortName(),
                property.Name,
                property.GetPrecision()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset",
                property.GetScale()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset"))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The constants are the ones consumers will write into a migration, so they are pinned rather
    /// than left to be read off whatever the code currently says.
    /// </summary>
    [Fact]
    public void ThePublishedPrecision_Is18And4()
    {
        Assert.Equal(18, CashierModelBuilderExtensions.MoneyPrecision);
        Assert.Equal(4, CashierModelBuilderExtensions.MoneyScale);
    }
}
