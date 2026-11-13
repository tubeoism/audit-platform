using System.Text;
using AuditPlatform.Master.Auth;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Tests.Auth;

public class AuthPrimitivesTests
{
    private static AuthKeys Keys(byte fill = 7) =>
        new(Options.Create(new AuthOptions { MasterKey = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()) }));

    // RFC 6238 appendix B, SHA-1 rows, truncated to the 6 digits authenticator apps display.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Totp_matches_the_rfc_6238_test_vectors(long unixTime, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Compute(secret, unixTime / Totp.StepSeconds));
    }

    [Fact]
    public void Totp_accepts_one_step_of_clock_drift_and_nothing_further()
    {
        var secret = Totp.NewSecret();
        var now = new DateTimeOffset(2026, 11, 13, 5, 0, 10, TimeSpan.Zero);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Verify(secret, Totp.Compute(secret, step), now));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Compute(secret, step - 1), now));
        Assert.Equal(step + 1, Totp.Verify(secret, " " + Totp.Compute(secret, step + 1)[..3] + " " + Totp.Compute(secret, step + 1)[3..], now));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step - 2), now));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step + 2), now));
        Assert.Null(Totp.Verify(secret, "12345", now));
        Assert.Null(Totp.Verify(secret, "abcdef", now));
        Assert.Null(Totp.Verify(secret, null, now));
    }

    [Fact]
    public void Base32_and_key_uri_follow_the_authenticator_conventions()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890")));

        var uri = Totp.OtpAuthUri("Audit Platform", "an@audit.io.vn", Encoding.ASCII.GetBytes("12345678901234567890"));
        Assert.Equal("otpauth://totp/Audit%20Platform:an%40audit.io.vn?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ" +
                     "&issuer=Audit%20Platform&algorithm=SHA1&digits=6&period=30", uri);
    }

    [Fact]
    public void Protected_values_open_only_with_the_same_key_and_context_and_reject_tampering()
    {
        var keys = Keys();
        var sealedValue = keys.Protect("totp-secret"u8, "totp:user-1");

        Assert.Equal("totp-secret"u8.ToArray(), keys.Unprotect(sealedValue, "totp:user-1"));
        Assert.Null(keys.Unprotect(sealedValue, "totp:user-2"));
        Assert.Null(Keys(8).Unprotect(sealedValue, "totp:user-1"));
        Assert.Null(keys.Unprotect(sealedValue[..^2] + (sealedValue[^1] == 'A' ? "BB" : "AA"), "totp:user-1"));
        Assert.Null(keys.Unprotect("not-a-protected-value", "totp:user-1"));
        Assert.NotEqual(sealedValue, keys.Protect("totp-secret"u8, "totp:user-1"));
    }

    [Fact]
    public void Token_hashes_depend_on_the_master_key_and_the_purpose()
    {
        var keys = Keys();
        Assert.Equal(keys.Hash("refresh", "abc"), Keys().Hash("refresh", "abc"));
        Assert.NotEqual(keys.Hash("refresh", "abc"), keys.Hash("link", "abc"));
        Assert.NotEqual(keys.Hash("refresh", "abc"), Keys(8).Hash("refresh", "abc"));
        Assert.Equal(64, keys.Hash("refresh", "abc").Length);
    }

    [Fact]
    public void Master_key_must_be_long_enough_and_its_absence_is_reported()
    {
        Assert.True(Keys().IsConfigured);
        Assert.False(new AuthKeys(Options.Create(new AuthOptions())).IsConfigured);
        Assert.Throws<InvalidOperationException>(() =>
            new AuthKeys(Options.Create(new AuthOptions { MasterKey = Convert.ToBase64String(new byte[16]) })));
    }

    [Fact]
    public void Bundled_list_knows_common_long_passwords_in_any_letter_case()
    {
        var list = new LocalPasswordList();
        Assert.True(list.Contains("passwordpassword"));
        Assert.True(list.Contains("PassWord1234"));
        Assert.False(list.Contains("ghe-da-xanh-luot-song-42"));
    }

    private sealed class Breached(bool answer) : IBreachedPasswordChecker
    {
        public ValueTask<bool> IsBreachedAsync(string password, CancellationToken cancellationToken) => ValueTask.FromResult(answer);
    }

    [Theory]
    [InlineData("short-pass", PasswordProblem.TooShort)]
    [InlineData("aaaaaaaaaaaaaaaa", PasswordProblem.TooPredictable)]
    [InlineData("xx-NguyenVanAn-2026", PasswordProblem.TooPredictable)]
    [InlineData("ghe-da-xanh-luot-song-42", null)]
    public async Task Password_policy_checks_length_and_predictability(string password, PasswordProblem? expected)
    {
        var policy = new PasswordPolicy(new Breached(false), Options.Create(new AuthOptions()));
        Assert.Equal(expected, await policy.CheckAsync(password, "nguyenvanan@audit.io.vn", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Password_policy_refuses_breached_passwords()
    {
        var policy = new PasswordPolicy(new Breached(true), Options.Create(new AuthOptions()));
        Assert.Equal(PasswordProblem.Breached,
            await policy.CheckAsync("ghe-da-xanh-luot-song-42", "an@audit.io.vn", TestContext.Current.CancellationToken));
    }
}
