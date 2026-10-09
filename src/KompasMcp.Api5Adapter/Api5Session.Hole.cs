using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Native hole (docs/05 SM-07): three measured modes and a position away from the origin.</summary>
/// <remarks>
/// Basis — probe M (<c>docs/acceptance/api7/hole-modes.md</c>). TEST: M.2 counterbore, M.3 countersink
/// (an 11-row table), M.4 blind with a flat bottom (<c>ksDTBlind</c> does not exist — blind is
/// <c>ksDTValue</c>), M.5 position away (<c>Point3DParamSurface</c> + <c>ksOffsetByCoords</c>). ROUTE —
/// API7: an API5 hole (<c>NewEntity(o3d_hole=52)</c>) has no mode parameters. INVARIANT: volume on the MAIN body.
/// History: docs/decisions/adapter-features.md#hole-route
/// </remarks>
public partial class Api5Session
{
    /// <summary>Hole family name in server responses.</summary>
    private const string HoleFamily = "hole";

    /// <summary>Tolerance for matching a body to a hole by the cylindrical-face radius, mm.</summary>
    private const double HoleRadiusToleranceMm = 0.01d;

    /// <summary>Tolerance for comparing the WRITTEN number with the read-back one on edit, mm.</summary>
    /// <remarks><c>1e-6</c> is the same tolerance used on CREATION (<c>HoleParametersMatch</c>), and it
    /// is not "by eye": the probe read the written values back with no divergence, while a derived
    /// countersink depth returned with the kernel's own noise — which is far beyond this tolerance and
    /// does not mask "not applied".</remarks>
    private const double HoleEditToleranceMm = 1e-6d;

    /// <summary>Create a native hole of a measured mode. A bridge refusal is returned as
    /// <c>CAPABILITY_UNAVAILABLE</c> with a cause, not a silent null: the caller must tell "API7
    /// unavailable" from "KOMPAS rejected the parameter".</summary>
    /// <remarks>The base face is taken by an explicit <c>face:</c> reference, not "the top face of the
    /// body". Probe M chose the largest face by area, but that was a probe technique: <c>BaseSurface</c>
    /// is the client's decision where to drill, and the server may not substitute a guess for
    /// it.</remarks>
    public HoleResult Hole(HoleCommand command)
    {
        if (!References.TryGet(command.FaceRef, out var anchor) || anchor is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.FaceRef}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(anchor.DocumentId);
        if (anchor.Payload is not ksFaceDefinition face)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.FaceRef}' указывает не на грань (kind={anchor.Kind}).",
                details: new Dictionary<string, object?> { ["kind"] = anchor.Kind });
        }

        ValidateHoleMode(command);

        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Параметры режима родного отверстия живут только в HoleParameters API7; " +
                "признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        // IChamfer.BaseObjects and IHoleDisposal.BaseSurface take an API7 object, so the face must
        // cross the bridge: an untransferred face is a value API7 will not accept.
        var baseSurface = bridge.TransferTo7(face) as IModelObject;
        if (baseSurface is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Опорная грань не перенесена в API7 (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") + ") — отверстие не создавалось.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var placementNotes = new List<string>();
        var (created, failure, reportedCountersinkDepth) =
            CreateHoleMode(bridge, container, command, baseSurface, placementNotes);

        if (!created)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отверстие через IHole3D не создано: " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = failure });
        }

        // Without RebuildModel the API7 write stays a representation: MEASURED by probe E on
        // IExtrusion.Sketch and repeated on the chamfer F.10 and the fillet. The call order is part of
        // the contract, not style.
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "hole." + command.Mode.ToString().ToLowerInvariant());

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        var count = Api7Hole.Count(container);
        var readBack = count is int n and > 0 ? Api7Hole.Read(container, n - 1) : null;
        // ALL axes of the matching radius are read, not the first: with several holes of one diameter
        // "the first match" is a neighbour's axis, and the created feature's coordinate would be
        // attributed to it. Below, the requested position is compared against the list separately.
        var axisOrigins = Api7Hole.FindCylinderOrigins(
            document.PartNow(), command.DiameterMm / 2d, HoleRadiusToleranceMm);
        var center = axisOrigins.Count > 0 ? axisOrigins[0] : null;

        var checks = new List<NamedCheck>
        {
            new("hole_created", true,
                Observed: command.Mode.ToString(),
                Expected: "режим создан и перестроен"),
            new("parameters_read_back", HoleParametersMatch(readBack, command),
                Observed: DescribeHole(readBack),
                Expected: DescribeHoleCommand(command)),
            new("face_count_grew", facesAfter is double after && facesBefore is double before && after > before,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: "цилиндрическая грань отверстия добавилась"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (command.OffsetXMm is not null || command.OffsetYMm is not null)
        {
            var wantedX = command.OffsetXMm ?? 0d;
            var wantedY = command.OffsetYMm ?? 0d;
            // It is searched AMONG ALL axes, not at the first: the request "shift to (25, 15)" is
            // satisfied if an axis with those coordinates exists on the body, and not satisfied if it
            // does not, even when a foreign axis of the matching radius stands nearby.
            var matching = axisOrigins.FirstOrDefault(o =>
                Math.Abs(o[0] - wantedX) <= 0.5d && Math.Abs(o[1] - wantedY) <= 0.5d);
            checks.Add(new NamedCheck(
                "position_applied",
                matching is not null,
                Observed: axisOrigins.Count == 0
                    ? "осей Ø" + command.DiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " на теле нет"
                    : string.Join("; ", axisOrigins.Select(o => $"({o[0]:0.###}, {o[1]:0.###})")),
                Expected: $"({wantedX:0.###}, {wantedY:0.###})"));
        }

        var materialRemoved = volumeBefore is double b && volumeAfter is double a && a < b;
        var volumePairRead = volumeBefore is double && volumeAfter is double;
        // The declared delta is the geometry check of this tool: a mismatch is a REFUSAL, an unreadable
        // pair is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var measuredDelta = volumeBefore is double vBefore && volumeAfter is double vAfter
            ? vBefore - vAfter
            : (double?)null;
        var declared = DeclaredExpectation.Evaluate(command.ExpectedVolumeDeltaMm3, measuredDelta);
        if (declared.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("volume_delta", declared));
            if (declared.IsRefusal)
            {
                throw DeclaredExpectation.Refusal(
                    declared, "kompas_hole", "уменьшение объёма", document.Revision);
            }
        }
        else if (volumePairRead)
        {
            // Direction only, and only when BOTH volumes were read: an unread pair is an unperformed
            // check, not a failed one.
            checks.Add(new NamedCheck(
                "volume_delta",
                materialRemoved,
                Observed: (volumeBefore!.Value - volumeAfter!.Value)
                    .ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: "не задано — проверено только направление"));
        }

        var numericMatch = declared.IsDeclared ? declared.IsConfirmed : (bool?)null;
        var geometryConfirmed = readBack is not null
            && checks.Exists(c => c.Name == "parameters_read_back" && c.Passed)
            && materialRemoved
            && numericMatch is not false
            && checks.TrueForAll(c => c.Name != "position_applied" || c.Passed);

        var unverified = new List<string>();
        if (declared.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnreadableReason("уменьшение объёма"));
        }
        else if (!volumePairRead)
        {
            unverified.Add(
                "volume_delta_not_computable — объём до или после операции не прочитан: ни аналитическое "
                + "ожидание, ни направление изменения материала проверить нечем");
        }
        else if (!geometryConfirmed)
        {
            unverified.Add(
                "geometry_not_confirmed — КОМПАС принял запись, но измерение не подтвердило ожидаемую геометрию");
        }

        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add(
                "expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал " +
                "вызывающий, численного доказательства нет");
        }

        if (command.Mode == HoleMode.ThroughCountersink)
        {
            // The derived depth is a MEASURED feature, not a caveat: with the "diameter + angle" method
            // writing to CountersinkDepth has no effect and the object returns its own number. The
            // returned value is therefore published, and it is stated here why it may differ from the
            // requested one.
            unverified.Add(
                "countersink_depth_derived — при способе «диаметр + угол» глубина зенковки " +
                "производна от угла (измерено M.3: запись 2/4/6 не меняет ничего, объект " +
                "возвращает (rM − rP)/tan(угол/2), где rM — радиус устья, rP — радиус пилота). " +
                "Сверяйте countersink_depth_mm, а не запрошенное число");
        }

        unverified.Add(
            "single_face_only — измерялся только один базовый случай на режим (M.2/M.3/M.4); " +
            "на наклонных гранях и в многотельных деталях режимы не проверялись");

        var reference = FindHoleEntity(document);
        if (reference is null)
        {
            // A reference to a feature the API5 tree does not show must not be issued: an edit through
            // it would fail anyway, and the caller would learn of it later.
            unverified.Add("feature_ref_withheld — признак не найден в дереве API5, ссылка не выдана");
            return new HoleResult(
                null,
                command.Mode.ToString().ToLowerInvariant(),
                readBack?.DiameterMm,
                readBack?.DepthMm,
                reportedCountersinkDepth,
                center,
                bodiesAfter,
                volumeAfter,
                new VerificationDto(VerificationLevel.CallReturned, checks, unverified),
                string.Join(", ", placementNotes));
        }

        return new HoleResult(
            ToDto(References.Register("feature", document.Id, document.Revision, reference),
                $"hole Ø{command.DiameterMm:0.###} {command.Mode}"),
            command.Mode.ToString().ToLowerInvariant(),
            readBack?.DiameterMm,
            readBack?.DepthMm,
            reportedCountersinkDepth,
            center,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", placementNotes));
    }

    /// <summary>One mode — one branch. Returns (created, failure cause, reported countersink depth): a
    /// triple, not a pair, because the countersink has a field the object computes itself, and losing
    /// it would pass a written number off as the effective one.</summary>
    private static (bool Created, string? Failure, double? ReportedDepth) CreateHoleMode(
        Api7Bridge bridge,
        IModelContainer container,
        HoleCommand command,
        IModelObject baseSurface,
        List<string> placementNotes)
    {
        // The position is a COMMON step for all three modes, not a blind-specific one. An earlier
        // revision supported the offset only in the blind_flat branch: acceptance HO.6 showed that a
        // through counterbore with offset_x_mm=-20 SILENTLY stayed at the origin and returned err=None,
        // passing a wrong position off as done. The offset is therefore an injected step each mode runs
        // between Add() and Update() on ITS OWN object: a separate Add() would create a second feature
        // and leave the first unfinished.
        // History: docs/decisions/adapter-features.md#hole-offset-common
        var wantsOffset = command.OffsetXMm is not null || command.OffsetYMm is not null;
        Func<IHoleDisposal, PlacementOutcome>? place = wantsOffset
            ? disposal =>
            {
                var outcome = Api7Hole.TryPlaceByCoordinates(
                    disposal, command.OffsetXMm ?? 0d, command.OffsetYMm ?? 0d);
                placementNotes.AddRange(outcome.Notes);
                if (!outcome.Applied)
                {
                    placementNotes.Add(
                        "позиционирование не применено: " + (outcome.Failure ?? "причина не сообщена"));
                }

                return outcome;
            }
            : null;

        switch (command.Mode)
        {
            case HoleMode.BlindFlat:
                var blind = Api7Hole.TryCreateBlindFlat(
                    container, baseSurface, command.DiameterMm, command.DepthMm!.Value, place);
                return (blind.Created, blind.Failure, null);

            case HoleMode.ThroughCounterbore:
                var counterbore = Api7Hole.TryCreateCounterbore(
                    container,
                    baseSurface,
                    command.DiameterMm,
                    command.CounterboreDiameterMm!.Value,
                    command.CounterboreDepthMm!.Value,
                    place);
                return (counterbore.Created, counterbore.Failure, null);

            case HoleMode.ThroughCountersink:
                var countersink = Api7Hole.TryCreateCountersink(
                    container,
                    baseSurface,
                    command.DiameterMm,
                    command.CountersinkDiameterMm!.Value,
                    command.CountersinkAngleDeg!.Value,
                    // The depth is written as zero and does not pretend to be meaningful: with the
                    // "diameter + angle" method it is derived and writing to it has no effect (M.3).
                    depthMm: 0d,
                    place);
                return (countersink.Created, countersink.Failure, countersink.ReportedDepthMm);

            default:
                return (false, $"режим '{command.Mode}' не реализован", null);
        }
    }

    /// <summary>"Field ↔ mode" rules. Refused before COM: mode numbers of different modes live in
    /// different interfaces, and "applied what we could, ignored the rest" would be silently wrong
    /// geometry here. The check is paid for by measurement — the cost of a mistake here is asymmetric:
    /// a spurious refusal is seen at once, while an accepted-and-ignored number survives to
    /// acceptance.</summary>
    private static void ValidateHoleMode(HoleCommand command)
    {
        if (command.DiameterMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Диаметр отверстия обязан быть положительным: КОМПАС принимает ноль и строит " +
                "признак без материала при неизменном объёме (тот же дефект, что у нулевого катета " +
                "фаски, F.12). Это не отверстие.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["diameter_mm"] = command.DiameterMm });
        }

        switch (command.Mode)
        {
            case HoleMode.BlindFlat:
                if (command.DepthMm is not double depth || depth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=blind_flat нужна положительная depth_mm: глухое отверстие без " +
                        "глубины выразить нечем.",
                        RetryPolicy.Never);
                }

                RejectPresent(command, "counterbore_diameter_mm", command.CounterboreDiameterMm, "blind_flat");
                RejectPresent(command, "counterbore_depth_mm", command.CounterboreDepthMm, "blind_flat");
                RejectPresent(command, "countersink_diameter_mm", command.CountersinkDiameterMm, "blind_flat");
                RejectPresent(command, "countersink_angle_deg", command.CountersinkAngleDeg, "blind_flat");
                break;

            case HoleMode.ThroughCounterbore:
                RejectPresent(command, "depth_mm", command.DepthMm, "through_counterbore");
                if (command.CounterboreDiameterMm is not double bore || bore <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_counterbore нужен положительный counterbore_diameter_mm: " +
                        "без диаметра выточки режим отличается от сквозного отверстия только именем.",
                        RetryPolicy.Never);
                }

                if (command.CounterboreDepthMm is not double boreDepth || boreDepth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_counterbore нужна положительная counterbore_depth_mm.",
                        RetryPolicy.Never);
                }

                if (bore <= command.DiameterMm)
                {
                    // A recess narrower than the pilot removes nothing: the MEASURED formula M.2 gives
                    // zero or a negative ring, so the feature would build "successfully" with no
                    // geometry.
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Диаметр выточки обязан быть больше диаметра пилота: выточка уже пилота не " +
                        "снимает материал (измеренная формула M.2 — кольцо π/4·(D²−d²)·h).",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?>
                        {
                            ["counterbore_diameter_mm"] = bore,
                            ["diameter_mm"] = command.DiameterMm,
                        });
                }

                RejectPresent(command, "countersink_diameter_mm", command.CountersinkDiameterMm, "through_counterbore");
                RejectPresent(command, "countersink_angle_deg", command.CountersinkAngleDeg, "through_counterbore");
                break;

            case HoleMode.ThroughCountersink:
                RejectPresent(command, "depth_mm", command.DepthMm, "through_countersink");
                if (command.CountersinkDiameterMm is not double mouth || mouth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_countersink нужен положительный countersink_diameter_mm — " +
                        "диаметр устья, а не пилота.",
                        RetryPolicy.Never);
                }

                if (command.CountersinkAngleDeg is not double angle || angle <= 0d || angle >= 180d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_countersink нужен countersink_angle_deg в интервале (0; 180): " +
                        "вне его конус зенковки не строится. Измерялись 60, 90 и 120 (M.3).",
                        RetryPolicy.Never);
                }

                if (mouth <= command.DiameterMm)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Диаметр устья зенковки обязан быть больше диаметра пилота: устье уже пилота " +
                        "не снимает материал.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?>
                        {
                            ["countersink_diameter_mm"] = mouth,
                            ["diameter_mm"] = command.DiameterMm,
                        });
                }

                RejectPresent(command, "counterbore_diameter_mm", command.CounterboreDiameterMm, "through_countersink");
                RejectPresent(command, "counterbore_depth_mm", command.CounterboreDepthMm, "through_countersink");
                break;

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Режим '{command.Mode}' не реализован: измерены blind_flat, through_counterbore " +
                    "и through_countersink.",
                    RetryPolicy.Never);
        }

        // The offset is given as a pair or not at all: one number of the two would leave the other
        // coordinate to the server's discretion, so the response would carry a different position than
        // requested.
        if ((command.OffsetXMm is null) != (command.OffsetYMm is null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Смещение задаётся парой offset_x_mm и offset_y_mm: одна координата без второй " +
                "оставляет другую на догадку сервера.",
                RetryPolicy.Never);
        }
    }

    private static void RejectPresent(HoleCommand command, string field, double? value, string mode)
    {
        if (value is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"При mode={mode} поле {field} запрещено: оно принадлежит другому режиму, и принять " +
                "его значило бы выдать за применённый параметр, которого этот режим не читает.",
                RetryPolicy.Never);
        }
    }

    /// <summary>The last operation element, which is the hole feature. The search is by
    /// <see cref="KompasObjectTypes.Hole3D"/> (583, <c>o3d_Hole3D</c>), NOT by
    /// <see cref="KompasObjectTypes.HoleOperation"/> (52).</summary>
    /// <remarks>MEASURED: the number the feature is CREATED with (<c>NewEntity(52)</c>) never appears in
    /// the tree — the live hole reads as <c>583</c> (<c>o3d_Hole3D</c>) — so a search by 52 never found it
    /// and <c>feature_ref</c> was wrongly withheld. Both tree collections are scanned deliberately.
    /// History: docs/decisions/adapter-features.md#hole-tree-type
    /// </remarks>
    private static ksEntity? FindHoleEntity(DocumentEntry document)
    {
        try
        {
            ksEntity? found = null;
            foreach (var kind in new[]
                     {
                         KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                         (short)-1,
                     })
            {
                if (document.PartNow().EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity
                        && entity.type == KompasObjectTypes.Hole3D)
                    {
                        found = entity;
                    }
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Compare the written with the read-back. Only the fields the mode actually reads are
    /// compared: for the countersink the depth is NOT compared with the requested one because it is
    /// derived, and demanding a match would give acceptance a knowingly false claim.</summary>
    private static bool HoleParametersMatch(HoleReadDto? read, HoleCommand command)
    {
        if (read is null)
        {
            return false;
        }

        var diameterOk = read.DiameterMm is double d && Math.Abs(d - command.DiameterMm) <= 1e-6;
        return command.Mode switch
        {
            HoleMode.BlindFlat => diameterOk
                && read.DepthMm is double depth && Math.Abs(depth - command.DepthMm!.Value) <= 1e-6,
            HoleMode.ThroughCounterbore => diameterOk
                && read.SpotfacingDiameterMm is double bore
                && Math.Abs(bore - command.CounterboreDiameterMm!.Value) <= 1e-6
                && read.SpotfacingDepthMm is double boreDepth
                && Math.Abs(boreDepth - command.CounterboreDepthMm!.Value) <= 1e-6,
            HoleMode.ThroughCountersink => diameterOk
                && read.CountersinkDiameterMm is double mouth
                && Math.Abs(mouth - command.CountersinkDiameterMm!.Value) <= 1e-6
                && read.CountersinkAngleDeg is double angle
                && Math.Abs(angle - command.CountersinkAngleDeg!.Value) <= 1e-6,
            _ => false,
        };
    }

    private static string DescribeHole(HoleReadDto? read) =>
        read is null
            ? "IHole3D не читается"
            : $"тип={read.HoleType} D={Fmt(read.DiameterMm)} глубина={Fmt(read.DepthMm)} " +
              $"({read.DepthType}) дно={read.EndFaceType} выточка={Fmt(read.SpotfacingDiameterMm)}×" +
              $"{Fmt(read.SpotfacingDepthMm)} зенковка={Fmt(read.CountersinkDiameterMm)}@ " +
              $"{Fmt(read.CountersinkAngleDeg)}° h={Fmt(read.CountersinkDepthMm)}";

    private static string DescribeHoleCommand(HoleCommand command) => command.Mode switch
    {
        HoleMode.BlindFlat => $"тип=ksHTBase D={Fmt(command.DiameterMm)} глубина={Fmt(command.DepthMm)}",
        HoleMode.ThroughCounterbore =>
            $"тип=ksHTCounterbore D={Fmt(command.DiameterMm)} выточка={Fmt(command.CounterboreDiameterMm)}×" +
            $"{Fmt(command.CounterboreDepthMm)}",
        HoleMode.ThroughCountersink =>
            $"тип=ksHTCountersinking D={Fmt(command.DiameterMm)} устье=" +
            $"{Fmt(command.CountersinkDiameterMm)}@ {Fmt(command.CountersinkAngleDeg)}°",
        _ => command.Mode.ToString(),
    };

    private static string Fmt(double? value) =>
        value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается";

    /// <summary>What the server sees of a hole: type, diameter, depth and mode parameters.</summary>
    /// <remarks>Read ONLY from API7 — MEASURED, not a choice: in <c>Interop.Kompas6API5</c> the type
    /// <c>ksHoleDefinition</c> does NOT EXIST (among 67 declared definitions, <c>ksChamferDefinition</c>
    /// and <c>ksFilletDefinition</c> are there, the hole is not). A hole is a feature with <c>type = 52</c>
    /// (<c>o3d_hole</c>) with no API5 definition, which is why SM-07 took the API7 route (ADR-004 §3).
    /// The feature is addressed by INDEX in <c>IModelContainer.Holes3D</c>; on ambiguity null is returned
    /// and the confirmation level drops. An empty field means "not read", not "zero".</remarks>
    private HoleDto? ReadHole(DocumentEntry document, int index)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null || Api7Hole.Count(container) is not int count || index < 0 || index >= count)
        {
            return null;
        }

        var read = Api7Hole.Read(container, index);
        if (read is null)
        {
            return null;
        }

        return new HoleDto(
            HoleType: read.HoleType,
            DiameterMm: read.DiameterMm,
            DepthType: read.DepthType,
            DepthMm: read.DepthMm,
            EndFaceType: read.EndFaceType,
            CounterboreDiameterMm: read.SpotfacingDiameterMm,
            CounterboreDepthMm: read.SpotfacingDepthMm,
            CountersinkDiameterMm: read.CountersinkDiameterMm,
            CountersinkAngleDeg: read.CountersinkAngleDeg,
            CountersinkDepthMm: read.CountersinkDepthMm,
            CenterMm: read.DiameterMm is double d
                ? Api7Hole.FindCylinderOrigin(document.PartNow(), d / 2d, HoleRadiusToleranceMm)
                : null);
    }

    /// <summary>Hole parameters for an EXISTING tree feature — as <see cref="ReadHole"/> but without a
    /// caller-supplied index: the caller named the feature, and the match must be unambiguous.</summary>
    /// <remarks>The match is by the NUMBER of holes in the API7 container: one hole, one link. With
    /// several, the "tree feature ↔ Holes3D entry" correspondence is unproven (the feature name does not
    /// survive the API5↔API7 transition — MEASURED by probe F) and null is returned; <c>family</c> stays
    /// recognised but the level drops to <c>call_returned</c>. Index 0 is not enough: <c>Holes3D[0]</c>
    /// is "the document's first hole", not the asked-about feature's. On a one-hole document they are the
    /// same, which is why acceptance row HD.25 first creates EXACTLY one hole.</remarks>
    private HoleDto? ReadHoleFeature(DocumentEntry document)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null || Api7Hole.Count(container) != 1)
        {
            return null;
        }

        return ReadHole(document, 0);
    }

    /// <summary>Edit the parameters of an EXISTING native hole via <c>kompas_update_feature</c>.</summary>
    /// <remarks>ROUTE — MEASURED (probe <c>scratch/_hole_edit_probe.py</c>; report
    /// <c>docs/acceptance/api7/hole-modes.md</c>, M.6): the feature is taken by <c>IHoles3D.Hole3D[index]</c>,
    /// the members of ITS OWN mode are written, <c>IModelObject.Update()</c> applied, then rebuild. INVARIANT:
    /// the address is the same as for the read — the correspondence is proven by the hole being unique
    /// (<c>count != 1</c> refuses <c>CAPABILITY_UNAVAILABLE</c> before COM). LIMIT: the mode is NOT changed.
    /// History: docs/decisions/adapter-features.md#hole-edit-route
    /// </remarks>
    private UpdateFeatureResult UpdateHole(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        // Fields of FOREIGN families — before COM, and this is not caution but a MEASURED defect class:
        // an accepted and unapplied number survives to acceptance looking like a completed edit. The
        // list is built from the common family-field table plus an explicit list of fields inapplicable
        // to features (see RejectForeignSolidFields), so a new contract field must get a role —
        // otherwise SolidFeatureClassificationTests drops it, not acceptance.
        RejectForeignSolidFields(
            command,
            HoleFamily,
            "diameter_mm, depth_mm (только режим blind_flat), counterbore_diameter_mm и " +
            "counterbore_depth_mm (только through_counterbore), countersink_diameter_mm и " +
            "countersink_angle_deg (только through_countersink), expected_volume_delta_mm3",
            // depth_mm is this family's OWN field, although the role table gives it to extrusion: a
            // blind hole's depth is set by it. Whether it is foreign FOR THE MODE (for counterbore and
            // countersink the depth is derived or set by the recess) is decided by ValidateHoleEdit from
            // the read mode — that is where it is MEASURED. Without this line editing a blind hole was
            // refused before COM (rows F08.15/16/19/20.edit).
            ownFields: new[] { "depth_mm" });

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Параметры режима родного отверстия живут только в HoleParameters API7; признак " +
                "не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // Address: the INDEX in IHoles3D — the same route as the read (ReadHoleFeature). The hole's
        // uniqueness is the proof of correspondence; with several holes the "zeroth" collection element
        // is a foreign hole, and writing to it would change the wrong feature.
        var count = Api7Hole.Count(container);
        if (count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак отверстия не сопоставлен с записью коллекции API7: отверстий в документе " +
                $"{count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано"}, " +
                "а адрес существующего признака — индекс в IHoles3D, и при нескольких отверстиях " +
                "соответствие «признак дерева ↔ запись Holes3D» ничем не доказано. Правка не " +
                "выполняется: запись в чужое отверстие изменила бы не тот объект.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["holes_count"] = count });
        }

        var readBefore = Api7Hole.Read(container, 0);
        var mode = HoleModeOfRead(readBefore);
        if (mode is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Режим существующего отверстия не опознан: IHole3D.HoleType прочитан как " +
                $"'{readBefore?.HoleType ?? "не читается"}', а правка выполняется только в СВОЁМ " +
                "режиме. Измерены три: ksHTBase, ksHTCounterbore, ksHTCountersinking.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["hole_type"] = readBefore?.HoleType });
        }

        ValidateHoleEdit(command, mode.Value);

        bool written;
        string? failure;
        double? reportedCountersinkDepth = null;
        switch (mode.Value)
        {
            case HoleMode.BlindFlat:
            {
                var outcome = Api7Hole.TryWriteBlindFlat(container, 0, command.DiameterMm, command.DepthMm);
                written = outcome.Written;
                failure = outcome.Failure;
                break;
            }

            case HoleMode.ThroughCounterbore:
            {
                var outcome = Api7Hole.TryWriteCounterbore(
                    container, 0, command.DiameterMm,
                    command.CounterboreDiameterMm, command.CounterboreDepthMm);
                written = outcome.Written;
                failure = outcome.Failure;
                break;
            }

            default:
            {
                var outcome = Api7Hole.TryWriteCountersink(
                    container, 0, command.DiameterMm,
                    command.CountersinkDiameterMm, command.CountersinkAngleDeg);
                written = outcome.Written;
                failure = outcome.Failure;
                reportedCountersinkDepth = outcome.ReportedDepthMm;
                break;
            }
        }

        if (!written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Параметры отверстия не записаны: " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = failure,
                    ["hole_mode"] = mode.Value.ToString(),
                });
        }

        // The order "write → Update() → rebuild" is part of the contract, not style: without
        // RebuildModel the API7 write stays a representation (MEASURED by probe E and repeated on the
        // chamfer and fillet).
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "hole.update");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var readBack = Api7Hole.Read(container, 0);
        var stateAfter = ReadFeatureState(entity);

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
                Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("hole_mode_preserved",
                string.Equals(readBefore?.HoleType, readBack?.HoleType, StringComparison.Ordinal),
                Observed: readBack?.HoleType ?? "не читается",
                Expected: readBefore?.HoleType ?? "не читается"),
        };

        // Each REQUESTED number gets its own read-back check: "Update() returned true" is not
        // application, and a common "parameters matched" check would not tell an applied field from an
        // unapplied one.
        if (command.DiameterMm is double wantedDiameter)
        {
            checks.Add(new NamedCheck(
                "diameter_read_back",
                readBack?.DiameterMm is double got && Math.Abs(got - wantedDiameter) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.DiameterMm), Expected: Fmt(wantedDiameter)));
        }

        if (command.DepthMm is double wantedDepth)
        {
            checks.Add(new NamedCheck(
                "depth_read_back",
                readBack?.DepthMm is double got && Math.Abs(got - wantedDepth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.DepthMm), Expected: Fmt(wantedDepth)));
        }

        if (command.CounterboreDiameterMm is double wantedBore)
        {
            checks.Add(new NamedCheck(
                "counterbore_diameter_read_back",
                readBack?.SpotfacingDiameterMm is double got && Math.Abs(got - wantedBore) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.SpotfacingDiameterMm), Expected: Fmt(wantedBore)));
        }

        if (command.CounterboreDepthMm is double wantedBoreDepth)
        {
            checks.Add(new NamedCheck(
                "counterbore_depth_read_back",
                readBack?.SpotfacingDepthMm is double got && Math.Abs(got - wantedBoreDepth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.SpotfacingDepthMm), Expected: Fmt(wantedBoreDepth)));
        }

        if (command.CountersinkDiameterMm is double wantedMouth)
        {
            checks.Add(new NamedCheck(
                "countersink_diameter_read_back",
                readBack?.CountersinkDiameterMm is double got && Math.Abs(got - wantedMouth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.CountersinkDiameterMm), Expected: Fmt(wantedMouth)));
        }

        if (command.CountersinkAngleDeg is double wantedAngle)
        {
            checks.Add(new NamedCheck(
                "countersink_angle_read_back",
                readBack?.CountersinkAngleDeg is double got && Math.Abs(got - wantedAngle) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.CountersinkAngleDeg), Expected: Fmt(wantedAngle)));
        }

        if (mode.Value == HoleMode.ThroughCountersink)
        {
            // The derived number is PUBLISHED but not asserted as written: with the "diameter + angle"
            // method writing to CountersinkDepth has no effect (M.3), and comparing it with the
            // requested number would demand a knowingly false match from acceptance.
            checks.Add(new NamedCheck(
                "countersink_depth_derived",
                reportedCountersinkDepth is not null,
                Observed: Fmt(reportedCountersinkDepth),
                Expected: "производна от угла и устья; записанное число не утверждается"));
        }

        // The delta sign is part of the quantity's definition: volume decreases when the edit removes
        // material and GROWS when the depth decreases. So "removed" = before − after is compared, not
        // the absolute value: a first draft of the probe compared numbers of opposite sign and gave a
        // false "did not match" on correct geometry (an instrument defect, not a fact about the
        // product).
        var removedDelta = volumeBefore is double beforeVolume && volumeAfter is double afterVolume
            ? beforeVolume - afterVolume
            : (double?)null;

        // Each declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable value is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declaredDelta = DeclaredExpectation.Evaluate(command.ExpectedVolumeDeltaMm3, removedDelta);
        if (declaredDelta.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("volume_delta", declaredDelta));
            if (declaredDelta.IsRefusal)
            {
                throw DeclaredExpectation.Refusal(
                    declaredDelta, "kompas_update_feature/" + HoleFamily, "уменьшение объёма",
                    document.Revision);
            }
        }

        var declaredVolume = DeclaredExpectation.Evaluate(command.ExpectedVolumeMm3, volumeAfter);
        if (declaredVolume.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("volume_expected", declaredVolume));
            if (declaredVolume.IsRefusal)
            {
                throw DeclaredExpectation.Refusal(
                    declaredVolume, "kompas_update_feature/" + HoleFamily, "объём после правки",
                    document.Revision);
            }
        }

        var deltaMatched = declaredDelta.IsDeclared ? declaredDelta.IsConfirmed : (bool?)null;
        var volumeMatched = declaredVolume.IsDeclared ? declaredVolume.IsConfirmed : (bool?)null;

        var declared = command.ExpectedVolumeDeltaMm3 is not null || command.ExpectedVolumeMm3 is not null;
        var unverified = new List<string>();
        if (declaredDelta.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnreadableReason("уменьшение объёма"));
        }

        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnreadableReason("объём после правки"));
        }

        if (!declared)
        {
            unverified.Add(
                "expected_volume_delta_not_supplied — аналитическое ожидание дельты объёма не задавал " +
                "вызывающий, численного доказательства правки нет: подтверждено только чтение " +
                "параметров обратно");
        }

        if (mode.Value == HoleMode.ThroughCountersink)
        {
            unverified.Add(
                "countersink_depth_derived — при способе «диаметр + угол» глубина зенковки производна " +
                "от угла (измерено M.3: запись 2/4/6 не меняет ничего, объект возвращает " +
                "(rM − rP)/tan(угол/2)). Записанное число не проверяется");
        }

        unverified.Add(
            "hole_identity_by_single_hole — соответствие признака дерева и записи IHoles3D доказано " +
            "единственностью отверстия в документе; при нескольких отверстиях вызов отвергается, а " +
            "сопоставление по имени или по порядку в дереве не измерялось");

        unverified.Add(
            "dependent_features_not_enumerated — сохранность признаков, построенных ПОСЛЕ отверстия, " +
            "здесь не проверяется");

        var readBacksOk = checks.TrueForAll(c =>
            (!c.Name.EndsWith("_read_back", StringComparison.Ordinal) || c.Passed)
            && (c.Name != "hole_mode_preserved" || c.Passed)
            && (c.Name != "feature_identity_preserved" || c.Passed));
        var expectationsOk = (command.ExpectedVolumeDeltaMm3 is null || deltaMatched is true)
                             && (command.ExpectedVolumeMm3 is null || volumeMatched is true);
        var geometryConfirmed = declared && expectationsOk && readBacksOk && readBack is not null;

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            HoleFamily,
            string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
            featuresAfter,
            volumeBefore,
            volumeAfter,
            readBack?.DepthMm,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>The mode of an existing hole by the read <c>IHole3D.HoleType</c>.</summary>
    /// <remarks>An unknown value returns <c>null</c>, not a "similar" mode: an edit in a foreign mode
    /// would write numbers into an interface the object does not read and the volume would not change —
    /// i.e. the refusal would look like a completed edit.</remarks>
    private static HoleMode? HoleModeOfRead(HoleReadDto? read) => read?.HoleType switch
    {
        "ksHTBase" => HoleMode.BlindFlat,
        "ksHTCounterbore" => HoleMode.ThroughCounterbore,
        "ksHTCountersinking" => HoleMode.ThroughCountersink,
        _ => null,
    };

    /// <summary>"Fields ↔ mode" on EDIT: each mode has its own set, and a foreign field is refused by
    /// name before COM.</summary>
    /// <remarks>The same rule as on creation (<see cref="ValidateHoleMode"/>) and for the same reason:
    /// mode numbers of different modes live in DIFFERENT parameter interfaces, so "accepted what we
    /// could, ignored the rest" is silently wrong geometry. Positivity is checked on a MEASURED basis:
    /// KOMPAS accepts a zero diameter and builds a feature with no material at unchanged volume (the
    /// same defect as a zero chamfer leg, F.12).</remarks>
    private static void ValidateHoleEdit(UpdateFeatureCommand command, HoleMode mode)
    {
        switch (mode)
        {
            case HoleMode.BlindFlat:
                RejectForeignHoleEditField(command, "blind_flat", "counterbore_diameter_mm",
                    command.CounterboreDiameterMm);
                RejectForeignHoleEditField(command, "blind_flat", "counterbore_depth_mm",
                    command.CounterboreDepthMm);
                RejectForeignHoleEditField(command, "blind_flat", "countersink_diameter_mm",
                    command.CountersinkDiameterMm);
                RejectForeignHoleEditField(command, "blind_flat", "countersink_angle_deg",
                    command.CountersinkAngleDeg);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "blind_flat");
                PositiveHoleEditValue("depth_mm", command.DepthMm, "blind_flat");
                break;

            case HoleMode.ThroughCounterbore:
                RejectForeignHoleEditField(command, "through_counterbore", "depth_mm", command.DepthMm);
                RejectForeignHoleEditField(command, "through_counterbore", "countersink_diameter_mm",
                    command.CountersinkDiameterMm);
                RejectForeignHoleEditField(command, "through_counterbore", "countersink_angle_deg",
                    command.CountersinkAngleDeg);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "through_counterbore");
                PositiveHoleEditValue("counterbore_diameter_mm", command.CounterboreDiameterMm,
                    "through_counterbore");
                PositiveHoleEditValue("counterbore_depth_mm", command.CounterboreDepthMm,
                    "through_counterbore");
                break;

            case HoleMode.ThroughCountersink:
                RejectForeignHoleEditField(command, "through_countersink", "depth_mm", command.DepthMm);
                RejectForeignHoleEditField(command, "through_countersink", "counterbore_diameter_mm",
                    command.CounterboreDiameterMm);
                RejectForeignHoleEditField(command, "through_countersink", "counterbore_depth_mm",
                    command.CounterboreDepthMm);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "through_countersink");
                PositiveHoleEditValue("countersink_diameter_mm", command.CountersinkDiameterMm,
                    "through_countersink");
                PositiveHoleEditValue("countersink_angle_deg", command.CountersinkAngleDeg,
                    "through_countersink");
                break;

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Режим '{mode}' не реализован: измерены blind_flat, through_counterbore и " +
                    "through_countersink.",
                    RetryPolicy.Never);
        }
    }

    private static void RejectForeignHoleEditField(
        UpdateFeatureCommand command,
        string mode,
        string field,
        double? value)
    {
        if (value is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — отверстие режима {mode}: поле {field} принадлежит ДРУГОМУ режиму, и " +
                "принять его значило бы выдать за применённый параметр, которого этот режим не " +
                "читает. Свои поля: " + HoleOwnFields(mode) + ". Смена режима существующего " +
                "отверстия правкой не выполняется: этот маршрут не измерялся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["family"] = HoleFamily,
                    ["hole_mode"] = mode,
                    ["foreign_fields"] = new[] { field },
                });
        }
    }

    private static string HoleOwnFields(string mode) => mode switch
    {
        "blind_flat" => "diameter_mm, depth_mm",
        "through_counterbore" => "diameter_mm, counterbore_diameter_mm, counterbore_depth_mm",
        "through_countersink" => "diameter_mm, countersink_diameter_mm, countersink_angle_deg",
        _ => "нет",
    };

    private static void PositiveHoleEditValue(string field, double? value, string mode)
    {
        if (value is double number && (number <= 0d || double.IsNaN(number) || double.IsInfinity(number)))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — отверстие режима {mode}: поле {field} обязано быть положительным конечным " +
                "числом. Нулевой диаметр КОМПАС принимает и строит признак без материала при " +
                "неизменном объёме (измерено на создании, тот же класс, что нулевой катет фаски F.12), " +
                "то есть «принято» здесь не означает «применено».",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = field, ["value"] = value });
        }
    }
}

/// <summary>Result of a native hole. Diameter and depth are returned read back from the model, not
/// passed in: "we called Update()" is not a geometric fact. <c>feature_ref</c> is empty when the
/// feature is created but not visible in the API5 tree: a reference an edit would fail through anyway
/// is more honest than a warning. <c>countersink_depth_mm</c> is what the object returned, not what was
/// written: with the "diameter + angle" method this property is derived (M.3).</summary>
public sealed record HoleResult(
    ReferenceDto? FeatureRef,
    string Mode,
    double? DiameterReadBackMm,
    double? DepthReadBackMm,
    double? CountersinkDepthReadBackMm,
    double[]? CenterMm,
    int BodyCount,
    double? VolumeMm3,
    VerificationDto Verification,
    string PlacementRoute);
