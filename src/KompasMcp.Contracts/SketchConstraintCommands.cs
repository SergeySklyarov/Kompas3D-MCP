namespace KompasMcp.Contracts.Ipc;

/// <summary>Request to write ONE parametritic constraint on one object of an existing sketch
/// (<c>kompas_create_sketch_constraint</c>).</summary>
/// <remarks>DOC: the route is the documented API7 one — <c>IDrawingObject1::NewConstraint()</c>
/// (<c>idrawingobject1_newconstraint.html</c>) → <c>IParametriticConstraint</c> → <c>Create()</c>
/// (<c>iparametriticconstraint_create.html</c>). MEASURED: the object is taken from the sketch's own
/// drawing container (<c>IDrawingContainer.GetObjects</c>), not from <c>IView1::FindObject</c>, whose
/// parameter class is not registered on the target installation.
/// INVARIANT: <see cref="Value"/>, <see cref="Expression"/> and <see cref="Variable"/> are accepted for
/// <c>ksCDimWithVariable</c> ONLY — the help says so for all three
/// (<c>iparametriticconstraint_value.html</c>), and accepting them elsewhere would let an ignored field
/// survive to acceptance looking like an applied dimension.
/// History: docs/decisions/contracts.md#sketch-constraints</remarks>
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
/// re-reading the object's list and the sketch state.</summary>
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
    IReadOnlyList<string> Notes);

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
    /// <summary>The document's PARAMETRIC MODE (<c>IKompasDocument2D1.ParametricMode</c>, <c>BOOL</c>) as it
    /// was READ, or <c>null</c> when it could not be read — in which case the reason is named in
    /// <see cref="Notes"/> and never substituted with <c>false</c>.</summary>
    /// <remarks>DOC: <c>ikompasdocument2d1_parametricmode.html</c>; reachable on the sketch's FRAGMENT
    /// document (<c>ikompasdocument2d1.html</c>: the 2D interface is obtained by <c>QueryInterface</c>).
    /// MEASURED 11.10.2026: the shipped wrapper declares <c>IKompasDocument2D1</c> TRUNCATED (its members end
    /// at <c>ChangeObjectsOrder</c>, slot 44), so the member is called through the typed twin
    /// <c>IKompasDocument2D1Twin</c>, which reproduces the vtable of the installed type library (slots 74
    /// put / 75 get) — not through <c>IDispatch</c>. READ ONLY: only the getter is called.
    /// WHY IT IS PUBLISHED HERE AND NOT BY <c>kompas_get_sketch_status</c>: the fragment document exists
    /// only inside an edit session, and this call already opens one; measured 11.10.2026, a read-only
    /// session opened on the sketch is not observation-neutral — a save that follows it carries a different
    /// constraint state (see the tool description and §4.73 of the API notes).
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

/// <summary>Request to create a LINEAR DIMENSION on a segment of an existing sketch
/// (<c>kompas_create_sketch_dimension</c>).</summary>
/// <remarks>DOC: the dimension is created by the documented route — <c>IView</c> QI
/// <c>ISymbols2DContainer</c> (<c>isymbols2dcontainer.html</c>) → <c>ILineDimensions::Add()</c>
/// (<c>ilinedimensions_add.html</c>) → <c>ILineDimension</c> (hierarchy
/// <c>IKompasAPIObject → IDrawingObject → ILineDimension</c>, <c>ilinedimension.html</c>) →
/// <c>IModelObject::Update()</c>. The dimension is bound to the segment ASSOCIATIVELY
/// (<c>ksCAssociation</c>, type 12), then made fixed (<c>ksCFixedDim</c>), then given its value
/// (<c>ksCDimWithVariable</c>) — MEASURED: without the association the value does not drive the
/// geometry, with it the length follows the value.
/// History: docs/decisions/contracts.md#sketch-dimension</remarks>
public sealed record CreateSketchDimensionCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Address of the SEGMENT the dimension measures, as issued by
    /// <c>kompas_list_sketch_entities</c>. The dimension's points are taken from that segment's own
    /// ends, so the dimension measures exactly what the client named.</summary>
    public required string ObjectRef { get; init; }

    /// <summary>Value of the dimension in millimetres. It is the driving value: the segment's length
    /// is expected to become exactly this number.</summary>
    public required double ValueMm { get; init; }

    /// <summary>How far the dimension line is drawn from the segment, mm. Positive puts it on the
    /// outer side of the segment (away from the sketch origin).</summary>
    public double OffsetMm { get; init; } = 10d;
}

/// <summary>Answer of <c>kompas_create_sketch_dimension</c>.</summary>
/// <param name="Applied">Whether the three facts hold: the dimension's points coincide with the
/// segment's ends, the dimension's value is re-read as requested, and the segment's length equals the
/// value. NOT the generic <c>Create() == true &amp;&amp; Valid != false</c> pair — see the remarks.</param>
/// <param name="AssociationAsMergePoints">The number of <c>ksCMergePoints</c> records the association
/// reads back as. MEASURED: the association appears as TWO such records with <c>Valid = true</c>,
/// while the <c>ksCAssociation</c> record itself carries <c>Valid = false</c> — so the generic rule is
/// NOT applied to the dimension and is NOT weakened for the other types either.</param>
public sealed record SketchDimensionCreateResult(
    string ObjectRef,
    string DimensionRef,
    double ValueMm,
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
    string Route);
