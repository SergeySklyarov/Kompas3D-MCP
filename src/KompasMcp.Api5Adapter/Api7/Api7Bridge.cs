using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>
/// Мост API5→API7 внутри одного сеанса (ADR-004 §1, §5).
/// </summary>
/// <remarks>
/// <para>
/// Класс существует потому, что часть режимов практического выпуска физически недоступна из API5,
/// и это измерено, а не предполагано: у <c>ksChamferDefinition</c> нет угла, он есть только у
/// <c>IChamfer.Angle</c> (проба F.10). Второй экземпляр КОМПАСа для этого не запускается —
/// <c>ksGetApplication7()</c> на том же API5-объекте отдаёт приложение с тем же PID
/// (api7-findings §3), поэтому мост живёт рядом с API5-сессией и пользуется её STA-очередью.
/// </para>
/// <para>
/// <b>Типизированные интерфейсы, не IDispatch.</b> Каждый вызов здесь — вызов через vtable
/// сгенерированной вендором обёртки. Аварийный путь позднего связывания остаётся в
/// <c>tools/KompasMcp.Api7Probe</c> (изолированный процесс) и в продукт не переносится: проба E
/// измерила, что запись через <c>IDispatch</c> в общем процессе роняла его кодом 0xC0000409, а
/// молча не сохранившее значение свойство читалось бы как «успех».
/// </para>
/// <para>
/// <b>Кэш перенесённых документов.</b> Ключом служит не только идентификатор документа, но и его
/// ревизия: после любой мутации кэш по этому документу негоден, иначе из кэша достали бы
/// представление уже перестроенной модели.
/// </para>
/// </remarks>
internal sealed class Api7Bridge
{
    private readonly KompasObject _application5;
    private readonly Dictionary<string, (long Revision, IModelContainer Container)> _containers = new(StringComparer.Ordinal);
    private IApplication? _application7;
    private string? _bridgeFailure;

    public Api7Bridge(KompasObject application5) => _application5 = application5;

    /// <summary>Почему мост не построился (для честного CAPABILITY_UNAVAILABLE вместо null-тишины).</summary>
    public string? BridgeFailure => _bridgeFailure;

    /// <summary>
    /// API7-представление того же приложения. null — мост недоступен, причина в
    /// <see cref="BridgeFailure"/>; вызов не бросает, потому что отсутствие API7 не должно ломать
    /// маршруты API5, работающие без него.
    /// </summary>
    public IApplication? Application()
    {
        if (_application7 is not null)
        {
            return _application7;
        }

        try
        {
            // Типизированный QI на IApplication: то, что interop-обёртка объявляет этот интерфейс,
            // не означает, что объект ему отвечает, поэтому ответ проверяется приведением.
            _application7 = _application5.ksGetApplication7() as IApplication;
            _bridgeFailure = _application7 is null ? "ksGetApplication7() вернул null" : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException)
        {
            _application7 = null;
            _bridgeFailure = Describe(ex);
        }

        return _application7;
    }

    /// <summary>Контейнер модельных объектов API7 для документа; null — представления нет.</summary>
    public IModelContainer? ContainerFor(ksDocument3D document, string documentId, long revision)
    {
        if (Application() is null)
        {
            return null;
        }

        if (_containers.TryGetValue(documentId, out var cached) && cached.Revision == revision)
        {
            return cached.Container;
        }

        _containers.Remove(documentId);
        try
        {
            var transferred = _application5.TransferInterface(
                document, Api7DualTransfer, 0) as IKompasDocument3D;
            if (transferred?.TopPart is not IModelContainer container)
            {
                _bridgeFailure = "TransferInterface(документ → API7) не дал IModelContainer через TopPart";
                return null;
            }

            _containers[documentId] = (revision, container);
            return container;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _bridgeFailure = Describe(ex);
            return null;
        }
    }

    /// <summary>
    /// Режим переноса объектов API5 → API7. Берётся из константы вендорского перечисления, а не
    /// числом: проба E показала, что неверный режим переноса даёт не ошибку, а объект, который
    /// «читается», но представляет другую сущность.
    /// </summary>
    private const int Api7DualTransfer = (int)ksAPITypeEnum.ksAPI7Dual;

    public object? TransferTo7(object source)
    {
        try
        {
            return _application5.TransferInterface(source, Api7DualTransfer, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _bridgeFailure = Describe(ex);
            return null;
        }
    }

    /// <summary>
    /// Массив перенесённых объектов для <c>BaseObjects</c>. Массив, а не коллекция: документация
    /// <c>IChamfer.BaseObjects</c> требует SAFEARRAY указателей IDispatch, и проба F.9 подтвердила
    /// это числом — фаска по <c>object[]</c> из четырёх рёбер сняла ровно 20·d₁·d₂.
    /// </summary>
    public object[]? TransferAllTo7(IReadOnlyList<object> sources)
    {
        var transferred = new object[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            if (TransferTo7(sources[i]) is not { } item)
            {
                return null;
            }

            transferred[i] = item;
        }

        return transferred;
    }

    /// <summary>
    /// Перестроение после записи в признак API7. <c>RebuildModel</c> принадлежит
    /// <see cref="IPart7"/> (измерено пробой E — не <c>IKompasDocument3D</c>), а API5-перестроение
    /// остаётся обязательным: без него объём, который читает API5, отстаёт.
    /// </summary>
    public static void Rebuild(IModelContainer container, ksDocument3D document5)
    {
        if (container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        document5.RebuildDocument();
    }

    /// <summary>Сброс кэша: документ закрыли, перестроили извне или сменилась ревизия.</summary>
    public void Invalidate(string documentId) => _containers.Remove(documentId);

    public void ReleaseAll()
    {
        foreach (var (_, container) in _containers.Values)
        {
            ComApartment.Release(container);
        }

        _containers.Clear();
        ComApartment.Release(_application7);
        _application7 = null;
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Параметры фаски, перечитанные из модели. Отдельная запись потому, что часть полей берётся из
/// API5 (<c>GetChamferParam</c>), а часть — только из API7 (<c>Angle</c>, <c>BuildingType</c>), и
/// «чего сервер не прочитал» обязано быть различимо от «прочитал ноль».
/// </summary>
public sealed record ChamferReadDto(
    bool? Transfer,
    double? Distance1Mm,
    double? Distance2Mm,
    double? AngleDeg,
    string? BuildingType,
    bool? Direction,
    bool? Tangent,
    int? BaseObjectCount,
    IReadOnlyList<string> ReadRoutes);

/// <summary>
/// Типизированные операции API7 над фаской (SM-11). Всё, что здесь происходит, измерено пробой F
/// на v24 и повторяется вызов в вызов; ни один шаг не объявлен «поддержанным» по наличию типа.
/// </summary>
internal static class Api7Chamfer
{
    /// <summary>
    /// Создать фаску «расстояние + угол». Возвращает пару (создано, причина), а не исключение:
    /// вызывающий обязан отличить отказ КОМПАСа от падения адаптера.
    /// </summary>
    public static (bool Created, string? Failure) TryCreateDistanceAngle(
        IModelContainer container,
        object[] baseObjects,
        double distanceMm,
        double angleDeg,
        bool direction,
        string? name)
    {
        try
        {
            var chamfer = container.Chamfers.Add();
            if (name is not null)
            {
                chamfer.Name = name;
            }

            // ksChamferSideAngle = «по стороне и углу»: Distance1 задаёт катет на опорной стороне,
            // Angle — угол фаски в градусах (измерено F.10: 30 при d=2 дало 20·d·(d·tg 30°) мм³).
            chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferSideAngle;
            chamfer.BaseObjects = baseObjects;
            chamfer.Distance1 = distanceMm;
            chamfer.Angle = angleDeg;
            chamfer.Direction = direction;
            var updated = chamfer.Update();
            return (updated, updated ? null : "IChamfer.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Число фасок в коллекции API7. null — не прочитано (не «ноль», и это разные ответы).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Chamfers.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Записать параметры в СУЩЕСТВУЮЩИЙ признак через <c>IChamfer</c> — маршрут, которым
    /// правится угол (в API5 члена «угол» нет вовсе, поэтому нужен он).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пишутся только те поля, что заданы: незаданное поле остаётся тем, что лежит в модели, и
    /// это принципиально. При способе «расстояние и угол» второй катет ПРОИЗВОДЕН от угла
    /// (<c>d₂ = d₁·tg α</c>); записать в него «прежнее число» значило бы закрепить устаревшую
    /// производную и потерять связь с углом.
    /// </para>
    /// <para>
    /// Порядок «запись → Update() → RebuildModel()» — часть контракта, а не стиль: без
    /// <c>Update()</c> сеттеры возвращают успех, но модель остаётся прежней (то же измерено на
    /// <c>IExtrusion.Sketch</c> пробой E и на создании фаски F.10).
    /// </para>
    /// <para>
    /// Возвращается пара (записано, причина), а не исключение: вызывающий обязан отличить отказ
    /// КОМПАСа от падения адаптера, и «не смогли» не должно выглядеть как «записали».
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure) TryWrite(
        IModelContainer container,
        int index,
        double? distance1Mm,
        double? distance2Mm,
        double? angleDeg,
        bool? direction)
    {
        try
        {
            if (container.Chamfers[index] is not IChamfer chamfer)
            {
                return (false, $"элемент коллекции Chamfers[{index}] не отдаёт IChamfer");
            }

            if (distance1Mm is double d1)
            {
                chamfer.Distance1 = d1;
            }

            if (angleDeg is double angle)
            {
                chamfer.Angle = angle;
            }

            if (direction is bool side)
            {
                chamfer.Direction = side;
            }

            // Второй катет пишется ПОСЛЕ угла: при способе «расстояние и угол» он производный, и
            // если клиент задал и угол, и катет явно, честнее применить явно запрошенное значение,
            // чем оставить производную от нового угла. Это осознанный порядок, а не случайный.
            if (distance2Mm is double d2)
            {
                chamfer.Distance2 = d2;
            }

            if (!chamfer.Update())
            {
                return (false, "IChamfer.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Индексы фасок в коллекции API7, совпавших с признаком API5 по первому катету.
    /// Сопоставление по катету, а не по имени: F.8 измерила, что имя, данное в API5, читается из
    /// API7 иначе («f-ch2» → «Фаска:1»), — имя идентификатором не является.
    /// </summary>
    public static IReadOnlyList<int> FindIndexesByIdenticalFirstLeg(IModelContainer container, double distance1Mm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.Distance1Mm is double d && Math.Abs(d - distance1Mm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>Прочитать параметры фаски типизированно, по индексу коллекции API7.</summary>
    public static ChamferReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Chamfers[index] is not IChamfer chamfer)
            {
                return null;
            }

            return new ChamferReadDto(
                Transfer: null,
                Distance1Mm: Read(() => chamfer.Distance1),
                Distance2Mm: Read(() => chamfer.Distance2),
                AngleDeg: Read(() => chamfer.Angle),
                BuildingType: Text(() => chamfer.BuildingType.ToString()),
                Direction: Read(() => chamfer.Direction),
                Tangent: Read(() => chamfer.Tangent),
                BaseObjectCount: chamfer.BaseObjects is object[] array ? array.Length : null,
                ReadRoutes: new[] { "API7 IChamfer" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? Read(Func<bool> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>
/// Что именно прочиталось из родного отверстия. Отдельная запись, а не голая пара чисел, потому
/// что у трёх режимов SM-07 параметры живут в ТРЁХ разных интерфейсах, полученных приведением
/// одного и того же <c>IHole3D.HoleParameters</c>, и «чего сервер не прочитал» обязано быть
/// различимо от «прочитал ноль».
/// </summary>
public sealed record HoleReadDto(
    string? HoleType,
    double? DiameterMm,
    string? DepthType,
    double? DepthMm,
    string? EndFaceType,
    double? SpotfacingDiameterMm,
    double? SpotfacingDepthMm,
    double? CountersinkDiameterMm,
    double? CountersinkAngleDeg,
    double? CountersinkDepthMm,
    string? CountersinkType,
    IReadOnlyList<string> ReadRoutes);

/// <summary>
/// Типизированные операции API7 над родными отверстиями (SM-07). Каждый маршрут измерен пробой M
/// на v24 (<c>docs/acceptance/api7/hole-modes.md</c>), и ни один не объявлен поддержанным по
/// наличию типа: три попытки записать режимные параметры в сам <c>IHole3D</c> были отвергнуты
/// измерением, потому что режимные числа живут не там.
/// </summary>
/// <remarks>
/// <para>
/// <b>Где лежат параметры режима.</b> Не на <c>IHole3D</c> (13 членов, dispids 1…12; режимных
/// чисел среди них нет), а на <c>HoleParameters</c> — объекте, доступном только на чтение, который
/// приводится к интерфейсу СВОЕГО режима:
/// <list type="bullet">
/// <item>цековка — <c>ISpotfacingHoleParameters</c> (<c>SpotfacingDiameter</c>, <c>SpotfacingDepth</c>);</item>
/// <item>зенковка — <c>ICountersinkHoleParameters</c> (<c>CountersinkType</c>, <c>CountersinkDiameter</c>,
/// <c>CountersinkAngle</c>, <c>CountersinkDepth</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Глубина зенковки производна.</b> При <c>CountersinkType = ksCTDiameterAngle (0)</c> запись в
/// <c>CountersinkDepth</c> чисел 2, 4 и 6 не меняет НИЧЕГО: все три дают одинаковый снятый материал,
/// потому что глубина следует из диаметра и угла, и объект возвращает <c>(rM − rP)/tan(угол/2)</c>,
/// где <c>rM</c> — радиус устья, <c>rP</c> — радиус пилота. Раньше здесь стояло <c>4/tan(угол/2)</c>:
/// это была подгонка под единственную строку M.3, в которой <c>rM − rP</c> оказалось равным 4
/// (устье Ø18, пилот Ø10), и потому «4» выглядело константой. Проба N.2 от 17.09.2026 развела
/// устье при неизменных пилоте и угле и сняла серию Ø14/16/18/20/24 → h = 2/3/4/5/7, что совпало с
/// <c>(rM − rP)/tan(45°)</c> во всех пяти строках и разошлось с константой в четырёх из пяти.
/// Поэтому <c>TryCreateCountersink</c> не считает запись глубины доказательством геометрии, а читает
/// её обратно и возвращает вызывающему — сверять надо то, что вернул объект, а не то, что записали.
/// </para>
/// <para>
/// <b>Слепое отверстие — это ksDTValue.</b> Члена <c>ksDTBlind</c> в вендорском перечислении не
/// существует вовсе (измерено чтением TLB; <c>ksDepthTypeEnum</c> = ksDTValue 0, ksDTReachThrough 1,
/// ksDTObject 2), поэтому «глухое» выражается первым. Дно задаётся <c>ksEndFaceTypeEnum.ksEFFlat</c>.
/// </para>
/// <para>
/// <b>Позиция вне начала координат</b> задаётся <c>IHoleDisposal.Point3DParamSurface</c>, приведённым
/// к <c>IPoint3DParamSurface</c>, вместе с <c>OffsetType = ksOffsetByCoords (3)</c> и смещениями
/// <c>Offset1</c> (X) и <c>Offset2</c> (Y). Из пяти проверенных маршрутов этот — единственный
/// сдвинувший отверстие; <c>AssociationVertex</c> и <c>DirectionObject</c> дали DISP_E_TYPEMISMATCH,
/// эскиз со смещённой окружностью до API7 не доехал, а <c>DepthVertex</c>/<c>DepthFace</c> читаются
/// как null и <c>Axis</c> как False. <c>SetSurfaceObject(BaseSurface)</c> вернул False — это
/// записано как есть и маршрут не отменяет: отверстие двигают именно три записи смещения.
/// </para>
/// </remarks>
/// <summary>
/// Итог позиционирования: применено ли смещение, причина отказа и заметки о ходе маршрута.
/// Отдельный тип, а не тройка, потому что этой формой описывается <em>вставной шаг</em>: он
/// передаётся в <c>TryCreateBlindFlat</c>/<c>TryCreateCounterbore</c>/<c>TryCreateCountersink</c>
/// и выполняется между <c>Add()</c> и <c>Update()</c>, на том же объекте. Позиция поддерживается
/// всеми тремя режимами — это измерено приёмкой HO.6, где без такой ветки сквозная цековка молча
/// оставалась в начале координат при ошибке=None.
/// </summary>
internal sealed record PlacementOutcome(
    bool Applied,
    string? Failure,
    IReadOnlyList<string> Notes);

internal static class Api7Hole
{
    /// <summary>Глухое отверстие: значение глубины, а не ссылка на объект-ограничитель.</summary>
    private const ksDepthTypeEnum DepthByValue = ksDepthTypeEnum.ksDTValue;

    /// <summary>Сквозное отверстие насквозь — измерено на каждой строке M.2/M.3.</summary>
    private const ksDepthTypeEnum DepthReachThrough = ksDepthTypeEnum.ksDTReachThrough;

    /// <summary>Плоское дно: единственный режим, подтверждённый числом в M.4 (π·r²·h, 471.238898038471).</summary>
    private const ksEndFaceTypeEnum EndFaceFlat = ksEndFaceTypeEnum.ksEFFlat;

    /// <summary>Зенковка «диаметр + угол»: при нём глубина производна от угла.</summary>
    private const ksCountersinkTypeEnum CountersinkDiameterAngle = ksCountersinkTypeEnum.ksCTDiameterAngle;

    /// <summary>Позиция по координатам на базовой поверхности (измерено в M.5).</summary>
    private const ksPoint3DSurfaceParamTypeEnum OffsetByCoords = ksPoint3DSurfaceParamTypeEnum.ksOffsetByCoords;

    /// <summary>Число родных отверстий в коллекции API7. null — не прочитано (не «ноль»).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Holes3D.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Прочитать отверстие типизированно, по индексу коллекции API7. Поля режимов заполняются
    /// только тогда, когда объект действительно отвечает на интерфейс этого режима: у сквозного
    /// отверстия основного типа оба режимных набора останутся null, и это правильный ответ.
    /// </summary>
    public static HoleReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return null;
            }

            var spotfacing = hole.HoleParameters as ISpotfacingHoleParameters;
            var countersink = hole.HoleParameters as ICountersinkHoleParameters;

            return new HoleReadDto(
                HoleType: Text(() => hole.HoleType.ToString()),
                DiameterMm: Read(() => hole.Diameter),
                DepthType: Text(() => hole.DepthType.ToString()),
                DepthMm: Read(() => hole.Depth),
                EndFaceType: Text(() => hole.EndFaceType.ToString()),
                SpotfacingDiameterMm: spotfacing is null ? null : Read(() => spotfacing.SpotfacingDiameter),
                SpotfacingDepthMm: spotfacing is null ? null : Read(() => spotfacing.SpotfacingDepth),
                CountersinkDiameterMm: countersink is null ? null : Read(() => countersink.CountersinkDiameter),
                CountersinkAngleDeg: countersink is null ? null : Read(() => countersink.CountersinkAngle),
                CountersinkDepthMm: countersink is null ? null : Read(() => countersink.CountersinkDepth),
                CountersinkType: countersink is null ? null : Text(() => countersink.CountersinkType.ToString()),
                ReadRoutes: new[] { "API7 IHole3D" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО глухого отверстия с плоским дном (режим <c>blind_flat</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (зонд <c>scratch/_hole_edit_probe.py</c>, нога 2 — сырой
    /// помощник <c>scratch/hole-edit-raw</c>): существующее отверстие берётся документированным
    /// членом <c>IHoles3D.Hole3D[index]</c> (страница <c>iholes3d_hole3d.html</c>), в него
    /// записываются члены СВОЕГО режима, применяется <c>IModelObject.Update()</c> (страница
    /// <c>imodelobject_update.html</c>), и объём меняется ровно на аналитику: Ø10 6 → 8 мм сняло
    /// <c>157.079632679</c> мм³ = π·r²·2 (снято 628.318530718 против 471.238898038 до правки).
    /// </para>
    /// <para>
    /// Пишутся только члены ЭТОГО режима. <c>HoleType</c>, <c>DepthType</c> и <c>EndFaceType</c> —
    /// часть опознания режима, а не параметр вызывающего: у глухого это <c>ksHTBase</c>,
    /// <c>ksDTValue</c> и <c>ksEFFlat</c>, и оставить их «как прочиталось» значило бы править
    /// признак, режим которого задан не вызывающим.
    /// </para>
    /// <para>
    /// Незаданный числовой член НЕ записывается: вызывающий, меняющий только глубину, не должен
    /// получать перезапись диаметра «прежним» числом — прочитанное значение производно от модели и
    /// закреплять его как вход нельзя.
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteBlindFlat(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? depthMm)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.DepthType = DepthByValue;
            hole.EndFaceType = EndFaceFlat;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (depthMm is double depth)
            {
                hole.Depth = depth;
            }

            return hole.Update()
                ? (true, null)
                : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕЙ сквозной цековки (режим <c>through_counterbore</c>).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (зонд <c>scratch/_hole_edit_probe.py</c>, нога 2): выточка
    /// Ø18×4 → Ø20×5 сняла <c>474.380490692</c> мм³ сверх прежнего, что есть разность колец
    /// π/4·(D²−d²)·h — 1178.097244573 против 703.716754404 (M.2). Пишутся <c>Diameter</c> пилота,
    /// <c>DepthType = ksDTReachThrough</c> и члены <c>ISpotfacingHoleParameters</c>; незаданные не
    /// записываются.
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteCounterbore(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? boreDiameterMm,
        double? boreDepthMm)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCounterbore;
            hole.DepthType = DepthReachThrough;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (boreDiameterMm is null && boreDepthMm is null)
            {
                return hole.Update()
                    ? (true, null)
                    : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
            }

            // Интерфейс параметров СВОЕГО режима: у чужого режима он недостижим — измерено контролем
            // (в) зонда (на глухом и на зенковке ISpotfacingHoleParameters не отвечает вовсе).
            if (hole.HoleParameters is not ISpotfacingHoleParameters spotfacing)
            {
                return (false,
                    "IHole3D.HoleParameters не приводится к ISpotfacingHoleParameters — " +
                    "параметры выточки записать некуда");
            }

            if (boreDiameterMm is double bore)
            {
                spotfacing.SpotfacingDiameter = bore;
            }

            if (boreDepthMm is double boreDepth)
            {
                spotfacing.SpotfacingDepth = boreDepth;
            }

            return hole.Update()
                ? (true, null)
                : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕЙ сквозной зенковки (режим <c>through_countersink</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (зонд <c>scratch/_hole_edit_probe.py</c>, нога 2): устье
    /// Ø20 → Ø24 при 90° сняло <c>605.280184592</c> мм³ сверх прежнего — разность
    /// <c>π·h/3·(rM² + rP·rM − 2·rP²)</c> при производной <c>h = (rM − rP)/tan(угол/2)</c>
    /// (1128.878960190 против 523.598775598). Оба устья лежат в таблице M.3/N.2.
    /// </para>
    /// <para>
    /// <b>Глубина зенковки НЕ записывается</b> — при <c>ksCTDiameterAngle</c> она производна
    /// (M.3: запись 2, 4 и 6 не меняет ничего), поэтому запись в неё была бы числом, которое
    /// объект не читает. Возвращается прочитанное ПОСЛЕ <c>Update()</c>: до перестроения у
    /// производного свойства ещё прежнее значение.
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure, double? ReportedDepthMm) TryWriteCountersink(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? mouthDiameterMm,
        double? angleDeg)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D", null);
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.DepthType = DepthReachThrough;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (hole.HoleParameters is not ICountersinkHoleParameters countersink)
            {
                return (false,
                    "IHole3D.HoleParameters не приводится к ICountersinkHoleParameters — " +
                    "параметры зенковки записать некуда", null);
            }

            countersink.CountersinkType = CountersinkDiameterAngle;
            if (mouthDiameterMm is double mouth)
            {
                countersink.CountersinkDiameter = mouth;
            }

            if (angleDeg is double angle)
            {
                countersink.CountersinkAngle = angle;
            }

            if (!hole.Update())
            {
                return (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен",
                    null);
            }

            return (true, null, Safe(() => countersink.CountersinkDepth));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    /// <summary>
    /// Позиция и ось отверстия, прочитанные с ТЕЛА: у самого <c>IHole3D</c> координат нет —
    /// <c>Axis</c> читается как False, <c>DepthVertex</c>/<c>DepthFace</c> как null (измерено M.5).
    /// Маршрут тот же, что у топологии: <c>GetMainBody() → FaceCollection → GetSurfaceParam() →
    /// ksCylinderParam.GetPlacement()</c>. Возвращается начало оси найденной цилиндрической грани
    /// радиуса <paramref name="radiusMm"/> либо null, если такой грани на теле нет.
    /// </summary>
    public static double[]? FindCylinderOrigin(ksPart part, double radiusMm, double toleranceMm)
    {
        var all = FindCylinderOrigins(part, radiusMm, toleranceMm);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// ВСЕ начала осей цилиндрических граней радиуса <paramref name="radiusMm"/>, в порядке обхода
    /// граней.
    /// </summary>
    /// <remarks>
    /// Нужен там, где одного значения недостаточно. В документе с несколькими отверстиями одного
    /// диаметра «первое совпадение» — это чужая ось, и выдать её за ось созданного отверстия значило
    /// бы приписать признаку координаты соседа. Вызывающий, знающий ЗАПРОШЕННУЮ позицию, обязан
    /// сверить её со списком и отличить «нашли то, что просили» от «нашли что-то подходящее по
    /// радиусу».
    /// </remarks>
    public static IReadOnlyList<double[]> FindCylinderOrigins(ksPart part, double radiusMm, double toleranceMm)
    {
        var found = new List<double[]>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            return found;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not ksFaceDefinition face)
            {
                continue;
            }

            try
            {
                if (face.GetSurface() is not ksSurface surface || !surface.IsCylinder()
                    || surface.GetSurfaceParam() is not ksCylinderParam cylinder)
                {
                    continue;
                }

                if (Math.Abs(cylinder.radius - radiusMm) > toleranceMm)
                {
                    continue;
                }

                if (cylinder.GetPlacement() is ksPlacement placement
                    && placement.GetOrigin(out var x, out var y, out var z))
                {
                    found.Add(new[] { x, y, z });
                }
            }
            catch (Exception ex) when (ex is COMException)
            {
                // Грань, параметры которой КОМПАС не отдал, — не повод бросить поиск остальных.
            }
        }

        return found;
    }

    /// <summary>
    /// Создать сквозную цековку: пилотное отверстие насквозь плюс выточка. Возвращает пару
    /// (создано, причина), а не исключение: вызывающий обязан отличить отказ КОМПАСа от падения
    /// адаптера, и «не смогли» не должно выглядеть как «создали».
    /// </summary>
    /// <remarks>
    /// Материал снимается ровно как <c>π·r²·h</c> пилота плюс <c>π/4·(D²−d²)·h_выточки</c> —
    /// кольцо, а не второй полный цилиндр: с пилотом Ø10, выточкой Ø18 глубиной 4 проба сняла
    /// 703.7167544041131 мм³ сверх сквозного, и π/4·(18²−10²)·4 = 703.7167544041137 (M.2).
    /// </remarks>
    public static (bool Created, string? Failure) TryCreateCounterbore(
        IModelContainer container,
        IModelObject baseSurface,
        double pilotDiameterMm,
        double boreDiameterMm,
        double boreDepthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D");
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCounterbore;
            hole.Diameter = pilotDiameterMm;
            hole.DepthType = DepthReachThrough;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // Смещение пишется ДО Update() и на ЭТОМ ЖЕ объекте: после Update() признак построен, и
            // запись осталась бы представлением. Позиция поддерживается всеми тремя режимами, а не
            // только глухим: приёмка HO.6 показала, что без этой ветки сквозная цековка молча
            // оставалась в начале координат при ошибке=None — то есть неверная позиция выдавалась
            // за выполненную.
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено");
                }
            }

            if (hole.HoleParameters is not ISpotfacingHoleParameters spotfacing)
            {
                return (false, "IHole3D.HoleParameters не приводится к ISpotfacingHoleParameters — режим цековки недостижим");
            }

            spotfacing.SpotfacingDiameter = boreDiameterMm;
            spotfacing.SpotfacingDepth = boreDepthMm;

            return hole.Update() ? (true, null) : (false, "IHole3D.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Создать сквозную зенковку: пилотное отверстие насквозь плюс коническая фаска.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Снятый сверх пилота материал равен <c>π·h/3 · (rM² + rP·rM − 2·rP²)</c>, где <c>h</c> —
    /// глубина, которую вернул САМ объект, <c>rM</c> — радиус устья, <c>rP</c> — радиус пилота.
    /// Правило прочитано с таблицы из 11 строк (3 угла × 3 входные глубины × 6 диаметров) в M.3, а
    /// не подобрано под одну точку.
    /// </para>
    /// <para>
    /// <paramref name="depthMm"/> передаётся в <c>CountersinkDepth</c> для полноты контракта, но при
    /// <c>CountersinkType = ksCTDiameterAngle</c> это свойство ПРОИЗВОДНОЕ: запись 2, 4 или 6 не
    /// меняет ничего. Поэтому возвращаемая глубина читается обратно, и судить о геометрии следует
    /// по ней, а не по записанному числу.
    /// </para>
    /// </remarks>
    public static (bool Created, string? Failure, double? ReportedDepthMm) TryCreateCountersink(
        IModelContainer container,
        IModelObject baseSurface,
        double pilotDiameterMm,
        double mouthDiameterMm,
        double angleDeg,
        double depthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D", null);
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем", null);
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.Diameter = pilotDiameterMm;
            hole.DepthType = DepthReachThrough;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // Смещение пишется ДО Update() и на ЭТОМ ЖЕ объекте (см. TryCreateCounterbore).
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено", null);
                }
            }

            if (hole.HoleParameters is not ICountersinkHoleParameters countersink)
            {
                return (false, "IHole3D.HoleParameters не приводится к ICountersinkHoleParameters — режим зенковки недостижим", null);
            }

            countersink.CountersinkType = CountersinkDiameterAngle;
            countersink.CountersinkDiameter = mouthDiameterMm;
            countersink.CountersinkAngle = angleDeg;
            countersink.CountersinkDepth = depthMm;

            if (!hole.Update())
            {
                return (false, "IHole3D.Update() вернул false", null);
            }

            // Глубина читается ПОСЛЕ Update(): у производного свойства до перестроения ещё
            // прежнее значение, и выдать его за результат значило бы соврать про геометрию.
            double? reported = Safe(() => countersink.CountersinkDepth);
            return (true, null, reported);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    /// <summary>
    /// Создать глухое отверстие с плоским дном: <c>ksDTValue</c> (члена <c>ksDTBlind</c> в
    /// вендорском перечислении нет) плюс <c>ksEFFlat</c>. Снятый материал равен <c>π·r²·h</c>:
    /// Ø10 глубиной 6 сняли 471.238898038471 против аналитических 471.238898038469 (M.4).
    /// </summary>
    public static (bool Created, string? Failure) TryCreateBlindFlat(
        IModelContainer container,
        IModelObject baseSurface,
        double diameterMm,
        double depthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D");
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.Diameter = diameterMm;
            hole.DepthType = DepthByValue;
            hole.Depth = depthMm;
            hole.EndFaceType = EndFaceFlat;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // Смещение пишется ДО Update() и на ЭТОМ ЖЕ объекте (см. TryCreateCounterbore).
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено");
                }
            }

            return hole.Update() ? (true, null) : (false, "IHole3D.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Сдвинуть отверстие с начала координат: <c>Point3DParamSurface</c> плюс
    /// <c>OffsetType = ksOffsetByCoords</c> и смещения по X и Y.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Возвращается (применено, причина, заметки о ходе маршрута): <c>SetSurfaceObject</c> вернул
    /// False в измерении, и это записано как есть, а не выдано за успех. Маршрут от этого не
    /// ломается — отверстие двигают три записи смещения, и проба M.5 поставила его ровно в
    /// запрошенные (25, 15).
    /// </para>
    /// <para>
    /// Если объект не отвечает на <c>IPoint3DParamSurface</c>, это ОТКАЗ, а не «вызвали и
    /// промолчали»: принять смещения и оставить отверстие в начале координат означало бы выдать
    /// невыполненное позиционирование за выполненное.
    /// </para>
    /// </remarks>
    public static PlacementOutcome TryPlaceByCoordinates(
        IHoleDisposal disposal,
        double offsetXmm,
        double offsetYmm)
    {
        var notes = new List<string>();
        try
        {
            if (disposal.Point3DParamSurface is null)
            {
                return new PlacementOutcome(
                    false,
                    "IHoleDisposal.Point3DParamSurface вернул null — маршрут позиционирования недоступен",
                    notes);
            }

            if (disposal.Point3DParamSurface is not IPoint3DParamSurface surface)
            {
                return new PlacementOutcome(
                    false,
                    "Point3DParamSurface не приводится к IPoint3DParamSurface — смещения записать некуда",
                    notes);
            }

            disposal.OffsetType = OffsetByCoords;
            surface.Offset1 = offsetXmm;
            surface.Offset2 = offsetYmm;

            notes.Add(
                $"OffsetType={OffsetByCoords} (ksOffsetByCoords), Offset1={offsetXmm}, Offset2={offsetYmm}");
            if (surface.SetSurfaceObject(disposal.BaseSurface) is { } accepted)
            {
                // Измерено: False. Записано как есть — этот факт и есть причина, по которой
                // маршрут описывается тремя записями смещения, а не этой привязкой.
                notes.Add("SetSurfaceObject(BaseSurface) → " + accepted);
            }

            return new PlacementOutcome(true, null, notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new PlacementOutcome(false, ex.GetType().Name + ": " + ex.Message, notes);
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>
/// Ось вращения, построенная в детали по двум точкам и перечитанная оттуда же.
/// </summary>
/// <remarks>
/// Возвращается и объект оси (для <c>IRotated.Axis</c>), и то, что удалось с него прочитать:
/// «ось построена» без чтения её состояния — это код возврата, а не факт. <see cref="Valid"/>
/// отделён от null: <c>null</c> здесь означает «состояние прочитать не удалось», <c>false</c> —
/// «КОМПАС считает ось негодной», и это разные ответы.
/// </remarks>
internal sealed record AxisHandle(
    IAxis3D Axis,
    IAxis3DBy2Points By2Points,
    bool? Updated,
    bool? Valid,
    int? AxesBefore,
    int? AxesAfter,
    IReadOnlyList<string> Notes);

/// <summary>
/// Типизированные операции API7 над вращением (SM-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>Что здесь воспроизведено и чем доказано.</b> Прогон <c>95fa844107ce41609d6278f8f6c5759f</c>
/// от 17.09.2026, шаги <c>R.24</c> (перебор всех трёх видов операции), <c>R.25</c> (эталон задания
/// с осью из своей детали), <c>R.26</c> (что операция делает с уже существующим телом). Все шаги
/// SM-03 в том прогоне — PASS; PASS 31 · FAIL 21 · UNKNOWN 6 по всему прогону.
/// </para>
/// <para>
/// <b>Чего здесь СОЗНАТЕЛЬНО нет.</b> Ни <c>NewEntity</c>, ни <c>Create()</c>. Прежняя блокировка
/// была ровно в этом: оболочка API5 вокруг объекта фабрики API7 — смешанный жизненный цикл, при
/// котором <c>Create()</c> возвращает <c>true</c> на пустой операции, объект появляется в дереве,
/// <c>Update()</c> читается как True, и ничего не строится. Поэтому маршрут здесь — только
/// <c>Rotateds.Add → IRotated → Update()</c>, и он один принимает <c>Profile</c> и <c>Axis</c>.
/// </para>
/// <para>
/// <b>Почему <c>OperationResult</c> всё-таки записывается.</b> Не потому, что он что-то решает —
/// измерено (R.26), что он НЕ решает: <c>boss</c> с записанным и прочитанным обратно
/// <c>ksOperationCut</c> изменил объём на 0. Он записывается ради согласованности модели с тем,
/// что КОМПАС показывает в дереве, и читается обратно, чтобы расхождение было видно. Вызывающий не
/// должен на него полагаться, и вид операции приходит из <see cref="RotationOperation"/>.
/// </para>
/// <para>
/// <b>Закон угла, установленный 18.09.2026 и заменивший прежнюю гипотезу.</b> Прежнее
/// утверждение «угол насыщается на 180°, запись 360 даёт половину оборота» происходило из шага
/// <c>R.26.angles</c>, который менял <c>CutOffByPoint</c>, а не угол, и ни в одной своей строке не
/// записывал <c>Angle[true] = 360</c> с полным набором параметров развёртки. Проба
/// <c>tools/KompasMcp.Api7Probe/FullTurnProbe.cs</c> (шаги F.1…F.5) и независимое чтение
/// сохранённого <c>.m3d</c> пробой <c>M3dVerificationProbe</c> дали иное: <c>Angle[true]</c> несёт
/// запрошенный угол НАПРЯМУЮ, а вторая половина пары, равная первой, удваивает развёртку. Значит
/// <c>(360, 0) → 360°</c> (полный цилиндр, <c>V = 50265.4824574366</c>), <c>(180, 0) → 180°</c>
/// (<c>25132.7412287183</c>), <c>(90, 0) → 90°</c>, <c>(180, 180) → 360°</c>. Полный оборот
/// выражается одним вызовом, и вид операции (<c>base</c>/<c>boss</c>/<c>cut</c>) на закон не
/// влияет. Разбор и таблица — <c>docs/acceptance/api7/full-turn-findings.md</c>.
/// </para>
/// </remarks>
internal static class Api7Rotated
{
    /// <summary>Вид операции → вендорский тип. Числа измерены в R.24 (27/28/29).</summary>
    public static ksObj3dTypeEnum TypeOf(RotationOperation operation) => operation switch
    {
        RotationOperation.Base => ksObj3dTypeEnum.o3d_baseRotated,
        RotationOperation.Boss => ksObj3dTypeEnum.o3d_bossRotated,
        RotationOperation.Cut => ksObj3dTypeEnum.o3d_cutRotated,
        _ => ksObj3dTypeEnum.o3d_bossRotated,
    };

    /// <summary>Направление → вендорское перечисление. <c>dtReverse</c> отсекается вызывающим.</summary>
    public static ksDirectionTypeEnum DirectionOf(RotationDirection direction) => direction switch
    {
        RotationDirection.Normal => ksDirectionTypeEnum.dtNormal,
        RotationDirection.Reverse => ksDirectionTypeEnum.dtReverse,
        RotationDirection.Both => ksDirectionTypeEnum.dtBoth,
        RotationDirection.MiddlePlane => ksDirectionTypeEnum.dtMiddlePlane,
        _ => ksDirectionTypeEnum.dtNormal,
    };

    /// <summary>
    /// <c>OperationResult</c>, соответствующий виду операции. Записывается для согласованности с
    /// деревом, не для переключения действия: измерено, что этот член не влияет ни на что (R.26).
    /// </summary>
    public static ksOperationResultEnum OperationResultOf(RotationOperation operation) => operation switch
    {
        RotationOperation.Base => ksOperationResultEnum.ksOperationNewBody,
        RotationOperation.Boss => ksOperationResultEnum.ksOperationUnion,
        RotationOperation.Cut => ksOperationResultEnum.ksOperationCut,
        _ => ksOperationResultEnum.ksOperationNewBody,
    };

    /// <summary>Код операции в ответе — то же слово, что в контракте, а не перечисление вендора.</summary>
    public static string NameOf(RotationOperation operation) =>
        operation.ToString().ToLowerInvariant();

    /// <summary>Число вращений в коллекции API7. null — не прочитано (не «ноль»).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Rotateds?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Построить ось вращения по двум точкам МОДЕЛИ в собственной детали.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Путь к коллекции осей нетривиален и это измерено.</b> <c>Axes3D</c> объявлен на
    /// <c>IAuxiliaryGeomContainer</c> (IID <c>{950FEBE2-F916-4E77-A37D-B061E5C22FA8}</c>), а НЕ на
    /// <c>IModelContainer</c>: обычное приведение контейнера даёт <c>null</c>, работает только QI
    /// на живом объекте детали. <c>part</c> переносится тем же мостом (<c>ksAPI7Dual</c>), что и
    /// документ, — второй экземпляр КОМПАСа для этого не нужен (ADR-004 §1).
    /// </para>
    /// <para>
    /// <b><c>Update()</c> вызывается ПОСЛЕ обеих точек</b> и это не стиль: измерено (R.13), что ось,
    /// обновлённая до подачи точек, читается <c>Valid=False</c> и в дерево не входит. Тот же порядок
    /// у вращения: <c>Update()</c> после всех записей.
    /// </para>
    /// <para>
    /// <b>Негодная ось не отбрасывается, а называется.</b> Если <c>Valid</c> не True, это
    /// записывается в заметки и возвращается вызывающему: отказ вращения на негодной оси не был бы
    /// фактом о вращении, и подменять причину отказом нельзя. Возвращается <c>null</c> только когда
    /// ось построить не удалось вовсе — это отдельный исход.
    /// </para>
    /// </remarks>
    public static AxisHandle? TryBuildAxisBy2Points(
        Api7Bridge bridge,
        object part,
        double[] point1,
        double[] point2)
    {
        var notes = new List<string>();
        try
        {
            if (bridge.TransferTo7(part) is not IModelObject part7)
            {
                notes.Add("деталь не переносится в API7 как IModelObject — ось не построить");
                return null;
            }

            if (part7 is not IModelContainer modelContainer)
            {
                notes.Add("перенесённая деталь не отвечает QI(IModelContainer) — Points3D недостижим");
                return null;
            }

            // QI, а не приведение контейнера: Axes3D объявлен на ДРУГОМ интерфейсе того же
            // объекта, и это измерено (см. remarks).
            if (part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                notes.Add("перенесённая деталь не отвечает QI(IAuxiliaryGeomContainer) " +
                    "(IID {950FEBE2-F916-4E77-A37D-B061E5C22FA8}) — Axes3D недостижим");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes)
            {
                notes.Add("IAuxiliaryGeomContainer.Axes3D → null");
                return null;
            }

            if (MakePoint(modelContainer, point1) is not { } p1
                || MakePoint(modelContainer, point2) is not { } p2)
            {
                notes.Add("точки оси не создались — ось не построить");
                return null;
            }

            var before = SafeInt(() => axes.Count);
            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                notes.Add("Axes3D.Add(o3d_axis2Points) не отдал QI(IAxis3DBy2Points)");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;

            // Update() только здесь, с обеими точками уже поданными (измерено R.13).
            var updated = SafeBool(by2.Update);
            var valid = SafeBool(() => by2.Valid);
            var after = SafeInt(() => axes.Count);

            notes.Add(
                $"ось по двум точкам: Update()={Raw(updated)}, Valid={Raw(valid)}, " +
                $"осей {Raw(before)}→{Raw(after)}");
            if (valid != true)
            {
                notes.Add("ось построена, но Valid не True — передаю её всё равно: отказ вращения на " +
                    "негодной оси не был бы фактом о вращении");
            }

            return new AxisHandle(by2, by2, updated, valid, before, after, notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"построение оси бросило {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Создать вращение и записать в него параметры, затем <c>Update()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Возвращается (объект, причина отказа): вызывающий обязан отличить отказ КОМПАСа от падения
    /// адаптера, и «не смогли» не должно выглядеть как «создали». Объект отдаётся наружу, потому
    /// что после <c>Update()</c> его надо прочитать обратно — а прочитанное и есть доказательство.
    /// </para>
    /// <para>
    /// <b>Порядок записей — часть контракта.</b> <c>Profile</c> и <c>Axis</c> до остальных: у
    /// вращения без оси развёртки не существует вовсе, и запись угла в такой объект ничего не
    /// значит.
    /// </para>
    /// <para>
    /// <b>Угол пишется в первую половину пары, вторая — ноль. Это измерено 18.09.2026, и прежнее
    /// обоснование было другим («вторая половина — состояние ядра»).</b> Измеренный закон
    /// (<c>docs/acceptance/api7/full-turn-findings.md</c>, проба
    /// <c>tools/KompasMcp.Api7Probe/FullTurnProbe.cs</c>, шаги F.1/F.1a…F.1e, независимая проверка —
    /// <c>M3dVerificationProbe</c>): <c>Angle[true]</c> несёт ЗАПРОШЕННЫЙ угол развёртки напрямую,
    /// а вторая половина пары, будучи равной первой, развёртку УДВАИВАЕТ. Отсюда
    /// <c>(360, 0) → 360°</c> (полный цилиндр, <c>V = 50265.4824574366</c>), <c>(180, 0) → 180°</c>
    /// (полуцилиндр, <c>25132.7412287183</c>), <c>(90, 0) → 90°</c>, но <c>(180, 180) → 360°</c>.
    /// Ноль во второй половине — не «пустое поле», а условие «поворот равен запрошенному углу».
    /// Вид операции (<c>base</c>/<c>boss</c>/<c>cut</c>) на закон не влияет.
    /// </para>
    /// </remarks>
    public static (IRotated? Rotation, string? Failure) TryCreate(
        IModelContainer container,
        RotationOperation operation,
        IModelObject profile,
        IAxis3D axis,
        double angleDeg,
        RotationDirection direction,
        bool thin)
    {
        try
        {
            var rotateds = container.Rotateds;
            if (rotateds is null)
            {
                return (null, "IModelContainer.Rotateds → null: фабрика недостижима");
            }

            if ((rotateds.Add(TypeOf(operation)) as IModelObject) as IRotated is not { } rotation)
            {
                return (null, "Rotateds.Add не отдал объект, отвечающий на QI(IRotated)");
            }

            rotation.Profile = profile;
            rotation.Axis = axis;

            // Индексированные свойства: и геттер, и сеттер принимают Boolean Normal. Запись без
            // индекса не компилируется (CS0856) — компилятор здесь дешевле, чем молчаливая запись
            // в член, которого у объекта нет.
            //
            // Угол пишется В ПЕРВУЮ половину пары, а вторая — НОЛЬ. Это измерено, а не выбрано по
            // симметрии (FullTurnProbe, шаги F.1/F.1a…F.1e, docs/acceptance/api7/full-turn-findings.md):
            //   Angle[true]=360, Angle[false]=0 → ПОЛНЫЙ цилиндр (50265.4824574366), читается 360;
            //   Angle[true]=180, Angle[false]=0 → ПОЛОВИНА     (25132.7412287183), читается 180;
            //   Angle[true]=90,  Angle[false]=0 → ЧЕТВЕРТЬ;
            //   Angle[true]=180, Angle[false]=180 → ПОЛНЫЙ цилиндр.
            // Отсюда: Angle[true] несёт ЗАПРОШЕННЫЙ угол развёртки напрямую, а вторая половина
            // пары, будучи равной первой, развёртку УДВАИВАЕТ. Поэтому ноль во второй половине —
            // это условие «угол поворота равен запрошенному», а не «пустое поле»: поставив туда
            // же число, мы получили бы двойной сектор и выдали бы его за запрошенный.
            rotation.Angle[true] = angleDeg;
            rotation.Angle[false] = 0d;
            rotation.Direction = DirectionOf(direction);
            rotation.RotatedType[true] = ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;

            // Записывается для согласованности с деревом и читается обратно вызывающим. Измерено
            // (R.26): на действие этот член НЕ влияет — boss с ksOperationCut изменил объём на 0.
            if (rotation is IRotated1 rotated1)
            {
                rotated1.OperationResult = OperationResultOf(operation);
            }

            // Тонкая стенка: маршрут измерен на СПЛОШНОМ теле. Значение true сюда не доходит —
            // вызывающий отвергает заданную тонкую стенку до COM, — но и здесь оно не «на всякий
            // случай»: false есть измеренная настройка, а не значение по умолчанию.
            if (rotation is IThinParameters thinParameters)
            {
                thinParameters.Thin = thin;
            }

            return rotation.Update()
                ? (rotation, null)
                : (rotation, "IRotated.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Прочитать вращение типизированно, по индексу коллекции API7.</summary>
    public static RotatedDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Rotateds?[index] is not IRotated rotation)
            {
                return null;
            }

            var axisState = Safe(() => rotation.Axis) is null ? "нет" : "есть";
            return new RotatedDto(
                OperationType: Text(() => rotation.RotatedType[true].ToString()),
                AngleDeg: Read(() => rotation.Angle[true]),
                Direction: Text(() => rotation.Direction.ToString()),
                AxisState: axisState,
                // Профиль у вращения — ОДИН объект (IModelObject), а не массив, как BaseObjects у
                // скругления. Поэтому «число входов» здесь либо 1, либо null, и выдавать за него
                // длину массива нельзя — это разные типы. null означает «профиль не читается».
                ProfileInputCount: Safe(() => rotation.Profile) is null ? null : 1);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Индексы вращений, у которых прочитался заданный угол. Опознание по углу — ЗАПАСНОЕ, и это
    /// надо называть вслух: угол не является признаком тождества объекта. Раньше здесь стояло
    /// обоснование «развёртка насыщается на 180°, поэтому у вращения на 180° и на 360° угол в
    /// модели одинаков» — оно ОПРОВЕРГНУТО измерением 18.09.2026
    /// (<c>docs/acceptance/api7/full-turn-findings.md</c>): насыщения нет, а угол читается тем,
    /// каким построен. Причина осторожности осталась, но другая и более простая: два признака с
    /// одинаковым углом существуют как угодно часто (два полуоборота вокруг разных осей), и
    /// «взять первый» означало бы править не тот признак. Поэтому два кандидата — это «опознать
    /// нечем», а не «взять первый».
    /// </summary>
    public static IReadOnlyList<int> FindIndexesByAngle(IModelContainer container, double angleDeg)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.AngleDeg is double angle && Math.Abs(angle - angleDeg) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// Индекс вращения, соответствующего сущности API5, по СОСТАВУ (угол и направление). Возвращает
    /// <c>null</c>, если совпадений нет или их больше одного: «взять первый» здесь означает
    /// записать угол в чужой признак.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сопоставление по углу слабо — два полуоборота вокруг разных осей дадут одинаковую пару, — и
    /// именно поэтому неоднозначность разрешается ОТКАЗОМ, а не выбором. Это тот же принцип, что и
    /// у <c>FindIndexesByIdenticalRadius</c> у скругления, но с тем отличием, что здесь
    /// сопоставляются ещё и направление, поэтому на практике кандидат обычно один.
    /// </para>
    /// <para>
    /// <b>Когда состав не читается, вызывающий передаёт <paramref name="knownIndex"/>.</b> Сущность,
    /// взятая из дерева API5, приходит сырым <c>__ComObject</c> и на приведение к <c>IRotated</c>
    /// отвечает отказом (измерено 18.09.2026), поэтому эталона для сравнения не существует. Тогда
    /// адрес известен ЗАРАНЕЕ — это позиция признака среди вращений дерева (см.
    /// <c>RotatedOrdinal</c>), и пересопоставлять по составу, которого нет, значило бы отказать
    /// адресуемому признаку.
    /// </para>
    /// </remarks>
    public static int? FindIndexFor(IModelContainer container, ksEntity entity, int? knownIndex = null)
    {
        if (Count(container) is not int count || count <= 0)
        {
            return null;
        }

        if (knownIndex is int known)
        {
            return known >= 0 && known < count ? known : null;
        }

        // Угол и направление адресованной сущности: сущность дерева API5 отвечает на IRotated тем же
        // объектом модели, поэтому её значения и есть эталон для сопоставления.
        double? wantAngle = null;
        string? wantDirection = null;
        if (entity is IRotated rotated)
        {
            wantAngle = Read(() => rotated.Angle[true]);
            wantDirection = Text(() => rotated.Direction.ToString());
        }

        var matches = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var candidate = Read(container, i);
            if (candidate is null)
            {
                continue;
            }

            if (wantAngle is double want
                && candidate.AngleDeg is double got
                && Math.Abs(got - want) > 1e-6)
            {
                continue;
            }

            if (wantDirection is not null && candidate.Direction is not null
                && !string.Equals(candidate.Direction, wantDirection, StringComparison.Ordinal))
            {
                continue;
            }

            matches.Add(i);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Записать угол и направление в СУЩЕСТВУЮЩЕЕ вращение и выполнить <c>Update()</c>. Возвращает
    /// пару (записано, причина), а не исключение: вызывающий обязан отличить отказ КОМПАСа от
    /// падения адаптера.
    /// </summary>
    /// <remarks>
    /// Пишутся только заданные поля: незаданное остаётся тем, что лежит в модели. <c>Angle[false]</c>
    /// сбрасывается в ноль ВСЕГДА, когда пишется угол, — измерено (F.1a против F.1c), что вторая
    /// половина пары, равная первой, удваивает развёртку, поэтому «оставить как есть» здесь значило
    /// бы получить двойной сектор при, казалось бы, точечной правке угла.
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteAngleAndDirection(
        IModelContainer container,
        int index,
        double? angleDeg,
        RotationDirection? direction)
    {
        try
        {
            if (container.Rotateds?[index] is not IRotated rotation)
            {
                return (false, $"элемент коллекции Rotateds[{index}] не отдаёт IRotated");
            }

            if (angleDeg is double angle)
            {
                rotation.Angle[true] = angle;
                rotation.Angle[false] = 0d;
            }

            if (direction is RotationDirection want)
            {
                rotation.Direction = DirectionOf(want);
            }

            return rotation.Update()
                ? (true, null)
                : (false, "IRotated.Update() вернул false — угол записан, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Точка модели из массива координат. Проверяется и длина, и что объект создался: «принял
    /// массив из двух чисел» дало бы точку в начале координат, и ось встала бы не туда.
    /// </summary>
    private static IPoint3D? MakePoint(IModelContainer container, IReadOnlyList<double> coordinates)
    {
        if (coordinates.Count != 3)
        {
            return null;
        }

        try
        {
            if (container.Points3D?.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = coordinates[0];
            point.Y = coordinates[1];
            point.Z = coordinates[2];
            return point.Update() ? point : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? SafeBool(Func<bool> value) => Safe(value);

    private static int? SafeInt(Func<int> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }

    private static string Raw(object? value) => value?.ToString() ?? "не прочитано";
}

/// <summary>
/// Параметры скругления, перечитанные из API7. Радиус читается только оттуда: у API5
/// <c>ksFilletDefinition.radius</c> есть и читается, но запись в него на существующем признаке
/// НЕ применяется — измерено 16.09.2026 (строка FL04r). Поэтому авторитетным источником радиуса
/// здесь служит <c>IFillet.Radius1</c>, а не определение API5.
/// </summary>
public sealed record FilletReadDto(
    double? RadiusMm,
    double? Radius2Mm,
    bool? Tangent,
    string? BuildingType,
    int? BaseObjectCount,
    IReadOnlyList<int>? BaseObjectReferences,
    IReadOnlyList<string> ReadRoutes);

/// <summary>
/// Типизированные операции API7 над скруглением (SM-09). Отличий от <see cref="Api7Chamfer"/> два,
/// и оба измерены, а не выведены из сходства имён:
/// <list type="number">
/// <item>радиус живёт в <c>IFillet.Radius1</c> (dispid 3), а не в <c>IChamfer.Distance1</c>;</item>
/// <item>в API5 запись радиуса на существующем признаке не применяется, поэтому маршрут API7 здесь
/// не «альтернатива», а единственный работающий — см. <c>Api5Session.UpdateFilletRadius</c>.</item>
/// </list>
/// </summary>
internal static class Api7Fillet
{
    /// <summary>Число скруглений в коллекции API7. null — не прочитано (не «ноль»).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Fillets.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Прочитать параметры скругления типизированно, по индексу коллекции API7.</summary>
    public static FilletReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return null;
            }

            // Входы читаются тем же вызовом, что и счётчик: счётчик без ссылок бесполезен для
            // адресации (правка набора идёт по составу входов, а не по их числу), а разница между
            // «не прочитано» и «пустой набор» обязана быть видна вызывающему.
            var inputs = ReadBaseObjects(container, index);
            return new FilletReadDto(
                RadiusMm: Read(() => fillet.Radius1),
                Radius2Mm: Read(() => fillet.Radius2),
                Tangent: Read(() => fillet.Tangent),
                BuildingType: Text(() => fillet.BuildingType.ToString()),
                BaseObjectCount: fillet.BaseObjects is object[] array ? array.Length : null,
                BaseObjectReferences: inputs is null ? null : ReferencesOf(inputs),
                ReadRoutes: new[] { "API7 IFillet" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Индексы скруглений в коллекции API7, совпавших с признаком API5 по радиусу. Сопоставление
    /// по числу, а не по имени: F.8 измерила, что имя, данное в API5, читается из API7 иначе, —
    /// имя идентификатором не является.
    /// </summary>
    public static IReadOnlyList<int> FindIndexesByIdenticalRadius(IModelContainer container, double radiusMm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.RadiusMm is double r && Math.Abs(r - radiusMm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// Записать радиус в СУЩЕСТВУЮЩЕЕ скругление через <c>IFillet</c> — маршрут, которым правится
    /// радиус, потому что запись через API5 на существующем признаке не применяется.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пишутся только заданные поля: незаданное остаётся тем, что лежит в модели. Порядок
    /// «запись → Update() → RebuildModel()» — часть контракта, а не стиль: без <c>Update()</c>
    /// сеттеры возвращают успех, а модель остаётся прежней (измерено на <c>IChamfer</c> пробой
    /// F.10 и на <c>IExtrusion.Sketch</c> пробой E).
    /// </para>
    /// <para>
    /// Возвращается пара (записано, причина), а не исключение: вызывающий обязан отличить отказ
    /// КОМПАСа от падения адаптера, и «не смогли» не должно выглядеть как «записали».
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteRadius(
        IModelContainer container,
        int index,
        double? radiusMm,
        double? radius2Mm,
        bool? tangent)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return (false, $"элемент коллекции Fillets[{index}] не отдаёт IFillet");
            }

            if (radiusMm is double r1)
            {
                fillet.Radius1 = r1;
            }

            if (radius2Mm is double r2)
            {
                fillet.Radius2 = r2;
            }

            if (tangent is bool t)
            {
                fillet.Tangent = t;
            }

            if (!fillet.Update())
            {
                return (false, "IFillet.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Индексы скруглений, у которых МНОЖЕСТВО ссылок входов совпадает с заданным. Опознание
    /// признака по составу, а не по имени (F.8: имя в API5 и API7 не совпадает), не по индексу
    /// (порядок выдачи произволен) и не по радиусу (два скругления одного радиуса неразличимы —
    /// именно на такой модели и ставился опыт адресации H2.7).
    /// </summary>
    public static IReadOnlyList<int> FindIndexesByInputReferences(
        IModelContainer container,
        IReadOnlyList<int> references)
    {
        var found = new List<int>();
        if (Count(container) is not int count || references.Count == 0)
        {
            return found;
        }

        var wanted = references.OrderBy(x => x).ToArray();
        for (var i = 0; i < count; i++)
        {
            var inputs = ReadBaseObjects(container, i);
            if (inputs is null)
            {
                continue;
            }

            var actual = ReferencesOf(inputs).OrderBy(x => x).ToArray();
            if (actual.SequenceEqual(wanted))
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// Единственное скругление с заданным составом входов. Возвращается <c>null</c> и при нуле, и
    /// при нескольких совпадениях: «взять первое попавшееся» означало бы записать набор в чужой
    /// признак и молча испортить чужую геометрию.
    /// </summary>
    public static int? FindIndexByInputReferences(
        IModelContainer container,
        IReadOnlyList<int> references)
    {
        var matches = FindIndexesByInputReferences(container, references);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Индексы скруглений, у которых прочитался заданный радиус. Это запасное опознание для
    /// случая, когда состав входов прочитать неоткуда: определение API5 на существующем признаке
    /// рёбер не отдаёт (измерено — 0 из 4 после скругления, строка FL10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему это не заменяет опознание по составу.</b> Радиус не различает два скругления
    /// одного радиуса — измерено (H2.7), что именно такая модель и ставилась. Поэтому вызывающий
    /// обязан отвергнуть неоднозначный ответ: два кандидата — это не «взять первый», а «опознать
    /// нечем».
    /// </para>
    /// <para>
    /// <b>Зачем всё-таки нужен.</b> Маршрут определения API5 умеет прочитать радиус признака
    /// всегда, а вот состав входов — не всегда. Сужение по радиусу вместе с последующей проверкой
    /// состава после записи даёт рабочее опознание там, где основной путь недоступен.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> FindIndexesByRadius(IModelContainer container, double radiusMm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            var read = Read(container, i);
            if (read?.RadiusMm is double r && Math.Abs(r - radiusMm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>
    /// Входы живого скругления — <c>IFillet.BaseObjects</c> как список объектов, пригодных к
    /// предъявлению обратно. Возвращается <c>null</c>, когда член не читается, и пустой список,
    /// когда признак не удерживает ни одного входа: «не прочитано» и «ноль» — разные ответы.
    /// </summary>
    /// <remarks>
    /// Элементы берутся как <see cref="IModelObject"/>, а не как <c>ksEntity</c>: измерено пробой H-2
    /// (шаг H2.2), что <c>BaseObjects</c> отдаёт <c>System.Object[]</c> из <c>System.__ComObject</c>,
    /// которые приводятся к <c>IModelObject</c> (4 из 4) и НЕ приводятся к <c>ksEntity</c>. Писать
    /// надо именно те объекты, которые признак отдал: обратный перенос в <c>ksEntity</c> теряет
    /// контекст, в котором ссылка осмысленна.
    /// </remarks>
    public static IReadOnlyList<IModelObject>? ReadBaseObjects(IModelContainer container, int index)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return null;
            }

            if (fillet.BaseObjects is not object[] raw)
            {
                return null;
            }

            var inputs = new List<IModelObject>(raw.Length);
            foreach (var element in raw)
            {
                if (element is IModelObject modelObject)
                {
                    inputs.Add(modelObject);
                }
            }

            return inputs;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Входы КАЖДОГО живого скругления — по списку на признак, в порядке коллекции
    /// <c>IModelContainer.Fillets</c>. Нужны там, где признак надо ОПОЗНАТЬ по составу, а не
    /// прочитать по индексу: уменьшать набор можно только у того скругления, которое само удерживает
    /// запрошенные входы.
    /// </summary>
    /// <remarks>
    /// «Не прочиталось» отличимо от «входов нет»: не читающееся скругление даёт в списке
    /// <c>null</c>, а удерживающее ноль входов — пустой список. Смешать их значило бы принять
    /// «не прочитано» за «признак пуст» и записать набор не туда.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<IModelObject>?> ReadBaseObjectsAll(IModelContainer container)
    {
        int count;
        try
        {
            count = container.Fillets.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Array.Empty<IReadOnlyList<IModelObject>?>();
        }

        var all = new List<IReadOnlyList<IModelObject>?>(count);
        for (var i = 0; i < count; i++)
        {
            all.Add(ReadBaseObjects(container, i));
        }

        return all;
    }

    /// <summary>
    /// Устойчивые ссылки входов — <c>IModelObject.Reference</c>. Служат мерой СОСТАВА признака:
    /// не зависят от порядка выдачи коллекции и перечитываются после мутации.
    /// </summary>
    /// <remarks>
    /// <b>Чего эти ссылки не делают.</b> Здесь стояло утверждение, что они «не связывают вход
    /// признака с ребром конечного тела: измерено (H2.7), что у входов ссылки 1073742065–2067, а у
    /// уцелевшего углового ребра тела — 1073742080, это разные контексты». <b>Утверждение
    /// опровергнуто замером 17.09.2026 и снято.</b> Полосы ссылок СОСЕДНИЕ: в решающем контроле
    /// <c>FL10x</c> перенесённое ребро тела получило <c>1073742309</c> при входе признака
    /// <c>1073742308</c>; тип у обоих <c>ksObjectEdge</c>, обе ссылки устойчивы к повторному
    /// чтению, различаются лишь адресные привязки RCW. Это обычная двойственность API5/API7 —
    /// <c>ksEntity</c> тела и <c>IModelObject</c> признака суть два разных COM-объекта про одно
    /// ребро, — а не непроходимая граница. Различие <c>…065-67</c> против <c>…080</c> было
    /// разностью ЗНАЧЕНИЙ, и вывод о контекстах из него не следовал. Отсюда и снятие запрета в
    /// <c>UpdateFilletEdgeSetByBodyEdges</c>: предъявлять рёбра тела можно, и <c>FL10x</c> это
    /// подтверждает. Ссылки по-прежнему употребляются для сравнения состава признака с самим собой,
    /// но это вопрос удобства, а не запрета.
    /// </remarks>
    public static IReadOnlyList<int> ReferencesOf(IReadOnlyList<IModelObject> inputs)
    {
        var refs = new List<int>(inputs.Count);
        foreach (var input in inputs)
        {
            try
            {
                refs.Add(input.Reference);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Элемент без читаемой ссылки пропускается: это ответ, а не падение.
            }
        }

        return refs;
    }

    /// <summary>
    /// Записать НОВЫЙ набор входов в существующее скругление через <c>IFillet.BaseObjects</c> —
    /// маршрут, которым правится набор рёбер, потому что через определение API5 он не применяется.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Как найден и чем доказан.</b> Проба H-2 (<c>docs/acceptance/api7/fillet-base-objects.md</c>,
    /// 14 PASS / 0 FAIL, четыре прогона подряд). Решающий контроль — замена при НЕИЗМЕННОМ размере
    /// набора (H2.4): состав сместился <c>(50,40) → (50,-40)</c>, а объём остался буквально прежним
    /// <c>79980.6858347058</c>. Сценарию «пересчитай то, чем ты обязан быть» двигать нечего, поэтому
    /// объём здесь не различает ничего — различает именно состав. Адресация проверена на модели с
    /// ДВУМЯ скруглениями одного радиуса (H2.7): правка <c>Fillets[1]</c> не задела свидетель
    /// <c>Fillets[0]</c>.
    /// </para>
    /// <para>
    /// <b>Почему не <c>Clear()</c> и не поиск рёбер тела.</b> Набор заменяется ЦЕЛИКОМ одной
    /// присваиванием: предварительное опустошение подменило бы предмет опыта, а рёбра конечного тела
    /// для углов скругления в топологии отсутствуют (0 из 4 — углы заняты цилиндрическими гранями).
    /// Предъявляются объекты, прочитанные ИЗ <c>BaseObjects</c> того же признака.
    /// </para>
    /// <para>
    /// <b>Порядок.</b> Запись → <c>IFillet.Update()</c> → перестроение модели вызывающим. Без
    /// <c>Update()</c> сеттер возвращает успех, а модель остаётся прежней — тот же контракт, что у
    /// радиуса (F.10, проба E).
    /// </para>
    /// <para>
    /// Возвращается пара (записано, причина), а не исключение: вызывающий обязан отличить отказ
    /// КОМПАСа от падения адаптера, и «не смогли» не должно выглядеть как «записали».
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteBaseObjects(
        IModelContainer container,
        int index,
        IReadOnlyList<IModelObject> inputs)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return (false, $"элемент коллекции Fillets[{index}] не отдаёт IFillet");
            }

            if (inputs.Count == 0)
            {
                return (false, "новый набор входов пуст: пустой набор — это удаление признака, а не правка набора");
            }

            // Присваивается массив тех же элементов, что признак отдал: подмена типа (например,
            // ksEntity) потеряла бы контекст ссылки и дала бы отказ, неотличимый от отказа КОМПАСа.
            var payload = new object[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                payload[i] = inputs[i];
            }

            fillet.BaseObjects = payload;

            if (!fillet.Update())
            {
                return (false, "IFillet.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? Read(Func<bool> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>
/// Булевы операции над телами (SM-15). Маршрут измерен 18.09.2026 пробой <c>--boolean</c>
/// (прогон <c>b10ffb70b7d24b6497417bc6639581b1</c>, PASS 13 · FAIL 0).
/// </summary>
/// <remarks>
/// Возвращается пара (признак, причина), а не исключение: вызывающий обязан отличить отказ КОМПАСа
/// от падения адаптера. Отказ здесь — обычный исход: ядро отвергает несвязные тела, касание по
/// ребру и касание в точке (измерено, шаг BO.7), и «не смогли построить» не должно выглядеть как
/// «построили».
/// </remarks>
internal static class Api7SolidBoolean
{
    /// <summary>
    /// Вид операции для API7. Значения берутся из <c>Kompas6Constants.ksBooleanType</c>, а не из
    /// каталога: каталог называл объединение нулём и ошибался, а перечисление даёт
    /// <c>ksIntersect = 1</c>, <c>ksDifference = 2</c>, <c>ksUnion = 3</c>.
    /// </summary>
    public static ksBooleanType ToKs(BooleanOperation operation) => operation switch
    {
        BooleanOperation.Union => ksBooleanType.ksUnion,
        BooleanOperation.Difference => ksBooleanType.ksDifference,
        BooleanOperation.Intersect => ksBooleanType.ksIntersect,
        _ => ksBooleanType.ksBooleanUnknown,
    };

    public static (IBoolean? Feature, string? Failure) TryCreate(
        IModelContainer container,
        IKompasAPIObject target7,
        object[] tools7,
        BooleanOperation operation,
        bool keepTools,
        string? name)
    {
        try
        {
            if (container.Booleans.Add() is not IBoolean boolean)
            {
                return (null, "Booleans.Add() не отдаёт IBoolean");
            }

            if (name is not null)
            {
                boolean.Name = name;
            }

            boolean.BaseObject = target7;
            boolean.ModifyObjects = tools7;
            boolean.BooleanType = ToKs(operation);

            // Копия цели не поддерживается: это отдельный режим SM-15.union.mode_save_base_copy с
            // приоритетом next, вне обязательного объёма выпуска. Поле выставляется явно, чтобы
            // поведение не зависело от значения по умолчанию.
            boolean.SaveCopyBaseObject = false;
            boolean.SaveCopyModifyObjects = keepTools;

            var updated = boolean.Update();
            return updated
                ? (boolean, null)
                : (null, "IBoolean.Update() вернул false — ядро отвергло операцию");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Число признаков объединения в коллекции. null — не прочитано (не «ноль»).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Booleans.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Правка ВИДА существующей булевой операции: перезапись <c>IBoolean.BooleanType</c> и
    /// <c>Update()</c>. Опорные тела (<c>BaseObject</c>, <c>ModifyObjects</c>) и политика сохранения
    /// инструментов НЕ перезаписываются — этот маршрут не измерялся.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> пробой <c>--boolean</c>, шаг <c>BO.11</c>, прогон
    /// <c>a2f5cf0a2ad342c59c36807101a65d51</c> (журнал <c>docs/acceptance/api7/boolean-ops.json</c>).
    /// Эталон §6.1: <c>A ∪ B</c> даёт 36 000 в габарите <c>(0,0,0)…(60,30,20)</c>, <c>A − B</c> —
    /// 12 000 в <c>x ≤ 20</c>, <c>A ∩ B</c> — 12 000 в <c>x ∈ [20,40]</c>.
    /// </para>
    /// <para>
    /// <b>Опыты и их исходы.</b> E-A: объединение → разность на ТОМ ЖЕ признаке даёт 36 000 → 12 000
    /// в габарите <c>x ≤ 20</c>, признаков <c>1 → 1</c>. E-C: разность → пересечение даёт 12 000 →
    /// 12 000, но габарит меняется на <c>x ∈ [20,40]</c> — это различающий контроль, без него прибор,
    /// сверяющий только объём, не отличил бы правку от полного бездействия. E-D: пересечение →
    /// объединение даёт 12 000 → 36 000.
    /// </para>
    /// <para>
    /// <b>E-E, контроль маршрута: <c>Update()</c> в паре обязателен.</b> Запись <c>ksDifference</c> БЕЗ
    /// вызова <c>Update()</c>, но С пересборкой оставляет геометрию прежней (36 000); та же запись С
    /// <c>Update()</c> даёт 12 000. То есть применяет именно пара «перезапись члена → <c>Update()</c>»,
    /// и <c>Update()</c> здесь не украшение.
    /// </para>
    /// <para>
    /// <b>E-B, измеренный смысл значения <c>ksBooleanUnknown</c>.</b> Запись <c>ksBooleanUnknown</c>
    /// переводит признак в объединение, и член читается обратно как <c>ksUnion</c> — сеттер
    /// НОРМАЛИЗУЕТ неизвестное значение в объединение. Клиент, записавший <c>0</c>, получит
    /// объединение, а не отказ. Первая редакция шага объявляла здесь контроль «неиспользуемое
    /// значение геометрию не меняет», и это ожидание было опровергнуто прогоном
    /// <c>f70c555f22394ad1a050eaf96c83dd04</c>; ослаблять утверждение под наблюдённый результат
    /// запрещено, поэтому опыт переименован в измерение, а роль контроля передана E-E.
    /// </para>
    /// <para>
    /// <b>Чего этот маршрут не делает.</b> Смена ОПЕРАНДОВ существующей булевой операции не
    /// измерялась: <c>BaseObject</c> и <c>ModifyObjects</c> здесь не перезаписываются, хотя и
    /// доступны на запись. Правка набора инструментов — отдельный опыт, которого пока нет.
    /// </para>
    /// </remarks>
    public static (bool Written, string? Failure) TryWriteOperation(
        IModelContainer container,
        int index,
        BooleanOperation operation)
    {
        try
        {
            if (container.Booleans[index] is not IBoolean boolean)
            {
                return (false, $"Booleans[{index}] не отдаёт IBoolean");
            }

            boolean.BooleanType = ToKs(operation);
            return boolean.Update()
                ? (true, null)
                : (false, "IBoolean.Update() вернул false — ядро отвергло правку вида операции");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IndexOutOfRangeException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>Вид операции, прочитанный с существующего признака. null — не прочитан.</summary>
    public static BooleanOperation? ReadOperation(IModelContainer container, int index)
    {
        try
        {
            if (container.Booleans[index] is not IBoolean boolean)
            {
                return null;
            }

            return boolean.BooleanType switch
            {
                ksBooleanType.ksUnion => BooleanOperation.Union,
                ksBooleanType.ksDifference => BooleanOperation.Difference,
                ksBooleanType.ksIntersect => BooleanOperation.Intersect,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Вспомогательные плоскости API7 (SM-16). Маршрут измерен 18.09.2026 пробой <c>--split</c>,
/// шаг SP.1: <c>Planes3D.Add(o3d_plane3Points)</c> → <c>IPlane3DBy3Points</c> с тремя точками МОДЕЛИ.
/// </summary>
/// <remarks>
/// Нормаль построенной плоскости равна <c>(P2−P1)×(P3−P1)</c> — это измерено на перестановке двух
/// точек: она инвертирует нормаль, не мешая построению (шаг SP.5). Поэтому три точки строятся из
/// ортонормированного базиса заданной нормали, а не «примерно вокруг точки»: знак стороны — это
/// выбор стороны, а не мелочь.
/// </remarks>
internal static class Api7SolidPlane
{
    public static (IPlane3D? Plane, double[]? UnitNormal, string? Failure) TryCreateByPointNormal(
        IModelContainer container,
        IReadOnlyList<double> pointMm,
        IReadOnlyList<double> normalMm,
        string? name)
    {
        double[] unit;
        (double[] P1, double[] P2, double[] P3) points;
        try
        {
            var three = PlaneBasis.ThreePoints(pointMm, normalMm);
            points = (three.P1, three.P2, three.P3);
            unit = three.UnitNormal;
        }
        catch (ArgumentException ex)
        {
            return (null, null, ex.Message);
        }

        try
        {
            if (container is not IAuxiliaryGeomContainer auxiliary || auxiliary.Planes3D is not { } planes)
            {
                return (null, null, "контейнер модели не отвечает IAuxiliaryGeomContainer.Planes3D");
            }

            var first = MakePoint(container, points.P1);
            var second = MakePoint(container, points.P2);
            var third = MakePoint(container, points.P3);
            if (first is null || second is null || third is null)
            {
                return (null, null, "точки плоскости не созданы (Points3D недоступны)");
            }

            if (planes.Add(ksObj3dTypeEnum.o3d_plane3Points) is not IPlane3D plane)
            {
                return (null, null, "Planes3D.Add(o3d_plane3Points) не отдал IPlane3D");
            }

            if (plane is not IPlane3DBy3Points byPoints)
            {
                return (null, null, "созданная плоскость не отвечает IPlane3DBy3Points");
            }

            if (name is not null)
            {
                plane.Name = name;
            }

            byPoints.Point1 = first;
            byPoints.Point2 = second;
            byPoints.Point3 = third;

            var updated = plane.Update();
            return updated
                ? (plane, unit, null)
                : (null, unit, "IPlane3D.Update() вернул false — плоскость не построена");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, unit, Describe(ex));
        }
    }

    /// <summary>Перенести в API7 существующую плоскость документа, адресованную ссылкой.</summary>
    public static (IPlane3D? Plane, string? Failure) TryTransfer(object source, Api7Bridge bridge)
    {
        try
        {
            if (bridge.TransferTo7(source) is not IPlane3D plane)
            {
                return (null, "объект по ссылке не отвечает IPlane3D");
            }

            return (plane, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    private static IPoint3D? MakePoint(IModelContainer container, double[] coordinates)
    {
        try
        {
            if (container.Points3D is not { } points || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = coordinates[0];
            point.Y = coordinates[1];
            point.Z = coordinates[2];
            point.Update();
            return point;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Разделение тела плоскостью (SM-16). Маршрут измерен 18.09.2026 пробой <c>--split</c>
/// (прогон <c>124682af57a242728ea765f1aae4816c</c>, PASS 11 · FAIL 0), шаг SP.2.
/// </summary>
/// <remarks>
/// У <c>ISplitSolid</c> содержательный член ОДИН — <c>CutObjects</c>. Отдельного «набора
/// сохраняемых частей» не существует, и он не нужен: разделение сохраняет все части по построению
/// (измерено: брусок 24 000 → тела 6 000 и 18 000, сумма 24 000). Именно это измерение сняло
/// блокировку OQ-A18, а не найденный член.
/// </remarks>
internal static class Api7SolidSplit
{
    public static (ISplitSolid? Feature, string? Failure) TryCreate(
        IModelContainer container,
        object[] cutObjects,
        string? name)
    {
        try
        {
            if (container.SplitSolids.Add() is not ISplitSolid split)
            {
                return (null, "SplitSolids.Add() не отдаёт ISplitSolid");
            }

            if (name is not null)
            {
                split.Name = name;
            }

            split.CutObjects = cutObjects;
            var updated = split.Update();
            return updated
                ? (split, null)
                : (null, "ISplitSolid.Update() вернул false — ядро отвергло разделение");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.SplitSolids.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Переписать опору СУЩЕСТВУЮЩЕГО признака разделения — маршрут правки (действие <c>edit</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> пробой <c>--split</c>, шаг SP.9 (прогон
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>), и измерен ВМЕСТЕ с отрицательным контролем, потому
    /// что первый, «очевидный» маршрут оказался неверным:
    /// </para>
    /// <list type="bullet">
    /// <item><b>E-A — работает:</b> опора читается обратно с существующего признака
    /// (<c>CutObjects</c> отдаёт один объект), отвечает <c>IPlane3DBy3Points</c>, и перенос её ТРЁХ
    /// ТОЧЕК ПОСТРОЕНИЯ на +5 по X с <c>Update()</c> каждой и перестроением даёт части 9000 и 15000
    /// при неизменном числе признаков разделения <c>1 → 1</c>;</item>
    /// <item><b>E-B — НЕ работает (отрицательный контроль):</b> запись ВТОРОЙ, только что созданной
    /// плоскости (<c>x = 20</c>) в <c>CutObjects</c> того же признака с <c>Update() = true</c> и
    /// перестроением результат НЕ меняет — части остались 9000 и 15000. То есть <c>Update() = true</c>
    /// здесь означает «принято», а не «применено»: признак продолжает резать по СВОЕЙ прежней
    /// опоре.</item>
    /// </list>
    /// <para>
    /// Поэтому правка опоры — это перенос точек САМОЙ опоры, а не подстановка другой плоскости.
    /// Подстановка остаётся рабочей только при СОЗДАНИИ признака (шаг SP.2) и в правке не
    /// используется. Ссылка на объект плоскости не сохраняется между вызовами (ревизия документа
    /// инвалидирует ссылки), поэтому опора каждый раз читается с ЖИВОЙ модели.
    /// </para>
    /// </remarks>
    public static (bool Moved, string? Failure) TryMoveSupport(
        IModelContainer container,
        int index,
        (double[] P1, double[] P2, double[] P3) points)
    {
        try
        {
            if (container.SplitSolids[index] is not ISplitSolid split)
            {
                return (false, $"элемент коллекции SplitSolids[{index}] не отдаёт ISplitSolid");
            }

            if (Api7PlaneSupport.FirstOf(split.CutObjects) is not IPlane3D support)
            {
                return (false, "опора признака разделения не отвечает IPlane3D: правка опоры идёт "
                    + "переносом точек её построения, а у этого объекта их нет");
            }

            return Api7PlaneSupport.MovePoints(support, points);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Опоры признаков SM-16 при ПРАВКЕ: чтение опоры с живого признака и перенос её точек построения.
/// </summary>
/// <remarks>
/// Вынесено отдельно, потому что оба семейства — разделение и отсечение — правятся ОДНИМ маршрутом
/// (шаг SP.9: E-A для разделения, E-C для отсечения), и второй экземпляр тех же правил разошёлся бы
/// с первым при первой же правке. Здесь же лежит объяснение, почему маршрут именно такой.
/// </remarks>
internal static class Api7PlaneSupport
{
    /// <summary>
    /// Первый элемент прочитанного набора <c>CutObjects</c>. Интероп отдаёт его то массивом, то одним
    /// объектом, и предполагать одно из двух значило бы гадать о типе: измерено (шаг SP.9), что при
    /// одной опоре приходит один объект, а не массив.
    /// </summary>
    public static object? FirstOf(object? readBack) => readBack switch
    {
        Array array when array.Length > 0 => array.GetValue(0),
        Array => null,
        null => null,
        _ => readBack,
    };

    /// <summary>
    /// Перенести три точки построения опоры в заданные координаты, затем перестроить опору.
    /// </summary>
    /// <remarks>
    /// Точки адресуются ПО ПОРЯДКУ (<c>Point1..Point3</c>), потому что порядок и задаёт нормаль
    /// (<c>(p2−p1)×(p3−p1)</c>) — измерено шагом SP.5: перестановка двух точек нормаль инвертирует.
    /// Поэтому перенос идёт в те же три точки, а не «в ближайшие».
    /// </remarks>
    public static (bool Moved, string? Failure) MovePoints(
        IPlane3D support,
        (double[] P1, double[] P2, double[] P3) points)
    {
        if (support is not IPlane3DBy3Points byPoints)
        {
            return (false, "опора не отвечает IPlane3DBy3Points: правка идёт переносом трёх точек "
                + "построения, а у этой опоры их нет");
        }

        var wanted = new[] { points.P1, points.P2, points.P3 };
        var actual = new[] { byPoints.Point1, byPoints.Point2, byPoints.Point3 };
        for (var i = 0; i < wanted.Length; i++)
        {
            if (wanted[i].Length != 3)
            {
                return (false, $"точка построения #{i + 1} задана {wanted[i].Length} числами вместо трёх");
            }

            if (actual[i] is not IPoint3D point)
            {
                return (false, $"точка построения опоры #{i + 1} не отвечает IPoint3D");
            }

            point.X = wanted[i][0];
            point.Y = wanted[i][1];
            point.Z = wanted[i][2];
            point.Update();
        }

        return support.Update()
            ? (true, null)
            : (false, "IPlane3D.Update() вернул false — опора не перестроена");
    }
}

/// <summary>
/// Отсечение тела по одну сторону плоскости (SM-16). Маршрут измерен 18.09.2026 пробой
/// <c>--split</c>, шаг SP.7.
/// </summary>
/// <remarks>
/// Соответствие знака измерено: при нормали <c>(1,0,0)</c> и плоскости <c>x = 10</c>
/// <c>Direction = true</c> оставляет сторону <b>в направлении нормали</b> (<c>s &gt; 0</c>,
/// V = 18 000), <c>false</c> — противоположную (<c>s &lt; 0</c>, V = 6 000). Поэтому «оставить
/// положительную сторону» и «Direction = true» — одно и то же, и это единственное место, где
/// знак превращается в параметр.
/// </remarks>
internal static class Api7SolidCut
{
    /// <summary>
    /// Создать признак отсечения по плоскости, направив его на ВЫБРАННОЕ тело.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Область применения — не украшение.</b> Установленная справка v24
    /// (<c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>) называет умолчание прямо: «По умолчанию
    /// область применения операции Сечение — Все объекты», и для плоского секущего объекта в неё
    /// входит и то, что плоскость пересекает, и то, что ЦЕЛИКОМ лежит со стороны отсечения. Поэтому
    /// вызов без задания области применения снимает материал у посторонних тел, и это поведение
    /// продукта, а не сбой.
    /// </para>
    /// <para>
    /// <b>Маршрут измерен 19.09.2026</b> пробой <c>--cut-area</c> (прогон
    /// <c>9e7599ce6e2448bb9ef983326eb16439</c>, 10 PASS · 0 FAIL), отчёт
    /// <c>docs/acceptance/api7/cut-area.json</c>, шаги CA.1–CA.7:
    /// <list type="bullet">
    /// <item><b>CA.1</b> — установленная библиотека типов <c>Bin\kAPI7.tlb</c> объявляет у
    /// <c>ICut</c> ровно четыре члена области применения: <c>ChooseType</c> (dispid 3),
    /// <c>ChoosePartsType</c> (4), <c>ChooseBodies</c> (5), <c>ChooseParts</c> (6);</item>
    /// <item><b>CA.2</b> — живой признак отвечает всеми четырьмя; до записи он сообщает
    /// <c>ChooseType=ksChBodiesAndParts</c>, <c>ChoosePartsType=ksChAutomaticDefinition</c>;</item>
    /// <item><b>CA.3</b> — без задания области применения постороннее тело исчезает (A=12000,
    /// S исчезло) — клиентский дефект воспроизведён на пробе;</item>
    /// <item><b>CA.4</b> — принятая форма значения: <c>ChooseType = ksChBodies</c>,
    /// <c>ChoosePartsType = ksChManualEditing</c>, <c>ChooseBodies = object[] { перенесённое в API7
    /// тело }</c>. Результат адресный: A=12000, S=1000;</item>
    /// <item><b>CA.5</b> — отрицательный контроль: та же форма с ДРУГИМ телом даёт ДРУГОЙ результат
    /// (A=18000 нетронуто, S=500), то есть форма действительно адресует, а не «принимается молча»;</item>
    /// <item><b>CA.6</b> — правка опоры адресность сохраняет (A=6000, S=1000);</item>
    /// <item><b>CA.7</b> — после <c>save → close → open</c> адресность сохраняется, и область
    /// читается с переоткрытого файла как <c>ChooseType=ksChBodies</c> с непустым
    /// <c>ChooseBodies</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Проверка ДО мутации.</b> Значения читаются обратно ДО <c>Update()</c>: если продукт не
    /// принял область применения, признак не создаётся вовсе. Создать признак с незапрошенной
    /// областью — значит снять материал у посторонних тел, и КОМПАС об этом не сообщит.
    /// </para>
    /// </remarks>
    public static (ICut? Feature, string? Failure) TryCreateByPlane(
        IModelContainer container,
        IPlane3D plane,
        bool keepPositiveSide,
        string? name,
        IKompasAPIObject? targetBody)
    {
        try
        {
            if (container.Cuts.Add() is not ICut cut)
            {
                return (null, "Cuts.Add() не отдаёт ICut");
            }

            if (name is not null)
            {
                cut.Name = name;
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = plane;
            cut.Direction = keepPositiveSide;

            if (targetBody is not null)
            {
                cut.ChooseType = ksChooseType.ksChBodies;
                cut.ChoosePartsType = ksChoosePartsType.ksChManualEditing;
                cut.ChooseBodies = new[] { targetBody };

                var readBack = ReadBodyChoice(cut);
                if (readBack.Failure is not null)
                {
                    return (null, "область применения не подтверждена до Update(): " + readBack.Failure);
                }
            }

            var updated = cut.Update();
            return updated
                ? (cut, null)
                : (null, "ICut.Update() вернул false — ядро отвергло отсечение");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>
    /// Прочитать обратно область применения живого признака — ДО мутации. Отсутствие тела в списке
    /// и отказ члена — разные ответы, и оба отличаются от «прочитано и совпало».
    /// </summary>
    public static (string? Type, int? Bodies, string? Failure) ReadBodyChoice(ICut cut)
    {
        try
        {
            var type = cut.ChooseType;
            var bodies = cut.ChooseBodies switch
            {
                null => 0,
                Array array => array.Length,
                _ => 1,
            };

            if (type != ksChooseType.ksChBodies)
            {
                return (type.ToString(), bodies,
                    $"продукт прочитал ChooseType={type}, а запрошено {ksChooseType.ksChBodies}");
            }

            return (type.ToString(), bodies, bodies < 1
                ? "список выбранных тел пуст: адресность не подтверждена"
                : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Cuts.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Переписать опору и сторону СУЩЕСТВУЮЩЕГО признака отсечения — маршрут правки (действие
    /// <c>edit</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> пробой <c>--split</c>, шаг SP.9 (прогон
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>), двумя отдельными опытами:
    /// </para>
    /// <list type="bullet">
    /// <item><b>E-C — опора:</b> <c>ICut.CutObject</c> читается обратно, отвечает
    /// <c>IPlane3DBy3Points</c>, и перенос его трёх точек на +5 по X (<c>x = 10 → 15</c>) с
    /// перестроением меняет остаток с 6000 на 9000;</item>
    /// <item><b>E-D — сторона:</b> смена ТОЛЬКО <c>Direction</c> на том же существующем признаке
    /// меняет остаток с 9000 на 15000. То есть сторону править можно, а опору — только переносом
    /// точек её построения: подстановка другой плоскости не работает (отрицательный контроль E-B на
    /// разделении, тот же объект и тот же маршрут).</item>
    /// </list>
    /// <para>
    /// Порядок «опора, затем сторона, затем <c>Update()</c>» взят у измеренных опытов: E-C менял
    /// опору без записи стороны, E-D — сторону без переноса опоры. Общая правка делает обе записи
    /// подряд; это и проверяется приёмкой, а не предполагается.
    /// </para>
    /// <para>
    /// <c>BuildingType</c> и <c>CutObject</c> НЕ переписываются: маршрут стороны измерен без них, а
    /// запись способа построения в существующий признак — отдельный опыт, которого не было. Признак,
    /// опора которого не отвечает <c>IPlane3DBy3Points</c>, отвергается, а не правится наугад.
    /// </para>
    /// <para>
    /// <b>Область применения при правке — измерено 19.09.2026</b> пробой <c>--cut-area</c>, шаг CA.6
    /// (прогон <c>9e7599ce6e2448bb9ef983326eb16439</c>): перенос опоры на существующем признаке
    /// адресность СОХРАНЯЕТ (A=6000, S=1000), а шаг CA.7 — что она переживает и
    /// <c>save → close → open</c>. Поэтому <paramref name="targetBody"/> здесь НЕ обязателен: без него
    /// область применения читается обратно ДО <c>Update()</c> и незаадресованный признак отвергается;
    /// с ним — переназначается тем же маршрутом, что и на создании. Переназначение без чтения обратно
    /// не делается: продукт принимает <c>ChooseBodies</c> молча и в форме, которая ничего не адресует
    /// (отрицательный контроль CA.5 отличается от положительного только телом).
    /// </para>
    /// </remarks>
    public static (bool Updated, string? Failure) TryMoveSupportAndSide(
        IModelContainer container,
        int index,
        (double[] P1, double[] P2, double[] P3) points,
        bool keepPositiveSide,
        IKompasAPIObject? targetBody = null)
    {
        try
        {
            if (container.Cuts[index] is not ICut cut)
            {
                return (false, $"элемент коллекции Cuts[{index}] не отдаёт ICut");
            }

            if (cut.CutObject is not IPlane3D support)
            {
                return (false, "опора признака отсечения не отвечает IPlane3D: правка опоры идёт "
                    + "переносом точек её построения, а у этого объекта их нет");
            }

            if (targetBody is not null)
            {
                cut.ChooseType = ksChooseType.ksChBodies;
                cut.ChoosePartsType = ksChoosePartsType.ksChManualEditing;
                cut.ChooseBodies = new[] { targetBody };

                var assigned = ReadBodyChoice(cut);
                if (assigned.Failure is not null)
                {
                    return (false, "область применения не подтверждена до Update(): " + assigned.Failure);
                }
            }
            else
            {
                var current = ReadBodyChoice(cut);
                if (current.Failure is not null)
                {
                    return (false, "признак не адресован: правка опоры сняла бы материал у посторонних "
                        + "тел, потому что умолчание области применения — «Все объекты» (справка "
                        + "rezultat_oper_v_zavisimosti_ot_s_o.html). Чтение области применения до "
                        + "мутации: " + current.Failure
                        + ". Передайте target_body_ref, чтобы назначить область явно.");
                }
            }

            var moved = Api7PlaneSupport.MovePoints(support, points);
            if (!moved.Moved)
            {
                return moved;
            }

            cut.Direction = keepPositiveSide;
            return cut.Update()
                ? (true, null)
                : (false, "ICut.Update() вернул false — ядро отвергло правку отсечения");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Перенос и поворот тела (SM-17). Маршрут измерен 18.09.2026 пробой <c>--reposition</c>
/// (прогон <c>929f08886f1348fe921943052a4026b0</c>, PASS 10 · FAIL 0).
/// </summary>
/// <remarks>
/// <para>
/// Положение пишет <b>только</b> <c>Position.InitByMatrix3D</c> с однородной матрицей 4×4 (OQ-A19).
/// Маршруты из 12 чисел и <c>SetDisplacementByAxis</c> принимаются с <c>Update() = true</c> и тело
/// не двигают — поэтому <c>Update() = true</c> здесь НЕ является доказательством, и адаптер обязан
/// проверять положение после вызова, а не доверять возвращённому значению.
/// </para>
/// <para>
/// Отрицательный контроль встроен: <c>Update() = false</c> без цели и без положения (шаг RP.1) —
/// это то, что отличает «операция выполнена» от «операция принята молча».
/// </para>
/// </remarks>
/// <summary>
/// Признак изменения положения: запись и чтение размещения ДОКУМЕНТИРОВАННЫМ ПАРАМЕТРИЧЕСКИМ
/// маршрутом — ориентация углами Эйлера, перенос смещением.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему матричный маршрут заменён, а не дополнен.</b> Прежняя редакция писала размещение
/// однородной матрицей 4×4 (<c>ILocalCoordinateSystem.InitByMatrix3D</c>) и на этом же основании
/// пыталась его читать. Измерено (проба <c>--reposition-params</c>, шаги RP.14–RP.23): документ
/// хранит не матрицу, а ПАРАМЕТРЫ ориентации, поэтому матричный вид переоткрытие НЕ переживает —
/// у записанного поворота на 90° вокруг Z после сохранения, закрытия и открытия
/// <c>GetVector(OX)</c> отдаёт <c>(1,0,0)</c> при габарите <c>(−5,−5,0)…(5,15,5)</c>, который
/// поворот подтверждает (RP.16), <c>WriteToFile</c> отдаёт единичную матрицу (RP.18), а верное
/// чтение живёт ровно до следующего открытия (RP.20). Отличить «записано» от «не восстановлено»
/// НЕЧЕМ: <c>Valid</c> читается <c>True</c> в обоих состояниях, а документированная
/// последовательность <c>Update()</c> и сборки чтение не восстанавливает (RP.23).
/// </para>
/// <para>
/// <b>Замена измерена целиком, а не выбрана по удобству</b> (проба <c>--reposition-params</c>,
/// прогон <c>a336120926fc4652a8bf737562568271</c>, шаг RP.25). Документированный параметрический
/// маршрут переоткрытие ПЕРЕЖИВАЕТ и в ориентации, и в переносе:
/// <c>Position.OrientationType = ksEulerCorners</c> + <c>LocalCSParameters →
/// ILocalCSEulerParam.PrecessionAngle/NutationAngle/RotationAngle</c> — тройка углов читается с
/// переоткрытого документа ДО сборки и ДО всякой записи (<c>angles_kept_D1 = true</c>,
/// <c>angles_kept_D2 = true</c>); <c>Position.ParameterType = ksPDisplace</c> +
/// <c>Parameters → IPoint3DParamDisplace.DX/DY/DZ</c> — перенос читается там же и различает две
/// постановки различающей пары (<c>displacement_after_D1 = (7,−11,13)</c>,
/// <c>displacement_after_D2 = (1,2,3)</c>), тогда как отрицательный контроль D0, у которого
/// смещение НЕ записывалось, даёт <c>ParameterType = 1 (ksPParamCoord)</c> и <c>(?,?,?)</c> —
/// то есть чтение различает, а не отдаёт постоянное.
/// </para>
/// <para>
/// <b>Порядок членов обязателен и измерен, а не выбран по вкусу:</b> сначала
/// <c>OrientationType</c>, потом интерфейс параметров ЭТОГО режима у <c>LocalCSParameters</c>;
/// затем <c>ParameterType</c>, потом интерфейс ЭТОГО типа у <c>Parameters</c>
/// (<c>ilocalcoordinatesystem_localcsparameters.html</c>, <c>ilocalcoordinatesystem_parametertype.html</c>).
/// Обратный порядок даёт объект ЧУЖОГО режима, и запись в него молча не применяется.
/// </para>
/// <para>
/// <b>Единицы — ГРАДУСЫ</b> (измерено: угол 30 дал поворот на 30°, <c>angle_deg_for_30 =
/// 29.99999999999998</c>), порядок спряжения тройки — <c>PNR</c> (измерен по совпадению со всеми
/// шестью произведениями; у остальных пяти расхождение 1). Разложение и сборка тройки живут в
/// <see cref="EulerOrientation"/> — единственном месте, где записан этот порядок.
/// </para>
/// <para>
/// <b>Успешный <c>Update()</c> доказательством не является</b> (шаг RP.2: три маршрута из четырёх
/// вернули <c>true</c> и тело не двинули), поэтому и запись, и чтение обязаны подтверждаться
/// отдельно: геометрией — в вызывающем коде, параметрами — <see cref="ReadPlacement"/>.
/// </para>
/// </remarks>
internal static class Api7SolidReposition
{
    /// <summary>
    /// Параметрическое представление размещения, прочитанное из документа КАК ЕСТЬ: без подстановок,
    /// без вывода из матрицы и без значений по умолчанию.
    /// </summary>
    /// <param name="OrientationType">Режим ориентации, прочитанный из документа (не предположенный).</param>
    /// <param name="ParameterType">Тип параметров точки, прочитанный из документа.</param>
    /// <param name="AnglesDeg">Тройка <c>(прецессия, нутация, вращение)</c> либо <c>null</c>, если интерфейс режима не подтверждён.</param>
    /// <param name="DisplacementMm">Смещение <c>(DX, DY, DZ)</c> либо <c>null</c>, если параметры точки не подтверждают интерфейс смещения.</param>
    internal sealed record PlacementReading(
        int OrientationType,
        int ParameterType,
        double[]? AnglesDeg,
        double[]? DisplacementMm)
    {
        /// <summary>
        /// Признак записан документированным режимом углов Эйлера. Ложь означает, что размещение
        /// писалось ЧУЖИМ маршрутом (матрицей) и параметрического представления у него нет.
        /// </summary>
        public bool IsEuler => OrientationType == (int)ksOrientationTypeEnum.ksEulerCorners;

        /// <summary>Перенос записан документированным смещением, а не координатами точки.</summary>
        public bool HasDisplacement => ParameterType == (int)ksPoint3DTypeEnum.ksPDisplace;
    }

    public static (IBodyReposition? Feature, string? Failure) TryCreate(
        IModelContainer container,
        IKompasAPIObject body7,
        double[] matrix16,
        string? name)
    {
        if (matrix16.Length != RepositionMatrix.Size)
        {
            return (null, $"матрица положения обязана содержать {RepositionMatrix.Size} чисел, а содержит {matrix16.Length}");
        }

        try
        {
            if (container.BodyRepositions.Add() is not IBodyReposition reposition)
            {
                return (null, "BodyRepositions.Add() не отдаёт IBodyReposition");
            }

            if (name is not null)
            {
                reposition.Name = name;
            }

            reposition.RepositionBody = body7;

            var failure = WritePlacement(reposition.Position, matrix16);
            if (failure is not null)
            {
                return (null, failure);
            }

            var updated = reposition.Update();
            return updated
                ? (reposition, null)
                : (null, "IBodyReposition.Update() вернул false — ядро отвергло преобразование");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.BodyRepositions.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Записать положение в СУЩЕСТВУЮЩИЙ признак — маршрут правки. Измерено (шаг RP.6): повторная
    /// запись того же вектора оставляет положение прежним, а возврат вектора в ноль возвращает тело
    /// домой, то есть параметр применяется к ИСХОДНЫМ входам, а не к текущему положению.
    /// </summary>
    public static (bool Written, string? Failure) TryWrite(
        IModelContainer container,
        int index,
        double[] matrix16)
    {
        try
        {
            if (container.BodyRepositions[index] is not IBodyReposition reposition)
            {
                return (false, $"элемент коллекции BodyRepositions[{index}] не отдаёт IBodyReposition");
            }

            var failure = WritePlacement(reposition.Position, matrix16);
            if (failure is not null)
            {
                return (false, failure);
            }

            var updated = reposition.Update();
            return updated ? (true, null) : (false, "IBodyReposition.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>
    /// Записать размещение параметрическим маршрутом: ориентацию — тройкой углов Эйлера, перенос —
    /// документированным смещением. <c>null</c> — записано; иначе причина отказа.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Матрица раскладывается, а не подменяется: поворот берётся из <c>matrix[0…10]</c> через
    /// <see cref="EulerOrientation.AnglesFromRotation"/>, перенос — из <c>matrix[12…14]</c>, то есть
    /// ровно оттуда, куда его кладут <see cref="RepositionMatrix.Translate"/> и
    /// <see cref="RepositionMatrix.RotateAboutAxis"/> (<c>t = c − R·c</c>). Второй раскладки матрицы
    /// здесь не заводится намеренно.
    /// </para>
    /// <para>
    /// <b>Перенос пишется ВСЕГДА, даже нулевой.</b> У поворота вокруг оси через начало координат
    /// <c>t = 0</c>, и пропуск записи оставил бы у признака ЧУЖОЙ тип параметров точки
    /// (<c>ksPParamCoord</c>), то есть прочитанное значение зависело бы от того, писал ли кто-то
    /// перенос, — а это уже не чтение модели, а чтение истории сеанса.
    /// </para>
    /// </remarks>
    private static string? WritePlacement(ILocalCoordinateSystem position, double[] matrix16)
    {
        position.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
        if (position.LocalCSParameters is not ILocalCSEulerParam euler)
        {
            return "LocalCSParameters не подтверждает ILocalCSEulerParam при "
                + "OrientationType = ksEulerCorners — документированный режим углов недостижим";
        }

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(matrix16);
        euler.PrecessionAngle = precession;
        euler.NutationAngle = nutation;
        euler.RotationAngle = rotation;

        position.ParameterType = ksPoint3DTypeEnum.ksPDisplace;
        if (position.Parameters is not IPoint3DParamDisplace displace)
        {
            return "Parameters не подтверждает IPoint3DParamDisplace при "
                + "ParameterType = ksPDisplace — документированный маршрут смещения недостижим";
        }

        displace.DX = matrix16[12];
        displace.DY = matrix16[13];
        displace.DZ = matrix16[14];
        return null;
    }

    /// <summary>
    /// Прочитать размещение из признака ПАРАМЕТРИЧЕСКИМ маршрутом — как есть, без записи.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ничего не записывается перед чтением, и это требование, а не стиль.</b> Измерено (RP.20):
    /// повторная запись возвращает верное чтение матричного вида на один шаг, поэтому запись перед
    /// чтением маскирует дефект. Здесь не вызывается ни один сеттер: только <c>OrientationType</c>,
    /// <c>LocalCSParameters</c>, <c>ParameterType</c> и <c>Parameters</c> на чтение.
    /// </para>
    /// <para>
    /// <b>Интерфейсы берутся БЕЗ приведения режима.</b> <c>LocalCSParameters</c> отдаёт параметры
    /// ТЕКУЩЕГО режима, и на переоткрытом документе режим уже прочитан из файла: у признака,
    /// записанного продуктом, это <c>ILocalCSEulerParam</c>, у признака, записанного матрицей, —
    /// интерфейс ДРУГОГО режима. Подставлять режим присваиванием здесь значило бы создавать
    /// параметрическое представление там, где его нет, и выдавать его за прочитанное.
    /// </para>
    /// </remarks>
    public static (PlacementReading? Reading, string? Failure) ReadPlacement(
        IModelContainer container,
        int index)
    {
        try
        {
            if (container.BodyRepositions[index] is not IBodyReposition reposition)
            {
                return (null, $"элемент коллекции BodyRepositions[{index}] не отдаёт IBodyReposition");
            }

            return ReadPlacement(reposition);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>То же для уже полученного признака — маршрут создания читает его до сборки.</summary>
    public static (PlacementReading? Reading, string? Failure) ReadPlacement(IBodyReposition reposition)
    {
        try
        {
            var position = reposition.Position;
            var orientationType = (int)position.OrientationType;
            var parameterType = (int)position.ParameterType;

            double[]? angles = position.LocalCSParameters is ILocalCSEulerParam euler
                ? new[] { euler.PrecessionAngle, euler.NutationAngle, euler.RotationAngle }
                : null;
            double[]? displacement = position.Parameters is IPoint3DParamDisplace displace
                ? new[] { displace.DX, displace.DY, displace.DZ }
                : null;

            return (new PlacementReading(orientationType, parameterType, angles, displacement), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>
/// Чтение элемента по сечениям из коллекции API7 — того самого маршрута, которым признак и создан.
/// Ни одна величина не берётся из ответа создания: коллекция читается заново.
/// </summary>
/// <remarks>
/// Измерено 20.09.2026 (проба <c>--b5</c>, шаг B5.12): <c>IModelContainer.Lofts</c> отвечает
/// <c>KompasAPI7.LoftsClass</c>, <c>Count</c> = 1 после создания одного признака, элемент по индексу
/// отдаёт <c>KompasAPI7.LoftClass</c>, с которого читаются <c>Sketchs</c> (2 элемента),
/// <c>Closed</c> (False), <c>CouplingsCount</c> (0) и <c>BuildingType(true)</c> (0 = <c>ksLoftAuto</c>).
/// </remarks>
internal static class Api7Loft
{
    /// <summary>Число элементов по сечениям. <c>null</c> — «не прочитано», а не ноль.</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Lofts?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Элемент по индексу. <c>null</c> — «не прочитано», а не «параметров нет».</summary>
    public static ILoft? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Lofts[index] as ILoft;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Число сечений в признаке. <c>null</c> — «не прочитано».</summary>
    public static int? SectionCount(ILoft loft)
    {
        try
        {
            return loft.Sketchs is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Цепочки соответствия сечений — <c>CouplingsCount</c>. <c>null</c> — «не прочитано».</summary>
    public static int? CouplingsCount(ILoft loft)
    {
        try
        {
            return loft.CouplingsCount;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Замкнута ли траектория соединения — <c>ILoft.Closed</c>. <c>null</c> — «не прочитано».</summary>
    public static bool? Closed(ILoft loft)
    {
        try
        {
            return loft.Closed;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Способ построения у крайнего сечения — <c>ILoft.BuildingType(BeginSection)</c>. Свойство
    /// С ИНДЕКСОМ: в C#-поверхности interop индекс объявлен <c>bool</c>, тогда как отражение по
    /// <c>get_BuildingType</c> находит <c>short</c> — две разные половины одного члена, и обе
    /// измерены (B5.9, B5.11). Какой индекс какому концу соответствует, измеряется, а не выводится
    /// из имени.
    /// </summary>
    public static int? BuildingType(ILoft loft, bool beginSection)
    {
        try
        {
            return Convert.ToInt32(loft.BuildingType[beginSection]);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }
}

/// <summary>
/// Чтение оболочки из коллекции API7 — вторая половина той же постановки, что и чтение с
/// определения API5. Нужна затем, чтобы «прочитано» подтверждалось двумя независимыми маршрутами.
/// </summary>
/// <remarks>
/// Измерено 20.09.2026 (шаг B5.12): <c>IModelContainer.Shells</c> отвечает
/// <c>KompasAPI7.ShellsClass</c>, <c>Count</c> = 1, элемент отдаёт <c>IShell</c> с
/// <c>Thickness</c> = 2, <c>ThinType</c> = <c>dt_reverse</c> (это «внутрь», подтверждено объёмом
/// 21632) и <c>DeletedFaces</c> = 1. Те же три величины, прочитанные с API5-определения:
/// <c>thickness</c> = 2, <c>thinType</c> = true, <c>FaceArray</c> = 1.
/// </remarks>
internal static class Api7Shell
{
    /// <summary>Число оболочек. <c>null</c> — «не прочитано».</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Shells?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Оболочка по индексу. <c>null</c> — «не прочитано».</summary>
    public static IShell? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Shells[index] as IShell;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Толщина стенки. <c>null</c> — «не прочитано».
    /// </summary>
    public static double? Thickness(IShell shell)
    {
        try
        {
            return shell.Thickness;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Направление толщины. В COM это <c>c_long</c>, в interop объявлено
    /// <c>ksDirectionTypeEnum</c> — типизация обёртки, а не факт о продукте. Значение отдаётся
    /// ЧИСЛОМ, чтобы не зависеть от имени перечисления.
    /// </summary>
    public static int? ThinType(IShell shell)
    {
        try
        {
            return Convert.ToInt32(shell.ThinType);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Число снятых граней — <c>DeletedFaces</c>. <c>null</c> — «не прочитано».</summary>
    public static int? DeletedFaceCount(IShell shell)
    {
        try
        {
            return shell.DeletedFaces is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}

/// <summary>
/// Чтение кинематической операции из коллекции API7. Нужно ради ОДНОГО члена, которого в API5 нет
/// вовсе: <c>IEvolution.OperationResult</c> — документированного ответа о виде операции
/// (<c>ksOperationNewBody</c> и т. д.).
/// </summary>
/// <remarks>
/// Измерено 20.09.2026 (проба <c>--b5</c>, шаг B5.12): <c>IModelContainer.Evolutions</c> отвечает
/// <c>KompasAPI7.EvolutionsClass</c>, <c>Count</c> = 1 после создания одного признака, а
/// <c>OperationResult</c> прочитан значением <b>1</b> (<c>ksOperationNewBody</c>) — то есть новое
/// тело, как и объявлено обязанной строкой <c>SM-04.base.single_profile_flat_path</c>.
/// </remarks>
internal static class Api7Evolution
{
    /// <summary>Число кинематических операций. <c>null</c> — «не прочитано», а не ноль.</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Evolutions?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Элемент по индексу. <c>null</c> — «не прочитано».</summary>
    public static IEvolution? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Evolutions[index] as IEvolution;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>IEvolution.OperationResult</c>. <c>null</c> — «не прочитано», а не ноль: ноль здесь значил
    /// бы конкретный вид операции, которого никто не измерял.
    /// </summary>
    public static int? OperationResult(IModelContainer container, int index)
    {
        try
        {
            return Read(container, index) is { } evolution
                ? Convert.ToInt32(evolution.OperationResult)
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}
