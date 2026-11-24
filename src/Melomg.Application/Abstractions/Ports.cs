using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Extraction;
using Melomg.Domain.Models;

namespace Melomg.Application.Abstractions;

public interface IIdGenerator
{
    string NewDocumentId();
    string NewFigureId();
}

public interface IWorkingDirectoryProvider
{
    string GetCurrentDirectory();
    string NormalizeDirectoryPath(string? explicitPath);
    bool DirectoryExists(string path);
    bool FileExists(string path);
}

public sealed record DocumentLookupContext(PdfDocument Document, string StorageDirectory, string DatabasePath);

public sealed record FigureOwnerContext(PdfDocument Document, DocumentFigure Figure, string StorageDirectory, string DatabasePath);

public sealed record DocumentListItem(
    string DocumentId, string Title, int PageCount, int FigureCount, string ModelName,
    double ConfidenceThreshold, string StorageDirectory, DateTimeOffset IngestedAtUtc,
    string WorkspaceMode = "standard", string? VocabPrimaryPrefix = null);

public interface IDocumentWorkspaceRepository
{
    Task CreateDocumentAsync(PdfDocument document, CancellationToken cancellationToken = default);
    Task SaveDocumentAsync(PdfDocument document, CancellationToken cancellationToken = default);
    Task<DocumentLookupContext?> FindDocumentAsync(string documentId, CancellationToken cancellationToken = default);
    Task<DocumentLookupContext?> FindDocumentInDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
    Task<FigureOwnerContext?> FindFigureOwnerAsync(string figureId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(string? directoryFilter = null, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);
    Task DiscardWorkspaceDatabaseAsync(string documentId, CancellationToken cancellationToken = default);
}

public interface IModelSettingsRepository
{
    Task<OnnxModelProfile> GetActiveModelProfileAsync(CancellationToken cancellationToken = default);
    Task SaveModelProfileAsync(OnnxModelProfile profile, CancellationToken cancellationToken = default);
    Task<OnnxModelProfile> ResetModelProfileAsync(CancellationToken cancellationToken = default);
}

public sealed record OnnxModelMetadata(
    string ModelName, string ModelPath, bool IsBuiltIn, long IrVersion, int OpsetVersion, string ProducerName,
    int InputWidth, int InputHeight, string InputTensorName, string OutputTensorName,
    IReadOnlyList<string> SupportedClasses, IReadOnlyList<string>? EmbeddedClasses = null);

public interface IOnnxModelInspector
{
    Task<OnnxModelProfile> GetBuiltInDefaultProfileAsync(CancellationToken cancellationToken = default);
    Task<Result<OnnxModelMetadata>> InspectAndValidateModelAsync(string modelPath, CancellationToken cancellationToken = default);
}

public sealed record DetectedFigureCandidate(
    int PageNumber, FigureCategory Category, BoundingBox BoundingBox, double Confidence,
    string? Caption, string? Label, DetectionSource Source, BoundingBox? CaptionBoundingBox = null,
    string? VocabPrefix = null, string? FigureNumber = null);

public sealed record RenderedRgbBitmap(int Width, int Height, int Dpi, byte[] RgbPixels, string RenderEngine);

public sealed record DetectedTextLabel(BoundingBox BoundingBox, string Text, double FontSize);

public sealed record AppliedMaskInfo(
    int Index, BoundingBox BoundingBoxPts, int PixelX, int PixelY, int PixelWidth, int PixelHeight,
    string? Text, string Style, string Source);

public interface IPdfRenderSession : IAsyncDisposable
{
    int PageCount { get; }
    string RenderEngine { get; }
    Task<Result<RenderedRgbBitmap>> RenderPageAsync(int pageNumber, int dpi, CancellationToken cancellationToken = default);
    Task<Result<RenderedRgbBitmap>> RenderRegionAsync(
        int pageNumber, PdfPageInfo pageInfo, BoundingBox regionPts, int dpi, int paddingPx, CancellationToken cancellationToken = default);
}

public interface IPdfPageRenderer
{
    Task<Result<IPdfRenderSession>> OpenSessionAsync(string pdfPath, CancellationToken cancellationToken = default);
}

public interface IFigureLabelDetector
{
    Task<Result<IReadOnlyList<DetectedTextLabel>>> DetectLabelsInRegionAsync(
        string pdfPath, int pageNumber, BoundingBox regionPts, CancellationToken cancellationToken = default);
}

public sealed record RenderedPageArtifact(
    int PageNumber, string OutputImagePath, string Format, int Dpi, int PixelWidth, int PixelHeight,
    long FileSizeBytes, bool OverlayEnabled, int OverlayFigureCount, string RenderEngine,
    int Quality = ExtractionOptions.DefaultQuality);

public interface IPageSnapshotExporter
{
    Task<Result<IReadOnlyList<RenderedPageArtifact>>> ExportPagesAsync(
        PdfDocument document, IReadOnlyList<int> pageNumbers, RenderPageOptions options,
        string outputDirectory, CancellationToken cancellationToken = default);
}

public sealed record PdfIngestionAnalysis(
    string SuggestedTitle, string Sha256Hash, IReadOnlyList<PdfPageInfo> Pages,
    IReadOnlyList<DetectedFigureCandidate> Candidates, OnnxModelMetadata ModelUsed,
    double EffectiveConfidenceThreshold, string InferenceEngine = "managed-fallback",
    DocumentVocabulary? Vocabulary = null, IReadOnlyList<string>? ScannedVocabPrefixes = null);

public sealed record OnnxRuntimeInstallRequest(string Provider = "directml", bool IncludeModel = true, bool Force = false);

public sealed record OnnxRuntimeFileEntry(string FileName, string FullPath, long SizeBytes, string Sha256);

public sealed record OnnxRuntimeStatus(
    bool IsInstalled, string InstalledProvider, string ActiveInferenceEngine, string? InstalledVersion,
    string RuntimeDirectory, string? ActiveModelName, string? ActiveModelPath, bool PretrainedModelInstalled,
    string PretrainedModelPath, long TotalInstalledBytes, IReadOnlyList<OnnxRuntimeFileEntry> Files,
    string? DiagnosticMessage = null);

public sealed record OnnxRuntimeUninstallResult(
    bool RemovedRuntime, bool RemovedPretrainedModel, int RemovedFileCount, long FreedBytes,
    string RuntimeDirectory, bool ResetModelToFallback, IReadOnlyList<string> PendingCleanupPaths);

public interface IOnnxRuntimeManager
{
    Task<OnnxRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<Result<OnnxRuntimeStatus>> InstallAsync(
        OnnxRuntimeInstallRequest request, Action<string>? onProgress = null, CancellationToken cancellationToken = default);
    Task<Result<OnnxRuntimeUninstallResult>> UninstallAsync(bool keepModel = false, CancellationToken cancellationToken = default);
    Task<OnnxRuntimeStatus> EnsureReadyForInferenceAsync(
        bool allowAutoInstall, Action<string>? onProgress = null, CancellationToken cancellationToken = default);
}

public interface IPdfIngestionEngine
{
    Task<Result<PdfIngestionAnalysis>> AnalyzePdfAsync(
        string pdfPath, OnnxModelProfile modelProfile, double? confidenceOverride = null, CancellationToken cancellationToken = default);
    Task<Result> InitializeWorkspaceAsync(string workspaceDirectory, CancellationToken cancellationToken = default);
    Task<Result<string>> MovePdfToProjectDirectoryAsync(string sourcePdfPath, string projectDirectory, CancellationToken cancellationToken = default);
    Task<Result> VerifyPdfIntegrityAsync(string storedPdfPath, string expectedSha256, CancellationToken cancellationToken = default);
}

public sealed record ExtractedFigureArtifact(
    string FigureId, int Index, int PageNumber, string OutputImagePath, string? MetadataJsonPath,
    string Format, int Dpi, int PaddingPx, int PixelWidth, int PixelHeight, long FileSizeBytes,
    bool IncludedCaption, string RenderEngine, IReadOnlyList<AppliedMaskInfo> AppliedMasks,
    int Quality = ExtractionOptions.DefaultQuality, string? FigureNumber = null, string? Label = null,
    string? Caption = null, double Confidence = 0, string? Category = null);

public interface IFigureImageExtractor
{
    Task<Result<ExtractedFigureArtifact>> ExtractFigureAsync(
        PdfDocument document, DocumentFigure figure, ExtractionOptions options, string outputDirectory, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ExtractedFigureArtifact>>> ExtractFiguresAsync(
        PdfDocument document, IReadOnlyList<DocumentFigure> figures, ExtractionOptions options, string outputDirectory, CancellationToken cancellationToken = default);
    Task<Result<string>> WriteManifestAsync(
        PdfDocument document, IReadOnlyList<ExtractedFigureArtifact> artifacts, string outputDirectory, CancellationToken cancellationToken = default);
    Task<Result> VerifyExtractedImageAsync(string imagePath, CancellationToken cancellationToken = default);
}
