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

    /// <summary>Minimum rules: 8+ characters with letters and digits.</summary>
    public static string? Validate(string? password) =>
        password is null || password.Length < 8 ? "Password must be at least 8 characters."
        : !password.Any(char.IsLetter) || !password.Any(char.IsDigit) ? "Password must contain letters and digits."
        : null;
}
