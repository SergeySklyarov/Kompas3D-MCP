using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.Documents;

namespace KompasMcp.Api5Adapter;

/// <summary>The DOCUMENT side of the specification block: a <c>.spw</c> opened by its own documented route,
/// and the rows of that document read independently of the active tab.</summary>
/// <remarks>DOC: <c>idocuments_open.html</c> — <c>Open(PathName, Visible, ReadOnly)</c> «открывает
/// существующий документ и добавляет его в коллекцию» and yields <c>IKompasDocument</c>;
/// <c>documenttypeenum.html</c> — <c>ksDocumentSpecification</c>;
/// <c>ikompasdocument_specificationdescriptions.html</c> — the collection is a property of the BASE
/// <c>IKompasDocument</c>, so the rows do not depend on which document is active.
/// INVARIANT: this route reads; it never activates the document and never writes it.
/// History: docs/decisions/bom.md#spec-read-spw</remarks>
public partial class Api5Session
{
    private const string SpecificationDocumentRoute =
        "api7:IDocuments.Open → IKompasDocument.SpecificationDescriptions.Active → "
        + "ISpecificationDescription.Objects → ISpecificationObject.Columns → ISpecificationColumnItems; "
        + "раздел - ISpecificationStyle.Sections по номеру; вторая дорога - "
        + "api5:SpcActiveDocument().GetSpecification().ksGetSpcObjectColumnText";

    /// <summary><c>DocumentTypeEnum.ksDocumentSpecification</c> — the API7 type of a specification
    /// document.</summary>
    /// <remarks>DOC: <c>documenttypeenum.html</c>, «Спецификация» = 3. Measured on the probe document.
    /// History: docs/decisions/bom.md#spec-read-spw</remarks>
    private const int SpecificationDocumentType = 3;

    private static bool IsSpecificationPath(string path) =>
        string.Equals(Path.GetExtension(path), ".spw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Open a <c>.spw</c> through <c>IDocuments.Open</c> and confirm the type the document reports.</summary>
    /// <remarks>INVARIANT: the extension only ROUTES the call — the kind is the document's own answer, and a
    /// file whose type is not <c>ksDocumentSpecification</c> is closed again and refused with the type.</remarks>
    private DocumentEntry OpenSpecificationDocument(
        ApplicationEntry application, string path, DocumentAccess access)
    {
        GuardFileNotLocked(path);
        var bridge = BridgeForApplication(application);
        if (bridge.Application() is not { Documents: { } documents })
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "API7 недоступен: " + (bridge.BridgeFailure ?? "причина не названа")
                + ". Документ-спецификация открывается только документированным IDocuments.Open.",
                RetryPolicy.Never);
        }

        IKompasDocument? opened;
        try
        {
            opened = documents.Open(path, application.DocumentsVisible, access == DocumentAccess.ReadOnly);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"IDocuments.Open прервался: {ex.Message}. Документ-спецификация не открыт.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["path"] = path });
        }

        if (opened is null)
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Открыть документ-спецификацию не удалось: {path}. IDocuments.Open вернул NULL: файла "
                + "нет, он повреждён или занят другим процессом.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["path"] = path });
        }

        var type = Int(() => (int)opened.DocumentType);
        if (type != SpecificationDocumentType)
        {
            // This call opened the file, so leaving it open would be an unnamed side effect.
            var closed = Bool(() => opened.Close(DocumentCloseOptions.kdDoNotSaveChanges));
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Файл '{path}' открыт, но тип документа {type?.ToString() ?? "не прочитан"} — не "
                + "спецификация (ksDocumentSpecification = 3): строки спецификации из него не читаются. "
                + $"Документ закрыт обратно: {closed switch { true => "да", false => "НЕТ", null => "результат не прочитан" }}.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["document_type"] = type,
                    ["expected_type"] = SpecificationDocumentType,
                    ["closed_by_tool"] = closed,
                });
        }

        var entry = RegisterDocument(
            document: null, part: null, application, DocumentKind.Specification, path,
            kindVerified: true, specification: opened);
        entry.Access = access;
        return entry;
    }

    /// <summary>The rows of a specification DOCUMENT, by <c>path</c> or by <c>document_id</c>.</summary>
    public GetSpecificationDocumentResult GetSpecificationDocument(GetSpecificationDocumentCommand command)
    {
        var (document, openedByTool, notes) = ResolveSpecificationDocument(command);
        if (document.Kind != DocumentKind.Specification)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Документ «{document.Id}» имеет тип {document.Kind}: строки документа-спецификации читает "
                + "только kompas_get_specification_document по файлу .spw. Объекты спецификации МОДЕЛИ "
                + "читает kompas_get_specification.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["kind"] = document.Kind.ToString(),
                    ["expected_kind"] = "specification",
                });
        }

        var unverified = new List<string>();
        var rows = ReadSpecificationDocumentRows(document, notes, unverified);

        return new GetSpecificationDocumentResult(
            new SpecificationDocumentDto(document.Path, document.Kind, openedByTool),
            rows,
            rows.Count,
            SpecificationDocumentRoute,
            notes.Distinct(StringComparer.Ordinal).ToList(),
            unverified);
    }

    /// <summary>The document the call addresses: an already-open one, or a file this call opens.</summary>
    /// <remarks>INVARIANT: exactly one of the two inputs names the source; both or neither is refused before
    /// COM by the same pure rule the Host applies. Opening by path does NOT make the document active: the
    /// answer says it was opened and the closing stays with the client.</remarks>
    private (DocumentEntry Document, bool OpenedByTool, List<string> Notes) ResolveSpecificationDocument(
        GetSpecificationDocumentCommand command)
    {
        var notes = new List<string>();
        var target = SpecificationDocumentInput.Parse(command.Path, command.DocumentId, out var refusal)
            ?? throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                refusal ?? SpecificationDocumentInput.Rule,
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["path"] = command.Path,
                    ["document_id"] = command.DocumentId,
                });

        if (target.Source == SpecificationDocumentSource.DocumentId)
        {
            notes.Add("document_read_by_id — строки прочитаны у документа, открытого ранее: этим вызовом "
                + "он не открывался");
            return (RequireDocument(target.Value), false, notes);
        }

        var existing = TryFindDocumentByPath(target.Value);
        if (existing is not null)
        {
            notes.Add("document_already_open — файл уже открыт этой сессией: возвращён тот же document_id, "
                + "второй экземпляр не открывался");
            return (existing, false, notes);
        }

        var opened = OpenSpecificationDocument(SingleApplicationForOpen(), target.Value, DocumentAccess.ReadOnly);
        notes.Add("document_opened_by_tool — файл открыт этим вызовом в режиме только для чтения; "
            + "закрытие остаётся за клиентом (kompas_close_document)");
        return (opened, true, notes);
    }

    /// <summary>The only connected instance, or a named refusal: the tool carries no <c>application_id</c>.</summary>
    /// <remarks>INVARIANT: with two instances connected the server does not pick one. The remedy is named:
    /// open the file with <c>kompas_open_document</c> (it takes <c>application_id</c>) and read by
    /// <c>document_id</c>.</remarks>
    private ApplicationEntry SingleApplicationForOpen()
    {
        if (_applications.Count == 1)
        {
            return _applications.Values.First();
        }

        if (_applications.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ApplicationDisconnected,
                "Нет подключённого экземпляра КОМПАС: открывать .spw негде. Сначала kompas_connect, затем "
                + "повторите вызов.",
                RetryPolicy.ReacquireContext);
        }

        throw new KompasContractException(
            ErrorCodes.AmbiguousApplication,
            $"Подключено экземпляров КОМПАС: {_applications.Count}. Открыть .spw по path без имени "
            + "экземпляра нельзя: сервер не выбирает экземпляр за вызывающего. Откройте файл через "
            + "kompas_open_document (у него есть application_id) и передайте сюда document_id.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["application_count"] = _applications.Count,
                ["remedy"] = "kompas_open_document с application_id, затем чтение по document_id",
            });
    }

    /// <summary>Every base object of the document as a row, with the second road compared where it exists.</summary>
    /// <remarks>INVARIANT: a field that did not read stays null AND is named in the notes; a row is never
    /// dropped silently. A disagreement between the two roads is a note, not a silent preference.</remarks>
    private List<SpecificationLineDto> ReadSpecificationDocumentRows(
        DocumentEntry document, List<string> notes, List<string> unverified)
    {
        var rows = new List<SpecificationLineDto>();
        var description = ActiveSpecificationDescription(document, notes);
        if (description is null)
        {
            return rows;
        }

        var sectionNames = SpecificationSectionNames(description, notes);
        var objects = SpecificationObjects(description, notes);
        if (objects.Count == 0)
        {
            notes.Add("specification_empty — в документе нет объектов спецификации: список пуст");
            return rows;
        }

        var specification5 = Specification5OfActiveDocument(document, unverified);

        foreach (var item in objects)
        {
            var sectionNumber = Int(() => item.Section);
            var section = sectionNumber is int number && sectionNames.TryGetValue(number, out var name)
                ? name
                : null;
            if (section is null && sectionNumber is not null)
            {
                notes.Add($"specification_section_name_absent — раздел {sectionNumber} не найден в стиле "
                    + "спецификации: название раздела не прочитано, номер прочитан");
            }

            var row = new SpecificationLineDto(
                section,
                sectionNumber,
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnPosition, "position", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnMark, "marking", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnName, "name", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnCount, "quantity", notes));
            rows.Add(row);

            if (specification5 is not null)
            {
                CompareRowWithApi5(specification5, item, row, notes);
            }
        }

        return rows;
    }

    /// <summary>Section number to its name, from the documented style collection.</summary>
    /// <remarks>DOC: <c>ispecificationstyle_sections.html</c>, <c>ispecificationsectionstyles_item.html</c>,
    /// <c>ispecificationsectionstyle_number.html</c>, <c>ispecificationsectionstyle_name.html</c>.
    /// The API5 route needs an ACTIVE document, so the style is the road that works for any .spw.
    /// History: docs/decisions/bom.md#spec-read-spw</remarks>
    private static Dictionary<int, string> SpecificationSectionNames(
        ISpecificationDescription description, List<string> notes)
    {
        var names = new Dictionary<int, string>();
        try
        {
            var sections = description.SpecificationStyle?.Sections;
            for (var index = 0; sections is not null; index++)
            {
                if (sections[index] is not ISpecificationSectionStyle section)
                {
                    break;
                }

                var number = Int(() => section.Number);
                var name = Text(() => section.Name);
                if (number is int value && name is not null)
                {
                    names[value] = name;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"specification_style_unread — стиль спецификации не прочитан: {ex.Message}; названия "
                + "разделов не разрешаются по номеру");
        }

        return names;
    }

    /// <summary>The <c>ksSpecification</c> of the ACTIVE document, or null with the reason named.</summary>
    /// <remarks>DOC: <c>kompasobject_spcactivedocument.html</c> — «Если документ спецификации не активен,
    /// функция возвращает NULL»; <c>ksspcdocument_getspecification.html</c>.
    /// INVARIANT: the document is not activated to make this road available — that would change the user's
    /// window. Identity is checked on the API7 object before the API5 call is trusted.</remarks>
    private ksSpecification? Specification5OfActiveDocument(DocumentEntry document, List<string> unverified)
    {
        const string unavailable = "api5_road_unavailable — вторая, независимая дорога (API5 "
            + "SpcActiveDocument → GetSpecification → ksGetSpcObjectColumnText) ";
        var application = _applications.TryGetValue(document.ApplicationId, out var entry) ? entry : null;
        var document7 = DocumentAsApi7(document);
        if (application is null || document7 is null)
        {
            unverified.Add(unavailable + "недоступна: экземпляр или API7-дескриптор документа не найден; "
                + "строки прочитаны ОДНОЙ дорогой (API7)");
            return null;
        }

        var active = SafeRead.Ref(() => BridgeForApplication(application).Application()?.ActiveDocument);
        if (active is null)
        {
            unverified.Add(unavailable + "недоступна: активный документ сеанса не прочитан");
            return null;
        }

        if (!SameComObject(active, document7))
        {
            unverified.Add(unavailable + "недоступна: документ-спецификация не активен, а активировать его "
                + "чтение не вправе; строки прочитаны ОДНОЙ дорогой (API7)");
            return null;
        }

        try
        {
            // The API5 root is asked only after the ACTIVE document was proved to be ours.
            var active5 = application.Application.SpcActiveDocument() as ksSpcDocument;
            if (active5?.GetSpecification() is ksSpecification specification)
            {
                return specification;
            }

            unverified.Add(unavailable + "недоступна: SpcActiveDocument().GetSpecification() не дал "
                + "ksSpecification");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            unverified.Add(unavailable + $"прервалась: {ex.Message}");
        }

        return null;
    }

    /// <summary>Compare one row with what API5 reads for the same object, and NAME every disagreement.</summary>
    /// <remarks>INVARIANT: the pointer is taken by the object's own unique number and the number is read back,
    /// so a neighbour's texts cannot stand in for this row. A comparison that cannot be made is a note.
    /// DOC: <c>ksspecification_ksgetspcobject.html</c>, <c>ksspecification_ksgetspcobjectnumber.html</c>,
    /// <c>ksspecification_ksgetspcobjectcolumntext.html</c>, <c>ksspecification_ksgetspcsectionname.html</c>.
    /// History: docs/decisions/bom.md#spec-read-spw</remarks>
    private static void CompareRowWithApi5(
        ksSpecification specification, ISpecificationBaseObject item, SpecificationLineDto row,
        List<string> notes)
    {
        var unique = Double(() => item.UniqueNumber);
        var label = row.Marking ?? (unique?.ToString(CultureInfo.InvariantCulture) ?? "объект без номера");
        if (unique is null)
        {
            notes.Add($"specification_api5_not_compared — {label}: уникальный номер объекта не прочитан");
            return;
        }

        var pointer = Int(() => specification.ksGetSpcObject(unique.Value));
        if (pointer is null or <= 0)
        {
            notes.Add($"specification_api5_not_compared — {label}: ksGetSpcObject({unique.Value}) не дал "
                + "указатель");
            return;
        }

        var readBack = Double(() => specification.ksGetSpcObjectNumber(pointer.Value));
        if (readBack is not null && Math.Abs(readBack.Value - unique.Value) > double.Epsilon)
        {
            notes.Add($"specification_api5_not_compared — {label}: указатель отвечает номером {readBack} "
                + $"вместо {unique}: сверка не выполнена");
            return;
        }

        var pairs = new (string Field, string? Api7, string? Api5)[]
        {
            ("position", row.Position, Text5(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnPosition)),
            ("marking", row.Marking, Text5(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnMark)),
            ("name", row.Name, Text5(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnName)),
            ("quantity", row.Quantity, Text5(specification, pointer.Value, ksSpecificationColumnTypeEnum.ksSColumnCount)),
            ("section", row.Section, Text(() => specification.ksGetSpcSectionName(pointer.Value))),
        };

        foreach (var (field, api7, api5) in pairs)
        {
            if (api5 is null)
            {
                notes.Add($"specification_api5_not_compared — {label}.{field}: API5 текст не прочитал");
                continue;
            }

            if (!string.Equals(api7 ?? string.Empty, api5, StringComparison.Ordinal))
            {
                notes.Add($"specification_api5_disagrees — {label}.{field}: API7 «{api7}» и API5 «{api5}»: "
                    + "дороги разошлись, опубликовано значение API7");
            }
        }
    }

    private static string? Text5(
        ksSpecification specification, int pointer, ksSpecificationColumnTypeEnum type) =>
        Text(() => specification.ksGetSpcObjectColumnText(
            pointer, (int)type, SpecificationColumnNumbers.FirstOfType, SpecificationColumnNumbers.MainBlock));

    /// <summary>One column of a row read through the documented API7 accessor chain.</summary>
    /// <remarks>DOC: <c>ispecificationobject_columns.html</c>, <c>ispecificationcolumns_column.html</c>
    /// («type, ColumnNumber, BlockNumber»), <c>ispecificationcolumn_columnitems.html</c>,
    /// <c>ispecificationcolumnitems_item.html</c>, <c>ispecificationcolumnitem_value.html</c>.
    /// INVARIANT: an absent column and an unread one are named apart, and a column of several элементов
    /// (исполнения) is joined in order instead of one being picked for the caller.
    /// History: docs/decisions/bom.md#spec-read-spw</remarks>
    private static string? Api7Column(
        ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type, string field, List<string> notes)
    {
        try
        {
            var columns = item.Columns;
            if (columns is null)
            {
                notes.Add($"specification_columns_absent — у объекта нет ISpecificationColumns: поле {field} "
                    + "не прочитано");
                return null;
            }

            var column = columns.Column[
                type, SpecificationColumnNumbers.FirstOfType, SpecificationColumnNumbers.MainBlock];
            if (column is null)
            {
                notes.Add($"specification_column_absent — в стиле нет колонки {field} (тип {(int)type}): поле "
                    + "не прочитано, а не пусто");
                return null;
            }

            if (column.ColumnItems is not ISpecificationColumnItems items || items.Count == 0)
            {
                notes.Add($"specification_column_empty — колонка {field} (тип {(int)type}) пришла без "
                    + "элементов: поле не прочитано");
                return null;
            }

            var values = new List<string>();
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index]?.Value is { } value)
                {
                    values.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            if (values.Count == 0)
            {
                notes.Add($"specification_column_unread — у колонки {field} (тип {(int)type}) ни один "
                    + "элемент не дал значения");
                return null;
            }

            return string.Join("|", values);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            notes.Add($"specification_column_unread — колонка {field} (тип {(int)type}) прервалась: "
                + $"{ex.Message}");
            return null;
        }
    }

    /// <summary>Digest of a <c>.spw</c>: how many objects it has and the texts of their columns.</summary>
    /// <remarks>INVARIANT: the savedness of a specification document is judged by what a read of it shows —
    /// its rows — because it has no <c>ksDocument3D</c> whose body count and box could answer instead.
    /// History: docs/decisions/bom.md#spec-read-spw</remarks>
    private string SpecificationFingerprint(DocumentEntry document)
    {
        try
        {
            var description = document.SpecificationDocument?.SpecificationDescriptions?.Active;
            var objects = description?.Objects;
            var items = objects switch
            {
                null => new List<object?>(),
                Array array => array.Cast<object?>().ToList(),
                _ => [objects],
            };

            var ignored = new List<string>();
            var parts = new List<string>();
            foreach (var item in items.OfType<ISpecificationBaseObject>())
            {
                parts.Add(Text(() => item.UniqueNumber.ToString(CultureInfo.InvariantCulture)) ?? "?");
                foreach (var type in new[]
                         {
                             ksSpecificationColumnTypeEnum.ksSColumnPosition,
                             ksSpecificationColumnTypeEnum.ksSColumnMark,
                             ksSpecificationColumnTypeEnum.ksSColumnName,
                             ksSpecificationColumnTypeEnum.ksSColumnCount,
                         })
                {
                    parts.Add(Api7Column(item, type, "fingerprint", ignored) ?? "?");
                }
            }

            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
            return $"spw:{items.Count}:{digest[..16]}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return DocumentSaveTracking.UnreadableFingerprint;
        }
    }
}
