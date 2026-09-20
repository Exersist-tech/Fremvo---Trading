namespace Trading.Exchanges.Kraken.Execution;

/// <summary>
/// How a Kraken error should be acted on.
/// </summary>
internal enum KrakenErrorClass
{
    /// <summary>
    /// Kraken refused the request because of the request's own content. The
    /// order could not have come into existence, so this is a definite "no".
    /// </summary>
    BadRequest = 0,

    /// <summary>
    /// Kraken says an order with this client order id already exists. This is
    /// positive evidence that an earlier submission succeeded.
    /// </summary>
    Duplicate = 1,

    /// <summary>
    /// Kraken says the order does not exist.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// Kraken says the order is no longer open.
    /// </summary>
    AlreadyClosed = 3,

    /// <summary>
    /// Nothing was established about the request. This covers outages, rate
    /// limits, nonce problems, and anything not recognised.
    /// </summary>
    Inconclusive = 4,

    /// <summary>
    /// The credential itself was refused. Nothing happened, but the problem is
    /// the key rather than the order.
    /// </summary>
    Credential = 5
}

/// <summary>
/// Classifies Kraken's error strings.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately asymmetric: an error counts as a definite refusal
/// only when it is about the order's own content, because only then is it
/// certain that no order was created. Everything else — including errors that
/// almost certainly mean Kraken did nothing, such as a rate limit or a nonce
/// problem — is reported as inconclusive.
/// </para>
/// <para>
/// That asymmetry is intentional. The cost of an unnecessary reconciliation
/// query is one extra API call. The cost of wrongly concluding that an order
/// was not placed is a duplicate live position, which is the most expensive
/// mistake an execution path can make. When two errors have such different
/// prices, the classifier leans towards the cheap one.
/// </para>
/// <para>
/// Unrecognised strings therefore fall through to
/// <see cref="KrakenErrorClass.Inconclusive"/> rather than to a refusal, so a
/// Kraken error this code has never seen cannot be read as proof of absence.
/// </para>
/// </remarks>
internal static class KrakenSpotErrorClassifier
{
    /// <summary>
    /// Errors about the content of the request. Kraken validates these before
    /// the order reaches the book, so the order does not exist.
    /// </summary>
    private static readonly string[] BadRequestMarkers =
    [
        "Invalid arguments",
        "Invalid price",
        "Invalid volume",
        "Insufficient funds",
        "Unknown asset pair",
        "Unknown order type",
        "Invalid order",
        "Order minimum not met",
        "Cost minimum not met",
        "Invalid leverage",
        "Trading agreement required",
        "Unavailable for this pair",

        // Kraken's precision and filter refusals. Each is raised while
        // validating the request, before the order can reach the book.
        "Tick size check failed",
        "Limit price check failed",
        "Price check failed",
        "Unknown asset",
        "Orders limit exceeded",
        "Positions limit exceeded",
        "Reduce only",
        "Post only order"
    ];

    /// <summary>
    /// Errors that mean Kraken was unwilling or unable to act right now.
    /// </summary>
    /// <remarks>
    /// These are listed even though unrecognised strings already fall through
    /// to <see cref="KrakenErrorClass.Inconclusive"/>. Naming them makes the
    /// intent testable: a future edit that moves a rate limit or a nonce error
    /// into the refusal list has to delete an explicit statement that it is
    /// not one, rather than merely add a marker.
    /// </remarks>
    private static readonly string[] InconclusiveMarkers =
    [
        "Rate limit exceeded",
        "Too many requests",
        "Temporary lockout",
        "Invalid nonce",
        "Service:Unavailable",
        "Service:Busy",
        "Internal error",
        "Cancel pending"
    ];

    private static readonly string[] CredentialMarkers =
    [
        "Invalid key",
        "Invalid signature",
        "Permission denied"
    ];

    /// <summary>
    /// Classifies a set of Kraken error strings. The most serious class present
    /// wins, so one recognised credential or duplicate answer is not diluted by
    /// an accompanying unrecognised string.
    /// </summary>
    public static KrakenErrorClass Classify(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException("Classifying requires at least one error.", nameof(errors));
        }

        if (errors.Any(e => CredentialMarkers.Any(m => e.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            return KrakenErrorClass.Credential;
        }

        if (errors.Any(IsDuplicate))
        {
            return KrakenErrorClass.Duplicate;
        }

        if (errors.Any(e => e.Contains("Unknown order", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Unknown position", StringComparison.OrdinalIgnoreCase)))
        {
            return KrakenErrorClass.NotFound;
        }

        if (errors.Any(e => e.Contains("Order already closed", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Order not open", StringComparison.OrdinalIgnoreCase)))
        {
            return KrakenErrorClass.AlreadyClosed;
        }

        if (errors.Any(e => InconclusiveMarkers.Any(m => e.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            // Checked before the refusal list so that a message carrying both
            // a rate limit and something that reads like a content complaint
            // is not mistaken for proof the order is absent.
            return KrakenErrorClass.Inconclusive;
        }

        if (errors.Any(e => BadRequestMarkers.Any(m => e.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            return KrakenErrorClass.BadRequest;
        }

        // Rate limits, nonce errors, service outages and anything unrecognised
        // all land here. None of them establishes what happened to the order.
        return KrakenErrorClass.Inconclusive;
    }

    /// <summary>
    /// True when Kraken is reporting that the client order id is already in
    /// use. Kraken's wording has varied, so more than one form is recognised.
    /// </summary>
    private static bool IsDuplicate(string error) =>
        error.Contains("cl_ord_id", StringComparison.OrdinalIgnoreCase)
        && (error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || error.Contains("already", StringComparison.OrdinalIgnoreCase)
            || error.Contains("in use", StringComparison.OrdinalIgnoreCase));
}
