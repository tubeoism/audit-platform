using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Domain.WorkingPapers;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.ViewModels;

/// <summary>Runs an action on the thread that owns the window. Sync events arrive on background threads.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed record CurrentUser(Guid Id, string Email, string DisplayName);

public static class Texts
{
    public static string Status(WorkingPaperStatus status) => status switch
    {
        WorkingPaperStatus.Draft => "Đang lập",
        WorkingPaperStatus.ReadyForReview => "Chờ soát xét",
        WorkingPaperStatus.ManagerQaSignedOff => "Đã ký cấp 1 (Manager QA)",
        WorkingPaperStatus.JapanDeskSignedOff => "Đã ký cấp 2 (Japan Desk)",
        _ => status.ToString(),
    };
}

/// <summary>
/// One laptop, one author at a time. The master records a change under the identity of the connection that
/// delivers it, so unsent work of one person must never be sent by the next person who signs in on the same laptop.
/// </summary>
public static class SessionGate
{
    /// <summary>Why this user may not start working on this laptop yet, or null if they may.</summary>
    public static async Task<string?> WhyRefusedAsync(LocalDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var foreign = await db.Outbox.CountAsync(o => o.AuthorUserId != userId, cancellationToken);
        if (foreign == 0)
        {
            return null;
        }

        var owner = await db.Profile.AsNoTracking().Where(p => p.UserId != userId).Select(p => p.DisplayName).FirstOrDefaultAsync(cancellationToken);
        return $"Máy này còn {foreign} thay đổi chưa gửi của {owner ?? "người dùng trước"}. Người đó cần đăng nhập và đồng bộ trước khi người khác dùng máy.";
    }
}
