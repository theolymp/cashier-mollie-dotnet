using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CashierMollie.Events;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// Keeps the "declared but never dispatched" event set honest in both directions.
///
/// Seven events are declared, unit-tested and dispatched by nothing. That is deliberate — but the
/// generated XML documentation ships inside the NuGet package, so whatever its summary claims is
/// what a consumer reads in IntelliSense. A summary saying "Dispatched when X" on an event nothing
/// dispatches is a promise the library does not keep, and the consumer's handler simply never runs.
///
/// This is the only test in the repo that reads the repository rather than constructed input, which
/// makes it the kind that rots silently: if the path lookup or the scan breaks, every "0 occurrences"
/// turns into a believable pass. Hence the known-positive and known-negative below run in the same
/// pass — a broken scan then fails loudly instead of granting an all-clear.
/// </summary>
public class PlannedEventsTests
{
    /// <summary>
    /// Events declared without any dispatch site, as an exact SET rather than a minimum.
    /// A newly declared event that nothing dispatches fails <see cref="EveryDispatchedEvent_IsNotMarkedReserved"/>;
    /// wiring one of these up without removing it here fails <see cref="EveryPlannedEvent_HasNoDispatchSite"/>.
    /// Both directions are the point: the set cannot drift unnoticed.
    /// </summary>
    private static readonly string[] PlannedEvents =
    [
        "BalanceTurnedStale",
        "MandateUpdated",
        "OrderInvoiceAvailable",
        "OrderPaymentFailedDueToInvalidMandate",
        "OrderProcessed",
        "RefundFailed",
        "RefundProcessed",
    ];

    private const string ReservedMarker = "RESERVED — no service dispatches this event";

    private static readonly Assembly LibraryAssembly = typeof(OrderPaymentPaid<>).Assembly;

    [Fact]
    public void EveryPlannedEvent_IsDocumentedAsReserved()
    {
        XElement doc = LoadShippedXmlDocumentation();
        List<string> members = doc.Descendants("member")
            .Select(m => (string?)m.Attribute("name") ?? string.Empty)
            .ToList();

        // Guard: the documentation was actually parsed. Without this, a path change makes every
        // lookup below miss and the assertions turn vacuous.
        Assert.NotEmpty(members);

        foreach (string name in PlannedEvents)
        {
            string typeMember = $"T:CashierMollie.Events.{name}`1";
            XElement? element = doc.Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == typeMember);

            Assert.True(element is not null, $"{typeMember} missing from the shipped XML documentation");

            string summary = element!.Element("summary")?.Value ?? string.Empty;
            Assert.Contains(ReservedMarker, summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryPlannedEvent_HasNoDispatchSite()
    {
        Dictionary<string, string> sources = ReadProductionSources();

        // Known-positive: a live event must be found, or the scan proves nothing.
        Assert.NotEmpty(FilesMentioning("SubscriptionCreated", sources));

        // Known-negative: an invented name must not be found, or the scan matches everything.
        Assert.Empty(FilesMentioning("QqzzzNichtVorhanden1017", sources));

        foreach (string name in PlannedEvents)
        {
            List<string> found = FilesMentioning(name, sources);
            Assert.True(
                found.Count == 0,
                $"{name} is listed as planned but appears in production code: {string.Join(", ", found)}");
        }
    }

    [Fact]
    public void EveryDispatchedEvent_IsNotMarkedReserved()
    {
        List<string> declared = LibraryAssembly.GetExportedTypes()
            .Where(t => t.Namespace == "CashierMollie.Events")
            .Select(t => t.Name.Split('`')[0])
            .Distinct()
            .ToList();

        // Guard: reflection found the events at all.
        Assert.NotEmpty(declared);

        // Guard: every name in the set is a real type, so a typo there cannot pass silently.
        foreach (string name in PlannedEvents)
        {
            Assert.Contains(name, declared);
        }

        Dictionary<string, string> sources = ReadProductionSources();

        foreach (string name in declared.Except(PlannedEvents))
        {
            List<string> found = FilesMentioning(name, sources);
            Assert.True(
                found.Count > 0,
                $"{name} is declared but dispatched nowhere. Wire it up, or add it to PlannedEvents "
                    + "and lead its XML summary with the reserved marker.");
        }
    }

    [Fact]
    public void EveryDeclaredEvent_HasOneReadmeRow_MarkedIffPlanned()
    {
        // The README is the document consumers actually read, and it is the surface my own first
        // pass missed: the XML summaries were corrected while the README table still described six
        // of the seven as if they fire. Worse, it already carried the warning on ONE of them, which
        // reads as exhaustive and certifies the rest. Pinning both directions ties the table to the
        // measured set so neither can drift alone.
        string readme = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "README.md"));
        Assert.NotEmpty(readme);

        List<string> declared = DeclaredEventNames();
        Assert.NotEmpty(declared);

        // Known-negative: an invented event must have no row, or "has a row" means nothing.
        Assert.Empty(TableRowsFor("QqzzzNichtVorhanden1017", readme));

        bool sawLiveRow = false;
        foreach (string name in declared)
        {
            List<string> rows = TableRowsFor(name, readme);
            Assert.True(
                rows.Count == 1,
                $"{name} has {rows.Count} README table rows, expected exactly one — a declared event "
                    + "with no row is undocumented, and two rows drift apart.");

            bool marked = rows[0].Contains(ReadmeNotDispatchedMarker, StringComparison.Ordinal);
            if (PlannedEvents.Contains(name))
            {
                Assert.True(marked, $"{name} is planned but its README row describes it as if it fires.");
            }
            else
            {
                Assert.False(marked, $"{name} is dispatched but its README row says it never is.");
                sawLiveRow = true;
            }
        }

        // Known-positive: at least one live event was actually checked against the unmarked branch.
        Assert.True(sawLiveRow, "No live event was examined, so the unmarked branch proved nothing.");
    }

    [Fact]
    public void InvoiceHook_IsNotInvoked_AndSaysSo()
    {
        // Lives here rather than in its own file because OrderInvoiceAvailable and
        // IInvoiceGenerator are the two ends of ONE unbuilt path: the event that would announce an
        // invoice is never dispatched, and the callback that would produce one is never called.
        // NullInvoiceGenerator used to instruct consumers to "replace in DI to enable invoice
        // generation" — an instruction someone can follow with a real sevdesk or PDF integration
        // and no way to tell it never runs.
        Dictionary<string, string> sources = ReadProductionSources();

        // Known-positive: a callback this library DOES make must be found, or the scan is blind.
        Assert.NotEmpty(FilesMentioning("DispatchAsync", sources));

        List<string> callers = FilesMentioning("GenerateAsync", sources)
            .Where(f => !f.Contains("IInvoiceGenerator", StringComparison.Ordinal)
                        && !f.Contains("NullInvoiceGenerator", StringComparison.Ordinal))
            .ToList();
        Assert.True(
            callers.Count == 0,
            $"IInvoiceGenerator.GenerateAsync is now called from {string.Join(", ", callers)}. "
                + "Remove the NOT INVOKED wording from both doc comments, drop the README caveat, "
                + "and delete this test — the seam is real now.");

        XElement doc = LoadShippedXmlDocumentation();
        foreach (string member in new[]
                 {
                     "T:CashierMollie.Interfaces.IInvoiceGenerator`1",
                     "T:CashierMollie.Services.NullInvoiceGenerator`1",
                 })
        {
            XElement? element = doc.Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == member);
            Assert.True(element is not null, $"{member} missing from the shipped XML documentation");
            Assert.Contains("NOT INVOKED BY THIS LIBRARY", element!.Value, StringComparison.Ordinal);
        }
    }

    private const string ReadmeNotDispatchedMarker = "Never dispatched in this version";

    private static List<string> DeclaredEventNames() =>
        LibraryAssembly.GetExportedTypes()
            .Where(t => t.Namespace == "CashierMollie.Events")
            .Select(t => t.Name.Split('`')[0])
            .Distinct()
            .ToList();

    private static List<string> TableRowsFor(string eventName, string readme) =>
        readme.Split('\n')
            .Where(l => l.StartsWith('|') && l.Contains($"`{eventName}<TKey>`", StringComparison.Ordinal))
            .ToList();

    private static List<string> FilesMentioning(string name, Dictionary<string, string> sources)
    {
        // Word boundaries matter: "OrderPaymentFailed" is a prefix of
        // "OrderPaymentFailedDueToInvalidMandate", so a substring match would report the planned
        // long name's occurrences as dispatch sites for the live short one.
        Regex pattern = new($@"\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        return sources.Where(kv => pattern.IsMatch(kv.Value)).Select(kv => kv.Key).ToList();
    }

    private static Dictionary<string, string> ReadProductionSources()
    {
        DirectoryInfo root = FindRepositoryRoot();
        string declarationFile = Path.Combine(
            root.FullName, "src", "CashierMollie", "Events", "CashierEvents.cs");

        Dictionary<string, string> sources = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !string.Equals(f, declarationFile, StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(root.FullName, f),
                f => File.ReadAllText(f));

        // Guard: production sources were found. An empty set would make every lookup return zero.
        Assert.NotEmpty(sources);
        return sources;
    }

    private static XElement LoadShippedXmlDocumentation()
    {
        string xmlPath = Path.ChangeExtension(LibraryAssembly.Location, ".xml");
        Assert.True(
            File.Exists(xmlPath),
            $"No XML documentation beside the assembly ({xmlPath}). It ships inside the package, so "
                + "its absence is itself a finding.");
        return XElement.Load(xmlPath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CashierMollie.sln")))
        {
            dir = dir.Parent;
        }

        // Fail, never skip: dotnet test exits 0 for an all-skipped run, so a skip here would be
        // indistinguishable from a pass.
        Assert.True(dir is not null, "CashierMollie.sln not found above " + AppContext.BaseDirectory);
        return dir!;
    }
}
