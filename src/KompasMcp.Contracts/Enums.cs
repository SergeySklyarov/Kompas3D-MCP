namespace KompasMcp.Contracts;

/// <summary>Version of the public tool/data contract. Bumped only with a documented migration.
/// Named in the plural so it cannot collide with the envelope's own <c>contract_version</c> member.</summary>
public static class ContractVersions
{
    public const string Current = "1.0";
}

/// <summary>Operation lifecycle (spec 1.8). <see cref="OutcomeUnknown"/> is a terminal-but-unresolved
/// state: the mutation may or may not have been applied and MUST NOT be retried blindly.</summary>
public enum OperationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    OutcomeUnknown,
}

/// <summary>What evidence backs a result. Higher levels include the guarantees of lower ones.
/// A tool must never report a level it did not actually reach (spec 1.11, 3.3).</summary>
public enum VerificationLevel
{
    /// <summary>Nothing beyond "the call returned" was checked.</summary>
    None = 0,

    /// <summary>Arguments were validated locally; no COM call happened.</summary>
    ArgumentValidated = 1,

    /// <summary>KOMPAS reported success. Not proof that the expected geometry exists.</summary>
    CallReturned = 2,

    /// <summary>An output file exists and is non-empty.</summary>
    FileCreated = 3,

    /// <summary>An independent parser read the file without errors.</summary>
    SyntaxChecked = 4,

    /// <summary>Expected entities/references exist inside the file (e.g. unique solids, instance count).</summary>
    StructureChecked = 5,

    /// <summary>The live model was re-read and shows the expected geometry (body count, bbox, volume).</summary>
    GeometryChecked = 6,

    /// <summary>The exported artefact was imported back and geometry matched within tolerance.</summary>
    GeometryRoundtripChecked = 7,
}

/// <summary>Machine-actionable advice about repeating a call (spec 2.2). Deliberately not a single
/// "retriable" flag: "retry with the same operation_id" and "re-acquire references first"
/// are different failure modes with different consequences.</summary>
public enum RetryPolicy
{
    /// <summary>Repeating is never correct.</summary>
    Never,

    /// <summary>Safe to send again with the identical <c>operation_id</c> and identical arguments.</summary>
    SameOperationId,

    /// <summary>Contextual references are stale; re-read the model and issue a new operation.</summary>
    ReacquireContext,

    /// <summary>Outcome is unknown; run reconciliation against the real document before any repeat.</summary>
    AfterReconciliation,
}

/// <summary>How the server came to own a KOMPAS application instance (spec 1.6).</summary>
public enum ApplicationOwnership
{
    Attached,
    Launched,
    Unknown,
}

/// <summary>Connection mode for <c>kompas_connect</c>.</summary>
public enum ConnectMode
{
    Attach,
    Launch,
}

/// <summary>Access requested when opening a document (spec 2.4).</summary>
public enum DocumentAccess
{
    ReadOnly,
    Edit,
}

/// <summary>What to do with a dirty document on close (spec 2.4). Default is the safe one.</summary>
public enum DirtyPolicy
{
    Refuse,
    Save,
    Discard,
}

/// <summary>Whether a checkpoint copies external dependencies or leaves them shared (spec 1.9).</summary>
public enum DependencyPolicy
{
    Copy,
    ReadOnly,
}

/// <summary>Coordinate space a component transform is expressed in (spec 1.10).</summary>
public enum CoordinateSpace
{
    Parent,
    AssemblyWorld,
}

/// <summary>Trustworthiness of external-change detection for a document (spec 1.7).</summary>
public enum ExternalChangeDetection
{
    Reliable,
    Conservative,
    Unavailable,
}

/// <summary>What exactly a pattern copies (docs/05 SM-18/SM-19; user help
/// <c>48_3_1_vibor_kopiruemih_obtktov</c>).</summary>
/// <remarks>DOC: copytype.html — the value maps to the numeric <c>ksObj3dTypeEnum</c>, and that mapping is
/// published by the SDK page, not inferred: operations — <c>o3d_meshCopy=35</c> (<c>o3d_circularCopy=36</c>),
/// bodies — <c>o3d_BodiesMeshCopy=528</c> (<c>o3d_BodiesCircularCopy=529</c>).
/// INVARIANT: the difference is substantive, not cosmetic — a pattern of OPERATIONS inherits the scope of
/// the source operation and creates no new bodies (help
/// <c>48_2_osobennoiti_postroeniy_massiviv_v_mnogotelnoy_detali</c>), while a pattern of BODIES creates body
/// copies and the body count grows. Hence a separate mode, not a flag.</remarks>
public enum PatternCopyKind
{
    /// <summary>Operations are copied (faces and edges, or operations with parameters). No new bodies.</summary>
    Operations,

    /// <summary>Bodies are copied. The body count grows.</summary>
    Bodies,
}

/// <summary>What a mirror pattern reflects (docs/05 SM-23).</summary>
/// <remarks>DOC: copytype.html — the mapping is published there: <c>o3d_mirrorOperation=48</c> is the
/// "mirror pattern" «зеркальный массив» of the selected operations, <c>o3d_mirrorAllOperation=49</c> is
/// «зеркально отразить все». Both operations produce <c>IMirrorPattern</c>; the second additionally answers
/// <c>IChooseBodies7</c> (copytype.html: «Дополнительно имеет интерфейс выбора тел IChooseBodies7»).</remarks>
public enum PatternMirrorMode
{
    /// <summary>Reflection of explicitly selected operations.</summary>
    SelectedOperations,

    /// <summary>Reflection of all bodies of the part; body selection goes through <c>IChooseBodies7</c>.</summary>
    AllBodies,
}
