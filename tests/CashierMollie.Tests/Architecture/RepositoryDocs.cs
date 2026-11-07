using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// The repository's own files, read from one place.
/// </summary>
/// <remarks>
/// <see cref="OptionsSurfaceTests"/> and <see cref="PlannedEventsTests"/> each carried a private
/// copy of the root lookup and the XML-documentation load, with a note saying they belonged
/// together at the third copy. The backlog reader below is that third copy, so here they are.
/// <para>
/// <see cref="PinnedNames"/> is the one that needed building. Both of those classes already pin a
/// set three ways -- against the code, against the shipped XML summaries and against the README
/// table -- and on 2026-11-07 both sets were nevertheless wrong in BACKLOG.md: it still called
/// <c>OrderNumberFormat</c> unread and <c>OrderProcessed</c> reserved, months after the code,
/// the summaries, the README rows and the tests had all been corrected together. Headings even
/// carried the counts ("Three", "Seven"), so the stale claim was quantified.
/// </para>
/// <para>
/// That file is not shipped, which is exactly why it was missed and why it matters: it is what a
/// session reads to decide what is still open, so a stale entry sends the next person to work on
/// something already done, or to leave standing something they believe is pinned. Prose cannot be
/// checked -- but one line of it can, if the line is a list. Hence the convention: each such entry
/// names its set on a single paragraph opening with a fixed prefix, and the test compares that
/// paragraph with the set the code pins.
/// </para>
/// </remarks>
internal static class RepositoryDocs
{
    /// <summary>
    /// The names in the one BACKLOG.md paragraph opening with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// Exactly one paragraph may carry the prefix. A second would be read past silently and the
    /// test would pin whichever came first -- a peer session lost half a repository's licence
    /// scope to that shape (a flag accepting repeats, <c>next(...)</c> taking the first), and it
    /// is the one failure here that would look like a pass.
    /// </remarks>
    internal static List<string> PinnedNames(string prefix)
    {
        string[] lines = Read("BACKLOG.md").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        List<int> starts = [];
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                starts.Add(i);
            }
        }

        Assert.True(
            starts.Count == 1,
            $"BACKLOG.md has {starts.Count} paragraphs opening with \"{prefix}\", expected exactly "
                + "one. Zero means the entry lost its pinned line; more than one means this check "
                + "would silently read only the first.");

        // The paragraph, not the line: the list wraps, and a one-line reader would pin the first
        // few names and call the rest absent.
        List<string> paragraph = [];
        for (int i = starts[0]; i < lines.Length && lines[i].Trim().Length > 0; i++)
        {
            paragraph.Add(lines[i]);
        }

        List<string> names = Regex
            .Matches(string.Join(" ", paragraph), @"`([A-Za-z][A-Za-z0-9_]*)`")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A prefix that matched a sentence carrying no names would otherwise compare an empty set
        // against an empty expectation and pass.
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>
    /// Every count the README claims for <paramref name="noun"/>, digits or spelled out.
    /// </summary>
    /// <remarks>
    /// The README ships inside the package, and it states the size of two sets the code owns:
    /// "23 domain events" and "7 entities across 7 tables", the last of those also written out as
    /// "seven tables". Those are the same kind of claim that went stale in BACKLOG.md -- a count
    /// of a set, in prose, with nothing able to fail -- except this copy reaches consumers.
    /// <para>
    /// Both spellings are read on purpose: pinning only the digits would leave the word form free
    /// to drift, and it is the form a writer reaches for at the start of a sentence. Measured
    /// against today's README, the pattern finds five claims and no false ones.
    /// </para>
    /// </remarks>
    internal static List<int> ClaimedCounts(string noun)
    {
        string[] words =
            ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
             "eleven", "twelve", "thirteen"];

        string number = @"(\d{1,3}|" + string.Join("|", words) + ")";
        Regex pattern = new(
            number + @"\s+(?:\w+\s+){0,2}?" + Regex.Escape(noun) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        List<int> counts = [];
        foreach (Match match in pattern.Matches(Read("README.md")))
        {
            string raw = match.Groups[1].Value;
            int index = Array.FindIndex(words, w => w.Equals(raw, StringComparison.OrdinalIgnoreCase));
            counts.Add(index >= 0 ? index + 1 : int.Parse(raw, CultureInfo.InvariantCulture));
        }

        // A noun the README never counts would otherwise pass every expectation vacuously.
        Assert.NotEmpty(counts);
        return counts;
    }

    internal static string Read(string relativePath)
    {
        string path = Path.Combine(Root().FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} not found at {path}");
        return File.ReadAllText(path);
    }

    internal static XElement ShippedXmlDocumentation(Assembly assembly)
    {
        string xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.True(
            File.Exists(xmlPath),
            $"No XML documentation beside the assembly ({xmlPath}). It ships inside the package, so "
                + "its absence is itself a finding.");
        return XElement.Load(xmlPath);
    }

    internal static DirectoryInfo Root()
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
