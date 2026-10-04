using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;

namespace Trading.Infrastructure.Data.Execution;

public sealed class EfLiveFillPersistenceTransaction(TradingDbContext context) : ILiveFillPersistenceTransaction
{
    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
