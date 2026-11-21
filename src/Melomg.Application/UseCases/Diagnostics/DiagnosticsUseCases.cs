using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;

namespace Melomg.Application.UseCases.Diagnostics;

public sealed record GetDeleteImpactQuery(string TargetId);

public sealed record DeleteImpactResponse(string TargetId, string EntityKind, string WarningSummary);

public sealed class GetDeleteImpactHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<DeleteImpactResponse>> HandleAsync(GetDeleteImpactQuery query, CancellationToken cancellationToken = default)
    {
        if (query.TargetId.StartsWith("Doc_", StringComparison.Ordinal))
        {
            var docLookup = await repository.FindDocumentAsync(query.TargetId, cancellationToken);
            if (docLookup is null) return DocumentErrors.NotFound(query.TargetId);

            var doc = docLookup.Document;
            string warningSummary = doc.Mode == WorkspaceMode.Project
                ? $"Deleting project '{doc.Id}' (\"{doc.Title}\", {doc.PageCount} page(s), {doc.Figures.Count} figure(s)) will permanently remove the project directory '{docLookup.StorageDirectory}', including its SQLite database and PDF '{doc.StoredPdfPath}'."
                : $"Deleting document workspace '{doc.Id}' (\"{doc.Title}\", {doc.PageCount} page(s), {doc.Figures.Count} figure(s)) will permanently remove its SQLite database and workspace outputs at '{docLookup.StorageDirectory}' (the original source PDF is never deleted).";
            return new DeleteImpactResponse(doc.Id, "document", warningSummary);
        }

        if (query.TargetId.StartsWith("Fig_", StringComparison.Ordinal))
        {
            var owner = await repository.FindFigureOwnerAsync(query.TargetId, cancellationToken);
            if (owner is null) return FigureErrors.NotFound(query.TargetId);

            var fig = owner.Figure;
            return new DeleteImpactResponse(
                fig.Id, "figure",
                $"Deleting figure '{fig.Id}' (index #{fig.Index}, page {fig.PageNumber}, {fig.Category}) from '{owner.Document.Id}' will shift subsequent figure indices up (n - 1).");
        }

        return DocumentErrors.NotFound(query.TargetId);
    }
}

public enum LintSeverity { Warning, Error }

public sealed record LintIssue(string Code, LintSeverity Severity, string EntityId, string Message);

public sealed record LintDocumentQuery(string TargetId);

public sealed record LintReportResponse(string TargetId, bool IsClean, int ErrorCount, int WarningCount, IReadOnlyList<LintIssue> Issues);

public sealed class LintDocumentHandler(
    IDocumentWorkspaceRepository repository, IPdfIngestionEngine pdfEngine,
    IFigureImageExtractor figureExtractor, IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<LintReportResponse>> HandleAsync(LintDocumentQuery query, CancellationToken cancellationToken = default)
    {
        PdfDocument document;
        string storageDir;
        IReadOnlyList<DocumentFigure> figuresToCheck;
        bool isFigureTarget = query.TargetId.StartsWith("Fig_", StringComparison.Ordinal);

        if (isFigureTarget)
        {
            var owner = await repository.FindFigureOwnerAsync(query.TargetId, cancellationToken);
            if (owner is null) return FigureErrors.NotFound(query.TargetId);
            document = owner.Document;
            storageDir = owner.StorageDirectory;
            figuresToCheck = [owner.Figure];
        }
        else
        {
            var lookup = await repository.FindDocumentAsync(query.TargetId, cancellationToken);
            if (lookup is null) return DocumentErrors.NotFound(query.TargetId);
            document = lookup.Document;
            storageDir = lookup.StorageDirectory;
            figuresToCheck = document.Figures;
        }

        var issues = new List<LintIssue>();
        if (!workingDirectoryProvider.DirectoryExists(storageDir) ||
            !workingDirectoryProvider.DirectoryExists(Path.Combine(storageDir, "figures")))
        {
            issues.Add(new LintIssue("Lint.BrokenFiguresDirectory", LintSeverity.Error, document.Id,
                $"Workspace directory or 'figures/' folder is missing under '{storageDir}'."));
        }

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
        }

        if (!isFigureTarget && document.Figures.Count == 0)
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

            if (!string.IsNullOrWhiteSpace(fig.LastExtractedPath))
            {
                var imgCheck = await figureExtractor.VerifyExtractedImageAsync(fig.LastExtractedPath, cancellationToken);
                if (!imgCheck.IsSuccess)
                    issues.Add(new LintIssue("Lint.BrokenExtractedImage", LintSeverity.Error, fig.Id, imgCheck.Error!.Description));
            }
        }

        int errorCount = issues.Count(i => i.Severity == LintSeverity.Error);
        return new LintReportResponse(query.TargetId, IsClean: errorCount == 0, errorCount, issues.Count - errorCount, issues);
    }
}
