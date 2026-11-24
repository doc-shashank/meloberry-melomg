using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Runtime;
using Melomg.Domain.Documents;
using Melomg.Infrastructure;
using Melomg.Infrastructure.Onnx;
using Melomg.Infrastructure.Pdf;
using Melomg.Infrastructure.Persistence;
using Melomg.Infrastructure.Services;
using Xunit;

namespace Melomg.UnitTests;

public sealed class OnnxRuntimeAndYoloTests
{
    [Fact]
    public async Task RuntimeManager_InstallsDirectMlAndModel_PromotesProfile_And_UninstallsCleanly()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            byte[] ortPkg = BuildZipArchive(
                ("runtimes/win-x64/native/onnxruntime.dll", BuildFakePeBytes("ORT_MAIN")),
                ("runtimes/win-x64/native/onnxruntime_providers_shared.dll", BuildFakePeBytes("ORT_SHARED")));
            byte[] dmlPkg = BuildZipArchive(
                ("bin/x64-win/DirectML.dll", BuildFakePeBytes("DIRECTML_BIN")));
            byte[] modelBytes = OnnxProtobufEngine.BuildCompactDocLayoutOnnxModel(1024, 1024, 17);

            using var handler = new MockDownloadHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://test.local/ort-dml.nupkg"] = ortPkg,
                ["https://test.local/ort-cpu.nupkg"] = ortPkg,
                ["https://test.local/dml-redist.nupkg"] = dmlPkg,
                ["https://test.local/doclayout.onnx"] = modelBytes
            });

            var opts = new MelomgStorageOptions
            {
                AppDataDirectory = Path.Combine(tempRoot, "appdata"),
                VerifyNativeLoadOnInstall = false,
                DirectMlRuntimePackageUrl = "https://test.local/ort-dml.nupkg",
                DirectMlRedistPackageUrl = "https://test.local/dml-redist.nupkg",
                CpuRuntimePackageUrl = "https://test.local/ort-cpu.nupkg",
                PretrainedModelDownloadUrl = "https://test.local/doclayout.onnx",
                ExpectedDirectMlRuntimeSha256 = Sha256Hex(ortPkg),
                ExpectedDirectMlRedistSha256 = Sha256Hex(dmlPkg),
                ExpectedCpuRuntimeSha256 = Sha256Hex(ortPkg),
                ExpectedPretrainedModelSha256 = Sha256Hex(modelBytes),
                HttpMessageHandlerOverride = handler
            };

            var onnxEngine = new OnnxProtobufEngine(opts);
            using var sessionHost = new NativeOnnxRuntimeSessionHost();
            var rtManager = new OnnxRuntimeManager(opts, onnxEngine, sessionHost);
            var settingsRepo = new SqliteModelSettingsRepository(opts, onnxEngine);

            var initial = await rtManager.GetStatusAsync();
            Assert.False(initial.IsInstalled);
            Assert.Equal("managed-fallback", initial.ActiveInferenceEngine);

            var installHandler = new InstallRuntimeHandler(rtManager, settingsRepo, onnxEngine);
            var progress = new List<string>();
            var installRes = await installHandler.HandleAsync(new InstallRuntimeCommand("directml", IncludeModel: true), progress.Add);

            Assert.True(installRes.IsSuccess, installRes.Error?.Description);
            var status = installRes.Value;
            Assert.True(status.IsInstalled);
            Assert.Equal("directml", status.InstalledProvider);
            Assert.Equal("onnxruntime-directml", status.ActiveInferenceEngine);
            Assert.True(status.PretrainedModelInstalled);
            Assert.True(File.Exists(Path.Combine(status.RuntimeDirectory, "onnxruntime.dll")));
            Assert.True(File.Exists(Path.Combine(status.RuntimeDirectory, "DirectML.dll")));
            Assert.True(File.Exists(opts.RuntimeManifestPath));
            Assert.True(File.Exists(opts.PretrainedModelPath));
            Assert.Contains(status.Files, f => f.FileName == "onnxruntime.dll" && f.Sha256.Length == 64);

            // Built-in profile should have been automatically promoted to the downloaded DocLayout-YOLO model
            var activeProfile = await settingsRepo.GetActiveModelProfileAsync();
            Assert.False(activeProfile.IsBuiltIn);
            Assert.Equal(Path.GetFullPath(opts.PretrainedModelPath), Path.GetFullPath(activeProfile.ModelPath));

            // KeepModel=true removes runtime binaries while preserving downloaded model
            var uninstallHandler = new UninstallRuntimeHandler(rtManager, settingsRepo);
            var keepRes = await uninstallHandler.HandleAsync(new UninstallRuntimeCommand(KeepModel: true));
            Assert.True(keepRes.IsSuccess);
            Assert.True(keepRes.Value.RemovedRuntime);
            Assert.False(keepRes.Value.RemovedPretrainedModel);
            Assert.False(Directory.Exists(opts.RuntimeRootDirectory));
            Assert.True(File.Exists(opts.PretrainedModelPath));

            // Re-install CPU runtime with --skip-model, then full uninstall removes model and resets active profile
            var cpuRes = await installHandler.HandleAsync(new InstallRuntimeCommand("cpu", IncludeModel: false));
            Assert.True(cpuRes.IsSuccess);
            Assert.Equal("cpu", cpuRes.Value.InstalledProvider);
            Assert.Equal("onnxruntime-cpu", cpuRes.Value.ActiveInferenceEngine);
            Assert.False(File.Exists(Path.Combine(cpuRes.Value.RuntimeDirectory, "DirectML.dll")));

            var fullUninstall = await uninstallHandler.HandleAsync(new UninstallRuntimeCommand(KeepModel: false));
            Assert.True(fullUninstall.IsSuccess);
            Assert.True(fullUninstall.Value.RemovedRuntime);
            Assert.True(fullUninstall.Value.RemovedPretrainedModel);
            Assert.True(fullUninstall.Value.ResetModelToFallback);
            Assert.False(Directory.Exists(opts.RuntimeBaseDirectory));
            Assert.False(File.Exists(opts.PretrainedModelPath));

            var revertedProfile = await settingsRepo.GetActiveModelProfileAsync();
            Assert.True(revertedProfile.IsBuiltIn);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task RuntimeManager_RejectsInvalidProvider_And_FallsBackGracefullyWhenOffline()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            using var failingHandler = new MockDownloadHandler(_ => throw new HttpRequestException("Simulated offline network."));
            var opts = new MelomgStorageOptions
            {
                AppDataDirectory = Path.Combine(tempRoot, "appdata"),
                VerifyNativeLoadOnInstall = false,
                AutoInstallRuntime = true,
                DirectMlRuntimePackageUrl = "https://offline.local/ort.nupkg",
                HttpMessageHandlerOverride = failingHandler
            };

            var onnxEngine = new OnnxProtobufEngine(opts);
            using var sessionHost = new NativeOnnxRuntimeSessionHost();
            var rtManager = new OnnxRuntimeManager(opts, onnxEngine, sessionHost);
            var settingsRepo = new SqliteModelSettingsRepository(opts, onnxEngine);

            // Auto-install attempt on network failure degrades gracefully to managed-fallback without throwing
            var ensureStatus = await rtManager.EnsureReadyForInferenceAsync(allowAutoInstall: true);
            Assert.False(ensureStatus.IsInstalled);
            Assert.Equal("managed-fallback", ensureStatus.ActiveInferenceEngine);
            Assert.Contains("offline", ensureStatus.DiagnosticMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Invalid provider returns validation error cleanly
            var installHandler = new InstallRuntimeHandler(rtManager, settingsRepo, onnxEngine);
            var badRes = await installHandler.HandleAsync(new InstallRuntimeCommand("vulkan"));
            Assert.False(badRes.IsSuccess);
            Assert.Equal("Runtime.InvalidProvider", badRes.Error!.Code);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public void NativeOnnxRuntimeSessionHost_LetterboxAndDecodeYolo_HandlesYoloV10AndYoloV8()
    {
        // 1. Portrait RGB image 400x600 -> letterboxed into 1024x1024 (scale = 1024/600, padX = (1024 - 683)/2 = 170.5)
        var rgb = new byte[400 * 600 * 3];
        Array.Fill(rgb, (byte)200);
        var (tensor, scale, padX, padY) = NativeOnnxRuntimeSessionHost.LetterboxRgbToNchwFloat(rgb, 400, 600, 1024, 1024);

        Assert.Equal(3 * 1024 * 1024, tensor.Length);
        Assert.Equal(1024f / 600f, scale, 3);
        Assert.True(padX > 165f && padX < 175f);
        Assert.Equal(0f, tensor[0] - (114f / 255f), 2); // top-left margin padded with 114/255

        // 2. YOLOv10 [1, 7, 6] output with DocStructBench classes:
        //    row 0: figure (cls 3, score 0.91) -> kept
        //    row 1: plain text (cls 1, score 0.95) -> ignored
        //    row 2: figure_caption (cls 4, score 0.88) -> must appear in CaptionNormalizedBoxes
        //    row 3: table (cls 5, score 0.84) -> structurally dropped (0 figures)
        //    row 4: table_caption (cls 6, score 0.89) -> structurally dropped (0 captions)
        //    row 5: isolate_formula (cls 8, score 0.92) -> structurally dropped (0 figures)
        //    row 6: formula_caption (cls 9, score 0.87) -> structurally dropped (0 captions)
        float x1 = padX + 50f * scale;
        float x2 = padX + 350f * scale;
        var v10Out = new float[]
        {
            x1, 100f * scale, x2, 400f * scale, 0.91f, 3f,
            x1, 20f  * scale, x2, 80f  * scale, 0.95f, 1f,
            x1, 410f * scale, x2, 460f * scale, 0.88f, 4f,
            x1, 500f * scale, x2, 750f * scale, 0.84f, 5f,
            x1, 470f * scale, x2, 495f * scale, 0.89f, 6f,
            x1, 760f * scale, x2, 820f * scale, 0.92f, 8f,
            x1, 825f * scale, x2, 850f * scale, 0.87f, 9f
        };

        var decodedV10 = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
            v10Out,
            [1, 7, 6],
            1024,
            1024,
            scale,
            padX,
            padY,
            400,
            600,
            confidenceThreshold: 0.35,
            iouThreshold: 0.45);

        Assert.Single(decodedV10.Figures);
        Assert.Equal(FigureCategory.Figure, decodedV10.Figures[0].Category);
        Assert.Equal(0.91, decodedV10.Figures[0].Confidence, 2);
        Assert.InRange(decodedV10.Figures[0].NormalizedBox.X, 0.11, 0.14);
        Assert.InRange(decodedV10.Figures[0].NormalizedBox.Width, 0.72, 0.77);
        Assert.Single(decodedV10.CaptionNormalizedBoxes);

        // Custom model profile with disallowed class label ("callout_box") also yields 0 figures
        var boxDecoded = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
            [512f, 512f, 200f, 150f, 0.9f, 0.9f],
            [1, 1, 6],
            1024, 1024, 1f, 0f, 0f, 1024, 1024,
            0.35, 0.45,
            customClasses: ["callout_box"]);
        Assert.Empty(boxDecoded.Figures);

        // 3. YOLOv8 transposed [1, 4 + 2, 2] where anchor 0 & anchor 1 heavily overlap (conf 0.89 vs 0.62) -> NMS keeps 1
        var v8Out = new float[6 * 2];
        // cx, cy, w, h for anchor 0 & anchor 1
        v8Out[0] = 512f; v8Out[1] = 515f;
        v8Out[2] = 512f; v8Out[3] = 512f;
        v8Out[4] = 300f; v8Out[5] = 295f;
        v8Out[6] = 250f; v8Out[7] = 248f;
        // class 0 = "figure", class 1 = "chart"
        v8Out[8] = 0.89f; v8Out[9] = 0.62f;
        v8Out[10] = 0.05f; v8Out[11] = 0.10f;

        var decodedV8 = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
            v8Out,
            [1, 6, 2],
            1024,
            1024,
            1f,
            0f,
            0f,
            1024,
            1024,
            confidenceThreshold: 0.35,
            iouThreshold: 0.45,
            customClasses: ["figure", "chart"]);

        Assert.Single(decodedV8.Figures);
        Assert.Equal(0.89, decodedV8.Figures[0].Confidence, 2);
    }

    [Fact]
    public async Task BugFixes_CrossProcessIds_RuntimeIntegrityAndRollback_RepoGuards_YoloBounds_And_PdfRotationAndOctal_Work()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            // TLG-001: Cross-instance persisted .melomg_id_clock prevents ID collisions
            var appData = Path.Combine(tempRoot, "appdata");
            var opts = new MelomgStorageOptions
            {
                AppDataDirectory = appData,
                VerifyNativeLoadOnInstall = false,
                DirectMlRuntimePackageUrl = "https://test.local/ort-dml.nupkg",
                DirectMlRedistPackageUrl = "https://test.local/dml-redist.nupkg",
                CpuRuntimePackageUrl = "https://test.local/ort-cpu.nupkg",
                PretrainedModelDownloadUrl = "https://test.local/broken.onnx"
            };
            var g1 = new MonotonicTimestampIdGenerator(opts);
            var g2 = new MonotonicTimestampIdGenerator(opts);
            var generated = Enumerable.Range(0, 8)
                .Select(i => (i % 2 == 0 ? g1 : g2).NewFigureId())
                .ToList();
            Assert.Equal(generated.Count, generated.Distinct(StringComparer.Ordinal).Count());
            Assert.True(File.Exists(Path.Combine(appData, ".melomg_id_clock")));

            // TLG-006: Install failure on model download rolls back partial runtime so status stays uninstalled;
            // and tampering a DLL flags status as corrupt via SHA-256 + length verification
            byte[] ortPkg = BuildZipArchive(("runtimes/win-x64/native/onnxruntime.dll", BuildFakePeBytes("ORT_MAIN")));
            byte[] dmlPkg = BuildZipArchive(("bin/x64-win/DirectML.dll", BuildFakePeBytes("DIRECTML_BIN")));
            byte[] goodModel = OnnxProtobufEngine.BuildCompactDocLayoutOnnxModel(1024, 1024, 17);

            opts.ExpectedDirectMlRuntimeSha256 = Sha256Hex(ortPkg);
            opts.ExpectedCpuRuntimeSha256 = Sha256Hex(ortPkg);
            opts.ExpectedDirectMlRedistSha256 = Sha256Hex(dmlPkg);
            opts.ExpectedPretrainedModelSha256 = Sha256Hex(goodModel);

            opts.HttpMessageHandlerOverride = new MockDownloadHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://test.local/ort-dml.nupkg"] = ortPkg,
                ["https://test.local/ort-cpu.nupkg"] = ortPkg,
                ["https://test.local/dml-redist.nupkg"] = dmlPkg
            });
            var onnxEngine = new OnnxProtobufEngine(opts);
            using var sessionHost = new NativeOnnxRuntimeSessionHost();
            var rtFail = new OnnxRuntimeManager(opts, onnxEngine, sessionHost);
            var failInstall = await rtFail.InstallAsync(new OnnxRuntimeInstallRequest("cpu", IncludeModel: true));
            Assert.False(failInstall.IsSuccess);
            Assert.False((await rtFail.GetStatusAsync()).IsInstalled);
            Assert.False(File.Exists(opts.RuntimeManifestPath));

            opts.PretrainedModelDownloadUrl = "https://test.local/doclayout.onnx";
            opts.HttpMessageHandlerOverride = new MockDownloadHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://test.local/ort-cpu.nupkg"] = ortPkg,
                ["https://test.local/doclayout.onnx"] = goodModel
            });
            var okInstall = await rtFail.InstallAsync(new OnnxRuntimeInstallRequest("cpu", IncludeModel: true));
            Assert.True(okInstall.IsSuccess);
            var installedDll = Path.Combine(okInstall.Value.RuntimeDirectory, "onnxruntime.dll");
            await File.WriteAllBytesAsync(installedDll, [0x4D, 0x5A, 0xFF]); // tampered bytes
            var corruptStatus = await rtFail.GetStatusAsync();
            Assert.False(corruptStatus.IsInstalled);
            Assert.Equal("corrupt", corruptStatus.InstalledProvider);

            // TLG-008: 1-class YOLOv5 [1, N, 6] + truncated YOLOv5/v8 buffers never throw IndexOutOfRangeException
            float[] yv5SingleClass = [512f, 512f, 200f, 150f, 0.9f, 0.9f];
            var yv5Decoded = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
                yv5SingleClass, [1, 1, 6], 1024, 1024, 1f, 0f, 0f, 1024, 1024, 0.35, 0.45, customClasses: ["figure"]);
            Assert.Single(yv5Decoded.Figures);
            var truncatedDecoded = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
                [1f, 2f, 3f], [1, 12, 100], 1024, 1024, 1f, 0f, 0f, 1024, 1024, 0.35, 0.45);
            Assert.Empty(truncatedDecoded.Figures);

            // TLG-009, TLG-012, TLG-013: Short expectedSha256 + inherited /Rotate 90 on parent /Pages + octal string escape
            var renderer = new WindowsPdfPageRenderer();
            var engine = new HybridOnnxPdfIngestionEngine(onnxEngine, renderer, sessionHost, rtFail);
            var testPdfPath = Path.Combine(tempRoot, "rotated_octal.pdf");
            await File.WriteAllBytesAsync(testPdfPath, BuildRotatedOctalPdfBytes());
            var verifyShort = await engine.VerifyPdfIntegrityAsync(testPdfPath, "abc");
            Assert.False(verifyShort.IsSuccess);
            Assert.Equal("Pdf.InvalidFormat", verifyShort.Error!.Code);

            var builtInProfile = await onnxEngine.GetBuiltInDefaultProfileAsync();
            var analyzeRes = await engine.AnalyzePdfAsync(testPdfPath, builtInProfile);
            Assert.True(analyzeRes.IsSuccess, analyzeRes.Error?.Description);
            Assert.Equal(792, analyzeRes.Value.Pages[0].WidthPts);
            Assert.Equal(612, analyzeRes.Value.Pages[0].HeightPts);
            Assert.Equal(90, analyzeRes.Value.Pages[0].RotationDegrees);

            // TLG-002: Directory prefix filter rejects sibling dir (`proj` vs `proj2`) and tolerates corrupt Doc_*.db
            var projDir = Path.Combine(tempRoot, "proj");
            var proj2Dir = Path.Combine(tempRoot, "proj2");
            var doc2StorageDir = Path.Combine(proj2Dir, "Doc_rot_test");
            Directory.CreateDirectory(projDir);
            Directory.CreateDirectory(doc2StorageDir);
            var docObj = PdfDocument.Create(
                "Doc_rot_test",
                analyzeRes.Value.SuggestedTitle,
                testPdfPath,
                testPdfPath,
                analyzeRes.Value.Sha256Hash,
                doc2StorageDir,
                analyzeRes.Value.ModelUsed.ModelName,
                analyzeRes.Value.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                analyzeRes.Value.Pages).Value;
            var settingsRepo = new SqliteModelSettingsRepository(opts, onnxEngine);
            var repo = new SqliteDocumentWorkspaceRepository(settingsRepo, new WorkingDirectoryProvider());
            await repo.SaveDocumentAsync(docObj);
            await File.WriteAllTextAsync(Path.Combine(projDir, "Doc_corrupt.db"), "not-a-sqlite-db");
            var scanned = await repo.FindDocumentInDirectoryAsync(projDir);
            Assert.Null(scanned);
            var listedProj = await repo.ListDocumentsAsync(projDir);
            Assert.Empty(listedProj);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static byte[] BuildRotatedOctalPdfBytes()
    {
        string content = "q 180 0 0 120 80 300 cm /Im1 Do Q\nBT /F1 11 Tf 82 280 Td (Figure\\0401:\\040Rotated) Tj ET\n";
        byte[] contentBytes = Encoding.Latin1.GetBytes(content);
        byte[] rgb = new byte[8 * 8 * 3];
        using var zMs = new MemoryStream();
        using (var z = new ZLibStream(zMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rgb, 0, rgb.Length);
        byte[] imgBytes = zMs.ToArray();

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offs = new List<long>();
        void Obj(int id) { offs.Add(pdf.Position); W($"{id} 0 obj\n"); }
        Obj(1); W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] /Rotate 90 >>\nendobj\n");
        Obj(3); W("<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im1 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj(4); W($"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {imgBytes.Length} >>\nstream\n");
        pdf.Write(imgBytes, 0, imgBytes.Length);
        W("\nendstream\nendobj\n");
        Obj(5); W($"<< /Length {contentBytes.Length} >>\nstream\n");
        pdf.Write(contentBytes, 0, contentBytes.Length);
        W("\nendstream\nendobj\n");
        Obj(6); W("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = pdf.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    [Fact]
    public async Task RuntimeManager_RejectsTraversalInPendingCleanup_And_CorruptManifestPaths()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var appData = Path.Combine(tempRoot, "appdata");
            var victimFile = Path.Combine(tempRoot, "outside_victim.txt");
            await File.WriteAllTextAsync(victimFile, "do not delete");

            var opts = new MelomgStorageOptions { AppDataDirectory = appData, VerifyNativeLoadOnInstall = false };
            Directory.CreateDirectory(appData);
            await File.WriteAllTextAsync(Path.Combine(appData, ".pending_cleanup"), victimFile + Environment.NewLine);

            Directory.CreateDirectory(opts.RuntimeRootDirectory);
            await File.WriteAllTextAsync(
                opts.RuntimeManifestPath,
                """{"provider":"cpu","version":"../../escape","files":[{"fileName":"../evil.dll","sizeBytes":10,"sha256":"00"}]}""");

            using var sessionHost = new NativeOnnxRuntimeSessionHost();
            var rtManager = new OnnxRuntimeManager(opts, new OnnxProtobufEngine(opts), sessionHost);

            var status = await rtManager.GetStatusAsync();
            Assert.True(File.Exists(victimFile));
            Assert.False(status.IsInstalled);
            Assert.Equal("corrupt", status.InstalledProvider);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private sealed class MockDownloadHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockDownloadHandler(IReadOnlyDictionary<string, byte[]> payloads)
        {
            _responder = req =>
            {
                string uri = req.RequestUri?.ToString() ?? string.Empty;
                if (payloads.TryGetValue(uri, out var data))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
        }

        public MockDownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    [Fact]
    public async Task Phase1_Bug1_Bug2_Bug3_Bug27_RegressionsPass()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            // Bug 1: Out-of-range YOLO classId is dropped instead of falling back to "figure",
            // and ONNX models without embedded class metadata keep EmbeddedClasses == null.
            var unknownClsDecoded = NativeOnnxRuntimeSessionHost.DecodeYoloOutput(
                [100f, 100f, 400f, 400f, 0.95f, 99f, 100f, 100f, 400f, 400f, 0.95f, -1f],
                [1, 2, 6],
                1024, 1024, 1f, 0f, 0f, 1024, 1024, 0.35, 0.45);
            Assert.Empty(unknownClsDecoded.Figures);
            Assert.Empty(unknownClsDecoded.CaptionNormalizedBoxes);

            // Bug 2: Default storage options pin SHA-256 digests and RequirePinnedSha256 = true;
            // refusing unconfigured digests and mismatched downloads.
            var defaultOpts = new MelomgStorageOptions();
            Assert.True(defaultOpts.RequirePinnedSha256);
            Assert.Equal(64, defaultOpts.ExpectedDirectMlRuntimeSha256?.Length);
            Assert.Equal(64, defaultOpts.ExpectedDirectMlRedistSha256?.Length);
            Assert.Equal(64, defaultOpts.ExpectedCpuRuntimeSha256?.Length);
            Assert.Equal("fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21", defaultOpts.ExpectedPretrainedModelSha256);
            Assert.DoesNotContain("/resolve/main/", defaultOpts.PretrainedModelDownloadUrl, StringComparison.OrdinalIgnoreCase);

            byte[] ortPkg = BuildZipArchive(("runtimes/win-x64/native/onnxruntime.dll", BuildFakePeBytes("ORT_MAIN")));
            var unpinnedOpts = new MelomgStorageOptions
            {
                AppDataDirectory = Path.Combine(tempRoot, "appdata_unpinned"),
                VerifyNativeLoadOnInstall = false,
                CpuRuntimePackageUrl = "https://test.local/ort-cpu.nupkg",
                ExpectedCpuRuntimeSha256 = null,
                RequirePinnedSha256 = true,
                HttpMessageHandlerOverride = new MockDownloadHandler(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["https://test.local/ort-cpu.nupkg"] = ortPkg
                })
            };
            var onnxEngine = new OnnxProtobufEngine(unpinnedOpts);
            using var sessionHost = new NativeOnnxRuntimeSessionHost();
            var rtManager = new OnnxRuntimeManager(unpinnedOpts, onnxEngine, sessionHost);
            var unpinnedRes = await rtManager.InstallAsync(new OnnxRuntimeInstallRequest("cpu", IncludeModel: false));
            Assert.False(unpinnedRes.IsSuccess);
            Assert.Contains("not configured", unpinnedRes.Error!.Description, StringComparison.OrdinalIgnoreCase);

            unpinnedOpts.ExpectedCpuRuntimeSha256 = new string('0', 64);
            var mismatchRes = await rtManager.InstallAsync(new OnnxRuntimeInstallRequest("cpu", IncludeModel: false));
            Assert.False(mismatchRes.IsSuccess);
            Assert.Contains("SHA-256 verification failed", mismatchRes.Error!.Description, StringComparison.OrdinalIgnoreCase);

            // Bug 3: DeleteDocumentAsync never deletes user PDFs or unrelated files in project_* / Doc_* folders.
            var projDir = Path.Combine(tempRoot, "project_safe_rm");
            Directory.CreateDirectory(projDir);
            var userPdf = Path.Combine(projDir, "source_book.pdf");
            var userNotes = Path.Combine(projDir, "user_notes.txt");
            await File.WriteAllBytesAsync(userPdf, BuildRotatedOctalPdfBytes());
            await File.WriteAllTextAsync(userNotes, "keep me");

            var settingsRepo = new SqliteModelSettingsRepository(unpinnedOpts, onnxEngine);
            var repo = new SqliteDocumentWorkspaceRepository(settingsRepo, new WorkingDirectoryProvider());
            var doc = PdfDocument.Create(
                "Doc_safe_rm", "Safe RM", userPdf, userPdf,
                Sha256Hex(await File.ReadAllBytesAsync(userPdf)),
                projDir, "model.onnx", 0.35, DateTimeOffset.UtcNow,
                [new PdfPageInfo(1, 612, 792, 0)], WorkspaceMode.Project).Value;
            await repo.CreateDocumentAsync(doc);
            await File.WriteAllBytesAsync(Path.Combine(projDir, "figures", "fig_001.png"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(projDir, "figures", "custom_user_file.txt"), "keep me too");

            await repo.DeleteDocumentAsync(doc.Id);

            Assert.False(File.Exists(Path.Combine(projDir, "Doc_safe_rm.db")));
            Assert.False(File.Exists(Path.Combine(projDir, "figures", "fig_001.png")));
            Assert.True(File.Exists(userPdf));
            Assert.True(File.Exists(userNotes));
            Assert.True(File.Exists(Path.Combine(projDir, "figures", "custom_user_file.txt")));

            // Bug 27: Form XObject with /ProcSet [/PDF /Text /ImageB /ImageC] is parsed as Form, not misclassified as raster /Image.
            var formPdfPath = Path.Combine(tempRoot, "form_procset_imageb.pdf");
            await File.WriteAllBytesAsync(formPdfPath, BuildFormXObjectWithImageProcSetPdfBytes());
            var renderer = new WindowsPdfPageRenderer();
            var engine = new HybridOnnxPdfIngestionEngine(onnxEngine, renderer, sessionHost, rtManager);
            var builtInProfile = await onnxEngine.GetBuiltInDefaultProfileAsync();
            var formAnalysis = await engine.AnalyzePdfAsync(formPdfPath, builtInProfile);
            Assert.True(formAnalysis.IsSuccess, formAnalysis.Error?.Description);
            Assert.Contains(formAnalysis.Value.Candidates, c =>
                string.Equals(c.FigureNumber, "7.1", StringComparison.OrdinalIgnoreCase) &&
                (c.Caption?.Contains("Nested form caption", StringComparison.OrdinalIgnoreCase) ?? false));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static byte[] BuildFormXObjectWithImageProcSetPdfBytes()
    {
        byte[] pageContent = Encoding.Latin1.GetBytes("q 1 0 0 1 0 0 cm /Fm1 Do Q\n");
        byte[] formContent = Encoding.Latin1.GetBytes(
            "q 240 0 0 160 80 420 cm /Im1 Do Q\nBT /F1 11 Tf 80 395 Td (Fig. 7.1 Nested form caption) Tj ET\n");
        byte[] rgb = new byte[8 * 8 * 3];
        using var zMs = new MemoryStream();
        using (var z = new ZLibStream(zMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rgb, 0, rgb.Length);
        byte[] imgBytes = zMs.ToArray();

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offs = new List<long>();
        void Obj(int id) { offs.Add(pdf.Position); W($"{id} 0 obj\n"); }
        Obj(1); W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj(3); W("<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Fm1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj(4); W($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /XObject << /Im1 6 0 R >> /Font << /F1 7 0 R >> >> /Length {formContent.Length} >>\nstream\n");
        pdf.Write(formContent, 0, formContent.Length);
        W("\nendstream\nendobj\n");
        Obj(5); W($"<< /Length {pageContent.Length} >>\nstream\n");
        pdf.Write(pageContent, 0, pageContent.Length);
        W("\nendstream\nendobj\n");
        Obj(6); W($"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {imgBytes.Length} >>\nstream\n");
        pdf.Write(imgBytes, 0, imgBytes.Length);
        W("\nendstream\nendobj\n");
        Obj(7); W("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = pdf.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] BuildZipArchive(params (string EntryPath, byte[] Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = zip.CreateEntry(path);
                using var s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildFakePeBytes(string tag)
    {
        byte[] tagBytes = Encoding.ASCII.GetBytes(tag);
        var buf = new byte[64 + tagBytes.Length];
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        Buffer.BlockCopy(tagBytes, 0, buf, 64, tagBytes.Length);
        return buf;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "melomg_rttest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
