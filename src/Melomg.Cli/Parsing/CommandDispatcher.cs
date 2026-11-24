using Melomg.Cli.Abstractions;
using Melomg.Cli.Commands;
using Melomg.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Cli.Parsing;

public sealed class CommandDispatcher(
    IEnumerable<CliCommand> commands,
    IServiceScopeFactory scopeFactory)
{
    private readonly Dictionary<string, CliCommand> _commands = commands
        .ToDictionary(c => c.Descriptor.Verb, StringComparer.OrdinalIgnoreCase);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<ParsedToken> tokens,
        IConsole console,
        CancellationToken cancellationToken = default)
    {
        bool agentJsonDefault = CliCommandHelpers.IsAgentEnvironmentEnabled();

        if (tokens.Count == 0)
            return await InvokeRegisteredCommandAsync("prime", [], new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), new GlobalCliOptions(false, agentJsonDefault), console, cancellationToken);

        if (!TryParseArgs(tokens, agentJsonDefault, out var verb, out var positional, out var named, out var multi, out var globals, out var showHelp, out var showVersion, out var parseErr))
        {
            bool wantsJson = agentJsonDefault || globals.Json ||
                tokens.TakeWhile(t => t.WasQuoted || t.Value != "--").Any(t => !t.WasQuoted && string.Equals(t.Value, "--json", StringComparison.OrdinalIgnoreCase));
            return CliResultExtensions.WriteValidationError(console, "Cli.ParseError", parseErr!, wantsJson);
        }

        if (showVersion)
            return await InvokeRegisteredCommandAsync("version", [], named, multi, globals, console, cancellationToken);

        if (showHelp)
        {
            if (!string.IsNullOrWhiteSpace(verb) && !string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase) && _commands.TryGetValue(verb, out var specific))
            {
                WriteCommandHelp(console, specific, globals);
                return ExitCodes.Success;
            }
            return await InvokeRegisteredCommandAsync("prime", [], named, multi, globals, console, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(verb) || string.Equals(verb, "help", StringComparison.OrdinalIgnoreCase))
        {
            if (positional.Count > 0 && _commands.TryGetValue(positional[0], out var topicCmd))
            {
                WriteCommandHelp(console, topicCmd, globals);
                return ExitCodes.Success;
            }
            return await InvokeRegisteredCommandAsync("prime", [], named, multi, globals, console, cancellationToken);
        }

        if (!_commands.TryGetValue(verb, out var resolvedCmd))
        {
            return CliResultExtensions.WriteValidationError(
                console,
                "Cli.UnknownCommand",
                $"Unknown command '{verb}'.",
                globals.Json,
                hint: "Run 'melomg prime' for supported commands.");
        }

        if (resolvedCmd.Descriptor.AllowedOptions is { } allowedOptions)
        {
            foreach (var optName in named.Keys)
            {
                if (!allowedOptions.Contains(optName))
                {
                    return CliResultExtensions.WriteValidationError(
                        console,
                        "Cli.InvalidOption",
                        $"Option '--{optName}' is not valid for command '{resolvedCmd.Descriptor.Verb}'.",
                        globals.Json,
                        hint: $"Usage: melomg {resolvedCmd.Descriptor.Usage}");
                }
            }
        }

        return await InvokeRegisteredCommandAsync(verb, positional, named, multi, globals, console, cancellationToken);
    }

    private async Task<int> InvokeRegisteredCommandAsync(
        string verb,
        IReadOnlyList<string> positional,
        IReadOnlyDictionary<string, string?> named,
        IReadOnlyDictionary<string, IReadOnlyList<string>> multi,
        GlobalCliOptions globals,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var cliCommand = _commands[verb];
        ServiceProvider? customProvider = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(globals.AppDataDirectory))
                customProvider = CliApplicationRunner.BuildServiceProvider(globals.AppDataDirectory);

            using var scope = customProvider is not null
                ? customProvider.CreateScope()
                : scopeFactory.CreateScope();

            var handler = (ICommandHandler)scope.ServiceProvider.GetRequiredService(cliCommand.Descriptor.HandlerType);
            var invocationContext = new CliInvocationContext(
                console,
                globals,
                positional,
                named,
                multi,
                cancellationToken);

            return await handler.InvokeAsync(invocationContext);
        }
        finally
        {
            customProvider?.Dispose();
        }
    }

    private static void WriteCommandHelp(IConsole console, CliCommand command, GlobalCliOptions globals)
    {
        if (globals.Silent && !globals.Json) return;
        var d = command.Descriptor;
        if (globals.Json)
        {
            var payload = new CliJsonEnvelope<CommandHelpPayload>(
                true,
                new CommandHelpPayload(d.Verb, $"melomg {d.Usage}", d.Description, d.DetailedNotes, d.Examples));
            console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                payload, MelomgCliJsonContext.Default.CliJsonEnvelopeCommandHelpPayload));
            return;
        }
        console.Out.WriteLine($"{d.Verb} — {d.Description}");
        console.Out.WriteLine($"Usage: melomg {d.Usage}");
        if (!string.IsNullOrWhiteSpace(d.DetailedNotes))
            console.Out.WriteLine($"Notes: {d.DetailedNotes}");
        if (d.Examples is { Count: > 0 })
        {
            foreach (var ex in d.Examples)
                console.Out.WriteLine($"  {ex}");
        }
    }

    private static bool TryParseArgs(
        IReadOnlyList<ParsedToken> tokens,
        bool agentJsonDefault,
        out string verb,
        out List<string> positionalArgs,
        out Dictionary<string, string?> namedOptions,
        out Dictionary<string, IReadOnlyList<string>> multiValueOptions,
        out GlobalCliOptions globals,
        out bool showHelp,
        out bool showVersion,
        out string? errorMessage)
    {
        verb = string.Empty;
        positionalArgs = [];
        namedOptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        multiValueOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var multiBuilders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        showHelp = false;
        showVersion = false;
        errorMessage = null;

        bool silent = false;
        bool json = agentJsonDefault;
        string? outputDir = null;
        string? modelPath = null;
        string? appDataDirectory = null;
        bool endOfOptions = false;

        var rawPositionals = new List<string>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var val = tokens[i].Value;

            if (!endOfOptions && !tokens[i].WasQuoted && val == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && !tokens[i].WasQuoted && (val == "-o" || val == "-v" || val == "-h"))
            {
                if (val == "-h") { showHelp = true; continue; }
                if (val == "-v") { showVersion = true; continue; }
                val = "--out";
            }

            if (!endOfOptions && !tokens[i].WasQuoted && val.StartsWith("--", StringComparison.Ordinal))
            {
                var opt = val[2..].ToLowerInvariant();
                if (opt == "silent") silent = true;
                else if (opt == "json") json = true;
                else if (opt == "help") showHelp = true;
                else if (opt == "version") showVersion = true;
                else if (CommandTokenizer.BooleanFlagOptions.Contains(opt))
                    namedOptions[opt] = "true";
                else if (CommandTokenizer.ValueOptions.Contains($"--{opt}"))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        globals = new GlobalCliOptions(silent, json, outputDir, modelPath, appDataDirectory);
                        errorMessage = $"Option '--{opt}' requires a value.";
                        return false;
                    }
                    var optVal = tokens[++i].Value;
                    if (opt == "out") outputDir = optVal;
                    else if (opt == "model") modelPath = optVal;
                    else if (opt == "app-data") appDataDirectory = optVal;
                    else
                    {
                        namedOptions[opt] = optVal;
                        if (!multiBuilders.TryGetValue(opt, out var list))
                            multiBuilders[opt] = list = [];
                        list.Add(optVal);
                    }
                }
                else if (rawPositionals.Count > 0)
                {
                    // Allow dash-prefixed positional literals after verb only if unrecognized flag? No — strict: reject unknown flags before '--'
                    globals = new GlobalCliOptions(silent, json, outputDir, modelPath, appDataDirectory);
                    errorMessage = $"Unknown option '--{opt}'. Use '--' before literal arguments starting with '--'.";
                    return false;
                }
                else
                {
                    globals = new GlobalCliOptions(silent, json, outputDir, modelPath, appDataDirectory);
                    errorMessage = $"Unknown option '--{opt}'.";
                    return false;
                }
            }
            else if (!endOfOptions && !tokens[i].WasQuoted && val.Length >= 2 && val[0] == '-' && val[1] != '-' && !char.IsDigit(val[1]) && val[1] != '.')
            {
                globals = new GlobalCliOptions(silent, json, outputDir, modelPath, appDataDirectory);
                errorMessage = $"Unknown option '{val}'. Use '--' before literal arguments starting with '-'.";
                return false;
            }
            else
            {
                rawPositionals.Add(val);
            }
        }

        foreach (var kv in multiBuilders)
            multiValueOptions[kv.Key] = kv.Value;

        globals = new GlobalCliOptions(silent, json, outputDir, modelPath, appDataDirectory);
        if (rawPositionals.Count == 0)
            return true;

        verb = rawPositionals[0];
        positionalArgs.AddRange(rawPositionals.Skip(1));
        return true;
    }
}
