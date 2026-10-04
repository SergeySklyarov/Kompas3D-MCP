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

    /// <summary>Полезная нагрузка ссылки на компонент: представление API7 и номер компонента.</summary>
    private sealed record ComponentPayload(IPart7 Part7, int Reference);

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
            seen++;
            var row = ReadComponent(document, child, parentRef, depth, notes);
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
        DocumentEntry document, IPart7 part, string? parentRef, int depth, List<string> notes)
    {
        var reference = Safe(() => part.Reference);
        var stored = References.Register(
            ComponentRefKind, document.Id, document.Revision, new ComponentPayload(part, reference));

        return new ComponentRowDto
        {
            ComponentRef = stored.Id,
            ParentRef = parentRef,
            Depth = depth,
            Name = Text(() => part.Name),
            Marking = Text(() => part.Marking),
            SourcePath = Text(() => part.FileName),
            IsDetail = Bool(() => part.Detail),
            // InstanceCount в interop — ИНДЕКСИРОВАННОЕ свойство, принимающее Part7
            // (документация: InstanceCount(iPart7)). Кратность читается через него; какая форма
            // верна, НЕ измерено — см. unverified_aspects режима.
            InstanceCount = InstanceCountOf(part),
            Fixed = Bool(() => part.Fixed),
            LoadState = Safe(() => part.LoadState).ToString(),
            Matrix = PlacementMatrixOf(document, reference),
        };
    }

    /// <summary>
    /// Кратность компонента. Индексированное свойство требует <see cref="Part7"/>; если объект к
    /// нему не приводится, кратность НЕ читается (null), а не подменяется единицей.
    /// </summary>
    private static int? InstanceCountOf(IPart7 part)
    {
        try
        {
            return part is Part7 typed ? part.InstanceCount[typed] : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Матрица размещения компонента через API5-представление (<c>ksPlacement.GetMatrix3D</c>).</summary>
    private double[]? PlacementMatrixOf(DocumentEntry document, int reference)
    {
        try
        {
            if (reference != 0 && document.PartNow().GetPart((short)reference) is ksPart part
                && part.GetPlacement() is ksPlacement placement
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
        object? created;
        try
        {
            // plane = null: деталь не приклеивается к плоскости. Документация требует «плоский объект»,
            // но какой именно плоскостью адресовать вставку, не измерено; передавать догадку о
            // базовой плоскости значило бы выдать предположение за параметр.
            created = document.Document.CreatePartInAssembly(command.SourcePath, null!);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"CreatePartInAssembly прервался: {ex.Message}. Компонент не вставлен.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "CreatePartInAssembly вернул null: компонент не создан. Файл-источник и тип сборки " +
                "не меняются; повтор с тем же operation_id допустим после проверки файла.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = command.SourcePath });
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
            "plane_argument_guessed — CreatePartInAssembly получил plane=null; документированный " +
            "«плоский объект приклейки» не задан и не измерено, что это допустимо",
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
            "matrix_convention_unverified — порядок элементов 4×4 (строка/столбец) не измерен: " +
            "совпадение проверено по началу координат, а не по осям",
            "reference_numbering_unverified — " + lookupNote,
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
        var sourceBefore = Text(() => payload.Part7.FileName);
        var matrixBefore = ReadPlacementMatrix(part5);
        var countBefore = CountComponents(document);

        try
        {
            payload.Part7.FileName = command.SourcePath;
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

        var sourceAfter = Text(() => payload.Part7.FileName);
        var matrixAfter = ReadPlacementMatrix(part5);
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
            "replace_route_unverified — замена сделана присваиванием IPart7.FileName; " +
            "ChangeObjectLinks не вызывался (его роль не измерена)",
            "reference_numbering_unverified — " + lookupNote,
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
                new ComponentPayload(child, Safe(() => child.Reference)));
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

    private ksPart ComponentPart5(DocumentEntry document, ComponentPayload payload, out string note)
    {
        try
        {
            if (document.PartNow().GetPart((short)payload.Reference) is ksPart part)
            {
                note = $"ksPart.GetPart({payload.Reference}) — номер взят из IPart7.Reference";
                return part;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // падение приводится ниже как отказ возможности, а не молчаливый null
            note = $"GetPart({payload.Reference}) бросил {ex.GetType().Name}";
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Компонент по номеру {payload.Reference} не получен ({note}): размещение этим " +
                "маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        note = $"GetPart({payload.Reference}) вернул не ksPart";
        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Компонент по номеру {payload.Reference} не ksPart: {note}.",
            RetryPolicy.ReacquireContext);
    }

    private ComponentRowDto? FindInserted(DocumentEntry document, string sourcePath, List<string> notes)
    {
        var top = TopPart7(document, notes);
        if (top is null)
        {
            return null;
        }

        ComponentRowDto? match = null;
        foreach (var child in ChildrenOf(top, notes))
        {
            var row = ReadComponent(document, child, parentRef: null, depth: 0, notes);
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
    /// Порядок элементов (строка/столбец) НЕ измерен: документация <c>ksPlacement.InitByMatrix3D</c>
    /// говорит только «одномерный массив из 16 элементов, матрица 4×4». Выбран построчный
    /// (row-major) с переносом в последнем столбце. Совпадение проверяется по началу координат,
    /// а не по осям, — и это названо в <c>unverified_aspects</c>.
    /// </remarks>
    private static double[] MatrixOf(TransformDto transform)
    {
        var (ox, oy, oz) = Vec3(transform.OriginMm);
        var (xx, xy, xz) = Vec3(transform.XAxis);
        var (yx, yy, yz) = Vec3(transform.YAxis);
        var (zx, zy, zz) = Cross(xx, xy, xz, yx, yy, yz);
        return
        [
            xx, yx, zx, ox,
            xy, yy, zy, oy,
            xz, yz, zz, oz,
            0, 0, 0, 1,
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

        // Сверяем только перенос (элементы 3, 7, 11 в построчной раскладке): порядок осей не измерен.
        for (var i = 0; i < 3; i++)
        {
            var actual = after[3 + i * 4];
            var want = expected[3 + i * 4];
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
            : $"origin=({matrix[3]:0.####}, {matrix[7]:0.####}, {matrix[11]:0.####})";

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
