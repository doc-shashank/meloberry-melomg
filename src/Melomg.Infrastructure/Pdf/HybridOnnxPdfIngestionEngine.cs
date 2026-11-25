using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Models;
using Melomg.Infrastructure.Onnx;

namespace Melomg.Infrastructure.Pdf;

public sealed record ParsedPdfImageXObject(
    string Name,
    int ObjectNumber,
    int PixelWidth,
    int PixelHeight);

public sealed record ParsedImagePlacement(
    string ResourceName,
    BoundingBox Box,
    ParsedPdfImageXObject? Image);

public sealed record ParsedVectorShape(
    bool IsRectangle,
    BoundingBox Bounds,
    int SegmentCount);

public sealed record ParsedTextSpan(
    double X,
    double Y,
    double Width,
    double FontSize,
    string Text,
    double Height = 0)
{
    public double EffectiveHeight => Height > 0 ? Height : Math.Max(8.0, FontSize * 1.18);
    public double Bottom => Y + EffectiveHeight;
    public double Right => X + Width;
}

public sealed record ParsedPdfFormXObject(
    string Name,
    int ObjectNumber,
    double[] Matrix,
    string DictionaryText,
    int StreamOffset,
    int StreamLength);

public sealed record ParsedPageScene(
    PdfPageInfo PageInfo,
    IReadOnlyList<ParsedImagePlacement> ImagePlacements,
    IReadOnlyList<ParsedVectorShape> VectorShapes,
    IReadOnlyList<ParsedTextSpan> TextSpans);

public sealed partial class HybridOnnxPdfIngestionEngine(
    OnnxProtobufEngine onnxEngine,
    IPdfPageRenderer pageRenderer,
    NativeOnnxRuntimeSessionHost? sessionHost = null,
    IOnnxRuntimeManager? runtimeManager = null) : IPdfIngestionEngine, IFigureLabelDetector
{
    private const int TensorGridWidth = 80;
    private const int TensorGridHeight = 104;
    private const int MaxDecompressedStreamBytes = 64 * 1024 * 1024;

    private sealed class PdfStreamLimitExceededException(string message) : IOException(message);

    private sealed record CachedPdfDocument(
        string FullPath,
        long FileLength,
        long LastWriteTimeUtcTicks,
        string Sha256,
        byte[] PdfBytes,
        PdfDocumentIndex Index,
        ConcurrentDictionary<int, ParsedPageScene> Scenes);

    private readonly Lock _sceneCacheLock = new();
    private CachedPdfDocument? _sceneCache;

    private async Task<Result<CachedPdfDocument>> GetOrBuildCachedDocumentAsync(
        string fullPath,
        byte[]? preloadedBytes = null,
        CancellationToken cancellationToken = default)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists)
            return DocumentErrors.PdfFileNotFound(fullPath);

        long fileLength = fi.Length;
        long lastWriteTicks = fi.LastWriteTimeUtc.Ticks;

        if (preloadedBytes is null)
        {
            lock (_sceneCacheLock)
            {
                if (_sceneCache is { } cached &&
                    string.Equals(cached.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    cached.FileLength == fileLength &&
                    cached.LastWriteTimeUtcTicks == lastWriteTicks)
                {
                    return cached;
                }
            }
        }

        byte[] pdfBytes = preloadedBytes ?? await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (!HasPdfSignature(pdfBytes))
            return DocumentErrors.InvalidPdfFormat(fullPath, "Missing '%PDF-' magic header.");

        string sha256 = ComputeSha256Hex(pdfBytes);
        lock (_sceneCacheLock)
        {
            if (_sceneCache is { } cached &&
                string.Equals(cached.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cached.Sha256, sha256, StringComparison.Ordinal))
            {
                return cached;
            }
        }

        var indexResult = BuildPdfDocumentIndex(pdfBytes, fullPath);
        if (!indexResult.IsSuccess)
            return indexResult.Error!;

        var entry = new CachedPdfDocument(
            fullPath,
            pdfBytes.LongLength,
            lastWriteTicks,
            sha256,
            pdfBytes,
            indexResult.Value,
            new ConcurrentDictionary<int, ParsedPageScene>());

        lock (_sceneCacheLock)
            _sceneCache = entry;

        return entry;
    }

    public async Task<Result<PdfIngestionAnalysis>> AnalyzePdfAsync(
        string pdfPath,
        OnnxModelProfile modelProfile,
        double? confidenceOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            return DocumentErrors.PdfFileNotFound(string.Empty);

        var fullPath = Path.GetFullPath(pdfPath.Trim());
        if (!File.Exists(fullPath))
            return DocumentErrors.PdfFileNotFound(fullPath);

        byte[] pdfBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (!HasPdfSignature(pdfBytes))
            return DocumentErrors.InvalidPdfFormat(fullPath, "Missing '%PDF-' magic header.");

        double effectiveConfidence = confidenceOverride ?? modelProfile.ConfidenceThreshold;
        if (double.IsNaN(effectiveConfidence) || effectiveConfidence < 0.05 || effectiveConfidence > 0.99)
            return DocumentErrors.InvalidConfidenceThreshold(effectiveConfidence);

        await onnxEngine.EnsureBuiltInModelExistsAsync(cancellationToken);
        var modelInspection = await onnxEngine.InspectAndValidateModelAsync(modelProfile.ModelPath, cancellationToken);
        if (!modelInspection.IsSuccess)
            return modelInspection.Error!;

        var modelMeta = modelInspection.Value;
        var cachedResult = await GetOrBuildCachedDocumentAsync(fullPath, pdfBytes, cancellationToken);
        if (!cachedResult.IsSuccess)
            return cachedResult.Error!;

        var cachedDoc = cachedResult.Value;
        var docIndex = cachedDoc.Index;
        var sceneMap = cachedDoc.Scenes;
        string sha256 = cachedDoc.Sha256;
        var allCandidates = new List<DetectedFigureCandidate>();
        var pageInfos = new List<PdfPageInfo>(docIndex.OrderedPageIds.Count);

        IPdfRenderSession? renderSession = null;
        var sessionRes = await pageRenderer.OpenSessionAsync(fullPath, cancellationToken);
        if (sessionRes.IsSuccess && sessionRes.Value.RenderEngine == "windows-pdf")
            renderSession = sessionRes.Value;
        else if (sessionRes.IsSuccess)
            await sessionRes.Value.DisposeAsync();

        var runtimeStatus = runtimeManager is not null
            ? await runtimeManager.GetStatusAsync(cancellationToken)
            : null;
        bool useNativeOnnx = sessionHost is not null &&
            runtimeStatus is { IsInstalled: true } &&
            !modelProfile.IsBuiltIn &&
            File.Exists(modelMeta.ModelPath) &&
            renderSession is not null;
        string actualInferenceEngine = "managed-fallback";

        var scenes = new List<ParsedPageScene>(docIndex.OrderedPageIds.Count);
        var scannedPrefixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < docIndex.OrderedPageIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageNum = i + 1;
            if (!sceneMap.TryGetValue(pageNum, out var scene) || scene is null)
            {
                var sceneResult = ParseSinglePageScene(pdfBytes, docIndex, docIndex.OrderedPageIds[i], pageNum, fullPath);
                if (!sceneResult.IsSuccess) return sceneResult.Error!;
                scene = sceneResult.Value;
                sceneMap[pageNum] = scene;
            }

            scenes.Add(scene);
            foreach (var span in MergeTextSpansOnSameLine(scene.TextSpans, maxGapPts: 14.0))
            {
                if (FigureNumberNormalizer.TryParseFigureCaption(span.Text, out var parsedCap))
                    scannedPrefixCounts[parsedCap.Prefix] = scannedPrefixCounts.GetValueOrDefault(parsedCap.Prefix) + 1;
            }
        }

        var scannedPrefixes = scannedPrefixCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToArray();
        string? dominantPrefix = scannedPrefixes.Length > 0 ? scannedPrefixes[0] : null;

        try
        {
            for (int i = 0; i < scenes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageNum = i + 1;
                var scene = scenes[i];
                RenderedRgbBitmap? pageBitmap = null;
                NeuralLayoutOutput? neuralLayout = null;

                if (renderSession is not null && pageNum <= renderSession.PageCount)
                {
                    int renderDpi = useNativeOnnx
                        ? Math.Clamp((int)Math.Round(Math.Max(modelMeta.InputWidth, modelMeta.InputHeight) * 72.0 / Math.Max(1.0, Math.Max(scene.PageInfo.WidthPts, scene.PageInfo.HeightPts))), 96, 150)
                        : 72;

                    var bmpRes = await renderSession.RenderPageAsync(pageNum, renderDpi, cancellationToken);
                    if (bmpRes.IsSuccess)
                        pageBitmap = bmpRes.Value;
                }

                if (useNativeOnnx && pageBitmap is not null && runtimeStatus is not null)
                {
                    if (sessionHost!.TryRunLayoutDetection(
                        runtimeStatus.RuntimeDirectory,
                        runtimeStatus.InstalledProvider,
                        modelMeta.ModelPath,
                        modelMeta.EmbeddedClasses,
                        pageBitmap,
                        effectiveConfidence,
                        modelProfile.IouThreshold,
                        out var detectedNeural,
                        out var pageEngine,
                        out _,
                        modelMeta.InputWidth,
                        modelMeta.InputHeight))
                    {
                        neuralLayout = detectedNeural;
                        actualInferenceEngine = pageEngine;
                    }
                }

                pageInfos.Add(scene.PageInfo);
                var pageCandidates = DetectFiguresOnPage(
                    scene,
                    pageBitmap,
                    modelMeta,
                    effectiveConfidence,
                    modelProfile.IouThreshold,
                    neuralLayout,
                    dominantPrefix);
                allCandidates.AddRange(pageCandidates);
            }
        }
        finally
        {
            if (renderSession is not null)
                await renderSession.DisposeAsync();
        }

        var vocab = DocumentVocabulary.BuildFromEntries(
            allCandidates.Select(c => (
                c.VocabPrefix,
                string.IsNullOrWhiteSpace(c.FigureNumber)
                    ? null
                    : FigureNumberNormalizer.NormalizeLookupNumber(c.FigureNumber))),
            scannedPrefixes);

        return new PdfIngestionAnalysis(
            docIndex.SuggestedTitle,
            sha256,
            pageInfos,
            allCandidates,
            modelMeta,
            effectiveConfidence,
            actualInferenceEngine,
            vocab,
            scannedPrefixes);
    }

    public Task<Result> InitializeWorkspaceAsync(
        string workspaceDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workspaceDirectory);
        Directory.CreateDirectory(Path.Combine(workspaceDirectory, "figures"));
        return Task.FromResult(Result.Success());
    }

    public async Task<Result<string>> MovePdfToProjectDirectoryAsync(
        string sourcePdfPath,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var fullSource = Path.GetFullPath(sourcePdfPath.Trim());
        if (!File.Exists(fullSource))
            return DocumentErrors.PdfFileNotFound(fullSource);

        byte[] header = new byte[1024];
        int bytesRead;
        await using (var fs = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bytesRead = await fs.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        }

        if (!HasPdfSignature(header.AsSpan(0, bytesRead)))
            return DocumentErrors.InvalidPdfFormat(fullSource, "Missing '%PDF-' magic header.");

        var fullProjectDir = Path.GetFullPath(projectDirectory.Trim());
        Directory.CreateDirectory(fullProjectDir);
        Directory.CreateDirectory(Path.Combine(fullProjectDir, "figures"));

        string destPath = Path.Combine(fullProjectDir, Path.GetFileName(fullSource));
        if (string.Equals(fullSource, destPath, StringComparison.OrdinalIgnoreCase))
            return destPath;

        if (File.Exists(destPath))
            return DocumentErrors.ProjectPdfAlreadyExists(destPath);

        try
        {
            File.Move(fullSource, destPath);
            return destPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DocumentErrors.PdfMoveFailed(fullSource, destPath, ex.Message);
        }
    }

    public async Task<Result> VerifyPdfIntegrityAsync(
        string storedPdfPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storedPdfPath) || !File.Exists(storedPdfPath))
            return DocumentErrors.PdfFileNotFound(storedPdfPath ?? string.Empty);

        await using var fs = new FileStream(storedPdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        byte[] header = new byte[1024];
        int headerRead = await fs.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (!HasPdfSignature(header.AsSpan(0, headerRead)))
            return DocumentErrors.InvalidPdfFormat(storedPdfPath, "Stored PDF lacks '%PDF-' header.");

        fs.Position = 0;
        byte[] hashBytes = await SHA256.HashDataAsync(fs, cancellationToken);
        string actualHash = Convert.ToHexStringLower(hashBytes);
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            string expectedPreview = expectedSha256 is { Length: > 12 }
                ? $"{expectedSha256[..12]}..."
                : (expectedSha256 ?? string.Empty);
            string actualPreview = actualHash.Length > 12 ? $"{actualHash[..12]}..." : actualHash;
            return DocumentErrors.InvalidPdfFormat(storedPdfPath, $"SHA-256 mismatch (expected {expectedPreview}, got {actualPreview}).");
        }

        return Result.Success();
    }

    // =========================================================================
    // Hybrid ONNX + Pixel-Aware Visual + PDF XObject Figure Detector
    // =========================================================================

    private List<DetectedFigureCandidate> DetectFiguresOnPage(
        ParsedPageScene scene,
        RenderedRgbBitmap? pageBitmap,
        OnnxModelMetadata modelMeta,
        double confidenceThreshold,
        double iouThreshold,
        NeuralLayoutOutput? neuralLayout = null,
        string? dominantVocabPrefix = null)
    {
        double pageW = scene.PageInfo.WidthPts;
        double pageH = scene.PageInfo.HeightPts;

        // 1. Merge running baseline text spans so split textbook captions become complete lines without bridging column gutters
        var mergedTextSpans = MergeTextSpansOnSameLine(scene.TextSpans, maxGapPts: 14.0);

        // 2. Group adjacent image tiles & filter tiny header/icon noise
        var meaningfulImages = FilterAndMergeImagePlacements(scene.ImagePlacements, pageW, pageH);

        // 3. O(N) spatial-grid clustering of vector graphics shapes (drops tables, booktabs rule-tables, and bordered prose callout boxes)
        var vectorClusters = ClusterVectorRegionsGrid(scene.VectorShapes, mergedTextSpans, pageW, pageH);

        // 4. Fuse composite vector+image diagrams (e.g., labeled multi-panel figures with arrows)
        var (compositeBoxes, consumedImages, consumedVectors) = BuildCompositeRegions(meaningfulImages, vectorClusters, mergedTextSpans, pageW, pageH);

        // 5. Build [3, H, W] input tensor for ONNX layout model (structural + rendered visual channels)
        int plane = TensorGridWidth * TensorGridHeight;
        var tensor = new float[3 * plane];

        foreach (var img in meaningfulImages)
            PaintBoxIntoTensor(tensor, 0, TensorGridWidth, TensorGridHeight, img.Box, pageW, pageH, 1.0f);

        foreach (var cluster in vectorClusters)
        {
            float intensity = Math.Clamp(0.65f + cluster.ShapeCount * 0.04f, 0.65f, 1.0f);
            PaintBoxIntoTensor(tensor, plane, TensorGridWidth, TensorGridHeight, cluster.Box, pageW, pageH, intensity);
        }

        foreach (var comp in compositeBoxes)
            PaintBoxIntoTensor(tensor, 0, TensorGridWidth, TensorGridHeight, comp.Box, pageW, pageH, 0.95f);

        foreach (var text in mergedTextSpans)
        {
            bool looksLikeCaption = IsCaptionText(text.Text);
            float weight = looksLikeCaption ? 0.95f : 0.25f;
            var textBox = new BoundingBox(
                Math.Clamp(text.X, 0, Math.Max(0, pageW - 2)),
                Math.Clamp(text.Y, 0, Math.Max(0, pageH - 2)),
                Math.Clamp(Math.Max(text.Width, 24), 1, Math.Max(1, pageW - text.X)),
                Math.Clamp(Math.Max(text.EffectiveHeight, 12), 1, Math.Max(1, pageH - text.Y)));
            PaintBoxIntoTensor(tensor, 2 * plane, TensorGridWidth, TensorGridHeight, textBox, pageW, pageH, weight);
        }

        List<BoundingBox> visualGraphicRegions = [];
        if (pageBitmap is not null)
            visualGraphicRegions = AugmentTensorWithRenderedVisualFeatures(tensor, plane, pageBitmap, mergedTextSpans, pageW, pageH);

        // 6. Execute ONNX layout model inference (prefer real neural ONNX Runtime when installed; managed fallback otherwise)
        IReadOnlyList<OnnxRawDetection> onnxDetections = neuralLayout is not null
            ? neuralLayout.Figures
                .Select(f => new OnnxRawDetection(
                    f.NormalizedBox.X,
                    f.NormalizedBox.Y,
                    f.NormalizedBox.Width,
                    f.NormalizedBox.Height,
                    f.Confidence,
                    f.Category))
                .ToList()
            : onnxEngine.RunLayoutInference(
                tensor,
                TensorGridWidth,
                TensorGridHeight,
                modelMeta,
                confidenceThreshold: Math.Min(confidenceThreshold, 0.30),
                iouThreshold);

        var neuralCaptionBoxes = neuralLayout is not null
            ? neuralLayout.CaptionNormalizedBoxes
                .Select(cb => new BoundingBox(
                    Math.Round(Math.Clamp(cb.X * pageW, 0, Math.Max(0, pageW - 4)), 2),
                    Math.Round(Math.Clamp(cb.Y * pageH, 0, Math.Max(0, pageH - 4)), 2),
                    Math.Round(Math.Clamp(cb.Width * pageW, 4, pageW), 2),
                    Math.Round(Math.Clamp(cb.Height * pageH, 4, pageH), 2)))
                .ToList()
            : [];

        // 7. Collect exact candidate boxes and fuse with ONNX predictions
        var regionList = new List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source)>();

        foreach (var comp in compositeBoxes)
            regionList.Add((ClampToPage(comp.Box, pageW, pageH), comp.Confidence, comp.Category, DetectionSource.Hybrid));

        for (int i = 0; i < meaningfulImages.Count; i++)
        {
            if (consumedImages.Contains(i)) continue;
            var img = meaningfulImages[i];
            if (img.Box.Width < 38 || img.Box.Height < 30) continue;
            regionList.Add((ClampToPage(img.Box, pageW, pageH), 0.90, FigureCategory.Figure, DetectionSource.PageLayout));
        }

        for (int i = 0; i < vectorClusters.Count; i++)
        {
            if (consumedVectors.Contains(i)) continue;
            var vec = vectorClusters[i];
            regionList.Add((ClampToPage(vec.Box, pageW, pageH), vec.Confidence, vec.SuggestedCategory ?? FigureCategory.Diagram, DetectionSource.Hybrid));
        }

        List<PageCaptionAnchor>? offlineCaptionAnchors = neuralLayout is null
            ? ExtractPageCaptionAnchors(mergedTextSpans, pageW, pageH)
            : null;

        bool HasNearbyFigureCaption(BoundingBox box) =>
            offlineCaptionAnchors is { Count: > 0 } &&
            offlineCaptionAnchors.Any(a => TryScoreCaptionMatch(box, a, dominantVocabPrefix, out _));

        // Only promote heuristic visual-only blobs when real neural ONNX layout inference did not run
        if (neuralLayout is null)
        {
            foreach (var vBox in visualGraphicRegions)
            {
                bool covered = regionList.Any(r =>
                    r.Box.ComputeIoU(vBox) >= 0.20 ||
                    vBox.ComputeContainment(r.Box) >= 0.55 ||
                    r.Box.ComputeContainment(vBox) >= 0.55);
                if (covered)
                    continue;
                if (ComputeTextCoverageRatio(vBox, mergedTextSpans) > 0.35)
                    continue;
                if (!HasNearbyFigureCaption(vBox) && !(vBox.Width >= 95 && vBox.Height >= 70))
                    continue;

                regionList.Add((ClampToPage(vBox, pageW, pageH), 0.78, FigureCategory.Figure, DetectionSource.OnnxModel));
            }
        }

        var fused = new List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source)>();
        var matchedRegionIndices = new HashSet<int>();

        foreach (var det in onnxDetections)
        {
            double bx = Math.Clamp(det.NormX * pageW, 0, Math.Max(0, pageW - 4));
            double by = Math.Clamp(det.NormY * pageH, 0, Math.Max(0, pageH - 4));
            double bw = Math.Clamp(det.NormWidth * pageW, 4, pageW - bx);
            double bh = Math.Clamp(det.NormHeight * pageH, 4, pageH - by);
            var onnxBox = new BoundingBox(Math.Round(bx, 2), Math.Round(by, 2), Math.Round(bw, 2), Math.Round(bh, 2));

            int bestIdx = -1;
            double bestOverlap = 0;
            for (int i = 0; i < regionList.Count; i++)
            {
                var rBox = regionList[i].Box;
                double overlap = Math.Max(onnxBox.ComputeIoU(rBox), rBox.ComputeContainment(onnxBox));
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0 && bestOverlap >= 0.25)
            {
                var r = regionList[bestIdx];
                BoundingBox addedBox;
                if (neuralLayout is not null)
                {
                    bool preferCleanStructuralHeader =
                        r.Source == DetectionSource.PageLayout &&
                        onnxBox.Y < 46 &&
                        r.Box.Y >= 46 &&
                        r.Box.ComputeContainment(onnxBox) >= 0.80 &&
                        onnxBox.Width <= r.Box.Width * 1.35;

                    addedBox = (onnxBox.ComputeIoU(r.Box) >= 0.45 && r.Source == DetectionSource.PageLayout) || preferCleanStructuralHeader
                        ? r.Box
                        : onnxBox;

                    double fusedConf = Math.Clamp(0.65 * det.Confidence + 0.35 * Math.Max(r.Confidence, 0.85), 0.0, 0.99);
                    var chosenCat = det.Category != FigureCategory.Figure ? det.Category : r.Category;
                    fused.Add((addedBox, fusedConf, chosenCat, DetectionSource.Hybrid));
                }
                else
                {
                    addedBox = r.Box;
                    double boostedConf = Math.Clamp(0.50 * det.Confidence + 0.50 * Math.Max(r.Confidence, 0.90), 0.0, 0.99);
                    var src = r.Source == DetectionSource.PageLayout ? DetectionSource.Hybrid : r.Source;
                    fused.Add((addedBox, boostedConf, r.Category, src));
                }

                matchedRegionIndices.Add(bestIdx);
                for (int i = 0; i < regionList.Count; i++)
                {
                    var rBox = regionList[i].Box;
                    if (rBox.ComputeContainment(addedBox) >= 0.55 || addedBox.ComputeIoU(rBox) >= 0.25)
                        matchedRegionIndices.Add(i);
                }
            }
            else if (det.Confidence >= (neuralLayout is not null ? confidenceThreshold : Math.Max(confidenceThreshold, 0.55)) &&
                     onnxBox.Width >= 32 && onnxBox.Height >= 24)
            {
                if (neuralLayout is null)
                {
                    double textCoverage = ComputeTextCoverageRatio(onnxBox, mergedTextSpans);
                    if (IsProseCalloutBox(onnxBox, mergedTextSpans) ||
                        textCoverage > 0.35 ||
                        (!HasNearbyFigureCaption(onnxBox) && !(onnxBox.Width >= 95 && onnxBox.Height >= 70 && textCoverage <= 0.15)))
                    {
                        continue;
                    }
                }

                fused.Add((onnxBox, det.Confidence, det.Category, DetectionSource.OnnxModel));
                for (int i = 0; i < regionList.Count; i++)
                {
                    var rBox = regionList[i].Box;
                    if (rBox.ComputeContainment(onnxBox) >= 0.55 || onnxBox.ComputeIoU(rBox) >= 0.25)
                        matchedRegionIndices.Add(i);
                }
            }
        }

        for (int i = 0; i < regionList.Count; i++)
        {
            if (matchedRegionIndices.Contains(i))
                continue;

            var r = regionList[i];
            if (neuralLayout is not null)
            {
                if (r.Source == DetectionSource.PageLayout || r.Confidence >= 0.80)
                    fused.Add(r);
            }
            else
            {
                bool isUnmatchedVectorOrVisual = r.Source != DetectionSource.PageLayout;
                if (!isUnmatchedVectorOrVisual ||
                    HasNearbyFigureCaption(r.Box) ||
                    (r.Confidence >= 0.85 && !IsProseCalloutBox(r.Box, mergedTextSpans) && ComputeTextCoverageRatio(r.Box, mergedTextSpans) <= 0.35))
                {
                    fused.Add(r);
                }
            }
        }

        // 8. NMS + confidence filtering + containment absorption + caption pairing + label expansion + visual rim snap + ordering
        var filtered = fused
            .Where(x => x.Confidence >= confidenceThreshold)
            .OrderByDescending(x => x.Confidence)
            .ToList();

        var deduplicated = new List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source)>();
        foreach (var item in filtered)
        {
            bool overlaps = deduplicated.Any(existing =>
                item.Box.ComputeIoU(existing.Box) > Math.Max(iouThreshold, 0.45) ||
                item.Box.ComputeContainment(existing.Box) > 0.72);
            if (overlaps)
                continue;

            if (item.Box.Area < pageW * pageH * 0.85)
            {
                deduplicated.RemoveAll(existing =>
                    existing.Box.ComputeContainment(item.Box) >= 0.65 &&
                    existing.Box.Area < item.Box.Area * 0.72);
            }

            deduplicated.Add(item);
        }

        var ordered = deduplicated
            .OrderBy(x => x.Box.Y)
            .ThenBy(x => x.Box.X)
            .Select(x => (
                Box: TrimRunningHeaderAndFooter(x.Box, pageBitmap, mergedTextSpans, pageW, pageH),
                x.Confidence,
                x.Category,
                x.Source))
            .ToList();

        var assigned = AssignCaptionsToPageFigures(ordered, mergedTextSpans, pageW, pageH, dominantVocabPrefix);

        var candidates = new List<DetectedFigureCandidate>(assigned.Count);
        var claimedNeuralCaptionIndices = new HashSet<int>();
        int localSeq = 1;
        foreach (var (trimmedBox, confidence, category, source, anchor) in assigned)
        {
            var captionMatch = anchor?.Text;
            var captionBox = anchor?.Box;

            // Structural exclusion: discard candidates whose nearest aligned caption belongs to a Table, Box, Equation, or Algorithm
            if (captionMatch is null && FindNearestNonFigureCaption(trimmedBox, mergedTextSpans, pageW, pageH) is not null)
                continue;

            var parsedCap = anchor?.Parsed;
            if (captionBox is null && neuralCaptionBoxes.Count > 0)
            {
                var (neuralCapBox, neuralCapIdx) = FindNearestNeuralCaptionBox(trimmedBox, neuralCaptionBoxes, claimedNeuralCaptionIndices);
                if (captionMatch is null && neuralCapBox is not null)
                {
                    var expanded = ExpandRect(neuralCapBox, 4.0);
                    var overlappingSpans = mergedTextSpans
                        .Where(s => !string.IsNullOrWhiteSpace(s.Text) &&
                                    s.Right >= expanded.X && s.X <= expanded.Right &&
                                    s.Bottom >= expanded.Y && s.Y <= expanded.Bottom)
                        .OrderBy(s => Math.Round(s.Y / 4.0))
                        .ThenBy(s => s.X)
                        .Select(s => s.Text.Trim())
                        .ToList();
                    if (overlappingSpans.Count > 0)
                    {
                        string joinedCaption = string.Join(' ', overlappingSpans).Trim();
                        if (!FigureNumberNormalizer.IsNonFigureArtifactCaption(joinedCaption) &&
                            FigureNumberNormalizer.TryParseFigureCaption(joinedCaption, out var neuralParsed))
                        {
                            captionBox = neuralCapBox;
                            captionMatch = joinedCaption;
                            parsedCap = neuralParsed;
                            claimedNeuralCaptionIndices.Add(neuralCapIdx);
                        }
                    }
                }
            }
            var resolvedCategory = RefineCategoryFromCaption(category, captionMatch);
            var label = parsedCap?.ShortLabel ?? $"{resolvedCategory} p{scene.PageInfo.PageNumber}.{localSeq}";

            // Strictly carve matched caption out of crop bounds before & after label expansion and visual ink snapping
            var carvedBox = CarveCaptionOutOfFigureBox(trimmedBox, captionBox, pageW, pageH);
            var refinedBox = ExpandBoxWithAttachedLabels(carvedBox, mergedTextSpans, captionBox, pageW, pageH);
            if (pageBitmap is not null)
                refinedBox = SnapBoxEdgesToVisualInk(refinedBox, pageBitmap, captionBox, pageW, pageH);
            refinedBox = CarveCaptionOutOfFigureBox(refinedBox, captionBox, pageW, pageH);

            localSeq++;

            candidates.Add(new DetectedFigureCandidate(
                scene.PageInfo.PageNumber,
                resolvedCategory,
                refinedBox,
                Math.Round(confidence, 4),
                captionMatch,
                label,
                source,
                captionBox,
                parsedCap?.Prefix,
                parsedCap?.Number));
        }

        return AbsorbContainedSubFigures(candidates, pageW);
    }

    internal static List<DetectedFigureCandidate> AbsorbContainedSubFigures(
        List<DetectedFigureCandidate> candidates,
        double pageWidthPts = 0)
    {
        if (candidates.Count <= 1)
            return candidates;

        var byAreaDesc = candidates
            .Select((c, idx) => (Candidate: c, Index: idx, Area: c.BoundingBox.Area))
            .OrderByDescending(x => x.Area)
            .ToList();

        var suppressed = new HashSet<int>();
        foreach (var outer in byAreaDesc)
        {
            if (suppressed.Contains(outer.Index))
                continue;

            string? outerShort = ExtractShortLabel(outer.Candidate.Caption);

            foreach (var inner in byAreaDesc)
            {
                if (inner.Index == outer.Index || suppressed.Contains(inner.Index))
                    continue;
                if (inner.Candidate.PageNumber != outer.Candidate.PageNumber)
                    continue;
                if (inner.Area >= outer.Area * 0.75)
                    continue;
                if (inner.Candidate.BoundingBox.ComputeContainment(outer.Candidate.BoundingBox) < 0.65)
                    continue;

                string? innerShort = ExtractShortLabel(inner.Candidate.Caption);
                if (outerShort is not null && innerShort is not null &&
                    !string.Equals(outerShort, innerShort, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                suppressed.Add(inner.Index);
            }
        }

        var kept = candidates
            .Where((_, idx) => !suppressed.Contains(idx))
            .ToList();

        if (kept.Count <= 1)
            return kept;

        return OrderCandidatesInReadingOrder(kept, pageWidthPts);
    }

    private static List<DetectedFigureCandidate> OrderCandidatesInReadingOrder(
        List<DetectedFigureCandidate> kept,
        double pageWidthPts)
    {
        double pageW = pageWidthPts > 0
            ? pageWidthPts
            : Math.Max(612.0, kept.Max(c => c.BoundingBox.Right));

        var ordered = new List<DetectedFigureCandidate>(kept.Count);
        foreach (var pageGroup in kept.GroupBy(c => c.PageNumber).OrderBy(g => g.Key))
        {
            var pageItems = pageGroup.ToList();
            bool hasLeftOnly = pageItems.Any(c => c.BoundingBox.Right < pageW * 0.54);
            bool hasRightOnly = pageItems.Any(c => c.BoundingBox.X > pageW * 0.46);

            List<DetectedFigureCandidate> pageOrdered;
            if (hasLeftOnly && hasRightOnly)
            {
                var spanning = pageItems
                    .Where(c => !(c.BoundingBox.Right < pageW * 0.54) && !(c.BoundingBox.X > pageW * 0.46))
                    .OrderBy(c => c.BoundingBox.Y)
                    .ThenBy(c => c.BoundingBox.X)
                    .ToList();
                var columnItems = pageItems
                    .Where(c => c.BoundingBox.Right < pageW * 0.54 || c.BoundingBox.X > pageW * 0.46)
                    .ToList();

                int bandCount = spanning.Count + 1;
                var leftBands = Enumerable.Range(0, bandCount).Select(_ => new List<DetectedFigureCandidate>()).ToArray();
                var rightBands = Enumerable.Range(0, bandCount).Select(_ => new List<DetectedFigureCandidate>()).ToArray();

                foreach (var item in columnItems)
                {
                    double centerY = item.BoundingBox.Y + item.BoundingBox.Height * 0.5;
                    int band = 0;
                    while (band < spanning.Count && centerY >= spanning[band].BoundingBox.Y + spanning[band].BoundingBox.Height * 0.5)
                        band++;

                    if (item.BoundingBox.Right < pageW * 0.54)
                        leftBands[band].Add(item);
                    else
                        rightBands[band].Add(item);
                }

                pageOrdered = new List<DetectedFigureCandidate>(pageItems.Count);
                for (int b = 0; b < bandCount; b++)
                {
                    pageOrdered.AddRange(leftBands[b].OrderBy(c => c.BoundingBox.Y).ThenBy(c => c.BoundingBox.X));
                    pageOrdered.AddRange(rightBands[b].OrderBy(c => c.BoundingBox.Y).ThenBy(c => c.BoundingBox.X));
                    if (b < spanning.Count)
                        pageOrdered.Add(spanning[b]);
                }
            }
            else
            {
                pageOrdered = pageItems
                    .OrderBy(c => c.BoundingBox.Y)
                    .ThenBy(c => c.BoundingBox.X)
                    .ToList();
            }

            var seriesSlots = new Dictionary<string, List<(int Slot, int Ordinal, string Norm, DetectedFigureCandidate Candidate)>>(StringComparer.Ordinal);
            for (int slot = 0; slot < pageOrdered.Count; slot++)
            {
                var cand = pageOrdered[slot];
                string norm = FigureNumberNormalizer.NormalizeLookupNumber(cand.FigureNumber);
                if (FigureSequenceAnalyzer.TryParseSeriesOrdinal(norm, out var seriesKey, out int ordinal))
                {
                    if (!seriesSlots.TryGetValue(seriesKey, out var list))
                        seriesSlots[seriesKey] = list = [];
                    list.Add((slot, ordinal, norm, cand));
                }
            }

            foreach (var list in seriesSlots.Values)
            {
                if (list.Count <= 1) continue;
                var targetSlots = list.Select(x => x.Slot).OrderBy(s => s).ToList();
                var sortedCandidates = list
                    .OrderBy(x => x.Ordinal)
                    .ThenBy(x => x.Norm, StringComparer.Ordinal)
                    .ThenBy(x => x.Slot)
                    .Select(x => x.Candidate)
                    .ToList();
                for (int k = 0; k < targetSlots.Count; k++)
                    pageOrdered[targetSlots[k]] = sortedCandidates[k];
            }

            ordered.AddRange(pageOrdered);
        }

        return ordered;
    }

    private static double ComputeTextCoverageRatio(BoundingBox box, IReadOnlyList<ParsedTextSpan> textSpans)
    {
        if (box.Area <= 0) return 0;
        double textArea = 0;
        foreach (var s in textSpans)
        {
            if (string.IsNullOrWhiteSpace(s.Text)) continue;
            double ix = Math.Max(0, Math.Min(box.Right, s.Right) - Math.Max(box.X, s.X));
            double iy = Math.Max(0, Math.Min(box.Bottom, s.Bottom) - Math.Max(box.Y, s.Y));
            if (ix > 0 && iy > 0)
                textArea += ix * iy;
        }
        return Math.Min(1.0, textArea / box.Area);
    }

    private static (BoundingBox? Box, int Index) FindNearestNeuralCaptionBox(
        BoundingBox figureBox,
        IReadOnlyList<BoundingBox> neuralCaptions,
        HashSet<int> claimedIndices)
    {
        BoundingBox? best = null;
        int bestIdx = -1;
        double bestDist = 75.0;
        for (int i = 0; i < neuralCaptions.Count; i++)
        {
            if (claimedIndices.Contains(i))
                continue;

            var cb = neuralCaptions[i];
            double overlapX = Math.Max(0, Math.Min(figureBox.Right, cb.Right) - Math.Max(figureBox.X, cb.X));
            if (overlapX <= 0 && Math.Abs((cb.X + cb.Width * 0.5) - (figureBox.X + figureBox.Width * 0.5)) > figureBox.Width * 0.65)
                continue;

            bool isBottomSide = cb.Y >= figureBox.Bottom - 32 && cb.Y >= figureBox.Y + figureBox.Height * 0.45;
            bool isTopSide = cb.Bottom <= figureBox.Y + 28 && cb.Bottom <= figureBox.Y + figureBox.Height * 0.55;
            double dist = isBottomSide
                ? Math.Abs(cb.Y - figureBox.Bottom)
                : (isTopSide ? Math.Abs(figureBox.Y - cb.Bottom) + 8 : 999);

            if (dist < bestDist)
            {
                bestDist = dist;
                best = cb;
                bestIdx = i;
            }
        }
        return (best, bestIdx);
    }

    private sealed record VectorCluster(BoundingBox Box, int ShapeCount, double Confidence, FigureCategory? SuggestedCategory);
    private sealed record MergedImageGroup(BoundingBox Box, int TileCount);
    private sealed record CompositeRegion(BoundingBox Box, double Confidence, FigureCategory Category);

    internal static List<ParsedTextSpan> MergeTextSpansOnSameLine(IReadOnlyList<ParsedTextSpan> rawSpans, double maxGapPts = 14.0)
    {
        if (rawSpans.Count <= 1)
            return [.. rawSpans];

        // Cluster by vertical proximity first so near-threshold baselines do not scramble column order.
        var byY = rawSpans
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .OrderBy(s => s.Y)
            .ThenBy(s => s.X)
            .ToList();

        var sorted = new List<ParsedTextSpan>(byY.Count);
        int idx = 0;
        while (idx < byY.Count)
        {
            int bandStart = idx;
            double bandAnchorY = byY[idx].Y;
            idx++;
            while (idx < byY.Count && Math.Abs(byY[idx].Y - bandAnchorY) <= 3.0)
                idx++;

            var band = byY.GetRange(bandStart, idx - bandStart);
            band.Sort((a, b) => a.X.CompareTo(b.X));
            sorted.AddRange(band);
        }

        var merged = new List<ParsedTextSpan>(sorted.Count);
        foreach (var span in sorted)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                bool sameLine = Math.Abs(prev.Y - span.Y) <= 4.0;
                double gap = span.X - (prev.X + prev.Width);
                double allowedGap = Math.Min(maxGapPts, Math.Max(5.0, Math.Max(prev.FontSize, span.FontSize) * 0.85));
                bool isCaptionSameFontContinuation =
                    IsCaptionText(prev.Text) &&
                    !IsCaptionText(span.Text) &&
                    Math.Abs(prev.FontSize - span.FontSize) <= 0.4 &&
                    !(prev.X < 90 && span.X >= 300) &&
                    span.X > prev.X + prev.Width * 0.65;
                double minGap = isCaptionSameFontContinuation
                    ? -Math.Max(12.0, prev.Width * 0.25)
                    : -12.0;
                bool startsNewCaptionAfterText =
                    IsCaptionText(span.Text) &&
                    !string.IsNullOrWhiteSpace(prev.Text) &&
                    prev.Text.Trim().Length > 10;
                if (sameLine && !startsNewCaptionAfterText && gap >= minGap && gap <= allowedGap)
                {
                    string sep = (prev.Text.EndsWith(' ') || span.Text.StartsWith(' ') || gap < 1.5) ? "" : " ";
                    string combinedText = prev.Text + sep + span.Text;
                    double newRight = Math.Max(prev.X + prev.Width, span.X + span.Width);
                    double newX = Math.Min(prev.X, span.X);
                    double newY = Math.Min(prev.Y, span.Y);
                    double newBottom = Math.Max(prev.Bottom, span.Bottom);
                    merged[^1] = new ParsedTextSpan(
                        newX,
                        newY,
                        newRight - newX,
                        Math.Max(prev.FontSize, span.FontSize),
                        combinedText,
                        newBottom - newY);
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }

    private static List<MergedImageGroup> FilterAndMergeImagePlacements(
        IReadOnlyList<ParsedImagePlacement> placements,
        double pageW,
        double pageH)
    {
        // Ignore running header/footer strips and microscopic artifacts (< 14 pts)
        var valid = placements
            .Where(p => p.Box.Width >= 14 && p.Box.Height >= 14)
            .Where(p => !IsHeaderOrFooterStrip(p.Box, pageW, pageH))
            .Where(p => !(p.Box.Width > pageW * 0.94 && p.Box.Height > pageH * 0.94))
            .ToList();

        if (valid.Count == 0)
            return [];

        // Grid/BFS merge for tiled raster strips (gap <= 8 pts)
        var groups = new List<MergedImageGroup>();
        var visited = new bool[valid.Count];

        for (int i = 0; i < valid.Count; i++)
        {
            if (visited[i]) continue;
            visited[i] = true;

            double minX = valid[i].Box.X;
            double minY = valid[i].Box.Y;
            double maxX = valid[i].Box.Right;
            double maxY = valid[i].Box.Bottom;
            int tiles = 1;

            var q = new Queue<int>();
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                var curBox = valid[cur].Box;
                for (int j = 0; j < valid.Count; j++)
                {
                    if (visited[j]) continue;
                    var b = valid[j].Box;
                    if (b.X <= curBox.Right + 8 && b.Right >= curBox.X - 8 &&
                        b.Y <= curBox.Bottom + 8 && b.Bottom >= curBox.Y - 8)
                    {
                        visited[j] = true;
                        minX = Math.Min(minX, b.X);
                        minY = Math.Min(minY, b.Y);
                        maxX = Math.Max(maxX, b.Right);
                        maxY = Math.Max(maxY, b.Bottom);
                        tiles++;
                        q.Enqueue(j);
                    }
                }
            }

            var box = new BoundingBox(Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(maxX - minX, 2), Math.Round(maxY - minY, 2));
            groups.Add(new MergedImageGroup(box, tiles));
        }

        return groups;
    }

    private static List<VectorCluster> ClusterVectorRegionsGrid(
        IReadOnlyList<ParsedVectorShape> shapes,
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        var meaningful = shapes.Where(s =>
        {
            if (s.Bounds.Width < 8 && s.Bounds.Height < 8)
                return false;
            if (s.Bounds.Width > pageW * 0.90 && s.Bounds.Height > pageH * 0.90)
                return false;
            if (IsHeaderOrFooterStrip(s.Bounds, pageW, pageH))
                return false;
            return true;
        }).ToList();

        if (meaningful.Count == 0)
            return [];

        const double cellSize = 20.0;
        int gridCols = Math.Max(1, (int)Math.Ceiling(pageW / cellSize));
        int gridRows = Math.Max(1, (int)Math.Ceiling(pageH / cellSize));

        var cellBuckets = new Dictionary<long, List<int>>();
        var shapeCells = new (int X0, int Y0, int X1, int Y1)[meaningful.Count];

        for (int i = 0; i < meaningful.Count; i++)
        {
            var b = meaningful[i].Bounds;
            int x0 = Math.Clamp((int)Math.Floor(b.X / cellSize), 0, gridCols - 1);
            int y0 = Math.Clamp((int)Math.Floor(b.Y / cellSize), 0, gridRows - 1);
            int x1 = Math.Clamp((int)Math.Floor(b.Right / cellSize), x0, gridCols - 1);
            int y1 = Math.Clamp((int)Math.Floor(b.Bottom / cellSize), y0, gridRows - 1);
            shapeCells[i] = (x0, y0, x1, y1);

            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    long key = ((long)cy << 32) | (uint)cx;
                    if (!cellBuckets.TryGetValue(key, out var list))
                    {
                        list = [];
                        cellBuckets[key] = list;
                    }
                    list.Add(i);
                }
            }
        }

        var assigned = new bool[meaningful.Count];
        var clusters = new List<VectorCluster>();
        var queue = new Queue<int>();

        for (int i = 0; i < meaningful.Count; i++)
        {
            if (assigned[i]) continue;
            assigned[i] = true;
            queue.Enqueue(i);

            double minX = meaningful[i].Bounds.X;
            double minY = meaningful[i].Bounds.Y;
            double maxX = meaningful[i].Bounds.Right;
            double maxY = meaningful[i].Bounds.Bottom;
            int count = 0;
            int rectCount = 0;
            int lineCount = 0;
            int thinRuleCount = 0;

            while (queue.Count > 0)
            {
                int curr = queue.Dequeue();
                var shape = meaningful[curr];
                minX = Math.Min(minX, shape.Bounds.X);
                minY = Math.Min(minY, shape.Bounds.Y);
                maxX = Math.Max(maxX, shape.Bounds.Right);
                maxY = Math.Max(maxY, shape.Bounds.Bottom);
                count++;
                if (shape.IsRectangle) rectCount++;
                if (Math.Min(shape.Bounds.Width, shape.Bounds.Height) <= 3.5 && Math.Max(shape.Bounds.Width, shape.Bounds.Height) >= 24)
                    thinRuleCount++;
                lineCount += Math.Max(1, shape.SegmentCount);

                var (x0, y0, x1, y1) = shapeCells[curr];
                int nx0 = Math.Max(0, x0 - 1);
                int ny0 = Math.Max(0, y0 - 1);
                int nx1 = Math.Min(gridCols - 1, x1 + 1);
                int ny1 = Math.Min(gridRows - 1, y1 + 1);

                for (int cy = ny0; cy <= ny1; cy++)
                {
                    for (int cx = nx0; cx <= nx1; cx++)
                    {
                        long key = ((long)cy << 32) | (uint)cx;
                        if (!cellBuckets.TryGetValue(key, out var bucket))
                            continue;

                        foreach (int neighbor in bucket)
                        {
                            if (assigned[neighbor]) continue;
                            var nb = meaningful[neighbor].Bounds;
                            var cb = shape.Bounds;
                            if (nb.X <= cb.Right + 18 && nb.Right >= cb.X - 18 &&
                                nb.Y <= cb.Bottom + 18 && nb.Bottom >= cb.Y - 18)
                            {
                                assigned[neighbor] = true;
                                queue.Enqueue(neighbor);
                            }
                        }
                    }
                }
            }

            double w = maxX - minX;
            double h = maxY - minY;
            // Filter out single thin horizontal section lines, callout boxes, and sparse noise
            if (w < 48 || h < 34 || count < 3)
                continue;
            if (w > pageW * 0.90 && h > pageH * 0.85)
                continue;

            var box = new BoundingBox(Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(w, 2), Math.Round(h, 2));

            // Structurally drop gridded tables, borderless booktabs rule-tables, and bordered prose callout boxes
            if (rectCount >= count * 0.65 && count >= 4)
                continue;
            if (thinRuleCount >= count * 0.70 && IsTabularTextGridInside(box, textSpans))
                continue;
            if (rectCount >= 1 && count <= 8 && IsProseCalloutBox(box, textSpans))
                continue;

            double confidence = Math.Clamp(0.68 + Math.Min(count, 16) * 0.018, 0.68, 0.96);
            FigureCategory? cat = lineCount >= count * 2
                ? FigureCategory.Chart
                : FigureCategory.Diagram;

            clusters.Add(new VectorCluster(box, count, confidence, cat));
        }

        return clusters;
    }

    private static (List<CompositeRegion> Composites, HashSet<int> ConsumedImages, HashSet<int> ConsumedVectors)
        BuildCompositeRegions(
            List<MergedImageGroup> images,
            List<VectorCluster> vectors,
            List<ParsedTextSpan> textSpans,
            double pageW,
            double pageH)
    {
        var composites = new List<CompositeRegion>();
        var consumedImages = new HashSet<int>();
        var consumedVectors = new HashSet<int>();

        int total = images.Count + vectors.Count;
        if (total == 0)
            return (composites, consumedImages, consumedVectors);

        var boxes = new BoundingBox[total];
        for (int i = 0; i < images.Count; i++) boxes[i] = images[i].Box;
        for (int i = 0; i < vectors.Count; i++) boxes[images.Count + i] = vectors[i].Box;

        var visited = new bool[total];
        for (int i = 0; i < total; i++)
        {
            if (visited[i]) continue;
            visited[i] = true;

            var compNodes = new List<int> { i };
            var q = new Queue<int>();
            q.Enqueue(i);

            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                var curBox = boxes[cur];
                for (int j = 0; j < total; j++)
                {
                    if (visited[j]) continue;
                    var jb = boxes[j];
                    if (jb.X <= curBox.Right + 24 && jb.Right >= curBox.X - 24 &&
                        jb.Y <= curBox.Bottom + 24 && jb.Bottom >= curBox.Y - 24)
                    {
                        visited[j] = true;
                        compNodes.Add(j);
                        q.Enqueue(j);
                    }
                }
            }

            if (compNodes.Count <= 1)
                continue;

            int imgMembers = compNodes.Count(idx => idx < images.Count);
            int vecMembers = compNodes.Count(idx => idx >= images.Count);
            if (imgMembers == 0 && vecMembers < 2)
                continue;

            double minX = compNodes.Min(idx => boxes[idx].X);
            double minY = compNodes.Min(idx => boxes[idx].Y);
            double maxX = compNodes.Max(idx => boxes[idx].Right);
            double maxY = compNodes.Max(idx => boxes[idx].Bottom);
            double w = maxX - minX;
            double h = maxY - minY;
            if (w > pageW * 0.94 && h > pageH * 0.90)
                continue;

            foreach (int idx in compNodes)
            {
                if (idx < images.Count) consumedImages.Add(idx);
                else consumedVectors.Add(idx - images.Count);
            }

            var box = new BoundingBox(Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(w, 2), Math.Round(h, 2));
            var cat = imgMembers > 0 ? FigureCategory.Figure : FigureCategory.Diagram;
            double conf = Math.Clamp(0.84 + Math.Min(compNodes.Count, 6) * 0.02, 0.84, 0.96);
            composites.Add(new CompositeRegion(box, conf, cat));
        }

        // Also promote any caption-anchored cluster/image pairs separated up to 48 pts horizontally under the same Fig. caption
        foreach (var cap in textSpans)
        {
            if (!IsCaptionText(cap.Text)) continue;
            var unconsumedNearby = new List<int>();
            for (int i = 0; i < images.Count; i++)
            {
                if (consumedImages.Contains(i)) continue;
                var b = images[i].Box;
                if (b.Bottom <= cap.Y + 14 && cap.Y - b.Bottom <= 55 && b.Right >= cap.X - 40 && b.X <= cap.X + Math.Max(cap.Width, 180) + 40)
                    unconsumedNearby.Add(i);
            }

            if (unconsumedNearby.Count >= 2)
            {
                double minX = unconsumedNearby.Min(i => images[i].Box.X);
                double minY = unconsumedNearby.Min(i => images[i].Box.Y);
                double maxX = unconsumedNearby.Max(i => images[i].Box.Right);
                double maxY = unconsumedNearby.Max(i => images[i].Box.Bottom);
                if (maxX - minX <= pageW * 0.92 && maxY - minY <= pageH * 0.80)
                {
                    foreach (int idx in unconsumedNearby) consumedImages.Add(idx);
                    composites.Add(new CompositeRegion(
                        new BoundingBox(Math.Round(minX, 2), Math.Round(minY, 2), Math.Round(maxX - minX, 2), Math.Round(maxY - minY, 2)),
                        0.92,
                        FigureCategory.Figure));
                }
            }
        }

        return (composites, consumedImages, consumedVectors);
    }

    private static bool IsHeaderOrFooterStrip(BoundingBox box, double pageW, double pageH)
    {
        bool inTopHeader = box.Bottom <= 48 && box.Height <= 46;
        bool inBottomFooter = box.Y >= pageH - 42 && box.Height <= 42;
        return inTopHeader || inBottomFooter;
    }

    private static void PaintBoxIntoTensor(
        float[] tensor,
        int planeOffset,
        int gridW,
        int gridH,
        BoundingBox box,
        double pageW,
        double pageH,
        float value)
    {
        int x0 = Math.Clamp((int)Math.Floor((box.X / pageW) * gridW), 0, gridW - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((box.Right / pageW) * gridW) - 1, x0, gridW - 1);
        int y0 = Math.Clamp((int)Math.Floor((box.Y / pageH) * gridH), 0, gridH - 1);
        int y1 = Math.Clamp((int)Math.Ceiling((box.Bottom / pageH) * gridH) - 1, y0, gridH - 1);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int idx = planeOffset + y * gridW + x;
                if (value > tensor[idx])
                    tensor[idx] = value;
            }
        }
    }

    private static BoundingBox ClampToPage(BoundingBox box, double pageW, double pageH) =>
        box.ClampToPage(pageW, pageH);

    private static bool IsCaptionText(string text) =>
        FigureNumberNormalizer.TryParseFigureCaption(text, out _);

    private static FigureCategory RefineCategoryFromCaption(FigureCategory fallback, string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return fallback;

        var lower = caption.TrimStart().ToLowerInvariant();
        if (lower.StartsWith("chart", StringComparison.Ordinal) || lower.StartsWith("plot", StringComparison.Ordinal) || lower.StartsWith("graph", StringComparison.Ordinal))
            return FigureCategory.Chart;
        if (lower.StartsWith("diagram", StringComparison.Ordinal) || lower.StartsWith("diag", StringComparison.Ordinal) || lower.StartsWith("scheme", StringComparison.Ordinal))
            return FigureCategory.Diagram;
        return fallback;
    }

    private static string? ExtractShortLabel(string? caption) =>
        FigureNumberNormalizer.TryParseFigureCaption(caption, out var parsed) ? parsed.ShortLabel : null;

    private static bool IsTabularTextGridInside(BoundingBox box, IReadOnlyList<ParsedTextSpan> textSpans)
    {
        var inside = textSpans
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) &&
                        s.X + s.Width * 0.5 >= box.X - 4 && s.X + s.Width * 0.5 <= box.Right + 4 &&
                        s.Y + s.EffectiveHeight * 0.5 >= box.Y - 4 && s.Y + s.EffectiveHeight * 0.5 <= box.Bottom + 4)
            .OrderBy(s => s.Y)
            .ThenBy(s => s.X)
            .ToList();

        if (inside.Count < 4) return false;

        int multiColRows = 0;
        int i = 0;
        while (i < inside.Count)
        {
            double rowY = inside[i].Y;
            int j = i + 1;
            while (j < inside.Count && Math.Abs(inside[j].Y - rowY) <= 4.0)
                j++;

            int cols = 1;
            double prevRight = inside[i].Right;
            for (int k = i + 1; k < j; k++)
            {
                if (inside[k].X - prevRight >= 12.0)
                    cols++;
                prevRight = Math.Max(prevRight, inside[k].Right);
            }
            if (cols >= 2) multiColRows++;
            i = j;
        }

        return multiColRows >= 2;
    }

    private static bool IsProseCalloutBox(BoundingBox box, IReadOnlyList<ParsedTextSpan> textSpans)
    {
        var inside = textSpans
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) &&
                        s.X + s.Width * 0.5 >= box.X && s.X + s.Width * 0.5 <= box.Right &&
                        s.Y + s.EffectiveHeight * 0.5 >= box.Y && s.Y + s.EffectiveHeight * 0.5 <= box.Bottom)
            .ToList();

        if (inside.Any(s => FigureNumberNormalizer.IsNonFigureArtifactCaption(s.Text)))
            return true;

        int proseLines = inside.Count(s =>
        {
            string t = s.Text.Trim();
            return t.Length >= 20 && t.Contains(' ', StringComparison.Ordinal);
        });
        return proseLines >= 3;
    }

    // =========================================================================
    // Lazy Byte-Level PDF 1.6 Object Scanner + ObjStm Unpacker + PNG Predictor
    // =========================================================================

    private sealed record PdfObjectEntry(int ObjectNumber, string DictionaryText, int StreamOffset, int StreamLength, int FileOffset = 0);

    private sealed record PdfDocumentIndex(
        string SuggestedTitle,
        Dictionary<int, PdfObjectEntry> Objects,
        double[] DefaultMediaBox,
        List<int> OrderedPageIds);

    private static Result<PdfDocumentIndex> BuildPdfDocumentIndex(byte[] pdfBytes, string pdfPath)
    {
        var objects = ScanIndirectObjectsLazy(pdfBytes);
        if (objects.Count == 0)
            return DocumentErrors.InvalidPdfFormat(pdfPath, "No indirect PDF objects found.");

        try
        {
            UnpackObjectStreams(pdfBytes, objects);
        }
        catch (PdfStreamLimitExceededException ex)
        {
            return DocumentErrors.InvalidPdfFormat(pdfPath, ex.Message);
        }

        string suggestedTitle = ExtractDocumentTitle(pdfBytes, objects, Path.GetFileNameWithoutExtension(pdfPath));

        int? catalogPagesRootId = ResolveCatalogPagesRootId(pdfBytes, objects);
        double[] defaultMediaBox = [0, 0, 612, 792];
        if (catalogPagesRootId.HasValue &&
            objects.TryGetValue(catalogPagesRootId.Value, out var catalogPagesObj) &&
            TryParseMediaBox(catalogPagesObj.DictionaryText, out var catalogRootBox))
        {
            defaultMediaBox = catalogRootBox;
        }
        else
        {
            foreach (var obj in objects.Values)
            {
                if (PagesTypeRegex().IsMatch(obj.DictionaryText) &&
                    TryParseMediaBox(obj.DictionaryText, out var rootBox))
                {
                    defaultMediaBox = rootBox;
                    break;
                }
            }
        }

        var orderedPageIds = ResolvePageOrder(objects, catalogPagesRootId);
        if (orderedPageIds.Count == 0)
            return DocumentErrors.InvalidPdfFormat(pdfPath, "No /Type /Page definitions found in PDF.");

        return new PdfDocumentIndex(suggestedTitle, objects, defaultMediaBox, orderedPageIds);
    }

    private static Dictionary<int, PdfObjectEntry> ScanIndirectObjectsLazy(byte[] pdfBytes)
    {
        var objects = new Dictionary<int, PdfObjectEntry>();
        int len = pdfBytes.Length;
        int i = 0;

        while (i < len - 6)
        {
            if (!TryMatchObjHeaderAt(pdfBytes, i, out int objNum, out int bodyStart))
            {
                i++;
                continue;
            }

            int objStartOffset = i;

            // Advance to 'stream' or 'endobj' inside dictionary/non-stream region
            int dictEnd = bodyStart;
            int streamOffset = -1;
            int streamLength = 0;
            bool foundStream = false;

            int parenDepth = 0;
            bool inComment = false;
            while (dictEnd < len)
            {
                byte b = pdfBytes[dictEnd];
                if (inComment)
                {
                    if (b is (byte)'\r' or (byte)'\n')
                        inComment = false;
                    dictEnd++;
                    continue;
                }

                if (parenDepth > 0)
                {
                    if (b == '\\')
                    {
                        dictEnd = Math.Min(len, dictEnd + 2);
                        continue;
                    }
                    if (b == '(') parenDepth++;
                    else if (b == ')') parenDepth--;
                    dictEnd++;
                    continue;
                }

                if (b == '%')
                {
                    inComment = true;
                    dictEnd++;
                    continue;
                }

                if (b == '(')
                {
                    parenDepth = 1;
                    dictEnd++;
                    continue;
                }

                if (MatchesKeywordAt(pdfBytes, dictEnd, "stream"u8) &&
                    (dictEnd == bodyStart || IsKeywordBoundaryBefore(pdfBytes[dictEnd - 1])) &&
                    dictEnd + 6 < len && (pdfBytes[dictEnd + 6] == '\r' || pdfBytes[dictEnd + 6] == '\n'))
                {
                    foundStream = true;
                    break;
                }

                if (MatchesKeywordAt(pdfBytes, dictEnd, "endobj"u8) &&
                    (dictEnd == bodyStart || IsKeywordBoundaryBefore(pdfBytes[dictEnd - 1])) &&
                    (dictEnd + 6 >= len || IsDelimiterOrSpace(pdfBytes[dictEnd + 6])))
                {
                    break;
                }

                dictEnd++;
            }

            string dictText = Encoding.Latin1.GetString(pdfBytes, bodyStart, Math.Max(0, dictEnd - bodyStart));

            if (foundStream)
            {
                int sStart = dictEnd + 6;
                if (sStart < len && pdfBytes[sStart] == '\r') sStart++;
                if (sStart < len && pdfBytes[sStart] == '\n') sStart++;

                if (TryParseIntEntry(dictText, "/Length", out int declaredLen) &&
                    declaredLen >= 0 &&
                    sStart + declaredLen <= len)
                {
                    streamOffset = sStart;
                    streamLength = declaredLen;
                    i = sStart + declaredLen;
                    int afterEndStream = FindBytes(pdfBytes, "endstream"u8, i, Math.Min(len, i + 32));
                    if (afterEndStream >= 0)
                        i = afterEndStream + 9;
                }
                else
                {
                    int endStreamPos = FindBytes(pdfBytes, "endstream"u8, sStart, len);
                    if (endStreamPos >= sStart)
                    {
                        int sEnd = endStreamPos;
                        if (sEnd > sStart && pdfBytes[sEnd - 1] == '\n') sEnd--;
                        if (sEnd > sStart && pdfBytes[sEnd - 1] == '\r') sEnd--;
                        streamOffset = sStart;
                        streamLength = Math.Max(0, sEnd - sStart);
                        i = endStreamPos + 9;
                    }
                    else
                    {
                        i = sStart;
                    }
                }
            }
            else
            {
                i = Math.Min(len, dictEnd + 6);
            }

            objects[objNum] = new PdfObjectEntry(objNum, dictText, streamOffset, streamLength, objStartOffset);
        }

        return objects;
    }

    private static void UnpackObjectStreams(byte[] pdfBytes, Dictionary<int, PdfObjectEntry> objects)
    {
        var objStreams = objects.Values
            .Where(o => o.StreamOffset >= 0 && o.DictionaryText.Contains("/ObjStm", StringComparison.Ordinal))
            .OrderBy(o => o.FileOffset)
            .ToList();

        foreach (var stm in objStreams)
        {
            if (!TryParseIntEntry(stm.DictionaryText, "/N", out int count) || count <= 0)
                continue;
            if (!TryParseIntEntry(stm.DictionaryText, "/First", out int firstOffset) || firstOffset <= 0)
                continue;

            var rawSlice = new ReadOnlySpan<byte>(pdfBytes, stm.StreamOffset, stm.StreamLength);
            byte[] decoded = DecodeStreamPayload(stm.DictionaryText, rawSlice);
            if (decoded.Length <= firstOffset)
                continue;

            string headerText = Encoding.Latin1.GetString(decoded, 0, firstOffset);
            var headerTokens = headerText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (headerTokens.Length < count * 2)
                count = headerTokens.Length / 2;

            var entries = new List<(int ObjNum, int Start)>(count);
            for (int k = 0; k < count; k++)
            {
                if (int.TryParse(headerTokens[k * 2], out int objId) &&
                    int.TryParse(headerTokens[k * 2 + 1], out int relOff))
                {
                    entries.Add((objId, firstOffset + relOff));
                }
            }

            for (int k = 0; k < entries.Count; k++)
            {
                int start = entries[k].Start;
                if (start < 0 || start >= decoded.Length)
                    continue;

                int rawEnd = (k + 1 < entries.Count) ? entries[k + 1].Start : decoded.Length;
                int end = Math.Min(rawEnd, decoded.Length);
                if (end <= start)
                    continue;

                string objText = Encoding.Latin1.GetString(decoded, start, end - start);
                if (!objects.TryGetValue(entries[k].ObjNum, out var existing) || stm.FileOffset > existing.FileOffset)
                    objects[entries[k].ObjNum] = new PdfObjectEntry(entries[k].ObjNum, objText, -1, 0, stm.FileOffset);
            }
        }
    }

    private static Result<ParsedPageScene> ParseSinglePageScene(
        byte[] pdfBytes,
        PdfDocumentIndex docIndex,
        int pageObjId,
        int pageNum,
        string pdfPath)
    {
        try
        {
            var objects = docIndex.Objects;
            var pageObj = objects[pageObjId];

            double[] visibleBox = ResolvePageVisibleBox(pageObjId, objects, docIndex.DefaultMediaBox);
            double mbLeft = visibleBox[0];
            double mbBottom = visibleBox[1];
            double unrotW = Math.Max(1, visibleBox[2] - visibleBox[0]);
            double unrotH = Math.Max(1, visibleBox[3] - visibleBox[1]);
            int rotation = ResolvePageRotation(pageObjId, objects);
            int normRot = ((rotation % 360) + 360) % 360;
            bool swapAxes = normRot == 90 || normRot == 270;
            double pageW = swapAxes ? unrotH : unrotW;
            double pageH = swapAxes ? unrotW : unrotH;

            var pageInfo = PdfPageInfo.Create(pageNum, pageW, pageH, rotation);
            if (!pageInfo.IsSuccess)
                return pageInfo.Error!;

            var resources = ResolvePageResources(pdfBytes, pageObjId, objects);
            var contentBytes = CollectPageContentStream(pdfBytes, pageObj.DictionaryText, objects);
            var (placements, vectorShapes, textSpans) = ParsePageContentStream(
                pdfBytes,
                objects,
                contentBytes,
                mbLeft,
                mbBottom,
                unrotW,
                unrotH,
                normRot,
                resources);

            return new ParsedPageScene(pageInfo.Value, placements, vectorShapes, textSpans);
        }
        catch (PdfStreamLimitExceededException ex)
        {
            return DocumentErrors.InvalidPdfFormat(pdfPath, ex.Message);
        }
    }

    private static byte[] DecodeStreamPayload(string dictText, ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0)
            return [];

        byte[] data = raw.ToArray();
        if (dictText.Contains("/FlateDecode", StringComparison.Ordinal))
            data = DecompressFlate(data);

        if (TryParseIntEntry(dictText, "/Predictor", out int predictor) && predictor > 1)
        {
            int colors = TryParseIntEntry(dictText, "/Colors", out int c) && c > 0 ? c : 1;
            int bpc = TryParseIntEntry(dictText, "/BitsPerComponent", out int b) && b > 0 ? b : 8;
            int columns = TryParseIntEntry(dictText, "/Columns", out int cols) && cols > 0
                ? cols
                : (TryParseIntEntry(dictText, "/Width", out int w) && w > 0 ? w : 1);

            data = ApplyPredictor(data, predictor, colors, bpc, columns);
        }

        return data;
    }

    private static byte[] DecompressFlate(byte[] raw)
    {
        return TryDecompress(raw, zlib: true) ?? TryDecompress(raw, zlib: false) ?? [];

        static byte[]? TryDecompress(byte[] inputBytes, bool zlib)
        {
            try
            {
                using var input = new MemoryStream(inputBytes);
                using Stream decoder = zlib
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int total = 0;
                int read;
                while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (total + read > MaxDecompressedStreamBytes)
                        throw new PdfStreamLimitExceededException($"Decompressed PDF stream exceeds {MaxDecompressedStreamBytes} bytes limit.");
                    output.Write(buffer, 0, read);
                    total += read;
                }
                return output.ToArray();
            }
            catch (PdfStreamLimitExceededException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }

    private static byte[] ApplyPredictor(byte[] data, int predictor, int colors, int bpc, int columns)
    {
        int bytesPerPixel = Math.Max(1, (colors * bpc + 7) / 8);
        int rowBytes = Math.Max(1, (columns * colors * bpc + 7) / 8);

        if (predictor == 2)
        {
            // TIFF Horizontal Differencing Predictor
            var result = (byte[])data.Clone();
            int rows = result.Length / rowBytes;
            for (int r = 0; r < rows; r++)
            {
                int rowStart = r * rowBytes;
                for (int x = bytesPerPixel; x < rowBytes; x++)
                    result[rowStart + x] = (byte)(result[rowStart + x] + result[rowStart + x - bytesPerPixel]);
            }
            return result;
        }

        if (predictor >= 10)
        {
            // PNG Row Predictors (each scanline prefixed with 1 filter byte)
            int stride = rowBytes + 1;
            int rows = data.Length / stride;
            if (rows <= 0)
                return data;

            var output = new byte[rows * rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int srcRow = r * stride;
                int dstRow = r * rowBytes;
                int prevRow = (r - 1) * rowBytes;
                byte filter = data[srcRow];

                for (int x = 0; x < rowBytes; x++)
                {
                    byte rawByte = data[srcRow + 1 + x];
                    byte left = x >= bytesPerPixel ? output[dstRow + x - bytesPerPixel] : (byte)0;
                    byte up = r > 0 ? output[prevRow + x] : (byte)0;
                    byte upLeft = (r > 0 && x >= bytesPerPixel) ? output[prevRow + x - bytesPerPixel] : (byte)0;

                    output[dstRow + x] = filter switch
                    {
                        0 => rawByte,
                        1 => (byte)(rawByte + left),
                        2 => (byte)(rawByte + up),
                        3 => (byte)(rawByte + ((left + up) >> 1)),
                        4 => (byte)(rawByte + PaethPredictor(left, up, upLeft)),
                        _ => rawByte
                    };
                }
            }
            return output;
        }

        return data;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static bool TryMatchObjHeaderAt(byte[] data, int pos, out int objNum, out int bodyStart)
    {
        objNum = 0;
        bodyStart = 0;

        if (pos > 0 && data[pos - 1] is not (byte)'\n' and not (byte)'\r' and not (byte)' ' and not (byte)'\t')
            return false;

        if (data[pos] < '0' || data[pos] > '9')
            return false;

        int p = pos;
        long parsedObj = 0;
        while (p < data.Length && data[p] >= '0' && data[p] <= '9')
        {
            parsedObj = parsedObj * 10 + (data[p] - '0');
            if (parsedObj > int.MaxValue) return false;
            p++;
        }

        if (p >= data.Length || !IsSpaceByte(data[p])) return false;
        while (p < data.Length && IsSpaceByte(data[p])) p++;

        if (p >= data.Length || data[p] < '0' || data[p] > '9') return false;
        while (p < data.Length && data[p] >= '0' && data[p] <= '9') p++;

        if (p >= data.Length || !IsSpaceByte(data[p])) return false;
        while (p < data.Length && IsSpaceByte(data[p])) p++;

        if (p + 3 > data.Length || data[p] != 'o' || data[p + 1] != 'b' || data[p + 2] != 'j')
            return false;

        int after = p + 3;
        if (after < data.Length && !IsSpaceByte(data[after]) && data[after] != '<' && data[after] != '[' && data[after] != '/')
            return false;

        objNum = (int)parsedObj;
        bodyStart = after;
        return true;
    }

    private static bool IsSpaceByte(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsKeywordBoundaryBefore(byte b) =>
        IsSpaceByte(b) || b is (byte)'>' or (byte)'<' or (byte)'[' or (byte)']' or (byte)')' or (byte)'(';

    private static bool IsDelimiterOrSpace(byte b) =>
        IsKeywordBoundaryBefore(b) || b == (byte)'/';

    private static bool MatchesKeywordAt(byte[] data, int pos, ReadOnlySpan<byte> keyword)
    {
        if (pos + keyword.Length > data.Length) return false;
        return data.AsSpan(pos, keyword.Length).SequenceEqual(keyword);
    }

    private static int FindBytes(byte[] data, ReadOnlySpan<byte> needle, int start, int limit)
    {
        int end = Math.Min(limit, data.Length - needle.Length + 1);
        for (int i = start; i < end; i++)
        {
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static int? ResolveCatalogPagesRootId(byte[] pdfBytes, Dictionary<int, PdfObjectEntry> objects)
    {
        int? catalogId = null;

        int tailLen = Math.Min(pdfBytes.Length, 65536);
        string tailText = Encoding.Latin1.GetString(pdfBytes, pdfBytes.Length - tailLen, tailLen);
        var tailRoots = RootRefRegex().Matches(tailText);
        for (int i = tailRoots.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(tailRoots[i].Groups[1].Value, out int parsedRoot) && objects.ContainsKey(parsedRoot))
            {
                catalogId = parsedRoot;
                break;
            }
        }

        if (!catalogId.HasValue)
        {
            foreach (var xrefObj in objects.Values
                .Where(o => XRefTypeRegex().IsMatch(o.DictionaryText))
                .OrderByDescending(o => o.FileOffset))
            {
                var m = RootRefRegex().Match(xrefObj.DictionaryText);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int parsedRoot) && objects.ContainsKey(parsedRoot))
                {
                    catalogId = parsedRoot;
                    break;
                }
            }
        }

        if (!catalogId.HasValue)
        {
            var catObj = objects.Values
                .Where(o => CatalogTypeRegex().IsMatch(o.DictionaryText))
                .OrderByDescending(o => o.FileOffset)
                .FirstOrDefault();
            if (catObj is not null)
                catalogId = catObj.ObjectNumber;
        }

        if (catalogId.HasValue && objects.TryGetValue(catalogId.Value, out var catalog))
        {
            var pagesMatch = PagesRefRegex().Match(catalog.DictionaryText);
            if (pagesMatch.Success && int.TryParse(pagesMatch.Groups[1].Value, out int pagesRootId) && objects.ContainsKey(pagesRootId))
                return pagesRootId;
        }

        return null;
    }

    private static List<int> ResolvePageOrder(Dictionary<int, PdfObjectEntry> objects, int? catalogPagesRootId = null)
    {
        var pageSet = objects.Values
            .Where(o => PageTypeRegex().IsMatch(o.DictionaryText))
            .Select(o => o.ObjectNumber)
            .ToHashSet();

        if (pageSet.Count == 0)
            return [];

        var ordered = new List<int>();
        var visitedNodes = new HashSet<int>();

        void Traverse(int nodeId)
        {
            if (!visitedNodes.Add(nodeId) || !objects.TryGetValue(nodeId, out var obj))
                return;

            if (pageSet.Contains(nodeId))
            {
                ordered.Add(nodeId);
                return;
            }

            var kidsMatch = KidsArrayRegex().Match(obj.DictionaryText);
            if (!kidsMatch.Success)
                return;

            foreach (Match refMatch in IndirectRefRegex().Matches(kidsMatch.Groups[1].Value))
            {
                if (int.TryParse(refMatch.Groups[1].Value, out int childId))
                    Traverse(childId);
            }
        }

        if (catalogPagesRootId.HasValue)
        {
            Traverse(catalogPagesRootId.Value);
        }
        else
        {
            var pagesRoots = objects.Values
                .Where(o => PagesTypeRegex().IsMatch(o.DictionaryText))
                .OrderBy(o => o.DictionaryText.Contains("/Parent", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(o => o.ObjectNumber)
                .ToList();

            if (pagesRoots.Count > 0)
                Traverse(pagesRoots[0].ObjectNumber);
        }

        if (ordered.Count == 0)
        {
            foreach (int id in pageSet.OrderBy(x => x))
            {
                if (!ordered.Contains(id))
                    ordered.Add(id);
            }
        }

        return ordered;
    }

    private static int ResolvePageRotation(int pageObjId, Dictionary<int, PdfObjectEntry> objects) =>
        WalkPageParentChain(pageObjId, objects, static dict =>
            TryParseIntEntry(dict, "/Rotate", out int rot) ? rot : (int?)null) ?? 0;

    private static string? ResolveSubDictionary(string parentDict, string key, Dictionary<int, PdfObjectEntry> objects)
    {
        var exactKeyRegex = new Regex(Regex.Escape(key) + @"(?=[/\s<\[\(\r\n])", RegexOptions.CultureInvariant);
        var keyMatch = exactKeyRegex.Match(parentDict);
        if (!keyMatch.Success)
            return null;

        int afterKey = keyMatch.Index + keyMatch.Length;
        while (afterKey < parentDict.Length && char.IsWhiteSpace(parentDict[afterKey]))
            afterKey++;

        var refMatch = Regex.Match(parentDict[afterKey..], @"^(\d+)\s+\d+\s+R\b");
        if (refMatch.Success && int.TryParse(refMatch.Groups[1].Value, out int targetId) && objects.TryGetValue(targetId, out var obj))
            return obj.DictionaryText;

        if (afterKey + 1 >= parentDict.Length || parentDict[afterKey] != '<' || parentDict[afterKey + 1] != '<')
            return null;

        int openIdx = afterKey;
        int depth = 0;
        for (int i = openIdx; i < parentDict.Length - 1; i++)
        {
            if (parentDict[i] == '<' && parentDict[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (parentDict[i] == '>' && parentDict[i + 1] == '>')
            {
                depth--;
                i++;
                if (depth == 0)
                    return parentDict[openIdx..(i + 1)];
            }
        }

        return null;
    }

    private static byte[] CollectPageContentStream(byte[] pdfBytes, string pageDict, Dictionary<int, PdfObjectEntry> objects)
    {
        using var ms = new MemoryStream();

        var arrayMatch = Regex.Match(pageDict, @"/Contents\s*\[(.*?)\]", RegexOptions.Singleline);
        if (arrayMatch.Success)
        {
            foreach (Match r in IndirectRefRegex().Matches(arrayMatch.Groups[1].Value))
            {
                if (int.TryParse(r.Groups[1].Value, out int objId) &&
                    objects.TryGetValue(objId, out var streamObj) &&
                    streamObj.StreamOffset >= 0 &&
                    streamObj.StreamLength > 0)
                {
                    var decoded = DecodeStreamPayload(
                        streamObj.DictionaryText,
                        new ReadOnlySpan<byte>(pdfBytes, streamObj.StreamOffset, streamObj.StreamLength));
                    ms.Write(decoded, 0, decoded.Length);
                    ms.WriteByte((byte)'\n');
                }
            }
            return ms.ToArray();
        }

        var singleMatch = Regex.Match(pageDict, @"/Contents\s+(\d+)\s+\d+\s+R\b");
        if (singleMatch.Success &&
            int.TryParse(singleMatch.Groups[1].Value, out int singleId) &&
            objects.TryGetValue(singleId, out var singleObj))
        {
            if (singleObj.StreamOffset >= 0 && singleObj.StreamLength > 0)
            {
                return DecodeStreamPayload(
                    singleObj.DictionaryText,
                    new ReadOnlySpan<byte>(pdfBytes, singleObj.StreamOffset, singleObj.StreamLength));
            }

            if (singleObj.DictionaryText.TrimStart().StartsWith('['))
            {
                foreach (Match r in IndirectRefRegex().Matches(singleObj.DictionaryText))
                {
                    if (int.TryParse(r.Groups[1].Value, out int objId) &&
                        objects.TryGetValue(objId, out var streamObj) &&
                        streamObj.StreamOffset >= 0 &&
                        streamObj.StreamLength > 0)
                    {
                        var decoded = DecodeStreamPayload(
                            streamObj.DictionaryText,
                            new ReadOnlySpan<byte>(pdfBytes, streamObj.StreamOffset, streamObj.StreamLength));
                        ms.Write(decoded, 0, decoded.Length);
                        ms.WriteByte((byte)'\n');
                    }
                }
                return ms.ToArray();
            }
        }

        return [];
    }

    private static (List<ParsedImagePlacement> Placements, List<ParsedVectorShape> Vectors, List<ParsedTextSpan> TextSpans)
        ParsePageContentStream(
            byte[] pdfBytes,
            Dictionary<int, PdfObjectEntry> objects,
            byte[] contentBytes,
            double mbLeft,
            double mbBottom,
            double unrotW,
            double unrotH,
            int rotationDegrees,
            ParsedPageResources pageResources)
    {
        var placements = new List<ParsedImagePlacement>();
        var vectors = new List<ParsedVectorShape>();
        var textSpans = new List<ParsedTextSpan>();

        ParseContentStreamRecursive(
            pdfBytes,
            objects,
            contentBytes,
            mbLeft,
            mbBottom,
            unrotW,
            unrotH,
            rotationDegrees,
            pageResources,
            initialCtm: [1, 0, 0, 1, 0, 0],
            depth: 0,
            placements,
            vectors,
            textSpans);

        return (placements, vectors, textSpans);
    }

    private static void ParseContentStreamRecursive(
        byte[] pdfBytes,
        Dictionary<int, PdfObjectEntry> objects,
        byte[] contentBytes,
        double mbLeft,
        double mbBottom,
        double unrotW,
        double unrotH,
        int rotationDegrees,
        ParsedPageResources resources,
        double[] initialCtm,
        int depth,
        List<ParsedImagePlacement> placements,
        List<ParsedVectorShape> vectors,
        List<ParsedTextSpan> textSpans)
    {
        if (contentBytes.Length == 0 || depth > 4)
            return;

        string streamText = Encoding.Latin1.GetString(contentBytes);
        var tokens = TokenizePdfContentStream(streamText);

        var ctmStack = new Stack<double[]>();
        double[] ctm = [.. initialCtm];

        var currentSegments = new List<(double X1, double Y1, double X2, double Y2)>();
        var currentPathPoints = new List<(double X, double Y)>();
        var pendingRects = new List<BoundingBox>();
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;

        bool inText = false;
        double fontSize = 11.0;
        double textLeading = 13.0;
        string? activeFontName = null;
        double[] textLineMatrix = [1, 0, 0, 1, 0, 0];
        double[] textMatrix = [1, 0, 0, 1, 0, 0];

        var operands = new List<string>(16);

        (double TopX, double TopY) MapPdfPointToTopLeft(double pdfX, double pdfY)
        {
            double ux = pdfX - mbLeft;
            double uy = pdfY - mbBottom;
            return rotationDegrees switch
            {
                90 => (uy, ux),
                180 => (unrotW - ux, uy),
                270 => (unrotH - uy, unrotW - ux),
                _ => (ux, unrotH - uy)
            };
        }

        BoundingBox ToTopLeftBox(double minPdfX, double minPdfY, double maxPdfX, double maxPdfY)
        {
            var p1 = MapPdfPointToTopLeft(minPdfX, minPdfY);
            var p2 = MapPdfPointToTopLeft(maxPdfX, minPdfY);
            var p3 = MapPdfPointToTopLeft(maxPdfX, maxPdfY);
            var p4 = MapPdfPointToTopLeft(minPdfX, maxPdfY);
            double left = Math.Max(0, Math.Min(Math.Min(p1.TopX, p2.TopX), Math.Min(p3.TopX, p4.TopX)));
            double top = Math.Max(0, Math.Min(Math.Min(p1.TopY, p2.TopY), Math.Min(p3.TopY, p4.TopY)));
            double right = Math.Max(left + 0.5, Math.Max(Math.Max(p1.TopX, p2.TopX), Math.Max(p3.TopX, p4.TopX)));
            double bottom = Math.Max(top + 0.5, Math.Max(Math.Max(p1.TopY, p2.TopY), Math.Max(p3.TopY, p4.TopY)));
            return new BoundingBox(Math.Round(left, 2), Math.Round(top, 2), Math.Round(right - left, 2), Math.Round(bottom - top, 2));
        }

        (double X, double Y) TransformPoint(double px, double py)
        {
            double tx = ctm[0] * px + ctm[2] * py + ctm[4];
            double ty = ctm[1] * px + ctm[3] * py + ctm[5];
            return (tx, ty);
        }

        (double TopX, double TopY) TransformToTopLeft(double px, double py)
        {
            var (tx, ty) = TransformPoint(px, py);
            return MapPdfPointToTopLeft(tx, ty);
        }

        void FlushPath()
        {
            if (pendingRects.Count > 0)
            {
                foreach (var rect in pendingRects)
                    vectors.Add(new ParsedVectorShape(IsRectangle: true, rect, SegmentCount: 0));
                pendingRects.Clear();
            }

            if (currentSegments.Count > 0 || currentPathPoints.Count >= 2)
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var s in currentSegments)
                {
                    minX = Math.Min(minX, Math.Min(s.X1, s.X2));
                    minY = Math.Min(minY, Math.Min(s.Y1, s.Y2));
                    maxX = Math.Max(maxX, Math.Max(s.X1, s.X2));
                    maxY = Math.Max(maxY, Math.Max(s.Y1, s.Y2));
                }

                if (minX <= maxX && minY <= maxY)
                {
                    var bounds = new BoundingBox(
                        Math.Round(Math.Max(0, minX), 2),
                        Math.Round(Math.Max(0, minY), 2),
                        Math.Round(Math.Max(0.5, maxX - minX), 2),
                        Math.Round(Math.Max(0.5, maxY - minY), 2));

                    vectors.Add(new ParsedVectorShape(IsRectangle: false, bounds, currentSegments.Count));
                }

                currentSegments.Clear();
            }

            currentPathPoints.Clear();
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            if (IsPdfOperator(tok))
            {
                switch (tok)
                {
                    case "q":
                        ctmStack.Push([.. ctm]);
                        break;
                    case "Q":
                        if (ctmStack.Count > 0)
                            ctm = ctmStack.Pop();
                        break;
                    case "cm" when operands.Count >= 6:
                        if (TryParseDoubles(operands, 6, out var m))
                            ctm = MultiplyMatrix(m, ctm);
                        break;
                    case "re" when operands.Count >= 4:
                        if (TryParseDoubles(operands, 4, out var rVals))
                        {
                            var p1 = TransformPoint(rVals[0], rVals[1]);
                            var p2 = TransformPoint(rVals[0] + rVals[2], rVals[1]);
                            var p3 = TransformPoint(rVals[0] + rVals[2], rVals[1] + rVals[3]);
                            var p4 = TransformPoint(rVals[0], rVals[1] + rVals[3]);
                            double minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
                            double maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
                            double minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
                            double maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
                            pendingRects.Add(ToTopLeftBox(minX, minY, maxX, maxY));
                        }
                        break;
                    case "m" when operands.Count >= 2:
                        if (TryParseDoubles(operands, 2, out var mp))
                        {
                            var tl = TransformToTopLeft(mp[0], mp[1]);
                            curX = startX = tl.TopX;
                            curY = startY = tl.TopY;
                            currentPathPoints.Add((curX, curY));
                        }
                        break;
                    case "l" when operands.Count >= 2:
                        if (TryParseDoubles(operands, 2, out var lp))
                        {
                            var tl = TransformToTopLeft(lp[0], lp[1]);
                            currentSegments.Add((curX, curY, tl.TopX, tl.TopY));
                            curX = tl.TopX;
                            curY = tl.TopY;
                            currentPathPoints.Add((curX, curY));
                        }
                        break;
                    case "c" when operands.Count >= 6:
                        if (TryParseDoubles(operands, 6, out var cp))
                        {
                            var c1 = TransformToTopLeft(cp[0], cp[1]);
                            var c2 = TransformToTopLeft(cp[2], cp[3]);
                            var endPt = TransformToTopLeft(cp[4], cp[5]);
                            currentSegments.Add((curX, curY, (curX + c1.TopX) * 0.5, (curY + c1.TopY) * 0.5));
                            currentSegments.Add(((curX + c1.TopX) * 0.5, (curY + c1.TopY) * 0.5, (c2.TopX + endPt.TopX) * 0.5, (c2.TopY + endPt.TopY) * 0.5));
                            currentSegments.Add(((c2.TopX + endPt.TopX) * 0.5, (c2.TopY + endPt.TopY) * 0.5, endPt.TopX, endPt.TopY));
                            curX = endPt.TopX;
                            curY = endPt.TopY;
                        }
                        break;
                    case "v" or "y" when operands.Count >= 4:
                        if (TryParseDoubles(operands, 4, out var vp))
                        {
                            var mid = TransformToTopLeft(vp[0], vp[1]);
                            var endPt = TransformToTopLeft(vp[2], vp[3]);
                            currentSegments.Add((curX, curY, mid.TopX, mid.TopY));
                            currentSegments.Add((mid.TopX, mid.TopY, endPt.TopX, endPt.TopY));
                            curX = endPt.TopX;
                            curY = endPt.TopY;
                        }
                        break;
                    case "h":
                        if (currentPathPoints.Count > 0)
                        {
                            currentSegments.Add((curX, curY, startX, startY));
                            curX = startX;
                            curY = startY;
                        }
                        break;
                    case "S" or "s" or "f" or "F" or "f*" or "B" or "b" or "B*" or "b*":
                        FlushPath();
                        break;
                    case "n":
                        pendingRects.Clear();
                        currentSegments.Clear();
                        currentPathPoints.Clear();
                        break;
                    case "Do" when operands.Count >= 1:
                        string resName = operands[^1];
                        if (resources.Forms.TryGetValue(resName, out var formObj))
                        {
                            if (depth < 4 && formObj.StreamOffset >= 0 && formObj.StreamLength > 0)
                            {
                                var formBytes = DecodeStreamPayload(
                                    formObj.DictionaryText,
                                    new ReadOnlySpan<byte>(pdfBytes, formObj.StreamOffset, formObj.StreamLength));
                                var formCtm = MultiplyMatrix(formObj.Matrix, ctm);
                                var childRes = MergeFormResources(pdfBytes, formObj.DictionaryText, resources, objects);
                                ParseContentStreamRecursive(
                                    pdfBytes,
                                    objects,
                                    formBytes,
                                    mbLeft,
                                    mbBottom,
                                    unrotW,
                                    unrotH,
                                    rotationDegrees,
                                    childRes,
                                    formCtm,
                                    depth + 1,
                                    placements,
                                    vectors,
                                    textSpans);
                            }
                        }
                        else if (resources.Images.TryGetValue(resName, out var imgObj))
                        {
                            var corners = new[]
                            {
                                TransformPoint(0, 0),
                                TransformPoint(1, 0),
                                TransformPoint(0, 1),
                                TransformPoint(1, 1)
                            };
                            var imBox = ToTopLeftBox(
                                corners.Min(c => c.X),
                                corners.Min(c => c.Y),
                                corners.Max(c => c.X),
                                corners.Max(c => c.Y));
                            placements.Add(new ParsedImagePlacement(resName, imBox, imgObj));
                        }
                        break;
                    case "BT":
                        inText = true;
                        textLineMatrix = [1, 0, 0, 1, 0, 0];
                        textMatrix = [1, 0, 0, 1, 0, 0];
                        break;
                    case "ET":
                        inText = false;
                        break;
                    case "TL" when operands.Count >= 1:
                        if (TryParseDouble(operands[^1], out double tlVal))
                            textLeading = tlVal;
                        break;
                    case "Tf" when operands.Count >= 2:
                        activeFontName = operands[^2];
                        if (TryParseDouble(operands[^1], out double fs) && fs > 0)
                            fontSize = fs;
                        break;
                    case "Td" when inText && operands.Count >= 2:
                        if (TryParseDoubles(operands, 2, out var td))
                        {
                            textLineMatrix = MultiplyMatrix([1, 0, 0, 1, td[0], td[1]], textLineMatrix);
                            textMatrix = [.. textLineMatrix];
                        }
                        break;
                    case "TD" when inText && operands.Count >= 2:
                        if (TryParseDoubles(operands, 2, out var tdd))
                        {
                            textLeading = -tdd[1];
                            textLineMatrix = MultiplyMatrix([1, 0, 0, 1, tdd[0], tdd[1]], textLineMatrix);
                            textMatrix = [.. textLineMatrix];
                        }
                        break;
                    case "T*" when inText:
                        textLineMatrix = MultiplyMatrix([1, 0, 0, 1, 0, -textLeading], textLineMatrix);
                        textMatrix = [.. textLineMatrix];
                        break;
                    case "Tm" when inText && operands.Count >= 6:
                        if (TryParseDoubles(operands, 6, out var tm))
                        {
                            textLineMatrix = tm;
                            textMatrix = [.. tm];
                        }
                        break;
                    case "Tj" or "'" or "\"" or "TJ" when inText && operands.Count >= 1:
                        if (tok is "'" or "\"")
                        {
                            textLineMatrix = MultiplyMatrix([1, 0, 0, 1, 0, -textLeading], textLineMatrix);
                            textMatrix = [.. textLineMatrix];
                        }
                        resources.ToUnicodeFonts.TryGetValue(activeFontName ?? string.Empty, out var cmap);
                        string txt = tok == "TJ"
                            ? DecodeTjArrayOperand(operands[^1], cmap)
                            : DecodeTextOperand(operands[^1], cmap);
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            double tmHorizScale = Math.Max(0.01, Math.Sqrt(textMatrix[0] * textMatrix[0] + textMatrix[1] * textMatrix[1]));
                            double tmVertScale = Math.Max(0.01, Math.Sqrt(textMatrix[2] * textMatrix[2] + textMatrix[3] * textMatrix[3]));
                            double ctmScaleX = Math.Max(0.01, Math.Sqrt(ctm[0] * ctm[0] + ctm[1] * ctm[1]));
                            double ctmScaleY = Math.Max(0.01, Math.Sqrt(ctm[2] * ctm[2] + ctm[3] * ctm[3]));
                            double effFont = Math.Max(4, fontSize * tmVertScale * ctmScaleY);
                            double estWidth = Math.Max(effFont * 0.45, txt.Length * fontSize * tmHorizScale * ctmScaleX * 0.50);
                            var baseline = TransformToTopLeft(textMatrix[4], textMatrix[5]);
                            double spanTop = Math.Max(0, baseline.TopY - effFont * 0.88);
                            double spanH = Math.Max(8.0, effFont * 1.15);
                            textSpans.Add(new ParsedTextSpan(baseline.TopX, spanTop, estWidth, effFont, txt, spanH));
                            textMatrix[4] += txt.Length * fontSize * tmHorizScale * 0.50;
                        }
                        break;
                }

                operands.Clear();
            }
            else
            {
                operands.Add(tok);
            }
        }
    }

    private static List<string> TokenizePdfContentStream(string content)
    {
        var tokens = new List<string>(Math.Max(64, content.Length / 8));
        var sb = new StringBuilder(128);
        int i = 0;

        while (i < content.Length)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '%')
            {
                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                    i++;
                continue;
            }

            if (c == '(')
            {
                sb.Clear();
                ReadBalancedParenLiteral(content, ref i, sb);
                tokens.Add(sb.ToString());
                continue;
            }

            if (c == '[')
            {
                sb.Clear();
                sb.Append('[');
                int depth = 1;
                i++;
                while (i < content.Length && depth > 0)
                {
                    char ch = content[i];
                    if (ch == '(')
                    {
                        ReadBalancedParenLiteral(content, ref i, sb);
                    }
                    else
                    {
                        i++;
                        if (ch == '[') depth++;
                        else if (ch == ']') depth--;
                        sb.Append(ch);
                    }
                }
                tokens.Add(sb.ToString());
                continue;
            }

            if (c == '<')
            {
                if (i + 1 < content.Length && content[i + 1] == '<')
                {
                    int start = i;
                    int depth = 0;
                    while (i < content.Length)
                    {
                        if (i + 1 < content.Length && content[i] == '<' && content[i + 1] == '<')
                        {
                            depth++;
                            i += 2;
                        }
                        else if (i + 1 < content.Length && content[i] == '>' && content[i + 1] == '>')
                        {
                            depth--;
                            i += 2;
                            if (depth <= 0) break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    tokens.Add(content[start..i]);
                    continue;
                }

                int end = content.IndexOf('>', i + 1);
                if (end < 0)
                {
                    tokens.Add(content[i..]);
                    break;
                }
                tokens.Add(content[i..(end + 1)]);
                i = end + 1;
                continue;
            }

            // Safely consume stray closing delimiters so tokenizer NEVER stalls
            if (c is ')' or ']' or '>')
            {
                i++;
                continue;
            }

            if (c == '/')
            {
                int start = i++;
                while (i < content.Length && !char.IsWhiteSpace(content[i]) && content[i] is not ('/' or '(' or ')' or '[' or ']' or '<' or '>' or '%'))
                    i++;
                tokens.Add(content[start..i]);
                continue;
            }

            int tokStart = i;
            while (i < content.Length && !char.IsWhiteSpace(content[i]) && content[i] is not ('(' or ')' or '[' or ']' or '<' or '>' or '/' or '%'))
                i++;
            if (i > tokStart)
            {
                string ident = content[tokStart..i];
                if (ident == "ID")
                {
                    int biIdx = tokens.LastIndexOf("BI");
                    if (biIdx >= 0 && tokens.Count - biIdx <= 40)
                    {
                        if (i < content.Length && char.IsWhiteSpace(content[i])) i++;
                        int eiPos = FindInlineImageEnd(content, i);
                        i = eiPos >= 0 ? eiPos + 2 : content.Length;
                        tokens.RemoveRange(biIdx, tokens.Count - biIdx);
                        continue;
                    }
                }
                tokens.Add(ident);
            }
            else
                i++;
        }

        return tokens;
    }

    private static int FindInlineImageEnd(string content, int start)
    {
        for (int p = start; p + 1 < content.Length; p++)
        {
            if (content[p] == 'E' && content[p + 1] == 'I' &&
                (p == start || char.IsWhiteSpace(content[p - 1])) &&
                (p + 2 >= content.Length || char.IsWhiteSpace(content[p + 2]) ||
                 content[p + 2] is '/' or '(' or '[' or '<' or '%'))
            {
                return p;
            }
        }
        return -1;
    }

    private static bool IsPdfOperator(string tok) => tok is
        "q" or "Q" or "cm" or "w" or "J" or "j" or "M" or "d" or "ri" or "i" or "gs" or
        "RG" or "rg" or "G" or "g" or "K" or "k" or "CS" or "cs" or "SC" or "sc" or "SCN" or "scn" or
        "re" or "m" or "l" or "c" or "v" or "y" or "h" or
        "S" or "s" or "f" or "F" or "f*" or "B" or "b" or "B*" or "b*" or "n" or "W" or "W*" or
        "Do" or "BT" or "ET" or "Tf" or "Td" or "TD" or "Tm" or "T*" or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts" or
        "Tj" or "TJ" or "'" or "\"" or "BMC" or "BDC" or "EMC" or "MP" or "DP" or "sh";

    private static double[] MultiplyMatrix(double[] a, double[] b) =>
    [
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5]
    ];

    private static string UnescapePdfString(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                char next = raw[++i];
                if (next is '\r' or '\n')
                {
                    if (next == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
                        i++;
                    continue;
                }

                if (next is >= '0' and <= '7')
                {
                    int octal = next - '0';
                    for (int k = 0; k < 2 && i + 1 < raw.Length && raw[i + 1] is >= '0' and <= '7'; k++)
                        octal = (octal * 8) + (raw[++i] - '0');
                    sb.Append((char)(octal & 0xFF));
                    continue;
                }

                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    '(' => '(',
                    ')' => ')',
                    '\\' => '\\',
                    _ => next
                });
            }
            else
            {
                sb.Append(raw[i]);
            }
        }
        return sb.ToString();
    }

    private static bool TryParseMediaBox(string dict, out double[] box)
    {
        box = [0, 0, 612, 792];
        var m = CropBoxRegex().Match(dict);
        if (!m.Success)
            m = MediaBoxRegex().Match(dict);
        if (!m.Success)
            return false;

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(m.Groups[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out box[i]))
                return false;
        }
        return true;
    }

    private static bool TryParseIntEntry(string dict, string key, out int value)
    {
        value = 0;
        var m = Regex.Match(dict, Regex.Escape(key) + @"\s+(-?\d+)(?!\s+\d+\s+R\b)");
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDoubles(List<string> operands, int count, out double[] values)
    {
        values = new double[count];
        int start = operands.Count - count;
        if (start < 0) return false;
        for (int i = 0; i < count; i++)
        {
            if (!TryParseDouble(operands[start + i], out values[i]))
                return false;
        }
        return true;
    }

    private static bool TryParseDouble(string raw, out double val) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out val);

    private static bool HasPdfSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        int searchLen = Math.Min(bytes.Length - 5, 1024);
        for (int i = 0; i <= searchLen; i++)
        {
            if (bytes[i] == '%' && bytes[i + 1] == 'P' && bytes[i + 2] == 'D' && bytes[i + 3] == 'F' && bytes[i + 4] == '-')
                return true;
        }
        return false;
    }

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex(@"/Type\s*/Catalog\b", RegexOptions.Compiled)]
    private static partial Regex CatalogTypeRegex();

    [GeneratedRegex(@"/Type\s*/Pages\b", RegexOptions.Compiled)]
    private static partial Regex PagesTypeRegex();

    [GeneratedRegex(@"/Type\s*/Page\b", RegexOptions.Compiled)]
    private static partial Regex PageTypeRegex();

    [GeneratedRegex(@"/Root\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex RootRefRegex();

    [GeneratedRegex(@"/Pages\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex PagesRefRegex();

    [GeneratedRegex(@"/Kids\s*\[(.*?)\]", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex KidsArrayRegex();

    [GeneratedRegex(@"(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex IndirectRefRegex();

    [GeneratedRegex(@"/([A-Za-z0-9_+.\-]+)\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex NamedRefRegex();

    [GeneratedRegex(@"/MediaBox\s*\[\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s*\]", RegexOptions.Compiled)]
    private static partial Regex MediaBoxRegex();

    [GeneratedRegex(@"/CropBox\s*\[\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s*\]", RegexOptions.Compiled)]
    private static partial Regex CropBoxRegex();

    [GeneratedRegex(@"/Info\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex InfoRefRegex();

    [GeneratedRegex(@"/Type\s*/XRef\b", RegexOptions.Compiled)]
    private static partial Regex XRefTypeRegex();

    [GeneratedRegex(@"(?:/(?:Dest|Next|Prev|First|Last)(?=[\s/<\[(\r\n])|/Type\s*/Outlines\b)", RegexOptions.Compiled)]
    private static partial Regex OutlineNodeRegex();

    [GeneratedRegex(@"/Title(?=\s*[(<])", RegexOptions.Compiled)]
    private static partial Regex TitleKeyRegex();

    private static string ExtractDocumentTitle(
        byte[] pdfBytes,
        Dictionary<int, PdfObjectEntry> objects,
        string fallbackTitle)
    {
        int tailLen = Math.Min(pdfBytes.Length, 65536);
        string tailText = Encoding.Latin1.GetString(pdfBytes, pdfBytes.Length - tailLen, tailLen);
        var tailMatches = InfoRefRegex().Matches(tailText);
        for (int i = tailMatches.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(tailMatches[i].Groups[1].Value, out int infoId) &&
                objects.TryGetValue(infoId, out var infoObj) &&
                TryExtractPdfTitleString(infoObj.DictionaryText, out var infoTitle))
            {
                return infoTitle;
            }
        }

        foreach (var xrefObj in objects.Values
            .Where(o => XRefTypeRegex().IsMatch(o.DictionaryText))
            .OrderByDescending(o => o.FileOffset))
        {
            var m = InfoRefRegex().Match(xrefObj.DictionaryText);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out int infoId) &&
                objects.TryGetValue(infoId, out var infoObj) &&
                TryExtractPdfTitleString(infoObj.DictionaryText, out var infoTitle))
            {
                return infoTitle;
            }
        }

        foreach (var obj in objects.Values)
        {
            if (!obj.DictionaryText.Contains("/Title", StringComparison.Ordinal))
                continue;
            if (OutlineNodeRegex().IsMatch(obj.DictionaryText))
                continue;
            if (TryExtractPdfTitleString(obj.DictionaryText, out var title))
                return title;
        }

        return fallbackTitle;
    }

    internal static bool TryExtractPdfTitleString(string dict, out string title)
    {
        title = string.Empty;
        var match = TitleKeyRegex().Match(dict);
        if (!match.Success)
            return false;

        int i = match.Index + match.Length;
        while (i < dict.Length && char.IsWhiteSpace(dict[i]))
            i++;

        if (i >= dict.Length)
            return false;

        string decoded;
        if (dict[i] == '(')
        {
            var sb = new StringBuilder();
            ReadBalancedParenLiteral(dict, ref i, sb);
            string token = sb.ToString();
            string inner = token.Length >= 2 && token[0] == '(' && token[^1] == ')'
                ? token[1..^1]
                : token.TrimStart('(');
            string unescaped = UnescapePdfString(inner);
            decoded = DecodePdfStringBytes(Encoding.Latin1.GetBytes(unescaped));
        }
        else if (dict[i] == '<' && (i + 1 >= dict.Length || dict[i + 1] != '<'))
        {
            int end = dict.IndexOf('>', i + 1);
            if (end < 0)
                return false;

            string rawHex = dict[(i + 1)..end];
            var hexChars = new StringBuilder(rawHex.Length);
            foreach (char ch in rawHex)
            {
                if (Uri.IsHexDigit(ch))
                    hexChars.Append(ch);
            }
            if (hexChars.Length == 0)
                return false;
            if (hexChars.Length % 2 != 0)
                hexChars.Append('0');

            var bytes = new byte[hexChars.Length / 2];
            string hexStr = hexChars.ToString();
            for (int b = 0; b < bytes.Length; b++)
            {
                if (!byte.TryParse(hexStr.AsSpan(b * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[b]))
                    return false;
            }

            decoded = DecodePdfStringBytes(bytes);
        }
        else
        {
            return false;
        }

        decoded = decoded.Replace("\0", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
            return false;

        title = decoded;
        return true;
    }

    private static string DecodePdfStringBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    private static void ReadBalancedParenLiteral(string content, ref int i, StringBuilder sb)
    {
        sb.Append('(');
        int depth = 1;
        i++;
        while (i < content.Length && depth > 0)
        {
            char ch = content[i++];
            if (ch == '\\' && i < content.Length)
                sb.Append('\\').Append(content[i++]);
            else
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                sb.Append(ch);
            }
        }
    }
}
