using Trading.Domain.Execution;

namespace Trading.ArchitectureTests;

public sealed class ReconciliationTests
{
    [Fact]
    public void ReconciliationRecordRequiresResolutionBeforeResubmissionWhenUnknown()
    {
        var record = new OrderReconciliationRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ex-123",
            ExchangeOrderStatus.Unknown,
            DateTimeOffset.UtcNow,
            "binance-testnet");

        Assert.True(record.RequiresManualReview);
        Assert.True(record.RequiresResolutionBeforeResubmission);

        record.Resolve(ExchangeOrderStatus.Filled, "Status confirmed by exchange polling.", DateTimeOffset.UtcNow);

        Assert.Equal(ExchangeOrderStatus.Filled, record.ObservedStatus);
        Assert.NotNull(record.ResolvedAtUtc);
    }

    [Fact]
    public void ReconciliationRecordRejectsUnknownResolvedStatus()
    {
        var record = new OrderReconciliationRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ex-222",
            ExchangeOrderStatus.PendingNew,
            DateTimeOffset.UtcNow,
            "binance-testnet");

        var exception = Assert.Throws<ArgumentException>(() =>
            record.Resolve(ExchangeOrderStatus.Unknown, "This should be invalid.", DateTimeOffset.UtcNow));

        Assert.Equal("resolvedStatus", exception.ParamName);
    }
}
