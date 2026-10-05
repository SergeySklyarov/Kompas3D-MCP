using KompasMcp.Contracts;

namespace KompasMcp.Domain.Documents;

/// <summary>Whether the document is saved, in the MCP's OWN model: what confirms that the file on disk holds
/// the model KOMPAS is holding.</summary>
/// <remarks>DOC: the target version has no documented "document changed" flag — the v24 help lists every
/// member of <c>ksDocument3D</c> and there is no <c>IsSaved</c>/<c>Modified</c> (<c>ksdocument3d_methods.html</c>,
/// <c>ksdocument3d_properties.html</c>), nor such a member in the installed interop, so the application must
/// keep the state. LIMIT: a geometry fingerprint is not enough — a mutation wrote a fresh one, so
/// "fingerprints equal" held exactly when the model had already diverged from the file; a fingerprint answers
/// "did someone else change the model", nothing more. History: docs/decisions/documents.md#save-tracking</remarks>
public enum DocumentSaveState
{
    /// <summary>No change unconfirmed by a file write.</summary>
    Clean = 0,

    /// <summary>A change unconfirmed by a file write: a mutation (including a partial one), a UI edit, a
    /// failed save, or a document never saved.</summary>
    Dirty = 1,

    /// <summary>Could not be determined: the state is not readable. The conservative outcome is "changed",
    /// not "confirmed clean".</summary>
    Unknown = 2,
}

/// <summary>What the server does with the document on close — the result of the decision table.</summary>
public enum CloseAction
{
    /// <summary>Close as is (nothing to save, or the policy is discard).</summary>
    Close,

    /// <summary>Refuse: there are changes and the policy forbids closing without saving.</summary>
    Refuse,

    /// <summary>Save, confirm the write by reading back, then close.</summary>
    SaveThenClose,
}

/// <summary>Save-state transitions and the close decision. A separate COM-free module because the
/// transitions are the only part testable without KOMPAS.</summary>
public static class DocumentSaveTracking
{
    /// <summary>The fingerprint meaning "could not be read". A separate value, not a number: zero bodies of
    /// an empty document and "the collection did not answer" are different facts.</summary>
    public const string UnreadableFingerprint = "unavailable";

    /// <summary>Everything but confirmed-clean counts as changed: an unknown state is not passed off as
    /// <c>dirty=false</c>.</summary>
    public static bool IsDirty(DocumentSaveState state) => state != DocumentSaveState.Clean;

    /// <summary>A normal mutation: the model diverged from the file. Only a file write confirmed by reading
    /// back clears it — a context read or a revision bump does not.</summary>
    public static DocumentSaveState AfterMutation() => DocumentSaveState.Dirty;

    /// <summary>A model edit outside MCP: the observed fingerprint diverged from the previous one.</summary>
    public static DocumentSaveState AfterExternalChange() => DocumentSaveState.Dirty;

    /// <summary>A document never yet written (created and not saved).</summary>
    public static DocumentSaveState AfterCreate() => DocumentSaveState.Dirty;

    /// <summary>Opening a file: KOMPAS read it from disk, the model matches the file.</summary>
    public static DocumentSaveState AfterOpen() => DocumentSaveState.Clean;

    /// <summary>The fingerprint is unreadable: immutability is not proved, so "unknown".</summary>
    public static DocumentSaveState AfterUnreadableObservation() => DocumentSaveState.Unknown;

    /// <summary>A save confirmed: the operation succeeded AND the file was re-read from disk.</summary>
    public static DocumentSaveState AfterConfirmedSave() => DocumentSaveState.Clean;

    /// <summary>A failed save does not clear the state: "clean" becomes "changed"; "changed"/"unknown" stay
    /// as they are.</summary>
    public static DocumentSaveState AfterFailedSave(DocumentSaveState current) =>
        current == DocumentSaveState.Clean ? DocumentSaveState.Dirty : current;

    /// <summary>Close decision table. <c>discard</c> closes without saving regardless of state — that is what
    /// "discard changes" means; <c>refuse</c> refuses everything not confirmed clean; <c>save</c> saves
    /// before closing.</summary>
    public static CloseAction Decide(DocumentSaveState state, DirtyPolicy policy) => policy switch
    {
        DirtyPolicy.Discard => CloseAction.Close,
        DirtyPolicy.Refuse => IsDirty(state) ? CloseAction.Refuse : CloseAction.Close,
        DirtyPolicy.Save => IsDirty(state) ? CloseAction.SaveThenClose : CloseAction.Close,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "неизвестная политика закрытия"),
    };
}
