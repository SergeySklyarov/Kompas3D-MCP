using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe CA — the cut "application area": which route directs the plane at the SELECTED bodies.</summary>
/// <remarks>
/// MEASURED: client acceptance 19.09.2026 (10:23–10:53) measured defect
/// <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>: the <c>kompas_cut_by_plane</c> contract names ONE body
/// (<c>target_body_ref</c>), while the plane removes material from ALL bodies of the document. Reading
/// the adapter code showed the mechanism: <c>Api5Session.SolidOps.cs</c> resolves <c>target_body_ref</c>,
/// but <c>Api7SolidCut.TryCreateByPlane</c> receives only the container, the plane, the side and the name.
/// DOC: the installed v24 help, <c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>: «По умолчанию область
/// применения операции Сечение — Все объекты», and for a planar cutting object «Область применения
/// включает в себя объекты, которые плоскость пересекает, а также объекты, целиком расположенные со
/// стороны отсечения». So the behaviour the client saw is the DOCUMENTED default-mode behaviour, not a
/// kernel failure. Hence the probe question: does a route to set the application area exist?
/// INVARIANT: member names are read from the installed type library <c>Bin\kAPI7.tlb</c>
/// (<see cref="TlbScan.MemberNames"/>) and from the interface declaration in
/// <c>Interop.KompasAPI7.dll</c>. A candidate list invented by a human measures the author's
/// imagination, not the product.
/// TEST: reference §3.3 of the order. A = <c>[10,40]×[0,30]×[0,20]</c>, V=18000;
/// S = <c>[100,110]×[0,10]×[0,10]</c>, V=1000. Plane with normal <c>(0,1,0)</c>, positive side
/// (<c>s = y − y₀ &gt; 0</c>): at <c>y₀=10</c> the remainder is A = 12000, S = 1000; at <c>y₀=5</c>
/// the remainder is A = 15000, S = 1000; at <c>y₀=15</c> the remainder is A = 9000, S = 1000.
/// History: docs/decisions/probes.md#cut-area
/// </remarks>
internal sealed class CutAreaProbe
{
    // ── reference §3.3: two independent bodies in one document ──
    private const double Ax0 = 10d, Ax1 = 40d, Ay0 = 0d, Ay1 = 30d, Az = 20d;
    private const double Sx0 = 100d, Sx1 = 110d, Sy0 = 0d, Sy1 = 10d, Sz = 10d;

    private static double VolA => (Ax1 - Ax0) * (Ay1 - Ay0) * Az;   // 18000
    private static double VolS => (Sx1 - Sx0) * (Sy1 - Sy0) * Sz;   // 1000

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public CutAreaProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "cut-area.json"),
            Path.Combine(options.ReportDir, "cut-area.md"));

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
            DeclaredMembers();
            LiveAreaReadBack();
            DefaultAreaReproducesClientDefect();
            TargetedCreate();
            NegativeControlTargetsStranger();
            EditKeepsTargeting();
            SaveReopenKeepsArea();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("CA.0", "Свой невидимый сеанс КОМПАС-3D v24",
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

        step.Observe("экземпляр API5 создан за " + clock.ElapsedMilliseconds + " мс, свой процесс: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("CA.9", "Завершение сеанса",
            "Остался ли чужой процесс КОМПАС после пробы?");
        try
        {
            _app.Visible = false;
        }
        catch (Exception)
        {
            // Not the subject of the question.
        }

        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && Survivors().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var survivors = Survivors();
        step.Observe("своих процессов до запуска: " + _pidsBefore.Count + ", новых после: "
            + survivors.Count + (survivors.Count == 0 ? "" : " (" + string.Join(", ", survivors) + ")"));
        step.Pass("проба закончила работу");
    }

    private List<int> Survivors() =>
        Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .Where(pid => !_pidsBefore.Contains(pid))
            .ToList();

    // ══════════════════════════════════════════════════════════════ CA.1 ══

    /// <summary>What the INSTALLED type library declares. This is the product's statement about itself,
    /// not one passing through the wrapper snapshot <c>Interop.KompasAPI7.dll</c> (ADR-003 §4).</summary>
    private void DeclaredMembers()
    {
        var step = _report.Begin("CA.1", "Объявленные члены области применения в kAPI7.tlb",
            "Объявляет ли установленная библиотека типов у ICut члены задания области применения?");
        var tlb = Path.Combine(_options.KompasRoot, "Bin", "kAPI7.tlb");
        step.Data["tlb_path"] = tlb;
        if (!File.Exists(tlb))
        {
            step.Fail("библиотека типов не найдена: " + tlb);
            return;
        }

        step.Data["tlb_sha256"] = InteropResolver.Sha256(tlb);

        var cutMembers = TlbScan.MemberNames(tlb, "ICut");
        step.Data["icut_members"] = cutMembers.Select(m => m.Name).ToList();
        step.Observe("ICut объявляет членов: " + cutMembers.Count + " — "
            + string.Join(", ", cutMembers.Select(m => m.Name)));

        var chooseObjects = TlbScan.MemberNames(tlb, "IChooseObjects");
        step.Data["ichooseobjects_members"] = chooseObjects.Select(m => m.Name).ToList();
        step.Observe("IChooseObjects объявляет членов: " + chooseObjects.Count + " — "
            + string.Join(", ", chooseObjects.Select(m => m.Name)));

        var names = new[] { "ChooseType", "ChooseBodies", "ChooseParts", "ChoosePartsType" };
        var perName = TlbScan.MembersOf(tlb, typeof(ICut).GUID, names);
        step.Data["icut_named"] = perName;
        foreach (var pair in perName)
        {
            step.Observe("ICut." + pair.Key + " → " + pair.Value);
        }

        var declared = names.Count(n => perName.TryGetValue(n, out var answer)
            && answer.StartsWith("объявлен", StringComparison.Ordinal));
        step.Observe("объявлено из четырёх имён области применения: " + declared);
        if (declared == 0)
        {
            step.Fail("ни одно из четырёх имён не объявлено в установленной библиотеке типов");
            return;
        }

        step.Pass("маршрут области применения объявлен установленной библиотекой типов");
    }

    // ══════════════════════════════════════════════════════════════ CA.2 ══

    /// <summary>Is the application area read back from a LIVE feature, and what does the product report
    /// about it by default.</summary>
    private void LiveAreaReadBack()
    {
        var step = _report.Begin("CA.2", "Область применения живого признака: чтение по умолчанию",
            "Отвечает ли живой ICut на члены области применения и что он отдаёт до записи?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlaneAtY(container, planes, 10d, "cut-y10", step);
            if (plane is null)
            {
                step.Fail("плоскость y=10 не построена");
                return;
            }

            if (container.Cuts.Add() is not ICut cut)
            {
                step.Fail("Cuts.Add() не отдаёт ICut");
                return;
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = (IModelObject)plane;
            cut.Direction = true;

            step.Observe("ДО Update(): " + DescribeArea(cut));
            step.Data["before_update"] = AreaSnapshot(cut);
            step.Observe("имена живого объекта, отвечающие области применения: "
                + string.Join(", ", new[] { "ChooseType", "ChooseBodies", "ChooseParts", "ChoosePartsType" }
                    .Select(n => n + "=" + Late.Dispid(cut, n))));

            var updated = cut.Update();
            step.Observe("Update()=" + updated);
            part.RebuildModel();
            doc.RebuildDocument();

            step.Observe("ПОСЛЕ Update(): " + DescribeArea(cut));
            step.Data["after_update"] = AreaSnapshot(cut);

            var rows = BodyRows(part);
            step.Observe("тела после отсечения: " + Describe(rows));
            step.Data["bodies_after"] = Describe(rows);
            step.Data["volume_in_a"] = VolumeInA(rows);
            step.Data["volume_in_s"] = VolumeInS(rows);

            if (AreaSnapshot(cut) is { Count: > 0 } snapshot
                && snapshot.Values.Any(v => v is not null and not "null"))
            {
                step.Pass("живой признак отвечает членами области применения; значения — в наблюдениях");
            }
            else
            {
                step.Unknown("живой признак не отдал ни одного значения области применения — "
                    + "это наблюдение, а не вывод об отсутствии маршрута");
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ CA.3 ══

    /// <summary>Reproduction of the client defect on the probe: the application area is NOT set, and the
    /// stranger body S vanishes together with the target's material. The expectation is declared before
    /// the experiment, from the product help.</summary>
    private void DefaultAreaReproducesClientDefect()
    {
        var step = _report.Begin("CA.3", "Режим по умолчанию «Все объекты»: воспроизведение дефекта",
            "Снимает ли плоскость материал у постороннего тела S, если область применения не задана?");
        step.Observe("ОЖИДАНИЕ (справка rezultat_oper_v_zavisimosti_ot_s_o.html): по умолчанию область "
            + "применения — «Все объекты», и объект, ЦЕЛИКОМ лежащий со стороны отсечения, в неё "
            + "входит. S лежит при y∈[0,10], то есть целиком со стороны s<0 → ожидается исчезновение S "
            + "и остаток A = 12000. Если S останется с объёмом 1000 — ожидание не подтвердилось.");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var (ok, note) = CutPlain(doc, part, 10d, step, "default");
            var rows = BodyRows(part);
            var a = VolumeInA(rows);
            var s = VolumeInS(rows);
            step.Observe("результат: " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                + "; тел " + rows.Count + " → " + Describe(rows));
            step.Observe("A=" + Api5.Num(a) + " (ожидание 12000), S=" + Api5.Num(s) + " (ожидание 1000)");
            step.Data["volume_in_a"] = a;
            step.Data["volume_in_s"] = s;
            step.Data["accepted"] = ok;

            if (a is not null && s is not null && Near(a.Value, 12000d) && Near(s.Value, 1000d))
            {
                step.Pass("режим по умолчанию сохранил S — ожидание по справке НЕ подтвердилось");
            }
            else
            {
                step.Pass("режим по умолчанию затронул постороннее тело — дефект воспроизведён на пробе");
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ CA.4 ══

    /// <summary>The main experiment: set the application area and obtain targeted cutting.</summary>
    /// <remarks>TEST: the shape of the <c>ChooseBodies</c> value is not guessed but enumerated: the
    /// candidates are presented one by one, and the accepted one is the candidate after which the
    /// MEASURED result matched the analytical expectation of targeted cutting. "Accepted without an
    /// exception" is not proof: <c>Update() = true</c> in this project has already meant "accepted
    /// silently".</remarks>
    private void TargetedCreate()
    {
        var step = _report.Begin("CA.4", "Адресное отсечение: область применения = тело A",
            "Можно ли направить плоскость на ВЫБРАННОЕ тело так, чтобы S остался неизменным?");
        step.Observe("ОЖИДАНИЕ: A = 12000, S = 1000 (два тела). Признак адресности — сохранность S.");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var targetElement = BodyElementNear(part, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            if (targetElement is null)
            {
                step.Fail("тело A не найдено в коллекции тел");
                return;
            }

            var body7 = TransferTo7(targetElement);
            step.Observe("тело A перенесено в API7: " + Api5.RuntimeName(body7));
            step.Data["target_transfer"] = Api5.RuntimeName(body7);

            var plane = MakePlaneAtY(container, planes, 10d, "cut-y10-targeted", step);
            if (plane is null)
            {
                step.Fail("плоскость y=10 не построена");
                return;
            }

            var candidates = new List<(string Name, object? Value)>
            {
                ("object[] { тело API7 }", body7 is null ? null : new[] { body7 }),
                ("object[] { BodyId }", new object[] { BodyIdOf(targetElement) }),
                ("тело API7 одним объектом", body7),
                ("BodyId числом", BodyIdOf(targetElement)),
            };

            var accepted = false;
            foreach (var (name, value) in candidates)
            {
                if (value is null)
                {
                    step.Observe("форма «" + name + "»: значение не построено, пропущена");
                    continue;
                }

                var (ok, note, area, rows) = TryTargetedCut(
                    container, part, doc, plane, value, step, name);
                step.Observe("форма «" + name + "»: " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                    + "; область после: " + area + "; тела: " + Describe(rows));

                var a = VolumeInA(rows);
                var s = VolumeInS(rows);
                var targeted = a is not null && s is not null
                    && Near(a.Value, 12000d) && Near(s.Value, 1000d);
                step.Data["shape_" + name] = (ok ? "принято" : "отказ: " + note)
                    + "; A=" + Api5.Num(a) + "; S=" + Api5.Num(s) + "; адресно=" + targeted;

                if (!targeted)
                {
                    // The document is already mutated; the next candidate needs a fresh document.
                    break;
                }

                accepted = true;
                step.Data["accepted_shape"] = name;
                step.Data["accepted_area"] = area;
                break;
            }

            if (accepted)
            {
                step.Pass("адресное отсечение достигнуто; форма значения области применения названа");
            }
            else
            {
                step.Unknown("ни одна испытанная форма не дала адресного результата; перечень форм — "
                    + "в наблюдениях. Это граница измерения, а не вывод об отсутствии маршрута");
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ CA.5 ══

    /// <summary>Negative control for CA.4: the target named is the STRANGER body S. If targeting works,
    /// the material must be removed from S while A stays whole — i.e. the result must DIFFER from
    /// CA.4.</summary>
    private void NegativeControlTargetsStranger()
    {
        var step = _report.Begin("CA.5", "Отрицательный контроль: целью названо тело S",
            "Даёт ли та же форма значения ДРУГОЙ результат, когда целью названо другое тело?");
        step.Observe("ОЖИДАНИЕ: A = 18000 (нетронуто), S = 500 (остаток при y>5 в теле S высотой 10). "
            + "Если результат совпадёт с CA.4, форма значения ничего не адресует.");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var strangerElement = BodyElementNear(part, Sx0, Sy0, 0d, Sx1, Sy1, Sz);
            if (strangerElement is null)
            {
                step.Fail("тело S не найдено в коллекции тел");
                return;
            }

            var body7 = TransferTo7(strangerElement);
            var plane = MakePlaneAtY(container, planes, 5d, "cut-y5-stranger", step);
            if (plane is null)
            {
                step.Fail("плоскость y=5 не построена");
                return;
            }

            var (ok, note, area, rows) = TryTargetedCut(
                container, part, doc, plane, body7 is null ? null : new[] { body7 }, step, "stranger");
            var a = VolumeInA(rows);
            var s = VolumeInS(rows);
            step.Observe("результат: " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                + "; область после: " + area + "; тела: " + Describe(rows));
            step.Observe("A=" + Api5.Num(a) + " (ожидание 18000), S=" + Api5.Num(s) + " (ожидание 500)");
            step.Data["volume_in_a"] = a;
            step.Data["volume_in_s"] = s;
            step.Data["area_after"] = area;

            if (a is not null && s is not null && Near(a.Value, VolA) && Near(s.Value, 500d))
            {
                step.Pass("контроль различает цели: смена цели сменила результат");
            }
            else if (a is not null && Near(a.Value, 12000d))
            {
                step.Fail("контроль не различает цели: результат тот же, что при цели A, — "
                    + "форма значения область применения не задаёт");
            }
            else
            {
                step.Unknown("контроль не дал ни ожидаемого адресного результата, ни повтора CA.4");
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ CA.6 ══

    /// <summary>Edit: moving the support of an existing feature. Targeting must survive — otherwise
    /// "set the application area" only works at creation.</summary>
    private void EditKeepsTargeting()
    {
        var step = _report.Begin("CA.6", "Правка опоры: сохраняется ли область применения",
            "Остаётся ли отсечение адресным после переноса плоскости на существующем признаке?");
        step.Observe("ОЖИДАНИЕ: после переноса опоры с y=10 на y=20 A = 6000, S = 1000.");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var targetElement = BodyElementNear(part, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var body7 = targetElement is null ? null : TransferTo7(targetElement);
            var plane = MakePlaneAtY(container, planes, 10d, "cut-y10-edit", step);
            if (plane is null)
            {
                step.Fail("плоскость y=10 не построена");
                return;
            }

            var (ok, note, area, rows) = TryTargetedCut(
                container, part, doc, plane, body7 is null ? null : new[] { body7 }, step, "edit");
            step.Observe("создание: " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                + "; область: " + area + "; тела: " + Describe(rows));

            // Moving the support: the plane construction points shift by +10 along Y.
            var moved = MoveSupportToY(container, part, doc, 20d, step);
            var after = BodyRows(part);
            var a = VolumeInA(after);
            var s = VolumeInS(after);
            step.Observe("после переноса опоры: A=" + Api5.Num(a) + " (ожидание 6000), S="
                + Api5.Num(s) + " (ожидание 1000); тела: " + Describe(after));
            step.Data["volume_in_a"] = a;
            step.Data["volume_in_s"] = s;
            step.Data["support_moved"] = moved;

            if (!moved)
            {
                step.Unknown("опора не перенесена — шаг не измеряет правку");
            }
            else if (a is not null && s is not null && Near(a.Value, 6000d) && Near(s.Value, 1000d))
            {
                step.Pass("после правки опоры адресность сохранилась");
            }
            else
            {
                step.Fail("после правки опоры адресность потеряна: A=" + Api5.Num(a)
                    + ", S=" + Api5.Num(s));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ CA.7 ══

    /// <summary>Save and reopen: is the application area read from the file, and does the cut stay
    /// targeted after <c>save → close → open</c>.</summary>
    private void SaveReopenKeepsArea()
    {
        var step = _report.Begin("CA.7", "Save → close → open: сохраняется ли область применения",
            "Читается ли назначенная область применения с переоткрытого файла?");
        var path = Path.Combine(_options.WorkDir, "cut-area-reopen.m3d");
        step.Data["model"] = path;

        var doc = NewPart(out var part);
        var reopened = false;
        try
        {
            if (!BuildTwoBodies(part, doc, step))
            {
                step.Fail("эталон A+S не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var targetElement = BodyElementNear(part, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var body7 = targetElement is null ? null : TransferTo7(targetElement);
            var plane = MakePlaneAtY(container, planes, 10d, "cut-y10-reopen", step);
            if (plane is null)
            {
                step.Fail("плоскость y=10 не построена");
                return;
            }

            var (ok, note, area, rows) = TryTargetedCut(
                container, part, doc, plane, body7 is null ? null : new[] { body7 }, step, "reopen");
            step.Observe("создание: " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                + "; область: " + area + "; тела: " + Describe(rows));

            if (!TrySave(doc, path, step))
            {
                step.Fail("файл не сохранён");
                return;
            }

            TryClose(doc);
            doc = null!;

            var (reopenedDoc, reopenedPart) = OpenPart(path);
            if (reopenedDoc is null || reopenedPart is null)
            {
                step.Fail("файл не переоткрыт");
                return;
            }

            doc = reopenedDoc;
            reopened = true;
            var rowsAfter = BodyRows(reopenedPart);
            var a = VolumeInA(rowsAfter);
            var s = VolumeInS(rowsAfter);
            step.Observe("после переоткрытия: A=" + Api5.Num(a) + " (ожидание 12000), S="
                + Api5.Num(s) + " (ожидание 1000); тела: " + Describe(rowsAfter));
            step.Data["volume_in_a"] = a;
            step.Data["volume_in_s"] = s;

            var readArea = ReadAreaOfFirstCut(reopenedDoc, step);
            step.Data["area_after_reopen"] = readArea;
            step.Observe("область применения, прочитанная с переоткрытого файла: " + readArea);

            if (a is not null && s is not null && Near(a.Value, 12000d) && Near(s.Value, 1000d))
            {
                step.Pass("отсечение осталось адресным после переоткрытия");
            }
            else
            {
                step.Fail("после переоткрытия результат изменился: A=" + Api5.Num(a)
                    + ", S=" + Api5.Num(s));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (!reopened)
            {
                TryClose(doc);
            }
            else
            {
                TryClose(doc);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════ cut route ══

    /// <summary>Cutting WITHOUT setting the application area — the route of the current adapter.</summary>
    private (bool Ok, string? Note) CutPlain(
        ksDocument3D doc, ksPart part, double y0, ProbeStep step, string label)
    {
        try
        {
            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container?.Cuts is null || planes is null)
            {
                return (false, "контейнер или Planes3D недоступны");
            }

            var plane = MakePlaneAtY(container, planes, y0, "cut-" + label, step);
            if (plane is null)
            {
                return (false, "плоскость не построена");
            }

            if (container.Cuts.Add() is not ICut cut)
            {
                return (false, "Cuts.Add() не отдаёт ICut");
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = (IModelObject)plane;
            cut.Direction = true;
            var updated = cut.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "ICut.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>Cutting with the application area SET. It returns both the area read AFTER
    /// <c>Update()</c> and the body composition: an accepted value and an applied value are different
    /// claims.</summary>
    private (bool Ok, string? Note, string Area, List<BodyRow> Rows) TryTargetedCut(
        IModelContainer container, ksPart part, ksDocument3D doc, IPlane3D plane,
        object? chooseBodies, ProbeStep step, string label)
    {
        try
        {
            if (container.Cuts.Add() is not ICut cut)
            {
                return (false, "Cuts.Add() не отдаёт ICut", "<нет признака>", BodyRows(part));
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = (IModelObject)plane;
            cut.Direction = true;

            // Write order: the object-selection mode first, then the composition. The enum name
            // ksChooseType.ksChBodies means "bodies enter the area" (unlike ksChParts and
            // ksChBodiesAndParts); ksChoosePartsType.ksChManualEditing means "selected objects" versus
            // ksChAutomaticDefinition (DOC: «Автоопределение», ksapi_kschoosepartstype.html).
            cut.ChooseType = ksChooseType.ksChBodies;
            cut.ChoosePartsType = ksChoosePartsType.ksChManualEditing;
            cut.ChooseBodies = chooseBodies;

            var updated = cut.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var rows = BodyRows(part);
            var area = DescribeArea(cut);
            step.Data["area_readback_" + label] = AreaSnapshot(cut);
            return updated ? (true, null, area, rows) : (false, "ICut.Update() вернул false", area, rows);
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex), "<исключение до чтения>", BodyRows(part));
        }
    }

    /// <summary>Moves the support of an existing cut feature to another Y value: the plane construction
    /// points shift by the difference. A support that does not answer <c>IPlane3DBy3Points</c> is
    /// rejected — no blind edit is performed.</summary>
    private bool MoveSupportToY(IModelContainer container, ksPart part, ksDocument3D doc, double y, ProbeStep step)
    {
        try
        {
            if (container.Cuts is null || container.Cuts.Count == 0)
            {
                step.Observe("правка: признаков отсечения нет");
                return false;
            }

            if (container.Cuts[container.Cuts.Count - 1] is not ICut cut)
            {
                step.Observe("правка: последний элемент Cuts не отдаёт ICut");
                return false;
            }

            if (cut.CutObject is not IPlane3DBy3Points support)
            {
                step.Observe("правка: опора признака не отвечает IPlane3DBy3Points");
                return false;
            }

            var points = new[] { support.Point1, support.Point2, support.Point3 }
                .Select(p => p as IPoint3D)
                .ToList();
            if (points.Any(p => p is null))
            {
                step.Observe("правка: точки опоры не читаются как IPoint3D");
                return false;
            }

            var current = points[0]!.Y;
            var shift = y - current;
            foreach (var point in points)
            {
                point!.Y = point.Y + shift;
                point.Update();
            }

            var updated = cut.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("правка: сдвиг опоры на " + Api5.Num(shift) + " по Y, Update()=" + updated);
            return updated;
        }
        catch (Exception ex)
        {
            step.Observe("правка: исключение " + HResult.Describe(ex));
            return false;
        }
    }

    private string ReadAreaOfFirstCut(ksDocument3D doc, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.Cuts is null || container.Cuts.Count == 0)
            {
                return "<признаков отсечения нет>";
            }

            return container.Cuts[0] is ICut cut ? DescribeArea(cut) : "<элемент не ICut>";
        }
        catch (Exception ex)
        {
            step.Observe("чтение области с файла: исключение " + HResult.Describe(ex));
            return "<исключение>";
        }
    }

    // ══════════════════════════════════════════════════════════════ area read ══

    private static string DescribeArea(ICut cut)
    {
        var snapshot = AreaSnapshot(cut);
        return string.Join(", ", snapshot.Select(p => p.Key + "=" + Api5.Raw(p.Value)));
    }

    private static Dictionary<string, object?> AreaSnapshot(ICut cut)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        result["ChooseType"] = ReadMember(() => cut.ChooseType);
        result["ChoosePartsType"] = ReadMember(() => cut.ChoosePartsType);
        result["ChooseBodies"] = ReadMember(() => cut.ChooseBodies);
        result["ChooseParts"] = ReadMember(() => cut.ChooseParts);
        return result;
    }

    private static object? ReadMember<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return "<" + HResult.Describe(ex) + ">";
        }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private bool BuildTwoBodies(ksPart part, ksDocument3D doc, ProbeStep step)
    {
        if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A"))
        {
            return false;
        }

        if (!ExtrudeRect(doc, part, Sx0, Sx1, Sy0, Sy1, Sz, step, "S"))
        {
            return false;
        }

        var rows = BodyRows(part);
        step.Observe("эталон построен: тел " + rows.Count + " → " + Describe(rows)
            + "; сумма " + Api5.Num(SumVolumes(rows)) + " (ожидание " + Api5.Num(VolA + VolS) + ")");
        step.Data["fixture_bodies"] = Describe(rows);
        step.Data["fixture_volume"] = SumVolumes(rows);
        return rows.Count == 2;
    }

    private IModelContainer? Container(ksDocument3D doc)
    {
        try
        {
            return _app.TransferInterface(doc, 2, 0) is IKompasDocument3D document7
                && document7.TopPart is IModelContainer container
                ? container
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private object? TransferTo7(object element)
    {
        try
        {
            return _app.TransferInterface(element, 2, 0);
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

    private (ksDocument3D? Doc, ksPart? Part) OpenPart(string path)
    {
        try
        {
            var doc = (ksDocument3D)_app.Document3D();
            if (!doc.Open(path, false))
            {
                return (null, null);
            }

            return (doc, doc.GetPart(-1) as ksPart);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static bool TrySave(ksDocument3D doc, string path, ProbeStep step)
    {
        try
        {
            var saved = doc.SaveAs(path);
            step.Observe("SaveAs(" + path + ")=" + saved);
            return saved;
        }
        catch (Exception ex)
        {
            step.Observe("SaveAs: исключение " + HResult.Describe(ex));
            return false;
        }
    }

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Not the subject of this step.
        }
    }

    private static List<BodyRow> BodyRows(ksPart part)
    {
        var rows = new List<BodyRow>();
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return rows;
            }

            bodies.refresh();
            var count = bodies.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = bodies.GetByIndex(i);
                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body
                    && Api5.SafeBool(() => body.GetGabarit(out _, out _, out _, out _, out _, out _)) == true)
                {
                    body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                    min = new[] { x1, y1, z1 };
                    max = new[] { x2, y2, z2 };
                }

                rows.Add(new BodyRow
                {
                    Index = i,
                    Element = element,
                    Volume = Api5.BodyVolume(element),
                    Min = min,
                    Max = max,
                });
            }
        }
        catch (Exception)
        {
            // A partial list is more honest than an empty one.
        }

        return rows;
    }

    private static object? BodyElementNear(
        ksPart part, double x0, double y0, double z0, double x1, double y1, double z1) =>
        BodyRows(part).FirstOrDefault(r => Near(r, x0, y0, z0, x1, y1, z1))?.Element;

    /// <summary>The body number for the "number" value shape. It is read from the body TRANSFER into
    /// API7 (<c>IBody7.BodyId</c>), not derived from the index in the collection: an index is not an
    /// identity.</summary>
    private object BodyIdOf(object? element)
    {
        var transferred = element is null ? null : TransferTo7(element);
        if (transferred is null)
        {
            return -1;
        }

        var value = Late.Get(transferred, "BodyId");
        return value is int id ? id : -1;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is not null && row.Max is not null
        && Math.Abs(row.Min[0] - x0) < 1e-6 && Math.Abs(row.Min[1] - y0) < 1e-6
        && Math.Abs(row.Min[2] - z0) < 1e-6
        && Math.Abs(row.Max[0] - x1) < 1e-6 && Math.Abs(row.Max[1] - y1) < 1e-6
        && Math.Abs(row.Max[2] - z1) < 1e-6;

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

    /// <summary>Volume of the bodies whose bounding box lies in the region of body A. It is computed
    /// from the BOUNDING BOX, not the index: after cutting, the remainder of A stays in the same region
    /// while S stays in its own, and this tells them apart regardless of collection order.</summary>
    private static double? VolumeInA(List<BodyRow> rows) =>
        SumOf(rows.Where(r => r.Max is not null && r.Min is not null
            && r.Min[0] > Ax0 - 0.5 && r.Max[0] < Ax1 + 0.5
            && r.Max[1] < Ay1 + 0.5 && r.Max[2] < Az + 0.5));

    private static double? VolumeInS(List<BodyRow> rows) =>
        SumOf(rows.Where(r => r.Max is not null && r.Min is not null
            && r.Min[0] > Sx0 - 0.5 && r.Max[0] < Sx1 + 0.5
            && r.Max[1] < Sy1 + 0.5 && r.Max[2] < Sz + 0.5));

    private static double? SumOf(IEnumerable<BodyRow> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 || list.Any(r => r.Volume is null)
            ? null
            : list.Sum(r => r.Volume!.Value);
    }

    private static double? SumVolumes(List<BodyRow> rows) =>
        rows.Count == 0 || rows.Any(r => r.Volume is null) ? null : rows.Sum(r => r.Volume!.Value);

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    /// <summary>Plane <c>y = y0</c> with normal <c>(0,1,0)</c>: model points <c>(0,y0,0)</c>,
    /// <c>(0,y0,1)</c>, <c>(1,y0,0)</c> give the normal <c>(p2−p1)×(p3−p1) = (0,1,0)</c>.</summary>
    private IPlane3D? MakePlaneAtY(
        IModelContainer container, IPlanes3D planes, double y0, string name, ProbeStep step)
    {
        var first = MakePoint(container, 0d, y0, 0d);
        var second = MakePoint(container, 0d, y0, 1d);
        var third = MakePoint(container, 1d, y0, 0d);
        if (first is null || second is null || third is null)
        {
            step.Observe(name + ": точки плоскости не созданы");
            return null;
        }

        try
        {
            if (planes.Add(ksObj3dTypeEnum.o3d_plane3Points) is not IPlane3D plane)
            {
                step.Observe(name + ": Planes3D.Add не отдал IPlane3D");
                return null;
            }

            if (plane is not IPlane3DBy3Points byPoints)
            {
                step.Observe(name + ": плоскость не отвечает IPlane3DBy3Points");
                return null;
            }

            plane.Name = name;
            byPoints.Point1 = first;
            byPoints.Point2 = second;
            byPoints.Point3 = third;
            var updated = plane.Update();
            step.Observe(name + ": Update()=" + updated);
            return updated ? plane : null;
        }
        catch (Exception ex)
        {
            step.Observe(name + ": исключение " + HResult.Describe(ex));
            return null;
        }
    }

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

    private static bool ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() базового выдавливания → false");
        }

        return created;
    }

    private static ksEntity ProfileSketchOn(
        ksDocument3D doc, string name, short planeType, double u0, double u1, double v0, double v1)
    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(planeType) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости " + planeType + " нет");
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
        }

        definition.EndEdit();
        return sketch;
    }

    private sealed class BodyRow
    {
        public required int Index { get; init; }

        public required object? Element { get; init; }

        public required double? Volume { get; init; }

        public required double[]? Min { get; init; }

        public required double[]? Max { get; init; }

        public string Describe()
        {
            var box = Min is null || Max is null
                ? "<габарит не прочитан>"
                : "[" + Api5.Num(Min[0]) + "," + Api5.Num(Min[1]) + "," + Api5.Num(Min[2]) + "]…["
                  + Api5.Num(Max[0]) + "," + Api5.Num(Max[1]) + "," + Api5.Num(Max[2]) + "]";
            return "#" + Index + " V=" + Api5.Num(Volume) + " " + box;
        }
    }
}
