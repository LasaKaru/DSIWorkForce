using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace DSI.WorkForce.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDsiData(this IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<TemplateRepository>();
        services.AddSingleton<ScanQueueRepository>();
        services.AddSingleton<MasterDataRepository>();
        services.AddSingleton<WorksheetRepository>();
        services.AddSingleton<ReportRepository>();
        return services;
    }
}
