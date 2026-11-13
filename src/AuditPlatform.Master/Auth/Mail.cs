using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Auth;

public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    /// <summary>Throws <see cref="EmailDeliveryException"/> when the message was not accepted for delivery.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Brevo transactional e-mail over the HTTP API (ADR-0005). Used for invitations and resets only.</summary>
public sealed class BrevoEmailSender(HttpClient http, IOptions<MailOptions> options) : IEmailSender
{
    private static readonly Uri Endpoint = new("https://api.brevo.com/v3/smtp/email");

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BrevoApiKey))
        {
            throw new EmailDeliveryException("Mail:BrevoApiKey is not configured on the master.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                sender = new { email = o.From, name = o.FromName },
                to = new[] { new { email = message.ToAddress, name = message.ToName } },
                subject = message.Subject,
                textContent = message.TextBody,
                htmlContent = message.HtmlBody,
            }),
        };
        request.Headers.Add("api-key", o.BrevoApiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The response body may echo the recipient; the status code is enough for the operator.
                throw new EmailDeliveryException($"Brevo refused the message (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new EmailDeliveryException("Brevo could not be reached.", ex);
        }
    }
}

/// <summary>Bilingual (Vietnamese, English) messages. Each carries one link and says when it expires.</summary>
public static class EmailTemplates
{
    public static EmailMessage Invitation(string to, string name, Uri link, TimeSpan validFor) => Build(to, name,
        "Lời mời tham gia Audit Platform / Invitation to Audit Platform",
        $"Bạn được mời sử dụng Audit Platform. Mở liên kết dưới đây để đặt mật khẩu và cài mã xác thực hai lớp (TOTP). Liên kết có hiệu lực trong {Hours(validFor)} giờ và chỉ dùng được một lần.",
        $"You have been invited to Audit Platform. Open the link below to set your password and enrol two-factor authentication (TOTP). The link is valid for {Hours(validFor)} hours and can be used once.",
        "Đặt mật khẩu / Set password", link);

    public static EmailMessage PasswordReset(string to, string name, Uri link, TimeSpan validFor) => Build(to, name,
        "Đặt lại mật khẩu Audit Platform / Reset your Audit Platform password",
        $"Có yêu cầu đặt lại mật khẩu cho tài khoản của bạn. Liên kết có hiệu lực trong {Minutes(validFor)} phút. Nếu bạn không yêu cầu, hãy bỏ qua thư này và báo cho quản trị viên; mật khẩu hiện tại vẫn giữ nguyên.",
        $"A password reset was requested for your account. The link is valid for {Minutes(validFor)} minutes. If you did not ask for it, ignore this message and tell an administrator; your current password stays unchanged.",
        "Đặt lại mật khẩu / Reset password", link);

    public static EmailMessage MfaEnrolment(string to, string name, Uri link, TimeSpan validFor) => Build(to, name,
        "Cài lại mã xác thực hai lớp / Re-enrol two-factor authentication",
        $"Quản trị viên đã đặt lại xác thực hai lớp của bạn. Mở liên kết dưới đây, nhập mật khẩu hiện tại và quét mã QR mới. Liên kết có hiệu lực trong {Hours(validFor)} giờ.",
        $"An administrator has reset your two-factor authentication. Open the link below, enter your current password and scan the new QR code. The link is valid for {Hours(validFor)} hours.",
        "Cài lại xác thực / Re-enrol", link);

    private static string Hours(TimeSpan t) => ((int)Math.Round(t.TotalHours)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan t) => ((int)Math.Round(t.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static EmailMessage Build(string to, string name, string subject, string vi, string en, string button, Uri link)
    {
        var e = HtmlEncoder.Default;
        var text = $"Xin chào {name},\n\n{vi}\n\n{link}\n\n---\n\nHello {name},\n\n{en}\n\n{link}\n\nAudit Platform";
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#1f2933;max-width:560px">
            <p>Xin chào {e.Encode(name)},</p><p>{e.Encode(vi)}</p>
            <p><a href="{e.Encode(link.AbsoluteUri)}" style="display:inline-block;padding:10px 18px;background:#1d4ed8;color:#ffffff;text-decoration:none;border-radius:4px">{e.Encode(button)}</a></p>
            <hr style="border:none;border-top:1px solid #d5d9de">
            <p>Hello {e.Encode(name)},</p><p>{e.Encode(en)}</p>
            <p style="font-size:13px;color:#52606d">{e.Encode(link.AbsoluteUri)}</p>
            <p style="font-size:13px;color:#52606d">Audit Platform</p>
            </div>
            """;
        return new EmailMessage(to, name, subject, text, html);
    }
}
