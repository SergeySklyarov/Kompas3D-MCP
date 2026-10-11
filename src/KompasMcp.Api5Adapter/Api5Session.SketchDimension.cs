using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>A DRIVING DIMENSION on a sketch in FOUR kinds — <c>linear</c>, <c>diametral</c>,
/// <c>radial</c> and <c>angular</c> (<c>kompas_create_sketch_dimension</c>; block G6).</summary>
/// <remarks>DOC: each kind has its own collection on <c>ISymbols2DContainer</c> → <c>Add()</c> → the
/// dimension interface → <c>IDrawingObject::Update()</c> (<c>idrawingobject_update.html</c>).
/// MEASURED: the LINEAR kind needs <c>ksCAssociation</c>; the other three REFUSE it and are bound by the
/// base object the help names. INVARIANT: confirmation is by FACT — the value and the measured geometry
/// re-read as requested — never by the generic pair, which is NOT weakened elsewhere.
/// History: docs/decisions/adapter-sketch.md#sketch-dimension-kinds</remarks>
public sealed partial class Api5Session
{
    /// <summary>The route, named once so the response and the acceptance evidence cannot drift
    /// apart.</summary>
    private const string SketchDimensionRoute =
        "ISketch.BeginEdit() → IView(QI ISymbols2DContainer).LineDimensions.Add() → ILineDimension " +
        "(точки — концы измеряемого отрезка) → Update() → QI(IDrawingObject1) → ksCAssociation " +
        "(партнёр — массив отрезков) → ksCFixedDim → ksCDimWithVariable(значение) → перестроение";

    /// <summary>How far the LINEAR dimension line is drawn from the segment when the client does not
    /// say — the value the tool used before <c>offset_mm</c> became optional.</summary>
    private const double DefaultDimensionOffsetMm = 10d;

    /// <summary>Creates a driving dimension of the kind asked for. <c>linear</c> is the DEFAULT and
    /// keeps the route it had; the three other kinds go through their own collection.</summary>
    public SketchDimensionCreateResult CreateSketchDimension(CreateSketchDimensionCommand command)
    {
        if (!SketchDimensionRule.TryResolve(command.Kind, out var kind, out var kindProblem))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                kindProblem!,
                details: new Dictionary<string, object?> { ["kind"] = command.Kind });
        }

        var foreign = SketchDimensionRule.ForeignFields(
            kind,
            command.SecondObjectRef is not null,
            command.ValueMm is not null,
            command.ValueDeg is not null,
            command.OffsetMm is not null);
        if (foreign.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Для вида '{SketchDimensionRule.Name(kind)}' поля {string.Join(", ", foreign)} не "
                + "принимаются. Поле чужого вида отвергается, а не игнорируется: иначе клиент считал бы "
                + "заданным то, чего сервер не задавал.",
                details: new Dictionary<string, object?>
                {
                    ["kind"] = SketchDimensionRule.Name(kind),
                    ["foreign_fields"] = foreign,
                });
        }

        return kind == SketchDimensionKind.Linear
            ? CreateLinearDimension(command)
            : CreateKindDimension(command, kind);
    }

    // ═══════════════════════════════════════════════════════ linear (the previous behaviour) ══

    /// <summary>Creates a driving linear dimension on one segment of an existing sketch.</summary>
    private SketchDimensionCreateResult CreateLinearDimension(CreateSketchDimensionCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        if (command.ObjectRef is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Размер ставится НА ОТРЕЗОК: поле object_ref обязательно, и «на первый попавшийся» "
                + "отрезок размером не является.",
                details: new Dictionary<string, object?> { ["missing_field"] = "object_ref" });
        }

        var valueMm = command.ValueMm ?? double.NaN;
        if (!double.IsFinite(valueMm) || valueMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"value_mm обязано быть положительным конечным числом, получено " +
                $"{Api5Number(command.ValueMm)}. Размер нулевой длины не выражает измерения.",
                details: new Dictionary<string, object?> { ["value_mm"] = command.ValueMm });
        }

        if (transferOrThrow(bridge, target.Sketch) is not { } sketch7)
        {
            throw new KompasContractException(ErrorCodes.CapabilityUnavailable, "Эскиз не перенесён в API7.",
                RetryPolicy.ReacquireContext);
        }

        var notes = new List<string>();

        // ONE session for the whole placement: the dimension object and the constraints that go on it
        // live in one edit session, and a NESTED BeginEdit() answers null — MEASURED, and it is the
        // defect that made the first version of this tool refuse with «вход в эскиз не состоялся».
        KompasAPI7.IFragmentDocument? fragment = null;
        try
        {
            fragment = sketch7.BeginEdit();
        }
        catch (COMException ex)
        {
            notes.Add($"BeginEdit отказал: HRESULT 0x{ex.HResult:X8}");
        }

        DimensionOutcome? outcome = null;
        if (fragment is null)
        {
            notes.Add("ISketch.BeginEdit() → null: вход в эскиз не состоялся");
        }
        else
        {
            try
            {
                var (objects, enumerateFailure) = Api7SketchConstraints.EnumerateInSession(fragment, notes);
                if (enumerateFailure is not null)
                {
                    notes.Add(enumerateFailure);
                }
                else
                {
                    var segmentHost = ResolveObject(objects, command.ObjectRef!, "object_ref", notes);
                    var segment = segmentHost as KompasAPI7.ILineSegment;
                    if (segment is null)
                    {
                        throw new KompasContractException(
                            ErrorCodes.InvalidArgument,
                            $"Объект по адресу '{command.ObjectRef}' не является отрезком: размер длины "
                            + "ставится на отрезок, и другой объект им не подменяется.",
                            details: new Dictionary<string, object?>
                            {
                                ["object_ref"] = command.ObjectRef,
                            });
                    }

                    double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                    double? lengthBefore = null;
                    try
                    {
                        x1 = segment.X1;
                        y1 = segment.Y1;
                        x2 = segment.X2;
                        y2 = segment.Y2;
                        lengthBefore = segment.Length;
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        throw new KompasContractException(
                            ErrorCodes.GeometryFailed,
                            "Концы измеряемого отрезка не прочитаны: " + ex.GetType().Name +
                            ". Размер строится ПО концам отрезка, поэтому без них он не ставится.",
                            RetryPolicy.ReacquireContext);
                    }

                    notes.Add($"измеряемый отрезок: ({x1:0.######},{y1:0.######})—" +
                              $"({x2:0.######},{y2:0.######}), длина {lengthBefore:0.######}");

                    outcome = PlaceDimension(fragment, segment, segmentHost, x1, y1, x2, y2, lengthBefore,
                        command with { ValueMm = valueMm }, notes);
                }
            }
            finally
            {
                try
                {
                    sketch7.EndEdit();
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    notes.Add("EndEdit(): " + ex.GetType().Name);
                }
            }
        }

        if (outcome is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сеанс правки эскиза не открылся: " + (notes.LastOrDefault() ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["notes"] = notes.ToArray() });
        }

        if (outcome.Failure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Размер не создан: " + outcome.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["object_ref"] = command.ObjectRef,
                    ["value_mm"] = valueMm,
                    ["notes"] = notes.ToArray(),
                });
        }

        BumpRevision(target.Document, "sketch.dimension.create");

        // The length is read AFTER the rebuild, through a fresh enumeration: the value is expected to
        // have moved the geometry, and that is the fact the caller is buying.
        var lengthAfter = ReadSegmentLength(sketch7, command.ObjectRef!, notes);
        var applied = outcome.PointsMatchSegment == true
                      && outcome.ValueRead is { } read && Math.Abs(read - valueMm) < 1e-6
                      && lengthAfter is { } after && Math.Abs(after - valueMm) < 1e-6;

        if (!applied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Размер создан, но ТРИ ФАКТА не совпали: точки размера на концах отрезка = " +
                $"{outcome.PointsMatchSegment}, значение размера перечитано как " +
                $"{Api5Number(outcome.ValueRead)} (запрошено {Api5Number(valueMm)}), длина " +
                $"отрезка после перестроения = {Api5Number(lengthAfter)}. Применением объявляется " +
                "только совпадение всех трёх.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["points_match_segment"] = outcome.PointsMatchSegment,
                    ["value_read"] = outcome.ValueRead,
                    ["value_requested"] = valueMm,
                    ["length_after"] = lengthAfter,
                    ["notes"] = notes.ToArray(),
                });
        }

        return new SketchDimensionCreateResult(
            ObjectRef: command.ObjectRef!,
            DimensionRef: outcome.DimensionRef ?? string.Empty,
            ValueMm: valueMm,
            Applied: true,
            LengthBeforeMm: outcome.LengthBefore,
            LengthAfterMm: lengthAfter,
            DimensionPointsCount: outcome.DimensionPointsCount,
            PointsMatchSegment: outcome.PointsMatchSegment,
            AssociationCreated: outcome.AssociationCreated,
            FixedDimCreated: outcome.FixedDimCreated,
            ValueDimCreated: outcome.ValueDimCreated,
            AssociationAsMergePoints: outcome.AssociationAsMergePoints,
            Notes: notes,
            Route: SketchDimensionRoute,
            Kind: SketchDimensionKinds.Linear,
            ValueUnit: "mm",
            GeometryName: "length_mm",
            GeometryBefore: outcome.LengthBefore,
            GeometryAfter: lengthAfter,
            ValueRead: outcome.ValueRead,
            DimensionVariable: outcome.DimensionVariable);
    }

    // ══════════════════════════════════════ diametral / radial / angular (the three new kinds) ══

    /// <summary>Places a dimension of a non-linear kind: its own collection, its own base object, then
    /// <c>ksCFixedDim</c> and <c>ksCDimWithVariable</c> — MEASURED: the value drives the geometry, and
    /// <c>ksCAssociation</c> is refused for these kinds, so it is not asked for.</summary>
    private SketchDimensionCreateResult CreateKindDimension(
        CreateSketchDimensionCommand command,
        SketchDimensionKind kind)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);
        var kindName = SketchDimensionRule.Name(kind);
        var value = kind == SketchDimensionKind.Angular ? command.ValueDeg : command.ValueMm;

        if (command.ObjectRef is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Для вида '{kindName}' поле object_ref обязательно: размер ставится на названный "
                + $"объект эскиза ({SketchDimensionRule.RequiredShapeText(kind)}).",
                details: new Dictionary<string, object?> { ["missing_field"] = "object_ref" });
        }

        if (SketchDimensionRule.NeedsSecondObject(kind) && command.SecondObjectRef is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Вид '{kindName}' измеряется между ДВУМЯ отрезками: поле second_object_ref "
                + "обязательно.",
                details: new Dictionary<string, object?> { ["missing_field"] = "second_object_ref" });
        }

        if (value is not { } wanted || !double.IsFinite(wanted) || wanted <= 0d
            || (kind == SketchDimensionKind.Angular && wanted >= 360d))
        {
            var field = kind == SketchDimensionKind.Angular ? "value_deg" : "value_mm";
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"{field} обязано быть положительным конечным числом"
                + (kind == SketchDimensionKind.Angular ? " меньше 360" : string.Empty)
                + $", получено {Api5Number(value)}. {SketchDimensionRule.ValueMeaning(kind)}.",
                details: new Dictionary<string, object?> { [field] = value });
        }

        if (transferOrThrow(bridge, target.Sketch) is not { } sketch7)
        {
            throw new KompasContractException(ErrorCodes.CapabilityUnavailable, "Эскиз не перенесён в API7.",
                RetryPolicy.ReacquireContext);
        }

        var notes = new List<string>();
        KindOutcome? outcome = null;
        var placed = false;

        KompasAPI7.IFragmentDocument? fragment = null;
        try
        {
            fragment = sketch7.BeginEdit();
        }
        catch (COMException ex)
        {
            notes.Add($"BeginEdit отказал: HRESULT 0x{ex.HResult:X8}");
        }

        if (fragment is null)
        {
            notes.Add("ISketch.BeginEdit() → null: вход в эскиз не состоялся");
        }
        else
        {
            try
            {
                var (objects, enumerateFailure) = Api7SketchConstraints.EnumerateInSession(fragment, notes);
                if (enumerateFailure is not null)
                {
                    notes.Add(enumerateFailure);
                }
                else
                {
                    outcome = PlaceKindDimension(kind, fragment, objects, command, wanted, notes);
                    placed = outcome.Failure is null;
                }
            }
            finally
            {
                try
                {
                    sketch7.EndEdit();
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    notes.Add("EndEdit(): " + ex.GetType().Name);
                }
            }
        }

        if (!placed)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Размер вида '{kindName}' не создан: "
                + (outcome?.Failure ?? notes.LastOrDefault() ?? "причина не названа"),
                RetryPolicy.SameOperationId,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = kindName,
                    ["object_ref"] = command.ObjectRef,
                    ["second_object_ref"] = command.SecondObjectRef,
                    ["notes"] = notes.ToArray(),
                });
        }

        BumpRevision(target.Document, "sketch.dimension.create");

        var geometryAfter = ReadKindGeometry(sketch7, kind, command, notes);
        var applied = outcome!.ValueRead is { } read && Math.Abs(read - wanted) < 1e-6
                      && geometryAfter is { } after && Math.Abs(after - wanted) < 1e-6;

        if (!applied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Размер вида '{kindName}' создан, но ДВА ФАКТА не совпали: значение размера " +
                $"перечитано как {Api5Number(outcome.ValueRead)} (запрошено {Api5Number(wanted)}), " +
                $"{SketchDimensionRule.GeometryName(kind)} после перестроения = " +
                $"{Api5Number(geometryAfter)}. Применением объявляется только совпадение обоих.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = kindName,
                    ["value_read"] = outcome.ValueRead,
                    ["value_requested"] = wanted,
                    ["geometry_after"] = geometryAfter,
                    ["notes"] = notes.ToArray(),
                });
        }

        var mm = kind == SketchDimensionKind.Angular ? (double?)null : wanted;
        return new SketchDimensionCreateResult(
            ObjectRef: command.ObjectRef!,
            DimensionRef: outcome.DimensionRef ?? string.Empty,
            ValueMm: mm,
            Applied: true,
            LengthBeforeMm: kind == SketchDimensionKind.Angular ? null : outcome.GeometryBefore,
            LengthAfterMm: kind == SketchDimensionKind.Angular ? null : geometryAfter,
            DimensionPointsCount: outcome.ConstraintRows,
            PointsMatchSegment: null,
            AssociationCreated: false,
            FixedDimCreated: outcome.FixedDimCreated,
            ValueDimCreated: outcome.ValueDimCreated,
            AssociationAsMergePoints: 0,
            Notes: notes,
            Route: KindRoute(kind),
            Kind: kindName,
            SecondObjectRef: command.SecondObjectRef,
            ValueDeg: kind == SketchDimensionKind.Angular ? wanted : null,
            ValueUnit: SketchDimensionKinds.ValueUnit(kindName),
            GeometryName: SketchDimensionRule.GeometryName(kind),
            GeometryBefore: outcome.GeometryBefore,
            GeometryAfter: geometryAfter,
            ValueRead: outcome.ValueRead,
            DimensionVariable: outcome.DimensionVariable);
    }

    private static string KindRoute(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral =>
            "ISketch.BeginEdit() → IView(QI ISymbols2DContainer).DiametralDimensions.Add() → "
            + "IDiametralDimension(BaseObject — окружность) → Update() → QI(IDrawingObject1) → "
            + "ksCFixedDim → ksCDimWithVariable(диаметр) → перестроение",
        SketchDimensionKind.Radial =>
            "ISketch.BeginEdit() → IView(QI ISymbols2DContainer).RadialDimensions.Add() → "
            + "IRadialDimension(BaseObject — дуга или окружность) → Update() → QI(IDrawingObject1) → "
            + "ksCFixedDim → ksCDimWithVariable(радиус) → перестроение",
        SketchDimensionKind.Angular =>
            "ISketch.BeginEdit() → IView(QI ISymbols2DContainer).AngleDimensions.Add(ksDrADimension) → "
            + "IAngleDimension(BaseObject1/BaseObject2 — два отрезка) → Update() → "
            + "QI(IDrawingObject1) → ksCFixedDim → ksCDimWithVariable(угол) → перестроение",
        _ => SketchDimensionRoute,
    };

    /// <summary>Outcome of a non-linear placement, all of it measured inside ONE session.</summary>
    private sealed record KindOutcome(
        string? DimensionRef,
        double? ValueRead,
        double? GeometryBefore,
        bool FixedDimCreated,
        bool ValueDimCreated,
        int ConstraintRows,
        string? DimensionVariable,
        string? Failure);

    private KindOutcome PlaceKindDimension(
        SketchDimensionKind kind,
        KompasAPI7.IFragmentDocument fragment,
        IReadOnlyList<SketchConstraintObject> objects,
        CreateSketchDimensionCommand command,
        double value,
        List<string> notes)
    {
        var failed = new KindOutcome(null, null, null, false, false, 0, null, null);
        try
        {
            var view = fragment.ViewsAndLayersManager?.Views?.get_View(0);
            if (view is not KompasAPI7.ISymbols2DContainer symbols)
            {
                return failed with
                {
                    Failure = "вид эскиза не отвечает QI(ISymbols2DContainer): документированного "
                              + "маршрута создания размера нет, и соседний маршрут не подставляется",
                };
            }

            // The measured object is resolved and its KIND checked BEFORE the kernel is asked to create
            // anything: a dimension placed on the wrong object would either be refused mid-write or,
            // worse, accepted as a different measurement.
            var first = ResolveSketchObject(objects, command.ObjectRef!, "object_ref");
            var firstShape = ShapeOf(first.Drawing);
            var accepted = SketchDimensionRule.AcceptedShapes(kind);
            if (firstShape is null || !accepted.Contains(firstShape.Value))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Объект по адресу '{command.ObjectRef}' — {ShapeText(firstShape)}; размер вида "
                    + $"'{SketchDimensionRule.Name(kind)}' ставится на "
                    + $"{SketchDimensionRule.RequiredShapeText(kind)}. Вид объекта проверяется ДО "
                    + "создания размера, и другой объект им не подменяется.",
                    details: new Dictionary<string, object?>
                    {
                        ["kind"] = SketchDimensionRule.Name(kind),
                        ["object_ref"] = command.ObjectRef,
                        ["object_shape"] = ShapeText(firstShape),
                    });
            }

            SketchConstraintObject? second = null;
            if (SketchDimensionRule.NeedsSecondObject(kind))
            {
                second = ResolveSketchObject(objects, command.SecondObjectRef!, "second_object_ref");
                if (ShapeOf(second.Drawing) != SketchObjectShape.Segment)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        $"Объект по адресу '{command.SecondObjectRef}' — "
                        + $"{ShapeText(ShapeOf(second.Drawing))}; угловой размер измеряется между ДВУМЯ "
                        + "отрезками.",
                        details: new Dictionary<string, object?>
                        {
                            ["second_object_ref"] = command.SecondObjectRef,
                            ["object_shape"] = ShapeText(ShapeOf(second.Drawing)),
                        });
                }

                if (string.Equals(command.SecondObjectRef, command.ObjectRef, StringComparison.Ordinal))
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "second_object_ref совпал с object_ref: угол измеряется между РАЗНЫМИ "
                        + "отрезками, и один отрезок сам с собой угла не образует.",
                        details: new Dictionary<string, object?>
                        {
                            ["object_ref"] = command.ObjectRef,
                        });
                }
            }

            KompasAPI7.IDrawingObject? dimension;
            switch (kind)
            {
                case SketchDimensionKind.Diametral:
                {
                    var added = symbols.DiametralDimensions.Add();
                    if (added is null)
                    {
                        return failed with { Failure = "IDiametralDimensions.Add() → null" };
                    }

                    added.BaseObject = first.Drawing;
                    notes.Add($"IDiametralDimensions.Add(): BaseObject = {ShapeText(firstShape)} " +
                              $"({first.Kind}); Update() → {SafeBoolText(() => added.Update())}");
                    dimension = added;
                    break;
                }

                case SketchDimensionKind.Radial:
                {
                    var added = symbols.RadialDimensions.Add();
                    if (added is null)
                    {
                        return failed with { Failure = "IRadialDimensions.Add() → null" };
                    }

                    added.BaseObject = first.Drawing;
                    notes.Add($"IRadialDimensions.Add(): BaseObject = {ShapeText(firstShape)} " +
                              $"({first.Kind}); Update() → {SafeBoolText(() => added.Update())}");
                    dimension = added;
                    break;
                }

                case SketchDimensionKind.Angular:
                {
                    var added = symbols.AngleDimensions.Add(DrawingObjectTypeEnum.ksDrADimension);
                    if (added is null)
                    {
                        return failed with { Failure = "IAngleDimensions.Add(ksDrADimension) → null" };
                    }

                    added.BaseObject1 = first.Drawing;
                    added.BaseObject2 = second!.Drawing;
                    notes.Add($"IAngleDimensions.Add(ksDrADimension): BaseObject1 = отрезок " +
                              $"[{first.Index}], BaseObject2 = отрезок [{second!.Index}]; Update() → " +
                              $"{SafeBoolText(() => added.Update())}");
                    dimension = added;
                    break;
                }

                default:
                    return failed with { Failure = "вид не поддержан" };
            }

            var dimensionRef = SafeInt(() => dimension.Reference)?.ToString();
            if (dimension as KompasAPI7.IDrawingObject1 is not { } host)
            {
                return new KindOutcome(dimensionRef, null, null, false, false, 0, null,
                    "объект-размер не отвечает QI(IDrawingObject1): NewConstraint() недоступен");
            }

            // The documented precondition of the value (iparametriticconstraint_value.html).
            var fixedDim = Api7SketchConstraints.Create(
                host, ksConstraintTypeEnum.ksCFixedDim, null, null, null, null, null, null, null, notes);
            notes.Add($"ksCFixedDim: {Describe(fixedDim)}");

            var valueDim = Api7SketchConstraints.Create(
                host, ksConstraintTypeEnum.ksCDimWithVariable, null, null, null, null, value,
                value.ToString(System.Globalization.CultureInfo.InvariantCulture), null, notes);
            notes.Add($"ksCDimWithVariable: {Describe(valueDim)}");

            // MEASURED and named rather than left unsaid: the association is REFUSED for these kinds,
            // so it is not asked for, and the binding is the base object set above.
            notes.Add("ksCAssociation НЕ запрашивается: измерено, что у этого вида ядро её отвергает "
                      + "(Create = false), а значение двигает геометрию и без неё — связывает размер "
                      + "опорный объект, названный справкой.");

            var rows = Api7SketchConstraints.ReadRows(host, notes);
            var variable = rows.LastOrDefault(r =>
                r.TypeCode == (int)ksConstraintTypeEnum.ksCDimWithVariable)?.Variable;
            notes.Add($"записей у размера: {rows.Count}; переменная размера: " +
                      (variable ?? "не прочитана"));

            double? valueRead = null;
            if (dimension is KompasAPI7.IDimensionText text)
            {
                valueRead = SafeDouble(() => text.NominalValue);
                notes.Add($"значение размера перечитано: {Api5Number(valueRead)}");
            }
            else
            {
                notes.Add("объект-размер не отвечает QI(IDimensionText): значение не перечитано");
            }

            var geometryBefore = ReadKindGeometryInSession(kind, objects, command, notes);

            return new KindOutcome(dimensionRef, valueRead, geometryBefore, fixedDim.Created == true,
                valueDim.Created == true, rows.Count, variable, null);
        }
        catch (KompasContractException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return failed with { Failure = "размещение размера отказало: " + ex.GetType().Name };
        }
    }

    /// <summary>The measured geometry parameter of a kind, read from ALREADY enumerated objects — the
    /// form used INSIDE the placement session, where the geometry must be captured before the session
    /// closes.</summary>
    private double? ReadKindGeometryInSession(
        SketchDimensionKind kind,
        IReadOnlyList<SketchConstraintObject> objects,
        CreateSketchDimensionCommand command,
        List<string> notes)
    {
        var first = FindByAddress(objects, command.ObjectRef!);
        if (first is null)
        {
            notes.Add($"геометрия до: объект '{command.ObjectRef}' не найден среди перечисленных");
            return null;
        }

        return kind switch
        {
            SketchDimensionKind.Diametral => first.Drawing is KompasAPI7.ICircle circle
                ? SafeDouble(() => circle.Radius) * 2d
                : null,
            SketchDimensionKind.Radial => RadiusOf(first.Drawing),
            SketchDimensionKind.Angular => AngleBetween(first.Drawing,
                FindByAddress(objects, command.SecondObjectRef!)?.Drawing),
            _ => first.Drawing is KompasAPI7.ILineSegment segment ? SafeDouble(() => segment.Length) : null,
        };
    }

    /// <summary>The same measurement AFTER the rebuild, through a FRESH enumeration by address.
    /// <c>null</c> means "not read" and is named in the notes, never silently zero.</summary>
    private double? ReadKindGeometry(
        KompasAPI7.ISketch sketch,
        SketchDimensionKind kind,
        CreateSketchDimensionCommand command,
        List<string> notes)
    {
        var readNotes = new List<string>();
        var value = InReadSession(sketch, readNotes, (_, objects) =>
            ReadKindGeometryInSession(kind, objects, command, readNotes));

        notes.AddRange(readNotes);
        if (value is null)
        {
            notes.Add($"{SketchDimensionRule.GeometryName(kind)} после перестроения НЕ прочитана — это "
                      + "названная причина, а не ноль");
        }

        return value;
    }

    private static double? RadiusOf(KompasAPI7.IDrawingObject drawing) => drawing switch
    {
        KompasAPI7.IArc arc => SafeDouble(() => arc.Radius),
        KompasAPI7.ICircle circle => SafeDouble(() => circle.Radius),
        _ => null,
    };

    /// <summary>The angle between two segments, in degrees, from their OWN direction vectors — not from
    /// <c>ILineSegment.Angle</c>, whose zero direction is a separate question.</summary>
    private static double? AngleBetween(KompasAPI7.IDrawingObject? first, KompasAPI7.IDrawingObject? second)
    {
        if (first is not KompasAPI7.ILineSegment a || second is not KompasAPI7.ILineSegment b)
        {
            return null;
        }

        try
        {
            var ax = a.X2 - a.X1;
            var ay = a.Y2 - a.Y1;
            var bx = b.X2 - b.X1;
            var by = b.Y2 - b.Y1;
            var la = Math.Sqrt(ax * ax + ay * ay);
            var lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-12 || lb < 1e-12)
            {
                return null;
            }

            var dot = (ax / la) * (bx / lb) + (ay / la) * (by / lb);
            var cross = (ax / la) * (by / lb) - (ay / la) * (bx / lb);
            return Math.Atan2(Math.Abs(cross), dot) * 180d / Math.PI;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static SketchObjectShape? ShapeOf(KompasAPI7.IDrawingObject drawing) => drawing switch
    {
        KompasAPI7.ILineSegment => SketchObjectShape.Segment,
        // The ARC is checked before the circle: an arc carries a radius too, and naming it a circle
        // would let a radial dimension be placed where the diametral kind was asked for.
        KompasAPI7.IArc => SketchObjectShape.Arc,
        KompasAPI7.ICircle => SketchObjectShape.Circle,
        _ => null,
    };

    private static string ShapeText(SketchObjectShape? shape) =>
        shape is null ? "не отрезок, не дуга и не окружность" : SketchDimensionRule.ShapeText(shape.Value);

    private static SketchConstraintObject? FindByAddress(
        IReadOnlyList<SketchConstraintObject> objects, string address) =>
        objects.FirstOrDefault(o => string.Equals(o.Address, address, StringComparison.Ordinal));

    /// <summary>Finds the object by its ADDRESS, the same rule as the constraint route: an address that
    /// resolves to zero or to several objects is a named refusal, never a silent "first match".</summary>
    private static SketchConstraintObject ResolveSketchObject(
        IReadOnlyList<SketchConstraintObject> objects, string address, string field)
    {
        var matches = objects.Where(o => string.Equals(o.Address, address, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                matches.Count == 0 ? ErrorCodes.DocumentNotFound : ErrorCodes.AmbiguousSelection,
                $"{field}='{address}' разрешился в {matches.Count} объектов вместо одного " +
                $"(в эскизе {objects.Count} объектов). Адрес обязан различать объекты, и молчаливый "
                + "выбор первого сделал бы его неоднозначным.",
                details: new Dictionary<string, object?>
                {
                    [field] = address,
                    ["matches"] = matches.Count,
                    ["enumerated"] = objects.Count,
                });
        }

        return matches[0];
    }

    private static string SafeBoolText(Func<bool> call)
    {
        try
        {
            return call().ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "не прочитано (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Outcome of the LINEAR dimension placement, all of it measured inside ONE session.</summary>
    private sealed record DimensionOutcome(
        string? DimensionRef,
        double? LengthBefore,
        double? ValueRead,
        bool? PointsMatchSegment,
        int? DimensionPointsCount,
        bool AssociationCreated,
        bool FixedDimCreated,
        bool ValueDimCreated,
        int AssociationAsMergePoints,
        string? Failure,
        string? DimensionVariable = null);

    private DimensionOutcome PlaceDimension(
        KompasAPI7.IFragmentDocument fragment,
        KompasAPI7.ILineSegment segment,
        KompasAPI7.IDrawingObject1 segmentHost,
        double x1, double y1, double x2, double y2,
        double? lengthBefore,
        CreateSketchDimensionCommand command,
        List<string> notes)
    {
        try
        {
            var view = fragment.ViewsAndLayersManager?.Views?.get_View(0);
            if (view is not KompasAPI7.ISymbols2DContainer symbols)
            {
                return new(null, lengthBefore, null, null, null, false, false, false, 0,
                    "вид эскиза не отвечает QI(ISymbols2DContainer): документированного маршрута "
                    + "создания размера нет, и соседний маршрут не подставляется");
            }

            var valueMm = command.ValueMm ?? double.NaN;

            var dimensions = symbols.LineDimensions;
            var before = SafeInt(() => dimensions.Count);
            var dimension = dimensions.Add();
            if (dimension is null)
            {
                return new(null, lengthBefore, null, null, null, false, false, false, 0,
                    "ILineDimensions.Add() → null");
            }

            notes.Add($"линейных размеров до: {Api5Number(before)}, после Add(): " +
                      $"{Api5Number(SafeInt(() => dimensions.Count))}");

            // The dimension measures between its own (X1,Y1) and (X2,Y2) — the help says
            // IDimensionText::NominalValue returns exactly that distance. The third point and the shelf
            // only place the dimension line, so they are derived from the segment, not invented.
            var normalX = -(y2 - y1);
            var normalY = x2 - x1;
            var normalLength = Math.Sqrt(normalX * normalX + normalY * normalY);
            var offset = command.OffsetMm ?? DefaultDimensionOffsetMm;
            var unitX = normalLength > 1e-12 ? normalX / normalLength : 0d;
            var unitY = normalLength > 1e-12 ? normalY / normalLength : 1d;
            // The dimension line is drawn AWAY from the origin: a positive offset must not push it
            // through the profile.
            if (unitX * (x1 + x2) + unitY * (y1 + y2) < 0d)
            {
                unitX = -unitX;
                unitY = -unitY;
            }

            dimension.X1 = x1;
            dimension.Y1 = y1;
            dimension.X2 = x2;
            dimension.Y2 = y2;
            dimension.X3 = x1 + unitX * offset;
            dimension.Y3 = y1 + unitY * offset;
            dimension.ShelfX = (x1 + x2) / 2d + unitX * offset;
            dimension.ShelfY = (y1 + y2) / 2d + unitY * offset;

            var updated = SafeBool(() => dimension.Update());
            notes.Add($"ILineDimension.Update() → {updated}");

            var dimensionRef = SafeInt(() => dimension.Reference) ?? 0;

            if (dimension as KompasAPI7.IDrawingObject1 is not { } dimensionHost)
            {
                return new(dimensionRef.ToString(), lengthBefore, null, null, null, false, false, false, 0,
                    "объект-размер не отвечает QI(IDrawingObject1): NewConstraint() недоступен");
            }

            // 1. The ASSOCIATION — measured to be what makes the value drive the geometry. Its partner
            //    is the SAFEARRAY form, and the partner IS the measured segment.
            var association = Api7SketchConstraints.Create(
                dimensionHost, ksConstraintTypeEnum.ksCAssociation, null,
                new object[] { segmentHost }, null, null, null, null, null, notes);
            var associationCreated = association.Created == true;
            notes.Add($"ksCAssociation: {Describe(association)}");

            // 2. The fixed dimension — the documented precondition of the value.
            var fixedDim = Api7SketchConstraints.Create(
                dimensionHost, ksConstraintTypeEnum.ksCFixedDim, null, null, null, null, null, null, null,
                notes);
            var fixedDimCreated = fixedDim.Created == true;
            notes.Add($"ksCFixedDim: {Describe(fixedDim)}");

            // 3. The value.
            var valueDim = Api7SketchConstraints.Create(
                dimensionHost, ksConstraintTypeEnum.ksCDimWithVariable, null, null, null, null,
                valueMm, valueMm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null, notes);
            var valueDimCreated = valueDim.Created == true;
            notes.Add($"ksCDimWithVariable: {Describe(valueDim)}");

            var rows = Api7SketchConstraints.ReadRows(dimensionHost, notes);
            var mergePoints = rows.Count(r => r.TypeCode == (int)ksConstraintTypeEnum.ksCMergePoints);
            notes.Add($"записей у размера: {rows.Count}; из них ksCMergePoints: {mergePoints} " +
                      "(ассоциация читается как ДВЕ такие записи с Valid = true, тогда как запись " +
                      "ksCAssociation несёт Valid = false)");

            // The kernel names the dimension's variable itself (v9 in the measured runs); it is
            // published so the client can bind it to a part variable with kompas_bind_parameter.
            var dimensionVariable = rows.LastOrDefault(r =>
                r.TypeCode == (int)ksConstraintTypeEnum.ksCDimWithVariable)?.Variable;
            notes.Add("переменная размера: " + (dimensionVariable ?? "не прочитана"));

            // CONFIRMATION BY FACT, part 1: the dimension's points sit on the segment's ends — and BOTH
            // are read AFTER the value was written, because the value MOVES the segment: comparing the
            // dimension's points with the ends captured BEFORE the write compared live data with stale
            // data, and that was a defect of this instrument, not a missing binding.
            bool? pointsMatch = null;
            try
            {
                var nowX1 = segment.X1;
                var nowY1 = segment.Y1;
                var nowX2 = segment.X2;
                var nowY2 = segment.Y2;
                notes.Add($"концы отрезка после записи значения: ({nowX1:0.######},{nowY1:0.######})—" +
                          $"({nowX2:0.######},{nowY2:0.######})");

                var sameOrder = Math.Abs(dimension.X1 - nowX1) < 1e-6 && Math.Abs(dimension.Y1 - nowY1) < 1e-6
                                && Math.Abs(dimension.X2 - nowX2) < 1e-6 && Math.Abs(dimension.Y2 - nowY2) < 1e-6;
                var reversed = Math.Abs(dimension.X1 - nowX2) < 1e-6 && Math.Abs(dimension.Y1 - nowY2) < 1e-6
                               && Math.Abs(dimension.X2 - nowX1) < 1e-6 && Math.Abs(dimension.Y2 - nowY1) < 1e-6;
                pointsMatch = sameOrder || reversed;
                notes.Add($"точки размера: ({dimension.X1:0.######},{dimension.Y1:0.######})—" +
                          $"({dimension.X2:0.######},{dimension.Y2:0.######}); совпадение с концами " +
                          $"отрезка = {pointsMatch} (порядок тот же: {sameOrder}, обратный: {reversed})");
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add("точки размера не перечитаны: " + ex.GetType().Name);
            }

            // CONFIRMATION BY FACT, part 2: the dimension's value, re-read from the dimension itself.
            double? valueRead = null;
            try
            {
                if (dimension is KompasAPI7.IDimensionText text)
                {
                    valueRead = text.NominalValue;
                }
                else
                {
                    notes.Add("объект-размер не отвечает QI(IDimensionText): значение не перечитано");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add("значение размера не перечитано: " + ex.GetType().Name);
            }

            return new(dimensionRef.ToString(), lengthBefore, valueRead, pointsMatch, rows.Count,
                associationCreated, fixedDimCreated, valueDimCreated, mergePoints, null, dimensionVariable);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new(null, lengthBefore, null, null, null, false, false, false, 0,
                "размещение размера отказало: " + ex.GetType().Name);
        }
    }

    /// <summary>The segment's length after the rebuild, by a FRESH enumeration of the same address.
    /// <c>null</c> means "not read" and is named in the notes, never silently zero.</summary>
    private double? ReadSegmentLength(KompasAPI7.ISketch sketch, string address, List<string> notes)
    {
        var readNotes = new List<string>();
        var length = InReadSession(sketch, readNotes, (_, objects) =>
        {
            var match = objects.FirstOrDefault(o => string.Equals(o.Address, address, StringComparison.Ordinal));
            return match?.Drawing is KompasAPI7.ILineSegment segment ? SafeDouble(() => segment.Length) : null;
        });

        notes.AddRange(readNotes);
        if (length is null)
        {
            notes.Add("длина отрезка после перестроения НЕ прочитана — это названная причина, а не ноль");
        }

        return length;
    }

    private static string Describe(Api7SketchConstraints.CreateOutcome outcome) =>
        outcome.Failure is not null
            ? outcome.Failure
            : "Create = " + (outcome.Created is null ? "не прочитано" : outcome.Created.Value.ToString());

    private static string Api5Number(double? value) =>
        value?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано";
}
