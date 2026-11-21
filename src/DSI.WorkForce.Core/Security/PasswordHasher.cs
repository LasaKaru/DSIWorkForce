using System.Security.Cryptography;

namespace DSI.WorkForce.Core.Security;

/// <summary>PBKDF2-SHA256 with a random salt (format: v1.iterations.salt.hash, base64). Never store plain passwords.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;   // OWASP recommendation for PBKDF2-HMAC-SHA256
    private const int SaltSize = 16, HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public const int MinLength = 6;

    /// <summary>Rules: at least 6 characters, not only spaces, and not the same as the user name.</summary>
    public static string? Validate(string? password, string? userName = null) =>
        string.IsNullOrWhiteSpace(password) ? "Please enter a password."
        : password.Length < MinLength ? $"Password must be at least {MinLength} characters."
        : userName is not null && string.Equals(password.Trim(), userName.Trim(), StringComparison.OrdinalIgnoreCase) ? "Password must not be the same as the user name."
        : null;
}
