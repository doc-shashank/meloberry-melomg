using System.Globalization;
using Melomg.Application.Abstractions;
using Melomg.Domain.Documents;
using Microsoft.Data.Sqlite;

namespace Melomg.Infrastructure.Persistence;

public sealed class SqliteDocumentWorkspaceRepository(
    SqliteModelSettingsRepository registry,
    IWorkingDirectoryProvider workingDirectoryProvider) : IDocumentWorkspaceRepository
{
    public async Task CreateDocumentAsync(PdfDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(document.WorkingDirectory);
        Directory.CreateDirectory(Path.Combine(document.WorkingDirectory, "figures"));
        await SaveDocumentAsync(document, cancellationToken);
    }

    public async Task SaveDocumentAsync(PdfDocument document, CancellationToken cancellationToken = default)
    {
        var dbPath = Path.Combine(document.WorkingDirectory, $"{document.Id}.db");
        Directory.CreateDirectory(document.WorkingDirectory);
        Directory.CreateDirectory(Path.Combine(document.WorkingDirectory, "figures"));

        await using (var conn = OpenWorkspaceConnection(dbPath))
        {
            await conn.OpenAsync(cancellationToken);
            await EnsureWorkspaceSchemaAsync(conn, cancellationToken);

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
            await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                conn, tx,
                "DELETE FROM DocumentFigureMasks; DELETE FROM DocumentFigures; DELETE FROM DocumentPages; DELETE FROM DocumentMeta;",
                cancellationToken);

            await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                conn, tx,
                """
                INSERT INTO DocumentMeta (
                    Id, Title, SourcePdfPath, StoredPdfPath, Sha256Hash, ModelName, ConfidenceThreshold,
                    IngestedAtUtc, ActiveFigureId, WorkspaceMode, RetainDatabase
                ) VALUES ($id, $title, $src, $stored, $sha, $model, $conf, $ts, $active, $mode, $retain);
                """,
                cancellationToken,
                ("$id", document.Id), ("$title", document.Title), ("$src", document.SourcePdfPath),
                ("$stored", document.StoredPdfPath), ("$sha", document.Sha256Hash), ("$model", document.ModelName),
                ("$conf", document.ConfidenceThreshold), ("$ts", document.IngestedAtUtc.ToString("O")),
                ("$active", document.ActiveFigureId),
                ("$mode", document.Mode == WorkspaceMode.Project ? "project" : "standard"),
                ("$retain", document.RetainDatabase ? 1 : 0));

            foreach (var p in document.Pages)
            {
                await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                    conn, tx,
                    "INSERT INTO DocumentPages (PageNumber, WidthPts, HeightPts, RotationDegrees) VALUES ($num, $w, $h, $rot);",
                    cancellationToken,
                    ("$num", p.PageNumber), ("$w", p.WidthPts), ("$h", p.HeightPts), ("$rot", p.RotationDegrees));
            }

            foreach (var f in document.Figures)
            {
                await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                    conn, tx,
                    """
                    INSERT INTO DocumentFigures (
                        Id, SortIndex, PageNumber, PageFigureIndex, Category, BboxX, BboxY, BboxW, BboxH,
                        Confidence, Caption, Label, Source, LastExtractedPath,
                        CaptionBboxX, CaptionBboxY, CaptionBboxW, CaptionBboxH
                    ) VALUES ($id, $idx, $page, $pidx, $cat, $x, $y, $w, $h, $conf, $cap, $lbl, $src, $ext, $cx, $cy, $cw, $ch);
                    """,
                    cancellationToken,
                    ("$id", f.Id), ("$idx", f.Index), ("$page", f.PageNumber), ("$pidx", f.PageFigureIndex),
                    ("$cat", f.Category.ToString().ToLowerInvariant()), ("$x", f.BoundingBox.X), ("$y", f.BoundingBox.Y),
                    ("$w", f.BoundingBox.Width), ("$h", f.BoundingBox.Height), ("$conf", f.Confidence),
                    ("$cap", f.Caption), ("$lbl", f.Label), ("$src", DocumentFigure.FormatSourceTag(f.Source)),
                    ("$ext", f.LastExtractedPath), ("$cx", f.CaptionBoundingBox?.X), ("$cy", f.CaptionBoundingBox?.Y),
                    ("$cw", f.CaptionBoundingBox?.Width), ("$ch", f.CaptionBoundingBox?.Height));

                foreach (var m in f.LabelMasks)
                {
                    await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                        conn, tx,
                        """
                        INSERT INTO DocumentFigureMasks (
                            FigureId, MaskIndex, BboxX, BboxY, BboxW, BboxH, LabelText, Style, ColorHex
                        ) VALUES ($figId, $idx, $x, $y, $w, $h, $txt, $style, $color);
                        """,
                        cancellationToken,
                        ("$figId", f.Id), ("$idx", m.Index), ("$x", m.BoundingBox.X), ("$y", m.BoundingBox.Y),
                        ("$w", m.BoundingBox.Width), ("$h", m.BoundingBox.Height), ("$txt", m.LabelText),
                        ("$style", FigureLabelMask.FormatStyle(m.Style)), ("$color", m.ColorHex));
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        await RegisterDocumentAsync(document, dbPath, cancellationToken);
    }

    public async Task<DocumentLookupContext?> FindDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveDocumentEntryAsync(documentId, cancellationToken);
        if (entry is null) return null;

        var doc = await LoadDocumentAsync(entry.DbPath, entry.StorageDirectory, cancellationToken);
        return doc is null ? null : new DocumentLookupContext(doc, entry.StorageDirectory, entry.DbPath);
    }

    public async Task<FigureOwnerContext?> FindFigureOwnerAsync(string figureId, CancellationToken cancellationToken = default)
    {
        await registry.EnsureInitializedAsync(cancellationToken);

        string? documentId = await QueryFigureDocumentIdAsync(figureId, cancellationToken);
        if (documentId is null)
        {
            await ScanDirectoryForDocumentsAsync(workingDirectoryProvider.GetCurrentDirectory(), cancellationToken);
            documentId = await QueryFigureDocumentIdAsync(figureId, cancellationToken);
        }

        if (documentId is null) return null;

        var docLookup = await FindDocumentAsync(documentId, cancellationToken);
        var figure = docLookup?.Document.FindFigure(figureId);
        return docLookup is null || figure is null
            ? null
            : new FigureOwnerContext(docLookup.Document, figure, docLookup.StorageDirectory, docLookup.DatabasePath);
    }

    public async Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(string? directoryFilter = null, CancellationToken cancellationToken = default)
    {
        var scanDir = string.IsNullOrWhiteSpace(directoryFilter)
            ? workingDirectoryProvider.GetCurrentDirectory()
            : directoryFilter;

        await ScanDirectoryForDocumentsAsync(scanDir, cancellationToken);
        await registry.EnsureInitializedAsync(cancellationToken);

        var items = new List<DocumentListItem>();
        var staleDocIds = new List<string>();
        await using (var conn = registry.OpenSettingsConnection())
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT DocumentId, Title, PageCount, FigureCount, ModelName, ConfidenceThreshold, DbPath, StorageDirectory, IngestedAtUtc, WorkspaceMode
                FROM DocumentRegistry
                ORDER BY IngestedAtUtc DESC;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string docId = reader.GetString(0);
                string dbPath = reader.GetString(6);
                string storageDir = reader.GetString(7);
                if (!File.Exists(dbPath))
                {
                    staleDocIds.Add(docId);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(directoryFilter) && !IsInsideOrEqualDirectory(storageDir, directoryFilter))
                    continue;

                var ingestedAt = DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                    ? dt
                    : DateTimeOffset.MinValue;
                string modeStr = reader.IsDBNull(9) ? "standard" : reader.GetString(9);

                items.Add(new DocumentListItem(
                    docId, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetDouble(5), storageDir, ingestedAt, modeStr));
            }
        }

        foreach (var staleId in staleDocIds)
            await DeregisterDocumentAsync(staleId, cancellationToken);

        return items;
    }

    public async Task<DocumentLookupContext?> FindDocumentInDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return null;

        var fullDir = Path.GetFullPath(directoryPath);
        foreach (var dbPath in Directory.EnumerateFiles(fullDir, "Doc_*.db").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var doc = await LoadDocumentAsync(dbPath, fullDir, cancellationToken);
            if (doc is not null)
            {
                await RegisterDocumentAsync(doc, dbPath, cancellationToken);
                return new DocumentLookupContext(doc, fullDir, dbPath);
            }
        }

        foreach (var subDir in Directory.EnumerateDirectories(fullDir, "Doc_*"))
        {
            var candidateDb = Path.Combine(subDir, $"{Path.GetFileName(subDir)}.db");
            if (!File.Exists(candidateDb)) continue;
            var doc = await LoadDocumentAsync(candidateDb, subDir, cancellationToken);
            if (doc is not null)
            {
                await RegisterDocumentAsync(doc, candidateDb, cancellationToken);
                return new DocumentLookupContext(doc, subDir, candidateDb);
            }
        }

        return null;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var lookup = await FindDocumentAsync(documentId, cancellationToken);
        if (lookup is null) return;

        await DeregisterDocumentAsync(documentId, cancellationToken);
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(lookup.StorageDirectory)) return;

        string leafName = Path.GetFileName(lookup.StorageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool isDedicatedWorkspaceDir =
            string.Equals(leafName, documentId, StringComparison.OrdinalIgnoreCase) ||
            leafName.StartsWith("Doc_", StringComparison.OrdinalIgnoreCase) ||
            leafName.StartsWith("project_", StringComparison.OrdinalIgnoreCase);

        if (isDedicatedWorkspaceDir)
        {
            Directory.Delete(lookup.StorageDirectory, recursive: true);
        }
        else
        {
            TryCleanupWorkspaceArtifacts(lookup.StorageDirectory, lookup.DatabasePath);
        }
    }

    public async Task DiscardWorkspaceDatabaseAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var lookup = await FindDocumentAsync(documentId, cancellationToken);
        await DeregisterDocumentAsync(documentId, cancellationToken);
        if (lookup is null) return;

        SqliteConnection.ClearAllPools();
        TryCleanupWorkspaceArtifacts(lookup.StorageDirectory, lookup.DatabasePath);
    }

    private static void TryCleanupWorkspaceArtifacts(string storageDirectory, string databasePath)
    {
        try
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
            var qaManifest = Path.Combine(storageDirectory, ".qa_overlays");
            if (File.Exists(qaManifest)) File.Delete(qaManifest);

            var figuresDir = Path.Combine(storageDirectory, "figures");
            if (Directory.Exists(figuresDir) && !Directory.EnumerateFileSystemEntries(figuresDir).Any())
                Directory.Delete(figuresDir, recursive: false);

            if (Directory.Exists(storageDirectory) && !Directory.EnumerateFileSystemEntries(storageDirectory).Any())
                Directory.Delete(storageDirectory, recursive: false);
        }
        catch { }
    }

    private async Task DeregisterDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        await registry.EnsureInitializedAsync(cancellationToken);
        await using var conn = registry.OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
            conn, tx, "DELETE FROM FigureEntityIndex WHERE DocumentId = $docId;", cancellationToken, ("$docId", documentId));
        await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
            conn, tx, "DELETE FROM DocumentRegistry WHERE DocumentId = $docId;", cancellationToken, ("$docId", documentId));

        await tx.CommitAsync(cancellationToken);
    }

    private static bool IsInsideOrEqualDirectory(string candidateDir, string filterDir)
    {
        var normFilter = Path.GetFullPath(filterDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var filterPrefix = normFilter + Path.DirectorySeparatorChar;
        var normCandidate = Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = Path.GetDirectoryName(normCandidate)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        return string.Equals(normCandidate, normFilter, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parentDir, normFilter, StringComparison.OrdinalIgnoreCase) ||
               normCandidate.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RegistryEntry(string DocumentId, string DbPath, string StorageDirectory);

    private async Task<string?> QueryFigureDocumentIdAsync(string figureId, CancellationToken cancellationToken)
    {
        await using var conn = registry.OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DocumentId FROM FigureEntityIndex WHERE FigureId = $figId;";
        cmd.Parameters.AddWithValue("$figId", figureId);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<RegistryEntry?> ResolveDocumentEntryAsync(string documentId, CancellationToken cancellationToken)
    {
        await registry.EnsureInitializedAsync(cancellationToken);

        var found = await QueryRegistryEntryAsync(documentId, cancellationToken);
        if (found is not null && File.Exists(found.DbPath)) return found;

        await ScanDirectoryForDocumentsAsync(workingDirectoryProvider.GetCurrentDirectory(), cancellationToken);
        found = await QueryRegistryEntryAsync(documentId, cancellationToken);
        return found is not null && File.Exists(found.DbPath) ? found : null;
    }

    private async Task<RegistryEntry?> QueryRegistryEntryAsync(string documentId, CancellationToken cancellationToken)
    {
        await using var conn = registry.OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DocumentId, DbPath, StorageDirectory FROM DocumentRegistry WHERE DocumentId = $docId;";
        cmd.Parameters.AddWithValue("$docId", documentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RegistryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task ScanDirectoryForDocumentsAsync(string rootDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return;

        await registry.EnsureInitializedAsync(cancellationToken);
        var registeredDbPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = registry.OpenSettingsConnection())
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DbPath FROM DocumentRegistry;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                registeredDbPaths.Add(Path.GetFullPath(reader.GetString(0)));
        }

        async Task TryRegisterDirAsync(string dir)
        {
            foreach (var candidateDb in Directory.EnumerateFiles(dir, "Doc_*.db"))
            {
                if (registeredDbPaths.Contains(Path.GetFullPath(candidateDb))) continue;
                var doc = await LoadDocumentAsync(candidateDb, dir, cancellationToken);
                if (doc is not null) await RegisterDocumentAsync(doc, candidateDb, cancellationToken);
            }
        }

        await TryRegisterDirAsync(rootDir);
        foreach (var subDir in Directory.EnumerateDirectories(rootDir))
            await TryRegisterDirAsync(subDir);
    }

    private async Task RegisterDocumentAsync(PdfDocument document, string dbPath, CancellationToken cancellationToken)
    {
        await registry.EnsureInitializedAsync(cancellationToken);
        await using var conn = registry.OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
            conn, tx,
            """
            INSERT INTO DocumentRegistry (
                DocumentId, Title, PageCount, FigureCount, ModelName, ConfidenceThreshold, DbPath, StorageDirectory, IngestedAtUtc, WorkspaceMode
            )
            VALUES ($id, $title, $pages, $figs, $model, $conf, $db, $dir, $ts, $mode)
            ON CONFLICT(DocumentId) DO UPDATE SET
                Title = excluded.Title, PageCount = excluded.PageCount, FigureCount = excluded.FigureCount,
                ModelName = excluded.ModelName, ConfidenceThreshold = excluded.ConfidenceThreshold,
                DbPath = excluded.DbPath, StorageDirectory = excluded.StorageDirectory,
                IngestedAtUtc = excluded.IngestedAtUtc, WorkspaceMode = excluded.WorkspaceMode;
            """,
            cancellationToken,
            ("$id", document.Id), ("$title", document.Title), ("$pages", document.PageCount),
            ("$figs", document.Figures.Count), ("$model", document.ModelName),
            ("$conf", document.ConfidenceThreshold), ("$db", dbPath), ("$dir", document.WorkingDirectory),
            ("$ts", document.IngestedAtUtc.ToString("O")),
            ("$mode", document.Mode == WorkspaceMode.Project ? "project" : "standard"));

        await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
            conn, tx, "DELETE FROM FigureEntityIndex WHERE DocumentId = $docId;", cancellationToken, ("$docId", document.Id));

        foreach (var fig in document.Figures)
        {
            await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
                conn, tx, "INSERT INTO FigureEntityIndex (FigureId, DocumentId) VALUES ($figId, $docId);",
                cancellationToken, ("$figId", fig.Id), ("$docId", document.Id));
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task<PdfDocument?> LoadDocumentAsync(string dbPath, string storageDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(dbPath)) return null;

        try
        {
            await using var conn = OpenWorkspaceConnection(dbPath);
            await conn.OpenAsync(cancellationToken);

            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'DocumentMeta';";
                if (await checkCmd.ExecuteScalarAsync(cancellationToken) is null or DBNull) return null;
            }

            return await LoadDocumentFromOpenConnectionAsync(conn, storageDirectory, cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<PdfDocument?> LoadDocumentFromOpenConnectionAsync(
        SqliteConnection conn, string storageDirectory, CancellationToken cancellationToken)
    {
        string id, title, sourcePdf, storedPdf, sha256, modelName;
        double conf;
        DateTimeOffset ingestedAt;
        string? activeFigId;
        WorkspaceMode workspaceMode;
        bool retainDatabase;

        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText =
                "SELECT Id, Title, SourcePdfPath, StoredPdfPath, Sha256Hash, ModelName, ConfidenceThreshold, IngestedAtUtc, ActiveFigureId, WorkspaceMode, RetainDatabase FROM DocumentMeta LIMIT 1;";
            await using var reader = await metaCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            id = reader.GetString(0);
            title = reader.GetString(1);
            sourcePdf = reader.GetString(2);
            storedPdf = reader.GetString(3);
            if (!File.Exists(storedPdf))
            {
                string localNamedPdf = Path.Combine(storageDirectory, Path.GetFileName(storedPdf));
                string localPdf = Path.Combine(storageDirectory, "source.pdf");
                storedPdf = File.Exists(localNamedPdf) ? localNamedPdf : (File.Exists(localPdf) ? localPdf : storedPdf);
            }
            sha256 = reader.GetString(4);
            modelName = reader.GetString(5);
            conf = reader.GetDouble(6);
            ingestedAt = DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : DateTimeOffset.UtcNow;
            activeFigId = reader.IsDBNull(8) ? null : reader.GetString(8);
            workspaceMode = string.Equals(reader.IsDBNull(9) ? "standard" : reader.GetString(9), "project", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceMode.Project
                : WorkspaceMode.Standard;
            retainDatabase = !reader.IsDBNull(10) && reader.GetInt32(10) != 0;
        }

        var pages = new List<PdfPageInfo>();
        await using (var pagesCmd = conn.CreateCommand())
        {
            pagesCmd.CommandText = "SELECT PageNumber, WidthPts, HeightPts, RotationDegrees FROM DocumentPages ORDER BY PageNumber ASC;";
            await using var reader = await pagesCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pages.Add(new PdfPageInfo(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3)));
        }

        var masksByFigure = new Dictionary<string, List<FigureLabelMask>>(StringComparer.Ordinal);
        await using (var masksCmd = conn.CreateCommand())
        {
            masksCmd.CommandText = "SELECT FigureId, MaskIndex, BboxX, BboxY, BboxW, BboxH, LabelText, Style, ColorHex FROM DocumentFigureMasks ORDER BY FigureId ASC, MaskIndex ASC;";
            await using var reader = await masksCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string figId = reader.GetString(0);
                if (!masksByFigure.TryGetValue(figId, out var list))
                    masksByFigure[figId] = list = [];

                FigureLabelMask.TryParseStyle(reader.GetString(7), out var style);
                list.Add(new FigureLabelMask(
                    reader.GetInt32(1),
                    new BoundingBox(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    style,
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        var figures = new List<DocumentFigure>();
        await using (var figsCmd = conn.CreateCommand())
        {
            figsCmd.CommandText =
                """
                SELECT Id, SortIndex, PageNumber, PageFigureIndex, Category, BboxX, BboxY, BboxW, BboxH,
                       Confidence, Caption, Label, Source, LastExtractedPath, CaptionBboxX, CaptionBboxY, CaptionBboxW, CaptionBboxH
                FROM DocumentFigures ORDER BY SortIndex ASC;
                """;
            await using var reader = await figsCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string figId = reader.GetString(0);
                DocumentFigure.TryParseCategory(reader.GetString(4), out var category);
                DocumentFigure.TryParseDetectionSource(reader.GetString(12), out var source);
                var bbox = new BoundingBox(reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8));
                BoundingBox? captionBox = !reader.IsDBNull(14) && !reader.IsDBNull(15) && !reader.IsDBNull(16) && !reader.IsDBNull(17)
                    ? new BoundingBox(reader.GetDouble(14), reader.GetDouble(15), reader.GetDouble(16), reader.GetDouble(17))
                    : null;

                masksByFigure.TryGetValue(figId, out var figMasks);
                figures.Add(DocumentFigure.Rehydrate(
                    figId, id, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), category, bbox,
                    reader.GetDouble(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
                    source, reader.IsDBNull(13) ? null : reader.GetString(13), captionBox, figMasks));
            }
        }

        return PdfDocument.Rehydrate(
            id, title, sourcePdf, storedPdf, sha256, storageDirectory, modelName, conf,
            ingestedAt, activeFigId, pages, figures, workspaceMode, retainDatabase);
    }

    private static SqliteConnection OpenWorkspaceConnection(string dbPath) => new($"Data Source={dbPath};Pooling=False");

    private static async Task EnsureWorkspaceSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await SqliteModelSettingsRepository.ExecuteNonQueryAsync(
            conn, null,
            """
            CREATE TABLE IF NOT EXISTS DocumentMeta (
                Id TEXT PRIMARY KEY, Title TEXT NOT NULL, SourcePdfPath TEXT NOT NULL, StoredPdfPath TEXT NOT NULL,
                Sha256Hash TEXT NOT NULL, ModelName TEXT NOT NULL, ConfidenceThreshold REAL NOT NULL,
                IngestedAtUtc TEXT NOT NULL, ActiveFigureId TEXT NULL,
                WorkspaceMode TEXT NOT NULL DEFAULT 'standard', RetainDatabase INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS DocumentPages (
                PageNumber INTEGER PRIMARY KEY, WidthPts REAL NOT NULL, HeightPts REAL NOT NULL, RotationDegrees INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS DocumentFigures (
                Id TEXT PRIMARY KEY, SortIndex INTEGER NOT NULL, PageNumber INTEGER NOT NULL, PageFigureIndex INTEGER NOT NULL,
                Category TEXT NOT NULL, BboxX REAL NOT NULL, BboxY REAL NOT NULL, BboxW REAL NOT NULL, BboxH REAL NOT NULL,
                Confidence REAL NOT NULL, Caption TEXT NULL, Label TEXT NOT NULL, Source TEXT NOT NULL, LastExtractedPath TEXT NULL,
                CaptionBboxX REAL NULL, CaptionBboxY REAL NULL, CaptionBboxW REAL NULL, CaptionBboxH REAL NULL
            );
            CREATE TABLE IF NOT EXISTS DocumentFigureMasks (
                FigureId TEXT NOT NULL, MaskIndex INTEGER NOT NULL, BboxX REAL NOT NULL, BboxY REAL NOT NULL,
                BboxW REAL NOT NULL, BboxH REAL NOT NULL, LabelText TEXT NULL, Style TEXT NOT NULL, ColorHex TEXT NULL,
                PRIMARY KEY (FigureId, MaskIndex)
            );
            """,
            cancellationToken);

        await SqliteModelSettingsRepository.EnsureColumnsAsync(conn, "DocumentMeta", [
            ("WorkspaceMode", "TEXT NOT NULL DEFAULT 'standard'"),
            ("RetainDatabase", "INTEGER NOT NULL DEFAULT 0")
        ], cancellationToken);

        await SqliteModelSettingsRepository.EnsureColumnsAsync(conn, "DocumentFigures", [
            ("CaptionBboxX", "REAL NULL"), ("CaptionBboxY", "REAL NULL"),
            ("CaptionBboxW", "REAL NULL"), ("CaptionBboxH", "REAL NULL")
        ], cancellationToken);
    }
}
