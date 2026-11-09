using AuditPlatform.AuditLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditPlatform.Master.Data;

public static class MasterDataServiceCollectionExtensions
{
    public static IServiceCollection AddMasterData(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<MasterDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IAuditLog, PostgresAuditLog>();
        return services;
    }
}
