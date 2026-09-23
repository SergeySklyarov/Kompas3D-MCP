using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// §3 — сращивается ли родное вращение-бобышка с СУЩЕСТВУЮЩИМ телом.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему этот опыт нужен отдельно.</b> Адаптер отказывает в бобышке при <c>bodiesBefore &gt;= 1</c>
/// с причиной <c>rotation_boss_glue_unmeasured</c>. Отказ опирается на два прошлых опыта, и <b>оба
/// записали неверный член результата</b>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>R.25</c> создал <c>o3d_bossRotated</c> и записал <c>OperationResult = ksOperationNewBody</c> —
///     то есть попросил НОВОЕ ТЕЛО у типа, который просит сращивание.
///   </description></item>
///   <item><description>
///     <c>R.26</c> «перекрёстная проверка (тип boss, результат cut)» записала
///     <c>OperationResult = ksOperationCut</c> — параметр, предназначенный для вырезания.
///   </description></item>
/// </list>
/// <para>
/// Значение, которое адаптер сам считает правильным для бобышки, — <c>ksOperationUnion</c>
/// (<c>Api7Rotated.OperationResultOf</c>), и оно <b>не измерялось ни разу</b>. Поэтому прошлый вывод
/// «сращивание не подтверждено» относится к постановке опыта, а не к продукту: опыт спрашивал
/// другое. Это тот же класс, что и опровергнутое «насыщение на 180°».
/// </para>
/// <para>
/// <b>Что измеряется.</b> Плита и цилиндр-выступ, пересекающиеся по положительной толщине, причём
/// часть цилиндра выходит за плиту. Тогда вклад в объём известен аналитически и равен объёму части
/// цилиндра ВНЕ плиты: сращивание добавляет ровно её, а не всё тело. Это различает три исхода —
/// «слилось» (прирост = выступающая часть), «второе тело» (тел 1→2), «отказ» (прироста нет).
/// </para>
/// <para>
/// <b>Контроль на вырождение.</b> Отдельно ставится случай без пересечения: если он даёт тот же
/// результат, что и пересекающийся, значит опыт меряет не сращивание, и вывод отменяется.
/// </para>
/// </remarks>
internal sealed class BossFuseProbe
{
    private const double PlateHalf = 60d;
    private const double PlateThickness = 10d;
    private const double CylinderRadius = 20d;
    private const double CylinderHeight = 40d;

    /// <summary>Plate volume: 120×120×10.</summary>
    private static double PlateVolume => (2d * PlateHalf) * (2d * PlateHalf) * PlateThickness;

    /// <summary>Cylinder volume: π·r²·h.</summary>
    private static double CylinderVolume => Math.PI * CylinderRadius * CylinderRadius * CylinderHeight;

    /// <summary>
    /// The part of the cylinder that protrudes beyond the plate: h minus the plate thickness.
    /// Only this much may be ADDED by a correct union.
    /// </summary>
    private static double ProtrudingVolume => Math.PI * CylinderRadius * CylinderRadius * (CylinderHeight - PlateThickness);

    private static double ExpectFused => PlateVolume + ProtrudingVolume;

    /// <summary>Two separate bodies: the plate plus the whole cylinder.</summary>
    private static double ExpectTwoBodies => PlateVolume + CylinderVolume;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public BossFuseProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "boss-fuse.json"),
            Path.Combine(options.ReportDir, "boss-fuse.md"));
    }

    public void Run()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            BossInEmptyDocumentControl();
            ReferenceCase();
            RepeatWithShiftedAxis();
            NoIntersectionCase();
            FullyInsideCase();
            UnionVersusNewBodyControl();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("B.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        var clock = Stopwatch.StartNew();
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
        clock.Stop();

        step.Observe("создан экземпляр API5 за " + clock.ElapsedMilliseconds + " мс");
        step.Observe("процессов KOMPAS.exe было до запуска: " + _pidsBefore.Count);
        step.Observe("свой процесс: " + (_ownPid == 0 ? "не определён" : _ownPid.ToString()));
        step.Pass("сеанс поднят");
    }

    // ══════════════════════════════════════════════════════════════ cases ══

    /// <summary>The reference case from the work order: plate 120×120×10, cylinder R20 H40 on Z.</summary>
    private void ReferenceCase()
    {
        var step = _report.Begin("B.1", "Эталон: бобышка-цилиндр R20 H40 на плите 120×120×10",
            "Добавляет ли ksOperationUnion ровно выступающую часть цилиндра?");

        step.Observe("ожидание ДО опыта: плита V=" + Api5.Num(PlateVolume)
            + "; цилиндр R20 H40 V=" + Api5.Num(CylinderVolume));
        step.Observe("  пересечение: цилиндр z∈[0,40], плита z∈[0,10] — толщина пересечения 10 мм");
        step.Observe("  выступает за плиту 30 мм → вклад сращивания ΔV=" + Api5.Num(ProtrudingVolume));
        step.Observe("  СЛИЛОСЬ → V=" + Api5.Num(ExpectFused) + ", тел 1→1");
        step.Observe("  ВТОРОЕ ТЕЛО → V=" + Api5.Num(ExpectTwoBodies) + ", тел 1→2");

        RunFusion(step, "эталон", plateShiftX: 0d, plateShiftY: 0d, withIntersection: true);
    }

    /// <summary>
    /// The work order also demands a repeat on a document with other dimensions and a shifted axis,
    /// with the expectation computed in advance.
    /// </summary>
    private void RepeatWithShiftedAxis()
    {
        var step = _report.Begin("B.2", "Повтор: другая плита и ось, сдвинутая к краю",
            "Повторяется ли результат на другой геометрии, а не только на эталоне?");

        // A smaller plate and a cylinder whose axis sits off-centre, so the intersection is not
        // symmetric: a route that only works for a centred coincident axis would fail here.
        const double half = 40d;
        const double thickness = 12d;
        const double radius = 12d;
        const double height = 30d;
        const double offsetX = 15d;

        var plate = (2d * half) * (2d * half) * thickness;
        var protruding = Math.PI * radius * radius * (height - thickness);

        step.Observe("ожидание ДО опыта: плита " + (2 * half) + "×" + (2 * half) + "×" + thickness
            + " V=" + Api5.Num(plate) + ", цилиндр R" + radius + " H" + height
            + " V=" + Api5.Num(Math.PI * radius * radius * height));
        step.Observe("  ось сдвинута на x=" + offsetX + " — пересечение несимметрично");
        step.Observe("  СЛИЛОСЬ → V=" + Api5.Num(plate + protruding)
            + " (ΔV=" + Api5.Num(protruding) + "), тел 1→1");

        RunFusion(step, "сдвиг", plateShiftX: offsetX, plateShiftY: 0d, withIntersection: true,
            half: half, thickness: thickness, radius: radius, height: height,
            expectFused: plate + protruding, expectTwo: plate + Math.PI * radius * radius * height);
    }

    /// <summary>
    /// Clears the probe itself before any conclusion about the product: the SAME profile, axis and
    /// code path, but in an EMPTY document with no blank. If the boss builds here and not on the
    /// plate, the refusal is about the boss-on-a-body; if it does not build here either, the probe
    /// never exercised the boss route at all and B.1–B.5 say nothing about fusion.
    /// </summary>
    private void BossInEmptyDocumentControl()
    {
        var step = _report.Begin("B.6", "Очистка прибора: та же бобышка в ПУСТОМ документе",
            "Строится ли маршрут вообще, когда тела под ним нет?");

        step.Observe("тот же эскиз, та же ось, тот же код; тела под бобышкой НЕТ");
        step.Observe("  ожидание: прирост = целому цилиндру π·r²·h=" + Api5.Num(CylinderVolume)
            + ", тел 0→1 — это действие create, а не сращивание");
        step.Observe("  перебираются ОБА значения OperationResult: если маршрут исправен только "
            + "с одним из них, отказ — про значение, а не про тело под бобышкой");

        var anyBuilt = false;
        foreach (var result in new[]
                 {
                     ksOperationResultEnum.ksOperationNewBody,
                     ksOperationResultEnum.ksOperationUnion,
                 })
        {
            var built = BossInEmptyDocument(step, result);
            anyBuilt |= built;
        }

        if (anyBuilt)
        {
            step.Pass("маршрут бобышки исправен хотя бы с одним значением OperationResult — "
                + "прибор проводит опыт, и отказы B.1…B.5 можно читать как факт о теле под бобышкой");
        }
        else
        {
            step.Fail("бобышка не создана ни с одним значением OperationResult — прибор не проводит "
                + "опыт, и выводы B.1…B.5 о сращивании недействительны");
        }
    }

    /// <summary>One boss attempt in an empty document; true when it produced the full cylinder.</summary>
    private bool BossInEmptyDocument(ProbeStep step, ksOperationResultEnum result)
    {
        step.Observe("— попытка с OperationResult=" + result);

        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);
            var volumeBefore = Api5.Volume(part);
            var bodiesBefore = Api5.BodyCount(part);
            step.Observe("до бобышки: тел=" + bodiesBefore + ", V=" + Api5.Num(volumeBefore));

            var sketch = BossProfileSketch(doc, part, 0d, 0d);
            if (sketch is null)
            {
                step.Observe("  → эскиз не построен");
                return false;
            }

            var axis = BuildAxis(doc, part, 0d, 0d, 0d, CylinderHeight, step);
            if (axis is null)
            {
                step.Observe("  → ось не построена");
                return false;
            }

            var rotation = CreateBoss(
                part, doc, sketch, axis, CylinderRadius, CylinderHeight, step, result);
            if (rotation is null)
            {
                step.Observe("  → Update() не подтвердил создание");
                return false;
            }

            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);
            var delta = Diff(volumeBefore, volumeAfter);
            var shape = DescribeShape(part, step);
            step.Observe("после бобышки: тел=" + bodiesBefore + "→" + bodiesAfter
                + ", V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + ", изменение=" + Api5.Num(delta));
            step.Observe("форма: " + shape);
            step.Observe("дерево: " + ReadTree(doc, step));

            if (delta is not null && Math.Abs(delta.Value - CylinderVolume) <= Tolerance(CylinderVolume))
            {
                step.Observe("  → построено: прирост " + Api5.Num(delta) + " = π·r²·h");
                return true;
            }

            step.Observe("  → не построено: изменение " + Api5.Num(delta)
                + " против ожидания " + Api5.Num(CylinderVolume));
            return false;
        }
        catch (Exception ex)
        {
            step.Observe("  → бросил " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// The discriminating control: the same intersecting setup, but with the result parameter the
    /// previous probes wrote. If this builds and Union does not, the refusal is about Union
    /// specifically; if both fail, it is about a boss on an existing body.
    /// </summary>
    private void UnionVersusNewBodyControl()
    {
        var step = _report.Begin("B.5", "Различающий контроль: ksOperationNewBody против ksOperationUnion",
            "Отказ вызван значением OperationResult или самим фактом бобышки на теле?");

        step.Observe("та же плита и та же ось, что в B.1; меняется ТОЛЬКО OperationResult");
        step.Observe("  если NewBody строится, а Union нет — отказ про Union;");
        step.Observe("  если оба не строятся — отказ про бобышку на существующем теле");

        RunFusion(step, "NewBody вместо Union", plateShiftX: 0d, plateShiftY: 0d,
            withIntersection: true, result: ksOperationResultEnum.ksOperationNewBody);
    }

    /// <summary>
    /// A cylinder that does not touch the plate at all. If this gives the same answer as the
    /// intersecting case, the experiment is measuring something other than fusion.
    /// </summary>
    private void NoIntersectionCase()
    {
        var step = _report.Begin("B.3", "Контроль на вырождение: цилиндр НЕ пересекает плиту",
            "Отличает ли опыт сращивание от простого появления второго тела?");

        step.Observe("цилиндр поднят так, что между ним и плитой есть зазор 20 мм");
        step.Observe("  ожидание: сращиваться НЕ с чем, поэтому исход «слилось» здесь означал бы, "
            + "что опыт меряет не сращивание и вывод B.1 отменяется");

        RunFusion(step, "без пересечения", plateShiftX: 0d, plateShiftY: 0d, withIntersection: false,
            gapMm: 20d);
    }

    /// <summary>
    /// A cylinder fully inside the plate. The work order is explicit: this must be recorded
    /// separately and the rule "a boss always adds volume" must not be imposed on it.
    /// </summary>
    private void FullyInsideCase()
    {
        var step = _report.Begin("B.4", "Цилиндр целиком внутри плиты",
            "Что происходит, когда сращивание не меняет объём по существу?");

        step.Observe("цилиндр целиком внутри материала: правильное сращивание даёт прирост РОВНО 0");
        step.Observe("  ноль здесь НЕ признак отказа — ноль и есть ожидаемый ответ "
            + "(это отдельный случай, а не расширение правила «бобышка обязана добавить объём»)");

        RunFusion(step, "целиком внутри", plateShiftX: 0d, plateShiftY: 0d, withIntersection: true,
            thickness: 60d, height: 20d, radius: 10d,
            expectFused: (2d * PlateHalf) * (2d * PlateHalf) * 60d,
            expectTwo: (2d * PlateHalf) * (2d * PlateHalf) * 60d + Math.PI * 100d * 20d);
    }

    // ══════════════════════════════════════════════════════════════ core ══

    private void RunFusion(
        ProbeStep step, string label, double plateShiftX, double plateShiftY, bool withIntersection,
        double half = PlateHalf, double thickness = PlateThickness,
        double radius = CylinderRadius, double height = CylinderHeight,
        double gapMm = 0d, double? expectFused = null, double? expectTwo = null,
        ksOperationResultEnum result = ksOperationResultEnum.ksOperationUnion)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);

            // The blank first: a real body in the document before the boss exists.
            var plate = BuildPlate(doc, part, half, thickness, plateShiftX, plateShiftY, step);
            if (plate is null)
            {
                step.Fail("плита не построена");
                return;
            }

            var volumeBefore = Api5.Volume(part);
            var bodiesBefore = Api5.BodyCount(part);
            step.Observe("до бобышки: тел=" + bodiesBefore + ", V=" + Api5.Num(volumeBefore));

            if (volumeBefore is null || Math.Abs(volumeBefore.Value - plate.Value) > Tolerance(plate.Value))
            {
                step.Fail("объём плиты не совпал: прочитано " + Api5.Num(volumeBefore)
                    + " против " + Api5.Num(plate.Value));
                return;
            }

            // The cylinder's own profile and axis, placed so the intersection is known in advance.
            var baseZ = withIntersection
                ? (gapMm > 0d ? thickness + gapMm : 0d)
                : thickness + gapMm;
            var sketch = BossProfileSketch(doc, part, plateShiftX, plateShiftY);
            if (sketch is null)
            {
                step.Fail("эскиз профиля бобышки не построен");
                return;
            }

            var axis = BuildAxis(doc, part, plateShiftX, plateShiftY, baseZ, height, step);
            if (axis is null)
            {
                step.Fail("ось бобышки не построена");
                return;
            }

            // THE experiment: `o3d_bossRotated` with `ksOperationUnion` — the value the adapter
            // itself considers correct and which no previous probe ever wrote.
            var rotation = CreateBoss(part, doc, sketch, axis, radius, height, step, result);
            if (rotation is null)
            {
                step.Fail("признак бобышки не создан");
                return;
            }

            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);
            var delta = Diff(volumeBefore, volumeAfter);

            step.Observe("после бобышки: тел=" + bodiesBefore + "→" + bodiesAfter
                + ", V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + ", изменение=" + Api5.Num(delta));

            var fused = expectFused ?? ExpectFused;
            var two = expectTwo ?? ExpectTwoBodies;
            var protrusion = fused - plate.Value;

            // The read-back of OperationResult: what the model kept is a fact in its own right, and
            // a route that silently discards the written value must not be reported as if it honoured it.
            step.Observe("OperationResult после записи: "
                + SafeRead(() => (rotation as IRotated1)?.OperationResult.ToString() ?? "IRotated1 не поддержан"));
            step.Observe("Angle[true]=" + SafeRead(() => rotation.Angle[true].ToString(CultureInfo.InvariantCulture))
                + ", Angle[false]=" + SafeRead(() => rotation.Angle[false].ToString(CultureInfo.InvariantCulture))
                + ", Direction=" + SafeRead(() => rotation.Direction.ToString()));

            var shape = DescribeShape(part, step);
            step.Observe("форма: " + shape);

            // The verdict is a volume statement against a pre-computed expectation, never against a
            // boolean returned by Update(). Three outcomes are distinguished, and "second body" is
            // not silently reported as success.
            if (bodiesAfter > bodiesBefore)
            {
                if (delta is not null && Math.Abs(delta.Value - protrusion) <= Tolerance(protrusion))
                {
                    step.Observe("объём вырос на выступающую часть, но тел стало больше: "
                        + "материал добавлен как ВТОРОЕ ТЕЛО, а не сращён");
                }

                step.Observe("тел " + bodiesAfter + " — сращивания нет: появилось отдельное тело");
            }

            var volumeMatchesFused = delta is not null && Math.Abs(delta.Value - protrusion) <= Tolerance(protrusion);
            var volumeMatchesWhole = delta is not null && Math.Abs(delta.Value - CylinderVolume) <= Tolerance(CylinderVolume);
            var noGain = delta is not null && Math.Abs(delta.Value) <= Tolerance(1d);
            var cylinderPresent = shape.Contains("цилиндрических граней 0", StringComparison.Ordinal) == false;

            // A case where NOTHING was built must never report success, however the expectations
            // happen to line up. This is the "vacuous pass" hole: when the expected gain is zero
            // (B.4), a probe that built nothing would otherwise satisfy the arithmetic exactly.
            if (!cylinderPresent && noGain)
            {
                step.Fail("признак НЕ появился: цилиндрических граней нет и объём не изменился — "
                    + "опыт ничего не построил, поэтому исход не считается ни сращиванием, ни отказом");
                return;
            }

            if (volumeMatchesFused && bodiesAfter == bodiesBefore)
            {
                step.Pass("СРАЩИВАНИЕ ЕСТЬ: прирост " + Api5.Num(delta) + " = выступающая часть "
                    + Api5.Num(protrusion) + ", тел " + bodiesBefore + "→" + bodiesAfter
                    + " (одно тело, как и требуется)");
            }
            else if (volumeMatchesWhole && bodiesAfter > bodiesBefore)
            {
                step.Observe("прирост равен ЦЕЛОМУ цилиндру при росте числа тел — это добавление "
                    + "отдельного тела, а не сращивание");
                step.Fail("сращивания нет: материал добавлен вторым телом");
            }
            else if (noGain && withIntersection && protrusion > Tolerance(1d))
            {
                step.Fail("прироста нет, хотя выступающая часть не нулевая: сращивание не произошло");
            }
            else
            {
                step.Fail("исход не назван: изменение " + Api5.Num(delta) + " не равно ни "
                    + Api5.Num(protrusion) + " (сращивание), ни " + Api5.Num(CylinderVolume)
                    + " (второе тело)");
            }

            step.Data["delta"] = delta;
            step.Data["bodies_before"] = bodiesBefore;
            step.Data["bodies_after"] = bodiesAfter;
            step.Data["volume_before"] = volumeBefore;
            step.Data["volume_after"] = volumeAfter;
            step.Data["expectation_fused"] = fused;
            step.Data["expectation_two_bodies"] = two;
            step.Data["label"] = label;

            // The feature must be in the history as a NATIVE boss for the claim to be about the
            // product rather than about a mass that happens to be there.
            step.Observe("дерево: " + ReadTree(doc, step));
        }
        catch (Exception ex)
        {
            step.Fail("опыт бросил " + ex.GetType().Name + ": " + ex.Message);
            step.Errors.Add(ex.ToString());
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Builds the blank as a real extruded body and returns its analytic volume.</summary>
    private double? BuildPlate(
        ksDocument3D doc, ksPart part, double half, double thickness,
        double shiftX, double shiftY, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
            {
                step.Observe("плита: GetDefaultEntity(o3d_planeXOY) вернул null");
                return null;
            }

            if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                step.Observe("плита: NewEntity(o3d_sketch) не дал определение");
                return null;
            }

            sketch.name = "B-plate-profile";
            sketchDefinition.SetPlane(plane);
            sketch.Create();

            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                step.Observe("плита: BeginEdit() вернул не ksDocument2D");
                return null;
            }

            // Placed at a chosen offset so the boss axis can be off-centre relative to the plate.
            var u0 = shiftX - half;
            var u1 = shiftX + half;
            var v0 = shiftY - half;
            var v1 = shiftY + half;
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
            sketchDefinition.EndEdit();

            if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
                || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                step.Observe("плита: NewEntity(o3d_baseExtrusion) не дал определение");
                return null;
            }

            // Measured route (Api5.BasePlate): directionType 0 → material to +z of the sketch plane;
            // SetSideParam(true, 0 /* etBlind */, thickness, 0d, false).
            definition.SetSketch(sketch);
            definition.directionType = 0;
            definition.SetSideParam(true, 0, thickness, 0d, false);
            if (extrusion.Create() != true)
            {
                step.Observe("плита: Create() → false");
                return null;
            }

            part.RebuildModel();
            doc.RebuildDocument();

            var analytic = (2 * half) * (2 * half) * thickness;
            var volume = Api5.Volume(part);
            step.Observe("плита: " + (2 * half) + "×" + (2 * half) + "×" + thickness
                + ", V=" + Api5.Num(volume) + " против аналитического " + Api5.Num(analytic));

            return volume is not null && Math.Abs(volume.Value - analytic) <= Tolerance(analytic)
                ? volume
                : null;
        }
        catch (Exception ex)
        {
            step.Observe("плита бросила " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>The rectangle that is revolved: u∈[cx, cx+r], v∈[cy−r, cy+r].</summary>
    private static ksEntity? BossProfileSketch(
        ksDocument3D doc, ksPart part, double centreX, double centreY, double radius = CylinderRadius)
    {
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
        {
            return null;
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            return null;
        }

        sketch.name = "B-boss-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();

        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            return null;
        }

        // The axis of revolution will be the profile's own left edge, so a full sweep is a full
        // cylinder rather than a ring.
        var u0 = centreX;
        var u1 = centreX + radius;
        var v0 = centreY - radius;
        var v1 = centreY + radius;
        editor.ksLineSeg(u0, v0, u1, v0, 1);
        editor.ksLineSeg(u1, v0, u1, v1, 1);
        editor.ksLineSeg(u1, v1, u0, v1, 1);
        editor.ksLineSeg(u0, v1, u0, v0, 1);
        sketchDefinition.EndEdit();

        // The rebuild after EndEdit is not optional: without it the profile is left uncommitted and
        // the rotation's Update() answers False — measured as the B.6 instrument control, which
        // caught exactly this in the probe's own code before any conclusion was drawn.
        part.RebuildModel();
        doc.RebuildDocument();

        return sketch;
    }

    /// <summary>
    /// The axis is built in MODEL coordinates by two points, the route measured to give a full
    /// cylinder (F.1a). The old R.25 resolved the axis from the sketch plane, which flipped the
    /// sign of the sweep.
    /// </summary>
    private IAxis3D? BuildAxis(
        ksDocument3D doc, ksPart part, double centreX, double centreY,
        double baseZ, double height, ProbeStep step)
    {
        try
        {
            // The part is transferred to API7 once and queried for BOTH containers on the same
            // object, the shape measured to work in F/R.13. Axes3D lives on
            // IAuxiliaryGeomContainer, points on IModelContainer.
            if (_app.TransferInterface(part, 2, 0) is not IModelObject part7)
            {
                step.Observe("ось: деталь не переносится в API7 как IModelObject");
                return null;
            }

            if (part7 is not IModelContainer container || part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                step.Observe("ось: деталь не отвечает QI(IModelContainer)/QI(IAuxiliaryGeomContainer)");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes)
            {
                step.Observe("ось: Axes3D → null");
                return null;
            }

            var p1 = MakePoint(container, centreX, centreY, baseZ);
            var p2 = MakePoint(container, centreX, centreY, baseZ + height);
            if (p1 is null || p2 is null)
            {
                step.Observe("ось: точки не создались");
                return null;
            }

            // `Add` is cast to IAxis3DBy2Points DIRECTLY and that reference is used: asking for the
            // base IAxis3D first yields a proxy whose further use the kernel does not honour —
            // measured here, the feature then refuses to build (B.6 control).
            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                step.Observe("ось: Axes3D.Add не отдал IAxis3DBy2Points");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;

            // Update() only after both points — measured in R.13.
            var updated = Api5.SafeBool(by2.Update);
            var valid = Api5.SafeBool(() => by2.Valid);
            step.Observe("ось: Update()=" + Api5.Raw(updated) + ", Valid=" + Api5.Raw(valid));
            return updated == true ? by2 : null;
        }
        catch (Exception ex)
        {
            step.Observe("ось бросила " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Creates the native boss with <c>ksOperationUnion</c> — the parameter whose measurement is the
    /// point of this probe.
    /// </summary>
    private IRotated? CreateBoss(
        ksPart part, ksDocument3D doc, ksEntity profile, IAxis3D axis,
        double radius, double height, ProbeStep step,
        ksOperationResultEnum result = ksOperationResultEnum.ksOperationUnion)
    {
        try
        {
            var document7 = _app.TransferInterface(doc, 2, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            if (container is null)
            {
                step.Observe("бобышка: TopPart как IModelContainer не получен");
                return null;
            }

            // The factory result is cast through IModelObject first, the route measured to work in
            // F/R.24. A direct cast yields a proxy that accepts member writes but is not bound to a
            // real feature — measured here: every case built nothing while Update() returned True.
            if (container.Rotateds is not { } rotateds)
            {
                step.Observe("бобышка: Rotateds недостижим");
                return null;
            }

            if ((rotateds.Add(ksObj3dTypeEnum.o3d_bossRotated) as IModelObject) as IRotated
                is not { } rotation)
            {
                step.Observe("бобышка: Rotateds.Add(o3d_bossRotated) не дал QI(IRotated)");
                return null;
            }

            rotation.Profile = _app.TransferInterface(profile, 2, 0) as IModelObject;
            rotation.Axis = axis;
            rotation.Angle[true] = 360d;
            rotation.Angle[false] = 0d;
            rotation.Direction = ksDirectionTypeEnum.dtNormal;
            rotation.RotatedType[true] = ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;

            // The measured law from F: the sweep is 2× the written angle, capped at 360, with the
            // axis on the profile boundary. 180 → 360° sweep → the whole cylinder.
            //
            // THE parameter under test. `ksOperationUnion` is what the adapter maps Boss to and what
            // no previous probe wrote: R.25 wrote ksOperationNewBody, R.26 wrote ksOperationCut.
            if (rotation is IRotated1 withResult)
            {
                withResult.OperationResult = result;
                step.Observe("OperationResult ← " + result + " (значение, которое адаптер считает "
                    + "правильным для бобышки; прошлые опыты писали NewBody и Cut)");
            }
            else
            {
                step.Observe("IRotated1 не поддержан — OperationResult не записан");
            }

            // `Update()` returning a boolean is NOT a verdict (the solver accepts a no-op); it is
            // recorded and used only to refuse a hard failure. The verdict is the volume.
            var updated = Api5.SafeBool(rotation.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("Update()=" + Api5.Raw(updated));
            return updated == true ? rotation : null;
        }
        catch (Exception ex)
        {
            step.Observe("бобышка бросила " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ readings ══

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return "чтение бросило " + ex.GetType().Name;
        }
    }

    private static string DescribeShape(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return "тела нет";
            }

            var bounds = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? "габарит x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "] y[" + Api5.Num(y1) + ","
                    + Api5.Num(y2) + "] z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]"
                : "габарит не прочитан";

            var cylinders = new List<string>();
            if (body.FaceCollection() is ksFaceCollection faces)
            {
                for (var f = 0; f < faces.GetCount(); f++)
                {
                    if (faces.GetByIndex(f) is not ksFaceDefinition face)
                    {
                        continue;
                    }

                    try
                    {
                        if (face.GetSurface() is ksSurface surface && surface.IsCylinder()
                            && surface.GetSurfaceParam() is ksCylinderParam cylinder)
                        {
                            cylinders.Add("r=" + Api5.Num(cylinder.radius) + " h=" + Api5.Num(cylinder.height));
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // A face whose parameters КОМПАС withheld is not a reason to abort the walk.
                    }
                }
            }

            return "цилиндрических граней " + cylinders.Count
                + (cylinders.Count == 0 ? string.Empty : " (" + string.Join("; ", cylinders) + ")")
                + ", " + bounds;
        }
        catch (Exception ex)
        {
            return "форма не прочитана: " + ex.GetType().Name;
        }
    }

    private static string ReadTree(ksDocument3D doc, ProbeStep step)
    {
        try
        {
            if (doc.GetPart(-1) is not ksPart part || part.GetFeature() is not ksFeature root)
            {
                return "корень дерева не получен";
            }

            if (root.SubFeatureCollection(true, false) is not ksFeatureCollection tree)
            {
                return "коллекция дерева не получена";
            }

            var names = new List<string>();
            for (var i = 0; i < tree.GetCount(); i++)
            {
                names.Add("[" + i + "] " + Api5.RuntimeName(tree.GetByIndex(i)));
            }

            return names.Count == 0 ? "дерево пусто" : string.Join("; ", names);
        }
        catch (Exception ex)
        {
            return "дерево бросило " + ex.GetType().Name;
        }
    }

    private static double? Diff(double? before, double? after) =>
        before is not null && after is not null ? after - before : null;

    private static IPoint3D? MakePoint(IModelContainer container, double x, double y, double z)
    {
        try
        {
            if (container.Points3D is not { } points || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Update();
            return point;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ksDocument3D NewPart(out ksPart part)
    {
        var doc = (ksDocument3D)_app.Document3D();
        doc.Create(true, true);
        if (doc.GetPart(-1) is not ksPart created)
        {
            throw new InvalidOperationException("деталь не получена");
        }

        part = created;
        return doc;
    }

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Not this step's subject.
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("B.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        // `Quit()` is asynchronous: a single sample would report a teardown race as a leak.
        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var left = NewProcesses();
        if (waited > 0)
        {
            step.Observe("процессы ушли за " + waited + " мс после Quit()");
        }

        step.Observe("своих процессов до запуска: " + _pidsBefore.Count
            + ", новых после: " + left.Count + (left.Count == 0 ? string.Empty : " (" + string.Join(", ", left) + ")"));
        if (_ownPid != 0 && left.Contains(_ownPid))
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit() и не ушёл за " + (limitMs / 1000) + " с");
            return;
        }

        step.Pass("новых процессов не осталось");
    }

    private List<int> NewProcesses()
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = process.Id;
            process.Dispose();
            if (!_pidsBefore.Contains(pid))
            {
                found.Add(pid);
            }
        }

        return found;
    }
}
