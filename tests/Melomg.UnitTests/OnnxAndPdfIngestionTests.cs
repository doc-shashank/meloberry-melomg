using System.IO.Compression;
using System.Text;
using Melomg.Application.Abstractions;
using Melomg.Domain.Documents;
using Melomg.Domain.Extraction;
using Melomg.Infrastructure;
using Melomg.Infrastructure.Extraction;
using Melomg.Infrastructure.Onnx;
using Melomg.Infrastructure.Pdf;
using Xunit;

namespace Melomg.UnitTests;

public sealed class OnnxAndPdfIngestionTests
{
    [Fact]
    public async Task OnnxEngine_CreatesValidBuiltInModel_ValidatesCustom_And_RejectsCorrupt()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var engine = new OnnxProtobufEngine(opts);

            var builtIn = await engine.GetBuiltInDefaultProfileAsync();
            Assert.True(File.Exists(builtIn.ModelPath));

            var metaRes = await engine.InspectAndValidateModelAsync(builtIn.ModelPath);
            Assert.True(metaRes.IsSuccess);
            Assert.Equal(9, metaRes.Value.IrVersion);
            Assert.Equal(17, metaRes.Value.OpsetVersion);
            Assert.Equal(640, metaRes.Value.InputWidth);
            Assert.Contains("figure", metaRes.Value.SupportedClasses);

            // Custom valid ONNX protobuf model
            var customPath = Path.Combine(tempRoot, "custom_layout.onnx");
            await File.WriteAllBytesAsync(customPath, OnnxProtobufEngine.BuildCompactDocLayoutOnnxModel(512, 512, 18));
            var customRes = await engine.InspectAndValidateModelAsync(customPath);
            Assert.True(customRes.IsSuccess);
            Assert.Equal(512, customRes.Value.InputWidth);
            Assert.Equal(18, customRes.Value.OpsetVersion);

            // Corrupt non-ONNX file must fail with Model.InvalidModelFormat
            var badPath = Path.Combine(tempRoot, "corrupt.onnx");
            await File.WriteAllTextAsync(badPath, "NOT_AN_ONNX_MODEL_PROTO_PAYLOAD");
            var badRes = await engine.InspectAndValidateModelAsync(badPath);
            Assert.False(badRes.IsSuccess);
            Assert.Equal("Model.InvalidModelFormat", badRes.Error!.Code);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task HybridPdfEngine_DetectsEmbeddedImageAndVectorFigures_And_ExtractsPngAndJpeg()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var renderer = new WindowsPdfPageRenderer();
            var pdfEngine = new HybridOnnxPdfIngestionEngine(onnx, renderer);
            var extractor = new PngJpegFigureExtractor(renderer, pdfEngine);

            var samplePdfPath = Path.Combine(tempRoot, "sample_paper.pdf");
            await File.WriteAllBytesAsync(samplePdfPath, BuildSampleMultiPagePdfBytes());

            var profile = await onnx.GetBuiltInDefaultProfileAsync();
            var analysisRes = await pdfEngine.AnalyzePdfAsync(samplePdfPath, profile);
            Assert.True(analysisRes.IsSuccess);

            var analysis = analysisRes.Value;
            Assert.Equal("Sample Multi-Figure Paper", analysis.SuggestedTitle);
            Assert.Equal(2, analysis.Pages.Count);
            Assert.True(analysis.Candidates.Count >= 3, $"Expected >= 3 detected figures, got {analysis.Candidates.Count}");

            // Check Page 1 image figure + caption association
            var p1Figs = analysis.Candidates.Where(c => c.PageNumber == 1).ToList();
            Assert.Contains(p1Figs, c => c.Caption is not null && c.Caption.Contains("Figure 1", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(p1Figs, c => c.Category == FigureCategory.Chart && c.Caption is not null && c.Caption.Contains("Chart 1", StringComparison.OrdinalIgnoreCase));

            // Check Page 2 diagram figure
            var p2Figs = analysis.Candidates.Where(c => c.PageNumber == 2).ToList();
            Assert.Contains(p2Figs, c => c.Category == FigureCategory.Diagram);

            // Build PdfDocument without copying source PDF into workspace
            var workspaceDir = Path.Combine(tempRoot, "Doc_20261120_010000_001");
            var initRes = await pdfEngine.InitializeWorkspaceAsync(workspaceDir);
            Assert.True(initRes.IsSuccess);
            Assert.False(File.Exists(Path.Combine(workspaceDir, "source.pdf")));

            var doc = PdfDocument.Create(
                "Doc_20261120_010000_001",
                analysis.SuggestedTitle,
                samplePdfPath,
                samplePdfPath,
                analysis.Sha256Hash,
                workspaceDir,
                analysis.ModelUsed.ModelName,
                analysis.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                analysis.Pages).Value;

            int seq = 1;
            foreach (var cand in analysis.Candidates)
            {
                doc.AddFigure($"Fig_20261120_010000_{seq:000}", cand.PageNumber, cand.Category, cand.BoundingBox, cand.Confidence, cand.Caption, cand.Label, cand.Source);
                seq++;
            }

            var pngOutDir = Path.Combine(tempRoot, "out_png");
            var pngRes = await extractor.ExtractFigureAsync(
                doc,
                doc.Figures[0],
                ExtractionOptions.Create("png", 150, 8).Value,
                pngOutDir);

            Assert.True(pngRes.IsSuccess);
            Assert.True(File.Exists(pngRes.Value.OutputImagePath));
            Assert.Null(pngRes.Value.MetadataJsonPath);
            Assert.Empty(Directory.GetFiles(pngOutDir, "*.meta.json"));
            Assert.True((await extractor.VerifyExtractedImageAsync(pngRes.Value.OutputImagePath)).IsSuccess);
            byte[] pngHeader = await File.ReadAllBytesAsync(pngRes.Value.OutputImagePath);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], pngHeader[..8]);

            // Opt-in metadata writes <figID>.meta.json
            var pngMetaOutDir = Path.Combine(tempRoot, "out_png_meta");
            var pngMetaRes = await extractor.ExtractFigureAsync(
                doc,
                doc.Figures[0],
                ExtractionOptions.Create("png", 150, 8, includeMetadata: true).Value,
                pngMetaOutDir);
            Assert.True(pngMetaRes.IsSuccess);
            Assert.NotNull(pngMetaRes.Value.MetadataJsonPath);
            Assert.True(File.Exists(pngMetaRes.Value.MetadataJsonPath));

            var jpgOutDir = Path.Combine(tempRoot, "out_jpg");
            var jpgRes = await extractor.ExtractFigureAsync(
                doc,
                doc.Figures[1],
                ExtractionOptions.Create("jpg", 200, 10).Value,
                jpgOutDir);

            Assert.True(jpgRes.IsSuccess);
            Assert.True((await extractor.VerifyExtractedImageAsync(jpgRes.Value.OutputImagePath)).IsSuccess);
            byte[] jpgBytes = await File.ReadAllBytesAsync(jpgRes.Value.OutputImagePath);
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], jpgBytes[..4]);
            Assert.Equal([0xFF, 0xD9], jpgBytes[^2..]);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task HybridPdfEngine_UnpacksObjStm_And_RendersPdf16ViaWindowsRenderer()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var renderer = new WindowsPdfPageRenderer();
            var pdfEngine = new HybridOnnxPdfIngestionEngine(onnx, renderer);
            var extractor = new PngJpegFigureExtractor(renderer, pdfEngine);
            var profile = await onnx.GetBuiltInDefaultProfileAsync();

            // 1. Produce real baseline JPEG bytes from a gradient figure via extractor
            var seedPdfPath = Path.Combine(tempRoot, "seed.pdf");
            await File.WriteAllBytesAsync(seedPdfPath, BuildSampleMultiPagePdfBytes());
            var seedAnalysis = (await pdfEngine.AnalyzePdfAsync(seedPdfPath, profile)).Value;
            var seedWs = Path.Combine(tempRoot, "seed_ws");
            Assert.True((await pdfEngine.InitializeWorkspaceAsync(seedWs)).IsSuccess);
            var seedDoc = PdfDocument.Create(
                "Doc_Seed",
                "Seed",
                seedPdfPath,
                seedPdfPath,
                seedAnalysis.Sha256Hash,
                seedWs,
                seedAnalysis.ModelUsed.ModelName,
                seedAnalysis.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                seedAnalysis.Pages).Value;
            var seedCand = seedAnalysis.Candidates[0];
            seedDoc.AddFigure("Fig_Seed_1", seedCand.PageNumber, seedCand.Category, seedCand.BoundingBox, seedCand.Confidence, seedCand.Caption, seedCand.Label, seedCand.Source);
            var seedJpgRes = await extractor.ExtractFigureAsync(seedDoc, seedDoc.Figures[0], ExtractionOptions.Create("jpg", 72, 0).Value, Path.Combine(tempRoot, "seed_jpg"));
            Assert.True(seedJpgRes.IsSuccess);
            byte[] dctJpegBytes = await File.ReadAllBytesAsync(seedJpgRes.Value.OutputImagePath);

            // 2. Build a spec-compliant PDF 1.6 containing:
            //    - /Type /XRef cross-reference stream
            //    - /Type /ObjStm storing /Type /Pages and /Type /Page
            //    - /FlateDecode + /Predictor 15 RGB Image XObject (/ImPng)
            //    - /DCTDecode JPEG Image XObject (/ImJpg)
            var pdf16Path = Path.Combine(tempRoot, "pdf16_features.pdf");
            await File.WriteAllBytesAsync(pdf16Path, BuildPdf16WithObjStmPredictorAndJpeg(dctJpegBytes));

            var analysisRes = await pdfEngine.AnalyzePdfAsync(pdf16Path, profile);
            Assert.True(analysisRes.IsSuccess);
            var analysis = analysisRes.Value;
            Assert.Single(analysis.Pages);
            Assert.True(analysis.Candidates.Count >= 2, $"Expected >= 2 figures, got {analysis.Candidates.Count}");

            // 3. Extract both figures to PNG via native Windows.Data.Pdf and verify decoded rendered pixels contain non-white image content
            var wsDir = Path.Combine(tempRoot, "Doc_Pdf16");
            Assert.True((await pdfEngine.InitializeWorkspaceAsync(wsDir)).IsSuccess);
            var doc = PdfDocument.Create(
                "Doc_Pdf16",
                "PDF 1.6 Test",
                pdf16Path,
                pdf16Path,
                analysis.Sha256Hash,
                wsDir,
                analysis.ModelUsed.ModelName,
                analysis.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                analysis.Pages).Value;

            int seq = 1;
            foreach (var c in analysis.Candidates)
            {
                doc.AddFigure($"Fig_Pdf16_{seq++}", c.PageNumber, c.Category, c.BoundingBox, c.Confidence, c.Caption, c.Label, c.Source);
            }

            foreach (var fig in doc.Figures)
            {
                var pngOut = await extractor.ExtractFigureAsync(doc, fig, ExtractionOptions.Create("png", 100, 4).Value, Path.Combine(tempRoot, "out16"));
                Assert.True(pngOut.IsSuccess, pngOut.Error?.Description);
                Assert.Equal("windows-pdf", pngOut.Value.RenderEngine);
                Assert.True((await extractor.VerifyExtractedImageAsync(pngOut.Value.OutputImagePath)).IsSuccess);
                Assert.True(pngOut.Value.FileSizeBytes > 300);

                var (_, _, rgb) = DecodePngRgbForTest(await File.ReadAllBytesAsync(pngOut.Value.OutputImagePath));
                Assert.True(CountDarkPixels(rgb) + CountColoredPixels(rgb) > 100, "Native Windows.Data.Pdf crop should render non-blank content.");
            }
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task ImageQualityOption_ScalesJpegSize_WritesPngPhysChunk_And_AppliesPresetDpi()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var renderer = new WindowsPdfPageRenderer();
            var pdfEngine = new HybridOnnxPdfIngestionEngine(onnx, renderer);
            var extractor = new PngJpegFigureExtractor(renderer, pdfEngine);
            var profile = await onnx.GetBuiltInDefaultProfileAsync();

            var pdfPath = Path.Combine(tempRoot, "quality_src.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildSampleMultiPagePdfBytes());
            var analysis = (await pdfEngine.AnalyzePdfAsync(pdfPath, profile)).Value;

            var wsDir = Path.Combine(tempRoot, "Doc_Q");
            Assert.True((await pdfEngine.InitializeWorkspaceAsync(wsDir)).IsSuccess);
            var doc = PdfDocument.Create(
                "Doc_Q",
                "Quality Test",
                pdfPath,
                pdfPath,
                analysis.Sha256Hash,
                wsDir,
                analysis.ModelUsed.ModelName,
                analysis.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                analysis.Pages).Value;

            var cand = analysis.Candidates[0];
            doc.AddFigure("Fig_Q_1", cand.PageNumber, cand.Category, cand.BoundingBox, cand.Confidence, cand.Caption, cand.Label, cand.Source);
            var fig = doc.Figures[0];

            var jpgLowRes = await extractor.ExtractFigureAsync(doc, fig, ExtractionOptions.Create("jpg", 150, 4, quality: "20").Value, Path.Combine(tempRoot, "jpg_q20"));
            var jpgHighRes = await extractor.ExtractFigureAsync(doc, fig, ExtractionOptions.Create("jpg", 150, 4, quality: "95").Value, Path.Combine(tempRoot, "jpg_q95"));

            Assert.True(jpgLowRes.IsSuccess && jpgHighRes.IsSuccess);
            Assert.Equal(20, jpgLowRes.Value.Quality);
            Assert.Equal(95, jpgHighRes.Value.Quality);
            Assert.True(jpgLowRes.Value.FileSizeBytes < jpgHighRes.Value.FileSizeBytes,
                $"Expected q=20 JPEG ({jpgLowRes.Value.FileSizeBytes} B) < q=95 JPEG ({jpgHighRes.Value.FileSizeBytes} B).");
            Assert.True((await extractor.VerifyExtractedImageAsync(jpgLowRes.Value.OutputImagePath)).IsSuccess);
            Assert.True((await extractor.VerifyExtractedImageAsync(jpgHighRes.Value.OutputImagePath)).IsSuccess);

            var pngHighPreset = ExtractionOptions.Create("png", null, 4, quality: "high").Value;
            Assert.Equal(240, pngHighPreset.Dpi);
            Assert.Equal(92, pngHighPreset.Quality);
            var pngRes = await extractor.ExtractFigureAsync(doc, fig, pngHighPreset, Path.Combine(tempRoot, "png_high"));
            Assert.True(pngRes.IsSuccess);
            Assert.Equal(92, pngRes.Value.Quality);

            byte[] pngBytes = await File.ReadAllBytesAsync(pngRes.Value.OutputImagePath);
            uint? ppmX = TryReadPngPhysPixelsPerMeterX(pngBytes);
            Assert.NotNull(ppmX);
            Assert.Equal((uint)Math.Round(240 / 0.0254), ppmX!.Value);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static byte[] BuildPdf16WithObjStmPredictorAndJpeg(byte[] dctJpegBytes)
    {
        // Build 16x12 RGB image with PNG Predictor 15 (filter byte 1 = Sub)
        const int imgW = 16;
        const int imgH = 12;
        int rowBytes = imgW * 3;
        var predictedRows = new byte[(rowBytes + 1) * imgH];
        for (int y = 0; y < imgH; y++)
        {
            int rowStart = y * (rowBytes + 1);
            predictedRows[rowStart] = 1; // PNG Sub filter
            var rawRow = new byte[rowBytes];
            for (int x = 0; x < imgW; x++)
            {
                rawRow[x * 3] = (byte)(10 + x * 12);
                rawRow[x * 3 + 1] = (byte)(180 - y * 8);
                rawRow[x * 3 + 2] = 90;
            }
            for (int i = 0; i < rowBytes; i++)
            {
                byte left = i >= 3 ? rawRow[i - 3] : (byte)0;
                predictedRows[rowStart + 1 + i] = (byte)(rawRow[i] - left);
            }
        }

        byte[] compressedPngImg;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(predictedRows, 0, predictedRows.Length);
            compressedPngImg = ms.ToArray();
        }

        // Pack obj 2 (/Pages) and obj 3 (/Page) inside /ObjStm (obj 7)
        string obj2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>";
        string obj3 = "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /ImPng 4 0 R /ImJpg 5 0 R >> /Font << /F1 9 0 R >> >> /Contents 6 0 R >>";
        string objStmHeader = $"2 0 3 {obj2.Length + 1} ";
        byte[] objStmPlain = Encoding.Latin1.GetBytes(objStmHeader + obj2 + "\n" + obj3);
        int firstOffset = Encoding.Latin1.GetByteCount(objStmHeader);

        byte[] compressedObjStm;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(objStmPlain, 0, objStmPlain.Length);
            compressedObjStm = ms.ToArray();
        }

        string p1Content =
            "q 0.1 0 0 0.1 0 0 cm\n" +
            "q 2000 0 0 1500 800 5000 cm /ImPng Do Q\n" +
            "q 2000 0 0 1500 800 2200 cm /ImJpg Do Q\n" +
            "10 0 0 10 0 0 cm BT /F1 10 Tf 1 0 0 1 80 480 Tm [(Fig.)20( 1.1 )]TJ ET\n" +
            "BT /F1 10 Tf 1 0 0 1 115 480 Tm [(PNG Predictor 15 Diagram)]TJ ET\n" +
            "BT /F1 10 Tf 1 0 0 1 80 200 Tm [(Fig.)20( 1.2 )]TJ ET\n" +
            "BT /F1 10 Tf 1 0 0 1 115 200 Tm [(DCT JPEG Photograph)]TJ ET\n" +
            "Q\n";

        using var pdf = new MemoryStream();
        void WriteAscii(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            pdf.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.6\n");

        uint offset1 = (uint)pdf.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        uint offset7 = (uint)pdf.Position;
        WriteAscii($"7 0 obj\n<< /Type /ObjStm /N 2 /First {firstOffset} /Filter /FlateDecode /Length {compressedObjStm.Length} >>\nstream\n");
        pdf.Write(compressedObjStm, 0, compressedObjStm.Length);
        WriteAscii("\nendstream\nendobj\n");

        uint offset4 = (uint)pdf.Position;
        WriteAscii($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Colors 3 /Columns {imgW} /Predictor 15 >> /Length {compressedPngImg.Length} >>\nstream\n");
        pdf.Write(compressedPngImg, 0, compressedPngImg.Length);
        WriteAscii("\nendstream\nendobj\n");

        uint offset5 = (uint)pdf.Position;
        WriteAscii($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {dctJpegBytes.Length} >>\nstream\n");
        pdf.Write(dctJpegBytes, 0, dctJpegBytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        uint offset6 = (uint)pdf.Position;
        byte[] pBytes = Encoding.Latin1.GetBytes(p1Content);
        WriteAscii($"6 0 obj\n<< /Length {pBytes.Length} >>\nstream\n");
        pdf.Write(pBytes, 0, pBytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        uint offset9 = (uint)pdf.Position;
        WriteAscii("9 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        uint offset8 = (uint)pdf.Position;
        // 10 entries (objects 0..9), /W [1 4 1] => 60 bytes
        var xrefData = new byte[10 * 6];
        static void WriteXrefEntry(byte[] buf, int idx, byte type, uint field2, byte field3)
        {
            int o = idx * 6;
            buf[o] = type;
            buf[o + 1] = (byte)(field2 >> 24);
            buf[o + 2] = (byte)(field2 >> 16);
            buf[o + 3] = (byte)(field2 >> 8);
            buf[o + 4] = (byte)field2;
            buf[o + 5] = field3;
        }
        WriteXrefEntry(xrefData, 0, 0, 0, 255);
        WriteXrefEntry(xrefData, 1, 1, offset1, 0);
        WriteXrefEntry(xrefData, 2, 2, 7, 0);
        WriteXrefEntry(xrefData, 3, 2, 7, 1);
        WriteXrefEntry(xrefData, 4, 1, offset4, 0);
        WriteXrefEntry(xrefData, 5, 1, offset5, 0);
        WriteXrefEntry(xrefData, 6, 1, offset6, 0);
        WriteXrefEntry(xrefData, 7, 1, offset7, 0);
        WriteXrefEntry(xrefData, 8, 1, offset8, 0);
        WriteXrefEntry(xrefData, 9, 1, offset9, 0);

        WriteAscii($"8 0 obj\n<< /Type /XRef /Size 10 /Root 1 0 R /W [1 4 1] /Length {xrefData.Length} >>\nstream\n");
        pdf.Write(xrefData, 0, xrefData.Length);
        WriteAscii($"\nendstream\nendobj\nstartxref\n{offset8}\n%%EOF\n");
        return pdf.ToArray();
    }

    internal static byte[] BuildSampleMultiPagePdfBytes()
    {
        // Build a valid 2-page PDF with:
        // - Page 1: /FlateDecode 32x24 RGB Image XObject (/Im1) + Figure 1 caption + Vector bar chart + Chart 1 caption
        // - Page 2: Vector architecture diagram + Diagram 2 caption
        var rgbPixels = new byte[32 * 24 * 3];
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int idx = (y * 32 + x) * 3;
                rgbPixels[idx] = (byte)(40 + x * 6);
                rgbPixels[idx + 1] = (byte)(80 + y * 6);
                rgbPixels[idx + 2] = 200;
            }
        }

        byte[] compressedImg;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(rgbPixels, 0, rgbPixels.Length);
            compressedImg = ms.ToArray();
        }

        string p1Content =
            "q 240 0 0 180 80 480 cm /Im1 Do Q\n" +
            "BT /F1 11 Tf 82 456 Td (Figure 1: Neural Feature Activation Map) Tj ET\n" +
            "1.5 w 0.1 0.2 0.3 RG\n" +
            "80 140 m 80 290 l 360 290 m 80 140 m 360 140 l S\n" +
            "0.2 0.5 0.9 rg 110 140 40 90 re B\n" +
            "0.9 0.4 0.2 rg 180 140 40 130 re B\n" +
            "0.2 0.7 0.4 rg 250 140 40 110 re B\n" +
            "BT /F1 11 Tf 82 118 Td (Chart 1: Latency Comparison Across Models) Tj ET\n";

        string p2Content =
            "2.0 w 0.15 0.25 0.45 RG 0.88 0.93 0.99 rg\n" +
            "90 500 120 70 re B\n" +
            "260 500 120 70 re B\n" +
            "430 500 100 70 re B\n" +
            "210 535 m 260 535 l S\n" +
            "380 535 m 430 535 l S\n" +
            "BT /F1 11 Tf 95 478 Td (Diagram 2: Hybrid ONNX Extraction Pipeline) Tj ET\n";

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

        // 1: Catalog
        BeginObj(1);
        WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        BeginObj(2);
        WriteAscii("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>\nendobj\n");

        // 3: Page 1
        BeginObj(3);
        WriteAscii("<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im1 5 0 R >> /Font << /F1 6 0 R >> >> /Contents 7 0 R >>\nendobj\n");

        // 4: Page 2
        BeginObj(4);
        WriteAscii("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 6 0 R >> >> /Contents 8 0 R >>\nendobj\n");

        // 5: Image XObject
        BeginObj(5);
        WriteAscii($"<< /Type /XObject /Subtype /Image /Width 32 /Height 24 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressedImg.Length} >>\nstream\n");
        pdf.Write(compressedImg, 0, compressedImg.Length);
        WriteAscii("\nendstream\nendobj\n");

        // 6: Font
        BeginObj(6);
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // 7: Page 1 content
        byte[] p1Bytes = Encoding.Latin1.GetBytes(p1Content);
        BeginObj(7);
        WriteAscii($"<< /Length {p1Bytes.Length} >>\nstream\n");
        pdf.Write(p1Bytes, 0, p1Bytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        // 8: Page 2 content
        byte[] p2Bytes = Encoding.Latin1.GetBytes(p2Content);
        BeginObj(8);
        WriteAscii($"<< /Length {p2Bytes.Length} >>\nstream\n");
        pdf.Write(p2Bytes, 0, p2Bytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        // 9: Info
        BeginObj(9);
        WriteAscii("<< /Title (Sample Multi-Figure Paper) >>\nendobj\n");

        long xrefPos = pdf.Position;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii($"{off:D10} 00000 n \n");

        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 9 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return pdf.ToArray();
    }

    [Fact]
    public async Task WindowsPdfRenderer_CropsRenderedTextAndVectors_IncludesCaptionOption_And_ObfuscatesLabels()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var renderer = new WindowsPdfPageRenderer();
            var pdfEngine = new HybridOnnxPdfIngestionEngine(onnx, renderer);
            var extractor = new PngJpegFigureExtractor(renderer, pdfEngine);

            var pdfPath = Path.Combine(tempRoot, "labeled_diagram.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildLabeledDiagramPdfBytes(useInsetCropBox: false));

            var profile = await onnx.GetBuiltInDefaultProfileAsync();
            var analysisRes = await pdfEngine.AnalyzePdfAsync(pdfPath, profile);
            Assert.True(analysisRes.IsSuccess);
            var analysis = analysisRes.Value;
            Assert.NotEmpty(analysis.Candidates);

            var figCand = analysis.Candidates[0];
            Assert.NotNull(figCand.Caption);
            Assert.NotNull(figCand.CaptionBoundingBox);

            // Detect labels inside diagram
            var labelsRes = await pdfEngine.DetectLabelsInRegionAsync(pdfPath, 1, figCand.BoundingBox);
            Assert.True(labelsRes.IsSuccess);
            Assert.Contains(labelsRes.Value, l => l.Text.Contains("Neutrophil", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(labelsRes.Value, l => l.Text.Contains("Endothelial", StringComparison.OrdinalIgnoreCase));

            var wsDir = Path.Combine(tempRoot, "Doc_WinPdf");
            Assert.True((await pdfEngine.InitializeWorkspaceAsync(wsDir)).IsSuccess);
            var doc = PdfDocument.Create(
                "Doc_WinPdf",
                "Labeled Diagram",
                pdfPath,
                pdfPath,
                analysis.Sha256Hash,
                wsDir,
                analysis.ModelUsed.ModelName,
                analysis.EffectiveConfidenceThreshold,
                DateTimeOffset.UtcNow,
                analysis.Pages).Value;

            doc.AddFigure(
                "Fig_WinPdf_1",
                figCand.PageNumber,
                figCand.Category,
                figCand.BoundingBox,
                figCand.Confidence,
                figCand.Caption,
                figCand.Label,
                figCand.Source,
                captionBoundingBox: figCand.CaptionBoundingBox);

            var fig = doc.Figures[0];
            var outDir = Path.Combine(tempRoot, "crops");

            // 1. Default crop via Windows.Data.Pdf: excludes caption, renders real Helvetica glyph pixels
            var cleanRes = await extractor.ExtractFigureAsync(doc, fig, ExtractionOptions.Create("png", 150, 8).Value, outDir);
            Assert.True(cleanRes.IsSuccess);
            Assert.Equal("windows-pdf", cleanRes.Value.RenderEngine);
            Assert.False(cleanRes.Value.IncludedCaption);

            var (cleanW, cleanH, cleanRgb) = DecodePngRgbForTest(await File.ReadAllBytesAsync(cleanRes.Value.OutputImagePath));
            int darkPixelsClean = CountDarkPixels(cleanRgb);
            Assert.True(darkPixelsClean > 200, $"Expected Windows.Data.Pdf to render dark text/vector pixels inside figure crop, got {darkPixelsClean}");

            // 2. Include-caption crop increases height substantially
            var capOutDir = Path.Combine(tempRoot, "crops_cap");
            var capRes = await extractor.ExtractFigureAsync(
                doc,
                fig,
                ExtractionOptions.Create("png", 150, 8, includeCaption: true).Value,
                capOutDir);
            Assert.True(capRes.IsSuccess);
            Assert.True(capRes.Value.IncludedCaption);
            Assert.True(capRes.Value.PixelHeight > cleanH + 10, $"Expected include-caption height > {cleanH + 10}, got {capRes.Value.PixelHeight}");

            // 3. Selective solid red obfuscation of "Neutrophil" label
            var solidDir = Path.Combine(tempRoot, "crops_solid");
            var solidRes = await extractor.ExtractFigureAsync(
                doc,
                fig,
                ExtractionOptions.Create(
                    "png",
                    150,
                    8,
                    obfuscateLabelPatterns: "Neutrophil",
                    obfuscateStyle: "solid",
                    obfuscateColorHex: "#FF0000").Value,
                solidDir);
            Assert.True(solidRes.IsSuccess);
            Assert.Single(solidRes.Value.AppliedMasks);
            var maskInfo = solidRes.Value.AppliedMasks[0];
            Assert.Contains("Neutrophil", maskInfo.Text ?? "", StringComparison.OrdinalIgnoreCase);

            var (solidW, solidH, solidRgb) = DecodePngRgbForTest(await File.ReadAllBytesAsync(solidRes.Value.OutputImagePath));
            Assert.Equal(cleanW, solidW);
            Assert.Equal(cleanH, solidH);
            int centerPx = ((maskInfo.PixelY + maskInfo.PixelHeight / 2) * solidW + (maskInfo.PixelX + maskInfo.PixelWidth / 2)) * 3;
            Assert.Equal(255, solidRgb[centerPx]);
            Assert.Equal(0, solidRgb[centerPx + 1]);
            Assert.Equal(0, solidRgb[centerPx + 2]);

            // 4. Whiteout, blur, pixelate all labels + --no-masks check
            foreach (var style in new[] { "whiteout", "blur", "pixelate" })
            {
                var styleDir = Path.Combine(tempRoot, $"crops_{style}");
                var styleRes = await extractor.ExtractFigureAsync(
                    doc,
                    fig,
                    ExtractionOptions.Create("png", 150, 8, obfuscateAllLabels: true, obfuscateStyle: style).Value,
                    styleDir);
                Assert.True(styleRes.IsSuccess);
                Assert.True(styleRes.Value.AppliedMasks.Count >= 2);
            }

            // Persistent mask + --no-masks leaves clean output identical to cleanRes
            var lblMatch = labelsRes.Value.First(l => l.Text.Contains("Endothelial", StringComparison.OrdinalIgnoreCase));
            Assert.True(fig.AddLabelMask(lblMatch.BoundingBox, doc.GetPage(1)!, lblMatch.Text, ObfuscationStyle.Solid, "#00FF00").IsSuccess);
            var noMaskRes = await extractor.ExtractFigureAsync(
                doc,
                fig,
                ExtractionOptions.Create("png", 150, 8, ignoreSavedMasks: true).Value,
                Path.Combine(tempRoot, "crops_nomask"));
            Assert.True(noMaskRes.IsSuccess);
            Assert.Empty(noMaskRes.Value.AppliedMasks);

            // 5. Full-page render & --overlay comparison
            var pagesDir = Path.Combine(tempRoot, "pages");
            var cleanPage = await extractor.ExportPagesAsync(doc, [1], RenderPageOptions.Create("png", 100, overlay: false).Value, pagesDir);
            var overlayPage = await extractor.ExportPagesAsync(doc, [1], RenderPageOptions.Create("png", 100, overlay: true).Value, pagesDir);
            Assert.True(cleanPage.IsSuccess && overlayPage.IsSuccess);
            Assert.False(cleanPage.Value[0].OverlayEnabled);
            Assert.True(overlayPage.Value[0].OverlayEnabled);
            Assert.True(overlayPage.Value[0].OverlayFigureCount >= 1);
            byte[] pCleanBytes = await File.ReadAllBytesAsync(cleanPage.Value[0].OutputImagePath);
            byte[] pOverlayBytes = await File.ReadAllBytesAsync(overlayPage.Value[0].OutputImagePath);
            Assert.False(pCleanBytes.AsSpan().SequenceEqual(pOverlayBytes));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task CropBoxInset_And_VisualPixelFeatureDetection_AlignWithRenderedPage()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var opts = new MelomgStorageOptions { AppDataDirectory = Path.Combine(tempRoot, "appdata") };
            var onnx = new OnnxProtobufEngine(opts);
            var renderer = new WindowsPdfPageRenderer();
            var pdfEngine = new HybridOnnxPdfIngestionEngine(onnx, renderer);

            var pdfPath = Path.Combine(tempRoot, "cropbox_paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, BuildLabeledDiagramPdfBytes(useInsetCropBox: true));

            var profile = await onnx.GetBuiltInDefaultProfileAsync();
            var analysisRes = await pdfEngine.AnalyzePdfAsync(pdfPath, profile);
            Assert.True(analysisRes.IsSuccess);

            var page = analysisRes.Value.Pages[0];
            Assert.Equal(532, page.WidthPts, 1);  // CropBox [40 40 572 752]
            Assert.Equal(712, page.HeightPts, 1);
            Assert.NotEmpty(analysisRes.Value.Candidates);

            var cand = analysisRes.Value.Candidates[0];
            Assert.True(cand.BoundingBox.ValidateWithinPage(page.WidthPts, page.HeightPts).IsSuccess);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static byte[] BuildLabeledDiagramPdfBytes(bool useInsetCropBox)
    {
        string mediaBox = "[0 0 612 792]";
        string cropEntry = useInsetCropBox ? " /CropBox [40 40 572 752]" : "";

        // Drawn with pure vectors (filled panels, arrows) and Helvetica diagram labels (no raster XObject!)
        string p1Content =
            "2.0 w 0.12 0.25 0.45 RG 0.85 0.92 0.99 rg\n" +
            "110 420 150 90 re B\n" +
            "0.98 0.88 0.82 rg 340 420 150 90 re B\n" +
            "260 465 m 340 465 l S\n" +
            "332 470 m 340 465 l 332 460 l S\n" +
            "0 0 0 rg 0 0 0 RG\n" +
            "BT /F1 14 Tf 125 462 Td (Neutrophil) Tj ET\n" +
            "BT /F1 14 Tf 352 462 Td (Endothelial cell) Tj ET\n" +
            "BT /F1 12 Tf 115 395 Td (Fig. 1.1: Leukocyte recruitment cascade) Tj ET\n" +
            "BT /F1 11 Tf 115 380 Td (across vascular endothelium.) Tj ET\n";

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
        WriteAscii($"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox {mediaBox} >>\nendobj\n");
        BeginObj(3);
        WriteAscii($"<< /Type /Page /Parent 2 0 R{cropEntry} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        BeginObj(4);
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        byte[] pBytes = Encoding.Latin1.GetBytes(p1Content);
        BeginObj(5);
        WriteAscii($"<< /Length {pBytes.Length} >>\nstream\n");
        pdf.Write(pBytes, 0, pBytes.Length);
        WriteAscii("\nendstream\nendobj\n");

        long xrefPos = pdf.Position;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii($"{off:D10} 00000 n \n");

        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return pdf.ToArray();
    }

    [Fact]
    public void TrimRunningHeaderAndFooter_SeparatesHeaderAcrossWhitespaceGap_WithoutClippingFigure()
    {
        const int pageW = 612;
        const int pageH = 792;
        var rgb = new byte[pageW * pageH * 3];
        Array.Fill(rgb, (byte)255);

        void PaintBand(int y0, int y1, int x0, int x1, byte val)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int idx = (y * pageW + x) * 3;
                    rgb[idx] = val;
                    rgb[idx + 1] = val;
                    rgb[idx + 2] = val;
                }
            }
        }

        // Running header ink at y=28..42, clean whitespace gap y=43..55, real figure ink at y=56..220
        PaintBand(28, 39, 40, 560, 40);
        PaintBand(42, 42, 40, 560, 80);
        PaintBand(56, 220, 40, 560, 90);

        var bmp = new RenderedRgbBitmap(pageW, pageH, 72, rgb, "test");
        var spans = new List<ParsedTextSpan>
        {
            new(40, 28, 220, 10, "CHAPTER 2 Cell Injury", 12)
        };

        var noisyBox = new BoundingBox(38, 0, 524, 222);
        var trimmed = HybridOnnxPdfIngestionEngine.TrimRunningHeaderAndFooter(noisyBox, bmp, spans, pageW, pageH);

        Assert.InRange(trimmed.Y, 52.0, 58.0);
        Assert.Equal(noisyBox.Bottom, trimmed.Bottom, precision: 2);
    }

    [Fact]
    public void AbsorbContainedSubFigures_DropsEnclosedSubPanelsEvenWithHigherConfidence_WhileKeepingDistinctFigures()
    {
        var outer = new DetectedFigureCandidate(
            28,
            FigureCategory.Figure,
            new BoundingBox(55, 55, 240, 600),
            0.9192,
            "Fig. 2.29 Mechanisms of intracellular accumulations.",
            "Fig. 2.29",
            DetectionSource.Hybrid);

        // Inner cell sub-panel with higher confidence (0.96) inside outer
        var innerSubPanel = new DetectedFigureCandidate(
            28,
            FigureCategory.Figure,
            new BoundingBox(68, 70, 95, 115),
            0.9600,
            null,
            "Figure p28.2",
            DetectionSource.PdfXObject);

        // Side-by-side separate labeled figure in right column
        var distinctSibling = new DetectedFigureCandidate(
            28,
            FigureCategory.Figure,
            new BoundingBox(320, 55, 240, 250),
            0.9100,
            "Fig. 2.30 Distinct figure.",
            "Fig. 2.30",
            DetectionSource.Hybrid);

        // Enclosed distinct figure WITH its own real figure number caption should NOT be dropped
        var distinctNested = new DetectedFigureCandidate(
            28,
            FigureCategory.Figure,
            new BoundingBox(335, 80, 120, 100),
            0.8800,
            "Fig. 2.31 Inset micrograph.",
            "Fig. 2.31",
            DetectionSource.Hybrid);

        var kept = HybridOnnxPdfIngestionEngine.AbsorbContainedSubFigures([outer, innerSubPanel, distinctSibling, distinctNested]);

        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, c => c.Label == "Figure p28.2");
        Assert.Contains(kept, c => c.Label == "Fig. 2.29");
        Assert.Contains(kept, c => c.Label == "Fig. 2.30");
        Assert.Contains(kept, c => c.Label == "Fig. 2.31");
    }

    [Fact]
    public void CarveCaptionOutOfFigureBox_And_MergeTextSpansOnSameLine_RespectCaptionAndColumnGutter()
    {
        var fig = new BoundingBox(55, 55, 240, 604.8);
        var bottomCap = new BoundingBox(55, 652, 230, 22);
        var carvedBottom = HybridOnnxPdfIngestionEngine.CarveCaptionOutOfFigureBox(fig, bottomCap, 612, 792);
        Assert.True(carvedBottom.Bottom <= bottomCap.Y - 2.0, $"Expected carvedBottom ({carvedBottom.Bottom}) <= {bottomCap.Y - 2.0}");

        var table = new BoundingBox(50, 90, 510, 300);
        var topCap = new BoundingBox(50, 82, 260, 18);
        var carvedTop = HybridOnnxPdfIngestionEngine.CarveCaptionOutOfFigureBox(table, topCap, 612, 792);
        Assert.True(carvedTop.Y >= topCap.Bottom + 2.0, $"Expected carvedTop.Y ({carvedTop.Y}) >= {topCap.Bottom + 2.0}");

        // Two words inside the left column (gap = 4 pts) SHOULD merge, while text across an 18pt column gutter MUST NOT merge
        var raw = new List<ParsedTextSpan>
        {
            new(48, 300, 45, 9, "Fig. 2.30", 10),
            new(97, 300, 150, 9, "Morphology of injury.", 10),
            new(265, 300, 220, 9, "normal prose in right column", 10) // gap = 265 - 247 = 18 pts
        };

        var merged = HybridOnnxPdfIngestionEngine.MergeTextSpansOnSameLine(raw, maxGapPts: 14.0);
        Assert.Equal(2, merged.Count);
        Assert.Contains("Fig. 2.30 Morphology of injury.", merged[0].Text);
        Assert.Equal("normal prose in right column", merged[1].Text);
    }

    private static (int Width, int Height, byte[] Rgb) DecodePngRgbForTest(byte[] pngBytes)
    {
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= pngBytes.Length)
        {
            uint len = (uint)((pngBytes[pos] << 24) | (pngBytes[pos + 1] << 16) | (pngBytes[pos + 2] << 8) | pngBytes[pos + 3]);
            string type = Encoding.ASCII.GetString(pngBytes, pos + 4, 4);
            int dataPos = pos + 8;
            if (type == "IHDR")
            {
                width = (pngBytes[dataPos] << 24) | (pngBytes[dataPos + 1] << 16) | (pngBytes[dataPos + 2] << 8) | pngBytes[dataPos + 3];
                height = (pngBytes[dataPos + 4] << 24) | (pngBytes[dataPos + 5] << 16) | (pngBytes[dataPos + 6] << 8) | pngBytes[dataPos + 7];
            }
            else if (type == "IDAT")
            {
                idat.Write(pngBytes, dataPos, (int)len);
            }
            else if (type == "IEND")
            {
                break;
            }
            pos = dataPos + (int)len + 4;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] scanlines = raw.ToArray();

        int stride = width * 3;
        var rgb = new byte[stride * height];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(scanlines, y * (stride + 1) + 1, rgb, y * stride, stride);

        return (width, height, rgb);
    }

    private static int CountDarkPixels(byte[] rgb)
    {
        int count = 0;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            if (rgb[i] < 120 && rgb[i + 1] < 120 && rgb[i + 2] < 120)
                count++;
        }
        return count;
    }

    private static int CountColoredPixels(byte[] rgb)
    {
        int count = 0;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            if (rgb[i] < 245 || rgb[i + 1] < 245 || rgb[i + 2] < 245)
                count++;
        }
        return count;
    }

    private static uint? TryReadPngPhysPixelsPerMeterX(byte[] png)
    {
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            uint len = ((uint)png[pos] << 24) | ((uint)png[pos + 1] << 16) | ((uint)png[pos + 2] << 8) | png[pos + 3];
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "pHYs" && len >= 9 && pos + 8 + 9 <= png.Length)
            {
                return ((uint)png[pos + 8] << 24) | ((uint)png[pos + 9] << 16) | ((uint)png[pos + 10] << 8) | png[pos + 11];
            }
            pos += 12 + (int)len;
        }
        return null;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "melomg_tests_" + Guid.NewGuid().ToString("N"));
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
