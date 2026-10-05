using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasAPI7;

namespace KompasMcp.Api5Adapter;

/// <summary>Sketch entities as objects with a STABLE ADDRESS: enumeration, addressed read and
/// addressed edit (<c>kompas_list_sketch_entities</c>, <c>kompas_edit_sketch_entity</c>).</summary>
/// <remarks>
/// INVARIANT: the ADDRESS is the string from <c>IKompasDocument1.GetObjectId</c>, accepted back by
/// <c>IKompasDocument1.FindObjectById</c>; a collection index is NOT an address (a rebuild shifts it).
/// INVARIANT: an edit is confirmed by RE-RESOLVING THE ADDRESS, not by a return code.
/// History: docs/decisions/adapter-sketch.md#sketch-entities
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Enumerate the entities of an existing sketch; with a given address, read one.</summary>
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

    /// <summary>Addressed edit of one existing sketch entity.</summary>
    /// <remarks>
    /// INVARIANT: the edit entry is for WRITING — <c>BeginEdit()</c>, not <c>BeginEditEx(true)</c>: a
    /// "read-only" mode would produce a refusal that looks like a missing capability.
    /// LIMIT: <c>delete</c> removes EXACTLY ONE entity named by an address, leaving the others in place.
    /// History: docs/decisions/adapter-sketch.md#sketch-entity-delete
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

        // Enumeration BEFORE the edit: the entity count is needed as a measured quantity, not an estimate.
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

        // INVARIANT: address resolution happens INSIDE the edit session: the address belongs to the
        // sketch's FRAGMENT document, not the part document, and the fragment exists only between
        // BeginEdit() and EndEdit().
        // History: docs/decisions/adapter-sketch.md#fragment-document
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

        // CONFIRMATION — re-resolving the SAME address, not a return code. It was already taken
        // INSIDE the edit session (where the address was resolved), and here it is only copied into
        // the report.
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

    /// <summary>Outcome of the edit session: the address resolved and the edit applied WITHIN one entry
    /// into the sketch.</summary>
    /// <remarks>The three failure fields are DISTINGUISHED, not merged: "the object at the address is
    /// not graphical" (<c>INVALID_ARGUMENT</c>), "the address did not resolve"
    /// (<c>STALE_REFERENCE</c>) and "the edit did not apply" (<c>GEOMETRY_FAILED</c>) are different
    /// states with different codes.</remarks>
    private sealed record SketchEntityEditOutcome(
        string? ResolveFailure,
        string? ArgumentFailure,
        string? EditFailure,
        bool ResolvedAfter,
        int? LayerAfter,
        string? KindAfter,
        string? AfterResolveFailure = null);

    /// <summary>Edit entry, ADDRESS RESOLUTION, edit, CONFIRMATION and EXIT — in one session.</summary>
    /// <remarks>INVARIANT: the address belongs to the sketch's FRAGMENT document, which exists only
    /// between <c>BeginEdit()</c> and <c>EndEdit()</c>; outside the session the part document answers
    /// an empty string for the same address.
    /// History: docs/decisions/adapter-sketch.md#fragment-document</remarks>
    private static SketchEntityEditOutcome ApplySketchEntityEdit(
        ISketch sketch, EditSketchEntityCommand command, List<string> notes)
    {
        FragmentDocument? fragment;
        try
        {
            // BeginEdit() — FOR WRITING. BeginEditEx(true) would open the sketch read-only, and the
            // edit would get a refusal indistinguishable from a missing capability.
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
                    // The Update() return is RECORDED BUT IS NOT THE VERDICT: an unsuccessful code does
                    // not prove the edit was not applied, so the application sign is taken by reading
                    // the value back below, not from this return.
                    // History: docs/decisions/adapter-sketch.md#update-return
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

            // CONFIRMATION — re-resolving the SAME address and reading the value back in the SAME
            // session, not a return code.
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
            // The exit is mandatory even on the failure branch: an open fragment would hold the sketch
            // in edit mode and the next call would find the model busy.
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or InvalidCastException)
            {
                // A failed exit is a reason for the journal, not grounds to return a false success.
            }
        }
    }

    /// <summary>The <c>IDrawingObject.Update()</c> return as EVIDENCE, not as a verdict: <c>null</c> —
    /// the call threw, <c>false</c> — it returned non-true. The decision on whether the edit applied is
    /// taken by reading the value back (see <c>ApplySketchEntityEdit</c>).</summary>
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

    /// <summary>The document that owns an API7 object: climb up via <c>Parent</c>. NOT
    /// <c>IApplication.ActiveDocument</c> — "active document" is a WINDOW state, and in an inactive
    /// document the address would be issued by a FOREIGN document, i.e. silently wrong.</summary>
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
