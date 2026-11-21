using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Melomg.Domain.Common;

namespace Melomg.Cli.Abstractions;

internal static class CliCommandHelpers
{

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

    public static bool TryResolveDocumentId(
        CliInvocationContext ctx,
        bool allowPositional,
        string usage,
        out string docId,
        out int errorCode)
    {
        if (allowPositional && ctx.PositionalArgs.Count > (ctx.TargetId is not null ? 0 : 1))
        {
            docId = string.Empty;
            errorCode = CliResultExtensions.WriteValidationError(ctx.Console, "Cli.InvalidArgs", $"Usage: {usage}", ctx.GlobalOptions.Json);
            return false;
        }

        var resolved = ctx.TargetId
            ?? (allowPositional && ctx.PositionalArgs.Count == 1 ? ctx.PositionalArgs[0] : ctx.EffectiveDocumentId);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            docId = string.Empty;
            errorCode = CliResultExtensions.WriteValidationError(ctx.Console, "Cli.MissingDocument", "No document workspace targeted. Pass <docID> or '--doc <docID>'.", ctx.GlobalOptions.Json);
            return false;
        }

        docId = resolved;
        errorCode = ExitCodes.Success;
        return true;
    }

    public static bool TryResolveFigureId(
        CliInvocationContext ctx,
        bool allowPositional,
        string usage,
        out string figId,
        out int errorCode)
    {
        if (allowPositional && ctx.PositionalArgs.Count > (ctx.TargetId is not null ? 0 : 1))
        {
            figId = string.Empty;
            errorCode = CliResultExtensions.WriteValidationError(ctx.Console, "Cli.InvalidArgs", $"Usage: {usage}", ctx.GlobalOptions.Json);
            return false;
        }

        var resolved = ctx.TargetId
            ?? (allowPositional && ctx.PositionalArgs.Count == 1 ? ctx.PositionalArgs[0] : ctx.EffectiveFigureId);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            figId = string.Empty;
            errorCode = CliResultExtensions.WriteValidationError(ctx.Console, "Cli.MissingFigure", "No figure targeted. Pass <figID> or '--figure <figID>'.", ctx.GlobalOptions.Json);
            return false;
        }

        figId = resolved;
        errorCode = ExitCodes.Success;
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

    public static bool ConfirmDeletion(CliInvocationContext ctx)
    {
        if (ctx.GlobalOptions.Yes) return true;
        var promptWriter = ctx.GlobalOptions.Json ? ctx.Console.Error : ctx.Console.Out;
        promptWriter.Write("Confirm deletion? [y/N]: ");
        promptWriter.Flush();
        var answer = ctx.Console.In.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
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
