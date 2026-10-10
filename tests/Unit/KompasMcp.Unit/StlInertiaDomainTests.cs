using System.Text;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>STL export and mass-centre inertia (block G5+G7): the PURE rules that can be checked without
/// KOMPAS.</summary>
/// <remarks>TEST: the triangle count read from a binary and from a text STL file, the box read from the
/// same bytes, the mapping of <c>binary</c> onto the FILE KIND (never onto the format code), the accuracy
/// guard, and the assembly of the <c>inertia</c> block — null WITH a reason instead of a zero, and the
/// mandatory unit/system fields. "The route works on KOMPAS" is NOT included and not substituted: it needs
/// a run on the target version. History: docs/decisions/geometry.md#stl</remarks>
public sealed class StlInertiaDomainTests
{
    // ---------------------------------------------------------------- STL triangle count

    /// <summary>A binary STL written the documented way: 80-byte header, a 4-byte count, 50 bytes per
    /// triangle. The count read back must come from the FILE, not from the writer.</summary>
    [Fact]
    public void BinaryStl_TriangleCountIsReadFromTheHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stl-binary-{Guid.NewGuid():N}.stl");
        try
        {
            WriteBinaryStl(path, 3);
            var facts = StlFileInspector.Inspect(path, binary: true);

            Assert.Equal(3, facts.TriangleCount);
            Assert.Equal(StlExportPlan.BinaryCountSource, facts.CountSource);
            Assert.Equal(80 + 4 + 3 * 50, facts.ByteLength);
            Assert.Empty(facts.UnverifiedAspects);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A text STL: the count is the number of <c>facet normal</c> lines, and the box comes from
    /// the <c>vertex</c> lines.</summary>
    [Fact]
    public void TextStl_TriangleCountIsCountedByFacetLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stl-text-{Guid.NewGuid():N}.stl");
        try
        {
            File.WriteAllText(path, """
                solid test
                facet normal 0 0 1
                  outer loop
                    vertex 0 0 0
                    vertex 10 0 0
                    vertex 0 20 0
                  endloop
                endfacet
                facet normal 0 0 1
                  outer loop
                    vertex 0 0 5
                    vertex 10 0 5
                    vertex 0 20 5
                  endloop
                endfacet
                endsolid test
                """, new UTF8Encoding(false));

            var facts = StlFileInspector.Inspect(path, binary: false);

            Assert.Equal(2, facts.TriangleCount);
            Assert.Equal(StlExportPlan.TextCountSource, facts.CountSource);
            Assert.True(facts.BoxRead);
            Assert.Equal(new[] { 0d, 0d, 0d }, facts.MinMm);
            Assert.Equal(new[] { 10d, 20d, 5d }, facts.MaxMm);
            // A text file's exact size depends on formatting, and that is NAMED rather than hidden.
            Assert.Contains(facts.UnverifiedAspects, a => a.StartsWith("text_file_size_is_formatting_dependent"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A binary STL whose header count disagrees with its size is a NAMED discrepancy, and the
    /// size wins — a short count read as a full one would be an invented measurement.</summary>
    [Fact]
    public void BinaryStl_HeaderCountDisagreementIsNamed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stl-mismatch-{Guid.NewGuid():N}.stl");
        try
        {
            WriteBinaryStl(path, triangles: 2, declaredCount: 5);
            var facts = StlFileInspector.Inspect(path, binary: true);

            Assert.Equal(2, facts.TriangleCount);
            Assert.Contains(facts.UnverifiedAspects, a => a.StartsWith("binary_header_count_differs"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An empty file is not a STL with zero triangles: the missing header is named.</summary>
    [Fact]
    public void BinaryStl_EmptyFileIsNamedNotZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stl-empty-{Guid.NewGuid():N}.stl");
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());
            var facts = StlFileInspector.Inspect(path, binary: true);

            Assert.Equal(0, facts.TriangleCount);
            Assert.False(facts.BoxRead);
            Assert.Contains(facts.UnverifiedAspects, a => a.StartsWith("binary_header_absent"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- binary -> file kind

    /// <summary><c>binary</c> maps onto the FILE KIND, not onto the format code: STL stays format 6 in both
    /// cases, and the two are separate members of the parameter object.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Plan_MapsBinaryOntoTheFileKindNotTheFormat(bool binary, bool expectedFormatBinary)
    {
        var plan = StlExportPlan.For(binary, 1.0, 30.0);

        Assert.Equal(StlExportPlan.FormatStl, plan.FormatCode);
        Assert.Equal(6, plan.FormatCode);
        Assert.Equal(expectedFormatBinary, plan.FormatBinary);
        Assert.Equal(binary ? StlExportPlan.BinaryCountSource : StlExportPlan.TextCountSource, plan.CountSource);
        Assert.Equal("stl", StlExportPlan.FormatName);
    }

    /// <summary>The accuracy is an argument of the call: a non-positive or non-finite value is refused
    /// BEFORE COM rather than silently clamped by the kernel.</summary>
    [Theory]
    [InlineData(0d, 30d)]
    [InlineData(-1d, 30d)]
    [InlineData(double.NaN, 30d)]
    [InlineData(1d, 0d)]
    [InlineData(1d, double.PositiveInfinity)]
    public void Plan_RefusesNonPositiveAccuracy(double length, double angle)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StlExportPlan.For(true, length, angle));
    }

    // ---------------------------------------------------------------- inertia block

    private static InertiaDto BuildAllOk() => InertiaBlock.Build(
        "mm|kg", InertiaBlock.CentralSystem,
        InertiaReading.Ok(1), InertiaReading.Ok(2), InertiaReading.Ok(3),
        InertiaReading.Ok(0), InertiaReading.Ok(0), InertiaReading.Ok(0),
        InertiaReading.Ok(4), InertiaReading.Ok(5), InertiaReading.Ok(6),
        InertiaReading.Ok(1), InertiaReading.Ok(2), InertiaReading.Ok(3),
        InertiaAxisReading.Ok(new[] { 1d, 0, 0 }),
        InertiaAxisReading.Ok(new[] { 0d, 1, 0 }),
        InertiaAxisReading.Ok(new[] { 0d, 0, 1 }));

    [Fact]
    public void InertiaBlock_CarriesUnitsAndBothSystems()
    {
        var block = BuildAllOk();

        Assert.Equal("mm|kg", block.Units);
        Assert.Equal(InertiaBlock.CentralSystem, block.System);
        Assert.Equal(InertiaBlock.PrincipalSystem, block.Principal!.System);
        Assert.Empty(block.Notes);
        Assert.Equal(3d, block.Axial!.Jz);
        Assert.Equal(3d, block.Principal!.Jz0);
        Assert.Equal(new[] { 1d, 0, 0 }, block.PrincipalAxes!.X);
    }

    /// <summary>An unread value is null WITH its reason — never a zero, which is indistinguishable from a
    /// measured zero moment.</summary>
    [Fact]
    public void InertiaBlock_UnreadValueIsNullWithReasonNotZero()
    {
        var block = InertiaBlock.Build(
            "mm|kg", InertiaBlock.CentralSystem,
            InertiaReading.Failed("jx_not_read — свойство jx не читается: COMException"),
            InertiaReading.Ok(2), InertiaReading.Ok(3),
            InertiaReading.Ok(0), InertiaReading.Ok(0), InertiaReading.Ok(0),
            InertiaReading.Ok(4), InertiaReading.Ok(5), InertiaReading.Ok(6),
            InertiaReading.Ok(1), InertiaReading.Ok(2), InertiaReading.Ok(3),
            InertiaAxisReading.Failed("principal_axis_x_not_read — GetAxisX вернул false"),
            InertiaAxisReading.Ok(new[] { 0d, 1, 0 }),
            InertiaAxisReading.Ok(new[] { 0d, 0, 1 }));

        Assert.Null(block.Axial!.Jx);
        Assert.Equal(3d, block.Axial.Jz);
        Assert.Null(block.PrincipalAxes!.X);
        Assert.Contains(block.Notes, n => n.StartsWith("jx_not_read"));
        Assert.Contains(block.Notes, n => n.StartsWith("principal_axis_x_not_read"));
        Assert.Contains(block.PrincipalAxes.Notes, n => n.StartsWith("principal_axis_x_not_read"));
    }

    /// <summary>Units and system are mandatory: a block without them is not a measurement and does not
    /// build at all.</summary>
    [Theory]
    [InlineData("", InertiaBlock.CentralSystem)]
    [InlineData("   ", InertiaBlock.CentralSystem)]
    [InlineData("mm|kg", "")]
    public void InertiaBlock_RequiresUnitsAndSystem(string units, string system)
    {
        Assert.Throws<ArgumentException>(() => InertiaBlock.Build(
            units, system,
            InertiaReading.Ok(1), InertiaReading.Ok(2), InertiaReading.Ok(3),
            InertiaReading.Ok(0), InertiaReading.Ok(0), InertiaReading.Ok(0),
            InertiaReading.Ok(4), InertiaReading.Ok(5), InertiaReading.Ok(6),
            InertiaReading.Ok(1), InertiaReading.Ok(2), InertiaReading.Ok(3),
            InertiaAxisReading.Ok(new[] { 1d, 0, 0 }),
            InertiaAxisReading.Ok(new[] { 0d, 1, 0 }),
            InertiaAxisReading.Ok(new[] { 0d, 0, 1 })));
    }

    /// <summary>A non-finite reading is a NAMED failure, not a value.</summary>
    [Fact]
    public void InertiaReading_NonFiniteValueIsAFailure()
    {
        var reading = InertiaReading.From(double.NaN, "jz");

        Assert.False(reading.Read);
        Assert.Null(reading.Value);
        Assert.StartsWith("jz_not_finite", reading.Failure);
    }

    // ---------------------------------------------------------------- helpers

    private static void WriteBinaryStl(string path, int triangles, long? declaredCount = null)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(new byte[80]);
        writer.Write((uint)(declaredCount ?? triangles));

        for (var triangle = 0; triangle < triangles; triangle++)
        {
            for (var i = 0; i < 12; i++)
            {
                writer.Write(0f);
            }

            writer.Write((ushort)0);
        }
    }
}
