namespace KompasMcp.Contracts.Ipc;

/// <summary>Command names on the Host→Worker pipe. Adding a command means adding a handler in the Worker; an unhandled
/// name fails as CAPABILITY_UNAVAILABLE rather than being ignored.</summary>
public static class WorkerCommands
{
    public const string EnvironmentProbe = "env.probe";
    public const string Ping = "sys.ping";

    /// <summary>Session inventory: KOMPAS instances and documents held by THIS Worker, with a dirty flag. A control
    /// command, not geometry: it serves exactly one decision — whether the session can be released without losing
    /// edits.</summary> <remarks>A separate command, not a repeat of <c>doc.list</c>: that one requires
    /// <c>application_id</c> and describes one instance, while release needs an inventory of ALL instances at once —
    /// otherwise a second application's document would stay unnamed and the "unsaved edits" refusal would be
    /// incomplete.</remarks>
    public const string SessionInventory = "session.inventory";
    public const string Connect = "app.connect";
    public const string Disconnect = "app.disconnect";
    public const string ListDocuments = "doc.list";
    public const string CreateDocument = "doc.create";
    public const string OpenDocument = "doc.open";
    public const string GetContext = "doc.context";
    public const string SaveDocument = "doc.save";
    public const string CloseDocument = "doc.close";
    public const string ListFeatures = "feat.list";

    /// <summary>Enumerates a part's SKETCHES — the objects <c>kompas_list_features</c> deliberately does not
    /// list.</summary>
    /// <remarks>DOC: <c>ksPart.EntityCollection(short objType)</c> — «При создании массив заполняется объектами
    /// указанного типа, содержащимися в компоненте» (<c>kspart_entitycollection.html</c>) with the type
    /// <c>o3d_sketch = 5</c>, «эскиз» → <c>ksSketchDefinition</c> / <c>ISketch</c> (<c>obj3dtype.html</c>).
    /// MEASURED: that collection still yields sketches with readable definitions after save → close → reopen
    /// (probe L), which is exactly what a reference minted at creation time does not survive. A separate
    /// command, not a wider <c>feat.list</c>: <c>feature_count</c> is asserted unchanged by neighbouring
    /// operations, so the meaning of that number must not move.
    /// History: docs/decisions/adapter-sketch.md#sketch-enumeration</remarks>
    public const string ListSketches = "sketch.list";
    public const string ListBodies = "body.list";
    public const string Measure = "geom.measure";
    public const string ResolveSelection = "geom.resolve";
    public const string ReadTopology = "topo.read";
    public const string CreateSketch = "sketch.create";
    public const string EditSketch = "sketch.edit";

    /// <summary>Changes the SUPPORT plane of an EXISTING sketch through the documented
    /// <c>ksSketchDefinition.SetPlane</c> («Изменить базовую плоскость эскиза»,
    /// <c>kssketchdefinition_setplane.html</c>), then a mandatory <c>sketch.Update()</c>.</summary> <remarks>DOC: the
    /// second half of the <c>edit</c> action of row <c>AUX-SKETCH.plane_and_profile_lifecycle</c>; the profile half is
    /// <c>kompas_edit_sketch</c>. A separate command, not a <c>plane</c> field on <c>kompas_edit_sketch</c>.
    /// History: docs/decisions/contracts.md#sketch-plane-command</remarks>
    public const string SetSketchPlane = "sketch.set_plane";
    public const string FinishSketch = "sketch.finish";
    public const string Extrude = "feat.extrude";

    /// <summary>Fillet by explicit references to the edges of the final body.</summary>
    public const string Fillet = "feat.fillet";

    /// <summary>Chamfer by explicit edge references (docs/05 SM-11). MEASURED: in API5 this is
    /// <c>NewEntity(o3d_chamfer=33)</c> + <c>ksChamferDefinition.SetChamferParam</c>; the «расстояние и угол» mode is
    /// API7 only (<c>IChamfer.Angle</c>).
    /// History: docs/decisions/contracts.md#chamfer-route</summary>
    public const string Chamfer = "feat.chamfer";

    /// <summary>Native hole (docs/05 SM-07). Modes MEASURED and reachable only through API7: mode parameters live on
    /// <c>HoleParameters</c> cast to the interface of its own mode (<c>ISpotfacingHoleParameters</c>,
    /// <c>ICountersinkHoleParameters</c>). A position off the origin is set by <c>IHoleDisposal.Point3DParamSurface</c>
    /// + <c>OffsetType=ksOffsetByCoords</c>.
    /// History: docs/decisions/contracts.md#hole-route</summary>
    public const string Hole = "feat.hole";

    /// <summary>Rotation (docs/05 SM-03). Route lies ENTIRELY in API7: <c>IModelContainer.Rotateds.Add(type)</c> →
    /// <c>QI(IRotated)</c> → write parameters → <c>Update()</c>. The API5 wrapper (<c>NewEntity</c> + <c>Create()</c>)
    /// does not work on this object — MEASURED, not a property of rotation.
    /// History: docs/decisions/contracts.md#rotation-route</summary>
    public const string Rotated = "feat.rotated";

    /// <summary>Sweep — «Элемент по траектории» (docs/05 SM-04).</summary> <remarks>DOC: the route is documented API5.
    /// <c>obj3dtype.html</c> documents <c>o3d_baseEvolution = 45 → ksBaseEvolutionDefinition</c>, but
    /// <c>ievolutions_add.html</c> lists only <c>o3d_bossEvolution</c> and <c>o3d_cutEvolution</c> for
    /// <c>IEvolutions::Add</c> — the base type is absent. MEASURED: <c>IEvolutions.Add(45)</c> returns
    /// <c>KompasAPI7.EvolutionClass</c>; the working route is <c>ksPart.NewEntity(45)</c> +
    /// <c>ksBaseEvolutionDefinition</c>.
    /// History: docs/decisions/contracts.md#sweep-route</remarks>
    public const string Sweep = "feat.sweep";

    /// <summary>Loft (docs/05 SM-05).</summary> <remarks>DOC: the route is documented API5, as with sweep.
    /// <c>obj3dtype.html</c> documents <c>o3d_baseLoft = 30 → ksBaseLoftDefinition</c>, but <c>ilofts_add.html</c>
    /// lists only <c>o3d_bossLoft</c> and <c>o3d_cutLoft</c> for <c>ILofts::Add</c>. MEASURED: the working route is
    /// <c>ksPart.NewEntity(30)</c> + <c>ksBaseLoftDefinition</c>, confirmed by volume <c>28000</c>.
    /// History: docs/decisions/contracts.md#loft-route</remarks>
    public const string Loft = "feat.loft";

    /// <summary>Shell (docs/05 SM-13).</summary> <remarks>DOC: the route is documented API7, the only one of the three
    /// needing no base type: <c>ishells_add.html</c> declares <c>IShells::Add()</c> with no type argument. MEASURED:
    /// <c>IModelContainer.Shells.Add()</c> returns <c>KompasAPI7._ShellClass</c> and casts to <c>IShell</c>; volumes
    /// 21632 / 24832 / 40256 confirm.
    /// History: docs/decisions/contracts.md#shell-route</remarks>
    public const string Shell = "feat.shell";

    /// <summary>Boolean operation on bodies with an explicit target and tools (docs/05 SM-15).</summary> <remarks>DOC:
    /// the documented API7 boolean path: <c>IModelContainer.Booleans.Add()</c> → <c>IBoolean</c> with <c>BaseObject</c>
    /// (target), <c>ModifyObjects</c> (tools), <c>BooleanType</c>, <c>SaveCopyModifyObjects</c>, then <c>Update()</c>.
    /// MEASURED: difference is target minus tools; the <c>ksBooleanType</c> values <c>ksIntersect=1</c>,
    /// <c>ksDifference=2</c>, <c>ksUnion=3</c> come from the enum, not the catalog. INVARIANT: the core neither rejects
    /// a repeated reference nor checks the target is outside the tool set.
    /// History: docs/decisions/contracts.md#boolean-route</remarks>
    public const string SolidBoolean = "solid.boolean";

    /// <summary>Splitting a body into parts by a plane (docs/05 SM-16).</summary> <remarks>DOC: route is the documented
    /// API7 path: <c>IModelContainer.SplitSolids.Add()</c> → <c>ISplitSolid</c> with the single meaningful member
    /// <c>CutObjects</c>, then <c>Update()</c>. MEASURED: splitting keeps ALL parts by construction, so no separate
    /// "kept set" member is needed.
    /// History: docs/decisions/contracts.md#split-route</remarks>
    public const string SolidSplit = "solid.split";

    /// <summary>Cutting a body by a plane with a chosen kept side (docs/05 SM-16).</summary> <remarks>DOC: route is the
    /// documented API7 path: <c>IModelContainer.Cuts.Add()</c> → <c>ICut</c> with <c>BuildingType = ksCutByPlane</c>,
    /// <c>CutObject</c> = plane, <c>Direction</c> = side choice, then <c>Update()</c>. MEASURED sign rule: with normal
    /// <c>(1,0,0)</c> and plane <c>x = 10</c>, <c>Direction = true</c> keeps the side along the normal (<c>s &gt;
    /// 0</c>, V = 18 000), <c>false</c> the opposite one (<c>s &lt; 0</c>, V = 6 000).
    /// History: docs/decisions/contracts.md#cut-by-plane-route</remarks>
    public const string SolidCutByPlane = "solid.cut_by_plane";

    /// <summary>Body translation and rotation (docs/05 SM-17).</summary> <remarks>DOC: route is the documented API7
    /// path: <c>IModelContainer.BodyRepositions.Add()</c> → <c>IBodyReposition</c> with <c>RepositionBody</c> = body,
    /// the placement written by <c>Position.InitByMatrix3D</c>, then <c>Update()</c>. MEASURED: only a homogeneous 4×4
    /// matrix writes the placement; routes from 12 numbers and <c>SetDisplacementByAxis</c> return <c>Update()
    /// = true</c> and do NOT move the body, so the adapter MUST verify the placement. Rotation direction is the
    /// right-hand rule, <c>(x,y) → (−y,x)</c>.
    /// History: docs/decisions/contracts.md#reposition-route</remarks>
    public const string SolidReposition = "solid.reposition";

    /// <summary>Parametric definiteness of an existing sketch — the status KOMPAS shows with the «+», «−», «!»
    /// signs.</summary> <remarks>DOC: route is documented API7: <c>TransferInterface(sketchEntity, ksAPI7Dual, 0)</c> →
    /// <c>ISketch.ConstraintsState</c> of type <c>ksConstraintsStateEnum</c>. MEASURED: five repeated reads changed
    /// neither volume nor topology counts, so the command runs as a READ — without <c>BeginEdit</c>, <c>Update</c> or
    /// rebuild.
    /// History: docs/decisions/contracts.md#sketch-status-route</remarks>
    public const string SketchStatus = "sketch.status";

    /// <summary>Reading the parameters of an existing feature (docs/05 §7 kompas_get_feature).</summary>
    public const string GetFeature = "feat.get";
    /// <summary>Editing the parameters of an existing feature in place (docs/05 §4.3, §7).</summary>
    public const string UpdateFeature = "feat.update";

    /// <summary>Grid pattern (docs/05 SM-18). Route is API7 only:
    /// <c>IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35)</c> → <c>QI(ILinearPattern)</c>.</summary> <remarks>DOC:
    /// the API7 route is published by SDK page <c>copytype.html</c> («o3d_meshCopy 35 ILinearPattern»). Not API5: the
    /// API5 definitions exist in the metadata dump, but patterns have no product route through <c>NewEntity +
    /// Create()</c> — the API5 wrapper around an API7 factory object builds nothing (<c>Create()</c> returns
    /// <c>true</c>, the volume does not change).
    /// History: docs/decisions/contracts.md#pattern-grid-route</remarks>
    public const string PatternGrid = "pattern.grid";

    /// <summary>Circular pattern (docs/05 SM-19): <c>FeaturePatterns.Add(o3d_circularCopy=36)</c> →
    /// <c>QI(ICircularPattern)</c>.</summary>
    public const string PatternCircular = "pattern.circular";

    /// <summary>Mirror pattern (docs/05 SM-23): <c>FeaturePatterns.Add(o3d_mirrorOperation=48</c> or
    /// <c>o3d_mirrorAllOperation=49)</c> → <c>QI(IMirrorPattern)</c>, the second kind additionally
    /// <c>QI(IChooseBodies7)</c>.</summary>
    public const string PatternMirror = "pattern.mirror";

    /// <summary>Re-read the parameters of an existing pattern or mirror from the model.</summary>
    public const string PatternRead = "pattern.read";

    /// <summary>Suppress and restore a feature (ksFeature.excluded, MEASURED).</summary>
    public const string SuppressFeature = "feat.suppress";

    /// <summary>Delete a feature with a list of dependent candidates before touching KOMPAS.</summary>
    public const string DeleteFeature = "feat.delete";
    public const string Rebuild = "doc.rebuild";
    public const string ExportStep = "export.step";
    public const string ImportStep = "import.step";
    public const string ExportImage = "export.image";
    public const string UnitProbe = "probe.units";

    // Assembly domain (order C1). Commands are named after the modes of profile assemblies-minimal-v1:
    // asm.list_components → ASM-03, asm.insert_component → ASM-02, asm.set_placement → ASM-04,
    // asm.replace_component → ASM-05, asm.check_links → ASM-06. ASM-01/ASM-07 reuse the common
    // lifecycle (doc.create/doc.open/doc.save/doc.close) instead of starting a second one.
    public const string ListComponents = "asm.list_components";
    public const string InsertComponent = "asm.insert_component";
    public const string SetComponentPlacement = "asm.set_placement";
    public const string ReplaceComponent = "asm.replace_component";
    public const string CheckComponentLinks = "asm.check_links";

    // ── block C2 "minimal mates" (profile mates-minimal-v1) ──
    // mate.create → MATE-01, mate.list → MATE-02, mate.set_parameter → MATE-03,
    // mate.set_fixed → MATE-04, mate.delete → MATE-05. MATE-06 (component placement after a mate)
    // is read by the existing asm.list_components and starts no tool of its own.
    public const string ListMates = "mate.list";
    public const string CreateMate = "mate.create";
    public const string SetMateParameter = "mate.set_parameter";
    public const string SetMateFixed = "mate.set_fixed";
    public const string DeleteMate = "mate.delete";
    // ── block DRW "drawings" (profile drawings-minimal-v1) ──
    // drawing.create_views → DRW-01, drawing.list_views → DRW-02, drawing.add_dimension → DRW-03,
    // drawing.set_title_block → DRW-04, drawing.export → DRW-05, drawing.set_technical_demand →
    // DRW-06, drawing.edit_view → the view edit action of DRW-01.
    public const string CreateDrawingViews = "drawing.create_views";
    public const string ListDrawingViews = "drawing.list_views";
    public const string AddDimension = "drawing.add_dimension";
    public const string SetTitleBlock = "drawing.set_title_block";
    public const string ExportDrawing = "drawing.export";
    public const string SetTechnicalDemand = "drawing.set_technical_demand";
    public const string EditView = "drawing.edit_view";
    // READ-ONLY siblings of the two setters above. A setter cannot witness persistence: it assigns the
    // expected value and then reads it back, so it passes even on a document that lost the value at
    // reopen. These read without carrying any expected value. History: docs/decisions/drawings.md#stamp-read
    public const string GetTitleBlock = "drawing.get_title_block";
    public const string GetTechnicalDemand = "drawing.get_technical_demand";
    // READ-ONLY dimension enumeration of the DRW-03 `read` action: a typed route that pairs each placed
    // dimension and reads its nominal, so survival across save → close → reopen can be judged on the
    // dimension objects themselves instead of on the view-wide `IView.ObjectCount`.
    public const string ListDimensions = "drawing.list_dimensions";
    // Rebuild the drawing after a model change (DRW-01 `rebuild` action). DOC:
    // ikompasdocument2d1_rebuilddocument.html. The typed member exists in the shipped interop;
    // IDrawingDocument.RebuildViews does NOT (measured by reflection).
    public const string RebuildDrawingViews = "drawing.rebuild_views";
    // ── block VM "variables and material" (profile variables-material-minimal-v1) ──
    // var.list → VM-01, var.set_value → VM-02, var.set_expression → VM-03, mat.get → VM-04,
    // mat.set → VM-04. VM-05 (save → close → reopen) reuses the common lifecycle plus the two
    // read-only commands, VM-06 measures an existing tool (kompas_measure), VM-07 reuses the shared
    // mutation/revision/dedup mechanism, VM-08 is the refusal class of all five commands.
    public const string ListVariables = "var.list";
    public const string SetVariableValue = "var.set_value";
    public const string SetVariableExpression = "var.set_expression";
    public const string GetMaterial = "mat.get";
    public const string SetMaterial = "mat.set";
    public const string Shutdown = "sys.shutdown";

    /// <summary>Creates a part auxiliary-geometry object — a plane, axis or point (<c>dep.refs.planes</c>,
    /// <c>dep.refs.axes</c>, <c>dep.refs.points_axes</c>).</summary> <remarks>DOC: route is documented API7, from the
    /// v24 help: <c>IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D</c>, <c>IModelContainer.GetPoints3D</c>, then
    /// <c>Add(ksObj3dTypeEnum)</c> with a type from the official table <c>obj3dtype.html</c>: <c>o3d_planeOffset</c> =
    /// 14, <c>o3d_planeAngle</c> = 15, <c>o3d_axis2Points</c> = 10, <c>o3d_axisConeFace</c> = 11, <c>o3d_axisEdge</c> =
    /// 12, <c>o3d_point3D</c> = 70.
    /// History: docs/decisions/contracts.md#aux-geometry-route</remarks>
    public const string CreateAuxGeometry = "aux.create";

    /// <summary>Enumerates and reads part auxiliary-geometry objects: planes, axes, points.</summary>
    /// <remarks>MEASURED: the help documents <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c> and
    /// <c>ISketchs.GetSketchByName</c>, but the shipped <c>Interop.KompasAPI7.dll</c> has NOT ONE member containing
    /// <c>ByName</c>. A name is therefore matched by enumerating the collection through the documented members
    /// <c>Count</c> + indexed property + <c>Name</c>. The returned collection index is NOT declared a stable address: a
    /// rebuild shifts it.
    /// History: docs/decisions/contracts.md#aux-list-route</remarks>
    public const string ListAuxGeometry = "aux.list";

    /// <summary>Edits an ALREADY CREATED plane as a part object: offset, tilt angle, support (<c>dep.refs.planes</c>,
    /// action <c>edit</c>).</summary> <remarks>A separate route, not a field on another call. MEASURED: editing the
    /// feature through a <c>plane</c> field was ACCEPTED (failure code <c>None</c>) and did NOT change the geometry.
    /// Here the edit goes through the documented setters <c>IPlane3DByOffset.Offset</c> / <c>IPlane3DByAngle.Angle</c>
    /// / <c>IPlane3DBy*.BasePlane</c>, then <c>Update()</c> and <c>RebuildModel</c>, and the answer carries the value
    /// READ BACK, not a success code passed off as an applied edit. History:
    /// docs/decisions/contracts.md#update-plane-route</remarks>
    public const string UpdatePlane = "aux.update_plane";

    /// <summary>Enumerates the entities of an EXISTING sketch with a STABLE ADDRESS (<c>dep.sketch.entities</c>,
    /// actions <c>discover</c> and <c>read</c>).</summary> <remarks>DOC: route documented by the v24 help:
    /// <c>ISketch.BeginEditEx(true)</c> → <c>IFragmentDocument.ViewsAndLayersManager.Views</c> → <c>IView</c> →
    /// <c>IDrawingContainer.GetObjects(ksAllObj)</c> → address <c>IKompasDocument1.GetObjectId</c> →
    /// <c>ISketch.EndEdit()</c>. The address is not a collection index nor a coordinate: it is the <c>GetObjectId</c>
    /// string that <c>FindObjectById</c> accepts back, so it survives a rebuild and a reopen; a collection index is NOT
    /// declared a stable address.</remarks>
    public const string ListSketchEntities = "sketch.entities";

    /// <summary>Address-targeted edit of ONE existing sketch entity (<c>dep.sketch.entities</c>, action
    /// <c>edit</c>).</summary> <remarks>MEASURED: the earlier sketch-edit schema accepted only a mode
    /// (<c>append</c>/<c>replace</c>/<c>delete_entities</c>) and a WHOLE NEW set of primitives — it recreated the
    /// contour rather than editing an entity (after <c>replace</c> the base-body volume changed from 80000 to 24000).
    /// Here the edit targets exactly one entity, and the answer carries the state read AFTER the edit — "accepted" and
    /// "applied" differ by measurement, not wording.
    /// History: docs/decisions/contracts.md#sketch-entity-edit-route</remarks>
    public const string EditSketchEntity = "sketch.entity_edit";
}

/// <summary>Request to create an auxiliary-geometry object.</summary> <remarks><para><b>Kind and mode are separated,
/// not merged into one field.</b> <see cref="Kind"/> answers "what it is" (plane, axis, point), <see cref="Mode"/> "how
/// it is built". One field with six values would make the answer ambiguous: "angle" without a kind does not say the
/// angle of what.</para> <para><b>Fields of foreign modes are not silently ignored.</b> Each mode declares its own set;
/// a field outside the set is rejected with <c>INVALID_ARGUMENT</c> listing its own fields. An accepted and ignored
/// field survives to acceptance looking like completed work — exactly the defect the contract forbids.</para></remarks>
public sealed record CreateAuxGeometryCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary><c>plane</c>, <c>axis</c> or <c>point</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Construction mode. For a plane: <c>offset</c> (offset along the normal) or <c>angle</c> (tilt around a
    /// base line). For an axis: <c>by_2_points</c>, <c>by_face</c> (cylindrical or conical surface), <c>by_edge</c>.
    /// For a point: <c>coordinates</c> or <c>displace</c> (offset from a support vertex).</summary>
    public required string Mode { get; init; }

    /// <summary>Offset in mm. Only for <c>plane/offset</c>.</summary>
    public double? OffsetMm { get; init; }

    /// <summary>Angle in degrees. Only for <c>plane/angle</c>.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>Direction sign. For an offset plane — along or against the normal; for a tilted one — the side the
    /// angle is measured from. <c>null</c> means "not set", and then the documented KOMPAS default is used rather than
    /// ours: substituting a sign ourselves would pass a guess off as a parameter.</summary>
    public bool? Direction { get; init; }

    /// <summary>Base plane by name: <c>xy</c>, <c>xz</c> or <c>yz</c>. Plane only.</summary>
    public string? BasePlane { get; init; }

    /// <summary>Base line (tilt axis) — a reference to an axis. Only for <c>plane/angle</c>.</summary>
    public string? BaseAxisRef { get; init; }

    /// <summary>Support — a FLAT FACE by reference. For <c>plane/offset</c> this is the documented support
    /// (<c>IPlane3DByOffset.BasePlane</c> accepts «базовую плоскость ИЛИ плоскую грань»).</summary>
    public string? BaseFaceRef { get; init; }

    /// <summary>First axis point. Only for <c>axis/by_2_points</c>.</summary>
    public double[]? Point1Mm { get; init; }

    /// <summary>Second axis point. Only for <c>axis/by_2_points</c>.</summary>
    public double[]? Point2Mm { get; init; }

    /// <summary>Axis face. Only for <c>axis/by_face</c>.</summary>
    public string? FaceRef { get; init; }

    /// <summary>Axis edge. Only for <c>axis/by_edge</c>.</summary>
    public string? EdgeRef { get; init; }

    /// <summary>Point coordinates. Only for <c>point/coordinates</c>.</summary>
    public double[]? CoordinatesMm { get; init; }

    /// <summary>Support vertex of the point. Only for <c>point/displace</c>.</summary>
    public string? AssociationVertexRef { get; init; }

    /// <summary>Point offset from the support vertex. Only for <c>point/displace</c>.</summary>
    public double[]? DisplacementMm { get; init; }

    /// <summary>Name of the object to create. Optional: without it KOMPAS supplies its own. A name is not an address:
    /// the address is the reference minted by enumeration.</summary>
    public string? Name { get; init; }
}

/// <summary>Result of creating an auxiliary-geometry object: read BACK from the model, not a restatement of the
/// request.</summary>
/// <param name="Kind">WHAT was created: <c>plane</c>, <c>axis</c>, <c>point</c> — read from the model.</param>
/// <param name="Mode">HOW it was built, read from the model in the request vocabulary. <c>null</c> when the native
/// construction is not expressible in that vocabulary (then <see cref="SubKind"/> carries the native type and the
/// reason is in <see cref="Diagnostics"/>). It is NEVER the echoed request: <see cref="Verification"/> carries a
/// <c>mode_read_back</c> check comparing the two, so a mismatch is visible instead of silently substituted.</param>
public sealed record AuxGeometryResult(
    string Kind,
    string? Mode,
    string? ReferenceId,
    string? Name,
    string? SubKind,
    double[]? PointMm,
    double[]? DirectionMm,
    double? AngleDeg,
    double? OffsetMm,
    bool? Direction,
    string? BaseName,
    string? LineName,
    int PlaneCount,
    int AxisCount,
    int PointCount,
    IReadOnlyList<string> Diagnostics,
    VerificationDto? Verification = null);

/// <summary>Request to enumerate auxiliary-geometry objects.</summary>
public sealed record ListAuxGeometryCommand
{
    public required string DocumentId { get; init; }

    /// <summary>What to read: <c>planes</c>, <c>axes</c>, <c>points</c> or <c>all</c>.</summary>
    public required string Include { get; init; }

    /// <summary>Name to address ONE object. Optional.</summary>
    public string? Name { get; init; }

    /// <summary>Row limit. The bound is named as a number: a row costs one COM call.</summary>
    public int? Limit { get; init; }
}

/// <summary>Enumeration answer: rows, route and collection counts.</summary>
public sealed record AuxGeometryListResult(
    IReadOnlyList<AuxGeometryRowDto> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Auxiliary-geometry enumeration row: an empty field means "not read", not zero.</summary>
/// <param name="Kind">WHAT it is: <c>plane</c>, <c>axis</c>, <c>point</c> (or <c>unreadable</c>).</param>
/// <param name="Mode">HOW it was built, in the request vocabulary of <c>kompas_create_aux_geometry</c>;
/// <c>null</c> when not expressible there (native type then in <see cref="SubKind"/>, reason in the notes).</param>
/// <param name="ReferenceId">Reference to this object, of the SAME kind the consuming tools accept
/// (<c>kompas_create_sketch</c>, <c>kompas_create_aux_geometry</c>, <c>kompas_update_plane</c>). <c>null</c>
/// when it could not be registered — the reason is then in <see cref="Notes"/>, and the row stays published.</param>
public sealed record AuxGeometryRowDto(
    string Kind,
    string? Mode,
    int Index,
    string? Name,
    string? SubKind,
    double[]? PointMm,
    double[]? DirectionMm,
    double? AngleDeg,
    double? OffsetMm,
    bool? Direction,
    string? BaseName,
    string? LineName,
    string? ReferenceId,
    IReadOnlyList<string> Notes);

/// <summary>Request to edit an existing plane. Exactly ONE of <see cref="OffsetMm"/> and <see cref="AngleDeg"/> is set,
/// and it must match the plane kind: an offset plane has no angle, a tilted one has no offset.</summary> <remarks>A
/// field of a foreign kind is rejected with <c>INVALID_ARGUMENT</c> listing the allowed ones — for the same reason
/// creation does this: an accepted and ignored field survives to acceptance looking like a completed edit.</remarks>
public sealed record UpdatePlaneCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Plane reference minted by creation or enumeration.</summary>
    public required string PlaneRef { get; init; }

    /// <summary>New offset along the normal, mm. Offset plane only.</summary>
    public double? OffsetMm { get; init; }

    /// <summary>New tilt angle, degrees. Tilted plane only.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>New support by name: <c>xy</c>, <c>xz</c> or <c>yz</c>.</summary>
    public string? BasePlane { get; init; }

    /// <summary>Direction sign: along or against the normal, the side the angle is measured from.</summary>
    public bool? Direction { get; init; }
}

/// <summary>Plane-edit result: the value READ BACK from the model and an applied flag derived from comparing the
/// request with the read-back.</summary>
public sealed record PlaneUpdateResult(
    string? ReferenceId,
    string? Kind,
    string? Mode,
    string? SubKind,
    string? Name,
    double? OffsetMm,
    double? AngleDeg,
    bool? Direction,
    string? BaseName,
    double[]? PointMm,
    double[]? DirectionMm,
    bool Applied,
    string? AppliedEvidence,
    int PlaneCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Request to enumerate sketch entities.</summary>
public sealed record ListSketchEntitiesCommand
{
    public required string SketchRef { get; init; }

    /// <summary>Address of ONE entity. Optional: without it all are enumerated.</summary>
    public string? Address { get; init; }

    /// <summary>Row limit: a row costs one COM call, the bound is named as a number.</summary>
    public int? Limit { get; init; }
}

/// <summary>Sketch-entity enumeration answer: rows, collection counts, route, notes.</summary>
public sealed record SketchEntitiesResult(
    IReadOnlyList<SketchEntityRowDto> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Sketch-entity enumeration row. An empty field means "not read", not zero; the reason is named in <see
/// cref="Notes"/>.</summary>
public sealed record SketchEntityRowDto(
    int Index,
    string? Address,
    string? Kind,
    string? Name,
    int? TypeCode,
    IReadOnlyList<string> Notes);

/// <summary>Request for an address-targeted sketch-entity edit. <see cref="Address"/> is mandatory: an edit of "the
/// first entity that comes up" is not address-targeted.</summary>
public sealed record EditSketchEntityCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string Address { get; init; }

    /// <summary><c>set_layer</c> — assign the layer number, <c>delete</c> — delete the entity.</summary>
    public required string Action { get; init; }

    /// <summary>Layer number. Mandatory when <c>action = set_layer</c>.</summary>
    public int? LayerNumber { get; init; }
}

/// <summary>Address-targeted edit result: the state READ AFTER the edit through the same address, and an applied flag.
/// "The code did not refuse" is not declared an application.</summary>
public sealed record SketchEntityEditResult(
    string? Address,
    string Action,
    bool Applied,
    bool? ResolvedAfter,
    int? LayerAfter,
    string? KindAfter,
    int EntityCountBefore,
    int EntityCountAfter,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of <see cref="WorkerCommands.EnvironmentProbe"/> — the P0 evidence record.</summary>
public sealed record EnvironmentProbeResult
{
    public required string WorkerRuntime { get; init; }

    public required string ProcessBitness { get; init; }

    /// <summary>ProgID → CLSID resolution actually observed in the registry.</summary>
    public required IReadOnlyDictionary<string, string?> ProgIdMap { get; init; }

    public required string? RegisteredServerPath { get; init; }

    public required string ServerPathBitness { get; init; }

    /// <summary>Live KOMPAS processes found by the OS, with window state. Read-only observation.</summary>
    public required IReadOnlyList<RunningInstanceInfo> RunningInstances { get; init; }

    /// <summary>Number of KOMPAS objects visible in the running object table.</summary>
    public required int RotEntryCount { get; init; }

    public required IReadOnlyList<string> InteropAssemblies { get; init; }

    public required IReadOnlyList<string> TypeLibraries { get; init; }

    /// <summary>Anything the probe could not determine — surfaced, not hidden.</summary>
    public IReadOnlyList<string> Unknowns { get; init; } = Array.Empty<string>();
}

public sealed record RunningInstanceInfo(int ProcessId, string ProcessName, bool HasMainWindow, string? MainWindowTitle, DateTimeOffset? StartTimeUtc, long WorkingSetBytes);

/// <summary>Payload of <see cref="WorkerCommands.Connect"/>.</summary>
public sealed record ConnectCommand
{
    public required ConnectMode Mode { get; init; }

    /// <summary>Explicit selector. Null means "attach is only allowed if exactly one candidate exists". Never resolves
    /// to "whichever object the ROT handed back first".</summary>
    public int? ProcessId { get; init; }

    public string? WindowTitle { get; init; }

    public string? ApplicationId { get; init; }

    public bool MakeVisible { get; init; }
}

public sealed record ConnectConflict
{
    public required IReadOnlyList<RunningInstanceInfo> Candidates { get; init; }

    /// <summary>ROT entries grouped by PID; more than one PID means the caller must choose.</summary>
    public required IReadOnlyList<int> RotProcessIds { get; init; }
}

public sealed record DisconnectCommand
{
    public required string ApplicationId { get; init; }

    public bool CloseOwnedApplication { get; init; }
}

public sealed record ListDocumentsCommand
{
    public required string ApplicationId { get; init; }
}

public sealed record CreateDocumentCommand
{
    public required string ApplicationId { get; init; }

    public required DocumentKind Kind { get; init; }

    public string? Name { get; init; }

    public string? Marking { get; init; }
}

public sealed record OpenDocumentCommand
{
    public required string ApplicationId { get; init; }

    /// <summary>Absolute, already policy-checked by the Host.</summary>
    public required string Path { get; init; }

    public required DocumentAccess Access { get; init; }
}

public sealed record GetContextCommand
{
    public required string DocumentId { get; init; }

    public string Detail { get; init; } = "minimal";
}

public sealed record SaveDocumentCommand
{
    public required string DocumentId { get; init; }

    public string? TargetPath { get; init; }

    public required long ExpectedRevision { get; init; }
}

public sealed record CloseDocumentCommand
{
    public required string DocumentId { get; init; }

    public required DirtyPolicy DirtyPolicy { get; init; }
}

public sealed record ListFeaturesCommand
{
    public required string DocumentId { get; init; }
}

public sealed record ListSketchesCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One sketch of a part, as a row of <c>kompas_list_sketches</c>.</summary>
/// <param name="SketchRef">A FRESH reference minted against the current revision — the same kind the
/// sketch tools accept; it is what makes a sketch reachable again after a rebuild or a reopen.</param>
/// <param name="Index">Position in the collection. A POSITION, not an address: a rebuild shifts it.</param>
/// <param name="Created">Whether the object is built. <c>null</c> means the flag did not read — named in
/// <see cref="Notes"/>, never reported as "not created".</param>
/// <param name="SupportPlaneName">Name of the support plane, read through <c>ksSketchDefinition.GetPlane()</c>;
/// <c>null</c> when it did not read, named in <see cref="Notes"/>.</param>
public sealed record SketchRowDto
{
    public required string SketchRef { get; init; }

    public string? Name { get; init; }

    public required int Index { get; init; }

    public bool? Created { get; init; }

    public string? SupportPlaneName { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Answer of <c>kompas_list_sketches</c>: the rows, the named route and the collection-level
/// notes.</summary>
/// <remarks>An OBJECT, not a bare array, and that is the same shape the neighbouring readers publish
/// (<c>kompas_list_sketch_entities</c>, <c>kompas_list_aux_geometry</c>): a bare array has nowhere to
/// put the route, and a client that receives an empty array cannot tell "no sketches" from "the
/// collection was not read" — the distinction this tool exists to keep.</remarks>
public sealed record SketchListResult(
    IReadOnlyList<SketchRowDto> Rows,
    string Route,
    IReadOnlyList<string> Notes);

public sealed record ListBodiesCommand
{
    public required string DocumentId { get; init; }
}

public sealed record MeasureCommand
{
    public required string TargetRef { get; init; }

    public required IReadOnlyList<MeasurableProperty> Properties { get; init; }

    /// <summary>Density in kg/m³, supplied by the caller. Server never guesses it.</summary>
    public double? DensityKgPerM3 { get; init; }
}

public sealed record ResolveSelectionCommand
{
    public required string BodyRef { get; init; }

    public required SelectionPredicateDto Predicate { get; init; }

    public bool RequireUnique { get; init; } = true;

    public int Limit { get; init; } = 100;
}

public sealed record ReadTopologyCommand
{
    public required string DocumentId { get; init; }

    public required string BodyRef { get; init; }

    /// <summary>faces | edges | both</summary>
    public required string Include { get; init; }
}

public sealed record CreateSketchCommand
{
    public required string DocumentId { get; init; }

    public required PlaneRefDto Plane { get; init; }

    public string? Name { get; init; }

    /// <summary>Revision the caller read. Enforced before any COM call, never advisory.</summary>
    public required long ExpectedRevision { get; init; }
}

public sealed record EditSketchCommand
{
    public required string SketchRef { get; init; }

    public required SketchEditMode Mode { get; init; }

    public required IReadOnlyList<SketchEntityDto> Entities { get; init; }

    /// <summary>Revision the caller read for the owning document.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>Request to change the support plane of an existing sketch (command <see
/// cref="WorkerCommands.SetSketchPlane"/>).</summary> <remarks>The shape of <see cref="Plane"/> is THE SAME as at
/// creation: <c>base</c>+<c>offset_mm</c> OR <c>reference</c>, exactly one at a time. A non-plane refusal happens
/// BEFORE COM and is named by a code. MEASURED: the core ACCEPTS a flat face as support — all six faces of a box rebind
/// the dependent body — and REJECTS an edge and a body (<c>SetPlane = False</c>). A <c>reference</c> not leading to a
/// plane is rejected by the KIND of the reference from the registry, without COM.
/// History: docs/decisions/contracts.md#sketch-plane-command</remarks>
public sealed record SetSketchPlaneCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Sketch reference minted by creation or by reading the feature.</summary>
    public required string SketchRef { get; init; }

    public required PlaneRefDto Plane { get; init; }

    /// <summary>Revision the caller read. Checked before touching COM.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>Support-change result: the support READ BACK through the documented <c>GetPlane()</c>, and the state of the
/// dependent body before and after — what distinguishes "accepted" from "applied".</summary> <remarks><b>The two halves
/// are named separately, and neither is passed off as the other.</b> Reading the support answers "was the support
/// written"; the bbox and volume of the dependent body answer "was the model rebuilt". MEASURED that on a support
/// change it is <c>sketch.Update()</c> that rebuilds, so <see cref="ApplyRoute"/> carries the route ACTUALLY invoked,
/// not the intent.</remarks>
public sealed record SetSketchPlaneResult(
    string SketchRef,
    string? SupportTypeBefore,
    string? SupportTypeAfter,
    string? SupportNameAfter,
    string? GabaritBefore,
    string? GabaritAfter,
    double? VolumeBefore,
    double? VolumeAfter,
    bool GeometryChanged,
    string ApplyRoute,
    IReadOnlyList<string> Diagnostics);

public sealed record FinishSketchCommand
{
    public required string SketchRef { get; init; }

    public bool RequireClosedProfile { get; init; } = true;
}

public sealed record ExtrudeCommand
{
    public required string SketchRef { get; init; }

    public required ExtrudeOperation Operation { get; init; }

    /// <summary> Depth in mm. Mandatory for <see cref="ExtrudeEndCondition.Blind"/>, forbidden for <see
    /// cref="ExtrudeEndCondition.Through"/> — the Host rejects either mistake before COM, because through-all ignores
    /// the number entirely. </summary>
    public double? DepthMm { get; init; }

    public ExtrudeEndCondition EndCondition { get; init; } = ExtrudeEndCondition.Blind;

    public required ExtrudeDirection Direction { get; init; }

    public string? TargetBodyRef { get; init; }

    /// <summary>Revision the caller read; mismatch is REVISION_CONFLICT before anything is created.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>Fillet of selected edges (docs/03 G04, docs/05 SM-09).</summary>
public sealed record FilletCommand
{
    /// <summary>Explicit <c>edge:</c> references obtained from kompas_read_topology. Collection position numbers are
    /// not accepted: docs/05 §6.5 forbids using indices as persistent identifiers.</summary>
    public required IReadOnlyList<string> EdgeRefs { get; init; }

    public required double RadiusMm { get; init; }

    /// <summary>Analytic expectation of the volume change, when derivable by the caller (e.g. 4·(1−π/4)·r²·h for four
    /// parallel edges). The server checks the measurement against it and does not report geometry_checked without a
    /// match. Without it, only the radius read-back confirms the edit, and the result is honestly marked as unproven
    /// geometry.</summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

public sealed record RebuildCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>Chamfer by explicit edges (docs/05 SM-11). The mode is stated explicitly: two legs are not enough for every
/// mode, and an angle is physically absent from the API5 definition.</summary>
public sealed record ChamferCommand
{
    /// <summary>Explicit <c>edge:</c> references from kompas_read_topology. Collection positions are not accepted:
    /// docs/05 §6.5 forbids using indices as persistent identifiers.</summary>
    public required IReadOnlyList<string> EdgeRefs { get; init; }

    /// <summary>two_distances (API5) or distance_angle (API7); see <see cref="ChamferMode"/>.</summary>
    public required ChamferMode Mode { get; init; }

    /// <summary>First leg, mm. Units MEASURED: the number is passed to the API as is and yields mm.</summary>
    public required double Distance1Mm { get; init; }

    /// <summary>Second leg, mm. Mandatory for two_distances, forbidden for distance_angle.</summary>
    public double? Distance2Mm { get; init; }

    /// <summary>Chamfer angle in DEGREES — MEASURED (Angle=30 with Distance1=2 removed 20·d·(d·tg 30°) as predicted;
    /// the radian hypothesis was rejected by the number). Mandatory for distance_angle, forbidden for
    /// two_distances.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>Chamfer side: in API5 this is the <c>transfer</c> parameter, in API7 <c>IChamfer.Direction</c>.
    /// MEASURED (F.4, F.11): with unequal legs the value changes which leg lands on which face; the volume does not
    /// differ, so the distinguisher is the lateral face areas.</summary>
    public bool Direction { get; init; }

    /// <summary>Analytic expectation of the volume decrease. For N parallel straight edges of length L this is
    /// N·(d₁·d₂/2)·L; without it only the direction of change is confirmed.</summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Native hole (docs/05 SM-07). The mode is stated explicitly because modes have different fields, and the
/// server MUST reject mismatches before touching COM rather than apply half.</summary> <remarks><para>The support face
/// is given by a <c>face:</c> reference, not "the top face of the body": <c>IChamfer.BaseObjects</c> and
/// <c>IHoleDisposal.BaseSurface</c> accept an object, and choosing it for the client would invent the support. Probe M
/// took the largest face by area, but that was a probe technique, not the contract.</para> <para>The position is an
/// optional coordinate pair ON the support face (MEASURED M.5: a Ø10 hole landed exactly at (25, 15)). Without it the
/// hole stays at the surface origin.</para></remarks>
public sealed record HoleCommand
{
    /// <summary>Explicit <c>face:</c> reference from kompas_read_topology: the surface the hole starts from.</summary>
    public required string FaceRef { get; init; }

    /// <summary>blind_flat (API7: ksDTValue + ksEFFlat), through_counterbore (M.2), through_countersink
    /// (M.3).</summary>
    public required HoleMode Mode { get; init; }

    /// <summary>Hole diameter, mm: for counterbore and countersink this is the PILOT diameter, not the recess or the
    /// mouth.</summary>
    public required double DiameterMm { get; init; }

    /// <summary>Depth, mm. Only when mode=blind_flat; forbidden for through modes.</summary>
    public double? DepthMm { get; init; }

    /// <summary>Counterbore recess diameter, mm (counterbore, M.2). Must exceed the pilot diameter.</summary>
    public double? CounterboreDiameterMm { get; init; }

    /// <summary>Counterbore recess depth, mm (counterbore, M.2).</summary>
    public double? CounterboreDepthMm { get; init; }

    /// <summary>Countersink mouth diameter, mm (M.3). Must exceed the pilot diameter.</summary>
    public double? CountersinkDiameterMm { get; init; }

    /// <summary>Countersink angle in DEGREES, strictly between 0 and 180 (M.3: a table of three angles — 60, 90, 120).
    /// For the "diameter + angle" mode the depth is DERIVED: the object returns <c>(rM − rP)/tan(angle/2)</c>, where
    /// <c>rM</c> is the mouth radius and <c>rP</c> the pilot radius (MEASURED N.2: a series of mouths Ø14/16/18/20/24
    /// at pilot Ø10 and 90° gave h = 2/3/4/5/7).</summary>
    public double? CountersinkAngleDeg { get; init; }

    /// <summary>Hole centre offset along the support face X, mm. Together with <see cref="OffsetYMm"/>.</summary>
    public double? OffsetXMm { get; init; }

    /// <summary>Hole centre offset along the support face Y, mm.</summary>
    public double? OffsetYMm { get; init; }

    /// <summary>Analytic expectation of the volume decrease, when derivable by the caller: <c>π·r²·h</c> (blind),
    /// <c>π·r²·h + π/4·(D²−d²)·h_recess</c> (counterbore), <c>π·r²·h + π·h_actual/3·(rM² + rP·rM − 2·rP²)</c>
    /// (countersink, where <c>h_actual</c> is the depth the object returned, not the requested one). Without it, only
    /// the parameter read-back confirms the edit, and the result is honestly marked as unproven geometry.</summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Rotation (docs/05 SM-03). The operation kind is stated explicitly and in advance: the factory kind
/// (<c>Rotateds.Add(27/28/29)</c>) declares which <c>OperationResult</c> values are admissible for it. MEASURED on one
/// geometry: <c>ksOperationUnion</c> fuses (bodies 1→1), <c>ksOperationNewBody</c> makes a second body (1→2).
/// History: docs/decisions/contracts.md#rotation-operation-result</summary>

/// <remarks>The axis is mandatory and is built in the SAME part. MEASURED: a rotation written without an axis is not
/// built at all — <c>Update()</c> returns False and 0 bodies remain. The axis is given by two points in MODEL
/// coordinates, built by the server as <c>o3d_axis2Points</c>, because <c>IRotated.Axis</c> accepts a model object, not
/// a sketch line, and a foreign axis is not substituted. The angle is in DEGREES and equals the built one up to a full
/// turn: <c>Angle[true]</c> carries the requested angle directly (90→90°, 180→180°, 360→360°); refusal starts only
/// above 360. Thin wall was not tested. History: docs/decisions/contracts.md#rotation-angle-limit</remarks>
public sealed record RotatedCommand
{
    /// <summary>Profile sketch: an explicit <c>sketch:</c> reference from kompas_create_sketch /
    /// kompas_get_feature.</summary>
    public required string SketchRef { get; init; }

    /// <summary>Operation kind. The factory call itself decides the action, not OperationResult.</summary>
    public required RotationOperation Operation { get; init; }

    /// <summary>Angle in DEGREES, strictly greater than 0 and not more than 360. A full turn (360) is built with one
    /// call: MEASURED that the write yields a full cylinder (at r=20, h=40), confirmed by a blind read of the saved
    /// <c>.m3d</c>. A value above 360 is rejected before mutating: a sector cannot exceed a full turn.
    /// History: docs/decisions/contracts.md#rotation-angle-limit</summary>
    public required double AngleDeg { get; init; }

    /// <summary>First axis point in model coordinates, mm.</summary>
    public required IReadOnlyList<double> AxisPoint1Mm { get; init; }

    /// <summary>Second axis point in model coordinates, mm. Must differ from the first.</summary>
    public required IReadOnlyList<double> AxisPoint2Mm { get; init; }

    /// <summary>Direction. <see cref="RotationDirection.Reverse"/> is rejected before mutating: MEASURED (R.26.sector)
    /// that this value builds nothing.</summary>
    public RotationDirection Direction { get; init; } = RotationDirection.Normal;

    /// <summary>Target body for boss and cut — an explicit <c>body:</c> reference. Mandatory for boss and cut: gluing
    /// and cutting "in general" means choosing the body for the client, and a multi-body part does not forgive such a
    /// choice. Forbidden for base.</summary>
    public string? TargetBodyRef { get; init; }

    /// <summary>Thin wall, mm. Not set — the body is solid, and this is the MEASURED route setting:
    /// <c>IThinParameters.Thin = false</c> (R.24/R.25). Thin wall was not tested by any run, so if it is set the server
    /// refuses CAPABILITY_UNAVAILABLE rather than write a number that was not measured.</summary>
    public double? ThinWallMm { get; init; }

    /// <summary>Analytic expectation of the volume AFTER the operation, when derivable by the caller. Without it, only
    /// the parameter read-back confirms the edit, and the result is honestly marked as unproven geometry.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Grid pattern (docs/05 SM-18).</summary> <remarks>The axis is
/// given by two MODEL points, not a reference: <c>ILinearPattern.Axis1/Axis2</c> accept <c>IModelObject</c>, and the
/// server builds the axis itself as <c>o3d_axis2Points</c> in the SAME part — an axis reference could drag one in from
/// a foreign part. MEASURED: <c>ILinearPattern.Vector1/Vector2</c> declare getters only in <c>kAPI7.tlb</c>, absent AT
/// ALL in <c>Interop.KompasAPI7.dll</c>, so direction is set by an axis, not a vector. The second direction
/// is optional: without <see cref="Axis2Point1Mm"/> and <see cref="Count2"/> this is a single-row pattern, <c>Count2 =
/// 1</c>. History: docs/decisions/contracts.md#pattern-grid-axis</remarks>
public sealed record PatternGridCommand
{
    public required string DocumentId { get; init; }

    /// <summary>What is copied: operations or bodies. The factory numeric type decides, not a flag.</summary>
    public required PatternCopyKind CopyKind { get; init; }

    /// <summary>Pattern source objects: <c>feature:</c> references (for operations) or <c>body:</c> references (for
    /// bodies). An empty list is rejected before COM: a pattern without sources is not built.</summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>First axis point of the first direction, model coordinates, mm.</summary>
    public required IReadOnlyList<double> Axis1Point1Mm { get; init; }

    /// <summary>Second axis point of the first direction, model coordinates, mm.</summary>
    public required IReadOnlyList<double> Axis1Point2Mm { get; init; }

    /// <summary>Step along the first direction, mm.</summary>
    public required double Step1Mm { get; init; }

    /// <summary>Instance count along the first direction, including the source. Greater than 1.</summary>
    public required int Count1 { get; init; }

    /// <summary>Tilt angle of the first grid axis, DEGREES. Not set — the model value stays as is (0), i.e. the axis is
    /// taken as built.</summary> <remarks>The type was MADE OPTIONAL BY MEASUREMENT, not for looks: while the field was
    /// mandatory "not set" was inexpressible and the adapter ALWAYS wrote zero, which for the second axis broke the
    /// grid (a rectangular grid degenerated into a line). The tool schema already declared both angles optional
    /// (Sch.Nullable), so the change does not alter the wire contract — only the behaviour when the field is omitted.
    /// History: docs/decisions/contracts.md#pattern-grid-angles</remarks>
    public double? Angle1Deg { get; init; }

    /// <summary>Copy direction along the first axis.</summary>
    public bool Direction1 { get; init; } = true;

    /// <summary>Interpretation of the step at the first-direction boundary (<c>BoundaryInstancesStepFactor1</c>).
    /// Default <c>false</c>.</summary>
    public bool BoundaryInstancesStepFactor1 { get; init; }

    /// <summary>First axis point of the second direction; not set — a single-row pattern.</summary>
    public IReadOnlyList<double>? Axis2Point1Mm { get; init; }

    /// <summary>Second axis point of the second direction.</summary>
    public IReadOnlyList<double>? Axis2Point2Mm { get; init; }

    /// <summary>Step along the second direction, mm. Effective only together with <see cref="Count2"/>.</summary>
    public double? Step2Mm { get; init; }

    /// <summary>Instance count along the second direction. Not set — the direction does not participate.</summary>
    public int? Count2 { get; init; }

    /// <summary>Angle BETWEEN grid directions, DEGREES. A rectangular grid is 90°; 0° aligns the second direction with
    /// the first. Not set — the model value stays as is (90°).</summary> <remarks>"Angle between directions", not "tilt
    /// of the second axis": MEASURED in one setup, at 0° copies continued the first axis, at 90° they stood along the
    /// second, and at 270° the model normalized the value to 180° and sent the second direction the opposite way. The
    /// second axis itself sets WHICH way the angle is laid out.
    /// History: docs/decisions/contracts.md#pattern-grid-angles</remarks>
    public double? Angle2Deg { get; init; }

    /// <summary>Copy direction along the second axis.</summary>
    public bool Direction2 { get; init; } = true;

    /// <summary>Interpretation of the step at the second-direction boundary.</summary>
    public bool BoundaryInstancesStepFactor2 { get; init; }

    /// <summary>Pattern building method, <c>ksLinearPatternBuildingTypeEnum</c> as a contract word: <c>save_all</c>
    /// (0), <c>save_along_perimeter</c> (1), <c>save_along_axially</c> (2), <c>chess_order_by_axis1</c> (3),
    /// <c>chess_order_by_axis2</c> (4). The numbers were read from <c>Interop.Kompas6Constants3D.dll</c>, not from the
    /// catalog.</summary>
    public string BuildingType { get; init; } = "save_all";

    /// <summary>Geometric copy (<c>IFeaturePattern.GeometryPattern</c>). Documented by SDK page
    /// <c>ifeaturepattern_geometrypattern.html</c>; route B4 is measured on <c>false</c>, so <c>true</c> is rejected
    /// before COM as an unmeasured mode.</summary>
    public bool GeometryPattern { get; init; }

    /// <summary>Analytic expectation of the document volume AFTER the operation, mm³.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>Analytic expectation of the body count AFTER the operation. For an operation pattern it equals the body
    /// count before, for a body pattern it grows. The comparison is exact: no tolerance is applied to countable
    /// quantities (the profile's tolerance_classes).</summary>
    public int? ExpectedBodyCount { get; init; }

    /// <summary>Radius of the cylindrical face by which the instance count is taken (mm). A Ø10 hole gives radius 5.
    /// Without it the per-name instance check is not performed, and this is honestly marked in
    /// <c>unverified_aspects</c> rather than passed off as a check.</summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Height of the cylindrical face, mm (plate thickness for a through hole).</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Analytic expectation of the hole-instance count. Exact match.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>Analytic axis coordinates of each instance in model coordinates, mm. Volume does not distinguish four
    /// holes from three plus one superimposed — this set does.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Circular pattern (docs/05 SM-19).</summary> <remarks>DOC: the direction mapping is taken from SDK page
/// <c>icircularpattern_props.html</c>: <c>Count1</c>/<c>Step1</c> are the RADIAL direction, <c>Count2</c>/<c>Step2</c>
/// the CIRCULAR one («Количество экземпляров в КОЛЬЦЕВОМ направлении», «УГЛОВОЙ шаг (ГРАДУСЫ)»).
/// INVARIANT: a full turn is expressed by the pair (Count2, Step2), not a flag — <c>ICircularPattern</c> has no
/// separate "full turn" member; the semantics is <c>Step2 = 360 / Count2</c>.
/// History: docs/decisions/contracts.md#pattern-circular-mapping</remarks>
public sealed record PatternCircularCommand
{
    public required string DocumentId { get; init; }

    public required PatternCopyKind CopyKind { get; init; }

    /// <summary>Pattern source objects: <c>feature:</c> or <c>body:</c> references.</summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>First pattern axis point, model coordinates, mm.</summary>
    public required IReadOnlyList<double> AxisPoint1Mm { get; init; }

    /// <summary>Second pattern axis point, model coordinates, mm.</summary>
    public required IReadOnlyList<double> AxisPoint2Mm { get; init; }

    /// <summary>Instance count in the RADIAL direction (<c>Count1</c>). Not set — 1.</summary>
    public int Count1 { get; init; } = 1;

    /// <summary>Step in the RADIAL direction (<c>Step1</c>), mm. Effective when <c>Count1 &gt; 1</c>.</summary>
    public double Step1Mm { get; init; }

    /// <summary>Instance count in the CIRCULAR direction (<c>Count2</c>).</summary>
    public required int Count2 { get; init; }

    /// <summary>ANGULAR step in the CIRCULAR direction (<c>Step2</c>), DEGREES. The unit is named by the help page
    /// itself; the "degrees versus radians" discrepancy differs by a factor of 57.3 and is checked by a separate
    /// calibration probe.</summary>
    public required double Step2Deg { get; init; }

    /// <summary>Step along the axis (<c>StepByAxis</c>), mm.</summary>
    public double StepByAxisMm { get; init; }

    /// <summary>Interpretation of the step at the radial-direction boundary.</summary>
    public bool BoundaryInstancesStepFactor1 { get; init; }

    /// <summary>Interpretation of the step at the circular-direction boundary.</summary>
    public bool BoundaryInstancesStepFactor2 { get; init; }

    /// <summary>Pattern build direction (<c>ReverseDirection</c>).</summary>
    public bool ReverseDirection { get; init; }

    /// <summary>Instance orientation (<c>SaveInitialOrientation</c>). The member exists ONLY on the circular
    /// pattern: <c>ILinearPattern</c> lacks it in both the help and the interop assembly, so it does not carry
    /// over between families. Not set - <see cref="DefaultSaveInitialOrientation"/> applies.</summary>
    /// <remarks>DOC: <c>icircularpattern_saveinitialorientation.html</c> - «TRUE - сохранять исходную
    /// ориентацию, FALSE - доворачивать до радиального направления»; the page names no default.
    /// INVARIANT: the effective default is FALSE, declared in ONE place - the constant below.
    /// The property is nullable so "the client omitted the field" stays expressible and reportable.
    /// History: docs/decisions/contracts.md#circular-orientation-default</remarks>
    public bool? SaveInitialOrientation { get; init; }

    /// <summary>Orientation written when the client omits the field: FALSE - instances turn to the radial
    /// direction, the usual meaning of a circular pattern for holes, slots and teeth.</summary>
    public const bool DefaultSaveInitialOrientation = false;

    /// <summary>The orientation actually written to the feature: the client's value, else the default.</summary>
    public bool EffectiveSaveInitialOrientation => SaveInitialOrientation ?? DefaultSaveInitialOrientation;

    /// <summary>True when the client omitted the field and the default was applied instead.</summary>
    public bool SaveInitialOrientationDefaulted => SaveInitialOrientation is null;

    /// <summary>Build method: <c>save_all</c> (0), <c>chess_order_by_axis1</c> (1), <c>chess_order_by_axis2</c>
    /// (2).</summary>
    public string BuildingType { get; init; } = "save_all";

    /// <summary>Geometric copy. <c>true</c> is rejected before COM as an unmeasured mode.</summary>
    public bool GeometryPattern { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public int? ExpectedBodyCount { get; init; }

    /// <summary>Radius of the cylindrical instance face, mm (Ø10 → 5). See PatternGridCommand.</summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Height of the cylindrical instance face, mm.</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Analytic expectation of the hole-instance count.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>Analytic axis coordinates of the instances in model coordinates, mm.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Mirror pattern (docs/05 SM-23).</summary> <remarks>The plane is an explicit object, not the current window
/// selection: the reflection side is set by the plane itself, not by the order of the selection points. The YOZ normal
/// sign (pointing in −X) is recorded in the plane contract (<c>PlaneRefDto</c>), not hidden in reference resolution.
/// DOC: <c>copytype.html</c> publishes the type numbers — <c>o3d_mirrorOperation=48</c> («зеркальный массив»,
/// <c>IMirrorPattern</c>) and <c>o3d_mirrorAllOperation=49</c> («зеркально отразить все», the same
/// <c>IMirrorPattern</c>, additionally <c>IChooseBodies7</c>).
/// History: docs/decisions/contracts.md#pattern-mirror-route</remarks>
public sealed record PatternMirrorCommand
{
    public required string DocumentId { get; init; }

    public required PatternMirrorMode Mode { get; init; }

    /// <summary>Source objects. For <see cref="PatternMirrorMode.SelectedOperations"/> — <c>feature:</c> references and
    /// a non-empty list; for <see cref="PatternMirrorMode.AllBodies"/> — either empty ("all bodies") or explicit
    /// <c>body:</c> references.</summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>Symmetry plane: a <c>plane:</c> reference or a base plane through <c>base</c>.</summary>
    public required PlaneRefDto Plane { get; init; }

    /// <summary>Keep the source objects (<c>SaveInitialObjects</c>). <c>true</c> adds a reflected copy, leaving the
    /// source; <c>false</c> replaces the source with it.</summary> <remarks>DOC: the scope is bounded by page
    /// <c>imirrorpattern_saveinitialobjects.html</c> (the property works ONLY for <c>o3d_mirrorAllOperation</c>).
    /// MEASURED: on <c>o3d_mirrorAllOperation</c> a write of <c>false</c> reads back as <c>false</c> and the bodies
    /// really are replaced; on <c>o3d_mirrorOperation</c> it reads back as <c>true</c> and the geometry does not change.
    /// So the parameter is accepted and its non-acceptance is a route note.
    /// History: docs/decisions/contracts.md#pattern-mirror-save-initial</remarks>
    public required bool SaveInitialObjects { get; init; }

    /// <summary>Body action type for <c>IChooseBodies7.ChooseBodiesType</c>: <c>new_body</c> (0), <c>automatic</c> (1),
    /// <c>manual</c> (2), <c>all_bodies</c> (3). Effective only on <see cref="PatternMirrorMode.AllBodies"/>.</summary>
    public string ChooseBodiesType { get; init; } = "all_bodies";

    public double? ExpectedVolumeMm3 { get; init; }

    public int? ExpectedBodyCount { get; init; }

    /// <summary>Radius of the cylindrical instance face, mm.</summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Height of the cylindrical instance face, mm.</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Analytic expectation of the hole-instance count.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>Analytic axis coordinates of the instances in model coordinates, mm.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Re-read the parameters of a pattern or mirror by a feature reference.</summary>
public sealed record PatternReadCommand
{
    public required string FeatureRef { get; init; }
}

/// <summary>New parameters of an EXISTING pattern feature, passed in the <c>pattern</c> field.</summary> <remarks>A
/// pattern has no depth, no radius, no sketch: its members belong to THREE API7 interfaces (<c>ILinearPattern</c>,
/// <c>ICircularPattern</c>, <c>IMirrorPattern</c>), and some names coincide only in appearance. A flat set of fields on
/// <see cref="UpdateFeatureCommand"/> would read as "all this applies to any pattern". Axes and plane are non-editable:
/// they accept <c>IModelObject</c>, and no run measured changing the support of an existing pattern. An edit must carry
/// ALL mode parameters that should remain — the only way to tell "exactly the requested thing changed" from "this too".
/// History: docs/decisions/contracts.md#pattern-edit-members</remarks>
public sealed record PatternEditDto
{
    /// <summary>Instance count along the first direction. For grid — axis 1, for circular — RADIAL.</summary>
    public int? Count1 { get; init; }

    /// <summary>Instance count along the second direction. For circular — CIRCULAR.</summary>
    public int? Count2 { get; init; }

    /// <summary>Step along the first direction, mm (for circular — the radial step).</summary>
    public double? Step1Mm { get; init; }

    /// <summary>Step along the second direction of a GRID pattern, mm. Not applicable to circular.</summary>
    public double? Step2Mm { get; init; }

    /// <summary>ANGULAR step of the circular direction of a circular pattern, DEGREES. Not applicable to
    /// grid.</summary>
    public double? Step2Deg { get; init; }

    /// <summary>Tilt angle of the first grid axis, DEGREES. Not applicable to circular.</summary>
    public double? Angle1Deg { get; init; }

    /// <summary>Tilt angle of the second grid axis, DEGREES. Not applicable to circular.</summary>
    public double? Angle2Deg { get; init; }

    /// <summary>Copy direction along the first axis. Not applicable to circular.</summary>
    public bool? Direction1 { get; init; }

    /// <summary>Copy direction along the second axis. Not applicable to circular.</summary>
    public bool? Direction2 { get; init; }

    /// <summary>Building method. The contract words are the same as at creation: for grid <c>save_all</c>,
    /// <c>save_along_perimeter</c>, <c>save_along_axially</c>, <c>chess_order_by_axis1</c>,
    /// <c>chess_order_by_axis2</c>; for circular <c>save_all</c>, <c>chess_order_by_axis1</c>,
    /// <c>chess_order_by_axis2</c>. An unknown word is rejected before mutating rather than substituted with a
    /// default.</summary>
    public string? BuildingType { get; init; }

    /// <summary>Step along the axis of a circular pattern, mm. Not applicable to grid.</summary>
    public double? StepByAxisMm { get; init; }

    /// <summary>Build direction of a circular pattern. Not applicable to grid.</summary>
    public bool? ReverseDirection { get; init; }

    /// <summary>Instance orientation of a circular pattern. Not applicable to grid or mirror.</summary>
    public bool? SaveInitialOrientation { get; init; }

    /// <summary>Keep the source objects of a mirror pattern. Not applicable to grid or circular.</summary>
    public bool? SaveInitialObjects { get; init; }

    /// <summary>Analytic expectation of the document volume AFTER the edit, mm³.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>Analytic expectation of the body count AFTER the edit. Exact comparison.</summary>
    public int? ExpectedBodyCount { get; init; }
}

/// <remarks><see cref="AngleDeg"/> is what lies in the model as the first slot of the pair <c>Angle[true]</c>, and it
/// equals the requested angle up to a full turn (MEASURED, confirmed by a blind file read).
/// History: docs/decisions/contracts.md#rotation-angle-limit</remarks>
public sealed record RotatedDto(
    string? OperationType,
    double? AngleDeg,
    string? Direction,
    string? AxisState,
    int? ProfileInputCount);


/// <remarks><see cref="CountersinkDepthMm"/> is what the OBJECT returned, not what was written: in the "diameter +
/// angle" mode the depth is derived, and writing 2/4/6 into it changes nothing (M.3).</remarks>
public sealed record HoleDto(
    string? HoleType,
    double? DiameterMm,
    string? DepthType,
    double? DepthMm,
    string? EndFaceType,
    double? CounterboreDiameterMm,
    double? CounterboreDepthMm,
    double? CountersinkDiameterMm,
    double? CountersinkAngleDeg,
    double? CountersinkDepthMm,
    double[]? CenterMm);

/// <summary>One extrusion side, as returned by <c>GetSideParam</c>.</summary>
public sealed record FeatureSideDto(    bool Side,
    short EndConditionType,
    string EndCondition,
    double DepthMm,
    double DraftValue,
    bool DraftOutward);

/// <summary>Extrusion thin wall; <c>ReverseThicknessMm</c> is a member with a vendor typo in the getter.</summary>
public sealed record FeatureThinDto(
    bool Thin,
    short ThinType,
    double NormalThicknessMm,
    double ReverseThicknessMm);

/// <summary>Chamfer re-read from the model. Fields are null when the call did not provide them: "not read" and "zero"
/// are different answers, and they must not be mixed (the same standard as measure).</summary>
public sealed record ChamferDto(
    bool? Transfer,
    double? Distance1Mm,
    double? Distance2Mm,
    double? AngleDeg,
    string? BuildingType,
    bool? Direction,
    int? BaseObjectCount);

/// <summary>Fillet parameters. The radius is read from API7 (<c>IFillet.Radius1</c>): API5
/// <c>ksFilletDefinition.radius</c> is readable but NOT applied when written to an existing feature (MEASURED), so the
/// live model is authoritative; empty = "not read".</summary> <param name="BaseObjectReferences">Feature input
/// references — <c>IModelObject.Reference</c> of each <c>IFillet.BaseObjects</c> element. History:
/// docs/decisions/contracts.md#fillet-base-object-references</param> <param name="BaseObjectInputRefs">Registry
/// references to the feature's OWN inputs — <c>input:&lt;hex&gt;</c>, one per <c>IFillet.BaseObjects</c> element, in
/// the same order as <paramref name="BaseObjectReferences"/>; <c>null</c> — not read.</param>
public sealed record FilletDto(
    double? RadiusMm,
    double? Radius2Mm,
    bool? Tangent,
    string? BuildingType,
    int? BaseObjectCount,
    IReadOnlyList<int>? BaseObjectReferences = null,
    IReadOnlyList<string>? BaseObjectInputRefs = null);

/// <summary>Support of a B3 feature, read FROM THE MODEL: a point, a unit normal, and — separately — the three
/// construction points the normal is derived from.</summary> <remarks>The three points are published deliberately: the
/// normal is a DERIVED quantity (a cross product), and a reader is entitled to see what it was derived from rather than
/// take it on faith. MEASURED: the read returns exactly the three construction points and the derived normal, and it
/// distinguishes different supports.
/// History: docs/decisions/contracts.md#support-plane-read</remarks>
public sealed record SupportPlaneDto(
    IReadOnlyList<double> PointMm,
    IReadOnlyList<double> NormalMm,
    IReadOnlyList<double> Point1Mm,
    IReadOnlyList<double> Point2Mm,
    IReadOnlyList<double> Point3Mm);

/// <summary>Parameters of a B3 feature (boolean, split, cut, reposition), read FROM THE MODEL.</summary> <remarks>Only
/// fields of its own family are filled; for other families they are <c>null</c>. An empty field means "not read", not
/// zero, and the reason goes into <see cref="UnreadableParameters"/>. Of the five reposition fields ONE is not
/// published — <see cref="RepositionAxisPointMm"/>: an axis point has no documented member and is not a placement
/// property, so it is always <c>null</c>. The other four are read by the documented parametric route
/// (<c>ksEulerCorners</c> + <c>LocalCSParameters</c>; <c>ksPDisplace</c> + <c>Parameters</c>).
/// History: docs/decisions/contracts.md#solid-feature-reposition-read</remarks>
public sealed record SolidFeatureDto(
    /// <summary>Boolean operation kind: <c>union</c>, <c>difference</c> or <c>intersect</c>.</summary>
    string? Operation = null,
    /// <summary>Whether the tool is kept as a separate body (<c>IBoolean.SaveCopyModifyObjects</c>).</summary>
    bool? KeepTools = null,
    /// <summary>Support of the split or cut: point, normal and three construction points.</summary>
    SupportPlaneDto? Plane = null,
    /// <summary>Which side remained in the cut (<c>ICut.Direction</c>): true — the normal side.</summary>
    bool? KeepSide = null,
    /// <summary>Kind of the reposition: <c>translate</c> or <c>rotate</c>. Read from the placement parameters: a unit
    /// read rotation with a read translation is a translation, a non-unit one is a rotation (see remarks).</summary>
    string? RepositionKind = null,
    /// <summary>Translation vector, mm. Read for a translation (<c>ksPDisplace</c> + <c>IPoint3DParamDisplace</c>); for
    /// a rotation <c>null</c> and named not applicable — the rotation contract does not accept a vector.</summary>
    IReadOnlyList<double>? RepositionVectorMm = null,
    /// <summary>Point on the rotation axis, mm. ALWAYS null: for a rotation it is not readable (no documented member,
    /// and it is not a placement property), for a translation it is not applicable (see remarks).</summary>
    IReadOnlyList<double>? RepositionAxisPointMm = null,
    /// <summary>Unit direction of the rotation axis — read for a rotation; <c>null</c> for a translation.</summary>
    IReadOnlyList<double>? RepositionAxisDirectionMm = null,
    /// <summary>Rotation angle in degrees — read for a rotation; <c>null</c> for a translation.</summary>
    double? RepositionAngleDeg = null,
    /// <summary>Names of unread parameters with the MEASURED reason. A non-empty list is not a read failure but its
    /// boundary: the other fields are filled.</summary>
    IReadOnlyList<string>? UnreadableParameters = null);

/// <summary>What the server read from the definition of an existing feature (docs/05 §7 kompas_get_feature).</summary>
public sealed record FeatureReadDto(
    ReferenceDto FeatureRef,
    string Family,
    string DefinitionInterface,
    string Name,
    int EntityTypeName,
    int UpdateStamp,
    bool IsValid,
    bool Excluded,
    bool IsRollBacked,
    int ObjectError,
    short? DirectionType,
    IReadOnlyList<FeatureSideDto> Sides,
    FeatureThinDto? Thin,
    string? OwnerFeatureName,
    ChamferDto? Chamfer,
    VerificationDto Verification,
    /// <summary>Fillet parameters. Trailing so as not to shift positional arguments: filled only for <c>family =
    /// "fillet"</c>, null otherwise.</summary>
    FilletDto? Fillet = null,
    /// <summary>Native-hole parameters. Trailing: filled only for <c>family = "hole"</c>. Read entirely from API7
    /// (<c>IHole3D</c> + <c>HoleParameters</c> cast to its own mode), because in API5 a hole definition does not exist
    /// physically — MEASURED: the vendor wrapper declares no <c>ksHoleDefinition</c>.
    /// History: docs/decisions/contracts.md#hole-no-api5-definition</remarks>
    HoleDto? Hole = null,
    /// <summary>Rotation parameters. Trailing: filled only for <c>family = "rotation"</c>. Read from API7
    /// (<c>IRotated</c>), because rotation has no API5 definition — <c>entity.GetDefinition()</c> returns null
    /// (MEASURED).</summary>
    RotatedDto? Rotated = null,
    /// <summary>B3 feature parameters. Trailing: filled only for a <c>family</c> of {<c>boolean</c>, <c>split</c>,
    /// <c>cut_by_plane</c>, <c>reposition</c>}. Read from API7, because these families have no API5 definition —
    /// <c>entity.GetDefinition()</c> returns null — and they are recognized by the FEATURE NUMBER IN THE TREE (69 / 633
    /// / 50 / 79), MEASURED.
    /// History: docs/decisions/contracts.md#solid-feature-numbers</remarks>
    SolidFeatureDto? Solid = null,
    /// <summary>Sweep parameters. Trailing: filled only for <c>family = "sweep"</c>.</summary> <remarks>Read from the
    /// TREE, and the tree type number does not equal the creation number: a feature created by <c>NewEntity(45)</c>
    /// (<c>o3d_baseEvolution</c>) appears under number <b>46</b> (<c>o3d_bossEvolution</c>) and answers
    /// <c>ksBossEvolutionDefinition</c>, not <c>ksBaseEvolutionDefinition</c>. The family is recognized BY THE
    /// DEFINITION INTERFACE, not the creation number. <c>OperationResult</c> lives only in API7 (<c>IEvolution</c>); if
    /// unavailable the field stays null — "not read".
    /// History: docs/decisions/contracts.md#sweep-tree-number</remarks>
    SweepDto? Sweep = null,
    /// <summary>Loft parameters. Trailing: only for <c>family = "loft"</c>.</summary> <remarks>MEASURED: a feature
    /// created by the adapter route (<c>ILofts.Add(o3d_bossLoft = 31)</c>) appears in the tree under the same number
    /// <b>31</b> and answers <c>ksBossLoftDefinition</c>. The parameters are read from the <b>document collection</b>
    /// (<c>IModelContainer.Lofts</c> → <c>ILofts</c>), not the creation handle.
    /// History: docs/decisions/contracts.md#loft-tree-number</remarks>
    LoftDto? Loft = null,
    /// <summary>Shell parameters. Trailing: only for <c>family = "shell"</c>.</summary> <remarks>MEASURED: a feature
    /// created by <c>NewEntity(43)</c> appears under number <b>43</b> (<c>o3d_shellOperation</c>) and answers
    /// <c>ksShellDefinition</c>. From API7 (<c>IShells</c> → <c>IShell</c>) the same quantities appear in the other
    /// half: two independent halves of one setup.
    /// History: docs/decisions/contracts.md#shell-tree-number</remarks>
    ShellDto? Shell = null);

public sealed record GetFeatureCommand
{
    /// <summary>A <c>feature:</c> reference from kompas_list_features or a mutation result. Its revision is what is
    /// checked — a read has no separate expected_revision for the same reason as measure.</summary>
    public required string FeatureRef { get; init; }
}

/// <summary>Reading the parametric definiteness of an existing sketch (<see cref="WorkerCommands.SketchStatus"/>,
/// <c>kompas_get_sketch_status</c>).</summary> <remarks>A read has no separate <c>expected_revision</c> — for the same
/// reason as measure and get_feature: the revision the reference was minted for already lies in the registry and is
/// checked there. A silent "take the current one" here would be exactly what the contract forbids.</remarks>
public sealed record GetSketchStatusCommand
{
    /// <summary>A <c>sketch:</c> reference minted by this server (kompas_create_sketch or a feature read). The active
    /// document, the current selection and "the first sketch that comes up" are not used: addressing is explicit only,
    /// and the revision comes from the reference registry.</summary>
    public required string SketchRef { get; init; }
}

/// <summary>Normalized sketch-definiteness status — what the server actually read, not what it assumed.</summary>
/// <remarks>MEASURED mapping to native states: <c>ksStateWellConstrained</c> (1) → <see cref="FullyDefined"/>;
/// <c>ksStateUnderConstrained</c> (2) → <see cref="UnderDefined"/>; <c>ksStateUnknown</c> (0) → <see cref="Unknown"/>;
/// <c>ksStateUnresolvedRedundancy</c> (3) → <see cref="NeedsAttention"/> — declared, but live control was <b>not
/// obtained</b>, published conservatively. The API returns no degrees of freedom: <c>ISketch.ConstraintsState</c>
/// returns a status, not a counter, so <see cref="SketchStatusResult.DegreesOfFreedom"/> is always <c>null</c> and is
/// not derived.
/// History: docs/decisions/contracts.md#sketch-status-mapping</remarks>
public enum SketchDefinitionStatus
{
    /// <summary>State not established. <c>ksStateUnknown</c> (0) or an unknown enum value.</summary>
    Unknown,

    /// <summary>Fully defined: «+». <c>ksStateWellConstrained</c> (1).</summary>
    FullyDefined,

    /// <summary>Under-defined: «−». <c>ksStateUnderConstrained</c> (2).</summary>
    UnderDefined,

    /// <summary>Needs attention: «!». <c>ksStateUnresolvedRedundancy</c> (3), live control not obtained.</summary>
    NeedsAttention,
}

/// <summary>Snapshot of sketch definiteness. <see cref="IsFullyDefined"/> is nullable deliberately: <c>null</c> means
/// "not reliably established", and substituting <c>false</c> is forbidden.</summary> <param
/// name="DefinitionStatus">Normalized status.</param> <param
/// name="IsFullyDefined"><c>true</c>/<c>false</c>/<c>null</c>; <c>null</c> for Unknown/NeedsAttention.</param> <param
/// name="DegreesOfFreedom">Always <c>null</c>: the confirmed route returns a status, not a number, and it is not
/// substituted with zero.</param> <param name="Diagnostics">Understandable reasons: why the status is what it
/// is.</param> <param name="Limitations">Bounds of the answer's reliability, named explicitly.</param>
public sealed record SketchStatusResult(
    SketchDefinitionStatus DefinitionStatus,
    bool? IsFullyDefined,
    int? DegreesOfFreedom,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Limitations,
    /// <summary>Raw <c>ksConstraintsStateEnum</c> value as KOMPAS returned it. <c>null</c> — the call did not arrive.
    /// Kept separately from the normalized status so "unknown enum value" and "KOMPAS answered <c>ksStateUnknown</c>"
    /// do not merge into one <c>unknown</c>.</summary>
    int? RawState = null,
    /// <summary>State name as declared in the constants assembly. <c>null</c> for an unknown number.</summary>
    string? NativeStateName = null,
    /// <summary>Sketch name in the model — so a human can confirm that exactly that sketch was read.</summary>
    string? SketchName = null,
    /// <summary>By which route the object reached <c>ISketch</c>: "ISketch directly" or "IModelObject →
    /// ISketch".</summary>
    string? TransferRoute = null)
{
    /// <summary>Expand the normalized status from the raw enum value. A pure function — kept separate from COM so it
    /// can be tested without KOMPAS, and separate from <see cref="DegreesOfFreedom"/>, which the route does not have at
    /// all.</summary> <remarks>The value 3 normalizes to <see cref="SketchDefinitionStatus.NeedsAttention"/> only when
    /// <paramref name="redundancyVerified"/> is true. Until live control is obtained the caller MUST pass <c>false</c>,
    /// and the value 3 honestly stays <see cref="SketchDefinitionStatus.Unknown"/>: "declared in the enum" is not the
    /// same as "measured on a live model".</remarks>
    public static SketchStatusResult FromRawState(
        int? raw,
        bool redundancyVerified,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> limitations,
        string? sketchName = null,
        string? transferRoute = null)
    {
        if (raw is null)
        {
            return new SketchStatusResult(
                SketchDefinitionStatus.Unknown, null, null, diagnostics, limitations,
                RawState: null, NativeStateName: null, SketchName: sketchName, TransferRoute: transferRoute);
        }

        var (status, isFullyDefined, name, extraDiagnostics, extraLimitations) = raw switch
        {
            1 => (SketchDefinitionStatus.FullyDefined, (bool?)true, "ksStateWellConstrained",
                  Array.Empty<string>(),
                  Array.Empty<string>()),
            2 => (SketchDefinitionStatus.UnderDefined, (bool?)false, "ksStateUnderConstrained",
                  Array.Empty<string>(),
                  Array.Empty<string>()),
            0 => (SketchDefinitionStatus.Unknown, (bool?)null, "ksStateUnknown",
                  new[] { "КОМПАС ответил ksStateUnknown: состояние эскиза не установлено. Это ответ " +
                          "продукта, а не отказ сервера — в частности, так отвечает пустой эскиз." },
                  Array.Empty<string>()),
            3 when redundancyVerified =>
                 (SketchDefinitionStatus.NeedsAttention, (bool?)null, "ksStateUnresolvedRedundancy",
                  Array.Empty<string>(), Array.Empty<string>()),
            3 => (SketchDefinitionStatus.Unknown, (bool?)null, "ksStateUnresolvedRedundancy",
                  new[] { "КОМПАС вернул ksStateUnresolvedRedundancy (3) — «эскиз требует внимания», — " +
                          "но живой контроль этого состояния в подтверждённом прогоне не получен. " +
                          "Значение опубликовано консервативно как unknown, чтобы объявленное в " +
                          "перечислении состояние не выдавалось за измеренное." },
                  new[] { "unresolved_redundancy_not_verified" }),
            _ => (SketchDefinitionStatus.Unknown, (bool?)null, null,
                  new[] { "КОМПАС вернул значение ksConstraintsStateEnum, которого нет в объявленном " +
                          "перечислении (" + raw.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          "). Это не «недоопределён»: смысл значения неизвестен, ответ остаётся неизвестным." },
                  new[] { "unknown_enum_value" }),
        };

        return new SketchStatusResult(
            status,
            isFullyDefined,
            null,
            [.. diagnostics, .. extraDiagnostics],
            [.. limitations, .. extraLimitations],
            RawState: raw,
            NativeStateName: name,
            SketchName: sketchName,
            TransferRoute: transferRoute);
    }
}

/// <summary>Editing the parameters of a real feature in place (docs/05 §4.3: not "delete and create a similar one").
/// Extrusions are supported for now; the value is checked by re-reading from the new definition object, and the
/// geometry by measuring the volume.</summary>
public sealed record UpdateFeatureCommand
{
    public required string FeatureRef { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Depth for end_condition=blind. A side is expected, not "change everything at once".</summary>
    public double? DepthMm { get; init; }

    public ExtrudeEndCondition? EndCondition { get; init; }

    /// <summary>New section-motion mode of a sweep (family=<c>sweep</c>). A separate field, not a shared
    /// <c>direction</c>: for a chamfer <c>direction</c> is the chamfer side, for a shell the wall side, and one name
    /// for three different things would make the answer ambiguous.</summary> <remarks>MEASURED: changing the mode
    /// <c>orthogonal → parallel → orthogonal</c> on ONE feature changed the volume, and <c>sketchShiftType</c> read
    /// back 0 and 2. The setup distinguishes only on an ARC: on a straight path both modes give one body.
    /// History: docs/decisions/contracts.md#sweep-shift-mode</remarks>
    public SweepShiftMode? ShiftMode { get; init; }

    /// <summary>New set of loft sections (family=<c>loft</c>), in joining order.</summary> <remarks>MEASURED: rebinding
    /// sections on a built feature changes the geometry. The <c>closed</c> field is absent deliberately: writing
    /// <c>ILoft.Closed</c> on a built feature returns <c>Update() = True</c> but reads back <c>False</c> with the
    /// volume unchanged, so closure is set ONLY at creation (<c>kompas_loft.closed</c>).
    /// History: docs/decisions/contracts.md#loft-section-refs</remarks>
    public IReadOnlyList<string>? SectionRefs { get; init; }

    /// <summary>New set of coupling chains (family=<c>loft</c>) — a <b>full replacement</b>, as with <see
    /// cref="SectionRefs"/>.</summary> <remarks>A chain describes the point correspondence of a SPECIFIC section set,
    /// so after the section set is replaced the old chain would keep a foreign correspondence. If an edit changes the
    /// sections and does NOT name the chains, the call is rejected; an empty list means "no chains".
    /// DOC: the chain is set by <c>ILoft.AddCoupling()</c> → <c>ICoupling</c> → <c>PositionOffset(Index)</c>, replaced
    /// by <c>ClearCouplings()</c> + <c>AddCoupling()</c>.
    /// History: docs/decisions/contracts.md#loft-couplings-edit</remarks>
    public IReadOnlyList<LoftCoupling>? Couplings { get; init; }

    /// <summary>New shell wall thickness (family=<c>shell</c>), mm.</summary> <remarks>MEASURED: changing <c>t</c> on
    /// one feature changed the read-back volume. The edit carries BOTH mode parameters — thickness and direction —
    /// because otherwise "exactly the requested thing changed" is indistinguishable from "this changed too".
    /// History: docs/decisions/contracts.md#shell-thickness-edit</remarks>
    public double? ThicknessMm { get; init; }

    /// <summary>New shell wall direction (family=<c>shell</c>): <c>true</c> — inward, <c>false</c> — outward. A
    /// separate field, not the chamfer's shared <c>direction</c>.</summary>
    public bool? ThinInward { get; init; }

    /// <summary>New SET of removed shell faces (family=<c>shell</c>) — a full replacement: what is passed is what
    /// should remain removed. Faces come from <c>kompas_read_topology</c>, not collection positions.</summary>
    /// <remarks>MEASURED: changing the removed-face set changes the read-back volume and face count, and re-writing the
    /// same set does not move the volume. A separate route, not a carry-over from the fillet: <c>Clear()</c> +
    /// <c>Add()</c> over <c>ksEntityCollection</c> measurably did NOT work for the FILLET. An empty list is rejected
    /// too: the operation is accepted (<c>Create/Update = true</c>) but the body does not change. History:
    /// docs/decisions/contracts.md#shell-face-refs</remarks>
    public IReadOnlyList<string>? FaceRefs { get; init; }

    /// <summary>Change the support sketch of the same feature (support edit per docs/05 §4.3). Needed for modes where
    /// there is no depth parameter: in a through cut the number is ignored by the solver, so "change the parameter"
    /// there is possible only by changing the profile.</summary>
    public string? SketchRef { get; init; }

    /// <summary>New first chamfer leg (mm). family=<c>chamfer</c>; not applied to extrusions. MEASURED:
    /// <c>SetChamferParam</c> on a 2×2→3×3 feature changes the volume by exactly 20·(d₂²−d₁²)·… and the value is
    /// re-read from the new definition object.</summary>
    public double? Distance1Mm { get; init; }

    /// <summary>New second chamfer leg (mm).</summary>
    public double? Distance2Mm { get; init; }

    /// <summary>New chamfer angle in degrees; editable only through the API7 route.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>New chamfer side (API5 transfer / API7 Direction).</summary>
    public bool? Direction { get; init; }

    /// <summary>New ROTATION angle (family=<c>rotation</c>), degrees. A separate field, not the shared <see
    /// cref="AngleDeg"/> (the chamfer angle): a call with <see cref="AngleDeg"/> on a rotation feature is rejected with
    /// INVALID_ARGUMENT pointing at this field.</summary> <remarks>MEASURED: changing the angle on ONE feature gave a
    /// geometric change. The order "write the angle to <c>IRotated.Angle[true]</c> → <c>IRotated.Update()</c> →
    /// <c>RebuildModel</c>" is part of the contract. Angle only: changing the profile and axis is NOT done by this
    /// call.
    /// History: docs/decisions/contracts.md#rotation-angle-edit</remarks>
    public double? RotationAngleDeg { get; init; }

    /// <summary>New rotation direction (family=<c>rotation</c>). <c>reverse</c> is rejected before mutating: MEASURED
    /// (R.26.sector) that it builds nothing.</summary>
    public RotationDirection? RotationDirection { get; init; }

    /// <summary>Kind of transformation when editing a REPOSITION feature (family=<c>reposition</c>).</summary>
    /// <remarks>An edit must change the parameters of an EXISTING feature relative to its ORIGINAL inputs, not to the
    /// current placement — a repeated <c>kompas_reposition</c> call creates a SECOND feature and shifts the body from
    /// the current one. MEASURED: re-writing the same vector leaves the bbox unchanged and returning it to zero brings
    /// the body home — the parameter is applied to the original body. Here <c>angle_deg</c> is the CHAMFER angle, so
    /// the rotation angle is <see cref="RepositionAngleDeg"/>.
    /// History: docs/decisions/contracts.md#reposition-kind-edit</remarks>
    public RepositionKind? RepositionKind { get; init; }

    /// <summary>New translation vector, mm, model coordinates. Mandatory for <c>translate</c>.</summary>
    public IReadOnlyList<double>? RepositionVectorMm { get; init; }

    /// <summary>New point on the rotation axis, mm. Mandatory for <c>rotate</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisPointMm { get; init; }

    /// <summary>New rotation axis direction. Mutually exclusive with <c>reposition_axis_point2_mm</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisDirectionMm { get; init; }

    /// <summary>New second rotation axis point. Mutually exclusive with <c>reposition_axis_direction_mm</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisPoint2Mm { get; init; }

    /// <summary>New rotation angle, degrees. Mandatory for <c>rotate</c>.</summary>
    public double? RepositionAngleDeg { get; init; }

    /// <summary>Analytic expectation of the body bbox after the edit.</summary> <remarks>Needed where volume cannot
    /// serve as the expectation. Under a rigid transformation volume is an INVARIANT, so <see
    /// cref="ExpectedVolumeMm3"/> for the <c>reposition</c> family confirms only that the transformation stayed rigid,
    /// not that the body went where it was asked: for a translation "moved" and "stayed" are indistinguishable by
    /// volume. The bbox distinguishes, and the same requirement is in acceptance. Not by reading the parameter back:
    /// <c>IBodyReposition.Position.X/Y/Z</c> does NOT carry the translation (MEASURED), so the check moved to geometry.
    /// History: docs/decisions/contracts.md#reposition-position-member</remarks>
    public BoundingBoxDto? ExpectedBboxMm { get; init; }

    /// <summary>New support of a SPLIT (<c>family=split</c>) or CUT (<c>family=cut_by_plane</c>) feature.</summary>
    /// <remarks>An edit must change the parameters of an EXISTING feature relative to its ORIGINAL inputs — a repeated
    /// <c>kompas_split_body</c> creates a SECOND split feature and cuts the already obtained part. The shape is the
    /// same as the like-named field of <c>kompas_split_body</c>/<c>kompas_cut_body</c>: <c>plane_ref</c> or
    /// <c>point_mm</c> + <c>normal_mm</c>; the side is <c>s = n·(p − p₀)</c>. MEASURED: the route moves the
    /// CONSTRUCTION POINTS of the feature's OWN support; a SECOND plane does not.
    /// History: docs/decisions/contracts.md#split-support-route</remarks>

    public CutPlaneDto? Plane { get; init; }

    /// <summary>New kept side for <c>family=cut_by_plane</c>: <c>positive</c> — <c>s &gt; 0</c>, <c>negative</c> — <c>s
    /// &lt; 0</c>.</summary> <remarks>MEASURED: <c>ICut.Direction = true</c> keeps the side along the normal (<c>s &gt;
    /// 0</c>), <c>false</c> the opposite one (<c>s &lt; 0</c>). When editing the side the feature is the same: the edit
    /// changes the REMAINDER, not the body count.</remarks>
    public string? KeepSide { get; init; }

    /// <summary>Analytic expectation of the PART volumes after a split edit (<c>family=split</c>).</summary>
    /// <remarks>The only quantity that confirms a split edit. The SUM of part volumes does not change, so volume
    /// preservation is an invariant, not a confirmation — a row checking only the sum would pass on complete inaction.
    /// Only the PER-VOLUME composition distinguishes, matched by volume equality WITHIN TOLERANCE (0.01 mm³ abs. / 1e-6
    /// rel.) with multiplicity: each expected volume gets its own body, order does not matter. On mismatch the call
    /// returns <c>NO_GEOMETRY_CHANGE</c> with <c>partial_effects=true</c> and the composition in <c>details</c>.
    /// History: docs/decisions/contracts.md#split-part-volumes</remarks>
    public IReadOnlyList<double>? ExpectedPartVolumesMm3 { get; init; }

    /// <summary>New KIND of an existing boolean operation (family=<c>boolean</c>): <c>union</c>, <c>difference</c> or
    /// <c>intersect</c>.</summary> <remarks>MEASURED: re-writing <c>IBoolean.BooleanType</c> on an EXISTING feature
    /// followed by <c>Update()</c> and a rebuild changes the geometry. The control is mandatory: on the reference the
    /// difference and intersection volumes are EQUAL, so the distinguishing case compares the bbox while the volume
    /// stays. Support bodies are NOT changed; <c>ksBooleanUnknown</c> (0) is normalized to <c>ksUnion</c>; the field
    /// name coincides with the <c>operation</c> parameter of <c>kompas_boolean</c>.
    /// History: docs/decisions/contracts.md#boolean-edit-kind</remarks>
    public BooleanOperation? Operation { get; init; }

    /// <summary>New fillet radius (mm); family=<c>fillet</c>. Editable only through the API7 route: in API5
    /// <c>ksFilletDefinition</c> has a radius, but writing to it on an existing feature is NOT applied — MEASURED: the
    /// setter returns success while <c>entity.Update()</c> + <c>RebuildDocument()</c> leave the volume unchanged (see
    /// <c>Api5Session.UpdateFilletRadius</c>). The radius is written to <c>IFillet.Radius1</c> on the live model, and
    /// the API5 feature is matched to it by RADIUS EQUALITY, not by name (a name set in API5 reads differently in
    /// API7).
    /// History: docs/decisions/contracts.md#fillet-radius-edit</remarks>
    public double? RadiusMm { get; init; }

    /// <summary>New SET of fillet edges (family=<c>fillet</c>) — a full replacement.</summary> <remarks>The edges come
    /// from <c>kompas_read_topology</c>, not collection positions. DOC: the route is API7 —
    /// <c>IModelContainer.Fillets[i] → IFillet</c>, read/write of <c>IFillet.BaseObjects</c> (full replacement), then
    /// the mandatory <c>IFillet.Update()</c>; objects come from the LIVE model. INVARIANT: the negative result on the
    /// API5 route holds — <c>ksFilletDefinition.array()</c> → <c>Clear()</c> → <c>Add()</c> → <c>entity.Update()</c>
    /// does NOT edit the set on an existing feature (<c>edges_read_back=0</c>).
    /// History: docs/decisions/contracts.md#fillet-edge-set-history</remarks>
    public IReadOnlyList<string>? EdgeRefs { get; init; }

    /// <summary>New set by the feature's OWN inputs (family=<c>fillet</c>) — the second currency.</summary>
    /// <remarks>MEASURED: shrinking a set and replacing its composition are different operations with different
    /// currencies — shrinkage is expressed only by objects read FROM the feature's <c>BaseObjects</c>, while <see
    /// cref="EdgeRefs"/> takes BODY edges. A feature's own input is an <c>IModelObject</c> from
    /// <c>IFillet.BaseObjects</c>, addressed by <c>IModelObject.Reference</c> (the same number in
    /// <c>fillet.base_object_references</c>); it has NO registry <c>edge:&lt;hex&gt;</c> string. The form is
    /// <c>input:&lt;hex Reference&gt;</c>, a FULL replacement.
    /// History: docs/decisions/contracts.md#fillet-base-object-references</remarks>
    public IReadOnlyList<string>? BaseObjectRefs { get; init; }

    /// <summary>Analytic expectation of the volume after the edit (G03: 100·80·12 = 96000 mm³).</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>Body the feature's scope is directed at when editing (family=<c>cut_by_plane</c>).</summary>
    /// <remarks>The scope must be assignable and RESTORABLE at create/edit/rebuild/save-reopen. If the feature ended up
    /// in the "All objects" default (help page <c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>), moving the support
    /// would remove material from unrelated bodies, with nothing to fix it but deletion and rebuilding. An absent field
    /// is not "all the same": if not set, the adapter MUST READ the scope of the live feature before mutation and
    /// refuse if it is not addressed (<c>ChooseType ≠ ksChBodies</c> or an empty body list).
    /// History: docs/decisions/contracts.md#cut-area-scope-edit</remarks>
    public string? TargetBodyRef { get; init; }

    /// <summary>New parameters of a PATTERN feature (family=<c>pattern</c>: SM-18/19/23).</summary>
    /// <remarks>The <c>pattern</c> family is the only one whose edit subject belongs to THREE API7 interfaces at once,
    /// and some names coincide only in appearance — a flat <c>count2</c> would read as "applicable to any pattern". The
    /// feature is identified BY THE FIELD ITSELF, not the tree type number: it is matched to an element of
    /// <c>IModelContainer.FeaturePatterns</c> by the same instrument as reading (<see cref="PatternReadCommand"/>). If
    /// the field is not set, no branch is chosen; mixing with other families is rejected. History:
    /// docs/decisions/contracts.md#pattern-edit-routing</remarks>
    public PatternEditDto? Pattern { get; init; }

    /// <summary>New hole diameter (family=<c>hole</c>), mm: the pilot of a counterbore/countersink.</summary>
    /// <remarks>DOC: the existing hole is taken by <c>IHoles3D.Hole3D[index]</c>, its own mode's members are written,
    /// then <c>IModelObject.Update()</c> and a rebuild. INVARIANT: the address is not guessed — the "tree feature ↔
    /// <c>Holes3D</c> record" correspondence is proved by the uniqueness of the hole; with several holes the call is
    /// rejected <c>CAPABILITY_UNAVAILABLE</c> before COM. The mode is NOT changed by an edit: it is read
    /// (<c>IHole3D.HoleType</c>) and serves as the frame — a foreign-mode field is rejected <c>INVALID_ARGUMENT</c>
    /// before COM.
    /// History: docs/decisions/contracts.md#hole-edit-address</remarks>
    public double? DiameterMm { get; init; }

    /// <summary>New counterbore relief diameter (mode <c>through_counterbore</c>), mm. A field of a FOREIGN mode for
    /// the rest: on a blind hole and on a countersink it is rejected <c>INVALID_ARGUMENT</c>.</summary>
    /// <remarks>MEASURED: a relief edit changed the removed volume by the ring difference π/4·(D²−d²)·h. It is written
    /// to <c>ISpotfacingHoleParameters.SpotfacingDiameter</c>; on a foreign mode this interface is UNREACHABLE on the
    /// object.
    /// History: docs/decisions/contracts.md#hole-counterbore-edit</remarks>
    public double? CounterboreDiameterMm { get; init; }

    /// <summary>New counterbore relief depth (mode <c>through_counterbore</c>), mm.</summary>
    public double? CounterboreDepthMm { get; init; }

    /// <summary>New countersink MOUTH diameter (mode <c>through_countersink</c>), mm — the mouth, not the pilot: the
    /// pilot is set by <see cref="DiameterMm"/>.</summary> <remarks>MEASURED: a mouth edit changed the removed volume
    /// by the difference <c>π·h/3·(rM² + rP·rM − 2·rP²)</c> with the derivative <c>h = (rM − rP)/tan(angle/2)</c>.
    /// History: docs/decisions/contracts.md#hole-countersink-edit</remarks>
    public double? CountersinkDiameterMm { get; init; }

    /// <summary>New countersink angle (mode <c>through_countersink</c>), degrees.</summary>
    public double? CountersinkAngleDeg { get; init; }

    /// <summary>Analytic expectation of the material REMOVED by the edit, mm³: <c>volume_before −
    /// volume_after</c>.</summary> <remarks>The sign is part of the definition: a hole removes material, so "became
    /// deeper" is positive and "became shallower" negative; comparing the magnitude is not allowed. The delta
    /// distinguishes "exactly what was requested applied" from "something else applied". <see
    /// cref="ExpectedVolumeMm3"/> is also accepted. Without a declared expectation the level stays
    /// <c>call_returned</c>, and <c>expected_volume_delta_not_supplied</c> appears in <c>unverified_aspects</c>.
    /// History: docs/decisions/contracts.md#hole-volume-delta</remarks>
    public double? ExpectedVolumeDeltaMm3 { get; init; }
}

/// <summary>Suppress or restore a feature. Route MEASURED: <c>ksFeature.excluded = true</c> removes the extrusion body
/// down to the plate volume, <c>false</c> restores it; the feature count does not change.</summary>
public sealed record SuppressFeatureCommand
{
    public required string FeatureRef { get; init; }

    /// <summary>true — suppress, false — restore.</summary>
    public required bool Suppressed { get; init; }

    /// <summary>Analytic expectation of the volume after suppression (or restore). Without it only the feature state is
    /// confirmed, and the result is honestly marked as unproven geometry.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Feature deletion. Dependents are listed BEFORE calling KOMPAS; neither API5 nor API7 gives a reliable list,
/// so the tree returns the candidates after the deleted feature, and their presence requires <see
/// cref="ConfirmDependents"/>.</summary>
public sealed record DeleteFeatureCommand
{
    public required string FeatureRef { get; init; }

    /// <summary>Explicit consent to delete a feature that has others after it in the tree.</summary>
    public bool ConfirmDependents { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Body in the result of operation B3: reference, volume, bounding box and a multi-piece flag.</summary>
/// <remarks>A separate record, not <c>BodyRowDto</c>: acceptance B3 is counted by VOLUMES, and <c>BodyRowDto</c>
/// carries no volume. <c>MultiBodyParts</c> is no decoration — the core represents a result of several pieces as ONE
/// body with several pieces (MEASURED), and without it "one body" would read as "material intact". <c>VolumeMm3</c> is
/// <c>null</c> when unread: "not read" and "zero" differ.</remarks>
public sealed record SolidBodyDto
{
    public required string BodyRef { get; init; }

    /// <summary><c>solid</c> or <c>sheet</c>.</summary>
    public required string Kind { get; init; }

    public double? VolumeMm3 { get; init; }

    public required BoundingBoxDto Bbox { get; init; }

    public required int FaceCount { get; init; }

    /// <summary>The body consists of several disconnected pieces. This is exactly how the core represents a broken-up
    /// result, and it is exactly this field that distinguishes "one body of two pieces" from "one body
    /// intact".</summary>
    public required bool MultiBodyParts { get; init; }
}

/// <summary>Plane for operations B3: an existing support, a point + normal, or a base plane + offset.</summary>
/// <remarks>The three ways are mutually exclusive, checked before the COM call. The plane side is NOT set here: it is
/// set by the sign <c>s = n·(p − p₀)</c> in the cut command itself. The normal must be non-zero and finite; point and
/// normal are in MODEL coordinates, mm. INVARIANT: the field shape matches the PUBLISHED <c>cut_plane</c> schema
/// literally — MEASURED: a divergence by one nesting level made the declared <c>CAPABILITY_UNAVAILABLE</c> unreachable
/// and the call failed parsing (<c>JsonException</c> at <c>$.plane.base</c>, <c>VERIFICATION_FAILED</c>).
/// History: docs/decisions/contracts.md#cut-plane-shape</remarks>
public sealed record CutPlaneDto
{
    /// <summary>Reference to an existing document plane.</summary>
    public string? PlaneRef { get; init; }

    /// <summary>Point the plane passes through, mm, model coordinates.</summary>
    public IReadOnlyList<double>? PointMm { get; init; }

    /// <summary>Plane normal, dimensionless, model coordinates. Non-zero and finite.</summary>
    public IReadOnlyList<double>? NormalMm { get; init; }

    /// <summary> Base plane (<c>xy</c> | <c>xz</c> | <c>yz</c>) — a DECLARED but NOT SUPPORTED way: the API7
    /// auxiliary-plane-with-offset route is not measured, and a call with this field refuses
    /// <c>CAPABILITY_UNAVAILABLE</c> before any mutation. </summary>
    public PlaneBase? Base { get; init; }

    /// <summary>Offset along the base plane normal, mm. Meaningful only together with <see cref="Base"/>.</summary>
    public double? OffsetMm { get; init; }
}

/// <summary>Shape verdict for a plane: which outcome it commits to, BEFORE any work with the model.
public enum CutPlaneFormVerdict
{
    /// <summary>Shape admissible: further checks are up to the route (reference, finiteness, normal).</summary>
    Ok,

    /// <summary>More than one way was named. A contradictory request — <c>INVALID_ARGUMENT</c>.</summary>
    ModesConflict,

    /// <summary><c>base</c> was named — a declared and NOT supported way: <c>CAPABILITY_UNAVAILABLE</c>.</summary>
    BaseUnsupported,

    /// <summary><c>offset_mm</c> without <c>base</c> — the offset does not express a plane:
    /// <c>INVALID_ARGUMENT</c>.</summary>
    OffsetWithoutBase,
}

/// <summary>The shape rule of <see cref="CutPlaneDto"/> — in one place and without COM.</summary> <remarks>The priority
/// is declared and does not depend on the order of fields in JSON: <see cref="CutPlaneFormVerdict.ModesConflict"/> —
/// <c>base</c> together with <c>plane_ref</c> or a point with a normal, checked FIRST; <see
/// cref="CutPlaneFormVerdict.BaseUnsupported"/> — <c>base</c> alone; <see
/// cref="CutPlaneFormVerdict.OffsetWithoutBase"/> — an offset without a base plane; <see
/// cref="CutPlaneFormVerdict.Ok"/> — either <c>plane_ref</c> or a point with a normal. The mutual exclusion
/// "<c>plane_ref</c> vs a point with a normal" is NOT checked here: the route names it.
/// History: docs/decisions/contracts.md#cut-plane-form-rule</remarks>
public static class CutPlaneForm
{
    public static CutPlaneFormVerdict Validate(CutPlaneDto plane)
    {
        var namesPointOrNormal = plane.PointMm is not null || plane.NormalMm is not null;
        var namesReference = plane.PlaneRef is { Length: > 0 };

        if (plane.Base is not null)
        {
            return namesReference || namesPointOrNormal
                ? CutPlaneFormVerdict.ModesConflict
                : CutPlaneFormVerdict.BaseUnsupported;
        }

        if (plane.OffsetMm is not null)
        {
            return CutPlaneFormVerdict.OffsetWithoutBase;
        }

        return CutPlaneFormVerdict.Ok;
    }
}

/// <summary>Boolean operation kind. The numbers correspond to <c>Kompas6Constants.ksBooleanType</c>.</summary>
public enum BooleanOperation
{
    Union = 3,
    Difference = 2,
    Intersect = 1,
}

/// <summary>Boolean operation on bodies: explicit target, explicit tool list, operation kind and tool-keeping policy
/// (docs/05 SM-15).</summary> <remarks>Addressing is by references only: neither "the current window", nor "index 0",
/// nor the collection order is a target — the declared body is searched among the BodyCollection elements by IUnknown,
/// and on no match the call is refused rather than a position substituted. INVARIANT: the checks the core does NOT make
/// live in the contract — the target is not part of the tool set; the set has no repeats; the list is non-empty. The
/// core accepts a repeated reference silently (MEASURED).</remarks>
public sealed record BooleanCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Target body. Difference is computed as "target minus tools".</summary>
    public required string TargetBodyRef { get; init; }

    /// <summary>Tool bodies. A non-empty list without repeats and without the target.</summary>
    public required IReadOnlyList<string> ToolBodyRefs { get; init; }

    public required BooleanOperation Operation { get; init; }

    /// <summary>Keep the tools as separate bodies in their former place (<c>IBoolean.SaveCopyModifyObjects</c>). A copy
    /// of the target is not supported: that is a separate mode <c>SM-15.union.mode_save_base_copy</c> with priority
    /// <c>next</c>, outside the mandatory scope.</summary>
    public bool KeepTools { get; init; }

    /// <summary>Analytic expectation of the result volume, when the caller can derive it.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Splitting a body into parts by a plane. All resulting parts are kept — this is the measured behaviour of
/// <c>ISplitSolid</c>, not a chosen policy.</summary>
public sealed record SplitCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required CutPlaneDto Plane { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Cutting a body on one side of a plane (docs/05 SM-16). <remarks>The kept side is set by the sign <c>s =
/// n·(p − p₀)</c>. MEASURED correspondence: the side "in the direction of the normal" (<c>s &gt; 0</c>) is
/// <c>ICut.Direction = true</c>.</remarks>
public sealed record CutByPlaneCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required CutPlaneDto Plane { get; init; }

    /// <summary>Kept side: <c>positive</c> — <c>s &gt; 0</c>, <c>negative</c> — <c>s &lt; 0</c>.</summary>
    public required string KeepSide { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Body position transform kind.</summary>
public enum RepositionKind
{
    Translate,
    Rotate,
}

/// <summary> Translation and rotation of a body (docs/05 SM-17). Both kinds are one feature <c>IBodyReposition</c>, and
/// both are written by a homogeneous 4×4 matrix: only it writes the position. </summary> <remarks>The rotation
/// axis is given by a point and a direction, or by two distinct points — in model coordinates, mm. A degenerate axis
/// (coincident points, zero direction) is refused before COM. The angle is in degrees, the sign by the right-hand rule
/// around the axis direction.</remarks>
public sealed record RepositionCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required RepositionKind Kind { get; init; }

    /// <summary>Translation vector, mm, model coordinates. Mandatory for <c>translate</c>.</summary>
    public IReadOnlyList<double>? VectorMm { get; init; }

    /// <summary>Point on the rotation axis, mm. Mandatory for <c>rotate</c>.</summary>
    public IReadOnlyList<double>? AxisPointMm { get; init; }

    /// <summary>Rotation axis direction. Mutually exclusive with <c>AxisPoint2Mm</c>.</summary>
    public IReadOnlyList<double>? AxisDirectionMm { get; init; }

    /// <summary>Second point of the rotation axis. Mutually exclusive with <c>AxisDirectionMm</c>.</summary>
    public IReadOnlyList<double>? AxisPoint2Mm { get; init; }

    /// <summary>Rotation angle in degrees. Mandatory for <c>rotate</c>.</summary>
    public double? AngleDeg { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Actual structure of a boolean result. There are no promises about the body count: the core represents a
/// result of several pieces as ONE body with several pieces (MEASURED: V = 18 000, <c>MultiBodyParts = true</c>, 12
/// faces for two pieces of 6).</summary>
public sealed record BooleanResultDto
{
    public required string FeatureRef { get; init; }

    public required string Operation { get; init; }

    public required bool KeepTools { get; init; }

    /// <summary>Bodies after the operation — the actual composition, not the expected one.</summary>
    public required IReadOnlyList<SolidBodyDto> ResultBodies { get; init; }

    /// <summary>Tools kept as separate bodies (an empty list if the policy consumed them).</summary>
    public required IReadOnlyList<SolidBodyDto> SavedTools { get; init; }

    /// <summary>Bodies the operation consumed.</summary>
    public required IReadOnlyList<string> ConsumedInputs { get; init; }

    /// <summary>Sum of the volumes of all document bodies after the operation.</summary>
    public required double TotalVolumeMm3 { get; init; }

    /// <summary>The sum of the individual volumes and the volume of the spatial union are different quantities and must
    /// not be mixed. Here it is exactly the sum over bodies.</summary>
    public string? VolumeNote { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

/// <summary>Split result: the full list of parts, each with its own reference.</summary>
public sealed record SplitResultDto
{
    public required string FeatureRef { get; init; }

    /// <summary>All parts produced by the split, with references and volumes.</summary>
    public required IReadOnlyList<SolidBodyDto> Parts { get; init; }

    /// <summary>Document bodies that did not take part in the operation — proof of their non-involvement.</summary>
    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required double PartsVolumeSumMm3 { get; init; }

    /// <summary>Plane normal in model coordinates — the one that actually applied.</summary>
    public IReadOnlyList<double>? PlaneNormalMm { get; init; }

    public IReadOnlyList<double>? PlanePointMm { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

/// <summary>Cut result: what remained and what was removed, named explicitly.</summary>
public sealed record CutByPlaneResultDto
{
    public required string FeatureRef { get; init; }

    /// <summary>The remaining body.</summary>
    public required SolidBodyDto Remaining { get; init; }

    /// <summary>The side named by the sign: <c>positive</c> — <c>s &gt; 0</c>.</summary>
    public required string KeptSide { get; init; }

    public required IReadOnlyList<double> PlaneNormalMm { get; init; }

    public required IReadOnlyList<double> PlanePointMm { get; init; }

    /// <summary>Document bodies that did not take part in the operation.</summary>
    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }

    /// <summary> The checks the verdict rests on: addressing (material removed from the NAMED body) and the integrity
    /// of unrelated bodies. They are published separately from <see cref="UnverifiedAspects"/>, because an empty
    /// limitation list with a vanished unrelated body would read as "fully verified" — this is how a client acceptance
    /// once got a false <c>geometry_checked</c> (defect <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>). </summary>
    public IReadOnlyList<NamedCheck>? Checks { get; init; }
}

/// <summary>Reposition result: the position before and after, the volume and the body count.</summary>
public sealed record RepositionResultDto
{
    public required string FeatureRef { get; init; }

    public required string Kind { get; init; }

    public required BoundingBoxDto BboxBefore { get; init; }

    public required BoundingBoxDto BboxAfter { get; init; }

    public required double VolumeMm3 { get; init; }

    /// <summary>Document body count before and after — a position transform neither creates nor consumes
    /// bodies.</summary>
    public required int BodiesBefore { get; init; }

    public required int BodiesAfter { get; init; }

    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

public sealed record ExportStepCommand
{
    public required string DocumentId { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>Revision the caller read; a mismatch aborts before the converter runs.</summary>
    public required long ExpectedRevision { get; init; }
}

public sealed record ImportStepCommand
{
    public required string ApplicationId { get; init; }

    public required string InputPath { get; init; }

    public string DesiredKind { get; init; } = "auto";

    public string? TargetPath { get; init; }

    /// <summary>P0 diagnostic mode: log every return value, null and interface type observed around the import instead
    /// of throwing at the first null. Used only by the probe.</summary>
    public bool TraceLifecycle { get; init; }
}

/// <summary>Raster snapshot of the current session's model by the documented API5 route:
/// <c>ksDocument3D.RasterFormatParam()</c> → <c>ksRasterFormatParam</c> →
/// <c>ksDocument3D.SaveAsToRasterFormat(fileName, rasterPar)</c>.</summary> <remarks>MEASURED: the two route modes are
/// MUTUALLY EXCLUSIVE — with a NON-EMPTY file name the core writes the file and <c>resultArrayBytes</c> stays null;
/// with an EMPTY file name the core returns <c>System.Byte[]</c> (PNG magic) and does NOT create a file. So "return the
/// picture" and "write the file" are two different route calls. Fields not confirmed by the probe are not part of the
/// contract.
/// History: docs/decisions/contracts.md#export-image-route</remarks>
public sealed record ExportImageCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Revision read by the caller; a mismatch gives REVISION_CONFLICT before COM.</summary>
    public required long ExpectedRevision { get; init; }

    /// <summary>Format name on the wire: png, jpg, bmp or tif.</summary>
    public required string Format { get; init; }

    /// <summary>Value of <c>extResolution</c>. Not set — the member is NOT written and the core default applies
    /// (MEASURED). The response limits apply to the result in any case: an excess is refused, not squeezed.</summary>
    public int? Resolution { get; init; }

    /// <summary>Value of <c>extScale</c>. Not set — the member is not written.</summary>
    public double? Scale { get; init; }

    /// <summary>Where to put the file. Not set — no file is created (byte mode).</summary>
    public string? SavePath { get; init; }

    /// <summary>Whether to return the picture in the response. Default — yes.</summary>
    public bool ReturnImageContent { get; init; } = true;

    /// <summary>Projection to show in the snapshot, by published name (front, rear, up, down, left, right,
    /// isometric). Not set — the current view is captured and left untouched. Set — applied before the
    /// render and RESTORED after it, unless <see cref="KeepView"/> says otherwise.</summary>
    /// <remarks>MEASURED: switching the projection does NOT mark the document changed
    /// (<c>IKompasDocument.Changed</c> stays false). In a VISIBLE window before the first <c>SetCurrent</c>
    /// no projection answers <c>IsCurrent=true</c>, so a call without <c>keep_view=true</c> refuses BEFORE
    /// changing the view. History: docs/decisions/contracts.md#view-projection</remarks>
    public string? View { get; init; }

    /// <summary>Keep the requested projection after the snapshot instead of restoring the previous one.
    /// Default — false (restore). Only meaningful together with <see cref="View"/>. In a visible window
    /// whose current projection cannot be read, <c>keep_view=true</c> is REQUIRED: there is nothing to
    /// restore, and the call refuses rather than move the window silently.</summary>
    public bool KeepView { get; init; }
}

/// <summary>Raster snapshot result. The dimensions are READ FROM THE HEADER, not taken from the request: the parameter
/// says what was asked for, the header says what came out.</summary>
public sealed record ExportImageResultDto
{
    public required string Format { get; init; }

    public required string MimeType { get; init; }

    /// <summary>Dimension from the header; null — not read (JPG and TIF dimensions are not parsed).</summary>
    public int? PixelWidth { get; init; }

    public int? PixelHeight { get; init; }

    /// <summary>Artifact size in bytes — MEASURED, not derived from the request.</summary>
    public required long BytesCount { get; init; }

    /// <summary>Path of the written file; null — no file was requested.</summary>
    public string? SavePath { get; init; }

    /// <summary>By which route mode the artifact was obtained: <c>memory</c> (empty file name, bytes in the response)
    /// or <c>file</c> (non-empty name, file on disk). Named because these are different core calls.</summary>
    public required string RasterRoute { get; init; }

    /// <summary>The file was written FROM THE SAME bytes that came back in the response (one render, not
    /// two).</summary>
    public bool? SavePathFromMemory { get; init; }

    /// <summary>View state: which projection was requested and what the collection reported back.</summary>
    public required string ViewNote { get; init; }

    /// <summary>Projection the CALLER asked for by name; null — the current view was captured.</summary>
    public string? RequestedView { get; init; }

    /// <summary>Projection SWITCHED TO, read back from the kernel after the change; null — not applied
    /// (no view requested, or the document carries no entry of that type). This is a measurement, not the
    /// echo of the request: the field names the type the collection reported as current.</summary>
    public string? AppliedView { get; init; }

    /// <summary>Projection the document showed BEFORE the change, read back by type; null — not read.
    /// MEASURED: in a visible window before the first <c>SetCurrent</c> this is always null, and a call
    /// asking for a switch WITHOUT <c>keep_view=true</c> is refused before the view changes, so a null
    /// here never means "the window was moved anyway".</summary>
    public string? PreviousView { get; init; }

    /// <summary>Whether the previous projection was restored after the snapshot.</summary>
    public bool? ViewRestored { get; init; }

    /// <summary>Value of the collection's <c>viewProjectionScheme</c> — the document's orientation
    /// scheme, read alongside the projection type.</summary>
    public long? ViewProjectionScheme { get; init; }

    /// <summary>base64 of the picture. The Host moves it into an image block and REMOVES it from the
    /// structure.</summary>
    public string? ImageBase64 { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }
}


/// <summary>Deliberate unit experiment (spec 1.10 / G08): draw known geometry, read it back every way.</summary>
public sealed record UnitProbeCommand
{
    public required string ApplicationId { get; init; }

    public required double KnownLengthMm { get; init; }

    public required double KnownRadiusMm { get; init; }
}

public sealed record UnitProbeResult
{
    public required double KnownLengthMm { get; init; }

    public required double KnownRadiusMm { get; init; }

    /// <summary>Raw, unconverted readings keyed by API call. Conversions live in the adapter.</summary>
    public required IReadOnlyDictionary<string, double?> RawReadings { get; init; }

    public required IReadOnlyDictionary<string, string?> Observations { get; init; }

    public required IReadOnlyList<string> ConfirmedScales { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }
}

// The assembly-domain contracts live in AssemblyCommands.cs (order C1).
// History: docs/decisions/contracts.md#assembly-domain-contracts
/// <summary>Feature row returned to the Host (spec 2.5).</summary>
public sealed record FeatureRowDto
{
    public required string FeatureRef { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public required bool State { get; init; }

    public string? PersistentId { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>Reference to the sketch this feature is built on, when it is an extrusion and the sketch can be read
    /// back through <c>GetSketch()</c>. Null for feature families without a sketch and when the read-back
    /// fails.</summary> <remarks>This closes the long-standing <c>sketch_reference_not_resolved</c> gap: before it, a
    /// sketch that arrived with a reopened document could be neither named nor edited, because nothing handed the
    /// caller a reference to it. It is a plain reference minted against the current revision, so the usual staleness
    /// rules apply unchanged (it dies on the next rebuild like any other handle).
    /// History: docs/decisions/contracts.md#sketch-ref-readback</remarks>
    public string? SketchRef { get; init; }
}

/// <summary>Body row returned to the Host.</summary>
public sealed record BodyRowDto
{
    public required string BodyRef { get; init; }

    public required string Kind { get; init; }

    public required BoundingBoxDto Bbox { get; init; }

    public required int FaceCount { get; init; }

    public required int EdgeCount { get; init; }
}

public sealed record FaceRowDto
{
    public required string FaceRef { get; init; }

    public required string SurfaceType { get; init; }

    public required double AreaMm2 { get; init; }

    public IReadOnlyList<double>? CenterMm { get; init; }

    public IReadOnlyList<double>? NormalAtCenter { get; init; }

    /// <summary>Radius of a cylindrical face, mm; null for non-cylindrical ones.</summary>
    public double? RadiusMm { get; init; }

    /// <summary>Extent of a cylindrical face along the axis, mm; not the operation depth.</summary>
    public double? HeightMm { get; init; }

    /// <summary>Axis direction of a cylindrical face (from placement, not from GetAxis).</summary>
    public IReadOnlyList<double>? AxisMm { get; init; }

    public required bool NormalAmbiguous { get; init; }
}

public sealed record EdgeRowDto
{
    public required string EdgeRef { get; init; }

    public required string CurveType { get; init; }

    public required double LengthMm { get; init; }

    public IReadOnlyList<IReadOnlyList<double>>? VerticesMm { get; init; }

    /// <summary>Circle/arc parameters when analytically recovered, mm.</summary>
    public double? RadiusMm { get; init; }

    public IReadOnlyList<double>? CenterMm { get; init; }
}

/// <summary>Where the Worker put a file and what it verified about it.</summary>
public sealed record ExportResultDto
{
    public required string OutputPath { get; init; }

    public required long ByteLength { get; init; }

    public required string Sha256 { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }

    public required long SourceRevision { get; init; }
}

/// <summary>What an import actually created — the question the historical attempt could not answer.</summary>
public sealed record ImportResultDto
{
    public required IReadOnlyList<string> CreatedDocumentIds { get; init; }

    public required IReadOnlyList<DocumentKind> CreatedDocumentKinds { get; init; }

    public required int BodyCount { get; init; }

    public required int ComponentCount { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }

    /// <summary>Step-by-step record of returns/nulls when <see cref="ImportStepCommand.TraceLifecycle"/> is
    /// set.</summary>
    public IReadOnlyList<string> Trace { get; init; } = Array.Empty<string>();
}

/// <summary>Sweep: a flat closed profile is carried along a continuous path (docs/05 SM-04).</summary>
/// <remarks>INVARIANT: the document comes from the PROFILE, not a separate field, so a reference from a foreign part
/// cannot enter the mutation. DOC: API5 page <c>ksbaseevolutiondefinition.html</c> gives the composition. MEASURED: the
/// path holder is cast to <c>ksEntityCollection</c> and <c>Add(sketch)</c> returns <c>True</c>; a path break is a
/// <b>refusal</b>. The orthogonality mode must be checked ON AN ARC: on a straight path <see
/// cref="SweepShiftMode.Parallel"/> and <see cref="SweepShiftMode.Orthogonal"/> give one body, on an R50/90° arc they
/// differ.
/// History: docs/decisions/contracts.md#sweep-route-b5</remarks>
public sealed record SweepCommand
{
    /// <summary>Profile sketch: an explicit <c>sketch:</c> reference. It also sets the document.</summary>
    public required string SketchRef { get; init; }

    /// <summary>Path sketch: an explicit <c>sketch:</c> reference. It must lie in the SAME part.</summary>
    public required string PathRef { get; init; }

    /// <summary> Kind of section motion along the path. Default <see cref="SweepShiftMode.Orthogonal"/>: this is the
    /// documented behaviour «плоскость образующей ортогональна направляющей», and it is what gives <c>S × L</c>.
    /// </summary>
    public SweepShiftMode ShiftMode { get; init; } = SweepShiftMode.Orthogonal;

    /// <summary>Analytic expectation of the volume AFTER the operation. Without it only the read-back of the parameters
    /// is confirmed, and the result is honestly marked as unproven geometry.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Loft: the body is built on an ordered set of sections (docs/05 SM-05).</summary> <remarks>DOC: the route is
/// API7 — the mandatory row <c>SM-05.base.mode_couplings</c> requires section correspondence chains, and API5 has none.
/// Sections are set by <c>ILoft.Sketchs</c> of type <c>VARIANT</c> («массив <c>SAFEARRAY</c> объектов <c>LPDISPATCH</c>»).
/// MEASURED: <c>AddCoupling()</c> returns <c>KompasAPI7.CouplingClass</c>. The volume is the truncated-pyramid formula
/// <c>V = h/3 · (A₁ + A₂ + √(A₁A₂))</c>, not "average area × height".
/// History: docs/decisions/contracts.md#loft-route-b5</remarks>
public sealed record LoftCommand
{
    /// <summary>Part document: a section set has no single "support" object, unlike a profile.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Sections IN THE ORDER of connection. At least two: a body is not built from a single section. Sketches,
    /// contours, spatial curves and faces — the composition is declared by the SDK help
    /// (<c>iloft_propers.html</c>).</summary>
    public required IReadOnlyList<string> SectionRefs { get; init; }

    /// <summary>Build method at the extreme sections — <c>ILoft.BuildingType(BeginSection)</c>. The values are
    /// documented by the page <c>ksloftbuildingtype.html</c>; <c>Auto</c> is confirmed by measurement
    /// (<c>BuildingType(true) = 0</c> and <c>BuildingType(false) = 0</c> for a just-created feature, i.e. <c>ksLoftAuto
    /// = 0</c>).</summary>
    public LoftBuilding Building { get; init; } = LoftBuilding.Auto;

    /// <summary> Close the path — <c>ILoft.Closed</c>. The write and the read-back are MEASURED (written
    /// <c>false</c>, read <c>False</c>). The difference from a "closed shell": here it is the <b>path joining the
    /// sections</b> that closes, not the body. </summary>
    public bool Closed { get; init; }

    /// <summary>Section correspondence chains — what makes the mandatory row <c>SM-05.base.mode_couplings</c> different
    /// from "just a body through sections".</summary>
/// <remarks>DOC: <c>iloft_addcoupling.html</c> — <c>AddCoupling()</c> → <c>ICoupling</c>; <c>icoupling_count.html</c> —
/// <c>Count</c> is «Количество сечений в цепочке». MEASURED: an explicit correspondence <b>replaces</b> the automatic
/// one; the offset is in mm (not a fraction). The chain is set BEFORE the first <c>Update()</c>.
/// History: docs/decisions/contracts.md#loft-couplings-create</remarks>
    public IReadOnlyList<LoftCoupling> Couplings { get; init; } = Array.Empty<LoftCoupling>();

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary> Section correspondence chain: one offset per EACH section, in the order of <see
/// cref="LoftCommand.SectionRefs"/>. The number of offsets must match the number of sections — checked by
/// <c>ICoupling.Count</c> («количество сечений в цепочке»). </summary>
public sealed record LoftCoupling
{
    /// <summary> Offsets along the section contours, mm — <c>ICoupling.PositionOffset(Index)</c>, where <c>Index</c> is
    /// the section index in the chain. Zero means the start of the contour and reproduces the automatic correspondence.
    /// </summary>
    public required IReadOnlyList<double> OffsetsMm { get; init; }
}

/// <summary>Shell: a cavity of the given thickness is subtracted from the body, optionally with faces removed (docs/05
/// SM-13).</summary> <remarks>MEASURED on BOTH APIs: an empty face list does NOT give a closed shell, so an empty face
/// list is <b>refused before COM</b>. DOC: <c>ksshelldefinition_thintype.html</c> — «<c>TRUE</c> — внутрь, <c>FALSE</c>
/// — наружу» for API5. INVARIANT: the half types differ — API7 <c>ThinType</c> is <c>long</c> (interop
/// <c>ksDirectionTypeEnum</c>), API5 <c>thinType</c> is <c>bool</c>.
/// History: docs/decisions/contracts.md#shell-route-b5</remarks>
public sealed record ShellCommand
{
    public required string DocumentId { get; init; }

    /// <summary> Faces removed before the wall is formed — <c>face:</c> references. The list must be <b>non-empty</b>:
    /// an empty one does not give a closed shell (MEASURED on both APIs), and such a call is refused with a named
    /// refusal before touching COM. </summary>
    public IReadOnlyList<string> FaceRefs { get; init; } = Array.Empty<string>();

    /// <summary>Wall thickness, mm. Strictly greater than 0.</summary>
    public required double ThicknessMm { get; init; }

    /// <summary>Wall formation direction. The correspondence is MEASURED, see the type description.</summary>
    public ShellThinDirection ThinDirection { get; init; } = ShellThinDirection.Inward;

    /// <summary>Tangent faces — <c>IShell.SetFaces(Faces, TangentFaces)</c> in API7.</summary> <remarks>The parameter
    /// is <b>declared</b> because an undeclared parameter is invisible to the product: <c>additionalProperties:
    /// false</c> would reject the call before COM, reading as "not supported". But <c>true</c> is <b>refused with a
    /// named refusal</b> rather than silently ignored: API5 <c>ksShellDefinition</c> has no "tangent faces" member, and
    /// this tool's route is API5. The mode <c>SM-13.shell.mode_tangent_faces</c> is outside the mandatory scope, so the
    /// bound is named.
    /// History: docs/decisions/contracts.md#shell-tangent-faces</remarks>
    public bool TangentFaces { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Sweep result.</summary>
public sealed record SweepResult(
    ReferenceDto? FeatureRef,
    string ShiftMode,
    int SectionCount,
    int PathPartCount,
    double? PathLengthMm,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Loft result.</summary>
public sealed record LoftResult(
    ReferenceDto? FeatureRef,
    string Building,
    bool Closed,
    int SectionCount,
    int? CouplingCount,
    IReadOnlyList<LoftCouplingDto>? Couplings,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Shell result.</summary>
public sealed record ShellResult(
    ReferenceDto? FeatureRef,
    double ThicknessMm,
    string ThinDirection,
    int RemovedFaceCount,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Sweep parameters read from the model. All fields are nullable: an empty field means "NOT READ", not
/// zero.</summary>
public sealed record SweepDto(
    string? ShiftMode,
    int? SectionCount,
    int? PathPartCount,
    double? PathLengthMm);

/// <summary> A correspondence chain read <b>FROM THE MODEL</b> — not a retelling of the request. An element of <see
/// cref="OffsetsMm"/> is <c>null</c> if the offset on that section could not be read: "not read" differs from zero.
/// <see cref="SectionCount"/> is <c>ICoupling.Count</c>, «количество сечений в цепочке»; <c>null</c> means the chain
/// size was not read either. </summary>
public sealed record LoftCouplingDto(int? SectionCount, IReadOnlyList<double?> OffsetsMm);

/// <summary>Loft parameters read from the model.</summary> <remarks><see cref="SectionRefs"/> — SECTIONS AS REFERENCES,
/// derived from the feature definition (<c>ksBaseLoftDefinition.Sketches()</c> /
/// <c>ksBossLoftDefinition.Sketches()</c>, returning <c>ksEntityCollection</c>), not saved since creation. A loft edit
/// changes ONLY the section set (<c>kompas_update_feature</c>, field <c>section_refs</c>); <c>kompas_list_features</c>
/// returns only shaping elements, so two sketches and one loft show ONE row. Without this field, editing an existing
/// loft is inexpressible after <c>save → close → reopen</c>.
/// History: docs/decisions/contracts.md#loft-read-section-refs</remarks>
public sealed record LoftDto(
    string? Building,
    bool? Closed,
    int? SectionCount,
    int? CouplingsCount,
    IReadOnlyList<LoftCouplingDto>? Couplings = null,
    IReadOnlyList<string>? SectionRefs = null);

/// <summary>Shell parameters read from the model.</summary> <remarks><see cref="RemovedFaceRefs"/> — REMOVED FACES AS
/// REFERENCES, derived from the feature definition (<c>ksShellDefinition.FaceArray()</c> → <c>ksEntityCollection</c>),
/// like <see cref="LoftDto.SectionRefs"/>: the set of removed faces is the edit input, and without a reference to the
/// faces removed BY THE FEATURE ITSELF a repeated set edit after a mutation is inexpressible. Such faces are ABSENT
/// from the body topology, so <c>kompas_read_topology</c> has nothing to take them from.
/// History: docs/decisions/contracts.md#shell-read-face-refs</remarks>
public sealed record ShellDto(
    double? ThicknessMm,
    string? ThinDirection,
    int? RemovedFaceCount,
    IReadOnlyList<string>? RemovedFaceRefs = null);
