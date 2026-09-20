using Microsoft.EntityFrameworkCore;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.Infrastructure.Data.Scanner;
using Trading.MarketData;
using Trading.Application.Scanner;
using Trading.Backtesting;
using Trading.Infrastructure.Data.Backtesting;
using Trading.Infrastructure.Data.Experiments;
using Trading.Application.Experiments;

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
        services.AddScoped<IHistoricalDatasetRepository, EfHistoricalDatasetRepository>();
        services.AddScoped<IScanRequestRepository, EfScanRequestRepository>();
        services.AddScoped<IScanResultRepository, EfScanResultRepository>();
        services.AddScoped<ScannerResultsQueryService>();
        services.AddScoped<IExperimentResultLedger, EfExperimentResultLedger>();

        return services;
    }
}
