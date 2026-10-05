using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Component-address identity — as a table, without KOMPAS.</summary>
/// <remarks>TEST: exactly the pure function is checked. INVARIANT (findings §1 and §3 of the 05.10.2026 order):
/// <list type="bullet">
/// <item><description>source matches, name matches, matrix CHANGED after an own mutation → identity is NOT false. The matrix is mutable state: both an own mutation and a mate change it.</description></item>
/// <item><description>name DIFFERS while source and matrix match → NOT a refusal. API5/API7 name comparison was never measured live, and a systematic format difference would refuse every assembly mutation.</description></item>
/// </list>
/// History: docs/decisions/tests.md#component-identity</remarks>
public class ComponentIdentityTests
{
    private const ComponentIdentitySignal NotRead = ComponentIdentitySignal.NotRead;
    private const ComponentIdentitySignal Match = ComponentIdentitySignal.Matches;
    private const ComponentIdentitySignal Differ = ComponentIdentitySignal.Differs;

    /// <summary>INVARIANT (item 1): a matrix change after an own mutation does not break identity.</summary>
    [Fact]
    public void SourceAndNameMatch_ButMatrixChangedAfterOwnMutation_IsNotRefused()
    {
        var verdict = ComponentIdentity.Decide(Match, Match, Differ);

        Assert.True(verdict.Matches);
    }

    /// <summary>INVARIANT (item 3): a name difference by itself does not refuse the mutation.</summary>
    [Fact]
    public void NameDiffers_WhileSourceAndMatrixMatch_IsNotRefused()
    {
        var verdict = ComponentIdentity.Decide(Match, Differ, Match);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void SourceMatches_EvenWhenBothSecondarySignalsDiffer_IsNotRefused()
    {
        // INVARIANT: both the name and the matrix differ — still not a refusal: neither is a measured,
        // immutable sign. Refusing here would rest the work on something unverified.
        var verdict = ComponentIdentity.Decide(Match, Differ, Differ);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void SourceDiffers_IsRefusedEvenWhenSecondarySignalsMatch()
    {
        // INVARIANT: the source is the only sign that decides a refusal — the number points at a component
        // with a different file. Negative control: matching name and matrix do NOT override a source
        // difference, else a number leading into a foreign component with the same matrix would pass.
        var verdict = ComponentIdentity.Decide(Differ, Match, Match);

        Assert.False(verdict.Matches);
        Assert.Contains("РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingRead_IsUnconfirmed()
    {
        var verdict = ComponentIdentity.Decide(NotRead, NotRead, NotRead);

        Assert.Null(verdict.Matches);
    }

    [Fact]
    public void SourceUnread_IsUnconfirmedEvenWhenNameMatches()
    {
        // INVARIANT: a matching name does NOT confirm the address — confirming by an unverified sign would
        // allow a mutation by an address that was never checked.
        var verdict = ComponentIdentity.Decide(NotRead, Match, NotRead);

        Assert.Null(verdict.Matches);
    }

    [Fact]
    public void SourceMatches_WithUnreadSecondarySignals_IsConfirmed()
    {
        var verdict = ComponentIdentity.Decide(Match, NotRead, NotRead);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void MatrixMismatch_IsNamedAsNonDecidingInTheDetail()
    {
        // INVARIANT: a matrix difference is NAMED, not swallowed — silence is indistinguishable from "we did not look".
        var verdict = ComponentIdentity.Decide(Match, Match, Differ);

        Assert.Contains("матрица размещения РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("отказом НЕ управляет", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NameMismatch_IsNamedAsNonDecidingInTheDetail()
    {
        var verdict = ComponentIdentity.Decide(Match, Differ, Match);

        Assert.Contains("имя компонента РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("отказом НЕ управляет", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanConfirmation_DoesNotCarryTheRuleExplanation()
    {
        // INVARIANT: on a clean match the note stays clean — the rule is printed where it explains a
        // difference or an unread sign.
        var verdict = ComponentIdentity.Decide(Match, Match, Match);

        Assert.True(verdict.Matches);
        Assert.DoesNotContain(ComponentIdentity.SecondarySignalsAreInformative, verdict.Detail,
            StringComparison.Ordinal);
    }
}
