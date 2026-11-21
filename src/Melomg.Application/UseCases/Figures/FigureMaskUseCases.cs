using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;

namespace Melomg.Application.UseCases.Figures;

internal static class FigureUseCaseHelpers
{
    public static async Task<(List<FigureMaskDto> SavedMasks, List<DetectedLabelDto> DetectedLabels)> LoadMasksAndDetectedLabelsAsync(
        FigureOwnerContext owner, IFigureLabelDetector labelDetector, CancellationToken cancellationToken)
    {
        var fig = owner.Figure;
        var savedDtos = fig.LabelMasks
            .Select(m => new FigureMaskDto(m.Index, m.BoundingBox, m.LabelText, FigureLabelMask.FormatStyle(m.Style), m.ColorHex))
            .ToList();

        var detectedDtos = new List<DetectedLabelDto>();
        if (File.Exists(owner.Document.StoredPdfPath))
        {
            var labelsRes = await labelDetector.DetectLabelsInRegionAsync(
                owner.Document.StoredPdfPath, fig.PageNumber, fig.BoundingBox, cancellationToken);
            if (labelsRes.IsSuccess)
            {
                int idx = 1;
                foreach (var lbl in labelsRes.Value)
                    detectedDtos.Add(new DetectedLabelDto(idx++, lbl.Text, lbl.BoundingBox, Math.Round(lbl.FontSize, 2)));
            }
        }
        return (savedDtos, detectedDtos);
    }

    public static async Task<Result<TResponse>> MutateFigureConditionalAsync<TResponse>(
        IDocumentWorkspaceRepository repository, string figureId,
        Func<FigureOwnerContext, Result<(TResponse Response, bool ShouldSave)>> mutate,
        CancellationToken cancellationToken)
    {
        var owner = await repository.FindFigureOwnerAsync(figureId, cancellationToken);
        if (owner is null) return FigureErrors.NotFound(figureId);

        var res = mutate(owner);
        if (!res.IsSuccess) return res.Error!;

        if (res.Value.ShouldSave)
            await repository.SaveDocumentAsync(owner.Document, cancellationToken);

        return res.Value.Response;
    }

    public static Task<Result<TResponse>> MutateFigureAsync<TResponse>(
        IDocumentWorkspaceRepository repository, string figureId,
        Func<FigureOwnerContext, Result<TResponse>> mutate,
        CancellationToken cancellationToken) =>
        MutateFigureConditionalAsync<TResponse>(repository, figureId, owner =>
        {
            var res = mutate(owner);
            return res.IsSuccess ? (res.Value, true) : res.Error!;
        }, cancellationToken);
}

public sealed record FigureMaskDto(int Index, BoundingBox BoundingBoxPts, string? LabelText, string Style, string? ColorHex);

public sealed record DetectedLabelDto(int Index, string Text, BoundingBox BoundingBoxPts, double FontSize);

public sealed record ListFigureMasksQuery(string FigureId);

public sealed record ListFigureMasksResponse(
    string FigureId, int PageNumber, IReadOnlyList<FigureMaskDto> SavedMasks, IReadOnlyList<DetectedLabelDto> DetectedLabels);

public sealed class ListFigureMasksHandler(IDocumentWorkspaceRepository repository, IFigureLabelDetector labelDetector)
{
    public async Task<Result<ListFigureMasksResponse>> HandleAsync(ListFigureMasksQuery query, CancellationToken cancellationToken = default)
    {
        var owner = await repository.FindFigureOwnerAsync(query.FigureId, cancellationToken);
        if (owner is null) return FigureErrors.NotFound(query.FigureId);

        var (saved, detected) = await FigureUseCaseHelpers.LoadMasksAndDetectedLabelsAsync(owner, labelDetector, cancellationToken);
        return new ListFigureMasksResponse(owner.Figure.Id, owner.Figure.PageNumber, saved, detected);
    }
}

public sealed record AddFigureMaskCommand(
    string FigureId, double X, double Y, double Width, double Height,
    string? LabelText = null, string? Style = null, string? ColorHex = null);

public sealed record AddFigureMaskResponse(string FigureId, int PageNumber, FigureMaskDto AddedMask, int TotalMasks);

public sealed class AddFigureMaskHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<AddFigureMaskResponse>> HandleAsync(AddFigureMaskCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<AddFigureMaskResponse>(repository, command.FigureId, owner =>
        {
            var page = owner.Document.GetPage(owner.Figure.PageNumber);
            if (page is null) return DocumentErrors.InvalidPageNumber(owner.Figure.PageNumber, owner.Document.PageCount);

            var boxRes = BoundingBox.Create(command.X, command.Y, command.Width, command.Height);
            if (!boxRes.IsSuccess) return boxRes.Error!;

            var style = ObfuscationStyle.Solid;
            if (!string.IsNullOrWhiteSpace(command.Style) && !FigureLabelMask.TryParseStyle(command.Style, out style))
                return FigureErrors.InvalidObfuscationStyle(command.Style);

            var addRes = owner.Figure.AddLabelMask(boxRes.Value, page, command.LabelText, style, command.ColorHex);
            if (!addRes.IsSuccess) return addRes.Error!;

            var added = addRes.Value;
            return new AddFigureMaskResponse(
                owner.Figure.Id, owner.Figure.PageNumber,
                new FigureMaskDto(added.Index, added.BoundingBox, added.LabelText, FigureLabelMask.FormatStyle(added.Style), added.ColorHex),
                owner.Figure.LabelMasks.Count);
        }, cancellationToken);
}

public sealed record AutoMaskFigureLabelsCommand(string FigureId, string? MatchPatterns = null, string? Style = null, string? ColorHex = null);

public sealed record AutoMaskFigureLabelsResponse(string FigureId, int AddedCount, int TotalMasks, IReadOnlyList<FigureMaskDto> AddedMasks);

public sealed class AutoMaskFigureLabelsHandler(IDocumentWorkspaceRepository repository, IFigureLabelDetector labelDetector)
{
    public async Task<Result<AutoMaskFigureLabelsResponse>> HandleAsync(AutoMaskFigureLabelsCommand command, CancellationToken cancellationToken = default)
    {
        var owner = await repository.FindFigureOwnerAsync(command.FigureId, cancellationToken);
        if (owner is null) return FigureErrors.NotFound(command.FigureId);

        var (doc, fig) = (owner.Document, owner.Figure);
        var page = doc.GetPage(fig.PageNumber);
        if (page is null) return DocumentErrors.InvalidPageNumber(fig.PageNumber, doc.PageCount);

        var style = ObfuscationStyle.Solid;
        if (!string.IsNullOrWhiteSpace(command.Style) && !FigureLabelMask.TryParseStyle(command.Style, out style))
            return FigureErrors.InvalidObfuscationStyle(command.Style);

        string? normalizedColor = null;
        if (!string.IsNullOrWhiteSpace(command.ColorHex) && !FigureLabelMask.TryNormalizeColorHex(command.ColorHex, out normalizedColor))
            return FigureErrors.InvalidColorHex(command.ColorHex);

        if (!File.Exists(doc.StoredPdfPath)) return DocumentErrors.PdfFileNotFound(doc.StoredPdfPath);

        var labelsRes = await labelDetector.DetectLabelsInRegionAsync(doc.StoredPdfPath, fig.PageNumber, fig.BoundingBox, cancellationToken);
        if (!labelsRes.IsSuccess) return labelsRes.Error!;

        var patterns = ExtractionOptions.ParsePatterns(command.MatchPatterns);
        var addedDtos = new List<FigureMaskDto>();

        foreach (var label in labelsRes.Value)
        {
            if (patterns.Count > 0 && !ExtractionOptions.MatchesAnyPattern(label.Text, patterns))
                continue;
            if (fig.LabelMasks.Any(existing => existing.BoundingBox.ComputeIoU(label.BoundingBox) >= 0.60))
                continue;

            var safeBox = label.BoundingBox.ClampToPage(page.WidthPts, page.HeightPts, 1, 1, 1, 1);
            var addRes = fig.AddLabelMask(safeBox, page, label.Text, style, normalizedColor);
            if (!addRes.IsSuccess) return addRes.Error!;

            var m = addRes.Value;
            addedDtos.Add(new FigureMaskDto(m.Index, m.BoundingBox, m.LabelText, FigureLabelMask.FormatStyle(m.Style), m.ColorHex));
        }

        if (addedDtos.Count > 0)
            await repository.SaveDocumentAsync(doc, cancellationToken);

        return new AutoMaskFigureLabelsResponse(fig.Id, addedDtos.Count, fig.LabelMasks.Count, addedDtos);
    }
}

public sealed record RemoveFigureMaskCommand(string FigureId, int MaskIndex);

public sealed record RemoveFigureMaskResponse(string FigureId, int RemovedMaskIndex, int RemainingMasks);

public sealed class RemoveFigureMaskHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<RemoveFigureMaskResponse>> HandleAsync(RemoveFigureMaskCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<RemoveFigureMaskResponse>(repository, command.FigureId, owner =>
        {
            var res = owner.Figure.RemoveLabelMask(command.MaskIndex);
            return res.IsSuccess
                ? new RemoveFigureMaskResponse(owner.Figure.Id, command.MaskIndex, owner.Figure.LabelMasks.Count)
                : res.Error!;
        }, cancellationToken);
}

public sealed record ClearFigureMasksCommand(string FigureId);

public sealed record ClearFigureMasksResponse(string FigureId, int ClearedCount);

public sealed class ClearFigureMasksHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<ClearFigureMasksResponse>> HandleAsync(ClearFigureMasksCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureConditionalAsync<ClearFigureMasksResponse>(repository, command.FigureId, owner =>
        {
            int cleared = owner.Figure.ClearLabelMasks();
            return (new ClearFigureMasksResponse(owner.Figure.Id, cleared), cleared > 0);
        }, cancellationToken);
}
