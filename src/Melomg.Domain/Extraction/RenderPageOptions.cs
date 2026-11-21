using Melomg.Domain.Common;

namespace Melomg.Domain.Extraction;

public sealed record RenderPageOptions(string Format, int Dpi, bool Overlay, int Quality = ExtractionOptions.DefaultQuality)
{
    public static Result<RenderPageOptions> Create(string? format = "png", int? dpi = null, bool overlay = false, string? quality = null)
    {
        var baseRes = ExtractionOptions.ValidateFormatQualityAndDpi(format, dpi, quality);
        if (!baseRes.IsSuccess) return baseRes.Error!;
        var (normalizedFormat, effectiveDpi, resolvedQuality) = baseRes.Value;
        return new RenderPageOptions(normalizedFormat, effectiveDpi, overlay, resolvedQuality);
    }
}
