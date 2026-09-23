using System.Runtime.InteropServices;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using Kompas6API5;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Подавление, восстановление и удаление признака (docs/05 §4.4, §7; SM-30 в каталоге).
///
/// Оба маршрута измерены пробой L от 12.09.2026 на живом v24 (docs/acceptance/api7/sketch-lifecycle.md),
/// а не выведены из names:
/// * L.7 — <c>ksFeature.excluded = true</c> снимает тело выдавливания (V становится объёмом
///   пластины), <c>false</c> возвращает; число признаков при этом не меняется. Одного
///   <c>RebuildDocument()</c> достаточно: в отличие от правки параметров (P2.3) здесь
///   <c>ksEntity.Update()</c> не требуется;
/// * L.8 — <c>ksDocument3D.DeleteObject(entity)</c> удаляет признак: возврат true, число признаков
///   минус один.
///
/// Члена «зависимые признаки» нет ни в API5, ни в API7 — это проверено рефлексией по обеим сборкам
/// (0 совпадений по Dependent*/Preceding*/UsedBy*), а не предположение. Поэтому API на вопрос
/// «что отвалится вместе с этим признаком» не отвечает, и сервер не притворяется, что отвечает:
/// возвращаются кандидаты — признаки, стоящие в дереве после удаляемого, — а их наличие блокирует
/// удаление, пока вызывающий не согласится явно.
/// </summary>
public partial class Api5Session
{
    public SuppressFeatureResult SetFeatureSuppressed(SuppressFeatureCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);

        // Та же пред-проверка, что и у удаления: мёртвый объект надо отклонять как устаревшую
        // ссылку, а не как «не тот тип» (строка L10: подавление уже удалённого признака давало
        // CAPABILITY_UNAVAILABLE, что правдоподобно, но неверно по существу).
        // Исключённый признак в коллекции 110 не показывается — поэтому он и спрашивается напрямую.
        // Проверка присутствия — ПО ИДЕНТИЧНОСТИ, а не по имени: измерено 19.09.2026 (проба I), что
        // два последовательных признака одного вида носят ОДНО имя, поэтому «имя есть в дереве»
        // отвечало бы «да» и про мёртвый объект, у которого остался одноимённый сосед.
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

        feature.excluded = command.Suppressed;
        document.Document.RebuildDocument();
        BumpRevision(document, command.Suppressed ? "feature.suppress" : "feature.restore");

        // Перечитывается с нового объекта признака: тот, что держали во время записи, может быть
        // кэшированным представлением, а «мы записали» доказательством того, что модель приняла, не является.
        var stateAfter = ReadFeatureState(entity);
        var volumeAfter = ReadVolume(document);
        var countAfter = CountFeatures(document);

        var readBack = stateAfter.Excluded == command.Suppressed;

        // Измерено этим прогоном (L04/L05, 12.09.2026): подавленный признак ИСЧЕЗАЕТ из
        // EntityCollection(o3d_operationElement=110) — kompas_list_features его не показывает, и
        // счётчик падает на единицу. Прежнее предположение «число признаков обязано остаться»
        // было поэтому неверным: оно меряло не гибель признака, а то, что исключённый признак
        // перестал попадать в эту коллекцию. Выживание доказывается самим объектом: имя то же,
        // состояние перечитывается.
        //
        // ЧТО ИЗМЕРЕНО 19.09.2026 (проба I, шаги E1–E6, прогон c90961c6a3ba478697da5bc243040719,
        // отчёт docs/acceptance/api7/feature-identity.json) И ЧЕГО ЗДЕСЬ БОЛЬШЕ НЕТ. Прежняя
        // редакция требовала `GetDefinition() is not null` как признака выживания и объявляла
        // потерю идентичности при ЛЮБОМ другом изменении счётчика. Оба требования оказались
        // дефектом прибора, а не фактом о продукте:
        // * у признаков B3 определения API5 НЕТ вовсе (§4.10.6: 69/633/50/79 — это номера в дереве,
        //   а `GetDefinition()` у них null), поэтому `survived` был ЛОЖНО false на каждом
        //   подавлении признака B3 и тянул за собой `feature_identity_lost`. «Определение
        //   читается» и «объект жив» — разные утверждения, и второе из первого не следует;
        // * подавление КАСКАДНО: подавление ПЕРВОГО из двух последовательных признаков изменения
        //   положения убрало из коллекции 110 ДВА элемента (4→2), а снятие подавления вернуло
        //   только один (2→3) — состояние ПОСЛЕ ОБОИХ достигается только снятием подавления с
        //   ОБОИХ (E4→E5). Это измеренный факт о зависимостях, а не потеря признака.
        // Поэтому счётчик больше не «объясняется единицей»: он измеряется, а превышение единицы
        // называется отдельным неподтверждённым аспектом, а не молчанием.
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

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            var tolerance = ProfileArea.Tolerance(expected);
            geometryConfirmed = Math.Abs(measured - expected) <= tolerance;
            checks.Add(new NamedCheck(
                "volume_after_suppression",
                geometryConfirmed,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "изменение геометрии не подтверждается");
            checks.Add(new NamedCheck(
                "volume_after_suppression",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        // Подавление приклейки объём уменьшает, подавление вырезания — увеличивает (измерено
        // пробой L.7: excluded=true на сквозном окне 40×20 вернуло пластине её 80000 мм³).
        // Направление поэтому не утверждается: наблюдаемый факт — что объём ИЗМЕНИЛСЯ, а каким
        // именно он обязан быть, заявляет вызывающий через expected_volume_mm3.
        var effectObserved = volumeBefore is double && volumeAfter is double
                             && Math.Abs(volumeAfter.Value - volumeBefore.Value) > VolumeChangeFloorMm3;
        checks.Add(new NamedCheck(
            command.Suppressed ? "volume_changed_on_suppress" : "volume_changed_on_restore",
            effectObserved,
            Observed: $"{volumeBefore?.ToString("0.####") ?? "нет"} → {volumeAfter?.ToString("0.####") ?? "нет"}"));

        // Уровень подтверждения держится на проверяемых утверждениях: состояние перечиталось,
        // объект признака жив, геометрия или эффект измерены. Счётчик в него не входит: каскад —
        // это свойство зависимостей, а не потеря доказательства (E3, проба I).
        var level = readBack && survived && (geometryConfirmed || effectObserved)
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
            // Не «ошибка», а измеренное свойство зависимостей: счётчик изменился сильнее, чем на
            // один элемент, потому что подавление признака уносит и стоящие после него зависимые
            // (E3: 4→2). Перечня зависимых в API нет (проба L.8), поэтому сервер называет
            // наблюдённое число и не выдаёт его ни за потерю признака, ни за полный откат.
            unverified.Insert(0, $"dependent_features_suppressed_together — число признаков изменилось на " +
                                 $"{countDelta} вместо одного: подавление уносит и зависимые признаки, " +
                                 "а перечислить их API не умеет. Состояние ПОСЛЕ достигается снятием " +
                                 "подавления с КАЖДОГО подавленного признака по отдельности");
        }
        if (!effectObserved && !geometryConfirmed)
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

        // Пред-проверка: признак обязан быть в модели. Без неё повторное удаление по уже
        // использованной ссылке возвращало «успех», ничего не удалив (строка L09 прогона
        // 12.09.2026: DeleteObject на мёртвом объекте отвечает true, дерево не меняется).
        // Исключённый признак в коллекции 110 тоже не показывается, поэтому отказа «не найден»
        // недостаточно: объект спрашивается напрямую, жив ли он.
        //
        // Присутствие и позиция берутся ПО ИДЕНТИЧНОСТИ (FindIt), а не по имени: два
        // последовательных признака одного вида носят одно имя (измерено 19.09.2026, проба I),
        // поэтому IndexOf(имя) вернул бы позицию ЧУЖОГО одноимённого признака и объявил бы
        // зависимыми не тех, кто стоит после удаляемого.
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

        // Позиция нужна ровно для одного: перечислить то, что стоит в дереве ПОСЛЕ удаляемого.
        //
        // ИДЕНТИЧНОСТЬ COM-объекта — первое и лучшее основание, но она НЕ переживает перестроение.
        // Измерено 19.09.2026 приёмкой: подавление и восстановление базового выдавливания (BG19/BG20)
        // пересоздают элемент дерева, и FindIt по прежнему объекту отвечает −1 — хотя объект жив и
        // отвечает IsCreated. На этом строка BG21 впервые за прогон удалила плиту вместо отказа:
        // позиция стала неизвестной, список зависимых вышел пустым, и «пусто» было прочитано как
        // «зависимых нет». Это НЕВЕРНЫЙ ЗНАЧОК ПО УМОЛЧАНИЮ: не знать, что стоит после признака, —
        // не то же самое, что знать, что после него ничего не стоит.
        //
        // Поэтому источников два, и они названы. Имя берётся ТОЛЬКО когда оно однозначно: два
        // последовательных признака одного вида носят одно имя (проба I), и «первое совпадение»
        // объявило бы зависимыми не тех. Неоднозначно — позиция НЕИЗВЕСТНА, и тогда кандидатами
        // объявляется всё дерево: удаление без явного согласия становится невозможным, а не свободным.
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
            deleted = document.Document.DeleteObject(entity);
        }
        catch (COMException ex)
        {
            // Отказ во время мутации — исход неизвестен, а не «чисто не получилось».
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"DeleteObject прервался: {ex.Message}. Сверьте фактическое состояние модели, " +
                "повтор той же операции запрещён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "feature.delete");

        var treeAfter = FeatureTreeElements(document);
        var volumeAfter = ReadVolume(document);

        // «Удалён» — по идентичности: объект-признак больше не находится в дереве. Проверка по
        // имени была бы ЛОЖНО отрицательной, если в модели остался одноимённый сосед (измерено
        // 19.09.2026, проба I: два признака «Изменение положения : Тело 1»).
        //
        // ТРИ УТВЕРЖДЕНИЯ РАЗДЕЛЕНЫ (наряд §6). Прежде здесь стояло
        //   gone = deleted && !stillPresent && treeAfter.Count == countBefore - 1;
        // Строгое «ровно на один меньше» делало ЛОЖНО отрицательным КАСКАД: клиентская приёмка
        // увидела дерево 5→3 (ушли целевой признак и его зависимые) и получила
        // feature_removed=false, хотя цель удалена. Замена == на <= сама по себе неверна в другую
        // сторону — она скрыла бы удаление ЛИШНИХ объектов. Поэтому:
        //   1) feature_removed              — удалён ли ЦЕЛЕВОЙ признак (по идентичности объекта);
        //   2) cascade_within_candidates    — ушли ли ТОЛЬКО он и объявленные кандидаты;
        //   3) independent_objects_preserved — целы ли объекты, стоявшие в дереве ДО него.
        // «Удалён целевой признак» и «каскад прошёл как ожидалось» — РАЗНЫЕ утверждения, и ни одно
        // из них не выводится из изменения общего числа признаков.
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

        // Независимые объекты — те, что стояли в дереве ДО цели. Проверяются ТОЛЬКО когда позиция
        // известна: при positionSource=unknown «до него» не определено, и утверждать нечего.
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

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            geometryConfirmed = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_after_delete",
                geometryConfirmed,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без ожидания объёма геометрия не подтверждена");
        }

        // Уровень проверки отвечает на вопрос «что именно доказано». Удаление целевого признака —
        // это структура; геометрия добавляется только тогда, когда подтверждён и каскад, и
        // сохранность независимых объектов. При удалённой цели и несостоявшемся каскаде уровень
        // остаётся структурным, а причина названа в unverified — понижать его до CallReturned
        // значило бы отрицать измеренное удаление, а повышать до geometry_checked — приписывать
        // себе проверку состава, которой не было.
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

    /// <summary>
    /// Имена объектов, которые БЫЛИ в дереве до операции и которых нет после.
    /// </summary>
    /// <remarks>
    /// Сравнение по имени — единственное доступное: COM-идентичность элемента дерева пересоздаётся
    /// перестроением (измерено 19.09.2026: после подавления и восстановления базового выдавливания
    /// <c>FindIt</c> по прежнему объекту отвечает −1), поэтому «тот же объект» по ней не
    /// восстанавливается. Имя даёт СОСТАВ ушедшего, а состав — ровно то, что нужно, чтобы отделить
    /// «удалён целевой признак» от «каскад прошёл как ожидалось» и от «ушло лишнее».
    /// Одноимённые элементы снимаются по одному: два одноимённых признака не закрываются одним
    /// ушедшим.
    /// </remarks>
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

    /// <summary>
    /// Элементы дерева признаков в порядке обхода <c>EntityCollection(110)</c> — вместе с их
    /// индексом и объектом.
    /// </summary>
    /// <remarks>
    /// Порядок здесь — наблюдаемый факт (порядок обхода коллекции), а не обещание, что КОМПАС
    /// перестраивает историю именно так. Поэтому позиция признака берётся не по номеру в этом
    /// списке, а через <see cref="TreePositionOf"/>: список служит для перечисления кандидатов, а
    /// не для адресации.
    /// </remarks>
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

    /// <summary>
    /// Позиция признака в дереве — ПО ИДЕНТИЧНОСТИ COM-объекта (<c>ksEntityCollection.FindIt</c>),
    /// а не по отображаемому имени.
    /// </summary>
    /// <remarks>
    /// Измерено 19.09.2026 (проба I, прогон <c>c90961c6a3ba478697da5bc243040719</c>): два
    /// последовательных признака изменения положения носят ОДНО имя
    /// «Изменение положения : Тело 1», поэтому поиск по имени указывает на первый из них всегда.
    /// <c>FindIt</c> отдаёт индекс с нуля и <c>−1</c> для объекта, которого в коллекции нет; оба
    /// исхода измерены на одном прогоне.
    /// </remarks>
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
}
