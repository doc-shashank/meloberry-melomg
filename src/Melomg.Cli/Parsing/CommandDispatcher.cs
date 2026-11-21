using Melomg.Cli.Abstractions;
using Melomg.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Cli.Parsing;

public sealed class CommandDispatcher(
    IEnumerable<CliCommand> commands,
    IServiceScopeFactory scopeFactory)
{
    private readonly Dictionary<string, CliCommand> _commands = commands
        .ToDictionary(c => c.Descriptor.Verb, StringComparer.OrdinalIgnoreCase);

    public async Task<int> ExecuteStagesAsync(
        IReadOnlyList<IReadOnlyList<ParsedToken>> stages,
        IConsole console,
        CliSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        if (stages.Count == 0)
        {
            WriteHelp(console);
            return ExitCodes.Success;
        }

        GlobalCliOptions? firstStageGlobals = null;
        ServiceProvider? customProvider = null;
        string? customProviderAppData = null;

        try
        {
            for (int s = 0; s < stages.Count; s++)
            {
                var stage = stages[s];
                if (!TryParseStage(stage, out var verb, out var targetId, out var positional, out var named, out var stageGlobals, out var showHelp, out var parseErr))
                {
                    bool wantsJson = stageGlobals.Json || (firstStageGlobals?.Json ?? false) ||
                        stage.TakeWhile(t => t.WasQuoted || t.Value != "--").Any(t => !t.WasQuoted && string.Equals(t.Value, "--json", StringComparison.OrdinalIgnoreCase));
                    return CliResultExtensions.WriteValidationError(console, "Cli.ParseError", parseErr!, wantsJson);
                }

                if (s == 0)
                    firstStageGlobals = stageGlobals;
                else
                    stageGlobals = MergeGlobalOptions(firstStageGlobals!, stageGlobals);

                if (showHelp || string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase))
                {
                    var helpTopic = !string.IsNullOrWhiteSpace(verb) && !string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase)
                        ? verb
                        : (targetId ?? (positional.Count > 0 ? positional[0] : null));
                    WriteHelp(console, helpTopic);
                    return ExitCodes.Success;
                }

                if (string.IsNullOrWhiteSpace(verb))
                {
                    WriteHelp(console);
                    return ExitCodes.ValidationError;
                }

                if (!_commands.TryGetValue(verb, out var cliCommand))
                {
                    return CliResultExtensions.WriteValidationError(
                        console,
                        "Cli.UnknownCommand",
                        $"Unknown command '{verb}'. Run '--help' for supported commands.",
                        stageGlobals.Json);
                }

                if (!string.IsNullOrWhiteSpace(stageGlobals.AppDataDirectory) &&
                    !string.Equals(customProviderAppData, stageGlobals.AppDataDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    customProvider?.Dispose();
                    customProvider = CliApplicationRunner.BuildServiceProvider(stageGlobals.AppDataDirectory);
                    customProviderAppData = stageGlobals.AppDataDirectory;
                }

                using var scope = customProvider is not null && !string.IsNullOrWhiteSpace(stageGlobals.AppDataDirectory)
                    ? customProvider.CreateScope()
                    : scopeFactory.CreateScope();
                var handler = (ICommandHandler)scope.ServiceProvider.GetRequiredService(cliCommand.Descriptor.HandlerType);
                var invocationContext = new CliInvocationContext(
                    console,
                    sessionContext,
                    stageGlobals,
                    targetId,
                    positional,
                    named,
                    cancellationToken);

                int exitCode = await handler.InvokeAsync(invocationContext);
                if (exitCode != ExitCodes.Success)
                    return exitCode;
            }

            return ExitCodes.Success;
        }
        finally
        {
            customProvider?.Dispose();
        }
    }

    private static readonly (CommandCategory Category, string Key, string Title)[] CategoryOrder =
    [
        (CommandCategory.Documents, "documents", "PDF Ingestion & Workspaces"),
        (CommandCategory.Figures, "figures", "Figure Inspection & Refinement"),
        (CommandCategory.Extraction, "extraction", "Figure Extraction"),
        (CommandCategory.Models, "models", "ONNX Models & Thresholds"),
        (CommandCategory.DiagnosticsAndDocs, "diagnostics", "Lint, Deletion & Embedded Docs")
    ];

    public void WriteHelp(IConsole console, string? topic = null)
    {
        if (!string.IsNullOrWhiteSpace(topic))
        {
            if (_commands.TryGetValue(topic, out var specific))
            {
                WriteCommandDetail(console, specific);
                return;
            }

            if (TryResolveCategory(topic, out var catEntry))
            {
                WriteCategoryHelp(console, catEntry.Category, catEntry.Title);
                return;
            }

            console.Out.WriteLine($"No help topic found for '{topic}'. Showing full reference:");
            console.Out.WriteLine();
        }

        console.Out.WriteLine("melomg — Hybrid ONNX PDF Ingestion, Page-Crop Extraction & Label Obfuscation CLI (.NET 10)");
        console.Out.WriteLine();
        console.Out.WriteLine("Boot Modes:");
        console.Out.WriteLine("  melomg                        Start Interactive REPL mode (melomg > )");
        console.Out.WriteLine("  melomg repl                   Start Interactive REPL mode (melomg > )");
        console.Out.WriteLine("  melomg <command> [args...]    Run a single stateless command or fluent '->' chain");
        console.Out.WriteLine();
        console.Out.WriteLine("Syntax & Entity IDs:");
        console.Out.WriteLine("  Prefixes:  Doc_<d/t> (ingested PDF workspace) | Fig_<d/t> (detected/manual figure)");
        console.Out.WriteLine("  Target:    <id> <verb> [args...]   (e.g., Fig_2026... extract ./out --dpi 300)");
        console.Out.WriteLine("  Chain:     melomg \"ingest ./paper.pdf -> figures -> extract 1 ./out --dpi 300\"");
        console.Out.WriteLine();
        console.Out.WriteLine("Global Options:");
        console.Out.WriteLine("  --doc <id>                    Target a Doc_<d/t> workspace explicitly");
        console.Out.WriteLine("  --figure <id>                 Target a Fig_<d/t> figure explicitly");
        console.Out.WriteLine("  --model <onnxPath>            Override ONNX model path for ingest/reanalyze");
        console.Out.WriteLine("  --yes                         Confirm deletion prompts non-interactively");
        console.Out.WriteLine("  --json                        Format stdout/stderr output as JSON");
        console.Out.WriteLine("  --silent                      Suppress non-error stdout output");
        console.Out.WriteLine("  --app-data <dir>              Override persistent ONNX settings/index directory");
        console.Out.WriteLine("  --help                        Show this overview or 'melomg <command> --help'");
        console.Out.WriteLine();
        console.Out.WriteLine("Commands (run 'melomg help <command>' or 'melomg help <category>' for details):");

        foreach (var (category, key, title) in CategoryOrder)
        {
            var group = _commands.Values
                .Where(c => c.Descriptor.Category == category)
                .OrderBy(c => c.Descriptor.Verb, StringComparer.Ordinal)
                .ToList();

            if (group.Count == 0)
                continue;

            console.Out.WriteLine();
            console.Out.WriteLine($"  [{title}]  (category: {key})");
            foreach (var cmd in group)
                console.Out.WriteLine($"    {cmd.Descriptor.Verb,-16} {cmd.Descriptor.Description}");
        }

        console.Out.WriteLine();
        console.Out.WriteLine("  [Built-in Help]");
        console.Out.WriteLine("    help [topic]     Show command syntax/examples or filter by category");
        console.Out.WriteLine();
        console.Out.WriteLine("Exit Codes: 0=Success  1=GeneralError  2=Validation/Bounds  3=NotFound  4=Conflict  5=Unauthorized");
    }

    private void WriteCommandDetail(IConsole console, CliCommand command)
    {
        var d = command.Descriptor;
        var categoryTitle = CategoryOrder.First(c => c.Category == d.Category).Title;

        console.Out.WriteLine($"{d.Verb} — {d.Description}");
        console.Out.WriteLine($"Category:  {categoryTitle}");
        console.Out.WriteLine($"Usage:     melomg {d.Usage}");
        console.Out.WriteLine($"Target ID: {(d.AcceptsLeadingTargetId ? "Supports leading <id> prefix (e.g., <id> " + d.Verb + " ...)" : "Does not take a leading <id> prefix")}");

        if (!string.IsNullOrWhiteSpace(d.DetailedNotes))
        {
            console.Out.WriteLine();
            console.Out.WriteLine("Details:");
            foreach (var line in d.DetailedNotes.Split('\n'))
                console.Out.WriteLine($"  {line.TrimEnd('\r')}");
        }

        if (d.Examples is { Count: > 0 })
        {
            console.Out.WriteLine();
            console.Out.WriteLine("Examples:");
            foreach (var ex in d.Examples)
                console.Out.WriteLine($"  {ex}");
        }
    }

    private void WriteCategoryHelp(IConsole console, CommandCategory category, string title)
    {
        console.Out.WriteLine($"[{title}] — Commands");
        console.Out.WriteLine();

        var group = _commands.Values
            .Where(c => c.Descriptor.Category == category)
            .OrderBy(c => c.Descriptor.Verb, StringComparer.Ordinal);

        foreach (var cmd in group)
        {
            console.Out.WriteLine($"  {cmd.Descriptor.Verb}");
            console.Out.WriteLine($"    Usage:   melomg {cmd.Descriptor.Usage}");
            console.Out.WriteLine($"    Summary: {cmd.Descriptor.Description}");
            if (cmd.Descriptor.Examples is { Count: > 0 })
                console.Out.WriteLine($"    Example: {cmd.Descriptor.Examples[0]}");
            console.Out.WriteLine();
        }
    }

    private static bool TryResolveCategory(string raw, out (CommandCategory Category, string Key, string Title) entry)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        foreach (var item in CategoryOrder)
        {
            if (string.Equals(item.Key, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Title, normalized, StringComparison.OrdinalIgnoreCase))
            {
                entry = item;
                return true;
            }
        }

        switch (normalized)
        {
            case "doc":
            case "docs-cmd":
            case "pdf":
            case "ingest":
                entry = CategoryOrder[0];
                return true;
            case "figure":
            case "fig":
            case "bbox":
            case "masks":
            case "obfuscation":
                entry = CategoryOrder[1];
                return true;
            case "extract":
            case "export":
            case "pages":
                entry = CategoryOrder[2];
                return true;
            case "model":
            case "onnx":
                entry = CategoryOrder[3];
                return true;
            case "lint":
            case "delete":
            case "guides":
                entry = CategoryOrder[4];
                return true;
            default:
                entry = default;
                return false;
        }
    }

    private bool TryParseStage(
        IReadOnlyList<ParsedToken> tokens,
        out string verb,
        out string? targetId,
        out List<string> positionalArgs,
        out Dictionary<string, string?> namedOptions,
        out GlobalCliOptions globals,
        out bool showHelp,
        out string? errorMessage)
    {
        verb = string.Empty;
        targetId = null;
        positionalArgs = [];
        namedOptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        showHelp = false;
        errorMessage = null;

        bool silent = false;
        bool json = false;
        bool yes = false;
        string? docId = null;
        string? figureId = null;
        string? modelPath = null;
        string? appDataDirectory = null;
        bool endOfOptions = false;

        var rawPositionals = new List<string>();
        bool HasSeenVerb() =>
            rawPositionals.Count >= (rawPositionals.Count > 0 && IsEntityId(rawPositionals[0]) ? 2 : 1);

        for (int i = 0; i < tokens.Count; i++)
        {
            var val = tokens[i].Value;
            if (!tokens[i].WasQuoted && val == CommandTokenizer.UnclosedQuoteSentinel)
            {
                globals = new GlobalCliOptions(silent, json, yes, docId, figureId, modelPath, appDataDirectory);
                errorMessage = "Unclosed quote in command input.";
                return false;
            }

            if (!endOfOptions && !tokens[i].WasQuoted && val == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && !tokens[i].WasQuoted && val.StartsWith("--", StringComparison.Ordinal))
            {
                var opt = val[2..].ToLowerInvariant();
                if (opt == "silent") silent = true;
                else if (opt == "json") json = true;
                else if (opt == "yes") yes = true;
                else if (opt == "help") showHelp = true;
                else if (CommandTokenizer.BooleanFlagOptions.Contains(opt))
                    namedOptions[opt] = "true";
                else if (CommandTokenizer.ValueOptions.Contains(val))
                {
                    if (i + 1 >= tokens.Count || (!tokens[i + 1].WasQuoted && tokens[i + 1].Value == CommandTokenizer.UnclosedQuoteSentinel))
                    {
                        globals = new GlobalCliOptions(silent, json, yes, docId, figureId, modelPath, appDataDirectory);
                        errorMessage = $"Option '--{opt}' requires a value.";
                        return false;
                    }
                    var optVal = tokens[++i].Value;
                    if (opt == "doc") docId = optVal;
                    else if (opt == "figure") figureId = optVal;
                    else if (opt == "model") modelPath = optVal;
                    else if (opt == "app-data") appDataDirectory = optVal;
                    else namedOptions[opt] = optVal;
                }
                else if (HasSeenVerb())
                {
                    rawPositionals.Add(val);
                }
                else
                {
                    globals = new GlobalCliOptions(silent, json, yes, docId, figureId, modelPath, appDataDirectory);
                    errorMessage = $"Unknown option '--{opt}'.";
                    return false;
                }
            }
            else
            {
                rawPositionals.Add(val);
            }
        }

        globals = new GlobalCliOptions(silent, json, yes, docId, figureId, modelPath, appDataDirectory);
        if (rawPositionals.Count == 0)
            return true;

        if (IsEntityId(rawPositionals[0]) && rawPositionals.Count >= 2 && _commands.TryGetValue(rawPositionals[1], out var targetCmd) && targetCmd.Descriptor.AcceptsLeadingTargetId)
        {
            targetId = rawPositionals[0];
            verb = targetCmd.Descriptor.Verb;
            positionalArgs.AddRange(rawPositionals.Skip(2));
        }
        else
        {
            verb = rawPositionals[0];
            positionalArgs.AddRange(rawPositionals.Skip(1));
        }

        return true;
    }

    private static GlobalCliOptions MergeGlobalOptions(GlobalCliOptions parent, GlobalCliOptions current) =>
        new(
            Silent: parent.Silent || current.Silent,
            Json: parent.Json || current.Json,
            Yes: parent.Yes || current.Yes,
            DocId: current.DocId ?? parent.DocId,
            FigureId: current.FigureId ?? parent.FigureId,
            ModelPath: current.ModelPath ?? parent.ModelPath,
            AppDataDirectory: current.AppDataDirectory ?? parent.AppDataDirectory);

    private static bool IsEntityId(string token) =>
        token.StartsWith("Doc_", StringComparison.Ordinal) ||
        token.StartsWith("Fig_", StringComparison.Ordinal);
}
