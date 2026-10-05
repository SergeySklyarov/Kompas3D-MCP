using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.Api5Adapter;

/// <summary>Visibility of a KOMPAS instance and its documents.</summary>
/// <remarks>INVARIANT: showing is reported by OBSERVATION, never by the fact of the call. Visibility is
/// therefore checked two independent ways — the COM property (<c>KompasObject.Visible</c>) and Windows
/// (<c>IsWindowVisible</c> on the main window) — and the document mode is re-read from the document
/// itself (<c>ksDocument3D.invisibleMode</c>), not inferred from what was requested.
/// History: docs/decisions/adapter-core.md#visibility</remarks>
public sealed partial class Api5Session
{
    /// <summary>Observed state of the application window: what COM says, what Windows says, and whether a
    /// window was obtained at all. The fields are kept separate on purpose — collapsing them into one
    /// "visible" is what produced the defect.</summary>
    public sealed record WindowObservation(
        bool? ComProperty,
        long WindowHandle,
        bool? WindowVisible,
        IReadOnlyList<string> VisibleChildTitles,
        string? Error)
    {
        /// <summary>Visible if and only if both independent observations say "yes".</summary>
        public bool Visible => ComProperty == true && WindowVisible == true;

        public bool Observed => ComProperty is not null || WindowVisible is not null;
    }

    /// <summary>Interrogates the application without deriving either confirmation from the other.</summary>
    public static WindowObservation ObserveApplicationWindow(KompasObject application)
    {
        bool? com = null;
        long handle = 0;
        bool? win32 = null;
        var titles = new List<string>();
        string? error = null;

        try
        {
            com = application.Visible;
        }
        catch (Exception ex)
        {
            error = "Visible: " + ex.GetType().Name;
        }

        try
        {
            handle = application.ksGetHWindow();
        }
        catch (Exception ex)
        {
            error ??= "ksGetHWindow: " + ex.GetType().Name;
        }

        if (handle != 0)
        {
            var hwnd = new IntPtr(handle);
            try
            {
                // Separate and independent of the COM property: this pair is what keeps "there is a PID"
                // from being passed off as "the window is visible".
                win32 = NativeMethods.IsWindowVisible(hwnd);
                titles = NativeMethods.VisibleChildWindowTitles(hwnd) ?? new List<string>();
            }
            catch (Exception ex)
            {
                error ??= "IsWindowVisible: " + ex.GetType().Name;
            }
        }

        return new WindowObservation(com, handle, win32, titles, error);
    }

    /// <summary>Asks to show or hide the instance and checks what happened. Assumes nothing about the
    /// result: it returns the observation after the attempt, not a "we called set" flag.</summary>
    /// <remarks>After showing, the window may not have redrawn yet, so the observation is taken with a
    /// short wait: without it the very first launch reported visible=false for a correct action (a race,
    /// not a defect).</remarks>
    private static WindowObservation ApplyApplicationVisibility(KompasObject application, bool wantVisible)
    {
        try
        {
            application.Visible = wantVisible;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // A failed show does not cancel the session: the user gets the actual state and the error
            // code in the response, not a "success" meaning something else.
            return new WindowObservation(null, 0, null, Array.Empty<string>(),
                "set_Visible: " + ex.GetType().Name + ": " + ex.Message);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var observed = ObserveApplicationWindow(application);
            if (observed.Visible == wantVisible)
            {
                return observed;
            }

            Thread.Sleep(100);
        }

        return ObserveApplicationWindow(application);
    }

    /// <summary>The document's visibility mode: what the document actually reports about itself.</summary>
    /// <remarks>DOC: <c>ksDocument3D.invisibleMode</c> is read-only; it is the only available way to ask
    /// the document itself rather than recall what was requested at Create/Open.</remarks>
    private static bool? ObserveDocumentVisible(ksDocument3D document)
    {
        try
        {
            return !document.invisibleMode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Makes the document active and, only if it is visible, refreshes the view.</summary>
    /// <remarks>Returns observation codes, not "success": <c>SetActive()</c> answers Boolean, and the
    /// return convention of <c>ksRefreshActiveWindow()</c> (Int32) is uncalibrated, so it is recorded
    /// as-is and does not decide whether the document is shown. The camera is deliberately untouched:
    /// <c>ZoomPrevNextOrAll</c> is never called in this code (see Api5SessionVisibilityGuardTests),
    /// because resetting the view after every operation is exactly what the requirement forbids.</remarks>
    private static (bool? Activated, object? Refresh) PresentDocument(
        KompasObject application, ksDocument3D document, bool documentVisible)
    {
        bool? activated = null;
        try
        {
            activated = document.SetActive();
        }
        catch (Exception)
        {
            activated = null;
        }

        if (!documentVisible)
        {
            return (activated, null);
        }

        object? refresh;
        try
        {
            refresh = application.ksRefreshActiveWindow();
        }
        catch (Exception ex)
        {
            refresh = ex.GetType().Name;
        }

        return (activated, refresh);
    }

    /// <summary>View refresh after a mutation on a visible document. Called from the shared
    /// <see cref="BumpRevision"/> path, so it does not depend on which operation changed the model.</summary>
    private void RefreshViewAfterMutation(DocumentEntry document)
    {
        if (!document.DocumentsVisible)
        {
            // Hidden mode stays exactly as it was: no window traffic. This is both regression protection
            // (the call count in hidden mode does not change) and the point of the requirement "hidden
            // mode is preserved and verified separately".
            return;
        }

        if (!_applications.TryGetValue(document.ApplicationId, out var application))
        {
            return;
        }

        try
        {
            application.Application.ksRefreshActiveWindow();
        }
        catch (Exception)
        {
            // The view is not the model's result: a failed redraw must not turn a successful geometric
            // operation into an error.
        }
    }
}
