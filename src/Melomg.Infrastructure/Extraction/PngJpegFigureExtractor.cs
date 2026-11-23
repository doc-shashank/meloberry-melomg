using System.IO.Compression;
using System.Text.Json;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Extraction;
using Melomg.Infrastructure.Serialization;

namespace Melomg.Infrastructure.Extraction;

public sealed class PngJpegFigureExtractor(
    IPdfPageRenderer pageRenderer,
    IFigureLabelDetector labelDetector) : IFigureImageExtractor, IPageSnapshotExporter
{
    public async Task<Result<ExtractedFigureArtifact>> ExtractFigureAsync(
        PdfDocument document,
        DocumentFigure figure,
        ExtractionOptions options,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var batchRes = await ExtractFiguresAsync(document, [figure], options, outputDirectory, cancellationToken);
        if (!batchRes.IsSuccess)
            return batchRes.Error!;

        return batchRes.Value[0];
    }

    public async Task<Result<IReadOnlyList<ExtractedFigureArtifact>>> ExtractFiguresAsync(
        PdfDocument document,
        IReadOnlyList<DocumentFigure> figures,
        ExtractionOptions options,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (figures.Count == 0)
            return FigureErrors.NoFiguresMatch("empty selection");

        if (!File.Exists(document.StoredPdfPath))
            return DocumentErrors.PdfFileNotFound(document.StoredPdfPath);

        var sessionRes = await pageRenderer.OpenSessionAsync(document.StoredPdfPath, cancellationToken);
        if (!sessionRes.IsSuccess)
            return sessionRes.Error!;

        await using var session = sessionRes.Value;
        Directory.CreateDirectory(outputDirectory);

        var results = new List<ExtractedFigureArtifact>(figures.Count);
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var figure in figures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = document.GetPage(figure.PageNumber);
            if (page is null)
                return DocumentErrors.InvalidPageNumber(figure.PageNumber, document.PageCount);

            var effectiveBox = figure.GetEffectiveCropBox(options.IncludeCaption, page);
            var renderBox = ComputeClampedRenderBox(
                effectiveBox,
                figure.CaptionBoundingBox,
                options.IncludeCaption,
                page,
                options.Dpi,
                options.PaddingPx);

            var renderRes = await session.RenderRegionAsync(
                figure.PageNumber,
                page,
                renderBox,
                options.Dpi,
                paddingPx: 0,
                cancellationToken);

            if (!renderRes.IsSuccess)
                return renderRes.Error!;

            var bitmap = renderRes.Value;
            var (cropLeftPts, cropTopPts, cropWidthPts, cropHeightPts) =
                RgbCanvasPrimitives.ComputeCroppedBoundsPts(renderBox, page, options.Dpi, 0);

            double pxScaleX = bitmap.Width / Math.Max(0.5, cropWidthPts);
            double pxScaleY = bitmap.Height / Math.Max(0.5, cropHeightPts);

            var masksToApply = await ResolveMasksToApplyAsync(
                document.StoredPdfPath,
                figure,
                effectiveBox,
                options,
                cancellationToken);

            var appliedInfos = new List<AppliedMaskInfo>(masksToApply.Count);
            int maskSeq = 1;
            double cropRightPts = cropLeftPts + cropWidthPts;
            double cropBottomPts = cropTopPts + cropHeightPts;
            foreach (var mask in masksToApply)
            {
                if (mask.Box.Right <= cropLeftPts ||
                    mask.Box.X >= cropRightPts ||
                    mask.Box.Bottom <= cropTopPts ||
                    mask.Box.Y >= cropBottomPts)
                {
                    continue;
                }

                int mx = Math.Clamp((int)Math.Floor((mask.Box.X - cropLeftPts) * pxScaleX) - 2, 0, Math.Max(0, bitmap.Width - 1));
                int my = Math.Clamp((int)Math.Floor((mask.Box.Y - cropTopPts) * pxScaleY) - 2, 0, Math.Max(0, bitmap.Height - 1));
                int mr = Math.Clamp((int)Math.Ceiling((mask.Box.Right - cropLeftPts) * pxScaleX) + 2, mx + 1, bitmap.Width);
                int mb = Math.Clamp((int)Math.Ceiling((mask.Box.Bottom - cropTopPts) * pxScaleY) + 2, my + 1, bitmap.Height);
                int mw = Math.Max(1, mr - mx);
                int mh = Math.Max(1, mb - my);

                ImageObfuscator.ApplyMask(bitmap.RgbPixels, bitmap.Width, bitmap.Height, mx, my, mw, mh, mask.Style, mask.ColorHex);
                appliedInfos.Add(new AppliedMaskInfo(
                    maskSeq++,
                    mask.Box,
                    mx,
                    my,
                    mw,
                    mh,
                    mask.Text,
                    FigureLabelMask.FormatStyle(mask.Style),
                    mask.Source));
            }

            string ext = options.Format == "jpg" ? "jpg" : "png";
            string baseStem = ResolveUniqueFileStem(figure, options.NamingMode, usedFileNames);
            string imagePath = Path.Combine(outputDirectory, $"{baseStem}.{ext}");
            string? metaPath = null;
            byte[] encodedBytes = EncodeBitmap(bitmap, options.Format, options.Dpi, options.Quality);
            await File.WriteAllBytesAsync(imagePath, encodedBytes, cancellationToken);

            if (options.IncludeMetadata)
            {
                metaPath = Path.Combine(outputDirectory, $"{baseStem}.meta.json");
                var metadata = new FigureSidecarMetadata(
                    figure.Id,
                    document.Id,
                    figure.Index,
                    figure.PageNumber,
                    figure.PageFigureIndex,
                    figure.Category.ToString().ToLowerInvariant(),
                    figure.Confidence,
                    DocumentFigure.FormatSourceTag(figure.Source),
                    bitmap.RenderEngine,
                    figure.Label,
                    figure.VocabPrefix,
                    figure.FigureNumber,
                    figure.Caption,
                    options.IncludeCaption && figure.CaptionBoundingBox is not null,
                    figure.BoundingBox,
                    figure.CaptionBoundingBox,
                    effectiveBox,
                    bitmap.Width,
                    bitmap.Height,
                    options.Dpi,
                    options.Quality,
                    options.PaddingPx,
                    options.Format,
                    appliedInfos,
                    imagePath,
                    TimeProvider.System.GetUtcNow());

                await File.WriteAllTextAsync(
                    metaPath,
                    JsonSerializer.Serialize(metadata, FigureMetadataJsonContext.Default.FigureSidecarMetadata),
                    cancellationToken);
            }

            results.Add(new ExtractedFigureArtifact(
                figure.Id,
                figure.Index,
                figure.PageNumber,
                imagePath,
                metaPath,
                options.Format,
                options.Dpi,
                options.PaddingPx,
                bitmap.Width,
                bitmap.Height,
                encodedBytes.LongLength,
                options.IncludeCaption && figure.CaptionBoundingBox is not null,
                bitmap.RenderEngine,
                appliedInfos,
                options.Quality,
                figure.FigureNumber,
                figure.Label,
                figure.Caption,
                figure.Confidence,
                figure.Category.ToString().ToLowerInvariant()));
        }

        return Result<IReadOnlyList<ExtractedFigureArtifact>>.Success(results);
    }

    public async Task<Result<string>> WriteManifestAsync(
        PdfDocument document,
        IReadOnlyList<ExtractedFigureArtifact> artifacts,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string manifestPath = Path.Combine(outputDirectory, "manifest.json");
        string cwd = Directory.GetCurrentDirectory();
        string fullOutDir = Path.GetFullPath(outputDirectory);
        string relOutDir = MakeRelativeForwardPath(cwd, fullOutDir);

        var entries = new Dictionary<string, FigureManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var art in artifacts)
        {
            string baseKey = !string.IsNullOrWhiteSpace(art.FigureNumber) ? art.FigureNumber! : art.FigureId;
            string key = baseKey;
            if (entries.ContainsKey(key))
            {
                key = $"{baseKey}_i{art.Index}";
                int suffix = 2;
                while (entries.ContainsKey(key))
                    key = $"{baseKey}_i{art.Index}_{suffix++}";
            }

            string fullImgPath = Path.GetFullPath(art.OutputImagePath);
            string relImgPath = MakeRelativeForwardPath(cwd, fullImgPath);
            string fileName = Path.GetFileName(fullImgPath);
            var fig = document.FindFigure(art.FigureId);

            entries[key] = new FigureManifestEntry(
                art.FigureId,
                art.FigureNumber ?? fig?.FigureNumber,
                art.PageNumber,
                art.Category ?? fig?.Category.ToString().ToLowerInvariant() ?? "figure",
                art.Label ?? fig?.Label,
                art.Caption ?? fig?.Caption,
                art.Confidence > 0 ? art.Confidence : (fig?.Confidence ?? 0),
                relImgPath,
                fileName,
                art.PixelWidth,
                art.PixelHeight);
        }

        var manifest = new FigureManifestDocument(
            document.Id,
            TimeProvider.System.GetUtcNow(),
            relOutDir,
            entries);

        string json = JsonSerializer.Serialize(manifest, FigureManifestJsonContext.Default.FigureManifestDocument);
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
        return manifestPath;
    }

    private static string MakeRelativeForwardPath(string baseDir, string targetPath)
    {
        try
        {
            string normBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normTarget = Path.GetFullPath(targetPath);
            if (normTarget.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(normBase, normTarget).Replace('\\', '/');
        }
        catch
        {
        }
        return targetPath.Replace('\\', '/');
    }

    public async Task<Result<IReadOnlyList<RenderedPageArtifact>>> ExportPagesAsync(
        PdfDocument document,
        IReadOnlyList<int> pageNumbers,
        RenderPageOptions options,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(document.StoredPdfPath))
            return DocumentErrors.PdfFileNotFound(document.StoredPdfPath);

        var sessionRes = await pageRenderer.OpenSessionAsync(document.StoredPdfPath, cancellationToken);
        if (!sessionRes.IsSuccess)
            return sessionRes.Error!;

        await using var session = sessionRes.Value;
        Directory.CreateDirectory(outputDirectory);

        var artifacts = new List<RenderedPageArtifact>(pageNumbers.Count);
        foreach (int pageNum in pageNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = document.GetPage(pageNum);
            if (page is null)
                return DocumentErrors.InvalidPageNumber(pageNum, document.PageCount);

            var bmpRes = await session.RenderPageAsync(pageNum, options.Dpi, cancellationToken);
            if (!bmpRes.IsSuccess)
                return bmpRes.Error!;

            var bmp = bmpRes.Value;
            var pageFigs = document.Figures.Where(f => f.PageNumber == pageNum).ToList();
            if (options.Overlay)
                OverlayDrawer.DrawPageOverlay(bmp.RgbPixels, bmp.Width, bmp.Height, page, pageFigs);

            string ext = options.Format == "jpg" ? "jpg" : "png";
            string suffix = options.Overlay ? "_overlay" : "";
            string imagePath = Path.Combine(outputDirectory, $"page_{pageNum:D3}{suffix}.{ext}");
            byte[] encodedBytes = EncodeBitmap(bmp, options.Format, options.Dpi, options.Quality);
            await File.WriteAllBytesAsync(imagePath, encodedBytes, cancellationToken);

            artifacts.Add(new RenderedPageArtifact(
                pageNum,
                imagePath,
                options.Format,
                options.Dpi,
                bmp.Width,
                bmp.Height,
                encodedBytes.LongLength,
                options.Overlay,
                options.Overlay ? pageFigs.Count : 0,
                bmp.RenderEngine,
                options.Quality));
        }

        return Result<IReadOnlyList<RenderedPageArtifact>>.Success(artifacts);
    }

    public async Task<Result> VerifyExtractedImageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return new Error("Figure.MissingImage", $"Extracted image '{imagePath}' was not found on disk.", ErrorType.NotFound);

        byte[] header = new byte[12];
        await using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = await fs.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (read < 8)
            return new Error("Figure.CorruptedImage", $"Extracted image '{imagePath}' is truncated.", ErrorType.Validation);

        bool isPng = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                     header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
        bool isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

        if (!isPng && !isJpeg)
            return new Error("Figure.CorruptedImage", $"Extracted image '{imagePath}' does not have valid PNG or JPEG magic bytes.", ErrorType.Validation);

        return Result.Success();
    }

    private async Task<List<(BoundingBox Box, string? Text, ObfuscationStyle Style, string? ColorHex, string Source)>>
        ResolveMasksToApplyAsync(
            string pdfPath,
            DocumentFigure figure,
            BoundingBox effectiveBox,
            ExtractionOptions options,
            CancellationToken cancellationToken)
    {
        var list = new List<(BoundingBox Box, string? Text, ObfuscationStyle Style, string? ColorHex, string Source)>();

        if (!options.IgnoreSavedMasks)
        {
            foreach (var saved in figure.LabelMasks)
            {
                if (saved.BoundingBox.Right > effectiveBox.X &&
                    saved.BoundingBox.X < effectiveBox.Right &&
                    saved.BoundingBox.Bottom > effectiveBox.Y &&
                    saved.BoundingBox.Y < effectiveBox.Bottom)
                {
                    list.Add((saved.BoundingBox, saved.LabelText, saved.Style, saved.ColorHex, "saved"));
                }
            }
        }

        bool wantAuto = options.ObfuscateAllLabels || (options.ObfuscateLabelPatterns is { Count: > 0 });
        if (!wantAuto)
            return list;

        var labelsRes = await labelDetector.DetectLabelsInRegionAsync(pdfPath, figure.PageNumber, figure.BoundingBox, cancellationToken);
        if (!labelsRes.IsSuccess)
            return list;

        foreach (var lbl in labelsRes.Value)
        {
            if (!options.ObfuscateAllLabels &&
                !ExtractionOptions.MatchesAnyPattern(lbl.Text, options.ObfuscateLabelPatterns ?? []))
            {
                continue;
            }

            // Exclude caption block lines from automatic diagram-label redaction
            if (figure.CaptionBoundingBox is { } capBox && lbl.BoundingBox.ComputeContainment(capBox) >= 0.50)
                continue;

            if (list.Any(existing => existing.Box.ComputeIoU(lbl.BoundingBox) >= 0.50))
                continue;

            list.Add((lbl.BoundingBox, lbl.Text, options.ObfuscationStyle, options.ObfuscationColorHex, "auto-label"));
        }

        return list;
    }

    private static string ResolveUniqueFileStem(DocumentFigure figure, string namingMode, HashSet<string> usedFileNames)
    {
        string baseStem;
        if (string.Equals(namingMode, "id", StringComparison.OrdinalIgnoreCase))
        {
            baseStem = figure.Id;
        }
        else
        {
            string safeNum = FigureNumberNormalizer.SanitizeForFileName(figure.FigureNumber);
            baseStem = !string.IsNullOrWhiteSpace(safeNum)
                ? $"fig_{safeNum}"
                : $"fig_p{figure.PageNumber}_i{figure.Index}";
        }

        string candidate = baseStem;
        if (!usedFileNames.Add(candidate))
        {
            candidate = $"{baseStem}_i{figure.Index}";
            int suffix = 2;
            while (!usedFileNames.Add(candidate))
                candidate = $"{baseStem}_i{figure.Index}_{suffix++}";
        }

        return candidate;
    }

    private static byte[] EncodeBitmap(RenderedRgbBitmap bmp, string format, int dpi, int quality) =>
        format == "jpg"
            ? JpegBaselineEncoder.EncodeRgb(bmp.RgbPixels, bmp.Width, bmp.Height, dpi, quality)
            : PngRgbEncoder.EncodeRgb(bmp.RgbPixels, bmp.Width, bmp.Height, dpi, quality);

    private static BoundingBox ComputeClampedRenderBox(
        BoundingBox effectiveBox,
        BoundingBox? captionBox,
        bool includeCaption,
        PdfPageInfo page,
        int dpi,
        int paddingPx)
    {
        var (left, top, cropW, cropH) = RgbCanvasPrimitives.ComputeCroppedBoundsPts(effectiveBox, page, dpi, paddingPx);
        double right = left + cropW, bottom = top + cropH;

        if (!includeCaption && captionBox is { } cap && right > cap.X && left < cap.Right)
        {
            if (cap.Y >= effectiveBox.Y + effectiveBox.Height * 0.45 && cap.Y - 1.0 > effectiveBox.Y + 12.0)
                bottom = Math.Min(bottom, cap.Y - 1.0);
            else if (cap.Bottom <= effectiveBox.Y + effectiveBox.Height * 0.55 && cap.Bottom + 1.0 < effectiveBox.Bottom - 12.0)
                top = Math.Max(top, cap.Bottom + 1.0);
        }

        return new BoundingBox(left, top, Math.Max(0.5, right - left), Math.Max(0.5, bottom - top));
    }
}

/// <summary>
/// Pure managed IEEE/RFC 2083 PNG RGB encoder.
/// </summary>
internal static class PngRgbEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeRgb(byte[] rgb, int width, int height, int dpi = 150, int quality = 90)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature, 0, PngSignature.Length);

        var ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        uint pixelsPerMeter = (uint)Math.Max(1, Math.Round(dpi / 0.0254));
        var phys = new byte[9];
        WriteUInt32BigEndian(phys, 0, pixelsPerMeter);
        WriteUInt32BigEndian(phys, 4, pixelsPerMeter);
        phys[8] = 1; // meter unit
        WriteChunk(output, "pHYs"u8, phys);

        int stride = width * 3;
        var rawScanlines = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int dstRow = y * (stride + 1);
            rawScanlines[dstRow] = 0;
            Buffer.BlockCopy(rgb, y * stride, rawScanlines, dstRow + 1, stride);
        }

        CompressionLevel compression = quality >= 90
            ? CompressionLevel.SmallestSize
            : quality >= 40
                ? CompressionLevel.Optimal
                : CompressionLevel.Fastest;

        byte[] compressedIdat;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, compression, leaveOpen: true))
                zlib.Write(rawScanlines, 0, rawScanlines.Length);
            compressedIdat = ms.ToArray();
        }

        WriteChunk(output, "IDAT"u8, compressedIdat);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        var lenBuf = new byte[4];
        WriteUInt32BigEndian(lenBuf, 0, (uint)data.Length);
        stream.Write(lenBuf, 0, 4);
        stream.Write(type);
        if (data.Length > 0)
            stream.Write(data, 0, data.Length);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        var crcBuf = new byte[4];
        WriteUInt32BigEndian(crcBuf, 0, crc);
        stream.Write(crcBuf, 0, 4);
    }

    private static void WriteUInt32BigEndian(byte[] buf, int offset, uint val)
    {
        buf[offset] = (byte)(val >> 24);
        buf[offset + 1] = (byte)(val >> 16);
        buf[offset + 2] = (byte)(val >> 8);
        buf[offset + 3] = (byte)val;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            table[n] = c;
        }
        return table;
    }
}

/// <summary>
/// Pure managed Baseline JFIF JPEG encoder (SOI, APP0 JFIF, DQT, SOF0, DHT, SOS, EOI).
/// </summary>
internal static class JpegBaselineEncoder
{
    private static readonly int[] ZigZag =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    private static readonly byte[] LumQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    ];

    private static readonly byte[] ChromQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    private static readonly byte[] StdDcLumNrcodes = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] StdDcLumValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] StdDcChromNrcodes = [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] StdDcChromValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] StdAcLumNrcodes = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] StdAcLumValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    ];

    private static readonly byte[] StdAcChromNrcodes = [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] StdAcChromValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    ];

    private static readonly (ushort Code, int Len)[] DcLumTable = BuildHuffmanTable(StdDcLumNrcodes, StdDcLumValues);
    private static readonly (ushort Code, int Len)[] DcChromTable = BuildHuffmanTable(StdDcChromNrcodes, StdDcChromValues);
    private static readonly (ushort Code, int Len)[] AcLumTable = BuildHuffmanTable(StdAcLumNrcodes, StdAcLumValues);
    private static readonly (ushort Code, int Len)[] AcChromTable = BuildHuffmanTable(StdAcChromNrcodes, StdAcChromValues);
    private static readonly double[,] CosTable = BuildCosTable();

    public static byte[] EncodeRgb(byte[] rgb, int width, int height, int dpi, int quality = 90)
    {
        int clampedQ = Math.Clamp(quality, 1, 100);
        int scale = clampedQ < 50 ? 5000 / clampedQ : 200 - clampedQ * 2;
        byte[] lumQuant = ScaleQuantTable(LumQuant, scale);
        byte[] chromQuant = ScaleQuantTable(ChromQuant, scale);

        using var ms = new MemoryStream();

        ms.Write([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01]);
        WriteUInt16Be(ms, (ushort)Math.Clamp(dpi, 72, 600));
        WriteUInt16Be(ms, (ushort)Math.Clamp(dpi, 72, 600));
        ms.Write([0x00, 0x00]);

        WriteDqt(ms, 0, lumQuant);
        WriteDqt(ms, 1, chromQuant);

        ms.Write([0xFF, 0xC0, 0x00, 17, 8]);
        WriteUInt16Be(ms, (ushort)height);
        WriteUInt16Be(ms, (ushort)width);
        ms.Write([3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);

        WriteDht(ms, 0x00, StdDcLumNrcodes, StdDcLumValues);
        WriteDht(ms, 0x10, StdAcLumNrcodes, StdAcLumValues);
        WriteDht(ms, 0x01, StdDcChromNrcodes, StdDcChromValues);
        WriteDht(ms, 0x11, StdAcChromNrcodes, StdAcChromValues);

        ms.Write([0xFF, 0xDA, 0x00, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0x00, 0x3F, 0x00]);

        var bitWriter = new BitWriter(ms);
        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;
        var yBlock = new double[64];
        var cbBlock = new double[64];
        var crBlock = new double[64];

        for (int by = 0; by < height; by += 8)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                for (int py = 0; py < 8; py++)
                {
                    int sy = Math.Min(height - 1, by + py);
                    for (int px = 0; px < 8; px++)
                    {
                        int sx = Math.Min(width - 1, bx + px);
                        int idx = (sy * width + sx) * 3;
                        double r = rgb[idx];
                        double g = rgb[idx + 1];
                        double b = rgb[idx + 2];

                        int bi = py * 8 + px;
                        yBlock[bi] = (0.299 * r + 0.587 * g + 0.114 * b) - 128.0;
                        cbBlock[bi] = (-0.168736 * r - 0.331264 * g + 0.5 * b);
                        crBlock[bi] = (0.5 * r - 0.418688 * g - 0.081312 * b);
                    }
                }

                prevDcY = EncodeBlock(yBlock, lumQuant, prevDcY, DcLumTable, AcLumTable, bitWriter);
                prevDcCb = EncodeBlock(cbBlock, chromQuant, prevDcCb, DcChromTable, AcChromTable, bitWriter);
                prevDcCr = EncodeBlock(crBlock, chromQuant, prevDcCr, DcChromTable, AcChromTable, bitWriter);
            }
        }

        bitWriter.Flush();
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private static byte[] ScaleQuantTable(byte[] baseTable, int scale)
    {
        var scaled = new byte[64];
        for (int i = 0; i < 64; i++)
            scaled[i] = (byte)Math.Clamp((baseTable[i] * scale + 50) / 100, 1, 255);
        return scaled;
    }

    private static int EncodeBlock(
        double[] block,
        byte[] quantTable,
        int prevDc,
        (ushort Code, int Len)[] dcTable,
        (ushort Code, int Len)[] acTable,
        BitWriter writer)
    {
        var dct = new double[64];
        var quant = new int[64];

        for (int v = 0; v < 8; v++)
        {
            double cv = v == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (int u = 0; u < 8; u++)
            {
                double cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                double sum = 0.0;
                for (int y = 0; y < 8; y++)
                {
                    double cosY = CosTable[y, v];
                    for (int x = 0; x < 8; x++)
                        sum += block[y * 8 + x] * CosTable[x, u] * cosY;
                }
                dct[v * 8 + u] = 0.25 * cu * cv * sum;
            }
        }

        for (int k = 0; k < 64; k++)
        {
            int rasterIdx = ZigZag[k];
            quant[k] = (int)Math.Round(dct[rasterIdx] / quantTable[rasterIdx]);
        }

        int dc = quant[0];
        int diff = dc - prevDc;
        WriteSignedValue(writer, diff, dcTable);

        int endPos = 63;
        while (endPos > 0 && quant[endPos] == 0)
            endPos--;

        if (endPos == 0)
        {
            var eob = acTable[0x00];
            writer.WriteBits(eob.Code, eob.Len);
            return dc;
        }

        int iPos = 1;
        while (iPos <= endPos)
        {
            int zeroRun = 0;
            while (quant[iPos] == 0 && iPos <= endPos)
            {
                zeroRun++;
                iPos++;
            }

            while (zeroRun >= 16)
            {
                var zrl = acTable[0xF0];
                writer.WriteBits(zrl.Code, zrl.Len);
                zeroRun -= 16;
            }

            int val = quant[iPos];
            int cat = GetBitLength(Math.Abs(val));
            int symbol = (zeroRun << 4) | cat;
            var huff = acTable[symbol];
            writer.WriteBits(huff.Code, huff.Len);
            WriteAmplitudeBits(writer, val, cat);
            iPos++;
        }

        if (endPos != 63)
        {
            var eob = acTable[0x00];
            writer.WriteBits(eob.Code, eob.Len);
        }

        return dc;
    }

    private static void WriteSignedValue(BitWriter writer, int val, (ushort Code, int Len)[] dcTable)
    {
        int cat = GetBitLength(Math.Abs(val));
        var huff = dcTable[cat];
        writer.WriteBits(huff.Code, huff.Len);
        WriteAmplitudeBits(writer, val, cat);
    }

    private static void WriteAmplitudeBits(BitWriter writer, int val, int cat)
    {
        if (cat == 0) return;
        int encoded = val > 0 ? val : ((1 << cat) - 1 + val);
        writer.WriteBits((ushort)encoded, cat);
    }

    private static int GetBitLength(int absVal)
    {
        int len = 0;
        while (absVal > 0)
        {
            len++;
            absVal >>= 1;
        }
        return len;
    }

    private static (ushort Code, int Len)[] BuildHuffmanTable(byte[] nrcodes, byte[] values)
    {
        var table = new (ushort Code, int Len)[256];
        ushort code = 0;
        int valIdx = 0;
        for (int len = 1; len <= 16; len++)
        {
            int count = nrcodes[len];
            for (int i = 0; i < count; i++)
            {
                table[values[valIdx++]] = (code, len);
                code++;
            }
            code <<= 1;
        }
        return table;
    }

    private static double[,] BuildCosTable()
    {
        var t = new double[8, 8];
        for (int a = 0; a < 8; a++)
            for (int b = 0; b < 8; b++)
                t[a, b] = Math.Cos(((2 * a + 1) * b * Math.PI) / 16.0);
        return t;
    }

    private static void WriteDqt(Stream s, byte id, byte[] table)
    {
        s.Write([0xFF, 0xDB, 0x00, 67, id]);
        var ordered = new byte[64];
        for (int k = 0; k < 64; k++)
            ordered[k] = table[ZigZag[k]];
        s.Write(ordered, 0, 64);
    }

    private static void WriteDht(Stream s, byte info, byte[] nrcodes, byte[] values)
    {
        int len = 2 + 1 + 16 + values.Length;
        s.Write([0xFF, 0xC4]);
        WriteUInt16Be(s, (ushort)len);
        s.WriteByte(info);
        s.Write(nrcodes, 1, 16);
        s.Write(values, 0, values.Length);
    }

    private static void WriteUInt16Be(Stream s, ushort val)
    {
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)val);
    }

    private sealed class BitWriter(Stream stream)
    {
        private int _buffer;
        private int _bitCount;

        public void WriteBits(ushort value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                _bitCount++;
                if (_bitCount == 8)
                {
                    EmitByte((byte)_buffer);
                    _buffer = 0;
                    _bitCount = 0;
                }
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                int remaining = 8 - _bitCount;
                _buffer = (_buffer << remaining) | ((1 << remaining) - 1);
                EmitByte((byte)_buffer);
                _buffer = 0;
                _bitCount = 0;
            }
        }

        private void EmitByte(byte b)
        {
            stream.WriteByte(b);
            if (b == 0xFF)
                stream.WriteByte(0x00);
        }
    }
}
