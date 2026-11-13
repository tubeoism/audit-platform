using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Data;

public sealed class MasterDbContext(DbContextOptions<MasterDbContext> options) : DbContext(options)
{
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<EngagementRow> Engagements => Set<EngagementRow>();
    public DbSet<EngagementMemberRow> EngagementMembers => Set<EngagementMemberRow>();
    public DbSet<ProcedureTemplateRow> ProcedureTemplates => Set<ProcedureTemplateRow>();
    public DbSet<ChangeRow> Changes => Set<ChangeRow>();
    public DbSet<SyncReceiptRow> SyncReceipts => Set<SyncReceiptRow>();
    public DbSet<SyncDocumentRow> SyncDocuments => Set<SyncDocumentRow>();
    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();
    public DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();
    public DbSet<EvidenceObjectRow> EvidenceObjects => Set<EvidenceObjectRow>();
    public DbSet<EvidenceIntakeRow> EvidenceIntake => Set<EvidenceIntakeRow>();
    public DbSet<UserTokenRow> UserTokens => Set<UserTokenRow>();
    public DbSet<RecoveryCodeRow> RecoveryCodes => Set<RecoveryCodeRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();
    public DbSet<StepUpGrantRow> StepUpGrants => Set<StepUpGrantRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<UserRow>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PasswordHash).HasMaxLength(400);
            e.Property(x => x.TotpSecret).HasMaxLength(400);
        });

        modelBuilder.Entity<UserTokenRow>(e =>
        {
            e.ToTable("user_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Purpose });
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<RecoveryCodeRow>(e =>
        {
            e.ToTable("recovery_codes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique();
            e.Property(x => x.CodeHash).HasMaxLength(64).IsFixedLength();
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<SessionRow>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.InstallationId });
            e.Property(x => x.DeviceName).HasMaxLength(100);
            e.Property(x => x.RevokedReason).HasMaxLength(40);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<RefreshTokenRow>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            e.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<StepUpGrantRow>(e =>
        {
            e.ToTable("step_up_grants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Purpose).HasMaxLength(20);
            e.Property(x => x.ContentHash).HasMaxLength(128);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<EngagementRow>(e =>
        {
            e.ToTable("engagements");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.ClientName).HasMaxLength(300);
            e.Property(x => x.ContractReference).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.EngagementId);
        });

        modelBuilder.Entity<EngagementMemberRow>(e =>
        {
            e.ToTable("engagement_members");
            e.HasKey(x => new { x.EngagementId, x.UserId, x.Role });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<ProcedureTemplateRow>(e =>
        {
            e.ToTable("procedure_templates");
            e.HasKey(x => new { x.Code, x.Version, x.Language });
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.AreaCode).HasMaxLength(10);
        });

        modelBuilder.Entity<ChangeRow>(e =>
        {
            e.ToTable("changes");
            e.HasKey(x => new { x.EngagementId, x.Sequence });
            e.HasIndex(x => x.ChangeId).IsUnique();
            e.HasIndex(x => new { x.EngagementId, x.DocumentId });
            e.Property(x => x.PayloadKind).HasMaxLength(40);
            e.HasOne<EngagementRow>().WithMany().HasForeignKey(x => x.EngagementId);
        });

        modelBuilder.Entity<SyncReceiptRow>(e =>
        {
            e.ToTable("sync_receipts");
            e.HasKey(x => x.ChangeId);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.HasOne<EngagementRow>().WithMany().HasForeignKey(x => x.EngagementId);
        });

        modelBuilder.Entity<SyncDocumentRow>(e =>
        {
            e.ToTable("sync_documents");
            e.HasKey(x => x.DocumentId);
            e.HasIndex(x => x.EngagementId);
            e.Property(x => x.TemplateCode).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.State).HasColumnType("text");
            e.HasOne<EngagementRow>().WithMany().HasForeignKey(x => x.EngagementId);
        });

        modelBuilder.Entity<NotificationRow>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt).HasFilter("sent_at IS NULL");
            e.HasIndex(x => new { x.RecipientUserId, x.CreatedAt });
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.LastError).HasMaxLength(400);
            e.HasOne<EngagementRow>().WithMany().HasForeignKey(x => x.EngagementId);
        });

        modelBuilder.Entity<AuditLogRow>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).ValueGeneratedNever();
            // text, not jsonb: jsonb re-serialises JSON, which would change the bytes the hash was computed over.
            e.Property(x => x.PayloadJson).HasColumnType("text");
            e.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Hash).HasMaxLength(64).IsFixedLength();
            e.HasIndex(x => x.EngagementId);
        });

        modelBuilder.Entity<EvidenceObjectRow>(e =>
        {
            e.ToTable("evidence_objects");
            e.HasKey(x => x.Hash);
            e.Property(x => x.Hash).HasMaxLength(64).IsFixedLength();
        });

        modelBuilder.Entity<EvidenceIntakeRow>(e =>
        {
            e.ToTable("evidence_intake");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EngagementId, x.Hash });
            e.Property(x => x.Hash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.OriginalFileName).HasMaxLength(400);
            e.Property(x => x.Source).HasMaxLength(100);
            e.HasOne<EvidenceObjectRow>().WithMany().HasForeignKey(x => x.Hash);
        });
    }
}
