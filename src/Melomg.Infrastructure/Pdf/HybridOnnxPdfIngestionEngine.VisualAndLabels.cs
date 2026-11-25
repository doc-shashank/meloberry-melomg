using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;

namespace Melomg.Infrastructure.Pdf;

public sealed partial class HybridOnnxPdfIngestionEngine
{
    public async Task<Result<IReadOnlyList<DetectedTextLabel>>> DetectLabelsInRegionAsync(
        string pdfPath,
        int pageNumber,
        BoundingBox regionPts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            return DocumentErrors.PdfFileNotFound(string.Empty);

        string fullPath = Path.GetFullPath(pdfPath.Trim());
        var cachedResult = await GetOrBuildCachedDocumentAsync(fullPath, preloadedBytes: null, cancellationToken);
        if (!cachedResult.IsSuccess)
            return cachedResult.Error!;

        var cachedDoc = cachedResult.Value;
        var docIndex = cachedDoc.Index;
        var sceneMap = cachedDoc.Scenes;
        if (pageNumber < 1 || pageNumber > docIndex.OrderedPageIds.Count)
            return DocumentErrors.InvalidPageNumber(pageNumber, docIndex.OrderedPageIds.Count);

        if (!sceneMap.TryGetValue(pageNumber, out var scene) || scene is null)
        {
            var sceneRes = ParseSinglePageScene(cachedDoc.PdfBytes, docIndex, docIndex.OrderedPageIds[pageNumber - 1], pageNumber, fullPath);
            if (!sceneRes.IsSuccess)
                return sceneRes.Error!;
            scene = sceneRes.Value;
            sceneMap[pageNumber] = scene;
        }
        double pageW = scene.PageInfo.WidthPts;
        double pageH = scene.PageInfo.HeightPts;

        // Keep separate diagram labels unmerged across wide horizontal gaps
        var mergedLabels = MergeTextSpansOnSameLine(scene.TextSpans, maxGapPts: 14.0);
        var labels = new List<DetectedTextLabel>();

        foreach (var span in mergedLabels)
        {
            var text = span.Text.Trim();
            if (text.Length == 0)
                continue;

            bool hasAlphaNum = text.Any(char.IsLetterOrDigit);
            if (!hasAlphaNum && text.Length <= 2)
                continue;

            double cx = span.X + span.Width * 0.5;
            double cy = span.Y + span.EffectiveHeight * 0.5;
            if (cx < regionPts.X - 2 || cx > regionPts.Right + 2 ||
                cy < regionPts.Y - 2 || cy > regionPts.Bottom + 2)
            {
                continue;
            }

            double bx = Math.Clamp(span.X, 0, Math.Max(0, pageW - 1));
            double by = Math.Clamp(span.Y, 0, Math.Max(0, pageH - 1));
            double bw = Math.Clamp(span.Width, 1, pageW - bx);
            double bh = Math.Clamp(span.EffectiveHeight, 1, pageH - by);

            labels.Add(new DetectedTextLabel(
                new BoundingBox(Math.Round(bx, 2), Math.Round(by, 2), Math.Round(bw, 2), Math.Round(bh, 2)),
                text,
                Math.Round(span.FontSize, 2)));
        }

        var ordered = labels
            .OrderBy(l => Math.Round(l.BoundingBox.Y / 6.0))
            .ThenBy(l => l.BoundingBox.X)
            .ToList();

        return Result<IReadOnlyList<DetectedTextLabel>>.Success(ordered);
    }

    // =========================================================================
    // /CropBox Inheritance, /Form XObjects & /ToUnicode CMap Text Decoding
    // =========================================================================

    internal sealed record ParsedPageResources(
        Dictionary<string, ParsedPdfImageXObject> Images,
        Dictionary<string, ParsedPdfFormXObject> Forms,
        Dictionary<string, PdfToUnicodeMap> ToUnicodeFonts);

    internal sealed class PdfToUnicodeMap
    {
        private static readonly int[] DefaultStrides = [4, 2];

        public Dictionary<uint, string> CharMap { get; } = [];
        public HashSet<int> SourceByteLengths { get; } = [];

        public bool TryDecodeHex(string hex, out string text)
        {
            text = string.Empty;
            if (CharMap.Count == 0 || string.IsNullOrWhiteSpace(hex))
                return false;

            string s = string.Concat(hex.Where(Uri.IsHexDigit));
            if (s.Length < 2)
                return false;

            var sb = new StringBuilder();
            bool TryAppendWithStride(int stride)
            {
                if (s.Length % stride != 0) return false;
                sb.Clear();
                bool anyMapped = false;
                for (int i = 0; i < s.Length; i += stride)
                {
                    if (!uint.TryParse(s.AsSpan(i, stride), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code))
                        continue;
                    if (CharMap.TryGetValue(code, out var mapped))
                    {
                        sb.Append(mapped);
                        anyMapped = true;
                    }
                    else if (code >= 0x20 && code <= 0x7E)
                    {
                        sb.Append((char)code);
                    }
                }
                return anyMapped;
            }

            IEnumerable<int> strides = SourceByteLengths.Count > 0
                ? SourceByteLengths.OrderByDescending(b => b).Select(b => b * 2)
                : DefaultStrides;

            bool mappedAny = false;
            foreach (int stride in strides)
            {
                if (TryAppendWithStride(stride))
                {
                    mappedAny = true;
                    break;
                }
            }

            text = sb.ToString();
            return mappedAny && text.Length > 0;
        }

        public string MapLiteralBytes(string raw)
        {
            if (CharMap.Count == 0 || string.IsNullOrEmpty(raw))
                return raw;

            var sb = new StringBuilder(raw.Length);
            int hits = 0;
            foreach (char ch in raw)
            {
                uint code = ch;
                if (CharMap.TryGetValue(code, out var mapped))
                {
                    sb.Append(mapped);
                    hits++;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return hits > 0 ? sb.ToString() : raw;
        }
    }

    private static T? WalkPageParentChain<T>(
        int startObjId,
        Dictionary<int, PdfObjectEntry> objects,
        Func<string, T?> selector) where T : struct
    {
        int currId = startObjId;
        var visited = new HashSet<int>();
        while (visited.Add(currId) && objects.TryGetValue(currId, out var obj))
        {
            if (selector(obj.DictionaryText) is { } found)
                return found;

            var parentMatch = ParentRefRegex().Match(obj.DictionaryText);
            if (!parentMatch.Success || !int.TryParse(parentMatch.Groups[1].Value, out currId))
                break;
        }
        return null;
    }

    private static T? WalkPageParentChainClass<T>(
        int startObjId,
        Dictionary<int, PdfObjectEntry> objects,
        Func<string, T?> selector) where T : class
    {
        int currId = startObjId;
        var visited = new HashSet<int>();
        while (visited.Add(currId) && objects.TryGetValue(currId, out var obj))
        {
            if (selector(obj.DictionaryText) is { } found)
                return found;

            var parentMatch = ParentRefRegex().Match(obj.DictionaryText);
            if (!parentMatch.Success || !int.TryParse(parentMatch.Groups[1].Value, out currId))
                break;
        }
        return null;
    }

    private static double[] ResolvePageVisibleBox(
        int pageObjId,
        Dictionary<int, PdfObjectEntry> objects,
        double[] defaultMediaBox) =>
        WalkPageParentChainClass(pageObjId, objects, static d => TryParseBoxRegex(d, CropBoxRegex(), out var c) ? c : null)
        ?? WalkPageParentChainClass(pageObjId, objects, static d => TryParseBoxRegex(d, MediaBoxRegex(), out var m) ? m : null)
        ?? defaultMediaBox;

    private static bool TryParseBoxRegex(string dict, Regex regex, out double[] box)
    {
        box = [0, 0, 612, 792];
        var m = regex.Match(dict);
        if (!m.Success)
            return false;

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(m.Groups[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out box[i]))
                return false;
        }
        return true;
    }

    private static ParsedPageResources ResolvePageResources(
        byte[] pdfBytes,
        int pageObjId,
        Dictionary<int, PdfObjectEntry> objects)
    {
        var chain = new List<string>();
        int currId = pageObjId;
        var visited = new HashSet<int>();
        while (visited.Add(currId) && objects.TryGetValue(currId, out var obj))
        {
            chain.Add(obj.DictionaryText);
            var parentMatch = ParentRefRegex().Match(obj.DictionaryText);
            if (!parentMatch.Success || !int.TryParse(parentMatch.Groups[1].Value, out currId))
                break;
        }

        var images = new Dictionary<string, ParsedPdfImageXObject>(StringComparer.Ordinal);
        var forms = new Dictionary<string, ParsedPdfFormXObject>(StringComparer.Ordinal);
        var fonts = new Dictionary<string, PdfToUnicodeMap>(StringComparer.Ordinal);

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            string? resText = ResolveSubDictionary(chain[i], "/Resources", objects);
            if (!string.IsNullOrWhiteSpace(resText))
                PopulateResourcesFromDict(pdfBytes, resText, objects, images, forms, fonts);
        }

        if (images.Count == 0 && forms.Count == 0 && fonts.Count == 0 && chain.Count > 0)
            PopulateResourcesFromDict(pdfBytes, chain[0], objects, images, forms, fonts);

        return new ParsedPageResources(images, forms, fonts);
    }

    private static ParsedPageResources MergeFormResources(
        byte[] pdfBytes,
        string formDict,
        ParsedPageResources parent,
        Dictionary<int, PdfObjectEntry> objects)
    {
        string? resText = ResolveSubDictionary(formDict, "/Resources", objects);
        if (string.IsNullOrWhiteSpace(resText))
            return parent;

        var images = new Dictionary<string, ParsedPdfImageXObject>(parent.Images, StringComparer.Ordinal);
        var forms = new Dictionary<string, ParsedPdfFormXObject>(parent.Forms, StringComparer.Ordinal);
        var fonts = new Dictionary<string, PdfToUnicodeMap>(parent.ToUnicodeFonts, StringComparer.Ordinal);

        PopulateResourcesFromDict(pdfBytes, resText, objects, images, forms, fonts);
        return new ParsedPageResources(images, forms, fonts);
    }

    private static void PopulateResourcesFromDict(
        byte[] pdfBytes,
        string resourceText,
        Dictionary<int, PdfObjectEntry> objects,
        Dictionary<string, ParsedPdfImageXObject> images,
        Dictionary<string, ParsedPdfFormXObject> forms,
        Dictionary<string, PdfToUnicodeMap> fonts)
    {
        string? xObjText = ResolveSubDictionary(resourceText, "/XObject", objects);
        if (!string.IsNullOrWhiteSpace(xObjText))
        {
            foreach (Match m in NamedRefRegex().Matches(xObjText))
            {
                string resName = "/" + m.Groups[1].Value;
                if (!int.TryParse(m.Groups[2].Value, out int objId)) continue;
                if (!objects.TryGetValue(objId, out var xObj)) continue;

                if (SubtypeImageRegex().IsMatch(xObj.DictionaryText))
                {
                    int pxW = TryParseIntEntry(xObj.DictionaryText, "/Width", out int w) ? w : 64;
                    int pxH = TryParseIntEntry(xObj.DictionaryText, "/Height", out int h) ? h : 64;

                    images[resName] = new ParsedPdfImageXObject(resName, objId, pxW, pxH);
                }
                else if (SubtypeFormRegex().IsMatch(xObj.DictionaryText) && xObj.StreamOffset >= 0)
                {
                    double[] matrix = [1, 0, 0, 1, 0, 0];
                    var matMatch = MatrixRegex().Match(xObj.DictionaryText);
                    if (matMatch.Success)
                    {
                        var parsed = new double[6];
                        bool ok = true;
                        for (int k = 0; k < 6; k++)
                        {
                            if (!double.TryParse(matMatch.Groups[k + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[k]))
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (ok) matrix = parsed;
                    }

                    forms[resName] = new ParsedPdfFormXObject(
                        resName,
                        objId,
                        matrix,
                        xObj.DictionaryText,
                        xObj.StreamOffset,
                        xObj.StreamLength);
                }
            }
        }

        string? fontText = ResolveSubDictionary(resourceText, "/Font", objects);
        if (!string.IsNullOrWhiteSpace(fontText))
        {
            foreach (Match m in NamedRefRegex().Matches(fontText))
            {
                string fontName = "/" + m.Groups[1].Value;
                if (!int.TryParse(m.Groups[2].Value, out int fontObjId)) continue;
                if (!objects.TryGetValue(fontObjId, out var fontObj)) continue;

                var toUnicodeMatch = ToUnicodeRefRegex().Match(fontObj.DictionaryText);
                if (!toUnicodeMatch.Success) continue;
                if (!int.TryParse(toUnicodeMatch.Groups[1].Value, out int cmapObjId)) continue;
                if (!objects.TryGetValue(cmapObjId, out var cmapObj) || cmapObj.StreamOffset < 0 || cmapObj.StreamLength <= 0) continue;

                var decodedCmap = DecodeStreamPayload(
                    cmapObj.DictionaryText,
                    new ReadOnlySpan<byte>(pdfBytes, cmapObj.StreamOffset, cmapObj.StreamLength));
                var map = ParseToUnicodeCMap(Encoding.Latin1.GetString(decodedCmap));
                if (map.CharMap.Count > 0)
                    fonts[fontName] = map;
            }
        }
    }

    private static PdfToUnicodeMap ParseToUnicodeCMap(string cmapText)
    {
        var map = new PdfToUnicodeMap();

        static int CountHexDigits(string raw)
        {
            int count = 0;
            foreach (char ch in raw)
                if (Uri.IsHexDigit(ch)) count++;
            return count;
        }

        foreach (Match blk in CodeSpaceRangeBlockRegex().Matches(cmapText))
        {
            foreach (Match pair in HexPairRegex().Matches(blk.Groups[1].Value))
            {
                int hexLen = Math.Max(CountHexDigits(pair.Groups[1].Value), CountHexDigits(pair.Groups[2].Value));
                int byteLen = hexLen / 2;
                if (byteLen is >= 1 and <= 4)
                    map.SourceByteLengths.Add(byteLen);
            }
        }

        foreach (Match blk in BfCharBlockRegex().Matches(cmapText))
        {
            var body = blk.Groups[1].Value;
            foreach (Match pair in HexPairRegex().Matches(body))
            {
                if (uint.TryParse(pair.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint src))
                {
                    int byteLen = CountHexDigits(pair.Groups[1].Value) / 2;
                    if (byteLen is >= 1 and <= 4)
                        map.SourceByteLengths.Add(byteLen);

                    var dst = DecodeUtf16BeHex(pair.Groups[2].Value);
                    if (!string.IsNullOrEmpty(dst))
                        map.CharMap[src] = dst;
                }
            }
        }

        foreach (Match blk in BfRangeBlockRegex().Matches(cmapText))
        {
            foreach (Match m in BfRangeEntryRegex().Matches(blk.Groups[1].Value))
            {
                if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint start) ||
                    !uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint end) ||
                    end < start)
                {
                    continue;
                }

                int byteLen = Math.Max(CountHexDigits(m.Groups[1].Value), CountHexDigits(m.Groups[2].Value)) / 2;
                if (byteLen is >= 1 and <= 4)
                    map.SourceByteLengths.Add(byteLen);

                if (m.Groups["arr"].Success)
                {
                    var entries = SingleHexTokenRegex().Matches(m.Groups["arr"].Value);
                    uint code = start;
                    foreach (Match entry in entries)
                    {
                        if (code > end) break;
                        var text = DecodeUtf16BeHex(entry.Groups[1].Value);
                        if (!string.IsNullOrEmpty(text))
                            map.CharMap[code] = text;
                        code++;
                    }
                }
                else if (m.Groups["dst"].Success && end - start <= 512)
                {
                    string baseText = DecodeUtf16BeHex(m.Groups["dst"].Value);
                    if (baseText.Length > 0 &&
                        (!char.IsSurrogate(baseText[0]) || (baseText.Length >= 2 && char.IsSurrogatePair(baseText, 0))))
                    {
                        int baseChar = char.ConvertToUtf32(baseText, 0);
                        for (uint c = start; c <= end; c++)
                        {
                            int cp = baseChar + (int)(c - start);
                            if (cp >= 0 && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF))
                                map.CharMap[c] = char.ConvertFromUtf32(cp);
                        }
                    }
                }
            }
        }

        return map;
    }

    private static string DecodeUtf16BeHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return string.Empty;

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return string.Empty;
        }

        if (bytes.Length == 1)
            return ((char)bytes[0]).ToString();

        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    private static string DecodeTextOperand(string rawToken, PdfToUnicodeMap? cmap)
    {
        if (rawToken.StartsWith('(') && rawToken.EndsWith(')') && rawToken.Length >= 2)
        {
            string lit = UnescapePdfString(rawToken[1..^1]);
            return cmap is not null ? cmap.MapLiteralBytes(lit) : lit;
        }

        if (rawToken.StartsWith('<') && rawToken.EndsWith('>') && rawToken.Length >= 4 && !rawToken.StartsWith("<<", StringComparison.Ordinal))
        {
            string hex = rawToken[1..^1];
            if (cmap is not null && cmap.TryDecodeHex(hex, out var mapped))
                return mapped;
            return DecodeAsciiFallbackHex(hex);
        }

        return string.Empty;
    }

    private static string DecodeTjArrayOperand(string rawArray, PdfToUnicodeMap? cmap)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < rawArray.Length)
        {
            if (rawArray[i] == '(')
            {
                i++;
                int start = i;
                int depth = 1;
                while (i < rawArray.Length && depth > 0)
                {
                    if (rawArray[i] == '\\' && i + 1 < rawArray.Length)
                    {
                        i += 2;
                    }
                    else
                    {
                        if (rawArray[i] == '(') depth++;
                        else if (rawArray[i] == ')') depth--;
                        if (depth > 0) i++;
                        else break;
                    }
                }
                string lit = UnescapePdfString(rawArray[start..i]);
                sb.Append(cmap is not null ? cmap.MapLiteralBytes(lit) : lit);
                if (i < rawArray.Length && rawArray[i] == ')') i++;
            }
            else if (rawArray[i] == '<')
            {
                int end = rawArray.IndexOf('>', i + 1);
                if (end < 0) break;
                string hex = rawArray[(i + 1)..end];
                if (cmap is not null && cmap.TryDecodeHex(hex, out var mapped))
                    sb.Append(mapped);
                else
                    sb.Append(DecodeAsciiFallbackHex(hex));
                i = end + 1;
            }
            else if (char.IsDigit(rawArray[i]) || rawArray[i] == '-')
            {
                int numStart = i;
                i++;
                while (i < rawArray.Length && (char.IsDigit(rawArray[i]) || rawArray[i] == '.'))
                    i++;
                if (double.TryParse(rawArray[numStart..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double kern) && kern < -120)
                    sb.Append(' ');
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    private static string DecodeAsciiFallbackHex(string hex)
    {
        if (hex.Length < 2) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < hex.Length; i += 2)
        {
            if (byte.TryParse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b) && b >= 0x20 && b <= 0x7E)
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    // =========================================================================
    // Visual Rendered-Page Tensor Augmentation & Label/Ink Box Refinement
    // =========================================================================

    private static List<BoundingBox> AugmentTensorWithRenderedVisualFeatures(
        float[] tensor,
        int plane,
        RenderedRgbBitmap bitmap,
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        int bmpW = bitmap.Width;
        int bmpH = bitmap.Height;
        var rgb = bitmap.RgbPixels;
        if (bmpW <= 0 || bmpH <= 0 || rgb.Length < bmpW * bmpH * 3)
            return [];

        var inkCell = new float[plane];
        var chromaCell = new float[plane];
        var edgeCell = new float[plane];

        for (int gy = 0; gy < TensorGridHeight; gy++)
        {
            int py0 = (gy * bmpH) / TensorGridHeight;
            int py1 = Math.Max(py0 + 1, ((gy + 1) * bmpH) / TensorGridHeight);

            for (int gx = 0; gx < TensorGridWidth; gx++)
            {
                int px0 = (gx * bmpW) / TensorGridWidth;
                int px1 = Math.Max(px0 + 1, ((gx + 1) * bmpW) / TensorGridWidth);

                int inkCount = 0;
                int chromaCount = 0;
                int edgeCount = 0;
                int samples = 0;

                for (int py = py0; py < py1 && py < bmpH; py++)
                {
                    for (int px = px0; px < px1 && px < bmpW; px++)
                    {
                        int idx = (py * bmpW + px) * 3;
                        byte r = rgb[idx];
                        byte g = rgb[idx + 1];
                        byte b = rgb[idx + 2];

                        int maxC = Math.Max(r, Math.Max(g, b));
                        int minC = Math.Min(r, Math.Min(g, b));
                        int lum = (r * 77 + g * 150 + b * 29) >> 8;

                        if (lum < 240)
                            inkCount++;
                        if (maxC - minC > 22)
                            chromaCount++;

                        if (px + 1 < bmpW && py + 1 < bmpH)
                        {
                            int rIdx = idx + 3;
                            int dIdx = idx + bmpW * 3;
                            int rLum = (rgb[rIdx] * 77 + rgb[rIdx + 1] * 150 + rgb[rIdx + 2] * 29) >> 8;
                            int dLum = (rgb[dIdx] * 77 + rgb[dIdx + 1] * 150 + rgb[dIdx + 2] * 29) >> 8;
                            if (Math.Abs(lum - rLum) + Math.Abs(lum - dLum) > 28)
                                edgeCount++;
                        }
                        samples++;
                    }
                }

                if (samples == 0) continue;
                int cell = gy * TensorGridWidth + gx;
                inkCell[cell] = inkCount / (float)samples;
                chromaCell[cell] = chromaCount / (float)samples;
                edgeCell[cell] = edgeCount / (float)samples;

                // Boost ONNX visual occupancy when colored graphics or high-density non-margin visuals appear
                bool notMargin = gy >= 5 && gy <= TensorGridHeight - 5;
                if (notMargin && (chromaCell[cell] >= 0.04f || (inkCell[cell] >= 0.30f && edgeCell[cell] >= 0.06f)))
                {
                    float visualImgScore = Math.Clamp(chromaCell[cell] * 2.2f + inkCell[cell] * 0.55f, 0f, 1.0f);
                    float visualVecScore = Math.Clamp(edgeCell[cell] * 1.9f, 0f, 0.9f);
                    tensor[cell] = Math.Max(tensor[cell], visualImgScore);
                    tensor[plane + cell] = Math.Max(tensor[plane + cell], visualVecScore);
                }
            }
        }

        // Extract salient graphic components directly from visual bitmap (catches scanned PDFs or Form/shading diagrams)
        var graphicMask = new bool[plane];
        for (int gy = 5; gy < TensorGridHeight - 5; gy++)
        {
            for (int gx = 1; gx < TensorGridWidth - 1; gx++)
            {
                int idx = gy * TensorGridWidth + gx;
                if (chromaCell[idx] >= 0.04f || inkCell[idx] >= 0.42f)
                    graphicMask[idx] = true;
            }
        }

        var visited = new bool[plane];
        var regions = new List<BoundingBox>();
        var queue = new Queue<int>();

        for (int gy = 5; gy < TensorGridHeight - 5; gy++)
        {
            for (int gx = 1; gx < TensorGridWidth - 1; gx++)
            {
                int start = gy * TensorGridWidth + gx;
                if (!graphicMask[start] || visited[start])
                    continue;

                visited[start] = true;
                queue.Enqueue(start);
                int minX = gx, maxX = gx, minY = gy, maxY = gy;
                int count = 0;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    int cx = cur % TensorGridWidth;
                    int cy = cur / TensorGridWidth;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                    count++;

                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 5 || ny >= TensorGridHeight - 5) continue;
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 1 || nx >= TensorGridWidth - 1) continue;
                            int nIdx = ny * TensorGridWidth + nx;
                            if (graphicMask[nIdx] && !visited[nIdx])
                            {
                                visited[nIdx] = true;
                                queue.Enqueue(nIdx);
                            }
                        }
                    }
                }

                double bx = (minX / (double)TensorGridWidth) * pageW;
                double by = (minY / (double)TensorGridHeight) * pageH;
                double bw = ((maxX - minX + 1) / (double)TensorGridWidth) * pageW;
                double bh = ((maxY - minY + 1) / (double)TensorGridHeight) * pageH;

                if (count >= 10 && bw >= 65 && bh >= 45 && !(bw > pageW * 0.94 && bh > pageH * 0.94))
                {
                    var candidate = new BoundingBox(Math.Round(bx, 2), Math.Round(by, 2), Math.Round(bw, 2), Math.Round(bh, 2));
                    if (!IsUniformShadedProseBox(candidate, bitmap, textSpans, pageW, pageH))
                        regions.Add(candidate);
                }
            }
        }

        return regions;
    }

    private static bool IsUniformShadedProseBox(
        BoundingBox box,
        RenderedRgbBitmap bitmap,
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        if (!IsProseCalloutBox(box, textSpans))
            return false;

        double scaleX = bitmap.Width / Math.Max(1.0, pageW);
        double scaleY = bitmap.Height / Math.Max(1.0, pageH);
        int x0 = Math.Clamp((int)Math.Floor((box.X + 4) * scaleX), 0, Math.Max(0, bitmap.Width - 1));
        int x1 = Math.Clamp((int)Math.Ceiling((box.Right - 4) * scaleX), x0, Math.Max(0, bitmap.Width - 1));
        int y0 = Math.Clamp((int)Math.Floor((box.Y + 4) * scaleY), 0, Math.Max(0, bitmap.Height - 1));
        int y1 = Math.Clamp((int)Math.Ceiling((box.Bottom - 4) * scaleY), y0, Math.Max(0, bitmap.Height - 1));

        int bgCount = 0;
        long sumR = 0, sumG = 0, sumB = 0;
        long sumSqR = 0, sumSqG = 0, sumSqB = 0;
        int step = Math.Max(1, Math.Min(x1 - x0, y1 - y0) / 24);

        for (int py = y0; py <= y1; py += step)
        {
            int rowBase = py * bitmap.Width;
            for (int px = x0; px <= x1; px += step)
            {
                int idx = (rowBase + px) * 3;
                byte r = bitmap.RgbPixels[idx];
                byte g = bitmap.RgbPixels[idx + 1];
                byte b = bitmap.RgbPixels[idx + 2];
                int lum = (r * 77 + g * 150 + b * 29) >> 8;
                if (lum < 160) continue; // ignore foreground text ink

                bgCount++;
                sumR += r; sumG += g; sumB += b;
                sumSqR += (long)r * r; sumSqG += (long)g * g; sumSqB += (long)b * b;
            }
        }

        if (bgCount < 16)
            return false;

        double varR = (sumSqR / (double)bgCount) - Math.Pow(sumR / (double)bgCount, 2);
        double varG = (sumSqG / (double)bgCount) - Math.Pow(sumG / (double)bgCount, 2);
        double varB = (sumSqB / (double)bgCount) - Math.Pow(sumB / (double)bgCount, 2);
        double maxStd = Math.Sqrt(Math.Max(0, Math.Max(varR, Math.Max(varG, varB))));
        return maxStd <= 12.0;
    }

    internal static BoundingBox TrimRunningHeaderAndFooter(
        BoundingBox box,
        RenderedRgbBitmap? bmp,
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        double top = box.Y;
        double bottom = box.Bottom;

        bool hasBmp = bmp is not null && bmp.Width > 0 && bmp.Height > 0 &&
                      bmp.RgbPixels.Length >= bmp.Width * bmp.Height * 3;
        double scaleX = hasBmp ? bmp!.Width / Math.Max(1.0, pageW) : 1.0;
        double scaleY = hasBmp ? bmp!.Height / Math.Max(1.0, pageH) : 1.0;
        int px0 = hasBmp ? Math.Clamp((int)Math.Floor((box.X + 2) * scaleX), 0, bmp!.Width - 1) : 0;
        int px1 = hasBmp ? Math.Clamp((int)Math.Ceiling((box.Right - 2) * scaleX), px0, bmp!.Width - 1) : 0;

        bool IsPtRowWhite(int ptY)
        {
            if (!hasBmp) return true;
            int py0 = Math.Clamp((int)Math.Floor(ptY * scaleY), 0, bmp!.Height - 1);
            int py1 = Math.Clamp((int)Math.Floor((ptY + 1) * scaleY) - 1, py0, bmp.Height - 1);
            int ink = 0, total = 0;
            for (int py = py0; py <= py1; py++)
            {
                int rowBase = py * bmp.Width;
                for (int px = px0; px <= px1; px++)
                {
                    int idx = (rowBase + px) * 3;
                    int lum = (bmp.RgbPixels[idx] * 77 + bmp.RgbPixels[idx + 1] * 150 + bmp.RgbPixels[idx + 2] * 29) >> 8;
                    if (lum < 240) ink++;
                    total++;
                }
            }
            return total == 0 || (ink / (double)total) < 0.015;
        }

        // Trim top running header band and bottom running footer band symmetrically
        if (pageH >= 400)
        {
            top = TrimBandEdge(fromTop: true, top, bottom);
            bottom = TrimBandEdge(fromTop: false, top, bottom);
        }

        double TrimBandEdge(bool fromTop, double curTop, double curBottom)
        {
            if (fromTop ? !(curTop < 54 && curBottom > 80) : !(curBottom > pageH - 44 && curTop < pageH - 80))
                return fromTop ? curTop : curBottom;

            var bandSpans = textSpans.Where(s =>
                (fromTop ? s.Y <= 46 && s.Bottom <= 56 : s.Y >= pageH - 44 && s.Bottom <= pageH) &&
                s.FontSize <= 14 && !IsCaptionText(s.Text) &&
                s.Right >= box.X - 10 && s.X <= box.Right + 10).ToList();
            bool hasBandSpan = bandSpans.Count > 0;
            if (!hasBandSpan && (fromTop ? curTop > 34 : curBottom < pageH - 28))
                return fromTop ? curTop : curBottom;

            bool trimmedWithBitmap = false;
            double result = fromTop ? curTop : curBottom;
            if (hasBmp)
            {
                int dir = fromTop ? 1 : -1;
                int yStart = fromTop ? (int)Math.Floor(Math.Max(curTop + 2, 16)) : (int)Math.Ceiling(Math.Min(curBottom - 2, pageH - 16));
                int yEnd = fromTop ? (int)Math.Ceiling(Math.Min(78.0, curBottom - 24.0)) : (int)Math.Floor(Math.Max(pageH - 72, curTop + 24));
                if ((yEnd - yStart) * dir > 8)
                {
                    bool seenBandInk = false;
                    int gapStart = -1, selectedGap = -1;
                    for (int ptY = yStart; dir > 0 ? ptY <= yEnd : ptY >= yEnd; ptY += dir)
                    {
                        if (!IsPtRowWhite(ptY))
                        {
                            bool inBandGap = fromTop ? (gapStart >= 0 && ptY - gapStart >= 5 && gapStart <= 58)
                                                     : (gapStart >= 0 && gapStart - ptY >= 5 && gapStart >= pageH - 58);
                            if (inBandGap)
                            {
                                int inkRowsPast = 0;
                                int checkLimit = fromTop
                                    ? (int)Math.Ceiling(Math.Min(curBottom - 2, ptY + 12))
                                    : (int)Math.Floor(Math.Max(curTop + 2, ptY - 12));
                                for (int cy = ptY; dir > 0 ? cy <= checkLimit : cy >= checkLimit; cy += dir)
                                    if (!IsPtRowWhite(cy)) inkRowsPast++;

                                if (inkRowsPast >= 3 || (fromTop ? ptY >= 46 : ptY <= pageH - 46))
                                {
                                    selectedGap = ptY;
                                    break;
                                }
                            }
                            gapStart = -1;
                            if (fromTop ? ptY <= 48 : ptY >= pageH - 46) seenBandInk = true;
                        }
                        else if (seenBandInk && gapStart < 0)
                        {
                            gapStart = ptY;
                        }
                    }

                    if (fromTop && selectedGap < 0 && gapStart >= 0 && (yEnd + 1) - gapStart >= 5 && gapStart <= 58)
                        selectedGap = yEnd + 1;

                    if (selectedGap > 0)
                    {
                        if (fromTop)
                        {
                            int checkBelowEnd = (int)Math.Ceiling(Math.Min(curBottom - 2, selectedGap + 60));
                            int firstInkY = -1;
                            for (int ptY = selectedGap; ptY <= checkBelowEnd; ptY++)
                                if (!IsPtRowWhite(ptY)) { firstInkY = ptY; break; }
                            if (firstInkY >= 0)
                            {
                                double candidate = Math.Max(selectedGap - 1.0, firstInkY - 1.5);
                                if (candidate < curBottom - 20) { result = Math.Max(curTop, candidate); trimmedWithBitmap = true; }
                            }
                        }
                        else
                        {
                            double candidate = Math.Min(selectedGap + 2.0, curBottom);
                            if (candidate > curTop + 20) { result = Math.Min(curBottom, candidate); trimmedWithBitmap = true; }
                        }
                    }
                }
            }

            if (!trimmedWithBitmap && hasBandSpan)
            {
                if (fromTop)
                {
                    double maxHdrBottom = bandSpans.Max(s => s.Bottom);
                    if (maxHdrBottom + 4 < curBottom - 20) result = Math.Max(curTop, maxHdrBottom + 2.0);
                }
                else
                {
                    double minFtrTop = bandSpans.Min(s => s.Y);
                    if (minFtrTop - 4 > curTop + 20) result = Math.Min(curBottom, minFtrTop - 2.0);
                }
            }

            return result;
        }

        return ClampToPage(new BoundingBox(box.X, top, box.Width, Math.Max(4, bottom - top)), pageW, pageH);
    }

    internal static BoundingBox CarveCaptionOutOfFigureBox(
        BoundingBox figBox,
        BoundingBox? captionBox,
        double pageW,
        double pageH)
    {
        if (captionBox is null)
            return figBox;

        if (Math.Min(figBox.Right, captionBox.Right) - Math.Max(figBox.X, captionBox.X) <= 0)
            return figBox;

        double top = figBox.Y;
        double bottom = figBox.Bottom;

        if (captionBox.Y >= figBox.Y + figBox.Height * 0.40 &&
            captionBox.Y - 2.0 < bottom &&
            captionBox.Y - 2.0 > top + 20.0)
        {
            bottom = captionBox.Y - 2.0;
        }
        else if (captionBox.Bottom <= figBox.Y + figBox.Height * 0.55 &&
                 captionBox.Bottom + 2.0 > top &&
                 captionBox.Bottom + 2.0 < bottom - 20.0)
        {
            top = captionBox.Bottom + 2.0;
        }

        return ClampToPage(new BoundingBox(figBox.X, top, figBox.Width, Math.Max(4, bottom - top)), pageW, pageH);
    }

    internal sealed record PageCaptionAnchor(
        string Text,
        BoundingBox Box,
        ParsedFigureCaption Parsed);

    internal static List<PageCaptionAnchor> ExtractPageCaptionAnchors(
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        var anchors = new List<PageCaptionAnchor>();

        for (int i = 0; i < textSpans.Count; i++)
        {
            var lead = textSpans[i];
            if (!FigureNumberNormalizer.TryParseFigureCaption(lead.Text, out var initialParsed))
                continue;

            // Reject wrapped inline prose references whose immediately preceding line is an unfinished prose line in the same column
            bool bareNumberLine = string.Equals(lead.Text.Trim(), initialParsed.ShortLabel, StringComparison.OrdinalIgnoreCase);
            if (bareNumberLine && HasUnfinishedPrecedingProseLine(lead, textSpans))
                continue;

            var lines = new List<ParsedTextSpan> { lead };
            var sb = new StringBuilder(lead.Text.Trim());

            bool hasRightColumnNeighbor = textSpans.Any(s =>
                !ReferenceEquals(s, lead) &&
                s.X > pageW * 0.48 &&
                Math.Abs(s.Y - lead.Y) <= 45);
            bool inLeftColumn = lead.Right < pageW * 0.52 && (lead.Width >= pageW * 0.25 || hasRightColumnNeighbor);
            bool inRightColumn = lead.X > pageW * 0.48;
            double maxVerticalGap = Math.Max(lead.FontSize * 1.35, 10.0);

            var prevLine = lead;
            for (int k = 0; k < 18; k++)
            {
                ParsedTextSpan? nextLine = null;
                double minGap = double.MaxValue;

                foreach (var cand in textSpans)
                {
                    if (ReferenceEquals(cand, prevLine) || lines.Contains(cand))
                        continue;
                    if (IsCaptionText(cand.Text) || FigureNumberNormalizer.IsNonFigureArtifactCaption(cand.Text))
                        continue;
                    if (cand.FontSize > lead.FontSize + 0.8)
                        continue;

                    if (lead.Width >= 80 && cand.X > lead.Right - 4)
                        continue;
                    if (inLeftColumn && (cand.X > pageW * 0.48 || cand.Right > pageW * 0.52))
                        continue;
                    if (inRightColumn && cand.X < pageW * 0.48)
                        continue;
                    if ((inLeftColumn || inRightColumn) && cand.X > prevLine.Right + 24)
                        continue;

                    double verticalGap = cand.Y - prevLine.Bottom;
                    if (verticalGap < -2 || verticalGap > maxVerticalGap)
                        continue;

                    bool alignedWithLead = Math.Abs(cand.X - lead.X) <= 18;
                    if (!alignedWithLead)
                        continue;

                    if (verticalGap < minGap)
                    {
                        minGap = verticalGap;
                        nextLine = cand;
                    }
                }

                if (nextLine is null)
                    break;

                lines.Add(nextLine);
                sb.Append(' ').Append(nextLine.Text.Trim());
                prevLine = nextLine;
            }

            string mergedText = sb.ToString().Trim();
            if (!FigureNumberNormalizer.TryParseFigureCaption(mergedText, out var finalParsed))
                continue;

            double minX = lines.Min(l => l.X);
            double minY = lines.Min(l => l.Y);
            double maxX = lines.Max(l => l.Right);
            double maxY = lines.Max(l => l.Bottom);
            var capBox = ClampToPage(new BoundingBox(minX, minY, Math.Max(4, maxX - minX), Math.Max(6, maxY - minY)), pageW, pageH);
            anchors.Add(new PageCaptionAnchor(mergedText, capBox, finalParsed));
        }

        return anchors;
    }

    private static bool HasUnfinishedPrecedingProseLine(ParsedTextSpan lead, IReadOnlyList<ParsedTextSpan> textSpans)
    {
        foreach (var prev in textSpans)
        {
            if (ReferenceEquals(prev, lead)) continue;
            double vGap = lead.Y - prev.Bottom;
            if (vGap < -2 || vGap > Math.Max(lead.FontSize * 0.65, 8.0)) continue;
            if (Math.Abs(prev.X - lead.X) > 10.0 || Math.Abs(prev.FontSize - lead.FontSize) > 0.7) continue;
            string t = prev.Text.Trim();
            if (t.Length < 25) continue;
            char last = t[^1];
            if (last is not ('.' or ':' or '?' or '!'))
                return true;
        }
        return false;
    }

    internal static List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source, PageCaptionAnchor? Anchor)>
        AssignCaptionsToPageFigures(
            IReadOnlyList<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source)> trimmedRegions,
            IReadOnlyList<ParsedTextSpan> textSpans,
            double pageW,
            double pageH,
            string? dominantVocabPrefix = null)
    {
        var regions = trimmedRegions.ToList();
        var anchors = ExtractPageCaptionAnchors(textSpans, pageW, pageH);

        // Multi-panel fusion: if two adjacent figures sit side-by-side under a single spanning caption, fuse them once
        if (regions.Count >= 2 && anchors.Count > 0 && anchors.Count < regions.Count)
        {
            var mergedIndices = new HashSet<int>();
            var fusedList = new List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source)>();

            for (int cIdx = 0; cIdx < anchors.Count; cIdx++)
            {
                var cap = anchors[cIdx];
                var spanningMembers = new List<int>();
                for (int rIdx = 0; rIdx < regions.Count; rIdx++)
                {
                    if (mergedIndices.Contains(rIdx)) continue;
                    var box = regions[rIdx].Box;
                    double dyBelow = cap.Box.Y - box.Bottom;
                    double hOverlap = Math.Min(box.Right, cap.Box.Right) - Math.Max(box.X, cap.Box.X);
                    if (dyBelow >= -24 && dyBelow <= 85 && hOverlap >= 16)
                        spanningMembers.Add(rIdx);
                }

                if (spanningMembers.Count == 2)
                {
                    var b0 = regions[spanningMembers[0]];
                    var b1 = regions[spanningMembers[1]];
                    double hGap = Math.Max(0, Math.Max(b0.Box.X, b1.Box.X) - Math.Min(b0.Box.Right, b1.Box.Right));
                    double vOverlap = Math.Min(b0.Box.Bottom, b1.Box.Bottom) - Math.Max(b0.Box.Y, b1.Box.Y);
                    double minX = Math.Min(b0.Box.X, b1.Box.X);
                    double minY = Math.Min(b0.Box.Y, b1.Box.Y);
                    double maxX = Math.Max(b0.Box.Right, b1.Box.Right);
                    double maxY = Math.Max(b0.Box.Bottom, b1.Box.Bottom);
                    double unionW = maxX - minX;
                    double unionH = maxY - minY;

                    bool neitherHasCloserOwnCap = !anchors.Where((_, idx) => idx != cIdx).Any(other =>
                        TryScoreCaptionMatch(b0.Box, other, dominantVocabPrefix, out _) ||
                        TryScoreCaptionMatch(b1.Box, other, dominantVocabPrefix, out _));

                    if (neitherHasCloserOwnCap && hGap <= 42 && vOverlap >= 24 &&
                        unionW <= pageW * 0.94 && unionH <= pageH * 0.88)
                    {
                        mergedIndices.Add(spanningMembers[0]);
                        mergedIndices.Add(spanningMembers[1]);
                        fusedList.Add((
                            ClampToPage(new BoundingBox(minX, minY, unionW, unionH), pageW, pageH),
                            Math.Max(b0.Confidence, b1.Confidence),
                            b0.Category,
                            DetectionSource.Hybrid));
                    }
                }
            }

            if (mergedIndices.Count > 0)
            {
                for (int i = 0; i < regions.Count; i++)
                    if (!mergedIndices.Contains(i)) fusedList.Add(regions[i]);
                regions = fusedList.OrderBy(r => r.Box.Y).ThenBy(r => r.Box.X).ToList();
            }
        }

        // Exclusive 1-to-1 bipartite caption matching by lowest score
        var pairs = new List<(int FigIdx, int CapIdx, double Score)>();
        for (int f = 0; f < regions.Count; f++)
        {
            for (int c = 0; c < anchors.Count; c++)
            {
                if (TryScoreCaptionMatch(regions[f].Box, anchors[c], dominantVocabPrefix, out double score))
                    pairs.Add((f, c, score));
            }
        }

        pairs.Sort((a, b) =>
        {
            int cmp = a.Score.CompareTo(b.Score);
            if (cmp != 0) return cmp;
            cmp = a.FigIdx.CompareTo(b.FigIdx);
            return cmp != 0 ? cmp : a.CapIdx.CompareTo(b.CapIdx);
        });

        var figAssigned = new PageCaptionAnchor?[regions.Count];
        var capTaken = new bool[anchors.Count];
        foreach (var (figIdx, capIdx, _) in pairs)
        {
            if (figAssigned[figIdx] is not null || capTaken[capIdx])
                continue;
            figAssigned[figIdx] = anchors[capIdx];
            capTaken[capIdx] = true;
        }

        var result = new List<(BoundingBox Box, double Confidence, FigureCategory Category, DetectionSource Source, PageCaptionAnchor? Anchor)>(regions.Count);
        for (int f = 0; f < regions.Count; f++)
        {
            var r = regions[f];
            result.Add((r.Box, r.Confidence, r.Category, r.Source, figAssigned[f]));
        }
        return result;
    }

    private static bool TryScoreCaptionMatch(
        BoundingBox box,
        PageCaptionAnchor anchor,
        string? dominantVocabPrefix,
        out double score)
    {
        score = double.MaxValue;
        var cap = anchor.Box;
        double spanRight = cap.X + Math.Max(cap.Width, 60);
        bool horizAligned = spanRight >= box.X - 70 && cap.X <= box.Right + 70;
        double horizOverlap = Math.Min(box.Right, cap.Right) - Math.Max(box.X, cap.X);

        double dist = double.MaxValue;
        if (horizAligned)
        {
            double dyBelow = cap.Y - box.Bottom;
            double dyAbove = box.Y - cap.Bottom;
            if (dyBelow >= -36 && dyBelow <= 95 && cap.Y >= box.Y + box.Height * 0.45)
                dist = Math.Abs(dyBelow) + (horizOverlap > 0 ? 0.0 : 14.0);
            else if (dyAbove >= -28 && dyAbove <= 65 && cap.Bottom <= box.Y + box.Height * 0.55)
                dist = Math.Abs(dyAbove) + 12.0 + (horizOverlap > 0 ? 0.0 : 14.0);
        }

        // Side-column caption fallback (adjacent column with >=25% vertical overlap and <=55pt horizontal gap)
        if (dist == double.MaxValue)
        {
            double vertOverlap = Math.Min(box.Bottom, cap.Bottom) - Math.Max(box.Y, cap.Y);
            double minH = Math.Min(box.Height, cap.Height);
            if (vertOverlap >= Math.Max(6.0, minH * 0.5))
            {
                double horizGap = cap.X >= box.Right
                    ? cap.X - box.Right
                    : (box.X >= cap.Right ? box.X - cap.Right : 0.0);
                if (horizGap >= -16 && horizGap <= 55)
                    dist = Math.Max(0.0, horizGap) + 22.0;
            }
        }

        if (dist == double.MaxValue)
            return false;

        if (!string.IsNullOrWhiteSpace(dominantVocabPrefix) &&
            string.Equals(anchor.Parsed.Prefix, dominantVocabPrefix, StringComparison.OrdinalIgnoreCase))
        {
            dist -= 6.0;
        }

        score = dist;
        return true;
    }

    private static string? FindNearestNonFigureCaption(
        BoundingBox box,
        IReadOnlyList<ParsedTextSpan> textSpans,
        double pageW,
        double pageH)
    {
        string? best = null;
        double bestDist = double.MaxValue;

        foreach (var span in textSpans)
        {
            if (!FigureNumberNormalizer.IsNonFigureArtifactCaption(span.Text))
                continue;

            double spanRight = span.X + Math.Max(span.Width, 60);
            bool horizAligned = spanRight >= box.X - 55 && span.X <= box.Right + 55;
            if (!horizAligned)
                continue;

            bool insideTopBand = span.Y >= box.Y - 6 && span.Bottom <= box.Y + Math.Min(48.0, box.Height * 0.45);
            double dyAbove = box.Y - span.Bottom;
            double dyBelow = span.Y - box.Bottom;

            double dist = double.MaxValue;
            if (insideTopBand)
                dist = Math.Max(0, span.Y - box.Y);
            else if (dyAbove >= -24 && dyAbove <= 55 && span.Bottom <= box.Y + box.Height * 0.45)
                dist = Math.Abs(dyAbove);
            else if (dyBelow >= -24 && dyBelow <= 55 && span.Y >= box.Y + box.Height * 0.55)
                dist = Math.Abs(dyBelow) + 6;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = span.Text.Trim();
            }
        }

        _ = pageW;
        _ = pageH;
        return best;
    }

    private static BoundingBox ExpandBoxWithAttachedLabels(
        BoundingBox artworkBox,
        IReadOnlyList<ParsedTextSpan> fineTextSpans,
        BoundingBox? captionBox,
        double pageW,
        double pageH)
    {
        double left = artworkBox.X;
        double top = artworkBox.Y;
        double right = artworkBox.Right;
        double bottom = artworkBox.Bottom;

        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var span in fineTextSpans)
            {
                var t = span.Text.Trim();
                if (t.Length == 0 || t.Length > 48 || IsCaptionText(t))
                    continue;

                if (span.Y <= 50 || span.Bottom >= pageH - 42)
                    continue;

                if (span.Width > Math.Max(artworkBox.Width * 0.72, 165))
                    continue;

                var spanBox = new BoundingBox(span.X, span.Y, Math.Max(2, span.Width), span.EffectiveHeight);
                if (captionBox is not null && spanBox.ComputeContainment(ExpandRect(captionBox, 3)) > 0.35)
                    continue;

                if (span.X >= left - 18 && span.Right <= right + 18 &&
                    span.Y >= top - 16 && span.Bottom <= bottom + 16)
                {
                    // Stop vertical expansion if caption sits immediately below
                    double nextBottom = Math.Max(bottom, span.Bottom);
                    if (captionBox is not null && captionBox.Y >= artworkBox.Bottom - 4 && nextBottom > captionBox.Y - 2)
                        nextBottom = bottom;

                    left = Math.Min(left, span.X);
                    top = Math.Min(top, span.Y);
                    right = Math.Max(right, span.Right);
                    bottom = Math.Max(bottom, nextBottom);
                }
            }
        }

        return ClampToPage(new BoundingBox(left, top, Math.Max(4, right - left), Math.Max(4, bottom - top)), pageW, pageH);
    }

    private static BoundingBox SnapBoxEdgesToVisualInk(
        BoundingBox box,
        RenderedRgbBitmap bmp,
        BoundingBox? captionBox,
        double pageW,
        double pageH)
    {
        double scaleX = bmp.Width / Math.Max(1.0, pageW);
        double scaleY = bmp.Height / Math.Max(1.0, pageH);
        var rgb = bmp.RgbPixels;
        if (rgb.Length < bmp.Width * bmp.Height * 3)
            return box;

        double left = box.X;
        double top = box.Y;
        double right = box.Right;
        double bottom = box.Bottom;

        bool HasEdgeInkVertical(double ptX, double ptTop, double ptBottom)
        {
            int px = Math.Clamp((int)Math.Round(ptX * scaleX), 0, bmp.Width - 1);
            int y0 = Math.Clamp((int)Math.Floor(ptTop * scaleY), 0, bmp.Height - 1);
            int y1 = Math.Clamp((int)Math.Ceiling(ptBottom * scaleY), y0, bmp.Height - 1);
            int ink = 0, total = Math.Max(1, y1 - y0 + 1);
            for (int py = y0; py <= y1; py++)
            {
                int idx = (py * bmp.Width + px) * 3;
                int lum = (rgb[idx] * 77 + rgb[idx + 1] * 150 + rgb[idx + 2] * 29) >> 8;
                if (lum < 236) ink++;
            }
            return ink / (double)total >= 0.03;
        }

        bool HasEdgeInkHorizontal(double ptY, double ptLeft, double ptRight)
        {
            int py = Math.Clamp((int)Math.Round(ptY * scaleY), 0, bmp.Height - 1);
            int x0 = Math.Clamp((int)Math.Floor(ptLeft * scaleX), 0, bmp.Width - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(ptRight * scaleX), x0, bmp.Width - 1);
            int ink = 0, total = Math.Max(1, x1 - x0 + 1);
            for (int px = x0; px <= x1; px++)
            {
                int idx = (py * bmp.Width + px) * 3;
                int lum = (rgb[idx] * 77 + rgb[idx + 1] * 150 + rgb[idx + 2] * 29) >> 8;
                if (lum < 236) ink++;
            }
            return ink / (double)total >= 0.03;
        }

        for (int step = 0; step < 6; step++)
        {
            if (left >= 1.5 && HasEdgeInkVertical(left, top, bottom)) left -= 1.0;
            else break;
        }

        for (int step = 0; step < 6; step++)
        {
            if (right <= pageW - 1.5 && HasEdgeInkVertical(right, top, bottom)) right += 1.0;
            else break;
        }

        for (int step = 0; step < 6; step++)
        {
            bool entersHeader = box.Y >= 46 && top - 1.0 < 46;
            bool capAbove = captionBox is not null && captionBox.Bottom <= box.Y + 4 && top - 1.0 <= captionBox.Bottom + 2.5;
            if (!entersHeader && !capAbove && top >= 1.5 && HasEdgeInkHorizontal(top, left, right)) top -= 1.0;
            else break;
        }

        for (int step = 0; step < 6; step++)
        {
            bool capBelow = captionBox is not null && captionBox.Y >= box.Bottom - 4 && bottom + 1.0 >= captionBox.Y - 2.5;
            if (!capBelow && bottom <= pageH - 1.5 && HasEdgeInkHorizontal(bottom, left, right)) bottom += 1.0;
            else break;
        }

        return ClampToPage(new BoundingBox(left, top, Math.Max(4, right - left), Math.Max(4, bottom - top)), pageW, pageH);
    }

    private static BoundingBox ExpandRect(BoundingBox box, double pad) =>
        new(Math.Max(0, box.X - pad), Math.Max(0, box.Y - pad), box.Width + 2 * pad, box.Height + 2 * pad);

    [GeneratedRegex(@"/Parent\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex ParentRefRegex();

    [GeneratedRegex(@"/Matrix\s*\[\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s*\]", RegexOptions.Compiled)]
    private static partial Regex MatrixRegex();

    [GeneratedRegex(@"/ToUnicode\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled)]
    private static partial Regex ToUnicodeRefRegex();

    [GeneratedRegex(@"begincodespacerange\s*(.*?)\s*endcodespacerange", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex CodeSpaceRangeBlockRegex();

    [GeneratedRegex(@"beginbfchar\s*(.*?)\s*endbfchar", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BfCharBlockRegex();

    [GeneratedRegex(@"beginbfrange\s*(.*?)\s*endbfrange", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BfRangeBlockRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>", RegexOptions.Compiled)]
    private static partial Regex HexPairRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(?:\[(?<arr>.*?)\]|<(?<dst>[0-9A-Fa-f]+)>)", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex BfRangeEntryRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>", RegexOptions.Compiled)]
    private static partial Regex SingleHexTokenRegex();

    [GeneratedRegex(@"/Subtype\s*/Image\b", RegexOptions.Compiled)]
    private static partial Regex SubtypeImageRegex();

    [GeneratedRegex(@"/Subtype\s*/Form\b", RegexOptions.Compiled)]
    private static partial Regex SubtypeFormRegex();
}
