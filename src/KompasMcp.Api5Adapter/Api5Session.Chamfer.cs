using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Chamfer (docs/05 SM-11): creation by a second method, read and parameter edit.</summary>
/// <remarks>
/// Basis — probe F of 12.09.2026 (<c>docs/acceptance/api7/chamfer.md</c>), not method names.
/// TEST: F.2 — the API5 route works: <c>NewEntity(o3d_chamfer=33)</c> →
/// <c>ksChamferDefinition.SetChamferParam(transfer, d1, d2)</c> → <c>array()</c> as
/// <c>ksEntityCollection</c> → <c>Add(edge)</c> → <c>Create()</c> → <c>RebuildDocument()</c>; four
/// vertical edges of a 100×80×10 plate with 2×2 legs removed exactly 20·d₁·d₂ = 80 mm³.
/// TEST: F.3/F.5 — editing the legs applies in place both before and after save→close→reopen, and the
/// value is read back (<c>ok=True transfer=False d1=3 d2=3</c>).
/// TEST: F.4/F.11 — <c>transfer</c> (aka <c>IChamfer.Direction</c>) changes which leg lands on which
/// face; volume does not distinguish this, the side-face areas do.
/// TEST: F.8 — an API5 chamfer is visible from API7 as <c>IChamfer</c> and its parameters read typed,
/// but the name reads differently in API7 («f-ch2» → «Фаска:1»), so the feature is identified by
/// registry object and type, not by name.
/// TEST: F.9/F.10 — with the "distance and angle" method the chamfer is built only via
/// <c>IChamfer.Angle = ksChamferSideAngle</c>, the angle in DEGREES: 30 with a 2-mm leg removed
/// 46.188021535141 mm³, which is 20·d·(d·tg 30°); the radian hypothesis would have given a negative
/// number and was refuted by measurement.
/// LIMIT: editing chamfer parameters is NOT the same as re-binding the extrusion's base sketch
/// (Q-EDIT-SKETCH, <c>edit = blocked_api</c>): there the refusal was measured on the profile-change
/// route, while here a number change is measured and works. Conversely, editing the angle of an
/// existing feature was NOT measured by this probe and is therefore explicitly refused, not "by
/// property presence".
/// History: docs/decisions/adapter-features.md#chamfer-route
/// </remarks>
public partial class Api5Session
{
    /// <summary>Chamfer family name in server responses.</summary>
    private const string ChamferFamily = "chamfer";

    /// <summary>Chamfer build method where the second leg is derived from the angle. The names are what
    /// <c>IChamfer.BuildingType.ToString()</c> returns from API7 (API5 has no method at all). Needed to
    /// recognise the feature for which the API5 write route loses the build method.</summary>
    private const string SideAngleBuildingType = "ksChamferSideAngle";

    /// <summary>The "two legs" method — the only one <c>SetChamferParam</c> can write.</summary>
    private const string TwoSidesBuildingType = "ksChamferTwoSides";

    /// <summary>API7 bridges — one per instance: a second bridge would mean a second session view.</summary>
    private readonly Dictionary<string, Api7Bridge> _api7Bridges = new(StringComparer.Ordinal);

    private Api7Bridge BridgeFor(DocumentEntry document)
    {
        var application = RequireApplication(document.ApplicationId);
        if (!_api7Bridges.TryGetValue(application.Id, out var bridge))
        {
            bridge = new Api7Bridge(application.Application);
            _api7Bridges[application.Id] = bridge;
        }

        return bridge;
    }

    /// <summary>Create a "distance + angle" chamfer — the only SM-11 mode API5 physically lacks. A
    /// bridge refusal is returned as <c>CAPABILITY_UNAVAILABLE</c> with a cause, not a silent null: the
    /// caller must tell "API7 unavailable" from "KOMPAS rejected the parameter".</summary>
    private ChamferResult ChamferByAngle(
        DocumentEntry document,
        IReadOnlyList<ksEntity> entities,
        ChamferCommand command,
        double? volumeBefore,
        double? facesBefore,
        int bodiesBefore,
        HashSet<string> unwrapRoutes)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Способ «расстояние и угол» без него недоступен; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var angle = command.AngleDeg!.Value;
        var baseObjects = bridge.TransferAllTo7([.. entities.Cast<object>()]);
        if (baseObjects is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Рёбра не перенесены в API7 (" + (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                ") — фаска не создавалась.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var created = Api7Chamfer.TryCreateDistanceAngle(
            container, baseObjects, command.Distance1Mm, angle, command.Direction, name: null);
        if (!created.Created)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Фаска через IChamfer не создана: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = created.Failure });
        }

        // Without RebuildModel the IChamfer write stays a representation: MEASURED by probe E on
        // IExtrusion.Sketch, and the same call order is mandatory for the chamfer.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "chamfer.angle");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);
        var api5Feature = FindChamferEntity(document);
        var readBack = Api7Chamfer.Count(container) is int count and > 0
            ? Api7Chamfer.Read(container, count - 1)
            : null;

        var parametersStored = readBack is not null
            && Math.Abs((readBack.AngleDeg ?? double.NaN) - angle) <= 1e-6
            && Math.Abs((readBack.Distance1Mm ?? double.NaN) - command.Distance1Mm) <= 1e-6;

        var checks = new List<NamedCheck>
        {
            new("base_objects_transferred", baseObjects.Length == entities.Count,
                Observed: baseObjects.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Expected: entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("parameters_read_back", parametersStored,
                Observed: readBack is null
                    ? "IChamfer не читается"
                    : $"Angle={readBack.AngleDeg:0.####} Distance1={readBack.Distance1Mm:0.####} Direction={readBack.Direction} способ={readBack.BuildingType}",
                Expected: $"Angle={angle:0.####} Distance1={command.Distance1Mm:0.####} Direction={command.Direction}"),
            new("visible_from_api5", api5Feature is not null,
                Observed: api5Feature is null ? "элемент type=33 в дереве API5 не найден" : $"«{api5Feature.name}»"),
            new("face_count_grew", facesAfter is double after && facesBefore is double before
                && after - before == entities.Count,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: $"+{entities.Count}"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        var materialRemoved = volumeBefore is double b && volumeAfter is double a && a < b;
        bool? numericMatch = null;
        if (command.ExpectedVolumeDeltaMm3 is double expectedDelta
            && volumeBefore is double vBefore && volumeAfter is double vAfter)
        {
            var measured = vBefore - vAfter;
            numericMatch = Math.Abs(measured - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
            checks.Add(new NamedCheck(
                "volume_delta",
                numericMatch.Value,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(new NamedCheck(
                "volume_delta",
                materialRemoved,
                Observed: volumeBefore is double cb && volumeAfter is double ca
                    ? (cb - ca).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    : "not_computable",
                Expected: "не задано — проверено только направление"));
        }

        var geometryConfirmed = created.Created
            && parametersStored
            && checks.Exists(c => c.Name == "face_count_grew" && c.Passed)
            && materialRemoved
            && numericMatch is not false;

        var unverified = new List<string>
        {
            "angle_units_measured_once — градусы подтверждены одним замером на прямых рёбрах " +
            "перпендикулярных граней (F.10); на дугах и наклонных рёбрах единица не проверялась",
        };
        if (!geometryConfirmed)
        {
            unverified.Insert(0, "geometry_not_confirmed — КОМПАС принял запись, но измерение не подтвердило ожидаемую геометрию");
        }

        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add("expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал вызывающий, численного доказательства нет");
        }

        if (api5Feature is null)
        {
            // A reference to a feature API5 does not see must not be issued: an edit through it would
            // fail anyway, and the caller would learn of it later.
            unverified.Add("feature_ref_withheld — признак не найден в дереве API5, ссылка не выдана");
            return new ChamferResult(
                null,
                entities.Count,
                readBack?.Distance1Mm,
                null,
                readBack?.Direction,
                bodiesAfter,
                volumeAfter,
                new VerificationDto(VerificationLevel.CallReturned, checks, unverified),
                string.Join(", ", unwrapRoutes));
        }

        var reference = References.Register("feature", document.Id, document.Revision, api5Feature);
        return new ChamferResult(
            ToDto(reference, $"chamfer {command.Distance1Mm:0.###} × {angle:0.###}° × {entities.Count}"),
            entities.Count,
            readBack?.Distance1Mm,
            null,
            readBack?.Direction,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", unwrapRoutes));
    }

    /// <summary>The last operation element with <c>type = 33</c> in the API5 tree. Search by type and
    /// order, not by name: F.8 MEASURED that a name given in API5 reads differently in API7 — the name
    /// is not a feature identifier.</summary>
    private static ksEntity? FindChamferEntity(DocumentEntry document)
    {
        try
        {
            ksEntity? found = null;
            if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                is not ksEntityCollection collection)
            {
                return null;
            }

            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity && entity.type == KompasObjectTypes.Chamfer)
                {
                    found = entity;
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    // ─── read and edit ──────────────────────────────────────────────────────────────────────

    /// <summary>What the server sees of a chamfer: legs and side from API5; if an API7 bridge is also
    /// built to the same document and there is one chamfer in it, angle and method are added. An empty
    /// field means "not read", not "zero".</summary>
    private ChamferDto? ReadChamfer(DocumentEntry document, object definition)
    {
        if (definition is not ksChamferDefinition chamfer)
        {
            return null;
        }

        var api5 = ReadChamferParam(chamfer);
        if (api5 is null)
        {
            return null;
        }

        var api7 = ReadChamferAngle(document, api5);
        return new ChamferDto(
            Transfer: api5.Transfer,
            Distance1Mm: api5.Distance1Mm,
            Distance2Mm: api5.Distance2Mm,
            AngleDeg: api7?.AngleDeg,
            BuildingType: api7?.BuildingType,
            // With two legs, "side" is read back from API7 as Direction; if there is no bridge, the
            // API5 transfer value remains — the same parameter of the same feature (F.4/F.11 MEASURED
            // that both change geometry identically).
            Direction: api7?.Direction ?? api5.Transfer,
            BaseObjectCount: api7?.BaseObjectCount);
    }

    /// <summary>Angle and method from API7 — only when the match is unambiguous. Measuring the angle of
    /// "the first chamfer in a row" with several chamfers would attribute a foreign number to the
    /// feature, so on ambiguity the server returns null and explains the cause in
    /// <c>unverified</c>.</summary>
    private ChamferReadDto? ReadChamferAngle(DocumentEntry document, ChamferParam api5)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null || Api7Chamfer.Count(container) is not int count)
        {
            return null;
        }

        var matches = new List<ChamferReadDto>();
        for (var i = 0; i < count; i++)
        {
            var read = Api7Chamfer.Read(container, i);
            // Matching by the first leg: it distinguishes chamfers from one another in the reference
            // acceptance cases and describes exactly what API5 wrote (F.8: D1=2, D2=2, Angle=45).
            if (read?.Distance1Mm is double d && Math.Abs(d - api5.Distance1Mm) <= 1e-6)
            {
                matches.Add(read);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Edit an angular chamfer by the API7 route: <c>IChamfer</c> on the live model, write →
    /// <c>Update()</c> → <c>RebuildModel()</c>; the only route able to write an angle (API5 has none).</summary>
    /// <remarks>Differences from the API5 route (<see cref="UpdateChamfer"/>): the feature is addressed by
    /// INDEX in <c>IModelContainer.Chamfers</c> and matched to the API5 feature by the first leg (the name
    /// is not an identifier — F.8); the second leg is DERIVED from the angle, so it is written only if the
    /// client set it explicitly; success is not "Update() returned true" but that the model gives the new
    /// angle and the leg <c>d₁·tg α</c>. INVARIANT: an ambiguous match (several chamfers with the same first
    /// leg, or none) is a refusal, not "took the first".</remarks>
    private UpdateFeatureResult UpdateChamferByAngle(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        var currentSource = entity.GetDefinition() as ksChamferDefinition;
        var current = currentSource is null ? null : ReadChamferParam(currentSource);
        if (current is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение фаски не перечитывается перед правкой: признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Угол фаски живёт только в IChamfer; признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var matches = Api7Chamfer.FindIndexesByIdenticalFirstLeg(container, current.Distance1Mm);
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                matches.Count == 0
                    ? "Фаска с первым катетом " + current.Distance1Mm.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм не найдена в IModelContainer.Chamfers: сопоставить признак API5 с IChamfer " +
                      "нечем, а записать угол в чужой признак нельзя. Признак не изменён."
                    : "Фаске с первым катетом " + current.Distance1Mm.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм отвечает " + matches.Count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " признаков API7 — сопоставление неоднозначно, и записывать угол «в первый " +
                      "попавшийся» означало бы изменить не тот признак. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["distance1_mm"] = current.Distance1Mm,
                    ["api7_matches"] = matches.Count,
                });
        }

        var index = matches[0];
        var before = Api7Chamfer.Read(container, index);

        // The second leg is written only if the client set it explicitly: otherwise it is derived.
        var distance1 = command.Distance1Mm;
        var distance2 = command.Distance2Mm;
        var angle = command.AngleDeg;
        var direction = command.Direction;

        var written = Api7Chamfer.TryWrite(container, index, distance1, distance2, angle, direction);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись в IChamfer не подтверждена: " + (written.Failure ?? "причина не сообщена") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        // Without the rebuild the IChamfer write stays a representation (the same order as on
        // creation: F.10 + probe E on IExtrusion.Sketch).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "chamfer.update.angle");

        var after = Api7Chamfer.Read(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var expectedDistance1 = distance1 ?? current.Distance1Mm;
        var expectedAngle = angle ?? before?.AngleDeg;
        // The derived leg is d₁·tg α, and this is what makes the check differ from "the value was
        // read back": if the kernel did not recompute the second leg, the angle was not truly applied.
        var expectedDerived = expectedAngle is double a && a > 0d && a < 90d
            ? expectedDistance1 * Math.Tan(a * Math.PI / 180d)
            : (double?)null;

        var angleStored = expectedAngle is null
            || (after?.AngleDeg is double readAngle && Math.Abs(readAngle - expectedAngle.Value) <= 1e-6);
        var distance1Stored = Math.Abs((after?.Distance1Mm ?? double.NaN) - expectedDistance1) <= 1e-6;
        var derivedStored = distance2 is not null
            ? Math.Abs((after?.Distance2Mm ?? double.NaN) - distance2.Value) <= 1e-6
            : expectedDerived is null
              || (after?.Distance2Mm is double readDerived
                  && Math.Abs(readDerived - expectedDerived.Value) <= 0.01);

        var parameterSource = entity.GetDefinition() as ksChamferDefinition;
        var api5ReadBack = parameterSource is null ? null : ReadChamferParam(parameterSource);
        var api5Agrees = api5ReadBack is null
            || Math.Abs(api5ReadBack.Distance1Mm - expectedDistance1) <= 1e-6;

        var buildingTypeKept = after?.BuildingType is null
            || before?.BuildingType is null
            || string.Equals(after.BuildingType, before.BuildingType, StringComparison.Ordinal);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IChamfer[{index}] Distance1={distance1?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "без изменений"} " +
                          $"Angle={angle?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "без изменений"} " +
                          $"Direction={direction?.ToString() ?? "без изменений"}",
                Expected: "запись принята и подтверждена перестроением"),
            new("angle_read_back", angleStored,
                Observed: $"Angle={after?.AngleDeg?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: $"Angle={expectedAngle?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не задан"}"),
            new("distance1_read_back", distance1Stored,
                Observed: $"Distance1={after?.Distance1Mm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: $"Distance1={expectedDistance1.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"),
            new("derived_leg_recomputed", derivedStored,
                Observed: $"Distance2={after?.Distance2Mm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: distance2 is not null
                    ? $"задан явно {distance2.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"
                    : expectedDerived is null
                      ? "производный от угла, угол не задан"
                      : $"{expectedDerived.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} (= d₁·tg α)"),
            new("building_type_kept", buildingTypeKept,
                Observed: $"{before?.BuildingType ?? "не читается"} → {after?.BuildingType ?? "не читается"}",
                Expected: "способ построения не подменён"),
            new("api5_sees_the_change", api5Agrees,
                Observed: api5ReadBack is null
                    ? "GetChamferParam не читается"
                    : $"d1={api5ReadBack.Distance1Mm:0.####} d2={api5ReadBack.Distance2Mm:0.####}",
                Expected: $"d1={expectedDistance1.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            volumeMatched = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_after_update",
                volumeMatched,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (after?.BuildingType == SideAngleBuildingType)
        {
            unverified.Add(
                "angle_units_measured_once — градусы подтверждены на прямых рёбрах перпендикулярных " +
                "граней (F.10 и эта правка); на дугах и наклонных рёбрах единица не проверялась");
        }

        var geometryConfirmed = angleStored && distance1Stored && derivedStored && buildingTypeKept
            && sameFeature && volumeMatched;
        if (!geometryConfirmed)
        {
            unverified.Insert(0, geometryConfirmed is false
                ? "geometry_not_confirmed — правка применена, но измерение не подтвердило ожидаемую геометрию"
                : "geometry_not_confirmed");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            ChamferFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // A chamfer has no depth: the field belongs to extrusion, so null rather than "zero".
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            AngleReadBackDeg: after?.AngleDeg);
    }

    /// <summary>Edit the legs and side of an existing chamfer. The value is read back from the new
    /// definition object, the feature must stay the same, and geometry is confirmed by volume
    /// measurement.</summary>
    private UpdateFeatureResult UpdateChamfer(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.Distance1Mm is <= 0d || command.Distance2Mm is <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Катеты фаски обязаны быть положительными: КОМПАС принимает ноль и создаёт признак " +
                "с нулевыми гранями при неизменном объёме (измерено пробой F.12).",
                RetryPolicy.Never);
        }

        if (command.Distance1Mm is null && command.Distance2Mm is null && command.AngleDeg is null
            && command.Direction is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для фаски нужен хотя бы один меняемый параметр: distance1_mm, distance2_mm, " +
                "angle_deg или direction.",
                RetryPolicy.Never);
        }

        // ─── API7 route: the only one able to write an angle ─────────────────────────────────
        // An angle is not physically expressible in API5 (ksChamferDefinition declares no "angle"
        // member), so with angle_deg present IChamfer writes. MEASURED 16.09.2026: writing Angle
        // followed by Update() applies to the model, the build method stays ksChamferSideAngle, and
        // the kernel recomputes the second leg as d₂ = d₁·tg α.
        if (command.AngleDeg is not null)
        {
            return UpdateChamferByAngle(document, entity, command, volumeBefore, featuresBefore,
                stateBefore);
        }

        // ─── API5 route: two legs and side ───────────────────────────────────────────────────
        // Only calls without angle_deg land here. If the feature's method is "distance and angle", the
        // API5 write would lose the angle (MEASURED: 30° → 45°, V 79896.07695154587 → 79820), so such
        // a call is refused BEFORE the mutation with an explanation of what to do instead.

        var currentSource = entity.GetDefinition() as ksChamferDefinition;
        var current = currentSource is null ? null : ReadChamferParam(currentSource);
        if (current is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение фаски не перечитывается перед правкой: признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // MEASURED ban on silent method substitution (probe 16.09.2026): the API5 write route
        // (SetChamferParam) only knows ksChamferTwoSides and does NOT preserve a "distance and angle"
        // chamfer (ksChamferSideAngle, d₂ = d₁·tg α), so the write is refused BEFORE the mutation. The
        // method is read from API7; on an ambiguous match ReadChamferAngle returns null and the write is
        // NOT refused — "method not read" is not "method is angular".
        // History: docs/decisions/adapter-features.md#chamfer-method-substitution
        var existingApi7 = ReadChamferAngle(document, current);
        if (existingApi7?.BuildingType == SideAngleBuildingType && command.AngleDeg is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Фаска построена способом «расстояние и угол» (ksChamferSideAngle), а маршрут записи " +
                "API5 умеет только «два катета» (ksChamferTwoSides): правка катета молча сменила бы " +
                "способ и потеряла угол (измерено: 30° → 45°, V 79896.07695154587 → 79820). " +
                "Чтобы получить такую фаску с другим размером, удалите признак и создайте заново " +
                "нужным способом.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["existing_building_type"] = existingApi7!.BuildingType,
                    ["write_route_supports"] = TwoSidesBuildingType,
                    ["angle_deg"] = existingApi7.AngleDeg,
                    ["distance2_mm_derived"] = existingApi7.Distance2Mm,
                    ["measured"] = "scratch/_probe_angle_edit2.py, 16.09.2026",
                });
        }

        var distance1 = command.Distance1Mm ?? current.Distance1Mm;
        // The second leg changes only together with the first or explicitly: otherwise editing "one
        // leg" would silently become editing both.
        var distance2 = command.Distance2Mm ?? (command.Distance1Mm is not null && command.Direction is null
            ? command.Distance1Mm.Value
            : current.Distance2Mm);
        var transfer = command.Direction ?? current.Transfer;

        var writeTarget = entity.GetDefinition() as ksChamferDefinition;
        if (writeTarget is null || !writeTarget.SetChamferParam(transfer, distance1, distance2))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetChamferParam не подтверждён: признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var writes = new List<NamedCheck>
        {
            new("set_chamfer_param", true,
                Observed: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}",
                Expected: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}"),
        };

        // The order "write → Update() → RebuildDocument()" is part of the contract: without Update()
        // the model stays as it was although every setter returned true (P2.3 for extrusions, F.3 for
        // the chamfer).
        if (!entity.Update())
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: записанное значение не применено к модели.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["applied_writes"] = writes.Count });
        }

        document.Document.RebuildDocument();
        var readBack = (entity.GetDefinition() as ksChamferDefinition) is { } afterDefinition
            ? ReadChamferParam(afterDefinition)
            : null;
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var valueStored = readBack is not null
            && Math.Abs(readBack.Distance1Mm - distance1) <= 1e-6
            && Math.Abs(readBack.Distance2Mm - distance2) <= 1e-6
            && readBack.Transfer == transfer;
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>(writes)
        {
            new("parameters_read_back", valueStored,
                Observed: readBack is null
                    ? "GetChamferParam не читается"
                    : $"transfer={readBack.Transfer} d1={readBack.Distance1Mm:0.####} d2={readBack.Distance2Mm:0.####}",
                Expected: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            volumeMatched = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_after_update",
                volumeMatched,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        var geometryConfirmed = valueStored && sameFeature && volumeMatched;
        if (!geometryConfirmed)
        {
            unverified.Insert(0, valueStored
                ? "volume_not_as_expected — значение записано и перечитано, но измерение объёма не совпало с ожиданием"
                : "value_not_read_back — параметр не перечитался с нового объекта определения");
        }

        BumpRevision(document, "chamfer.update");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            ChamferFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            readBack?.Distance1Mm,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }
}
