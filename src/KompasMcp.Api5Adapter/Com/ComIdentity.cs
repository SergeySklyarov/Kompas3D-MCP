using System.Runtime.InteropServices;

namespace KompasMcp.Api5Adapter.Com;

/// <summary>Identity of a COM object, stated as COM states it: the <c>IUnknown</c> pointer.</summary>
/// <remarks>INVARIANT: identity is the COM object's own identity, not a guess about КОМПАС — two wrappers over
/// one model object answer with the same pointer, and two model objects never share one while both are alive.
/// <para>INVARIANT: the pointer is released as soon as it is read; the object stays alive because the registry
/// holds it. The string is the value of a live pointer, compared only within one session.</para>
/// MEASURED: a sketch is reachable by two routes (creation's entity and the part's entity collection); which
/// of them is "the same object" is asked here, not assumed.
/// History: docs/decisions/adapter-sketch.md#one-live-reference</remarks>
public static class ComIdentity
{
    /// <summary>The object's <c>IUnknown</c> as a string, or <c>null</c> when it cannot be stated —
    /// not a COM object, or the call failed. <c>null</c> means "identity unknown", never "different":
    /// the caller then mints a new reference rather than merging two objects under one address.</summary>
    public static string? Of(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            var unknown = Marshal.GetIUnknownForObject(payload);
            if (unknown == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return unknown.ToInt64().ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or COMException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}
