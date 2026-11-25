using System.Text;
using System.Text.RegularExpressions;

namespace Melomg.Domain.Documents;

public sealed record DocumentVocabulary(
    string? PrimaryPrefix,
    IReadOnlyList<string> DetectedPrefixes,
    string NumberingStyle,
    int NumberedCount,
    int UnnumberedCount)
{
    public static DocumentVocabulary Empty { get; } = new(null, [], "none", 0, 0);

    public static DocumentVocabulary BuildFrom(
        IEnumerable<DocumentFigure> figures,
        IReadOnlyList<string>? scannedPrefixes = null) =>
        BuildFromEntries(figures.Select(f => (f.VocabPrefix, f.NormalizedNumber)), scannedPrefixes);

    public static DocumentVocabulary BuildFromEntries(
        IEnumerable<(string? Prefix, string? NormalizedNumber)> entries,
        IReadOnlyList<string>? scannedPrefixes = null)
    {
        var prefixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int numberedCount = 0;
        int unnumberedCount = 0;

        bool hasHierarchical = false;
        bool hasPrefixed = false;
        bool hasRoman = false;
        bool hasSequential = false;

        foreach (var (prefix, normalizedNumber) in entries)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                string p = prefix.Trim();
                prefixCounts[p] = prefixCounts.GetValueOrDefault(p) + 1;
            }

            if (!string.IsNullOrWhiteSpace(normalizedNumber))
            {
                numberedCount++;
                ClassifyNumberStyle(normalizedNumber, ref hasHierarchical, ref hasPrefixed, ref hasRoman, ref hasSequential);
            }
            else
            {
                unnumberedCount++;
            }
        }

        if (prefixCounts.Count == 0 && scannedPrefixes is { Count: > 0 })
        {
            foreach (var p in scannedPrefixes.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                string trimmed = p.Trim();
                prefixCounts[trimmed] = prefixCounts.GetValueOrDefault(trimmed) + 1;
            }
        }

        var orderedPrefixes = prefixCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToArray();

        string? primary = orderedPrefixes.Length > 0 ? orderedPrefixes[0] : null;

        int styleCount = (hasHierarchical ? 1 : 0) + (hasPrefixed ? 1 : 0) + (hasRoman ? 1 : 0) + (hasSequential ? 1 : 0);
        string style = numberedCount == 0
            ? "none"
            : styleCount > 1
                ? "mixed"
                : hasHierarchical
                    ? "hierarchical"
                    : hasPrefixed
                        ? "prefixed"
                        : hasRoman
                            ? "roman"
                            : "sequential";

        return new DocumentVocabulary(primary, orderedPrefixes, style, numberedCount, unnumberedCount);
    }

    private static void ClassifyNumberStyle(
        string normalizedNumber,
        ref bool hasHierarchical,
        ref bool hasPrefixed,
        ref bool hasRoman,
        ref bool hasSequential)
    {
        if (FigureNumberNormalizer.IsPureRoman(normalizedNumber))
        {
            hasRoman = true;
            return;
        }

        if ((normalizedNumber.Length >= 2 && char.IsLetter(normalizedNumber[0]) && char.IsDigit(normalizedNumber[1])) ||
            (normalizedNumber.Length >= 3 && char.IsLetter(normalizedNumber[0]) && normalizedNumber[1] == '-' && char.IsDigit(normalizedNumber[2])))
        {
            hasPrefixed = true;
            return;
        }

        if (normalizedNumber.Contains('.') || normalizedNumber.Contains('-'))
        {
            hasHierarchical = true;
            return;
        }

        hasSequential = true;
    }
}

public sealed record ParsedFigureCaption(
    string Prefix,
    string Number,
    string NormalizedNumber,
    string ShortLabel);

public static partial class FigureNumberNormalizer
{
    private static readonly HashSet<string> InlineProseStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "shows", "show", "illustrates", "illustrate", "demonstrates", "demonstrate",
        "depicts", "depict", "presents", "present", "is", "are", "was", "were",
        "can", "will", "has", "have", "compares", "compare", "summarizes", "summarize",
        "indicates", "indicate", "reveals", "reveal", "highlights", "highlight",
        "represents", "represent", "displays", "display", "contains", "contain",
        "gives", "give", "provides", "provide", "above", "below", "and", "or",
        "in", "to", "of", "on", "for", "with", "from", "at", "by", "out"
    };

    public static bool TryParseFigureCaption(string? text, out ParsedFigureCaption parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.TrimStart();
        if (IsNonFigureArtifactCaption(trimmed))
            return false;

        var m = VisualCaptionRegex().Match(trimmed);
        if (!m.Success)
            return false;

        string rawPrefix = m.Groups["prefix"].Value.Trim();
        string rawNum = m.Groups["num"].Value.Trim();
        string delim = m.Groups["delim"].Value;
        string rawRest = m.Groups["rest"].Value;
        string rest = rawRest.TrimStart();

        if (string.IsNullOrWhiteSpace(rawNum))
            return false;

        // Reject hyphenated compound prose words like "Figure X-ray diffraction..." or "Figure V-shaped..."
        if (delim.TrimEnd() is "-" or "–" or "—" && rawRest.Length > 0 && char.IsLower(rawRest[0]))
            return false;

        // Roman numerals require an explicit delimiter or capitalized title so words like "Figure I" in prose don't misfire
        if (IsPureRoman(rawNum) && string.IsNullOrEmpty(delim) && (rest.Length == 0 || !char.IsUpper(rest[0])))
            return false;

        // Reject parenthetical/inline prose references like "Fig. 2.16).", "Fig. 2.22):", "Fig. 2.10, left"
        if (rest.Length > 0 && (rest[0] is ')' or ']' or ',' or ';'))
            return false;

        // Reject inline prose continuations like "Fig. 2.14 sis is important..." or "Figure 2.1 shows..."
        if (string.IsNullOrEmpty(delim) && !string.IsNullOrEmpty(rest))
        {
            if (char.IsLower(rest[0]))
                return false;

            var nextWordMatch = LeadingWordRegex().Match(rest);
            if (nextWordMatch.Success && InlineProseStopWords.Contains(nextWordMatch.Value))
                return false;
        }

        string norm = NormalizeLookupNumber(rawNum);
        if (string.IsNullOrEmpty(norm))
            return false;

        string cleanNum = rawNum.TrimEnd(':', '.', '-', '–', '—').Trim();
        string shortLabel = $"{rawPrefix} {cleanNum}".Trim();
        parsed = new ParsedFigureCaption(rawPrefix, cleanNum, norm, shortLabel);
        return true;
    }

    public static bool IsNonFigureArtifactCaption(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return NonFigureArtifactRegex().IsMatch(text.TrimStart());
    }

    public static (string? Prefix, string StrippedNumber) ExtractLeadingPrefix(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return (null, string.Empty);

        string s = rawInput.Trim();
        if (s.StartsWith('#'))
            s = s[1..].TrimStart();

        var prefixMatch = LeadingVisualPrefixRegex().Match(s);
        if (!prefixMatch.Success)
            return (null, s);

        string prefix = prefixMatch.Value.Trim();
        string rest = s[prefixMatch.Length..].TrimStart();
        return (string.IsNullOrWhiteSpace(prefix) ? null : prefix, rest);
    }

    public static string NormalizeLookupNumber(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return string.Empty;

        string s = rawInput.Trim();
        if (s.StartsWith('#'))
            s = s[1..].TrimStart();

        var prefixMatch = LeadingVisualPrefixRegex().Match(s);
        if (prefixMatch.Success)
            s = s[prefixMatch.Length..].TrimStart();

        s = s.TrimEnd(':', '.', '-', '–', '—', ' ');

        // Normalize "2.1(a)" -> "2.1a" and collapse internal spaces around punctuation
        s = SubPanelParenRegex().Replace(s, "$1");
        s = InternalPunctSpaceRegex().Replace(s, "$1");
        s = s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return s;
    }

    public static string SanitizeForFileName(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return string.Empty;

        var sb = new StringBuilder(number.Length);
        foreach (char c in number.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
                sb.Append(char.ToLowerInvariant(c));
            else if (c is '(' or ')' or ' ')
                continue;
            else
                sb.Append('_');
        }
        return sb.ToString().Trim('_', '.');
    }

    internal static bool IsPureRoman(string token) =>
        !string.IsNullOrWhiteSpace(token) && RomanNumeralRegex().IsMatch(token.Trim());

    [GeneratedRegex(
        @"^(?<prefix>(?:Figures?|Figs?\.?|Diagrams?|Diags?\.?|Charts?|Graphs?|Plots?|Illustrations?|Illus\.?|Plates?|Pl\.?|Schemes?|Maps?|Images?|Imgs?\.?|Pictures?|Pics?\.?|Exhibits?))\s+(?<num>(?:[SA]-?\d+(?:\.\d+)*(?:\([a-zA-Z0-9]+\)|[a-zA-Z])?|\d+(?:[.\-]\d+)*(?:\([a-zA-Z0-9]+\)|[a-zA-Z])?|(?-i:[IVX]{1,6})))(?<delim>\s*[:.\-–—|])?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex VisualCaptionRegex();

    [GeneratedRegex(
        @"^(?:Tables?|Tabs?\.?|Tbls?\.?|Box(?:es)?|Text\s+Box(?:es)?|Sidebars?|Callouts?|Eqs?\.?|Eqns?\.?|Equations?|Formulas?|Algorithms?|Algo\.?|Listings?)\s*(?:\(?[0-9IVXA-Za-z.\-]+\)?|:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NonFigureArtifactRegex();

    [GeneratedRegex(
        @"^(?:Figures?|Figs?\.?|Diagrams?|Diags?\.?|Charts?|Graphs?|Plots?|Illustrations?|Illus\.?|Plates?|Pl\.?|Schemes?|Maps?|Images?|Imgs?\.?|Pictures?|Pics?\.?|Exhibits?)\b\.?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LeadingVisualPrefixRegex();

    [GeneratedRegex(@"^[A-Za-z]+", RegexOptions.Compiled)]
    private static partial Regex LeadingWordRegex();

    [GeneratedRegex(@"\(([A-Za-z0-9]{1,3})\)", RegexOptions.Compiled)]
    private static partial Regex SubPanelParenRegex();

    [GeneratedRegex(@"\s*([.\-])\s*", RegexOptions.Compiled)]
    private static partial Regex InternalPunctSpaceRegex();

    [GeneratedRegex(@"^[IVX]{1,6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RomanNumeralRegex();
}
