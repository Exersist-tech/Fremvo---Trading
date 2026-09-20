using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Exchanges.Kraken.Execution;

/// <summary>
/// Declares that this deployment can actually send Spot orders to Kraken.
/// </summary>
/// <remarks>
/// <para>
/// Registering this type is the single act that opens the promotion ladder out
/// of paper. It is deliberately a separate type from
/// <see cref="KrakenSpotOrderGateway"/>: constructing a gateway is something a
/// test or a tool might reasonably do, whereas registering a live route is a
/// statement about the deployment. Keeping them apart means the capability
/// cannot be acquired by accident.
/// </para>
/// <para>
/// It carries the gateway so that "a route exists" and "the thing the route
/// names can be reached" are the same fact rather than two that could drift.
/// </para>
/// </remarks>
public sealed class KrakenSpotLiveExecutionRoute : ILiveExecutionRoute
{
    public KrakenSpotLiveExecutionRoute(ISpotOrderGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        if (gateway.Exchange != ExchangeKind.Kraken)
        {
            throw new ArgumentException(
                "A Kraken live route must be backed by a Kraken gateway.", nameof(gateway));
        }

        Gateway = gateway;
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    public ISpotOrderGateway Gateway { get; }
}
