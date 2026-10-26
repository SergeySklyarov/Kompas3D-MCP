using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;

namespace KompasMcp.Api5Adapter;

/// <summary>Level (b) of the BOM — the native specification objects of a model, block <c>BOM_SPEC_READ</c>,
/// mode <c>BOM-04</c>.</summary>
/// <remarks>DOC: the list comes from <c>ispecificationdescription_objects.html</c> («полный список объектов
/// спецификации»), the texts and the section name from <c>ksspecification_ksgetspcobjectcolumntext.html</c>
/// and <c>ksspecification_ksgetspcsectionname.html</c>.
/// INVARIANT: nothing is counted and nothing is mixed with the component tree — this answers what the
/// designer put into the specification. An empty specification is an empty list with a note, not a refusal.
/// History: docs/decisions/bom.md#spec-read</remarks>
public partial class Api5Session
{
    private const string SpecificationRoute =
        "api7:IKompasDocument.SpecificationDescriptions.Active → ISpecificationDescription.Objects → "
        + "ISpecificationBaseObject.UniqueNumber → api5:ksDocument3D.GetSpecification().ksGetSpcObject → "
        + "ksGetSpcSectionName + ksGetSpcObjectColumnText(тип колонки)";

    /// <summary>The specification rows of an open model document.</summary>
    public GetSpecificationResult GetSpecification(GetSpecificationCommand command)
    {
        var document = RequireModelForSpecification(command.DocumentId);
        var notes = new List<string>();
        var rows = new List<SpecificationLineDto>();

        var description = ActiveSpecificationDescription(document, notes);
        if (description is null)
        {
            return new GetSpecificationResult(rows, 0, SpecificationRoute, notes);
        }

        var objects = SpecificationObjects(description, notes);
        if (objects.Count == 0)
        {
            notes.Add("specification_empty — в документе нет объектов спецификации: список пуст");
            return new GetSpecificationResult(rows, 0, SpecificationRoute, notes);
        }

        var specification = Specification5(document);
        foreach (var item in objects)
        {
            var row = ReadSpecificationLine(specification, item, notes);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return new GetSpecificationResult(rows, rows.Count, SpecificationRoute, notes);
    }

    /// <summary>The description the document shows, or null when it has none.</summary>
    /// <remarks>DOC: <c>ikompasdocument_specificationdescriptions.html</c> — the collection comes from the
    /// document; <c>ispecificationdescriptions_active.html</c> — «Если в документе нет описаний
    /// спецификаций, возвращается NULL». A model may legally have none, so null is an answer, not a fault.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private ISpecificationDescription? ActiveSpecificationDescription(DocumentEntry document, List<string> notes)
    {
        var document7 = DocumentAsApi7(document);
        if (document7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Документ «{document.Id}» не переносится в API7: описания спецификации недоступны.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            var descriptions = document7.SpecificationDescriptions;
            if (descriptions is null)
            {
                notes.Add("specification_descriptions_absent — IKompasDocument.SpecificationDescriptions "
                    + "вернул null: описание спецификации не прочитано");
                return null;
            }

            var active = descriptions.Active;
            if (active is null)
            {
                notes.Add("specification_absent — у документа нет описания спецификации "
                    + "(ISpecificationDescriptions.Active = NULL): объектов спецификации нет");
            }

            return active;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Описание спецификации не читается через API7: {ex.Message}",
                RetryPolicy.ReacquireContext);
        }
    }

    /// <summary>The whole list of specification objects in display order.</summary>
    /// <remarks>DOC: <c>ispecificationdescription_objects.html</c> — VARIANT: one object is VT_DISPATCH,
    /// several are VT_ARRAY|VT_DISPATCH, and the order is the visual order of the description.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static List<ISpecificationBaseObject> SpecificationObjects(
        ISpecificationDescription description, List<string> notes)
    {
        var objects = new List<ISpecificationBaseObject>();
        try
        {
            switch (description.Objects)
            {
                case null:
                    notes.Add("specification_objects_null — ISpecificationDescription.Objects вернул null: "
                        + "список объектов не прочитан");
                    break;
                case Array array:
                    foreach (var item in array)
                    {
                        if (item is ISpecificationBaseObject single)
                        {
                            objects.Add(single);
                        }
                        else
                        {
                            notes.Add($"specification_object_not_base — в списке объект типа "
                                + $"{item?.GetType().Name ?? "null"}: базовым объектом он не является, "
                                + "строка не прочитана");
                        }
                    }

                    break;
                case ISpecificationBaseObject single:
                    objects.Add(single);
                    break;
                default:
                    notes.Add("specification_objects_unexpected — ISpecificationDescription.Objects вернул "
                        + $"{description.Objects.GetType().Name}, а не SAFEARRAY/VT_DISPATCH: список не прочитан");
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"specification_objects_failed — Objects прервался: {ex.Message}");
        }

        return objects;
    }

    /// <summary>The API5 side, which owns the texts and the section name.</summary>
    /// <remarks>DOC: <c>ksdocument3d_getspecification.html</c> — <c>ksDocument3D.GetSpecification()</c>
    /// returns the <c>ksSpecification</c> of the model.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static ksSpecification Specification5(DocumentEntry document)
    {
        try
        {
            if (document.Document3D.GetSpecification() is ksSpecification specification)
            {
                return specification;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"ksDocument3D.GetSpecification() не дал ksSpecification: {ex.Message}",
                RetryPolicy.ReacquireContext);
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            "ksDocument3D.GetSpecification() не вернул ksSpecification: объекты спецификации "
            + "документированным маршрутом не читаются.",
            RetryPolicy.ReacquireContext);
    }

    /// <summary>One row: the pointer is taken by the object's OWN unique number, and the number read back
    /// proves the pointer addresses that object and not a neighbour.</summary>
    /// <remarks>DOC: <c>ispecificationobject_uniquenumber.html</c> («присваивается при создании»),
    /// <c>ksspecification_ksgetspcobject.html</c> («по уникальному номеру»),
    /// <c>ksspecification_ksgetspcobjectnumber.html</c> (read it back).
    /// INVARIANT: a row whose pointer does not answer with the same number is NOT published — a foreign
    /// object's texts under this row's identity would be a wrong answer, not a missing one.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static SpecificationLineDto? ReadSpecificationLine(
        ksSpecification specification, ISpecificationBaseObject item, List<string> notes)
    {
        var unique = Double(() => item.UniqueNumber);
        var sectionNumber = Int(() => item.Section);
        if (unique is null)
        {
            notes.Add($"specification_object_number_unread — у объекта раздела {sectionNumber?.ToString() ?? "?"} "
                + "уникальный номер не прочитан: строка не опубликована");
            return null;
        }

        var pointer = Int(() => specification.ksGetSpcObject(unique.Value));
        if (pointer is null or <= 0)
        {
            notes.Add($"specification_object_pointer_absent — ksGetSpcObject({unique.Value}) не дал указатель: "
                + "строка не опубликована");
            return null;
        }

        var readBack = Double(() => specification.ksGetSpcObjectNumber(pointer.Value));
        if (readBack is null || Math.Abs(readBack.Value - unique.Value) > double.Epsilon)
        {
            notes.Add($"specification_object_pointer_mismatch — указатель отвечает номером "
                + $"{readBack?.ToString() ?? "?"} вместо {unique.Value}: строка не опубликована");
            return null;
        }

        return new SpecificationLineDto(
            Text(() => specification.ksGetSpcSectionName(pointer.Value)),
            sectionNumber,
            Column(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnPosition),
            Column(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnMark),
            Column(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnName),
            Column(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnCount));
    }

    /// <summary>The text of one column, named by its documented TYPE.</summary>
    /// <remarks>DOC: <c>ksspecification_ksgetspcobjectcolumntext.html</c> — «ispoln — номер колонки данного
    /// типа, начиная с 1; block — номер блока исполнений, начиная с 0»; the type comes from
    /// <c>ksspecificationcolumntypeenum.html</c>. The ordinal column number is never used: it depends on the
    /// specification style, the type does not.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static string? Column(
        ksSpecification specification, int pointer, ksSpecificationColumnTypeEnum type) =>
        Text(() => specification.ksGetSpcObjectColumnText(
            pointer, (int)type, SpecificationColumnNumbers.FirstOfType, SpecificationColumnNumbers.MainBlock));

    /// <summary>A model document: a specification belongs to an assembly or a part.</summary>
    /// <remarks>LIMIT: a drawing and a fragment are refused — the order reads specification objects of a
    /// model; a drawing's specification description is a different level and is not in this contract.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private DocumentEntry RequireModelForSpecification(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind is DocumentKind.Assembly or DocumentKind.Part)
        {
            return document;
        }

        throw new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Документ «{documentId}» имеет тип {document.Kind}: чтение объектов спецификации — только по "
            + "документу-модели (сборка или деталь).",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["kind"] = document.Kind.ToString(),
                ["supported_kinds"] = new[] { "assembly", "part" },
            });
    }
}
