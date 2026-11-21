using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Documents;

public enum ObfuscationStyle { Solid, Blur, Pixelate, Whiteout }

/// <summary>
/// Represents a label or rectangular region inside a figure to obfuscate during extraction.
/// Coordinates are stored in top-left PDF points relative to the owning page's visible bounds.
/// </summary>
public sealed record FigureLabelMask(
    int Index,
    BoundingBox BoundingBox,
    string? LabelText,
    ObfuscationStyle Style,
    string? ColorHex)
{
    public const string DefaultSolidColorHex = "#1E293B";

    public static Result<FigureLabelMask> Create(
        int index,
        BoundingBox boundingBox,
        PdfPageInfo page,
        string? labelText = null,
        ObfuscationStyle style = ObfuscationStyle.Solid,
        string? colorHex = null)
    {
        if (index < 1)
            return IndexErrors.OutOfIndexBounds(index, 1, int.MaxValue);

        var boundsCheck = boundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
        if (!boundsCheck.IsSuccess) return boundsCheck.Error!;

        string? normalizedColor = null;
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            if (!TryNormalizeColorHex(colorHex, out var validHex))
                return FigureErrors.InvalidColorHex(colorHex);
            normalizedColor = validHex;
        }
        else if (style == ObfuscationStyle.Solid)
        {
            normalizedColor = DefaultSolidColorHex;
        }

        return new FigureLabelMask(index, boundingBox, string.IsNullOrWhiteSpace(labelText) ? null : labelText.Trim(), style, normalizedColor);
    }

    public static bool TryParseStyle(string? raw, out ObfuscationStyle style)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            style = ObfuscationStyle.Solid;
            return false;
        }

        (bool ok, style) = raw.Trim().ToLowerInvariant() switch
        {
            "solid" or "box" or "redact" or "crosshatch" => (true, ObfuscationStyle.Solid),
            "blur" => (true, ObfuscationStyle.Blur),
            "pixelate" or "mosaic" => (true, ObfuscationStyle.Pixelate),
            "whiteout" or "white" or "erase" => (true, ObfuscationStyle.Whiteout),
            _ => (false, ObfuscationStyle.Solid)
        };
        return ok;
    }

    public static string FormatStyle(ObfuscationStyle style) => style switch
    {
        ObfuscationStyle.Blur => "blur",
        ObfuscationStyle.Pixelate => "pixelate",
        ObfuscationStyle.Whiteout => "whiteout",
        _ => "solid"
    };

    public static bool TryNormalizeColorHex(string? raw, out string normalizedHex)
    {
        normalizedHex = DefaultSolidColorHex;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('#')) trimmed = trimmed[1..];
        if (trimmed.Length != 6 || !trimmed.All(Uri.IsHexDigit)) return false;

        normalizedHex = "#" + trimmed.ToUpperInvariant();
        return true;
    }

    public static (byte R, byte G, byte B) ParseRgbOrDefault(string? colorHex)
    {
        string hex = TryNormalizeColorHex(colorHex, out var valid) ? valid : DefaultSolidColorHex;
        return (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    }

    public (byte R, byte G, byte B) GetRgbOrDefault() => ParseRgbOrDefault(ColorHex);
}
