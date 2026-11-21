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
            .AddScoped<IngestPdfHandler>().AddScoped<OpenProjectHandler>().AddScoped<UseDocumentHandler>()
            .AddScoped<ListDocumentsHandler>().AddScoped<GetDocumentInfoHandler>().AddScoped<SetDocumentTitleHandler>()
            .AddScoped<ReanalyzeDocumentHandler>().AddScoped<DeleteDocumentHandler>()
            .AddScoped<ListFiguresHandler>().AddScoped<InspectFigureHandler>().AddScoped<AddManualFigureHandler>()
            .AddScoped<SetFigureBoundingBoxHandler>().AddScoped<SetFigureCaptionHandler>().AddScoped<SetFigureLabelHandler>()
            .AddScoped<SetFigureCategoryHandler>().AddScoped<ReorderFigureHandler>().AddScoped<DeleteFigureHandler>()
            .AddScoped<ListFigureMasksHandler>().AddScoped<AddFigureMaskHandler>().AddScoped<AutoMaskFigureLabelsHandler>()
            .AddScoped<RemoveFigureMaskHandler>().AddScoped<ClearFigureMasksHandler>()
            .AddScoped<ExtractFiguresHandler>().AddScoped<RenderPagesHandler>()
            .AddScoped<GetModelInfoHandler>().AddScoped<SetModelHandler>().AddScoped<SetModelThresholdHandler>()
            .AddScoped<ResetModelHandler>().AddScoped<GetRuntimeStatusHandler>().AddScoped<InstallRuntimeHandler>()
            .AddScoped<UninstallRuntimeHandler>()
            .AddScoped<GetDeleteImpactHandler>().AddScoped<LintDocumentHandler>();

        return services;
    }
}
