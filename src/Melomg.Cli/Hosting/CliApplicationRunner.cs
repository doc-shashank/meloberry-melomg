using System.Diagnostics.CodeAnalysis;
using Melomg.Application;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Commands;
using Melomg.Cli.Parsing;
using Melomg.Cli.Repl;
using Melomg.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Cli.Hosting;

public static class CliServiceRegistration
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<InteractiveReplLoop>();

        // Documents & Ingestion
        RegisterCommand<IngestCliCommand, IngestCliCommand.Handler>(services);
        RegisterCommand<OpenCliCommand, OpenCliCommand.Handler>(services);
        RegisterCommand<UseDocCliCommand, UseDocCliCommand.Handler>(services);
        RegisterCommand<ListDocsCliCommand, ListDocsCliCommand.Handler>(services);
        RegisterCommand<InfoCliCommand, InfoCliCommand.Handler>(services);
        RegisterCommand<SetTitleCliCommand, SetTitleCliCommand.Handler>(services);
        RegisterCommand<ReanalyzeCliCommand, ReanalyzeCliCommand.Handler>(services);

        // Figures Inspection, Refinement & Obfuscation Masks
        RegisterCommand<FiguresCliCommand, FiguresCliCommand.Handler>(services);
        RegisterCommand<InspectCliCommand, InspectCliCommand.Handler>(services);
        RegisterCommand<AddFigureCliCommand, AddFigureCliCommand.Handler>(services);
        RegisterCommand<SetBboxCliCommand, SetBboxCliCommand.Handler>(services);
        RegisterCommand<SetCaptionCliCommand, SetCaptionCliCommand.Handler>(services);
        RegisterCommand<SetLabelCliCommand, SetLabelCliCommand.Handler>(services);
        RegisterCommand<SetCategoryCliCommand, SetCategoryCliCommand.Handler>(services);
        RegisterCommand<SetIndexCliCommand, SetIndexCliCommand.Handler>(services);
        RegisterCommand<MaskCliCommand, MaskCliCommand.Handler>(services);

        // Extraction, Page Rendering, ONNX Model & Runtime, Deletion, Lint & Docs
        RegisterCommand<ExtractCliCommand, ExtractCliCommand.Handler>(services);
        RegisterCommand<RenderCliCommand, RenderCliCommand.Handler>(services);
        RegisterCommand<ModelCliCommand, ModelCliCommand.Handler>(services);
        RegisterCommand<RuntimeCliCommand, RuntimeCliCommand.Handler>(services);
        RegisterCommand<DeleteCliCommand, DeleteCliCommand.Handler>(services);
        RegisterCommand<LintCliCommand, LintCliCommand.Handler>(services);
        RegisterCommand<DocsCliCommand, DocsCliCommand.Handler>(services);

        return services;
    }

    private static void RegisterCommand<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCommand,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(IServiceCollection services)
        where TCommand : CliCommand, new()
        where THandler : class, ICommandHandler
    {
        services.AddSingleton<CliCommand, TCommand>();
        services.AddScoped<THandler>();
    }
}

public static class CliApplicationRunner
{
    public static ServiceProvider BuildServiceProvider(string? appDataDirectory = null)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(opts =>
        {
            if (!string.IsNullOrWhiteSpace(appDataDirectory))
                opts.AppDataDirectory = Path.GetFullPath(appDataDirectory);
        });
        services.AddCli();
        return services.BuildServiceProvider();
    }

    public static async Task<int> ExecuteAsync(
        string[] args,
        IConsole console,
        string? appDataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedAppData = appDataDirectory ?? ExtractAppDataArg(args);
        using var provider = BuildServiceProvider(resolvedAppData);
        return await ExecuteWithProviderAsync(args, console, provider, cancellationToken);
    }

    public static async Task<int> ExecuteWithProviderAsync(
        string[] args,
        IConsole console,
        IServiceProvider provider,
        CancellationToken cancellationToken = default)
    {
        var filteredArgs = StripAppDataArg(args);

        // Dual Boot Mode 1: Interactive REPL when invoked with no arguments or explicit `repl`
        if (filteredArgs.Length == 0 ||
            (filteredArgs.Length == 1 && string.Equals(filteredArgs[0], "repl", StringComparison.OrdinalIgnoreCase)))
        {
            var repl = provider.GetRequiredService<InteractiveReplLoop>();
            return await repl.RunAsync(console, cancellationToken);
        }

        // Dual Boot Mode 2: Stateless One-Time CLI (supports single command or `->` fluent chain)
        var dispatcher = provider.GetRequiredService<CommandDispatcher>();
        var stages = CommandTokenizer.SplitArgsIntoChainStages(filteredArgs);
        var statelessContext = new CliSessionContext();
        return await dispatcher.ExecuteStagesAsync(stages, console, statelessContext, cancellationToken);
    }

    private static string? ExtractAppDataArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--") break;
            if (string.Equals(args[i], "--app-data", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string[] StripAppDataArg(string[] args)
    {
        var list = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                for (; i < args.Length; i++) list.Add(args[i]);
                break;
            }
            if (string.Equals(args[i], "--app-data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            list.Add(args[i]);
        }
        return [.. list];
    }
}
