using System.Globalization;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace SpecProbe;

/// <summary>Which VALUES the edit route writes into a column of a `.spw` row, and what the other columns
/// hold afterwards — the text matrix the acceptance asked for.</summary>
/// <remarks>WHY: the contract's own measurement wrote an ASCII zone («Z1») and promoted the zone to
/// writable for a `.spw`; the acceptance found that a Cyrillic zone lands as «@100~)» and that the note
/// column was appended to. The matrix varies ONE column at a time and reads EVERY column back, so the
/// corruption is told apart from the plain write and from a neighbour's change.
/// DOC: <c>ksspecification_kssetspcobjectcolumntext.html</c> (columnType, ispoln, block, BSTR str),
/// <c>ispecificationcolumnitem_value.html</c> (Value is settable, VT_BSTR for a string column).
/// History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
internal sealed partial class SpecProbe
{
    private const string TextRowPrefix = "SPW.711.";
    private const string TextBaselineNote = "исходное";

    private string SpwTextPath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "texts.spw");

    private string SpwTextLimitsPath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "texts-limits.spw");

    private string SpwTextWidthPath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "texts-width.spw");

    private string SpwTextWidePath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "texts-wide.spw");

    private string SpwTextCreatePath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "texts-create.spw");

    /// <summary>The values the acceptance named: ASCII, Cyrillic words, a zone number, digits, one letter,
    /// an empty string and a long one. Each goes into ONE column of its own row.</summary>
    private static readonly (string Column, string Value)[] TextCases =
    [
        ("zone", "Z1"),
        ("zone", "Зона А"),
        ("zone", "А3"),
        ("zone", "A3"),
        ("zone", "10"),
        ("zone", "Б"),
        ("zone", "З"),
        ("zone", "А"),
        ("zone", ""),
        ("zone", "Зона с длинным текстом 1234567890"),
        ("zone", "ZZ"),
        ("format", "Ф5"),
        ("format", "А3"),
        ("note", "Пр.1"),
        ("note", "Зона А"),
        ("user", "U1"),
        ("user", "Зона А"),
    ];

    /// <summary>What the first matrix cannot tell apart: a value too LONG for the column and a value with a
    /// SPACE or a lowercase letter. Length varies on pure ASCII and pure digits, so the alphabet stays out
    /// of the answer. History: docs/decisions/bom.md#spec-spw-zone-text</summary>
    private static readonly (string Column, string Value)[] TextLimitCases =
    [
        ("zone", "АБ"),
        ("zone", "АБВ"),
        ("zone", "АБВГ"),
        ("zone", "А Б"),
        ("zone", "A B"),
        ("zone", "Z 1"),
        ("zone", "1 2"),
        ("zone", "Зона"),
        ("zone", "зона"),
        ("zone", "123"),
        ("zone", "1234"),
        ("zone", "Z1Z"),
        ("zone", "А3Б"),
        ("zone", "А3-А5"),
        ("zone", "Z1 Z2"),
        ("zone", "А3А"),
    ];

    /// <summary>Whether the other text columns share the zone's cell limit: the same lengths on «Формат» and
    /// the user column, and a long text in «Примечание», which is where the zone text is moved.</summary>
    private static readonly (string Column, string Value)[] TextWidthCases =
    [
        ("format", "АБ"),
        ("format", "АБВ"),
        ("format", "АБВГ"),
        ("user", "АБВ"),
        ("user", "АБВГ"),
        ("note", "Примечание из сорока символов 1234567890"),
    ];

    /// <summary>How far the wide columns go: the user column and «Примечание», so the guard is not applied
    /// where the cell holds the text, and «Формат» is confirmed at its measured 3 characters.</summary>
    private static readonly (string Column, string Value)[] TextWideCases =
    [
        ("format", "АБВГД"),
        ("user", "АБВГДЕ"),
        ("user", "АБВГДЕЖЗ"),
        ("note", "Примечание длиной шестьдесят символов 123456789012345678901234567890"),
    ];

    /// <summary>Whether the CREATION route is bounded the same way: the same values written before the first
    /// <c>Update()</c> through the documented settable element.</summary>
    private static readonly (string Column, string Value)[] TextCreateCases =
    [
        ("zone", "Зона А"),
        ("zone", "А3"),
        ("format", "АБВГ"),
        ("note", "Зона А"),
    ];

    /// <summary>Rows the API7 route writes: the documented settable <c>ColumnItems.Value</c> + <c>Update()</c>,
    /// measured on the zone and on the note (a column the API5 road writes without damage).</summary>
    private static readonly (string Column, string Value)[] TextApi7Cases =
    [
        ("zone", "Зона А"),
        ("note", "Зона А"),
    ];

    private static ksSpecificationColumnTypeEnum TypeOfKey(string key) => key switch
    {
        "zone" => ksSpecificationColumnTypeEnum.ksSColumnZone,
        "format" => ksSpecificationColumnTypeEnum.ksSColumnFormat,
        "note" => ksSpecificationColumnTypeEnum.ksSColumnNote,
        "user" => ksSpecificationColumnTypeEnum.ksSColumnUser,
        _ => ksSpecificationColumnTypeEnum.ksSColumnUnknown,
    };

    /// <summary>Writes the matrix through the API5 edit route, one value per call, and records every column
    /// of the row before and after that call.</summary>
    /// <remarks>INVARIANT: the note starts at a known text, so a column appending to it is visible; the row
    /// is re-read from the description after each write, because the collection read before it is stale.
    /// DOC: <c>ksspecification_ksspcobjectedit.html</c>, <c>ksspecification_ksspcobjectend.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    private void WriteTextMatrix(
        IReadOnlyList<(string Column, string Value)> cases, string path, string label, bool api7Cases,
        bool writeAtCreation = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(SpwFixturePath, path, overwrite: true);
        var spw = OpenSpw(path, label);
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, label);
        if (description is not { } active || active.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            Record($"{label}_baseobjects", "описание спецификации и BaseObjects доступны", "нет", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var section = SectionFor(active);
        var specification = ActivateSpecification(spw, label);
        if (specification is null)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        RecordColumnWidths(specification, label);

        var created = new List<(int Index, string Marking, string Column, string Value)>();
        var index = 0;
        foreach (var (column, value) in cases)
        {
            index++;
            var marking = TextRowPrefix + index.ToString("000", CultureInfo.InvariantCulture);
            var row = writeAtCreation
                ? CreateTextRow(baseObjects, section, marking, $"Строка {column}", (TypeOfKey(column), value))
                : CreateTextRow(baseObjects, section, marking, $"Строка {column}");
            Record($"{label}_row_{index}", $"строка {marking} создана для колонки {column} = «{value}»",
                row is null ? "null" : "создана", "INFO");
            created.Add((index, marking, column, value));
        }

        foreach (var (rowIndex, marking, column, value) in created)
        {
            var row = FindTextRow(spw, marking);
            if (row is null)
            {
                Record($"{label}_{rowIndex}_{column}_row", $"строка {marking} найдена перед правкой", "нет", "FAIL");
                continue;
            }

            var before = SnapshotTextColumns(row);
            if (writeAtCreation)
            {
                // Создание пишет значение ДО первого Update(); правки здесь нет - читается, что легло.
                var writtenAtCreation = ReadFrom(row, TypeOfKey(column));
                Record($"{label}_{rowIndex}_{column}", $"создание колонки {column} = «{value}» (длина {value.Length})",
                    $"прочитано=«{writtenAtCreation}»; после=[{SnapshotTextColumns(row)}]", "INFO");
                continue;
            }

            var unique = (double)row.UniqueNumber;
            var answer = EditColumn(specification, unique, TypeOfKey(column), value);
            var reread = FindTextRow(spw, marking);
            var after = reread is null ? "<строки нет>" : SnapshotTextColumns(reread);
            var written = reread is null ? null : ReadFrom(reread, TypeOfKey(column));
            Record($"{label}_{rowIndex}_{column}", $"правка колонки {column} = «{value}» (длина {value.Length})",
                $"setter={answer.Setter}; прочитано=«{written}»; до=[{before}]; после=[{after}]",
                answer.Setter == 1 ? "PASS" : "FAIL");
        }

        if (api7Cases)
        {
            foreach (var (column, value) in TextApi7Cases)
            {
                var marking = "SPW.719." + (column == "zone" ? "001" : "002");
                CreateTextRow(baseObjects, section, marking, $"Строка API7 {column}");
                var row = FindTextRow(spw, marking);
                var update = row is null ? null : WriteViaApi7(row, TypeOfKey(column), value);
                var reread = FindTextRow(spw, marking);
                var written = reread is null ? null : ReadFrom(reread, TypeOfKey(column));
                var note = reread is null ? null : ReadFrom(reread, ksSpecificationColumnTypeEnum.ksSColumnNote);
                Record($"{label}_api7_{column}", $"API7 ColumnItems.Value = «{value}» + Update()",
                    $"update={update?.ToString() ?? "нет строки"}; прочитано=«{written}»; note=«{note}»", "INFO");
            }
        }

        spw.Save();
        Record($"{label}_saved", "IKompasDocument.Save() после матрицы значений", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens the file the write stage saved: every row's four columns through BOTH roads, so the
    /// matrix is about the file and not about the session.</summary>
    /// <remarks>History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    private void ReadTextMatrix(
        IReadOnlyList<(string Column, string Value)> cases, string path, string label)
    {
        var spw = OpenSpw(path, label + "-read");
        if (spw is null)
        {
            return;
        }

        var specification = ActivateSpecification(spw, label + "_read");
        var index = 0;
        foreach (var (column, value) in cases)
        {
            index++;
            var marking = TextRowPrefix + index.ToString("000", CultureInfo.InvariantCulture);
            var row = FindTextRow(spw, marking);
            if (row is null)
            {
                Record($"{label}_persisted_{index}_{column}", $"строка {marking} в файле", "нет", "FAIL");
                continue;
            }

            var unique = (double)row.UniqueNumber;
            var api7 = SnapshotTextColumns(row);
            var api5 = specification is null
                ? "<нет дороги>"
                : $"zone=«{Api5Text(specification, unique, ksSpecificationColumnTypeEnum.ksSColumnZone)}»; "
                  + $"note=«{Api5Text(specification, unique, ksSpecificationColumnTypeEnum.ksSColumnNote)}»";
            var writtenApi7 = ReadFrom(row, TypeOfKey(column));
            Record($"{label}_persisted_{index}_{column}", $"строка {marking}: {column} = «{value}» в файле",
                $"api7[{api7}]; api5[{api5}]; прочитано=«{writtenApi7}»",
                string.Equals(writtenApi7, value.Trim(), StringComparison.Ordinal) ? "PASS" : "INFO");
        }

        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    private void SpwColumnsTextWrite() => WriteTextMatrix(TextCases, SpwTextPath, "text", api7Cases: true);

    private void SpwColumnsTextRead() => ReadTextMatrix(TextCases, SpwTextPath, "text");

    private void SpwColumnsTextLimitsWrite() => WriteTextMatrix(TextLimitCases, SpwTextLimitsPath, "textlimits", false);

    private void SpwColumnsTextLimitsRead() => ReadTextMatrix(TextLimitCases, SpwTextLimitsPath, "textlimits");

    private void SpwColumnsTextWidthWrite() => WriteTextMatrix(TextWidthCases, SpwTextWidthPath, "textwidth", false);

    private void SpwColumnsTextWidthRead() => ReadTextMatrix(TextWidthCases, SpwTextWidthPath, "textwidth");

    private void SpwColumnsTextWideWrite() => WriteTextMatrix(TextWideCases, SpwTextWidePath, "textwide", false);

    private void SpwColumnsTextWideRead() => ReadTextMatrix(TextWideCases, SpwTextWidePath, "textwide");

    private void SpwColumnsTextCreateWrite() =>
        WriteTextMatrix(TextCreateCases, SpwTextCreatePath, "textcreate", false, writeAtCreation: true);

    private void SpwColumnsTextCreateRead() => ReadTextMatrix(TextCreateCases, SpwTextCreatePath, "textcreate");

    /// <summary>The documented width of every column and of the text in it, as the document answers.</summary>
    /// <remarks>DOC: <c>ksspecification_ksgetwidthcolumnspc.html</c> — «ширина колонки или текста в колонке»,
    /// inputs «номер колонки» and «TRUE - колонка, FALSE - текст в колонке». INVARIANT: the number the method
    /// takes is recorded here, not interpreted: the page does not say whether it is the table ordinal or the
    /// column type, so the probe reports what each number answers and nothing more.
    /// History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    private void RecordColumnWidths(ksSpecification specification, string label)
    {
        for (var number = 1; number <= 10; number++)
        {
            var column = SafeNumber(() => specification.ksGetWidthColumnSpc(number, true));
            var text = SafeNumber(() => specification.ksGetWidthColumnSpc(number, false));
            Record($"{label}_width_{number}", $"ksGetWidthColumnSpc({number}) - колонка и текст в ней",
                $"column={column ?? "<не прочитано>"}, text={text ?? "<не прочитано>"}", "INFO");
        }
    }

    private static string? SafeNumber(Func<double> read)
    {
        try
        {
            return read().ToString("0.###", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Creates one row with a known NOTE, so a write that appends to the note is visible.</summary>
    /// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c> (the row appears after <c>Update</c>),
    /// <c>ispecificationobject_update.html</c>. History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    private ISpecificationBaseObject? CreateTextRow(
        ISpecificationBaseObjects baseObjects, int section, string marking, string name,
        (ksSpecificationColumnTypeEnum Type, string Value)? extra = null)
    {
        var created = baseObjects.Add(section, 0);
        if (created is null)
        {
            return null;
        }

        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, marking);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, name);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, "1");
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnNote, TextBaselineNote);
        if (extra is { } column)
        {
            SetText(created, column.Type, column.Value);
        }

        return created.Update() ? created : null;
    }

    /// <summary>The row with this marking, from a FRESH read of the description.</summary>
    private static ISpecificationBaseObject? FindTextRow(IKompasDocument spw, string marking) =>
        ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == marking);

    /// <summary>The four columns of the matrix in one line, so a write is compared with its neighbours.</summary>
    private static string SnapshotTextColumns(ISpecificationBaseObject row) =>
        $"zone=«{ReadFrom(row, ksSpecificationColumnTypeEnum.ksSColumnZone)}»; "
        + $"format=«{ReadFrom(row, ksSpecificationColumnTypeEnum.ksSColumnFormat)}»; "
        + $"note=«{ReadFrom(row, ksSpecificationColumnTypeEnum.ksSColumnNote)}»; "
        + $"user=«{ReadFrom(row, ksSpecificationColumnTypeEnum.ksSColumnUser)}»";

    /// <summary>Writes the value through the documented API7 element setter and returns what Update said.</summary>
    /// <remarks>DOC: <c>ispecificationcolumnitem_value.html</c> («Позволяет получить и установить значение
    /// элемента»; VT_BSTR для строковой колонки), <c>ispecificationobject_update.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    private static bool? WriteViaApi7(ISpecificationBaseObject row, ksSpecificationColumnTypeEnum type, string text)
    {
        foreach (var columns in new[] { SafeColumns(row, additional: false), SafeColumns(row, additional: true) })
        {
            if (columns is null || columns.Column[type, 1, 0] is not { } column
                || column.ColumnItems is not ISpecificationColumnItems items)
            {
                continue;
            }

            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is { } element)
                {
                    element.Value = text;
                    return row.Update();
                }
            }
        }

        return null;
    }
}
