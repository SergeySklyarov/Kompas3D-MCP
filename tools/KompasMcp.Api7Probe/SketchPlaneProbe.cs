using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>Probe SP — changing an existing sketch's BASE plane via the documented
/// <c>ksSketchDefinition.SetPlane</c> and reading it back with <c>GetPlane</c>.</summary>
/// <remarks>DOC (pages taken from the official help root, mirrored in <c>scratch/sdk-docs/</c>):
/// <c>kssketchdefinition_setplane.html</c> — «SetPlane — Изменить базовую плоскость эскиза»,
/// <c>BOOL SetPlane(LPENTITY plane)</c>, the parameter is «указатель на интерфейс
/// базовой плоскости эскиза <c>ksEntity</c> или <c>IEntity</c>»; <c>kssketchdefinition_getplane.html</c>
/// — «GetPlane — Получить базовую плоскость эскиза», <c>LPENTITY GetPlane()</c>. Both are listed under
/// «ISketchDefinition — методы» (<c>kssketchdefinition_methods.html</c>) with <c>GetSurface</c>,
/// <c>GetLocation</c>/<c>SetLocation</c> and <c>UserSetPlacement</c>. The question is put by the PROBE,
/// not the product: a route the product does not yet have cannot be measured with the product's own tool.
/// LIMIT: the fixture is a 40×40 rectangle centred on the sketch origin with a 10 mm base extrusion; the
/// volume 40·40·10 = 16000 mm³ is the SAME before and after — it is the POSITION that changes, so volume
/// is NOT a discriminating quantity. EXPECTED (declared BEFORE the run, derived analytically): a 40×40×10
/// box whose two 40-extents are centred on the model origin and whose 10-extent lies on the plane normal
/// side; on <c>xy+15</c> that is <c>x∈[−20,20], y∈[−20,20], z∈[15,25]</c>; after the move to <c>xz</c>
/// the 10-extent must leave Z for Y (<c>x∈[−20,20], z∈[−20,20]</c>, <c>y</c> a 10-long segment). The SIDE
/// (sign) is not predicted — the help does not fix it and it is measured, not guessed. TEST: SP.4 writes
/// the SAME plane (geometry must not change), SP.5 another (must change): a lone "accepted and unchanged"
/// does not separate "not applied" from "nothing to apply", so the steps come as a pair, and the "does the
/// instrument see changes at all" control follows the writes (class 42). Own STA thread, own invisible
/// KOMPAS instance, own documents in <c>scratch</c>; foreign processes are not killed.
/// History: docs/decisions/probes.md#sp-plane</remarks>
internal sealed class SketchPlaneProbe
{
    // ── fixture ─────────────────────────────────────────────────────────────────────────────────
    private const double BoxSide = 40d;
    private const double BoxHeight = 10d;
    private const double OffsetMm = 15d;

    /// <summary>Offset of the AUXILIARY plane used by the auxiliary-plane step. Distinct from
    /// <see cref="OffsetMm"/>, so a support change to it is visible in the extent: the box must land on
    /// z∈[25,35].</summary>
    private const double AuxPlaneOffset = 25d;

    /// <summary>Box volume: 40·40·10. Equal before and after the support change — NOT a discriminating quantity.</summary>
    private const double BoxVolume = BoxSide * BoxSide * BoxHeight;

    private const double Tolerance = 1e-6;

    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short PlaneXoz = 2;
    private const short PlaneOffset = 14;
    private const short FaceType = 6;
    private const short EdgeType = 7;
    private const short BaseExtrusion = 24;
    private const int LineStyle = 1;

    /// <summary>DOC: <c>ksapitypeenum.html</c> — <c>ksAPI5Auto = 1</c> («API5 - интерфейсы
    /// автоматизации»). Named rather than written as a literal, because the value selects which API the
    /// transferred object belongs to and a wrong one is accepted silently.</summary>
    private const int Api5Auto = (int)ksAPITypeEnum.ksAPI5Auto;

    /// <summary>DOC: <c>ksapitypeenum.html</c> — <c>ksAPI7Dual = 2</c> («API7 - дуальные интерфейсы»).</summary>
    private const int Api7Dual = (int)ksAPITypeEnum.ksAPI7Dual;

    /// <summary>DOC: <c>ksapitypeenum.html</c> — <c>ksAPIUndef = 0</c> («Интерфейс неопределённого
    /// типа»); per <c>kompasobject_transferinterface.html</c> note (3) this asks for the source object
    /// itself.</summary>
    private const int ApiUndef = (int)ksAPITypeEnum.ksAPIUndef;

    /// <summary>DOC: <c>ksObj3dTypeEnum</c> — <c>o3d_planeOffset = 14</c>, the object type the transfer
    /// is asked for. <c>objNewType = 0</c> is what the help's note (6) allows but does not recommend;
    /// the wanted type is therefore named explicitly.</summary>
    private const int PlaneOffsetObjectType = (int)ksObj3dTypeEnum.o3d_planeOffset;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private string _savedPath = string.Empty;

    /// <summary>The sketch that is re-bound: a single one for the whole run, so every write targets the
    /// same subject rather than a fresh one.</summary>
    private ksEntity? _sketch;

    public SketchPlaneProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "sketch-plane.json"),
            Path.Combine(options.ReportDir, "sketch-plane.md"));

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            BuildFixtureOnOffsetPlane();
            ReadPlaneBack();
            WriteSamePlaneKeepsGeometry();
            WriteOtherPlaneMovesGeometry();
            ReadPlaneBackAfterEdit();
            // Reopen runs BEFORE the negative cases: they write into the same support, and afterwards
            // "did edit SP.5 apply at load" can no longer be measured (order rule: a certainly mutating
            // case comes last).
            ReopenThenEditAgain();
            WriteNonPlaneObject();
            WritePlanarFaceCandidate();
            WriteAuxiliaryPlaneCandidate();
            WriteApi5PlaneOverFaceCandidate();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("SP.X", "Необработанное исключение пробы SP");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ── session ─────────────────────────────────────────────────────────────────────────────────
    private void Launch()
    {
        var step = _report.Begin("SP.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
        try
        {
            var before = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray().Except(before).ToArray();
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            step.Pass("Экземпляр запущен и атрибутирован диффом процессов; Worker к нему не подключён.");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("SP.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            _app.Quit();
            var pid = _options.ProcessId;
            step.Pass(pid is null || WaitUntilGone(pid.Value, 30_000)
                ? "Собственный экземпляр завершён штатно."
                : "Процесс жив через 30 с после Quit().");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение не подтверждено: " + ex.Message);
        }
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────
    private void BuildFixtureOnOffsetPlane()
    {
        var step = _report.Begin("SP.2",
            "Коробка 40×40×10 на смещённой плоскости xy+15: эскиз, выталкивание, габарит",
            "Строится ли заготовка, у которой смена опоры различима ГАБАРИТОМ?");

        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Create(true, true) != true)
        {
            step.Fail("Документ не создан.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);

        // Offset plane as a MODEL object: NewEntity(o3d_planeOffset=14) + ksPlaneOffsetDefinition.
        var (plane, planeFailure) = NewOffsetPlane("SP-offset", PlaneXoy, OffsetMm);
        if (plane is null)
        {
            step.Fail("Смещённая плоскость не создана: " + planeFailure);
            return;
        }

        step.Data["offset_plane"] = plane.name;
        step.Data["offset_plane_type"] = plane.type;

        var (sketch, sketchFailure) = NewSketchOn(plane, "SP-profile");
        if (sketch is null)
        {
            step.Fail("Эскиз на смещённой плоскости не создан: " + sketchFailure);
            return;
        }

        _sketch = sketch;
        if (sketch.GetDefinition() is not ksSketchDefinition definition
            || definition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("Редактор эскиза не дан.");
            return;
        }

        var half = BoxSide / 2d;
        editor.ksLineSeg(-half, -half, half, -half, LineStyle);
        editor.ksLineSeg(half, -half, half, half, LineStyle);
        editor.ksLineSeg(half, half, -half, half, LineStyle);
        editor.ksLineSeg(-half, half, -half, -half, LineStyle);
        step.Data["endedit"] = Api5.Raw(TryBool(definition.EndEdit));

        var (extrusion, extrusionFailure) = NewBaseExtrusion(sketch, BoxHeight);
        if (extrusion is null)
        {
            step.Fail("Базовое выталкивание не создано: " + extrusionFailure);
            return;
        }

        _doc.RebuildDocument();

        var volume = Api5.Volume(_part);
        var gabarit = Gabarit(_part);
        step.Data["volume"] = Api5.Num(volume);
        step.Data["volume_expected"] = Api5.Num(BoxVolume);
        step.Data["gabarit"] = Describe(gabarit);
        step.Data["bodies"] = Api5.BodyCount(_part);
        step.Data["faces"] = Api5.FaceCount(_part);

        if (volume is not double v || Math.Abs(v - BoxVolume) > 1e-6)
        {
            step.Fail($"Объём {Api5.Num(volume)} не совпал с ожиданием {Api5.Num(BoxVolume)} — заготовка не та, "
                + "и дальнейшие шаги измеряли бы не тот предмет.");
            return;
        }

        if (!IsBox(gabarit, BoxSide, BoxSide, BoxHeight))
        {
            step.Fail("Габарит " + Describe(gabarit) + " не является коробкой 40×40×10 — заготовка не та.");
            return;
        }

        step.Data["box_centred"] = IsCentred(gabarit, BoxSide, BoxSide);
        step.Data["normal_side_mm"] = NormalSide(gabarit, BoxSide, BoxSide);
        step.Pass($"Коробка {Describe(gabarit)} объёмом {Api5.Num(volume)} стоит на xy+{OffsetMm:0.###}: "
            + "смена опоры обязана сдвинуть десятимиллиметровое протяжение с оси Z.");
    }

    // ── read back: documented GetPlane ──────────────────────────────────────────────────────────
    private void ReadPlaneBack()
    {
        var step = _report.Begin("SP.3",
            "Чтение опоры обратно: ksSketchDefinition.GetPlane и ISketch.Plane (API7)",
            "Документированный член чтения в поставленной сборке существует и отвечает?");

        if (_sketch is null)
        {
            step.Unknown("Эскиза нет — читать нечего (SP.2 не состоялся).");
            return;
        }

        if (_sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("GetDefinition() не дал ksSketchDefinition.");
            return;
        }

        object? plane = null;
        string? failure = null;
        try
        {
            plane = definition.GetPlane();
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        step.Data["getplane_failure"] = failure ?? "нет";
        if (plane is null)
        {
            step.Fail("GetPlane() вернул null" + (failure is null ? string.Empty : " (исключение: " + failure + ")"));
            return;
        }

        step.Data["getplane_runtime"] = Api5.RuntimeName(plane);
        if (plane is ksEntity entity)
        {
            step.Data["getplane_name"] = entity.name;
            step.Data["getplane_type"] = entity.type;
            step.Data["getplane_type_expected"] = PlaneOffset;
        }
        else
        {
            step.Data["getplane_cast_ksEntity"] = "не приводится";
        }

        // API7 route: on ISketch the support is read by a property. The probe also reads it: the adapter
        // already has this route (Api7SketchEntities.ReadPlane), and a divergence of the two routes is a fact.
        var api7 = ReadPlaneApi7(_sketch, step);

        if (plane is ksEntity read && read.type == PlaneOffset)
        {
            step.Pass("GetPlane() отвечает объектом типа " + read.type + " («" + read.name
                + "») — документированное чтение опоры работает на поставленной сборке; API7-маршрут: "
                + (api7 ?? "не ответил"));
        }
        else
        {
            step.Pass("GetPlane() отвечает объектом (" + Api5.RuntimeName(plane)
                + "), тип " + (plane is ksEntity e2 ? e2.type.ToString(CultureInfo.InvariantCulture) : "—")
                + " вместо ожидаемого " + PlaneOffset + "; API7-маршрут: " + (api7 ?? "не ответил"));
        }
    }

    // ── SP.4: write the SAME plane ──────────────────────────────────────────────────────────────
    private void WriteSamePlaneKeepsGeometry()
    {
        var step = _report.Begin("SP.4",
            "SetPlane той же плоскостью: геометрия не меняется (отрицательный контроль)",
            "Отличает «правка не применяется» от «правке нечего менять»?");

        var before = Snapshot(step, "before");
        if (before is null)
        {
            step.Unknown("Заготовки нет — писать не на что.");
            return;
        }

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var plane = TryGetPlane(definition);
        if (plane is null)
        {
            step.Unknown("Текущая опора не прочиталась — писать ту же нечем.");
            return;
        }

        var accepted = TrySetPlane(definition, plane, step, "accepted");
        _doc.RebuildDocument();
        var after = Snapshot(step, "after");
        if (after is null)
        {
            step.Fail("Габарит после записи не прочитался — сравнивать не с чем.");
            return;
        }

        var same = Same(before.Value, after.Value);
        step.Data["geometry_unchanged"] = same;
        if (same)
        {
            step.Pass("SetPlane той же плоскостью (принято=" + accepted + ") геометрию не изменил: "
                + before.Value.Describe() + " → " + after.Value.Describe());
        }
        else
        {
            step.Fail("SetPlane ТОЙ ЖЕ плоскостью изменил геометрию: " + before.Value.Describe()
                + " → " + after.Value.Describe() + " — запись не идемпотентна, и SP.5 после неё неразличим.");
        }
    }

    // ── SP.5: write ANOTHER plane — the discriminating half of the pair ─────────────────────────
    private void WriteOtherPlaneMovesGeometry()
    {
        var step = _report.Begin("SP.5",
            "SetPlane другой плоскостью (xz): габарит обязан сдвинуться, объём остаться",
            "Перепривязка существующего эскиза применяется и видна на зависимом теле?");

        var before = Snapshot(step, "before");
        if (before is null)
        {
            step.Unknown("Заготовки нет — писать не на что.");
            return;
        }

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var xz = _part.GetDefaultEntity(PlaneXoz) as ksEntity;
        if (xz is null)
        {
            step.Fail("GetDefaultEntity(o3d_planeXOZ=2) вернул null — писать нечем.");
            return;
        }

        step.Data["target_plane"] = xz.name;
        step.Data["target_plane_type"] = xz.type;

        var surfaceBefore = SketchSurface();
        step.Data["surface_origin_before"] = Vector(surfaceBefore.Origin);
        step.Data["surface_normal_before"] = Vector(surfaceBefore.Normal);
        step.Data["surface_note_before"] = surfaceBefore.Note;

        var accepted = TrySetPlane(definition, xz, step, "accepted");
        var surfaceAfterWrite = SketchSurface();
        step.Data["surface_normal_after_write"] = Vector(surfaceAfterWrite.Normal);
        step.Data["surface_note_after_write"] = surfaceAfterWrite.Note;

        var after = RebuildLadder(step, before.Value, "xz");
        if (after is null)
        {
            step.Fail("Габарит после перепривязки не прочитался.");
            return;
        }

        var surfaceAfter = SketchSurface();
        step.Data["surface_normal_after"] = Vector(surfaceAfter.Normal);

        step.Data["gabarit_before"] = before.Value.Describe();
        step.Data["gabarit_after"] = after.Value.Describe();
        step.Data["volume_before"] = Api5.Num(before.Value.Volume);
        step.Data["volume_after"] = Api5.Num(after.Value.Volume);
        step.Data["moved"] = !Same(before.Value, after.Value);
        step.Data["box_after"] = IsBox(after.Value.Gabarit, BoxSide, BoxSide, BoxHeight);
        step.Data["centred_after"] = IsCentred(after.Value.Gabarit, BoxSide, BoxSide);
        step.Data["normal_side_after_mm"] = NormalSide(after.Value.Gabarit, BoxSide, BoxSide);
        step.Data["thickness_axis_after"] = ThicknessAxis(after.Value.Gabarit);
        step.Data["thickness_axis_before"] = ThicknessAxis(before.Value.Gabarit);
        step.Data["sketch_surface_moved"] = surfaceBefore.Normal is not null && surfaceAfter.Normal is not null
            && (Math.Abs(surfaceBefore.Normal[0] - surfaceAfter.Normal[0]) > 1e-6
                || Math.Abs(surfaceBefore.Normal[1] - surfaceAfter.Normal[1]) > 1e-6
                || Math.Abs(surfaceBefore.Normal[2] - surfaceAfter.Normal[2]) > 1e-6);

        var volumeKept = before.Value.Volume is double vb && after.Value.Volume is double va
            && Math.Abs(vb - BoxVolume) <= 1e-6 && Math.Abs(va - BoxVolume) <= 1e-6;
        var moved = !Same(before.Value, after.Value);
        var stillBox = IsBox(after.Value.Gabarit, BoxSide, BoxSide, BoxHeight);
        var centred = IsCentred(after.Value.Gabarit, BoxSide, BoxSide);

        if (accepted == false)
        {
            step.Fail("SetPlane(xz) вернул false — ядро правку не приняло (ветвь честной остановки §6 наряда).");
            return;
        }

        if (!moved)
        {
            var surfaceLine = step.Data["sketch_surface_moved"] is true
                ? "Поверхность САМОГО эскиза при этом сдвинулась (" + Vector(surfaceBefore.Normal) + " → "
                    + Vector(surfaceAfter.Normal) + "), то есть записана другая опора, а зависимое тело "
                    + "не пересчитано НИ ОДНОЙ ступенью (первая применившая: "
                    + step.Data["xz_first_route_that_applied"] + ")."
                : "Поверхность самого эскиза тоже не сдвинулась (" + Vector(surfaceAfter.Normal) + ").";
            step.Fail("SetPlane(xz) принят, но геометрия не сдвинулась: " + before.Value.Describe()
                + " → " + after.Value.Describe() + " — «принято» не равно «применено». " + surfaceLine);
            return;
        }

        if (!stillBox || !centred || !volumeKept)
        {
            step.Fail("Геометрия сдвинулась, но не в предсказуемую коробку: " + after.Value.Describe()
                + " (коробка=" + stillBox + ", центрирована=" + centred + ", объём сохранён=" + volumeKept + ").");
            return;
        }

        step.Pass($"Перепривязка применена: {before.Value.Describe()} → {after.Value.Describe()}, "
            + "объём " + Api5.Num(after.Value.Volume) + " сохранён, десятимиллиметровое протяжение "
            + "перешло с оси " + ThicknessAxis(before.Value.Gabarit) + " на ось "
            + ThicknessAxis(after.Value.Gabarit) + ".");
    }

    // ── SP.6: read after the edit — control that the read is not a constant ─────────────────────
    private void ReadPlaneBackAfterEdit()
    {
        var step = _report.Begin("SP.6",
            "Чтение опоры после правки: GetPlane отвечает НОВОЙ плоскостью",
            "Чтение вообще различает две опоры, или отвечает одним и тем же?");

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var plane = TryGetPlane(definition);
        if (plane is not ksEntity entity)
        {
            step.Fail("GetPlane() после правки не дал объекта: " + Api5.RuntimeName(plane));
            return;
        }

        step.Data["plane_name"] = entity.name;
        step.Data["plane_type"] = entity.type;
        step.Data["expected_type"] = PlaneXoz;
        var api7 = ReadPlaneApi7(_sketch, step);
        step.Data["api7_plane"] = api7 ?? "не ответил";

        if (entity.type == PlaneXoz)
        {
            step.Pass("GetPlane() после правки отвечает типом " + entity.type + " («" + entity.name
                + "») — чтение различает опоры, а не повторяет константу; API7: " + (api7 ?? "не ответил"));
        }
        else
        {
            step.Fail("GetPlane() после правки отвечает типом " + entity.type + " («" + entity.name
                + "») вместо ожидаемого " + PlaneXoz + " — либо запись не применилась, либо чтение не различает.");
        }
    }

    // ── SP.8: non-plane — edge AND body ─────────────────────────────────────────────────────────
    private void WriteNonPlaneObject()
    {
        var step = _report.Begin("SP.8",
            "SetPlane не-плоскостью: ребро тела и само тело — ответ ядра",
            "Что делает ядро, если в опору эскиза подать ребро или тело?");
        step.Observe("Продукт обязан отвергать такое ДО COM именным кодом; здесь измеряется ТОЛЬКО "
            + "поведение ядра, и оно не наследуется продуктом.");

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        // Both cases from order §3.1 P3: "edge/body". The setups are independent, so the answers are
        // named separately rather than with one word "non-plane".
        var edge = FirstEntityOfType(EdgeType);
        var edgeResult = edge is null
            ? "ребро не найдено"
            : Attempt(step, definition, edge, "edge");
        step.Data["edge_found"] = edge is not null;
        step.Data["edge_result"] = edgeResult;

        object body = _part.GetMainBody();
        var bodyResult = Attempt(step, definition, body, "body");
        step.Data["body_result"] = bodyResult;

        step.Pass("Ответ ядра: ребро → " + edgeResult + "; тело → " + bodyResult
            + ". Это ФАКТ О ЯДРЕ, а не разрешение продукту передавать не-плоскость в COM.");
    }

    /// <summary>One non-plane setup: the call's answer, the extent shift, and what became the support.</summary>
    private string Attempt(ProbeStep step, ksSketchDefinition definition, object candidate, string tag)
    {
        var before = Snapshot(step, tag + "_before");
        if (before is not { } start)
        {
            // An empty snapshot must not be fed into the ladder: Same(null, …) answers "not equal", and
            // the step would be named as having moved NOTHING. The unmeasured is called unmeasured.
            step.Data[tag + "_moved"] = null;
            return "габарит до постановки не прочитан — постановка не оценена";
        }

        var accepted = TrySetPlane(definition, candidate, step, tag + "_accepted");
        var after = RebuildLadder(step, start, tag);
        var planeAfter = TryGetPlane(definition);

        var moved = after is not null && !Same(start, after.Value);
        step.Data[tag + "_moved"] = moved;
        step.Data[tag + "_plane_after_type"] = planeAfter is ksEntity pe ? pe.type : (int?)null;
        step.Data[tag + "_runtime"] = Api5.RuntimeName(candidate);

        return "SetPlane=" + (accepted?.ToString() ?? "исключение")
            + ", габарит " + (moved ? "СДВИНУЛСЯ" : "не сдвинулся")
            + ", опора после — " + (planeAfter is ksEntity pe2 ? "тип " + pe2.type : "не прочитана");
    }

    // ── SP.9: planar face as support ────────────────────────────────────────────────────────────
    private void WritePlanarFaceCandidate()
    {
        var step = _report.Begin("SP.9",
            "SetPlane плоской гранью тела: годится ли грань в опору перепривязки?",
            "Может ли опубликованный контракт принимать грань там, где принимает плоскость?");

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var faces = EntitiesOfType(FaceType, 8);
        step.Data["faces_found"] = faces.Count;
        if (faces.Count == 0)
        {
            step.Unknown("Объектов типа " + FaceType + " (o3d_face) в документе нет — грань не поставлена.");
            return;
        }

        // A DISCRIMINATING SETUP, NOT ONE NAMED ANSWER. The 40×40×10 box has six faces: two parallel to
        // the current support and four perpendicular to it. If a face is fit for the re-bind support at
        // all, the perpendicular one must turn the box; if none of the six turns it, "face accepted" means
        // only that the property was written. MEASURED: the first run (15:12) fed ONE face and so could
        // not separate these two explanations.
        var moved = new List<string>();
        var accepted = new List<string>();
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            var before = Snapshot(step, "face" + i + "_before");
            if (before is not { } start)
            {
                step.Observe("Грань " + i + ": габарит до постановки не прочитан — постановка не оценена.");
                continue;
            }

            var result = TrySetPlane(definition, face, step, "face" + i + "_accepted");
            var after = RebuildLadder(step, start, "face" + i);
            var planeAfter = TryGetPlane(definition);

            var movedNow = after is not null && !Same(start, after.Value);
            step.Data["face" + i + "_type"] = face.type;
            step.Data["face" + i + "_moved"] = movedNow;
            step.Data["face" + i + "_plane_after_type"] = planeAfter is ksEntity pe ? pe.type : (int?)null;
            if (result == true)
            {
                accepted.Add(i.ToString(CultureInfo.InvariantCulture));
            }

            if (movedNow)
            {
                moved.Add(i.ToString(CultureInfo.InvariantCulture));
            }
        }

        step.Data["faces_accepted"] = accepted.Count == 0 ? "ни одна" : string.Join(",", accepted);
        step.Data["faces_that_moved"] = moved.Count == 0 ? "ни одна" : string.Join(",", moved);
        step.Data["gabarit_after"] = Describe(Gabarit(_part));

        if (moved.Count > 0)
        {
            step.Pass("Грань годится в опору перепривязки: из " + faces.Count + " граней коробки "
                + moved.Count + " (" + string.Join(",", moved) + ") развернули зависимое тело; "
                + "принято ядром " + step.Data["faces_accepted"] + ".");
            return;
        }

        step.Pass("Ядро ПРИНИМАЕТ грань в опору (принято " + step.Data["faces_accepted"] + " из "
            + faces.Count + "), но ни одна из " + faces.Count + " граней этой заготовки геометрию "
            + "не развернула: «принято» здесь означает запись свойства. Различающая постановка "
            + "поставлена на ВСЕХ гранях, а не на одной — иначе эти два объяснения были бы неразличимы.");
    }

    // ── SP.10: an AUXILIARY plane built through API7 (the product's own route) ──────────────────
    /// <summary>Can an auxiliary plane, created the way <c>kompas_create_aux_geometry</c> creates it,
    /// serve as the sketch support — and WHICH documented transfer brings it into the API5 object the
    /// kernel accepts?</summary>
    /// <remarks>DOC: <c>kssketchdefinition_setplane.html</c> names the parameter «указатель на интерфейс
    /// базовой плоскости эскиза <c>ksEntity</c> или <c>IEntity</c>» — the plane is not required to come
    /// from the API5 factory. The product's auxiliary planes are created through the documented API7 route
    /// (<c>IPlanes3D.Add(o3d_planeOffset)</c> → <c>IPlane3DByOffset</c>,
    /// <c>ksapi_iplane3dbyoffset_setbaseplane.html</c>), so the question is whether THAT object reaches
    /// <c>SetPlane</c>, and by which of the documented transfers.
    /// DOC: <c>kompasobject_transferinterface.html</c> — <c>TransferInterface(obj, apiNewType, objNewType)</c>
    /// takes <c>apiNewType</c> from <c>ksAPITypeEnum</c> and, per its notes, (2) returns the SOURCE object
    /// when the new API matches the object's own, (3) also returns it when <c>apiNewType = 0</c>, and (6)
    /// yields the base object's interface when <c>objNewType = 0</c>, RECOMMENDING an explicit object type.
    /// EXPECTED (declared BEFORE the run): notes (2) and (3) fix the answer for the SAME-API call and for
    /// <c>objNewType = 0</c> — the source object comes back, and that is a property of the DOCUMENTED CALL,
    /// not of the kernel. For the OTHER API with an EXPLICIT object type the help promises nothing, so the
    /// kernel decides and the step NAMES the branch it landed in instead of assuming one. All three calls
    /// are therefore made and recorded side by side; none of their answers is averaged with another.
    /// History: docs/decisions/probes.md#sp-plane</remarks>
    private void WriteAuxiliaryPlaneCandidate()
    {
        var step = _report.Begin("SP.10",
            "SetPlane вспомогательной плоскостью, созданной API7 (маршрут kompas_create_aux_geometry)",
            "Каким документированным переносом объект API7-плоскости попадает в опору эскиза?");

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var plane = NewAuxOffsetPlane(step);
        if (plane is null)
        {
            step.Unknown("Вспомогательная плоскость API7 не создана — постановка не сделана.");
            return;
        }

        step.Data["aux_runtime"] = Api5.RuntimeName(plane);
        step.Data["aux_is_ksEntity"] = plane is ksEntity;
        step.Data["aux_is_iplane3d"] = plane is KompasAPI7.IPlane3D;

        // THREE DOCUMENTED CALLS, EACH RECORDED SEPARATELY. The previous revision made only the
        // same-API call and read its answer as a property of the kernel; the help fixes that answer by
        // its own notes (2)/(3), so the call measured the DOCUMENT. The main call asks for the other API
        // with the wanted object type named explicitly — the only form the help leaves open.
        var main = Transfer(step, plane, Api5Auto, PlaneOffsetObjectType, "main");
        var baseType = Transfer(step, plane, Api5Auto, ApiUndef, "basetype");
        var sameApi = Transfer(step, plane, Api7Dual, ApiUndef, "sameapi");

        // Does API5 SEE the plane the API7 factory made? If it does, the product has a second, independent
        // route to an API5 entity — and it is the reference the transferred object is compared against.
        var api5Planes = EntitiesOfType(PlaneOffset, 16);
        step.Data["api5_offset_plane_count"] = api5Planes.Count;
        step.Data["api5_offset_plane_names"] = string.Join(" | ", api5Planes.Select(p => p.name ?? "?"));
        var byName = api5Planes.FirstOrDefault(p => p.name == "SP-aux");
        step.Data["api5_found_by_name"] = byName is not null;

        var before = Snapshot(step, "before");
        if (before is not { } start)
        {
            step.Unknown("Габарит до постановки не прочитан — постановка не оценена.");
            return;
        }

        // Main attempt: the object the documented transfer returned, if it is the kind SetPlane takes.
        bool? acceptedMain = null;
        var movedMain = false;
        SnapshotValue? last = null;
        if (main is ksEntity mainEntity)
        {
            acceptedMain = TrySetPlane(definition, mainEntity, step, "main_accepted");
            var afterMain = RebuildLadder(step, start, "main");
            var planeAfterMain = TryGetPlane(definition) as ksEntity;
            movedMain = afterMain is not null && !Same(start, afterMain.Value);
            step.Data["main_moved"] = movedMain;
            step.Data["main_plane_after_type"] = planeAfterMain?.type;
            step.Data["main_plane_after_name"] = planeAfterMain?.name;
            last = afterMain;
        }
        else
        {
            // An unmeasured quantity is named unmeasured, not written as "no".
            step.Data["main_moved"] = null;
            step.Observe("Основной перенос (ksAPI5Auto, o3d_planeOffset) не дал ksEntity — подавать в "
                + "опору нечего, и это названо, а не выдано за отказ ядра.");
        }

        // Is the transferred object the SAME MODEL OBJECT the API5 enumeration shows? Compared on the
        // quantities that survive COM identity — model type, name, and the kernel's own answers. When the
        // transfer yielded no ksEntity there is nothing to compare, and that is SAID rather than left as
        // three false flags that would read as "a different object".
        if (main is ksEntity transferred && byName is not null)
        {
            step.Data["main_matches_byname_type"] = transferred.type == byName.type;
            step.Data["main_matches_byname_name"] =
                string.Equals(transferred.name, byName.name, StringComparison.Ordinal);
            step.Data["main_matches_byname_reference"] = ReferenceEquals(transferred, byName);
        }
        else
        {
            step.Observe("Сверка перенесённого объекта с перечислением не поставлена: перенос ksEntity "
                + "не дал — сравнивать не с чем.");
        }

        // The by-name object is applied as well, so "same model object" is checked by the kernel's own
        // answer and by the geometry, not by the type/name pair alone.
        bool? acceptedByName = null;
        var movedByName = false;
        if (byName is not null)
        {
            var beforeName = Snapshot(step, "byname_before");
            acceptedByName = TrySetPlane(definition, byName, step, "byname_accepted");
            var afterName = RebuildLadder(step, beforeName ?? start, "byname");
            var planeAfterName = TryGetPlane(definition) as ksEntity;
            movedByName = afterName is not null && !Same(beforeName ?? start, afterName.Value);
            step.Data["byname_moved"] = movedByName;
            step.Data["byname_plane_after_type"] = planeAfterName?.type;
            step.Data["byname_plane_after_name"] = planeAfterName?.name;
            // With the geometry already moved by the main attempt, the SAME plane must leave it in place:
            // "did not move" then reads as agreement, not as a refusal, and is named as such.
            step.Data["byname_agrees_with_main"] = last is { } mainState && afterName is not null
                && Same(mainState, afterName.Value);
            last = afterName ?? last;
        }
        else
        {
            step.Observe("Плоскость «SP-aux» в API5-перечислении o3d_planeOffset не найдена — сверка с "
                + "перечислением не поставлена, и это названо.");
        }

        var after = last ?? Snapshot(step, "after");
        step.Data["gabarit_before"] = start.Describe();
        step.Data["gabarit_after"] = after?.Describe() ?? "не прочитан";
        step.Data["expected_if_applied"] = "коробка 40×40×10, тонкое протяжение по Z, z∈[25,35]";

        var appliedBy = movedMain ? "объект API7 принят после переноса ksAPI5Auto/o3d_planeOffset"
            : movedByName ? "перенос ksAPI5Auto/o3d_planeOffset опору не дал; сработал только объект, "
                + "взятый из ПЕРЕЧИСЛЕНИЯ API5 по имени"
            : "ни один заход не сдвинул геометрию";

        step.Pass("Вспомогательная плоскость API7 в опоре: " + appliedBy
            + ". Переносы: (ksAPI5Auto, o3d_planeOffset) → " + Api5.RuntimeName(main)
            + " (ksEntity=" + step.Data["main_is_ksEntity"] + ")"
            + ", (ksAPI5Auto, 0) → " + Api5.RuntimeName(baseType)
            + " (ksEntity=" + step.Data["basetype_is_ksEntity"] + ")"
            + ", (ksAPI7Dual, 0) → " + Api5.RuntimeName(sameApi)
            + " (тот же объект=" + step.Data["sameapi_same_object"] + ", документованное следствие)"
            + "; SetPlane(перенесённый)=" + (acceptedMain?.ToString() ?? "не подавался")
            + (acceptedByName is null ? string.Empty : ", SetPlane(объект из перечисления)=" + acceptedByName)
            + "; сверка с перечислением: " + (step.Data.ContainsKey("main_matches_byname_type")
                ? "тип=" + step.Data["main_matches_byname_type"]
                    + ", имя=" + step.Data["main_matches_byname_name"]
                    + ", тот же объект=" + step.Data["main_matches_byname_reference"]
                : "не поставлена (перенос ksEntity не дал)")
            + "; габарит " + step.Data["gabarit_before"] + " → " + step.Data["gabarit_after"] + ".");
    }

    /// <summary>One documented <c>TransferInterface</c> call, with everything its answer carries: the
    /// runtime type, whether it answers <c>QI(ksEntity)</c>, whether it IS the source object, and — when
    /// it is a <c>ksEntity</c> — its model type and name. Named by <paramref name="tag"/> so three
    /// answers are never merged into one.</summary>
    private object? Transfer(ProbeStep step, object source, int apiNewType, int objNewType, string tag)
    {
        var result = Api5.SafeObject(() => _app.TransferInterface(source, apiNewType, objNewType));
        step.Data[tag + "_api_new_type"] = apiNewType;
        step.Data[tag + "_obj_new_type"] = objNewType;
        step.Data[tag + "_runtime"] = Api5.RuntimeName(result);
        step.Data[tag + "_is_ksEntity"] = result is ksEntity;
        step.Data[tag + "_is_iplane3d"] = result is KompasAPI7.IPlane3D;
        step.Data[tag + "_same_object"] = result is not null && ReferenceEquals(result, source);
        if (result is ksEntity entity)
        {
            step.Data[tag + "_entity_type"] = entity.type;
            step.Data[tag + "_entity_name"] = Api5.SafeObject(() => entity.name);
        }

        return result;
    }

    // ── SP.11: an API5 offset plane built over a FACE ───────────────────────────────────────────
    /// <summary>Can the documented API5 offset plane be built over a FACE, and does that plane then
    /// serve as the sketch support? This is the route that yields an API5 <c>ksEntity</c> — the only
    /// object kind <c>SetPlane</c> has been measured to accept.</summary>
    /// <remarks>DOC: the API5 member is <c>ksPlaneOffsetDefinition.SetPlane(LPENTITY)</c> — «указатель на
    /// интерфейс плоскости ksEntity или IEntity» (<c>ksplaneoffsetdefinition_setplane.html</c>); the
    /// API7 twin is documented wider, «базовая плоскость ИЛИ ПЛОСКАЯ ГРАНЬ»
    /// (<c>ksapi_iplane3dbyoffset_setbaseplane.html</c>). The two pages do NOT say the same thing, so the
    /// kernel is asked rather than the documentation read one way.
    /// History: docs/decisions/probes.md#sp-plane</remarks>
    private void WriteApi5PlaneOverFaceCandidate()
    {
        var step = _report.Begin("SP.11",
            "Смещённая плоскость API5, построенная ОТ ГРАНИ, как опора эскиза",
            "Принимает ли API5-маршрут грань базой — и годится ли полученная плоскость в опору?");

        if (_sketch is null || _sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Unknown("Определения эскиза нет.");
            return;
        }

        var face = FirstEntityOfType(FaceType);
        if (face is null)
        {
            step.Unknown("Граней типа " + FaceType + " в документе нет — постановка не сделана.");
            return;
        }

        step.Data["face_name"] = face.name;
        step.Data["face_type"] = face.type;

        var plane = NewApi5OffsetPlaneOver(face, step);
        if (plane is null)
        {
            step.Pass("API5-маршрут смещённой плоскости грань базой НЕ принял: см. данные шага. "
                + "Это ФАКТ О МАРШРУТЕ, а не отказ продукта.");
            return;
        }

        step.Data["plane_is_ksEntity"] = plane is ksEntity;
        step.Data["plane_type"] = plane.type;

        var before = Snapshot(step, "before");
        if (before is not { } start)
        {
            step.Unknown("Габарит до постановки не прочитан — постановка не оценена.");
            return;
        }

        var accepted = TrySetPlane(definition, plane, step, "accepted");
        var after = RebuildLadder(step, start, "api5");
        var planeAfter = TryGetPlane(definition) as ksEntity;
        var moved = after is not null && !Same(start, after.Value);
        step.Data["moved"] = moved;
        step.Data["plane_after_type"] = planeAfter?.type;
        step.Data["gabarit_before"] = start.Describe();
        step.Data["gabarit_after"] = after?.Describe() ?? "не прочитан";

        step.Pass("Плоскость API5 от грани: создана (тип " + plane.type + "), в опору SetPlane="
            + (accepted?.ToString() ?? "исключение") + ", габарит " + step.Data["gabarit_before"]
            + " → " + step.Data["gabarit_after"] + " (сдвинулся=" + moved + ").");
    }

    /// <summary>An offset plane created the API5 way over the given entity (a face here):
    /// <c>NewEntity(o3d_planeOffset)</c> + <c>ksPlaneOffsetDefinition.SetPlane</c> + offset/direction.</summary>
    private ksEntity? NewApi5OffsetPlaneOver(ksEntity basis, ProbeStep step)
    {
        try
        {
            if (_part.NewEntity(PlaneOffset) is not ksEntity plane)
            {
                step.Observe("NewEntity(o3d_planeOffset=14) не дал ksEntity.");
                return null;
            }

            plane.name = "SP-api5-from-face";
            if (plane.GetDefinition() is not ksPlaneOffsetDefinition offsetDefinition)
            {
                step.Observe("определение смещённой плоскости не получено.");
                return null;
            }

            var basisAccepted = TrySetBasisPlane(offsetDefinition, basis, step, "basis_accepted");
            offsetDefinition.offset = AuxPlaneOffset;
            offsetDefinition.direction = true;
            step.Data["create"] = Api5.Raw(TryBool(plane.Create));
            if (basisAccepted != true || TryBool(plane.Create) != true)
            {
                step.Observe("грань как база смещённой плоскости: SetPlane=" + basisAccepted);
                return null;
            }

            return plane;
        }
        catch (Exception ex)
        {
            step.Observe("построение плоскости API5 от грани бросило " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>An offset plane created exactly the way the product creates auxiliary planes: API7
    /// <c>IPlanes3D.Add(o3d_planeOffset)</c> over the standard XY plane, then <c>Update()</c>.</summary>
    private object? NewAuxOffsetPlane(ProbeStep step)
    {
        try
        {
            if (_app.TransferInterface(_part, Api7Dual, ApiUndef) is not KompasAPI7.IModelObject part7)
            {
                step.Observe("деталь не переносится в API7 как IModelObject.");
                return null;
            }

            if (part7 is not KompasAPI7.IAuxiliaryGeomContainer auxiliary
                || auxiliary.Planes3D is not KompasAPI7.IPlanes3D planes)
            {
                step.Observe("деталь не отвечает QI(IAuxiliaryGeomContainer)/Planes3D.");
                return null;
            }

            if (planes.Add(ksObj3dTypeEnum.o3d_planeOffset) is not KompasAPI7.IPlane3D plane)
            {
                step.Observe("IPlanes3D.Add(o3d_planeOffset) не дал IPlane3D.");
                return null;
            }

            if (plane is not KompasAPI7.IPlane3DByOffset byOffset)
            {
                step.Observe("созданная плоскость не отвечает QI(IPlane3DByOffset).");
                return null;
            }

            if (_part.GetDefaultEntity(PlaneXoy) is not { } basePlane
                || _app.TransferInterface(basePlane, Api7Dual, ApiUndef) is not KompasAPI7.IModelObject basePlane7)
            {
                step.Observe("базовая плоскость XY не перенесена в API7.");
                return null;
            }

            plane.Name = "SP-aux";
            byOffset.BasePlane = basePlane7;
            byOffset.Offset = AuxPlaneOffset;
            byOffset.Direction = true;
            step.Data["aux_update"] = Api5.Raw(Api5.SafeBool(() => plane.Update()));

            // The product rebuilds after creating auxiliary geometry, and the probe mirrors it: without
            // the rebuild the object sits in the container while the model does not know it. Leaving it
            // out would make a probe defect look like a kernel refusal.
            TryBool(_part.RebuildModel);
            _doc.RebuildDocument();
            return plane;
        }
        catch (Exception ex)
        {
            step.Observe("создание вспомогательной плоскости бросило " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ── SP.7: save→close→reopen, then edit again ────────────────────────────────────────────────
    private void ReopenThenEditAgain()
    {
        var step = _report.Begin("SP.7",
            "save→close→reopen: применилась ли правка при загрузке, и работает ли правка с диска",
            "Состояние, которое видит продукт, а не только сеанс записи (класс 30)?");

        if (_sketch is null)
        {
            step.Unknown("Эскиза нет.");
            return;
        }

        _savedPath = Path.Combine(_options.WorkDir, "sketch-plane.m3d");
        step.Data["saved_path"] = _savedPath;
        if (_doc.SaveAs(_savedPath) != true)
        {
            step.Fail("SaveAs не подтверждён.");
            return;
        }

        step.Data["closed"] = Api5.Raw(TryBool(_doc.close));
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Open(_savedPath, true) != true)
        {
            step.Fail("Open не подтверждён — говорить о состоянии с диска нечего.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);

        // After reopen the sketch is taken from the TREE by feature, not from memory: the writing
        // session's reference need not survive the reopen (rule of probe G).
        var reopened = FindSketchOfFirstExtrusion(step);
        if (reopened is null)
        {
            step.Fail("Эскиз зависимого признака после reopen не найден — предмет правки потерян.");
            return;
        }

        _sketch = reopened;
        var volume = Api5.Volume(_part);
        var gabarit = Gabarit(_part);
        step.Data["volume_after_reopen"] = Api5.Num(volume);
        step.Data["gabarit_after_reopen"] = Describe(gabarit);
        step.Data["volume_expected"] = Api5.Num(BoxVolume);

        if (reopened.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("Определение эскиза после reopen не получено.");
            return;
        }

        // Read IMMEDIATELY and remembered as a number, not re-read when the verdict is assembled: after
        // the second reopen, a reference to a CLOSED document's object answers type 0. MEASURED in run
        // 15:18 — the step text printed "type 0" where the same step's data held 2.
        var reopenedPlane = TryGetPlane(definition) as ksEntity;
        var reopenedPlaneType = reopenedPlane?.type;
        step.Data["plane_after_reopen_type"] = reopenedPlaneType;
        step.Data["plane_after_reopen_name"] = reopenedPlane?.name;

        if (volume is not double v || Math.Abs(v - BoxVolume) > 1e-6)
        {
            step.Fail("После reopen объём " + Api5.Num(volume) + " вместо " + Api5.Num(BoxVolume)
                + " — состояние с диска не то, и правка шла бы по другому предмету.");
            return;
        }

        // DECISIVE QUESTION OF THE STEP: did edit SP.5 apply at document LOAD. If yes, "not recomputed in
        // the session" is a session property, not a route refusal; if no, the edit is written to the
        // property and affects the model in no state.
        var reopenedValue = new SnapshotValue(gabarit, volume);
        var appliedAtLoad = IsBox(gabarit, BoxSide, BoxSide, BoxHeight)
            && IsCentred(gabarit, BoxSide, BoxSide)
            && ThicknessAxis(gabarit) == "Y";
        step.Data["applied_at_load"] = appliedAtLoad;
        step.Data["thickness_axis_after_reopen"] = ThicknessAxis(gabarit);
        step.Data["gabarit_expected_if_applied"] = "коробка 40×40×10 с тонким протяжением по Y";

        // Reverse re-bind: xy+15 again, now on the on-disk document.
        var (backPlane, backFailure) = NewOffsetPlane("SP-back", PlaneXoy, OffsetMm);
        if (backPlane is null)
        {
            step.Fail("Обратная плоскость не создана: " + backFailure);
            return;
        }

        var accepted = TrySetPlane(definition, backPlane, step, "accepted_back");
        var afterBack = RebuildLadder(step, reopenedValue, "back");
        step.Data["gabarit_after_back"] = afterBack?.Describe() ?? "не прочитан";
        step.Data["back_accepted"] = accepted?.ToString() ?? "исключение";
        var supportAfterBack = TryGetPlane(definition) as ksEntity;
        var backPlaneType = supportAfterBack?.type;
        step.Data["plane_after_back_type"] = backPlaneType;

        var movedInSession = afterBack is not null && !Same(afterBack.Value, reopenedValue);
        step.Data["moved_back_in_session"] = movedInSession;

        // Second reopen cycle: the only state in which "written" must become "applied", if the route
        // affects the model at all.
        var secondPath = Path.Combine(_options.WorkDir, "sketch-plane-back.m3d");
        if (_doc.SaveAs(secondPath) != true)
        {
            step.Observe("Второй SaveAs не подтверждён — состояние после повторной загрузки не измерено.");
        }
        else
        {
            TryBool(_doc.close);
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(secondPath, true) == true)
            {
                _part = (ksPart)_doc.GetPart(-1);
                var gabarit2 = Gabarit(_part);
                var volume2 = Api5.Volume(_part);
                step.Data["gabarit_after_second_reopen"] = Describe(gabarit2);
                step.Data["volume_after_second_reopen"] = Api5.Num(volume2);
                step.Data["back_applied_at_load"] = IsBox(gabarit2, BoxSide, BoxSide, BoxHeight)
                    && IsCentred(gabarit2, BoxSide, BoxSide)
                    && ThicknessAxis(gabarit2) == "Z";
                step.Data["second_saved_path"] = secondPath;

                // The subject for the negative cases SP.8/SP.9 is the LIVE document's sketch, not the
                // closed one: the previous session's reference is invalid after the reopen.
                _sketch = FindSketchOfFirstExtrusion(step);
            }
            else
            {
                step.Observe("Второе Open не подтверждено.");
            }
        }

        // TWO HALVES, AND THEY ARE INDEPENDENT. MEASURED: run 15:16 gave moved_back_in_session=True AND
        // back_applied_at_load=True at once, while the earlier wording claimed "only at load, not in the
        // session" — contradicting its own data. The first branch fired earlier and masked the second.
        // Now both measured quantities are named in one sentence and neither is passed off as the other.
        var appliedBackAtLoad = step.Data["back_applied_at_load"] is true;
        var movedBy = step.Data["back_first_route_that_applied"];
        var inSession = movedInSession
            ? "сдвинуло габарит в сеансе (первая ступень — «" + movedBy + "»)"
            : "в сеансе габарит не сдвинуло";
        var atLoad = appliedBackAtLoad
            ? "и применилось при загрузке (габарит " + (step.Data["gabarit_after_second_reopen"] ?? "—") + ")"
            : "но при повторной загрузке состояние с диска не изменилось (габарит "
              + (step.Data["gabarit_after_second_reopen"] ?? "не прочитан") + ")";

        if (movedInSession || appliedBackAtLoad)
        {
            step.Pass("Две половины измерены раздельно. (1) Правка SP.5 (опора xz) в состоянии С ДИСКА: "
                + "applied_at_load=" + appliedAtLoad + ", опора после переоткрытия — тип "
                + (reopenedPlaneType?.ToString(CultureInfo.InvariantCulture) ?? "не прочитана")
                + " («" + (step.Data["plane_after_reopen_name"] ?? "—") + "»), габарит "
                + (step.Data["gabarit_after_reopen"] ?? "—") + ". "
                + "(2) Обратная перепривязка на документе С ДИСКА: принята ядром "
                + (accepted?.ToString() ?? "исключение") + ", " + inSession + " " + atLoad
                + "; опора читается обратно типом "
                + (backPlaneType?.ToString(CultureInfo.InvariantCulture) ?? "не прочитана")
                + ". Правка опоры меняет модель, а не только свойство.");
            return;
        }

        step.Fail("Правка опоры не влияет на модель НИ В ОДНОМ из измеренных состояний: принята ("
            + (accepted?.ToString() ?? "исключение") + "), читается обратно типом "
            + (backPlaneType?.ToString(CultureInfo.InvariantCulture) ?? "не прочитана")
            + ", но ни одна ступень пересборки и ни одна повторная загрузка габарит не сдвинули "
            + "(при загрузке после SP.5 сдвинулось=" + appliedAtLoad + ").");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Rebuild ladder: which routes force the dependent body to recompute after the support
    /// change; the first step that moves the extent is the one that applied the edit.</summary>
    /// <remarks>MEASURED: <c>SetPlane(xz) = True</c> with an unchanged extent — "accepted and not
    /// applied". A probe defect and a product boundary look alike there, so ALL documented steps are tried
    /// and only if none moves the geometry does it become a fact about the product. The steps are not
    /// mixed with the write: the extent is re-read between steps, else "moved" fits no single one.
    /// History: docs/decisions/probes.md#sp-ladder</remarks>
    private SnapshotValue? RebuildLadder(ProbeStep step, SnapshotValue before, string tag)
    {
        var routes = new (string Name, Action Apply)[]
        {
            ("definition.EndEdit()", () =>
            {
                if (_sketch?.GetDefinition() is ksSketchDefinition d)
                {
                    TryBool(d.EndEdit);
                }
            }),
            ("sketch.Update()", () =>
            {
                if (_sketch is not null)
                {
                    TryBool(_sketch.Update);
                }
            }),
            ("part.RebuildModel()", () => TryBool(_part.RebuildModel)),
            ("document.RebuildDocument()", () => _doc.RebuildDocument()),
        };

        var firstMover = (string?)null;
        SnapshotValue? last = null;
        foreach (var (name, apply) in routes)
        {
            try
            {
                apply();
            }
            catch (Exception ex)
            {
                step.Observe("Ступень «" + name + "» бросила " + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            last = Snapshot(step, tag + "_after_" + Slug(name));
            if (last is not { } current)
            {
                step.Observe("После «" + name + "»: габарит не прочитан — ступень не оценена.");
                continue;
            }

            var differs = !Same(before, current);
            var movedByThis = differs && firstMover is null;
            // TWO DIFFERENT ASSERTIONS, and they must not be confused. "Extent differs from the start" is
            // true even for a step that did nothing itself, if a previous step already moved the geometry:
            // MEASURED, run 15:16 — the `_moved` fields of RebuildModel and RebuildDocument were True
            // while it was `sketch.Update()` that moved it. The field is named after what it measures.
            step.Data[tag + "_" + Slug(name) + "_differs_from_start"] = differs;
            step.Data[tag + "_" + Slug(name) + "_moved_by_this_route"] = movedByThis;
            if (movedByThis)
            {
                // The FIRST moving step is the application route. Later steps are measured on the
                // already-moved geometry, so their "moved" adds nothing — hence both roles are named here,
                // not a list of "movers".
                firstMover = name;
                step.Observe("После «" + name + "»: " + current.Describe() + " — СДВИНУЛОСЬ (первая ступень)");
            }
            else if (differs)
            {
                step.Observe("После «" + name + "»: " + current.Describe()
                    + " — не сдвинулось ЭТОЙ ступенью (геометрия уже была сдвинута «" + firstMover + "»)");
            }
            else
            {
                step.Observe("После «" + name + "»: " + current.Describe() + " — не сдвинулось");
            }
        }

        step.Data[tag + "_first_route_that_applied"] = firstMover ?? "ни одна";
        if (firstMover is null && last is not null)
        {
            // No step moved the geometry — then the reopen recomputes it, and that is measured
            // separately: SP.7.
            step.Observe("Ни одна из " + routes.Length + " ступеней пересборки геометрию не сдвинула; "
                + "переоткрытие документа проверяется шагом SP.7.");
        }

        return last;
    }

    private static string Slug(string name) => name
        .Replace("definition.", string.Empty, StringComparison.Ordinal)
        .Replace("sketch.", string.Empty, StringComparison.Ordinal)
        .Replace("part.", string.Empty, StringComparison.Ordinal)
        .Replace("document.", string.Empty, StringComparison.Ordinal)
        .Replace("()", string.Empty, StringComparison.Ordinal);

    /// <summary>Sketch surface: did the sketch ITSELF move, not just the written support property. The
    /// normal is taken at the start of the parametric domain — <c>GetNormal(u, v, …)</c> is documented as
    /// a function of the surface parameters, and "take u = 0" would be a guess about its domain.</summary>
    /// </summary>
    private (double[]? Origin, double[]? Normal, string Note) SketchSurface()
    {
        try
        {
            if (_sketch?.GetDefinition() is not ksSketchDefinition definition)
            {
                return (null, null, "определения эскиза нет");
            }

            var surfaceObject = definition.GetSurface();
            if (surfaceObject is not KompasAPI7.IMathSurface3D surface)
            {
                return (null, null, "GetSurface() не приводится к IMathSurface3D ("
                    + Api5.RuntimeName(surfaceObject) + ")");
            }

            var u = surface.ParamUMin;
            var v = surface.ParamVMin;
            double[]? origin = surface.GetPoint(u, v, out var px, out var py, out var pz)
                ? [px, py, pz]
                : null;
            double[]? normal = surface.GetNormal(u, v, out var nx, out var ny, out var nz)
                ? [nx, ny, nz]
                : null;
            return (origin, normal, "прочитано");
        }
        catch (Exception ex)
        {
            return (null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Vector(double[]? value) => value is null || value.Length < 3
        ? "не прочитан"
        : "(" + Api5.Num(value[0]) + ", " + Api5.Num(value[1]) + ", " + Api5.Num(value[2]) + ")";

    private readonly record struct SnapshotValue(double[]? Gabarit, double? Volume)
    {
        public string Describe() => SketchPlaneProbe.Describe(Gabarit) + " V=" + Api5.Num(Volume);
    }

    private SnapshotValue? Snapshot(ProbeStep step, string tag)
    {
        var gabarit = Gabarit(_part);
        var volume = Api5.Volume(_part);
        if (gabarit is null)
        {
            step.Data[tag + "_gabarit"] = "не прочитан";
            return null;
        }

        step.Data[tag + "_gabarit"] = Describe(gabarit);
        step.Data[tag + "_volume"] = Api5.Num(volume);
        return new SnapshotValue(gabarit, volume);
    }

    private static bool Same(SnapshotValue a, SnapshotValue b)
    {
        if (a.Gabarit is null || b.Gabarit is null)
        {
            return false;
        }

        for (var i = 0; i < 6; i++)
        {
            if (Math.Abs(a.Gabarit[i] - b.Gabarit[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Main-body extent: <c>ksBody.GetGabarit(out …)</c> — six numbers, not an object.</summary>
    private static double[]? Gabarit(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return null;
            }

            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? [x1, y1, z1, x2, y2, z2]
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Describe(double[]? gabarit) => gabarit is null || gabarit.Length < 6
        ? "габарит не прочитан"
        : "x[" + Api5.Num(gabarit[0]) + "…" + Api5.Num(gabarit[3]) + "]"
            + " y[" + Api5.Num(gabarit[1]) + "…" + Api5.Num(gabarit[4]) + "]"
            + " z[" + Api5.Num(gabarit[2]) + "…" + Api5.Num(gabarit[5]) + "]";

    /// <summary>A box with two extents <paramref name="a"/> and one <paramref name="b"/>.</summary>
    private static bool IsBox(double[]? gabarit, double a, double a2, double b)
    {
        if (gabarit is null)
        {
            return false;
        }

        var extents = new[]
        {
            Math.Abs(gabarit[3] - gabarit[0]),
            Math.Abs(gabarit[4] - gabarit[1]),
            Math.Abs(gabarit[5] - gabarit[2]),
        };
        Array.Sort(extents);
        var expected = new[] { b, a, a2 };
        Array.Sort(expected);
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(extents[i] - expected[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The two forty-millimetre extents are centred on the model origin.</summary>
    private static bool IsCentred(double[]? gabarit, double a, double a2)
    {
        if (gabarit is null)
        {
            return false;
        }

        var axes = new (double Lo, double Hi)[]
        {
            (gabarit[0], gabarit[3]),
            (gabarit[1], gabarit[4]),
            (gabarit[2], gabarit[5]),
        };
        foreach (var (lo, hi) in axes)
        {
            if (Math.Abs(Math.Abs(hi - lo) - a) <= 1e-6 || Math.Abs(Math.Abs(hi - lo) - a2) <= 1e-6)
            {
                if (Math.Abs(lo + hi) > 1e-6)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Offset of the ten-millimetre extent from the origin along its own axis.</summary>
    private static string NormalSide(double[]? gabarit, double a, double a2)
    {
        if (gabarit is null)
        {
            return "не прочитан";
        }

        var axes = new[] { "X", "Y", "Z" };
        var values = new (double Lo, double Hi)[]
        {
            (gabarit[0], gabarit[3]),
            (gabarit[1], gabarit[4]),
            (gabarit[2], gabarit[5]),
        };
        for (var i = 0; i < 3; i++)
        {
            var extent = Math.Abs(values[i].Hi - values[i].Lo);
            if (Math.Abs(extent - a) > 1e-6 && Math.Abs(extent - a2) > 1e-6)
            {
                return axes[i] + ": " + Api5.Num(values[i].Lo) + "…" + Api5.Num(values[i].Hi);
            }
        }

        return "не найдено";
    }

    /// <summary>The axis along which the box is thin (10 mm) — i.e. the support normal axis.</summary>
    private static string ThicknessAxis(double[]? gabarit)
    {
        if (gabarit is null)
        {
            return "?";
        }

        var axes = new[] { "X", "Y", "Z" };
        for (var i = 0; i < 3; i++)
        {
            var lo = gabarit[i];
            var hi = gabarit[i + 3];
            if (Math.Abs(Math.Abs(hi - lo) - BoxHeight) <= 1e-6)
            {
                return axes[i];
            }
        }

        return "?";
    }

    private (ksEntity? Plane, string? Failure) NewOffsetPlane(string name, short baseType, double offsetMm)
    {
        try
        {
            if (_part.GetDefaultEntity(baseType) is not ksEntity basePlane)
            {
                return (null, $"GetDefaultEntity({baseType}) вернул null");
            }

            if (_part.NewEntity(PlaneOffset) is not ksEntity plane)
            {
                return (null, "NewEntity(o3d_planeOffset=14) не дал ksEntity");
            }

            plane.name = name;
            if (plane.GetDefinition() is not ksPlaneOffsetDefinition definition)
            {
                return (null, "определение смещённой плоскости не получено");
            }

            definition.SetPlane(basePlane);
            definition.offset = Math.Abs(offsetMm);
            definition.direction = offsetMm >= 0;
            if (TryBool(plane.Create) != true)
            {
                return (null, "Create() смещённой плоскости не подтверждён");
            }

            return (plane, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private (ksEntity? Sketch, string? Failure) NewSketchOn(ksEntity plane, string name)
    {
        try
        {
            if (_part.NewEntity(SketchType) is not ksEntity sketch)
            {
                return (null, "NewEntity(o3d_sketch=5) не дал ksEntity");
            }

            sketch.name = name;
            if (sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                return (null, "определение эскиза не получено");
            }

            if (definition.SetPlane(plane) != true)
            {
                return (null, "SetPlane при СОЗДАНИИ эскиза не принят");
            }

            if (TryBool(sketch.Create) != true)
            {
                return (null, "Create() эскиза не подтверждён");
            }

            return (sketch, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private (ksEntity? Extrusion, string? Failure) NewBaseExtrusion(ksEntity sketch, double depthMm)
    {
        try
        {
            if (_part.NewEntity(BaseExtrusion) is not ksEntity extrusion)
            {
                return (null, "NewEntity(o3d_baseExtrusion=24) не дал ksEntity");
            }

            extrusion.name = "SP-extrusion";
            if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                return (null, "определение выталкивания не получено");
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            definition.SetSideParam(true, 0 /* etBlind */, depthMm, 0d, false);
            if (TryBool(extrusion.Create) != true)
            {
                return (null, "Create() выталкивания не подтверждён");
            }

            return (extrusion, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private object? TryGetPlane(ksSketchDefinition definition)
    {
        try
        {
            return definition.GetPlane();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool? TrySetPlane(ksSketchDefinition definition, object plane, ProbeStep step, string tag)
    {
        try
        {
            var result = definition.SetPlane(plane);
            step.Data[tag] = result;
            return result;
        }
        catch (Exception ex)
        {
            step.Data[tag] = "исключение";
            step.Observe("SetPlane бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>The same write on the OFFSET plane's definition — the base of a plane, not a sketch
    /// support. A separate helper because the two members belong to different interfaces.</summary>
    private bool? TrySetBasisPlane(ksPlaneOffsetDefinition definition, object basis, ProbeStep step, string tag)
    {
        try
        {
            var result = definition.SetPlane(basis);
            step.Data[tag] = result;
            return result;
        }
        catch (Exception ex)
        {
            step.Data[tag] = "исключение";
            step.Observe("SetPlane базы плоскости бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private ksEntity? FirstEntityOfType(short type)
    {
        var found = EntitiesOfType(type, 1);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Up to <paramref name="max"/> objects of the given type — to set on ALL of them, not one.</summary>
    /// <remarks>Selecting by type number is appropriate here because the subject is tree elements of a
    /// given class (face, edge), not a feature definition: the tree-vs-factory number substitution happened
    /// on definitions (24 vs 25) and is already named in <see cref="FindSketchOfFirstExtrusion"/>.</remarks>
    private List<ksEntity> EntitiesOfType(short type, int max)
    {
        var result = new List<ksEntity>();
        try
        {
            if (_part.EntityCollection(type) is not ksEntityCollection collection)
            {
                return result;
            }

            for (var i = 0; i < collection.GetCount() && result.Count < max; i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity && entity.type == type)
                {
                    result.Add(entity);
                }
            }
        }
        catch (Exception)
        {
            return result;
        }

        return result;
    }

    /// <summary>The dependent feature's sketch after reopen — from the TREE, not the session's memory.</summary>
    /// <remarks>MEASURED, and it closes TWO INSTRUMENT DEFECTS that both looked like absence of the
    /// subject: a base extrusion appears in the tree under a DIFFERENT number than its factory number, so
    /// selecting by factory number finds nothing; and the element's definition is a DIFFERENT interface
    /// from the one its factory name suggests. Tree numbers and factory numbers are separate systems, as
    /// are the definition interfaces. All three extrusion definitions are tried, and the element's tree
    /// number is recorded in the data because it IS the measured quantity, not decoration.
    /// History: docs/decisions/probes.md#sp-route</remarks>
    private ksEntity? FindSketchOfFirstExtrusion(ProbeStep step)
    {
        try
        {
            if (_part.EntityCollection(110) is not ksEntityCollection operations)
            {
                step.Observe("EntityCollection(110) не дала коллекцию — эскиз ищется иначе.");
                return null;
            }

            var count = operations.GetCount();
            step.Data["operations"] = count;
            var types = new List<int>();
            for (var i = 0; i < count; i++)
            {
                if (operations.GetByIndex(i) is not ksEntity operation)
                {
                    continue;
                }

                types.Add(operation.type);
                var definition = operation.GetDefinition();
                var sketch = definition switch
                {
                    ksBaseExtrusionDefinition baseDefinition => baseDefinition.GetSketch() as ksEntity,
                    ksBossExtrusionDefinition bossDefinition => bossDefinition.GetSketch() as ksEntity,
                    ksCutExtrusionDefinition cutDefinition => cutDefinition.GetSketch() as ksEntity,
                    _ => null,
                };
                if (sketch is not null)
                {
                    step.Data["sketch_by_tree"] = sketch.name;
                    step.Data["extrusion_tree_type"] = operation.type;
                    step.Data["extrusion_factory_type"] = BaseExtrusion;
                    step.Data["extrusion_definition"] = definition?.GetType().Name ?? "null";
                    return sketch;
                }
            }

            step.Data["operation_types"] = string.Join(",", types);
            step.Observe("Среди " + count + " элементов дерева ни один не отдал определение выталкивания "
                + "с эскизом; типы элементов: " + string.Join(",", types));
        }
        catch (Exception ex)
        {
            step.Observe("Поиск эскиза по дереву бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        return null;
    }

    /// <summary>Reads the support by the API7 route (<c>ISketch.Plane</c>) — the one the adapter already
    /// uses. A route failure is named, not replaced by absence of a support.</summary>
    private static string? ReadPlaneApi7(ksEntity sketch, ProbeStep step)
    {
        try
        {
            if (sketch is not KompasAPI7.ISketch api7Sketch)
            {
                step.Data["api7_cast"] = "эскиз не приводится к ISketch";
                return null;
            }

            var plane = api7Sketch.Plane;
            if (plane is null)
            {
                step.Data["api7_cast"] = "ISketch.Plane → null";
                return null;
            }

            step.Data["api7_plane_type"] = Api5.Raw(plane.ModelObjectType);
            step.Data["api7_plane_name"] = plane.Name;
            return plane.ModelObjectType + " («" + plane.Name + "»)";
        }
        catch (Exception ex)
        {
            step.Data["api7_cast"] = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static bool? TryBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }
}
