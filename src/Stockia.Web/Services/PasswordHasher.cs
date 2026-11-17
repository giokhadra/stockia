using System.Security.Cryptography;

namespace Stockia.Web.Services;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);

    /// <summary>True when a stored hash should be replaced at the next successful login
    /// (an older algorithm, or a lower work factor).</summary>
    bool NeedsRehash(string stored);
}

/// <summary>SR-34: passwords are hashed with bcrypt, work factor 12; the password itself is never stored.
/// Hashes written before bcrypt (PBKDF2, format iterations.salt.hash) still verify, and
/// <see cref="NeedsRehash"/> tells the login to replace them with bcrypt.</summary>
public class BcryptPasswordHasher : IPasswordHasher
{
    public const int WorkFactor = 12;
    private static readonly Pbkdf2PasswordHasher Legacy = new();

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (!IsBcrypt(stored)) return Legacy.Verify(password, stored);
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, stored);
        }
        catch (Exception)
        {
            return false;   // a damaged hash never lets anyone in
        }
    }

    public bool NeedsRehash(string stored)
    {
        if (!IsBcrypt(stored)) return true;
        try
        {
            return BCrypt.Net.BCrypt.PasswordNeedsRehash(stored, WorkFactor);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsBcrypt(string stored) => stored.StartsWith("$2", StringComparison.Ordinal);
}

/// <summary>The walking skeleton's first hasher (PBKDF2-SHA256). Kept only to read hashes already
/// stored; new hashes are bcrypt (SR-34).</summary>
public class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 100_000;

    // Stored format: iterations.salt.hash (base64)
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iter) || iter <= 0) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            if (expected.Length == 0) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool NeedsRehash(string stored) => false;
}
