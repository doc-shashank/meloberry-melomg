using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Infrastructure.Extraction;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using WinPdf = Windows.Data.Pdf;

namespace Melomg.Infrastructure.Pdf;

/// <summary>
/// Renders actual PDF pages and sub-regions using Windows 10/11 native WinRT
/// <c>Windows.Data.Pdf</c> (Direct2D/PDFium backend) with zero OS file locks.
/// </summary>
public sealed class WindowsPdfPageRenderer : IPdfPageRenderer
{
    public async Task<Result<IPdfRenderSession>> OpenSessionAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return DocumentErrors.PdfFileNotFound(pdfPath ?? string.Empty);

        byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
        InMemoryRandomAccessStream? memStream = null;

        try
        {
            memStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(memStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(pdfBytes);
                await writer.StoreAsync().AsTask(cancellationToken);
                await writer.FlushAsync().AsTask(cancellationToken);
                writer.DetachStream();
            }

            memStream.Seek(0);
            var winDoc = await WinPdf.PdfDocument.LoadFromStreamAsync(memStream).AsTask(cancellationToken);
            if (winDoc.PageCount == 0)
            {
                memStream.Dispose();
                return DocumentErrors.InvalidPdfFormat(pdfPath, "Windows PDF renderer opened 0 pages.");
            }

            var session = new WindowsPdfRenderSession(pdfPath, winDoc, memStream);
            memStream = null;
            return Result<IPdfRenderSession>.Success(session);
        }
        catch (OperationCanceledException)
        {
            memStream?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            memStream?.Dispose();
            return DocumentErrors.PdfRenderFailed(pdfPath, 0, ex.Message);
        }
    }

    private sealed class WindowsPdfRenderSession(
        string pdfPath,
        WinPdf.PdfDocument doc,
        InMemoryRandomAccessStream backingStream) : IPdfRenderSession
    {
        public int PageCount => (int)doc.PageCount;
        public string RenderEngine => "windows-pdf";

        public Task<Result<RenderedRgbBitmap>> RenderPageAsync(
            int pageNumber,
            int dpi,
            CancellationToken cancellationToken = default) =>
            RenderCoreAsync(
                pageNumber,
                dpi,
                page => (page.Size.Width * (dpi / 96.0), page.Size.Height * (dpi / 96.0), 8192.0, null),
                cancellationToken);

        public Task<Result<RenderedRgbBitmap>> RenderRegionAsync(
            int pageNumber,
            PdfPageInfo pageInfo,
            BoundingBox regionPts,
            int dpi,
            int paddingPx,
            CancellationToken cancellationToken = default) =>
            RenderCoreAsync(
                pageNumber,
                dpi,
                page =>
                {
                    var (cropLeftPts, cropTopPts, cropWidthPts, cropHeightPts) =
                        RgbCanvasPrimitives.ComputeCroppedBoundsPts(regionPts, pageInfo, dpi, paddingPx);
                    double dipScaleX = page.Size.Width / Math.Max(1.0, pageInfo.WidthPts);
                    double dipScaleY = page.Size.Height / Math.Max(1.0, pageInfo.HeightPts);
                    double sx = Math.Clamp(cropLeftPts * dipScaleX, 0.0, Math.Max(0.0, page.Size.Width - 1.0));
                    double sy = Math.Clamp(cropTopPts * dipScaleY, 0.0, Math.Max(0.0, page.Size.Height - 1.0));
                    double sw = Math.Clamp(cropWidthPts * dipScaleX, 1.0, page.Size.Width - sx);
                    double sh = Math.Clamp(cropHeightPts * dipScaleY, 1.0, page.Size.Height - sy);
                    return (cropWidthPts * (dpi / 72.0), cropHeightPts * (dpi / 72.0), 4096.0, new Rect(sx, sy, sw, sh));
                },
                cancellationToken);

        private async Task<Result<RenderedRgbBitmap>> RenderCoreAsync(
            int pageNumber,
            int dpi,
            Func<WinPdf.PdfPage, (double RawW, double RawH, double MaxDim, Rect? SourceRect)> computeBounds,
            CancellationToken cancellationToken)
        {
            if (pageNumber < 1 || pageNumber > PageCount)
                return DocumentErrors.InvalidPageNumber(pageNumber, PageCount);

            try
            {
                using var page = doc.GetPage((uint)(pageNumber - 1));
                await page.PreparePageAsync().AsTask(cancellationToken);

                var (rawW, rawH, maxDim, sourceRect) = computeBounds(page);
                double capScale = Math.Min(1.0, Math.Min(maxDim / Math.Max(1.0, rawW), maxDim / Math.Max(1.0, rawH)));
                var opts = new WinPdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(16, Math.Round(rawW * capScale)),
                    DestinationHeight = (uint)Math.Max(16, Math.Round(rawH * capScale)),
                    BackgroundColor = Color.FromArgb(255, 255, 255, 255),
                    IsIgnoringHighContrast = true,
                    BitmapEncoderId = BitmapEncoder.BmpEncoderId
                };
                if (sourceRect.HasValue)
                    opts.SourceRect = sourceRect.Value;

                using var outStream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(outStream, opts).AsTask(cancellationToken);
                outStream.Seek(0);

                uint byteCount = checked((uint)outStream.Size);
                var bmpBytes = new byte[byteCount];
                using (var reader = new DataReader(outStream.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync(byteCount).AsTask(cancellationToken);
                    reader.ReadBytes(bmpBytes);
                    reader.DetachStream();
                }

                var rgb = BmpRgbDecoder.DecodeToRgb(bmpBytes, out int actualW, out int actualH);
                return new RenderedRgbBitmap(actualW, actualH, dpi, rgb, RenderEngine);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return DocumentErrors.PdfRenderFailed(pdfPath, pageNumber, ex.Message);
            }
        }

        public ValueTask DisposeAsync()
        {
            backingStream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal static class BmpRgbDecoder
    {
        public static byte[] DecodeToRgb(byte[] bmp, out int width, out int height)
        {
            if (bmp.Length < 54 || bmp[0] != (byte)'B' || bmp[1] != (byte)'M')
                throw new InvalidDataException("Invalid BMP header.");

            int pixelOffset = BitConverter.ToInt32(bmp, 10);
            int headerSize = BitConverter.ToInt32(bmp, 14);
            width = BitConverter.ToInt32(bmp, 18);
            int rawHeight = BitConverter.ToInt32(bmp, 22);
            bool topDown = rawHeight < 0;
            height = Math.Abs(rawHeight);

            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"Invalid BMP dimensions {width}x{rawHeight}.");

            ushort bpp = BitConverter.ToUInt16(bmp, 28);
            int compression = BitConverter.ToInt32(bmp, 30);

            int rByteIdx = 2, gByteIdx = 1, bByteIdx = 0; // default BGRA / BGR layout
            if (bpp == 32 && compression == 3 && bmp.Length >= 66)
            {
                uint rMask = BitConverter.ToUInt32(bmp, 54);
                uint gMask = BitConverter.ToUInt32(bmp, 58);
                uint bMask = BitConverter.ToUInt32(bmp, 62);
                rByteIdx = MaskToByteOffset(rMask, 2);
                gByteIdx = MaskToByteOffset(gMask, 1);
                bByteIdx = MaskToByteOffset(bMask, 0);
            }

            if (bpp != 24 && bpp != 32)
                throw new NotSupportedException($"Unsupported BMP bit depth: {bpp}.");

            int bppBytes = bpp / 8;
            int srcStride = bpp == 32 ? width * 4 : ((width * 3 + 3) / 4) * 4;
            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                int srcRow = topDown ? y : (height - 1 - y);
                int srcBase = pixelOffset + srcRow * srcStride;
                int dstBase = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    int sp = srcBase + x * bppBytes;
                    if (sp + bppBytes - 1 >= bmp.Length) continue;
                    int dp = dstBase + x * 3;
                    rgb[dp] = bmp[sp + rByteIdx];
                    rgb[dp + 1] = bmp[sp + gByteIdx];
                    rgb[dp + 2] = bmp[sp + bByteIdx];
                }
            }

            _ = headerSize;
            return rgb;
        }

        private static int MaskToByteOffset(uint mask, int fallback) => mask switch
        {
            0x000000FFu => 0,
            0x0000FF00u => 1,
            0x00FF0000u => 2,
            0xFF000000u => 3,
            _ => fallback
        };
    }
}
