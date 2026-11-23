using System.Globalization;
using System.Text;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Models;
using Melomg.Cli.Abstractions;
using Melomg.Domain.Extraction;

namespace Melomg.Cli.Commands;

public sealed class ExtractCliCommand : CliCommand
{
    private const string UsageText = "extract <addr|pdfPath> [nums|all...] [-o dir] [--format png|jpg] [--dpi 72..600] [--quality 1..100|low|medium|high|max] [--padding 0..100] [--naming number|id] [--include-caption] [--mask-all] [--mask-match pat] [--mask-style solid|blur|pixelate|whiteout] [--mask-color #RRGGBB] [--no-masks] [--meta] [--manifest] [--keep-overlay] [--retain-db] [--offline] [--quiet] [--verbose] [--confidence 0.05..0.99]";

    public ExtractCliCommand()
        : base(new CommandDescriptor(
            Verb: "extract",
            Usage: UsageText,
            Description: "Renders PDF page regions and crops image/diagram figures as PNG or JPEG with configurable quality.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Extraction,
            Examples:
            [
                "melomg extract Doc_2026...#2.1 -o ./assets --quality high",
                "melomg extract Doc_2026... all -o ./assets --manifest",
                "melomg extract ./paper.pdf 2.1 -o ./assets --quality high",
                "melomg extract Doc_2026... all -o ./assets --mask-all --mask-style blur"
            ]))
    {
    }

    public sealed class Handler(ExtractFiguresHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryGetIntOption(context, "dpi", out var dpiOpt, out int dpiErr))
                return dpiErr;
            if (!CliCommandHelpers.TryGetIntOption(context, "padding", out var padOpt, out int padErr))
                return padErr;
            if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var confOpt, out int confErr))
                return confErr;

            string format = context.NamedOptions.TryGetValue("format", out var fmt) && !string.IsNullOrWhiteSpace(fmt)
                ? fmt
                : "png";
            context.NamedOptions.TryGetValue("quality", out var qualityOpt);
            context.NamedOptions.TryGetValue("naming", out var namingOpt);
            int padding = padOpt ?? ExtractionOptions.DefaultPaddingPx;
            bool includeCaption = context.NamedOptions.ContainsKey("include-caption");
            bool maskAll = context.NamedOptions.ContainsKey("mask-all");
            bool ignoreSavedMasks = context.NamedOptions.ContainsKey("no-masks");
            bool includeMetadata = context.NamedOptions.ContainsKey("meta");
            bool keepOverlay = context.NamedOptions.ContainsKey("keep-overlay");
            bool retainDb = context.NamedOptions.ContainsKey("retain-db");
            bool offline = context.NamedOptions.ContainsKey("offline");
            bool emitManifest = context.NamedOptions.ContainsKey("manifest");
            bool quiet = context.NamedOptions.ContainsKey("quiet");
            bool verbose = context.NamedOptions.ContainsKey("verbose");
            context.NamedOptions.TryGetValue("mask-match", out var maskPatterns);
            context.NamedOptions.TryGetValue("mask-style", out var maskStyle);
            context.NamedOptions.TryGetValue("mask-color", out var maskColor);

            string target = context.PositionalArgs[0];
            var selectors = context.PositionalArgs.Count > 1
                ? context.PositionalArgs.Skip(1).ToList()
                : null;

            var res = await useCase.HandleAsync(
                new ExtractFiguresCommand(
                    target,
                    selectors,
                    context.GlobalOptions.OutputDirectory,
                    format,
                    dpiOpt,
                    padding,
                    includeCaption,
                    maskAll,
                    maskPatterns,
                    ignoreSavedMasks,
                    maskStyle,
                    maskColor,
                    qualityOpt,
                    namingOpt ?? ExtractionOptions.DefaultNamingMode,
                    includeMetadata,
                    keepOverlay,
                    retainDb,
                    offline,
                    context.GlobalOptions.ModelPath,
                    confOpt,
                    emitManifest,
                    quiet,
                    verbose),
                context.CancellationToken);

            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            var v = res.Value;
            var sb = new StringBuilder();
            string capTag = v.IncludeCaption ? ", +caption" : "";
            sb.AppendLine($"Extracted {v.ExtractedCount} figure(s) ({v.Format.ToUpperInvariant()} @ {v.Dpi} DPI, q={v.Quality}, pad={v.PaddingPx}px{capTag}, engine={v.RenderEngine}) -> '{v.OutputDirectory}':");
            if (v.Artifacts is not null)
            {
                foreach (var art in v.Artifacts)
                {
                    string numTag = string.IsNullOrWhiteSpace(art.FigureNumber) ? $"#f{art.Index}" : $"#{art.FigureNumber}";
                    string maskTag = art.AppliedMasks.Count > 0 ? $", {art.AppliedMasks.Count} mask(s)" : "";
                    sb.AppendLine($"  {numTag,-8} {art.FigureId} (p{art.PageNumber}, {art.PixelWidth}x{art.PixelHeight}px, {art.FileSizeBytes} B{maskTag}) -> {art.OutputImagePath}");
                }
            }
            if (!string.IsNullOrWhiteSpace(v.ManifestPath))
                sb.AppendLine($"Manifest: {v.ManifestPath}");
            if (v.CleanedOverlayCount > 0)
                sb.AppendLine($"Cleaned {v.CleanedOverlayCount} QA overlay file(s).");

            CliCommandHelpers.WriteOutput(context, v, sb.ToString().TrimEnd());
            return ExitCodes.Success;
        }
    }
}

public sealed class RenderCliCommand : CliCommand
{
    private const string UsageText = "render <docId> <pageNum|all> [-o dir] [--format png|jpg] [--dpi 72..600] [--quality 1..100|low|medium|high|max] [--overlay]";

    public RenderCliCommand()
        : base(new CommandDescriptor(
            Verb: "render",
            Usage: UsageText,
            Description: "Renders full PDF pages with optional bounding-box, caption, and mask inspection overlays.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Extraction,
            Examples:
            [
                "melomg render Doc_2026... 1 -o ./preview --overlay --dpi 150",
                "melomg render Doc_2026... all -o ./pages --format jpg --quality high"
            ]))
    {
    }

    public sealed class Handler(RenderPagesHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            var pos = context.PositionalArgs;
            if (pos.Count != 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string docId = pos[0];
            if (!CliCommandHelpers.TryGetIntOption(context, "dpi", out var dpiOpt, out int dpiErr))
                return dpiErr;

            string format = context.NamedOptions.TryGetValue("format", out var fmt) && !string.IsNullOrWhiteSpace(fmt)
                ? fmt
                : "png";
            context.NamedOptions.TryGetValue("quality", out var qualityOpt);
            bool overlay = context.NamedOptions.ContainsKey("overlay");

            bool renderAll = string.Equals(pos[1], "all", StringComparison.OrdinalIgnoreCase);
            int? pageNum = null;
            if (!renderAll)
            {
                if (!int.TryParse(pos[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPage))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"Page target '{pos[1]}' must be a 1-based page number or 'all'.", context.GlobalOptions.Json);
                pageNum = parsedPage;
            }

            var res = await useCase.HandleAsync(
                new RenderPagesCommand(docId, pageNum, renderAll, context.GlobalOptions.OutputDirectory, format, dpiOpt, overlay, qualityOpt),
                context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v =>
            {
                var sb = new StringBuilder();
                string ovTag = v.Overlay ? ", overlay=ON" : "";
                sb.AppendLine($"Rendered {v.RenderedCount} page(s) ({v.Format.ToUpperInvariant()} @ {v.Dpi} DPI, q={v.Quality}{ovTag}) -> '{v.OutputDirectory}':");
                foreach (var art in v.Artifacts)
                    sb.AppendLine($"  page {art.PageNumber}: {art.PixelWidth}x{art.PixelHeight}px -> {art.OutputImagePath}");
                return sb.ToString().TrimEnd();
            });
        }
    }
}

public sealed class RmCliCommand : CliCommand
{
    private const string UsageText = "rm <addr>";

    public RmCliCommand()
        : base(new CommandDescriptor(
            Verb: "rm",
            Usage: UsageText,
            Description: "Deletes a document workspace or figure immediately (non-interactive).",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Diagnostics,
            Examples: ["melomg rm Doc_2026...#2.1", "melomg rm Doc_2026..."]))
    {
    }

    public sealed class Handler(DeleteTargetHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(new DeleteTargetCommand(context.PositionalArgs[0]), context.CancellationToken);
            return CliCommandHelpers.Finish(context, res, v =>
                v.EntityKind == "document"
                    ? $"Deleted document workspace {v.DeletedId}."
                    : $"Deleted figure {v.DeletedId} from {v.DocumentId} (remaining: {v.RemainingFigures}).");
        }
    }
}

public sealed class CheckCliCommand : CliCommand
{
    private const string UsageText = "check <addr> [--quiet] [--verbose]";

    public CheckCliCommand()
        : base(new CommandDescriptor(
            Verb: "check",
            Usage: UsageText,
            Description: "Verifies stored PDF SHA-256 integrity, figure bounds, extracted image headers, and sequence anomalies.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Diagnostics,
            Examples: ["melomg check Doc_2026...", "melomg check Doc_2026...#2.1 --json"]))
    {
    }

    public sealed class Handler(LintDocumentHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            bool quiet = context.NamedOptions.ContainsKey("quiet");
            bool verbose = context.NamedOptions.ContainsKey("verbose");
            var res = await useCase.HandleAsync(new LintDocumentQuery(context.PositionalArgs[0], quiet, verbose), context.CancellationToken);
            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            var report = res.Value;
            var sb = new StringBuilder();
            sb.AppendLine($"Check {report.TargetId}: {(report.IsClean ? "CLEAN" : "FAILED")} ({report.ErrorCount} error(s), {report.WarningCount} warning(s))");
            foreach (var issue in report.Issues)
                sb.AppendLine($"  [{issue.Severity}] {issue.Code} ({issue.EntityId}): {issue.Message}");
            if (report.SuspectPages is { Count: > 0 })
                sb.AppendLine($"Suspect pages for QA: {string.Join(", ", report.SuspectPages)}");

            var formatted = sb.ToString().TrimEnd();
            if (!report.IsClean)
            {
                if (context.GlobalOptions.Json)
                    CliCommandHelpers.WriteOutput(context, report, formatted);
                else
                    context.Console.Error.WriteLine(formatted);
                return ExitCodes.ValidationError;
            }

            CliCommandHelpers.WriteOutput(context, report, formatted);
            return ExitCodes.Success;
        }
    }
}

public sealed class ModelCliCommand : CliCommand
{
    private const string UsageText = "model <info|set|threshold|reset> [args...]";

    public ModelCliCommand()
        : base(new CommandDescriptor(
            Verb: "model",
            Usage: UsageText,
            Description: "Inspects, sets, tunes thresholds, or resets the ONNX layout detection model.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Models,
            Examples:
            [
                "melomg model info",
                "melomg model set ./doclaynet.onnx --confidence 0.40 --iou 0.45",
                "melomg model threshold 0.35 0.50",
                "melomg model reset"
            ]))
    {
    }

    public sealed class Handler(
        GetModelInfoHandler getInfo,
        SetModelHandler setModel,
        SetModelThresholdHandler setThreshold,
        ResetModelHandler resetModel) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count == 0)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string sub = context.PositionalArgs[0].ToLowerInvariant();
            switch (sub)
            {
                case "info":
                {
                    var res = await getInfo.HandleAsync(new GetModelInfoQuery(), context.CancellationToken);
                    if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                    var m = res.Value;
                    var text =
                        $"ONNX Model:  {m.ModelName} ({(m.IsBuiltIn ? "built-in" : "custom")})\n" +
                        $"Path:        {m.ModelPath}\n" +
                        $"Format:      IR v{m.IrVersion}, Opset {m.OpsetVersion}, Input=[1, 3, {m.InputHeight}, {m.InputWidth}]\n" +
                        $"Engine:      {m.InferenceEngine} (runtime={(m.RuntimeInstalled ? "installed" : "not installed")})\n" +
                        $"Thresholds:  confidence={m.ConfidenceThreshold:F2}, iou={m.IouThreshold:F2}\n" +
                        $"Classes:     {string.Join(", ", m.SupportedClasses)}";
                    CliCommandHelpers.WriteOutput(context, m, text);
                    return ExitCodes.Success;
                }
                case "set":
                {
                    if (context.PositionalArgs.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: model set <onnxPath> [--confidence 0.05..0.99] [--iou 0.10..0.95]", context.GlobalOptions.Json);

                    if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var conf, out int cErr)) return cErr;
                    if (!CliCommandHelpers.TryGetDoubleOption(context, "iou", out var iou, out int iErr)) return iErr;

                    var res = await setModel.HandleAsync(new SetModelCommand(context.PositionalArgs[1], conf, iou), context.CancellationToken);
                    if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                    CliCommandHelpers.WriteOutput(context, res.Value, $"Configured ONNX model '{res.Value.ModelName}' (conf={res.Value.ConfidenceThreshold:F2}, iou={res.Value.IouThreshold:F2}).");
                    return ExitCodes.Success;
                }
                case "threshold":
                {
                    if (context.PositionalArgs.Count < 2 || context.PositionalArgs.Count > 3)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: model threshold <confidence> [iou]", context.GlobalOptions.Json);

                    if (!double.TryParse(context.PositionalArgs[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double conf))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"Confidence '{context.PositionalArgs[1]}' must be numeric.", context.GlobalOptions.Json);

                    double? iou = null;
                    if (context.PositionalArgs.Count == 3)
                    {
                        if (!double.TryParse(context.PositionalArgs[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedIou))
                            return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"IoU '{context.PositionalArgs[2]}' must be numeric.", context.GlobalOptions.Json);
                        iou = parsedIou;
                    }

                    var res = await setThreshold.HandleAsync(new SetModelThresholdCommand(conf, iou), context.CancellationToken);
                    if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                    CliCommandHelpers.WriteOutput(context, res.Value, $"Updated thresholds: confidence={res.Value.ConfidenceThreshold:F2}, iou={res.Value.IouThreshold:F2}.");
                    return ExitCodes.Success;
                }
                case "reset":
                {
                    var res = await resetModel.HandleAsync(new ResetModelCommand(), context.CancellationToken);
                    if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                    CliCommandHelpers.WriteOutput(context, res.Value, $"Reset ONNX model to built-in '{res.Value.ModelName}' (conf={res.Value.ConfidenceThreshold:F2}, iou={res.Value.IouThreshold:F2}).");
                    return ExitCodes.Success;
                }
                default:
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown model subcommand '{sub}'. Use info, set, threshold, or reset.", context.GlobalOptions.Json);
            }
        }
    }
}
