using Melomg.Application.Abstractions;
using Melomg.Application.Addressing;
using Melomg.Application.UseCases.Figures;
using Melomg.Application.UseCases.Runtime;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;
using Melomg.Domain.Models;

namespace Melomg.Application.UseCases.Documents;

public enum ViewMode { Outline, Full, Ids }

public sealed record DocumentVocabularyDto(
    string? Primary,
    IReadOnlyList<string> Prefixes,
    string Style,
    int Numbered,
    int Unnumbered)
{
    public static DocumentVocabularyDto FromDomain(DocumentVocabulary v) =>
        new(v.PrimaryPrefix, v.DetectedPrefixes, v.NumberingStyle, v.NumberedCount, v.UnnumberedCount);
}

public sealed record FigureOutlineDto(
    int I,
    string Id,
    string? Num,
    int Page,
    string Category,
    string Label,
    string? Caption);

public sealed record FigureIdsDto(
    int I,
    string Id,
    string? Num,
    int Page);

public sealed record FigureFullDto(
    int Index,
    string FigureId,
    int PageNumber,
    int PageFigureIndex,
    string Category,
    double Confidence,
    BoundingBox BoundingBox,
    string Source,
    string Label,
    string? VocabPrefix,
    string? FigureNumber,
    string? Caption,
    BoundingBox? CaptionBoundingBox,
    string? LastExtractedPath);

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
                c.Caption, c.Label, c.Source, targetIndex: null, captionBoundingBox: c.CaptionBoundingBox,
                vocabPrefix: c.VocabPrefix, figureNumber: c.FigureNumber);
            if (!addResult.IsSuccess) return addResult.Error!;
        }
        return Result.Success();
    }

    public static IReadOnlyList<FigureOutlineDto> ToOutlines(IEnumerable<DocumentFigure> figures) =>
        figures.Select(f => new FigureOutlineDto(
            f.Index, f.Id, f.FigureNumber, f.PageNumber,
            f.Category.ToString().ToLowerInvariant(), f.Label, f.Caption)).ToList();

    public static IReadOnlyList<FigureIdsDto> ToIds(IEnumerable<DocumentFigure> figures) =>
        figures.Select(f => new FigureIdsDto(f.Index, f.Id, f.FigureNumber, f.PageNumber)).ToList();

    public static IReadOnlyList<FigureFullDto> ToFulls(IEnumerable<DocumentFigure> figures) =>
        figures.Select(f => new FigureFullDto(
            f.Index, f.Id, f.PageNumber, f.PageFigureIndex,
            f.Category.ToString().ToLowerInvariant(), f.Confidence, f.BoundingBox,
            DocumentFigure.FormatSourceTag(f.Source), f.Label, f.VocabPrefix, f.FigureNumber,
            f.Caption, f.CaptionBoundingBox, f.LastExtractedPath)).ToList();
}

public sealed record FigureAnomalyDto(
    string Code,
    string Severity,
    string Message,
    string? ExpectedNumber = null,
    IReadOnlyList<int>? SuspectPages = null,
    IReadOnlyList<string>? FigureIds = null);

public sealed record IngestPdfCommand(
    string TargetPath,
    string? OutputDirectory = null,
    bool ProjectMode = false,
    bool Refresh = false,
    double? ConfidenceThreshold = null,
    string? ModelPath = null,
    string? Title = null,
    bool Offline = false,
    bool RetainDatabase = false,
    bool Quiet = false,
    bool Verbose = false);

public sealed record IngestPdfResponse(
    string Id,
    string Title,
    string WorkspaceDirectory,
    string StoredPdfPath,
    string? DatabasePath,
    string? Sha256Hash,
    int Pages,
    int FigureCount,
    DocumentVocabularyDto Vocab,
    IReadOnlyList<FigureOutlineDto>? Figures,
    string ModelName,
    double ConfidenceThreshold,
    string InferenceEngine = "managed-fallback",
    bool RetainDatabase = false,
    string WorkspaceMode = "standard",
    bool ReopenedExisting = false,
    int? PreviousFigureCount = null,
    IReadOnlyList<FigureAnomalyDto>? Anomalies = null,
    IReadOnlyList<int>? SuspectPages = null,
    IReadOnlyList<FigureFullDto>? FullFigures = null);

public sealed class IngestPdfHandler(
    IIdGenerator idGenerator,
    IWorkingDirectoryProvider workingDirectoryProvider,
    IModelSettingsRepository modelSettings,
    IOnnxModelInspector modelInspector,
    IPdfIngestionEngine pdfEngine,
    IDocumentWorkspaceRepository repository,
    IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<IngestPdfResponse>> HandleAsync(IngestPdfCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.TargetPath))
            return DocumentErrors.PdfFileNotFound(string.Empty);

        string target = command.TargetPath.Trim();

        // 1. Refresh existing workspace by Doc_* ID
        if (target.StartsWith("Doc_", StringComparison.Ordinal))
        {
            var lookup = await repository.FindDocumentAsync(target, cancellationToken);
            if (lookup is null) return DocumentErrors.NotFound(target);
            return await RefreshExistingAsync(lookup, command, cancellationToken);
        }

        var fullTarget = Path.GetFullPath(target);

        // 2. Re-open existing project directory (or refresh if --refresh)
        if (workingDirectoryProvider.DirectoryExists(fullTarget))
        {
            var existing = await repository.FindDocumentInDirectoryAsync(fullTarget, cancellationToken);
            if (existing is null) return DocumentErrors.ProjectNotFound(fullTarget);

            if (command.Refresh)
                return await RefreshExistingAsync(existing, command, cancellationToken);

            var existingDoc = existing.Document;
            return BuildIngestResponse(
                existingDoc, existing.StorageDirectory, existing.DatabasePath, "cached",
                reopenedExisting: true, previousFigureCount: null, command.Quiet, command.Verbose);
        }

        // 3. Project mode: move PDF into project directory & persist DB
        if (command.ProjectMode)
            return await IngestAsProjectAsync(fullTarget, command, cancellationToken);

        // 4. Standard ingest: reference source PDF in-place without copying
        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            fullTarget, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var analysis = analysisResult.Value;
        var docId = idGenerator.NewDocumentId();
        string baseWorkspaceRoot = string.IsNullOrWhiteSpace(command.OutputDirectory)
            ? Path.Combine(workingDirectoryProvider.GetCurrentDirectory(), ".meloberry", "melomg")
            : workingDirectoryProvider.NormalizeDirectoryPath(command.OutputDirectory);
        var workspaceDir = Path.Combine(baseWorkspaceRoot, docId);

        var workspaceInit = await pdfEngine.InitializeWorkspaceAsync(workspaceDir, cancellationToken);
        if (!workspaceInit.IsSuccess) return workspaceInit.Error!;

        var resolvedTitle = string.IsNullOrWhiteSpace(command.Title) ? analysis.SuggestedTitle : command.Title.Trim();
        var docResult = PdfDocument.Create(
            docId, resolvedTitle, fullTarget, fullTarget, analysis.Sha256Hash, workspaceDir,
            analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold, TimeProvider.System.GetUtcNow(),
            analysis.Pages, WorkspaceMode.Standard, command.RetainDatabase, analysis.ScannedVocabPrefixes);
        if (!docResult.IsSuccess) return docResult.Error!;

        var document = docResult.Value;
        var popRes = ModelResolutionHelper.PopulateFigures(document, analysis.Candidates, idGenerator);
        if (!popRes.IsSuccess) return popRes.Error!;

        await repository.CreateDocumentAsync(document, cancellationToken);

        return BuildIngestResponse(
            document, document.WorkingDirectory, Path.Combine(document.WorkingDirectory, $"{document.Id}.db"),
            analysis.InferenceEngine, reopenedExisting: false, previousFigureCount: null, command.Quiet, command.Verbose);
    }

    private static IngestPdfResponse BuildIngestResponse(
        PdfDocument document,
        string workspaceDirectory,
        string? databasePath,
        string inferenceEngine,
        bool reopenedExisting,
        int? previousFigureCount,
        bool quiet,
        bool verbose)
    {
        var anomalyReport = FigureSequenceAnalyzer.Analyze(document);
        var anomalies = anomalyReport.Anomalies.Count > 0
            ? anomalyReport.Anomalies.Select(a => new FigureAnomalyDto(a.Code, a.Severity, a.Message, a.ExpectedNumber, a.SuspectPages, a.FigureIds)).ToList()
            : null;
        var suspectPages = anomalyReport.SuspectPages.Count > 0 ? anomalyReport.SuspectPages : null;

        return new IngestPdfResponse(
            document.Id,
            document.Title,
            workspaceDirectory,
            document.StoredPdfPath,
            databasePath,
            quiet ? null : document.Sha256Hash,
            document.PageCount,
            document.Figures.Count,
            DocumentVocabularyDto.FromDomain(document.Vocabulary),
            quiet ? null : ModelResolutionHelper.ToOutlines(document.Figures),
            document.ModelName,
            document.ConfidenceThreshold,
            inferenceEngine,
            document.RetainDatabase,
            document.Mode == WorkspaceMode.Project ? "project" : "standard",
            reopenedExisting,
            previousFigureCount,
            anomalies,
            suspectPages,
            verbose ? ModelResolutionHelper.ToFulls(document.Figures) : null);
    }

    private async Task<Result<IngestPdfResponse>> IngestAsProjectAsync(
        string fullPdfPath, IngestPdfCommand command, CancellationToken cancellationToken)
    {
        string projectDir = !string.IsNullOrWhiteSpace(command.OutputDirectory)
            ? workingDirectoryProvider.NormalizeDirectoryPath(command.OutputDirectory)
            : Path.Combine(workingDirectoryProvider.GetCurrentDirectory(), $"project_{SanitizeProjectFolderSegment(Path.GetFileNameWithoutExtension(fullPdfPath))}");

        string expectedDestPdf = Path.Combine(projectDir, Path.GetFileName(fullPdfPath));
        if (!string.Equals(fullPdfPath, expectedDestPdf, StringComparison.OrdinalIgnoreCase) &&
            workingDirectoryProvider.FileExists(expectedDestPdf))
        {
            return DocumentErrors.ProjectPdfAlreadyExists(expectedDestPdf);
        }

        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            fullPdfPath, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var moveResult = await pdfEngine.MovePdfToProjectDirectoryAsync(fullPdfPath, projectDir, cancellationToken);
        if (!moveResult.IsSuccess) return moveResult.Error!;

        string movedPdfPath = moveResult.Value;
        bool movedFromSource = !string.Equals(fullPdfPath, movedPdfPath, StringComparison.OrdinalIgnoreCase);

        void TryRollbackMovedPdf()
        {
            if (!movedFromSource) return;
            try
            {
                if (File.Exists(movedPdfPath) && !File.Exists(fullPdfPath))
                    File.Move(movedPdfPath, fullPdfPath);
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
                analysis.Pages, WorkspaceMode.Project, retainDatabase: true, analysis.ScannedVocabPrefixes);
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

            return BuildIngestResponse(
                document, projectDir, Path.Combine(projectDir, $"{document.Id}.db"),
                analysis.InferenceEngine, reopenedExisting: false, previousFigureCount: null, command.Quiet, command.Verbose);
        }
        catch
        {
            TryRollbackMovedPdf();
            throw;
        }
    }

    private async Task<Result<IngestPdfResponse>> RefreshExistingAsync(
        DocumentLookupContext lookup, IngestPdfCommand command, CancellationToken cancellationToken)
    {
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
                page, c.Confidence, c.Caption, c.Label, c.Source, c.CaptionBoundingBox,
                labelMasks: null, vocabPrefix: c.VocabPrefix, figureNumber: c.FigureNumber);
            if (!figResult.IsSuccess) return figResult.Error!;
            createdFigures.Add(figResult.Value);
        }

        doc.SetScannedVocabPrefixes(analysis.ScannedVocabPrefixes);
        var replaceResult = doc.ReplaceDetectedFigures(createdFigures, analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold);
        if (!replaceResult.IsSuccess) return replaceResult.Error!;

        if (!string.IsNullOrWhiteSpace(command.Title))
        {
            var titleRes = doc.SetTitle(command.Title);
            if (!titleRes.IsSuccess) return titleRes.Error!;
        }

        await repository.SaveDocumentAsync(doc, cancellationToken);

        return BuildIngestResponse(
            doc, lookup.StorageDirectory, lookup.DatabasePath,
            analysis.InferenceEngine, reopenedExisting: false, previousFigureCount: previousCount, command.Quiet, command.Verbose);
    }

    private static string SanitizeProjectFolderSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "document";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "document" : cleaned;
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

public sealed record CategoryCountDto(string Category, int Count);

public sealed record GetTargetQuery(
    string Address,
    ViewMode View = ViewMode.Outline,
    int? PageNumber = null,
    string? Category = null,
    double? MinConfidence = null);

public sealed record GetTargetResponse(
    string Target,
    string View,
    string DocumentId,
    string? Title = null,
    int? PageCount = null,
    int? TotalFigures = null,
    DocumentVocabularyDto? Vocab = null,
    string? ModelName = null,
    double? ConfidenceThreshold = null,
    string? Sha256Hash = null,
    string? StorageDirectory = null,
    IReadOnlyList<PdfPageInfo>? Pages = null,
    IReadOnlyList<CategoryCountDto>? CategoryBreakdown = null,
    IReadOnlyList<FigureOutlineDto>? Figures = null,
    IReadOnlyList<FigureFullDto>? FullFigures = null,
    IReadOnlyList<FigureIdsDto>? Ids = null,
    FigureDetailResponse? Figure = null);

public sealed class GetTargetHandler(AddressResolver resolver, IFigureLabelDetector labelDetector)
{
    public async Task<Result<GetTargetResponse>> HandleAsync(GetTargetQuery query, CancellationToken cancellationToken = default)
    {
        var resolvedRes = await resolver.ResolveTargetAsync(query.Address, cancellationToken);
        if (!resolvedRes.IsSuccess) return resolvedRes.Error!;

        var resolved = resolvedRes.Value;
        var doc = resolved.DocumentContext.Document;
        string viewStr = query.View.ToString().ToLowerInvariant();

        // Single figure target (<figId> or <docId>#<num|fN>)
        if (resolved.Address.Kind is AddressKind.FigureId or AddressKind.FigureIndex or AddressKind.FigureNumber &&
            resolved.Figures.Count == 1)
        {
            var fig = resolved.Figures[0];
            if (query.View == ViewMode.Ids)
            {
                return new GetTargetResponse(
                    "figure", viewStr, doc.Id,
                    Ids: ModelResolutionHelper.ToIds([fig]));
            }

            if (query.View == ViewMode.Outline)
            {
                return new GetTargetResponse(
                    "figure", viewStr, doc.Id,
                    Figures: ModelResolutionHelper.ToOutlines([fig]));
            }

            var page = doc.GetPage(fig.PageNumber) ?? new PdfPageInfo(fig.PageNumber, 612, 792);
            var (savedMasks, detectedLabels) = await FigureUseCaseHelpers.LoadMasksAndDetectedLabelsAsync(
                new FigureOwnerContext(doc, fig, resolved.DocumentContext.StorageDirectory, resolved.DocumentContext.DatabasePath),
                labelDetector,
                cancellationToken);

            var detail = new FigureDetailResponse(
                fig.Id, doc.Id, fig.Index, fig.PageNumber, fig.PageFigureIndex,
                fig.Category.ToString().ToLowerInvariant(), fig.Confidence, DocumentFigure.FormatSourceTag(fig.Source),
                fig.Label, fig.VocabPrefix, fig.FigureNumber, fig.Caption, fig.BoundingBox, fig.CaptionBoundingBox,
                fig.BoundingBox.ToNormalized(page.WidthPts, page.HeightPts), page.WidthPts, page.HeightPts,
                fig.BoundingBox.ToPixelRect(ExtractionOptions.DefaultDpi, ExtractionOptions.DefaultPaddingPx, page.WidthPts, page.HeightPts),
                fig.LastExtractedPath, savedMasks, detectedLabels);

            return new GetTargetResponse("figure", viewStr, doc.Id, Figure: detail);
        }

        // Document / page / sub-panel list target
        FigureCategory? filterCategory = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            if (!DocumentFigure.TryParseCategory(query.Category, out var parsedCat))
                return FigureErrors.InvalidCategory(query.Category);
            filterCategory = parsedCat;
        }

        if (query.PageNumber.HasValue && doc.GetPage(query.PageNumber.Value) is null)
            return DocumentErrors.InvalidPageNumber(query.PageNumber.Value, doc.PageCount);

        if (query.MinConfidence.HasValue && (query.MinConfidence.Value < 0 || query.MinConfidence.Value > 1))
            return FigureErrors.InvalidConfidence(query.MinConfidence.Value);

        var filtered = resolved.Figures
            .Where(f => (!query.PageNumber.HasValue || f.PageNumber == query.PageNumber.Value) &&
                        (!filterCategory.HasValue || f.Category == filterCategory.Value) &&
                        (!query.MinConfidence.HasValue || f.Confidence >= query.MinConfidence.Value))
            .ToList();

        if (query.View == ViewMode.Ids)
        {
            return new GetTargetResponse(
                "document", viewStr, doc.Id,
                Ids: ModelResolutionHelper.ToIds(filtered));
        }

        var vocabDto = DocumentVocabularyDto.FromDomain(doc.Vocabulary);

        if (query.View == ViewMode.Outline)
        {
            return new GetTargetResponse(
                "document", viewStr, doc.Id,
                Title: doc.Title,
                PageCount: doc.PageCount,
                TotalFigures: doc.Figures.Count,
                Vocab: vocabDto,
                Figures: ModelResolutionHelper.ToOutlines(filtered));
        }

        var breakdown = doc.Figures
            .GroupBy(f => f.Category)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new CategoryCountDto(g.Key.ToString().ToLowerInvariant(), g.Count()))
            .ToList();

        return new GetTargetResponse(
            "document", viewStr, doc.Id,
            Title: doc.Title,
            PageCount: doc.PageCount,
            TotalFigures: doc.Figures.Count,
            Vocab: vocabDto,
            ModelName: doc.ModelName,
            ConfidenceThreshold: doc.ConfidenceThreshold,
            Sha256Hash: doc.Sha256Hash,
            StorageDirectory: resolved.DocumentContext.StorageDirectory,
            Pages: doc.Pages,
            CategoryBreakdown: breakdown,
            FullFigures: ModelResolutionHelper.ToFulls(filtered));
    }
}

public sealed record DeleteTargetCommand(string Address);

public sealed record DeleteTargetResponse(string DeletedId, string EntityKind, string? DocumentId = null, int? RemainingFigures = null);

public sealed class DeleteTargetHandler(AddressResolver resolver, IDocumentWorkspaceRepository repository)
{
    public async Task<Result<DeleteTargetResponse>> HandleAsync(DeleteTargetCommand command, CancellationToken cancellationToken = default)
    {
        var parsedRes = AddressResolver.Parse(command.Address);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;

        if (parsedRes.Value.Kind == AddressKind.Document)
        {
            var lookup = await repository.FindDocumentAsync(parsedRes.Value.DocumentId!, cancellationToken);
            if (lookup is null) return DocumentErrors.NotFound(parsedRes.Value.DocumentId!);

            await repository.DeleteDocumentAsync(lookup.Document.Id, cancellationToken);
            return new DeleteTargetResponse(lookup.Document.Id, "document");
        }

        var ownerRes = await resolver.ResolveSingleFigureAsync(command.Address, cancellationToken);
        if (!ownerRes.IsSuccess) return ownerRes.Error!;

        var owner = ownerRes.Value;
        var delRes = owner.Document.DeleteFigure(owner.Figure.Id);
        if (!delRes.IsSuccess) return delRes.Error!;

        await repository.SaveDocumentAsync(owner.Document, cancellationToken);
        return new DeleteTargetResponse(owner.Figure.Id, "figure", owner.Document.Id, owner.Document.Figures.Count);
    }
}
