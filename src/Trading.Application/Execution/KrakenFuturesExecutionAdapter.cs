using Trading.Domain.Execution;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using DomainPositionDirection = Trading.Domain.Execution.FuturesPositionDirection;
using GatewayPositionDirection = Trading.Exchanges.Abstractions.Execution.FuturesPositionDirection;

namespace Trading.Application.Execution;

/// <summary>
/// Transforms a separate futures command into one Kraken Futures demo order.
/// </summary>
/// <remarks>
/// This adapter has no persistence or orchestration role and is intentionally
/// absent from live-service registration. It performs one placement attempt;
/// unknown outcomes require reconciliation and are never retried here.
/// </remarks>
public sealed class KrakenFuturesExecutionAdapter : IFuturesExecutionAdapter
{
    private readonly IFuturesOrderGateway _gateway;
    private readonly IFuturesDemoExecutionAccountSource _accounts;
    private readonly TimeProvider _timeProvider;

    public KrakenFuturesExecutionAdapter(
        IFuturesOrderGateway gateway,
        IFuturesDemoExecutionAccountSource accounts,
        TimeProvider timeProvider)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ExecutionResult> ExecuteAsync(
        FuturesExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = _timeProvider.GetUtcNow();

        var account = await _accounts
            .ResolveAsync(command.FuturesDemoAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return Refuse(command, now, "The explicitly named futures demo account could not be resolved.");
        }

        if (!account.IsDemoOnly)
        {
            return Refuse(command, now, "The futures account is not explicitly demo-only.");
        }

        if (account.Exchange != ExchangeKind.Kraken || _gateway.Exchange != ExchangeKind.Kraken)
        {
            return Refuse(command, now, "The futures demo account and execution route must both be Kraken.");
        }

        var request = new FuturesOrderRequest(
            command.Symbol,
            command.Direction == TradeDirection.Buy ? FuturesOrderSide.Buy : FuturesOrderSide.Sell,
            command.PositionDirection == DomainPositionDirection.LongPosition
                ? GatewayPositionDirection.LongPosition
                : GatewayPositionDirection.ShortPosition,
            FuturesOrderType.Limit,
            command.Quantity,
            command.LimitPrice,
            command.ReduceOnly,
            command.ClientOrderId,
            validateOnly: false);

        FuturesOrderPlacement placement;
        try
        {
            placement = await _gateway
                .PlaceAsync(account.Credential, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            return Unknown(command, now, $"The futures demo route threw {exception.GetType().Name}.");
        }

        return placement.Outcome switch
        {
            FuturesPlacementOutcome.Accepted => new ExecutionResult(
                command.Id, true, "Accepted", 0m, 0m, 0m, now, outcome: ExecutionOutcome.Accepted),
            FuturesPlacementOutcome.Rejected => new ExecutionResult(
                command.Id, false, "Rejected", 0m, 0m, 0m, now,
                "The futures demo venue rejected the request.", ExecutionOutcome.Rejected),
            _ => Unknown(command, now, "The futures demo venue did not establish the order state.")
        };
    }

    private static ExecutionResult Refuse(FuturesExecutionCommand command, DateTimeOffset now, string reason) =>
        new(command.Id, false, "Refused", 0m, 0m, 0m, now, reason, ExecutionOutcome.Rejected);

    private static ExecutionResult Unknown(FuturesExecutionCommand command, DateTimeOffset now, string reason) =>
        new(command.Id, false, "Unknown", 0m, 0m, 0m, now, reason, ExecutionOutcome.Unknown);
}
