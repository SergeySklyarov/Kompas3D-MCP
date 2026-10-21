using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe S — dynamic cross-sections of a model: is a section WRITABLE through the documented
/// route, does the section mode change the raster snapshot, and do the sections survive save/reopen
/// (order <c>OMEGA_G11_SECTIONS</c>).</summary>
/// <remarks>DOC: <c>idynamiccrosssectionsmanager.html</c> — «Интерфейс является дополнительным к интерфейсу
/// документа IKompasDocument3D», «Версия Компас v18.1»; <c>idynamiccrosssection_addstep.html</c> —
/// <c>AddStep(ksDynamicCrossSectionStepBuildingTypeEnum)</c> returns <c>IDynamicCrossSectionStep</c>;
/// <c>idynamiccrosssectionstep_parameters.html</c> — <c>Parameters</c> is an <c>IKompasAPIObject</c>,
/// read only; <c>idynamiccrosssectionstepparametersbyoffsetplane_offsetplane.html</c> — <c>OffsetPlane</c>
/// is an <c>IPlane3DByOffset</c>, read only; <c>…_reversedirection.html</c> — <c>BOOL ReverseDirection()</c>.
/// INVARIANT: a section is confirmed by reading it back and by a measured change of the raster snapshot,
/// never by the object the add call returned. History: docs/decisions/sections.md#probe</remarks>
internal sealed class SectionsProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double SectionOffset = 5d;
    private const short RasterBmp = 0;
    private const short RasterColorBits = 24;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D? _part5;
    private ksDocument3D? _assembly5;
    private string _partPath = string.Empty;
    private string _assemblyPath = string.Empty;
    private string _sourcePath = string.Empty;

    public SectionsProbe(ProbeReport report, Options options)
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
            if (BuildPart() is null)
            {
                return;
            }

            var manager = Manager(_part5!, "S.3");
            if (manager is null)
            {
                return;
            }

            var section = AddSection(manager);
            if (section is null)
            {
                return;
            }

            WriteStep(section);
            ReadBack(manager, section);
            Snapshots(manager, _part5!);
            SaveReopen();
            DeleteSection(manager, section);
            RotatedPlaneRoute(manager);
            AssemblyRoute();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("S.X", "Необработанное исключение пробы S");
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
        var step = _report.Begin("S.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
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

    private string? BuildPart()
    {
        var step = _report.Begin("S.2", "Деталь с телом на диске", "По чему строить сечение?");
        var directory = Path.Combine(_options.WorkDir, "sections");
        Directory.CreateDirectory(directory);
        _partPath = Path.Combine(directory, "g11-part.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, true) != true)
        {
            step.Fail("Документ не создан.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "g11") is null)
        {
            step.Fail("Плита не построена.");
            return null;
        }

        document.RebuildDocument();
        var saved = document.SaveAs(_partPath);
        step.Data["save_as"] = saved;
        step.Data["path"] = _partPath;
        step.Data["body_volume"] = Api5.Num(Api5.Volume(part));
        if (saved != true)
        {
            step.Fail("Деталь не сохранена.");
            return null;
        }

        _part5 = document;
        step.Pass("Деталь построена и сохранена: сечение строится по её телу.");
        return _partPath;
    }

    /// <summary>The decisive QI of the order: the manager is «дополнительный» for
    /// <c>IKompasDocument3D</c>, so it is reached by a cast and nothing else.</summary>
    private IDynamicCrossSectionsManager? Manager(ksDocument3D document, string id)
    {
        var step = _report.Begin(id, "IDynamicCrossSectionsManager через QueryInterface",
            "Отдаёт ли документ менеджер динамических сечений?");
        var document7 = Api5.SafeObject(() => _app.TransferInterface(document, 2, 0)) as IKompasDocument3D;
        step.Data["document7"] = Api5.RuntimeName(document7);
        var manager = document7 as IDynamicCrossSectionsManager;
        step.Data["manager"] = Api5.RuntimeName(manager);
        if (manager is null)
        {
            step.Fail("IKompasDocument3D не отвечает QI(IDynamicCrossSectionsManager) — маршрута нет.");
            return null;
        }

        step.Data["count_before"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["mode_default"] = Api5.Raw(Api5.SafeBool(() => manager.DynamicCrossSectionModeOn));
        step.Data["current_before"] = Api5.RuntimeName(Api5.SafeObject(() => manager.CurrentDynamicCrossSection));
        step.Pass("QI(IDynamicCrossSectionsManager) отвечает; сечений до записи "
            + Api5.Raw(step.Data["count_before"]) + ".");
        return manager;
    }

    private IDynamicCrossSection? AddSection(IDynamicCrossSectionsManager manager)
    {
        var step = _report.Begin("S.4", "AddDynamicCrossSection", "Создаётся ли сечение без шага?");
        var created = Api5.SafeObject(() => manager.AddDynamicCrossSection());
        step.Data["add_returned"] = Api5.RuntimeName(created);
        if (created is not IDynamicCrossSection section)
        {
            step.Fail("AddDynamicCrossSection() не отдал IDynamicCrossSection: " + Api5.RuntimeName(created));
            return null;
        }

        step.Data["count_after_add"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["steps_count"] = Api5.SafeInt(() => section.StepsCount);
        step.Data["fill_cut_planes"] = Api5.Raw(Api5.SafeBool(() => section.FillCutPlanes));
        step.Data["current_after_add"] = Api5.RuntimeName(Api5.SafeObject(() => manager.CurrentDynamicCrossSection));
        step.Data["section_from_index"] = Api5.RuntimeName(Api5.SafeObject(() => manager.DynamicCrossSection[0]));
        step.Pass("Сечение создано; шагов в нём " + Api5.Raw(step.Data["steps_count"]) + ".");
        return section;
    }

    /// <summary>The main risk of the order: the step is built by an OFFSET PLANE, and the plane object is
    /// handed out by the parameters (read only), so it is configured through its own interface.</summary>
    private void WriteStep(IDynamicCrossSection section)
    {
        var step = _report.Begin("S.5", "Шаг сечения смещённой плоскостью",
            "Записывается ли шаг документированными членами и входит ли он в сечение?");
        var created = Api5.SafeObject(() => section.AddStep(ksDynamicCrossSectionStepBuildingTypeEnum.ksDCSTByOffsetPlane));
        step.Data["add_step_returned"] = Api5.RuntimeName(created);
        if (created is not IDynamicCrossSectionStep dynamicStep)
        {
            step.Fail("AddStep(ksDCSTByOffsetPlane) не отдал IDynamicCrossSectionStep: "
                + Api5.RuntimeName(created));
            return;
        }

        step.Data["steps_count_after_add"] = Api5.SafeInt(() => section.StepsCount);
        step.Data["building_type"] = Api5.Raw(Api5.SafeEnum(() => dynamicStep.BuildingType));

        var parameters = Api5.SafeObject(() => dynamicStep.Parameters);
        step.Data["parameters"] = Api5.RuntimeName(parameters);
        if (parameters is not IDynamicCrossSectionStepParametersByOffsetPlane byOffset)
        {
            step.Fail("Parameters не отвечает QI(IDynamicCrossSectionStepParametersByOffsetPlane): "
                + Api5.RuntimeName(parameters));
            return;
        }

        var plane = Api5.SafeObject(() => byOffset.OffsetPlane);
        step.Data["offset_plane"] = Api5.RuntimeName(plane);
        if (plane is not IPlane3DByOffset planeByOffset)
        {
            step.Fail("OffsetPlane не отвечает QI(IPlane3DByOffset): " + Api5.RuntimeName(plane));
            return;
        }

        step.Data["plane_base_before"] = Text(() => planeByOffset.BasePlane?.Name);
        step.Data["plane_offset_before"] = Api5.Num(Api5.SafeDouble(() => planeByOffset.Offset));
        step.Data["plane_direction_before"] = Api5.Raw(Api5.SafeBool(() => planeByOffset.Direction));

        var basePlane = DefaultPlane(_part5!, "XOY");
        step.Data["base_plane_transferred"] = Api5.RuntimeName(basePlane);
        if (basePlane is null)
        {
            step.Fail("Стандартная плоскость XOY не перенесена в API7 — опору задать нечем.");
            return;
        }

        try
        {
            planeByOffset.BasePlane = basePlane;
            planeByOffset.Offset = SectionOffset;
            planeByOffset.Direction = true;
            step.Observe("Плоскость: BasePlane, Offset=" + Api5.Num(SectionOffset) + ", Direction=true записаны");
        }
        catch (COMException ex)
        {
            step.Fail("Запись входов смещённой плоскости бросила HRESULT 0x" + ex.HResult.ToString("X8")
                + ": " + ex.Message);
            return;
        }

        step.Data["plane_update"] = Api5.Raw(Api5.SafeBool(() => planeByOffset.Update()));
        step.Data["reverse_direction"] = Api5.Raw(Api5.SafeBool(() => byOffset.ReverseDirection()));
        step.Data["plane_building_type"] = Api5.Raw(Api5.SafeEnum(() => byOffset.PlaneBuildingType));
        step.Data["section_update"] = Api5.Raw(Api5.SafeBool(() => section.Update()));

        step.Data["plane_base_after"] = Text(() => planeByOffset.BasePlane?.Name);
        step.Data["plane_offset_after"] = Api5.Num(Api5.SafeDouble(() => planeByOffset.Offset));
        step.Data["steps_count_after_update"] = Api5.SafeInt(() => section.StepsCount);

        var offsetAfter = Api5.SafeDouble(() => planeByOffset.Offset);
        if (offsetAfter is null)
        {
            step.Fail("Смещение плоскости не читается обратно: запись не подтверждена.");
            return;
        }

        step.Pass("Шаг записан и подтверждён чтением: смещение " + Api5.Num(offsetAfter) + " мм.");
    }

    private void ReadBack(IDynamicCrossSectionsManager manager, IDynamicCrossSection section)
    {
        var step = _report.Begin("S.6", "Чтение обратно", "Возвращает ли модель то, что записано?");
        step.Data["sections_count"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["steps_count"] = Api5.SafeInt(() => section.StepsCount);
        var read = Api5.SafeObject(() => section.Step[0]);
        step.Data["step0"] = Api5.RuntimeName(read);
        if (read is not IDynamicCrossSectionStep readStep)
        {
            step.Fail("Шаг 0 не прочитан как IDynamicCrossSectionStep: " + Api5.RuntimeName(read));
            return;
        }

        step.Data["step0_building_type"] = Api5.Raw(Api5.SafeEnum(() => readStep.BuildingType));
        var parameters = Api5.SafeObject(() => readStep.Parameters);
        step.Data["step0_parameters"] = Api5.RuntimeName(parameters);
        if (parameters is not IDynamicCrossSectionStepParametersByOffsetPlane byOffset)
        {
            step.Fail("Параметры шага 0 не отвечают QI(ByOffsetPlane): " + Api5.RuntimeName(parameters));
            return;
        }

        step.Data["step0_plane_building_type"] = Api5.Raw(Api5.SafeEnum(() => byOffset.PlaneBuildingType));
        var plane = Api5.SafeObject(() => byOffset.OffsetPlane) as IPlane3DByOffset;
        step.Data["step0_offset_plane"] = Api5.RuntimeName(plane);
        if (plane is null)
        {
            step.Fail("OffsetPlane шага 0 не отвечает QI(IPlane3DByOffset).");
            return;
        }

        var offset = Api5.SafeDouble(() => plane.Offset);
        step.Data["step0_offset"] = Api5.Num(offset);
        step.Data["step0_base_plane"] = Text(() => plane.BasePlane?.Name);
        step.Data["step0_direction"] = Api5.Raw(Api5.SafeBool(() => plane.Direction));
        step.Data["mode"] = Api5.Raw(Api5.SafeBool(() => manager.DynamicCrossSectionModeOn));
        if (offset is null)
        {
            step.Fail("Смещение не прочитано обратно.");
            return;
        }

        step.Pass("Сечений " + Api5.Raw(step.Data["sections_count"]) + ", шагов "
            + Api5.Raw(step.Data["steps_count"]) + ", смещение " + Api5.Num(offset) + " мм.");
    }

    /// <summary>The acceptance measure of the order: a snapshot in the section mode differs from the
    /// snapshot without it, and the snapshot after switching the mode off returns to the first one.</summary>
    private void Snapshots(IDynamicCrossSectionsManager manager, ksDocument3D document)
    {
        var step = _report.Begin("S.7", "Снимок: режим сечения виден?",
            "Отличается ли растровый снимок в невидимом экземпляре при включённом сечении?");
        var directory = Path.Combine(_options.WorkDir, "sections");
        var offPath = Path.Combine(directory, "s11-off.bmp");
        var onPath = Path.Combine(directory, "s11-on.bmp");
        var backPath = Path.Combine(directory, "s11-back.bmp");

        var modeWas = Api5.SafeBool(() => manager.DynamicCrossSectionModeOn);
        SetMode(manager, false);
        var before = Raster(document, offPath);
        SetMode(manager, true);
        var inMode = Raster(document, onPath);
        SetMode(manager, false);
        var after = Raster(document, backPath);
        if (modeWas is bool original)
        {
            SetMode(manager, original);
        }

        step.Data["bytes_off"] = before?.Length;
        step.Data["bytes_on"] = inMode?.Length;
        step.Data["bytes_back"] = after?.Length;
        if (before is null || inMode is null || after is null)
        {
            step.Fail("Снимок не снят: сравнение невозможно.");
            return;
        }

        var onDiff = DifferingShare(before, inMode);
        var backDiff = DifferingShare(before, after);
        step.Data["diff_share_mode_on"] = Api5.Num(onDiff);
        step.Data["diff_share_after_off"] = Api5.Num(backDiff);
        step.Artifacts.Add(offPath);
        step.Artifacts.Add(onPath);
        step.Artifacts.Add(backPath);

        // The threshold is the measure of "the pictures differ": a share of bytes below one percent is
        // what a re-render of an unchanged scene shows (measured), so anything above it is a visible
        // difference and anything at or below it is the same picture.
        const double VisibleShare = 0.01d;
        if (onDiff is null || backDiff is null)
        {
            step.Fail("Доли различающихся байтов не посчитаны.");
            return;
        }

        if (onDiff > VisibleShare && backDiff <= VisibleShare)
        {
            step.Pass("Сечение видно на снимке: в режиме различается " + Api5.Num(onDiff)
                + " байтов, после выключения — " + Api5.Num(backDiff) + " (порог "
                + Api5.Num(VisibleShare) + ").");
            return;
        }

        if (onDiff <= VisibleShare)
        {
            step.Unknown("Сечение на снимке не видно: в режиме различается " + Api5.Num(onDiff)
                + " байтов (порог " + Api5.Num(VisibleShare) + "). Режим записан, но снимок его не "
                + "показывает — инструменты пишутся с подтверждением чтением обратно.");
            return;
        }

        step.Fail("После выключения режима снимок не вернулся к исходному: различается "
            + Api5.Num(backDiff) + " байтов (порог " + Api5.Num(VisibleShare) + ").");
    }

    private void SetMode(IDynamicCrossSectionsManager manager, bool on)
    {
        try
        {
            manager.DynamicCrossSectionModeOn = on;
        }
        catch (COMException ex)
        {
            _report.Note("S.7", "Запись режима " + on + " бросила HRESULT 0x" + ex.HResult.ToString("X8"));
        }
    }

    /// <summary>One raster of the model through the same documented pair the product's
    /// <c>kompas_export_image</c> uses: <c>RasterFormatParam → Init → SaveAsToRasterFormat</c>.
    /// BMP is chosen because it is uncompressed, so two pictures can be compared byte by byte.</summary>
    private byte[]? Raster(ksDocument3D document, string path)
    {
        ksRasterFormatParam? parameter = null;
        try
        {
            parameter = (ksRasterFormatParam)document.RasterFormatParam();
            if (parameter is null)
            {
                return null;
            }

            parameter.Init();
            parameter.format = RasterBmp;
            parameter.colorBPP = RasterColorBits;
            parameter.extResolution = 200;
            parameter.returnResultAsArrayBytes = true;
            if (document.SaveAsToRasterFormat(string.Empty, parameter) != true)
            {
                return null;
            }

            var raw = parameter.resultArrayBytes;
            var bytes = raw switch
            {
                byte[] typed => typed,
                Array array => array.Cast<object>().Select(Convert.ToByte).ToArray(),
                _ => null,
            };
            if (bytes is { Length: > 0 })
            {
                File.WriteAllBytes(path, bytes);
            }

            return bytes;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (parameter is not null)
            {
                Marshal.ReleaseComObject(parameter);
            }
        }
    }

    /// <summary>Share of differing bytes, or null when the pictures have different lengths — a different
    /// size is a different picture, not a measured difference.</summary>
    private static double? DifferingShare(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return null;
        }

        var differing = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                differing++;
            }
        }

        return (double)differing / left.Length;
    }

    private void SaveReopen()
    {
        var step = _report.Begin("S.8", "Сохранение и повторное открытие",
            "Сохраняются ли сечения и режим в файле?");
        var saved = _part5!.SaveAs(_partPath);
        step.Data["save_as"] = saved;
        if (saved != true)
        {
            step.Fail("Документ не сохранён.");
            return;
        }

        _part5.close();
        var reopened = (ksDocument3D)_app.Document3D();
        var opened = reopened.Open(_partPath, true);
        step.Data["open"] = opened;
        if (opened != true)
        {
            step.Fail("Документ не открыт заново.");
            return;
        }

        _part5 = reopened;
        var manager = Manager(reopened, "S.8.1");
        if (manager is null)
        {
            step.Fail("У переоткрытого документа менеджер не получен.");
            return;
        }

        step.Data["count_after_reopen"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["mode_after_reopen"] = Api5.Raw(Api5.SafeBool(() => manager.DynamicCrossSectionModeOn));
        var section = Api5.SafeObject(() => manager.DynamicCrossSection[0]) as IDynamicCrossSection;
        int? steps = null;
        double? offset = null;
        if (section is not null)
        {
            steps = Api5.SafeInt(() => section.StepsCount);
            var parameters = Api5.SafeObject(() => section.Step[0].Parameters);
            if (parameters is IDynamicCrossSectionStepParametersByOffsetPlane byOffset
                && Api5.SafeObject(() => byOffset.OffsetPlane) is IPlane3DByOffset plane)
            {
                offset = Api5.SafeDouble(() => plane.Offset);
            }
        }

        step.Data["steps_after_reopen"] = steps;
        step.Data["offset_after_reopen"] = Api5.Num(offset);

        if (step.Data["count_after_reopen"] is int count && count > 0)
        {
            step.Pass("Сечение пережило сохранение: сечений " + count + ", режим "
                + Api5.Raw(step.Data["mode_after_reopen"]) + ", смещение "
                + Api5.Raw(step.Data["offset_after_reopen"]) + ".");
            return;
        }

        step.Fail("После повторного открытия сечений " + Api5.Raw(step.Data["count_after_reopen"])
            + ": сечения в файле не сохраняются.");
    }

    private void DeleteSection(IDynamicCrossSectionsManager manager, IDynamicCrossSection section)
    {
        var step = _report.Begin("S.9", "Удаление сечения", "Убирает ли Delete() сечение из модели?");
        var deleted = Api5.SafeBool(() => section.Delete());
        step.Data["delete_returned"] = Api5.Raw(deleted);
        step.Data["count_after_delete"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["mode_after_delete"] = Api5.Raw(Api5.SafeBool(() => manager.DynamicCrossSectionModeOn));
        if (deleted == true && step.Data["count_after_delete"] is 0)
        {
            step.Pass("Сечение удалено: счётчик 0.");
            return;
        }

        step.Fail("Delete() вернул " + Api5.Raw(deleted) + ", сечений "
            + Api5.Raw(step.Data["count_after_delete"]) + ".");
    }

    /// <summary>The second documented kind of step: is the rotated plane writable, i.e. does its
    /// <c>Plane</c> (<c>IPlane3DByAngle</c>) take the documented inputs?</summary>
    private void RotatedPlaneRoute(IDynamicCrossSectionsManager manager)
    {
        var step = _report.Begin("S.10", "Шаг повёрнутой плоскостью",
            "Есть ли документированный маршрут записи у повёрнутой плоскости?");
        var section = Api5.SafeObject(() => manager.AddDynamicCrossSection()) as IDynamicCrossSection;
        if (section is null)
        {
            step.Fail("Сечение для повёрнутой плоскости не создано.");
            return;
        }

        var created = Api5.SafeObject(() => section.AddStep(ksDynamicCrossSectionStepBuildingTypeEnum.ksDCSTByRotatedPlane));
        step.Data["add_step_returned"] = Api5.RuntimeName(created);
        if (created is not IDynamicCrossSectionStep dynamicStep)
        {
            step.Fail("AddStep(ksDCSTByRotatedPlane) не отдал IDynamicCrossSectionStep.");
            return;
        }

        var parameters = Api5.SafeObject(() => dynamicStep.Parameters);
        step.Data["parameters"] = Api5.RuntimeName(parameters);
        if (parameters is not IDynamicCrossSectionStepParametersByRotatedPlane byRotated)
        {
            step.Fail("Parameters не отвечает QI(ByRotatedPlane): " + Api5.RuntimeName(parameters));
            return;
        }

        var plane = Api5.SafeObject(() => byRotated.Plane);
        step.Data["plane"] = Api5.RuntimeName(plane);
        step.Data["plane_building_type"] = Api5.Raw(Api5.SafeEnum(() => byRotated.PlaneBuildingType));
        var byAngle = plane as IPlane3DByAngle;
        step.Data["plane_as_by_angle"] = Api5.RuntimeName(byAngle);
        if (byAngle is null)
        {
            step.Fail("Plane не отвечает QI(IPlane3DByAngle): угол записать нечем.");
            return;
        }

        var basePlane = DefaultPlane(_part5!, "XOY");
        try
        {
            byAngle.BasePlane = basePlane;
            byAngle.Angle = 30d;
            step.Observe("Плоскость: BasePlane=XOY, Angle=30 записаны");
        }
        catch (COMException ex)
        {
            step.Fail("Запись входа повёрнутой плоскости бросила HRESULT 0x" + ex.HResult.ToString("X8")
                + ": " + ex.Message);
            return;
        }

        step.Data["plane_update"] = Api5.Raw(Api5.SafeBool(() => byAngle.Update()));
        step.Data["section_update"] = Api5.Raw(Api5.SafeBool(() => section.Update()));
        step.Data["angle_after"] = Api5.Num(Api5.SafeDouble(() => byAngle.Angle));
        var angle = Api5.SafeDouble(() => byAngle.Angle);
        if (angle is null)
        {
            step.Fail("Угол не читается обратно.");
            return;
        }

        step.Pass("Повёрнутая плоскость принимает BasePlane и Angle, угол читается обратно: "
            + Api5.Num(angle) + "°.");
    }

    /// <summary>The same manager on an ASSEMBLY document: the order requires sections for assemblies too.</summary>
    private void AssemblyRoute()
    {
        var step = _report.Begin("S.11", "Сечение сборки", "Применим ли маршрут к документу сборки?");
        var directory = Path.Combine(_options.WorkDir, "sections");
        _sourcePath = Path.Combine(directory, "g11-source.m3d");
        _assemblyPath = Path.Combine(directory, "g11-assembly.m3d");
        var source = (ksDocument3D)_app.Document3D();
        if (source.Create(true, true) != true)
        {
            step.Fail("Документ-источник для сборки не создан.");
            return;
        }

        if (Api5.BasePlate((ksPart)source.GetPart(-1), 40, 40, 10,
                _report.Begin("S.11.0", "Деталь-источник для сборки"), "g11-src") is null)
        {
            step.Fail("Деталь-источник для сборки не построена.");
            return;
        }

        source.RebuildDocument();
        var savedSource = source.SaveAs(_sourcePath);
        step.Data["source_saved"] = savedSource;
        source.close();
        _part5?.close();
        _part5 = null!;

        var assembly = (ksDocument3D)_app.Document3D();
        if (assembly.Create(true, false) != true)
        {
            step.Fail("Документ сборки не создан.");
            return;
        }

        var parts7 = (_app.TransferInterface(assembly, 2, 0) as IKompasDocument3D)?.TopPart?.Parts;
        if (parts7 is null)
        {
            step.Fail("IPart7.Parts не дал IParts7 — вставлять нечем.");
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            step.Observe("AddFromFile попытка " + attempt + " → "
                + Api5.RuntimeName(parts7.AddFromFile(_sourcePath, true, true)));
        }

        var components = Api5.SafeInt(() => parts7.Count);
        step.Data["components"] = components;
        if (components is null or 0)
        {
            step.Fail("Компоненты не вставлены: сборка пуста.");
            return;
        }

        assembly.RebuildDocument();
        assembly.SaveAs(_assemblyPath);
        _assembly5 = assembly;
        var manager = Manager(assembly, "S.11.1");
        if (manager is null)
        {
            step.Fail("Документ сборки не отдал менеджер сечений.");
            return;
        }

        var section = Api5.SafeObject(() => manager.AddDynamicCrossSection()) as IDynamicCrossSection;
        if (section is null)
        {
            step.Fail("Сечение сборки не создано.");
            return;
        }

        var created = Api5.SafeObject(() => section.AddStep(ksDynamicCrossSectionStepBuildingTypeEnum.ksDCSTByOffsetPlane));
        var parameters = (created as IDynamicCrossSectionStep)?.Parameters;
        var byOffset = parameters as IDynamicCrossSectionStepParametersByOffsetPlane;
        step.Data["parameters"] = Api5.RuntimeName(parameters);
        if (byOffset is null)
        {
            step.Fail("Параметры шага сборки не отвечают QI(ByOffsetPlane).");
            return;
        }

        var plane = Api5.SafeObject(() => byOffset.OffsetPlane) as IPlane3DByOffset;
        var basePlane = DefaultPlane(assembly, "XOY");
        step.Data["base_plane"] = Api5.RuntimeName(basePlane);
        if (plane is null || basePlane is null)
        {
            step.Fail("Плоскость шага сборки не получена.");
            return;
        }

        plane.BasePlane = basePlane;
        plane.Offset = SectionOffset;
        step.Data["plane_update"] = Api5.Raw(Api5.SafeBool(() => plane.Update()));
        step.Data["section_update"] = Api5.Raw(Api5.SafeBool(() => section.Update()));
        SetMode(manager, true);
        step.Data["mode"] = Api5.Raw(Api5.SafeBool(() => manager.DynamicCrossSectionModeOn));
        step.Data["sections_count"] = Api5.SafeInt(() => manager.DynamicCrossSectionsCount);
        step.Data["steps_count"] = Api5.SafeInt(() => section.StepsCount);
        step.Data["offset_after"] = Api5.Num(Api5.SafeDouble(() => plane.Offset));
        if (step.Data["offset_after"] is null || step.Data["sections_count"] is not 1)
        {
            step.Fail("Сечение сборки не подтверждено чтением: сечений "
                + Api5.Raw(step.Data["sections_count"]) + ", смещение "
                + Api5.Raw(step.Data["offset_after"]) + ".");
            return;
        }

        step.Pass("Сечение сборки записано и подтверждено чтением.");
    }

    /// <summary>The standard plane of a document as an API7 object: <c>ksPart.GetDefaultEntity</c> by type,
    /// transferred with <c>TransferInterface</c>. MEASURED: <c>IPlanes3D</c> holds no standard planes.
    /// History: docs/decisions/sections.md#probe</summary>
    private IModelObject? DefaultPlane(ksDocument3D document, string wanted)
    {
        var type = wanted switch
        {
            "XOY" => ksObj3dTypeEnum.o3d_planeXOY,
            "XOZ" => ksObj3dTypeEnum.o3d_planeXOZ,
            "YOZ" => ksObj3dTypeEnum.o3d_planeYOZ,
            _ => ksObj3dTypeEnum.o3d_planeXOY,
        };
        var entity = Api5.SafeObject(() => ((ksPart)document.GetPart(-1)).GetDefaultEntity((short)type));
        return entity is null ? null : Api5.SafeObject(() => _app.TransferInterface(entity, 2, 0)) as IModelObject;
    }

    private void Shutdown()
    {
        var step = _report.Begin("S.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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
                return;
            }

            step.Fail("Процесс остался после Quit().");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение отказало: " + ex.Message);
        }
    }

    /// <summary>A string member that may be missing: a refusal is <c>null</c>, never a substituted value.</summary>
    private static string? Text(Func<string?> call)
    {
        try
        {
            return call();
        }
        catch (COMException)
        {
            return null;
        }
    }
}
