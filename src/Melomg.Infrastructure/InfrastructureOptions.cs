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
        "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/8ab23ff0b208d773c3791e38348c40c96aed3c90/doclayout_yolo_docstructbench_imgsz1024.onnx";

    public string? ExpectedDirectMlRuntimeSha256 { get; set; } =
        "6763468507b7cfc777b1334b3e174c11a540ddacb7bd4354bc2e0ec89e56eec2";
    public string? ExpectedDirectMlRedistSha256 { get; set; } =
        "9f07482559087088a4dba4ae76eeeee1fad3f7077a92ccfbdb439c6bc2964c09";
    public string? ExpectedCpuRuntimeSha256 { get; set; } =
        "9c32108e9a1c78b090a95c2a219f244dd5f067d4caf68fb0f967103f3f20839c";
    public string? ExpectedPretrainedModelSha256 { get; set; } =
        "fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21";
    public bool RequirePinnedSha256 { get; set; } = true;

    public TimeSpan AutoInstallConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public HttpMessageHandler? HttpMessageHandlerOverride { get; set; }

    private static string ResolveDefaultAppDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("MELOMG_APP_DATA")
            ?? Environment.GetEnvironmentVariable("MELOMG_APPDATA");
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
