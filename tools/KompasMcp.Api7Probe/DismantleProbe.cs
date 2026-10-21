using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe D — exploded assembly: is a dismantle step WRITABLE through the documented route
/// (order <c>OMEGA_G9_DISMANTLE</c>).</summary>
/// <remarks>DOC: <c>idismantleparameters.html</c> — QI from <c>IAssemblyDocument</c>, «Версия: Компас v20»;
/// <c>idismantleparameters_adddismantlestep.html</c> — <c>AddDismantleStep()</c> returns
/// <c>IDismantleStep</c>; <c>idismantlestep_parts.html</c> — «VARIANT ... массив SafeArray
/// VT_ARRAY|VT_DISPATCH» of <c>IPart7</c>, read and write; <c>idismantlestep_distance.html</c> — double,
/// read and write; <c>idismantlestep_directionobject.html</c> — <c>IModelObject</c>, read and write;
/// <c>idismantlestep_vector.html</c> — <c>IVector3D</c>, READ ONLY; <c>ksapi_idismantlestep_update.html</c>
/// — <c>int32_t Update()</c>.
/// INVARIANT: the step is confirmed by reading it back and by a measured change of component placement,
/// never by the object the call returned. History: docs/decisions/dismantle.md#probe</remarks>
internal sealed class DismantleProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double StepDistance = 40d;
    private const int PlaneXoy = 1;
    private const int PlaneXoz = 2;
    private const int PlaneYoz = 3;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D? _assembly5;
    private ksDocument3D? _reopened5;
    private IAxis3D? _xAxis;
    private string _assemblyPath = string.Empty;
    private string _sourcePath = string.Empty;

    public DismantleProbe(ProbeReport report, Options options)
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
            if (BuildSource() is null || BuildAssembly() is null)
            {
                return;
            }

            var parameters = Parameters();
            if (parameters is null)
            {
                return;
            }

            WriteStep(parameters);
            ReadBack(parameters);
            ModeAndGeometry(parameters);
            Semantics(parameters);
            SaveReopen();
            DeleteStep();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("D.X", "Необработанное исключение пробы D");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("D.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
        try
        {
            var before = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray()
                .Except(before).ToArray();
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            step.Pass("Экземпляр запущен и атрибутирован диффом процессов.");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private string? BuildSource()
    {
        var step = _report.Begin("D.2", "Деталь-источник: плита на диске",
            "Из чего вставлять компоненты?");
        var directory = Path.Combine(_options.WorkDir, "dismantle");
        Directory.CreateDirectory(directory);
        _sourcePath = Path.Combine(directory, "g9-source.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, true) != true)
        {
            step.Fail("Документ-источник не создан.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "g9-src") is null)
        {
            step.Fail("Плита не построена.");
            return null;
        }

        document.RebuildDocument();
        var saved = document.SaveAs(_sourcePath);
        step.Data["save_as"] = saved;
        step.Data["path"] = _sourcePath;
        // MEASURED: AddFromFile answers null while the source is open in this session.
        document.close();
        if (saved == true && File.Exists(_sourcePath))
        {
            step.Pass("Источник сохранён и закрыт.");
            return _sourcePath;
        }

        step.Fail("Источник не сохранён.");
        return null;
    }

    private ksDocument3D? BuildAssembly()
    {
        var step = _report.Begin("D.3", "Сборка из двух компонентов",
            "Есть ли сборка, на которой шаг разнесения вообще применим?");
        _assemblyPath = Path.Combine(_options.WorkDir, "dismantle", "g9-assembly.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, false) != true)
        {
            step.Fail("Create(невидимый, сборка) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "G9-asm";
        part.Update();

        // DOC (iparts7_addfromfile.html): the documented insert route; returns the inserted component.
        var parts7 = (_app.TransferInterface(document, 2, 0) as IKompasDocument3D)?.TopPart?.Parts;
        if (parts7 is null)
        {
            step.Fail("IPart7.Parts не дал IParts7 — вставлять нечем.");
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                step.Observe("AddFromFile попытка " + attempt + " → "
                    + Api5.RuntimeName(parts7.AddFromFile(_sourcePath, true, true)));
            }
            catch (Exception ex)
            {
                step.Fail("AddFromFile бросил: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // MEASURED (block C1 of the mates order): without an offset both inserts sit at the origin and
        // the assembly degenerates; the second component is moved 150 mm along X.
        var offset = new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            150, 0, 0, 1,
        };
        if (document.PartCollection(true) is ksPartCollection inserted && inserted.GetCount() >= 2
            && inserted.GetByIndex(1) is ksPart second && document.DefaultPlacement() is ksPlacement placement)
        {
            placement.InitByMatrix3D(offset);
            second.SetPlacement(placement);
            second.UpdatePlacement();
            document.RebuildDocument();
        }

        var count = Api5.SafeInt(() => parts7.Count);
        step.Data["components"] = count;
        var saved = document.SaveAs(_assemblyPath);
        step.Data["save_as"] = saved;
        step.Data["path"] = _assemblyPath;
        if (count == 2)
        {
            _assembly5 = document;
            step.Pass("Сборка из двух компонентов создана и сохранена.");
            return document;
        }

        step.Fail("Компонентов " + count + ", ожидалось 2.");
        return null;
    }

    /// <summary>The decisive QI of the order: <c>IDismantleParameters</c> is «дополнительный» for
    /// <c>IAssemblyDocument</c>, so it is reached by a cast and nothing else.</summary>
    private IDismantleParameters? Parameters()
    {
        var step = _report.Begin("D.4", "IDismantleParameters через QueryInterface",
            "Отдаёт ли документ сборки интерфейс параметров разнесения?");
        var document7 = Api5.SafeObject(() => _app.TransferInterface(_assembly5!, 2, 0)) as IKompasDocument3D;
        step.Data["document7"] = Api5.RuntimeName(document7);
        var assembly = document7 as IAssemblyDocument;
        step.Data["assembly_document"] = Api5.RuntimeName(assembly);
        if (assembly is null)
        {
            step.Fail("Документ сборки не отвечает QI(IAssemblyDocument).");
            return null;
        }

        var parameters = assembly as IDismantleParameters;
        step.Data["dismantle_parameters"] = Api5.RuntimeName(parameters);
        if (parameters is null)
        {
            step.Fail("IAssemblyDocument не отвечает QI(IDismantleParameters) — маршрута нет.");
            return null;
        }

        step.Data["dismantle_mode_default"] = Api5.Raw(Api5.SafeBool(() => assembly.DismantleMode));
        step.Data["steps_count_before"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Data["step0_before"] = Api5.RuntimeName(Api5.SafeObject(() => parameters.DismantleStep[0]));
        step.Pass("QI(IDismantleParameters) отвечает; шагов до записи "
            + Api5.Raw(step.Data["steps_count_before"]) + ".");
        return parameters;
    }

    /// <summary>The main risk of the order: components, distance and direction written to a step, and
    /// what the step answers back after <c>Update()</c>.</summary>
    private void WriteStep(IDismantleParameters parameters)
    {
        var step = _report.Begin("D.5", "Запись шага: Parts, Distance, DirectionObject, Update",
            "Принимает ли шаг документированные входы и входит ли он в модель?");
        var top = Api5.SafeObject(() => ((IKompasDocument3D)_app.TransferInterface(_assembly5!, 2, 0)!).TopPart)
            as IPart7;
        var components = Components(top, step);
        if (components.Count < 2)
        {
            step.Fail("Компоненты не прочитаны как IPart7 — адресовать шаг нечем.");
            return;
        }

        var target = components[0];
        var created = Api5.SafeObject(() => parameters.AddDismantleStep());
        step.Data["add_returned"] = Api5.RuntimeName(created);
        if (created is not IDismantleStep dismantle)
        {
            step.Fail("AddDismantleStep() не отдал IDismantleStep: " + Api5.RuntimeName(created));
            return;
        }

        step.Data["steps_count_after_add"] = Api5.SafeInt(() => parameters.DismantleStepsCount);

        // Parts is VARIANT holding a SafeArray of IPart7; both a managed array and a bare object are
        // tried, because the interop wrapper decides which one crosses the boundary.
        foreach (var (form, value) in new (string, object)[]
                 {
                     ("object[]", new object[] { target }),
                     ("IPart7", target),
                 })
        {
            try
            {
                dismantle.Parts = value;
                var read = Api5.SafeObject(() => dismantle.Parts);
                var length = read is Array array ? array.Length : read is null ? 0 : 1;
                step.Data["parts_" + form] = Api5.RuntimeName(read) + "/" + length;
                step.Observe($"Parts как {form}: записано, читается {Api5.RuntimeName(read)}, "
                    + $"элементов {length}");
                if (length > 0)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["parts_" + form] = "отказ: " + HResult.Describe(ex);
                step.Observe($"Parts как {form}: отказ {HResult.Describe(ex)}");
            }
        }

        dismantle.Distance = StepDistance;
        step.Data["distance_written"] = Api5.Raw(Api5.SafeDouble(() => dismantle.Distance));

        // Direction is an IModelObject, not a vector: Vector is read-only. Each documented model object
        // that can carry a direction is tried, and every answer is recorded.
        var candidates = new List<(string Name, object? Object)>
        {
            ("плоскость XoY", DefaultPlane(PlaneXoy)),
            ("плоскость XoZ", DefaultPlane(PlaneXoz)),
            ("плоскость YoZ", DefaultPlane(PlaneYoz)),
            ("ребро компонента", EdgeOf(0)),
            ("грань компонента", FaceOf(0)),
            ("компонент", target),
            ("ось сборки", DirectionAxis(step)),
        };
        foreach (var candidate in candidates)
        {
            if (candidate.Object is not IModelObject model)
            {
                step.Observe($"{candidate.Name}: не IModelObject ({Api5.RuntimeName(candidate.Object)})");
                continue;
            }

            try
            {
                dismantle.DirectionObject = model;
                var readBack = Api5.SafeObject(() => dismantle.DirectionObject);
                var vector = Api5.SafeObject(() => dismantle.Vector);
                step.Observe($"{candidate.Name}: DirectionObject записан, читается "
                    + $"{Api5.RuntimeName(readBack)}, Vector {Api5.RuntimeName(vector)} "
                    + DescribeVector(vector));
                step.Data["vector_members"] = VectorMembers(vector);
                if (readBack is not null)
                {
                    step.Data["direction"] = candidate.Name;
                    step.Data["vector"] = DescribeVector(vector);
                    break;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["direction_" + candidate.Name] = "отказ: " + HResult.Describe(ex);
                step.Observe($"{candidate.Name}: отказ {HResult.Describe(ex)}");
            }
        }

        OrderVariant(parameters, target);
        MultiComponentStep(parameters, components);

        step.Data["update"] = Api5.SafeInt(() => dismantle.Update());
        _assembly5!.RebuildDocument();
        step.Data["steps_count_after_update"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        if (Api5.SafeInt(() => dismantle.Update()) is not null)
        {
            step.Pass("Шаг создан; входы записаны, ответ чтения — в данных шага.");
        }
        else
        {
            step.Fail("Update() шага не ответил.");
        }
    }

    private void ReadBack(IDismantleParameters parameters)
    {
        var step = _report.Begin("D.6", "Шаг читается обратно",
            "Что вернулось из модели: число шагов, компоненты, расстояние, вектор?");
        step.Data["count"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        var live = Api5.SafeObject(() => parameters.DismantleStep[0]) as IDismantleStep;
        step.Data["step0"] = Api5.RuntimeName(live);
        if (live is null)
        {
            step.Fail("Шаг по индексу 0 не читается — записи нет.");
            return;
        }

        var parts = Api5.SafeObject(() => live.Parts);
        step.Data["parts"] = Api5.RuntimeName(parts);
        step.Data["parts_count"] = parts is Array array ? array.Length : parts is null ? 0 : 1;
        var distance = Api5.SafeDouble(() => live.Distance);
        step.Data["distance"] = Api5.Raw(distance);
        step.Data["vector"] = DescribeVector(Api5.SafeObject(() => live.Vector));
        step.Data["direction_object"] = Api5.RuntimeName(Api5.SafeObject(() => live.DirectionObject));
        if (distance is not null && Math.Abs(distance.Value - StepDistance) < 1e-6
            && step.Data["parts_count"] is int count && count > 0)
        {
            step.Pass("Шаг читается: компоненты и расстояние те же, что записаны.");
        }
        else
        {
            step.Fail("Шаг читается не тем, что записано.");
        }
    }

    /// <summary>The proof the order asks for: does the documented read of component placement see the
    /// dismantled state, and does switching the mode off return the assembled state?</summary>
    private void ModeAndGeometry(IDismantleParameters parameters)
    {
        var step = _report.Begin("D.7", "Разнесение видно чтением: положение компонентов",
            "Меняется ли положение компонента шага при включении режима и возвращается ли обратно?");
        var document7 = Api5.SafeObject(() => _app.TransferInterface(_assembly5!, 2, 0)) as IKompasDocument3D;
        var top = document7?.TopPart;
        var components = Components(top, step);
        step.Data["components"] = components.Count;
        if (components.Count < 2)
        {
            step.Fail("Компоненты не прочитаны — измерять нечего.");
            return;
        }

        var assembled = components.Select(c => Translation(top!, c)).ToList();
        step.Data["assembled"] = assembled.Select(Point).ToList();
        step.Data["boxes_assembled"] = Boxes(_assembly5!, step);

        var mode = Api5.SafeBool(() => ((IAssemblyDocument)document7!).DismantleMode);
        step.Data["mode_before"] = Api5.Raw(mode);
        try
        {
            ((IAssemblyDocument)document7!).DismantleMode = true;
        }
        catch (COMException ex)
        {
            step.Fail("Запись DismantleMode=true отказала: " + HResult.Describe(ex));
            return;
        }

        _assembly5!.RebuildDocument();
        step.Data["mode_written"] = Api5.Raw(Api5.SafeBool(() => ((IAssemblyDocument)document7!).DismantleMode));
        var exploded = components.Select(c => Translation(top!, c)).ToList();
        step.Data["exploded"] = exploded.Select(Point).ToList();
        step.Data["boxes_exploded"] = Boxes(_assembly5!, step);
        var moved = assembled.Zip(exploded, (a, b) => Delta(a, b)).ToList();
        step.Data["deltas"] = moved.Select(Point).ToList();
        step.Observe("Смещения компонентов в разнесённом виде: "
            + string.Join("; ", moved.Select(Point)));

        ((IAssemblyDocument)document7!).DismantleMode = false;
        _assembly5.RebuildDocument();
        var back = components.Select(c => Translation(top!, c)).ToList();
        step.Data["restored"] = back.Select(Point).ToList();
        step.Data["boxes_restored"] = Boxes(_assembly5!, step);

        var anyMoved = moved.Any(d => d is not null
            && (Math.Abs(d[0]) > 1e-6 || Math.Abs(d[1]) > 1e-6 || Math.Abs(d[2]) > 1e-6));
        var restored = assembled.Zip(back, (a, b) => Delta(a, b))
            .All(d => d is not null && Math.Abs(d[0]) < 1e-6 && Math.Abs(d[1]) < 1e-6
                && Math.Abs(d[2]) < 1e-6);
        step.Data["any_moved"] = anyMoved;
        step.Data["restored_exactly"] = restored;
        if (anyMoved && restored)
        {
            step.Pass("Разнесение видно документированным чтением матриц и снимается выключением режима.");
        }
        else
        {
            // MEASURED-or-not is the order's decision point, so the answer is recorded, not forced.
            step.Unknown($"Разнесение чтением матриц: смещение {anyMoved}, возврат {restored}. "
                + "Если оба false — режим только отображения, подтверждение идёт чтением шагов.");
        }
    }

    /// <summary>The semantics the tool has to encode: what an empty step list does in the mode, whether
    /// a step removal while the mode is on returns the placement, and what zero and negative distances
    /// mean. History: docs/decisions/dismantle.md#probe</summary>
    private void Semantics(IDismantleParameters parameters)
    {
        var step = _report.Begin("D.10", "Семантика режима: пустой список, удаление, знак расстояния",
            "Что означает режим без шагов, удаление шага при включённом режиме и отрицательное расстояние?");
        var document7 = Api5.SafeObject(() => _app.TransferInterface(_assembly5!, 2, 0)) as IKompasDocument3D;
        var assembly = document7 as IAssemblyDocument;
        var top = document7?.TopPart;
        var components = Components(top, step);
        if (assembly is null || top is null || components.Count < 2)
        {
            step.Fail("Сборка или компоненты не прочитаны.");
            return;
        }

        var assembled = components.Select(c => Translation(top, c)).ToList();
        step.Data["assembled"] = assembled.Select(Point).ToList();

        // (1) mode on with the two steps from D.5/D.5c, then clear them: does the placement return?
        assembly.DismantleMode = true;
        _assembly5!.RebuildDocument();
        step.Data["mode_on_with_steps"] = components.Select(c => Point(Translation(top, c))).ToList();
        step.Data["clear"] = Api5.Raw(Api5.SafeBool(() => parameters.ClearDismantleParameters()));
        _assembly5.RebuildDocument();
        step.Data["after_clear_mode_on"] = components.Select(c => Point(Translation(top, c))).ToList();

        // (2) a fresh step with distance 0 and then a negative distance on the X axis.
        var axis = DirectionAxis(step);
        step.Data["axis"] = Api5.RuntimeName(axis);
        var target = components[0];
        foreach (var distance in new[] { 0d, -30d })
        {
            if (Api5.SafeObject(() => parameters.AddDismantleStep()) is not IDismantleStep live)
            {
                step.Fail("Шаг не создан.");
                return;
            }

            live.Parts = new object[] { target };
            live.Distance = distance;
            if (axis is IModelObject model)
            {
                live.DirectionObject = model;
            }

            step.Data["update_" + distance] = Api5.SafeInt(() => live.Update());
            _assembly5.RebuildDocument();
            step.Data["distance_read_" + distance] = Api5.Raw(Api5.SafeDouble(() => live.Distance));
            step.Data["placement_" + distance] = Point(Translation(top, components[0]));
            Api5.SafeObject(() => parameters.DeleteDismantleStep(0));
            _assembly5.RebuildDocument();
        }

        // (3) mode off; one step is left in place so the save/reopen step has something to keep.
        assembly.DismantleMode = false;
        _assembly5.RebuildDocument();
        step.Data["mode_off"] = components.Select(c => Point(Translation(top, c))).ToList();
        if (Api5.SafeObject(() => parameters.AddDismantleStep()) is IDismantleStep kept)
        {
            kept.Parts = new object[] { target };
            kept.Distance = StepDistance;
            if (axis is IModelObject model)
            {
                kept.DirectionObject = model;
            }

            step.Data["kept_update"] = Api5.SafeInt(() => kept.Update());
            _assembly5.RebuildDocument();
        }

        step.Data["count"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Unknown("Семантика записана; правила инструмента берутся из этих данных.");
    }

    private void SaveReopen()
    {
        var step = _report.Begin("D.8", "Сохранение и переоткрытие: шаги и режим",
            "Переживают ли шаги и режим сохранение файла?");
        try
        {
            _assembly5!.Save();
            step.Data["closed"] = Api5.Raw(Api5.SafeBool(() => _assembly5!.close()));
            var reopened = (ksDocument3D)_app.Document3D();
            var opened = Api5.SafeBool(() => reopened.Open(_assemblyPath, true));
            _reopened5 = reopened;
            step.Data["opened"] = Api5.Raw(opened);
            var document7 = Api5.SafeObject(() => _app.TransferInterface(reopened, 2, 0))
                as IKompasDocument3D;
            var assembly = document7 as IAssemblyDocument;
            var parameters = assembly as IDismantleParameters;
            step.Data["parameters_after_reopen"] = Api5.RuntimeName(parameters);
            step.Data["count_after_reopen"] = parameters is null
                ? null
                : Api5.SafeInt(() => parameters.DismantleStepsCount);
            step.Data["mode_after_reopen"] = assembly is null
                ? null
                : Api5.Raw(Api5.SafeBool(() => assembly.DismantleMode));
            var live = parameters is null
                ? null
                : Api5.SafeObject(() => parameters.DismantleStep[0]) as IDismantleStep;
            step.Data["distance_after_reopen"] = live is null
                ? null
                : Api5.Raw(Api5.SafeDouble(() => live.Distance));
            var partsAfterReopen = live is null ? null : Api5.SafeObject(() => live.Parts);
            step.Data["parts_after_reopen"] = partsAfterReopen is null
                ? null
                : partsAfterReopen is Array reopenedArray
                    ? reopenedArray.Length
                    : 1;
            if (step.Data["count_after_reopen"] is int count && count > 0 && live is not null)
            {
                step.Pass("Шаг и режим пережили сохранение и переоткрытие.");
            }
            else
            {
                step.Fail("После переоткрытия шаг не читается — шаги не сохраняются.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Fail("Сохранение/переоткрытие отказало: " + HResult.Describe(ex));
        }
    }

    private void DeleteStep()
    {
        var step = _report.Begin("D.9", "Удаление шага и очистка",
            "Удаляются ли шаги документированными методами?");
        var document = _reopened5 ?? _assembly5!;
        var assembly = Api5.SafeObject(() => _app.TransferInterface(document, 2, 0)) as IAssemblyDocument;
        var parameters = assembly as IDismantleParameters;
        if (parameters is null)
        {
            step.Fail("Параметры разнесения после переоткрытия не получены.");
            return;
        }

        var before = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Data["before"] = before;
        step.Data["delete"] = Api5.Raw(Api5.SafeBool(() => parameters.DeleteDismantleStep(0)));
        var afterDelete = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Data["after_delete"] = afterDelete;
        Api5.SafeObject(() => parameters.AddDismantleStep());
        step.Data["after_add_again"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Data["clear"] = Api5.Raw(Api5.SafeBool(() => parameters.ClearDismantleParameters()));
        var afterClear = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Data["after_clear"] = afterClear;
        if (afterDelete is not null && afterDelete < before && afterClear == 0)
        {
            step.Pass("Удаление шага и очистка работают.");
        }
        else
        {
            step.Fail("Удаление шага или очистка не изменили число шагов.");
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("D.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            _app.Quit();
            var pid = _options.ProcessId;
            var gone = pid == 0 || SpinWait.SpinUntil(
                () => Process.GetProcessesByName("KOMPAS").All(p => p.Id != pid), TimeSpan.FromSeconds(20));
            if (gone)
            {
                step.Pass("Процесс завершился.");
            }
            else
            {
                step.Fail("Процесс остался после Quit().");
            }
        }
        catch (Exception ex)
        {
            step.Fail("Завершение отказало: " + ex.Message);
        }
    }

    private List<IPart7> Components(IPart7? top, ProbeStep step)
    {
        var result = new List<IPart7>();
        if (top is null)
        {
            return result;
        }

        var raw = Api5.SafeObject(() => top.get_PartsEx((object)(int)ksPart7CollectionTypeEnum.ksAllParts));
        switch (raw)
        {
            case Array array:
                result.AddRange(array.OfType<IPart7>());
                break;
            case IPart7 single:
                result.Add(single);
                break;
            default:
                step.Observe("PartsEx вернул " + Api5.RuntimeName(raw));
                break;
        }

        return result;
    }

    /// <summary>The translation of a component, read through the documented
    /// <c>IPart7.GetSummMatrix</c>; the matrix is 16 elements, row-major 4x4.</summary>
    private double[]? Translation(IPart7 parent, IPart7 child)
    {
        if (child is not Part7 typed)
        {
            return null;
        }

        var matrix = Api5.SafeObject(() => parent.GetSummMatrix(typed));
        if (matrix is not Array array || array.Length < 16)
        {
            return null;
        }

        var values = array.Cast<object>().Select(v => Convert.ToDouble(v)).ToArray();
        return new[] { values[12], values[13], values[14] };
    }

    /// <summary>A component's first edge as the direction object: «направляющий объект» is an
    /// <c>IModelObject</c>, and an edge is the object a client has when it knows a direction from the
    /// geometry. API5 route: <c>ksPart.BodyCollection</c> → <c>ksBody.FaceCollection</c> →
    /// <c>ksFace.EdgeCollection</c>, then transferred to API7.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private object? EdgeOf(int index) => TransferFirst(index, body =>
        ((ksEdgeCollection)((ksFaceDefinition)((ksFaceCollection)((ksBody)body).FaceCollection()).GetByIndex(0))
            .EdgeCollection()).GetByIndex(0));

    private object? FaceOf(int index) => TransferFirst(index, body =>
        ((ksFaceCollection)((ksBody)body).FaceCollection()).GetByIndex(0));

    private object? TransferFirst(int index, Func<object, object?> pick)
    {
        var part = ComponentPart5(index);
        var body = part is null
            ? null
            : Api5.SafeObject(() => ((ksBodyCollection)part.BodyCollection()).GetByIndex(0));
        var picked = body is null ? null : Api5.SafeObject(() => pick(body));
        return picked is null ? null : Api5.SafeObject(() => _app.TransferInterface(picked, 2, 0));
    }

    private ksPart? ComponentPart5(int index) =>
        _assembly5?.PartCollection(true) is ksPartCollection collection
            ? Api5.SafeObject(() => collection.GetByIndex(index)) as ksPart
            : null;

    /// <summary>Per-component body bbox through the documented <c>ksBody.GetGabarit</c>: the second
    /// documented read the order allows for the explosion proof.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private List<string> Boxes(ksDocument3D document, ProbeStep step)
    {
        var result = new List<string>();
        if (document.PartCollection(true) is not ksPartCollection collection)
        {
            return result;
        }

        for (var index = 0; index < collection.GetCount(); index++)
        {
            var part = Api5.SafeObject(() => collection.GetByIndex(index)) as ksPart;
            var body = part is null
                ? null
                : Api5.SafeObject(() => ((ksBodyCollection)part.BodyCollection()).GetByIndex(0)) as ksBody;
            if (body is null)
            {
                result.Add("<нет тела>");
                continue;
            }

            try
            {
                var read = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                result.Add(read
                    ? Point(new[] { x1, y1, z1 }) + "…" + Point(new[] { x2, y2, z2 })
                    : "<габарит не прочитан>");
            }
            catch (COMException ex)
            {
                result.Add("<отказ " + HResult.Describe(ex) + ">");
            }
        }

        step.Data["boxes_count"] = result.Count;
        return result;
    }

    /// <summary>The direction object a client can name by a vector: an auxiliary axis built through the
    /// documented <c>IAuxiliaryGeomContainer.Axes3D.Add(o3d_axis2Points)</c> route, which the product
    /// already uses for <c>kompas_create_aux_geometry</c>. DOC: <c>iaxes3d_add.html</c>,
    /// <c>iaxis3dby2points_point1.html</c>, <c>ipoint3d_x.html</c>.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private object? DirectionAxis(ProbeStep step, bool alongY = false)
    {
        // MEASURED: a second axis through the same two points is not created, so the X axis is made once
        // and reused; the Y axis is made separately. History: docs/decisions/dismantle.md#probe
        if (!alongY && _xAxis is not null)
        {
            return _xAxis;
        }

        var document7 = Api5.SafeObject(() => _app.TransferInterface(_assembly5!, 2, 0)) as IKompasDocument3D;
        if (document7?.TopPart is not IAuxiliaryGeomContainer auxiliary
            || document7.TopPart is not IModelContainer model)
        {
            step.Observe("ось сборки: деталь не отвечает QI(IAuxiliaryGeomContainer/IModelContainer)");
            return null;
        }

        var first = Point3D(model, 0, 0, 0, step);
        var second = Point3D(model, alongY ? 0 : 100, alongY ? 100 : 0, 0, step);
        if (first is null || second is null)
        {
            return null;
        }

        try
        {
            if (auxiliary.Axes3D.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3D axis)
            {
                step.Observe("ось сборки: Axes3D.Add(o3d_axis2Points) не отдал IAxis3D");
                return null;
            }

            if (axis is IAxis3DBy2Points byTwoPoints)
            {
                byTwoPoints.Point1 = first;
                byTwoPoints.Point2 = second;
            }

            step.Data["axis_update"] = Api5.Raw(Api5.SafeBool(() => axis.Update()));
            if (!alongY)
            {
                _xAxis = axis;
            }

            return axis;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Observe("ось сборки: отказ " + HResult.Describe(ex));
            return null;
        }
    }

    private IPoint3D? Point3D(IModelContainer container, double x, double y, double z, ProbeStep step)
    {
        try
        {
            if (container.Points3D.Add() is not IPoint3D point)
            {
                step.Observe("точка оси: Points3D.Add() не отдал IPoint3D");
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            return Api5.SafeBool(() => point.Update()) == true ? point : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Observe("точка оси: отказ " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>Write ORDER is a property of the product, not a detail: a step filled in the other order
    /// tells whether the direction is dropped by the later writes.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private void OrderVariant(IDismantleParameters parameters, IPart7 target)
    {
        var step = _report.Begin("D.5b", "Другой порядок записи: направление, затем компоненты",
            "Сохраняется ли направление, если задать его первым?");
        if (Api5.SafeObject(() => parameters.AddDismantleStep()) is not IDismantleStep second)
        {
            step.Fail("Второй шаг не создан.");
            return;
        }

        var axis = DirectionAxis(step);
        step.Data["axis"] = Api5.RuntimeName(axis);
        if (axis is IModelObject model)
        {
            second.DirectionObject = model;
            step.Data["direction_after_write"] = Api5.RuntimeName(
                Api5.SafeObject(() => second.DirectionObject));
            step.Data["vector_after_direction"] = DescribeVector(Api5.SafeObject(() => second.Vector));
        }

        second.Parts = new object[] { target };
        second.Distance = StepDistance;
        step.Data["update"] = Api5.SafeInt(() => second.Update());
        _assembly5!.RebuildDocument();
        step.Data["direction_after_update"] = Api5.RuntimeName(
            Api5.SafeObject(() => second.DirectionObject));
        step.Data["vector_after_update"] = DescribeVector(Api5.SafeObject(() => second.Vector));
        step.Data["count"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        Api5.SafeObject(() => parameters.DeleteDismantleStep(1));
        step.Data["count_after_cleanup"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Unknown("Порядок записи записан; вывод — по данным шага.");
    }

    /// <summary>«Сначала один компонент, затем несколько»: one step addressing both components at once,
    /// along another axis and with another distance, so the measured deltas tell the steps apart.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private void MultiComponentStep(IDismantleParameters parameters, List<IPart7> components)
    {
        var step = _report.Begin("D.5c", "Шаг на НЕСКОЛЬКО компонентов",
            "Принимает ли шаг массив из двух компонентов?");
        if (Api5.SafeObject(() => parameters.AddDismantleStep()) is not IDismantleStep several)
        {
            step.Fail("Шаг не создан.");
            return;
        }

        var axis = DirectionAxis(step, alongY: true);
        step.Data["axis"] = Api5.RuntimeName(axis);
        several.Parts = components.Cast<object>().ToArray();
        step.Data["parts_written"] = components.Count;
        step.Data["parts_read"] = Api5.SafeObject(() => several.Parts) is Array array
            ? array.Length
            : 1;
        several.Distance = 25d;
        step.Data["distance"] = Api5.Raw(Api5.SafeDouble(() => several.Distance));
        if (axis is IModelObject model)
        {
            several.DirectionObject = model;
        }

        step.Data["update"] = Api5.SafeInt(() => several.Update());
        _assembly5!.RebuildDocument();
        step.Data["count"] = Api5.SafeInt(() => parameters.DismantleStepsCount);
        step.Unknown("Запись шага на два компонента записана; смещение измеряется в D.7.");
    }

    private IModelObject? DefaultPlane(int plane)
    {
        var part = _assembly5 is null ? null : Api5.SafeObject(() => _assembly5.GetPart(-1)) as ksPart;
        var entity = part is null ? null : Api5.SafeObject(() => part.GetDefaultEntity((short)plane));
        return entity is null ? null : Api5.SafeObject(() => _app.TransferInterface(entity, 2, 0)) as IModelObject;
    }

    private static double[]? Delta(double[]? a, double[]? b) =>
        a is null || b is null ? null : new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };

    /// <summary>The vector's components are read by name: the wrapper of this build does not publish
    /// X/Y/Z on the interface, so the property is looked up instead of guessed.
    /// History: docs/decisions/dismantle.md#probe</summary>
    private static string DescribeVector(object? vector)
    {
        if (vector is null)
        {
            return "null";
        }

        var axes = new[] { "X", "Y", "Z" }
            .Select(name => vector.GetType().GetProperty(name) is { } property
                ? Api5.Num(Api5.SafeDouble(() => Convert.ToDouble(property.GetValue(vector))))
                : "?")
            .ToList();
        return "(" + string.Join(", ", axes) + ") " + Api5.RuntimeName(vector);
    }

    /// <summary>The wrapper does not publish X/Y/Z on the interface, so the members the vector object
    /// actually carries are listed once instead of guessed. History: docs/decisions/dismantle.md#probe</summary>
    private static List<string> VectorMembers(object? vector)
    {
        if (vector is null)
        {
            return new List<string>();
        }

        var type = vector.GetType();
        return type.GetProperties().Select(p => p.Name + "="
            + Api5.Raw(Api5.SafeObject(() => p.GetValue(vector)))).ToList();
    }

    private static string Point(double[]? value) =>
        value is null ? "<нет>" : "(" + string.Join(", ", value.Select(v => Api5.Num(v))) + ")";
}
