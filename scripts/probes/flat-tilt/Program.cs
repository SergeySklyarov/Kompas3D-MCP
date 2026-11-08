using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace FlatTiltProbe;

/// <summary>Probe for DEBT_FLAT_NORMAL_MATRIX_0_14: what the 16 elements of <c>SetMatrix3D</c> mean.</summary>
/// <remarks>INVARIANT: every number is read back from KOMPAS — the view's frame, the walled DXF, the
/// projection list — and compared with the standard view of the same part; an accepted call is never
/// the evidence. LIMIT: measures the six axial directions and the tilt cases of the order.
/// History: docs/decisions/flat-part.md#view-choice</remarks>
internal sealed class Probe : IDisposable
{
    private const double Width = 40d;
    private const double Height = 30d;
    private const double Thickness = 20d;
    private const short RectParam = 15;
    private const double SizeTolerance = 0.5d;
    private const double OffsetTolerance = 0.05d;

    /// <summary>One part per axis: the plate of its own face, the extrusion across that face, the hole.</summary>
    /// <remarks>INVARIANT: the plate lies in the plane PERPENDICULAR to the hole's axis and is extruded
    /// across it, so the hole pierces the extruded face; a hole along one axis is a plain wall on the
    /// faces of the other two, and its offset is what tells a mirror from a rotation.
    /// Layout: plane id (<c>o3d_planeXOY=1, XOZ=2, YOZ=3</c>), plate corner and size, extrusion depth,
    /// hole centre in the plane's own coordinates and its radius.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private static readonly Dictionary<string, Layout> Face = new()
    {
        ["z"] = new Layout(1, -Width / 2d, -Height / 2d, Width, Height, Thickness, 12d, -6d, 4d),
        ["y"] = new Layout(2, -Width / 2d, -Thickness / 2d, Width, Thickness, Height, -9d, 7d, 3d),
        ["x"] = new Layout(3, -Height / 2d, -Thickness / 2d, Height, Thickness, Width, 8d, -5d, 2.5d),
    };

    private sealed record Layout(int PlaneId, double Left, double Bottom, double SizeA, double SizeB,
        double Depth, double HoleA, double HoleB, double Radius);

    private readonly string _work;
    private readonly JsonArray _rows = new();
    private readonly Dictionary<string, Part> _parts = new();
    private KompasObject? _app5;
    private IApplication? _app7;

    public Probe(string work)
    {
        _work = Path.GetFullPath(work);
        Directory.CreateDirectory(_work);
    }

    private sealed record Part(string Axis, IKompasDocument3D Doc7, ksDocument3D Doc5, string Path);

    private sealed record StandardView(int Type, int Number, double[]? Frame, Contour? Contour);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ResolveInterop();
        var mode = args.Length > 0 ? args[0] : "matrix";
        var work = args.Length > 1 ? args[1] : Path.Combine("scratch", "probe-flat-tilt");
        using var probe = new Probe(work);
        probe.Run(mode);
        return probe.Failed ? 1 : 0;
    }

    private bool Failed => _rows.Any(row => (string?)row!["verdict"] == "FAIL");

    private void Run(string mode)
    {
        try
        {
            Launch();
            BuildPart("z");
            BuildPart("y");
            BuildPart("x");
            if (mode is "matrix" or "all")
            {
                foreach (var axis in new[] { "z", "y", "x" })
                {
                    DumpCollection(axis);
                    StandardViews(axis);
                }
            }

            if (mode is "axial" or "all")
            {
                AxialCandidates();
            }

            if (mode is "extra" or "all")
            {
                Extra();
            }
        }
        catch (Exception ex)
        {
            Row("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }
        finally
        {
            Write(mode);
        }
    }

    // ── environment ─────────────────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
            ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован");
        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("объект не приводится к KompasObject");
        _app5.Visible = false;
        _app7 = _app5.ksGetApplication7() as IApplication
            ?? throw new InvalidOperationException("ksGetApplication7() не дал IApplication");
    }

    // ── the parts ───────────────────────────────────────────────────────────────────────────────

    private void BuildPart(string axis)
    {
        var doc7 = _app7!.Documents.Add(DocumentTypeEnum.ksDocumentPart, false) as IKompasDocument3D
            ?? throw new InvalidOperationException("Documents.Add(ksDocumentPart) не дал IKompasDocument3D");
        var doc5 = _app5!.TransferInterface(doc7, (int)ksAPITypeEnum.ksAPI5Auto, 0) as ksDocument3D
            ?? throw new InvalidOperationException("TransferInterface не дал ksDocument3D");
        var part = doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) не дал ksPart");

        var layout = Face[axis];
        var plane = (ksEntity)part.GetDefaultEntity((short)layout.PlaneId);
        var plate = AddSketch(part, plane, "plate", editor =>
        {
            var left = layout.Left;
            var bottom = layout.Bottom;
            var right = left + layout.SizeA;
            var top = bottom + layout.SizeB;
            editor.ksLineSeg(left, bottom, right, bottom, 1);
            editor.ksLineSeg(right, bottom, right, top, 1);
            editor.ksLineSeg(right, top, left, top, 1);
            editor.ksLineSeg(left, top, left, bottom, 1);
        });
        var boss = (ksEntity)part.NewEntity(25);
        if (boss.GetDefinition() is not ksBossExtrusionDefinition bossDefinition)
        {
            throw new InvalidOperationException("определение выдавливания не ksBossExtrusionDefinition");
        }

        bossDefinition.SetSketch(plate);
        bossDefinition.SetSideParam(true, 0, layout.Depth, 0, false);
        boss.Create();

        // The hole sketch lies on the base plane itself, so a cut in both directions from that plane is
        // a through hole along the plate's extrusion axis.
        var holeSketch = AddSketch(part, plane, "hole",
            editor => editor.ksCircle(layout.HoleA, layout.HoleB, layout.Radius, 1));
        var cut = (ksEntity)part.NewEntity(26);
        if (cut.GetDefinition() is not ksCutExtrusionDefinition cutDefinition)
        {
            throw new InvalidOperationException("определение вырезания не ksCutExtrusionDefinition");
        }

        cutDefinition.SetSketch(holeSketch);
        cutDefinition.directionType = 2;
        cutDefinition.SetSideParam(true, 0, Width, 0, false);
        cutDefinition.SetSideParam(false, 0, Width, 0, false);
        cut.Create();
        part.Update();
        doc5.RebuildDocument();

        var path = Path.Combine(_work, "flat-" + axis + ".m3d");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        doc7.SaveAs(path);
        Row("part." + axis, "деталь " + axis + " построена и сохранена",
            "файл=" + File.Exists(path) + " габарит=" + Gabarit(part) + " граней=" + FaceCount(part),
            File.Exists(path) ? "INFO" : "FAIL");
        _parts[axis] = new Part(axis, doc7, doc5, path);
    }

    private static ksEntity AddSketch(ksPart part, ksEntity plane, string name, Action<ksDocument2D> draw)
    {
        var sketch = (ksEntity)part.NewEntity(5);
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        draw(editor);
        definition.EndEdit();
        return sketch;
    }

    private static int FaceCount(ksPart part)
    {
        var body = part.GetMainBody() as ksBody;
        return body?.FaceCollection() is ksFaceCollection collection ? collection.GetCount() : -1;
    }

    private static string Gabarit(ksPart part)
    {
        var body = part.GetMainBody() as ksBody;
        if (body is null || !body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
        {
            return "не прочитан";
        }

        return Num(Math.Abs(x2 - x1)) + " x " + Num(Math.Abs(y2 - y1)) + " x " + Num(Math.Abs(z2 - z1));
    }

    // ── the projection list of the part ─────────────────────────────────────────────────────────

    private void DumpCollection(string axis)
    {
        var part = _parts[axis];
        var collection = Safe(() => part.Doc5.GetViewProjectionCollection() as ksViewProjectionCollection);
        if (collection is null)
        {
            Row("collection", "GetViewProjectionCollection() даёт ksViewProjectionCollection", "null", "FAIL");
            return;
        }

        var count = collection.GetCount();
        Row("collection.count", "проекций в документе " + axis, count.ToString(CultureInfo.InvariantCulture),
            "INFO");
        var manager = Manager(part.Doc7);
        Row("collection.manager", "IKompasDocument3D1.ViewProjectionManager даёт IViewProjectionManager",
            manager is null ? "null" : "IViewProjectionManager", manager is null ? "FAIL" : "INFO");
        for (var index = 0; index < count; index++)
        {
            var projection = Safe(() => collection.GetByIndex(index) as ksViewProjection);
            if (projection is null)
            {
                Row("collection[" + index + "]", "проекция по индексу", "null", "INFO");
                continue;
            }

            Row("collection[" + index + "]", "проекция документа " + axis,
                "тип=" + Safe(projection.GetViewProjectonType) + " имя=«" + (Safe(() => projection.name) ?? "?")
                + "» текущая=" + Safe(projection.IsCurrent), "INFO");
        }
    }

    // ── the six standard views and their contours ───────────────────────────────────────────────

    /// <summary>The six standard views of a part: which face each one shows, in sheet millimetres.</summary>
    /// <remarks>DOC: iviews_addstandartviews.html — «x, y - координаты создаваемого главного вида
    /// (спереди)», «dx, dy - расстояние между видами»; ksviewprojectiontype.html — the type ordinals.
    /// MEASURED: the frame alone says which face the view shows; the hole in that face appears as a
    /// CIRCLE, so a view of a wall without the hole is recognised by its empty contour.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private List<StandardView> StandardViews(string axis)
    {
        var part = _parts[axis];
        var drawing = CreateDrawing();
        var views = Views(drawing);
        var created = new List<(int Type, int Number)>();
        foreach (var type in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var before = ViewNumbers(views);
            AddViews(views, part.Path, "Спереди", 100d, 100d, type);
            foreach (var number in ViewNumbers(views).Except(before).OrderBy(value => value))
            {
                created.Add((type, number));
            }
        }

        // The frames are read BEFORE SaveAs: a pointer taken afterwards no longer names the document.
        var editor = Editor(drawing);
        var frames = created.ToDictionary(pair => pair.Number, pair => ViewGabarit(editor, pair.Number));
        var full = SaveAndRead(drawing, "standard-" + axis);
        var result = new List<StandardView>();
        foreach (var (type, number) in created)
        {
            var frame = frames[number];
            var contour = full?.Clip(frame);
            result.Add(new StandardView(type, number, frame, contour));
            var name = Safe(() => views.get_ViewByNumber(number) as IView) is { } view
                ? Safe(() => (view as IAssociationView)?.ProjectionName)
                : null;
            Row("standard." + axis + "." + type + "." + number,
                "стандартный вид типа " + type + " детали " + axis,
                "имя=«" + (name ?? "?") + "» рамка=" + Rect(frame) + " "
                + (contour is null ? "контур не прочитан" : contour.Describe()), "INFO");
        }

        drawing.Close(Kompas6Constants.DocumentCloseOptions.kdDoNotSaveChanges);
        return result;
    }

    // ── the candidate arrangements of the 4×4 ───────────────────────────────────────────────────

    /// <summary>Every candidate arrangement on the six axial directions, against the standard views.</summary>
    /// <remarks>INVARIANT: a candidate wins only if its view's contour coincides with the standard view's
    /// contour on ALL six directions — box size and hole offsets relative to the box centre, which the
    /// frame alone cannot tell apart from a mirror.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private void AxialCandidates()
    {
        var arrangements = new[] { "rows", "cols" };
        var widths = new Dictionary<(string Arrangement, string Side), string>();
        foreach (var axis in new[] { "z", "y", "x" })
        {
            var part = _parts[axis];
            var standards = StandardViews(axis);
            foreach (var arrangement in arrangements)
            {
                foreach (var sign in new[] { 1, -1 })
                {
                    var name = "Kmc" + axis + (sign > 0 ? "P" : "N") + arrangement;
                    var matrix = MatrixFor(AxisVector(axis, sign), arrangement);
                    var contour = MatrixContour(part, name, matrix, "matrix-" + name);
                    // Only the pair that shows THIS face is the reference: another axis's view would
                    // match by box size and pretend the candidate reproduced the right face.
                    var matched = standards
                        .Where(view => TypesOf(axis).Contains(view.Type))
                        .Where(view => SameContour(view.Contour, contour))
                        .Select(view => "тип " + view.Type + "/вид " + view.Number)
                        .ToArray();
                    var side = axis + (sign > 0 ? "+" : "-");
                    widths[(arrangement, side)] = matched.Length > 0 ? string.Join(",", matched) : "нет";
                    Row("axial." + side + "." + arrangement,
                        "вид по матрице (" + arrangement + ") для нормали " + side
                        + ": рамка и контур против шести стандартных видов",
                        (contour is null ? "контур не прочитан" : contour.Describe()) + "; совпал с ["
                        + widths[(arrangement, side)] + "]",
                        matched.Length == 1 ? "INFO" : "FAIL");
                }
            }
        }

        foreach (var arrangement in arrangements)
        {
            var winners = new[] { "z+", "z-", "y+", "y-", "x+", "x-" }
                .Where(side => widths[(arrangement, side)] != "нет").ToArray();
            Row("candidate." + arrangement, "кандидат «" + arrangement + "» воспроизвёл стандартный вид",
                "осей совпало " + winners.Length + "/6: " + string.Join(", ", winners.Select(side =>
                    side + "=" + widths[(arrangement, side)])),
                winners.Length == 6 ? "PASS" : "FAIL");
        }
    }

    /// <summary>The standard view types of one face, in <c>ksViewProjectionType</c> ordinals.</summary>
    /// <remarks>MEASURED: the standard views show the faces as «Спереди/Сзади» - XY, «Сверху/Снизу» -
    /// XZ, «Слева/Справа» - YZ, which is what the contour of the hole in each part answers.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private static int[] TypesOf(string axis) => axis switch
    {
        "z" => new[] { 1, 2 },
        "y" => new[] { 3, 4 },
        _ => new[] { 5, 6 },
    };

    private static double[] AxisVector(string axis, int sign) => axis switch
    {
        "z" => new[] { 0d, 0d, (double)sign },
        "y" => new[] { 0d, (double)sign, 0d },
        _ => new[] { (double)sign, 0d, 0d },
    };

    /// <summary>A 4×4 from a view direction: Z along the normal, X and Y right-handed around it.</summary>
    /// <remarks>INVARIANT: the basis is built by one rule for every direction, so the candidates differ
    /// only in the ARRANGEMENT of the same numbers; the translation is the last column and the last
    /// element is 1, which is the identity's own shape. History: docs/decisions/flat-part.md#view-choice</remarks>
    private static double[] MatrixFor(double[] normal, string arrangement)
    {
        var z = Normalize(normal);
        var reference = Math.Abs(z[2]) < 0.9d ? new[] { 0d, 0d, 1d } : new[] { 0d, 1d, 0d };
        var x = Normalize(Cross(reference, z));
        var y = Cross(z, x);
        var basis = new[] { x, y, z };
        var matrix = new double[16];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                matrix[(row * 4) + column] = arrangement == "rows" ? basis[row][column] : basis[column][row];
            }
        }

        matrix[15] = 1d;
        return matrix;
    }

    private Contour? MatrixContour(Part part, string name, double[] matrix, string tag)
    {
        var manager = Manager(part.Doc7);
        if (manager is null)
        {
            Row("matrix." + name, "менеджер проекций детали", "null", "FAIL");
            return null;
        }

        IViewProjection7? projection;
        try
        {
            projection = manager.Add() as IViewProjection7;
        }
        catch (COMException ex)
        {
            Row("matrix." + name + ".add", "IViewProjectionManager.Add()", Describe(ex), "FAIL");
            return null;
        }

        if (projection is null)
        {
            Row("matrix." + name + ".add", "IViewProjectionManager.Add()", "null", "FAIL");
            return null;
        }

        projection.Name = name;
        var applied = Safe(() => projection.SetMatrix3D(matrix));
        var updated = Safe(projection.Update);
        Row("matrix." + name + ".call", "Add() + Name + SetMatrix3D + Update() для «" + name + "»",
            "SetMatrix3D=" + applied + " Update=" + updated + " индекс=" + Safe(() => projection.UserProjectionIndex)
            + " тип=" + Safe(() => projection.ViewProjectonType),
            applied == true && updated == true ? "INFO" : "FAIL");

        var collection = Safe(() => part.Doc5.GetViewProjectionCollection() as ksViewProjectionCollection);
        var found = collection is not null && Safe(() => collection.GetByName(name, true, true)) is not null;
        Row("matrix." + name + ".state", "состояние документа после создания проекции",
            "Changed=" + Safe(() => part.Doc7.Changed) + " проекций=" + Safe(() => collection?.GetCount())
            + " GetByName(«" + name + "»)=" + found, found ? "INFO" : "FAIL");

        var drawing = CreateDrawing();
        var views = Views(drawing);
        var before = ViewNumbers(views);
        var accepted = AddViews(views, part.Path, name, 100d, 100d);
        var created = ViewNumbers(views).Except(before).OrderBy(value => value).ToArray();
        Row("matrix." + name + ".view", "AddStandartViews(файл, «" + name + "», [1])",
            "принят=" + accepted + " новых=" + string.Join(",", created), accepted ? "INFO" : "FAIL");
        if (created.Length == 0)
        {
            drawing.Close(Kompas6Constants.DocumentCloseOptions.kdDoNotSaveChanges);
            return null;
        }

        var editor = Editor(drawing);
        var frame = ViewGabarit(editor, created.Last());
        Row("matrix." + name + ".frame", "рамка вида по проекции «" + name + "»", Rect(frame), "INFO");
        var contour = SaveAndRead(drawing, tag)?.Clip(frame);
        drawing.Close(Kompas6Constants.DocumentCloseOptions.kdDoNotSaveChanges);
        return contour;
    }

    /// <summary>Three questions the product needs answered: cleanup, translation place, sign of the row.</summary>
    /// <remarks>INVARIANT: each answer is the view's own frame or contour read back, not the return code.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private void Extra()
    {
        var part = _parts["z"];
        var manager = Manager(part.Doc7)
            ?? throw new InvalidOperationException("менеджер проекций не получен");

        // 1. Does deleting the projection leave the document changed?
        var changedBefore = Safe(() => part.Doc7.Changed);
        IViewProjection7? temporary = null;
        try
        {
            temporary = manager.Add() as IViewProjection7;
        }
        catch (COMException ex)
        {
            Row("cleanup.add", "Add() для проверки удаления", Describe(ex), "FAIL");
        }

        var changedAfterAdd = Safe(() => part.Doc7.Changed);
        var created = temporary is not null;
        bool? deleted = null;
        if (temporary is not null)
        {
            temporary.Name = "KmcDelete";
            Safe(() => temporary.SetMatrix3D(Identity()));
            Safe(temporary.Update);
            deleted = Safe(temporary.Delete);
        }

        var changedAfterDelete = Safe(() => part.Doc7.Changed);
        var collection = Safe(() => part.Doc5.GetViewProjectionCollection() as ksViewProjectionCollection);
        Row("cleanup", "создание и удаление пользовательской проекции: остаётся ли документ изменённым",
            "создана=" + created + " Changed до=" + changedBefore + " после Add=" + changedAfterAdd + " Delete="
            + deleted + " после Delete=" + changedAfterDelete + " проекций=" + Safe(() => collection?.GetCount()),
            "INFO");

        // 2. Where the translation lives: last column or last row, and the corner element.
        foreach (var variant in new[] { "zero", "column", "row", "corner" })
        {
            var matrix = Identity();
            switch (variant)
            {
                case "column":
                    matrix[3] = 10d;
                    break;
                case "row":
                    matrix[12] = 10d;
                    break;
                case "corner":
                    matrix[15] = 0d;
                    break;
            }

            var contour = MatrixContour(part, "KmcT" + variant, matrix, "translation-" + variant);
            Row("translation." + variant, "матрица с переносом в «" + variant + "» даёт вид",
                contour is null ? "контура нет" : contour.Describe(), "INFO");
        }

        // 3. The sign: which of the two rows along the axis is the line of sight.
        foreach (var sign in new[] { 1, -1 })
        {
            var matrix = Identity();
            matrix[10] = sign;
            var contour = MatrixContour(part, "KmcZ" + (sign > 0 ? "P" : "N"), matrix, "sign-" + sign);
            Row("sign." + (sign > 0 ? "+" : "-"), "матрица с третьей строкой (0,0," + sign + ")",
                contour is null ? "контура нет" : contour.Describe(), "INFO");
        }
    }

    private static double[] Identity()
    {
        var matrix = new double[16];
        matrix[0] = 1d;
        matrix[5] = 1d;
        matrix[10] = 1d;
        matrix[15] = 1d;
        return matrix;
    }

    // ── drawings, views, frames ─────────────────────────────────────────────────────────────────

    private IKompasDocument2D CreateDrawing() =>
        _app7!.Documents.Add(DocumentTypeEnum.ksDocumentDrawing, false) as IKompasDocument2D
        ?? throw new InvalidOperationException("Documents.Add(ksDocumentDrawing) не дал IKompasDocument2D");

    private ksDocument2D Editor(IKompasDocument2D drawing) =>
        _app5!.TransferInterface(drawing, (int)ksAPITypeEnum.ksAPI5Auto, 0) as ksDocument2D
        ?? throw new InvalidOperationException("TransferInterface чертежа не дал ksDocument2D");

    private static IViews Views(IKompasDocument2D drawing) =>
        (drawing.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews
        ?? throw new InvalidOperationException("чертёж не отдал IViews");

    private static IViewProjectionManager? Manager(IKompasDocument3D document) =>
        (document as IKompasDocument3D1)?.ViewProjectionManager as IViewProjectionManager;

    private static bool AddViews(IViews views, string partPath, string name, double x = 100d, double y = 100d,
        int type = 0)
    {
        try
        {
            // MEASURED: a dependent view is built only next to the main «Спереди», so the main type is
            // always in the list; dx/dy keep the frames apart on the sheet.
            var list = type is 0 or 1 ? new object[] { 1 } : new object[] { 1, type };
            return views.AddStandartViews(partPath, name, list, x, y, 1d, 200d, 200d);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static int[] ViewNumbers(IViews views)
    {
        var numbers = new List<int>();
        var count = Safe(() => views.Count);
        for (var index = 0; index < count; index++)
        {
            if (Safe(() => views.get_View(index) as IView) is { } view && Safe(() => view.Number) is int number
                && number > 0)
            {
                numbers.Add(number);
            }
        }

        return numbers.ToArray();
    }

    private double[]? ViewGabarit(ksDocument2D? editor, int? number)
    {
        if (editor is null || number is not int value)
        {
            return null;
        }

        try
        {
            var pointer = editor.ksGetViewReference(value);
            if (pointer == 0 || _app5!.GetParamStruct(RectParam) is not ksRectParam rect)
            {
                return null;
            }

            if (editor.ksGetObjGabaritRect(pointer, rect) == 0)
            {
                return null;
            }

            return rect.GetpBot() is ksMathPointParam bottom && rect.GetpTop() is ksMathPointParam top
                ? new[] { bottom.x, bottom.y, top.x, top.y }
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // ── DXF: the contour the view really carries ────────────────────────────────────────────────

    private Contour? SaveAndRead(IKompasDocument2D drawing, string tag)
    {
        var drawingPath = Path.Combine(_work, tag + ".cdw");
        var dxfPath = Path.Combine(_work, tag + ".dxf");
        foreach (var file in new[] { drawingPath, dxfPath })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        drawing.SaveAs(drawingPath);
        var library = ExportLibraryPath();
        if (library is null || !File.Exists(drawingPath))
        {
            Row("dxf." + tag, "чертёж сохранён и библиотека найдена",
                "библиотека=" + library + " файл=" + File.Exists(drawingPath), "FAIL");
            return null;
        }

        var converter = _app7!.get_Converter(library) as IConverter;
        if (converter is null)
        {
            Row("dxf." + tag, "конвертер DXF готов", "IConverter не получен", "FAIL");
            return null;
        }

        converter.GetFilter(1, true, out var commandId);
        if (commandId <= 0)
        {
            Row("dxf." + tag, "конвертер DXF готов", "GetFilter(1, true) не дал команду: " + commandId, "FAIL");
            return null;
        }

        converter.Convert(drawingPath, dxfPath, commandId, false);
        if (!File.Exists(dxfPath))
        {
            Row("dxf." + tag, "DXF записан", "файла нет", "FAIL");
            return null;
        }

        return Contour.Read(dxfPath);
    }

    private static string? ExportLibraryPath()
    {
        var directory = typeof(Probe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "KompasInteropDir")?.Value;
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var root = Directory.GetParent(directory)?.Parent?.Parent?.Parent;
        var path = root is null ? null : Path.Combine(root.FullName, "Libs", "ImpExp", "dwgdxfExp.rtw");
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Whether the two contours are the same figure, allowing an in-plane ROTATION only.</summary>
    /// <remarks>INVARIANT: the rotation is a quarter turn at most, never a mirror: a mirrored contour is a
    /// different part, and the hole offsets are what tell the two apart.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private static bool SameContour(Contour? reference, Contour? candidate)
    {
        if (reference is null || candidate is null)
        {
            return false;
        }

        var left = reference.SortedSize();
        var right = candidate.SortedSize();
        if (Math.Abs(left[0] - right[0]) > SizeTolerance || Math.Abs(left[1] - right[1]) > SizeTolerance)
        {
            return false;
        }

        var expected = reference.CircleOffsets();
        var actual = candidate.CircleOffsets();
        if (expected.Count != actual.Count || expected.Count == 0)
        {
            return expected.Count == actual.Count;
        }

        for (var turn = 0; turn < 4; turn++)
        {
            if (expected.All(pair => actual.TryGetValue(pair.Key, out var offset)
                && Math.Abs(Rotate(pair.Value, turn).X - offset.X) <= OffsetTolerance
                && Math.Abs(Rotate(pair.Value, turn).Y - offset.Y) <= OffsetTolerance))
            {
                return true;
            }
        }

        return false;
    }

    private static (double X, double Y) Rotate((double X, double Y) point, int turn) => turn switch
    {
        1 => (-point.Y, point.X),
        2 => (-point.X, -point.Y),
        3 => (point.Y, -point.X),
        _ => point,
    };

    // ── numbers ────────────────────────────────────────────────────────────────────────────────

    private static double[] Cross(double[] left, double[] right) => new[]
    {
        (left[1] * right[2]) - (left[2] * right[1]),
        (left[2] * right[0]) - (left[0] * right[2]),
        (left[0] * right[1]) - (left[1] * right[0]),
    };

    private static double[] Normalize(double[] vector)
    {
        var length = Math.Sqrt((vector[0] * vector[0]) + (vector[1] * vector[1]) + (vector[2] * vector[2]));
        return length <= 0d
            ? new[] { 0d, 0d, 1d }
            : new[] { vector[0] / length, vector[1] / length, vector[2] / length };
    }

    private static double[]? Numbers(object? value)
    {
        if (value is double[] typed)
        {
            return typed;
        }

        if (value is object[] objects)
        {
            return objects.Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToArray();
        }

        if (value is Array array && array.Rank == 1)
        {
            var result = new double[array.Length];
            for (var index = 0; index < array.Length; index++)
            {
                result[index] = Convert.ToDouble(array.GetValue(index), CultureInfo.InvariantCulture);
            }

            return result;
        }

        return null;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return default;
        }
        catch (InvalidCastException)
        {
            return default;
        }
        catch (MissingMemberException)
        {
            return default;
        }
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Rect(double[]? box) => box is { Length: 4 }
        ? "x " + Num(box[0]) + ".." + Num(box[2]) + ", y " + Num(box[1]) + ".." + Num(box[3])
        : "не прочитана";

    private static string Describe(Exception error) => error.GetType().Name + ": " + error.Message;

    // ── report ─────────────────────────────────────────────────────────────────────────────────

    private void Row(string id, string check, string observed, string verdict)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["check"] = check,
            ["observed"] = observed,
            ["verdict"] = verdict,
        });
        Console.WriteLine(verdict + " " + id + ": " + observed);
    }

    private void Write(string mode)
    {
        var path = Path.Combine(_work, "probe-" + mode + ".json");
        var document = new JsonObject
        {
            ["probe"] = "flat-tilt-" + mode,
            ["at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["rows"] = _rows.DeepClone(),
        };
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Console.WriteLine("записано: " + path + "; строк " + _rows.Count);
    }

    private static void ResolveInterop()
    {
        var directory = typeof(Probe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "KompasInteropDir")?.Value;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name;
            var candidate = Path.Combine(directory, name + ".dll");
            return name is not null && File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    public void Dispose()
    {
        foreach (var part in _parts.Values)
        {
            try
            {
                part.Doc7.Close(Kompas6Constants.DocumentCloseOptions.kdDoNotSaveChanges);
            }
            catch (COMException)
            {
                // The measurement is already written; a refused close changes nothing it reported.
            }

            Marshal.ReleaseComObject(part.Doc5);
        }

        if (_app5 is not null)
        {
            try
            {
                ((dynamic)_app5).Exit();
            }
            catch (Exception)
            {
                // Same reason: the session ends either way.
            }

            Marshal.ReleaseComObject(_app5);
        }
    }
}
