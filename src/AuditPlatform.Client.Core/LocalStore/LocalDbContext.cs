using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.LocalStore;

public sealed class LocalDbContext(DbContextOptions<LocalDbContext> options) : DbContext(options)
{
    public DbSet<OutboxChangeRow> Outbox => Set<OutboxChangeRow>();
    public DbSet<SyncStateRow> SyncState => Set<SyncStateRow>();
    public DbSet<LocalEngagementRow> Engagements => Set<LocalEngagementRow>();
    public DbSet<LocalDocumentRow> Documents => Set<LocalDocumentRow>();
    public DbSet<ReplicaChangeRow> ReplicaLog => Set<ReplicaChangeRow>();
    public DbSet<LocalMemberRow> Members => Set<LocalMemberRow>();
    public DbSet<LocalTemplateRow> Templates => Set<LocalTemplateRow>();
    public DbSet<LocalProfileRow> Profile => Set<LocalProfileRow>();
    public DbSet<LocalEvidenceRow> Evidence => Set<LocalEvidenceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxChangeRow>(e =>
        {
            e.ToTable("outbox");
            // INTEGER PRIMARY KEY AUTOINCREMENT: assigned by SQLite, never reused.
            e.HasKey(x => x.LocalSequence);
            e.Property(x => x.LocalSequence).ValueGeneratedOnAdd();
            e.HasIndex(x => x.ChangeId).IsUnique();
            e.HasIndex(x => new { x.EngagementId, x.State, x.LocalSequence });
            e.Property(x => x.State).HasConversion<string>();
            // SQLite has no DateTimeOffset; store as UTC ticks.
            e.Property(x => x.CreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<LocalDocumentRow>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.DocumentId);
            e.HasIndex(x => x.EngagementId);
        });

        modelBuilder.Entity<ReplicaChangeRow>(e =>
        {
            e.ToTable("replica_log");
            e.HasKey(x => new { x.EngagementId, x.Sequence });
            e.HasIndex(x => x.ChangeId).IsUnique();
            e.HasIndex(x => new { x.EngagementId, x.DocumentId });
            e.Property(x => x.ClientCreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            e.Property(x => x.ReceivedAt).HasConversion(v => v == null ? (long?)null : v.Value.UtcTicks, v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));
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

        modelBuilder.Entity<LocalMemberRow>(e =>
        {
            e.ToTable("engagement_members");
            e.HasKey(x => new { x.EngagementId, x.UserId });
        });

        modelBuilder.Entity<LocalTemplateRow>(e =>
        {
            e.ToTable("templates");
            e.HasKey(x => new { x.Code, x.Version, x.Language });
        });

        modelBuilder.Entity<LocalEvidenceRow>(e =>
        {
            e.ToTable("evidence");
            e.HasKey(x => new { x.EngagementId, x.Hash });
            e.HasIndex(x => new { x.EngagementId, x.Upload });
            e.Property(x => x.Upload).HasConversion<string>();
            e.Property(x => x.AddedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        modelBuilder.Entity<LocalProfileRow>(e =>
        {
            e.ToTable("profile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
