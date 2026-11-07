using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The reconciliation rule for an open whose answer was lost: the attempt evidence decides, and a
/// dead instance before the call is a DEFINITE "nothing was opened".</summary>
/// <remarks>WHY this test exists. MEASURED: an open after the attached instance died answered
/// OUTCOME_UNKNOWN with no document_id and no base revision, so reconciliation answered "nothing to compare
/// with" and the client had to guess. The rule is pure, so the three answers are held here.
/// History: docs/decisions/host.md#open-attempt-evidence</remarks>
public class OpenAttemptPolicyTests
{
    [Fact]
    public void DeadInstanceBeforeTheCall_ProvesNothingWasOpened()
    {
        var verdict = OpenAttemptPolicy.Decide(instanceAliveBeforeCall: false, instanceAliveAtAnswer: null, out var reason);

        Assert.Equal(OpenAttemptVerdict.NotStarted, verdict);
        Assert.Contains(OpenAttemptPolicy.NotStartedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ДО начала", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AliveBeforeAndDeadNow_IsALossAfterTheStart_NotAProof()
    {
        var verdict = OpenAttemptPolicy.Decide(instanceAliveBeforeCall: true, instanceAliveAtAnswer: false, out var reason);

        Assert.Equal(OpenAttemptVerdict.LostAfterStart, verdict);
        Assert.Contains(OpenAttemptPolicy.LostAfterStartCode, reason, StringComparison.Ordinal);
        Assert.Contains("ПОСЛЕ начала", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, null)]
    [InlineData(null, false)]
    [InlineData(null, null)]
    public void AnythingElse_StaysUndeterminedWithItsReason(bool? aliveBefore, bool? aliveAtAnswer)
    {
        var verdict = OpenAttemptPolicy.Decide(aliveBefore, aliveAtAnswer, out var reason);

        Assert.Equal(OpenAttemptVerdict.Undetermined, verdict);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
