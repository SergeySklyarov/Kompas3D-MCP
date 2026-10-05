using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>Mates domain — block C2, profile <c>mates-minimal-v1</c>, modes <c>MATE-01…MATE-06</c>.</summary>
/// <remarks>ROUTE — the documented API7 path: <c>IPart7.MateConstraints</c> →
/// <c>IMateConstraints3D.Add(MateConstraintType)</c> → <c>BaseObject1/2</c> → <c>Update()</c>;
/// confirmation is <c>Valid</c>, not <c>Update()=true</c>.
/// LIMIT: <c>ksDocument3D.AddMateConstraint</c> is NOT used — it returned <c>False</c> under every
/// documented combination; cause not established, closed by decision.
/// History: docs/decisions/mates.md#api5-addmateconstraint</remarks>
public sealed partial class Api5Session
{
    private const string MateRefKind = "mate";

    /// <summary>Payload of a mate reference: the API5 object, its ordinal and type.</summary>
    private sealed record MatePayload(ksMateConstraint Mate5, int Ordinal, short ConstraintType);

    // ── MATE-02 ──
    /// <summary>Enumerate the assembly's mates.</summary>
    public ListMatesResult ListMates(ListMatesCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var rows = ReadMates(document, notes);

        return new ListMatesResult(
            rows,
            rows.Count,
            "API5 ksDocument3D.MateConstraintCollection → GetCount/GetByIndex → ksMateConstraint." +
            "GetBaseObj/constraintType/distance/fixed; подтверждение — IMateConstraint3D.Valid (API7)",
            notes);
    }

    // ── MATE-01 ──
    /// <summary>Create a mate between faces of two components.</summary>
    public CreateMateResult CreateMate(CreateMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var type = MateTypeFromName(command.ConstraintType);
        var notes = new List<string>();

        var first = FaceObject7(document, command.FirstComponentRef, command.FirstFaceIndex, notes);
        var second = FaceObject7(document, command.SecondComponentRef, command.SecondFaceIndex, notes);

        var mates7 = RequireMateConstraints(document);

        // THE COUNTER IS NOT REPLACED BY ZERO: `?? 0` would mean "no mates" where the number was not
        // read, and `?? before` would mean "unchanged" — both replace the UNKNOWN with the KNOWN.
        var before = RequireMateCount(document, mates7, "до создания");
        var beforeRows = ReadMates(document, notes);

        IMateConstraint3D? mate;
        try
        {
            mate = mates7.Add(type);
            if (mate is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "IMateConstraints3D.Add вернул null: сопряжение не создано.",
                    RetryPolicy.SameOperationId);
            }

            mate.BaseObject1 = first;
            mate.BaseObject2 = second;
            if (AlignmentFromName(command.Alignment) is { } alignment)
            {
                mate.Alignment = alignment;
            }

            if (command.ParamValue is { } value)
            {
                mate.ParamValue = value;
            }
        }
        catch (COMException ex)
        {
            // The mate object ALREADY exists (Add created it): a partial effect must bump the
            // revision and revoke references, otherwise the model changed while the revision and
            // references stay old (defect M4).
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Создание сопряжения прервалось: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        bool? updated;
        try
        {
            updated = mate.Update();
        }
        catch (COMException ex)
        {
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"IMateConstraint3D.Update() бросил: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        var valid = Bool(() => mate.Valid);
        var after = RequireMateCount(document, mates7, "после создания");

        // CONFIRMATION IS Valid, not "Update()=true": a mate between two objects of the SAME component
        // also gives Update()=true but Valid=false.
        if (updated != true || valid != true)
        {
            // An invalid mate ALREADY lies in the assembly: the model changed, and the revision must
            // show it.
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Сопряжение НЕ подтверждено: Update()={updated}, Valid={valid}, сопряжений " +
                $"{before} → {after}. Запись могла быть создана, но недействительна.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["update"] = updated,
                    ["valid"] = valid,
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        BumpRevision(document, "mate.create");

        var rows = ReadMates(document, notes);

        // THE NEW MATE IS FOUND BY SET DIFFERENCE, NOT BY "THE LAST ROW": `rows.LastOrDefault()` assumes
        // the new mate was appended and that the collection order matches the API7 indexer — the same
        // unverified correspondence as for components. The difference is over the MULTISET of signatures,
        // so two identical mates still yield exactly one new one.
        var created = FindNewMate(beforeRows, rows, out var createdNote);
        notes.Add(createdNote);
        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Сопряжение создано (сопряжений " + before + " → " + after + "), но выделить его в " +
                "коллекции не удалось: " + createdNote + ". Адресовать сопряжение нечем.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        if (after <= before)
        {
            // The mate count did not grow while the result was already returned as success — the same
            // defect class as "success on a failed check".
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Сопряжение НЕ создано: число сопряжений {before} → {after} при Update()={updated} " +
                "и Valid=true. Успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        var stored = References.Register(
            MateRefKind, document.Id, document.Revision,
            new MatePayload(MateAt(document, created.Ordinal ?? 0)!, created.Ordinal ?? 0, (short)type));

        return new CreateMateResult(
            ToDto(References.Require(stored.Id, document.Id, document.Revision), "mate"),
            created with { MateRef = stored.Id },
            after,
            new VerificationDto(
                VerificationLevel.StructureChecked,
                new List<NamedCheck>
                {
                    new("mate_created", after > before, Observed: $"{before} → {after}",
                        Expected: $"{before + 1}"),
                    new("mate_valid", true, Observed: "Valid=true"),
                    new("mate_read_back", true,
                        Observed: $"тип «{created.ConstraintType}», объектов два: " +
                                  $"{created.BaseObject1 is not null} / {created.BaseObject2 is not null}"),
                },
                notes));
    }

    // ── MATE-03 ──
    /// <summary>Change a mate parameter (distance or angle).</summary>
    public SetMateParameterResult SetMateParameter(SetMateParameterCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var mate = Mate7(document, payload.Ordinal);
        if (mate is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Сопряжение по номеру {payload.Ordinal} не получено как IMateConstraint3D: " +
                "параметр этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED BEFORE THE MUTATION: the ordinal from the API5 collection is applied to
        // the API7 indexer, and a mismatch or unreadability would write the parameter into a FOREIGN
        // mate (defect M7).
        var identity = MateIdentityMatches(payload, mate, out var identityNote);
        if (identity != true)
        {
            throw MateIdentityRefusal(document, command.MateRef, payload, identity, identityNote);
        }

        var before = SafeDouble(() => mate.ParamValue);
        try
        {
            mate.ParamValue = command.ParamValue;
            mate.Update();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись ParamValue прервалась: {ex.Message}. Параметр не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        var after = SafeDouble(() => mate.ParamValue);
        BumpRevision(document, "mate.set_parameter");

        var matched = before is not null && after is not null
            && Math.Abs(after.Value - command.ParamValue) <= 1e-6;
        var checks = new List<NamedCheck>
        {
            new("param_value_read_back", matched,
                Observed: after?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: command.ParamValue.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
        };

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the re-read parameter did not match the requested
        // one (or was not read), and a result of "done" would be a lie.
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Параметр сопряжения ЗАПИСАН, но не подтверждён: перечитано "
                + (after is null
                    ? "НЕ ЧИТАЕТСЯ"
                    : after.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                + ", запрошено " + command.ParamValue.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + ". Успехом это не считается: модель изменена, параметр не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["param_before"] = before,
                    ["param_after"] = after,
                    ["param_requested"] = command.ParamValue,
                });
        }

        return new SetMateParameterResult(
            ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate"),
            before,
            after,
            new VerificationDto(matched ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks,
                matched ? new List<string>() : new List<string>
                {
                    "param_value_not_read_back — перечитанное значение не совпало с заданным",
                }));
    }

    // ── MATE-04 ──
    /// <summary>Set the component-fixing flag of a mate.</summary>
    public SetMateFixedResult SetMateFixed(SetMateFixedCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var mate = Mate7(document, payload.Ordinal);
        if (mate is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Сопряжение по номеру {payload.Ordinal} не получено как IMateConstraint3D: " +
                "признак фиксации этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED BEFORE THE MUTATION — by the same rule as the parameter: a fixing flag
        // written into a FOREIGN mate changes the wrong model.
        var identity = MateIdentityMatches(payload, mate, out var identityNote);
        if (identity != true)
        {
            throw MateIdentityRefusal(document, command.MateRef, payload, identity, identityNote);
        }

        var wanted = FixedFromName(command.Fixed);
        // The fixing flag is read EXPLICITLY: an unread value is not replaced by the first enum value
        // (`default(ksMateFixedTypeEnum)` = `ksMFixedUnknown`, i.e. "unfix" where the COM read did not
        // happen).
        var beforeRead = TryRead(() => mate.Fixed);
        try
        {
            mate.Fixed = wanted;
            mate.Update();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись Fixed прервалась: {ex.Message}. Признак фиксации не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        var afterRead = TryRead(() => mate.Fixed);
        BumpRevision(document, "mate.set_fixed");

        // A match is confirmed ONLY by a read value: `afterRead.Ok == false` is "not read", not "matched".
        var matched = afterRead.Ok && afterRead.Value == wanted;
        var before = beforeRead.Ok ? beforeRead.Value : (ksMateFixedTypeEnum?)null;
        var after = afterRead.Ok ? afterRead.Value : (ksMateFixedTypeEnum?)null;

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the fixing flag was written, but the re-read value
        // did not match the requested one or was not read.
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Признак фиксации ЗАПИСАН, но не подтверждён: перечитано «"
                + (FixedName(after) ?? "не читается") + "», запрошено «" + command.Fixed + "». Успехом это " +
                "не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["fixed_before"] = FixedName(before),
                    ["fixed_after"] = FixedName(after),
                    ["fixed_requested"] = command.Fixed,
                });
        }

        return new SetMateFixedResult(
            ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate"),
            FixedName(before),
            FixedName(after),
            new VerificationDto(matched ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                new List<NamedCheck>
                {
                    new("fixed_read_back", matched,
                        Observed: FixedName(after) ?? "не читается", Expected: command.Fixed),
                },
                matched ? new List<string>() : new List<string>
                {
                    "fixed_not_read_back — перечитанный признак не прочитан или не совпал с заданным",
                }));
    }

    // ── MATE-05 ──
    /// <summary>Delete a mate.</summary>
    public DeleteMateResult DeleteMate(DeleteMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var before = MateConstraintCount(document, "до удаления");

        // Deletion is the documented ksDocument3D.RemoveMateConstraint(constraintType, obj1, obj2)
        // (ksdocument3d_removemateconstraint.html). The objects are taken from the mate itself by the
        // same documented GetBaseObj(1|2) — "the first one found" would be an address substitution.
        var first = Ref(() => payload.Mate5.GetBaseObj(1));
        var second = Ref(() => payload.Mate5.GetBaseObj(2));
        if (first is null || second is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Базовые объекты сопряжения не прочитаны (GetBaseObj вернул null): удалять нечем — " +
                "RemoveMateConstraint требует оба объекта.",
                RetryPolicy.ReacquireContext);
        }

        bool removed;
        try
        {
            removed = document.Document.RemoveMateConstraint(payload.ConstraintType, first, second);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"RemoveMateConstraint прервался: {ex.Message}. Сопряжение не удалено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        var after = MateConstraintCount(document, "после удаления");

        if (!removed || after >= before)
        {
            // A deletion that KOMPAS confirmed but the re-read count did not means: the model changed
            // while the revision and references do not know about it.
            if (removed)
            {
                BumpRevision(document, "mate.delete.partial", invalidateAll: true);
            }

            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Сопряжение НЕ удалено: RemoveMateConstraint={removed}, сопряжений {before} → {after}.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        // The DTO is built BEFORE references are revoked: `InvalidateDocument` drops ALL references of
        // the document, and a `Require` after it would reject the very reference used for deletion.
        var dto = ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate");

        References.InvalidateDocument(document.Id);
        BumpRevision(document, "mate.delete");

        return new DeleteMateResult(
            dto,
            before,
            after,
            new VerificationDto(
                VerificationLevel.StructureChecked,
                new List<NamedCheck>
                {
                    new("mate_removed", after < before, Observed: $"{before} → {after}",
                        Expected: $"{before - 1}"),
                },
                new List<string>()));
    }

    // ── helpers ──

    /// <summary>The assembly's mates as <c>IMateConstraints3D</c> (API7).</summary>
    private IMateConstraints3D RequireMateConstraints(DocumentEntry document)
    {
        var notes = new List<string>();
        var top = TopPart7(document, notes);
        if (top?.MateConstraints is not { } mates)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.MateConstraints не вернул IMateConstraints3D: коллекция сопряжений недостижима.",
                RetryPolicy.ReacquireContext);
        }

        return mates;
    }

    /// <summary>A mate by ordinal as <c>IMateConstraint3D</c> (API7).</summary>
    private IMateConstraint3D? Mate7(DocumentEntry document, int ordinal)
    {
        try
        {
            return RequireMateConstraints(document).MateConstraint3D[ordinal];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether this is the same mate: the API7 type and base objects are compared against the
    /// API5 collection at the same ordinal.</summary>
    /// <returns><c>true</c> — identity confirmed; <c>false</c> — the ordinal leads to another mate;
    /// <c>null</c> — nothing to compare.</returns>
    /// <remarks>WHY: the API7 mate is taken by the ordinal from the API5 collection; that API7↔API5 order
    /// correspondence is an ASSUMPTION, and if violated the parameter would be written into a FOREIGN mate.
    /// WHAT IS COMPARED: type and base objects by PRESENCE (bitwise comparison is NOT done).
    /// History: docs/decisions/mates.md#mate-identity</remarks>
    private bool? MateIdentityMatches(MatePayload payload, IMateConstraint3D mate7, out string detail)
    {
        var type5 = payload.ConstraintType;
        var type7 = Int(() => (int)mate7.ConstraintType);
        var base1Api5 = Ref(() => payload.Mate5.GetBaseObj(1)) is not null;
        var base2Api5 = Ref(() => payload.Mate5.GetBaseObj(2)) is not null;
        var base1Api7 = Ref(() => mate7.BaseObject1) is not null;
        var base2Api7 = Ref(() => mate7.BaseObject2) is not null;

        var signals = new List<string>();
        var confirmed = false;
        var contradicted = false;

        if (type7 is not null)
        {
            var equal = (short)type7.Value == type5;
            signals.Add(equal
                ? $"тип сопряжения совпал («{MateTypeName(type7.Value)}»)"
                : $"тип сопряжения РАСХОДИТСЯ: API5 «{MateTypeName(type5)}» ({(int)type5}), API7 " +
                  $"«{MateTypeName(type7.Value)}» ({type7.Value})");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("тип сопряжения со стороны API7 не прочитан");
        }

        // Base objects: the signal is informative only if at least one object is present somewhere —
        // "both mates without base objects" is an absence of information, not a confirmation.
        if (base1Api5 || base2Api5 || base1Api7 || base2Api7)
        {
            var equal = base1Api5 == base1Api7 && base2Api5 == base2Api7;
            signals.Add(equal
                ? $"базовые объекты совпали по наличию (1: {base1Api5}, 2: {base2Api5})"
                : $"базовые объекты РАСХОДЯТСЯ: API5 (1: {base1Api5}, 2: {base2Api5}), " +
                  $"API7 (1: {base1Api7}, 2: {base2Api7})");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("базовые объекты не читаются ни с одной стороны — признак не даёт информации");
        }

        detail = string.Join("; ", signals);
        if (contradicted)
        {
            return false;
        }

        return confirmed ? true : null;
    }

    /// <summary>Refusal on an unconfirmed mate identity: a mismatch and "nothing to compare" forbid the
    /// mutation equally. Shared point for <c>set_mate_parameter</c> and <c>set_mate_fixed</c>.</summary>
    private KompasContractException MateIdentityRefusal(
        DocumentEntry document, string mateRef, MatePayload payload, bool? identity, string detail) =>
        new(
            ErrorCodes.StaleReference,
            identity == false
                ? $"Сопряжение по номеру {payload.Ordinal} — НЕ то, на которое указывает ссылка: {detail}. "
                  + "Параметр был бы записан в ЧУЖОЕ сопряжение, поэтому мутация не выполняется. "
                  + "Перечитайте сопряжения kompas_list_mates и возьмите свежую ссылку."
                : $"Тождество сопряжения по номеру {payload.Ordinal} НЕ подтверждено: {detail}. "
                  + "Мутация по неподтверждённому адресу не выполняется — перечитайте сопряжения "
                  + "kompas_list_mates и возьмите свежую ссылку.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["mate_ref"] = mateRef,
                ["ordinal"] = payload.Ordinal,
                ["identity_check"] = detail,
                ["identity_confirmed"] = identity is null ? "не сверено" : "расхождение",
                ["document_id"] = document.Id,
            });

    /// <summary>
    /// The mate count from the documented API5 collection
    /// <c>ksDocument3D.MateConstraintCollection</c>. An unread count is a refusal, not zero and not the
    /// previous value.
    /// </summary>
    private static int MateConstraintCount(DocumentEntry document, string stage)
    {
        var count = SafeInt(() => (document.Document.MateConstraintCollection() as ksMateConstraintCollection)!.GetCount());
        if (count is not null)
        {
            return count.Value;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Число сопряжений {stage} не прочитано (ksMateConstraintCollection.GetCount): подтвердить " +
            "удаление нечем.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    /// <summary>The mate count, READ. An unread count is a refusal, not zero: creation or deletion cannot be
    /// confirmed "by an unread counter".</summary>
    private static int RequireMateCount(DocumentEntry document, IMateConstraints3D mates, string stage)
    {
        var count = SafeInt(() => mates.Count);
        if (count is not null)
        {
            return count.Value;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Число сопряжений {stage} не прочитано (IMateConstraints3D.Count): подтвердить операцию " +
            "нечем. Подменять непрочитанное число нулём или прежним значением здесь запрещено.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    /// <summary>A mate row signature for comparing two collection snapshots.</summary>
    private static string MateSignature(MateRowDto row) => string.Join('|',
        row.ConstraintType ?? "?",
        row.Fixed ?? "?",
        row.ParamValue?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.Alignment ?? "?",
        row.Direction?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.BaseObject1 ?? "?",
        row.BaseObject2 ?? "?",
        row.Valid?.ToString() ?? "?");

    /// <summary>Pick the mate that was absent from the <paramref name="before"/> snapshot.</summary>
    /// <remarks>Comparison is over the MULTISET of signatures: two identical mates before creation give
    /// two occurrences, and a third after gives exactly one new one.</remarks>
    private static MateRowDto? FindNewMate(List<MateRowDto> before, List<MateRowDto> after, out string note)
    {
        var remaining = new List<string>(before.Select(MateSignature));
        MateRowDto? candidate = null;
        var addedCount = 0;

        foreach (var row in after)
        {
            var index = remaining.IndexOf(MateSignature(row));
            if (index >= 0)
            {
                remaining.RemoveAt(index);
                continue;
            }

            addedCount++;
            candidate = row;
        }

        if (addedCount == 1 && candidate is not null)
        {
            note = $"новое сопряжение выделено разностью множеств: номер {candidate.Ordinal}";
            return candidate;
        }

        note = addedCount == 0
            ? "новое сопряжение НЕ выделено: перечитанная коллекция не отличается от снимка до создания"
            : $"новое сопряжение НЕ выделено однозначно: разность дала {addedCount} строк";
        return null;
    }

    private ksMateConstraint? MateAt(DocumentEntry document, int ordinal)
    {
        try
        {
            return document.Document.MateConstraintCollection() is ksMateConstraintCollection mates
                && ordinal >= 0 && ordinal < mates.GetCount()
                && mates.GetByIndex(ordinal) is ksMateConstraint mate
                    ? mate
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private MatePayload RequireMate(DocumentEntry document, string mateRef)
    {
        var stored = References.Require(mateRef, document.Id, document.Revision);
        if (stored.Payload is not MatePayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{mateRef}» не адресует сопряжение: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        return payload;
    }

    /// <summary>Read all assembly mates via the documented <c>MateConstraintCollection</c>.</summary>
    private List<MateRowDto> ReadMates(DocumentEntry document, List<string> notes)
    {
        var rows = new List<MateRowDto>();
        ksMateConstraintCollection? mates;
        try
        {
            mates = document.Document.MateConstraintCollection() as ksMateConstraintCollection;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("mate_collection_unavailable — MateConstraintCollection не получена");
            return rows;
        }

        if (mates is null)
        {
            notes.Add("mate_collection_unavailable — MateConstraintCollection не ksMateConstraintCollection");
            return rows;
        }

        // AN UNREAD COUNT IS A READ FAILURE, NOT "NO MATES": `?? 0` would give an empty list, making
        // "no mates" indistinguishable from "the collection did not answer".
        var count = SafeInt(() => mates.GetCount());
        if (count is null)
        {
            notes.Add("mate_count_unread — число сопряжений не прочитано: перечень пуст, но это НЕ " +
                      "означает «сопряжений нет»");
            return rows;
        }

        for (var index = 0; index < count.Value; index++)
        {
            var mate = Ref(() => mates.GetByIndex(index)) as ksMateConstraint;
            if (mate is null)
            {
                notes.Add($"mate_{index}_not_ksMateConstraint");
                continue;
            }

            var typeValue = SafeInt(() => mate.constraintType) ?? -1;
            var stored = References.Register(
                MateRefKind, document.Id, document.Revision,
                new MatePayload(mate, index, (short)typeValue));

            // THE API7↔API5 ORDER CORRESPONDENCE IS NOT ASSUMED ON READ EITHER: `Valid`, `Alignment` and
            // `Name` are taken from the API7 object at the same ordinal, and on a type mismatch they
            // describe a FOREIGN mate — the row says so. The read is not a refusal: a mutation via this
            // reference will be rejected by the check in SetMateParameter/SetMateFixed.
            var mate7 = Mate7(document, index);
            var type7 = mate7 is null ? null : Int(() => (int)mate7.ConstraintType);
            if (typeValue >= 0 && type7 is not null && (short)type7.Value != (short)typeValue)
            {
                notes.Add($"mate_{index}_identity_mismatch — тип API7 «{MateTypeName(type7.Value)}» не " +
                          $"совпал с типом API5 «{MateTypeName(typeValue)}»: Valid/Alignment/Name " +
                          "прочитаны у объекта API7 по тому же номеру, и их принадлежность этой строке " +
                          "НЕ подтверждена");
            }

            rows.Add(new MateRowDto
            {
                MateRef = stored.Id,
                Ordinal = index,
                ConstraintType = MateTypeName(typeValue),
                // `fixed` is a C# keyword, so the property is taken by its escaped name `@fixed`.
                // DOC: ksmateconstraint_fixed.html — «fixed — признак фиксации».
                Fixed = FixedName(SafeInt(() => mate.@fixed)),
                ParamValue = SafeDouble(() => mate.distance),
                Direction = SafeInt(() => mate.direction),
                Alignment = AlignmentName(EnumOrNull(() => mate7?.Alignment)),
                Valid = Bool(() => mate7?.Valid),
                BaseObject1 = ObjectTypeName(Ref(() => mate.GetBaseObj(1))),
                BaseObject2 = ObjectTypeName(Ref(() => mate.GetBaseObj(2))),
                Name = Ref(() => mate7?.Name),
            });
        }

        return rows;
    }

    /// <summary>
    /// A component face as <c>IModelObject</c>: the documented
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>, then transfer to API7.
    /// </summary>
    private IModelObject FaceObject7(
        DocumentEntry document, string componentRef, int faceIndex, List<string> notes)
    {
        var stored = References.Require(componentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{componentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5At(document, payload.Ordinal);
        if (part5 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Компонент по порядковому номеру {payload.Ordinal} не получен как ksPart: грань " +
                "адресовать нечем.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED HERE TOO, and it also refuses on "not compared": the face comes from the
        // component found by ordinal, so a foreign ordinal would mate FOREIGN faces. The decision is the
        // same PURE ComponentIdentity — the SOURCE decides; name and matrix are notes (see IdentityMatches).
        var identity = IdentityMatches(part5, payload, out var identityNote);
        notes.Add("component_identity — " + identityNote);
        if (identity != true)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                "Грань адресована по номеру, тождество которого НЕ подтверждено: " + identityNote +
                ". Сопряжение по неподтверждённому адресу не создаётся; перечитайте структуру " +
                "kompas_list_components и возьмите свежую ссылку.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["ordinal"] = payload.Ordinal,
                    ["identity_confirmed"] = identity is null ? "не сверено (источник не прочитан)" : "расхождение источника",
                });
        }

        ksFaceCollection? faces;
        try
        {
            faces = part5.BodyCollection() is ksBodyCollection bodies && bodies.GetCount() > 0
                && bodies.GetByIndex(0) is ksBody body
                    ? body.FaceCollection() as ksFaceCollection
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            faces = null;
        }

        if (faces is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"У компонента {payload.Ordinal} не прочитано тело или грани " +
                "(ksPart.BodyCollection → ksBody.FaceCollection): грань адресовать нечем. Это тот же " +
                "класс, что «пустой компонент» на маршруте CreatePartInAssembly.",
                RetryPolicy.ReacquireContext);
        }

        var count = SafeInt(() => faces.GetCount()) ?? 0;
        if (faceIndex < 0 || faceIndex >= count)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Номер грани {faceIndex} вне диапазона: у компонента {count} грань(ей).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["face_index"] = faceIndex,
                    ["face_count"] = count,
                });
        }

        var face = faces.GetByIndex(faceIndex);
        var bridge = BridgeFor(document);
        if (bridge.TransferTo7(face) is not IModelObject model)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Грань {faceIndex} компонента не переносится в API7 как IModelObject: " +
                "сопряжение этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        notes.Add($"грань компонента {payload.Ordinal}[{faceIndex}] перенесена в API7");
        return model;
    }

    private static MateConstraintType MateTypeFromName(string name) => name switch
    {
        "coincidence" => MateConstraintType.mc_Coincidence,
        "parallel" => MateConstraintType.mc_Parallel,
        "perpendicular" => MateConstraintType.mc_Perpendicular,
        "tangency" => MateConstraintType.mc_Tangency,
        "concentric" => MateConstraintType.mc_Concentric,
        "distance" => MateConstraintType.mc_Distance,
        "angle" => MateConstraintType.mc_Angle,
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный тип сопряжения «{name}». Допустимы: coincidence, parallel, perpendicular, " +
            "tangency, concentric, distance, angle.",
            RetryPolicy.Never),
    };

    /// <summary>Mate type name by the documented <c>MateConstraintType</c>; an unknown value is named by number,
    /// not replaced by the nearest known one.</summary>
    private static string MateTypeName(int value) => value switch
    {
        -1 => "unknown",
        0 => "coincidence",
        1 => "parallel",
        2 => "perpendicular",
        3 => "tangency",
        4 => "concentric",
        5 => "distance",
        6 => "angle",
        7 => "in_place",
        9 => "transmission",
        10 => "cam_gear",
        11 => "symmetric",
        14 => "dependent",
        _ => $"unknown_{value}",
    };

    /// <summary>Fixing flag by name. DOC: <c>ksmatefixedtypeenum.html</c> — «ksMFixedUnknown = 0, Неопределено;
    /// ksMFixedPart1 = 1; ksMFixedPart2 = 2»; DOC: <c>mateconstraintfixed.html</c> — «0 нет фиксации,
    /// 1 фиксировать деталь 1, 2 фиксировать деталь 2». The sources diverge: value 0 is «Неопределено» in
    /// the API5 enum but "no fixing" in the API7 constants; the public name <c>none</c> follows the SECOND.</summary>
    private static ksMateFixedTypeEnum FixedFromName(string name) => name switch
    {
        "none" => ksMateFixedTypeEnum.ksMFixedUnknown,
        "first" => ksMateFixedTypeEnum.ksMFixedPart1,
        "second" => ksMateFixedTypeEnum.ksMFixedPart2,
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный признак фиксации «{name}». Допустимы: none, first, second.",
            RetryPolicy.Never),
    };

    private static string? FixedName(ksMateFixedTypeEnum? value) => value switch
    {
        ksMateFixedTypeEnum.ksMFixedUnknown => "none",
        ksMateFixedTypeEnum.ksMFixedPart1 => "first",
        ksMateFixedTypeEnum.ksMFixedPart2 => "second",
        null => null,
        _ => $"unknown_{(int)value.Value}",
    };

    /// <summary>
    /// Name by the RAW numeric value (field <c>ksMateConstraint.fixed</c>). Same numbering as
    /// <see cref="FixedFromName"/>: 0 is "no fixing" per <c>mateconstraintfixed.html</c>.
    /// </summary>
    private static string? FixedName(int? value) => value switch
    {
        null => null,
        0 => "none",
        1 => "first",
        2 => "second",
        _ => $"unknown_{value}",
    };

    private static ksMateConstraintAlignmentEnum? AlignmentFromName(string? name) => name switch
    {
        null => null,
        "opposite" => ksMateConstraintAlignmentEnum.ksMCAlignmentOpposite,
        "cooriented" => ksMateConstraintAlignmentEnum.ksMCAlignmentCooriented,
        "closest" => ksMateConstraintAlignmentEnum.ksMCAlignmentClosest,
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный вариант выравнивания «{name}». Допустимы: opposite, cooriented, closest.",
            RetryPolicy.Never),
    };

    private static string? AlignmentName(ksMateConstraintAlignmentEnum? value) => value switch
    {
        null => null,
        ksMateConstraintAlignmentEnum.ksMCAlignmentOpposite => "opposite",
        ksMateConstraintAlignmentEnum.ksMCAlignmentCooriented => "cooriented",
        ksMateConstraintAlignmentEnum.ksMCAlignmentClosest => "closest",
        ksMateConstraintAlignmentEnum.ksMCAlignmentUnknown => "unknown",
        _ => $"enum_{(int)value.Value}",
    };

    /// <summary>Base-object type name — as KOMPAS itself calls it, no guessing.</summary>
    private static string? ObjectTypeName(object? value)
        => value is null ? null : value.GetType().Name;
}
