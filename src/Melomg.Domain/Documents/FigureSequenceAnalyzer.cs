using System.Globalization;
using System.Text.RegularExpressions;

namespace Melomg.Domain.Documents;

public sealed record FigureAnomaly(
    string Code,
    string Severity,
    string Message,
    string? ExpectedNumber = null,
    IReadOnlyList<int>? SuspectPages = null,
    IReadOnlyList<string>? FigureIds = null);

public sealed record FigureAnomalyReport(
    IReadOnlyList<FigureAnomaly> Anomalies,
    IReadOnlyList<int> SuspectPages);

public static partial class FigureSequenceAnalyzer
{
    public const double DefaultLowConfidenceThreshold = 0.55;

    [GeneratedRegex(@"^(?<series>(?:[A-Za-z]-?|\d+(?:[.\-]\d+)*[.\-]))(?<ord>\d+)(?:[a-zA-Z]|\([a-zA-Z0-9]+\))?$", RegexOptions.Compiled)]
    private static partial Regex HierarchicalSeriesRegex();

    [GeneratedRegex(@"^(?<ord>\d+)(?:[a-zA-Z]|\([a-zA-Z0-9]+\))?$", RegexOptions.Compiled)]
    private static partial Regex SequentialSeriesRegex();

    public static FigureAnomalyReport Analyze(PdfDocument document, double lowConfidenceThreshold = DefaultLowConfidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(document);

        var anomalies = new List<FigureAnomaly>();
        var highSignalPages = new SortedSet<int>();
        var allAnomalyPages = new SortedSet<int>();

        // 1. Duplicate figure numbers (ignore sub-panels with distinct normalized numbers like 2.1a vs 2.1b)
        var duplicateGroups = document.Figures
            .Where(f => !string.IsNullOrWhiteSpace(f.NormalizedNumber))
            .GroupBy(f => f.NormalizedNumber!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var grp in duplicateGroups)
        {
            var figs = grp.OrderBy(f => f.PageNumber).ThenBy(f => f.Index).ToList();
            var pages = figs.Select(f => f.PageNumber).Distinct().OrderBy(p => p).ToList();
            var figIds = figs.Select(f => f.Id).ToList();
            string displayNum = figs[0].FigureNumber ?? grp.Key;

            foreach (int p in pages)
            {
                highSignalPages.Add(p);
                allAnomalyPages.Add(p);
            }

            anomalies.Add(new FigureAnomaly(
                Code: "Anomaly.DuplicateNumber",
                Severity: "warning",
                Message: $"Duplicate figure number '{displayNum}' assigned to {figs.Count} figures on page(s) {string.Join(", ", pages)}.",
                ExpectedNumber: displayNum,
                SuspectPages: pages,
                FigureIds: figIds));
        }

        // 2. Parse series entries for out-of-order and sequence-gap analysis
        var parsedEntries = new List<(string SeriesKey, int Ordinal, int PageNumber, int Index, string FigureId, string DisplayNum)>();
        foreach (var fig in document.Figures.OrderBy(f => f.PageNumber).ThenBy(f => f.Index))
        {
            if (TryParseSeriesOrdinal(fig.NormalizedNumber, out var seriesKey, out int ordinal))
                parsedEntries.Add((seriesKey, ordinal, fig.PageNumber, fig.Index, fig.Id, fig.FigureNumber ?? fig.NormalizedNumber!));
        }

        foreach (var seriesGroup in parsedEntries.GroupBy(e => e.SeriesKey, StringComparer.Ordinal))
        {
            var items = seriesGroup.OrderBy(e => e.PageNumber).ThenBy(e => e.Index).ToList();
            if (items.Count < 2)
                continue;

            // 2a. Out-of-order check (number drops on a later page)
            int maxOrdinal = items[0].Ordinal;
            int maxOrdPage = items[0].PageNumber;
            foreach (var item in items.Skip(1))
            {
                if (item.Ordinal < maxOrdinal && item.PageNumber > maxOrdPage)
                {
                    var pages = new[] { maxOrdPage, item.PageNumber }.Distinct().OrderBy(p => p).ToList();
                    foreach (int p in pages)
                    {
                        highSignalPages.Add(p);
                        allAnomalyPages.Add(p);
                    }

                    anomalies.Add(new FigureAnomaly(
                        Code: "Anomaly.OutOfOrderNumber",
                        Severity: "warning",
                        Message: $"Figure '{item.DisplayNum}' on page {item.PageNumber} appears out of order after '{seriesGroup.Key}{maxOrdinal}' on page {maxOrdPage}.",
                        ExpectedNumber: item.DisplayNum,
                        SuspectPages: pages,
                        FigureIds: [item.FigureId]));
                }
                else if (item.Ordinal > maxOrdinal)
                {
                    maxOrdinal = item.Ordinal;
                    maxOrdPage = item.PageNumber;
                }
            }

            // 2b. Sequence-gap check across sorted distinct ordinals
            var byOrdinal = items
                .GroupBy(e => e.Ordinal)
                .OrderBy(g => g.Key)
                .Select(g => (
                    Ordinal: g.Key,
                    MinPage: g.Min(x => x.PageNumber),
                    MaxPage: g.Max(x => x.PageNumber),
                    FirstDisplay: g.First().DisplayNum))
                .ToList();

            for (int i = 1; i < byOrdinal.Count; i++)
            {
                var prev = byOrdinal[i - 1];
                var curr = byOrdinal[i];
                int diff = curr.Ordinal - prev.Ordinal;
                if (diff <= 1 || diff > 25)
                    continue;

                int startPage = Math.Min(prev.MaxPage, curr.MinPage);
                int endPage = Math.Max(prev.MaxPage, curr.MinPage);
                var suspectPages = Enumerable.Range(startPage, Math.Min(12, endPage - startPage + 1)).ToList();
                foreach (int p in suspectPages)
                {
                    highSignalPages.Add(p);
                    allAnomalyPages.Add(p);
                }

                string firstMissing = $"{seriesGroup.Key}{prev.Ordinal + 1}";
                string missingLabel = diff == 2
                    ? firstMissing
                    : $"{firstMissing}..{seriesGroup.Key}{curr.Ordinal - 1}";

                anomalies.Add(new FigureAnomaly(
                    Code: "Anomaly.SequenceGap",
                    Severity: "warning",
                    Message: $"Detected {seriesGroup.Key}{prev.Ordinal} (p{prev.MaxPage}) and {seriesGroup.Key}{curr.Ordinal} (p{curr.MinPage}); missing {missingLabel} on page(s) {startPage}-{endPage}.",
                    ExpectedNumber: firstMissing,
                    SuspectPages: suspectPages));
            }
        }

        // 3. Low-confidence figures
        var lowConf = document.Figures
            .Where(f => f.Source != DetectionSource.Manual && f.Confidence < lowConfidenceThreshold)
            .OrderBy(f => f.PageNumber)
            .ThenBy(f => f.Index)
            .ToList();

        if (lowConf.Count > 0)
        {
            var pages = lowConf.Select(f => f.PageNumber).Distinct().OrderBy(p => p).ToList();
            foreach (int p in pages)
            {
                highSignalPages.Add(p);
                allAnomalyPages.Add(p);
            }

            anomalies.Add(new FigureAnomaly(
                Code: "Anomaly.LowConfidence",
                Severity: "warning",
                Message: $"{lowConf.Count} detected figure(s) have confidence below {lowConfidenceThreshold:F2} on page(s) {string.Join(", ", pages)}.",
                SuspectPages: pages,
                FigureIds: lowConf.Take(20).Select(f => f.Id).ToList()));
        }

        // 4. Unnumbered figures (when document uses numbered captions)
        var unnumbered = document.Figures
            .Where(f => string.IsNullOrWhiteSpace(f.NormalizedNumber))
            .OrderBy(f => f.PageNumber)
            .ThenBy(f => f.Index)
            .ToList();

        if (unnumbered.Count > 0 && document.Vocabulary.NumberedCount > 0)
        {
            var pages = unnumbered.Select(f => f.PageNumber).Distinct().OrderBy(p => p).ToList();
            foreach (int p in pages)
                allAnomalyPages.Add(p);

            anomalies.Add(new FigureAnomaly(
                Code: "Anomaly.UnnumberedFigure",
                Severity: "warning",
                Message: $"{unnumbered.Count} unnumbered figure candidate(s) detected on page(s) {string.Join(", ", pages)}.",
                SuspectPages: pages,
                FigureIds: unnumbered.Take(20).Select(f => f.Id).ToList()));
        }

        var finalSuspectPages = highSignalPages.Count > 0
            ? highSignalPages.ToList()
            : allAnomalyPages.ToList();

        return new FigureAnomalyReport(anomalies, finalSuspectPages);
    }

    public static bool TryParseSeriesOrdinal(string? normalizedNumber, out string seriesKey, out int ordinal)
    {
        seriesKey = string.Empty;
        ordinal = 0;
        if (string.IsNullOrWhiteSpace(normalizedNumber))
            return false;

        string s = normalizedNumber.Trim();
        var hm = HierarchicalSeriesRegex().Match(s);
        if (hm.Success && int.TryParse(hm.Groups["ord"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0)
        {
            seriesKey = hm.Groups["series"].Value;
            return true;
        }

        var sm = SequentialSeriesRegex().Match(s);
        if (sm.Success && int.TryParse(sm.Groups["ord"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) && ordinal > 0)
        {
            seriesKey = string.Empty;
            return true;
        }

        return false;
    }
}
