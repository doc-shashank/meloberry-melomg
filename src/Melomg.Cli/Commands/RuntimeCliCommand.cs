using System.Text;
using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Runtime;
using Melomg.Cli.Abstractions;

namespace Melomg.Cli.Commands;

public sealed class RuntimeCliCommand : CliCommand
{
    private const string UsageText = "runtime <status|install|uninstall> [--provider directml|cpu] [--skip-model] [--keep-model] [--force]";

    public RuntimeCliCommand()
        : base(new CommandDescriptor(
            Verb: "runtime",
            Usage: UsageText,
            Description: "Inspects, installs, or deletes AppData-hosted ONNX Runtime native binaries and pretrained DocLayout-YOLO weights.",
            HandlerType: typeof(Handler),
            Category: CommandCategory.Models,
            Examples:
            [
                "melomg runtime status",
                "melomg runtime install",
                "melomg runtime install --provider cpu --skip-model",
                "melomg runtime uninstall"
            ]))
    {
    }

    public sealed class Handler(
        GetRuntimeStatusHandler getStatus,
        InstallRuntimeHandler installRuntime,
        UninstallRuntimeHandler uninstallRuntime) : ICommandHandler
    {
        public async Task<int> InvokeAsync(CliInvocationContext context)
        {
            if (context.PositionalArgs.Count == 0)
                return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Usage: {UsageText}", context.GlobalOptions.Json);

            string sub = context.PositionalArgs[0].Trim().ToLowerInvariant();
            switch (sub)
            {
                case "status":
                {
                    var res = await getStatus.HandleAsync(new GetRuntimeStatusQuery(), context.CancellationToken);
                    if (!res.IsSuccess)
                        return res.ToExitCode(context.Console, context.GlobalOptions.Json);

                    CliCommandHelpers.WriteOutput(context, res.Value, FormatStatus(res.Value));
                    return ExitCodes.Success;
                }

                case "install":
                {
                    string provider = context.NamedOptions.TryGetValue("provider", out var pVal) && !string.IsNullOrWhiteSpace(pVal)
                        ? pVal
                        : "directml";
                    bool includeModel = !context.NamedOptions.ContainsKey("skip-model");
                    bool force = context.NamedOptions.ContainsKey("force");

                    var res = await installRuntime.HandleAsync(
                        new InstallRuntimeCommand(provider, includeModel, force),
                        msg =>
                        {
                            if (!context.GlobalOptions.Silent)
                                context.Console.Error.WriteLine(msg);
                        },
                        context.CancellationToken);

                    if (!res.IsSuccess)
                        return res.ToExitCode(context.Console, context.GlobalOptions.Json);

                    CliCommandHelpers.WriteOutput(context, res.Value, FormatStatus(res.Value));
                    return ExitCodes.Success;
                }

                case "uninstall":
                {
                    bool keepModel = context.NamedOptions.ContainsKey("keep-model");
                    var res = await uninstallRuntime.HandleAsync(new UninstallRuntimeCommand(keepModel), context.CancellationToken);
                    if (!res.IsSuccess)
                        return res.ToExitCode(context.Console, context.GlobalOptions.Json);

                    var u = res.Value;
                    double freedMb = u.FreedBytes / (1024.0 * 1024.0);
                    var sb = new StringBuilder();
                    sb.Append($"Removed {u.RemovedFileCount} file(s) ({freedMb:F2} MB freed)");
                    if (u.ResetModelToFallback)
                        sb.Append("; reverted active model to built-in fallback");
                    sb.Append('.');
                    if (u.PendingCleanupPaths.Count > 0)
                        sb.Append($" {u.PendingCleanupPaths.Count} locked file(s) scheduled for cleanup on next run.");

                    CliCommandHelpers.WriteOutput(context, u, sb.ToString());
                    return ExitCodes.Success;
                }

                default:
                    return CliResultExtensions.WriteValidationError(context.Console, "Cli.InvalidArgs", $"Unknown runtime subcommand '{sub}'. Use status, install, or uninstall.", context.GlobalOptions.Json);
            }
        }

        private static string FormatStatus(OnnxRuntimeStatus s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"ONNX Runtime:     {(s.IsInstalled ? $"installed (v{s.InstalledVersion}, provider={s.InstalledProvider})" : "not installed")}");
            sb.AppendLine($"Inference Engine: {s.ActiveInferenceEngine}");
            sb.AppendLine($"Runtime Dir:      {s.RuntimeDirectory}");
            sb.AppendLine($"Active Model:     {s.ActiveModelName ?? "(default)"} ({s.ActiveModelPath ?? "-"})");
            sb.AppendLine($"DocLayout Model:  {(s.PretrainedModelInstalled ? $"installed ({s.PretrainedModelPath})" : "not installed")}");
            sb.AppendLine($"Installed Size:   {s.TotalInstalledBytes / (1024.0 * 1024.0):F2} MB ({s.Files.Count} file(s))");
            foreach (var f in s.Files)
            {
                string shaShort = string.IsNullOrEmpty(f.Sha256) ? "-" : f.Sha256[..Math.Min(12, f.Sha256.Length)];
                sb.AppendLine($"  - {f.FileName,-38} {f.SizeBytes,10:N0} B  sha256:{shaShort}");
            }
            if (!string.IsNullOrWhiteSpace(s.DiagnosticMessage))
                sb.AppendLine($"Note:             {s.DiagnosticMessage}");
            return sb.ToString().TrimEnd();
        }
    }
}
