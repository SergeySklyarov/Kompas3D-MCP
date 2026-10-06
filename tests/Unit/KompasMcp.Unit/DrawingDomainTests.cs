using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Drawing domain (block DRW): the contract of the drawing tools and the honesty of their
/// descriptions.</summary>
/// <remarks>TEST: only what is checkable WITHOUT KOMPAS — registration, worker-command mapping, the mandatory
/// fields of mutations, schema strictness, the nullability that keeps "not read" distinct from "read zero",
/// and the descriptions that NAME the current limits (dimensions bind to view POINTS not model topology,
/// PDF is not a documented programmatic route, stamp cell ids are not documented, the view gabarit is not
/// published). The "the route works" check is NOT included and not substituted: it needs a run on the target
/// version. History: docs/decisions/drawings.md#tools</remarks>
public class DrawingDomainTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static readonly (string Name, string Command)[] Domain =
    {
        ("kompas_create_drawing_views", "drawing.create_views"),
        ("kompas_list_drawing_views", "drawing.list_views"),
        ("kompas_edit_view", "drawing.edit_view"),
        ("kompas_rebuild_drawing_views", "drawing.rebuild_views"),
        ("kompas_add_dimension", "drawing.add_dimension"),
        ("kompas_list_dimensions", "drawing.list_dimensions"),
        ("kompas_set_title_block", "drawing.set_title_block"),
        ("kompas_get_title_block", "drawing.get_title_block"),
        ("kompas_set_technical_demand", "drawing.set_technical_demand"),
        ("kompas_get_technical_demand", "drawing.get_technical_demand"),
        ("kompas_export_drawing", "drawing.export"),
    };

    public static TheoryData<string> AllTools { get; } = new(Domain.Select(d => d.Name));

    [Theory]
    [MemberData(nameof(AllTools))]
    public void DomainTool_IsRegisteredWithItsOwnWorkerCommand(string name)
    {
        var tool = Tool(name);
        Assert.Equal(Domain.Single(d => d.Name == name).Command, tool.WorkerCommand);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }

    [Theory]
    [InlineData("kompas_create_drawing_views")]
    [InlineData("kompas_add_dimension")]
    [InlineData("kompas_set_title_block")]
    [InlineData("kompas_edit_view")]
    [InlineData("kompas_set_technical_demand")]
    public void DrawingMutation_DeclaresOperationIdAndExpectedRevision(string name)
    {
        var tool = Tool(name);
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        var required = ((JsonArray)tool.InputSchema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.True(tool.IsMutation);
        Assert.Contains("operation_id", properties.Select(p => p.Key));
        Assert.Contains("operation_id", required);
        Assert.Contains("expected_revision", required);
        Assert.Contains("document_id", required);
    }

    [Fact]
    public void ListDrawingViews_IsNotAMutation()
    {
        var tool = Tool("kompas_list_drawing_views");
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.True(tool.Behaviour.RequiresDocument);
    }

    /// <summary>The read-only siblings of the two setters must NOT be mutations.</summary>
    /// <remarks>TEST: a setter cannot witness persistence — it assigns the expected value and reads it
    /// back, so it passes even on a document that lost the value at reopen. The read routes exist to be
    /// an independent witness, therefore they must carry no expected_revision and no operation_id, and
    /// must not be declared destructive. History: docs/decisions/drawings.md#stamp-read</remarks>
    [Theory]
    [InlineData("kompas_get_title_block")]
    [InlineData("kompas_get_technical_demand")]
    public void DrawingReadRoute_IsNotAMutation(string name)
    {
        var tool = Tool(name);
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.False(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresDocument);
        // A read must NOT demand a revision: it writes nothing, so a stale revision must not refuse it.
        Assert.DoesNotContain("expected_revision", properties.Select(p => p.Key));
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void DrawingSchema_IsStrictAndRequiresDocument(string name)
    {
        var schema = Tool(name).InputSchema;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("document_id", required);
    }

    [Fact]
    public void CreateViews_DescriptionNamesTheSourceFileBoundary()
    {
        // INVARIANT: the client must see that the PROJECTION is bound to the source FILE, and that an
        // unsaved model is a named refusal — not only in the code.
        var description = Tool("kompas_create_drawing_views").Description;
        Assert.Contains("AddStandartViews", description, StringComparison.Ordinal);
        Assert.Contains("DOCUMENT_NOT_FOUND", description, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDimension_DescriptionNamesTheAssociativityBoundary()
    {
        // INVARIANT: associativity of the dimension to MODEL topology is NOT claimed — the help documents
        // points, not a binding. The word must be present as a boundary, not as a promise.
        var description = Tool("kompas_add_dimension").Description;
        Assert.Contains("ТОЧКАМ", description, StringComparison.Ordinal);
        Assert.Contains("НЕ ЗАЯВЛЯЕТСЯ", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportDrawing_DescriptionNamesPdfAsUndocumentedByName()
    {
        var description = Tool("kompas_export_drawing").Description;
        Assert.Contains("dxf", description, StringComparison.Ordinal);
        Assert.Contains("dwg", description, StringComparison.Ordinal);
        Assert.Contains("PDF", description, StringComparison.Ordinal);
        Assert.Contains("FORMAT_UNAVAILABLE", description, StringComparison.Ordinal);
    }

    [Fact]
    public void SetTitleBlock_DescriptionNamesTheUndocumentedCellIds()
    {
        var description = Tool("kompas_set_title_block").Description;
        Assert.Contains("IStamp", description, StringComparison.Ordinal);
        Assert.Contains("НЕ документирует", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportDrawing_KeepsOverwriteOptionalAndDefaultsFalse()
    {
        // INVARIANT: rewriting an existing file without an explicit flag is silent data loss, so overwrite
        // is NOT required and not defaulted true.
        var schema = Tool("kompas_export_drawing").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("output_path", required);
        Assert.Contains("format", required);
        Assert.DoesNotContain("overwrite", required);
    }

    [Fact]
    public void CreateViews_KeepsScaleAndGapsOptional()
    {
        // INVARIANT: absent scale/gaps mean the documented KOMPAS default, not a server invention — so they
        // are NOT required.
        var schema = Tool("kompas_create_drawing_views").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("source_path", required);
        Assert.DoesNotContain("scale", required);
        Assert.DoesNotContain("dx", required);
        Assert.DoesNotContain("dy", required);
        Assert.DoesNotContain("projections", required);
    }

    [Fact]
    public void AddDimension_RequiresPoint1AndTheKind_ButNotPoint2ForRadial()
    {
        var schema = Tool("kompas_add_dimension").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("view_ref", required);
        Assert.Contains("dimension_type", required);
        Assert.Contains("point1", required);
        Assert.DoesNotContain("point2", required);
    }

    [Fact]
    public void DimensionType_EnumIsExactlyTheThreeDocumentedCollections()
    {
        // INVARIANT: the enum must mirror the three collections documented for ISymbols2DContainer — no more,
        // because an unpublished kind would be a guess.
        var schema = Tool("kompas_add_dimension").InputSchema;
        var dimensionType = (JsonObject)((JsonObject)schema["properties"]!)["dimension_type"]!;
        var values = ((JsonArray)dimensionType["enum"]!).Select(v => v!.GetValue<string>()).ToArray();

        var expected = new[] { "linear", "radial", "diametral" }.OrderBy(v => v).ToArray();
        Assert.Equal(expected, values.OrderBy(v => v).ToArray());
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void ExportFormat_EnumIsExactlyDxfAndDwg()
    {
        var schema = Tool("kompas_export_drawing").InputSchema;
        var format = (JsonObject)((JsonObject)schema["properties"]!)["format"]!;
        var values = ((JsonArray)format["enum"]!).Select(v => v!.GetValue<string>()).ToArray();

        Assert.Equal(new[] { "dwg", "dxf" }, values.OrderBy(v => v).ToArray());
        Assert.Equal(2, values.Length);
    }

    [Fact]
    public void Projections_EnumMatchesThePublishedProjectionTypeNames()
    {
        // INVARIANT: the schema enum and the adapter's dictionary must agree — a name accepted by the schema
        // but refused by the adapter (or vice versa) would be an inconsistency the client cannot see.
        var schema = Tool("kompas_create_drawing_views").InputSchema;
        var projections = (JsonObject)((JsonObject)schema["properties"]!)["projections"]!;
        var items = (JsonObject)projections["items"]!;
        var values = ((JsonArray)items["enum"]!).Select(v => v!.GetValue<string>()).ToArray();

        var expected = new[]
        {
            "front", "rear", "top", "bottom", "left", "right",
            "isometric", "iso_yzx", "iso_zxy", "dimetric",
        };

        Assert.Equal(expected.OrderBy(v => v).ToArray(), values.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void TitleBlockCells_IsAFreeObjectKeyedByNumber()
    {
        // INVARIANT: the cell ids are NOT documented, so the schema must not enumerate a fixed set of
        // "наименование / обозначение / …" keys — it is a MAP: arbitrary numeric-string keys, every
        // value a string. The value schema lives on the object's own additionalProperties, NOT inside
        // `properties` (which would publish a literal key literally named "additionalProperties").
        var schema = Tool("kompas_set_title_block").InputSchema;
        var cells = (JsonObject)((JsonObject)schema["properties"]!)["cells"]!;

        Assert.Equal("object", cells["type"]!.GetValue<string>());
        Assert.False(cells.ContainsKey("properties"));
        var valueSchema = (JsonObject)cells["additionalProperties"]!;
        Assert.Equal("string", valueSchema["type"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void DrawingTool_DescriptionIsNotAStalePromise(string name)
    {
        // INVARIANT: no description may imply a live run already happened — the wording must reflect the
        // CURRENT state, not the presence of an acceptance word.
        var description = Tool(name).Description;
        Assert.DoesNotContain("живой приёмки не было", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("живого прогона не было", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DrawingViewRow_KeepsUnreadFieldsNullable()
    {
        // INVARIANT: an empty field means "not read", not zero — the gabarit is NOT published by IView, so it
        // stays null rather than being filled approximately.
        var row = new DrawingViewRowDto
        {
            ViewRef = "view:00000000000000000000000000000000",
        };

        Assert.Null(row.Number);
        Assert.Null(row.Scale);
        Assert.Null(row.GabaritMin);
        Assert.Null(row.GabaritMax);
        Assert.Null(row.SourcePath);
        Assert.Null(row.ObjectCount);
    }

    [Fact]
    public void DimensionRow_KeepsUnreadFieldsNullable()
    {
        var row = new DimensionRowDto
        {
            DimensionRef = "dimension:00000000000000000000000000000000",
            Kind = "linear",
        };

        Assert.Null(row.ValueMm);
        Assert.Null(row.Valid);
        Assert.Null(row.Point1);
        Assert.Null(row.Point2);
    }

    [Fact]
    public void TitleBlockCell_KeepsReadBackNullableSoMismatchIsVisible()
    {
        // INVARIANT: a mismatch is not hidden — read-back stays null when the cell could not be read, and
        // Matched is false, so the caller sees the failure instead of an echo.
        var cell = new TitleBlockCellDto("5", "текст", null, false);

        Assert.False(cell.Matched);
        Assert.Null(cell.ReadBack);
        Assert.Equal("текст", cell.Requested);
    }

    [Fact]
    public void WrongDocumentKind_IsANamedRefusalCode()
    {
        // INVARIANT: applying a drawing operation to a wrong-kind document is WRONG_DOCUMENT_KIND — a code
        // the caller can branch on — not a raw COM exception. The code exists and has a Russian message.
        Assert.Equal("WRONG_DOCUMENT_KIND", ErrorCodes.WrongDocumentKind);
        Assert.False(string.IsNullOrWhiteSpace(ErrorMessages.For(ErrorCodes.WrongDocumentKind)));
    }

    [Fact]
    public void FormatUnavailable_IsANamedRefusalCode()
    {
        // INVARIANT: an undocumented export format (PDF) is refused BY NAME, not attempted blindly.
        Assert.Equal("FORMAT_UNAVAILABLE", ErrorCodes.FormatUnavailable);
        Assert.False(string.IsNullOrWhiteSpace(ErrorMessages.For(ErrorCodes.FormatUnavailable)));
    }

    [Fact]
    public void DrawingCommands_PublishTheirWireNames()
    {
        Assert.Equal("drawing.create_views", WorkerCommands.CreateDrawingViews);
        Assert.Equal("drawing.list_views", WorkerCommands.ListDrawingViews);
        Assert.Equal("drawing.edit_view", WorkerCommands.EditView);
        Assert.Equal("drawing.rebuild_views", WorkerCommands.RebuildDrawingViews);
        Assert.Equal("drawing.add_dimension", WorkerCommands.AddDimension);
        Assert.Equal("drawing.list_dimensions", WorkerCommands.ListDimensions);
        Assert.Equal("drawing.set_title_block", WorkerCommands.SetTitleBlock);
        Assert.Equal("drawing.set_technical_demand", WorkerCommands.SetTechnicalDemand);
        Assert.Equal("drawing.export", WorkerCommands.ExportDrawing);
    }

    [Fact]
    public void RebuildDrawingViews_NamesTheDocumentedRouteAndTheAbsentMember()
    {
        // INVARIANT: the rebuild action names the DOCUMENTED route it actually calls
        // (IKompasDocument2D1.RebuildDocument), and states that IDrawingDocument.RebuildViews — named by
        // the help but declared by no type of the shipped interop — is not used. The verdict must be the
        // RE-READ view, never the returned boolean alone.
        var tool = Tool("kompas_rebuild_drawing_views");
        var description = tool.Description;

        Assert.True(tool.IsMutation);
        Assert.Contains("RebuildDocument", description, StringComparison.Ordinal);
        Assert.Contains("ikompasdocument2d1_rebuilddocument.html", description, StringComparison.Ordinal);
        Assert.Contains("НЕ объявлен", description, StringComparison.Ordinal);
        Assert.Contains("ПЕРЕЧИТАННЫЙ", description, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildDrawingViewsResult_KeepsTheCallAndTheRereadSeparate()
    {
        // INVARIANT: the boolean says "the call completed", the re-read row says "the view is still there";
        // they are carried separately so a TRUE verdict cannot be mistaken for a proven projection change.
        var before = new DrawingViewRowDto { ViewRef = "view:1", Number = 1, SourcePath = "m.m3d" };
        var result = new RebuildDrawingViewsResult(
            true, before, before, "IDrawingDocument(QI IKompasDocument2D1).RebuildDocument()",
            new VerificationDto(VerificationLevel.StructureChecked, Array.Empty<NamedCheck>(),
                                Array.Empty<string>()));

        Assert.True(result.RebuildReturned);
        Assert.Equal(result.ViewBefore!.Number, result.ViewAfter!.Number);
        Assert.Contains("RebuildDocument", result.Route, StringComparison.Ordinal);
    }

    [Fact]
    public void EditView_DescriptionNamesTheDocumentedWritableRouteAndTheUntestedBoundary()
    {
        // INVARIANT: the corrected finding — v24 DOES document a view edit (Scale is writable and needs
        // Update). The description must carry the route, and must NOT imply suppression/deletion are
        // proven by it.
        var description = Tool("kompas_edit_view").Description;
        Assert.Contains("IView.Scale", description, StringComparison.Ordinal);
        Assert.Contains("IDrawingObject::Update", description, StringComparison.Ordinal);
        Assert.Contains("НЕ выполняются", description, StringComparison.Ordinal);
    }

    [Fact]
    public void EditView_KeepsEveryPropertyOptionalSoNullMeansUntouched()
    {
        // INVARIANT: a null property means "leave it alone", not a server-invented default — so scale/x/y
        // are all optional and only the addressing fields are required.
        var schema = Tool("kompas_edit_view").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("view_ref", required);
        Assert.Contains("expected_revision", required);
        Assert.DoesNotContain("scale", required);
        Assert.DoesNotContain("x", required);
        Assert.DoesNotContain("y", required);
    }

    [Fact]
    public void SetTechnicalDemand_IsAMutationRequiringText()
    {
        // INVARIANT: item 6 of the order is mandatory once step 0 produced a documented route, so the tool
        // exists and carries the route in its description.
        var tool = Tool("kompas_set_technical_demand");
        var description = tool.Description;
        var required = ((JsonArray)tool.InputSchema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.True(tool.IsMutation);
        Assert.Contains("ITechnicalDemand", description, StringComparison.Ordinal);
        Assert.Contains("idrawingdocument_technicaldemand.html", description, StringComparison.Ordinal);
        Assert.Contains("text", required);
        Assert.Contains("operation_id", required);
    }

    [Fact]
    public void EditViewResult_KeepsBeforeAndAfterDistinct()
    {
        // INVARIANT: a positive edit is proven by the SAME view changing, so the result carries both states
        // rather than one row that could be an added view.
        var before = new DrawingViewRowDto { ViewRef = "view:1", Scale = 1.0 };
        var after = new DrawingViewRowDto { ViewRef = "view:1", Scale = 2.0 };
        var result = new EditViewResult(
            before, after,
            new VerificationDto(VerificationLevel.StructureChecked, Array.Empty<NamedCheck>(), Array.Empty<string>()));

        Assert.Equal(2.0, result.After.Scale);
        Assert.Equal(1.0, result.Before.Scale);
        Assert.Equal(result.Before.ViewRef, result.After.ViewRef);
    }
}
