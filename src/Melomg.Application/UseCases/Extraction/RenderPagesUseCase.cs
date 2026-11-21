using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;

namespace Melomg.Application.UseCases.Extraction;

public sealed record RenderPagesCommand(
    string? DocumentId, int? PageNumber, bool RenderAll, string? OutputDirectory = null,
    string Format = "png", int? Dpi = null, bool Overlay = false, string? Quality = null);

public sealed record RenderPagesResponse(
    string DocumentId, string OutputDirectory, string Format, int Dpi, int Quality,
    bool Overlay, string RenderEngine, int RenderedCount, IReadOnlyList<RenderedPageArtifact> Artifacts);

public sealed class RenderPagesHandler(
    IDocumentWorkspaceRepository repository, IPageSnapshotExporter exporter, IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<RenderPagesResponse>> HandleAsync(RenderPagesCommand command, CancellationToken cancellationToken = default)
    {
        var optsRes = RenderPageOptions.Create(command.Format, command.Dpi, command.Overlay, command.Quality);
        if (!optsRes.IsSuccess) return optsRes.Error!;
        if (string.IsNullOrWhiteSpace(command.DocumentId)) return DocumentErrors.NotFound(string.Empty);

        var lookup = await repository.FindDocumentAsync(command.DocumentId, cancellationToken);
        if (lookup is null) return DocumentErrors.NotFound(command.DocumentId);

        var doc = lookup.Document;
        List<int> pageNumbers;
        if (command.RenderAll)
            pageNumbers = Enumerable.Range(1, doc.PageCount).ToList();
        else if (command.PageNumber.HasValue && doc.GetPage(command.PageNumber.Value) is not null)
            pageNumbers = [command.PageNumber.Value];
        else
            return DocumentErrors.InvalidPageNumber(command.PageNumber ?? 0, doc.PageCount);

        string outDir = string.IsNullOrWhiteSpace(command.OutputDirectory)
            ? Path.Combine(lookup.StorageDirectory, "pages")
            : workingDirectoryProvider.NormalizeDirectoryPath(command.OutputDirectory);

        var exportRes = await exporter.ExportPagesAsync(doc, pageNumbers, optsRes.Value, outDir, cancellationToken);
        if (!exportRes.IsSuccess) return exportRes.Error!;

        var artifacts = exportRes.Value;
        if (optsRes.Value.Overlay && artifacts.Count > 0)
            QaOverlayManifestHelper.Track(lookup.StorageDirectory, artifacts);

        return new RenderPagesResponse(
            doc.Id, outDir, optsRes.Value.Format, optsRes.Value.Dpi, optsRes.Value.Quality, optsRes.Value.Overlay,
            artifacts.FirstOrDefault()?.RenderEngine ?? "windows-pdf", artifacts.Count, artifacts);
    }
}
