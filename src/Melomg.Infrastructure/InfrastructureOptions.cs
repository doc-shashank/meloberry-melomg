using Melomg.Application.Abstractions;
using Melomg.Domain.Models;

namespace Melomg.Infrastructure;

public sealed class MelomgStorageOptions
{
    public const string PinnedOnnxRuntimeVersion = "1.20.1";
    public const string PinnedDirectMlVersion = "1.15.2";

    public string AppDataDirectory { get; set; } = ResolveDefaultAppDataDirectory();
    public string SettingsDbPath => Path.Combine(AppDataDirectory, "melomg_settings.db");
    public string ModelsDirectory => Path.Combine(AppDataDirectory, "models");
    public string BuiltInModelPath => Path.Combine(ModelsDirectory, OnnxModelProfile.BuiltInModelName);
    public string PretrainedModelPath => Path.Combine(ModelsDirectory, OnnxModelProfile.PretrainedModelName);
    public string RuntimeBaseDirectory => Path.Combine(AppDataDirectory, "runtime");
    public string RuntimeRootDirectory => Path.Combine(RuntimeBaseDirectory, "onnxruntime", "win-x64");
    public string RuntimeManifestPath => Path.Combine(RuntimeRootDirectory, "manifest.json");

    public bool AutoInstallRuntime { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("MELOMG_OFFLINE"), "1", StringComparison.OrdinalIgnoreCase);
    public string DefaultRuntimeProvider { get; set; } = "directml";
    public bool VerifyNativeLoadOnInstall { get; set; } = true;

    public string DirectMlRuntimePackageUrl { get; set; } =
        "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.20.1/microsoft.ml.onnxruntime.directml.1.20.1.nupkg";
    public string DirectMlRedistPackageUrl { get; set; } =
        "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.2/microsoft.ai.directml.1.15.2.nupkg";
    public string CpuRuntimePackageUrl { get; set; } =
        "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/1.20.1/microsoft.ml.onnxruntime.1.20.1.nupkg";
    public string PretrainedModelDownloadUrl { get; set; } =
        "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx";

    public string? ExpectedDirectMlRuntimeSha256 { get; set; }
    public string? ExpectedDirectMlRedistSha256 { get; set; }
    public string? ExpectedCpuRuntimeSha256 { get; set; }
    public string? ExpectedPretrainedModelSha256 { get; set; }

    public TimeSpan AutoInstallConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public HttpMessageHandler? HttpMessageHandlerOverride { get; set; }

    private static string ResolveDefaultAppDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("MELOMG_APPDATA");
        return !string.IsNullOrWhiteSpace(overrideDir)
            ? Path.GetFullPath(overrideDir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meloberry");
    }
}

public sealed class WorkingDirectoryProvider : IWorkingDirectoryProvider
{
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();
    public string NormalizeDirectoryPath(string? explicitPath) =>
        string.IsNullOrWhiteSpace(explicitPath) ? Directory.GetCurrentDirectory() : Path.GetFullPath(explicitPath.Trim());
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
}
