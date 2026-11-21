using Melomg.Cli.Abstractions;
using Melomg.Cli.Hosting;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliApplicationRunner.ExecuteAsync(args, new SystemConsole(), cancellationToken: cts.Token);
