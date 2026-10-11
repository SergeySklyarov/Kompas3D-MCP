namespace KompasMcp.Contracts.Ipc;

/// <summary>Request to write ONE parametritic constraint on one object of an existing sketch
/// (<c>kompas_create_sketch_constraint</c>).</summary>
/// <remarks>DOC: the documented API7 route — <c>IDrawingObject1::NewConstraint()</c> →
/// <c>IParametriticConstraint</c> → <c>Create()</c>. MEASURED: the object comes from the sketch's own
/// drawing container (<c>IDrawingContainer.GetObjects</c>), not from <c>IView1::FindObject</c>, whose
/// parameter class is not registered on the target installation.
/// INVARIANT: <see cref="Value"/>, <see cref="Expression"/> and <see cref="Variable"/> are accepted
/// for <c>ksCDimWithVariable</c> ONLY. History: docs/decisions/contracts.md#sketch-constraints</remarks>
public sealed record CreateSketchConstraintCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Type NAME from the <c>ksConstraintTypeEnum</c> vocabulary (<c>ksCHorizontal</c>,
    /// <c>ksCFixedPoint</c>, …). The vocabulary is taken from the constants assembly, not from a list
    /// duplicated in the schema.</summary>
    public required string ConstraintType { get; init; }

    /// <summary>Address of the object the constraint is written on, as issued by
    /// <c>kompas_list_sketch_entities</c>.</summary>
    public required string ObjectRef { get; init; }

    /// <summary>Point index on the object; <c>null</c> means "not set". For a circle <c>0</c> is the
    /// centre (<c>iparametriticconstraint_index.html</c>).</summary>
    public int? PointIndex { get; init; }

    /// <summary>Address of the second object. An array form is used for the types whose partner is a set
    /// (<c>ksCAssociation</c>); <see cref="PartnerRefs"/> carries that form.</summary>
    public string? PartnerRef { get; init; }

    /// <summary>Several partner addresses, for the types whose partner is a SAFEARRAY
    /// (<c>iparametriticconstraint_partner.html</c>).</summary>
    public IReadOnlyList<string>? PartnerRefs { get; init; }

    public int? PartnerPointIndex { get; init; }

    /// <summary>Address of the axis object, for the types that carry one
    /// (<c>ksCSymmetryTwoPoints</c>, <c>ksCBisector</c>).</summary>
    public string? AxisRef { get; init; }

    /// <summary>Value of a driving dimension. <c>ksCDimWithVariable</c> ONLY.</summary>
    public double? Value { get; init; }

    /// <summary>Expression of a driving dimension. <c>ksCDimWithVariable</c> ONLY.</summary>
    public string? Expression { get; init; }

    /// <summary>Name of the variable of a driving dimension. <c>ksCDimWithVariable</c> ONLY.</summary>
    public string? Variable { get; init; }
}

/// <summary>One constraint of an object, as READ BACK from the model. An unread field is <c>null</c>
/// with the reason in the row notes — never zero and never "false".</summary>
public sealed record SketchConstraintRowDto(
    int Index,
    string? Type,
    int? TypeCode,
    int? PointIndex,
    int? PartnerIndex,
    int? SegmentIndex,
    int? PartnerSegmentIndex,
    bool? Valid,
    double? Value,
    string? Expression,
    string? Variable,
    bool? PartnerPresent,
    IReadOnlyList<string> Notes);

/// <summary>Answer of <c>kompas_create_sketch_constraint</c>: the constraint was written and CONFIRMED by
/// re-reading the object's list, the sketch state AND the geometry the constraint must bind.</summary>
/// <param name="Applied">Whether the requested type is present in the list AFTER the write. Derived from
/// the re-read, not from the return of <c>Create()</c>.</param>
public sealed record SketchConstraintCreateResult(
    string ObjectRef,
    string ConstraintType,
    int TypeCode,
    bool Applied,
    int ConstraintCountBefore,
    int ConstraintCountAfter,
    IReadOnlyList<SketchConstraintRowDto> Constraints,
    int? StateRaw,
    string? StateName,
    string Route,
    IReadOnlyList<string> Notes,
    /// <summary>The relation re-read from the geometry and found to hold, or <c>null</c> when the type's
    /// relation is not a number (then the reason is in <see cref="UnverifiedAspects"/>). A relation that
    /// is measurable but does NOT hold is not a success — it is a named <c>GEOMETRY_FAILED</c>.</summary>
    string? VerifiedRelation = null,
    double? RelationMeasured = null,
    double? RelationExpected = null,
    double? RelationTolerance = null,
    /// <summary>What could NOT be confirmed by re-reading, each with its reason — the alternative to a
    /// silent pass. Empty means the relation was checked and holds.</summary>
    IReadOnlyList<string>? UnverifiedAspects = null);

/// <summary>Request to read the constraints of a sketch's objects.</summary>
public sealed record ListSketchConstraintsCommand
{
    public required string SketchRef { get; init; }

    /// <summary>Address of ONE object; without it every object of the sketch is listed.</summary>
    public string? ObjectRef { get; init; }

    public int? Limit { get; init; }
}

/// <summary>Constraints of one object of the sketch.</summary>
public sealed record SketchConstraintObjectDto(
    int ObjectIndex,
    string? ObjectRef,
    string? Kind,
    int? ObjectStateRaw,
    string? ObjectStateName,
    IReadOnlyList<SketchConstraintRowDto> Constraints);

/// <summary>Answer of <c>kompas_list_sketch_constraints</c>.</summary>
public sealed record SketchConstraintsResult(
    IReadOnlyList<SketchConstraintObjectDto> Objects,
    int ConstraintTotal,
    string Route,
    IReadOnlyList<string> Notes,
    /// <summary>The document's PARAMETRIC MODE (<c>IKompasDocument2D1.ParametricMode</c>, <c>BOOL</c>), as
    /// READ — <c>null</c> when unread, with the reason in <see cref="Notes"/>, never <c>false</c>.</summary>
    /// <remarks>DOC: <c>ikompasdocument2d1_parametricmode.html</c>, reachable on the sketch's FRAGMENT
    /// document. MEASURED: the shipped wrapper declares <c>IKompasDocument2D1</c> TRUNCATED (members end
    /// at <c>ChangeObjectsOrder</c>, slot 44), so the member is called through the typed twin
    /// <c>IKompasDocument2D1Twin</c>, not through <c>IDispatch</c>; only the getter is called, and it is
    /// published here because the fragment document exists only inside an edit session.
    /// History: docs/decisions/contracts.md#sketch-status-mapping</remarks>
    bool? ParametricMode = null);

/// <summary>Request to delete constraints of ONE object of an existing sketch.</summary>
public sealed record DeleteSketchConstraintCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string ObjectRef { get; init; }

    /// <summary>Delete ALL constraints of the object (<c>DeleteConstraints</c>) instead of one.
    /// <c>null</c> means "not stated" and is read as <c>false</c>: a field with a default must not be
    /// REQUIRED, or a client that trusts the published default would be refused for omitting it.</summary>
    public bool? All { get; init; }

    /// <summary>Position in the object's constraint list. Mandatory when <see cref="All"/> is false.
    /// The list is the one this server hands out, and it is re-read before the deletion.</summary>
    public int? ConstraintIndex { get; init; }
}

/// <summary>Answer of <c>kompas_delete_sketch_constraint</c>: the counts BEFORE and AFTER are measured
/// inside one session, so the difference describes the deletion and not two different sessions.</summary>
public sealed record SketchConstraintDeleteResult(
    string ObjectRef,
    bool All,
    bool Applied,
    int ConstraintCountBefore,
    int ConstraintCountAfter,
    IReadOnlyList<SketchConstraintRowDto> Constraints,
    int? StateRaw,
    string? StateName,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Request to create a DRIVING DIMENSION on a sketch (<c>kompas_create_sketch_dimension</c>):
/// four kinds share it — <c>linear</c> (the default), <c>diametral</c>, <c>radial</c>, <c>angular</c>.</summary>
/// <remarks>DOC: each kind has its own documented collection on <c>ISymbols2DContainer</c> → <c>Add()</c>
/// → the dimension interface → <c>IDrawingObject::Update()</c>; only the angular collection's <c>Add</c>
/// takes a drawing-object type (<c>ksDrADimension</c>).
/// MEASURED: the LINEAR kind is bound by <c>ksCAssociation</c>; the other three REFUSE it and are bound
/// by their base object. INVARIANT: a field of a foreign kind is refused before COM, and the measured
/// object's kind is checked against the kind asked for. History: docs/decisions/contracts.md#sketch-dimension</remarks>
public sealed record CreateSketchDimensionCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Address of the object the dimension measures, as issued by
    /// <c>kompas_list_sketch_entities</c>: the segment for <c>linear</c>, the circle for
    /// <c>diametral</c>, the arc or circle for <c>radial</c>, and the FIRST segment for
    /// <c>angular</c>.</summary>
    public required string ObjectRef { get; init; }

    /// <summary>Kind of the dimension: <c>linear</c> (the default), <c>diametral</c>, <c>radial</c> or
    /// <c>angular</c>. An omitted field is the default, so a call written before this field existed
    /// keeps its meaning.</summary>
    public string? Kind { get; init; }

    /// <summary>Address of the SECOND measured segment. <c>angular</c> ONLY, where it is required: an
    /// angle is measured between two segments. Supplying it for any other kind is a named refusal, not
    /// an ignored field.</summary>
    public string? SecondObjectRef { get; init; }

    /// <summary>Value of the dimension in millimetres. <c>linear</c> (the segment's length),
    /// <c>diametral</c> (the DIAMETER, not the radius) and <c>radial</c> (the RADIUS) ONLY. The
    /// segment's length / the diameter / the radius is expected to become exactly this number.</summary>
    public double? ValueMm { get; init; }

    /// <summary>Value of the dimension in degrees. <c>angular</c> ONLY: the angle between the two
    /// segments is expected to become exactly this number.</summary>
    public double? ValueDeg { get; init; }

    /// <summary>How far the dimension line is drawn from the segment, mm — <c>linear</c> ONLY, because
    /// only the linear dimension is placed by three points and a shelf. Positive puts it on the outer
    /// side of the segment (away from the sketch origin). Omitted means the default of 10; the other
    /// kinds place themselves from their base object and take their own position members
    /// (<c>Angle</c>, <c>Radius</c>, <c>Xc</c>/<c>Yc</c>), which this tool does not set.</summary>
    public double? OffsetMm { get; init; }
}

/// <summary>Answer of <c>kompas_create_sketch_dimension</c>.</summary>
/// <param name="Applied">Whether the confirmation held for the kind asked for: the value re-read as
/// requested AND the measured geometry re-read as requested — plus, for <c>linear</c>, the dimension's
/// points on the segment's ends. NOT the generic <c>Create() == true &amp;&amp; Valid != false</c> pair.</param>
/// <param name="GeometryName">WHICH geometry <see cref="GeometryBefore"/> and
/// <see cref="GeometryAfter"/> describe: <c>length_mm</c>, <c>diameter_mm</c>, <c>radius_mm</c> or
/// <c>angle_deg</c>. The pre-existing linear pair <see cref="LengthBeforeMm"/>/<see cref="LengthAfterMm"/>
/// is kept unchanged for clients written against the published contract.</param>
public sealed record SketchDimensionCreateResult(
    string ObjectRef,
    string DimensionRef,
    double? ValueMm,
    bool Applied,
    double? LengthBeforeMm,
    double? LengthAfterMm,
    int? DimensionPointsCount,
    bool? PointsMatchSegment,
    bool AssociationCreated,
    bool FixedDimCreated,
    bool ValueDimCreated,
    int AssociationAsMergePoints,
    IReadOnlyList<string> Notes,
    string Route,
    /// <summary>The kind that was placed, named back so a client that omitted the field learns what it
    /// got.</summary>
    string Kind,
    string? SecondObjectRef = null,
    double? ValueDeg = null,
    string? ValueUnit = null,
    string? GeometryName = null,
    double? GeometryBefore = null,
    double? GeometryAfter = null,
    /// <summary>The value re-read FROM THE DIMENSION ITSELF (<c>IDimensionText::NominalValue</c>), in
    /// <see cref="ValueUnit"/>. It is one of the confirmation's facts and is published next to
    /// <see cref="GeometryAfter"/> so a client sees the two SEPARATELY: the dimension's own value and the
    /// geometry the kernel moved. <c>null</c> means the member did not answer — the reason is in
    /// <see cref="Notes"/>.</summary>
    double? ValueRead = null,
    /// <summary>The sketch-feature variable the kernel gave the dimension (<c>v9</c> in the measured
    /// runs), so the client can bind it to a part variable with <c>kompas_bind_parameter</c> right away.
    /// <c>null</c> means it was not read, and the reason is in <see cref="Notes"/>.</summary>
    string? DimensionVariable = null);
