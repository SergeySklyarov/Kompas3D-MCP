using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Creation and editing of a pattern answer a failed parameter read-back the SAME way.</summary>
/// <remarks>INVARIANT: a failed <c>read_back_*</c> check puts <c>parameter_not_read_back</c> FIRST among
/// the unverified aspects — the edit path already did, creation carried the check but no mark.
/// MEASURED: the read result is SUBSTITUTED here, so the rule is exercised without a live КОМПАС.
/// History: docs/decisions/tests.md#pattern-read-back-mark</remarks>
public class PatternReadBackMarkTests
{
    private static NamedCheck ReadBack(bool passed) =>
        new("read_back_save_initial_orientation", passed, Observed: passed ? "false" : "true", Expected: "false");

    private static NamedCheck Other(bool passed) => new("pattern_created", passed);

    /// <summary>INVARIANT: a read-back that returns a DIFFERENT value than was written is marked, and the
    /// mark is first — it names why the feature is not confirmed.</summary>
    [Fact]
    public void FailedReadBack_MarksFirst()
    {
        var unverified = new List<string> { "analytical_volume_not_declared" };

        PatternReadBackMarks.MarkUnreadBack(unverified, [ReadBack(false), Other(true)]);

        Assert.Equal(PatternReadBackMarks.ParameterNotReadBack, unverified[0]);
        Assert.Equal(2, unverified.Count);
    }

    /// <summary>INVARIANT (negative control): a read-back that matched adds NO mark — a mark that is
    /// always present names nothing.</summary>
    [Fact]
    public void MatchingReadBack_AddsNoMark()
    {
        var unverified = new List<string>();

        PatternReadBackMarks.MarkUnreadBack(unverified, [ReadBack(true), Other(true)]);

        Assert.Empty(unverified);
    }

    /// <summary>INVARIANT: only a <c>read_back_*</c> failure is marked; another failed check is not this
    /// rule's business.</summary>
    [Fact]
    public void OtherFailedCheck_IsNotMarked()
    {
        var unverified = new List<string>();

        PatternReadBackMarks.MarkUnreadBack(unverified, [Other(false)]);

        Assert.Empty(unverified);
    }

    /// <summary>INVARIANT: no read-back check at all (a family that has no such member) is not a failure
    /// of one.</summary>
    [Fact]
    public void NoReadBackCheck_IsNotAFailure()
    {
        var unverified = new List<string>();

        PatternReadBackMarks.MarkUnreadBack(unverified, [Other(true)]);

        Assert.Empty(unverified);
    }
}
