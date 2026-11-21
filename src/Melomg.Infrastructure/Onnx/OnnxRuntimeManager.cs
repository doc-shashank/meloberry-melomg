using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Errors;
using Melomg.Infrastructure.Serialization;

namespace Melomg.Infrastructure.Onnx;

/// <summary>
/// Installs, verifies, updates, and removes AppData-hosted ONNX Runtime native DLLs and pretrained weights.
/// </summary>
public sealed class OnnxRuntimeManager : IOnnxRuntimeManager
{
    private const string PendingCleanupFileName = ".pending_cleanup";
    private const long MaxArchiveEntryBytes = 250L * 1024 * 1024;

    private readonly MelomgStorageOptions _options;
    private readonly OnnxProtobufEngine _onnxEngine;
    private readonly NativeOnnxRuntimeSessionHost _sessionHost;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _autoInstallFailedInProcess;

    public OnnxRuntimeManager(MelomgStorageOptions options, OnnxProtobufEngine onnxEngine, NativeOnnxRuntimeSessionHost sessionHost)
    {
        _options = options;
        _onnxEngine = onnxEngine;
        _sessionHost = sessionHost;
        TryFlushPendingCleanup();
    }

    public Task<OnnxRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        TryFlushPendingCleanup();
        return Task.FromResult(ReadCurrentStatusInternal());
    }

    public async Task<OnnxRuntimeStatus> EnsureReadyForInferenceAsync(
        bool allowAutoInstall, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var status = ReadCurrentStatusInternal();
        if (status.IsInstalled || !allowAutoInstall || !_options.AutoInstallRuntime || _autoInstallFailedInProcess)
            return status;

        var overallTimeout = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, (_options.AutoInstallConnectTimeout * 6).Ticks));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(overallTimeout);

        try
        {
            onProgress?.Invoke($"ONNX Runtime not found in '{_options.RuntimeRootDirectory}'. Downloading ({_options.DefaultRuntimeProvider})...");
            var res = await InstallAsync(
                new OnnxRuntimeInstallRequest(_options.DefaultRuntimeProvider, IncludeModel: true, Force: false), onProgress, cts.Token);
            if (res.IsSuccess) return res.Value;
            _autoInstallFailedInProcess = true;
            return status with { DiagnosticMessage = res.Error?.Description };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            _autoInstallFailedInProcess = true;
            return status with { DiagnosticMessage = $"Auto-install skipped (offline or timed out): {ex.Message}" };
        }
    }

    public async Task<Result<OnnxRuntimeStatus>> InstallAsync(
        OnnxRuntimeInstallRequest request, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        string provider = (request.Provider ?? _options.DefaultRuntimeProvider).Trim().ToLowerInvariant();
        if (provider is not ("directml" or "cpu"))
            return RuntimeErrors.InvalidProvider(request.Provider ?? string.Empty);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            TryFlushPendingCleanup();

            var existing = ReadCurrentStatusInternal();
            if (existing.IsInstalled && string.Equals(existing.InstalledProvider, provider, StringComparison.OrdinalIgnoreCase) && !request.Force)
            {
                if (request.IncludeModel && !existing.PretrainedModelInstalled)
                {
                    var modelOnlyRes = await DownloadAndValidatePretrainedModelAsync(onProgress, cancellationToken);
                    if (!modelOnlyRes.IsSuccess) return modelOnlyRes.Error!;
                }
                return Result<OnnxRuntimeStatus>.Success(ReadCurrentStatusInternal());
            }

            Directory.CreateDirectory(_options.RuntimeRootDirectory);
            string version = MelomgStorageOptions.PinnedOnnxRuntimeVersion;
            string stagingDir = Path.Combine(_options.RuntimeRootDirectory, $".staging-{Guid.NewGuid():N}");
            string targetVersionDir = Path.Combine(_options.RuntimeRootDirectory, version);
            string downloadsDir = Path.Combine(stagingDir, ".downloads");
            Directory.CreateDirectory(downloadsDir);

            string? backupVersionDir = null, backupManifestPath = null;
            bool movedIntoTarget = false;

            void RollbackTarget()
            {
                if (!movedIntoTarget) return;
                _sessionHost.UnloadAllForUninstall();
                TryDeleteFileSilent(_options.RuntimeManifestPath);
                TryDeleteDirectorySilent(targetVersionDir);
                if (backupVersionDir is not null && Directory.Exists(backupVersionDir))
                    try { Directory.Move(backupVersionDir, targetVersionDir); } catch { }
                if (backupManifestPath is not null && File.Exists(backupManifestPath))
                    try { File.Move(backupManifestPath, _options.RuntimeManifestPath, overwrite: true); } catch { }
            }

            try
            {
                onProgress?.Invoke($"Downloading Microsoft ONNX Runtime v{version} ({provider}) package...");
                string pkgUrl = provider == "directml" ? _options.DirectMlRuntimePackageUrl : _options.CpuRuntimePackageUrl;
                string? expectedRuntimeSha = provider == "directml" ? _options.ExpectedDirectMlRuntimeSha256 : _options.ExpectedCpuRuntimeSha256;

                string ortNupkgPath = Path.Combine(downloadsDir, "ort.nupkg");
                await DownloadToFileAsync(pkgUrl, ortNupkgPath, _options.AutoInstallConnectTimeout, expectedRuntimeSha, onProgress, "ONNX Runtime", cancellationToken);
                ExtractMatchingFilesFromZip(
                    ortNupkgPath,
                    e => { var n = e.FullName.Replace('\\', '/'); return n.Contains("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase); },
                    stagingDir);

                if (provider == "directml")
                {
                    onProgress?.Invoke($"Downloading Microsoft DirectML v{MelomgStorageOptions.PinnedDirectMlVersion} redistributable...");
                    string dmlNupkgPath = Path.Combine(downloadsDir, "dml.nupkg");
                    await DownloadToFileAsync(_options.DirectMlRedistPackageUrl, dmlNupkgPath, _options.AutoInstallConnectTimeout, _options.ExpectedDirectMlRedistSha256, onProgress, "DirectML", cancellationToken);
                    ExtractMatchingFilesFromZip(
                        dmlNupkgPath,
                        e => { var n = e.FullName.Replace('\\', '/'); return string.Equals(Path.GetFileName(n), "DirectML.dll", StringComparison.OrdinalIgnoreCase) && (n.Contains("x64-win", StringComparison.OrdinalIgnoreCase) || n.Contains("win-x64", StringComparison.OrdinalIgnoreCase)); },
                        stagingDir);
                }

                TryDeleteDirectorySilent(downloadsDir);

                if (!File.Exists(Path.Combine(stagingDir, "onnxruntime.dll")))
                    return RuntimeErrors.InstallFailed("Downloaded package archive did not contain runtimes/win-x64/native/onnxruntime.dll.");
                if (provider == "directml" && !File.Exists(Path.Combine(stagingDir, "DirectML.dll")))
                    return RuntimeErrors.InstallFailed("Downloaded DirectML archive did not contain DirectML.dll.");

                var manifestFiles = new List<OnnxRuntimeManifestFile>();
                foreach (var dllPath in Directory.GetFiles(stagingDir, "*.dll").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(dllPath);
                    if (!HasPeMagic(dllPath))
                        return RuntimeErrors.InstallFailed($"Downloaded binary '{fi.Name}' failed PE executable header validation.");
                    manifestFiles.Add(new OnnxRuntimeManifestFile(fi.Name, fi.Length, ComputeSha256Hex(dllPath)));
                }

                _sessionHost.UnloadAllForUninstall();
                if (Directory.Exists(targetVersionDir))
                {
                    backupVersionDir = $"{targetVersionDir}.bak-{Guid.NewGuid():N}";
                    Directory.Move(targetVersionDir, backupVersionDir);
                }
                if (File.Exists(_options.RuntimeManifestPath))
                {
                    backupManifestPath = $"{_options.RuntimeManifestPath}.bak-{Guid.NewGuid():N}";
                    File.Move(_options.RuntimeManifestPath, backupManifestPath, overwrite: true);
                }

                Directory.Move(stagingDir, targetVersionDir);
                movedIntoTarget = true;

                await File.WriteAllTextAsync(
                    _options.RuntimeManifestPath,
                    JsonSerializer.Serialize(new OnnxRuntimeManifest(version, provider, TimeProvider.System.GetUtcNow(), manifestFiles), RuntimeManifestJsonContext.Default.OnnxRuntimeManifest),
                    cancellationToken);

                if (_options.VerifyNativeLoadOnInstall && !_sessionHost.TryEnsureLoaded(targetVersionDir, provider, out var loadErr))
                {
                    RollbackTarget();
                    return RuntimeErrors.LoadFailed(loadErr ?? "Unknown native load error.");
                }

                if (request.IncludeModel && (request.Force || !File.Exists(_options.PretrainedModelPath)))
                {
                    var modelRes = await DownloadAndValidatePretrainedModelAsync(onProgress, cancellationToken);
                    if (!modelRes.IsSuccess) { RollbackTarget(); return modelRes.Error!; }
                }

                _autoInstallFailedInProcess = false;
                return Result<OnnxRuntimeStatus>.Success(ReadCurrentStatusInternal());
            }
            catch (OperationCanceledException) { RollbackTarget(); throw; }
            catch (Exception ex) { RollbackTarget(); return RuntimeErrors.InstallFailed(ex.Message); }
            finally
            {
                if (backupVersionDir is not null) TryDeleteDirectorySilent(backupVersionDir);
                if (backupManifestPath is not null) TryDeleteFileSilent(backupManifestPath);
                TryDeleteDirectorySilent(stagingDir);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<Result<OnnxRuntimeUninstallResult>> UninstallAsync(bool keepModel = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _sessionHost.UnloadAllForUninstall();
            _autoInstallFailedInProcess = false;

            bool removedRuntime = false, removedModel = false;
            int removedCount = 0;
            long freedBytes = 0;
            var pending = new List<string>();

            if (Directory.Exists(_options.RuntimeRootDirectory))
            {
                foreach (var file in Directory.GetFiles(_options.RuntimeRootDirectory, "*", SearchOption.AllDirectories))
                {
                    long len = new FileInfo(file).Length;
                    if (TryDeleteFileWithLockTracking(file, pending)) { removedCount++; freedBytes += len; removedRuntime = true; }
                }

                TryDeleteDirectorySilent(_options.RuntimeRootDirectory);
                var runtimeParent = Path.GetDirectoryName(_options.RuntimeRootDirectory);
                if (!string.IsNullOrEmpty(runtimeParent)) TryDeleteEmptyParentChain(runtimeParent, _options.AppDataDirectory);
                if (!Directory.Exists(_options.RuntimeRootDirectory)) removedRuntime = true;
            }

            if (Directory.Exists(_options.ModelsDirectory))
            {
                foreach (var part in Directory.GetFiles(_options.ModelsDirectory, "*.part", SearchOption.TopDirectoryOnly))
                {
                    long len = new FileInfo(part).Length;
                    if (TryDeleteFileWithLockTracking(part, pending)) { removedCount++; freedBytes += len; }
                }

                if (!keepModel && File.Exists(_options.PretrainedModelPath))
                {
                    long len = new FileInfo(_options.PretrainedModelPath).Length;
                    if (TryDeleteFileWithLockTracking(_options.PretrainedModelPath, pending)) { removedCount++; freedBytes += len; removedModel = true; }
                }
            }

            if (pending.Count > 0)
            {
                Directory.CreateDirectory(_options.AppDataDirectory);
                await File.WriteAllLinesAsync(PendingCleanupFilePath, pending, cancellationToken);
            }

            return Result<OnnxRuntimeUninstallResult>.Success(new OnnxRuntimeUninstallResult(
                removedRuntime, removedModel, removedCount, freedBytes, _options.RuntimeRootDirectory, false, pending));
        }
        catch (Exception ex) { return RuntimeErrors.UninstallFailed(ex.Message); }
        finally { _gate.Release(); }
    }

    private string PendingCleanupFilePath => Path.Combine(_options.AppDataDirectory, PendingCleanupFileName);

    private OnnxRuntimeStatus ReadCurrentStatusInternal()
    {
        bool pretrainedExists = File.Exists(_options.PretrainedModelPath);
        long modelBytes = pretrainedExists ? new FileInfo(_options.PretrainedModelPath).Length : 0;

        OnnxRuntimeManifest? manifest = null;
        if (File.Exists(_options.RuntimeManifestPath))
        {
            try { manifest = JsonSerializer.Deserialize(File.ReadAllText(_options.RuntimeManifestPath), RuntimeManifestJsonContext.Default.OnnxRuntimeManifest); }
            catch { manifest = null; }
        }

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Provider) ||
            manifest.Files is null)
        {
            if (File.Exists(_options.RuntimeManifestPath))
            {
                return new OnnxRuntimeStatus(
                    false, "corrupt", "managed-fallback", manifest?.Version, _options.RuntimeRootDirectory, null, null,
                    pretrainedExists, _options.PretrainedModelPath, modelBytes, [],
                    "Runtime manifest is corrupt or missing required fields; run 'melomg runtime install --force'.");
            }

            var baseFiles = pretrainedExists
                ? new List<OnnxRuntimeFileEntry> { new(Path.GetFileName(_options.PretrainedModelPath), _options.PretrainedModelPath, modelBytes, string.Empty) }
                : [];
            return new OnnxRuntimeStatus(
                false, "none", "managed-fallback", null, _options.RuntimeRootDirectory, null, null,
                pretrainedExists, _options.PretrainedModelPath, modelBytes, baseFiles);
        }

        string versionDir = Path.Combine(_options.RuntimeRootDirectory, manifest.Version);
        bool validVersionDir = IsPathInsideDirectory(versionDir, _options.RuntimeRootDirectory);
        var entries = new List<OnnxRuntimeFileEntry>();
        long totalBytes = 0;
        bool allDllsPresent = validVersionDir && manifest.Files.Count > 0;
        bool integrityMismatch = !validVersionDir;

        if (validVersionDir)
        {
            foreach (var mf in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(mf?.FileName))
                {
                    allDllsPresent = false;
                    integrityMismatch = true;
                    continue;
                }
                string full = Path.Combine(versionDir, mf.FileName);
                if (!IsPathInsideDirectory(full, versionDir))
                {
                    allDllsPresent = false;
                    integrityMismatch = true;
                    continue;
                }
                if (!File.Exists(full)) { allDllsPresent = false; continue; }

                long sz = new FileInfo(full).Length;
                string actualSha = ComputeSha256Hex(full);
                if ((mf.SizeBytes > 0 && sz != mf.SizeBytes) ||
                    (!string.IsNullOrWhiteSpace(mf.Sha256) && !string.Equals(actualSha, mf.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    allDllsPresent = false;
                    integrityMismatch = true;
                }
                totalBytes += sz;
                entries.Add(new OnnxRuntimeFileEntry(mf.FileName, full, sz, actualSha));
            }
        }

        if (File.Exists(_options.RuntimeManifestPath))
        {
            long mSz = new FileInfo(_options.RuntimeManifestPath).Length;
            totalBytes += mSz;
            entries.Add(new OnnxRuntimeFileEntry("manifest.json", _options.RuntimeManifestPath, mSz, string.Empty));
        }
        if (pretrainedExists)
        {
            totalBytes += modelBytes;
            entries.Add(new OnnxRuntimeFileEntry(Path.GetFileName(_options.PretrainedModelPath), _options.PretrainedModelPath, modelBytes, string.Empty));
        }

        string provider = manifest.Provider.ToLowerInvariant();
        string engine = allDllsPresent ? (provider == "directml" ? "onnxruntime-directml" : "onnxruntime-cpu") : "managed-fallback";

        return new OnnxRuntimeStatus(
            allDllsPresent, allDllsPresent ? provider : "corrupt", engine, manifest.Version, versionDir, null, null,
            pretrainedExists, _options.PretrainedModelPath, totalBytes, entries,
            allDllsPresent ? null : (integrityMismatch
                ? "One or more runtime DLLs failed size or SHA-256 verification; run 'melomg runtime install --force'."
                : "One or more runtime DLLs are missing; run 'melomg runtime install --force'."));
    }

    private async Task<Result> DownloadAndValidatePretrainedModelAsync(Action<string>? onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.ModelsDirectory);
        string tempPart = Path.Combine(_options.ModelsDirectory, $".doclayout-{Guid.NewGuid():N}.part");
        try
        {
            onProgress?.Invoke($"Downloading DocLayout-YOLO ONNX model ({Domain.Models.OnnxModelProfile.PretrainedModelName})...");
            await DownloadToFileAsync(
                _options.PretrainedModelDownloadUrl, tempPart, TimeSpan.FromSeconds(120),
                _options.ExpectedPretrainedModelSha256, onProgress, "DocLayout-YOLO model", cancellationToken);

            var inspected = await _onnxEngine.InspectAndValidateModelAsync(tempPart, cancellationToken);
            if (!inspected.IsSuccess) return inspected.Error!;

            if (File.Exists(_options.PretrainedModelPath)) File.Delete(_options.PretrainedModelPath);
            File.Move(tempPart, _options.PretrainedModelPath);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return RuntimeErrors.InstallFailed($"Failed downloading pretrained model: {ex.Message}"); }
        finally { TryDeleteFileSilent(tempPart); }
    }

    private async Task DownloadToFileAsync(
        string url, string destinationPath, TimeSpan connectTimeout, string? expectedSha256,
        Action<string>? onProgress, string label, CancellationToken cancellationToken)
    {
        using var client = _options.HttpMessageHandlerOverride is not null
            ? new HttpClient(_options.HttpMessageHandlerOverride, disposeHandler: false)
            : new HttpClient(new SocketsHttpHandler { ConnectTimeout = connectTimeout });

        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("melomg/1.0");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalLength = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[65536];
        long downloaded = 0;
        int lastPercentReported = -1, read;
        await using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                downloaded += read;
                if (onProgress is not null && totalLength > 0)
                {
                    int pct = (int)(downloaded * 100 / totalLength.Value);
                    if (pct >= lastPercentReported + 25 || pct == 100)
                    {
                        lastPercentReported = pct;
                        onProgress($"  {label}: {pct}% ({downloaded / 1024.0 / 1024.0:F1} MB)");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actualSha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actualSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} SHA-256 verification failed.");
        }
    }

    private static void ExtractMatchingFilesFromZip(string zipFilePath, Func<ZipArchiveEntry, bool> predicate, string destDirectory)
    {
        using var fs = File.OpenRead(zipFilePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var buf = new byte[65536];
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || !predicate(entry)) continue;
            string fileName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            string destPath = Path.Combine(destDirectory, fileName);
            using var entryStream = entry.Open();
            using var outFs = File.Create(destPath);
            long totalWritten = 0;
            int read;
            while ((read = entryStream.Read(buf, 0, buf.Length)) > 0)
            {
                totalWritten += read;
                if (totalWritten > MaxArchiveEntryBytes)
                    throw new InvalidDataException($"Archive entry '{fileName}' exceeded maximum uncompressed size limit ({MaxArchiveEntryBytes} bytes).");
                outFs.Write(buf, 0, read);
            }
        }
    }

    internal static bool IsPathInsideDirectory(string candidatePath, string rootDirectory)
    {
        string fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCand = Path.GetFullPath(candidatePath);
        return fullCand.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPeMagic(string filePath)
    {
        Span<byte> hdr = stackalloc byte[2];
        using var fs = File.OpenRead(filePath);
        return fs.Read(hdr) == 2 && hdr[0] == (byte)'M' && hdr[1] == (byte)'Z';
    }

    private static string ComputeSha256Hex(string filePath) { using var fs = File.OpenRead(filePath); return Convert.ToHexStringLower(SHA256.HashData(fs)); }

    private static bool TryDeleteFileWithLockTracking(string path, List<string> pending)
    {
        try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { pending.Add(path); return false; }
    }

    private void TryFlushPendingCleanup()
    {
        try
        {
            if (!File.Exists(PendingCleanupFilePath)) return;
            var remaining = new List<string>();
            foreach (var line in File.ReadAllLines(PendingCleanupFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string full = Path.GetFullPath(line.Trim());
                bool allowed = IsPathInsideDirectory(full, _options.RuntimeBaseDirectory) ||
                               IsPathInsideDirectory(full, _options.ModelsDirectory);
                if (!allowed || !File.Exists(full)) continue;
                TryDeleteFileWithLockTracking(full, remaining);
            }

            if (remaining.Count == 0)
            {
                File.Delete(PendingCleanupFilePath);
                if (!File.Exists(_options.RuntimeManifestPath))
                {
                    TryDeleteDirectorySilent(_options.RuntimeRootDirectory);
                    TryDeleteEmptyParentChain(_options.RuntimeBaseDirectory, _options.AppDataDirectory);
                }
            }
            else
            {
                File.WriteAllLines(PendingCleanupFilePath, remaining);
            }
        }
        catch { }
    }

    private static void TryDeleteFileSilent(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectorySilent(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private static void TryDeleteEmptyParentChain(string dir, string stopAtParent)
    {
        try
        {
            string stopFull = Path.GetFullPath(stopAtParent);
            while (Directory.Exists(dir) &&
                   !string.Equals(Path.GetFullPath(dir), stopFull, StringComparison.OrdinalIgnoreCase) &&
                   !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir) ?? string.Empty;
                if (string.IsNullOrEmpty(dir)) break;
            }
        }
        catch { }
    }
}
