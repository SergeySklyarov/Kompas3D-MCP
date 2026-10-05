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
}

/// <summary>Enumerate the assembly's mates.</summary>
public sealed record ListMatesCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>Change the parameter of an existing mate (distance or angle).</summary>
public sealed record SetMateParameterCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }

    public required double ParamValue { get; init; }
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

    public string? Name { get; init; }
}

public sealed record ListMatesResult(
    IReadOnlyList<MateRowDto> Mates,
    int MateCount,
    string Route,
    IReadOnlyList<string> Notes);

public sealed record CreateMateResult(
    ReferenceDto MateRef,
    MateRowDto Mate,
    int MateCount,
    VerificationDto Verification);

public sealed record SetMateParameterResult(
    ReferenceDto MateRef,
    double? ParamValueBefore,
    double? ParamValueAfter,
    VerificationDto Verification);

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
