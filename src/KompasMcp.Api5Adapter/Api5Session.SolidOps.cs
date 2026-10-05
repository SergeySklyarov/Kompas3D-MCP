using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>B3 body operations: boolean (SM-15), split and cut (SM-16), reposition and rotation
/// (SM-17).</summary>
/// <remarks>INVARIANT: the basis is measurement, not member names — three isolated probes of
/// 18.09.2026 (<c>--boolean</c>, <c>--split</c>, <c>--reposition</c>; logs in
/// <c>docs/acceptance/api7/</c>) confirmed the routes and, more importantly, their LIMITS; only what
/// is measured is carried over below, and the unverified is named unverified.
/// INVARIANT: the checks run before the COM call — the kernel neither rejects a repeated reference
/// nor checks that the target is not among the tools (MEASURED, step BO.9: a repeat is accepted
/// silently, bodies 3→2), so both checks live here, before COM, rather than relying on the kernel.
/// INVARIANT: a successful <c>Update()</c> is not proof — on reposition three routes out of four
/// returned <c>true</c> and did not move the body (step RP.2), so every handler re-reads the model
/// after the call and returns the ACTUAL state, not the promised one.</remarks>
public sealed partial class Api5Session
{
    /// <summary>Name of the boolean-operation family in references and responses.</summary>
    private const string BooleanRefKind = "solid_boolean";

    private const string SplitRefKind = "solid_split";

    private const string CutRefKind = "solid_cut";

    private const string RepositionRefKind = "solid_reposition";

    // ══════════════════════════════════════════════════════════════════════════════════ SM-15 ══

    /// <summary>A boolean operation on bodies: explicit target, explicit tool set, operation kind
    /// and tool-preservation policy.</summary>
    public BooleanResultDto SolidBoolean(BooleanCommand command)
    {
        if (command.ToolBodyRefs is not { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "tool_body_refs пуст — булевой операции нужен хотя бы один инструмент.",
                details: new Dictionary<string, object?> { ["target_body_ref"] = command.TargetBodyRef });
        }

        var document = RequireDocument(command.DocumentId);
        GuardBooleanRefs(command.TargetBodyRef, command.ToolBodyRefs);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.boolean");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);

        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var tools = new List<BodyTarget>(command.ToolBodyRefs.Count);
        foreach (var toolRef in command.ToolBodyRefs)
        {
            var tool = ResolveBodyTarget(document, part, toolRef, bodiesBefore);
            if (tool.Index == target.Index)
            {
                // The kernel rejects this case (step BO.9), but only AFTER the attempt; the check
                // here gives the caller the culprit's name rather than "Update() returned false".
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Тело '{toolRef}' указано и целью, и инструментом.",
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = command.TargetBodyRef,
                        ["tool_body_ref"] = toolRef,
                    });
            }

            tools.Add(tool);
        }

        if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject target7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело-цель не переносится в API7 как IKompasAPIObject: " +
                (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                RetryPolicy.ReacquireContext);
        }

        var tools7 = new object[tools.Count];
        for (var i = 0; i < tools.Count; i++)
        {
            if (bridge.TransferTo7(tools[i].RawElement) is not { } tool7)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Инструмент {i} ('{command.ToolBodyRefs[i]}') не переносится в API7: " +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                    RetryPolicy.ReacquireContext);
            }

            tools7[i] = tool7;
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidBoolean.TryCreate(
            container, target7, tools7, command.Operation, command.KeepTools, name: null);

        if (created.Feature is null)
        {
            // A kernel refusal is a FACT about the geometry, not an adapter fault, and the model
            // state after it is reported as it is: "accepted" and "refused, but the model changed"
            // are different things.
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Булева операция не построена: " + created.Failure +
                ". Ядро отвергает несвязные тела, касание по ребру и касание в точке (измерено).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["operation"] = command.Operation.ToString().ToLowerInvariant(),
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                    ["volume_before"] = SumVolumes(bodiesBefore),
                    ["volume_after"] = SumVolumes(after),
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.boolean");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.BooleanOperation, "solid.boolean");
        var resultRef = References.Register(BooleanRefKind, document.Id, document.Revision, address.Entity).Id;
        var totalVolume = rows.Sum(r => r.VolumeMm3 ?? 0d);

        var consumed = new List<string> { command.TargetBodyRef };
        consumed.AddRange(command.ToolBodyRefs);

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is double expected && !VolumeMatches(totalVolume, expected))
        {
            // The expectation is named but not confirmed. This is NOT a reason to weaken the check
            // nor to declare success: the mismatch goes into the response as an unverified aspect.
            unverified.Add("expected_volume_mismatch");
        }

        return new BooleanResultDto
        {
            FeatureRef = resultRef,
            Operation = command.Operation.ToString().ToLowerInvariant(),
            KeepTools = command.KeepTools,
            ResultBodies = rows,
            SavedTools = command.KeepTools ? MatchSavedTools(rows, tools) : Array.Empty<SolidBodyDto>(),
            ConsumedInputs = consumed,
            TotalVolumeMm3 = totalVolume,
            VolumeNote = "Сумма индивидуальных объёмов всех тел документа. Объём пространственного "
                + "объединения — другая величина, и смешивать их нельзя.",
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    /// <summary>Addressing checks the kernel does not make: the target is not among the tools and
    /// there are no repeats.</summary>
    /// <remarks>MEASURED 18.09.2026 (step BO.9): the kernel ACCEPTS a repeated reference silently
    /// (bodies 3→2, total volume 49 000→37 000), so repeat detection must live in the
    /// contract.</remarks>
    private static void GuardBooleanRefs(string targetBodyRef, IReadOnlyList<string> toolBodyRefs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < toolBodyRefs.Count; i++)
        {
            var reference = toolBodyRefs[i];
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"tool_body_refs[{i}] пуст — инструмент обязан быть адресован ссылкой.");
            }

            if (string.Equals(reference, targetBodyRef, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Тело '{reference}' указано и целью, и инструментом.",
                    details: new Dictionary<string, object?> { ["target_body_ref"] = targetBodyRef });
            }

            if (!seen.Add(reference))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' повторяется в tool_body_refs. Ядро такой повтор принимает "
                    + "молча и потребляет тело дважды, поэтому повтор отвергается здесь.",
                    details: new Dictionary<string, object?>
                    {
                        ["tool_body_ref"] = reference,
                        ["tool_body_refs"] = toolBodyRefs,
                    });
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════ SM-16 ══

    /// <summary>Splitting a body by a plane. ALL resulting parts are returned, each with its own
    /// reference.</summary>
    /// <remarks>MEASURED (step SP.2): no separate "which parts to keep" choice is needed — the split
    /// keeps all parts by construction (a 24 000 bar → 6 000 and 18 000, sum 24 000). It is this
    /// measurement that lifted blocker OQ-A18, not a discovered member.</remarks>
    public SplitResultDto SolidSplit(SplitCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.split");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var targetIndex = target.Index;

        var plane = ResolveCutPlane(document, bridge, container, command.Plane, out var planeFailure);
        if (plane.Plane is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Плоскость разделения не построена: " + planeFailure,
                RetryPolicy.SameOperationId);
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidSplit.TryCreate(container, new object[] { plane.Plane }, name: null);
        if (created.Feature is null)
        {
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Разделение не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.split");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.SplitSolid, "solid.split");
        var featureRef = References.Register(SplitRefKind, document.Id, document.Revision, address.Entity).Id;

        // INVARIANT: parts are the bodies that did not exist before the operation; untouched are
        // those that existed and remain. Identification is by GEOMETRY (bounding box AND volume),
        // not by position in the collection. INVARIANT: the target body is EXCLUDED from the
        // matching — the operation consumed it, so matching it proves nothing and would only put
        // the target back into the untouched list.
        // History: docs/decisions/adapter-solid.md#split-parts-selection
        var parts = new List<SolidBodyDto>();
        var untouched = new List<SolidBodyDto>();
        var claimed = new HashSet<int>();
        foreach (var row in rows)
        {
            var match = bodiesBefore.FirstOrDefault(
                b => b.Index != targetIndex && !claimed.Contains(b.Index) && MatchesSnapshot(row, b));
            if (match is null)
            {
                parts.Add(row);
            }
            else
            {
                claimed.Add(match.Index);
                untouched.Add(row);
            }
        }

        var partsVolume = parts.Sum(r => r.VolumeMm3 ?? 0d);

        var unverified = new List<string>();
        if (parts.Count < 2)
        {
            unverified.Add("parts_less_than_two");
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            var beforeVolume = bodiesBefore.FirstOrDefault(b => b.Index == targetIndex)?.Volume;
            if (beforeVolume is double before && !VolumeMatches(partsVolume, before))
            {
                unverified.Add("parts_volume_differs_from_source");
            }
        }

        return new SplitResultDto
        {
            FeatureRef = featureRef,
            Parts = parts,
            UntouchedBodies = untouched,
            PartsVolumeSumMm3 = partsVolume,
            PlaneNormalMm = plane.UnitNormal,
            PlanePointMm = plane.Point,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    /// <summary>Cutting a body to one side of a plane. The side is named by the sign
    /// <c>s = n·(p − p₀)</c>.</summary>
    /// <remarks>MEASURED (step SP.7): <c>ICut.Direction = true</c> keeps the side in the direction of
    /// the normal, i.e. <c>s &gt; 0</c>.</remarks>
    public CutByPlaneResultDto SolidCutByPlane(CutByPlaneCommand command)
    {
        var keepPositive = command.KeepSide switch
        {
            "positive" => true,
            "negative" => false,
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"keep_side '{command.KeepSide}' не распознан: ожидалось 'positive' (s > 0) или 'negative' (s < 0).",
                details: new Dictionary<string, object?> { ["keep_side"] = command.KeepSide }),
        };

        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.cut_by_plane");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var targetIndex = target.Index;

        var plane = ResolveCutPlane(document, bridge, container, command.Plane, out var planeFailure);
        if (plane.Plane is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Плоскость отсечения не построена: " + planeFailure,
                RetryPolicy.SameOperationId);
        }

        var target7 = bridge.TransferTo7(target.RawElement);
        if (target7 is not IKompasAPIObject targetBody)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело-цель не перенесено в API7 как IKompasAPIObject: "
                + (bridge.BridgeFailure ?? "TransferInterface вернул null")
                + ". Отсечение БЕЗ названного тела снимает материал у ВСЕХ тел документа: справка "
                + "продукта (rezultat_oper_v_zavisimosti_ot_s_o.html) называет умолчанием «Все "
                + "объекты», и объект, ЦЕЛИКОМ лежащий со стороны отсечения, в область применения "
                + "входит. Признак с незапрошенной областью не создаётся.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["target_body_ref"] = command.TargetBodyRef,
                    ["target_body_index"] = targetIndex,
                    ["code"] = "target_body_not_transferred",
                });
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidCut.TryCreateByPlane(
            container, plane.Plane, keepPositive, name: null, targetBody);
        if (created.Feature is null)
        {
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["keep_side"] = command.KeepSide,
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.cut_by_plane");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.CutByPlane, "solid.cut_by_plane");
        var featureRef = References.Register(CutRefKind, document.Id, document.Revision, address.Entity).Id;

        // INVARIANT: the remainder is identified by MATCHING THE BODY COMPOSITION, not by "the first
        // whose volume differs from the target". INVARIANT: snapshot matching also sees VANISHED
        // bodies — exactly what the former check missed.
        // History: docs/decisions/adapter-solid.md#cut-remainder-identification
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var remainingSnapshot = changes.MatchedOf(targetIndex);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var otherMoved = changes.UnchangedViolations(targetIndex);
        var createdBodies = changes.NewBodies;

        var remaining = remainingSnapshot is null
            ? null
            : rows.FirstOrDefault(r => MatchesSnapshot(r, remainingSnapshot));
        if (remaining is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "После отсечения тело-цель не найдено: сопоставление состава тел не дало ему пары. "
                + "Это отказ, а не «остаток нулевого объёма» — объявить нечего.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = rows.Count,
                    ["vanished_bodies"] = vanished.Count,
                    ["body_volumes_after"] = BodyVolumesText(rows),
                });
        }

        // ADDRESSING IS THE SUBJECT OF THIS CALL, and it is checked, not assumed: ONE body was
        // named, so no other body has the right to change, vanish or appear.
        if (vanished.Count > 0 || otherMoved.Count > 0 || createdBodies.Count > 0)
        {
            var parts = new List<string>();
            if (vanished.Count > 0)
            {
                parts.Add("исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index)));
            }

            if (otherMoved.Count > 0)
            {
                parts.Add("изменились посторонние тела: " + string.Join("; ", otherMoved));
            }

            if (createdBodies.Count > 0)
            {
                parts.Add("появились новые тела " + string.Join(", ", createdBodies.Select(b => "тел" + b.Index)));
            }

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение затронуло не только названное тело: " + string.Join("; ", parts)
                + ". Мутация уже выполнена — откат не обещается. Фактический состав тел: "
                + BodyVolumesText(rows) + "; ревизия после мутации " + document.Revision
                + ". Прочитайте состав тел и повторите операцию на исправленном состоянии.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["target_body_index"] = targetIndex,
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["changed_nontarget_bodies"] = otherMoved.ToArray(),
                    ["created_bodies"] = createdBodies.Select(b => b.Index).ToArray(),
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["code"] = "cut_not_addressed_to_target_body",
                });
        }

        var untouched = rows.Where(r => !ReferenceEquals(r, remaining)).ToList();
        var targetMoved = changes.DeltaOf(targetIndex) is double moved
                          && Math.Abs(moved) > VolumeChangeFloorMm3;

        var checks = new List<NamedCheck>
        {
            new("target_body_affected", targetMoved,
                Observed: "тел" + targetIndex + ": ΔV=" + Num(changes.DeltaOf(targetIndex)),
                Expected: "изменение объёма названного тела"),
            new("nontarget_bodies_unchanged", otherMoved.Count == 0,
                Observed: untouched.Count == 0 ? "<посторонних тел нет>" : BodyVolumesText(untouched),
                Expected: "объёмы посторонних тел не изменились"),
            new("no_bodies_vanished", vanished.Count == 0,
                Observed: Num(vanished.Count), Expected: "0"),
            new("no_bodies_created", createdBodies.Count == 0,
                Observed: Num(createdBodies.Count), Expected: "0"),
        };

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is double expected
            && remaining.VolumeMm3 is double actual)
        {
            var matched = VolumeMatches(actual, expected);
            checks.Add(new NamedCheck(
                "volume_expected",
                matched,
                Observed: Num(remaining.VolumeMm3),
                Expected: Num(expected)));
            if (!matched)
            {
                unverified.Add("expected_volume_mismatch — объявленный объём остатка "
                               + Num(expected) + " не совпал с измеренным " + Num(remaining.VolumeMm3));
            }
        }
        else
        {
            unverified.Add("no_expectation_supplied — без expected_volume_mm3 отсечение не может "
                           + "быть подтверждено геометрически: адресность проверена по составу тел, "
                           + "а объём остатка — нет");
        }

        if (!targetMoved)
        {
            unverified.Add("target_body_unchanged — объём названного тела не изменился: отличить "
                           + "«плоскость не пересекает тело» от «запись не применилась» составом тел "
                           + "нельзя");
        }

        return new CutByPlaneResultDto
        {
            FeatureRef = featureRef,
            Remaining = remaining,
            KeptSide = keepPositive ? "positive" : "negative",
            PlaneNormalMm = plane.UnitNormal ?? Array.Empty<double>(),
            PlanePointMm = plane.Point ?? Array.Empty<double>(),
            UntouchedBodies = untouched,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
            Checks = checks,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════ SM-17 ══

    /// <summary>Repositioning a body by a vector or rotating it about an axis. The volume and the
    /// number of bodies are preserved; only the selected body's placement changes.</summary>
    public RepositionResultDto SolidReposition(RepositionCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.reposition");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);

        var matrix = command.Kind switch
        {
            RepositionKind.Translate => BuildTranslation(command),
            RepositionKind.Rotate => BuildRotation(command),
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"kind '{command.Kind}' не распознан.",
                details: new Dictionary<string, object?> { ["kind"] = command.Kind.ToString() }),
        };

        if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject body7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело не переносится в API7 как IKompasAPIObject: " +
                (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                RetryPolicy.ReacquireContext);
        }

        var before = target.Snapshot;
        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidReposition.TryCreate(container, body7, matrix, name: null);
        if (created.Feature is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Преобразование положения не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        RequirePlacementRoundTrip(created.Feature, matrix, command.Kind);

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.reposition");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.BodyRepositionFeature, "solid.reposition");
        var featureRef = References.Register(RepositionRefKind, document.Id, document.Revision, address.Entity).Id;

        // INVARIANT: a successful Update() here is NOT proof — three routes out of four returned
        // true and did not move the body (step RP.2), so the placement is re-read and "did not
        // move" is a refusal, not a success with a zero result. INVARIANT: comparison uses the
        // bounding box computed by the SAME matrix — volume does not change on a translation, so by
        // volume alone "moved" and "stayed" are indistinguishable.
        var after = rows.FirstOrDefault(r => MatchesMoved(r, matrix, before));
        if (after is null)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "IBodyReposition.Update() вернул успех, но тело осталось на прежнем месте. "
                + "На v24 это отдельный измеренный исход: положение пишет только однородная матрица 4×4, "
                + "и молчаливый no-op возможен.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = command.Kind.ToString().ToLowerInvariant(),
                    ["bbox_before"] = before?.Min is null
                        ? null
                        : new[] { before.Min, before.Max },
                });
        }

        var untouched = rows.Where(r => !ReferenceEquals(r, after)).ToList();
        var unverified = new List<string>();
        if (rows.Count != bodiesBefore.Count)
        {
            unverified.Add("body_count_changed");
        }

        return new RepositionResultDto
        {
            FeatureRef = featureRef,
            Kind = command.Kind.ToString().ToLowerInvariant(),
            BboxBefore = Box(before),
            BboxAfter = after.Bbox,
            VolumeMm3 = after.VolumeMm3 ?? double.NaN,
            BodiesBefore = bodiesBefore.Count,
            BodiesAfter = rows.Count,
            UntouchedBodies = untouched,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    private static double[] BuildTranslation(RepositionCommand command)
    {
        if (command.VectorMm is not { Count: 3 } vector)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Перенос требует vector_mm из трёх чисел в модельных координатах, мм.");
        }

        if (command.AxisPointMm is not null || command.AxisDirectionMm is not null
            || command.AxisPoint2Mm is not null || command.AngleDeg is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Перенос не принимает параметров поворота: указаны axis_* или angle_deg.",
                details: new Dictionary<string, object?> { ["kind"] = "translate" });
        }

        try
        {
            return RepositionMatrix.Translate(vector);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, ex.Message);
        }
    }

    private static double[] BuildRotation(RepositionCommand command)
    {
        if (command.AngleDeg is not double angle)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот требует angle_deg в градусах.");
        }

        if (command.VectorMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот не принимает vector_mm: указан вектор переноса.",
                details: new Dictionary<string, object?> { ["kind"] = "rotate" });
        }

        if (command.AxisPointMm is not { Count: 3 } point)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот требует axis_point_mm — точку на оси, в модельных координатах, мм.");
        }

        // INVARIANT: the axis is set by EITHER a direction OR a second point. Both at once or
        // neither is a refusal: "took whatever looked right" here would mean rotating about the
        // wrong axis, and KOMPAS does not err on that.
        var hasDirection = command.AxisDirectionMm is { Count: 3 };
        var hasSecondPoint = command.AxisPoint2Mm is { Count: 3 };
        if (hasDirection == hasSecondPoint)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                hasDirection
                    ? "Ось поворота задана и направлением, и второй точкой — выберите одно."
                    : "Ось поворота не задана: нужен axis_direction_mm либо axis_point2_mm.",
                details: new Dictionary<string, object?>
                {
                    ["has_axis_direction"] = hasDirection,
                    ["has_axis_point2"] = hasSecondPoint,
                });
        }

        double[] direction;
        if (hasDirection)
        {
            direction = new[] { command.AxisDirectionMm![0], command.AxisDirectionMm[1], command.AxisDirectionMm[2] };
        }
        else
        {
            var second = command.AxisPoint2Mm!;
            direction = new[] { second[0] - point[0], second[1] - point[1], second[2] - point[2] };
            if (RigidFrame.Norm(direction) <= 0d)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Две точки оси поворота совпали — направление не определено.",
                    details: new Dictionary<string, object?>
                    {
                        ["axis_point_mm"] = command.AxisPointMm,
                        ["axis_point2_mm"] = command.AxisPoint2Mm,
                    });
            }
        }

        try
        {
            return RepositionMatrix.RotateAboutAxis(
                new[] { point[0], point[1], point[2] }, direction, angle);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, ex.Message);
        }
    }

    /// <summary>A written placement is read back and compared with the requested one BY MATRIX, not
    /// by numbers.</summary>
    /// <remarks>INVARIANT: a successful <c>IBodyReposition.Update()</c> means "accepted", not
    /// "applied" — MEASURED (step RP.2) that three routes out of four returned <c>true</c> and did
    /// not move the body, so creation is additionally confirmed by reading the written parameters
    /// back. INVARIANT: comparison is by matrix, not by the number triple — the Euler-angle
    /// parameterisation is ambiguous (at nutation 0 or 180° the sum of precession and rotation is
    /// defined up to redistribution), so requiring equal numbers would reject a CORRECT write. A
    /// matrix is assembled from the read triple and compared with the requested one:
    /// <see cref="EulerOrientation"/> keeps the conjugation order in one place, and swapping that
    /// order diverges here at once — MEASURED divergence with a foreign order is 1, with the correct
    /// one 0 or 2.2·10⁻¹⁶. LIMIT: the tolerance 10⁻⁶ is six orders below the divergence a wrong
    /// order produces (1) and ten orders above the measured residual of the correct decomposition
    /// (2.2·10⁻¹⁶); it is deliberately wider than machine precision so that rounding is not turned
    /// into a refusal.</remarks>
    private static void RequirePlacementRoundTrip(
        IBodyReposition feature, double[] matrix, RepositionKind kind)
    {
        var read = Api7SolidReposition.ReadPlacement(feature);
        if (read.Reading is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Размещение не перечитывается сразу после записи: " + (read.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        var reading = read.Reading;
        var difference = PlacementDifference(reading, matrix);
        if (difference is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Записанное размещение прочиталось НЕ тем маршрутом, которым записано: "
                + $"OrientationType={reading.OrientationType}, ParameterType={reading.ParameterType}. "
                + "Документ хранит параметры ориентации, поэтому признак без режима углов Эйлера "
                + "означает, что запись не применилась.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["orientation_type"] = reading.OrientationType,
                    ["parameter_type"] = reading.ParameterType,
                    ["kind"] = kind.ToString().ToLowerInvariant(),
                });
        }

        if (difference > PlacementRoundTripTolerance)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Записанное размещение не воспроизводится из прочитанных параметров: расхождение "
                + $"{difference:R} по матрице (вид — {kind.ToString().ToLowerInvariant()}). Это признак "
                + "подменённого порядка спряжения углов Эйлера либо неприменённой записи.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["matrix_difference"] = difference,
                    ["requested_matrix"] = matrix,
                    ["read_angles_deg"] = reading.AnglesDeg,
                    ["read_displacement_mm"] = reading.DisplacementMm,
                });
        }
    }

    /// <summary>The divergence between the requested matrix and the placement ASSEMBLED from the
    /// read parameters. <c>null</c> — the read failed or used a different route (not
    /// "zero").</summary>
    /// <remarks>INVARIANT: <c>null</c> and <c>0</c> are DIFFERENT answers here — zero means "the
    /// read reproduces the requested", <c>null</c> means "the feature has no parameters of this
    /// route"; merging them would take missing data for a match.</remarks>
    private static double? PlacementDifference(
        Api7SolidReposition.PlacementReading? reading, double[] matrix)
    {
        if (reading is null
            || !reading.IsEuler
            || reading.AnglesDeg is not { Length: 3 } angles
            || !reading.HasDisplacement
            || reading.DisplacementMm is not { Length: 3 } displacement)
        {
            return null;
        }

        var restored = EulerOrientation.RotationFromAngles(angles[0], angles[1], angles[2]);
        restored[12] = displacement[0];
        restored[13] = displacement[1];
        restored[14] = displacement[2];
        return EulerOrientation.MaxDifference(restored, matrix);
    }

    /// <summary>Tolerance of the "written → read" matrix check; the rationale is in the doc comment
    /// above.</summary>
    private const double PlacementRoundTripTolerance = 1e-6;

    // ═══════════════════════════════════════════════════════════════════════════════ helpers ══

    /// <summary>The API7 bridge or an explicit <c>CAPABILITY_UNAVAILABLE</c> with a reason. A silent
    /// null is not allowed: the caller must tell "API7 unavailable" from "KOMPAS rejected the
    /// geometry".</summary>
    private IModelContainer RequireContainer(Api7Bridge bridge, DocumentEntry document, string tool)
    {
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is not null)
        {
            return container;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Маршрут API7 для '{tool}' не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
            ". Операция не выполнялась.",
            RetryPolicy.ReacquireContext);
    }

    /// <summary>The FORM of a plane specification: mutually exclusive modes, <c>offset_mm</c> without
    /// <c>base</c>, and the declared refusal on <c>base</c>. Checked before any work with the model,
    /// on both routes — creation and edit.</summary>
    /// <remarks>MEASURED by the B3 client acceptance (19.09.2026, three FAIL rows): the schema's
    /// declared <c>CAPABILITY_UNAVAILABLE</c> on <c>plane.base</c> was UNREACHABLE — the DTO field
    /// shape differed from the published one, and the call failed while parsing the payload with
    /// <c>JsonException</c> and code <c>VERIFICATION_FAILED</c>. The shape is brought in line with
    /// the published one (<c>CutPlaneDto</c>), and this check makes the declared outcome executable
    /// and shared by <c>kompas_split</c>, <c>kompas_cut_by_plane</c> and the applicable
    /// <c>kompas_update_feature</c>. INVARIANT: the check priority is declared and does not depend
    /// on field order in JSON.
    /// <list type="number">
    /// <item><c>base</c> named TOGETHER with another mode (<c>plane_ref</c> or point with normal) —
    /// <c>INVALID_ARGUMENT</c>: the request is contradictory, and answering it with the declared
    /// capability refusal would hide from the client that it named two modes at once;</item>
    /// <item><c>base</c> named alone (with or without an offset) — <c>CAPABILITY_UNAVAILABLE</c>:
    /// exactly what the field description promises;</item>
    /// <item><c>offset_mm</c> without <c>base</c> — <c>INVALID_ARGUMENT</c>: an offset without a base
    /// plane does not express a plane, and accepting the parameter silently would declare it
    /// accepted;</item>
    /// <item><c>plane_ref</c> together with a point or normal — <c>INVALID_ARGUMENT</c> (checked by
    /// the calling route, because edit refuses a reference for its own reason).</item>
    /// </list></remarks>
    private static void GuardCutPlaneForm(CutPlaneDto plane)
    {
        switch (CutPlaneForm.Validate(plane))
        {
            case CutPlaneFormVerdict.Ok:
                return;

            case CutPlaneFormVerdict.ModesConflict:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Плоскость задана базовой плоскостью И другим способом. base объявлен, но не "
                    + "поддержан, поэтому «base плюс точка с нормалью» — это противоречивый запрос, а "
                    + "не объявленный отказ возможности: назовите один способ.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["base"] = plane.Base!.Value.ToString(),
                        ["plane_ref"] = plane.PlaneRef,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                        ["code"] = "plane_modes_conflict",
                    });

            case CutPlaneFormVerdict.BaseUnsupported:
                // Declared unsupported in the schema — refuses with the same code. The offset is
                // named in details: a declared parameter is either honoured or its role is
                // reported, never silently dropped.
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Способ «базовая плоскость + смещение» не измерен на API7 (вспомогательная плоскость "
                    + "со смещением), поэтому не поддержан; задайте плоскость точкой и нормалью.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["base"] = plane.Base!.Value.ToString(),
                        ["offset_mm"] = plane.OffsetMm,
                        ["offset_applied"] = false,
                        ["code"] = "plane_base_unsupported",
                    });

            case CutPlaneFormVerdict.OffsetWithoutBase:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "offset_mm задан без base. Смещение отсчитывается от базовой плоскости, поэтому само "
                    + "по себе оно плоскости не задаёт: укажите base вместе с offset_mm либо задайте "
                    + "плоскость точкой и нормалью.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["offset_mm"] = plane.OffsetMm,
                        ["has_plane_ref"] = plane.PlaneRef is not null,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                        ["code"] = "plane_offset_without_base",
                    });

            default:
                throw new KompasContractException(
                    ErrorCodes.VerificationFailed,
                    $"Правило формы плоскости вернуло неизвестный исход: {CutPlaneForm.Validate(plane)}.",
                    RetryPolicy.Never);
        }
    }

    /// <summary>The plane of the operation: an existing support or a point + normal.</summary>
    /// <remarks>The "base plane + offset" mode is refused with <c>CAPABILITY_UNAVAILABLE</c> and a reason, not
    /// filled in by a guess: the offset auxiliary-plane route of API7 was not measured in a single
    /// run, and the sign of the base plane's normal is exactly what decides which side the operation
    /// cuts away.</remarks>
    /// <remarks>
    /// THREE different outcomes are separated here, and previously they were lumped into one
    /// <c>GEOMETRY_FAILED</c>:
    /// <list type="bullet">
    /// <item>the specification is inexpressible (both forms at once, neither form, a zero or
    /// non-numeric normal) — <c>INVALID_ARGUMENT</c>, fixed by the client;</item>
    /// <item>the specification is expressible but the route is unsupported ("base plane + offset") —
    /// <c>CAPABILITY_UNAVAILABLE</c>, exactly as promised in the schema field description;</item>
    /// <item>the kernel did not build the plane from correct data — <c>GEOMETRY_FAILED</c>, and only
    /// this outcome remains a <c>null</c> return with the reason in <paramref name="failure"/>.</item>
    /// </list>
    /// MEASURED 18.09.2026 by acceptance row B3.17: before this separation a zero normal and a plane
    /// without a normal answered <c>GEOMETRY_FAILED</c>, i.e. "the kernel failed" instead of "the
    /// request is inexpressible". For the client these are different invitations: repeat the operation
    /// versus fix the argument.
    /// History: docs/decisions/adapter-solid.md#plane-form</remarks>
    private (IPlane3D? Plane, double[]? UnitNormal, double[]? Point) ResolveCutPlane(
        DocumentEntry document,
        Api7Bridge bridge,
        IModelContainer container,
        CutPlaneDto plane,
        out string? failure)
    {
        failure = null;

        GuardCutPlaneForm(plane);

        if (plane.PlaneRef is { Length: > 0 } reference)
        {
            if (plane.PointMm is not null || plane.NormalMm is not null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Плоскость задана и ссылкой, и точкой с нормалью — выберите одно.",
                    details: new Dictionary<string, object?>
                    {
                        ["plane_ref"] = reference,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                    });
            }

            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' не несёт объекта плоскости.",
                    details: new Dictionary<string, object?> { ["plane_ref"] = reference });
            }

            var (transferred, transferFailure) = Api7SolidPlane.TryTransfer(stored.Payload, bridge);
            if (transferred is null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Плоскость по ссылке '{reference}' не получена: {transferFailure}",
                    details: new Dictionary<string, object?> { ["plane_ref"] = reference });
            }

            // The normal of an existing plane is NOT recomputed: it belongs to the model, and claiming
            // it as our own would mean reporting a number that was never measured.
            return (transferred, null, null);
        }

        if (plane.PointMm is not { Count: 3 } point || plane.NormalMm is not { Count: 3 } normal)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость не задана: нужен plane_ref либо point_mm и normal_mm (по три числа).",
                details: new Dictionary<string, object?>
                {
                    ["has_point"] = plane.PointMm is not null,
                    ["has_normal"] = plane.NormalMm is not null,
                });
        }

        foreach (var value in point.Concat(normal))
        {
            if (!double.IsFinite(value))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Точка и нормаль плоскости обязаны быть конечными числами.",
                    details: new Dictionary<string, object?> { ["point_mm"] = point, ["normal_mm"] = normal });
            }
        }

        // A zero normal is refused BEFORE COM: the check is taken from PlaneBasis rather than written
        // again — otherwise the "normal is non-zero" rule would live in two places and diverge at the
        // first edit.
        try
        {
            _ = PlaneBasis.FromNormal(normal);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость невыразима: " + ex.Message,
                details: new Dictionary<string, object?> { ["normal_mm"] = normal });
        }

        var created = Api7SolidPlane.TryCreateByPointNormal(container, point, normal, name: null);
        if (created.Plane is null)
        {
            // The data are correct but the plane was not built — this is now a kernel refusal.
            failure = created.Failure ?? "плоскость не построена";
            return (null, created.UnitNormal, new[] { point[0], point[1], point[2] });
        }

        return (created.Plane, created.UnitNormal, new[] { point[0], point[1], point[2] });
    }

    /// <summary>Document bodies with volume, bounding box, face count and a multi-piece flag.</summary>
    /// <remarks>
    /// Read by the same route as <c>ReadBodySnapshots</c> (<c>refresh()</c> before the walk,
    /// <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c>, <c>GetGabarit</c>), but with two
    /// extra fields that B3 acceptance lacks: <c>MultiBodyParts</c> tells "one body made of two
    /// pieces" from "one whole body", and <c>FaceCount</c> gives an independent sign of the same.
    /// </remarks>
    private List<SolidBodyDto> ReadSolidBodies(DocumentEntry document)
    {
        var rows = new List<SolidBodyDto>();
        var bodies = (ksBodyCollection)document.PartNow().BodyCollection();
        bodies.refresh();
        var count = bodies.GetCount();
        for (var i = 0; i < count; i++)
        {
            var element = AsInterface<ksBody>(bodies.GetByIndex(i))
                ?? (i == 0 ? AsInterface<ksBody>(document.PartNow().GetMainBody()) : null);
            if (element is null)
            {
                // A body that does not answer as ksBody is NOT skipped: skipping would shift the
                // numbering and let a foreign body pass for the result.
                rows.Add(new SolidBodyDto
                {
                    BodyRef = References.Register("body_unresolved", document.Id, document.Revision, bodies.GetByIndex(i)).Id,
                    Kind = "unresolved",
                    VolumeMm3 = null,
                    Bbox = BoundingBoxDto.Empty,
                    FaceCount = -1,
                    MultiBodyParts = false,
                });
                continue;
            }

            var edgeCount = CountUniqueEdges(element, out var faces);
            rows.Add(new SolidBodyDto
            {
                BodyRef = References.Register("body", document.Id, document.Revision, element).Id,
                Kind = SafeIsSolid(element) ? "solid" : "sheet",
                VolumeMm3 = SafeDouble(() => MassProperties(element, (uint)KompasUnits.MassMmKg)?.v
                    ?? throw new InvalidCastException("объём недоступен")),
                Bbox = ReadBodyBox(element),
                FaceCount = faces,
                MultiBodyParts = SafeFlag(() => element.MultiBodyParts),
            });

            _ = edgeCount;
        }

        return rows;
    }

    /// <summary>Whether the body matches the result of the transformation: the bounding box computed BEFORE the
    /// attempt by the SAME matrix is compared. Comparison by position, not by volume: volume does not
    /// change on a translation, so by it "moved" and "stayed" are indistinguishable.</summary>
    private static bool MatchesMoved(SolidBodyDto row, double[] matrix, BodySnapshot? before)
    {
        if (before?.Min is not { Length: 3 } min || before.Max is not { Length: 3 } max)
        {
            return false;
        }

        var corners = new[]
        {
            new[] { min[0], min[1], min[2] }, new[] { max[0], min[1], min[2] },
            new[] { min[0], max[1], min[2] }, new[] { max[0], max[1], min[2] },
            new[] { min[0], min[1], max[2] }, new[] { max[0], min[1], max[2] },
            new[] { min[0], max[1], max[2] }, new[] { max[0], max[1], max[2] },
        };

        var images = corners.Select(c => RepositionMatrix.Apply(matrix, c)).ToList();
        var expected = new[]
        {
            images.Min(p => p[0]), images.Min(p => p[1]), images.Min(p => p[2]),
            images.Max(p => p[0]), images.Max(p => p[1]), images.Max(p => p[2]),
        };

        var actual = new[]
        {
            row.Bbox.MinMm[0], row.Bbox.MinMm[1], row.Bbox.MinMm[2],
            row.Bbox.MaxMm[0], row.Bbox.MaxMm[1], row.Bbox.MaxMm[2],
        };

        for (var i = 0; i < 6; i++)
        {
            if (!double.IsFinite(actual[i]) || Math.Abs(expected[i] - actual[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The composition of the feature tree AT SNAPSHOT TIME — what makes "new" different from "was
    /// already there".</summary>
    /// <remarks>
    /// <b>Why not by name.</b> The former rule "new = a name absent from the snapshot" relied on the
    /// uniqueness of the DISPLAYED name. Client acceptance on 19.09.2026 measured the opposite: KOMPAS
    /// gives two consecutive reposition features the SAME name
    /// «Изменение положения : Тело 1», so the second feature was dropped by the filter, and
    /// <c>solid.reposition</c> answered <c>GEOMETRY_FAILED</c> with the geometry built CORRECTLY.
    /// <b>What was measured instead of a guess</b> (probe I, <c>--identity</c>, 19.09.2026, run
    /// <c>c90961c6a3ba478697da5bc243040719</c>, report <c>docs/acceptance/api7/feature-identity.json</c>):
    /// <list type="number">
    /// <item>the element address is stable: two consecutive walks of collection 110 return the same
    /// COM object at the same index;</item>
    /// <item><c>ksEntityCollection.FindIt(entity)</c> returns the element index from zero and <c>−1</c>
    /// for an object not in the collection;</item>
    /// <item>a collection taken BEFORE the operation does NOT track mutation: after two operations it
    /// still reports the original element count and <c>FindIt = −1</c> for both new features.
    /// That is what makes it a snapshot of "what was before", not a second view of the current
    /// state.</item>
    /// </list>
    /// Hence the addressing rule: an element is NEW if and only if the retained snapshot does not know
    /// it. There is no first or last match, no fixed index and no rename here; when names coincide, it
    /// is the identity of the COM object that distinguishes, not the string.
    /// <b>Negative control.</b> A snapshot that does not know an element must answer <c>−1</c>, and one
    /// that knows it — its index. Both outcomes were measured in one run: elements that existed before
    /// the operation gave <c>0</c> and <c>1</c>, both new features — <c>−1</c>. Had <c>FindIt</c>
    /// answered <c>−1</c> to everything, the set difference would have declared all elements new and the
    /// address would have been rejected as ambiguous rather than handed out at random.
    /// History: docs/decisions/adapter-solid.md#identity</remarks>
    private sealed class FeatureTreeSnapshot
    {
        private readonly ksEntityCollection? _collection;

        private FeatureTreeSnapshot(ksEntityCollection? collection, IReadOnlyList<(ksEntity Entity, string Name)> elements)
        {
            _collection = collection;
            Elements = elements;
        }

        public IReadOnlyList<(ksEntity Entity, string Name)> Elements { get; }

        public int Count => Elements.Count;

        public static FeatureTreeSnapshot Capture(DocumentEntry document)
        {
            var collection = document.PartNow()
                .EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement)) as ksEntityCollection;
            var elements = new List<(ksEntity, string)>();
            if (collection is not null)
            {
                var count = collection.GetCount();
                for (var i = 0; i < count; i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity)
                    {
                        elements.Add((entity, entity.name ?? string.Empty));
                    }
                }
            }

            return new FeatureTreeSnapshot(collection, elements);
        }

        /// <summary>Index of the element in the snapshot, or <c>−1</c> if the snapshot does not know it.</summary>
        public int IndexOf(ksEntity entity)
        {
            if (_collection is null)
            {
                return -1;
            }

            try
            {
                return _collection.FindIt(entity);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A failed lookup is "I do not know", not "I know": the element is treated as new, and
                // the address is either recognised by type or the call honestly refuses. A silent 0
                // here would pass an existing element off as new.
                return -1;
            }
        }

        public bool WasPresent(ksEntity entity) => IndexOf(entity) >= 0;

        public string[] Names => Elements.Select(e => e.Name).ToArray();
    }

    /// <summary>The address of the just-created feature — an API5 TREE ELEMENT, not an API7 object.</summary>
    /// <remarks>
    /// Probe T (steps TL.2, TL.6, TL.7, TL.9; run <c>1c111eff3cd94007b436c5a3862e48bc</c>) measured: a
    /// feature created by an API7 factory IS present in the API5 tree, and it is exactly the one that
    /// answers suppression (<c>ksFeature.excluded</c>, volume 37 000 → 49 000 and back) and deletion
    /// (<c>DeleteObject</c>, bodies 2 → 3). But the API7 object itself — <c>IBoolean</c>,
    /// <c>ISplitSolid</c>, <c>ICut</c>, <c>IBodyReposition</c> — is not an API5 feature, and
    /// <c>RequireFeatureEntity</c> rejects it. A reference to the API7 object would make
    /// <c>discover</c>, <c>suppress_restore</c> and <c>delete_dependencies</c> impossible for all
    /// eleven rows — that is, four of the ten actions per row would have to be closed as "no API".
    /// <b>Recognition is by set difference on COM-object identity</b> relative to the
    /// <see cref="FeatureTreeSnapshot"/> taken before the operation, plus a filter by feature TYPE.
    /// Neither the collection order nor the displayed name is an address: names of two consecutive
    /// features of one kind COINCIDE (MEASURED 19.09.2026, probe I), so the name distinguishes
    /// nothing, and the order is not promised.
    /// A candidate must answer <c>GetFeature()</c> as <c>ksFeature</c> (this cuts off auxiliary
    /// geometry — the plane and point that split and cut create together with the feature) and be
    /// EXACTLY ONE. Zero or several is an honest refusal with a list, not a reference "to something
    /// similar": suppressing a foreign feature would mean silently spoiling foreign geometry.
    /// WHY A FILTER BY TYPE AND NOT BY COUNT. The first edition of the rule required "exactly one new
    /// element", and on the <c>save_tools</c> mode this gave a refusal with a SUCCESSFULLY performed
    /// operation: <c>keep_tools=true</c> creates TWO features — the operation itself and the auxiliary
    /// «Копия тела» (MEASURED: <c>type=69 «Булева операция:1»</c> and <c>type=79 «Копия тела :
    /// Тело 1»</c>). Both are new, both answer <c>ksFeature</c>, and counting cannot tell them apart —
    /// the operation type does. The numbers are taken from measurement
    /// (<c>scratch/b3-measure-feature-types.py</c>, <c>kompas_list_features</c>), not by analogy:
    /// 69 — boolean, 633 — split, 50 — cut, 79 — reposition.
    /// History: docs/decisions/adapter-solid.md#feature-address</remarks>
    private (ksEntity Entity, string Name) RequireCreatedFeatureAddress(
        DocumentEntry document, FeatureTreeSnapshot before, int expectedTreeType, string tool)
    {
        var candidates = new List<(ksEntity Entity, string Name)>();
        var newNames = new List<string>();
        var newElements = new List<string>();
        if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
            is ksEntityCollection collection)
        {
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                // "Was before" — by COM-object identity, not by the name string.
                if (before.WasPresent(entity) || entity.GetFeature() is not ksFeature)
                {
                    continue;
                }

                var name = entity.name ?? string.Empty;
                newNames.Add(name);
                newElements.Add($"[{i}] «{name}» type={entity.type}");
                if (entity.type != expectedTreeType)
                {
                    continue;
                }

                candidates.Add((entity, name));
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"После '{tool}' новый признак в дереве API5 опознан неоднозначно: подходящих "
            + $"{candidates.Count} ({string.Join(", ", candidates.Select(c => $"«{c.Name}»"))}). "
            + "Операция выполнена, но адрес признака не получен: подавление, удаление и чтение "
            + "признака по этой ссылке недоступны.",
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["candidates"] = candidates.Select(c => c.Name).ToArray(),
                ["expected_tree_type"] = expectedTreeType,
                ["new_names"] = newNames.ToArray(),
                ["new_elements"] = newElements.ToArray(),
                ["names_before"] = before.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                ["elements_before"] = before.Count,
            });
    }

    /// <summary>Whether the read body matches the "before" snapshot: volume within the profile tolerance AND
    /// bounding box on each axis. Volume alone is not enough: different bodies can share a volume (in
    /// the B3 reference the target's volume and the tool's volume are equal — 24 000), and then
    /// "untouched" and "part" would be mixed up.</summary>
    /// <remarks>A missing bounding box in the snapshot is "nothing to compare with", not "matched": <c>false</c>
    /// is returned and the caller sees the body as unrecognised. A silent <c>true</c> here would mean a
    /// body counted as untouched on volume alone.</remarks>
    private static bool MatchesSnapshot(SolidBodyDto row, BodySnapshot snapshot)
    {
        if (snapshot.Volume is double volume
            && row.VolumeMm3 is double actual
            && !VolumeMatches(actual, volume))
        {
            return false;
        }

        if (snapshot.Min is not { Length: 3 } min || snapshot.Max is not { Length: 3 } max)
        {
            return false;
        }

        var box = row.Bbox;
        if (box?.MinMm is not { Count: 3 } actualMin || box.MaxMm is not { Count: 3 } actualMax)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(actualMin[i] - min[i]) > 0.01d || Math.Abs(actualMax[i] - max[i]) > 0.01d)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>OBSOLETE 19.09.2026. The former "body untouched" sign for cutting: it compared the
    /// candidate's volume with the TARGET's, so a foreign body was never recognised (different volume),
    /// every other body was declared untouched WITHOUT proof, and a vanished body was not listed.
    /// Superseded by body-composition matching in <c>SolidCutByPlane</c>; kept as a record, nothing
    /// calls it — a call would be a regression of <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>.
    /// History: docs/decisions/adapter-solid.md#cut-untouched</summary>
    private static bool IsUntouched(SolidBodyDto row, List<BodySnapshot> before, int targetIndex)
    {
        var snapshot = before.FirstOrDefault(b => b.Index == targetIndex);
        if (snapshot?.Volume is not double volume || row.VolumeMm3 is not double actual)
        {
            return false;
        }

        return VolumeMatches(actual, volume);
    }

    /// <summary>Whether the body matches at least one "before" snapshot — by BOUNDING BOX AND VOLUME, not by its
    /// place in the collection: the operation consumes the target body, and a part takes its index.</summary>
    private static bool MatchesAnySnapshot(SolidBodyDto row, List<BodySnapshot> before) =>
        before.Any(b => MatchesSnapshot(row, b));

    private static string BodyVolumesText(List<BodySnapshot> snapshots) =>
        NumberListText(snapshots.Select(b => b.Volume).ToList());

    private static BoundingBoxDto Box(BodySnapshot? snapshot) =>
        snapshot?.Min is { Length: 3 } min && snapshot.Max is { Length: 3 } max
            ? new BoundingBoxDto(min, max)
            : BoundingBoxDto.Empty;

    /// <summary>Whether the read bounding box matches the analytically declared one. The tolerance is the profile
    /// length tolerance: 0.01 mm absolute or 1e-6 relative. A missing side is "nothing to compare with",
    /// not "matched": a silent <c>true</c> here would pass an unverified position off as verified.</summary>
    private static bool BoxMatches(BoundingBoxDto? actual, BoundingBoxDto expected)
    {
        if (actual?.MinMm is not { Count: 3 } actualMin || actual.MaxMm is not { Count: 3 } actualMax
            || expected.MinMm is not { Count: 3 } expectedMin || expected.MaxMm is not { Count: 3 } expectedMax)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.IsFinite(actualMin[i]) || !double.IsFinite(actualMax[i]))
            {
                return false;
            }

            var tolerance = Math.Max(0.01d, 1e-6 * Math.Abs(expectedMax[i]));
            if (Math.Abs(actualMin[i] - expectedMin[i]) > tolerance
                || Math.Abs(actualMax[i] - expectedMax[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Bounding box as text — for refusal messages and check fields.</summary>
    private static string BoxText(BoundingBoxDto? box)
    {
        if (box?.MinMm is not { Count: 3 } min || box.MaxMm is not { Count: 3 } max)
        {
            return "<габарит не прочитан>";
        }

        return "(" + string.Join(",", min) + ")…(" + string.Join(",", max) + ")";
    }

    private static double SumVolumes(List<BodySnapshot> snapshots) =>
        snapshots.Sum(s => s.Volume ?? 0d);

    private static double? SumVolumesOrNull(List<BodySnapshot> snapshots) =>
        snapshots.Any(s => s.Volume is null) ? null : snapshots.Sum(s => s.Volume ?? 0d);

    /// <summary>Volume comparison within the profile tolerance: 0.01 mm³ absolute or 1e-6 relative.</summary>
    private static bool VolumeMatches(double actual, double expected)
    {
        var delta = Math.Abs(actual - expected);
        return delta <= 0.01d || delta <= 1e-6 * Math.Abs(expected);
    }

    private static List<SolidBodyDto> MatchSavedTools(
        List<SolidBodyDto> rows, List<BodyTarget> tools)
    {
        // Saved tools are looked up by the bounding box BEFORE the operation: they stay in place, and
        // "the same volume" does not tell them from the result when the result's volume matches a body.
        var matched = new List<SolidBodyDto>();
        foreach (var tool in tools)
        {
            var snapshot = tool.Snapshot;
            if (snapshot?.Min is not { Length: 3 } min || snapshot.Max is not { Length: 3 } max)
            {
                continue;
            }

            var row = rows.FirstOrDefault(r =>
                Near(r.Bbox.MinMm, min) && Near(r.Bbox.MaxMm, max)
                && snapshot.Volume is double v && r.VolumeMm3 is double a && VolumeMatches(a, v));
            if (row is not null)
            {
                matched.Add(row);
            }
        }

        return matched;
    }

    private static bool Near(IReadOnlyList<double> actual, double[] expected)
    {
        if (actual.Count != 3)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.IsFinite(actual[i]) || Math.Abs(actual[i] - expected[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A body's boolean flag, where "not read" honestly becomes <c>false</c> only because the field is
    /// required. A missing read does not distort acceptance here: <c>MultiBodyParts</c> is an ADDITIONAL
    /// sign, while the main one (<c>FaceCount</c>) is read separately and independently.</summary>
    private static bool SafeFlag(Func<bool> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return false;
        }
    }

    // =============================================================================================
    // Editing B3 features (order §5, §7: action edit)
    // =============================================================================================

    private const string RepositionFamily = "reposition";

    private const string SplitFamily = "split";

    private const string CutByPlaneFamily = "cut_by_plane";

    private const string BooleanFamily = "boolean";

    /// <summary>The position of a tree element among features OF THE SAME TYPE, and the total number of such
    /// features in the tree.</summary>
    /// <param name="Ordinal">How many features of this type stand in the tree before the target;
    /// <c>null</c> — the target was not found among them. Zero and "not found" are different outcomes
    /// and must not be mixed.</param>
    /// <param name="Count">How many features of this type are in the tree in total; <c>-1</c> — could
    /// not be read.</param>
    private readonly record struct SameTypeScan(int? Ordinal, int Count);

    /// <summary>The position of a tree element among features OF THE SAME TYPE — how many such features stand in
    /// the tree before it. This is the element's address in the API7 collection of the same operation.</summary>
    /// <remarks>
    /// <b>Why position, not name.</b> The name in API5 and the name in API7 for one and the same object
    /// diverge — this is MEASURED on a fillet (F.8: a name set in API5 reads differently in API7), and
    /// for the same reason <c>Api7Fillet.FindIndexesByIdenticalRadius</c> and
    /// <c>Api7Rotated.FindIndexFor</c> match by VALUE, not by name. For B3 features there is no
    /// identifier value known before the edit at all (the boolean operands are consumed after the
    /// union, the split plane is an auxiliary object), so the address is taken by position. This is the
    /// same technique used to edit a rotation when the tree entity does not answer <c>QI(IRotated)</c>
    /// (<c>RotatedOrdinal</c>), and it is checked by geometry in acceptance: editing the wrong feature
    /// will not give the expected volume and bounding box.
    /// The feature count is returned TOGETHER with the position, not by a separate walk: two walks of
    /// one collection could diverge, and the decision on address suitability is made from both numbers
    /// at once (<see cref="RequireSameTypeIndex"/>).
    /// </remarks>
    private static SameTypeScan ScanSameType(ksPart part, ksEntity target, int type)
    {
        try
        {
            var kinds = new[]
            {
                KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                (short)-1,
            };
            foreach (var kind in kinds)
            {
                if (part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                var ordinal = 0;
                var seen = 0;
                int? found = null;
                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is not ksEntity candidate || candidate.type != type)
                    {
                        continue;
                    }

                    seen++;
                    if (ReferenceEquals(candidate, target))
                    {
                        found = ordinal;
                    }
                    else if (found is null)
                    {
                        ordinal++;
                    }
                }

                if (seen > 0)
                {
                    // Features of this type were found; if the target is not among them, there must be
                    // no second pass over another collection: that would mean the target lies elsewhere.
                    return new SameTypeScan(found, seen);
                }
            }

            return new SameTypeScan(null, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new SameTypeScan(null, -1);
        }
    }

    /// <summary>The index of a feature in the API7 collection of the same operation — or an honest refusal if the
    /// match is not proven.</summary>
    /// <remarks>
    /// <b>Why "position less than the element count" is not enough.</b> The position in the tree and the
    /// index in the API7 collection are two DIFFERENT lists, and they coincide only when there are
    /// exactly as many features of this type in the tree as there are elements in the collection. For
    /// reposition this condition is violated by measurement: the number <c>79</c> is carried not only by
    /// "Изменение положения" but also by the auxiliary «Копия тела» that the boolean tool-preservation
    /// mode creates (<c>scratch/b3-measure-feature-types.py</c>, 18.09.2026). In a document with a copy
    /// the position of "Изменение положения" stops being an index into <c>BodyRepositions</c>, and the
    /// write would land in a FOREIGN feature. Therefore, when the numbers diverge, the call is refused
    /// before mutation.
    /// The price of the refusal: editing a feature next to which lives a feature of the same number but
    /// a different operation is not performed. This is the chosen side — refusal is preferred to writing
    /// into the wrong object, because "applied in the wrong place" is indistinguishable in the response
    /// from "applied".
    /// </remarks>
    private static int RequireSameTypeIndex(
        DocumentEntry document,
        ksEntity entity,
        int treeType,
        int? api7Count,
        string family,
        string collectionName)
    {
        var position = ScanSameType(document.PartNow(), entity, treeType);
        var ordinal = position.Ordinal;
        var treeCount = position.Count;

        if (ordinal is not int index || api7Count is not int total || treeCount != total
            || index < 0 || index >= total)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Признак ({family}) не сопоставлен с элементом коллекции API7 {collectionName}: "
                + $"позиция среди признаков этого типа в дереве — {Describe(ordinal)}, признаков этого "
                + $"типа в дереве — {Describe(treeCount)}, элементов в коллекции — {Describe(api7Count)}. "
                + "Правка не выполняется: записать параметр в чужой признак значило бы изменить не тот "
                + "объект. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = family + "_feature_not_matched",
                    ["ordinal"] = ordinal,
                    ["tree_count"] = treeCount,
                    ["api7_count"] = api7Count,
                    ["tree_type"] = treeType,
                });
        }

        return index;
    }

    /// <summary>Editing an EXISTING reposition feature via <c>kompas_update_feature</c>.</summary>
    /// <remarks>
    /// <b>The route is MEASURED 18.09.2026</b> (probe <c>--reposition</c>, step RP.6): editing feature[0]
    /// by rewriting the same vector leaves the bounding box <c>(17,−11,13)…(37,−1,18)</c>, and resetting
    /// the vector to zero brings the body home — the parameter is applied to the ORIGINAL inputs, not to
    /// the current position. This is exactly what order §5 requires, and a repeated
    /// <c>kompas_reposition</c> does not satisfy it: it creates a second feature and accumulates the
    /// offset.
    /// <b>A successful <c>Update()</c> is not proof.</b> Step RP.2 measured three routes out of four that
    /// returned <c>true</c> and did not move the body. So the translation is READ BACK
    /// (<c>Position.X/Y/Z</c>) and compared with the matrix that was requested: if the offset accumulated,
    /// the read would give a doubled value. Volume under a rigid transformation must be preserved, and
    /// that is checked too — by it "moved" and "stayed" are indistinguishable, but "the transformation
    /// stayed rigid" is visible.
    /// The checks "fields of other families are rejected" are the same here as for the other editable
    /// families: silently applying half a request is worse than refusing.
    /// History: docs/decisions/adapter-solid.md#edit-reposition</remarks>
    private UpdateFeatureResult UpdateSolidReposition(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            RepositionFamily,
            "reposition_kind, reposition_vector_mm и поля оси поворота (reposition_axis_point_mm, "
            + "reposition_axis_direction_mm / reposition_axis_point2_mm, reposition_angle_deg)");

        if (command.RepositionKind is not RepositionKind kind)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка изменения положения требует reposition_kind (translate или rotate): без вида "
                + "преобразования непонятно, что именно менять, а угадывать по наличию полей значило бы "
                + "принимать решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RepositionFamily });
        }

        // Field-composition checks are reused VERBATIM from the creation route: the same names, the same
        // rules (a vector — three finite numbers; an axis — either a direction or a second point, but not
        // both and not neither; an angle is required). Duplicating them here would create a second place
        // where they could diverge.
        var synthetic = new RepositionCommand
        {
            DocumentId = document.Id,
            TargetBodyRef = command.FeatureRef,
            Kind = kind,
            VectorMm = command.RepositionVectorMm,
            AxisPointMm = command.RepositionAxisPointMm,
            AxisDirectionMm = command.RepositionAxisDirectionMm,
            AxisPoint2Mm = command.RepositionAxisPoint2Mm,
            AngleDeg = command.RepositionAngleDeg,
            ExpectedRevision = command.ExpectedRevision,
        };

        var matrix = kind switch
        {
            RepositionKind.Translate => BuildTranslation(synthetic),
            RepositionKind.Rotate => BuildRotation(synthetic),
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"kind '{kind}' не распознан.",
                details: new Dictionary<string, object?> { ["kind"] = kind.ToString() }),
        };

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.reposition.update");

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.BodyRepositionFeature,
            Api7SolidReposition.Count(container),
            RepositionFamily,
            "BodyRepositions");

        var before = Api7SolidReposition.ReadPlacement(container, index);
        var written = Api7SolidReposition.TryWrite(container, index, matrix);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Положение не записано: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.reposition.update");

        var readBack = Api7SolidReposition.ReadPlacement(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var wanted = new[] { matrix[12], matrix[13], matrix[14] };

        // THE EDIT IS READ BACK AND COMPARED BY MATRIX — on the same basis as creation
        // (see RequirePlacementRoundTrip). Here it is also a direct check of order §5: the parameter must
        // be applied to the ORIGINAL inputs of the feature, not accumulated. Accumulation would give a
        // divergence exactly equal to the previous transformation, i.e. detectable by matrix.
        var readBackDifference = PlacementDifference(readBack.Reading, matrix);
        if (readBackDifference is double readBackGap && readBackGap > PlacementRoundTripTolerance)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Правка не подтвердилась чтением: размещение, собранное из прочитанных параметров, "
                + $"расходится с заданным на {readBackGap:R} по матрице (вид — "
                + $"{kind.ToString().ToLowerInvariant()}). Это признак того, что параметр применён не к "
                + "исходным входам признака.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["matrix_difference"] = readBackGap,
                    ["requested_matrix"] = matrix,
                    ["read_back_angles_deg"] = readBack.Reading?.AnglesDeg,
                    ["read_back_displacement_mm"] = readBack.Reading?.DisplacementMm,
                    ["read_back_before_angles_deg"] = before.Reading?.AnglesDeg,
                    ["read_back_before_displacement_mm"] = before.Reading?.DisplacementMm,
                });
        }

        // A rigid transformation does not change volume. This is not an "expectation from the order" but
        // an invariant: if the volume changed, the written placement is not a reposition.
        var volumePreserved = volumeBefore is double v0 && volumeAfter is double v1
            && Math.Abs(v1 - v0) <= ProfileArea.Tolerance(v0);
        var featureCountPreserved = featuresAfter == featuresBefore;
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        var rowsAfter = ReadSolidBodies(document);
        var moved = command.ExpectedBboxMm is null
            ? null
            : rowsAfter.FirstOrDefault(r => BoxMatches(r.Bbox, command.ExpectedBboxMm));

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featureCountPreserved,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
            new("volume_preserved", volumePreserved, Observed: Num(volumeAfter), Expected: Num(volumeBefore)),
        };

        if (command.ExpectedBboxMm is not null)
        {
            checks.Add(new NamedCheck(
                "bbox_expected", moved is not null,
                Observed: rowsAfter.Count == 0 ? "<тел нет>" : BoxText(rowsAfter[0].Bbox),
                Expected: BoxText(command.ExpectedBboxMm)));
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            checks.Add(new NamedCheck(
                "volume_expected",
                volumeAfter is double measured && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                Observed: Num(volumeAfter),
                Expected: Num(expected)));
        }

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is null)
        {
            // The wording must not contradict the level: with a declared bounding box the edit is
            // confirmed geometrically, but what is confirmed is the POSITION, not the invariance of the
            // volume. Saying "not confirmed geometrically" would lie the other way.
            unverified.Add(command.ExpectedBboxMm is null
                ? "expected_volume_not_supplied — без аналитического ожидания объёма правка не может "
                  + "быть подтверждена геометрически"
                : "expected_volume_not_supplied — объём не объявлен, поэтому подтверждено положение "
                  + "(габарит), но НЕ неизменность объёма: у жёсткого преобразования объём обязан "
                  + "сохраняться, и это осталось непроверенным");
        }

        if (command.ExpectedBboxMm is null)
        {
            unverified.Add("expected_bbox_not_supplied — у жёсткого преобразования объём инвариантен, "
                           + "поэтому положение подтверждает только габарит (expected_bbox_mm)");
        }

        if (readBack.Reading is null)
        {
            unverified.Add("position_read_back_failed: "
                + (readBack.Failure ?? "причина не сообщена"));
        }
        else if (readBackDifference is null)
        {
            // It read back, but NOT by that route: the feature has no parametric representation
            // (OrientationType ≠ ksEulerCorners, or the translation is not written as a displacement).
            // This is not "no data" but a named reason, and it differs from a read failure.
            unverified.Add("position_read_back_not_parametric — параметры размещения прочитаны не "
                + "маршрутом углов Эйлера: OrientationType=" + readBack.Reading.OrientationType
                + ", ParameterType=" + readBack.Reading.ParameterType);
        }

        if (command.ExpectedBboxMm is not null && moved is null)
        {
            // The bounding box is declared by the caller and did not match — this is a refusal, not "not
            // checked".
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Правка выполнена, но тело не встало в объявленный габарит. Просили перенос "
                + $"({string.Join(",", wanted)}); габарит после правки — "
                + (rowsAfter.Count == 0 ? "<тел нет>" : BoxText(rowsAfter[0].Bbox))
                + ", ожидался " + BoxText(command.ExpectedBboxMm) + ". Это признак того, что параметр "
                + "применён не к исходным входам признака (наряд §5 запрещает накопление смещения).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_translation"] = wanted,
                    ["read_back_displacement_mm"] = readBack.Reading?.DisplacementMm,
                    ["read_back_angles_deg"] = readBack.Reading?.AnglesDeg,
                    ["read_back_before_displacement_mm"] = before.Reading?.DisplacementMm,
                    ["bbox_after"] = rowsAfter.Count == 0 ? null : BoxText(rowsAfter[0].Bbox),
                    ["expected_bbox"] = BoxText(command.ExpectedBboxMm),
                });
        }

        // A declared expectation did not match — this is a refusal, not "not checked". Returning "success
        // at a lower level" would pass the unreached off as reached: the edit has a caller who declared
        // the number, and he is entitled to learn that the number was not obtained.
        if (command.ExpectedVolumeMm3 is double declared
            && (volumeAfter is not double measuredAfter
                || Math.Abs(measuredAfter - declared) > ProfileArea.Tolerance(declared)))
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Правка выполнена, но объём не совпал с объявленным: ожидалось " + Num(declared)
                + ", измерено " + Num(volumeAfter) + ". У жёсткого преобразования объём — инвариант, "
                + "поэтому расхождение означает, что записанное преобразование не является "
                + "преобразованием положения.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["expected_volume_mm3"] = declared,
                    ["volume_after_mm3"] = volumeAfter,
                    ["volume_before_mm3"] = volumeBefore,
                    ["bbox_after"] = rowsAfter.Count == 0 ? null : BoxText(rowsAfter[0].Bbox),
                });
        }

        // The level "geometry checked" means exactly one thing: the expectation declared by the caller
        // was COMPARED with the model and matched. Requiring specifically the volume here is a defect, and
        // it is MEASURED: under a rigid transformation the volume is an INVARIANT, and the position is
        // confirmed by the BOUNDING BOX (this is also said above in unverified), while the first edition
        // required ExpectedVolumeMm3, so a call that declared ONE bounding box and got it answered
        // level=call_returned. That is, matching evidence was declared undeclared, and the caller could
        // not tell "checked and matched" from "not checked". Found by acceptance row B3.27 (order §6.6),
        // fixed here.
        var declaredExpectation = DeclaresExpectation(command);
        var geometryConfirmed = declaredExpectation && checks.TrueForAll(c => c.Passed);

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            RepositionFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    // =============================================================================================
    // Editing the SM-16 families (split and cut) — action edit of order §7
    // =============================================================================================

    /// <summary>A contract field belonging to the B3 families: its name as in the schema, its owning families and
    /// how its value is read from the command.</summary>
    /// <param name="Name">The field name in the tool schema (snake_case), not the C# property name.</param>
    /// <param name="Owners">The families the field is allowed to. There may be several: the support
    /// <c>plane</c> belongs to both split and cut.</param>
    /// <param name="Read">Reading the value: <c>null</c> means "the field was not passed", and this
    /// differs from "passed and rejected".</param>
    private sealed record SolidField(string Name, string[] Owners, Func<UpdateFeatureCommand, object?> Read);

    /// <summary>All fields of <see cref="UpdateFeatureCommand"/> belonging to the B3 families — ONE table for two
    /// questions: "who owns the field" and "what lies in it".</summary>
    /// <remarks>
    /// <b>Why one table and not two.</b> The first edition kept owners in a dictionary and values in a
    /// separate enumerator, and these two structures could diverge. They would have diverged silently: a
    /// field added to the enumerator without an entry in the dictionary was NEVER rejected, because
    /// <c>TryGetValue</c> returned <c>false</c> and the "field is foreign" condition short-circuited to
    /// <c>false</c>. This is exactly the same class of defect as P5 (a declared but swallowed field),
    /// only from the other side: there the field was forgotten in the forbidden list, here — in the known
    /// list, and the outcome is one — the field is accepted and not applied. Now there is nothing to
    /// diverge: the enumerator walks this same table.
    /// <b>Why an enumeration, not a "forbidden list".</b> The enumeration of what occurs in the command
    /// changes together with the contract, and the default here must be "do not reject": a field not
    /// assigned to any family is rejected not here, but by its family or by the check of inapplicable
    /// parameters below. Therefore the completeness of the table is checked separately — by the test
    /// <c>SolidFeatureClassificationTests</c>, which verifies it against the contract itself: a new
    /// command field will not pass until it is assigned to a family, to the inapplicable, to addressing
    /// or to geometry expectations.
    /// History: docs/decisions/adapter-solid.md#field-classification</remarks>
    private static readonly SolidField[] SolidFields =
    {
        new("operation", new[] { BooleanFamily }, c => c.Operation),
        new("plane", new[] { SplitFamily, CutByPlaneFamily }, c => c.Plane),
        new("keep_side", new[] { CutByPlaneFamily }, c => c.KeepSide),
        new("target_body_ref", new[] { CutByPlaneFamily }, c => c.TargetBodyRef),
        new("expected_part_volumes_mm3", new[] { SplitFamily }, c => c.ExpectedPartVolumesMm3),
        new("reposition_kind", new[] { RepositionFamily }, c => c.RepositionKind),
        new("reposition_vector_mm", new[] { RepositionFamily }, c => c.RepositionVectorMm),
        new("reposition_axis_point_mm", new[] { RepositionFamily }, c => c.RepositionAxisPointMm),
        new("reposition_axis_direction_mm", new[] { RepositionFamily }, c => c.RepositionAxisDirectionMm),
        new("reposition_axis_point2_mm", new[] { RepositionFamily }, c => c.RepositionAxisPoint2Mm),
        new("reposition_angle_deg", new[] { RepositionFamily }, c => c.RepositionAngleDeg),
        // A field of the ARRAY family (queue B4) — assigned to its own family, not to "inapplicable".
        // This is the fix of a FOUND defect, not decoration: the pattern field was added to the contract
        // by queue B4 but was assigned to no role table, so the completeness check
        // SolidFeatureClassificationTests.EveryCommandProperty_IsClassifiedExactlyOnce failed on it —
        // and, judging by the last run date, had failed since the field appeared. Here it gets the role
        // "family": the array branch returns EARLIER than all others, so a B3 feature never reaches it,
        // and an array feature with a foreign field is rejected by ForeignFamilyFields.
        new("pattern", new[] { PatternFamily }, c => c.Pattern),
        // Fields of the HOLE family (order SM07 §3.2, queue B2). Assigned to their family on the same
        // basis as pattern: a hole feature READS them (its own branch by tree type 583), and features of
        // other families must reject them — and do, because the enumeration is built from this same
        // table. Previously these fields were declared NOWHERE: a call with them on a foreign feature
        // would be accepted and swallowed, and on a hole it was rejected CAPABILITY_UNAVAILABLE with the
        // text «этот признак — null (type=583)», which is what row F08.16.edit measured before this fix
        // (docs/STATUS.md).
        new("diameter_mm", new[] { HoleFamily }, c => c.DiameterMm),
        new("counterbore_diameter_mm", new[] { HoleFamily }, c => c.CounterboreDiameterMm),
        new("counterbore_depth_mm", new[] { HoleFamily }, c => c.CounterboreDepthMm),
        new("countersink_diameter_mm", new[] { HoleFamily }, c => c.CountersinkDiameterMm),
        new("countersink_angle_deg", new[] { HoleFamily }, c => c.CountersinkAngleDeg),
        // The volume-delta expectation is also a field of THIS family, not a common one: only the hole
        // branch reads it. Declaring it a "common expectation" would mean accepting it on reposition and
        // boolean operations and silently not applying it — the same class of defect as keep_side (P5).
        new("expected_volume_delta_mm3", new[] { HoleFamily }, c => c.ExpectedVolumeDeltaMm3),
    };

    /// <summary>Whether the caller declared an analytical geometry expectation — volume and/or bounding box.</summary>
    /// <remarks>The rule is moved into one place because it decides TWO different outcomes: a boolean edit WITHOUT
    /// an expectation is refused at once, while a reposition edit WITH an expectation gets the level
    /// "geometry checked". While this condition stood written twice it had already diverged — and
    /// diverged just enough that a declared and matching bounding box was reported as undeclared (defect
    /// P6, found by acceptance row B3.27). The difference was one word: in reposition the level required
    /// specifically the volume, although the bounding box confirms the position no worse.</remarks>
    private static bool DeclaresExpectation(UpdateFeatureCommand command) =>
        command.ExpectedVolumeMm3 is not null || command.ExpectedBboxMm is not null;

    /// <summary>Refusal on fields of FOREIGN B3 families: the definition of the feature is enumerated IN FULL.</summary>
    /// <remarks>
    /// <b>Why an enumeration, not a "forbidden list".</b> The first edition listed the forbidden fields
    /// by hand, and it already suffered from this: <c>keep_side</c> did not make it into the list, so a
    /// call <c>plane + keep_side</c> on a SPLIT feature was accepted and <c>keep_side</c> was silently
    /// ignored — exactly the class of defect the rule "a parameter not declared in the schema does not
    /// reach COM" was written against (§9.1 P4), only from the other side: declared but swallowed. Here
    /// every family field must be ASSIGNED to a family (<see cref="SolidFields"/>), and a field passed to
    /// a family that does not own it is rejected.
    /// <b>What this check does not promise.</b> It rejects a passed field but does NOT prove that the
    /// list of family fields is complete: completeness is held by the test
    /// <c>SolidFeatureClassificationTests</c>, which verifies the table against the contract itself.
    /// Earlier there stood here a claim that the table and the enumerator "are verified on refusal"; that
    /// was wrong — they were verified nowhere, and the divergence between them was silent. The line is
    /// replaced with a description of what the check actually does.
    /// Fields belonging to no B3 family (extrude, chamfer, fillet, rotation) are checked separately
    /// below: they are not "a foreign family" but simply inapplicable to a B3 feature.
    /// </remarks>
    /// <param name="ownFields">
    /// Names of fields that THIS family reads, even though the role table gives them to another
    /// department. Introduced 20.09.2026 by order SM07 §3.2 for one measured case: <c>depth_mm</c> is an
    /// EXTRUDE field in the table and at the same time an OWN field of a blind hole (<c>blind_flat</c>).
    /// Without this list, editing a blind hole was rejected INVALID_ARGUMENT before COM — and this is
    /// MEASURED on the first delivery with the hole branch (rows F08.15/16/19/20.edit, run 20.09.2026):
    /// the depth is read as a foreign field, although the hole branch reads it. The length of the list is
    /// held not by "common sense" but by acceptance: with it a blind hole is edited, while counterbore
    /// and countersink with <c>depth_mm</c> are still rejected — but now BY MODE
    /// (<c>ValidateHoleEdit</c>), where that is measured (HO.13/HO.16).
    /// </param>
    private static void RejectForeignSolidFields(
        UpdateFeatureCommand command,
        string family,
        string allowed,
        IReadOnlyCollection<string>? ownFields = null)
    {
        bool Owned(string name) => ownFields is not null
                                   && ownFields.Contains(name, StringComparer.Ordinal);

        var foreign = SolidFields
            .Where(f => f.Read(command) is not null
                        && !f.Owners.Contains(family, StringComparer.Ordinal)
                        && !Owned(f.Name))
            .Select(f => f.Name)
            .ToList();

        if (foreign.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — {family}: к нему применяются только {allowed}. Переданы поля других "
                + $"семейств: {string.Join(", ", foreign)}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["family"] = family,
                    ["foreign_fields"] = foreign,
                });
        }

        if ((command.DepthMm is not null && !Owned("depth_mm"))
            || command.EndCondition is not null || command.SketchRef is not null
            || command.Distance1Mm is not null || command.Distance2Mm is not null || command.AngleDeg is not null
            || command.Direction is not null || command.RadiusMm is not null || command.EdgeRefs is not null
            || command.BaseObjectRefs is not null || command.RotationAngleDeg is not null
            || command.RotationDirection is not null
            // Queue B5 (kinematics, sections, shell) — also foreign fields for B3 features. `couplings`
            // was added 20.09.2026: queue B5 added SIX editable fields, and five of them made it into the
            // foreign-field lists while the sixth did not. This was found by the unit test
            // SolidFeatureClassificationTests (a field with no role = a field the adapter will accept and
            // swallow), and the probe scratch/_couplings_scope_probe.py measured the swallowing itself:
            // a call "edit a feature + couplings" returned success, the geometry changed, and the
            // couplings were not applied. It is rejected BEFORE COM, like the other five.
            || command.ShiftMode is not null || command.SectionRefs is not null
            || command.Couplings is not null
            || command.ThicknessMm is not null || command.ThinInward is not null
            || command.FaceRefs is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — {family}: к нему применяются только {allowed}. Параметры выдавливания, "
                + "фаски, скругления, вращения и параметры кинематической операции, элемента по "
                + "сечениям (section_refs и couplings) и оболочки к признакам B3 не применяются.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = family });
        }
    }

    /// <summary>Specifying the support for EDITING an SM-16 feature: the same validation as for creation, but
    /// WITHOUT creating a plane object.</summary>
    /// <remarks>
    /// <b>Why without creation.</b> MEASURED 18.09.2026 (probe <c>--split</c>, step SP.9, step E-B —
    /// negative control): substituting ANOTHER, just-created plane into an existing feature does NOT
    /// change the result — <c>Update()</c> returns <c>true</c> and the parts stay as they were. A
    /// different route works (E-A for split, E-C for cut): transferring the THREE CONSTRUCTION POINTS of
    /// the feature's OWN support. Therefore three points are computed here and no plane object is created
    /// at all — otherwise an unused object would remain in the document on every edit.
    /// <b><c>plane_ref</c> is refused on edit.</b> The route is measured for the feature's OWN support,
    /// and there is no way to prove that the presented reference is that support: comparing plane
    /// references was not measured, and substituting a foreign plane gives no result (E-B). Moving a
    /// foreign plane and reporting an edit would mean passing the unreached off as reached, so the
    /// outcome is honest — a refusal stating that the support is described by a point and a normal.
    /// The point and normal checks are not duplicated but taken from the same rules as at creation:
    /// finiteness here, a non-zero normal — from <c>PlaneBasis.FromNormal</c>, three construction points —
    /// from <c>PlaneBasis.ThreePoints</c>.
    /// </remarks>
    private (double[] P1, double[] P2, double[] P3, double[] UnitNormal, double[] Point) ResolveSupportPlane(
        CutPlaneDto plane,
        string family)
    {
        GuardCutPlaneForm(plane);

        if (plane.PlaneRef is { Length: > 0 } reference)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Правка ({family}) принимает опору только точкой и нормалью, а не ссылкой "
                + $"'{reference}': маршрут правки — перенос точек СОБСТВЕННОЙ опоры признака, а "
                + "доказать, что предъявленная ссылка и есть эта опора, нечем (сравнение ссылок "
                + "плоскостей не измерялось). Подстановка чужой плоскости результата не даёт — "
                + "измерено шагом SP.9, опыт E-B. Перечислите опору заново: point_mm и normal_mm.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["plane_ref"] = reference,
                    ["family"] = family,
                });
        }

        if (plane.PointMm is not { Count: 3 } point || plane.NormalMm is not { Count: 3 } normal)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опора не задана: нужны point_mm и normal_mm (по три числа).",
                details: new Dictionary<string, object?>
                {
                    ["has_point"] = plane.PointMm is not null,
                    ["has_normal"] = plane.NormalMm is not null,
                    ["family"] = family,
                });
        }

        foreach (var value in point.Concat(normal))
        {
            if (!double.IsFinite(value))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Точка и нормаль плоскости обязаны быть конечными числами.",
                    details: new Dictionary<string, object?>
                    {
                        ["point_mm"] = point,
                        ["normal_mm"] = normal,
                        ["family"] = family,
                    });
            }
        }

        try
        {
            _ = PlaneBasis.FromNormal(normal);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость невыразима: " + ex.Message,
                details: new Dictionary<string, object?> { ["normal_mm"] = normal, ["family"] = family });
        }

        var three = PlaneBasis.ThreePoints(point, normal);
        return (three.P1, three.P2, three.P3, three.UnitNormal, new[] { point[0], point[1], point[2] });
    }

    /// <summary>Editing an EXISTING split feature: a new support written into the same feature.</summary>
    /// <remarks>
    /// <b>The route is MEASURED 18.09.2026</b> (probe <c>--split</c>, step SP.9, run
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>): transferring the three construction points of the
    /// feature's OWN support turns the parts <c>6 000 / 18 000</c> at <c>x = 10</c> into
    /// <c>9 000 / 15 000</c> at <c>x = 15</c>, the feature count stays <c>1 → 1</c>, the sum
    /// <c>24 000</c> does not change. The negative control of the same step (E-B) showed that the
    /// "obvious" route — substituting another plane into <c>CutObjects</c> — does not change the result,
    /// and it is not used in the edit.
    /// <b>The definition is enumerated in full, not "the field being changed".</b> A split has two kinds
    /// of support (an existing plane or a point with a normal). Reading the support back IS possible —
    /// this is MEASURED 18.09.2026 by step SP.10 (<c>CutObjects</c> returns three construction points and
    /// a normal, and different supports read differently), and this is exactly what
    /// <c>kompas_get_feature</c> publishes in the <c>solid.plane</c> block. The requirement of request
    /// completeness is kept not because reading is impossible, but because an answer to a partial request
    /// would not tell "exactly what was asked changed" from "what was not mentioned changed too".
    /// <b>Confirmation is only the composition of parts.</b> The sum of volumes does not change when a
    /// split is edited, so <c>expected_volume_mm3</c> is not accepted here at all (it is rejected with a
    /// pointer to <c>expected_part_volumes_mm3</c>): a row checking the sum would pass on complete
    /// inaction.
    /// History: docs/decisions/adapter-solid.md#edit-split</remarks>
    private UpdateFeatureResult UpdateSolidSplit(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            SplitFamily,
            "plane (новая опора) и expected_part_volumes_mm3 (ожидаемые объёмы частей)");

        if (command.ExpectedVolumeMm3 is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке разделения expected_volume_mm3 не принимается: сумма объёмов частей при "
                + "правке не меняется (24 000 и при x = 10, и при x = 15), поэтому сверка суммы прошла "
                + "бы и на полном бездействии. Объявите expected_part_volumes_mm3 — состав частей, "
                + "например [9000, 15000].",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        if (command.ExpectedBboxMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке разделения expected_bbox_mm не принимается: частей несколько, и один "
                + "габарит не говорит, какой части он принадлежит. Объявите expected_part_volumes_mm3.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        if (command.Plane is not CutPlaneDto planeSpec)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка разделения требует plane: без новой опоры менять нечего, а подставлять опору "
                + "из текущего состояния модели значило бы принимать решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        var expectedParts = RequirePartVolumes(command.ExpectedPartVolumesMm3);
        var spec = ResolveSupportPlane(planeSpec, SplitFamily);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.split.update");
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.SplitSolid,
            Api7SolidSplit.Count(container),
            SplitFamily,
            "SplitSolids");

        var moved = Api7SolidSplit.TryMoveSupport(container, index, (spec.P1, spec.P2, spec.P3));
        if (!moved.Moved)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Опора разделения не переписана: " + (moved.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = moved.Failure,
                    ["api7_index"] = index,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.split.update");

        var rows = ReadSolidBodies(document);
        var stateAfter = ReadFeatureState(entity);
        var featuresAfter = CountFeatures(document);
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        // PARTS ARE RECOGNISED BY CHANGE, not by matching the expectation. The former edition fed ALL
        // document bodies into UnmatchedVolume and published their volumes as observed: the expectation
        // was sought ANYWHERE in the document, so a foreign body whose volume accidentally matched a
        // declared part closed the declaration; an extra part did not prevent a pass; and a composition
        // mismatch could not be told from bad arithmetic (defect
        // CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, order §4.2). Here the set of parts is taken from
        // BODY-COMPOSITION MATCHING and does not depend on the declared expectation at all. The predicate
        // is changes.Touched (volume OR bounding box changed), not changes.Changed: a part that moved
        // with the same volume is still a part of this split and must not drop out of the set.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var createdBodies = changes.NewBodies;
        var partSnapshots = changes.Touched
            .Select(changes.MatchedOf)
            .Where(s => s is not null)
            .Select(s => s!)
            .Concat(createdBodies)
            .ToList();

        var claimedSnapshots = new HashSet<int>();
        var parts = new List<SolidBodyDto>();
        var foreign = new List<SolidBodyDto>();
        foreach (var row in rows)
        {
            var hit = -1;
            for (var j = 0; j < partSnapshots.Count; j++)
            {
                if (!claimedSnapshots.Contains(j) && MatchesSnapshot(row, partSnapshots[j]))
                {
                    hit = j;
                    break;
                }
            }

            if (hit >= 0)
            {
                claimedSnapshots.Add(hit);
                parts.Add(row);
            }
            else
            {
                foreign.Add(row);
            }
        }

        // Parts are recognised only when the operation left a VISIBLE trace. If nothing changed and
        // nothing appeared, the set of parts of THIS split cannot be derived from the change, and
        // substituting all document bodies for it is not allowed — that was the defect.
        var partsIdentified = partSnapshots.Count > 0;
        var declaredCount = expectedParts?.Count ?? 0;

        string? volumeMiss = null;
        if (expectedParts is not null && partsIdentified)
        {
            // A MULTISET COMPARISON BY VOLUME is done: equal cardinality plus an injective match of each
            // declaration to its own part — this is multiset equality. Multiplicity is accounted for, a
            // part does not close two declarations and is not substituted by a foreign body.
            volumeMiss = parts.Count != declaredCount
                ? $"частей {parts.Count}, а объявлено {declaredCount}"
                : UnmatchedVolume(parts, expectedParts);
        }

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featuresAfter == featuresBefore,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
        };

        if (expectedParts is not null)
        {
            checks.Add(new NamedCheck(
                "expected_part_volumes",
                volumeMiss is null && partsIdentified,
                Observed: partsIdentified
                    ? $"части ({parts.Count}): " + BodyVolumesText(parts)
                    : "<части не опознаны: ни одно тело не изменилось и не появилось>",
                Expected: $"части ({declaredCount}): " + NumberListText(expectedParts)));
        }

        if (partsIdentified)
        {
            checks.Add(new NamedCheck(
                "foreign_bodies_unchanged", vanished.Count == 0,
                Observed: vanished.Count > 0
                    ? "исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index))
                    : foreign.Count == 0 ? "<посторонних тел нет>" : BodyVolumesText(foreign),
                Expected: "посторонние тела не изменились и ни одно не исчезло"));
        }

        var unverified = new List<string>();
        if (expectedParts is null)
        {
            unverified.Add("expected_part_volumes_not_supplied — сумма объёмов частей при правке "
                           + "разделения не меняется, поэтому подтвердить правку может только состав "
                           + "частей (expected_part_volumes_mm3)");
        }
        else if (!partsIdentified)
        {
            unverified.Add("parts_not_identified_by_change — ни одно тело не изменилось и не появилось, "
                           + "поэтому ЧАСТИ ЭТОГО разделения опознать нечем: список всех тел документа "
                           + "частями не является, а сверять объявление с ним значило бы подтверждать "
                           + "ожидание подбором. Объявленное ожидание НЕ проверено.");
        }

        if (vanished.Count > 0)
        {
            unverified.Add("foreign_bodies_vanished — при правке разделения исчезли тела "
                           + string.Join(", ", vanished.Select(b => "тел" + b.Index))
                           + ": разделение ЗАМЕНЯЕТ тело на части и ничего не удаляет");
        }

        // Neither a bounding box nor a volume changed: the feature may already have had such a support.
        // This is not a refusal (and the declared expectation is checked above — it is compared with the
        // MODEL itself), but neither is it silence: this call cannot tell "the support was already like
        // this" from "the write was not applied" by reading the support back — the support-read route on
        // a live feature was not measured.
        if (!partsIdentified && vanished.Count == 0)
        {
            unverified.Add("geometry_unchanged_by_edit — ни один габарит и объём тела не изменились: "
                           + "отличить «опора уже была такой» от «запись не применилась» этим вызовом "
                           + "нельзя. Объявленный состав частей сверен с моделью, а не с изменением.");
        }

        if (expectedParts is not null && volumeMiss is not null)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Опора переписана, но состав частей не совпал с объявленным: " + volumeMiss
                + ". Сверялся состав ЧАСТЕЙ ЭТОГО разделения (тела, изменившиеся или появившиеся в "
                + "этой операции), а не любой набор тел документа. Это признак того, что параметр "
                + "применён не к исходным входам признака: правка обязана резать ИСХОДНОЕ тело, а не "
                + "уже полученные части (наряд §5).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["part_volumes_observed"] = parts.Select(p => p.VolumeMm3).ToArray(),
                    ["foreign_body_volumes"] = foreign.Select(p => p.VolumeMm3).ToArray(),
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["expected_part_volumes_mm3"] = expectedParts,
                    ["unmatched"] = volumeMiss,
                });
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            SplitFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(
                expectedParts is not null && checks.TrueForAll(c => c.Passed)
                    ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>Editing an EXISTING cut feature: a new support and a new kept side.</summary>
    /// <remarks>
    /// <b>The route is MEASURED 18.09.2026</b> (probe <c>--split</c>, step SP.9, run
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>), by two separate experiments: E-C — transferring the
    /// support points by +5 along X changes the remainder from 6000 to 9000; E-D — changing ONLY
    /// <c>Direction</c> on the same feature changes the remainder from 9000 to 15000. The former edition
    /// of this handler substituted ANOTHER plane into the feature, and this is measured not to work
    /// (negative control E-B).
    /// <b>The definition is enumerated in full.</b> BOTH the support AND the side are required: reading
    /// the current support and side from a live feature and filling in the missing part was not measured,
    /// and silently performing half the request means reporting an edit that did not happen.
    /// History: docs/decisions/adapter-solid.md#edit-cut</remarks>
    private UpdateFeatureResult UpdateSolidCutByPlane(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            CutByPlaneFamily,
            "plane (опора), keep_side (оставляемая сторона), target_body_ref (область применения), "
            + "expected_volume_mm3 и expected_bbox_mm");

        if (command.ExpectedPartVolumesMm3 is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке отсечения expected_part_volumes_mm3 не принимается: отсечение оставляет "
                + "ОДНО тело, а не набор частей. Объявите expected_volume_mm3 (объём остатка).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        if (command.Plane is not CutPlaneDto planeSpec)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка отсечения требует plane: опора перечисляется целиком, а не додумывается из "
                + "состояния модели. Опору при этом МОЖНО прочитать — kompas_get_feature отдаёт её "
                + "в блоке solid.plane (маршрут измерен 18.09.2026 пробой --split, шаг SP.10); "
                + "требование полноты остаётся потому, что ответ на частичный запрос не отличил бы "
                + "«изменилось ровно то, что просили» от «изменилось заодно и то, о чём промолчали». "
                + "Рабочий порядок — прочитать признак, подставить прочитанное и изменить нужное поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        if (command.KeepSide is not string side)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка отсечения требует keep_side: сторона — такой же параметр признака, как опора, "
                + "и угадывать её из текущего состояния модели нельзя.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        var keepPositive = side switch
        {
            "positive" => true,
            "negative" => false,
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"keep_side '{side}' не распознан: ожидалось 'positive' (s > 0) или 'negative' (s < 0).",
                details: new Dictionary<string, object?> { ["keep_side"] = side }),
        };

        var spec = ResolveSupportPlane(planeSpec, CutByPlaneFamily);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.cut_by_plane.update");
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.CutByPlane,
            Api7SolidCut.Count(container),
            CutByPlaneFamily,
            "Cuts");

        // The application scope is restored on edit just as it is assigned on creation (order §3.2).
        // Without target_body_ref it is read back before mutation and an unaddressed feature is rejected
        // — otherwise moving the support would remove material from foreign bodies, because the scope
        // default is «Все объекты» (help rezultat_oper_v_zavisimosti_ot_s_o.html).
        IKompasAPIObject? targetBody = null;
        if (command.TargetBodyRef is string targetRef)
        {
            var target = ResolveBodyTarget(document, document.PartNow(), targetRef, bodiesBefore);
            if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject transferred)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Тело для области применения не перенесено в API7 как IKompasAPIObject: "
                    + (bridge.BridgeFailure ?? "TransferInterface вернул null")
                    + ". Правка признака с незаданной областью применения сняла бы материал у "
                    + "посторонних тел, поэтому вызов отвергнут до мутации.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = targetRef,
                        ["target_body_index"] = target.Index,
                        ["code"] = "target_body_not_transferred",
                    });
            }

            targetBody = transferred;
        }

        var written = Api7SolidCut.TryMoveSupportAndSide(
            container, index, (spec.P1, spec.P2, spec.P3), keepPositive, targetBody);
        if (!written.Updated)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение не переписано: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = written.Failure,
                    ["api7_index"] = index,
                    ["keep_side"] = side,
                    ["target_body_ref"] = command.TargetBodyRef,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.cut_by_plane.update");

        var rows = ReadSolidBodies(document);
        var stateAfter = ReadFeatureState(entity);
        var featuresAfter = CountFeatures(document);
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        // The remainder is recognised BY CHANGE: it is the body that matched none of the "before"
        // snapshots. The former edition sought it as "the body whose volume differs from the target's",
        // but there is no target in the edit — CREATING the feature consumed it, so there is nothing to
        // identify the remainder with. Here the selection is structural and does NOT depend on the
        // declared expectation: fitting a body to the expectation would mean checking the expectation by
        // itself.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var createdBodies = changes.NewBodies;
        var changed = changes.Changed;

        // "Touched" is wider than "volume changed": a body that moved is touched too, and the addressing
        // check must see it. While only the volume stood here, a cut that shifted a foreign bar passed as
        // "exactly one body touched".
        var touched = changes.Touched;

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featuresAfter == featuresBefore,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
        };

        var declaredVolume = command.ExpectedVolumeMm3;
        var unverified = new List<string>();

        // ADDRESSING IS THE SUBJECT OF THIS CALL. A cut leaves EXACTLY one remainder body, so "exactly
        // one body touched, none vanished and none appeared" is not decoration of the response but what
        // the call must confirm. Previously there stood here
        // changed = rows.Where(r => !MatchesAnySnapshot(r, bodiesBefore)): the walk went ONLY over bodies
        // AFTER the operation, so a vanished body never entered the list, and an edit that swept away a
        // foreign bar looked like "exactly one body changed" and passed as geometry_checked — this is the
        // false confirmation from client acceptance 19.09.2026 (defect
        // CUT-PLANE-APPLIED-TO-UNNAMED-BODIES, order §3.1).
        var addressingOk = touched.Count == 1 && vanished.Count == 0 && createdBodies.Count == 0;

        if (!addressingOk && (touched.Count > 0 || vanished.Count > 0 || createdBodies.Count > 0))
        {
            var parts = new List<string>();
            if (touched.Count > 1)
            {
                parts.Add("затронуты тела " + string.Join(", ", touched.Select(i => "тел" + i))
                    + " (" + string.Join("; ", touched.Select(i => "тел" + i + ": " + ChangeKind(changes, i))) + ")");
            }

            if (vanished.Count > 0)
            {
                parts.Add("исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index)));
            }

            if (createdBodies.Count > 0)
            {
                parts.Add("появились новые тела "
                    + string.Join(", ", createdBodies.Select(b => "тел" + b.Index)));
            }

            if (touched.Count == 0)
            {
                parts.Add("ни одно тело не изменилось ни объёмом, ни положением");
            }

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка отсечения затронула не только тело-остаток: " + string.Join("; ", parts)
                + ". Отсечение оставляет ОДНО тело, и объявленное ожидание к неизвестному телу не "
                + "применяется. Мутация уже выполнена — откат не обещается. Фактический состав тел: "
                + BodyVolumesText(rows) + "; ревизия после мутации " + document.Revision
                + ". Прочитайте состав тел и повторите операцию на исправленном состоянии.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                    ["keep_side"] = side,
                    ["target_body_ref"] = command.TargetBodyRef,
                    ["changed_bodies"] = changed.ToArray(),
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["created_bodies"] = createdBodies.Select(b => b.Index).ToArray(),
                    ["body_volumes_before"] = BodyVolumesText(bodiesBefore),
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["code"] = "cut_not_addressed_to_target_body",
                });
        }

        if (changed.Count == 1)
        {
            var remainingSnapshot = changes.MatchedOf(changed[0]);
            var remaining = remainingSnapshot is null
                ? null
                : rows.FirstOrDefault(r => MatchesSnapshot(r, remainingSnapshot));

            if (remaining is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Изменившееся тело не найдено в списке тел после правки: сопоставление состава не "
                    + "дало ему пары. Объявить ожидание нечем.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["revision_after"] = document.Revision,
                        ["changed_bodies"] = changed.ToArray(),
                        ["body_volumes_after"] = BodyVolumesText(rows),
                    });
            }

            checks.Add(new NamedCheck(
                "target_body_affected",
                true,
                Observed: "тел" + changed[0] + ": ΔV=" + Num(changes.DeltaOf(changed[0])),
                Expected: "изменение объёма тела-остатка"));
            checks.Add(new NamedCheck(
                "nontarget_bodies_unchanged", vanished.Count == 0 && createdBodies.Count == 0,
                Observed: "исчезло " + Num(vanished.Count) + ", появилось " + Num(createdBodies.Count),
                Expected: "0 и 0"));
            checks.Add(new NamedCheck("no_bodies_vanished", vanished.Count == 0,
                Observed: Num(vanished.Count), Expected: "0"));
            checks.Add(new NamedCheck("no_bodies_created", createdBodies.Count == 0,
                Observed: Num(createdBodies.Count), Expected: "0"));

            if (declaredVolume is double expected)
            {
                checks.Add(new NamedCheck(
                    "volume_expected",
                    remaining.VolumeMm3 is double measured
                        && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                    Observed: Num(remaining.VolumeMm3),
                    Expected: Num(expected)));
            }

            if (command.ExpectedBboxMm is BoundingBoxDto expectedBox)
            {
                checks.Add(new NamedCheck(
                    "bbox_expected",
                    BoxMatches(remaining.Bbox, expectedBox),
                    Observed: BoxText(remaining.Bbox),
                    Expected: BoxText(expectedBox)));
            }

            if (declaredVolume is null && command.ExpectedBboxMm is null)
            {
                unverified.Add("no_expectation_supplied — без аналитического ожидания "
                               + "(expected_volume_mm3 или expected_bbox_mm) правка отсечения не может "
                               + "быть подтверждена геометрически: адресность проверена по составу тел, "
                               + "а объём остатка — нет");
            }

            if (checks.Any(c => !c.Passed))
            {
                var failed = string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name));
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    "Отсечение переписано, но геометрия не совпала с объявленной: " + failed
                    + ". Остаток — " + BoxText(remaining.Bbox) + " при объёме "
                    + Num(remaining.VolumeMm3) + ".",
                    RetryPolicy.SameOperationId,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["revision_after"] = document.Revision,
                        ["requested_plane_point_mm"] = spec.Point,
                        ["requested_plane_normal_mm"] = spec.UnitNormal,
                        ["keep_side"] = side,
                        ["remaining_bbox"] = BoxText(remaining.Bbox),
                        ["remaining_volume_mm3"] = remaining.VolumeMm3,
                        ["expected_volume_mm3"] = declaredVolume,
                        ["expected_bbox"] = command.ExpectedBboxMm is null
                            ? null
                            : BoxText(command.ExpectedBboxMm),
                    });
            }

            return new UpdateFeatureResult(
                ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
                CutByPlaneFamily,
                sameFeature,
                featuresAfter,
                volumeBefore,
                ReadVolume(document),
                null,
                null,
                new VerificationDto(
                    (declaredVolume is not null || command.ExpectedBboxMm is not null)
                        && checks.TrueForAll(c => c.Passed)
                        ? VerificationLevel.GeometryChecked
                        : VerificationLevel.CallReturned,
                    checks,
                    unverified));
        }

        // Only two cases land here: exactly one body changed (handled above), or NO body changed,
        // vanished or appeared. The "more than one changed" branch above became unreachable on purpose:
        // it was a separate refusal for exactly the same subject as the addressing check, and two checks
        // of one case would diverge — that is precisely why the former edition missed a vanished body:
        // the `changed.Count > 1` branch counted only bodies AFTER the operation.
        //
        // No body changed — this is NOT a refusal: the feature may already have had such support and
        // side. But neither is it confirmation: this call cannot tell "the parameters were already like
        // this" from "the write was not applied" — reading the support and side back on a live feature
        // was not measured. So the outcome is honest: the call passed, the level is "call returned", and
        // the reason is named.
        //
        // The case "the body moved but the volume did not change" is named separately: a cut removes
        // material, so a remainder without ΔV is not a remainder, and a declared expectation is not
        // applied to it.
        var movedOnly = touched.Count == 1 && changed.Count == 0;
        checks.Add(new NamedCheck(
            "geometry_changed",
            false,
            Observed: movedOnly
                ? "тел" + touched[0] + " переехал без изменения объёма ("
                    + ChangeKind(changes, touched[0]) + ")"
                : "ни одно тело не изменилось",
            Expected: "изменение остатка"));
        unverified.Add(movedOnly
            ? "geometry_unchanged_by_edit — отсечение оставило тело с прежним объёмом: материал не "
              + "убран, остаток не опознан. Объявленное ожидание НЕ проверено."
            : "geometry_unchanged_by_edit — ни один габарит и объём тела не изменились: "
              + "отличить «опора и сторона уже были такими» от «запись не применилась» этим "
              + "вызовом нельзя. Объявленное ожидание НЕ проверено: тело-остаток не опознано.");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            CutByPlaneFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(VerificationLevel.CallReturned, checks, unverified));
    }

    /// <summary>Editing the KIND of an existing boolean operation (order §7, action <c>edit</c>).</summary>
    /// <remarks>
    /// <b>The route is MEASURED</b> by probe <c>--boolean</c>, step <c>BO.11</c>, run
    /// <c>a2f5cf0a2ad342c59c36807101a65d51</c>: rewriting <c>IBoolean.BooleanType</c> on an EXISTING
    /// feature + <c>Update()</c> + rebuild changes the geometry (E-A <c>36 000 → 12 000</c> in the
    /// bounding box <c>x ≤ 20</c>; E-D <c>12 000 → 36 000</c>). Experiment E-E confirmed that it is the
    /// PAIR "write → <c>Update()</c>" that applies: a write without <c>Update()</c> but with a rebuild
    /// does not change the geometry.
    /// <b>Why the bounding box and not only the volume.</b> The volume of the difference and the volume
    /// of the intersection on the reference of §6.1 are EQUAL (12 000), so checking one volume alone
    /// would also confirm complete inaction. The bounding box distinguishes: the difference lies in
    /// <c>x ≤ 20</c>, the intersection — in <c>x ∈ [20,40]</c>. This is the same lesson as in row
    /// <c>B3L.04</c> (for a translation the volume before and after is 1 000, and a row checking only
    /// volumes would pass on inaction), and it is repeated here on its own family rather than carried
    /// over.
    /// <b>What the edit does not do.</b> The operand bodies (<c>BaseObject</c>, <c>ModifyObjects</c>) and
    /// the tool-preservation policy are NOT rewritten: editing the tool set was not measured, and
    /// rewriting an untested route would pass the unreached off as reached.
    /// History: docs/decisions/adapter-solid.md#edit-boolean</remarks>
    private UpdateFeatureResult UpdateSolidBoolean(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            BooleanFamily,
            "operation (новый вид операции), expected_volume_mm3 и expected_bbox_mm");

        if (command.Operation is not BooleanOperation operation)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка булевой операции требует operation (union, difference или intersect): без вида "
                + "операции менять нечего, а вывести его из текущего состояния значило бы принять "
                + "решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = BooleanFamily });
        }

        if (!DeclaresExpectation(command))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка булевой операции требует аналитического ожидания — expected_volume_mm3 и/или "
                + "expected_bbox_mm: успешный Update() доказательством не является (измерено: у переноса "
                + "три маршрута из четырёх возвращают успех и тело не двигают), а объём разности и "
                + "пересечения на эталоне РАВНЫ, поэтому одного объёма мало — габарит различает.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = BooleanFamily });
        }

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.boolean.update");

        // The body composition BEFORE the edit — not for addressing (a boolean operation's inputs are
        // consumed on purpose), but so the result is recognised independently of the declared expectation.
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.BooleanOperation,
            Api7SolidBoolean.Count(container),
            BooleanFamily,
            "Booleans");

        var operationBefore = Api7SolidBoolean.ReadOperation(container, index);
        var written = Api7SolidBoolean.TryWriteOperation(container, index, operation);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Вид булевой операции не переписан: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_operation"] = operation.ToString(),
                    ["api7_failure"] = written.Failure,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.boolean.update");

        var operationAfter = Api7SolidBoolean.ReadOperation(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);
        var rowsAfter = ReadSolidBodies(document);

        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);
        var featureCountPreserved = featuresAfter == featuresBefore;
        var operationPreserved = operationAfter == operation;

        // SUBJECT OF PROOF. The result of a boolean operation is the CHANGED (or newly appeared) body,
        // NOT "any document body whose bounding box matched the declared one". The former edition sought
        // the body by matching the EXPECTATION:
        //   rowsAfter.FirstOrDefault(r => BoxMatches(r.Bbox, command.ExpectedBboxMm))
        // and published the volumes of ALL document bodies as observed — that is, it compared a bounding
        // box with numbers of another kind and another subject. Separately: a body found by SUCH a search
        // may be foreign — a bounding box matching the expectation does not make the body the result of
        // THIS operation, so the "confirmation" rested on fitting to the expectation (defect
        // CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, order §4.1).
        //
        // A second foray into the same defect (MEASURED 19.09.2026 on delivery
        // publish-b3-20260919-targeting, row B3.25): recognition went by changes.Changed, which is the
        // list of bodies with a changed VOLUME. On the reference the volume of the difference and the
        // volume of the intersection are EQUAL (12 000 mm³), only the bounding box position
        // distinguishes them, so a correctly applied `intersect` edit landed in resultBodies.Count == 0
        // and was rejected as NO_GEOMETRY_CHANGE. The predicate "the result changed" must also cover a
        // body that moved: changes.Touched = volume OR bounding box changed.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var resultBodies = changes.Touched
            .Select(changes.MatchedOf)
            .Where(s => s is not null)
            .Select(s => s!)
            .Concat(changes.NewBodies)
            .ToList();
        var resultBody = resultBodies.Count == 1
            ? rowsAfter.FirstOrDefault(r => MatchesSnapshot(r, resultBodies[0]))
            : null;
        var resultIdentity = resultBodies.Count switch
        {
            0 => "ни одно тело не изменилось и не появилось — результата операции не видно",
            1 => "тел" + resultBodies[0].Index + " (" + ChangeKind(changes, resultBodies[0].Index)
                 + ", габарит [" + Range(resultBodies[0].Min) + "|" + Range(resultBodies[0].Max) + "])",
            _ => "изменившихся и появившихся тел " + resultBodies.Count
                 + " — какое из них результат операции, по этим данным не установить",
        };

        var bboxMatched = command.ExpectedBboxMm is not null
                         && resultBody is not null
                         && BoxMatches(resultBody.Bbox, command.ExpectedBboxMm);

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featureCountPreserved,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
            new("operation_read_back", operationPreserved,
                Observed: operationAfter?.ToString() ?? "<не прочитан>", Expected: operation.ToString()),
        };

        if (command.ExpectedBboxMm is not null)
        {
            // Expectation, observation and verdict refer to ONE subject — the bounding box of ONE body,
            // recognised by change, not by matching the expectation. The observed type is the same as the
            // expected one: a bounding box against a bounding box, not a list of volumes against a
            // bounding box.
            checks.Add(new NamedCheck(
                "bbox_expected", bboxMatched,
                Observed: resultBody is null ? "<результат не опознан: " + resultIdentity + ">"
                    : BoxText(resultBody.Bbox),
                Expected: BoxText(command.ExpectedBboxMm)));
            checks.Add(new NamedCheck(
                "result_body_identity", resultBody is not null,
                Observed: resultIdentity,
                Expected: "ровно одно тело, изменившееся или появившееся в этой операции"));
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            // The DOCUMENT volume and the RESULT volume are different quantities, and they must not be
            // mixed. Here the document-volume expectation is declared (that is how the caller declares it
            // for a boolean operation), so the document volume is what is observed; the result volume is
            // published by a separate check, not substituted into the same row.
            checks.Add(new NamedCheck(
                "volume_expected",
                volumeAfter is double measured && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                Observed: Num(volumeAfter) + " (объём документа)",
                Expected: Num(expected)));

            if (resultBody is not null)
            {
                checks.Add(new NamedCheck(
                    "result_body_volume",
                    true,
                    Observed: Num(resultBody.VolumeMm3) + " (объём тела-результата)",
                    Expected: "наблюдение, а не ожидание: объём результата отделён от объёма документа"));
            }
        }

        var volumeMatched = command.ExpectedVolumeMm3 is null
            || (volumeAfter is double m && Math.Abs(m - command.ExpectedVolumeMm3.Value)
                <= ProfileArea.Tolerance(command.ExpectedVolumeMm3.Value));
        var bboxOk = command.ExpectedBboxMm is null || bboxMatched;

        if (!volumeMatched || !bboxOk)
        {
            // Order §5: on a partial mutation, return an error with the actual state. Here the mutation
            // has already happened (the kind is rewritten and applied), so the refusal must carry both the
            // revision and the actual geometry — otherwise the client's next call fails
            // REVISION_CONFLICT.
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Вид операции переписан, но геометрия не совпала с объявленной: "
                + string.Join("; ", new[]
                {
                    volumeMatched ? null : "volume_expected",
                    bboxOk ? null : "bbox_expected",
                }.Where(x => x is not null))
                + $". Тела после правки: {BodyVolumesText(rowsAfter)} при суммарном объёме "
                + $"{Num(volumeAfter)}.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_operation"] = operation.ToString(),
                    ["operation_before"] = operationBefore?.ToString(),
                    ["operation_after"] = operationAfter?.ToString(),
                    ["expected_volume_mm3"] = command.ExpectedVolumeMm3,
                    ["volume_after_mm3"] = volumeAfter,
                    ["expected_bbox_mm"] = command.ExpectedBboxMm is null ? null : BoxText(command.ExpectedBboxMm),
                    ["body_volumes_after"] = BodyVolumesText(rowsAfter),
                });
        }

        var unverified = new List<string>();
        if (command.ExpectedBboxMm is null)
        {
            unverified.Add("expected_bbox_not_supplied — объём разности и объём пересечения на эталоне "
                           + "равны (12 000), поэтому без габарита правка на этих видах неотличима от "
                           + "полного бездействия");
        }

        // THE LIMIT OF RECOGNITION, named explicitly. Recognising the result rests on observable
        // quantities — volume and bounding box. A body whose shape changed but whose volume and all six
        // bounding-box coordinates matched is indistinguishable from inaction by these measurements, and
        // it must not be declared recognised. The wording stands here because silence about it would read
        // as "any change of the result is detected".
        unverified.Add("result_identity_by_observables — результат опознаётся по изменению объёма или "
                       + "габарита; тело, изменившее форму при совпавших объёме и габарите, этим "
                       + "сравнением не обнаруживается");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            BooleanFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    /// <summary>Why the body landed in the "result of the operation" set: its volume changed, only its bounding
    /// box shifted, or the body appeared. The string is needed by the caller to tell "the edit worked
    /// with material" from "the edit moved the body" — these two observations require different
    /// conclusions, and they cannot be told apart by the mere fact of being in the list.</summary>
    private static string ChangeKind(BodyComparison changes, int index)
    {
        if (changes.NewBodies.Any(n => n.Index == index))
        {
            return "новое тело";
        }

        if (changes.Changed.Contains(index))
        {
            return "изменился объём, ΔV="
                + (changes.DeltaOf(index) is double delta ? Range(delta) : "?")
                + " мм³";
        }

        if (changes.BoxChangedOf(index))
        {
            return "объём не изменился, сдвиг габарита "
                + Range(changes.BoxShiftOf(index))
                + " мм";
        }

        return "изменение не подтверждено";
    }

    /// <summary>Checking the declared part volumes: each expectation gets its own body, order does not matter.</summary>
    /// <remarks>The REASON for the mismatch is returned, not a <c>bool</c>: the refusal needs to name which volume
    /// was not found, otherwise the caller has nothing to fix. A body that has already closed one
    /// expectation is not counted towards a second one — otherwise the list <c>[6000, 6000]</c> would be
    /// confirmed by a single body of 6 000.</remarks>
    private static string? UnmatchedVolume(IReadOnlyList<SolidBodyDto> rows, IReadOnlyList<double> expected)
    {
        var used = new bool[rows.Count];
        foreach (var want in expected)
        {
            var hit = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (used[i] || rows[i].VolumeMm3 is not double volume)
                {
                    continue;
                }

                if (Math.Abs(volume - want) <= ProfileArea.Tolerance(want))
                {
                    hit = i;
                    break;
                }
            }

            if (hit < 0)
            {
                return "не найдено тело с объёмом " + Num(want) + " (тела: " + BodyVolumesText(rows) + ")";
            }

            used[hit] = true;
        }

        return null;
    }

    /// <summary>Volumes of the declared parts: at least two (a split yields no fewer than two parts).</summary>
    private static IReadOnlyList<double>? RequirePartVolumes(IReadOnlyList<double>? declared)
    {
        if (declared is null)
        {
            return null;
        }

        if (declared.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"expected_part_volumes_mm3 содержит {declared.Count} значений: разделение даёт не "
                + "меньше двух частей, поэтому одного ожидания мало — оно не отличило бы разделение "
                + "от бездействия.",
                details: new Dictionary<string, object?> { ["expected_part_volumes_mm3"] = declared });
        }

        foreach (var value in declared)
        {
            if (!double.IsFinite(value) || value < 0d)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Объёмы частей обязаны быть конечными неотрицательными числами.",
                    details: new Dictionary<string, object?> { ["expected_part_volumes_mm3"] = declared });
            }
        }

        return declared;
    }

    private static string BodyVolumesText(IReadOnlyList<SolidBodyDto> rows) =>
        NumberListText(rows.Select(r => r.VolumeMm3).ToList());

    private static string NumberListText(IReadOnlyList<double?> values) =>
        "[" + string.Join(", ", values.Select(Num)) + "]";

    private static string NumberListText(IReadOnlyList<double> values) =>
        "[" + string.Join(", ", values.Select(v => Num(v))) + "]";

    /// <summary>A number or an honest "not read" — for <c>details</c> and checks.</summary>
    private static string Describe(int? value) => value is int number ? number.ToString() : "<не прочитано>";
}
