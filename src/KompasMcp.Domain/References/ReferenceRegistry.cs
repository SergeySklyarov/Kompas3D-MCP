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

/// <summary>
/// Server-side handles for topology elements (spec 1.7): every reference carries the document
/// revision it was minted against, and resolving a reference from an older revision is an error
/// rather than a silent re-lookup.
/// </summary>
/// <remarks>
/// Deliberately not a <c>Dictionary&lt;string, object&gt;</c> with an implicit "find something
/// similar" fallback: re-resolving by geometry on its own would let a stale face reference pick a
/// different face after a rebuild, which is precisely the failure mode the contract forbids.
/// Geometric re-search exists only as an explicit, separate operation that returns candidates.
/// </remarks>
public sealed class ReferenceRegistry
{
    private readonly ConcurrentDictionary<string, StoredReference> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// Insertion order, kept so that re-stamping walks references in the order they were minted.
    /// A ConcurrentDictionary enumerates in arbitrary order, and the log of a rebuild is easier to
    /// trust when it is deterministic.
    /// </summary>
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

    /// <summary>
    /// Register a reference whose identifier is DETERMINED BY THE SUBJECT, not by a fresh uuid.
    /// </summary>
    /// <remarks>
    /// Almost every reference in this server is opaque: the caller is told a uuid and may do nothing
    /// with it but hand it back. A fillet's own input is the exception, and the reason is measured
    /// (<c>docs/acceptance/api7/fillet-base-objects.md</c>): the only currency that shrinks a fillet's
    /// edge set is the set of <c>IModelObject</c> objects the feature itself hands out through
    /// <c>IFillet.BaseObjects</c>, and their address is the number <c>IModelObject.Reference</c>.
    /// Those numbers are what <c>kompas_get_feature</c> already reports as
    /// <c>base_object_references</c>, so the identifier is fixed by the model, not chosen here.
    /// Minting a uuid instead would produce a reference that carries no address at all.
    /// <para>
    /// Re-registration is idempotent and REFRESHES the revision: the same input read twice is the
    /// same reference, and the later read is the one whose revision must match. Treating it as a
    /// collision would make the second <c>kompas_get_feature</c> fail.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Resolve a reference that must still be valid for <paramref name="currentRevision"/>.
    /// </summary>
    /// <exception cref="Contracts.KompasContractException">
    /// STALE_REFERENCE when the document moved on, DOCUMENT_NOT_FOUND when the reference was never
    /// issued or already dropped. Neither is retried automatically.
    /// </exception>
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

    /// <summary>Kind of a reference that identifies a topology element rather than a model object.</summary>
    public static bool IsTopologyHandle(string kind) =>
        kind is "face" or "edge" or "vertex" or "loop" or InputKind;

    /// <summary>
    /// Kind of a reference that addresses a FEATURE'S OWN INPUT (an <c>IModelObject</c> from
    /// <c>IFillet.BaseObjects</c>) rather than an element of the body's topology.
    /// </summary>
    /// <remarks>
    /// This kind counts as a topology handle on purpose: an own input is as perishable as a body
    /// edge — after any mutation the feature may hold a different set — so an ordinary revision
    /// bump must DROP these references rather than re-stamp them. Re-stamping is reserved for the
    /// handles this server just minted for objects it created or edited, which is exactly what an
    /// input is not: its composition can change under the caller's feet.
    /// </remarks>
    public const string InputKind = "input";

    /// <summary>
    /// Move a document's references to a new revision.
    /// </summary>
    /// <param name="documentId">Document whose references are affected.</param>
    /// <param name="newRevision">Revision the surviving references are re-stamped to.</param>
    /// <param name="invalidateAll">
    /// True after a rebuild, reload, restore or a detected external change — then nothing from
    /// the previous revision survives. False for an ordinary mutation performed by this server:
    /// in that case the model objects it just created or edited (a sketch, a feature, a body)
    /// are exactly the handles the next command needs, so they are re-stamped instead of
    /// dropped. Dropping them made the natural sequence create_sketch → edit_sketch → extrude
    /// impossible, because the second step bumped the revision and killed the sketch the third
    /// step was about to consume.
    /// </param>
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

    /// <summary>Ids currently held for a document — used by snapshots and diagnostics.</summary>
    public IReadOnlyList<string> ForDocument(string documentId) =>
        _byId.Values.Where(v => string.Equals(v.DocumentId, documentId, StringComparison.Ordinal)).Select(v => v.Id).ToArray();
}
