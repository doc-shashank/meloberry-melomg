using System.Text.Json.Serialization;
using Melomg.Application.Abstractions;
using Melomg.Domain.Documents;

namespace Melomg.Infrastructure.Serialization;

internal sealed record FigureSidecarMetadata(
    string FigureId, string DocumentId, int Index, int PageNumber, int PageFigureIndex,
    string Category, double Confidence, string Source, string RenderEngine, string? Label,
    string? VocabPrefix, string? FigureNumber, string? Caption,
    bool IncludedCaption, BoundingBox BoundingBoxPts, BoundingBox? CaptionBoundingBoxPts,
    BoundingBox EffectiveCropBoxPts, int PixelWidth, int PixelHeight, int Dpi, int Quality,
    int PaddingPx, string Format, IReadOnlyList<AppliedMaskInfo> AppliedMasks, string OutputImagePath,
    DateTimeOffset ExtractedAtUtc);

internal sealed record OnnxRuntimeManifestFile(string FileName, long SizeBytes, string Sha256);

internal sealed record OnnxRuntimeManifest(string Version, string Provider, DateTimeOffset InstalledAtUtc, IReadOnlyList<OnnxRuntimeManifestFile> Files);

internal sealed record FigureManifestEntry(
    string FigureId,
    string? Num,
    int Page,
    string Category,
    string? Label,
    string? Caption,
    double Confidence,
    string Path,
    string FileName,
    int WidthPx,
    int HeightPx);

internal sealed record FigureManifestDocument(
    string DocumentId,
    DateTimeOffset GeneratedAtUtc,
    string OutputDirectory,
    Dictionary<string, FigureManifestEntry> Figures);

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class ModelSettingsJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FigureSidecarMetadata))]
internal sealed partial class FigureMetadataJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FigureManifestDocument))]
internal sealed partial class FigureManifestJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OnnxRuntimeManifest))]
internal sealed partial class RuntimeManifestJsonContext : JsonSerializerContext;
