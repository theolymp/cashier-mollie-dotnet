using System.Text.RegularExpressions;

namespace CashierMollie.Tests.Architecture;

/// <summary>
/// Money in this library is <c>decimal</c> from end to end, and no <c>double</c> or <c>float</c>
/// takes part in it.
/// </summary>
/// <remarks>
/// net11.0 changed the <c>(decimal)double</c> conversion to be exact: measured 2026-11-12,
/// <c>(decimal)(0.1 + 0.2)</c> is <c>0.3</c> on net10.0 and <c>0.3000000000000000444089209850</c> on
/// net11.0. A library that targets both would then compute different amounts from the same
/// source, depending on the consumer's framework. Today <c>src/</c> contains no floating-point type
/// at all, which is why the move to net11.0 cannot shift a charge; this test keeps that true,
/// rather than leaving it a fact somebody measured once.
/// <para>
/// It reads the source, not the assembly, because a cast inside a method body leaves no member
/// signature behind for reflection to find. The pattern looks for code shapes (casts,
/// declarations, suffixed literals, conversions) rather than the bare word, so that an English
/// "double" in a comment is not a finding. <c>Math</c> is deliberately not on the list: its
/// overloads are typed by the argument, and <c>Math.Min</c> over two decimals stays decimal.
/// </para>
/// </remarks>
public class NoFloatingPointTests
{
    private static readonly Regex FloatingPoint = new(
        @"\((double|float)\)"
            + @"|\b(double|float)\??\s+[A-Za-z_]\w*\s*[=;,)]"
            + @"|\b\d+(\.\d+)?[dDfF]\b"
            + @"|\bConvert\.To(Double|Single)\b"
            + @"|\b(double|float)\.(Parse|TryParse)\b"
            + @"|\bMathF\.",
        RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("decimal x = (decimal)(double)price;")]
    [InlineData("double rate = 0.15;")]
    [InlineData("var r = 0.15d;")]
    [InlineData("var r = Convert.ToDouble(s);")]
    [InlineData("var r = MathF.Round(x);")]
    [InlineData("float f = 1;")]
    public void ThePatternRecognisesEachShapeItLooksFor(string line)
    {
        // Known positives: without these, a pattern that matches nothing would pass the scan below.
        Assert.Matches(FloatingPoint, line);
    }

    [Fact]
    public void ThePatternIgnoresTheWordInProse()
    {
        Assert.DoesNotMatch(FloatingPoint, "// a double charge would follow; the float of the account");
    }

    [Fact]
    public void NoSourceFileUsesFloatingPoint()
    {
        string src = Path.Combine(RepositoryDocs.Root().FullName, "src");
        string[] files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // An empty set satisfies any "nothing found".
        Assert.True(files.Length > 20, $"Only {files.Length} source files under {src}; the scan would prove nothing.");

        List<string> findings = [];
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (FloatingPoint.IsMatch(lines[i]))
                {
                    findings.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            findings.Count == 0,
            "Floating point in the library source. On net11.0 (decimal)double is exact and on net10.0 it "
                + "is not, so the same code would charge different amounts per framework:\n"
                + string.Join("\n", findings));
    }
}
