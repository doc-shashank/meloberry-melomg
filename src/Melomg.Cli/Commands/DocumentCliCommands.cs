using System.Text;
using Melomg.Application.UseCases.Documents;
using Melomg.Cli.Abstractions;

namespace Melomg.Cli.Commands;

public sealed class IngestCliCommand : CliCommand
{
    private const string UsageText = "ingest <pdfPath|projectDir|docId> [-o dir] [--project] [--refresh] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline] [--retain-db] [--quiet] [--verbose]";

    public IngestCliCommand()
        : base(new CommandDescriptor(
            Verb: "ingest",
            Usage: UsageText,
            Description: "Ingests a PDF (or opens/refreshes a project) and returns detected vocabulary, anomaly checks, and figure outline.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples:
            [
                "melomg ingest ./paper.pdf",
                "melomg ingest ./paper.pdf --project -o ./study",
                "melomg ingest Doc_2026... --refresh --confidence 0.45",
                "melomg ingest ./paper.pdf --quiet --json"
            ]))
    {
    }

    public sealed class Handler(IngestPdfHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var confidence, out int numErr))
                return numErr;

            context.NamedOptions.TryGetValue("title", out var title);
            bool project = context.NamedOptions.ContainsKey("project");
            bool refresh = context.NamedOptions.ContainsKey("refresh");
            bool offline = context.NamedOptions.ContainsKey("offline");
            bool retainDb = context.NamedOptions.ContainsKey("retain-db") || project;
            bool quiet = context.NamedOptions.ContainsKey("quiet");
            bool verbose = context.NamedOptions.ContainsKey("verbose");

            var res = await useCase.HandleAsync(
                new IngestPdfCommand(
                    context.PositionalArgs[0],
                    context.GlobalOptions.OutputDirectory,
                    project,
                    refresh,
                    confidence,
                    context.GlobalOptions.ModelPath,
                    title,
                    offline,
                    retainDb,
                    quiet,
                    verbose),
                context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v =>
            {
                var sb = new StringBuilder();
                string vocab = string.IsNullOrWhiteSpace(v.Vocab.Primary) ? "none" : $"{v.Vocab.Primary} ({v.Vocab.Style})";
                sb.AppendLine($"{v.Id}  \"{v.Title}\"  pages={v.Pages}  figures={v.FigureCount}  vocab={vocab}  engine={v.InferenceEngine}");
                if (v.Figures is not null)
                {
                    foreach (var f in v.Figures)
                    {
                        string numTag = string.IsNullOrWhiteSpace(f.Num) ? $"#f{f.I}" : $"#{f.Num}";
                        string cap = string.IsNullOrWhiteSpace(f.Caption) ? "" : $"  \"{f.Caption}\"";
                        sb.AppendLine($"  {numTag,-8} {f.Id}  p{f.Page}  {f.Category,-7}  {f.Label}{cap}");
                    }
                }
                if (v.Anomalies is { Count: > 0 })
                {
                    sb.AppendLine($"Anomalies ({v.Anomalies.Count}):");
                    foreach (var a in v.Anomalies)
                        sb.AppendLine($"  [{a.Severity}] {a.Code}: {a.Message}");
                }
                if (v.SuspectPages is { Count: > 0 })
                    sb.AppendLine($"Suspect pages for QA: {string.Join(", ", v.SuspectPages)}");
                return sb.ToString().TrimEnd();
            });
        }
    }
}

public sealed class GetCliCommand : CliCommand
{
    private const string UsageText = "get <addr> [--view outline|full|ids] [--page <n>] [--category <figure|diagram|chart>] [--min-conf <0..1>]";

    public GetCliCommand()
        : base(new CommandDescriptor(
            Verb: "get",
            Usage: UsageText,
            Description: "Inspects an ingested document or figure using compact outline (default), full, or ids views.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples:
            [
                "melomg get Doc_2026...",
                "melomg get Doc_2026...#2.1 --view full",
                "melomg get Doc_2026... --view ids --page 2"
            ]))
    {
    }

    public sealed class Handler(GetTargetHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            var view = ViewMode.Outline;
            if (context.NamedOptions.TryGetValue("view", out var rawView) && !string.IsNullOrWhiteSpace(rawView))
            {
                switch (rawView.Trim().ToLowerInvariant())
                {
                    case "outline": view = ViewMode.Outline; break;
                    case "full": view = ViewMode.Full; break;
                    case "ids": view = ViewMode.Ids; break;
                    default:
                        return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidView", $"View '{rawView}' must be outline, full, or ids.", context.GlobalOptions.Json);
                }
            }

            if (!CliCommandHelpers.TryGetIntOption(context, "page", out var pageNum, out int pageErr))
                return pageErr;
            if (!CliCommandHelpers.TryGetDoubleOption(context, "min-conf", out var minConf, out int confErr))
                return confErr;

            context.NamedOptions.TryGetValue("category", out var category);
            var res = await useCase.HandleAsync(
                new GetTargetQuery(context.PositionalArgs[0], view, pageNum, category, minConf),
                context.CancellationToken);

            return CliCommandHelpers.Finish(context, res, v => FormatGetOutput(v));
        }

        private static string FormatGetOutput(GetTargetResponse v)
        {
            var sb = new StringBuilder();
            if (v.Figure is { } f)
            {
                sb.AppendLine($"Figure:    {f.FigureId} (#f{f.Index}, num={f.FigureNumber ?? "-"}, doc={f.DocumentId})");
                sb.AppendLine($"Page:      {f.PageNumber} ({f.PageWidthPts:F1}x{f.PageHeightPts:F1} pts)");
                sb.AppendLine($"Category:  {f.Category} (src={f.Source}, conf={f.Confidence:F2})");
                sb.AppendLine($"Label:     {f.Label}");
                sb.AppendLine($"Caption:   {f.Caption ?? "(none)"}");
                sb.AppendLine($"BBox:      ({f.BoundingBoxPts.X:F1}, {f.BoundingBoxPts.Y:F1}, {f.BoundingBoxPts.Width:F1}, {f.BoundingBoxPts.Height:F1})");
                sb.AppendLine($"Masks:     {f.LabelMasks.Count} saved, {f.DetectedLabels.Count} text labels");
                return sb.ToString().TrimEnd();
            }

            if (v.Ids is not null)
            {
                foreach (var id in v.Ids)
                    sb.AppendLine($"{id.I}  {id.Id}  num={id.Num ?? "-"}  p{id.Page}");
                return sb.Length == 0 ? $"{v.DocumentId}: 0 matching figures." : sb.ToString().TrimEnd();
            }

            string vocab = v.Vocab?.Primary is { } p ? $"  vocab={p}" : "";
            sb.AppendLine($"{v.DocumentId}  \"{v.Title}\"  pages={v.PageCount}  total={v.TotalFigures}{vocab}");
            if (v.Figures is not null)
            {
                foreach (var fig in v.Figures)
                {
                    string numTag = string.IsNullOrWhiteSpace(fig.Num) ? $"#f{fig.I}" : $"#{fig.Num}";
                    string cap = string.IsNullOrWhiteSpace(fig.Caption) ? "" : $"  \"{fig.Caption}\"";
                    sb.AppendLine($"  {numTag,-8} {fig.Id}  p{fig.Page}  {fig.Category,-7}  {fig.Label}{cap}");
                }
            }
            else if (v.FullFigures is not null)
            {
                foreach (var fig in v.FullFigures)
                {
                    string cap = string.IsNullOrWhiteSpace(fig.Caption) ? "" : $"  \"{fig.Caption}\"";
                    sb.AppendLine($"  [{fig.Index}] {fig.FigureId}  num={fig.FigureNumber ?? "-"}  p{fig.PageNumber}  {fig.Category,-7}  conf={fig.Confidence:F2}  bbox=({fig.BoundingBox.X:F1},{fig.BoundingBox.Y:F1},{fig.BoundingBox.Width:F1},{fig.BoundingBox.Height:F1}){cap}");
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}

public sealed class LsCliCommand : CliCommand
{
    private const string UsageText = "ls [directory]";

    public LsCliCommand()
        : base(new CommandDescriptor(
            Verb: "ls",
            Usage: UsageText,
            Description: "Lists discovered Doc_<d/t> workspaces and their detected figure vocabulary.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg ls", "melomg ls ./work --json"]))
    {
    }

    public sealed class Handler(ListDocumentsHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string? dir = context.PositionalArgs.Count == 1 ? context.PositionalArgs[0] : null;
            var res = await useCase.HandleAsync(new ListDocumentsQuery(dir), context.CancellationToken);
            if (!res.IsSuccess)
                return res.ToExitCode(context.Console, context.GlobalOptions.Json);

            var sb = new StringBuilder();
            if (res.Value.Count == 0)
            {
                sb.Append("No ingested document workspaces found.");
            }
            else
            {
                foreach (var item in res.Value)
                {
                    string vTag = string.IsNullOrWhiteSpace(item.VocabPrimaryPrefix) ? "" : $"  vocab={item.VocabPrimaryPrefix}";
                    sb.AppendLine($"{item.DocumentId}  [{item.WorkspaceMode}]  \"{item.Title}\"  pages={item.PageCount}  figures={item.FigureCount}{vTag}  ({item.StorageDirectory})");
                }
            }

            CliCommandHelpers.WriteOutput(context, res.Value, sb.ToString().TrimEnd());
            return ExitCodes.Success;
        }
    }
}
