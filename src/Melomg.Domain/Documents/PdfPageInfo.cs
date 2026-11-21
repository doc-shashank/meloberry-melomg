using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public sealed record PdfPageInfo(int PageNumber, double WidthPts, double HeightPts, int RotationDegrees = 0)
{
    public static Result<PdfPageInfo> Create(int pageNumber, double widthPts, double heightPts, int rotationDegrees = 0)
    {
        if (pageNumber < 1)
            return DocumentErrors.InvalidPageNumber(pageNumber, Math.Max(1, pageNumber));
        if (widthPts <= 0 || heightPts <= 0)
            return FigureErrors.InvalidBoundingBox("Page width and height must be positive.");

        return new PdfPageInfo(pageNumber, Math.Round(widthPts, 2), Math.Round(heightPts, 2), ((rotationDegrees % 360) + 360) % 360);
    }
}
