using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Extraction;
using Melomg.Domain.Models;
using Xunit;

namespace Melomg.UnitTests;

public sealed class DomainAndOrderingTests
{
    private static PdfDocument CreateSampleDocument(int pageCount = 2)
    {
        var pages = Enumerable.Range(1, pageCount)
            .Select(p => PdfPageInfo.Create(p, 612, 792).Value)
            .ToList();

        var docRes = PdfDocument.Create(
            "Doc_20261120_010000_001",
            "Sample Paper",
            "C:\\work\\sample.pdf",
            "C:\\work\\Doc_20261120_010000_001\\source.pdf",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "C:\\work\\Doc_20261120_010000_001",
            OnnxModelProfile.BuiltInModelName,
            0.35,
            DateTimeOffset.UtcNow,
            pages);

        Assert.True(docRes.IsSuccess);
        return docRes.Value;
    }

    [Fact]
    public void DocumentFigures_EnforceOneBasedIndexing_Exchange_And_Shift()
    {
        var doc = CreateSampleDocument();
        var box = BoundingBox.Create(50, 50, 200, 120).Value;

        Assert.True(doc.AddFigure("Fig_1", 1, FigureCategory.Figure, box, 0.90, "Cap 1", "Fig 1", DetectionSource.Hybrid).IsSuccess);
        Assert.True(doc.AddFigure("Fig_2", 1, FigureCategory.Chart, box, 0.85, "Cap 2", "Chart 2", DetectionSource.OnnxModel).IsSuccess);
        Assert.True(doc.AddFigure("Fig_3", 2, FigureCategory.Diagram, box, 0.80, "Cap 3", "Diag 3", DetectionSource.Manual).IsSuccess);

        // Insert at index 2 -> shifts Fig_2 and Fig_3 down (n+1)
        var ins = doc.AddFigure("Fig_4", 2, FigureCategory.Table, box, 0.95, "Table 1", "Table 1", DetectionSource.Manual, targetIndex: 2);
        Assert.True(ins.IsSuccess);
        Assert.Equal(["Fig_1", "Fig_4", "Fig_2", "Fig_3"], doc.Figures.Select(f => f.Id).ToArray());
        Assert.Equal([1, 2, 3, 4], doc.Figures.Select(f => f.Index).ToArray());

        // Exchange index 2 and index 4
        Assert.True(doc.ReorderFigure("Fig_4", 4, ReorderMode.Exchange).IsSuccess);
        Assert.Equal(["Fig_1", "Fig_3", "Fig_2", "Fig_4"], doc.Figures.Select(f => f.Id).ToArray());

        // Forward shift: index 2 -> index 4
        Assert.True(doc.ReorderFigure("Fig_3", 4, ReorderMode.Shift).IsSuccess);
        Assert.Equal(["Fig_1", "Fig_2", "Fig_4", "Fig_3"], doc.Figures.Select(f => f.Id).ToArray());

        // Backward shift: index 4 -> index 1
        Assert.True(doc.ReorderFigure("Fig_3", 1, ReorderMode.Shift).IsSuccess);
        Assert.Equal(["Fig_3", "Fig_1", "Fig_2", "Fig_4"], doc.Figures.Select(f => f.Id).ToArray());

        // Delete middle item (index 2 -> Fig_1) compacts indices to 1..3
        Assert.True(doc.DeleteFigure("Fig_1").IsSuccess);
        Assert.Equal(["Fig_3", "Fig_2", "Fig_4"], doc.Figures.Select(f => f.Id).ToArray());
        Assert.Equal([1, 2, 3], doc.Figures.Select(f => f.Index).ToArray());
    }

    [Fact]
    public void DocumentFigures_OutOfBoundsIndices_ReturnOutOfIndexBoundsError()
    {
        var doc = CreateSampleDocument();
        var box = BoundingBox.Create(50, 50, 100, 100).Value;
        Assert.True(doc.AddFigure("Fig_1", 1, FigureCategory.Figure, box, 0.9, null, null, DetectionSource.Manual).IsSuccess);

        var badInsertLow = doc.AddFigure("Fig_2", 1, FigureCategory.Figure, box, 0.9, null, null, DetectionSource.Manual, targetIndex: 0);
        Assert.False(badInsertLow.IsSuccess);
        Assert.Equal("Index.OutOfIndexBounds", badInsertLow.Error!.Code);

        var badInsertHigh = doc.AddFigure("Fig_2", 1, FigureCategory.Figure, box, 0.9, null, null, DetectionSource.Manual, targetIndex: 3);
        Assert.False(badInsertHigh.IsSuccess);
        Assert.Equal("Index.OutOfIndexBounds", badInsertHigh.Error!.Code);

        var badReorder = doc.ReorderFigure("Fig_1", 2, ReorderMode.Shift);
        Assert.False(badReorder.IsSuccess);
        Assert.Equal("Index.OutOfIndexBounds", badReorder.Error!.Code);
    }

    [Fact]
    public void BoundingBox_ValidatesPageBounds_ComputesIoU_And_ClampsPixelRect()
    {
        var page = PdfPageInfo.Create(1, 612, 792).Value;
        var outBox = BoundingBox.Create(500, 100, 200, 100).Value;
        var boundsErr = outBox.ValidateWithinPage(page.WidthPts, page.HeightPts);
        Assert.False(boundsErr.IsSuccess);
        Assert.Equal("Figure.BoundingBoxOutOfPageBounds", boundsErr.Error!.Code);

        var a = BoundingBox.Create(100, 100, 100, 100).Value;
        var b = BoundingBox.Create(150, 100, 100, 100).Value;
        double iou = a.ComputeIoU(b); // intersection=5000, union=15000 => 0.3333
        Assert.InRange(iou, 0.33, 0.34);

        var edgeBox = BoundingBox.Create(0, 0, 72, 72).Value;
        var px = edgeBox.ToPixelRect(dpi: 144, paddingPx: 10, page.WidthPts, page.HeightPts);
        Assert.Equal(0, px.PixelX);
        Assert.Equal(0, px.PixelY);
        Assert.Equal(154, px.PixelWidth); // 144 + 10 right padding
    }

    [Fact]
    public void ModelProfile_And_ExtractionOptions_EnforceValidRanges()
    {
        var profile = OnnxModelProfile.CreateDefault("model.onnx");
        Assert.False(profile.SetThresholds(0.01).IsSuccess);
        Assert.False(profile.SetThresholds(0.999).IsSuccess);
        Assert.False(profile.SetThresholds(0.50, 0.05).IsSuccess);
        Assert.True(profile.SetThresholds(0.45, 0.50).IsSuccess);

        Assert.False(ExtractionOptions.Create("bmp", 150, 8).IsSuccess);
        Assert.False(ExtractionOptions.Create("png", 30, 8).IsSuccess);
        Assert.False(ExtractionOptions.Create("jpg", 150, 200).IsSuccess);

        var validJpg = ExtractionOptions.Create("jpeg", 300, 16);
        Assert.True(validJpg.IsSuccess);
        Assert.Equal("jpg", validJpg.Value.Format);

        Assert.False(ExtractionOptions.Create("png", 150, 8, obfuscateStyle: "unknown").IsSuccess);
        Assert.False(ExtractionOptions.Create("png", 150, 8, obfuscateColorHex: "NOTHEX").IsSuccess);
        Assert.True(ExtractionOptions.Create("png", 150, 8, obfuscateStyle: "whiteout", obfuscateColorHex: "abcdef").IsSuccess);
        Assert.False(RenderPageOptions.Create("webp", 150, true).IsSuccess);
        Assert.True(RenderPageOptions.Create("png", 200, true).IsSuccess);

        // --quality numeric + presets + DPI override rules
        var badQZero = ExtractionOptions.Create("jpg", null, 8, quality: "0");
        Assert.False(badQZero.IsSuccess);
        Assert.Equal("Figure.InvalidQuality", badQZero.Error!.Code);

        var badQOver = ExtractionOptions.Create("jpg", null, 8, quality: "101");
        Assert.False(badQOver.IsSuccess);
        Assert.Equal("Figure.InvalidQuality", badQOver.Error!.Code);

        Assert.Equal("Figure.InvalidQuality", ExtractionOptions.Create("png", null, 8, quality: "bogus").Error!.Code);
        Assert.Equal("Figure.InvalidQuality", RenderPageOptions.Create("png", null, false, "invalid").Error!.Code);

        foreach (var (quality, dpiOverride, expectedQ, expectedDpi) in new (string? Q, int? Dpi, int ExpQ, int ExpDpi)[]
        {
            (null, null, 90, 150),
            ("85", null, 85, 150),
            ("low", null, 55, 96),
            ("medium", null, 78, 150),
            ("high", null, 92, 240),
            ("max", null, 100, 300),
            ("ultra", null, 100, 300),
            ("high", 180, 92, 180)
        })
        {
            var opts = ExtractionOptions.Create("jpg", dpiOverride, 8, quality: quality).Value;
            Assert.Equal(expectedQ, opts.Quality);
            Assert.Equal(expectedDpi, opts.Dpi);
        }

        var renderMax = RenderPageOptions.Create("jpg", null, true, "max").Value;
        Assert.Equal((100, 300), (renderMax.Quality, renderMax.Dpi));
        var renderOverride = RenderPageOptions.Create("png", 200, false, "low").Value;
        Assert.Equal((55, 200), (renderOverride.Quality, renderOverride.Dpi));
    }

    [Fact]
    public void DocumentFigure_LabelMasks_And_CaptionEffectiveCropBox_WorkCleanly()
    {
        var doc = CreateSampleDocument();
        var page = doc.GetPage(1)!;
        var artBox = BoundingBox.Create(60, 100, 300, 160).Value;
        var capBox = BoundingBox.Create(60, 268, 300, 24).Value;

        var figRes = doc.AddFigure("Fig_Mask_1", 1, FigureCategory.Diagram, artBox, 0.92, "Fig. 1: Cascade", "Fig. 1", DetectionSource.Hybrid, captionBoundingBox: capBox);
        Assert.True(figRes.IsSuccess);
        var fig = figRes.Value;

        Assert.Equal(artBox, fig.GetEffectiveCropBox(includeCaption: false, page));
        var withCap = fig.GetEffectiveCropBox(includeCaption: true, page);
        Assert.Equal(192, withCap.Height, 1);

        var badMask = fig.AddLabelMask(BoundingBox.Create(580, 10, 80, 20).Value, page, "OutOfPage");
        Assert.False(badMask.IsSuccess);
        Assert.Equal("Figure.BoundingBoxOutOfPageBounds", badMask.Error!.Code);

        var m1 = fig.AddLabelMask(BoundingBox.Create(80, 120, 60, 16).Value, page, "Alpha", ObfuscationStyle.Solid, "#112233");
        var m2 = fig.AddLabelMask(BoundingBox.Create(180, 120, 60, 16).Value, page, "Beta", ObfuscationStyle.Blur);
        Assert.True(m1.IsSuccess && m2.IsSuccess);
        Assert.Equal(2, fig.LabelMasks.Count);

        Assert.True(fig.RemoveLabelMask(1).IsSuccess);
        Assert.Single(fig.LabelMasks);
        Assert.Equal(1, fig.LabelMasks[0].Index);
        Assert.Equal("Beta", fig.LabelMasks[0].LabelText);

        Assert.False(fig.RemoveLabelMask(5).IsSuccess);
        Assert.Equal(1, fig.ClearLabelMasks());
        Assert.Empty(fig.LabelMasks);
    }

    [Fact]
    public void ExtractionOptions_SupportsLiteralsGlobsAndRegex_GuardsReDoS_And_AcceptsCrosshatchAlias()
    {
        Assert.True(FigureLabelMask.TryParseStyle("crosshatch", out var style));
        Assert.Equal(ObfuscationStyle.Solid, style);

        var patterns = ExtractionOptions.ParsePatterns(@"Neutrophil,Endothelial*,^(Fig|Table)\.\s*\d{1,3}$");
        Assert.Equal(3, patterns.Count);
        Assert.True(ExtractionOptions.MatchesAnyPattern("Mature Neutrophil Cell", patterns));
        Assert.True(ExtractionOptions.MatchesAnyPattern("Endothelial Junction", patterns));
        Assert.True(ExtractionOptions.MatchesAnyPattern("Fig. 12", patterns));
        Assert.False(ExtractionOptions.MatchesAnyPattern("Unrelated Label", patterns));

        // ReDoS adversarial pattern must return cleanly inside timeout without hanging
        var redosPatterns = ExtractionOptions.ParsePatterns("^(a+)+$");
        Assert.False(ExtractionOptions.MatchesAnyPattern(new string('a', 32) + "!", redosPatterns));
    }
}
