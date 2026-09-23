using System.Text.Json.Serialization;

namespace KompasMcp.Contracts;

public enum SketchEntityKind
{
    Line,
    Circle,
    Arc,
    Rectangle,
    Polyline,
}

/// <summary>
/// Sketch primitive in sketch-local millimetres (spec 2.6). Each variant uses only the fields
/// its <see cref="SketchEntityKind"/> needs; the validator rejects the rest so a circle cannot
/// quietly carry an ignored <see cref="EndMm"/>.
/// </summary>
public sealed record SketchEntityDto
{
    public required SketchEntityKind Kind { get; init; }

    /// <summary>line/polyline vertex/rectangle origin.</summary>
    public IReadOnlyList<double>? StartMm { get; init; }

    /// <summary>line end point.</summary>
    public IReadOnlyList<double>? EndMm { get; init; }

    /// <summary>circle/arc centre, rectangle corner.</summary>
    public IReadOnlyList<double>? CenterMm { get; init; }

    public double? RadiusMm { get; init; }

    /// <summary>Arc start angle, degrees, CCW from +X of the sketch frame.</summary>
    public double? StartDeg { get; init; }

    /// <summary>
    /// Arc sweep in degrees. Negative means clockwise — the sign is meaningful and must be
    /// preserved by the adapter (spec 2.6).
    /// </summary>
    public double? SweepDeg { get; init; }

    /// <summary>rectangle width along sketch +X, mm.</summary>
    public double? WidthMm { get; init; }

    /// <summary>rectangle height along sketch +Y, mm.</summary>
    public double? HeightMm { get; init; }

    /// <summary>polyline vertices, mm.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? PointsMm { get; init; }

    public bool? Closed { get; init; }
}

/// <summary>Which named plane, or an offset plane / planar face reference.</summary>
public sealed record PlaneRefDto
{
    public PlaneBase? Base { get; init; }

    /// <summary>Reference to an existing plane/face. Mutually exclusive with <see cref="Base"/>.</summary>
    public string? Reference { get; init; }

    /// <summary>Offset along the base plane normal, mm (sign convention is adapter-verified, G07).</summary>
    public double OffsetMm { get; init; }
}

public enum PlaneBase
{
    Xy,
    Xz,
    Yz,
}

public enum SketchEditMode
{
    Append,
    Replace,
    DeleteEntities,
}

public enum ExtrudeOperation
{
    /// <summary>First solid of an empty body list.</summary>
    Base,

    /// <summary>Add material to an existing body.</summary>
    Boss,

    /// <summary>Remove material from an existing body.</summary>
    Cut,
}

public enum ExtrudeDirection
{
    Positive,
    Negative,
    Symmetric,
}

/// <summary>
/// How an extrusion ends. Measured on v24 (probe P2.1): <see cref="Blind"/> honours depth_mm for
/// every directionType, while <see cref="Through"/> (vendor etThroughAll) is only honoured when the
/// extrusion runs symmetric — with directionType 0 nothing is cut at all and with 1 every end
/// condition collapses to the same result. Through therefore takes no depth: 1 mm and 1000 mm cut
/// identically through a plate, and up-to-near-surface is a different condition, not a big number.
/// </summary>
public enum ExtrudeEndCondition
{
    /// <summary>На заданную глубину (vendor etBlind). Значение по умолчанию.</summary>
    Blind,

    /// <summary>Только вырезание: насквозь через весь материал (vendor etThroughAll).</summary>
    Through,
}

/// <summary>
/// Способ построения фаски (docs/05 SM-11). Способ обязан быть назван вызывающим, потому что
/// <see cref="DistanceAngle"/> физически недоступен в API5: угла нет ни в
/// <c>ksChamferDefinition</c>, ни в <c>SetChamferParam(transfer, d1, d2)</c> — измерено пробой F
/// на v24 (12.09.2026).
/// </summary>
public enum ChamferMode
{
    /// <summary>Двумя катетами; маршрут API5 (<c>NewEntity(o3d_chamfer=33)</c> + <c>SetChamferParam</c>).</summary>
    TwoDistances,

    /// <summary>Расстоянием и углом; маршрут API7 (<c>IChamfer.Angle</c>). Угол — в градусах (F.10).</summary>
    DistanceAngle,
}

/// <summary>
/// Режим родного отверстия (docs/05 SM-07). Все четыре измерены пробой M на v24 и живут только в
/// API7 того же сеанса: у API5 отверстие есть (<c>o3d_hole=52</c>), но параметров режима в его
/// определении нет.
/// </summary>
public enum HoleMode
{
    /// <summary>Глухое с плоским дном: <c>ksDTValue</c> + <c>ksEFFlat</c>; снимается π·r²·h (M.4).</summary>
    BlindFlat,

    /// <summary>Сквозная цековка: пилот насквозь плюс кольцевая выточка (M.2).</summary>
    ThroughCounterbore,

    /// <summary>Сквозная зенковка: пилот насквозь плюс коническая фаска (M.3).</summary>
    ThroughCountersink,
}

/// <summary>
/// Вид операции вращения. Именно ЭТО значение решает действие, а не
/// <c>IRotated1.OperationResult</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Измерено 17.09.2026 (шаг R.26, прогон <c>95fa8441</c>).</b> На подготовленной плите
/// 120×120×40 <c>o3d_bossRotated</c> с записанным И прочитанным обратно
/// <c>OperationResult = ksOperationCut</c> изменил объём на <b>0</b>, тогда как та же операция,
/// созданная как <c>Add(o3d_cutRotated)</c>, сняла ровно 25132.7412287183 мм³. То есть
/// <c>OperationResult</c> совершает круговой рейс и НИ НА ЧТО не влияет — это метаданные.
/// Поэтому вид операции задаётся ВЫБОРОМ ВЫЗОВА фабрики, и клиент не может «переключить» уже
/// открытую операцию: он обязан назвать вид заранее.
/// </para>
/// <para>
/// Отсюда же — отказ в <c>kompas_update_feature</c> на попытку сменить вид вращения: смена вида
/// означала бы удаление признака и создание нового, а это не правка на месте.
/// </para>
/// </remarks>
public enum RotationOperation
{
    /// <summary>Первое тело: <c>o3d_baseRotated</c> (27).</summary>
    Base,

    /// <summary>Приклейка к существующему телу: <c>o3d_bossRotated</c> (28).</summary>
    Boss,

    /// <summary>Вырезание: <c>o3d_cutRotated</c> (29).</summary>
    Cut,
}

/// <summary>
/// Направление вращения — <c>ksDirectionTypeEnum</c>, как измерено (R.26.sector).
/// </summary>
/// <remarks>
/// <para>
/// <b>Все четыре значения измерены на полуобороте, и одно из них не строит ничего.</b>
/// <list type="bullet">
/// <item><c>Normal</c> (dtNormal=0) — материал по обе стороны оси (габарит x[−20,20]);</item>
/// <item><c>Both</c> (dtBoth=2) — тоже по обе стороны; именно это значение использовал поставляемый
/// файл <c>BEARING 410</c> для настоящего частичного вращения (R.22);</item>
/// <item><c>MiddlePlane</c> (dtMiddlePlane=3) — односторонний (x[0,20]) и ЕДИНСТВЕННЫЙ, у которого
/// смена направления двигает сектор;</item>
/// <item><c>Reverse</c> (dtReverse=1) — <b>не строит ничего</b>: <c>Update()</c> возвращает False,
/// тел 0. Это факт о значении, а не о вращении, и сервер обязан его отвергать до мутации, а не
/// сообщать «построено» по коду возврата.</item>
/// </list>
/// </para>
/// <para>
/// Объём при смене направления на полуобороте НЕ различается: полуцилиндр одинаков с обеих сторон,
/// и габарит тоже симметричен. Поэтому «сектор переехал» доказывается только стороной материала, и
/// вызывающий, желающий это проверить, обязан сравнить габарит, а не объём.
/// </para>
/// </remarks>
public enum RotationDirection
{
    /// <summary>dtNormal=0 — обе стороны оси.</summary>
    Normal,

    /// <summary>dtReverse=1 — измерено: НЕ строит ничего. Сервер отвергает до мутации.</summary>
    Reverse,

    /// <summary>dtBoth=2 — обе стороны; значение из поставляемого BEARING 410.</summary>
    Both,

    /// <summary>dtMiddlePlane=3 — односторонне, и единственное, что двигает сектор.</summary>
    MiddlePlane,
}

/// <summary>Thread specification (spec 2.6). Structural description, not a modelled helix unless asked.</summary>
public sealed record ThreadSpecDto
{
    public required string Kind { get; init; }

    public required double NominalDiameterMm { get; init; }

    public required double PitchMm { get; init; }

    public required string Handedness { get; init; }

    /// <summary>cosmetic | native | nominal_envelope — the caller must state which one is real.</summary>
    public required string Representation { get; init; }
}

/// <summary>Structural selection predicate (spec 2.5). Deliberately not free text.</summary>
public sealed record SelectionPredicateDto
{
    public string? SurfaceType { get; init; }

    /// <summary>Unit direction in <see cref="CoordinateSpace"/> the normal must match.</summary>
    public IReadOnlyList<double>? NormalDirection { get; init; }

    public double? NormalAngleToleranceDeg { get; init; }

    public CoordinateSpace? CoordinateSpace { get; init; }

    public string? ExtremumAxis { get; init; }

    public string? ExtremumMode { get; init; }

    public IReadOnlyList<double>? AreaRangeMm2 { get; init; }

    public IReadOnlyList<double>? BboxRangeMm { get; init; }
}

/// <summary>Properties a caller asks <c>kompas_measure</c> to return (spec 2.5).</summary>
public enum MeasurableProperty
{
    Bbox,
    Volume,
    SurfaceArea,
    Mass,
    Centroid,
}

/// <summary>Result of measuring one target.</summary>
public sealed record MeasurementDto
{
    public BoundingBoxDto? Bbox { get; init; }

    /// <summary>mm³. Null when not requested or not derivable.</summary>
    public double? VolumeMm3 { get; init; }

    public double? SurfaceAreaMm2 { get; init; }

    /// <summary>kg. Only when a density is actually known — a nullable beats an invented density (spec 1.10).</summary>
    public double? MassKg { get; init; }

    public IReadOnlyList<double>? CentroidMm { get; init; }

    /// <summary>Aspect the server could NOT confirm in the unit system, e.g. "volume_units".</summary>
    public IReadOnlyList<string> UnverifiedAspects { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Тип движения сечения по траектории кинематической операции — <c>ksEvolutionShiftSketchTypeEnum</c>.
/// </summary>
/// <remarks>
/// <para>
/// Числа прочитаны со страницы официальной справки SDK v24 <c>ksevolutionshiftsketchtypeenum.html</c>
/// (открыта по проводу 20.09.2026), а не выведены по аналогии: <c>ksEvShiftParallel = 0</c> —
/// «образующая переносится параллельно самой себе»; <c>ksEvShiftKeepAngle = 1</c> — «образующая при
/// переносе сохраняет исходный угол с направляющей»; <c>ksEvShiftOrtogonal = 2</c> — «плоскость
/// образующей выставляется и сохраняется ортогональной направляющей» (орфография вендора сохранена).
/// </para>
/// <para>
/// <b>На ПРЯМОЙ траектории <see cref="Parallel"/> и <see cref="Orthogonal"/> неразличимы.</b>
/// Измерено 20.09.2026 (проба <c>--b5</c>, шаги B5.1/B5.2): на дуге R50/90° ортогональный режим дал
/// <c>24674.011002723353</c> при ожидании <c>S × L = 24674.011002723397</c>, а параллельный —
/// <c>15707.963267948984</c>, то есть отличие <c>8966.047734774369</c> мм³. Поэтому строка приёмки
/// режима ортогональности обязана стоять на ДУГЕ, а не на отрезке.
/// </para>
/// </remarks>
public enum SweepShiftMode
{
    /// <summary>ksEvShiftParallel = 0 — перенос параллельно самому себе.</summary>
    Parallel,

    /// <summary>ksEvShiftKeepAngle = 1 — сохраняет исходный угол с направляющей.</summary>
    KeepAngle,

    /// <summary>ksEvShiftOrtogonal = 2 — ортогонально направляющей.</summary>
    Orthogonal,
}

/// <summary>
/// Способы построения элемента по сечениям у крайних сечений — <c>ksLoftBuildingType</c>.
/// </summary>
/// <remarks>
/// Числа прочитаны со страницы официальной справки SDK v24 <c>ksloftbuildingtype.html</c> (по проводу
/// 20.09.2026): <c>ksLoftAuto = 0</c>, <c>ksLoftByNormal = 1</c>, <c>ksLoftByObject = 2</c>,
/// <c>ksLoftCupola = 3</c>.
/// </remarks>
public enum LoftBuilding
{
    /// <summary>ksLoftAuto = 0 — автоматически.</summary>
    Auto,

    /// <summary>ksLoftByNormal = 1 — по нормали.</summary>
    ByNormal,

    /// <summary>ksLoftByObject = 2 — по объекту.</summary>
    ByObject,

    /// <summary>ksLoftCupola = 3 — купол.</summary>
    Cupola,
}

/// <summary>
/// Направление формирования тонкой стенки оболочки. Соответствие ЗНАЧЕНИЙ измерено, а не выведено.
/// </summary>
/// <remarks>
/// <para>
/// <b>Измерено 20.09.2026</b> (проба <c>--b5</c>, шаг B5.5; короб 100×80×10 с удалённой верхней
/// гранью, t = 2): <c>thinType = true</c> даёт <c>21631.999999999996</c> мм³ — это <b>внутрь</b>
/// (полость 96·76·8); <c>thinType = false</c> даёт <c>24832.000000000022</c> — это <b>наружу</b>
/// (104·84·12 − 80000). Отличие <c>3200.0000000000255</c> мм³.
/// </para>
/// <para>
/// Первая редакция пробы ждала ОБРАТНОГО соответствия и получила измеренное; под сомнение было
/// поставлено ожидание, а не измерение. Типы половин разные: API7 <c>IShell.ThinType</c> объявлен
/// как <c>long</c>, API5 <c>ksShellDefinition.thinType</c> — как <c>bool</c>.
/// </para>
/// </remarks>
public enum ShellThinDirection
{
    /// <summary>thinType = true — материал внутрь, полость по внешнему контуру. Измерено: 21632 при t = 2.</summary>
    Inward,

    /// <summary>thinType = false — материал наружу. Измерено: 24832 при t = 2.</summary>
    Outward,
}
