using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Documents;
using Melomg.Domain.Common;
using Melomg.Domain.Models;

namespace Melomg.Application.UseCases.Models;

public sealed record GetModelInfoQuery();

public sealed record ModelInfoResponse(
    string ModelName, string ModelPath, bool IsBuiltIn, double ConfidenceThreshold, double IouThreshold,
    int InputWidth, int InputHeight, int OpsetVersion, long IrVersion, IReadOnlyList<string> SupportedClasses,
    bool RuntimeInstalled = false, string InferenceEngine = "managed-fallback");

public sealed class GetModelInfoHandler(
    IModelSettingsRepository settingsRepository,
    IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ModelInfoResponse>> HandleAsync(GetModelInfoQuery query, CancellationToken cancellationToken = default)
    {
        var profile = await settingsRepository.GetActiveModelProfileAsync(cancellationToken);
        var status = await runtimeManager.GetStatusAsync(cancellationToken);
        return ToResponse(profile, status);
    }

    internal static ModelInfoResponse ToResponse(OnnxModelProfile profile, OnnxRuntimeStatus? status = null)
    {
        bool rtInstalled = status?.IsInstalled ?? false;
        string engine = rtInstalled && !profile.IsBuiltIn ? status!.ActiveInferenceEngine : "managed-fallback";
        return new ModelInfoResponse(
            profile.ModelName, profile.ModelPath, profile.IsBuiltIn, profile.ConfidenceThreshold, profile.IouThreshold,
            profile.InputWidth, profile.InputHeight, profile.OpsetVersion, profile.IrVersion, profile.SupportedClasses,
            rtInstalled, engine);
    }
}

public sealed record SetModelCommand(string ModelPath, double? ConfidenceThreshold = null, double? IouThreshold = null);

public sealed class SetModelHandler(
    IModelSettingsRepository settingsRepository, IOnnxModelInspector modelInspector, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ModelInfoResponse>> HandleAsync(SetModelCommand command, CancellationToken cancellationToken = default)
    {
        var inspected = await modelInspector.InspectAndValidateModelAsync(command.ModelPath, cancellationToken);
        if (!inspected.IsSuccess) return inspected.Error!;

        var current = await settingsRepository.GetActiveModelProfileAsync(cancellationToken);
        var customResult = ModelResolutionHelper.CreateCustomFromMetadata(
            inspected.Value, command.ConfidenceThreshold ?? current.ConfidenceThreshold, command.IouThreshold ?? current.IouThreshold);
        if (!customResult.IsSuccess) return customResult.Error!;

        await settingsRepository.SaveModelProfileAsync(customResult.Value, cancellationToken);
        return GetModelInfoHandler.ToResponse(customResult.Value, await runtimeManager.GetStatusAsync(cancellationToken));
    }
}

public sealed record SetModelThresholdCommand(double ConfidenceThreshold, double? IouThreshold = null);

public sealed class SetModelThresholdHandler(IModelSettingsRepository settingsRepository, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ModelInfoResponse>> HandleAsync(SetModelThresholdCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await settingsRepository.GetActiveModelProfileAsync(cancellationToken);
        var setResult = profile.SetThresholds(command.ConfidenceThreshold, command.IouThreshold);
        if (!setResult.IsSuccess) return setResult.Error!;

        await settingsRepository.SaveModelProfileAsync(profile, cancellationToken);
        return GetModelInfoHandler.ToResponse(profile, await runtimeManager.GetStatusAsync(cancellationToken));
    }
}

public sealed record ResetModelCommand();

public sealed class ResetModelHandler(IModelSettingsRepository settingsRepository, IOnnxRuntimeManager runtimeManager)
{
    public async Task<Result<ModelInfoResponse>> HandleAsync(ResetModelCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await settingsRepository.ResetModelProfileAsync(cancellationToken);
        return GetModelInfoHandler.ToResponse(profile, await runtimeManager.GetStatusAsync(cancellationToken));
    }
}
