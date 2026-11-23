using Melomg.Application;
using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Domain.Documents;
using Melomg.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Melomg.UnitTests;

public sealed class WorkspaceAndLintTests
{
    [Fact]
    public async Task SqliteWorkspace_RoundTrip_AutoDiscovery_Refresh_And_LintFailureDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "melomg_ws_" + Guid.NewGuid().ToString("N"));
        var appDataA = Path.Combine(tempRoot, "appdata_a");
        var appDataB = Path.Combine(tempRoot, "appdata_b");
        var workDir = Path.Combine(tempRoot, "work");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(tempRoot, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            string docId;
            string firstFigId;
            string extractedPng;

            // Scope A: ingest and extract
            {
                await using var spA = BuildProvider(appDataA);
                using var scope = spA.CreateScope();

                var ingest = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
                var res = await ingest.HandleAsync(new IngestPdfCommand(pdfPath, workDir, RetainDatabase: true));
                Assert.True(res.IsSuccess);
                Assert.True(res.Value.RetainDatabase);
                docId = res.Value.Id;
                Assert.NotNull(res.Value.Figures);
                Assert.NotEmpty(res.Value.Figures!);
                firstFigId = res.Value.Figures![0].Id;
                Assert.False(File.Exists(Path.Combine(workDir, docId, "source.pdf")));

                var render = scope.ServiceProvider.GetRequiredService<RenderPagesHandler>();
                var qaOverlayDir = Path.Combine(workDir, "qa_overlay");
                var renderKeepRes = await render.HandleAsync(new RenderPagesCommand(docId, 1, false, qaOverlayDir, "png", 100, Overlay: true));
                Assert.True(renderKeepRes.IsSuccess);
                string overlayFile = renderKeepRes.Value.Artifacts[0].OutputImagePath;
                Assert.True(File.Exists(overlayFile));

                var extract = scope.ServiceProvider.GetRequiredService<ExtractFiguresHandler>();
                var extKeep = await extract.HandleAsync(new ExtractFiguresCommand(firstFigId, Format: "png", Dpi: 150, PaddingPx: 8, KeepOverlay: true));
                Assert.True(extKeep.IsSuccess);
                Assert.Equal(0, extKeep.Value.CleanedOverlayCount);
                Assert.True(File.Exists(overlayFile));

                var extRes = await extract.HandleAsync(new ExtractFiguresCommand(firstFigId, Format: "png", Dpi: 150, PaddingPx: 8));
                Assert.True(extRes.IsSuccess);
                Assert.Equal(1, extRes.Value.CleanedOverlayCount);
                Assert.False(File.Exists(overlayFile));
                Assert.False(Directory.Exists(qaOverlayDir));
                Assert.NotNull(extRes.Value.Artifacts);
                extractedPng = extRes.Value.Artifacts![0].OutputImagePath;
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(extractedPng)!, "*.meta.json"));

                var lint = scope.ServiceProvider.GetRequiredService<LintDocumentHandler>();
                var lintRes = await lint.HandleAsync(new LintDocumentQuery(docId));
                Assert.True(lintRes.IsSuccess);
                Assert.True(lintRes.Value.IsClean);
            }

            // Scope B (brand new AppData registry): portable directory discovery finds Doc_* in workDir
            {
                await using var spB = BuildProvider(appDataB);
                using var scope = spB.CreateScope();

                var list = scope.ServiceProvider.GetRequiredService<ListDocumentsHandler>();
                var listRes = await list.HandleAsync(new ListDocumentsQuery(workDir));
                Assert.True(listRes.IsSuccess);
                Assert.Single(listRes.Value);
                Assert.Equal(docId, listRes.Value[0].DocumentId);

                // Refresh with strict high threshold (0.99) drops lower-confidence items
                var ingestB = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
                var reRes = await ingestB.HandleAsync(new IngestPdfCommand(docId, Refresh: true, ConfidenceThreshold: 0.99));
                Assert.True(reRes.IsSuccess);
                Assert.True(reRes.Value.FigureCount <= reRes.Value.PreviousFigureCount);

                // Restore figures by refreshing at 0.35
                var restoreRes = await ingestB.HandleAsync(new IngestPdfCommand(docId, Refresh: true, ConfidenceThreshold: 0.35));
                Assert.True(restoreRes.IsSuccess);
                Assert.True(restoreRes.Value.FigureCount >= 3);

                // Corrupt extracted image and verify Lint detects Lint.BrokenExtractedImage
                var info = await scope.ServiceProvider.GetRequiredService<GetTargetHandler>()
                    .HandleAsync(new GetTargetQuery(docId, ViewMode.Outline));
                Assert.True(info.IsSuccess);
                string activeFig = info.Value.Figures![0].Id;
                var ext2 = await scope.ServiceProvider.GetRequiredService<ExtractFiguresHandler>()
                    .HandleAsync(new ExtractFiguresCommand(activeFig));
                Assert.True(ext2.IsSuccess);
                Assert.NotNull(ext2.Value.Artifacts);
                await File.WriteAllTextAsync(ext2.Value.Artifacts![0].OutputImagePath, "NOT_PNG");

                var lintAfter = await scope.ServiceProvider.GetRequiredService<LintDocumentHandler>()
                    .HandleAsync(new LintDocumentQuery(docId));
                Assert.True(lintAfter.IsSuccess);
                Assert.False(lintAfter.Value.IsClean);
                Assert.Contains(lintAfter.Value.Issues, i => i.Code == "Lint.BrokenExtractedImage");
            }
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task EphemeralIngest_DiscardsDbAfterExtract_WhileProjectIngest_MovesPdfAndRetainsDb()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "melomg_proj_" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(tempRoot, "appdata");
        var cwdDir = Path.Combine(tempRoot, "cwd");
        var workDir = Path.Combine(tempRoot, "work");
        var outDir = Path.Combine(tempRoot, "out_figs");
        Directory.CreateDirectory(cwdDir);
        Directory.CreateDirectory(workDir);

        try
        {
            var services = new ServiceCollection();
            services.AddApplication();
            services.AddInfrastructure(opts => opts.AppDataDirectory = appData);
            services.AddSingleton<IWorkingDirectoryProvider>(new FixedWorkingDirectoryProvider(cwdDir));
            await using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            // 1. Default non-project ingest -> extract discards Doc_* DB & removes empty Doc_* dir
            var ephPdf = Path.Combine(tempRoot, "ephemeral.pdf");
            await File.WriteAllBytesAsync(ephPdf, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            var ingest = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
            var ingRes = await ingest.HandleAsync(new IngestPdfCommand(ephPdf, workDir, Offline: true));
            Assert.True(ingRes.IsSuccess);
            Assert.False(ingRes.Value.RetainDatabase);
            Assert.True(Directory.Exists(ingRes.Value.WorkspaceDirectory));

            var extract = scope.ServiceProvider.GetRequiredService<ExtractFiguresHandler>();
            var extRes = await extract.HandleAsync(new ExtractFiguresCommand(ingRes.Value.Id, Selectors: ["all"], OutputDirectory: outDir));
            Assert.True(extRes.IsSuccess);
            Assert.False(extRes.Value.RetainedDatabase);
            Assert.NotEmpty(Directory.GetFiles(outDir, "fig_*.png"));
            Assert.False(Directory.Exists(ingRes.Value.WorkspaceDirectory));
            Assert.Empty(Directory.GetFiles(workDir, "*.db", SearchOption.AllDirectories));
            Assert.True(File.Exists(ephPdf));

            // 2. Project ingest without dir -> moves PDF into <CWD>/project_<PDF name>/ and keeps DB forever
            var projSourcePdf = Path.Combine(tempRoot, "My Anatomy Chapter.pdf");
            await File.WriteAllBytesAsync(projSourcePdf, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            var openRes = await ingest.HandleAsync(new IngestPdfCommand(projSourcePdf, ProjectMode: true, Offline: true));
            Assert.True(openRes.IsSuccess);
            Assert.False(openRes.Value.ReopenedExisting);

            string expectedProjectDir = Path.Combine(cwdDir, "project_My Anatomy Chapter");
            string expectedMovedPdf = Path.Combine(expectedProjectDir, "My Anatomy Chapter.pdf");
            Assert.Equal(expectedProjectDir, openRes.Value.WorkspaceDirectory);
            Assert.Equal(expectedMovedPdf, openRes.Value.StoredPdfPath);
            Assert.False(File.Exists(projSourcePdf));
            Assert.True(File.Exists(expectedMovedPdf));
            Assert.True(File.Exists(openRes.Value.DatabasePath));

            // Extracting without --retain-db STILL keeps project DB intact
            var projExtRes = await extract.HandleAsync(new ExtractFiguresCommand(openRes.Value.Id, Selectors: ["all"]));
            Assert.True(projExtRes.IsSuccess);
            Assert.True(projExtRes.Value.RetainedDatabase);
            Assert.True(File.Exists(openRes.Value.DatabasePath));

            // Re-opening existing project directory loads it without re-ingesting
            var reopenRes = await ingest.HandleAsync(new IngestPdfCommand(expectedProjectDir, ProjectMode: true));
            Assert.True(reopenRes.IsSuccess);
            Assert.True(reopenRes.Value.ReopenedExisting);
            Assert.Equal(openRes.Value.Id, reopenRes.Value.Id);

            // ls discovers the project workspace in CWD
            var listRes = await scope.ServiceProvider.GetRequiredService<ListDocumentsHandler>()
                .HandleAsync(new ListDocumentsQuery(cwdDir));
            Assert.True(listRes.IsSuccess);
            Assert.Contains(listRes.Value, d => d.DocumentId == openRes.Value.Id && d.WorkspaceMode == "project");

            // Deleting underlying DB causes ls to prune the missing registry entry cleanly
            File.Delete(openRes.Value.DatabasePath!);
            var prunedList = await scope.ServiceProvider.GetRequiredService<ListDocumentsHandler>()
                .HandleAsync(new ListDocumentsQuery(cwdDir));
            Assert.True(prunedList.IsSuccess);
            Assert.DoesNotContain(prunedList.Value, d => d.DocumentId == openRes.Value.Id);

            // SEC-01: QA overlay cleanup never deletes non-image spoofed _overlay.png or unrelated files
            var secPdf = Path.Combine(tempRoot, "sec.pdf");
            await File.WriteAllBytesAsync(secPdf, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());
            var secIngest = await ingest.HandleAsync(new IngestPdfCommand(secPdf, workDir, RetainDatabase: true, Offline: true));
            Assert.True(secIngest.IsSuccess);
            string spoofOverlay = Path.Combine(workDir, "fake_overlay.png");
            await File.WriteAllTextAsync(spoofOverlay, "NOT A REAL PNG");
            string validOverlay = (await scope.ServiceProvider.GetRequiredService<RenderPagesHandler>()
                .HandleAsync(new RenderPagesCommand(secIngest.Value.Id, 1, false, workDir, "png", 96, Overlay: true)))
                .Value.Artifacts[0].OutputImagePath;
            Assert.True(File.Exists(validOverlay));

            // Manually plant spoofed overlay path in .qa_overlays to verify magic-byte gate on cleanup
            string manifestPath = Path.Combine(secIngest.Value.WorkspaceDirectory, ".qa_overlays");
            await File.AppendAllTextAsync(manifestPath, Environment.NewLine + spoofOverlay + Environment.NewLine);
            var secExtract = await extract.HandleAsync(new ExtractFiguresCommand(secIngest.Value.Id, Selectors: ["f1"], OutputDirectory: outDir));
            Assert.True(secExtract.IsSuccess);
            Assert.False(File.Exists(validOverlay));
            Assert.True(File.Exists(spoofOverlay));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task LegacyDatabaseWithTableOrFormulaRows_SkipsNonVisualArtifactsOnLoad()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "melomg_legacy_db_" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(tempRoot, "appdata");
        var workDir = Path.Combine(tempRoot, "work");
        Directory.CreateDirectory(workDir);

        try
        {
            var pdfPath = Path.Combine(tempRoot, "legacy.pdf");
            await File.WriteAllBytesAsync(pdfPath, OnnxAndPdfIngestionTests.BuildSampleMultiPagePdfBytes());

            await using var sp = BuildProvider(appData);
            using var scope = sp.CreateScope();

            var ingest = scope.ServiceProvider.GetRequiredService<IngestPdfHandler>();
            var res = await ingest.HandleAsync(new IngestPdfCommand(pdfPath, workDir, RetainDatabase: true, Offline: true));
            Assert.True(res.IsSuccess);
            string dbPath = res.Value.DatabasePath!;
            int originalCount = res.Value.FigureCount;

            // Inject legacy "Table" and "Formula" rows directly into SQLite
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO DocumentFigures (Id, SortIndex, PageNumber, PageFigureIndex, Category, BboxX, BboxY, BboxW, BboxH, Confidence, Source, Label, Caption)
                    VALUES ('Fig_LegacyTable', 98, 1, 98, 'table', 20, 20, 100, 80, 0.90, 'onnx', 'Table 1', 'Table 1. Legacy table'),
                           ('Fig_LegacyFormula', 99, 1, 99, 'formula', 20, 120, 100, 40, 0.90, 'onnx', 'Eq 1', 'Eq 1. Legacy formula');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var repo = scope.ServiceProvider.GetRequiredService<IDocumentWorkspaceRepository>();
            var reloaded = await repo.FindDocumentAsync(res.Value.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(originalCount, reloaded!.Document.Figures.Count);
            Assert.DoesNotContain(reloaded.Document.Figures, f => f.Id is "Fig_LegacyTable" or "Fig_LegacyFormula");
            Assert.All(reloaded.Document.Figures, f =>
                Assert.True(f.Category is FigureCategory.Figure or FigureCategory.Diagram or FigureCategory.Chart));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempRoot);
        }
    }

    private sealed class FixedWorkingDirectoryProvider(string currentDir) : IWorkingDirectoryProvider
    {
        public string GetCurrentDirectory() => currentDir;
        public string NormalizeDirectoryPath(string? explicitPath) =>
            string.IsNullOrWhiteSpace(explicitPath) ? currentDir : Path.GetFullPath(explicitPath.Trim());
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
    }

    private static ServiceProvider BuildProvider(string appDataDir)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts => opts.AppDataDirectory = appDataDir);
        return services.BuildServiceProvider();
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
