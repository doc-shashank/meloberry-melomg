using Melomg.Domain.Documents;

namespace Melomg.Infrastructure.Extraction;

/// <summary>
/// Shared RGB pixel-buffer drawing primitives and PDF-to-crop bounds computation
/// used by page rendering, visual overlays, and label obfuscation.
/// </summary>
internal static class RgbCanvasPrimitives
{
    public static (double LeftPts, double TopPts, double WidthPts, double HeightPts) ComputeCroppedBoundsPts(
        BoundingBox regionPts,
        PdfPageInfo pageInfo,
        int dpi,
        int paddingPx)
    {
        double padPts = paddingPx * (72.0 / Math.Max(72, dpi));
        double left = Math.Clamp(regionPts.X - padPts, 0.0, Math.Max(0.0, pageInfo.WidthPts - 0.5));
        double top = Math.Clamp(regionPts.Y - padPts, 0.0, Math.Max(0.0, pageInfo.HeightPts - 0.5));
        double right = Math.Clamp(regionPts.Right + padPts, left + 0.5, pageInfo.WidthPts);
        double bottom = Math.Clamp(regionPts.Bottom + padPts, top + 0.5, pageInfo.HeightPts);
        return (left, top, right - left, bottom - top);
    }

    public static void FillRect(byte[] rgb, int imgW, int imgH, int x, int y, int w, int h, (byte R, byte G, byte B) color)
    {
        int xStart = Math.Max(0, x);
        int yStart = Math.Max(0, y);
        int xEnd = Math.Min(imgW - 1, x + w - 1);
        int yEnd = Math.Min(imgH - 1, y + h - 1);
        for (int py = yStart; py <= yEnd; py++)
        {
            for (int px = xStart; px <= xEnd; px++)
            {
                int idx = (py * imgW + px) * 3;
                rgb[idx] = color.R;
                rgb[idx + 1] = color.G;
                rgb[idx + 2] = color.B;
            }
        }
    }

    public static void DrawRectOutline(byte[] rgb, int imgW, int imgH, int x, int y, int w, int h, (byte R, byte G, byte B) color, int thickness)
    {
        DrawLine(rgb, imgW, imgH, x, y, x + w, y, color, thickness);
        DrawLine(rgb, imgW, imgH, x + w, y, x + w, y + h, color, thickness);
        DrawLine(rgb, imgW, imgH, x + w, y + h, x, y + h, color, thickness);
        DrawLine(rgb, imgW, imgH, x, y + h, x, y, color, thickness);
    }

    public static void DrawLine(byte[] rgb, int imgW, int imgH, int x0, int y0, int x1, int y1, (byte R, byte G, byte B) color, int thickness)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int radius = Math.Max(0, thickness / 2);

        while (true)
        {
            for (int oy = -radius; oy <= radius; oy++)
            {
                int py = y0 + oy;
                if (py < 0 || py >= imgH) continue;
                for (int ox = -radius; ox <= radius; ox++)
                {
                    int px = x0 + ox;
                    if (px < 0 || px >= imgW) continue;
                    int idx = (py * imgW + px) * 3;
                    rgb[idx] = color.R;
                    rgb[idx + 1] = color.G;
                    rgb[idx + 2] = color.B;
                }
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
