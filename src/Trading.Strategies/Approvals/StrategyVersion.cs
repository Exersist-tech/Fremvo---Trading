using System.Globalization;

namespace Trading.Strategies.Approvals;

public sealed class StrategyTemplateVersionIdentity : IEquatable<StrategyTemplateVersionIdentity>
{
    public StrategyTemplateVersionIdentity(string templateId, int version)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A strategy template id is required.", nameof(templateId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A template version must be positive.");
        }

        TemplateId = templateId.Trim();
        Version = version;
    }

    public string TemplateId { get; }

    public int Version { get; }

    public bool Equals(StrategyTemplateVersionIdentity? other) =>
        other is not null
        && Version == other.Version
        && string.Equals(TemplateId, other.TemplateId, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as StrategyTemplateVersionIdentity);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(TemplateId), Version);

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{TemplateId}@{Version}");
}

public sealed class StrategyParameterSchemaReference
{
    public StrategyParameterSchemaReference(string schemaId, int version, string contentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(schemaId))
        {
            throw new ArgumentException("A parameter schema id is required.", nameof(schemaId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A parameter schema version must be positive.");
        }

        SchemaId = schemaId.Trim();
        Version = version;
        ContentFingerprint = ValidateSha256(contentFingerprint, nameof(contentFingerprint));
    }

    public string SchemaId { get; }

    public int Version { get; }

    public string ContentFingerprint { get; }

    internal static string ValidateSha256(string fingerprint, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)
            || fingerprint.Length != 64
            || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A SHA-256 content fingerprint must contain exactly 64 hexadecimal characters.",
                parameterName);
        }

        return fingerprint.ToUpperInvariant();
    }
}

public sealed class StrategyVersion
{
    public StrategyVersion(
        StrategyTemplateVersionIdentity identity,
        StrategyParameterSchemaReference parameterSchema,
        string contentFingerprint,
        DateTimeOffset authoredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(parameterSchema);
        EnsureUtc(authoredAtUtc, nameof(authoredAtUtc));

        Identity = identity;
        ParameterSchema = parameterSchema;
        ContentFingerprint = StrategyParameterSchemaReference.ValidateSha256(
            contentFingerprint,
            nameof(contentFingerprint));
        AuthoredAtUtc = authoredAtUtc;
    }

    public StrategyTemplateVersionIdentity Identity { get; }

    public StrategyParameterSchemaReference ParameterSchema { get; }

    public string ContentFingerprint { get; }

    public DateTimeOffset AuthoredAtUtc { get; }

    public StrategyVersion CreateNext(
        StrategyParameterSchemaReference parameterSchema,
        string contentFingerprint,
        DateTimeOffset authoredAtUtc) =>
        new(
            new StrategyTemplateVersionIdentity(Identity.TemplateId, checked(Identity.Version + 1)),
            parameterSchema,
            contentFingerprint,
            authoredAtUtc);

    internal static void EnsureUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must use UTC.", parameterName);
        }
    }
}
