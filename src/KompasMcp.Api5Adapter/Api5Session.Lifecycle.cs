using System.Runtime.InteropServices;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api5Adapter;

/// <summary>Suppress, restore and delete a feature (docs/05 §4.4, §7; SM-30 in the catalog).</summary>
/// <remarks>MEASURED by probe L on live v24: <c>ksFeature.excluded</c> toggles the extrusion body
/// (<c>true</c> removes, <c>false</c> restores, count unchanged) and one <c>RebuildDocument()</c> suffices
/// (no <c>ksEntity.Update()</c>, unlike a parameter edit P2.3); <c>ksDocument3D.DeleteObject(entity)</c>
/// returns true, count minus one. INVARIANT: no "dependent features" member exists in API5 or API7
/// (reflection: 0 matches for Dependent*/Preceding*/UsedBy*), so the server returns candidates — features
/// after the deleted one — whose presence blocks deletion until the caller agrees.
/// History: docs/decisions/adapter-core.md#lifecycle-suppress-delete</remarks>
public partial class Api5Session
{
    public SuppressFeatureResult SetFeatureSuppressed(SuppressFeatureCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);

        // The same pre-check as for deletion: a dead object must be rejected as a stale reference, not as
        // "the wrong type" (row L10). A suppressed feature is not shown in collection 110, so it is asked
        // about directly; presence is checked BY IDENTITY, not by name (two consecutive features share a name).
        // History: docs/decisions/adapter-core.md#feature-suppression
        var presentInTree = TreePositionOf(document, entity) >= 0;
        if (!presentInTree && !SafeIsCreated(entity))
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Признак «{entity.name}» больше не в модели: подавление не выполнялось.",
                RetryPolicy.ReacquireContext);
        }

        if (entity.GetFeature() is not ksFeature feature)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Признак не отвечает на GetFeature() как ksFeature " +
                $"({entity.GetFeature()?.GetType().Name ?? "null"}): подавление неприменимо.",
                RetryPolicy.Never);
        }

        // INVARIANT: the state a later RESTORE is compared against must be a state of the BUILT model.
        // MEASURED: a rebuild can leave a feature unapplied, and the document then answers a volume
        // larger by exactly the wall of one hole; the write's own rebuild restored the model, so a record
        // taken on that state made the restore REFUSE a correct model. The model is therefore settled
        // BEFORE the record, and again after the write, so both compared sides are built states.
        // History: docs/decisions/adapter-core.md#rebuild-verify
        SettleRebuild(document);
        var volumeBefore = ReadVolume(document);
        var countBefore = CountFeatures(document);
        var stateBefore = ReadFeatureState(entity);
        // The state the model is in BEFORE the write, and the revision it is at. Both are needed by the
        // comparison a later restore performs: the state is what "came back" is measured against, and the
        // revision is how the session knows no other mutation moved the model in between.
        // The state is read TWICE: a single read can lag by one operation, and a record built on a stale
        // read would produce a FALSE refusal on the restore. A disagreement makes the comparison
        // unavailable instead (naryad PRE_RELEASE_0_6_0 П2.2).
        // History: docs/decisions/adapter-core.md#suppression-restore-comparison
        var modelBefore = ReadModelState(document);
        var modelBeforeRecheck = ReadModelState(document);
        var revisionBeforeWrite = document.Revision;

        feature.excluded = command.Suppressed;
        // Settled, not a single call: the state read below is what a later restore is compared against.
        SettleRebuild(document);
        BumpRevision(document, command.Suppressed ? "feature.suppress" : "feature.restore");

        // Re-read from a fresh feature object: the one held during the write may be a cached view, and
        // "we wrote it" is not evidence that the model accepted it.
        var stateAfter = ReadFeatureState(entity);
        var volumeAfter = ReadVolume(document);
        var countAfter = CountFeatures(document);
        var modelAfter = ReadModelState(document);

        var readBack = stateAfter.Excluded == command.Suppressed;

        // INVARIANT: the counter is measured, not "explained by one"; a deviation beyond one is named as a
        // separate unverified aspect, not silence. MEASURED: a suppressed feature DISAPPEARS from
        // EntityCollection(o3d_operationElement=110) and suppression CASCADES.
        // History: docs/decisions/adapter-core.md#feature-suppression
        var survived = stateAfter.Name == stateBefore.Name && SafeIsCreated(entity);
        var definitionReadable = entity.GetDefinition() is not null;
        var countDelta = countAfter - countBefore;
        var countDifferenceExplained = countDelta == 0
            || (countDelta == -1 && command.Suppressed)
            || (countDelta == 1 && !command.Suppressed);
        var cascadeSuspected = command.Suppressed ? countDelta < -1 : countDelta > 1;

        var checks = new List<NamedCheck>
        {
            new("excluded_read_back", readBack,
                Observed: stateAfter.Excluded ? "true" : "false",
                Expected: command.Suppressed ? "true" : "false"),
            new("feature_survives", survived,
                Observed: $"имя «{stateAfter.Name}», IsCreated={SafeIsCreated(entity)}, " +
                          $"определение API5={(definitionReadable ? "читается" : "отсутствует (штатно для этого семейства)")}, " +
                          $"признаков в коллекции 110: {countBefore}→{countAfter}"),
        };

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — что перестало строиться из-за подавления, сервер не " +
            "перечисляет: достоверного перечня зависимых в API нет (проба L.8)",
        };

        // The declared expectation is a FAILED CHECK with a warning when it did not hold, and a NAMED gap
        // when it could not be compared — neither is a refusal. The RESTORE comparison below STAYS a
        // refusal: it caught a genuinely wrong model.
        // History: docs/decisions/adapter-core.md#declared-expectation-rule
        var declaredVolume = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        checks.Add(DeclaredExpectation.Check("volume_after_suppression", declaredVolume));
        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после изменения подавления", declaredVolume));
        }
        else if (declaredVolume.IsNotConfirmed)
        {
            unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после изменения подавления", declaredVolume));
        }

        if (!declaredVolume.IsDeclared)
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "изменение геометрии не подтверждается");
        }

        var geometryConfirmed = declaredVolume.IsConfirmed;

        // Suppressing a boss decreases the volume, suppressing a cut increases it (MEASURED). The direction
        // is not asserted: the observed fact is that the volume CHANGED, and what it must be is declared by
        // the caller via expected_volume_mm3.
        var effectObserved = volumeBefore is double before && volumeAfter is double after
                             && VolumeMoved(after - before, before);
        checks.Add(new NamedCheck(
            command.Suppressed ? "volume_changed_on_suppress" : "volume_changed_on_restore",
            effectObserved,
            Observed: $"{volumeBefore?.ToString("0.####") ?? "нет"} → {volumeAfter?.ToString("0.####") ?? "нет"}"));

        // WHAT THE RESTORE IS CHECKED AGAINST. The state before the suppression was captured by THIS
        // session; the restore is compared with it, and a model that did not come back is a refusal.
        // A comparison that cannot be made is NAMED in unverified_aspects, never skipped silently.
        // History: docs/decisions/adapter-core.md#suppression-restore-comparison
        var comparison = command.Suppressed
            ? null
            : SuppressionRestorePolicy.Compare(
                document.Suppression, command.FeatureRef, revisionBeforeWrite, modelAfter,
                () => ReadModelState(document));
        if (comparison is { Verdict: RestoreVerdict.Mismatched })
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                comparison.Reason + ". Вызов НЕ считается успешным: клиент просил вернуть геометрию, "
                + "а получил другую модель. Модель оставлена в измеренном состоянии — молчаливого "
                + "отката (повторного подавления) сервер не делает.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "restored_to_pre_suppression_state",
                    ["operation"] = "restore",
                    ["volume_before_suppression_mm3"] = document.Suppression?.Before.VolumeMm3,
                    ["volume_suppressed_mm3"] = document.Suppression?.Suppressed.VolumeMm3,
                    ["volume_after_restore_mm3"] = modelAfter.VolumeMm3,
                    ["bodies_before_suppression"] = document.Suppression?.Before.BodyCount,
                    ["bodies_suppressed"] = document.Suppression?.Suppressed.BodyCount,
                    ["bodies_after_restore"] = modelAfter.BodyCount,
                    ["faces_before_suppression"] = document.Suppression?.Before.FaceCount,
                    ["faces_suppressed"] = document.Suppression?.Suppressed.FaceCount,
                    ["faces_after_restore"] = modelAfter.FaceCount,
                    ["volume_delta_mm3"] = comparison.VolumeDeltaMm3,
                    ["revision_after_suppression"] = document.Suppression?.RevisionAfterSuppress,
                    ["revision_before_restore"] = revisionBeforeWrite,
                    ["feature_name"] = stateAfter.Name,
                    ["feature_excluded"] = stateAfter.Excluded,
                    ["feature_is_valid"] = stateAfter.IsValid,
                    ["feature_object_error"] = stateAfter.ObjectError,
                });
        }

        if (command.Suppressed)
        {
            if (stateBefore.Excluded)
            {
                // The feature was ALREADY suppressed when this call arrived. Its pre-suppression state was
                // not observed by this call, and inventing one from the current (already suppressed) model
                // would compare a state with itself. The record is left untouched; the consequence is named.
                unverified.Add("pre_suppression_state_unavailable — признак был подавлен ДО этого вызова, "
                    + "поэтому состояние «до подавления» этим вызовом не наблюдалось и сверка при снятии "
                    + "будет недоступна (запомнить состояние уже подавленной модели значило бы сравнить "
                    + "её саму с собой)");
            }
            else if (modelBefore.IsReadable && modelAfter.IsReadable)
            {
                document.Suppression = new SuppressionRecord(
                    command.FeatureRef, stateAfter.Name, document.Revision, modelBefore, modelAfter,
                    modelBeforeRecheck);
                checks.Add(new NamedCheck(
                    "pre_suppression_state_recorded",
                    true,
                    Observed: $"объём {Num(modelBefore.VolumeMm3)}, тел {modelBefore.BodyCount}, "
                              + $"граней {modelBefore.FaceCount}; после подавления объём "
                              + $"{Num(modelAfter.VolumeMm3)}, тел {modelAfter.BodyCount}, "
                              + $"граней {modelAfter.FaceCount}",
                    Expected: "состояние до подавления запомнено сеансом — снятие будет сверено с ним"));
                if (!modelBefore.Matches(modelBeforeRecheck))
                {
                    // The two reads of "before" disagreed: the record is kept (a later restore may still
                    // re-read), but the comparison will be NAMED unavailable rather than give a false
                    // refusal. Naming it here makes the disagreement visible at suppression time too.
                    unverified.Add("pre_suppression_state_unreliable — состояние «до подавления» прочитано "
                        + "ДВАЖДЫ и чтения разошлись: объём " + Num(modelBefore.VolumeMm3)
                        + " и " + Num(modelBeforeRecheck.VolumeMm3) + ". Сверка при снятии будет названа "
                        + "недоступной, чтобы устаревшее чтение не стало ложным отказом");
                }
            }
            else
            {
                unverified.Add("pre_suppression_state_unreadable — состояние модели прочитать не удалось, "
                    + "поэтому сверка при снятии подавления будет недоступна");
            }
        }
        else if (comparison is { Verdict: RestoreVerdict.Matched })
        {
            checks.Add(new NamedCheck(
                "restored_to_pre_suppression_state",
                true,
                Observed: $"объём {Num(modelAfter.VolumeMm3)}, тел {modelAfter.BodyCount}, "
                          + $"граней {modelAfter.FaceCount}",
                Expected: $"объём {Num(document.Suppression!.Before.VolumeMm3)}, "
                          + $"тел {document.Suppression.Before.BodyCount}, "
                          + $"граней {document.Suppression.Before.FaceCount} — состояние до подавления, "
                          + "запомненное сеансом"));
            document.Suppression = null;
        }
        else if (comparison is not null)
        {
            // The record is KEPT: an unavailable comparison now does not mean it will stay unavailable —
            // a later restore of the recorded handle at the recorded revision is still comparable.
            unverified.Add("restore_comparison_unavailable — " + comparison.Reason);
        }

        // The verification level rests on checkable claims: the state re-read, the feature object alive,
        // the geometry or effect measured. The counter is not part of it: a cascade is a property of the
        // dependencies, not a loss of evidence (E3, probe I). On a RESTORE the comparison with the recorded
        // pre-suppression state replaces "the volume changed" whenever it is available: "it changed" is
        // true of a wrong model too (docs/decisions/adapter-core.md#suppression-restore-comparison).
        var geometryWitness = !command.Suppressed && comparison is { IsAvailable: true }
            ? comparison.IsMatched
            : effectObserved;
        var level = readBack && survived && (geometryConfirmed || geometryWitness)
            ? geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.StructureChecked
            : VerificationLevel.CallReturned;
        if (!readBack)
        {
            unverified.Insert(0, "state_not_read_back — excluded не перечитался запрошенным значением");
        }
        if (!survived)
        {
            unverified.Insert(0, "feature_identity_lost — признак перестал читаться как тот же объект");
        }
        if (!countDifferenceExplained)
        {
            unverified.Insert(0, "feature_count_unexplained — число признаков изменилось не на ту " +
                                 "единицу, которую даёт исключение из коллекции 110");
        }
        if (cascadeSuspected)
        {
            // A measured property of the dependencies: the counter changed by more than one because
            // suppressing a feature also takes its dependents. There is no dependent list in the API, so the
            // server names the observed number.
            unverified.Insert(0, $"dependent_features_suppressed_together — число признаков изменилось на " +
                                 $"{countDelta} вместо одного: подавление уносит и зависимые признаки, " +
                                 "а перечислить их API не умеет. Состояние ПОСЛЕ достигается снятием " +
                                 "подавления с КАЖДОГО подавленного признака по отдельности");
        }
        if (!geometryWitness && !geometryConfirmed)
        {
            unverified.Insert(0, "no_measured_volume_effect — изменение состояния признака не отразилось " +
                                 "на объёме: геометрия не подтверждена");
        }
        if (command.Suppressed)
        {
            unverified.Add("hidden_from_list_features — подавленный признак не показывается " +
                           "kompas_list_features (он исчезает из EntityCollection(110)); это " +
                           "измеренное ограничение контракта, а не потеря признака");
        }

        return new SuppressFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            command.Suppressed,
            stateAfter.Excluded,
            survived,
            countAfter,
            volumeBefore,
            volumeAfter,
            new VerificationDto(DeclaredExpectation.CapLevel(level, declaredVolume), checks, unverified),
            Warnings: DeclaredExpectation.Warnings(declaredVolume));
    }

    public DeleteFeatureResult DeleteFeature(DeleteFeatureCommand command)
    {
        if (!References.TryGet(command.FeatureRef, out var probe) || probe is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.FeatureRef}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(probe.DocumentId);
        var stored = References.Require(command.FeatureRef, document.Id, document.Revision);

        // INVARIANT: this tool deletes a FEATURE, a SKETCH or an AUXILIARY object of a part, never an
        // assembly component: a component has its own rules (dependent mates, a confirmed address) and its
        // own tool, so deleting it here would bypass both. The refusal is by the reference KIND and precedes
        // any COM mutation. DOC: ksdocoment3d_deleteobject.html - «Удалить можно любой трехмерный объект».
        // History: docs/decisions/adapter-core.md#delete-object-kinds
        if (stored.Kind == "component")
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ссылка адресует КОМПОНЕНТ сборки, а не объект детали. Компонент удаляется своим "
                + "инструментом kompas_delete_component: он читает зависимые сопряжения и требует "
                + "подтверждения. kompas_delete_feature компонент не удаляет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["reference_kind"] = stored.Kind,
                    ["use_tool"] = "kompas_delete_component",
                });
        }

        if (stored.Kind is "sketch" or "plane" or "axis" or "point")
        {
            return DeleteNonFeatureObject(document, stored, command);
        }

        var entity = stored.Payload as ksEntity
            ?? (stored.Payload as ksFeature)?.GetObject() as ksEntity;
        if (entity is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.FeatureRef}' указывает на {stored.Payload?.GetType().Name ?? "null"} "
                + "вместо признака.",
                RetryPolicy.ReacquireContext);
        }

        var name = entity.name ?? string.Empty;

        // Pre-check: the feature must be in the model — a repeated deletion by an already-used reference
        // returned "success" while deleting nothing (row L09). A suppressed feature is also not shown in
        // collection 110, so the object is asked directly whether it is alive. Presence and position are
        // taken BY IDENTITY (FindIt), not by name (two consecutive features share a name).
        // History: docs/decisions/adapter-core.md#delete-position
        var tree = FeatureTreeElements(document);
        var identityPosition = TreePositionOf(document, entity);
        if (identityPosition < 0 && !SafeIsCreated(entity))
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Признак «{name}» больше не в модели: объект не отвечает на IsCreated(), в дереве " +
                "его нет. Удаление не выполнялось.",
                RetryPolicy.ReacquireContext);
        }

        // The position is needed to enumerate what stands AFTER the deleted feature. INVARIANT: COM identity
        // does NOT survive a rebuild (FindIt on an old object answers −1 though it is alive). An unknown
        // position must not default to "no dependents": the name is taken ONLY when unambiguous, else the
        // whole tree is offered as candidates.
        // History: docs/decisions/adapter-core.md#delete-position
        var nameMatches = tree.Where(e => e.Name == name).ToList();
        var position = identityPosition >= 0
            ? identityPosition
            : nameMatches.Count == 1 ? nameMatches[0].Index : -1;
        var positionSource = identityPosition >= 0
            ? "identity"
            : nameMatches.Count == 1 ? "unique_name" : "unknown";

        var candidates = position >= 0
            ? tree.SkipWhile(e => e.Index <= position).Select(e => e.Name).ToList()
            : tree.Select(e => e.Name).ToList();
        if (candidates.Count > 0 && !command.ConfirmDependents)
        {
            throw new KompasContractException(
                ErrorCodes.DependentFeatures,
                positionSource == "unknown"
                    ? $"Позиция признака «{name}» в дереве не определена: одноимённых элементов " +
                      $"{nameMatches.Count}, и ни один не совпал с адресом по идентичности. " +
                      "Перечислить зависимые поэтому нельзя, и удаление не выполняется: " +
                      "«неизвестно, что после него» — не то же самое, что «после него ничего нет». " +
                      "Для удаления нужно явное согласие confirm_dependents=true."
                    : $"После признака «{name}» в дереве есть {candidates.Count} других; сервер не " +
                      "может назвать их зависимыми достоверно, поэтому удаляет только по явному " +
                      "согласию.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["feature_name"] = name,
                    ["candidate_dependents"] = candidates,
                    ["tree_position"] = position,
                    ["position_source"] = positionSource,
                    ["same_name_elements"] = nameMatches.Count,
                    ["identity_position"] = identityPosition,
                });
        }

        var volumeBefore = ReadVolume(document);
        var countBefore = tree.Count;

        bool deleted;
        try
        {
            deleted = document.Document3D.DeleteObject(entity);
        }
        catch (COMException ex)
        {
            // A failure during the mutation — the outcome is unknown, not "it cleanly did not work".
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"DeleteObject прервался: {ex.Message}. Сверьте фактическое состояние модели, " +
                "повтор той же операции запрещён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        SettleAfterMutation(document);
        BumpRevision(document, "feature.delete");

        var treeAfter = FeatureTreeElements(document);
        var volumeAfter = ReadVolume(document);

        // "Deleted" is by identity: a name check would be FALSELY negative if a same-named neighbour
        // remains. INVARIANT: three claims kept separate — 1) feature_removed (target gone by identity);
        // 2) cascade_within_candidates (only it and the declared candidates went); 3) independent_objects
        // preserved (objects before it intact); a strict "exactly one fewer" made a CASCADE falsely negative.
        // History: docs/decisions/adapter-core.md#delete-position
        var stillPresent = TreePositionOf(document, entity) >= 0;
        var featureRemoved = deleted && !stillPresent;

        var vanished = VanishedNames(tree, treeAfter);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { name };
        foreach (var candidate in candidates)
        {
            allowed.Add(candidate);
        }

        var outsideCandidates = vanished.Where(n => !allowed.Contains(n)).ToList();
        var cascadeWithinCandidates = vanished.Count > 0 && outsideCandidates.Count == 0;

        // Independent objects are those that stood BEFORE the target; checked ONLY when the position is
        // known, since with positionSource=unknown "before it" is undefined.
        var independent = position >= 0
            ? tree.Take(position).Select(e => e.Name).ToList()
            : new List<string>();
        var independentPreserved = independent.All(
            n => treeAfter.Any(e => string.Equals(e.Name, n, StringComparison.Ordinal)));

        var structural = featureRemoved && cascadeWithinCandidates && independentPreserved;

        var checks = new List<NamedCheck>
        {
            new("delete_object_returned", deleted, Observed: deleted ? "true" : "false"),
            new("feature_removed", featureRemoved,
                Observed: $"«{name}» " +
                          (stillPresent ? "остался в дереве" : "в дереве отсутствует")
                          + $", позиция определена по «{positionSource}»"),
            new("cascade_within_candidates", cascadeWithinCandidates,
                Observed: vanished.Count == 0
                    ? "ни один объект не ушёл из дерева"
                    : $"ушли [{string.Join("; ", vanished)}], вне объявленных кандидатов "
                      + $"[{string.Join("; ", outsideCandidates)}]; признаков {countBefore}→{treeAfter.Count}"),
            new("independent_objects_preserved", independentPreserved,
                Observed: independent.Count == 0
                    ? positionSource == "unknown"
                        ? "объекты до цели не проверялись: позиция не определена"
                        : "объектов до цели в дереве не было"
                    : $"до цели стояли [{string.Join("; ", independent)}] — все на месте: "
                      + independentPreserved),
        };

        var unverified = new List<string>
        {
            "dependents_are_candidates_only — перечень построен по порядку дерева, а не по связи " +
            "«кто на кого ссылается»: достоверного API-перечня зависимых нет (проба L.8)",
            deleted ? "orphaned_references_possible — ссылки предыдущей ревизии отклоняются реестром, " +
                      "но что именно перестало строиться, сервер не проверяет"
                    : "feature_not_removed — удаление не подтверждено перечитыванием дерева",
        };

        if (deleted && !cascadeWithinCandidates)
        {
            unverified.Add("cascade_composition_not_confirmed — состав ушедших объектов не совпал с "
                + "«цель плюс объявленные кандидаты»: ушли [" + string.Join("; ", vanished)
                + "], вне кандидатов [" + string.Join("; ", outsideCandidates) + "]");
        }

        if (position < 0)
        {
            unverified.Add("independent_objects_not_separable — позиция цели не определена "
                + $"(источник «{positionSource}»), поэтому «до неё» не восстановить и независимые "
                + "объекты этим вызовом не проверены");
        }

        // The declared expectation is a FAILED CHECK with a warning when it did not hold, and a NAMED gap
        // when it could not be compared — neither is a refusal.
        // History: docs/decisions/adapter-core.md#declared-expectation-rule
        var declaredVolume = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        checks.Add(DeclaredExpectation.Check("volume_after_delete", declaredVolume));
        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после удаления", declaredVolume));
        }
        else if (declaredVolume.IsNotConfirmed)
        {
            unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после удаления", declaredVolume));
        }

        if (!declaredVolume.IsDeclared)
        {
            unverified.Add("expected_volume_not_supplied — без ожидания объёма геометрия не подтверждена");
        }

        var geometryConfirmed = declaredVolume.IsConfirmed;

        // The verification level answers "what exactly was proved": removing the target is structure;
        // geometry is added only when the cascade and the preservation of independent objects are both
        // confirmed. A failed cascade keeps the level structural with the reason named in unverified.
        return new DeleteFeatureResult(
            name,
            deleted,
            countBefore,
            treeAfter.Count,
            candidates,
            vanished,
            volumeBefore,
            volumeAfter,
            new VerificationDto(
                DeclaredExpectation.CapLevel(
                    structural && geometryConfirmed ? VerificationLevel.GeometryChecked
                        : featureRemoved ? VerificationLevel.StructureChecked
                        : VerificationLevel.CallReturned,
                    declaredVolume),
                checks,
                unverified),
            Warnings: DeclaredExpectation.Warnings(declaredVolume));
    }

    /// <summary>Delete a SKETCH or an AUXILIARY-GEOMETRY object - objects NOT in the feature tree
    /// (collection 110), so the tree-order candidate rule cannot describe them.</summary>
    /// <remarks>MEASURED: before this branch existed a sketch reference reached the tree rule, the position
    /// came back unknown, and the WHOLE tree was offered as candidates - an extrusion on ANOTHER sketch was
    /// named a dependent. INVARIANT: "the dependent list could not be computed" is not "there are no
    /// dependents" - the first refuses without <c>confirm_dependents</c> and names why. DOC: <c>GetSketch()</c>
    /// exists on the extrusion/rotation/evolution/rib definitions, and no member containing <c>Depend</c>
    /// exists in either API. History: docs/decisions/adapter-core.md#delete-object-kinds</remarks>
    private DeleteFeatureResult DeleteNonFeatureObject(
        DocumentEntry document, StoredReference stored, DeleteFeatureCommand command)
    {
        var label = stored.Kind switch
        {
            "sketch" => "эскизе",
            "plane" => "вспомогательной плоскости",
            "axis" => "вспомогательной оси",
            _ => "вспомогательной точке",
        };
        var name = stored.Payload is ksEntity sketchEntity
            ? sketchEntity.name ?? stored.Kind
            : ReadModelObjectName(stored.Payload) ?? stored.Kind;

        var (candidates, route, routeNote) = stored.Kind switch
        {
            "sketch" => SketchDependents(document, stored),
            _ => AuxDependents(document, stored),
        };

        var treeBefore = FeatureTreeElements(document);

        if (candidates is null && !command.ConfirmDependents)
        {
            throw new KompasContractException(
                ErrorCodes.DependentFeatures,
                $"Перечислить зависимые объекты на {label} «{name}» документированным маршрутом не "
                + $"удалось: {routeNote}. «Перечень неизвестен» — не то же самое, что «зависимых нет», "
                + "поэтому удаление выполняется только по явному согласию confirm_dependents=true.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["object_kind"] = stored.Kind,
                    ["object_name"] = name,
                    ["dependent_route"] = route,
                    ["dependent_route_note"] = routeNote,
                });
        }

        if (candidates is { Count: > 0 } && !command.ConfirmDependents)
        {
            throw new KompasContractException(
                ErrorCodes.DependentFeatures,
                $"На {label} «{name}» построено объектов: {candidates.Count}. Удаление унесло бы их с "
                + "собой. Без confirm_dependents=true удаление не выполняется, модель не меняется. "
                + "Перечень — в details.candidate_dependents.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["object_kind"] = stored.Kind,
                    ["object_name"] = name,
                    ["candidate_dependents"] = candidates,
                    ["dependent_route"] = route,
                });
        }

        var volumeBefore = ReadVolume(document);
        var payload = stored.Payload
            ?? throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.FeatureRef}' не несёт объекта: удалять нечего.",
                RetryPolicy.ReacquireContext);
        var countsBefore = AuxCountsOf(document);

        // An auxiliary object is CREATED through API7 factories, so the handle the registry holds is an
        // API7 object, while the documented deletion route is the API5 ksDocument3D.DeleteObject. The object
        // is therefore transferred to API5 first - asking for API5 from an API7 object is the informative
        // direction - and the outcome is NAMED: "the same object came back" means no conversion happened and
        // is not read as a successful transfer.
        var bridge = BridgeFor(document);
        var transferred = stored.Kind == "sketch" ? null : bridge.TransferTo5(payload);
        var target = transferred is not null && !SameComObject(transferred, payload) ? transferred : payload;
        var transferNote = stored.Kind == "sketch"
            ? "объект уже API5 (ksEntity): перенос не требовался"
            : transferred is null
                ? "перенос API7->API5 не удался: в DeleteObject подан объект API7"
                : SameComObject(transferred, payload)
                    ? "перенос API7->API5 вернул ТОТ ЖЕ объект: конверсии не произошло"
                    : "объект передан в DeleteObject после переноса API7->API5";

        bool deleted;
        try
        {
            deleted = document.Document3D.DeleteObject(target);
        }
        catch (COMException ex)
        {
            // A failure during the mutation - the outcome is unknown, not "it cleanly did not work".
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"DeleteObject прервался: {ex.Message}. Сверьте фактическое состояние модели, "
                + "повтор той же операции запрещён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        SettleAfterMutation(document);
        BumpRevision(document, stored.Kind + ".delete");

        var countsAfter = AuxCountsOf(document);
        var stillPresent = NonFeatureStillPresent(document, stored, countsBefore, countsAfter);
        var removed = deleted && !stillPresent;
        var treeAfter = FeatureTreeElements(document);
        var volumeAfter = ReadVolume(document);
        var vanished = VanishedNames(treeBefore, treeAfter);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { name };
        foreach (var candidate in candidates ?? [])
        {
            allowed.Add(candidate);
        }

        var outside = vanished.Where(n => !allowed.Contains(n)).ToList();
        var cascadeWithin = vanished.Count == 0 || outside.Count == 0;

        var checks = new List<NamedCheck>
        {
            new("delete_object_returned", deleted, Observed: deleted ? "true" : "false"),
            new("object_removed", removed,
                Observed: $"«{name}» " + (stillPresent ? "остался в модели" : "в модели отсутствует")
                          + $" (счётчики коллекций до {DescribeAuxCounts(countsBefore)}, "
                          + $"после {DescribeAuxCounts(countsAfter)})"),
            new("cascade_within_candidates", cascadeWithin,
                Observed: vanished.Count == 0
                    ? "ни один признак не ушёл из дерева"
                    : $"ушли [{string.Join("; ", vanished)}], вне объявленных кандидатов "
                      + $"[{string.Join("; ", outside)}]; признаков {treeBefore.Count}->{treeAfter.Count}"),
            new("deletion_target_transfer", stored.Kind == "sketch" || transferred is not null,
                Observed: transferNote),
        };

        var unverified = new List<string>
        {
            "dependents_are_documented_route_only - перечень зависимых прочитан маршрутом "
            + route + ", а не выдан ядром: члена «зависимые объекты» нет ни в API5, ни в API7",
            "deletion_target_transfer - " + transferNote,
            removed
                ? "orphaned_references_possible - ссылки предыдущей ревизии отклоняются реестром, "
                  + "но что именно перестало строиться, сервер не проверяет"
                : "object_not_removed - удаление не подтверждено перечитыванием модели",
        };
        if (candidates is null)
        {
            unverified.Add("dependent_list_not_computable - " + routeNote);
        }

        if (deleted && !cascadeWithin)
        {
            unverified.Add("cascade_composition_not_confirmed - состав ушедших признаков не совпал с "
                + "«цель плюс объявленные кандидаты»: ушли [" + string.Join("; ", vanished)
                + "], вне кандидатов [" + string.Join("; ", outside) + "]");
        }

        var declaredVolume = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        checks.Add(DeclaredExpectation.Check("volume_after_delete", declaredVolume));
        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после удаления", declaredVolume));
        }
        else if (declaredVolume.IsNotConfirmed)
        {
            unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после удаления", declaredVolume));
        }

        if (!declaredVolume.IsDeclared)
        {
            unverified.Add("expected_volume_not_supplied - без ожидания объёма геометрия не подтверждена");
        }

        var structural = removed && cascadeWithin;
        return new DeleteFeatureResult(
            name,
            deleted,
            treeBefore.Count,
            treeAfter.Count,
            candidates ?? [],
            vanished,
            volumeBefore,
            volumeAfter,
            new VerificationDto(
                DeclaredExpectation.CapLevel(
                    structural && declaredVolume.IsConfirmed ? VerificationLevel.GeometryChecked
                        : removed ? VerificationLevel.StructureChecked
                        : VerificationLevel.CallReturned,
                    declaredVolume),
                checks,
                unverified),
            Warnings: DeclaredExpectation.Warnings(declaredVolume));
    }

    /// <summary>The features built ON a sketch, by the documented <c>GetSketch()</c> route.</summary>
    /// <remarks><c>Candidates</c> is <c>null</c> when the list could not be computed at all (a different
    /// statement from an empty list). History: docs/decisions/adapter-core.md#delete-object-kinds</remarks>
    private (List<string>? Candidates, string Route, string Note) SketchDependents(
        DocumentEntry document, StoredReference stored)
    {
        const string route = "GetSketch() определения признака (выдавливание, вращение, кинематическая "
            + "операция, отсечение по эскизу, ребро жёсткости)";
        if (stored.Payload is not ksEntity sketch)
        {
            return (null, route, "ссылка не содержит объекта эскиза");
        }

        var names = new List<string>();
        foreach (var (_, entity, featureName) in FeatureTreeElements(document))
        {
            var owner = SketchOfDefinition(entity);
            if (owner is not null && SameComObject(owner, sketch))
            {
                names.Add(featureName);
            }
        }

        return (names, route, "перечень прочитан");
    }

    /// <summary>Objects built ON an auxiliary plane: a sketch that lies on it.</summary>
    /// <remarks><c>Candidates</c> is <c>null</c> when the route could not be walked to the end - a read or a
    /// transfer that failed - because an empty list would be read as "nothing depends on it".
    /// <para>LIMIT: only SKETCHES are examined. A tilted plane's base plane and its base axis are reachable
    /// by the same documented shape (<c>ksPlaneAngleDefinition.GetPlane()/GetAxis()</c>), but the API5 side
    /// does not enumerate auxiliary planes the way collection 5 enumerates sketches, so those two are NOT
    /// checked and the note says so instead of implying a full list.</para>
    /// History: docs/decisions/adapter-core.md#delete-object-kinds</remarks>
    private (List<string>? Candidates, string Route, string Note) AuxDependents(
        DocumentEntry document, StoredReference stored)
    {
        const string route = "GetPlane() опоры эскиза (ksSketchDefinition.GetPlane)";
        if (stored.Kind != "plane" || stored.Payload is null)
        {
            return (null, route,
                "обратной ссылки «кто построен на этом объекте» нет ни в API5, ни в API7 (метаданные: "
                + "0 совпадений по Depend*), а коллекции API7 удаления не имеют");
        }

        var bridge = BridgeFor(document);
        var target = bridge.TransferTo7(stored.Payload);
        if (target is null)
        {
            return (null, route, "перенос удаляемой плоскости в пространство сравнения не удался");
        }

        var names = new List<string>();
        var examined = 0;
        var unreadable = 0;

        if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.Sketch))
            is ksEntityCollection sketches)
        {
            for (var i = 0; i < sketches.GetCount(); i++)
            {
                examined++;
                if (sketches.GetByIndex(i) is not ksEntity sketch)
                {
                    unreadable++;
                    continue;
                }

                var support = SketchSupportPlane(sketch);
                if (support is null)
                {
                    unreadable++;
                    continue;
                }

                if (SameTransferredPlane(bridge, support, target))
                {
                    names.Add(sketch.name ?? $"[{i}]");
                }
            }
        }

        if (unreadable > 0)
        {
            return (null, route,
                $"опору прочитать не удалось у {unreadable} из {examined} эскизов: перечень неполон, "
                + "а неполный перечень выдавать за полный нельзя");
        }

        return (names, route,
            $"осмотрено эскизов: {examined}; базовые плоскости наклонных и смещённых плоскостей и их "
            + "базовые оси этим маршрутом НЕ проверялись");
    }

    /// <summary>The support plane of a sketch, by the documented <c>ksSketchDefinition.GetPlane()</c>.</summary>
    private static object? SketchSupportPlane(ksEntity sketch)
    {
        try
        {
            return sketch.GetDefinition() is ksSketchDefinition definition ? definition.GetPlane() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether an API5 plane object is the SAME model object as an API7 one.</summary>
    private static bool SameTransferredPlane(Api7Bridge bridge, object api5Plane, object api7Target)
    {
        var transferred = bridge.TransferTo7(api5Plane);
        return transferred is not null && SameComObject(transferred, api7Target);
    }

    /// <summary>The sketch a feature's definition is built on, by the documented <c>GetSketch()</c>, or
    /// <c>null</c> when the family declares no such member or the read fails.</summary>
    private static object? SketchOfDefinition(ksEntity entity)
    {
        try
        {
            return entity.GetDefinition() switch
            {
                ksBaseExtrusionDefinition d => d.GetSketch(),
                ksBossExtrusionDefinition d => d.GetSketch(),
                ksCutExtrusionDefinition d => d.GetSketch(),
                ksBaseRotatedDefinition d => d.GetSketch(),
                ksBossRotatedDefinition d => d.GetSketch(),
                ksCutRotatedDefinition d => d.GetSketch(),
                ksBaseEvolutionDefinition d => d.GetSketch(),
                ksBossEvolutionDefinition d => d.GetSketch(),
                ksCutEvolutionDefinition d => d.GetSketch(),
                ksCutBySketchDefinition d => d.GetSketch(),
                ksRibDefinition d => d.GetSketch(),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether a non-feature object is still in the model after the mutation.</summary>
    /// <remarks>COM identity does NOT survive a rebuild, so a sketch is asked about directly
    /// (<c>IsCreated</c>) and an auxiliary object is judged by its collection's count. The count is the
    /// primary evidence: a re-enumerated wrapper would compare unequal even if the object were alive.</remarks>
    private bool NonFeatureStillPresent(
        DocumentEntry document, StoredReference stored,
        (int? Planes, int? Axes, int? Points) before, (int? Planes, int? Axes, int? Points) after)
    {
        if (stored.Payload is ksEntity entity && stored.Kind == "sketch")
        {
            return SafeIsCreated(entity);
        }

        var (countBefore, countAfter) = stored.Kind switch
        {
            "plane" => (before.Planes, after.Planes),
            "axis" => (before.Axes, after.Axes),
            _ => (before.Points, after.Points),
        };

        if (countBefore is not null && countAfter is not null)
        {
            return countAfter >= countBefore;
        }

        // Neither the count nor the identity is readable: the object is reported as still present, so a
        // failed read can never be laundered into a confirmed removal.
        return true;
    }

    /// <summary>Counts of the auxiliary collections, or <c>null</c> per collection when unreachable.</summary>
    private (int? Planes, int? Axes, int? Points) AuxCountsOf(DocumentEntry document)
    {
        var bridge = BridgeFor(document);
        var (model, auxiliary, _) = Api7AuxGeometry.Containers(bridge, document.PartNow());
        if (model is null || auxiliary is null)
        {
            return (null, null, null);
        }

        return (Api7AuxGeometry.SafeCount(auxiliary.Planes3D),
            Api7AuxGeometry.SafeCount(auxiliary.Axes3D),
            Api7AuxGeometry.SafeCount(model.Points3D));
    }

    private static string DescribeAuxCounts((int? Planes, int? Axes, int? Points) counts) =>
        $"плоскостей={counts.Planes?.ToString() ?? "не прочитано"}, "
        + $"осей={counts.Axes?.ToString() ?? "не прочитано"}, "
        + $"точек={counts.Points?.ToString() ?? "не прочитано"}";

    private static string? ReadModelObjectName(object? payload)
    {
        try
        {
            return (payload as IModelObject)?.Name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Names of objects that WERE in the tree before the operation and are gone after.</summary>
    /// <remarks>Comparison by name is the only one available: a tree element's COM identity is recreated by
    /// a rebuild, so "the same object" cannot be recovered. The name gives the COMPOSITION of what left.
    /// Same-named elements are removed one by one.</remarks>
    private static List<string> VanishedNames(
        List<(int Index, ksEntity Entity, string Name)> before,
        List<(int Index, ksEntity Entity, string Name)> after)
    {
        var left = after.Select(e => e.Name).ToList();
        var vanished = new List<string>();
        foreach (var element in before)
        {
            var at = left.IndexOf(element.Name);
            if (at < 0)
            {
                vanished.Add(element.Name);
            }
            else
            {
                left.RemoveAt(at);
            }
        }

        return vanished;
    }

    public sealed record SuppressFeatureResult(
        ReferenceDto FeatureRef,
        bool RequestedSuppressed,
        bool? ExcludedReadBack,
        bool FeatureSurvived,
        int FeatureCount,
        double? VolumeBeforeMm3,
        double? VolumeAfterMm3,
        VerificationDto Verification,
        IReadOnlyList<string>? Warnings = null);

    public sealed record DeleteFeatureResult(
        string FeatureName,
        bool Deleted,
        int FeaturesBefore,
        int FeaturesAfter,
        IReadOnlyList<string> CandidateDependents,
        // MEASURED: the objects that ACTUALLY left the tree (before minus after), never the promised
        // list. A cascade removes more than the target, and without this field the real composition is
        // readable only from a check's prose - a client that wants it must re-enumerate the tree
        // itself, which is a second, possibly newer, measurement.
        IReadOnlyList<string> RemovedObjects,
        double? VolumeBeforeMm3,
        double? VolumeAfterMm3,
        VerificationDto Verification,
        IReadOnlyList<string>? Warnings = null);

    /// <summary>Feature tree elements in the walk order of <c>EntityCollection(110)</c>, with their index
    /// and object.</summary>
    /// <remarks>The order here is an observed fact (the collection's walk order), not a promise that KOMPAS
    /// rebuilds history that way; the list serves to enumerate candidates, not to address them.</remarks>
    private List<(int Index, ksEntity Entity, string Name)> FeatureTreeElements(DocumentEntry document)
    {
        var elements = new List<(int, ksEntity, string)>();
        if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
            is not ksEntityCollection collection)
        {
            return elements;
        }

        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            if (collection.GetByIndex(i) is ksEntity entity)
            {
                elements.Add((i, entity, entity.name ?? $"[{i}]"));
            }
        }

        return elements;
    }

    /// <summary>A feature's position in the tree — BY COM OBJECT IDENTITY (<c>ksEntityCollection.FindIt</c>),
    /// not by its display name.</summary>
    /// <remarks>MEASURED: two consecutive reposition features carry ONE name, so a name search always
    /// points at the first of them. <c>FindIt</c> returns a zero-based index and <c>−1</c> if absent.</remarks>
    private int TreePositionOf(DocumentEntry document, ksEntity entity)
    {
        try
        {
            return document.PartNow()
                       .EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                   is ksEntityCollection collection
                ? collection.FindIt(entity)
                : -1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return -1;
        }
    }

    /// <summary>The three numbers a suppression is checked against: the TOTAL volume of the solid bodies,
    /// their count and their face count.</summary>
    /// <remarks>WHY THE TOTAL, NOT THE MAIN BODY: <see cref="ReadVolume"/> answers with the main body, and
    /// MEASURED that suppressing a pattern of BODIES takes whole bodies away. The total sums
    /// <c>ksPart.BodyCollection()</c> after its own <c>refresh()</c>, and the face count uses the same
    /// documented call <c>CountUniqueEdges</c> already makes. INVARIANT: an unread body or volume yields
    /// <see cref="ModelStateSnapshot.Unreadable"/>, never a partial sum — a partial sum would look smaller.
    /// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
    private ModelStateSnapshot ReadModelState(DocumentEntry document)
    {
        try
        {
            var part = document.PartNow();
            var bodies = (ksBodyCollection)part.BodyCollection();
            bodies.refresh();
            var count = bodies.GetCount();

            // BodyCollection reporting empty while the part still hands back a main body is a measured
            // shape (see ListBodies): the fallback keeps the count and the volume from contradicting each
            // other, which is what a comparison must not do.
            if (count == 0)
            {
                if (part.GetMainBody() is not ksBody only)
                {
                    return new ModelStateSnapshot(0d, 0, 0);
                }

                var onlyVolume = MassProperties(only, (uint)KompasUnits.MassMmKg)?.v;
                var onlyFaces = FaceCountOf(only);
                return onlyVolume is null || onlyFaces < 0
                    ? ModelStateSnapshot.Unreadable
                    : new ModelStateSnapshot(onlyVolume, 1, onlyFaces);
            }

            var total = 0d;
            var faces = 0;
            for (var i = 0; i < count; i++)
            {
                var body = AsInterface<ksBody>(bodies.GetByIndex(i))
                           ?? (i == 0 ? AsInterface<ksBody>(part.GetMainBody()) : null);
                if (body is null)
                {
                    return ModelStateSnapshot.Unreadable;
                }

                var volume = MassProperties(body, (uint)KompasUnits.MassMmKg)?.v;
                var faceCount = FaceCountOf(body);
                if (volume is null || faceCount < 0)
                {
                    return ModelStateSnapshot.Unreadable;
                }

                total += volume.Value;
                faces += faceCount;
            }

            return new ModelStateSnapshot(total, count, faces);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return ModelStateSnapshot.Unreadable;
        }
    }

    /// <summary>Face count of one body through the documented <c>ksBody.FaceCollection()</c>; <c>−1</c>
    /// when the collection cannot be read, which makes the whole snapshot unreadable rather than "fewer
    /// faces".</summary>
    private static int FaceCountOf(ksBody body)
    {
        try
        {
            return body.FaceCollection() is ksFaceCollection faces ? faces.GetCount() : -1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return -1;
        }
    }
}
