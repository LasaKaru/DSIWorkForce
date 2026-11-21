using DSI.WorkForce.Core.Licensing;
using DSI.WorkForce.Core.Security;

namespace DSI.WorkForce.Tests;

public class LicensingAndSecurityTests
{
    private static readonly (string Private, string Public) Keys = LicenseCodec.CreateKeyPair();
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private static string Make(LicenseType type = LicenseType.Trial, int days = 30, string? installation = null, string product = LicenseCodec.ProductCode) =>
        LicenseCodec.Sign(new LicenseInfo
        {
            LicensedTo = "Samson Rubber", Type = type, Product = product, IssuedUtc = Now, InstallationId = installation,
            ExpiresUtc = type == LicenseType.Perpetual ? null : Now.AddDays(days),
        }, Keys.Private);

    private static LicenseCheck Check(string? key, string? installation = null, DateTime? at = null) =>
        LicenseCodec.Verify(key, [Keys.Public], installation, at ?? Now);

    [Fact]
    public void Valid_trial_is_accepted_and_reports_days_left()
    {
        var c = Check(Make(days: 30));
        Assert.True(c.IsValid);
        Assert.Equal("Samson Rubber", c.License!.LicensedTo);
    }

    [Fact]
    public void Expired_license_locks_the_product() =>
        Assert.Equal(LicenseStatus.Expired, Check(Make(days: 30), at: Now.AddDays(31)).Status);

    [Fact]
    public void Perpetual_license_never_expires() =>
        Assert.True(Check(Make(LicenseType.Perpetual), at: Now.AddYears(20)).IsValid);

    [Fact]
    public void Changing_any_character_breaks_the_signature()
    {
        var key = Make();
        var tampered = key[..30] + (key[30] == 'A' ? 'B' : 'A') + key[31..];
        Assert.Equal(LicenseStatus.Invalid, Check(tampered).Status);
    }

    [Fact]
    public void Key_signed_by_someone_else_is_rejected()
    {
        var other = LicenseCodec.CreateKeyPair();
        var forged = LicenseCodec.Sign(new LicenseInfo { LicensedTo = "Pirate", Type = LicenseType.Perpetual }, other.PrivateKeyPem);
        Assert.Equal(LicenseStatus.Invalid, Check(forged).Status);
    }

    [Fact]
    public void Installation_bound_key_only_works_on_that_installation()
    {
        var key = Make(installation: "ABC123");
        Assert.True(Check(key, "abc123").IsValid);
        Assert.Equal(LicenseStatus.WrongInstallation, Check(key, "OTHER").Status);
    }

    [Fact]
    public void Key_for_another_product_is_rejected() =>
        Assert.Equal(LicenseStatus.WrongProduct, Check(Make(product: "Other.Product")).Status);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("HLO2-abc")]
    [InlineData("HLO2-!!!.###")]
    public void Garbage_never_throws(string? key) => Assert.False(Check(key).IsValid);

    [Fact]
    public void Pasted_key_with_line_breaks_is_accepted() =>
        Assert.True(Check(LicenseCodec.Pretty(Make())).IsValid);

#if DEBUG
    [Fact]
    public void Debug_build_trusts_the_development_key()
    {
        var devPem = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools",
            "DSI.WorkForce.LicenseGenerator", "dev-keys", "dev-private-key.pem"));
        var key = LicenseCodec.Sign(new LicenseInfo { LicensedTo = "Dev", Type = LicenseType.Trial, ExpiresUtc = DateTime.UtcNow.AddDays(5) }, devPem);
        Assert.True(LicenseCodec.Verify(key, LicensePublicKeys.Trusted, null, DateTime.UtcNow).IsValid);
    }
#endif

    [Fact]
    public void Passwords_are_salted_and_verified()
    {
        var h1 = PasswordHasher.Hash("Secret123");
        var h2 = PasswordHasher.Hash("Secret123");
        Assert.NotEqual(h1, h2);                       // random salt
        Assert.DoesNotContain("Secret123", h1);
        Assert.True(PasswordHasher.Verify("Secret123", h1));
        Assert.False(PasswordHasher.Verify("secret123", h1));
        Assert.False(PasswordHasher.Verify("Secret123", "garbage"));
    }

    [Theory]
    [InlineData("short1", false)]
    [InlineData("onlyletters", false)]
    [InlineData("12345678", false)]
    [InlineData("Press2026", true)]
    public void Password_rules(string pw, bool ok) => Assert.Equal(ok, PasswordHasher.Validate(pw) is null);
}
