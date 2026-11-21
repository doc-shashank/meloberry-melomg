using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;

namespace Melomg.Application.UseCases.Figures;

public sealed record FigureSummaryDto(
    int Index, string FigureId, int PageNumber, int PageFigureIndex, string Category, double Confidence,
    BoundingBox BoundingBox, string Source, string Label, string? Caption, string? LastExtractedPath);

public sealed record ListFiguresQuery(string DocumentId, int? PageNumber = null, string? Category = null, double? MinConfidence = null);

public sealed record ListFiguresResponse(string DocumentId, string Title, int TotalFigures, IReadOnlyList<FigureSummaryDto> Figures);

public sealed class ListFiguresHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<ListFiguresResponse>> HandleAsync(ListFiguresQuery query, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(query.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(query.DocumentId);

        var doc = lookup.Document;
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

        var filtered = doc.Figures
            .Where(f => (!query.PageNumber.HasValue || f.PageNumber == query.PageNumber.Value) &&
                        (!filterCategory.HasValue || f.Category == filterCategory.Value) &&
                        (!query.MinConfidence.HasValue || f.Confidence >= query.MinConfidence.Value))
            .Select(f => new FigureSummaryDto(
                f.Index, f.Id, f.PageNumber, f.PageFigureIndex, f.Category.ToString().ToLowerInvariant(),
                f.Confidence, f.BoundingBox, DocumentFigure.FormatSourceTag(f.Source), f.Label, f.Caption, f.LastExtractedPath))
            .ToList();

        return new ListFiguresResponse(doc.Id, doc.Title, doc.Figures.Count, filtered);
    }
}

public sealed record InspectFigureQuery(string FigureId);

public sealed record FigureDetailResponse(
    string FigureId, string DocumentId, int Index, int PageNumber, int PageFigureIndex, string Category,
    double Confidence, string Source, string Label, string? Caption, BoundingBox BoundingBoxPts,
    BoundingBox? CaptionBoundingBoxPts, NormalizedBoundingBox NormalizedBox, double PageWidthPts, double PageHeightPts,
    PixelRect DefaultPixelRectAt150Dpi, string? LastExtractedPath, IReadOnlyList<FigureMaskDto> LabelMasks,
    IReadOnlyList<DetectedLabelDto> DetectedLabels);

public sealed class InspectFigureHandler(IDocumentWorkspaceRepository repository, IFigureLabelDetector labelDetector)
{
    public async Task<Result<FigureDetailResponse>> HandleAsync(InspectFigureQuery query, CancellationToken cancellationToken = default)
    {
        var owner = await repository.FindFigureOwnerAsync(query.FigureId, cancellationToken);
        if (owner is null) return FigureErrors.NotFound(query.FigureId);

        var fig = owner.Figure;
        var page = owner.Document.GetPage(fig.PageNumber) ?? new PdfPageInfo(fig.PageNumber, 612, 792);
        var (savedMasks, detectedLabels) = await FigureUseCaseHelpers.LoadMasksAndDetectedLabelsAsync(owner, labelDetector, cancellationToken);

        return new FigureDetailResponse(
            fig.Id, owner.Document.Id, fig.Index, fig.PageNumber, fig.PageFigureIndex,
            fig.Category.ToString().ToLowerInvariant(), fig.Confidence, DocumentFigure.FormatSourceTag(fig.Source),
            fig.Label, fig.Caption, fig.BoundingBox, fig.CaptionBoundingBox,
            fig.BoundingBox.ToNormalized(page.WidthPts, page.HeightPts), page.WidthPts, page.HeightPts,
            fig.BoundingBox.ToPixelRect(ExtractionOptions.DefaultDpi, ExtractionOptions.DefaultPaddingPx, page.WidthPts, page.HeightPts),
            fig.LastExtractedPath, savedMasks, detectedLabels);
    }
}

public sealed record AddManualFigureCommand(
    string DocumentId, int PageNumber, double X, double Y, double Width, double Height,
    string? Category = null, string? Caption = null, string? Label = null, int? Index = null);

public sealed record AddManualFigureResponse(
    string DocumentId, string FigureId, int Index, int PageNumber, string Category, BoundingBox BoundingBox);

public sealed class AddManualFigureHandler(IDocumentWorkspaceRepository repository, IIdGenerator idGenerator)
{
    public async Task<Result<AddManualFigureResponse>> HandleAsync(AddManualFigureCommand command, CancellationToken cancellationToken = default)
    {
        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        var category = FigureCategory.Figure;
        if (!string.IsNullOrWhiteSpace(command.Category) && !DocumentFigure.TryParseCategory(command.Category, out category))
            return FigureErrors.InvalidCategory(command.Category);

        var bboxResult = BoundingBox.Create(command.X, command.Y, command.Width, command.Height);
        if (!bboxResult.IsSuccess) return bboxResult.Error!;

        var figId = idGenerator.NewFigureId();
        var addResult = lookup.Document.AddFigure(
            figId, command.PageNumber, category, bboxResult.Value, 1.0, command.Caption, command.Label,
            DetectionSource.Manual, targetIndex: command.Index);
        if (!addResult.IsSuccess) return addResult.Error!;

        lookup.Document.ActivateFigure(figId);
        await repository.SaveDocumentAsync(lookup.Document, cancellationToken);

        var fig = addResult.Value;
        return new AddManualFigureResponse(
            lookup.Document.Id, fig.Id, fig.Index, fig.PageNumber, fig.Category.ToString().ToLowerInvariant(), fig.BoundingBox);
    }
}

public sealed record SetFigureBoundingBoxCommand(string FigureId, double X, double Y, double Width, double Height);
public sealed record SetFigureBoundingBoxResponse(string FigureId, int PageNumber, BoundingBox BoundingBox);

public sealed class SetFigureBoundingBoxHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<SetFigureBoundingBoxResponse>> HandleAsync(SetFigureBoundingBoxCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<SetFigureBoundingBoxResponse>(repository, command.FigureId, owner =>
        {
            var page = owner.Document.GetPage(owner.Figure.PageNumber);
            if (page is null) return DocumentErrors.InvalidPageNumber(owner.Figure.PageNumber, owner.Document.PageCount);

            var boxResult = BoundingBox.Create(command.X, command.Y, command.Width, command.Height);
            if (!boxResult.IsSuccess) return boxResult.Error!;

            var updateResult = owner.Figure.UpdateBoundingBox(boxResult.Value, page);
            return updateResult.IsSuccess
                ? new SetFigureBoundingBoxResponse(owner.Figure.Id, owner.Figure.PageNumber, owner.Figure.BoundingBox)
                : updateResult.Error!;
        }, cancellationToken);
}

public sealed record SetFigureCaptionCommand(string FigureId, string Caption);
public sealed record SetFigureCaptionResponse(string FigureId, string Caption);

public sealed class SetFigureCaptionHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<SetFigureCaptionResponse>> HandleAsync(SetFigureCaptionCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<SetFigureCaptionResponse>(repository, command.FigureId, owner =>
        {
            var res = owner.Figure.SetCaption(command.Caption);
            return res.IsSuccess ? new SetFigureCaptionResponse(owner.Figure.Id, owner.Figure.Caption!) : res.Error!;
        }, cancellationToken);
}

public sealed record SetFigureLabelCommand(string FigureId, string Label);
public sealed record SetFigureLabelResponse(string FigureId, string Label);

public sealed class SetFigureLabelHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<SetFigureLabelResponse>> HandleAsync(SetFigureLabelCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<SetFigureLabelResponse>(repository, command.FigureId, owner =>
        {
            var res = owner.Figure.SetLabel(command.Label);
            return res.IsSuccess ? new SetFigureLabelResponse(owner.Figure.Id, owner.Figure.Label) : res.Error!;
        }, cancellationToken);
}

public sealed record SetFigureCategoryCommand(string FigureId, string Category);
public sealed record SetFigureCategoryResponse(string FigureId, string Category);

public sealed class SetFigureCategoryHandler(IDocumentWorkspaceRepository repository)
{
    public async Task<Result<SetFigureCategoryResponse>> HandleAsync(SetFigureCategoryCommand command, CancellationToken cancellationToken = default)
    {
        if (!DocumentFigure.TryParseCategory(command.Category, out var parsed))
            return FigureErrors.InvalidCategory(command.Category);

        return await FigureUseCaseHelpers.MutateFigureAsync<SetFigureCategoryResponse>(repository, command.FigureId, owner =>
        {
            owner.Figure.SetCategory(parsed);
            return Result<SetFigureCategoryResponse>.Success(
                new SetFigureCategoryResponse(owner.Figure.Id, owner.Figure.Category.ToString().ToLowerInvariant()));
        }, cancellationToken);
    }
}

public sealed record ReorderFigureCommand(string FigureId, int TargetIndex, ReorderMode Mode);
public sealed record ReorderFigureResponse(string FigureId, int NewIndex, string Mode);

public sealed class ReorderFigureHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<ReorderFigureResponse>> HandleAsync(ReorderFigureCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<ReorderFigureResponse>(repository, command.FigureId, owner =>
        {
            var res = owner.Document.ReorderFigure(command.FigureId, command.TargetIndex, command.Mode);
            return res.IsSuccess
                ? new ReorderFigureResponse(owner.Figure.Id, owner.Figure.Index, command.Mode.ToString().ToLowerInvariant())
                : res.Error!;
        }, cancellationToken);
}

public sealed record DeleteFigureCommand(string FigureId);
public sealed record DeleteFigureResponse(string DeletedFigureId, string DocumentId, int RemainingFigures);

public sealed class DeleteFigureHandler(IDocumentWorkspaceRepository repository)
{
    public Task<Result<DeleteFigureResponse>> HandleAsync(DeleteFigureCommand command, CancellationToken cancellationToken = default) =>
        FigureUseCaseHelpers.MutateFigureAsync<DeleteFigureResponse>(repository, command.FigureId, owner =>
        {
            var res = owner.Document.DeleteFigure(command.FigureId);
            return res.IsSuccess
                ? new DeleteFigureResponse(command.FigureId, owner.Document.Id, owner.Document.Figures.Count)
                : res.Error!;
        }, cancellationToken);
}
