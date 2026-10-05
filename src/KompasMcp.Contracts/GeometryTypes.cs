using System.Text.Json.Serialization;

namespace KompasMcp.Contracts;

public enum SketchEntityKind
{
    Line,
    Circle,
    Arc,
    Rectangle,
    Polyline,
}

/// <summary>Sketch primitive in sketch-local millimetres (spec 2.6). Each variant uses only the fields its
/// <see cref="SketchEntityKind"/> needs; the validator rejects the rest so a circle cannot quietly carry an
/// ignored <see cref="EndMm"/>.</summary>
public sealed record SketchEntityDto
{
    public required SketchEntityKind Kind { get; init; }

    /// <summary>line/polyline vertex/rectangle origin.</summary>
    public IReadOnlyList<double>? StartMm { get; init; }

    /// <summary>line end point.</summary>
    public IReadOnlyList<double>? EndMm { get; init; }

    /// <summary>circle/arc centre, rectangle corner.</summary>
    public IReadOnlyList<double>? CenterMm { get; init; }

    public double? RadiusMm { get; init; }

    /// <summary>Arc start angle, degrees, CCW from +X of the sketch frame.</summary>
    public double? StartDeg { get; init; }

    /// <summary>Arc sweep in degrees. Negative means clockwise — the sign is meaningful and must be
    /// preserved by the adapter (spec 2.6).</summary>
    public double? SweepDeg { get; init; }

    /// <summary>rectangle width along sketch +X, mm.</summary>
    public double? WidthMm { get; init; }

    /// <summary>rectangle height along sketch +Y, mm.</summary>
    public double? HeightMm { get; init; }

    /// <summary>polyline vertices, mm.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? PointsMm { get; init; }

    public bool? Closed { get; init; }
}

/// <summary>Which named plane, or an offset plane / planar face reference.</summary>
public sealed record PlaneRefDto
{
    public PlaneBase? Base { get; init; }

    /// <summary>Reference to an existing plane/face. Mutually exclusive with <see cref="Base"/>.</summary>
    public string? Reference { get; init; }

    /// <summary>Offset along the base plane normal, mm (sign convention is adapter-verified, G07).</summary>
    public double OffsetMm { get; init; }
}

public enum PlaneBase
{
    Xy,
    Xz,
    Yz,
}

public enum SketchEditMode
{
    Append,
    Replace,
    DeleteEntities,
}

public enum ExtrudeOperation
{
    /// <summary>First solid of an empty body list.</summary>
    Base,

    /// <summary>Add material to an existing body.</summary>
    Boss,

    /// <summary>Remove material from an existing body.</summary>
    Cut,
}

public enum ExtrudeDirection
{
    Positive,
    Negative,
    Symmetric,
}

/// <summary>How an extrusion ends. MEASURED on v24 (probe P2.1): <see cref="Blind"/> honours depth_mm for
/// every directionType, while <see cref="Through"/> (vendor etThroughAll) is honoured only when the
/// extrusion runs symmetric — with directionType 0 nothing is cut at all and with 1 every end condition
/// collapses to the same result. Through therefore takes no depth: 1 mm and 1000 mm cut identically through
/// a plate, and up-to-near-surface is a different condition, not a big number.</summary>
public enum ExtrudeEndCondition
{
    /// <summary>To a given depth (vendor etBlind). Default value.</summary>
    Blind,

    /// <summary>Cutting only: right through all the material (vendor etThroughAll).</summary>
    Through,
}

/// <summary>Chamfer construction method (docs/05 SM-11). The method must be named by the caller because
/// <see cref="DistanceAngle"/> is physically unavailable in API5: there is no angle in either
/// <c>ksChamferDefinition</c> or <c>SetChamferParam(transfer, d1, d2)</c> — MEASURED by probe F on v24
/// (12.09.2026).</summary>
public enum ChamferMode
{
    /// <summary>Two legs; API5 route (<c>NewEntity(o3d_chamfer=33)</c> + <c>SetChamferParam</c>).</summary>
    TwoDistances,

    /// <summary>Distance and angle; API7 route (<c>IChamfer.Angle</c>). The angle is in degrees (F.10).</summary>
    DistanceAngle,
}

/// <summary>Native-hole mode (docs/05 SM-07). All four were MEASURED by probe M on v24 and live only in
/// API7 of the same session: API5 has a hole (<c>o3d_hole=52</c>) but no mode parameters in its
/// definition.</summary>
public enum HoleMode
{
    /// <summary>Blind with a flat bottom: <c>ksDTValue</c> + <c>ksEFFlat</c>; removes π·r²·h (M.4).</summary>
    BlindFlat,

    /// <summary>Through counterbore: a pilot through plus an annular recess (M.2).</summary>
    ThroughCounterbore,

    /// <summary>Through countersink: a pilot through plus a conical chamfer (M.3).</summary>
    ThroughCountersink,
}

/// <summary>Rotation operation kind. THIS value decides the action, not <c>IRotated1.OperationResult</c>.</summary>
/// <remarks>MEASURED (17.09.2026, step R.26, run <c>95fa8441</c>): on a prepared 120×120×40 plate, <c>o3d_bossRotated</c>
/// with a written AND read-back <c>OperationResult = ksOperationCut</c> changed the volume by 0, while the same
/// operation created as <c>Add(o3d_cutRotated)</c> removed exactly 25132.7412287183 mm³. So <c>OperationResult</c>
/// round-trips and affects NOTHING — it is metadata. The kind is set by the CHOICE of factory call, and the client
/// cannot "switch" an already-open operation; hence <c>kompas_update_feature</c> refuses a kind change: that would
/// delete the feature and create a new one, not edit in place. History: docs/decisions/contracts.md#rotation-operation</remarks>
public enum RotationOperation
{
    /// <summary>First body: <c>o3d_baseRotated</c> (27).</summary>
    Base,

    /// <summary>Boss onto an existing body: <c>o3d_bossRotated</c> (28).</summary>
    Boss,

    /// <summary>Cut: <c>o3d_cutRotated</c> (29).</summary>
    Cut,
}

/// <summary>Rotation direction — <c>ksDirectionTypeEnum</c>, as MEASURED (R.26.sector).</summary>
/// <remarks>MEASURED on a half-turn: <c>Normal</c> (dtNormal=0) and <c>Both</c> (dtBoth=2) put material on both sides
/// (x[−20,20]); <c>MiddlePlane</c> (dtMiddlePlane=3) is one-sided (x[0,20]) and the ONLY one whose direction change
/// moves the sector; <c>Reverse</c> (dtReverse=1) builds NOTHING (<c>Update()</c> returns False, 0 bodies) and the
/// server rejects it before mutating. The shipped <c>BEARING 410</c> used <c>Both</c> for a real partial rotation (R.22).
/// INVARIANT: on a half-turn the volume does not differ between directions (a half-cylinder is symmetric), so "the
/// sector moved" is proved only by the side of the material — compare the extent, not the volume.
/// History: docs/decisions/contracts.md#rotation-direction</remarks>
public enum RotationDirection
{
    /// <summary>dtNormal=0 — both sides of the axis.</summary>
    Normal,

    /// <summary>dtReverse=1 — MEASURED: builds NOTHING. The server rejects it before mutating.</summary>
    Reverse,

    /// <summary>dtBoth=2 — both sides; the value from the shipped BEARING 410.</summary>
    Both,

    /// <summary>dtMiddlePlane=3 — one-sided, and the only one that moves the sector.</summary>
    MiddlePlane,
}

/// <summary>Thread specification (spec 2.6). Structural description, not a modelled helix unless asked.</summary>
public sealed record ThreadSpecDto
{
    public required string Kind { get; init; }

    public required double NominalDiameterMm { get; init; }

    public required double PitchMm { get; init; }

    public required string Handedness { get; init; }

    /// <summary>cosmetic | native | nominal_envelope — the caller must state which one is real.</summary>
    public required string Representation { get; init; }
}

/// <summary>Structural selection predicate (spec 2.5). Deliberately not free text.</summary>
public sealed record SelectionPredicateDto
{
    public string? SurfaceType { get; init; }

    /// <summary>Unit direction in <see cref="CoordinateSpace"/> the normal must match.</summary>
    public IReadOnlyList<double>? NormalDirection { get; init; }

    public double? NormalAngleToleranceDeg { get; init; }

    public CoordinateSpace? CoordinateSpace { get; init; }

    public string? ExtremumAxis { get; init; }

    public string? ExtremumMode { get; init; }

    public IReadOnlyList<double>? AreaRangeMm2 { get; init; }

    public IReadOnlyList<double>? BboxRangeMm { get; init; }
}

/// <summary>Properties a caller asks <c>kompas_measure</c> to return (spec 2.5).</summary>
public enum MeasurableProperty
{
    Bbox,
    Volume,
    SurfaceArea,
    Mass,
    Centroid,
}

/// <summary>Result of measuring one target.</summary>
public sealed record MeasurementDto
{
    public BoundingBoxDto? Bbox { get; init; }

    /// <summary>mm³. Null when not requested or not derivable.</summary>
    public double? VolumeMm3 { get; init; }

    public double? SurfaceAreaMm2 { get; init; }

    /// <summary>kg. Only when a density is actually known — a nullable beats an invented density (spec 1.10).</summary>
    public double? MassKg { get; init; }

    public IReadOnlyList<double>? CentroidMm { get; init; }

    /// <summary>Aspect the server could NOT confirm in the unit system, e.g. "volume_units".</summary>
    public IReadOnlyList<string> UnverifiedAspects { get; init; } = Array.Empty<string>();
}

/// <summary>Section-motion type along a kinematic path — <c>ksEvolutionShiftSketchTypeEnum</c>.</summary>
/// <remarks>DOC: ksevolutionshiftsketchtypeenum.html — the numbers were read from the official SDK v24 help
/// (opened over the wire 20.09.2026), not inferred by analogy: <c>ksEvShiftParallel = 0</c> —
/// «образующая переносится параллельно самой себе»; <c>ksEvShiftKeepAngle = 1</c> — «образующая при переносе
/// сохраняет исходный угол с направляющей»; <c>ksEvShiftOrtogonal = 2</c> — «плоскость образующей
/// выставляется и сохраняется ортогональной направляющей» (vendor spelling preserved).
/// MEASURED (20.09.2026, probe <c>--b5</c>, steps B5.1/B5.2): on an R50/90° arc the orthogonal mode gave
/// <c>24674.011002723353</c> against the expected <c>S × L = 24674.011002723397</c>, while the parallel one
/// gave <c>15707.963267948984</c> — a difference of <c>8966.047734774369</c> mm³. Hence the orthogonality-mode
/// acceptance row MUST stand on an ARC, not a segment.</remarks>
public enum SweepShiftMode
{
    /// <summary>ksEvShiftParallel = 0 — translated parallel to itself.</summary>
    Parallel,

    /// <summary>ksEvShiftKeepAngle = 1 — keeps the initial angle with the guide.</summary>
    KeepAngle,

    /// <summary>ksEvShiftOrtogonal = 2 — orthogonal to the guide.</summary>
    Orthogonal,
}

/// <summary>How a loft builds its end sections — <c>ksLoftBuildingType</c>.</summary>
/// <remarks>DOC: ksloftbuildingtype.html — the numbers were read from the official SDK v24 help (over the
/// wire 20.09.2026): <c>ksLoftAuto = 0</c>, <c>ksLoftByNormal = 1</c>, <c>ksLoftByObject = 2</c>,
/// <c>ksLoftCupola = 3</c>.</remarks>
public enum LoftBuilding
{
    /// <summary>ksLoftAuto = 0 — automatic.</summary>
    Auto,

    /// <summary>ksLoftByNormal = 1 — by normal.</summary>
    ByNormal,

    /// <summary>ksLoftByObject = 2 — by object.</summary>
    ByObject,

    /// <summary>ksLoftCupola = 3 — cupola.</summary>
    Cupola,
}

/// <summary>Direction in which a shell's thin wall is formed. The VALUE mapping is MEASURED, not inferred.</summary>
/// <remarks>MEASURED (20.09.2026, probe <c>--b5</c>, step B5.5; 100×80×10 box with the top face removed,
/// t = 2): <c>thinType = true</c> gives <c>21631.999999999996</c> mm³ — INWARD (cavity 96·76·8);
/// <c>thinType = false</c> gives <c>24832.000000000022</c> — OUTWARD (104·84·12 − 80000). Difference
/// <c>3200.0000000000255</c> mm³. INVARIANT: the half types differ — API7 <c>IShell.ThinType</c> is
/// <c>long</c>, API5 <c>ksShellDefinition.thinType</c> is <c>bool</c>.
/// History: docs/decisions/contracts.md#shell-thin-direction</remarks>
public enum ShellThinDirection
{
    /// <summary>thinType = true — material inward, cavity along the outer contour. MEASURED: 21632 at t = 2.</summary>
    Inward,

    /// <summary>thinType = false — material outward. MEASURED: 24832 at t = 2.</summary>
    Outward,
}
