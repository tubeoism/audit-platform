using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.LocalStore;

public sealed class LocalDbContext(DbContextOptions<LocalDbContext> options) : DbContext(options)
{
    public DbSet<OutboxChangeRow> Outbox => Set<OutboxChangeRow>();
    public DbSet<SyncStateRow> SyncState => Set<SyncStateRow>();
    public DbSet<LocalEngagementRow> Engagements => Set<LocalEngagementRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxChangeRow>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.ChangeId);
            e.HasIndex(x => new { x.EngagementId, x.State, x.CreatedAt });
            e.Property(x => x.State).HasConversion<string>();
            // SQLite cannot order by DateTimeOffset natively; store as UTC ticks.
            e.Property(x => x.CreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<SyncStateRow>(e =>
        {
            e.ToTable("sync_state");
            e.HasKey(x => x.EngagementId);
        });

        modelBuilder.Entity<LocalEngagementRow>(e =>
        {
            e.ToTable("engagements");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
        });
    }
}
