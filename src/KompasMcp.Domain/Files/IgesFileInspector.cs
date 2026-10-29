using System.Globalization;
using System.Text;

namespace KompasMcp.Domain.Files;

/// <summary>Structural read of an IGES file: the section record counts, the entity count and the units,
/// all taken from the BYTES on disk rather than from the converter's return value.</summary>
/// <remarks>DOC: IGES is an open file standard, not KOMPAS help: records are 80 columns, the section
/// letter stands in column 73, the terminate record declares the number of records in each section and
/// the directory section holds two records per entity. INVARIANT: the parse only checks the structure and
/// claims nothing about KOMPAS; a file that does not agree with its own declaration is a named refusal.
/// History: docs/decisions/geometry.md#iges</remarks>
public static class IgesFileInspector
{
    private const int RecordLength = 80;

    /// <summary>Zero-based index of column 73, where the section letter stands.</summary>
    private const int SectionColumn = 72;

    /// <summary>Section letters of the standard, in file order.</summary>
    private const string SectionLetters = "SGDPT";

    /// <summary>Units flag (global field 14) to millimetres per unit; 3 means "see the units name".</summary>
    private static readonly Dictionary<int, (string Name, double Mm)> UnitsFlags = new()
    {
        [1] = ("inch", 25.4),
        [2] = ("millimetre", 1d),
        [4] = ("foot", 304.8),
        [5] = ("mile", 1609344d),
        [6] = ("metre", 1000d),
        [7] = ("kilometre", 1000000d),
        [8] = ("mil", 0.0254),
        [9] = ("micron", 0.001),
        [10] = ("centimetre", 10d),
        [11] = ("microinch", 0.0000254),
    };

    public sealed record IgesFacts(
        long ByteLength,
        bool StructureParsed,
        int EntityCount,
        int StartRecords,
        int GlobalRecords,
        int DirectoryRecords,
        int ParameterRecords,
        int TerminateRecords,
        int? UnitsFlag,
        string? UnitsName,
        double? UnitsMmPerUnit,
        IReadOnlyList<string> UnverifiedAspects);

    /// <summary>Read the structure of the file at <paramref name="path"/>.</summary>
    /// <remarks>INVARIANT: <c>StructureParsed</c> is true only when every section count declared by the
    /// terminate record equals the count actually found, the directory section is even and holds at least
    /// one entity. Each disagreement is named, never smoothed over.
    /// History: docs/decisions/geometry.md#iges</remarks>
    public static IgesFacts Inspect(string path)
    {
        var length = new FileInfo(path).Length;
        var unverified = new List<string>();
        if (length == 0)
        {
            unverified.Add("empty_file — файл пуст");
            return new IgesFacts(0, false, 0, 0, 0, 0, 0, 0, null, null, null, unverified);
        }

        // MEASURED: the kernel writes the file with CRLF line ends, so a record is one LINE of 80
        // columns, not 80 bytes of the stream; slicing the stream by 80 shifted every section letter.
        var counts = new Dictionary<char, int>();
        var global = new StringBuilder();
        var terminate = string.Empty;
        var malformed = false;
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path, Encoding.ASCII))
        {
            lineNumber++;
            var line = raw;
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length != RecordLength)
            {
                unverified.Add(
                    $"record_not_80 — запись {lineNumber} длиной {line.Length} колонок, а запись IGES занимает ровно 80");
                malformed = true;
                continue;
            }

            var letter = line[SectionColumn];
            if (SectionLetters.IndexOf(letter) < 0)
            {
                unverified.Add($"unknown_section_letter — в колонке 73 записи {lineNumber} стоит «{letter}»");
                malformed = true;
                continue;
            }

            counts[letter] = counts.TryGetValue(letter, out var seen) ? seen + 1 : 1;
            if (letter == 'G')
            {
                global.Append(line[..SectionColumn]);
            }
            else if (letter == 'T')
            {
                terminate = line;
            }
        }

        var start = Count(counts, 'S');
        var globalRecords = Count(counts, 'G');
        var directory = Count(counts, 'D');
        var parameter = Count(counts, 'P');
        var terminateRecords = Count(counts, 'T');

        var declared = new[] { -1, -1, -1, -1 };
        if (terminateRecords == 0)
        {
            unverified.Add("terminate_record_absent — запись T не найдена: объявленных счётчиков нет");
        }
        else
        {
            for (var field = 0; field < 4; field++)
            {
                // MEASURED: the kernel prefixes each count with its section letter ("S      2"), so the
                // declared value is the digits of the 8-column field, not the whole field.
                var slice = terminate.Substring(field * 8, 8).Trim();
                var digits = new string(slice.Where(char.IsDigit).ToArray());
                declared[field] = digits.Length > 0
                    && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : -1;
                if (declared[field] < 0)
                {
                    unverified.Add($"terminate_field_unreadable — поле {field + 1} записи T не число: «{slice}»");
                }
            }

            NameIfDiffers(unverified, "start", declared[0], start);
            NameIfDiffers(unverified, "global", declared[1], globalRecords);
            NameIfDiffers(unverified, "directory", declared[2], directory);
            NameIfDiffers(unverified, "parameter", declared[3], parameter);
        }

        if (directory % 2 != 0)
        {
            unverified.Add(
                $"directory_records_odd — записей D {directory}, а сущность занимает две: число обязано быть чётным");
        }

        var entities = directory / 2;
        if (entities == 0)
        {
            unverified.Add("no_entities — сущностей нет: пустая модель не является состоявшимся экспортом");
        }

        ReadUnits(global.ToString(), unverified, out var unitsFlag, out var unitsName, out var unitsMm);

        var countsAgree = terminateRecords > 0 && declared.All(value => value >= 0)
            && declared[0] == start && declared[1] == globalRecords
            && declared[2] == directory && declared[3] == parameter;
        var parsed = countsAgree && directory % 2 == 0 && entities > 0 && !malformed;

        return new IgesFacts(length, parsed, entities, start, globalRecords, directory, parameter,
            terminateRecords, unitsFlag, unitsName, unitsMm, unverified);
    }

    private static int Count(Dictionary<char, int> counts, char letter) =>
        counts.TryGetValue(letter, out var value) ? value : 0;

    private static void NameIfDiffers(List<string> unverified, string section, int declared, int found)
    {
        if (declared >= 0 && declared != found)
        {
            unverified.Add(
                $"section_{section}_count_differs — запись T объявляет {declared}, в файле {found}");
        }
    }

    /// <summary>Units from the global section: field 14 is the flag, field 15 the name. A flag the
    /// standard does not define, or a missing field, leaves the units NULL with the reason named — the
    /// scale is never guessed.</summary>
    private static void ReadUnits(string global, List<string> unverified, out int? flag,
        out string? name, out double? mmPerUnit)
    {
        flag = null;
        name = null;
        mmPerUnit = null;
        if (global.Length == 0)
        {
            unverified.Add("global_section_absent — глобальной секции нет: единицы не прочитаны");
            return;
        }

        var fields = SplitParameters(global, global[0]);
        if (fields.Count < 15)
        {
            unverified.Add($"global_fields_short — полей {fields.Count}, единицы стоят в 14-м и 15-м");
            return;
        }

        if (int.TryParse(fields[13].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            flag = value;
        }
        else
        {
            unverified.Add($"units_flag_unreadable — поле 14 не число: «{fields[13].Trim()}»");
        }

        name = fields[14].Trim();
        if (name.Length == 0)
        {
            name = null;
        }

        if (flag is { } known && UnitsFlags.TryGetValue(known, out var unit))
        {
            mmPerUnit = unit.Mm;
        }
        else if (flag == 3)
        {
            unverified.Add(
                "units_flag_is_name — флаг 3 означает «см. имя единицы»: масштаб по имени не выводится");
        }
        else if (flag is not null)
        {
            unverified.Add($"units_flag_unknown — флаг {flag} стандартом не определён");
        }
    }

    /// <summary>Split the global section by its own declared delimiter, keeping Hollerith strings
    /// (<c>5Hxxxxx</c>) and quoted strings whole — a delimiter inside them is data, not a separator.</summary>
    /// <remarks>The first token is field 1 itself, so global field N sits at index N-1.
    /// History: docs/decisions/geometry.md#iges</remarks>
    public static IReadOnlyList<string> SplitParameters(string text, char delimiter)
    {
        var fields = new List<string> { delimiter.ToString() };
        var current = new StringBuilder();
        var index = 1;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
                index++;
                continue;
            }

            if (char.IsDigit(character))
            {
                var end = index;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    end++;
                }

                if (end < text.Length && (text[end] == 'H' || text[end] == 'h')
                    && int.TryParse(text[index..end], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var held) && held >= 0)
                {
                    var start = end + 1;
                    var take = Math.Min(held, Math.Max(0, text.Length - start));
                    current.Append(text, start, take);
                    index = start + take;
                    continue;
                }
            }

            // LIMIT: quoted strings (a 1988 revision feature) are not handled; the standard form of the
            // global section uses Hollerith strings, and an unrecognised quote only shifts fields.
            current.Append(character);
            index++;
        }

        fields.Add(current.ToString());
        return fields;
    }
}
