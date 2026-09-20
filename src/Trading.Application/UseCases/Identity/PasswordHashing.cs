using System.Security.Cryptography;

namespace Trading.Application.UseCases.Identity;

/// <summary>
/// The outcome of checking a supplied password against a stored hash.
/// </summary>
/// <remarks>
/// This is a three-state result rather than a boolean because "the stored
/// hash is in an older format" is a success that carries an obligation: the
/// caller should re-hash and store the password using the current parameters.
/// Collapsing that into <c>true</c> would leave weakly hashed passwords in
/// the database indefinitely.
/// </remarks>
public enum PasswordVerification
{
    /// <summary>The password does not match.</summary>
    Failed = 0,

    /// <summary>The password matches and the stored hash is current.</summary>
    Success,

    /// <summary>
    /// The password matches, but the stored hash uses superseded parameters
    /// and should be replaced with a freshly computed one.
    /// </summary>
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    /// <summary>
    /// Produces a self-describing hash string safe to store in SQL.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Checks a password against a stored hash without revealing, through
    /// timing or through the result, anything beyond match or no match.
    /// </summary>
    PasswordVerification Verify(string? storedHash, string? password);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
/// </summary>
/// <remarks>
/// <para>
/// A password is never stored, and a hash is never reversible. Each password
/// gets its own 128-bit random salt, so two users choosing the same password
/// produce different hashes and a precomputed table is useless against the
/// database.
/// </para>
/// <para>
/// The iteration count is deliberately high to make offline guessing
/// expensive if the database is ever stolen. It is stored inside the hash
/// string rather than assumed, so the count can be raised later without
/// invalidating existing passwords: an older hash still verifies and is
/// reported as needing a rehash.
/// </para>
/// <para>
/// PBKDF2 was chosen over a memory-hard algorithm such as Argon2id only
/// because it is in the base class library and adds no dependency. It is
/// weaker against GPU attack. The format carries a version marker precisely
/// so the algorithm can be replaced without a migration that would otherwise
/// have to reset every password.
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>
    /// OWASP's 2023 floor for PBKDF2-HMAC-SHA256. Public so the cost can be
    /// asserted by tests rather than silently drifting downwards.
    /// </summary>
    public const int CurrentIterations = 600_000;

    private const int SaltBytes = 16;
    private const int SubkeyBytes = 32;
    private const string Version1 = "pbkdf2-sha256";

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>
    /// A hash of a password nobody holds, used to spend the same time on an
    /// unknown account as on a real one. Computed once; it reveals nothing
    /// because the input is random and discarded.
    /// </summary>
    private readonly Lazy<string> _decoyHash;

    public Pbkdf2PasswordHasher() =>
        _decoyHash = new Lazy<string>(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    /// <summary>
    /// A stored hash for an account that does not exist or has no password
    /// set, so the caller can perform a real verification anyway.
    /// </summary>
    /// <remarks>
    /// Without this, an unknown address returns immediately while a known one
    /// spends the full iteration cost, and the difference tells an attacker
    /// which addresses are registered. On an invitation-only platform that
    /// membership is itself worth protecting.
    /// </remarks>
    public string DecoyHash => _decoyHash.Value;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, CurrentIterations, Algorithm, SubkeyBytes);

        return string.Join(
            '$',
            Version1,
            CurrentIterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(subkey));
    }

    public PasswordVerification Verify(string? storedHash, string? password)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(password))
        {
            return PasswordVerification.Failed;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Version1, StringComparison.Ordinal))
        {
            // An unreadable hash is a failure, never a pass. A corrupt or
            // truncated value must not be treated as "no password required".
            return PasswordVerification.Failed;
        }

        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
        {
            return PasswordVerification.Failed;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordVerification.Failed;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return PasswordVerification.Failed;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);

        // Fixed-time comparison. A byte-by-byte comparison that returns early
        // leaks how much of the hash matched, which is enough to reconstruct
        // it one byte at a time.
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        return iterations < CurrentIterations
            ? PasswordVerification.SuccessRehashNeeded
            : PasswordVerification.Success;
    }
}
