using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Hosting;
using Xunit;

namespace Melomg.CliTests;

public sealed class BugFixCliRegressionTests
{
    [Fact]
    public async Task Bugs5_10_14_18_20_23_24_26_36_37_44_CliContractRegressionsPass()
    {
        var root = CreateTempRoot();
        var appData = Path.Combine(root, "appdata");
        var workDir = Path.Combine(root, "work");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(workDir);
        var originalCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(root);

            // Bug 23: --json help emits valid JSON envelope; extra args rejected on runtime status & model info/reset
            var helpJsonC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["extract", "--help", "--json"], helpJsonC, appData));
            using (var hDoc = JsonDocument.Parse(helpJsonC.StandardOutput))
            {
                Assert.True(hDoc.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("extract", hDoc.RootElement.GetProperty("data").GetProperty("verb").GetString());
            }

            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "runtime", "status", "extra"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "model", "info", "extra"], new TestConsole(), appData));
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "model", "reset", "extra"], new TestConsole(), appData));

            // Bug 26: Cross-command options & unknown single-dash flags rejected
            var crossOptC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "ls", "--dpi", "150"], crossOptC, appData));
            Assert.Contains("Cli.InvalidOption", crossOptC.StandardError);

            var badDashC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "ls", "-z"], badDashC, appData));
            Assert.Contains("Cli.ParseError", badDashC.StandardError);

            // Bug 14: PDF filename starting with Doc_ is ingested as a file, not misrouted as Document.NotFound
            var docNamedPdf = Path.Combine(root, "Doc_sample_input.pdf");
            await File.WriteAllBytesAsync(docNamedPdf, BuildSamplePdfBytes());

            var ingDocNamedC = new TestConsole();
            int ingCode = await CliApplicationRunner.ExecuteAsync(
                ["--json", "ingest", "Doc_sample_input.pdf", "-o", workDir, "--retain-db", "--offline"], ingDocNamedC, appData);
            Assert.Equal(ExitCodes.Success, ingCode);
            string docId = JsonDocument.Parse(ingDocNamedC.StandardOutput).RootElement.GetProperty("data").GetProperty("id").GetString()!;

            // Bug 18: Trailing selectors on #-addressed extract rejected
            var trailC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", $"{docId}#2.1", "2.2", "-o", outDir], trailC, appData));
            Assert.Contains("Cli.InvalidArgs", trailC.StandardError);

            // Bug 24: --min-conf NaN rejected
            var nanC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(
                ["--json", "get", docId, "--min-conf", "NaN"], nanC, appData));
            Assert.Contains("Figure.InvalidConfidence", nanC.StandardError);

            // Bug 36: Single-figure get rejects list filters
            var singleFilterC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(
                ["--json", "get", $"{docId}#2.1", "--page", "1"], singleFilterC, appData));
            Assert.Contains("Cli.InvalidArgs", singleFilterC.StandardError);

            // Bug 26: Empty figure patch rejected
            var emptyPatchC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(
                ["--json", "patch", $"{docId}#2.1"], emptyPatchC, appData));
            Assert.Contains("Cli.InvalidArgs", emptyPatchC.StandardError);

            // Bug 37: add rejects both --index and 7th positional index
            var dupIdxC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(
                ["--json", "add", docId, "1", "50", "50", "100", "100", "1", "--index", "1"], dupIdxC, appData));
            Assert.Contains("Cli.InvalidArgs", dupIdxC.StandardError);

            // Bug 20: Failed one-shot extract --retain-db leaves zero orphaned Doc_* dirs
            var oneshotCwd = Path.Combine(root, "oneshot_cwd");
            Directory.CreateDirectory(oneshotCwd);
            Directory.SetCurrentDirectory(oneshotCwd);
            var failOneShotC = new TestConsole();
            Assert.Equal(ExitCodes.NotFound, await CliApplicationRunner.ExecuteAsync(
                ["--json", "extract", docNamedPdf, "9999", "--retain-db", "--offline"], failOneShotC, appData));
            var orphanRoot = Path.Combine(oneshotCwd, ".meloberry", "melomg");
            Assert.True(!Directory.Exists(orphanRoot) || Directory.GetDirectories(orphanRoot).Length == 0);
            Directory.SetCurrentDirectory(root);

            // Bug 10 & Bug 37: batch rejects bogus view, wrong-typed fields, unknown set key; accepts numeric quality, mask add, global -o
            var badViewBatch = JsonSerializer.Serialize(new object[] { new { op = "get", addr = docId, view = "BOGUS" } });
            var badViewC = new TestConsole(badViewBatch);
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "batch", "-"], badViewC, appData));
            Assert.Contains("Cli.InvalidView", badViewC.StandardError);

            var badTypeBatch = JsonSerializer.Serialize(new object[] { new { op = "get", addr = docId, page = "not_an_int" } });
            var badTypeC = new TestConsole(badTypeBatch);
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "batch", "-"], badTypeC, appData));
            Assert.Contains("Json.ParseError", badTypeC.StandardError);

            var batchOut = Path.Combine(root, "batch_global_out");
            var goodBatch = JsonSerializer.Serialize(new object[]
            {
                new { op = "mask", addr = $"{docId}#2.1", action = "add", bbox = new[] { 90, 140, 40, 14 }, label = "TestMask", style = "blur" },
                new { op = "extract", addr = $"{docId}#2.1", format = "jpg", quality = 85, retainDb = true },
                new { op = "render", addr = docId, page = 1, format = "jpg", quality = 80 }
            });
            var goodBatchC = new TestConsole(goodBatch);
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "batch", "-", "-o", batchOut], goodBatchC, appData));
            Assert.True(File.Exists(Path.Combine(batchOut, "fig_2.1.jpg")));
            Assert.NotEmpty(Directory.GetFiles(batchOut, "page_*.jpg"));

            // Bug 5 + Bug 26 + Bug 44: check --verbose includes metadata; failed check --json emits error envelope on stderr with 0 stdout
            var checkVerboseC = new TestConsole();
            Assert.Equal(ExitCodes.Success, await CliApplicationRunner.ExecuteAsync(["--json", "check", docId, "--verbose"], checkVerboseC, appData));
            using (var cvDoc = JsonDocument.Parse(checkVerboseC.StandardOutput))
            {
                var data = cvDoc.RootElement.GetProperty("data");
                Assert.True(data.GetProperty("sourcePdfVerified").GetBoolean());
                Assert.Equal(2, data.GetProperty("checkedFigureCount").GetInt32());
            }

            // Corrupt source PDF to force check failure
            await File.AppendAllTextAsync(docNamedPdf, "\n% corrupt-for-bug5");
            var failCheckC = new TestConsole();
            Assert.Equal(ExitCodes.ValidationError, await CliApplicationRunner.ExecuteAsync(["--json", "check", docId], failCheckC, appData));
            Assert.Equal(string.Empty, failCheckC.StandardOutput.Trim());
            using (var fcDoc = JsonDocument.Parse(failCheckC.StandardError))
            {
                Assert.False(fcDoc.RootElement.GetProperty("success").GetBoolean());
                var err = fcDoc.RootElement.GetProperty("error");
                Assert.Equal("Lint.Failed", err.GetProperty("code").GetString());
                var details = err.GetProperty("details");
                Assert.False(details.GetProperty("isClean").GetBoolean());
                Assert.Equal("error", details.GetProperty("issues")[0].GetProperty("severity").GetString());
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
        for (int i = 0; i < rgbPixels.Length; i += 3) { rgbPixels[i] = 45; rgbPixels[i + 1] = 120; rgbPixels[i + 2] = 220; }
        byte[] compressedImg;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(rgbPixels, 0, rgbPixels.Length);
            compressedImg = ms.ToArray();
        }

        string p1Content =
            "q 220 0 0 160 80 500 cm /Im1 Do Q\n" +
            "BT /F1 11 Tf 82 478 Td (Fig. 2.1: Sample Architecture) Tj ET\n" +
            "1.5 w 0.1 0.2 0.3 RG 0.3 0.6 0.9 rg\n" +
            "90 160 50 90 re B 170 160 50 120 re B 80 160 m 260 160 l S\n" +
            "BT /F1 11 Tf 82 138 Td (Fig. 2.2: Throughput Chart) Tj ET\n";

        using var pdf = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offs = new List<long>();
        void Obj(int id) { offs.Add(pdf.Position); W($"{id} 0 obj\n"); }
        Obj(1); W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj(3); W("<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im1 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj(4); W($"<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressedImg.Length} >>\nstream\n");
        pdf.Write(compressedImg, 0, compressedImg.Length);
        W("\nendstream\nendobj\n");
        byte[] p1Bytes = Encoding.Latin1.GetBytes(p1Content);
        Obj(5); W($"<< /Length {p1Bytes.Length} >>\nstream\n");
        pdf.Write(p1Bytes, 0, p1Bytes.Length);
        W("\nendstream\nendobj\n");
        Obj(6); W("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = pdf.Position;
        W($"xref\n0 {offs.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offs) W($"{off:D10} 00000 n \n");
        W($"trailer\n<< /Size {offs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "melomg_clireg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryCleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
