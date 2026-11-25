using System.Diagnostics.CodeAnalysis;
using Melomg.Application;
using Melomg.Cli.Abstractions;
using Melomg.Cli.Commands;
using Melomg.Cli.Parsing;
using Melomg.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Cli.Hosting;

public static class CliServiceRegistration
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        services.AddSingleton<CommandDispatcher>();

        // Discovery & Metadata
        RegisterCommand<PrimeCliCommand, PrimeCliCommand.Handler>(services);
        RegisterCommand<SpecCliCommand, SpecCliCommand.Handler>(services);
        RegisterCommand<VersionCliCommand, VersionCliCommand.Handler>(services);

        // Documents & Inspection
        RegisterCommand<IngestCliCommand, IngestCliCommand.Handler>(services);
        RegisterCommand<GetCliCommand, GetCliCommand.Handler>(services);
        RegisterCommand<LsCliCommand, LsCliCommand.Handler>(services);

        // Figures, Masks & Refinement
        RegisterCommand<PatchCliCommand, PatchCliCommand.Handler>(services);
        RegisterCommand<AddCliCommand, AddCliCommand.Handler>(services);
        RegisterCommand<MaskCliCommand, MaskCliCommand.Handler>(services);

        // Extraction, Page Rendering, Batch, Check & Deletion
        RegisterCommand<ExtractCliCommand, ExtractCliCommand.Handler>(services);
        RegisterCommand<RenderCliCommand, RenderCliCommand.Handler>(services);
        RegisterCommand<BatchCliCommand, BatchCliCommand.Handler>(services);
        RegisterCommand<RmCliCommand, RmCliCommand.Handler>(services);
        RegisterCommand<CheckCliCommand, CheckCliCommand.Handler>(services);

        // ONNX Model & Runtime Setup
        RegisterCommand<ModelCliCommand, ModelCliCommand.Handler>(services);
        RegisterCommand<RuntimeCliCommand, RuntimeCliCommand.Handler>(services);

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
            var resolved = appDataDirectory ?? CliCommandHelpers.ResolveEnvAppData();
            if (!string.IsNullOrWhiteSpace(resolved))
                opts.AppDataDirectory = Path.GetFullPath(resolved);
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
        var resolvedAppData = appDataDirectory ?? ExtractAppDataArg(args) ?? CliCommandHelpers.ResolveEnvAppData();
        var filteredArgs = StripAppDataArg(args);
        using var provider = BuildServiceProvider(resolvedAppData);
        return await ExecuteWithProviderAsync(filteredArgs, console, provider, cancellationToken);
    }

    public static async Task<int> ExecuteWithProviderAsync(
        string[] args,
        IConsole console,
        IServiceProvider provider,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = provider.GetRequiredService<CommandDispatcher>();
        var tokens = CommandTokenizer.FromArgs(args);
        return await dispatcher.ExecuteAsync(tokens, console, cancellationToken);
    }

    private static string? ExtractAppDataArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") break;
            if (string.Equals(args[i], "--app-data", StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            if (IsValueTakingOptionToken(args[i]))
                i++;
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
            if (IsValueTakingOptionToken(args[i]) && i + 1 < args.Length)
                list.Add(args[++i]);
        }
        return [.. list];
    }

    private static bool IsValueTakingOptionToken(string token) =>
        string.Equals(token, "-o", StringComparison.Ordinal) ||
        CommandTokenizer.ValueOptions.Contains(token);
}
