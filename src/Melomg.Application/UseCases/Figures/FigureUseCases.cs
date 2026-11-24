using Melomg.Application.Abstractions;
using Melomg.Application.Addressing;
using Melomg.Application.UseCases.Documents;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;

namespace Melomg.Application.UseCases.Figures;

public sealed record FigureDetailResponse(
    string FigureId,
    string DocumentId,
    int Index,
    int PageNumber,
    int PageFigureIndex,
    string Category,
    double Confidence,
    string Source,
    string Label,
    string? VocabPrefix,
    string? FigureNumber,
    string? Caption,
    BoundingBox BoundingBoxPts,
    BoundingBox? CaptionBoundingBoxPts,
    NormalizedBoundingBox NormalizedBox,
    double PageWidthPts,
    double PageHeightPts,
    PixelRect DefaultPixelRectAt150Dpi,
    string? LastExtractedPath,
    IReadOnlyList<FigureMaskDto> LabelMasks,
    IReadOnlyList<DetectedLabelDto> DetectedLabels);

public sealed record AddManualFigureCommand(
    string DocumentId,
    int PageNumber,
    double X,
    double Y,
    double Width,
    double Height,
    string? Category = null,
    string? Caption = null,
    string? Label = null,
    string? FigureNumber = null,
    int? Index = null);

public sealed record AddManualFigureResponse(
    string DocumentId,
    string FigureId,
    int Index,
    int PageNumber,
    string Category,
    string? FigureNumber,
    BoundingBox BoundingBox);

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
            DetectionSource.Manual, targetIndex: command.Index, figureNumber: command.FigureNumber);
        if (!addResult.IsSuccess) return addResult.Error!;

        lookup.Document.ActivateFigure(figId);
        await repository.SaveDocumentAsync(lookup.Document, cancellationToken);

        var fig = addResult.Value;
        return new AddManualFigureResponse(
            lookup.Document.Id, fig.Id, fig.Index, fig.PageNumber,
            fig.Category.ToString().ToLowerInvariant(), fig.FigureNumber, fig.BoundingBox);
    }
}

public sealed record PatchTargetCommand(
    string Address,
    string? Title = null,
    BoundingBox? BoundingBox = null,
    string? Caption = null,
    string? Label = null,
    string? FigureNumber = null,
    string? Category = null,
    int? MoveIndex = null,
    ReorderMode? MoveMode = null);

public sealed record PatchTargetResponse(
    string TargetId,
    string EntityKind,
    string DocumentId,
    string? Title = null,
    int? Index = null,
    string? Category = null,
    string? Label = null,
    string? FigureNumber = null,
    string? Caption = null,
    BoundingBox? BoundingBox = null,
    DocumentVocabularyDto? Vocab = null);

public sealed class PatchTargetHandler(AddressResolver resolver, IDocumentWorkspaceRepository repository)
{
    public async Task<Result<PatchTargetResponse>> HandleAsync(PatchTargetCommand command, CancellationToken cancellationToken = default)
    {
        var parsedRes = AddressResolver.Parse(command.Address);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;

        var parsed = parsedRes.Value;
        if (parsed.Kind == AddressKind.Document)
        {
            if (command.BoundingBox is not null || command.Caption is not null || command.Label is not null ||
                command.FigureNumber is not null || command.Category is not null || command.MoveIndex.HasValue)
            {
                return FigureErrors.InvalidAddress(command.Address, "Figure fields (bbox, caption, label, num, category, move) require a figure address (<docId>#<num|fN> or <figId>).");
            }

            if (command.Title is null)
                return new Error("Cli.InvalidArgs", "Specify --set title=... or JSON { \"title\": ... } to patch a document.", ErrorType.Validation);

            var lookup = await repository.FindDocumentAsync(parsed.DocumentId!, cancellationToken);
            if (lookup is null) return DocumentErrors.NotFound(parsed.DocumentId!);

            var titleRes = lookup.Document.SetTitle(command.Title);
            if (!titleRes.IsSuccess) return titleRes.Error!;

            await repository.SaveDocumentAsync(lookup.Document, cancellationToken);
            return new PatchTargetResponse(lookup.Document.Id, "document", lookup.Document.Id, Title: lookup.Document.Title);
        }

        if (command.Title is not null)
            return FigureErrors.InvalidAddress(command.Address, "Document 'title' can only be patched on a Doc_<id> address.");

        if (command.Category is null && command.BoundingBox is null && command.Caption is null &&
            command.Label is null && command.FigureNumber is null && !command.MoveIndex.HasValue)
        {
            return new Error("Cli.InvalidArgs", "Specify at least one figure field to patch (--set, --move, or JSON).", ErrorType.Validation);
        }

        var ownerRes = await resolver.ResolveSingleFigureAsync(command.Address, cancellationToken);
        if (!ownerRes.IsSuccess) return ownerRes.Error!;

        var owner = ownerRes.Value;
        var doc = owner.Document;
        var fig = owner.Figure;

        if (command.Category is not null)
        {
            if (!DocumentFigure.TryParseCategory(command.Category, out var cat))
                return FigureErrors.InvalidCategory(command.Category);
            fig.SetCategory(cat);
        }

        if (command.BoundingBox is { } box)
        {
            var page = doc.GetPage(fig.PageNumber);
            if (page is null) return DocumentErrors.InvalidPageNumber(fig.PageNumber, doc.PageCount);

            var validBox = BoundingBox.Create(box.X, box.Y, box.Width, box.Height);
            if (!validBox.IsSuccess) return validBox.Error!;

            var updRes = fig.UpdateBoundingBox(validBox.Value, page);
            if (!updRes.IsSuccess) return updRes.Error!;
        }

        if (command.Caption is not null)
        {
            var capRes = fig.SetCaption(command.Caption);
            if (!capRes.IsSuccess) return capRes.Error!;
        }

        if (command.Label is not null)
        {
            var lblRes = fig.SetLabel(command.Label);
            if (!lblRes.IsSuccess) return lblRes.Error!;
        }

        if (command.FigureNumber is not null)
        {
            var numRes = fig.SetFigureNumber(command.FigureNumber);
            if (!numRes.IsSuccess) return numRes.Error!;
        }

        if (command.MoveIndex.HasValue)
        {
            var moveRes = doc.ReorderFigure(fig.Id, command.MoveIndex.Value, command.MoveMode ?? ReorderMode.Shift);
            if (!moveRes.IsSuccess) return moveRes.Error!;
        }

        doc.RefreshVocabulary();
        await repository.SaveDocumentAsync(doc, cancellationToken);

        return new PatchTargetResponse(
            fig.Id, "figure", doc.Id,
            Index: fig.Index,
            Category: fig.Category.ToString().ToLowerInvariant(),
            Label: fig.Label,
            FigureNumber: fig.FigureNumber,
            Caption: fig.Caption,
            BoundingBox: fig.BoundingBox,
            Vocab: DocumentVocabularyDto.FromDomain(doc.Vocabulary));
    }
}
