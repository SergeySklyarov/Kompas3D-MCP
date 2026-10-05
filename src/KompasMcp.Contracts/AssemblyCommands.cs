namespace KompasMcp.Contracts;

/// <summary>
/// Контракты домена сборок — наряд C1 (профиль <c>assemblies-minimal-v1</c>, режимы
/// <c>ASM-01…ASM-07</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Компонент — это ссылка на файл, а не тело.</b> Поэтому наружу выходит не геометрия, а
/// структура: экземпляр компонента, его источник (файл), размещение и кратность. Две вставки одной
/// детали дают ДВА экземпляра ОДНОЙ уникальной детали — это и есть содержательный критерий блока,
/// отделяющий сборку от композиции тел в одной детали.
/// </para>
/// <para>
/// <b>Размещение задаётся жёстким преобразованием</b> (<see cref="TransformDto"/>: начало и две
/// ортонормированные оси), а не «сдвигом от текущего»: сдвиг без системы координат — не адрес, и
/// перечитать его обратно нельзя.
/// </para>
/// </remarks>
public sealed record InsertComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Абсолютный путь к файлу-источнику детали внутри разрешённого корня (проверен Хостом).</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Размещение компонента. <c>null</c> означает «не задано»: компонент вставляется по
    /// документированному умолчанию КОМПАСа, а не по догадке сервера о начале координат.
    /// </summary>
    public TransformDto? Transform { get; init; }

    /// <summary>Зафиксировать компонент после вставки. Зафиксированный компонент перемещать нельзя.</summary>
    public bool Fixed { get; init; } = true;
}

/// <summary>Перечисление структуры сборки.</summary>
public sealed record ListComponentsCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Обходить ли вложенные подсборки.</summary>
    public bool Recursive { get; init; }
}

/// <summary>Задать размещение компонента жёстким преобразованием и перечитать его.</summary>
public sealed record SetComponentPlacementCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string ComponentRef { get; init; }

    public required TransformDto Transform { get; init; }
}

/// <summary>Заменить источник компонента с сохранением размещения.</summary>
public sealed record ReplaceComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string ComponentRef { get; init; }

    /// <summary>Новый файл-источник внутри разрешённого корня.</summary>
    public required string SourcePath { get; init; }
}

/// <summary>Проверка ссылок компонентов на файлы-источники.</summary>
public sealed record CheckComponentLinksCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>
/// Строка структуры сборки. Пустое поле означает «не прочитано», а не ноль; причина называется в
/// <see cref="ListComponentsResult.Notes"/>.
/// </summary>
public sealed record ComponentRowDto
{
    /// <summary>Непрозрачная ссылка на экземпляр, привязанная к ревизии.</summary>
    public required string ComponentRef { get; init; }

    /// <summary>Ссылка на родительский узел (подсборку); null — компонент верхнего уровня.</summary>
    public string? ParentRef { get; init; }

    /// <summary>Глубина вложенности: 0 — верхний уровень.</summary>
    public required int Depth { get; init; }

    public string? Name { get; init; }

    /// <summary>Обозначение компонента (<c>IPart7.Marking</c>).</summary>
    public string? Marking { get; init; }

    /// <summary>Имя файла-источника (<c>IPart7.FileName</c>).</summary>
    public string? SourcePath { get; init; }

    /// <summary>Признак «деталь/сборка» (<c>IPart7.Detail</c> или <c>ksPart.IsDetail</c>).</summary>
    public bool? IsDetail { get; init; }

    /// <summary>Кратность: число вставок этой детали (<c>IPart7.InstanceCount</c>).</summary>
    public int? InstanceCount { get; init; }

    /// <summary>
    /// Число тел компонента — <c>ksPart.BodyCollection()</c>
    /// (<c>kspart_bodycollection.html</c>). Названо наружу потому, что «компонент есть» и «у
    /// компонента есть геометрия» — разные утверждения: вставка методом
    /// <c>CreatePartInAssembly</c> давала компонент с НУЛЁМ тел (измерено 05.10.2026).
    /// </summary>
    public int? BodyCount { get; init; }

    /// <summary>Число граней первого тела компонента — <c>ksBody.FaceCollection()</c>.</summary>
    public int? FaceCount { get; init; }

    /// <summary>
    /// Номер компонента в документе (<c>IPart7.Reference</c>) — он же аргумент
    /// <c>ksPart.GetPart</c>. Назван наружу, потому что адресация компонента держится на нём, и
    /// «ссылка есть, а номера нет» — это ровно то, что делает адрес непроверяемым.
    /// </summary>
    public int? ReferenceNumber { get; init; }

    /// <summary>Состояние фиксации (<c>IPart7.Fixed</c>).</summary>
    public bool? Fixed { get; init; }

    /// <summary>Состояние загрузки источника (<c>IPart7.LoadState</c>, <c>ksLoadStateEnum</c>).</summary>
    public string? LoadState { get; init; }

    /// <summary>Суммарная матрица размещения (16 чисел 4×4), если прочитана.</summary>
    public IReadOnlyList<double>? Matrix { get; init; }
}

/// <summary>Ответ перечисления структуры сборки.</summary>
public sealed record ListComponentsResult(
    IReadOnlyList<ComponentRowDto> Components,
    int UniquePartCount,
    int InstanceCount,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Результат вставки компонента: перечитанный из модели, а не пересказ запроса.</summary>
public sealed record InsertComponentResult(
    ReferenceDto ComponentRef,
    ComponentRowDto Component,
    int ComponentCount,
    VerificationDto Verification);

/// <summary>Результат задания размещения: размещение ДО и ПОСЛЕ из одного момента.</summary>
public sealed record SetComponentPlacementResult(
    ReferenceDto ComponentRef,
    IReadOnlyList<double> PlacementBeforeMatrix,
    IReadOnlyList<double> PlacementAfterMatrix,
    VerificationDto Verification);

/// <summary>Результат замены источника компонента с сохранением размещения.</summary>
public sealed record ReplaceComponentResult(
    ReferenceDto ComponentRef,
    string? SourcePathBefore,
    string? SourcePathAfter,
    IReadOnlyList<double>? PlacementBeforeMatrix,
    IReadOnlyList<double>? PlacementAfterMatrix,
    int ComponentCount,
    VerificationDto Verification);

/// <summary>Строка проверки ссылки компонента на файл-источник.</summary>
public sealed record ComponentLinkDto(
    string ComponentRef,
    string? Name,
    string? SourcePath,
    bool? SourceExists,
    string? LoadState,
    string Verdict);

/// <summary>Результат проверки ссылок: перечень с вердиктом по каждой.</summary>
public sealed record CheckComponentLinksResult(
    IReadOnlyList<ComponentLinkDto> Links,
    int BrokenCount,
    VerificationDto Verification);
