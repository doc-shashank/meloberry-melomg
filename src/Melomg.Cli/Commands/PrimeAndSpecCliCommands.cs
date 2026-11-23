using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Melomg.Cli.Abstractions;

namespace Melomg.Cli.Commands;

public sealed class PrimeCliCommand : CliCommand
{
    public const string GrammarCard =
        """
        melomg — Agent-first PDF image/diagram crop extractor (crop-only, no tables/boxes/formulas).
        Env: MELOMG_AGENT=1 or MBY_AGENT=1 => --json (minified). MELOMG_APP_DATA=<dir> | --app-data <dir> | -o <dir> | --

        ADDR: <docId> | <docId>#<num> (e.g. #2.1) | <docId>#f<idx> | <docId>#p<page> | <docId>#n:<num> | <figId>

        CMDS:
          prime                                 This grammar card
          spec [topic...]                       Micro-docs: extract patch mask batch vocab onnx errors
          ingest <pdf|dir|docId> [-o dir] [opts] Ingest PDF -> returns vocab + outline + anomalies (--project, --refresh, --retain-db, --offline, --quiet, --verbose)
          get <addr> [--view outline|full|ids]  Inspect doc or figure (--page, --category, --min-conf)
          ls [dir]                              List workspaces
          extract <addr|pdf> [nums|all...] [-o dir] [opts] Crop figure(s) to PNG/JPG (--quality low|medium|high|max|1..100, --dpi 72..600, --manifest, --include-caption, --mask-all, --mask-match <pat>, --naming number|id, --quiet)
          patch <addr> [file|-] [--set k=v]... [--move idx:shift|exchange] Update title or figure bbox/caption/label/num/category/order
          add <docId> <p> <x> <y> <w> <h>       Register manual figure (--category figure|diagram|chart, --num, --label, --caption)
          mask <figAddr> <ls|add|auto|rm|clear> Manage label redaction masks
          render <docId> <p|all> [-o dir] [--overlay] Render full page(s) for QA
          rm <addr>                             Delete workspace or figure immediately
          check <addr> [--quiet]                Verify PDF SHA-256, bounds, image headers, and sequence anomalies
          batch <file|->                        Run JSON op array ($0.id / $prev.id, e.g. "$0.id#2.1")
          model <info|set|threshold|reset>      Configure ONNX model
          runtime <status|install|uninstall>    Manage AppData ONNX Runtime
          version                               CLI version

        Default workspace: .meloberry/melomg/Doc_* (unless -o or --project).
        EXIT: 0=OK 1=IO/General 2=Validation/Lint 3=NotFound 4=Conflict
        """;

    public PrimeCliCommand()
        : base(new CommandDescriptor(
            Verb: "prime",
            Usage: "prime",
            Description: "Prints the compact AI-agent grammar card.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Discovery))
    {
    }

    public sealed class Handler : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            string version = VersionCliCommand.GetCliVersion();
            var payload = new PrimePayload("melomg", version, GrammarCard.TrimEnd());
            CliCommandHelpers.WriteOutput(context, payload, GrammarCard.TrimEnd());
            return Task.FromResult(ExitCodes.Success);
        }
    }
}

public sealed class SpecCliCommand : CliCommand
{
    private static readonly (string Topic, string Summary, string Content)[] Topics =
    [
        ("extract", "Crop-only extraction flags, number targeting, quality presets, and one-shot PDF mode",
            """
            # extract
            Syntax: melomg extract <addr|pdfPath> [nums|all...] [-o dir] [opts]
            Crop-only: renders PDF page region via Windows.Data.Pdf and crops to PNG/JPG (never dumps embedded /Image streams).
            Targeting:
              melomg extract Doc_...#2.1 -o ./assets --quality high
              melomg extract Doc_... 2.1 2.4 -o ./assets
              melomg extract Doc_... 2.1,2.4 -o ./assets
              melomg extract ./paper.pdf 2.1 -o ./assets   (one-shot ephemeral ingest -> crop -> auto-cleanup)
            Options:
              --format png|jpg          (default png)
              --quality low|medium|high|max|1..100  (presets set default DPI: low=96, medium=150, high=240, max=300)
              --dpi 72..600             (overrides preset DPI)
              --padding 0..100          (default 8 px)
              --naming number|id        (default number -> fig_<num>.<ext>; id -> Fig_<d/t>.<ext>)
              --include-caption         (include matched caption in crop)
              --mask-all                (redact all detected text labels inside figure)
              --mask-match <patterns>   (comma-separated literal/glob/regex patterns, 250ms timeout)
              --mask-style solid|blur|pixelate|whiteout
              --mask-color <#RRGGBB>
              --no-masks                (ignore saved masks)
              --meta                    (emit .meta.json sidecar per image)
              --manifest                (emit <outDir>/manifest.json mapping num -> path, page, caption)
              --keep-overlay            (do not delete tracked QA overlays)
              --retain-db               (keep .meloberry/melomg/Doc_*.db after extraction)
              --offline                 (skip runtime auto-install)
              --quiet | --verbose       (omit or expand artifact details)
            """),
        ("patch", "Surgical partial updates via --set, --move, or JSON/stdin",
            """
            # patch
            Syntax: melomg patch <addr> [file|-] [--set k=v]... [--move idx:shift|exchange]
            Doc fields (<docId>): title
            Figure fields (<docId>#<num|fN> | <figId>):
              bbox=x,y,w,h  caption="text"  label="text"  num="2.1"  category=figure|diagram|chart
            JSON schema (file or stdin '-'):
              { "title"?: string }  (on Doc_*)
              { "bbox"?: [x,y,w,h], "caption"?: string, "label"?: string, "num"?: string, "category"?: "figure"|"diagram"|"chart", "move"?: "1:shift" }
            """),
        ("mask", "Label redaction masks on a figure",
            """
            # mask
            Syntax: melomg mask <figAddr> <ls|add|auto|rm|clear> [args...]
            Subcommands:
              ls                                      List saved masks + detected text labels
              add <x> <y> <w> <h> [--label t] [--style solid|blur|pixelate|whiteout] [--color #RRGGBB]
              auto [--match <p1,p2>] [--style ...] [--color ...]
              rm <maskIndex>
              clear
            """),
        ("batch", "Atomic-style sequential JSON batch execution with $0.id / $prev.id",
            """
            # batch
            Syntax: melomg batch <file|->
            Input: JSON array of op objects. Interpolates $0.id / $prev.id in 'addr' (e.g. "$0.id#2.1").
            Supported ops:
              { "op": "ingest", "pdf": "./p.pdf", "out"?: "./w", "project"?: false, "refresh"?: false, "retainDb"?: true, "offline"?: true, "confidence"?: 0.4, "title"?: "...", "quiet"?: false, "verbose"?: false }
              { "op": "get", "addr": "$0.id", "view"?: "outline"|"full"|"ids" }
              { "op": "extract", "addr": "$0.id", "selectors": ["2.1","2.4"], "out"?: "./assets", "format"?: "png", "quality"?: "high", "dpi"?: 240, "naming"?: "number", "manifest"?: true, "quiet"?: false }
              { "op": "patch", "addr": "$0.id#2.1", "set": { "caption"?: "...", "label"?: "...", "num"?: "...", "category"?: "diagram", "bbox"?: [x,y,w,h], "move"?: "1:shift", "title"?: "..." } }
              { "op": "add", "addr": "$0.id", "page": 1, "bbox": [x,y,w,h], "category"?: "diagram", "num"?: "2.5", "caption"?: "...", "label"?: "..." }
              { "op": "mask", "addr": "$0.id#2.1", "action": "auto"|"ls"|"clear"|"rm", "match"?: "...", "style"?: "blur", "index"?: 1 }
              { "op": "render", "addr": "$0.id", "page"?: 1, "all"?: false, "out"?: "./qa", "overlay"?: true }
              { "op": "check", "addr": "$0.id" }
              { "op": "rm", "addr": "$0.id" }
            """),
        ("vocab", "Automatic PDF vocabulary detection and number-only targeting",
            """
            # vocab
            melomg scans PDF captions during ingestion, rejects inline prose references ("Figure 2.1 shows..."),
            and rejects non-figure artifacts (Table, Box, Callout, Equation, Formula, Algorithm).
            Detected 'vocab' + anomaly lint returned on 'ingest' & 'check':
              { "primary": "Fig.", "prefixes": ["Fig."], "style": "hierarchical", "numbered": 6, "unnumbered": 0 }
              anomalies?: [Anomaly.SequenceGap, Anomaly.DuplicateNumber, Anomaly.OutOfOrderNumber, Anomaly.UnnumberedFigure, Anomaly.LowConfidence], suspectPages?: [5, 6]
            Targeting:
              Caller only supplies the number: #2.1 or 'extract <docId> 2.1 2.4' or 'extract ./paper.pdf 2.1'.
              Sub-panels: #2.1 expands to 2.1a, 2.1b on 'extract' if parent 2.1 does not exist; use #2.1a or #f<idx> for single-target edits.
              Escape #f<n> / #p<n> collisions with #n:<num> (e.g. #n:p2).
            """),
        ("onnx", "ONNX Runtime, DocLayout-YOLO allowlist, and structural exclusion",
            """
            # onnx
            Allowed FigureCategory enum: ONLY figure, diagram, chart (Table, Formula, Box are structurally absent).
            Neural decoder & PDF vector/visual heuristics drop tables, booktabs rule-tables, shaded/bordered prose callout boxes, and formulas.
            Commands:
              melomg runtime status | install [--provider directml|cpu] [--skip-model] [--force] | uninstall [--keep-model]
              melomg model info | set <onnxPath> [--confidence 0.05..0.99] [--iou 0.10..0.95] | threshold <conf> [iou] | reset
            """),
        ("errors", "JSON envelope and exit codes",
            """
            # errors
            Success (exit 0, stdout): {"success":true,"data":{...}}
            Failure (exit != 0, stderr): {"success":false,"error":{"code":"...","description":"...","type":"...","path":"...","hint":"..."}}
            Codes: 0=Success 1=IO/General 2=Validation/Json/Lint 3=NotFound 4=Conflict(AmbiguousNumber/ProjectExists)
            """)
    ];

    public SpecCliCommand()
        : base(new CommandDescriptor(
            Verb: "spec",
            Usage: "spec [topic...]",
            Description: "Prints focused micro-schemas and documentation for extract, patch, mask, batch, vocab, onnx, or errors.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Discovery,
            Examples: ["melomg spec", "melomg spec extract vocab"]))
    {
    }

    public sealed class Handler : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count == 0)
            {
                var topicItems = Topics.Select(t => new SpecTopicItem(t.Topic, t.Summary)).ToList();
                var sb = new StringBuilder();
                foreach (var t in Topics)
                    sb.AppendLine($"  {t.Topic,-10} {t.Summary}");
                CliCommandHelpers.WriteOutput(context, new SpecResponse(Topics: topicItems), sb.ToString().TrimEnd());
                return Task.FromResult(ExitCodes.Success);
            }

            var matched = new List<SpecEntryPayload>();
            foreach (var raw in context.PositionalArgs)
            {
                var found = Topics.FirstOrDefault(t => string.Equals(t.Topic, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(found.Topic))
                {
                    return Task.FromResult(CliResultExtensions.WriteValidationError(
                        context.Console,
                        "Spec.NotFound",
                        $"Unknown spec topic '{raw}'.",
                        context.GlobalOptions.Json,
                        hint: $"Available topics: {string.Join(", ", Topics.Select(t => t.Topic))}."));
                }
                if (matched.All(m => !string.Equals(m.Topic, found.Topic, StringComparison.OrdinalIgnoreCase)))
                    matched.Add(new SpecEntryPayload(found.Topic, found.Content.TrimEnd()));
            }

            string text = string.Join(Environment.NewLine + Environment.NewLine, matched.Select(m => m.Content));
            CliCommandHelpers.WriteOutput(context, new SpecResponse(Specs: matched), text);
            return Task.FromResult(ExitCodes.Success);
        }
    }
}

public sealed class VersionCliCommand : CliCommand
{
    public VersionCliCommand()
        : base(new CommandDescriptor(
            Verb: "version",
            Usage: "version",
            Description: "Prints melomg CLI version and .NET runtime details.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Discovery))
    {
    }

    public static string GetCliVersion() =>
        Melomg.Domain.Common.MelomgVersion.Resolve(typeof(VersionCliCommand).Assembly);

    public sealed class Handler : ICommandHandler
    {
        public Task<int> InvokeAsync(CliInvocationContext context)
        {
            string ver = GetCliVersion();
            var payload = new VersionPayload("melomg", ver, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription);
            CliCommandHelpers.WriteOutput(context, payload, $"melomg {ver} ({RuntimeInformation.FrameworkDescription})");
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
