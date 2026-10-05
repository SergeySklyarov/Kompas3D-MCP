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
        var before = SafeInt(() => mates7.Count) ?? 0;

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
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Создание сопряжения прервалось: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        bool? updated;
        try
        {
            updated = mate.Update();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"IMateConstraint3D.Update() бросил: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        var valid = Safe(() => mate.Valid);
        var after = SafeInt(() => mates7.Count) ?? before;

        // ПОДТВЕРЖДЕНИЕ — это Valid, а не «Update()=true»: измерено 05.10.2026, что сопряжение с
        // двумя объектами ОДНОГО компонента тоже дало Update()=true, но Valid=false.
        if (updated != true || valid != true)
        {
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
        var created = rows.LastOrDefault();
        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Сопряжение создано (сопряжений " + before + " → " + after + "), но перечитать его " +
                "в коллекции не удалось: адресовать сопряжение нечем.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
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

        var wanted = FixedFromName(command.Fixed);
        var before = Safe(() => mate.Fixed);
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
        var after = Safe(() => mate.Fixed);
        BumpRevision(document, "mate.set_fixed");

        var matched = after == wanted;
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
                    "fixed_not_read_back — перечитанный признак не совпал с заданным",
                }));
    }

    // ===================================================================================== MATE-05
    /// <summary>Удалить сопряжение.</summary>
    public DeleteMateResult DeleteMate(DeleteMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var before = SafeInt(() => (document.Document.MateConstraintCollection() as ksMateConstraintCollection)!.GetCount()) ?? 0;

        // Удаление — документированный ksDocument3D.RemoveMateConstraint(constraintType, obj1, obj2)
        // (ksdocument3d_removemateconstraint.html). Объекты берутся у самого сопряжения тем же
        // документированным GetBaseObj(1|2) — «первый попавшийся» здесь был бы подменой адреса.
        var first = Safe(() => payload.Mate5.GetBaseObj(1));
        var second = Safe(() => payload.Mate5.GetBaseObj(2));
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
        var after = SafeInt(() => (document.Document.MateConstraintCollection() as ksMateConstraintCollection)!.GetCount()) ?? before;

        if (!removed || after >= before)
        {
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

        var count = SafeInt(() => mates.GetCount()) ?? 0;
        for (var index = 0; index < count; index++)
        {
            var mate = Safe(() => mates.GetByIndex(index)) as ksMateConstraint;
            if (mate is null)
            {
                notes.Add($"mate_{index}_not_ksMateConstraint");
                continue;
            }

            var typeValue = SafeInt(() => mate.constraintType) ?? -1;
            var stored = References.Register(
                MateRefKind, document.Id, document.Revision,
                new MatePayload(mate, index, (short)typeValue));

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
                Alignment = AlignmentName(Safe(() => Mate7(document, index)?.Alignment)),
                Valid = Safe(() => Mate7(document, index)?.Valid),
                BaseObject1 = ObjectTypeName(Safe(() => mate.GetBaseObj(1))),
                BaseObject2 = ObjectTypeName(Safe(() => mate.GetBaseObj(2))),
                Name = Safe(() => Mate7(document, index)?.Name),
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
