using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace SheetMetalProbe;

/// <summary>Measures the documented sheet-metal routes on this installation, end to end.</summary>
/// <remarks>
/// INVARIANT: each check records EXPECTED and READ, so a route that answers without building anything
/// cannot pass; a kernel refusal is recorded as INFO and never retried with other input.
/// INVARIANT: the entry is the product's own route (ProgID KOMPAS.Application.5, then ksGetApplication7).
/// DOC: isheetmetal*.html, ks*enum.html, ksdocument3d_deleteobject.html (reference/kompas-sdk-v24).
/// History: docs/decisions/sheet-metal.md#probe-routes
/// </remarks>
internal sealed class SheetMetalProbe : IDisposable
{
    private const short PlaneXoy = 1;
    private const short ObjSketch = 5;

    /// <summary>ksAPITypeEnum.ksAPI7Dual — the transfer mode the product's bridge uses.</summary>
    private static readonly int Api7Dual = (int)ksAPITypeEnum.ksAPI7Dual;

    /// <summary>ksAPITypeEnum.ksAPI5Auto — the reverse direction, used for the deletion route.</summary>
    private static readonly int Api5Auto = (int)ksAPITypeEnum.ksAPI5Auto;

    private readonly string _outPath;
    private readonly List<Check> _checks = new();

    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;
    private ISheetMetalContainer? _container;
    private ISheetMetalBody? _body;
    private ISheetMetalBody? _body2;
    private ISheetMetalBend? _bend1;
    private ISheetMetalBend? _bend2;
    private ISheetMetalCut? _cut;
    private ISheetMetalHole? _hole;
    private ISheetMetalBendUnfoldParameters? _unfold;
    private string _version = "unknown";
    private int? _pid;
    private double _thickness = 1.5d;
    private double _radius = 2d;
    private int _facesBeforeCut;
    private double _depthCutDepth;

    public SheetMetalProbe(string outPath) => _outPath = outPath;

    public int Run()
    {
        try
        {
            Launch();
            Document();
            Container();
            Body();
            EditBody();
            UnfoldTypes();
            CutAndHole();
            Bend();
            SecondBend();
            EditAfterBend();
            Delete();
            UnfoldOperation();
            Unfold();
            Reopen();
            Refusals();
        }
        catch (ProbeStop)
        {
            // The reason is already recorded by the step that stopped the probe.
        }
        catch (Exception ex)
        {
            Record("probe", "проба доходит до конца", Describe(ex), Verdict.Fail);
        }

        Write();
        return Failed ? 1 : 0;
    }

    private bool Failed => _checks.Any(c => c.Verdict == Verdict.Fail);

    // ---------------------------------------------------------------------------------------------
    // Steps
    // ---------------------------------------------------------------------------------------------

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "не зарегистрирован", Verdict.Fail);
            throw new ProbeStop();
        }

        var before = KompasProcessIds();
        try
        {
            _app5 = Activator.CreateInstance(type) as KompasObject;
        }
        catch (COMException ex)
        {
            Record("app5_launch", "экземпляр КОМПАС создан", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (_app5 is null)
        {
            Record("app5_launch", "объект приводится к KompasObject", "не приводится", Verdict.Fail);
            throw new ProbeStop();
        }

        var appeared = KompasProcessIds().Except(before).ToArray();
        _pid = appeared.Length == 1 ? appeared[0] : null;
        Record("app5_launch", "объект приводится к KompasObject", "KompasObject", Verdict.Pass);

        try
        {
            _app5.Visible = false;
        }
        catch (COMException ex)
        {
            Record("app5_visible", "Visible = false", Describe(ex), Verdict.Info);
        }

        _version = ReadVersion(_app5);
        Record("kompas_version", "версия прочитана", _version, Verdict.Info);

        try
        {
            _app7 = _app5.ksGetApplication7() as IApplication;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("api7_bridge", "ksGetApplication7() даёт IApplication", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (_app7 is null)
        {
            Record("api7_bridge", "ksGetApplication7() даёт IApplication", "null", Verdict.Fail);
            throw new ProbeStop();
        }

        Record("api7_bridge", "ksGetApplication7() даёт IApplication", "IApplication", Verdict.Pass);
    }

    private void Document()
    {
        IKompasDocument added;
        try
        {
            added = _app7!.Documents.Add(DocumentTypeEnum.ksDocumentPart, false);
        }
        catch (COMException ex)
        {
            Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        _doc7 = added as IKompasDocument3D;
        if (_doc7 is null)
        {
            Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", RuntimeName(added), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", "IKompasDocument3D", Verdict.Pass);

        _doc5 = _app5!.ActiveDocument3D() as ksDocument3D
            ?? _app5.TransferInterface(_doc7, (int)ksAPITypeEnum.ksAPI5Auto, 0) as ksDocument3D;
        if (_doc5 is null)
        {
            Record("document_api5", "API5-вид того же документа получен", "null", Verdict.Fail);
            throw new ProbeStop();
        }

        Record("document_api5", "API5-вид того же документа получен", "ksDocument3D", Verdict.Pass);
    }

    private void Container()
    {
        var part = _doc7!.TopPart;
        _container = part as ISheetMetalContainer;
        if (_container is null)
        {
            Record("container_qi", "IPart7 приводится к ISheetMetalContainer", RuntimeName(part), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("container_qi", "IPart7 приводится к ISheetMetalContainer", "ISheetMetalContainer", Verdict.Pass);
        CheckInt("bodies_count_initial", 0, _container.SheetMetalBodies.Count);
        CheckInt("bends_count_initial", 0, _container.SheetMetalBends.Count);
        CheckInt("cuts_count_initial", 0, _container.SheetMetalCuts.Count);
        CheckInt("holes_count_initial", 0, _container.SheetMetalHoles.Count);
    }

    private void Body()
    {
        var sketch5 = MakeRectangleSketch(-20d, -20d, 20d, 20d, "sketch", "эскиз 40x40 на плоскости XOY создан");
        var sketch7 = Transfer7<ISketch>(sketch5, "sketch_to7", "эскиз перенесён в API7 (ISketch)");

        SheetMetalBody created;
        try
        {
            created = _container!.SheetMetalBodies.Add();
        }
        catch (COMException ex)
        {
            Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        _body = created as ISheetMetalBody;
        if (_body is null)
        {
            Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", RuntimeName(created), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", "ISheetMetalBody", Verdict.Pass);

        _body.Sketch = (Sketch)sketch7;
        _body.Thickness = 1.5d;
        _body.Radius = 2d;
        _body.Direction = ksDirectionTypeEnum.dtNormal;

        CheckBool("body_update", true, SafeBool(() => _body.Update()));
        CheckInt("bodies_count_after_add", 1, _container.SheetMetalBodies.Count);
        CheckDouble("body_thickness_read_back", 1.5d, SafeDouble(() => _body.Thickness));
        CheckDouble("body_radius_read_back", 2d, SafeDouble(() => _body.Radius));
        Record("body_profile_read", "Profile после задания Sketch (справка связи не описывает)",
            RuntimeName(SafeObject(() => _body.Profile)), Verdict.Info);
    }

    /// <summary>Order item 1: the edit route <c>kompas_edit_sheet_metal_body</c> rests on.</summary>
    /// <remarks>DOC: isheetmetalbody_thickness.html, ..._radius.html, ..._unfoldtype.html,
    /// ..._bendcoefficient.html, ..._bendvalue.html, ..._bendreduction.html, ..._bendtablepath.html
    /// History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void EditBody()
    {
        var before = Gabarit();
        Record("edit_gabarit_before", "габарит до правки толщины и радиуса", before, Verdict.Info);

        TrySet("edit_thickness_set", () => _body!.Thickness = 2d);
        TrySet("edit_radius_set", () => _body!.Radius = 3d);
        CheckBool("edit_update", true, SafeBool(() => _body!.Update()));
        CheckDouble("edit_thickness_read_back", 2d, SafeDouble(() => _body!.Thickness));
        CheckDouble("edit_radius_read_back", 3d, SafeDouble(() => _body!.Radius));
        Record("edit_gabarit_after", "габарит после правки", Gabarit(), Verdict.Info);

        // MEASURED: a flat body's Z extent IS its thickness, so this is the geometry check the edit
        // route needs and not a re-read of the property it just wrote.
        CheckDouble("edit_thickness_by_gabarit", 2d, ExtentZ());

        _thickness = SafeDouble(() => _body!.Thickness) ?? _thickness;
        _radius = SafeDouble(() => _body!.Radius) ?? _radius;
    }

    /// <summary>Order item 1, second half: every unfold-length type the page set names.</summary>
    /// <remarks>DOC: ksunfoldtypeenum.html — ksCoefficient 0, ksValueBend 1, ksDecreaseBend 2,
    /// ksTableBends 3. Each page of the value says the property is available only under its own type.
    /// History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void UnfoldTypes()
    {
        UnfoldTypeCase("coefficient", 0, () => _body!.BendCoefficient = 0.4d,
            () => _body!.BendCoefficient, 0.4d);
        UnfoldTypeCase("bend_value", 1, () => _body!.BendValue = 1.2d,
            () => _body!.BendValue, 1.2d);
        UnfoldTypeCase("bend_reduction", 2, () => _body!.BendReduction = 0.3d,
            () => _body!.BendReduction, 0.3d);

        TrySet("edit_unfoldtype_table_set", () => _body!.UnfoldType = (ksUnfoldTypeEnum)3);
        CheckBool("edit_unfoldtype_table_update", true, SafeBool(() => _body!.Update()));
        CheckInt("edit_unfoldtype_table_read_back", 3, SafeInt(() => _body!.UnfoldType));
        Record("edit_bendtablepath_read", "путь к таблице сгибов при типе «из таблицы»",
            Quote(SafeString(() => _body!.BendTablePath)), Verdict.Info);

        var tableTypeTaken = SafeInt(() => _body!.UnfoldType) == 3;
        Record("edit_table_blocks_coefficient_check",
            "недоступность сеттеров при типе «из таблицы» проверяется только если тип установился",
            tableTypeTaken ? "тип установился, проверка ниже" : "тип не установился: проверка не применима",
            Verdict.Info);
        if (tableTypeTaken)
        {
            // The page says the coefficient, value and reduction setters are UNAVAILABLE under the table
            // type, because the numbers come from the base. A refusal is the documented answer.
            CheckBool("edit_table_blocks_coefficient", true, Refused(() => _body!.BendCoefficient = 0.4d));
        }
        Record("edit_bendtablepath_not_attempted",
            "BendTablePath не записывался: страница называет тип BSTR и смысл «путь к таблице сгибов», "
            + "формат файла таблицы не описан", "не пробовался", Verdict.Info);

        TrySet("edit_unfoldtype_reset_set", () => _body!.UnfoldType = (ksUnfoldTypeEnum)0);
        TrySet("edit_unfoldtype_reset_coefficient", () => _body!.BendCoefficient = 0.4d);
        CheckBool("edit_unfoldtype_reset_update", true, SafeBool(() => _body!.Update()));
    }

    private void UnfoldTypeCase(string id, int type, Action setParam, Func<double> getParam, double expected)
    {
        TrySet("edit_unfoldtype_" + id + "_set", () => _body!.UnfoldType = (ksUnfoldTypeEnum)type);
        CheckBool("edit_unfoldtype_" + id + "_update", true, SafeBool(() => _body!.Update()));
        CheckInt("edit_unfoldtype_" + id + "_read_back", type, SafeInt(() => _body!.UnfoldType));
        TrySet("edit_unfoldparam_" + id + "_set", setParam);
        CheckBool("edit_unfoldparam_" + id + "_update", true, SafeBool(() => _body!.Update()));
        CheckDouble("edit_unfoldparam_" + id + "_read_back", expected, SafeDouble(getParam));
    }

    /// <summary>Order item 4: the cut and the hole routes the features order will write.</summary>
    /// <remarks>DOC: isheetmetalcuts_add.html, isheetmetalcut_body.html, ..._sketch.html, ..._cuttype.html,
    /// ..._depth.html, isheetmetalholes_add.html, isheetmetalhole_baseplane.html, ..._x.html, ..._y.html,
    /// ..._diameter.html, ..._cuttype.html, ..._depth.html. Both Add pages say: set the parameters, then
    /// IModelObject::Update. History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void CutAndHole()
    {
        _facesBeforeCut = FaceCount();
        Record("cut_faces_before", "граней у тела до выреза", _facesBeforeCut.ToString(CultureInfo.InvariantCulture),
            Verdict.Info);

        // Two documented inputs are measured, because the page names the sketch of a cut without saying
        // which support it must have: the XOY plane the body was built from, and a face of the body.
        var facesAfterPlaneCut = TryCut("cut", null, -15d, -15d, -5d, -5d);
        var planeCut = _cut;
        CheckBool("cut_faces_increased", true, facesAfterPlaneCut > _facesBeforeCut);

        var facesAfterFaceCut = TryCut("cut_face", FaceEntity(), 0d, 0d, 8d, 8d);
        CheckBool("cut_face_faces_increased", true, facesAfterFaceCut > facesAfterPlaneCut);
        if (facesAfterFaceCut <= facesAfterPlaneCut)
        {
            // Nothing cut through the face support either: the deletion step still measures the object.
            _cut = planeCut;
        }

        var facesAfterCut = FaceCount();

        var holes = _container!.SheetMetalHoles;
        var holesBefore = holes.Count;
        SheetMetalHole holeCreated;
        try
        {
            holeCreated = holes.Add();
        }
        catch (COMException ex)
        {
            Record("hole_add", "ISheetMetalHoles.Add() отдаёт ISheetMetalHole", Describe(ex), Verdict.Fail);
            return;
        }

        _hole = holeCreated as ISheetMetalHole;
        if (_hole is null)
        {
            Record("hole_add", "ISheetMetalHoles.Add() отдаёт ISheetMetalHole", RuntimeName(holeCreated), Verdict.Fail);
            return;
        }

        Record("hole_add", "ISheetMetalHoles.Add() отдаёт ISheetMetalHole", "ISheetMetalHole", Verdict.Pass);
        var face = FirstFace();
        Record("hole_baseplane", "опорная грань найдена", face is null ? "null" : "IModelObject", Verdict.Info);
        TrySet("hole_body_set", () => _hole.Body = (IKompasAPIObject)_body!);
        if (face is not null)
        {
            TrySet("hole_baseplane_set", () => _hole.BasePlane = face);
        }

        TrySet("hole_x_set", () => _hole.X = 5d);
        TrySet("hole_y_set", () => _hole.Y = 5d);
        TrySet("hole_diameter_set", () => _hole.Diameter = 6d);
        TrySet("hole_cuttype_set", () => _hole.CutType = ksHoleCutTypeEnum.ksHoleCutByDepth);
        TrySet("hole_depth_set", () => _hole.Depth = 1d);
        CheckBool("hole_update", true, SafeBool(() => _hole.Update()));
        CheckInt("hole_count_after_add", holesBefore + 1, holes.Count);
        CheckDouble("hole_depth_read_back", 1d, SafeDouble(() => _hole.Depth));
        CheckDouble("hole_diameter_read_back", 6d, SafeDouble(() => _hole.Diameter));
        CheckInt("hole_cuttype_read_back", 1, SafeInt(() => _hole.CutType));

        var facesAfterHole = FaceCount();
        Record("hole_faces_after", "граней у тела после отверстия",
            facesAfterHole.ToString(CultureInfo.InvariantCulture), Verdict.Info);
        CheckBool("hole_faces_increased", true, facesAfterHole > facesAfterCut);
    }

    /// <summary>One cut attempt with a named support; returns the face count after the attempt.</summary>
    /// <remarks>DOC: isheetmetalcuts_add.html, isheetmetalcut_body.html, ..._sketch.html, ..._cuttype.html,
    /// ..._depth.html. History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private int TryCut(string prefix, ksEntity? support, double x1, double y1, double x2, double y2)
    {
        var sketch5 = MakeRectangleSketch(x1, y1, x2, y2, prefix + "_sketch", "эскиз выреза создан", support);
        Record(prefix + "_sketch_support", "опора эскиза выреза",
            support is null ? "плоскость XOY" : "грань тела", Verdict.Info);
        var sketch7 = Transfer7<ISketch>(sketch5, prefix + "_sketch_to7", "эскиз выреза перенесён в API7");

        var cuts = _container!.SheetMetalCuts;
        var cutsBefore = cuts.Count;
        ISheetMetalCut? cut;
        try
        {
            cut = cuts.Add() as ISheetMetalCut;
        }
        catch (COMException ex)
        {
            Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", Describe(ex), Verdict.Fail);
            return FaceCount();
        }

        if (cut is null)
        {
            Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", "Add() → null", Verdict.Fail);
            return FaceCount();
        }

        Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", "ISheetMetalCut", Verdict.Pass);
        _cut = cut;
        TrySet(prefix + "_body_set", () => cut.Body = (IKompasAPIObject)_body!);
        TrySet(prefix + "_sketch_set", () => cut.Sketch = (Sketch)sketch7);
        TrySet(prefix + "_cuttype_set", () => cut.CutType = ksHoleCutTypeEnum.ksHoleCutByDepth);
        TrySet(prefix + "_depth_set", () => cut.Depth = 1d);
        CheckBool(prefix + "_update", true, SafeBool(() => cut.Update()));
        CheckInt(prefix + "_count", cutsBefore + 1, cuts.Count);
        CheckDouble(prefix + "_depth_read_back", 1d, SafeDouble(() => cut.Depth));
        CheckInt(prefix + "_cuttype_read_back", 1, SafeInt(() => cut.CutType));
        Record(prefix + "_body_read_back", "Body выреза читается назад",
            RuntimeName(ReadValue(() => cut.Body, prefix + "_body_read")), Verdict.Info);

        var faces = FaceCount();
        Record(prefix + "_faces_after", "граней у тела после выреза",
            faces.ToString(CultureInfo.InvariantCulture), Verdict.Info);
        return faces;
    }

    private void Bend()
    {
        var edge = FirstEdge();
        var edge7 = Transfer7<IModelObject>(edge, "edge_to7", "ребро тела перенесено в API7 (IModelObject)");

        var bends = _container!.SheetMetalBends;
        var countBefore = bends.Count;
        SheetMetalBend created;
        try
        {
            created = bends.Add();
        }
        catch (COMException ex)
        {
            Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        _bend1 = created as ISheetMetalBend;
        if (_bend1 is null)
        {
            Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", RuntimeName(created), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", "ISheetMetalBend", Verdict.Pass);

        _bend1.BendObject = edge7;
        _bend1.Angle = 90d;
        CheckBool("bend_update", true, SafeBool(() => _bend1.Update()));
        CheckInt("bends_count_after_add", countBefore + 1, bends.Count);
        CheckDouble("bend_angle_read_back", 90d, SafeDouble(() => _bend1.Angle));

        // DOC: isheetmetalbend_bendobjects.html (VARIANT), ..._direction.html (BOOL),
        // ..._internalradius.html (BOOL), ..._lengthtype.html (ksBendLengthTypeEnum), ..._length.html.
        // MEASURED: the read-back of BendObjects is compared by COM identity, because the edge comes back
        // as another wrapper and a name comparison is not available for an edge at all.
        var bendObjects = ReadValue(() => _bend1.BendObjects, "bend_bendobjects_read");
        Record("bend_bendobjects_type", "что вернул BendObjects", RuntimeName(bendObjects), Verdict.Info);
        CheckInt("bend_bendobjects_count", 1, ArrayLength(bendObjects));
        CheckBool("bend_bendobjects_is_requested_edge", true, ArrayContains(bendObjects, edge7));

        TrySet("bend_direction_set", () => _bend1.Direction = true);
        TrySet("bend_internalradius_set", () => _bend1.InternalRadius = true);
        TrySet("bend_lengthtype_set", () => _bend1.LengthType = ksBendLengthTypeEnum.ksBendLengthByContour);
        TrySet("bend_length_set", () => _bend1.Length = 5d);
        CheckBool("bend_update_after_fields", true, SafeBool(() => _bend1.Update()));
        CheckBool("bend_direction_read_back", true, SafeBool(() => _bend1.Direction));
        CheckBool("bend_internalradius_read_back", true, SafeBool(() => _bend1.InternalRadius));
        CheckInt("bend_lengthtype_read_back", 1, SafeInt(() => _bend1.LengthType));
        CheckDouble("bend_length_read_back", 5d, SafeDouble(() => _bend1.Length));
    }

    /// <summary>Order item 3, second half: a second bend on another edge of the same body.</summary>
    /// <remarks>DOC: isheetmetalbends_add.html. History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void SecondBend()
    {
        var edge = SecondEdge();
        var edge7 = Transfer7<IModelObject>(edge, "bend2_edge_to7", "второе ребро перенесено в API7");

        var bends = _container!.SheetMetalBends;
        var countBefore = bends.Count;
        SheetMetalBend created;
        try
        {
            created = bends.Add();
        }
        catch (COMException ex)
        {
            Record("bend2_add", "второй сгиб создан", Describe(ex), Verdict.Fail);
            return;
        }

        _bend2 = created as ISheetMetalBend;
        if (_bend2 is null)
        {
            Record("bend2_add", "второй сгиб создан", RuntimeName(created), Verdict.Fail);
            return;
        }

        Record("bend2_add", "второй сгиб создан", "ISheetMetalBend", Verdict.Pass);
        _bend2.BendObject = edge7;
        _bend2.Angle = 45d;
        CheckBool("bend2_update", true, SafeBool(() => _bend2.Update()));
        CheckInt("bend2_count", countBefore + 1, bends.Count);
        CheckDouble("bend2_angle_read_back", 45d, SafeDouble(() => _bend2.Angle));
        CheckDouble("bend1_angle_after_second", 90d, SafeDouble(() => _bend1!.Angle));
    }

    /// <summary>Order item 2: editing the body AFTER it has bends — the research left this open.</summary>
    /// <remarks>DOC: isheetmetalbody_thickness.html, imodelobject_update.html (IModelObject::Update is the
    /// call both Add pages require). History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void EditAfterBend()
    {
        var bendsBefore = _container!.SheetMetalBends.Count;
        var gabaritBefore = Gabarit();
        Record("edit2_gabarit_before", "габарит согнутого тела до правки толщины", gabaritBefore, Verdict.Info);

        TrySet("edit2_thickness_set", () => _body!.Thickness = 2.5d);
        CheckBool("edit2_update", true, SafeBool(() => _body!.Update()));
        CheckDouble("edit2_thickness_read_back", 2.5d, SafeDouble(() => _body!.Thickness));
        Record("edit2_body_state", "тело после правки читается: толщина и счётчик сгибов", "см. проверки выше",
            Verdict.Info);
        CheckInt("edit2_bends_count", bendsBefore, _container.SheetMetalBends.Count);
        CheckDouble("edit2_bend1_angle", 90d, SafeDouble(() => _bend1!.Angle));
        CheckDouble("edit2_bend2_angle", 45d, SafeDouble(() => _bend2!.Angle));
        Record("edit2_gabarit_after", "габарит согнутого тела после правки", Gabarit(), Verdict.Info);
        CheckBool("edit2_gabarit_changed", true, gabaritBefore != Gabarit());

        _thickness = SafeDouble(() => _body!.Thickness) ?? _thickness;
    }

    /// <summary>Order item 5: deletion through the route the product already uses.</summary>
    /// <remarks>DOC: ksdocument3d_deleteobject.html — «Удалить объект». The product calls it from
    /// Api5Session.Lifecycle.cs; the created objects are API7, so each is transferred to API5 first.
    /// History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void Delete()
    {
        var cutsBefore = _container!.SheetMetalCuts.Count;
        DeleteVia5(_cut, "delete_cut");
        CheckInt("delete_cut_count", cutsBefore - 1, _container.SheetMetalCuts.Count);
        CheckInt("delete_cut_bodies_kept", 1, _container.SheetMetalBodies.Count);
        var facesAfterCutDelete = FaceCount();
        Record("delete_cut_faces", "граней после удаления выреза",
            facesAfterCutDelete.ToString(CultureInfo.InvariantCulture), Verdict.Info);
        CheckInt("delete_cut_faces_restored", _facesBeforeCut, facesAfterCutDelete);

        var bendsBefore = _container.SheetMetalBends.Count;
        var gabaritBefore = Gabarit();
        DeleteVia5(_bend1, "delete_bend");
        CheckInt("delete_bend_count", bendsBefore - 1, _container.SheetMetalBends.Count);
        CheckInt("delete_bend_body_kept", 1, _container.SheetMetalBodies.Count);
        Record("delete_bend_gabarit", "габарит после удаления сгиба", Gabarit(), Verdict.Info);
        CheckBool("delete_bend_gabarit_changed", true, gabaritBefore != Gabarit());
    }

    private bool? DeleteVia5(object? api7Object, string id)
    {
        if (api7Object is null)
        {
            Record(id, "удаление документированным DeleteObject", "объекта нет: шаг не создал его", Verdict.Fail);
            return null;
        }

        object target = api7Object;
        try
        {
            if (_app5!.TransferInterface(api7Object, Api5Auto, 0) is { } transferred)
            {
                // INVARIANT: "the same object came back" is not a conversion; asking for the object's own
                // API answers the source object by design.
                var converted = !SameCom(transferred, api7Object);
                target = converted ? transferred : api7Object;
                Record(id + "_transfer", "объект передан в API5", converted ? "перенос выполнен" : "тот же объект",
                    Verdict.Info);
            }
            else
            {
                Record(id + "_transfer", "объект передан в API5", "null: в DeleteObject подан объект API7",
                    Verdict.Info);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id + "_transfer", "объект передан в API5", Describe(ex), Verdict.Info);
        }

        try
        {
            var deleted = _doc5!.DeleteObject(target);
            Record(id, "DeleteObject → true", deleted.ToString(), deleted ? Verdict.Pass : Verdict.Fail);
            return deleted;
        }
        catch (COMException ex)
        {
            Record(id, "DeleteObject → true", Describe(ex), Verdict.Fail);
            return null;
        }
    }

    /// <summary>The documented "Согнуть/Разогнуть" operation: its type comes as the first argument.</summary>
    /// <remarks>DOC: isheetmetalbendedstraightens_add.html, isheetmetalbendedstraighten_props.html.
    /// MEASURED: the body's <c>Straighten</c> flag alone changes no geometry, so the operation is the
    /// route that has to be tried before "the unfold is unreachable" can be said.
    /// History: docs/decisions/sheet-metal.md#probe</remarks>
    private void UnfoldOperation()
    {
        var operations = _container!.SheetMetalBendedStraightens;
        SheetMetalBendedStraighten created;
        try
        {
            created = operations.Add(ksObj3dTypeEnum.o3d_sheetMetalBendUnfold);
        }
        catch (COMException ex)
        {
            Record("unfold_op_add", "ISheetMetalBendedStraightens.Add(o3d_sheetMetalBendUnfold)", Describe(ex), Verdict.Fail);
            return;
        }

        if (created as ISheetMetalBendedStraighten is not { } operation)
        {
            Record("unfold_op_add", "Add(o3d_sheetMetalBendUnfold) отдаёт ISheetMetalBendedStraighten",
                RuntimeName(created), Verdict.Fail);
            return;
        }

        Record("unfold_op_add", "Add(o3d_sheetMetalBendUnfold) отдаёт ISheetMetalBendedStraighten",
            "ISheetMetalBendedStraighten", Verdict.Pass);

        if (FirstFace() is { } face)
        {
            TrySet("unfold_op_fixed_face", () => operation.FixedFace = face);
        }

        CheckBool("unfold_op_update", true, SafeBool(() => operation.Update()));
        CheckInt("unfold_op_count", 1, operations.Count);
        Record("unfold_op_is_created", "IsCreated после операции развёртки",
            Flag(_unfold is null ? null : SafeBool(() => _unfold.IsCreated)), Verdict.Info);
        Record("gabarit_after_unfold_op", "габарит после операции развёртки", Gabarit(), Verdict.Info);
    }

    /// <summary>Order item 6: the unfold as a round trip — unfold, fold back, unfold, save, reopen.</summary>
    /// <remarks>DOC: isheetmetalbendunfoldparameters_unfold.html, ..._unfoldplane.html, ..._fixedfaces.html,
    /// ..._updateparam.html, ..._iscreated.html, ..._deleteparam.html. MEASURED: UpdateParam() answers FALSE
    /// even when the unfold IS created, so success is judged by IsCreated and the geometry.
    /// History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private void Unfold()
    {
        var unfold = _unfold = _container!.SheetMetalBendUnfoldParameters;
        Record("unfold_params_created", "IsCreated у параметров развёртки до разгибания",
            Flag(SafeBool(() => unfold.IsCreated)), Verdict.Info);
        Record("unfold_flag", "Unfold до разгибания", Flag(SafeBool(() => unfold.Unfold)), Verdict.Info);

        var bent = Gabarit();
        Record("gabarit_bent", "габарит согнутого тела", bent, Verdict.Info);

        if (Plane7() is { } plane)
        {
            TrySet("unfold_plane_set", () => unfold.UnfoldPlane = plane);
        }

        if (FirstFace() is { } fixedFace)
        {
            TrySet("unfold_fixed_faces_set", () => unfold.FixedFaces = new object[] { fixedFace });
        }

        TrySet("unfold_set", () => unfold.Unfold = true);
        Record("unfold_update_param", "UpdateParam() при заданных входах",
            Flag(SafeBool(() => unfold.UpdateParam())), Verdict.Info);
        CheckBool("unfold_is_created", true, SafeBool(() => unfold.IsCreated));
        CheckBool("unfold_flag_read_back", true, SafeBool(() => unfold.Unfold));
        var fixedFaces = ReadValue(() => unfold.FixedFaces, "unfold_fixed_faces_read");
        Record("unfold_fixed_faces_type", "что вернул FixedFaces", RuntimeName(fixedFaces), Verdict.Info);
        CheckInt("unfold_fixed_faces_count", 1, ArrayLength(fixedFaces));
        CheckBool("unfold_plane_nonempty", true, SafeObject(() => unfold.UnfoldPlane) is not null);
        var flat = Gabarit();
        Record("gabarit_after_unfold", "габарит после Unfold + UpdateParam", flat, Verdict.Info);
        CheckBool("gabarit_changed_by_unfold", true, bent != flat);
        CheckDouble("unfold_extent_by_normal", _thickness, ExtentZ());

        // Folding back is the same documented pair: the flag plus UpdateParam(). The geometry must
        // return to the bent state, otherwise "unfold = false" would be a claim nobody verified.
        TrySet("fold_back_set", () => unfold.Unfold = false);
        Record("fold_back_update_param", "UpdateParam() при Unfold = false",
            Flag(SafeBool(() => unfold.UpdateParam())), Verdict.Info);
        CheckBool("fold_back_flag_read_back", false, SafeBool(() => unfold.Unfold));
        Record("fold_back_is_created", "IsCreated после Unfold = false",
            Flag(SafeBool(() => unfold.IsCreated)), Verdict.Info);
        var foldedBack = Gabarit();
        Record("gabarit_after_fold_back", "габарит после Unfold = false", foldedBack, Verdict.Info);
        CheckBool("fold_back_gabarit_returns", true, foldedBack == bent);

        TrySet("unfold_again_set", () => unfold.Unfold = true);
        Record("unfold_again_update_param", "UpdateParam() при повторном Unfold = true",
            Flag(SafeBool(() => unfold.UpdateParam())), Verdict.Info);
        CheckBool("unfold_again_is_created", true, SafeBool(() => unfold.IsCreated));
        var flatAgain = Gabarit();
        Record("gabarit_after_unfold_again", "габарит после повторной развёртки", flatAgain, Verdict.Info);
        CheckBool("unfold_again_gabarit", true, flatAgain == flat);
    }

    /// <summary>Applies a write whose refusal is a measurement, not a crash.</summary>
    private void TrySet(string id, Action write)
    {
        try
        {
            write();
            Record(id, "запись принята", "принята", Verdict.Pass);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, "запись принята", Describe(ex), Verdict.Fail);
        }
    }

    private void Reopen()
    {
        // The counts are taken from the model as it stands, not from the plan: a step that refused
        // leaves the collection as it was, and a hard-coded number would report that refusal twice.
        var bendsAtSave = _container!.SheetMetalBends.Count;
        var cutsAtSave = _container.SheetMetalCuts.Count;
        var holesAtSave = _container.SheetMetalHoles.Count;
        Record("save_counts", "счётчики сгибов/вырезов/отверстий на момент сохранения",
            Count(bendsAtSave) + "/" + Count(cutsAtSave) + "/" + Count(holesAtSave), Verdict.Info);

        var file = Path.Combine(Path.GetTempPath(),
            "sheet-metal-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".m3d");

        try
        {
            _doc7!.SaveAs(file);
        }
        catch (COMException ex)
        {
            Record("save", "деталь сохранена", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (!File.Exists(file))
        {
            Record("save", "деталь сохранена", "файла нет: " + file, Verdict.Fail);
            throw new ProbeStop();
        }

        Record("save", "деталь сохранена", file, Verdict.Pass);

        IKompasDocument reopened;
        try
        {
            reopened = _app7!.Documents.Open(file, false, true);
        }
        catch (COMException ex)
        {
            Record("reopen", "сохранённый файл открывается", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (reopened as IKompasDocument3D is not { TopPart: { } part } || part as ISheetMetalContainer is not { } container)
        {
            Record("reopen", "сохранённый файл открывается как листовая деталь", RuntimeName(reopened), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("reopen", "сохранённый файл открывается как листовая деталь", "ISheetMetalContainer", Verdict.Pass);
        CheckInt("reopen_bodies_count", 1, container.SheetMetalBodies.Count);
        CheckInt("reopen_bends_count", bendsAtSave, container.SheetMetalBends.Count);
        CheckInt("reopen_cuts_count", cutsAtSave, container.SheetMetalCuts.Count);
        CheckInt("reopen_holes_count", holesAtSave, container.SheetMetalHoles.Count);

        var body = BodyAt(container);
        if (body is null)
        {
            Record("reopen_body_read", "тело читается после открытия", "элемент не приводится к ISheetMetalBody", Verdict.Fail);
            return;
        }

        CheckDouble("reopen_thickness", _thickness, SafeDouble(() => body.Thickness));
        CheckDouble("reopen_radius", _radius, SafeDouble(() => body.Radius));

        var unfold = container.SheetMetalBendUnfoldParameters;
        CheckBool("reopen_unfold_is_created", true, SafeBool(() => unfold.IsCreated));
        CheckBool("reopen_unfold_flag", true, SafeBool(() => unfold.Unfold));
        var reopenFaces = ReadValue(() => unfold.FixedFaces, "reopen_unfold_fixed_faces_read");
        CheckInt("reopen_unfold_fixed_faces", 1, ArrayLength(reopenFaces));
        CheckBool("reopen_unfold_plane_nonempty", true, SafeObject(() => unfold.UnfoldPlane) is not null);
        Record("reopen_gabarit", "габарит после открытия", GabaritOn(_app5?.ActiveDocument3D() as ksDocument3D),
            Verdict.Info);
        CheckDouble("reopen_extent_by_normal", _thickness,
            ExtentZOn(_app5?.ActiveDocument3D() as ksDocument3D));

        Record("deleteparam_call", "DeleteParam() после открытия",
            Flag(SafeBool(() => unfold.DeleteParam())), Verdict.Info);
        CheckBool("deleteparam_is_created_after", false, SafeBool(() => unfold.IsCreated));
        Record("deleteparam_unfold_after", "Unfold после DeleteParam", Flag(SafeBool(() => unfold.Unfold)),
            Verdict.Info);
    }

    /// <summary>Opens one saved file and reports what the model holds — the check that splits a lost
    /// write from a failed read.</summary>
    public int RunInspect(string file)
    {
        try
        {
            Launch();
            Inspect(file);
        }
        catch (ProbeStop)
        {
        }
        catch (Exception ex)
        {
            Record("probe", "проба доходит до конца", Describe(ex), Verdict.Fail);
        }

        Write();
        return Failed ? 1 : 0;
    }

    private void Inspect(string file)
    {
        Record("inspect_file", "файл существует", file + (File.Exists(file) ? "" : " — НЕТ"),
            File.Exists(file) ? Verdict.Pass : Verdict.Fail);
        if (!File.Exists(file))
        {
            return;
        }

        IKompasDocument opened;
        try
        {
            opened = _app7!.Documents.Open(file, false, true);
        }
        catch (COMException ex)
        {
            Record("inspect_open", "файл открывается", Describe(ex), Verdict.Fail);
            return;
        }

        if (opened as IKompasDocument3D is not { TopPart: { } part } || part as ISheetMetalContainer is not { } container)
        {
            Record("inspect_open", "файл открывается как листовая деталь", RuntimeName(opened), Verdict.Fail);
            return;
        }

        Record("inspect_open", "файл открывается как листовая деталь", "ISheetMetalContainer", Verdict.Pass);
        Record("inspect_counters", "счётчики тел/сгибов/вырезов/отверстий",
            Count(container.SheetMetalBodies.Count) + "/" + Count(container.SheetMetalBends.Count) + "/"
            + Count(container.SheetMetalCuts.Count) + "/" + Count(container.SheetMetalHoles.Count),
            Verdict.Info);

        var body = BodyAt(container);
        Record("inspect_body", "тело читается", body is null ? "нет" : "ISheetMetalBody",
            body is null ? Verdict.Fail : Verdict.Pass);
        if (body is null)
        {
            return;
        }

        Record("inspect_values", "толщина/радиус/способ/поле",
            Num(SafeDouble(() => body.Thickness) ?? double.NaN) + " / "
            + Num(SafeDouble(() => body.Radius) ?? double.NaN) + " / "
            + SafeInt(() => body.UnfoldType) + " / "
            + Num(SafeDouble(() => body.BendReduction) ?? double.NaN), Verdict.Info);
    }

    /// <summary>Runs only the persistence experiment: does a body reach the file without the later steps?</summary>
    public int RunPersist()
    {
        try
        {
            Launch();
            Persist();
        }
        catch (ProbeStop)
        {
        }
        catch (Exception ex)
        {
            Record("probe", "проба доходит до конца", Describe(ex), Verdict.Fail);
        }

        Write();
        return Failed ? 1 : 0;
    }

    /// <summary>Which part of the product's save/reopen route loses the body.</summary>
    /// <remarks>INVARIANT: each case is a fresh document and a fresh file, so a case cannot inherit the
    /// previous one's state. DOC: isheetmetalbodies_add.html, ikompasdocument_saveas.html,
    /// ksdocument3d.html (SaveAs). History: docs/decisions/sheet-metal.md#body-tools</remarks>
    private void Persist()
    {
        PersistCase("p1_api5_save", rebuild: false, saveThroughApi7: false);
        PersistCase("p2_api7_save", rebuild: false, saveThroughApi7: true);
        PersistCase("p3_api5_save_after_rebuild", rebuild: true, saveThroughApi7: false);
    }

    private void PersistCase(string id, bool rebuild, bool saveThroughApi7)
    {
        IKompasDocument added;
        try
        {
            added = _app7!.Documents.Add(DocumentTypeEnum.ksDocumentPart, false);
        }
        catch (COMException ex)
        {
            Record(id + "_document", "новая деталь создана", Describe(ex), Verdict.Fail);
            return;
        }

        if (added as IKompasDocument3D is not { } doc7)
        {
            Record(id + "_document", "новая деталь создана", RuntimeName(added), Verdict.Fail);
            return;
        }

        _doc7 = doc7;
        _doc5 = _app5!.TransferInterface(doc7, Api5Auto, 0) as ksDocument3D;
        if (_doc5 is null)
        {
            Record(id + "_document_api5", "API5-вид детали получен", "null", Verdict.Fail);
            return;
        }

        if (doc7.TopPart as ISheetMetalContainer is not { } container)
        {
            Record(id + "_container", "IPart7 приводится к ISheetMetalContainer", RuntimeName(doc7.TopPart),
                Verdict.Fail);
            return;
        }

        var sketch5 = MakeRectangleSketch(-20d, -20d, 20d, 20d, id + "_sketch", "эскиз 40x40 на XOY создан");
        var sketch7 = Transfer7<ISketch>(sketch5, id + "_sketch_to7", "эскиз перенесён в API7");

        if (container.SheetMetalBodies.Add() is not ISheetMetalBody body)
        {
            Record(id + "_body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", "не приводится",
                Verdict.Fail);
            return;
        }

        body.Sketch = (Sketch)sketch7;
        body.Thickness = 2d;
        body.Radius = 3d;
        body.Direction = ksDirectionTypeEnum.dtNormal;
        body.UnfoldType = ksUnfoldTypeEnum.ksDecreaseBend;
        body.BendReduction = 0.3d;
        CheckBool(id + "_update", true, SafeBool(() => body.Update()));
        CheckInt(id + "_bodies_in_session", 1, container.SheetMetalBodies.Count);
        CheckDouble(id + "_extent_in_session", 2d, ExtentZOn(_doc5));

        if (rebuild)
        {
            try
            {
                _doc5.RebuildDocument();
                Record(id + "_rebuild", "деталь перестроена", "RebuildDocument() вызван", Verdict.Info);
            }
            catch (COMException ex)
            {
                Record(id + "_rebuild", "деталь перестроена", Describe(ex), Verdict.Fail);
            }

            // MEASURED: the container read before the rebuild answers 0 afterwards, so it is taken again
            // from the document — otherwise the case would measure a stale COM object, not the model.
            container = doc7.TopPart as ISheetMetalContainer;
            Record(id + "_bodies_after_rebuild", "счётчик тел после перестроения",
                container is null ? "контейнер не прочитан" : Count(container.SheetMetalBodies.Count),
                Verdict.Info);
            CheckDouble(id + "_extent_after_rebuild", 2d, ExtentZOn(_doc5));
        }

        var file = Path.Combine(Path.GetTempPath(),
            "smb-persist-" + id + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".m3d");
        try
        {
            if (saveThroughApi7)
            {
                doc7.SaveAs(file);
            }
            else
            {
                _doc5.SaveAs(file);
            }
        }
        catch (COMException ex)
        {
            Record(id + "_save", "деталь сохранена", Describe(ex), Verdict.Fail);
            return;
        }

        var exists = File.Exists(file);
        Record(id + "_save", "деталь сохранена", (exists ? "файл есть: " : "файла нет: ") + file,
            exists ? Verdict.Pass : Verdict.Fail);
        if (!exists)
        {
            return;
        }

        IKompasDocument reopened;
        try
        {
            reopened = _app7!.Documents.Open(file, false, true);
        }
        catch (COMException ex)
        {
            Record(id + "_reopen", "сохранённый файл открывается", Describe(ex), Verdict.Fail);
            return;
        }

        if (reopened as IKompasDocument3D is not { TopPart: { } part }
            || part as ISheetMetalContainer is not { } reContainer)
        {
            Record(id + "_reopen", "файл открывается как листовая деталь", RuntimeName(reopened), Verdict.Fail);
            return;
        }

        CheckInt(id + "_reopen_bodies", 1, reContainer.SheetMetalBodies.Count);
        var reBody = BodyAt(reContainer);
        CheckDouble(id + "_reopen_thickness", 2d, reBody is null ? null : SafeDouble(() => reBody.Thickness));
        CheckDouble(id + "_reopen_radius", 3d, reBody is null ? null : SafeDouble(() => reBody.Radius));
    }

    /// <summary>Order item 7: what the kernel answers on input the tools must refuse themselves.</summary>
    /// <remarks>INVARIANT: these answers are recorded as INFO with the kernel's own result — the question is
    /// what it does, not what the product should do. DOC: isheetmetalbodies_add.html,
    /// isheetmetalbends_add.html, isheetmetalcuts_add.html. History: docs/decisions/sheet-metal.md#probe-routes
    /// </remarks>
    private void Refusals()
    {
        var sketch5 = MakeRectangleSketch(60d, -10d, 80d, 10d, "body2_sketch", "эскиз второго тела 20x20 создан");
        var sketch7 = Transfer7<ISketch>(sketch5, "body2_sketch_to7", "эскиз второго тела перенесён в API7");

        SheetMetalBody created;
        try
        {
            created = _container!.SheetMetalBodies.Add();
        }
        catch (COMException ex)
        {
            Record("refusal_body2_add", "второе тело создано", Describe(ex), Verdict.Fail);
            return;
        }

        _body2 = created as ISheetMetalBody;
        if (_body2 is null)
        {
            Record("refusal_body2_add", "второе тело создано", RuntimeName(created), Verdict.Fail);
            return;
        }

        _body2.Sketch = (Sketch)sketch7;
        _body2.Thickness = 1.5d;
        _body2.Radius = 2d;
        _body2.Direction = ksDirectionTypeEnum.dtNormal;
        Record("refusal_body2_add", "второе тело создано", "Update = " + Flag(SafeBool(() => _body2.Update())),
            Verdict.Info);
        Record("refusal_body2_count", "счётчик тел после второго тела", Count(_container.SheetMetalBodies.Count),
            Verdict.Info);

        RefusalBodyWrite("thickness_zero", () => _body2.Thickness = 0d);
        RefusalBodyWrite("thickness_negative", () => _body2.Thickness = -1.5d);
        RefusalBodyWrite("radius_zero", () => _body2.Radius = 0d);

        // Restored so the bend below meets a valid body; the numbers come from the documented body pages.
        _body2.Thickness = 1.5d;
        _body2.Radius = 2d;
        _body2.Update();
        Record("refusal_body2_restored", "второе тело восстановлено после негодных записей",
            "толщина " + Num(SafeDouble(() => _body2.Thickness) ?? double.NaN)
            + ", радиус " + Num(SafeDouble(() => _body2.Radius) ?? double.NaN), Verdict.Info);

        RefusalForeignEdge();
        RefusalEmptySketchCut();
    }

    private void RefusalBodyWrite(string id, Action write)
    {
        try
        {
            write();
            Record("refusal_" + id + "_set", "запись негодного значения принята свойством", "принята", Verdict.Info);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("refusal_" + id + "_set", "запись негодного значения принята свойством", Describe(ex), Verdict.Info);
        }

        Record("refusal_" + id + "_update", "Update() с негодным значением",
            Flag(SafeBool(() => _body2!.Update())), Verdict.Info);
        Record("refusal_" + id + "_thickness", "Thickness после Update()",
            Num(SafeDouble(() => _body2!.Thickness) ?? double.NaN), Verdict.Info);
        Record("refusal_" + id + "_radius", "Radius после Update()",
            Num(SafeDouble(() => _body2!.Radius) ?? double.NaN), Verdict.Info);
        Record("refusal_" + id + "_is_created",
            "IsCreated у ISheetMetalBody: справка v24 такого свойства не описывает", "не читается", Verdict.Info);
        Record("refusal_" + id + "_bodies_count", "счётчик тел", Count(_container!.SheetMetalBodies.Count),
            Verdict.Info);
        Record("refusal_" + id + "_gabarit", "габарит", Gabarit(), Verdict.Info);
    }

    /// <summary>A bend whose edge belongs to another body: the API7 route has no body argument.</summary>
    private void RefusalForeignEdge()
    {
        var edge = EdgeOfBody(1);
        if (edge is null)
        {
            Record("refusal_foreign_edge_source", "ребро второго тела найдено",
                "BodyCollection() без второго элемента", Verdict.Info);
            return;
        }

        var edge7 = Transfer7<IModelObject>(edge, "refusal_foreign_edge_to7", "ребро второго тела перенесено в API7");
        var bends = _container!.SheetMetalBends;
        var countBefore = bends.Count;
        ISheetMetalBend? bend;
        try
        {
            bend = bends.Add() as ISheetMetalBend;
        }
        catch (COMException ex)
        {
            Record("refusal_foreign_edge_add", "сгиб по ребру другого тела", Describe(ex), Verdict.Info);
            return;
        }

        if (bend is null)
        {
            Record("refusal_foreign_edge_add", "сгиб по ребру другого тела", "Add() → null", Verdict.Info);
            return;
        }

        try
        {
            bend.BendObject = edge7;
            bend.Angle = 45d;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("refusal_foreign_edge_fields", "поля сгиба по чужому ребру", Describe(ex), Verdict.Info);
        }

        Record("refusal_foreign_edge_update", "Update() сгиба по ребру другого тела",
            Flag(SafeBool(() => bend.Update())), Verdict.Info);
        Record("refusal_foreign_edge_bends_count", "счётчик сгибов: отказ или +1",
            Count(countBefore) + " -> " + Count(bends.Count), Verdict.Info);
        Record("refusal_foreign_edge_gabarit", "габарит модели", Gabarit(), Verdict.Info);
    }

    private void RefusalEmptySketchCut()
    {
        var sketch5 = MakeEmptySketch();
        if (sketch5 is null)
        {
            return;
        }

        var sketch7 = Transfer7<ISketch>(sketch5, "refusal_empty_sketch_to7", "пустой эскиз перенесён в API7");
        var cuts = _container!.SheetMetalCuts;
        var countBefore = cuts.Count;
        ISheetMetalCut? cut;
        try
        {
            cut = cuts.Add() as ISheetMetalCut;
        }
        catch (COMException ex)
        {
            Record("refusal_empty_sketch_add", "вырез по пустому эскизу", Describe(ex), Verdict.Info);
            return;
        }

        if (cut is null)
        {
            Record("refusal_empty_sketch_add", "вырез по пустому эскизу", "Add() → null", Verdict.Info);
            return;
        }

        try
        {
            cut.Body = (IKompasAPIObject)_body!;
            cut.Sketch = (Sketch)sketch7;
            cut.CutType = ksHoleCutTypeEnum.ksHoleCutByDepth;
            cut.Depth = 1d;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("refusal_empty_sketch_fields", "поля выреза по пустому эскизу", Describe(ex), Verdict.Info);
        }

        Record("refusal_empty_sketch_update", "Update() выреза по пустому эскизу",
            Flag(SafeBool(() => cut.Update())), Verdict.Info);
        Record("refusal_empty_sketch_cuts_count", "счётчик вырезов: осталась ли запись",
            Count(countBefore) + " -> " + Count(cuts.Count), Verdict.Info);
    }

    // ---------------------------------------------------------------------------------------------
    // Cut probe (order SHEET_METAL_CUT_PROBE): a documented route that puts a cut sketch ON a face
    // ---------------------------------------------------------------------------------------------

    /// <summary>Runs the cut probe alone: one flat body, then the face-support routes and refusals.</summary>
    /// <remarks>DOC: ksfacedefinition_getentity.html, kssketchdefinition_setplane.html, isketch_plane.html,
    /// isheetmetalcut_profile.html, isheetmetalcuts_add.html, ksholecuttypeenum.html.
    /// History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    public int RunCut()
    {
        try
        {
            Launch();
            Document();
            Container();
            Body();
            CutProbe();
        }
        catch (ProbeStop)
        {
            // The reason is already recorded by the step that stopped the probe.
        }
        catch (Exception ex)
        {
            Record("probe", "проба доходит до конца", Describe(ex), Verdict.Fail);
        }

        Write();
        return Failed ? 1 : 0;
    }

    /// <summary>Order items 1-5: face support, cut by geometry, Profile, reopen, refusals.</summary>
    private void CutProbe()
    {
        var face = PlanarFace(0, "face");
        var support = face is null ? null : FaceSupport(face, "face_entity");
        if (face is null || support is null)
        {
            return;
        }

        var faces = FaceCount();
        Record("cut_face_faces_before", "граней у тела до вырезов", Count(faces), Verdict.Info);
        Record("cut_face_gabarit_before", "габарит до вырезов", Gabarit(), Verdict.Info);

        // Item 2, through the sheet: ksHoleCutByWidth is the through mode, ksHoleCutByDepth the other.
        var sketch5 = MakeFaceSketch(face, support, -15d, -15d, -5d, -5d, "cut_face");
        var sketch7 = Transfer7<ISketch>(sketch5, "cut_face_to7", "эскиз на грани перенесён в API7");
        var afterThrough = TryFaceCut("cut_face_through", sketch7, 0, 0d, false);
        CheckBool("cut_face_through_faces_increased", true, afterThrough > faces);
        faces = afterThrough;

        // Item 2, by depth: the second mode the enum names.
        var depth5 = MakeFaceSketch(face, support, 5d, 5d, 15d, 15d, "cut_depth");
        var depth7 = Transfer7<ISketch>(depth5, "cut_depth_to7", "эскиз на грани для выреза на глубину перенесён");
        var afterDepth = TryFaceCut("cut_face_depth", depth7, 1, 0.5d, false);
        CheckBool("cut_face_depth_faces_increased", true, afterDepth > faces);
        faces = afterDepth;

        // Item 1, second documented path: ISketch.Plane takes an IModelObject, so the face itself.
        var plane5 = MakeRectangleSketch(-5d, 5d, 5d, 15d, "cut_api7_sketch", "эскиз на XOY для опоры-грани создан");
        var plane7 = Transfer7<ISketch>(plane5, "cut_api7_to7", "эскиз перенесён в API7");
        var face7 = FaceModel(face, "cut_api7_face_to7");
        if (face7 is not null)
        {
            TrySet("cut_api7_plane_set", () => plane7.Plane = face7);
            CheckBool("cut_api7_plane_is_face", true, SameCom(SafeObject(() => plane7.Plane), face7));
            var afterApi7 = TryFaceCut("cut_api7", plane7, 0, 0d, false);
            CheckBool("cut_api7_faces_increased", true, afterApi7 > faces);
            faces = afterApi7;
        }

        // Item 3: Profile names only IModelObject; the sketch is that type by its documented hierarchy.
        var profile5 = MakeFaceSketch(face, support, -15d, 5d, -5d, 15d, "cut_profile");
        var profile7 = Transfer7<ISketch>(profile5, "cut_profile_to7", "эскиз для Profile перенесён в API7");
        var afterProfile = TryFaceCut("cut_profile", profile7, 0, 0d, true);
        CheckBool("cut_profile_faces_increased", true, afterProfile > faces);
        faces = afterProfile;

        // Item 1 again, but with the support set before the contour exists: the late write above cannot
        // move a contour that was already built, so only this order can judge the API7 property.
        if (face7 is not null)
        {
            var early7 = MakeApi7FaceSketch(face7, -5d, -15d, 5d, -5d, "cut_api7_early");
            if (early7 is not null)
            {
                var afterEarly = TryFaceCut("cut_api7_early", early7, 0, 0d, false);
                CheckBool("cut_api7_early_faces_increased", true, afterEarly > faces);
            }
        }

        ReopenCut();
        CutRefusals(face, support);
    }

    /// <summary>A sketch whose support is set through ISketch.Plane before any geometry exists.</summary>
    /// <remarks>MEASURED: setting Plane on a sketch that already carries a contour leaves that contour on its
    /// old plane, so only this order judges the property. DOC: isketch_plane.html.
    /// History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private ISketch? MakeApi7FaceSketch(IModelObject face7, double x1, double y1, double x2, double y2, string prefix)
    {
        var part = Part5();
        if (part.NewEntity(ObjSketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            Record(prefix, "эскиз без опоры создан", "NewEntity(o3d_sketch) → null", Verdict.Fail);
            return null;
        }

        var sketch7 = Transfer7<ISketch>(sketch, prefix + "_to7", "эскиз перенесён в API7 до построения");
        TrySet(prefix + "_plane_set", () => sketch7.Plane = face7);
        CheckBool(prefix + "_plane_is_face", true, SameCom(SafeObject(() => sketch7.Plane), face7));

        sketch.Create();
        if (definition.BeginEdit() is not ksDocument2D editor)
        {
            Record(prefix, "эскиз на грани создан", "BeginEdit() → не ksDocument2D", Verdict.Fail);
            return null;
        }

        editor.ksLineSeg(x1, y1, x2, y1, 1);
        editor.ksLineSeg(x2, y1, x2, y2, 1);
        editor.ksLineSeg(x2, y2, x1, y2, 1);
        editor.ksLineSeg(x1, y2, x1, y1, 1);
        definition.EndEdit();
        Record(prefix, "эскиз на грани создан", "4 отрезка", Verdict.Pass);
        CheckBool(prefix + "_plane_is_face_after_create", true, SameCom(SafeObject(() => sketch7.Plane), face7));
        CheckBool(prefix + "_plane_is_not_xoy_after_create", false,
            SameCom(SafeObject(() => sketch7.Plane), Part5().GetDefaultEntity(PlaneXoy)));
        return sketch7;
    }

    /// <summary>The first planar face of a body, read the way the collection page documents it.</summary>
    /// <remarks>DOC: ksfacecollection_getbyindex.html returns ksFaceDefinition; ksfacedefinition_isplanar.html.
    /// History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private ksFaceDefinition? PlanarFace(int bodyIndex, string id)
    {
        var part = Part5();
        if (part.BodyCollection() is not ksBodyCollection bodies)
        {
            Record(id + "_source", "массив тел получен", "BodyCollection() → null", Verdict.Fail);
            return null;
        }

        bodies.refresh();
        if (bodies.GetCount() <= bodyIndex || bodies.GetByIndex(bodyIndex) is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            Record(id + "_source", "тело и массив граней получены", "тела или граней нет", Verdict.Fail);
            return null;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            var element = faces.GetByIndex(f);
            var face = element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;
            if (face is null)
            {
                Record(id + "_source", "элемент массива приводится к ksFaceDefinition", RuntimeName(element),
                    Verdict.Fail);
                return null;
            }

            if (SafeBool(face.IsPlanar) == true)
            {
                Record(id + "_source", "плоская грань тела найдена", "тело " + bodyIndex + ", грань " + f,
                    Verdict.Pass);
                return face;
            }
        }

        Record(id + "_source", "плоская грань тела найдена", "плоских граней нет", Verdict.Fail);
        return null;
    }

    /// <summary>The face as a model entity — the documented conversion a sketch support needs.</summary>
    /// <remarks>DOC: ksfacedefinition_getentity.html — "Получить указатель на интерфейс грани ksEntity
    /// или IEntity". History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private ksEntity? FaceSupport(ksFaceDefinition face, string id)
    {
        object? entity;
        try
        {
            entity = face.GetEntity();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, "GetEntity() отдаёт ksEntity", Describe(ex), Verdict.Fail);
            return null;
        }

        if (entity as ksEntity is not { } support)
        {
            Record(id, "GetEntity() отдаёт ksEntity", RuntimeName(entity), Verdict.Fail);
            return null;
        }

        Record(id, "GetEntity() отдаёт ksEntity", "ksEntity", Verdict.Pass);
        return support;
    }

    /// <summary>The same face as an API7 object — the input ISketch.Plane and ISheetMetalCut.Profile take.</summary>
    private IModelObject? FaceModel(ksFaceDefinition face, string id)
    {
        try
        {
            return _app5!.TransferInterface(face, Api7Dual, 0) as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, "грань перенесена в API7 (IModelObject)", Describe(ex), Verdict.Fail);
            return null;
        }
    }

    /// <summary>A contour sketch on a face, with the support read back and compared with XOY.</summary>
    /// <remarks>INVARIANT: the read-back support must be the face entity and not the XOY plane, so a sketch
    /// that silently fell back cannot pass. DOC: kssketchdefinition_setplane.html, ..._getplane.html.
    /// History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private ksEntity MakeFaceSketch(ksFaceDefinition face, ksEntity support, double x1, double y1, double x2,
        double y2, string prefix)
    {
        var sketch = MakeRectangleSketch(x1, y1, x2, y2, prefix, "эскиз на грани создан", support);
        var plane = (sketch.GetDefinition() as ksSketchDefinition)?.GetPlane();
        CheckBool(prefix + "_plane_is_face", true, SameCom(plane, support));
        CheckBool(prefix + "_plane_is_not_xoy", false, SameCom(plane, Part5().GetDefaultEntity(PlaneXoy)));
        Record(prefix + "_face", "опора эскиза - грань", RuntimeName(face), Verdict.Info);
        return sketch;
    }

    /// <summary>One cut attempt with a named sketch or profile; returns the face count after it.</summary>
    /// <remarks>DOC: isheetmetalcuts_add.html, isheetmetalcut_body.html, ..._sketch.html, ..._profile.html,
    /// ..._cuttype.html, ..._depth.html. History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private int TryFaceCut(string prefix, ISketch sketch7, int cutType, double depth, bool byProfile)
    {
        var cuts = _container!.SheetMetalCuts;
        var countBefore = cuts.Count;
        ISheetMetalCut? cut;
        try
        {
            cut = cuts.Add() as ISheetMetalCut;
        }
        catch (COMException ex)
        {
            Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", Describe(ex), Verdict.Fail);
            return FaceCount();
        }

        if (cut is null)
        {
            Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", "Add() → null", Verdict.Fail);
            return FaceCount();
        }

        Record(prefix + "_add", "ISheetMetalCuts.Add() отдаёт ISheetMetalCut", "ISheetMetalCut", Verdict.Pass);
        TrySet(prefix + "_body_set", () => cut.Body = (IKompasAPIObject)_body!);
        if (byProfile)
        {
            TrySet(prefix + "_profile_set", () => cut.Profile = (IModelObject)sketch7);
            Record(prefix + "_profile_read_back", "Profile читается назад",
                RuntimeName(ReadValue(() => cut.Profile, prefix + "_profile_read")), Verdict.Info);
        }
        else
        {
            TrySet(prefix + "_sketch_set", () => cut.Sketch = (Sketch)sketch7);
        }

        TrySet(prefix + "_cuttype_set", () => cut.CutType = (ksHoleCutTypeEnum)cutType);
        if (cutType == 1)
        {
            TrySet(prefix + "_depth_set", () => cut.Depth = depth);
        }

        CheckBool(prefix + "_update", true, SafeBool(() => cut.Update()));
        CheckInt(prefix + "_count", countBefore + 1, cuts.Count);
        CheckInt(prefix + "_cuttype_read_back", cutType, SafeInt(() => cut.CutType));
        if (cutType == 1)
        {
            CheckDouble(prefix + "_depth_read_back", depth, SafeDouble(() => cut.Depth));
        }

        _cut = cut;
        if (cutType == 1)
        {
            _depthCutDepth = depth;
        }

        var faces = FaceCount();
        Record(prefix + "_faces_after", "граней у тела после выреза", Count(faces), Verdict.Info);
        Record(prefix + "_gabarit_after", "габарит после выреза", Gabarit(), Verdict.Info);
        return faces;
    }

    /// <summary>Item 4: the found cut survives save and open — count, type, depth, faces.</summary>
    private void ReopenCut()
    {
        var cutsAtSave = _container!.SheetMetalCuts.Count;
        var facesAtSave = FaceCount();
        var file = Path.Combine(Path.GetTempPath(),
            "sheet-metal-cut-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".m3d");

        try
        {
            _doc7!.SaveAs(file);
        }
        catch (COMException ex)
        {
            Record("cut_save", "деталь сохранена", Describe(ex), Verdict.Fail);
            return;
        }

        if (!File.Exists(file))
        {
            Record("cut_save", "деталь сохранена", "файла нет: " + file, Verdict.Fail);
            return;
        }

        Record("cut_save", "деталь сохранена", file, Verdict.Pass);

        IKompasDocument reopened;
        try
        {
            reopened = _app7!.Documents.Open(file, false, true);
        }
        catch (COMException ex)
        {
            Record("cut_reopen", "сохранённый файл открывается", Describe(ex), Verdict.Fail);
            return;
        }

        if (reopened as IKompasDocument3D is not { TopPart: { } part }
            || part as ISheetMetalContainer is not { } container)
        {
            Record("cut_reopen", "сохранённый файл открывается как листовая деталь", RuntimeName(reopened),
                Verdict.Fail);
            return;
        }

        Record("cut_reopen", "сохранённый файл открывается как листовая деталь", "ISheetMetalContainer", Verdict.Pass);
        CheckInt("cut_reopen_cuts_count", cutsAtSave, container.SheetMetalCuts.Count);
        CheckInt("cut_reopen_faces", facesAtSave, FaceCountOn(_app5?.ActiveDocument3D() as ksDocument3D));

        // The index base of the collection is not documented, so every index that answers is read and the
        // depth cut is found by its own numbers instead of by a guessed position.
        var depthCuts = 0;
        for (var index = 0; index <= cutsAtSave; index++)
        {
            if (CutAtExact(container.SheetMetalCuts, index) is not { } item)
            {
                continue;
            }

            var type = SafeInt(() => item.CutType);
            var depth = SafeDouble(() => item.Depth);
            Record("cut_reopen_item_" + index, "вырез по индексу " + index,
                "CutType " + (type?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
                + ", Depth " + (depth is null ? "не прочитано" : Num(depth.Value)), Verdict.Info);
            if (type == 1 && depth is { } read && Math.Abs(read - _depthCutDepth) <= 1e-6)
            {
                depthCuts++;
            }
        }

        CheckInt("cut_reopen_depth_cut_found", 1, depthCuts);
    }

    /// <summary>Item 5: a sketch on another body's face and an empty sketch on a face, as the kernel answers.</summary>
    private void CutRefusals(ksFaceDefinition face, ksEntity support)
    {
        if (SecondBody())
        {
            var foreignFace = PlanarFace(1, "refusal_foreign_face");
            var foreignSupport = foreignFace is null ? null : FaceSupport(foreignFace, "refusal_foreign_face_entity");
            if (foreignFace is not null && foreignSupport is not null)
            {
                var foreign5 = MakeFaceSketch(foreignFace, foreignSupport, 65d, -5d, 75d, 5d,
                    "refusal_foreign_face_sketch");
                var foreign7 = Transfer7<ISketch>(foreign5, "refusal_foreign_face_to7",
                    "эскиз на грани второго тела перенесён в API7");
                RefusalCut("refusal_foreign_face", foreign7);
            }
        }

        var empty5 = MakeEmptySketch("refusal_empty_face_sketch", support);
        if (empty5 is not null)
        {
            Record("refusal_empty_face_support", "опора пустого эскиза - грань",
                SameCom((empty5.GetDefinition() as ksSketchDefinition)?.GetPlane(), support) ? "грань" : "не грань",
                Verdict.Info);
            var empty7 = Transfer7<ISketch>(empty5, "refusal_empty_face_to7", "пустой эскиз на грани перенесён в API7");
            RefusalCut("refusal_empty_face", empty7);
        }
    }

    /// <summary>One cut attempt whose answer is a fact, not a requirement.</summary>
    /// <remarks>INVARIANT: a refusal is recorded as INFO with the kernel's own answer, and the record left
    /// in the collection is part of that answer. History: docs/decisions/sheet-metal.md#cut-probe</remarks>
    private void RefusalCut(string prefix, ISketch sketch7)
    {
        var cuts = _container!.SheetMetalCuts;
        var countBefore = cuts.Count;
        var facesBefore = (FaceCountOf(0), FaceCountOf(1));
        ISheetMetalCut? cut;
        try
        {
            cut = cuts.Add() as ISheetMetalCut;
        }
        catch (COMException ex)
        {
            Record(prefix + "_add", "вырез создан", Describe(ex), Verdict.Info);
            return;
        }

        if (cut is null)
        {
            Record(prefix + "_add", "вырез создан", "Add() → null", Verdict.Info);
            return;
        }

        try
        {
            cut.Body = (IKompasAPIObject)_body!;
            cut.Sketch = (Sketch)sketch7;
            cut.CutType = ksHoleCutTypeEnum.ksHoleCutByWidth;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(prefix + "_fields", "поля выреза заданы", Describe(ex), Verdict.Info);
        }

        Record(prefix + "_update", "Update() выреза", Flag(SafeBool(() => cut.Update())), Verdict.Info);
        Record(prefix + "_cuts_count", "счётчик вырезов: осталась ли запись",
            Count(countBefore) + " -> " + Count(cuts.Count), Verdict.Info);
        // MEASURED: the main-body count does not separate the two bodies, so each body is counted here.
        Record(prefix + "_faces_body0", "граней первого тела до и после выреза",
            Count(facesBefore.Item1) + " -> " + Count(FaceCountOf(0)), Verdict.Info);
        Record(prefix + "_faces_body1", "граней второго тела до и после выреза",
            Count(facesBefore.Item2) + " -> " + Count(FaceCountOf(1)), Verdict.Info);
    }

    /// <summary>A second sheet body, 20x20 at x 60..80 — the "another body" a refusal needs.</summary>
    private bool SecondBody()
    {
        var sketch5 = MakeRectangleSketch(60d, -10d, 80d, 10d, "cut_body2_sketch", "эскиз второго тела 20x20 создан");
        var sketch7 = Transfer7<ISketch>(sketch5, "cut_body2_to7", "эскиз второго тела перенесён в API7");
        try
        {
            _body2 = _container!.SheetMetalBodies.Add() as ISheetMetalBody;
        }
        catch (COMException ex)
        {
            Record("cut_body2_add", "второе тело создано", Describe(ex), Verdict.Fail);
            return false;
        }

        if (_body2 is null)
        {
            Record("cut_body2_add", "второе тело создано", "Add() → null", Verdict.Fail);
            return false;
        }

        _body2.Sketch = (Sketch)sketch7;
        _body2.Thickness = 1.5d;
        _body2.Radius = 2d;
        _body2.Direction = ksDirectionTypeEnum.dtNormal;
        CheckBool("cut_body2_update", true, SafeBool(() => _body2.Update()));
        return true;
    }

    /// <summary>The cut at one index, with an out-of-range answer read as "no cut here".</summary>
    private static ISheetMetalCut? CutAtExact(ISheetMetalCuts cuts, int index)
    {
        try
        {
            return cuts.get_SheetMetalCut(index) as ISheetMetalCut;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // API5 side: the sketch and the body edge the documented route needs as inputs
    // ---------------------------------------------------------------------------------------------

    private ksEntity MakeRectangleSketch(double x1, double y1, double x2, double y2, string prefix, string label,
        ksEntity? support = null)
    {
        var part = Part5();
        support ??= part.GetDefaultEntity(PlaneXoy) as ksEntity;
        if (support is null)
        {
            Record(prefix, label, "опора эскиза (плоскость XOY или грань) → null", Verdict.Fail);
            throw new ProbeStop();
        }

        if (part.NewEntity(ObjSketch) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            Record(prefix, label, "NewEntity(o3d_sketch) или его определение → null", Verdict.Fail);
            throw new ProbeStop();
        }

        definition.SetPlane(support);
        sketch.Create();
        if (definition.BeginEdit() is not ksDocument2D editor)
        {
            Record(prefix, label, "BeginEdit() → не ksDocument2D", Verdict.Fail);
            throw new ProbeStop();
        }

        editor.ksLineSeg(x1, y1, x2, y1, 1);
        editor.ksLineSeg(x2, y1, x2, y2, 1);
        editor.ksLineSeg(x2, y2, x1, y2, 1);
        editor.ksLineSeg(x1, y2, x1, y1, 1);
        definition.EndEdit();

        Record(prefix, label, "4 отрезка", Verdict.Pass);
        return sketch;
    }

    /// <summary>A sketch with no geometry at all — the input a cut must refuse.</summary>
    private ksEntity? MakeEmptySketch(string prefix = "refusal_empty_sketch", ksEntity? support = null)
    {
        var part = Part5();
        support ??= part.GetDefaultEntity(PlaneXoy) as ksEntity;
        if (support is null
            || part.NewEntity(ObjSketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            Record(prefix, "пустой эскиз создан", "NewEntity/GetDefaultEntity → null", Verdict.Info);
            return null;
        }

        definition.SetPlane(support);
        sketch.Create();
        if (definition.BeginEdit() is not ksDocument2D editor)
        {
            Record(prefix, "пустой эскиз создан", "BeginEdit() → не ksDocument2D", Verdict.Info);
            return null;
        }

        definition.EndEdit();
        Record(prefix, "пустой эскиз создан", "ни одного отрезка", Verdict.Info);
        return sketch;
    }

    private object FirstEdge()
    {
        if (EdgeOfBody(0) is { } edge)
        {
            Record("edge_source", "ребро тела найдено", "тело 0, ребро 0", Verdict.Pass);
            return edge;
        }

        Record("edge_source", "ребро тела найдено", "ни у одной грани нет рёбер", Verdict.Fail);
        throw new ProbeStop();
    }

    /// <summary>An edge of the same body that is NOT the first one — the second bend needs it.</summary>
    private object SecondEdge()
    {
        var part = Part5();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            Record("bend2_edge_source", "второе ребро найдено", "GetMainBody()/FaceCollection() → null", Verdict.Fail);
            throw new ProbeStop();
        }

        var first = FirstEdge();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            var element = faces.GetByIndex(f);
            var face = element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var candidate = edges.GetByIndex(e);
                if (!SameCom(candidate, first))
                {
                    Record("bend2_edge_source", "второе ребро найдено", "грань " + f + ", ребро " + e, Verdict.Pass);
                    return candidate;
                }
            }
        }

        Record("bend2_edge_source", "второе ребро найдено", "все рёбра совпали с первым", Verdict.Fail);
        throw new ProbeStop();
    }

    private object? EdgeOfBody(int bodyIndex)
    {
        var part = Part5();
        if (part.BodyCollection() is not ksBodyCollection bodies)
        {
            return null;
        }
        bodies.refresh();
        if (bodies.GetCount() <= bodyIndex || bodies.GetByIndex(bodyIndex) is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return null;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            var element = faces.GetByIndex(f);
            var face = element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;
            if (face?.EdgeCollection() is ksEdgeCollection edges && edges.GetCount() > 0)
            {
                return edges.GetByIndex(0);
            }
        }

        return null;
    }

    /// <summary>The XOY plane of the part, transferred to API7 — the unfold plane input.</summary>
    private IModelObject? Plane7()
    {
        try
        {
            return Part5().GetDefaultEntity(PlaneXoy) is { } plane
                ? _app5!.TransferInterface(plane, Api7Dual, 0) as IModelObject
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The first face of the body as an API5 entity — the support a sketch can lie on.</summary>
    /// <remarks>MEASURED: the kernel accepts a planar face as sketch support (SetPlane = TRUE).
    /// History: docs/decisions/sheet-metal.md#probe-routes</remarks>
    private ksEntity? FaceEntity()
    {
        var part = Part5();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces
            || faces.GetCount() < 1)
        {
            return null;
        }

        var element = faces.GetByIndex(0);
        return element as ksEntity;
    }

    /// <summary>Reads a value whose refusal to be read is a fact, not a crash.</summary>
    private object? ReadValue(Func<object?> call, string id)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, "значение читается", Describe(ex), Verdict.Info);
            return null;
        }
    }

    /// <summary>The first face of the body, transferred to API7 — the fixed face the unfold needs.</summary>
    private IModelObject? FirstFace()
    {
        var part = Part5();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces
            || faces.GetCount() < 1)
        {
            return null;
        }

        try
        {
            return _app5!.TransferInterface(faces.GetByIndex(0), Api7Dual, 0) as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Transfers an API5 object to API7 and records the failure as a check of its own.</summary>
    private T Transfer7<T>(object api5Object, string id, string label)
        where T : class
    {
        object? transferred;
        try
        {
            transferred = _app5!.TransferInterface(api5Object, Api7Dual, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, label, Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (transferred as T is not { } typed)
        {
            Record(id, label, RuntimeName(transferred), Verdict.Fail);
            throw new ProbeStop();
        }

        Record(id, label, typeof(T).Name, Verdict.Pass);
        return typed;
    }

    private static ISheetMetalBody? BodyAt(ISheetMetalContainer container)
    {
        var collection = container.SheetMetalBodies;
        foreach (var index in new object[] { 1, 0 })
        {
            try
            {
                if (collection.get_SheetMetalBody(index) is { } item)
                {
                    return item as ISheetMetalBody;
                }
            }
            catch (COMException)
            {
                // An out-of-range index is the documented collection answer; the other base is tried.
            }
        }

        return null;
    }

    /// <summary>The API5 part of the document this probe created: <c>GetPart(-1)</c> is the route the
    /// product itself uses, so the probe measures the same object the product would touch.</summary>
    private ksPart Part5() => (ksPart)_doc5!.GetPart(-1);

    private string Gabarit() => GabaritOn(_doc5);

    private double? ExtentZ() => ExtentZOn(_doc5);

    private static string GabaritOn(ksDocument3D? document)
    {
        try
        {
            if (document is null || document.GetPart(-1) is not ksPart part)
            {
                return "нет API5-детали";
            }

            if (!part.GetGabarit(true, true, out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return "GetGabarit → false";
            }

            return "x " + Num(x1) + ".." + Num(x2) + ", y " + Num(y1) + ".." + Num(y2)
                + ", z " + Num(z1) + ".." + Num(z2);
        }
        catch (COMException ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>The Z extent — the thickness by the normal for a flat body, whatever its X/Y size.</summary>
    private static double? ExtentZOn(ksDocument3D? document)
    {
        try
        {
            if (document is null || document.GetPart(-1) is not ksPart part
                || !part.GetGabarit(true, true, out _, out _, out var z1, out _, out _, out var z2))
            {
                return null;
            }

            return Math.Abs(z2 - z1);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The main body's face count — the "did the geometry change" criterion for a cut.</summary>
    private int FaceCount() => FaceCountOn(_doc5);

    /// <summary>The face count of one body of the document, by index in the body collection.</summary>
    private int FaceCountOf(int bodyIndex)
    {
        try
        {
            return Part5().BodyCollection() is ksBodyCollection bodies && bodies.GetCount() > bodyIndex
                && bodies.GetByIndex(bodyIndex) is ksBody body && body.FaceCollection() is ksFaceCollection faces
                ? faces.GetCount()
                : -1;
        }
        catch (COMException)
        {
            return -1;
        }
    }

    private static int FaceCountOn(ksDocument3D? document)
    {
        try
        {
            return document?.GetPart(-1) is ksPart part && part.GetMainBody() is ksBody body
                && body.FaceCollection() is ksFaceCollection faces
                ? faces.GetCount()
                : -1;
        }
        catch (COMException)
        {
            return -1;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Shutdown, checks and evidence
    // ---------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_app5 is null)
        {
            return;
        }

        try
        {
            _app5.Quit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Quit() refusing is not a measurement: the instance is taken down by its own PID below.
        }

        if (_pid is not int pid)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // Already gone: the normal case.
        }
    }

    private void CheckBool(string id, bool expected, bool? read)
    {
        Record(id, expected ? "true" : "false", read?.ToString() ?? "не прочитано",
            read == expected ? Verdict.Pass : Verdict.Fail);
    }

    private void CheckInt(string id, int expected, int? read)
    {
        Record(id, expected.ToString(CultureInfo.InvariantCulture),
            read?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано",
            read == expected ? Verdict.Pass : Verdict.Fail);
    }

    private void CheckDouble(string id, double expected, double? read)
    {
        var same = read is { } value && Math.Abs(value - expected) <= 1e-6;
        Record(id, Num(expected), read is null ? "не прочитано" : Num(read.Value), same ? Verdict.Pass : Verdict.Fail);
    }

    private void Record(string id, string expected, string read, Verdict verdict)
    {
        _checks.Add(new Check(id, expected, read, verdict));
        Console.WriteLine($"{verdict,-5} {id,-40} ожидалось: {expected} | прочитано: {read}");
    }

    private void Write()
    {
        var full = Path.GetFullPath(_outPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"probe\": \"sheet-metal\",\n");
        json.Append("  \"at\": \"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append("\",\n");
        json.Append("  \"kompas_version\": \"").Append(Escape(_version)).Append("\",\n");
        json.Append("  \"command\": \"dotnet run --project scripts/probes/sheet-metal/SheetMetalProbe.csproj -c Release -- <out.json>\",\n");
        json.Append("  \"summary\": { \"pass\": ").Append(Count(Verdict.Pass))
            .Append(", \"fail\": ").Append(Count(Verdict.Fail))
            .Append(", \"info\": ").Append(Count(Verdict.Info)).Append(" },\n");
        json.Append("  \"checks\": [\n");
        for (var i = 0; i < _checks.Count; i++)
        {
            var check = _checks[i];
            json.Append("    { \"id\": \"").Append(Escape(check.Id))
                .Append("\", \"verdict\": \"").Append(check.Verdict.ToString().ToUpperInvariant())
                .Append("\", \"expected\": \"").Append(Escape(check.Expected))
                .Append("\", \"read\": \"").Append(Escape(check.Read)).Append("\" }")
                .Append(i + 1 < _checks.Count ? ",\n" : "\n");
        }

        json.Append("  ]\n}\n");
        File.WriteAllText(full, json.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine("JSON: " + full);
        Console.WriteLine("PASS " + Count(Verdict.Pass) + " / FAIL " + Count(Verdict.Fail) + " / INFO " + Count(Verdict.Info));
    }

    private int Count(Verdict verdict) => _checks.Count(c => c.Verdict == verdict);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ReadVersion(KompasObject app)
    {
        try
        {
            int major = 0, minor = 0, build = 0, revision = 0;
            app.ksGetSystemVersion(out major, out minor, out build, out revision);
            return major + "." + minor + "." + build + "." + revision;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException)
        {
            return "unknown";
        }
    }

    private static int[] KompasProcessIds() =>
        Process.GetProcessesByName("KOMPAS").Select(p => p.Id).ToArray();

    private static string Flag(bool? value) => value is null ? "не прочитано" : value.Value ? "true" : "false";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string? value) => value is null ? "не прочитано" : "\"" + value + "\"";

    private static string RuntimeName(object? value) =>
        value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    private static string Describe(Exception ex) =>
        ex is COMException com ? $"COMException 0x{com.HResult:X8}: {com.Message}" : ex.GetType().Name + ": " + ex.Message;

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    private static bool? SafeBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<object?> call)
    {
        try
        {
            return call() is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    private static string? SafeString(Func<string> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static object? SafeObject(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether a write was refused by the kernel — the documented answer for a blocked setter.</summary>
    private static bool? Refused(Action write)
    {
        try
        {
            write();
            return false;
        }
        catch (COMException)
        {
            return true;
        }
    }

    /// <summary>How many objects the property returned: a VARIANT array, or a single unwrapped object.</summary>
    private static int? ArrayLength(object? value) => value switch
    {
        null => null,
        Array array => array.Length,
        _ => 1,
    };

    /// <summary>Whether the array the property returned holds the requested object, by COM identity.</summary>
    private static bool? ArrayContains(object? value, object? expected)
    {
        if (value is null || expected is null)
        {
            return null;
        }

        if (value is not Array array)
        {
            return SameCom(value, expected);
        }

        foreach (var item in array)
        {
            if (SameCom(item, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameCom(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (ReferenceEquals(left, right))
        {
            return true;
        }

        try
        {
            return Marshal.GetIUnknownForObject(left) == Marshal.GetIUnknownForObject(right);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private enum Verdict
    {
        Pass,
        Fail,
        Info,
    }

    private sealed record Check(string Id, string Expected, string Read, Verdict Verdict);

    private sealed class ProbeStop : Exception;
}
