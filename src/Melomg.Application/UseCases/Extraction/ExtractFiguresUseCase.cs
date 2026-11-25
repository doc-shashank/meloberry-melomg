using Melomg.Application.Abstractions;
using Melomg.Application.Addressing;
using Melomg.Application.UseCases.Documents;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;

namespace Melomg.Application.UseCases.Extraction;

public static class QaOverlayManifestHelper
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
                if (underWorkspace && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
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
    string Target,
    IReadOnlyList<string>? Selectors = null,
    string? OutputDirectory = null,
    string Format = "png",
    int? Dpi = null,
    int PaddingPx = ExtractionOptions.DefaultPaddingPx,
    bool IncludeCaption = false,
    bool ObfuscateAllLabels = false,
    string? ObfuscateLabelPatterns = null,
    bool IgnoreSavedMasks = false,
    string? ObfuscateStyle = null,
    string? ObfuscateColorHex = null,
    string? Quality = null,
    string NamingMode = ExtractionOptions.DefaultNamingMode,
    bool IncludeMetadata = false,
    bool KeepOverlay = false,
    bool RetainDatabase = false,
    bool Offline = false,
    string? ModelPath = null,
    double? ConfidenceThreshold = null,
    bool EmitManifest = false,
    bool Quiet = false,
    bool Verbose = false);

public sealed record ExtractFiguresResponse(
    string DocumentId,
    string OutputDirectory,
    string Format,
    int Dpi,
    int PaddingPx,
    int Quality,
    bool IncludeCaption,
    string RenderEngine,
    int ExtractedCount,
    IReadOnlyList<ExtractedFigureArtifact>? Artifacts,
    int CleanedOverlayCount = 0,
    bool RetainedDatabase = true,
    DocumentVocabularyDto? Vocab = null,
    string? ManifestPath = null);

public sealed class ExtractFiguresHandler(
    AddressResolver resolver,
    IDocumentWorkspaceRepository repository,
    IFigureImageExtractor figureExtractor,
    IWorkingDirectoryProvider workingDirectoryProvider,
    IIdGenerator idGenerator,
    IModelSettingsRepository modelSettings,
    IOnnxModelInspector modelInspector,
    IPdfIngestionEngine pdfEngine,
    IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ExtractFiguresResponse>> HandleAsync(ExtractFiguresCommand command, CancellationToken cancellationToken = default)
    {
        var optionsResult = ExtractionOptions.Create(
            command.Format, command.Dpi, command.PaddingPx, command.IncludeCaption, command.ObfuscateAllLabels,
            command.ObfuscateLabelPatterns, command.IgnoreSavedMasks, command.ObfuscateStyle, command.ObfuscateColorHex,
            command.Quality, command.IncludeMetadata, command.NamingMode, command.EmitManifest);
        if (!optionsResult.IsSuccess) return optionsResult.Error!;

        if (string.IsNullOrWhiteSpace(command.Target))
            return FigureErrors.InvalidAddress(string.Empty, "Target address or PDF path is required.");

        var options = optionsResult.Value;
        string target = command.Target.Trim();

        // One-shot PDF extraction when target is a PDF file path (or not a Doc_/Fig_ address)
        bool looksLikeIdAddress = !workingDirectoryProvider.FileExists(target) &&
                                  !workingDirectoryProvider.DirectoryExists(target) &&
                                  AddressResolver.LooksLikeEntityIdAddress(target);
        if (!looksLikeIdAddress)
            return await HandleOneShotPdfAsync(target, command, options, cancellationToken);

        var resolvedRes = await resolver.ResolveTargetAsync(target, cancellationToken);
        if (!resolvedRes.IsSuccess) return resolvedRes.Error!;

        var resolved = resolvedRes.Value;
        var document = resolved.DocumentContext.Document;
        string workspaceStorageDir = resolved.DocumentContext.StorageDirectory;
        IReadOnlyList<DocumentFigure> targets;

        if (resolved.Address.Kind == AddressKind.Document)
        {
            if (command.Selectors is null || command.Selectors.Count == 0)
                return new Error("Cli.InvalidArgs", "Specify figure number(s) (e.g. '2.1'), 'all', or address '<docId>#<num>'.", ErrorType.Validation, Hint: $"Document '{document.Id}' has {document.Figures.Count} figure(s).");

            var selRes = AddressResolver.ResolveSelectors(document, command.Selectors);
            if (!selRes.IsSuccess) return selRes.Error!;
            targets = selRes.Value;
        }
        else
        {
            if (command.Selectors is { Count: > 0 })
            {
                return new Error(
                    "Cli.InvalidArgs",
                    $"Cannot combine extra selectors ({string.Join(", ", command.Selectors)}) with a specific target address '{target}'.",
                    ErrorType.Validation,
                    Hint: $"Pass selectors after the document ID instead: melomg extract {document.Id} <selector1> <selector2>");
            }
            targets = resolved.Figures;
        }

        if (targets.Count == 0)
            return FigureErrors.NoFiguresMatch(target);

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

        string? manifestPath = null;
        if (options.EmitManifest)
        {
            var manRes = await figureExtractor.WriteManifestAsync(document, artifacts, outDir, cancellationToken);
            if (!manRes.IsSuccess) return manRes.Error!;
            manifestPath = manRes.Value;
        }

        int cleanedOverlays = !command.KeepOverlay ? QaOverlayManifestHelper.Cleanup(workspaceStorageDir) : 0;
        bool retainDatabase = document.Mode == WorkspaceMode.Project || document.RetainDatabase || command.RetainDatabase;
        if (!retainDatabase) await repository.DiscardWorkspaceDatabaseAsync(document.Id, cancellationToken);

        return new ExtractFiguresResponse(
            document.Id, outDir, options.Format, options.Dpi, options.PaddingPx, options.Quality,
            options.IncludeCaption, artifacts.FirstOrDefault()?.RenderEngine ?? "windows-pdf",
            artifacts.Count, command.Quiet ? null : artifacts, cleanedOverlays, retainDatabase,
            DocumentVocabularyDto.FromDomain(document.Vocabulary), manifestPath);
    }

    private async Task<Result<ExtractFiguresResponse>> HandleOneShotPdfAsync(
        string pdfTarget,
        ExtractFiguresCommand command,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        if (command.Selectors is null || command.Selectors.Count == 0)
            return new Error("Cli.InvalidArgs", "One-shot PDF extraction requires figure number(s) (e.g. '2.1') or 'all'.", ErrorType.Validation, Hint: "Pass figure number(s) after the PDF path or run 'melomg ingest <pdf>' first.");

        string fullPdfPath = Path.GetFullPath(pdfTarget);
        var analysisResult = await ModelResolutionHelper.PrepareAndAnalyzePdfAsync(
            fullPdfPath, command.ModelPath, command.ConfidenceThreshold, command.Offline,
            runtimeManager, modelSettings, modelInspector, pdfEngine, cancellationToken);
        if (!analysisResult.IsSuccess) return analysisResult.Error!;

        var analysis = analysisResult.Value;
        string docId = idGenerator.NewDocumentId();
        string tempWorkspace = command.RetainDatabase
            ? Path.Combine(workingDirectoryProvider.GetCurrentDirectory(), ".meloberry", "melomg", docId)
            : Path.Combine(Path.GetTempPath(), $"melomg_oneshot_{docId}");

        bool persisted = false;
        try
        {
            var docResult = PdfDocument.Create(
                docId, analysis.SuggestedTitle, fullPdfPath, fullPdfPath, analysis.Sha256Hash, tempWorkspace,
                analysis.ModelUsed.ModelName, analysis.EffectiveConfidenceThreshold, TimeProvider.System.GetUtcNow(),
                analysis.Pages, WorkspaceMode.Standard, command.RetainDatabase, analysis.ScannedVocabPrefixes);
            if (!docResult.IsSuccess) return docResult.Error!;

            var document = docResult.Value;
            var popRes = ModelResolutionHelper.PopulateFigures(document, analysis.Candidates, idGenerator);
            if (!popRes.IsSuccess) return popRes.Error!;

            var selRes = AddressResolver.ResolveSelectors(document, command.Selectors);
            if (!selRes.IsSuccess) return selRes.Error!;

            var targets = selRes.Value;
            if (command.RetainDatabase)
            {
                var initRes = await pdfEngine.InitializeWorkspaceAsync(tempWorkspace, cancellationToken);
                if (!initRes.IsSuccess) return initRes.Error!;
            }

            string outDir = string.IsNullOrWhiteSpace(command.OutputDirectory)
                ? (command.RetainDatabase ? Path.Combine(tempWorkspace, "figures") : workingDirectoryProvider.GetCurrentDirectory())
                : workingDirectoryProvider.NormalizeDirectoryPath(command.OutputDirectory);

            var extractResult = await figureExtractor.ExtractFiguresAsync(document, targets, options, outDir, cancellationToken);
            if (!extractResult.IsSuccess) return extractResult.Error!;

            var artifacts = extractResult.Value;
            if (command.RetainDatabase)
            {
                for (int i = 0; i < targets.Count && i < artifacts.Count; i++)
                    targets[i].RecordExtraction(artifacts[i].OutputImagePath);
                if (targets.Count == 1) document.ActivateFigure(targets[0].Id);
                await repository.CreateDocumentAsync(document, cancellationToken);
                persisted = true;
            }

            string? manifestPath = null;
            if (options.EmitManifest)
            {
                var manRes = await figureExtractor.WriteManifestAsync(document, artifacts, outDir, cancellationToken);
                if (!manRes.IsSuccess) return manRes.Error!;
                manifestPath = manRes.Value;
            }

            return new ExtractFiguresResponse(
                document.Id, outDir, options.Format, options.Dpi, options.PaddingPx, options.Quality,
                options.IncludeCaption, artifacts.FirstOrDefault()?.RenderEngine ?? "windows-pdf",
                artifacts.Count, command.Quiet ? null : artifacts, CleanedOverlayCount: 0, RetainedDatabase: command.RetainDatabase,
                DocumentVocabularyDto.FromDomain(document.Vocabulary), manifestPath);
        }
        finally
        {
            if (!persisted)
            {
                try
                {
                    if (Directory.Exists(tempWorkspace))
                        Directory.Delete(tempWorkspace, recursive: true);
                }
                catch { }
            }
        }
    }
}
