using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Com;

namespace KompasMcp.Api5Adapter;

/// <summary>«Копия геометрии»: geometry copied from another part's file and kept tied to that source
/// (order <c>OMEGA_G8_MASTER_GEOMETRY</c>, profile <c>master-geometry-minimal-v1</c>).</summary>
/// <remarks>DOC: <c>imodelcontainer_copiesgeometry.html</c> (<c>CopiesGeometry</c>, read-only) →
/// <c>icopiesgeometry_add.html</c> (<c>Add()</c> → <c>ICopyGeometry</c>);
/// <c>icopygeometry_addinitialobjectsfromexternaldocument.html</c> (source document, its local CS, a
/// <c>SafeArray</c> of <c>IModelObject</c>); <c>icopygeometry_watchforsourcechange.html</c>;
/// <c>icopygeometry_synhronise.html</c>; <c>icopygeometry_documentfilename.html</c> (read-only);
/// <c>imodelobject_update.html</c> (the Update that commits the properties).
/// INVARIANT: a copy is confirmed by re-reading the receiver's bodies/volume and the collection back —
/// never by the BOOL a call returned. History: docs/decisions/master-geometry.md#copy-geometry-route</remarks>
public partial class Api5Session
{
    private const short PlaneXoy = 1;

    /// <summary>Creates one «Копия геометрии» operation in the receiver from a saved part file.</summary>
    public CopyGeometryResult CopyGeometry(CopyGeometryCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        RequirePart(document);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        if (document.Path is not null && PathsEqual(document.Path, command.SourcePath))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Источник совпадает с приёмником: копировать документ в себя нечего.",
                RetryPolicy.SameOperationId);
        }

        var kind = command.ObjectKind;
        if (kind is not ("body" or "plane_xoy"))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"object_kind «{kind}» не поддерживается: измерены body и plane_xoy.",
                RetryPolicy.SameOperationId);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision)
                        as IModelContainer
                        ?? throw new KompasContractException(
                            ErrorCodes.CapabilityUnavailable,
                            "IModelContainer приёмника не получен: "
                            + (bridge.BridgeFailure ?? "причина не известна"),
                            RetryPolicy.ReacquireContext);
        var collection = container.CopiesGeometry
                         ?? throw new KompasContractException(
                             ErrorCodes.CapabilityUnavailable,
                             "IModelContainer.CopiesGeometry → null: операции копий недоступны.",
                             RetryPolicy.ReacquireContext);

        var bodiesBefore = CountBodies(document);
        var volumeBefore = ReadVolume(document);
        var source = OpenCopySource(document, bridge, command.SourcePath, kind, out var closeAfter);
        try
        {
            // DOC: icopygeometry_contextobjects.html — TRUE means «работа в контексте» (objects of THIS
            // document); the foreign-file route needs FALSE, or every foreign object answers
            // IsSuitableObject = FALSE. MEASURED: the default is TRUE.
            if (collection.Add() is not ICopyGeometry operation)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "ICopiesGeometry.Add() не вернул объект, отвечающий ICopyGeometry: операция не создана.",
                    RetryPolicy.SameOperationId);
            }

            operation.ContextObjects = false;
            var suitable = SafeBool(() => operation.IsSuitableObject(source.Object)) == true;
            // DOC: icopygeometry_getexternallocalcooordinatesystem.html returns ILocalCoordinateSystem; the
            // add call takes the coclass. MEASURED: null is accepted here, the source CS is not required.
            LocalCoordinateSystem? externalCs = source.Document7 is null
                ? null
                : operation.GetExternalLocalCooordinateSystem(source.Document7) as LocalCoordinateSystem;
            var returned = source.Document7 is not null
                           && SafeBool(() => operation.AddInitialObjectsFromExternalDocument(
                               source.Document7, externalCs!, new object[] { source.Object })) == true;

            // DOC: imodelobject_update.html — «Метод Update необходимо вызвать для объекта, свойства
            // которого были изменены ... чтобы эти изменения вступили в силу». MEASURED: without it the
            // operation stays a draft (empty tree, Count 0) while the add call already answered TRUE.
            var updated = SafeBool(() => ((IModelObject)operation).Update()) == true;
            document.Document3D.RebuildDocument();

            if (command.WatchForSourceChange)
            {
                operation.WatchForSourceChange = true;
            }

            var copies = SafeInt(() => collection.Count) ?? 0;
            var bodiesAfter = CountBodies(document);
            var volumeAfter = ReadVolume(document);
            var copyIndex = copies > 0 ? copies - 1 : 0;
            var inTree = copies > 0;
            var geometry = kind == "body" ? bodiesAfter > bodiesBefore : inTree;

            var checks = new List<NamedCheck>
            {
            new("object_accepted", returned, returned.ToString()),
            new("update_returned", updated, updated.ToString()),
            new("operation_in_collection", inTree, copies.ToString(), ">= 1"),
            new("suitable_before_add", suitable, suitable.ToString()),                new("geometry_read", geometry, $"{bodiesBefore} -> {bodiesAfter} тел",
                    kind == "body" ? "тел больше" : "операция в коллекции"),
            };
            var unverified = new List<string>();
            if (!geometry)
            {
                unverified.Add("геометрия приёмника не изменилась: операция создана, но копии в модели нет");
            }

            if (!inTree || !geometry)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Копия геометрии не подтверждена чтением: операция "
                    + (inTree ? "в коллекции, но тела в приёмнике нет" : "не попала в коллекцию")
                    + $" (вызов {returned}, Update {updated}, тел {bodiesBefore} -> {bodiesAfter}).",
                    RetryPolicy.SameOperationId,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["returned"] = returned,
                        ["update"] = updated,
                        ["copies"] = copies,
                        ["bodies_before"] = bodiesBefore,
                        ["bodies_after"] = bodiesAfter,
                        ["source_path"] = command.SourcePath,
                    });
            }

            BumpRevision(document, "master.copy_geometry");
            return new CopyGeometryResult(
                copyIndex,
                command.SourcePath,
                kind,
                bodiesBefore,
                bodiesAfter,
                volumeBefore,
                volumeAfter,
                new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Копия геометрии отказала: " + (ComHResult.From(ex) is int code
                    ? ComHResult.Name(code)
                    : ex.Message),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                hresult: ex.HResult);
        }
        finally
        {
            CloseCopySource(source, closeAfter);
        }
    }

    /// <summary>Lists the document's «Копия геометрии» operations.</summary>
    /// <remarks>DOC: <c>icopiesgeometry_copygeometry.html</c> — <c>CopyGeometry[Index]</c> «по индексу или
    /// по имени». An index that does not answer is named in <c>notes</c>, not skipped silently.
    /// History: docs/decisions/master-geometry.md#copy-geometry-route</remarks>
    public ListCopiesGeometryResult ListCopiesGeometry(ListCopiesGeometryCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var rows = new List<CopiesGeometryRowDto>();
        var notes = new List<string>();
        const string route = "IModelContainer.CopiesGeometry → ICopiesGeometry.CopyGeometry[Index]";

        var bridge = BridgeFor(document);
        if (bridge.ContainerFor(document.Document3D, document.Id, document.Revision) is not IModelContainer container)
        {
            notes.Add("api7_bridge_unavailable — " + (bridge.BridgeFailure ?? "причина не известна")
                      + ": операции копий не прочитаны");
            return new ListCopiesGeometryResult(rows, route, notes);
        }

        if (container.CopiesGeometry is not ICopiesGeometry collection)
        {
            notes.Add("copies_geometry_unavailable — IModelContainer.CopiesGeometry → null");
            return new ListCopiesGeometryResult(rows, route, notes);
        }

        var count = SafeInt(() => collection.Count) ?? -1;
        if (count < 0)
        {
            notes.Add("copies_geometry_count_unread — ICopiesGeometry.Count не прочитан");
            return new ListCopiesGeometryResult(rows, route, notes);
        }

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var operation = SafeObject(() => collection.CopyGeometry[index]) as ICopyGeometry;
            if (operation is null)
            {
                notes.Add($"copy_{index}_unread — CopyGeometry[{index}] не отвечает");
                continue;
            }

            rows.Add(new CopiesGeometryRowDto
            {
                Index = index,
                SourcePath = SafeObject(() => operation.DocumentFileName) as string,
                WatchForSourceChange = SafeBool(() => operation.WatchForSourceChange),
                ContextObjects = SafeBool(() => operation.ContextObjects),
                MirrorCopy = SafeBool(() => operation.MirrorCopy),
                ByCollectionGeometry = SafeBool(() => operation.ByCollectionGeometry),
            });
        }

        return new ListCopiesGeometryResult(rows, route, notes);
    }

    /// <summary>Synchronises one copy with its source and re-reads the receiver.</summary>
    /// <remarks>DOC: <c>icopygeometry_synhronise.html</c> — «Синхронизировать с источником».
    /// MEASURED: the call answers TRUE even when the source file is gone and the geometry stays, so an
    /// unreadable source is refused HERE, before the product is called.
    /// History: docs/decisions/master-geometry.md#source-gone</remarks>
    public SynchroniseCopyResult SynchroniseCopy(SynchroniseCopyCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        RequirePart(document);
        RequireRevision(document, command.ExpectedRevision);

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision)
                        as IModelContainer
                        ?? throw new KompasContractException(
                            ErrorCodes.CapabilityUnavailable,
                            "IModelContainer приёмника не получен: "
                            + (bridge.BridgeFailure ?? "причина не известна"),
                            RetryPolicy.ReacquireContext);
        var collection = container.CopiesGeometry
                         ?? throw new KompasContractException(
                             ErrorCodes.CapabilityUnavailable,
                             "IModelContainer.CopiesGeometry → null: операций копий нет.",
                             RetryPolicy.ReacquireContext);
        var count = SafeInt(() => collection.Count) ?? 0;
        if (command.CopyIndex < 0 || command.CopyIndex >= count)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Операции с номером {command.CopyIndex} нет: в документе {count} " +
                "(номера выдаёт kompas_list_copies_geometry).",
                RetryPolicy.SameOperationId);
        }

        var operation = SafeObject(() => collection.CopyGeometry[command.CopyIndex]) as ICopyGeometry
                        ?? throw new KompasContractException(
                            ErrorCodes.CapabilityUnavailable,
                            $"CopyGeometry[{command.CopyIndex}] не отвечает ICopyGeometry: синхронизировать нечего.",
                            RetryPolicy.ReacquireContext);
        var sourcePath = SafeObject(() => operation.DocumentFileName) as string;
        if (string.IsNullOrWhiteSpace(sourcePath) || !SafeFileExists(sourcePath))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Источник копии не читается: «{sourcePath}». Synhronise() отвечает TRUE и в этом случае, " +
                "поэтому отказ выдан до вызова продукта.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = sourcePath });
        }

        var bodiesBefore = CountBodies(document);
        var volumeBefore = ReadVolume(document);
        var returned = SafeBool(() => operation.Synhronise()) == true;
        document.Document3D.RebuildDocument();
        var bodiesAfter = CountBodies(document);
        var volumeAfter = ReadVolume(document);

        BumpRevision(document, "master.synchronise_copy");
        var checks = new List<NamedCheck>
        {
            new("source_readable", true, sourcePath),
            new("synhronise_returned", returned, returned.ToString()),
            new("volume_read", volumeAfter is not null,
                volumeBefore is null || volumeAfter is null
                    ? "не прочитан"
                    : $"{volumeBefore:0.###} -> {volumeAfter:0.###} мм³"),
            new("bodies_read", bodiesAfter >= 0, $"{bodiesBefore} -> {bodiesAfter}"),
        };
        return new SynchroniseCopyResult(
            command.CopyIndex,
            sourcePath,
            returned,
            bodiesBefore,
            bodiesAfter,
            volumeBefore,
            volumeAfter,
            new VerificationDto(
                // LIMIT: the BOOL does not separate «синхронизировано» from «источник недоступен», and
                // the expected volume is not known here, so only the receiver is re-read.
                VerificationLevel.CallReturned,
                checks,
                new[] { "что приёмник принял изменение источника, ответ Synhronise не доказывает: " +
                        "объём до и после возвращён, ожидаемое значение задаёт клиент" }));
    }

    /// <summary>The source side of the address: the document as <c>IKompasDocument3D</c> and the object
    /// to copy as <c>IModelObject</c>.</summary>
    /// <remarks>DOC: <c>kspart_bodycollection.html</c> + <c>ksbodycollection_getbyindex.html</c> for the
    /// body, <c>kspart_getdefaultentity.html</c> for the XoY plane. MEASURED: a body taken from
    /// <c>ksPart.GetMainBody()</c> answers <c>IsSuitableObject = FALSE</c>; the collection element is
    /// accepted. History: docs/decisions/master-geometry.md#copy-geometry-route</remarks>
    private CopySource OpenCopySource(
        DocumentEntry receiver, Api7Bridge bridge, string sourcePath, string kind, out bool closeAfter)
    {
        closeAfter = false;
        var existing = TryFindDocumentByPath(sourcePath);
        ksDocument3D source5;
        if (existing is not null && existing.Kind == DocumentKind.Part && existing.Document3D is not null)
        {
            source5 = existing.Document3D;
        }
        else
        {
            var application = RequireApplication(receiver.ApplicationId);
            var opened = (ksDocument3D)application.Application.Document3D();
            if (!opened.Open(sourcePath, !application.DocumentsVisible))
            {
                ComApartment.Release(opened);
                throw new KompasContractException(
                    ErrorCodes.DocumentNotFound,
                    $"Источник копии не открывается: {sourcePath}. Файла нет, он повреждён или занят.",
                    RetryPolicy.SameOperationId,
                    details: new Dictionary<string, object?> { ["source_path"] = sourcePath });
            }

            source5 = opened;
            closeAfter = true;
        }

        var part = (ksPart)source5.GetPart(-1);
        var document7 = bridge.TransferTo7(source5) as IKompasDocument3D;
        object? api5 = kind == "body" ? CopyBodyElement(part) : part.GetDefaultEntity(PlaneXoy);
        var model = api5 is null ? null : bridge.TransferTo7(api5) as IModelObject;
        if (document7 is null || model is null)
        {
            var reason = kind == "body"
                ? "в источнике нет тела, адресуемого коллекцией ksPart.BodyCollection"
                : "плоскость XoY источника не передаётся в API7 как IModelObject";
            if (closeAfter)
            {
                try
                {
                    source5.close();
                }
                catch (COMException)
                {
                    // The refusal below is the answer; a failed cleanup of a throwaway document is not.
                }

                ComApartment.Release(source5);
            }

            throw new KompasContractException(
                ErrorCodes.NoBody,
                "Адрес объекта источника не разрешился: " + reason + $" (object_kind «{kind}»).",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = sourcePath });
        }

        return new CopySource(source5, document7, model);
    }

    private void CloseCopySource(CopySource source, bool closeAfter)
    {
        if (!closeAfter)
        {
            return;
        }

        try
        {
            source.Document5.close();
        }
        catch (COMException)
        {
            // The document was opened only for this call; a failed close is named by the next open.
        }

        ComApartment.Release(source.Document5);
    }

    /// <summary>The body element of the collection, refreshed first: a stale collection answers the
    /// membership from before the last rebuild.</summary>
    private static object? CopyBodyElement(ksPart part)
    {
        if (part.BodyCollection() is not ksBodyCollection bodies)
        {
            return null;
        }

        bodies.refresh();
        return bodies.GetCount() > 0 ? bodies.GetByIndex(0) : null;
    }

    private sealed record CopySource(ksDocument3D Document5, IKompasDocument3D Document7, IModelObject Object);
}
