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

    /// <summary>The requested projection was switched to and the read-back confirmed the type.</summary>
    public bool Applied { get; init; }

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
            throw new KompasContractException(
                ErrorCodes.ViewUnavailable,
                $"Проекция '{wanted.Wire}' не подтверждена обратным чтением: SetCurrent()={switched}, "
                + $"прочитан тип {afterType?.ToString() ?? "null"}, ожидался {wanted.Type}. "
                + "Снимок не снят: подписывать картинку не тем видом нельзя.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["requested_view"] = wanted.Wire,
                    ["requested_type"] = wanted.Type,
                    ["set_current_returned"] = switched,
                    ["type_after"] = afterType,
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
                : $"проекция '{wanted.Wire}' применена на время снимка; прежний вид будет возвращён",
        };
    }

    /// <summary>Put the previous projection back. The restore is read back as well: "I called SetCurrent"
    /// is not "the view is back".</summary>
    public static void Restore(DocumentEntry document, ViewSwapState state, out bool? restored, out string? note)
    {
        restored = null;
        note = null;

        if (state.PreviousView is not { Length: > 0 } previousName
            || !ViewProjections.TryResolve(previousName, out var previous))
        {
            // The previous type was not read, or is outside the published list (a user projection, say).
            // Restoring by an unread value would be a guess; the caller is told instead.
            note = "previous_view_not_restored — прежний вид не был прочитан или не входит в "
                + "опубликованный перечень, вернуть его по имени нельзя; документ остался на "
                + $"выбранной проекции '{state.AppliedView}'";
            restored = false;
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
        catch (COMException ex)
        {
            restored = false;
            note = $"previous_view_not_restored — возврат вида бросил исключение: {ex.Message}";
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
