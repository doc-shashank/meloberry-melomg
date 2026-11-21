using Melomg.Cli.Abstractions;
using Melomg.Cli.Parsing;

namespace Melomg.Cli.Repl;

public sealed class InteractiveReplLoop(CommandDispatcher dispatcher)
{
    public async Task<int> RunAsync(IConsole console, CancellationToken cancellationToken = default)
    {
        var sessionContext = new CliSessionContext();
        console.Out.WriteLine("melomg — Hybrid ONNX PDF Figure Extractor REPL — type 'help' for commands, 'exit' to quit.");

        while (!cancellationToken.IsCancellationRequested)
        {
            console.Out.Write("melomg > ");
            console.Out.Flush();

            var line = console.In.ReadLine();
            if (line is null)
                break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase))
                break;

            var stages = CommandTokenizer.SplitIntoChainStages(trimmed);
            await dispatcher.ExecuteStagesAsync(stages, console, sessionContext, cancellationToken);
        }

        return ExitCodes.Success;
    }
}
