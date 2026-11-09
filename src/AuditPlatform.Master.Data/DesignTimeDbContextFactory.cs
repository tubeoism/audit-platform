using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuditPlatform.Master.Data;

/// <summary>Used only by <c>dotnet ef</c> to generate migrations; no database connection is opened.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql("Host=localhost;Database=audit_master_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
