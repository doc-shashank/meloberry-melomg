using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public enum WorkspaceMode { Standard, Project }

public sealed class PdfDocument
{
    private readonly List<PdfPageInfo> _pages;
    private readonly List<DocumentFigure> _figures;
    private readonly List<string> _scannedVocabPrefixes;

    private PdfDocument(
        string id, string title, string sourcePdfPath, string storedPdfPath, string sha256Hash,
        string workingDirectory, string modelName, double confidenceThreshold, DateTimeOffset ingestedAtUtc,
        string? activeFigureId, WorkspaceMode mode, bool retainDatabase,
        List<PdfPageInfo> pages, List<DocumentFigure> figures, List<string> scannedVocabPrefixes)
    {
        Id = id; Title = title; SourcePdfPath = sourcePdfPath; StoredPdfPath = storedPdfPath;
        Sha256Hash = sha256Hash; WorkingDirectory = workingDirectory; ModelName = modelName;
        ConfidenceThreshold = confidenceThreshold; IngestedAtUtc = ingestedAtUtc;
        ActiveFigureId = activeFigureId; Mode = mode;
        RetainDatabase = mode == WorkspaceMode.Project || retainDatabase;
        _pages = pages; _figures = figures; _scannedVocabPrefixes = scannedVocabPrefixes;
        Vocabulary = DocumentVocabulary.BuildFrom(_figures, _scannedVocabPrefixes);
    }

    public string Id { get; private set; }
    public string Title { get; private set; }
    public string SourcePdfPath { get; private set; }
    public string StoredPdfPath { get; private set; }
    public string Sha256Hash { get; private set; }
    public int PageCount => _pages.Count;
    public string WorkingDirectory { get; private set; }
    public string ModelName { get; private set; }
    public double ConfidenceThreshold { get; private set; }
    public DateTimeOffset IngestedAtUtc { get; private set; }
    public string? ActiveFigureId { get; private set; }
    public WorkspaceMode Mode { get; private set; }
    public bool RetainDatabase { get; private set; }
    public DocumentVocabulary Vocabulary { get; private set; }
    public IReadOnlyList<PdfPageInfo> Pages => _pages;
    public IReadOnlyList<DocumentFigure> Figures => _figures;

    public static Result<PdfDocument> Create(
        string id, string title, string sourcePdfPath, string storedPdfPath, string sha256Hash,
        string workingDirectory, string modelName, double confidenceThreshold, DateTimeOffset ingestedAtUtc,
        IReadOnlyList<PdfPageInfo> pages, WorkspaceMode mode = WorkspaceMode.Standard, bool retainDatabase = false,
        IEnumerable<string>? scannedVocabPrefixes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (string.IsNullOrWhiteSpace(workingDirectory))
            return DocumentErrors.InvalidWorkingDirectory(workingDirectory ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
            return DocumentErrors.EmptyTitle();
        if (confidenceThreshold < 0.05 || confidenceThreshold > 0.99 || double.IsNaN(confidenceThreshold))
            return DocumentErrors.InvalidConfidenceThreshold(confidenceThreshold);
        if (pages.Count == 0)
            return DocumentErrors.InvalidPdfFormat(sourcePdfPath, "Document contains 0 pages.");

        return new PdfDocument(
            id.Trim(), title.Trim(), sourcePdfPath.Trim(), storedPdfPath.Trim(), sha256Hash.Trim(),
            workingDirectory.Trim(), modelName.Trim(), Math.Round(confidenceThreshold, 4), ingestedAtUtc,
            null, mode, retainDatabase, pages.OrderBy(p => p.PageNumber).ToList(), [],
            scannedVocabPrefixes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? []);
    }

    public static PdfDocument Rehydrate(
        string id, string title, string sourcePdfPath, string storedPdfPath, string sha256Hash,
        string workingDirectory, string modelName, double confidenceThreshold, DateTimeOffset ingestedAtUtc,
        string? activeFigureId, IEnumerable<PdfPageInfo> pages, IEnumerable<DocumentFigure> figures,
        WorkspaceMode mode = WorkspaceMode.Standard, bool retainDatabase = false,
        IEnumerable<string>? scannedVocabPrefixes = null)
    {
        var figureList = figures.ToList();
        IndexedListReorderer.SortInPlace(figureList);
        return new PdfDocument(
            id, title, sourcePdfPath, storedPdfPath, sha256Hash, workingDirectory, modelName,
            confidenceThreshold, ingestedAtUtc, activeFigureId, mode, retainDatabase,
            pages.OrderBy(p => p.PageNumber).ToList(), figureList,
            scannedVocabPrefixes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? []);
    }

    public void EnableDatabaseRetention() => RetainDatabase = true;

    public void RefreshVocabulary() =>
        Vocabulary = DocumentVocabulary.BuildFrom(_figures, _scannedVocabPrefixes);

    public void SetScannedVocabPrefixes(IEnumerable<string>? prefixes)
    {
        _scannedVocabPrefixes.Clear();
        if (prefixes is not null)
        {
            foreach (var p in prefixes.Where(s => !string.IsNullOrWhiteSpace(s)))
                _scannedVocabPrefixes.Add(p.Trim());
        }
        RefreshVocabulary();
    }

    public Result SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return DocumentErrors.EmptyTitle();
        Title = title.Trim();
        return Result.Success();
    }

    public PdfPageInfo? GetPage(int pageNumber) => _pages.FirstOrDefault(p => p.PageNumber == pageNumber);

    public Result<DocumentFigure> AddFigure(
        string figureId, int pageNumber, FigureCategory category, BoundingBox boundingBox, double confidence,
        string? caption, string? label, DetectionSource source, int? targetIndex = null,
        BoundingBox? captionBoundingBox = null, string? vocabPrefix = null, string? figureNumber = null)
    {
        var page = GetPage(pageNumber);
        if (page is null) return DocumentErrors.InvalidPageNumber(pageNumber, PageCount);

        var figResult = DocumentFigure.Create(
            figureId, Id, pageNumber, category, boundingBox, page, confidence, caption, label, source,
            captionBoundingBox, labelMasks: null, vocabPrefix: vocabPrefix, figureNumber: figureNumber);
        if (!figResult.IsSuccess) return figResult.Error!;

        var figure = figResult.Value;
        var insertResult = IndexedListReorderer.InsertAt(_figures, figure, targetIndex);
        if (!insertResult.IsSuccess) return insertResult.Error!;

        RecalculatePageFigureIndices();
        RefreshVocabulary();
        ActiveFigureId ??= figure.Id;
        return figure;
    }

    public Result ReplaceDetectedFigures(IEnumerable<DocumentFigure> detectedFigures, string modelName, double confidenceThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (confidenceThreshold < 0.05 || confidenceThreshold > 0.99 || double.IsNaN(confidenceThreshold))
            return DocumentErrors.InvalidConfidenceThreshold(confidenceThreshold);

        _figures.Clear();
        int idx = 1;
        foreach (var fig in detectedFigures)
        {
            fig.SetIndexInternal(idx++);
            _figures.Add(fig);
        }

        RecalculatePageFigureIndices();
        RefreshVocabulary();
        ModelName = modelName.Trim();
        ConfidenceThreshold = Math.Round(confidenceThreshold, 4);
        ActiveFigureId = _figures.FirstOrDefault()?.Id;
        return Result.Success();
    }

    public Result ActivateFigure(string figureId)
    {
        var fig = FindFigure(figureId);
        if (fig is null) return FigureErrors.NotFound(figureId);
        ActiveFigureId = fig.Id;
        return Result.Success();
    }

    public Result ReorderFigure(string figureId, int targetIndex, ReorderMode mode)
    {
        var res = IndexedListReorderer.Reorder(_figures, figureId, targetIndex, mode, FigureErrors.NotFound(figureId));
        if (!res.IsSuccess) return res.Error!;
        RecalculatePageFigureIndices();
        return Result.Success();
    }

    public Result DeleteFigure(string figureId)
    {
        var res = IndexedListReorderer.RemoveAndShiftUp(_figures, figureId, FigureErrors.NotFound(figureId));
        if (!res.IsSuccess) return res.Error!;

        RecalculatePageFigureIndices();
        RefreshVocabulary();
        if (string.Equals(ActiveFigureId, figureId, StringComparison.Ordinal))
            ActiveFigureId = _figures.FirstOrDefault()?.Id;
        return Result.Success();
    }

    public DocumentFigure? FindFigure(string figureId) =>
        _figures.FirstOrDefault(f => string.Equals(f.Id, figureId, StringComparison.Ordinal));

    public DocumentFigure? FindFigureByIndex(int oneBasedIndex) =>
        _figures.FirstOrDefault(f => f.Index == oneBasedIndex);

    public (IReadOnlyList<DocumentFigure> Matches, bool IsSubPanelExpansion) FindFiguresByNumber(string rawNumber)
    {
        string norm = FigureNumberNormalizer.NormalizeLookupNumber(rawNumber);
        if (string.IsNullOrEmpty(norm))
            return ([], false);

        var exact = _figures
            .Where(f => string.Equals(f.NormalizedNumber, norm, StringComparison.Ordinal))
            .ToList();
        if (exact.Count > 0)
            return (exact, false);

        var subPanels = _figures
            .Where(f => f.NormalizedNumber is { } fn &&
                        fn.Length > norm.Length &&
                        fn.StartsWith(norm, StringComparison.Ordinal) &&
                        char.IsLetter(fn[norm.Length]))
            .ToList();

        return (subPanels, subPanels.Count > 0);
    }

    public void RecalculatePageFigureIndices()
    {
        var pageCounts = new Dictionary<int, int>();
        foreach (var fig in _figures)
        {
            pageCounts.TryGetValue(fig.PageNumber, out int current);
            int next = current + 1;
            pageCounts[fig.PageNumber] = next;
            fig.SetPageFigureIndexInternal(next);
        }
    }
}
