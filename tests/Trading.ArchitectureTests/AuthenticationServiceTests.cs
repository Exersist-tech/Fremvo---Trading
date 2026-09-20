using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers password storage and sign-in.
/// </summary>
/// <remarks>
/// These replace tests that asserted the previous behaviour: that any
/// password of eight characters or more signed in any active user. No
/// password was stored or compared at all, so those tests were pinning the
/// defect in place.
/// </remarks>
public sealed class AuthenticationServiceTests
{
    private const string CorrectPassword = "CorrectHorseBattery9!";

    private static readonly Pbkdf2PasswordHasher Hasher = new();

    private static User ActiveUser(string? password = CorrectPassword, RoleType role = RoleType.User) =>
        new(
            Guid.NewGuid(),
            "trader@example.com",
            "Trader",
            "en-US",
            "UTC",
            "USD",
            role,
            multiFactorAuthenticationEnabled: false,
            UserStatus.Active,
            passwordHash: password is null ? null : Hasher.Hash(password));

    private static User SuspendedUser() =>
        new(
            Guid.NewGuid(),
            "suspended@example.com",
            "Suspended",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            multiFactorAuthenticationEnabled: false,
            UserStatus.Suspended,
            passwordHash: Hasher.Hash(CorrectPassword));

    private static AuthenticationService Service() => new(Hasher);

    [Fact]
    public void TheCorrectPasswordSignsInAnActiveUser()
    {
        var outcome = Service().Authenticate(ActiveUser(), CorrectPassword);

        Assert.True(outcome.Succeeded);
        Assert.Equal(AuthenticationFailureReason.None, outcome.FailureReason);
    }

    [Fact]
    public void AWrongPasswordIsRejected()
    {
        var outcome = Service().Authenticate(ActiveUser(), "WrongHorseBattery9!");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AuthenticationFailureReason.IncorrectPassword, outcome.FailureReason);
    }

    [Fact]
    public void ALongButWrongPasswordIsRejected()
    {
        // The previous implementation accepted this: it checked only that the
        // password was at least eight characters long.
        var outcome = Service().Authenticate(ActiveUser(), "any password at all, quite long");

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void PasswordComparisonIsCaseSensitive()
    {
        Assert.False(Service().Authenticate(ActiveUser(), CorrectPassword.ToUpperInvariant()).Succeeded);

        // Flip the case of the first character only, so the difference is
        // unambiguously case and nothing else.
        var first = CorrectPassword[0];
        var flippedFirst = char.IsUpper(first)
            ? char.ToLower(first, System.Globalization.CultureInfo.InvariantCulture)
            : char.ToUpperInvariant(first);

        var flipped = flippedFirst + CorrectPassword[1..];

        Assert.False(Service().Authenticate(ActiveUser(), flipped).Succeeded);
    }

    [Fact]
    public void AnAccountWithNoPasswordCannotSignIn()
    {
        // An invited user who has not set a password must not be able to sign
        // in, and an absent hash must never read as "no password required".
        var outcome = Service().Authenticate(ActiveUser(password: null), "anything at all");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AuthenticationFailureReason.NoPasswordSet, outcome.FailureReason);
    }

    [Fact]
    public void AnAccountWithNoPasswordCannotSignInWithAnEmptyPassword()
    {
        Assert.False(Service().Authenticate(ActiveUser(password: null), string.Empty).Succeeded);
        Assert.False(Service().Authenticate(ActiveUser(password: null), null).Succeeded);
    }

    [Fact]
    public void AnUnknownAccountIsRejected()
    {
        var outcome = Service().Authenticate(null, CorrectPassword);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AuthenticationFailureReason.UnknownAccount, outcome.FailureReason);
    }

    [Fact]
    public void ASuspendedUserIsRejectedEvenWithTheCorrectPassword()
    {
        var outcome = Service().Authenticate(SuspendedUser(), CorrectPassword);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AuthenticationFailureReason.AccountSuspended, outcome.FailureReason);
    }

    [Fact]
    public void ASuspendedUserWithAWrongPasswordReportsTheWrongPasswordNotTheSuspension()
    {
        // Suspension is only disclosed once the password is known to be
        // correct. Otherwise a caller who does not hold the password could
        // still learn that the address exists.
        var outcome = Service().Authenticate(SuspendedUser(), "WrongHorseBattery9!");

        Assert.Equal(AuthenticationFailureReason.IncorrectPassword, outcome.FailureReason);
    }

    [Fact]
    public void AnEmptyOrWhitespacePasswordIsRejected()
    {
        Assert.False(Service().Authenticate(ActiveUser(), string.Empty).Succeeded);
        Assert.False(Service().Authenticate(ActiveUser(), "   ").Succeeded);
        Assert.False(Service().Authenticate(ActiveUser(), null).Succeeded);
    }
}

public sealed class PasswordHasherTests
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();

    [Fact]
    public void TheHashDoesNotContainThePassword()
    {
        const string password = "SecretPassphrase42!";

        var hash = Hasher.Hash(password);

        // Storing anything recoverable would mean a database leak hands over
        // every password directly.
        Assert.DoesNotContain(password, hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSamePasswordHashesDifferentlyEachTime()
    {
        const string password = "SecretPassphrase42!";

        // Distinct salts, so two users with the same password do not share a
        // hash and a precomputed table is useless.
        Assert.NotEqual(Hasher.Hash(password), Hasher.Hash(password));
    }

    [Fact]
    public void BothOfTwoIdenticalPasswordsStillVerify()
    {
        const string password = "SecretPassphrase42!";

        Assert.Equal(PasswordVerification.Success, Hasher.Verify(Hasher.Hash(password), password));
        Assert.Equal(PasswordVerification.Success, Hasher.Verify(Hasher.Hash(password), password));
    }

    [Fact]
    public void TheHashRecordsItsAlgorithmAndCost()
    {
        var parts = Hasher.Hash("SecretPassphrase42!").Split('$');

        // The cost is stored rather than assumed, so it can be raised later
        // without invalidating existing passwords.
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal(Pbkdf2PasswordHasher.CurrentIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[1]);
    }

    [Fact]
    public void AWrongPasswordFailsVerification()
    {
        var hash = Hasher.Hash("SecretPassphrase42!");

        Assert.Equal(PasswordVerification.Failed, Hasher.Verify(hash, "SecretPassphrase42"));
        Assert.Equal(PasswordVerification.Failed, Hasher.Verify(hash, "secretpassphrase42!"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$600000$onlythree")]
    [InlineData("bcrypt$600000$c2FsdA==$c3Via2V5")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$c3Via2V5")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$c3Via2V5")]
    [InlineData("pbkdf2-sha256$600000$not base64!$c3Via2V5")]
    [InlineData("pbkdf2-sha256$600000$$")]
    public void AnUnreadableStoredHashNeverVerifies(string storedHash)
    {
        // A corrupt, truncated or foreign hash must fail closed. Treating it
        // as a pass would turn a data problem into an authentication bypass.
        Assert.Equal(PasswordVerification.Failed, Hasher.Verify(storedHash, "anything"));
    }

    [Fact]
    public void ANullStoredHashNeverVerifies()
    {
        Assert.Equal(PasswordVerification.Failed, Hasher.Verify(null, "anything"));
    }

    [Fact]
    public void AHashWithFewerIterationsVerifiesButAsksToBeUpgraded()
    {
        const string password = "SecretPassphrase42!";

        var weak = Hasher.Hash(password)
            .Replace(
                $"${Pbkdf2PasswordHasher.CurrentIterations}$",
                "$1000$",
                StringComparison.Ordinal);

        // Rebuild the subkey at the lower cost so the value is genuinely a
        // valid older hash rather than a corrupt one.
        var parts = weak.Split('$');
        var salt = Convert.FromBase64String(parts[2]);
        var subkey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var older = string.Join('$', parts[0], "1000", parts[2], Convert.ToBase64String(subkey));

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, Hasher.Verify(older, password));
    }

    [Fact]
    public void TheIterationCountMeetsTheCurrentGuidance()
    {
        // A low cost makes offline guessing cheap if the database is stolen.
        Assert.True(Pbkdf2PasswordHasher.CurrentIterations >= 600_000);
    }

    [Fact]
    public void HashingRefusesAnEmptyPassword()
    {
        Assert.Throws<ArgumentException>(() => Hasher.Hash(string.Empty));
        Assert.Throws<ArgumentException>(() => Hasher.Hash("   "));
    }

    [Fact]
    public void TheDecoyHashIsStableAndVerifiesNothing()
    {
        // Used to spend the same time on an unknown account as on a real one.
        Assert.Equal(Hasher.DecoyHash, Hasher.DecoyHash);
        Assert.Equal(PasswordVerification.Failed, Hasher.Verify(Hasher.DecoyHash, "anything"));
    }
}

public sealed class UserPasswordTests
{
    private static User NewUser(string? passwordHash = null) =>
        new(
            Guid.NewGuid(),
            "user@example.com",
            "User",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            multiFactorAuthenticationEnabled: false,
            UserStatus.Active,
            passwordHash);

    [Fact]
    public void ANewUserHasNoPasswordUnlessOneIsSupplied()
    {
        var user = NewUser();

        Assert.False(user.HasPassword);
        Assert.Null(user.PasswordHash);
    }

    [Fact]
    public void ABlankSuppliedHashIsTreatedAsNoPassword()
    {
        // Whitespace must not read as a credential.
        Assert.False(NewUser("   ").HasPassword);
        Assert.False(NewUser(string.Empty).HasPassword);
    }

    [Fact]
    public void SettingAPasswordHashMakesTheAccountSignInCapable()
    {
        var user = NewUser();

        user.SetPasswordHash("pbkdf2-sha256$600000$c2FsdA==$c3Via2V5");

        Assert.True(user.HasPassword);
    }

    [Fact]
    public void SettingABlankPasswordHashIsRefused()
    {
        var user = NewUser("pbkdf2-sha256$600000$c2FsdA==$c3Via2V5");

        // Clearing a credential must be deliberate, not a side effect of
        // passing an empty value to the setter.
        Assert.Throws<ArgumentException>(() => user.SetPasswordHash(string.Empty));
        Assert.Throws<ArgumentException>(() => user.SetPasswordHash("   "));
        Assert.True(user.HasPassword);
    }

    [Fact]
    public void RemovingAPasswordLeavesTheAccountUnableToSignIn()
    {
        var user = NewUser("pbkdf2-sha256$600000$c2FsdA==$c3Via2V5");

        user.RemovePassword();

        Assert.False(user.HasPassword);
    }
}

public sealed class RegistrationPasswordTests
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();

    [Fact]
    public void ARegisteredUserCanSignInWithTheChosenPassword()
    {
        const string password = "ChosenAtRegistration1!";

        var user = new RegistrationService(Hasher).Register(
            new RegisterUserRequest(
                "new@example.com",
                "New User",
                "en-US",
                "UTC",
                "USD",
                "INVITE-CODE",
                password),
            Guid.NewGuid(),
            Guid.NewGuid());

        // Registration previously validated the password and then discarded
        // it, leaving the new account with no credential at all.
        Assert.True(user.HasPassword);
        Assert.True(new AuthenticationService(Hasher).Authenticate(user, password).Succeeded);
    }

    [Fact]
    public void ARegisteredUserCannotSignInWithADifferentPassword()
    {
        var user = new RegistrationService(Hasher).Register(
            new RegisterUserRequest(
                "new@example.com",
                "New User",
                "en-US",
                "UTC",
                "USD",
                "INVITE-CODE",
                "ChosenAtRegistration1!"),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.False(new AuthenticationService(Hasher).Authenticate(user, "SomethingElse1!").Succeeded);
    }

    [Fact]
    public void TheRegisteredHashIsNotThePlaintext()
    {
        const string password = "ChosenAtRegistration1!";

        var user = new RegistrationService(Hasher).Register(
            new RegisterUserRequest(
                "new@example.com",
                "New User",
                "en-US",
                "UTC",
                "USD",
                "INVITE-CODE",
                password),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.DoesNotContain(password, user.PasswordHash!, StringComparison.OrdinalIgnoreCase);
    }
}
