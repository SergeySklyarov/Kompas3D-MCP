namespace KompasMcp.Contracts;

/// <summary>Interference domain — block G1 (profile <c>assembly-interference-minimal-v1</c>, modes
/// <c>INT-01…INT-04</c>). Both commands READ the model: neither changes the revision, the dirty flag,
/// the feature count or the tree.</summary>
/// <remarks>INVARIANT: an unread outcome is NOT "no intersection" — a kernel call that threw leaves
/// <see cref="InterferencePairDto.Intersecting"/> null with a <c>basis</c> naming the failure, the same
/// rule that forbids substituting zero for an unread number. Intersections go through API5 and the gap
/// through API7; the two routes are never mixed inside one pair.
/// History: docs/decisions/assembly.md#interference-contracts</remarks>
public sealed record CheckInterferenceCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Components to check, as references from <c>kompas_list_components</c>. <c>null</c> means
    /// "every addressable top-level component". Fewer than two DISTINCT components is refused.</summary>
    public IReadOnlyList<string>? ComponentRefs { get; init; }

    /// <summary>Whether tangencies count as intersections. DOC: <c>ksbody_checkintersectionwithbody.html</c>
    /// — «checkTangent - считать касания пересечениями». The flag is echoed in the answer because it
    /// changes the meaning of every row.</summary>
    public bool CheckTangent { get; init; }

    /// <summary>Whether to read the interacting faces of every intersecting body pair.</summary>
    public bool IncludeFaces { get; init; }
}

/// <summary>One intersection found between two bodies. <see cref="Type"/> is a name from
/// <c>Intersection_Type</c>, never a bare number.</summary>
public sealed record InterferenceHitDto(
    int BodyA,
    int BodyB,
    string Type,
    IReadOnlyList<int>? IntersectingFacesA,
    IReadOnlyList<int>? IntersectingFacesB,
    IReadOnlyList<int>? ConnectedFacesA,
    IReadOnlyList<int>? ConnectedFacesB);

/// <summary>One checked component pair.</summary>
/// <remarks><see cref="Intersecting"/> and <see cref="Volumetric"/> are nullable: a kernel call that
/// threw leaves the pair UNKNOWN, and <c>false</c> there would claim "no intersection" for a question
/// nobody answered.</remarks>
public sealed record InterferencePairDto(
    string ComponentA,
    string ComponentB,
    bool? Intersecting,
    bool? Volumetric,
    IReadOnlyList<InterferenceHitDto> Intersections,
    string Basis);

/// <summary>Answer of the pair-wise interference check.</summary>
public sealed record CheckInterferenceResult(
    int PairsChecked,
    IReadOnlyList<InterferencePairDto> Pairs,
    bool CheckTangent,
    string CheckTangentMeaning,
    string Route,
    long Revision,
    IReadOnlyList<string> Notes);

/// <summary>One measured object: a component, or a face of that component.</summary>
/// <remarks>INVARIANT: <see cref="FaceIndex"/> is the number in the component body's
/// <c>FaceCollection</c> — the same numbering <c>kompas_create_mate</c> publishes.</remarks>
public sealed record MeasureObjectDto
{
    public required string ComponentRef { get; init; }

    public int? FaceIndex { get; init; }
}

/// <summary>Measure the minimum distance (and the angle, where defined) between two objects.</summary>
public sealed record MeasureGapCommand
{
    public required string DocumentId { get; init; }

    public required MeasureObjectDto Object1 { get; init; }

    public required MeasureObjectDto Object2 { get; init; }
}

/// <summary>Answer of the gap measurement.</summary>
/// <remarks>INVARIANT: <see cref="MinDistanceMm"/> and <see cref="MinPointsMm"/> are <c>null</c> when the
/// kernel did not define them (a non-zero distance is what the help promises); <c>0</c> would claim a
/// measured zero. <see cref="UnitsBasis"/> names where the unit comes from.
/// MEASURED: <c>IMeasurement3D.GetMinPoint1/2</c> do return the segment (at dx = 15 with B rotated 45°
/// the pair is <c>(5, 0, −1)</c> and <c>(15 − 5·√2, 0, −1)</c>), and they return FALSE for a pair whose
/// closest points are not unique — that is named, not turned into zeros.</remarks>
public sealed record MeasureGapResult(
    string? MeasureResult,
    double? MinDistanceMm,
    IReadOnlyList<IReadOnlyList<double>>? MinPointsMm,
    double? AngleDeg,
    bool? IsAngleValid,
    string UnitsBasis,
    string Route,
    long Revision,
    IReadOnlyList<string> Notes);
