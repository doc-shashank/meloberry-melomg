using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;

namespace Melomg.Application.UseCases.Extraction;

internal static class QaOverlayManifestHelper
{
    private const string ManifestFileName = ".qa_overlays";

    public static void Track(string workspaceStorageDir, IReadOnlyList<RenderedPageArtifact> artifacts)
    {
        try
        {
            Directory.CreateDirectory(workspaceStorageDir);
            string manifestPath = Path.Combine(workspaceStorageDir, ManifestFileName);
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(manifestPath))
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                    if (!string.IsNullOrWhiteSpace(line)) tracked.Add(line.Trim());
            }

            foreach (var art in artifacts)
                if (!string.IsNullOrWhiteSpace(art.OutputImagePath)) tracked.Add(Path.GetFullPath(art.OutputImagePath));

            File.WriteAllLines(manifestPath, tracked);
        }
        catch { }
    }

    public static int Cleanup(string workspaceStorageDir)
    {
        int cleanedCount = 0;
        var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifestPath = Path.Combine(workspaceStorageDir, ManifestFileName);
        string protectedWorkspace = Path.GetFullPath(workspaceStorageDir);
        string workspacePrefix = protectedWorkspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string protectedFigures = Path.GetFullPath(Path.Combine(workspaceStorageDir, "figures"));

        try
        {
            if (File.Exists(manifestPath))
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                    if (!string.IsNullOrWhiteSpace(line)) candidateFiles.Add(Path.GetFullPath(line.Trim()));
            }
        }
        catch { }

        foreach (string subName in new[] { "pages", "qa_overlay" })
        {
            string subDir = Path.Combine(workspaceStorageDir, subName);
            try
            {
                if (Directory.Exists(subDir))
                    foreach (var file in Directory.GetFiles(subDir, "*_overlay.*"))
                        candidateFiles.Add(Path.GetFullPath(file));
            }
            catch { }
        }

        foreach (var filePath in candidateFiles)
        {
            try
            {
                if (!IsSafeOverlayImageFile(filePath)) continue;
                string? parent = Path.GetDirectoryName(filePath);
                File.Delete(filePath);
                cleanedCount++;
                if (!string.IsNullOrWhiteSpace(parent)) candidateDirs.Add(Path.GetFullPath(parent));
            }
            catch { }
        }

        try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }

        foreach (var dir in candidateDirs)
        {
            try
            {
                if (string.Equals(dir, protectedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dir, protectedFigures, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool underWorkspace = dir.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase);
                string leafName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                bool isDedicatedOverlayDir = leafName is "qa_overlay" or "pages";
                if ((underWorkspace || isDedicatedOverlayDir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }

        return cleanedCount;
    }

    private static bool IsSafeOverlayImageFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Path.GetFileNameWithoutExtension(filePath).EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(filePath) || (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            return false;

        Span<byte> hdr = stackalloc byte[8];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = fs.Read(hdr);
        bool isPng = read >= 8 && hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47;
        bool isJpg = read >= 3 && hdr[0] == 0xFF && hdr[1] == 0xD8 && hdr[2] == 0xFF;
        return isPng || isJpg;
    }
}

public sealed record ExtractFiguresCommand(
    string? TargetId, string? Selector = null, int? PageNumber = null, string? OutputDirectory = null,
    string Format = "png", int? Dpi = null, int PaddingPx = ExtractionOptions.DefaultPaddingPx,
    bool IncludeCaption = false, bool ObfuscateAllLabels = false, string? ObfuscateLabelPatterns = null,
    bool IgnoreSavedMasks = false, string? ObfuscateStyle = null, string? ObfuscateColorHex = null,
    string? Quality = null, bool IncludeMetadata = false, bool KeepOverlay = false, bool RetainDatabase = false);

public sealed record ExtractFiguresResponse(
    string DocumentId, string OutputDirectory, string Format, int Dpi, int PaddingPx, int Quality,
    bool IncludeCaption, string RenderEngine, int ExtractedCount, IReadOnlyList<ExtractedFigureArtifact> Artifacts,
    int CleanedOverlayCount = 0, bool RetainedDatabase = true);

public sealed class ExtractFiguresHandler(
    IDocumentWorkspaceRepository repository, IFigureImageExtractor figureExtractor, IWorkingDirectoryProvider workingDirectoryProvider)
{
    public async Task<Result<ExtractFiguresResponse>> HandleAsync(ExtractFiguresCommand command, CancellationToken cancellationToken = default)
    {
        var optionsResult = ExtractionOptions.Create(
            command.Format, command.Dpi, command.PaddingPx, command.IncludeCaption, command.ObfuscateAllLabels,
            command.ObfuscateLabelPatterns, command.IgnoreSavedMasks, command.ObfuscateStyle, command.ObfuscateColorHex,
            command.Quality, command.IncludeMetadata);
        if (!optionsResult.IsSuccess) return optionsResult.Error!;
        if (string.IsNullOrWhiteSpace(command.TargetId)) return DocumentErrors.NotFound(string.Empty);

        var options = optionsResult.Value;
        PdfDocument document;
        string workspaceStorageDir;
        List<DocumentFigure> targets = [];

        if (command.TargetId.StartsWith("Fig_", StringComparison.Ordinal))
        {
            var owner = await repository.FindFigureOwnerAsync(command.TargetId, cancellationToken);
            if (owner is null) return FigureErrors.NotFound(command.TargetId);
            document = owner.Document;
            workspaceStorageDir = owner.StorageDirectory;
            targets.Add(owner.Figure);
        }
        else
        {
            var lookup = await repository.FindDocumentAsync(command.TargetId, cancellationToken);
            if (lookup is null) return DocumentErrors.NotFound(command.TargetId);
            document = lookup.Document;
            workspaceStorageDir = lookup.StorageDirectory;
            var resolveResult = ResolveDocumentTargets(document, command.Selector, command.PageNumber);
            if (!resolveResult.IsSuccess) return resolveResult.Error!;
            targets.AddRange(resolveResult.Value);
        }

        if (targets.Count == 0) return FigureErrors.NoFiguresMatch(command.Selector ?? command.TargetId);

        string outDir = string.IsNullOrWhiteSpace(command.OutputDirectory)
            ? Path.Combine(workspaceStorageDir, "figures")
            : workingDirectoryProvider.NormalizeDirectoryPath(command.OutputDirectory);

        var extractResult = await figureExtractor.ExtractFiguresAsync(document, targets, options, outDir, cancellationToken);
        if (!extractResult.IsSuccess) return extractResult.Error!;

        var artifacts = extractResult.Value;
        for (int i = 0; i < targets.Count && i < artifacts.Count; i++)
            targets[i].RecordExtraction(artifacts[i].OutputImagePath);

        if (targets.Count == 1) document.ActivateFigure(targets[0].Id);
        if (command.RetainDatabase && !document.RetainDatabase) document.EnableDatabaseRetention();

        await repository.SaveDocumentAsync(document, cancellationToken);

        int cleanedOverlays = !command.KeepOverlay ? QaOverlayManifestHelper.Cleanup(workspaceStorageDir) : 0;
        bool retainDatabase = document.Mode == WorkspaceMode.Project || document.RetainDatabase || command.RetainDatabase;
        if (!retainDatabase) await repository.DiscardWorkspaceDatabaseAsync(document.Id, cancellationToken);

        return new ExtractFiguresResponse(
            document.Id, outDir, options.Format, options.Dpi, options.PaddingPx, options.Quality,
            options.IncludeCaption, artifacts.FirstOrDefault()?.RenderEngine ?? "windows-pdf",
            artifacts.Count, artifacts, cleanedOverlays, retainDatabase);
    }

    private static Result<IReadOnlyList<DocumentFigure>> ResolveDocumentTargets(PdfDocument document, string? selector, int? pageNumber)
    {
        if (pageNumber.HasValue)
        {
            if (document.GetPage(pageNumber.Value) is null) return DocumentErrors.InvalidPageNumber(pageNumber.Value, document.PageCount);
            var pageFigs = document.Figures.Where(f => f.PageNumber == pageNumber.Value).ToList();
            return pageFigs.Count > 0 ? pageFigs : FigureErrors.NoFiguresMatch($"page {pageNumber.Value}");
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            if (string.IsNullOrWhiteSpace(document.ActiveFigureId)) return FigureErrors.NoFiguresMatch("active figure");
            var active = document.FindFigure(document.ActiveFigureId);
            return active is null ? FigureErrors.NotFound(document.ActiveFigureId) : new List<DocumentFigure> { active };
        }

        var trimmed = selector.Trim();
        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            return document.Figures.Count > 0 ? document.Figures.ToList() : FigureErrors.NoFiguresMatch("all");

        if (trimmed.StartsWith("Fig_", StringComparison.Ordinal))
        {
            var byId = document.FindFigure(trimmed);
            return byId is null ? FigureErrors.NotFound(trimmed) : new List<DocumentFigure> { byId };
        }

        if (int.TryParse(trimmed, out int idx))
        {
            if (idx < 1 || idx > document.Figures.Count)
                return IndexErrors.OutOfIndexBounds(idx, 1, Math.Max(1, document.Figures.Count));
            var byIdx = document.FindFigureByIndex(idx);
            return byIdx is null ? FigureErrors.NotFound($"#{idx}") : new List<DocumentFigure> { byIdx };
        }

        return FigureErrors.NoFiguresMatch(trimmed);
    }
}
