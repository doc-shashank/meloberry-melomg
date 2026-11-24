using System.Globalization;
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
            Examples: ["melomg batch ops.json --json", "melomg batch - --json < ops.json"],
            AllowedOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
    {
    }

    public sealed class Handler(
        IngestPdfHandler ingestHandler,
        GetTargetHandler getHandler,
        ExtractFiguresHandler extractHandler,
        PatchTargetHandler patchHandler,
        AddManualFigureHandler addHandler,
        ListFigureMasksHandler listMasksHandler,
        AddFigureMaskHandler addMaskHandler,
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

                    var opRes = ReadOptionalString(elem, "op", stepPath);
                    if (!opRes.IsSuccess)
                        return Result.Failure(opRes.Error!).ToExitCode(context.Console, context.GlobalOptions.Json);
                    if (string.IsNullOrWhiteSpace(opRes.Value))
                        return CliResultExtensions.WriteValidationError(context.Console, "Json.ParseError", "Missing required 'op' string.", context.GlobalOptions.Json, path: $"{stepPath}.op");

                    string op = opRes.Value.Trim().ToLowerInvariant();
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
                    var pdfRes = ResolveInterpolatedString(elem, "pdf", stepPath, stepIds, prevId);
                    if (!pdfRes.IsSuccess) return pdfRes.Error!;

                    var outDirRes = ReadOptionalString(elem, "out", stepPath);
                    if (!outDirRes.IsSuccess) return outDirRes.Error!;
                    var projectRes = ReadOptionalBool(elem, "project", stepPath);
                    if (!projectRes.IsSuccess) return projectRes.Error!;
                    var refreshRes = ReadOptionalBool(elem, "refresh", stepPath);
                    if (!refreshRes.IsSuccess) return refreshRes.Error!;
                    var offlineRes = ReadOptionalBool(elem, "offline", stepPath);
                    if (!offlineRes.IsSuccess) return offlineRes.Error!;
                    var retainDbRes = ReadOptionalBool(elem, "retainDb", stepPath);
                    if (!retainDbRes.IsSuccess) return retainDbRes.Error!;
                    var quietRes = ReadOptionalBool(elem, "quiet", stepPath);
                    if (!quietRes.IsSuccess) return quietRes.Error!;
                    var verboseRes = ReadOptionalBool(elem, "verbose", stepPath);
                    if (!verboseRes.IsSuccess) return verboseRes.Error!;
                    var confRes = ReadOptionalDouble(elem, "confidence", stepPath);
                    if (!confRes.IsSuccess) return confRes.Error!;
                    var modelRes = ReadOptionalString(elem, "model", stepPath);
                    if (!modelRes.IsSuccess) return modelRes.Error!;
                    var titleRes = ReadOptionalString(elem, "title", stepPath);
                    if (!titleRes.IsSuccess) return titleRes.Error!;

                    var res = await ingestHandler.HandleAsync(
                        new IngestPdfCommand(
                            pdfRes.Value!,
                            outDirRes.Value ?? context.GlobalOptions.OutputDirectory,
                            projectRes.Value,
                            refreshRes.Value,
                            confRes.Value,
                            modelRes.Value ?? context.GlobalOptions.ModelPath,
                            titleRes.Value,
                            offlineRes.Value,
                            retainDbRes.Value || projectRes.Value,
                            quietRes.Value,
                            verboseRes.Value),
                        ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.Id) : res.Error!;
                }

                case "get":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;

                    var viewRes = ReadOptionalString(elem, "view", stepPath);
                    if (!viewRes.IsSuccess) return viewRes.Error!;

                    ViewMode view;
                    if (viewRes.Value is null)
                    {
                        view = ViewMode.Outline;
                    }
                    else
                    {
                        switch (viewRes.Value.Trim().ToLowerInvariant())
                        {
                            case "outline": view = ViewMode.Outline; break;
                            case "full": view = ViewMode.Full; break;
                            case "ids": view = ViewMode.Ids; break;
                            default:
                                return new Error("Cli.InvalidView", $"View '{viewRes.Value}' must be outline, full, or ids.", ErrorType.Validation, Path: $"{stepPath}.view");
                        }
                    }

                    var pageRes = ReadOptionalInt(elem, "page", stepPath);
                    if (!pageRes.IsSuccess) return pageRes.Error!;
                    var categoryRes = ReadOptionalString(elem, "category", stepPath);
                    if (!categoryRes.IsSuccess) return categoryRes.Error!;
                    var minConfRes = ReadOptionalDouble(elem, "minConf", stepPath);
                    if (!minConfRes.IsSuccess) return minConfRes.Error!;

                    var res = await getHandler.HandleAsync(
                        new GetTargetQuery(addrRes.Value!, view, pageRes.Value, categoryRes.Value, minConfRes.Value), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.Figure?.FigureId ?? res.Value.DocumentId) : res.Error!;
                }

                case "extract":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;

                    List<string>? selectors = null;
                    if (elem.TryGetProperty("selectors", out var selArr) && selArr.ValueKind != JsonValueKind.Null)
                    {
                        if (selArr.ValueKind != JsonValueKind.Array)
                            return new Error("Json.ParseError", "'selectors' must be an array of strings.", ErrorType.Validation, Path: $"{stepPath}.selectors");

                        selectors = [];
                        int selIdx = 0;
                        foreach (var item in selArr.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } s)
                                return new Error("Json.ParseError", "Every item in 'selectors' must be a string.", ErrorType.Validation, Path: $"{stepPath}.selectors[{selIdx}]");
                            selectors.Add(s);
                            selIdx++;
                        }
                    }

                    var outDirRes = ReadOptionalString(elem, "out", stepPath);
                    if (!outDirRes.IsSuccess) return outDirRes.Error!;
                    var formatRes = ReadOptionalString(elem, "format", stepPath);
                    if (!formatRes.IsSuccess) return formatRes.Error!;
                    var dpiRes = ReadOptionalInt(elem, "dpi", stepPath);
                    if (!dpiRes.IsSuccess) return dpiRes.Error!;
                    var paddingRes = ReadOptionalInt(elem, "padding", stepPath);
                    if (!paddingRes.IsSuccess) return paddingRes.Error!;
                    var qualityRes = ReadOptionalQuality(elem, "quality", stepPath);
                    if (!qualityRes.IsSuccess) return qualityRes.Error!;
                    var namingRes = ReadOptionalString(elem, "naming", stepPath);
                    if (!namingRes.IsSuccess) return namingRes.Error!;
                    var includeCaptionRes = ReadOptionalBool(elem, "includeCaption", stepPath);
                    if (!includeCaptionRes.IsSuccess) return includeCaptionRes.Error!;
                    var maskAllRes = ReadOptionalBool(elem, "maskAll", stepPath);
                    if (!maskAllRes.IsSuccess) return maskAllRes.Error!;
                    var maskMatchRes = ReadOptionalString(elem, "maskMatch", stepPath);
                    if (!maskMatchRes.IsSuccess) return maskMatchRes.Error!;
                    var maskStyleRes = ReadOptionalString(elem, "maskStyle", stepPath);
                    if (!maskStyleRes.IsSuccess) return maskStyleRes.Error!;
                    var maskColorRes = ReadOptionalString(elem, "maskColor", stepPath);
                    if (!maskColorRes.IsSuccess) return maskColorRes.Error!;
                    var noMasksRes = ReadOptionalBool(elem, "noMasks", stepPath);
                    if (!noMasksRes.IsSuccess) return noMasksRes.Error!;
                    var metaRes = ReadOptionalBool(elem, "meta", stepPath);
                    if (!metaRes.IsSuccess) return metaRes.Error!;
                    var keepOverlayRes = ReadOptionalBool(elem, "keepOverlay", stepPath);
                    if (!keepOverlayRes.IsSuccess) return keepOverlayRes.Error!;
                    var retainDbRes = ReadOptionalBool(elem, "retainDb", stepPath);
                    if (!retainDbRes.IsSuccess) return retainDbRes.Error!;
                    var offlineRes = ReadOptionalBool(elem, "offline", stepPath);
                    if (!offlineRes.IsSuccess) return offlineRes.Error!;
                    var manifestRes = ReadOptionalBool(elem, "manifest", stepPath);
                    if (!manifestRes.IsSuccess) return manifestRes.Error!;
                    var quietRes = ReadOptionalBool(elem, "quiet", stepPath);
                    if (!quietRes.IsSuccess) return quietRes.Error!;
                    var verboseRes = ReadOptionalBool(elem, "verbose", stepPath);
                    if (!verboseRes.IsSuccess) return verboseRes.Error!;

                    var res = await extractHandler.HandleAsync(new ExtractFiguresCommand(
                        addrRes.Value!, selectors, outDirRes.Value ?? context.GlobalOptions.OutputDirectory,
                        formatRes.Value ?? "png", dpiRes.Value, paddingRes.Value ?? ExtractionOptions.DefaultPaddingPx,
                        includeCaptionRes.Value, maskAllRes.Value, maskMatchRes.Value, noMasksRes.Value,
                        maskStyleRes.Value, maskColorRes.Value, qualityRes.Value,
                        namingRes.Value ?? ExtractionOptions.DefaultNamingMode, metaRes.Value, keepOverlayRes.Value,
                        retainDbRes.Value, offlineRes.Value, context.GlobalOptions.ModelPath,
                        EmitManifest: manifestRes.Value, Quiet: quietRes.Value, Verbose: verboseRes.Value), ct);

                    return res.IsSuccess ? Result<string?>.Success(res.Value.DocumentId) : res.Error!;
                }

                case "patch":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    if (!elem.TryGetProperty("set", out var setObj) || setObj.ValueKind != JsonValueKind.Object)
                        return new Error("Json.ParseError", "patch requires a 'set' object.", ErrorType.Validation, Path: $"{stepPath}.set");

                    string setPath = $"{stepPath}.set";
                    foreach (var p in setObj.EnumerateObject())
                    {
                        if (p.Name is not ("title" or "bbox" or "caption" or "label" or "num" or "category" or "move"))
                            return new Error("Json.ParseError", $"Unrecognized patch property '{p.Name}'.", ErrorType.Validation, Path: $"{setPath}.{p.Name}");
                    }

                    var titleRes = ReadOptionalString(setObj, "title", setPath);
                    if (!titleRes.IsSuccess) return titleRes.Error!;
                    var captionRes = ReadOptionalString(setObj, "caption", setPath);
                    if (!captionRes.IsSuccess) return captionRes.Error!;
                    var labelRes = ReadOptionalString(setObj, "label", setPath);
                    if (!labelRes.IsSuccess) return labelRes.Error!;
                    var numRes = ReadOptionalString(setObj, "num", setPath);
                    if (!numRes.IsSuccess) return numRes.Error!;
                    var categoryRes = ReadOptionalString(setObj, "category", setPath);
                    if (!categoryRes.IsSuccess) return categoryRes.Error!;
                    var moveRes = ReadOptionalString(setObj, "move", setPath);
                    if (!moveRes.IsSuccess) return moveRes.Error!;

                    BoundingBox? bbox = null;
                    if (setObj.TryGetProperty("bbox", out var bboxEl) && bboxEl.ValueKind != JsonValueKind.Null)
                    {
                        if (!TryParseBboxArray(bboxEl, out bbox))
                            return new Error("Json.ParseError", "'bbox' must be [x, y, w, h].", ErrorType.Validation, Path: $"{setPath}.bbox");
                    }

                    int? moveIdx = null;
                    ReorderMode? moveMode = null;
                    if (!string.IsNullOrWhiteSpace(moveRes.Value))
                    {
                        if (!CliCommandHelpers.TryParseMoveSpec(moveRes.Value, out int mIdx, out var mMode))
                            return new Error("Cli.InvalidMode", $"Invalid move '{moveRes.Value}'.", ErrorType.Validation, Path: $"{setPath}.move");
                        moveIdx = mIdx;
                        moveMode = mMode;
                    }

                    var res = await patchHandler.HandleAsync(
                        new PatchTargetCommand(addrRes.Value!, titleRes.Value, bbox, captionRes.Value, labelRes.Value, numRes.Value, categoryRes.Value, moveIdx, moveMode), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.TargetId) : res.Error!;
                }

                case "add":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    var pageRes = ReadOptionalInt(elem, "page", stepPath);
                    if (!pageRes.IsSuccess) return pageRes.Error!;
                    if (!pageRes.Value.HasValue)
                        return new Error("Json.ParseError", "add requires 'page'.", ErrorType.Validation, Path: $"{stepPath}.page");
                    if (!elem.TryGetProperty("bbox", out var bboxEl) || !TryParseBboxArray(bboxEl, out var bbox) || bbox is null)
                        return new Error("Json.ParseError", "add requires 'bbox': [x, y, w, h].", ErrorType.Validation, Path: $"{stepPath}.bbox");

                    var categoryRes = ReadOptionalString(elem, "category", stepPath);
                    if (!categoryRes.IsSuccess) return categoryRes.Error!;
                    var captionRes = ReadOptionalString(elem, "caption", stepPath);
                    if (!captionRes.IsSuccess) return captionRes.Error!;
                    var labelRes = ReadOptionalString(elem, "label", stepPath);
                    if (!labelRes.IsSuccess) return labelRes.Error!;
                    var numRes = ReadOptionalString(elem, "num", stepPath);
                    if (!numRes.IsSuccess) return numRes.Error!;
                    var indexRes = ReadOptionalInt(elem, "index", stepPath);
                    if (!indexRes.IsSuccess) return indexRes.Error!;

                    var res = await addHandler.HandleAsync(
                        new AddManualFigureCommand(addrRes.Value!, pageRes.Value.Value, bbox.X, bbox.Y, bbox.Width, bbox.Height, categoryRes.Value, captionRes.Value, labelRes.Value, numRes.Value, indexRes.Value), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.FigureId) : res.Error!;
                }

                case "mask":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    var actionRes = ReadOptionalString(elem, "action", stepPath);
                    if (!actionRes.IsSuccess) return actionRes.Error!;
                    if (string.IsNullOrWhiteSpace(actionRes.Value))
                        return new Error("Json.ParseError", "mask requires 'action' (ls|add|auto|rm|clear).", ErrorType.Validation, Path: $"{stepPath}.action");

                    switch (actionRes.Value.Trim().ToLowerInvariant())
                    {
                        case "ls" or "list":
                            var lsRes = await listMasksHandler.HandleAsync(new ListFigureMasksQuery(addrRes.Value!), ct);
                            return lsRes.IsSuccess ? Result<string?>.Success(lsRes.Value.FigureId) : lsRes.Error!;
                        case "add":
                        {
                            if (!elem.TryGetProperty("bbox", out var maskBboxEl) || !TryParseBboxArray(maskBboxEl, out var maskBbox) || maskBbox is null)
                                return new Error("Json.ParseError", "mask add requires 'bbox': [x, y, w, h].", ErrorType.Validation, Path: $"{stepPath}.bbox");
                            var labelRes = ReadOptionalString(elem, "label", stepPath);
                            if (!labelRes.IsSuccess) return labelRes.Error!;
                            var styleRes = ReadOptionalString(elem, "style", stepPath);
                            if (!styleRes.IsSuccess) return styleRes.Error!;
                            var colorRes = ReadOptionalString(elem, "color", stepPath);
                            if (!colorRes.IsSuccess) return colorRes.Error!;

                            var addMaskRes = await addMaskHandler.HandleAsync(
                                new AddFigureMaskCommand(addrRes.Value!, maskBbox.X, maskBbox.Y, maskBbox.Width, maskBbox.Height, labelRes.Value, styleRes.Value, colorRes.Value), ct);
                            return addMaskRes.IsSuccess ? Result<string?>.Success(addMaskRes.Value.FigureId) : addMaskRes.Error!;
                        }
                        case "auto":
                        {
                            var matchRes = ReadOptionalString(elem, "match", stepPath);
                            if (!matchRes.IsSuccess) return matchRes.Error!;
                            var styleRes = ReadOptionalString(elem, "style", stepPath);
                            if (!styleRes.IsSuccess) return styleRes.Error!;
                            var colorRes = ReadOptionalString(elem, "color", stepPath);
                            if (!colorRes.IsSuccess) return colorRes.Error!;

                            var autoRes = await autoMaskHandler.HandleAsync(
                                new AutoMaskFigureLabelsCommand(addrRes.Value!, matchRes.Value, styleRes.Value, colorRes.Value), ct);
                            return autoRes.IsSuccess ? Result<string?>.Success(autoRes.Value.FigureId) : autoRes.Error!;
                        }
                        case "rm" or "remove":
                        {
                            var mIdxRes = ReadOptionalInt(elem, "index", stepPath);
                            if (!mIdxRes.IsSuccess) return mIdxRes.Error!;
                            if (!mIdxRes.Value.HasValue)
                                return new Error("Json.ParseError", "mask rm requires 'index'.", ErrorType.Validation, Path: $"{stepPath}.index");
                            var rmRes = await removeMaskHandler.HandleAsync(new RemoveFigureMaskCommand(addrRes.Value!, mIdxRes.Value.Value), ct);
                            return rmRes.IsSuccess ? Result<string?>.Success(rmRes.Value.FigureId) : rmRes.Error!;
                        }
                        case "clear":
                            var clrRes = await clearMasksHandler.HandleAsync(new ClearFigureMasksCommand(addrRes.Value!), ct);
                            return clrRes.IsSuccess ? Result<string?>.Success(clrRes.Value.FigureId) : clrRes.Error!;
                        default:
                            return new Error("Cli.InvalidArgs", $"Unsupported batch mask action '{actionRes.Value}'.", ErrorType.Validation, Path: $"{stepPath}.action");
                    }
                }

                case "render":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    var pageRes = ReadOptionalInt(elem, "page", stepPath);
                    if (!pageRes.IsSuccess) return pageRes.Error!;
                    var allRes = ReadOptionalBool(elem, "all", stepPath);
                    if (!allRes.IsSuccess) return allRes.Error!;
                    var outDirRes = ReadOptionalString(elem, "out", stepPath);
                    if (!outDirRes.IsSuccess) return outDirRes.Error!;
                    var formatRes = ReadOptionalString(elem, "format", stepPath);
                    if (!formatRes.IsSuccess) return formatRes.Error!;
                    var dpiRes = ReadOptionalInt(elem, "dpi", stepPath);
                    if (!dpiRes.IsSuccess) return dpiRes.Error!;
                    var qualityRes = ReadOptionalQuality(elem, "quality", stepPath);
                    if (!qualityRes.IsSuccess) return qualityRes.Error!;
                    var overlayRes = ReadOptionalBool(elem, "overlay", stepPath);
                    if (!overlayRes.IsSuccess) return overlayRes.Error!;

                    var res = await renderHandler.HandleAsync(
                        new RenderPagesCommand(
                            addrRes.Value!, pageRes.Value, allRes.Value,
                            outDirRes.Value ?? context.GlobalOptions.OutputDirectory,
                            formatRes.Value ?? "png", dpiRes.Value, overlayRes.Value, qualityRes.Value), ct);
                    return res.IsSuccess ? Result<string?>.Success(res.Value.DocumentId) : res.Error!;
                }

                case "check":
                {
                    var addrRes = ResolveInterpolatedString(elem, "addr", stepPath, stepIds, prevId);
                    if (!addrRes.IsSuccess) return addrRes.Error!;
                    var quietRes = ReadOptionalBool(elem, "quiet", stepPath);
                    if (!quietRes.IsSuccess) return quietRes.Error!;
                    var verboseRes = ReadOptionalBool(elem, "verbose", stepPath);
                    if (!verboseRes.IsSuccess) return verboseRes.Error!;
                    var res = await checkHandler.HandleAsync(new LintDocumentQuery(addrRes.Value!, quietRes.Value, verboseRes.Value), ct);
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
            var rawRes = ReadOptionalString(elem, propName, stepPath);
            if (!rawRes.IsSuccess) return rawRes.Error!;
            if (string.IsNullOrWhiteSpace(rawRes.Value))
                return new Error("Json.ParseError", $"Missing required '{propName}'.", ErrorType.Validation, Path: $"{stepPath}.{propName}");

            string raw = rawRes.Value;
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
                "mask" => ["op", "addr", "action", "bbox", "label", "match", "style", "color", "index"],
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

        private static Result<string?> ReadOptionalString(JsonElement el, string name, string stepPath)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                return Result<string?>.Success(null);
            if (prop.ValueKind == JsonValueKind.String)
                return Result<string?>.Success(prop.GetString());
            return new Error("Json.ParseError", $"Property '{name}' must be a string.", ErrorType.Validation, Path: $"{stepPath}.{name}");
        }

        private static Result<bool> ReadOptionalBool(JsonElement el, string name, string stepPath)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                return Result<bool>.Success(false);
            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return Result<bool>.Success(prop.GetBoolean());
            return new Error("Json.ParseError", $"Property '{name}' must be a boolean.", ErrorType.Validation, Path: $"{stepPath}.{name}");
        }

        private static Result<int?> ReadOptionalInt(JsonElement el, string name, string stepPath)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                return Result<int?>.Success(null);
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
                return Result<int?>.Success(v);
            return new Error("Json.ParseError", $"Property '{name}' must be an integer.", ErrorType.Validation, Path: $"{stepPath}.{name}");
        }

        private static Result<double?> ReadOptionalDouble(JsonElement el, string name, string stepPath)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                return Result<double?>.Success(null);
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v))
                return Result<double?>.Success(v);
            return new Error("Json.ParseError", $"Property '{name}' must be a number.", ErrorType.Validation, Path: $"{stepPath}.{name}");
        }

        private static Result<string?> ReadOptionalQuality(JsonElement el, string name, string stepPath)
        {
            if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                return Result<string?>.Success(null);
            if (prop.ValueKind == JsonValueKind.String)
                return Result<string?>.Success(prop.GetString());
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int q))
                return Result<string?>.Success(q.ToString(CultureInfo.InvariantCulture));
            return new Error("Json.ParseError", $"Property '{name}' must be a string or integer.", ErrorType.Validation, Path: $"{stepPath}.{name}");
        }
    }

    [GeneratedRegex(@"\$(\d+|prev)\.id\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RefTokenRegex();
}
