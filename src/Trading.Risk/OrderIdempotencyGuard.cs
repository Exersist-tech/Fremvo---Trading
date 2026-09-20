namespace Trading.Risk;

public sealed class OrderIdempotencyResult
{
    public OrderIdempotencyResult(bool isDuplicate, string? reason = null, bool isConflict = false)
    {
        IsDuplicate = isDuplicate;
        IsConflict = isConflict;
        Reason = reason;
    }

    /// <summary>The same client order id was already submitted with an identical payload.</summary>
    public bool IsDuplicate { get; }

    /// <summary>
    /// The same client order id was already submitted with a <em>different</em> payload. This is
    /// never safe to send: the exchange may already hold the earlier order under that id.
    /// </summary>
    public bool IsConflict { get; }

    /// <summary>True only when the order may be submitted.</summary>
    public bool IsAccepted => !IsDuplicate && !IsConflict;

    public string? Reason { get; }
}

public sealed class OrderIdempotencyGuard
{
    private readonly Dictionary<string, string> _knownOrderSignatures = new(StringComparer.OrdinalIgnoreCase);

    public OrderIdempotencyResult RegisterOrCheck(string clientOrderId, string symbol, decimal quantity, decimal price)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException("Client order id is required.", nameof(clientOrderId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        var signature = $"{symbol.Trim()}:{quantity}:{price}";

        if (_knownOrderSignatures.TryGetValue(clientOrderId.Trim(), out var existingSignature))
        {
            if (string.Equals(existingSignature, signature, StringComparison.OrdinalIgnoreCase))
            {
                return new OrderIdempotencyResult(true, "Duplicate client order id for an identical order payload.");
            }

            return new OrderIdempotencyResult(
                false,
                "Client order id conflicts with a different order payload.",
                isConflict: true);
        }

        _knownOrderSignatures[clientOrderId.Trim()] = signature;
        return new OrderIdempotencyResult(false, "Client order id accepted for first submission.");
    }
}
