using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public sealed record PixelRect(
    int PixelX,
    int PixelY,
    int PixelWidth,
    int PixelHeight,
    int PagePixelWidth,
    int PagePixelHeight);

public sealed record NormalizedBoundingBox(double X, double Y, double Width, double Height);

/// <summary>
/// Axis-aligned bounding box in top-left PDF points (72 pts = 1 inch).
/// </summary>
public sealed record BoundingBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;

    public static Result<BoundingBox> Create(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
            return FigureErrors.InvalidBoundingBox("Coordinates must be finite numbers.");

        if (x < 0 || y < 0)
            return FigureErrors.InvalidBoundingBox("Origin coordinates (x, y) must be non-negative.");

        if (width <= 0 || height <= 0)
            return FigureErrors.InvalidBoundingBox("Width and height must be positive.");

        return new BoundingBox(Math.Round(x, 2), Math.Round(y, 2), Math.Round(width, 2), Math.Round(height, 2));
    }

    public Result ValidateWithinPage(double pageWidthPts, double pageHeightPts)
    {
        const double eps = 0.5;
        return X < -eps || Y < -eps || Right > pageWidthPts + eps || Bottom > pageHeightPts + eps
            ? FigureErrors.BoundingBoxOutOfPageBounds(X, Y, Width, Height, pageWidthPts, pageHeightPts)
            : Result.Success();
    }

    public double ComputeIoU(BoundingBox other)
    {
        double inter = ComputeIntersectionArea(other);
        double union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public double ComputeContainment(BoundingBox outer) =>
        Area <= 0 ? 0 : ComputeIntersectionArea(outer) / Area;

    private double ComputeIntersectionArea(BoundingBox other)
    {
        double interW = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X));
        double interH = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y));
        return interW * interH;
    }

    public BoundingBox ClampToPage(
        double pageWidthPts,
        double pageHeightPts,
        double minWidth = 1.0,
        double minHeight = 1.0,
        double marginRight = 2.0,
        double marginBottom = 2.0)
    {
        double x = Math.Clamp(X, 0, Math.Max(0, pageWidthPts - marginRight));
        double y = Math.Clamp(Y, 0, Math.Max(0, pageHeightPts - marginBottom));
        double w = Math.Clamp(Width, minWidth, Math.Max(minWidth, pageWidthPts - x));
        double h = Math.Clamp(Height, minHeight, Math.Max(minHeight, pageHeightPts - y));
        return new BoundingBox(Math.Round(x, 2), Math.Round(y, 2), Math.Round(w, 2), Math.Round(h, 2));
    }

    public NormalizedBoundingBox ToNormalized(double pageWidthPts, double pageHeightPts) =>
        new(
            Math.Clamp(X / pageWidthPts, 0, 1),
            Math.Clamp(Y / pageHeightPts, 0, 1),
            Math.Clamp(Width / pageWidthPts, 0, 1),
            Math.Clamp(Height / pageHeightPts, 0, 1));

    public PixelRect ToPixelRect(int dpi, int paddingPx, double pageWidthPts, double pageHeightPts)
    {
        double scale = dpi / 72.0;
        int pagePxW = Math.Max(1, (int)Math.Ceiling(pageWidthPts * scale));
        int pagePxH = Math.Max(1, (int)Math.Ceiling(pageHeightPts * scale));

        int cx = Math.Clamp((int)Math.Floor(X * scale) - paddingPx, 0, Math.Max(0, pagePxW - 1));
        int cy = Math.Clamp((int)Math.Floor(Y * scale) - paddingPx, 0, Math.Max(0, pagePxH - 1));
        int cr = Math.Clamp((int)Math.Ceiling(Right * scale) + paddingPx, cx + 1, pagePxW);
        int cb = Math.Clamp((int)Math.Ceiling(Bottom * scale) + paddingPx, cy + 1, pagePxH);
        return new PixelRect(cx, cy, Math.Max(1, cr - cx), Math.Max(1, cb - cy), pagePxW, pagePxH);
    }
}
