using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Direct face editing: moving faces of a body without touching the feature tree
/// (SM-24.move_faces, profile <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>isurfacecontainer.html</c> — the container is obtained from <c>IPart7</c> via
/// <c>IUnknown::QueryInterface</c>; <c>FaceMovers</c> gives «коллекция операций Переместить грань».
/// DOC: <c>ifacemover.html</c>, <c>ifacemover_setfaces.html</c> — <c>SetFaces(Faces, TangentFaces)</c>,
/// <c>Offset</c> (distance), <c>Direction</c> («Сменить направление»), then <c>IModelObject::Update</c>.
/// MEASURED: an API5 face answers as <c>IFace</c> only through <c>TransferInterface(face, ksAPI7Dual, 0)</c>.
/// INVARIANT: the operation is confirmed by the numbers read back — the swept volume against
/// <c>d·A</c> and the area against <c>d·P</c> — because <c>Update() = true</c> is «accepted», not
/// «applied».
/// History: docs/decisions/direct-faces.md#implementation</remarks>
public partial class Api5Session
{
    /// <summary>Move faces of one body along their normal by one offset.</summary>
    public MoveFacesResult MoveFaces(MoveFacesCommand command)
    {
        ValidateMoveFacesCommand(command);

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
                "В документе нет тел: перемещать грани не у чего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = 0 });
        }

        // INVARIANT: every face must belong to ONE body. The reference registry states the document and
        // the revision but not the owner, so the owner is asked of the model: a face of a neighbouring
        // body would otherwise be moved as if it were this body's.
        var targetIndex = BodyIndexOf(bodies, faces[0]);
        if (targetIndex < 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Грань '{command.FaceRefs[0]}' не найдена ни в одном теле документа: ссылка устарела "
                + "или указывает на грань, которой уже нет. Ничего не перемещалось.",
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
                + "перемещаются в одном теле, иначе «на сколько сдвинулось» перестаёт быть одним числом. "
                + "Ничего не перемещалось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["face_ref"] = command.FaceRefs[i],
                    ["body_index"] = index,
                    ["target_body_index"] = targetIndex,
                });
        }

        var body = bodies[targetIndex];
        var bodyCountBefore = bodies.Count;
        var revisionBefore = document.Revision;
        var volumeBefore = MassProperties(body, (uint)KompasUnits.MassMmKg)?.v;
        var areaBefore = MassProperties(body, (uint)KompasUnits.MassMmKg)?.F;
        var facesBefore = CountFaces(document);

        var unverified = new List<string>();
        var faceArea = SumFaceAreas(faces, unverified);
        var perimeter = SumFacePerimeters(faces, unverified);

        // INVARIANT: a move that must leave no solid body is refused BEFORE the operation exists.
        // MEASURED: an inward move equal to the wall thickness is accepted by the kernel (Update() = true),
        // leaves volume 0 and keeps the feature in the tree, so the refusal arrived after the mutation.
        // The side is the measured one: reverse = false goes against the outward normal, true along it.
        var signedOffsetMm = command.Reverse ? Math.Abs(command.OffsetMm) : -Math.Abs(command.OffsetMm);
        var collapseVolume = volumeBefore is double intactVolume && faceArea is double namedArea
            ? FaceMoveEffect.ExpectedVolume(intactVolume, namedArea, signedOffsetMm)
            : (double?)null;
        if (!command.TangentGroup
            && FaceMoveEffect.CollapsesBody(volumeBefore, faceArea, signedOffsetMm))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Смещение снимает тело с себя: ожидаемый объём V + d·A при площади граней " +
                Num(faceArea) + " мм² и смещении " + Num(command.OffsetMm) + " мм равен " +
                Num(collapseVolume) + " мм³, то есть не больше нуля. Это отказ ДО операции: признак не " +
                "создавался, модель не тронута. К режиму tangent_group проверка не применяется: там " +
                "набор шире названных граней, и V + d·A его не описывает.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["volume_before_mm3"] = volumeBefore,
                    ["face_area_mm2"] = faceArea,
                    ["offset_mm"] = command.OffsetMm,
                    ["reverse"] = command.Reverse,
                    ["expected_volume_mm3"] = collapseVolume,
                });
        }

        // ROUTE: the container comes from IPart7, not from the document container: the documented entry
        // is «Дополнительный интерфейс компонента … у IPart7 посредством IUnknown::QueryInterface».
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Грани не перемещались.",
                RetryPolicy.ReacquireContext);
        }

        if (container is not IPart7 part7 || part7 is not ISurfaceContainer surfaces)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7 не отвечает как ISurfaceContainer: контейнер поверхностей недостижим, а другого "
                + "документированного входа к перемещению граней нет. Грани не перемещались.",
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
                    "). Грани не перемещались.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRefs[i] });
            }

            faces7[i] = face7;
        }

        object? added;
        try
        {
            added = surfaces.FaceMovers?.Add();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISurfaceContainer.FaceMovers.Add() прервался: " + ex.GetType().Name +
                ". Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (added is not IFaceMover mover)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "FaceMovers.Add() не вернул IFaceMover: коллекция операций недоступна. Грани не "
                + "перемещались.",
                RetryPolicy.ReacquireContext);
        }

        // TangentFaces comes from the caller: true widens the named set with the tangent chain.
        var accepted = SafeBool(() => mover.SetFaces(faces7, command.TangentGroup));
        if (accepted != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IFaceMover.SetFaces вернул " + (accepted is null ? "ошибку вызова" : "false") +
                ": набор граней не принят. Грани не перемещались.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_faces"] = faces.Count,
                    ["tangent_faces"] = command.TangentGroup,
                });
        }

        // DOC: ifacemover_faces.html — Faces is read-only «Массив граней … VT_ARRAY | VT_DISPATCH», so
        // what the operation holds after SetFaces IS the resolved set, tangent chain included. It is the
        // only measurable answer to «did the group widen», and it is read before the move.
        var groupSize = MoverFaceCount(mover);

        try
        {
            mover.Offset = command.OffsetMm;
            mover.Direction = command.Reverse;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства перемещения не записаны: " + ex.GetType().Name + ". Операция осталась в "
                + "документе без параметров и названа в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var updated = SafeBool(() => ((IModelObject)mover).Update());
        if (updated != true)
        {
            var details = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(details, ReadKompasFailure(document));
            // MEASURED: a FILLET's own face named without tangent_group is refused by the kernel
            // (code 33, "objects intersect with an error") while the same call on a PLANE whose tangent
            // neighbours are the fillets is accepted. The refusal names the flag, or the caller reads
            // "a fillet face cannot be moved at all".
            // History: docs/decisions/direct-faces.md#tangent-refusal
            details["tangent_group_suggested"] = !command.TangentGroup;
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() перемещения вернул " + (updated is null ? "ошибку вызова" : "false") +
                ": тело не перестроено. Смещение, снимающее тело с себя, даёт именно этот исход — это "
                + "отказ, а не частичный результат."
                + (command.TangentGroup
                    ? string.Empty
                    : " Если названная грань касательна соседям (например, грань скругления), повторите "
                      + "вызов с tangent_group=true: без флага ядро отказывает, когда цепочка не названа."),
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "solid.move_faces");

        var bodiesAfter = BodiesOf(document);
        var bodyAfter = targetIndex < bodiesAfter.Count ? bodiesAfter[targetIndex] : null;
        var volumeAfter = bodyAfter is null ? null : MassProperties(bodyAfter, (uint)KompasUnits.MassMmKg)?.v;
        var areaAfter = bodyAfter is null ? null : MassProperties(bodyAfter, (uint)KompasUnits.MassMmKg)?.F;
        var facesAfter = CountFaces(document);

        // INVARIANT: a body whose volume became zero was not moved, it was destroyed. MEASURED: a 30 mm
        // inward move on a 20 mm body answers Update() = true and leaves volume 0, which the distance
        // arithmetic would otherwise read as a long sweep.
        if (volumeBefore is double intact && Math.Abs(intact) > 1e-6d
            && volumeAfter is double collapsed && Math.Abs(collapsed) <= 1e-6d)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Объём тела стал нулевым (" + Num(volumeBefore) + " → " + Num(volumeAfter) +
                " мм³): смещение сняло тело с себя, и это отказ, а не перемещение. Тело осталось в "
                + "изменённом состоянии — см. partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["face_area_mm2"] = faceArea,
                    ["offset_mm"] = command.OffsetMm,
                });
        }

        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));

        bool ApplyDeclared(List<NamedCheck> target)
        {
            if (!declared.IsDeclared)
            {
                return true;
            }

            DeclaredExpectation.AddIfDeclared(target, "expected_volume", declared);
            if (declared.IsUnverifiable)
            {
                unverified.Add(DeclaredExpectation.UnverifiableReason("объём после перемещения", declared));
            }
            else if (declared.IsNotConfirmed)
            {
                unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после перемещения", declared));
            }

            return declared.IsConfirmed;
        }

        // INVARIANT: Update() = true without a change is NOT a success. MEASURED on the removal route:
        // the call answered true and the body stayed as it was, so the volume is what decides.
        if (volumeBefore is double start && volumeAfter is double end && Math.Abs(end - start) <= 1e-6d)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Update() вернул true, а объём тела не изменился (" + Num(volumeBefore) + " → " +
                Num(volumeAfter) + " мм³): операция принята, но не применена. Это отказ, а не "
                + "выполненное перемещение.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["face_area_mm2"] = faceArea,
                    ["offset_mm"] = command.OffsetMm,
                    ["reverse"] = command.Reverse,
                });
        }

        if (command.TangentGroup)
        {
            // MEASURED: the operation moves the tangent chain, so the swept set is wider than the named
            // one and V + d·A over the NAMED area does not predict the answer. Success is therefore
            // judged by signs that are documented and readable — the volume changed, the counts held, the
            // set the operation holds grew — and the level stays call_returned unless the caller declared
            // the volume. History: docs/decisions/direct-faces.md#tangent
            var groupChecks = new List<NamedCheck>
            {
                new("operation_updated", updated == true, "Update() вернул true"),
                new("volume_changed", FaceMoveEffect.VolumeChanged(volumeBefore, volumeAfter),
                    Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
                    Expected: "объём изменился — иначе перемещения нет"),
                new("body_count_unchanged", bodiesAfter.Count == bodyCountBefore,
                    Observed: bodyCountBefore + " → " + bodiesAfter.Count,
                    Expected: "перемещение грани не добавляет тел"),
                new("face_count_unchanged", facesAfter == facesBefore,
                    Observed: Num(facesBefore) + " → " + Num(facesAfter),
                    Expected: "перемещение грани не меняет число граней"),
            };

            var wider = FaceMoveEffect.GroupWider(groupSize, faces.Count);
            if (groupSize is int size)
            {
                groupChecks.Add(new NamedCheck("tangent_group_wider", wider,
                    Observed: size + " граней в наборе операции",
                    Expected: "больше названных " + faces.Count + ": цепочка касательных"));
            }
            else
            {
                unverified.Add("tangent_group_size_not_read — IFaceMover.Faces не ответил: широта набора "
                               + "не измерена, и «касательных граней нет» не утверждается");
            }

            var declaredConfirmed = ApplyDeclared(groupChecks);

            var groupNotes = new List<string>
            {
                "tangent_group_arithmetic_not_applicable — ожидаемый объём V + d·A относится к НАЗВАННЫМ "
                + "граням; в режиме касательных набор шире, поэтому объём не предсказывается, а "
                + "проверяется признаками: объём изменился, число граней прежнее, набор вырос",
                "tangent_faces_moved — число граней в наборе операции после SetFaces(Faces, true), "
                + "прочитано из IFaceMover.Faces",
            };

            var groupWarnings = new List<string>();
            if (groupSize is int held && !wider)
            {
                groupWarnings.Add("tangent_faces_none — у названных граней нет касательных соседей: "
                    + "SetFaces(..., true) дал тот же набор из " + held + " граней, перемещены только "
                    + "названные");
            }

            return new MoveFacesResult(
                faces.Count, command.OffsetMm, command.Reverse,
                ObservedOffsetMm: null, faceArea, perimeter, volumeBefore, volumeAfter, areaBefore,
                areaAfter, ExpectedVolumeMm3: null, ExpectedAreaMm2: null, bodiesAfter.Count,
                revisionBefore, document.Revision,
                new VerificationDto(
                    declared.IsDeclared && declaredConfirmed
                        ? VerificationLevel.GeometryChecked
                        : VerificationLevel.CallReturned,
                    groupChecks, unverified),
                groupNotes,
                Warnings: groupWarnings.Count == 0 ? null : groupWarnings)
            {
                TangentFacesMoved = groupSize,
            };
        }

        var observed = FaceMoveEffect.ObservedDisplacement(volumeBefore, volumeAfter, faceArea);
        var requested = Math.Abs(command.OffsetMm);

        // The requested distance is the prediction: the face must have swept |offset|, and a face of a
        // different area would give a different volume for the same distance.
        var distanceMatches = FaceMoveEffect.Matches(Math.Abs(observed ?? double.NaN), requested);

        // MEASURED: the kernel moves tangent neighbours even with TangentFaces = false, so the volume
        // grows past d·A of the named faces and no arithmetic separates the named face from the chain.
        // A LONGER sweep is therefore success with a warning; a shorter one stays a refusal.
        var implicitTangent = !distanceMatches && FaceMoveEffect.SweptFurther(observed, requested);

        if (!distanceMatches && !implicitTangent)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Перемещение не подтверждено числом: при площади граней " + Num(faceArea) + " мм² и "
                + "запрошенном смещении " + Num(command.OffsetMm) + " мм объём изменился на " +
                Num(volumeBefore is double v0 && volumeAfter is double v1 ? v1 - v0 : null) +
                " мм³, то есть на " + Num(observed) + " мм. Ожидалось смещение той же величины. "
                + "Тело осталось в изменённом состоянии — см. partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["face_area_mm2"] = faceArea,
                    ["observed_offset_mm"] = observed,
                    ["offset_mm"] = command.OffsetMm,
                });
        }

        // INVARIANT: an overshot volume is NOT predicted: the extra came from faces nobody named, and
        // the body area grows by more than the named perimeter's band, so neither is a prediction.
        var expectedVolume = !implicitTangent && observed is double displacement && faceArea is double area
            ? FaceMoveEffect.ExpectedVolume(volumeBefore ?? 0d, area, displacement)
            : (double?)null;
        var expectedArea = !implicitTangent && observed is double shift
            && perimeter is double band && areaBefore is double surface
            ? FaceMoveEffect.ExpectedArea(surface, band, shift)
            : (double?)null;

        var checks = new List<NamedCheck>
        {
            new("operation_updated", updated == true, "Update() вернул true"),
            new("body_count_unchanged", bodiesAfter.Count == bodyCountBefore,
                Observed: bodyCountBefore + " → " + bodiesAfter.Count,
                Expected: "перемещение грани не добавляет тел"),
            new("face_count_unchanged", facesAfter == facesBefore,
                Observed: Num(facesBefore) + " → " + Num(facesAfter),
                Expected: "перемещение грани не меняет число граней"),
            new("offset_matches", distanceMatches,
                Observed: Num(observed) + " мм по объёму", Expected: Num(command.OffsetMm) + " мм"),
        };

        if (expectedVolume is not null)
        {
            checks.Add(new NamedCheck("volume_matches", FaceMoveEffect.Matches(volumeAfter, expectedVolume),
                Observed: Num(volumeAfter) + " мм³", Expected: Num(expectedVolume) + " мм³ (V + d·A)"));
        }

        // The area is a SECOND, independent signal: the band belongs to the perimeter of the faces that
        // were named, so a face the kernel re-resolved to a neighbour of another size is caught here
        // even when the volume happens to match. It is compared only when the sweep is the requested one.
        var areaMatches = expectedArea is null || FaceMoveEffect.Matches(areaAfter, expectedArea);
        if (expectedArea is not null)
        {
            checks.Add(new NamedCheck("area_matches", areaMatches,
                Observed: Num(areaAfter) + " мм²", Expected: Num(expectedArea) + " мм² (F + P·d)"));
        }

        if (!areaMatches && expectedArea is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Площадь поверхности не подтверждает перемещение ИМЕННО названных граней: после "
                + "операции " + Num(areaAfter) + " мм², ожидалось " + Num(expectedArea) +
                " мм² (F + P·d при периметре " + Num(perimeter) + " мм). Объём при этом совпал, поэтому "
                + "перемещена другая грань той же площади. Тело осталось в изменённом состоянии.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["area_before_mm2"] = areaBefore,
                    ["area_after_mm2"] = areaAfter,
                    ["expected_area_mm2"] = expectedArea,
                    ["perimeter_mm"] = perimeter,
                    ["observed_offset_mm"] = observed,
                });
        }

        var geometryConfirmed = distanceMatches && areaMatches && FaceMoveEffect.Matches(volumeAfter, expectedVolume);
        geometryConfirmed &= ApplyDeclared(checks);

        var notes = new List<string>
        {
            // MEASURED: only Direction = true was probed, and it moved the face outward. Which side
            // false means is not established, so the tool reports the displacement it measured instead
            // of claiming a direction.
            "direction_reported_by_measurement — сторона перемещения не назначается: observed_offset_mm "
            + "показывает фактическое смещение со знаком (положительное — объём вырос)",
            "no_history_requirement — операция адресует грани и тело, а не признаки дерева, поэтому тело "
            + "без истории построения (импорт STEP) ею обрабатывается так же; приёмка - строки E5.STEP.*",
        };

        var warnings = new List<string>();
        if (implicitTangent)
        {
            // MEASURED: with TangentFaces = false the kernel still moved the tangent neighbours, so the
            // volume grew past d·A of the named face. Refusing here would report a move that happened as
            // not done; nothing in the API says which faces moved implicitly, so neither volume nor area
            // is predicted and the level stays call_returned.
            warnings.Add("implicit_tangent_faces_moved — объём вырос больше, чем d·A названных граней: "
                + "ядро сдвинуло и касательные соседи, хотя tangent_group не запрашивался; измеренное "
                + "смещение " + Num(observed) + " мм против запрошенных " + Num(requested) + " мм. "
                + "Объём в этом режиме не предсказывается, ответ - успех с этим предупреждением");
            unverified.Add("named_face_band_not_checked — площадь тела выросла больше полосы периметра "
                           + "названных граней (касательные дают свою добавку), поэтому полоса не "
                           + "отличает названную грань от цепочки");
        }

        unverified.Add("implicit_tangent_faces_not_named — SetFaces вызывается с TangentFaces = false, "
                       + "поэтому касательные соседи в набор не входят и их судьба здесь не читается");
        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не "
                       + "проверяется");

        return new MoveFacesResult(
            faces.Count,
            command.OffsetMm,
            command.Reverse,
            observed,
            faceArea,
            perimeter,
            volumeBefore,
            volumeAfter,
            areaBefore,
            areaAfter,
            expectedVolume,
            expectedArea,
            bodiesAfter.Count,
            revisionBefore,
            document.Revision,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            notes,
            Warnings: (warnings.Count == 0 ? null : warnings.Concat(
                DeclaredExpectation.Warnings(declared) ?? Array.Empty<string>()).ToList()));
    }

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM, for the same reason as at
    /// shell: an accepted-and-ignored number survives to acceptance looking like a completed move.
    /// </summary>
    private static void ValidateMoveFacesCommand(MoveFacesCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: грани перемещаются в конкретной детали.",
                RetryPolicy.Never);
        }

        if (command.FaceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список перемещаемых граней пуст. MEASURED: SetFaces с пустым массивом возвращает false, "
                + "то есть операция не создаётся вовсе; пустой вызов не выдаётся за перемещение. "
                + "Укажите хотя бы одну грань.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["requested_faces"] = 0 });
        }

        if (command.FaceRefs.Count != command.FaceRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список граней содержит повторы: одна и та же грань названа дважды, и площадь "
                + "перемещаемого набора перестала бы быть суммой разных граней.",
                RetryPolicy.Never);
        }

        if (!double.IsFinite(command.OffsetMm) || command.OffsetMm == 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Смещение должно быть конечным и не равным нулю, а получено " + Num(command.OffsetMm) +
                " мм. MEASURED: при Offset = 0 операция принимается (Update() = true) и тело не "
                + "меняется — такой вызов читался бы как выполненное перемещение.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["offset_mm"] = command.OffsetMm });
        }

    }

    /// <summary>Size of the set the operation holds, read from the documented read-only
    /// <c>IFaceMover.Faces</c>. <c>null</c> when the getter does not answer: «not read» differs from
    /// «one face».</summary>
    private static int? MoverFaceCount(IFaceMover mover)
    {
        try
        {
            return mover.Faces switch
            {
                null => null,
                Array array => array.Length,
                _ => 1,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Bodies of the document, read fresh: the collection is the model's own answer, not a
    /// remembered index.</summary>
    private static List<ksBody> BodiesOf(DocumentEntry document)
    {
        var bodies = new List<ksBody>();
        try
        {
            if (document.PartNow().BodyCollection() is not ksBodyCollection collection)
            {
                return bodies;
            }

            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksBody body)
                {
                    bodies.Add(body);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // An unreadable collection is an empty one: the caller names NO_BODY, which is honest for
            // "cannot tell" too, because nothing can be moved without a body.
        }

        return bodies;
    }

    /// <summary>Index of the body whose face collection holds this face, or <c>-1</c>. Identity is the COM
    /// pointer: the registry states the document and the revision but not the owner.
    /// </summary>
    private static int BodyIndexOf(IReadOnlyList<ksBody> bodies, ksFaceDefinition face)
    {
        var identity = ComIdentity.Of(face);
        if (identity is null)
        {
            return -1;
        }

        for (var index = 0; index < bodies.Count; index++)
        {
            try
            {
                if (bodies[index].FaceCollection() is not ksFaceCollection faces)
                {
                    continue;
                }

                for (var i = 0; i < faces.GetCount(); i++)
                {
                    if (string.Equals(ComIdentity.Of(faces.GetByIndex(i)), identity, StringComparison.Ordinal))
                    {
                        return index;
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A body that does not answer is skipped, not guessed: a wrong owner would move the face
                // of a neighbour.
            }
        }

        return -1;
    }

    /// <summary>Total area of the faces that will move, mm². <c>null</c> when any of them does not read:
    /// a partial sum would pass for the whole and understate the expected volume.</summary>
    private static double? SumFaceAreas(IReadOnlyList<ksFaceDefinition> faces, List<string> unverified)
    {
        var total = 0d;
        foreach (var face in faces)
        {
            var area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
            if (area is not double value)
            {
                unverified.Add("face_area_not_read — GetArea не ответил ни на одной из перемещаемых граней");
                return null;
            }

            total += value;
        }

        return total;
    }

    /// <summary>Total perimeter of the faces that will move, mm — the length of the band the move adds to
    /// the side faces. <c>null</c> when any face does not answer.</summary>
    private static double? SumFacePerimeters(IReadOnlyList<ksFaceDefinition> faces, List<string> unverified)
    {
        var total = 0d;
        foreach (var face in faces)
        {
            double sum;
            try
            {
                if (face.EdgeCollection() is not ksEdgeCollection edges)
                {
                    unverified.Add("face_perimeter_not_read — EdgeCollection грани не получена");
                    return null;
                }

                sum = 0d;
                for (var i = 0; i < edges.GetCount(); i++)
                {
                    if (AsInterface<ksEdgeDefinition>(edges.GetByIndex(i)) is not ksEdgeDefinition edge)
                    {
                        unverified.Add("face_perimeter_not_read — ребро грани не привелось к ksEdgeDefinition");
                        return null;
                    }

                    sum += SafeDouble(() => edge.GetLength((uint)KompasUnits.LengthMm)) ?? double.NaN;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                unverified.Add("face_perimeter_not_read — EdgeCollection прервалась: " + ex.GetType().Name);
                return null;
            }

            if (!double.IsFinite(sum))
            {
                unverified.Add("face_perimeter_not_read — длина ребра грани не прочитана");
                return null;
            }

            total += sum;
        }

        return total;
    }
}
