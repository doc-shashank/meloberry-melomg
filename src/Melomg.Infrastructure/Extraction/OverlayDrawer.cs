using Melomg.Domain.Documents;

namespace Melomg.Infrastructure.Extraction;

/// <summary>
/// Draws visual inspection bounding-box overlays onto full rendered PDF page bitmaps.
/// </summary>
internal static class OverlayDrawer
{
    private static readonly (byte R, byte G, byte B) FigureBoxColor = (37, 99, 235);
    private static readonly (byte R, byte G, byte B) CaptionBoxColor = (5, 150, 105);
    private static readonly (byte R, byte G, byte B) MaskBoxColor = (220, 38, 38);
    private static readonly (byte R, byte G, byte B) WhiteColor = (255, 255, 255);

    private static readonly Dictionary<char, byte[]> Font5X7 = new()
    {
        ['#'] = [0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010],
        ['0'] = [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        ['1'] = [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        ['2'] = [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        ['3'] = [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        ['4'] = [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        ['5'] = [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        ['6'] = [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        ['7'] = [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        ['8'] = [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        ['9'] = [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        ['F'] = [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        ['M'] = [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        [' '] = [0, 0, 0, 0, 0, 0, 0]
    };

    public static void DrawPageOverlay(
        byte[] rgb,
        int imgW,
        int imgH,
        PdfPageInfo page,
        IReadOnlyList<DocumentFigure> pageFigures)
    {
        double scaleX = imgW / Math.Max(1.0, page.WidthPts);
        double scaleY = imgH / Math.Max(1.0, page.HeightPts);
        int badgeScale = imgW >= 1200 ? 2 : 1;

        foreach (var fig in pageFigures)
        {
            var (fx, fy, fw, fh) = ToPixelBox(fig.BoundingBox, scaleX, scaleY, imgW, imgH);
            RgbCanvasPrimitives.DrawRectOutline(rgb, imgW, imgH, fx, fy, fw, fh, FigureBoxColor, 3);

            string badgeText = $"#{fig.Index}";
            int textPxW = badgeText.Length * 6 * badgeScale;
            int badgeW = Math.Min(imgW - fx, textPxW + 8 * badgeScale);
            int badgeH = Math.Min(imgH, 11 * badgeScale);
            int badgeY = fy >= badgeH ? fy - badgeH : fy;
            RgbCanvasPrimitives.FillRect(rgb, imgW, imgH, fx, badgeY, badgeW, badgeH, FigureBoxColor);
            DrawBitmapText(rgb, imgW, imgH, fx + 4 * badgeScale, badgeY + 2 * badgeScale, badgeText, WhiteColor, badgeScale);

            if (fig.CaptionBoundingBox is not null)
            {
                var (cx, cy, cw, ch) = ToPixelBox(fig.CaptionBoundingBox, scaleX, scaleY, imgW, imgH);
                DrawDashedRect(rgb, imgW, imgH, cx, cy, cw, ch, CaptionBoxColor, 2, dashLen: 8, gapLen: 5);
            }

            foreach (var mask in fig.LabelMasks)
            {
                var (mx, my, mw, mh) = ToPixelBox(mask.BoundingBox, scaleX, scaleY, imgW, imgH);
                RgbCanvasPrimitives.DrawRectOutline(rgb, imgW, imgH, mx, my, mw, mh, MaskBoxColor, 2);
                DrawDiagonalHatch(rgb, imgW, imgH, mx, my, mw, mh, MaskBoxColor, spacing: 8);
            }
        }
    }

    private static (int X, int Y, int W, int H) ToPixelBox(BoundingBox box, double scaleX, double scaleY, int imgW, int imgH)
    {
        int x = Math.Clamp((int)Math.Floor(box.X * scaleX), 0, Math.Max(0, imgW - 2));
        int y = Math.Clamp((int)Math.Floor(box.Y * scaleY), 0, Math.Max(0, imgH - 2));
        int right = Math.Clamp((int)Math.Ceiling(box.Right * scaleX), x + 1, imgW - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom * scaleY), y + 1, imgH - 1);
        return (x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static void DrawDashedRect(
        byte[] rgb,
        int imgW,
        int imgH,
        int x,
        int y,
        int w,
        int h,
        (byte R, byte G, byte B) color,
        int thickness,
        int dashLen,
        int gapLen)
    {
        DrawDashedSegment(rgb, imgW, imgH, x, y, x + w, y, color, thickness, dashLen, gapLen);
        DrawDashedSegment(rgb, imgW, imgH, x + w, y, x + w, y + h, color, thickness, dashLen, gapLen);
        DrawDashedSegment(rgb, imgW, imgH, x + w, y + h, x, y + h, color, thickness, dashLen, gapLen);
        DrawDashedSegment(rgb, imgW, imgH, x, y + h, x, y, color, thickness, dashLen, gapLen);
    }

    private static void DrawDashedSegment(
        byte[] rgb,
        int imgW,
        int imgH,
        int x0,
        int y0,
        int x1,
        int y1,
        (byte R, byte G, byte B) color,
        int thickness,
        int dashLen,
        int gapLen)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double totalLen = Math.Sqrt(dx * dx + dy * dy);
        if (totalLen < 1) return;

        int period = Math.Max(2, dashLen + gapLen);
        for (double d = 0; d < totalLen; d += period)
        {
            double t0 = d / totalLen;
            double t1 = Math.Min(totalLen, d + dashLen) / totalLen;
            int sx = (int)Math.Round(x0 + (x1 - x0) * t0);
            int sy = (int)Math.Round(y0 + (y1 - y0) * t0);
            int ex = (int)Math.Round(x0 + (x1 - x0) * t1);
            int ey = (int)Math.Round(y0 + (y1 - y0) * t1);
            RgbCanvasPrimitives.DrawLine(rgb, imgW, imgH, sx, sy, ex, ey, color, thickness);
        }
    }

    private static void DrawDiagonalHatch(
        byte[] rgb,
        int imgW,
        int imgH,
        int rx,
        int ry,
        int rw,
        int rh,
        (byte R, byte G, byte B) color,
        int spacing)
    {
        int xEnd = Math.Min(imgW - 1, rx + rw);
        int yEnd = Math.Min(imgH - 1, ry + rh);
        for (int py = Math.Max(0, ry); py <= yEnd; py++)
        {
            for (int px = Math.Max(0, rx); px <= xEnd; px++)
            {
                if ((px + py) % spacing != 0) continue;
                int idx = (py * imgW + px) * 3;
                rgb[idx] = (byte)((rgb[idx] + color.R) / 2);
                rgb[idx + 1] = (byte)((rgb[idx + 1] + color.G) / 2);
                rgb[idx + 2] = (byte)((rgb[idx + 2] + color.B) / 2);
            }
        }
    }

    private static void DrawBitmapText(
        byte[] rgb,
        int imgW,
        int imgH,
        int startX,
        int startY,
        string text,
        (byte R, byte G, byte B) color,
        int scale)
    {
        int cursorX = startX;
        foreach (char ch in text)
        {
            if (Font5X7.TryGetValue(ch, out var rows))
            {
                for (int r = 0; r < 7; r++)
                {
                    byte bits = rows[r];
                    for (int c = 0; c < 5; c++)
                    {
                        if ((bits & (1 << (4 - c))) == 0) continue;
                        RgbCanvasPrimitives.FillRect(
                            rgb,
                            imgW,
                            imgH,
                            cursorX + c * scale,
                            startY + r * scale,
                            scale,
                            scale,
                            color);
                    }
                }
            }
            cursorX += 6 * scale;
        }
    }
}
