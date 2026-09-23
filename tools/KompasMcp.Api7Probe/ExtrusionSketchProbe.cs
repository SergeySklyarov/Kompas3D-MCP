using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба E — можно ли сменить ОПОРНЫЙ ЭСКИЗ существующего признака выдавливания через API7.
/// </summary>
/// <remarks>
/// Повод ровно один и он измерен: в API5 `SetSketch` у определения выдавливания возвращает true,
/// но `GetSketch()` перечитывает прежний эскиз и объём не меняется (строка L11 приёмки
/// 12.09.2026, `docs/acceptance/INDEX.md`). У сквозного вырезания глубины как параметра нет
/// (P2.1: solver игнорирует число), поэтому без смены опоры режим `SM-02.cut_extrusion.through`
/// не имеет измеримой правки вообще.
///
/// Статическая часть вопроса уже решена рефлексией по обёртке (см. ADR-004 §6 про неполноту):
/// `IExtrusion.Sketch` и `ICutExtrusion.Sketch` объявлены и читаемыми, и записываемыми
/// (`set_Sketch(Sketch)`), у `IExtrusion1` есть `Profile`/`Profiles` тоже с сеттерами, и у всех
/// — `Update()`. Но «свойство объявлено» не равно «модель меняется»: проверяется исключительно
/// числом объёма. Ожидания аналитические: пластина 100×80×10 = 80000 мм³, окно 40×20 насквозь
/// снимает 8000 (V = 72000), круг Ø20 насквозь снимает π·10²·10 = 3141.5926 (V = 76858.4073).
///
/// Документ перед правкой сохраняется, закрывается и открывается заново: правка опоры на
/// «свеженаписанном» признаке ничего не стоила бы как доказательство, что маршрут работает с
/// моделью, а не с только что созданными объектами.
/// </remarks>
internal sealed class ExtrusionSketchProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double WindowWidth = 40d;
    private const double WindowHeight = 20d;
    private const double HoleRadius = 10d;

    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;
    private const double WindowVolume = WindowWidth * WindowHeight * PlateThickness;
    private const double CutVolume = PlateVolume - WindowVolume;
    private const double CircleVolume = PlateVolume - Math.PI * HoleRadius * HoleRadius * PlateThickness;

    private const short Sketch = 5;
    private const short PlaneXoy = 1;
    private const short BaseExtrusion = 24;
    private const short CutExtrusion = 26;
    private const short EndConditionThrough = 1;
    private const short OperationElement = 110;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private IModelContainer? _container7;

    /// <summary>Перенесённый документ: нужен для RebuildModel() — у API7 своё перестроение модели.</summary>
    private IKompasDocument3D? _document7;
    private string _savedPath = string.Empty;
    private double _volumeBeforeEdit;

    public ExtrusionSketchProbe(ProbeReport report, Options options)
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
            BuildPlateWithWindow();
            if (_part is null)
            {
                return;
            }

            Bridge();
            EnumerateFeatures7();
            var circle = DrawCircleSketch();
            if (circle is null)
            {
                return;
            }

            // Порядок важен: маршруты A/B в предыдущем прогоне оставляли модель с нечитаемым
            // объёмом (E.7: V после записи Profile = NaN), и шедший после них шаг терял базлайн.
            // Решающий вопрос — типизированный вызов, поэтому он идёт первым.
            RetargetRouteC(circle);
            RetargetRouteA(circle);
            RetargetRouteB(circle);
            Persistence();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("E.X", "Необработанное исключение пробы E");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════ сессия ══
    private void Launch()
    {
        var step = _report.Begin("E.1", "Свой невидимый экземпляр", "Зонд работает со своим КОМПАС?");
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
        var step = _report.Begin("E.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_doc is not null)
            {
                step.Data["doc_close"] = Api5.Raw(TryBool(() => _doc.close()));
            }

            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            step.Data["quit"] = Api5.Raw(TryBool(() => { _app.Quit(); return true; }));
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

    // ══════════════════════════════════════════════ построение и reopen ══
    private void BuildPlateWithWindow()
    {
        var step = _report.Begin("E.2", "Пластина + сквозное окно, затем save→close→reopen",
            "Признак, у которого меняем опору, пришёл с диска, а не только что создан?");
        try
        {
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step, "e") is null)
            {
                step.Fail("Пластина не построена.");
                return;
            }

            if (_part.GetDefaultEntity(PlaneXoy) is not ksEntity plane
                || _part.NewEntity(Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                step.Fail("Эскиз окна не создан.");
                return;
            }

            sketch.name = "e-window";
            definition.SetPlane(plane);
            sketch.Create();
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit окна не дал редактор.");
                return;
            }

            editor.ksLineSeg(-WindowWidth / 2, -WindowHeight / 2, WindowWidth / 2, -WindowHeight / 2, 1);
            editor.ksLineSeg(WindowWidth / 2, -WindowHeight / 2, WindowWidth / 2, WindowHeight / 2, 1);
            editor.ksLineSeg(WindowWidth / 2, WindowHeight / 2, -WindowWidth / 2, WindowHeight / 2, 1);
            editor.ksLineSeg(-WindowWidth / 2, WindowHeight / 2, -WindowWidth / 2, -WindowHeight / 2, 1);
            definition.EndEdit();
            _doc.RebuildDocument();

            if (_part.NewEntity(CutExtrusion) is not ksEntity cut
                || cut.GetDefinition() is not ksCutExtrusionDefinition cutDefinition)
            {
                step.Fail("Определение вырезания не получено.");
                return;
            }

            cut.name = "e-cut-window";
            cutDefinition.SetSketch(sketch);
            cutDefinition.directionType = 2;
            cutDefinition.SetSideParam(true, EndConditionThrough, 0d, 0d, false);
            cutDefinition.SetSideParam(false, EndConditionThrough, 0d, 0d, false);
            step.Data["cut_create"] = cut.Create();
            _doc.RebuildDocument();

            _savedPath = Path.Combine(_options.WorkDir, "extrusion-sketch.m3d");
            step.Data["saved_path"] = _savedPath;
            if (_doc.SaveAs(_savedPath) != true)
            {
                step.Fail("SaveAs не подтверждён.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(_savedPath, true) != true)
            {
                step.Fail("Open не подтверждён.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            _volumeBeforeEdit = Api5.Volume(_part) ?? double.NaN;
            step.Data["volume_after_reopen"] = Api5.Num(_volumeBeforeEdit);
            step.Data["volume_expected"] = CutVolume;
            var ok = Math.Abs(_volumeBeforeEdit - CutVolume) < 1d;
            step.Observe($"V после reopen = {Api5.Num(_volumeBeforeEdit)}, ожидание {CutVolume}.");
            if (ok)
            {
                step.Pass("Окно на месте после reopen: дальнейшая правка идёт по модели с диска.");
            }
            else
            {
                step.Fail("Число после reopen не совпало — правку проверять не на чем.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Построение не удалось: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════ мост и обход ══
    private void Bridge()
    {
        var step = _report.Begin("E.3", "Мост API5→API7", "Часть документа доступна как IModelContainer?");
        try
        {
            var app7 = (IApplication?)_app.ksGetApplication7();
            if (app7 is null)
            {
                step.Fail("ksGetApplication7() вернул null.");
                return;
            }

            app7.Visible = false;
            var document7 = _app.TransferInterface(_doc, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                            as IKompasDocument3D;
            if (document7 is null)
            {
                step.Fail("Перенос документа не дал IKompasDocument3D.");
                return;
            }

            _document7 = document7;
            _container7 = document7.TopPart as IModelContainer;
            if (_container7 is null)
            {
                step.Fail("TopPart не является IModelContainer.");
                return;
            }

            step.Pass("Мост есть: IApplication + IKompasDocument3D + IModelContainer того же сеанса.");
        }
        catch (Exception ex)
        {
            step.Fail("Мост не построен: " + ex.Message);
        }
    }

    private void EnumerateFeatures7()
    {
        var step = _report.Begin("E.4", "Чем в API7 представлен признак выдавливания из API5",
            "Есть ли у него объект, которому вообще можно назначить другой эскиз?");
        if (_container7 is null)
        {
            step.Unknown("Моста нет — шаг не выполнялся.");
            return;
        }

        try
        {
            var observations = new List<string>();
            var extrusions = _container7.Extrusions;
            observations.Add($"Extrusions.Count={extrusions.Count}");
            for (var i = 0; i < extrusions.Count; i++)
            {
                var item = At(extrusions, i);
                observations.Add($"Extrusions[{i}] тип={Api5.RuntimeName(item)} " +
                                 $"имя={Str(item, "Name") ?? "—"} " +
                                 $"valid={Obj(item, "Valid") ?? "—"} " +
                                 $"sketch={TypeName(Obj(item, "Sketch"))}");
            }

            var cuts = _container7.Cuts;
            observations.Add($"Cuts.Count={cuts.Count}");
            for (var i = 0; i < cuts.Count; i++)
            {
                var item = At(cuts, i);
                observations.Add($"Cuts[{i}] тип={Api5.RuntimeName(item)} " +
                                 $"имя={Str(item, "Name") ?? "—"}");
            }

            step.Data["api7_feature_tree"] = observations;
            foreach (var line in observations)
            {
                step.Observe(line);
            }

            step.Pass("Обход выполнен: по этим строкам видно, в какую коллекцию лёг признак и " +
                      "перечитывается ли его эскиз из API7.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Обход упал: " + ex.Message);
        }
    }

    private Sketch? DrawCircleSketch()
    {
        var step = _report.Begin("E.5", "Второй эскиз: круг Ø20, перенос 5→7",
            "Можно ли получить объект API7, пригодный для свойства Sketch?");
        try
        {
            if (_part.GetDefaultEntity(PlaneXoy) is not ksEntity plane
                || _part.NewEntity(Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                step.Fail("Эскиз круга не создан.");
                return null;
            }

            sketch.name = "e-hole";
            definition.SetPlane(plane);
            sketch.Create();
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("Редактор круга не получен.");
                return null;
            }

            editor.ksCircle(0d, 0d, HoleRadius, 1);
            definition.EndEdit();
            _doc.RebuildDocument();

            var transferred = _app.TransferInterface(sketch, (int)ksAPITypeEnum.ksAPI7Dual, 0) as Sketch;
            step.Data["api7_sketch_type"] = Api5.RuntimeName(transferred);
            if (transferred is null)
            {
                step.Fail("Перенос эскиза 5→7 не дал объект типа Sketch — присваивать свойству нечего.");
                return null;
            }

            step.Pass($"Эскиз перенесён: {Api5.RuntimeName(transferred)} (именно этот тип ждёт set_Sketch).");
            return transferred;
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Эскиз круга не готов: " + ex.Message);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════ маршруты правки ══
    /// <summary>Маршрут A: `IExtrusion.Sketch` (свойство объявлено с сеттером).</summary>
    private void RetargetRouteA(Sketch circle)
    {
        var step = _report.Begin("E.6", "Маршрут A: IExtrusion.Sketch = круг + Update()",
            "Назначается ли опора так, чтобы поменялась геометрия?");
        if (!TryRoute(step, "Sketch", circle))
        {
            step.Observe("Маршрут A не подтверждён — результат решения принимает маршрут B.");
        }
    }

    /// <summary>Маршрут B: `IExtrusion1.Profile` (свойство объявлено с сеттером на IModelObject).</summary>
    private void RetargetRouteB(Sketch circle)
    {
        var step = _report.Begin("E.7", "Маршрут B: IExtrusion1.Profile = круг + Update()",
            "Если Sketch не применяется, может применяться Profile?");
        object? profile = FindCutElement7(step);
        if (profile is null)
        {
            step.Observe("Объекта, которому можно присвоить Profile, не найдено.");
            return;
        }

        TryRoute(step, "Profile", circle);
    }

    /// <summary>
    /// Общий корпус маршрутов A и B: найти признак вырезания в API7, присвоить ему свойство через
    /// позднее связывание, вызвать Update(), перестроить и сверить объём с аналитикой. Решение —
    /// только за числом: «свойство объявлено» и «вызов не бросил» доказательствами не считаются.
    /// </summary>
    private bool TryRoute(ProbeStep step, string property, Sketch circle)
    {
        try
        {
            var target = FindCutElement7(step);
            if (target is null)
            {
                step.Fail("Признак вырезания в API7 не найден.");
                return false;
            }

            var before = Api5.Volume(_part) ?? double.NaN;
            var sketchBefore = $"{TypeName(Obj(target, property))} имя={Str(Obj(target, property), "Name") ?? "—"}";
            _volumeBeforeEdit = before;

            var assigned = Api5.Raw(TryValue(() =>
            {
                Late.Set(target, property, circle);
                Late.Call(target, "Update");
                return "ок";
            }));
            // Разделение «не записалось» / «не перестроилось»: перечитывается ТО ЖЕ свойство,
            // которое пишется. Прежний вариант всегда читал Sketch и для Profile давал не ту
            // величину (замечено в прогоне E.7).
            step.Data[$"{property}_stored_immediately"] = Str(Obj(target, property), "Name") ?? "—";
            _doc.RebuildDocument();

            var after = Api5.Volume(_part) ?? double.NaN;
            object? sketchAfterObject = Obj(target, property);
            var nameAfter = Str(sketchAfterObject, "Name");
            var sketchAfter = $"{TypeName(sketchAfterObject)} имя={nameAfter ?? "—"}";
            step.Data[$"{property}_assigned"] = assigned;
            step.Data["volume_before"] = Api5.Num(before);
            step.Data["volume_after"] = Api5.Num(after);
            step.Data["volume_expected"] = CircleVolume;
            step.Data["sketch_before"] = sketchBefore;
            step.Data["sketch_after"] = sketchAfter;
            step.Observe($"V {Api5.Num(before)} → {Api5.Num(after)} (ожидание {CircleVolume:0.####}); " +
                         $"{property} перечитан как {sketchAfter}; присвоение: {assigned}.");

            var geometryChanged = Math.Abs(after - CircleVolume) < 1d;
            var readBack = string.Equals(nameAfter, "e-hole", StringComparison.Ordinal)
                           || (sketchAfterObject is not null && nameAfter is not null
                               && nameAfter != "e-window");
            if (geometryChanged && readBack)
            {
                _routeWorks = true;
                _workingProperty = property;
                step.Pass($"Маршрут {property} применён: объём стал {Api5.Num(after)} при аналитических " +
                          $"{CircleVolume:0.####} и опора перечитывается новая — модель изменилась, а " +
                          "не только свойство.");
                return true;
            }

            step.Unknown($"Маршрут {property} не подтверждён числом (V={Api5.Num(after)} при " +
                         $"ожидании {CircleVolume:0.####}, перечитано «{nameAfter ?? "—"}»).");
            return false;
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail($"Маршрут {property} упал: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Маршрут C: типизированное присваивание вместо позднего связывания.
    /// </summary>
    /// <remarks>
    /// Первая версия этого шага (PROPERTYPUTREF + `RebuildModel` через IDispatch) убила процесс
    /// кодом 0xC0000409 (fail-fast) — см. `docs/STATUS.md`. Это само по себе наблюдение: поздний
    /// вызов этих членов в v24 небезопасен, и повторять его в пробе, где за один прогон теряется
    /// и отчёт, и экземпляр КОМПАС, незачем.
    ///
    /// Вопрос при этом оставался тот же, и у него есть более чистый дискриминант. Маршруты A и B
    /// писали свойство через IDispatch (`Late.Set`), то есть «молчаливый отказ» мог быть отказом
    /// диспатч-механизма, а не API7. Типизированное обращение идёт напрямую по vtable интерфейса:
    /// если и оно не применит геометрию, объяснение «маршалинг» отпадает и остаётся свойство
    /// или перестроение модели.
    /// </remarks>
    private void RetargetRouteC(Sketch circle)
    {
        var step = _report.Begin("E.9", "Маршрут C: типизированное IExtrusion.Sketch (vtable, не IDispatch)",
            "Отказ A/B принадлежал механизму IDispatch или свойство действительно не применяется?");
        if (_container7 is null)
        {
            step.Unknown("Моста API7 нет.");
            return;
        }

        try
        {
            var target = FindCutElement7(step);
            if (target is null)
            {
                step.Fail("Признак вырезания в API7 не найден.");
                return;
            }

            step.Data["target_runtime_type"] = Api5.RuntimeName(target);
            var asExtrusion = target as IExtrusion;
            var asExtrusion1 = target as IExtrusion1;
            step.Data["qi_IExtrusion"] = asExtrusion is not null;
            step.Data["qi_IExtrusion1"] = asExtrusion1 is not null;
            if (asExtrusion is null)
            {
                step.Fail("Объект не поддерживает IExtrusion — типизированного доступа к Sketch нет.");
                return;
            }

            var before = Api5.Volume(_part) ?? double.NaN;
            step.Data["volume_before"] = Api5.Num(before);
            step.Data["sketch_before_typed"] = asExtrusion.Sketch is null
                ? "null" : Str(asExtrusion.Sketch, "Name") ?? "—";

            asExtrusion.Sketch = circle;
            var storedName = asExtrusion.Sketch is null ? "null" : Str(asExtrusion.Sketch, "Name");
            step.Data["sketch_immediately_after_typed_set"] = storedName ?? "—";
            step.Observe($"Сразу после типизированной записи Sketch перечитывается как " +
                         $"«{storedName ?? "—"}». " +
                         (storedName == "e-hole"
                             ? "Значение свойство принимает: отказ A/B принадлежал позднему связыванию."
                             : "Свойство не принимает значение и по vtable: дело не в IDispatch."));

            // Развод по шагам, потому что исходы разные: сразу после записи значение ЕСТЬ
            // («e-hole»), после Update()+RebuildDocument() — нет (перечитывается «e-window»).
            // Проверяется единственная несыгранная возможность: перестроение средствами API7.
            // Раньше тот же вызов шёл через IDispatch и убивал процесс (0xC0000409); здесь он
            // типизированный, то есть риск того же класса не воспроизводится.
            // RebuildModel принадлежит IPart7 (проверено отражением: у IKompasDocument3D такого
            // члена нет), поэтому берётся перенесённая часть, а не документ.
            var part7 = _document7 is null ? null : _document7.TopPart as IPart7;
            step.Data["qi_IPart7"] = part7 is not null;
            var rebuild7 = Api5.Raw(TryValue(() =>
            {
                part7!.RebuildModel(true);
                return "ок";
            }));
            step.Data["rebuild_model_api7"] = rebuild7;
            var nameAfterRebuild7 = Str(Obj(target, "Sketch"), "Name");
            var volumeAfterRebuild7 = Api5.Volume(_part) ?? double.NaN;
            step.Data["sketch_after_rebuild_model"] = nameAfterRebuild7 ?? "—";
            step.Data["volume_after_rebuild_model"] = Api5.Num(volumeAfterRebuild7);
            step.Observe($"После RebuildModel(): опора «{nameAfterRebuild7 ?? "—"}», " +
                         $"V={Api5.Num(volumeAfterRebuild7)}, вызов={rebuild7}.");

            var update = Api5.Raw(TryValue(() =>
            {
                asExtrusion.Update();
                return "ок";
            }));
            _doc.RebuildDocument();
            step.Data["update"] = update;
            step.Data["sketch_after_update_and_api5_rebuild"] = Str(Obj(target, "Sketch"), "Name") ?? "—";

            var after = Api5.Volume(_part) ?? double.NaN;
            step.Data["volume_after"] = Api5.Num(after);
            step.Data["volume_expected"] = CircleVolume;
            step.Data["sketch_after_rebuild"] = asExtrusion.Sketch is null
                ? "null" : Str(asExtrusion.Sketch, "Name") ?? "—";
            step.Observe($"V {Api5.Num(before)} → {Api5.Num(after)} (ожидание {CircleVolume:0.####}).");

            if (Math.Abs(after - CircleVolume) < 1d)
            {
                _routeWorks = true;
                _workingProperty = "IExtrusion.Sketch (типизированно)";
                _volumeBeforeEdit = after;
                step.Pass($"Маршрут C применён: V={Api5.Num(after)} при аналитических " +
                          $"{CircleVolume:0.####}. Смена опоры возможна типизированным вызовом — " +
                          "это и есть кандидат для UpdateFeature.");
            }
            else
            {
                step.Unknown("Типизированная запись тоже не изменила геометрию: " +
                            "объяснение через IDispatch отпадает, остаётся неприменение опоры " +
                            "или перестроение, которого API5 RebuildDocument не делает.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail($"Маршрут C упал: {ex.Message}");
        }
    }

    private bool _routeWorks;
    private string? _workingProperty;

    /// <summary>Ищет в Extrusions признак с именем вырезания окна (API5-имя сохраняется).</summary>
    private object? FindCutElement7(ProbeStep step)
    {
        if (_container7 is null)
        {
            return null;
        }

        var seen = new List<string>();
        var extrusions = _container7.Extrusions;
        for (var i = 0; i < extrusions.Count; i++)
        {
            object? item = null;
            try
            {
                item = At(extrusions, i);
            }
            catch (Exception ex)
            {
                seen.Add($"Extrusions[{i}]: {ex.GetType().Name}");
                continue;
            }

            if (item is null)
            {
                continue;
            }

            var name = Str(item, "Name");
            seen.Add($"Extrusions[{i}] «{name ?? "—"}» {Api5.RuntimeName(item)}");
            if (name is not null && name.Contains("cut", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        step.Observe("Походами по Extrusions: " + string.Join(" | ", seen));
        return null;
    }

    // ══════════════════════════════════════════════════════ переживание правки ══
    private void Persistence()
    {
        var step = _report.Begin("E.8", "Переживает ли правка save→close→reopen и что видит API5",
            "Если опора сменилась, она сменилась в модели, а не в представлении сеанса?");
        if (!_routeWorks)
        {
            step.Observe("Ни один маршрут не изменил геометрию — проверять сохранение нечего.");
            step.Unknown("Правка не применена, шаг переживания не выполнялся.");
            return;
        }

        try
        {
            var volumeBeforeSave = Api5.Volume(_part) ?? double.NaN;
            if (_doc.Save() != true)
            {
                step.Fail("Save не подтверждён.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(_savedPath, true) != true)
            {
                step.Fail("Open не подтверждён.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            var volumeAfterReopen = Api5.Volume(_part) ?? double.NaN;
            step.Data["volume_saved"] = Api5.Num(volumeBeforeSave);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);
            step.Observe($"V до сохранения {Api5.Num(volumeBeforeSave)}, после reopen {Api5.Num(volumeAfterReopen)}.");

            // Что видит API5: GetSketch того же признака должен перечитать круг.
            var api5Read = ReadApi5SketchOfCut(step);
            var persisted = Math.Abs(volumeAfterReopen - _volumeBeforeEdit) < 1d;
            if (persisted && api5Read == "e-hole")
            {
                step.Pass("Правка опоры пережила сохранение и повторное открытие, а API5 видит новым " +
                          "эскиз тот же признак: маршрут годится как основной для действия edit.");
            }
            else
            {
                step.Unknown($"Что-то не сошлось: объём {Api5.Num(volumeAfterReopen)} при " +
                             $"{Api5.Num(_volumeBeforeEdit)}, API5 перечитал «{api5Read ?? "—"}». " +
                             "Годность маршрута не подтверждена.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Переживание правки не измерено: " + ex.Message);
        }
    }

    private string? ReadApi5SketchOfCut(ProbeStep step)
    {
        if (_part.EntityCollection(OperationElement) is not ksEntityCollection collection)
        {
            step.Observe("EntityCollection(110) недоступна.");
            return null;
        }

        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (collection.GetByIndex(i) is not ksEntity entity)
            {
                continue;
            }

            var name = entity.name ?? string.Empty;
            if (!name.Contains("cut", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var read = entity.GetDefinition() switch
            {
                ksCutExtrusionDefinition c => (c.GetSketch() as ksEntity)?.name,
                ksBaseExtrusionDefinition b => (b.GetSketch() as ksEntity)?.name,
                _ => null,
            };

            step.Observe($"API5 видит признак «{name}» с опорой «{read ?? "—"}». " +
                         $"Рабочий маршрут в API7: {(_workingProperty is null ? "не найден" : _workingProperty)}.");
            return read;
        }

        step.Observe("Признака вырезания с «cut» в имени в дереве API5 не найдено.");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════ хелперы ══
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
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray()
                .Except(before).ToArray();
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

    // ─── читалки позднего связывания ────────────────────────────────────────────────
    // Позднее связывание спрашивает у самого COM-объекта: типизированный доступ к
    // параметрическим свойствам (Item[i]) и к членам более новых версий интерфейсов в этой
    // обёртке не объявлен, а вопрос «есть ли член» обязан решаться на рабочем объекте.
    private static object? Obj(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            return Late.Get(target, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Str(object? target, string name) => Obj(target, name) as string;

    private static string TypeName(object? target) =>
        target is null ? "null" : target.GetType().Name;

    /// <summary>Элемент коллекции API7: доступ к параметрическому свойству, имя аксессора
    /// в разных версиях обёртки различается, поэтому пробуются оба.</summary>
    private static object? At(object collection, int index)
    {
        foreach (var accessor in new[] { "get_Item", "Item" })
        {
            try
            {
                var value = Late.Call(collection, accessor, index);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // Отказ одного аксессора ничего не утверждает о наличии члена.
            }
        }

        return null;
    }
}
