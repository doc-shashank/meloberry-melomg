using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Cli.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Extraction;

namespace Melomg.Cli.Commands;

public sealed partial class BatchCliCommand : CliCommand
{
    private const string UsageText = "batch <file|->";

    public BatchCliCommand()
        : base(new CommandDescriptor(
            Verb: "batch",
            Usage: UsageText,
            Description: "Executes a JSON array of operations sequentially with $0.id / $prev.id reference interpolation.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Diagnostics,
            Examples: ["melomg batch ops.json --json", "melomg batch - --json < ops.json"]))
    {
    }

    public sealed class Handler(
        IngestPdfHandler ingestHandler,
        GetTargetHandler getHandler,
        ExtractFiguresHandler extractHandler,
        PatchTargetHandler patchHandler,
        AddManualFigureHandler addHandler,
        ListFigureMasksHandler listMasksHandler,
        AutoMaskFigureLabelsHandler autoMaskHandler,
        RemoveFigureMaskHandler removeMaskHandler,
        ClearFigureMasksHandler clearMasksHandler,
        RenderPagesHandler renderHandler,
        LintDocumentHandler checkHandler,
        DeleteTargetHandler rmHandler) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            var readRes = await CliCommandHelpers.ReadFileOrStdinAsync(context.PositionalArgs[0], context.Console, context.CancellationToken);
            if (!readRes.IsSuccess) return readRes.ToExitCode(context.Console, context.GlobalOptions.Json);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(readRes.Value);
            }
            catch (JsonException ex)
            {
                return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", ex.Message, context.GlobalOptions.Json);
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "Batch input must be a JSON array.", context.GlobalOptions.Json);

                var stepIds = new List<string?>();
                string? prevId = null;
                var results = new List<BatchOpResultItem>();
                int i = 0;

                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    string stepPath = $"batch[{i}]";
                    if (elem.ValueKind != JsonValueKind.Object)
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "Each batch entry must be an object.", context.GlobalOptions.Json, path: stepPath);

                    if (!TryGetString(elem, "op", out var op) || string.IsNullOrWhiteSpace(op))
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "Missing required 'op' string.", context.GlobalOptions.Json, path: $"{stepPath}.op");

                    op = op.Trim().ToLowerInvariant();
                    if (!ValidateOpProperties(elem, op, stepPath, out var propErr))
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", propErr!, context.GlobalOptions.Json, path: stepPath);

                    var stepRes = await ExecuteStepAsync(elem, op, stepPath, stepIds, prevId, context);
                    if (!stepRes.IsSuccess)
                    {
                        var err = stepRes.Error! with { Path = stepRes.Error.Path ?? stepPath };
                        return Result.Failure(err).ToExitCode(context.Console, context.GlobalOptions.Json);
                    }

                    string? createdOrTargetId = stepRes.Value;
                    stepIds.Add(createdOrTargetId);
                    if (!string.IsNullOrWhiteSpace(createdOrTargetId))
                        prevId = createdOrTargetId;

                    results.Add(new BatchOpResultItem(i, op, createdOrTargetId, true));
                    i++;
                }

                var response = new BatchResponse(results.Count, results);
                var sb = new StringBuilder();
                sb.AppendLine($"Batch completed {response.ExecutedCount} operation(s):");
                foreach (var r in response.Results)
                    sb.AppendLine($"  [{r.Index}] {r.Op} -> {r.Id ?? "ok"}");

                CliCommandHelpers.WriteOutput(context, response, sb.ToString().TrimEnd());
                return ExitCodes.Success;
            }
        }

        private async Task<Result<string?>> ExecuteStepAsync(
            JsonElement elem,
            string op,
            string stepPath,
            IReadOnlyList<string?> stepIds,
            string? prevId,
            CliInvocationContext context)
        {
            var ct = context.CancellationToken;
            switch (op)
            {
                case "ingest":
                {
                    if (!TryGetString(elem, "pdf", out var pdf) || string.IsNullOrWhiteSpace(pdf))
                        return new Error("Json.ParseError", "ingest requires 'pdf'.", ErrorType.Validation, Path: $"{stepPath}.pdf");

                    TryGetString(elem, "out", out var outDir);
                    TryGetBool(elem, "project", out bool project);
                    TryGetBool(elem, "refresh", out bool refresh);
                    TryGetBool(elem, "offline", out bool offline);
                    TryGetBool(elem, "retainDb", out bool retainDb);
                    TryGetBool(elem, "quiet", out bool quiet);
                    TryGetBool(elem, "verbose", out bool verbose);
                    TryGetDouble(elem, "confidence", out var conf);
                    TryGetString(elem, "model", out var model);
                    TryGetString(elem, "title", out var title);

                    var res = await ingestHandler.HandleAsync(
                        new IngestPdfCommand(pdf, outDir, project, refresh, conf, model ?? context.GlobalOptions.ModelPath, title, offline, retainDb || project, quiet, verbose), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.Id) : res.Error!;
                }

                case "get":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    TryGetString(elem, "view", out var viewStr);
                    ViewMode view = (viewStr ?? "outline").Trim().ToLowerInvariant() switch
                    {
                        "full" => ViewMode.Full,
                        "ids" => ViewMode.Ids,
                        _ => ViewMode.Outline
                    };
                    TryGetInt(elem, "page", out var page);
                    TryGetString(elem, "category", out var category);
                    TryGetDouble(elem, "minConf", out var minConf);

                    var res = await getHandler.HandleAsync(new GetTargetQuery(addrRes.Value!, view, page, category, minConf), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.Figure?.FigureId ?? res.Value.DocumentId) : res.Error!;
                }

                case "extract":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;

                    List<string>? selectors = null;
                    if (elem.TryGetProperty("selectors", out var selArr) && selArr.ValueKind == JsonValueKind.Array)
                    {
                        selectors = [];
                        foreach (var item in selArr.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                                selectors.Add(s);
                        }
                    }

                    TryGetString(elem, "out", out var outDir);
                    TryGetString(elem, "format", out var format);
                    TryGetInt(elem, "dpi", out var dpi);
                    TryGetInt(elem, "padding", out var padding);
                    TryGetString(elem, "quality", out var quality);
                    TryGetString(elem, "naming", out var naming);
                    TryGetBool(elem, "includeCaption", out bool includeCaption);
                    TryGetBool(elem, "maskAll", out bool maskAll);
                    TryGetString(elem, "maskMatch", out var maskMatch);
                    TryGetString(elem, "maskStyle", out var maskStyle);
                    TryGetString(elem, "maskColor", out var maskColor);
                    TryGetBool(elem, "noMasks", out bool noMasks);
                    TryGetBool(elem, "meta", out bool meta);
                    TryGetBool(elem, "keepOverlay", out bool keepOverlay);
                    TryGetBool(elem, "retainDb", out bool retainDb);
                    TryGetBool(elem, "offline", out bool offline);
                    TryGetBool(elem, "manifest", out bool manifest);
                    TryGetBool(elem, "quiet", out bool quiet);
                    TryGetBool(elem, "verbose", out bool verbose);

                    var res = await extractHandler.HandleAsync(new ExtractFiguresCommand(
                        addrRes.Value!, selectors, outDir ?? context.GlobalOptions.OutputDirectory,
                        format ?? "png", dpi, padding ?? ExtractionOptions.DefaultPaddingPx,
                        includeCaption, maskAll, maskMatch, noMasks, maskStyle, maskColor, quality,
                        naming ?? ExtractionOptions.DefaultNamingMode, meta, keepOverlay, retainDb, offline,
                        context.GlobalOptions.ModelPath, EmitManifest: manifest, Quiet: quiet, Verbose: verbose), ct);

                    return res.IsSuccess ? Result<string?>.Success(res.Value.DocumentId) : res.Error!;
                }

                case "patch":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    if (!elem.TryGetProperty("set", out var setObj) || setObj.ValueKind != JsonValueKind.Object)
                        return new Error("Json.ParseError", "patch requires a 'set' object.", ErrorType.Validation, Path: $"{stepPath}.set");

                    TryGetString(setObj, "title", out var title);
                    TryGetString(setObj, "caption", out var caption);
                    TryGetString(setObj, "label", out var label);
                    TryGetString(setObj, "num", out var num);
                    TryGetString(setObj, "category", out var category);
                    TryGetString(setObj, "move", out var moveStr);
                    BoundingBox? bbox = null;
                    if (setObj.TryGetProperty("bbox", out var bboxEl))
                    {
                        if (!TryParseBboxArray(bboxEl, out bbox))
                            return new Error("Json.ParseError", "'bbox' must be [x, y, w, h].", ErrorType.Validation, Path: $"{stepPath}.set.bbox");
                    }

                    int? moveIdx = null;
                    ReorderMode? moveMode = null;
                    if (!string.IsNullOrWhiteSpace(moveStr))
                    {
                        if (!CliCommandHelpers.TryParseMoveSpec(moveStr, out int mIdx, out var mMode))
                            return new Error("Cli.InvalidMode", $"Invalid move '{moveStr}'.", ErrorType.Validation, Path: $"{stepPath}.set.move");
                        moveIdx = mIdx;
                        moveMode = mMode;
                    }

                    var res = await patchHandler.HandleAsync(
                        new PatchTargetCommand(addrRes.Value!, title, bbox, caption, label, num, category, moveIdx, moveMode), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.TargetId) : res.Error!;
                }

                case "add":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    if (!TryGetInt(elem, "page", out var page) || !page.HasValue)
                        return new Error("Json.ParseError", "add requires 'page'.", ErrorType.Validation, Path: $"{stepPath}.page");
                    if (!elem.TryGetProperty("bbox", out var bboxEl) || !TryParseBboxArray(bboxEl, out var bbox) || bbox is null)
                        return new Error("Json.ParseError", "add requires 'bbox': [x, y, w, h].", ErrorType.Validation, Path: $"{stepPath}.bbox");

                    TryGetString(elem, "category", out var category);
                    TryGetString(elem, "caption", out var caption);
                    TryGetString(elem, "label", out var label);
                    TryGetString(elem, "num", out var num);
                    TryGetInt(elem, "index", out var index);

                    var res = await addHandler.HandleAsync(
                        new AddManualFigureCommand(addrRes.Value!, page.Value, bbox.X, bbox.Y, bbox.Width, bbox.Height, category, caption, label, num, index), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.FigureId) : res.Error!;
                }

                case "mask":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    if (!TryGetString(elem, "action", out var action) || string.IsNullOrWhiteSpace(action))
                        return new Error("Json.ParseError", "mask requires 'action' (ls|auto|rm|clear).", ErrorType.Validation, Path: $"{stepPath}.action");

                    switch (action.Trim().ToLowerInvariant())
                    {
                        case "ls" or "list":
                            var lsRes = await listMasksHandler.HandleAsync(new ListFigureMasksQuery(addrRes.Value!), ct);
                            return lsRes.IsSuccess ? Result<string?>.Success(lsRes.Value.FigureId) : lsRes.Error!;
                        case "auto":
                            TryGetString(elem, "match", out var match);
                            TryGetString(elem, "style", out var style);
                            TryGetString(elem, "color", out var color);
                            var autoRes = await autoMaskHandler.HandleAsync(new AutoMaskFigureLabelsCommand(addrRes.Value!, match, style, color), ct);
                            return autoRes.IsSuccess ? Result<string?>.Success(autoRes.Value.FigureId) : autoRes.Error!;
                        case "rm" or "remove":
                            if (!TryGetInt(elem, "index", out var mIdx) || !mIdx.HasValue)
                                return new Error("Json.ParseError", "mask rm requires 'index'.", ErrorType.Validation, Path: $"{stepPath}.index");
                            var rmRes = await removeMaskHandler.HandleAsync(new RemoveFigureMaskCommand(addrRes.Value!, mIdx.Value), ct);
                            return rmRes.IsSuccess ? Result<string?>.Success(rmRes.Value.FigureId) : rmRes.Error!;
                        case "clear":
                            var clrRes = await clearMasksHandler.HandleAsync(new ClearFigureMasksCommand(addrRes.Value!), ct);
                            return clrRes.IsSuccess ? Result<string?>.Success(clrRes.Value.FigureId) : clrRes.Error!;
                        default:
                            return new Error("Cli.InvalidArgs", $"Unsupported batch mask action '{action}'.", ErrorType.Validation, Path: $"{stepPath}.action");
                    }
                }

                case "render":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    TryGetInt(elem, "page", out var page);
                    TryGetBool(elem, "all", out bool all);
                    TryGetString(elem, "out", out var outDir);
                    TryGetString(elem, "format", out var format);
                    TryGetInt(elem, "dpi", out var dpi);
                    TryGetString(elem, "quality", out var quality);
                    TryGetBool(elem, "overlay", out bool overlay);

                    var res = await renderHandler.HandleAsync(
                        new RenderPagesCommand(addrRes.Value!, page, all, outDir, format ?? "png", dpi, overlay, quality), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.DocumentId) : res.Error!;
                }

                case "check":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    TryGetBool(elem, "quiet", out bool quiet);
                    TryGetBool(elem, "verbose", out bool verbose);
                    var res = await checkHandler.HandleAsync(new LintDocumentQuery(addrRes.Value!, quiet, verbose), ct);
                    if (!res.IsSuccess) return res.Error!;
                    if (!res.Value.IsClean)
                        return new Error("Lint.Failed", $"Check failed with {res.Value.ErrorCount} error(s).", ErrorType.Validation, Path: stepPath);
                    return Result<string?>.Success(res.Value.TargetId);
                }

                case "rm":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    var res = await rmHandler.HandleAsync(new DeleteTargetCommand(addrRes.Value!), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.DeletedId) : res.Error!;
                }

                default:
                    return new Error("Cli.InvalidArgs", $"Unsupported batch op '{op}'.", ErrorType.Validation, Path: $"{stepPath}.op");
            }
        }

        private static Result<string?> ResolveInterpolatedString(
            JsonElement elem,
            string propName,
            string stepPath,
            IReadOnlyList<string?> stepIds,
            string? prevId)
        {
            if (!TryGetString(elem, propName, out var raw) || string.IsNullOrWhiteSpace(raw))
                return new Error("Json.ParseError", $"Missing required '{propName}'.", ErrorType.Validation, Path: $"{stepPath}.{propName}");

            string interpolated = RefTokenRegex().Replace(raw, m =>
            {
                string key = m.Groups[1].Value;
                if (string.Equals(key, "prev", StringComparison.OrdinalIgnoreCase))
                    return prevId ?? m.Value;

                if (int.TryParse(key, out int idx) && idx >= 0 && idx < stepIds.Count && !string.IsNullOrWhiteSpace(stepIds[idx]))
                    return stepIds[idx]!;

                return m.Value;
            });

            if (RefTokenRegex().IsMatch(interpolated))
                return new Error("Batch.UnresolvedRef", $"Unresolved reference in '{raw}'.", ErrorType.Validation, Path: $"{stepPath}.{propName}");

            return Result<string?>.Success(interpolated);
        }

        private static bool ValidateOpProperties(JsonElement elem, string op, string stepPath, out string? error)
        {
            error = null;
            HashSet<string> allowed = op switch
            {
                "ingest" => ["op", "pdf", "out", "project", "refresh", "offline", "retainDb", "confidence", "model", "title", "quiet", "verbose"],
                "get" => ["op", "addr", "view", "page", "category", "minConf"],
                "extract" => ["op", "addr", "selectors", "out", "format", "dpi", "padding", "quality", "naming", "includeCaption", "maskAll", "maskMatch", "maskStyle", "maskColor", "noMasks", "meta", "keepOverlay", "retainDb", "offline", "manifest", "quiet", "verbose"],
                "patch" => ["op", "addr", "set"],
                "add" => ["op", "addr", "page", "bbox", "category", "caption", "label", "num", "index"],
                "mask" => ["op", "addr", "action", "match", "style", "color", "index"],
                "render" => ["op", "addr", "page", "all", "out", "format", "dpi", "quality", "overlay"],
                "check" => ["op", "addr", "quiet", "verbose"],
                "rm" => ["op", "addr"],
                _ => ["op"]
            };

            if (op is not ("ingest" or "get" or "extract" or "patch" or "add" or "mask" or "render" or "check" or "rm"))
            {
                error = $"Unknown batch op '{op}'.";
                return false;
            }

            foreach (var p in elem.EnumerateObject())
            {
                if (!allowed.Contains(p.Name))
                {
                    error = $"Unrecognized property '{p.Name}' for op '{op}' at {stepPath}.";
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseBboxArray(JsonElement el, out BoundingBox? bbox)
        {
            bbox = null;
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 4)
                return false;
            var arr = new double[4];
            int idx = 0;
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out arr[idx++]))
                    return false;
            }
            bbox = new BoundingBox(arr[0], arr[1], arr[2], arr[3]);
            return true;
        }

        private static bool TryGetString(JsonElement el, string name, out string? value)
        {
            value = null;
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return true;
            }
            return false;
        }

        private static bool TryGetBool(JsonElement el, string name, out bool value)
        {
            value = false;
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = prop.GetBoolean();
                return true;
            }
            return false;
        }

        private static bool TryGetInt(JsonElement el, string name, out int? value)
        {
            value = null;
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
            {
                value = v;
                return true;
            }
            return false;
        }

        private static bool TryGetDouble(JsonElement el, string name, out double? value)
        {
            value = null;
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v))
            {
                value = v;
                return true;
            }
            return false;
        }
    }

    [GeneratedRegex(@"\$(\d+|prev)\.id\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RefTokenRegex();
}
