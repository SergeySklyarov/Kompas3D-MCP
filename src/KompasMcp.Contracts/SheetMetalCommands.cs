namespace KompasMcp.Contracts;

/// <summary>Sheet-metal block — profile <c>sheet-metal-minimal-v1</c>, modes <c>SMB-01…SMB-03</c>.</summary>
/// <remarks>INVARIANT: a body is created through the documented API7 route
/// <c>IPart7 as ISheetMetalContainer</c> → <c>ISheetMetalBodies.Add()</c> → <c>Sketch</c>/<c>Thickness</c>/
/// <c>Radius</c>/<c>Direction</c>/<c>UnfoldType</c> → <c>IModelObject.Update()</c>. INVARIANT: success is
/// the READ-BACK values matching the requested ones plus a collection counter that grew by one, never
/// <c>Update() = true</c>. INVARIANT: <c>ISheetMetalBody</c> has no <c>IsCreated</c> in v24 — a body is
/// judged by the counter, its properties and its extent. History: docs/decisions/sheet-metal.md#body-tools</remarks>
public sealed record CreateSheetMetalBodyCommand
{
    /// <summary>Existing sketch of the part (from <c>kompas_list_sketches</c>); it becomes the profile of
    /// the flat body.</summary>
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    public required double ThicknessMm { get; init; }

    public required double BendRadiusMm { get; init; }

    /// <summary>By name: <c>normal</c>, <c>reverse</c>, <c>both</c>, <c>middle_plane</c> — the values
    /// <c>ksdirectiontypeenum.html</c> names, taken from the same enum the body page points at.</summary>
    public required string Direction { get; init; }

    /// <summary>By name: <c>coefficient</c>, <c>bend_value</c>, <c>bend_reduction</c>. <c>bend_table</c> is
    /// NOT a value: the kernel does not accept the type without a bend table and the help does not
    /// describe the table's file format (MEASURED, probe routes).</summary>
    public required string UnfoldType { get; init; }

    /// <summary>Only with <c>unfold_type = coefficient</c> (<c>ISheetMetalBody.BendCoefficient</c>).</summary>
    public double? BendCoefficient { get; init; }

    /// <summary>Only with <c>unfold_type = bend_value</c> (<c>BendValue</c>).</summary>
    public double? BendValue { get; init; }

    /// <summary>Only with <c>unfold_type = bend_reduction</c> (<c>BendReduction</c>).</summary>
    public double? BendReduction { get; init; }
}

/// <summary>Read the sheet-metal state: one body by reference, or every body of the document.</summary>
public sealed record GetSheetMetalCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Body from <c>kompas_create_sheet_metal_body</c>. <c>null</c> — every body of the
    /// document plus the collection counters.</summary>
    public string? BodyRef { get; init; }
}

/// <summary>Change an existing sheet-metal body: thickness, bend radius, unfold type and its field.</summary>
/// <remarks>INVARIANT: only supplied fields are written; a field of another unfold type is a refusal
/// naming the field, because a number the kernel records and ignores reads as success.
/// History: docs/decisions/sheet-metal.md#body-tools</remarks>
public sealed record EditSheetMetalBodyCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string BodyRef { get; init; }

    public double? ThicknessMm { get; init; }

    public double? BendRadiusMm { get; init; }

    /// <summary>By name, as in <see cref="CreateSheetMetalBodyCommand.UnfoldType"/>.</summary>
    public string? UnfoldType { get; init; }

    public double? BendCoefficient { get; init; }

    public double? BendValue { get; init; }

    public double? BendReduction { get; init; }
}

/// <summary>One sheet-metal body as the MODEL holds it. <c>null</c> means "not read", never zero.</summary>
public sealed record SheetMetalBodyDto
{
    public string? BodyRef { get; init; }

    /// <summary>Ordinal in <c>ISheetMetalContainer.SheetMetalBodies</c>.</summary>
    public int? Ordinal { get; init; }

    public double? ThicknessMm { get; init; }

    public double? BendRadiusMm { get; init; }

    /// <summary>Unfold-length method by name, or the raw number when the kernel answers an unknown one.</summary>
    public string? UnfoldType { get; init; }

    /// <summary>The field the method selects, read back: <c>BendCoefficient</c>, <c>BendValue</c> or
    /// <c>BendReduction</c>. <c>null</c> — not the current method, or not read.</summary>
    public double? UnfoldValue { get; init; }

    /// <summary><c>ISheetMetalBody.Straighten</c> — the flag, not a rebuilt geometry.</summary>
    public bool? Straighten { get; init; }

    /// <summary>Extent along the sketch normal: for a flat body this IS the thickness (MEASURED).</summary>
    public double? ExtentByNormalMm { get; init; }
}

/// <summary>Collection counters of <c>ISheetMetalContainer</c>, as the pages name the properties. An
/// <c>unread</c> entry names a counter the kernel did not answer — silence is not a zero.</summary>
public sealed record SheetMetalCountersDto
{
    public int? Bodies { get; init; }

    public int? Bends { get; init; }

    public int? Cuts { get; init; }

    public int? Holes { get; init; }

    public int? LineBends { get; init; }

    public int? ClosedCorners { get; init; }

    public int? Flangings { get; init; }

    public int? Jalousies { get; init; }

    public int? Plates { get; init; }

    public int? PressFormings { get; init; }

    public int? Punchs { get; init; }

    public int? Ribs { get; init; }

    public int? Shoulders { get; init; }

    public int? SketchBends { get; init; }

    public int? Undercuts { get; init; }

    public int? RuledShells { get; init; }

    public int? LinearRuledShells { get; init; }

    public int? ConvertsToSheetMetals { get; init; }

    public int? BendedStraightens { get; init; }

    /// <summary>Names of the counters that did not answer.</summary>
    public IReadOnlyList<string> Unread { get; init; } = Array.Empty<string>();
}

public sealed record CreateSheetMetalBodyResult(
    ReferenceDto BodyRef,
    SheetMetalBodyDto Body,
    int BodiesBefore,
    int BodiesAfter,
    VerificationDto Verification);

public sealed record GetSheetMetalResult(
    IReadOnlyList<SheetMetalBodyDto> Bodies,
    SheetMetalCountersDto Counters,
    VerificationDto Verification)
{
    /// <summary>Every bend of the document, in collection order. Empty when the collection is empty;
    /// <c>null</c> — the counter did not answer, so "none" is not claimed.</summary>
    public IReadOnlyList<SheetMetalBendDto>? Bends { get; init; }

    /// <summary>Every hole of the document, in collection order.</summary>
    public IReadOnlyList<SheetMetalHoleDto>? Holes { get; init; }
}

public sealed record EditSheetMetalBodyResult(
    ReferenceDto BodyRef,
    SheetMetalBodyDto BodyBefore,
    SheetMetalBodyDto BodyAfter,
    VerificationDto Verification);

/// <summary>A bend on one edge of an existing sheet-metal body.</summary>
/// <remarks>INVARIANT: exactly one edge — <c>ISheetMetalBends.Add()</c> was measured with one object only.
/// INVARIANT: the edge must belong to the body being bent: the kernel accepts an edge of ANOTHER body and
/// grows the bend counter, and this product cannot delete the bend afterwards.
/// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
public sealed record CreateSheetMetalBendCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string BodyRef { get; init; }

    /// <summary>One edge of that body, as the edge listing of the product addresses it.</summary>
    public required string EdgeRef { get; init; }

    /// <summary><c>ISheetMetalBend.Angle</c> in degrees.</summary>
    public required double AngleDeg { get; init; }

    /// <summary><c>ISheetMetalBend.Direction</c> — the page calls it BOOL and does not name its values;
    /// <c>true</c> is the value the probe wrote.</summary>
    public bool? Direction { get; init; }

    /// <summary><c>ISheetMetalBend.InternalRadius</c>: TRUE — внутренний радиус, FALSE — наружный.</summary>
    public bool? InternalRadius { get; init; }

    /// <summary><c>ISheetMetalBend.Radius</c> in mm; only meaningful together with
    /// <see cref="InternalRadius"/>.</summary>
    public double? RadiusMm { get; init; }

    /// <summary>By name: <c>continue</c>, <c>contour</c>, <c>touch</c>, <c>contour_internal</c>,
    /// <c>tangent_internal</c> — <c>ksBendLengthTypeEnum</c>.</summary>
    public string? LengthType { get; init; }

    /// <summary><c>ISheetMetalBend.Length</c> — length of the straight part, mm.</summary>
    public double? LengthMm { get; init; }
}

/// <summary>A round hole in an existing sheet-metal body, placed on a face of that body.</summary>
/// <remarks>INVARIANT: the kernel builds the hole's own sketch (<c>isheetmetalhole_sketch.html</c>: «Эскиз
/// создается при создании»), so the caller gives a face and a point on it, never a sketch.
/// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
public sealed record CreateSheetMetalHoleCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string BodyRef { get; init; }

    /// <summary>The face the hole sits on (<c>ISheetMetalHole.BasePlane</c>), a face of that body.</summary>
    public required string BaseFaceRef { get; init; }

    public required double XMm { get; init; }

    public required double YMm { get; init; }

    public required double DiameterMm { get; init; }

    /// <summary>By name: <c>by_width</c> (0), <c>by_depth</c> (1) — <c>ksHoleCutTypeEnum</c>.
    /// <c>up_to_surface</c> (2) is NOT a value: it needs <c>DepthObject</c>, a face that sets the
    /// depth.</summary>
    public string? CutType { get; init; }

    /// <summary><c>ISheetMetalHole.Depth</c>, mm — only with <c>cut_type = by_depth</c>.</summary>
    public double? DepthMm { get; init; }
}

/// <summary>One bend as the MODEL holds it. <c>null</c> means "not read", never zero.</summary>
public sealed record SheetMetalBendDto
{
    public string? BendRef { get; init; }

    /// <summary>Ordinal in <c>ISheetMetalContainer.SheetMetalBends</c>.</summary>
    public int? Ordinal { get; init; }

    public double? AngleDeg { get; init; }

    public bool? Direction { get; init; }

    public bool? InternalRadius { get; init; }

    public double? RadiusMm { get; init; }

    public string? LengthType { get; init; }

    public double? LengthMm { get; init; }

    /// <summary><c>BendObjects</c> holds the requested edge, compared by COM identity. <c>null</c> — not
    /// read. The kernel hands the edge back as another wrapper, so a name is not available at all.</summary>
    public bool? EdgeIsRequested { get; init; }
}

/// <summary>One hole as the MODEL holds it.</summary>
public sealed record SheetMetalHoleDto
{
    public string? HoleRef { get; init; }

    public int? Ordinal { get; init; }

    public double? XMm { get; init; }

    public double? YMm { get; init; }

    public double? DiameterMm { get; init; }

    /// <summary>By name, as in <see cref="CreateSheetMetalHoleCommand.CutType"/>.</summary>
    public string? CutType { get; init; }

    public double? DepthMm { get; init; }
}

public sealed record CreateSheetMetalBendResult(
    ReferenceDto BendRef,
    SheetMetalBendDto Bend,
    int BendsBefore,
    int BendsAfter,
    SheetMetalBodyDto? Body,
    VerificationDto Verification);

public sealed record CreateSheetMetalHoleResult(
    ReferenceDto HoleRef,
    SheetMetalHoleDto Hole,
    int HolesBefore,
    int HolesAfter,
    SheetMetalBodyDto? Body,
    VerificationDto Verification);
