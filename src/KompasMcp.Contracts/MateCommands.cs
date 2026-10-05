namespace KompasMcp.Contracts;

/// <summary>
/// Контракты домена сопряжений — блок C2 (профиль <c>mates-minimal-v1</c>, режимы
/// <c>MATE-01…MATE-06</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — решение заказчика от 05.10.2026.</b> Сопряжения строятся на документированном
/// API7-пути: <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c> →
/// <c>BaseObject1</c>/<c>BaseObject2</c> → <c>Update()</c>. Метод
/// <c>ksDocument3D.AddMateConstraint</c> документирован как метод ПОСТОЯННОГО сопряжения, но на
/// гранях, полученных документированным путём, вернул <c>False</c> при всех документированных
/// сочетаниях параметров, и <b>причина не установлена</b>; вопрос закрыт решением, а не выводом
/// «метод не работает».
/// </para>
/// <para>
/// <b>Объект сопряжения адресуется парой «компонент + номер грани».</b> Грань берётся
/// документированным <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>
/// (<c>kspart_bodycollection.html</c>) и переносится в API7 как <c>IModelObject</c>. Число тел и
/// граней компонента видно в <see cref="ComponentRowDto.BodyCount"/>/<see cref="ComponentRowDto.FaceCount"/>:
/// без них «компонент вставлен» неотличимо от «вставлен пустой компонент».
/// </para>
/// <para>
/// <b>Тип сопряжения передаётся ИМЕНЕМ, а не числом.</b> Числовые значения
/// <c>MateConstraintType</c> — деталь реализации обёртки; наружу выходит имя, и неизвестное имя
/// отвергается, а не подменяется ближайшим известным.
/// </para>
/// </remarks>
public sealed record CreateMateCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>
    /// Тип сопряжения именем: <c>coincidence</c>, <c>parallel</c>, <c>perpendicular</c>,
    /// <c>tangency</c>, <c>concentric</c>, <c>distance</c>, <c>angle</c>.
    /// </summary>
    public required string ConstraintType { get; init; }

    /// <summary>Ссылка на первый компонент (из <c>kompas_list_components</c>).</summary>
    public required string FirstComponentRef { get; init; }

    /// <summary>Номер грани первого компонента в его <c>FaceCollection()</c>.</summary>
    public required int FirstFaceIndex { get; init; }

    public required string SecondComponentRef { get; init; }

    public required int SecondFaceIndex { get; init; }

    /// <summary>
    /// Вариант выравнивания направлений именем: <c>opposite</c>, <c>cooriented</c>, <c>closest</c>.
    /// <c>null</c> — оставить документированное умолчание КОМПАСа.
    /// </summary>
    public string? Alignment { get; init; }

    /// <summary>
    /// Параметр ограничения (расстояние или угол) — <c>IMateConstraint3D.ParamValue</c>. Для
    /// сопряжений без параметра не задаётся: <c>null</c> означает «не задано», а не ноль.
    /// </summary>
    public double? ParamValue { get; init; }
}

/// <summary>Перечисление сопряжений сборки.</summary>
public sealed record ListMatesCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>Изменить параметр существующего сопряжения (расстояние или угол).</summary>
public sealed record SetMateParameterCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }

    public required double ParamValue { get; init; }
}

/// <summary>Задать признак фиксации компонентов сопряжением.</summary>
public sealed record SetMateFixedCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }

    /// <summary>Именем: <c>none</c>, <c>first</c>, <c>second</c> (по <c>ksMateFixedTypeEnum</c>).</summary>
    public required string Fixed { get; init; }
}

/// <summary>Удалить сопряжение.</summary>
public sealed record DeleteMateCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MateRef { get; init; }
}

/// <summary>
/// Строка сопряжения. Пустое поле означает «не прочитано», а не ноль: молчание прибора не
/// превращается в значение.
/// </summary>
public sealed record MateRowDto
{
    public required string MateRef { get; init; }

    /// <summary>Порядковый номер в <c>MateConstraintCollection</c> — он же адрес ссылки.</summary>
    public int? Ordinal { get; init; }

    /// <summary>Тип сопряжения именем; неизвестное число называется числом, а не выдумкой.</summary>
    public string? ConstraintType { get; init; }

    public string? Alignment { get; init; }

    public string? Fixed { get; init; }

    public double? ParamValue { get; init; }

    public int? Direction { get; init; }

    /// <summary>Тип первого базового объекта, как его называет сам КОМПАС.</summary>
    public string? BaseObject1 { get; init; }

    public string? BaseObject2 { get; init; }

    /// <summary><c>IMateConstraint3D.Valid</c> — подтверждение сопряжения, а не «Update()=true».</summary>
    public bool? Valid { get; init; }

    public string? Name { get; init; }
}

public sealed record ListMatesResult(
    IReadOnlyList<MateRowDto> Mates,
    int MateCount,
    string Route,
    IReadOnlyList<string> Notes);

public sealed record CreateMateResult(
    ReferenceDto MateRef,
    MateRowDto Mate,
    int MateCount,
    VerificationDto Verification);

public sealed record SetMateParameterResult(
    ReferenceDto MateRef,
    double? ParamValueBefore,
    double? ParamValueAfter,
    VerificationDto Verification);

public sealed record SetMateFixedResult(
    ReferenceDto MateRef,
    string? FixedBefore,
    string? FixedAfter,
    VerificationDto Verification);

public sealed record DeleteMateResult(
    ReferenceDto MateRef,
    int MateCountBefore,
    int MateCountAfter,
    VerificationDto Verification);
