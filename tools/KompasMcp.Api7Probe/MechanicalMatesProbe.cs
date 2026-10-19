using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe G4 — mechanical mates: are «Механическая передача» and «Кулачок - толкатель» created by
/// the documented API7 route, are their properties readable, and is the LINK observable?</summary>
/// <remarks>DOC: the route is <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c>
/// («Add - Создает новый элемент и добавляет его в коллекцию»), then <c>QueryInterface</c> to
/// <c>IMate3DTransmission</c> / <c>IMate3DCamGear</c> — the interface pages name exactly that way of
/// obtaining them. DOC: <c>imate3dtransmission_methods.html</c> lists ONE method (<c>SetScale</c>);
/// <c>imate3dcamgear_methods.html</c> is 404, so the cam gear has no documented methods at all.
/// INVARIANT: a mate that returns <c>true</c> proves nothing — the linkage is confirmed only by a MEASURED
/// change of the driven component's placement after the driving component was moved by the documented
/// <c>ksPart.GetPlacement/SetPlacement/UpdatePlacement</c> route and the document was rebuilt.
/// History: docs/decisions/probes.md#mechanical-mates</remarks>
internal sealed class MechanicalMatesProbe
{
    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short BaseExtrusion = 24;
    private const int LineStyle = 1;

    private const double GearRadius = 30d;
    private const double GearHeight = 10d;
    private const double GearOffsetX = 150d;
    private const double FollowerWidth = 60d;
    private const double FollowerHeight = 20d;
    private const double FollowerThickness = 8d;
    private const double FollowerOffsetY = 120d;

    private const double TurnDegrees = 30d;
    private const double TurnTolerance = 0.5d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private ProbeStep? _current;

    private KompasObject _app = null!;
    private IApplication? _app7;
    private ksDocument3D _doc = null!;
    private IAxis3D? _axis1;
    private IAxis3D? _axis2;
    private IAxis3D? _camAxis;
    private readonly List<uint> _pidsBefore = new();

    public MechanicalMatesProbe(ProbeReport report, Options options)
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
            if (BuildSourcePart("gear", GearRadius, GearHeight) is not { } gear
                || BuildSourcePart("follower", -1d, FollowerThickness) is not { } follower)
            {
                return;
            }

            if (BuildAssembly(gear, follower) is not { } path)
            {
                return;
            }

            if (Reopen(path) is not { } document)
            {
                return;
            }

            _doc = document;
            var components = Components();
            if (components.Count < 3)
            {
                var step = _report.Begin("G4.4", "Три компонента сборки", "Есть ли что сопрягать?");
                step.Fail($"компонентов {components.Count}, нужно 3 (две шестерни и толкатель).");
                return;
            }

            TransmissionCreate(components[0], components[1]);
            TransmissionLinkage(components[0], components[1]);
            CamCreate(components[0], components[2]);
            CamLinkage(components[0], components[2]);
            PositionerControl(follower);
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("G4.X", "Необработанное исключение пробы G4");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("G4.1", "Свой невидимый экземпляр КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        _current = step;
        try
        {
            foreach (var process in Process.GetProcessesByName("KOMPAS"))
            {
                _pidsBefore.Add((uint)process.Id);
                process.Dispose();
            }

            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;

            var created = Process.GetProcessesByName("KOMPAS")
                .Select(p => (uint)p.Id).Except(_pidsBefore).ToArray();
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            _app7 = _app.ksGetApplication7() as IApplication;
            step.Observe("ksGetApplication7 → " + (_app7 is null ? "null" : "IApplication"));
            step.Pass("экземпляр запущен и атрибутирован диффом процессов");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("G4.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            _app.Quit();
            var pid = _options.ProcessId;
            var gone = pid is null || WaitUntilGone(pid.Value, 30_000);
            step.Pass(gone ? "собственный экземпляр завершён штатно" : "процесс жив через 30 с после Quit()");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение не подтверждено: " + ex.Message);
        }
    }

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
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

            System.Threading.Thread.Sleep(500);
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════ fixture ══

    /// <summary>A source part: a cylinder (radius &gt; 0) or a plate (radius &lt; 0) extruded to
    /// <paramref name="thickness"/> along the sketch normal.</summary>
    private string? BuildSourcePart(string name, double radius, double thickness)
    {
        var step = _report.Begin("G4.2." + name, "Деталь-источник «" + name + "»",
            "Есть ли файл, из которого вставлять компонент?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mech-mates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, true) != true)
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "g4-" + name;
        part.Update();

        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane)
        {
            step.Fail("GetDefaultEntity(o3d_planeXOY) вернул null.");
            return null;
        }

        if (part.NewEntity(SketchType) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            step.Fail("Эскиз или его определение не созданы.");
            return null;
        }

        sketch.name = "g4-" + name + "-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();
        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("BeginEdit() вернул не ksDocument2D.");
            return null;
        }

        try
        {
            if (radius > 0)
            {
                editor.ksCircle(0d, 0d, radius, LineStyle);
            }
            else
            {
                var halfWidth = FollowerWidth / 2d;
                var halfHeight = FollowerHeight / 2d;
                editor.ksLineSeg(-halfWidth, -halfHeight, halfWidth, -halfHeight, LineStyle);
                editor.ksLineSeg(halfWidth, -halfHeight, halfWidth, halfHeight, LineStyle);
                editor.ksLineSeg(halfWidth, halfHeight, -halfWidth, halfHeight, LineStyle);
                editor.ksLineSeg(-halfWidth, halfHeight, -halfWidth, -halfHeight, LineStyle);
            }
        }
        finally
        {
            sketchDefinition.EndEdit();
        }

        if (part.NewEntity(BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Fail("Базовое выдавливание не создано.");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0 /* etBlind */, thickness, 0d, false);
        if (Api5.SafeBool(extrusion.Create) != true)
        {
            step.Fail("Create() выдавливания вернул false.");
            return null;
        }

        document.RebuildDocument();
        var faces = Api5.ReadFaces(part, step);
        step.Data["faces"] = faces.Count;
        step.Observe("граней у детали: " + faces.Count
            + ", цилиндрических: " + faces.Count(f => f.IsCylinder));

        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true.");
            return null;
        }

        document.close();
        step.Observe("файл: " + path + "; документ закрыт");
        step.Pass("источник сохранён");
        return path;
    }

    private string? BuildAssembly(string gearPath, string followerPath)
    {
        var step = _report.Begin("G4.3", "Сборка: две шестерни и толкатель",
            "Документированный IParts7.AddFromFile даёт компоненты с геометрией?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mech-mates");
        var path = Path.Combine(directory, "mech-assembly.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, false) != true)
        {
            step.Fail("Create(невидимый, сборка) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "g4-asm";
        part.Update();

        var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        var parts7 = top7?.Parts;
        if (parts7 is null)
        {
            step.Fail("IPart7.Parts не дал IParts7 — вставлять нечем.");
            return null;
        }

        foreach (var (file, label) in new[] { (gearPath, "шестерня 1"), (gearPath, "шестерня 2"), (followerPath, "толкатель") })
        {
            Part7? inserted = null;
            try
            {
                inserted = parts7.AddFromFile(file, ExternalFile: true, Redraw: true);
            }
            catch (Exception ex)
            {
                step.Fail("AddFromFile(" + label + ") бросил: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            step.Observe("AddFromFile(" + label + ") → " + (inserted is null ? "null" : "Part7"));
        }

        var count = Api5.SafeInt(() => parts7.Count);
        step.Data["components"] = count;
        step.Observe("компонентов по IParts7.Count: " + count);

        // THE COMPONENTS ARE SEPARATED, otherwise the axes coincide and the transmission degenerates.
        // DOC/MEASURED (block C1): placement is written through DefaultPlacement → InitByMatrix3D →
        // SetPlacement → UpdatePlacement; the matrix layout is [X,0][Y,0][Z,0][translation,1].
        var offsets = new[]
        {
            (Index: 0, X: 0d, Y: 0d),
            (Index: 1, X: GearOffsetX, Y: 0d),
            (Index: 2, X: 0d, Y: FollowerOffsetY),
        };

        foreach (var (index, x, y) in offsets)
        {
            if (document.PartCollection(true) is not ksPartCollection collection
                || index >= collection.GetCount()
                || collection.GetByIndex(index) is not ksPart component
                || document.DefaultPlacement() is not ksPlacement placement)
            {
                step.Observe($"компонент {index}: размещение не задано (маршрут не дал объект)");
                continue;
            }

            var matrix = new double[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                x, y, 0, 1,
            };
            placement.InitByMatrix3D(matrix);
            component.SetPlacement(placement);
            component.UpdatePlacement();
            step.Observe($"компонент {index} сдвинут на ({Api5.Num(x)}; {Api5.Num(y)})");
        }

        document.RebuildDocument();
        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true — переоткрытие мерить не на чем.");
            return null;
        }

        document.close();
        step.Observe("сборка сохранена и закрыта: " + path);
        step.Pass("сборка с тремя компонентами сохранена");
        return path;
    }

    private ksDocument3D? Reopen(string path)
    {
        var step = _report.Begin("G4.4", "Переоткрытие сборки с диска",
            "Появляются ли тела компонентов после загрузки документа?");
        _current = step;

        var document = (ksDocument3D)_app.Document3D();
        var opened = Api5.SafeBool(() => document.Open(path));
        step.Observe("Open(" + path + ") = " + opened);
        if (opened != true)
        {
            step.Fail("сборка не открылась");
            return null;
        }

        // The object Open was called on does not become the document (MEASURED in probe M): the active
        // document is taken from the application.
        var active = Api5.SafeObject(() => _app.GetType().InvokeMember(
            "ActiveDocument", System.Reflection.BindingFlags.GetProperty, null, _app, null)) as ksDocument3D;
        if (active is not null)
        {
            document = active;
        }

        document.RebuildDocument();
        var components = Api5.SafeInt(() => (document.PartCollection(true) as ksPartCollection)!.GetCount());
        step.Data["components"] = components;
        step.Observe("компонентов после переоткрытия: " + components);
        step.Pass("сборка переоткрыта");
        return document;
    }

    private List<ksPart> Components()
    {
        var list = new List<ksPart>();
        if (_doc.PartCollection(true) is not ksPartCollection collection)
        {
            return list;
        }

        for (var index = 0; index < collection.GetCount(); index++)
        {
            if (collection.GetByIndex(index) is ksPart component)
            {
                list.Add(component);
            }
        }

        return list;
    }

    // ══════════════════════════════════════════════════ transmission ══

    private void TransmissionCreate(ksPart first, ksPart second)
    {
        var step = _report.Begin("G4.5", "Механическая передача вращение-вращение создаётся маршрутом §2",
            "IMateConstraints3D.Add(mc_Transmission) + QI(IMate3DTransmission) даёт сопряжение и его свойства?");
        _current = step;

        var face1 = CylinderFace(first, step, "1");
        var face2 = CylinderFace(second, step, "2");
        if (face1 is null || face2 is null)
        {
            step.Fail("цилиндрическая грань не получена ни у одного компонента — ось не построить");
            return;
        }

        _axis1 = AxisByFace(first, face1, step, "1");
        _axis2 = AxisByFace(second, face2, step, "2");
        // THE MATE TAKES THE FACES, the positioner takes the AXES: MEASURED — the same mate whose
        // BaseObject1/2 and RotationAxis1/2 are constructive axes reads back Valid=false, while the
        // faces read back Valid=true. The axis object is what InitAxis accepts, so both are kept.
        var axis1 = face1;
        var axis2 = face2;
        step.Data["axis_built_1"] = _axis1 is not null;
        step.Data["axis_built_2"] = _axis2 is not null;

        if (Mates7() is not { } mates)
        {
            step.Fail("IPart7.MateConstraints не дал IMateConstraints3D.");
            return;
        }

        var before = Api5.SafeInt(() => mates.Count);
        step.Data["mates_before"] = before;

        IMateConstraint3D? mate = null;
        try
        {
            mate = mates.Add(MateConstraintType.mc_Transmission);
        }
        catch (Exception ex)
        {
            step.Observe("Add(mc_Transmission) бросил: " + ex.GetType().Name + ": " + ex.Message);
        }

        if (mate is null)
        {
            step.Fail("Add(mc_Transmission) вернул null — сопряжение не создано.");
            return;
        }

        step.Observe("Add(mc_Transmission) → " + mate.GetType().FullName);
        var transmission = mate as IMate3DTransmission;
        step.Observe("QI(IMate3DTransmission) → " + (transmission is null ? "null" : "получен"));
        if (transmission is null)
        {
            step.Fail("сопряжение не приводится к IMate3DTransmission — свойства этим маршрутом недоступны.");
            return;
        }

        // THE ORDER IS THE ONE ACCEPTED FOR ORDINARY MATES (BaseObject1/2 → parameters → Update), because
        // the property-write order is NOT documented; if it does not work, that is recorded, not permuted.
        // DOC: imateconstraint3d_baseobject1/2.html — «Базовый объект 1/2», IModelObject, read/write.
        Try("BaseObject1", () => mate.BaseObject1 = axis1);
        Try("BaseObject2", () => mate.BaseObject2 = axis2);
        Try("MotionType1", () => transmission.MotionType1 = ksMateMotionTypeEnum.ksMMotionRotation);
        Try("MotionType2", () => transmission.MotionType2 = ksMateMotionTypeEnum.ksMMotionRotation);
        Try("RotationAxis1", () => transmission.RotationAxis1 = axis1);
        Try("RotationAxis2", () => transmission.RotationAxis2 = axis2);
        Try("SetScale(1,2)", () => step.Data["set_scale"] = transmission.SetScale(1d, 2d));
        Try("Direction1", () => transmission.Direction1 = true);
        Try("Direction2", () => transmission.Direction2 = true);
        Try("Update", () => step.Data["update"] = mate.Update());

        _doc.RebuildDocument();

        step.Data["motion1"] = Api5.SafeInt(() => (int)transmission.MotionType1);
        step.Data["motion2"] = Api5.SafeInt(() => (int)transmission.MotionType2);
        step.Data["scale1"] = Api5.SafeDouble(() => transmission.Scale1);
        step.Data["scale2"] = Api5.SafeDouble(() => transmission.Scale2);
        step.Data["direction1"] = Api5.SafeBool(() => transmission.Direction1);
        step.Data["direction2"] = Api5.SafeBool(() => transmission.Direction2);
        step.Data["axis1_read"] = Api5.SafeObject(() => transmission.RotationAxis1) is not null;
        step.Data["axis2_read"] = Api5.SafeObject(() => transmission.RotationAxis2) is not null;
        step.Data["base1_read"] = Api5.SafeObject(() => mate.BaseObject1) is not null;
        step.Data["base2_read"] = Api5.SafeObject(() => mate.BaseObject2) is not null;
        step.Data["valid"] = Api5.SafeBool(() => mate.Valid);
        step.Data["mates_after"] = Api5.SafeInt(() => mates.Count);

        step.Observe($"перечитано: MotionType1={step.Data["motion1"]}, MotionType2={step.Data["motion2"]}, "
            + $"Scale1={Api5.Num(step.Data["scale1"] as double?)}, Scale2={Api5.Num(step.Data["scale2"] as double?)}, "
            + $"Direction1={step.Data["direction1"]}, Direction2={step.Data["direction2"]}, Valid={step.Data["valid"]}, "
            + $"сопряжений {step.Data["mates_before"]} → {step.Data["mates_after"]}");

        if (Api5.SafeBool(() => mate.Valid) == true)
        {
            step.Pass("сопряжение создано и Valid=true; свойства читаются из модели");
        }
        else
        {
            step.Fail("сопряжение создано, но Valid≠true: связь не подтверждена.");
        }
    }

    private void TransmissionLinkage(ksPart first, ksPart second)
    {
        var step = _report.Begin("G4.6", "Связь передачи наблюдаема: поворот компонента 1 двигает компонент 2",
            "Меняется ли положение компонента 2 по соотношению Scale1:Scale2 при повороте компонента 1?");
        _current = step;

        var beforeFirst = Placement(first);
        var beforeSecond = Placement(second);
        step.Data["second_before"] = beforeSecond;
        if (beforeFirst is null || beforeSecond is null)
        {
            step.Fail("размещение компонентов не прочитано — мерить нечего.");
            return;
        }

        var angleBefore = AngleOf(beforeSecond);
        var turned = TurnAboutZ(first, beforeFirst, TurnDegrees, step);
        if (turned is null)
        {
            step.Fail("поворот компонента 1 документированным маршрутом не применён.");
            return;
        }

        var afterFirst = Placement(first);
        var afterSecond = Placement(second);
        step.Data["first_after"] = afterFirst;
        step.Data["second_after"] = afterSecond;
        if (afterFirst is null || afterSecond is null)
        {
            step.Fail("размещение после поворота не прочитано.");
            return;
        }

        var firstTurn = AngleDelta(beforeFirst, afterFirst);
        var secondTurn = AngleDelta(beforeSecond, afterSecond);
        var secondShift = TranslationDelta(beforeSecond, afterSecond);
        step.Data["placement_route_second_turn_deg"] = secondTurn;
        step.Data["placement_route_second_shift_mm"] = secondShift;

        // ROUTE B, measured on a component put back where it started: the positioner is the documented
        // «управление положением компонентов в сборке», the route a mate can be solved on.
        TurnAboutZ(first, beforeFirst, 0d, step);
        var positionerTurn = (double?)null;
        var positionerShift = (double?)null;
        if (RotateByPositioner(first, _axis1, TurnDegrees, step, "компонент 1"))
        {
            var secondByPositioner = Placement(second);
            var firstByPositioner = Placement(first);
            step.Data["first_after_positioner"] = firstByPositioner;
            step.Data["second_after_positioner"] = secondByPositioner;
            if (firstByPositioner is not null && secondByPositioner is not null)
            {
                firstTurn = AngleDelta(beforeFirst, firstByPositioner);
                positionerTurn = AngleDelta(beforeSecond, secondByPositioner);
                positionerShift = TranslationDelta(beforeSecond, secondByPositioner);
            }
        }

        step.Data["first_turn_deg"] = firstTurn;
        step.Data["positioner_route_second_turn_deg"] = positionerTurn;
        step.Data["positioner_route_second_shift_mm"] = positionerShift;
        step.Observe($"маршрут размещения: компонент 1 {Api5.Num(firstTurn)}°, компонент 2 "
            + $"{Api5.Num(secondTurn)}°, перенос {Api5.Num(secondShift)} мм");
        step.Observe($"маршрут позиционера: компонент 2 {Api5.Num(positionerTurn)}°, перенос "
            + $"{Api5.Num(positionerShift)} мм");

        secondTurn = positionerTurn is not null && Math.Abs(positionerTurn.Value) > TurnTolerance
            ? positionerTurn
            : secondTurn;

        // THE RATIO IS MEASURED, NOT ASSUMED: at Scale1:Scale2 = 1:2 the driven component may turn by
        // alpha/2 or by alpha*2 — the direction of the coefficient binding is what this number settles.
        // A DRIVER THAT DID NOT TURN GIVES NO RATIO: comparing two zeros would report "alpha/2" as true.
        if (firstTurn is not null && Math.Abs(firstTurn.Value) <= TurnTolerance)
        {
            step.Observe("сопряжение ВЕРНУЛО компонент 1 в прежнее положение: солвер не пропустил ход "
                + "ведущего компонента — связь живая, но ведомый за ним не пошёл");
        }

        var driverTurned = firstTurn is not null && Math.Abs(firstTurn.Value) > TurnTolerance;
        var half = driverTurned
            && Math.Abs(Math.Abs(secondTurn ?? 0d) - Math.Abs(firstTurn!.Value) / 2d) <= TurnTolerance;
        var twice = driverTurned
            && Math.Abs(Math.Abs(secondTurn ?? 0d) - Math.Abs(firstTurn!.Value) * 2d) <= TurnTolerance;
        step.Data["ratio_half"] = half;
        step.Data["ratio_twice"] = twice;

        if (secondTurn is null || Math.Abs(secondTurn.Value) <= TurnTolerance)
        {
            step.Fail("компонент 2 НЕ двинулся ни маршрутом размещения, ни маршрутом позиционера: "
                + "связь передачи этими маршрутами не наблюдается.");
            return;
        }

        var route = positionerTurn is not null && Math.Abs(positionerTurn.Value) > TurnTolerance
            ? "позиционера"
            : "размещения";
        step.Pass(half
            ? $"связь наблюдаема маршрутом {route}: компонент 2 повернулся на alpha/2 (Scale1:Scale2 = 1:2)"
            : twice
                ? $"связь наблюдаема маршрутом {route}: компонент 2 повернулся на alpha*2 (коэффициенты связаны наоборот)"
                : $"компонент 2 двинулся маршрутом {route}, но соотношение не alpha/2 и не alpha*2 — привязка коэффициентов иная");
    }

    // ══════════════════════════════════════════════════════ cam gear ══

    private void CamCreate(ksPart cam, ksPart follower)
    {
        var step = _report.Begin("G4.7", "Кулачок - толкатель создаётся маршрутом §2",
            "IMateConstraints3D.Add(mc_CamGear) + QI(IMate3DCamGear) даёт сопряжение и его свойства?");
        _current = step;

        var camFaces = Faces(cam, step, "кулачок");
        var followerFaces = Faces(follower, step, "толкатель");
        if (camFaces.Count == 0 || followerFaces.Count == 0)
        {
            step.Fail("грани кулачка или толкателя не прочитаны.");
            return;
        }

        // DOC: CamFaces — «Массив SAFEARRAY граней кулачка», type VARIANT. The array is passed as an
        // array of IModelObject, which is what a SAFEARRAY of VARIANT carries.
        var camArray = camFaces.Select(f => (object)f.Model).ToArray();
        var followerFace = followerFaces[0].Model;
        var camFace = CylinderFace(cam, step, "кулачка");
        if (camFace is null)
        {
            step.Fail("цилиндрическая грань кулачка не получена — ось вращения не построить.");
            return;
        }

        _camAxis = AxisByFace(cam, camFace, step, "кулачка");
        var rotationAxis = camFace;
        step.Data["cam_axis_built"] = _camAxis is not null;
        var trajectory = StraightEdgeObject(follower, step);
        if (trajectory is null)
        {
            step.Fail("траектория толкателя не получена.");
            return;
        }

        if (Mates7() is not { } mates)
        {
            step.Fail("IPart7.MateConstraints не дал IMateConstraints3D.");
            return;
        }

        var before = Api5.SafeInt(() => mates.Count);
        IMateConstraint3D? mate = null;
        try
        {
            mate = mates.Add(MateConstraintType.mc_CamGear);
        }
        catch (Exception ex)
        {
            step.Observe("Add(mc_CamGear) бросил: " + ex.GetType().Name + ": " + ex.Message);
        }

        if (mate is null)
        {
            step.Fail("Add(mc_CamGear) вернул null — сопряжение не создано.");
            return;
        }

        var camGear = mate as IMate3DCamGear;
        step.Observe("QI(IMate3DCamGear) → " + (camGear is null ? "null" : "получен"));
        if (camGear is null)
        {
            step.Fail("сопряжение не приводится к IMate3DCamGear.");
            return;
        }

        Try("BaseObject1", () => mate.BaseObject1 = camFaces[0].Model);
        Try("BaseObject2", () => mate.BaseObject2 = followerFace);
        Try("CamFaces", () => camGear.CamFaces = camArray);
        Try("FollowerFace", () => camGear.FollowerFace = followerFace);
        Try("RotationAxis", () => camGear.RotationAxis = rotationAxis);
        Try("Trajectory", () => camGear.Trajectory = trajectory);
        Try("Update", () => step.Data["update"] = mate.Update());
        _doc.RebuildDocument();

        step.Data["cam_faces_read"] = Api5.SafeObject(() => camGear.CamFaces) is Array read ? read.Length : null;
        step.Data["follower_face_read"] = Api5.SafeObject(() => camGear.FollowerFace) is not null;
        step.Data["rotation_axis_read"] = Api5.SafeObject(() => camGear.RotationAxis) is not null;
        step.Data["trajectory_read"] = Api5.SafeObject(() => camGear.Trajectory) is not null;
        step.Data["base1_read"] = Api5.SafeObject(() => mate.BaseObject1) is not null;
        step.Data["base2_read"] = Api5.SafeObject(() => mate.BaseObject2) is not null;
        step.Data["valid"] = Api5.SafeBool(() => mate.Valid);
        step.Data["mates_before"] = before;
        step.Data["mates_after"] = Api5.SafeInt(() => mates.Count);
        step.Observe($"перечитано: CamFaces={step.Data["cam_faces_read"]}, "
            + $"FollowerFace={step.Data["follower_face_read"]}, RotationAxis={step.Data["rotation_axis_read"]}, "
            + $"Trajectory={step.Data["trajectory_read"]}, Valid={step.Data["valid"]}, "
            + $"сопряжений {before} → {step.Data["mates_after"]}");

        if (Api5.SafeBool(() => mate.Valid) == true)
        {
            step.Pass("сопряжение создано и Valid=true; свойства читаются из модели");
        }
        else
        {
            step.Fail("сопряжение создано, но Valid≠true: связь не подтверждена.");
        }
    }

    private void CamLinkage(ksPart cam, ksPart follower)
    {
        var step = _report.Begin("G4.8", "Связь кулачка наблюдаема: поворот кулачка двигает толкатель",
            "Меняется ли положение толкателя при повороте кулачка на два разных угла?");
        _current = step;

        var start = Placement(cam);
        var followerBefore = Placement(follower);
        if (start is null || followerBefore is null)
        {
            step.Fail("размещение не прочитано — мерить нечего.");
            return;
        }

        var shift1 = TurnAndMeasure(cam, follower, start, 15d, followerBefore, step, "15");
        TurnAboutZ(cam, start, 0d, step);
        var positioner1 = RotateByPositioner(cam, _camAxis, 15d, step, "кулачок, 15°")
            ? ShiftOf(follower, followerBefore, step, "15° маршрутом позиционера")
            : null;
        var positioner2 = RotateByPositioner(cam, _camAxis, 15d, step, "кулачок, ещё 15°")
            ? ShiftOf(follower, followerBefore, step, "30° маршрутом позиционера")
            : null;
        step.Data["shift_at_15"] = shift1;
        step.Data["positioner_shift_at_15"] = positioner1;
        step.Data["positioner_shift_at_30"] = positioner2;

        var best = new[] { shift1, positioner1, positioner2 }.Where(v => v is not null)
            .Select(v => v!.Value).DefaultIfEmpty(0d).Max(v => Math.Abs(v));
        step.Data["max_shift_mm"] = best;

        if (best <= 1e-6)
        {
            step.Fail("толкатель НЕ двинулся ни на одном угле ни одним из двух маршрутов: связь кулачка "
                + "этими маршрутами не наблюдается.");
            return;
        }

        step.Pass("связь наблюдаема: наибольший перенос толкателя " + Api5.Num(best) + " мм");
    }

    private double? ShiftOf(ksPart follower, double[] before, ProbeStep step, string label)
    {
        var after = Placement(follower);
        if (after is null)
        {
            return null;
        }

        var shift = TranslationDelta(before, after);
        step.Observe($"после поворота кулачка на {label}: перенос толкателя {Api5.Num(shift)} мм");
        return shift;
    }

    /// <summary>Turn the cam to a fixed angle from the start placement and return the follower's shift.</summary>
    private double? TurnAndMeasure(
        ksPart cam, ksPart follower, double[] start, double degrees, double[] followerBefore, ProbeStep step,
        string label)
    {
        if (TurnAboutZ(cam, start, degrees, step) is null)
        {
            return null;
        }

        return ShiftOf(follower, followerBefore, step, label + "° маршрутом размещения");
    }

    // ══════════════════════════════════════════════════════════ control ══

    /// <summary>DISCRIMINATING CONTROL: does the documented positioner route move a component at all where
    /// no mate exists? Without it, «Prepare()=FALSE» cannot be told from a misused instrument.</summary>
    private void PositionerControl(string followerPath)
    {
        var step = _report.Begin("G4.9", "Контроль прибора: позиционер поворачивает компонент БЕЗ сопряжений",
            "Работает ли документированный маршрут позиционера там, где сопряжений нет?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mech-mates");
        var path = Path.Combine(directory, "positioner-control.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, false) != true)
        {
            step.Fail("сборка контроля не создана.");
            return;
        }

        // THE CONTAINER IS TAKEN FROM THE DOCUMENT JUST CREATED, not from the application's active
        // document: MEASURED in this step — after a previous assembly was reopened, `Create` did not make
        // the new document active, and the inserts went into the OTHER document (0 components saved).
        var document7 = _app.TransferInterface(document, (int)ksAPITypeEnum.ksAPI7Dual, 0)
            as IKompasDocument3D;
        if (document7?.TopPart?.Parts is not { } parts7)
        {
            step.Fail("IParts7 нового документа не получен — вставлять нечем.");
            return;
        }

        parts7.AddFromFile(followerPath, ExternalFile: true, Redraw: true);
        parts7.AddFromFile(followerPath, ExternalFile: true, Redraw: true);
        var insertedCount = Api5.SafeInt(() => parts7.Count);
        step.Observe("компонентов контроля после вставки: " + insertedCount);
        if (insertedCount is null or < 2)
        {
            step.Fail("вставка в документ контроля не дала двух компонентов.");
            return;
        }

        document.RebuildDocument();
        document.SaveAs(path);
        document.close();

        if (Reopen(path) is not { } reopened)
        {
            step.Fail("сборка контроля не переоткрылась.");
            return;
        }

        _doc = reopened;
        if (_doc.PartCollection(true) is not ksPartCollection collection || collection.GetCount() < 1
            || collection.GetByIndex(0) is not ksPart component)
        {
            step.Fail("компонент контроля не получен.");
            return;
        }

        var before = Placement(component);
        if (before is null)
        {
            step.Fail("размещение компонента контроля не прочитано.");
            return;
        }

        var edge = StraightEdgeObject(component, step);
        IAxis3D? axis = null;
        if (edge is not null)
        {
            axis = AxisByEdge(component, edge, step);
        }

        if (!RotateByPositioner(component, axis, TurnDegrees, step, "компонент контроля"))
        {
            step.Fail("позиционер НЕ повернул даже несопряжённый компонент: маршрут в этом приборе не "
                + "работает, и его отказ в опыте выше ничего не говорит о сопряжениях.");
            return;
        }

        var after = Placement(component);
        var turn = after is null ? null : AngleDelta(before, after);
        step.Data["control_turn_deg"] = turn;
        step.Observe("поворот несопряжённого компонента: " + Api5.Num(turn) + "°");
        if (turn is null || Math.Abs(turn.Value) <= TurnTolerance)
        {
            step.Fail("позиционер ответил успехом, но компонент не повернулся: маршрут не подтверждён.");
            return;
        }

        step.Pass("контроль пройден: позиционер поворачивает несопряжённый компонент — его отказ выше "
            + "относится к сопряжённым компонентам, а не к прибору");
    }

    /// <summary>An axis through a straight edge: <c>o3d_axisEdge</c> → <c>IAxis3DByEdge.Edge</c>.</summary>
    private IAxis3D? AxisByEdge(ksPart component, IModelObject edge, ProbeStep step)
    {
        try
        {
            if (_app.TransferInterface(component, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                is not IAuxiliaryGeomContainer auxiliary
                || auxiliary.Axes3D is not { } axes
                || axes.Add(ksObj3dTypeEnum.o3d_axisEdge) is not IAxis3D axis
                || axis is not IAxis3DByEdge byEdge)
            {
                step.Observe("ось по ребру не построена: контейнер, коллекция или QI не дали объект");
                return null;
            }

            byEdge.Edge = edge;
            var updated = Api5.SafeBool(axis.Update);
            step.Observe("ось по ребру построена, Update()=" + updated);
            return updated == true ? axis : null;
        }
        catch (Exception ex)
        {
            step.Observe("ось по ребру бросила " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private IMateConstraints3D? Mates7()
    {
        var top = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        return top?.MateConstraints;
    }

    /// <summary>A constructive rotation axis through a cylindrical face — the documented API7 route
    /// <c>IAuxiliaryGeomContainer.Axes3D.Add(o3d_axisConeFace)</c> → <c>IAxis3DByConeface.Face</c>.</summary>
    /// <remarks>WHY an axis and not the face: <c>IComponentPositioner7.InitAxis</c> takes «конструктивную
    /// ось или прямолинейное ребро», so the face is not an address the mover accepts, and a mate whose
    /// axis is a bare face is not the same object as one built the documented way.
    /// History: docs/decisions/probes.md#mechanical-mates</remarks>
    private IAxis3D? AxisByFace(ksPart component, IModelObject face, ProbeStep step, string label)
    {
        try
        {
            if (_app.TransferInterface(component, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                is not IAuxiliaryGeomContainer auxiliary)
            {
                step.Observe($"{label}: деталь не отвечает QI(IAuxiliaryGeomContainer) — ось не построить");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes || axes.Add(ksObj3dTypeEnum.o3d_axisConeFace) is not IAxis3D axis)
            {
                step.Observe($"{label}: Axes3D.Add(o3d_axisConeFace) не отдал IAxis3D");
                return null;
            }

            if (axis is not IAxis3DByConeface byFace)
            {
                step.Observe($"{label}: ось не отвечает QI(IAxis3DByConeface)");
                return null;
            }

            axis.Name = "g4-axis-" + label;
            byFace.Face = face;
            var updated = Api5.SafeBool(axis.Update);
            step.Observe($"{label}: ось по цилиндрической грани построена, Update()={updated}");
            return updated == true ? axis : null;
        }
        catch (Exception ex)
        {
            step.Observe($"{label}: построение оси бросило {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rotate a component by the DOCUMENTED positioner route
    /// <c>IKompasDocument3D1.ComponentPositioner</c> → <c>Prepare(pnRotate)</c> → <c>InitAxis</c> →
    /// <c>RotateComponent</c> → <c>Finish</c>.</summary>
    /// <remarks>WHY this route and not a placement write: the positioner is documented as «Интерфейс
    /// управления положением компонентов в сборке», i.e. the route that lets the assembly solve its
    /// mates while the component moves; a raw <c>SetPlacement</c> writes the matrix and asks no solver.
    /// Both are measured, and the difference is the finding.
    /// History: docs/decisions/probes.md#mechanical-mates</remarks>
    private bool RotateByPositioner(ksPart component, IAxis3D? axis, double degrees, ProbeStep step, string label)
    {
        try
        {
            if ((_app7?.ActiveDocument as IKompasDocument3D1)?.ComponentPositioner is not { } positioner)
            {
                step.Observe($"{label}: ComponentPositioner не получен");
                return false;
            }

            if (_app.TransferInterface(component, (int)ksAPITypeEnum.ksAPI7Dual, 0) is not Part7 part7)
            {
                step.Observe($"{label}: компонент не переносится в API7 как Part7");
                return false;
            }

            var prepared = positioner.Prepare(part7, Positioner_Type.pnRotate);
            step.Observe($"{label}: Prepare(pnRotate) = {prepared}");
            if (axis is not null)
            {
                var axisSet = Api5.SafeBool(() => positioner.InitAxis(axis));
                step.Observe($"{label}: InitAxis(ось) = {axisSet}");
            }

            var radians = degrees * Math.PI / 180d;
            var rotated = Api5.SafeBool(() => positioner.RotateComponent(radians));
            var finished = Api5.SafeBool(positioner.Finish);
            _doc.RebuildDocument();
            step.Observe($"{label}: RotateComponent({Api5.Num(radians)} рад) = {rotated}, Finish() = {finished}");
            return rotated == true;
        }
        catch (Exception ex)
        {
            step.Observe($"{label}: маршрут позиционера бросил {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The component's CYLINDRICAL face as <c>IModelObject</c> — what a rotation axis is built
    /// from, and the fallback object when the axis itself cannot be built.</summary>
    private IModelObject? CylinderFace(ksPart component, ProbeStep step, string label)
    {
        var faces = Faces(component, step, "компонент " + label);
        var cylinder = faces.FirstOrDefault(f => f.IsCylinder);
        if (cylinder is null)
        {
            step.Observe($"у компонента {label} цилиндрической грани нет — ось не адресуется");
            return null;
        }

        step.Observe($"компонент {label}: цилиндрическая грань[{cylinder.Index}]");
        return cylinder.Model;
    }

    /// <summary>A straight edge of a component as <c>IModelObject</c> — the follower's trajectory.</summary>
    private IModelObject? StraightEdgeObject(ksPart component, ProbeStep step)
    {
        if (component.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0
            || bodies.GetByIndex(0) is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("у толкателя нет тела или граней — ребро не адресуется");
            return null;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (FaceDefinition(faces.GetByIndex(f)) is not { } definition
                || definition.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var model = ToModelObject(edges.GetByIndex(e));
                if (model is not null)
                {
                    step.Observe($"траектория толкателя: ребро[{e}] грани[{f}]");
                    return model;
                }
            }
        }

        step.Observe("ни одно ребро толкателя не перенесено в API7 как IModelObject");
        return null;
    }

    private sealed record FaceHandle(int Index, bool IsCylinder, IModelObject Model);

    private List<FaceHandle> Faces(ksPart component, ProbeStep step, string label)
    {
        var list = new List<FaceHandle>();
        if (component.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0
            || bodies.GetByIndex(0) is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe($"{label}: тел или граней нет");
            return list;
        }

        for (var index = 0; index < faces.GetCount(); index++)
        {
            var element = faces.GetByIndex(index);
            var definition = FaceDefinition(element);
            if (definition is null)
            {
                continue;
            }

            var model = ToModelObject(element);
            if (model is null)
            {
                continue;
            }

            var isCylinder = Api5.SafeObject(definition.GetSurface) is ksSurface surface
                && Api5.SafeObject(surface.GetSurfaceParam) is ksCylinderParam;
            list.Add(new FaceHandle(index, isCylinder, model));
        }

        step.Observe($"{label}: граней {faces.GetCount()}, перенесено {list.Count}, "
            + $"цилиндрических {list.Count(f => f.IsCylinder)}");
        return list;
    }

    private static ksFaceDefinition? FaceDefinition(object? element) =>
        element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;

    private IModelObject? ToModelObject(object? element) =>
        element is null ? null : _app.TransferInterface(element, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;

    /// <summary>The component's placement as 16 numbers, or <c>null</c> if it was NOT READ.</summary>
    private static double[]? Placement(ksPart part)
    {
        try
        {
            if (part.GetPlacement() is not ksPlacement placement || !placement.GetMatrix3D(out var raw))
            {
                return null;
            }

            if (raw is not Array array || array.Length < 16)
            {
                return null;
            }

            var result = new double[16];
            for (var index = 0; index < 16; index++)
            {
                result[index] = Convert.ToDouble(array.GetValue(index), CultureInfo.InvariantCulture);
            }

            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Turn a component about the Z axis through its own origin, keeping the translation.</summary>
    private double[]? TurnAboutZ(ksPart part, double[] start, double degrees, ProbeStep step)
    {
        var radians = degrees * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var matrix = new double[]
        {
            cos, sin, 0, 0,
            -sin, cos, 0, 0,
            0, 0, 1, 0,
            start[12], start[13], start[14], 1,
        };

        try
        {
            if (part.GetPlacement() is not ksPlacement placement)
            {
                step.Observe("GetPlacement() не дал ksPlacement — поворот не применён");
                return null;
            }

            placement.InitByMatrix3D(matrix);
            part.SetPlacement(placement);
            part.UpdatePlacement();
            _doc.RebuildDocument();
            step.Observe($"поворот на {Api5.Num(degrees)}° записан документированным маршрутом");
            return matrix;
        }
        catch (Exception ex)
        {
            step.Observe("поворот бросил: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>The rotation angle about Z carried by the matrix's X axis.</summary>
    private static double AngleOf(double[] matrix) =>
        Math.Atan2(matrix[1], matrix[0]) * 180d / Math.PI;

    private static double? AngleDelta(double[] before, double[] after)
    {
        var delta = AngleOf(after) - AngleOf(before);
        while (delta > 180d)
        {
            delta -= 360d;
        }

        while (delta < -180d)
        {
            delta += 360d;
        }

        return delta;
    }

    private static double TranslationDelta(double[] before, double[] after)
    {
        var dx = after[12] - before[12];
        var dy = after[13] - before[13];
        var dz = after[14] - before[14];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _current?.Observe(what + " бросил: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
