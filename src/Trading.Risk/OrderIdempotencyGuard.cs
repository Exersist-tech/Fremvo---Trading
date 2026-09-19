namespace Trading.Risk;

public sealed class OrderIdempotencyResult
{
    public OrderIdempotencyResult(bool isDuplicate, string? reason = null)
    {
        IsDuplicate = isDuplicate;
        Reason = reason;
    }

    public bool IsDuplicate { get; }

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

            return new OrderIdempotencyResult(false, "Client order id conflicts with a different order payload.");
        }

        _knownOrderSignatures[clientOrderId.Trim()] = signature;
        return new OrderIdempotencyResult(false, "Client order id accepted for first submission.");
    }
}
