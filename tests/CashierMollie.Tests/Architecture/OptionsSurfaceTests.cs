using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CashierMollie;
using CashierMollie.Models;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// A consumer-settable option that no code reads is a silent no-op: the value is accepted, the
/// documentation describes an effect, and nothing happens. This class has now bitten three times —
/// SwapOptions.Prorate (made to throw in eb8cd7f), seven never-dispatched events, and the two
/// settings below — so it is worth a detector rather than another round of noticing.
///
/// Unlike <see cref="PlannedEventsTests"/>, this scan MUST ignore comments. A comment mentioning an
/// option would credit it with a read site and grant a silent all-clear, which is the dangerous
/// direction; over there a comment mention produces a loud false alarm instead. The stripper
/// therefore has its own test below, against constructed input.
///
/// Scanning is by NAME, so an option name shared by two option types is credited to both. Prorate
/// is exactly that case (SwapOptions reads it to throw, SubscriptionOptions does not read it at
/// all) — acceptable, because both entrances now fail loudly either way.
///
/// Repository-reading helpers are deliberately duplicated from PlannedEventsTests rather than
/// shared; at a third copy they belong in one place.
/// </summary>
public class OptionsSurfaceTests
{
    /// <summary>
    /// Settings nothing reads, as an exact SET. Wiring one up fails
    /// <see cref="UnreadOption_IsStillUnread"/>; adding a setting nothing reads fails
    /// <see cref="EverySettableOption_IsReadSomewhere"/>.
    /// </summary>
    private static readonly string[] UnreadOptions =
        ["ApiKey", "Locale", "OrderNumberFormat"];

    private const string NotHonouredMarker = "NOT HONOURED — nothing reads this setting";

    private static readonly Type[] OptionTypes =
        [typeof(CashierMollieOptions), typeof(SwapOptions), typeof(SubscriptionOptions)];

    [Fact]
    public void CommentStripper_DropsCommentsAndKeepsCode()
    {
        // The guard that makes every zero below mean something. Without it, a comment counts as a
        // read site and an unread option passes silently.
        string stripped = StripComments(
            "var a = _options.Locale;   // _options.OrderNumberFormat\n"
            + "/* _options.Currency */\n"
            + "/// <summary>_options.ApiKey</summary>\n");

        Assert.Contains("Locale", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderNumberFormat", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Currency", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySettableOption_IsReadSomewhere()
    {
        Dictionary<string, string> sources = ReadProductionSources();

        // Known-positive: a setting whose read site is MEASURED, not assumed. The first attempt
        // here used ApiKey and failed — its only mention outside the declaration was a comment,
        // which is how the third unread option was found.
        Assert.NotEmpty(FilesReading("ProcessingInterval", sources));

        // Known-negative: an invented name must not be found.
        Assert.Empty(FilesReading("QqzzzNichtVorhanden1017", sources));

        List<string> settable = SettableOptionNames();
        Assert.NotEmpty(settable);

        foreach (string name in settable.Except(UnreadOptions))
        {
            Assert.True(
                FilesReading(name, sources).Count > 0,
                $"Option '{name}' is settable but no code reads it. Wire it up, or add it to "
                    + "UnreadOptions and say so in its XML summary — a setting that accepts a value "
                    + "and does nothing is indistinguishable from one that works.");
        }
    }

    [Fact]
    public void UnreadOption_IsStillUnread()
    {
        Dictionary<string, string> sources = ReadProductionSources();
        List<string> settable = SettableOptionNames();

        foreach (string name in UnreadOptions)
        {
            // A typo here cannot pass quietly: the name must be a real option.
            Assert.Contains(name, settable);

            List<string> found = FilesReading(name, sources);
            Assert.True(
                found.Count == 0,
                $"Option '{name}' is listed as unread but is read in: {string.Join(", ", found)}. "
                    + "Remove it from UnreadOptions and drop the NOT HONOURED line from its summary.");
        }
    }

    [Fact]
    public void UnreadOption_IsDocumentedAsNotHonoured()
    {
        XElement doc = LoadShippedXmlDocumentation();
        Assert.NotEmpty(doc.Descendants("member").ToList());

        foreach (string name in UnreadOptions)
        {
            string member = $"P:CashierMollie.CashierMollieOptions.{name}";
            XElement? element = doc.Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == member);

            Assert.True(element is not null, $"{member} missing from the shipped XML documentation");
            Assert.Contains(NotHonouredMarker, element!.Element("summary")?.Value ?? "", StringComparison.Ordinal);
        }
    }

    private static List<string> SettableOptionNames() =>
        OptionTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .Distinct()
            .ToList();

    private static List<string> FilesReading(string name, Dictionary<string, string> sources)
    {
        Regex pattern = new($@"\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        return sources
            .Where(kv => !kv.Key.Contains("Options.cs", StringComparison.Ordinal))
            .Where(kv => pattern.IsMatch(StripComments(kv.Value)))
            .Select(kv => kv.Key)
            .ToList();
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*?$", " ", RegexOptions.Multiline);
    }

    private static Dictionary<string, string> ReadProductionSources()
    {
        DirectoryInfo root = FindRepositoryRoot();
        Dictionary<string, string> sources = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root.FullName, f), File.ReadAllText);

        Assert.NotEmpty(sources);
        return sources;
    }

    private static XElement LoadShippedXmlDocumentation()
    {
        string xmlPath = Path.ChangeExtension(typeof(CashierMollieOptions).Assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), $"No XML documentation beside the assembly ({xmlPath})");
        return XElement.Load(xmlPath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CashierMollie.sln")))
        {
            dir = dir.Parent;
        }

        // Fail, never skip: dotnet test exits 0 for an all-skipped run.
        Assert.True(dir is not null, "CashierMollie.sln not found above " + AppContext.BaseDirectory);
        return dir!;
    }
}
