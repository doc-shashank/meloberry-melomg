using System.Globalization;
using Melomg.Application.Abstractions;

namespace Melomg.Infrastructure.Services;

public sealed class MonotonicTimestampIdGenerator(MelomgStorageOptions? options = null) : IIdGenerator
{
    private readonly Lock _gate = new();
    private readonly string? _clockFilePath = string.IsNullOrWhiteSpace(options?.AppDataDirectory)
        ? null
        : Path.Combine(Path.GetFullPath(options!.AppDataDirectory), ".melomg_id_clock");
    private long _lastEpochMs;

    public string NewDocumentId() => $"Doc_{NextTimestampStamp()}";
    public string NewFigureId() => $"Fig_{NextTimestampStamp()}";

    private string NextTimestampStamp()
    {
        long nextMs;
        lock (_gate)
            nextMs = _clockFilePath is null ? NextInMemoryMs() : NextCrossProcessMs(_clockFilePath);

        return DateTimeOffset.FromUnixTimeMilliseconds(nextMs).ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
    }

    private long NextInMemoryMs()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _lastEpochMs = nowMs <= _lastEpochMs ? _lastEpochMs + 1 : nowMs;
    }

    private long NextCrossProcessMs(string clockFilePath)
    {
        var dir = Path.GetDirectoryName(clockFilePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        const int maxAttempts = 50;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(clockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                long persistedMs = 0;
                if (fs.Length > 0)
                {
                    using var reader = new StreamReader(fs, leaveOpen: true);
                    long.TryParse(reader.ReadToEnd().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out persistedMs);
                }

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long floorMs = Math.Max(_lastEpochMs, persistedMs);
                long nextMs = nowMs <= floorMs ? floorMs + 1 : nowMs;

                fs.Position = 0;
                fs.SetLength(0);
                using (var writer = new StreamWriter(fs, leaveOpen: true))
                {
                    writer.Write(nextMs.ToString(CultureInfo.InvariantCulture));
                    writer.Flush();
                }
                fs.Flush(flushToDisk: true);

                return _lastEpochMs = nextMs;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(1);
            }
        }

        return NextInMemoryMs();
    }
}
