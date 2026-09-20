using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Kraken;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the credential-permission probe: request signing, the guarantee that
/// checking for trade permission can never place a real order, and how Kraken's
/// responses are interpreted.
/// </summary>
public sealed class KrakenPermissionProbeTests
{
    // Kraken's own published example for the API-Sign header. If the signing
    // method is changed in a way that breaks authentication, this fails at
    // build time rather than appearing later as an unexplained permission error.
    private const string KrakenDocumentedSecret =
        "kQH5HW/8p1uGOVjbgWA7FunAmGO8lsSUXNsu3eow76sz84Q18fWxnyRzBHCd3pd5nE9qa99HAZtuZuj6F1huXg==";

    private const string KrakenDocumentedSignature =
        "4/dpxb3iT4tp/ZCVEwSnEsLxx0bqyhLpdfOpc6fn7OR8+UClSV5n9E6aSS8MPtnRfp32bAb0nmbRn6H8ndwLUQ==";

    [Fact]
    public void SignMatchesKrakensPublishedTestVector()
    {
        const string nonce = "1616492376594";
        const string postData =
            "nonce=1616492376594&ordertype=limit&pair=XBTUSD&price=37500&type=buy&volume=1.25";

        var signature = KrakenRequestSigner.Sign("/0/private/AddOrder", nonce, postData, KrakenDocumentedSecret);

        Assert.Equal(KrakenDocumentedSignature, signature);
    }

    [Fact]
    public void SignRejectsASecretThatIsNotBase64WithoutEchoingIt()
    {
        const string badSecret = "this-is-not-base64!!";

        var exception = Assert.Throws<FormatException>(
            () => KrakenRequestSigner.Sign("/0/private/Balance", "1", "nonce=1", badSecret));

        // The malformed value is the secret itself and must never appear in the
        // error text.
        Assert.DoesNotContain(badSecret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateNonceIsNonDecreasingOverTime()
    {
        var earlier = KrakenRequestSigner.CreateNonce(DateTimeOffset.UnixEpoch.AddSeconds(10));
        var later = KrakenRequestSigner.CreateNonce(DateTimeOffset.UnixEpoch.AddSeconds(11));

        Assert.True(long.Parse(later, System.Globalization.CultureInfo.InvariantCulture)
            > long.Parse(earlier, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The most important test in this file. Checking whether a key may trade
    /// is done by asking Kraken to validate an order. If the validate-only flag
    /// were ever dropped, connecting an API key would place a real order.
    /// </summary>
    [Fact]
    public void TradeProbeBodyAlwaysCarriesKrakensValidateOnlyFlag()
    {
        var body = KrakenPermissionProbe.BuildTradeProbeBody("1616492376594");

        Assert.Contains("validate=true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TradeProbeBodyNeverOmitsValidateForAnyNonce()
    {
        foreach (var nonce in new[] { "1", "999", "1616492376594", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            Assert.Contains(
                KrakenPermissionProbe.ValidateOnlyFlag,
                KrakenPermissionProbe.BuildTradeProbeBody(nonce),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InterpretResponseTreatsAnEmptyErrorArrayAsPermissionHeld()
    {
        var answer = KrakenPermissionProbe.InterpretResponse("""{"error":[],"result":{}}""");

        Assert.True(answer.Granted);
        Assert.False(answer.Denied);
    }

    [Fact]
    public void InterpretResponseTreatsPermissionDeniedAsPermissionAbsent()
    {
        var answer = KrakenPermissionProbe.InterpretResponse("""{"error":["EGeneral:Permission denied"]}""");

        Assert.False(answer.Granted);
        Assert.True(answer.Denied);
    }

    [Fact]
    public void InterpretResponseTreatsAnUnknownErrorAsInconclusiveRatherThanAbsent()
    {
        // An error the platform does not recognise says nothing about the key.
        // Reading a service outage as "permission absent" would tell the user
        // to change a key setting that is already correct.
        var answer = KrakenPermissionProbe.InterpretResponse("""{"error":["EService:Unavailable"]}""");

        Assert.False(answer.Granted);
        Assert.False(answer.Denied);
        Assert.Contains("EService:Unavailable", answer.Errors);
    }

    [Theory]
    [InlineData("""{"error":["EAPI:Invalid key"]}""")]
    [InlineData("""{"error":["EAPI:Invalid signature"]}""")]
    [InlineData("""{"error":["EAPI:Invalid nonce"]}""")]
    public void InterpretResponseDistinguishesABadCredentialFromAMissingPermission(string payload)
    {
        // Reporting a bad key as "permission absent" would tell the user the
        // wrong thing to fix.
        Assert.Throws<ExchangePermissionProbeException>(
            () => KrakenPermissionProbe.InterpretResponse(payload));
    }

    [Fact]
    public void InterpretResponseRejectsAResponseWithNoErrorField()
    {
        // Kraken answers HTTP 200 even on failure and reports problems only in
        // the error array. A response without that field is not a Kraken
        // response and must not be treated as success.
        Assert.Throws<ExchangePermissionProbeException>(
            () => KrakenPermissionProbe.InterpretResponse("""{"result":{}}"""));
    }

    [Fact]
    public void InterpretResponseRejectsAnEmptyBody()
    {
        Assert.Throws<ExchangePermissionProbeException>(() => KrakenPermissionProbe.InterpretResponse(""));
    }

    [Fact]
    public void ProbeIsRegisteredForKraken()
    {
        using var client = new HttpClient { BaseAddress = new Uri("https://api.kraken.com") };
        var probe = new KrakenPermissionProbe(client, TimeProvider.System);

        Assert.Equal(ExchangeKind.Kraken, probe.Exchange);
    }

    [Fact]
    public void KrakenConnectorExposesNoWithdrawalOperation()
    {
        // The platform must never be able to move user funds. This asserts the
        // capability is absent from the connector surface rather than merely
        // unused.
        //
        // Property accessors are excluded deliberately. Reporting that a key
        // *can* withdraw is exactly how a withdrawal-capable key is detected
        // and refused, so a read-only flag named CanWithdraw is required rather
        // than forbidden. What must not exist is an operation that performs
        // one.
        var withdrawalOperations = typeof(KrakenPermissionProbe).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => !method.IsSpecialName)
            .Where(method =>
                method.Name.StartsWith("Withdraw", StringComparison.OrdinalIgnoreCase)
                || method.Name.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("PerformWithdraw", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("RequestWithdraw", StringComparison.OrdinalIgnoreCase))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();

        Assert.Empty(withdrawalOperations);
    }
}
