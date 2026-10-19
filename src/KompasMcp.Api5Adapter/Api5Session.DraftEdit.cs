using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
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

        if (DefinitionOf(entity) is not ksInclineDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак не отвечает ksInclineDefinition: записать угол, сторону, набор граней и "
                + "основание некуда. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "draft_definition_not_recognized",
                    ["tree_type"] = entity.type,
                });
        }

        var angleBefore = SafeDouble(() => definition.angle);
        var flagBefore = SafeBool(() => definition.direction);
        var facesBefore = FaceArrayCount(definition);

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

        if (!WriteDraftParameters(definition, targetAngle, flag.Value))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись angle/direction в ksInclineDefinition не состоялась: признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        if (basePayload is not null && SafeBool(() => definition.SetPlane(basePayload)) != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Новое основание не принято: SetPlane вернул отказ на объекте вида «" + baseKind
                + "». Признак изменён частично: угол и сторона записаны.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["base_kind"] = baseKind });
        }

        var facesWritten = -1;
        if (faces is not null)
        {
            facesWritten = WriteDraftFaces(definition, faces);
            if (facesWritten != faces.Count)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Уклоняемые грани записаны не полностью: " + facesWritten + " из " + faces.Count
                    + ". Признак изменён частично.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true);
            }
        }

        var updated = SafeBool(entity.Update);
        if (updated != true)
        {
            var refusalDetails = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(refusalDetails, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: записанный режим не применён к модели. Признак "
                + "изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: refusalDetails);
        }

        document.PartNow().RebuildModel();
        SettleAfterMutation(document);
        BumpRevision(document, "draft.update");

        var reference = References.Require(command.FeatureRef, document.Id, document.Revision);
        var stateAfter = ReadFeatureState(entity);
        RequireNoFeatureError(document, entity, command.FeatureRef, "Уклон (правка)");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var (angleReadBack, flagReadBack) = ReadInclineParameters(entity);

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
            checks.Add(new NamedCheck("faces_read_back", FaceArrayCount(definition) == faces.Count,
                Observed: Num(FaceArrayCount(definition)), Expected: Num(faces.Count)));
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
            var (point, normal) = ReadPlanePlacement(document, BridgeFor(document), planeEntity);
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

    /// <summary>Angle and flag written together: the two are one mode, and writing half of it would make
    /// "exactly what was asked changed" indistinguishable from "this changed too".</summary>
    private static bool WriteDraftParameters(ksInclineDefinition definition, double angle, bool flag)
    {
        try
        {
            definition.angle = angle;
            definition.direction = flag;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>The face set is a FULL replacement: what is passed is what must be drafted.</summary>
    private static int WriteDraftFaces(ksInclineDefinition definition, IReadOnlyList<ksFaceDefinition> faces)
    {
        object? holder;
        try
        {
            holder = definition.FaceArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        if (SafeBool(() => collection.Clear()) != true)
        {
            return 0;
        }

        var written = 0;
        foreach (var face in faces)
        {
            if (SafeBool(() => collection.Add(face)) == true)
            {
                written++;
            }
        }

        return written;
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

    /// <summary>Faces of the feature's own array, or null when the array does not read.</summary>
    private static int? FaceArrayCount(ksInclineDefinition definition)
    {
        try
        {
            return definition.FaceArray() is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}
