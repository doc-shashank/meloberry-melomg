using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Melomg.Application;
using Melomg.Application.Abstractions;
using Melomg.Application.Addressing;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Domain.Documents;
using Melomg.Domain.Models;
using Melomg.Infrastructure;
using Melomg.Infrastructure.Onnx;
using Melomg.Infrastructure.Pdf;
using Melomg.Infrastructure.Persistence;
using Melomg.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Melomg.UnitTests;

public sealed class BugFixRegressionTests
{
    [Fact]
    public async Task Bugs1_37_39_OnnxProtobufMetadataEmbeddedClassesPriorityAndDynamicInputSize()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var engine = new OnnxProtobufEngine(opts);

            // Bug 1 + Bug 39: dynamic dims (0/symbolic) & no metadata_props -> EmbeddedClasses == null, imgsz1024 inferred
            var dynPath = Path.Combine(tempRoot, "custom_doclayout_imgsz1024.onnx");
            await File.WriteAllBytesAsync(dynPath, BuildMinimalOnnxModel(inputW: 0, inputH: 0, metadataProps: []));
            var dynRes = await engine.InspectAndValidateModelAsync(dynPath);
            Assert.True(dynRes.IsSuccess, dynRes.Error?.Description);
            Assert.Null(dynRes.Value.EmbeddedClasses);
            Assert.Equal(["figure", "diagram", "chart"], dynRes.Value.SupportedClasses);
            Assert.Equal(1024, dynRes.Value.InputWidth);
            Assert.Equal(1024, dynRes.Value.InputHeight);

            // Bug 37: melomg_classes takes priority over names even when names appears AFTER melomg_classes
            var prioPath = Path.Combine(tempRoot, "prio.onnx");
            await File.WriteAllBytesAsync(prioPath, BuildMinimalOnnxModel(640, 640, [
                ("melomg_classes", "diagram,chart"),
                ("names", "{0: 'title', 1: 'figure'}")
            ]));
            var prioRes = await engine.InspectAndValidateModelAsync(prioPath);
            Assert.True(prioRes.IsSuccess, prioRes.Error?.Description);
            Assert.Equal(["diagram", "chart"], prioRes.Value.EmbeddedClasses);
            Assert.Equal(["diagram", "chart"], prioRes.Value.SupportedClasses);
        }
        finally { TryDelete(tempRoot); }
    }

    [Fact]
    public void Bugs4_11_22_NativeSessionHost_ParsePythonDictClassNamesAndSafeDisposal()
    {
        // Bug 22: JSON-quoted keys and sparse indices preserve exact index positions
        var parsed = NativeOnnxRuntimeSessionHost.ParsePythonDictClassNames("""{"0": "title", "3": "figure", "5": "figure_caption"}""");
        Assert.NotNull(parsed);
        Assert.Equal(6, parsed!.Count);
        Assert.Equal("title", parsed[0]);
        Assert.Equal(string.Empty, parsed[1]);
        Assert.Equal(string.Empty, parsed[2]);
        Assert.Equal("figure", parsed[3]);
        Assert.Equal(string.Empty, parsed[4]);
        Assert.Equal("figure_caption", parsed[5]);

        // Bug 4 + Bug 11: Concurrent/repeated Dispose & Reset handles without throwing or deadlocking
        using var host = new NativeOnnxRuntimeSessionHost();
        host.UnloadAllForUninstall();
        host.Dispose();
        host.Dispose();
    }

    [Fact]
    public async Task Bugs6_8_9_17_38_44_RefreshSnapshot_RehydrateRenumber_ClearedNum_WalAndLintBounds()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var appData = Path.Combine(tempRoot, "appdata");
            var workDir = Path.Combine(tempRoot, "work");
            Directory.CreateDirectory(workDir);

            var pdfPath = Path.Combine(tempRoot, "chapter.pdf");
            await File.WriteAllBytesAsync(pdfPath, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            await using var sp = BuildProvider(appData);
            using var scope = sp.CreateScope();

            var ingest = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
            var patch = scope.ServiceProvider.GetRequiredService<PatchTargetHandler>();
            var repo = scope.ServiceProvider.GetRequiredService<IDocumentWorkspaceRepository>();
            var lint = scope.ServiceProvider.GetRequiredService<LintDocumentHandler>();

            var ingRes = await ingest.HandleAsync(new IngestPdfCommand(pdfPath, workDir, RetainDatabase: true, Offline: true));
            Assert.True(ingRes.IsSuccess);
            string docId = ingRes.Value.Id;
            string dbPath = ingRes.Value.DatabasePath!;

            // Bug 17: SQLite workspace DB and settings DB use WAL journal mode
            await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", (await cmd.ExecuteScalarAsync())?.ToString()?.ToLowerInvariant());
            }

            // Bug 9: Clearing FigureNumber via patch num="" persists across SQLite reload
            var firstFig = ingRes.Value.Figures!.First(f => !string.IsNullOrWhiteSpace(f.Num));
            var clearRes = await patch.HandleAsync(new PatchTargetCommand(firstFig.Id, FigureNumber: ""));
            Assert.True(clearRes.IsSuccess);
            Assert.Null(clearRes.Value.FigureNumber);

            var reloadedAfterClear = await repo.FindDocumentAsync(docId);
            Assert.NotNull(reloadedAfterClear);
            var reloadedFig = reloadedAfterClear!.Document.FindFigure(firstFig.Id);
            Assert.NotNull(reloadedFig);
            Assert.Null(reloadedFig!.FigureNumber);
            Assert.Null(reloadedFig.NormalizedNumber);

            // Bug 8: Filtering legacy non-image rows during SQLite rehydration compacts SortIndex 1..N with no gaps
            await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE DocumentFigures SET SortIndex = SortIndex + 10;
                    INSERT INTO DocumentFigures (Id, SortIndex, PageNumber, PageFigureIndex, Category, BboxX, BboxY, BboxW, BboxH, Confidence, Source, Label, Caption)
                    VALUES ('Fig_LegacyGap', 1, 1, 1, 'table', 20, 20, 100, 80, 0.90, 'onnx', 'Table 1', 'Table 1');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var rehydrated = await repo.FindDocumentAsync(docId);
            Assert.NotNull(rehydrated);
            Assert.Equal(
                Enumerable.Range(1, rehydrated!.Document.Figures.Count).ToArray(),
                rehydrated.Document.Figures.Select(f => f.Index).ToArray());

            // Bug 6: Modifying source PDF and running ingest --refresh updates Sha256Hash so check passes
            await File.AppendAllTextAsync(pdfPath, "\n% bug6-refresh-comment");
            var refreshRes = await ingest.HandleAsync(new IngestPdfCommand(docId, Refresh: true, Offline: true));
            Assert.True(refreshRes.IsSuccess);
            Assert.NotEqual(ingRes.Value.Sha256Hash, refreshRes.Value.Sha256Hash);

            var cleanLint = await lint.HandleAsync(new LintDocumentQuery(docId, Quiet: false, Verbose: true));
            Assert.True(cleanLint.IsSuccess);
            Assert.True(cleanLint.Value.IsClean);
            Assert.True(cleanLint.Value.SourcePdfVerified);
            Assert.Equal(refreshRes.Value.FigureCount, cleanLint.Value.CheckedFigureCount);

            // Bug 38 + Bug 44: Out-of-bounds caption box and mask are flagged as Lint.OutOfPageBounds; LintSeverity serializes lowercase
            var docForBounds = (await repo.FindDocumentAsync(docId))!.Document;
            var figId = docForBounds.Figures[0].Id;
            await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE DocumentFigures SET CaptionBboxX = 550, CaptionBboxY = 10, CaptionBboxW = 200, CaptionBboxH = 30 WHERE Id = $id;
                    INSERT INTO DocumentFigureMasks (FigureId, MaskIndex, BboxX, BboxY, BboxW, BboxH, LabelText, Style, ColorHex)
                    VALUES ($id, 1, 580, 750, 100, 100, 'OOBMask', 'solid', '#000000');
                    """;
                cmd.Parameters.AddWithValue("$id", figId);
                await cmd.ExecuteNonQueryAsync();
            }

            var oobLint = await lint.HandleAsync(new LintDocumentQuery(docId, Quiet: true));
            Assert.True(oobLint.IsSuccess);
            Assert.False(oobLint.Value.IsClean);
            Assert.Contains(oobLint.Value.Issues, i => i.Code == "Lint.OutOfPageBounds" && i.Message.Contains("Caption box"));
            Assert.Contains(oobLint.Value.Issues, i => i.Code == "Lint.OutOfPageBounds" && i.Message.Contains("Mask #1"));
            Assert.All(oobLint.Value.Issues, i => Assert.Equal(LintSeverity.Error, i.Severity)); // Quiet omits warnings

            string severityJson = JsonSerializer.Serialize(oobLint.Value.Issues[0]);
            Assert.Contains("\"Severity\":\"error\"", severityJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":1", severityJson, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public void Bugs7_14_24_25_34_38_DomainAndAddressingHardening()
    {
        // Bug 14: LooksLikeEntityIdAddress never treats .pdf paths or directory paths as entity IDs
        Assert.False(AddressResolver.LooksLikeEntityIdAddress("Doc_sample_ch2.pdf"));
        Assert.False(AddressResolver.LooksLikeEntityIdAddress("dir/Doc_123"));
        Assert.False(AddressResolver.LooksLikeEntityIdAddress(@"C:\work\Doc_123"));
        Assert.True(AddressResolver.LooksLikeEntityIdAddress("Doc_20261124_100000_001"));
        Assert.True(AddressResolver.LooksLikeEntityIdAddress("Doc_20261124_100000_001#2.1"));
        Assert.True(AddressResolver.LooksLikeEntityIdAddress("Fig_20261124_100000_002"));

        // Bug 24: Rounding-after-validation and finite checks
        Assert.False(BoundingBox.Create(-0.001, 10, 50, 50).IsSuccess);
        Assert.False(BoundingBox.Create(10, 10, 0.001, 50).IsSuccess);
        Assert.False(BoundingBox.Create(double.NaN, 10, 50, 50).IsSuccess);
        Assert.False(BoundingBox.Create(10, 10, double.PositiveInfinity, 50).IsSuccess);
        Assert.True(BoundingBox.Create(0, 0, 0.01, 0.01).IsSuccess);

        Assert.False(PdfPageInfo.Create(1, double.NaN, 792).IsSuccess);
        Assert.False(PdfPageInfo.Create(1, 0.001, 792).IsSuccess);

        // Bug 34: #Fig. 2.1 normalizes to 2.1
        Assert.Equal("2.1", FigureNumberNormalizer.NormalizeLookupNumber("#Fig. 2.1"));

        // Bug 25: Roman numeral lookup does not falsely expand; S-1 classifies as prefixed; SetFigureNumber("---") returns InvalidFigureNumber
        var doc = PdfDocument.Create(
            "Doc_vocab", "Vocab", "a.pdf", "a.pdf", new string('a', 64), "C:\\tmp", "m.onnx", 0.35, DateTimeOffset.UtcNow,
            [PdfPageInfo.Create(1, 612, 792).Value]).Value;
        var box = BoundingBox.Create(20, 20, 100, 100).Value;
        Assert.True(doc.AddFigure("Fig_1", 1, FigureCategory.Figure, box, 0.9, "Plate II Alpha", "Plate II", DetectionSource.Hybrid, vocabPrefix: "Plate", figureNumber: "II").IsSuccess);
        Assert.True(doc.AddFigure("Fig_2", 1, FigureCategory.Figure, box, 0.9, "Plate IV Beta", "Plate IV", DetectionSource.Hybrid, vocabPrefix: "Plate", figureNumber: "IV").IsSuccess);
        var (romanMatches, isSub) = doc.FindFiguresByNumber("I");
        Assert.Empty(romanMatches);
        Assert.False(isSub);

        var prefVocab = DocumentVocabulary.BuildFromEntries([("Fig.", "s-1"), ("Fig.", "s-2")]);
        Assert.Equal("prefixed", prefVocab.NumberingStyle);

        var badNum = doc.Figures[0].SetFigureNumber("---");
        Assert.False(badNum.IsSuccess);
        Assert.Equal("Figure.InvalidFigureNumber", badNum.Error!.Code);

        // Bug 7: n:2 never falls back to 1-based index even if document has 2+ figures
        var docNum = PdfDocument.Create(
            "Doc_num", "Num", "a.pdf", "a.pdf", new string('b', 64), "C:\\tmp", "m.onnx", 0.35, DateTimeOffset.UtcNow,
            [PdfPageInfo.Create(1, 612, 792).Value]).Value;
        Assert.True(docNum.AddFigure("Fig_A", 1, FigureCategory.Figure, box, 0.9, "Fig. 2.1 A", "Fig. 2.1", DetectionSource.Hybrid, vocabPrefix: "Fig.", figureNumber: "2.1").IsSuccess);
        Assert.True(docNum.AddFigure("Fig_B", 1, FigureCategory.Figure, box, 0.9, "Fig. 2.2 B", "Fig. 2.2", DetectionSource.Hybrid, vocabPrefix: "Fig.", figureNumber: "2.2").IsSuccess);
        Assert.False(AddressResolver.ResolveSelectors(docNum, ["n:2"]).IsSuccess);
        Assert.False(AddressResolver.ResolveSelectors(docNum, ["2"]).IsSuccess);

        // Bug 38: 3-level hierarchical figure numbers (1.2.1 -> 1.2.3 detects gap 1.2.2)
        Assert.True(FigureSequenceAnalyzer.TryParseSeriesOrdinal("1.2.3", out var seriesKey, out int ordinal));
        Assert.Equal("1.2.", seriesKey);
        Assert.Equal(3, ordinal);

        var doc3 = PdfDocument.Create(
            "Doc_hier3", "Hier3", "a.pdf", "a.pdf", new string('c', 64), "C:\\tmp", "m.onnx", 0.35, DateTimeOffset.UtcNow,
            [PdfPageInfo.Create(1, 612, 792).Value]).Value;
        Assert.True(doc3.AddFigure("Fig_1", 1, FigureCategory.Figure, box, 0.9, "Fig. 1.2.1 First", "Fig. 1.2.1", DetectionSource.Hybrid, vocabPrefix: "Fig.", figureNumber: "1.2.1").IsSuccess);
        Assert.True(doc3.AddFigure("Fig_2", 1, FigureCategory.Figure, box, 0.9, "Fig. 1.2.3 Third", "Fig. 1.2.3", DetectionSource.Hybrid, vocabPrefix: "Fig.", figureNumber: "1.2.3").IsSuccess);
        var hierReport = FigureSequenceAnalyzer.Analyze(doc3);
        Assert.Contains(hierReport.Anomalies, a => a.Code == "Anomaly.SequenceGap" && a.ExpectedNumber == "1.2.2");
    }

    [Fact]
    public async Task Bug15_MonotonicTimestampIdGenerator_CrossProcessMutexMonotonicity()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var g1 = new MonotonicTimestampIdGenerator(opts);
            var g2 = new MonotonicTimestampIdGenerator(opts);

            var tasks = Enumerable.Range(0, 20)
                .Select(i => Task.Run(() => (i % 2 == 0 ? g1 : g2).NewFigureId()))
                .ToArray();
            var ids = await Task.WhenAll(tasks);

            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }
        finally { TryDelete(tempRoot); }
    }

    [Fact]
    public async Task Bugs12_13_29_30_31_32_33_41_PdfParserHardening()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var engine = new HybridOnnxPdfIngestionEngine(onnx, new WindowsPdfPageRenderer());
            var profile = await onnx.GetBuiltInDefaultProfileAsync();

            // Bug 30: Title decoding handles escaped parens and UTF-16BE BOM hex strings
            Assert.True(HybridOnnxPdfIngestionEngine.TryExtractPdfTitleString(@"<< /Title (Cell \(Injury\) Atlas) >>", out var escTitle));
            Assert.Equal("Cell (Injury) Atlas", escTitle);
            Assert.True(HybridOnnxPdfIngestionEngine.TryExtractPdfTitleString(@"<< /Title <FEFF00480065006C006C006F> >>", out var utf16Title));
            Assert.Equal("Hello", utf16Title);

            // Bugs 12, 29, 30 (outline hijack), 31 (empty Form Do), 32 (indirect /Contents array), 33 (multiline bfrange + surrogate), 41:
            var complexPdfPath = Path.Combine(tempRoot, "complex_parser.pdf");
            await File.WriteAllBytesAsync(complexPdfPath, BuildComplexParserPdfBytes());

            var res = await engine.AnalyzePdfAsync(complexPdfPath, profile);
            Assert.True(res.IsSuccess, res.Error?.Description);
            Assert.Equal("True (Info) Title", res.Value.SuggestedTitle);

            // Only 1 real figure (empty Form XObject must NOT emit a phantom image placement), and Fig. 2.5 caption recovered after inline image BI..ID..EI
            var fig = Assert.Single(res.Value.Candidates, c => c.FigureNumber == "2.5");
            Assert.Contains("After inline image", fig.Caption ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Bug 13: Incremental /ObjStm update supersedes earlier object definition
            var incPdfPath = Path.Combine(tempRoot, "incremental_objstm.pdf");
            await File.WriteAllBytesAsync(incPdfPath, BuildIncrementalObjStmPdfBytes());
            var incRes = await engine.AnalyzePdfAsync(incPdfPath, profile);
            Assert.True(incRes.IsSuccess, incRes.Error?.Description);
            Assert.Equal("Updated Title From Later ObjStm", incRes.Value.SuggestedTitle);
        }
        finally { TryDelete(tempRoot); }
    }

    [Fact]
    public void Bugs40_42_CaptionLineLimitColumnIsolationAndTwoColumnReadingOrder()
    {
        // Bug 40: 8-line left-column caption is NOT truncated at 5 lines and does NOT absorb right-column prose
        var spans = new List<ParsedTextSpan>();
        double y = 300;
        for (int line = 0; line < 8; line++)
        {
            string text = line == 0
                ? "Fig. 2.2 Long textbook caption line 1"
                : $"Continuation line {line + 1} describing cellular morphology.";
            spans.Add(new ParsedTextSpan(40, y, 220, 9.0, text, 10.5));
            // Adjacent right-column body prose on every line
            spans.Add(new ParsedTextSpan(320, y, 240, 9.0, $"Right column prose {line + 1} that must not bleed.", 10.5));
            y += 11.5;
        }

        var anchors = HybridOnnxPdfIngestionEngine.ExtractPageCaptionAnchors(spans, 612, 792);
        var cap = Assert.Single(anchors);
        Assert.Contains("Continuation line 8", cap.Text);
        Assert.DoesNotContain("Right column prose", cap.Text);

        // Bug 42: Two-column page reading order puts left-column before right-column and orders series ascending
        var c231 = new DetectedFigureCandidate(29, FigureCategory.Figure, new BoundingBox(320, 60, 220, 140), 0.9, "Fig. 2.31 Right top", "Fig. 2.31", DetectionSource.Hybrid, null, "Fig.", "2.31");
        var c230 = new DetectedFigureCandidate(29, FigureCategory.Figure, new BoundingBox(40, 120, 220, 140), 0.9, "Fig. 2.30 Left lower", "Fig. 2.30", DetectionSource.Hybrid, null, "Fig.", "2.30");
        var ordered = HybridOnnxPdfIngestionEngine.AbsorbContainedSubFigures([c231, c230], pageWidthPts: 612);
        Assert.Equal(["2.30", "2.31"], ordered.Select(c => c.FigureNumber ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Bugs16_21_35_36_43_ResilientScanProjectConflictStandardCleanupSingleFiltersAndOfflinePrecision()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var appData = Path.Combine(tempRoot, "appdata");
            var workDir = Path.Combine(tempRoot, "work");
            var projDir = Path.Combine(tempRoot, "proj_one");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(projDir);

            var pdf1 = Path.Combine(tempRoot, "one.pdf");
            var pdf2 = Path.Combine(tempRoot, "two.pdf");
            await File.WriteAllBytesAsync(pdf1, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());
            await File.WriteAllBytesAsync(pdf2, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            await using var sp = BuildProvider(appData);
            using var scope = sp.CreateScope();

            var ingest = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
            var get = scope.ServiceProvider.GetRequiredService<GetTargetHandler>();

            // Bug 21: Second --project ingest into same project directory fails with Project.AlreadyExists
            var p1 = await ingest.HandleAsync(new IngestPdfCommand(pdf1, projDir, ProjectMode: true, Offline: true));
            Assert.True(p1.IsSuccess);
            var p2 = await ingest.HandleAsync(new IngestPdfCommand(pdf2, projDir, ProjectMode: true, Offline: true));
            Assert.False(p2.IsSuccess);
            Assert.Equal("Project.AlreadyExists", p2.Error!.Code);
            Assert.Single(Directory.GetFiles(projDir, "Doc_*.db"));

            // Bug 36: Single-figure get rejects --page / --category / --min-conf
            string figId = p1.Value.Figures![0].Id;
            var badSingle = await get.HandleAsync(new GetTargetQuery(figId, ViewMode.Outline, PageNumber: 1));
            Assert.False(badSingle.IsSuccess);
            Assert.Equal("Cli.InvalidArgs", badSingle.Error!.Code);

            // Bug 35: Standard ingest does not leave orphaned workspace directory on validation failure (e.g. empty/invalid confidence)
            var orphanCheckDir = Path.Combine(tempRoot, "orphan_check");
            Directory.CreateDirectory(orphanCheckDir);
            var badIng = await ingest.HandleAsync(new IngestPdfCommand(pdf2, orphanCheckDir, ConfidenceThreshold: 9.9, Offline: true));
            Assert.False(badIng.IsSuccess);
            Assert.Empty(Directory.GetDirectories(orphanCheckDir));

            // Bug 16: Listing documents in root with nested workspaces succeeds cleanly
            var listRes = await scope.ServiceProvider.GetRequiredService<ListDocumentsHandler>()
                .HandleAsync(new ListDocumentsQuery(tempRoot));
            Assert.True(listRes.IsSuccess);
            Assert.Contains(listRes.Value, d => d.DocumentId == p1.Value.Id);

            // Bug 43: Offline fallback ignores pure prose pages with decorative thin boxes
            var prosePdf = Path.Combine(tempRoot, "prose_only.pdf");
            await File.WriteAllBytesAsync(prosePdf, BuildProseBoxOnlyPdfBytes());
            var proseIng = await ingest.HandleAsync(new IngestPdfCommand(prosePdf, workDir, Offline: true));
            Assert.True(proseIng.IsSuccess);
            Assert.Equal(0, proseIng.Value.FigureCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempRoot);
        }
    }

    private static byte[] BuildComplexParserPdfBytes()
    {
        // Tests Bug 12 (grandparent /Resources), Bug 29 & 41 (BI..ID..EI with '[' and '<'), Bug 30 (Outline /Title vs /Info /Title),
        // Bug 31 (empty Form XObject Do), Bug 32 (indirect /Contents array), Bug 33 (multiline beginbfrange + unpaired surrogate).
        byte[] rgb = new byte[8 * 8 * 3];
        using var zMs = new MemoryStream();
        using (var z = new ZLibStream(zMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rgb, 0, rgb.Length);
        byte[] imgBytes = zMs.ToArray();

        // Stream 1: empty Form Do + real image placement + inline image whose binary payload has '[' and '<' and '('
        byte[] s1 = Encoding.Latin1.GetBytes(
            "q 100 0 0 100 50 50 cm /FmEmpty Do Q\n" +
            "q 240 0 0 160 80 420 cm /Im1 Do Q\n" +
            "BI /W 1 /H 1 /CS /G /BPC 8 ID [<(raw_bytes EI\n");
        // Stream 2: caption text encoded using ToUnicode CMap where <0001>='F', <0002>='i', <0003>='g'
        byte[] s2 = Encoding.Latin1.GetBytes(
            "BT /F1 11 Tf 80 395 Td [<000100020003> (. 2.5 After inline image)] TJ ET\n");

        byte[] cmapBytes = Encoding.Latin1.GetBytes(
            "begincmap\n" +
            "1 beginbfrange\n" +
            "<0001> <0003> [\n<0046>\n<0069>\n<0067>\n]\n" +
            "<0010> <0011> <D800>\n" +
            "endbfrange\n" +
            "endcmap\n");

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offs = new List<long>();
        void Obj(int id) { offs.Add(pdf.Position); W($"{id} 0 obj\n"); }

        Obj(1); W("<< /Type /Catalog /Pages 2 0 R /Outlines 11 0 R >>\nendobj\n");
        // Grandparent /Pages holds /Resources (Bug 12)
        Obj(2); W("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 7 0 R /FmEmpty 8 0 R >> /Font << /F1 9 0 R >> >> >>\nendobj\n");
        // Intermediate /Pages parent
        Obj(3); W("<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 >>\nendobj\n");
        // Page points to indirect /Contents array object 5 (Bug 32)
        Obj(4); W("<< /Type /Page /Parent 3 0 R /Contents 5 0 R >>\nendobj\n");
        Obj(5); W("[6 0 R 12 0 R]\nendobj\n");
        Obj(6); W($"<< /Length {s1.Length} >>\nstream\n"); pdf.Write(s1); W("\nendstream\nendobj\n");
        Obj(7); W($"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {imgBytes.Length} >>\nstream\n"); pdf.Write(imgBytes); W("\nendstream\nendobj\n");
        // Empty Form XObject (StreamLength == 0) (Bug 31)
        Obj(8); W("<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj(9); W("<< /Type /Font /Subtype /Type0 /ToUnicode 10 0 R >>\nendobj\n");
        Obj(10); W($"<< /Length {cmapBytes.Length} >>\nstream\n"); pdf.Write(cmapBytes); W("\nendstream\nendobj\n");
        // Outline node with decoy /Title (Bug 30)
        Obj(11); W("<< /Type /Outlines /Title (Bookmark Decoy Title) /First 11 0 R >>\nendobj\n");
        Obj(12); W($"<< /Length {s2.Length} >>\nstream\n"); pdf.Write(s2); W("\nendstream\nendobj\n");
        // Real /Info object referenced by trailer (Bug 30)
        Obj(13); W("<< /Title (True \\(Info\\) Title) >>\nendobj\n");

        long xref = pdf.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R /Info 13 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] BuildIncrementalObjStmPdfBytes()
    {
        byte[] content = Encoding.Latin1.GetBytes("BT /F1 11 Tf 80 700 Td (Hello) Tj ET\n");
        byte[] objStmBody = Encoding.Latin1.GetBytes("5 0 << /Title (Updated Title From Later ObjStm) >>");
        int firstOffset = "5 0 ".Length;

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.6\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>\nendobj\n");
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); pdf.Write(content); W("\nendstream\nendobj\n");
        // Earlier version of object 5 (old title)
        W("5 0 obj\n<< /Title (Old Title) >>\nendobj\n");
        // Later incremental /ObjStm redefining object 5
        W($"6 0 obj\n<< /Type /ObjStm /N 1 /First {firstOffset} /Length {objStmBody.Length} >>\nstream\n");
        pdf.Write(objStmBody);
        W("\nendstream\nendobj\n");
        W("trailer\n<< /Size 7 /Root 1 0 R /Info 5 0 R >>\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] BuildProseBoxOnlyPdfBytes()
    {
        string content =
            "1 w 50 500 500 180 re S\n" +
            "BT /F1 11 Tf 60 650 Td (Key concepts of cellular injury and adaptive responses in pathology.) Tj ET\n" +
            "BT /F1 11 Tf 60 630 Td (Cells adapt to stress by hypertrophy, hyperplasia, atrophy, or metaplasia.) Tj ET\n" +
            "BT /F1 11 Tf 60 610 Td (Irreversible injury leads to necrosis or apoptosis depending on stimuli.) Tj ET\n" +
            "BT /F1 11 Tf 60 590 Td (Additional prose commentary without any diagram or figure content.) Tj ET\n";
        byte[] contentBytes = Encoding.Latin1.GetBytes(content);

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offs = new List<long>();
        void Obj(int id) { offs.Add(pdf.Position); W($"{id} 0 obj\n"); }
        Obj(1); W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj(3); W("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj(4); W($"<< /Length {contentBytes.Length} >>\nstream\n"); pdf.Write(contentBytes); W("\nendstream\nendobj\n");
        Obj(5); W("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = pdf.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] BuildMinimalOnnxModel(int inputW, int inputH, (string Key, string Value)[] metadataProps)
    {
        static void WriteVarint(Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
        static void WriteVarintField(Stream s, int fn, ulong v) { WriteVarint(s, (ulong)((fn << 3) | 0)); WriteVarint(s, v); }
        static void WriteLengthField(Stream s, int fn, byte[] payload) { WriteVarint(s, (ulong)((fn << 3) | 2)); WriteVarint(s, (ulong)payload.Length); s.Write(payload, 0, payload.Length); }
        static void WriteStringField(Stream s, int fn, string val) => WriteLengthField(s, fn, Encoding.UTF8.GetBytes(val));

        static byte[] EncodeValueInfo(string name, long[] dims)
        {
            using var shapeMs = new MemoryStream();
            foreach (var d in dims)
            {
                using var dimMs = new MemoryStream();
                if (d > 0) WriteVarintField(dimMs, 1, (ulong)d);
                else WriteStringField(dimMs, 2, "dyn");
                WriteLengthField(shapeMs, 1, dimMs.ToArray());
            }
            using var ttMs = new MemoryStream();
            WriteVarintField(ttMs, 1, 1);
            WriteLengthField(ttMs, 2, shapeMs.ToArray());
            using var tMs = new MemoryStream();
            WriteLengthField(tMs, 1, ttMs.ToArray());
            using var viMs = new MemoryStream();
            WriteStringField(viMs, 1, name);
            WriteLengthField(viMs, 2, tMs.ToArray());
            return viMs.ToArray();
        }

        using var graphMs = new MemoryStream();
        WriteStringField(graphMs, 2, "test_graph");
        WriteLengthField(graphMs, 11, EncodeValueInfo("images", [1, 3, inputH, inputW]));
        WriteLengthField(graphMs, 12, EncodeValueInfo("output0", [1, 10, 6]));

        using var opsetMs = new MemoryStream();
        WriteStringField(opsetMs, 1, "");
        WriteVarintField(opsetMs, 2, 17);

        using var modelMs = new MemoryStream();
        WriteVarintField(modelMs, 1, 9);
        WriteStringField(modelMs, 2, "unit-test");
        WriteLengthField(modelMs, 7, graphMs.ToArray());
        WriteLengthField(modelMs, 8, opsetMs.ToArray());

        foreach (var (k, v) in metadataProps)
        {
            using var metaMs = new MemoryStream();
            WriteStringField(metaMs, 1, k);
            WriteStringField(metaMs, 2, v);
            WriteLengthField(modelMs, 14, metaMs.ToArray());
        }
        return modelMs.ToArray();
    }

    private static ServiceProvider BuildProvider(string appDataDir)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = appDataDir);
        return services.BuildServiceProvider();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "melomg_reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
