using System.Security.Cryptography;

namespace Trading.Application.UseCases.Identity;

/// <summary>
/// Generates invitation codes. On an invitation-only platform the code is the only thing standing
/// between an outsider and an account, so it must be unpredictable: never derived from an email
/// address, a name, a counter, or a timestamp.
/// </summary>
public static class InvitationCodeGenerator
{
    /// <summary>
    /// 20 bytes of cryptographic randomness, giving 160 bits of entropy. Encoded with an alphabet
    /// that omits characters people confuse when retyping a code by hand.
    /// </summary>
    private const int EntropyBytes = 20;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(EntropyBytes);
        var chars = new char[bytes.Length * 2];

        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = Alphabet[bytes[i] & 0x1F];
            chars[(i * 2) + 1] = Alphabet[(bytes[i] >> 3) & 0x1F];
        }

        return new string(chars);
    }
}
