using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Locating the mate a create call added — as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure function behind kompas_create_mate. INVARIANT: a validity change of an older
/// mate does not hide the new one, and is reported as a count.
/// History: docs/decisions/mates.md#new-mate-signature</remarks>
public class MateDifferenceTests
{
    private static MateSnapshotRow Row(string signature, bool? valid = true) => new(signature, valid);

    [Fact]
    public void NewMateThatInvalidatesAnOlderOne_IsStillFound()
    {
        var before = new[] { Row("coincidence|0") };
        var after = new[] { Row("coincidence|0", valid: false), Row("distance|50") };

        var result = MateDifference.Find(before, after);

        Assert.Equal(1, result.AddedIndex);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.NewlyInvalid);
    }

    [Fact]
    public void ThirdIdenticalMate_GivesExactlyOneAddedRow()
    {
        var before = new[] { Row("parallel|0"), Row("parallel|0") };
        var after = new[] { Row("parallel|0"), Row("parallel|0"), Row("parallel|0") };

        var result = MateDifference.Find(before, after);

        Assert.Equal(1, result.AddedCount);
        Assert.NotNull(result.AddedIndex);
        Assert.Equal(0, result.NewlyInvalid);
    }

    [Fact]
    public void DuplicatesWithDifferentValidity_DoNotInventAValidityChange()
    {
        var before = new[] { Row("parallel|0", valid: false), Row("parallel|0", valid: true) };
        var after = new[] { Row("parallel|0", valid: true), Row("parallel|0", valid: false), Row("angle|30") };

        var result = MateDifference.Find(before, after);

        Assert.Equal(2, result.AddedIndex);
        Assert.Equal(0, result.NewlyInvalid);
    }

    [Fact]
    public void NoNewRow_IsNotAFind()
    {
        var before = new[] { Row("coincidence|0") };
        var after = new[] { Row("coincidence|0") };

        var result = MateDifference.Find(before, after);

        Assert.Null(result.AddedIndex);
        Assert.Equal(0, result.AddedCount);
    }

    [Fact]
    public void TwoUnmatchedRows_AreAmbiguous_AndNotAFind()
    {
        // INVARIANT: when the stable part of an older row really changed, the new mate is not guessed.
        var before = new[] { Row("coincidence|0") };
        var after = new[] { Row("coincidence|1"), Row("distance|50") };

        var result = MateDifference.Find(before, after);

        Assert.Null(result.AddedIndex);
        Assert.Equal(2, result.AddedCount);
    }
}
