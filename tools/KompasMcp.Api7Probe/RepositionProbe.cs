using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба B3 — перенос и поворот тела (SM-17.reposition) через API7 <c>IBodyRepositions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Открытый вопрос OQ-A19.</b> У <c>IBodyReposition</c> объявлен <c>Position</c> типа
/// <c>LocalCoordinateSystem</c> и, по библиотеке типов, только с геттером; вторая половина
/// <c>RepositionCentre</c> — точка смещения. Поэтому маршрут «перенести тело на вектор» не
/// очевиден: неизвестно ни то, каким членом записывается смещение, ни то, в каком порядке лежат
/// числа в матрице, если запись идёт через <c>InitByMatrix3D</c>.
/// </para>
/// <para>
/// <b>Отсутствие сеттера у свойства не доказывает невозможность записи</b> (правило проекта):
/// возвращённый объект может быть живым и изменяемым на месте. Здесь это проверяется перебором
/// маршрутов, причём КАЖДЫЙ маршрут идёт в СВОЁМ документе, а результат каждого называется
/// числом — иначе «маршрут работает» и «тело не сдвинулось» выглядят одинаково.
/// </para>
/// <para>
/// <b>Эталон §6.6.</b> Асимметричный брусок <c>T=[10,30]×[0,10]×[0,5]</c>, V=1000: перенос
/// <c>(7,−11,13)</c> → <c>[17,−11,13]…[37,−1,18]</c>; поворот +90° вокруг Z через начало координат →
/// <c>[−10,10,0]…[0,30,5]</c>; тот же поворот вокруг оси через <c>(5,0,0)</c> →
/// <c>[−5,5,0]…[5,25,5]</c>. Асимметрия важна: по симметричной детали знак и направление
/// преобразования неразличимы.
/// </para>
/// </remarks>
internal sealed class RepositionProbe
{
    private const double Tx0 = 10d, Tx1 = 30d, Ty0 = 0d, Ty1 = 10d, Tz = 5d;
    private const double StrangerX0 = 100d, StrangerX1 = 110d, StrangerY0 = 0d, StrangerY1 = 10d, StrangerZ = 10d;

    private static double VolumeT => (Tx1 - Tx0) * (Ty1 - Ty0) * Tz;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public RepositionProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "reposition.json"),
            Path.Combine(options.ReportDir, "reposition.md"));

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
            ReadPositionMembers();
            TranslationRoute();
            Translate();
            RotateAboutOrigin();
            RotateAboutAxisThroughPoint();
            EditExistingOperation();
            ReopenReadBack();
            ReadBackAfterWrite();
            TranslationVectorRoutes();
            ReopenedTranslationRead();
            RotationReadBack();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("RP.0", "Свой невидимый сеанс КОМПАС-3D v24",
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

    // ══════════════════════════════════════════════════════════════ routes ══

    /// <summary>
    /// Что за объект отдаёт <c>Position</c> и какие члены у него отвечают живьём. Чтение
    /// библиотеки типов уже показало <c>InitByMatrix3D</c> и <c>SetDisplacementByAxis</c>; здесь
    /// проверяется, что живой объект на них отвечает, а не только объявляет их.
    /// </summary>
    private void ReadPositionMembers()
    {
        var step = _report.Begin("RP.1", "Что такое IBodyReposition.Position живьём",
            "Отвечает ли возвращённый объект на InitByMatrix3D и SetDisplacementByAxis, и что он отдаёт на чтение?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: true))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not { } created)
            {
                step.Fail("BodyRepositions.Add() недоступен");
                return;
            }

            if (created is not IBodyReposition reposition)
            {
                step.Fail("BodyRepositions.Add() не отдаёт IBodyReposition");
                return;
            }

            step.Observe("Add() → " + Api5.RuntimeName(created));
            step.Observe("  CopyBoby по умолчанию: " + Api5.Raw(SafeBoolOf(() => reposition.CopyBoby)));
            step.Observe("  RepositionBody по умолчанию: " + Api5.RuntimeName(SafeObjectOf(() => reposition.RepositionBody)));
            step.Observe("  RepositionCentre по умолчанию: " + Api5.RuntimeName(SafeObjectOf(() => reposition.RepositionCentre)));

            var position = SafeObjectOf(() => reposition.Position);
            step.Observe("  Position → " + Api5.RuntimeName(position));
            if (position is not ILocalCoordinateSystem local)
            {
                step.Fail("Position не отвечает ILocalCoordinateSystem");
                return;
            }

            step.Observe("  Position.X/Y/Z: " + Api5.Raw(SafeDouble(() => local.X)) + " / "
                + Api5.Raw(SafeDouble(() => local.Y)) + " / " + Api5.Raw(SafeDouble(() => local.Z)));
            step.Observe("  Position.ParameterType: " + Api5.Raw(SafeEnum(() => local.ParameterType)));
            step.Observe("  Position.OrientationType: " + Api5.Raw(SafeEnum(() => local.OrientationType)));
            // Vector3D — индексированное по оси свойство (get_Vector3D(ksObj3dTypeEnum)); обычной
            // записью оно в C# не берётся (CS0856). Читаем тем же вопросом через явный метод
            // GetVector: он отвечает тремя числами и потому годится как наблюдение.
            step.Observe("  Position.GetVector(OX): " + DescribeVector(local, ksObj3dTypeEnum.o3d_axisOX));
            step.Observe("  Position.GetVector(OY): " + DescribeVector(local, ksObj3dTypeEnum.o3d_axisOY));
            step.Observe("  Position.GetVector(OZ): " + DescribeVector(local, ksObj3dTypeEnum.o3d_axisOZ));
            step.Observe("  Position.LocalCSParameters: " + Api5.RuntimeName(SafeObjectOf(() => local.LocalCSParameters)));
            step.Observe("  Position.Parameters: " + Api5.RuntimeName(SafeObjectOf(() => local.Parameters)));

            // Проба записи: пустая операция должна отвергнуться, иначе «принято» ничего не значит.
            var emptyUpdate = SafeBoolOf(() => reposition.Update());
            step.Observe("  Update() без цели и без Position: " + Api5.Raw(emptyUpdate));
            step.Data["empty_update"] = emptyUpdate;

            step.Pass("Position читается живьём и отвечает на члены, которых требует запись");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Каким членом записывается ПЕРЕНОС. Перебираются маршруты, и каждый идёт в своём документе:
    /// накопление от предыдущей попытки сделало бы сравнение бессмысленным. Порядок чисел в
    /// матрице — часть маршрута, поэтому обе раскладки названы отдельно.
    /// </summary>
    private void TranslationRoute()
    {
        var step = _report.Begin("RP.2", "Каким членом записывается перенос: перебор маршрутов",
            "Какой из маршрутов действительно ДВИГАЕТ тело на (7,−11,13)?");
        step.Observe("ожидание для каждого маршрута: габарит (17, −11, 13)…(37, −1, 18)");
        step.Observe("перенос выбран потому, что единичная матрица поворота симметрична: раскладка "
            + "строк/столбцов на нём неразличима, и он честно отвечает только на вопрос о переносе");

        // R1: InitByMatrix3D, оси, затем начало.
        AttemptRoute(step, "InitByMatrix3D: оси (3×3), затем начало",
            reposition => reposition.Position.InitByMatrix3D(Matrix(Identity(), new[] { 7d, -11d, 13d })));

        // R2: InitByMatrix3D, начало, затем оси.
        AttemptRoute(step, "InitByMatrix3D: начало, затем оси (3×3)",
            reposition => reposition.Position.InitByMatrix3D(MatrixOriginFirst(Identity(), new[] { 7d, -11d, 13d })));

        // R3: SetDisplacementByAxis по каждой оси. Имена осей взяты из перечисления, а не угаданы:
        // в ksObj3dTypeEnum это o3d_axisOX=71 / o3d_axisOY=72 / o3d_axisOZ=73 (того же перечисления
        // требует и GetVector, и индексатор Vector3D).
        AttemptRoute(step, "SetDisplacementByAxis(OX,7), (OY,−11), (OZ,13)",
            reposition =>
            {
                reposition.Position.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOX, 7d);
                reposition.Position.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOY, -11d);
                reposition.Position.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOZ, 13d);
            });

        // R4: матрица 16 чисел — на случай, если ядро ждёт однородную 4×4.
        AttemptRoute(step, "InitByMatrix3D: однородная 4×4 (16 чисел)",
            reposition => reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), new[] { 7d, -11d, 13d })));

        step.Pass("маршруты перебраны раздельно; какой из них двинул тело — в наблюдениях");
    }

    private void AttemptRoute(ProbeStep step, string label, Action<IBodyReposition> configure)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Observe(label + ": брусок не построен");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Observe(label + ": брусок не опознан → " + Describe(before));
                return;
            }

            var (ok, note) = ApplyReposition(doc, part, target, configure, step);
            var after = BodyRows(part);
            var moved = after.FirstOrDefault(r => r.Min is not null && Math.Abs(r.Min[0] - (Tx0 + 7d)) < 1e-6);
            var verdict = !ok ? "ОТКАЗ (" + note + ")"
                : moved is not null && Near(moved, 17d, -11d, 13d, 37d, -1d, 18d) ? "ДВИНУЛ КАК ОЖИДАЛОСЬ"
                : "принят, но положение " + (after.Count == 0 ? "<тел нет>" : Describe(after));
            step.Observe(label + " → " + verdict);
            step.Data["route_" + label] = verdict;
        }
        catch (Exception ex)
        {
            step.Observe(label + " → исключение " + HResult.Describe(ex));
            step.Data["route_" + label] = "исключение " + HResult.Describe(ex);
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ cases ══

    private void Translate()
    {
        var step = _report.Begin("RP.3", "Перенос тела на вектор (7,−11,13)",
            "Меняет ли перенос только положение, сохраняя объём, число тел и постороннее тело?");
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): T=" + Box(Tx0, Ty0, 0d, Tx1, Ty1, Tz)
            + ", V=" + Api5.Num(VolumeT) + " → габарит (17, −11, 13)…(37, −1, 18), V=1000, тело одно");

        RunMoveCase(step, "перенос (7,−11,13)",
            reposition => reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), new[] { 7d, -11d, 13d })),
            1000d, new[] { 17d, -11d, 13d }, new[] { 37d, -1d, 18d });
    }

    private void RotateAboutOrigin()
    {
        var step = _report.Begin("RP.4", "Поворот +90° вокруг Z через начало координат",
            "Совпадает ли результат с правым правилом: x∈[−10,0], y∈[10,30]?");
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): (x,y) → (−y,x), значит габарит "
            + "(−10, 10, 0)…(0, 30, 5), V=1000");
        step.Observe("матрица поворота на +90° вокруг Z: X-ось образа (0,1,0), Y-ось образа (−1,0,0)");
        step.Observe("ВТОРАЯ ГИПОТЕЗА (транспонированная раскладка, (x,y) → (y,−x)): габарит "
            + "(0, −30, 0)…(10, −10, 5) — прибор обязан назвать, какая из двух совпала");

        RunMoveCase(step, "поворот +90° вокруг Z через начало",
            reposition => reposition.Position.InitByMatrix3D(RotationZ(90d, 0d, 0d)),
            1000d, new[] { -10d, 10d, 0d }, new[] { 0d, 30d, 5d },
            "правое правило: (x,y) → (−y,x)",
            (new[] { 0d, -30d, 0d }, new[] { 10d, -10d, 5d }, "транспонированная раскладка: (x,y) → (y,−x)"));
    }

    private void RotateAboutAxisThroughPoint()
    {
        var step = _report.Begin("RP.5", "Поворот +90° вокруг Z через точку (5,0,0)",
            "Учитывается ли точка опоры: габарит (−5,5,0)…(5,25,5)?");
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): поворот вокруг оси через (5,0,0) даёт "
            + "(−5, 5, 0)…(5, 25, 5), V=1000");
        step.Observe("перенос при этом равен c − R·c = (5,0,0) − (0,5,0) = (5,−5,0)");
        step.Observe("ВТОРАЯ ГИПОТЕЗА (транспонированная раскладка): габарит (5, −25, 0)…(15, −5, 5)");

        RunMoveCase(step, "поворот +90° вокруг Z через (5,0,0)",
            reposition => reposition.Position.InitByMatrix3D(RotationZ(90d, 5d, 0d)),
            1000d, new[] { -5d, 5d, 0d }, new[] { 5d, 25d, 5d },
            "правое правило вокруг (5,0,0)",
            (new[] { 5d, -25d, 0d }, new[] { 15d, -5d, 5d }, "транспонированная раскладка вокруг (5,0,0)"));
    }

    private void RunMoveCase(
        ProbeStep step, string label, Action<IBodyReposition> configure,
        double expectedVolume, double[] expectedMin, double[] expectedMax,
        string layoutLabel = "однородная 4×4, перенос в последней строке",
        (double[] Min, double[] Max, string Label)? alternate = null)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: true))
            {
                step.Fail(label + ": брусок не построен");
                return;
            }

            var before = BodyRows(part);
            step.Observe(label + ": тел до операции " + before.Count + " → " + Describe(before));
            var target = FindBody(before, Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            var stranger = FindBody(before, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ);
            if (target is null || stranger is null)
            {
                step.Fail(label + ": брусок или постороннее тело не опознаны");
                return;
            }

            var (ok, note) = ApplyReposition(doc, part, target, configure, step);
            if (!ok)
            {
                step.Fail(label + ": " + note);
                return;
            }

            var after = BodyRows(part);
            step.Observe(label + ": тел после операции " + after.Count + " → " + Describe(after));
            step.Data["bodies_after_" + label] = after.Count;
            step.Data["case_" + label] = Describe(after);

            if (after.Count != before.Count)
            {
                step.Fail(label + ": число тел " + before.Count + " → " + after.Count
                    + " — преобразование положения не должно ни создавать, ни потреблять тела");
                return;
            }

            var (layout, moved) = MatchLayout(after,
                (expectedMin, expectedMax, layoutLabel), alternate);
            if (moved is null)
            {
                step.Fail(label + ": тела с ожидаемым габаритом " + Point(expectedMin) + "…"
                    + Point(expectedMax) + " нет"
                    + (alternate is null
                        ? string.Empty
                        : ", и вторая гипотеза («" + alternate.Value.Label + "») тоже не совпала")
                    + ": " + Describe(after));
                return;
            }

            step.Data["layout_" + label] = layout;
            if (layout != layoutLabel)
            {
                step.Observe(label + ": совпала НЕ первая гипотеза, а «" + layout
                    + "» — направление преобразования противоположно объявленному");
            }

            var delta = Math.Abs((moved.Volume ?? double.NaN) - expectedVolume);
            step.Data["volume_" + label] = moved.Volume;
            if (double.IsNaN(delta) || delta > 0.01d)
            {
                step.Fail(label + ": объём " + Api5.Num(moved.Volume) + " против ожидаемого "
                    + Api5.Num(expectedVolume));
                return;
            }

            var strangerAfter = after.FirstOrDefault(r => Near(r, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ));
            if (strangerAfter is null || Math.Abs((strangerAfter.Volume ?? 0d) - 1000d) > 0.01d)
            {
                step.Fail(label + ": постороннее тело сдвинулось или исчезло: "
                    + (strangerAfter?.Describe() ?? "нет"));
                return;
            }

            step.Observe(label + ": постороннее тело не тронуто: " + strangerAfter.Describe());
            step.Pass(label + ": положение изменилось ровно как посчитано, объём и состав тел сохранены; "
                + "совпавшая гипотеза — " + layout);
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО признака: параметр задаётся относительно ИСХОДНЫХ входов, а не
    /// применяется повторно к текущему положению. Проверка: тот же вектор, записанный дважды,
    /// не должен накапливать смещение, а возврат к исходному вектору обязан вернуть исходное
    /// положение.
    /// </summary>
    private void EditExistingOperation()
    {
        var step = _report.Begin("RP.6", "Правка существующего признака: смещение не накапливается",
            "Даёт ли повторная запись того же вектора прежнее положение, а возврат вектора — исходное?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Fail("брусок не опознан");
                return;
            }

            var (ok, note) = ApplyReposition(doc, part, target,
                reposition => reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), new[] { 7d, -11d, 13d })),
                step);
            if (!ok)
            {
                step.Fail("первый перенос: " + note);
                return;
            }

            var first = BodyRows(part);
            step.Observe("после первого переноса: " + Describe(first));
            var featureIndex = RepositionCount(doc);
            step.Observe("признаков BodyRepositions: " + Api5.Raw(featureIndex));

            // Повторно записываем ТОТ ЖЕ вектор в ТОТ ЖЕ признак.
            var (ok2, note2) = WriteExisting(doc, part, 0, Matrix4x4(Identity(), new[] { 7d, -11d, 13d }), step);
            var second = BodyRows(part);
            step.Observe("после повторной записи того же вектора (" + (ok2 ? "принято" : "ОТКАЗ: " + note2)
                + "): " + Describe(second));
            step.Data["after_repeat"] = Describe(second);

            var (ok3, note3) = WriteExisting(doc, part, 0, Matrix4x4(Identity(), new double[] { 0d, 0d, 0d }), step);
            var third = BodyRows(part);
            step.Observe("после возврата вектора в ноль (" + (ok3 ? "принято" : "ОТКАЗ: " + note3)
                + "): " + Describe(third));
            step.Data["after_reset"] = Describe(third);
            step.Data["reposition_count"] = RepositionCount(doc);

            if (!ok2 || !ok3)
            {
                step.Fail("повторная запись в существующий признак не прошла");
                return;
            }

            var atExpected = second.FirstOrDefault(r => Near(r, 17d, -11d, 13d, 37d, -1d, 18d));
            if (atExpected is null)
            {
                // Две разные причины выглядят одинаково, если их не различить: тело могло НЕ
                // сдвинуться вовсе (маршрут правки не сработал) либо сдвинуться ДВАЖДЫ (параметр
                // применён к текущему положению). Это разные факты о продукте, и называть их надо
                // по отдельности — иначе «не двинулось» запишется как «накапливается».
                var doubled = second.FirstOrDefault(r => Near(r, 24d, -22d, 26d, 44d, -12d, 31d));
                step.Fail(doubled is not null
                    ? "повторная запись того же вектора сместила тело ЕЩЁ РАЗ (габарит удвоенного "
                        + "смещения) — параметр применяется к текущему положению, а не к исходным входам"
                    : "после повторной записи тело не оказалось на ожидаемом месте: " + Describe(second)
                        + " — сработал не маршрут правки, а не «накопление» смещения");
                return;
            }

            var backHome = third.FirstOrDefault(r => Near(r, Tx0, Ty0, 0d, Tx1, Ty1, Tz));
            if (backHome is null)
            {
                step.Fail("возврат вектора в ноль не вернул тело в исходное положение: " + Describe(third));
                return;
            }

            step.Pass("смещение не накапливается: повтор вектора оставляет положение, обнуление "
                + "возвращает исходное");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    private void ReopenReadBack()
    {
        var step = _report.Begin("RP.7", "Перенос переживает save → close → reopen",
            "Читаются ли признак переноса и новое положение с переоткрытого файла?");
        var doc = NewPart(out var part);
        var path = Path.Combine(_options.WorkDir, "reposition-reopen.m3d");
        try
        {
            if (!BuildBar(part, doc, step, withStranger: true))
            {
                step.Fail("брусок не построен");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Fail("брусок не опознан");
                return;
            }

            var (ok, note) = ApplyReposition(doc, part, target,
                reposition => reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), new[] { 7d, -11d, 13d })),
                step);
            if (!ok)
            {
                step.Fail("перенос: " + note);
                return;
            }

            var after = BodyRows(part);
            step.Observe("тел до сохранения: " + Describe(after));
            doc.SaveAs(path);
            step.Observe("сохранено: " + path + " (" + new FileInfo(path).Length + " байт)");
            doc.close();

            var reopened = (ksDocument3D)_app.Document3D();
            if (!reopened.Open(path))
            {
                step.Fail("файл не открылся: " + path);
                return;
            }

            try
            {
                var reopenedPart = (ksPart)reopened.GetPart(-1);
                var rows = BodyRows(reopenedPart);
                step.Observe("тел после переоткрытия: " + Describe(rows));
                step.Observe("признаков BodyRepositions после переоткрытия: " + Api5.Raw(RepositionCount(reopened)));
                step.Data["after_reopen"] = Describe(rows);
                step.Data["reposition_count_after_reopen"] = RepositionCount(reopened);
                var moved = rows.FirstOrDefault(r => Near(r, 17d, -11d, 13d, 37d, -1d, 18d));
                if (moved is null)
                {
                    step.Fail("после переоткрытия тело не там, где его оставил перенос");
                    return;
                }

                step.Pass("положение и признак переноса пережили save → close → reopen");
            }
            finally
            {
                TryClose(reopened);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ read ══

    /// <summary>
    /// Что читается ОБРАТНО с признака ПОСЛЕ записи — и различает ли чтение перенос от поворота.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Зачем отдельный шаг. Наряд §7 требует, чтобы действие <c>read</c> отдавало «параметры и входы
    /// ИЗ МОДЕЛИ». У семейства переноса эти параметры — вид преобразования, вектор, ось и угол, и
    /// сложить их можно только из <c>Position</c>. До этого шага был измерен ровно один его срез —
    /// чтение ПУСТОГО признака (RP.1): <c>GetVector</c> на нём отвечает <c>False (0, 0, 0)</c>. Этого
    /// мало: чтение записанного не измерялось вовсе, а публиковать чтение, которое всегда отдаёт
    /// null, значит выдать «в модели ничего нет» за свойство модели. Это тот же класс ошибки, что
    /// урок <c>4/tan</c>: прибор, который не может ничего прочитать, описывает себя.
    /// </para>
    /// <para>
    /// <b>Ожидания объявлены ДО опыта.</b> Перенос: оси единичные, <c>X/Y/Z = (7, −11, 13)</c>.
    /// Поворот на 90° вокруг Z через начало координат: <c>Rz = [[0, −1, 0], [1, 0, 0], [0, 0, 1]]</c>,
    /// и запись в <c>InitByMatrix3D</c> идёт ПО СТОЛБЦАМ (это измерено шагом RP.4 и записано в
    /// <see cref="RotationZ"/>), поэтому ось X локальной системы — либо столбец <c>(0, 1, 0)</c>,
    /// либо строка <c>(0, −1, 0)</c>. Обе гипотезы объявлены заранее и названа совпавшая: одна и та же
    /// запись читается как строка и как столбец с точностью до знака угла, и выбрать «на глаз» здесь
    /// значило бы угадать направление поворота.
    /// </para>
    /// <para>
    /// <b>Отрицательный контроль встроен.</b> Третий опыт — признак, которому НЕ записывали ничего.
    /// Если и он покажет единичные оси, то чтение не отличает «записано» от «не записано», и
    /// публиковать его нельзя. Ожидание контроля: <c>GetVector</c> отвечает отказом, а не единицей.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Что читается ОБРАТНО с признака ПОСЛЕ записи: различимы ли перенос и поворот, и читается ли
    /// сам вектор переноса.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Зачем эти шаги. Наряд §7 требует, чтобы действие <c>read</c> отдавало «параметры и входы
    /// ИЗ МОДЕЛИ». У семейства переноса эти параметры — вид преобразования, вектор, ось и угол, и
    /// сложить их можно только из <c>Position</c>. До этих шагов был измерен ровно один срез —
    /// чтение ПУСТОГО признака (RP.1): <c>GetVector</c> на нём отвечает <c>False (0, 0, 0)</c>.
    /// Публиковать чтение, построенное на таком срезе, значило бы выдать «в модели ничего нет» за
    /// свойство модели. Это тот же класс ошибки, что урок <c>4/tan</c>: прибор, который ничего не
    /// может прочитать, описывает себя.
    /// </para>
    /// <para>
    /// <b>Два вопроса — два вердикта, и это не формальность.</b> Первая редакция задавала оба
    /// вопроса одним шагом и останавливалась на первом же расхождении. Прогон это и показал: перенос
    /// не прочитался, шаг отказал и завершился — и ответ про ПОВОРОТ, ради которого всё затевалось,
    /// остался неизмеренным. Шаг, который замолкает на первой неожиданности, прячет ровно то, что
    /// ещё не знает.
    /// </para>
    /// <para>
    /// <b>Ожидания объявлены ДО опыта.</b> Перенос: оси единичные. Поворот на 90° вокруг Z через
    /// начало координат: <c>Rz = [[0, −1, 0], [1, 0, 0], [0, 0, 1]]</c>, и запись в
    /// <c>InitByMatrix3D</c> идёт ПО СТОЛБЦАМ (измерено шагом RP.4 и записано в
    /// <see cref="RotationZ"/>), поэтому ось X локальной системы — либо столбец <c>(0, 1, 0)</c>,
    /// либо строка <c>(0, −1, 0)</c>. Обе гипотезы объявлены заранее и названа совпавшая: одна и та
    /// же запись читается как строка и как столбец с точностью до знака угла, и выбрать «на глаз»
    /// значило бы угадать направление поворота.
    /// </para>
    /// <para>
    /// <b>Отрицательный контроль встроен в оба шага.</b> Третий признак — тот, которому НЕ
    /// записывали ничего. Если и он отдаст оси, чтение не отличает «записано» от «не записано», и
    /// публиковать его нельзя.
    /// </para>
    /// </remarks>
    private void ReadBackAfterWrite()
    {
        var step = _report.Begin("RP.8", "Различимы ли перенос и поворот по чтению Position",
            "Отличает ли чтение повёрнутую систему от неповёрнутой, и отказывает ли оно там, где записи не было?");
        var numbers = _report.Begin("RP.9", "Читается ли ВЕКТОР переноса обратно",
            "Возвращает ли Position то смещение (7, −11, 13), которое было записано?");

        step.Observe("A. перенос — ожидание: оси (1,0,0)/(0,1,0)/(0,0,1)");
        step.Observe("B. поворот 90° вокруг Z через начало — ожидание: ось X = (0,1,0) [столбцы] "
            + "либо (0,−1,0) [строки], ось Y = (−1,0,0) либо (1,0,0), ось Z = (0,0,1)");
        step.Observe("C. контроль — признак, которому НЕ записывали ничего: GetVector обязан отказать");
        numbers.Observe("A. ожидание: Position.X/Y/Z = (7, −11, 13) и Vector3D(OX/OY/OZ) = тот же вектор");

        PositionRead readA;
        var docA = NewPart(out var partA);
        try
        {
            if (!BuildBar(partA, docA, step, withStranger: false))
            {
                step.Fail("брусок A не построен");
                numbers.Fail("брусок A не построен");
                return;
            }

            var targetA = FindBody(BodyRows(partA), Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (targetA is null)
            {
                step.Fail("брусок A не опознан");
                numbers.Fail("брусок A не опознан");
                return;
            }

            var (okA, noteA) = ApplyReposition(docA, partA, targetA,
                reposition => reposition.Position.InitByMatrix3D(
                    Matrix4x4(Identity(), new[] { 7d, -11d, 13d })), step);
            if (!okA)
            {
                step.Fail("перенос: " + noteA);
                numbers.Fail("перенос: " + noteA);
                return;
            }

            readA = ReadPosition(docA, 0, step, "A");

            // Контроль: ВТОРОЙ признак того же документа, созданный и НЕ настроенный. От A он
            // отличается ровно одним — отсутствием записи. Update() у него не вызывается намеренно:
            // RP.1 измерил, что без цели и без Position он отвечает False, и требовать от контроля
            // успешного Update значило бы проверять не то.
            var containerA = Container(docA);
            if (containerA?.BodyRepositions?.Add() is not IBodyReposition control)
            {
                step.Fail("контрольный признак не создан: BodyRepositions.Add() недоступен");
                numbers.Fail("контрольный признак не создан");
                return;
            }

            var controlLocal = SafeObjectOf(() => control.Position) as ILocalCoordinateSystem;
            var (controlOxOk, controlOx) = controlLocal is null
                ? ((bool?)null, (double[]?)null)
                : ReadVector(controlLocal, ksObj3dTypeEnum.o3d_axisOX);

            var axesIdentity = Near(readA.Ox, 1d, 0d, 0d) && Near(readA.Oy, 0d, 1d, 0d)
                && Near(readA.Oz, 0d, 0d, 1d);
            var controlRefused = controlOxOk is false;
            var translationRead = NearScalar(readA.X, 7d) && NearScalar(readA.Y, -11d)
                && NearScalar(readA.Z, 13d);

            step.Data["A_axes_identity"] = axesIdentity;
            step.Data["C_refused"] = controlRefused;
            numbers.Data["A_translation_read"] = translationRead;

            step.Observe("A. прочитано: X/Y/Z = " + Triple(readA.X, readA.Y, readA.Z)
                + ", оси " + AxisText(readA.OxOk, readA.Ox) + " / " + AxisText(readA.OyOk, readA.Oy)
                + " / " + AxisText(readA.OzOk, readA.Oz)
                + ", OrientationType = " + (readA.Orientation ?? "<не прочитано>")
                + ", ParameterType = " + (readA.ParameterType ?? "<не прочитано>"));
            step.Observe("C. контроль, признак без записи: ось X " + AxisText(controlOxOk, controlOx));
            numbers.Observe("A. прочитано: X/Y/Z = " + Triple(readA.X, readA.Y, readA.Z)
                + ", Vector3D(OX/OY/OZ) = " + (readA.Vector3DText ?? "<не прочитано>"));

            if (!controlRefused)
            {
                step.Fail("контроль не сработал: признак БЕЗ записи тоже отдаёт ось ("
                    + AxisText(controlOxOk, controlOx) + "), значит чтение не отличает «записано» "
                    + "от «не записано»");
            }
            else if (!axesIdentity)
            {
                step.Fail("записанный перенос не прочитался: оси не единичны — "
                    + AxisText(readA.OxOk, readA.Ox) + " / " + AxisText(readA.OyOk, readA.Oy) + " / "
                    + AxisText(readA.OzOk, readA.Oz));
            }

            if (translationRead)
            {
                numbers.Pass("вектор переноса читается обратно как Position.X/Y/Z");
            }
            else
            {
                numbers.Fail(
                    "записанный перенос (7, −11, 13) НЕ читается обратно: Position.X/Y/Z = "
                    + Triple(readA.X, readA.Y, readA.Z) + ", Vector3D = "
                    + (readA.Vector3DText ?? "<не прочитано>")
                    + ". При этом тело встало куда просили (RP.3), то есть запись применилась, а "
                    + "обратного чтения у этого числа нет — значит поле reposition_vector_mm этим "
                    + "маршрутом не наполняется, и для него нужен отдельный опыт");
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
            numbers.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(docA);
        }

        var docB = NewPart(out var partB);
        try
        {
            if (!BuildBar(partB, docB, step, withStranger: false))
            {
                if (step.Verdict != Verdict.Fail)
                {
                    step.Fail("брусок B не построен");
                }

                return;
            }

            var targetB = FindBody(BodyRows(partB), Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (targetB is null)
            {
                if (step.Verdict != Verdict.Fail)
                {
                    step.Fail("брусок B не опознан");
                }

                return;
            }

            var (okB, noteB) = ApplyReposition(docB, partB, targetB,
                reposition => reposition.Position.InitByMatrix3D(RotationZ(90d, 0d, 0d)), step);
            if (!okB)
            {
                if (step.Verdict != Verdict.Fail)
                {
                    step.Fail("поворот: " + noteB);
                }

                return;
            }

            var readB = ReadPosition(docB, 0, step, "B");
            var columnLayout = Near(readB.Ox, 0d, 1d, 0d) && Near(readB.Oy, -1d, 0d, 0d)
                && Near(readB.Oz, 0d, 0d, 1d);
            var rowLayout = Near(readB.Ox, 0d, -1d, 0d) && Near(readB.Oy, 1d, 0d, 0d)
                && Near(readB.Oz, 0d, 0d, 1d);
            var rotated = columnLayout || rowLayout;

            step.Data["B_axes_match"] = columnLayout ? "columns" : rowLayout ? "rows" : "neither";

            step.Observe("B. прочитано: X/Y/Z = " + Triple(readB.X, readB.Y, readB.Z)
                + ", оси " + AxisText(readB.OxOk, readB.Ox) + " / " + AxisText(readB.OyOk, readB.Oy)
                + " / " + AxisText(readB.OzOk, readB.Oz)
                + ", OrientationType = " + (readB.Orientation ?? "<не прочитано>"));
            step.Observe("B. раскладка: " + (columnLayout ? "СТОЛБЦЫ (гипотеза подтвердилась)"
                : rowLayout ? "СТРОКИ (гипотеза подтвердилась)" : "ни одна из двух объявленных"));

            if (step.Verdict == Verdict.Fail)
            {
                // Перенос уже отказал — вердикт шага уже «отказ», и переписывать его нечем. Ответ
                // про поворот всё равно записан в наблюдениях, и это здесь главное.
                return;
            }

            if (!rotated)
            {
                step.Fail("оси повёрнутой системы не совпали ни с одной объявленной гипотезой: "
                    + "отличить поворот от переноса по этому чтению нельзя");
                return;
            }

            step.Pass("чтение различает перенос и поворот: у переноса оси единичны, у поворота — "
                + (columnLayout ? "столбцы" : "строки") + " матрицы, а признак без записи от осей "
                + "отказывается");
        }
        catch (Exception ex)
        {
            if (step.Verdict != Verdict.Fail)
            {
                step.Fail(HResult.Describe(ex));
            }
        }
        finally
        {
            TryClose(docB);
        }
    }

    /// <summary>Прочитанные с живого признака числа. Каждое поле — «null = не прочитано», не «ноль».</summary>
    private sealed record PositionRead(
        double? X, double? Y, double? Z,
        bool? OxOk, double[]? Ox,
        bool? OyOk, double[]? Oy,
        bool? OzOk, double[]? Oz,
        string? Orientation, string? ParameterType, string? Vector3DText);

    private PositionRead ReadPosition(ksDocument3D doc, int index, ProbeStep step, string label)
    {
        var container = Container(doc);
        if (container?.BodyRepositions is not { } collection || collection.Count <= index)
        {
            step.Observe(label + ". признак[" + index + "] не найден");
            return EmptyPositionRead;
        }

        if (collection[index] is not IBodyReposition feature)
        {
            step.Observe(label + ". элемент[" + index + "] не отвечает IBodyReposition");
            return EmptyPositionRead;
        }

        if (SafeObjectOf(() => feature.Position) is not ILocalCoordinateSystem local)
        {
            step.Observe(label + ". Position не отвечает ILocalCoordinateSystem");
            return EmptyPositionRead;
        }

        var (oxOk, ox) = ReadVector(local, ksObj3dTypeEnum.o3d_axisOX);
        var (oyOk, oy) = ReadVector(local, ksObj3dTypeEnum.o3d_axisOY);
        var (ozOk, oz) = ReadVector(local, ksObj3dTypeEnum.o3d_axisOZ);

        return new PositionRead(
            SafeDouble(() => local.X), SafeDouble(() => local.Y), SafeDouble(() => local.Z),
            oxOk, ox, oyOk, oy, ozOk, oz,
            SafeEnum(() => local.OrientationType)?.ToString(),
            SafeEnum(() => local.ParameterType)?.ToString(),
            Vector3DText(local));
    }

    /// <summary>
    /// <c>Vector3D</c> — свойство, ИНДЕКСИРОВАННОЕ по оси (<c>get_Vector3D(ksObj3dTypeEnum)</c>),
    /// поэтому обычной записью в C# оно не берётся (CS0856) и спрашивается через IDispatch.
    /// Отказ здесь — наблюдение, а не сбой: у неповёрнутой системы вектора может не быть вовсе.
    /// </summary>
    private static string? Vector3DText(ILocalCoordinateSystem local)
    {
        try
        {
            var parts = new List<string>();
            foreach (var (label, axis) in new[]
                     {
                         ("OX", ksObj3dTypeEnum.o3d_axisOX),
                         ("OY", ksObj3dTypeEnum.o3d_axisOY),
                         ("OZ", ksObj3dTypeEnum.o3d_axisOZ),
                     })
            {
                parts.Add(label + "=" + Api5.Raw(Late.Call(local, "get_Vector3D", axis)));
            }

            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>«Не прочитано» целиком — все поля null, а не ноль.</summary>
    private static PositionRead EmptyPositionRead =>
        new(null, null, null, null, null, null, null, null, null, null, null, null);

    // ══════════════════════════════ вектор переноса ══

    /// <summary>
    /// Читается ли ВЕКТОР переноса хоть каким-нибудь маршрутом — развёртка кандидатов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Зачем отдельный шаг. Шаг RP.9 измерил, что <c>Position.X/Y/Z</c> записанного переноса не
    /// отдаёт, а <c>Vector3D</c> через IDispatch отвечает <c>DISP_E_UNKNOWNNAME</c>. Один
    /// отрицательный результат закрывает ОДИН маршрут, а не вопрос: полей у <c>ILocalCoordinateSystem</c>
    /// хватает на несколько гипотез, и каждая стоит своей проверки. Без этого вектора действие
    /// <c>read</c> наряда §7 у семейства переноса закрыть нечем — <c>reposition_vector_mm</c> и
    /// <c>reposition_axis_point_mm</c> из одних только осей не выводятся.
    /// </para>
    /// <para>
    /// <b>Ожидание объявлено ДО опыта и одно на все маршруты:</b> записан вектор <c>(7, −11, 13)</c>,
    /// и вопрос шага — «отдаёт ли ХОТЬ ОДИН маршрут эти три числа». Маршрут, который вернёт что-то
    /// другое, не «почти подошёл»: подставить вместо вектора габарит, объём или положение центра
    /// значило бы выдать производное за прочитанное.
    /// </para>
    /// <para>
    /// <b>Контроль встроен и он различающий.</b> Те же чтения выполняются на признаке, которому НЕ
    /// записывали ничего. Маршрут, отдающий <c>(7, −11, 13)</c> и там, описывает не признак, а себя.
    /// Кроме того отдельно измеряется чтение ДО <c>Update()</c>: если система координат хранит
    /// запрошенное преобразование до применения, числа видны там, а после применения — уже нет.
    /// </para>
    /// </remarks>
    private void TranslationVectorRoutes()
    {
        var step = _report.Begin("RP.10", "Читается ли вектор переноса хоть каким-нибудь маршрутом",
            "Отдаёт ли ХОТЬ ОДИН член Position записанные (7, −11, 13), и не отдаёт ли он их и там, где записи не было?");

        var wanted = new[] { 7d, -11d, 13d };
        step.Observe("ожидание для КАЖДОГО маршрута: (7, −11, 13) — и ни один из них не должен "
            + "отдать эти числа на признаке без записи");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var target = FindBody(BodyRows(part), Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Fail("брусок не опознан");
                return;
            }

            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not { } created
                || created is not IBodyReposition reposition)
            {
                step.Fail("BodyRepositions.Add() не отдаёт IBodyReposition");
                return;
            }

            if (Transfer(target) is not IKompasAPIObject body7)
            {
                step.Fail("тело не переносится в API7 как IKompasAPIObject");
                return;
            }

            reposition.RepositionBody = body7;
            reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), wanted));

            // ── чтение ДО Update(): хранит ли система координат запрошенное преобразование ──
            var before = ReadPosition(doc, 0, step, "до Update");
            step.Observe("до Update(): X/Y/Z = " + Triple(before.X, before.Y, before.Z)
                + ", оси " + AxisText(before.OxOk, before.Ox) + " / " + AxisText(before.OyOk, before.Oy)
                + " / " + AxisText(before.OzOk, before.Oz));
            var beforeHolds = NearScalar(before.X, 7d) && NearScalar(before.Y, -11d)
                && NearScalar(before.Z, 13d);

            var updated = reposition.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("Update()=" + updated + " (успех сам по себе доказательством не является)");

            var after = ReadPosition(doc, 0, step, "после Update");
            var afterHolds = NearScalar(after.X, 7d) && NearScalar(after.Y, -11d)
                && NearScalar(after.Z, 13d);
            step.Observe("после Update(): X/Y/Z = " + Triple(after.X, after.Y, after.Z)
                + ", оси " + AxisText(after.OxOk, after.Ox) + " / " + AxisText(after.OyOk, after.Oy)
                + " / " + AxisText(after.OzOk, after.Oz));

            // ── остальные кандидаты: их тип и что они вообще отдают ──
            var local = SafeObjectOf(() => reposition.Position) as ILocalCoordinateSystem;
            step.Observe("Position.Parameters: " + Api5.RuntimeName(SafeObjectOf(() => local!.Parameters)));
            step.Observe("Position.LocalCSParameters: " + Api5.RuntimeName(SafeObjectOf(() => local!.LocalCSParameters)));
            step.Observe("Position.AssociationObject: " + Api5.RuntimeName(SafeObjectOf(() => local!.AssociationObject)));
            step.Observe("Position.DefaultObject(OX): " + Api5.RuntimeName(SafeObjectOf(() => local!.get_DefaultObject(ksObj3dTypeEnum.o3d_axisOX))));
            step.Observe("Position.Current: " + Api5.Raw(local is null ? null : Api5.SafeBool(() => local.Current)));
            step.Observe("Position.ParameterType: " + Api5.Raw(local is null ? null : SafeEnum(() => local.ParameterType)));
            step.Observe("RepositionCentre: " + Api5.RuntimeName(SafeObjectOf(() => reposition.RepositionCentre)));
            step.Observe("CopyBoby: " + Api5.Raw(SafeBoolOf(() => reposition.CopyBoby)));

            // Три числа, если маршрут их всё-таки отдаёт: X/Y/Z у Parameters и у LocalCSParameters
            // спрашиваются через IDispatch — их тип объявлен как IKompasAPIObject, конкретный
            // интерфейс заранее неизвестен, и предполагать его значило бы гадать.
            foreach (var (label, source) in new[]
                     {
                         ("Parameters", SafeObjectOf(() => local!.Parameters)),
                         ("LocalCSParameters", SafeObjectOf(() => local!.LocalCSParameters)),
                         ("RepositionCentre", SafeObjectOf(() => reposition.RepositionCentre)),
                     })
            {
                if (source is null)
                {
                    step.Observe(label + ".X/Y/Z: источник null");
                    continue;
                }

                var values = new List<string>();
                foreach (var member in new[] { "X", "Y", "Z" })
                {
                    values.Add(member + "=" + LateText(source, member));
                }

                step.Observe(label + ".X/Y/Z: " + string.Join(", ", values));
            }

            // ── контроль: тот же набор чтений на признаке БЕЗ записи ──
            var controlDoc = NewPart(out var controlPart);
            try
            {
                if (BuildBar(controlPart, controlDoc, step, withStranger: false)
                    && FindBody(BodyRows(controlPart), Tx0, Ty0, 0d, Tx1, Ty1, Tz) is { } controlTarget
                    && Container(controlDoc)?.BodyRepositions?.Add() is IBodyReposition control
                    && Transfer(controlTarget) is IKompasAPIObject controlBody)
                {
                    control.RepositionBody = controlBody;
                    var controlRead = ReadPosition(controlDoc, 0, step, "контроль");
                    var controlHolds = NearScalar(controlRead.X, 7d) && NearScalar(controlRead.Y, -11d)
                        && NearScalar(controlRead.Z, 13d);
                    step.Observe("контроль без записи: X/Y/Z = "
                        + Triple(controlRead.X, controlRead.Y, controlRead.Z)
                        + ", оси " + AxisText(controlRead.OxOk, controlRead.Ox));
                    step.Data["control_holds"] = controlHolds;

                    if (controlHolds)
                    {
                        step.Fail("контроль не сработал: признак БЕЗ записи тоже отдаёт (7, −11, 13) — "
                            + "такой маршрут описывает себя, а не признак");
                        return;
                    }
                }
                else
                {
                    step.Observe("контроль не построен — это отмечено, а не выдано за чистый опыт");
                    step.Data["control_built"] = false;
                }
            }
            finally
            {
                TryClose(controlDoc);
            }

            var anyHolds = beforeHolds || afterHolds;
            step.Data["before_update_holds"] = beforeHolds;
            step.Data["after_update_holds"] = afterHolds;

            if (anyHolds)
            {
                step.Pass("вектор переноса читается: "
                    + (beforeHolds ? "до Update() как Position.X/Y/Z" : string.Empty)
                    + (beforeHolds && afterHolds ? " и " : string.Empty)
                    + (afterHolds ? "после Update() как Position.X/Y/Z" : string.Empty));
                return;
            }

            step.Fail("вектор переноса (7, −11, 13) не отдал НИ ОДИН из проверенных маршрутов: "
                + "Position.X/Y/Z до и после Update() — (0, 0, 0); оси описывают только поворот; "
                + "Parameters/LocalCSParameters/RepositionCentre — см. наблюдения. Значит "
                + "reposition_vector_mm этим семейством маршрутов не наполняется, и для него нужен "
                + "другой источник (например, матрица размещения тела), а не Position");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Читается ли ВЕКТОР переноса с ПЕРЕОТКРЫТОГО документа — то есть из того состояния модели,
    /// которое действие <c>read</c> и обязано читать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Шаги RP.8/RP.9/RP.10 читали признак ЖИВЫМ — в том же сеансе, где
    /// он и записан. Между двумя разными выводами прибор до сих пор не различал: «система координат
    /// не хранит перенос» и «система координат отдаёт перенос только тогда, когда построена из
    /// файла». Первый закрывает вопрос, второй открывает маршрут. Шаг RP.7 измерил, что признак и
    /// новое положение переживают <c>save → close → reopen</c>, но перенос с переоткрытого признака
    /// там НЕ читался — читался только габарит тела.
    /// </para>
    /// <para>
    /// <b>Ожидание объявлено до опыта и своё у каждого случая:</b> A записывает (7, −11, 13) и
    /// обязан прочитаться как (7, −11, 13); контроль B записывает (1, 2, 3) тем же маршрутом и
    /// обязан прочитаться как (1, 2, 3). Оси в A ожидаются единичными — перенос ориентацию не
    /// меняет, — и это положительный контроль: если и они не читаются, отказывает вся система
    /// координат, а не один её член.
    /// </para>
    /// <para>
    /// <b>Контроль различающий, а не «пустой признак».</b> Пустой признак отличался бы от A не
    /// одним, а двумя (нет записи и нет тела), и по нему нельзя было бы отличить «читается
    /// записанное» от «читается постоянное». Поэтому B — тот же маршрут, другое число.
    /// </para>
    /// </remarks>
    private void ReopenedTranslationRead()
    {
        var step = _report.Begin("RP.11", "Читается ли вектор переноса с ПЕРЕОТКРЫТОГО документа",
            "Отдаёт ли Position.X/Y/Z записанное после save → close → reopen, и различает ли он ДВА разных вектора?");

        var wanted = new[] { 7d, -11d, 13d };
        var other = new[] { 1d, 2d, 3d };
        step.Observe("A. ожидание: Position.X/Y/Z = (7, −11, 13), оси — единичные (перенос ориентацию не меняет)");
        step.Observe("B. контроль — тот же маршрут записи, но вектор (1, 2, 3): чтение обязано отдать "
            + "(1, 2, 3) и НЕ отдать (7, −11, 13)");

        var readA = ReopenedCase(step, "A", wanted, "reposition-read-a.m3d");
        var readB = ReopenedCase(step, "B", other, "reposition-read-b.m3d");
        if (readA is null || readB is null)
        {
            step.Fail("переоткрытый документ не измерен: случай не построен, и это отмечено выше, "
                + "а не выдано за чистый опыт");
            return;
        }

        var holdsA = NearScalar(readA.X, wanted[0]) && NearScalar(readA.Y, wanted[1])
            && NearScalar(readA.Z, wanted[2]);
        var holdsB = NearScalar(readB.X, other[0]) && NearScalar(readB.Y, other[1])
            && NearScalar(readB.Z, other[2]);
        var controlLeaks = NearScalar(readB.X, wanted[0]) && NearScalar(readB.Y, wanted[1])
            && NearScalar(readB.Z, wanted[2]);
        var axesA = Near(readA.Ox, 1d, 0d, 0d) && Near(readA.Oy, 0d, 1d, 0d) && Near(readA.Oz, 0d, 0d, 1d);

        step.Data["A_holds"] = holdsA;
        step.Data["B_holds_own"] = holdsB;
        step.Data["B_leaks_A"] = controlLeaks;
        step.Data["A_axes_identity"] = axesA;

        step.Observe("A. прочитано: X/Y/Z = " + Triple(readA.X, readA.Y, readA.Z)
            + ", оси " + AxisText(readA.OxOk, readA.Ox) + " / " + AxisText(readA.OyOk, readA.Oy)
            + " / " + AxisText(readA.OzOk, readA.Oz));
        step.Observe("B. прочитано: X/Y/Z = " + Triple(readB.X, readB.Y, readB.Z)
            + ", оси " + AxisText(readB.OxOk, readB.Ox) + " / " + AxisText(readB.OyOk, readB.Oy)
            + " / " + AxisText(readB.OzOk, readB.Oz));

        if (controlLeaks)
        {
            step.Fail("контроль не сработал: признак с вектором (1, 2, 3) читается как (7, −11, 13) — "
                + "такое чтение отдаёт не записанное, а постоянное, и вектором переноса не является");
            return;
        }

        if (!holdsA || !holdsB)
        {
            step.Fail("вектор переноса с переоткрытого документа не читается: A = "
                + Triple(readA.X, readA.Y, readA.Z) + " (ожидалось 7, −11, 13), B = "
                + Triple(readB.X, readB.Y, readB.Z) + " (ожидалось 1, 2, 3). При этом оси "
                + (axesA
                    ? "читаются и единичны — признак найден и отвечает, а переноса в его членах нет"
                    : "тоже не читаются — отказывает вся система координат, а не один её член"));
            return;
        }

        step.Pass("вектор переноса читается с переоткрытого документа: A = (7, −11, 13), B = (1, 2, 3)");
    }

    /// <summary>
    /// Один случай шага RP.11: записать вектор, сохранить, закрыть, переоткрыть, прочитать признак.
    /// <c>null</c> — случай не построен, и вызывающий обязан сказать это вслух, а не считать
    /// отсутствие измерения отрицательным результатом.
    /// </summary>
    private PositionRead? ReopenedCase(ProbeStep step, string label, double[] vector, string fileName)
    {
        var doc = NewPart(out var part);
        var path = Path.Combine(_options.WorkDir, fileName);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Observe(label + ": брусок не построен");
                return null;
            }

            var target = FindBody(BodyRows(part), Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Observe(label + ": брусок не опознан");
                return null;
            }

            var (ok, note) = ApplyReposition(doc, part, target,
                reposition => reposition.Position.InitByMatrix3D(Matrix4x4(Identity(), vector)), step);
            if (!ok)
            {
                step.Observe(label + ": запись вектора отвергнута — " + note);
                return null;
            }

            doc.SaveAs(path);
            doc.close();

            var reopened = (ksDocument3D)_app.Document3D();
            if (!reopened.Open(path))
            {
                step.Observe(label + ": файл не открылся — " + path);
                return null;
            }

            try
            {
                step.Observe(label + ": признаков BodyRepositions после переоткрытия: "
                    + Api5.Raw(RepositionCount(reopened)));
                ReopenedPositionDetails(step, label, reopened);
                return ReadPosition(reopened, 0, step, label);
            }
            finally
            {
                TryClose(reopened);
            }
        }
        catch (Exception ex)
        {
            step.Observe(label + ": исключение " + HResult.Describe(ex));
            return null;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Члены системы координат ПЕРЕОТКРЫТОГО признака, которые в шагах RP.8–RP.10 не спрашивались:
    /// состояние, тип, ссылка, а также <c>WriteToFile</c>. Последний отвечает на отдельный вопрос —
    /// лежат ли числа в самой системе координат, независимо от того, отдают ли их её члены.
    /// </summary>
    private void ReopenedPositionDetails(ProbeStep step, string label, ksDocument3D doc)
    {
        var container = Container(doc);
        if (container?.BodyRepositions is not { } collection || collection.Count == 0
            || collection[0] is not IBodyReposition feature
            || SafeObjectOf(() => feature.Position) is not ILocalCoordinateSystem local)
        {
            step.Observe(label + ". Position переоткрытого признака не получен — остальные члены не спрашиваются");
            return;
        }

        step.Observe(label + ". Valid=" + Api5.Raw(SafeBoolOf(() => local.Valid))
            + ", Type=" + Api5.Raw(SafeEnum(() => local.Type))
            + ", ModelObjectType=" + Api5.Raw(SafeEnum(() => local.ModelObjectType))
            + ", Reference=" + Api5.Raw(SafeInt(() => local.Reference))
            + ", Current=" + Api5.Raw(SafeBoolOf(() => local.Current)));
        step.Observe(label + ". типизированный get_Vector3D(OX) → "
            + Api5.Raw(TypedVector3D(local, ksObj3dTypeEnum.o3d_axisOX)));

        // Обновление САМОЙ системы координат: если записанное преобразование попадает в её члены
        // только при этом вызове, чтение обязано измениться. Порядок важен — сначала чтение, потом
        // Update, потом повторное чтение: иначе измерение перезаписало бы само себя.
        var before = new[] { SafeDouble(() => local.X), SafeDouble(() => local.Y), SafeDouble(() => local.Z) };
        var updated = SafeBoolOf(() => local.Update());
        var after = new[] { SafeDouble(() => local.X), SafeDouble(() => local.Y), SafeDouble(() => local.Z) };
        step.Observe(label + ". Position.Update()=" + Api5.Raw(updated) + ": X/Y/Z до "
            + Triple(before[0], before[1], before[2]) + ", после " + Triple(after[0], after[1], after[2]));

        var file = Path.Combine(_options.WorkDir, "reposition-position-" + label + ".txt");
        var written = SafeBoolOf(() => local.WriteToFile(file));
        step.Observe(label + ". Position.WriteToFile → " + Api5.Raw(written) + ": "
            + (File.Exists(file) ? DumpFile(file) : "файла нет"));
    }

    /// <summary>Файл, записанный системой координат, — как наблюдение, а не как маршрут чтения.</summary>
    private static string DumpFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return "файл " + new FileInfo(path).Length + " байт, первые 300 символов: "
                + (text.Length > 300 ? text[..300] + "…" : text);
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Типизированный вызов <c>get_Vector3D(ksObj3dTypeEnum)</c>. Через IDispatch этот член отвечает
    /// <c>DISP_E_UNKNOWNNAME</c> (шаг RP.9), поэтому вопрос задаётся по интерфейсу, а не поздним
    /// связыванием: «через IDispatch не берётся» и «не существует» — разные утверждения.
    /// </summary>
    private static object? TypedVector3D(ILocalCoordinateSystem local, ksObj3dTypeEnum axis)
    {
        try
        {
            return local.get_Vector3D(axis);
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Читается ли ТОЧКА ОПОРЫ поворота, и выводятся ли из осей ось и угол.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Последний открытый вопрос действия <c>read</c> у семейства 4.10. Шаг RP.11 измерил, что система
    /// координат признака переноса состоит из одной ориентации: её <c>WriteToFile</c> содержит ровно
    /// матрицу 3×3, а <c>X/Y/Z</c> нулевы и на переоткрытом документе. Для ПОВОРОТА из этого следует
    /// проверяемая гипотеза: если <c>X/Y/Z</c> — это смещение начала координат <c>p − R·p</c>, то у
    /// поворота вокруг точки (5, 0, 0) они обязаны прочитаться как (5, −5, 0), а у поворота вокруг
    /// начала координат — остаться нулевыми. Именно эта пара и различает «член не заполняется никогда»
    /// и «член заполняется только для поворота».
    /// </para>
    /// <para>
    /// <b>Второй вопрос того же шага — выводимость.</b> Ось и угол не читаются ни одним членом, но
    /// выводятся из осей: угол — <c>acos((tr R − 1)/2)</c>, ось — собственный вектор собственного
    /// значения 1. Это утверждение тоже проверяется, а не принимается на веру: записано 90° вокруг Z,
    /// и вывод обязан дать 90° вокруг Z. Совпадение вывода с известным входом — единственное, что
    /// делает такой маршрут годным для <c>read</c>.
    /// </para>
    /// </remarks>
    private void RotationReadBack()
    {
        var step = _report.Begin("RP.12", "Читается ли точка опоры поворота и выводятся ли ось и угол",
            "Отдают ли Position.X/Y/Z точку опоры, и совпадает ли вывод оси и угла из осей с записанным поворотом?");

        step.Observe("A. поворот +90° вокруг Z через (5,0,0): гипотеза p − R·p → X/Y/Z = (5, −5, 0)");
        step.Observe("B. контроль, поворот +90° вокруг Z через начало координат: X/Y/Z обязаны остаться "
            + "(0, 0, 0) — иначе чтение отдаёт не точку опоры, а постоянное число");
        step.Observe("вывод для обоих случаев: угол = acos((tr R − 1)/2), ось — собственный вектор R; "
            + "ожидание — 90° и Z = (0, 0, 1)");

        var throughPoint = RotationCase(step, "A", RotationZ(90d, 5d, 0d), 5d, 0d);
        var throughOrigin = RotationCase(step, "B", RotationZ(90d, 0d, 0d), 0d, 0d);
        if (throughPoint is null || throughOrigin is null)
        {
            step.Fail("случай не построен — это отмечено выше, а не выдано за чистый опыт");
            return;
        }

        var pointRead = NearScalar(throughPoint.X, 5d) && NearScalar(throughPoint.Y, -5d)
            && NearScalar(throughPoint.Z, 0d);
        var originZero = NearScalar(throughOrigin.X, 0d) && NearScalar(throughOrigin.Y, 0d)
            && NearScalar(throughOrigin.Z, 0d);
        var derivedA = DeriveRotation(throughPoint);
        var derivedB = DeriveRotation(throughOrigin);
        var derivedOk = derivedA is { AngleDeg: 90d } && Near(derivedA.Value.Axis, 0d, 0d, 1d)
            && derivedB is { AngleDeg: 90d } && Near(derivedB.Value.Axis, 0d, 0d, 1d);

        step.Data["point_read"] = pointRead;
        step.Data["origin_zero"] = originZero;
        step.Data["derived_ok"] = derivedOk;

        step.Observe("A. прочитано: X/Y/Z = " + Triple(throughPoint.X, throughPoint.Y, throughPoint.Z)
            + ", оси " + AxisText(throughPoint.OxOk, throughPoint.Ox) + " / "
            + AxisText(throughPoint.OyOk, throughPoint.Oy) + " / " + AxisText(throughPoint.OzOk, throughPoint.Oz));
        step.Observe("B. прочитано: X/Y/Z = " + Triple(throughOrigin.X, throughOrigin.Y, throughOrigin.Z)
            + ", оси " + AxisText(throughOrigin.OxOk, throughOrigin.Ox) + " / "
            + AxisText(throughOrigin.OyOk, throughOrigin.Oy) + " / " + AxisText(throughOrigin.OzOk, throughOrigin.Oz));
        step.Observe("вывод из осей: A → " + DescribeDerived(derivedA) + ", B → " + DescribeDerived(derivedB));

        if (!derivedOk)
        {
            step.Fail("вывод оси и угла из осей не совпал с записанным поворотом 90° вокруг Z: A → "
                + DescribeDerived(derivedA) + ", B → " + DescribeDerived(derivedB)
                + ". Тогда read не может объявить reposition_angle_deg и reposition_axis_direction_mm "
                + "выводимыми, и это надо сказать, а не округлить");
            return;
        }

        if (pointRead && originZero)
        {
            step.Pass("точка опоры поворота читается как Position.X/Y/Z (p − R·p), а ось и угол выводятся "
                + "из осей и совпадают с записанным поворотом");
            return;
        }

        step.Fail("точка опоры поворота НЕ читается: поворот вокруг (5, 0, 0) дал X/Y/Z = "
            + Triple(throughPoint.X, throughPoint.Y, throughPoint.Z) + " (ожидалось 5, −5, 0), поворот "
            + "вокруг начала координат — " + Triple(throughOrigin.X, throughOrigin.Y, throughOrigin.Z)
            + " (ожидалось 0, 0, 0). Значит Position.X/Y/Z не отдают и точку опоры, а вывод из осей "
            + "работает: " + DescribeDerived(derivedA));
    }

    /// <summary>Один случай шага RP.12: записать поворот известной матрицей и прочитать признак.</summary>
    private PositionRead? RotationCase(ProbeStep step, string label, double[] matrix, double cx, double cy)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Observe(label + ": брусок не построен");
                return null;
            }

            var target = FindBody(BodyRows(part), Tx0, Ty0, 0d, Tx1, Ty1, Tz);
            if (target is null)
            {
                step.Observe(label + ": брусок не опознан");
                return null;
            }

            var (ok, note) = ApplyReposition(doc, part, target,
                reposition => reposition.Position.InitByMatrix3D(matrix), step);
            if (!ok)
            {
                step.Observe(label + ": запись поворота вокруг (" + cx + ", " + cy + ") отвергнута — " + note);
                return null;
            }

            return ReadPosition(doc, 0, step, label);
        }
        catch (Exception ex)
        {
            step.Observe(label + ": исключение " + HResult.Describe(ex));
            return null;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Вывод оси и угла из осей локальной системы. Раскладка — СТОЛБЦЫ (измерено шагом RP.8): ось X
    /// локальной системы есть первый столбец матрицы поворота. Угол считается по следу, ось — как
    /// собственный вектор собственного значения 1, то есть через кососимметричную часть:
    /// <c>axis ∝ (R₂₃ − R₃₂, R₃₁ − R₁₃, R₁₂ − R₂₁)</c>. Углы 0 и 180° этим маршрутом неразличимы по
    /// знаку оси, и это ограничение вывода, а не чтения.
    /// </summary>
    private static (double AngleDeg, double[] Axis)? DeriveRotation(PositionRead read)
    {
        if (read.Ox is not { Length: 3 } ox || read.Oy is not { Length: 3 } oy || read.Oz is not { Length: 3 } oz)
        {
            return null;
        }

        // Столбцы: r[i][j] = j-я компонента i-й оси.
        var trace = ox[0] + oy[1] + oz[2];
        var angle = Math.Acos(Math.Clamp((trace - 1d) / 2d, -1d, 1d)) * 180d / Math.PI;

        var axis = new[]
        {
            oy[2] - oz[1],
            oz[0] - ox[2],
            ox[1] - oy[0],
        };
        var norm = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
        if (norm < 1e-9)
        {
            return (angle, new[] { 0d, 0d, 0d });
        }

        return (angle, new[] { axis[0] / norm, axis[1] / norm, axis[2] / norm });
    }

    private static string DescribeDerived((double AngleDeg, double[] Axis)? derived) =>
        derived is not { } value
            ? "<не выведено: оси не прочитаны>"
            : value.AngleDeg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "°, ось "
              + Triple(value.Axis[0], value.Axis[1], value.Axis[2]);

    /// <summary>Значение члена через IDispatch — «не прочитано» отличается от «ноль».</summary>
    private static string LateText(object source, string member)
    {
        try
        {
            return Api5.Raw(Late.Get(source, member));
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    private static (bool? Ok, double[]? Value) ReadVector(ILocalCoordinateSystem local, ksObj3dTypeEnum axis)
    {
        try
        {
            var ok = local.GetVector(axis, out var x, out var y, out var z);
            return (ok, new[] { x, y, z });
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string AxisText(bool? ok, double[]? value) =>
        value is null ? "<не прочитано>" : Api5.Raw(ok) + Triple(value[0], value[1], value[2]);

    private static string Triple(double? x, double? y, double? z) =>
        "(" + Api5.Num(x) + ", " + Api5.Num(y) + ", " + Api5.Num(z) + ")";

    /// <summary>
    /// Сравнение ОДНОГО прочитанного числа с объявленным ожиданием. Отдельное имя, а не перегрузка
    /// <see cref="Near(double[], double, double, double)"/>: у чтения с признака «не прочитано» —
    /// это null, и оно обязано НЕ совпасть с ожиданием, а не упасть и не сойти за ноль.
    /// </summary>
    private static bool NearScalar(double? actual, double expected) =>
        actual is double value && Math.Abs(value - expected) <= 1e-6;

    // ══════════════════════════════════════════════════════════════ core ══

    private (bool Ok, string? Note) ApplyReposition(
        ksDocument3D doc, ksPart part, BodyRow target, Action<IBodyReposition> configure, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not { } created)
            {
                return (false, "BodyRepositions.Add() недоступен");
            }

            if (created is not IBodyReposition reposition)
            {
                return (false, "BodyRepositions.Add() не отдаёт IBodyReposition");
            }

            if (Transfer(target) is not IKompasAPIObject body7)
            {
                return (false, "тело не переносится в API7 как IKompasAPIObject");
            }

            reposition.RepositionBody = body7;
            configure(reposition);
            var updated = reposition.Update();
            step.Observe("вызов: RepositionBody=тело, Update()=" + updated);
            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "IBodyReposition.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>Запись в СУЩЕСТВУЮЩИЙ признак по индексу: маршрут правки, а не создания.</summary>
    private (bool Ok, string? Note) WriteExisting(
        ksDocument3D doc, ksPart part, int index, object matrix, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.BodyRepositions is not { } collection)
            {
                return (false, "BodyRepositions недоступны");
            }

            if (collection[index] is not IBodyReposition existing)
            {
                return (false, "элемент " + index + " не отдаёт IBodyReposition");
            }

            existing.Position.InitByMatrix3D(matrix);
            var updated = existing.Update();
            step.Observe("правка признака[" + index + "]: Update()=" + updated);
            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Матрица в раскладке «оси, затем начало»: 3×3 построчно, затем вектор переноса.
    /// </summary>
    private static double[] Matrix(double[] axes, double[] origin) =>
        new[] { axes[0], axes[1], axes[2], axes[3], axes[4], axes[5], axes[6], axes[7], axes[8],
            origin[0], origin[1], origin[2] };

    private static double[] MatrixOriginFirst(double[] axes, double[] origin) =>
        new[] { origin[0], origin[1], origin[2], axes[0], axes[1], axes[2], axes[3], axes[4], axes[5],
            axes[6], axes[7], axes[8] };

    private static double[] Matrix4x4(double[] axes, double[] origin) =>
        new[] { axes[0], axes[1], axes[2], 0d, axes[3], axes[4], axes[5], 0d, axes[6], axes[7], axes[8], 0d,
            origin[0], origin[1], origin[2], 1d };

    private static double[] Identity() => new[] { 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d };

    /// <summary>
    /// Сопоставление наблюдённого габарита с ДВУМЯ заранее объявленными гипотезами раскладки.
    /// </summary>
    /// <remarks>
    /// Прибор обязан различать гипотезы, а не подтверждать одну догадку: на симметричной детали и на
    /// переносе «поворот на +90°» и «поворот на −90°» дают одинаково выглядящий успех. Совпадение
    /// второй гипотезы — тоже измерение, и оно называется, а не прячется за «FAIL».
    /// </remarks>
    private static (string Layout, BodyRow? Row) MatchLayout(
        List<BodyRow> after,
        (double[] Min, double[] Max, string Label) primary,
        (double[] Min, double[] Max, string Label)? alternate)
    {
        var first = after.FirstOrDefault(r => Near(r, primary.Min, primary.Max));
        if (first is not null)
        {
            return (primary.Label, first);
        }

        if (alternate is { } alt)
        {
            var second = after.FirstOrDefault(r => Near(r, alt.Min, alt.Max));
            if (second is not null)
            {
                return (alt.Label, second);
            }
        }

        return ("ни одна из объявленных", null);
    }

    /// <summary>
    /// Поворот на <paramref name="angleDeg"/> вокруг оси Z через точку <c>(cx,cy,0)</c>.
    /// </summary>
    /// <remarks>
    /// Раскладка — однородная 4×4, потому что именно она ДВИГАЕТ тело: маршруты из 12 чисел
    /// (оси+начало) и <c>SetDisplacementByAxis</c> принимаются с <c>Update()=True</c>, но положения
    /// не меняют (RP.2). Строчно: 3×3, где СТОЛБЦЫ — образы базисных векторов, поэтому для поворота
    /// на +90° это <c>(0,1,0)</c>, <c>(−1,0,0)</c>, <c>(0,0,1)</c>; последняя строка — перенос,
    /// равный <c>c − R·c</c>.
    /// <para>
    /// Направление поворота этой записью НЕ доказывается: строка и столбец в 3×3 различаются только
    /// знаком угла. Поэтому RP.4 объявляет ДВЕ гипотезы заранее и называет совпавшую.
    /// </para>
    /// </remarks>
    private static double[] RotationZ(double angleDeg, double cx, double cy)
    {
        var a = angleDeg * Math.PI / 180d;
        var c = Math.Cos(a);
        var s = Math.Sin(a);

        // R = [[c, −s, 0], [s, c, 0], [0, 0, 1]]; столбцы: (c,s,0), (−s,c,0), (0,0,1).
        var axes = new[] { c, s, 0d, -s, c, 0d, 0d, 0d, 1d };
        var origin = new[] { cx - (c * cx - s * cy), cy - (s * cx + c * cy), 0d };
        return Matrix4x4(axes, origin);
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private bool BuildBar(ksPart part, ksDocument3D doc, ProbeStep step, bool withStranger)
    {
        if (!ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "T"))
        {
            return false;
        }

        return !withStranger
            || ExtrudeRect(doc, part, StrangerX0, StrangerX1, StrangerY0, StrangerY1, StrangerZ, step, "C");
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

    private object? Transfer(BodyRow row)
    {
        try
        {
            return _app.TransferInterface(row.Element!, 2, 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private int? RepositionCount(ksDocument3D doc)
    {
        var container = Container(doc);
        return container?.BodyRepositions is { } collection ? SafeInt(() => collection.Count) : null;
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
                var body = element as ksBody;
                double[]? min = null;
                double[]? max = null;
                int? faceCount = null;
                if (body is not null)
                {
                    if (Api5.SafeBool(() => body.GetGabarit(out _, out _, out _, out _, out _, out _)) == true)
                    {
                        body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                        min = new[] { x1, y1, z1 };
                        max = new[] { x2, y2, z2 };
                    }

                    faceCount = body.FaceCollection() is ksFaceCollection faces
                        ? SafeInt(() => faces.GetCount())
                        : null;
                }

                rows.Add(new BodyRow
                {
                    Index = i,
                    Element = element,
                    Volume = Api5.BodyVolume(element),
                    Min = min,
                    Max = max,
                    FaceCount = faceCount,
                });
            }
        }
        catch (Exception)
        {
            // Частичный список честнее пустого.
        }

        return rows;
    }

    private static BodyRow? FindBody(
        List<BodyRow> rows, double x0, double y0, double z0, double x1, double y1, double z1) =>
        rows.FirstOrDefault(r => Near(r, x0, y0, z0, x1, y1, z1));

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        Near(row.Min, x0, y0, z0) && Near(row.Max, x1, y1, z1);

    private static bool Near(BodyRow row, double[] min, double[] max) =>
        Near(row.Min, min[0], min[1], min[2]) && Near(row.Max, max[0], max[1], max[2]);

    private static bool Near(double[]? actual, double x, double y, double z) =>
        actual is not null
        && Math.Abs(actual[0] - x) < 1e-6 && Math.Abs(actual[1] - y) < 1e-6 && Math.Abs(actual[2] - z) < 1e-6;

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    private static string Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        "[" + Api5.Num(x0) + "," + Api5.Num(x1) + "]×[" + Api5.Num(y0) + "," + Api5.Num(y1) + "]×["
        + Api5.Num(z0) + "," + Api5.Num(z1) + "]";

    private static string Point(double[] value) =>
        "(" + string.Join(", ", value.Select(v => Api5.Num(v))) + ")";

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

    private static double? SafeDouble(Func<double> call)
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

    private static bool? SafeBoolOf(Func<bool> call)
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

    private static T? SafeEnum<T>(Func<T> call) where T : struct, Enum
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

    private static object? SafeObjectOf(Func<object> call)
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

    /// <summary>
    /// Одноосный вопрос к <c>Position.GetVector</c>: отвечает ли он и что именно. Нужен потому, что
    /// «член объявлен» и «член отвечает числом» — разные утверждения, а индексированное свойство
    /// <c>Vector3D</c> в C# напрямую не вызывается.
    /// </summary>
    private static string DescribeVector(ILocalCoordinateSystem local, ksObj3dTypeEnum axis)
    {
        try
        {
            var ok = local.GetVector(axis, out var x, out var y, out var z);
            return Api5.Raw(ok) + " (" + Api5.Num(x) + ", " + Api5.Num(y) + ", " + Api5.Num(z) + ")";
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("RP.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var left = NewProcesses();
        step.Observe("своих процессов до запуска: " + _pidsBefore.Count + ", новых после: " + left.Count
            + (left.Count == 0 ? string.Empty : " (" + string.Join(", ", left) + ")"));
        if (_ownPid != 0 && left.Contains(_ownPid))
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit()");
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

    private sealed class BodyRow
    {
        public int Index { get; init; }

        public object? Element { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        public int? FaceCount { get; init; }

        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")")
            + " граней=" + Api5.Raw(FaceCount);
    }
}
