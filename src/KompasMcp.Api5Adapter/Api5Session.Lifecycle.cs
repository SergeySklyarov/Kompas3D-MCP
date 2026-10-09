using System.Runtime.InteropServices;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using Kompas6API5;

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
        document.Document3D.RebuildDocument();
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

        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declaredVolume = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        checks.Add(DeclaredExpectation.Check("volume_after_suppression", declaredVolume));
        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после изменения подавления", declaredVolume));
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

        // A DECLARED EXPECTATION THAT DID NOT HOLD IS A REFUSAL, and this is deliberately NOT the old
        // rule the pattern family followed (docs/decisions/adapter-core.md#pattern-declared-volume, where
        // a failed declaration only MARKED the result). The customer decision recorded in the decision doc unified the
        // rule: a declared expectation is the geometry check of the tool, so a mismatch refuses the call.
        // History: docs/decisions/adapter-core.md#declared-expectation-rule
        if (declaredVolume.IsRefusal)
        {
            throw DeclaredExpectation.Refusal(
                declaredVolume,
                "kompas_set_feature_suppressed",
                command.Suppressed ? "объём после подавления" : "объём после снятия подавления",
                document.Revision,
                consequence: "Для возврата подавление нужно снять или применить заново решением клиента.",
                extraDetails: new Dictionary<string, object?>
                {
                    ["suppress_operation"] = command.Suppressed ? "suppress" : "restore",
                    ["feature_name"] = stateAfter.Name,
                    ["feature_excluded"] = stateAfter.Excluded,
                    ["feature_is_valid"] = stateAfter.IsValid,
                    ["feature_object_error"] = stateAfter.ObjectError,
                });
        }

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
            new VerificationDto(level, checks, unverified));
    }

    public DeleteFeatureResult DeleteFeature(DeleteFeatureCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);
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

        document.Document3D.RebuildDocument();
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

        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declaredVolume = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadModelState(document).VolumeMm3);
        checks.Add(DeclaredExpectation.Check("volume_after_delete", declaredVolume));
        if (declaredVolume.IsRefusal)
        {
            throw DeclaredExpectation.Refusal(
                declaredVolume, "kompas_delete_feature", "объём после удаления", document.Revision);
        }

        if (declaredVolume.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после удаления", declaredVolume));
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
            volumeBefore,
            volumeAfter,
            new VerificationDto(
                structural && geometryConfirmed ? VerificationLevel.GeometryChecked
                    : featureRemoved ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified));
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
        VerificationDto Verification);

    public sealed record DeleteFeatureResult(
        string FeatureName,
        bool Deleted,
        int FeaturesBefore,
        int FeaturesAfter,
        IReadOnlyList<string> CandidateDependents,
        double? VolumeBeforeMm3,
        double? VolumeAfterMm3,
        VerificationDto Verification);

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
