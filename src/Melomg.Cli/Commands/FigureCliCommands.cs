using System.Globalization;
using System.Text;
using Melomg.Application.UseCases.Figures;
using Melomg.Cli.Abstractions;

namespace Melomg.Cli.Commands;

public sealed class FiguresCliCommand : CliCommand
{
    public FiguresCliCommand()
        : base(new CommandDescriptor(
            Verb: "figures",
            Usage: "[<docID>] figures [--page <n>] [--category <cat>] [--min-confidence <0..1>]",
            Description: "Lists detected and manual figures in a document with 1-based indices, coordinates, and captions.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Doc_2026... figures", "melomg --doc Doc_2026... figures --page 1 --category chart"]))
    {
    }

    public sealed class Handler(ListFiguresHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveDocumentId(context, allowPositional: true, "[<docID>] figures", out var docId, out int err))
                return err;

            if (!CliCommandHelpers.TryGetIntOption(context, "page", out var pageNum, out int pageErr))
                return pageErr;
            if (!CliCommandHelpers.TryGetDoubleOption(context, "min-confidence", out var minConf, out int confErr))
                return confErr;

            context.NamedOptions.TryGetValue("category", out var category);
            var res = await useCase.HandleAsync(new ListFiguresQuery(docId, pageNum, category, minConf), context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v =>
            {
                if (v.Figures.Count == 0)
                    return $"Document {v.DocumentId} has 0 matching figures.";

                var sb = new StringBuilder();
                sb.AppendLine($"Figures in {v.DocumentId} (\"{v.Title}\"): {v.Figures.Count} shown / {v.TotalFigures} total");
                foreach (var f in v.Figures)
                {
                    var cap = string.IsNullOrWhiteSpace(f.Caption) ? "" : $"  \"{f.Caption}\"";
                    sb.AppendLine($"  [{f.Index}] {f.FigureId}  p{f.PageNumber}  {f.Category,-8}  conf={f.Confidence:F2}  src={f.Source,-11}  bbox=({f.BoundingBox.X:F1}, {f.BoundingBox.Y:F1}, {f.BoundingBox.Width:F1}, {f.BoundingBox.Height:F1}){cap}");
                }
                return sb.ToString().TrimEnd();
            });
        }
    }
}

public sealed class InspectCliCommand : CliCommand
{
    public InspectCliCommand()
        : base(new CommandDescriptor(
            Verb: "inspect",
            Usage: "[<figID>] inspect",
            Description: "Displays detailed coordinates, normalized box, pixel size, caption, and extraction state for a figure.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Fig_2026... inspect", "melomg --figure Fig_2026... inspect --json"]))
    {
    }

    public sealed class Handler(InspectFigureHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: true, "[<figID>] inspect", out var figId, out int err))
                return err;

            var res = await useCase.HandleAsync(new InspectFigureQuery(figId), context.CancellationToken);
            return CliCommandHelpers.Finish(context, res, f =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Figure:      {f.FigureId} (#{f.Index} in {f.DocumentId})");
                sb.AppendLine($"Page:        {f.PageNumber} (page-local #{f.PageFigureIndex}, {f.PageWidthPts:F1}x{f.PageHeightPts:F1} pts)");
                sb.AppendLine($"Category:    {f.Category}  (source: {f.Source}, confidence: {f.Confidence:F2})");
                sb.AppendLine($"Label:       {f.Label}");
                sb.AppendLine($"Caption:     {f.Caption ?? "(none)"}");
                sb.AppendLine($"BBox (pts):  x={f.BoundingBoxPts.X:F2}, y={f.BoundingBoxPts.Y:F2}, w={f.BoundingBoxPts.Width:F2}, h={f.BoundingBoxPts.Height:F2}");
                if (f.CaptionBoundingBoxPts is { } cb)
                    sb.AppendLine($"CaptionBBox: x={cb.X:F2}, y={cb.Y:F2}, w={cb.Width:F2}, h={cb.Height:F2}");
                sb.AppendLine($"Normalized:  x={f.NormalizedBox.X:F4}, y={f.NormalizedBox.Y:F4}, w={f.NormalizedBox.Width:F4}, h={f.NormalizedBox.Height:F4}");
                sb.AppendLine($"Pixels@150:  {f.DefaultPixelRectAt150Dpi.PixelWidth}x{f.DefaultPixelRectAt150Dpi.PixelHeight} px");
                sb.AppendLine($"Saved Masks: {f.LabelMasks.Count}");
                foreach (var m in f.LabelMasks)
                    sb.AppendLine($"               [{m.Index}] ({m.BoundingBoxPts.X:F1}, {m.BoundingBoxPts.Y:F1}, {m.BoundingBoxPts.Width:F1}, {m.BoundingBoxPts.Height:F1}) style={m.Style} {(m.LabelText is null ? "" : $"\"{m.LabelText}\"")}".TrimEnd());
                sb.AppendLine($"Text Labels: {f.DetectedLabels.Count}");
                foreach (var l in f.DetectedLabels.Take(20))
                    sb.AppendLine($"               [{l.Index}] \"{l.Text}\" @ ({l.BoundingBoxPts.X:F1}, {l.BoundingBoxPts.Y:F1}, {l.BoundingBoxPts.Width:F1}, {l.BoundingBoxPts.Height:F1})");
                if (f.DetectedLabels.Count > 20)
                    sb.AppendLine($"               ... (+{f.DetectedLabels.Count - 20} more; run '--json' for full list)");
                sb.AppendLine($"Extracted:   {f.LastExtractedPath ?? "(not yet extracted)"}");
                return sb.ToString().TrimEnd();
            }, f => { context.SessionContext.ActiveDocumentId = f.DocumentId; context.SessionContext.ActiveFigureId = f.FigureId; });
        }
    }
}

public sealed class AddFigureCliCommand : CliCommand
{
    public AddFigureCliCommand()
        : base(new CommandDescriptor(
            Verb: "addfigure",
            Usage: "[<docID>] addfigure <page> <x> <y> <width> <height> [--category <cat>] [--caption <text>] [--label <text>] [index]",
            Description: "Manually registers a figure bounding box on a PDF page and inserts it at [index] or appends.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Doc_2026... addfigure 1 72 120 420 260 --category diagram --caption \"Figure 1: System\""]))
    {
    }

    public sealed class Handler(AddManualFigureHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveDocumentId(context, allowPositional: false, "[<docID>] addfigure <page> <x> <y> <width> <height> [index]", out var docId, out int err))
                return err;

            if (context.PositionalArgs.Count < 5 || context.PositionalArgs.Count > 6)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID>] addfigure <page> <x> <y> <width> <height> [index]", context.GlobalOptions.Json);

            Span<double> coords = stackalloc double[4];
            if (!int.TryParse(context.PositionalArgs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNum) ||
                !CliCommandHelpers.TryParseDoubles(context.PositionalArgs, 1, coords))
            {
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", "Page and bounding box coordinates (x, y, width, height) must be numeric.", context.GlobalOptions.Json);
            }

            if (!CliCommandHelpers.TryParseOptionalPositiveIndex(
                    context.PositionalArgs.Count == 6 ? context.PositionalArgs[5] : null,
                    out var index,
                    out int idxErr,
                    context.Console,
                    context.GlobalOptions.Json))
            {
                return idxErr;
            }

            context.NamedOptions.TryGetValue("category", out var category);
            context.NamedOptions.TryGetValue("caption", out var caption);
            context.NamedOptions.TryGetValue("label", out var label);

            var res = await useCase.HandleAsync(
                new AddManualFigureCommand(docId, pageNum, coords[0], coords[1], coords[2], coords[3], category, caption, label, index),
                context.CancellationToken);

            return CliCommandHelpers.Finish(
                context,
                res,
                v => $"Added manual figure {v.FigureId} at index {v.Index} on page {v.PageNumber} ({v.Category}).",
                v => { context.SessionContext.ActiveDocumentId = v.DocumentId; context.SessionContext.ActiveFigureId = v.FigureId; });
        }
    }
}

public sealed class SetBboxCliCommand : CliCommand
{
    public SetBboxCliCommand()
        : base(new CommandDescriptor(
            Verb: "setbbox",
            Usage: "[<figID>] setbbox <x> <y> <width> <height>",
            Description: "Updates the bounding box (in PDF points) of a figure within its page dimensions.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Fig_2026... setbbox 60 110 440 280"]))
    {
    }

    public sealed class Handler(SetFigureBoundingBoxHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] setbbox <x> <y> <width> <height>", out var figId, out int err))
                return err;

            Span<double> coords = stackalloc double[4];
            if (context.PositionalArgs.Count != 4 || !CliCommandHelpers.TryParseDoubles(context.PositionalArgs, 0, coords))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] setbbox <x> <y> <width> <height>", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(new SetFigureBoundingBoxCommand(figId, coords[0], coords[1], coords[2], coords[3]), context.CancellationToken);
            return CliCommandHelpers.Finish(context, res, v => $"Updated bounding box of {v.FigureId} on page {v.PageNumber} to ({v.BoundingBox.X:F2}, {v.BoundingBox.Y:F2}, {v.BoundingBox.Width:F2}, {v.BoundingBox.Height:F2}).");
        }
    }
}

public sealed class SetCaptionCliCommand : CliCommand
{
    public SetCaptionCliCommand()
        : base(new CommandDescriptor(
            Verb: "setcaption",
            Usage: "[<figID>] setcaption <text>",
            Description: "Sets or updates the caption associated with a figure.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Fig_2026... setcaption \"Figure 1: Transformer Architecture\""]))
    {
    }

    public sealed class Handler(SetFigureCaptionHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] setcaption <text>", out var figId, out int err))
                return err;
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] setcaption <text>", context.GlobalOptions.Json);
            return CliCommandHelpers.Finish(context, await useCase.HandleAsync(new SetFigureCaptionCommand(figId, context.PositionalArgs[0]), context.CancellationToken), v => $"Updated caption for {v.FigureId}.");
        }
    }
}

public sealed class SetLabelCliCommand : CliCommand
{
    public SetLabelCliCommand()
        : base(new CommandDescriptor(
            Verb: "setlabel",
            Usage: "[<figID>] setlabel <label>",
            Description: "Sets or updates the short label (e.g., 'Figure 2.1') on a figure.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Fig_2026... setlabel \"Figure 2.1\""]))
    {
    }

    public sealed class Handler(SetFigureLabelHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] setlabel <label>", out var figId, out int err))
                return err;
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] setlabel <label>", context.GlobalOptions.Json);
            return CliCommandHelpers.Finish(context, await useCase.HandleAsync(new SetFigureLabelCommand(figId, context.PositionalArgs[0]), context.CancellationToken), v => $"Updated label of {v.FigureId} to \"{v.Label}\".");
        }
    }
}

public sealed class SetCategoryCliCommand : CliCommand
{
    public SetCategoryCliCommand()
        : base(new CommandDescriptor(
            Verb: "setcategory",
            Usage: "[<figID>] setcategory <figure|chart|diagram|table|formula>",
            Description: "Sets the classification category of a figure.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples: ["melomg Fig_2026... setcategory chart"]))
    {
    }

    public sealed class Handler(SetFigureCategoryHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] setcategory <figure|chart|diagram|table|formula>", out var figId, out int err))
                return err;
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] setcategory <figure|chart|diagram|table|formula>", context.GlobalOptions.Json);
            return CliCommandHelpers.Finish(context, await useCase.HandleAsync(new SetFigureCategoryCommand(figId, context.PositionalArgs[0]), context.CancellationToken), v => $"Updated category of {v.FigureId} to '{v.Category}'.");
        }
    }
}

public sealed class SetIndexCliCommand : CliCommand
{
    public SetIndexCliCommand()
        : base(new CommandDescriptor(
            Verb: "setindex",
            Usage: "[<figID>] setindex <number> <exchange|shift>",
            Description: "Reorders a figure using 1-based indexing in exchange or shift mode.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            DetailedNotes: "'exchange' swaps positions with the occupant at <number>. 'shift' shifts intermediate items up (n - 1) or down (n + 1). Rejects out-of-range indices with Index.OutOfIndexBounds.",
            Examples: ["melomg Fig_2026... setindex 1 shift", "melomg Fig_2026... setindex 3 exchange"]))
    {
    }

    public sealed class Handler(ReorderFigureHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] setindex <number> <exchange|shift>", out var figId, out int err))
                return err;

            if (context.PositionalArgs.Count != 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] setindex <number> <exchange|shift>", context.GlobalOptions.Json);

            if (!int.TryParse(context.PositionalArgs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetIndex))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidIndex", $"Target index '{context.PositionalArgs[0]}' must be an integer.", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryParseReorderMode(context.PositionalArgs[1], out var mode))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidMode", "Mode must be 'exchange' or 'shift'.", context.GlobalOptions.Json);

            return CliCommandHelpers.Finish(context, await useCase.HandleAsync(new ReorderFigureCommand(figId, targetIndex, mode), context.CancellationToken), v => $"Moved figure {v.FigureId} to index {v.NewIndex} ({v.Mode}).");
        }
    }
}

public sealed class MaskCliCommand : CliCommand
{
    public MaskCliCommand()
        : base(new CommandDescriptor(
            Verb: "mask",
            Usage: "[<figID>] mask <list|add|auto|remove|clear> [args...]",
            Description: "Lists, adds, auto-detects, removes, or clears label obfuscation masks on a figure.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            DetailedNotes: "Subcommands:\n  mask list\n  mask add <x> <y> <w> <h> [--label \"<text>\"] [--style <solid|blur|pixelate|whiteout>] [--color <#RRGGBB>]\n  mask auto [--match \"<term1,term2>\"] [--style <solid|blur|pixelate|whiteout>] [--color <#RRGGBB>]\n  mask remove <maskIndex>\n  mask clear",
            Examples:
            [
                "melomg Fig_2026... mask list",
                "melomg Fig_2026... mask auto --match \"Neutrophil,Macrophage\" --style solid",
                "melomg Fig_2026... mask add 120 240 85 18 --label \"Manual Region\" --style blur",
                "melomg Fig_2026... mask remove 1",
                "melomg Fig_2026... mask clear"
            ]))
    {
    }

    public sealed class Handler(
        ListFigureMasksHandler listHandler,
        AddFigureMaskHandler addHandler,
        AutoMaskFigureLabelsHandler autoHandler,
        RemoveFigureMaskHandler removeHandler,
        ClearFigureMasksHandler clearHandler) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveFigureId(context, allowPositional: false, "[<figID>] mask <list|add|auto|remove|clear> ...", out var figId, out int err))
                return err;

            var pos = context.PositionalArgs;
            if (pos.Count == 0)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask <list|add|auto|remove|clear> ...", context.GlobalOptions.Json);

            string sub = pos[0].ToLowerInvariant();
            switch (sub)
            {
                case "list":
                    if (pos.Count != 1)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask list", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(context, await listHandler.HandleAsync(new ListFigureMasksQuery(figId), context.CancellationToken), v =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Masks on {v.FigureId} (p{v.PageNumber}): {v.SavedMasks.Count} saved, {v.DetectedLabels.Count} detected label(s)");
                        foreach (var m in v.SavedMasks)
                            sb.AppendLine($"  [M{m.Index}] ({m.BoundingBoxPts.X:F1}, {m.BoundingBoxPts.Y:F1}, {m.BoundingBoxPts.Width:F1}, {m.BoundingBoxPts.Height:F1}) style={m.Style} {(m.LabelText is null ? "" : $"\"{m.LabelText}\"")}".TrimEnd());
                        foreach (var l in v.DetectedLabels)
                            sb.AppendLine($"  [L{l.Index}] \"{l.Text}\" @ ({l.BoundingBoxPts.X:F1}, {l.BoundingBoxPts.Y:F1}, {l.BoundingBoxPts.Width:F1}, {l.BoundingBoxPts.Height:F1})");
                        return sb.ToString().TrimEnd();
                    });
                case "add":
                {
                    Span<double> coords = stackalloc double[4];
                    if (pos.Count != 5 || !CliCommandHelpers.TryParseDoubles(pos, 1, coords))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask add <x> <y> <w> <h> [--label \"<text>\"] [--style <solid|blur|pixelate|whiteout>] [--color <#RRGGBB>]", context.GlobalOptions.Json);

                    context.NamedOptions.TryGetValue("label", out var labelText);
                    context.NamedOptions.TryGetValue("style", out var style);
                    context.NamedOptions.TryGetValue("color", out var color);

                    return CliCommandHelpers.Finish(
                        context,
                        await addHandler.HandleAsync(new AddFigureMaskCommand(figId, coords[0], coords[1], coords[2], coords[3], labelText, style, color), context.CancellationToken),
                        v => $"Added mask #{v.AddedMask.Index} ({v.AddedMask.Style}) to {v.FigureId} at ({v.AddedMask.BoundingBoxPts.X:F1}, {v.AddedMask.BoundingBoxPts.Y:F1}, {v.AddedMask.BoundingBoxPts.Width:F1}, {v.AddedMask.BoundingBoxPts.Height:F1}).");
                }
                case "auto":
                    if (pos.Count != 1)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask auto [--match \"<term1,term2>\"] [--style <solid|blur|pixelate|whiteout>] [--color <#RRGGBB>]", context.GlobalOptions.Json);
                    context.NamedOptions.TryGetValue("match", out var match);
                    context.NamedOptions.TryGetValue("style", out var autoStyle);
                    context.NamedOptions.TryGetValue("color", out var autoColor);
                    return CliCommandHelpers.Finish(
                        context,
                        await autoHandler.HandleAsync(new AutoMaskFigureLabelsCommand(figId, match, autoStyle, autoColor), context.CancellationToken),
                        v => $"Auto-masked {v.AddedCount} label(s) on {v.FigureId} (total saved masks: {v.TotalMasks}).");
                case "remove":
                    if (pos.Count != 2 || !int.TryParse(pos[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maskIndex))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask remove <maskIndex>", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(
                        context,
                        await removeHandler.HandleAsync(new RemoveFigureMaskCommand(figId, maskIndex), context.CancellationToken),
                        v => $"Removed mask #{v.RemovedMaskIndex} from {v.FigureId} (remaining: {v.RemainingMasks}).");
                case "clear":
                    if (pos.Count != 1)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<figID>] mask clear", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(
                        context,
                        await clearHandler.HandleAsync(new ClearFigureMasksCommand(figId), context.CancellationToken),
                        v => $"Cleared {v.ClearedCount} mask(s) from {v.FigureId}.");
                default:
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown mask subcommand '{sub}'. Use list, add, auto, remove, or clear.", context.GlobalOptions.Json);
            }
        }
    }
}
