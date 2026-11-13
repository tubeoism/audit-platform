using System.Text;
using System.Text.Encodings.Web;
using AuditPlatform.Master.Data.Entities;
using QRCoder;

namespace AuditPlatform.Master.Auth;

/// <summary>
/// The few browser pages a user needs before the desktop client can sign in: accept an invitation (set password,
/// scan the QR code, save recovery codes) and reset a forgotten password. Server-rendered, no scripts, no cookies;
/// each form is authorised by the one-time token from the e-mailed link, so there is no session to forge.
/// </summary>
internal static class AccountPages
{
    private static readonly HtmlEncoder E = HtmlEncoder.Default;

    public static void MapAccountPages(this WebApplication app)
    {
        var pages = app.MapGroup("/account")
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy)
            .AddEndpointFilter<RequireAuthKeysFilter>()
            .AddEndpointFilter(async (ctx, next) =>
            {
                var headers = ctx.HttpContext.Response.Headers;
                headers.CacheControl = "no-store";
                headers["Referrer-Policy"] = "no-referrer";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy =
                    "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
                return await next(ctx);
            })
            .DisableAntiforgery()
            .ExcludeFromDescription();

        pages.MapGet("/enrol", async (string? token, AccountService accounts, CancellationToken ct) =>
            await accounts.DescribeLinkAsync(token, ct) is { Purpose: not UserTokenPurpose.PasswordReset } link
                ? PasswordStep(token!, link, null)
                : Dead());

        pages.MapPost("/enrol", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            string? token = form["token"], password = form["password"];
            if (await accounts.DescribeLinkAsync(token, ct) is not { Purpose: not UserTokenPurpose.PasswordReset } link)
            {
                return Dead();
            }

            if (link.Purpose == UserTokenPurpose.Invite && password != form["confirm"])
            {
                return PasswordStep(token!, link, Mismatch);
            }

            var result = await accounts.BeginEnrolmentAsync(token, password, Actor.Web(Address(http)), ct);
            return result.Error is { } error
                ? error == AuthErrors.LinkInvalid ? Dead() : PasswordStep(token!, link, error.Message)
                : CodeStep(token!, result.Value!, null);
        });

        pages.MapPost("/enrol/confirm", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            string? token = form["token"], ticket = form["ticket"];
            var result = await accounts.ConfirmEnrolmentAsync(token, ticket, form["code"], Actor.Web(Address(http)), ct);
            if (result.Error is null)
            {
                return RecoveryCodes(result.Value!);
            }

            var resumed = await accounts.ResumeEnrolmentAsync(token, ticket, ct);
            return result.Error == AuthErrors.CodeInvalid && resumed.Value is { } enrolment
                ? CodeStep(token!, enrolment, result.Error.Message)
                : Dead();
        });

        pages.MapGet("/forgot", () => Forgot(false));

        pages.MapPost("/forgot", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            await accounts.RequestPasswordResetAsync(form["email"], Actor.Web(Address(http)), ct);
            return Forgot(true);
        });

        pages.MapGet("/reset", async (string? token, AccountService accounts, CancellationToken ct) =>
            await accounts.DescribeLinkAsync(token, ct) is { Purpose: UserTokenPurpose.PasswordReset } link
                ? Reset(token!, link, null)
                : Dead());

        pages.MapPost("/reset", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            string? token = form["token"], password = form["password"];
            if (await accounts.DescribeLinkAsync(token, ct) is not { Purpose: UserTokenPurpose.PasswordReset } link)
            {
                return Dead();
            }

            if (password != form["confirm"])
            {
                return Reset(token!, link, Mismatch);
            }

            var result = await accounts.ResetPasswordAsync(token, password, Actor.Web(Address(http)), ct);
            return result.Error is { } error
                ? error == AuthErrors.LinkInvalid ? Dead() : Reset(token!, link, error.Message)
                : Page("Đã đổi mật khẩu", "Password changed",
                    P("Mật khẩu mới đã có hiệu lực và mọi thiết bị đã bị đăng xuất. Hãy đăng nhập lại trong ứng dụng bằng mật khẩu mới và mã xác thực.",
                        "The new password is in effect and every device has been signed out. Sign in again in the application with the new password and your authenticator code."));
        });
    }

    internal static string? Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private const string Mismatch = "Hai lần nhập mật khẩu không khớp. / The two passwords do not match.";

    private static IResult PasswordStep(string token, LinkInfo link, string? error)
    {
        var invite = link.Purpose == UserTokenPurpose.Invite;
        var body = new StringBuilder()
            .Append(P($"Tài khoản: {link.Email}", $"Account: {link.Email}"))
            .Append(invite
                ? P("Bước 1/3. Đặt mật khẩu tối thiểu 12 ký tự. Mật khẩu từng bị lộ trong các vụ rò rỉ sẽ bị từ chối.",
                    "Step 1 of 3. Choose a password of at least 12 characters. Passwords known from breaches are refused.")
                : P("Bước 1/3. Nhập mật khẩu hiện tại để cài lại mã xác thực hai lớp.",
                    "Step 1 of 3. Enter your current password to re-enrol two-factor authentication."))
            .Append(Error(error))
            .Append("<form method=\"post\" action=\"/account/enrol\" autocomplete=\"off\">")
            .Append(Hidden("token", token))
            .Append(Field("password", invite ? "Mật khẩu / Password" : "Mật khẩu hiện tại / Current password", "password",
                invite ? "new-password" : "current-password"));
        if (invite)
        {
            body.Append(Field("confirm", "Nhập lại mật khẩu / Repeat password", "password", "new-password"));
        }

        return Page(invite ? "Kích hoạt tài khoản" : "Cài lại xác thực hai lớp", invite ? "Activate your account" : "Re-enrol two-factor authentication",
            body.Append(Button("Tiếp tục / Continue")).Append("</form>").ToString());
    }

    private static IResult CodeStep(string token, Enrolment enrolment, string? error)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(enrolment.OtpAuthUri, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(5);
        var secret = string.Join(' ', enrolment.SecretBase32.Chunk(4).Select(c => new string(c)));
        return Page("Cài mã xác thực hai lớp", "Enrol two-factor authentication",
            P("Bước 2/3. Mở Microsoft Authenticator hoặc Google Authenticator, chọn thêm tài khoản và quét mã QR này.",
                "Step 2 of 3. Open Microsoft Authenticator or Google Authenticator, add an account and scan this QR code.")
            + $"<div class=\"qr\">{svg}</div>"
            + P($"Không quét được? Nhập khoá này vào ứng dụng: ", "Cannot scan? Type this key into the app: ")
            + $"<p class=\"mono\">{E.Encode(secret)}</p>"
            + P("Sau đó nhập mã 6 chữ số ứng dụng đang hiển thị.", "Then enter the 6-digit code the app shows.")
            + Error(error)
            + "<form method=\"post\" action=\"/account/enrol/confirm\" autocomplete=\"off\">"
            + Hidden("token", token) + Hidden("ticket", enrolment.Ticket)
            + "<label>Mã xác thực / Code<input name=\"code\" inputmode=\"numeric\" pattern=\"[0-9 ]{6,7}\" maxlength=\"7\" required autofocus autocomplete=\"one-time-code\"></label>"
            + Button("Xác nhận / Confirm") + "</form>");
    }

    private static IResult RecoveryCodes(IReadOnlyList<string> codes) =>
        Page("Lưu mã khôi phục", "Save your recovery codes",
            P("Bước 3/3. Tài khoản đã kích hoạt. Dưới đây là 10 mã khôi phục, mỗi mã dùng được một lần để đăng nhập khi bạn mất điện thoại. Trang này chỉ hiện một lần: hãy in hoặc cất vào trình quản lý mật khẩu, không lưu cùng máy tính làm việc.",
                "Step 3 of 3. Your account is active. These are 10 recovery codes; each signs you in once if you lose your phone. This page is shown only once: print the codes or keep them in a password manager, not on the work laptop.")
            + "<ul class=\"codes\">" + string.Concat(codes.Select(c => $"<li class=\"mono\">{E.Encode(c)}</li>")) + "</ul>"
            + P("Mã khôi phục không dùng được để ký duyệt (sign-off). Bây giờ bạn có thể đóng trang và đăng nhập trong ứng dụng Audit Platform.",
                "Recovery codes cannot be used to sign off working papers. You can now close this page and sign in to the Audit Platform application."));

    private static IResult Forgot(bool submitted) =>
        Page("Quên mật khẩu", "Forgot password", submitted
            ? P("Nếu địa chỉ này có tài khoản, một thư chứa liên kết đặt lại mật khẩu đã được gửi. Liên kết có hiệu lực 30 phút.",
                "If this address has an account, a message with a reset link has been sent. The link is valid for 30 minutes.")
            : P("Nhập địa chỉ e-mail công việc. Bạn vẫn cần mã xác thực hai lớp để đăng nhập sau khi đổi mật khẩu.",
                  "Enter your work e-mail address. You still need your authenticator code to sign in after the reset.")
              + "<form method=\"post\" action=\"/account/forgot\">" + Field("email", "E-mail", "email", "username")
              + Button("Gửi liên kết / Send link") + "</form>");

    private static IResult Reset(string token, LinkInfo link, string? error) =>
        Page("Đặt lại mật khẩu", "Reset password",
            P($"Tài khoản: {link.Email}", $"Account: {link.Email}")
            + P("Mật khẩu mới tối thiểu 12 ký tự. Sau khi đổi, mọi thiết bị sẽ bị đăng xuất.",
                "The new password needs at least 12 characters. Every device is signed out after the change.")
            + Error(error)
            + "<form method=\"post\" action=\"/account/reset\" autocomplete=\"off\">" + Hidden("token", token)
            + Field("password", "Mật khẩu mới / New password", "password", "new-password")
            + Field("confirm", "Nhập lại mật khẩu / Repeat password", "password", "new-password")
            + Button("Đổi mật khẩu / Change password") + "</form>");

    private static IResult Dead() =>
        Page("Liên kết không còn hiệu lực", "This link is no longer valid",
            P("Liên kết không hợp lệ, đã được dùng hoặc đã hết hạn. Hãy nhờ quản trị viên gửi lại lời mời, hoặc yêu cầu đặt lại mật khẩu lần nữa.",
                "The link is invalid, already used or expired. Ask an administrator to send the invitation again, or request another password reset."),
            StatusCodes.Status410Gone);

    private static string P(string vi, string en) => $"<p>{E.Encode(vi)}<br><span class=\"en\">{E.Encode(en)}</span></p>";

    private static string Error(string? message) => message is null ? string.Empty : $"<p class=\"error\" role=\"alert\">{E.Encode(message)}</p>";

    private static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{name}\" value=\"{E.Encode(value)}\">";

    private static string Field(string name, string label, string type, string autocomplete) =>
        $"<label>{E.Encode(label)}<input name=\"{name}\" type=\"{type}\" autocomplete=\"{autocomplete}\" maxlength=\"320\" required></label>";

    private static string Button(string text) => $"<button type=\"submit\">{E.Encode(text)}</button>";

    private static IResult Page(string titleVi, string titleEn, string body, int status = StatusCodes.Status200OK) =>
        Results.Content($$"""
            <!doctype html>
            <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="robots" content="noindex"><title>{{E.Encode(titleVi)}} · Audit Platform</title>
            <style>
            body{font-family:"Segoe UI",system-ui,Arial,sans-serif;background:#f3f4f6;color:#1f2933;margin:0;padding:24px;line-height:1.5}
            main{max-width:520px;margin:0 auto;background:#fff;border:1px solid #d5d9de;border-radius:6px;padding:28px}
            h1{font-size:22px;margin:0 0 2px}.sub,.en{color:#52606d;font-size:14px}.brand{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:#1d4ed8;margin:0 0 12px}
            label{display:block;margin:14px 0;font-weight:600;font-size:14px}
            input{display:block;width:100%;box-sizing:border-box;margin-top:6px;padding:10px;font-size:16px;border:1px solid #9aa5b1;border-radius:4px}
            button{padding:10px 20px;font-size:15px;background:#1d4ed8;color:#fff;border:0;border-radius:4px;cursor:pointer}
            .error{background:#fdecea;border-left:4px solid #b42318;padding:10px 12px;color:#7a1410}
            .qr{text-align:center;margin:8px 0}.qr svg{width:220px;height:220px}
            .mono{font-family:Consolas,"Courier New",monospace;font-size:17px;letter-spacing:.05em}
            .codes{list-style:none;padding:0;display:grid;grid-template-columns:1fr 1fr;gap:6px 24px}
            </style></head><body><main>
            <p class="brand">Audit Platform</p><h1>{{E.Encode(titleVi)}}</h1><p class="sub">{{E.Encode(titleEn)}}</p>
            {{body}}
            </main></body></html>
            """, "text/html; charset=utf-8", Encoding.UTF8, status);
}
