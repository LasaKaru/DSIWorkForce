using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSI.WorkForce.Core.Licensing;

/// <summary>
/// License key format:  HLO2-&lt;base64url(json payload)&gt;.&lt;base64url(ECDSA P-256 / SHA-256 signature)&gt;
/// Keys are signed with Helao2's private key (kept by Helao2, used only by the license generator).
/// The product verifies with the embedded public key, so a key cannot be forged or altered (e.g. a changed expiry).
/// </summary>
public static class LicenseCodec
{
    public const string ProductCode = "DSI.WorkForce";
    public const string Prefix = "HLO2-";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Used by the license generator only (needs the private key).</summary>
    public static string Sign(LicenseInfo license, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var payload = JsonSerializer.SerializeToUtf8Bytes(license, Json);
        var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        return Prefix + B64(payload) + "." + B64(signature);
    }

    /// <summary>Checks signature, product, installation and expiry. Never throws for bad input.</summary>
    public static LicenseCheck Verify(string? key, IEnumerable<string> trustedPublicKeys, string? installationId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(key)) return new(LicenseStatus.Missing, null, "No license key has been entered.");

        // Accept keys pasted with spaces / line breaks.
        var compact = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!compact.StartsWith(Prefix, StringComparison.Ordinal) || compact.Count(c => c == '.') != 1)
            return new(LicenseStatus.Invalid, null, "This is not a valid license key.");

        byte[] payload, signature;
        try
        {
            var parts = compact[Prefix.Length..].Split('.');
            payload = FromB64(parts[0]);
            signature = FromB64(parts[1]);
        }
        catch (FormatException) { return new(LicenseStatus.Invalid, null, "This is not a valid license key."); }

        var signedByHelao2 = false;
        foreach (var publicKey in trustedPublicKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                if (ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256)) { signedByHelao2 = true; break; }
            }
            catch (CryptographicException) { }
        }
        if (!signedByHelao2) return new(LicenseStatus.Invalid, null, "The license key signature is not valid.");

        LicenseInfo? license;
        try { license = JsonSerializer.Deserialize<LicenseInfo>(payload, Json); }
        catch (JsonException) { license = null; }
        if (license is null) return new(LicenseStatus.Invalid, null, "The license key content is not readable.");

        if (!string.Equals(license.Product, ProductCode, StringComparison.Ordinal))
            return new(LicenseStatus.WrongProduct, license, $"This key is for '{license.Product}', not for {ProductCode}.");
        if (license.InstallationId is { Length: > 0 } boundTo && !string.Equals(boundTo, installationId, StringComparison.OrdinalIgnoreCase))
            return new(LicenseStatus.WrongInstallation, license, "This key was issued for a different installation.");
        if (license.ExpiresUtc is { } expires && utcNow >= expires)
            return new(LicenseStatus.Expired, license, $"The license expired on {expires.ToLocalTime():d MMM yyyy}.");

        var until = license.ExpiresUtc is { } e ? $"until {e.ToLocalTime():d MMM yyyy}" : "with no expiry date";
        return new(LicenseStatus.Valid, license, $"{license.Type} license for {license.LicensedTo}, valid {until}.");
    }

    /// <summary>Creates a new signing key pair. Returns (private key PEM - keep secret!, public key for the product).</summary>
    public static (string PrivateKeyPem, string PublicKeyBase64) CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyFromPrivate(string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    /// <summary>Formats a key in groups for e-mails / printing (whitespace is ignored when entering it).</summary>
    public static string Pretty(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 64) sb.AppendLine(key.Substring(i, Math.Min(64, key.Length - i)));
        return sb.ToString().TrimEnd();
    }
}
