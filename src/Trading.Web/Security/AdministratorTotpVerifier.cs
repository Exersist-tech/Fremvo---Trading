using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Secrets;

namespace Trading.Web.Security;

internal sealed class AdministratorTotpVerifier(
    ISecretStore secrets, TradingDbContext db, TimeProvider time)
{
    public const string StepClaim = "trading:admin-mfa-step";

    public async Task<long?> VerifyAndRedeemAsync(
        Guid userId, string? code, CancellationToken cancellationToken)
    {
        if (code is not { Length: 6 } || code.Any(character => character is < '0' or > '9'))
            return null;

        var secret = await secrets.GetSecretAsync($"admin-mfa-{userId:N}", cancellationToken)
            .ConfigureAwait(false);
        var key = DecodeBase32(secret);
        var codeBytes = Encoding.ASCII.GetBytes(code);
        try
        {
            var now = time.GetUtcNow();
            var currentStep = now.ToUnixTimeSeconds() / 30;
            foreach (var step in new[] { currentStep, currentStep - 1, currentStep + 1 })
            {
                var expected = GenerateCode(key, step);
                var matches = CryptographicOperations.FixedTimeEquals(codeBytes, expected);
                CryptographicOperations.ZeroMemory(expected);
                if (!matches)
                    continue;

                db.AdministratorMfaRedemptions.Add(new AdministratorMfaRedemption
                {
                    UserId = userId,
                    TimeStep = step,
                    UsedAtUtc = now
                });
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), userId, "AdministratorMfaVerified",
                    "User", userId.ToString("N"), now, null, null, null));
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return step;
                }
                catch (DbUpdateException exception) when (exception.InnerException is SqlException sql
                    && sql.Number is 2601 or 2627)
                {
                    db.ChangeTracker.Clear();
                    return null;
                }
            }
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(codeBytes);
        }
    }

    internal static byte[] GenerateCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7fffffff;
        var digits = (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        CryptographicOperations.ZeroMemory(hash);
        return Encoding.ASCII.GetBytes(digits);
    }

    private static byte[] DecodeBase32(string secret)
    {
        if (secret.Length is < 52 or > 128)
            throw new InvalidOperationException("Administrator MFA secret configuration is invalid.");

        var result = new byte[secret.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        try
        {
            foreach (var character in secret)
            {
                var digit = character switch
                {
                    >= 'A' and <= 'Z' => character - 'A',
                    >= '2' and <= '7' => character - '2' + 26,
                    _ => throw new InvalidOperationException("Administrator MFA secret configuration is invalid.")
                };
                buffer = (buffer << 5 | digit) & 0xffff;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    result[index++] = (byte)(buffer >> bits);
                }
            }
            if (index < 32 || (buffer & ((1 << bits) - 1)) != 0)
                throw new InvalidOperationException("Administrator MFA secret configuration is invalid.");
            return result;
        }
        catch (InvalidOperationException)
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }
}
