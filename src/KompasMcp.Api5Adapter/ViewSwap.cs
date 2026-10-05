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

    public bool? Restored { get; set; }

    public string? RestoreNote { get; set; }

    public required string Note { get; init; }
}

/// <summary>What to do with the window view after a snapshot: put the previous projection back, or leave
/// the requested one. The decision itself lives in <see cref="ViewRestorePlan"/> (Domain), where the
/// deterministic test lane can call it without КОМПАС.</summary>
/// <remarks>INVARIANT: <c>NotNeeded</c> is returned only when the caller explicitly said
/// <c>keep_view=true</c>; that flag is a CONSENT to keep the new view, not a wish to be honoured
/// silently. MEASURED: in a visible window before the first <c>SetCurrent</c> no projection answers
/// <c>IsCurrent=true</c>, so nothing can be restored — the refusal is taken BEFORE the view changes
/// rather than discovered after the snapshot.</remarks>

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

        var previous = FindCurrentType(collection);
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
            var readBack = previousWire is null
                ? "ни одна проекция не ответила IsCurrent=true"
                : $"прочитан тип {previous}, которому не соответствует ни одно опубликованное имя";
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"Текущую проекцию документа вернуть после снимка нечем: {readBack}. Вид окна НЕ "
                + $"изменён: вызов отказал до SetCurrent('{wanted.Wire}'). Повторите с keep_view=true — "
                + "это согласие оставить запрошенную проекцию после снимка.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["previous_view"] = previousWire,
                    ["previous_type"] = previous,
                    ["keep_view"] = false,
                    ["available_types"] = AllTypes(collection),
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
        var afterType = FindCurrentType(collection);

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
            Restore(document, attempted, out var restoredBack, out var backNote);

            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"Проекция '{wanted.Wire}' не подтверждена обратным чтением: SetCurrent()={switched}, "
                + $"прочитан тип {afterType?.ToString() ?? "null"}, ожидался {wanted.Type}. "
                + "Снимок не снят: подписывать картинку не тем видом нельзя. "
                + (restoredBack == true
                    ? "Прежний вид окна возвращён."
                    : $"Прежний вид окна вернуть не удалось: {backNote}"),
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["set_current_returned"] = switched,
                    ["type_after"] = afterType,
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
            Note = keepView
                ? $"проекция '{wanted.Wire}' применена и оставлена по keep_view"
                : $"проекция '{wanted.Wire}' применена на время снимка; прежний вид "
                    + $"{ViewProjections.Describe(previous!.Value)} будет возвращён после снимка",
        };
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
            restored = FindCurrentType(collection) == previous.Type;
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
            return document.Document.GetViewProjectionCollection() as ksViewProjectionCollection;
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

    private static int? FindCurrentType(ksViewProjectionCollection collection)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = collection.GetByIndex(i) as ksViewProjection;
            if (projection is null)
            {
                continue;
            }

            try
            {
                if (projection.IsCurrent())
                {
                    return SafeType(projection);
                }
            }
            catch (COMException)
            {
                // One unreadable entry must not abort the scan: the remaining entries are still evidence.
                continue;
            }
        }

        return null;
    }

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
            return (int)projection.GetViewProjectonType();
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
