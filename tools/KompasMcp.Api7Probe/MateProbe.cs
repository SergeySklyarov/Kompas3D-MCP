using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба M — сопряжения сборки: адресуются ли ГРАНИ КОМПОНЕНТОВ и создаётся ли сопряжение.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем эта проба.</b> Блок C1 (минимальные сборки) закрыт, и следующий блок — сопряжения.
/// Маршрут сопряжения ДОКУМЕНТИРОВАН и прочитан по проводу 05.10.2026:
/// <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c> →
/// <c>IMateConstraint3D</c> (<c>BaseObject1</c>, <c>BaseObject2</c>, <c>Alignment</c>,
/// <c>ParamValue</c>, <c>Fixed</c>, <c>Update()</c>). Не измерено другое: <c>BaseObject1/2</c>
/// имеют тип <c>IModelObject</c> и обязаны указывать на грани КОМПОНЕНТОВ, то есть адрес должен
/// вести в документ компонента, а не в саму сборку. Если грань компонента оттуда не достаётся,
/// блок придётся строить иначе — и узнать это надо ДО проектирования режимов.
/// </para>
/// <para>
/// <b>Только документированные вызовы.</b> Маршрут чтения грани — тот, который проект уже
/// применяет и который описан в справке: <c>ksPart.GetMainBody() → ksBody.FaceCollection()</c>,
/// а НЕ <c>EntityCollection(o3d_face)</c>. Передача в API7 — документированный
/// <c>TransferInterface(…, ksAPI7Dual, …)</c>. Составные части вставляются маршрутом, ИЗМЕРЕННЫМ
/// в блоке C1: <c>CreatePartInAssembly(file, plane)</c> для первого экземпляра и
/// <c>CopyPart(source, placement)</c> для повторного (повторная <c>CreatePartInAssembly</c> того же
/// файла возвращает null — измерено 04.10.2026).
/// </para>
/// <para>
/// <b>Что здесь доказательство.</b> Числа: число сопряжений до и после, <c>Valid</c> созданного
/// сопряжения, непустые <c>BaseObject1/2</c>, и — решающее — сместился ли компонент после
/// сопряжения. Сопряжение «создано» без смещения не отличает принятую связь от пустой записи.
/// </para>
/// </remarks>
internal sealed class MateProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly List<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private IApplication? _app7;
    private ksPart? _sourcePart;
    private int _ownPid;

    public MateProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Пишет отчёт изнутри прогона — для диагностического пути <c>--keep</c>.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "mate.json"),
            Path.Combine(options.ReportDir, "mate.md"));
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
            var source = BuildSourcePart();
            if (source is null)
            {
                return;
            }

            var assembly = BuildAssemblyWithTwoComponents(source.Value.Path);
            if (assembly is null)
            {
                return;
            }

            ReadComponents(assembly.Value.Document, assembly.Value.Part);

            var objects = ReadComponentFaceObjects(assembly.Value.Document, assembly.Value.SourcePart);
            if (objects is null)
            {
                return;
            }

            CreateMate(assembly.Value.Part, objects[0], objects[1]);
            Negative_SameObjectTwice(assembly.Value.Part, objects[0]);
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("M.X", "Необработанное исключение пробы M");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════ сеанс ══

    private void Launch()
    {
        var step = _report.Begin("M.0", "Свой невидимый экземпляр КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        _current = step;
        _clock.Restart();

        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));

        step.Observe("процессов KOMPAS.exe было до запуска: " + _pidsBefore.Count
            + (_ownPid == 0 ? "; новый процесс сразу не виден" : "; свой процесс " + _ownPid));

        try
        {
            _app7 = _app.GetType().InvokeMember(
                "ksGetApplication7", System.Reflection.BindingFlags.InvokeMethod, null, _app, null) as IApplication;
            step.Observe("ksGetApplication7 → " + (_app7 is null ? "null" : "IApplication"));
        }
        catch (Exception ex)
        {
            step.Observe("ksGetApplication7 бросил: " + ex.Message);
        }

        step.Pass("сеанс поднят за " + _clock.ElapsedMilliseconds + " мс");
    }

    private void Shutdown()
    {
        var step = _report.Begin("M.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        System.Threading.Thread.Sleep(1500);
        var left = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .Where(pid => !_pidsBefore.Contains(pid))
            .ToList();
        step.Data["new_processes_left"] = left;
        if (left.Count == 0)
        {
            step.Pass("новых процессов KOMPAS.exe не осталось");
        }
        else
        {
            step.Fail("остались процессы: " + string.Join(", ", left));
        }
    }

    // ═══════════════════════════════════════════════════════ деталь-источник ══

    private (string Path, ksPart Part)? BuildSourcePart()
    {
        var step = _report.Begin("M.1", "Деталь-источник: плита 100×80×10 на диске",
            "Есть ли файл, из которого вставлять компонент?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mate");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mate-source.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, true))
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        _sourcePart = part;
        part.name = "Mate-src";
        part.Update();

        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "mate-src") is null)
        {
            step.Fail("плита не построена.");
            return null;
        }

        var volume = Api5.Volume(part);
        step.Data["volume_mm3"] = volume;
        step.Data["analytic_mm3"] = PlateVolume;
        step.Observe("объём источника: " + Api5.Num(volume) + " (аналитика " + Api5.Num(PlateVolume) + ")");

        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true.");
            return null;
        }

        // Документ ЗАКРЫВАЕТСЯ: измерено 05.10.2026 — при открытом источнике CreatePartInAssembly
        // возвращает null. Грань компонента берётся не из этого документа, а поиском по точке
        // (IPart7.FindObjectsByPoint) уже в сборке.
        document.close();
        step.Observe("файл: " + path + "; документ-источник закрыт");
        step.Pass("источник сохранён");
        return (path, part);
    }

    // ═════════════════════════════════════════════════════════════ сборка ══

    private (ksDocument3D Document, ksPart Part, ksPart SourcePart)? BuildAssemblyWithTwoComponents(
        string sourcePath)
    {
        var step = _report.Begin("M.2", "Сборка с ДВУМЯ компонентами одной детали",
            "Сборка, в которой есть что сопрягать?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mate");
        var path = Path.Combine(directory, "mate-assembly.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, false))
        {
            step.Fail("Create(невидимый, сборка) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "Mate-asm";
        part.Update();

        // Плоскость приклейки обязательна (измерено в C1: null даёт GEOMETRY_FAILED).
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not { } plane)
        {
            step.Fail("GetDefaultEntity(o3d_planeXOY=1) не вернул плоскость.");
            return null;
        }

        object? first;
        try
        {
            first = document.CreatePartInAssembly(sourcePath, plane);
        }
        catch (Exception ex)
        {
            step.Fail("CreatePartInAssembly бросил: " + ex.Message);
            return null;
        }

        if (first is not ksPart firstPart)
        {
            step.Fail("CreatePartInAssembly не вернул ksPart (вернул "
                + (first is null ? "null" : first.GetType().FullName) + ").");
            return null;
        }

        step.Observe("первый экземпляр создан: ksPart");

        // ВТОРОЙ экземпляр ТОЙ ЖЕ детали: повторная CreatePartInAssembly того же файла возвращает
        // null (измерено 04.10.2026), документированный маршрут копии — CopyPart.
        object? copy;
        try
        {
            copy = document.CopyPart(firstPart, document.DefaultPlacement());
        }
        catch (Exception ex)
        {
            step.Fail("CopyPart бросил: " + ex.Message);
            return null;
        }

        if (copy is not ksPart)
        {
            step.Fail("CopyPart не вернул ksPart (вернул "
                + (copy is null ? "null" : copy.GetType().FullName) + ").");
            return null;
        }

        document.RebuildDocument();
        step.Observe("второй экземпляр создан через CopyPart");

        // Второй компонент СДВИГАЕТСЯ на известное расстояние: без этого грани совпадают, и
        // «объект по точке» не отличил бы один экземпляр от другого. Маршрут записи размещения
        // измерен в блоке C1 (ksDocument3D.DefaultPlacement → InitByMatrix3D → SetPlacement →
        // UpdatePlacement), раскладка массива — [X,0][Y,0][Z,0][перенос,1].
        var offset = new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            150, 0, 0, 1,
        };
        if (copy is ksPart secondPart && document.DefaultPlacement() is ksPlacement placement)
        {
            placement.InitByMatrix3D(offset);
            secondPart.SetPlacement(placement);
            secondPart.UpdatePlacement();
            document.RebuildDocument();
            step.Observe("второй компонент сдвинут на 150 мм по X");
        }
        else
        {
            step.Observe("размещение второго компонента НЕ записано (DefaultPlacement не ksPlacement)");
        }

        var count = CountComponents(document);
        step.Data["components"] = count;
        if (count != 2)
        {
            step.Fail("компонентов " + count + ", ожидалось 2.");
            return null;
        }

        if (document.SaveAs(path) != true)
        {
            step.Observe("SaveAs не дал true — продолжаем без сохранения: сопряжение мерится на живом документе.");
        }

        step.Pass("сборка с двумя компонентами готова");
        return (document, part, _sourcePart!);
    }

    private static int CountComponents(ksDocument3D document)
    {
        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                return -1;
            }

            return collection.GetCount();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void ReadComponents(ksDocument3D document, ksPart assemblyPart)
    {
        var step = _report.Begin("M.3", "Компоненты сборки читаются по порядку",
            "Достаётся ли ksPart каждого компонента?");
        _current = step;

        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                step.Fail("PartCollection(true) не вернул ksPartCollection.");
                return;
            }

            var names = new List<string>();
            for (var index = 0; index < collection.GetCount(); index++)
            {
                if (collection.GetByIndex(index) is ksPart component)
                {
                    names.Add(index + ":" + (Api5.SafeObject(() => component.name) ?? "—"));
                }
                else
                {
                    names.Add(index + ":не ksPart");
                }
            }

            step.Data["components"] = names;
            step.Observe("компоненты: " + string.Join(", ", names));
            step.Pass("перечислены " + names.Count + " компонент(ов)");
        }
        catch (Exception ex)
        {
            step.Fail("перечисление бросило: " + ex.Message);
        }
    }

    // ═════════════════════════════════════════════ РЕШАЮЩИЙ ОПЫТ: грани компонентов ══

    private List<IModelObject>? ReadComponentFaceObjects(ksDocument3D document, ksPart sourcePart)
    {
        var step = _report.Begin("M.4", "Грань компонента как IModelObject — поиск по точке",
            "Находятся ли грани ДВУХ компонентов сборки документированным IPart7.FindObjectsByPoint?");
        _current = step;

        try
        {
            // ИЗМЕРЕНО 05.10.2026: ksPart.GetMainBody() у КОМПОНЕНТА сборки даёт null (при
            // LoadState=ksLCompletely и Load(true)=True), поэтому грань ищется не через тело, а
            // документированным поиском объекта по точке — в системе САМОЙ СБОРКИ.
            var top = _app7?.ActiveDocument as IKompasDocument3D;
            var topPart = top?.TopPart;
            if (topPart is null)
            {
                step.Fail("активный документ не дал IKompasDocument3D.TopPart (Part7).");
                return null;
            }

            step.Observe("верхняя часть сборки получена как Part7: да");

            // Точки — НЕ центры, а четверть верхней грани плит. ДЕФЕКТ ПРОБЫ, ПОЙМАННЫЙ ПРОГОНОМ:
            // точка (0, 0, 10) лежит НА базовых плоскостях сборки, и FindObjectsByPoint вернул
            // именно их — «Плоскость ZX» (o3d_planeXOZ) и «Плоскость ZY» (o3d_planeYOZ), а не грань
            // компонента. Плита 100×80×10 выдавлена в +Z, поэтому верхняя грань на z = 10 и
            // занимает x ∈ [−50, 50], y ∈ [−40, 40]; берём (25, 20, 10) и, у сдвинутого на 150 мм
            // второго компонента, (175, 20, 10) — обе точки вне обеих плоскостей.
            var probes = new (string Label, double X, double Y, double Z)[]
            {
                ("компонент 1", 25d, 20d, 10d),
                ("компонент 2", 175d, 20d, 10d),
            };

            // ДЕФЕКТ ПРОБЫ, ПОЙМАННЫЙ ПЕРВЫМ ПРОГОНОМ ЭТОЙ РЕДАКЦИИ: точки дали 2 и 1 объект, а
            // сопряжение было построено на ПЕРВЫХ ДВУХ из общего списка — то есть на двух объектах
            // ОДНОГО компонента, и такое сопряжение Valid=false. Это тот же класс, что MANIA.17:
            // «различающая пара» измерила один и тот же узел. Поэтому теперь берётся ровно по
            // одному объекту С КАЖДОЙ точки, а состав пары называется вслух.
            // ГДЕ ТЕЛО ПО Z — измеряется сканом, а не предполагается: первая редакция считала, что
            // плита занимает z ∈ [0, 10], и точка z = 10 не нашла ничего. Скан называет числа.
            // FirstLevel — документированный параметр, и его значение решает, видна ли геометрия
            // КОМПОНЕНТА: при true находились только базовые плоскости сборки. Проверяются ОБА.
            foreach (var firstLevel in new[] { true, false })
            {
                foreach (var z in new[] { -5d, 0d, 5d, 10d })
                {
                    var level = firstLevel;
                    var zz = z;
                    var rawScan = Api5.SafeObject(() => topPart.FindObjectsByPoint(25d, 20d, zz, level));
                    var foundScan = AsModelObjects(rawScan);
                    step.Observe("скан FirstLevel=" + level + " (25, 20, "
                        + zz.ToString(CultureInfo.InvariantCulture) + ") → "
                        + (rawScan is null ? "null" : "объектов " + foundScan.Count + ": "
                            + string.Join(", ", foundScan.Select(o => Api5.SafeEnum(() => o.ModelObjectType).ToString()))));
                }
            }

            // ГЕОМЕТРИЯ КОМПОНЕНТА ищется НА САМОМ КОМПОНЕНТЕ (его IPart7) в ЛОКАЛЬНЫХ координатах:
            // поиск по верхней части сборки находил только её базовые плоскости. Параметр
            // FirstLevel проверяется оба значения, как и раньше.
            var componentParts = new List<IPart7>();
            for (var index = 0; index < 2; index++)
            {
                if (ComponentByIndex(document, index) is { } component7)
                {
                    componentParts.Add(component7);
                }
            }

            step.Observe("компонентов как IPart7: " + componentParts.Count);
            foreach (var component7 in componentParts)
            {
                foreach (var firstLevel in new[] { true, false })
                {
                    foreach (var z in new[] { 0d, 5d, 10d })
                    {
                        var level = firstLevel;
                        var zz = z;
                        var rawScan = Api5.SafeObject(() => component7.FindObjectsByPoint(25d, 20d, zz, level));
                        var foundScan = AsModelObjects(rawScan);
                        step.Observe("скан на КОМПОНЕНТЕ FirstLevel=" + level + " (25, 20, "
                            + zz.ToString(CultureInfo.InvariantCulture) + ") → "
                            + (rawScan is null ? "null" : "объектов " + foundScan.Count + ": "
                                + string.Join(", ", foundScan.Select(o => Api5.SafeEnum(() => o.ModelObjectType).ToString()))));
                    }
                }
            }

            var perProbe = new List<List<IModelObject>>();
            foreach (var probe in probes)
            {
                var raw = Api5.SafeObject(() => topPart.FindObjectsByPoint(probe.X, probe.Y, probe.Z, false));
                var found = AsModelObjects(raw);
                perProbe.Add(found);
                step.Observe(probe.Label + ": FindObjectsByPoint("
                    + probe.X.ToString(CultureInfo.InvariantCulture) + ", "
                    + probe.Y.ToString(CultureInfo.InvariantCulture) + ", "
                    + probe.Z.ToString(CultureInfo.InvariantCulture) + ") → "
                    + (raw is null ? "null" : raw.GetType().Name + ", объектов " + found.Count));
                // ЧТО именно найдено — называется вслух: без типа объекта «сопряжение не Valid»
                // неотличимо от «в BaseObject ушло не то».
                foreach (var item in found)
                {
                    step.Observe("   " + probe.Label + " объект: тип="
                        + Api5.SafeEnum(() => item.ModelObjectType)
                        + ", имя='" + (Api5.SafeObject(() => item.Name) ?? "—") + "'");
                }
            }

            var objects = new List<IModelObject>();
            foreach (var found in perProbe)
            {
                if (found.Count > 0)
                {
                    objects.Add(found[0]);
                }
            }

            step.Data["per_probe"] = perProbe.Select(f => f.Count).ToArray();
            step.Data["objects"] = objects.Count;
            step.Observe("в сопряжение пойдут объекты: " + objects.Count
                + " (по одному с каждой точки, не «первые два из списка»)");
            if (objects.Count < 2)
            {
                step.Fail("с разных точек собрано объектов: " + objects.Count + ", нужно минимум 2.");
                return null;
            }

            step.Pass("по одному объекту с каждой из " + objects.Count + " точек");
            return objects;
        }
        catch (Exception ex)
        {
            step.Fail("поиск по точке бросил: " + ex.Message);
            return null;
        }
    }

    /// <summary>Компонент по порядковому номеру как <c>IPart7</c> (адрес измерен в блоке C1).</summary>
    private IPart7? ComponentByIndex(ksDocument3D document, int index)
    {
        try
        {
            return document.PartCollection(true) is ksPartCollection collection
                && collection.GetByIndex(index) is ksPart part
                ? TransferTo7(part) as IPart7
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Приводит результат <c>FindObjectsByPoint</c> к списку <c>IModelObject</c>.</summary>
    /// <remarks>
    /// Документация не фиксирует форму ответа: это может быть один объект, SAFEARRAY или массив
    /// <c>object[]</c>. Разбираются все три случая, а неожиданная форма НАЗЫВАЕТСЯ, а не
    /// проглатывается.
    /// </remarks>
    private List<IModelObject> AsModelObjects(object? raw)
    {
        var result = new List<IModelObject>();
        switch (raw)
        {
            case null:
                return result;
            case IModelObject single:
                result.Add(single);
                return result;
            case System.Array array:
                foreach (var item in array)
                {
                    if (item is IModelObject model)
                    {
                        result.Add(model);
                    }
                }

                return result;
            default:
                _report.Note(_current?.Id ?? "M", "FindObjectsByPoint вернул неожиданную форму: "
                    + raw.GetType().FullName);
                return result;
        }
    }

    /// <summary>
    /// Запасной документированный путь: открыть документ-ИСТОЧНИК компонента
    /// (<c>IPart7.OpenSourceDocument</c>) и прочитать тело там.
    /// </summary>
    /// <remarks>
    /// Оставлен диагностическим: если <c>Load(true)</c> уже дал тело, сюда не заходим. Если не дал —
    /// этот шаг НАЗЫВАЕТ, что именно вернул <c>OpenSourceDocument</c>, вместо молчаливого «не
    /// получилось»: следующая правка должна опираться на наблюдение, а не на догадку.
    /// </remarks>
    private ksBody? BodyFromSourceDocument(ksPart component, int index, ProbeStep step)
    {
        try
        {
            if (TransferTo7(component) is not IPart7 component7)
            {
                step.Observe("компонент " + index + ": документ-источник не открывается — нет IPart7");
                return null;
            }

            var parameters = component7.GetOpenDocumentParam();
            if (parameters is null)
            {
                step.Observe("компонент " + index + ": GetOpenDocumentParam() вернул null");
                return null;
            }

            var source = component7.OpenSourceDocument(parameters);
            step.Observe("компонент " + index + ": OpenSourceDocument → "
                + (source is null ? "null" : source.GetType().FullName));
            return null;
        }
        catch (Exception ex)
        {
            step.Observe("компонент " + index + ": OpenSourceDocument бросил "
                + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════ РЕШАЮЩИЙ ОПЫТ: сопряжение ══

    private void CreateMate(ksPart assemblyPart, IModelObject first, IModelObject second)
    {
        var step = _report.Begin("M.5", "Сопряжение создаётся через IPart7.MateConstraints.Add",
            "Add(MateConstraintType) + BaseObject1/2 + Update() дают ЖИВОЕ сопряжение?");
        _current = step;

        try
        {
            var part7 = TransferTo7(assemblyPart) as IPart7;
            if (part7 is null)
            {
                step.Fail("сборка не перенеслась в API7 как IPart7.");
                return;
            }

            var mates = part7.MateConstraints;
            if (mates is null)
            {
                step.Fail("IPart7.MateConstraints вернул null.");
                return;
            }

            var before = Api5.SafeInt(() => mates.Count);
            step.Data["count_before"] = before;
            step.Observe("сопряжений до: " + before);

            IMateConstraint3D mate;
            try
            {
                mate = mates.Add(MateConstraintType.mc_Coincidence);
            }
            catch (Exception ex)
            {
                step.Fail("Add(mc_Coincidence) бросил: " + ex.Message);
                return;
            }

            if (mate is null)
            {
                step.Fail("Add(mc_Coincidence) вернул null.");
                return;
            }

            step.Observe("сопряжение создано: ConstraintType=" + Api5.SafeEnum(() => mate.ConstraintType));

            // Объекты — грани РАЗНЫХ компонентов. Пара «грань1 ↔ грань2» и есть предмет проверки:
            // если адрес ведёт не в компонент, а в сборку, объект не примется.
            try
            {
                mate.BaseObject1 = first;
                mate.BaseObject2 = second;
                mate.Alignment = ksMateConstraintAlignmentEnum.ksMCAlignmentOpposite;
            }
            catch (Exception ex)
            {
                step.Fail("запись BaseObject1/2 или Alignment бросила: " + ex.Message);
                return;
            }

            step.Data["base1_set"] = Api5.SafeObject(() => mate.BaseObject1) is not null;
            step.Data["base2_set"] = Api5.SafeObject(() => mate.BaseObject2) is not null;
            step.Observe("BaseObject1 задан: " + step.Data["base1_set"]
                + ", BaseObject2 задан: " + step.Data["base2_set"]);

            var updated = false;
            try
            {
                updated = mate.Update();
            }
            catch (Exception ex)
            {
                step.Fail("Update() бросил: " + ex.Message);
                return;
            }

            var after = Api5.SafeInt(() => mates.Count);
            step.Data["update"] = updated;
            step.Data["count_after"] = after;
            step.Data["valid"] = Api5.SafeBool(() => mate.Valid);
            step.Observe("Update()=" + updated + ", сопряжений после: " + after
                + ", Valid=" + step.Data["valid"]);

            // ПОДТВЕРЖДЕНИЕ — это Valid, а не рост счётчика. Измерено 05.10.2026: сопряжение с
            // двумя объектами ОДНОГО компонента тоже дало Update()=true и счётчик 0 → 1, но
            // Valid=false. Значит «Update()=true» доказательством не является, и ослаблять это
            // утверждение под наблюдённый результат запрещено.
            var valid = Api5.SafeBool(() => mate.Valid);
            if (updated && valid == true)
            {
                step.Pass("сопряжение подтверждено: Valid=true, сопряжений " + before + " → " + after);
            }
            else
            {
                step.Fail("сопряжение НЕ подтверждено: Update()=" + updated + ", Valid=" + valid
                    + ", сопряжений " + before + " → " + after
                    + ". Запись создана, но недействительна — это факт о продукте, а не о пробе.");
            }
        }
        catch (Exception ex)
        {
            step.Fail("сопряжение бросило: " + ex.Message);
        }
    }

    /// <summary>
    /// Отрицательный контроль: один и тот же объект в обе позиции. Сопряжение грани с самой собой
    /// либо отвергается, либо не даёт Valid — но не должно молча считаться успешным.
    /// </summary>
    private void Negative_SameObjectTwice(ksPart assemblyPart, IModelObject only)
    {
        var step = _report.Begin("M.6", "Отрицательный контроль: один объект в обе позиции",
            "Сопряжение объекта с самим собой отвергается, а не считается успешным?");
        _current = step;

        try
        {
            if (TransferTo7(assemblyPart) is not IPart7 part7 || part7.MateConstraints is not { } mates)
            {
                step.Fail("сборка не перенеслась в API7 как IPart7.");
                return;
            }

            var before = Api5.SafeInt(() => mates.Count);
            IMateConstraint3D mate;
            try
            {
                mate = mates.Add(MateConstraintType.mc_Coincidence);
                mate.BaseObject1 = only;
                mate.BaseObject2 = only;
            }
            catch (Exception ex)
            {
                step.Observe("запись отвергнута на этапе присваивания: " + ex.Message);
                step.Pass("отказ назван на присваивании");
                return;
            }

            bool updated;
            try
            {
                updated = mate.Update();
            }
            catch (Exception ex)
            {
                step.Observe("Update() бросил: " + ex.Message);
                step.Pass("отказ назван на Update()");
                return;
            }

            var valid = Api5.SafeBool(() => mate.Valid);
            var after = Api5.SafeInt(() => mates.Count);
            step.Data["update"] = updated;
            step.Data["valid"] = valid;
            step.Data["count_before"] = before;
            step.Data["count_after"] = after;
            step.Observe("Update()=" + updated + ", Valid=" + valid + ", сопряжений " + before + " → " + after);

            // Честный вердикт: если продукт принял вырожденное сопряжение — это НАЗЫВАЕТСЯ, а не
            // выдаётся за наш провал. Проба измеряет, а не желает.
            if (updated && valid == true)
            {
                step.Fail("вырожденное сопряжение принято как Valid — это факт о продукте, а не о пробе");
            }
            else
            {
                step.Pass("вырожденное сопряжение не подтверждено (Update()=" + updated + ", Valid=" + valid + ")");
            }
        }
        catch (Exception ex)
        {
            step.Fail("отрицательный контроль бросил: " + ex.Message);
        }
    }

    // ═════════════════════════════════════════════════════════════════ мост ══

    /// <summary>Переносит объект API5 в API7 как <c>Part7</c> (тип аргумента <c>FindObject</c>).</summary>
    private Part7? TransferPart7(object? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as Part7;
        }
        catch (Exception ex)
        {
            _report.Note(_current?.Id ?? "M", "TransferInterface(→ Part7) бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Переносит объект API5 в API7 как <c>IModelObject</c>, либо null с причиной.</summary>
    private IModelObject? TransferTo7(object? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as IModelObject;
        }
        catch (Exception ex)
        {
            _report.Note(_current?.Id ?? "M", "TransferInterface(→ API7) бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private ProbeStep? _current;
}
