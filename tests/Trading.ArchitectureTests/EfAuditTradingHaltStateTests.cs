using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class EfAuditTradingHaltStateTests
{
    [Fact]
    public async Task ReloadedHaltStateRespectsAllScopesAndReleases()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var strategy = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await using (var db = Context(database))
        {
            void Write(string action, string target, int seconds) =>
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), owner, action,
                    "TradingHalt", target, now.AddSeconds(seconds), null, "operator decision", "halt-test"));
            Write("Trading.EmergencyStopEngaged", "platform", 0);
            Write("Trading.MarketHalted", "BTC/USD", 1);
            Write("Trading.UserHalted", owner.ToString("D"), 2);
            Write("Trading.StrategyHalted", strategy.ToString("D"), 3);
            Write("Trading.CloseOnlyEnabled", owner.ToString("D"), 4);
            Write("Trading.ReduceOnlyEnabled", owner.ToString("D"), 5);
            await db.SaveChangesAsync();
        }

        await using (var db = Context(database))
        {
            var reader = new EfAuditTradingHaltState(db);
            var flags = await reader.GetAsync(
                new PipelineContext(owner, TradingMode.Paper, "halt-test"), "BTC/USD", strategy, CancellationToken.None);
            Assert.True(flags.EmergencyStop);
            Assert.True(flags.MarketHalt);
            Assert.True(flags.AccountHalted);
            Assert.True(flags.StrategyHalted);
            Assert.True(flags.CloseOnlyMode);
            Assert.True(flags.ReduceOnlyMode);
            var unrelated = await reader.GetAsync(
                new PipelineContext(other, TradingMode.Paper, "halt-test"), "ETH/USD", Guid.NewGuid(), CancellationToken.None);
            Assert.True(unrelated.EmergencyStop);
            Assert.False(unrelated.MarketHalt || unrelated.AccountHalted || unrelated.StrategyHalted
                || unrelated.CloseOnlyMode || unrelated.ReduceOnlyMode);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), owner, "Trading.EmergencyStopReleased",
                "TradingHalt", "platform", now.AddMinutes(1), null, "operator decision", "halt-test"));
            await db.SaveChangesAsync();
        }

        await using (var db = Context(database))
        {
            var flags = await new EfAuditTradingHaltState(db).GetAsync(
                new PipelineContext(other, TradingMode.Paper, "halt-test"), "ETH/USD", Guid.NewGuid(), CancellationToken.None);
            Assert.False(flags.EmergencyStop);
        }
    }

    private static TradingDbContext Context(string database) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(database).Options);
}
