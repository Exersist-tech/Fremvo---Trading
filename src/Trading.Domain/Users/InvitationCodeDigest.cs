using System.Security.Cryptography;
using System.Text;

namespace Trading.Domain.Users;

public static class InvitationCodeDigest
{
    public static string Compute(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            throw new ArgumentException("An invitation code is required.", nameof(code));
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Any(character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Invitation code contains unsupported characters.", nameof(code));
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
    }
}
