using System.Runtime.InteropServices;

namespace EVProbe;

/// <summary>Sink for the model document's notification events, declared in the probe with the IID the
/// connection point of <c>ksDocument3DNotify</c> carries.</summary>
/// <remarks>INVARIANT: the sink only records - no COM call and no document work inside a handler, because
/// the worker runs on one STA thread and a call from a handler re-enters it.
/// DOC: the v24 help mirror, page ksdocs3dnotify_events.html (the list of model events)
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

/// <summary>Receives the model events and appends their names to a shared log.</summary>
/// <remarks>TEST: the probe reports the order of the names it got, so "the event arrived" is a line in the
/// evidence file, not a claim. History: docs/decisions/adapter-core.md#document-events</remarks>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class EvSink : IDocument3DNotifySink
{
    private readonly List<string> _log;

    public EvSink(List<string> log) => _log = log;

    private bool Mark(string name)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff} {name}");
        }

        return true;
    }

    public bool BeginRebuild() => Mark("BeginRebuild");
    public bool Rebuild() => Mark("Rebuild");
    public bool BeginChoiceMaterial() => Mark("BeginChoiceMaterial");
    public bool ChoiceMaterial(string material, double density) => Mark($"ChoiceMaterial({material})");
    public bool BeginChoiceMarking() => Mark("BeginChoiceMarking");
    public bool ChoiceMarking(string marking) => Mark($"ChoiceMarking({marking})");
    public bool BeginSetPartFromFile() => Mark("BeginSetPartFromFile");
    public bool BeginCreatePartFromFile(bool typeDoc, object plane) => Mark("BeginCreatePartFromFile");
    public bool CreateEmbodiment(string marking) => Mark($"CreateEmbodiment({marking})");
    public bool DeleteEmbodiment(string marking) => Mark($"DeleteEmbodiment({marking})");
    public bool ChangeCurrentEmbodiment(string marking) => Mark($"ChangeCurrentEmbodiment({marking})");
    public bool BeginChoiceProperty(object obj, double propId) => Mark("BeginChoiceProperty");
    public bool ChoiceProperty(object obj, double propId) => Mark("ChoiceProperty");
    public bool BeginRollbackFeatures() => Mark("BeginRollbackFeatures");
    public bool RollbackFeatures() => Mark("RollbackFeatures");
    public bool BedinLoadCombinationChange(int index) => Mark($"BedinLoadCombinationChange({index})");
    public bool LoadCombinationChange(int index) => Mark($"LoadCombinationChange({index})");
    public bool BeginDeleteMaterial() => Mark("BeginDeleteMaterial");
    public bool DeleteMaterial() => Mark("DeleteMaterial");
    public bool BeginDeleteProperty(object obj, double propId) => Mark("BeginDeleteProperty");
    public bool DeleteProperty(object obj, double propId) => Mark("DeleteProperty");
}

/// <summary>The two COM members a subscription needs; declared here because the interop assemblies carry
/// the KOMPAS types only.</summary>
/// <remarks>DOC: the v24 help mirror, page eventscommon.html - the Automation route
/// is the standard ConnectionPoint mechanism (Advise/Unadvise), and unsubscribing is the subscriber's duty.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
[ComImport]
[Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnectionPointContainer
{
    void EnumConnectionPoints(out IntPtr ppEnum);
    void FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCp);
}

[ComImport]
[Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnectionPoint
{
    void GetConnectionInterface(out Guid pIID);
    void GetConnectionPointContainer(out IntPtr ppCPC);
    void Advise([MarshalAs(UnmanagedType.Interface)] object pUnkSink, out int pdwCookie);
    void Unadvise(int dwCookie);
    void EnumConnections(out IntPtr ppEnum);
}
