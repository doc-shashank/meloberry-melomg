using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Hosting;
using Melomg.Infrastructure.Onnx;
using Xunit;

namespace Melomg.CliTests;

public sealed class DualBootAndCommandsTests
{
    [Fact]
    public async Task StatelessChain_And_Isolation_WorkEndToEnd()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var outDir = Path.Combine(root, "crops");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // 1. Fluent chain in stateless mode with --json (--retain-db keeps DB for multi-extract & lint)
            var c1 = new TestConsole();
            int code1 = await CliApplicationRunner.ExecuteAsync(
                ["--json", $"ingest \"{pdfPath}\" \"{workDir}\" --offline --retain-db -> figures -> extract 1 \"{outDir}\" --format png --dpi 200 --padding 10 -> extract all \"{outDir}\" --format jpg --dpi 150 -> lint"],
                c1,
                appData);

            Assert.Equal(ExitCodes.Success, code1);
            Assert.Contains("\"extractedCount\"", c1.StandardOutput);
            Assert.Contains("\"inferenceEngine\": \"managed-fallback\"", c1.StandardOutput);
            Assert.Contains("\"isClean\": true", c1.StandardOutput);

            // Verify real files emitted into outDir without residual .meta.json by default
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.png"));
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.jpg"));
            Assert.Empty(Directory.GetFiles(outDir, "*.meta.json"));

            // Verify opt-in --meta writes sidecar metadata
            var cMeta = new TestConsole();
            var listPre = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], listPre, appData));
            using var preDoc = JsonDocument.Parse(listPre.StandardOutput);
            string preDocId = preDoc.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;
            Assert.False(File.Exists(Path.Combine(workDir, preDocId, "source.pdf")));
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", preDocId, "extract", "1", outDir, "--meta"], cMeta, appData));
            Assert.NotEmpty(Directory.GetFiles(outDir, "*.meta.json"));

            // 2. Subsequent stateless call without --doc fails cleanly
            var c2 = new TestConsole();
            int code2 = await CliApplicationRunner.ExecuteAsync(["figures"], c2, appData);
            Assert.Equal(ExitCodes.ValidationError, code2);
            Assert.Contains("Cli.MissingDocument", c2.StandardError);

            // 3. Extract DocumentId via listdocs --json and query with --doc
            var c3 = new TestConsole();
            int code3 = await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], c3, appData);
            Assert.Equal(ExitCodes.Success, code3);
            using var jsonDoc = JsonDocument.Parse(c3.StandardOutput);
            string docId = jsonDoc.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;

            var c4 = new TestConsole();
            int code4 = await CliApplicationRunner.ExecuteAsync(["--doc", docId, "info"], c4, appData);
            Assert.Equal(ExitCodes.Success, code4);
            Assert.Contains(docId, c4.StandardOutput);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task InteractiveRepl_RetainsActiveState_SupportsRefinement_And_DeletionGuards()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            var replScript = string.Join(Environment.NewLine,
            [
                $"ingest \"{pdfPath}\" \"{workDir}\" --title \"REPL Test Doc\" --offline --retain-db",
                "settitle \"Updated REPL Doc\"",
                "figures",
                "inspect",
                "setcaption \"Updated Figure Caption\"",
                "setlabel \"Fig. 1A\"",
                "setcategory diagram",
                "setbbox 75 125 250 185",
                "addfigure 1 340 400 180 120 --category table --caption \"Table 1: Metrics\"",
                "setindex 1 shift",
                "extract page 1 --dpi 150",
                "lint",
                "exit"
            ]);

            var console = new TestConsole(replScript);
            int code = await CliApplicationRunner.ExecuteAsync([], console, appData);

            Assert.Equal(ExitCodes.Success, code);
            Assert.Contains("melomg > ", console.StandardOutput);
            Assert.Contains("Updated title of", console.StandardOutput);
            Assert.Contains("Updated caption for", console.StandardOutput);
            Assert.Contains("Updated category of", console.StandardOutput);
            Assert.Contains("Added manual figure", console.StandardOutput);
            Assert.Contains("Moved figure", console.StandardOutput);
            Assert.Contains("Lint ", console.StandardOutput);
            Assert.Contains("CLEAN", console.StandardOutput);

            // Fetch DocId and test deletion guards (abort on 'n', confirm on 'y')
            var listConsole = new TestConsole();
            await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], listConsole, appData);
            using var docJson = JsonDocument.Parse(listConsole.StandardOutput);
            string docId = docJson.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;

            var abortDelete = new TestConsole("n\n");
            int abortCode = await CliApplicationRunner.ExecuteAsync([docId, "delete"], abortDelete, appData);
            Assert.Equal(ExitCodes.Success, abortCode);
            Assert.Contains("WARNING:", abortDelete.StandardError);
            Assert.Contains("Deletion cancelled.", abortDelete.StandardOutput);

            var confirmDelete = new TestConsole("y\n");
            int delCode = await CliApplicationRunner.ExecuteAsync([docId, "delete"], confirmDelete, appData);
            Assert.Equal(ExitCodes.Success, delCode);
            Assert.Contains($"Deleted document workspace {docId}", confirmDelete.StandardOutput);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task OnnxModelCommands_HelpTopics_And_EmbeddedDocs_WorkProperly()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");

        try
        {
            // 1. model info
            var cInfo = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["model", "info"], cInfo, appData));
            Assert.Contains("melomg_doclayout_v1.onnx", cInfo.StandardOutput);

            // 2. model threshold
            var cThresh = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["model", "threshold", "0.42", "0.50"], cThresh, appData));
            Assert.Contains("confidence=0.42", cThresh.StandardOutput);

            // 3. Reject invalid .onnx file
            var badOnnx = Path.Combine(root, "invalid.onnx");
            await File.WriteAllTextAsync(badOnnx, "INVALID");
            var cBad = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["model", "set", badOnnx], cBad, appData));
            Assert.Contains("Model.InvalidModelFormat", cBad.StandardError);

            // 4. Accept valid custom .onnx file & reset
            var customOnnx = Path.Combine(root, "custom_doclayout.onnx");
            await File.WriteAllBytesAsync(customOnnx, OnnxProtobufEngine.BuildCompactDocLayoutOnnxModel(512, 512, 18));
            var cSet = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["model", "set", customOnnx], cSet, appData));
            Assert.Contains("custom_doclayout.onnx", cSet.StandardOutput);

            var cReset = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["model", "reset"], cReset, appData));
            Assert.Contains("melomg_doclayout_v1.onnx", cReset.StandardOutput);

            // 5. Help & Embedded docs
            var cHelp = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--help"], cHelp, appData));
            Assert.Contains("ingest", cHelp.StandardOutput);
            Assert.Contains("extract", cHelp.StandardOutput);

            var cDocs = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "docs", "--all"], cDocs, appData));
            Assert.Contains("ONNX_AND_DETECTION.md", cDocs.StandardOutput);
            Assert.Contains("COMMANDS.md", cDocs.StandardOutput);
            Assert.Contains("runtime install", cDocs.StandardOutput);

            // 6. Runtime status, invalid provider validation, and uninstall guard
            var cRtStatus = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "runtime", "status"], cRtStatus, appData));
            Assert.Contains("\"activeInferenceEngine\": \"managed-fallback\"", cRtStatus.StandardOutput);

            var cRtBad = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "runtime", "install", "--provider", "bogus"], cRtBad, appData));
            Assert.Contains("Runtime.InvalidProvider", cRtBad.StandardError);

            var cRtUninst = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--yes", "--json", "runtime", "uninstall"], cRtUninst, appData));
            Assert.Contains("\"removedFileCount\": 0", cRtUninst.StandardOutput);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task RenderOverlay_And_LabelObfuscationMaskCommands_WorkEndToEnd()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var previewDir = Path.Combine(root, "previews");
        var maskedDir = Path.Combine(root, "masked");
        var cleanDir = Path.Combine(root, "clean");
        var jpgHighDir = Path.Combine(root, "jpg_high");
        var pageLowDir = Path.Combine(root, "page_low");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "diagram.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // Stateless chain: ingest -> render page 1 overlay -> inspect -> mask auto -> extract (--keep-overlay preserves overlay) -> extract (default deletes overlay) -> lint
            var c1 = new TestConsole();
            int code1 = await CliApplicationRunner.ExecuteAsync(
                [
                    "--json",
                    $"ingest \"{pdfPath}\" \"{workDir}\" --offline --retain-db -> render 1 \"{previewDir}\" --overlay --dpi 120 --quality medium -> mask auto --style blur -> extract 1 \"{maskedDir}\" --include-caption --keep-overlay -> extract 1 \"{cleanDir}\" --no-masks --keep-overlay -> lint"
                ],
                c1,
                appData);

            Assert.Equal(ExitCodes.Success, code1);
            Assert.True(File.Exists(Path.Combine(previewDir, "page_001_overlay.png")));
            Assert.NotEmpty(Directory.GetFiles(maskedDir, "*.png"));
            Assert.NotEmpty(Directory.GetFiles(cleanDir, "*.png"));
            Assert.Empty(Directory.GetFiles(maskedDir, "*.meta.json"));

            // Default extract (without --keep-overlay) cleans up tracked QA overlays automatically
            var cFind = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], cFind, appData));
            using var jFind = JsonDocument.Parse(cFind.StandardOutput);
            string overlayDocId = jFind.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;
            var cAutoClean = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", overlayDocId, "extract", "1", cleanDir], cAutoClean, appData));
            Assert.False(File.Exists(Path.Combine(previewDir, "page_001_overlay.png")));

            // Find Fig_* ID and test --quality preset/numeric on extract/render + invalid quality error
            var cDocs = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], cDocs, appData));
            using var jDoc = JsonDocument.Parse(cDocs.StandardOutput);
            string docId = jDoc.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;

            var cHigh = new TestConsole();
            int highCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", docId, "extract", "1", jpgHighDir, "--format", "jpg", "--quality", "high"],
                cHigh,
                appData);
            Assert.Equal(ExitCodes.Success, highCode);
            Assert.NotEmpty(Directory.GetFiles(jpgHighDir, "*.jpg"));
            using (var jHigh = JsonDocument.Parse(cHigh.StandardOutput))
            {
                Assert.Equal(240, jHigh.RootElement.GetProperty("data").GetProperty("dpi").GetInt32());
                Assert.Equal(92, jHigh.RootElement.GetProperty("data").GetProperty("quality").GetInt32());
            }

            var cRenderQ = new TestConsole();
            int renderQCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", docId, "render", "1", pageLowDir, "--format", "jpg", "--quality", "low"],
                cRenderQ,
                appData);
            Assert.Equal(ExitCodes.Success, renderQCode);
            Assert.True(File.Exists(Path.Combine(pageLowDir, "page_001.jpg")));
            using (var jRend = JsonDocument.Parse(cRenderQ.StandardOutput))
            {
                Assert.Equal(96, jRend.RootElement.GetProperty("data").GetProperty("dpi").GetInt32());
                Assert.Equal(55, jRend.RootElement.GetProperty("data").GetProperty("quality").GetInt32());
            }

            var cBadQ = new TestConsole();
            int badQCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", docId, "extract", "1", jpgHighDir, "--quality", "999"],
                cBadQ,
                appData);
            Assert.Equal(ExitCodes.ValidationError, badQCode);
            Assert.Contains("Figure.InvalidQuality", cBadQ.StandardError);

            var repl = new TestConsole(string.Join(Environment.NewLine,
            [
                $"usedoc {docId}",
                "inspect",
                "mask list",
                "mask add 100 180 70 18 --label \"Manual Box\" --style whiteout",
                "mask remove 1",
                "mask clear",
                "exit"
            ]));

            int replCode = await CliApplicationRunner.ExecuteAsync([], repl, appData);
            Assert.Equal(ExitCodes.Success, replCode);
            Assert.Contains("Saved Masks:", repl.StandardOutput);
            Assert.Contains("Encoder Block", repl.StandardOutput);
            Assert.Contains("Added mask #", repl.StandardOutput);
            Assert.Contains("Removed mask #1", repl.StandardOutput);
            Assert.Contains("Cleared ", repl.StandardOutput);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task EphemeralIngest_And_OpenProject_WorkViaCli()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var ephWork = Path.Combine(root, "eph_work");
        var ephOut = Path.Combine(root, "eph_out");
        var customProjDir = Path.Combine(root, "custom_study_project");
        Directory.CreateDirectory(ephWork);

        try
        {
            var ephPdf = Path.Combine(root, "oneshot.pdf");
            await File.WriteAllBytesAsync(ephPdf, BuildSamplePdfBytes());

            // 1. Standard ingest -> extract (no --retain-db) discards temporary DB automatically
            var cEph = new TestConsole();
            int ephCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", $"ingest \"{ephPdf}\" \"{ephWork}\" --offline -> extract all \"{ephOut}\""],
                cEph,
                appData);
            Assert.Equal(ExitCodes.Success, ephCode);
            Assert.Contains("\"retainedDatabase\": false", cEph.StandardOutput);
            Assert.NotEmpty(Directory.GetFiles(ephOut, "*.png"));
            Assert.Empty(Directory.GetFiles(ephWork, "*.db", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(ephWork));

            // 2. open <pdfPath> <projectDir> moves the PDF into customProjDir and retains DB
            var projPdf = Path.Combine(root, "moving_source.pdf");
            await File.WriteAllBytesAsync(projPdf, BuildSamplePdfBytes());

            var cOpen = new TestConsole();
            int openCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", $"open \"{projPdf}\" \"{customProjDir}\" --offline -> extract all"],
                cOpen,
                appData);
            Assert.Equal(ExitCodes.Success, openCode);
            Assert.Contains("\"reopenedExisting\": false", cOpen.StandardOutput);
            Assert.Contains("\"retainedDatabase\": true", cOpen.StandardOutput);
            Assert.False(File.Exists(projPdf));
            Assert.True(File.Exists(Path.Combine(customProjDir, "moving_source.pdf")));
            Assert.NotEmpty(Directory.GetFiles(customProjDir, "Doc_*.db"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(customProjDir, "figures"), "*.png"));

            // 3. Re-opening project directory loads cached workspace directly
            var cReopen = new TestConsole();
            int reopenCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "open", customProjDir],
                cReopen,
                appData);
            Assert.Equal(ExitCodes.Success, reopenCode);
            Assert.Contains("\"reopenedExisting\": true", cReopen.StandardOutput);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task BugFixes_AppDataChain_ValueOptions_DashTitles_FlagPrecedence_JsonPrompts_And_CorruptUninstall_Work()
    {
        var root = CreateTempRoot();
        try
        {
            var appData1 = Path.Combine(root, "appdata_custom");
            var workDir = Path.Combine(root, "work");
            var outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(workDir);
            var pdfPath = Path.Combine(root, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // TLG-014 + TLG-015: --app-data inside a quoted chain + --quality value option alongside quoted chain
            var c1 = new TestConsole();
            int e1 = await CliApplicationRunner.ExecuteAsync(
                ["--quality", "high", $"--app-data \"{appData1}\" ingest \"{pdfPath}\" \"{workDir}\" --offline --retain-db -> extract all \"{outDir}\""],
                c1);
            Assert.True(e1 == ExitCodes.Success, $"ERR: {c1.StandardError}\nOUT: {c1.StandardOutput}");
            Assert.True(File.Exists(Path.Combine(appData1, ".melomg_id_clock")));
            Assert.True(Directory.EnumerateFiles(outDir, "Fig_*.png").Any());

            var docA = ExtractFirstTokenWithPrefix(c1.StandardOutput, "Doc_");

            // TLG-015: Literal --heading via `--` and after verb
            var cTitle = new TestConsole();
            int eTitle = await CliApplicationRunner.ExecuteAsync(
                ["--app-data", appData1, docA, "settitle", "--", "-- Section A"],
                cTitle);
            Assert.True(eTitle == ExitCodes.Success, $"docA={docA}\nERR={cTitle.StandardError}\nC1_OUT={c1.StandardOutput}");

            // TLG-016: Explicit --doc overrides stale REPL ActiveFigureId on extract; --figure overrides ActiveDocumentId on lint
            var workDir2 = Path.Combine(root, "work2");
            Directory.CreateDirectory(workDir2);
            var c2 = new TestConsole();
            int e2 = await CliApplicationRunner.ExecuteAsync(
                [$"ingest \"{pdfPath}\" \"{workDir2}\" --offline --retain-db -> figures"],
                c2,
                appData1);
            Assert.Equal(ExitCodes.Success, e2);
            var docB = ExtractFirstTokenWithPrefix(c2.StandardOutput, "Doc_");
            var figB = ExtractFirstTokenWithPrefix(c2.StandardOutput, "Fig_");

            var outOverride = Path.Combine(root, "out_override");
            var cOverride = new TestConsole();
            int eOverride = await CliApplicationRunner.ExecuteAsync(
                [$"usedoc {docB} -> {figB} inspect -> --doc {docA} extract all \"{outOverride}\" --retain-db -> info"],
                cOverride,
                appData1);
            Assert.True(eOverride == ExitCodes.Success, $"ERR={cOverride.StandardError}\nOUT={cOverride.StandardOutput}");
            Assert.Contains(docA, cOverride.StandardOutput);

            var cLint = new TestConsole();
            int eLint = await CliApplicationRunner.ExecuteAsync(
                [$"usedoc {docA} -> --figure {figB} lint"],
                cLint,
                appData1);
            Assert.Equal(ExitCodes.Success, eLint);
            Assert.Contains(figB, cLint.StandardOutput);

            // TLG-017: --json deletion prompt goes to stderr (leaving clean JSON on stdout)
            var cDel = new TestConsole("n\n");
            int eDel = await CliApplicationRunner.ExecuteAsync(
                ["--json", docA, "delete"],
                cDel,
                appData1);
            Assert.Equal(ExitCodes.Success, eDel);
            Assert.Contains("Confirm deletion? [y/N]:", cDel.StandardError);
            Assert.DoesNotContain("Confirm deletion? [y/N]:", cDel.StandardOutput);
            using (var docJson = JsonDocument.Parse(cDel.StandardOutput))
                Assert.True(docJson.RootElement.GetProperty("success").GetBoolean());

            // TLG-017: Corrupt runtime directory still prompts confirmation on uninstall instead of silent wipe
            var corruptRuntimeDir = Path.Combine(appData1, "runtime", "onnxruntime", "win-x64", "1.20.1");
            Directory.CreateDirectory(corruptRuntimeDir);
            await File.WriteAllBytesAsync(Path.Combine(corruptRuntimeDir, "onnxruntime.dll"), [0x4D, 0x5A, 0x00, 0x00]);
            var manifestPath = Path.Combine(appData1, "runtime", "onnxruntime", "win-x64", "manifest.json");
            await File.WriteAllTextAsync(manifestPath, """
            {
              "version": "1.20.1",
              "provider": "cpu",
              "installedAtUtc": "2026-01-01T00:00:00Z",
              "files": [
                { "fileName": "onnxruntime.dll", "sizeBytes": 4, "sha256": "0000" }
              ]
            }
            """);

            var cRt = new TestConsole("n\n");
            int eRt = await CliApplicationRunner.ExecuteAsync(["--json", "runtime", "uninstall", "--keep-model"], cRt, appData1);
            Assert.Equal(ExitCodes.Success, eRt);
            Assert.Contains("Confirm deletion? [y/N]:", cRt.StandardError);
            Assert.True(File.Exists(Path.Combine(corruptRuntimeDir, "onnxruntime.dll")));
        }
        finally
        {
            TryCleanup(root);
        }
    }

    [Fact]
    public async Task CliGrammar_ErrorHandling_And_EphemeralSessionReset_Regressions()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(root, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSamplePdfBytes());

            // CLI-01: Unknown flag with --json emits JSON error envelope
            var badOptJson = new TestConsole();
            int badCode = await CliApplicationRunner.ExecuteAsync(["--badopt", "--json", "listdocs"], badOptJson, appData);
            Assert.Equal(ExitCodes.ValidationError, badCode);
            Assert.Contains("\"code\":\"Cli.ParseError\"", badOptJson.StandardError);

            // CLI-05: Unknown docs topic returns NotFound (exit code 3)
            var badDocs = new TestConsole();
            Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(["docs", "nonexistent-topic"], badDocs, appData));

            // CLI-02 + CLI-03 + CLI-04: '--app-data' after '--', '--title foo->bar', and 'usedoc --doc' + extra arg check
            var cChain = new TestConsole();
            int chainCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", $"ingest \"{pdfPath}\" \"{workDir}\" --offline --retain-db --title foo->bar -> settitle -- \"--app-data\""],
                cChain,
                appData);
            Assert.Equal(ExitCodes.Success, chainCode);
            Assert.Contains("\"--app-data\"", cChain.StandardOutput);

            var cList = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "listdocs", workDir], cList, appData));
            using var listJson = JsonDocument.Parse(cList.StandardOutput);
            string docId = listJson.RootElement.GetProperty("data")[0].GetProperty("documentId").GetString()!;

            var cUseDocFlag = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["usedoc", "--doc", docId], cUseDocFlag, appData));

            var cExtraArg = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync([docId, "info", "unexpectedExtra"], cExtraArg, appData));

            // BUG-06: Non-retained ingest -> extract in REPL clears active document/figure so subsequent 'figures' fails cleanly
            var replConsole = new TestConsole(string.Join(Environment.NewLine,
            [
                $"ingest \"{pdfPath}\" \"{workDir}\" --offline",
                $"extract all \"{outDir}\"",
                "figures",
                "exit"
            ]));
            int replCode = await CliApplicationRunner.ExecuteAsync([], replConsole, appData);
            Assert.Equal(ExitCodes.Success, replCode);
            Assert.Contains("Discarded temporary workspace DB", replConsole.StandardOutput);
            Assert.Contains("Cli.MissingDocument", replConsole.StandardError);
        }
        finally
        {
            TryCleanup(root);
        }
    }

    private static string ExtractFirstTokenWithPrefix(string text, string prefix)
    {
        foreach (var token in text.Split([' ', '\r', '\n', ',', '(', ')', ':', '"', '/', '\\', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.TrimEnd('.');
            if (clean.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^3];
            if (clean.StartsWith(prefix, StringComparison.Ordinal))
                return clean;
        }
        throw new InvalidOperationException($"Token starting with '{prefix}' not found in:\n{text}");
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
            "BT /F1 11 Tf 82 478 Td (Figure 1: Sample Architecture) Tj ET\n" +
            "1.5 w 0.1 0.2 0.3 RG 0.3 0.6 0.9 rg\n" +
            "90 160 50 90 re B 170 160 50 120 re B 80 160 m 260 160 l S\n" +
            "BT /F1 11 Tf 82 138 Td (Chart 1: Throughput) Tj ET\n";

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
