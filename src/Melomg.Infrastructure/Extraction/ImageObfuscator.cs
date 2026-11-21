using Melomg.Domain.Documents;

namespace Melomg.Infrastructure.Extraction;

/// <summary>
/// Pixel-level label obfuscation engine operating directly on rendered RGB crop buffers.
/// </summary>
internal static class ImageObfuscator
{
    public static void ApplyMask(
        byte[] rgb,
        int imgW,
        int imgH,
        int x,
        int y,
        int w,
        int h,
        ObfuscationStyle style,
        string? colorHex)
    {
        int rx = Math.Clamp(x, 0, Math.Max(0, imgW - 1));
        int ry = Math.Clamp(y, 0, Math.Max(0, imgH - 1));
        int rw = Math.Clamp(w, 1, imgW - rx);
        int rh = Math.Clamp(h, 1, imgH - ry);

        switch (style)
        {
            case ObfuscationStyle.Whiteout:
                ApplyWhiteout(rgb, imgW, imgH, rx, ry, rw, rh);
                break;
            case ObfuscationStyle.Blur:
                ApplyBoxBlur3Pass(rgb, imgW, rx, ry, rw, rh);
                break;
            case ObfuscationStyle.Pixelate:
                ApplyPixelate(rgb, imgW, rx, ry, rw, rh);
                break;
            default:
                ApplySolid(rgb, imgW, imgH, rx, ry, rw, rh, FigureLabelMask.ParseRgbOrDefault(colorHex));
                break;
        }
    }

    private static void ApplySolid(byte[] rgb, int imgW, int imgH, int rx, int ry, int rw, int rh, (byte R, byte G, byte B) fill)
    {
        RgbCanvasPrimitives.FillRect(rgb, imgW, imgH, rx, ry, rw, rh, fill);
        var border = ((byte)(fill.R * 0.75), (byte)(fill.G * 0.75), (byte)(fill.B * 0.75));
        RgbCanvasPrimitives.DrawRectOutline(rgb, imgW, imgH, rx, ry, Math.Max(1, rw - 1), Math.Max(1, rh - 1), border, 1);
    }

    private static void ApplyWhiteout(byte[] rgb, int imgW, int imgH, int rx, int ry, int rw, int rh)
    {
        var rSamples = new List<byte>();
        var gSamples = new List<byte>();
        var bSamples = new List<byte>();

        void AddPixel(int px, int py)
        {
            int idx = (py * imgW + px) * 3;
            rSamples.Add(rgb[idx]);
            gSamples.Add(rgb[idx + 1]);
            bSamples.Add(rgb[idx + 2]);
        }

        int sx0 = Math.Max(0, rx - 2), sx1 = Math.Min(imgW - 1, rx + rw + 1);
        int sy0 = Math.Max(0, ry - 2), sy1 = Math.Min(imgH - 1, ry + rh + 1);
        foreach (int py in (ReadOnlySpan<int>)[ry - 2, ry - 1, ry + rh, ry + rh + 1])
            if (py >= 0 && py < imgH)
                for (int px = sx0; px <= sx1; px++) AddPixel(px, py);
        foreach (int px in (ReadOnlySpan<int>)[rx - 2, rx - 1, rx + rw, rx + rw + 1])
            if (px >= 0 && px < imgW)
                for (int py = sy0; py <= sy1; py++) AddPixel(px, py);

        (byte R, byte G, byte B) bg = (255, 255, 255);
        if (rSamples.Count > 0)
        {
            rSamples.Sort(); gSamples.Sort(); bSamples.Sort();
            int mid = rSamples.Count / 2;
            bg = (rSamples[mid], gSamples[mid], bSamples[mid]);
        }

        RgbCanvasPrimitives.FillRect(rgb, imgW, imgH, rx, ry, rw, rh, bg);
    }

    private static void ApplyBoxBlur3Pass(byte[] rgb, int imgW, int rx, int ry, int rw, int rh)
    {
        int radius = Math.Clamp(Math.Min(rw, rh) / 4, 4, 24);
        var bufA = new byte[rw * rh * 3];
        var bufB = new byte[rw * rh * 3];

        for (int row = 0; row < rh; row++)
            Buffer.BlockCopy(rgb, ((ry + row) * imgW + rx) * 3, bufA, row * rw * 3, rw * 3);

        for (int pass = 0; pass < 3; pass++)
        {
            // Horizontal pass: bufA -> bufB
            for (int y = 0; y < rh; y++)
            {
                int rowOff = y * rw * 3;
                for (int x = 0; x < rw; x++)
                {
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(rw - 1, x + radius);
                    int sumR = 0, sumG = 0, sumB = 0, count = x1 - x0 + 1;
                    for (int kx = x0; kx <= x1; kx++)
                    {
                        int idx = rowOff + kx * 3;
                        sumR += bufA[idx];
                        sumG += bufA[idx + 1];
                        sumB += bufA[idx + 2];
                    }
                    int dstIdx = rowOff + x * 3;
                    bufB[dstIdx] = (byte)(sumR / count);
                    bufB[dstIdx + 1] = (byte)(sumG / count);
                    bufB[dstIdx + 2] = (byte)(sumB / count);
                }
            }

            // Vertical pass: bufB -> bufA
            for (int y = 0; y < rh; y++)
            {
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(rh - 1, y + radius);
                int count = y1 - y0 + 1;
                for (int x = 0; x < rw; x++)
                {
                    int sumR = 0, sumG = 0, sumB = 0;
                    for (int ky = y0; ky <= y1; ky++)
                    {
                        int idx = (ky * rw + x) * 3;
                        sumR += bufB[idx];
                        sumG += bufB[idx + 1];
                        sumB += bufB[idx + 2];
                    }
                    int dstIdx = (y * rw + x) * 3;
                    bufA[dstIdx] = (byte)(sumR / count);
                    bufA[dstIdx + 1] = (byte)(sumG / count);
                    bufA[dstIdx + 2] = (byte)(sumB / count);
                }
            }
        }

        for (int row = 0; row < rh; row++)
            Buffer.BlockCopy(bufA, row * rw * 3, rgb, ((ry + row) * imgW + rx) * 3, rw * 3);
    }

    private static void ApplyPixelate(byte[] rgb, int imgW, int rx, int ry, int rw, int rh)
    {
        int blockSize = Math.Clamp(Math.Min(rw, rh) / 4, 6, 20);
        for (int by = 0; by < rh; by += blockSize)
        {
            int bh = Math.Min(blockSize, rh - by);
            for (int bx = 0; bx < rw; bx += blockSize)
            {
                int bw = Math.Min(blockSize, rw - bx);
                long sumR = 0, sumG = 0, sumB = 0;
                int count = bw * bh;

                for (int y = 0; y < bh; y++)
                {
                    int py = ry + by + y;
                    for (int x = 0; x < bw; x++)
                    {
                        int px = rx + bx + x;
                        int idx = (py * imgW + px) * 3;
                        sumR += rgb[idx];
                        sumG += rgb[idx + 1];
                        sumB += rgb[idx + 2];
                    }
                }

                byte avgR = (byte)(sumR / Math.Max(1, count));
                byte avgG = (byte)(sumG / Math.Max(1, count));
                byte avgB = (byte)(sumB / Math.Max(1, count));

                for (int y = 0; y < bh; y++)
                {
                    int py = ry + by + y;
                    for (int x = 0; x < bw; x++)
                    {
                        int px = rx + bx + x;
                        int idx = (py * imgW + px) * 3;
                        rgb[idx] = avgR;
                        rgb[idx + 1] = avgG;
                        rgb[idx + 2] = avgB;
                    }
                }
            }
        }
    }
}
