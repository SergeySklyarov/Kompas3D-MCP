using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe CG — copying geometry from another part's file into a part and keeping the copy
/// associatively tied to its source (order <c>OMEGA_G8_MASTER_GEOMETRY</c>).</summary>
/// <remarks>DOC: <c>imodelcontainer_copiesgeometry.html</c> — <c>IModelContainer.CopiesGeometry</c>,
/// read-only; <c>icopiesgeometry_add.html</c> — <c>Add()</c> returns <c>ICopyGeometry</c>;
/// <c>icopygeometry_addinitialobjectsfromexternaldocument.html</c> — the address of a foreign object is
/// a live <c>IKompasDocument3D</c> plus an <c>ILocalCoordinateSystem</c> of that document plus a
/// <c>SafeArray VT_ARRAY|VT_DISPATCH</c> of <c>IModelObject</c>;
/// <c>icopygeometry_getexternallocalcooordinatesystem.html</c> — «Версия КОМПАС v22»;
/// <c>icopygeometry_synhronise.html</c>, <c>icopygeometry_watchforsourcechange.html</c>.
/// INVARIANT: the copy is confirmed by reading the receiver's geometry and the operation back, never by
/// the BOOL the call returned. History: docs/decisions/probes.md#copy-geometry</remarks>
internal sealed class CopyGeometryProbe
{
    private const short PlaneXoy = 1;
    private const double Width = 100d;
    private const double Height = 80d;
    private const double Thickness = 10d;
    private const double ThicknessAfter = 25d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private IKompasDocument3D? _sourceDocument7;
    private IModelObject? _bodyModel;
    private object? _factoryCollection;
    private int? _factoryCount;
    private string _sourcePath = string.Empty;
    private string _receiverPath = string.Empty;

    public CopyGeometryProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            var source = BuildSource();
            var receiver = BuildReceiver();
            if (source is null || receiver is null)
            {
                return;
            }

            var operation = Factory(receiver.Value.Doc);
            if (operation is null)
            {
                return;
            }

            var objects = Address(source.Value.Part, source.Value.Extrusion, operation,
                out var externalCs, out var externalDoc);
            if (objects is null)
            {
                return;
            }

            var accepted = AddFromFile(operation, externalDoc, externalCs, objects, receiver.Value.Doc);
            if (accepted is null)
            {
                return;
            }

            ReadBack(operation, accepted, receiver.Value.Doc);
            if (RefreshedBodyCount(receiver.Value.Doc) == 0)
            {
                AddThroughOpenDocument(receiver.Value.Doc);
                AddThroughCollectionGeometry(receiver.Value.Doc);
            }

            ReadCollection(receiver.Value.Doc);
            // MEASURED: the save/reopen step closes the receiver, so the live pointer is spent by it;
            // associativity runs first and the reopened file is then read for what it kept.
            Associativity(source.Value.Extrusion, accepted, receiver.Value.Doc);
            WatchAndReopen(receiver.Value.Doc);
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("CG.X", "Необработанное исключение пробы CG");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("CG.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
        try
        {
            var before = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray().Except(before).ToArray();
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            step.Pass("Экземпляр запущен и атрибутирован диффом процессов.");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    /// <summary>The source part is a plate whose thickness is changed later: the associativity step
    /// needs an edit that changes the volume in a known direction.</summary>
    private (ksDocument3D Doc, ksPart Part, ksEntity? Extrusion)? BuildSource()
    {
        var step = _report.Begin("CG.2", "Источник: пластина 100x80x10 в файле",
            "Есть ли сохранённый файл, из которого можно копировать?");
        var doc = (ksDocument3D)_app.Document3D();
        if (doc.Create(true, true) != true)
        {
            step.Fail("Документ-источник не создан.");
            return null;
        }

        var part = (ksPart)doc.GetPart(-1);
        var extrusion = Api5.BasePlate(part, Width, Height, Thickness, step, "cg-src");
        if (extrusion is null)
        {
            step.Fail("Пластина не построена.");
            return null;
        }

        doc.RebuildDocument();
        _sourcePath = Path.Combine(_options.WorkDir, "g8-source.m3d");
        var saved = doc.SaveAs(_sourcePath);

        // MEASURED: Open() on a file this session already has open answers FALSE, so the source is closed
        // first. The reopened document is what the product will see, so the feature is taken from its own
        // tree: a feature pointer of the closed document is spent.
        if (_options.CopyGeometryDiskSource)
        {
            step.Data["source_closed"] = Api5.Raw(Api5.SafeBool(() => doc.close()));
            var reopened = (ksDocument3D)_app.Document3D();
            var openedFromDisk = Api5.SafeBool(() => reopened.Open(_sourcePath, true));
            step.Data["source_reopened_from_disk"] = openedFromDisk;
            if (openedFromDisk == true)
            {
                doc = reopened;
                part = (ksPart)doc.GetPart(-1);
                extrusion = Api5.ReadFeatures(part, step).Select(f => f.Entity)
                    .FirstOrDefault(e => e is not null && e.type == Api5.BaseExtrusion);
                step.Data["source_extrusion_from_tree"] = extrusion is not null;
            }
        }

        _sourceDocument7 = TransferTo7Document(doc);
        step.Data["save_as"] = saved;
        step.Data["path"] = _sourcePath;
        step.Data["volume"] = Api5.Volume(part);
        step.Data["bodies"] = Api5.BodyCount(part);
        if (saved == true && Api5.BodyCount(part) == 1)
        {
            step.Pass($"Источник сохранён, тело одно, объём {Api5.Num(Api5.Volume(part))} мм³.");
            return (doc, part, extrusion);
        }

        step.Fail("Источник не сохранён или тело не одно.");
        return null;
    }

    private (ksDocument3D Doc, ksPart Part)? BuildReceiver()
    {
        var step = _report.Begin("CG.3", "Приёмник: пустая деталь в файле",
            "Куда копировать и что читать до копирования?");
        var doc = (ksDocument3D)_app.Document3D();
        if (doc.Create(true, true) != true)
        {
            step.Fail("Документ-приёмник не создан.");
            return null;
        }

        var part = (ksPart)doc.GetPart(-1);
        _receiverPath = Path.Combine(_options.WorkDir, "g8-receiver.m3d");
        var saved = doc.SaveAs(_receiverPath);
        step.Data["save_as"] = saved;
        step.Data["path"] = _receiverPath;
        step.Data["bodies"] = Api5.BodyCount(part);
        step.Data["volume"] = Api5.Volume(part);
        if (saved == true && Api5.BodyCount(part) == 0)
        {
            step.Pass("Приёмник сохранён пустым: тел 0.");
            return (doc, part);
        }

        step.Fail("Приёмник не сохранён или в нём уже есть тело.");
        return null;
    }

    private ICopyGeometry? Factory(ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.4", "Фабрика копий геометрии доступна живьём",
            "Отдаёт ли контейнер ICopiesGeometry и создаётся ли операция по Add()?");
        try
        {
            if (Container(receiverDoc) is not IModelContainer container)
            {
                step.Fail("TransferInterface(документ → API7) не дал IModelContainer.");
                return null;
            }

            var collection = container.CopiesGeometry;
            if (collection is null)
            {
                step.Fail("IModelContainer.CopiesGeometry → null.");
                return null;
            }

            step.Data["count_before"] = Api5.SafeInt(() => collection.Count);
            var created = collection.Add();
            if (created is not ICopyGeometry operation)
            {
                step.Fail("CopiesGeometry.Add() не отдал объект, отвечающий QI(ICopyGeometry): "
                    + Api5.RuntimeName(created));
                return null;
            }

            step.Observe("Add() → " + Api5.RuntimeName(created));
            step.Data["count_after"] = Api5.SafeInt(() => collection.Count);
            _factoryCollection = collection;
            _factoryCount = step.Data["count_after"] as int?;
            step.Data["document_file_name_empty"] = Api5.Raw(Api5.SafeObject(() => operation.DocumentFileName));
            step.Data["watch_default"] = Api5.Raw(Api5.SafeBool(() => operation.WatchForSourceChange));
            step.Data["context_objects_default"] = Api5.Raw(Api5.SafeBool(() => operation.ContextObjects));
            step.Data["mirror_default"] = Api5.Raw(Api5.SafeBool(() => operation.MirrorCopy));

            // MEASURED: ContextObjects defaults to TRUE («работа в контексте», i.e. objects of THIS
            // document); the external-file route needs FALSE, otherwise every foreign object answers
            // IsSuitableObject=FALSE.
            operation.ContextObjects = false;
            step.Data["context_objects_written"] = Api5.Raw(Api5.SafeBool(() => operation.ContextObjects));
            step.Pass("Коллекция и Add() работают; операция создана пустой.");
            return operation;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            step.Fail("Маршрут фабрики отказал: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>The decisive question of the order: is there a documented, stable address for an object
    /// of a foreign document? The page names the address: the source <c>IKompasDocument3D</c>, its
    /// <c>ILocalCoordinateSystem</c>, and the object itself as <c>IModelObject</c>.</summary>
    private List<(string Name, IModelObject Object)>? Address(ksPart sourcePart, ksEntity? extrusion,
        ICopyGeometry operation, out LocalCoordinateSystem? externalCs, out IKompasDocument3D? externalDoc)
    {
        externalCs = null;
        externalDoc = null;
        var step = _report.Begin("CG.5", "Адрес объекта чужого документа",
            "Принимает ли документированный маршрут объект источника и как он называется?");

        // Which collections the container itself offers: the body taken from API5 GetMainBody is not
        // suitable, so the candidate list is widened to what IModelContainer publishes.
        step.Data["container_members"] = typeof(IModelContainer).GetProperties()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        step.Data["model_objects_members"] = typeof(IModelObjects).GetMembers()
            .Where(m => m.MemberType is System.Reflection.MemberTypes.Method
                or System.Reflection.MemberTypes.Property)
            .Select(m => m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        step.Data["model_object_members"] = typeof(IModelObject).GetProperties()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var candidates = new List<(string Name, object? Api5, IModelObject? Api7, bool? Suitable)>();
        foreach (var (name, raw) in new (string, object?)[]
                 {
                     ("тело", Api5.SafeObject(() => sourcePart.GetMainBody())),
                     ("тело из BodyCollection", Api5.SafeObject(() => BodyElement(sourcePart))),
                     ("выдавливание", extrusion),
                     ("плоскость XoY", Api5.SafeObject(() => sourcePart.GetDefaultEntity(PlaneXoy))),
                 })
        {
            var seven = TransferTo7(raw);
            candidates.Add((name, raw, seven, seven is null
                ? null
                : Api5.SafeBool(() => operation.IsSuitableObject(seven))));
        }

        // DOC: imodelcontainer_objects.html — `Objects = iObject.Objects(ObjType)` returns the 3D objects
        // of that type; obj3dtype.html — o3d_body = 115. This is the documented list of model objects,
        // while a body taken from API5 GetMainBody answers IsSuitableObject=FALSE.
        var sourceContainer = _sourceDocument7?.TopPart as IModelContainer;
        var objectsForms = new (string Name, object Argument)[]
        {
            ("enum ksObjectBody7", KompasAPIObjectTypeEnum.ksObjectBody7),
            ("int 115", 115),
            ("short 115", (short)115),
            ("string 115", "115"),
        };
        step.Data["objects_forms"] = objectsForms.Select(f =>
        {
            var raw = Api5.SafeObject(() => sourceContainer!.Objects[f.Argument]);
            var count = raw is IModelObjects list ? Api5.SafeInt(() => list.Count) : null;
            return $"{f.Name}: {Api5.RuntimeName(raw)}/{Api5.Raw(count)}";
        }).ToList();
        var bodiesOfSource = Api5.SafeObject(() => sourceContainer!.Objects[KompasAPIObjectTypeEnum.ksObjectBody7]) as IModelObjects;
        var bodiesCount = bodiesOfSource is null ? 0 : Api5.SafeInt(() => bodiesOfSource.Count) ?? 0;
        step.Data["object_type_enum_body_names"] = Enum.GetNames<KompasAPIObjectTypeEnum>()
            .Where(n => n.Contains("body", StringComparison.OrdinalIgnoreCase)).ToList();
        step.Data["source_objects_by_type"] = CountByType(sourceContainer);
        step.Data["source_objects_body"] = Api5.RuntimeName(bodiesOfSource);
        step.Data["source_objects_body_count"] = bodiesCount;
        for (var i = 0; i < bodiesCount; i++)
        {
            var index = i;
            if (Api5.SafeObject(() => bodiesOfSource![index]) is not IModelObject model)
            {
                continue;
            }

            var type = Api5.SafeObject(() => model.ModelObjectType);
            candidates.Add(($"Objects[115][{index}] {type}", model, model,
                Api5.SafeBool(() => operation.IsSuitableObject(model))));
        }

        foreach (var candidate in candidates)
        {
            step.Observe($"{candidate.Name}: API5 {Api5.RuntimeName(candidate.Api5)} → API7 "
                + $"{Api5.RuntimeName(candidate.Api7)}, IsSuitableObject={Api5.Raw(candidate.Suitable)}");
        }

        step.Data["candidates"] = candidates
            .Select(c => $"{c.Name}:{Api5.RuntimeName(c.Api7)}:{Api5.Raw(c.Suitable)}")
            .ToList();

        // The external document pointer is the second half of the address. It is taken from the live
        // source document, because the documented route asks for IKompasDocument3D, not for a path.
        var document7 = _sourceDocument7;
        externalDoc = document7;
        step.Data["external_document"] = Api5.RuntimeName(document7);
        if (document7 is null)
        {
            step.Fail("Документ-источник не удалось получить как IKompasDocument3D — адрес неполон.");
            return null;
        }

        externalCs = Api5.SafeObject(() => operation.GetExternalLocalCooordinateSystem(document7)) as LocalCoordinateSystem;
        step.Data["external_cs"] = Api5.RuntimeName(externalCs);

        var addressable = candidates
            .Where(c => c.Api7 is not null)
            .Select(c => (c.Name, Object: c.Api7!))
            .ToList();
        step.Data["addressable"] = addressable.Select(c => c.Name).ToList();
        if (addressable.Count == 0)
        {
            step.Fail("Ни один объект источника не передался в API7 как IModelObject — адреса нет.");
            return null;
        }

        // IsSuitableObject is recorded, not obeyed: with the source not yet attached the product answers
        // FALSE for every foreign object, so a FALSE here is not yet a verdict about the object.
        step.Pass($"Адрес собран: документ-источник + его СК ({Api5.RuntimeName(externalCs)}) + "
            + $"объект как IModelObject; кандидатов {addressable.Count}.");
        return addressable;
    }

    private IModelObject? AddFromFile(ICopyGeometry operation, IKompasDocument3D? externalDoc,
        LocalCoordinateSystem? externalCs, List<(string Name, IModelObject Object)> objects,
        ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.6", "AddInitialObjectsFromExternalDocument + Update",
            "Принимает ли маршрут объект чужого документа и вводит ли Update() операцию в модель?");
        // DOC: icopygeometry.html - the hierarchy is IDispatch > IKompasAPIObject > IModelObject >
        // ICopyGeometry, so the operation is an IModelObject. DOC: imodelobject_update.html - «Метод
        // Update необходимо вызвать для объекта, свойства которого были изменены ... чтобы эти изменения
        // вступили в силу». MEASURED: without Update the operation stays a draft - the add call answers
        // TRUE and nothing reaches the tree; with it the tree gains the operation.
        step.Data["operation_is_model_object"] = operation as IModelObject is not null;
        foreach (var candidate in objects)
        {
            try
            {
                var result = operation.AddInitialObjectsFromExternalDocument(externalDoc!, externalCs!,
                    new object[] { candidate.Object });
                var update = Api5.SafeBool(() => ((IModelObject)operation).Update());
                step.Data["update_" + candidate.Name] = Api5.Raw(update);
                receiverDoc.RebuildDocument();
                var bodies = Api5.BodyCount((ksPart)receiverDoc.GetPart(-1));
                var initial = Api5.SafeBool(() => operation.IsInitialObject(candidate.Object));
                step.Data["returned_" + candidate.Name] = result;
                step.Data["initial_" + candidate.Name] = Api5.Raw(initial);
                step.Observe($"{candidate.Name}: возврат {result}, Update()={Api5.Raw(update)}, "
                    + $"тел в приёмнике {bodies}, "
                    + $"IsSuitableObject={Api5.Raw(Api5.SafeBool(() => operation.IsSuitableObject(candidate.Object)))}, "
                    + $"IsInitialObject={Api5.Raw(initial)}");
                if (result && initial == true)
                {
                    _bodyModel = candidate.Object;
                    step.Data["accepted"] = candidate.Name;
                    step.Pass($"Объект «{candidate.Name}» принят и записан как исходный.");
                    return candidate.Object;
                }
            }
            catch (COMException ex)
            {
                step.Data["hresult_" + candidate.Name] = HResult.Describe(ex);
                step.Observe($"{candidate.Name}: отказ {HResult.Describe(ex)}");
            }
        }

        step.Fail("Ни один объект источника не принят: адрес не разрешается.");
        return null;
    }

    private void ReadBack(ICopyGeometry operation, IModelObject body, ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.7", "Что читается обратно и что появилось в приёмнике",
            "Скопировалась ли геометрия и записался ли адрес источника?");
        var part = (ksPart)receiverDoc.GetPart(-1);
        step.Data["document_file_name"] = Api5.Raw(Api5.SafeObject(() => operation.DocumentFileName));
        step.Data["is_initial_object"] = Api5.Raw(Api5.SafeBool(() => operation.IsInitialObject(body)));

        // MEASURED: the call answers TRUE and the source name is recorded, yet the receiver still has no
        // body; the rebuild and, after it, Synhronise are tried one by one and each result is recorded.
        receiverDoc.RebuildDocument();
        step.Data["bodies_after_rebuild"] = RefreshedBodyCount(receiverDoc);
        step.Data["volume_after_rebuild"] = Api5.Volume(part);
        step.Observe($"После перестройки: тел {RefreshedBodyCount(receiverDoc)}, "
            + $"объём {Api5.Num(Api5.Volume(part))} мм³.");

        if (Api5.BodyCount(part) == 0)
        {
            step.Data["synhronise_after_add"] = Api5.Raw(Api5.SafeBool(() => operation.Synhronise()));
            receiverDoc.RebuildDocument();
            step.Data["bodies_after_synhronise"] = RefreshedBodyCount(receiverDoc);
            step.Data["volume_after_synhronise"] = Api5.Volume(part);
            step.Observe($"После Synhronise: тел {RefreshedBodyCount(receiverDoc)}, "
                + $"объём {Api5.Num(Api5.Volume(part))} мм³.");
        }

        step.Data["objects_by_type_receiver"] = CountByType(Container(receiverDoc));
        try
        {
            var features = Api5.ReadFeatures(part, step);
            step.Data["receiver_features"] = features.Select(f => f.Name).ToList();
            step.Observe("Дерево приёмника: " + string.Join(", ", features.Select(f => f.Name)));
        }
        catch (Exception ex)
        {
            step.Data["receiver_features_error"] = ex.GetType().Name + ": " + ex.Message;
        }
        step.Data["bodies"] = RefreshedBodyCount(receiverDoc);
        step.Data["volume"] = Api5.Volume(part);
        step.Observe("Объекты приёмника по типам: "
            + string.Join(", ", CountByType(Container(receiverDoc)).Select(p => p.Key + "=" + p.Value)));
        if (RefreshedBodyCount(receiverDoc) > 0)
        {
            step.Pass("Геометрия в приёмнике есть: тело появилось.");
        }
        else
        {
            step.Fail("Тела в приёмнике нет — копия не создана.");
        }
    }

    /// <summary>How the created operation is read back is a separate documented question: the page names
    /// an index or a name, and the interop exposes two indexers. Each form is tried and recorded.</summary>
    /// <summary>DOC: <c>icopygeometry_opendocument.html</c> - «Открывает документ-источник на
    /// редактирование». The first route used a document the probe created itself; here the source is the
    /// one the operation opened, which is the product's own idea of the source.</summary>
    private void AddThroughOpenDocument(ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.7b", "Источник, открытый самой операцией",
            "Даёт ли геометрию маршрут, в котором источник открывает OpenDocument?");
        var collection = Container(receiverDoc)?.CopiesGeometry;
        if (collection?.Add() is not ICopyGeometry operation)
        {
            step.Fail("Вторую операцию создать не удалось.");
            return;
        }

        var opened = Api5.SafeObject(() => operation.OpenDocument(false, true)) as IKompasDocument3D;
        step.Data["opened"] = Api5.RuntimeName(opened);
        if (opened is null)
        {
            step.Fail("OpenDocument не дал IKompasDocument3D.");
            return;
        }

        var model = _bodyModel;
        step.Data["model"] = Api5.RuntimeName(model);
        if (model is null)
        {
            step.Fail("Тело источника не было получено ранее.");
            return;
        }

        step.Data["suitable"] = Api5.Raw(Api5.SafeBool(() => operation.IsSuitableObject(model)));
        var cs = Api5.SafeObject(() => operation.GetExternalLocalCooordinateSystem(opened)) as LocalCoordinateSystem;
        step.Data["cs"] = Api5.RuntimeName(cs);
        step.Data["returned"] = Api5.Raw(Api5.SafeBool(
            () => operation.AddInitialObjectsFromExternalDocument(opened, cs!, new object[] { model })));
        step.Data["update"] = Api5.Raw(Api5.SafeBool(() => ((IModelObject)operation).Update()));
        step.Data["initial"] = Api5.Raw(Api5.SafeBool(() => operation.IsInitialObject(model)));
        step.Data["document_file_name"] = Api5.Raw(Api5.SafeObject(() => operation.DocumentFileName));
        receiverDoc.RebuildDocument();
        step.Data["bodies_after"] = RefreshedBodyCount(receiverDoc);
        step.Data["volume_after"] = Api5.Volume((ksPart)receiverDoc.GetPart(-1));
        step.Observe($"После маршрута: тел {RefreshedBodyCount(receiverDoc)}, "
            + $"объём {Api5.Num(Api5.Volume((ksPart)receiverDoc.GetPart(-1)))} мм³.");
        if (RefreshedBodyCount(receiverDoc) > 0)
        {
            step.Pass("Геометрия появилась: тело в приёмнике есть.");
        }
        else
        {
            step.Fail("Тела в приёмнике нет и на этом маршруте.");
        }
    }

    /// <summary>DOC: <c>imodelcontainer_collectionsgeometry.html</c>, <c>icollectionsgeometry_add.html</c>,
    /// <c>icollectiongeometry_geometry.html</c> (VARIANT, read and write), and
    /// <c>icopygeometry_bycollectiongeometry.html</c>. The question is whether these pieces form one
    /// operation that reaches the model tree.</summary>
    private void AddThroughCollectionGeometry(ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.7c", "Коллекция геометрии и ByCollectionGeometry",
            "Собирается ли из ICollectionGeometry и ICopyGeometry операция, видимая в дереве?");
        var container = Container(receiverDoc);
        var collections = Api5.SafeObject(() => container?.CollectionsGeometry) as ICollectionsGeometry;
        step.Data["collections_geometry"] = Api5.RuntimeName(collections);
        if (collections?.Add() is not ICollectionGeometry collection || _bodyModel is null)
        {
            step.Fail("Коллекция геометрии не создана.");
            return;
        }

        step.Data["collection"] = Api5.RuntimeName(collection);
        try
        {
            collection.Geometry = new object[] { _bodyModel };
            step.Observe("Geometry записана; читается обратно: "
                + Api5.RuntimeName(Api5.SafeObject(() => collection.Geometry)));
        }
        catch (COMException ex)
        {
            step.Observe("Запись Geometry отказала: " + HResult.Describe(ex));
        }

        step.Data["collections_count"] = Api5.SafeInt(() => collections.Count);

        var copies = container?.CopiesGeometry;
        foreach (var (name, argument) in new (string, object)[]
                 {
                     ("коллекция", collection),
                     ("тело", _bodyModel),
                 })
        {
            if (copies?.Add() is not ICopyGeometry operation)
            {
                step.Fail("Операцию создать не удалось.");
                return;
            }

            try
            {
                operation.ByCollectionGeometry = true;
                step.Data["by_collection_" + name] = Api5.Raw(Api5.SafeBool(() => operation.ByCollectionGeometry));
                step.Data["add_" + name] = Api5.Raw(
                    Api5.SafeBool(() => operation.AddInitialObjects(new object[] { argument })));
                step.Data["update_" + name] = Api5.Raw(
                    Api5.SafeBool(() => ((IModelObject)operation).Update()));
                receiverDoc.RebuildDocument();
                var features = Api5.ReadFeatures((ksPart)receiverDoc.GetPart(-1), step)
                    .Select(f => f.Name).ToList();
                step.Data["tree_" + name] = features;
                step.Data["bodies_" + name] = RefreshedBodyCount(receiverDoc);
                step.Observe($"{name}: дерево [{string.Join(", ", features)}], "
                    + $"тел {RefreshedBodyCount(receiverDoc)}");
                if (RefreshedBodyCount(receiverDoc) > 0)
                {
                    step.Pass("Геометрия появилась через коллекцию геометрии.");
                    return;
                }
            }
            catch (COMException ex)
            {
                step.Data["hresult_" + name] = HResult.Describe(ex);
                step.Observe($"{name}: отказ {HResult.Describe(ex)}");
            }
        }

        step.Fail("Ни один вариант не дал тела в приёмнике.");
    }

    private void ReadCollection(ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.7a", "Чтение операции из коллекции: индекс или имя",
            "Какая форма обращения к ICopiesGeometry отвечает?");
        var collection = Container(receiverDoc)?.CopiesGeometry;
        if (collection is null)
        {
            step.Fail("Коллекция не получена.");
            return;
        }

        var container = Container(receiverDoc);
        step.Data["collections_geometry"] = Api5.RuntimeName(
            Api5.SafeObject(() => container?.CollectionsGeometry));
        step.Data["collections_geometry_count"] = Api5.SafeInt(
            () => container!.CollectionsGeometry.Count);
        step.Data["count"] = Api5.SafeInt(() => collection.Count);
        step.Data["count_same_instance"] = _factoryCollection is ICopiesGeometry same
            ? Api5.SafeInt(() => same.Count)
            : null;
        step.Data["collection_same_instance"] = Api5.RuntimeName(_factoryCollection);
        var forms = new (string Name, object Argument)[]
        {
            ("CopyGeometry[0]", 0),
            ("CopyGeometry[1]", 1),
            ("CopyGeometry[short 0]", (short)0),
            ("CopyGeometry[\"0\"]", "0"),
            ("CopyGeometry[\"1\"]", "1"),
        };
        foreach (var form in forms)
        {
            var raw = Api5.SafeObject(() => collection.CopyGeometry[form.Argument]);
            step.Observe($"{form.Name} → {Api5.RuntimeName(raw)}");
            step.Data[form.Name] = Api5.RuntimeName(raw);
        }

        step.Unknown("Формы записаны; отвечающая выбирается по наблюдению выше.");
    }

    private void WatchAndReopen(ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.8", "Слежение за источником: запись, чтение, сохранение, переоткрытие",
            "Переживает ли операция сохранение и читается ли признак слежения после reopen?");
        try
        {
            var current = Api5.SafeObject(() => CopyOperation(receiverDoc));
            if (current is not ICopyGeometry live)
            {
                step.Fail("После Add() операция не находится по индексу 0.");
                return;
            }

            live.WatchForSourceChange = true;
            step.Data["watch_written"] = true;
            step.Data["watch_read"] = Api5.Raw(Api5.SafeBool(() => live.WatchForSourceChange));
            receiverDoc.Save();

            // MEASURED: Open() on a file this session already has open answers FALSE, so the receiver is
            // closed first; without the close the reopen is never measured at all.
            step.Data["volume_before_reopen"] = Api5.Volume((ksPart)receiverDoc.GetPart(-1));
            step.Data["closed"] = Api5.Raw(Api5.SafeBool(() => receiverDoc.close()));
            var reopened = (ksDocument3D)_app.Document3D();
            step.Data["reopen_opened"] = Api5.Raw(Api5.SafeBool(() => reopened.Open(_receiverPath, true)));
            var reopenedContainer = Container(reopened);
            step.Data["container_after_reopen"] = Api5.RuntimeName(reopenedContainer);
            step.Data["collection_after_reopen"] = Api5.RuntimeName(
                Api5.SafeObject(() => reopenedContainer?.CopiesGeometry));
            step.Data["count_raw_after_reopen"] = Api5.SafeInt(
                () => reopenedContainer!.CopiesGeometry.Count);
            var again = CopyOperation(reopened) as ICopyGeometry;
            step.Data["count_after_reopen"] = again is null
                ? null
                : Api5.SafeInt(() => Container(reopened)!.CopiesGeometry.Count);
            step.Data["document_file_name_after_reopen"] = again is null
                ? null
                : Api5.Raw(Api5.SafeObject(() => again.DocumentFileName));
            step.Data["watch_after_reopen"] = again is null
                ? null
                : Api5.Raw(Api5.SafeBool(() => again.WatchForSourceChange));
            step.Data["volume_after_reopen"] = Api5.Volume((ksPart)reopened.GetPart(-1));
            step.Data["bodies_after_reopen"] = RefreshedBodyCount(reopened);
            step.Data["tree_after_reopen"] = Api5.ReadFeatures((ksPart)reopened.GetPart(-1), step)
                .Select(f => f.Name).ToList();
            step.Observe("После reopen: DocumentFileName="
                + Api5.Raw(again is null ? null : Api5.SafeObject(() => again.DocumentFileName))
                + ", WatchForSourceChange="
                + Api5.Raw(again is null ? null : Api5.SafeBool(() => again.WatchForSourceChange)));
            if (again is not null)
            {
                step.Pass("Операция пережила сохранение и читается с переоткрытого файла.");
            }
            else
            {
                step.Fail("После reopen операция не найдена.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Fail("Сохранение/переоткрытие отказало: " + HResult.Describe(ex));
        }
    }

    /// <summary>The associativity claim of the order: the source changes, <c>Synhronise()</c> runs, the
    /// receiver's geometry changes the way the source change implies.</summary>
    private void Associativity(ksEntity? extrusion, IModelObject body, ksDocument3D receiverDoc)
    {
        var step = _report.Begin("CG.9", "Ассоциативность: источник изменился, приёмник синхронизирован",
            "Меняется ли геометрия приёмника так, как следует из изменения источника?");
        var before = Api5.Volume((ksPart)receiverDoc.GetPart(-1));
        step.Data["volume_before"] = before;

        if (extrusion is null)
        {
            step.Unknown("Признак выдавливания источника в дереве не найден — менять нечем.");
            return;
        }

        if (Api5.SafeObject(() => extrusion.GetDefinition()) is not ksBaseExtrusionDefinition definition)
        {
            step.Fail("Определение источника не получено — изменить его нечем.");
            return;
        }

        definition.SetSideParam(true, 0 /* etBlind */, ThicknessAfter, 0d, false);
        var updated = Api5.SafeBool(() => extrusion.Update());
        step.Data["source_update"] = updated;

        if (CopyOperation(receiverDoc) is not ICopyGeometry live)
        {
            step.Fail("Операция в приёмнике не найдена перед синхронизацией.");
            return;
        }

        live.WatchForSourceChange = false;
        var synchronised = Api5.SafeBool(() => live.Synhronise());
        step.Data["synhronise"] = synchronised;
        receiverDoc.RebuildDocument();

        var after = Api5.Volume((ksPart)receiverDoc.GetPart(-1));
        step.Data["volume_after"] = after;
        var expected = Width * Height * ThicknessAfter;
        step.Data["volume_expected"] = expected;
        step.Observe($"Объём приёмника: до {Api5.Num(before)}, после {Api5.Num(after)}, "
            + $"ожидается {Api5.Num(expected)} мм³.");
        if (after is not null && Math.Abs(after.Value - expected) < expected * 0.01)
        {
            step.Pass("Приёмник принял изменение источника: объём равен ожидаемому.");
        }
        else
        {
            step.Fail("Объём приёмника не стал ожидаемым — ассоциативность не доказана.");
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("CG.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            _app.Quit();
            var pid = _options.ProcessId;
            var gone = pid == 0 || SpinWait.SpinUntil(
                () => Process.GetProcessesByName("KOMPAS").All(p => p.Id != pid), TimeSpan.FromSeconds(20));
            if (gone)
            {
                step.Pass("Процесс завершился.");
            }
            else
            {
                step.Fail("Процесс остался после Quit().");
            }
        }
        catch (Exception ex)
        {
            step.Fail("Завершение отказало: " + ex.Message);
        }
    }

    /// <summary>Counts the container's objects by documented type: <c>Objects(ObjType)</c> is the only
    /// documented list of model objects, so "what appeared" is measured type by type.</summary>
    private Dictionary<string, int> CountByType(IModelContainer? container)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (container is null)
        {
            return result;
        }

        foreach (var type in Enum.GetValues<KompasAPIObjectTypeEnum>())
        {
            var count = Api5.SafeInt(() => (container.Objects[type] as IModelObjects)!.Count);
            if (count is > 0)
            {
                result[type.ToString()] = count.Value;
            }
        }

        return result;
    }

    /// <summary>DOC: <c>ksPart.BodyCollection()</c> returns <c>ksBodyCollection</c>; the element is taken by
    /// <c>GetByIndex</c> after <c>refresh()</c>, as the adapter does. GetMainBody is not used here because
    /// its object answered IsSuitableObject=FALSE.</summary>
    private static object? BodyElement(ksPart part)
    {
        if (part.BodyCollection() is not ksBodyCollection collection)
        {
            return null;
        }

        collection.refresh();
        return collection.GetCount() > 0 ? collection.GetByIndex(0) : null;
    }

    /// <summary>INVARIANT: the body count is read after <c>refresh()</c>; a stale collection answers the
    /// state before the rebuild, which is exactly the wrong number here.</summary>
    private static int RefreshedBodyCount(ksDocument3D doc)
    {
        try
        {
            if (((ksPart)doc.GetPart(-1)).BodyCollection() is not ksBodyCollection collection)
            {
                return -1;
            }

            collection.refresh();
            return collection.GetCount();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private IModelContainer? Container(ksDocument3D doc) =>
        _app.TransferInterface(doc, 2 /* ksAPI7Dual */, 0) is IKompasDocument3D document7
            ? document7.TopPart as IModelContainer
            : null;

    private ICopyGeometry? CopyOperation(ksDocument3D doc)
    {
        try
        {
            var collection = Container(doc)?.CopiesGeometry;
            if (collection is null || Api5.SafeInt(() => collection.Count) is not > 0)
            {
                return null;
            }

            // Two indexers exist: ICopiesGeometry::CopyGeometry[Index] («по индексу или по имени») and the
            // inherited IModelObjects indexer. Which one answers is measured, not assumed.
            return Api5.SafeObject(() => collection.CopyGeometry[0]) as ICopyGeometry
                ?? Api5.SafeObject(() => ((IModelObjects)collection)[0]) as ICopyGeometry;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IKompasDocument3D? TransferTo7Document(object? source) =>
        Api5.SafeObject(() => _app.TransferInterface(source, 2, 0)) as IKompasDocument3D;

    private IModelObject? TransferTo7(object? source) =>
        Api5.SafeObject(() => _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0)) as IModelObject;
}
