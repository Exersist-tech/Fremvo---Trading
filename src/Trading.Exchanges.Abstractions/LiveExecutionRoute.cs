namespace Trading.Exchanges.Abstractions;

/// <summary>
/// A real execution route to an exchange: the component that can actually send
/// an order to a venue and read back what happened to it.
/// </summary>
/// <remarks>
/// <para>
/// Implementing this interface is the act that makes real trading possible for
/// an exchange. Nothing else in the platform can reach a venue, so the set of
/// registered implementations is the complete and literal answer to "which
/// exchanges can this deployment trade on for real".
/// </para>
/// <para>
/// There is deliberately no implementation today. A live route requires the
/// Kraken order gateway, its recorded-response replay harness, and the
/// reconciliation error taxonomy, none of which exist yet. Until one is
/// written and registered, promotion out of paper is refused by the platform
/// rather than merely hidden in the user interface.
/// </para>
/// </remarks>
public interface ILiveExecutionRoute
{
    /// <summary>The exchange this route can send orders to.</summary>
    ExchangeKind Exchange { get; }
}

/// <summary>
/// Answers whether a real execution route exists for an exchange.
/// </summary>
public interface ILiveExecutionRouteProvider
{
    bool HasRouteFor(ExchangeKind exchange);
}

/// <summary>
/// Reports the execution routes that are actually registered in this
/// deployment.
/// </summary>
/// <remarks>
/// The answer is derived from the registered <see cref="ILiveExecutionRoute"/>
/// implementations rather than from configuration. A setting could be turned on
/// for an exchange that has no route behind it, which would let an account be
/// promoted into a stage where its orders go nowhere; a registration cannot be
/// wrong in that way, because the thing being counted is the route itself.
/// </remarks>
public sealed class LiveExecutionRouteProvider : ILiveExecutionRouteProvider
{
    private readonly HashSet<ExchangeKind> _exchanges;

    public LiveExecutionRouteProvider(IEnumerable<ILiveExecutionRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _exchanges = routes.Select(route => route.Exchange).ToHashSet();
    }

    public bool HasRouteFor(ExchangeKind exchange) => _exchanges.Contains(exchange);
}
