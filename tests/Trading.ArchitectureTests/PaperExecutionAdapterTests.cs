using Trading.Domain.Execution;

namespace Trading.ArchitectureTests;

public sealed class PaperExecutionAdapterTests
{
    [Fact]
    public async Task PaperExecutionAdapterFillsPaperCommandsOnly()
    {
        var adapter = new PaperExecutionAdapter();
        var command = new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTCUSDT",
            TradeDirection.Buy,
            1.5m,
            120000m,
            DateTimeOffset.UtcNow,
            "client-order-paper-1");

        var result = await adapter.ExecuteAsync(command);

        Assert.True(result.Success);
        Assert.Equal("Filled", result.Status);
        Assert.Equal(command.Quantity, result.FilledQuantity);
        Assert.Equal(command.Price, result.AverageFillPrice);
        Assert.Equal(1_440m, result.Fees);
        Assert.Equal(PaperExecutionAdapter.DefaultEstimatedTakerFeeRate, adapter.EstimatedTakerFeeRate);
        Assert.Single(adapter.Ledger);
    }

    [Fact]
    public async Task ExplicitEstimatedFeeRateIsAppliedAndInvalidRatesAreRejected()
    {
        var adapter = new PaperExecutionAdapter(estimatedTakerFeeRate: 0.002m);
        var command = new ExecutionCommand(Guid.NewGuid(), Guid.NewGuid(), "BTC/USD",
            TradeDirection.Sell, 2m, 100m, DateTimeOffset.UtcNow, "paper-fee-estimate");

        var result = await adapter.ExecuteAsync(command);

        Assert.Equal(0.4m, result.Fees);
        Assert.Equal(result.Fees, Assert.Single(adapter.Ledger).Fees);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaperExecutionAdapter(estimatedTakerFeeRate: -0.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaperExecutionAdapter(estimatedTakerFeeRate: 1.001m));
    }

    [Fact]
    public async Task PaperExecutionAdapterRejectsLiveExecutionCommands()
    {
        var adapter = new PaperExecutionAdapter();
        var command = new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ETHUSDT",
            TradeDirection.Sell,
            2m,
            3500m,
            DateTimeOffset.UtcNow,
            "client-order-live-1",
            isPaperOnly: false,
            exchangeAccountId: Guid.NewGuid());

        var result = await adapter.ExecuteAsync(command);

        Assert.False(result.Success);
        Assert.Equal("Rejected", result.Status);
        Assert.Contains("paper-only", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(adapter.Ledger);
    }

    [Fact]
    public async Task ConcurrentPaperWorkersReceiveConsistentLedgerSnapshots()
    {
        var adapter = new PaperExecutionAdapter();
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var writers = ids.Select((id, index) => Task.Run(() => adapter.ExecuteAsync(new ExecutionCommand(
            id, Guid.NewGuid(), "BTC/USD", TradeDirection.Buy, 1m, 100m,
            DateTimeOffset.UtcNow, $"paper-concurrent-{index}")))).ToArray();
        var readers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var snapshot = adapter.Ledger;
                Assert.Equal(snapshot.Count, snapshot.Select(entry => entry.ExecutionCommandId).Distinct().Count());
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await Task.WhenAll(readers);
        Assert.Equal(ids.Order(), adapter.Ledger.Select(entry => entry.ExecutionCommandId).Order());
        Assert.All(ids, id => Assert.NotNull(adapter.FindFill(id)));
    }

    [Fact]
    public async Task ConcurrentDuplicateCommandCannotCreateTwoSimulatedFills()
    {
        var adapter = new PaperExecutionAdapter();
        var command = new ExecutionCommand(Guid.NewGuid(), Guid.NewGuid(), "BTC/USD",
            TradeDirection.Buy, 1m, 100m, DateTimeOffset.UtcNow, "paper-duplicate-command");

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => adapter.ExecuteAsync(command))));

        Assert.Single(results, result => result.Success);
        Assert.Equal(19, results.Count(result => !result.Success));
        var fill = Assert.Single(adapter.Ledger);
        Assert.Same(fill, adapter.FindFill(command.Id));
    }
}
