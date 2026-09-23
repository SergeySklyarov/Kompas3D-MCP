using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба L — перечисление эскиза после reopen и жизненный цикл признака.
/// Закрывает два блокатора выпуска: dep.sketch.entities (план §2.2) и SM-30
/// suppress/restore/delete-with-dependencies (план §5.1).
/// </summary>
/// <remarks>
/// Вопросы поставлены так, чтобы ответ не зависел от памяти клиента: документ сохраняется,
/// закрывается и открывается заново, и только после этого эскиз обязан быть найден и прочитан.
/// Собственное представление «что я рисовал» не используется: сервер должен находить объект по
/// модели, иначе адресное редактирование чужого эскиза невозможно в принципе.
///
/// Типизированные вызовы — там, где интерфейс описан; «есть ли у работающего объекта член X»
/// спрашивается у самого объекта через <see cref="Late.Dispid"/> (DISP_E_MEMBERNOTFOUND — ответ
/// объекта, любой прочий сбой — проблема COM, в отчёте они не смешиваются).
/// </remarks>
internal sealed class SketchLifecycleProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;

    /// <summary>
    /// Окно 40×20 центром в (0,0), вырезанное насквозь. Вырезание, а не вторая вставка: второй
    /// изолированный контур дал бы ВТОРОЕ тело, а Api5.Volume читает главное тело, и «объём
    /// не совпал» оказался бы артефактом выбора формы, а не свойством КОМПАС.
    /// </summary>
    private const double RectWidth = 40d;

    private const double RectHeight = 20d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;
    private const double WindowVolume = RectWidth * RectHeight * PlateThickness;

    /// <summary>Пластина 80000 минус окно 8000.</summary>
    private const double CutVolume = PlateVolume - WindowVolume;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private ksSketchDefinition? _sketch;

    /// <summary>Тот же эскиз как объект дерева: SetSketch принимает entity, а не определение.</summary>
    private ksEntity? _sketchEntity;
    private KompasAPI7.IModelContainer? _container7;
    private string _savedPath = string.Empty;
    private double _volumeBeforeEdit;

    /// <summary>Объём, который обязан вернуться после восстановления подавленного признака.</summary>
    private double _volumeWithFeature;

    public SketchLifecycleProbe(ProbeReport report, Options options)
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
            BuildAndReopen();
            if (_sketch is not null)
            {
                EnumerateApi5();
                if (_sketch is not null)
                {
                    EditAddressedApi5();
                    Bridge();
                    DiscoverApi7();
                    SuppressRestore();
                    DeleteFeature();
                }
            }
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("L.E", "Необработанное исключение пробы L");
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
        var step = _report.Begin("L.1", "Свой невидимый экземпляр и доказательство PID",
            "Зонд работает с тем КОМПАС, который сам запустил?");
        try
        {
            var before = KompasIds();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = WaitForNewProcess(before, 90_000);
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется, проба остановлена.");
                return;
            }

            _options.ProcessId = (int)created[0];
            var windowPid = WindowPid(_app.ksGetHWindow());
            step.Data["pid"] = created[0];
            step.Data["pid_from_window"] = windowPid ?? -1;
            step.Observe($"Дифф процессов: {created[0]}; окно COM-объекта: {Api5.Raw(windowPid)}.");
            step.Pass(windowPid == (int)created[0]
                ? "Сеанс атрибутирован двумя независимыми способами."
                : "Способы разошлись — атрибуция под вопросом, дальше не идём.");
            if (windowPid != (int)created[0])
            {
                _app = null!;
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("L.Z", "Завершение сеанса", "Остаётся ли осиротевший процесс?");
        try
        {
            if (_doc is not null)
            {
                step.Data["doc_close"] = Api5.Raw(TryBool(() => _doc.close()));
            }

            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым; закройте его штатно.");
                return;
            }

            step.Data["quit"] = Api5.Raw(TryBool(() => { _app.Quit(); return true; }));
            var pid = _options.ProcessId;
            var gone = pid is null || WaitUntilGone(pid.Value, 30_000);
            step.Pass(gone
                ? "Собственный экземпляр завершён штатно, по имени процессов не убивали."
                : "Процесс жив через 30 с после Quit(): штатное завершение не подтверждено.");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение не подтверждено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════ построение и reopen ══
    private void BuildAndReopen()
    {
        var step = _report.Begin("L.2", "Пластина + эскиз 40×20 + выдавливание, затем save→close→reopen",
            "Что из созданного находится в переоткрытом документе без памяти о собственных рисунках?");
        try
        {
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Create(true, true) != true)
            {
                step.Fail("Document3D().Create(invisible, деталь) не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step, "l") is null)
            {
                step.Fail("Пластина не построена.");
                return;
            }

            step.Observe($"Пластина: V={Api5.Num(Api5.Volume(_part))} (ожидалось {PlateVolume}).");
            _sketch = NewBoxSketch(step);
            if (_sketch is null)
            {
                return;
            }

            if (!CutWindow(step))
            {
                return;
            }

            _savedPath = Path.Combine(_options.WorkDir, "lifecycle-box.m3d");
            step.Data["saved_path"] = _savedPath;
            if (_doc.SaveAs(_savedPath) != true)
            {
                step.Fail("SaveAs не подтверждён возвращаемым значением.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(_savedPath, true) != true)
            {
                step.Fail("Open переоткрытого документа не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            _volumeBeforeEdit = Api5.Volume(_part) ?? double.NaN;
            step.Data["volume_after_reopen"] = Api5.Num(_volumeBeforeEdit);
            step.Observe($"После reopen: V={Api5.Num(_volumeBeforeEdit)}, ожидание {CutVolume}.");
            var features = FeatureNames(step, "после reopen");
            step.Data["features_after_reopen"] = features;

            if (Math.Abs(_volumeBeforeEdit - CutVolume) >= 1d)
            {
                step.Fail("Объём после reopen не совпал с аналитикой — дальше измерять нечего.");
                return;
            }

            _sketch = FindSketchAfterReopen(step);
            if (_sketch is null)
            {
                step.Fail("Эскиз в переоткрытом документе не найден: перечисление и правка по адресу " +
                          "в API5 невозможны, и это и есть ответ на вопрос пробы.");
                return;
            }

            step.Pass($"Переоткрытый документ принят: V={Api5.Num(_volumeBeforeEdit)}, " +
                      $"признаков {features.Count}, эскиз найден по модели.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Построение или reopen не удались: " + ex.Message);
        }
    }

    private ksSketchDefinition? NewBoxSketch(ProbeStep step)
    {
        if (_part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || _part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Observe("API5: базовая плоскость или эскиз не создались.");
            return null;
        }

        _sketchEntity = sketch;
        sketch.name = "l-box";
        definition.SetPlane(plane);
        sketch.Create();
        if (definition.BeginEdit() is not ksDocument2D editor)
        {
            step.Observe("BeginEdit() вернул не ksDocument2D.");
            return null;
        }

        var x0 = -RectWidth / 2d;
        var y0 = -RectHeight / 2d;
        editor.ksLineSeg(x0, y0, x0 + RectWidth, y0, 1);
        editor.ksLineSeg(x0 + RectWidth, y0, x0 + RectWidth, y0 + RectHeight, 1);
        editor.ksLineSeg(x0 + RectWidth, y0 + RectHeight, x0, y0 + RectHeight, 1);
        editor.ksLineSeg(x0, y0 + RectHeight, x0, y0, 1);
        definition.EndEdit();
        _doc.RebuildDocument();
        step.Observe("Эскиз «l-box»: четыре отрезка 40×20 на XOY.");
        return definition;
    }

    private bool CutWindow(ProbeStep step)
    {
        if (_sketchEntity is null)
        {
            step.Observe("Entity эскиза не сохранён — вырезание строить нечем.");
            return false;
        }

        if (_part.NewEntity(CutExtrusion) is not ksEntity operation
            || operation.GetDefinition() is not ksCutExtrusionDefinition definition)
        {
            step.Observe("API5: NewEntity(o3d_cutExtrusion=26) или его определение не получены.");
            return false;
        }

        definition.SetSketch(_sketchEntity);

        // P2.1: etThroughAll=1 работает только при directionType=symmetric(2); число глубины в
        // этом режимеsolver отбрасывает, поэтому передаётся 0 — как в проверенном маршруте адаптера.
        definition.directionType = 2;
        definition.SetSideParam(true, (short)EndConditionThrough, 0d, 0d, false);
        definition.SetSideParam(false, (short)EndConditionThrough, 0d, 0d, false);
        if (operation.Create() != true)
        {
            step.Observe("Create() вырезания вернул не true.");
            return false;
        }

        _doc.RebuildDocument();
        var volume = Api5.Volume(_part);
        step.Observe($"Сквозное окно 40×20: V={Api5.Num(volume)}, ожидание {CutVolume} " +
                     $"(снят объём {WindowVolume}).");
        if (volume is null || Math.Abs(volume.Value - CutVolume) >= 1d)
        {
            step.Observe("Число не совпало — дальше идти нельзя: все последующие замеры опираются на это.");
            return false;
        }

        _volumeWithFeature = volume.Value;
        return true;
    }

    private const short CutExtrusion = 26;
    private const short EndConditionThrough = 1;

    /// <summary>Эскизы части: EntityCollection(o3d_sketch=5), определение ищется по модели.</summary>
    private ksSketchDefinition? FindSketchAfterReopen(ProbeStep step)
    {
        var attempts = new List<string>();
        foreach (var objType in new short[] { Api5.Sketch, 15, 16 })
        {
            object? raw;
            try
            {
                raw = _part.EntityCollection(objType);
            }
            catch (Exception ex)
            {
                attempts.Add($"EntityCollection({objType}): {ex.GetType().Name}");
                continue;
            }

            if (raw is not ksEntityCollection collection)
            {
                attempts.Add($"EntityCollection({objType}): {Api5.RuntimeName(raw)}");
                continue;
            }

            attempts.Add($"EntityCollection({objType}): count={collection.GetCount()}");
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                if (entity.GetDefinition() is ksSketchDefinition sketch)
                {
                    // Тот же объект дерева, а не только определение: правка и выдавливание работают с entity.
                    _sketchEntity = entity;
                    step.Observe($"Эскиз найден: EntityCollection({objType})[{i}] → «{entity.name}» type={entity.type}.");
                    step.Data["sketch_lookup"] = attempts;
                    return sketch;
                }
            }
        }

        step.Data["sketch_lookup"] = attempts;
        step.Observe("Эскиз по модели не найден: " + string.Join(" | ", attempts));
        return null;
    }

    // ═══════════════════════════════════════════════ API5: перечисление объектов ══
    private void EnumerateApi5()
    {
        var step = _report.Begin("L.3", "API5: есть ли у ksDocument2D перечисление объектов",
            "Можно ли увидеть все примитивы эскиза, не помня координат, которые рисовал сам?");
        try
        {
            if (_sketch!.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit переоткрытого эскиза вернул не ksDocument2D.");
                return;
            }

            var names = new[]
            {
                "ksGetObjCount", "ksGetObjectCount", "ksFirstObj", "ksNextObj", "ksGetFirstObj",
                "ksGetNextObj", "ksGetObjList", "ksGetObjects", "ksObjects", "Count", "Elements",
                "ModelObjects", "ksEnumObjects", "ksGetObjArray", "SketchEntities",
            };
            var availability = names.ToDictionary(name => name, name => Late.Dispid(editor, name));
            step.Data["dispatch_member_probe"] = availability;
            var known = availability.Where(p => p.Value.StartsWith("dispid=", StringComparison.Ordinal)).ToList();
            step.Observe(known.Count > 0
                ? "Живой объект знает имена перечисления: " + string.Join(", ", known.Select(p => p.Key + " " + p.Value))
                : "Ни одно из 15 имён перечисления живой объект не знает: ответы — DISP_E_UNKNOWNNAME (0x80020006), то есть имени в IDispatch объекта нет.");

            var found = editor.ksFindObj(0d, -RectHeight / 2d, 1e-3);
            step.Data["ksFindObj_mid_bottom_edge"] = found;
            step.Observe($"ksFindObj в середину нижнего отрезка (0,{-RectHeight / 2d}) → ref {found}: " +
                         "работает на данных, загруженных с диска.");

            var existing = new List<int>();
            for (var reference = 1; reference <= 1024; reference++)
            {
                if (editor.ksExistObj(reference) != 0)
                {
                    existing.Add(reference);
                }
            }

            step.Data["existing_refs_1_1024"] = existing.Count;
            step.Data["refs_sample"] = existing.Take(32).ToList();
            var dense = existing.Count >= 4 && existing.Count <= 12 && existing.Max() - existing.Min() <= 15;
            step.Data["refs_dense"] = dense;
            step.Observe($"ksExistObj(1..1024) вернул {existing.Count} существующих ref " +
                         $"(разброс {(existing.Count > 0 ? existing.Max() - existing.Min() : -1)}): " +
                         string.Join(",", existing.Take(32)));

            var reads = new Dictionary<string, string>();
            foreach (var reference in existing.Take(6))
            {
                foreach (var parType in new[] { 1, 2, 3 })
                {
                    object? param = null;
                    try
                    {
                        var code = editor.ksGetObjParam(reference, param!, parType);
                        reads[$"ref{reference}/par{parType}"] = $"code={code} param={Api5.Raw(param)}";
                    }
                    catch (Exception ex)
                    {
                        reads[$"ref{reference}/par{parType}"] = ex.GetType().Name;
                    }
                }

                try
                {
                    object? box = null;
                    reads[$"ref{reference}/gabarit"] =
                        $"code={editor.ksGetObjGabaritRect(reference, box!)} box={Api5.Raw(box)}";
                }
                catch (Exception ex)
                {
                    reads[$"ref{reference}/gabarit"] = ex.GetType().Name;
                }
            }

            step.Data["reads_by_ref"] = reads;
            _sketch.EndEdit();

            if (dense && found != 0)
            {
                step.Pass($"Перечисление есть: ref плотные ({existing.Count} на 4 ожидаемых отрезка), " +
                          "ksFindObj работает после reopen — адрес существует в модели, а не в памяти клиента.");
            }
            else
            {
                step.Unknown($"Перечисления нет: dispatch-имён {known.Count}, существующих ref {existing.Count} " +
                             "при 4 ожидаемых объектах. Для выпуска это значит: либо API7, либо отказ режима.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Проба перечисления упала: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════ API5: правка по адресу ══
    private void EditAddressedApi5()
    {
        var step = _report.Begin("L.4", "API5: изменить примитивы по ref и перестроить тело",
            "Достаточно ли адреса из модели, чтобы правка дошла до геометрии?");
        try
        {
            if (_sketch!.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit не дал ksDocument2D.");
                return;
            }

            var bottom = editor.ksFindObj(0d, -RectHeight / 2d, 1e-3);
            var top = editor.ksFindObj(0d, RectHeight / 2d, 1e-3);
            step.Data["refs"] = new[] { bottom, top };
            if (bottom == 0 || top == 0)
            {
                _sketch.EndEdit();
                step.Fail($"Стороны не найдены (низ={bottom}, верх={top}) — правка не выполнялась.");
                return;
            }

            var movedBottom = editor.ksMoveObj(bottom, 0d, -10d);
            var movedTop = editor.ksMoveObj(top, 0d, 10d);
            step.Data["ksMoveObj_returns"] = new[] { movedBottom, movedTop };
            _sketch.EndEdit();
            _doc.RebuildDocument();

            var volume = Api5.Volume(_part) ?? double.NaN;

            // Окно стало 40×40: снятый объём 16000, значит V = 80000 − 16000.
            var expected = PlateVolume - RectWidth * (RectHeight + 20d) * PlateThickness;
            step.Data["volume_after_move"] = Api5.Num(volume);
            step.Data["volume_expected"] = expected;
            _volumeBeforeEdit = volume;
            _volumeWithFeature = volume;

            if (movedBottom != 0 && movedTop != 0 && Math.Abs(volume - expected) < 1d)
            {
                step.Pass($"ksMoveObj(ref,…) на переоткрытом эскизе перестроил тело: V={Api5.Num(volume)} " +
                          $"при ожидании {expected}. Адресное изменение эскиза в API5 возможно.");
            }
            else
            {
                step.Unknown($"ksMoveObj вернул {movedBottom}/{movedTop}, но объём {Api5.Num(volume)} не совпал " +
                             $"с {expected}: правка по адресу не подтверждена.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Правка по адресу не выполнена: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════ API7: мост и разведка ══
    private void Bridge()
    {
        var step = _report.Begin("L.5", "Мост API5→API7 в сеансе пробы L",
            "API7 приходит из того же сеанса?");
        try
        {
            var raw = _app.ksGetApplication7();
            if (raw is null)
            {
                step.Fail("ksGetApplication7() вернул null.");
                return;
            }

            var app7 = (KompasAPI7.IApplication)raw;
            app7.Visible = false;
            var transferred = _app.TransferInterface(_doc, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            step.Data["transfer_document"] = Api5.RuntimeName(transferred);

            // Единственный перенос, дающий IModelContainer: документ переносится как
            // IKompasDocument3D (так измерено в пробе A7.2), а контейнером модели является ЧАСТЬ.
            if (transferred is not KompasAPI7.IKompasDocument3D document7)
            {
                step.Fail($"TransferInterface(документ, ksAPI7Dual) не дал IKompasDocument3D " +
                          $"(получено {Api5.RuntimeName(transferred)}).");
                return;
            }

            var partRaw = document7.TopPart;
            step.Data["api7_part_type"] = Api5.RuntimeName(partRaw);
            _container7 = partRaw as KompasAPI7.IModelContainer;
            if (_container7 is null && partRaw is not null)
            {
                // QI вручную: обёртка объявления не гарантирует, что объект его поддерживает.
                step.Data["modelcontainer_qi"] = Late.Dispid(partRaw, "Holes3D");
            }

            if (_container7 is null)
            {
                step.Fail($"Part дал {Api5.RuntimeName(partRaw)}, а не IModelContainer — API7-модель недоступна.");
                return;
            }

            step.Pass("Мост есть: IApplication + IKompasDocument3D + IModelContainer того же сеанса " +
                      "через ksGetApplication7() + TransferInterface(ksAPI7Dual).");
        }
        catch (Exception ex)
        {
            step.Fail("Мост не построен: " + ex.Message);
        }
    }

    private void DiscoverApi7()
    {
        var step = _report.Begin("L.6", "API7: чем перечисляются сущности эскиза",
            "Есть ли типизированный доступ к объектам эскиза там, где API5 его не дал?");
        if (_container7 is null)
        {
            step.Unknown("Моста API7 нет — шаг не выполнялся, а не пройден.");
            return;
        }

        try
        {
            var sketches = _container7.Sketchs;
            step.Data["api7_sketches_count"] = sketches.Count;
            step.Observe($"IModelContainer.Sketchs.Count = {sketches.Count}.");
            if (sketches.Count == 0)
            {
                step.Unknown("Sketchs пуст: эскиз API7-представления не имеет либо коллекция иная.");
                return;
            }

            // Item — параметрическое свойство: типизированный доступ к нему в этой обёртке не
            // объявлен, поэтому спрашиваем у самого объекта (позднее связывание, проба на это и заведена).
            object? sketch = null;
            foreach (var getter in new[] { "Item", "get_Item" })
            {
                try
                {
                    sketch = Late.Call(sketches, getter, 0);
                    step.Data["api7_sketch_item_route"] = getter;
                    break;
                }
                catch (Exception ex)
                {
                    step.Observe($"Sketchs.{getter}(0): {ex.GetType().Name}");
                }
            }

            if (sketch is null)
            {
                step.Unknown("Эскиз из IModelContainer.Sketchs не извлечён — см. наблюдения шага.");
                return;
            }
            step.Data["api7_sketch_type"] = Api5.RuntimeName(sketch);
            var probe = new[]
            {
                "Elements", "ModelObjects", "IndModelObjects", "Curves", "Constraints",
                "Dimensions", "Count", "GetSketch",
            }.ToDictionary(name => name, name => Late.Dispid(sketch!, name));
            step.Data["api7_sketch_members"] = probe;

            object? fragment = null;
            try
            {
                fragment = Late.Call(sketch!, "BeginEdit");
            }
            catch (Exception ex)
            {
                step.Observe("BeginEdit через IDispatch: " + ex.GetType().Name);
            }

            step.Data["api7_beginedit_type"] = Api5.RuntimeName(fragment);
            if (fragment is not null)
            {
                step.Data["api7_fragment_members"] = new[]
                {
                    "ModelObjects", "IndModelObjects", "LayoutSheets", "Elements", "GetFirstObject",
                }.ToDictionary(name => name, name => Late.Dispid(fragment, name));
                try
                {
                    Late.Call(fragment, "EndEdit");
                }
                catch (Exception ex)
                {
                    step.Observe("EndEdit фрагмента: " + ex.GetType().Name);
                }
            }

            step.Pass("Разведка API7 выполнена; имена и типы — в данных шага, по ним и выбирается маршрут.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Разведка API7 упала: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════ жизненный цикл признака ══
    private void SuppressRestore()
    {
        var step = _report.Begin("L.7", "Подавление и восстановление: ksFeature.excluded",
            "Отключается ли признак так, что объём уходит и возвращается?");
        try
        {
            var target = LastFeature(step);
            if (target?.GetFeature() is not ksFeature feature)
            {
                step.Fail($"GetFeature() не дал ksFeature ({Api5.RuntimeName(target?.GetFeature())}).");
                return;
            }

            var before = Api5.Volume(_part) ?? double.NaN;
            feature.excluded = true;
            _doc.RebuildDocument();
            var suppressed = Api5.Volume(_part) ?? double.NaN;
            feature.excluded = false;
            _doc.RebuildDocument();
            var restored = Api5.Volume(_part) ?? double.NaN;
            step.Data["volumes"] = new Dictionary<string, string>
            {
                ["до"] = Api5.Num(before),
                ["подавлен"] = Api5.Num(suppressed),
                ["восстановлен"] = Api5.Num(restored),
            };
            step.Observe($"V: до {Api5.Num(before)} → excluded {Api5.Num(suppressed)} (ожидание {PlateVolume}) " +
                         $"→ снято {Api5.Num(restored)} (ожидание {Api5.Num(before)}).");

            // Подавление вырезания объём УВЕЛИЧИВАЕТ (окно перестаёт сниматься), подавление
            // приклейки — уменьшает. Направление поэтому не утверждается: проверяются факт
            // изменения, возврат к исходному числу и неизменность числа признаков.
            var changed = Math.Abs(suppressed - before) > 1d;
            var cameBack = Math.Abs(restored - before) < 1d;
            var matchesAnalytic = Math.Abs(suppressed - PlateVolume) < 1d;
            step.Data["volume_changed"] = changed;
            step.Data["volume_came_back"] = cameBack;
            step.Data["volume_equals_plate"] = matchesAnalytic;
            _volumeBeforeEdit = restored;
            _volumeWithFeature = restored;

            if (changed && cameBack && matchesAnalytic)
            {
                step.Pass("excluded=true убирает окно (V = объём пластины 80000), false возвращает " +
                          $"прежнее {Api5.Num(before)}: подавление и восстановление измерены на признаке, " +
                          "созданном не этим сеансом после reopen.");
            }
            else
            {
                step.Unknown("Эффект подавления не совпал с аналитикой — режим не подтверждён.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Подавление не измерено: " + ex.Message);
        }
    }

    private void DeleteFeature()
    {
        var step = _report.Begin("L.8", "Удаление признака и перечень зависимых до удаления",
            "Удаляется ли признак средством API5 и чем честным перечисляются его зависимости?");
        try
        {
            var target = LastFeature(step);
            if (target is null)
            {
                return;
            }

            var name = target.name ?? string.Empty;
            var before = Api5.Volume(_part) ?? double.NaN;
            var names = FeatureNames(step, "до удаления");
            // Собственного члена «зависимые» нет ни в API5, ни в API7 (проверено рефлексией по
            // обеим сборкам), поэтому перечисляются признаки ПОСЛЕ удаляемого — как кандидаты.
            var at = names.IndexOf(name);
            var candidates = at >= 0 ? names.Skip(at + 1).ToList() : names;
            step.Data["target"] = name;
            step.Data["candidate_dependents"] = candidates;
            step.Observe($"Кандидаты зависимых (признаки после «{name}» в дереве): {string.Join(", ", candidates)}.");

            var deleted = TryBool(() => _doc.DeleteObject(target));
            _doc.RebuildDocument();
            var after = Api5.Volume(_part) ?? double.NaN;
            var remaining = FeatureNames(step, "после удаления");
            step.Data["delete_return"] = Api5.Raw(deleted);
            step.Data["volume_before"] = Api5.Num(before);
            step.Data["volume_after"] = Api5.Num(after);
            step.Observe($"DeleteObject → {Api5.Raw(deleted)}; V {Api5.Num(before)} → {Api5.Num(after)}; " +
                         $"признаков {names.Count} → {remaining.Count}.");

            step.Data["volume_expected_after_delete"] = PlateVolume;
            var volumeBack = Math.Abs(after - PlateVolume) < 1d;
            step.Data["volume_back_to_plate"] = volumeBack;
            if (deleted == true && remaining.Count == names.Count - 1 && volumeBack)
            {
                step.Pass("ksDocument3D.DeleteObject удаляет признак: число признаков минус один и V " +
                          "возвращается к пластине. Зависимые — кандидаты по порядку дерева, и в " +
                          "контракте надо писать именно это.");
            }
            else
            {
                step.Unknown($"Удаление не подтверждено (возврат {Api5.Raw(deleted)}, признаки " +
                             $"{names.Count}→{remaining.Count}).");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Удаление не измерено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ хелперы ══
    private List<string> FeatureNames(ProbeStep step, string moment)
    {
        try
        {
            if (_part.EntityCollection(Api5.OperationElement) is not ksEntityCollection collection)
            {
                step.Observe($"{moment}: EntityCollection(110) вернул не ksEntityCollection.");
                return new List<string>();
            }

            var names = new List<string>();
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity)
                {
                    names.Add(entity.name ?? $"[{i}]:" + entity.type);
                }
            }

            step.Observe($"{moment}: признаков {names.Count} ({string.Join(", ", names)}).");
            return names;
        }
        catch (Exception ex)
        {
            step.Observe($"{moment}: перечень признаков не удался — {ex.GetType().Name}.");
            return new List<string>();
        }
    }

    /// <summary>Последний признак дерева — созданное позже остальных выдавливание.</summary>
    private ksEntity? LastFeature(ProbeStep step)
    {
        if (_part.EntityCollection(Api5.OperationElement) is not ksEntityCollection collection)
        {
            step.Fail("EntityCollection(o3d_operationElement=110) недоступна.");
            return null;
        }

        ksEntity? last = null;
        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (collection.GetByIndex(i) is ksEntity entity)
            {
                last = entity;
            }
        }

        if (last is null)
        {
            step.Fail("Признаков построения нет — измерять жизненный цикл нечего.");
        }
        else
        {
            step.Observe($"Берётся признак «{last.name}» (type={last.type}).");
        }

        return last;
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
            return ex.GetType().Name;
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Процесса нет — это и есть ответ, а не ошибка.
            return false;
        }
    }

    private static uint[] KompasIds() =>
        Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();

    private static uint[] WaitForNewProcess(uint[] before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var created = KompasIds().Except(before).ToArray();
            if (created.Length > 0)
            {
                return created;
            }

            Thread.Sleep(250);
        }

        return Array.Empty<uint>();
    }

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ProcessAlive(pid))
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return !ProcessAlive(pid);
    }

    private static int? WindowPid(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = NativeWindow.GetWindowThreadProcessId(handle, out var processId);
        return processId == 0 ? null : (int)processId;
    }
}
