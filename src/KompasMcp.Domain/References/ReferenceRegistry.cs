using System.Collections.Concurrent;

namespace KompasMcp.Domain.References;

/// <summary>A minted reference and the state it was valid for.</summary>
public sealed record StoredReference(
    string Id,
    string Kind,
    string DocumentId,
    long Revision,
    string? PersistentFeatureId,
    object? Payload)
{
    /// <summary>The COM payload is opaque to this layer; only the Worker knows what it points at.</summary>
    public T PayloadAs<T>() =>
        Payload is T typed ? typed : throw new InvalidCastException($"Ссылка {Kind} не содержит {typeof(T).Name}.");
}

/// <summary>Server-side handles for topology elements (spec 1.7): every reference carries the document
/// revision it was minted against, and resolving a reference from an older revision is an error
/// rather than a silent re-lookup.</summary>
/// <remarks>INVARIANT: not a <c>Dictionary&lt;string, object&gt;</c> with an implicit "find something
/// similar" fallback — re-resolving by geometry alone would let a stale face reference pick a different
/// face after a rebuild. Geometric re-search exists only as an explicit operation returning candidates.</remarks>
public sealed class ReferenceRegistry
{
    private readonly ConcurrentDictionary<string, StoredReference> _byId = new(StringComparer.Ordinal);

    /// <summary>Insertion order, so re-stamping walks references in mint order (a ConcurrentDictionary
    /// enumerates arbitrarily).</summary>
    private readonly ConcurrentDictionary<string, StoredReference> _byOrder = new(StringComparer.Ordinal);

    public int Count => _byId.Count;

    /// <summary>Mint an opaque reference id ("<c>kind:uuid</c>") bound to a revision.</summary>
    public StoredReference Register(string kind, string documentId, long revision, object? payload, string? persistentFeatureId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Ревизия не может быть отрицательной.");
        }

        var stored = new StoredReference($"{kind}:{Guid.NewGuid():N}", kind, documentId, revision, persistentFeatureId, payload);
        if (!_byId.TryAdd(stored.Id, stored))
        {
            throw new InvalidOperationException("Ссылка с таким идентификатором уже существует.");
        }

        _byOrder[stored.Id] = stored;
        return stored;
    }

    public bool TryGet(string id, out StoredReference? reference) => _byId.TryGetValue(id, out reference);

    /// <summary>Register a reference whose identifier is DETERMINED BY THE SUBJECT, not by a fresh uuid.</summary>
    /// <remarks>Most references are opaque: the caller gets a uuid and hands it back. A fillet's own input
    /// is the exception — the only currency that shrinks a fillet's edge set is the <c>IModelObject</c> set
    /// the feature hands out through <c>IFillet.BaseObjects</c>, addressed by <c>IModelObject.Reference</c>,
    /// which <c>kompas_get_feature</c> reports as <c>base_object_references</c>. The identifier is fixed
    /// by the model, not chosen here; a uuid would carry no address. Re-registration is idempotent and
    /// REFRESHES the revision — the same input read twice is the same reference, and the later read is
    /// the one whose revision must match. Source: docs/acceptance/api7/fillet-base-objects.md</remarks>
    public StoredReference RegisterDeterministic(
        string id, string kind, string documentId, long revision, object? payload,
        string? persistentFeatureId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Ревизия не может быть отрицательной.");
        }

        var stored = new StoredReference(id, kind, documentId, revision, persistentFeatureId, payload);
        _byId[id] = stored;
        _byOrder[id] = stored;
        return stored;
    }

    /// <summary>Resolve a reference that must still be valid for <paramref name="currentRevision"/>.</summary>
    /// <exception cref="Contracts.KompasContractException">STALE_REFERENCE when the document moved on,
    /// DOCUMENT_NOT_FOUND when the reference was never issued or already dropped. Neither is retried
    /// automatically.</exception>
    public StoredReference Require(string id, string documentId, long currentRevision)
    {
        if (!_byId.TryGetValue(id, out var stored) || stored is null)
        {
            throw new Contracts.KompasContractException(
                Contracts.ErrorCodes.StaleReference,
                $"Ссылка '{id}' не найдена в реестре: она либо выпущена для другой сессии, либо уже отозвана.",
                Contracts.RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["reference_id"] = id, ["current_revision"] = currentRevision });
        }

        if (!string.Equals(stored.DocumentId, documentId, StringComparison.Ordinal))
        {
            throw new Contracts.KompasContractException(
                Contracts.ErrorCodes.StaleReference,
                $"Ссылка '{id}' выпущена для другого документа.",
                Contracts.RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["reference_document_id"] = stored.DocumentId,
                    ["requested_document_id"] = documentId,
                });
        }

        if (stored.Revision != currentRevision)
        {
            throw new Contracts.KompasContractException(
                Contracts.ErrorCodes.StaleReference,
                $"Ссылка относится к ревизии {stored.Revision}, текущая {currentRevision}.",
                Contracts.RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["reference_revision"] = stored.Revision,
                    ["current_revision"] = currentRevision,
                });
        }

        return stored;
    }

    /// <summary>Drop every reference of a document: called on close and after a reload.</summary>
    public int InvalidateDocument(string documentId)
    {
        var dropped = 0;
        foreach (var key in _byId.Keys.Where(k => string.Equals(_byId[k]?.DocumentId, documentId, StringComparison.Ordinal)).ToArray())
        {
            if (_byId.TryRemove(key, out _))
            {
                dropped++;
            }

            _byOrder.TryRemove(key, out _);
        }

        return dropped;
    }

    public static bool IsTopologyHandle(string kind) =>
        kind is "face" or "edge" or "vertex" or "loop" or InputKind;

    /// <summary>Kind of a reference that addresses a FEATURE'S OWN INPUT (an <c>IModelObject</c> from
    /// <c>IFillet.BaseObjects</c>) rather than an element of the body's topology.</summary>
    /// <remarks>INVARIANT: this kind counts as a topology handle — an own input is as perishable as a
    /// body edge, so an ordinary revision bump must DROP these references rather than re-stamp them.
    /// Re-stamping is reserved for handles this server just minted for objects it created or edited.</remarks>
    public const string InputKind = "input";

    /// <summary>Move a document's references to a new revision.</summary>
    /// <param name="documentId">Document whose references are affected.</param>
    /// <param name="newRevision">Revision the surviving references are re-stamped to.</param>
    /// <param name="invalidateAll">True after a rebuild, reload, restore or a detected external change —
    /// nothing from the previous revision survives. False for an ordinary mutation: the model objects it
    /// just created or edited are exactly the handles the next command needs, so they are re-stamped
    /// instead of dropped. Dropping them made create_sketch → edit_sketch → extrude impossible.</param>
    /// <returns>How many references were dropped.</returns>
    public int RevisionForward(string documentId, long newRevision, bool invalidateAll)
    {
        if (invalidateAll)
        {
            return InvalidateDocument(documentId);
        }

        var dropped = 0;
        foreach (var stored in _byOrder.Values.Where(v => string.Equals(v.DocumentId, documentId, StringComparison.Ordinal)).ToArray())
        {
            if (IsTopologyHandle(stored.Kind))
            {
                if (_byId.TryRemove(stored.Id, out _))
                {
                    dropped++;
                }

                continue;
            }

            _byId[stored.Id] = stored with { Revision = newRevision };
        }

        return dropped;
    }

    public IReadOnlyList<string> ForDocument(string documentId) =>
        _byId.Values.Where(v => string.Equals(v.DocumentId, documentId, StringComparison.Ordinal)).Select(v => v.Id).ToArray();
}
