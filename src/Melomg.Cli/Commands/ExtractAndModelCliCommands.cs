using System.Globalization;
using System.Text;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Application.UseCases.Models;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Docs;
using Melomg.Domain.Common;
using Melomg.Domain.Extraction;

namespace Melomg.Cli.Commands;

public sealed class ExtractCliCommand : CliCommand
{
    public ExtractCliCommand()
        : base(new CommandDescriptor(
            Verb: "extract",
            Usage: "[<docID|figID>] extract [all|<index>|<figID>|page <n>] [outputDir] [--format <png|jpg>] [--dpi <72..600>] [--quality <1..100|low|medium|high|max>] [--padding <0..100>] [--include-caption] [--obfuscate-labels] [--obfuscate-label <patterns>] [--obfuscate-style <solid|blur|pixelate|whiteout>] [--obfuscate-color <#RRGGBB>] [--no-masks] [--meta] [--keep-overlay] [--retain-db]",
            Description: "Renders actual PDF page regions via Windows.Data.Pdf and crops figures as PNG or JPEG with configurable quality and optional label obfuscation.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Extraction,
            DetailedNotes: "Outputs <figID>.<png|jpg> into [outputDir] (defaults to <workspaceDir>/figures/). Pass '--meta' to also emit <figID>.meta.json sidecar files. Any QA overlays created via 'render --overlay' are automatically cleaned up after extraction succeeds (pass '--keep-overlay' to retain them). For non-project 'ingest' workspaces, the temporary Doc_<d/t>.db file is also deleted automatically after extraction unless '--retain-db' was passed on 'ingest' or 'extract' (project workspaces opened with 'open' always retain their DB). Use '--quality <1..100|low|medium|high|max>' (default 90) to control JPEG DCT quantization and PNG compression; presets also set a default DPI when '--dpi' is omitted (low=96, medium=150, high=240, max=300). Pass '--include-caption' to include matched caption lines in the crop. Use '--obfuscate-labels' or '--obfuscate-label \"term1,term2\"' (or persisted 'mask' regions) with '--obfuscate-style solid|whiteout|blur|pixelate' to redact figure labels; pass '--no-masks' to export a clean unmasked copy.",
            Examples:
            [
                "melomg Fig_2026... extract ./out --dpi 300 --format jpg --quality 92",
                "melomg Fig_2026... extract ./out --quality high --include-caption",
                "melomg Fig_2026... extract ./quiz --obfuscate-labels --obfuscate-style blur",
                "melomg Doc_2026... extract all ./study --obfuscate-label \"Neutrophil,Endothelial*\" --obfuscate-style solid",
                "melomg Fig_2026... extract ./clean --no-masks"
            ]))
    {
    }

    public sealed class Handler(ExtractFiguresHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryGetIntOption(context, "dpi", out var dpiOpt, out int dpiErr))
                return dpiErr;
            if (!CliCommandHelpers.TryGetIntOption(context, "padding", out var padOpt, out int padErr))
                return padErr;

            string format = context.NamedOptions.TryGetValue("format", out var fmt) && !string.IsNullOrWhiteSpace(fmt)
                ? fmt
                : "png";
            context.NamedOptions.TryGetValue("quality", out var qualityOpt);
            int padding = padOpt ?? ExtractionOptions.DefaultPaddingPx;
            bool includeCaption = context.NamedOptions.ContainsKey("include-caption");
            bool obfuscateAll = context.NamedOptions.ContainsKey("obfuscate-labels");
            bool ignoreSavedMasks = context.NamedOptions.ContainsKey("no-masks");
            bool includeMetadata = context.NamedOptions.ContainsKey("meta");
            bool keepOverlay = context.NamedOptions.ContainsKey("keep-overlay");
            bool retainDb = context.NamedOptions.ContainsKey("retain-db");
            context.NamedOptions.TryGetValue("obfuscate-label", out var obfPatterns);
            context.NamedOptions.TryGetValue("obfuscate-style", out var obfStyle);
            context.NamedOptions.TryGetValue("obfuscate-color", out var obfColor);

            string? targetId;
            string? selector = null;
            int? pageNumber = null;
            string? outputDir = null;

            var pos = context.PositionalArgs;

            if (context.TargetId is not null && context.TargetId.StartsWith("Fig_", StringComparison.Ordinal))
            {
                if (pos.Count > 1)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: <figID> extract [outputDir] [--format <png|jpg>] [--dpi <72..600>] [--quality <1..100|low|medium|high|max>] [--padding <0..100>]", context.GlobalOptions.Json);
                targetId = context.TargetId;
                outputDir = pos.Count == 1 ? pos[0] : null;
            }
            else if (pos.Count == 0)
            {
                targetId = context.TargetId ?? context.ExplicitTargetId ?? context.SessionContext.ActiveFigureId ?? context.SessionContext.ActiveDocumentId;
            }
            else if (string.Equals(pos[0], "page", StringComparison.OrdinalIgnoreCase))
            {
                if (pos.Count < 2 || pos.Count > 3)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID>] extract page <pageNum> [outputDir]", context.GlobalOptions.Json);

                if (!int.TryParse(pos[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pNum))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"Page number '{pos[1]}' must be an integer.", context.GlobalOptions.Json);

                targetId = context.TargetId ?? context.EffectiveDocumentId;
                pageNumber = pNum;
                outputDir = pos.Count == 3 ? pos[2] : null;
            }
            else if (context.TargetId is null &&
                     context.GlobalOptions.FigureId is not null &&
                     pos.Count == 1 &&
                     !string.Equals(pos[0], "all", StringComparison.OrdinalIgnoreCase) &&
                     !pos[0].StartsWith("Fig_", StringComparison.Ordinal) &&
                     !int.TryParse(pos[0], out _))
            {
                targetId = context.GlobalOptions.FigureId;
                outputDir = pos[0];
            }
            else
            {
                if (pos.Count > 2)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID>] extract [all|<index>|<figID>] [outputDir]", context.GlobalOptions.Json);

                targetId = context.TargetId ?? context.EffectiveDocumentId;
                selector = pos[0];
                outputDir = pos.Count == 2 ? pos[1] : null;
            }

            if (string.IsNullOrWhiteSpace(targetId))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.MissingTarget", "No document or figure targeted. Run 'ingest' or pass <docID>/<figID> or '--doc'.", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(
                new ExtractFiguresCommand(
                    targetId,
                    selector,
                    pageNumber,
                    outputDir,
                    format,
                    dpiOpt,
                    padding,
                    includeCaption,
                    obfuscateAll,
                    obfPatterns,
                    ignoreSavedMasks,
                    obfStyle,
                    obfColor,
                    qualityOpt,
                    includeMetadata,
                    keepOverlay,
                    retainDb),
                context.CancellationToken);

            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            if (!res.Value.RetainedDatabase)
            {
                context.SessionContext.ActiveDocumentId = null;
                context.SessionContext.ActiveFigureId = null;
            }
            else
            {
                context.SessionContext.ActiveDocumentId = res.Value.DocumentId;
                if (res.Value.Artifacts.Count == 1)
                    context.SessionContext.ActiveFigureId = res.Value.Artifacts[0].FigureId;
            }

            var sb = new StringBuilder();
            string capTag = res.Value.IncludeCaption ? ", +caption" : "";
            sb.AppendLine($"Extracted {res.Value.ExtractedCount} figure(s) ({res.Value.Format.ToUpperInvariant()} @ {res.Value.Dpi} DPI, q={res.Value.Quality}, padding {res.Value.PaddingPx}px{capTag}, engine={res.Value.RenderEngine}) to '{res.Value.OutputDirectory}':");
            foreach (var art in res.Value.Artifacts)
            {
                string maskTag = art.AppliedMasks.Count > 0 ? $", {art.AppliedMasks.Count} mask(s)" : "";
                sb.AppendLine($"  [{art.Index}] {art.FigureId} (p{art.PageNumber}, {art.PixelWidth}x{art.PixelHeight}px, {art.FileSizeBytes} bytes{maskTag}) -> {art.OutputImagePath}");
            }

            if (res.Value.CleanedOverlayCount > 0)
                sb.AppendLine($"Cleaned up {res.Value.CleanedOverlayCount} QA overlay file(s).");

            if (!res.Value.RetainedDatabase)
                sb.AppendLine($"Discarded temporary workspace DB {res.Value.DocumentId}.");

            CliCommandHelpers.WriteOutput(context, res.Value, sb.ToString().TrimEnd());
            return ExitCodes.Success;
        }
    }
}

public sealed class RenderCliCommand : CliCommand
{
    public RenderCliCommand()
        : base(new CommandDescriptor(
            Verb: "render",
            Usage: "[<docID>] render <pageNum|all> [outputDir] [--format <png|jpg>] [--dpi <72..600>] [--quality <1..100|low|medium|high|max>] [--overlay]",
            Description: "Renders full PDF pages with optional bounding-box, caption, and mask inspection overlays.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Extraction,
            DetailedNotes: "Outputs page_001.<png|jpg> (or page_001_overlay.<png|jpg> with '--overlay') into [outputDir] (defaults to <workspaceDir>/pages/). Use '--quality <1..100|low|medium|high|max>' (default 90) to control JPEG DCT quantization and PNG compression; presets set default DPI when '--dpi' is omitted (low=96, medium=150, high=240, max=300). Overlays show blue numbered figure crop boxes, emerald dashed caption boxes, and crimson hatched obfuscation masks.",
            Examples:
            [
                "melomg Doc_2026... render 1 ./preview --overlay --dpi 150",
                "melomg Doc_2026... render all ./pages --format jpg --quality high"
            ]))
    {
    }

    public sealed class Handler(RenderPagesHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            var docId = context.TargetId ?? context.EffectiveDocumentId;
            if (string.IsNullOrWhiteSpace(docId))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.MissingDocument", "No document workspace targeted. Run 'ingest' or pass <docID> or '--doc'.", context.GlobalOptions.Json);

            var pos = context.PositionalArgs;
            if (pos.Count < 1 || pos.Count > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID>] render <pageNum|all> [outputDir] [--format <png|jpg>] [--dpi <72..600>] [--quality <1..100|low|medium|high|max>] [--overlay]", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryGetIntOption(context, "dpi", out var dpiOpt, out int dpiErr))
                return dpiErr;

            string format = context.NamedOptions.TryGetValue("format", out var fmt) && !string.IsNullOrWhiteSpace(fmt)
                ? fmt
                : "png";
            context.NamedOptions.TryGetValue("quality", out var qualityOpt);
            bool overlay = context.NamedOptions.ContainsKey("overlay");
            string? outputDir = pos.Count == 2 ? pos[1] : null;

            bool renderAll = string.Equals(pos[0], "all", StringComparison.OrdinalIgnoreCase);
            int? pageNum = null;
            if (!renderAll)
            {
                if (!int.TryParse(pos[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPage))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"Page target '{pos[0]}' must be a 1-based page number or 'all'.", context.GlobalOptions.Json);
                pageNum = parsedPage;
            }

            var res = await useCase.HandleAsync(
                new RenderPagesCommand(docId, pageNum, renderAll, outputDir, format, dpiOpt, overlay, qualityOpt),
                context.CancellationToken);

            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            context.SessionContext.ActiveDocumentId = res.Value.DocumentId;

            var sb = new StringBuilder();
            string ovTag = res.Value.Overlay ? ", overlay=ON" : "";
            sb.AppendLine($"Rendered {res.Value.RenderedCount} page(s) ({res.Value.Format.ToUpperInvariant()} @ {res.Value.Dpi} DPI, q={res.Value.Quality}{ovTag}, engine={res.Value.RenderEngine}) to '{res.Value.OutputDirectory}':");
            foreach (var art in res.Value.Artifacts)
            {
                string boxInfo = art.OverlayEnabled ? $", {art.OverlayFigureCount} box(es)" : "";
                sb.AppendLine($"  page {art.PageNumber}: {art.PixelWidth}x{art.PixelHeight}px, {art.FileSizeBytes} bytes{boxInfo} -> {art.OutputImagePath}");
            }

            CliCommandHelpers.WriteOutput(context, res.Value, sb.ToString().TrimEnd());
            return ExitCodes.Success;
        }
    }
}

public sealed class ModelCliCommand : CliCommand
{
    public ModelCliCommand()
        : base(new CommandDescriptor(
            Verb: "model",
            Usage: "model <info|set|threshold|reset> [args...]",
            Description: "Inspects, sets, tunes thresholds, or resets the ONNX layout detection model.",
            AcceptsLeadingTargetId: false,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Models,
            DetailedNotes: "Subcommands:\n  model info\n  model set <onnxPath> [--confidence <0.05..0.99>] [--iou <0.10..0.95>]\n  model threshold <confidence> [iou]\n  model reset",
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
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: model <info|set|threshold|reset> ...", context.GlobalOptions.Json);

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
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: model set <onnxPath> [--confidence <0.05..0.99>] [--iou <0.10..0.95>]", context.GlobalOptions.Json);

                    if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var conf, out int cErr)) return cErr;
                    if (!CliCommandHelpers.TryGetDoubleOption(context, "iou", out var iou, out int iErr)) return iErr;

                    var res = await setModel.HandleAsync(new SetModelCommand(context.PositionalArgs[1], conf, iou), context.CancellationToken);
                    if (!res.IsSuccess) return res.ToExitCode(context.Console, context.GlobalOptions.Json);
                    CliCommandHelpers.WriteOutput(context, res.Value, $"Configured ONNX model '{res.Value.ModelName}' (IR v{res.Value.IrVersion}, Opset {res.Value.OpsetVersion}, conf={res.Value.ConfidenceThreshold:F2}, iou={res.Value.IouThreshold:F2}).");
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

public sealed class DeleteCliCommand : CliCommand
{
    public DeleteCliCommand()
        : base(new CommandDescriptor(
            Verb: "delete",
            Usage: "<docID|figID> delete [--yes]",
            Description: "Prints impact warning and confirms [y/N] before deleting a figure or document workspace.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.DiagnosticsAndDocs,
            DetailedNotes: "Always prints a WARNING impact summary to stderr and prompts 'Confirm deletion? [y/N]:' unless '--yes' is set.",
            Examples: ["melomg Fig_2026... delete", "melomg --yes Doc_2026... delete"]))
    {
    }

    public sealed class Handler(
        GetDeleteImpactHandler impactHandler,
        DeleteDocumentHandler deleteDocument,
        DeleteFigureHandler deleteFigure) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > (context.TargetId is not null ? 0 : 1))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: <docID|figID> delete [--yes]", context.GlobalOptions.Json);

            var targetId = context.TargetId ?? (context.PositionalArgs.Count == 1 ? context.PositionalArgs[0] : context.ExplicitTargetId);
            if (string.IsNullOrWhiteSpace(targetId))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.MissingTarget", "Usage: <docID|figID> delete [--yes]", context.GlobalOptions.Json);

            var impact = await impactHandler.HandleAsync(new GetDeleteImpactQuery(targetId), context.CancellationToken);
            if (!impact.IsSuccess)
                return impact.ToExitCode(context.Console, context.GlobalOptions.Json);

            context.Console.Error.WriteLine($"WARNING: {impact.Value.WarningSummary}");

            if (!CliCommandHelpers.ConfirmDeletion(context))
            {
                CliCommandHelpers.WriteOutput(context, new DeleteAbortedPayload(true, targetId), "Deletion cancelled.");
                return ExitCodes.Success;
            }

            if (targetId.StartsWith("Doc_", StringComparison.Ordinal))
            {
                return CliCommandHelpers.Finish(
                    context,
                    await deleteDocument.HandleAsync(new DeleteDocumentCommand(targetId), context.CancellationToken),
                    v => $"Deleted document workspace {v.DeletedDocumentId}.",
                    _ =>
                    {
                        if (context.SessionContext.ActiveDocumentId == targetId)
                        {
                            context.SessionContext.ActiveDocumentId = null;
                            context.SessionContext.ActiveFigureId = null;
                        }
                    });
            }

            return CliCommandHelpers.Finish(
                context,
                await deleteFigure.HandleAsync(new DeleteFigureCommand(targetId), context.CancellationToken),
                v => $"Deleted figure {v.DeletedFigureId} (remaining: {v.RemainingFigures}).",
                _ => { if (context.SessionContext.ActiveFigureId == targetId) context.SessionContext.ActiveFigureId = null; });
        }
    }
}

public sealed class LintCliCommand : CliCommand
{
    public LintCliCommand()
        : base(new CommandDescriptor(
            Verb: "lint",
            Usage: "[<docID|figID>] lint",
            Description: "Checks source PDF SHA-256 integrity, figures/ directory, bounding boxes, and extracted image magic bytes.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.DiagnosticsAndDocs,
            Examples: ["melomg Doc_2026... lint", "melomg --doc Doc_2026... lint --json"]))
    {
    }

    public sealed class Handler(LintDocumentHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > (context.TargetId is not null ? 0 : 1))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID|figID>] lint", context.GlobalOptions.Json);

            var targetId = context.TargetId
                ?? (context.PositionalArgs.Count == 1 ? context.PositionalArgs[0] : null)
                ?? context.ExplicitTargetId
                ?? context.SessionContext.ActiveDocumentId
                ?? context.SessionContext.ActiveFigureId;

            if (string.IsNullOrWhiteSpace(targetId))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.MissingTarget", "No document or figure targeted for lint.", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(new LintDocumentQuery(targetId), context.CancellationToken);
            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            var report = res.Value;
            var sb = new StringBuilder();
            sb.AppendLine($"Lint {report.TargetId}: {(report.IsClean ? "CLEAN" : "FAILED")} ({report.ErrorCount} error(s), {report.WarningCount} warning(s))");
            foreach (var issue in report.Issues)
                sb.AppendLine($"  [{issue.Severity}] {issue.Code} ({issue.EntityId}): {issue.Message}");

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

public sealed class DocsCliCommand : CliCommand
{
    public DocsCliCommand()
        : base(new CommandDescriptor(
            Verb: "docs",
            Usage: "docs [overview|commands|onnx|agent|architecture] [--all]",
            Description: "Prints built-in Markdown documentation guides bundled directly inside melomg.exe.",
            AcceptsLeadingTargetId: false,
            HandlerType: typeof(Handler),
            Category: CommandCategory.DiagnosticsAndDocs,
            Examples: ["melomg docs", "melomg docs onnx", "melomg docs --all --json"]))
    {
    }

    public sealed class Handler : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            bool showAll = context.NamedOptions.ContainsKey("all");

            if (showAll)
            {
                var all = EmbeddedDocsCatalog.Topics
                    .Select(t => new DocsTopicAllItem(t.Topic, t.FileName, t.Title, t.Summary, EmbeddedDocsCatalog.ReadContent(t)))
                    .ToList();

                var sb = new StringBuilder();
                foreach (var item in all)
                {
                    sb.AppendLine($"================ {item.Title} ({item.FileName}) ================");
                    sb.AppendLine(item.Content);
                    sb.AppendLine();
                }

                CliCommandHelpers.WriteOutput(context, all, sb.ToString().TrimEnd());
                return Task.FromResult(ExitCodes.Success);
            }

            if (context.PositionalArgs.Count == 0)
            {
                var indexPayload = EmbeddedDocsCatalog.Topics
                    .Select(t => new DocsTopicIndexItem(t.Topic, t.FileName, t.Title, t.Summary))
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("melomg Built-In Documentation Guides:");
                sb.AppendLine();
                foreach (var t in EmbeddedDocsCatalog.Topics)
                    sb.AppendLine($"  melomg docs {t.Topic,-14} ({t.FileName}) — {t.Summary}");
                sb.AppendLine();
                sb.AppendLine("Run 'melomg docs <topic>' or 'melomg docs --all [--json]' to read full content.");

                CliCommandHelpers.WriteOutput(context, indexPayload, sb.ToString().TrimEnd());
                return Task.FromResult(ExitCodes.Success);
            }

            if (context.PositionalArgs.Count > 1)
                return Task.FromResult(CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: docs [overview|commands|onnx|agent|architecture] [--all]", context.GlobalOptions.Json));

            var topic = EmbeddedDocsCatalog.FindTopic(context.PositionalArgs[0]);
            if (topic is null)
                return Task.FromResult(Result.Failure(new Error("Docs.TopicNotFound", $"Unknown docs topic '{context.PositionalArgs[0]}'. Use: overview, commands, onnx, agent, or architecture.", ErrorType.NotFound)).ToExitCode(context.Console, context.GlobalOptions.Json));

            string content = EmbeddedDocsCatalog.ReadContent(topic);
            CliCommandHelpers.WriteOutput(context, new DocsTopicDetailPayload(topic.Topic, topic.FileName, topic.Title, content), content);
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
