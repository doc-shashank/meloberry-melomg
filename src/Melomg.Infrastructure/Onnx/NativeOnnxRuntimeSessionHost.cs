using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Melomg.Application.Abstractions;
using Melomg.Domain.Documents;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Melomg.Infrastructure.Onnx;

public sealed record NeuralFigureBox(BoundingBox NormalizedBox, double Confidence, FigureCategory Category, string ClassLabel);

public sealed record NeuralLayoutOutput(IReadOnlyList<NeuralFigureBox> Figures, IReadOnlyList<BoundingBox> CaptionNormalizedBoxes);

/// <summary>
/// Manages dynamic Win32 loading of AppData-hosted ONNX Runtime DLLs (DirectML GPU + CPU fallback),
/// pooled <see cref="InferenceSession"/> caching, YOLO tensor preprocessing, and output decoding.
/// </summary>
public sealed class NativeOnnxRuntimeSessionHost : IDisposable
{
    private static readonly string[] DocStructBenchClasses =
    [
        "title", "plain text", "abandon", "figure", "figure_caption",
        "table", "table_caption", "table_footnote", "isolate_formula", "formula_caption"
    ];

    private sealed record NativeLoadSnapshot(string VersionDir, nint OnnxRuntimeHandle);

    private static readonly object ResolverGate = new();
    private static bool s_resolverInstalled;
    private static NativeOnnxRuntimeSessionHost? s_activeHost;

    private readonly object _hostGate = new();
    private readonly object _sessionCacheGate = new();
    private readonly ReaderWriterLockSlim _inferenceLock = new(LockRecursionPolicy.NoRecursion);
    private readonly ConcurrentDictionary<string, CachedSessionEntry> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<nint> _auxiliaryHandles = [];

    private volatile NativeLoadSnapshot? _nativeSnapshot;
    private string? _loadedVersionDir;
    private string? _loadedProvider;
    private nint _dllDirectoryCookie;
    private nint _directMlHandle;
    private nint _providersSharedHandle;
    private nint _onnxRuntimeHandle;
    private bool _disposed;

    private sealed record CachedSessionEntry(
        InferenceSession Session, string ActiveEngine, string InputName,
        int InputWidth, int InputHeight, IReadOnlyList<string>? CustomClasses, long ModelTicks);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(nint cookie);

    public bool TryEnsureLoaded(string versionDir, string provider, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(versionDir) || !Directory.Exists(versionDir))
        {
            errorMessage = $"Runtime directory '{versionDir}' does not exist.";
            return false;
        }

        string ortDll = Path.Combine(versionDir, "onnxruntime.dll");
        if (!File.Exists(ortDll))
        {
            errorMessage = $"Missing '{ortDll}'.";
            return false;
        }

        lock (_hostGate)
        {
            if (_disposed) { errorMessage = "ONNX session host is disposed."; return false; }

            if (string.Equals(_loadedVersionDir, versionDir, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_loadedProvider, provider, StringComparison.OrdinalIgnoreCase) &&
                _onnxRuntimeHandle != 0)
            {
                return true;
            }

            UnloadHandlesLocked();
            InstallDllImportResolverOnce(this);

            try
            {
                _dllDirectoryCookie = AddDllDirectory(versionDir);

                if (string.Equals(provider, "directml", StringComparison.OrdinalIgnoreCase))
                {
                    string dmlDll = Path.Combine(versionDir, "DirectML.dll");
                    if (File.Exists(dmlDll) && NativeLibrary.TryLoad(dmlDll, out nint dmlHandle))
                        _directMlHandle = dmlHandle;
                }

                string sharedDll = Path.Combine(versionDir, "onnxruntime_providers_shared.dll");
                if (File.Exists(sharedDll) && NativeLibrary.TryLoad(sharedDll, out nint sharedHandle))
                    _providersSharedHandle = sharedHandle;

                if (!NativeLibrary.TryLoad(ortDll, out nint ortHandle))
                {
                    UnloadHandlesLocked();
                    errorMessage = $"Failed to load native library '{ortDll}'.";
                    return false;
                }

                _onnxRuntimeHandle = ortHandle;
                _loadedVersionDir = versionDir;
                _loadedProvider = provider;
                _nativeSnapshot = new NativeLoadSnapshot(versionDir, ortHandle);
                return true;
            }
            catch (Exception ex)
            {
                UnloadHandlesLocked();
                errorMessage = ex.Message;
                return false;
            }
        }
    }

    public bool TryRunLayoutDetection(
        string versionDir, string provider, string modelPath, IReadOnlyList<string>? fallbackClasses,
        RenderedRgbBitmap pageBitmap, double confidenceThreshold, double iouThreshold,
        out NeuralLayoutOutput output, out string activeEngine, out string? errorMessage)
    {
        output = new NeuralLayoutOutput([], []);
        activeEngine = "managed-fallback";
        errorMessage = null;

        if (!TryEnsureLoaded(versionDir, provider, out errorMessage))
            return false;

        _inferenceLock.EnterReadLock();
        try
        {
            if (_disposed || _nativeSnapshot is null)
            {
                errorMessage = "ONNX runtime was unloaded.";
                return false;
            }

            var entry = GetOrCreateSession(modelPath, provider, fallbackClasses);
            activeEngine = entry.ActiveEngine;

            var (tensorData, scale, padX, padY) = LetterboxRgbToNchwFloat(
                pageBitmap.RgbPixels, pageBitmap.Width, pageBitmap.Height, entry.InputWidth, entry.InputHeight);

            var inputTensor = new DenseTensor<float>(tensorData, [1, 3, entry.InputHeight, entry.InputWidth]);
            using var results = entry.Session.Run([NamedOnnxValue.CreateFromTensor(entry.InputName, inputTensor)]);
            var first = results.FirstOrDefault();
            if (first is null) return true;

            var outTensor = first.AsTensor<float>();
            float[] flat = outTensor is DenseTensor<float> dense ? dense.Buffer.ToArray() : outTensor.ToArray();

            output = DecodeYoloOutput(
                flat, outTensor.Dimensions, entry.InputWidth, entry.InputHeight, scale, padX, padY,
                pageBitmap.Width, pageBitmap.Height, confidenceThreshold, iouThreshold, entry.CustomClasses);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        finally
        {
            _inferenceLock.ExitReadLock();
        }
    }

    public void UnloadAllForUninstall() { lock (_hostGate) UnloadHandlesLocked(); }

    public void Dispose()
    {
        lock (_hostGate)
        {
            if (_disposed) return;
            _disposed = true;
            UnloadHandlesLocked();
        }
    }

    private CachedSessionEntry GetOrCreateSession(string modelPath, string requestedProvider, IReadOnlyList<string>? fallbackClasses)
    {
        long ticks = File.GetLastWriteTimeUtc(modelPath).Ticks;
        string fullModelPath = Path.GetFullPath(modelPath);
        string fallbackKey = fallbackClasses is { Count: > 0 } ? string.Join(",", fallbackClasses) : string.Empty;
        string cacheKey = $"{fullModelPath}|{requestedProvider}|{ticks}|{fallbackKey}";

        if (_sessions.TryGetValue(cacheKey, out var existing))
            return existing;

        lock (_sessionCacheGate)
        {
            if (_sessions.TryGetValue(cacheKey, out existing))
                return existing;

            var (session, activeEngine) = CreateSessionWithProviderFallback(modelPath, requestedProvider);
            var inputMeta = session.InputMetadata.FirstOrDefault();
            string inputName = string.IsNullOrWhiteSpace(inputMeta.Key) ? "images" : inputMeta.Key;
            int[] dims = inputMeta.Value?.Dimensions ?? [1, 3, 1024, 1024];
            int inputH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 1024;
            int inputW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 1024;

            var embeddedClasses = ExtractClassesFromModelMetadata(session.ModelMetadata.CustomMetadataMap);
            var effectiveClasses = embeddedClasses is { Count: > 0 } ? embeddedClasses : (fallbackClasses is { Count: > 0 } ? fallbackClasses : null);

            var created = new CachedSessionEntry(session, activeEngine, inputName, inputW, inputH, effectiveClasses, ticks);
            _sessions[cacheKey] = created;
            return created;
        }
    }

    private static (InferenceSession Session, string ActiveEngine) CreateSessionWithProviderFallback(string modelPath, string requestedProvider)
    {
        if (string.Equals(requestedProvider, "directml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var dmlOptions = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };
                dmlOptions.AppendExecutionProvider_DML(0);
                return (new InferenceSession(modelPath, dmlOptions), "onnxruntime-directml");
            }
            catch { }
        }

        using var cpuOptions = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        return (new InferenceSession(modelPath, cpuOptions), "onnxruntime-cpu");
    }

    private static IReadOnlyList<string>? ExtractClassesFromModelMetadata(IDictionary<string, string>? customMeta)
    {
        if (customMeta is null || customMeta.Count == 0) return null;
        if (customMeta.TryGetValue("melomg_classes", out var rawCsv) &&
            TryParseClassNamesEntry("melomg_classes", rawCsv) is { Count: > 0 } csvParsed)
            return csvParsed;
        if (customMeta.TryGetValue("names", out var rawNames) &&
            TryParseClassNamesEntry("names", rawNames) is { Count: > 0 } dictParsed)
            return dictParsed;
        return null;
    }

    internal static IReadOnlyList<string>? TryParseClassNamesEntry(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(key, "melomg_classes", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parsed.Length > 0 ? parsed : null;
        }
        return string.Equals(key, "names", StringComparison.OrdinalIgnoreCase) ? ParsePythonDictClassNames(value) : null;
    }

    internal static IReadOnlyList<string>? ParsePythonDictClassNames(string rawNames)
    {
        var map = new Dictionary<int, string>();
        int maxIdx = -1;
        foreach (var part in rawNames.Trim('{', '}', ' ').Split(','))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string keyToken = part[..colon].Trim().Trim('\'', '"', ' ');
            if (!int.TryParse(keyToken, out int idx) || idx < 0) continue;
            string val = part[(colon + 1)..].Trim().Trim('\'', '"', ' ');
            if (!string.IsNullOrWhiteSpace(val))
            {
                map[idx] = val;
                if (idx > maxIdx) maxIdx = idx;
            }
        }
        if (map.Count == 0 || maxIdx > 4096) return null;

        var result = new string[maxIdx + 1];
        Array.Fill(result, string.Empty);
        foreach (var (k, v) in map) result[k] = v;
        return result;
    }

    private void UnloadHandlesLocked()
    {
        _nativeSnapshot = null;
        _inferenceLock.EnterWriteLock();
        try
        {
            lock (_sessionCacheGate)
            {
                foreach (var key in _sessions.Keys.ToList())
                    if (_sessions.TryRemove(key, out var entry)) entry.Session.Dispose();
            }

            while (_auxiliaryHandles.TryTake(out nint aux))
                if (aux != 0) { try { NativeLibrary.Free(aux); } catch { } }

            FreeHandle(ref _onnxRuntimeHandle);
            FreeHandle(ref _providersSharedHandle);
            FreeHandle(ref _directMlHandle);

            if (_dllDirectoryCookie != 0)
            {
                try { RemoveDllDirectory(_dllDirectoryCookie); } catch { }
                _dllDirectoryCookie = 0;
            }

            _loadedVersionDir = null;
            _loadedProvider = null;
        }
        finally
        {
            _inferenceLock.ExitWriteLock();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        static void FreeHandle(ref nint handle)
        {
            if (handle == 0) return;
            nint toFree = handle;
            handle = 0;
            try { NativeLibrary.Free(toFree); } catch { }
        }
    }

    private static void InstallDllImportResolverOnce(NativeOnnxRuntimeSessionHost host)
    {
        lock (ResolverGate)
        {
            s_activeHost = host;
            if (s_resolverInstalled) return;
            try { NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, ResolveOnnxNativeLibrary); }
            catch (InvalidOperationException) { }
            s_resolverInstalled = true;
        }
    }

    private static nint ResolveOnnxNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        var host = s_activeHost;
        var snapshot = host?._nativeSnapshot;
        if (host is null || snapshot is null || string.IsNullOrWhiteSpace(snapshot.VersionDir)) return 0;

        string cleanName = Path.GetFileNameWithoutExtension(libraryName);
        if (string.Equals(cleanName, "onnxruntime", StringComparison.OrdinalIgnoreCase) && snapshot.OnnxRuntimeHandle != 0)
            return snapshot.OnnxRuntimeHandle;

        string fileName = string.Equals(cleanName, "onnxruntime", StringComparison.OrdinalIgnoreCase)
            ? "onnxruntime.dll"
            : (libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : $"{libraryName}.dll");
        string candidate = Path.Combine(snapshot.VersionDir, fileName);

        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint depHandle))
        {
            host._auxiliaryHandles.Add(depHandle);
            return depHandle;
        }
        return 0;
    }

    public static (float[] TensorData, float Scale, float PadX, float PadY) LetterboxRgbToNchwFloat(
        byte[] rgbPixels, int srcWidth, int srcHeight, int targetWidth, int targetHeight)
    {
        float scale = Math.Min((float)targetWidth / Math.Max(1, srcWidth), (float)targetHeight / Math.Max(1, srcHeight));
        int scaledW = Math.Max(1, (int)Math.Round(srcWidth * scale));
        int scaledH = Math.Max(1, (int)Math.Round(srcHeight * scale));
        float padX = (targetWidth - scaledW) * 0.5f;
        float padY = (targetHeight - scaledH) * 0.5f;

        int plane = targetWidth * targetHeight;
        var tensor = new float[3 * plane];
        Array.Fill(tensor, 114f / 255f);

        int startX = Math.Clamp((int)Math.Floor(padX), 0, targetWidth - 1);
        int endX = Math.Clamp((int)Math.Ceiling(padX + scaledW), 0, targetWidth);
        int startY = Math.Clamp((int)Math.Floor(padY), 0, targetHeight - 1);
        int endY = Math.Clamp((int)Math.Ceiling(padY + scaledH), 0, targetHeight);
        const float inv255 = 1f / 255f;

        for (int ty = startY; ty < endY; ty++)
        {
            float sy = Math.Clamp((ty - padY) / scale, 0f, Math.Max(0, srcHeight - 1));
            int y0 = (int)Math.Floor(sy), y1 = Math.Min(y0 + 1, srcHeight - 1);
            float fy = sy - y0;

            for (int tx = startX; tx < endX; tx++)
            {
                float sx = Math.Clamp((tx - padX) / scale, 0f, Math.Max(0, srcWidth - 1));
                int x0 = (int)Math.Floor(sx), x1 = Math.Min(x0 + 1, srcWidth - 1);
                float fx = sx - x0;

                int i00 = (y0 * srcWidth + x0) * 3, i10 = (y0 * srcWidth + x1) * 3;
                int i01 = (y1 * srcWidth + x0) * 3, i11 = (y1 * srcWidth + x1) * 3;
                float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
                int dst = ty * targetWidth + tx;

                for (int c = 0; c < 3; c++)
                    tensor[c * plane + dst] = (rgbPixels[i00 + c] * w00 + rgbPixels[i10 + c] * w10 + rgbPixels[i01 + c] * w01 + rgbPixels[i11 + c] * w11) * inv255;
            }
        }

        return (tensor, scale, padX, padY);
    }

    public static NeuralLayoutOutput DecodeYoloOutput(
        ReadOnlySpan<float> data, ReadOnlySpan<int> dims, int inputW, int inputH, float scale, float padX, float padY,
        int origWidth, int origHeight, double confidenceThreshold, double iouThreshold, IReadOnlyList<string>? customClasses = null)
    {
        var rawFigures = new List<(BoundingBox Box, double Score, FigureCategory Category, string Label)>();
        var rawCaptions = new List<(BoundingBox Box, double Score)>();
        bool needsNms = false;

        if (dims.Length == 3 && dims[2] == 6 && dims[0] == 1 && customClasses is not { Count: 1 })
        {
            for (int r = 0; r < dims[1] && (long)r * 6 + 5 < data.Length; r++)
            {
                int o = r * 6;
                float score = data[o + 4];
                if (score < confidenceThreshold) continue;
                float x1 = data[o], y1 = data[o + 1], x2 = data[o + 2], y2 = data[o + 3];
                if (x2 <= 2f && y2 <= 2f && inputW > 32) { x1 *= inputW; x2 *= inputW; y1 *= inputH; y2 *= inputH; }
                ClassifyAndCollect(x1, y1, x2, y2, score, (int)Math.Round(data[o + 5]), inputW, inputH, scale, padX, padY, origWidth, origHeight, customClasses, rawFigures, rawCaptions);
            }
        }
        else if (dims.Length == 3 && dims[0] == 1 && dims[2] != 6 && dims[1] >= 5 && dims[1] <= 128 &&
                 (dims[2] > dims[1] || customClasses?.Count == dims[1] - 4))
        {
            needsNms = true;
            int channels = dims[1], anchors = dims[2], numClasses = channels - 4;
            int maxSafeAnchors = anchors > 0 ? Math.Min(anchors, data.Length / channels) : 0;

            for (int a = 0; a < maxSafeAnchors; a++)
            {
                int bestCls = 0;
                float bestProb = 0f;
                for (int c = 0; c < numClasses; c++)
                {
                    int idx = (4 + c) * anchors + a;
                    if ((uint)idx >= (uint)data.Length) break;
                    if (data[idx] > bestProb) { bestProb = data[idx]; bestCls = c; }
                }

                if (bestProb < confidenceThreshold || 3 * anchors + a >= data.Length) continue;
                CollectCenterBox(data[a], data[anchors + a], data[2 * anchors + a], data[3 * anchors + a], bestProb, bestCls);
            }
        }
        else if (dims.Length == 3 && dims[0] == 1 && dims[2] >= 6 && dims[2] <= 130)
        {
            needsNms = true;
            int rows = dims[1], stride = dims[2], numClasses = stride - 5;

            for (int r = 0; r < rows && (long)r * stride + stride - 1 < data.Length; r++)
            {
                int o = r * stride;
                float objConf = data[o + 4];
                if (objConf < 0.01f) continue;

                int bestCls = 0;
                float bestClsProb = 0f;
                for (int c = 0; c < numClasses; c++)
                {
                    float p = data[o + 5 + c];
                    if (p > bestClsProb) { bestClsProb = p; bestCls = c; }
                }

                float score = objConf * bestClsProb;
                if (score >= confidenceThreshold)
                    CollectCenterBox(data[o], data[o + 1], data[o + 2], data[o + 3], score, bestCls);
            }
        }

        void CollectCenterBox(float cx, float cy, float w, float h, float score, int cls)
        {
            if (w <= 2f && h <= 2f && inputW > 32) { cx *= inputW; w *= inputW; cy *= inputH; h *= inputH; }
            ClassifyAndCollect(cx - w * 0.5f, cy - h * 0.5f, cx + w * 0.5f, cy + h * 0.5f, score, cls, inputW, inputH, scale, padX, padY, origWidth, origHeight, customClasses, rawFigures, rawCaptions);
        }

        var keptFigures = new List<NeuralFigureBox>();
        double figNmsIou = needsNms ? iouThreshold : Math.Max(0.70, iouThreshold);
        foreach (var cand in rawFigures.OrderByDescending(f => f.Score))
        {
            if (!keptFigures.Any(k => k.NormalizedBox.ComputeIoU(cand.Box) > figNmsIou))
                keptFigures.Add(new NeuralFigureBox(cand.Box, Math.Round(cand.Score, 4), cand.Category, cand.Label));
        }

        var keptCaptions = new List<BoundingBox>();
        foreach (var cap in rawCaptions.OrderByDescending(c => c.Score))
        {
            if (!keptCaptions.Any(k => k.ComputeIoU(cap.Box) > 0.65))
                keptCaptions.Add(cap.Box);
        }

        return new NeuralLayoutOutput(keptFigures, keptCaptions);
    }

    private static void ClassifyAndCollect(
        float x1, float y1, float x2, float y2, float score, int classId,
        int inputW, int inputH, float scale, float padX, float padY, int origWidth, int origHeight,
        IReadOnlyList<string>? customClasses,
        List<(BoundingBox Box, double Score, FigureCategory Category, string Label)> figures,
        List<(BoundingBox Box, double Score)> captions)
    {
        float denomW = Math.Max(1f, origWidth * scale), denomH = Math.Max(1f, origHeight * scale);
        double nx1 = Math.Clamp((x1 - padX) / denomW, 0.0, 1.0), ny1 = Math.Clamp((y1 - padY) / denomH, 0.0, 1.0);
        double nx2 = Math.Clamp((x2 - padX) / denomW, 0.0, 1.0), ny2 = Math.Clamp((y2 - padY) / denomH, 0.0, 1.0);
        double nw = nx2 - nx1, nh = ny2 - ny1;
        if (nw < 0.015 || nh < 0.015) return;

        var normBox = new BoundingBox(nx1, ny1, nw, nh);
        var labels = customClasses is { Count: > 0 } ? customClasses : DocStructBenchClasses;
        if (classId < 0 || classId >= labels.Count) return;
        string rawLabel = labels[classId];
        string lower = rawLabel.Trim().ToLowerInvariant();

        if (lower is "figure_caption" or "picture_caption" or "image_caption" or "diagram_caption" or "chart_caption" or "caption")
        {
            captions.Add((normBox, score));
            return;
        }

        // Strict visual-image/diagram allowlist: tables, formulas, callout boxes, and text classes are never emitted.
        FigureCategory? category = lower switch
        {
            "figure" or "picture" or "image" or "photo" or "illustration" or "graphic" => FigureCategory.Figure,
            "diagram" or "flowchart" or "schematic" => FigureCategory.Diagram,
            "chart" or "plot" or "graph" => FigureCategory.Chart,
            _ => null
        };
        if (category is null) return;

        figures.Add((normBox, score, category.Value, rawLabel));
    }
}
