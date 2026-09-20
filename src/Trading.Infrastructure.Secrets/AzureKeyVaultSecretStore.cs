using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Trading.Infrastructure.Secrets;

/// <summary>
/// Stores exchange credentials in Azure Key Vault.
/// </summary>
/// <remarks>
/// This implementation receives its credential from the caller (normally
/// <c>DefaultAzureCredential</c> backed by an App Service managed identity).
/// It contains neither an Azure credential nor an exchange credential itself.
/// The database retains an application-level reference only, and this class
/// maps it to Key Vault's stricter secret-name format at its boundary.
/// </remarks>
public sealed class AzureKeyVaultSecretStore : ISecretStore
{
    private readonly SecretClient _client;

    public AzureKeyVaultSecretStore(SecretClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask StoreSecretAsync(
        string secretName,
        string secretValue,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);

        // Key Vault assigns the immutable version. Callers must not attempt to
        // emulate that server-controlled value in application storage.
        await _client
            .SetSecretAsync(ToKeyVaultName(secretName), secretValue.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> GetSecretAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetSecretAsync(ToKeyVaultName(secretName), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Value.Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            throw new KeyNotFoundException("The requested exchange credential was not found in Key Vault.", exception);
        }
    }

    public async ValueTask<string?> GetSecretVersionAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client
                .GetSecretAsync(ToKeyVaultName(secretName), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Value.Properties.Version;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async ValueTask RemoveSecretAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Key Vault deletion is intentionally asynchronous. There is no
            // reason to hold a disconnect request open through its retention
            // window, and credential references are never re-used.
            await _client
                .StartDeleteSecretAsync(ToKeyVaultName(secretName), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // Disconnect is idempotent: a missing secret leaves nothing to
            // remove, rather than leaving the account connected.
        }
    }

    private static string ToKeyVaultName(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var keyVaultName = applicationName.Trim().Replace('/', '-');

        if (keyVaultName.Length is < 1 or > 127
            || keyVaultName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "The exchange credential reference cannot be represented as an Azure Key Vault secret name.",
                nameof(applicationName));
        }

        return keyVaultName;
    }
}
