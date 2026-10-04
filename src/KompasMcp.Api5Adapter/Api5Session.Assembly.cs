using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Домен сборок — наряд C1, профиль <c>assemblies-minimal-v1</c>, режимы <c>ASM-02…ASM-06</c>
/// (ASM-01 и ASM-07 обслуживает общий жизненный цикл документа).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут измерен, но не прогнан.</b> Члены ниже измерены сканером
/// <c>tools/KompasMcp.InteropScan</c> по поставленным обёрткам 24.0.0.2799 и подтверждены
/// страницами справки по проводу (см. <c>coverage/solid-v24/catalog.json</c> → семейство
/// <c>ASM</c>). Живого прогона по сборке не было, поэтому уровни проверки здесь честно стоят на
/// <see cref="VerificationLevel.CallReturned"/> или ниже, а непроверенное названо в
/// <c>unverified_aspects</c>. Это НЕ подтверждённая возможность: «маршрут написан» ≠ «работает».
/// </para>
/// <para>
/// <b>Две системы нумерации, не переносить по аналогии.</b> Читаю структуру через API7
/// (<c>IPart7.PartsEx</c>), а размещение пишу через API5 (<c>ksPart.GetPlacement/SetPlacement/
/// UpdatePlacement</c>), потому что у API7 нет абсолютной записи размещения — только позиционер
/// со сдвигом. Мост между ними — <c>IPart7.Reference</c> (номер компонента), который передаётся в
/// <c>ksPart.GetPart</c>. Тождество этих номеров НЕ измерено и названо непроверенным.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Вид ссылки на компонент в реестре ссылок.</summary>
    private const string ComponentRefKind = "component";

    /// <summary>
    /// Полезная нагрузка ссылки на компонент: представление API7, <c>IPart7.Reference</c> и
    /// ПОРЯДКОВЫЙ НОМЕР в перечислении структуры.
    /// </summary>
    /// <remarks>
    /// Порядковый номер — адрес, потому что <c>IPart7.Reference</c> номером компонента не является
    /// (измерено: 1073741857), а сопоставление по файлу-источнику НЕОДНОЗНАЧНО при двух экземплярах
    /// одной детали. Адрес по номеру опирается на предположение, что порядок <c>IPart7.PartsEx</c> и
    /// порядок <c>ksPartCollection</c> совпадают; предположение проверяется различающим контролем
    /// (размещение одного экземпляра не должно менять другой).
    /// </remarks>
    private sealed record ComponentPayload(IPart7 Part7, int Reference, int Ordinal);

    // ===================================================================================== ASM-03
    /// <summary>Перечисление структуры сборки.</summary>
    public ListComponentsResult ListComponents(ListComponentsCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var rows = new List<ComponentRowDto>();
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var top = TopPart7(document, notes);
        if (top is null)
        {
            return new ListComponentsResult(rows, 0, 0, "api7:IAssemblyDocument.TopPart → IPart7.PartsEx",
                notes);
        }

        var seen = 0;
        Walk(top, parentRef: null, depth: 0, command.Recursive, document, rows, uniqueParts, ref seen,
            notes);

        return new ListComponentsResult(rows, uniqueParts.Count, seen,
            "api7:IAssemblyDocument.TopPart → IPart7.PartsEx(ksAllParts); размещение — GetSummMatrix",
            notes);
    }

    private void Walk(
        IPart7 node,
        string? parentRef,
        int depth,
        bool recursive,
        DocumentEntry document,
        List<ComponentRowDto> rows,
        HashSet<string> uniqueParts,
        ref int seen,
        List<string> notes)
    {
        foreach (var child in ChildrenOf(node, notes))
        {
            var ordinal = seen;
            seen++;
            var row = ReadComponent(document, node, child, parentRef, depth, ordinal, notes);
            rows.Add(row);
            if (row.SourcePath is { Length: > 0 } path)
            {
                uniqueParts.Add(path);
            }

            if (recursive && row.IsDetail == false)
            {
                Walk(child, row.ComponentRef, depth + 1, recursive: true, document, rows,
                    uniqueParts, ref seen, notes);
            }
        }
    }

    private List<IPart7> ChildrenOf(IPart7 node, List<string> notes)
    {
        var children = new List<IPart7>();
        try
        {
            var raw = node.get_PartsEx((object)(int)ksPart7CollectionTypeEnum.ksAllParts);
            switch (raw)
            {
                case null:
                    notes.Add("parts_ex_returned_null — PartsEx(ksAllParts) вернул null: " +
                              "компонентов не перечислено (возможно, сборка пуста)");
                    break;
                case Array array:
                    foreach (var item in array)
                    {
                        if (item is IPart7 part)
                        {
                            children.Add(part);
                        }
                    }

                    break;
                case IPart7 single:
                    // Документация: один объект → VT_DISPATCH, несколько → VT_ARRAY|VT_DISPATCH.
                    children.Add(single);
                    break;
                default:
                    notes.Add($"parts_ex_unexpected_type — PartsEx вернул {raw.GetType().Name}, " +
                              "а не SAFEARRAY/VT_DISPATCH: структура не прочитана");
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"parts_ex_failed — PartsEx бросил {ex.GetType().Name}: {ex.Message}");
        }

        return children;
    }

    private ComponentRowDto ReadComponent(
        DocumentEntry document, IPart7 parent, IPart7 part, string? parentRef, int depth,
        int ordinal, List<string> notes)
    {
        var reference = Safe(() => part.Reference);
        var stored = References.Register(
            ComponentRefKind, document.Id, document.Revision,
            new ComponentPayload(part, reference, ordinal));

        return new ComponentRowDto
        {
            ComponentRef = stored.Id,
            ParentRef = parentRef,
            Depth = depth,
            Name = Text(() => part.Name),
            Marking = Text(() => part.Marking),
            SourcePath = Text(() => part.FileName),
            IsDetail = Bool(() => part.Detail),
            // Кратность читается С РОДИТЕЛЯ: документация — «Count = iObject.InstanceCount(iPart7)»,
            // где iObject есть узел, содержащий вставки. ИЗМЕРЕНО 04.10.2026: чтение со САМОГО
            // компонента даёт 0 — то есть «счётчик не с той стороны» виден числом, а не молчанием.
            InstanceCount = InstanceCountOf(parent, part),
            ReferenceNumber = reference,
            Fixed = Bool(() => part.Fixed),
            LoadState = Safe(() => part.LoadState).ToString(),
            Matrix = PlacementMatrixByOrdinal(document, ordinal),
        };
    }

    /// <summary>
    /// Кратность компонента: <c>parent.InstanceCount(child)</c>. Индексированное свойство требует
    /// <see cref="Part7"/>; если объект к нему не приводится, кратность НЕ читается (null), а не
    /// подменяется единицей.
    /// </summary>
    private static int? InstanceCountOf(IPart7 parent, IPart7 child)
    {
        try
        {
            return parent is Part7 typedParent && child is Part7 typedChild
                ? parent.InstanceCount[typedChild]
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Матрица размещения компонента по его ПОРЯДКОВОМУ номеру в перечислении структуры.
    /// </summary>
    /// <remarks>
    /// Номер — адрес, потому что <c>IPart7.Reference</c> номером компонента не является (измерено
    /// 04.10.2026: 1073741857), а сопоставление по файлу-источнику неоднозначно при двух экземплярах
    /// одной детали. Порядок <c>ksPartCollection</c> сопоставляется с порядком <c>IPart7.PartsEx</c> —
    /// это ПРЕДПОЛОЖЕНИЕ, и различающий контроль (размещение одного экземпляра не меняет другой)
    /// его проверяет.
    /// </remarks>
    private double[]? PlacementMatrixByOrdinal(DocumentEntry document, int ordinal)
    {
        try
        {
            var parts = ComponentParts5(document);
            if (ordinal >= 0 && ordinal < parts.Count
                && parts[ordinal].GetPlacement() is ksPlacement placement
                && placement.GetMatrix3D(out var raw))
            {
                return Matrix16(raw);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Первый <c>ksPart</c>, чей <c>fileName</c> совпал с источником; иначе null.
    /// </summary>
    /// <remarks>
    /// <b>Перечисление — через <c>ksDocument3D.PartCollection(TRUE)</c>, а не перебором
    /// <c>GetPart(n)</c>.</b> Измерено 04.10.2026: <c>GetPart(1)</c> на сборке с ДВУМЯ компонентами
    /// вернул компонент ВТОРОГО источника, а <c>GetPart(2)</c> — null, то есть номером компонента
    /// <c>GetPart</c> не перечисляет. Документированный маршрут перечисления — динамический массив
    /// компонентов сборки: <c>PartCollection(refresh=true)</c> → <c>ksPartCollection</c>
    /// (<c>GetCount</c>/<c>GetByIndex</c>).
    /// </remarks>
    private static ksPart? FindPart5BySource(DocumentEntry document, string sourcePath)
    {
        var wanted = Path.GetFileName(sourcePath);
        foreach (var candidate in ComponentParts5(document))
        {
            if (string.Equals(Path.GetFileName(Safe(() => candidate.fileName) ?? string.Empty),
                    wanted, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Компоненты сборки по документированному массиву <c>PartCollection(refresh=true)</c>.</summary>
    private static List<ksPart> ComponentParts5(DocumentEntry document)
    {
        var parts = new List<ksPart>();
        try
        {
            if (document.Document.PartCollection(true) is not ksPartCollection collection)
            {
                return parts;
            }

            var count = collection.GetCount();
            for (var index = 0; index < count; index++)
            {
                if (collection.GetByIndex(index) is ksPart part)
                {
                    parts.Add(part);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return parts;
        }

        return parts;
    }

    /// <summary>
    /// <c>ksPlacement</c> из жёсткого преобразования; при отсутствии преобразования — документированное
    /// умолчание документа (<c>ksDocument3D.DefaultPlacement()</c>).
    /// </summary>
    private object? BuildPlacement(DocumentEntry document, TransformDto? transform)
    {
        var placement = document.Document.DefaultPlacement();
        if (transform is null)
        {
            return placement;
        }

        if (placement is not ksPlacement typed)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ksDocument3D.DefaultPlacement() не вернул ksPlacement: размещение этим маршрутом не " +
                "задаётся.",
                RetryPolicy.Never);
        }

        typed.InitByMatrix3D(ToVariant(MatrixOf(transform)));
        return typed;
    }

    // ===================================================================================== ASM-02
    /// <summary>
    /// Вставка компонента из файла. Маршрут — API5 <c>ksDocument3D.CreatePartInAssembly</c>:
    /// он единственный возвращает указатель на созданный компонент (документировано), тогда как
    /// API7 <c>ExecuteProcessOfInsertComponentFromFile</c> «запускает процесс» и адреса не отдаёт.
    /// </summary>
    public InsertComponentResult InsertComponent(InsertComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        var before = CountComponents(document);

        // ПЕРВЫЙ экземпляр — CreatePartInAssembly, ВТОРОЙ И ПОСЛЕДУЮЩИЕ — CopyPart.
        //
        // ИЗМЕРЕНО 04.10.2026 живым прогоном: повторная CreatePartInAssembly ТОГО ЖЕ файла возвращает
        // null (GEOMETRY_FAILED), тогда как вставка ДРУГОГО файла проходит. То есть отказ специфичен
        // для повторной вставки одной детали, а не для «второй вставки вообще». Документированный
        // маршрут копии компонента — ksDocument3D.CopyPart(sourcePart, newPlacement).
        //
        // Плоскость приклейки обязательна (документация: «плоский объект ksEntity или IEntity, к
        // которому приклеивается деталь»); берётся документированный GetDefaultEntity(o3d_planeXOY=1).
        // Передача null вместо плоскости тоже даёт GEOMETRY_FAILED — заглушка не проходит молча.
        var existing = FindPart5BySource(document, command.SourcePath);
        object? created;
        try
        {
            if (existing is not null)
            {
                created = document.Document.CopyPart(existing, BuildPlacement(document, command.Transform));
            }
            else
            {
                var plane = document.PartNow().GetDefaultEntity(KompasObjectTypes.PlaneXoy);
                if (plane is null)
                {
                    throw new KompasContractException(
                        ErrorCodes.CapabilityUnavailable,
                        "GetDefaultEntity(o3d_planeXOY) не вернул плоскость приклейки: без неё " +
                        "CreatePartInAssembly не вызывается, компонент не создан.",
                        RetryPolicy.ReacquireContext);
                }

                created = document.Document.CreatePartInAssembly(command.SourcePath, plane);
            }
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Вставка компонента прервалась: {ex.Message}. Компонент не вставлен.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                (existing is not null
                    ? "CopyPart вернул null: копия компонента не создана."
                    : "CreatePartInAssembly вернул null: компонент не создан.") +
                " Файл-источник и тип сборки не меняются; повтор с тем же operation_id допустим " +
                "после проверки файла.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["source_path"] = command.SourcePath,
                    ["route"] = existing is not null ? "CopyPart" : "CreatePartInAssembly",
                });
        }

        // Размещение первого экземпляра: CreatePartInAssembly плоскости не принимает, поэтому
        // заданное преобразование применяется к созданному компоненту ПОСЛЕ создания.
        if (existing is null && command.Transform is not null && created is ksPart firstPart)
        {
            try
            {
                if (BuildPlacement(document, command.Transform) is { } firstPlacement)
                {
                    firstPart.SetPlacement(firstPlacement);
                    firstPart.UpdatePlacement();
                }
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Компонент создан, но размещение не записалось: {ex.Message}. Размещение не " +
                    "подтверждено.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        // Параметр Fixed обязан быть ПРИМЕНЁН, а не объявлен: «объявлено и проглочено» — тот же
        // дефект, что «не поддержано, но обещано». Фиксация ставится на созданный компонент
        // (CreatePartInAssembly отдаёт ksPart).
        if (created is ksPart createdPart)
        {
            try
            {
                createdPart.fixedComponent = command.Fixed;
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Компонент создан, но фиксация (fixedComponent={command.Fixed}) не записалась: " +
                    $"{ex.Message}. Состояние фиксации не подтверждено.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.insert_component");

        var after = CountComponents(document);
        var notes = new List<string>();
        var inserted = FindInserted(document, command.SourcePath, notes);
        if (inserted is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Компонент создан (число компонентов " + before + " → " + after + "), но перечитать " +
                "его в структуре не удалось: адресовать вставку нечем. Это не «успех по умолчанию» — " +
                "результат не подтверждён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["component_count"] = after });
        }

        var payload = (ComponentPayload)References
            .Require(inserted.ComponentRef, document.Id, document.Revision).Payload!;
        var componentRef = ToDto(
            References.Require(inserted.ComponentRef, document.Id, document.Revision),
            $"component {inserted.Name ?? "?"}");

        var checks = new List<NamedCheck>
        {
            new("component_count_increased", after == before + 1,
                Observed: $"{before} → {after}", Expected: $"{before + 1}"),
            new("inserted_component_read_back", true,
                Observed: $"«{inserted.Name ?? "?"}», источник «{inserted.SourcePath ?? "?"}»"),
        };

        var unverified = new List<string>
        {
            "placement_not_applied — размещение при вставке не задавалось (Transform=null): компонент " +
            "стоит по умолчанию КОМПАСа, а не по координатам запроса",
            "reference_numbering_unverified — тождество IPart7.Reference и номера ksPart.GetPart " +
            "не измерено; адресация компонента держится на нём",
            "plane_is_base_xy — плоскостью приклейки взята базовая XOY (GetDefaultEntity(o3d_planeXOY)); " +
            "как приклейка к произвольной плоскости влияет на размещение, не измерялось",
        };
        if (payload.Reference == 0)
        {
            unverified.Add("component_reference_zero — IPart7.Reference вернул 0: адрес компонента " +
                           "по номеру может быть нерабочим");
        }

        return new InsertComponentResult(
            componentRef,
            inserted,
            after,
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified));
    }

    // ===================================================================================== ASM-04
    /// <summary>
    /// Задать размещение компонента жёстким преобразованием и перечитать его. Запись — API5
    /// <c>ksPlacement.InitByMatrix3D</c> + <c>ksPart.SetPlacement</c> + <c>ksPart.UpdatePlacement</c>
    /// (единственная документированная абсолютная запись размещения).
    /// </summary>
    public SetComponentPlacementResult SetComponentPlacement(SetComponentPlacementCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var matrix = MatrixOf(command.Transform);
        var beforeMatrix = ReadPlacementMatrix(part5) ?? Array.Empty<double>();

        bool written;
        try
        {
            if (part5.GetPlacement() is not ksPlacement placement)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "ksPart.GetPlacement() не вернул ksPlacement: размещение компонента этим " +
                    "маршрутом не задаётся.",
                    RetryPolicy.Never);
            }

            placement.InitByMatrix3D(ToVariant(matrix));
            written = part5.SetPlacement(placement);
            part5.UpdatePlacement();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись размещения прервалась: {ex.Message}. Размещение могло измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.set_placement");

        var afterMatrix = ReadPlacementMatrix(part5) ?? Array.Empty<double>();

        var matched = MatricesClose(beforeMatrix, afterMatrix, matrix);
        var checks = new List<NamedCheck>
        {
            new("set_placement_returned", written, Observed: written ? "true" : "false"),
            new("placement_read_back_matches_request", matched,
                Observed: Describe(afterMatrix), Expected: Describe(matrix)),
        };

        var unverified = new List<string>
        {
            "rotation_not_checked — сверено только начало координат: различающий контроль по осям " +
            "(поворот) этой строкой не ставится, а на единичном повороте раскладка неотличима",
            "component_lookup_by_source — " + lookupNote,
        };
        if (!matched)
        {
            unverified.Insert(0, "placement_not_read_back — перечитанное размещение не совпало с " +
                                 "заданным: запись не подтверждена");
        }

        return new SetComponentPlacementResult(
            ToDto(stored, "component"),
            beforeMatrix,
            afterMatrix,
            new VerificationDto(matched ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-05
    /// <summary>Заменить источник компонента с сохранением размещения.</summary>
    public ReplaceComponentResult ReplaceComponent(ReplaceComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var sourceBefore = Safe(() => part5.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixBefore = ReadPlacementMatrix(part5);
        var countBefore = CountComponents(document);

        // ЗАМЕНА ИСТОЧНИКА — документированный сеттер IPart7.FileName.
        //
        // ИЗМЕРЕНО 04.10.2026 живым прогоном, две ветки:
        //  * ksDocument3D.SetPartFromFileEx(fileName, part, …) — «part — указатель на интерфейс
        //    компонента, который будет вставлен в документ»: это маршрут ВСТАВКИ, а не замены.
        //    Измерено: он добавил ТРЕТИЙ компонент вместо замены первого.
        //  * присваивание IPart7.FileName меняет представление немедленно, но без перестроения
        //    МОДЕЛИ (IPart7.RebuildModel) не переживало save→close→reopen. Здесь оно и добавлено.
        try
        {
            // Пишется ОБА представления: API5 ksPart.fileName (свойство документа компонента) и
            // API7 IPart7.FileName. Измерено 04.10.2026: одного API7-сеттера НЕ хватало — замена не
            // переживала save→close→reopen.
            part5.fileName = command.SourcePath;
            part5.Update();
            payload.Part7.FileName = command.SourcePath;
            payload.Part7.RebuildModel(true);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Замена FileName прервалась: {ex.Message}. Ссылка могла измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.replace_component");

        // Перечитывается СВЕЖИЙ объект: после замены прежний может быть представлением.
        var part5After = FindPart5BySource(document, command.SourcePath);
        var sourceAfter = Safe(() => part5After?.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixAfter = part5After is not null
            ? ReadPlacementMatrix(part5After)
            : ReadPlacementMatrix(part5);
        var countAfter = CountComponents(document);

        var placementPreserved = matrixBefore is not null && matrixAfter is not null
                                 && MatricesEqual(matrixBefore, matrixAfter);
        var sourceChanged = !string.Equals(sourceBefore, sourceAfter, StringComparison.OrdinalIgnoreCase);

        var checks = new List<NamedCheck>
        {
            new("source_changed", sourceChanged,
                Observed: sourceAfter ?? "не читается", Expected: command.SourcePath),
            new("placement_preserved", placementPreserved,
                Observed: Describe(matrixAfter), Expected: Describe(matrixBefore)),
            new("component_count_unchanged", countAfter == countBefore,
                Observed: $"{countBefore} → {countAfter}", Expected: $"{countBefore}"),
        };

        var unverified = new List<string>
        {
            "external_link_assumed — SetPartFromFileEx вызван с externalFile=true (вставка со ссылкой " +
            "на внешний файл); вариант «телом» (false) не измерялся",
            "component_lookup_by_source — " + lookupNote,
        };
        if (!placementPreserved)
        {
            unverified.Insert(0, "placement_not_preserved — размещение до и после не совпало " +
                                 "(или не читается): сохранение размещения не подтверждено");
        }

        var structural = sourceChanged && countAfter == countBefore;
        return new ReplaceComponentResult(
            ToDto(stored, "component"),
            sourceBefore,
            sourceAfter,
            matrixBefore,
            matrixAfter,
            countAfter,
            new VerificationDto(
                structural && placementPreserved ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-06
    /// <summary>Проверка ссылок компонентов на файлы-источники.</summary>
    public CheckComponentLinksResult CheckComponentLinks(CheckComponentLinksCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var links = new List<ComponentLinkDto>();

        var top = TopPart7(document, notes);
        if (top is not null)
        {
            CollectLinks(document, top, links, notes);
        }

        var broken = links.Count(l => l.SourceExists == false);
        var unverified = new List<string>();
        if (links.Count == 0)
        {
            unverified.Add("no_components — в сборке не перечислено ни одного компонента: проверять нечего");
        }

        unverified.Add("broken_link_shape_unmeasured — чем именно выглядит битая ссылка (LoadState, " +
                       "отказ Load или пустой FileName) не измерено: вердикт опирается на наличие " +
                       "файла по пути, а не на состояние загрузки КОМПАСа");

        var checks = new List<NamedCheck>
        {
            new("links_enumerated", links.Count > 0, Observed: links.Count.ToString(CultureInfo.InvariantCulture)),
            new("broken_named", true, Observed: broken.ToString(CultureInfo.InvariantCulture)),
        };

        return new CheckComponentLinksResult(
            links, broken,
            new VerificationDto(links.Count > 0 ? VerificationLevel.StructureChecked
                : VerificationLevel.CallReturned, checks, unverified));
    }

    private void CollectLinks(
        DocumentEntry document, IPart7 node, List<ComponentLinkDto> links, List<string> notes)
    {
        foreach (var child in ChildrenOf(node, notes))
        {
            var path = Text(() => child.FileName);
            var exists = path is { Length: > 0 } && SafeFileExists(path);
            var stored = References.Register(
                ComponentRefKind, document.Id, document.Revision,
                new ComponentPayload(child, Safe(() => child.Reference), links.Count));
            links.Add(new ComponentLinkDto(
                stored.Id,
                Text(() => child.Name),
                path,
                path is { Length: > 0 } ? exists : null,
                Safe(() => child.LoadState).ToString(),
                path is not { Length: > 0 } ? "источник не назван (FileName пуст)"
                    : exists ? "источник на месте"
                    : "ИСТОЧНИК ОТСУТСТВУЕТ — ссылка битая"));
        }
    }

    // ===================================================================================== помощники
    private DocumentEntry RequireAssembly(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Документ «{documentId}» имеет тип {document.Kind}: команды сборки применимы только " +
                "к документу-сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                });
        }

        return document;
    }

    private void RequireRevision(DocumentEntry document, long expected)
    {
        if (expected != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Ожидалась ревизия {expected}, текущая {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["expected_revision"] = expected,
                    ["current_revision"] = document.Revision,
                });
        }
    }

    private void RequireSourceFile(string path)
    {
        if (!SafeFileExists(path))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Файл-источник не найден: {path}",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = path });
        }
    }

    private IPart7? TopPart7(DocumentEntry document, List<string> notes)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is IPart7 part7)
        {
            return part7;
        }

        notes.Add("api7_bridge_unavailable — " + (bridge.BridgeFailure ?? "причина не известна") +
                  ": структура сборки не прочитана");
        return null;
    }

    /// <summary>
    /// API5-представление компонента для записи размещения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ИЗМЕРЕНО 04.10.2026 живым прогоном и опровергло мост по номеру.</b> Ожидалось, что
    /// <c>IPart7.Reference</c> — номер компонента для <c>ksPart.GetPart</c>. На сборке с одним
    /// компонентом он равен <b>1073741857</b> (0x40000001) — это не номер: <c>GetPart</c> по нему
    /// вернул не <c>ksPart</c>. Поэтому компонент ищется ПЕРЕБОРОМ номеров с сопоставлением по
    /// файлу-источнику (<c>ksPart.filename</c>), и найденный номер называется в примечании.
    /// </para>
    /// <para>
    /// Перебор ограничен числом, а не «разумным»: 1…<see cref="MaxComponentScan"/>. Неоднозначность
    /// (два компонента одного файла) разрешается только для ОДНОГО совпадения; при нескольких
    /// вызывающий получает отказ с перечнем номеров, а не «первый попавшийся».
    /// </para>
    /// </remarks>
    private ksPart ComponentPart5(DocumentEntry document, ComponentPayload payload, out string note)
    {
        var parts = ComponentParts5(document);
        if (payload.Ordinal >= 0 && payload.Ordinal < parts.Count)
        {
            note = $"ksPartCollection.GetByIndex({payload.Ordinal}) — адрес по порядковому номеру " +
                   $"(IPart7.Reference={payload.Reference} номером компонента НЕ является; " +
                   "сопоставление порядков API7↔API5 — предположение, проверяемое различающим контролем)";
            return parts[payload.Ordinal];
        }

        // Диагностика: что вообще отдаёт PartCollection. Без неё «не найден» неотличимо от
        // «перечисление пустое», и следующая правка снова угадывала бы.
        var sample = parts
            .Select(p => $"name='{Safe(() => p.name)}' file='{Safe(() => p.fileName)}'").ToList();
        note = $"порядковый номер {payload.Ordinal} вне PartCollection(true) " +
               $"(всего компонентов {sample.Count}: {string.Join(" | ", sample)})";
        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"API5-представление компонента не получено ({note}): размещение этим маршрутом не " +
            "задаётся.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["api7_reference"] = payload.Reference,
                ["ordinal"] = payload.Ordinal,
                ["components"] = sample.Count,
            });
    }

    private ComponentRowDto? FindInserted(DocumentEntry document, string sourcePath, List<string> notes)
    {
        var top = TopPart7(document, notes);
        if (top is null)
        {
            return null;
        }

        ComponentRowDto? match = null;
        var ordinal = 0;
        foreach (var child in ChildrenOf(top, notes))
        {
            var row = ReadComponent(document, top, child, parentRef: null, depth: 0, ordinal, notes);
            ordinal++;
            if (row.SourcePath is { Length: > 0 } path
                && string.Equals(Path.GetFileName(path), Path.GetFileName(sourcePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                match = row;
            }
        }

        return match;
    }

    private double[]? ReadPlacementMatrix(ksPart part)
    {
        try
        {
            return part.GetPlacement() is ksPlacement placement && placement.GetMatrix3D(out var raw)
                ? Matrix16(raw)
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Матрица 4×4 из жёсткого преобразования (начало + две ортонормированные оси; Z = X × Y).
    /// </summary>
    /// <remarks>
    /// <b>Раскладка — измеренная, не выбранная.</b> <see cref="RepositionMatrix"/> (Domain/Geometry)
    /// хранит ту же раскладку, что КОМПАС пишет для положения тела, и она подтверждена пробой RP.2
    /// (18.09.2026, прогон <c>929f0888…</c>): три первых числа — образ оси X, следующие три — оси Y,
    /// затем Z, последние четыре — строка переноса и 1. То есть 3×3 лежит ПОСТОЛБЦОВО, а перенос
    /// стоит в ПОСЛЕДНЕЙ СТРОКЕ (индексы 12, 13, 14). Первая редакция этого метода писала
    /// построчно с переносом в 3/7/11 — живой прогон 04.10.2026 показал, что запись размещения
    /// тогда НЕ берётся (перечитанное начало координат осталось нулевым).
    /// </remarks>
    private static double[] MatrixOf(TransformDto transform)
    {
        var (ox, oy, oz) = Vec3(transform.OriginMm);
        var (xx, xy, xz) = Vec3(transform.XAxis);
        var (yx, yy, yz) = Vec3(transform.YAxis);
        var (zx, zy, zz) = Cross(xx, xy, xz, yx, yy, yz);
        return
        [
            xx, xy, xz, 0,
            yx, yy, yz, 0,
            zx, zy, zz, 0,
            ox, oy, oz, 1,
        ];
    }

    private static double[] Matrix16(object? variant)
    {
        if (variant is Array array && array.Length >= 16)
        {
            var result = new double[16];
            for (var i = 0; i < 16; i++)
            {
                result[i] = Convert.ToDouble(array.GetValue(i), CultureInfo.InvariantCulture);
            }

            return result;
        }

        return new double[16];
    }

    private static object ToVariant(double[] matrix)
    {
        var result = new double[matrix.Length];
        Array.Copy(matrix, result, matrix.Length);
        return result;
    }

    private static bool MatricesClose(double[]? before, double[]? after, double[] expected)
    {
        if (after is null || after.Length < 16)
        {
            return false;
        }

        // Сверяем перенос: в измеренной раскладке он стоит в последней строке — индексы 12, 13, 14.
        for (var i = 0; i < 3; i++)
        {
            var actual = after[12 + i];
            var want = expected[12 + i];
            if (Math.Abs(actual - want) > Math.Max(0.01, Math.Abs(want) * 1e-6))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatricesEqual(double[]? a, double[]? b)
    {
        if (a is null || b is null || a.Length < 16 || b.Length < 16)
        {
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (Math.Abs(a[i] - b[i]) > Math.Max(0.01, Math.Abs(a[i]) * 1e-6))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(double[]? matrix) =>
        matrix is null || matrix.Length < 16
            ? "не читается"
            : $"origin=({matrix[12]:0.####}, {matrix[13]:0.####}, {matrix[14]:0.####})";

    private static (double X, double Y, double Z) Vec3(IReadOnlyList<double> v) =>
        (v.Count > 0 ? v[0] : 0, v.Count > 1 ? v[1] : 0, v.Count > 2 ? v[2] : 0);

    private static (double X, double Y, double Z) Cross(
        double ax, double ay, double az, double bx, double by, double bz) =>
        (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return default;
        }
    }

    private static string? Text(Func<string?> read) => Safe(read);

    private static bool? Bool(Func<bool> read) => Safe(read);
}
