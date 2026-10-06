namespace KompasMcp.Contracts;

/// <summary>Drawing-domain contracts — block DRW (profile <c>drawings-minimal-v1</c>, modes
/// <c>DRW-01…DRW-06</c>).</summary>
/// <remarks>INVARIANT: an association view is addressed by the source MODEL FILE and a projection name from
/// the document's own projection list; the request carries no geometry, so the response is read back from
/// the drawing rather than echoing the request. INVARIANT: a drawing dimension attaches to view POINTS in
/// view coordinates, not to model topology — associativity is NOT claimed
/// (<c>iassociationview_projectionname.html</c> documents projection names, not a point binding).
/// History: docs/decisions/drawings.md#contracts</remarks>

/// <summary>Create a group of standard associative views of a model.</summary>
public sealed record CreateDrawingViewsCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Absolute path to the saved source model inside the allowed root (checked by the Host).
    /// INVARIANT: the model must exist on disk — a standard view is a projection OF A FILE, and an
    /// in-memory unsaved model has no file to project.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Model orientation name from the source document's projection list
    /// (<c>iassociationview_projectionname.html</c>: «имя проекции (из списка проекций в
    /// документе-источнике)»). Null means the documented KOMPAS default ("Спереди"), not a guess.</summary>
    public string? ProjectionName { get; init; }

    /// <summary>View types to build, from the documented <c>ProjectionType</c> enum
    /// (<c>projectiontype.html</c>): <c>front</c>=vp_Front, <c>top</c>=vp_Up, <c>left</c>=vp_Left,
    /// <c>right</c>=vp_Right, <c>rear</c>=vp_Rear, <c>bottom</c>=vp_Down, <c>isometric</c>=vp_IsoXYZ,
    /// <c>dimetric</c>=vp_Dio. Empty means "the document default set".</summary>
    public IReadOnlyList<string> Projections { get; init; } = Array.Empty<string>();

    /// <summary>Anchor point of the view group, in drawing mm.</summary>
    public double X { get; init; }

    public double Y { get; init; }

    /// <summary>View scale; null means the documented KOMPAS default (1:1), not a server guess.</summary>
    public double? Scale { get; init; }

    /// <summary>Gap between views along X and Y, in drawing mm. Null means the KOMPAS default.</summary>
    public double? DX { get; init; }

    public double? DY { get; init; }
}

/// <summary>Enumerate the views of a drawing.</summary>
public sealed record ListDrawingViewsCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One drawing view, read back from the document.</summary>
public sealed record DrawingViewRowDto
{
    /// <summary>Opaque reference to the view, bound to the revision.</summary>
    public required string ViewRef { get; init; }

    /// <summary>View number in the document (<c>IView.Number</c>).</summary>
    public int? Number { get; init; }

    public string? Name { get; init; }

    /// <summary>View type name (<c>IView.ViewType</c>, <c>LtViewType</c>).</summary>
    public string? ViewType { get; init; }

    public double? Scale { get; init; }

    /// <summary>Anchor X in drawing mm (<c>IView.X</c>).</summary>
    public double? X { get; init; }

    /// <summary>Anchor Y in drawing mm (<c>IView.Y</c>).</summary>
    public double? Y { get; init; }

    /// <summary>Source model file of an associative view (<c>IAssociationView.SourceFileName</c>).</summary>
    public string? SourcePath { get; init; }

    /// <summary>Projection name of an associative view (<c>IAssociationView.ProjectionName</c>).</summary>
    public string? ProjectionName { get; init; }

    /// <summary>Whether hidden lines are shown (<c>IAssociationView.HiddenLinesVisible</c>).</summary>
    public bool? HiddenLinesVisible { get; init; }

    /// <summary>Whether centre lines are shown (<c>IAssociationView.CenterLinesVisible</c>).</summary>
    public bool? CenterLinesVisible { get; init; }

    /// <summary>Number of objects in the view (<c>IView.ObjectCount</c>).</summary>
    public int? ObjectCount { get; init; }

    /// <summary>Gabarit of the view (min/max in drawing mm), when the documented route yields it.</summary>
    public IReadOnlyList<double>? GabaritMin { get; init; }

    public IReadOnlyList<double>? GabaritMax { get; init; }
}

/// <summary>Answer to a drawing-view enumeration.</summary>
public sealed record ListDrawingViewsResult(
    IReadOnlyList<DrawingViewRowDto> Views,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of creating standard views: re-read from the drawing, not a retelling of the request.</summary>
public sealed record CreateDrawingViewsResult(
    IReadOnlyList<DrawingViewRowDto> Views,
    int CreatedCount,
    VerificationDto Verification);

/// <summary>Change an EXISTING view through the documented writable properties.</summary>
/// <remarks>DOC: <c>iview_scale.html</c> — <c>Scale</c> is read/write and «вступает в силу после вызова
/// метода IDrawingObject::Update»; <c>iview_x.html</c>/<c>iview_y.html</c> — the anchor point follows the
/// same update route. INVARIANT: this edits the object the caller already has, and the response is the
/// SAME view re-read afterwards — adding another view is not an edit.
/// History: docs/decisions/drawings.md#view-edit</remarks>
public sealed record EditViewCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Reference to the view to change (from <c>kompas_list_drawing_views</c>).</summary>
    public required string ViewRef { get; init; }

    /// <summary>New view scale. Null means "leave the scale alone" — not a server-invented default.</summary>
    public double? Scale { get; init; }

    /// <summary>New anchor X in drawing mm. Null means "leave X alone".</summary>
    public double? X { get; init; }

    /// <summary>New anchor Y in drawing mm. Null means "leave Y alone".</summary>
    public double? Y { get; init; }
}

/// <summary>Result of editing a view: the same view re-read, plus the before/after comparison.</summary>
public sealed record EditViewResult(
    DrawingViewRowDto Before,
    DrawingViewRowDto After,
    VerificationDto Verification);

/// <summary>Add a dimension to a view.</summary>
public sealed record AddDimensionCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Reference to the target view (from <c>kompas_list_drawing_views</c>).</summary>
    public required string ViewRef { get; init; }

    /// <summary>Dimension kind: <c>linear</c>, <c>diametral</c>, <c>radial</c> — the three collections
    /// documented for the container (<c>isymbols2dcontainer_props.html</c>).</summary>
    public required string DimensionType { get; init; }

    /// <summary>First attachment point (linear). For diametral/radial — the centre of the measured arc.</summary>
    public required IReadOnlyList<double> Point1 { get; init; }

    /// <summary>Second attachment point (linear only). Diametral/radial dimensions ignore it.</summary>
    public IReadOnlyList<double>? Point2 { get; init; }

    /// <summary>Dimension-line position: linear — X3/Y3, radial/diametral — the shelf point.</summary>
    public IReadOnlyList<double>? Position { get; init; }

    /// <summary>Measured value to set: linear — the dimension line offset is derived from the points;
    /// radial/diametral — the radius. Null means "let the kernel measure from the points".</summary>
    public double? ValueMm { get; init; }
}

/// <summary>A dimension read back from the drawing.</summary>
public sealed record DimensionRowDto
{
    /// <summary>Opaque reference to the dimension, bound to the revision.</summary>
    public required string DimensionRef { get; init; }

    /// <summary>linear | diametral | radial.</summary>
    public required string Kind { get; init; }

    /// <summary>Value read back from the dimension object: linear — the distance between the attachment
    /// points; radial/diametral — the radius.</summary>
    public double? ValueMm { get; init; }

    /// <summary>Colour of the dimension line, if read.</summary>
    public IReadOnlyList<double>? Point1 { get; init; }

    public IReadOnlyList<double>? Point2 { get; init; }

    /// <summary>Whether the dimension reports itself valid (<c>Valid</c>).</summary>
    public bool? Valid { get; init; }
}

/// <summary>Result of adding a dimension: the dimension re-read, plus a verification.</summary>
public sealed record AddDimensionResult(
    DimensionRowDto Dimension,
    VerificationDto Verification);

/// <summary>Fill the cells of the drawing's title block (основная надпись).</summary>
public sealed record SetTitleBlockCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Cell values keyed by the DOCUMENTED cell identifier (<c>IStamp.Text(Id)</c>). The mapping
    /// from a GOST 2.104 notion ("наименование", "обозначение", …) to a numeric cell id is NOT documented
    /// in the SDK reference — the caller supplies the id from the drawing's own stamp layout. History:
    /// docs/decisions/drawings.md#stamp-cells</summary>
    public IReadOnlyDictionary<string, string> Cells { get; init; } = new Dictionary<string, string>();
}

/// <summary>One title-block cell, re-read after <c>IStamp.Update()</c>.</summary>
public sealed record TitleBlockCellDto(
    string CellId,
    string? Requested,
    string? ReadBack,
    bool Matched);

/// <summary>Result of filling the title block: cells re-read, not echoed.</summary>
public sealed record SetTitleBlockResult(
    IReadOnlyList<TitleBlockCellDto> Cells,
    VerificationDto Verification);

/// <summary>Export a drawing to a documented interchange format.</summary>
public sealed record ExportDrawingCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Absolute path for the exported file inside the allowed export root.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Target format: <c>dxf</c> or <c>dwg</c> — the only two the SDK reference names for the
    /// converter (<c>iconverter_getfilter.html</c>: FORMAT_DXF=1, FORMAT_DWG=2). PDF is NOT a documented
    /// programmatic route and is refused by name. History: docs/decisions/drawings.md#export-formats</summary>
    public required string Format { get; init; }

    /// <summary>Overwrite an existing file. Default false: rewriting without an explicit flag is a silent
    /// data loss.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Result of exporting a drawing: the file confirmed by existence, size and signature.</summary>
public sealed record ExportDrawingResult(
    string OutputPath,
    string Format,
    long ByteLength,
    string Signature,
    VerificationDto Verification);

/// <summary>Write the drawing's technical requirements (технические требования) and apply them.</summary>
/// <remarks>DOC: <c>itechnicaldemand_text.html</c> — <c>IDrawingDocument.TechnicalDemand</c> →
/// <c>ITechnicalDemand.Text</c> returns the <c>IText</c> of the block («доступно только для чтения»
/// applies to the property, the text itself is written through <c>IText.Str</c>);
/// <c>itechnicaldemand_update.html</c> — <c>Update()</c> «применить заданные параметры технических
/// требований». The block is created on write, so <c>IsCreated</c> is read back as evidence that the
/// text landed. History: docs/decisions/drawings.md#technical-demand</remarks>
public sealed record SetTechnicalDemandCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Full text of the technical requirements block. Lines are separated by newlines.</summary>
    public required string Text { get; init; }
}

/// <summary>Result of writing the technical requirements: the text re-read after Update().</summary>
public sealed record SetTechnicalDemandResult(
    string? ReadBack,
    bool? IsCreated,
    VerificationDto Verification);
