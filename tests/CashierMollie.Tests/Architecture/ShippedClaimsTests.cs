using System.Text.RegularExpressions;
using System.Xml.Linq;
using CashierMollie.Interfaces;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// What the package SAYS about itself, read out of the shipped XML documentation.
/// </summary>
/// <remarks>
/// The generated XML ships inside the .nupkg, so every summary is IntelliSense text for a
/// consumer. Three times now a summary has described intent rather than behaviour -- the silent
/// <c>Prorate</c>, seven events documented as dispatched, and the capability claim below -- and
/// each was found by reading rather than by a failing test.
/// <para>
/// Found on 2026-11-09 in the artifact downloaded back from nuget.org, minutes after publishing
/// 0.5.0: <c>IBillingEngine.UpdateQuantityAsync</c> shipped "Updates subscription quantity
/// (seat-based billing)" while <c>ICashierService.UpdateQuantityAsync</c>, right beside it,
/// stated the opposite and the measured truth -- no billing path reads that field. The interface
/// is public and DI-registered, so it is a reachable entrance with the wrong sign on it.
/// </para>
/// <para>
/// This class pins the CLASS of error rather than the one sentence: a capability phrase may
/// appear only where it is negated. Published versions are immutable, so 0.5.0 keeps the claim;
/// this keeps the next one from shipping it.
/// </para>
/// </remarks>
public class ShippedClaimsTests
{
    /// <summary>
    /// Phrases naming a capability this library does not have. A summary may mention one only if
    /// it also says the capability is refused -- any of the markers below.
    /// </summary>
    /// <remarks>
    /// The first version of this check demanded one fixed negation per phrase, which reported
    /// <c>ProrationSupport</c> -- the class that holds the guard's message and throw -- as a false
    /// claim. Its summary is accurate; the matcher was not. Fixed by asking what makes a mention
    /// legitimate (it names the refusal) rather than by exempting the file, which would have been
    /// an exception list growing one entry per honest sentence.
    /// </remarks>
    private static readonly string[] CapabilityClaims = ["seat-based billing", "proration"];

    private static readonly string[] RefusalMarkers = ["NOT", "not ", "Throws", "throw"];

    [Fact]
    public void NoShippedSummaryClaimsACapabilityThisLibraryLacks()
    {
        XElement doc = RepositoryDocs.ShippedXmlDocumentation(typeof(IBillingEngine<>).Assembly);
        List<XElement> members = doc.Descendants("member").ToList();
        Assert.NotEmpty(members);

        List<string> offenders = [];
        int examined = 0;

        foreach (string phrase in CapabilityClaims)
        {
            int mentions = 0;
            foreach (XElement member in members)
            {
                XElement? summary = member.Element("summary");
                if (summary is null)
                {
                    continue;
                }

                string text = Regex.Replace(string.Concat(summary.Nodes()
                    .Select(n => n is XText t ? t.Value : n.ToString())), @"\s+", " ");
                if (!text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mentions++;
                if (!RefusalMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
                {
                    offenders.Add($"{member.Attribute("name")?.Value}: \"{text.Trim()}\"");
                }
            }

            // Base set beside the zero: a phrase nobody mentions proves nothing, and a typo in
            // the phrase above would otherwise read as a clean sweep.
            Assert.True(mentions > 0,
                $"No shipped summary mentions '{phrase}' at all. Either the documentation stopped "
                    + "describing it or this phrase is misspelled -- both make the check vacuous.");
            examined += mentions;
        }

        Assert.True(offenders.Count == 0,
            "A shipped summary claims a capability this library does not have. A consumer reads "
                + "this in IntelliSense:\n  " + string.Join("\n  ", offenders));

        // Known-positive for the reader itself: the sweep really saw text.
        Assert.True(examined >= CapabilityClaims.Length);
    }
}
