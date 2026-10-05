using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Домен сопряжений — блок C2, профиль <c>mates-minimal-v1</c>, режимы <c>MATE-01…MATE-06</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — решение заказчика 05.10.2026: документированный API7-путь.</b>
/// <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c> →
/// <c>BaseObject1</c>/<c>BaseObject2</c> → <c>Update()</c>. Подтверждено живым прогоном
/// (проба M, <c>tools/KompasMcp.Api7Probe --mate</c>): <c>Update()=True</c>, <c>Valid=True</c>,
/// сопряжений 0 → 1.
/// </para>
/// <para>
/// <b><c>ksDocument3D.AddMateConstraint</c> здесь НЕ применяется.</b> Он документирован как метод
/// ПОСТОЯННОГО сопряжения (<c>ksdocument3d_addmateconstraint.html</c>), но на гранях, полученных
/// документированным путём, вернул <c>False</c> при ВСЕХ документированных сочетаниях
/// (<c>direction</c> 0/−1/1, <c>fixed</c> 0/1, <c>mc_Coincidence</c>/<c>mc_Distance</c>/
/// <c>mc_Parallel</c>). Причина не установлена; вопрос закрыт решением заказчика, а не выводом
/// «метод не работает».
/// </para>
/// <para>
/// <b>Грань компонента.</b> Документированный <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>
/// (<c>kspart_bodycollection.html</c>), затем перенос в API7 как <c>IModelObject</c>. Компонент
/// адресуется порядковым номером (измерено в блоке C1).
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    private const string MateRefKind = "mate";

    /// <summary>Полезная нагрузка ссылки на сопряжение: API5-объект, номер и тип.</summary>
    private sealed record MatePayload(ksMateConstraint Mate5, int Ordinal, short ConstraintType);

    // ===================================================================================== MATE-02
    /// <summary>Перечисление сопряжений сборки.</summary>
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

    // ===================================================================================== MATE-01
    /// <summary>Создать сопряжение между гранями двух компонентов.</summary>
    public CreateMateResult CreateMate(CreateMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var type = MateTypeFromName(command.ConstraintType);
        var notes = new List<string>();

        var first = FaceObject7(document, command.FirstComponentRef, command.FirstFaceIndex, notes);
        var second = FaceObject7(document, command.SecondComponentRef, command.SecondFaceIndex, notes);

        var mates7 = RequireMateConstraints(document);

        // СЧЁТЧИК НЕ ПОДМЕНЯЕТСЯ НУЛЁМ. `?? 0` означало «сопряжений нет» там, где число просто не
        // прочиталось; `?? before` — «число не изменилось» там же. Оба подменяли НЕИЗВЕСТНОЕ
        // ИЗВЕСТНЫМ (дефект L9 ревью 05.10.2026): ложное «сопряжение не создано» и ложный отказ
        // удаления, которое фактически прошло.
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
            // Объект сопряжения УЖЕ создан вызовом Add: частичный эффект обязан поднять ревизию и
            // отозвать ссылки, иначе модель изменилась, а ревизия и ссылки прежние (дефект M4).
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

        // ПОДТВЕРЖДЕНИЕ — это Valid, а не «Update()=true»: измерено 05.10.2026, что сопряжение с
        // двумя объектами ОДНОГО компонента тоже дало Update()=true, но Valid=false.
        if (updated != true || valid != true)
        {
            // Недействительное сопряжение УЖЕ лежит в сборке: модель изменилась, и ревизия обязана
            // это показать (дефект M4 ревью 05.10.2026).
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

        // НОВОЕ СОПРЯЖЕНИЕ ИЩЕТСЯ РАЗНОСТЬЮ МНОЖЕСТВ, А НЕ «ПОСЛЕДНЕЙ СТРОКОЙ».
        //
        // `rows.LastOrDefault()` молча предполагает, что новое сопряжение добавлено в конец и что
        // порядок коллекции совпадает с порядком API7-индексатора — то же непроверенное
        // соответствие порядков, что и у компонентов (дефект M7 ревью 05.10.2026). Разность
        // считается по МУЛЬТИМНОЖЕСТВУ подписей: два одинаковых сопряжения до создания дают два
        // вхождения, и третье после — ровно одно новое.
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
            // Число сопряжений не выросло, а результат уже выдан как успешный: это тот же дефект,
            // что «успех при проваленной проверке» (M5 ревью 05.10.2026).
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

    // ===================================================================================== MATE-03
    /// <summary>Изменить параметр сопряжения (расстояние или угол).</summary>
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

        // ТОЖДЕСТВО СВЕРЯЕТСЯ ДО МУТАЦИИ: номер из API5-коллекции применяется к API7-индексатору, и
        // расхождение или нечитаемость означали бы запись параметра в ЧУЖОЕ сопряжение (дефект M7).
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

        // ПРОВАЛ ОБЯЗАТЕЛЬНОЙ ПРОВЕРКИ — ЭТО НЕ УСПЕХ: перечитанный параметр не совпал с заданным
        // (или не прочитан), а результат «выполнено» был бы ложью (дефект M5 ревью 05.10.2026).
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

    // ===================================================================================== MATE-04
    /// <summary>Задать признак фиксации компонентов сопряжением.</summary>
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

        // ТОЖДЕСТВО СВЕРЯЕТСЯ ДО МУТАЦИИ — тем же правилом, что у параметра: признак фиксации,
        // записанный в ЧУЖОЕ сопряжение, — это изменение не той модели (дефект M7 ревью 05.10.2026).
        var identity = MateIdentityMatches(payload, mate, out var identityNote);
        if (identity != true)
        {
            throw MateIdentityRefusal(document, command.MateRef, payload, identity, identityNote);
        }

        var wanted = FixedFromName(command.Fixed);
        // Чтение признака фиксации — ЯВНОЕ: непрочитанное значение не подменяется первым значением
        // перечисления (прежний `Safe` давал `default(ksMateFixedTypeEnum)` = `ksMFixedUnknown`, то
        // есть «снятие фиксации» там, где COM-чтение не состоялось).
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

        // Совпадение подтверждается ТОЛЬКО прочитанным значением: `afterRead.Ok == false` — это «не
        // прочитано», а не «совпало».
        var matched = afterRead.Ok && afterRead.Value == wanted;
        var before = beforeRead.Ok ? beforeRead.Value : (ksMateFixedTypeEnum?)null;
        var after = afterRead.Ok ? afterRead.Value : (ksMateFixedTypeEnum?)null;

        // ПРОВАЛ ОБЯЗАТЕЛЬНОЙ ПРОВЕРКИ — ЭТО НЕ УСПЕХ (дефект M5 ревью 05.10.2026): признак
        // фиксации записан, но перечитанное значение не совпало с заданным или не прочитано.
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

    // ===================================================================================== MATE-05
    /// <summary>Удалить сопряжение.</summary>
    public DeleteMateResult DeleteMate(DeleteMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var before = MateConstraintCount(document, "до удаления");

        // Удаление — документированный ksDocument3D.RemoveMateConstraint(constraintType, obj1, obj2)
        // (ksdocument3d_removemateconstraint.html). Объекты берутся у самого сопряжения тем же
        // документированным GetBaseObj(1|2) — «первый попавшийся» здесь был бы подменой адреса.
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
            // Удаление, которое КОМПАС подтвердил, а перечитанное число — нет, означает: модель
            // изменилась, а ревизия и ссылки об этом не знают (дефект M4 ревью 05.10.2026).
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

        // DTO собирается ДО отзыва ссылок: `InvalidateDocument` снимает ВСЕ ссылки документа, и
        // вызов `Require` после него отверг бы ту самую ссылку, которой удаляли. Это был дефект
        // первой редакции: он выглядел как «ссылка не найдена» и уводил поиск в чужую сторону.
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

    // ==================================================================================== помощники

    /// <summary>Сопряжения сборки как <c>IMateConstraints3D</c> (API7).</summary>
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

    /// <summary>Сопряжение по порядковому номеру как <c>IMateConstraint3D</c> (API7).</summary>
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

    /// <summary>
    /// То ли это сопряжение: тип и базовые объекты API7 сверяются с API5-коллекцией по тому же номеру.
    /// </summary>
    /// <returns>
    /// <c>true</c> — тождество подтверждено; <c>false</c> — расхождение (номер ведёт в другое
    /// сопряжение); <c>null</c> — сверить нечем.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Зачем.</b> <c>SetMateParameter</c> и <c>SetMateFixed</c> брали API7-сопряжение
    /// <c>IMateConstraints3D.MateConstraint3D[ordinal]</c> по номеру из API5-коллекции
    /// <c>ksDocument3D.MateConstraintCollection</c> без всякой сверки: соответствие порядков API7↔API5
    /// — ПРЕДПОЛОЖЕНИЕ того же рода, что и у компонентов. Если оно нарушено, параметр записывался бы в
    /// ЧУЖОЕ сопряжение, а <c>ReadMates</c> читал бы у чужого объекта <c>Valid</c>/<c>Alignment</c>/
    /// <c>Name</c> (дефект M7 ревью 05.10.2026).
    /// </para>
    /// <para>
    /// <b>Что сверяется и по каким источникам.</b> Тип: API5 <c>ksMateConstraint.constraintType</c>
    /// против API7 <c>IMateConstraint3D.ConstraintType</c>. Базовые объекты: API5
    /// <c>ksMateConstraint.GetBaseObj(1|2)</c> против API7 <c>BaseObject1</c>/<c>BaseObject2</c> — по
    /// НАЛИЧИЮ. Побитовое сравнение самих объектов не делается: перенос API5-объекта в API7 даёт
    /// обёртку, тождество которой COM-объекту сопряжения живьём не измерялось, и ложное расхождение
    /// заблокировало бы верную работу. Это названо, а не выдано за полную сверку.
    /// </para>
    /// </remarks>
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

        // Базовые объекты: признак информативен, только если хоть один объект где-то присутствует.
        // «Оба сопряжения без базовых объектов» — это не подтверждение тождества, а отсутствие
        // информации, и выдавать его за совпадение запрещено.
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

    /// <summary>
    /// Отказ на неподтверждённом тождестве сопряжения: расхождение и «сверить нечем» запрещают мутацию
    /// одинаково. Общая точка для <c>set_mate_parameter</c> и <c>set_mate_fixed</c>.
    /// </summary>
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
    /// Число сопряжений по документированной API5-коллекции <c>ksDocument3D.MateConstraintCollection</c>.
    /// Непрочитанное число — отказ, а не ноль и не прежнее значение.
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

    /// <summary>
    /// Число сопряжений, ПРОЧИТАННОЕ. Непрочитанное число — отказ, а не ноль: подтвердить создание
    /// или удаление «по непрочитанному счётчику» нельзя.
    /// </summary>
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

    /// <summary>
    /// Подпись строки сопряжения для сравнения двух снимков коллекции.
    /// </summary>
    private static string MateSignature(MateRowDto row) => string.Join('|',
        row.ConstraintType ?? "?",
        row.Fixed ?? "?",
        row.ParamValue?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.Alignment ?? "?",
        row.Direction?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.BaseObject1 ?? "?",
        row.BaseObject2 ?? "?",
        row.Valid?.ToString() ?? "?");

    /// <summary>
    /// Выделить сопряжение, которого не было в снимке <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// Сравнение идёт по МУЛЬТИМНОЖЕСТВУ подписей: два одинаковых сопряжения до создания дают два
    /// вхождения, и третье после — ровно одно новое. «Последняя строка» такого не различает.
    /// </remarks>
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

    /// <summary>Чтение всех сопряжений сборки документированным <c>MateConstraintCollection</c>.</summary>
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

        // НЕПРОЧИТАННОЕ ЧИСЛО — ЭТО ОТКАЗ ЧТЕНИЯ, А НЕ «СОПРЯЖЕНИЙ НЕТ». Прежде `?? 0` давал
        // пустой перечень, и «сопряжений нет» было неотличимо от «коллекция не ответила»
        // (дефект L9 ревью 05.10.2026).
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

            // СООТВЕТСТВИЕ ПОРЯДКОВ API7↔API5 НА ЧТЕНИИ ТОЖЕ НЕ ПОДРАЗУМЕВАЕТСЯ. `Valid`, `Alignment`
            // и `Name` берутся у ОБЪЕКТА API7 по тому же номеру, и если тип API7 не совпал с типом
            // API5, эти поля описывают ЧУЖОЕ сопряжение — строка об этом говорит, а не молчит
            // (дефект M7 ревью 05.10.2026). Чтение отказом не является: строка честно называет
            // непрочитанное, а мутация по этой ссылке будет отвергнута сверкой в
            // SetMateParameter/SetMateFixed.
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
                // `fixed` — ключевое слово C#, поэтому свойство берётся экранированным именем
                // `@fixed` (ksmateconstraint_fixed.html: «fixed — признак фиксации»).
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
    /// Грань компонента как <c>IModelObject</c>: документированный
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c> и перенос в API7.
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

        // ТОЖДЕСТВО ПРОВЕРЯЕТСЯ И ЗДЕСЬ, И ТОЖЕ ОТКАЗЫВАЕТ НА «НЕ СВЕРЕНО»: грань берётся у компонента,
        // найденного по номеру, и если номер ведёт в чужой экземпляр, сопряжение было бы создано между
        // ЧУЖИМИ гранями. Нечитаемость (identity == null) — это «мутация по неподтверждённому адресу»,
        // и она запрещена так же, как расхождение (дефект M7 ревью 05.10.2026).
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
                    ["identity_confirmed"] = identity is null ? "не сверено" : "расхождение",
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

    /// <summary>
    /// Имя типа сопряжения по документированному <c>MateConstraintType</c>; неизвестное значение
    /// называется числом, а не подменяется ближайшим известным.
    /// </summary>
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

    /// <summary>
    /// Признак фиксации по имени. Отображение опирается на ДВА документированных источника, и их
    /// расхождение названо, а не сглажено: <c>ksmatefixedtypeenum.html</c> — «ksMFixedUnknown = 0,
    /// Неопределено; ksMFixedPart1 = 1; ksMFixedPart2 = 2», <c>mateconstraintfixed.html</c> —
    /// «0 нет фиксации, 1 фиксировать деталь 1, 2 фиксировать деталь 2». То есть значение 0 в
    /// перечислении API5 названо «Неопределено», а в константах API7 — «нет фиксации»; публичное имя
    /// <c>none</c> следует ВТОРОМУ источнику, потому что оно описывает смысл, а не имя константы.
    /// </summary>
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
    /// Имя по СЫРОМУ числовому значению (поле <c>ksMateConstraint.fixed</c>). Та же нумерация, что и
    /// у <see cref="FixedFromName"/>: 0 — «нет фиксации» по <c>mateconstraintfixed.html</c>.
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

    /// <summary>Имя типа базового объекта — как его называет сам КОМПАС, без догадок.</summary>
    private static string? ObjectTypeName(object? value)
        => value is null ? null : value.GetType().Name;
}
