using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Hosting;
using Xunit;

namespace Melomg.CliTests;

public sealed class DualBootAndCommandsTests
{
    [Fact]
    public async Task BareCliPrintsPrime_SpecAndVersionWork_AndAgentEnvVarsEmitMinifiedJson()
    {
        var root = CreateTempRoot();
        var envAppData = Path.Combine(root, "env_appdata");
        string? origMelomgAgent = Environment.GetEnvironmentVariable("MELOMG_AGENT");
        string? origMbyAgent = Environment.GetEnvironmentVariable("MBY_AGENT");
        string? origAppData = Environment.GetEnvironmentVariable("MELOMG_APP_DATA");

        try
        {
            Environment.SetEnvironmentVariable("MELOMG_AGENT", null);
            Environment.SetEnvironmentVariable("MBY_AGENT", null);
            Environment.SetEnvironmentVariable("MELOMG_APP_DATA", envAppData);

            // 1. Bare melomg prints compact ~280-token prime grammar card (REPL removed)
            var primeConsole = new TestConsole();
            int primeCode = await CliApplicationRunner.ExecuteAsync([], primeConsole);
            Assert.Equal(ExitCodes.Success, primeCode);
            Assert.Contains("ADDR:", primeConsole.StandardOutput);
            Assert.Contains("CMDS:", primeConsole.StandardOutput);
            Assert.Contains("EXIT:", primeConsole.StandardOutput);
            Assert.DoesNotContain("melomg >", primeConsole.StandardOutput);

            // 2. -v / version & -h / help & spec topics
            var verConsole = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["-v"], verConsole));
            Assert.Contains("melomg ", verConsole.StandardOutput);

            var helpConsole = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["-h"], helpConsole));
            Assert.Contains("spec", helpConsole.StandardOutput);

            var specConsole = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["spec", "vocab", "errors"], specConsole));
            Assert.Contains("# vocab", specConsole.StandardOutput);
            Assert.Contains("# errors", specConsole.StandardOutput);

            // 3. MELOMG_AGENT=1 auto-enables minified JSON + honors MELOMG_APP_DATA
            Environment.SetEnvironmentVariable("MELOMG_AGENT", "1");
            var agentConsole = new TestConsole();
            int agentCode = await CliApplicationRunner.ExecuteAsync(["runtime", "status"], agentConsole);
            Assert.Equal(ExitCodes.Success, agentCode);
            string trimmed1 = agentConsole.StandardOutput.TrimEnd('\r', '\n');
            Assert.DoesNotContain('\n', trimmed1);
            using (var doc = JsonDocument.Parse(trimmed1))
            {
                Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
                Assert.Contains(envAppData, doc.RootElement.GetProperty("data").GetProperty("runtimeDirectory").GetString()!, StringComparison.OrdinalIgnoreCase);
            }

            // 4. MBY_AGENT=1 (shared Meloberry toolchain env) also auto-enables minified JSON & WhenWritingNull
            Environment.SetEnvironmentVariable("MELOMG_AGENT", null);
            Environment.SetEnvironmentVariable("MBY_AGENT", "1");
            var mbyConsole = new TestConsole();
            int mbyCode = await CliApplicationRunner.ExecuteAsync(["version"], mbyConsole);
            Assert.Equal(ExitCodes.Success, mbyCode);
            string trimmed2 = mbyConsole.StandardOutput.TrimEnd('\r', '\n');
            Assert.DoesNotContain('\n', trimmed2);
            using (var doc2 = JsonDocument.Parse(trimmed2))
            {
                Assert.True(doc2.RootElement.GetProperty("success").GetBoolean());
                Assert.False(doc2.RootElement.TryGetProperty("error", out _));
                var data = doc2.RootElement.GetProperty("data");
                Assert.Equal("melomg", data.GetProperty("tool").GetString());
                Assert.Equal(Melomg.Domain.Common.MelomgVersion.Current, data.GetProperty("version").GetString());
            }

            var primeJsonConsole = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["prime"], primeJsonConsole));
            using (var docPrime = JsonDocument.Parse(primeJsonConsole.StandardOutput.TrimEnd('\r', '\n')))
            {
                Assert.Equal(Melomg.Domain.Common.MelomgVersion.Current, docPrime.RootElement.GetProperty("data").GetProperty("version").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MELOMG_AGENT", origMelomgAgent);
            Environment.SetEnvironmentVariable("MBY_AGENT", origMbyAgent);
            Environment.SetEnvironmentVariable("MELOMG_APP_DATA", origAppData);
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task CoreWorkflow_IngestGetLsExtractAndOneShot_UseNumberTargetingAndSemanticFilenames()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var outDir = Path.Combine(root, "assets");
        var oneShotOutDir = Path.Combine(root, "oneshot_assets");
        var projectDir = Path.Combine(root, "proj_ws");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "anatomy.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // 1. Ingest with --retain-db --offline --json
            var ingestC = new TestConsole();
            int ingestCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "ingest", pdfPath, "-o", workDir, "--retain-db", "--offline"],
                ingestC,
                appData);
            Assert.Equal(ExitCodes.Success, ingestCode);

            string docId;
            using (var json = JsonDocument.Parse(ingestC.StandardOutput))
            {
                var data = json.RootElement.GetProperty("data");
                docId = data.GetProperty("id").GetString()!;
                Assert.Equal("Fig.", data.GetProperty("vocab").GetProperty("primary").GetString());
                Assert.Equal(2, data.GetProperty("figureCount").GetInt32());
                Assert.False(File.Exists(Path.Combine(workDir, docId, "source.pdf")));
            }

            // 2. ls discovers workspace with vocabPrimaryPrefix
            var lsC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "ls", workDir], lsC, appData));
            using (var lsJson = JsonDocument.Parse(lsC.StandardOutput))
            {
                var arr = lsJson.RootElement.GetProperty("data");
                Assert.Equal(1, arr.GetArrayLength());
                Assert.Equal(docId, arr[0].GetProperty("documentId").GetString());
                Assert.Equal("Fig.", arr[0].GetProperty("vocabPrimaryPrefix").GetString());
            }

            // 3. get views: outline (default), full, ids, and figure address (#2.1)
            var getIdsC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "get", docId, "--view", "ids"], getIdsC, appData));
            using (var idsJson = JsonDocument.Parse(getIdsC.StandardOutput))
                Assert.Equal(2, idsJson.RootElement.GetProperty("data").GetProperty("ids").GetArrayLength());

            var getFullC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "get", $"{docId}#2.1", "--view", "full"], getFullC, appData));
            using (var fullJson = JsonDocument.Parse(getFullC.StandardOutput))
                Assert.Equal("2.1", fullJson.RootElement.GetProperty("data").GetProperty("figure").GetProperty("figureNumber").GetString());

            // 4. Targeted extract by #num -> semantic filename fig_2.1.png, no .meta.json by default
            var ext1C = new TestConsole();
            int ext1Code = await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", $"{docId}#2.1", "-o", outDir, "--quality", "high"],
                ext1C,
                appData);
            Assert.Equal(ExitCodes.Success, ext1Code);
            Assert.True(File.Exists(Path.Combine(outDir, "fig_2.1.png")));
            Assert.Empty(Directory.GetFiles(outDir, "*.meta.json"));

            // 5. Multi-number selector (comma + space) as JPG with quality preset and --naming id
            var ext2C = new TestConsole();
            int ext2Code = await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", docId, "2.1,2.2", "-o", outDir, "--format", "jpg", "--quality", "80", "--naming", "id"],
                ext2C,
                appData);
            Assert.Equal(ExitCodes.Success, ext2Code);
            Assert.Equal(2, Directory.GetFiles(outDir, "Fig_*.jpg").Length);

            var ext3C = new TestConsole();
            int ext3Code = await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", docId, "2.1", "2.2", "-o", Path.Combine(root, "multi_space"), "--format", "png", "--dpi", "120"],
                ext3C,
                appData);
            Assert.Equal(ExitCodes.Success, ext3Code);
            Assert.True(File.Exists(Path.Combine(root, "multi_space", "fig_2.1.png")));
            Assert.True(File.Exists(Path.Combine(root, "multi_space", "fig_2.2.png")));

            // 6. One-shot extract directly on PDF with figure number; zero stray Doc_* workspaces remain in CWD
            var beforeCwdDocs = Directory.GetDirectories(Directory.GetCurrentDirectory(), "Doc_*");
            var oneshotC = new TestConsole();
            int oneshotCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", pdfPath, "2.1", "-o", oneShotOutDir, "--offline"],
                oneshotC,
                appData);
            Assert.Equal(ExitCodes.Success, oneshotCode);
            Assert.True(File.Exists(Path.Combine(oneShotOutDir, "fig_2.1.png")));
            Assert.Empty(Directory.GetDirectories(oneShotOutDir, "Doc_*"));
            var afterCwdDocs = Directory.GetDirectories(Directory.GetCurrentDirectory(), "Doc_*");
            Assert.Equal(beforeCwdDocs.Length, afterCwdDocs.Length);

            // 7. Project mode + --refresh + unknown number error envelope with hint
            var projPdf = Path.Combine(root, "proj_paper.pdf");
            await File.WriteAllBytesAsync(projPdf, BuildSamplePdfBytes());
            var projC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "ingest", projPdf, "--project", "-o", projectDir, "--offline"], projC, appData));
            string projId = JsonDocument.Parse(projC.StandardOutput).RootElement.GetProperty("data").GetProperty("id").GetString()!;

            var refreshC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "ingest", projId, "--refresh", "--offline"], refreshC, appData));

            var errC = new TestConsole();
            int errCode = await CliApplicationRunner.ExecuteAsync(["--json", "extract", $"{docId}#99.9"], errC, appData);
            Assert.Equal(ExitCodes.NotFound, errCode);
            using (var errJson = JsonDocument.Parse(errC.StandardError))
            {
                Assert.False(errJson.RootElement.GetProperty("success").GetBoolean());
                var errObj = errJson.RootElement.GetProperty("error");
                Assert.Equal("Figure.NotFound", errObj.GetProperty("code").GetString());
                Assert.False(string.IsNullOrWhiteSpace(errObj.GetProperty("hint").GetString()));
            }
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task PatchAddRejectsTable_MaskRenderAutoCleanup_CheckModelRuntimeAndNonInteractiveRm()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var qaDir = Path.Combine(root, "qa");
        var outDir = Path.Combine(root, "crops");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "refinement.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            var ingestC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "ingest", pdfPath, "-o", workDir, "--retain-db", "--offline"], ingestC, appData));
            string docId = JsonDocument.Parse(ingestC.StandardOutput).RootElement.GetProperty("data").GetProperty("id").GetString()!;

            // 1. Repeatable --set + --move on patch
            var patchDocC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "patch", docId, "--set", "title=Refined Atlas"], patchDocC, appData));
            Assert.Contains("Refined Atlas", patchDocC.StandardOutput);

            var patchFigC = new TestConsole();
            int patchFigCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "patch", $"{docId}#f1", "--set", "category=diagram", "--set", "label=Diag 2.1", "--set", "num=2.5", "--set", "bbox=75,110,230,170", "--move", "2:exchange"],
                patchFigC,
                appData);
            Assert.Equal(ExitCodes.Success, patchFigCode);
            using (var pJson = JsonDocument.Parse(patchFigC.StandardOutput))
                Assert.Equal("diagram", pJson.RootElement.GetProperty("data").GetProperty("category").GetString());

            // 2. Stdin JSON patch
            var patchStdinC = new TestConsole("""{"caption":"Patched via stdin"}""");
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "patch", $"{docId}#f1", "-"], patchStdinC, appData));
            Assert.Contains("Patched via stdin", patchStdinC.StandardOutput);

            // 3. add valid diagram vs add rejected table
            var addOkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["--json", "add", docId, "1", "50", "50", "180", "120", "--category", "diagram", "--num", "3.1", "--caption", "Diagram 3.1: Pathway"],
                addOkC,
                appData));

            var addTableC = new TestConsole();
            int addTableCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "add", docId, "1", "50", "50", "180", "120", "--category", "table"],
                addTableC,
                appData);
            Assert.Equal(ExitCodes.ValidationError, addTableCode);
            Assert.Contains("Figure.InvalidCategory", addTableC.StandardError);

            // 4. Mask subcommands (auto, ls, add, rm, clear) on #3.1
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "mask", $"{docId}#3.1", "auto"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "mask", $"{docId}#3.1", "add", "60", "60", "40", "14", "--style", "blur", "--label", "ManualLabel"], new TestConsole(), appData));
            var maskLsC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "mask", $"{docId}#3.1", "ls"], maskLsC, appData));
            Assert.Contains("ManualLabel", maskLsC.StandardOutput);
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "mask", $"{docId}#3.1", "rm", "1"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "mask", $"{docId}#3.1", "clear"], new TestConsole(), appData));

            // 5. Render QA overlay -> extract cleans up tracked overlay
            var renderC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "render", docId, "1", "-o", qaDir, "--overlay"], renderC, appData));
            string overlayPath = JsonDocument.Parse(renderC.StandardOutput).RootElement.GetProperty("data").GetProperty("artifacts")[0].GetProperty("outputImagePath").GetString()!;
            Assert.True(File.Exists(overlayPath));

            var extCleanC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "extract", $"{docId}#3.1", "-o", outDir], extCleanC, appData));
            Assert.Equal(1, JsonDocument.Parse(extCleanC.StandardOutput).RootElement.GetProperty("data").GetProperty("cleanedOverlayCount").GetInt32());
            Assert.False(File.Exists(overlayPath));

            // 6. check, model, runtime uninstall, non-interactive rm figure & rm workspace
            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "check", docId], checkC, appData));
            Assert.True(JsonDocument.Parse(checkC.StandardOutput).RootElement.GetProperty("data").GetProperty("isClean").GetBoolean());

            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "model", "info"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "model", "threshold", "0.42"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "model", "reset"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "runtime", "uninstall"], new TestConsole(), appData));

            // Non-interactive rm without --yes or stdin prompts
            var rmFigC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "rm", $"{docId}#3.1"], rmFigC, appData));
            Assert.Equal("figure", JsonDocument.Parse(rmFigC.StandardOutput).RootElement.GetProperty("data").GetProperty("entityKind").GetString());

            var rmDocC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "rm", docId], rmDocC, appData));
            Assert.Equal("document", JsonDocument.Parse(rmDocC.StandardOutput).RootElement.GetProperty("data").GetProperty("entityKind").GetString());
            Assert.False(Directory.Exists(Path.Combine(workDir, docId)));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task BatchCommand_InterpolatesIds_And_FailsFastWithIndexedPath()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var outDir = Path.Combine(root, "batch_out");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "batch.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            string batchJson = JsonSerializer.Serialize(new object[]
            {
                new { op = "ingest", pdf = pdfPath, @out = workDir, offline = true, retainDb = true },
                new { op = "patch", addr = "$0.id", set = new { title = "Batch Renamed" } },
                new { op = "extract", addr = "$0.id#2.1", @out = outDir, quality = "medium" },
                new { op = "check", addr = "$prev.id" },
                new { op = "rm", addr = "$0.id" }
            });

            var okConsole = new TestConsole(batchJson);
            int okCode = await CliApplicationRunner.ExecuteAsync(["--json", "batch", "-"], okConsole, appData);
            Assert.Equal(ExitCodes.Success, okCode);
            using (var doc = JsonDocument.Parse(okConsole.StandardOutput))
                Assert.Equal(5, doc.RootElement.GetProperty("data").GetProperty("executedCount").GetInt32());
            Assert.True(File.Exists(Path.Combine(outDir, "fig_2.1.png")));

            // Invalid op at step 1 fails fast and reports path "batch[1].set"
            string badBatch = JsonSerializer.Serialize(new object[]
            {
                new { op = "ingest", pdf = pdfPath, @out = workDir, offline = true, retainDb = true },
                new { op = "patch", addr = "$0.id", set = new { unknownKey = "oops" } }
            });
            var errConsole = new TestConsole(badBatch);
            int errCode = await CliApplicationRunner.ExecuteAsync(["--json", "batch", "-"], errConsole, appData);
            Assert.Equal(ExitCodes.ValidationError, errCode);
            using (var errDoc = JsonDocument.Parse(errConsole.StandardError))
            {
                string? errPath = errDoc.RootElement.GetProperty("error").GetProperty("path").GetString();
                Assert.NotNull(errPath);
                Assert.StartsWith("batch[1]", errPath, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task IngestQuietAndVerbose_ExtractManifest_CheckAnomalies_And_DefaultWorkspaceUnderMeloberry()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var outDir = Path.Combine(root, "assets");
        var originalCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(root);
            var pdfPath = Path.Combine(root, "sample.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // 1. Default ingest without -o places retained workspace under .meloberry/melomg/<docId>
            var quietIngest = new TestConsole();
            int code = await CliApplicationRunner.ExecuteAsync(
                ["--json", "ingest", pdfPath, "--retain-db", "--offline", "--quiet"], quietIngest, appData);
            Assert.Equal(ExitCodes.Success, code);

            string docId;
            using (var doc = JsonDocument.Parse(quietIngest.StandardOutput))
            {
                var data = doc.RootElement.GetProperty("data");
                docId = data.GetProperty("id").GetString()!;
                string wsDir = data.GetProperty("workspaceDirectory").GetString()!;
                Assert.Contains(Path.Combine(".meloberry", "melomg"), wsDir, StringComparison.OrdinalIgnoreCase);
                Assert.True(Directory.Exists(wsDir));
                // Quiet mode omits figures array
                Assert.False(data.TryGetProperty("figures", out _));
            }

            // 2. Patch second figure number to trigger a sequence gap (2.1 -> 2.4) and run check
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["--json", "patch", $"{docId}#2.2", "--set", "num=2.4", "--set", "caption=Fig. 2.4: Shifted"], new TestConsole(), appData));

            var checkC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "check", docId], checkC, appData));
            using (var doc = JsonDocument.Parse(checkC.StandardOutput))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("isClean").GetBoolean());
                Assert.Contains(data.GetProperty("issues").EnumerateArray(),
                    w => w.GetProperty("code").GetString() == "Lint.SequenceGap");
                Assert.True(data.GetProperty("suspectPages").GetArrayLength() > 0);
            }

            // 3. Extract with --manifest --quiet emits manifest.json and omits per-artifact array
            var extC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", docId, "all", "-o", outDir, "--manifest", "--quiet"], extC, appData));
            string manifestPath;
            using (var doc = JsonDocument.Parse(extC.StandardOutput))
            {
                var data = doc.RootElement.GetProperty("data");
                manifestPath = data.GetProperty("manifestPath").GetString()!;
                Assert.False(data.TryGetProperty("artifacts", out _));
            }
            Assert.True(File.Exists(manifestPath));
            using (var mDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                var figuresEl = mDoc.RootElement.GetProperty("figures");
                Assert.True(figuresEl.EnumerateObject().Count() >= 2);
                Assert.True(figuresEl.TryGetProperty("2.1", out _));
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            TryCleanup(root);
        }
    }

    private static byte[] BuildSamplePdfBytes()
    {
        var rgbPixels = new byte[16 * 16 * 3];
        for (int i = 0; i < rgbPixels.Length; i += 3)
        {
            rgbPixels[i] = 45;
            rgbPixels[i + 1] = 120;
            rgbPixels[i + 2] = 220;
        }

        byte[] compressedImg;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(rgbPixels, 0, rgbPixels.Length);
            compressedImg = ms.ToArray();
        }

        string p1Content =
            "q 220 0 0 160 80 500 cm /Im1 Do Q\n" +
            "BT /F1 12 Tf 110 580 Td (Encoder Block) Tj ET\n" +
            "BT /F1 11 Tf 82 478 Td (Fig. 2.1: Sample Architecture) Tj ET\n" +
            "1.5 w 0.1 0.2 0.3 RG 0.3 0.6 0.9 rg\n" +
            "90 160 50 90 re B 170 160 50 120 re B 80 160 m 260 160 l S\n" +
            "BT /F1 11 Tf 82 138 Td (Fig. 2.2: Throughput Chart) Tj ET\n";

        using var pdf = new MemoryStream();
        void WriteAscii(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            pdf.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.4\n");
        var offsets = new List<long>();
        void BeginObj(int id)
        {
            offsets.Add(pdf.Position);
            WriteAscii($"{id} 0 obj\n");
        }

        BeginObj(1);
        WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2);
        WriteAscii("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        BeginObj(3);
        WriteAscii("<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im1 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        BeginObj(4);
        WriteAscii($"<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressedImg.Length} >>\nstream\n");
        pdf.Write(compressedImg, 0, compressedImg.Length);
        WriteAscii("\nendstream\nendobj\n");

        byte[] p1Bytes = Encoding.Latin1.GetBytes(p1Content);
        BeginObj(5);
        WriteAscii($"<< /Length {p1Bytes.Length} >>\nstream\n");
        pdf.Write(p1Bytes, 0, p1Bytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        BeginObj(6);
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        long xref = pdf.Position;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) WriteAscii($"{off:D10} 00000 n \n");

        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "melomg_clitest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryCleanup(string path)
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
