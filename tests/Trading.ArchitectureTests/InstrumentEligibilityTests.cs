using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Xunit;

namespace Trading.ArchitectureTests;

public sealed class InstrumentEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxEvidenceAge = TimeSpan.FromHours(24);

    private static EligibilityScope Scope(
        EligibilityPurpose purpose,
        CandleInterval interval = CandleInterval.FourHours,
        TradingProductType product = TradingProductType.Spot) =>
        new(purpose, interval, product);

    private static InstrumentEligibility NewEligibility() => new(Guid.NewGuid());

    private static void GrantChain(
        InstrumentEligibility eligibility,
        CandleInterval interval,
        params EligibilityPurpose[] purposes)
    {
        foreach (var purpose in purposes)
        {
            var scope = Scope(purpose, interval);
            eligibility.Grant(
                scope,
                Now,
                Now,
                MaxEvidenceAge,
                scope.IsLive ? Guid.NewGuid() : null);
        }
    }

    [Fact]
    public void NewInstrumentHoldsNoGrants()
    {
        var eligibility = NewEligibility();

        Assert.Empty(eligibility.Grants);
        Assert.False(eligibility.IsGranted(Scope(EligibilityPurpose.Research), Now, MaxEvidenceAge));
    }

    [Fact]
    public void ResearchIsTheEntryPointAndNeedsNoPrerequisite()
    {
        var eligibility = NewEligibility();

        eligibility.Grant(Scope(EligibilityPurpose.Research), Now, Now, MaxEvidenceAge);

        Assert.True(eligibility.IsGranted(Scope(EligibilityPurpose.Research), Now, MaxEvidenceAge));
    }

    [Fact]
    public void AHigherPurposeCannotBeGrantedWithoutItsPrerequisite()
    {
        var eligibility = NewEligibility();

        Assert.Throws<InvalidOperationException>(() =>
            eligibility.Grant(Scope(EligibilityPurpose.Backtest), Now, Now, MaxEvidenceAge));
    }

    [Fact]
    public void LiveTradingCannotBeGrantedToAnInstrumentThatIsNotPaperEligible()
    {
        var eligibility = NewEligibility();
        GrantChain(eligibility, CandleInterval.FourHours, EligibilityPurpose.Research, EligibilityPurpose.Backtest);

        Assert.Throws<InvalidOperationException>(() =>
            eligibility.Grant(Scope(EligibilityPurpose.SpotLive), Now, Now, MaxEvidenceAge, Guid.NewGuid()));
    }

    [Fact]
    public void ALiveGrantRequiresAnExplicitApprovingAdministrator()
    {
        var eligibility = NewEligibility();
        GrantChain(
            eligibility,
            CandleInterval.FourHours,
            EligibilityPurpose.Research,
            EligibilityPurpose.Backtest,
            EligibilityPurpose.Paper,
            EligibilityPurpose.SpotTest);

        Assert.Throws<ArgumentException>(() =>
            eligibility.Grant(Scope(EligibilityPurpose.SpotLive), Now, Now, MaxEvidenceAge, approvedByUserId: null));
    }

    [Fact]
    public void ANonLiveGrantDoesNotRequireAnApprover()
    {
        var eligibility = NewEligibility();

        eligibility.Grant(Scope(EligibilityPurpose.Research), Now, Now, MaxEvidenceAge);

        Assert.Null(Assert.Single(eligibility.Grants).ApprovedByUserId);
    }

    [Fact]
    public void RevokingALowerPurposeCascadesToEveryPurposeAboveIt()
    {
        var eligibility = NewEligibility();
        GrantChain(
            eligibility,
            CandleInterval.FourHours,
            EligibilityPurpose.Research,
            EligibilityPurpose.Backtest,
            EligibilityPurpose.Paper,
            EligibilityPurpose.SpotTest,
            EligibilityPurpose.SpotLive);

        var revoked = eligibility.Revoke(Scope(EligibilityPurpose.Backtest));

        Assert.Contains(Scope(EligibilityPurpose.Backtest), revoked);
        Assert.Contains(Scope(EligibilityPurpose.Paper), revoked);
        Assert.Contains(Scope(EligibilityPurpose.SpotTest), revoked);
        Assert.Contains(Scope(EligibilityPurpose.SpotLive), revoked);
        Assert.DoesNotContain(Scope(EligibilityPurpose.Research), revoked);
        Assert.True(eligibility.IsGranted(Scope(EligibilityPurpose.Research), Now, MaxEvidenceAge));
    }

    [Fact]
    public void RevocationOnlyAffectsTheSameIntervalAndProductType()
    {
        var eligibility = NewEligibility();
        GrantChain(eligibility, CandleInterval.FourHours, EligibilityPurpose.Research, EligibilityPurpose.Backtest);
        GrantChain(eligibility, CandleInterval.OneMinute, EligibilityPurpose.Research, EligibilityPurpose.Backtest);

        eligibility.Revoke(Scope(EligibilityPurpose.Research, CandleInterval.OneMinute));

        Assert.True(eligibility.IsGranted(
            Scope(EligibilityPurpose.Backtest, CandleInterval.FourHours), Now, MaxEvidenceAge));
        Assert.False(eligibility.IsGranted(
            Scope(EligibilityPurpose.Backtest, CandleInterval.OneMinute), Now, MaxEvidenceAge));
    }

    [Fact]
    public void APairEligibleForFourHoursIsNotTherebyEligibleForOneMinute()
    {
        var eligibility = NewEligibility();
        GrantChain(
            eligibility,
            CandleInterval.FourHours,
            EligibilityPurpose.Research,
            EligibilityPurpose.Backtest,
            EligibilityPurpose.Paper);

        Assert.True(eligibility.IsGranted(
            Scope(EligibilityPurpose.Paper, CandleInterval.FourHours), Now, MaxEvidenceAge));
        Assert.False(eligibility.IsGranted(
            Scope(EligibilityPurpose.Paper, CandleInterval.OneMinute), Now, MaxEvidenceAge));
    }

    [Fact]
    public void SpotEligibilityDoesNotConferFuturesEligibility()
    {
        var eligibility = NewEligibility();
        GrantChain(
            eligibility,
            CandleInterval.FourHours,
            EligibilityPurpose.Research,
            EligibilityPurpose.Backtest,
            EligibilityPurpose.Paper,
            EligibilityPurpose.SpotTest,
            EligibilityPurpose.SpotLive);

        Assert.False(eligibility.IsGranted(
            new EligibilityScope(EligibilityPurpose.FuturesTest, CandleInterval.FourHours, TradingProductType.Futures),
            Now,
            MaxEvidenceAge));
    }

    [Fact]
    public void ExpiredEvidenceMeansTheGrantConfersNothing()
    {
        var eligibility = NewEligibility();
        GrantChain(eligibility, CandleInterval.FourHours, EligibilityPurpose.Research);

        var muchLater = Now.Add(MaxEvidenceAge).AddMinutes(1);

        Assert.False(eligibility.IsGranted(Scope(EligibilityPurpose.Research), muchLater, MaxEvidenceAge));
    }

    [Fact]
    public void AGrantIsLostWhenItsPrerequisiteEvidenceExpiresEvenIfItsOwnIsFresh()
    {
        var eligibility = NewEligibility();
        eligibility.Grant(Scope(EligibilityPurpose.Research), Now, Now, MaxEvidenceAge);

        // The backtest grant is refreshed later, but the research evidence beneath it is not.
        var later = Now.AddHours(20);
        eligibility.Grant(Scope(EligibilityPurpose.Backtest), later, later, MaxEvidenceAge);

        var afterResearchExpiry = Now.Add(MaxEvidenceAge).AddMinutes(1);

        Assert.False(eligibility.IsGranted(Scope(EligibilityPurpose.Backtest), afterResearchExpiry, MaxEvidenceAge));
    }

    [Fact]
    public void ExpireStaleRemovesExpiredGrantsAndCascades()
    {
        var eligibility = NewEligibility();
        GrantChain(
            eligibility,
            CandleInterval.FourHours,
            EligibilityPurpose.Research,
            EligibilityPurpose.Backtest,
            EligibilityPurpose.Paper);

        var afterExpiry = Now.Add(MaxEvidenceAge).AddMinutes(1);
        var revoked = eligibility.ExpireStale(afterExpiry, MaxEvidenceAge);

        Assert.Equal(3, revoked.Count);
        Assert.Empty(eligibility.Grants);
    }

    [Fact]
    public void RevokeAllClearsEveryGrant()
    {
        var eligibility = NewEligibility();
        GrantChain(eligibility, CandleInterval.FourHours, EligibilityPurpose.Research, EligibilityPurpose.Backtest);
        GrantChain(eligibility, CandleInterval.OneDay, EligibilityPurpose.Research);

        var revoked = eligibility.RevokeAll();

        Assert.Equal(3, revoked.Count);
        Assert.Empty(eligibility.Grants);
    }

    [Fact]
    public void RegrantingRefreshesTheEvidenceTimestamp()
    {
        var eligibility = NewEligibility();
        eligibility.Grant(Scope(EligibilityPurpose.Research), Now, Now, MaxEvidenceAge);

        var later = Now.AddHours(20);
        eligibility.Grant(Scope(EligibilityPurpose.Research), later, later, MaxEvidenceAge);

        Assert.True(eligibility.IsGranted(Scope(EligibilityPurpose.Research), later.AddHours(1), MaxEvidenceAge));
        Assert.Single(eligibility.Grants);
    }

    [Fact]
    public void EvidenceCannotBeNewerThanTheGrantThatReliesOnIt()
    {
        Assert.Throws<ArgumentException>(() =>
            new InstrumentEligibilityGrant(Scope(EligibilityPurpose.Research), Now, Now.AddMinutes(1), null));
    }

    [Fact]
    public void AGrantRequiresAConcreteInterval()
    {
        Assert.Throws<ArgumentException>(() =>
            new InstrumentEligibilityGrant(
                Scope(EligibilityPurpose.Research, CandleInterval.None), Now, Now, null));
    }

    [Fact]
    public void PrerequisiteChainMatchesTheDocumentedLadder()
    {
        Assert.Null(InstrumentEligibility.Prerequisite(EligibilityPurpose.Research));
        Assert.Equal(EligibilityPurpose.Research, InstrumentEligibility.Prerequisite(EligibilityPurpose.Backtest));
        Assert.Equal(EligibilityPurpose.Backtest, InstrumentEligibility.Prerequisite(EligibilityPurpose.Paper));
        Assert.Equal(EligibilityPurpose.Paper, InstrumentEligibility.Prerequisite(EligibilityPurpose.SpotTest));
        Assert.Equal(EligibilityPurpose.SpotTest, InstrumentEligibility.Prerequisite(EligibilityPurpose.SpotLive));
        Assert.Equal(EligibilityPurpose.Paper, InstrumentEligibility.Prerequisite(EligibilityPurpose.FuturesTest));
        Assert.Equal(EligibilityPurpose.FuturesTest, InstrumentEligibility.Prerequisite(EligibilityPurpose.FuturesLive));
    }
}
