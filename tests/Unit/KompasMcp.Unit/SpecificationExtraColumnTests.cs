using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>What <c>extra_columns</c> means before KOMPAS is asked: the documented TYPE keys, the number and
/// the block, and every refusal of the pure rule.</summary>
/// <remarks>TEST: the rule is pure, so it is held here; the write itself, the read-back of both roads and the
/// column list of a real style are held by the acceptance rows <c>SPEC-05.*</c> of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-spw-row-write</remarks>
public class SpecificationExtraColumnTests
{
    [Fact]
    public void ATypedColumnIsAcceptedWithItsDocumentedNumberAndBlock()
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto("format", null, null, " А4 ")], out var refusal);

        Assert.Null(refusal);
        var column = Assert.Single(columns!);
        Assert.Equal(1, column.Type);
        Assert.Equal(SpecificationColumnNumbers.FirstOfType, column.Number);
        Assert.Equal(SpecificationColumnNumbers.MainBlock, column.Block);
        Assert.Equal("А4", column.Value);
    }

    [Theory]
    [InlineData("user", 10, 2, 0)]
    [InlineData("mass", 8, 1, 0)]
    [InlineData("code_okp", 16, 1, 3)]
    [InlineData("document_number", 13, 1, 0)]
    public void EveryDocumentedKeyMapsToItsEnumNumber(string key, int type, int number, int block)
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto(key, number, block, "x")], out var refusal);

        Assert.Null(refusal);
        var column = Assert.Single(columns!);
        Assert.Equal(type, column.Type);
        Assert.Equal(number, column.Number);
        Assert.Equal(block, column.Block);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("marking")]
    [InlineData("name")]
    [InlineData("quantity")]
    public void TheFourRowFieldsAreRefusedAsExtraColumns(string key)
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto(key, null, null, "x")], out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Equal("type", refusal.Field);
        Assert.Contains("полем строки", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedWithTheDocumentedList()
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto("colour", null, null, "x")], out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Contains("ksspecificationcolumntypeenum.html", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("code_okp", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "x", "type")]
    [InlineData("format", 0, "x", "number")]
    [InlineData("format", -2, "x", "number")]
    public void AMissingKeyOrANonPositiveNumberIsRefused(
        string? key, int? number, string value, string field)
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto(key!, number, null, value)], out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Equal(field, refusal.Field);
    }

    [Fact]
    public void ANegativeBlockIsRefused()
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto("mass", 1, -1, "1.25")], out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Equal("block", refusal.Field);
        Assert.Contains("начиная с 0", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankValueIsRefused(string value)
    {
        var columns = SpecificationExtraColumns.Parse(
            [new SpecificationExtraColumnDto("note", null, null, value)], out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Equal("value", refusal.Field);
    }

    [Fact]
    public void TheSameColumnTwiceIsRefusedInsteadOfPicked()
    {
        var columns = SpecificationExtraColumns.Parse(
            [
                new SpecificationExtraColumnDto("mass", 1, 0, "1.25"),
                new SpecificationExtraColumnDto("mass", 1, 0, "2"),
            ],
            out var refusal);

        Assert.Null(columns);
        Assert.NotNull(refusal);
        Assert.Contains("дважды", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoColumnsOfOneTypeAreToldApartByTheNumber()
    {
        var columns = SpecificationExtraColumns.Parse(
            [
                new SpecificationExtraColumnDto("user", 1, 0, "a"),
                new SpecificationExtraColumnDto("user", 2, 0, "b"),
            ],
            out var refusal);

        Assert.Null(refusal);
        Assert.Equal(2, columns!.Count);
        Assert.NotEqual(columns[0].Number, columns[1].Number);
    }

    [Fact]
    public void NoColumnsIsAnEmptyListAndNotARefusal()
    {
        var columns = SpecificationExtraColumns.Parse(null, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(columns);
        Assert.Empty(columns);
    }

    [Fact]
    public void AKeyIsSpelledByTypeAndNumber()
    {
        Assert.Equal("format", SpecificationColumnKeys.KeyOf(1, 1));
        Assert.Equal("user", SpecificationColumnKeys.KeyOf(10, 1));
        Assert.Equal("user2", SpecificationColumnKeys.KeyOf(10, 2));
    }
}
