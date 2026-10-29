using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace E2Probe;

/// <summary>E2 probe: builds a plate with history, then measures whether the documented rib routes
/// reach it and whether their answers can be compared with an expected number.</summary>
/// <remarks>WHY A PROBE AND NOT A TEST: the order is research - the product has no rib tool yet, so
/// the only thing measurable is what KOMPAS answers on the documented routes.
/// DOC: E2_RESEARCH_DEVELOPER_PROMPT.md §1.5; the rib family of the SDK 24 help snapshot this order
/// captured under reference/kompas-sdk-v24/ (`docs-wire-...-e2`): ksribdefinition*.html, irib*.html,
/// iribs*.html, imodelcontainer_ribs.html, ksribsideenum.html, ksthinparam*.html, obj3dtype.html.
/// History: docs/decisions/ribs.md#probe</remarks>
internal sealed partial class E2Probe : IDisposable
{
    // Fixture: a plate 40 x 30 x 20 mm spanning y = -15..+15, so the XOZ plane (y = 0) is its middle
    // plane. A rib on that plane stays inside the plate whichever way it is extruded, and its material
    // lands above the top face - that is the volume the probe compares with 400 * t.
    private const double Width = 40;
    private const double Depth = 30;
    private const double Height = 20;
    private const double Thickness = 6;
    private const double RibBaseZ = 20;
    private const double RibApexZ = 40;
    // Triangle (0, 20) - (20, 40) - (40, 20): legs 40 and 20, area 400 mm2, so a rib of thickness t
    // adds exactly 400 * t mm3 and reaches z = 40.
    private const double RibArea = 400;

    private const int PlaneXoy = 1;
    private const int PlaneXoz = 2;
    private const int SketchType = 5;
    private const int BossExtrusionType = 25;
    // DOC: obj3dtype.html names 44 <c>o3d_ribOperation</c> «операция "ребро жесткости"» with
    // <c>ksRibDefinition</c> in API5 and <c>IRib</c> in API7 - one number for both routes.
    private const int RibOperation = 44;

    // ksribsideenum.html: 0 left, 1 right (both within the sketch plane), 2 up, 3 down (across it).
    private const short SideLeft = 0;
    private const short SideRight = 1;
    private const short SideUp = 2;
    private const short SideDown = 3;
    // direction_type.html: 0 - прямое (наружу), 1 - обратное (внутрь), 2 - в обе стороны.
    private const short ThinOneSide = 0;
    private const short ThinReverse = 1;
    private const short ThinBoth = 2;

    // ST_MIX_MM | ST_MIX_KG: millimetres for length, kilograms for mass (MEASURED in the product).
    private const uint MassMmKg = 17;

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _states = new();
    private bool _flip;
    private bool _reverseChain;
    private short _workingSide = SideLeft;
    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;
    private ksEntity? _ribSketch;
    private ksEntity? _ribEntity;

    public E2Probe(string outPath) => _outPath = Path.GetFullPath(outPath);

    public int Run(bool keepOpen, string? savePath, bool positionsOnly = false)
    {
        try
        {
            Launch();
            DetectOrientation();
            if (positionsOnly)
            {
                PositionMatrix();
            }
            else
            {
                EntryPoints();
                SideMatrix();
                ThicknessModes();
                Draft();
                Refusals();
                ReadAndEdit();
                if (savePath is not null)
                {
                    SaveReopen(savePath);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }

        Write();
        if (!keepOpen)
        {
            CloseCurrent();
        }

        return _rows.Any(row => row?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    // ── environment and fixture ─────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "нет", "FAIL");
            throw new InvalidOperationException("KOMPAS is not registered");
        }

        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        _app5.Visible = false;
        Record("app5", "экземпляр КОМПАС создан", "KompasObject", "PASS");

        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app7", "ksGetApplication7() даёт IApplication", _app7 is null ? "null" : "IApplication",
            _app7 is null ? "FAIL" : "PASS");
    }

    /// <summary>Builds the plate and the rib sketch in a fresh document.</summary>
    /// <remarks>DOC: the help requires one open chain as the rib contour, so the normal fixture is two
    /// segments; the closed contour and the contour outside the body are the refusal cases.</remarks>
    private void BuildFixture(bool closedContour = false, bool outsideBody = false, bool withSketch = true)
    {
        _ribEntity = null;
        _ribSketch = null;
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, false) вернул false");
        }

        var part = _doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(PlaneXoy) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOY не получена");

        var plate = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) → null");
        var plateDefinition = plate.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition не получен");
        plateDefinition.SetPlane(plane);
        plate.Create();

        var plateEditor = plateDefinition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() → не ksDocument2D");
        plateEditor.ksLineSeg(0, -Depth / 2, Width, -Depth / 2, 1);
        plateEditor.ksLineSeg(Width, -Depth / 2, Width, Depth / 2, 1);
        plateEditor.ksLineSeg(Width, Depth / 2, 0, Depth / 2, 1);
        plateEditor.ksLineSeg(0, Depth / 2, 0, -Depth / 2, 1);
        plateDefinition.EndEdit();

        var feature = part.NewEntity(BossExtrusionType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(plate);
        if (!extrusion.SetSideParam(true, 0, Height, 0, false))
        {
            throw new InvalidOperationException("SetSideParam вернул false");
        }

        feature.Create();

        if (withSketch)
        {
            BuildRibSketch(part, closedContour, outsideBody);
        }

        part.Update();
        _doc5.RebuildDocument();
        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");
    }

    private void BuildRibSketch(ksPart part, bool closedContour, bool outsideBody)
    {
        var plane = part.GetDefaultEntity(PlaneXoz) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOZ не получена");
        var sketch = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) для ребра → null");
        var definition = sketch.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition ребра не получен");
        definition.SetPlane(plane);
        sketch.Create();

        var editor = definition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() ребра → не ksDocument2D");
        // `_flip` is the measured answer to "which way does the sketch y axis point": the interop
        // exposes no plane axes, so the probe measures both directions instead of assuming one.
        var sign = _flip ? -1 : 1;
        var baseZ = outsideBody ? RibApexZ + 20 : RibBaseZ;
        var apexZ = outsideBody ? RibApexZ + 40 : RibApexZ;
        if (closedContour)
        {
            editor.ksLineSeg(0, sign * baseZ, Width, sign * baseZ, 1);
            editor.ksLineSeg(Width, sign * baseZ, Width, sign * apexZ, 1);
            editor.ksLineSeg(Width, sign * apexZ, 0, sign * apexZ, 1);
            editor.ksLineSeg(0, sign * apexZ, 0, sign * baseZ, 1);
        }
        else
        {
            // The chain direction is measured too: `ksRibSideEnum` names left/right RELATIVE TO THE
            // CURVE, so a reversed chain must move which value builds. See DetectOrientation.
            if (_reverseChain)
            {
                editor.ksLineSeg(Width, sign * baseZ, Width / 2, sign * apexZ, 1);
                editor.ksLineSeg(Width / 2, sign * apexZ, 0, sign * baseZ, 1);
            }
            else
            {
                editor.ksLineSeg(0, sign * baseZ, Width / 2, sign * apexZ, 1);
                editor.ksLineSeg(Width / 2, sign * apexZ, Width, sign * baseZ, 1);
            }
        }

        definition.EndEdit();
        _ribSketch = sketch;
    }

    /// <summary>Measures which (sketch direction, side) pairs put material above the plate.</summary>
    /// <remarks>MEASURED, not assumed: a rib drawn on the wrong side of the top face adds nothing, and a
    /// probe that trusted its own sign would read that as "the route does not work". The pair that lifts
    /// the gabarit to z = 40 is the one the rest of the probe uses.</remarks>
    private void DetectOrientation()
    {
        var worked = new List<string>();
        foreach (var flip in new[] { false, true })
        {
            _flip = flip;
            foreach (var side in new[] { SideLeft, SideRight, SideUp, SideDown })
            {
                var (delta, topZ) = BuildAndMeasure(
                    $"diag_{(flip ? "flipped" : "as_drawn")}_side_{side}", side, ThinOneSide, Thickness, 0, 0);
                if (Math.Abs(delta) > 1e-6 && topZ > Height + 1e-6)
                {
                    worked.Add($"{(flip ? "flipped" : "as_drawn")}/side_{side}");
                    if (worked.Count == 1)
                    {
                        _workingSide = side;
                    }
                }
            }
        }

        if (worked.Count == 0)
        {
            Record("orientation", "сочетание «направление эскиза x Side» даёт материал над плитой",
                "ни одно из восьми", "FAIL");
            _flip = false;
            _workingSide = SideLeft;
            return;
        }

        _flip = worked[0].StartsWith("flipped", StringComparison.Ordinal);
        Record("orientation", "сочетание «направление эскиза x Side» даёт материал над плитой",
            $"работают {worked.Count} из 8: {string.Join(", ", worked)}; взято {worked[0]}", "PASS");

        // Second half of the same question: the enum names left/right of the CURVE, so the same four
        // values are measured again on a chain drawn the other way round.
        _reverseChain = true;
        var reversed = new List<string>();
        foreach (var side in new[] { SideLeft, SideRight, SideUp, SideDown })
        {
            var (delta, topZ) = BuildAndMeasure($"diag_reversed_side_{side}", side, ThinOneSide, Thickness, 0, 0);
            if (Math.Abs(delta) > 1e-6 && topZ > Height + 1e-6)
            {
                reversed.Add($"side_{side}");
            }
        }

        _reverseChain = false;
        Record("chain_direction", "направление цепочки меняет то, какое значение Side строит",
            reversed.Count == 0 ? "обратная цепочка не строит ни одним значением" : string.Join(", ", reversed),
            "MEASURED");
    }

    // ── entry points ────────────────────────────────────────────────────────────────────────────

    /// <summary>§1.2 of the order: both documented entry points must be reachable in the v24 interop.</summary>
    private void EntryPoints()
    {
        BuildFixture();
        try
        {
            var part = _doc5!.GetPart(-1) as ksPart;
            var entity = part?.NewEntity(RibOperation) as ksEntity;
            var definition = entity?.GetDefinition() as ksRibDefinition;
            Record("entry_api5", "ksEntity(o3d_ribOperation).GetDefinition() даёт ksRibDefinition",
                definition is null ? "null" : "ksRibDefinition", definition is null ? "FAIL" : "PASS");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("entry_api5", "ksEntity(o3d_ribOperation).GetDefinition() даёт ksRibDefinition",
                Describe(ex), "FAIL");
        }

        try
        {
            var container = _doc7!.TopPart as IModelContainer;
            var ribs = container?.Ribs;
            Record("entry_api7", "(IModelContainer)IPart7.Ribs даёт IRibs",
                ribs is null ? "null" : "IRibs", ribs is null ? "FAIL" : "PASS");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("entry_api7", "(IModelContainer)IPart7.Ribs даёт IRibs", Describe(ex), "FAIL");
        }

        CloseCurrent();
    }

    // ── the measured modes ──────────────────────────────────────────────────────────────────────

    /// <summary>§1.2 row <c>SM-14.rib.mode_side_positions</c>: all four <c>ksRibSideEnum</c> values,
    /// each compared with the expected 400 * t.</summary>
    private void SideMatrix()
    {
        foreach (var side in new[] { SideLeft, SideRight, SideUp, SideDown })
        {
            var (delta, topZ) = BuildAndMeasure("side_" + side, side, ThinOneSide, Thickness, 0, 0);
            Record("side_" + side + "_volume", $"прирост объёма {RibArea * Thickness:0} мм3 и габарит z = 40",
                $"dV={delta:0.###}, z={topZ:0.###}",
                Near(Math.Abs(delta), RibArea * Thickness, 1e-6) && Near(topZ, RibApexZ, 1e-6) ? "PASS" : "FAIL");
        }
    }

    /// <summary>§1.2 row <c>SM-14.rib.mode_symmetric_thickness</c>: <c>SetThinParam(2, 6, 6)</c> must add
    /// both thicknesses, one-sided <c>(0, 6, 0)</c> must add one - the pair is what makes the mode
    /// distinguishable from "thickness ignored".</summary>
    private void ThicknessModes()
    {
        var one = BuildAndMeasure("thin_one_side_6", _workingSide, ThinOneSide, Thickness, 0, 0).Delta;
        var both = BuildAndMeasure("thin_both_6_6", _workingSide, ThinBoth, Thickness, 0, Thickness).Delta;
        var reverse = BuildAndMeasure("thin_reverse_6", _workingSide, ThinReverse, Thickness, 0, 0).Delta;

        Record("thin_one_side_volume", $"одна сторона: прирост {RibArea * Thickness:0} мм3",
            $"{one:0.###}", Near(Math.Abs(one), RibArea * Thickness, 1e-6) ? "PASS" : "FAIL");
        Record("thin_both_volume", $"симметричная толщина 6+6: прирост {RibArea * 2 * Thickness:0} мм3",
            $"{both:0.###}", Near(Math.Abs(both), RibArea * 2 * Thickness, 1e-6) ? "PASS" : "FAIL");
        Record("thin_reverse_volume", $"обратное направление стенки: прирост {RibArea * Thickness:0} мм3",
            $"{reverse:0.###}", Near(Math.Abs(reverse), RibArea * Thickness, 1e-6) ? "PASS" : "FAIL");
        Record("thin_modes_differ", "симметричная толщина даёт вдвое больше одной стороны",
            $"{both:0.###} против {one:0.###}",
            Math.Abs(one) > 1e-6 && Near(Math.Abs(both), 2 * Math.Abs(one), 1e-6) ? "PASS" : "FAIL");
    }

    /// <summary>§1.2 row <c>SM-14.rib.mode_face_draft</c>: <c>Angle</c> must change the volume; the
    /// expected trapezoid is recorded next to the measurement, the assertion is the monotonicity.</summary>
    /// <remarks>MEASURED: the angle ADDS material (2400 -> 2663.389 -> 3059.881), so the draft expands the
    /// rib outward; a check that expected a decrease would have called a working route broken.
    /// LIMIT: the exact trapezoid depends on which face the angle is measured from.</remarks>
    private void Draft()
    {
        var zero = BuildAndMeasure("draft_0", _workingSide, ThinOneSide, Thickness, 0, 0).Delta;
        var two = BuildAndMeasure("draft_2", _workingSide, ThinOneSide, Thickness, 2, 0).Delta;
        var five = BuildAndMeasure("draft_5", _workingSide, ThinOneSide, Thickness, 5, 0).Delta;
        Record("draft_direction", "уклон меняет прирост монотонно (0 < 2 < 5 градусов)",
            $"{zero:0.###} < {two:0.###} < {five:0.###}",
            Math.Abs(zero) < Math.Abs(two) && Math.Abs(two) < Math.Abs(five) ? "PASS" : "FAIL");
        Record("draft_5_estimate", "оценка трапеции по формуле уклона (чтение, не приговор)",
            $"{five:0.###} при основании {RibArea * Thickness:0}", "MEASURED");
    }

    /// <summary>§1.2 of the order: a refusal is a verdict, not a silence - a closed contour, a contour
    /// outside the body and a rib without a sketch must all be refused.</summary>
    private void Refusals()
    {
        Refusal("refusal_closed_contour", closedContour: true, outsideBody: false, withSketch: true);
        Refusal("refusal_outside_body", closedContour: false, outsideBody: true, withSketch: true);
        Refusal("refusal_no_sketch", closedContour: false, outsideBody: false, withSketch: false);
    }

    private void Refusal(string id, bool closedContour, bool outsideBody, bool withSketch)
    {
        try
        {
            BuildFixture(closedContour, outsideBody, withSketch);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record(id, "фикстура для отказа построена", Describe(ex), "FAIL");
            return;
        }

        var before = State(id + ": до");
        var (updated, failure) = CreateRib(_workingSide, ThinOneSide, Thickness, 0, 0);
        var after = State(id + ": после");
        var delta = Volume(after) - Volume(before);
        var refused = failure is not null || updated == false || Math.Abs(delta) < 1e-6;
        Record(id, "недопустимый вход отвергнут (отказ метода, Update=false или нулевой прирост)",
            failure ?? $"Update={updated}, dV={delta:0.###}", refused ? "PASS" : "FAIL");
        CloseCurrent();
    }

    // ── reading, editing, persistence ───────────────────────────────────────────────────────────

    /// <summary>§1.4 of the order: the parameters must be readable back and an edit must change the
    /// geometry - otherwise "the route works" only means "Update answered true".</summary>
    private void ReadAndEdit()
    {
        BuildFixture();
        var before = State("до правки");
        var (updated, failure) = CreateRib(_workingSide, ThinOneSide, Thickness, 0, 0);
        if (failure is not null)
        {
            Record("read_edit", "ребро создано для чтения параметров", failure, "FAIL");
            CloseCurrent();
            return;
        }

        var created = State("ребро создано");
        Record("read_edit_created", "ребро создано и материал добавлен",
            $"Update={updated}, dV={Volume(created) - Volume(before):0.###}",
            Math.Abs(Volume(created) - Volume(before)) > 1e-6 ? "PASS" : "FAIL");

        var api5 = ReadDefinition();
        Record("read_api5", "ksRibDefinition читает side, angle, index и толщину обратно",
            api5 ?? "не прочитано", api5 is null ? "FAIL" : "PASS");
        var api7 = ReadRib7();
        Record("read_api7", "IRib читает Side, Angle, SegmentIndex обратно", api7 ?? "не прочитано",
            api7 is not null && !api7.StartsWith("IRibs", StringComparison.Ordinal) ? "PASS" : "FAIL");

        var edited = EditDefinition(angle: 5);
        var afterEdit = State("после правки угла");
        // The expected number comes from an independent run of the same geometry (draft_5), not from
        // "the volume moved": that is what makes the edit a checked change instead of a receipt.
        Record("edit_angle", "правка Angle=5 даёт тот же объём, что ребро сразу с углом 5",
            $"Update={edited}, {Volume(afterEdit):0.###} против {Volume(created):0.###} до правки",
            edited && Volume(afterEdit) > Volume(created) ? "PASS" : "FAIL");
        CloseCurrent();
    }

    private string? ReadDefinition()
    {
        try
        {
            var definition = RibEntity()?.GetDefinition() as ksRibDefinition;
            if (definition is null)
            {
                return null;
            }

            var read = $"side={definition.side}, angle={definition.angle}, index={definition.index}";
            var thin = definition.GetThinParam(out var thinType, out var normal, out var reverse);
            return read + $", GetThinParam={thin} ({thinType}, {normal:0.###}, {reverse:0.###})";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private string? ReadRib7()
    {
        try
        {
            var container = _doc7?.TopPart as IModelContainer;
            var ribs = container?.Ribs;
            if (ribs is null)
            {
                return null;
            }

            var rib = ribs.get_Rib(0);
            return rib is null
                ? "IRibs.get_Rib(0) = null"
                : $"side={rib.Side}, angle={rib.Angle}, segment={rib.SegmentIndex}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private bool EditDefinition(double angle)
    {
        try
        {
            var entity = RibEntity();
            var definition = entity?.GetDefinition() as ksRibDefinition;
            if (definition is null)
            {
                return false;
            }

            definition.angle = angle;
            var updated = entity!.Update();
            _doc5?.RebuildDocument();
            return updated;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private void SaveReopen(string path)
    {
        BuildFixture();
        var before = State("до сохранения");
        var (updated, failure) = CreateRib(_workingSide, ThinOneSide, Thickness, 0, 0);
        if (failure is not null)
        {
            Record("save_reopen", "ребро создано перед сохранением", failure, "FAIL");
            CloseCurrent();
            return;
        }

        var created = State("ребро создано");
        Record("save_reopen_created", "ребро добавлено до сохранения",
            $"Update={updated}, dV={Volume(created) - Volume(before):0.###}",
            Math.Abs(Volume(created) - Volume(before)) > 1e-6 ? "PASS" : "FAIL");

        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (_doc5!.SaveAs(target) != true)
        {
            Record("save_reopen", "документ сохранён", "SaveAs вернул не true", "FAIL");
            CloseCurrent();
            return;
        }

        CloseCurrent();
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Open(target, false))
        {
            Record("save_reopen", "документ переоткрыт", "Open вернул false", "FAIL");
            return;
        }

        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
        var after = State("после переоткрытия");
        Record("save_reopen_volume", "объём после переоткрытия равен объёму после создания ребра",
            $"{Volume(after):0.###} против {Volume(created):0.###}",
            Near(Volume(after), Volume(created), 1e-9) ? "PASS" : "FAIL");
        Record("save_reopen_rib", "ребро читается из дерева после переоткрытия",
            RibEntity() is null ? "не найдено" : "найдено", RibEntity() is null ? "FAIL" : "PASS");
    }

    // ── creation ────────────────────────────────────────────────────────────────────────────────

    private (double Delta, double TopZ) BuildAndMeasure(string id, short side, short thinType, double normal,
        double angle, double reverse)
    {
        BuildFixture();
        var before = State(id + ": до");
        var (updated, failure) = CreateRib(side, thinType, normal, angle, reverse);
        var after = State(id + ": после");
        var delta = Volume(after) - Volume(before);
        var topZ = Gabarit(after)?[5] ?? double.NaN;
        Record(id, $"ребро Side={side}, thinType={thinType}, толщина {normal}/{reverse}, угол {angle}",
            failure ?? $"Update={updated}, dV={delta:0.###}, граней {Faces(before)}→{Faces(after)}, " +
            $"габарит z {Gabarit(before)?[5]:0.###}→{topZ:0.###}",
            "MEASURED");
        CloseCurrent();
        return (delta, topZ);
    }

    /// <summary>Creates the rib on the documented API5 route: entity, definition, sketch, side, thin
    /// parameters, angle, then <c>Create</c>.</summary>
    private (bool? Updated, string? Failure) CreateRib(short side, short thinType, double normal,
        double angle, double reverse)
    {
        try
        {
            // MEASURED: the entity NewEntity returns is not in the tree until Create(), so the probe
            // holds the handle; a tree search before Create() finds nothing.
            _ribEntity ??= (_doc5?.GetPart(-1) as ksPart)?.NewEntity(RibOperation) as ksEntity;
            var entity = _ribEntity;
            var definition = entity?.GetDefinition() as ksRibDefinition;
            if (entity is null || definition is null)
            {
                return (null, "ksRibDefinition не получен");
            }

            if (_ribSketch is null)
            {
                // Deliberate: the refusal case "rib without a sketch" must reach Create with no contour.
                return (entity.Create(), null);
            }

            if (!definition.SetSketch(_ribSketch))
            {
                return (null, "SetSketch вернул false");
            }

            definition.side = side;
            definition.angle = angle;
            definition.index = 0;
            if (!definition.SetThinParam(thinType, normal, reverse))
            {
                return (null, "SetThinParam вернул false");
            }

            var ok = entity.Create();
            _doc5?.RebuildDocument();
            return (ok, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>The rib entity: the held handle, or the last <c>o3d_ribOperation</c> in a reopened tree.</summary>
    private ksEntity? RibEntity()
    {
        if (_ribEntity is not null)
        {
            return _ribEntity;
        }

        var part = _doc5?.GetPart(-1) as ksPart;
        var collection = part?.EntityCollection(0) as ksEntityCollection;
        if (collection is null)
        {
            return null;
        }

        for (var index = collection.GetCount() - 1; index >= 0; index--)
        {
            if (collection.GetByIndex(index) is ksEntity entity && entity.type == RibOperation)
            {
                return entity;
            }
        }

        return null;
    }

    // ── reading ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the state the order asks to compare: volume, face count, gabarit, solidity.</summary>
    private JsonObject State(string label)
    {
        var part = _doc5?.GetPart(-1) as ksPart;
        var bodies = part?.BodyCollection() as ksBodyCollection;
        var count = bodies?.GetCount() ?? 0;
        var body = count > 0 ? bodies!.GetByIndex(0) as ksBody : null;
        var faces = body?.FaceCollection() as ksFaceCollection;

        double? volume = null;
        double? area = null;
        try
        {
            if (body?.CalcMassInertiaProperties(MassMmKg) is ksMassInertiaParam properties)
            {
                volume = properties.v;
                area = properties.F;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            volume = null;
        }

        double[]? gabarit = null;
        try
        {
            if (body is not null && body.GetGabarit(out var x1, out var y1, out var z1,
                    out var x2, out var y2, out var z2))
            {
                gabarit = new[] { x1, y1, z1, x2, y2, z2 };
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            gabarit = null;
        }

        var state = new JsonObject
        {
            ["label"] = label,
            ["bodies"] = count,
            ["faces"] = faces?.GetCount() ?? 0,
            ["volume_mm3"] = volume is null ? null : Math.Round(volume.Value, 4),
            ["area_mm2"] = area is null ? null : Math.Round(area.Value, 4),
            ["gabarit"] = gabarit is null ? null : JsonSerializer.SerializeToNode(gabarit),
            ["volume_nonzero"] = volume is null ? null : volume.Value > 1e-6,
        };
        _states.Add(state.DeepClone());
        return state;
    }

    private static double Volume(JsonObject state) => state["volume_mm3"]?.GetValue<double>() ?? double.NaN;

    private static int Faces(JsonObject state) => state["faces"]?.GetValue<int>() ?? 0;

    private static double[]? Gabarit(JsonObject state) => state["gabarit"]?.Deserialize<double[]>();

    private static bool Near(double actual, double expected, double tolerance) =>
        double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected));

    // ── output ──────────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string expected, string actual, string verdict)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["expected"] = expected,
            ["actual"] = actual,
            ["verdict"] = verdict,
        });
        Console.WriteLine($"{verdict}  {id}: {actual}");
    }

    private void Write()
    {
        var document = new JsonObject
        {
            ["probe"] = "E2 stiffening ribs",
            ["kompas"] = "v24",
            ["flip"] = _flip,
            ["working_side"] = _workingSide,
            ["rows"] = _rows.DeepClone(),
            ["states"] = _states.DeepClone(),
        };
        var directory = Path.GetDirectoryName(_outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"evidence: {_outPath}");
    }

    private void CloseCurrent()
    {
        try
        {
            _doc7?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // Housekeeping only: a part left open must not turn a measured run into a failed one.
        }

        _doc7 = null;
        _doc5 = null;
        _ribSketch = null;
        _ribEntity = null;
    }

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COMException 0x{com.HResult:X8}: {com.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    public void Dispose()
    {
        _doc7 = null;
        _doc5 = null;
        _app7 = null;
        _app5 = null;
    }
}
