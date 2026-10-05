using KompasMcp.Domain.Imaging;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The restore decision around a snapshot — what <c>kompas_export_image</c> does with the window
/// view when the call ends, however it ends.</summary>
/// <remarks>The decision is a PURE function in <c>KompasMcp.Domain</c>, so the deterministic lane calls it
/// DIRECTLY. Reading the adapter's source and grepping for substrings was the defect this replaces: such a
/// test breaks on a rename and passes on wrong logic. Only the one property nothing here can prove without
/// КОМПАС — <c>Restore</c> never lets an exception escape the caller's <c>finally</c> — is still checked
/// against the source, and that check is named as such. INVARIANT: <c>keep_view=true</c> is a CONSENT,
/// answered first. History: docs/decisions/adapter-core.md#view-swap</remarks>
public sealed class ViewRestorePlanTests
{
    /// <summary>The full table of the decision, one row per reachable combination.</summary>
    /// <remarks>MEASURED facts the rows encode: before the first <c>SetCurrent</c> no projection answers
    /// <c>IsCurrent=true</c>, so the previous view is UNREAD (null); a fresh document's projection is
    /// dimetry, which reads back but, when its type has no published name, is UNPUBLISHED (also null to
    /// this function).</remarks>
    [Theory]
    // requested, keepView, previousView, expected
    // The caller did not ask for a projection: there is nothing to put back.
    [InlineData(false, false, null, RestoreDecision.NotNeeded)]
    [InlineData(false, false, "isometric", RestoreDecision.NotNeeded)]
    // Consent: the caller asked to keep the requested projection. The read-back is irrelevant — a
    // readable previous view does NOT cause a restore the caller did not want.
    [InlineData(true, true, "isometric", RestoreDecision.NotNeeded)]
    [InlineData(true, true, "dimetric", RestoreDecision.NotNeeded)]
    [InlineData(true, true, null, RestoreDecision.NotNeeded)]
    // The previous view is readable AND published: put it back.
    [InlineData(true, false, "isometric", RestoreDecision.Restore)]
    [InlineData(true, false, "front", RestoreDecision.Restore)]
    [InlineData(true, false, "dimetric", RestoreDecision.Restore)]
    // The previous view was not read (visible window, before the first SetCurrent).
    [InlineData(true, false, null, RestoreDecision.NotNeeded)]
    // The previous view was read but its type is not published (an empty name is the same fact here:
    // nothing to address by name).
    [InlineData(true, false, "", RestoreDecision.NotNeeded)]
    public void Decide_returns_the_named_outcome(
        bool requested, bool keepView, string? previousView, RestoreDecision expected)
    {
        Assert.Equal(expected, ViewRestorePlan.Decide(requested, keepView, previousView));
    }

    [Fact]
    public void Consent_is_answered_before_the_previous_view_is_considered()
    {
        // Order matters: with keep_view the caller consented to the new view. If the previous view were
        // consulted first, its being unreadable could not turn a consent into a refusal — Decide returns
        // NotNeeded either way — but the CALLER would then be tempted to report a missing restore. The
        // decision and the reporting must agree that consent means "nothing to do", so both cases answer
        // identically.
        Assert.Equal(
            ViewRestorePlan.Decide(true, keepView: true, previousView: "isometric"),
            ViewRestorePlan.Decide(true, keepView: true, previousView: null));
    }

    [Fact]
    public void An_unread_previous_view_is_never_restored_by_guess()
    {
        // Restoring by a value nobody read would be a guess dressed as a measurement: Decide must not
        // return Restore for a null previous view, whatever the other flags say.
        Assert.NotEqual(RestoreDecision.Restore, ViewRestorePlan.Decide(true, false, null));
    }

    [Fact]
    public void Restore_never_lets_an_exception_escape_the_callers_finally()
    {
        // THIS ONE IS READ FROM THE SOURCE, and the reason is named: `Restore` runs inside the caller's
        // `finally`, and an exception there would replace a FINISHED snapshot's result with a refusal —
        // the picture lost because the VIEW could not be put back. Reaching that in a test needs KOMPAS to
        // throw from `GetViewProjectionCollection()`, which the deterministic lane cannot do. So the
        // guarantee is checked positionally, and only this guarantee: the method catches `Exception`, not
        // just `COMException` (a COM failure is converted into a contract exception by the collection
        // reader, and that converted one is exactly what used to escape), and it sets `restored = false`
        // on the failure path rather than rethrowing.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "KompasMcp.Api5Adapter", "ViewSwap.cs"));
        var body = Body(source, "public static void Restore(");

        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (COMException", body, StringComparison.Ordinal);
        Assert.Contains("restored = false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmed_consent_skips_the_restore_entirely()
    {
        // §3 of the order: with keep_view=true and a confirmed switch, the restore must not run at all —
        // otherwise `view_restored=false` and a "previous_view_not_restored" note land in the response for
        // work the caller explicitly declined. The caller must therefore gate the call on consent being
        // ABSENT, which is asserted here by the name of the guard it must use.
        var caller = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "KompasMcp.Api5Adapter", "Api5Session.ImageExport.cs"));

        Assert.Contains("viewState.Applied && viewState.KeepView", caller, StringComparison.Ordinal);
    }

    [Fact]
    public void Unconfirmed_switch_still_reaches_the_restore()
    {
        // §3 of the older order, kept: when SetCurrent went through but the read-back could not confirm
        // the type, the window may have moved anyway, so that path restores REGARDLESS of keep_view and
        // names the outcome in the refusal's `details`. The gate must therefore include SwitchAttempted.
        var caller = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "KompasMcp.Api5Adapter", "Api5Session.ImageExport.cs"));

        Assert.Contains("viewState.SwitchAttempted", caller, StringComparison.Ordinal);
        Assert.Contains("out var restored, out var restoreNote", caller, StringComparison.Ordinal);
    }

    [Fact]
    public void Unconfirmed_switch_is_restored_by_its_own_path_not_by_the_consent_plan()
    {
        // The refusal inside Apply decides the unconfirmed switch itself: a keep_view=true there was a
        // consent to a CONFIRMED projection, not to an unverified one, so the plan's "consent => do
        // nothing" answer must NOT be reused. Asserted by the name of the dedicated helper — and by the
        // absence of the plain `Restore(` call with the old signature argument order, which would route
        // the unconfirmed case through the consent check.
        var swap = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "KompasMcp.Api5Adapter", "ViewSwap.cs"));
        var apply = Body(swap, "public static ViewSwapState Apply(");

        Assert.Contains("RestoreUnconfirmedSwitch(document, attempted", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore(document, attempted", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unconfirmed_restore_sentence_never_ends_in_an_empty_tail()
    {
        // The previous edition appended "…вернуть не удалось: {note}" unconditionally, and the note is
        // null on every outcome except a failed restore — so a missing note produced the colon with
        // nothing after it. The outcome, not the note, must decide the wording. Checked positionally
        // because the failure is in the refusal STRING, which cannot be reached without КОМПАС.
        var swap = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "KompasMcp.Api5Adapter", "ViewSwap.cs"));
        var body = Body(swap, "private static string DescribeUnconfirmedRestore(");

        Assert.Contains("note is { Length: > 0 }", body, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"Прежний вид окна вернуть не удалось: {backNote}\"", body,
            StringComparison.Ordinal);
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KompasMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }

    /// <summary>Body of a declaration: from its opening brace to the matching closing one, with nested
    /// braces counted. A plain substring slice lied the moment the member grew inner blocks.</summary>
    private static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в объявлении не найдено: {declaration}");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"у объявления нет тела: {declaration}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        Assert.Fail($"тело объявления не закрыто: {declaration}");
        return string.Empty;
    }
}
