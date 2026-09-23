using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.P0Probe;

/// <summary>
/// P0.12 — adapter-level ground truth for the body-collection discrepancy.
/// </summary>
/// <remarks>
/// The vertical MCP scenario shows a contradiction that cannot be resolved by reading the code:
/// <c>kompas_extrude</c> reports <c>body_count = 1</c> and a volume of 80000 mm³, and the
/// immediately following <c>kompas_list_bodies</c> for the same <c>document_id</c> returns an
/// empty list with <c>status = succeeded</c>. Both go through the same
/// <c>Part.BodyCollection()</c>. This step calls the <b>production adapter</b> directly — not raw
/// COM, not a mock — and prints every intermediate value, so the answer is observed rather than
/// argued. It stays in the probe as a regression check: a future fix must keep these rows equal.
/// </remarks>
internal static class AdapterFacts
{
    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "P0.12",
            "Адаптер: почему список тел пуст при наличии тела",
            "Какие значения реально возвращают BodyCollection().GetCount(), GetByIndex(0) и GetMainBody() в момент расхождения?");

        using var session = new Api5Session();
        try
        {
            var application = session.Connect(new ConnectCommand { Mode = ConnectMode.Launch });
            step.Observe($"сеанс: app={application.Id}, pid={application.ProcessId}, ownership={application.Ownership}.");

            var document = session.CreateDocument(new CreateDocumentCommand
            {
                ApplicationId = application.Id,
                Kind = DocumentKind.Part,
                Name = "P0_Adapter_Body",
            });
            step.Observe($"документ {document.Id}, ревизия {document.Revision}.");

            var sketch = session.CreateSketch(new CreateSketchCommand
            {
                DocumentId = document.Id,
                Plane = new PlaneRefDto { Base = PlaneBase.Xy },
                Name = "profile",
                ExpectedRevision = document.Revision,
            });

            session.EditSketch(new EditSketchCommand
            {
                SketchRef = sketch.Id,
                Mode = SketchEditMode.Replace,
                ExpectedRevision = document.Revision,
                Entities = new[]
                {
                    new SketchEntityDto
                    {
                        Kind = SketchEntityKind.Rectangle,
                        StartMm = new double[] { 0, 0 },
                        WidthMm = 100,
                        HeightMm = 80,
                    },
                },
            });

            session.FinishSketch(new FinishSketchCommand { SketchRef = sketch.Id, RequireClosedProfile = false });

            var extrude = session.Extrude(new ExtrudeCommand
            {
                SketchRef = sketch.Id,
                Operation = ExtrudeOperation.Base,
                DepthMm = 10,
                Direction = ExtrudeDirection.Positive,
                ExpectedRevision = document.Revision,
            });
            step.Observe($"экструзия: тел={extrude.BodyCount}, объём={extrude.VolumeMm3?.ToString("0.####") ?? "null"}, " +
                         $"уровень={(extrude.Verification?.Level ?? VerificationLevel.None)}");

            // ---- the discrepancy itself, measured at one instant -------------------------------
            var entry = session.RequireDocument(document.Id);
            var countViaAdapter = session.CountBodies(entry);
            var rows = session.ListBodies(new ListBodiesCommand { DocumentId = document.Id });
            var rawCollection = (ksBodyCollection)entry.PartNow().BodyCollection();
            var rawCountBeforeRefresh = rawCollection.GetCount();

            object? firstElement = null;
            string firstTypeName = "<нет элементов>";
            bool firstIsBody = false;
            string? firstDefinitionType = null;
            if (rawCountBeforeRefresh > 0)
            {
                firstElement = rawCollection.GetByIndex(0);
                firstTypeName = firstElement?.GetType().FullName ?? "null";
                firstIsBody = firstElement is ksBody;
                try
                {
                    firstDefinitionType = (firstElement as ksEntity)?.GetDefinition()?.GetType().Name ?? "<не ksEntity>";
                }
                catch (Exception ex)
                {
                    firstDefinitionType = ex.GetType().Name;
                }
            }

            var mainBody = entry.PartNow().GetMainBody();
            step.Observe($"CountBodies() = {countViaAdapter}; ListBodies().Count = {rows.Count}; " +
                         $"сырой BodyCollection().GetCount() = {rawCountBeforeRefresh}.");
            step.Observe($"GetByIndex(0): тип = {firstTypeName}, is ksBody = {firstIsBody}, GetDefinition() = {firstDefinitionType}.");
            step.Observe($"GetMainBody(): {(mainBody is null ? "null" : mainBody.GetType().FullName)}.");

            step.Data["count_via_adapter"] = countViaAdapter;
            step.Data["rows_count"] = rows.Count;
            step.Data["raw_collection_count"] = rawCountBeforeRefresh;
            step.Data["first_element_type"] = firstTypeName;
            step.Data["first_element_is_ksbody"] = firstIsBody;
            step.Data["main_body_type"] = mainBody?.GetType().FullName ?? "null";
            step.Data["row_kinds"] = rows.Select(r => r.Kind).ToArray();

            // A second read of the same document after the first ListBodies call is what separates
            // "the collection is empty" from "the enumeration consumed it".
            var rowsSecond = session.ListBodies(new ListBodiesCommand { DocumentId = document.Id });
            var countAfterList = session.CountBodies(entry);
            step.Observe($"повторный вызов: ListBodies().Count = {rowsSecond.Count}, CountBodies() = {countAfterList}.");
            step.Data["rows_count_second"] = rowsSecond.Count;
            step.Data["count_after_list"] = countAfterList;

            // Does the main body expose faces, i.e. is it a real solid even if the collection lies?
            if (mainBody is ksBody body)
            {
                var faces = (ksFaceCollection)body.FaceCollection();
                step.Observe($"GetMainBody().FaceCollection().GetCount() = {faces.GetCount()} (ожидаем 6 для бруска).");
                step.Data["main_body_faces"] = faces.GetCount();

                var pointer = Marshal.GetIUnknownForObject(body);
                step.Observe($"IUnknown основного тела = 0x{pointer:X}.");
                if (rawCountBeforeRefresh > 0 && firstElement is not null)
                {
                    var collectionPointer = Marshal.GetIUnknownForObject(firstElement);
                    step.Observe($"IUnknown элемента коллекции = 0x{collectionPointer:X} → {(collectionPointer == pointer ? "то же тело" : "ДРУГОЙ объект")}.");
                    step.Data["same_unknown"] = collectionPointer == pointer;
                    Marshal.Release(collectionPointer);
                }

                Marshal.Release(pointer);
            }

            var agrees = countViaAdapter == rows.Count && rows.Count >= 1;
            if (agrees)
            {
                step.Pass($"Расхождение не воспроизводится в этом прогоне: адаптер вернул {rows.Count} строк при {countViaAdapter} телах.");
            }
            else
            {
                step.Fail(
                    $"Расхождение ВОСПРОИЗВЕДЕНО на уровне адаптера: CountBodies()={countViaAdapter}, " +
                    $"ListBodies().Count={rows.Count}, сырой GetCount()={rawCountBeforeRefresh}. " +
                    $"Смотрите поля выше: они говорят, где именно теряется элемент.");
            }

            session.Disconnect(application.Id, closeOwnedApplication: true);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Шаг адаптера не завершён.");
        }
    }
}
