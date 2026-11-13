using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuditPlatform.Client.Core.LocalStore;

/// <summary>Used only by <c>dotnet ef</c> to generate migrations; no database is opened.</summary>
internal sealed class DesignTimeLocalDbContextFactory : IDesignTimeDbContextFactory<LocalDbContext>
{
    public LocalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
