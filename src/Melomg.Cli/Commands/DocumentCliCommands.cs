using System.Text;
using Melomg.Application.UseCases.Documents;
using Melomg.Cli.Abstractions;

namespace Melomg.Cli.Commands;

public sealed class IngestCliCommand : CliCommand
{
    public IngestCliCommand()
        : base(new CommandDescriptor(
            Verb: "ingest",
            Usage: "ingest <pdfPath> [workingDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline] [--retain-db]",
            Description: "Ingests a PDF into a Doc_<d/t> workspace and detects figures via the Hybrid ONNX pipeline.",
            AcceptsLeadingTargetId: false,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            DetailedNotes: "Verifies '%PDF-' magic bytes, creates <workingDir>/Doc_<d/t>/ with Doc_<d/t>.db and figures/ (referencing the source PDF without copying it), auto-installs ONNX Runtime into AppData if missing (unless '--offline' or MELOMG_OFFLINE=1, falling back cleanly to managed detection), runs ONNX + PDF structure analysis, and latches ActiveDocumentId + ActiveFigureId. Non-project Doc_<d/t>.db files are automatically discarded once 'extract' finishes unless '--retain-db' is passed on 'ingest' or 'extract' (or use 'open' for a persistent project folder).",
            Examples:
            [
                "melomg ingest ./paper.pdf",
                "melomg ingest ./paper.pdf ./work --confidence 0.45 --title \"Attention Paper\"",
                "melomg ingest ./paper.pdf ./work --retain-db",
                "melomg \"ingest ./paper.pdf -> figures -> extract 1 ./out --dpi 300\""
            ]))
    {
    }

    public sealed class Handler(IngestPdfHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1 || context.PositionalArgs.Count > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: ingest <pdfPath> [workingDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline] [--retain-db]", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var confidence, out int numErr))
                return numErr;

            context.NamedOptions.TryGetValue("title", out var title);
            bool offline = context.NamedOptions.ContainsKey("offline");
            bool retainDb = context.NamedOptions.ContainsKey("retain-db");
            string pdfPath = context.PositionalArgs[0];
            string? workingDir = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : null;

            var res = await useCase.HandleAsync(
                new IngestPdfCommand(pdfPath, workingDir, confidence, context.GlobalOptions.ModelPath, title, offline, retainDb),
                context.CancellationToken);

            return CliCommandHelpers.Finish(
                context,
                res,
                v => $"Ingested '{v.Title}' -> {v.DocumentId} ({v.PageCount} page(s), {v.FigureCount} figure(s) detected via {v.ModelName} @ {v.ConfidenceThreshold:F2}, engine={v.InferenceEngine}).",
                v => { context.SessionContext.ActiveDocumentId = v.DocumentId; context.SessionContext.ActiveFigureId = v.FirstFigureId; });
        }
    }
}

public sealed class OpenCliCommand : CliCommand
{
    public OpenCliCommand()
        : base(new CommandDescriptor(
            Verb: "open",
            Usage: "open <pdfPath|projectDir> [projectDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline]",
            Description: "Opens a PDF as a persistent project (moving the PDF into [projectDir] or ./project_<PDF name>/) or re-activates an existing project directory.",
            AcceptsLeadingTargetId: false,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            DetailedNotes: "When passed a PDF file, validates '%PDF-', analyzes figures, physically moves the PDF into [projectDir] (or '<CWD>/project_<PDF name>/' when omitted), writes a persistent project SQLite database there (always retained across extractions), and latches ActiveDocumentId + ActiveFigureId. When passed an existing project directory, loads and activates its stored project workspace without re-ingesting.",
            Examples:
            [
                "melomg open ./paper.pdf",
                "melomg open ./paper.pdf ./my-study --confidence 0.40",
                "melomg \"open ./paper.pdf -> figures -> extract all ./crops\"",
                "melomg open ./project_paper"
            ]))
    {
    }

    public sealed class Handler(OpenProjectHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count < 1 || context.PositionalArgs.Count > 2)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: open <pdfPath|projectDir> [projectDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline]", context.GlobalOptions.Json);

            if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var confidence, out int numErr))
                return numErr;

            context.NamedOptions.TryGetValue("title", out var title);
            bool offline = context.NamedOptions.ContainsKey("offline");
            string targetPath = context.PositionalArgs[0];
            string? projectDir = context.PositionalArgs.Count == 2 ? context.PositionalArgs[1] : null;

            var res = await useCase.HandleAsync(
                new OpenProjectCommand(targetPath, projectDir, confidence, context.GlobalOptions.ModelPath, title, offline),
                context.CancellationToken);

            return CliCommandHelpers.Finish(
                context,
                res,
                v => $"{(v.ReopenedExisting ? "Opened existing project" : "Created project")} '{v.Title}' at '{v.ProjectDirectory}' -> {v.DocumentId} ({v.PageCount} page(s), {v.FigureCount} figure(s), engine={v.InferenceEngine}).",
                v => { context.SessionContext.ActiveDocumentId = v.DocumentId; context.SessionContext.ActiveFigureId = v.FirstFigureId; });
        }
    }
}

public sealed class UseDocCliCommand : CliCommand
{
    public UseDocCliCommand()
        : base(new CommandDescriptor(
            Verb: "usedoc",
            Usage: "usedoc <docID>",
            Description: "Activates an existing Doc_<d/t> workspace in REPL or a fluent chain.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg usedoc Doc_20261120_015000_001", "melomg \"usedoc Doc_2026... -> figures\""]))
    {
    }

    public sealed class Handler(UseDocumentHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > (context.TargetId is not null ? 0 : 1))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: usedoc <docID>", context.GlobalOptions.Json);

            var docId = context.TargetId ?? (context.PositionalArgs.Count == 1 ? context.PositionalArgs[0] : context.GlobalOptions.DocId);
            if (string.IsNullOrWhiteSpace(docId))
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: usedoc <docID>", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(new UseDocumentCommand(docId), context.CancellationToken);
            return CliCommandHelpers.Finish(
                context,
                res,
                v => $"Active document: {v.DocumentId} (\"{v.Title}\", {v.PageCount} page(s), {v.FigureCount} figure(s)).",
                v => { context.SessionContext.ActiveDocumentId = v.DocumentId; context.SessionContext.ActiveFigureId = v.ActiveFigureId; });
        }
    }
}

public sealed class ListDocsCliCommand : CliCommand
{
    public ListDocsCliCommand()
        : base(new CommandDescriptor(
            Verb: "listdocs",
            Usage: "listdocs [directory]",
            Description: "Lists ingested Doc_<d/t> workspaces and auto-discovers portable folders.",
            AcceptsLeadingTargetId: false,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg listdocs", "melomg listdocs ./work --json"]))
    {
    }

    public sealed class Handler(ListDocumentsHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count > 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: listdocs [directory]", context.GlobalOptions.Json);

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
                    sb.AppendLine($"{item.DocumentId}  [{item.WorkspaceMode}]  \"{item.Title}\"  pages={item.PageCount}  figures={item.FigureCount}  model={item.ModelName} ({item.StorageDirectory})");
                }
            }

            CliCommandHelpers.WriteOutput(context, res.Value, sb.ToString().TrimEnd());
            return ExitCodes.Success;
        }
    }
}

public sealed class InfoCliCommand : CliCommand
{
    public InfoCliCommand()
        : base(new CommandDescriptor(
            Verb: "info",
            Usage: "[<docID>] info",
            Description: "Displays PDF metadata, SHA-256 hash, page dimensions, ONNX model used, and figure breakdown.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg Doc_2026... info", "melomg --doc Doc_2026... info --json"]))
    {
    }

    public sealed class Handler(GetDocumentInfoHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveDocumentId(context, allowPositional: true, "[<docID>] info", out var docId, out int err))
                return err;

            var res = await useCase.HandleAsync(new GetDocumentInfoQuery(docId), context.CancellationToken);
            return CliCommandHelpers.Finish(context, res, d =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Document:    {d.DocumentId}");
                sb.AppendLine($"Title:       {d.Title}");
                sb.AppendLine($"Pages:       {d.PageCount}");
                sb.AppendLine($"Figures:     {d.FigureCount} (active: {d.ActiveFigureId ?? "none"})");
                sb.AppendLine($"Model:       {d.ModelName} (confidence >= {d.ConfidenceThreshold:F2})");
                sb.AppendLine($"SHA-256:     {d.Sha256Hash}");
                sb.AppendLine($"Workspace:   {d.StorageDirectory}");
                if (d.CategoryBreakdown.Count > 0)
                    sb.AppendLine($"Categories:  {string.Join(", ", d.CategoryBreakdown.Select(c => $"{c.Category}={c.Count}"))}");
                return sb.ToString().TrimEnd();
            }, d => context.SessionContext.ActiveDocumentId = d.DocumentId);
        }
    }
}

public sealed class SetTitleCliCommand : CliCommand
{
    public SetTitleCliCommand()
        : base(new CommandDescriptor(
            Verb: "settitle",
            Usage: "[<docID>] settitle <title>",
            Description: "Updates the human-readable title of an ingested PDF document workspace.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg Doc_2026... settitle \"Neural Architecture Survey\""]))
    {
    }

    public sealed class Handler(SetDocumentTitleHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveDocumentId(context, allowPositional: false, "[<docID>] settitle <title>", out var docId, out int err))
                return err;

            if (context.PositionalArgs.Count != 1)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", "Usage: [<docID>] settitle <title>", context.GlobalOptions.Json);

            var res = await useCase.HandleAsync(new SetDocumentTitleCommand(docId, context.PositionalArgs[0]), context.CancellationToken);
            return CliCommandHelpers.Finish(context, res, v => $"Updated title of {v.DocumentId} to \"{v.Title}\".");
        }
    }
}

public sealed class ReanalyzeCliCommand : CliCommand
{
    public ReanalyzeCliCommand()
        : base(new CommandDescriptor(
            Verb: "reanalyze",
            Usage: "[<docID>] reanalyze [--confidence <0.05..0.99>] [--model <onnxPath>] [--offline]",
            Description: "Re-runs the Hybrid ONNX figure detector on the stored PDF and refreshes detected figures.",
            AcceptsLeadingTargetId: true,
            HandlerType: typeof(Handler),
            Category: CommandCategory.Documents,
            Examples: ["melomg Doc_2026... reanalyze --confidence 0.50", "melomg Doc_2026... reanalyze --model ./custom.onnx"]))
    {
    }

    public sealed class Handler(ReanalyzeDocumentHandler useCase) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (!CliCommandHelpers.TryResolveDocumentId(context, allowPositional: true, "[<docID>] reanalyze", out var docId, out int err))
                return err;

            if (!CliCommandHelpers.TryGetDoubleOption(context, "confidence", out var confidence, out int numErr))
                return numErr;

            bool offline = context.NamedOptions.ContainsKey("offline");
            var res = await useCase.HandleAsync(
                new ReanalyzeDocumentCommand(docId, confidence, context.GlobalOptions.ModelPath, offline),
                context.CancellationToken);

            return CliCommandHelpers.Finish(
                context,
                res,
                v => $"Reanalyzed {v.DocumentId}: {v.PreviousFigureCount} -> {v.FigureCount} figure(s) using {v.ModelName} @ {v.ConfidenceThreshold:F2} (engine={v.InferenceEngine}).",
                v => { context.SessionContext.ActiveDocumentId = v.DocumentId; context.SessionContext.ActiveFigureId = v.FirstFigureId; });
        }
    }
}
