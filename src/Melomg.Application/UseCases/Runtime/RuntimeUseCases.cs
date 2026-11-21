using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Documents;
using Melomg.Domain.Common;
using Melomg.Domain.Errors;

namespace Melomg.Application.UseCases.Runtime;

internal static class RuntimeModelActivationHelper
{
    public static async Task TryPromotePretrainedModelAsync(
        IModelSettingsRepository modelSettings, IOnnxModelInspector modelInspector,
        OnnxRuntimeStatus status, CancellationToken cancellationToken)
    {
        if (!status.IsInstalled || !status.PretrainedModelInstalled ||
            string.IsNullOrWhiteSpace(status.PretrainedModelPath) || !File.Exists(status.PretrainedModelPath))
            return;

        var current = await modelSettings.GetActiveModelProfileAsync(cancellationToken);
        if (!current.IsBuiltIn) return;

        var inspected = await modelInspector.InspectAndValidateModelAsync(status.PretrainedModelPath, cancellationToken);
        if (!inspected.IsSuccess) return;

        var profileResult = ModelResolutionHelper.CreateCustomFromMetadata(inspected.Value, current.ConfidenceThreshold, current.IouThreshold);
        if (profileResult.IsSuccess)
            await modelSettings.SaveModelProfileAsync(profileResult.Value, cancellationToken);
    }

    public static async Task<OnnxRuntimeStatus> EnrichStatusAsync(
        OnnxRuntimeStatus status, IModelSettingsRepository modelSettings, CancellationToken cancellationToken)
    {
        var active = await modelSettings.GetActiveModelProfileAsync(cancellationToken);
        return status with { ActiveModelName = active.ModelName, ActiveModelPath = active.ModelPath };
    }
}

public sealed record GetRuntimeStatusQuery();

public sealed class GetRuntimeStatusHandler(IOnnxRuntimeManager runtimeManager, IModelSettingsRepository modelSettings)
{
    public async Task<Result<OnnxRuntimeStatus>> HandleAsync(GetRuntimeStatusQuery query, CancellationToken cancellationToken = default)
    {
        var status = await runtimeManager.GetStatusAsync(cancellationToken);
        return Result<OnnxRuntimeStatus>.Success(await RuntimeModelActivationHelper.EnrichStatusAsync(status, modelSettings, cancellationToken));
    }
}

public sealed record InstallRuntimeCommand(string Provider = "directml", bool IncludeModel = true, bool Force = false);

public sealed class InstallRuntimeHandler(
    IOnnxRuntimeManager runtimeManager, IModelSettingsRepository modelSettings, IOnnxModelInspector modelInspector)
{
    public async Task<Result<OnnxRuntimeStatus>> HandleAsync(
        InstallRuntimeCommand command, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        string normalizedProvider = (command.Provider ?? "directml").Trim().ToLowerInvariant();
        if (normalizedProvider is not ("directml" or "cpu"))
            return RuntimeErrors.InvalidProvider(command.Provider ?? string.Empty);

        var installResult = await runtimeManager.InstallAsync(
            new OnnxRuntimeInstallRequest(normalizedProvider, command.IncludeModel, command.Force), onProgress, cancellationToken);
        if (!installResult.IsSuccess) return installResult.Error!;

        await RuntimeModelActivationHelper.TryPromotePretrainedModelAsync(modelSettings, modelInspector, installResult.Value, cancellationToken);

        var updated = await runtimeManager.GetStatusAsync(cancellationToken);
        return Result<OnnxRuntimeStatus>.Success(await RuntimeModelActivationHelper.EnrichStatusAsync(updated, modelSettings, cancellationToken));
    }
}

public sealed record UninstallRuntimeCommand(bool KeepModel = false);

public sealed class UninstallRuntimeHandler(IOnnxRuntimeManager runtimeManager, IModelSettingsRepository modelSettings)
{
    public async Task<Result<OnnxRuntimeUninstallResult>> HandleAsync(UninstallRuntimeCommand command, CancellationToken cancellationToken = default)
    {
        var result = await runtimeManager.UninstallAsync(command.KeepModel, cancellationToken);
        if (!result.IsSuccess) return result.Error!;

        var currentModel = await modelSettings.GetActiveModelProfileAsync(cancellationToken);
        bool resetToFallback = false;
        if (!currentModel.IsBuiltIn && !File.Exists(currentModel.ModelPath))
        {
            await modelSettings.ResetModelProfileAsync(cancellationToken);
            resetToFallback = true;
        }

        return Result<OnnxRuntimeUninstallResult>.Success(result.Value with { ResetModelToFallback = resetToFallback });
    }
}
