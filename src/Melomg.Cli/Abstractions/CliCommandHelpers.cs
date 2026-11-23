using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Melomg.Domain.Common;

namespace Melomg.Cli.Abstractions;

internal static class CliCommandHelpers
{
    public static bool IsAgentEnvironmentEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("MELOMG_AGENT"), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("MBY_AGENT"), "1", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveEnvAppData() =>
        Environment.GetEnvironmentVariable("MELOMG_APP_DATA")
        ?? Environment.GetEnvironmentVariable("MELOMG_APPDATA");

    public static bool TryParseReorderMode(string raw, out ReorderMode mode)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "exchange":
                mode = ReorderMode.Exchange;
                return true;
            case "shift":
                mode = ReorderMode.Shift;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static bool TryParseMoveSpec(string raw, out int index, out ReorderMode mode)
    {
        index = 0;
        mode = ReorderMode.Shift;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            return false;

        if (parts.Length == 2)
            return TryParseReorderMode(parts[1], out mode);

        return true;
    }

    public static bool TryGetDoubleOption(
        CliInvocationContext context,
        string key,
        out double? parsed,
        out int errorCode)
    {
        parsed = null;
        errorCode = ExitCodes.Success;
        if (!context.NamedOptions.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            errorCode = CliResultExtensions.WriteValidationError(
                context.Console,
                "Cli.InvalidNumber",
                $"Option '--{key}' value '{raw}' must be a valid number.",
                context.GlobalOptions.Json);
            return false;
        }

        parsed = value;
        return true;
    }

    public static bool TryGetIntOption(
        CliInvocationContext context,
        string key,
        out int? parsed,
        out int errorCode)
    {
        parsed = null;
        errorCode = ExitCodes.Success;
        if (!context.NamedOptions.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            errorCode = CliResultExtensions.WriteValidationError(
                context.Console,
                "Cli.InvalidNumber",
                $"Option '--{key}' value '{raw}' must be an integer.",
                context.GlobalOptions.Json);
            return false;
        }

        parsed = value;
        return true;
    }

    public static bool TryParseOptionalPositiveIndex(
        string? raw,
        out int? parsed,
        out int errorCode,
        IConsole console,
        bool json)
    {
        parsed = null;
        errorCode = ExitCodes.Success;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            errorCode = CliResultExtensions.WriteValidationError(
                console,
                "Cli.InvalidIndex",
                $"Index '{raw}' must be an integer.",
                json);
            return false;
        }

        parsed = value;
        return true;
    }

    public static bool TryParseDoubles(IReadOnlyList<string> args, int startIndex, Span<double> values)
    {
        if (args.Count < startIndex + values.Length) return false;
        for (int i = 0; i < values.Length; i++)
            if (!double.TryParse(args[startIndex + i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        return true;
    }

    public static async Task<Result<string>> ReadFileOrStdinAsync(string fileOrDash, IConsole console, CancellationToken cancellationToken)
    {
        if (string.Equals(fileOrDash, "-", StringComparison.Ordinal))
        {
            string text = await console.In.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                return new Error("Json.EmptyInput", "No JSON input received on stdin.", ErrorType.Validation);
            return text;
        }

        string fullPath = Path.GetFullPath(fileOrDash);
        if (!File.Exists(fullPath))
            return new Error("File.NotFound", $"Input JSON file '{fullPath}' was not found.", ErrorType.NotFound);

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    public static int Finish<T>(
        CliInvocationContext ctx,
        Result<T> res,
        Func<T, string> format,
        Action<T>? onSuccess = null)
    {
        if (!res.IsSuccess)
            return res.ToExitCode(ctx.Console, ctx.GlobalOptions.Json);
        onSuccess?.Invoke(res.Value);
        WriteOutput(ctx, res.Value, format(res.Value));
        return ExitCodes.Success;
    }

    public static void WriteOutput<T>(CliInvocationContext context, T payload, string humanReadableText)
    {
        if (context.GlobalOptions.Silent && !context.GlobalOptions.Json)
            return;

        if (context.GlobalOptions.Json)
        {
            var envelope = new CliJsonEnvelope<T>(true, payload);
            var typeInfo = (JsonTypeInfo<CliJsonEnvelope<T>>?)MelomgCliJsonContext.Default.GetTypeInfo(typeof(CliJsonEnvelope<T>))
                ?? throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered in MelomgCliJsonContext.");
            context.Console.Out.WriteLine(JsonSerializer.Serialize(envelope, typeInfo));
            return;
        }

        context.Console.Out.WriteLine(humanReadableText);
    }
}
