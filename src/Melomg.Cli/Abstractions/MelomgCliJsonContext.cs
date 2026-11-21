using System.Text.Json.Serialization;
using Melomg.Application.Abstractions;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Application.UseCases.Models;

namespace Melomg.Cli.Abstractions;

internal sealed record CliJsonEnvelope<T>(bool Success, T Data);

internal sealed record CliJsonErrorBody(string Code, string Description, string Type);

internal sealed record CliJsonErrorEnvelope(bool Success, CliJsonErrorBody Error);

internal sealed record DeleteAbortedPayload(bool Aborted, string TargetId);

internal sealed record RuntimeUninstallAbortedPayload(bool Aborted, string Action);

internal sealed record DocsTopicAllItem(string Topic, string FileName, string Title, string Summary, string Content);

internal sealed record DocsTopicIndexItem(string Topic, string FileName, string Title, string Summary);

internal sealed record DocsTopicDetailPayload(string Topic, string FileName, string Title, string Content);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliJsonErrorEnvelope))]
internal sealed partial class MelomgCliCompactJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliJsonEnvelope<IngestPdfResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<OpenProjectResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<UseDocumentResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<IReadOnlyList<DocumentListItem>>))]
[JsonSerializable(typeof(CliJsonEnvelope<DocumentInfoResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<SetDocumentTitleResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ReanalyzeDocumentResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ExtractFiguresResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<RenderPagesResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ModelInfoResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<OnnxRuntimeStatus>))]
[JsonSerializable(typeof(CliJsonEnvelope<OnnxRuntimeUninstallResult>))]
[JsonSerializable(typeof(CliJsonEnvelope<RuntimeUninstallAbortedPayload>))]
[JsonSerializable(typeof(CliJsonEnvelope<DeleteAbortedPayload>))]
[JsonSerializable(typeof(CliJsonEnvelope<DeleteDocumentResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<DeleteFigureResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<LintReportResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<List<DocsTopicAllItem>>))]
[JsonSerializable(typeof(CliJsonEnvelope<List<DocsTopicIndexItem>>))]
[JsonSerializable(typeof(CliJsonEnvelope<DocsTopicDetailPayload>))]
[JsonSerializable(typeof(CliJsonEnvelope<ListFiguresResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<FigureDetailResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AddManualFigureResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<SetFigureBoundingBoxResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<SetFigureCaptionResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<SetFigureLabelResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<SetFigureCategoryResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ReorderFigureResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ListFigureMasksResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AddFigureMaskResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<AutoMaskFigureLabelsResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<RemoveFigureMaskResponse>))]
[JsonSerializable(typeof(CliJsonEnvelope<ClearFigureMasksResponse>))]
internal sealed partial class MelomgCliJsonContext : JsonSerializerContext;
