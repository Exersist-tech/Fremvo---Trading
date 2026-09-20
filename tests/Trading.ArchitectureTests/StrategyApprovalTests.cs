using System.Linq.Expressions;
using System.Reflection;
using Trading.Domain.Execution;
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
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("platform.ema-trend", 1),
                new StrategyParameterSchemaReference("ema-parameters", 1, FirstFingerprint),
                FirstFingerprint,
                s_createdAtUtc),
            creator,
            s_createdAtUtc);
}
