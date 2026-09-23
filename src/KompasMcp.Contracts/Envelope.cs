using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KompasMcp.Contracts;

/// <summary>
/// JSON options shared by the public tool contract and the Host/Worker IPC frame.
/// Wire names are snake_case (spec 2.2 example); enums are lower_snake strings; nothing is
/// dropped to null-omitting defaults because a null field is meaningful in the envelope.
/// </summary>
public static class KompJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            // Default (strict) number handling is deliberate: a client that writes NaN or
            // Infinity using named-literal tolerance would get a parse error here, and the
            // validator rejects finite-but-nonsense values (negative lengths) further down.
            WriteIndented = false,
        };
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Serialise to a <see cref="JsonNode"/> so the value can be embedded as MCP
    /// <c>structuredContent</c> without a string round-trip re-camel-casing anything.
    /// A null result is represented by a null node — System.Text.Json has no non-null
    /// "JSON null" JsonNode value.
    /// </summary>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
}

/// <summary>One named verification performed by the server (spec 2.2).</summary>
public sealed record NamedCheck(
    string Name,
    bool Passed,
    string? Observed = null,
    string? Expected = null);

/// <summary>
/// How far the result was actually verified. <c>unverified_aspects</c> must list everything
/// the level does not cover, so a caller cannot read "file_created" as "geometry is correct".
/// </summary>
public sealed record VerificationDto(
    VerificationLevel Level,
    IReadOnlyList<NamedCheck> Checks,
    IReadOnlyList<string> UnverifiedAspects);

/// <summary>Structured error (spec 2.2). Distinguished from a protocol error by the caller.</summary>
public sealed record ErrorDto(
    string Code,
    string Message,
    RetryPolicy RetryPolicy,
    int? Hresult,
    bool PartialEffects,
    JsonObject? Details);

/// <summary>A file the server produced. SHA256 is mandatory for anything user-visible (spec 1.12).</summary>
public sealed record ArtifactDto(
    string Id,
    string RelativePath,
    string AbsolutePath,
    string MediaType,
    long ByteLength,
    string Sha256,
    DateTimeOffset CreatedAtUtc,
    string? OperationId,
    long? SourceRevision,
    string Provenance);

/// <summary>
/// The uniform envelope every CAD tool returns (spec 2.2).
/// </summary>
public sealed record ResultEnvelope<TResult>
{
    public string ContractVersion { get; init; } = ContractVersions.Current;
    public string? OperationId { get; init; }
    public OperationStatus Status { get; init; }
    public string? ApplicationId { get; init; }
    public string? DocumentId { get; init; }
    public long? RevisionBefore { get; init; }
    public long? RevisionAfter { get; init; }
    public TResult? Result { get; init; }
    public VerificationDto? Verification { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ArtifactDto> Artifacts { get; init; } = Array.Empty<ArtifactDto>();
    public ErrorDto? Error { get; init; }

    public static ResultEnvelope<T> Ok<T>(
        T result,
        VerificationDto verification,
        string? operationId = null,
        string? applicationId = null,
        string? documentId = null,
        long? revisionBefore = null,
        long? revisionAfter = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<ArtifactDto>? artifacts = null) => new()
        {
            Status = OperationStatus.Succeeded,
            OperationId = operationId,
            ApplicationId = applicationId,
            DocumentId = documentId,
            RevisionBefore = revisionBefore,
            RevisionAfter = revisionAfter,
            Result = result,
            Verification = verification,
            Warnings = warnings ?? Array.Empty<string>(),
            Artifacts = artifacts ?? Array.Empty<ArtifactDto>(),
        };

    public static ResultEnvelope<T> Empty<T>(OperationStatus status, ErrorDto error, string? operationId = null) => new()
    {
        Status = status,
        OperationId = operationId,
        Error = error,
    };
}

/// <summary>
/// Thrown inside the server to abort with a contract error. The Host converts it into an
/// <see cref="ErrorDto"/>; it never escapes as an unhandled exception to the MCP client.
/// </summary>
public sealed class KompasContractException : Exception
{
    public string Code { get; }

    public RetryPolicy RetryPolicy { get; }

    public bool PartialEffects { get; }

    public int? Hresult { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }

    public KompasContractException(
        string code,
        string? message = null,
        RetryPolicy retryPolicy = RetryPolicy.Never,
        bool partialEffects = false,
        int? hresult = null,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message ?? ErrorMessages.For(code))
    {
        Code = code;
        RetryPolicy = retryPolicy;
        PartialEffects = partialEffects;
        Hresult = hresult;
        Details = details;
    }

    public ErrorDto ToErrorDto()
    {
        JsonObject? details = null;
        if (Details is not null)
        {
            details = new JsonObject();
            foreach (var (key, value) in Details)
            {
                // Serialize structurally: value.ToString() on a collection produced the type name
                // ("System.String[]") in an error detail, which is worse than no detail.
                details[ToJsonKey(key)] = value is null ? null : JsonSerializer.SerializeToNode(value, KompJson.Options);
            }
        }

        return new ErrorDto(Code, Message, RetryPolicy, Hresult, PartialEffects, details);
    }

    private static string ToJsonKey(string key)
    {
        var chars = new List<char>(key.Length + 8);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    chars.Add('_');
                }

                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(c);
            }
        }

        return new string(chars.ToArray());
    }
}
