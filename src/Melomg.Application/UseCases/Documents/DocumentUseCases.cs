using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Runtime;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Models;

namespace Melomg.Application.UseCases.Documents;

internal static class ModelResolutionHelper
{
    public static Result<OnnxModelProfile> CreateCustomFromMetadata(OnnxModelMetadata meta, double confidenceThreshold, double iouThreshold) =>
        OnnxModelProfile.CreateCustom(
            meta.ModelName, meta.ModelPath, confidenceThreshold, iouThreshold,
            meta.InputWidth, meta.InputHeight, meta.OpsetVersion, meta.IrVersion, meta.SupportedClasses);

    public static async Task<Result<OnnxModelProfile>> ResolveEffectiveModelAsync(
        string? explicitModelPath, double? confidenceOverride, IModelSettingsRepository modelSettings,
        IOnnxModelInspector modelInspector, CancellationToken cancellationToken)
    {
        var activeProfile = await modelSettings.GetActiveModelProfileAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(explicitModelPath))
        {
            var inspected = await modelInspector.InspectAndValidateModelAsync(explicitModelPath, cancellationToken);
            if (!inspected.IsSuccess) return inspected.Error!;
            return CreateCustomFromMetadata(inspected.Value, confidenceOverride ?? activeProfile.ConfidenceThreshold, activeProfile.IouThreshold);
        }

        if (!confidenceOverride.HasValue)
            return activeProfile;

        var clone = OnnxModelProfile.Rehydrate(
            activeProfile.ModelName, activeProfile.ModelPath, activeProfile.IsBuiltIn, activeProfile.ConfidenceThreshold,
            activeProfile.IouThreshold, activeProfile.InputWidth, activeProfile.InputHeight,
            activeProfile.OpsetVersion, activeProfile.IrVersion, activeProfile.SupportedClasses);
        var thresholdCheck = clone.SetThresholds(confidenceOverride.Value, null);
        return thresholdCheck.IsSuccess ? clone : thresholdCheck.Error!;
    }

    public static async Task<Result<PdfIngestionAnalysis>> PrepareAndAnalyzePdfAsync(
        string fullPdfPath, string? modelPath, double? confidenceThreshold, bool offline,
        IOnnxRuntimeManager runtimeManager, IModelSettingsRepository modelSettings,
        IOnnxModelInspector modelInspector, IPdfIngestionEngine pdfEngine, CancellationToken cancellationToken)
    {
        var runtimeStatus = await runtimeManager.EnsureReadyForInferenceAsync(allowAutoInstall: !offline, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(modelPath))
            await RuntimeModelActivationHelper.TryPromotePretrainedModelAsync(modelSettings, modelInspector, runtimeStatus, cancellationToken);

        var modelResult = await ResolveEffectiveModelAsync(modelPath, confidenceThreshold, modelSettings, modelInspector, cancellationToken);
        if (!modelResult.IsSuccess) return modelResult.Error!;

        return await pdfEngine.AnalyzePdfAsync(fullPdfPath, modelResult.Value, confidenceThreshold, cancellationToken);
    }

    public static Result PopulateFigures(PdfDocument document, IReadOnlyList<DetectedFigureCandidate> candidates, IIdGenerator idGenerator)
    {
        foreach (var c in candidates)
        {
            var addResult = document.AddFigure(
                idGenerator.NewFigureId(), c.PageNumber, c.Category, c.BoundingBox, c.Confidence,
                c.Caption, c.Label, c.Source, targetIndex: null, captionBoundingBox: c.CaptionBoundingBox);
            if (!addResult.IsSuccess) return addResult.Error!;
        }
        return Result.Success();
    }
}

public sealed record IngestPdfCommand(
    string PdfPath, string? WorkingDirectory = null, double? ConfidenceThreshold = null,
    string? ModelPath = null, string? Title = null, bool Offline = false, bool RetainDatabase = false);

public sealed record IngestPdfResponse(
    string DocumentId, string Title, string WorkspaceDirectory, string StoredPdfPath, string Sha256Hash,
    int PageCount, int FigureCount, string? FirstFigureId, string ModelName, double ConfidenceThreshold,
    string InferenceEngine = "managed-fallback", bool RetainDatabase = false, string WorkspaceMode = "standard");

public sealed record OpenProjectCommand(
    string TargetPath, string? ProjectDirectory = null, double? ConfidenceThreshold = null,
    string? ModelPath = null, string? Title = null, bool Offline = false);

public sealed record OpenProjectResponse(
    string DocumentId, string Title, string ProjectDirectory, string StoredPdfPath, string DatabasePath,
    string Sha256Hash, int PageCount, int FigureCount, string? FirstFigureId, string ModelName,
    double ConfidenceThreshold, string InferenceEngine = "managed-fallback", bool ReopenedExisting = false);

public sealed class IngestPdfHandler(
    IIdGenerator idGenerator, IWorkingDirectoryProvider workingDirectoryProvider,
    IModelSettingsRepository modelSettings, IOnnxModelInspector modelInspector,
    IPdfIngestionEngine pdfEngine, IDocumentWorkspaceRepository repository, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<IngestPdfResponse>> HandleAsync(IngestPdfCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.PdfPath))
            return DocumentErrors.PdfFileNotFound(string.Empty);

        var fullPdfPath = Path.GetFullPath(command.PdfPath.Trim());
        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            fullPdfPath, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var analysis = analysisResult.Value;
        var docId = idGenerator.NewDocumentId();
        var workspaceDir = Path.Combine(workingDirectoryProvider.NormalizeDirectoryPath(command.WorkingDirectory), docId);

        var workspaceInit = await pdfEngine.InitializeWorkspaceAsync(workspaceDir, cancellationToken);
        if (!workspaceInit.IsSuccess) return workspaceInit.Error!;

        var resolvedTitle = string.IsNullOrWhiteSpace(command.Title) ? analysis.SuggestedTitle : command.Title.Trim();
        var docResult = PdfDocument.Create(
            docId, resolvedTitle, fullPdfPath, fullPdfPath, analysis.Sha256Hash, workspaceDir,
            analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold, TimeProvider.System.GetUtcNow(),
            analysis.Pages, WorkspaceMode.Standard, command.RetainDatabase);
        if (!docResult.IsSuccess) return docResult.Error!;

        var document = docResult.Value;
        var popRes = ModelResolutionHelper.PopulateFigures(document, analysis.Candidates, idGenerator);
        if (!popRes.IsSuccess) return popRes.Error!;

        await repository.CreateDocumentAsync(document, cancellationToken);

        return new IngestPdfResponse(
            document.Id, document.Title, document.WorkingDirectory, document.StoredPdfPath, document.Sha256Hash,
            document.PageCount, document.Figures.Count, document.ActiveFigureId, document.ModelName,
            document.ConfidenceThreshold, analysis.InferenceEngine, document.RetainDatabase, "standard");
    }
}

public sealed class OpenProjectHandler(
    IIdGenerator idGenerator, IWorkingDirectoryProvider workingDirectoryProvider,
    IModelSettingsRepository modelSettings, IOnnxModelInspector modelInspector,
    IPdfIngestionEngine pdfEngine, IDocumentWorkspaceRepository repository, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<OpenProjectResponse>> HandleAsync(OpenProjectCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.TargetPath))
            return DocumentErrors.PdfFileNotFound(string.Empty);

        var fullTarget = Path.GetFullPath(command.TargetPath.Trim());
        if (workingDirectoryProvider.DirectoryExists(fullTarget))
        {
            var existing = await repository.FindDocumentInDirectoryAsync(fullTarget, cancellationToken);
            if (existing is null) return DocumentErrors.ProjectNotFound(fullTarget);

            var existingDoc = existing.Document;
            return new OpenProjectResponse(
                existingDoc.Id, existingDoc.Title, existing.StorageDirectory, existingDoc.StoredPdfPath, existing.DatabasePath,
                existingDoc.Sha256Hash, existingDoc.PageCount, existingDoc.Figures.Count,
                existingDoc.ActiveFigureId ?? existingDoc.Figures.FirstOrDefault()?.Id,
                existingDoc.ModelName, existingDoc.ConfidenceThreshold, "cached", ReopenedExisting: true);
        }

        string projectDir = !string.IsNullOrWhiteSpace(command.ProjectDirectory)
            ? workingDirectoryProvider.NormalizeDirectoryPath(command.ProjectDirectory)
            : Path.Combine(workingDirectoryProvider.GetCurrentDirectory(), $"project_{SanitizeProjectFolderSegment(Path.GetFileNameWithoutExtension(fullTarget))}");

        string expectedDestPdf = Path.Combine(projectDir, Path.GetFileName(fullTarget));
        if (!string.Equals(fullTarget, expectedDestPdf, StringComparison.OrdinalIgnoreCase) &&
            workingDirectoryProvider.FileExists(expectedDestPdf))
        {
            return DocumentErrors.ProjectPdfAlreadyExists(expectedDestPdf);
        }

        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            fullTarget, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var moveResult = await pdfEngine.MovePdfToProjectDirectoryAsync(fullTarget, projectDir, cancellationToken);
        if (!moveResult.IsSuccess) return moveResult.Error!;

        string movedPdfPath = moveResult.Value;
        bool movedFromSource = !string.Equals(fullTarget, movedPdfPath, StringComparison.OrdinalIgnoreCase);

        void TryRollbackMovedPdf()
        {
            if (!movedFromSource) return;
            try
            {
                if (File.Exists(movedPdfPath) && !File.Exists(fullTarget))
                    File.Move(movedPdfPath, fullTarget);
            }
            catch { }
        }

        try
        {
            var analysis = analysisResult.Value;
            var docId = idGenerator.NewDocumentId();
            var resolvedTitle = string.IsNullOrWhiteSpace(command.Title) ? analysis.SuggestedTitle : command.Title.Trim();

            var docResult = PdfDocument.Create(
                docId, resolvedTitle, movedPdfPath, movedPdfPath, analysis.Sha256Hash, projectDir,
                analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold, TimeProvider.System.GetUtcNow(),
                analysis.Pages, WorkspaceMode.Project, retainDatabase: true);
            if (!docResult.IsSuccess)
            {
                TryRollbackMovedPdf();
                return docResult.Error!;
            }

            var document = docResult.Value;
            var popRes = ModelResolutionHelper.PopulateFigures(document, analysis.Candidates, idGenerator);
            if (!popRes.IsSuccess)
            {
                TryRollbackMovedPdf();
                return popRes.Error!;
            }

            await repository.CreateDocumentAsync(document, cancellationToken);

            return new OpenProjectResponse(
                document.Id, document.Title, projectDir, movedPdfPath, Path.Combine(projectDir, $"{document.Id}.db"),
                document.Sha256Hash, document.PageCount, document.Figures.Count, document.ActiveFigureId,
                document.ModelName, document.ConfidenceThreshold, analysis.InferenceEngine, ReopenedExisting: false);
        }
        catch
        {
            TryRollbackMovedPdf();
            throw;
        }
    }

    private static string SanitizeProjectFolderSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "document";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "document" : cleaned;
    }
}

public sealed record UseDocumentCommand(string DocumentId);

public sealed record UseDocumentResponse(string DocumentId, string Title, int PageCount, int FigureCount, string? ActiveFigureId, string StorageDirectory);

public sealed class UseDocumentHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<UseDocumentResponse>> HandleAsync(UseDocumentCommand command, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        var doc = lookup.Document;
        return new UseDocumentResponse(doc.Id, doc.Title, doc.PageCount, doc.Figures.Count, doc.ActiveFigureId, lookup.StorageDirectory);
    }
}

public sealed record ListDocumentsQuery(string? Directory = null);

public sealed class ListDocumentsHandler(IDocumentWorkspaceRepository repository, IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<IReadOnlyList<DocumentListItem>>> HandleAsync(ListDocumentsQuery query, CancellationToken cancellationToken = default)
    {
        string? normalized = string.IsNullOrWhiteSpace(query.Directory) ? null : workingDirectoryProvider.NormalizeDirectoryPath(query.Directory);
        var items = await repository.ListDocumentsAsync(normalized, cancellationToken);
        return Result<IReadOnlyList<DocumentListItem>>.Success(items);
    }
}

public sealed record GetDocumentInfoQuery(string DocumentId);

public sealed record CategoryCountDto(string Category, int Count);

public sealed record DocumentInfoResponse(
    string DocumentId, string Title, string SourcePdfPath, string StoredPdfPath, string Sha256Hash,
    int PageCount, int FigureCount, string? ActiveFigureId, string ModelName, double ConfidenceThreshold,
    string StorageDirectory, DateTimeOffset IngestedAtUtc, IReadOnlyList<PdfPageInfo> Pages, IReadOnlyList<CategoryCountDto> CategoryBreakdown);

public sealed class GetDocumentInfoHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<DocumentInfoResponse>> HandleAsync(GetDocumentInfoQuery query, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(query.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(query.DocumentId);

        var doc = lookup.Document;
        var breakdown = doc.Figures
            .GroupBy(f => f.Category)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new CategoryCountDto(g.Key.ToString().ToLowerInvariant(), g.Count()))
            .ToList();

        return new DocumentInfoResponse(
            doc.Id, doc.Title, doc.SourcePdfPath, doc.StoredPdfPath, doc.Sha256Hash, doc.PageCount, doc.Figures.Count,
            doc.ActiveFigureId, doc.ModelName, doc.ConfidenceThreshold, lookup.StorageDirectory, doc.IngestedAtUtc, doc.Pages, breakdown);
    }
}

public sealed record SetDocumentTitleCommand(string DocumentId, string Title);

public sealed record SetDocumentTitleResponse(string DocumentId, string Title);

public sealed class SetDocumentTitleHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<SetDocumentTitleResponse>> HandleAsync(SetDocumentTitleCommand command, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        var setResult = lookup.Document.SetTitle(command.Title);
        if (!setResult.IsSuccess) return setResult.Error!;

        await repository.SaveDocumentAsync(lookup.Document, cancellationToken);
        return new SetDocumentTitleResponse(lookup.Document.Id, lookup.Document.Title);
    }
}

public sealed record ReanalyzeDocumentCommand(string DocumentId, double? ConfidenceThreshold = null, string? ModelPath = null, bool Offline = false);

public sealed record ReanalyzeDocumentResponse(
    string DocumentId, int PreviousFigureCount, int FigureCount, string? FirstFigureId,
    string ModelName, double ConfidenceThreshold, string InferenceEngine = "managed-fallback");

public sealed class ReanalyzeDocumentHandler(
    IIdGenerator idGenerator, IModelSettingsRepository modelSettings, IOnnxModelInspector modelInspector,
    IPdfIngestionEngine pdfEngine, IDocumentWorkspaceRepository repository, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ReanalyzeDocumentResponse>> HandleAsync(ReanalyzeDocumentCommand command, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        var doc = lookup.Document;
        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            doc.StoredPdfPath, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var analysis = analysisResult.Value;
        int previousCount = doc.Figures.Count;
        var createdFigures = new List<DocumentFigure>(analysis.Candidates.Count);

        foreach (var c in analysis.Candidates)
        {
            var page = doc.GetPage(c.PageNumber);
            if (page is null) continue;

            var figResult = DocumentFigure.Create(
                idGenerator.NewFigureId(), doc.Id, c.PageNumber, c.Category, c.BoundingBox,
                page, c.Confidence, c.Caption, c.Label, c.Source, c.CaptionBoundingBox);
            if (!figResult.IsSuccess) return figResult.Error!;
            createdFigures.Add(figResult.Value);
        }

        var replaceResult = doc.ReplaceDetectedFigures(createdFigures, analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold);
        if (!replaceResult.IsSuccess) return replaceResult.Error!;

        await repository.SaveDocumentAsync(doc, cancellationToken);

        return new ReanalyzeDocumentResponse(
            doc.Id, previousCount, doc.Figures.Count, doc.ActiveFigureId, doc.ModelName, doc.ConfidenceThreshold, analysis.InferenceEngine);
    }
}

public sealed record DeleteDocumentCommand(string DocumentId);

public sealed record DeleteDocumentResponse(string DeletedDocumentId);

public sealed class DeleteDocumentHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<DeleteDocumentResponse>> HandleAsync(DeleteDocumentCommand command, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        await repository.DeleteDocumentAsync(command.DocumentId, cancellationToken);
        return new DeleteDocumentResponse(command.DocumentId);
    }
}
