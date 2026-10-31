using System.Runtime.InteropServices;
using KompasMcp.Domain.Documents;

namespace KompasMcp.Api5Adapter.Com;

/// <summary>Event sink and connection point for the model document's notifications.</summary>
/// <remarks>DOC: the v24 help mirror, pages <c>eventscommon.html</c> (the Automation route is the standard
/// ConnectionPoint mechanism, and unsubscribing is the subscriber's duty),
/// <c>ksdocument3d_getdocument3dnotify.html</c> (the source comes from
/// <c>ksDocument3D::GetDocument3DNotify</c>) and <c>ksdocument3dnotify_events.html</c> (the 21 events).
/// History: docs/decisions/adapter-core.md#document-events</remarks>
[ComImport]
[Guid("b6c1bcfd-68da-4a0a-a95c-296084c6a01a")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IDocument3DNotifySink
{
    [DispId(1)] bool BeginRebuild();
    [DispId(2)] bool Rebuild();
    [DispId(3)] bool BeginChoiceMaterial();
    [DispId(4)] bool ChoiceMaterial(string material, double density);
    [DispId(5)] bool BeginChoiceMarking();
    [DispId(6)] bool ChoiceMarking(string marking);
    [DispId(7)] bool BeginSetPartFromFile();
    [DispId(8)] bool BeginCreatePartFromFile(bool typeDoc, object plane);
    [DispId(9)] bool CreateEmbodiment(string marking);
    [DispId(10)] bool DeleteEmbodiment(string marking);
    [DispId(11)] bool ChangeCurrentEmbodiment(string marking);
    [DispId(12)] bool BeginChoiceProperty(object obj, double propId);
    [DispId(13)] bool ChoiceProperty(object obj, double propId);
    [DispId(14)] bool BeginRollbackFeatures();
    [DispId(15)] bool RollbackFeatures();
    [DispId(16)] bool BedinLoadCombinationChange(int index);
    [DispId(17)] bool LoadCombinationChange(int index);
    [DispId(18)] bool BeginDeleteMaterial();
    [DispId(19)] bool DeleteMaterial();
    [DispId(20)] bool BeginDeleteProperty(object obj, double propId);
    [DispId(21)] bool DeleteProperty(object obj, double propId);
}

/// <summary>Receives the document's notifications and raises the mark of the owning document.</summary>
/// <remarks>INVARIANT: the handler never refuses anything - the bridge observes the user's work and does not
/// take part in it, so every answer is <c>true</c>. MEASURED: the only event that arrives for a model change is
/// <c>Rebuild</c>; save and close raise nothing on this interface.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class DocumentEventSink : IDocument3DNotifySink
{
    private readonly DocumentEventMark _mark;

    public DocumentEventSink(DocumentEventMark mark) => _mark = mark;

    private bool Allow(string name)
    {
        if (name == "Rebuild")
        {
            _mark.Raise();
        }

        return true;
    }

    public bool BeginRebuild() => Allow(nameof(BeginRebuild));
    public bool Rebuild() => Allow(nameof(Rebuild));
    public bool BeginChoiceMaterial() => Allow(nameof(BeginChoiceMaterial));
    public bool ChoiceMaterial(string material, double density) => Allow(nameof(ChoiceMaterial));
    public bool BeginChoiceMarking() => Allow(nameof(BeginChoiceMarking));
    public bool ChoiceMarking(string marking) => Allow(nameof(ChoiceMarking));
    public bool BeginSetPartFromFile() => Allow(nameof(BeginSetPartFromFile));
    public bool BeginCreatePartFromFile(bool typeDoc, object plane) => Allow(nameof(BeginCreatePartFromFile));
    public bool CreateEmbodiment(string marking) => Allow(nameof(CreateEmbodiment));
    public bool DeleteEmbodiment(string marking) => Allow(nameof(DeleteEmbodiment));
    public bool ChangeCurrentEmbodiment(string marking) => Allow(nameof(ChangeCurrentEmbodiment));
    public bool BeginChoiceProperty(object obj, double propId) => Allow(nameof(BeginChoiceProperty));
    public bool ChoiceProperty(object obj, double propId) => Allow(nameof(ChoiceProperty));
    public bool BeginRollbackFeatures() => Allow(nameof(BeginRollbackFeatures));
    public bool RollbackFeatures() => Allow(nameof(RollbackFeatures));
    public bool BedinLoadCombinationChange(int index) => Allow(nameof(BedinLoadCombinationChange));
    public bool LoadCombinationChange(int index) => Allow(nameof(LoadCombinationChange));
    public bool BeginDeleteMaterial() => Allow(nameof(BeginDeleteMaterial));
    public bool DeleteMaterial() => Allow(nameof(DeleteMaterial));
    public bool BeginDeleteProperty(object obj, double propId) => Allow(nameof(BeginDeleteProperty));
    public bool DeleteProperty(object obj, double propId) => Allow(nameof(DeleteProperty));
}

[ComImport]
[Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnectionPointContainer
{
    void EnumConnectionPoints(out IntPtr enumerator);
    void FindConnectionPoint(ref Guid riid, out IConnectionPoint point);
}

[ComImport]
[Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnectionPoint
{
    void GetConnectionInterface(out Guid iid);
    void GetConnectionPointContainer(out IntPtr container);
    void Advise([MarshalAs(UnmanagedType.Interface)] object sink, out int cookie);
    void Unadvise(int cookie);
    void EnumConnections(out IntPtr enumerator);
}

/// <summary>The one call a release makes, separated so the "exactly once" rule is testable without COM.</summary>
internal interface IAdviseRelease
{
    void Unadvise(int cookie);
    void Release();
}

/// <summary>One subscription to a model document's events, released exactly once.</summary>
/// <remarks>DOC: <c>eventscommon.html</c> - for Automation the events come through ConnectionPoint, and
/// releasing them is the subscriber's own job; the file interface says the same for a document being
/// closed. INVARIANT: a failed subscription never blocks the document - the caller keeps
/// <c>conservative</c> and the document opens as before.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public sealed class DocumentEventSubscription : IDisposable
{
    // The connection point of ksDocument3DNotify carries the event interface IID of the sink above.
    private static readonly Guid NotifyIid = new("b6c1bcfd-68da-4a0a-a95c-296084c6a01a");

    private readonly Action<string>? _warn;
    private IAdviseRelease? _release;
    private int _cookie = -1;

    private DocumentEventSubscription(IAdviseRelease release, int cookie, Action<string>? warn)
    {
        _release = release;
        _cookie = cookie;
        _warn = warn;
    }

    /// <summary>Subscription over an arbitrary release seam. Test-only: the production path is
    /// <see cref="TrySubscribe"/>.</summary>
    internal static DocumentEventSubscription Create(IAdviseRelease release, int cookie,
        Action<string>? warn = null) => new(release, cookie, warn);

    /// <summary>Subscribes to the events of one document; <paramref name="error"/> names the reason when it
    /// could not.</summary>
    public static DocumentEventSubscription? TrySubscribe(object? source, DocumentEventMark mark,
        out string? error, Action<string>? warn = null)
    {
        error = null;
        if (source is null)
        {
            error = "notify_source_null";
            return null;
        }

        if (source is not IConnectionPointContainer container)
        {
            error = "connection_point_container_absent";
            return null;
        }

        var iid = NotifyIid;
        try
        {
            container.FindConnectionPoint(ref iid, out var point);
            point.Advise(new DocumentEventSink(mark), out var cookie);
            return new DocumentEventSubscription(new ConnectionPointRelease(point), cookie, warn);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Releases the subscription; a second call does nothing.</summary>
    /// <remarks>INVARIANT: exactly one <c>Unadvise</c> per successful <c>Advise</c> - KOMPAS keeps the cookie
    /// and a repeated release is not a documented no-op. A refused release is written to the worker journal:
    /// swallowed silently, a subscription left behind on a closed document would look like a clean close.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public void Dispose()
    {
        var release = _release;
        var cookie = _cookie;
        _release = null;
        _cookie = -1;
        if (release is null || cookie < 0)
        {
            return;
        }

        try
        {
            release.Unadvise(cookie);
        }
        catch (COMException ex)
        {
            // The document may already be gone; the subscription is released either way and the mark with it.
            _warn?.Invoke($"unadvise refused: 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            release.Release();
        }
    }

    /// <summary>The production release: one <c>Unadvise</c> and the interface reference with it.</summary>
    private sealed class ConnectionPointRelease : IAdviseRelease
    {
        private IConnectionPoint? _point;

        public ConnectionPointRelease(IConnectionPoint point) => _point = point;

        public void Unadvise(int cookie) => _point!.Unadvise(cookie);

        public void Release()
        {
            var point = _point;
            _point = null;
            if (point is not null)
            {
                Marshal.ReleaseComObject(point);
            }
        }
    }
}
