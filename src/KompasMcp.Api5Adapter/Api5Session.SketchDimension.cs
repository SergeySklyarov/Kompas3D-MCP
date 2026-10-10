using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>A LINEAR DIMENSION on a segment of an existing sketch, bound associatively and driving the
/// geometry (<c>kompas_create_sketch_dimension</c>; block G6).</summary>
/// <remarks>
/// DOC: the dimension is created by the documented route — <c>IView</c> QI <c>ISymbols2DContainer</c>
/// (<c>isymbols2dcontainer.html</c> — «Дополнительный интерфейс вида… можно получить у интерфейса вида
/// IView посредством QueryInterface») → <c>ILineDimensions::Add()</c>
/// (<c>ilinedimensions_add.html</c>) → <c>ILineDimension</c>, whose hierarchy is
/// <c>IKompasAPIObject → IDrawingObject → ILineDimension</c> (<c>ilinedimension.html</c>), then
/// <c>IModelObject::Update()</c>. The value is set through <c>ksCDimWithVariable</c>, whose documented
/// precondition is an existing <c>ksCFixedDim</c> on the same object
/// (<c>iparametriticconstraint_value.html</c>).
/// MEASURED: the ASSOCIATION is what makes the value drive the geometry. Without it the same call is
/// accepted and the length does not move; with it the length becomes the value (40 → 50) and survives
/// save → close → reopen. The association is written as <c>ksCAssociation</c> with the measured segment
/// as its SAFEARRAY partner.
/// INVARIANT: the confirmation is by FACT, not by the generic pair — the dimension's points must
/// coincide with the segment's ends, the dimension's value must be re-read as requested, and the
/// segment's length must equal the value. The generic <c>Create() == true &amp;&amp; Valid != false</c>
/// pair is NOT weakened for the other types: it stays where it is, in
/// <see cref="CreateSketchConstraint"/>. MEASURED here: the association reads back as TWO
/// <c>ksCMergePoints</c> records with <c>Valid = true</c>, while the <c>ksCAssociation</c> record
/// itself carries <c>Valid = false</c> — so the generic pair would refuse a working association.
/// History: docs/decisions/adapter-sketch.md#sketch-dimension
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>The route, named once so the response and the acceptance evidence cannot drift
    /// apart.</summary>
    private const string SketchDimensionRoute =
        "ISketch.BeginEdit() → IView(QI ISymbols2DContainer).LineDimensions.Add() → ILineDimension " +
        "(точки — концы измеряемого отрезка) → Update() → QI(IDrawingObject1) → ksCAssociation " +
        "(партнёр — массив отрезков) → ksCFixedDim → ksCDimWithVariable(значение) → перестроение";

    /// <summary>Creates a driving linear dimension on one segment of an existing sketch.</summary>
    public SketchDimensionCreateResult CreateSketchDimension(CreateSketchDimensionCommand command)
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

        if (!double.IsFinite(command.ValueMm) || command.ValueMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"value_mm обязано быть положительным конечным числом, получено {command.ValueMm}. "
                + "Размер нулевой длины не выражает измерения.",
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
                        command, notes);
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
                    ["value_mm"] = command.ValueMm,
                    ["notes"] = notes.ToArray(),
                });
        }

        BumpRevision(target.Document, "sketch.dimension.create");

        // The length is read AFTER the rebuild, through a fresh enumeration: the value is expected to
        // have moved the geometry, and that is the fact the caller is buying.
        var lengthAfter = ReadSegmentLength(sketch7, command.ObjectRef!, notes);
        var applied = outcome.PointsMatchSegment == true
                      && outcome.ValueRead is { } read && Math.Abs(read - command.ValueMm) < 1e-6
                      && lengthAfter is { } after && Math.Abs(after - command.ValueMm) < 1e-6;

        if (!applied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Размер создан, но ТРИ ФАКТА не совпали: точки размера на концах отрезка = " +
                $"{outcome.PointsMatchSegment}, значение размера перечитано как " +
                $"{Api5Number(outcome.ValueRead)} (запрошено {Api5Number(command.ValueMm)}), длина " +
                $"отрезка после перестроения = {Api5Number(lengthAfter)}. Применением объявляется " +
                "только совпадение всех трёх.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["points_match_segment"] = outcome.PointsMatchSegment,
                    ["value_read"] = outcome.ValueRead,
                    ["value_requested"] = command.ValueMm,
                    ["length_after"] = lengthAfter,
                    ["notes"] = notes.ToArray(),
                });
        }

        return new SketchDimensionCreateResult(
            ObjectRef: command.ObjectRef!,
            DimensionRef: outcome.DimensionRef ?? string.Empty,
            ValueMm: command.ValueMm,
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
            Route: SketchDimensionRoute);
    }

    /// <summary>Outcome of the dimension placement, all of it measured inside ONE session.</summary>
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
        string? Failure);

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
            var offset = command.OffsetMm;
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
                command.ValueMm, command.ValueMm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null, notes);
            var valueDimCreated = valueDim.Created == true;
            notes.Add($"ksCDimWithVariable: {Describe(valueDim)}");

            var rows = Api7SketchConstraints.ReadRows(dimensionHost, notes);
            var mergePoints = rows.Count(r => r.TypeCode == (int)ksConstraintTypeEnum.ksCMergePoints);
            notes.Add($"записей у размера: {rows.Count}; из них ksCMergePoints: {mergePoints} " +
                      "(ассоциация читается как ДВЕ такие записи с Valid = true, тогда как запись " +
                      "ksCAssociation несёт Valid = false)");

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
                associationCreated, fixedDimCreated, valueDimCreated, mergePoints, null);
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
