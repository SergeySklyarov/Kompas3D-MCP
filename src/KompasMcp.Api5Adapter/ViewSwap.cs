using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Domain.Imaging;

namespace KompasMcp.Api5Adapter;

/// <summary>State of the view swap around a snapshot. Every field is a MEASUREMENT: what the collection
/// reported, never what the request asked for.</summary>
internal sealed class ViewSwapState
{
    /// <summary>The view was not requested: the snapshot captures the current view untouched.</summary>
    public static ViewSwapState NotRequested => new()
    {
        Note = "текущий вид окна сервера; проекция не запрашивалась",
    };

    /// <summary>The requested projection COULD be switched to — the caller must now restore it in its
    /// own `finally`, because the window view may already have moved.</summary>
    public bool Applied { get; init; }

    /// <summary><c>SetCurrent()</c> was invoked and returned; the OUTCOME was not confirmed by the
    /// read-back. Separate from <see cref="Applied"/> because a named refusal must still try to put the
    /// window back, while a refusal taken BEFORE the call must not touch the view at all.</summary>
    public bool SwitchAttempted { get; init; }

    /// <summary>Keep the requested projection after the snapshot instead of restoring the previous one.
    /// Carried into the state because the restore decision is taken in the CALLER's `finally`, and by then
    /// the request object is out of reach.</summary>
    public bool KeepView { get; init; }

    public string? AppliedView { get; init; }

    public string? PreviousView { get; init; }

    public long? Scheme { get; init; }

    /// <summary>How many projections were read looking for the current one, and how many of those reads
    /// refused. Published with the answer so "no current projection" and "the read failed" can be told
    /// apart without a second run.</summary>
    public int ReadAttempts { get; init; }

    public int ReadFailures { get; init; }

    /// <summary>What the read established (see <see cref="ViewCauses"/>).</summary>
    public string? Cause { get; init; }

    public bool? Restored { get; set; }

    public string? RestoreNote { get; set; }

    public required string Note { get; init; }
}

/// <summary>Applying and restoring a view projection through the documented API5 route.</summary>
/// <remarks>DOC: <c>ksdocument3d_getviewprojectioncollection.html</c>, <c>ksviewprojection.html</c>
/// (<c>GetViewProjectonType</c>, <c>IsCurrent</c>, <c>SetCurrent</c>),
/// <c>ksviewprojectioncollection.html</c> (<c>GetCount</c>, <c>GetByIndex</c>, <c>refresh</c>,
/// <c>viewProjectionScheme</c>). MEASURED: the applied type is <c>ksViewProjectionType</c>
/// (7 = isometric), NOT API5 <c>ProjectionType</c> — the enums do not share numbers; applying a
/// projection leaves <c>IKompasDocument.Changed</c> false, so the revision does not move.
/// History: docs/decisions/adapter-core.md#view-swap</remarks>
internal static class ViewSwap
{
    public static ViewSwapState Apply(DocumentEntry document, ViewProjectionSpec wanted, bool keepView)
    {
        var collection = ReadCollection(document)
            ?? throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                "GetViewProjectionCollection() вернул null: документ не отдал коллекцию проекций, "
                + "и применить вид нечем.",
                RetryPolicy.SameOperationId);

        var previous = ReadCurrent(collection, out var previousAttempts, out var previousFailures);
        var previousCause = CauseOf(previous, previousAttempts, previousFailures);
        var target = FindByType(collection, wanted.Type);
        if (target is null)
        {
            // The order demands this be measured, not guessed: if the collection carries no entry of the
            // requested type, a tool that "switched anyway" would silently render the old view and label
            // it with the new name. Naming the refusal is the only honest option.
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"В коллекции проекций документа нет проекции типа {wanted.Type} ('{wanted.Wire}'): "
                + $"доступны типы [{string.Join(", ", AllTypes(collection))}]. "
                + "Снимок не снят, чтобы не подменить запрошенный вид текущим.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["available_types"] = AllTypes(collection),
                });
        }

        // DECIDED BEFORE `SetCurrent`, not after the snapshot. MEASURED: what matters is
        // RESTORABILITY, not just readability — a fresh document's projection is dimetry (type 8),
        // which reads back but has no published wire name, so the restore would fail silently while
        // the call reported success. The window must not move on a promise the tool cannot keep.
        // History: docs/decisions/adapter-core.md#view-swap
        var previousWire = previous is int previousIndex ? ViewProjections.Describe(previousIndex) : null;
        var previousRestorable = previousWire is not null && ViewProjections.TryResolve(previousWire, out _);
        if (!keepView && !previousRestorable)
        {
            // INVARIANT: the refusal names CAUSE, the number of reads it rests on and the restore outcome,
            // so "no projection reported IsCurrent" and "the read refused" cannot be confused and the
            // caller's window state is never a guess.
            // History: docs/decisions/adapter-core.md#view-swap
            var readBack = previousCause == ViewCauses.Unpublished
                ? $"прочитан тип {previous}, которому не соответствует ни одно опубликованное имя"
                : DescribeCause(previousCause);
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"Текущую проекцию документа вернуть после снимка нечем: {readBack} (прочитано проекций "
                + $"с попыткой определить текущую: {previousAttempts}, чтений отказало: {previousFailures}). "
                + $"Вид окна НЕ изменён: вызов отказал до SetCurrent('{wanted.Wire}'), прежний вид "
                + "восстанавливать не понадобилось. Повторите с keep_view=true — это согласие оставить "
                + "запрошенную проекцию после снимка.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["previous_view"] = previousWire,
                    ["previous_type"] = previous,
                    ["keep_view"] = false,
                    ["available_types"] = AllTypes(collection),
                    ["view_cause"] = previousCause,
                    ["read_attempts"] = previousAttempts,
                    ["read_failures"] = previousFailures,
                    ["view_switch_attempted"] = false,
                    ["previous_view_restored"] = false,
                    ["keep_view_required_without_previous_view"] = true,
                    ["remedy"] = "keep_view=true — согласие оставить запрошенную проекцию после снимка; "
                        + "прежний вид окна вернуть нечем: он либо не читается до первого SetCurrent, "
                        + "либо его тип не входит в опубликованный перечень имён",
                });
        }

        bool switched;
        try
        {
            switched = target.SetCurrent();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"SetCurrent() на проекции '{wanted.Wire}' бросил исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult);
        }

        collection.refresh();
        var afterType = ReadCurrent(collection, out var attempts, out var failures);
        var afterCause = CauseOf(afterType, attempts, failures);

        if (afterType != wanted.Type)
        {
            // SetCurrent returning TRUE is not the result — the read-back is. A mismatch means the view
            // did NOT change, and the snapshot that follows would carry a false label.
            //
            // The switch WAS attempted, so the window view may already have moved even though the label
            // could not be confirmed. The refusal therefore tries to put the previous type back and
            // reports the outcome in `details`: an unconfirmed label must not cost the caller the view.
            var attempted = new ViewSwapState
            {
                SwitchAttempted = true,
                KeepView = keepView,
                AppliedView = ViewProjections.Describe(wanted.Type),
                PreviousView = previous is int previousType ? ViewProjections.Describe(previousType) : null,
                Note = string.Empty,
            };
            RestoreUnconfirmedSwitch(document, attempted, out var restoredBack, out var backNote);

            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"Проекция '{wanted.Wire}' не подтверждена обратным чтением: SetCurrent()={switched}, "
                + $"прочитан тип {afterType?.ToString() ?? "null"}, ожидался {wanted.Type}; причина чтения — "
                + $"{DescribeCause(afterCause)} (проекций с попыткой чтения {attempts}, из них чтений "
                + $"отказало {failures}). Снимок не снят: подписывать картинку не тем видом нельзя. "
                + DescribeUnconfirmedRestore(restoredBack, backNote),
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["set_current_returned"] = switched,
                    ["type_after"] = afterType,
                    ["keep_view"] = keepView,
                    ["view_cause"] = afterCause,
                    ["read_attempts"] = attempts,
                    ["read_failures"] = failures,
                    ["view_switch_attempted"] = true,
                    ["previous_view_restored"] = restoredBack,
                    ["previous_view_restore_note"] = backNote,
                });
        }

        return new ViewSwapState
        {
            Applied = true,
            KeepView = keepView,
            AppliedView = ViewProjections.Describe(wanted.Type),
            PreviousView = previous is int p ? ViewProjections.Describe(p) : null,
            Scheme = ReadScheme(collection),
            ReadAttempts = attempts,
            ReadFailures = failures,
            Cause = afterCause,
            Note = keepView
                ? $"проекция '{wanted.Wire}' применена и оставлена по keep_view"
                : $"проекция '{wanted.Wire}' применена на время снимка; прежний вид "
                    + $"{ViewProjections.Describe(previous!.Value)} будет возвращён после снимка",
        };
    }

    /// <summary>Put the previous projection back after an UNCONFIRMED switch, whatever
    /// <c>keep_view</c> said, and read it back: "I called SetCurrent" is not "the view is back".</summary>
    /// <remarks>INVARIANT: called from <see cref="Apply"/>'s refusal and independent of
    /// <see cref="ViewRestorePlan"/> — that plan answers for a CONFIRMED switch, where consent decides.
    /// Here the switch was ATTEMPTED and the label is unconfirmed, so the window may have moved without
    /// the caller agreeing to anything: <c>keep_view=true</c> consented to a CONFIRMED projection, not to
    /// an unverified one. This mirrors the caller's `finally`, which restores regardless of consent.
    /// NEVER throws, for the same reason as <see cref="Restore"/>.</remarks>
    private static void RestoreUnconfirmedSwitch(
        DocumentEntry document, ViewSwapState state, out bool? restored, out string? note)
    {
        restored = null;
        note = null;
        if (!ViewProjections.TryResolve(state.PreviousView, out var previous))
        {
            restored = false;
            note = "previous_view_not_restored — прежний вид не был прочитан или не входит в "
                + "опубликованный перечень, вернуть его по имени нельзя";
            return;
        }

        try
        {
            var collection = ReadCollection(document);
            var target = collection is null ? null : FindByType(collection, previous.Type);
            if (collection is null || target is null)
            {
                restored = false;
                note = "previous_view_not_restored — коллекция проекций недоступна при возврате вида";
                return;
            }

            target.SetCurrent();
            collection.refresh();
            restored = ReadCurrent(collection, out _, out _) == previous.Type;
            if (restored != true)
            {
                note = "previous_view_not_restored — обратное чтение после возврата не подтвердило "
                    + $"прежний тип {previous.Type}";
            }
        }
        catch (Exception ex)
        {
            restored = false;
            note = $"previous_view_not_restored — возврат вида бросил {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>The unconfirmed-switch restore outcome as one sentence for the refusal text.</summary>
    /// <remarks>INVARIANT: the text never ends in an empty tail. The previous edition appended
    /// <c>"…вернуть не удалось: {note}"</c> unconditionally, but the note is null on every outcome except
    /// a failed restore — so a missing note produced the colon with nothing after it, reading as a silent
    /// failure. The outcome, not the note, decides the wording.</remarks>
    private static string DescribeUnconfirmedRestore(bool? restored, string? note)
    {
        if (restored == true)
        {
            return "Прежний вид окна возвращён.";
        }

        return note is { Length: > 0 }
            ? $"Прежний вид окна вернуть не удалось: {note}"
            : "Прежний вид окна вернуть не удалось: причина не названа, документ остался на "
                + $"выбранной проекции '{restored}'";
    }

    /// <summary>Put the previous projection back. The restore is read back as well: "I called SetCurrent"
    /// is not "the view is back".</summary>
    /// <remarks>INVARIANT: this method NEVER throws. It runs inside the caller's `finally`, and an
    /// exception there would replace a finished snapshot's result with a refusal — the picture would be
    /// lost because the VIEW could not be put back. Every failure, including the contract exception
    /// <see cref="ReadCollection"/> raises around a failing <c>GetViewProjectionCollection()</c>, becomes
    /// `restored = false` plus a note.
    /// History: docs/decisions/adapter-core.md#view-swap</remarks>
    public static void Restore(DocumentEntry document, ViewSwapState state, out bool? restored, out string? note)
    {
        restored = null;
        note = null;

        var decision = ViewRestorePlan.Decide(requested: true, state.KeepView, state.PreviousView);
        if (decision == RestoreDecision.NotNeeded)
        {
            // Either the caller consented to keep the new view, or the previous type was never read.
            // Restoring by an unread value would be a guess; the caller is told instead. Consent is NOT
            // a failure: with keep_view=true the caller must not see a "not restored" note, so `restored`
            // stays null and the note is empty. The caller decides whether to call this at all.
            if (state.KeepView)
            {
                return;
            }

            restored = false;
            note = "previous_view_not_restored — прежний вид не был прочитан или не входит в "
                + "опубликованный перечень, вернуть его по имени нельзя; документ остался на "
                + $"выбранной проекции '{state.AppliedView}'";
            return;
        }

        if (!ViewProjections.TryResolve(state.PreviousView, out var previous))
        {
            restored = false;
            note = "previous_view_not_restored — прежнее имя не разбирается как проекция";
            return;
        }

        try
        {
            var collection = ReadCollection(document);
            var target = collection is null ? null : FindByType(collection, previous.Type);
            if (collection is null || target is null)
            {
                note = "previous_view_not_restored — коллекция проекций недоступна при возврате вида";
                restored = false;
                return;
            }

            target.SetCurrent();
            collection.refresh();
            restored = ReadCurrent(collection, out _, out _) == previous.Type;
            if (restored != true)
            {
                note = "previous_view_not_restored — обратное чтение после возврата не подтвердило "
                    + $"прежний тип {previous.Type}";
            }
        }
        catch (Exception ex)
        {
            // Deliberately every exception, not only COMException: the swap reads the collection through
            // a helper that converts COM failures into a contract exception, and an escaping exception
            // here would destroy the snapshot result the `finally` is protecting.
            restored = false;
            note = $"previous_view_not_restored — возврат вида бросил {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static ksViewProjectionCollection? ReadCollection(DocumentEntry document)
    {
        try
        {
            return document.Document3D.GetViewProjectionCollection() as ksViewProjectionCollection;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"GetViewProjectionCollection() бросил исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult);
        }
    }

    private static ksViewProjection? FindByType(ksViewProjectionCollection collection, int type)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = collection.GetByIndex(i) as ksViewProjection;
            if (projection is not null && SafeType(projection) == type)
            {
                return projection;
            }
        }

        return null;
    }

    /// <summary>Reads the CURRENT projection's type, counting the reads it rests on: a <c>null</c> type is
    /// either "none reported IsCurrent" or "the read refused", and the two counts tell them apart.</summary>
    private static int? ReadCurrent(ksViewProjectionCollection collection, out int attempts, out int failures)
    {
        attempts = 0;
        failures = 0;
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            attempts++;
            ksViewProjection? projection;
            try
            {
                projection = collection.GetByIndex(i) as ksViewProjection;
            }
            catch (COMException)
            {
                failures++;
                continue;
            }

            if (projection is null)
            {
                failures++;
                continue;
            }

            try
            {
                if (!projection.IsCurrent())
                {
                    continue;
                }

                return projection.GetViewProjectonType();
            }
            catch (COMException)
            {
                // One unreadable entry is evidence about the READ, not about the view: counted, not thrown.
                failures++;
            }
        }

        return null;
    }

    /// <summary>What the read established, in the vocabulary published by the answer.</summary>
    private static string CauseOf(int? type, int attempts, int failures)
    {
        if (type is int current)
        {
            return ViewProjections.Describe(current) is null ? ViewCauses.Unpublished : ViewCauses.Named;
        }

        return failures > 0 && failures == attempts ? ViewCauses.ReadFailed : ViewCauses.NotCurrent;
    }

    private static string DescribeCause(string cause) => cause switch
    {
        ViewCauses.NotCurrent => "ни одна проекция не ответила IsCurrent=true (проекция не найдена текущей)",
        ViewCauses.ReadFailed => "чтение проекций отказало (чтение отказало)",
        ViewCauses.Unpublished =>
            "текущая проекция прочитана, но опубликованного имени у её типа нет",
        _ => "текущая проекция прочитана и названа",
    };

    private static IReadOnlyList<int> AllTypes(ksViewProjectionCollection collection)
    {
        var types = new List<int>();
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            if (collection.GetByIndex(i) is ksViewProjection projection && SafeType(projection) is int type)
            {
                types.Add(type);
            }
        }

        return types;
    }

    private static int? SafeType(ksViewProjection projection)
    {
        try
        {
            return projection.GetViewProjectonType();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static long? ReadScheme(ksViewProjectionCollection collection)
    {
        try
        {
            return Convert.ToInt64(collection.viewProjectionScheme);
        }
        catch (COMException)
        {
            return null;
        }
    }
}

/// <summary>The measurements a read of the projection collection establishes, in the vocabulary the answer
/// publishes: the refusal and the success use the same names, so no two places can disagree.</summary>
internal static class ViewCauses
{
    /// <summary>A current projection was read and the document carries its published name.</summary>
    public const string Named = "current_projection_named";

    /// <summary>No projection reported IsCurrent=true.</summary>
    public const string NotCurrent = "no_current_projection";

    /// <summary>Every read of the collection refused (COM failure or an unreadable type).</summary>
    public const string ReadFailed = "read_failed";

    /// <summary>A current type was read, but it has no published wire name and cannot be restored by name.</summary>
    public const string Unpublished = "unpublished_current_projection";
}
