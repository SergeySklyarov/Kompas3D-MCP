using KompasMcp.Domain.Assembly;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Whether the exploded-view steps may be rewritten over an empty list.</summary>
/// <remarks>TEST: every combination of the clear's BOOL (TRUE/FALSE/unread) and the re-read count (0,
/// non-zero, unread) is exercised — a refusal of the clear cannot be produced through COM, so this test is
/// what holds the refusal.
/// History: docs/decisions/dismantle.md#rewrite</remarks>
public class DismantleClearRuleTests
{
    public static TheoryData<bool?, int?, bool> Combinations { get; } = new()
    {
        { true, 0, true },
        { true, 1, false },
        { true, 2, false },
        { true, null, false },
        { false, 0, false },
        { false, 1, false },
        { false, null, false },
        { null, 0, false },
        { null, 1, false },
        { null, null, false },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void IsConfirmed_RequiresTrueAndZeroCount(bool? clearReturned, int? stepsAfterClear, bool expected)
    {
        Assert.Equal(expected, DismantleClearRule.IsConfirmed(clearReturned, stepsAfterClear));
    }
}
