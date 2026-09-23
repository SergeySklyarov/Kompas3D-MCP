namespace KompasMcp.Contracts;

/// <summary>Document kind as exposed by the contract (spec 2.3).</summary>
public enum DocumentKind
{
    Part,
    Assembly,
    Drawing,
    Fragment,
}

/// <summary>Axis-aligned box in public units (mm).</summary>
public sealed record BoundingBoxDto(
    IReadOnlyList<double> MinMm,
    IReadOnlyList<double> MaxMm)
{
    public static BoundingBoxDto Empty { get; } = new(
        new double[] { double.NaN, double.NaN, double.NaN },
        new double[] { double.NaN, double.NaN, double.NaN });
}

/// <summary>
/// Everything a caller needs to address a document safely (spec 2.3). Commands must carry
/// <see cref="Id"/> and, for mutations, <see cref="Revision"/> — never rely on the active tab.
/// </summary>
public sealed record DocumentContextDto
{
    public required string Id { get; init; }

    public required string ApplicationId { get; init; }

    public required DocumentKind Kind { get; init; }

    /// <summary>Normalised absolute path, or null while the document has never been saved.</summary>
    public string? Path { get; init; }

    public required bool Dirty { get; init; }

    public required long Revision { get; init; }

    public required int FeatureCount { get; init; }

    public required int BodyCount { get; init; }

    /// <summary>Component instances for an assembly; 0 for a part.</summary>
    public required int ComponentCount { get; init; }

    /// <summary>Unit system the server reports for this document, e.g. "mm".</summary>
    public required string UnitSystem { get; init; }

    /// <summary>Origin of the document model space in mm.</summary>
    public IReadOnlyList<double>? Origin { get; init; }

    /// <summary>Opaque fingerprint of geometry state, used when events are unavailable.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Перечитанное состояние самого документа: <c>!invisibleMode</c>. null — документ не ответил.
    /// Отдельно от видимости приложения, потому что показать приложение и показать документ — два
    /// разных факта (дефект 12.09.2026 был именно в их смешении).
    /// </summary>
    public bool? DocumentVisible { get; init; }

    /// <summary>Режим, в котором документ был создан или открыт (наследуется от экземпляра).</summary>
    public bool DocumentsVisibleMode { get; init; }

    /// <summary>Что вернул <c>ksDocument3D.SetActive()</c> при предъявлении документа.</summary>
    public bool? DocumentActiveReported { get; init; }

    public required ExternalChangeDetection ExternalChangeDetection { get; init; }
}

/// <summary>
/// Opaque handle to a topology element. It is scoped to a document revision: after a rebuild the
/// server rejects it instead of silently re-resolving (spec 1.7).
/// </summary>
public sealed record ReferenceDto
{
    /// <summary>Opaque id, e.g. "face:6f2c…". Raw COM pointers never leave the Worker.</summary>
    public required string Id { get; init; }

    /// <summary>face | edge | body | vertex | feature | sketch | plane | component | view …</summary>
    public required string Kind { get; init; }

    public required string DocumentId { get; init; }

    /// <summary>Document revision the reference was minted against.</summary>
    public required long Revision { get; init; }

    /// <summary>Feature id if the API guarantees stability across rebuilds; null otherwise.</summary>
    public string? PersistentFeatureId { get; init; }

    public BoundingBoxDto? Bbox { get; init; }

    /// <summary>Human-readable hint ("planar face at +Z", "circular edge r=5"). Never used for matching.</summary>
    public string? SemanticHint { get; init; }
}

/// <summary>
/// Rigid placement: origin plus orthonormal X and Y of a right-handed frame; Z = X × Y is
/// computed by the server (spec 1.10). Composition order is documented in Domain.TransformMath.
/// </summary>
public sealed record TransformDto
{
    public required IReadOnlyList<double> OriginMm { get; init; }

    public required IReadOnlyList<double> XAxis { get; init; }

    public required IReadOnlyList<double> YAxis { get; init; }

    /// <summary>Identity placement at the origin.</summary>
    public static TransformDto Identity { get; } = new()
    {
        OriginMm = new double[] { 0, 0, 0 },
        XAxis = new double[] { 1, 0, 0 },
        YAxis = new double[] { 0, 1, 0 },
    };
}

/// <summary>Cursor-bounded list (spec 2.3). A cursor is bound to a revision.</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public string? NextCursor { get; init; }

    /// <summary>Null when the total is not cheaply known — do not fabricate it.</summary>
    public int? TotalKnown { get; init; }
}

/// <summary>How a BOM line is sourced (spec 1.11). Classified by project metadata, not name prefix.</summary>
public enum ItemClassification
{
    Manufactured,
    Purchased,
    Reference,
    Unknown,
}

public sealed record BomRowDto
{
    /// <summary>Grouping key: normalised file + configuration/execution + marking.</summary>
    public required string PartKey { get; init; }

    public string? Marking { get; init; }

    public string? Name { get; init; }

    public string? SourcePath { get; init; }

    public string? Configuration { get; init; }

    public ItemClassification Classification { get; init; }

    /// <summary>Instance count, not distinct file count.</summary>
    public required int Quantity { get; init; }

    /// <summary>Never guessed from a file name (spec 2.3).</summary>
    public string? Material { get; init; }

    public string? Specification { get; init; }

    public required IReadOnlyList<string> SourceInstances { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Session-level facts about one КОМПАС instance (spec 1.6).</summary>
public sealed record ApplicationInfoDto
{
    public required string ApplicationId { get; init; }

    public required int ProcessId { get; init; }

    public required string Version { get; init; }

    public required ApplicationOwnership Ownership { get; init; }

    /// <summary>attach or launch — how this session came to exist.</summary>
    public required string ConnectedAs { get; init; }

    /// <summary>Executable path as registered for this instance.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Фактическая видимость окна приложения: истина только когда и COM-свойство Visible, и
    /// Win32 IsWindowVisible согласны. Наличие HWND или определимый PID видимостью не считаются —
    /// скрытое окно КОМПАС даёт и то, и другое.
    /// </summary>
    public required bool Visible { get; init; }

    /// <summary>Значение <c>KompasObject.Visible</c>, перечитанное у приложения.</summary>
    public bool? ApplicationVisibleByCom { get; init; }

    /// <summary>Ответ <c>IsWindowVisible</c> по главному окну приложения — независимое наблюдение.</summary>
    public bool? ApplicationWindowVisibleByWindows { get; init; }

    /// <summary>Главное окно приложения, как его вернул <c>ksGetHWindow</c> (0 — окно не получено).</summary>
    public long WindowHandle { get; init; }

    /// <summary>Заголовки видимых дочерних окон: окна документов, открытые у пользователя.</summary>
    public IReadOnlyList<string> DocumentVisibleTitles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Чем наблюдение закончилось, если оно не удалось целиком. Отсутствует, когда наблюдено всё.
    /// </summary>
    public string? VisibilityObservationError { get; init; }

    /// <summary>
    /// В каком режиме сервер создаёт и открывает документы этого экземпляра. Отдельно от
    /// видимости приложения: показать окно — не то же самое, что показать документ.
    /// </summary>
    public bool DocumentsVisible { get; init; }

    public required int OpenDocumentCount { get; init; }
}

/// <summary>Intersection verdict categories (spec 1.10). Contact and overlap are different answers.</summary>
public enum IntersectionVerdict
{
    Clear,
    Contact,
    Overlap,
    Unknown,
}

/// <summary>Thread/geometry result of one discrete position probe (spec 2.7 A07).</summary>
public sealed record PositionProbeResultDto
{
    public required int Index { get; init; }
    public required TransformDto Transform { get; init; }
    public required IntersectionVerdict Verdict { get; init; }
    public required IReadOnlyList<string> CollidingInstances { get; init; }
}
