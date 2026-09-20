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
        Assert.Single(adapter.Ledger);
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
}
