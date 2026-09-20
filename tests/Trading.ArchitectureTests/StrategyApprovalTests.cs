using System.Linq.Expressions;
using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class StrategyApprovalTests
{
    private static readonly DateTimeOffset s_createdAtUtc =
        new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private const string FirstFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string SecondFingerprint =
        "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void LegalTransitionsProduceNewImmutableApprovalSnapshots()
    {
        var creator = StrategyApprovalActor.Human(Guid.NewGuid());
        var approval = Draft(creator);

        var underReview = approval.TransitionTo(
            StrategyApprovalState.UnderReview,
            creator,
            s_createdAtUtc.AddMinutes(1));
        var approved = underReview.TransitionTo(
            StrategyApprovalState.Approved,
            creator,
            s_createdAtUtc.AddMinutes(2),
            creator);
        var retired = approved.TransitionTo(
            StrategyApprovalState.Retired,
            creator,
            s_createdAtUtc.AddMinutes(3));

        Assert.Equal(StrategyApprovalState.Draft, approval.State);
        Assert.Empty(approval.Transitions);
        Assert.Equal(StrategyApprovalState.UnderReview, underReview.State);
        Assert.Single(underReview.Transitions);
        Assert.Equal(StrategyApprovalState.Approved, approved.State);
        Assert.Same(creator, approved.ApprovedBy);
        Assert.Equal(StrategyApprovalState.Retired, retired.State);
        Assert.True(retired.IsTerminal);
    }

    [Fact]
    public void RejectsIllegalSkippedBackwardDuplicateAndTerminalTransitions()
    {
        var creator = StrategyApprovalActor.Human(Guid.NewGuid());
        var draft = Draft(creator);

        Assert.Throws<InvalidOperationException>(() => draft.TransitionTo(
            StrategyApprovalState.Approved,
            creator,
            s_createdAtUtc.AddMinutes(1),
            creator));
        Assert.Throws<InvalidOperationException>(() => draft.TransitionTo(
            StrategyApprovalState.Draft,
            creator,
            s_createdAtUtc.AddMinutes(1)));

        var underReview = draft.TransitionTo(
            StrategyApprovalState.UnderReview,
            creator,
            s_createdAtUtc.AddMinutes(2));
        Assert.Throws<ArgumentException>(() => underReview.TransitionTo(
            StrategyApprovalState.Rejected,
            creator,
            s_createdAtUtc.AddMinutes(1)));

        var rejected = underReview.TransitionTo(
            StrategyApprovalState.Rejected,
            creator,
            s_createdAtUtc.AddMinutes(3));
        Assert.True(rejected.IsTerminal);
        Assert.Throws<InvalidOperationException>(() => rejected.TransitionTo(
            StrategyApprovalState.Approved,
            creator,
            s_createdAtUtc.AddMinutes(4),
            creator));
        Assert.Throws<InvalidOperationException>(() => rejected.TransitionTo(
            StrategyApprovalState.Retired,
            creator,
            s_createdAtUtc.AddMinutes(4)));

        var retired = draft.TransitionTo(
            StrategyApprovalState.Retired,
            creator,
            s_createdAtUtc.AddMinutes(4));
        Assert.Throws<InvalidOperationException>(() => retired.TransitionTo(
            StrategyApprovalState.Rejected,
            creator,
            s_createdAtUtc.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => retired.TransitionTo(
            StrategyApprovalState.Approved,
            creator,
            s_createdAtUtc.AddMinutes(5),
            creator));
    }

    [Fact]
    public void ApprovalRequiresTheRecordedHumanApproverAndCannotBeWorkerPromoted()
    {
        var human = StrategyApprovalActor.Human(Guid.NewGuid());
        var otherHuman = StrategyApprovalActor.Human(Guid.NewGuid());
        var worker = StrategyApprovalActor.AutomatedProcess("approval-worker");
        var underReview = Draft(human).TransitionTo(
            StrategyApprovalState.UnderReview,
            worker,
            s_createdAtUtc.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => underReview.TransitionTo(
            StrategyApprovalState.Approved,
            worker,
            s_createdAtUtc.AddMinutes(2),
            human));
        Assert.Throws<InvalidOperationException>(() => underReview.TransitionTo(
            StrategyApprovalState.Approved,
            human,
            s_createdAtUtc.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => underReview.TransitionTo(
            StrategyApprovalState.Approved,
            human,
            s_createdAtUtc.AddMinutes(2),
            otherHuman));

        var approved = underReview.TransitionTo(
            StrategyApprovalState.Approved,
            human,
            s_createdAtUtc.AddMinutes(2),
            human);
        var transition = Assert.Single(approved.Transitions.Where(
            transition => transition.To == StrategyApprovalState.Approved));

        Assert.Same(human, transition.Actor);
        Assert.Same(human, transition.Approver);
    }

    [Fact]
    public void StrategyVersionAndSchemaAreImmutableAndChangedContentCreatesNewIdentity()
    {
        var schema = new StrategyParameterSchemaReference("ema-parameters", 1, FirstFingerprint);
        var version = new StrategyVersion(
            new StrategyTemplateVersionIdentity("platform.ema-trend", 1),
            schema,
            FirstFingerprint,
            s_createdAtUtc);
        var changedSchema = new StrategyParameterSchemaReference("ema-parameters", 2, SecondFingerprint);
        var changedVersion = version.CreateNext(
            changedSchema,
            SecondFingerprint,
            s_createdAtUtc.AddDays(1));

        Assert.Equal("platform.ema-trend", version.Identity.TemplateId);
        Assert.Equal(1, version.Identity.Version);
        Assert.Equal(1, version.ParameterSchema.Version);
        Assert.Equal(2, changedVersion.Identity.Version);
        Assert.Equal(2, changedVersion.ParameterSchema.Version);
        Assert.NotEqual(version.Identity, changedVersion.Identity);
        Assert.NotEqual(version.ContentFingerprint, changedVersion.ContentFingerprint);
        Assert.Throws<ArgumentException>(() => new StrategyVersion(
            new StrategyTemplateVersionIdentity("platform.ema-trend", 1),
            schema,
            "not-a-sha256",
            s_createdAtUtc));
    }

    [Fact]
    public void ApprovalModelsHaveNoCodeUploadOrTradingExecutionSurface()
    {
        var approvalTypes = typeof(StrategyApproval).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(StrategyApproval).Namespace)
            .ToArray();

        Assert.DoesNotContain(approvalTypes.SelectMany(type => type.GetProperties()), property =>
            property.SetMethod is not null
            || property.Name.Contains("Code", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Source", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Delegate", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Expression", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            approvalTypes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .SelectMany(method => method.GetParameters()),
            parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)
                || parameter.ParameterType == typeof(Expression)
                || (parameter.ParameterType.IsGenericType
                    && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Expression<>)));
        Assert.False(typeof(TradeIntent).IsAssignableFrom(typeof(StrategyApproval)));
    }

    [Fact]
    public void ApprovalRequirementsAllowOnlyCompleteFreshResearchEvidence()
    {
        var requirements = Requirements();
        var evidence = Evidence();

        var allowed = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements,
            evidence,
            CandleInterval.OneHour,
            TradingProductType.Spot,
            StrategyApprovalMode.Backtest,
            s_createdAtUtc.AddMinutes(30));

        Assert.True(allowed.Allowed);
        Assert.Empty(allowed.Failures);
    }

    [Fact]
    public void ApprovalRequirementsFailClosedForAbsentStaleAndIncompleteEvidence()
    {
        var requirements = Requirements();
        var absent = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, null, CandleInterval.OneHour, TradingProductType.Spot,
            StrategyApprovalMode.Research, s_createdAtUtc);
        var stale = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, Evidence(observedAtUtc: s_createdAtUtc.AddHours(-2)),
            CandleInterval.OneHour, TradingProductType.Spot,
            StrategyApprovalMode.Research, s_createdAtUtc);
        var incomplete = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, Evidence(closedHistoryCandles: 99),
            CandleInterval.OneHour, TradingProductType.Spot,
            StrategyApprovalMode.Research, s_createdAtUtc);

        Assert.False(absent.Allowed);
        Assert.False(stale.Allowed);
        Assert.False(incomplete.Allowed);
    }

    [Theory]
    [InlineData(99.99, 0.10, 0.01)]
    [InlineData(100.00, 0.11, 0.01)]
    [InlineData(100.00, 0.10, 0.011)]
    public void ApprovalRequirementsEnforceDecimalMarketBounds(
        decimal liquidity,
        decimal spread,
        decimal slippage)
    {
        var evaluation = StrategyApprovalRequirementEvaluator.Evaluate(
            Requirements(),
            Evidence(liquidity, spread, slippage),
            CandleInterval.OneHour,
            TradingProductType.Spot,
            StrategyApprovalMode.Research,
            s_createdAtUtc);

        Assert.False(evaluation.Allowed);
    }

    [Fact]
    public void ApprovalRequirementsRejectMismatchedScopeAndBlockLiveFutures()
    {
        var requirements = Requirements();
        var wrongInterval = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, Evidence(), CandleInterval.FourHours, TradingProductType.Spot,
            StrategyApprovalMode.Research, s_createdAtUtc);
        var wrongMode = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, Evidence(), CandleInterval.OneHour, TradingProductType.Spot,
            StrategyApprovalMode.Paper, s_createdAtUtc);
        var wrongProduct = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements, Evidence(), CandleInterval.OneHour, TradingProductType.Futures,
            StrategyApprovalMode.Research, s_createdAtUtc);

        Assert.False(wrongInterval.Allowed);
        Assert.False(wrongMode.Allowed);
        Assert.False(wrongProduct.Allowed);
        Assert.Throws<ArgumentException>(() => Requirements(
            products: new[] { TradingProductType.Futures }));
        Assert.Throws<ArgumentException>(() => Requirements(
            modes: new[] { StrategyApprovalMode.SpotLive }));
    }

    [Fact]
    public void ApprovalCannotBeApprovedWithoutRequirementsAndIntersectionsOnlyTighten()
    {
        var human = StrategyApprovalActor.Human(Guid.NewGuid());
        var underReview = StrategyApproval.CreateDraft(
                Guid.NewGuid(),
                Version(),
                human,
                s_createdAtUtc)
            .TransitionTo(StrategyApprovalState.UnderReview, human, s_createdAtUtc.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => underReview.TransitionTo(
            StrategyApprovalState.Approved, human, s_createdAtUtc.AddMinutes(2), human));

        var tighter = new StrategyApprovalRequirements(
            new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, InstrumentId) },
            200,
            200m,
            0.05m,
            0.005m,
            TimeSpan.FromMinutes(10),
            new[] { CandleInterval.OneHour },
            new[] { TradingProductType.Spot },
            new[] { StrategyApprovalMode.Backtest });
        var intersection = Requirements().Intersect(tighter);

        Assert.Equal(200, intersection.MinimumClosedHistoryCandles);
        Assert.Equal(200m, intersection.MinimumLiquidity);
        Assert.Equal(0.05m, intersection.MaximumSpread);
        Assert.Equal(0.005m, intersection.MaximumEstimatedSlippage);
        Assert.Equal(TimeSpan.FromMinutes(10), intersection.MaximumEvidenceAge);
        Assert.Equal(new[] { StrategyApprovalMode.Backtest }, intersection.AllowedModes);
        Assert.Throws<ArgumentException>(() => new StrategyApprovalRequirements(
            Array.Empty<ApprovedInstrumentScope>(), 1, 1m, 1m, 1m, TimeSpan.FromMinutes(1),
            new[] { CandleInterval.OneHour }, new[] { TradingProductType.Spot },
            new[] { StrategyApprovalMode.Research }));
    }

    [Fact]
    public void ApprovalAuditRecordsAreDeterministicAndUtcOnly()
    {
        var human = StrategyApprovalActor.Human(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var approval = Draft(human).TransitionTo(
            StrategyApprovalState.UnderReview,
            human,
            s_createdAtUtc.AddMinutes(1));
        var transition = Assert.Single(approval.Transitions);

        Assert.Equal(1, transition.Sequence);
        Assert.Equal(StrategyApprovalState.Draft, transition.From);
        Assert.Equal(StrategyApprovalState.UnderReview, transition.To);
        Assert.Equal(s_createdAtUtc.AddMinutes(1), transition.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, transition.OccurredAtUtc.Offset);
        Assert.Throws<ArgumentException>(() => Draft(human).TransitionTo(
            StrategyApprovalState.UnderReview,
            human,
            new DateTimeOffset(2026, 9, 20, 14, 1, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void StrategiesAssemblyApprovalModelsHaveNoExecutionOrExchangeReferences()
    {
        var references = typeof(StrategyApproval).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(references, name =>
            name.Contains("Kraken", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("System.Net.Http", StringComparison.Ordinal)
            || name.Contains("Trading.Exchanges", StringComparison.Ordinal)
            || name.Contains("Trading.Application", StringComparison.Ordinal)
            || name.Contains("Trading.Risk", StringComparison.Ordinal));
    }

    private static StrategyApproval Draft(StrategyApprovalActor creator) =>
        StrategyApproval.CreateDraft(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Version(),
            creator,
            s_createdAtUtc,
            Requirements());

    private static readonly Guid InstrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static StrategyVersion Version() => new(
        new StrategyTemplateVersionIdentity("platform.ema-trend", 1),
        new StrategyParameterSchemaReference("ema-parameters", 1, FirstFingerprint),
        FirstFingerprint,
        s_createdAtUtc);

    private static StrategyApprovalRequirements Requirements(
        IEnumerable<TradingProductType>? products = null,
        IEnumerable<StrategyApprovalMode>? modes = null) =>
        new(
            new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, InstrumentId) },
            100,
            100m,
            0.10m,
            0.01m,
            TimeSpan.FromMinutes(30),
            new[] { CandleInterval.OneHour },
            products ?? new[] { TradingProductType.Spot },
            modes ?? new[] { StrategyApprovalMode.Research, StrategyApprovalMode.Backtest });

    private static StrategyApprovalEvidence Evidence(
        decimal? liquidity = 100m,
        decimal? spread = 0.10m,
        decimal? estimatedSlippage = 0.01m,
        int closedHistoryCandles = 100,
        DateTimeOffset? observedAtUtc = null) => new(
        InstrumentId,
        AssetClass.Cryptocurrency,
        closedHistoryCandles,
        liquidity,
        spread,
        estimatedSlippage,
        observedAtUtc ?? s_createdAtUtc);
}
