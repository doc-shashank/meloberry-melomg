using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public sealed record PdfPageInfo(int PageNumber, double WidthPts, double HeightPts, int RotationDegrees = 0)
{
    public static Result<PdfPageInfo> Create(int pageNumber, double widthPts, double heightPts, int rotationDegrees = 0)
    {
        if (pageNumber < 1)
            return DocumentErrors.InvalidPageNumber(pageNumber, Math.Max(1, pageNumber));
        if (!double.IsFinite(widthPts) || !double.IsFinite(heightPts))
            return FigureErrors.InvalidBoundingBox("Page width and height must be finite numbers.");

        double rw = Math.Round(widthPts, 2), rh = Math.Round(heightPts, 2);
        if (rw <= 0 || rh <= 0)
            return FigureErrors.InvalidBoundingBox("Page width and height must be positive (at least 0.01 pts).");

        return new PdfPageInfo(pageNumber, rw, rh, ((rotationDegrees % 360) + 360) % 360);
    }
}
