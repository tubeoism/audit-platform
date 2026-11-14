using System.Net;
using System.Net.Http.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditPlatform.Master.Tests.Auth;

/// <summary>ADR-0005 end to end, through the real HTTP pipeline and a real PostgreSQL schema.</summary>
public sealed class AuthFlowTests(AuthFactory factory) : IClassFixture<AuthFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<HttpStatusCode> MeAsync(HttpClient client, string accessToken)
    {
        using var request = AuthFactory.Authorized(HttpMethod.Get, AuthRoutes.Me, accessToken);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<TokenResponse?> RefreshAsync(HttpClient client, string refreshToken, Guid installationId)
    {
        using var response = await client.PostAsJsonAsync(AuthRoutes.Refresh, new RefreshRequest(refreshToken, installationId), Ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TokenResponse>(Ct) : null;
    }

    // ----- Invitation and enrolment -------------------------------------------------------------------------------

    [Fact]
    public async Task Invitation_enrolment_and_first_sign_in()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        const string email = "first.user@audit.io.vn";
        var invited = await factory.ScopeAsync(sp => sp.GetRequiredService<AccountService>()
            .InviteAsync(Actor.Console("tu"), " First.User@Audit.io.vn ", "First User", SystemRole.User, Ct));
        Assert.True(invited.Value!.MailSent);

        var mail = factory.Mail.Sent.Last(m => m.ToAddress == email);
        Assert.Contains("https://master.test/account/enrol?token=", mail.TextBody, StringComparison.Ordinal);
        var token = factory.Mail.TokenFor(email);

        // Before enrolment the account cannot sign in, whatever is sent.
        using (var early = await AuthFactory.LoginAsync(client, email, AuthFactory.Password, "000000"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);
        }

        var page = await client.GetStringAsync(Rel($"/account/enrol?token={token}"), Ct);
        Assert.Contains(email, page, StringComparison.Ordinal);

        var (secret, ticket) = await AuthFactory.SubmitPasswordAsync(client, token, AuthFactory.Password);

        // A wrong code shows the same QR code again and consumes nothing.
        var retry = await AuthFactory.ConfirmRawAsync(client, token, ticket, "000000");
        Assert.Contains("role=\"alert\"", retry, StringComparison.Ordinal);
        Assert.Contains("name=\"ticket\"", retry, StringComparison.Ordinal);

        // The QR code must scale to the page's 220 px box: a fixed width/height on the svg cuts the symbol off and
        // authenticator apps cannot read it.
        var svgTag = System.Text.RegularExpressions.Regex.Match(retry, "<svg[^>]*>").Value;
        Assert.Contains("viewBox=\"0 0 220 220\"", svgTag, StringComparison.Ordinal);
        Assert.DoesNotContain(" width=", svgTag, StringComparison.Ordinal);
        Assert.DoesNotContain(" height=", svgTag, StringComparison.Ordinal);

        var codes = await AuthFactory.ConfirmAsync(client, token, ticket, Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow())));
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());

        // The link is single-use.
        using (var again = await client.GetAsync(Rel($"/account/enrol?token={token}"), Ct))
        {
            Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        }

        var user = new TestUser(invited.Value.UserId, email, AuthFactory.Password, secret, codes);
        var tokens = await factory.SignInAsync(client, user);
        Assert.Equal(email, tokens.User.Email);
        Assert.Equal("User", tokens.User.Role);
        Assert.Equal(factory.Clock.GetUtcNow().AddMinutes(15), tokens.AccessTokenExpiresAt);
        Assert.Equal(factory.Clock.GetUtcNow().AddDays(14), tokens.RefreshTokenExpiresAt);
        Assert.Equal(factory.Clock.GetUtcNow().AddDays(90), tokens.SessionExpiresAt);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, tokens.AccessToken));

        // The sync hub now admits the signed-in user and still refuses everyone else.
        using var negotiate = AuthFactory.Authorized(HttpMethod.Post, $"{SyncHubRoutes.Path}/negotiate?negotiateVersion=1", tokens.AccessToken);
        using var hub = await client.SendAsync(negotiate, Ct);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        using var anonymous = await client.PostAsync(Rel($"{SyncHubRoutes.Path}/negotiate?negotiateVersion=1"), null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Nothing usable is stored: no plain secret, token or recovery code.
        var stored = await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct));
        Assert.StartsWith("v1.", stored.TotpSecret, StringComparison.Ordinal);
        Assert.DoesNotContain(Base32.Encode(secret), stored.TotpSecret!, StringComparison.Ordinal);
        Assert.NotEqual(AuthFactory.Password, stored.PasswordHash);
        var hashes = await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().RecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == stored.Id).Select(c => c.CodeHash).ToListAsync(Ct));
        Assert.Equal(10, hashes.Count);
        Assert.DoesNotContain(hashes, h => codes.Contains(h));

        // Every step is in the hash-chained audit log, and the chain is intact.
        var audit = await factory.AuditAsync();
        var mine = audit.Where(a => a.EntityId == stored.Id.ToString("D")).Select(a => a.Action).ToList();
        Assert.Equal(["auth.invite.sent", "auth.login.refused", "auth.totp.enrolled", "auth.login.succeeded"], mine);
        Assert.Contains("vm-console:tu", audit.First(a => a.Action == "auth.invite.sent" && a.EntityId == stored.Id.ToString("D")).PayloadJson, StringComparison.Ordinal);
        Assert.Null(HashChain.Verify(audit.Select(r => new AuditEntry(r.Sequence, r.OccurredAt, r.ActorUserId, r.Action, r.EntityType,
            r.EntityId, r.EngagementId, r.PayloadJson, r.PreviousHash, r.Hash))));
    }

    [Theory]
    [InlineData("too-short-1", "12")]
    [InlineData("passwordpassword", "đã lộ")]
    [InlineData("weak.person-2026!", "dễ đoán")]
    public async Task Weak_passwords_are_refused_at_enrolment(string password, string expectedMessage)
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        // The local part doubles as the "contains your e-mail name" case.
        var email = $"weak.person@{Guid.NewGuid():N}.audit.io.vn";
        await factory.ScopeAsync(sp => sp.GetRequiredService<AccountService>().InviteAsync(Actor.Console("test"), email, "Weak", SystemRole.User, Ct));

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = factory.Mail.TokenFor(email), ["password"] = password, ["confirm"] = password,
        });
        using var response = await client.PostAsync(Rel("/account/enrol"), form, Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"ticket\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitation_expires_after_24_hours_and_a_new_invitation_retires_the_old_link()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var email = $"late-{Guid.NewGuid():N}@audit.io.vn";
        Task<Outcome<InviteResult>> Invite() => factory.ScopeAsync(sp => sp.GetRequiredService<AccountService>()
            .InviteAsync(Actor.Console("test"), email, "Late", SystemRole.User, Ct));

        await Invite();
        var first = factory.Mail.TokenFor(email);
        await Invite();
        var second = factory.Mail.TokenFor(email);
        Assert.NotEqual(first, second);

        using (var old = await client.GetAsync(Rel($"/account/enrol?token={first}"), Ct))
        {
            Assert.Equal(HttpStatusCode.Gone, old.StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromHours(23));
        using (var valid = await client.GetAsync(Rel($"/account/enrol?token={second}"), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromHours(2));
        using var expired = await client.GetAsync(Rel($"/account/enrol?token={second}"), Ct);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Fact]
    public async Task Failed_mail_delivery_is_reported_and_audited()
    {
        factory.RequireDatabase();
        factory.Mail.Fail = true;
        try
        {
            var email = $"nomail-{Guid.NewGuid():N}@audit.io.vn";
            var result = await factory.ScopeAsync(sp => sp.GetRequiredService<AccountService>()
                .InviteAsync(Actor.Console("test"), email, "No Mail", SystemRole.User, Ct));

            Assert.False(result.Value!.MailSent);
            Assert.Contains(await factory.AuditAsync(), a => a.Action == "auth.mail.failed" && a.EntityId == result.Value.UserId.ToString("D"));
        }
        finally
        {
            factory.Mail.Fail = false;
        }
    }

    // ----- Sign-in ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_failed_sign_in_gets_the_same_answer_and_the_fifth_locks_the_account_for_15_minutes()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();

        using var unknown = await AuthFactory.LoginAsync(client, "nobody@audit.io.vn", user.Password, "123456");
        var unknownBody = await unknown.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        for (var i = 0; i < 4; i++)
        {
            using var wrong = await AuthFactory.LoginAsync(client, user.Email, "wrong-password-000", factory.NextCode(user.Secret));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(unknownBody, await wrong.Content.ReadAsStringAsync(Ct));
        }

        // A wrong code counts like a wrong password: this is the fifth failure.
        using (var wrongCode = await AuthFactory.LoginAsync(client, user.Email, user.Password, "000000"))
        {
            Assert.Equal(unknownBody, await wrongCode.Content.ReadAsStringAsync(Ct));
        }

        // Locked: even correct credentials are refused, with the same answer.
        using (var locked = await AuthFactory.LoginAsync(client, user.Email, user.Password, factory.NextCode(user.Secret)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
            Assert.Equal(unknownBody, await locked.Content.ReadAsStringAsync(Ct));
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(15));
        await factory.SignInAsync(client, user);

        var actions = (await factory.AuditAsync()).Where(a => a.EntityId == user.Id.ToString("D")).Select(a => a.Action).ToList();
        Assert.Equal(5, actions.Count(a => a == "auth.attempt.failed"));
        Assert.Single(actions, a => a == "auth.account.locked");
        Assert.Single(actions, a => a == "auth.login.refused");
    }

    [Fact]
    public async Task A_totp_code_works_once_and_parallel_guesses_cannot_outrun_the_lockout()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();

        var code = factory.NextCode(user.Secret);
        using (var first = await AuthFactory.LoginAsync(client, user.Email, user.Password, code))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await AuthFactory.LoginAsync(client, user.Email, user.Password, code))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }

        // Twenty guesses at once: the row lock makes them count one by one, so the account locks.
        var guesses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var response = await AuthFactory.LoginAsync(client, user.Email, user.Password, $"{i:D6}");
            return response.StatusCode;
        }));
        Assert.All(guesses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        var row = await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct));
        Assert.True(row.LockedUntil > factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_reports_how_many_remain()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();

        using (var first = await AuthFactory.LoginAsync(client, user.Email, user.Password, null, user.RecoveryCodes[0].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)))
        {
            var tokens = await first.Content.ReadFromJsonAsync<TokenResponse>(Ct);
            Assert.Equal(9, tokens!.RecoveryCodesRemaining);
        }

        using var second = await AuthFactory.LoginAsync(client, user.Email, user.Password, null, user.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    // ----- Tokens and sessions ------------------------------------------------------------------------------------

    [Fact]
    public async Task Access_token_expires_after_15_minutes_and_refresh_rotates()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var installation = Guid.NewGuid();
        var first = await factory.SignInAsync(client, user, installation);

        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, first.AccessToken));
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, first.AccessToken));

        var second = await RefreshAsync(client, first.RefreshToken, installation);
        Assert.NotNull(second);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, second.AccessToken));
        Assert.Equal(first.SessionExpiresAt, second.SessionExpiresAt);
    }

    [Fact]
    public async Task A_lost_refresh_reply_can_be_retried_briefly_but_later_reuse_revokes_the_session()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var installation = Guid.NewGuid();
        var login = await factory.SignInAsync(client, user, installation);

        var lost = await RefreshAsync(client, login.RefreshToken, installation);
        factory.Clock.Advance(TimeSpan.FromSeconds(10));
        var retried = await RefreshAsync(client, login.RefreshToken, installation);
        Assert.NotNull(retried);

        // The reply that was never received is dead; the retried one carries the session.
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var current = await RefreshAsync(client, retried.RefreshToken, installation);
        Assert.NotNull(current);

        // Presenting the retired token now is treated as theft: the whole session ends, newest token included.
        Assert.Null(await RefreshAsync(client, lost!.RefreshToken, installation));
        Assert.Null(await RefreshAsync(client, current.RefreshToken, installation));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, current.AccessToken));
        Assert.Contains(await factory.AuditAsync(), a => a.Action == "auth.session.revoked"
            && a.EntityId == user.Id.ToString("D") && a.PayloadJson.Contains("refresh_token_reuse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refresh_token_is_bound_to_its_installation()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var installation = Guid.NewGuid();
        var login = await factory.SignInAsync(client, user, installation);

        Assert.Null(await RefreshAsync(client, login.RefreshToken, Guid.NewGuid()));
        Assert.Null(await RefreshAsync(client, login.RefreshToken, installation));
    }

    [Fact]
    public async Task Session_slides_14_days_and_ends_at_90_days_regardless_of_use()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();

        var idleInstallation = Guid.NewGuid();
        var idle = await factory.SignInAsync(client, user, idleInstallation);
        var busyInstallation = Guid.NewGuid();
        var busy = await factory.SignInAsync(client, user, busyInstallation);

        for (var day = 10; day <= 80; day += 10)
        {
            factory.Clock.Advance(TimeSpan.FromDays(10));
            busy = (await RefreshAsync(client, busy.RefreshToken, busyInstallation))!;
            Assert.NotNull(busy);
        }

        // Unused for more than 14 days.
        Assert.Null(await RefreshAsync(client, idle.RefreshToken, idleInstallation));

        // Used every 10 days, yet over at day 90: the last token expires with the session, not 14 days later.
        Assert.Equal(busy.SessionExpiresAt, busy.RefreshTokenExpiresAt);
        factory.Clock.Advance(TimeSpan.FromDays(11));
        Assert.Null(await RefreshAsync(client, busy.RefreshToken, busyInstallation));
    }

    [Fact]
    public async Task Signing_in_again_on_a_laptop_replaces_its_session_and_sign_out_ends_it()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var installation = Guid.NewGuid();

        var first = await factory.SignInAsync(client, user, installation);
        var second = await factory.SignInAsync(client, user, installation);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, first.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, second.AccessToken));

        using (var logout = await client.PostAsJsonAsync(AuthRoutes.Logout, new LogoutRequest(second.RefreshToken), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, second.AccessToken));
        Assert.Null(await RefreshAsync(client, second.RefreshToken, installation));
    }

    [Fact]
    public async Task Forged_and_foreign_tokens_are_refused()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var tokens = await factory.SignInAsync(client, user);

        var parts = tokens.AccessToken.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string([.. parts[2].Reverse()])}";
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, tampered));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, "not-a-token"));

        // A token signed by a master with another key (same issuer, same user id) is worthless here.
        await using var other = new AuthFactory { MasterKey = Convert.ToBase64String(new byte[32]) };
        await other.InitializeAsync();
        using var otherClient = other.CreateClient();
        var foreign = await other.SignInAsync(otherClient, await other.EnrolAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, foreign.AccessToken));
    }

    // ----- Sign-off step-up ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Sign_off_grant_needs_a_fresh_code_and_is_single_use_and_bound_to_paper_and_content()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var paper = Guid.NewGuid();
        const string hash = "9f2c1e0b7a";

        async Task<(HttpStatusCode Status, StepUpResponse? Grant)> StepUpAsync(string accessToken, string code)
        {
            using var request = AuthFactory.Authorized(HttpMethod.Post, AuthRoutes.StepUp, accessToken, new StepUpRequest(code, paper, hash));
            using var response = await client.SendAsync(request, Ct);
            return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<StepUpResponse>(Ct) : null);
        }

        Task<bool> ConsumeAsync(Guid userId, string? token, Guid workingPaper, string contentHash) =>
            factory.ScopeAsync(sp => sp.GetRequiredService<ISignOffStepUp>().ConsumeAsync(userId, token, workingPaper, contentHash, Ct));

        // The code that signed in cannot also sign off: an unattended, signed-in laptop is not enough.
        var signInCode = factory.NextCode(user.Secret);
        using var loginResponse = await AuthFactory.LoginAsync(client, user.Email, user.Password, signInCode);
        var login = (await loginResponse.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUpAsync(login.AccessToken, signInCode)).Status);

        // Recovery codes are not accepted either.
        Assert.Equal(HttpStatusCode.Forbidden, (await StepUpAsync(login.AccessToken, user.RecoveryCodes[0])).Status);

        // No access token, no grant.
        using (var anonymous = await client.PostAsJsonAsync(AuthRoutes.StepUp, new StepUpRequest(factory.NextCode(user.Secret), paper, hash), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var (status, grant) = await StepUpAsync(login.AccessToken, factory.NextCode(user.Secret));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(factory.Clock.GetUtcNow().AddMinutes(5), grant!.ExpiresAt);

        Assert.False(await ConsumeAsync(user.Id, grant.StepUpToken, Guid.NewGuid(), hash));      // another working paper
        Assert.False(await ConsumeAsync(user.Id, grant.StepUpToken, paper, "content-changed"));  // edited after the code was entered
        Assert.False(await ConsumeAsync(Guid.NewGuid(), grant.StepUpToken, paper, hash));        // another user
        Assert.True(await ConsumeAsync(user.Id, grant.StepUpToken, paper, hash));
        Assert.False(await ConsumeAsync(user.Id, grant.StepUpToken, paper, hash));               // spent

        var (_, stale) = await StepUpAsync(login.AccessToken, factory.NextCode(user.Secret));
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await ConsumeAsync(user.Id, stale!.StepUpToken, paper, hash));              // older than 5 minutes

        var actions = (await factory.AuditAsync()).Where(a => a.EntityId == user.Id.ToString("D")).Select(a => a.Action).ToList();
        Assert.Equal(2, actions.Count(a => a == "auth.step_up.issued"));
        Assert.Single(actions, a => a == "auth.step_up.consumed");
    }

    // ----- Password reset and MFA reset ---------------------------------------------------------------------------

    [Fact]
    public async Task Password_reset_changes_the_password_signs_every_device_out_and_still_needs_totp()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var session = await factory.SignInAsync(client, user);
        const string newPassword = "mot-mat-khau-moi-hoan-toan-77";

        // Unknown addresses get the same answer and no mail.
        var before = factory.Mail.Sent.Count;
        using (var unknown = await client.PostAsJsonAsync(AuthRoutes.ForgotPassword, new ForgotPasswordRequest("ghost@audit.io.vn"), Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        }

        Assert.Equal(before, factory.Mail.Sent.Count);

        using (var known = await client.PostAsJsonAsync(AuthRoutes.ForgotPassword, new ForgotPasswordRequest(user.Email), Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        }

        var token = factory.Mail.TokenFor(user.Email);
        Assert.Contains("/account/reset?token=", factory.Mail.Sent.Last(m => m.ToAddress == user.Email).TextBody, StringComparison.Ordinal);

        // A reset link does not open the enrolment page.
        using (var wrongPage = await client.GetAsync(Rel($"/account/enrol?token={token}"), Ct))
        {
            Assert.Equal(HttpStatusCode.Gone, wrongPage.StatusCode);
        }

        using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["password"] = newPassword, ["confirm"] = newPassword }))
        using (var reset = await client.PostAsync(Rel("/account/reset"), form, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, session.AccessToken));
        using (var old = await AuthFactory.LoginAsync(client, user.Email, user.Password, factory.NextCode(user.Secret)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        using (var noCode = await AuthFactory.LoginAsync(client, user.Email, newPassword, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        }

        await factory.SignInAsync(client, user with { Password = newPassword });

        // The link worked once.
        using var reuse = await client.GetAsync(Rel($"/account/reset?token={token}"), Ct);
        Assert.Equal(HttpStatusCode.Gone, reuse.StatusCode);
    }

    [Fact]
    public async Task Password_reset_requests_are_limited_to_three_per_hour_and_expire_after_30_minutes()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var before = factory.Mail.Sent.Count(m => m.ToAddress == user.Email);

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsJsonAsync(AuthRoutes.ForgotPassword, new ForgotPasswordRequest(user.Email), Ct);
        }

        Assert.Equal(before + 3, factory.Mail.Sent.Count(m => m.ToAddress == user.Email));

        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        using var expired = await client.GetAsync(Rel($"/account/reset?token={factory.Mail.TokenFor(user.Email)}"), Ct);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Fact]
    public async Task Mfa_reset_ends_sessions_and_re_enrolment_needs_the_current_password()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var user = await factory.EnrolAsync();
        var session = await factory.SignInAsync(client, user);

        var reset = await factory.ScopeAsync(sp => sp.GetRequiredService<AccountService>().ResetMfaAsync(Actor.Console("tu"), user.Id, Ct));
        Assert.True(reset.Value!.MailSent);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, session.AccessToken));
        using (var oldCode = await AuthFactory.LoginAsync(client, user.Email, user.Password, factory.NextCode(user.Secret)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
        }

        using (var oldRecovery = await AuthFactory.LoginAsync(client, user.Email, user.Password, null, user.RecoveryCodes[1]))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldRecovery.StatusCode);
        }

        // Holding the mailbox is not enough: without the password no QR code is shown.
        var token = factory.Mail.TokenFor(user.Email);
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["password"] = "not-the-password-1" }))
        using (var wrong = await client.PostAsync(Rel("/account/enrol"), form, Ct))
        {
            var html = await wrong.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("name=\"ticket\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<svg", html, StringComparison.Ordinal);
        }

        // Nor can the code step be reached with a made-up ticket.
        var forged = await AuthFactory.ConfirmRawAsync(client, token, "v1.AAAA", "000000");
        Assert.DoesNotContain("<svg", forged, StringComparison.Ordinal);

        var (secret, ticket) = await AuthFactory.SubmitPasswordAsync(client, token, user.Password);
        Assert.NotEqual(user.Secret, secret);
        var codes = await AuthFactory.ConfirmAsync(client, token, ticket, Totp.Compute(secret, Totp.StepAt(factory.Clock.GetUtcNow())));
        Assert.Empty(codes.Intersect(user.RecoveryCodes));
        await factory.SignInAsync(client, user with { Secret = secret });
    }

    // ----- Administration -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_api_is_closed_to_users_and_never_returns_an_invitation_link()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var admin = await factory.SignInAsync(client, await factory.EnrolAsync(SystemRole.Admin));
        var user = await factory.SignInAsync(client, await factory.EnrolAsync());

        using (var anonymous = await client.GetAsync(Rel(AuthRoutes.AdminUsers), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var asUser = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Get, AuthRoutes.AdminUsers, user.AccessToken), Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
        }

        var email = $"invitee-{Guid.NewGuid():N}@audit.io.vn";
        using (var invite = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Post, AuthRoutes.AdminUsers, admin.AccessToken,
                   new InviteUserRequest(email, "Invitee", "User")), Ct))
        {
            var body = await invite.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(factory.Mail.TokenFor(email), body, StringComparison.Ordinal);
        }

        using (var badRole = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Post, AuthRoutes.AdminUsers, admin.AccessToken,
                   new InviteUserRequest($"x-{Guid.NewGuid():N}@audit.io.vn", "X", "Root")), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        }

        using var list = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Get, AuthRoutes.AdminUsers, admin.AccessToken), Ct);
        var users = await list.Content.ReadFromJsonAsync<List<AdminUserInfo>>(Ct);
        var invitee = Assert.Single(users!, u => u.Email == email);
        Assert.False(invitee.IsEnrolled);
        Assert.Contains((await factory.AuditAsync()), a => a.Action == "auth.invite.sent" && a.ActorUserId == admin.User.Id
            && a.EntityId == invitee.Id.ToString("D"));
    }

    [Fact]
    public async Task Disabling_a_user_or_revoking_a_laptop_takes_effect_on_the_next_request()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var admin = await factory.SignInAsync(client, await factory.EnrolAsync(SystemRole.Admin));
        var user = await factory.EnrolAsync();
        Guid laptopA = Guid.NewGuid(), laptopB = Guid.NewGuid();
        var a = await factory.SignInAsync(client, user, laptopA);
        var b = await factory.SignInAsync(client, user, laptopB);

        using (var devices = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Get, $"{AuthRoutes.AdminUsers}/{user.Id}/devices", admin.AccessToken), Ct))
        {
            Assert.Equal(2, (await devices.Content.ReadFromJsonAsync<List<DeviceInfo>>(Ct))!.Count(d => d.IsActive));
        }

        // Remove one laptop: its tokens die at once, the other keeps working.
        using (var revoke = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Delete, $"{AuthRoutes.AdminUsers}/{user.Id}/devices/{laptopA}", admin.AccessToken), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, a.AccessToken));
        Assert.Null(await RefreshAsync(client, a.RefreshToken, laptopA));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(client, b.AccessToken));

        using (var disable = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Post, $"{AuthRoutes.AdminUsers}/{user.Id}/disable", admin.AccessToken), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, b.AccessToken));
        Assert.Null(await RefreshAsync(client, b.RefreshToken, laptopB));
        using (var signIn = await AuthFactory.LoginAsync(client, user.Email, user.Password, factory.NextCode(user.Secret)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        }

        using (var enable = await client.SendAsync(AuthFactory.Authorized(HttpMethod.Post, $"{AuthRoutes.AdminUsers}/{user.Id}/enable", admin.AccessToken), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        }

        await factory.SignInAsync(client, user);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_disabled_or_demoted_and_a_role_change_ends_sessions()
    {
        // Own database: the rule depends on how many administrators exist.
        await using var isolated = new AuthFactory();
        await isolated.InitializeAsync();
        isolated.RequireDatabase();
        using var client = isolated.CreateClient();
        var only = await isolated.EnrolAsync(SystemRole.Admin);
        var admin = await isolated.SignInAsync(client, only);

        async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string accessToken, object? body = null)
        {
            using var response = await client.SendAsync(AuthFactory.Authorized(method, path, accessToken, body), Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Conflict, await SendAsync(HttpMethod.Post, $"{AuthRoutes.AdminUsers}/{only.Id}/disable", admin.AccessToken));
        Assert.Equal(HttpStatusCode.Conflict, await SendAsync(HttpMethod.Put, $"{AuthRoutes.AdminUsers}/{only.Id}/role", admin.AccessToken, new SetRoleRequest("User")));

        var second = await isolated.EnrolAsync();
        var secondSession = await isolated.SignInAsync(client, second);
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Put, $"{AuthRoutes.AdminUsers}/{second.Id}/role", admin.AccessToken, new SetRoleRequest("Admin")));

        // The promoted user's old token (role User) is dead; the new one carries Admin.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(client, secondSession.AccessToken));
        var promoted = await isolated.SignInAsync(client, second);
        Assert.Equal("Admin", promoted.User.Role);

        // With two administrators the first can now be demoted, by the second.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(HttpMethod.Put, $"{AuthRoutes.AdminUsers}/{only.Id}/role", promoted.AccessToken, new SetRoleRequest("User")));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(HttpMethod.Get, AuthRoutes.AdminUsers, admin.AccessToken));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(HttpMethod.Post, $"{AuthRoutes.AdminUsers}/{Guid.NewGuid()}/disable", promoted.AccessToken));
    }

    [Fact]
    public async Task Console_command_invites_the_first_administrator_and_lists_accounts()
    {
        await using var isolated = new AuthFactory();
        await isolated.InitializeAsync();
        isolated.RequireDatabase();
        using var _ = isolated.CreateClient();

        using var output = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(isolated.Services, ["invite", "tu@audit.io.vn", "Tu", "--admin", "--show-link"], output));
        Assert.Contains("https://master.test/account/enrol?token=" + isolated.Mail.TokenFor("tu@audit.io.vn"), output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await AdminCli.RunAsync(isolated.Services, ["list"], output));
        Assert.Matches(@"tu@audit\.io\.vn\s+Admin\s+invited", output.ToString());

        Assert.Equal(1, await AdminCli.RunAsync(isolated.Services, ["disable", "tu@audit.io.vn"], output));   // last administrator
        Assert.Equal(1, await AdminCli.RunAsync(isolated.Services, ["disable", "ghost@audit.io.vn"], output));
        Assert.Equal(2, await AdminCli.RunAsync(isolated.Services, ["frobnicate"], output));
        Assert.Equal(2, await AdminCli.RunAsync(isolated.Services, ["invite", "a@audit.io.vn", "A", "--root"], output));
    }

    [Fact]
    public async Task Without_a_master_key_the_master_refuses_to_authenticate_anyone()
    {
        await using var unconfigured = new AuthFactory { MasterKey = null };
        await unconfigured.InitializeAsync();
        unconfigured.RequireDatabase();
        using var client = unconfigured.CreateClient();

        using var login = await AuthFactory.LoginAsync(client, "tu@audit.io.vn", AuthFactory.Password, "123456");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        using var page = await client.GetAsync(Rel("/account/forgot"), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        using var output = new StringWriter();
        Assert.Equal(1, await AdminCli.RunAsync(unconfigured.Services, ["list"], output));

        // Health checks keep working, so a deploy without the key is not rolled back.
        using var live = await client.GetAsync(Rel("/healthz/ready"), Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Account_pages_send_no_store_and_no_referrer_headers()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Rel("/account/forgot"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }
}
