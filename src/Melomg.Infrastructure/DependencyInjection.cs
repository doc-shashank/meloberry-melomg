using Melomg.Application.Abstractions;
using Melomg.Infrastructure.Extraction;
using Melomg.Infrastructure.Onnx;
using Melomg.Infrastructure.Pdf;
using Melomg.Infrastructure.Persistence;
using Melomg.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, Action<MelomgStorageOptions>? configure = null)
    {
        var options = new MelomgStorageOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IWorkingDirectoryProvider, WorkingDirectoryProvider>();
        services.AddSingleton<IIdGenerator, MonotonicTimestampIdGenerator>();

        services.AddSingleton<OnnxProtobufEngine>();
        services.AddSingleton<IOnnxModelInspector>(sp => sp.GetRequiredService<OnnxProtobufEngine>());
        services.AddSingleton<NativeOnnxRuntimeSessionHost>();
        services.AddSingleton<OnnxRuntimeManager>();
        services.AddSingleton<IOnnxRuntimeManager>(sp => sp.GetRequiredService<OnnxRuntimeManager>());
        services.AddSingleton<IPdfPageRenderer, WindowsPdfPageRenderer>();

        services.AddSingleton<HybridOnnxPdfIngestionEngine>();
        services.AddSingleton<IPdfIngestionEngine>(sp => sp.GetRequiredService<HybridOnnxPdfIngestionEngine>());
        services.AddSingleton<IFigureLabelDetector>(sp => sp.GetRequiredService<HybridOnnxPdfIngestionEngine>());

        services.AddSingleton<PngJpegFigureExtractor>();
        services.AddSingleton<IFigureImageExtractor>(sp => sp.GetRequiredService<PngJpegFigureExtractor>());
        services.AddSingleton<IPageSnapshotExporter>(sp => sp.GetRequiredService<PngJpegFigureExtractor>());

        services.AddSingleton<SqliteModelSettingsRepository>();
        services.AddSingleton<IModelSettingsRepository>(sp => sp.GetRequiredService<SqliteModelSettingsRepository>());
        services.AddScoped<IDocumentWorkspaceRepository, SqliteDocumentWorkspaceRepository>();
        return services;
    }
}
