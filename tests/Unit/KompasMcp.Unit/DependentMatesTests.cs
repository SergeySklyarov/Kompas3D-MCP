using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The dependent-mate agreement rule — as a table, without KOMPAS.</summary>
/// <remarks>TEST: exactly the pure function is checked. INVARIANT: the kernel's answer and the collection
/// walk must agree; a disagreement is NEVER resolved in favour of the smaller number, and an unread kernel
/// answer is NOT agreement (it means nothing was cross-checked).
/// History: docs/decisions/assembly.md#delete-component</remarks>
public class DependentMatesTests
{
    [Fact]
    public void BothRoutesAgree_IsAgreement()
    {
        var verdict = DependentMates.Decide(1, 1);

        Assert.True(verdict.Agree);
        Assert.Contains("сошлись", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRoutesAgreeOnNone_IsAgreement()
    {
        var verdict = DependentMates.Decide(0, 0);

        Assert.True(verdict.Agree);
    }

    [Fact]
    public void KernelFindsMoreThanTheWalk_IsDisagreement()
    {
        // THE DANGEROUS DIRECTION: the walk under-counted, so deleting on its list would remove a component
        // while a mate is still attached. Must not be read as agreement.
        var verdict = DependentMates.Decide(0, 1);

        Assert.False(verdict.Agree);
        Assert.Contains("РАСХОДЯТСЯ", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WalkFindsMoreThanTheKernel_IsAlsoDisagreement()
    {
        // The other direction is a disagreement too: neither route is declared the winner.
        var verdict = DependentMates.Decide(2, 1);

        Assert.False(verdict.Agree);
    }

    [Fact]
    public void UnreadKernelAnswer_IsNotAgreement()
    {
        // "Not compared" and "compared and equal" are different states; the first must not pass as the second.
        var verdict = DependentMates.Decide(3, null);

        Assert.False(verdict.Agree);
        Assert.Contains("НЕ выполнена", verdict.Detail, StringComparison.Ordinal);
    }
}
