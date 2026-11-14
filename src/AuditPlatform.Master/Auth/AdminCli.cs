using System.Globalization;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Engagements;
using AuditPlatform.Master.Evidence;

namespace AuditPlatform.Master.Auth;

/// <summary>
/// Account administration from the VM console (<c>sudo audit-admin ...</c>). This is how the first administrator
/// is invited, and the way back in if every administrator is locked out. Every action is written to the audit log
/// with the console operator's name.
/// </summary>
internal static class AdminCli
{
    private const string Usage = """
        Usage: audit-admin <command>
          list                                       Danh sách tài khoản
          invite <email> "<tên>" [--admin] [--show-link]   Mời người dùng (gửi e-mail)
          reset-mfa <email> [--show-link]            Xoá TOTP, gửi liên kết cài lại
          role <email> admin|user                    Đổi vai trò hệ thống
          disable <email> | enable <email>           Vô hiệu hoá / kích hoạt lại
          devices <email>                            Các máy đã đăng nhập
          revoke <email> [installation-id]           Thu hồi phiên (một máy hoặc tất cả)

          engagement list                            Danh sách engagement và thành viên
          engagement create <mã> "<khách hàng>" "<số hợp đồng>" <từ yyyy-mm-dd> <đến yyyy-mm-dd>
          engagement member <mã> <email> <vai trò>   Thêm vai trò: preparer|managerqa|japandesk|engagementmanager
          engagement unmember <mã> <email> <vai trò> Gỡ vai trò
          templates import en|vi < tệp.md            Nhập một tệp thủ tục (ia-aup-procedures) từ stdin
          templates list                             Thư viện thủ tục đang dùng
          evidence verify                            Băm lại mọi file bằng chứng; báo file thiếu hoặc bị sửa (sau khôi phục, hoặc định kỳ)

        --show-link in liên kết ra màn hình, dùng khi e-mail không gửi được.
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextReader? input = null)
    {
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var words = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (words.Length == 0 || flags.Except(["--admin", "--show-link"]).Any())
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        if (!services.GetRequiredService<AuthKeys>().IsConfigured)
        {
            await output.WriteLineAsync("Chưa có Auth__MasterKey trong /etc/audit-master/master.env. Chạy: sudo audit-auth-setup");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var actor = Actor.Console(Environment.GetEnvironmentVariable("AUDIT_ADMIN_OPERATOR") is { Length: > 0 } name ? name : "unknown");
        var ct = CancellationToken.None;

        switch (words)
        {
            case ["list"]:
                foreach (var u in await accounts.ListUsersAsync(ct))
                {
                    var state = !u.IsActive ? "disabled" : !u.IsEnrolled ? "invited" : u.IsLocked ? "locked" : "active";
                    await output.WriteLineAsync($"{u.Email,-36} {u.Role,-6} {state,-9} sessions={u.ActiveSessions}  {u.DisplayName}");
                }

                return 0;

            case ["invite", var email, var displayName]:
                return await ReportLinkAsync(await accounts.InviteAsync(actor, email, displayName,
                    flags.Contains("--admin") ? SystemRole.Admin : SystemRole.User, ct));

            case ["reset-mfa", var email]:
                return await WithUserAsync(email, async u => await ReportLinkAsync(await accounts.ResetMfaAsync(actor, u.Id, ct)));

            case ["role", var email, var roleName] when Enum.TryParse<SystemRole>(roleName, true, out var role) && Enum.IsDefined(role):
                return await WithUserAsync(email, async u => await ReportAsync(await accounts.SetRoleAsync(actor, u.Id, role, ct)));

            case ["disable" or "enable", var email]:
                return await WithUserAsync(email, async u => await ReportAsync(await accounts.SetActiveAsync(actor, u.Id, words[0] == "enable", ct)));

            case ["devices", var email]:
                return await WithUserAsync(email, async u =>
                {
                    foreach (var d in await accounts.ListDevicesAsync(u.Id, ct))
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{d.InstallationId:D}  {(d.IsActive ? "active " : "revoked")}  last seen {d.LastSeenAt:yyyy-MM-dd HH:mm}Z  {d.DeviceName}"));
                    }

                    return 0;
                });

            case ["revoke", var email, .. var rest] when rest.Length == 0 || (rest.Length == 1 && Guid.TryParse(rest[0], out _)):
                return await WithUserAsync(email, async u => await ReportAsync(
                    await accounts.RevokeSessionsAsync(actor, u.Id, rest.Length == 1 ? Guid.Parse(rest[0]) : null, ct)));

            case ["engagement", "list"]:
                foreach (var e in await scope.ServiceProvider.GetRequiredService<EngagementService>().ListAllAsync(ct))
                {
                    await output.WriteLineAsync($"{e.Code,-16} {e.Status,-10} {e.PeriodStart:yyyy-MM-dd}..{e.PeriodEnd:yyyy-MM-dd}  {e.ClientName}");
                    foreach (var m in e.Members)
                    {
                        await output.WriteLineAsync($"    {m.DisplayName,-28} {string.Join(", ", m.Roles)}");
                    }
                }

                return 0;

            case ["engagement", "create", var code, var client, var contract, var from, var to]
                when DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                    && DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end):
            {
                var created = await scope.ServiceProvider.GetRequiredService<EngagementService>().CreateAsync(actor, code, client, contract, start, end, ct);
                await output.WriteLineAsync(created.Error is { } error ? $"LỖI [{error.Code}] {error.Message}" : $"OK. Engagement {code} = {created.Value:D}. Đã ghi audit log.");
                return created.Succeeded ? 0 : 1;
            }

            case ["engagement", "member" or "unmember", var code, var email, var roleName]
                when Enum.TryParse<EngagementRole>(roleName, true, out var role) && Enum.IsDefined(role):
                return await WithUserAsync(email, async u =>
                {
                    var changed = await scope.ServiceProvider.GetRequiredService<EngagementService>()
                        .SetMemberAsync(actor, code, u.Id, role, words[1] == "member", ct);
                    await output.WriteLineAsync(changed.Error is { } error ? $"LỖI [{error.Code}] {error.Message}"
                        : changed.Value ? "OK. Đã ghi audit log." : "Không có gì thay đổi.");
                    return changed.Succeeded ? 0 : 1;
                });

            case ["templates", "import", var language]:
            {
                var markdown = input is null ? "" : await input.ReadToEndAsync(ct);
                var imported = await scope.ServiceProvider.GetRequiredService<TemplateService>().ImportAsync(actor, markdown, language, ct);
                if (imported.Error is { } error)
                {
                    await output.WriteLineAsync($"LỖI [{error.Code}] {error.Message}");
                    return 1;
                }

                var r = imported.Value!;
                await output.WriteLineAsync($"OK. {r.AreaName}: thêm {r.Added}, không đổi {r.Unchanged}. {string.Join(", ", r.AddedCodes)}");
                return 0;
            }

            case ["templates", "list"]:
                foreach (var t in (await scope.ServiceProvider.GetRequiredService<TemplateService>().ListAsync(ct))
                    .GroupBy(t => (t.Code, t.Language)).Select(g => g.MaxBy(t => t.Version)!))
                {
                    await output.WriteLineAsync($"{t.Code,-12} {t.Language} v{t.Version}  {t.Title}");
                }

                return 0;

            case ["evidence", "verify"]:
            {
                var verified = await scope.ServiceProvider.GetRequiredService<EvidenceService>().VerifyAllAsync(
                    Environment.GetEnvironmentVariable("AUDIT_ADMIN_OPERATOR") is { Length: > 0 } who ? who : "unknown", ct);
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"Đã kiểm tra {verified.Checked} file ({verified.Bytes / (1024.0 * 1024):0.#} MB đã đọc và khớp mã băm)."));
                foreach (var hash in verified.Missing)
                {
                    await output.WriteLineAsync($"THIẾU     {hash}");
                }

                foreach (var hash in verified.Damaged)
                {
                    await output.WriteLineAsync($"BỊ SỬA    {hash}");
                }

                await output.WriteLineAsync(verified.IsClean ? "OK. Đã ghi audit log." : "CÓ VẤN ĐỀ: khôi phục các file trên từ bản sao R2 (RESTORE.md), rồi chạy lại. Đã ghi audit log.");
                return verified.IsClean ? 0 : 1;
            }

            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }

        async Task<int> WithUserAsync(string email, Func<UserRow, Task<int>> action)
        {
            if (await accounts.FindUserAsync(email, ct) is not { } user)
            {
                await output.WriteLineAsync($"Không tìm thấy tài khoản: {email}");
                return 1;
            }

            return await action(user);
        }

        async Task<int> ReportAsync(Outcome<bool> outcome)
        {
            await output.WriteLineAsync(outcome.Error is { } error ? $"LỖI [{error.Code}] {error.Message}" : "OK. Đã ghi audit log.");
            return outcome.Succeeded ? 0 : 1;
        }

        async Task<int> ReportLinkAsync(Outcome<InviteResult> outcome)
        {
            if (outcome.Error is { } error)
            {
                await output.WriteLineAsync($"LỖI [{error.Code}] {error.Message}");
                return 1;
            }

            var result = outcome.Value!;
            var show = flags.Contains("--show-link");
            await output.WriteLineAsync(result.MailSent
                ? "OK. Đã gửi e-mail kèm liên kết (hiệu lực 24 giờ)."
                : "CẢNH BÁO: e-mail KHÔNG gửi được (kiểm tra Mail__BrevoApiKey và sender trên Brevo)."
                  + (show ? string.Empty : " Chạy lại lệnh với --show-link để lấy liên kết."));
            if (show)
            {
                await output.WriteLineAsync($"Liên kết một lần, chỉ chuyển cho đúng người dùng qua kênh an toàn:\n{result.Link}");
            }

            return result.MailSent || show ? 0 : 1;
        }
    }
}
