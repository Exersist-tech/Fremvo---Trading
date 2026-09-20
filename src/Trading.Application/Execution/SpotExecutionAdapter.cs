using Trading.Domain.Execution;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

/// <summary>
/// Sends pipeline execution commands to a real spot venue.
/// </summary>
/// <remarks>
/// <para>
/// This adapter is written against <see cref="ISpotOrderGateway"/> and
/// contains no exchange-specific type. Kraken's request encoding, error
/// strings and response shapes stay inside the Kraken connector, so adding a
/// second exchange is a new gateway rather than a change here.
/// </para>
/// <para>
/// It is the last checkpoint before a real order. Four conditions are checked
/// and each one refuses rather than adapts:
/// </para>
/// <list type="number">
/// <item>A paper-only command is never sent to a venue.</item>
/// <item>A command with no resolvable account is refused.</item>
/// <item>An account still in <see cref="TradingStage.Paper"/> is refused.</item>
/// <item>An account on a different exchange than this gateway is refused.</item>
/// </list>
/// <para>
/// None of these is a fallback to simulation. Quietly turning a real order into
/// a simulated one would report a fill the user does not have.
/// </para>
/// </remarks>
public sealed class SpotExecutionAdapter : IExecutionAdapter
{
    private readonly ISpotOrderGateway _gateway;
    private readonly ISpotExecutionAccountSource _accounts;
    private readonly TimeProvider _timeProvider;

    public SpotExecutionAdapter(
        ISpotOrderGateway gateway,
        ISpotExecutionAccountSource accounts,
        TimeProvider timeProvider)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = _timeProvider.GetUtcNow();

        // A simulated command must never reach a venue. This adapter refuses
        // it outright rather than silently simulating, because a caller that
        // routed a paper command here has a bug that must surface.
        if (command.IsPaperOnly)
        {
            return Refuse(command, now, "A paper-only command must not be sent to an exchange.");
        }

        if (command.ExchangeAccountId is not { } accountId)
        {
            return Refuse(command, now, "The command names no exchange account.");
        }

        var account = await _accounts.ResolveAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Refuse(command, now, "The exchange account could not be resolved.");
        }

        if (account.Exchange != _gateway.Exchange)
        {
            return Refuse(
                command,
                now,
                "The exchange account belongs to a different exchange than this execution route.");
        }

        // Stage is re-checked here even though promotion already enforces it.
        // This is the last point before real money, and a control at the edge
        // must not rely on a control taken earlier still holding.
        if (account.Stage == TradingStage.Paper)
        {
            return Refuse(
                command,
                now,
                "The exchange account is in paper stage and cannot send orders to the exchange.");
        }

        var request = new SpotOrderRequest(
            command.Symbol,
            command.Direction == TradeDirection.Buy ? SpotOrderSide.Buy : SpotOrderSide.Sell,
            SpotOrderType.Limit,
            command.Quantity,
            command.Price,

            // The platform's own identifier travels with the order so the
            // venue enforces idempotency and the order stays findable if the
            // response is lost.
            command.ClientOrderId,

            // The one place in the platform that asks for a real order.
            validateOnly: false);

        SpotOrderPlacement placement;
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
            // An unanticipated connector failure proves nothing about the
            // order. It degrades to "unknown", never to "rejected".
            return Unknown(
                command,
                now,
                $"The execution route threw {exception.GetType().Name} and established nothing.");
        }

        return placement.Outcome switch
        {
            SpotPlacementOutcome.Accepted => new ExecutionResult(
                command.Id,
                success: true,
                status: "Accepted",
                filledQuantity: 0m,
                averageFillPrice: 0m,
                fees: 0m,
                executedAtUtc: now,
                failureReason: null,

                // Acceptance is not a fill. The quantity and price stay zero
                // until fills are read back, so an accepted order cannot be
                // reported as executed at its limit price.
                outcome: ExecutionOutcome.Filled),

            SpotPlacementOutcome.Rejected => new ExecutionResult(
                command.Id,
                success: false,
                status: "Rejected",
                filledQuantity: 0m,
                averageFillPrice: 0m,
                fees: 0m,
                executedAtUtc: now,
                failureReason: Describe(placement),
                outcome: ExecutionOutcome.Rejected),

            // The venue says this client order id is already in use, which is
            // evidence an earlier submission succeeded. The existing order must
            // be found, so this is unknown rather than a refusal.
            SpotPlacementOutcome.DuplicateClientOrderId => Unknown(
                command,
                now,
                "The exchange already holds an order with this client order id. "
                + Describe(placement)),

            // A validate-only answer from a route asked for a real order means
            // the request was altered somewhere. Nothing was placed, but the
            // route cannot be trusted to have done what it was told.
            SpotPlacementOutcome.Validated => Unknown(
                command,
                now,
                "The execution route returned a validation result for an order that was meant to be placed."),

            _ => Unknown(command, now, Describe(placement))
        };
    }

    private static string Describe(SpotOrderPlacement placement) =>
        placement.ExchangeErrors.Count == 0
            ? "The exchange gave no reason."
            : "The exchange answered: " + string.Join(", ", placement.ExchangeErrors);

    /// <summary>
    /// A refusal by this adapter. Nothing was sent, so the order definitely
    /// does not exist and no reconciliation is needed.
    /// </summary>
    private static ExecutionResult Refuse(ExecutionCommand command, DateTimeOffset now, string reason) =>
        new(
            command.Id,
            success: false,
            status: "Refused",
            filledQuantity: 0m,
            averageFillPrice: 0m,
            fees: 0m,
            executedAtUtc: now,
            failureReason: reason,
            outcome: ExecutionOutcome.Rejected);

    private static ExecutionResult Unknown(ExecutionCommand command, DateTimeOffset now, string reason) =>
        new(
            command.Id,
            success: false,
            status: "Unknown",
            filledQuantity: 0m,
            averageFillPrice: 0m,
            fees: 0m,
            executedAtUtc: now,
            failureReason: reason,
            outcome: ExecutionOutcome.Unknown);
}
