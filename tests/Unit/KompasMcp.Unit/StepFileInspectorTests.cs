using System.Text;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Structural read of a STEP file: the schema string of the header, the length unit and the
/// number of solids and occurrences — all from the bytes.</summary>
/// <remarks>TEST: a well-formed file, an assembly, a missing unit, a missing header, and a name that
/// only appears inside a string argument. "The converter writes such a file on KOMPAS" is NOT covered
/// here: that needs a run on the target version.
/// History: docs/decisions/geometry.md#step-metadata</remarks>
public sealed class StepFileInspectorTests
{
    private const string Header =
        "ISO-10303-21;\r\nHEADER;\r\n"
        + "FILE_DESCRIPTION(('STEP AP242'),'1');\r\n"
        + "FILE_NAME('part','','',(''),(''),'','');\r\n"
        + "FILE_SCHEMA(('AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }'));\r\n"
        + "ENDSEC;\r\nDATA;\r\n";

    [Fact]
    public void Part_ReadsSchemaUnitsAndSolids()
    {
        var path = Write(Header
            + "#1=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\r\n"
            + "#2=MANIFOLD_SOLID_BREP('',#3);\r\n"
            + "#4=MANIFOLD_SOLID_BREP('',#5);\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: false);

        Assert.True(facts.HeaderParsed);
        Assert.Equal("AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }", facts.Schema);
        Assert.Equal("mm", facts.Units);
        Assert.Equal(2, facts.UniqueSolids);
        Assert.Equal(2, facts.InstanceCount);
        Assert.DoesNotContain(facts.UnverifiedAspects, aspect => aspect.StartsWith("schema_not_found"));
        Assert.DoesNotContain(facts.UnverifiedAspects, aspect => aspect.StartsWith("units_not_read"));
    }

    [Fact]
    public void Assembly_CountsOccurrences()
    {
        var path = Write(Header
            + "#1=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.));\r\n"
            + "#2=MANIFOLD_SOLID_BREP('',#3);\r\n"
            + "#4=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','','',#5,#6,$);\r\n"
            + "#7=NEXT_ASSEMBLY_USAGE_OCCURRENCE('2','','',#5,#6,$);\r\nENDSEC;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: true);

        Assert.Equal("m", facts.Units);
        Assert.Equal(2, facts.InstanceCount);
        Assert.Equal(1, facts.UniqueSolids);
    }

    [Fact]
    public void AssemblyWithoutOccurrences_LeavesInstanceCountNullWithReason()
    {
        var path = Write(Header + "#2=MANIFOLD_SOLID_BREP('',#3);\r\nENDSEC;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: true);

        Assert.Null(facts.InstanceCount);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("instance_count_not_read"));
    }

    [Fact]
    public void MissingUnit_LeavesUnitsNullWithReason()
    {
        var path = Write(Header + "#2=MANIFOLD_SOLID_BREP('',#3);\r\nENDSEC;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: false);

        Assert.Null(facts.Units);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("units_not_read"));
    }

    [Fact]
    public void FileWithoutHeader_IsNamed()
    {
        var path = Write("#2=MANIFOLD_SOLID_BREP('',#3);\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: false);

        Assert.False(facts.HeaderParsed);
        Assert.Null(facts.Schema);
        Assert.Contains(facts.UnverifiedAspects, aspect => aspect.StartsWith("schema_not_found"));
    }

    [Fact]
    public void EntityNameInsideAString_IsNotCounted()
    {
        var path = Write(Header
            + "#1=PRODUCT('MANIFOLD_SOLID_BREP','MANIFOLD_SOLID_BREP','',(#2));\r\n"
            + "#3=MANIFOLD_SOLID_BREP('',#4);\r\nENDSEC;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: false);

        Assert.Equal(1, facts.UniqueSolids);
    }

    [Fact]
    public void ConversionBasedUnit_IsNamedAsWritten()
    {
        var path = Write(Header
            + "#1=(CONVERSION_BASED_UNIT('INCH',#2)LENGTH_UNIT()NAMED_UNIT(#3));\r\n"
            + "#4=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\r\nENDSEC;\r\n");

        var facts = StepFileInspector.Inspect(path, assembly: false);

        Assert.Equal("inch", facts.Units);
    }

    private static string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"step-{Guid.NewGuid():N}.step");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
