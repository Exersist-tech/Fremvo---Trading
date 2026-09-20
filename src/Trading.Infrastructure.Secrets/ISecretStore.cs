using System.Collections.Concurrent;

namespace Trading.Infrastructure.Secrets;

public interface ISecretStore
{
    ValueTask StoreSecretAsync(string secretName, string secretValue, string? version = null, CancellationToken cancellationToken = default);

    ValueTask<string> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    ValueTask<string?> GetSecretVersionAsync(string secretName, CancellationToken cancellationToken = default);

    ValueTask RemoveSecretAsync(string secretName, CancellationToken cancellationToken = default);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, SecretEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask StoreSecretAsync(string secretName, string secretValue, string? version = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedName = NormalizeSecretName(secretName);

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new ArgumentException("Secret value is required.", nameof(secretValue));
        }

        _entries[normalizedName] = new SecretEntry(secretValue.Trim(), version?.Trim() ?? "v1");
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedName = NormalizeSecretName(secretName);

        if (!_entries.TryGetValue(normalizedName, out var entry))
        {
            throw new KeyNotFoundException($"Secret '{normalizedName}' was not found.");
        }

        return ValueTask.FromResult(entry.Value);
    }

    public ValueTask<string?> GetSecretVersionAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedName = NormalizeSecretName(secretName);

        if (!_entries.TryGetValue(normalizedName, out var entry))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(entry.Version);
    }

    public ValueTask RemoveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedName = NormalizeSecretName(secretName);
        _entries.TryRemove(normalizedName, out _);
        return ValueTask.CompletedTask;
    }

    private static string NormalizeSecretName(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("Secret name is required.", nameof(secretName));
        }

        return secretName.Trim();
    }

    private sealed record SecretEntry(string Value, string? Version);
}

public sealed record SecretReference
{
    public SecretReference(string secretName, string? version = null)
        : this("default-vault", secretName, version)
    {
    }

    public SecretReference(string vaultName, string secretName, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(vaultName))
        {
            throw new ArgumentException("Vault name is required.", nameof(vaultName));
        }

        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("Secret name is required.", nameof(secretName));
        }

        VaultName = vaultName.Trim();
        SecretName = secretName.Trim();
        Version = version;
    }

    public string VaultName { get; }

    public string SecretName { get; }

    public string? Version { get; }
}
