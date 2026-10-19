namespace KompasMcp.Contracts;

/// <summary>Draft-domain contracts — block SM-12 (profile <c>drafts-minimal-v1</c>, modes
/// <c>SM-12.incline.*</c> and <c>SM-12.draft_from_edges.*</c>).</summary>
/// <remarks>INVARIANT: the side of a draft is declared relative to the BODY, never as the API flag. The
/// product's <c>direction</c>/<c>Direction</c> is read from the normal of the BASE OBJECT, so the same
/// flag tilts the face the other way on a plane and on a face. MEASURED: 382367.30 / 417632.70 mm³ on
/// the reference bar for the two sides. History: docs/decisions/adapter-features.md#draft-route-and-direction
/// </remarks>

/// <summary>Tilt faces of one body away from a base — the native «Уклон» (order SM-12).</summary>
public sealed record DraftCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string OperationId { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>Flat faces of ONE body, all with the same angle. INVARIANT: an empty list is refused
    /// before COM; faces of different bodies are a named refusal, not a silent first body.</summary>
    public required IReadOnlyList<string> FaceRefs { get; init; }

    /// <summary>Base as a flat face of the body. Exactly one of <see cref="BaseFaceRef"/> and
    /// <see cref="BasePlaneRef"/> must be set — both or neither is <c>INVALID_ARGUMENT</c>.</summary>
    public string? BaseFaceRef { get; init; }

    /// <summary>Base as a plane in the form the project already uses (<c>xy</c>/<c>xz</c>/<c>yz</c> or a
    /// <c>plane_ref</c>). A plane parallel to the drafted face is refused.</summary>
    public string? BasePlaneRef { get; init; }

    /// <summary>Draft angle in DEGREES, <c>0 &lt; |angle| &lt; 90</c>. The bound is the contract's, not
    /// the kernel's: 0° changes nothing and 90° degenerates the face.</summary>
    public required double AngleDeg { get; init; }

    /// <summary><c>outward</c> or <c>inward</c> RELATIVE TO THE BODY: outward adds material, inward
    /// removes it. Mandatory — there is no default, because the product's own default is the opposite
    /// thing on the two kinds of base.</summary>
    public required string Direction { get; init; }

    /// <summary>Declared volume after the operation, as with <c>kompas_shell</c>.</summary>
    public double? ExpectedVolumeMm3 { get; init; }
}

/// <summary>Tilt a face away from a base line — the native «Уклон от базовой линии» (API7 only).</summary>
public sealed record DraftFromEdgesCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string OperationId { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>Edges of the base line: one edge or a chain. Empty is refused before COM.</summary>
    public required IReadOnlyList<string> BaseLineEdgeRefs { get; init; }

    /// <summary>Direction object: a plane, a flat face, a straight edge or an axis. A revolution
    /// surface is <c>CAPABILITY_UNAVAILABLE</c> — it is not chosen in this profile.</summary>
    public required string DirectionRef { get; init; }

    /// <summary>Side of the draft: a face adjacent to the base line. Null lets the product choose, as
    /// the guide documents, and the response names what it chose.</summary>
    public string? SideFaceRef { get; init; }

    public required double AngleDeg { get; init; }

    /// <summary>«Сменить направление»: <c>Reverse</c> of the feature. Default <c>false</c>; the response
    /// always names the value it used.</summary>
    public bool Reverse { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }
}

/// <summary>Both draft families read back from the model, not echoed from the request.</summary>
public sealed record DraftResult
{
    public required string FeatureRef { get; init; }

    /// <summary><c>draft</c> or <c>draft_from_edges</c> — which family the feature belongs to.</summary>
    public required string Family { get; init; }

    public required int FaceCount { get; init; }

    public required double? AngleReadBackDeg { get; init; }

    /// <summary>The side as the MODEL reports it, translated to <c>outward</c>/<c>inward</c> against the
    /// body. Null when the base normal could not be read — an unread value is null, never a guess.</summary>
    public required string? DirectionReadBack { get; init; }

    public required bool? ReverseReadBack { get; init; }

    public required double? VolumeBeforeMm3 { get; init; }

    public required double? VolumeAfterMm3 { get; init; }

    public required int BodiesBefore { get; init; }

    public required int BodiesAfter { get; init; }

    public required long RevisionBefore { get; init; }

    public required long RevisionAfter { get; init; }

    public required VerificationDto Verification { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>The two draft sides as the contract names them.</summary>
public static class DraftDirection
{
    public const string Outward = "outward";

    public const string Inward = "inward";
}

/// <summary>Pure rules of the draft contracts, shared by validation, schema bounds and tests.</summary>
/// <remarks>INVARIANT: the flag the product receives is DERIVED from the side and the base orientation;
/// the caller's word is never passed through. MEASURED: on the reference bar the XOY plane (normal into
/// the body) and the bottom face (normal out of it) give opposite results for the same flag.
/// History: docs/decisions/adapter-features.md#draft-route-and-direction</remarks>
public static class DraftRules
{
    /// <summary>The contract's own angle bound: 0° does nothing, 90° degenerates the face.</summary>
    public const double MinAbsAngleDeg = 0d;

    public const double MaxAbsAngleDeg = 90d;

    /// <summary><c>0 &lt; |angle| &lt; 90</c>, finite. A NaN or an infinity is refused here, not by COM.</summary>
    public static bool AngleInRange(double angleDeg) =>
        double.IsFinite(angleDeg)
        && Math.Abs(angleDeg) > MinAbsAngleDeg
        && Math.Abs(angleDeg) < MaxAbsAngleDeg;

    /// <summary>Exactly one base form: a face or a plane, never both, never neither.</summary>
    public static bool SingleBase(string? faceRef, string? planeRef) =>
        (faceRef is not null) ^ (planeRef is not null);

    /// <summary>The <c>direction</c> flag the product must receive.</summary>
    /// <param name="direction"><c>outward</c> or <c>inward</c>, relative to the body.</param>
    /// <param name="baseNormalPointsOutOfBody">Whether the normal of the base object points away from the
    /// material. A standard plane has no material side; the adapter passes the side the plane faces.</param>
    /// <returns>The flag, or null when the word is not one of the two the contract publishes.</returns>
    public static bool? ProductDirection(string? direction, bool baseNormalPointsOutOfBody) => direction switch
    {
        DraftDirection.Outward => baseNormalPointsOutOfBody,
        DraftDirection.Inward => !baseNormalPointsOutOfBody,
        _ => null,
    };

    /// <summary>The contract side for a product flag and a base orientation — the read-back direction.</summary>
    public static string? ContractDirection(bool productFlag, bool baseNormalPointsOutOfBody) =>
        productFlag == baseNormalPointsOutOfBody ? DraftDirection.Outward : DraftDirection.Inward;
}
