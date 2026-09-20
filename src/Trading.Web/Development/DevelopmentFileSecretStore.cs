using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Trading.Infrastructure.Secrets;

namespace Trading.Web.Development;

/// <summary>
/// A secret store for local development that survives an application restart.
/// </summary>
/// <remarks>
/// <para>
/// In Azure the secret store is Key Vault, reached with a managed identity. On
/// a developer machine there is no Key Vault, and the in-memory store loses
/// every credential when the process stops, which would force a user to paste
/// their API key again after every restart.
/// </para>
/// <para>
/// This store encrypts each value with ASP.NET Core Data Protection and writes
/// it outside the repository, under the user's local application data. It is
/// registered only in the Development environment and throws if constructed
/// anywhere else. It is not a Key Vault substitute and must never be used in a
/// deployed environment.
/// </para>
/// <para>
/// The file holds ciphertext only. The protection keys live in a separate
/// directory, so the secrets file on its own does not reveal a credential.
/// </para>
/// </remarks>
internal sealed class DevelopmentFileSecretStore : ISecretStore, IDisposable
{
    private const string ProtectorPurpose = "Fremvo.Development.ExchangeCredentials.v1";

    private readonly IDataProtector _protector;
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, StoredSecret> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public DevelopmentFileSecretStore(
        IDataProtectionProvider protectionProvider,
        IHostEnvironment environment,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(protectionProvider);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DevelopmentFileSecretStore is only permitted in the Development environment. " +
                "Deployed environments must use the Key Vault backed secret store.");
        }

        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
        _filePath = filePath;

        Load();
    }

    /// <summary>
    /// The default location: the user's local application data, never the
    /// repository working tree, so a credential cannot be committed by accident.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FremvoTrading",
        "development-secrets.json");

    public async ValueTask StoreSecretAsync(
        string secretName,
        string secretValue,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var name = Normalize(secretName);

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new ArgumentException("Secret value is required.", nameof(secretValue));
        }

        _entries[name] = new StoredSecret(
            _protector.Protect(secretValue.Trim()),
            version?.Trim() ?? "v1");

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<string> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = Normalize(secretName);

        if (!_entries.TryGetValue(name, out var entry))
        {
            throw new KeyNotFoundException($"Secret '{name}' was not found.");
        }

        return ValueTask.FromResult(_protector.Unprotect(entry.ProtectedValue));
    }

    public ValueTask<string?> GetSecretVersionAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _entries.TryGetValue(Normalize(secretName), out var entry) ? entry.Version : null);
    }

    public async ValueTask RemoveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(Normalize(secretName), out _);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("Secret name is required.", nameof(secretName));
        }

        return secretName.Trim();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var json = File.ReadAllText(_filePath);

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        Dictionary<string, StoredSecret>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, StoredSecret>>(json);
        }
        catch (JsonException)
        {
            // A corrupt development file is discarded rather than failing
            // startup. No credential is recoverable from it in any case.
            return;
        }

        if (stored is null)
        {
            return;
        }

        foreach (var pair in stored)
        {
            _entries[pair.Key] = pair.Value;
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new Dictionary<string, StoredSecret>(_entries, StringComparer.OrdinalIgnoreCase));

            await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private sealed record StoredSecret(string ProtectedValue, string? Version);

    public void Dispose() => _fileLock.Dispose();
}
