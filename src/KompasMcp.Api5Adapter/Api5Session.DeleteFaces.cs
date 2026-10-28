using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Direct face deletion in two documented modes (SM-24.delete_faces, profile
/// <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>isurfacecontainer_faceremovers.html</c> — «коллекция операций удаления граней»;
/// <c>ifaceremover_savebody.html</c> — <c>SaveBody</c> is a BOOL «Сохранять целостность тела»;
/// <c>ifaceremover_faces.html</c> — one face travels as <c>VT_DISPATCH</c>, several as
/// <c>VT_ARRAY | VT_DISPATCH</c>.
/// MEASURED: on a box all six faces answer <c>Update() = false</c> with <c>SaveBody = true</c> and
/// <c>true</c> with <c>SaveBody = false</c>; the shape of the argument and the order of the two
/// properties change nothing. The mode is therefore executed and judged by the numbers, never by
/// <c>Update()</c> alone.
/// History: docs/decisions/direct-faces.md#delete-restore</remarks>
public partial class Api5Session
{
    /// <summary>Delete faces of one body: either leaving a shell (<c>open_shell</c>) or keeping the body
    /// solid (<c>keep_body</c>).</summary>
    public DeleteFacesResult DeleteFaces(DeleteFacesCommand command)
    {
        ValidateDeleteFacesCommand(command);

        var document = RequireDocument(command.DocumentId);
        RequirePart(document);

        var faces = new List<ksFaceDefinition>(command.FaceRefs.Count);
        foreach (var faceRef in command.FaceRefs)
        {
            faces.Add(RequireFace(document, faceRef));
        }

        var bodies = BodiesOf(document);
        if (bodies.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.NoBody,
                "В документе нет тел: удалять грани не у чего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = 0 });
        }

        var targetIndex = BodyIndexOf(bodies, faces[0]);
        if (targetIndex < 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Грань '{command.FaceRefs[0]}' не найдена ни в одном теле документа: ссылка устарела "
                + "или указывает на грань, которой уже нет. Ничего не удалялось.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRefs[0] });
        }

        for (var i = 1; i < faces.Count; i++)
        {
            var index = BodyIndexOf(bodies, faces[i]);
            if (index == targetIndex)
            {
                continue;
            }

            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Грань '{command.FaceRefs[i]}' принадлежит другому телу: все грани одного вызова "
                + "удаляются в одном теле. Ничего не удалялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["face_ref"] = command.FaceRefs[i],
                    ["body_index"] = index,
                    ["target_body_index"] = targetIndex,
                });
        }

        var body = bodies[targetIndex];
        // -1 means «not read», so the «all faces» refusal is skipped instead of guessed.
        var bodyFaces = FaceCountOf(body);
        if (bodyFaces >= 0 && faces.Count >= bodyFaces)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Названы ВСЕ грани тела (" + faces.Count + " из " + bodyFaces + "): удаление всех граней "
                + "оставляет пустоту, а не тело и не оболочку. Укажите хотя бы одну грань меньше. "
                + "Ничего не удалялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["requested_faces"] = faces.Count,
                    ["body_faces"] = bodyFaces,
                    ["body_index"] = targetIndex,
                });
        }

        var keepBody = command.Mode == DeleteFacesModes.KeepBody;
        var bodyCountBefore = bodies.Count;
        var revisionBefore = document.Revision;
        var volumeBefore = MassProperties(body, (uint)KompasUnits.MassMmKg)?.v;
        var areaBefore = MassProperties(body, (uint)KompasUnits.MassMmKg)?.F;
        var facesBefore = CountFaces(document);

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Грани не удалялись.",
                RetryPolicy.ReacquireContext);
        }

        if (container is not IPart7 part7 || part7 is not ISurfaceContainer surfaces)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7 не отвечает как ISurfaceContainer: контейнер поверхностей недостижим, а другого "
                + "документированного входа к удалению граней нет. Грани не удалялись.",
                RetryPolicy.ReacquireContext);
        }

        var faces7 = new object[faces.Count];
        for (var i = 0; i < faces.Count; i++)
        {
            if (bridge.TransferTo7(faces[i]) is not IFace face7)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Грань не перенесена в API7 как IFace (" +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                    "). Грани не удалялись.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRefs[i] });
            }

            faces7[i] = face7;
        }

        object? added;
        try
        {
            added = surfaces.FaceRemovers?.Add();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISurfaceContainer.FaceRemovers.Add() прервался: " + ex.GetType().Name +
                ". Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (added is not IFaceRemover remover)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "FaceRemovers.Add() не вернул IFaceRemover: коллекция операций недоступна. Грани не "
                + "удалялись.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            // DOC: one face is VT_DISPATCH, several are VT_ARRAY | VT_DISPATCH. The single-object form is
            // the one the documentation names first, so it is used for one face.
            remover.Faces = faces7.Length == 1 ? faces7[0] : faces7;
            remover.SaveBody = keepBody;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства удаления не записаны: " + ex.GetType().Name + ". Операция осталась в "
                + "документе без параметров и названа в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var updated = SafeBool(() => ((IModelObject)remover).Update());
        var failure = ReadKompasFailure(document);
        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "solid.delete_faces");

        var bodiesAfter = BodiesOf(document);
        var bodyAfter = targetIndex < bodiesAfter.Count ? bodiesAfter[targetIndex] : null;
        var volumeAfter = bodyAfter is null ? null : MassProperties(bodyAfter, (uint)KompasUnits.MassMmKg)?.v;
        var areaAfter = bodyAfter is null ? null : MassProperties(bodyAfter, (uint)KompasUnits.MassMmKg)?.F;
        var facesAfter = CountFaces(document);
        var removed = FaceRemovalEffect.FacesRemoved(facesBefore, facesAfter);

        // INVARIANT: Update() = true is «accepted», not «applied», and Update() = false is an honest
        // refusal. The mode decides which numbers must hold, and a mismatch is a named refusal.
        if (updated != true)
        {
            var details = new Dictionary<string, object?>
            {
                ["named_code"] = "GEOMETRY_FAILED",
                ["mode"] = command.Mode,
                ["update_accepted"] = updated,
                ["faces_before"] = facesBefore,
                ["faces_after"] = facesAfter,
                ["volume_before_mm3"] = volumeBefore,
                ["volume_after_mm3"] = volumeAfter,
            };
            AddKompasFailure(details, failure);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() удаления вернул " + (updated is null ? "ошибку вызова" : "false") + " в режиме " +
                command.Mode + ": тело не перестроено. MEASURED: на бруске все шесть граней отвечают так "
                + "при SaveBody = true, то есть целостность тела из этой грани не восстанавливается. "
                + "Это отказ, а не выполненное удаление.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        if (!FaceRemovalEffect.MatchesRequest(removed, faces.Count))
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Update() вернул true, а число граней изменилось на " + Num(removed) + " вместо " +
                faces.Count + " (" + Num(facesBefore) + " → " + Num(facesAfter) + "): операция принята, "
                + "но не применена. Это отказ, а не выполненное удаление.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode,
                    ["requested_faces"] = faces.Count,
                    ["faces_removed"] = removed,
                    ["faces_before"] = facesBefore,
                    ["faces_after"] = facesAfter,
                });
        }

        var solidAfter = FaceRemovalEffect.IsSolid(volumeAfter) == true;
        var checks = new List<NamedCheck>
        {
            new("operation_updated", updated == true, "Update() вернул true"),
            new("faces_removed", removed == faces.Count,
                Observed: Num(facesBefore) + " → " + Num(facesAfter),
                Expected: "граней стало меньше на " + faces.Count),
            new("body_count_unchanged", bodiesAfter.Count == bodyCountBefore,
                Observed: bodyCountBefore + " → " + bodiesAfter.Count,
                Expected: "удаление граней не меняет число тел"),
        };

        if (keepBody)
        {
            checks.Add(new NamedCheck("body_still_solid", solidAfter,
                Observed: Num(volumeAfter) + " мм³", Expected: "объём тела не нулевой"));
            if (!solidAfter)
            {
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    "Режим keep_body дал оболочку: объём тела " + Num(volumeAfter) + " мм³. Тело осталось "
                    + "в изменённом состоянии — см. partial_effects.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["mode"] = command.Mode,
                        ["volume_before_mm3"] = volumeBefore,
                        ["volume_after_mm3"] = volumeAfter,
                        ["faces_after"] = facesAfter,
                    });
            }
        }
        else
        {
            // MEASURED: SaveBody = false leaves no material at all, so the volume IS the check for the
            // open-shell mode; a non-zero volume would mean the wrong mode answered.
            checks.Add(new NamedCheck("volume_zero", FaceRemovalEffect.IsSolid(volumeAfter) == false,
                Observed: Num(volumeAfter) + " мм³", Expected: "0 мм³ — оболочка не заключает материала"));
            checks.Add(new NamedCheck("area_decreased", FaceRemovalEffect.AreaDecreased(areaBefore, areaAfter) == true,
                Observed: Num(areaBefore) + " → " + Num(areaAfter) + " мм²",
                Expected: "площадь поверхности уменьшилась"));
            if (FaceRemovalEffect.IsSolid(volumeAfter) == true)
            {
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    "Режим open_shell оставил тело твёрдым: объём " + Num(volumeAfter) + " мм³ при "
                    + "SaveBody = false. Это отказ, а не выполненная операция — см. partial_effects.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["mode"] = command.Mode,
                        ["volume_before_mm3"] = volumeBefore,
                        ["volume_after_mm3"] = volumeAfter,
                        ["faces_after"] = facesAfter,
                    });
            }
        }

        var notes = new List<string>
        {
            "mode_reported_by_measurement — режим назван полем mode, а не выведен из ответа: open_shell "
            + "проверяется нулевым объёмом, keep_body - ненулевым",
            "keep_body_route_measured_on_box — MEASURED: на бруске 40x30x20 все шесть граней дают "
            + "Update() = false при SaveBody = true, поэтому режим отвечает отказом GEOMETRY_FAILED, "
            + "а не успехом; вылечиваемость другой геометрии не измерялась",
        };

        return new DeleteFacesResult(
            faces.Count,
            command.Mode,
            updated,
            volumeBefore,
            volumeAfter,
            areaBefore,
            areaAfter,
            (int)Math.Round(facesBefore ?? 0d),
            (int)Math.Round(facesAfter ?? 0d),
            bodyCountBefore,
            bodiesAfter.Count,
            revisionBefore,
            document.Revision,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                new List<string>
                {
                    "save_reopen_not_measured — сохранение и переоткрытие удалённой грани в этот наряд "
                    + "не входят (наряд DIRECT_FACES_NO_HISTORY)",
                }),
            notes);
    }

    /// <summary>Refusals before COM: an empty list, duplicates, an unknown mode and a face list that
    /// cannot be executed are cheaper to refuse here than after the document has been mutated.</summary>
    private static void ValidateDeleteFacesCommand(DeleteFacesCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: грани удаляются в конкретной детали.",
                RetryPolicy.Never);
        }

        if (command.FaceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней пуст: операция не создаётся, а пустой вызов не выдаётся за "
                + "удаление. Укажите хотя бы одну грань.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["requested_faces"] = 0 });
        }

        if (command.FaceRefs.Count != command.FaceRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список граней содержит повторы: одна и та же грань названа дважды, и «сколько граней "
                + "должно исчезнуть» перестаёт быть одним числом.",
                RetryPolicy.Never);
        }

        if (command.Mode is not (DeleteFacesModes.OpenShell or DeleteFacesModes.KeepBody))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Режим '" + command.Mode + "' не документирован: IFaceRemover.SaveBody знает только "
                + "open_shell (false) и keep_body (true).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode,
                    ["allowed"] = new[] { DeleteFacesModes.OpenShell, DeleteFacesModes.KeepBody },
                });
        }
    }
}
