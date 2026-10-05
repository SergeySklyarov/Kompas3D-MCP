using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Assemblies domain — order C1, profile <c>assemblies-minimal-v1</c>, modes <c>ASM-02…ASM-06</c>
/// (ASM-01 and ASM-07 are served by the shared document lifecycle).</summary>
/// <remarks>MEASURED: structure read via API7 (<c>IPart7.PartsEx</c>), placement written via API5
/// (<c>ksPart.GetPlacement/SetPlacement/UpdatePlacement</c>) — API7 has no absolute placement write.
/// INVARIANT: a component address is the ORDINAL in the flat <c>ksDocument3D.PartCollection(true)</c>,
/// not <c>IPart7.Reference</c> (MEASURED: 1073741857). The ordinal addresses TOP-LEVEL components only;
/// a nested component has no address and a mutation by a guessed ordinal is forbidden.</remarks>
public partial class Api5Session
{
    /// <summary>Component reference kind in the reference registry.</summary>
    private const string ComponentRefKind = "component";

    /// <summary>Ordinal of a NESTED component: it has no address. It differs from any real ordinal in that real
    /// ones start at zero; "-1" means "the API5 address does not apply".</summary>
    private const int NestedComponentOrdinal = -1;

    /// <summary>Depth limit of the structure walk. Exceeding it is NAMED, not silenced.</summary>
    private const int MaxStructureDepth = 64;

    /// <summary>Component reference payload: the API7 view, the PARENT node (the matrix is read fresh from it), <c>IPart7.Reference</c> (diagnostics, not an address) and the ORDINAL in the structure enumeration.</summary>
    /// <remarks>The ordinal is the address: <c>IPart7.Reference</c> is not a component number (MEASURED: 1073741857) and matching by source file is ambiguous with two instances of one part. The parent is stored so the matrix is read FRESH at comparison time.
    /// INVARIANT: the placement matrix is NOT stored in the reference — a snapshot went stale from the server's OWN mutation and rejected a second mutation with <c>STALE_REFERENCE</c>.
    /// History: docs/decisions/assembly.md#identity</remarks>
    private sealed record ComponentPayload(IPart7 Part7, IPart7? Parent7, int? Reference, int Ordinal);

    // ===================================================================================== ASM-03
    /// <summary>Enumerate the assembly structure.</summary>
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

        var enumerated = 0;
        var topLevelOrdinal = 0;
        Walk(top, parentRef: null, depth: 0, command.Recursive, document, rows, uniqueParts,
            ref enumerated, ref topLevelOrdinal, notes);

        if (rows.Any(r => r.Depth > 0))
        {
            notes.Add("nested_components_not_addressable — вложенные компоненты перечислены для " +
                      "ЧТЕНИЯ структуры, но адреса API5 у них нет: ksDocument3D.PartCollection(true) " +
                      "перечисляет компоненты сборки ПЛОСКО, поэтому размещение, замена и геометрия " +
                      "вложенного компонента этим выпуском НЕ поддерживаются и отвергаются, а не " +
                      "выполняются по предположительному номеру");
        }

        return new ListComponentsResult(rows, uniqueParts.Count, enumerated,
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
        ref int enumerated,
        ref int topLevelOrdinal,
        List<string> notes)
    {
        if (depth > MaxStructureDepth)
        {
            // The depth limit is NAMED: a silent "cut off and did not say" is indistinguishable from
            // "the structure ended". The former walk had no limit at all.
            notes.Add($"structure_depth_limit — обход структуры остановлен на глубине {MaxStructureDepth}");
            return;
        }

        foreach (var child in ChildrenOf(node, notes))
        {
            enumerated++;

            // AN API5 ADDRESS EXISTS ONLY FOR A TOP-LEVEL COMPONENT.
            // `ksDocument3D.PartCollection(true)` enumerates assembly components FLATLY, so the ordinal
            // is meaningful only at depth 0. The former walk numbered the WHOLE tree with ONE counter,
            // and a nested component's ordinal was passed to PartCollection — so geometry reads and
            // mutations would hit a FOREIGN component. That is not "nested support" but address
            // substitution.
            var ordinal = depth == 0 ? topLevelOrdinal++ : NestedComponentOrdinal;
            var row = ReadComponent(document, node, child, parentRef, depth, ordinal, notes);
            rows.Add(row);
            if (row.SourcePath is { Length: > 0 } path)
            {
                uniqueParts.Add(path);
            }

            if (row.IsDetail is null)
            {
                // AN UNREAD SIGNAL IS NAMED, NOT SWALLOWED. The former walk simply stopped: "the
                // structure ended" and "the signal was not read" were indistinguishable, whereas
                // `CountComponents` writes a note in the same case (defect L6, review 05.10.2026).
                notes.Add($"component_detail_unread_in_walk — у компонента «{row.Name ?? "?"}» " +
                          "признак «деталь/сборка» не прочитан: обход под этим узлом не продолжен, " +
                          "структура может быть неполной");
            }
            else if (recursive && row.IsDetail == false)
            {
                Walk(child, row.ComponentRef, depth + 1, recursive: true, document, rows,
                    uniqueParts, ref enumerated, ref topLevelOrdinal, notes);
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
                    // DOC: one object → VT_DISPATCH, several → VT_ARRAY|VT_DISPATCH.
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
        // Reference is a diagnostic number, NOT an address (see ComponentPayload). An unread value
        // stays null instead of becoming 0: "the number was not read" and "number 0" are different
        // claims.
        var reference = Int(() => part.Reference);
        var addressable = ordinal != NestedComponentOrdinal;

        // THE API7 MATRIX FOR THE RESPONSE. Read via the documented IPart7.GetSummMatrix
        // (ipart7_getsummmatrix.html): «суммарная матрица преобразования координат», 16 elements, 4x4.
        // This is the placement AT READ TIME — it is what goes into the response; it is NOT stored in
        // the REFERENCE, otherwise the snapshot would go stale from the server's own mutation (see
        // ComponentPayload). Read only for an addressable (top-level) component: a nested one has no
        // address, so there is nothing to read.
        var matrix7 = addressable ? SummMatrix7(parent, part) : null;

        var stored = References.Register(
            ComponentRefKind, document.Id, document.Revision,
            new ComponentPayload(part, parent, reference, ordinal));

        if (!addressable)
        {
            notes.Add($"nested_component_no_address — компонент «{Text(() => part.Name) ?? "?"}» " +
                      $"вложен (глубина {depth}): тело, грани и размещение НЕ читаются, потому что " +
                      "адрес API5 перечисляет компоненты плоско и у вложенного компонента его нет");
        }

        return new ComponentRowDto
        {
            ComponentRef = stored.Id,
            ParentRef = parentRef,
            Depth = depth,
            Name = Text(() => part.Name),
            Marking = Text(() => part.Marking),
            SourcePath = Text(() => part.FileName),
            IsDetail = Bool(() => part.Detail),
            // Instance count is read from the PARENT: DOC — «Count = iObject.InstanceCount(iPart7)»,
            // where iObject is the node containing the insertions. MEASURED 04.10.2026: reading from
            // the component ITSELF gives 0 — i.e. "the counter is on the wrong side" is visible as a
            // number, not as silence.
            InstanceCount = InstanceCountOf(parent, part),
            ReferenceNumber = reference,
            Fixed = Bool(() => part.Fixed),
            LoadState = ReadEnumName(() => part.LoadState),
            // COMPONENT BODIES AND FACES — documented ksPart.BodyCollection() → ksBody.FaceCollection().
            // Without them "inserted" is indistinguishable from "empty component inserted". Read only for
            // an addressable component, so "not read" is honestly null.
            BodyCount = addressable ? BodyCountOf(ComponentPart5At(document, ordinal)) : null,
            FaceCount = addressable ? FaceCountOf(ComponentPart5At(document, ordinal)) : null,
            // PLACEMENT IS RETURNED FROM THE API7 SIDE (GetSummMatrix) at read time; if the API7 matrix
            // was not read, the API5 matrix is named — an unread value is not replaced by zeros. The
            // matrix is NOT stored in the reference: identity comparison reads it fresh from the live
            // parent (see IdentityMatches and ComponentPayload).
            Matrix = addressable ? matrix7 ?? PlacementMatrixByOrdinal(document, ordinal) : null,
        };
    }

    /// <summary>
    /// The component's total coordinate-transform matrix from the API7 side — the documented
    /// <c>IPart7.GetSummMatrix(IPart7 Part1)</c>.
    /// </summary>
    /// <remarks>DOC: <c>ipart7_getsummmatrix.html</c>, verbatim: «GetSummMatrix — Получить суммарную матрицу
    /// преобразование координат»; «Элементы матрицы возвращаются в виде одномерного массива из
    /// шестнадцати элементов»; «Матрица имеет размер 4х4»; parameter <c>Part1</c> — «указатель на
    /// интерфейс IPart7 детали из которой нужно сделать пересчет координат». Called on the PARENT: for
    /// a top-level component the parent is the assembly's top component, so the matrix is in document
    /// coordinates and comparable with API5 <c>ksPart.GetPlacement().GetMatrix3D</c>. An unread value
    /// is <c>null</c>, not zeros.</remarks>
    private static double[]? SummMatrix7(IPart7 parent, IPart7 child)
    {
        // The parameter is documented as "a pointer to the IPart7 interface", while the shipped wrapper
        // 24.0.0.2799 declares it as the concrete class Part7 — a wrapper/help divergence, named here
        // rather than smoothed over: if the object does not cast to Part7, the matrix is NOT read
        // (null), and the caller will see that.
        if (child is not Part7 typedChild)
        {
            return null;
        }

        try
        {
            return Matrix16(parent.GetSummMatrix(typedChild));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Component instance count: <c>parent.InstanceCount(child)</c>. The indexed property requires
    /// <see cref="Part7"/>; if the object does not cast, the count is NOT read (null) instead of being
    /// replaced by one.
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

    /// <summary>The API5 view of a component by ordinal (the address was MEASURED in C1).</summary>
    private static ksPart? ComponentPart5At(DocumentEntry document, int ordinal)
    {
        try
        {
            var parts = ComponentParts5(document);
            return ordinal >= 0 && ordinal < parts.Count ? parts[ordinal] : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Component body count via the documented <c>ksPart.BodyCollection()</c>.</summary>
    private static int? BodyCountOf(ksPart? component)
    {
        try
        {
            return component?.BodyCollection() is ksBodyCollection bodies ? bodies.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Face count of the component's first body via <c>ksBody.FaceCollection()</c>.</summary>
    private static int? FaceCountOf(ksPart? component)
    {
        try
        {
            return component?.BodyCollection() is ksBodyCollection bodies
                && bodies.GetCount() > 0
                && bodies.GetByIndex(0) is ksBody body
                && body.FaceCollection() is ksFaceCollection faces
                    ? faces.GetCount()
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Component placement matrix by its ORDINAL in the structure enumeration.</summary>
    /// <remarks>The ordinal is the address because <c>IPart7.Reference</c> is not a component number (MEASURED
    /// 04.10.2026: 1073741857), and matching by source file is ambiguous with two instances of one part.
    /// The <c>ksPartCollection</c> order is matched to the <c>IPart7.PartsEx</c> order — this is an
    /// ASSUMPTION, and a discriminating control (moving one instance does not move another) checks it.</remarks>
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

    /// <summary>Assembly components via the documented array <c>PartCollection(refresh=true)</c>.</summary>
    /// <remarks>
    /// ENUMERATION IS VIA <c>ksDocument3D.PartCollection(TRUE)</c>, not <c>GetPart(n)</c>: MEASURED
    /// 04.10.2026, <c>GetPart(1)</c> returned the component of the SECOND source and <c>GetPart(2)</c>
    /// returned null. The documented array is <c>PartCollection(refresh=true)</c> →
    /// <c>ksPartCollection</c> (<c>GetCount</c>/<c>GetByIndex</c>).
    /// </remarks>
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
    /// <c>ksPlacement</c> from a rigid transform; with no transform, the documented document default
    /// (<c>ksDocument3D.DefaultPlacement()</c>).
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
    /// <summary>Insert a component from a file — the documented API7 <c>IPart7.Parts →
    /// IParts7.AddFromFile(FileName, ExternalFile=true, Redraw=true) → Part7</c>
    /// (<c>iparts7_addfromfile.html</c>), yielding components WITH GEOMETRY.</summary>
    /// <remarks>LIMIT: <c>ksDocument3D.CreatePartInAssembly</c> is rejected as MEASURABLY wrong (0 bodies, 0
    /// faces; it CREATES a part, it does not insert one).
    /// History: docs/decisions/assembly.md#insert-route</remarks>
    public InsertComponentResult InsertComponent(InsertComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        // Snapshot BEFORE insertion: both the component count and their sources. With these, the
        // inserted instance is confirmed as the SINGLE new one, not as "the last in the list".
        var beforeParts = ComponentParts5(document);
        var beforeOrdinals = beforeParts.Count;
        var beforeNames = beforeParts.Select(p => Ref(() => p.fileName)).ToList();

        // COMPONENT INSERTION — the documented IParts7.AddFromFile (iparts7_addfromfile.html).
        // MEASURED 05.10.2026: the former CreatePartInAssembly gave 0 bodies / 0 faces — DOC calls it a
        // part CREATED in the assembly, not an insertion. Here each component gets 1 body / 6 faces and
        // survives save→close→reopen. History: docs/decisions/assembly.md#insert-route
        var bridge = BridgeFor(document);
        if (bridge.TransferTo7(document.Document) is not IKompasDocument3D document7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ-сборка не переносится в API7 как IKompasDocument3D: коллекция компонентов " +
                "IPart7.Parts недостижима, вставка не выполнена.",
                RetryPolicy.ReacquireContext);
        }

        var top7 = document7.TopPart;
        var parts7 = top7?.Parts;
        if (parts7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.Parts не вернул IParts7: вставлять нечем, компонент не создан.",
                RetryPolicy.ReacquireContext);
        }

        Part7? inserted7;
        try
        {
            inserted7 = parts7.AddFromFile(command.SourcePath, ExternalFile: true, Redraw: true);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"IParts7.AddFromFile прервался: {ex.Message}. Компонент не вставлен.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        if (inserted7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IParts7.AddFromFile вернул null: компонент не вставлен. Файл-источник и тип сборки " +
                "не меняются; повтор с тем же operation_id допустим после проверки файла.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = command.SourcePath });
        }

        // THE INSERTED INSTANCE'S ADDRESS IS CONFIRMED, NOT "THE LAST IN THE COLLECTION".
        //
        // `ksDocument3D.PartCollection(true)` (ksdocument3d_partcollection.html) enumerates assembly
        // components, and insertion adds EXACTLY ONE. The new ordinal is confirmed by TWO discriminating
        // signals AT ONCE: (1) the new component's source matched the requested one; (2) the neighbour
        // at the former ordinal kept ITS former source. "Last" alone is not enough: with a
        // non-appending order it would address a foreign component, and matching by file name is
        // ambiguous with two instances of one part.
        var afterParts = ComponentParts5(document);
        ksPart? createdPart = null;
        var addressNote = $"после вставки компонентов {afterParts.Count}, ожидалось {beforeOrdinals + 1}";
        if (afterParts.Count == beforeOrdinals + 1)
        {
            var candidate = afterParts[beforeOrdinals];
            var candidateName = Ref(() => candidate.fileName);
            var sourceMatches = !string.IsNullOrEmpty(candidateName)
                && string.Equals(Path.GetFileName(candidateName), Path.GetFileName(command.SourcePath),
                    StringComparison.OrdinalIgnoreCase);
            var neighborPreserved = beforeOrdinals == 0 || string.Equals(
                Path.GetFileName(Ref(() => afterParts[beforeOrdinals - 1].fileName) ?? string.Empty),
                Path.GetFileName(beforeNames[beforeOrdinals - 1] ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
            if (sourceMatches && neighborPreserved)
            {
                createdPart = candidate;
                addressNote = $"PartCollection.GetByIndex({beforeOrdinals}) — единственный новый " +
                              "компонент; источник совпал с запрошенным, сосед сохранил прежний источник";
            }
            else
            {
                addressNote = $"номер {beforeOrdinals}: источник='{candidateName ?? "не читается"}', " +
                              $"совпадение={sourceMatches}, сосед сохранён={neighborPreserved}";
            }
        }

        // MANDATORY REQUEST PARAMETERS MUST BE APPLIED, NOT SWALLOWED: with no API5 view there is nowhere
        // to put the requested placement and fixing, and "inserted as requested" would be a lie. The
        // insertion ALREADY happened — a named refusal with partial effects, not "not created".
        if (createdPart is null)
        {
            // A PARTIAL EFFECT BUMPS THE REVISION AND REVOKES REFERENCES: the model changed while the
            // revision and references stayed old, so the next call would mutate a model that is already
            // different (defect M4, review 05.10.2026).
            BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);

            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Компонент ВСТАВЛЕН (компонентов сборки {beforeOrdinals} → {afterParts.Count}), но " +
                $"API5-представление вставленного экземпляра не получено ({addressNote}): размещение " +
                "и фиксация НЕ применены. Это ЧАСТИЧНЫЙ ЭФФЕКТ, а не «компонент не создан»; повторная " +
                "вставка для исправления размещения запрещена — сверьте модель и работайте со " +
                "структурой.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["source_path"] = command.SourcePath,
                    ["components_before"] = beforeOrdinals,
                    ["components_after"] = afterParts.Count,
                    ["address_note"] = addressNote,
                });
        }

        // Placement: the requested transform is applied to the inserted component and RE-READ. "Written"
        // is confirmed by the re-read translation matching the request, not by the fact of the call: the
        // MATE lesson — a successful call does not prove the result.
        var placementApplied = command.Transform is null;
        double[]? placementAfter = null;
        if (command.Transform is not null)
        {
            try
            {
                if (BuildPlacement(document, command.Transform) is { } placement)
                {
                    createdPart.SetPlacement(placement);
                    createdPart.UpdatePlacement();
                }
            }
            catch (COMException ex)
            {
                BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Компонент вставлен, но размещение не записалось: {ex.Message}. Размещение не " +
                    "подтверждено.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }

            var wanted = MatrixOf(command.Transform);
            placementAfter = ReadPlacementMatrix(createdPart);
            placementApplied = MatrixMatchesRequest(placementAfter, wanted);
        }

        // The Fixed parameter must be APPLIED and RE-READ: "declared and swallowed" is the same defect
        // as "not supported but promised". Read via the documented IPart7.Fixed of the SAME inserted
        // instance; an unread value (null) is NOT counted as a match.
        bool fixedApplied;
        bool? fixedAfter;
        try
        {
            createdPart.fixedComponent = command.Fixed;
            fixedAfter = Bool(() => ((IPart7)inserted7).Fixed);
        }
        catch (COMException ex)
        {
            BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Компонент вставлен, но фиксация (fixedComponent={command.Fixed}) не записалась: " +
                $"{ex.Message}. Состояние фиксации не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        fixedApplied = fixedAfter == command.Fixed;

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.insert_component");

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the revision is already bumped, so a client reading
        // only `status` must not take an unapplied placement for a completed one (defect M5, review
        // 05.10.2026).
        if (!placementApplied || !fixedApplied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Компонент вставлен, но ЗАПРОШЕННЫЕ ПАРАМЕТРЫ НЕ ПОДТВЕРЖДЕНЫ: "
                + (placementApplied ? string.Empty : $"размещение перечитано как «{Describe(placementAfter)}» и не совпало с запрошенным; ")
                + (fixedApplied ? string.Empty : $"фиксация перечитана как «{(fixedAfter is null ? "не читается" : fixedAfter.Value.ToString())}» вместо «{command.Fixed}»; ")
                + "успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["placement_applied"] = placementApplied,
                    ["fixed_applied"] = fixedApplied,
                    ["fixed_requested"] = command.Fixed,
                    ["fixed_read_back"] = fixedAfter,
                    ["placement_read_back"] = placementAfter,
                });
        }

        var after = CountComponents(document);
        var notes = new List<string>();

        // The component row is built FROM THE INSERTED INSTANCE (inserted7), not by a file-name search:
        // with two instances of one part, a name search would return the wrong instance.
        var inserted = top7 is null
            ? null
            : ReadComponent(document, top7, (IPart7)inserted7, parentRef: null, depth: 0,
                ordinal: beforeOrdinals, notes);

        if (inserted is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Компонент вставлен, но перечитать его в структуре не удалось: адресовать вставку " +
                "нечем. Это не «успех по умолчанию» — результат не подтверждён.",
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
            new("component_count_increased", afterParts.Count == beforeOrdinals + 1,
                Observed: $"{beforeOrdinals} → {afterParts.Count}", Expected: $"{beforeOrdinals + 1}"),
            new("inserted_component_read_back", true,
                Observed: $"«{inserted.Name ?? "?"}», источник «{inserted.SourcePath ?? "?"}»"),
            new("inserted_address_confirmed", true, Observed: addressNote),
            new("placement_applied", placementApplied,
                Observed: command.Transform is null ? "размещение не задавалось (Transform=null)"
                    : Describe(placementAfter),
                Expected: command.Transform is null ? "—" : "совпадение переноса с запросом"),
            new("fixed_applied", fixedApplied,
                Observed: fixedAfter is null ? "фиксация не прочитана" : fixedAfter.Value.ToString(),
                Expected: command.Fixed.ToString()),
        };

        var unverified = new List<string>();
        if (command.Transform is null)
        {
            unverified.Add("placement_not_applied — размещение при вставке НЕ задавалось " +
                           "(Transform=null в запросе): компонент стоит по умолчанию КОМПАСа");
        }
        else if (!placementApplied)
        {
            unverified.Add("placement_not_read_back — перечитанное размещение не совпало с заданным: " +
                           "запись не подтверждена");
        }

        if (!fixedApplied)
        {
            unverified.Add("fixed_not_read_back — перечитанный признак фиксации не совпал с заданным " +
                           "или не прочитан");
        }

        unverified.Add("reference_numbering_unverified — IPart7.Reference — ДИАГНОСТИЧЕСКОЕ число, а не " +
                       "адрес: адресация идёт по номеру в ksDocument3D.PartCollection(true) и " +
                       "подтверждается различающим контролем при вставке");

        if (payload.Reference is null or 0)
        {
            unverified.Add("component_reference_unread — IPart7.Reference не прочитан (или равен 0): " +
                           "диагностический номер компонента недоступен. Адресация на него не " +
                           "опирается, но и подтвердить его нельзя");
        }

        return new InsertComponentResult(
            componentRef,
            inserted,
            after,
            new VerificationDto(
                placementApplied && fixedApplied ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-04
    /// <summary>
    /// Set a component's placement by a rigid transform and re-read it. Write via API5
    /// <c>ksPlacement.InitByMatrix3D</c> + <c>ksPart.SetPlacement</c> + <c>ksPart.UpdatePlacement</c>
    /// (the only documented absolute placement write).
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

        // AN UNREAD MATRIX IS NOT A ZERO MATRIX. An earlier revision substituted
        // `Array.Empty<double>()`, i.e. "zero placement", where the placement simply was not read
        // (defect M6, review 05.10.2026 — the same class as fix C).
        var beforeMatrix = ReadPlacementMatrix(part5);

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

        var afterMatrix = ReadPlacementMatrix(part5);
        var matched = MatrixMatchesRequest(afterMatrix, matrix);
        var checks = new List<NamedCheck>
        {
            new("set_placement_returned", written, Observed: written ? "true" : "false"),
            new("placement_read_back_matches_request", matched,
                Observed: Describe(afterMatrix), Expected: Describe(matrix)),
        };

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the re-read placement did not match the requested
        // one (or was not read), and a result of "done" would be a lie (defect M5).
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Размещение записано, но ПЕРЕЧИТАНОЕ размещение не совпало с запрошенным: "
                + $"запрос «{Describe(matrix)}», перечитано «{Describe(afterMatrix)}». Успехом это не " +
                "считается: модель изменена, размещение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["placement_before"] = beforeMatrix,
                    ["placement_after"] = afterMatrix,
                    ["placement_requested"] = matrix,
                });
        }

        var unverified = new List<string>
        {
            "component_address_by_ordinal — " + lookupNote,
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
    /// <summary>Replace a component's source while preserving placement.</summary>
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
        var sourceBefore = Ref(() => part5.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixBefore = ReadPlacementMatrix(part5);
        var countBefore = CountComponents(document);

        // SOURCE REPLACEMENT — the documented ksPart.SetFileName. DOC: kspart_setfilename.html, notes
        // verbatim: «Метод используется для компонентов, вставленных в сборку»; «Документ с указанным
        // именем должен существовать»; «Компонент не должен быть деталью из библиотеки моделей или
        // стандартным элементом»; «ИЗМЕНЕНИЕ ВСТУПАЕТ В СИЛУ ПОСЛЕ ВЫЗОВА МЕТОДА ksPart::Update» —
        // hence Update below. The method returns BOOL and the return IS checked; the shipped wrapper
        // 24.0.0.2799 declares only a property, so the result is read back through the documented getter
        // ksPart.fileName. History: docs/decisions/assembly.md#replace-source
        try
        {
            part5.fileName = command.SourcePath;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись ksPart.fileName прервалась: {ex.Message}. Ссылка могла измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        // Help note 4: the change takes effect AFTER Update().
        part5.Update();

        var replacedPath = Ref(() => part5.fileName);
        if (!string.Equals(
                Path.GetFileName(replacedPath ?? string.Empty),
                Path.GetFileName(command.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // THIS IS NOT A "CLEAN FAILURE", even though the re-read name did not match: `ksPart.fileName`
            // and `ksPart.Update()` have already run, so a replay with the SAME operation_id would apply
            // the replacement a SECOND time. Declared a partial effect.
            // History: docs/decisions/assembly.md#replace-partial
            BumpRevision(document, "assembly.replace_component.partial", invalidateAll: true);

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Источник компонента НЕ заменён: после записи и Update() перечитывается " +
                $"«{replacedPath ?? "null"}» вместо «{command.SourcePath}». Запись fileName и Update() " +
                "уже вызваны, поэтому исход считается ЧАСТИЧНЫМ ЭФФЕКТОМ, а не чистым отказом: " +
                "повтор с тем же operation_id без согласования недопустим. Причины по справке: документа " +
                "с указанным именем нет, либо компонент — деталь из библиотеки моделей или стандартный " +
                "элемент.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["source_path"] = command.SourcePath,
                    ["read_back"] = replacedPath,
                    ["update_called"] = true,
                    ["revision_bumped"] = true,
                });
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.replace_component");

        // THE SAME INSTANCE IS RE-READ — by the SAME ORDINAL, not "the first with the same file": a name
        // search read `sourceAfter`/`matrixAfter` from a DIFFERENT instance when the assembly already had
        // the new source (defect M8, review 05.10.2026).
        var part5After = ComponentPart5At(document, payload.Ordinal);
        var sourceAfter = Ref(() => part5After?.fileName) ?? Text(() => payload.Part7.FileName);
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
            "component_address_by_ordinal — " + lookupNote,
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
    /// <summary>Check component references to source files.</summary>
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
                new ComponentPayload(child, node, Int(() => child.Reference), links.Count));
            links.Add(new ComponentLinkDto(
                stored.Id,
                Text(() => child.Name),
                path,
                path is { Length: > 0 } ? exists : null,
                ReadEnumName(() => child.LoadState),
                path is not { Length: > 0 } ? "источник не назван (FileName пуст)"
                    : exists ? "источник на месте"
                    : "ИСТОЧНИК ОТСУТСТВУЕТ — ссылка битая"));
        }
    }

    // ===================================================================================== helpers
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

    /// <summary>The API5 view of a component for writing placement.</summary>
    /// <remarks>
    /// MEASURED 04.10.2026, and it REFUTED the bridge by <c>IPart7.Reference</c>: on a one-component
    /// assembly it equals 1073741857 (0x40000001), and <c>GetPart</c> by it returned no <c>ksPart</c>.
    /// So the address is the ORDINAL in the flat <c>ksDocument3D.PartCollection(true)</c>. The
    /// <c>ksPartCollection</c>↔<c>IPart7.PartsEx</c> order is an ASSUMPTION, so identity is checked
    /// before a mutation (<see cref="IdentityMatches"/>).
    /// </remarks>
    private ksPart ComponentPart5(DocumentEntry document, ComponentPayload payload, out string note)
    {
        if (payload.Ordinal == NestedComponentOrdinal)
        {
            // NESTED COMPONENT: no address. A mutation "by a guessed ordinal" is forbidden — it would hit
            // a FOREIGN component, because PartCollection enumerates components flatly. This is a LIMIT
            // of the delivery, named and not worked around.
            note = "вложенный компонент: адрес API5 неприменим (PartCollection перечисляет компоненты плоско)";
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Компонент вложен в подсборку, и адресовать его API5-маршрутом нельзя: " +
                "ksDocument3D.PartCollection(true) перечисляет компоненты сборки ПЛОСКО, поэтому " +
                "порядковый номер вложенного компонента адресом не является. Размещение и замена " +
                "вложенного компонента этим выпуском не поддерживаются; работайте с компонентом " +
                "верхнего уровня.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["api7_reference"] = payload.Reference,
                    ["ordinal"] = payload.Ordinal,
                    ["supported"] = "компоненты верхнего уровня (Depth=0)",
                });
        }

        var parts = ComponentParts5(document);
        if (payload.Ordinal >= 0 && payload.Ordinal < parts.Count)
        {
            var candidate = parts[payload.Ordinal];
            note = $"ksPartCollection.GetByIndex({payload.Ordinal}) — адрес по порядковому номеру " +
                   $"(IPart7.Reference={payload.Reference} номером компонента НЕ является; " +
                   "сопоставление порядков API7↔API5 проверено различающим контролем)";

            // IDENTITY IS CHECKED BEFORE THE MUTATION, and the refusal also fires on "not compared": the
            // ordinal from `IPart7.PartsEx` indexes the flat `PartCollection(true)`, so a subassembly
            // before a part would shift the indices and address a FOREIGN component. The DECISION is the
            // PURE `ComponentIdentity.Decide` (Domain); ONLY THE SOURCE decides the refusal, name and
            // placement matrix are notes, and "nothing to compare" refuses too.
            // History: docs/decisions/assembly.md#identity
            var identity = IdentityMatches(candidate, payload, out var identityNote);
            note += "; " + identityNote;
            if (identity != true)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    identity == false
                        ? "Адрес компонента НЕ подтверждён: по порядковому номеру "
                          + $"{payload.Ordinal} в ksDocument3D.PartCollection(true) лежит ДРУГОЙ " +
                          "компонент (расходится файл-источник). Мутация по неподтверждённому номеру " +
                          "попала бы в ЧУЖОЙ компонент, поэтому она не выполняется. Перечитайте " +
                          "структуру сборки kompas_list_components и возьмите свежую ссылку."
                        : "Адрес компонента НЕ подтверждён: файл-источник компонента не прочитан с " +
                          "одной из сторон, а имя и матрица размещения отказом не управляют, поэтому " +
                          "подтвердить адрес НЕЧЕМ. Мутация по НЕПОДТВЕРЖДЁННОМУ адресу не выполняется " +
                          "— перечитайте структуру сборки kompas_list_components и возьмите свежую " +
                          "ссылку.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["ordinal"] = payload.Ordinal,
                        ["identity_check"] = identityNote,
                        ["identity_confirmed"] = identity is null ? "не сверено (источник не прочитан)" : "расхождение источника",
                        ["api7_reference"] = payload.Reference,
                    });
            }

            return candidate;
        }

        // Diagnostics: what PartCollection actually returns. Without it "not found" is indistinguishable
        // from "the enumeration is empty", and the next fix would guess again.
        var sample = parts
            .Select(p => $"name='{Ref(() => p.name)}' file='{Ref(() => p.fileName)}'").ToList();
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

    /// <summary>Whether this is the same component: THREE signals are compared, but ONLY THE SOURCE DECIDES THE REFUSAL.</summary>
    /// <returns><c>true</c> — identity confirmed (the source file was read on both sides and matched); <c>false</c> — the address leads to a FOREIGN component (the source was read and differs); <c>null</c> — NOTHING to compare (the source was not read on one side).</returns>
    /// <remarks>DECISION IS A PURE FUNCTION (<see cref="ComponentIdentity.Decide"/>, Domain, no COM types): the table is checked without KOMPAS.
    /// Only the SOURCE FILE decides the refusal — the one signal MEASURED live and not changing by itself; the component name (never compared live) and the placement matrix (MUTABLE, layout not measured) are NOTES.
    /// Both matrices are read FRESH at ONE point in time, and FILE NAMES are compared, not full paths (API7 and API5 return the path differently).
    /// History: docs/decisions/assembly.md#identity</remarks>
    private bool? IdentityMatches(ksPart part5, ComponentPayload payload, out string detail)
    {
        var from5 = Ref(() => part5.fileName);
        var from7 = Text(() => payload.Part7.FileName);
        var name5 = string.IsNullOrWhiteSpace(from5) ? null : Path.GetFileName(from5);
        var name7 = string.IsNullOrWhiteSpace(from7) ? null : Path.GetFileName(from7);

        var componentName5 = Ref(() => part5.name);
        var componentName7 = Text(() => payload.Part7.Name);

        // BOTH MATRICES AT ONE POINT IN TIME. API5 — by ordinal; API7 — from the LIVE parent, re-read
        // here rather than taken as a snapshot from the reference (see ComponentPayload).
        var matrix5 = ReadPlacementMatrix(part5);
        var matrix7 = payload.Parent7 is null ? null : SummMatrix7(payload.Parent7, payload.Part7);

        var sourceSignal = name5 is null || name7 is null
            ? ComponentIdentitySignal.NotRead
            : string.Equals(name5, name7, StringComparison.OrdinalIgnoreCase)
                ? ComponentIdentitySignal.Matches
                : ComponentIdentitySignal.Differs;
        var nameSignal = CompareRaw(componentName5, componentName7);
        var matrixSignal = matrix5 is null || matrix7 is null
            ? ComponentIdentitySignal.NotRead
            : MatricesEqual(matrix5, matrix7)
                ? ComponentIdentitySignal.Matches
                : ComponentIdentitySignal.Differs;

        var verdict = ComponentIdentity.Decide(sourceSignal, nameSignal, matrixSignal);

        // THE VALUES ARE NEXT TO THE RULE: the note explains the decision AND names what was actually
        // read. Silence about a mismatch is indistinguishable from "we did not look".
        detail = verdict.Detail
            + $" [прочитано: источник по номеру «{name5 ?? "не читается"}», ссылка «{name7 ?? "не читается"}»; "
            + $"имя «{componentName5 ?? "не читается"}» / «{componentName7 ?? "не читается"}»; "
            + $"матрица по номеру {Describe(matrix5)}, ссылка {Describe(matrix7)}]";
        return verdict.Matches;
    }

    /// <summary>Compare two texts as an identity signal; empty on either side means "not read".</summary>
    private static ComponentIdentitySignal CompareRaw(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return ComponentIdentitySignal.NotRead;
        }

        return string.Equals(a, b, StringComparison.Ordinal)
            ? ComponentIdentitySignal.Matches
            : ComponentIdentitySignal.Differs;
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

    /// <summary>A 4x4 matrix from a rigid transform (origin + two orthonormal axes; Z = X × Y).</summary>
    /// <remarks>
    /// The layout is MEASURED, not chosen: the triples are NOT consecutive — the array reads as
    /// <c>[X, 0][Y, 0][Z, 0][translation, 1]</c> (MEASURED by rotation, 04.10.2026, row
    /// <c>ASM.04.rotation</c>; the first revision shifted at 3/7/11 and the write did not take). A pure
    /// translation cannot tell the packing apart, so the discriminating control must be a rotation.
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

    /// <returns>A 16-number matrix, or <c>null</c> if the value was NOT READ (an earlier revision returned zeros,
    /// turning "not read" into "at the origin" — defect M6, review 05.10.2026).</returns>
    private static double[]? Matrix16(object? variant)
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

        return null;
    }

    private static object ToVariant(double[] matrix)
    {
        var result = new double[matrix.Length];
        Array.Copy(matrix, result, matrix.Length);
        return result;
    }

    /// <summary>Whether the RE-READ placement matches the requested one: rotation and translation.</summary>
    /// <remarks>12 significant elements are compared: three rotation rows (0…2, 4…6, 8…10) and the translation
    /// (12…14). An earlier revision compared ONLY the translation, so an unapplied rotation read back as
    /// confirmed (defect M6, review 05.10.2026).</remarks>
    private static bool MatrixMatchesRequest(double[]? after, double[] expected)
    {
        if (after is null || after.Length < 16)
        {
            // NOT READ means NOT CONFIRMED. Previously an unread matrix turned into zeros and "matched" a
            // request whose translation was also zero.
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (i is 3 or 7 or 11 or 15)
            {
                continue;
            }

            var actual = after[i];
            var want = expected[i];
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

    /// <summary>Read a COM value with an EXPLICIT distinction between "read" and "not read".</summary>
    /// <remarks>The logic lives in <see cref="SafeRead"/> (Domain, no KOMPAS COM types) and has a unit test. The
    /// former <c>Safe&lt;T&gt;</c> returned <c>default</c>, substituting a KNOWN value for an UNKNOWN one.</remarks>
    private static ReadResult<T> TryRead<T>(Func<T> read) => SafeRead.TryRead(read);

    private static T? Ref<T>(Func<T?> read) where T : class => SafeRead.Ref(read);

    private static string? Text(Func<string?> read) => SafeRead.Text(read);

    /// <summary>Read bool: <c>null</c> — NOT READ, <c>false</c> — read as false.</summary>
    private static bool? Bool(Func<bool> read) => SafeRead.Bool(read);

    /// <summary>Read an already-nullable bool (e.g. <c>obj?.Valid</c>): <c>null</c> — not read.</summary>
    private static bool? Bool(Func<bool?> read) => SafeRead.Bool(read);

    private static int? Int(Func<int> read) => SafeRead.Int(read);

    private static long? Long(Func<long> read) => SafeRead.Long(read);

    private static double? Double(Func<double> read) => SafeRead.Double(read);

    /// <summary>The name of an enum value; "not read" is printed AS A WORD, not as the first enum value.</summary>
    private static string ReadEnumName<T>(Func<T> read) where T : struct, Enum => SafeRead.EnumName(read);

    /// <summary>An already-nullable enum value (e.g. <c>obj?.Alignment</c>): <c>null</c> — NOT read. An unread
    /// value is not replaced by the first enum value.</summary>
    private static T? EnumOrNull<T>(Func<T?> read) where T : struct, Enum => SafeRead.EnumOrNull(read);
}
