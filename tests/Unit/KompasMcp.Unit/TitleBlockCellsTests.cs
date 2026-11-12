using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Which title-block cells an operation changed, and which cells follow a model property.</summary>
/// <remarks>TEST: dictionaries and ids only, so every branch is exercised without КОМПАС.
/// History: docs/decisions/drawings.md#stamp-rebuild-changes</remarks>
public class TitleBlockCellsTests
{
    /// <summary>The defect the row exists for: a rebuild refilled the name and cleared the designation,
    /// and the answer said nothing. Both cells are reported with their old and new values.</summary>
    [Fact]
    public void Changed_ReportsBothTheRefilledNameAndTheClearedDesignation()
    {
        var before = new Dictionary<int, string?>
        {
            [1] = "ЧЕРТЁЖ П4",
            [2] = "P4-001",
            [6] = "^(#3~1:1)brackets=0;param=1;type=view;viewName=2;~",
        };
        var after = new Dictionary<int, string?>
        {
            [1] = "P4-STAMP-MODEL",
            [2] = string.Empty,
            [6] = "^(#3~1:1)brackets=0;param=1;type=view;viewName=2;~",
        };

        var changes = TitleBlockCells.Changed(before, after);

        Assert.Equal(new[] { 1, 2 }, changes.Select(c => c.CellId));
        Assert.Equal(("ЧЕРТЁЖ П4", "P4-STAMP-MODEL"), (changes[0].Before, changes[0].After));
        Assert.Equal(("P4-001", string.Empty), (changes[1].Before, changes[1].After));
    }

    /// <summary>An unchanged stamp is silent: naming cells that did not change would be noise a client
    /// cannot use to decide whether its value survived.</summary>
    [Fact]
    public void Changed_IsEmptyWhenTheStampDidNotMove()
    {
        var same = new Dictionary<int, string?> { [1] = "ЧЕРТЁЖ П4" };
        Assert.Empty(TitleBlockCells.Changed(same, new Dictionary<int, string?>(same)));
    }

    /// <summary>A cell that became non-empty only after the operation is a change from an absent reading;
    /// an absent cell and an empty one must not be confused with each other.</summary>
    [Fact]
    public void Changed_NamesACellThatAppearedOrDisappeared()
    {
        var appeared = TitleBlockCells.Changed(
            new Dictionary<int, string?>(), new Dictionary<int, string?> { [3] = "Сталь 45" });
        Assert.Equal((3, (string?)null, (string?)"Сталь 45"), (appeared[0].CellId, appeared[0].Before,
            appeared[0].After));

        var disappeared = TitleBlockCells.Changed(
            new Dictionary<int, string?> { [3] = "Сталь 45" }, new Dictionary<int, string?>());
        Assert.Equal((3, (string?)"Сталь 45", (string?)null), (disappeared[0].CellId, disappeared[0].Before,
            disappeared[0].After));
    }

    /// <summary>The measured layout links exactly two ids to a model property; every other id stays
    /// without a claim.</summary>
    [Fact]
    public void ModelPropertyOf_NamesOnlyTheMeasuredIds()
    {
        Assert.Equal("ksPart.name", TitleBlockCells.ModelPropertyOf(1));
        Assert.Equal("ksPart.marking", TitleBlockCells.ModelPropertyOf(2));
        Assert.Null(TitleBlockCells.ModelPropertyOf(0));
        Assert.Null(TitleBlockCells.ModelPropertyOf(6));
    }
}
