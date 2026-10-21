using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a section call answers success only when the counter grew by exactly one and the
/// added row reads back with the written offset.</summary>
/// <remarks>MEASURED defect: the call answered success while the counter stayed 0 and the model held no
/// section — the check compared nothing, so an absent cut reached the client as a created one.
/// History: docs/decisions/sections.md#assembly-section</remarks>
public sealed class SectionCreationRuleTests
{
    [Fact]
    public void UnchangedCounterIsARefusal()
    {
        var reason = SectionCreationRule.RefusalReason(0, 0, new[] { 25d }, Array.Empty<double?>());

        Assert.Equal(SectionCreationRule.CountUnchanged, reason);
    }

    [Fact]
    public void GrownCounterWithTheWrittenStepIsConfirmed()
    {
        Assert.Null(SectionCreationRule.RefusalReason(0, 1, new[] { 25d }, new double?[] { 25d }));
        Assert.Null(SectionCreationRule.RefusalReason(1, 2, new[] { 5d }, new double?[] { 5d }));
    }

    [Fact]
    public void GrownCounterWithoutTheStepIsARefusal()
    {
        var empty = SectionCreationRule.RefusalReason(0, 1, new[] { 25d }, Array.Empty<double?>());
        var unread = SectionCreationRule.RefusalReason(0, 1, new[] { 25d }, new double?[] { null });
        var other = SectionCreationRule.RefusalReason(0, 1, new[] { 25d }, new double?[] { 5d });

        Assert.Equal(SectionCreationRule.StepMissing, empty);
        Assert.Equal(SectionCreationRule.StepMissing, unread);
        Assert.Equal(SectionCreationRule.StepMissing, other);
    }

    [Fact]
    public void CounterThatJumpedIsARefusal()
    {
        var reason = SectionCreationRule.RefusalReason(0, 2, new[] { 25d }, new double?[] { 25d, 5d });

        Assert.Equal(SectionCreationRule.CountJumped, reason);
    }

    [Fact]
    public void UnreadCounterIsARefusal()
    {
        Assert.Equal(SectionCreationRule.CountUnchanged,
            SectionCreationRule.RefusalReason(null, null, Array.Empty<double>(), Array.Empty<double?>()));
        Assert.False(SectionCreationRule.GrewByOne(null, 1));
        Assert.True(SectionCreationRule.GrewByOne(0, 1));
    }
}
