using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба RR — читается ли ВЕКТОР ПЕРЕНОСА признака изменения положения, и каким членом.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем.</b> Действие <c>read</c> наряда §7 требует, чтобы «параметры и входы читались из
/// модели». Для переноса два параметра — <c>reposition_vector_mm</c> и <c>reposition_axis_point_mm</c> —
/// не читаются ничем: они поимённо названы в <c>solid.unreadable_parameters</c>. Основанием для
/// этого служили шаги RP.9–RP.12 пробы <c>--reposition</c>, и главное из них — RP.11: система
/// координат признака, записанная самой КОМПАС (<c>Position.WriteToFile</c>), содержит 69 байт и
/// ровно матрицу ориентации 3×3, начала в ней нет.
/// </para>
/// <para>
/// <b>Что здесь перемеряется и почему.</b> Из «в этом объекте начала нет» было выведено «величины в
/// модели нет». Это разные утверждения, и второе из первого не следует. Прямая проверка самого
/// файла модели (19.09.2026, распаковка потоков <c>Contents</c>): файл с записанным переносом
/// (7, −11, 13) содержит эту тройку <c>double</c> ЧЕТЫРЕ раза, а контрольный файл с переносом
/// (1, 2, 3) — ни одного раза, зато содержит свою тройку четыре раза. Значит величина в модели ЕСТЬ,
/// и вопрос не «хранится ли она», а «каким членом её отдаёт продукт».
/// </para>
/// <para>
/// Поэтому перечень членов здесь не угадывается, а ЧИТАЕТСЯ из библиотеки типов самого продукта
/// (<c>Bin\kAPI7.tlb</c>, 21.04.2025) — список, придуманный человеком, измеряет его воображение.
/// Живой объект затем опрашивается по каждому объявленному имени, и всё, что отвечает, читается.
/// </para>
/// <para>
/// <b>Правка 19.09.2026 (первый прогон пробы, 67c2ba62af0e43719e82e0720468f366).</b> RR.3/RR.4 дали
/// отрицание, но два места прибора были негодны, и оба чинятся здесь, а не толкуются:
/// </para>
/// <list type="number">
/// <item><b>RR.1 объявлял, что у <c>IBodyReposition</c> нет ни одного члена.</b> Это дефект
/// перебора: он шёл от memId 1 и останавливался после восьми промахов, а члены объявлены около 800.
/// Исправлено в <see cref="TlbScan.MemberNames"/>.</item>
/// <item><b>Объект <c>Position</c> опрашивался только по членам <c>ILocalCoordinateSystem</c>.</b> Он
/// типизирован как размещение (<c>reposition.Position.InitByMatrix3D(...)</c> компилируется), а члены
/// <c>IPlacement3D</c> — <c>GetMatrix3D</c>, <c>GetOrigin</c>, <c>GetEulerAngles</c>, <c>GetPoint3D</c> —
/// не спрашивались НИ РАЗУ, хотя <c>InitByMatrix3D</c> пишет ту самую матрицу 4×4. Отсюда RR.5.</item>
/// </list>
/// <para>
/// <b>Отрицательный контроль обязателен.</b> Член, который всегда отдаёт одну и ту же четвёрку чисел,
/// доказывает не чтение, а константу. Поэтому каждый маршрут здесь проверяется ДВУМЯ признаками:
/// перенос (7, −11, 13) и перенос (1, 2, 3). Совпадение с обоими известными входами — единственное,
/// что делает маршрут годным для <c>read</c>.
/// </para>
/// </remarks>
internal sealed class RepositionReadProbe
{
    private const double Tx0 = 0d, Ty0 = 0d, Tx1 = 20d, Ty1 = 10d, Tz = 5d;

    /// <summary>Записанный перенос основного случая. Габарит после него: (7,−11,13)…(27,−1,18).</summary>
    private static readonly double[] Vector = { 7d, -11d, 13d };

    /// <summary>Отрицательный контроль: другой перенос того же вида. Габарит: (1,2,3)…(21,12,8).</summary>
    private static readonly double[] Control = { 1d, 2d, 3d };

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;
    private string _tlb = string.Empty;
    private string[] _featureNames = Array.Empty<string>();
    private string[] _positionNames = Array.Empty<string>();
    private string[] _pool = Array.Empty<string>();

    public RepositionReadProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "reposition-read.json"),
            Path.Combine(options.ReportDir, "reposition-read.md"));

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
            _tlb = Path.Combine(_options.KompasRoot, "Bin", "kAPI7.tlb");
            DeclaredMembers();
            LiveSurface();
            ReadAnswers();
            PlacementTyped();
            Descent();
            AfterReopen();
            Verdict();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ RR.1 ══

    /// <summary>
    /// Какие члены библиотека типов ПРОДУКТА объявляет у признака изменения положения, у его
    /// системы координат и у размещения. Ни одного имени не придумано здесь: все прочитаны.
    /// </summary>
    private void DeclaredMembers()
    {
        var step = _report.Begin("RR.1", "Какие члены объявлены у признака переноса и его системы координат",
            "Какие имена членов называет сама библиотека типов продукта?");
        step.Data["tlb"] = _tlb;
        if (!File.Exists(_tlb))
        {
            step.Fail("библиотека типов не найдена: " + _tlb);
            return;
        }

        var wanted = new[]
        {
            "IBodyReposition", "ILocalCoordinateSystem", "IPlacement3D", "IBody7",
            "IKompasAPIObject", "IBodyRepositions",
        };

        var all = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var name in wanted)
        {
            var members = TlbScan.MemberNames(_tlb, name);
            all[name] = members.Select(m => m.MemId + ":" + m.Name).ToArray();
            step.Observe(name + " → " + (members.Count == 0
                ? "<ни одного>"
                : string.Join(", ", members.Select(m => m.MemId + ":" + m.Name))));
        }

        step.Data["declared"] = all;

        _featureNames = Names("IBodyReposition")
            .Concat(new[] { "Position", "RepositionCentre", "CopyBoby", "BaseObject", "ModifyObjects" })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _positionNames = Names("ILocalCoordinateSystem")
            .Concat(Names("IPlacement3D"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _pool = TlbScan.AllMemberNames(_tlb).ToArray();

        step.Data["feature_names_probed"] = _featureNames;
        step.Data["position_names_probed"] = _positionNames;
        step.Data["pool_size"] = _pool.Length;

        var reposition = all["IBodyReposition"];
        var coordinate = all["ILocalCoordinateSystem"];
        step.Observe("всего объявлено: IBodyReposition " + reposition.Length
            + ", ILocalCoordinateSystem " + coordinate.Length
            + ", IPlacement3D " + all["IPlacement3D"].Length
            + "; пул имён всей библиотеки " + _pool.Length);

        if (reposition.Length == 0)
        {
            step.Fail("у IBodyReposition не объявлено ни одного члена — перечень не прочитан");
        }
        else if (_pool.Length == 0)
        {
            step.Unknown("пул имён библиотеки пуст — опрос живых объектов будет неполным");
        }
        else
        {
            step.Pass("перечень членов прочитан из библиотеки типов продукта, а не придуман");
        }
    }

    private string[] Names(string interfaceName) =>
        TlbScan.MemberNames(_tlb, interfaceName).Select(m => m.Name).ToArray();

    // ══════════════════════════════════════════════════════════════ RR.2 ══

    /// <summary>
    /// Что из объявленного отвечает ЖИВОЙ объект. Объявленный член и отвечающий член — разные
    /// утверждения: первый говорит о библиотеке, второй о работающем приложении.
    /// </summary>
    private void LiveSurface()
    {
        var step = _report.Begin("RR.2", "На какие объявленные имена отвечает ЖИВОЙ признак переноса",
            "Совпадает ли объявленный перечень членов с тем, на что объект отвечает?");
        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "RR");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Vector, step);
            if (feature is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var after = BodyRows(part);
            step.Observe("после переноса: " + Describe(after)
                + " (ожидание габарита (7,−11,13)…(27,−1,18))");
            step.Data["after_transfer"] = Describe(after);

            var featureSurface = TlbScan.LiveMembers(feature, _featureNames);
            foreach (var pair in featureSurface)
            {
                step.Observe("признак." + pair.Key + ": " + pair.Value);
            }

            step.Data["feature_surface"] = featureSurface;

            // Третья сторона вопроса: что объект объявляет САМ. Обёртка — снимок, библиотека типов —
            // описание продукта, а это ответ самого работающего объекта.
            var featureOwn = Late.MemberNames(feature);
            step.Data["feature_own_names"] = featureOwn.Select(m => m.MemId + ":" + m.Name).ToArray();
            step.Observe("признак объявляет СВОИХ имён: " + featureOwn.Count
                + (featureOwn.Count == 0 ? string.Empty
                    : " — " + string.Join(", ", featureOwn.Select(m => m.MemId + ":" + m.Name))));

            var position = SafeObject(() => feature.Position);
            step.Data["position_clr"] = Api5.RuntimeName(position);
            step.Data["position_is_placement"] = position is IPlacement3D;
            step.Data["position_is_local_cs"] = position is ILocalCoordinateSystem;

            if (position is not null)
            {
                var positionOwn = Late.MemberNames(position);
                step.Data["position_own_names"] = positionOwn.Select(m => m.MemId + ":" + m.Name).ToArray();
                step.Observe("Position объявляет СВОИХ имён: " + positionOwn.Count
                    + (positionOwn.Count == 0 ? string.Empty
                        : " — " + string.Join(", ", positionOwn.Select(m => m.MemId + ":" + m.Name))));

                var positionSurface = TlbScan.LiveMembers(position, _positionNames);
                foreach (var pair in positionSurface)
                {
                    step.Observe("Position." + pair.Key + ": " + pair.Value);
                }

                step.Data["position_surface"] = positionSurface;
                var answering = positionSurface
                    .Where(p => p.Value.Contains("dispid=", StringComparison.Ordinal))
                    .Select(p => p.Key)
                    .ToArray();
                step.Data["position_answering"] = answering;
                step.Observe("Position отвечает на " + answering.Length + " имён из " + _positionNames.Length);
            }

            var featureAnswering = featureSurface
                .Where(p => p.Value.Contains("dispid=", StringComparison.Ordinal))
                .Select(p => p.Key)
                .ToArray();
            step.Data["feature_answering"] = featureAnswering;
            step.Observe("признак отвечает на " + featureAnswering.Length + " имён из " + _featureNames.Length);

            step.Pass("живая поверхность признака измерена: отвечающих имён " + featureAnswering.Length);
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

    // ══════════════════════════════════════════════════════════════ RR.3 ══

    /// <summary>
    /// Читается ли записанный вектор хоть одним из ОТВЕЧАЮЩИХ членов. Ожидание (7, −11, 13)
    /// объявлено до чтения; контроль — тот же признак с вектором (1, 2, 3).
    /// </summary>
    private void ReadAnswers()
    {
        var step = _report.Begin("RR.3", "Отдаёт ли хоть один отвечающий член вектор (7, −11, 13)",
            "Найдётся ли среди отвечающих членов тот, который возвращает записанные числа?");
        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "RR");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Vector, step);
            if (feature is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var reads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in _featureNames)
            {
                reads[name] = Describe(SafeGet(feature, name));
            }

            step.Data["reads"] = reads;
            foreach (var pair in reads)
            {
                step.Observe("признак." + pair.Key + " → " + pair.Value);
            }

            var position = SafeObject(() => feature.Position);
            if (position is not null)
            {
                var coordinateReads = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var name in _positionNames)
                {
                    coordinateReads[name] = Describe(SafeGet(position, name));
                }

                step.Data["coordinate_reads"] = coordinateReads;
                foreach (var pair in coordinateReads)
                {
                    step.Observe("Position." + pair.Key + " → " + pair.Value);
                }
            }

            var hits = Hits(step.Data, Vector);
            step.Data["vector_found_in"] = hits;

            if (hits.Length > 0)
            {
                step.Pass("вектор переноса ЧИТАЕТСЯ членом: " + string.Join(", ", hits));
            }
            else
            {
                step.Fail("ни один отвечающий член не отдал записанный вектор (7, −11, 13) "
                    + "при позднем связывании; проверяются типизированный маршрут и спуск");
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

    // ══════════════════════════════════════════════════════════════ RR.5 ══

    /// <summary>
    /// Читается ли записанный вектор САМИМ объектом системы координат: сначала по тем именам, которые
    /// объект объявляет о себе, затем типизированно — по тому интерфейсу обёртки, который он
    /// действительно реализует.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сигнатуры не угадываются: они ЧИТАЮТСЯ отражением по интерфейсу обёртки, и в отчёт попадает
    /// сама сигнатура. Аргументы <c>ref</c> заполняются значениями по умолчанию — <c>Invoke</c> сам
    /// выделяет под них место и возвращает записанное обратно.
    /// </para>
    /// <para>
    /// <b>Почему не <c>IPlacement3D</c>.</b> Первый прогон этой редакции пробовал именно его и
    /// получил <c>position_is_placement = false</c>: объект <c>Position</c> этого интерфейса не
    /// реализует, хотя библиотека типов объявляет у <c>IPlacement3D</c> одиннадцать имён. Спрашивать
    /// надо тот интерфейс, который объект подтверждает, — <c>ILocalCoordinateSystem</c>.
    /// </para>
    /// </remarks>
    private void PlacementTyped()
    {
        var step = _report.Begin("RR.5", "Отдаёт ли Position записанный вектор (опрос объекта)",
            "Возвращает ли хоть один член, объявленный самим объектом, записанный вектор, "
            + "и подтверждает ли это контроль?");

        var cases = new[] { ("основной (7,−11,13)", Vector), ("контроль (1,2,3)", Control) };
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new List<string>();

        foreach (var (label, vector) in cases)
        {
            var doc = NewPart(out var part);
            try
            {
                var box = ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "RR");
                if (box is null)
                {
                    step.Observe(label + ": брусок не построен");
                    continue;
                }

                var feature = CreateReposition(doc, part, box, vector, step);
                if (feature is null)
                {
                    step.Observe(label + ": признак не создан");
                    continue;
                }

                var position = SafeObject(() => feature.Position);
                if (position is null)
                {
                    step.Observe(label + ": Position не получен");
                    continue;
                }

                // (а) Собственные имена объекта — его собственное утверждение о себе. Оговорка:
                // IDispatch::GetTypeInfo(0) отдаёт УМОЛЧАТЕЛЬНЫЙ интерфейс класса, поэтому у Position
                // перечень совпал с перечнем признака; члены, специфичные для системы координат,
                // видны только по подтверждённому интерфейсу — это ветка (б).
                var own = Late.MemberNames(position);
                step.Data["own_names_" + label] = own.Select(m => m.MemId + ":" + m.Name).ToArray();
                step.Observe(label + ": объект объявляет " + own.Count + " своих имён (умолчательный "
                    + "интерфейс класса)");
                foreach (var (memId, name) in own)
                {
                    var key = label + " · " + memId + ":" + name;
                    if (!IsSafeRead(name))
                    {
                        rows[key] = "объявлен, прибором НЕ вызывается: может менять состояние";
                        continue;
                    }

                    var value = Describe(SafeGet(position, name));
                    rows[key] = "собственный член → " + value;
                    if (ContainsTriple(value, vector))
                    {
                        found.Add(memId + ":" + name + " [" + label + "]");
                    }
                }

                // (б) Типизированный маршрут по подтверждённому интерфейсу обёртки.
                var contract = position is ILocalCoordinateSystem
                    ? typeof(ILocalCoordinateSystem)
                    : position is IPlacement3D ? typeof(IPlacement3D) : null;
                step.Data["typed_contract_" + label] = contract?.Name ?? "<ни один интерфейс обёртки не подтверждён>";

                if (contract is not null)
                {
                    foreach (var method in contract.GetMethods().Where(m => m.GetParameters().Length <= 2))
                    {
                        var name = method.Name.StartsWith("get_", StringComparison.Ordinal)
                            ? method.Name[4..]
                            : method.Name;
                        if (method.Name.StartsWith("set_", StringComparison.Ordinal)
                            || method.Name.StartsWith("put_", StringComparison.Ordinal)
                            || !IsSafeRead(name))
                        {
                            rows[label + " · " + method.Name] =
                                "объявлен, прибором НЕ вызывается: может менять состояние";
                            continue;
                        }

                        var (signature, result, written) = InvokeDeclared(position, contract, method.Name);
                        var text = result + (written.Length == 0
                            ? string.Empty
                            : " | " + string.Join(" | ", written));
                        rows[label + " · " + method.Name] = signature + " → " + text;
                        if (ContainsTriple(text, vector))
                        {
                            found.Add(method.Name + " [" + label + "]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                step.Observe(label + ": исключение " + HResult.Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        step.Data["placement_reads"] = rows;
        foreach (var pair in rows)
        {
            step.Observe(pair.Key + " → " + pair.Value);
        }

        step.Data["triple_found_in"] = found.ToArray();

        // Маршрут годен, только если он различил ОБА известных входа. Член, отдающий одну и ту же
        // четвёрку на обоих, — константа, а не чтение.
        var main = found.Where(f => f.Contains("основной", StringComparison.Ordinal)).Select(Name).ToArray();
        var control = found.Where(f => f.Contains("контроль", StringComparison.Ordinal)).Select(Name).ToArray();
        var both = main.Intersect(control, StringComparer.Ordinal).ToArray();
        step.Data["routes_distinguishing_both_inputs"] = both;

        if (both.Length > 0)
        {
            step.Pass("член различает ДВА известных входа: " + string.Join(", ", both));
        }
        else if (found.Count > 0)
        {
            step.Fail("вектор найден, но один и тот же член не подтверждён на обоих входах — "
                + "это признак константы, а не чтения: " + string.Join(", ", found));
        }
        else
        {
            step.Fail("ни один член, объявленный самим объектом, и ни один метод подтверждённого "
                + "интерфейса не отдал записанный вектор ни на основном входе, ни на контроле");
        }
    }

    // ══════════════════════════════════════════════════════════════ RR.6 ══

    /// <summary>
    /// Спуск в объектные члены признака: <c>RepositionCentre</c> и всё прочее, что вернуло живой
    /// объект. Объект, на который признак отвечает, но который не раскрыт, — это незаданный вопрос,
    /// а не отрицательный ответ.
    /// </summary>
    private void Descent()
    {
        var step = _report.Begin("RR.6", "Спуск в объектные члены признака: не спрятан ли вектор глубже",
            "Отвечает ли на что-нибудь объект, возвращённый RepositionCentre и другими членами?");
        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "RR");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Vector, step);
            if (feature is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var objects = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in _featureNames)
            {
                var value = SafeGet(feature, name);
                if (value is null || value is string || value is bool || value is double || value is int)
                {
                    continue;
                }

                objects[name] = value;
            }

            // Объектные члены САМОЙ системы координат: get_LocalCSParameters отвечает живым объектом,
            // и объект, на который признак отвечает, но который не раскрыт, — это незаданный вопрос.
            if (SafeObject(() => feature.Position) is { } position && position is ILocalCoordinateSystem)
            {
                var contract = typeof(ILocalCoordinateSystem);
                foreach (var method in contract.GetMethods().Where(m => m.GetParameters().Length <= 2))
                {
                    var name = method.Name.StartsWith("get_", StringComparison.Ordinal)
                        ? method.Name[4..]
                        : method.Name;
                    if (method.Name.StartsWith("set_", StringComparison.Ordinal)
                        || method.Name.StartsWith("put_", StringComparison.Ordinal)
                        || !IsSafeRead(name))
                    {
                        continue;
                    }

                    var (_, result, _) = InvokeDeclared(position, contract, method.Name);
                    var value = SafeGet(position, name);
                    if (value is null || value is string || value is bool || value is double || value is int)
                    {
                        continue;
                    }

                    objects["Position." + name] = value;
                    step.Observe("Position." + name + " → " + Api5.RuntimeName(value)
                        + " (типизированно: " + result + ")");
                }
            }

            step.Data["object_members"] = objects.Keys.ToArray();
            var found = new List<string>();

            foreach (var (name, value) in objects)
            {
                // Спрашиваем объект о нём самом; пул имён библиотеки — только запасной ход, потому
                // что имя, объявленное библиотекой у ДРУГОГО интерфейса, объект всё равно не примет.
                var own = Late.MemberNames(value);
                var surface = own.Count > 0
                    ? own.ToDictionary(m => m.MemId + ":" + m.Name, _ => "собственный член", StringComparer.Ordinal)
                    : TlbScan.LiveMembers(value, _pool)
                        .Where(p => p.Value.Contains("dispid=", StringComparison.Ordinal))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

                step.Observe(name + " (" + Api5.RuntimeName(value) + "): объявляет своих имён "
                    + own.Count + ", опрошено " + surface.Count);

                if (surface.Count == 0)
                {
                    continue;
                }

                var reads = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in surface.Keys)
                {
                    var plain = member.Contains(':') ? member[(member.IndexOf(':') + 1)..] : member;
                    reads[member] = IsSafeRead(plain)
                        ? Describe(SafeGet(value, plain))
                        : "объявлен, прибором НЕ вызывается: может менять состояние";
                }

                step.Data["descent_" + name] = reads;
                foreach (var pair in reads)
                {
                    step.Observe(name + "." + pair.Key + " → " + pair.Value);
                }

                foreach (var pair in reads)
                {
                    if (ContainsTriple(pair.Value, Vector))
                    {
                        found.Add(name + "." + pair.Key);
                    }
                }
            }

            step.Data["triple_found_in"] = found.ToArray();
            if (found.Count > 0)
            {
                step.Pass("вектор найден при спуске: " + string.Join(", ", found));
            }
            else
            {
                step.Fail("при спуске в объектные члены вектор (7, −11, 13) не найден ни в одном из "
                    + objects.Count + " раскрытых объектов");
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

    // ══════════════════════════════════════════════════════════════ RR.4 ══

    /// <summary>
    /// То же на ПЕРЕОТКРЫТОМ документе: действие <c>read</c> в продукте работает и с ним, поэтому
    /// вопрос перемеряется на состоянии, которое продукт действительно видит.
    /// </summary>
    private void AfterReopen()
    {
        var step = _report.Begin("RR.4", "Читается ли вектор с ПЕРЕОТКРЫТОГО документа",
            "Отдаёт ли признак переноса записанные числа после save → close → open?");
        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, Tx0, Tx1, Ty0, Ty1, Tz, step, "RR");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Vector, step);
            if (feature is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var path = Path.Combine(_options.WorkDir, "reposition-read.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs: " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            var reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            var container = Container(reopened);
            if (container?.BodyRepositions is not { } collection || collection.Count == 0
                || collection[0] is not IBodyReposition reopenedFeature)
            {
                step.Fail("признак переноса после переоткрытия не найден");
                TryClose(reopened);
                return;
            }

            step.Observe("признаков BodyRepositions после переоткрытия: " + Api5.Raw(collection.Count));

            var reads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in _featureNames)
            {
                reads[name] = Describe(SafeGet(reopenedFeature, name));
            }

            step.Data["reads_reopened"] = reads;
            foreach (var pair in reads)
            {
                step.Observe("признак." + pair.Key + " → " + pair.Value);
            }

            var position = SafeObject(() => reopenedFeature.Position);
            if (position is not null)
            {
                var coordinateReads = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var name in _positionNames)
                {
                    coordinateReads[name] = Describe(SafeGet(position, name));
                }

                step.Data["coordinate_reads_reopened"] = coordinateReads;
                foreach (var pair in coordinateReads)
                {
                    step.Observe("Position." + pair.Key + " → " + pair.Value);
                }
            }

            var typed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (position is not null)
            {
                foreach (var (memId, member) in Late.MemberNames(position))
                {
                    typed[memId + ":" + member] = "собственный член → " + Describe(SafeGet(position, member));
                }

                var contract = position is ILocalCoordinateSystem
                    ? typeof(ILocalCoordinateSystem)
                    : position is IPlacement3D ? typeof(IPlacement3D) : null;
                if (contract is null)
                {
                    typed["<типизированный маршрут>"] =
                        "Position переоткрытого признака не подтверждает ни один интерфейс обёртки";
                }
                else
                {
                    foreach (var method in contract.GetMethods().Where(m => m.GetParameters().Length <= 2))
                    {
                        var name = method.Name.StartsWith("get_", StringComparison.Ordinal)
                            ? method.Name[4..]
                            : method.Name;
                        if (method.Name.StartsWith("set_", StringComparison.Ordinal)
                            || method.Name.StartsWith("put_", StringComparison.Ordinal)
                            || !IsSafeRead(name))
                        {
                            typed[method.Name] = "объявлен, прибором НЕ вызывается: может менять состояние";
                            continue;
                        }

                        var (signature, result, written) = InvokeDeclared(position, contract, method.Name);
                        typed[method.Name] = signature + " → " + result
                            + (written.Length == 0 ? string.Empty : " | " + string.Join(" | ", written));
                    }
                }
            }

            step.Data["placement_reads_reopened"] = typed;
            foreach (var pair in typed)
            {
                step.Observe(pair.Key + " → " + pair.Value);
            }

            var found = reads
                .Concat(step.Data.TryGetValue("coordinate_reads_reopened", out var extra)
                        && extra is Dictionary<string, string> coordinate ? coordinate
                        : new Dictionary<string, string>())
                .Concat(typed)
                .Where(p => ContainsTriple(p.Value, Vector))
                .Select(p => p.Key)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            step.Data["vector_found_in"] = found;

            if (found.Length > 0)
            {
                step.Pass("с переоткрытого документа вектор читается членом: " + string.Join(", ", found));
            }
            else
            {
                step.Fail("с переоткрытого документа вектор (7, −11, 13) не отдал ни один член; "
                    + "признак найден и отвечает, а маршрут чтения не найден");
            }

            TryClose(reopened);
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════════════════════ RR.7 ══

    /// <summary>
    /// Итог пробы ОДНИМ утверждением: назван маршрут чтения или названо, что его нет. Отчёт без
    /// итога читается как «неизвестно», а вопрос наряда §6 требует ответа «да» или «нет».
    /// </summary>
    private void Verdict()
    {
        var step = _report.Begin("RR.7", "Итог: найден ли маршрут чтения вектора переноса",
            "Есть ли член, отдающий записанный перенос, подтверждённый двумя известными входами?");

        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var previous in _report.Steps)
        {
            foreach (var key in new[] { "vector_found_in", "triple_found_in", "routes_distinguishing_both_inputs" })
            {
                if (!previous.Data.TryGetValue(key, out var value) || value is not IEnumerable<string> list)
                {
                    continue;
                }

                foreach (var item in list)
                {
                    routes.Add(previous.Id + " " + key + ": " + item);
                }
            }
        }

        step.Data["routes"] = routes.ToArray();
        if (routes.Count == 0)
        {
            step.Fail("маршрут чтения вектора переноса НЕ найден: ни позднее связывание по объявленным "
                + "именам, ни типизированное чтение размещения, ни спуск в объектные члены не отдали "
                + "записанную тройку. Действие read для reposition_vector_mm остаётся открытым");
            return;
        }

        step.Pass("маршрут чтения назван: " + string.Join(" · ", routes));
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    /// <summary>
    /// Вызов объявленного члена по ИНТЕРФЕЙСУ (не поздним связыванием): сигнатура читается
    /// отражением и попадает в отчёт, аргументы <c>ref</c> заполняются значениями по умолчанию.
    /// </summary>
    private static (string Signature, string Result, string[] Written) InvokeDeclared(
        object target, Type contract, string name)
    {
        var method = contract.GetMethod(name);
        if (method is null)
        {
            return ("<" + name + " не объявлен в обёртке " + contract.Name + ">", "—", Array.Empty<string>());
        }

        var parameters = method.GetParameters();
        var signature = name + "(" + string.Join(", ", parameters.Select(p =>
            (p.ParameterType.IsByRef ? "ref " : string.Empty)
            + (p.ParameterType.GetElementType() ?? p.ParameterType).Name + " " + p.Name))
            + ") : " + method.ReturnType.Name;

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type.IsByRef)
            {
                type = type.GetElementType()!;
            }

            args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        object? returned;
        try
        {
            returned = method.Invoke(target, args);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return (signature, HResult.Describe(inner), Array.Empty<string>());
        }

        var written = new List<string>();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType.IsByRef)
            {
                written.Add(parameters[i].Name + "=" + Describe(args[i]));
            }
        }

        return (signature, Describe(returned), written.ToArray());
    }

    /// <summary>
    /// Можно ли звать этот член, не рискуя изменить измеряемый объект. Прибор обязан мерить, а не
    /// править: <c>SetDisplacementByAxis</c>, <c>SetStartingOrientation</c> и <c>InitByMatrix3D</c>
    /// объявлены у того же объекта, что и чтение, и вызов «на всякий случай» переписал бы состояние
    /// до измерения. Такие члены называются в отчёте по имени, но не вызываются.
    /// </summary>
    private static bool IsSafeRead(string name) =>
        !name.StartsWith("Set", StringComparison.Ordinal)
        && !name.StartsWith("Init", StringComparison.Ordinal)
        && !name.StartsWith("put_", StringComparison.Ordinal)
        && !name.StartsWith("set_", StringComparison.Ordinal)
        && !string.Equals(name, "Rotate", StringComparison.Ordinal)
        && !string.Equals(name, "WriteToFile", StringComparison.Ordinal)
        && !string.Equals(name, "ReadFromFile", StringComparison.Ordinal);

    /// <summary>
    /// Ищет ЛИБО записанную тройку подряд, либо имя члена, в котором она встретилась. Сравниваются
    /// числа, а не подстроки: «17» содержит «7», и поиск по подстроке объявил бы находкой мусор.
    /// </summary>
    private static bool ContainsTriple(string text, double[] vector)
    {
        var numbers = Numbers(text);
        for (var i = 0; i + 2 < numbers.Count; i++)
        {
            if (Math.Abs(numbers[i] - vector[0]) < 1e-6
                && Math.Abs(numbers[i + 1] - vector[1]) < 1e-6
                && Math.Abs(numbers[i + 2] - vector[2]) < 1e-6)
            {
                return true;
            }
        }

        return false;
    }

    private static List<double> Numbers(string text)
    {
        var found = new List<double>();
        foreach (Match match in Regex.Matches(text, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"))
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                found.Add(value);
            }
        }

        return found;
    }

    /// <summary>Ключи шагов, в которых встретилась тройка, — по уже собранным данным отчёта.</summary>
    private static string[] Hits(Dictionary<string, object?> data, double[] vector)
    {
        var hits = new List<string>();
        foreach (var (key, value) in data)
        {
            if (value is Dictionary<string, string> map)
            {
                hits.AddRange(map.Where(p => ContainsTriple(p.Value, vector)).Select(p => key + ":" + p.Key));
            }
        }

        return hits.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Имя члена из подписи вида «GetMatrix3D [основной (7,−11,13)]».</summary>
    private static string Name(string label) => label.Split(' ')[0];

    private static string Describe(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is Array array)
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                parts.Add(Describe(item));
            }

            return "[" + string.Join(", ", parts) + "]";
        }

        if (value is double number)
        {
            return Api5.Num(number);
        }

        return Api5.Raw(value);
    }

    /// <summary>Чтение члена поздним связыванием: отказ — это отсутствие ответа, а не исключение шага.</summary>
    private static object? SafeGet(object target, string name)
    {
        try
        {
            return Late.Get(target, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T? SafeObject<T>(Func<T> call) where T : class
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

    private void Launch()
    {
        var step = _report.Begin("RR.Z0", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
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
        var step = _report.Begin("RR.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit: " + HResult.Describe(ex));
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 15000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var alive = NewProcesses();
        step.Data["orphans"] = alive;
        step.Observe("своих процессов после Quit: " + alive.Count + ", ожидание " + waited + " мс");
        if (alive.Count == 0)
        {
            step.Pass("сеанс освобождён");
        }
        else
        {
            step.Unknown("процесс остался");
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
            // Не предмет этого шага.
        }
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

    private IBodyReposition? CreateReposition(
        ksDocument3D doc, ksPart part, BodyRow target, double[] vector, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not IBodyReposition reposition)
            {
                step.Observe("BodyRepositions.Add() недоступен");
                return null;
            }

            if (_app.TransferInterface(target.Element!, 2, 0) is not IKompasAPIObject transferred)
            {
                step.Observe("тело не переносится в API7");
                return null;
            }

            reposition.RepositionBody = transferred;
            reposition.Position.InitByMatrix3D(Matrix4x4(vector));
            var updated = reposition.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("вызов: вектор (" + string.Join(", ", vector.Select(v => Api5.Num(v)))
                + "), Update()=" + updated);
            return updated ? reposition : null;
        }
        catch (Exception ex)
        {
            step.Observe("создание признака: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>Матрица 4×4 с единичной ориентацией и заданным переносом.</summary>
    private static double[] Matrix4x4(double[] vector) => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        vector[0], vector[1], vector[2], 1d,
    };

    private static BodyRow? ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() = false");
            return null;
        }

        return BodyRows(part).FirstOrDefault(r => Near(r, u0, v0, 0d, u1, v1, thickness));
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
                if (bodies.GetByIndex(i) is not { } element)
                {
                    continue;
                }

                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body)
                {
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

                rows.Add(new BodyRow(i, element, Api5.BodyVolume(element), min, max));
            }
        }
        catch (Exception)
        {
            // Возвращается прочитанное.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 0.01 && Math.Abs(min[1] - y0) < 0.01 && Math.Abs(min[2] - z0) < 0.01
        && Math.Abs(max[0] - x1) < 0.01 && Math.Abs(max[1] - y1) < 0.01 && Math.Abs(max[2] - z1) < 0.01;

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    private sealed record BodyRow(int Index, object? Element, double? Volume, double[]? Min, double[]? Max)
    {
        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")");
    }
}
