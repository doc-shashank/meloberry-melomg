using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public enum FigureCategory { Figure, Diagram, Chart }

public enum DetectionSource { OnnxModel, PageLayout, Hybrid, Manual }

public sealed class DocumentFigure : IIndexedEntity
{
    private readonly List<FigureLabelMask> _labelMasks;

    private DocumentFigure(
        string id, string documentId, int index, int pageNumber, int pageFigureIndex,
        FigureCategory category, BoundingBox boundingBox, double confidence,
        string? caption, string label, DetectionSource source,
        string? lastExtractedPath, BoundingBox? captionBoundingBox, List<FigureLabelMask> labelMasks,
        string? vocabPrefix, string? figureNumber, string? normalizedNumber)
    {
        Id = id; DocumentId = documentId; Index = index; PageNumber = pageNumber;
        PageFigureIndex = pageFigureIndex; Category = category; BoundingBox = boundingBox;
        Confidence = confidence; Caption = caption; Label = label; Source = source;
        LastExtractedPath = lastExtractedPath; CaptionBoundingBox = captionBoundingBox;
        _labelMasks = labelMasks;
        VocabPrefix = vocabPrefix; FigureNumber = figureNumber; NormalizedNumber = normalizedNumber;
    }

    public string Id { get; private set; }
    public string DocumentId { get; private set; }
    public int Index { get; private set; }
    public int PageNumber { get; private set; }
    public int PageFigureIndex { get; private set; }
    public FigureCategory Category { get; private set; }
    public BoundingBox BoundingBox { get; private set; }
    public double Confidence { get; private set; }
    public string? Caption { get; private set; }
    public string Label { get; private set; }
    public DetectionSource Source { get; private set; }
    public string? LastExtractedPath { get; private set; }
    public BoundingBox? CaptionBoundingBox { get; private set; }
    public string? VocabPrefix { get; private set; }
    public string? FigureNumber { get; private set; }
    public string? NormalizedNumber { get; private set; }
    public IReadOnlyList<FigureLabelMask> LabelMasks => _labelMasks;

    public void SetIndexInternal(int index) => Index = index;
    internal void SetPageFigureIndexInternal(int pageFigureIndex) => PageFigureIndex = pageFigureIndex;

    public static Result<DocumentFigure> Create(
        string id, string documentId, int pageNumber, FigureCategory category,
        BoundingBox boundingBox, PdfPageInfo page, double confidence,
        string? caption, string? label, DetectionSource source,
        BoundingBox? captionBoundingBox = null, IEnumerable<FigureLabelMask>? labelMasks = null,
        string? vocabPrefix = null, string? figureNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        if (pageNumber != page.PageNumber)
            return DocumentErrors.InvalidPageNumber(pageNumber, page.PageNumber);

        var boundsCheck = boundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
        if (!boundsCheck.IsSuccess) return boundsCheck.Error!;

        if (captionBoundingBox is not null)
        {
            var capCheck = captionBoundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
            if (!capCheck.IsSuccess) return capCheck.Error!;
        }

        if (confidence < 0.0 || confidence > 1.0 || double.IsNaN(confidence))
            return FigureErrors.InvalidConfidence(confidence);

        var normalizedCaption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        var (resolvedPrefix, resolvedNum, resolvedNorm, parsedShortLabel) = ResolveVocabularyMetadata(
            vocabPrefix, figureNumber, normalizedCaption, label);

        var resolvedLabel = !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : parsedShortLabel ?? $"{category} p{pageNumber}";

        var maskList = labelMasks is null ? [] : labelMasks.OrderBy(m => m.Index).ToList();

        return new DocumentFigure(
            id.Trim(), documentId.Trim(), 1, pageNumber, 1, category, boundingBox,
            Math.Round(confidence, 4), normalizedCaption, resolvedLabel, source, null, captionBoundingBox, maskList,
            resolvedPrefix, resolvedNum, resolvedNorm);
    }

    public static DocumentFigure Rehydrate(
        string id, string documentId, int index, int pageNumber, int pageFigureIndex,
        FigureCategory category, BoundingBox boundingBox, double confidence,
        string? caption, string label, DetectionSource source, string? lastExtractedPath,
        BoundingBox? captionBoundingBox = null, IEnumerable<FigureLabelMask>? labelMasks = null,
        string? vocabPrefix = null, string? figureNumber = null, string? normalizedNumber = null)
    {
        var (resolvedPrefix, resolvedNum, resolvedNorm, _) = ResolveVocabularyMetadata(
            vocabPrefix, figureNumber, caption, label);
        if (!string.IsNullOrWhiteSpace(normalizedNumber))
            resolvedNorm = FigureNumberNormalizer.NormalizeLookupNumber(normalizedNumber);

        return new(
            id, documentId, index, pageNumber, pageFigureIndex, category, boundingBox, confidence,
            caption, label, source, lastExtractedPath, captionBoundingBox,
            labelMasks is null ? [] : [.. labelMasks.OrderBy(m => m.Index)],
            resolvedPrefix, resolvedNum, resolvedNorm);
    }

    private static (string? Prefix, string? Number, string? Normalized, string? ShortLabel) ResolveVocabularyMetadata(
        string? explicitPrefix, string? explicitNumber, string? caption, string? label)
    {
        string? prefix = string.IsNullOrWhiteSpace(explicitPrefix) ? null : explicitPrefix.Trim();
        string? num = string.IsNullOrWhiteSpace(explicitNumber) ? null : explicitNumber.Trim();
        string? shortLabel = null;

        if (FigureNumberNormalizer.TryParseFigureCaption(caption, out var capParsed))
        {
            prefix ??= capParsed.Prefix;
            num ??= capParsed.Number;
            shortLabel = capParsed.ShortLabel;
        }
        else if (FigureNumberNormalizer.TryParseFigureCaption(label, out var lblParsed))
        {
            prefix ??= lblParsed.Prefix;
            num ??= lblParsed.Number;
            shortLabel = lblParsed.ShortLabel;
        }

        string? norm = string.IsNullOrWhiteSpace(num) ? null : FigureNumberNormalizer.NormalizeLookupNumber(num);
        if (string.IsNullOrWhiteSpace(norm))
        {
            num = null;
            norm = null;
        }

        return (prefix, num, norm, shortLabel);
    }

    public Result UpdateBoundingBox(BoundingBox newBox, PdfPageInfo page)
    {
        var check = newBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
        if (!check.IsSuccess) return check.Error!;
        BoundingBox = newBox;
        return Result.Success();
    }

    public Result SetCaptionBoundingBox(BoundingBox? captionBox, PdfPageInfo page)
    {
        if (captionBox is not null)
        {
            var check = captionBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
            if (!check.IsSuccess) return check.Error!;
        }
        CaptionBoundingBox = captionBox;
        return Result.Success();
    }

    public Result<FigureLabelMask> AddLabelMask(
        BoundingBox box, PdfPageInfo page, string? labelText = null,
        ObfuscationStyle style = ObfuscationStyle.Solid, string? colorHex = null)
    {
        var maskRes = FigureLabelMask.Create(_labelMasks.Count + 1, box, page, labelText, style, colorHex);
        if (!maskRes.IsSuccess) return maskRes.Error!;
        _labelMasks.Add(maskRes.Value);
        return maskRes.Value;
    }

    public Result RemoveLabelMask(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _labelMasks.Count)
            return FigureErrors.MaskNotFound(oneBasedIndex, _labelMasks.Count);

        _labelMasks.RemoveAt(oneBasedIndex - 1);
        for (int i = 0; i < _labelMasks.Count; i++)
            _labelMasks[i] = _labelMasks[i] with { Index = i + 1 };
        return Result.Success();
    }

    public int ClearLabelMasks()
    {
        int removed = _labelMasks.Count;
        _labelMasks.Clear();
        return removed;
    }

    public BoundingBox GetEffectiveCropBox(bool includeCaption, PdfPageInfo page)
    {
        if (!includeCaption || CaptionBoundingBox is null) return BoundingBox;
        double left = Math.Clamp(Math.Min(BoundingBox.X, CaptionBoundingBox.X), 0, Math.Max(0, page.WidthPts - 1));
        double top = Math.Clamp(Math.Min(BoundingBox.Y, CaptionBoundingBox.Y), 0, Math.Max(0, page.HeightPts - 1));
        double right = Math.Clamp(Math.Max(BoundingBox.Right, CaptionBoundingBox.Right), left + 1, page.WidthPts);
        double bottom = Math.Clamp(Math.Max(BoundingBox.Bottom, CaptionBoundingBox.Bottom), top + 1, page.HeightPts);
        return new BoundingBox(Math.Round(left, 2), Math.Round(top, 2), Math.Round(right - left, 2), Math.Round(bottom - top, 2));
    }

    public Result SetCaption(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return FigureErrors.EmptyCaption();
        Caption = caption.Trim();
        if (FigureNumberNormalizer.TryParseFigureCaption(Caption, out var parsed))
        {
            VocabPrefix = parsed.Prefix;
            FigureNumber = parsed.Number;
            NormalizedNumber = parsed.NormalizedNumber;
        }
        return Result.Success();
    }

    public Result SetLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return FigureErrors.EmptyLabel();
        Label = label.Trim();
        if (FigureNumberNormalizer.TryParseFigureCaption(Label, out var parsed))
        {
            VocabPrefix ??= parsed.Prefix;
            if (string.IsNullOrWhiteSpace(FigureNumber))
            {
                FigureNumber = parsed.Number;
                NormalizedNumber = parsed.NormalizedNumber;
            }
        }
        return Result.Success();
    }

    public Result SetFigureNumber(string? figureNumber, string? vocabPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(figureNumber))
        {
            FigureNumber = null;
            NormalizedNumber = null;
            if (!string.IsNullOrWhiteSpace(vocabPrefix))
                VocabPrefix = vocabPrefix.Trim();
            return Result.Success();
        }

        string trimmed = figureNumber.Trim();
        string norm = FigureNumberNormalizer.NormalizeLookupNumber(trimmed);
        if (string.IsNullOrWhiteSpace(norm))
            return FigureErrors.EmptyLabel();

        if (!string.IsNullOrWhiteSpace(vocabPrefix))
            VocabPrefix = vocabPrefix.Trim();

        FigureNumber = trimmed;
        NormalizedNumber = norm;
        return Result.Success();
    }

    public void SetCategory(FigureCategory category) => Category = category;

    public void RecordExtraction(string extractedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedPath);
        LastExtractedPath = extractedPath.Trim();
    }

    public static bool TryParseCategory(string raw, out FigureCategory category)
    {
        (bool ok, category) = raw.Trim().ToLowerInvariant() switch
        {
            "figure" or "fig" or "image" or "img" or "illustration" or "plate" => (true, FigureCategory.Figure),
            "diagram" or "flowchart" or "schematic" or "scheme" => (true, FigureCategory.Diagram),
            "chart" or "plot" or "graph" => (true, FigureCategory.Chart),
            _ => (false, default(FigureCategory))
        };
        return ok;
    }

    public static bool TryParseDetectionSource(string raw, out DetectionSource source)
    {
        (bool ok, source) = raw.Trim().ToLowerInvariant() switch
        {
            "onnx" or "onnxmodel" => (true, DetectionSource.OnnxModel),
            "page-layout" or "pagelayout" or "pdf-xobject" or "pdfxobject" => (true, DetectionSource.PageLayout),
            "hybrid" => (true, DetectionSource.Hybrid),
            "manual" => (true, DetectionSource.Manual),
            _ => (false, DetectionSource.OnnxModel)
        };
        return ok;
    }

    public static string FormatSourceTag(DetectionSource source) => source switch
    {
        DetectionSource.PageLayout => "page-layout",
        DetectionSource.Hybrid => "hybrid",
        DetectionSource.Manual => "manual",
        _ => "onnx"
    };
}
