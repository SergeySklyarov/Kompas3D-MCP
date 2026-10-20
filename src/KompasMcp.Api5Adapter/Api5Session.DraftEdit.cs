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
/// LIMIT: the base does not change — a request for it is refused by name before any write.
/// LIMIT: the base-line family's edit (angle and <c>Reverse</c>) is not here: its feature has no API5
/// definition and the API7 object must be matched first; a request for it is refused by name.
/// History: docs/decisions/adapter-features.md#draft-base-edit-refused</remarks>
public partial class Api5Session
{
    /// <summary>Family name of a draft in edit responses.</summary>
    private const string DraftFamily = "draft";

    /// <summary>Fields a draft edit accepts. Everything else is refused, not ignored.</summary>
    private static readonly string[] DraftEditFields =
    {
        "angle_deg", "direction", "face_refs", "base_face_ref", "base_plane",
    };

    /// <summary>Edit a draft: angle, side and face set. The base is refused, not changed.</summary>
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

        // LIMIT: a new base is REFUSED, not attempted. MEASURED: IIncline.Plane applies a new base only
        // on the object IInclines.Add returned; on the object found by name — what a reference gives —
        // a package with Plane applies nothing, and an Update after a lone Plane re-applies the old base.
        // History: docs/decisions/adapter-features.md#draft-base-edit-refused
        if (command.BaseFaceRef is not null || command.BasePlane is not null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Основание построенного уклона не меняется: признак придётся пересоздать. Маршрут API7 "
                + "применяет новое основание только к объекту, который вернул IInclines.Add; по ссылке "
                + "признак находится в коллекции по имени, и запись Plane на таком объекте не "
                + "применяется. Модель не изменена.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "draft_base_edit_unsupported",
                    ["has_base_face_ref"] = command.BaseFaceRef is not null,
                    ["has_base_plane"] = command.BasePlane is not null,
                    ["remedy"] = "удалить признак и создать уклон с нужным основанием заново",
                });
        }

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

        // INVARIANT: the properties are written in one package, then one Update() applies the mode.
        // History: docs/decisions/adapter-features.md#draft-base-edit-refused
        var bridge = BridgeFor(document);

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
            if (faces7 is not null)
            {
                incline.Faces = faces7;
            }

            incline.Angle = targetAngle;
            incline.Direction = flag.Value;
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
