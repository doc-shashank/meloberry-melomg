using Melomg.Application.Abstractions;
using Melomg.Application.Addressing;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;

namespace Melomg.Application.UseCases.Diagnostics;

public sealed class LowercaseLintSeverityConverter()
    : System.Text.Json.Serialization.JsonStringEnumConverter<LintSeverity>(System.Text.Json.JsonNamingPolicy.CamelCase);

[System.Text.Json.Serialization.JsonConverter(typeof(LowercaseLintSeverityConverter))]
public enum LintSeverity { Warning, Error }

public sealed record LintIssue(string Code, LintSeverity Severity, string EntityId, string Message);

public sealed record LintDocumentQuery(string TargetAddress, bool Quiet = false, bool Verbose = false);

public sealed record LintReportResponse(
    string TargetId,
    bool IsClean,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<LintIssue> Issues,
    IReadOnlyList<int>? SuspectPages = null,
    int? CheckedFigureCount = null,
    bool? SourcePdfVerified = null);

public sealed class LintDocumentHandler(
    AddressResolver resolver,
    IPdfIngestionEngine pdfEngine,
    IFigureImageExtractor figureExtractor,
    IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<LintReportResponse>> HandleAsync(LintDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var resolvedRes = await resolver.ResolveTargetAsync(query.TargetAddress, cancellationToken);
        if (!resolvedRes.IsSuccess) return resolvedRes.Error!;

        var resolved = resolvedRes.Value;
        var document = resolved.DocumentContext.Document;
        string storageDir = resolved.DocumentContext.StorageDirectory;
        IReadOnlyList<DocumentFigure> figuresToCheck = resolved.Figures;
        bool isDocTarget = resolved.Address.Kind == AddressKind.Document;
        string reportedId = resolved.Address.Kind == AddressKind.FigureId
            ? resolved.Figures[0].Id
            : (isDocTarget ? document.Id : resolved.Address.RawAddress);

        var issues = new List<LintIssue>();
        if (!workingDirectoryProvider.DirectoryExists(storageDir) ||
            !workingDirectoryProvider.DirectoryExists(Path.Combine(storageDir, "figures")))
        {
            issues.Add(new LintIssue("Lint.BrokenFiguresDirectory", LintSeverity.Error, document.Id,
                $"Workspace directory or 'figures/' folder is missing under '{storageDir}'."));
        }

        bool sourcePdfVerified = false;
        if (!workingDirectoryProvider.FileExists(document.StoredPdfPath))
        {
            issues.Add(new LintIssue("Lint.MissingSourcePdf", LintSeverity.Error, document.Id,
                $"Source PDF '{document.StoredPdfPath}' is missing."));
        }
        else
        {
            var integrity = await pdfEngine.VerifyPdfIntegrityAsync(document.StoredPdfPath, document.Sha256Hash, cancellationToken);
            if (!integrity.IsSuccess)
                issues.Add(new LintIssue("Lint.CorruptedSourcePdf", LintSeverity.Error, document.Id, integrity.Error!.Description));
            else
                sourcePdfVerified = true;
        }

        if (isDocTarget && document.Figures.Count == 0)
        {
            issues.Add(new LintIssue("Lint.NoFiguresDetected", LintSeverity.Warning, document.Id,
                $"Document '{document.Id}' currently has 0 detected or manual figures."));
        }

        foreach (var fig in figuresToCheck)
        {
            var page = document.GetPage(fig.PageNumber);
            if (page is null)
            {
                issues.Add(new LintIssue("Lint.OutOfPageBounds", LintSeverity.Error, fig.Id,
                    $"Figure '{fig.Id}' references missing page {fig.PageNumber}."));
                continue;
            }

            var boundsCheck = fig.BoundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
            if (!boundsCheck.IsSuccess)
                issues.Add(new LintIssue("Lint.OutOfPageBounds", LintSeverity.Error, fig.Id, boundsCheck.Error!.Description));

            if (fig.CaptionBoundingBox is { } capBox)
            {
                var capCheck = capBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
                if (!capCheck.IsSuccess)
                    issues.Add(new LintIssue("Lint.OutOfPageBounds", LintSeverity.Error, fig.Id, $"Caption box: {capCheck.Error!.Description}"));
            }

            foreach (var mask in fig.LabelMasks)
            {
                var maskCheck = mask.BoundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
                if (!maskCheck.IsSuccess)
                    issues.Add(new LintIssue("Lint.OutOfPageBounds", LintSeverity.Error, fig.Id, $"Mask #{mask.Index}: {maskCheck.Error!.Description}"));
            }

            if (!string.IsNullOrWhiteSpace(fig.LastExtractedPath))
            {
                var imgCheck = await figureExtractor.VerifyExtractedImageAsync(fig.LastExtractedPath, cancellationToken);
                if (!imgCheck.IsSuccess)
                    issues.Add(new LintIssue("Lint.BrokenExtractedImage", LintSeverity.Error, fig.Id, imgCheck.Error!.Description));
            }
        }

        IReadOnlyList<int>? suspectPages = null;
        if (isDocTarget)
        {
            var anomalyReport = FigureSequenceAnalyzer.Analyze(document);
            foreach (var anomaly in anomalyReport.Anomalies)
            {
                string entityId = anomaly.FigureIds is { Count: 1 } ? anomaly.FigureIds[0] : document.Id;
                issues.Add(new LintIssue(MapAnomalyLintCode(anomaly.Code), LintSeverity.Warning, entityId, anomaly.Message));
            }
            if (anomalyReport.SuspectPages.Count > 0)
                suspectPages = anomalyReport.SuspectPages;
        }

        int errorCount = issues.Count(i => i.Severity == LintSeverity.Error);
        int warningCount = issues.Count - errorCount;
        var returnedIssues = query.Quiet
            ? issues.Where(i => i.Severity == LintSeverity.Error).ToList()
            : issues;
        return new LintReportResponse(
            reportedId,
            IsClean: errorCount == 0,
            errorCount,
            warningCount,
            returnedIssues,
            suspectPages,
            CheckedFigureCount: query.Verbose ? figuresToCheck.Count : null,
            SourcePdfVerified: query.Verbose ? sourcePdfVerified : null);
    }

    private static string MapAnomalyLintCode(string anomalyCode) => anomalyCode switch
    {
        "Anomaly.SequenceGap" => "Lint.SequenceGap",
        "Anomaly.DuplicateNumber" => "Lint.DuplicateFigureNumber",
        "Anomaly.OutOfOrderNumber" => "Lint.OutOfOrderFigureNumber",
        "Anomaly.UnnumberedFigure" => "Lint.UnnumberedFigure",
        "Anomaly.LowConfidence" => "Lint.LowConfidenceFigure",
        _ => anomalyCode
    };
}
