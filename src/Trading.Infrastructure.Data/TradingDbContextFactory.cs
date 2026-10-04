using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trading.Infrastructure.Data;

public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TradingDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Set ConnectionStrings__TradingDb before generating or applying SQL migrations.");

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TradingDbContext(options);
    }
}
