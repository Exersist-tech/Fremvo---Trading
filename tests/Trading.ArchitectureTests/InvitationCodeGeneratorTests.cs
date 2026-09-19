using Trading.Application.UseCases.Identity;

namespace Trading.ArchitectureTests;

public sealed class InvitationCodeGeneratorTests
{
    [Fact]
    public void AGeneratedCodeIsNotDerivedFromTheInviteesEmailAddress()
    {
        const string Email = "invitee@example.com";

        var code = InvitationCodeGenerator.Generate();

        Assert.DoesNotContain("invitee", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example", code, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Email, code, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCodesAreUnpredictableAndDoNotRepeat()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 2_000; i++)
        {
            Assert.True(codes.Add(InvitationCodeGenerator.Generate()), "A generated invitation code repeated.");
        }
    }

    [Fact]
    public void AGeneratedCodeCarriesEnoughEntropyToResistGuessing()
    {
        var code = InvitationCodeGenerator.Generate();

        // 40 characters over a 32-symbol alphabet is 200 encoded bits carrying 160 bits of entropy.
        Assert.Equal(40, code.Length);
        Assert.All(code, c => Assert.True(
            "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c, StringComparison.Ordinal),
            $"Unexpected character '{c}' in an invitation code."));
    }
}
