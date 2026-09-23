using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба I — чем ОТЛИЧАЕТСЯ только что созданный признак от уже существующего, когда
/// отображаемое имя у них ОДИНАКОВОЕ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем.</b> Клиентская приёмка 19.09.2026 (дефект <c>REPOSITION-SECOND-FEATURE-ADDRESS-AMBIGUOUS</c>)
/// измерила: второй признак изменения положения в одном документе получает ТО ЖЕ имя, что и первый
/// («Изменение положения : Тело 1»), и правило адаптера «новый = имя, которого не было в снимке ДО»
/// отбрасывает его: <c>candidates=[]</c>. Геометрия при этом построена верно.
/// </para>
/// <para>
/// <b>Что здесь измеряется.</b> Не «какой маршрут кажется надёжнее», а что отвечает живой API на
/// каждый из кандидатов, объявленных ЗАРАНЕЕ:
/// </para>
/// <list type="number">
/// <item>устойчивость адреса элемента между двумя чтениями коллекции 110 (тот же COM-объект или нет);</item>
/// <item><c>ksEntityCollection.FindIt(entity)</c> — возвращает ли индекс и что отдаёт на чужом объекте;</item>
/// <item>жива ли коллекция, взятая ДО операции, и находит ли она в себе элементы ПОСЛЕ операции;</item>
/// <item><c>ksDocument3D.GetLastFeature()</c> — указывает ли он на только что созданный признак;</item>
/// <item><c>ksFeature.GetObject()</c> — связан ли элемент дерева с объектом API7, которым признак создан;</item>
/// <item>порядок элементов в коллекции 110 после добавления (в конец или нет).</item>
/// </list>
/// <para>
/// <b>Почему это отдельная проба, а не правка адаптера сразу.</b> Правило проекта: измерение
/// расходится с ожиданием — первым под сомнение ставится ОЖИДАНИЕ. Маршрут, выбранный по догадке,
/// даёт зелёную строку, которая держится на совпадении имён, — то есть ровно тот дефект, который
/// здесь и вскрыт. Ни один из кандидатов не объявляется рабочим до того, как он различает ДВА
/// одноимённых признака на одном документе.
/// </para>
/// </remarks>
internal sealed class IdentityProbe
{
    // Эталон §4.3 наряда: A = [0,20]×[0,10]×[0,5], V=1000; S — постороннее тело.
    private const double Ax0 = 0d, Ax1 = 20d, Ay0 = 0d, Ay1 = 10d, Az = 5d;
    private const double Sx0 = 100d, Sx1 = 110d, Sy0 = 0d, Sy1 = 10d, Sz = 10d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public IdentityProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "feature-identity.json"),
            Path.Combine(options.ReportDir, "feature-identity.md"));

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
            var doc = NewPart(out var part);
            var step0 = _report.Begin("I.0", "Постановка: A=[0,20]×[0,10]×[0,5] и постороннее S",
                "Тела построены и видны дереву до первой операции?");
            if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step0, "A")
                || !ExtrudeRect(doc, part, Sx0, Sx1, Sy0, Sy1, Sz, step0, "S"))
            {
                step0.Fail("эталон §4.3 не построен — дальнейшее измеряло бы пустоту");
                return;
            }

            var bodies = BodyRows(part);
            step0.Observe("тел " + bodies.Count + ": " + string.Join(" | ", bodies.Select(b => b.Describe())));
            step0.Pass("тела A и S построены");

            var baseline = Baseline(part);
            var findIt = FindItSemantics(part, doc, baseline);
            var first = CreateReposition(
                doc, part, Translate(new[] { 10d, 0d, 0d }), "перенос [10,0,0]", "I.2",
                (Ax0, Ay0, 0d, Ax1, Ay1, Az), (Ax0 + 10d, Ay0, 0d, Ax1 + 10d, Ay1, Az));
            var second = CreateReposition(
                doc, part, RotationZ(90d, 0d, 0d), "поворот +90° вокруг Z через начало", "I.3",
                (Ax0 + 10d, Ay0, 0d, Ax1 + 10d, Ay1, Az), (-10d, 10d, 0d, 0d, 30d, Az));

            Verdict(baseline, findIt, first, second);
            Lifecycle(doc, part, second);
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("I.Z0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
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
        step.Observe("свой процесс: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("I.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit: " + HResult.Describe(ex));
        }

        // Ожидание обязательно: без него STA-поток выходит из pump раньше процесса, и «уборка не
        // подтверждена» выглядит как зависший COM-вызов, хотя это лишь незавершённый Quit.
        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 15000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var alive = NewProcesses();
        step.Observe("своих процессов после Quit: " + alive.Count
            + (alive.Count == 0 ? string.Empty : " (" + string.Join(", ", alive) + ")")
            + ", ожидание " + waited + " мс");
        step.Data["orphans"] = alive;
        if (alive.Count == 0)
        {
            step.Pass("сеанс освобождён");
        }
        else
        {
            step.Unknown("процесс остался — уборка требует внимания");
        }
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

    // ══════════════════════════════════════════════════════════════ I.1 ══

    /// <summary>
    /// Два чтения коллекции 110 подряд: тот же ли COM-объект лежит в одном и том же индексе.
    /// Это основание для любого сопоставления «до/после»: если адрес не устойчив даже между двумя
    /// соседними чтениями, то и разность множеств по указателю ничего не значит.
    /// </summary>
    private List<ElementRow> Baseline(ksPart part)
    {
        var step = _report.Begin("I.1", "Устойчивость адреса элемента между двумя чтениями коллекции 110",
            "Отдаёт ли GetByIndex(i) один и тот же COM-объект при двух последовательных обходах?");
        var rows = ReadTree(part, step, "чтение-1");
        var again = ReadTree(part, step, "чтение-2");

        var stable = rows.Count == again.Count;
        for (var i = 0; stable && i < rows.Count; i++)
        {
            stable = rows[i].ElementPtr == again[i].ElementPtr
                     && rows[i].Name == again[i].Name
                     && rows[i].Type == again[i].Type;
        }

        step.Data["count_1"] = rows.Count;
        step.Data["count_2"] = again.Count;
        step.Data["same_object_per_index"] = stable;
        step.Observe("устойчивость по указателю элемента и по имени/типу: " + stable);
        if (stable)
        {
            step.Pass("адрес элемента устойчив между чтениями");
        }
        else
        {
            step.Unknown("адрес элемента НЕ устойчив между двумя чтениями — сопоставление по указателю негодно");
        }

        return rows;
    }

    /// <summary>
    /// Семантика <c>FindIt</c>: что он отдаёт на своём элементе и что на чужом. Без этого «FindIt
    /// вернул -1» нельзя читать как «не в коллекции».
    /// </summary>
    private (int OnSelf, int OnForeign, int Count) FindItSemantics(
        ksPart part, ksDocument3D doc, List<ElementRow> baseline)
    {
        var step = _report.Begin("I.1b", "Семантика ksEntityCollection.FindIt",
            "FindIt(entity) отдаёт индекс? что отдаёт на объекте, которого в коллекции нет?");
        var collection = Collection(part);
        if (collection is null)
        {
            step.Fail("EntityCollection(110) не получена");
            return (-999, -999, -1);
        }

        var count = SafeInt(collection.GetCount) ?? -1;
        var onSelf = baseline.Count > 0 && baseline[0].Raw is { } first
            ? (SafeInt(() => collection.FindIt(first)) ?? -998)
            : -997;

        // Чужой объект — эскиз: он есть в документе, но в коллекцию 110 не входит.
        object? foreign = SafeObject(() => doc.GetPart(-1) is ksPart p ? p.GetDefaultEntity(Api5.PlaneXoy) : null);
        var onForeign = foreign is null ? -996 : (SafeInt(() => collection.FindIt(foreign)) ?? -995);

        step.Data["count"] = count;
        step.Data["findit_on_self_index0"] = onSelf;
        step.Data["findit_on_foreign"] = onForeign;
        step.Observe("GetCount=" + count + "; FindIt(элемент[0])=" + onSelf + "; FindIt(плоскость XOY)=" + onForeign);
        step.Pass("семантика FindIt измерена");
        return (onSelf, onForeign, count);
    }

    // ══════════════════════════════════════════════════════════════ I.2 / I.3 ══

    private sealed record CreationResult(
        string Label,
        List<ElementRow> After,
        List<ElementRow> NewByPointer,
        List<string> PointerVerdict,
        string? LastFeature,
        string? LastFeatureMatch,
        string? TreeTypeOfNew,
        ksEntityCollection? HeldCollection = null);

    /// <summary>
    /// Создаёт ОДИН признак изменения положения и измеряет, какими маршрутами его элемент в дереве
    /// отличается от уже существовавших. Коллекция «до» берётся ЖИВОЙ и удерживается: вопрос
    /// «переживает ли она мутацию» — часть измерения, а не предположение.
    /// </summary>
    private CreationResult CreateReposition(
        ksDocument3D doc, ksPart part, double[] matrix, string label, string id,
        (double X0, double Y0, double Z0, double X1, double Y1, double Z1) targetBefore,
        (double X0, double Y0, double Z0, double X1, double Y1, double Z1) targetAfter)
    {
        var step = _report.Begin(id, label,
            "Каким маршрутом новый элемент дерева отличается от существующих при одинаковом имени?");

        var collectionBefore = Collection(part);
        var before = ReadTree(part, step, "до операции");
        var beforeCount = collectionBefore is null ? -1 : SafeInt(collectionBefore.GetCount) ?? -1;
        var beforePointers = before.Select(r => r.ElementPtr).ToHashSet();

        var target = FindBody(BodyRows(part),
            targetBefore.X0, targetBefore.Y0, targetBefore.Z0,
            targetBefore.X1, targetBefore.Y1, targetBefore.Z1);
        if (target is null)
        {
            step.Fail("тело-цель A не найдено среди тел документа по габариту "
                + Api5.Raw(targetBefore.X0) + "…" + Api5.Raw(targetBefore.X1));
            return new CreationResult(label, before, new List<ElementRow>(), new List<string>(), null, null, null, collectionBefore);
        }

        var container = Container(doc);
        if (container?.BodyRepositions?.Add() is not { } createdObject || createdObject is not IBodyReposition created)
        {
            step.Fail("BodyRepositions.Add() недоступен");
            return new CreationResult(label, before, new List<ElementRow>(), new List<string>(), null, null, null, collectionBefore);
        }

        if (Transfer(target) is not IKompasAPIObject body7)
        {
            step.Fail("тело не переносится в API7 как IKompasAPIObject");
            return new CreationResult(label, before, new List<ElementRow>(), new List<string>(), null, null, null, collectionBefore);
        }

        created.RepositionBody = body7;
        created.Position.InitByMatrix3D(matrix);
        var updated = created.Update();
        part.RebuildModel();
        doc.RebuildDocument();
        step.Observe("RepositionBody=тело A, InitByMatrix3D, Update()=" + updated);

        var after = ReadTree(part, step, "после операции");
        var newByPointer = after.Where(r => !beforePointers.Contains(r.ElementPtr)).ToList();
        var sameNameAsExisting = newByPointer.Count > 0 && before.Any(r => r.Name == newByPointer[0].Name);
        step.Data["count_before"] = beforeCount;
        step.Data["count_after"] = after.Count;
        step.Data["names_after"] = after.Select(r => r.Name).ToArray();
        step.Data["new_by_pointer"] = newByPointer.Select(r => r.Describe()).ToArray();
        step.Data["new_name_collides"] = sameNameAsExisting;
        step.Observe("элементов " + beforeCount + " → " + after.Count + "; новых по указателю: " + newByPointer.Count);
        foreach (var row in newByPointer)
        {
            step.Observe("  новый: " + row.Describe());
        }

        // Контроль геометрии: отказ адресации не должен выдаваться за отказ ядра. Тело-цель обязано
        // оказаться там, куда его послали, — иначе измеряется не тот случай.
        var moved = FindBody(BodyRows(part),
            targetAfter.X0, targetAfter.Y0, targetAfter.Z0,
            targetAfter.X1, targetAfter.Y1, targetAfter.Z1);
        step.Data["target_after"] = moved?.Describe() ?? "не найдено по объявленному габариту";
        step.Observe("тело-цель после операции: " + (moved?.Describe() ?? "НЕ найдено по объявленному габариту"));

        step.Observe("имя нового совпадает с уже существовавшим: " + sameNameAsExisting
            + " — именно на этом падает фильтр «имя, которого не было в снимке»");

        // Кандидат 1: FindIt на УДЕРЖАННОЙ коллекции «до».
        var verdicts = new List<string>();
        if (collectionBefore is not null)
        {
            var live = SafeInt(collectionBefore.GetCount) ?? -1;
            step.Data["held_collection_count_after"] = live;
            step.Observe("удержанная коллекция «до» после операции: GetCount=" + live
                + " (живой вид: " + (live == after.Count) + ")");
            foreach (var row in after)
            {
                var index = SafeInt(() => collectionBefore.FindIt(row.Raw!));
                verdicts.Add(row.Name + " [" + row.Index + "] FindIt(held)=" + Api5.Raw(index)
                    + " " + (index is >= 0 && index < beforeCount ? "был-до" : "НОВЫЙ"));
            }
        }

        // Кандидат 2: свежая коллекция + FindIt на ней же (контроль: должен находить всех).
        var fresh = Collection(part);
        var freshVerdict = new List<string>();
        if (fresh is not null)
        {
            foreach (var row in after)
            {
                var index = SafeInt(() => fresh.FindIt(row.Raw!));
                freshVerdict.Add("[" + row.Index + "]=" + Api5.Raw(index));
            }
        }

        step.Data["findit_held"] = verdicts.ToArray();
        step.Data["findit_fresh"] = freshVerdict.ToArray();

        // Кандидат 3: GetLastFeature().
        string? lastFeature = null;
        string? lastMatch = null;
        try
        {
            var last = doc.GetLastFeature();
            if (last is null)
            {
                lastFeature = "null";
            }
            else
            {
                var lastPtr = Ptr(last);
                var lastFeatureObject = (last as ksFeature)?.name ?? (last as ksEntity)?.name ?? "<без имени>";
                var lastType = (last as ksFeature)?.type ?? (last as ksEntity)?.type;
                lastFeature = "«" + lastFeatureObject + "» type=" + Api5.Raw(lastType)
                    + " CLR=" + Api5.RuntimeName(last) + " ptr=" + Hex(lastPtr);
                var matched = after.FirstOrDefault(r => r.ElementPtr == lastPtr || r.FeaturePtr == lastPtr);
                lastMatch = matched is null
                    ? "не совпал ни с одним элементом коллекции 110"
                    : "совпал с элементом [" + matched.Index + "] «" + matched.Name + "» ("
                      + (newByPointer.Contains(matched) ? "НОВЫЙ" : "существовавший") + ")";
            }
        }
        catch (Exception ex)
        {
            lastFeature = HResult.Describe(ex);
        }

        step.Data["get_last_feature"] = lastFeature;
        step.Data["get_last_feature_match"] = lastMatch;
        step.Observe("GetLastFeature(): " + lastFeature);
        step.Observe("  сопоставление: " + lastMatch);

        var treeTypeOfNew = newByPointer.Count == 1
            ? ObjectTypeName(newByPointer[0].Type ?? -1) + " (" + newByPointer[0].Type + ")"
            : null;

        if (newByPointer.Count == 1 && sameNameAsExisting)
        {
            step.Pass("новый элемент однозначно отделён по указателю при совпадающем имени");
        }
        else if (newByPointer.Count == 1)
        {
            step.Pass("новый элемент однозначно отделён по указателю");
        }
        else
        {
            step.Unknown("новых элементов по указателю " + newByPointer.Count + " — разность множеств не различает");
        }

        return new CreationResult(
            label, after, newByPointer, verdicts, lastFeature, lastMatch, treeTypeOfNew, collectionBefore);
    }

    // ══════════════════════════════════════════════════════════════ verdict ══

    private void Verdict(
        List<ElementRow> baseline,
        (int OnSelf, int OnForeign, int Count) findIt,
        CreationResult first,
        CreationResult second)
    {
        var step = _report.Begin("I.4", "Свод: какой маршрут различает ВТОРОЙ одноимённый признак",
            "Есть ли маршрут, который на двух одноимённых признаках указывает именно на созданный?");

        var rows = new List<string>();
        rows.Add("FindIt(элемент[0]) = " + findIt.OnSelf + " при GetCount=" + findIt.Count
            + " → 0-based, " + (findIt.OnSelf == 0 ? "совпадает с индексом" : "НЕ совпадает с индексом"));
        rows.Add("FindIt(чужой объект) = " + findIt.OnForeign
            + (findIt.OnForeign < 0 ? " → отрицательное число означает «не найден»" : " → чужой объект найден, семантика иная"));
        rows.Add("разность по указателю на первом создании: новых " + first.NewByPointer.Count
            + "; на втором: " + second.NewByPointer.Count);
        rows.Add("имена: " + string.Join(" | ", (second.After ?? new List<ElementRow>()).Select(r => r.Name)));
        rows.Add("GetLastFeature() после первого: " + first.LastFeature + " → " + first.LastFeatureMatch);
        rows.Add("GetLastFeature() после второго: " + second.LastFeature + " → " + second.LastFeatureMatch);
        rows.Add("тип нового элемента: " + (second.TreeTypeOfNew ?? first.TreeTypeOfNew ?? "не определён"));

        foreach (var row in rows)
        {
            step.Observe(row);
        }

        step.Data["rows"] = rows.ToArray();
        step.Data["baseline_count"] = baseline.Count;

        // Контроль семантики «удержанная коллекция — снимок»: после ВТОРОЙ операции коллекция,
        // удержанная перед ПЕРВОЙ, обязана по-прежнему считать новым элемент первой операции и
        // считать новым элемент второй. Если она «ожила» и обновилась, разность множеств по ней
        // означала бы не «было ли ДО», а «что вообще есть», и маршрут был бы негоден.
        var snapshotRows = new List<string>();
        if (first.HeldCollection is { } held)
        {
            var heldCount = SafeInt(held.GetCount) ?? -1;
            snapshotRows.Add("удержанная перед I.2 коллекция после обеих операций: GetCount=" + heldCount
                + " (снимок ожидает " + baseline.Count + ")");
            foreach (var row in second.After ?? new List<ElementRow>())
            {
                var index = SafeInt(() => held.FindIt(row.Raw!));
                snapshotRows.Add("  «" + row.Name + "» [" + row.Index + "] FindIt(снимок I.2)=" + Api5.Raw(index));
            }
        }

        foreach (var row in snapshotRows)
        {
            step.Observe(row);
        }

        step.Data["snapshot_rows"] = snapshotRows.ToArray();

        var snapshotUsable = snapshotRows.Count > 0
                             && second.NewByPointer.Count == 1
                             && snapshotRows.Any(r => r.Contains("снимок I.2)=-1"));
        step.Data["snapshot_semantics_usable"] = snapshotUsable;

        var twoDistinct = first.NewByPointer.Count == 1 && second.NewByPointer.Count == 1
                          && first.NewByPointer[0].ElementPtr != second.NewByPointer[0].ElementPtr;
        var lastFeatureUsable = second.LastFeatureMatch is not null
                                && second.LastFeatureMatch.Contains("НОВЫЙ");
        step.Data["pointer_difference_distinguishes"] = twoDistinct;
        step.Data["get_last_feature_distinguishes"] = lastFeatureUsable;

        if (twoDistinct)
        {
            step.Pass("разность множеств по идентичности COM-объекта различает оба признака при одинаковом имени");
        }
        else
        {
            step.Unknown("ни один объявленный маршрут не различает оба признака");
        }
    }

    // ══════════════════════════════════════════════════════════════ I.5 ══

    /// <summary>
    /// Жизненный цикл двух ОДНОИМЁННЫХ признаков: подавление, снятие подавления, удаление — по
    /// тому же маршруту, которым идёт продукт (<c>ksFeature.excluded</c> + <c>RebuildDocument()</c>).
    /// </summary>
    /// <remarks>
    /// Наряд §4.2 требует отдельного доказательства восстановления: клиентская приёмка 19.09.2026
    /// показала, что после снятия подавления положение тела не вернулось, а признаков стало 2 из 3.
    /// Догадка «это следствие дефекта адресации» проверке не подлежит — здесь измеряется сам
    /// маршрут, и каждое состояние называется числами: состав коллекции 110, габариты тел и
    /// живость объекта признака.
    /// </remarks>
    private void Lifecycle(ksDocument3D doc, ksPart part, CreationResult last)
    {
        var step = _report.Begin("I.5", "Жизненный цикл двух одноимённых признаков: подавление и снятие",
            "Возвращает ли снятие подавления состояние ПОСЛЕ, когда в дереве два признака с одним именем?");

        var before = ReadTree(part, step, "исходное состояние");
        var repositions = before.Where(r => r.Type == 79 && r.Entity is not null).ToList();
        if (repositions.Count != 2)
        {
            step.Fail("в дереве не ровно два признака типа 79, а " + repositions.Count
                + " — измерять нечего");
            return;
        }

        var first = repositions[0];
        var second = repositions[1];
        step.Observe("первый одноимённый: [" + first.Index + "] ptr=" + Hex(first.ElementPtr));
        step.Observe("второй одноимённый: [" + second.Index + "] ptr=" + Hex(second.ElementPtr));
        step.Observe("исходное состояние тел: " + Describe(BodyRows(part)));

        // Последовательность выбрана так, чтобы каждый вопрос был отделён от соседнего:
        // E1/E2 — подавление и снятие ВТОРОГО (последнего в дереве);
        // E3/E4 — подавление и снятие ПЕРВОГО: здесь и проверяется, каскадно ли подавление;
        // E5     — снятие подавления ВТОРОГО после E4: возвращает ли оно состояние ПОСЛЕ;
        // E6     — удаление ВТОРОГО: возвращает ли оно состояние ПОСЛЕ ПЕРВОГО.
        var states = new List<string>
        {
            Suppress(part, doc, second, true, step, "E1 подавление ВТОРОГО"),
            Suppress(part, doc, second, false, step, "E2 снятие подавления ВТОРОГО"),
            Suppress(part, doc, first, true, step, "E3 подавление ПЕРВОГО"),
            Suppress(part, doc, first, false, step, "E4 снятие подавления ПЕРВОГО"),
            Suppress(part, doc, second, false, step, "E5 снятие подавления ВТОРОГО после E4"),
            Delete(part, doc, second, step, "E6 удаление ВТОРОГО"),
        };

        step.Data["states"] = states.ToArray();
        step.Data["reposition_pointers"] = new[] { Hex(first.ElementPtr), Hex(second.ElementPtr) };
        step.Data["last_created_pointer"] = last.NewByPointer.Count == 1
            ? Hex(last.NewByPointer[0].ElementPtr)
            : "<не определён>";

        // Каскад подавления: подавление ПЕРВОГО убирает из коллекции 110 больше одного элемента.
        var cascade = states[2].Contains("элементов 2");
        var restoreFirstAlone = states[3].Contains("элементов 3")
                                && states[3].Contains("габарит (10, 0, 0)");
        var restoreSecondAfter = states[4].Contains("элементов 4")
                                 && states[4].Contains("габарит (-10, 10, 0)");
        var deleteSecond = states[5].Contains("элементов 3")
                           && states[5].Contains("габарит (10, 0, 0)");

        step.Data["suppression_cascades"] = cascade;
        step.Data["restore_of_first_alone"] = restoreFirstAlone;
        step.Data["restore_of_second_after_cascade"] = restoreSecondAfter;
        step.Data["delete_of_second_confirmed"] = deleteSecond;

        step.Observe("КАСКАД: подавление первого убрало из коллекции 110 больше одного элемента: " + cascade);
        step.Observe("снятие подавления ПЕРВОГО вернуло состояние ПОСЛЕ ПЕРВОГО, но НЕ ПОСЛЕ ОБОИХ: "
            + restoreFirstAlone);
        step.Observe("снятие подавления ВТОРОГО после E4 вернуло состояние ПОСЛЕ ОБОИХ: " + restoreSecondAfter);

        if (cascade && restoreFirstAlone && restoreSecondAfter && deleteSecond)
        {
            step.Pass("жизненный цикл измерен по шагам: подавление каскадно, восстановление — по каждому "
                + "признаку отдельно, состояние ПОСЛЕ достигается только снятием подавления с обоих");
        }
        else
        {
            step.Unknown("последовательность не дала ожидаемых состояний — смотри строки состояний");
        }
    }

    /// <summary>Удаление признака маршрутом продукта: <c>ksDocument3D.DeleteObject(entity)</c>.</summary>
    private string Delete(ksPart part, ksDocument3D doc, ElementRow row, ProbeStep step, string label)
    {
        var line = label + ": ";
        try
        {
            if (row.Entity is null)
            {
                line += "элемент не отвечает ksEntity";
                step.Observe(line);
                return line;
            }

            var deleted = doc.DeleteObject(row.Entity);
            doc.RebuildDocument();
            var tree = ReadTree(part, step, label);
            line += "DeleteObject=" + deleted + ", элементов " + tree.Count + ", "
                + Describe(BodyRows(part))
                + ", имена=[" + string.Join(" | ", tree.Select(r => r.Name)) + "]";
        }
        catch (Exception ex)
        {
            line += HResult.Describe(ex);
        }

        step.Observe(line);
        return line;
    }

    /// <summary>
    /// Один акт подавления/снятия маршрутом продукта и его полное описание.
    /// </summary>
    private string Suppress(
        ksPart part, ksDocument3D doc, ElementRow row, bool suppressed, ProbeStep step, string label)
    {
        var line = label + ": ";
        try
        {
            var entity = row.Entity;
            if (entity is null)
            {
                line += "элемент не отвечает ksEntity — маршрут неприменим";
                step.Observe(line);
                return line;
            }

            if (entity.GetFeature() is not ksFeature feature)
            {
                line += "GetFeature() не даёт ksFeature";
                step.Observe(line);
                return line;
            }

            feature.excluded = suppressed;
            doc.RebuildDocument();

            var tree = ReadTree(part, step, label);
            var bodies = BodyRows(part);
            var sameObject = SafeBool(() => entity.IsCreated());
            var reread = ReadTree(part, step, label + " (перечитывание)");
            var stillThere = reread.Any(r => r.ElementPtr == row.ElementPtr);

            line += "excluded=" + suppressed + ", элементов " + tree.Count
                + ", тел " + bodies.Count + ", " + Describe(bodies)
                + ", IsCreated=" + Api5.Raw(sameObject)
                + ", объект признака в дереве " + (stillThere ? "остался" : "отсутствует")
                + ", имена=[" + string.Join(" | ", tree.Select(r => r.Name)) + "]";
        }
        catch (Exception ex)
        {
            line += HResult.Describe(ex);
        }

        step.Observe(line);
        return line;
    }

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    // ══════════════════════════════════════════════════════════════ reading ══

    private List<ElementRow> ReadTree(ksPart part, ProbeStep step, string label)
    {
        var rows = new List<ElementRow>();
        var collection = Collection(part);
        if (collection is null)
        {
            step.Observe(label + ": EntityCollection(110) → null");
            return rows;
        }

        var count = SafeInt(collection.GetCount) ?? 0;
        for (var i = 0; i < count; i++)
        {
            object? element = null;
            try
            {
                element = collection.GetByIndex(i);
            }
            catch (Exception ex)
            {
                step.Observe(label + "[" + i + "]: GetByIndex → " + HResult.Describe(ex));
                continue;
            }

            var row = new ElementRow
            {
                Index = i,
                Raw = element,
                ClrType = Api5.RuntimeName(element),
                ElementPtr = Ptr(element),
                Entity = element as ksEntity,
                Feature = element as ksFeature,
            };
            row.Name = row.Feature?.name ?? row.Entity?.name ?? "<без имени>";
            row.Type = row.Feature?.type ?? row.Entity?.type;
            row.Created = row.Entity is null ? null : SafeBool(() => row.Entity.IsCreated());

            if (row.Entity is not null)
            {
                var feature = SafeObject(() => row.Entity.GetFeature());
                row.FeaturePtr = feature is null ? IntPtr.Zero : Ptr(feature);
                row.FeatureFromEntity = feature as ksFeature;
                if (feature is ksFeature kf)
                {
                    var inner = SafeObject(() => kf.GetObject());
                    row.ObjectClr = Api5.RuntimeName(inner);
                    row.ObjectPtr = inner is null ? IntPtr.Zero : Ptr(inner);
                }
            }
            else if (row.Feature is not null)
            {
                var inner = SafeObject(() => row.Feature.GetObject());
                row.ObjectClr = Api5.RuntimeName(inner);
                row.ObjectPtr = inner is null ? IntPtr.Zero : Ptr(inner);
            }

            rows.Add(row);
        }

        step.Observe(label + ": элементов " + rows.Count + " (GetCount=" + count + ")");
        return rows;
    }

    private static ksEntityCollection? Collection(ksPart part)
    {
        try
        {
            return part.EntityCollection(Api5.OperationElement) as ksEntityCollection;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ElementRow
    {
        public int Index { get; init; }

        public object? Raw { get; init; }

        public string Name { get; set; } = "<нет>";

        public int? Type { get; set; }

        public string ClrType { get; init; } = "<нет>";

        public bool? Created { get; set; }

        public IntPtr ElementPtr { get; init; }

        public IntPtr FeaturePtr { get; set; }

        public IntPtr ObjectPtr { get; set; }

        public string? ObjectClr { get; set; }

        public ksEntity? Entity { get; init; }

        public ksFeature? Feature { get; init; }

        public ksFeature? FeatureFromEntity { get; set; }

        public string Describe() =>
            "[" + Index + "] «" + Name + "» type=" + Api5.Raw(Type) + " (" + ObjectTypeName(Type ?? -1) + ")"
            + " создан=" + Api5.Raw(Created) + " CLR=" + ClrType
            + " ptr=" + Hex(ElementPtr)
            + " GetObject=" + (ObjectClr ?? "<нет>") + "@" + Hex(ObjectPtr);
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private static string Hex(IntPtr value) => value == IntPtr.Zero
        ? "0"
        : "0x" + value.ToInt64().ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Адрес COM-объекта. <c>GetIUnknownForObject</c> увеличивает счётчик ссылок, поэтому ссылка
    /// отпускается сразу: объект держится нашим RCW, а нам нужен только адрес для сравнения.
    /// </summary>
    private static IntPtr Ptr(object? value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.GetIUnknownForObject(value);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }

        return pointer;
    }

    private static string ObjectTypeName(int value) => Api5.ObjectTypeName(value);

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
                if (bodies.GetByIndex(i) is not object element)
                {
                    continue;
                }

                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body)
                {
                    // GetGabarit — метод с шестью out-параметрами, а лямбда не выносит их наружу,
                    // поэтому значения забираются локальной функцией, а не внутри SafeBool.
                    (double[] Min, double[] Max)? box = null;
                    if (Api5.SafeBool(() =>
                        {
                            var ok = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                            if (ok)
                            {
                                box = (new[] { x1, y1, z1 }, new[] { x2, y2, z2 });
                            }

                            return ok;
                        }) == true
                        && box is { } value)
                    {
                        min = value.Min;
                        max = value.Max;
                    }
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
            // Возвращается то, что успело прочитаться: пустой список виден вызывающему.
        }

        return rows;
    }

    private static BodyRow? FindBody(
        List<BodyRow> rows, double x0, double y0, double z0, double x1, double y1, double z1) =>
        rows.FirstOrDefault(r => Near(r, x0, y0, z0, x1, y1, z1));

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 0.01 && Math.Abs(min[1] - y0) < 0.01 && Math.Abs(min[2] - z0) < 0.01
        && Math.Abs(max[0] - x1) < 0.01 && Math.Abs(max[1] - y1) < 0.01 && Math.Abs(max[2] - z1) < 0.01;

    private static double[] Matrix4x4(double[] axes, double[] origin) =>
        new[] { axes[0], axes[1], axes[2], 0d, axes[3], axes[4], axes[5], 0d, axes[6], axes[7], axes[8], 0d,
            origin[0], origin[1], origin[2], 1d };

    private static double[] RotationZ(double angleDeg, double cx, double cy)
    {
        var a = angleDeg * Math.PI / 180d;
        var c = Math.Cos(a);
        var s = Math.Sin(a);
        var axes = new[] { c, s, 0d, -s, c, 0d, 0d, 0d, 1d };
        var origin = new[] { cx - (c * cx - s * cy), cy - (s * cx + c * cy), 0d };
        return Matrix4x4(axes, origin);
    }

    private static double[] Translate(double[] vector) => Matrix4x4(
        new[] { 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d }, vector);

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

    private static bool? SafeBool(Func<bool> call)
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

    private static object? SafeObject(Func<object?> call)
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

    private sealed class BodyRow
    {
        public int Index { get; init; }

        public object? Element { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")");
    }
}
