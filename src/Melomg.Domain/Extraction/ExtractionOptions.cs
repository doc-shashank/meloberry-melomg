using System.Globalization;
using System.Text.RegularExpressions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Extraction;

public sealed record ExtractionOptions(
    string Format,
    int Dpi,
    int PaddingPx,
    bool IncludeCaption = false,
    bool ObfuscateAllLabels = false,
    IReadOnlyList<string>? ObfuscateLabelPatterns = null,
    bool IgnoreSavedMasks = false,
    ObfuscationStyle ObfuscationStyle = ObfuscationStyle.Solid,
    string ObfuscationColorHex = FigureLabelMask.DefaultSolidColorHex,
    int Quality = ExtractionOptions.DefaultQuality,
    bool IncludeMetadata = false,
    string NamingMode = ExtractionOptions.DefaultNamingMode,
    bool EmitManifest = false)
{
    public const int DefaultDpi = 150;
    public const int DefaultPaddingPx = 8;
    public const int DefaultQuality = 90;
    public const string DefaultNamingMode = "number";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static Result<ExtractionOptions> Create(
        string? format = "png",
        int? dpi = null,
        int paddingPx = DefaultPaddingPx,
        bool includeCaption = false,
        bool obfuscateAllLabels = false,
        string? obfuscateLabelPatterns = null,
        bool ignoreSavedMasks = false,
        string? obfuscateStyle = null,
        string? obfuscateColorHex = null,
        string? quality = null,
        bool includeMetadata = false,
        string? namingMode = null,
        bool emitManifest = false)
    {
        var baseRes = ValidateFormatQualityAndDpi(format, dpi, quality);
        if (!baseRes.IsSuccess) return baseRes.Error!;
        var (normalizedFormat, effectiveDpi, resolvedQuality) = baseRes.Value;

        if (paddingPx < 0 || paddingPx > 100)
            return FigureErrors.InvalidPadding(paddingPx);

        var parsedStyle = ObfuscationStyle.Solid;
        if (!string.IsNullOrWhiteSpace(obfuscateStyle) && !FigureLabelMask.TryParseStyle(obfuscateStyle, out parsedStyle))
            return FigureErrors.InvalidObfuscationStyle(obfuscateStyle);

        string resolvedColor = FigureLabelMask.DefaultSolidColorHex;
        if (!string.IsNullOrWhiteSpace(obfuscateColorHex) && !FigureLabelMask.TryNormalizeColorHex(obfuscateColorHex, out resolvedColor))
            return FigureErrors.InvalidColorHex(obfuscateColorHex);

        string resolvedNaming = string.IsNullOrWhiteSpace(namingMode) ? DefaultNamingMode : namingMode.Trim().ToLowerInvariant();
        if (resolvedNaming is not ("number" or "id"))
            return FigureErrors.InvalidNamingMode(namingMode ?? string.Empty);

        return new ExtractionOptions(
            normalizedFormat, effectiveDpi, paddingPx, includeCaption, obfuscateAllLabels,
            ParsePatterns(obfuscateLabelPatterns), ignoreSavedMasks, parsedStyle, resolvedColor, resolvedQuality,
            includeMetadata, resolvedNaming, emitManifest);
    }

    internal static Result<(string Format, int Dpi, int Quality)> ValidateFormatQualityAndDpi(string? format, int? dpi, string? quality)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "png" : format.Trim().ToLowerInvariant();
        if (normalizedFormat == "jpeg") normalizedFormat = "jpg";
        if (normalizedFormat is not ("png" or "jpg"))
            return FigureErrors.InvalidFormat(format ?? string.Empty);

        if (!TryParseQualitySpec(quality, out int resolvedQuality, out int? presetDefaultDpi))
            return FigureErrors.InvalidQuality(quality ?? string.Empty);

        int effectiveDpi = dpi ?? presetDefaultDpi ?? DefaultDpi;
        if (effectiveDpi is < 72 or > 600)
            return FigureErrors.InvalidDpi(effectiveDpi);

        return (normalizedFormat, effectiveDpi, resolvedQuality);
    }

    public static bool TryParseQualitySpec(string? raw, out int quality, out int? presetDefaultDpi)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            quality = DefaultQuality;
            presetDefaultDpi = null;
            return true;
        }

        var trimmed = raw.Trim().ToLowerInvariant();
        (quality, presetDefaultDpi) = trimmed switch
        {
            "low" => (55, (int?)96),
            "medium" => (78, (int?)150),
            "high" => (92, (int?)240),
            "max" or "ultra" => (100, (int?)300),
            _ => (-1, (int?)null)
        };
        if (quality > 0) return true;

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) && numeric is >= 1 and <= 100)
        {
            quality = numeric;
            return true;
        }

        quality = DefaultQuality;
        return false;
    }

    public static IReadOnlyList<string> ParsePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var parts = new List<string>();
        int start = 0, braceDepth = 0, bracketDepth = 0, parenDepth = 0;
        bool escaped = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '[') bracketDepth++;
            else if (c == ']' && bracketDepth > 0) bracketDepth--;
            else if (bracketDepth == 0)
            {
                if (c == '{') braceDepth++;
                else if (c == '}' && braceDepth > 0) braceDepth--;
                else if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == ',' && braceDepth == 0 && parenDepth == 0)
                {
                    var item = raw[start..i].Trim();
                    if (item.Length > 0) parts.Add(item);
                    start = i + 1;
                }
            }
        }
        var tail = raw[start..].Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool MatchesAnyPattern(string text, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(text) || patterns.Count == 0) return false;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            bool hasRegexMeta = pattern.IndexOfAny(['^', '$', '(', ')', '[', ']', '{', '}', '\\', '+', '|']) >= 0;
            bool hasGlob = pattern.Contains('*') || pattern.Contains('?');

            if (hasGlob && !hasRegexMeta)
            {
                string globRegex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                try
                {
                    if (Regex.IsMatch(text, globRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                        return true;
                }
                catch (RegexMatchTimeoutException) { }
            }
            else if (hasRegexMeta)
            {
                try
                {
                    if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                        return true;
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { }
            }

            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
