using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The component-address rule — as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure function is checked against the measured fixture shape: two different files of one
/// name and an instance excluded from the calculation sitting BEFORE them. That excluded instance is in
/// <c>ksPartCollection</c> and absent from <c>IPart7.PartsEx</c>, so a counter over the records shifts every
/// later address by one — the defect this rule exists to remove.
/// History: docs/decisions/tests.md#component-address</remarks>
public class ComponentAddressTests
{
    private const string DirA = @"C:\fixture\a";
    private const string DirB = @"C:\fixture\b";

    /// <summary>A 4×4 rigid transform translated by <paramref name="x"/> millimetres.</summary>
    private static double[] At(double x) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, 0, 0, 1,
    ];

    private static ComponentInstance Instance(string? path, double? x) =>
        new(path, x is null ? null : At(x.Value));

    /// <summary>The measured collection: a#1, a#2 (hidden), a#3 (EXCLUDED), b#1, sub#1, sub#2.</summary>
    private static List<ComponentInstance> FixtureCollection() =>
    [
        Instance(Path.Combine(DirA, "part.m3d"), 0),
        Instance(Path.Combine(DirA, "part.m3d"), 100),
        Instance(Path.Combine(DirA, "part.m3d"), 150),
        Instance(Path.Combine(DirB, "part.m3d"), 200),
        Instance(@"C:\fixture\sub.m3d", 300),
        Instance(@"C:\fixture\sub.m3d", 400),
    ];

    [Fact]
    public void ExcludedInstanceBeforeTheOthers_DoesNotShiftTheAddress()
    {
        // INVARIANT: the address is the index in the flat collection. Record #3 (b/part.m3d) must land on
        // index 3, not on 2 — index 2 holds the EXCLUDED a/part.m3d, which is exactly the foreign component a
        // counter would address.
        var collection = FixtureCollection();

        var b = ComponentAddress.Locate(Instance(Path.Combine(DirB, "part.m3d"), 200), collection);
        var subFirst = ComponentAddress.Locate(Instance(@"C:\fixture\sub.m3d", 300), collection);
        var subSecond = ComponentAddress.Locate(Instance(@"C:\fixture\sub.m3d", 400), collection);

        Assert.Equal(3, b.Index);
        Assert.Equal(4, subFirst.Index);
        Assert.Equal(5, subSecond.Index);
    }

    [Fact]
    public void SameFileNameInAnotherFolder_IsADifferentComponent()
    {
        // INVARIANT: two files of one name are exactly the pair a name comparison confirms as "the same".
        // The first a/part.m3d record must resolve to index 0 and never to index 3.
        var verdict = ComponentAddress.Locate(
            Instance(Path.Combine(DirA, "part.m3d"), 0), FixtureCollection());

        Assert.Equal(0, verdict.Index);
    }

    [Fact]
    public void SeveralInstancesOfOneFile_AreToldApartByPlacement()
    {
        // Both a/part.m3d records share a path, so only the placement distinguishes them.
        var collection = FixtureCollection();

        Assert.Equal(0, ComponentAddress.Locate(Instance(Path.Combine(DirA, "part.m3d"), 0), collection).Index);
        Assert.Equal(1, ComponentAddress.Locate(Instance(Path.Combine(DirA, "part.m3d"), 100), collection).Index);
    }

    [Fact]
    public void TwoInstancesAtOnePlacement_GetNoAddress()
    {
        // LIMIT, named: the documented properties cannot tell two such instances apart, so the record gets NO
        // address — a guessed index would address a foreign component.
        List<ComponentInstance> collection =
        [
            Instance(Path.Combine(DirA, "part.m3d"), 0),
            Instance(Path.Combine(DirA, "part.m3d"), 0),
            Instance(Path.Combine(DirB, "part.m3d"), 200),
        ];

        var verdict = ComponentAddress.Locate(Instance(Path.Combine(DirA, "part.m3d"), 0), collection);

        Assert.Null(verdict.Index);
        Assert.Contains("однозначного сопоставления нет", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueSource_WithAnotherPlacement_IsNotConfirmed()
    {
        // The path is unique in the collection, yet the placement differs: the two sides describe different
        // instances, so the address is refused rather than taken from the path alone.
        var verdict = ComponentAddress.Locate(
            Instance(Path.Combine(DirB, "part.m3d"), 999), FixtureCollection());

        Assert.Null(verdict.Index);
        Assert.Contains("ДРУГОМУ размещению", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueSource_WithoutAPlacement_IsConfirmedByThePath()
    {
        // "Placement not read" is not a mismatch: with a single instance of that file the path decides.
        var verdict = ComponentAddress.Locate(
            new ComponentInstance(Path.Combine(DirB, "part.m3d"), null), FixtureCollection());

        Assert.Equal(3, verdict.Index);
        Assert.Contains("размещение не сверено", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSource_GetsNoAddress()
    {
        var verdict = ComponentAddress.Locate(Instance(@"C:\fixture\c\part.m3d", 0), FixtureCollection());

        Assert.Null(verdict.Index);
        Assert.Contains("нет ни одного экземпляра", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadSource_GetsNoAddress()
    {
        // "Not read" is not "matched": nothing to compare means no address.
        var verdict = ComponentAddress.Locate(Instance(null, 0), FixtureCollection());

        Assert.Null(verdict.Index);
        Assert.Contains("не прочитан", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SameSource_ComparesTheFullPathCaseInsensitively()
    {
        Assert.True(ComponentAddress.SameSource(@"C:\A\part.m3d", @"c:\a\PART.M3D"));
        Assert.False(ComponentAddress.SameSource(@"C:\a\part.m3d", @"C:\b\part.m3d"));
        Assert.False(ComponentAddress.SameSource(@"C:\a\part.m3d", "part.m3d"));
        Assert.False(ComponentAddress.SameSource(null, @"C:\a\part.m3d"));
        Assert.False(ComponentAddress.SameSource("   ", @"C:\a\part.m3d"));
    }

    [Fact]
    public void SameMatrix_UsesTheProjectTolerance()
    {
        Assert.True(ComponentAddress.SameMatrix(At(0), At(0.005)));
        Assert.False(ComponentAddress.SameMatrix(At(0), At(0.5)));
        Assert.False(ComponentAddress.SameMatrix(At(0), null));
        Assert.False(ComponentAddress.SameMatrix(null, null));
        Assert.False(ComponentAddress.SameMatrix(At(0), [1, 0, 0]));
    }
}
