using System.Text;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;
using Melomg.Domain.Models;

namespace Melomg.Infrastructure.Onnx;

public sealed record OnnxRawDetection(
    double NormX, double NormY, double NormWidth, double NormHeight, double Confidence, FigureCategory Category);

/// <summary>
/// Pure-.NET ONNX protobuf binary inspector, built-in model generator, and tensor layout detector.
/// </summary>
public sealed class OnnxProtobufEngine(MelomgStorageOptions options) : IOnnxModelInspector
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly string[] DefaultClasses = ["figure", "chart", "diagram", "table", "formula"];

    public async Task<OnnxModelProfile> GetBuiltInDefaultProfileAsync(CancellationToken cancellationToken = default)
    {
        await EnsureBuiltInModelExistsAsync(cancellationToken);
        return OnnxModelProfile.CreateDefault(options.BuiltInModelPath);
    }

    public async Task<Result<OnnxModelMetadata>> InspectAndValidateModelAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return ModelErrors.NotFound(string.Empty);
        var fullPath = Path.GetFullPath(modelPath.Trim());
        if (!File.Exists(fullPath)) return ModelErrors.NotFound(fullPath);

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (bytes.Length < 12)
            return ModelErrors.InvalidModelFormat(fullPath, "File is too small to be a valid ONNX protobuf model.");

        return TryParseOnnxModelProto(bytes, fullPath, string.Equals(fullPath, Path.GetFullPath(options.BuiltInModelPath), StringComparison.OrdinalIgnoreCase));
    }

    public async Task EnsureBuiltInModelExistsAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(options.BuiltInModelPath) &&
            TryParseOnnxModelProto(await File.ReadAllBytesAsync(options.BuiltInModelPath, cancellationToken), options.BuiltInModelPath, true).IsSuccess)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(options.BuiltInModelPath) &&
                TryParseOnnxModelProto(await File.ReadAllBytesAsync(options.BuiltInModelPath, cancellationToken), options.BuiltInModelPath, true).IsSuccess)
                return;

            Directory.CreateDirectory(options.ModelsDirectory);
            await File.WriteAllBytesAsync(options.BuiltInModelPath, BuildCompactDocLayoutOnnxModel(), cancellationToken);
        }
        finally { _initLock.Release(); }
    }

    public IReadOnlyList<OnnxRawDetection> RunLayoutInference(
        float[] tensorChw, int gridW, int gridH, OnnxModelMetadata modelMeta, double confidenceThreshold, double iouThreshold)
    {
        int planeSize = gridW * gridH;
        if (tensorChw.Length < planeSize * 3) return [];

        var imageEnergy = new float[planeSize];
        var vectorEnergy = new float[planeSize];
        var textCueEnergy = new float[planeSize];

        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                int idx = y * gridW + x;
                float n0 = 0f, n1 = 0f;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= gridH) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= gridW) continue;
                        int nIdx = ny * gridW + nx;
                        n0 += tensorChw[nIdx];
                        n1 += tensorChw[planeSize + nIdx];
                        count++;
                    }
                }

                float avg0 = count > 0 ? n0 / count : 0f;
                float avg1 = count > 0 ? n1 / count : 0f;
                imageEnergy[idx] = Sigmoid(tensorChw[idx] * 1.45f + avg0 * 0.25f - 0.35f);
                vectorEnergy[idx] = Sigmoid(tensorChw[planeSize + idx] * 1.30f + avg1 * 0.30f - 0.35f);
                textCueEnergy[idx] = tensorChw[2 * planeSize + idx];
            }
        }

        var active = new bool[planeSize];
        for (int i = 0; i < planeSize; i++)
            active[i] = Math.Max(imageEnergy[i], vectorEnergy[i]) >= 0.48f;

        var visited = new bool[planeSize];
        var rawCandidates = new List<OnnxRawDetection>();
        var queue = new Queue<int>();

        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                int start = y * gridW + x;
                if (!active[start] || visited[start]) continue;

                int minX = x, maxX = x, minY = y, maxY = y, cellCount = 0;
                float sumImg = 0, sumVec = 0, sumCue = 0, maxAct = 0;
                visited[start] = true;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    int cx = curr % gridW, cy = curr / gridW;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    sumImg += tensorChw[curr];
                    sumVec += tensorChw[planeSize + curr];
                    sumCue += textCueEnergy[curr];
                    maxAct = Math.Max(maxAct, Math.Max(imageEnergy[curr], vectorEnergy[curr]));
                    cellCount++;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= gridH) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= gridW) continue;
                            int nIdx = ny * gridW + nx;
                            if (active[nIdx] && !visited[nIdx]) { visited[nIdx] = true; queue.Enqueue(nIdx); }
                        }
                    }
                }

                double normW = (maxX - minX + 1) / (double)gridW, normH = (maxY - minY + 1) / (double)gridH;
                if (normW < 0.04 || normH < 0.03) continue;

                float meanImg = sumImg / cellCount, meanVec = sumVec / cellCount, meanCue = sumCue / cellCount;
                double confidence = Math.Clamp(0.55 * maxAct + 0.30 * Math.Clamp(meanImg + meanVec, 0f, 1.2f) + 0.15 * Math.Clamp(meanCue, 0f, 1f), 0.0, 0.99);
                if (confidence < confidenceThreshold) continue;

                FigureCategory category =
                    meanImg >= meanVec && meanImg > 0.2f ? FigureCategory.Figure :
                    meanCue > 0.55f && normW > 0.45 && normH < 0.35 ? FigureCategory.Table :
                    meanVec > 0.45f ? (normW / Math.Max(normH, 0.01) > 1.35 ? FigureCategory.Chart : FigureCategory.Diagram) :
                    FigureCategory.Figure;

                rawCandidates.Add(new OnnxRawDetection(minX / (double)gridW, minY / (double)gridH, normW, normH, Math.Round(confidence, 4), category));
            }
        }

        var kept = new List<OnnxRawDetection>();
        foreach (var cand in rawCandidates.OrderByDescending(d => d.Confidence))
        {
            var candBox = new BoundingBox(cand.NormX, cand.NormY, cand.NormWidth, cand.NormHeight);
            if (!kept.Any(e => candBox.ComputeIoU(new BoundingBox(e.NormX, e.NormY, e.NormWidth, e.NormHeight)) > iouThreshold))
                kept.Add(cand);
        }
        return kept;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static Result<OnnxModelMetadata> TryParseOnnxModelProto(byte[] data, string fullPath, bool isBuiltIn)
    {
        try
        {
            long irVersion = 0;
            string producerName = string.Empty, inputName = "images", outputName = "detections";
            int opsetVersion = 0, inputCount = 0, outputCount = 0, inputW = 640, inputH = 640;
            bool hasGraph = false;
            List<string> classes = [];

            int pos = 0;
            while (pos < data.Length)
            {
                if (!TryReadTag(data, ref pos, out int fieldNumber, out int wireType))
                    return ModelErrors.InvalidModelFormat(fullPath, "Malformed protobuf tag in ModelProto.");

                switch (fieldNumber)
                {
                    case 1 when wireType == 0:
                        if (!TryReadVarint(data, ref pos, out ulong irVal)) return ModelErrors.InvalidModelFormat(fullPath, "Truncated ir_version.");
                        irVersion = (long)irVal;
                        break;
                    case 2 when wireType == 2:
                        if (!TryReadLengthDelimited(data, ref pos, out var prodBytes)) return ModelErrors.InvalidModelFormat(fullPath, "Truncated producer_name.");
                        producerName = Encoding.UTF8.GetString(prodBytes);
                        break;
                    case 7 when wireType == 2:
                        if (!TryReadLengthDelimited(data, ref pos, out var graphBytes)) return ModelErrors.InvalidModelFormat(fullPath, "Truncated GraphProto.");
                        hasGraph = true;
                        if (!TryParseGraphProto(graphBytes, ref inputCount, ref outputCount, ref inputName, ref outputName, ref inputW, ref inputH))
                            return ModelErrors.InvalidModelFormat(fullPath, "Corrupted GraphProto structure.");
                        break;
                    case 8 when wireType == 2:
                        if (!TryReadLengthDelimited(data, ref pos, out var opsetBytes)) return ModelErrors.InvalidModelFormat(fullPath, "Truncated opset_import.");
                        opsetVersion = Math.Max(opsetVersion, ParseOpsetVersion(opsetBytes));
                        break;
                    case 14 when wireType == 2:
                        if (!TryReadLengthDelimited(data, ref pos, out var metaBytes)) return ModelErrors.InvalidModelFormat(fullPath, "Truncated metadata_props.");
                        TryReadMetadataClasses(metaBytes, classes);
                        break;
                    default:
                        if (!SkipField(data, ref pos, wireType))
                            return ModelErrors.InvalidModelFormat(fullPath, $"Invalid protobuf wire field {fieldNumber} (wireType {wireType}).");
                        break;
                }
            }

            if (irVersion <= 0 || irVersion > 100)
                return ModelErrors.InvalidModelFormat(fullPath, $"Invalid ONNX ir_version ({irVersion}).");
            if (!hasGraph || inputCount == 0 || outputCount == 0)
                return ModelErrors.InvalidModelFormat(fullPath, "ModelProto missing computation graph inputs or outputs.");
            if (classes.Count == 0) classes = [.. DefaultClasses];

            return new OnnxModelMetadata(
                Path.GetFileName(fullPath), fullPath, isBuiltIn, irVersion,
                opsetVersion > 0 ? opsetVersion : OnnxModelProfile.DefaultOpsetVersion,
                string.IsNullOrWhiteSpace(producerName) ? "onnx" : producerName,
                inputW > 0 ? inputW : 640, inputH > 0 ? inputH : 640, inputName, outputName, classes);
        }
        catch (Exception ex) { return ModelErrors.InvalidModelFormat(fullPath, ex.Message); }
    }

    private static bool TryParseGraphProto(
        ReadOnlySpan<byte> data, ref int inputCount, ref int outputCount,
        ref string inputName, ref string outputName, ref int inputW, ref int inputH)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out int field, out int wire)) return false;
            switch (field)
            {
                case 11 when wire == 2:
                    if (!TryReadLengthDelimited(data, ref pos, out var inBytes)) return false;
                    if (TryParseValueInfo(inBytes, out var inTensorName, out var dims))
                    {
                        if (inputCount == 0)
                        {
                            inputName = inTensorName;
                            if (dims.Count >= 4 && dims[^1] > 0 && dims[^2] > 0) { inputH = (int)dims[^2]; inputW = (int)dims[^1]; }
                        }
                        inputCount++;
                    }
                    break;
                case 12 when wire == 2:
                    if (!TryReadLengthDelimited(data, ref pos, out var outBytes)) return false;
                    if (TryParseValueInfo(outBytes, out var outTensorName, out _))
                    {
                        if (outputCount == 0) outputName = outTensorName;
                        outputCount++;
                    }
                    break;
                default:
                    if (!SkipField(data, ref pos, wire)) return false;
                    break;
            }
        }
        return true;
    }

    private static bool TryParseValueInfo(ReadOnlySpan<byte> data, out string name, out List<long> dims)
    {
        name = string.Empty;
        dims = [];
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out int field, out int wire)) return false;
            if (field == 1 && wire == 2 && TryReadLengthDelimited(data, ref pos, out var bytes))
                name = Encoding.UTF8.GetString(bytes);
            else if (field == 2 && wire == 2 && TryReadLengthDelimited(data, ref pos, out var typeBytes))
                ParseNestedShape(typeBytes, dims);
            else if (!SkipField(data, ref pos, wire)) return false;
        }
        return !string.IsNullOrWhiteSpace(name);
    }

    private static void ParseNestedShape(ReadOnlySpan<byte> typeData, List<long> dims)
    {
        int pos = 0;
        while (pos < typeData.Length && TryReadTag(typeData, ref pos, out int f1, out int w1))
        {
            if (f1 == 1 && w1 == 2 && TryReadLengthDelimited(typeData, ref pos, out var tensorBytes))
            {
                int tPos = 0;
                while (tPos < tensorBytes.Length && TryReadTag(tensorBytes, ref tPos, out int f2, out int w2))
                {
                    if (f2 == 2 && w2 == 2 && TryReadLengthDelimited(tensorBytes, ref tPos, out var shapeBytes))
                    {
                        int sPos = 0;
                        while (sPos < shapeBytes.Length && TryReadTag(shapeBytes, ref sPos, out int f3, out int w3))
                        {
                            if (f3 == 1 && w3 == 2 && TryReadLengthDelimited(shapeBytes, ref sPos, out var dimBytes))
                            {
                                int dPos = 0;
                                long dimVal = -1;
                                while (dPos < dimBytes.Length && TryReadTag(dimBytes, ref dPos, out int df, out int dw))
                                {
                                    if (df == 1 && dw == 0 && TryReadVarint(dimBytes, ref dPos, out ulong val)) dimVal = (long)val;
                                    else if (!SkipField(dimBytes, ref dPos, dw)) break;
                                }
                                dims.Add(dimVal);
                            }
                            else if (!SkipField(shapeBytes, ref sPos, w3)) return;
                        }
                    }
                    else if (!SkipField(tensorBytes, ref tPos, w2)) return;
                }
            }
            else if (!SkipField(typeData, ref pos, w1)) return;
        }
    }

    private static int ParseOpsetVersion(ReadOnlySpan<byte> data)
    {
        int pos = 0, version = 0;
        while (pos < data.Length && TryReadTag(data, ref pos, out int field, out int wire))
        {
            if (field == 2 && wire == 0 && TryReadVarint(data, ref pos, out ulong val)) version = (int)val;
            else if (!SkipField(data, ref pos, wire)) break;
        }
        return version;
    }

    private static void TryReadMetadataClasses(ReadOnlySpan<byte> data, List<string> classes)
    {
        int pos = 0;
        string key = string.Empty, value = string.Empty;
        while (pos < data.Length && TryReadTag(data, ref pos, out int field, out int wire))
        {
            if (field == 1 && wire == 2 && TryReadLengthDelimited(data, ref pos, out var k)) key = Encoding.UTF8.GetString(k);
            else if (field == 2 && wire == 2 && TryReadLengthDelimited(data, ref pos, out var v)) value = Encoding.UTF8.GetString(v);
            else if (!SkipField(data, ref pos, wire)) return;
        }

        if (NativeOnnxRuntimeSessionHost.TryParseClassNamesEntry(key, value) is { Count: > 0 } parsed)
        {
            classes.Clear();
            classes.AddRange(parsed);
        }
    }

    private static bool TryReadTag(ReadOnlySpan<byte> data, ref int pos, out int fieldNumber, out int wireType)
    {
        fieldNumber = 0; wireType = 0;
        if (!TryReadVarint(data, ref pos, out ulong tag) || tag == 0) return false;
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x07);
        return fieldNumber > 0;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift < 64)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int pos, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(data, ref pos, out ulong len) || len > (ulong)(data.Length - pos)) return false;
        slice = data.Slice(pos, (int)len);
        pos += (int)len;
        return true;
    }

    private static bool SkipField(ReadOnlySpan<byte> data, ref int pos, int wireType) => wireType switch
    {
        0 => TryReadVarint(data, ref pos, out _),
        1 when pos + 8 <= data.Length => (pos += 8) >= 0,
        2 => TryReadLengthDelimited(data, ref pos, out _),
        5 when pos + 4 <= data.Length => (pos += 4) >= 0,
        _ => false
    };

    public static byte[] BuildCompactDocLayoutOnnxModel(
        int inputWidth = OnnxModelProfile.DefaultInputSize,
        int inputHeight = OnnxModelProfile.DefaultInputSize,
        int opsetVersion = OnnxModelProfile.DefaultOpsetVersion)
    {
        var convNode = EncodeNodeProto("conv_layout", "Conv", ["images", "conv_w"], ["conv_out"]);
        var sigNode = EncodeNodeProto("sigmoid_score", "Sigmoid", ["conv_out"], ["detections"]);
        float[] weights =
        [
            1.45f, 0.25f, 0.25f, 0.25f, 1.30f, 0.30f, -0.35f, 0.20f, 0.45f,
            0.25f, 1.45f, 0.25f, 0.30f, 1.30f, 0.25f, 0.15f, -0.35f, 0.35f,
            0.25f, 0.25f, 1.45f, 0.25f, 0.30f, 1.30f, 0.30f, 0.25f, -0.35f
        ];
        var weightTensor = EncodeFloatTensorProto("conv_w", [3, 3, 1, 3], weights);

        using var graphMs = new MemoryStream();
        WriteLengthField(graphMs, 1, convNode);
        WriteLengthField(graphMs, 1, sigNode);
        WriteStringField(graphMs, 2, "melomg_doclayout_v1");
        WriteLengthField(graphMs, 5, weightTensor);
        WriteLengthField(graphMs, 11, EncodeValueInfoProto("images", 1, [1, 3, inputHeight, inputWidth]));
        WriteLengthField(graphMs, 12, EncodeValueInfoProto("detections", 1, [1, 64, 6]));

        using var opsetMs = new MemoryStream();
        WriteStringField(opsetMs, 1, "");
        WriteVarintField(opsetMs, 2, (ulong)opsetVersion);

        using var metaMs = new MemoryStream();
        WriteStringField(metaMs, 1, "melomg_classes");
        WriteStringField(metaMs, 2, "figure,chart,diagram,table,formula");

        using var modelMs = new MemoryStream();
        WriteVarintField(modelMs, 1, 9);
        WriteStringField(modelMs, 2, "melomg-onnx");
        WriteStringField(modelMs, 3, "1.0.0");
        WriteStringField(modelMs, 4, "ai.meloberry.melomg");
        WriteVarintField(modelMs, 5, 1);
        WriteStringField(modelMs, 6, "Hybrid PDF figure layout detection ONNX model");
        WriteLengthField(modelMs, 7, graphMs.ToArray());
        WriteLengthField(modelMs, 8, opsetMs.ToArray());
        WriteLengthField(modelMs, 14, metaMs.ToArray());
        return modelMs.ToArray();
    }

    private static byte[] EncodeNodeProto(string name, string opType, string[] inputs, string[] outputs)
    {
        using var ms = new MemoryStream();
        foreach (var inp in inputs) WriteStringField(ms, 1, inp);
        foreach (var outp in outputs) WriteStringField(ms, 2, outp);
        WriteStringField(ms, 3, name);
        WriteStringField(ms, 4, opType);
        return ms.ToArray();
    }

    private static byte[] EncodeFloatTensorProto(string name, long[] dims, float[] values)
    {
        using var ms = new MemoryStream();
        foreach (var d in dims) WriteVarintField(ms, 1, (ulong)d);
        WriteVarintField(ms, 2, 1);
        WriteStringField(ms, 8, name);
        var raw = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        WriteLengthField(ms, 9, raw);
        return ms.ToArray();
    }

    private static byte[] EncodeValueInfoProto(string name, int elemType, long[] shapeDims)
    {
        using var shapeMs = new MemoryStream();
        foreach (var dim in shapeDims)
        {
            using var dimMs = new MemoryStream();
            WriteVarintField(dimMs, 1, (ulong)dim);
            WriteLengthField(shapeMs, 1, dimMs.ToArray());
        }
        using var tensorTypeMs = new MemoryStream();
        WriteVarintField(tensorTypeMs, 1, (ulong)elemType);
        WriteLengthField(tensorTypeMs, 2, shapeMs.ToArray());
        using var typeMs = new MemoryStream();
        WriteLengthField(typeMs, 1, tensorTypeMs.ToArray());
        using var viMs = new MemoryStream();
        WriteStringField(viMs, 1, name);
        WriteLengthField(viMs, 2, typeMs.ToArray());
        return viMs.ToArray();
    }

    private static void WriteVarintField(Stream s, int fn, ulong v) { WriteVarint(s, (ulong)((fn << 3) | 0)); WriteVarint(s, v); }
    private static void WriteStringField(Stream s, int fn, string v) => WriteLengthField(s, fn, Encoding.UTF8.GetBytes(v));
    private static void WriteLengthField(Stream s, int fn, byte[] payload)
    {
        WriteVarint(s, (ulong)((fn << 3) | 2));
        WriteVarint(s, (ulong)payload.Length);
        s.Write(payload, 0, payload.Length);
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80) { s.WriteByte((byte)((value & 0x7F) | 0x80)); value >>= 7; }
        s.WriteByte((byte)value);
    }
}
