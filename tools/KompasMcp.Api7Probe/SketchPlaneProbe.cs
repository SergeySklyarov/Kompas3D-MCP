using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба SP — смена ОПОРНОЙ плоскости существующего эскиза документированным
/// <c>ksSketchDefinition.SetPlane</c> и чтение её обратно <c>GetPlane</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем отдельная проба.</b> Наряд <c>AUX_SKETCH_PLANE_EDIT_DEVELOPER_PROMPT.md</c> §3.1
/// требует измерить ДО правки продукта, что документированный маршрут вообще работает на
/// поставленной сборке 24.0.0.2799: прецедент <c>DRB</c> показал, что страница справки может
/// существовать, а члена в поставке не быть. Вопрос ставится ПРОБОЙ, а не продуктом: маршрут,
/// которого в продукте ещё нет, нельзя измерять его же инструментом.
/// </para>
/// <para>
/// <b>Документационная основа снята 21.09.2026 с официального корня справки</b> (страницы лежат и в
/// зеркале <c>scratch/sdk-docs/</c>):
/// <c>kssketchdefinition_setplane.html</c> — «SetPlane — Изменить базовую плоскость эскиза»,
/// <c>BOOL SetPlane(LPENTITY plane)</c>, параметр — «указатель на интерфейс базовой плоскости эскиза
/// <c>ksEntity</c> или <c>IEntity</c>»; <c>kssketchdefinition_getplane.html</c> — «GetPlane —
/// Получить базовую плоскость эскиза», <c>LPENTITY GetPlane()</c>. Обе страницы перечислены в
/// «ISketchDefinition — методы» (<c>kssketchdefinition_methods.html</c>) вместе с
/// <c>GetSurface</c>, <c>GetLocation</c>/<c>SetLocation</c> и <c>UserSetPlacement</c>.
/// </para>
/// <para>
/// <b>Ожидания объявлены ДО прогона и выводятся аналитически, а не берутся из прежних прогонов.</b>
/// Заготовка — прямоугольник 40×40 с центром в начале координат эскиза, базовое выдавливание 10 мм:
/// <list type="bullet">
/// <item>объём 40·40·10 = <b>16000</b> мм³ — и до, и после смены опоры: меняется ПОЛОЖЕНИЕ, а не
/// размер, поэтому объём различающей величиной НЕ является, и это сказано прямо;</item>
/// <item>габарит — коробка 40×40×10, у которой ДВА протяжения по 40 центрированы на начале координат
/// модели, а третье (10) лежит со стороны нормали плоскости: на <c>xy+15</c> это
/// <c>x∈[−20,20], y∈[−20,20], z∈[15,25]</c>;</item>
/// <item>после переноса на <c>xz</c> та же коробка обязана встать так, что десятимиллиметровое
/// протяжение уйдёт с оси Z на ось Y: <c>x∈[−20,20], z∈[−20,20]</c>, а <c>y</c> станет отрезком
/// длиной 10. Сторона (знак) не предсказывается: справка её не задаёт, и она называется измеренной,
/// а не угаданной.</item>
/// </list>
/// </para>
/// <para>
/// <b>Различающая пара.</b> SP.4 пишет ТУ ЖЕ плоскость (геометрия обязана не измениться), SP.5 —
/// другую (обязана измениться). Одиночная запись «приняли и ничего не изменилось» не отличает
/// «правка не применяется» от «правка не нужна», поэтому шаги идут парой, а контроль «инструмент
/// вообще видит изменения» ставится ПОСЛЕ записей (класс 42).
/// </para>
/// <para>
/// Свой STA-поток, собственный невидимый экземпляр КОМПАС, свои документы в <c>scratch</c>. Чужие
/// процессы не завершаются, пользовательские модели не открываются.
/// </para>
/// </remarks>
internal sealed class SketchPlaneProbe
{
    // ── заготовка ───────────────────────────────────────────────────────────────────────────────
    private const double BoxSide = 40d;
    private const double BoxHeight = 10d;
    private const double OffsetMm = 15d;

    /// <summary>Объём коробки: 40·40·10. Одинаков до и после смены опоры — величина НЕ различающая.</summary>
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

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private string _savedPath = string.Empty;

    /// <summary>Эскиз, который перепривязывается: он один на весь прогон, чтобы каждая запись шла по
    /// тому же предмету, а не по свежему.</summary>
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
            // Переоткрытие идёт ДО отрицательных случаев: они пишут в ту же опору, и после них
            // «применилась ли правка SP.5 при загрузке» уже не измерить (правило порядка: заведомо
            // мутирующий случай — последним).
            ReopenThenEditAgain();
            WriteNonPlaneObject();
            WritePlanarFaceCandidate();
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

    // ── сеанс ───────────────────────────────────────────────────────────────────────────────────
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

    // ── заготовка ───────────────────────────────────────────────────────────────────────────────
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

        // Смещённая плоскость как ОБЪЕКТ модели: NewEntity(o3d_planeOffset=14) + ksPlaneOffsetDefinition.
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

    // ── чтение обратно: документированный GetPlane ──────────────────────────────────────────────
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

        // API7-маршрут: у ISketch опора читается свойством. Проба приводит и его: у адаптера этот
        // маршрут уже есть (Api7SketchEntities.ReadPlane), и расхождение двух маршрутов — факт.
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

    // ── SP.4: запись ТОЙ ЖЕ плоскости ───────────────────────────────────────────────────────────
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

    // ── SP.5: запись ДРУГОЙ плоскости — различающая половина пары ───────────────────────────────
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

    // ── SP.6: чтение после правки — контроль, что чтение не константа ───────────────────────────
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

    // ── SP.8: не-плоскость — ребро И тело ───────────────────────────────────────────────────────
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

        // Оба случая из наряда §3.1 P3: «ребро/тело». Постановки независимы, поэтому ответы
        // называются отдельно, а не одним словом «не-плоскость».
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

    /// <summary>Одна постановка не-плоскости: ответ вызова, сдвиг габарита и что стало опорой.</summary>
    private string Attempt(ProbeStep step, ksSketchDefinition definition, object candidate, string tag)
    {
        var before = Snapshot(step, tag + "_before");
        if (before is not { } start)
        {
            // Пустой снимок в лестницу подавать нельзя: Same(null, …) отвечает «не одинаково», и
            // ступень была бы названа сдвинувшей НИЧЕГО. Неизмеренное называется неизмеренным.
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

    // ── SP.9: плоская грань как опора ───────────────────────────────────────────────────────────
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

        // РАЗЛИЧАЮЩАЯ ПОСТАНОВКА, А НЕ ОДИН НАЗВАННЫЙ ОТВЕТ. Коробка 40×40×10 имеет шесть граней:
        // две параллельны текущей опоре и четыре — перпендикулярны ей. Если грань вообще годится в
        // опору перепривязки, то перпендикулярная обязана развернуть коробку; если ни одна из шести
        // её не разворачивает, «грань принята» означает только запись свойства. Первый прогон
        // (15:12) подавал ОДНУ грань и потому не различал этих двух объяснений.
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

    // ── SP.7: save→close→reopen, затем правка снова ─────────────────────────────────────────────
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

        // Эскиз после reopen берётся из ДЕРЕВА по признаку, а не из памяти: ссылка сеанса записи
        // после переоткрытия не обязана быть той же (правило пробы G).
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

        // Читается СРАЗУ и запоминается числом, а не перечитывается при составлении итога: после
        // второго переоткрытия ссылка на объект ЗАКРЫТОГО документа отвечает типом 0. Измерено в
        // прогоне 15:18 — текст шага напечатал «тип 0» там, где данные того же шага хранили 2.
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

        // РЕШАЮЩИЙ ВОПРОС ШАГА: применилась ли правка SP.5 при ЗАГРУЗКЕ документа. Если да, то
        // «не пересчитано в сеансе» — свойство сеанса, а не отказ маршрута; если нет — правка
        // записана в свойство и не влияет на модель ни в одном состоянии.
        var reopenedValue = new SnapshotValue(gabarit, volume);
        var appliedAtLoad = IsBox(gabarit, BoxSide, BoxSide, BoxHeight)
            && IsCentred(gabarit, BoxSide, BoxSide)
            && ThicknessAxis(gabarit) == "Y";
        step.Data["applied_at_load"] = appliedAtLoad;
        step.Data["thickness_axis_after_reopen"] = ThicknessAxis(gabarit);
        step.Data["gabarit_expected_if_applied"] = "коробка 40×40×10 с тонким протяжением по Y";

        // Обратная перепривязка: xy+15 заново, уже на документе с диска.
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

        // Второй цикл переоткрытия: единственное состояние, в котором «записано» обязано стать
        // «применено», если маршрут вообще влияет на модель.
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

                // Предмет для отрицательных случаев SP.8/SP.9 — эскиз ЖИВОГО документа, а не
                // закрытого: ссылка прежнего сеанса после переоткрытия недействительна.
                _sketch = FindSketchOfFirstExtrusion(step);
            }
            else
            {
                step.Observe("Второе Open не подтверждено.");
            }
        }

        // ДВЕ ПОЛОВИНЫ, И ОНИ НЕЗАВИСИМЫ. Прогон 15:16 дал moved_back_in_session=True И
        // back_applied_at_load=True одновременно, а прежняя формулировка утверждала «только при
        // загрузке, в сеансе не двигает» — то есть противоречила собственным данным. Первая ветка
        // срабатывала раньше и перекрывала вторую. Теперь обе измеренные величины называются в
        // одном предложении, и ни одна не выдаётся за другую.
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

    // ── вспомогательное ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Лестница пересборки: какие маршруты вообще заставляют зависимое тело пересчитаться после
    /// смены опоры. Ступени применяются по очереди, габарит читается ПОСЛЕ КАЖДОЙ, и первой
    /// сдвинувшей считается та, после которой он изменился.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем лестница, а не один вызов.</b> Первый прогон (21.09.2026, 15:08) дал
    /// <c>SetPlane(xz) = True</c> и неизменившийся габарит — то есть «принято и не применено».
    /// Это ровно тот случай, когда дефект прибора и граница продукта выглядят одинаково: маршрут
    /// пересборки у пробы мог быть неполным. Поэтому сначала перебираются ВСЕ документированные
    /// ступени, и только если ни одна не двигает геометрию, это становится фактом о продукте.
    /// </para>
    /// <para>
    /// <b>Ступени не смешиваются с записью.</b> Между ступенями габарит читается заново: иначе
    /// «сдвинулось» нельзя приписать ни одной из них.
    /// </para>
    /// </remarks>
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
            // ДВА РАЗНЫХ УТВЕРЖДЕНИЯ, и путать их нельзя. «Габарит отличается от исходного» верно и
            // для ступени, которая сама ничего не сделала, если геометрию уже сдвинула предыдущая:
            // в прогоне 15:16 поля `_moved` у RebuildModel и RebuildDocument стояли True при том,
            // что сдвинула именно `sketch.Update()`. Поле названо по тому, что оно меряет.
            step.Data[tag + "_" + Slug(name) + "_differs_from_start"] = differs;
            step.Data[tag + "_" + Slug(name) + "_moved_by_this_route"] = movedByThis;
            if (movedByThis)
            {
                // ПЕРВАЯ сдвинувшая ступень и есть маршрут применения. Последующие ступени меряются
                // уже на сдвинутой геометрии, поэтому «сдвинулось» у них ничего не добавляет — и
                // именно поэтому здесь называются обе роли, а не список «сдвинувших».
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
            // Ни одна ступень не сдвинула геометрию — тогда её пересчитывает переоткрытие, и это
            // измеряется отдельно: SP.7.
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

    /// <summary>
    /// Поверхность эскиза: сдвинулся ли САМ эскиз, а не только записанное свойство опоры. Нормаль
    /// берётся в начале параметрической области — <c>GetNormal(u, v, …)</c> документирован как
    /// функция параметров поверхности, и «взять u = 0» было бы догадкой о её области.
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

    /// <summary>Габарит главного тела: <c>ksBody.GetGabarit(out …)</c> — шесть чисел, а не объект.</summary>
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

    /// <summary>Коробка с двумя протяжениями <paramref name="a"/> и одним <paramref name="b"/>.</summary>
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

    /// <summary>Два сорокамиллиметровых протяжения центрированы на начале координат модели.</summary>
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

    /// <summary>Отступ десятимиллиметрового протяжения от начала координат по своей оси.</summary>
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

    /// <summary>Ось, вдоль которой коробка тонкая (10 мм) — то есть ось нормали опоры.</summary>
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

    private ksEntity? FirstEntityOfType(short type)
    {
        var found = EntitiesOfType(type, 1);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>До <paramref name="max"/> объектов указанного типа — для постановки на ВСЕХ, а не на одном.</summary>
    /// <remarks>
    /// Отбор по номеру типа здесь уместен, потому что предмет — элементы дерева заданного класса
    /// (грань, ребро), а не определение признака: подмена номера дерева фабричным случалась именно на
    /// определениях (24 против 25) и уже названа в <see cref="FindSketchOfFirstExtrusion"/>.
    /// </remarks>
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

    /// <summary>Эскиз зависимого признака после переоткрытия — из ДЕРЕВА, а не из памяти сеанса.</summary>
    /// <remarks>
    /// <para>
    /// <b>Два дефекта прибора закрыты здесь, и оба были выданы за отсутствие предмета.</b> Первый
    /// прогон (15:08) отбирал элементы с <c>type == o3d_baseExtrusion (24)</c> и не нашёл ни одного
    /// при <c>operations = 1</c>: базовое выталкивание видно в дереве под <b>25</b>
    /// (<c>o3d_bossExtrusion</c>), а не под своим фабричным 24 — номер дерева и номер фабрики разные
    /// системы. Второй прогон (15:10) уже не отбирал по номеру, но приводил определение к ОДНОМУ
    /// типу <c>ksBaseExtrusionDefinition</c>, а у 25-го элемента определение —
    /// <c>ksBossExtrusionDefinition</c>: по <c>docs/compatibility/kompas-api5-metadata.json</c> это
    /// ТРИ РАЗНЫХ интерфейса с тремя разными IID (<c>deefefe1…</c>, <c>deefefe4…</c>,
    /// <c>deefefe7…</c>) и без наследования. Перебираются все три.
    /// </para>
    /// <para>
    /// Номер дерева элемента записывается в данные: он и есть измеренная величина, а не украшение.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Чтение опоры маршрутом API7 (<c>ISketch.Plane</c>) — тем же, что уже использует адаптер.
    /// Отказ маршрута называется, а не подменяется отсутствием опоры.
    /// </summary>
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
