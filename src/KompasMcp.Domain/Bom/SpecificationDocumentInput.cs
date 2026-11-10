namespace KompasMcp.Domain.Bom;

/// <summary>Which source a specification-document read addresses.</summary>
public enum SpecificationDocumentSource
{
    /// <summary>An absolute path to a <c>.spw</c> the call opens itself.</summary>
    Path,

    /// <summary>A document this session already registered.</summary>
    DocumentId,
}

/// <summary>The one source a call addresses, and nothing else.</summary>
public sealed record SpecificationDocumentTarget(SpecificationDocumentSource Source, string Value);

/// <summary>The "exactly one source" rule of <c>kompas_get_specification_document</c>, pure and testable.</summary>
/// <remarks>INVARIANT: both inputs at once and neither of them are the SAME refusal class — the server does
/// not pick a source for the caller, and choosing one of two contradictory sources would silently read a
/// different document than the other field names. An empty or blank string is "not given", never a path.
/// TEST: SpecificationDocumentTests.
/// History: docs/decisions/bom.md#spec-read-spw</remarks>
public static class SpecificationDocumentInput
{
    /// <summary>The rule as the client reads it; one wording for the Host and the Worker.</summary>
    public const string Rule =
        "Нужно РОВНО ОДНО из двух: path (абсолютный путь к .spw) ЛИБО document_id уже открытого "
        + "документа-спецификации. Оба поля сразу или ни одного — отказ до обращения к КОМПАС.";

    /// <summary>The source the two fields name, or <c>null</c> with a refusal when they do not name one.</summary>
    public static SpecificationDocumentTarget? Parse(string? path, string? documentId, out string? refusal)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasDocument = !string.IsNullOrWhiteSpace(documentId);
        if (hasPath && hasDocument)
        {
            refusal = Rule + " Получены оба: путь «" + path + "» и документ «" + documentId + "».";
            return null;
        }

        if (!hasPath && !hasDocument)
        {
            refusal = Rule + " Не получено ни одного.";
            return null;
        }

        refusal = null;
        return hasPath
            ? new SpecificationDocumentTarget(SpecificationDocumentSource.Path, path!.Trim())
            : new SpecificationDocumentTarget(SpecificationDocumentSource.DocumentId, documentId!.Trim());
    }
}
