using System.Text.Json.Serialization;
using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Application.UseCases.Models;

namespace Melomg.Cli.Abstractions;

internal sealed record CliJsonEnvelope<T>(bool Success, T Data);

internal sealed record CliJsonErrorBody(
    string Code,
    string Description,
    string Type,
    string? Path = null,
    string? Hint = null);

internal sealed record CliJsonErrorEnvelope(bool Success, CliJsonErrorBody Error);

internal sealed record PrimePayload(string Tool, string Version, string Grammar);

internal sealed record SpecTopicItem(string Topic, string Summary);

internal sealed record SpecEntryPayload(string Topic, string Content);

internal sealed record SpecResponse(
    IReadOnlyList<SpecTopicItem>? Topics = null,
    IReadOnlyList<SpecEntryPayload>? Specs = null);

internal sealed record VersionPayload(string Tool, string Version, string Runtime, string Os);

internal sealed record BatchOpResultItem(int Index, string Op, string? Id, bool Success);

internal sealed record BatchResponse(int ExecutedCount, IReadOnlyList<BatchOpResultItem> Results);

internal sealed record PatchJsonInput(
    string? Title = null,
    double[]? Bbox = null,
    string? Caption = null,
    string? Label = null,
    string? Num = null,
    string? Category = null,
    string? Move = null);

internal sealed record AddFigureJsonInput(
    int? Page = null,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    double[]? Bbox = null,
    string? Category = null,
    string? Caption = null,
    string? Label = null,
    string? Num = null,
    int? Index = null);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliJsonErrorEnvelope))]
[JsonSerializable(typeof(CliJsonEnvelope<PrimePayload>))]
[JsonSerializable(typeof(CliJsonEnvelope<SpecResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<VersionPayload>))]
[JsonSerializable(typeof(CliJsonEnvelope<IngestPdfResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<IReadOnlyList<DocumentListItem>>))]
[JsonSerializable(typeof(CliJsonEnvelope<GetTargetResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<DeleteTargetResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ExtractFiguresResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<RenderPagesResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ModelInfoResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<OnnxRuntimeStatus>))]
[JsonSerializable(typeof(CliJsonEnvelope<OnnxRuntimeUninstallResult>))]
[JsonSerializable(typeof(CliJsonEnvelope<LintReportResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AddManualFigureResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<PatchTargetResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<FigureDetailResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ListFigureMasksResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AddFigureMaskResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AutoMaskFigureLabelsResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<RemoveFigureMaskResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ClearFigureMasksResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<BatchResponse>))]
[JsonSerializable(typeof(PatchJsonInput))]
[JsonSerializable(typeof(AddFigureJsonInput))]
internal sealed partial class MelomgCliJsonContext : JsonSerializerContext;
