using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Domain.Models;

public sealed class OnnxModelProfile
{
    public const string BuiltInModelName = "melomg_doclayout_v1.onnx";
    public const string PretrainedModelName = "doclayout_yolo_docstructbench_imgsz1024.onnx";
    public const int PretrainedInputSize = 1024;
    public const double DefaultConfidenceThreshold = 0.35;
    public const double DefaultIouThreshold = 0.45;
    public const int DefaultInputSize = 640;
    public const int DefaultOpsetVersion = 17;
    private static readonly string[] DefaultClasses = ["figure", "diagram", "chart"];

    private readonly List<string> _supportedClasses;

    private OnnxModelProfile(
        string modelName, string modelPath, bool isBuiltIn, double confidenceThreshold, double iouThreshold,
        int inputWidth, int inputHeight, int opsetVersion, long irVersion, List<string> supportedClasses)
    {
        ModelName = modelName; ModelPath = modelPath; IsBuiltIn = isBuiltIn;
        ConfidenceThreshold = confidenceThreshold; IouThreshold = iouThreshold;
        InputWidth = inputWidth; InputHeight = inputHeight;
        OpsetVersion = opsetVersion; IrVersion = irVersion;
        _supportedClasses = supportedClasses;
    }

    public string ModelName { get; private set; }
    public string ModelPath { get; private set; }
    public bool IsBuiltIn { get; private set; }
    public double ConfidenceThreshold { get; private set; }
    public double IouThreshold { get; private set; }
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public int OpsetVersion { get; private set; }
    public long IrVersion { get; private set; }
    public IReadOnlyList<string> SupportedClasses => _supportedClasses;

    public static OnnxModelProfile CreateDefault(string builtInModelPath) =>
        new(BuiltInModelName, builtInModelPath, true, DefaultConfidenceThreshold, DefaultIouThreshold,
            DefaultInputSize, DefaultInputSize, DefaultOpsetVersion, 9, [.. DefaultClasses]);

    public static Result<OnnxModelProfile> CreateCustom(
        string modelName, string modelPath, double confidenceThreshold, double iouThreshold,
        int inputWidth, int inputHeight, int opsetVersion, long irVersion, IEnumerable<string>? supportedClasses = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var confCheck = ValidateConfidence(confidenceThreshold);
        if (!confCheck.IsSuccess) return confCheck.Error!;
        var iouCheck = ValidateIou(iouThreshold);
        if (!iouCheck.IsSuccess) return iouCheck.Error!;
        if (inputWidth <= 0 || inputHeight <= 0)
            return ModelErrors.InvalidModelFormat(modelPath, "Input tensor dimensions must be positive.");

        var classes = supportedClasses?.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (classes.Count == 0) classes = [.. DefaultClasses];

        return new OnnxModelProfile(
            modelName.Trim(), modelPath.Trim(), false, Math.Round(confidenceThreshold, 4),
            Math.Round(iouThreshold, 4), inputWidth, inputHeight, opsetVersion, irVersion, classes);
    }

    public static OnnxModelProfile Rehydrate(
        string modelName, string modelPath, bool isBuiltIn, double confidenceThreshold, double iouThreshold,
        int inputWidth, int inputHeight, int opsetVersion, long irVersion, IEnumerable<string> supportedClasses) =>
        new(modelName, modelPath, isBuiltIn, confidenceThreshold, iouThreshold,
            inputWidth, inputHeight, opsetVersion, irVersion, supportedClasses.ToList());

    public Result SetThresholds(double confidence, double? iou = null)
    {
        var confCheck = ValidateConfidence(confidence);
        if (!confCheck.IsSuccess) return confCheck.Error!;
        if (iou.HasValue)
        {
            var iouCheck = ValidateIou(iou.Value);
            if (!iouCheck.IsSuccess) return iouCheck.Error!;
            IouThreshold = Math.Round(iou.Value, 4);
        }
        ConfidenceThreshold = Math.Round(confidence, 4);
        return Result.Success();
    }

    private static Result ValidateConfidence(double v) =>
        double.IsNaN(v) || v < 0.05 || v > 0.99 ? ModelErrors.InvalidThreshold("confidence", v, 0.05, 0.99) : Result.Success();

    private static Result ValidateIou(double v) =>
        double.IsNaN(v) || v < 0.10 || v > 0.95 ? ModelErrors.InvalidThreshold("IoU", v, 0.10, 0.95) : Result.Success();
}
