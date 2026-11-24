using System.Globalization;
using System.Text;
using System.Text.Json;
using Melomg.Application.UseCases.Figures;
using Melomg.Cli.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;

namespace Melomg.Cli.Commands;

public sealed class PatchCliCommand : CliCommand
{
    private const string UsageText = "patch <addr> [file|-] [--set k=v]... [--move <idx>:<shift|exchange>]";

    public PatchCliCommand()
        : base(new CommandDescriptor(
            Verb: "patch",
            Usage: UsageText,
            Description: "Updates document title or figure bbox, caption, label, number, category, or order.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples:
            [
                "melomg patch Doc_2026... --set title=\"Cell Biology\"",
                "melomg patch Doc_2026...#2.1 --set category=diagram --set label=\"Fig. 2.1\"",
                "melomg patch Doc_2026...#f2 --move 1:shift"
            ],
            AllowedOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "set", "move" }))
    {
    }

    public sealed class Handler(PatchTargetHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1 || context.PositionalArgs.Count > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string address = context.PositionalArgs[0];
            string? title = null;
            BoundingBox? bbox = null;
            string? caption = null;
            string? label = null;
            string? num = null;
            string? category = null;
            int? moveIndex = null;
            ReorderMode? moveMode = null;

            if (context.PositionalArgs.Count == 2)
            {
                var readRes = await CliCommandHelpers.ReadFileOrStdinAsync(context.PositionalArgs[1], context.Console, context.CancellationToken);
                if (!readRes.IsSuccess) return readRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                if (!TryParsePatchJson(readRes.Value, out var jsonInput, out var jsonErr))
                    return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", jsonErr!, context.GlobalOptions.Json);

                title = jsonInput!.Title;
                caption = jsonInput.Caption;
                label = jsonInput.Label;
                num = jsonInput.Num;
                category = jsonInput.Category;

                if (jsonInput.Bbox is not null)
                {
                    if (jsonInput.Bbox.Length != 4)
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "'bbox' must be a 4-element array [x, y, width, height].", context.GlobalOptions.Json, path: "bbox");
                    bbox = new BoundingBox(jsonInput.Bbox[0], jsonInput.Bbox[1], jsonInput.Bbox[2], jsonInput.Bbox[3]);
                }

                if (!string.IsNullOrWhiteSpace(jsonInput.Move))
                {
                    if (!CliCommandHelpers.TryParseMoveSpec(jsonInput.Move, out int mIdx, out var mMode))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidMode", $"Invalid move spec '{jsonInput.Move}'. Use <index>:<shift|exchange>.", context.GlobalOptions.Json, path: "move");
                    moveIndex = mIdx;
                    moveMode = mMode;
                }
            }

            if (context.MultiValueOptions.TryGetValue("set", out var setPairs))
            {
                foreach (var pair in setPairs)
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Invalid --set '{pair}'. Expected key=value.", context.GlobalOptions.Json);

                    string key = pair[..eq].Trim().ToLowerInvariant();
                    string val = pair[(eq + 1)..];
                    switch (key)
                    {
                        case "title": title = val; break;
                        case "caption": caption = val; break;
                        case "label": label = val; break;
                        case "num": case "number": num = val; break;
                        case "category": category = val; break;
                        case "bbox":
                            if (!TryParseBboxString(val, out var parsedBbox))
                                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", $"Invalid bbox '{val}'. Expected x,y,w,h.", context.GlobalOptions.Json);
                            bbox = parsedBbox;
                            break;
                        default:
                            return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown patch key '{key}'. Allowed: title, bbox, caption, label, num, category.", context.GlobalOptions.Json);
                    }
                }
            }

            if (context.NamedOptions.TryGetValue("move", out var moveRaw) && !string.IsNullOrWhiteSpace(moveRaw))
            {
                if (!CliCommandHelpers.TryParseMoveSpec(moveRaw, out int mIdx, out var mMode))
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidMode", $"Invalid --move '{moveRaw}'. Use <index>:<shift|exchange>.", context.GlobalOptions.Json);
                moveIndex = mIdx;
                moveMode = mMode;
            }

            var res = await useCase.HandleAsync(
                new PatchTargetCommand(address, title, bbox, caption, label, num, category, moveIndex, moveMode),
                context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v =>
                v.EntityKind == "document"
                    ? $"Patched document {v.DocumentId} title=\"{v.Title}\"."
                    : $"Patched figure {v.TargetId} (#f{v.Index}, num={v.FigureNumber ?? "-"}, {v.Category}).");
        }

        internal static bool TryParseBboxString(string raw, out BoundingBox? bbox)
        {
            bbox = null;
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4) return false;
            Span<double> vals = stackalloc double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i]))
                    return false;
            bbox = new BoundingBox(vals[0], vals[1], vals[2], vals[3]);
            return true;
        }

        private static bool TryParsePatchJson(string json, out PatchJsonInput? input, out string? error)
        {
            input = null;
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Patch JSON must be an object.";
                    return false;
                }
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name is not ("title" or "bbox" or "caption" or "label" or "num" or "category" or "move"))
                    {
                        error = $"Unrecognized patch property '{prop.Name}'.";
                        return false;
                    }
                }
                input = JsonSerializer.Deserialize(json, MelomgCliJsonContext.Default.PatchJsonInput);
                if (input is null) { error = "Patch JSON was empty."; return false; }
                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

public sealed class AddCliCommand : CliCommand
{
    private const string UsageText = "add <docId> <page> <x> <y> <w> <h> [--category <figure|diagram|chart>] [--num <num>] [--caption <text>] [--label <text>] [--index <n>]  OR  add <docId> <file|->";

    public AddCliCommand()
        : base(new CommandDescriptor(
            Verb: "add",
            Usage: UsageText,
            Description: "Manually registers an image, diagram, or chart bounding box on a PDF page.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples:
            [
                "melomg add Doc_2026... 1 72 120 420 260 --category diagram --num 2.5 --caption \"Fig. 2.5: Pipeline\"",
                "melomg add Doc_2026... -"
            ],
            AllowedOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "category", "num", "caption", "label", "index"
            }))
    {
    }

    public sealed class Handler(AddManualFigureHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            var pos = context.PositionalArgs;
            if (pos.Count < 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string docId = pos[0];
            if (pos.Count == 2)
            {
                var readRes = await CliCommandHelpers.ReadFileOrStdinAsync(pos[1], context.Console, context.CancellationToken);
                if (!readRes.IsSuccess) return readRes.ToExitCode(context.Console, context.GlobalOptions.Json);

                AddFigureJsonInput? jsonInput;
                try
                {
                    using var doc = JsonDocument.Parse(readRes.Value);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "Add JSON must be an object.", context.GlobalOptions.Json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name is not ("page" or "x" or "y" or "width" or "height" or "bbox" or "category" or "caption" or "label" or "num" or "index"))
                            return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", $"Unrecognized property '{prop.Name}'.", context.GlobalOptions.Json, path: prop.Name);
                    }
                    jsonInput = JsonSerializer.Deserialize(readRes.Value, MelomgCliJsonContext.Default.AddFigureJsonInput);
                }
                catch (JsonException ex)
                {
                    return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", ex.Message, context.GlobalOptions.Json);
                }

                if (jsonInput?.Page is null)
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "'page' is required in add JSON.", context.GlobalOptions.Json, path: "page");

                double x, y, w, h;
                if (jsonInput.Bbox is { Length: 4 })
                {
                    x = jsonInput.Bbox[0]; y = jsonInput.Bbox[1]; w = jsonInput.Bbox[2]; h = jsonInput.Bbox[3];
                }
                else if (jsonInput.X.HasValue && jsonInput.Y.HasValue && jsonInput.Width.HasValue && jsonInput.Height.HasValue)
                {
                    x = jsonInput.X.Value; y = jsonInput.Y.Value; w = jsonInput.Width.Value; h = jsonInput.Height.Value;
                }
                else
                {
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Provide 'bbox': [x,y,w,h] or 'x','y','width','height'.", context.GlobalOptions.Json);
                }

                var jsonRes = await useCase.HandleAsync(
                    new AddManualFigureCommand(docId, jsonInput.Page.Value, x, y, w, h, jsonInput.Category, jsonInput.Caption, jsonInput.Label, jsonInput.Num, jsonInput.Index),
                    context.CancellationToken);

                return CliCommandHelpers.Finish(context, jsonRes, v => $"Added {v.FigureId} (#f{v.Index}, p{v.PageNumber}, {v.Category}).");
            }

            if (pos.Count < 6 || pos.Count > 7)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            Span<double> coords = stackalloc double[4];
            if (!int.TryParse(pos[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNum) ||
                !CliCommandHelpers.TryParseDoubles(pos, 2, coords))
            {
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidNumber", "Page and bounding box coordinates (x, y, width, height) must be numeric.", context.GlobalOptions.Json);
            }

            if (!CliCommandHelpers.TryGetIntOption(context, "index", out var optIdx, out int idxOptErr))
                return idxOptErr;

            if (optIdx.HasValue && pos.Count == 7)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Specify either --index or a 7th positional index, not both.", context.GlobalOptions.Json);

            int? index = optIdx;
            if (!index.HasValue && pos.Count == 7 &&
                !CliCommandHelpers.TryParseOptionalPositiveIndex(pos[6], out index, out int idxErr, context.Console, context.GlobalOptions.Json))
            {
                return idxErr;
            }

            context.NamedOptions.TryGetValue("category", out var category);
            context.NamedOptions.TryGetValue("caption", out var caption);
            context.NamedOptions.TryGetValue("label", out var label);
            context.NamedOptions.TryGetValue("num", out var num);

            var res = await useCase.HandleAsync(
                new AddManualFigureCommand(docId, pageNum, coords[0], coords[1], coords[2], coords[3], category, caption, label, num, index),
                context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v => $"Added {v.FigureId} (#f{v.Index}, p{v.PageNumber}, {v.Category}).");
        }
    }
}

public sealed class MaskCliCommand : CliCommand
{
    private const string UsageText = "mask <figAddr> <ls|add|auto|rm|clear> [args...]";

    public MaskCliCommand()
        : base(new CommandDescriptor(
            Verb: "mask",
            Usage: UsageText,
            Description: "Lists, adds, auto-detects, removes, or clears label obfuscation masks on a figure.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Figures,
            Examples:
            [
                "melomg mask Doc_2026...#2.1 ls",
                "melomg mask Doc_2026...#2.1 auto --match \"Neutrophil,Macrophage\" --style solid",
                "melomg mask Fig_2026... add 120 240 85 18 --label \"Label\" --style blur",
                "melomg mask Doc_2026...#2.1 rm 1",
                "melomg mask Doc_2026...#2.1 clear"
            ],
            AllowedOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "label", "style", "color", "match"
            }))
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
            var pos = context.PositionalArgs;
            if (pos.Count < 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string figAddr = pos[0];
            string sub = pos[1].ToLowerInvariant();
            switch (sub)
            {
                case "ls" or "list":
                    if (pos.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mask <figAddr> ls", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(context, await listHandler.HandleAsync(new ListFigureMasksQuery(figAddr), context.CancellationToken), v =>
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Masks on {v.FigureId} (p{v.PageNumber}): {v.SavedMasks.Count} saved, {v.DetectedLabels.Count} detected label(s)");
                        foreach (var m in v.SavedMasks)
                            sb.AppendLine($"  [M{m.Index}] ({m.BoundingBoxPts.X:F1},{m.BoundingBoxPts.Y:F1},{m.BoundingBoxPts.Width:F1},{m.BoundingBoxPts.Height:F1}) {m.Style} {m.LabelText}".TrimEnd());
                        foreach (var l in v.DetectedLabels)
                            sb.AppendLine($"  [L{l.Index}] \"{l.Text}\" @ ({l.BoundingBoxPts.X:F1},{l.BoundingBoxPts.Y:F1},{l.BoundingBoxPts.Width:F1},{l.BoundingBoxPts.Height:F1})");
                        return sb.ToString().TrimEnd();
                    });
                case "add":
                {
                    Span<double> coords = stackalloc double[4];
                    if (pos.Count != 6 || !CliCommandHelpers.TryParseDoubles(pos, 2, coords))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mask <figAddr> add <x> <y> <w> <h> [--label text] [--style solid|blur|pixelate|whiteout] [--color #RRGGBB]", context.GlobalOptions.Json);

                    context.NamedOptions.TryGetValue("label", out var labelText);
                    context.NamedOptions.TryGetValue("style", out var style);
                    context.NamedOptions.TryGetValue("color", out var color);

                    return CliCommandHelpers.Finish(
                        context,
                        await addHandler.HandleAsync(new AddFigureMaskCommand(figAddr, coords[0], coords[1], coords[2], coords[3], labelText, style, color), context.CancellationToken),
                        v => $"Added mask #{v.AddedMask.Index} ({v.AddedMask.Style}) to {v.FigureId}.");
                }
                case "auto":
                    if (pos.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mask <figAddr> auto [--match p1,p2] [--style ...] [--color ...]", context.GlobalOptions.Json);
                    context.NamedOptions.TryGetValue("match", out var match);
                    context.NamedOptions.TryGetValue("style", out var autoStyle);
                    context.NamedOptions.TryGetValue("color", out var autoColor);
                    return CliCommandHelpers.Finish(
                        context,
                        await autoHandler.HandleAsync(new AutoMaskFigureLabelsCommand(figAddr, match, autoStyle, autoColor), context.CancellationToken),
                        v => $"Auto-masked {v.AddedCount} label(s) on {v.FigureId} (total: {v.TotalMasks}).");
                case "rm" or "remove":
                    if (pos.Count != 3 || !int.TryParse(pos[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maskIndex))
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mask <figAddr> rm <maskIndex>", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(
                        context,
                        await removeHandler.HandleAsync(new RemoveFigureMaskCommand(figAddr, maskIndex), context.CancellationToken),
                        v => $"Removed mask #{v.RemovedMaskIndex} from {v.FigureId} (remaining: {v.RemainingMasks}).");
                case "clear":
                    if (pos.Count != 2)
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: mask <figAddr> clear", context.GlobalOptions.Json);
                    return CliCommandHelpers.Finish(
                        context,
                        await clearHandler.HandleAsync(new ClearFigureMasksCommand(figAddr), context.CancellationToken),
                        v => $"Cleared {v.ClearedCount} mask(s) from {v.FigureId}.");
                default:
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown mask subcommand '{sub}'. Use ls, add, auto, rm, or clear.", context.GlobalOptions.Json);
            }
        }
    }
}
