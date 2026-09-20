using Microsoft.EntityFrameworkCore;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;

namespace Trading.Web.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTradingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("TradingDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'TradingDb' is not configured.");
        }

        services.AddDbContext<TradingDbContext>(options =>
            options.UseSqlServer(connectionString));
        services.AddScoped<ICandleRepository, EfCandleRepository>();

        return services;
    }
}
