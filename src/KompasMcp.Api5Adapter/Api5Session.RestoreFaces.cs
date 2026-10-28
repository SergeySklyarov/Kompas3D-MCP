using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>Restore a surface from a face of a shelled body (SM-24.restore_faces.by_face, profile
/// <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>isurfacecontainer_restoredsurfaces.html</c> — «коллекция операций восстановления
/// поверхностей»; <c>irestoredsurfaces_add.html</c> — <c>Add()</c> gives the operation;
/// <c>irestoredsurface_face.html</c> — <c>Face</c> is «исходная грань поверхности», then
/// <c>IModelObject::Update</c>.
/// MEASURED: the operation creates a SEPARATE surface object (the document gains one body) and does not
/// heal the shell, so the volume is reported as read and never promised.
/// History: docs/decisions/direct-faces.md#delete-restore</remarks>
public partial class Api5Session
{
    /// <summary>Restore a surface from the named face and report what the model now holds.</summary>
    public RestoreFacesResult RestoreFaces(RestoreFacesCommand command)
    {
        ValidateRestoreFacesCommand(command);

        var document = RequireDocument(command.DocumentId);
        RequirePart(document);

        var face = RequireFace(document, command.FaceRef);

        var bodies = BodiesOf(document);
        if (bodies.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.NoBody,
                "В документе нет тел: восстанавливать поверхность не из чего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = 0 });
        }

        // INVARIANT: the face must belong to a body of THIS document. The reference registry states the
        // document and the revision but not the owner, so the owner is asked of the model.
        var targetIndex = BodyIndexOf(bodies, face);
        if (targetIndex < 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Грань '{command.FaceRef}' не найдена ни в одном теле документа: ссылка устарела или "
                + "указывает на грань, которой уже нет. Поверхность не создавалась.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRef });
        }

        var target = bodies[targetIndex];
        var bodyCountBefore = bodies.Count;
        var revisionBefore = document.Revision;
        var volumeBefore = MassProperties(target, (uint)KompasUnits.MassMmKg)?.v;
        var areaBefore = MassProperties(target, (uint)KompasUnits.MassMmKg)?.F;

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Поверхность не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (container is not IPart7 part7 || part7 is not ISurfaceContainer surfaces)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7 не отвечает как ISurfaceContainer: коллекция RestoredSurfaces недостижима, а "
                + "другого документированного входа к восстановлению поверхности нет. Поверхность не "
                + "создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (bridge.TransferTo7(face) is not IFace face7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Грань не перенесена в API7 как IFace (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                "). Поверхность не создавалась.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRef });
        }

        object? added;
        try
        {
            added = surfaces.RestoredSurfaces?.Add();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISurfaceContainer.RestoredSurfaces.Add() прервался: " + ex.GetType().Name +
                ". Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (added is not IRestoredSurface restored)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "RestoredSurfaces.Add() не вернул IRestoredSurface: коллекция операций недоступна. "
                + "Поверхность не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            restored.Face = face7;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойство Face не записано: " + ex.GetType().Name + ". Операция осталась в документе без "
                + "параметров и названа в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var updated = SafeBool(() => ((IModelObject)restored).Update());
        var failure = ReadKompasFailure(document);
        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "solid.restore_faces");

        var bodiesAfter = BodiesOf(document);
        var volumeAfter = targetIndex < bodiesAfter.Count
            ? MassProperties(bodiesAfter[targetIndex], (uint)KompasUnits.MassMmKg)?.v
            : null;
        var areaAfter = targetIndex < bodiesAfter.Count
            ? MassProperties(bodiesAfter[targetIndex], (uint)KompasUnits.MassMmKg)?.F
            : null;

        if (updated != true)
        {
            var details = new Dictionary<string, object?>
            {
                ["named_code"] = "GEOMETRY_FAILED",
                ["face_ref"] = command.FaceRef,
                ["update_accepted"] = updated,
                ["body_count_before"] = bodyCountBefore,
                ["body_count_after"] = bodiesAfter.Count,
            };
            AddKompasFailure(details, failure);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() восстановления вернул " + (updated is null ? "ошибку вызова" : "false") +
                ": операция не применена. Это отказ, а не созданная поверхность.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        var created = bodiesAfter.Count - bodyCountBefore;
        var checks = new List<NamedCheck>
        {
            new("operation_updated", updated == true, "Update() вернул true"),
            // MEASURED: the restored surface is a separate object, so the document gains exactly one body.
            new("object_created", created == 1,
                Observed: bodyCountBefore + " → " + bodiesAfter.Count + " тел",
                Expected: "поверхность — отдельный объект: тел стало на одно больше"),
            new("shell_not_healed", volumeAfter is double left && volumeBefore is double was &&
                Math.Abs(left - was) <= 1e-6d,
                Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
                Expected: "объём тела не возвращается — восстановленная поверхность его не вылечивает"),
        };

        if (created != 1)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Update() вернул true, а нового объекта не появилось: тел " + bodyCountBefore + " → " +
                bodiesAfter.Count + ". Восстановленная поверхность документирована как отдельный объект, "
                + "поэтому это отказ, а не выполненное восстановление.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["face_ref"] = command.FaceRef,
                    ["body_count_before"] = bodyCountBefore,
                    ["body_count_after"] = bodiesAfter.Count,
                });
        }

        var notes = new List<string>
        {
            "surface_not_volume — восстановление создаёт отдельную поверхность из названной грани и НЕ "
            + "возвращает объём тела: LIMIT маршрута, а не отказ вызова",
            "body_count_is_the_check — успех подтверждается появлением нового объекта (тел +1), а не "
            + "объёмом: объём оболочки остаётся нулевым и после восстановления",
        };

        return new RestoreFacesResult(
            command.FaceRef,
            command.Mode,
            updated,
            bodyCountBefore,
            bodiesAfter.Count,
            volumeBefore,
            volumeAfter,
            areaBefore,
            areaAfter,
            revisionBefore,
            document.Revision,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                new List<string>
                {
                    "restored_surface_not_identified — восстановленная поверхность не адресуется ссылкой: "
                    + "реестр ссылок граней и тел её не описывает, поэтому проверка идёт по числу тел",
                }),
            notes);
    }

    /// <summary>Refusals before COM: the document, the face and the only documented mode.</summary>
    private static void ValidateRestoreFacesCommand(RestoreFacesCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: поверхность восстанавливается в конкретной детали.",
                RetryPolicy.Never);
        }

        if (string.IsNullOrWhiteSpace(command.FaceRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Грань не задана: IRestoredSurface.Face — это «исходная грань поверхности», без неё "
                + "операция не создаётся.",
                RetryPolicy.Never);
        }

        if (!string.Equals(command.Mode, "by_face", StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Режим '" + command.Mode + "' не документирован: у RestoredSurfaces единственный вход - "
                + "by_face (IRestoredSurface.Face).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["mode"] = command.Mode, ["allowed"] = "by_face" });
        }
    }
}
