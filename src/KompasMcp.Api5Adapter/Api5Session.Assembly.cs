using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Assemblies domain — order C1, profile <c>assemblies-minimal-v1</c>, modes <c>ASM-02…ASM-06</c>;
/// ASM-01 and ASM-07 are served by the shared document lifecycle.</summary>
/// <remarks>MEASURED: structure read via API7 (<c>IPart7.PartsEx</c>), placement written via API5
/// (<c>ksPart.GetPlacement/SetPlacement/UpdatePlacement</c>) — API7 has no absolute placement write.
/// INVARIANT: a component address is the INDEX in the flat <c>ksDocument3D.PartCollection(true)</c>, not
/// <c>IPart7.Reference</c>, and it is FOUND by matching (<see cref="ComponentAddress"/>), never counted; a
/// nested component has no address and a mutation by a guessed one is forbidden.
/// History: docs/decisions/assembly.md#identity</remarks>
public partial class Api5Session
{
    /// <summary>Component reference kind in the reference registry.</summary>
    private const string ComponentRefKind = "component";

    /// <summary>Ordinal of a NESTED component: it has no address. It differs from any real ordinal in that real
    /// ones start at zero; "-1" means "the API5 address does not apply".</summary>
    private const int NestedComponentOrdinal = -1;

    /// <summary>Ordinal of a TOP-LEVEL component whose address was NOT confirmed by matching: no documented
    /// property told it apart from the other instances. "-2" keeps it distinct from a real index and from
    /// the nested case, whose refusals differ.</summary>
    private const int UnaddressedComponentOrdinal = -2;

    /// <summary>Depth limit of the structure walk. Exceeding it is NAMED, not silenced.</summary>
    private const int MaxStructureDepth = 64;

    /// <summary>Component reference payload: the API7 view, the PARENT node (the matrix is read fresh from
    /// it), <c>IPart7.Reference</c> (diagnostics, not an address) and the ORDINAL.</summary>
    /// <remarks>INVARIANT: the ordinal is the address of a TOP-LEVEL component only; a NESTED one is
    /// addressed by the PATH IN THE REFERENCE ID (<see cref="ComponentPath"/>), so one place carries it.
    /// INVARIANT: the placement matrix is NOT stored here — a snapshot went stale from the server's OWN
    /// mutation and rejected a second mutation with <c>STALE_REFERENCE</c>.
    /// History: docs/decisions/assembly.md#identity, docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private sealed record ComponentPayload(IPart7 Part7, IPart7? Parent7, int? Reference, int Ordinal);

    // ── ASM-03 ──
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
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Walk(top, parentRef: null, depth: 0, command.Recursive, document, rows, uniqueParts,
            ref enumerated, ranks, notes, path: null);

        if (rows.Any(r => r.Depth > 0))
        {
            notes.Add("nested_component_addressed_by_path — вложенный компонент адресуется путём " +
                      "индексов IPart7.Parts от верхней сборки, и такой ссылкой работают " +
                      "kompas_set_component_placement и kompas_replace_component (маршрут API7: " +
                      "IPart7.Placement и IPart7.FileName). Остальные операции над компонентом " +
                      "(удаление, BOM, пересечения, разнесение, массивы компонентов) требуют " +
                      "компонент ВЕРХНЕГО уровня и отвергают вложенный с CAPABILITY_UNAVAILABLE, " +
                      "потому что адрес API5 перечисляет компоненты плоско");
        }

        return new ListComponentsResult(rows, uniqueParts.Count, enumerated,
            "api7:IAssemblyDocument.TopPart → IPart7.PartsEx(ksAllParts); адрес верхнего компонента — "
            + "сопоставление записи с ksDocument3D.PartCollection(true) по полному пути и размещению, "
            + "адрес вложенного — путь индексов; размещение — GetSummMatrix в координатах родителя",
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
        Dictionary<string, int> ranks,
        List<string> notes,
        IReadOnlyList<int>? path)
    {
        if (depth > MaxStructureDepth)
        {
            // The depth limit is NAMED: a silent "cut off" is indistinguishable from "the structure
            // ended".
            notes.Add($"structure_depth_limit — обход структуры остановлен на глубине {MaxStructureDepth}");
            return;
        }

        var children = ChildrenOf(node, notes);
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            enumerated++;

            // THE PATH IS THE ADDRESS OF A NESTED COMPONENT: level by level, the index of the child in
            // the SAME enumeration the row was read from. MEASURED: this index equals the one
            // IPart7.Parts→IParts7.Part(index) answers, so a later command resolves the SAME component.
            var childPath = path is null ? [index] : path.Append(index).ToArray();

            // THE ADDRESS IS MATCHED, NOT COUNTED: `IPart7.PartsEx` omits an excluded instance, so a
            // counter would shift every later address onto a FOREIGN component. A record that cannot be
            // matched gets NO address (see AddressOf), never a neighbour's.
            var row = ReadComponent(document, node, child, parentRef, depth, ranks, notes, childPath);
            rows.Add(row);
            if (row.SourcePath is { Length: > 0 } source)
            {
                uniqueParts.Add(source);
            }

            if (row.IsDetail is null)
            {
                // AN UNREAD SIGNAL IS NAMED, NOT SWALLOWED: "the structure ended" and "the signal was
                // not read" must not look alike, and `CountComponents` writes a note in the same case.
                notes.Add($"component_detail_unread_in_walk — у компонента «{row.Name ?? "?"}» " +
                          "признак «деталь/сборка» не прочитан: обход под этим узлом не продолжен, " +
                          "структура может быть неполной");
            }
            else if (recursive && row.IsDetail == false)
            {
                Walk(child, row.ComponentRef, depth + 1, recursive: true, document, rows,
                    uniqueParts, ref enumerated, ranks, notes, childPath);
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
        Dictionary<string, int>? ranks, List<string> notes, IReadOnlyList<int> path)
    {
        // Reference is a diagnostic number, NOT an address (see ComponentPayload). An unread value
        // stays null, not 0: "not read" and "0" are different claims.
        var reference = Int(() => part.Reference);

        // THE API7 MATRIX FOR THE RESPONSE, via the documented IPart7.GetSummMatrix
        // (ipart7_getsummmatrix.html): «суммарная матрица преобразования координат», 16 elements, 4x4.
        // It is the placement AT READ TIME in the coordinates of the PARENT node — MEASURED: for a
        // nested component it equals IPart7.Placement's local matrix and does NOT include the parent's
        // own origin. It is NOT stored in the REFERENCE (see ComponentPayload).
        var matrix7 = SummMatrix7(parent, part);
        var ordinal = depth == 0 ? AddressOf(document, part, matrix7, ranks, notes) : NestedComponentOrdinal;
        var addressable = ordinal != NestedComponentOrdinal && ordinal != UnaddressedComponentOrdinal;

        var stored = References.Register(
            ComponentRefKind, document.Id, document.Revision,
            new ComponentPayload(part, parent, reference, ordinal),
            idSuffix: depth == 0 ? null : ComponentPath.Separator + ComponentPath.Format(path));

        if (depth != 0)
        {
            notes.Add($"nested_component_geometry_not_read — у компонента «{Text(() => part.Name) ?? "?"}» " +
                      $"(глубина {depth}) число тел и граней НЕ читается: ksPart.BodyCollection() " +
                      "доступен только по плоскому PartCollection, а размещение и источник этого " +
                      "компонента читаются и пишутся маршрутом API7");
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
            // where iObject is the node containing the insertions. Reading from the component ITSELF
            // gives 0 — "the counter is on the wrong side" is visible as a number, not as silence.
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
            Matrix = matrix7 ?? (addressable ? PlacementMatrixByOrdinal(document, ordinal) : null),
        };
    }

    /// <summary>The address of a TOP-LEVEL component: its index in the flat
    /// <c>ksDocument3D.PartCollection(true)</c>, FOUND by matching (see <see cref="ComponentAddress"/>).</summary>
    /// <param name="ranks">Rank per source file over one enumeration; <c>null</c> — a just-inserted record,
    /// which the insert appends, so its rank is the LAST one of its file.</param>
    /// <remarks>DOC: <c>kspart_filename.html</c>, <c>ipart7_filename.html</c>, <c>kspart_getplacement.html</c>,
    /// <c>ipart7_getsummmatrix.html</c>, <c>kspart_excluded.html</c>, <c>kspartcollection_getbyindex.html</c>.
    /// INVARIANT: no unambiguous match → NO address and a named note; a neighbour's index is never used.
    /// History: docs/decisions/assembly.md#identity</remarks>
    private int AddressOf(
        DocumentEntry document, IPart7 part, double[]? matrix7, Dictionary<string, int>? ranks,
        List<string> notes)
    {
        var instances = ComponentParts5(document)
            .Select(part5 => new ComponentInstance(Ref(() => part5.fileName), ReadPlacementMatrix(part5),
                Bool(() => part5.excluded)))
            .ToList();
        var source = Text(() => part.FileName);

        // THE RANK IS COUNTED PER SOURCE FILE, not across the whole enumeration: the same-file instances are
        // the only ones a counter can shift, because an excluded instance is the only one PartsEx omits.
        var rank = ComponentAddress.LastRank(instances, source);
        if (ranks is not null && !string.IsNullOrWhiteSpace(source))
        {
            ranks.TryGetValue(source, out rank);
            ranks[source] = rank + 1;
        }

        var verdict = ComponentAddress.Locate(
            new ComponentInstance(source, matrix7, Excluded: false), instances, rank);

        if (verdict.Index is { } index)
        {
            return index;
        }

        notes.Add($"component_address_unconfirmed — у компонента «{Text(() => part.Name) ?? "?"}» "
                  + $"{verdict.Detail}");
        return UnaddressedComponentOrdinal;
    }

    /// <summary>The component's total coordinate-transform matrix from API7 — the documented
    /// <c>IPart7.GetSummMatrix(IPart7 Part1)</c>.</summary>
    /// <remarks>DOC: <c>ipart7_getsummmatrix.html</c>, verbatim: «GetSummMatrix — Получить суммарную матрицу
    /// преобразование координат»; «Элементы матрицы возвращаются в виде одномерного массива из
    /// шестнадцати элементов»; «Матрица имеет размер 4х4»; parameter <c>Part1</c> — «указатель на
    /// интерфейс IPart7 детали из которой нужно сделать пересчет координат». Called on the PARENT, so the
    /// matrix is in document coordinates and comparable with API5 <c>ksPart.GetPlacement().GetMatrix3D</c>.
    /// An unread value is <c>null</c>, not zeros.</remarks>
    private static double[]? SummMatrix7(IPart7 parent, IPart7 child)
    {
        // The parameter is documented as "a pointer to the IPart7 interface", while the shipped wrapper
        // declares it as the concrete class Part7 — a wrapper/help divergence, named here rather than
        // smoothed over: if the object does not cast to Part7, the matrix is NOT read (null).
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
            return parent is Part7 && child is Part7 typedChild
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
    /// <remarks>The ordinal is the address (<c>IPart7.Reference</c> is not a component number; matching by
    /// source file is ambiguous). The <c>ksPartCollection</c> ↔ <c>IPart7.PartsEx</c> order is an
    /// ASSUMPTION, checked by a discriminating control (moving one instance does not move another).
    /// DOC: the SAME read as <c>ReadPlacementMatrix</c> - <c>ksplacement_getmatrix.html</c>.
    /// History: docs/decisions/adapter-core.md#getmatrix3d-name</remarks>
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
    /// ENUMERATION IS VIA <c>ksDocument3D.PartCollection(TRUE)</c>, not <c>GetPart(n)</c>: <c>GetPart(1)</c>
    /// returned the component of the SECOND source and <c>GetPart(2)</c> returned null. The documented
    /// array is <c>PartCollection(refresh=true)</c> → <c>ksPartCollection</c>
    /// (<c>GetCount</c>/<c>GetByIndex</c>).
    /// </remarks>
    private static List<ksPart> ComponentParts5(DocumentEntry document)
    {
        var parts = new List<ksPart>();
        try
        {
            if (document.Document3D.PartCollection(true) is not ksPartCollection collection)
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
        var placement = document.Document3D.DefaultPlacement();
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

    // ── ASM-02 ──
    /// <summary>Insert a component from a file — the documented API7 <c>IPart7.Parts →
    /// IParts7.AddFromFile(FileName, ExternalFile=true, Redraw=true) → Part7</c>
    /// (<c>iparts7_addfromfile.html</c>), yielding components WITH GEOMETRY.</summary>
    /// <remarks>LIMIT: <c>ksDocument3D.CreatePartInAssembly</c> is MEASURABLY wrong (0 bodies, 0 faces — it
    /// CREATES a part, not inserts one). History: docs/decisions/assembly.md#insert-route</remarks>
    public InsertComponentResult InsertComponent(InsertComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        // INVARIANT: the frame is refused BEFORE AddFromFile, the point of no return of this route.
        // MEASURED: validating it where the matrix is built left the component inserted, the revision
        // unmoved and the answer a plain INVALID_ARGUMENT, so a partial effect read as "no change".
        if (command.Transform is not null)
        {
            TransformMath.FromDto(command.Transform);
        }

        // Snapshot BEFORE insertion: both the component count and their sources. With these, the
        // inserted instance is confirmed as the SINGLE new one, not as "the last in the list".
        var beforeParts = ComponentParts5(document);
        var beforeOrdinals = beforeParts.Count;
        var beforeNames = beforeParts.Select(p => Ref(() => p.fileName)).ToList();

        // COMPONENT INSERTION — the documented IParts7.AddFromFile (iparts7_addfromfile.html). Each
        // component gets 1 body / 6 faces and survives save→close→reopen, unlike CreatePartInAssembly.
        // History: docs/decisions/assembly.md#insert-route
        var bridge = BridgeFor(document);
        if (bridge.TransferTo7(document.Document3D) is not IKompasDocument3D document7)
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

        // THE INSERTED INSTANCE'S ADDRESS IS CONFIRMED, NOT "THE LAST IN THE COLLECTION":
        // `ksDocument3D.PartCollection(true)` (ksdocument3d_partcollection.html) enumerates components,
        // and insertion adds EXACTLY ONE. Two signals are checked AT ONCE: (1) the new source matched the
        // request; (2) the neighbour at the former ordinal kept ITS source — "last" alone is not enough.
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
            // revision and references stayed old, so the next call would mutate a model already
            // different.
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

        document.Document3D.RebuildDocument();
        BumpRevision(document, "assembly.insert_component");

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the revision is already bumped, so a client reading
        // only `status` must not take an unapplied placement for a completed one.
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
        // with two instances of one part, a name search would return the wrong instance. Its address is
        // found by matching (see AddressOf), so the row and the mutation route cannot disagree.
        // ranks: null names the insert case — the new instance is the LAST record of its file. The path is
        // empty because a top-level component's address is the ordinal, never a path.
        var inserted = top7 is null
            ? null
            : ReadComponent(document, top7, (IPart7)inserted7, parentRef: null, depth: 0, ranks: null,
                notes, path: []);

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
            // An unapplied placement or fixing was REFUSED above, so reaching here means both matched:
            // the level is not a second opinion on a check that already threw.
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified));
    }

    // ── ASM-04 ──
    /// <summary>Set a component's placement by a rigid transform and re-read it: API5
    /// <c>ksPlacement.InitByMatrix3D</c> + <c>ksPart.SetPlacement</c> + <c>ksPart.UpdatePlacement</c>.</summary>
    /// <remarks>INVARIANT: a NESTED component goes by API7 (<c>IPart7.Placement</c> →
    /// <c>IPlacement3D.InitByMatrix3D</c> → <c>IPart7.UpdatePlacement</c>) — it has no API5 ordinal — and
    /// there <c>transform</c> means the component's LOCAL frame, not document coordinates.
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    public SetComponentPlacementResult SetComponentPlacement(SetComponentPlacementCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var stored = RequireComponentReference(document, command.ComponentRef);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        if (ComponentPath.TryParse(command.ComponentRef, out var nestedPath))
        {
            return SetNestedPlacement(document, command, stored, nestedPath);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var matrix = MatrixOf(command.Transform);

        // AN UNREAD MATRIX IS NOT A ZERO MATRIX: substituting `Array.Empty<double>()` would report a
        // "zero placement" where the placement simply was not read (the same class as fix C).
        var beforeMatrix = ReadPlacementMatrix(part5);

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
            part5.SetPlacement(placement);
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

        document.Document3D.RebuildDocument();
        BumpRevision(document, "assembly.set_placement");

        var afterMatrix = ReadPlacementMatrix(part5);
        var matched = MatrixMatchesRequest(afterMatrix, matrix);

        // INVARIANT: success is named by the RE-READ, never by the API's BOOL return of the write.
        // DOC: kspart_setplacement.html and kspart_updateplacement.html both promise TRUE on success.
        // MEASURED: on an already-inserted component BOTH return FALSE while the placement IS applied
        // and re-read identical, so a check built on that return would report a failed write on every
        // success. A flag that is false on success is worse than no flag: the caller cannot tell it
        // from a real failure.
        // History: docs/decisions/adapter-core.md#set-placement-flag
        var checks = new List<NamedCheck>
        {
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

        return new SetComponentPlacementResult(
            ToDto(stored, "component"),
            beforeMatrix,
            afterMatrix,
            // A mismatched placement was REFUSED above: reaching here means the re-read matched.
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified));
    }

    /// <summary>A NESTED component's placement through the documented API7 route:
    /// <c>IPart7.Placement</c> → <c>IPlacement3D.InitByMatrix3D</c> → <c>IPart7.UpdatePlacement(Redraw)</c>,
    /// then the rebuild of every parent up to the assembly (<c>ipart7_placement.html</c>,
    /// <c>iplacement3d_initbymatrix3d.html</c>, <c>ipart7_updateplacement.html</c>).</summary>
    /// <remarks>MEASURED: <c>IPart7.Placement.GetMatrix3D</c> returns the LOCAL matrix the write took (the
    /// parent's origin is NOT added), and <c>UpdatePlacement</c> answers FALSE on an applied write — so
    /// success is named by the RE-READ only. History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private SetComponentPlacementResult SetNestedPlacement(
        DocumentEntry document, SetComponentPlacementCommand command, StoredReference stored,
        IReadOnlyList<int> path)
    {
        var matrix = MatrixOf(command.Transform);
        var node = ResolveNested(document, stored, path, out var chain, out var addressNote,
            out var unconfirmedAddress);
        var beforeMatrix = NestedPlacementMatrix(node);

        try
        {
            if (node.Placement is not { } placement || !placement.InitByMatrix3D(ToVariant(matrix)))
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "IPart7.Placement не вернул IPlacement3D или InitByMatrix3D ответил false: " +
                    "размещение вложенного компонента этим маршрутом не задаётся.",
                    RetryPolicy.Never);
            }

            node.UpdatePlacement(false);
        }
        catch (COMException ex)
        {
            // NOTHING IS BUMPED: the same rule as the API5 route — a write that broke off may have applied,
            // and the refusal says so; the references of this revision still address the same components.
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись размещения вложенного компонента прервалась: {ex.Message}. Размещение могло " +
                "измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["component_path"] = DescribePath(path),
                    ["placement_before"] = beforeMatrix,
                    ["placement_requested"] = matrix,
                });
        }

        RebuildAncestors(document, chain);
        BumpRevision(document, "assembly.set_placement.nested");

        var afterMatrix = NestedPlacementMatrix(node);
        var matched = MatrixMatchesRequest(afterMatrix, matrix);
        var checks = new List<NamedCheck>
        {
            new("placement_read_back_matches_request", matched,
                Observed: Describe(afterMatrix), Expected: Describe(matrix)),
        };

        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Размещение вложенного компонента записано, но ПЕРЕЧИТАННАЯ локальная матрица не " +
                $"совпала с запрошенной: запрос «{Describe(matrix)}», перечитано «{Describe(afterMatrix)}». " +
                "Успехом это не считается: модель изменена, размещение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["component_path"] = DescribePath(path),
                    ["placement_before"] = beforeMatrix,
                    ["placement_after"] = afterMatrix,
                    ["placement_requested"] = matrix,
                    ["failure_snapshot"] = new Dictionary<string, object?>
                    {
                        ["route"] = "api7:IPart7.Placement→InitByMatrix3D→UpdatePlacement",
                        ["component_path"] = DescribePath(path),
                        ["placement_before"] = beforeMatrix,
                        ["placement_after"] = afterMatrix,
                        ["update_placement_called"] = true,
                        ["revision_bumped"] = true,
                    },
                });
        }

        return new SetComponentPlacementResult(
            ToDto(stored, "component"),
            beforeMatrix,
            afterMatrix,
            new VerificationDto(VerificationLevel.StructureChecked, checks,
            [
                "nested_placement_is_local — " + NestedPlacementMeaning,
                "component_address_by_path — " + addressNote,
                .. unconfirmedAddress is null ? Array.Empty<string>() : new[] { unconfirmedAddress },
            ]));
    }

    // ── ASM-05 ──
    /// <summary>Replace a component's source while preserving placement.</summary>
    public ReplaceComponentResult ReplaceComponent(ReplaceComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        var stored = RequireComponentReference(document, command.ComponentRef);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        if (ComponentPath.TryParse(command.ComponentRef, out var nestedPath))
        {
            return ReplaceNestedSource(document, command, stored, nestedPath);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var sourceBefore = Ref(() => part5.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixBefore = ReadPlacementMatrix(part5);
        var countBefore = CountComponents(document);

        // SOURCE REPLACEMENT — the documented ksPart.SetFileName. DOC: kspart_setfilename.html, notes
        // verbatim: «Метод используется для компонентов, вставленных в сборку»; «Документ с указанным
        // именем должен существовать»; «Компонент не должен быть деталью из библиотеки моделей или
        // стандартным элементом»; «ИЗМЕНЕНИЕ ВСТУПАЕТ В СИЛУ ПОСЛЕ ВЫЗОВА МЕТОДА ksPart::Update» —
        // hence Update below. The shipped wrapper declares only a property, so the BOOL return is read
        // back through the getter ksPart.fileName. History: docs/decisions/assembly.md#replace-source
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

        document.Document3D.RebuildDocument();
        BumpRevision(document, "assembly.replace_component");

        // THE SAME INSTANCE IS RE-READ — by the SAME ORDINAL, not "the first with the same file": a name
        // search would read `sourceAfter`/`matrixAfter` from a DIFFERENT instance when the assembly
        // already had the new source.
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

    /// <summary>Replace the SOURCE of a NESTED component through the documented <c>IPart7.FileName</c>
    /// (put) + <c>IModelObject.Update</c> — the property takes effect only after that call
    /// (<c>ipart7_filename.html</c>: «Свойство вступает в силу после вызова метода IModelObject::Update»;
    /// «Имя файла можно установить только у компонента, вставленного в сборку»).</summary>
    /// <remarks>INVARIANT: the answer is named by the RE-READ of the source and of the local placement, not
    /// by <c>Update()</c>'s BOOL. INVARIANT: the reference keeps working across this operation — MEASURED:
    /// <c>IPart7.Reference</c> stays the same number after the replacement, so a second command with the
    /// SAME reference resolves the same instance.
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private ReplaceComponentResult ReplaceNestedSource(
        DocumentEntry document, ReplaceComponentCommand command, StoredReference stored,
        IReadOnlyList<int> path)
    {
        var node = ResolveNested(document, stored, path, out var chain, out var addressNote,
            out var unconfirmedAddress);
        var sourceBefore = Text(() => node.FileName);
        var matrixBefore = NestedPlacementMatrix(node);

        try
        {
            node.FileName = command.SourcePath;
        }
        catch (COMException ex)
        {
            BumpRevision(document, "assembly.replace_component.nested.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись IPart7.FileName вложенного компонента прервалась: {ex.Message}. Ссылка могла " +
                "измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["component_path"] = DescribePath(path),
                });
        }

        node.Update();
        RebuildAncestors(document, chain);

        var sourceAfter = Text(() => node.FileName);
        var matrixAfter = NestedPlacementMatrix(node);
        // THE FULL PATH IS COMPARED, not a file name: two files of one name are exactly the pair a name
        // comparison calls "replaced". The read-back value is the model's own answer, not the request.
        var sourceChanged = sourceAfter is { Length: > 0 } readBack
                            && ComponentAddress.SameSource(readBack, command.SourcePath);

        if (!sourceChanged)
        {
            // NOT A "CLEAN FAILURE": FileName and Update have already run, so a replay with the SAME
            // operation_id would apply the replacement a SECOND time.
            // History: docs/decisions/assembly.md#replace-partial
            BumpRevision(document, "assembly.replace_component.nested.unread", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Источник вложенного компонента НЕ заменён: после записи IPart7.FileName и Update() " +
                $"перечитывается «{sourceAfter ?? "null"}» вместо «{command.SourcePath}». Запись и Update() " +
                "уже вызваны, поэтому исход считается ЧАСТИЧНЫМ ЭФФЕКТОМ, а не чистым отказом.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["component_path"] = DescribePath(path),
                    ["source_path"] = command.SourcePath,
                    ["read_back"] = sourceAfter,
                    ["placement_before"] = matrixBefore,
                    ["placement_after"] = matrixAfter,
                    ["update_called"] = true,
                    ["revision_bumped"] = true,
                    ["failure_snapshot"] = new Dictionary<string, object?>
                    {
                        ["route"] = "api7:IPart7.FileName→IModelObject.Update",
                        ["component_path"] = DescribePath(path),
                        ["source_before"] = sourceBefore,
                        ["source_after"] = sourceAfter,
                        ["source_requested"] = command.SourcePath,
                    },
                });
        }

        BumpRevision(document, "assembly.replace_component.nested");

        var placementPreserved = matrixBefore is not null && matrixAfter is not null
                                 && MatricesEqual(matrixBefore, matrixAfter);
        var checks = new List<NamedCheck>
        {
            new("source_changed", sourceChanged,
                Observed: sourceAfter ?? "не читается", Expected: command.SourcePath),
            new("placement_preserved", placementPreserved,
                Observed: Describe(matrixAfter), Expected: Describe(matrixBefore)),
        };

        var unverified = new List<string>
        {
            "nested_placement_is_local — " + NestedPlacementMeaning,
            "component_address_by_path — " + addressNote,
        };
        if (unconfirmedAddress is not null)
        {
            unverified.Add(unconfirmedAddress);
        }

        if (!placementPreserved)
        {
            unverified.Insert(0, "placement_not_preserved — локальное размещение до и после не совпало " +
                                 "(или не читается): сохранение размещения не подтверждено");
        }

        return new ReplaceComponentResult(
            ToDto(stored, "component"),
            sourceBefore,
            sourceAfter,
            matrixBefore,
            matrixAfter,
            CountComponents(document),
            new VerificationDto(
                placementPreserved ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ── ASM-08 ──
    /// <summary>Delete a TOP-LEVEL assembly component through the documented
    /// <c>ksDocument3D.DeleteObject(obj)</c> (<c>ksdocument3d_deleteobject.html</c>: «Удалить объект:
    /// деталь, операцию, сопряжение»; «Удалить можно любой трехмерный объект»).</summary>
    /// <remarks>MEASURED (probe DC, docs/04 §4.78): <c>DeleteObject(ksPart)</c> returns true, the component
    /// count falls by one, and the OTHER components keep their placements. INVARIANT: the component's SOURCE
    /// FILE is not touched — the insertion into this assembly is removed, not the part file. INVARIANT: the
    /// ordinal of the remaining components shifts, so ALL references of this revision are revoked and the
    /// revision is bumped. History: docs/decisions/assembly.md#delete-component</remarks>
    public DeleteComponentResult DeleteComponent(DeleteComponentCommand command)
    {
        var document = RequireAssemblyForDelete(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        // NESTED COMPONENT AND A CONFIRMED ADDRESS ARE BOTH SETTLED HERE, BEFORE ANY COM MUTATION: a nested
        // component has no API5 address (CAPABILITY_UNAVAILABLE) and an unconfirmed ordinal would delete a
        // FOREIGN component (STALE_REFERENCE).
        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var sourcePath = Ref(() => part5.fileName);

        var notes = new List<string>();
        var dependents = ReadDependentMates(document, part5, notes,
            out var objectConstraintsCount, out var crossCheckNote);

        // THE REFUSAL COMES BEFORE COM MUTATES ANYTHING: with dependent mates and no agreement the model is
        // left exactly as it was, and the caller is told which mates would go.
        if (dependents.Count > 0 && !command.ConfirmDependents)
        {
            throw new KompasContractException(
                ErrorCodes.DependentFeatures,
                $"Компонент участвует в {dependents.Count} сопряжении(ях): удаление унесло бы их с собой. "
                + "Без confirm_dependents=true удаление не выполняется, состав сборки не меняется. "
                + "Перечень — в details.dependent_mates.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["dependent_mates"] = dependents,
                    ["dependent_mate_count"] = dependents.Count,
                    ["mates_related_from_kernel"] = objectConstraintsCount,
                    ["dependent_mate_route"] = crossCheckNote,
                    ["lookup_note"] = lookupNote,
                });
        }

        var before = SnapshotComponents(document);
        var mateCountBefore = MateConstraintCount(document, "до удаления компонента");
        var addressedKey = before.Count > payload.Ordinal && payload.Ordinal >= 0
            ? before[payload.Ordinal].Key
            : null;

        bool deleted;
        try
        {
            deleted = document.Document3D.DeleteObject(part5);
        }
        catch (COMException ex)
        {
            // A failure DURING the mutation: the outcome is unknown, not "it cleanly did not work".
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"DeleteObject прервался: {ex.Message}. Сверьте фактический состав сборки, повтор той же "
                + "операции запрещён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        SettleAfterMutation(document);

        // THE ADDRESSED COMPONENT'S REFERENCE IS BUILT BEFORE THE REFERENCES ARE REVOKED: InvalidateDocument
        // drops every reference of the document, and a Require afterwards would reject the very reference
        // the caller used.
        var dto = ToDto(stored, "component");
        References.InvalidateDocument(document.Id);
        BumpRevision(document, "assembly.delete_component");

        var after = SnapshotComponents(document);
        var mateCountAfter = MateConstraintCount(document, "после удаления компонента");

        // WHICH ONE LEFT IS DECIDED BY IDENTITY (source file + placement), NOT BY THE ORDINAL: the remaining
        // components' ordinals shift, so "index 0 is gone" would be a statement about the numbering, not
        // about the component. The removed entry is the multiset difference before minus after.
        // THE DECISION IS THE PURE ComponentRemoval (Domain, no COM types): the ordinal is not identity, and
        // the multiset difference is what says which entry left. The keys carry the file and the rounded
        // placement, so the message names what actually left.
        var removal = ComponentRemoval.Decide(
            before.Select(e => e.Key).ToList(), after.Select(e => e.Key).ToList(), addressedKey);
        var left = removal.Left;
        var removedIsAddressed = removal.AddressedRemoved;
        var countFell = removal.CountFell;
        var ambiguous = removal.Ambiguous;
        var addressedOccurrences = addressedKey is null ? 0 : before.Count(e => e.Key == addressedKey);

        // SURVIVORS' PLACEMENTS: the entries still listed after the deletion must be the earlier ones
        // unchanged, so no entry at a NEW placement may appear. INVARIANT: identity carries the placement, so
        // the addressed claim already rests on this fact; stated on its own it says what its name says
        // instead of repeating the conjunction of the two checks beside it.
        var survivorsMoved = removal.Appeared.Count > 0;
        var survivorsPreserved = countFell && !survivorsMoved;

        var sourceStillThere = sourcePath is { Length: > 0 } && SafeFileExists(sourcePath);

        var expectedMatesAfter = dependents.Count > 0
            ? mateCountBefore - dependents.Count
            : mateCountBefore;
        var mateFateMatches = mateCountAfter == expectedMatesAfter;

        var checks = new List<NamedCheck>
        {
            new("delete_object_returned", deleted, Observed: deleted ? "true" : "false"),
            new("component_count_decreased", countFell,
                Observed: $"{before.Count} → {after.Count}", Expected: $"{before.Count - 1}"),
            new("addressed_component_removed", removedIsAddressed,
                Observed: $"ушло [{string.Join("; ", left)}]",
                Expected: "ровно адресованный (файл + размещение)"),
            new("survivors_placements_preserved", survivorsPreserved,
                Observed: string.Join("; ", after.Select(e => e.Summary())),
                Expected: "уцелевшие — те же записи (файл + размещение), что были до удаления"),
            new("dependent_mates_removed_with_component", mateFateMatches,
                Observed: $"сопряжений {mateCountBefore} → {mateCountAfter}",
                Expected: $"{expectedMatesAfter}"),
            new("source_file_untouched", sourceStillThere,
                Observed: sourcePath ?? "источник не читается",
                Expected: "файл детали существует и не удаляется"),
        };

        var unverified = new List<string>
        {
            "component_address_by_ordinal — " + lookupNote,
            "dependent_mate_route — " + crossCheckNote,
        };

        if (ambiguous)
        {
            unverified.Add("addressed_instance_indistinguishable — в сборке " + addressedOccurrences
                + " компонента(ов) с ТЕМ ЖЕ источником и ТЕМ ЖЕ размещением: «ушёл именно адресованный» "
                + "проверкой по идентичности неотличимо от «ушёл его двойник». Сверьте состав.");
        }

        if (!removedIsAddressed || !countFell)
        {
            unverified.Insert(0, "component_not_removed — удаление не подтверждено перечитыванием состава: "
                + $"было {before.Count}, стало {after.Count}, ушло [{string.Join("; ", left)}]");
        }

        if (!mateFateMatches)
        {
            unverified.Insert(0, "dependent_mate_fate_unexpected — сопряжений до " + mateCountBefore
                + ", зависимых " + dependents.Count + ", после " + mateCountAfter
                + ": ожидалось " + expectedMatesAfter + ". Судьба зависимых сопряжений не подтверждена.");
        }

        if (survivorsMoved && dependents.Count > 0)
        {
            unverified.Add("survivor_placement_changed_with_mates — размещение уцелевшего компонента "
                + "изменилось, а в сборке были зависимые сопряжения: ядро сняло сопряжение вместе с "
                + "компонентом, и пересчёт мог двинуть остальные. Это НАЗВАНО, а не выдано за "
                + "«размещения не изменились»");
        }
        else if (survivorsMoved)
        {
            unverified.Add("survivor_placement_changed — после удаления в перечне появились записи "
                + "(файл + размещение), которых до него не было: уцелевшие компоненты остались не на "
                + "своих местах, хотя зависимых сопряжений в сборке не было. Это НАЗВАНО, а не выдано "
                + "за «размещения не изменились»");
        }

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the revision is already bumped, so a client reading only
        // `status` must not take an unconfirmed deletion for a completed one.
        if (!deleted || !countFell || !removedIsAddressed)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Удаление компонента НЕ подтверждено перечитыванием состава сборки: "
                + $"DeleteObject={deleted}, компонентов {before.Count} → {after.Count}, "
                + $"ушло [{string.Join("; ", left)}]. Успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["components_before"] = before.Count,
                    ["components_after"] = after.Count,
                    ["removed"] = left.ToList(),
                    ["expected_removed"] = addressedKey,
                });
        }

        var disposition = dependents.Count == 0
            ? "сопряжений у компонента не было: удалять вместе с ним нечего"
            : mateFateMatches
                ? $"ядро удалило {dependents.Count} зависимое(ых) сопряжение(ий) ВМЕСТЕ с компонентом "
                  + $"(сопряжений {mateCountBefore} → {mateCountAfter}); недействительных остатков не осталось"
                : $"сопряжений было {mateCountBefore}, зависимых {dependents.Count}, осталось {mateCountAfter}: "
                  + "ожидаемое «минус зависимые» НЕ подтвердилось — сверьте сопряжения";

        return new DeleteComponentResult(
            dto,
            sourcePath,
            dependents,
            disposition,
            before.Count,
            after.Count,
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified),
            Warnings: notes.Count == 0 ? null : notes);
    }

    /// <summary>Whether the document is an assembly, refusing a wrong kind with <c>WRONG_DOCUMENT_KIND</c>.</summary>
    /// <remarks>DELIBERATELY DIFFERENT FROM THE SIBLING C1 TOOLS: <see cref="RequireAssembly"/> answers
    /// <c>INVALID_ARGUMENT</c> for the same condition, and the interference block answered
    /// <c>WRONG_DOCUMENT_KIND</c>. The order for component deletion names <c>WRONG_DOCUMENT_KIND</c>, and the
    /// divergence is kept rather than smoothed over — a code that silently changed under one tool while the
    /// others kept theirs would be worse. History: docs/decisions/contracts.md#wrong-document-kind</remarks>
    private DocumentEntry RequireAssemblyForDelete(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{documentId}» имеет тип {document.Kind}: удалять компонент можно только в "
                + "документе-сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                });
        }

        return document;
    }

    /// <summary>Mates the component takes part in, read by TWO documented routes that must agree.</summary>
    /// <remarks>MEASURED (probe DC): the collection walk matches by RCW identity of
    /// <c>BaseObject1/2</c> → <c>IModelObject.Part</c>, because two insertions of one file share both name
    /// and source file. <c>ObjectConstraints</c> is the kernel's own answer, so the two together make an
    /// UNDER-COUNT visible: a walk that misses a mate would otherwise delete a component while a mate is
    /// still attached. The agreement rule is the pure <see cref="DependentMates.Decide"/>.
    /// History: docs/decisions/assembly.md#delete-component</remarks>
    /// <param name="objectConstraintsCount">Mates the kernel reports, or <c>null</c> when unread.</param>
    private List<MateRowDto> ReadDependentMates(
        DocumentEntry document,
        ksPart part5,
        List<string> notes,
        out int? objectConstraintsCount,
        out string crossCheckNote)
    {
        var rows = ReadMates(document, notes);
        var dependents = new List<MateRowDto>();

        var component7 = BridgeFor(document).TransferTo7(part5) as IPart7;
        if (component7 is null)
        {
            // WITHOUT THE API7 VIEW OF THE COMPONENT THE OWNER CANNOT BE COMPARED AT ALL: an empty list would
            // read as "no dependents", which is exactly the dangerous direction. Refuse instead.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Компонент не переносится в API7 как IPart7: владельца базовых объектов сопряжений "
                + "сравнить нечем, и «сопряжений нет» выдавать нельзя. Удаление не выполняется.",
                RetryPolicy.ReacquireContext);
        }

        foreach (var row in rows)
        {
            var storedMate = References.Require(row.MateRef, document.Id, document.Revision);
            if (storedMate.Payload is not MatePayload matePayload)
            {
                continue;
            }

            if (MateTouchesComponent(document, matePayload.Mate5, component7, out var which))
            {
                dependents.Add(row);
                notes.Add($"dependent_mate — сопряжение «{row.Name ?? "?"}» (номер {row.Ordinal}) "
                    + $"участвует базовым объектом {which}");
            }
        }

        objectConstraintsCount = KernelMateCount(document, component7, notes);

        // THE AGREEMENT IS THE PURE DependentMates (Domain, no COM types). A disagreement is a REFUSAL, never
        // a preference for the smaller set: the walk can UNDER-count, and deleting on a short list would
        // remove a component while a mate is still attached.
        var agreement = DependentMates.Decide(dependents.Count, objectConstraintsCount);
        crossCheckNote = agreement.Detail;

        if (!agreement.Agree && objectConstraintsCount is not null)
        {
            // THE KERNEL SAYS MORE (OR FEWER) MATES THAN THE REFERENCE SCAN FOUND. Deleting on the smaller
            // set could remove a component while a mate is still attached, so the call refuses.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Перечень зависимых сопряжений НЕ подтверждён: перебор коллекции дал " + dependents.Count
                + ", а ядро (IMateConstraints3D.ObjectConstraints) — " + objectConstraintsCount
                + ". Удаление по меньшему набору могло бы унести компонент при живом сопряжении, "
                + "поэтому оно не выполняется.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["by_collection_walk"] = dependents.Count,
                    ["by_object_constraints"] = objectConstraintsCount,
                });
        }

        return dependents;
    }

    /// <summary>Whether the mate's base objects belong to the component — by RCW identity of
    /// <c>IModelObject.Part</c>.</summary>
    /// <param name="which">1 or 2: which base object matched (0 when none).</param>
    private bool MateTouchesComponent(
        DocumentEntry document, ksMateConstraint mate5, IPart7 component7, out int which)
    {
        which = 0;
        var bridge = BridgeFor(document);
        for (short index = 1; index <= 2; index++)
        {
            var baseObject = Ref(() => mate5.GetBaseObj(index));
            if (baseObject is null)
            {
                continue;
            }

            if (bridge.TransferTo7(baseObject) is not IModelObject model)
            {
                continue;
            }

            // TWO ADDRESSING SCHEMES, ONE QUESTION: an ordinary mate names a FACE, so its owner is
            // `IModelObject.Part`; a dependent position names the COMPONENT itself, so the object IS the
            // component. Matching only the face owner read the second kind as "no component" (measured).
            if (ReferenceEquals(model, component7)
                || (Ref(() => model.Part) is { } owner && ReferenceEquals(owner, component7)))
            {
                which = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>How many mates the kernel reports for the component via
    /// <c>IMateConstraints3D.ObjectConstraints</c>; <c>null</c> when that read failed.</summary>
    private int? KernelMateCount(DocumentEntry document, IPart7 component7, List<string> notes)
    {
        var top = TopPart7(document, notes);
        var mates = top?.MateConstraints;
        if (mates is null || component7 is not IModelObject model)
        {
            return null;
        }

        try
        {
            var raw = mates.ObjectConstraints[model];
            return raw switch
            {
                null => 0,
                Array array => array.Length,
                _ => 1,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            notes.Add("object_constraints_failed — IMateConstraints3D.ObjectConstraints бросил "
                + ex.GetType().Name + ": перекрёстная проверка не выполнена");
            return null;
        }
    }

    /// <summary>One component of the structure as the deletion compares it: source file and placement.</summary>
    /// <remarks>Equality is by (file name, rounded matrix): the ordinal is NOT identity — it shifts for the
    /// survivors. Two insertions of one file at ONE placement are indistinguishable here, and that limit is
    /// NAMED rather than smoothed over.</remarks>
    private sealed record ComponentSnapshotEntry(string File, double[]? Matrix)
    {
        public string Key => File + "|" + (Matrix is null
            ? "матрица не читается"
            : string.Join(",", Matrix.Select(v => Math.Round(v, 6).ToString(CultureInfo.InvariantCulture))));

        public string Summary() => $"«{File}» {Describe(Matrix)}";
    }

    /// <summary>The structure as a list of (source file, placement) per ordinal, in enumeration order.</summary>
    private List<ComponentSnapshotEntry> SnapshotComponents(DocumentEntry document)
    {
        var entries = new List<ComponentSnapshotEntry>();
        foreach (var part in ComponentParts5(document))
        {
            var file = Path.GetFileName(Ref(() => part.fileName) ?? "источник не читается");
            entries.Add(new ComponentSnapshotEntry(file, ReadPlacementMatrix(part)));
        }

        return entries;
    }

    // ── ASM-06 ──
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

    // ── helpers ──
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

    /// <summary>The reference a component command is allowed to resolve: a component of THIS document.</summary>
    /// <remarks>INVARIANT: a reference minted in ANOTHER document is refused as CAPABILITY_UNAVAILABLE, not
    /// as a stale handle — nothing about it is out of date, and re-reading this document will never make it
    /// usable. A reference of this document with an older revision stays STALE_REFERENCE (the general rule
    /// of the registry, <see cref="ReferenceRegistry.Require"/>).
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private StoredReference RequireComponentReference(DocumentEntry document, string componentRef)
    {
        if (References.TryGet(componentRef, out var known) && known is not null
            && !string.Equals(known.DocumentId, document.Id, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Ссылка «{componentRef}» выпущена для ДРУГОГО документа: компонент принадлежит документу "
                + $"«{known.DocumentId}», а команда назвала «{document.Id}». Документ сам не заменяется: "
                + "перечитайте структуру kompas_list_components того документа, в котором компонент "
                + "должен правиться.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["reference_id"] = componentRef,
                    ["reference_document_id"] = known.DocumentId,
                    ["document_id"] = document.Id,
                    ["supported"] = "компонент того же документа",
                });
        }

        return References.Require(componentRef, document.Id, document.Revision);
    }

    /// <summary>What a nested placement matrix means — the wording every answer carries.</summary>
    /// <remarks>MEASURED (probe `nested-address`): <c>IPart7.Placement.GetMatrix3D</c> gives the LOCAL frame
    /// of the component inside its parent, and <c>GetSummMatrix(parent, child)</c> answers the same numbers,
    /// so the parent's own origin is NOT added.</remarks>
    private const string NestedPlacementMeaning =
        "матрица вложенного компонента — ЛОКАЛЬНАЯ система координат в его родителе (IPart7.Placement → "
        + "IPlacement3D.GetMatrix3D), а не координаты документа: начало родителя в неё не входит";

    /// <summary>The live object a NESTED reference names, resolved by its PATH of indices from the top
    /// assembly through the documented recursion <c>IPart7.Parts</c> → <c>IParts7.Part(index)</c>.</summary>
    /// <param name="chain">Top assembly down to the component itself — every ancestor is rebuilt after a
    /// write, so the path is resolved ONCE and the same objects carry the change.</param>
    /// <remarks>INVARIANT: the path is resolved, never guessed: a level that answers nothing, or a resolved
    /// instance whose <c>IPart7.Reference</c> differs from the one the reference was minted with, is
    /// STALE_REFERENCE — the same code an API5 component with an unconfirmed ordinal gets, and for the same
    /// reason: a mutation by a guessed address would hit a FOREIGN component.
    /// INVARIANT: a component whose READ load state is not «загружен полностью» is refused
    /// CAPABILITY_UNAVAILABLE (`ksloadstateenum.html`: «Компонент не загружен — не отображается в окне
    /// документа. Редактировать нельзя»).
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private IPart7 ResolveNested(
        DocumentEntry document, StoredReference stored, IReadOnlyList<int> path,
        out List<IPart7> chain, out string note, out string? unconfirmed)
    {
        var payload = stored.Payload as ComponentPayload;
        var notes = new List<string>();
        var top = TopPart7(document, notes);
        chain = new List<IPart7>();
        if (top is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "API7-представление сборки не получено: вложенный компонент адресуется путём по "
                + "IPart7.Parts, а без него адреса нет. " + string.Join("; ", notes),
                RetryPolicy.ReacquireContext);
        }

        chain.Add(top);
        var node = top;
        for (var level = 0; level < path.Count; level++)
        {
            var child = ChildAt(node, path[level]);
            if (child is null)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Адрес вложенного компонента не найден: путь {DescribePath(path)} на уровне {level} "
                    + $"индекс {path[level]} не дал компонента (IParts7.Part ответил null). Компонент мог "
                    + "быть удалён или структура изменилась — перечитайте kompas_list_components и "
                    + "возьмите свежую ссылку.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["component_path"] = DescribePath(path),
                        ["level"] = level,
                        ["index"] = path[level],
                    });
            }

            node = child;
            chain.Add(child);
        }

        // THE INSTANCE IS CONFIRMED, NOT ASSUMED: an index addresses a POSITION, and a position survives a
        // structure change that the reference does not. The decision is the PURE rule in Domain.
        var resolvedReference = Int(() => node.Reference);
        var verdict = NestedComponentAddress.Decide(
            payload?.Reference, resolvedReference, ReadEnumName(() => node.LoadState));
        if (verdict.Kind != NestedAddressVerdictKind.Addressable)
        {
            throw new KompasContractException(
                verdict.Kind == NestedAddressVerdictKind.StaleReference
                    ? ErrorCodes.StaleReference
                    : ErrorCodes.CapabilityUnavailable,
                verdict.Detail,
                verdict.Kind == NestedAddressVerdictKind.StaleReference
                    ? RetryPolicy.ReacquireContext
                    : RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_path"] = DescribePath(path),
                    ["reference_minted"] = payload?.Reference,
                    ["reference_resolved"] = resolvedReference,
                });
        }

        note = $"путь {DescribePath(path)}: {verdict.Detail}";
        unconfirmed = verdict.Unconfirmed;
        return node;
    }

    /// <summary>The child of a node at an index of the documented recursive collection
    /// <c>IPart7.Parts</c> (<c>iparts7_part.html</c>: «VT_I4 - индекс компонента»).</summary>
    private static IPart7? ChildAt(IPart7 node, int index)
    {
        try
        {
            return node.Parts?.get_Part(index) as IPart7;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>A component's LOCAL placement: <c>IPart7.Placement</c> →
    /// <c>IPlacement3D.GetMatrix3D</c>, 16 numbers. <c>null</c> — not read, never zeros.</summary>
    /// <remarks>DOC: <c>ipart7_placement.html</c> («Получить локальную систему координат компонента»),
    /// <c>iplacement3d_getmatrix3d.html</c> («массив координат SAFEARRAY double (VT_ARRAY | VT_R8)»).
    /// MEASURED: the returned matrix is exactly what <c>InitByMatrix3D</c> wrote.</remarks>
    private static double[]? NestedPlacementMatrix(IPart7 part)
    {
        try
        {
            return part.Placement is { } placement ? Matrix16(placement.GetMatrix3D()) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Rebuild the path's ANCESTORS from the nearest one up, then the document — the order the
    /// route was measured with. The BOOL of <c>RebuildModel</c> is not a success claim: the write is
    /// confirmed by the re-read matrix, and a rebuild that answers false still gets its build from
    /// <c>ksDocument3D.RebuildDocument()</c>.</summary>
    /// <remarks>DOC: <c>ipart7_rebuildmodel.html</c>, <c>ksdocument3d_rebuilddocument.html</c>.
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private static void RebuildAncestors(DocumentEntry document, List<IPart7> chain)
    {
        for (var index = chain.Count - 2; index >= 0; index--)
        {
            try
            {
                chain[index].RebuildModel(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // The document rebuild below is the build the comparison relies on.
            }
        }

        document.Document3D.RebuildDocument();
    }

    /// <summary>A path of indices, as an answer names it.</summary>
    private static string DescribePath(IReadOnlyList<int> path) => string.Join("/", path);

    /// <summary>The source files of the SUBASSEMBLIES this assembly holds, at any depth — the files a save
    /// of this assembly can write on its own.</summary>
    /// <remarks>MEASURED (probe `nested-address`): saving ONLY the parent assembly rewrites the file of the
    /// subassembly whose component was changed (size and write time move), so a save that does not name
    /// those files hides a change on disk from its caller.
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private List<string> NestedSourceFiles(DocumentEntry document, List<string> notes)
    {
        var files = new List<string>();
        var top = TopPart7(document, notes);
        if (top is not null)
        {
            CollectSubassemblyFiles(top, depth: 0, files, notes);
        }

        return files;
    }

    private void CollectSubassemblyFiles(IPart7 node, int depth, List<string> files, List<string> notes)
    {
        if (depth > MaxStructureDepth)
        {
            notes.Add($"subassembly_files_depth_limit — обход подсборок остановлен на глубине "
                      + $"{MaxStructureDepth}");
            return;
        }

        foreach (var child in ChildrenOf(node, notes))
        {
            // A DETAIL carries no file of its own to write: only a subassembly is saved together with its
            // parent. An unread flag leaves the file out AND is named, not silently treated as "a part".
            var detail = Bool(() => child.Detail);
            if (detail is null)
            {
                notes.Add($"subassembly_flag_unread — у компонента «{Text(() => child.Name) ?? "?"}» признак "
                          + "«деталь/сборка» не прочитан: его файл не сверяется с записанными");
                continue;
            }

            if (detail != false)
            {
                continue;
            }

            var path = Text(() => child.FileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                notes.Add($"subassembly_file_unread — у подсборки «{Text(() => child.Name) ?? "?"}» файл не "
                          + "прочитан: запись его файла сохранением верхней сборки не сверяется");
                continue;
            }

            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(path);
            }

            CollectSubassemblyFiles(child, depth + 1, files, notes);
        }
    }

    /// <summary>How many control rebuilds a save of an assembly that HOLDS a subassembly waits through.</summary>
    private const int NestedRebuildAttempts = 3;

    /// <summary>The pause between those rebuilds.</summary>
    private const int NestedRebuildPauseMs = 300;

    /// <summary>Wait for the kernel's rebuild by BUDGET before a save of an assembly that holds nested
    /// components.</summary>
    /// <remarks>DOC: <c>ipart7_needrebuild.html</c> promises a flag, but MEASURED: it is ABSENT from
    /// <c>Interop.KompasAPI7.dll</c> (compile error CS1061), so there is nothing to poll and the wait is a
    /// named budget. MEASURED: a save raced against a rebuild answers with the OLD placement, so the wait
    /// stands before the call. Only a save whose document HAS a subassembly pays it.
    /// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
    private void WaitForNestedRebuild(DocumentEntry document, List<string> notes)
    {
        try
        {
            for (var attempt = 0; attempt < NestedRebuildAttempts; attempt++)
            {
                document.Document3D.RebuildDocument();
                System.Threading.Thread.Sleep(NestedRebuildPauseMs);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // A wait that failed is NAMED: the save still runs, and its file stamps stay the proof.
            notes.Add($"rebuild_before_nested_save_failed — перестроение перед сохранением бросило "
                      + $"{ex.GetType().Name}: файлы подсборок могут не содержать правку");
        }
    }

    private IPart7? TopPart7(DocumentEntry document, List<string> notes)    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
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
    /// The address is the ORDINAL in the flat <c>ksDocument3D.PartCollection(true)</c>, because
    /// <c>IPart7.Reference</c> is not a component number. The <c>ksPartCollection</c>↔<c>IPart7.PartsEx</c>
    /// order is an ASSUMPTION, so identity is checked before a mutation (<see cref="IdentityMatches"/>).
    /// </remarks>
    private ksPart ComponentPart5(DocumentEntry document, ComponentPayload payload, out string note,
        string operation = "мутация")
    {
        if (payload.Ordinal == NestedComponentOrdinal)
        {
            // NESTED COMPONENT: no API5 address. A mutation "by a guessed ordinal" is forbidden — it would
            // hit a FOREIGN component, because PartCollection enumerates components flatly. Placement and
            // source replacement do NOT come here: they go by the API7 route (see SetNestedPlacement).
            note = "вложенный компонент: адрес API5 неприменим (PartCollection перечисляет компоненты плоско)";
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Компонент вложен в подсборку, и этот маршрут идёт через API5, где адреса у вложенного " +
                "компонента нет: ksDocument3D.PartCollection(true) перечисляет компоненты сборки ПЛОСКО, " +
                "поэтому порядковый номер вложенного компонента адресом не является. Поэтому " + operation +
                " вложенного компонента не выполняется. Размещение и замена источника вложенного " +
                "компонента работают маршрутом API7 (kompas_set_component_placement, " +
                "kompas_replace_component); для остальных операций работайте с компонентом верхнего " +
                "уровня.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["api7_reference"] = payload.Reference,
                    ["ordinal"] = payload.Ordinal,
                    ["supported"] = "компоненты верхнего уровня (Depth=0); API7-маршрут: размещение и "
                                    + "замена источника",
                });
        }

        if (payload.Ordinal == UnaddressedComponentOrdinal)
        {
            // NO ADDRESS, NOT A NEIGHBOUR'S: the record matched no collection element unambiguously
            // (see ComponentAddress), and any index would be a guess into a FOREIGN component.
            note = "адрес не подтверждён сопоставлением с ksDocument3D.PartCollection(true)";
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                "Адрес компонента НЕ подтверждён: ни полный путь файла-источника, ни размещение не "
                + "отличили эту запись от остальных экземпляров сборки, поэтому " + operation
                + " по ней не выполняется — любой номер был бы догадкой и попал бы в ЧУЖОЙ компонент. "
                + ComponentAddress.Rule
                + " Перечитайте структуру сборки kompas_list_components и возьмите свежую ссылку.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["api7_reference"] = payload.Reference,
                    ["ordinal"] = payload.Ordinal,
                    ["identity_confirmed"] = "не сопоставлено",
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
            // PURE `ComponentIdentity.Decide` (Domain): ONLY THE SOURCE decides, name and matrix are notes.
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
                          "компонент (расходится файл-источник). Поэтому " + operation +
                          " по неподтверждённому номеру не выполняется: она попала бы в ЧУЖОЙ " +
                          "компонент. Перечитайте структуру сборки kompas_list_components и возьмите " +
                          "свежую ссылку."
                        : "Адрес компонента НЕ подтверждён: файл-источник компонента не прочитан с " +
                          "одной из сторон, а имя и матрица размещения отказом не управляют, поэтому " +
                          "подтвердить адрес НЕЧЕМ. " + operation + " по НЕПОДТВЕРЖДЁННОМУ адресу не " +
                          "выполняется — перечитайте структуру сборки kompas_list_components и " +
                          "возьмите свежую ссылку.",
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

    /// <summary>Whether this is the same component: THREE signals compared, ONLY THE SOURCE DECIDES THE
    /// REFUSAL.</summary>
    /// <returns><c>true</c> — the source matched; <c>false</c> — it differs; <c>null</c> — nothing to
    /// compare.</returns>
    /// <remarks>DECISION IS A PURE FUNCTION (<see cref="ComponentIdentity.Decide"/>, Domain, no COM types);
    /// name and MUTABLE matrix are NOTES. INVARIANT: the source is compared as the FULL PATH, normalised and
    /// case-insensitive — two different files of one name are exactly the pair a name comparison confirms.
    /// History: docs/decisions/assembly.md#identity</remarks>
    private bool? IdentityMatches(ksPart part5, ComponentPayload payload, out string detail)
    {
        var from5 = Ref(() => part5.fileName);
        var from7 = Text(() => payload.Part7.FileName);

        var componentName5 = Ref(() => part5.name);
        var componentName7 = Text(() => payload.Part7.Name);

        // BOTH MATRICES AT ONE POINT IN TIME. API5 — by ordinal; API7 — from the LIVE parent, re-read
        // here rather than taken as a snapshot from the reference (see ComponentPayload).
        var matrix5 = ReadPlacementMatrix(part5);
        var matrix7 = payload.Parent7 is null ? null : SummMatrix7(payload.Parent7, payload.Part7);

        var sourceSignal = string.IsNullOrWhiteSpace(from5) || string.IsNullOrWhiteSpace(from7)
            ? ComponentIdentitySignal.NotRead
            : ComponentAddress.SameSource(from5, from7)
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
            + $" [прочитано: источник по номеру «{from5 ?? "не читается"}», ссылка «{from7 ?? "не читается"}»; "
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

    /// <summary>Placement matrix of a part, in document coordinates; <c>null</c> - not read.</summary>
    /// <remarks>DOC: <c>ksplacement_getmatrix.html</c> - «GetMatrix - Получить суммарную матрицу
    /// преобразования координат»: «Элементы матрицы возвращаются в виде одномерного массива из 16
    /// элементов. Матрица имеет размер 4х4». The type library spells the SAME member
    /// <c>GetMatrix3D</c> while its neighbour keeps the suffix in both (<c>InitByMatrix3D</c>), so the
    /// page is cited under its own name. History: docs/decisions/adapter-core.md#getmatrix3d-name</remarks>
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
    /// <c>[X, 0][Y, 0][Z, 0][translation, 1]</c>. A pure translation cannot tell the packing apart, so
    /// the discriminating control must be a rotation.
    /// </remarks>
    private static double[] MatrixOf(TransformDto transform)
    {
        // INVARIANT: the frame is validated BEFORE a matrix exists. MEASURED: an axis of length 2
        // reaches the kernel as a scaled basis, the write lands and the revision moves, so the refusal
        // arrives as VERIFICATION_FAILED after a partial write instead of INVALID_ARGUMENT before it.
        var frame = TransformMath.FromDto(transform);
        var (ox, oy, oz) = Vec3(frame.OriginMm);
        var (xx, xy, xz) = Vec3(frame.XMm);
        var (yx, yy, yz) = Vec3(frame.YMm);
        var (zx, zy, zz) = Vec3(frame.ZMm);
        return
        [
            xx, xy, xz, 0,
            yx, yy, yz, 0,
            zx, zy, zz, 0,
            ox, oy, oz, 1,
        ];
    }

    /// <returns>A 16-number matrix, or <c>null</c> if the value was NOT READ (returning zeros would turn
    /// "not read" into "at the origin").</returns>
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
    /// <remarks>12 significant elements are compared: three rotation rows (0…2, 4…6, 8…10) and the
    /// translation (12…14). Comparing ONLY the translation would let an unapplied rotation read back as
    /// confirmed.</remarks>
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

    private static bool MatricesEqual(double[]? a, double[]? b) => ComponentAddress.SameMatrix(a, b);

    private static string Describe(double[]? matrix) =>
        matrix is null || matrix.Length < 16
            ? "не читается"
            : $"origin=({matrix[12]:0.####}, {matrix[13]:0.####}, {matrix[14]:0.####})";

    private static (double X, double Y, double Z) Vec3(IReadOnlyList<double> v) =>
        (v.Count > 0 ? v[0] : 0, v.Count > 1 ? v[1] : 0, v.Count > 2 ? v[2] : 0);

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

    private static double? Double(Func<double> read) => SafeRead.Double(read);

    /// <summary>The name of an enum value; "not read" is printed AS A WORD, not as the first enum value.</summary>
    private static string ReadEnumName<T>(Func<T> read) where T : struct, Enum => SafeRead.EnumName(read);

    /// <summary>An already-nullable enum value (e.g. <c>obj?.Alignment</c>): <c>null</c> — NOT read. An unread
    /// value is not replaced by the first enum value.</summary>
    private static T? EnumOrNull<T>(Func<T?> read) where T : struct, Enum => SafeRead.EnumOrNull(read);
}
