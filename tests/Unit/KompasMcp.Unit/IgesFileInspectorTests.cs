using System.Text;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Structural read of an IGES file (block IGES): the section counts, the entity count and the
/// units, all from the bytes.</summary>
/// <remarks>TEST: a well-formed file, a terminate record that disagrees with the file, an odd directory
/// section, an empty file, records that are not 80 columns, and units that cannot be resolved — each one
/// a NAMED aspect instead of a guessed value. "The converter writes such a file on KOMPAS" is NOT covered
/// here: that needs a run on the target version.
/// History: docs/decisions/geometry.md#iges</remarks>
public sealed class IgesFileInspectorTests
{
    private const string Global =
        ",1H;,2HAB,1Hf,2HKP,2Hv1,32,38,6,308,15,2HRC,1.0,2,2HMM,11,0,";

    [Fact]
    public void WellFormedFile_CountsSectionsAndEntities()
    {
        var path = Write(Iges(start: 1, global: 1, directory: 4, parameter: 2));

        var facts = IgesFileInspector.Inspect(path);

        Assert.True(facts.StructureParsed);
        Assert.Empty(facts.UnverifiedAspects);
        Assert.Equal(2, facts.EntityCount);
        Assert.Equal(1, facts.StartRecords);
        Assert.Equal(1, facts.GlobalRecords);
        Assert.Equal(4, facts.DirectoryRecords);
        Assert.Equal(2, facts.ParameterRecords);
        Assert.Equal(1, facts.TerminateRecords);
        Assert.Equal(2, facts.UnitsFlag);
        Assert.Equal("MM", facts.UnitsName);
        Assert.Equal(1d, facts.UnitsMmPerUnit);
        Assert.Empty(facts.UnverifiedAspects);
    }

    [Fact]
    public void TerminateRecordDisagrees_IsNotParsed()
    {
        // The file holds 4 directory records while the terminate record declares 6.
        var path = Write(Iges(start: 1, global: 1, directory: 4, parameter: 2, declared: (1, 1, 6, 2)));

        var facts = IgesFileInspector.Inspect(path);

        Assert.False(facts.StructureParsed);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("section_directory_count_differs"));
    }

    [Fact]
    public void OddDirectorySection_IsNotParsed()
    {
        var path = Write(Iges(start: 1, global: 1, directory: 3, parameter: 2));

        var facts = IgesFileInspector.Inspect(path);

        Assert.False(facts.StructureParsed);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("directory_records_odd"));
    }

    [Fact]
    public void EmptyFile_IsNamed()
    {
        var path = Write(string.Empty);

        var facts = IgesFileInspector.Inspect(path);

        Assert.False(facts.StructureParsed);
        Assert.Equal(0, facts.ByteLength);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("empty_file"));
    }

    [Fact]
    public void RecordsNotEightyColumns_IsNamed()
    {
        var path = Write(Iges(start: 1, global: 1, directory: 2, parameter: 1) + "IGES tail" + Environment.NewLine);

        var facts = IgesFileInspector.Inspect(path);

        Assert.False(facts.StructureParsed);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("record_not_80"));
    }

    [Fact]
    public void UnitsFieldShort_LeavesUnitsNullWithReason()
    {
        var path = Write(Iges(start: 1, global: 1, directory: 2, parameter: 1, globalText: ",1H;,6Hsender,"));

        var facts = IgesFileInspector.Inspect(path);

        Assert.True(facts.StructureParsed);
        Assert.Null(facts.UnitsFlag);
        Assert.Null(facts.UnitsMmPerUnit);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("global_fields_short"));
    }

    [Fact]
    public void UnitsFlagBy_Name_IsNotGuessed()
    {
        var path = Write(Iges(start: 1, global: 1, directory: 2, parameter: 1,
            globalText: ",1H;,2HAB,1Hf,2HKP,2Hv1,32,38,6,308,15,2HRC,1.0,3,2HMM,11,0,"));

        var facts = IgesFileInspector.Inspect(path);

        Assert.Equal(3, facts.UnitsFlag);
        Assert.Null(facts.UnitsMmPerUnit);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("units_flag_is_name"));
    }

    [Fact]
    public void GlobalSection_IsSplitByItsOwnDelimiter_KeepingHollerithStrings()
    {
        var fields = IgesFileInspector.SplitParameters(Global, ',');

        Assert.Equal(";", fields[1]);
        Assert.Equal("2", fields[13]);
        Assert.Equal("MM", fields[14]);
    }

    private static string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"iges-{Guid.NewGuid():N}.igs");
        File.WriteAllText(path, content, Encoding.ASCII);
        return path;
    }

    private static string Iges(int start, int global, int directory, int parameter,
        (int S, int G, int D, int P)? declared = null, string globalText = Global)
    {
        var records = new List<string>();
        for (var index = 0; index < start; index++)
        {
            records.Add(Record(string.Empty, 'S', index + 1));
        }

        for (var index = 0; index < global; index++)
        {
            records.Add(Record(index == 0 ? globalText : string.Empty, 'G', index + 1));
        }

        for (var index = 0; index < directory; index++)
        {
            records.Add(Record(string.Empty, 'D', index + 1));
        }

        for (var index = 0; index < parameter; index++)
        {
            records.Add(Record(string.Empty, 'P', index + 1));
        }

        var counts = declared ?? (start, global, directory, parameter);
        records.Add(Record($"{counts.S,8}{counts.G,8}{counts.D,8}{counts.P,8}", 'T', 1));

        // MEASURED: the kernel writes the file with CRLF line ends; a record is one LINE of 80 columns.
        return string.Join(Environment.NewLine, records) + Environment.NewLine;
    }

    /// <summary>One IGES record: 72 columns of content, the section letter in column 73, a 7-digit
    /// sequence number in columns 74-80.</summary>
    private static string Record(string content, char section, int sequence) =>
        content.PadRight(72)[..72] + section + sequence.ToString("D7");
}
