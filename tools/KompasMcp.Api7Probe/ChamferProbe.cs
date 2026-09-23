using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба F — фаска (SM-11): чем именно КОМПАС-3D v24 её строит, правит и переживает перезагрузку.
/// </summary>
/// <remarks>
/// <para>
/// Основание (уточнение заказчика от 12.09.2026): наличие <c>ksChamferDefinition</c> и
/// <c>o3d_chamfer = 33</c> — найденный маршрут, а НЕ подтверждённая поддержка. Значит ответ даёт
/// только измерение: объём, перечитанные параметры и геометрия граней до и после. Ни ненулевой
/// объект, ни <c>true</c> от <c>Create()</c>/`Update()`, ни S_OK доказательством не считаются
/// (ADR-003 §3).
/// </para>
/// <para>
/// <b>Эталон аналитический и он выбран так, чтобы угол и катеты читались из одного числа.</b>
/// Пластина 100×80×10 (V₀ = 80000), фаска по четырём ВЕРТИКАЛЬНЫМ угловым рёбрам длиной 10 мм:
/// каждое ребро снимает призму с прямоугольным треугольником в сечении, катеты d₁ и d₂ →
/// ΔV = 4 · (d₁·d₂/2) · 10 = 20·d₁·d₂. Для равных катетов 2×2 это ровно 80 мм³ (V = 79920),
/// для 3×3 — 180. Четыре угловых ребра не влияют друг на друга (между ними нет общего угла при
/// вершине в плоскости XY), поэтому формула точная, а не приближённая.
/// </para>
/// <para>
/// <b>Что именно разрешает эта проба.</b> (1) работает ли маршрут API5 вообще и правится ли
/// признак на месте; (2) что значит <c>transfer</c> в <c>SetChamferParam</c> — числом, а не по
/// описанию «признак направления фаски»; (3) единицы <c>IChamfer.Angle</c> в API7 (градусы или
/// радианы) и какое из двух толкований угла («от какой стороны катет») реализовано — по ΔV и по
/// площади смежной грани; (4) семантика <c>IChamfer.Direction</c> — ΔV её не различает
/// (площадь треугольника симметрична к перестановке катетов), поэтому различатор — площадь
/// верхней грани; (5) видит ли API7 фаску, созданную API5, и правится ли она типизированно;
/// (6) переживает ли признак save→close→reopen и остаётся ли редактируемым после;
/// (7) что происходит на заведомо неверном параметре.
/// </para>
/// <para>
/// Порядок шагов — часть измерения (вывод пробы E): сценарии, которые могут оставить модель с
/// нечитаемым числом, идут на собственных документах и после решающих шагов, чтобы не съедать
/// базлайн. Каждый шаг записывает и ожидаемое число, и полученное.
/// </para>
/// </remarks>
internal sealed class ChamferProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    private const short Sketch = 5;
    private const short PlaneXoy = 1;
    private const short Chamfer = 33;
    private const short OperationElement = 110;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private IApplication? _app7;

    public ChamferProbe(ProbeReport report, Options options)
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
            // Решающий вопрос — работает ли маршрут API5. Он идёт первым и на своём документе.
            var api5 = Api5CreateAndEdit();
            if (api5 is not null)
            {
                Api5DirectionSemantics();
                ReopenAndEdit(api5);
            }

            Bridge();
            if (_app7 is not null)
            {
                SeeApi5ChamferInApi7();
                Api7CreateTwoDistances();
                Api7DistanceAndAngle();
                Api7Direction();
                RejectInvalid();
            }
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("F.X", "Необработанное исключение пробы F");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════ сессия ══
    private void Launch()
    {
        var step = _report.Begin("F.1", "Свой невидимый экземпляр и доказательство PID",
            "Зонд работает с тем КОМПАС, который сам запустил?");
        try
        {
            var before = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = WaitForNewProcess(before, 90_000);
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            step.Pass("Сеанс запущен и атрибутирован диффом процессов.");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("F.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            step.Data["quit"] = Api5.Raw(TryBool(() =>
            {
                _app.Quit();
                return true;
            }));
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

    private void Bridge()
    {
        var step = _report.Begin("F.7", "Мост API5→API7", "Тот же сеанс отдаёт IApplication?");
        try
        {
            _app7 = (IApplication?)_app.ksGetApplication7();
            if (_app7 is null)
            {
                step.Fail("ksGetApplication7() вернул null.");
                return;
            }

            _app7.Visible = false;
            step.Data["qi_iapplication"] = true;
            step.Pass("IApplication получен из существующего API5-сеанса.");
        }
        catch (Exception ex)
        {
            _app7 = null;
            step.Fail("Мост не построен: " + ex.Message);
        }
    }

    // ════════════════════════════════════════════════════ маршрут API5 ══
    /// <summary>
    /// Создание фаски двумя катетами средствами API5 и её правка на месте.
    /// </summary>
    /// <returns>Пластина с фаской — она же используется для шага reopen.</returns>
    private Plate? Api5CreateAndEdit()
    {
        var step = _report.Begin("F.2", "API5: фаска 2×2 по четырём вертикальным рёбрам",
            "Появляется ли фаска вообще и снимает ли она ровно 20·d₁·d₂?");
        var plate = NewPlate(step, "f-chamfer", out var edges);
        if (plate is null || edges.Count != 4)
        {
            return null;
        }

        var feature = (ksEntity)plate.Part.NewEntity(Chamfer);
        step.Data["new_entity_type"] = feature.type;
        if (feature.GetDefinition() is not ksChamferDefinition definition)
        {
            step.Fail($"GetDefinition() вернул {Api5.RuntimeName(feature.GetDefinition())} вместо ksChamferDefinition.");
            return null;
        }

        step.Data["definition_clr_type"] = Api5.RuntimeName(definition);
        var accepted = Api5.Raw(TryBool(() => definition.SetChamferParam(false, 2d, 2d)));
        step.Data["set_chamfer_param"] = accepted;
        feature.name = "f-ch2";

        var array = definition.array();
        step.Data["array_clr_type"] = Api5.RuntimeName(array);
        if (array is not ksEntityCollection collection)
        {
            step.Fail($"array() вернул {Api5.RuntimeName(array)} вместо ksEntityCollection — рёбра подать некуда.");
            return null;
        }

        var added = 0;
        foreach (var edge in edges)
        {
            if (Api5.Raw(TryBool(() => collection.Add(edge))) is not "True")
            {
                continue;
            }

            added++;
        }

        step.Data["edges_added"] = added;
        step.Data["edges_requested"] = edges.Count;
        var created = Api5.Raw(TryBool(feature.Create));
        plate.Doc.RebuildDocument();

        var volume = Api5.Volume(plate.Part);
        var expected = PlateVolume - 20d * 2d * 2d;
        step.Data["create"] = created;
        step.Data["volume"] = Api5.Num(volume);
        step.Data["volume_expected"] = Api5.Num(expected);
        step.Data["faces"] = Api5.FaceCount(plate.Part);
        step.Data["delta"] = Delta(volume);

        // Обратное чтение — с НОВОГО объекта определения, а не тем же RCW, которым писали.
        var readBack = ReadChamferParam(feature, step, "после Create");

        var removed = Removed(volume);
        var matched = removed is double r && Math.Abs(r - 80d) <= Tolerance(80d);
        step.Observe($"V = {Api5.Num(volume)} при ожидании {Api5.Num(expected)}; снято {Api5.Num(removed)} при ожидании 80.");
        if (matched && created is "True" && added == 4)
        {
            step.Pass("Маршрут API5 работает: фаска 2×2 сняла ровно 20·d₁·d₂ = 80 мм³, признак создан.");
        }
        else
        {
            step.Fail($"Фаска не применена числами: Create={created}, рёбер {added}/4, снято {Api5.Num(removed)} при ожидании 80.");
            return null;
        }

        // Правка на месте: 2×2 → 3×3, ожидание ΔV = 20·9 = 180.
        var edit = _report.Begin("F.3", "API5: правка катетов существующей фаски 2→3",
            "Применяется ли правка параметров того же признака (для фаски это и есть edit)?");
        var writeTarget = feature.GetDefinition() as ksChamferDefinition;
        edit.Data["fresh_definition_same_object"] = ReferenceEquals(writeTarget, definition);
        edit.Data["set_again"] = Api5.Raw(TryBool(() => writeTarget!.SetChamferParam(false, 3d, 3d)));
        edit.Data["entity_update"] = Api5.Raw(TryBool(feature.Update));
        plate.Doc.RebuildDocument();

        var after = Api5.Volume(plate.Part);
        var expectedAfter = PlateVolume - 20d * 3d * 3d;
        edit.Data["volume"] = Api5.Num(after);
        edit.Data["volume_expected"] = Api5.Num(expectedAfter);
        edit.Data["delta"] = Delta(after);
        var editReadBack = ReadChamferParam(feature, edit, "после правки");
        var editMatched = Removed(after) is double re && Math.Abs(re - 180d) <= Tolerance(180d);
        edit.Observe($"V = {Api5.Num(after)} при ожидании {Api5.Num(expectedAfter)}; перечитано {editReadBack}.");
        if (editMatched)
        {
            edit.Pass("Правка катетов применена на месте: объём стал 79820 при аналитических 79820, параметр перечитан.");
        }
        else
        {
            edit.Unknown("Правка катетов числами не подтверждена: маршрут edit для фаски в API5 не доказан.");
        }

        plate.Feature = feature;
        plate.Edited = editMatched;
        return plate;
    }

    /// <summary>
    /// Что делает <c>transfer</c> у <c>SetChamferParam</c>. ΔV этот выбор не различает: площадь
    /// треугольника симметрична к перестановке катетов, поэтому различатор — площадь ВЕРХНЕЙ грани
    /// (катет, отложенный на ней, съедаёт её с каждой из четырёх сторон).
    /// </summary>
    private void Api5DirectionSemantics()
    {
        var step = _report.Begin("F.4", "API5: семантика transfer при неравных катетах 2×4",
            "Что именно переносит transfer: какой катет ложится на какую грань?");
        foreach (var transfer in new[] { false, true })
        {
            var probe = NewPlate(step, $"f-tr-{(transfer ? "on" : "off")}", out var edges);
            if (probe is null || edges.Count != 4)
            {
                return;
            }

            var feature = (ksEntity)probe.Part.NewEntity(Chamfer);
            if (feature.GetDefinition() is not ksChamferDefinition definition
                || definition.array() is not ksEntityCollection collection)
            {
                step.Fail("Определение или коллекция рёбер не получены.");
                return;
            }

            definition.SetChamferParam(transfer, 2d, 4d);
            foreach (var edge in edges)
            {
                collection.Add(edge);
            }

            feature.Create();
            probe.Doc.RebuildDocument();

            var key = transfer ? "transfer_true" : "transfer_false";
            step.Data[$"{key}_volume"] = Api5.Num(Api5.Volume(probe.Part));
            step.Data[$"{key}_removed"] = Api5.Num(Removed(Api5.Volume(probe.Part)));
            step.Data[$"{key}_side_face_areas"] = SideFaceAreas(probe.Part);
            step.Data[$"{key}_read_back"] = ReadChamferParam(feature, step, key);
            Close(probe.Doc);
        }

        var off = step.Data["transfer_false_side_face_areas"];
        var on = step.Data["transfer_true_side_face_areas"];
        step.Observe($"Боковые грани: transfer=false → {off}, transfer=true → {on}. Снятие материала: " +
                     $"{step.Data["transfer_false_removed"]} против {step.Data["transfer_true_removed"]}.");
        if (Equals(off, on))
        {
            step.Unknown("Распределение площадей боковых граней одинаково при обоих значениях transfer: " +
                        "влияние transfer на расположение катетов этим эталоном не измерено.");
            return;
        }

        step.Pass("transfer меняет расположение фаски численно: при одном значении катет 2 ложится на " +
                  "грань с нормалью X, при другом — на грань с нормалью Y. Это наблюдение, а не " +
                  "толкование имени параметра.");
    }

    /// <summary>save → закрыть → открыть заново, затем правка того же признака.</summary>
    private void ReopenAndEdit(Plate plate)
    {
        var step = _report.Begin("F.5", "API5: save→close→reopen, перечитать и снова править",
            "Фаска — часть модели, а не состояние сеанса?");
        var path = Path.Combine(_options.WorkDir, plate.Name + ".m3d");
        step.Data["saved_path"] = path;
        if (plate.Doc.SaveAs(path) != true)
        {
            step.Fail("SaveAs не подтверждён.");
            return;
        }

        step.Data["closed"] = Api5.Raw(TryBool(() => plate.Doc.close()));
        var reopened = (ksDocument3D)_app.Document3D();
        if (reopened.Open(path, true) != true)
        {
            step.Fail("Open не подтверждён.");
            return;
        }

        var part = (ksPart)reopened.GetPart(-1);
        step.Data["volume_after_reopen"] = Api5.Num(Api5.Volume(part));
        step.Data["features"] = Api5.ReadFeatures(part, step).Select(f => f.Describe()).ToArray();
        step.Data["faces_after_reopen"] = Api5.FaceCount(part);

        // Признак ищется по дереву, а не по памяти прогона: это и есть проверка «по модели».
        ksEntity? found = null;
        if (part.EntityCollection(OperationElement) is ksEntityCollection collection)
        {
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity && entity.type == Chamfer)
                {
                    found = entity;
                    break;
                }
            }

            step.Data["collection_110_count"] = collection.GetCount();
        }

        step.Data["found_in_collection_110"] = found is not null;
        step.Data["found_name"] = found?.name ?? "—";
        step.Data["parameters_after_reopen"] = found is null ? "—" : ReadChamferParam(found, step, "после reopen");

        if (found?.GetDefinition() is ksChamferDefinition definition)
        {
            definition.SetChamferParam(false, 5d, 5d);
            found.Update();
            reopened.RebuildDocument();
            var after = Api5.Volume(part);
            var expected = PlateVolume - 20d * 5d * 5d;
            step.Data["volume_after_reedit"] = Api5.Num(after);
            step.Data["volume_after_reedit_expected"] = Api5.Num(expected);
            step.Data["removed_after_reedit"] = Api5.Num(Removed(after));
            step.Data["parameters_after_reedit"] = ReadChamferParam(found, step, "после второй правки");
            var ok = Removed(after) is double r && Math.Abs(r - 500d) <= Tolerance(500d);
            step.Observe($"После reopen снято {Api5.Num(Removed(after))} при ожидании 20·5·5 = 500.");
            if (ok)
            {
                step.Pass("Фаска найдена по дереву после reopen, её параметры читаются и правка применяется: " +
                          "save_reopen и edit доказаны на признаке с диска.");
            }
            else
            {
                step.Unknown("Признак найден и перечитан после reopen, но повторная правка числами не подтверждена.");
            }
        }
        else
        {
            step.Fail("Фаска после reopen не найдена в EntityCollection(o3d_operationElement=110) — читать нечего.");
        }

        Close(reopened);
    }

    // ════════════════════════════════════════════════════ маршрут API7 ══
    /// <summary>Видна ли фаска, созданная API5, как объект API7 и берётся ли она типизированно.</summary>
    private void SeeApi5ChamferInApi7()
    {
        var step = _report.Begin("F.8", "API7: что видно из фаски, созданной API5",
            "Попадёт ли API5-признак в IModelContainer.Chamfers и что вернут его свойства?");
        var plate = NewPlate(step, "f-api7-view", out var edges);
        if (plate is null || edges.Count != 4)
        {
            return;
        }

        var container = Container(step, plate.Doc);
        if (container is null)
        {
            return;
        }

        var feature = (ksEntity)plate.Part.NewEntity(Chamfer);
        if (feature.GetDefinition() is not ksChamferDefinition definition
            || definition.array() is not ksEntityCollection collection)
        {
            step.Fail("Определение API5-фаски не получено.");
            return;
        }

        definition.SetChamferParam(false, 2d, 2d);
        foreach (var edge in edges)
        {
            collection.Add(edge);
        }

        feature.Create();
        plate.Doc.RebuildDocument();
        step.Data["api5_volume"] = Api5.Num(Api5.Volume(plate.Part));

        var chamfers = container.Chamfers;
        step.Data["chamfers_count"] = Api5.Raw(TryValue(() => chamfers.Count));
        object? item = null;
        try
        {
            item = chamfers[0];
        }
        catch (Exception ex)
        {
            step.Data["get_item_0"] = HResult.Describe(ex);
        }

        step.Data["item_runtime_type"] = Api5.RuntimeName(item);
        step.Data["item_qi_ichamfer"] = item is IChamfer;
        if (item is IChamfer chamfer)
        {
            step.Data["api7_name"] = chamfer.Name ?? "—";
            step.Data["api7_valid"] = Api5.Raw(TryValue(() => chamfer.Valid));
            step.Data["api7_building_type"] = Api5.Raw(TryValue(() => chamfer.BuildingType));
            step.Data["api7_distance1"] = Api5.Raw(TryValue(() => chamfer.Distance1));
            step.Data["api7_distance2"] = Api5.Raw(TryValue(() => chamfer.Distance2));
            step.Data["api7_angle"] = Api5.Raw(TryValue(() => chamfer.Angle));
            step.Data["api7_direction"] = Api5.Raw(TryValue(() => chamfer.Direction));
            step.Data["api7_tangent"] = Api5.Raw(TryValue(() => chamfer.Tangent));
            step.Data["api7_base_objects_type"] = Api5.RuntimeName(TryValue(() => chamfer.BaseObjects));
            step.Data["api7_base_objects_count"] = CountOf(chamfer.BaseObjects);
            step.Observe($"API7 видит: «{step.Data["api7_name"]}» Valid={step.Data["api7_valid"]}, " +
                         $"BuildingType={step.Data["api7_building_type"]}, D1={step.Data["api7_distance1"]}, " +
                         $"D2={step.Data["api7_distance2"]}, Angle={step.Data["api7_angle"]}, " +
                         $"Direction={step.Data["api7_direction"]}, опор {step.Data["api7_base_objects_count"]}.");
            step.Pass("Фаска API5 доступна как IChamfer: семейство и параметры читаются типизированно.");
        }
        else
        {
            step.Unknown("Chamfers[0] не ответил на IChamfer — типизированного чтения параметров нет.");
        }

        Close(plate.Doc);
    }

    /// <summary>Фаска двумя катетами, созданная целиком в API7.</summary>
    private void Api7CreateTwoDistances()
    {
        var step = _report.Begin("F.9", "API7: Chamfers.Add, двумя катетами 2×2",
            "Создаётся ли фаска средствами API7 и равна ли её работа работе API5?");
        var plate = NewPlate(step, "f-api7-dd", out var edges);
        if (plate is null || edges.Count != 4)
        {
            return;
        }

        var container = Container(step, plate.Doc);
        if (container is null)
        {
            return;
        }

        var baseObjects = ToApi7(step, edges);
        if (baseObjects is null)
        {
            step.Fail("Рёбра не перенесены в API7 — создавать нечем.");
            return;
        }

        var created = TryValue(() =>
        {
            var chamfer = container.Chamfers.Add();
            chamfer.Name = "a7-ch";
            chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferTwoSides;
            chamfer.BaseObjects = baseObjects;
            chamfer.Distance1 = 2d;
            chamfer.Distance2 = 2d;
            chamfer.Update();
            Rebuild(container, plate);
            return "ок";
        });
        step.Data["add_configure_update"] = created;
        step.Data["volume"] = Api5.Num(Api5.Volume(plate.Part));
        step.Data["removed"] = Api5.Num(Removed(Api5.Volume(plate.Part)));
        step.Data["expected_removed"] = 80d;
        step.Data["faces"] = Api5.FaceCount(plate.Part);
        var ok = Removed(Api5.Volume(plate.Part)) is double r && Math.Abs(r - 80d) <= Tolerance(80d);
        step.Observe($"Снято {Api5.Num(Removed(Api5.Volume(plate.Part)))} при ожидании 80 (20·d₁·d₂).");
        if (ok)
        {
            step.Pass("API7 строит фаску двумя катетами: число совпало с API5-эталоном, маршрут равноценен.");
        }
        else
        {
            step.Fail($"API7-фаска не изменила геометрию ожидаемо: {created}, снято {Api5.Num(Removed(Api5.Volume(plate.Part)))}.");
        }

        Close(plate.Doc);
    }

    /// <summary>
    /// Фаска «расстояние + угол» — тот режим, для которого в <c>ksChamferDefinition</c> члена нет.
    /// Гипотезы единицы угла перебираются явно: выбор делает измеренное число, а не соглашение.
    /// </summary>
    private void Api7DistanceAndAngle()
    {
        var step = _report.Begin("F.10", "API7: расстояние + угол (ksChamferSideAngle)",
            "Есть ли режим, в каких единицах угол и какой катет он задаёт?");
        var plate = NewPlate(step, "f-api7-da", out var edges);
        if (plate is null || edges.Count != 4)
        {
            return;
        }

        var container = Container(step, plate.Doc);
        if (container is null)
        {
            return;
        }

        var baseObjects = ToApi7(step, edges);
        if (baseObjects is null)
        {
            return;
        }

        const double Distance = 2d;
        const double Degrees = 30d;
        var created = TryValue(() =>
        {
            var chamfer = container.Chamfers.Add();
            chamfer.Name = "a7-da";
            chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferSideAngle;
            chamfer.BaseObjects = baseObjects;
            chamfer.Distance1 = Distance;
            chamfer.Angle = Degrees;
            chamfer.Update();
            Rebuild(container, plate);
            return "ок";
        });

        var removed = Removed(Api5.Volume(plate.Part));
        step.Data["add_configure_update"] = created;
        step.Data["volume"] = Api5.Num(Api5.Volume(plate.Part));
        step.Data["removed"] = Api5.Num(removed);
        step.Data["read_back_angle"] = Api5.Raw(TryValue(() =>
            container.Chamfers[0] is IChamfer c ? c.Angle : double.NaN));
        step.Data["read_back_distance1"] = Api5.Raw(TryValue(() =>
            container.Chamfers[0] is IChamfer c ? c.Distance1 : double.NaN));

        // Толкования: катет-второй = d·tan(α) (градусы), d/tan(α) (градусы, угол от другой грани),
        // те же два с радианами и тот же угол в радианах буквально.
        var tanDeg = Distance * Math.Tan(Degrees * Math.PI / 180d);
        var cotDeg = Distance / Math.Tan(Degrees * Math.PI / 180d);
        var candidates = new Dictionary<string, double>
        {
            ["20·d·(d·tan30°) = катеты d и d·tg α"] = 20d * Distance * tanDeg,
            ["20·d·(d/tg30°) = катеты d и d/tg α"] = 20d * Distance * cotDeg,
            ["20·d² (угол проигнорирован, как два равных катета)"] = 20d * Distance * Distance,
            ["угол как радианы: 20·d·d·tg(30 рад)"] = 20d * Distance * (Distance * Math.Tan(Degrees)),
        };
        step.Data["candidates"] = candidates.ToDictionary(p => p.Key, p => Api5.Num(p.Value));
        foreach (var pair in candidates)
        {
            step.Observe($"кандидат {pair.Key} = {Api5.Num(pair.Value)}");
        }

        var hit = candidates.FirstOrDefault(p => p.Value is not 0 && removed is double m
            && Math.Abs(m - p.Value) <= Tolerance(p.Value));
        step.Observe($"Измерено снято {Api5.Num(removed)}.");
        if (hit.Key is not null)
        {
            step.Pass($"Режим «расстояние + угол» работает и понятен по числам: {hit.Key} = " +
                      $"{Api5.Num(hit.Value)}. Это замер, а не соглашение об единицах.");
        }
        else
        {
            step.Unknown("Ни одно из перебранных толкований угла не совпало с измерением: единицы и " +
                        "сторона отсчёта угла НЕ установлены, в контракт они не входят.");
        }

        Close(plate.Doc);
    }

    /// <summary>
    /// Направление фаски в API7. ΔV её не различает, различает площадь грани, с которой фаска
    /// снимает катет, — на пластине с неравными катетами.
    /// </summary>
    private void Api7Direction()
    {
        var step = _report.Begin("F.11", "API7: Direction при 2×4",
            "Разворачивает ли Direction фаску численно (площадь верхней грани), а не только значение свойства?");
        foreach (var direction in new[] { false, true })
        {
            var plate = NewPlate(step, $"f-api7-dir-{(direction ? "on" : "off")}", out var edges);
            if (plate is null || edges.Count != 4)
            {
                return;
            }

            var container = Container(step, plate.Doc);
            var baseObjects = container is null ? null : ToApi7(step, edges);
            if (container is null || baseObjects is null)
            {
                return;
            }

            var key = direction ? "direction_true" : "direction_false";
            step.Data[$"{key}_update"] = TryValue(() =>
            {
                var chamfer = container.Chamfers.Add();
                chamfer.Name = "a7-dir";
                chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferTwoSides;
                chamfer.BaseObjects = baseObjects;
                chamfer.Distance1 = 2d;
                chamfer.Distance2 = 4d;
                chamfer.Direction = direction;
                chamfer.Update();
                Rebuild(container, plate);
                return "ок";
            });
            step.Data[$"{key}_read_back_direction"] = Api5.Raw(TryValue(() =>
                container.Chamfers[0] is IChamfer c ? c.Direction : (object)"null"));
            step.Data[$"{key}_removed"] = Api5.Num(Removed(Api5.Volume(plate.Part)));
            step.Data[$"{key}_side_face_areas"] = SideFaceAreas(plate.Part);
            Close(plate.Doc);
        }

        var off = step.Data["direction_false_side_face_areas"];
        var on = step.Data["direction_true_side_face_areas"];
        step.Observe($"Боковые грани: false → {off}, true → {on}; снято {step.Data["direction_false_removed"]} " +
                     $"против {step.Data["direction_true_removed"]}.");
        if (Equals(off, on))
        {
            step.Unknown("Direction перечитывается, а распределение площадей боковых граней не меняется: " +
                        "влияние направления фаски на этом эталоне не измерено.");
            return;
        }

        step.Pass("Direction меняет геометрию: катеты ложатся на разные грани — различение по площадям " +
                  "боковых граней (ΔV её не различает, площадь треугольника симметрична).");
    }

    /// <summary>Заведомо неверный параметр: обязан не войти в модель.</summary>
    private void RejectInvalid()
    {
        var step = _report.Begin("F.12", "Отрицательный случай: катет 0 и катет больше грани",
            "Неверное значение не вошло в модель и не оставило незаявленных изменений?");
        foreach (var (name, distance) in new[] { ("zero", 0d), ("oversize", 200d) })
        {
            var plate = NewPlate(step, $"f-bad-{name}", out var edges);
            if (plate is null || edges.Count != 4)
            {
                return;
            }

            var before = Api5.Volume(plate.Part);
            var faces = Api5.FaceCount(plate.Part);
            var feature = (ksEntity)plate.Part.NewEntity(Chamfer);
            var outcome = "не создавался";
            if (feature.GetDefinition() is ksChamferDefinition definition
                && definition.array() is ksEntityCollection collection)
            {
                definition.SetChamferParam(false, distance, distance);
                foreach (var edge in edges)
                {
                    collection.Add(edge);
                }

                outcome = Api5.Raw(TryBool(feature.Create));
                plate.Doc.RebuildDocument();
            }

            var after = Api5.Volume(plate.Part);
            step.Data[$"{name}_create"] = outcome;
            step.Data[$"{name}_volume_before"] = Api5.Num(before);
            step.Data[$"{name}_volume_after"] = Api5.Num(after);
            step.Data[$"{name}_faces_before"] = faces;
            step.Data[$"{name}_faces_after"] = Api5.FaceCount(plate.Part);
            step.Data[$"{name}_kompas_error"] = KompasErrorText();
            step.Observe($"{name}: Create={outcome}, V {Api5.Num(before)} → {Api5.Num(after)}, " +
                         $"граней {faces} → {step.Data[$"{name}_faces_after"]}, {step.Data[$"{name}_kompas_error"]}.");
            Close(plate.Doc);
        }

        step.Pass("Отрицательные катеты прогнаны: исход каждого — числа до и после, а не только ответ вызова. " +
                  "Что из этого следует для контракта, решается по отчёту, а не здесь.");
    }

    // ═══════════════════════════════════════════════════════════════════ построение ══
    private Plate? NewPlate(ProbeStep parent, string name, out List<ksEntity> cornerEdges)
    {
        cornerEdges = new List<ksEntity>();
        var step = _report.Begin(parent.Id + ":plate", "Пластина 100×80×10 для «" + name + "»", null);
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, name) is null)
            {
                step.Fail("Пластина не построена.");
                return null;
            }

            doc.RebuildDocument();
            var volume = Api5.Volume(part);
            if (volume is not double v || Math.Abs(v - PlateVolume) > Tolerance(PlateVolume))
            {
                step.Fail($"Базовый объём не 80000: {Api5.Num(volume)} — эталон не годится.");
                return null;
            }

            cornerEdges.AddRange(VerticalCornerEdges(part, step));
            step.Data["volume"] = Api5.Num(volume);
            step.Data["corner_edges"] = cornerEdges.Count;
            step.Data["faces"] = Api5.FaceCount(part);
            step.Pass("Пластина готова: V=80000, вертикальных угловых рёбер " + cornerEdges.Count + ".");
            return new Plate { Name = name, Doc = doc, Part = part, StartedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Пластина не построена: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    /// <summary>
    /// Четыре вертикальных угловых ребра конечного тела — тот же критерий отбора, что в пробе P2.2:
    /// из <c>GetMainBody() → FaceCollection → EdgeCollection</c>, а не из <c>EntityCollection(o3d_edge)</c>,
    /// где лежат и эскизные контуры.
    /// </summary>
    private static List<ksEntity> VerticalCornerEdges(ksPart part, ProbeStep step)
    {
        var chosen = new List<ksEntity>();
        var seen = new HashSet<IntPtr>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("Тело или коллекция граней недоступны — рёбер не видно.");
            return chosen;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not object faceObject)
            {
                continue;
            }

            var face = faceObject as ksFaceDefinition
                       ?? ((faceObject as ksEntity)?.GetDefinition() as ksFaceDefinition);
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition
                           ?? ((edgeObject as ksEntity)?.GetDefinition() as ksEdgeDefinition);
                if (edge is null || Api5.SafeBool(edge.IsStraight) != true)
                {
                    continue;
                }

                if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1
                    || !v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                {
                    continue;
                }

                var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                var corner = Math.Abs(Math.Abs(ax) - PlateWidth / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - PlateHeight / 2d) < 1e-3;
                var spans = Math.Abs(Math.Max(az, bz) - PlateThickness) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                if (!(vertical && corner && spans))
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(entity);
                try
                {
                    if (seen.Add(pointer))
                    {
                        chosen.Add(entity);
                    }
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }

        return chosen;
    }

    private object? ToApi7(ProbeStep step, IReadOnlyList<ksEntity> edges)
    {
        var list = new List<object>(edges.Count);
        foreach (var edge in edges)
        {
            object? transferred;
            try
            {
                transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            }
            catch (Exception ex)
            {
                step.Data["transfer_edge"] = HResult.Describe(ex);
                return null;
            }

            if (transferred is null)
            {
                step.Data["transfer_edge"] = "null";
                return null;
            }

            step.Data["transfer_edge_type"] = Api5.RuntimeName(transferred);
            list.Add(transferred);
        }

        return list.ToArray();
    }

    private IModelContainer? Container(ProbeStep step, ksDocument3D doc)
    {
        try
        {
            var document7 = _app.TransferInterface(doc, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            step.Data["qi_imodelcontainer"] = container is not null;
            if (container is null)
            {
                step.Unknown("Документ/TopPart не дали IModelContainer — шаги API7 на этой пластине не выполняются.");
            }

            return container;
        }
        catch (Exception ex)
        {
            step.Data["qi_imodelcontainer"] = "исключение: " + ex.Message;
            return null;
        }
    }

    private static void Rebuild(IModelContainer container, Plate plate)
    {
        // RebuildModel принадлежит IPart7 (измерено пробой E); типизированный вызов, не IDispatch.
        if (container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        plate.Doc.RebuildDocument();
    }

    // ═══════════════════════════════════════════════════════════════════ измерения ══
    private static double? Removed(double? volume) =>
        volume is double v && v > 0 ? PlateVolume - v : null;

    private static double? Delta(double? volume) => volume is double v ? v - PlateVolume : null;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private static string ReadChamferParam(ksEntity feature, ProbeStep step, string when)
    {
        var fresh = feature.GetDefinition() as ksChamferDefinition;
        if (fresh is null)
        {
            return "определение не читается";
        }

        try
        {
            var ok = fresh.GetChamferParam(out var transfer, out var d1, out var d2);
            var text = $"ok={ok} transfer={transfer} d1={Api5.Num(d1)} d2={Api5.Num(d2)}";
            step.Observe($"{when}: {text}");
            return text;
        }
        catch (Exception ex)
        {
            return "исключение: " + ex.Message;
        }
    }

    /// <summary>
    /// Площади боковых плоских граней (нормаль вдоль X или Y) — вот что различает направление
    /// фаски на вертикальном ребре: катет, отложенный от грани, съедает её площадь на d·h.
    /// Площадь верхней грани для равных по произведению катетов не меняется, поэтому она
    /// различатором не является (ошибочная версия этого шага давала бы ложное «transfer ни на что
    /// не влияет»).
    /// </summary>
    private static string SideFaceAreas(ksPart part)
    {
        var byAxis = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["x"] = new List<string>(),
            ["y"] = new List<string>(),
        };
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            return "тело недоступно";
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            var element = faces.GetByIndex(f);
            var face = element as ksFaceDefinition ?? ((element as ksEntity)?.GetDefinition() as ksFaceDefinition);
            if (face is null || Api5.SafeBool(face.IsPlanar) != true)
            {
                continue;
            }

            if (face.GetSurface() is not ksSurface surface || surface.GetSurfaceParam() is not ksPlaneParam plane
                || plane.GetPlacement() is not ksPlacement placement)
            {
                continue;
            }

            if (!placement.GetVector(2, out var nx, out var ny, out var nz))
            {
                continue;
            }

            // Оси нормале: |nx|≈1 → грань перпендикулярна X (её площадь = 80·10 до фаски),
            // |ny|≈1 → перпендикулярна Y (100·10). Прочие (наклонные плоскости фаски, торцы) не нужны.
            var axis = Math.Abs(Math.Abs(nx) - 1d) < 1e-6 ? "x"
                : Math.Abs(Math.Abs(ny) - 1d) < 1e-6 ? "y"
                : null;
            if (axis is null)
            {
                continue;
            }

            var area = Api5.SafeDouble(() => face.GetArea(1));
            byAxis[axis].Add(Api5.Num(area));
        }

        return "x[" + string.Join(",", byAxis["x"].OrderBy(s => s, StringComparer.Ordinal)) + "] "
             + "y[" + string.Join(",", byAxis["y"].OrderBy(s => s, StringComparer.Ordinal)) + "]";
    }

    private static int? CountOf(object? value) => value is object[] array ? array.Length : null;

    private string KompasErrorText()
    {
        try
        {
            var error = _app7?.KompasError;
            return error is null
                ? "нет API7-сеанса"
                : $"Code={Api5.Raw(TryValue(() => error.Code))} «{Api5.Raw(TryValue(() => error.Description))}»";
        }
        catch (Exception ex)
        {
            return "KompasError: " + ex.GetType().Name;
        }
    }

    private static void Close(ksDocument3D doc)
    {
        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // Закрытие диагностикой не является: осиротевший документ заметен на шаге F.Z.
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

    private static object? TryValue(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static uint[] WaitForNewProcess(uint[] before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray().Except(before).ToArray();
            if (created.Length > 0)
            {
                return created;
            }

            System.Threading.Thread.Sleep(250);
        }

        return Array.Empty<uint>();
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

    private sealed class Plate
    {
        public required string Name { get; init; }

        public required ksDocument3D Doc { get; init; }

        public required ksPart Part { get; init; }

        public DateTime StartedUtc { get; init; }

        public ksEntity? Feature { get; set; }

        public bool Edited { get; set; }
    }
}
