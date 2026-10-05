namespace KompasMcp.Domain.References;

/// <summary>
/// Состояние одного признака тождества компонента: прочитан ли он и совпал ли.
/// </summary>
/// <remarks>
/// Три состояния, а не <c>bool</c>: «не прочитано» и «прочитано и совпало» — разные утверждения, и
/// подмена первого вторым давала бы подтверждение адреса по молчанию COM.
/// </remarks>
public enum ComponentIdentitySignal
{
    /// <summary>Признак не прочитан хотя бы с одной стороны — сведения нет.</summary>
    NotRead,

    /// <summary>Признак прочитан с обеих сторон и совпал.</summary>
    Matches,

    /// <summary>Признак прочитан и РАСХОДИТСЯ.</summary>
    Differs,
}

/// <summary>
/// Вердикт тождества адреса компонента.
/// </summary>
/// <param name="Matches">
/// <c>true</c> — тождество подтверждено; <c>false</c> — адрес ведёт в ЧУЖОЙ компонент;
/// <c>null</c> — сверить нечем.
/// </param>
/// <param name="Detail">Человекочитаемое объяснение: какие признаки читались и что решило вердикт.</param>
public sealed record ComponentIdentityVerdict(bool? Matches, string Detail);

/// <summary>
/// Правило тождества адреса компонента — ЧИСТАЯ функция от трёх признаков, без КОМПАС.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем вынесено сюда.</b> Порядковый номер из <c>IPart7.PartsEx</c> применяется как индекс в
/// плоской <c>ksDocument3D.PartCollection(true)</c> — это ПРЕДПОЛОЖЕНИЕ, и перед мутацией оно
/// сверяется. Правило сверки жило разложенным по ветвям адаптера, где регрессию нельзя было ни
/// увидеть тестом, ни назвать одним местом. Здесь оно — таблица, проверяемая без КОМПАС.
/// </para>
/// <para>
/// <b>ОТКАЗ РЕШАЕТ ТОЛЬКО ИСТОЧНИК.</b> Из трёх признаков отказом управляет лишь файл-источник:
/// это единственный признак, который (а) измерен живьём, (б) не меняется сам по себе — источник
/// компонента меняет только явная замена. Имя компонента в дереве и матрица размещения — ПРИМЕЧАНИЯ,
/// а не основание отказа:
/// </para>
/// <list type="bullet">
/// <item><description>
/// сравнение имён API5 (<c>ksPart.name</c>) и API7 (<c>IPart7.Name</c>) живьём не измерялось, и
/// систематическое расхождение форматов отвергало бы ВСЕ мутации сборки;
/// </description></item>
/// <item><description>
/// матрица размещения — ИЗМЕНЯЕМОЕ состояние, а не тождество: её меняет и собственная мутация
/// сервера, и сопряжение. Снимок матрицы, снятый при перечислении структуры, отвергал бы вторую
/// мутацию по той же ссылке; а раскладка <c>IPart7.GetSummMatrix</c> в справке не описана и живьём
/// не измерена, поэтому по <c>AGENTS.md</c> она не может быть основанием поведения.
/// </description></item>
/// </list>
/// <para>
/// Расхождение имени и матрицы НАЗЫВАЕТСЯ в примечании — молчание неотличимо от «не смотрели».
/// </para>
/// </remarks>
public static class ComponentIdentity
{
    /// <summary>
    /// Почему имя и матрица не решают отказ. Печатается в примечании, когда оно вообще нужно.
    /// </summary>
    public const string SecondarySignalsAreInformative =
        "Отказ решает только ИСТОЧНИК: имя компонента и матрица размещения — примечания. Сравнение имён "
        + "API5/API7 живьём не измерялось, а матрица размещения — изменяемое состояние (её меняет и "
        + "собственная мутация, и сопряжение), раскладка IPart7.GetSummMatrix не измерена.";

    /// <summary>
    /// Свести три признака в один вердикт.
    /// </summary>
    /// <param name="source">Файл-источник компонента: <c>ksPart.fileName</c> против <c>IPart7.FileName</c>.</param>
    /// <param name="name">Имя компонента в дереве: <c>ksPart.name</c> против <c>IPart7.Name</c>.</param>
    /// <param name="placement">
    /// Матрица размещения: API5 по номеру против API7 <c>GetSummMatrix</c>, снятые НА ОДИН МОМЕНТ.
    /// </param>
    public static ComponentIdentityVerdict Decide(
        ComponentIdentitySignal source,
        ComponentIdentitySignal name,
        ComponentIdentitySignal placement)
    {
        var signals = string.Join("; ",
            DescribeSource(source),
            DescribeSecondary("имя компонента", name),
            DescribeSecondary("матрица размещения", placement));

        if (source == ComponentIdentitySignal.Differs)
        {
            return new ComponentIdentityVerdict(false,
                signals + ". Источник РАСХОДИТСЯ: по этому номеру лежит компонент с ДРУГИМ "
                + "файлом-источником, значит номер ведёт не туда, и мутация по нему не выполняется. "
                + SecondarySignalsAreInformative);
        }

        if (source == ComponentIdentitySignal.Matches)
        {
            // Совпадение источника подтверждает адрес. Расхождение имени или матрицы отказом НЕ
            // управляет, но, если оно есть, о нём честно говорится — вместе с причиной.
            var secondaryDiffers = name == ComponentIdentitySignal.Differs
                || placement == ComponentIdentitySignal.Differs;
            return new ComponentIdentityVerdict(true,
                secondaryDiffers ? signals + ". " + SecondarySignalsAreInformative : signals);
        }

        return new ComponentIdentityVerdict(null,
            signals + ". Источник не прочитан с одной из сторон, а имя и матрица отказом не управляют, "
            + "поэтому подтвердить адрес НЕЧЕМ: мутация по неподтверждённому адресу не выполняется. "
            + SecondarySignalsAreInformative);
    }

    private static string DescribeSource(ComponentIdentitySignal signal) => signal switch
    {
        ComponentIdentitySignal.Matches => "источник (файл) совпал",
        ComponentIdentitySignal.Differs => "источник (файл) РАСХОДИТСЯ",
        _ => "источник (файл) не читается с одной из сторон",
    };

    private static string DescribeSecondary(string what, ComponentIdentitySignal signal) => signal switch
    {
        ComponentIdentitySignal.Matches => $"{what} совпал(а) (отказом не управляет)",
        ComponentIdentitySignal.Differs => $"{what} РАСХОДИТСЯ (отказом НЕ управляет)",
        _ => $"{what} не читается",
    };
}
