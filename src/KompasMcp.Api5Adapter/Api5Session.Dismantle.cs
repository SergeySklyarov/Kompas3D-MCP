using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>The exploded view of an assembly — block G9 (order <c>OMEGA_G9_DISMANTLE</c>).</summary>
/// <remarks>DOC: <c>idismantleparameters.html</c> — «Интерфейс может быть получен в качестве
/// дополнительного для интерфейса IAssemblyDocument посредством вызова метода IUnknown::QueryInterface»,
/// «Версия: Компас v20»; <c>idismantleparameters_adddismantlestep.html</c>;
/// <c>idismantlestep_parts.html</c>, <c>_distance.html</c>, <c>_directionobject.html</c>, <c>_vector.html</c>;
/// <c>ksapi_idismantlestep_update.html</c>.
/// INVARIANT: a step is confirmed by reading it back after <c>Update()</c>, never by the object the add call
/// returned. History: docs/decisions/dismantle.md#probe</remarks>
public partial class Api5Session
{
    private const string DismantleRoute =
        "api7:QI(IAssemblyDocument → IDismantleParameters) → AddDismantleStep/Parts/Distance/" +
        "DirectionObject → Update; чтение — DismantleStepsCount/DismantleStep[i]";

    // ── G9 ──
    /// <summary>Read the mode and the steps of the exploded view.</summary>
    public ListDismantleStepsResult ListDismantleSteps(ListDismantleStepsCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var (assembly, parameters) = RequireDismantle(document, notes);
        var top = TopPart7(document, notes);
        var steps = ReadSteps(parameters, document, top, notes);

        return new ListDismantleStepsResult(
            Bool(() => assembly.DismantleMode),
            Int(() => parameters.DismantleStepsCount) ?? steps.Count,
            steps,
            DismantleRoute,
            notes);
    }

    /// <summary>Turn the mode on or off and write the steps.</summary>
    /// <remarks>MEASURED: clearing the steps with the mode on returns the assembled placement, so a rewrite
    /// does not need the mode to be switched off first; a step written while the mode is off leaves the
    /// document with the mode on. History: docs/decisions/dismantle.md#probe</remarks>
    public SetDismantleResult SetDismantle(SetDismantleCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        var notes = new List<string>();
        var (assembly, parameters) = RequireDismantle(document, notes);
        var top = TopPart7(document, notes);
        var checks = new List<NamedCheck>();
        var unverified = new List<string>();

        var before = Int(() => parameters.DismantleStepsCount);
        checks.Add(new NamedCheck("steps_count_read_before", before is not null, before?.ToString()));

        if (command.DeleteSteps is { Count: > 0 } doomed)
        {
            // DOC: idismantleparameters_deletedismantlestep.html — the step at the index goes away and the
            // indices after it shift, so the highest index is removed first.
            foreach (var index in doomed.Distinct().OrderByDescending(position => position))
            {
                var deleted = Bool(() => parameters.DeleteDismantleStep(index));
                checks.Add(new NamedCheck("step_deleted_" + index, deleted == true));
                if (deleted != true)
                {
                    unverified.Add($"step_not_deleted_{index} — DeleteDismantleStep({index}) не подтвердил " +
                                   "удаление: шаг мог остаться");
                }
            }
        }

        if (command.Clear || command.Steps is { Count: 0 })
        {
            // DOC: idismantleparameters_cleardismantleparameters.html — removes every step at once.
            var cleared = Bool(() => parameters.ClearDismantleParameters());
            checks.Add(new NamedCheck("clear_returned", cleared == true));
            if (cleared != true)
            {
                unverified.Add("clear_not_confirmed — ClearDismantleParameters() не подтвердил очистку");
            }
        }
        else if (command.Steps is { Count: > 0 } requested)
        {
            // THE DOCUMENTED INTERFACE HAS NO «EDIT A STEP»: a rewrite is «clear, then add».
            if (before is > 0)
            {
                Bool(() => parameters.ClearDismantleParameters());
                notes.Add("steps_rewritten — шагов до записи " + before +
                          ": документированный интерфейс не правит шаг, поэтому старые удалены и записаны " +
                          "заново (ClearDismantleParameters, затем AddDismantleStep)");
            }

            foreach (var step in requested)
            {
                WriteStep(document, top, parameters, step, checks, unverified, notes);
            }
        }

        if (command.Mode is { } mode)
        {
            try
            {
                assembly.DismantleMode = mode;
                checks.Add(new NamedCheck("mode_written", Bool(() => assembly.DismantleMode) == mode));
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Запись IAssemblyDocument.DismantleMode прервалась: " + ex.Message +
                    ". Шаги могли быть записаны, режим — нет.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        document.Document3D.RebuildDocument();
        BumpRevision(document, "assembly.set_dismantle");

        var after = Int(() => parameters.DismantleStepsCount);
        var steps = ReadSteps(parameters, document, top, notes);
        checks.Add(new NamedCheck("steps_count_after", after is not null, after?.ToString(),
            command.Steps is null ? null : command.Steps.Count.ToString()));

        // LIMIT: the direction of a step cannot be read back (see DismantleStepRowDto), so the write of a
        // direction is confirmed by the measured placement change of an acceptance run, not here.
        unverified.Add("direction_not_read_back — DirectionObject читается null, Vector приходит без " +
                       "параметров: направление шага этим выпуском не читается и в результате не " +
                       "возвращается");
        unverified.Add("placement_change_not_verified — смещение компонентов проверяет приёмка " +
                       "(группа --dismantle-only), а не этот вызов");

        return new SetDismantleResult(
            Bool(() => assembly.DismantleMode),
            before ?? 0,
            after ?? steps.Count,
            steps,
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified),
            notes);
    }

    /// <summary>One step: components, distance, direction, then <c>Update()</c> — in that order, because the
    /// step enters the model only on <c>Update()</c> (MEASURED: count 0 before, 1 after).
    /// History: docs/decisions/dismantle.md#probe</summary>
    private void WriteStep(
        DocumentEntry document,
        IPart7? top,
        IDismantleParameters parameters,
        DismantleStepDto step,
        List<NamedCheck> checks,
        List<string> unverified,
        List<string> notes)
    {
        if (top is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Деталь сборки не получена как IPart7: адресовать компоненты нечем.",
                RetryPolicy.Never);
        }

        var parts = new List<object>();
        foreach (var reference in step.ComponentRefs)
        {
            parts.Add(RequireComponent(document, top, reference));
        }

        var direction = RequireDirection(document, step.DirectionRef);

        IDismantleStep? created;
        try
        {
            created = Ref(() => parameters.AddDismantleStep());
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "AddDismantleStep() прервался: " + ex.Message,
                RetryPolicy.Never);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "AddDismantleStep() не вернул IDismantleStep.",
                RetryPolicy.Never);
        }

        try
        {
            // DOC: Parts is a SafeArray of IPart7; one element and several are both written as an array and
            // the model returns a single VT_DISPATCH when the step has one component (MEASURED).
            created.Parts = parts.ToArray();
            created.Distance = step.DistanceMm;
            created.DirectionObject = direction;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Запись входов шага разнесения прервалась: " + ex.Message +
                ". Шаг мог остаться пустым; очистите шаги (clear) перед повтором.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        var updated = Int(() => created.Update());
        checks.Add(new NamedCheck("step_update_returned", updated is not null, updated?.ToString()));
        if (updated is null)
        {
            unverified.Add("step_update_unread — Update() шага не ответил: шаг мог не войти в модель");
        }

        notes.Add("step_written — компонентов " + parts.Count + ", расстояние " +
                  step.DistanceMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                  " мм; направление — ось «" + step.DirectionRef + "»");
    }

    /// <summary>The component of this assembly addressed by a reference, as the step expects it.</summary>
    private IPart7 RequireComponent(DocumentEntry document, IPart7 top, string reference)
    {
        var stored = References.Require(reference, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{reference}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        if (payload.Ordinal == NestedComponentOrdinal)
        {
            // The step takes IPart7 of an insertion; a nested component has no API5 address, so it is
            // refused instead of being addressed by a guessed ordinal.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Компонент вложен в подсборку: шаг разнесения адресуется компонентами верхнего уровня, " +
                "потому что ksDocument3D.PartCollection(true) перечисляет компоненты сборки ПЛОСКО.",
                RetryPolicy.Never);
        }

        if (payload.Part7 is not { } part7)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{reference}» не несёт объект IPart7.",
                RetryPolicy.ReacquireContext);
        }

        return part7;
    }

    /// <summary>The direction object: an auxiliary axis of this assembly.</summary>
    /// <remarks>MEASURED: a default plane, an edge, a face and the component itself left the placement
    /// unchanged; an auxiliary axis moved the component by exactly the step distance. The documented type is
    /// only <c>IModelObject</c>, so the axis is required and anything else is refused.
    /// History: docs/decisions/dismantle.md#probe</remarks>
    private IModelObject RequireDirection(DocumentEntry document, string reference)
    {
        var stored = References.Require(reference, document.Id, document.Revision);
        if (stored.Payload is not IModelObject model)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка «{reference}» не адресует направляющий объект: это {stored.Kind}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["direction_ref"] = reference,
                    ["supported_kinds"] = new[] { "axis" },
                });
        }

        return model;
    }

    private List<DismantleStepRowDto> ReadSteps(
        IDismantleParameters parameters,
        DocumentEntry document,
        IPart7? top,
        List<string> notes)
    {
        var rows = new List<DismantleStepRowDto>();
        var count = Int(() => parameters.DismantleStepsCount);
        if (count is null)
        {
            notes.Add("steps_count_unread — DismantleStepsCount не прочитан: шаги не перечислены");
            return rows;
        }

        var components = top is null ? new List<IPart7>() : ChildrenOf(top, notes);
        for (var index = 0; index < count; index++)
        {
            var position = index;
            var step = Ref(() => parameters.DismantleStep[position]);
            if (step is null)
            {
                notes.Add($"step_unread — шаг {position} не прочитан");
                continue;
            }

            var parts = StepParts(step, notes);
            var refs = new List<string>();
            foreach (var part in parts)
            {
                var ordinal = components.FindIndex(candidate => SameComObject(candidate, part));
                if (ordinal < 0)
                {
                    notes.Add($"step_component_unmatched — компонент шага {position} не найден среди " +
                              "компонентов сборки: адрес не выдан");
                    continue;
                }

                refs.Add(References.Register(
                    ComponentRefKind, document.Id, document.Revision,
                    new ComponentPayload(part, top, Int(() => part.Reference), ordinal)).Id);
            }

            rows.Add(new DismantleStepRowDto(
                position, refs, parts.Count, Double(() => step.Distance)));
        }

        return rows;
    }

    /// <summary>DOC: <c>idismantlestep_parts.html</c> — the value is a <c>SafeArray</c> when the step has
    /// several components and a single <c>VT_DISPATCH</c> when it has one (MEASURED).
    /// History: docs/decisions/dismantle.md#probe</summary>
    private List<IPart7> StepParts(IDismantleStep step, List<string> notes)
    {
        var parts = new List<IPart7>();
        object? raw;
        try
        {
            raw = step.Parts;
        }
        catch (COMException ex)
        {
            notes.Add("step_parts_unread — Parts бросил " + ex.GetType().Name + ": " + ex.Message);
            return parts;
        }

        switch (raw)
        {
            case Array array:
                parts.AddRange(array.OfType<IPart7>());
                break;
            case IPart7 single:
                parts.Add(single);
                break;
            case null:
                notes.Add("step_parts_empty — Parts шага пуст");
                break;
            default:
                notes.Add("step_parts_unexpected_type — Parts вернул " + raw.GetType().Name);
                break;
        }

        return parts;
    }

    /// <summary>The documented QI of the order: <c>IDismantleParameters</c> is an additional interface of
    /// <c>IAssemblyDocument</c> and is reached by a cast, nothing else.</summary>
    private (IAssemblyDocument Assembly, IDismantleParameters Parameters) RequireDismantle(
        DocumentEntry document, List<string> notes)
    {
        var application = RequireApplication(document.ApplicationId).Application;
        var document7 = Ref(() => application.TransferInterface(document.Document3D, 2, 0))
            as IKompasDocument3D;
        if (document7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ не перенесён в API7 как IKompasDocument3D: параметры разнесения недоступны.",
                RetryPolicy.Never);
        }

        if (document7 as IAssemblyDocument is not { } assembly)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ не отвечает QI(IAssemblyDocument): параметры разнесения недоступны.",
                RetryPolicy.Never);
        }

        if (assembly as IDismantleParameters is not { } parameters)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IAssemblyDocument не отвечает QI(IDismantleParameters): разнесённый вид этим выпуском " +
                "КОМПАС не поддерживается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["interface"] = "IDismantleParameters",
                    ["document_kind"] = document.Kind.ToString(),
                });
        }

        notes.Add("dismantle_interface_acquired — QI(IAssemblyDocument → IDismantleParameters) отвечает");
        return (assembly, parameters);
    }
}
