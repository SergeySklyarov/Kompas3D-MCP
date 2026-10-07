using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Enumeration of a part's SKETCHES (<c>kompas_list_sketches</c>).</summary>
/// <remarks>
/// DOC: <c>ksPart.EntityCollection(short objType)</c> — «При создании массив заполняется объектами
/// указанного типа, содержащимися в компоненте» (<c>kspart_entitycollection.html</c>) — with the type
/// <c>o3d_sketch = 5</c>, «эскиз» → <c>ksSketchDefinition</c> / <c>ISketch</c> (<c>obj3dtype.html</c>).
/// The same route is documented for API7 (<c>IModelContainer::Sketchs</c> → <c>ISketchs</c>,
/// <c>isketchs.html</c>); the API5 collection is taken because a row must carry the SAME <c>ksEntity</c>
/// the rest of the server addresses a sketch by, and because that collection was MEASURED to still yield
/// sketches with readable definitions after save → close → reopen (probe L).
/// INVARIANT: this is a READ — no <c>BeginEdit</c>, no <c>Update</c>, no rebuild, and the revision is not
/// bumped. A sketch reference minted here is bound to the current revision, so the usual staleness rules
/// apply to it unchanged.
/// INVARIANT: an unread field is <c>null</c> and named in the row notes, never zero and never "no".
/// History: docs/decisions/adapter-sketch.md#sketch-enumeration
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Every sketch of the document, each with a reference usable by the sketch tools.</summary>
    public IReadOnlyList<SketchRowDto> ListSketches(ListSketchesCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var rows = new List<SketchRowDto>();

        var collection = (ksEntityCollection)document.PartNow()
            .EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.Sketch));

        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (collection.GetByIndex(i) is not ksEntity entity)
            {
                continue;
            }

            var notes = new List<string>();
            var created = TryIsCreated(entity, notes);

            string? supportName = null;
            if (entity.GetDefinition() is ksSketchDefinition definition)
            {
                // The support is read by the same member the support-change tool uses; a failure is named
                // rather than reported as "the sketch has no support".
                supportName = TryGetPlaneName(definition);
                if (supportName is null)
                {
                    notes.Add("опора эскиза не прочитана (ksSketchDefinition.GetPlane() вернул пусто)");
                }
            }
            else
            {
                notes.Add("ksSketchDefinition эскиза не получен: опора не читалась");
            }

            rows.Add(new SketchRowDto
            {
                SketchRef = References.Register("sketch", document.Id, document.Revision, entity).Id,
                Name = entity.name,
                Index = i,
                Created = created,
                SupportPlaneName = supportName,
                Notes = notes,
            });
        }

        return rows;
    }

    /// <summary>The <c>IsCreated()</c> flag, or <c>null</c> when the call itself failed. A failed read is
    /// NOT "not created": the two are different statements about the model, and only one of them is a
    /// fact about it.</summary>
    private static bool? TryIsCreated(ksEntity entity, List<string> notes)
    {
        try
        {
            return entity.IsCreated();
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or InvalidCastException)
        {
            notes.Add($"признак создания не прочитан ({ex.GetType().Name}): это НЕ «не создан»");
            return null;
        }
    }
}
