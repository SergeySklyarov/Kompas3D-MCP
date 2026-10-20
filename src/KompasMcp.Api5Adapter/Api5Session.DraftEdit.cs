using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Editing a draft — the family «Уклон» от основания (SM-12) through <c>kompas_update_feature</c>.</summary>
/// <remarks>INVARIANT: the edit writes the SAME feature and calls <c>Update()</c>; the confirmation is the
/// volume, not the return code.
/// INVARIANT: the side is carried across the edit — the product flag is recomputed from the base
/// orientation, so changing the base alone does not silently flip the side.
/// LIMIT: the base-line family's edit (angle and <c>Reverse</c>) is not here: its feature has no API5
/// definition and the API7 object must be matched first; a request for it is refused by name.
/// History: docs/decisions/adapter-features.md#draft-route-and-direction</remarks>
public partial class Api5Session
{
    /// <summary>Family name of a draft in edit responses.</summary>
    private const string DraftFamily = "draft";

    /// <summary>Fields a draft edit accepts. Everything else is refused, not ignored.</summary>
    private static readonly string[] DraftEditFields =
    {
        "angle_deg", "direction", "face_refs", "base_face_ref", "base_plane",
    };

    /// <summary>Edit a draft: angle, side, face set and base (face ↔ plane).</summary>
    private UpdateFeatureResult UpdateDraftFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignDraftEditFields(command);

        // ROUTE: the same API7 object the creation uses — API5 and API7 are never called on one feature.
        var incline = FindDraftIncline(document, entity, out var container, out var reason);
        if (incline is null || container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Объект IIncline этого признака не найден: " + reason + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "draft_object_not_matched",
                    ["tree_type"] = entity.type,
                });
        }

        var angleBefore = SafeDouble(() => incline.Angle);
        var flagBefore = SafeBool(() => incline.Direction);
        var facesBefore = InclineFaceCount(incline);

        var angle = command.AngleDeg ?? angleBefore;
        if (angle is not double targetAngle || !DraftRules.AngleInRange(targetAngle))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона должен быть конечным числом, 0 < |angle_deg| < 90; получено "
                + Num(angle) + ". Ни с модели, ни из запроса годного угла не собралось — признак "
                + "не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["angle_deg"] = angle });
        }

        // The flag is carried across the edit WITHOUT re-reading the old base: MEASURED, GetPlane() of a
        // built feature does not give an object whose orientation this route can read, and a guessed side
        // would tilt the face the wrong way. So: angle only keeps the flag; `direction` is the product
        // flag, as the field is documented for this family; a NEW base must come WITH `direction`.
        if ((command.BaseFaceRef is not null || command.BasePlane is not null)
            && command.Direction is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Смена основания без direction не выполняется: ориентация ПРЕЖНЕГО основания этим "
                + "маршрутом не читается, и сторона уклона не переносится — подставленная догадка "
                + "наклонила бы грань в другую сторону. Передайте direction вместе с новым основанием. "
                + "Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["has_base_face_ref"] = command.BaseFaceRef is not null,
                    ["has_base_plane"] = command.BasePlane is not null,
                });
        }

        var (basePayload, basePoint, baseNormal, baseKind) =
            ResolveDraftEditBase(document, command);
        var baseOut = basePoint is null || baseNormal is null
            ? false
            : BaseNormalPointsOutOfBody(document, basePoint, baseNormal);

        var flag = command.Direction ?? flagBefore;
        if (flag is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Сторона уклона не собралась: с модели флаг не прочитан, а в запросе direction не "
                + "задан. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["flag_before"] = flagBefore,
                    ["direction_from_request"] = command.Direction,
                });
        }

        var side = basePoint is null ? null : DraftRules.ContractDirection(flag.Value, baseOut);

        List<ksFaceDefinition>? faces = null;
        if (command.FaceRefs is { Count: > 0 } faceRefs)
        {
            if (faceRefs.Count != faceRefs.Distinct(StringComparer.Ordinal).Count())
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Список граней содержит повторы: одна и та же грань указана дважды, и «сколько "
                    + "граней уклонено» стало бы неотличимо от «сколько раз её назвали».",
                    RetryPolicy.Never);
            }

            faces = new List<ksFaceDefinition>(faceRefs.Count);
            foreach (var faceRef in faceRefs)
            {
                faces.Add(RequireFace(document, faceRef));
            }
        }
        else if (command.FaceRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список уклоняемых граней пуст: уклон без грани не выражается. Передайте хотя бы одну "
                + "плоскую грань или не передавайте поле.",
                RetryPolicy.Never);
        }

        // INVARIANT: the parameters are written together with the base, then one Update() applies the mode.
        // MEASURED: API5 SetPlane on a BUILT feature answers TRUE and changes nothing; the same base
        // through IIncline.Plane is applied — 400000 mm³ and X max 104.408, exactly DR-04.
        // History: docs/decisions/adapter-features.md#draft-edit-and-plane-base
        var bridge = BridgeFor(document);
        IModelObject? base7 = null;
        if (basePayload is not null)
        {
            if (bridge.TransferTo7(basePayload) is not IModelObject transferred)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Новое основание вида «" + baseKind + "» не перенесено в API7 как IModelObject (" +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null") + "). Признак не изменён.",
                    RetryPolicy.ReacquireContext);
            }

            base7 = transferred;
        }

        object[]? faces7 = null;
        if (faces is not null)
        {
            faces7 = new object[faces.Count];
            for (var i = 0; i < faces.Count; i++)
            {
                if (bridge.TransferTo7(faces[i]) is not IModelObject face7)
                {
                    throw new KompasContractException(
                        ErrorCodes.GeometryFailed,
                        "Уклоняемая грань не перенесена в API7 как IModelObject. Признак не изменён.",
                        RetryPolicy.ReacquireContext);
                }

                faces7[i] = face7;
            }
        }

        try
        {
            // ORDER: ONE property per Update. MEASURED, three variants of one call: Plane alone applies
            // (400000 / X max 104.408); Plane batched with Angle does nothing; Plane batched with Direction
            // does nothing either. So the side goes first, then the base, each confirmed on its own.
            if (faces7 is not null)
            {
                incline.Faces = faces7;
            }

            if (base7 is not null)
            {
                incline.Direction = flag.Value;
                if (SafeBool(() => ((IModelObject)incline).Update()) != true)
                {
                    throw new KompasContractException(
                        ErrorCodes.GeometryFailed,
                        "Сторона уклона записана, но Update() не подтвердил её применение. Признак "
                        + "изменён частично.",
                        RetryPolicy.ReacquireContext,
                        partialEffects: true);
                }

                RebuildApi7AndSettle(container, document);

                // MEASURED: after Update() the matched wrapper stops carrying property writes — the
                // re-matched object is what a new call would find.
                incline = FindDraftIncline(document, entity, out var again, out _) ?? incline;
                container = again ?? container;

                incline.Plane = base7;
                if (SafeBool(() => ((IModelObject)incline).Update()) != true)
                {
                    throw new KompasContractException(
                        ErrorCodes.GeometryFailed,
                        "Новое основание записано, но Update() не подтвердил его применение. Признак "
                        + "изменён частично.",
                        RetryPolicy.ReacquireContext,
                        partialEffects: true);
                }

                RebuildApi7AndSettle(container, document);
                incline = FindDraftIncline(document, entity, out again, out _) ?? incline;
                container = again ?? container;
            }

            incline.Angle = targetAngle;
            if (base7 is null)
            {
                incline.Direction = flag.Value;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства уклона не записаны: " + ex.GetType().Name + ". Признак изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var updated = SafeBool(() => ((IModelObject)incline).Update());
        if (updated != true)
        {
            var refusalDetails = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(refusalDetails, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() уклона вернул false: записанный режим не применён к модели. Признак "
                + "изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: refusalDetails);
        }

        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "draft.update");

        var reference = References.Require(command.FeatureRef, document.Id, document.Revision);
        var stateAfter = ReadFeatureState(entity);
        RequireNoApi7FeatureError(document, incline, command.FeatureRef, "Уклон (правка)");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var (angleReadBack, flagReadBack) = ReadInclineParameters(incline);

        var checks = new List<NamedCheck>
        {
            new("update_returned", updated == true, "Update() вернул true"),
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                && featuresBefore == featuresAfter,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            new("feature_count_unchanged", featuresAfter == featuresBefore,
                "признаков " + featuresBefore + " → " + featuresAfter),
            new("angle_read_back",
                angleReadBack is double readAngle
                && Math.Abs(readAngle - targetAngle) <= 1e-6,
                Observed: Num(angleReadBack), Expected: Num(targetAngle)),
            new("direction_read_back", flagReadBack == flag,
                Observed: flagReadBack?.ToString(), Expected: flag.ToString()),
        };

        if (faces is not null)
        {
            checks.Add(new NamedCheck("faces_read_back", InclineFaceCount(incline) == faces.Count,
                Observed: Num(InclineFaceCount(incline)), Expected: Num(faces.Count)));
        }

        var unverified = new List<string>
        {
            "draft_from_edges_edit_not_supported — правка базовой линии, направляющего объекта и стороны "
            + "семейства «уклон от базовой линии» в этот наряд не входит",
        };

        var geometryConfirmed = ConfirmDraftGeometry(
            command.ExpectedVolumeMm3, volumeBefore, volumeAfter, document, checks, unverified);

        return new UpdateFeatureResult(
            ToDto(reference, stateAfter.Name),
            DraftFamily,
            true,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            Warnings: Array.Empty<string>());
    }

    /// <summary>The base for an edit: the requested one, or the feature's own.</summary>
    /// <remarks>The requested base is resolved exactly as at creation; the feature's own is read from
    /// <c>GetPlane</c>, so "changed only the angle" keeps the side it had.</remarks>
    private (object? Payload, double[]? Point, double[]? Normal, string Kind) ResolveDraftEditBase(
        DocumentEntry document, UpdateFeatureCommand command)
    {
        if (command.BaseFaceRef is not null || command.BasePlane is not null)
        {
            if (!DraftRules.SingleBase(command.BaseFaceRef, command.BasePlane is null ? null : "plane"))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Основание задаётся РОВНО одной формой: base_face_ref либо base_plane. Обе сразу "
                    + "или ни одной — отказ.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["has_base_face_ref"] = command.BaseFaceRef is not null,
                        ["has_base_plane"] = command.BasePlane is not null,
                    });
            }

            if (command.BaseFaceRef is { Length: > 0 } faceRef)
            {
                var face = RequireFace(document, faceRef);
                if (PlanarPlacementOf(face) is not { } planar)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        $"Основание '{faceRef}' не плоская грань: уклон отсчитывается от плоскости.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?> { ["base_face_ref"] = faceRef });
                }

                return (face, ToArray(planar.Point), ToArray(planar.Normal), "face");
            }

            var plane = command.BasePlane!;
            var planeEntity = plane.Reference is { Length: > 0 } planeReference
                ? ResolvePlaneSupportObject(
                    document, References.Require(planeReference, document.Id, document.Revision),
                    planeReference, new List<string>())
                : ResolvePlaneEntity(document, plane);
            var (point, normal) = ReadPlanePlacement(document, BridgeFor(document), planeEntity, plane);
            if (point is null || normal is null)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Нормаль новой плоскости основания не прочитана: сторона уклона отсчитывается от "
                    + "неё. Признак не изменён.",
                    RetryPolicy.ReacquireContext);
            }

            return (planeEntity, point, normal, "plane");
        }

        // No base in the request: the caller keeps the one the feature has. The geometry is not read,
        // and the side is reported only when the base was given in the same call.
        return (null, null, null, "unchanged");
    }

    /// <summary>The API7 object of the draft the reference points at, or null with the reason.</summary>
    /// <remarks>MEASURED: the API7 object is matched to the tree element BY NAME — a name that matches
    /// twice is not an identifier, and the edit is refused instead of picking one.
    /// History: docs/decisions/adapter-features.md#draft-route-and-direction</remarks>
    private IIncline? FindDraftIncline(
        DocumentEntry document, ksEntity entity, out IModelContainer? container, out string? reason)
    {
        reason = null;
        container = null;

        var bridge = BridgeFor(document);
        container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            reason = "api7_container_unavailable — маршрут API7 не построился: "
                + (bridge.BridgeFailure ?? "причина не известна");
            return null;
        }

        var name = SafeEntityName(entity);
        if (string.IsNullOrEmpty(name))
        {
            reason = "draft_name_unreadable — имя элемента дерева не прочиталось, а объект API7 "
                + "сопоставляется только по имени";
            return null;
        }

        IInclines? collection;
        try
        {
            collection = container.Inclines;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            reason = "draft_collection_unavailable — IModelContainer.Inclines не отвечает: "
                + ex.GetType().Name;
            return null;
        }

        if (collection is null)
        {
            reason = "draft_collection_unavailable — IModelContainer.Inclines пуст";
            return null;
        }

        // DOC: <c>iinclines.html</c> — <c>IInclines.Incline</c> takes an index OR a name. The name is
        // asked first: MEASURED, an object walked out of the collection by index carries single-property
        // writes but ignores a batch of them, which is the shape of every failed base edit.
        try
        {
            if (collection.Incline[(object)name] is IIncline byName)
            {
                return byName;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The name lookup is not the only documented form: the walk below stays as the fallback.
        }

        var count = SafeInt(() => collection.Count) ?? 0;
        IIncline? found = null;
        var matches = 0;
        for (var i = 0; i < count; i++)
        {
            if (collection.Incline[(object)i] is not IIncline item)
            {
                continue;
            }

            if (!string.Equals(SafeModelName(item), name, StringComparison.Ordinal))
            {
                continue;
            }

            found = item;
            matches++;
        }

        if (matches == 1)
        {
            return found;
        }

        reason = matches == 0
            ? "draft_object_not_found — в IInclines нет объекта с именем «" + name
              + "» (объектов в коллекции: " + count.ToString(CultureInfo.InvariantCulture) + ")"
            : "draft_object_ambiguous — имя «" + name + "» отвечает " + matches
              + " объектам: имя не является идентификатором";
        return null;
    }

    /// <summary>Number of drafted faces on the API7 feature: <c>Faces</c> is a VARIANT that carries one
    /// face as a bare object and a set as an array.</summary>
    private static int? InclineFaceCount(IIncline incline)
    {
        try
        {
            return incline.Faces switch
            {
                null => null,
                object[] array => array.Length,
                Array array => array.Length,
                _ => 1,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Fields of other families are refused by name, not ignored.</summary>
    /// <remarks>INVARIANT: the allowed names are this family's own; <c>angle_deg</c>, <c>direction</c>
    /// and <c>face_refs</c> are shared with the chamfer and shell families, and the FEATURE decides which
    /// family they belong to here — the same rule by which those families are selected.</remarks>
    private static void RejectForeignDraftEditFields(UpdateFeatureCommand command)
    {
        var foreign = NonB5EditFields
            .Concat(B5EditFields)
            .Where(f => !DraftEditFields.Contains(f.Name, StringComparer.Ordinal))
            .Where(f => f.Read(command) is not null)
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (foreign.Count == 0)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Признак — уклон: к нему применяются только angle_deg, direction, face_refs, base_face_ref "
            + "и base_plane. Переданные поля других семейств не игнорируются, а отвергаются: "
            + "принятое и проигнорированное число доживает до приёмки, выглядя как выполненная правка.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["family"] = DraftFamily,
                ["foreign_fields"] = foreign,
            });
    }

    /// <summary>Draft parameters read from the API7 feature (DR-05 of the order).</summary>
    /// <remarks>INVARIANT: the read goes through the route that writes. The side relative to the BODY is
    /// not published: <c>Plane</c> tells a plane from a face only by the interface it answers, so an
    /// unrecognised base stays null WITH the reason instead of a guess.
    /// History: docs/decisions/adapter-features.md#draft-edit-and-plane-base</remarks>
    private DraftDto? ReadDraftFeature(DocumentEntry document, ksEntity entity, out string? reason)
    {
        reason = null;

        var incline = FindDraftIncline(document, entity, out _, out reason);
        if (incline is null)
        {
            return null;
        }

        var (angle, flag) = ReadInclineParameters(incline);
        var faces = InclineFaceCount(incline);

        if (angle is null || flag is null || faces is null)
        {
            reason = "draft_params_partly_unreadable — с IIncline не прочиталось: "
                + (angle is null ? "Angle " : string.Empty)
                + (flag is null ? "Direction " : string.Empty)
                + (faces is null ? "Faces" : string.Empty);
        }

        object? baseObject;
        try
        {
            baseObject = incline.Plane;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            baseObject = null;
        }

        var baseKind = baseObject is IPlane3D ? "plane" : null;
        if (baseObject is null)
        {
            reason ??= "draft_base_not_read — Plane не вернул объект основания";
        }
        else if (baseKind is null)
        {
            reason ??= "draft_base_kind_not_read — объект основания не отвечает IPlane3D, а плоскость от "
                + "грани этим маршрутом не отличаетcя: вид основания не публикуется";
        }

        return new DraftDto(angle, flag, faces, baseKind);
    }
}
