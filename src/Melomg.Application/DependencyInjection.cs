using Melomg.Application.Addressing;
using Melomg.Application.UseCases.Diagnostics;
using Melomg.Application.UseCases.Documents;
using Melomg.Application.UseCases.Extraction;
using Melomg.Application.UseCases.Figures;
using Melomg.Application.UseCases.Models;
using Melomg.Application.UseCases.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Melomg.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services
            .AddScoped<AddressResolver>()
            .AddScoped<IngestPdfHandler>()
            .AddScoped<ListDocumentsHandler>()
            .AddScoped<GetTargetHandler>()
            .AddScoped<DeleteTargetHandler>()
            .AddScoped<AddManualFigureHandler>()
            .AddScoped<PatchTargetHandler>()
            .AddScoped<ListFigureMasksHandler>()
            .AddScoped<AddFigureMaskHandler>()
            .AddScoped<AutoMaskFigureLabelsHandler>()
            .AddScoped<RemoveFigureMaskHandler>()
            .AddScoped<ClearFigureMasksHandler>()
            .AddScoped<ExtractFiguresHandler>()
            .AddScoped<RenderPagesHandler>()
            .AddScoped<GetModelInfoHandler>()
            .AddScoped<SetModelHandler>()
            .AddScoped<SetModelThresholdHandler>()
            .AddScoped<ResetModelHandler>()
            .AddScoped<GetRuntimeStatusHandler>()
            .AddScoped<InstallRuntimeHandler>()
            .AddScoped<UninstallRuntimeHandler>()
            .AddScoped<LintDocumentHandler>();

        return services;
    }
}
