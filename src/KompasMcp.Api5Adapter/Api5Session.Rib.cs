using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Rib — a stiffening rib built on an open sketch chain (docs/05 SM-14).</summary>
/// <remarks>DOC: <c>obj3dtype.html</c> — 44 = <c>o3d_ribOperation</c>, «операция "ребро жесткости"»,
/// <c>ksRibDefinition</c>; the definition is obtained through <c>ksEntity::GetDefinition</c>
/// (<c>ksribdefinition.html</c>), and the thickness lives only there — <c>SetThinParam</c>
/// (<c>ksribdefinition_setthinparam.html</c>), because <c>irib_propers.html</c> lists no thickness on
/// <c>IRib</c>.
/// MEASURED: the entity <c>NewEntity(44)</c> returns enters the tree only after <c>Create()</c>, so the
/// handle is held rather than searched for.
/// INVARIANT: <c>Create()/Update() = true</c> is "accepted", not "applied" — the rib is confirmed by the
/// volume against the chain's own area times the thickness, and a call that leaves the body unchanged is
/// refused instead of reported as success.
/// LIMIT: <c>body</c> is refused, not ignored — the definition has no body member.
/// History: docs/decisions/ribs.md#routes</remarks>
public partial class Api5Session
{
    /// <summary>Rib — <c>o3d_ribOperation</c>.</summary>
    private const short RibOperation = 44;

    /// <summary><c>dtNormal</c> — «прямое направление (для тонкой стенки - наружу)», the thin-wall mode of
    /// this order: one thickness, material on one side.</summary>
    private const short ThinTypeNormal = 0;

    /// <summary><c>dtBoth</c> — «в обе стороны»: the symmetric mode, the same thickness on both sides of the
    /// sketch plane.</summary>
    private const short ThinTypeBoth = 2;

    /// <summary>Rib: one thickness, a given position and draft (SM-14).</summary>
    public RibResult Rib(RibCommand command)
    {
        ValidateRibCommand(command);

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var part = document.PartNow();

        // The chain is analysed BEFORE COM: the kernel refuses a closed or branching contour at
        // SetSketch with a bare false, and the caller cannot tell that from "the geometry is wrong".
        var chain = ReadRibChain(document, target.Sketch);
        if (chain is { IsChain: false })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Эскиз ребра не годится: " + chain.Reason + ".",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["segments"] = chain.SegmentCount,
                    ["vertices"] = chain.PointCount,
                });
        }

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var facesBefore = CountFaces(document);
        var boxBefore = part.GetMainBody() is ksBody bodyBefore ? ReadBodyBox(bodyBefore) : null;

        if (part.NewEntity(RibOperation) is not ksEntity entity
            || entity.GetDefinition() is not ksRibDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение ребра не получено: NewEntity(44) вернул объект, у которого нет "
                + "ksRibDefinition. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var sketchAccepted = SafeBool(() => definition.SetSketch(target.Sketch));
        if (sketchAccepted != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetSketch вернул " + (sketchAccepted is null ? "ошибку вызова" : "false")
                + ": эскиз ребра не принят ядром. Замкнутый контур отвергается именно здесь, но "
                + "контур этого вызова разобран сервером как одна разомкнутая цепочка — отказ "
                + "пришёл от ядра. Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        definition.side = command.Side;
        definition.angle = command.DraftAngleDeg;
        definition.index = command.SegmentIndex;

        var thinType = command.Symmetric ? ThinTypeBoth : ThinTypeNormal;
        var reverseThickness = command.Symmetric ? command.ThicknessMm : 0d;
        var thinAccepted = SafeBool(() =>
            definition.SetThinParam(thinType, command.ThicknessMm, reverseThickness));
        if (thinAccepted != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetThinParam(" + thinType + ", " + Num(command.ThicknessMm) + ", "
                + Num(reverseThickness) + ") вернул "
                + (thinAccepted is null ? "ошибку вызова" : "false")
                + ": толщина ребра не задана. Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["thickness_mm"] = command.ThicknessMm,
                    ["symmetric"] = command.Symmetric,
                });
        }

        // The documented reason route is cleared BEFORE the call, so a code read after a refusal is this
        // call's own and not a stale one from an earlier operation.
        ClearKompasResult(document);
        var created = SafeBool(entity.Create);
        if (created != true)
        {
            var details = new Dictionary<string, object?>
            {
                ["named_code"] = "GEOMETRY_FAILED",
                ["side"] = command.Side,
                ["contour_area_mm2"] = chain?.AreaMm2,
                ["segment_index"] = command.SegmentIndex,
                ["draft_angle_deg"] = command.DraftAngleDeg,
            };
            AddKompasFailure(details, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Create() ребра вернул " + (created is null ? "ошибку вызова" : "false")
                + ": тело не построено. Измерено: контур целиком вне тела даёт именно этот исход, и "
                + "он отказ, а не частичный результат; положение side, не строящее на направлении "
                + "этой цепочки, даёт тот же ответ.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        SafeBool(entity.Update);
        part.RebuildModel();
        SettleAfterMutation(document);

        // INVARIANT: the reference carries the revision AFTER creation - minted before the bump, the
        // published revision is one behind the result.
        // History: docs/decisions/adapter-features.md#feature-reference-points-at-the-tree
        BumpRevision(document, "rib.create");
        var reference = References.Register("feature", document.Id, document.Revision, entity);

        // The feature's OWN state decides, not Create()=true: a rib can stay in the tree carrying a
        // kernel error. The reference is registered above, so the refusal names the feature to remove.
        // History: docs/decisions/adapter-features.md#feature-error-refusal
        RequireNoFeatureError(document, entity, reference.Id, "Ребро жёсткости");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);
        var boxAfter = part.GetMainBody() is ksBody bodyAfter ? ReadBodyBox(bodyAfter) : null;

        double? added = volumeBefore is double before && volumeAfter is double after ? after - before : null;
        var expected = RibChain.ExpectedVolumeMm3(chain?.AreaMm2, command.ThicknessMm, command.Symmetric);

        // MEASURED: "accepted" and "applied" differ exactly here - the kernel answers true while the
        // body does not change, so a silent body is a refusal, not a success with zero effect.
        if (added is double grown && grown <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ребро принято (Create() = true), но тело не изменилось: прирост объёма "
                + Num(grown) + " мм³. Это отказ, а не успех без изменения — материал не добавлен.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["volume_added_mm3"] = grown,
                    ["side"] = command.Side,
                });
        }

        var parameters = ReadRibParameters(document, definition);
        var expectedThinType = command.Symmetric ? "both" : "normal";

        // The read-back is the only witness that the MODE was applied: SetThinParam answers before the
        // geometry exists, so an accepted-but-ignored mode would look like a completed operation.
        if (parameters.ThinType is string appliedThinType
            && !string.Equals(appliedThinType, expectedThinType, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Режим тонкой стенки не применён: запрошено " + expectedThinType + " (SetThinParam("
                + thinType + ", " + Num(command.ThicknessMm) + ", " + Num(reverseThickness) + ")), а модель "
                + "читает thin_type = " + appliedThinType + ". Это отказ, а не успех без изменения.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["thin_type_read"] = appliedThinType,
                    ["thin_type_expected"] = expectedThinType,
                    ["symmetric"] = command.Symmetric,
                });
        }

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Create() вернул true"),
            new("sketch_accepted", sketchAccepted == true, "SetSketch принял эскиз"),
            new("thin_param_accepted", thinAccepted == true,
                "SetThinParam(" + thinType + ", " + Num(command.ThicknessMm) + ", "
                + Num(reverseThickness) + ") вернул true"),
            new("body_count_kept", bodiesAfter == bodiesBefore,
                Observed: "тел " + bodiesBefore + " → " + bodiesAfter,
                Expected: "ребро приклеивается к телу: число тел не меняется"),
            new("face_count_grew", facesAfter > facesBefore,
                Observed: Num(facesBefore) + " → " + Num(facesAfter),
                Expected: "у ребра появились свои грани"),
        };

        var unverified = new List<string>();

        if (chain is null)
        {
            unverified.Add("chain_not_analysed — цепочка эскиза не прочитана из модели, поэтому "
                + "площадь контура и ожидаемый объём не посчитаны; ядро при этом контур приняло");
        }
        else if (command.DraftAngleDeg != 0d)
        {
            // MEASURED: a draft ADDS material, so S × t stops being the expectation. The volume is still
            // reported and compared with "it grew", not with a figure nobody measured.
            unverified.Add("volume_expectation_skipped_due_to_draft — при уклоне "
                + Num(command.DraftAngleDeg) + "° прирост больше S × t (измерено: 6-мм ребро даёт "
                + "2400 мм³ при 0°, 2663.389 при 2° и 3059.881 при 5°), поэтому ожидание по площади "
                + "не применялось: подтверждено только «объём вырос»");
        }
        else if (expected is double expectedValue)
        {
            var matches = RibChain.VolumeMatches(expectedValue, added);
            checks.Add(new NamedCheck("volume_matches_contour", matches,
                Observed: Num(added) + " мм³",
                Expected: Num(expectedValue) + " мм³ = площадь контура "
                          + Num(chain.AreaMm2) + " мм² × " + Num(command.ThicknessMm) + " мм"
                          + (command.Symmetric ? " × 2 (симметрично)" : string.Empty) + ", "
                          + "допуск " + (RibChain.VolumeTolerance * 100d).ToString("0.#",
                              System.Globalization.CultureInfo.InvariantCulture) + " %"));
            if (!matches)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Прирост объёма " + Num(added) + " мм³ не совпал с площадью контура × толщину"
                    + (command.Symmetric ? " × 2" : string.Empty) + " (" + Num(expectedValue)
                    + " мм³, допуск "
                    + (RibChain.VolumeTolerance * 100d).ToString("0.#",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " %). Ребро построено не по той цепочке, которую прочитал сервер, либо "
                    + "толщина применена иначе.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["named_code"] = "GEOMETRY_FAILED",
                        ["volume_added_mm3"] = added,
                        ["expected_volume_mm3"] = expectedValue,
                        ["contour_area_mm2"] = chain.AreaMm2,
                    });
            }
        }

        AddBoundsChecks(boxBefore, boxAfter, checks, unverified);
        AddReadBackChecks(parameters, command, checks, unverified);

        return new RibResult(
            ToDto(reference, entity.name),
            command.Side,
            command.ThicknessMm,
            command.DraftAngleDeg,
            command.SegmentIndex,
            chain?.AreaMm2,
            expected,
            added,
            bodiesAfter,
            volumeAfter,
            boxAfter,
            parameters,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>());
    }

    /// <summary>The box must GROW and never shrink: a rib adds material outside the body, so a box that
    /// moved inward means the material went somewhere other than the side the caller asked for. WHICH axis
    /// grew is reported, not predicted — the side→axis correspondence needs the sketch frame, and a
    /// predicted axis would be the server's own geometry rather than the model's.</summary>
    private static void AddBoundsChecks(
        BoundingBoxDto? before,
        BoundingBoxDto? after,
        List<NamedCheck> checks,
        List<string> unverified)
    {
        if (before is null || after is null
            || before.MinMm.Count < 3 || before.MaxMm.Count < 3
            || after.MinMm.Count < 3 || after.MaxMm.Count < 3)
        {
            unverified.Add("bounds_not_compared — габарит до или после операции не прочитан, поэтому "
                + "«габарит вырос» не проверено, а не опровергнуто");
            return;
        }

        var axes = new[] { "x", "y", "z" };
        var grown = new List<string>();
        var shrunk = new List<string>();
        for (var axis = 0; axis < 3; axis++)
        {
            var low = before.MinMm[axis] - after.MinMm[axis];
            var high = after.MaxMm[axis] - before.MaxMm[axis];
            if (low > 1e-6 || high > 1e-6)
            {
                grown.Add(axes[axis] + (low > 1e-6 ? " min" : string.Empty)
                    + (high > 1e-6 ? " max" : string.Empty));
            }

            if (low < -1e-6 || high < -1e-6)
            {
                shrunk.Add(axes[axis]);
            }
        }

        checks.Add(new NamedCheck("bounds_grew", grown.Count > 0 && shrunk.Count == 0,
            Observed: "габарит " + BoxText(before) + " → " + BoxText(after)
                      + (grown.Count > 0 ? "; вырос по " + string.Join(", ", grown) : "; не вырос")
                      + (shrunk.Count > 0 ? "; уменьшился по " + string.Join(", ", shrunk) : string.Empty),
            Expected: "ребро добавляет материал наружу: габарит растёт и не уменьшается"));
    }

    /// <summary>The parameters as the MODEL holds them, right after creation. The read-back is what tells
    /// "the value was applied" from "the call returned": the setters answer before the geometry exists.
    /// </summary>
    private RibDto ReadRibParameters(DocumentEntry document, ksRibDefinition definition)
    {
        var side = SafeInt(() => definition.side);
        var angle = SafeDouble(() => definition.angle);
        var index = SafeInt(() => definition.index);

        int? thinType = null;
        double? normal = null;
        double? reverse = null;
        try
        {
            if (definition.GetThinParam(out var type, out var forward, out var backward))
            {
                thinType = type;
                normal = forward;
                reverse = backward;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            thinType = null;
        }

        return new RibDto(
            side,
            angle,
            index,
            thinType is null ? null : ThinTypeName(thinType.Value),
            normal,
            reverse,
            SketchRefOfRib(document, definition));
    }

    /// <summary>The sketch of the rib as a reference, derived from the definition.</summary>
    private string? SketchRefOfRib(DocumentEntry document, ksRibDefinition definition)
    {
        try
        {
            return AsInterface<ksEntity>(definition.GetSketch()) is ksEntity sketch
                ? References.Register("sketch", document.Id, document.Revision, sketch).Id
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Compare the read-back with the request. A mismatch is a NAMED unverified aspect, not a
    /// silent pass: the parameters decide whether an edit of this rib is expressible at all.</summary>
    private static void AddReadBackChecks(
        RibDto parameters,
        RibCommand command,
        List<NamedCheck> checks,
        List<string> unverified)
    {
        checks.Add(new NamedCheck("read_back_side", parameters.Side == command.Side,
            Observed: parameters.Side?.ToString() ?? "не прочитано",
            Expected: command.Side.ToString()));
        checks.Add(new NamedCheck("read_back_angle",
            parameters.AngleDeg is double angle && Math.Abs(angle - command.DraftAngleDeg) <= 1e-6,
            Observed: Num(parameters.AngleDeg) + "°",
            Expected: Num(command.DraftAngleDeg) + "°"));
        checks.Add(new NamedCheck("read_back_segment_index", parameters.SegmentIndex == command.SegmentIndex,
            Observed: parameters.SegmentIndex?.ToString() ?? "не прочитано",
            Expected: command.SegmentIndex.ToString()));
        checks.Add(new NamedCheck("read_back_thickness",
            parameters.NormalThicknessMm is double thickness
            && Math.Abs(thickness - command.ThicknessMm) <= 1e-6,
            Observed: Num(parameters.NormalThicknessMm) + " мм",
            Expected: Num(command.ThicknessMm) + " мм"));

        // The mode is a PAIR: the type says which sides, the reverse thickness says how much of the
        // second one. Checking only the type would pass a symmetric rib whose second side is empty.
        var expectedThinType = command.Symmetric ? "both" : "normal";
        checks.Add(new NamedCheck("read_back_thin_type",
            string.Equals(parameters.ThinType, expectedThinType, StringComparison.Ordinal),
            Observed: parameters.ThinType ?? "не прочитано",
            Expected: expectedThinType));

        if (command.Symmetric)
        {
            checks.Add(new NamedCheck("read_back_reverse_thickness",
                parameters.ReverseThicknessMm is double reverse
                && Math.Abs(reverse - command.ThicknessMm) <= 1e-6,
                Observed: Num(parameters.ReverseThicknessMm) + " мм",
                Expected: Num(command.ThicknessMm) + " мм"));
        }

        if (parameters.SketchRef is null)
        {
            unverified.Add("rib_sketch_not_resolved — GetSketch() ребра не отдал эскиз: ссылка на "
                + "эскиз не выпущена, поэтому пересоздать ребро по ней нельзя");
        }
    }

    /// <summary>Read the sketch chain FROM THE MODEL and analyse it. <c>null</c> means "not read" and is
    /// named by the caller: a chain that could not be read is not a chain that is absent.</summary>
    private RibChain.Outcome? ReadRibChain(DocumentEntry document, ksEntity sketch)
    {
        SketchEntitiesRead? read;
        try
        {
            (read, _) = Api7SketchEntities.Read(BridgeFor(document), sketch, 500);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return null;
        }

        if (read is null || read.Rows.Count == 0)
        {
            return null;
        }

        var segments = new List<RibSegment>(read.Rows.Count);
        foreach (var row in read.Rows)
        {
            // A chain is lines only: an arc's area is not the shoelace of its ends, so the analysis stops
            // here and says so instead of treating the chord as the curve.
            if (row.RadiusMm is not null
                || row.X1Mm is not double x1 || row.Y1Mm is not double y1
                || row.X2Mm is not double x2 || row.Y2Mm is not double y2)
            {
                return null;
            }

            segments.Add(new RibSegment(x1, y1, x2, y2));
        }

        return RibChain.Analyze(segments);
    }

    /// <summary>Thin-wall mode name. DOC: <c>direction_type.html</c> — 0 <c>dtNormal</c>, 1
    /// <c>dtReverse</c>, 2 <c>dtBoth</c>, 3 <c>dtMiddlePlane</c>; an unknown number is returned as a
    /// number, not turned into null.</summary>
    private static string ThinTypeName(int value) => value switch
    {
        0 => "normal",
        1 => "reverse",
        2 => "both",
        3 => "middle_plane",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM. The cost of a mistake is
    /// asymmetric: a spurious refusal is seen at once, while an accepted-and-ignored number survives
    /// to acceptance looking like a completed operation.</summary>
    private static void ValidateRibCommand(RibCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SketchRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Эскиз не задан: ребро строится по разомкнутой цепочке конкретного эскиза.",
                RetryPolicy.Never);
        }

        if (command.Side is < 0 or > 3)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "side = " + command.Side + " вне набора ksRibSideEnum: 0 left, 1 right, 2 up, 3 down. "
                + "Других положений ребра в документации нет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["side"] = command.Side });
        }

        if (!double.IsFinite(command.ThicknessMm) || command.ThicknessMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Толщина ребра должна быть положительным конечным числом, а получено "
                + Num(command.ThicknessMm) + " мм.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["thickness_mm"] = command.ThicknessMm });
        }

        if (!double.IsFinite(command.DraftAngleDeg) || Math.Abs(command.DraftAngleDeg) >= 90d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона ребра задаётся в ГРАДУСАХ и по модулю меньше 90, а получено "
                + Num(command.DraftAngleDeg) + "°: при 90° грань ребра вырождается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["draft_angle_deg"] = command.DraftAngleDeg });
        }

        if (command.SegmentIndex < 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "segment_index = " + command.SegmentIndex + " отрицателен: сегменты нумеруются с нуля, "
                + "0 — первый сегмент эскиза.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["segment_index"] = command.SegmentIndex });
        }

        if (command.BodyRef is not null)
        {
            // The parameter IS declared in the schema (otherwise it would be invisible to the product)
            // but is NOT executed here: API5 ksRibDefinition has no body member at all, and accepting
            // it silently would return success for work that never happened.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "body не задаётся: у API5 ksRibDefinition члена «тело» нет вовсе (маршрут этого "
                + "инструмента — API5 o3d_ribOperation=44), а многотельная адресация ребра в "
                + "обязательный объём этого наряда не входит. Не передавайте поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["body_ref"] = command.BodyRef,
                    ["api5_member_present"] = false,
                });
        }
    }
}
