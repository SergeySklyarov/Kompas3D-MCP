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
    VerificationDto Verification);

public sealed record EditSheetMetalBodyResult(
    ReferenceDto BodyRef,
    SheetMetalBodyDto BodyBefore,
    SheetMetalBodyDto BodyAfter,
    VerificationDto Verification);
