using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasAPI7;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Сущности эскиза как объекты с УСТОЙЧИВЫМ АДРЕСОМ: перечисление, адресное чтение и адресная
/// правка (<c>kompas_list_sketch_entities</c>, <c>kompas_edit_sketch_entity</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Что здесь измеряется, а не утверждается.</b> Действия <c>discover</c>, <c>read</c> и
/// <c>edit</c> зависимости <c>dep.sketch.entities</c> до этого наряда стояли отказами, и причина
/// была названа прямо: перечисления сущностей эскиза в продукте нет, адреса, по которому читать
/// одну сущность, тоже нет, а схема правки эскиза принимает режим и НОВЫЙ НАБОР примитивов
/// целиком — то есть пересоздаёт контур, а не правит сущность.
/// </para>
/// <para>
/// <b>Адрес.</b> Это строка, которую выдаёт <c>IKompasDocument1.GetObjectId</c> и принимает обратно
/// <c>IKompasDocument1.FindObjectById</c>. Индекс коллекции адресом не объявляется: перестроение
/// его сдвигает, и «N-й объект» перестал бы указывать на ту же сущность после первой же мутации.
/// </para>
/// <para>
/// <b>Правка подтверждается ПОВТОРНЫМ РАЗРЕШЕНИЕМ АДРЕСА</b>, а не кодом возврата: у
/// <c>delete</c> подтверждением служит то, что адрес больше НЕ разрешается, у <c>set_layer</c> —
/// прочитанный номер слоя. Код «не отказал» применением не объявляется.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Перечислить сущности существующего эскиза; при заданном адресе — прочитать одну.</summary>
    public SketchEntitiesResult ListSketchEntities(ListSketchEntitiesCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        var limit = command.Limit ?? 500;
        if (limit <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"limit обязан быть положительным, получено {limit}.",
                details: new Dictionary<string, object?> { ["limit"] = limit });
        }

        var (read, failure) = Api7SketchEntities.Read(bridge, target.Sketch, limit);
        if (read is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сущности эскиза не перечислены: " + (failure ?? "причина не названа") +
                ". Пустой список и «не прочитано» — разные состояния, поэтому отказ назван, а не " +
                "выдан за ноль сущностей.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["failure"] = failure });
        }

        var rows = read.Rows.ToList();
        var notes = read.Notes.ToList();

        if (command.Address is { Length: > 0 } wanted)
        {
            var found = rows
                .Where(r => string.Equals(r.Address, wanted, StringComparison.Ordinal))
                .ToList();

            if (found.Count == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.DocumentNotFound,
                    $"Сущности с адресом '{wanted}' в эскизе нет: адрес не разрешился перечислением " +
                    $"(перечислено сущностей {rows.Count}). Адрес выдаётся этим же перечислением, " +
                    "поэтому чужой адрес отвергается, а не подменяется похожим.",
                    details: new Dictionary<string, object?>
                    {
                        ["address"] = wanted,
                        ["enumerated"] = rows.Count,
                    });
            }

            if (found.Count > 1)
            {
                throw new KompasContractException(
                    ErrorCodes.AmbiguousSelection,
                    $"Адрес '{wanted}' встретился {found.Count} раза: адрес обязан различать сущности, " +
                    "и молчаливый выбор первой сделал бы его неоднозначным.",
                    details: new Dictionary<string, object?>
                    {
                        ["address"] = wanted,
                        ["matches"] = found.Count,
                    });
            }

            rows = found;
        }

        var noAddress = rows.Count(r => string.IsNullOrEmpty(r.Address));
        if (noAddress > 0)
        {
            notes.Add($"без адреса осталось сущностей: {noAddress} — они не адресуемы и правке по " +
                "адресу недоступны");
        }

        return new SketchEntitiesResult(
            rows.Select(r => new SketchEntityRowDto(
                r.Index, r.Address, r.Kind, r.Name, r.TypeCode, r.Notes)).ToList(),
            read.CollectionCounts,
            read.Route,
            notes);
    }

    /// <summary>
    /// Адресная правка одной существующей сущности эскиза.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему режим <c>delete</c> — не то же самое, что <c>delete_entities</c> чужой схемы.</b>
    /// <c>delete_entities</c> пересобирает контур целиком, а здесь удаляется РОВНО ОДНА сущность,
    /// названная адресом, и остальные остаются на своих местах — это и есть различающий признак
    /// адресности.
    /// </para>
    /// <para>
    /// <b>Вход в редактирование — на ЗАПИСЬ.</b> <c>BeginEdit()</c>, а не
    /// <c>BeginEditEx(true)</c>: правка обязана менять модель, и брать на это режим «только
    /// чтение» значило бы получить отказ, выглядящий как отсутствие возможности.
    /// </para>
    /// </remarks>
    public SketchEntityEditResult EditSketchEntity(EditSketchEntityCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        if (command.Address is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка сущности эскиза требует address: правка «первой попавшейся» сущности адресной " +
                "не является.",
                details: new Dictionary<string, object?> { ["missing_field"] = "address" });
        }

        if (command.Action is not ("set_layer" or "delete"))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Действие '{command.Action}' не объявлено. Объявлены: set_layer, delete.",
                details: new Dictionary<string, object?>
                {
                    ["action"] = command.Action,
                    ["supported"] = new[] { "set_layer", "delete" },
                });
        }

        if (command.Action == "set_layer" && command.LayerNumber is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для действия set_layer обязательно поле layer_number.",
                details: new Dictionary<string, object?> { ["missing_field"] = "layer_number" });
        }

        if (command.Action == "delete" && command.LayerNumber is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поле layer_number принадлежит действию set_layer и не принадлежит delete: " +
                "принятое и проигнорированное поле доживает до приёмки, выглядя как выполненная правка.",
                details: new Dictionary<string, object?>
                {
                    ["foreign_fields"] = new[] { "layer_number" },
                    ["allowed_fields"] = new[] { "sketch_ref", "expected_revision", "address", "action" },
                });
        }

        // Перечисление ДО правки: счётчик сущностей нужен как измеренная величина, а не как оценка.
        var (beforeRead, beforeFailure) = Api7SketchEntities.Read(bridge, target.Sketch, 500);
        if (beforeRead is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сущности эскиза не перечислены до правки: " + (beforeFailure ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["failure"] = beforeFailure });
        }

        var beforeRows = beforeRead.Rows
            .Where(r => string.Equals(r.Address, command.Address, StringComparison.Ordinal))
            .ToList();
        if (beforeRows.Count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Адрес '{command.Address}' разрешился в {beforeRows.Count} сущностей вместо одной: " +
                "правка по неоднозначному адресу не выполняется.",
                details: new Dictionary<string, object?>
                {
                    ["address"] = command.Address,
                    ["matches"] = beforeRows.Count,
                });
        }

        var notes = new List<string>(beforeRead.Notes)
        {
            $"Сущностей в эскизе до правки: {beforeRead.Rows.Count}.",
        };

        if (bridge.TransferTo7(target.Sketch) is not ISketch sketch7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Эскиз не переносится в API7 как ISketch: адресная правка идёт только этим маршрутом.",
                RetryPolicy.ReacquireContext);
        }

        // ПРЕЖНЯЯ РЕДАКЦИЯ БРАЛА ЗДЕСЬ `DocumentOf(sketch7)` — документ ДЕТАЛИ — и разрешала адрес
        // на нём. Это была неверная посылка: `FindObjectById` объявлен на `IKompasDocument1`, но
        // адрес принадлежит документу ФРАГМЕНТА эскиза, и документ детали по любому адресу отвечает
        // отказом. Измерено 21.09.2026 (scratch/_probe_dse_dpt.py, бинари
        // publish-deproutes-20260921-e): документ детали — «» на всех кандидатах, документ
        // фрагмента — непустой адрес. Поэтому документ-получатель берётся внутри сеанса правки,
        // ниже, из `BeginEdit()`.
        // РАЗРЕШЕНИЕ АДРЕСА ИДЁТ ВНУТРИ СЕАНСА ПРАВКИ. Адрес принадлежит документу ФРАГМЕНТА
        // эскиза, а не документу детали: измерено 21.09.2026 различающим замером по получателю и
        // родителю (scratch/_probe_dse_dpt.py, бинари publish-deproutes-20260921-e) — документ
        // детали отвечает пустой строкой на все четыре сущности, документ фрагмента выдаёт адрес.
        // Фрагмент существует только между BeginEdit() и EndEdit(), поэтому и разрешение адреса,
        // и подтверждение правки повторным разрешением живут внутри ApplySketchEntityEdit.
        var outcome = ApplySketchEntityEdit(sketch7, command, notes);
        if (outcome.ArgumentFailure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Объект по адресу '{command.Address}' не отвечает IDrawingObject: правке доступны " +
                "графические объекты эскиза, и другой объект ими не подменяется.",
                details: new Dictionary<string, object?>
                {
                    ["address"] = command.Address,
                    ["kind"] = beforeRows[0].Kind,
                    ["failure"] = outcome.ArgumentFailure,
                });
        }

        if (outcome.ResolveFailure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Адрес '{command.Address}' не разрешился в объект: {outcome.ResolveFailure}",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["address"] = command.Address });
        }

        if (outcome.EditFailure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Адресная правка сущности эскиза не применена: " + outcome.EditFailure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["failure"] = outcome.EditFailure });
        }

        var (model, _, containersFailure) = Api7AuxGeometry.Containers(bridge, target.Document.PartNow());
        if (model is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Контейнеры API7 недостижимы для перестроения: " + (containersFailure ?? "причина не названа"),
                RetryPolicy.ReacquireContext);
        }

        Api7Bridge.Rebuild(model, target.Document.Document);
        BumpRevision(target.Document, "sketch.entity_edit");

        // ПОДТВЕРЖДЕНИЕ — ПОВТОРНОЕ РАЗРЕШЕНИЕ ТОГО ЖЕ АДРЕСА, а не код возврата. Оно уже снято
        // ВНУТРИ сеанса правки (там же, где адрес разрешался), и здесь только переносится в отчёт.
        var resolvedAfter = outcome.ResolvedAfter;
        int? layerAfter = outcome.LayerAfter;
        string? kindAfter = outcome.KindAfter;

        var (afterRead, _) = Api7SketchEntities.Read(bridge, target.Sketch, 500);
        var countAfter = afterRead?.Rows.Count ?? -1;

        var applied = command.Action switch
        {
            "delete" => !resolvedAfter,
            "set_layer" => resolvedAfter && layerAfter == command.LayerNumber,
            _ => false,
        };

        notes.Add(command.Action == "delete"
            ? $"Подтверждение: адрес после удаления разрешается={resolvedAfter} (ожидание — не " +
              $"разрешается). {outcome.AfterResolveFailure ?? "причина не названа"}"
            : $"Подтверждение: номер слоя после правки прочитан={layerAfter} (запрошено " +
              $"{command.LayerNumber}); адрес разрешается={resolvedAfter}.");

        return new SketchEntityEditResult(
            Address: command.Address,
            Action: command.Action,
            Applied: applied,
            ResolvedAfter: resolvedAfter,
            LayerAfter: layerAfter,
            KindAfter: kindAfter,
            EntityCountBefore: beforeRead.Rows.Count,
            EntityCountAfter: countAfter,
            Route: Api7SketchEntities.Route,
            Notes: notes);
    }

    /// <summary>
    /// Итог сеанса правки: адрес разрешён и правка применена ВНУТРИ одного входа в эскиз.
    /// </summary>
    /// <remarks>
    /// Три поля отказа РАЗЛИЧАЮТСЯ, а не сливаются в одно: «объект по адресу не графический»
    /// (<c>INVALID_ARGUMENT</c>), «адрес не разрешился» (<c>STALE_REFERENCE</c>) и «правка не
    /// применилась» (<c>GEOMETRY_FAILED</c>) — разные состояния с разными кодами.
    /// </remarks>
    private sealed record SketchEntityEditOutcome(
        string? ResolveFailure,
        string? ArgumentFailure,
        string? EditFailure,
        bool ResolvedAfter,
        int? LayerAfter,
        string? KindAfter,
        string? AfterResolveFailure = null);

    /// <summary>
    /// Вход в редактирование, РАЗРЕШЕНИЕ АДРЕСА, правка, ПОДТВЕРЖДЕНИЕ и ВЫХОД — в одном сеансе.
    /// </summary>
    /// <remarks>
    /// Адрес принадлежит документу ФРАГМЕНТА эскиза, а фрагмент живёт только между
    /// <c>BeginEdit()</c> и <c>EndEdit()</c>. Поэтому и <c>FindObjectById</c>, и повторное
    /// разрешение адреса как подтверждение берутся ЗДЕСЬ, а не у вызывающего: вне сеанса документ
    /// детали на тот же адрес отвечает пустой строкой (измерено 21.09.2026,
    /// <c>scratch/_probe_dse_dpt.py</c>, бинари <c>publish-deproutes-20260921-e</c>).
    /// </remarks>
    private static SketchEntityEditOutcome ApplySketchEntityEdit(
        ISketch sketch, EditSketchEntityCommand command, List<string> notes)
    {
        FragmentDocument? fragment;
        try
        {
            // BeginEdit() — НА ЗАПИСЬ. BeginEditEx(true) открыл бы эскиз только для чтения, и
            // правка получила бы отказ, неотличимый от отсутствия возможности.
            fragment = sketch.BeginEdit();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return new($"ISketch.BeginEdit() отказал: HRESULT 0x{ex.HResult:X8}",
                null, null, false, null, null);
        }

        if (fragment is null)
        {
            return new("ISketch.BeginEdit() → null: вход в эскиз на запись не состоялся",
                null, null, false, null, null);
        }

        try
        {
            if (fragment as IKompasDocument1 is not { } fragmentDocument)
            {
                return new(
                    "Документ фрагмента не отвечает QI(IKompasDocument1): FindObjectById объявлен " +
                    "на нём, а у IFragmentDocument (44 члена, IID {E19CE626-DF9C-48C4-A83D-3E3BC7F0DACA}) " +
                    "его нет — адрес разрешить нечем",
                    null, null, false, null, null);
            }

            var (resolved, resolveFailure) = Api7SketchEntities.ResolveAddress(
                fragmentDocument, command.Address);
            if (resolved is null)
            {
                return new(resolveFailure ?? "адрес не разрешился", null, null, false, null, null);
            }

            if (resolved is not IDrawingObject drawing)
            {
                return new(null,
                    "объект по адресу не отвечает IDrawingObject",
                    null, false, null, null);
            }

            bool? updateReturned = null;
            switch (command.Action)
            {
                case "set_layer":
                    var wantedLayer = command.LayerNumber!.Value;
                    drawing.LayerNumber = wantedLayer;
                    // ВОЗВРАТ Update() ЗАПИСЫВАЕТСЯ, НО ПРИГОВОРОМ НЕ ЯВЛЯЕТСЯ. Правило проекта:
                    // «успешный код не равен применённой правке», и обратное тоже верно — неуспешный
                    // код не доказывает, что правка не применилась. Измерено 21.09.2026
                    // (scratch/_probe_dse_edit.py по бинарям publish-deproutes-20260921-f):
                    // IDrawingObject.LayerNumber = 7 принимается, а Update() возвращает не-true —
                    // прежняя редакция объявляла это отказом правки и получала GEOMETRY_FAILED при
                    // живом объекте. Признак применения берётся ЧТЕНИЕМ ОБРАТНО, ниже.
                    updateReturned = SafeUpdate(drawing);
                    notes.Add($"Действие: IDrawingObject.LayerNumber = {wantedLayer}; " +
                        $"IDrawingObject.Update() вернул " +
                        $"{(updateReturned is null ? "не прочитан" : updateReturned.ToString())} " +
                        "(признак применения — чтение номера слоя обратно, а не этот возврат).");
                    break;
                case "delete":
                    drawing.Delete();
                    notes.Add("Действие: IDrawingObject.Delete() — удалена РОВНО одна сущность, " +
                        "названная адресом.");
                    break;
                default:
                    return new(null, null,
                        $"действие '{command.Action}' объявлено, но обработчика не имеет",
                        false, null, null);
            }

            // ПОДТВЕРЖДЕНИЕ — ПОВТОРНОЕ РАЗРЕШЕНИЕ ТОГО ЖЕ АДРЕСА И ЧТЕНИЕ ЗНАЧЕНИЯ ОБРАТНО В ТОЙ ЖЕ
            // СЕССИИ, а не код возврата.
            var (afterObject, afterFailure) = Api7SketchEntities.ResolveAddress(
                fragmentDocument, command.Address);
            var resolvedAfter = afterObject is not null;
            int? layerAfter = null;
            string? kindAfter = null;
            if (afterObject is IDrawingObject afterDrawing)
            {
                layerAfter = ReadLayer(afterDrawing, notes);
                kindAfter = SafeToString(() => afterDrawing.DrawingObjectType.ToString());
            }

            switch (command.Action)
            {
                case "set_layer" when layerAfter != command.LayerNumber:
                    return new(null, null,
                        $"номер слоя после правки прочитан {layerAfter?.ToString() ?? "не прочитан"}, " +
                        $"запрошено {command.LayerNumber} (IDrawingObject.Update() вернул " +
                        $"{(updateReturned is null ? "не прочитан" : updateReturned.ToString())})",
                        resolvedAfter, layerAfter, kindAfter, afterFailure);
                case "delete" when resolvedAfter:
                    return new(null, null,
                        "адрес разрешается и после удаления: удаление не применилось",
                        resolvedAfter, layerAfter, kindAfter, afterFailure);
            }

            return new(null, null, null, resolvedAfter, layerAfter, kindAfter, afterFailure);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return new(null, null, $"правка сущности отказала: HRESULT 0x{ex.HResult:X8}",
                false, null, null);
        }
        catch (InvalidCastException ex)
        {
            return new(null, null, $"правка сущности бросила {ex.GetType().Name}",
                false, null, null);
        }
        finally
        {
            // Выход обязателен и в ветке отказа: открытый фрагмент держал бы эскиз в режиме правки,
            // и следующий вызов получил бы занятую модель.
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or InvalidCastException)
            {
                // Выход не удался — это причина для журнала, а не повод вернуть ложный успех.
            }
        }
    }

    /// <summary>
    /// Возврат <c>IDrawingObject.Update()</c> как ПОКАЗАНИЕ, а не как приговор: <c>null</c> — вызов
    /// бросил, <c>false</c> — вернул не-true. Решение о применении правки принимается чтением
    /// значения обратно (см. <c>ApplySketchEntityEdit</c>).
    /// </summary>
    private static bool? SafeUpdate(IDrawingObject drawing)
    {
        try
        {
            return drawing.Update();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or InvalidCastException)
        {
            return null;
        }
    }

    private static int? ReadLayer(IDrawingObject drawing, List<string> notes)
    {
        try
        {
            return drawing.LayerNumber;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or InvalidCastException)
        {
            notes.Add($"номер слоя не прочитан: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? SafeToString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Документ, которому принадлежит объект API7: подъём по <c>Parent</c>. Не
    /// <c>IApplication.ActiveDocument</c> — «активный документ» есть состояние ОКНА, и в неактивном
    /// документе адрес был бы выдан ЧУЖИМ документом, то есть молча неверным.
    /// </summary>
    private static IKompasDocument? DocumentOf(IKompasAPIObject? start)
    {
        var node = start;
        for (var depth = 0; depth < 32 && node is not null; depth++)
        {
            if (node is IKompasDocument document)
            {
                return document;
            }

            try
            {
                node = node.Parent;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or InvalidCastException)
            {
                return null;
            }
        }

        return null;
    }
}
