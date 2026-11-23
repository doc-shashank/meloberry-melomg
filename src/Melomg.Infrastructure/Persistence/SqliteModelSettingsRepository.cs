using System.Text.Json;
using Melomg.Application.Abstractions;
using Melomg.Domain.Models;
using Melomg.Infrastructure.Onnx;
using Melomg.Infrastructure.Serialization;
using Microsoft.Data.Sqlite;

namespace Melomg.Infrastructure.Persistence;

public sealed class SqliteModelSettingsRepository(MelomgStorageOptions options, OnnxProtobufEngine onnxEngine) : IModelSettingsRepository
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized && File.Exists(options.SettingsDbPath)) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized && File.Exists(options.SettingsDbPath)) return;

            Directory.CreateDirectory(options.AppDataDirectory);
            await onnxEngine.EnsureBuiltInModelExistsAsync(cancellationToken);

            await using var conn = OpenSettingsConnection();
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS ModelSettings (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    ModelName TEXT NOT NULL, ModelPath TEXT NOT NULL, IsBuiltIn INTEGER NOT NULL,
                    ConfidenceThreshold REAL NOT NULL, IouThreshold REAL NOT NULL,
                    InputWidth INTEGER NOT NULL, InputHeight INTEGER NOT NULL,
                    OpsetVersion INTEGER NOT NULL, IrVersion INTEGER NOT NULL,
                    SupportedClassesJson TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS DocumentRegistry (
                    DocumentId TEXT PRIMARY KEY, Title TEXT NOT NULL, PageCount INTEGER NOT NULL, FigureCount INTEGER NOT NULL,
                    ModelName TEXT NOT NULL, ConfidenceThreshold REAL NOT NULL, DbPath TEXT NOT NULL,
                    StorageDirectory TEXT NOT NULL, IngestedAtUtc TEXT NOT NULL, WorkspaceMode TEXT NOT NULL DEFAULT 'standard',
                    VocabPrimaryPrefix TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS FigureEntityIndex (FigureId TEXT PRIMARY KEY, DocumentId TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await EnsureColumnsAsync(conn, "DocumentRegistry", [
                ("WorkspaceMode", "TEXT NOT NULL DEFAULT 'standard'"),
                ("VocabPrimaryPrefix", "TEXT NULL")
            ], cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    internal static async Task EnsureColumnsAsync(
        SqliteConnection conn, string tableName, IEnumerable<(string Name, string Definition)> columns, CancellationToken cancellationToken)
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await pragmaCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existingCols.Add(reader.GetString(1));
        }

        foreach (var (name, def) in columns)
        {
            if (existingCols.Contains(name)) continue;
            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {name} {def};";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task ExecuteNonQueryAsync(
        SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public SqliteConnection OpenSettingsConnection() => new($"Data Source={options.SettingsDbPath};Pooling=False");

    public async Task<OnnxModelProfile> GetActiveModelProfileAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT ModelName, ModelPath, IsBuiltIn, ConfidenceThreshold, IouThreshold,
                   InputWidth, InputHeight, OpsetVersion, IrVersion, SupportedClassesJson
            FROM ModelSettings WHERE Id = 1;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return await onnxEngine.GetBuiltInDefaultProfileAsync(cancellationToken);

        bool isBuiltIn = reader.GetInt32(2) != 0;
        string modelPath = isBuiltIn ? options.BuiltInModelPath : reader.GetString(1);
        var classes = JsonSerializer.Deserialize(reader.GetString(9), ModelSettingsJsonContext.Default.ListString)
            ?? ["figure", "diagram", "chart"];

        return OnnxModelProfile.Rehydrate(
            reader.GetString(0), modelPath, isBuiltIn, reader.GetDouble(3), reader.GetDouble(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8), classes);
    }

    public async Task SaveModelProfileAsync(OnnxModelProfile profile, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var conn = OpenSettingsConnection();
        await conn.OpenAsync(cancellationToken);

        const string sql =
            """
            INSERT INTO ModelSettings (
                Id, ModelName, ModelPath, IsBuiltIn, ConfidenceThreshold, IouThreshold,
                InputWidth, InputHeight, OpsetVersion, IrVersion, SupportedClassesJson, UpdatedAtUtc
            )
            VALUES (1, $name, $path, $builtIn, $conf, $iou, $w, $h, $opset, $ir, $classes, $ts)
            ON CONFLICT(Id) DO UPDATE SET
                ModelName = excluded.ModelName, ModelPath = excluded.ModelPath, IsBuiltIn = excluded.IsBuiltIn,
                ConfidenceThreshold = excluded.ConfidenceThreshold, IouThreshold = excluded.IouThreshold,
                InputWidth = excluded.InputWidth, InputHeight = excluded.InputHeight,
                OpsetVersion = excluded.OpsetVersion, IrVersion = excluded.IrVersion,
                SupportedClassesJson = excluded.SupportedClassesJson, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        await ExecuteNonQueryAsync(conn, null, sql, cancellationToken,
            ("$name", profile.ModelName),
            ("$path", profile.ModelPath),
            ("$builtIn", profile.IsBuiltIn ? 1 : 0),
            ("$conf", profile.ConfidenceThreshold),
            ("$iou", profile.IouThreshold),
            ("$w", profile.InputWidth),
            ("$h", profile.InputHeight),
            ("$opset", profile.OpsetVersion),
            ("$ir", profile.IrVersion),
            ("$classes", JsonSerializer.Serialize(profile.SupportedClasses, ModelSettingsJsonContext.Default.IReadOnlyListString)),
            ("$ts", TimeProvider.System.GetUtcNow().ToString("O")));
    }

    public async Task<OnnxModelProfile> ResetModelProfileAsync(CancellationToken cancellationToken = default)
    {
        var defaults = await onnxEngine.GetBuiltInDefaultProfileAsync(cancellationToken);
        await SaveModelProfileAsync(defaults, cancellationToken);
        return defaults;
    }
}
