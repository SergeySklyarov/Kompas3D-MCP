namespace KompasMcp.Contracts;

/// <summary>Mate-domain contracts — block C2 (profile <c>mates-minimal-v1</c>, modes <c>MATE-01…MATE-06</c>).</summary>
/// <remarks>INVARIANT: mates use the documented API7 path: <c>IPart7.MateConstraints</c> →
/// <c>IMateConstraints3D.Add(MateConstraintType)</c> → <c>BaseObject1</c>/<c>BaseObject2</c> → <c>Update()</c>.
/// INVARIANT: a mate is addressed by "component + face number": the face comes from the documented
/// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c> (<c>kspart_bodycollection.html</c>) and moves into API7 as
/// <c>IModelObject</c>. INVARIANT: the mate type is passed by NAME, not number; an unknown name is rejected. History:
/// docs/decisions/contracts.md#mates-route</remarks>
public sealed record CreateMateCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Mate type by name: <c>coincidence</c>, <c>parallel</c>, <c>perpendicular</c>, <c>tangency</c>,
    /// <c>concentric</c>, <c>distance</c>, <c>angle</c>.</summary>
    public required string ConstraintType { get; init; }

    /// <summary>Reference to the first component (from <c>kompas_list_components</c>).</summary>
    public required string FirstComponentRef { get; init; }

    /// <summary>Face number of the first component in its <c>FaceCollection()</c>.</summary>
    public required int FirstFaceIndex { get; init; }

    public required string SecondComponentRef { get; init; }

    public required int SecondFaceIndex { get; init; }

    /// <summary>Direction-alignment variant by name: <c>opposite</c>, <c>cooriented</c>, <c>closest</c>. <c>null</c>
    /// keeps the documented KOMPAS default.</summary>
    public string? Alignment { get; init; }

    /// <summary>Constraint parameter (distance or angle) — <c>IMateConstraint3D.ParamValue</c>. Not set for mates
    /// without a parameter: <c>null</c> means "not set", not zero.</summary>
    public double? ParamValue { get; init; }

    /// <summary>Transmission only: motion of the first side by name — <c>rotation</c> or <c>linear</c>
    /// (<c>IMate3DTransmission.MotionType1</c>, <c>ksMateMotionTypeEnum</c>).</summary>
    public string? Motion1 { get; init; }

    /// <summary>Transmission only: motion of the second side (<c>MotionType2</c>).</summary>
    public string? Motion2 { get; init; }

    /// <summary>Transmission only: <c>Scale1</c> of the ratio, written through
    /// <c>IMate3DTransmission.SetScale(Scale1, Scale2)</c>; must be a finite number &gt; 0.</summary>
    public double? Scale1 { get; init; }

    /// <summary>Transmission only: <c>Scale2</c> of the ratio, same route and bounds as <see cref="Scale1"/>.</summary>
    public double? Scale2 { get; init; }

    /// <summary>Transmission only: <c>Direction1</c>; <c>null</c> keeps what the kernel sets.</summary>
    public bool? Direction1 { get; init; }

    /// <summary>Transmission only: <c>Direction2</c>; <c>null</c> keeps what the kernel sets.</summary>
    public bool? Direction2 { get; init; }
}

/// <summary>Enumerate the assembly's mates.</summary>
public sealed record ListMatesCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>Change an existing mate: its parameter (distance or angle) or, for a transmission, its ratio
/// and directions.</summary>
/// <remarks>INVARIANT: the two field groups never mix — a parameter where the mate has none and a
/// transmission field on another type are both refusals naming the field, because a number accepted and
/// never applied reads as success. The mate type lives in the MODEL, so this rule is enforced by the
/// adapter, not by the request schema. History: docs/decisions/mates.md#transmission-edit</remarks>
public sealed record SetMateParameterCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }

    /// <summary>Distance or angle mates only — <c>IMateConstraint3D.ParamValue</c>.</summary>
    public double? ParamValue { get; init; }

    /// <summary>Transmission only: <c>Scale1</c>, written through <c>SetScale</c> together with
    /// <see cref="Scale2"/>; one side alone is a refusal.</summary>
    public double? Scale1 { get; init; }

    /// <summary>Transmission only: <c>Scale2</c>, same route and rule as <see cref="Scale1"/>.</summary>
    public double? Scale2 { get; init; }

    /// <summary>Transmission only: <c>Direction1</c> (<c>put_Direction1</c>).</summary>
    public bool? Direction1 { get; init; }

    /// <summary>Transmission only: <c>Direction2</c> (<c>put_Direction2</c>).</summary>
    public bool? Direction2 { get; init; }
}

/// <summary>Set the component-fixation flag through a mate.</summary>
public sealed record SetMateFixedCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }

    /// <summary>By name: <c>none</c>, <c>first</c>, <c>second</c> (per <c>ksMateFixedTypeEnum</c>).</summary>
    public required string Fixed { get; init; }
}

/// <summary>Delete a mate.</summary>
public sealed record DeleteMateCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }
}

/// <summary>One mate row. An empty field means "not read", not zero: silence of the instrument is never turned into a
/// value.</summary>
public sealed record MateRowDto
{
    public required string MateRef { get; init; }

    /// <summary>Ordinal in <c>MateConstraintCollection</c> — also the address of the reference.</summary>
    public int? Ordinal { get; init; }

    /// <summary>Mate type by name; an unknown number is reported as the number, not invented.</summary>
    public string? ConstraintType { get; init; }

    public string? Alignment { get; init; }

    public string? Fixed { get; init; }

    public double? ParamValue { get; init; }

    public int? Direction { get; init; }

    /// <summary>Type of the first base object, as KOMPAS itself names it.</summary>
    public string? BaseObject1 { get; init; }

    public string? BaseObject2 { get; init; }

    /// <summary><c>IMateConstraint3D.Valid</c> — confirmation of the mate, not "Update()=true".</summary>
    public bool? Valid { get; init; }

    /// <summary>Transmission only, read from the model: <c>MotionType1</c> as <c>rotation</c>/<c>linear</c>.
    /// <c>null</c> — not read or not a transmission.</summary>
    public string? Motion1 { get; init; }

    /// <summary>Transmission only: <c>MotionType2</c> as <c>rotation</c>/<c>linear</c>.</summary>
    public string? Motion2 { get; init; }

    /// <summary>Transmission only: <c>Scale1</c> read back.</summary>
    public double? Scale1 { get; init; }

    /// <summary>Transmission only: <c>Scale2</c> read back.</summary>
    public double? Scale2 { get; init; }

    /// <summary>Transmission only: <c>Direction1</c> read back.</summary>
    public bool? Direction1 { get; init; }

    /// <summary>Transmission only: <c>Direction2</c> read back.</summary>
    public bool? Direction2 { get; init; }

    public string? Name { get; init; }
}

public sealed record ListMatesResult(
    IReadOnlyList<MateRowDto> Mates,
    int MateCount,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of creating a mate.</summary>
/// <param name="AlignmentRequested">The alignment asked for, echoed back; <c>null</c> — none was requested.</param>
/// <param name="AlignmentRead">Read after <c>Update()</c>; a FACT, not a criterion. <c>null</c> — not read.</param>
/// <param name="FaceNormalsDot">Dot of the two mated faces' normals in assembly coordinates — the MEASURED
/// orientation. An explicit value is confirmed by it (-1/+1); <c>closest</c> names none, not judged.</param>
/// <remarks>INVARIANT: an explicit value is confirmed by the dot, never by the number read back; a mismatch is
/// a refusal with partial effects. History: docs/decisions/mates.md#alignment-geometry-criterion</remarks>
public sealed record CreateMateResult(
    ReferenceDto MateRef,
    MateRowDto Mate,
    int MateCount,
    string? AlignmentRequested,
    string? AlignmentRead,
    double? FaceNormalsDot,
    VerificationDto Verification);

public sealed record SetMateParameterResult(
    ReferenceDto MateRef,
    double? ParamValueBefore,
    double? ParamValueAfter,
    VerificationDto Verification,
    MateTransmissionStateDto? TransmissionBefore = null,
    MateTransmissionStateDto? TransmissionAfter = null);

/// <summary>A transmission's own numbers as the MODEL holds them; every field is a read value, and
/// <c>null</c> means "not read", never zero or <c>false</c>.</summary>
public sealed record MateTransmissionStateDto
{
    public double? Scale1 { get; init; }

    public double? Scale2 { get; init; }

    public bool? Direction1 { get; init; }

    public bool? Direction2 { get; init; }

    /// <summary><c>IMateConstraint3D.Valid</c> — the mate's own verdict after the rebuild.</summary>
    public bool? Valid { get; init; }
}

public sealed record SetMateFixedResult(
    ReferenceDto MateRef,
    string? FixedBefore,
    string? FixedAfter,
    VerificationDto Verification);

public sealed record DeleteMateResult(
    ReferenceDto MateRef,
    int MateCountBefore,
    int MateCountAfter,
    VerificationDto Verification);
