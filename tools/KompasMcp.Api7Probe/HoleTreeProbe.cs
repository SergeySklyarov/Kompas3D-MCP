using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Probe N: two facts the SM-07 acceptance group got wrong, both measured instead of argued.
/// </summary>
/// <remarks>
/// <para>
/// The group <c>HO</c> in <c>scripts/mcp-smoke.py</c> failed three assertions on its first real run.
/// In each case the question was whether the <em>expectation</em> was wrong rather than the product,
/// and the answer could only come from the live session — not from reasoning about the names.
/// </para>
/// <para>
/// <b>N.1 — what number the committed feature carries in the tree.</b> The adapter looks for the hole
/// with <c>entity.type == 52</c>, reading <c>o3d_holeOperation</c> as the tree's own number. The
/// acceptance run printed <c>типов=['25', '583']</c> — the hole was <c>583</c>, not <c>52</c>, and the
/// adapter therefore withheld <c>feature_ref</c> and then reported "нечего править". Probe
/// <see cref="HoleProbe"/> had already recorded <c>ModelObjectType 583</c> for an object API7
/// returned from <c>IHoles3D.Add()</c>, so <c>583</c> is the API7 <em>model object</em> number while
/// <c>52</c> is the API5 <em>NewEntity</em> constant. Those are two different numbering systems, and
/// the adapter's search conflated them. This step prints what each route actually yields for the same
/// committed feature, so the fix is written against the number the tree really uses.
/// </para>
/// <para>
/// <b>N.2 — how the derived countersink depth is read.</b> The catalog and the adapter both explain
/// <c>CountersinkDepth</c> as "the object returns <c>4/tan(угол/2)</c>". The acceptance run measured
/// <c>5.0</c> for a mouth of Ø20, pilot Ø10, 90° — and 5 is not 4/tan(45°). The formula in the docs
/// was fitted to the one row that happened to have <c>rM − rP = 4</c> (Ø18 mouth, Ø10 pilot), which
/// makes "4" look like a constant when it is the radial difference of that row. This step varies the
/// mouth with everything else fixed and prints the reported depth next to <c>(rM − rP)/tan(угол/2)</c>,
/// so the law is either read off the series or the step fails.
/// </para>
/// </remarks>
internal sealed class HoleTreeProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PilotDiameter = 10d;
    private const double CountersinkAngle = 90d;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private static double ThroughHole(double diameterMm) =>
        Math.PI * diameterMm * diameterMm / 4d * PlateThickness;

    /// <summary>
    /// Material the countersink removes BEYOND the pilot: <c>π·h/3·(rM² + rP·rM − 2rP²)</c>, with
    /// <c>h</c> the depth the object reports. Rule read in M.3 off an 11-row table; reused here as a
    /// cross-check on the depth law rather than re-derived.
    /// </summary>
    private static double ConeBeyondPilot(double mouthMm, double pilotMm, double reportedDepthMm)
    {
        var rM = mouthMm / 2d;
        var rP = pilotMm / 2d;
        return Math.PI * reportedDepthMm / 3d * (rM * rM + rP * rM - 2d * rP * rP);
    }

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<int> _pidsBefore = new();
    private readonly List<string> _route = new();

    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private KompasAPI7.IModelContainer? _container;
    private KompasAPI7.IHoles3D? _holes;
    private ProbeStep _plateStep = null!;

    private const int ExpectedLeftovers = 2;

    public HoleTreeProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        SampleProcessesBefore();
        Launch();
        if (_holes is null)
        {
            return;
        }

        try
        {
            TreeNumbering();
            CountersinkDepthLaw();
        }
        finally
        {
            Shutdown();
        }
    }

    private void SampleProcessesBefore()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("N.0", "Свой невидимый экземпляр и пластина-эталон",
            "Сеанс и эталон 100×80×10 подняты?");
        _clock.Restart();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        step.Observe("экземпляр API5 создан за " + _clock.ElapsedMilliseconds + " мс");
        _doc = (ksDocument3D)_app.Document3D();
        _doc.Create(true, true);
        _part = (ksPart)_doc.GetPart(-1);
        _plateStep = step;
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step) is null)
        {
            step.Fail("пластина не построена — измерять нечего");
            return;
        }

        _doc.RebuildDocument();
        var document7 = Transfer(_doc, step);
        _container = document7?.TopPart as KompasAPI7.IModelContainer;
        _holes = _container?.Holes3D;
        if (_holes is null)
        {
            step.Fail("Holes3D недоступно — маршрут API7 не построен");
            return;
        }

        step.Pass("сеанс поднят, эталон построен, Holes3D доступно");
    }

    private void Shutdown()
    {
        var step = _report.Begin("N.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _doc?.close();
        }
        catch (Exception ex)
        {
            step.Observe("документ не закрылся: " + ex.GetType().Name);
        }

        try
        {
            _app.Quit();
            step.Observe("Quit() вызван на собственном экземпляре");
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name);
        }

        var appeared = new List<int>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            appeared = Process.GetProcessesByName("KOMPAS")
                .Select(p =>
                {
                    var pid = p.Id;
                    p.Dispose();
                    return pid;
                })
                .Where(pid => !_pidsBefore.Contains(pid))
                .ToList();
            if (appeared.Count == 0)
            {
                break;
            }

            Thread.Sleep(500);
        }

        step.Observe("посторонних процессов до запуска: " + _pidsBefore.Count
            + "; новых после Quit(): " + appeared.Count
            + (appeared.Count == 0 ? string.Empty : " (" + string.Join(", ", appeared) + ")"));

        if (appeared.Count == 0)
        {
            step.Pass("новых процессов не осталось");
        }
        else if (appeared.Count <= ExpectedLeftovers)
        {
            step.Pass("Quit() не завершает KOMPAS.exe (замер SM-03): осталось " + appeared.Count
                + " собственных процесса-сеанса, что для этой версии ожидаемо");
        }
        else
        {
            step.Fail("осталось больше процессов, чем их создаёт сам метод: " + appeared.Count
                + " против ожидаемых " + ExpectedLeftovers);
        }
    }

    // ════════════════════════════════════════════════════════════════ N.1 ══

    /// <summary>
    /// Под каким номером признак отверстия виден в дереве после того, как его создал API7.
    /// </summary>
    /// <remarks>
    /// Сравниваются ТРИ источника одного и того же признака, потому что «номер типа» здесь не одно
    /// число, а три разных системы нумерации, и путать их — это и есть найденный дефект:
    /// <list type="number">
    /// <item><c>ksEntity.type</c> объекта, которого вернул <c>NewEntity(52)</c> — номер, под которым
    /// признак СОЗДАЁТСЯ через API5;</item>
    /// <item><c>IModelObject.ModelObjectType</c> живого <c>IHole3D</c> из API7 — номер, под которым
    /// признак живёт в модели;</item>
    /// <item><c>ksEntity.type</c> всех элементов обеих коллекций дерева API5
    /// (<c>OperationElement = 110</c> и общая) ДО и ПОСЛЕ создания — то, что видит адаптер, когда
    /// ищет признак, чтобы выдать <c>feature_ref</c>.</item>
    /// </list>
    /// Пункт 3 и решает вопрос: адаптер обязан искать по тому номеру, который там реально появился.
    /// </remarks>
    private void TreeNumbering()
    {
        var step = _report.Begin("N.1", "Под каким номером признак отверстия виден в дереве",
            "Совпадает ли 52 из NewEntity с номером, под которым признак появляется в дереве API5?");

        var before = TreeSnapshot();
        step.Data["tree_before"] = before.ToArray();
        step.Observe("дерево ДО отверстия: " + Render(before));

        // Номер, под которым признак создаётся через API5 — исходная посылка адаптера.
        var newEntityType = Count(() => (int)((ksEntity)_part.NewEntity(52)!).type);
        step.Data["api5_new_entity_type"] = newEntityType;
        step.Observe("NewEntity(52).type = " + Api5.Raw(newEntityType)
            + " → имя " + (newEntityType is int t ? Api5.ObjectTypeName(t) : "<нет>"));

        // Отверстие создаётся маршрутом API7 — точно тем, которым его создаёт продукт.
        if (!CreateBaseHole(step))
        {
            return;
        }

        var live = _holes!.Count > 0 ? _holes[0] : null;
        var modelObjectType = live is null ? (int?)null : Count(() => (int)live.ModelObjectType);
        step.Data["api7_model_object_type"] = modelObjectType;
        step.Observe("IHoles3D[0].ModelObjectType = " + Api5.Raw(modelObjectType)
            + " → имя " + (modelObjectType is int mot ? Api5.ObjectTypeName(mot) : "<нет>"));

        var after = TreeSnapshot();
        step.Data["tree_after"] = after.ToArray();
        step.Observe("дерево ПОСЛЕ отверстия: " + Render(after));

        // Что именно прибавилось. Дифф, а не «есть ли 52 в дереве»: 52 может лежать там и от
        // чего-то другого, и тогда утверждение было бы верным по случайности.
        var beforeKeys = before.Select(Render).ToHashSet(StringComparer.Ordinal);
        var added = after.Where(r => !beforeKeys.Contains(Render(r))).ToList();
        step.Data["tree_added"] = added.ToArray();
        step.Observe("прибавилось записей: " + added.Count + " → " + Render(added));

        var addedTypes = added.Select(r => r.Type).Distinct().OrderBy(v => v).ToList();
        step.Data["tree_added_types"] = addedTypes.ToArray();
        step.Data["adapter_searches_for"] = 52;
        step.Data["adapter_would_find"] = addedTypes.Contains(52);

        if (addedTypes.Count == 0)
        {
            step.Fail("признак не появился в дереве API5 ни под каким номером: адаптеру нечего искать, "
                + "и «feature_ref_withheld» было бы следствием этого, а не поиска по 52");
            return;
        }

        var matches = addedTypes.Contains(52);
        step.Observe(matches
            ? "номер 52 в дереве ЕСТЬ: поиск адаптера по 52 верен, а расхождение приёмки — в другом"
            : "номер 52 в дереве ОТСУТСТВУЕТ; признак живёт под " + string.Join(", ", addedTypes)
              + " — поиск по 52 не находит созданное API7 отверстие, и это дефект адаптера");

        step.Data["conclusion"] = matches
            ? "tree_uses_52"
            : "tree_uses_" + string.Join("_", addedTypes) + "_not_52";

        step.Pass(matches
            ? "Подтверждено: дерево показывает признак под номером 52 (NewEntity(52) и дерево согласованы)."
            : "Измерено: API7-признак виден в дереве под " + string.Join(", ", addedTypes)
              + ", а не под 52. Поиск адаптера по 52 обязан быть исправлен на этот номер.");
    }

    /// <summary>
    /// Снимок дерева API5: обе коллекции, которые адаптер вообще смотрит. Обе — потому что
    /// «OperationElement = 110» это лишь одна из них, и отсутствие признака в одной не означает
    /// отсутствия в дереве.
    /// </summary>
    private List<(string Where, int Index, int Type, string Name)> TreeSnapshot()
    {
        var rows = new List<(string, int, int, string)>();
        foreach (var (where, kind) in new[]
                 {
                     ("OperationElement(110)", (short)110),
                     ("EntityCollection(-1)", (short)-1),
                 })
        {
            try
            {
                if (_part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity)
                    {
                        rows.Add((where, i, entity.type, Api5.SafeObject(() => entity.name) as string ?? "<без имени>"));
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add((where, -1, -1, "чтение бросило: " + ex.GetType().Name));
            }
        }

        return rows;
    }

    private static string Render((string Where, int Index, int Type, string Name) row) =>
        row.Where + "[" + row.Index + "] type=" + row.Type + " («" + row.Name + "»)";

    private static string Render(IEnumerable<(string Where, int Index, int Type, string Name)> rows) =>
        rows.Any() ? string.Join("; ", rows.Select(Render)) : "<пусто>";

    private bool CreateBaseHole(ProbeStep step)
    {
        var hole = _holes!.Add();
        if (hole is null)
        {
            step.Fail("IHoles3D.Add() вернул null");
            return false;
        }

        try
        {
            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.Diameter = PilotDiameter;
            hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            if (hole is KompasAPI7.IHoleDisposal disposal)
            {
                disposal.BaseSurface = TopFace(step);
                disposal.Perpendicular = true;
            }

            hole.Update();
            _part.RebuildModel();
            _doc.RebuildDocument();
            return true;
        }
        catch (Exception ex)
        {
            step.Fail("базовое отверстие не создано: " + HResult.Describe(ex));
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════ N.2 ══

    /// <summary>
    /// Как читается ПРОИЗВОДНАЯ глубина зенковки при разных устьях.
    /// </summary>
    /// <remarks>
    /// Пилот, угол и толщина фиксированы; меняется только диаметр устья. Каждая строка — своя
    /// пластина, иначе снятый объём был бы суммой по нескольким отверстиям и ни одна строка ни с
    /// чем бы не сошлась. Печатается возвращённая глубина рядом с <c>(rM − rP)/tan(угол/2)</c>: если
    /// закон верен, согласие должно быть не в одной строке, а во всей серии — константа, подогнанная
    /// под одну точку, здесь не пройдёт.
    /// </remarks>
    private void CountersinkDepthLaw()
    {
        var step = _report.Begin("N.2", "Производная глубина зенковки при разных устьях",
            "Глубина это 4/tan(угол/2) или (rM − rP)/tan(угол/2)?");

        var rows = new List<string>();
        var agreeDepth = 0;
        var agreeVolume = 0;
        var total = 0;

        foreach (var mouth in new[] { 14d, 16d, 18d, 20d, 24d })
        {
            if (!FreshPlate())
            {
                rows.Add("устье Ø" + mouth + " → свежая пластина не построена");
                continue;
            }

            var before = Api5.Volume(_part);
            var recorded = MeasureCountersink(step, mouth, out var reported, out var failure);
            if (failure is not null)
            {
                rows.Add("устье Ø" + mouth + " → замер не получен: " + failure);
                continue;
            }

            var extra = recorded - ThroughHole(PilotDiameter);
            var rM = mouth / 2d;
            var rP = PilotDiameter / 2d;
            var predictedDepth = (rM - rP) / Math.Tan(CountersinkAngle / 2d * Math.PI / 180d);
            var constantHypothesis = 4d / Math.Tan(CountersinkAngle / 2d * Math.PI / 180d);

            total++;
            var depthOk = reported is double h && Math.Abs(h - predictedDepth) <= 1e-6d;
            // Кросс-проверка независимым числом: если глубина верна, то объём сверх пилота обязан
            // сойтись по правилу M.3, посчитанному на ЭТОЙ возвращённой глубине. Одно и то же
            // утверждение, полученное двумя разными измерениями, — это и отличает закон от подгонки.
            var predictedExtra = reported is double hh ? ConeBeyondPilot(mouth, PilotDiameter, hh) : double.NaN;
            var volumeOk = reported is not null
                && Math.Abs(predictedExtra - extra) <= Tolerance(extra);

            rows.Add("устье Ø" + mouth + " → возвращено h=" + Api5.Num(reported)
                + " (задавали 4); (rM−rP)/tan45°=" + Api5.Num(predictedDepth)
                + " " + (depthOk ? "совпало" : "НЕ совпало")
                + "; 4/tan45°=" + Api5.Num(constantHypothesis)
                + " " + (reported is double d2 && Math.Abs(d2 - constantHypothesis) <= 1e-6d ? "совпало" : "НЕ совпало")
                + "; снято сверх пилота " + Api5.Num(extra) + ", по правилу M.3 от h → "
                + Api5.Num(predictedExtra) + " " + (volumeOk ? "совпало" : "НЕ совпало"));
            if (depthOk)
            {
                agreeDepth++;
            }

            if (volumeOk)
            {
                agreeVolume++;
            }
        }

        foreach (var row in rows)
        {
            step.Observe(row);
        }

        step.Data["rows"] = rows.ToArray();
        step.Data["rows_total"] = total;
        step.Data["depth_law_agreements"] = agreeDepth;
        step.Data["volume_rule_agreements"] = agreeVolume;
        step.Data["depth_law"] = "h = (rM − rP)/tan(угол/2)";
        step.Data["rejected_law"] = "h = 4/tan(угол/2) — константа 4 совпадает лишь со строкой, где rM−rP=4 (устье Ø18, пилот Ø10)";

        if (total == 0)
        {
            step.Fail("ни одной строки не измерено — закон глубины не установлен");
        }
        else if (agreeDepth == total && agreeVolume == total)
        {
            step.Pass("Закон подтверждён на всей серии: h = (rM − rP)/tan(угол/2), и снятый объём сходится "
                + "по правилу M.3, посчитанному на возвращённой глубине (" + total + " из " + total + " строк).");
        }
        else
        {
            step.Fail("Сошлось " + agreeDepth + " из " + total + " по глубине и " + agreeVolume
                + " по объёму — закон не подтверждён: " + string.Join(" | ", rows));
        }
    }

    /// <summary>Одна зенковка на свежей пластине: (снятый объём, возвращённая глубина, причина отказа).</summary>
    private double MeasureCountersink(ProbeStep step, double mouth, out double? reported, out string? failure)
    {
        reported = null;
        failure = null;
        var before = Api5.Volume(_part);
        var hole = _holes!.Add();
        if (hole is null)
        {
            failure = "Add() вернул null";
            return double.NaN;
        }

        try
        {
            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.Diameter = PilotDiameter;
            hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            if (hole is KompasAPI7.IHoleDisposal disposal)
            {
                disposal.BaseSurface = TopFace(step);
                disposal.Perpendicular = true;
            }

            if (hole.HoleParameters is not KompasAPI7.ICountersinkHoleParameters countersink)
            {
                failure = "нет ICountersinkHoleParameters";
                return double.NaN;
            }

            countersink.CountersinkType = (short)ksCountersinkTypeEnum.ksCTDiameterAngle;
            countersink.CountersinkDiameter = mouth;
            countersink.CountersinkAngle = CountersinkAngle;
            // Глубина записывается нулём осознанно: предмет замера — именно то, что объект считает
            // САМ. Ненулевая запись ничего не меняет (измерено M.3), но ноль делает это очевидным.
            countersink.CountersinkDepth = 0d;

            hole.Update();
            _part.RebuildModel();
            _doc.RebuildDocument();

            reported = countersink.CountersinkDepth;
            if (before is not double b || Api5.Volume(_part) is not double a)
            {
                failure = "объём до/после не прочитан";
                return double.NaN;
            }

            return b - a;
        }
        catch (Exception ex)
        {
            failure = HResult.Describe(ex);
            return double.NaN;
        }
    }

    // ═══════════════════════════════════════════════════════════════ helpers ══

    private bool FreshPlate()
    {
        try
        {
            _doc?.close();
        }
        catch (Exception)
        {
            // Заменяется ниже; осиротевшие процессы — забота N.Z, а не этого вызова.
        }

        _doc = (ksDocument3D)_app.Document3D();
        if (Api5.SafeBool(() => _doc.Create(true, true)) != true)
        {
            return false;
        }

        _part = (ksPart)_doc.GetPart(-1);
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, _plateStep) is null)
        {
            return false;
        }

        _doc.RebuildDocument();
        var document7 = Transfer(_doc, null);
        _container = document7?.TopPart as KompasAPI7.IModelContainer;
        _holes = _container?.Holes3D;
        return _holes is not null;
    }

    private KompasAPI7.IKompasDocument3D? Transfer(ksDocument3D document, ProbeStep? step)
    {
        try
        {
            var result = _app.TransferInterface(document, 2 /* ksAPI7Dual */, 0);
            if (result is KompasAPI7.IKompasDocument3D transferred)
            {
                _route.Add("документ → API7: OK");
                return transferred;
            }

            _route.Add("документ → API7: " + Api5.RuntimeName(result));
            step?.Observe("TransferInterface(документ) не дал IKompasDocument3D: " + Api5.RuntimeName(result));
            return null;
        }
        catch (Exception ex)
        {
            _route.Add("документ → API7: бросил " + HResult.Describe(ex));
            step?.Observe("TransferInterface(документ) бросил: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Самая большая грань по площади, перенесённая в API7. Тот же маршрут, что в пробе M: опорная
    /// грань — это то, от чего зависит, куда встанет отверстие, и вторая реализация этого была бы
    /// вторым поводом ошибиться.
    /// </summary>
    private KompasAPI7.IModelObject? TopFace(ProbeStep step)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("API5: GetMainBody()/FaceCollection() недоступны — базовую грань найти нечем.");
            return null;
        }

        object? largest = null;
        var bestArea = double.MinValue;
        var count = faces.GetCount();
        for (var i = 0; i < count; i++)
        {
            var element = faces.GetByIndex(i);
            if (element is not ksFaceDefinition face)
            {
                continue;
            }

            var area = Api5.SafeDouble(() => face.GetArea(Api5.LengthMm));
            if (area is double value && value > bestArea)
            {
                bestArea = value;
                largest = element;
            }
        }

        if (largest is null)
        {
            step.Observe("API5: ни одной грани с читаемой площадью (FaceCollection дала " + count + " элементов).");
            return null;
        }

        try
        {
            return _app.TransferInterface(largest, 2, 0) as KompasAPI7.IModelObject;
        }
        catch (Exception ex)
        {
            step.Observe("перенос грани в API7 бросил: " + HResult.Describe(ex));
            return null;
        }
    }

    private static int Count(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
