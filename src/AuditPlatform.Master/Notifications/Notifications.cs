using System.Text.Json;
using System.Threading.Channels;
using AuditPlatform.AuditLog;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Engagements;
using AuditPlatform.Master.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Notifications;

/// <summary>Wakes the dispatcher as soon as a transaction that created notifications has committed.</summary>
public sealed class NotificationSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Pulse() => _channel.Writer.TryWrite(true);

    /// <summary>Returns when pulsed or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: the periodic sweep picks up anything a pulse missed (retries, a restart).
        }
    }
}

/// <summary>
/// Sends the notifications the master owes ("your sign-off was broken by an edit", "the engagement was locked"). Rows are written by the
/// transaction that caused them, so a notification cannot be lost between the event and the mail; delivery is
/// at-least-once with retries, and the row stays as the record that the person was told.
/// </summary>
public sealed class NotificationSender(MasterDbContext db, IEmailSender mail, IAuditLog auditLog, TimeProvider clock, ILogger<NotificationSender> log)
{
    public const int MaxAttempts = 12;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Tries every notification that is due. Returns the number sent.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var retryBefore = now - RetryAfter;
        var due = await db.Notifications.AsNoTracking()
            .Where(n => n.SentAt == null && n.Attempts < MaxAttempts && (n.LastAttemptAt == null || n.LastAttemptAt < retryBefore))
            .OrderBy(n => n.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var n in due)
        {
            // Claim first, so two dispatchers (or a dispatcher and a restart) do not send the same mail at the same time.
            var claimed = await db.Notifications
                .Where(x => x.Id == n.Id && x.SentAt == null && x.Attempts == n.Attempts)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, n.Attempts + 1).SetProperty(x => x.LastAttemptAt, now), cancellationToken);
            if (claimed != 1)
            {
                continue;
            }

            var error = await TrySendAsync(n, cancellationToken);
            if (error is null)
            {
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                await db.Notifications.Where(x => x.Id == n.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SentAt, now).SetProperty(x => x.LastError, (string?)null), cancellationToken);
                var concernsEngagement = n.Kind == EngagementLifecycle.LockedKind;
                await auditLog.AppendAsync(new AuditEvent(now, Guid.Empty, $"notify.{n.Kind}.sent", concernsEngagement ? "engagement" : "working_paper",
                    (concernsEngagement ? n.EngagementId : n.DocumentId).ToString("D"), n.EngagementId,
                    JsonSerializer.Serialize(new { notificationId = n.Id, recipient = n.RecipientUserId, changeId = n.ChangeId, attempt = n.Attempts + 1 }, Json)),
                    cancellationToken);
                await tx.CommitAsync(cancellationToken);
                sent++;
            }
            else
            {
                log.NotificationFailed(n.Id, n.Attempts + 1, error);
                await db.Notifications.Where(x => x.Id == n.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastError, error), cancellationToken);
            }
        }

        return sent;
    }

    private async Task<string?> TrySendAsync(NotificationRow n, CancellationToken cancellationToken)
    {
        if (n.Kind is not (FieldSyncEngine.SealBrokenKind or EngagementLifecycle.LockedKind))
        {
            return $"Unknown notification kind '{n.Kind}'.";
        }

        var recipient = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == n.RecipientUserId && u.IsActive, cancellationToken);
        if (recipient is null)
        {
            return "Recipient is unknown or disabled.";
        }

        var actor = await db.Users.AsNoTracking().Where(u => u.Id == n.ActorUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        var engagement = await db.Engagements.AsNoTracking().Where(e => e.Id == n.EngagementId).Select(e => e.Code).FirstAsync(cancellationToken);
        try
        {
            if (n.Kind == EngagementLifecycle.LockedKind)
            {
                await mail.SendAsync(EmailTemplates.EngagementLocked(recipient.Email, recipient.DisplayName, engagement, actor ?? "-", n.CreatedAt), cancellationToken);
                return null;
            }

            var template = await db.SyncDocuments.AsNoTracking().Where(d => d.DocumentId == n.DocumentId).Select(d => d.TemplateCode).FirstOrDefaultAsync(cancellationToken);
            await mail.SendAsync(EmailTemplates.SealBroken(recipient.Email, recipient.DisplayName, engagement, template ?? "-", actor ?? "-", n.CreatedAt), cancellationToken);
            return null;
        }
        catch (EmailDeliveryException ex)
        {
            return ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
        }
    }
}

internal static partial class NotificationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} was not sent (attempt {Attempt}): {Error}")]
    public static partial void NotificationFailed(this ILogger logger, Guid notificationId, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification dispatch failed; it will be tried again.")]
    public static partial void DispatchFailed(this ILogger logger, Exception exception);
}

/// <summary>Runs <see cref="NotificationSender"/> when signalled and once a minute.</summary>
internal sealed class NotificationDispatcher(
    IServiceScopeFactory scopes, NotificationSignal signal, IOptions<MasterOptions> options, ILogger<NotificationDispatcher> log) : BackgroundService
{
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunNotificationDispatcher)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await signal.WaitAsync(Sweep, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NotificationSender>().DispatchPendingAsync(stoppingToken);
            }
#pragma warning disable CA1031 // The dispatcher must survive a database or mail outage and try again.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                log.DispatchFailed(ex);
            }
        }
    }
}
