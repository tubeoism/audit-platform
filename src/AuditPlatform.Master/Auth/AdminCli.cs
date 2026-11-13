using System.Globalization;
using AuditPlatform.Master.Data.Entities;

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

        --show-link in liên kết ra màn hình, dùng khi e-mail không gửi được.
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output)
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
            await output.WriteLineAsync(result.MailSent
                ? "OK. Đã gửi e-mail kèm liên kết (hiệu lực 24 giờ)."
                : "CẢNH BÁO: tài khoản đã tạo nhưng e-mail KHÔNG gửi được (kiểm tra Mail__BrevoApiKey). Chạy lại với --show-link để lấy liên kết.");
            if (flags.Contains("--show-link"))
            {
                await output.WriteLineAsync($"Liên kết một lần, chỉ chuyển cho đúng người dùng qua kênh an toàn:\n{result.Link}");
            }

            return result.MailSent || flags.Contains("--show-link") ? 0 : 1;
        }
    }
}
