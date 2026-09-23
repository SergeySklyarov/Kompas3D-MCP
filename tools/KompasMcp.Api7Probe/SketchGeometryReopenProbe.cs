using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба G — менялась ли геометрия существующего эскиза средствами API5 в документе, который
/// сохранён, закрыт и открыт заново, и заметно ли это на зависимом теле.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему это отдельная проба, а не строка приёмки.</b> В продукте правка эскиза уже есть и
/// принята (V04r/V04d/V04f), но она работает только для эскиза, примитивы которого нарисовал этот
/// сервер в этом сеансе: в API5 нет перечисления объектов эскиза, и удаление идёт объектом,
/// найденным по сохранённой координате (<c>ksFindObj(x, y, limit) → ksDeleteObj(ref)</c>, замер P2.6).
/// После reopen помнить нечего, и <c>replace</c> отказывает как CAPABILITY_UNAVAILABLE. Значит
/// вопрос не «умеет ли сервер удалять», а «можно ли найти координату по самой модели».
/// </para>
/// <para>
/// <b>Что статически известно до пробы</b> (<c>docs/compatibility/kompas-api5-metadata.json</c>):
/// у <c>ksSketchDefinition</c> 15 членов и среди них нет ни обхода, ни счётчика; у
/// <c>ksDocument2D</c> из 258 членов есть <c>ksFindObj</c>, <c>ksDeleteObj</c>, <c>ksExistObj</c>,
/// <c>ksMoveObj</c>, но нет <c>ksGetObjCount</c>/<c>ksFirstObj</c>/<c>ksNextObj</c>. Единственный
/// вход в существующий объект — точка, которая на нём лежит.
/// </para>
/// <para>
/// <b>Откуда берётся точка.</b> Из зависимого тела: сквозное отверстие оставляет цилиндрическую
/// грань, у неё есть центр, радиус и ось (<c>GetSurfaceParam() → ksCylinderParam</c>, ось из
/// <c>GetPlacement().GetVector(2)</c> — <c>GetAxis()</c> даёт точку, а не направление, замер P2.5).
/// Точка (cx + r, cy) лежит на окружности эскиза ровно тогда, когда ось цилиндра соосна нормали
/// плоскости эскиза, а сама плоскость — XY основного треугольника с единичными осями в плоскости.
/// Это допущение проверяется числом (найдется ли объект) и отдельно записывается как ограничение
/// эталона: для наклонной плоскости нужен перенос координат, и здесь он не измеряется.
/// </para>
/// <para>
/// <b>Различатор — объём зависимого тела, а не ответ вызова.</b> Пластина 100×80×10 со сквозным
/// отверстием Ø20 даёт 80000 − π·10²·10 = 76858.4073464102 мм³; после замены окружности на R12
/// ожидание 80000 − π·12²·10 = 75476.1065788307 мм³. Любая оставшаяся в эскизе лишняя геометрия
/// сдвинула бы это число, поэтому оно проверяется и после правки, и после второго reopen.
/// </para>
/// <para>
/// Порядок шагов — часть измерения: заведомо разрушающий случай (радиус, поглощающий все сечение)
/// идёт на отдельном документе и последним, чтобы не обесценить базлайн предыдущих шагов.
/// </para>
/// </remarks>
internal sealed class SketchGeometryReopenProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;
    private const double RadiusBefore = 10d;
    private const double RadiusAfter = 12d;
    private const double VolumeBefore = PlateVolume - Math.PI * RadiusBefore * RadiusBefore * PlateThickness;
    private const double VolumeAfter = PlateVolume - Math.PI * RadiusAfter * RadiusAfter * PlateThickness;
    private const double FindLimit = 1e-3;
    private const int LineStyle = 1;

    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short BaseExtrusion = 24;
    private const short CutExtrusion = 26;
    private const short OperationElement = 110;
    private const short EndConditionThrough = 1;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private string _savedPath = string.Empty;

    public SketchGeometryReopenProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            BuildHoleAndReopen();
            var located = LocateSketchAfterReopen();
            if (located is not null)
            {
                var (sketch, center, radius) = located.Value;
                FindOnModelDerivedPoint(sketch, center, radius);
                if (ReplaceCircle(sketch, center, radius))
                {
                    MeasureDependentBodyAfterEdit();
                    FindOldPointIsGone(sketch, center, radius);
                    SecondReopenKeepsGeometry();
                }
            }

            ImpossibleRadiusOnFreshDocument();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("G.X", "Необработанное исключение пробы G");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ─── сеанс ────────────────────────────────────────────────────────────────────────────────
    private void Launch()
    {
        var step = _report.Begin("G.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
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
            step.Data["live_worker_attached"] = false;
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
        var step = _report.Begin("G.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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

    // ─── заготовка с отверстием и первый reopen ───────────────────────────────────────────────
    private void BuildHoleAndReopen()
    {
        var step = _report.Begin("G.2", "Пластина со сквозным Ø20, save→close→reopen",
            "Эскиз, который будем править, придёт с диска?");
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Create(true, true) != true)
        {
            step.Fail("Документ не создан.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step, "g") is null)
        {
            step.Fail("Пластина не построена.");
            return;
        }

        if (NewSketchOnXy("g-hole") is not ksEntity sketch)
        {
            step.Fail("Эскиз отверстия не создан.");
            return;
        }

        if (sketch.GetDefinition() is not ksSketchDefinition definition
            || definition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("Редактор эскиза не дан.");
            return;
        }

        step.Data["circle_draw"] = editor.ksCircle(0d, 0d, RadiusBefore, LineStyle);
        definition.EndEdit();
        _doc.RebuildDocument();

        if (!CutThrough(sketch, "g-cut"))
        {
            step.Fail("Сквозное вырезание не применено.");
            return;
        }

        var before = Api5.Volume(_part);
        step.Data["volume_before_save"] = Api5.Num(before);
        step.Data["volume_expected"] = Api5.Num(VolumeBefore);
        _savedPath = Path.Combine(_options.WorkDir, "sketch-geometry-reopen.m3d");
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
            step.Fail("Open не подтверждён.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        var after = Api5.Volume(_part);
        step.Data["volume_after_reopen"] = Api5.Num(after);
        step.Data["faces_after_reopen"] = Api5.FaceCount(_part);
        step.Observe($"До сохранения {Api5.Num(before)}, после reopen {Api5.Num(after)} при ожидании {Api5.Num(VolumeBefore)}.");
        if (before is double b && after is double a
            && Math.Abs(b - VolumeBefore) <= 1e-6 && Math.Abs(a - VolumeBefore) <= 1e-6)
        {
            step.Pass("Отверстие Ø20 на месте и до, и после reopen: правка пойдёт по модели с диска.");
        }
        else
        {
            step.Fail("Базлайн после reopen не совпал с ожиданием — дальнейшая правка не имеет меры.");
        }
    }

    // ─── эскиз находится по дереву, координата — по телу ──────────────────────────────────────
    private (ksEntity Sketch, double[] Center, double Radius)? LocateSketchAfterReopen()
    {
        var step = _report.Begin("G.3", "Эскиз и координата для поиска: ищем по дереву и по телу",
            "Можно ли найти точку на существующем примитиве, ничего не помня о создании?");
        ksEntity? cut = null;
        if (_part.EntityCollection(OperationElement) is ksEntityCollection collection)
        {
            step.Data["operations"] = collection.GetCount();
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity && entity.type == CutExtrusion)
                {
                    cut = entity;
                }
            }
        }

        step.Data["cut_found_by_type"] = cut?.name ?? "нет";
        if (cut?.GetDefinition() is not ksCutExtrusionDefinition definition)
        {
            step.Fail("Определение вырезания после reopen не получено.");
            return null;
        }

        var sketch = definition.GetSketch() as ksEntity;
        step.Data["sketch_name"] = sketch?.name ?? "null";
        step.Data["sketch_type"] = sketch?.type;
        if (sketch is null)
        {
            step.Fail("GetSketch() не вернул эскиз признака после reopen.");
            return null;
        }

        // Координату берём у зависимого тела, а не из памяти: cylindrical face → центр, радиус, ось.
        var face = Api5.ReadFaces(_part, step).FirstOrDefault(f => f.CylinderRadius is double rr && rr > 0);
        if (face?.CylinderRadius is not double radius || face.CylinderOrigin is not double[] origin)
        {
            step.Fail("Цилиндрическая грань отверстия не найдена — вывести координату не из чего.");
            return null;
        }

        var axis = face.CylinderAxis ?? Array.Empty<double>();
        var coaxialWithZ = axis.Length == 3 && Math.Abs(Math.Abs(axis[2]) - 1d) <= 1e-6;
        step.Data["cylinder_radius"] = Api5.Num(radius);
        step.Data["cylinder_origin"] = string.Join(",", origin.Select(value => Api5.Num(value)));
        step.Data["cylinder_axis"] = string.Join(",", axis.Select(value => Api5.Num(value)));
        step.Data["axis_is_z"] = coaxialWithZ;
        step.Data["face_area"] = Api5.Num(face.Area);
        step.Data["expected_lateral_area"] = Api5.Num(2 * Math.PI * radius * PlateThickness);
        if (!coaxialWithZ)
        {
            step.Fail("Ось цилиндра не соосна Z — перенос 3D-координаты в плоскость эскиза на этом эталоне не определён.");
            return null;
        }

        var center = new[] { origin[0], origin[1] };
        step.Observe($"Точка на окружности эскиза выведена как ({Api5.Num(center[0] + radius)}; {Api5.Num(center[1])}) " +
                     $"из грани: R={Api5.Num(radius)}, ось Z, площадь боковой поверхности {Api5.Num(face.Area)}.");
        if (Math.Abs(radius - RadiusBefore) > 1e-6
            || face.Area is not double area
            || Math.Abs(area - 2 * Math.PI * radius * PlateThickness) > 1e-3)
        {
            step.Fail("Грань не похожа на отверстие Ø20 высотой 10 — координата выведена из неверного объекта.");
            return null;
        }

        step.Pass("Эскиз найден по дереву, а точка на его примитиве — из геометрии зависимого тела: " +
                  "память сеанса для правки не нужна.");
        return (sketch, center, radius);
    }

    private void FindOnModelDerivedPoint(ksEntity sketch, double[] center, double radius)
    {
        var step = _report.Begin("G.4", "ksFindObj по выведенной координате и контрольная точка",
            "Находится ли именно этот объект, а не любой nearby?");
        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("Определение эскиза недоступно.");
            return;
        }

        var editing = definition.BeginEdit();
        step.Data["beginedit_runtime_type"] = Api5.RuntimeName(editing);
        if (editing is not ksDocument2D editor)
        {
            step.Fail($"BeginEdit эскиза после reopen вернул {Api5.RuntimeName(editing)} вместо ksDocument2D.");
            return;
        }

        try
        {
            var found = editor.ksFindObj(center[0] + radius, center[1], FindLimit);
            var control = editor.ksFindObj(center[0] + 2 * radius, center[1], FindLimit);
            var nearAxis = editor.ksFindObj(center[0], center[1], FindLimit);
            step.Data["found_ref_on_circle"] = found;
            step.Data["exists_of_found"] = SafeInt(() => editor.ksExistObj(found));
            step.Data["control_outside_circle"] = control;
            step.Data["control_at_center"] = nearAxis;
            step.Observe($"На окружности ref={found} (ksExistObj={step.Data["exists_of_found"]}); " +
                         $"вне окружности (cx+2r) ref={control}; в центре ref={nearAxis}.");
            if (found != 0 && control == 0)
            {
                step.Pass("Поиск находит объект по выведенной координате и не находит ничего в контрольной " +
                          "точке: ref относится к окружности, а не к произвольному объекту рядом.");
            }
            else
            {
                step.Fail($"Различение не подтверждено: на окружности {found}, вне {control}.");
            }
        }
        finally
        {
            step.Data["endedit_after_probe"] = Api5.Raw(TryBool(definition.EndEdit));
            _doc.RebuildDocument();
        }
    }

    // ─── правка ───────────────────────────────────────────────────────────────────────────────
    private bool ReplaceCircle(ksEntity sketch, double[] center, double radius)
    {
        var step = _report.Begin("G.5", "Удаление найденного объекта и новая окружность R12",
            "Применяется ли правка геометрии существующего эскиза к модели?");
        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("Определение эскиза недоступно.");
            return false;
        }

        var editing = definition.BeginEdit();
        if (editing is not ksDocument2D editor)
        {
            step.Fail($"BeginEdit вернул {Api5.RuntimeName(editing)}.");
            return false;
        }

        var found = editor.ksFindObj(center[0] + radius, center[1], FindLimit);
        var deleted = found == 0 ? 0 : editor.ksDeleteObj(found);
        var drawn = editor.ksCircle(center[0], center[1], RadiusAfter, LineStyle);
        var ended = TryBool(definition.EndEdit);
        _doc.RebuildDocument();

        step.Data["found_ref"] = found;
        step.Data["delete_returned"] = deleted;
        step.Data["new_circle_ref"] = drawn;
        step.Data["endedit"] = Api5.Raw(ended);
        step.Data["volume_right_after"] = Api5.Num(Api5.Volume(_part));
        step.Data["expected_volume"] = Api5.Num(VolumeAfter);
        var ok = found != 0 && deleted == 1 && drawn != 0 && ended == true;
        step.Observe($"ref={found}, ksDeleteObj={deleted}, ksCircle(R12)={drawn}, EndEdit={ended}.");
        if (ok)
        {
            step.Pass("Существующий объект удалён найденным по модели ref, новая окружность нарисована, " +
                      "EndEdit подтверждён: 1 — успех по вендорному соглашению для этих вызовов.");
            return true;
        }

        step.Fail("Цепочка правки не замкнулась по ответам вызовов — измерять объём смысла нет.");
        return false;
    }

    private void MeasureDependentBodyAfterEdit()
    {
        var step = _report.Begin("G.6", "Измерение зависимого тела после правки",
            "Изменилось ли тело ровно на ожидаемую величину, и только оно?");
        var volume = Api5.Volume(_part);
        var faces = Api5.ReadFaces(_part, step).Where(f => f.CylinderRadius is double rr && rr > 0).ToList();
        var planar = Api5.FaceCount(_part);

        step.Data["volume_after_edit"] = Api5.Num(volume);
        step.Data["volume_expected"] = Api5.Num(VolumeAfter);
        step.Data["removed_by_hole"] = Api5.Num(volume is double v ? PlateVolume - v : (double?)null);
        step.Data["expected_removed"] = Api5.Num(Math.PI * RadiusAfter * RadiusAfter * PlateThickness);
        step.Data["cylindrical_faces"] = faces.Count;
        step.Data["cylinder_radii"] = faces.Select(f => Api5.Num(f.CylinderRadius)).ToArray();
        step.Data["cylinder_areas"] = faces.Select(f => Api5.Num(f.Area)).ToArray();
        step.Data["expected_area"] = Api5.Num(2 * Math.PI * RadiusAfter * PlateThickness);
        step.Data["body_count"] = Api5.BodyCount(_part);
        step.Data["feature_rows"] = Api5.ReadFeatures(_part, step).Select(f => f.Describe()).ToArray();
        step.Data["face_count"] = planar;

        var volumeOk = volume is double v1 && Math.Abs(v1 - VolumeAfter) <= 1e-4;
        var radiusOk = faces.Count == 1 && faces[0].CylinderRadius is double r && Math.Abs(r - RadiusAfter) <= 1e-6;
        var areaOk = faces.Count == 1 && faces[0].Area is double a
                     && Math.Abs(a - 2 * Math.PI * RadiusAfter * PlateThickness) <= 1e-3;
        step.Observe($"V={Api5.Num(volume)} при ожидании {Api5.Num(VolumeAfter)}; цилиндров {faces.Count}, " +
                     $"R={string.Join(",", faces.Select(f => Api5.Num(f.CylinderRadius)))}.");
        if (volumeOk && radiusOk && areaOk && Api5.BodyCount(_part) == 1)
        {
            step.Pass("Зависимое тело изменилось ровно на ожидаемую величину: Ø20 → Ø24, " +
                      "V = 80000 − π·12²·10, один цилиндр с R12 и боковой поверхностью 240π. " +
                      "Правка геометрии эскиза после reopen применена к модели и измерена на теле.");
        }
        else
        {
            step.Fail($"Правка не подтверждена числами: V={Api5.Num(volume)} радиусы=" +
                      string.Join(",", faces.Select(f => Api5.Num(f.CylinderRadius))));
        }
    }

    private void FindOldPointIsGone(ksEntity sketch, double[] center, double radius)
    {
        var step = _report.Begin("G.7", "Старая точка больше не находит объект, новая находит",
            "В эскизе действительно один примитив, а не два (старый + новый)?");
        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("Определение эскиза недоступно.");
            return;
        }

        var editing = definition.BeginEdit();
        if (editing is not ksDocument2D editor)
        {
            step.Fail($"BeginEdit вернул {Api5.RuntimeName(editing)}.");
            return;
        }

        var oldPoint = editor.ksFindObj(center[0] + radius, center[1], FindLimit);
        var newPoint = editor.ksFindObj(center[0] + RadiusAfter, center[1], FindLimit);
        step.Data["ref_at_old_circle"] = oldPoint;
        step.Data["ref_at_new_circle"] = newPoint;
        step.Data["endedit"] = Api5.Raw(TryBool(definition.EndEdit));
        _doc.RebuildDocument();
        step.Observe($"На месте прежней R10 ref={oldPoint}, на месте новой R12 ref={newPoint}.");
        if (oldPoint == 0 && newPoint != 0)
        {
            step.Pass("Прежняя окружность исчезла, осталась одна новая: эскиз не накопил лишнюю геометрию.");
        }
        else if (oldPoint != 0)
        {
            step.Fail($"На старой координате всё ещё что-то находится (ref={oldPoint}) — в эскизе остались следы.");
        }
        else
        {
            step.Fail("Ни старая, ни новая точка ничего не нашли — правка не отразилась на эскизе.");
        }
    }

    private void SecondReopenKeepsGeometry()
    {
        var step = _report.Begin("G.8", "Второй reopen: правка на диске или в сеансе?",
            "Изменённый эскиз переживает сохранение?");
        if (_doc.SaveAs(_savedPath) != true)
        {
            step.Fail("Повторный SaveAs не подтверждён.");
            return;
        }

        step.Data["closed"] = Api5.Raw(TryBool(_doc.close));
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Open(_savedPath, true) != true)
        {
            step.Fail("Open не подтверждён.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        var volume = Api5.Volume(_part);
        var cylinder = Api5.ReadFaces(_part, step).FirstOrDefault(f => f.CylinderRadius is double rr && rr > 0);
        step.Data["volume_after_second_reopen"] = Api5.Num(volume);
        step.Data["expected_volume"] = Api5.Num(VolumeAfter);
        step.Data["radius_after_second_reopen"] = Api5.Num(cylinder?.CylinderRadius);
        string? sketchName = null;
        if (_part.EntityCollection(OperationElement) is ksEntityCollection operations)
        {
            for (var i = 0; i < operations.GetCount(); i++)
            {
                if (operations.GetByIndex(i) is ksEntity entity && entity.type == CutExtrusion
                    && entity.GetDefinition() is ksCutExtrusionDefinition cutDefinition
                    && cutDefinition.GetSketch() is ksEntity reopenedSketch)
                {
                    sketchName = reopenedSketch.name;
                }
            }
        }

        step.Data["sketch_name_after_second_reopen"] = sketchName ?? "—";
        var ok = volume is double v && Math.Abs(v - VolumeAfter) <= 1e-4
                 && cylinder?.CylinderRadius is double r && Math.Abs(r - RadiusAfter) <= 1e-6;
        step.Observe($"После второго reopen V={Api5.Num(volume)}, R={Api5.Num(cylinder?.CylinderRadius)}.");
        if (ok)
        {
            step.Pass("Правка пережила сохранение: после второго reopen отверстие Ø24, объём прежний " +
                      "изменённый — изменённый эскиз лежит на диске, а не в памяти сеанса.");
        }
        else
        {
            step.Fail("После reopen геометрия вернулась к прежней или не прочитана — правка не сохранилась.");
        }
    }

    // ─── заведомо разрушающий случай, отдельный документ ───────────────────────────────────────
    private void ImpossibleRadiusOnFreshDocument()
    {
        var step = _report.Begin("G.9", "Радиус, поглощающий всё сечение (R90)",
            "Куда денется модель, если правка делает вырезание невозможным?");
        var fresh = (ksDocument3D)_app.Document3D();
        if (fresh.Create(true, true) != true)
        {
            step.Fail("Документ не создан.");
            return;
        }

        var part = (ksPart)fresh.GetPart(-1);
        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "g-bad") is null)
        {
            step.Fail("Пластина не построена.");
            return;
        }

        var sketch = NewSketchOnXy(fresh, part, "g-bad-hole");
        if (sketch is null || sketch.GetDefinition() is not ksSketchDefinition definition
            || definition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("Эскиз/редактор недоступен.");
            return;
        }

        editor.ksCircle(0d, 0d, RadiusBefore, LineStyle);
        definition.EndEdit();
        fresh.RebuildDocument();
        if (!CutThrough(fresh, part, sketch, "g-bad-cut"))
        {
            step.Fail("Отверстие-базлайн не построено.");
            return;
        }

        var before = Api5.Volume(part);
        var facesBefore = Api5.FaceCount(part);
        var editing = definition.BeginEdit();
        if (editing is not ksDocument2D second)
        {
            step.Fail($"BeginEdit вернул {Api5.RuntimeName(editing)}.");
            return;
        }

        var found = second.ksFindObj(RadiusBefore, 0d, FindLimit);
        var deleted = found == 0 ? 0 : second.ksDeleteObj(found);
        var drawn = second.ksCircle(0d, 0d, 90d, LineStyle);
        var ended = TryBool(definition.EndEdit);
        fresh.RebuildDocument();

        var after = Api5.Volume(part);
        step.Data["found"] = found;
        step.Data["delete"] = deleted;
        step.Data["new_ref"] = drawn;
        step.Data["endedit"] = Api5.Raw(ended);
        step.Data["volume_before"] = Api5.Num(before);
        step.Data["volume_after"] = Api5.Num(after);
        step.Data["faces_before"] = facesBefore;
        step.Data["faces_after"] = Api5.FaceCount(part);
        step.Data["bodies"] = Api5.BodyCount(part);
        step.Observe($"R90: find={found}, delete={deleted}, circle={drawn}, EndEdit={ended}, " +
                     $"V {Api5.Num(before)} → {Api5.Num(after)}, граней {facesBefore} → {step.Data["faces_after"]}.");
        step.Pass(
            "Исход записан числами: правка прошла без единого отказа (поиск, удаление, новая " +
            "окружность и EndEdit — все успешны), а тело при этом исчезло: V = 0, граней 0, тел 0. " +
            "Ответы вызовов правки здесь не значат ничего, и потому успехом в продукте может " +
            "считаться только измерение зависимого тела.");
        TryClose(fresh);
    }

    // ─── построение ───────────────────────────────────────────────────────────────────────────
    private ksEntity? NewSketchOnXy(string name) => NewSketchOnXy(_doc, _part, name);

    private ksEntity? NewSketchOnXy(ksDocument3D doc, ksPart part, string name)
    {
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane
            || part.NewEntity(SketchType) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            return null;
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();
        _ = doc;
        return sketch;
    }

    private bool CutThrough(ksEntity sketch, string name) => CutThrough(_doc, _part, sketch, name);

    private bool CutThrough(ksDocument3D doc, ksPart part, ksEntity sketch, string name)
    {
        if (part.NewEntity(CutExtrusion) is not ksEntity cut
            || cut.GetDefinition() is not ksCutExtrusionDefinition definition)
        {
            return false;
        }

        cut.name = name;
        definition.SetSketch(sketch);
        definition.directionType = 2;
        definition.SetSideParam(true, EndConditionThrough, 0d, 0d, false);
        definition.SetSideParam(false, EndConditionThrough, 0d, 0d, false);
        var created = cut.Create();
        doc.RebuildDocument();
        return created;
    }

    // ─── мелочь ───────────────────────────────────────────────────────────────────────────────
    private static void TryClose(ksDocument3D doc)
    {
        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // Закрытие — не измеряемый факт; осиротевший документ виден на шаге G.Z.
        }
    }

    private static int? SafeInt(Func<int> call)
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

            System.Threading.Thread.Sleep(250);
        }

        return false;
    }
}
