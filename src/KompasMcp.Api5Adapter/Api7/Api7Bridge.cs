using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>The API5→API7 bridge within a single session (ADR-004 §1, §5).</summary>
/// <remarks>DOC: some modes are unreachable from API5 (<c>ksChamferDefinition</c> has no angle, only
/// <c>IChamfer.Angle</c> does), so the bridge lives beside the API5 session and uses its STA queue;
/// <c>ksGetApplication7()</c> returns the SAME PID — no second instance.
/// INVARIANT: typed interfaces, not IDispatch — every call goes through the vendor wrapper's vtable;
/// IDispatch in the shared process crashes it, while an unsaved property reads as success.
/// INVARIANT: the transferred-document cache is keyed by document id AND revision, so any mutation invalidates it.
/// History: docs/decisions/adapter-api7.md#bridge</remarks>
internal sealed class Api7Bridge
{
    private readonly KompasObject _application5;
    private readonly Dictionary<string, (long Revision, IModelContainer Container)> _containers = new(StringComparer.Ordinal);
    private IApplication? _application7;
    private string? _bridgeFailure;

    public Api7Bridge(KompasObject application5) => _application5 = application5;

    /// <summary>Why the bridge failed to build (for an honest CAPABILITY_UNAVAILABLE instead of null silence).</summary>
    public string? BridgeFailure => _bridgeFailure;

    /// <summary>The API7 view of the same application. null — the bridge is unavailable, the reason is
    /// in <see cref="BridgeFailure"/>; the call does not throw, because a missing API7 must not break
    /// the API5 routes that work without it.</summary>
    public IApplication? Application()
    {
        if (_application7 is not null)
        {
            return _application7;
        }

        try
        {
            // A typed QI to IApplication: the interop wrapper declaring the interface does not mean
            // the object answers it, so the answer is checked by a cast.
            _application7 = _application5.ksGetApplication7() as IApplication;
            _bridgeFailure = _application7 is null ? "ksGetApplication7() вернул null" : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException)
        {
            _application7 = null;
            _bridgeFailure = Describe(ex);
        }

        return _application7;
    }

    /// <summary>The API7 model-object container for a document; null — no view.</summary>
    public IModelContainer? ContainerFor(ksDocument3D document, string documentId, long revision)
    {
        if (Application() is null)
        {
            return null;
        }

        if (_containers.TryGetValue(documentId, out var cached) && cached.Revision == revision)
        {
            return cached.Container;
        }

        _containers.Remove(documentId);
        try
        {
            var transferred = _application5.TransferInterface(
                document, Api7DualTransfer, 0) as IKompasDocument3D;
            if (transferred?.TopPart is not IModelContainer container)
            {
                _bridgeFailure = "TransferInterface(документ → API7) не дал IModelContainer через TopPart";
                return null;
            }

            _containers[documentId] = (revision, container);
            return container;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _bridgeFailure = Describe(ex);
            return null;
        }
    }

    /// <summary>The API5 → API7 transfer mode. Taken from a vendor enum constant, not a literal: probe E
    /// showed that a wrong transfer mode does not error but yields an object that "reads" yet represents
    /// a different entity.</summary>
    private const int Api7DualTransfer = (int)ksAPITypeEnum.ksAPI7Dual;

    public object? TransferTo7(object source)
    {
        try
        {
            return _application5.TransferInterface(source, Api7DualTransfer, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _bridgeFailure = Describe(ex);
            return null;
        }
    }

    /// <summary>An array of transferred objects for <c>BaseObjects</c> — an array, not a collection.</summary>
    /// <remarks>DOC: <c>IChamfer.BaseObjects</c> requires a SAFEARRAY of IDispatch pointers.
    /// History: docs/decisions/adapter-api7.md#bridge</remarks>
    public object[]? TransferAllTo7(IReadOnlyList<object> sources)
    {
        var transferred = new object[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            if (TransferTo7(sources[i]) is not { } item)
            {
                return null;
            }

            transferred[i] = item;
        }

        return transferred;
    }

    /// <summary>Rebuild after writing to an API7 feature. MEASURED: <c>RebuildModel</c> belongs to
    /// <see cref="IPart7"/>, not <c>IKompasDocument3D</c>; the API5 rebuild stays mandatory — without it
    /// the volume read by API5 lags.</summary>
    public static void Rebuild(IModelContainer container, ksDocument3D document5)
    {
        if (container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        document5.RebuildDocument();
    }

    /// <summary>Invalidate the cache: the document was closed, rebuilt externally, or the revision changed.</summary>
    public void Invalidate(string documentId) => _containers.Remove(documentId);

    public void ReleaseAll()
    {
        foreach (var (_, container) in _containers.Values)
        {
            ComApartment.Release(container);
        }

        _containers.Clear();
        ComApartment.Release(_application7);
        _application7 = null;
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>Chamfer parameters re-read from the model. A separate record because some fields come from
/// API5 (<c>GetChamferParam</c>) and some only from API7 (<c>Angle</c>, <c>BuildingType</c>), and
/// "what the server did not read" must be distinguishable from "read as zero".</summary>
public sealed record ChamferReadDto(
    bool? Transfer,
    double? Distance1Mm,
    double? Distance2Mm,
    double? AngleDeg,
    string? BuildingType,
    bool? Direction,
    bool? Tangent,
    int? BaseObjectCount,
    IReadOnlyList<string> ReadRoutes);

/// <summary>Typed API7 operations over the chamfer (SM-11). Every route is MEASURED on v24 and repeats
/// call to call; no step is declared "supported" merely because a type exists.</summary>
internal static class Api7Chamfer
{
    /// <summary>Create a "distance + angle" chamfer. Returns (created, reason), not an exception: the
    /// caller must distinguish a KOMPAS refusal from an adapter crash.</summary>
    public static (bool Created, string? Failure) TryCreateDistanceAngle(
        IModelContainer container,
        object[] baseObjects,
        double distanceMm,
        double angleDeg,
        bool direction,
        string? name)
    {
        try
        {
            var chamfer = container.Chamfers.Add();
            if (name is not null)
            {
                chamfer.Name = name;
            }

            // DOC: ksChamferSideAngle means "by side and angle": Distance1 sets the leg on the
            // reference side, Angle is the chamfer angle in degrees.
            chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferSideAngle;
            chamfer.BaseObjects = baseObjects;
            chamfer.Distance1 = distanceMm;
            chamfer.Angle = angleDeg;
            chamfer.Direction = direction;
            var updated = chamfer.Update();
            return (updated, updated ? null : "IChamfer.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The number of chamfers in the API7 collection. null — not read (not "zero"; these are
    /// different answers).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Chamfers.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Write parameters into an EXISTING feature via <c>IChamfer</c> — the route that edits the
    /// angle (API5 has no angle member at all).</summary>
    /// <remarks>INVARIANT: only supplied fields are written. In the "distance and angle" method the second
    /// leg is DERIVED from the angle (<c>d₂ = d₁·tg α</c>); writing a stale number would freeze the
    /// derivative and lose the link to the angle.
    /// INVARIANT: "write → Update() → RebuildModel()" is part of the contract — without <c>Update()</c>
    /// the setters return success while the model stays as before. Returns (written, reason), not an
    /// exception: "could not" must not look like "wrote".</remarks>
    public static (bool Written, string? Failure) TryWrite(
        IModelContainer container,
        int index,
        double? distance1Mm,
        double? distance2Mm,
        double? angleDeg,
        bool? direction)
    {
        try
        {
            if (container.Chamfers[index] is not IChamfer chamfer)
            {
                return (false, $"элемент коллекции Chamfers[{index}] не отдаёт IChamfer");
            }

            if (distance1Mm is double d1)
            {
                chamfer.Distance1 = d1;
            }

            if (angleDeg is double angle)
            {
                chamfer.Angle = angle;
            }

            if (direction is bool side)
            {
                chamfer.Direction = side;
            }

            // The second leg is written AFTER the angle: in the "distance and angle" method it is
            // derived, and if the client supplied both an angle and a leg explicitly, it is more honest
            // to apply the explicitly requested value than to leave the derivative of the new angle.
            // This order is deliberate.
            if (distance2Mm is double d2)
            {
                chamfer.Distance2 = d2;
            }

            if (!chamfer.Update())
            {
                return (false, "IChamfer.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Indices of chamfers in the API7 collection that match the API5 feature by the first leg.
    /// Matching by leg, not by name: F.8 MEASURED that a name given in API5 reads differently from API7,
    /// so a name is not an identifier.</summary>
    public static IReadOnlyList<int> FindIndexesByIdenticalFirstLeg(IModelContainer container, double distance1Mm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.Distance1Mm is double d && Math.Abs(d - distance1Mm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>Read chamfer parameters typed, by API7 collection index.</summary>
    public static ChamferReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Chamfers[index] is not IChamfer chamfer)
            {
                return null;
            }

            return new ChamferReadDto(
                Transfer: null,
                Distance1Mm: Read(() => chamfer.Distance1),
                Distance2Mm: Read(() => chamfer.Distance2),
                AngleDeg: Read(() => chamfer.Angle),
                BuildingType: Text(() => chamfer.BuildingType.ToString()),
                Direction: Read(() => chamfer.Direction),
                Tangent: Read(() => chamfer.Tangent),
                BaseObjectCount: chamfer.BaseObjects is object[] array ? array.Length : null,
                ReadRoutes: new[] { "API7 IChamfer" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? Read(Func<bool> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>What exactly was read from a native hole. A separate record, not a bare pair of numbers,
/// because the three SM-07 modes keep their parameters in THREE different interfaces obtained by
/// casting the same <c>IHole3D.HoleParameters</c>, and "what the server did not read" must be
/// distinguishable from "read as zero".</summary>
public sealed record HoleReadDto(
    string? HoleType,
    double? DiameterMm,
    string? DepthType,
    double? DepthMm,
    string? EndFaceType,
    double? SpotfacingDiameterMm,
    double? SpotfacingDepthMm,
    double? CountersinkDiameterMm,
    double? CountersinkAngleDeg,
    double? CountersinkDepthMm,
    string? CountersinkType,
    IReadOnlyList<string> ReadRoutes);

/// <summary>Typed API7 operations over native holes (SM-07); every route is MEASURED on v24.</summary>
/// <remarks>INVARIANT: mode parameters live not on <c>IHole3D</c> but on <c>HoleParameters</c>, a read-only
/// object cast to the interface of ITS OWN mode — the mode numbers are there, not on the hole.
/// INVARIANT: the countersink depth is DERIVED — with <c>CountersinkType = ksCTDiameterAngle (0)</c>, writing
/// into <c>CountersinkDepth</c> changes NOTHING; it equals <c>(rM − rP)/tan(angle/2)</c>, so
/// <c>TryCreateCountersink</c> reads the depth back, not the write.
/// History: docs/decisions/adapter-api7.md#hole</remarks>
/// <summary>Positioning result: whether the offset applied, the refusal reason and route notes.</summary>
internal sealed record PlacementOutcome(
    bool Applied,
    string? Failure,
    IReadOnlyList<string> Notes);

internal static class Api7Hole
{
    /// <summary>A blind hole: a depth value, not a reference to a limiting object.</summary>
    private const ksDepthTypeEnum DepthByValue = ksDepthTypeEnum.ksDTValue;

    /// <summary>A through hole — MEASURED on every row of M.2/M.3.</summary>
    private const ksDepthTypeEnum DepthReachThrough = ksDepthTypeEnum.ksDTReachThrough;

    /// <summary>A flat bottom (the only mode confirmed by a number in M.4).</summary>
    private const ksEndFaceTypeEnum EndFaceFlat = ksEndFaceTypeEnum.ksEFFlat;

    /// <summary>A "diameter + angle" countersink: with it the depth is derived from the angle.</summary>
    private const ksCountersinkTypeEnum CountersinkDiameterAngle = ksCountersinkTypeEnum.ksCTDiameterAngle;

    /// <summary>A position by coordinates on the base surface (MEASURED in M.5).</summary>
    private const ksPoint3DSurfaceParamTypeEnum OffsetByCoords = ksPoint3DSurfaceParamTypeEnum.ksOffsetByCoords;

    /// <summary>The number of native holes in the API7 collection. null — not read (not "zero").</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Holes3D.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Read a hole typed, by API7 collection index. Mode fields are filled only when the object
    /// actually answers that mode's interface: for a through hole of the base type both mode sets stay
    /// null, which is the correct answer.</summary>
    public static HoleReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return null;
            }

            var spotfacing = hole.HoleParameters as ISpotfacingHoleParameters;
            var countersink = hole.HoleParameters as ICountersinkHoleParameters;

            return new HoleReadDto(
                HoleType: Text(() => hole.HoleType.ToString()),
                DiameterMm: Read(() => hole.Diameter),
                DepthType: Text(() => hole.DepthType.ToString()),
                DepthMm: Read(() => hole.Depth),
                EndFaceType: Text(() => hole.EndFaceType.ToString()),
                SpotfacingDiameterMm: spotfacing is null ? null : Read(() => spotfacing.SpotfacingDiameter),
                SpotfacingDepthMm: spotfacing is null ? null : Read(() => spotfacing.SpotfacingDepth),
                CountersinkDiameterMm: countersink is null ? null : Read(() => countersink.CountersinkDiameter),
                CountersinkAngleDeg: countersink is null ? null : Read(() => countersink.CountersinkAngle),
                CountersinkDepthMm: countersink is null ? null : Read(() => countersink.CountersinkDepth),
                CountersinkType: countersink is null ? null : Text(() => countersink.CountersinkType.ToString()),
                ReadRoutes: new[] { "API7 IHole3D" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Edit an EXISTING flat-bottomed blind hole (mode <c>blind_flat</c>).</summary>
    /// <remarks>MEASURED: the hole is taken by the documented <c>IHoles3D.Hole3D[index]</c>, its own-mode
    /// members are written and <c>IModelObject.Update()</c> applied; the volume changes exactly analytically.
    /// INVARIANT: only members of THIS mode are written; <c>HoleType</c>, <c>DepthType</c> and <c>EndFaceType</c>
    /// are mode identification, not caller parameters (blind: <c>ksHTBase</c>, <c>ksDTValue</c>, <c>ksEFFlat</c>).
    /// INVARIANT: an unsupplied numeric member is NOT written — a read value is derived from the model and
    /// must not be frozen as input.
    /// History: docs/decisions/adapter-api7.md#hole</remarks>
    public static (bool Written, string? Failure) TryWriteBlindFlat(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? depthMm)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.DepthType = DepthByValue;
            hole.EndFaceType = EndFaceFlat;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (depthMm is double depth)
            {
                hole.Depth = depth;
            }

            return hole.Update()
                ? (true, null)
                : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Edit an EXISTING through counterbore (mode <c>through_counterbore</c>).</summary>
    /// <remarks>MEASURED: the bore changes the ring difference π/4·(D²−d²)·h. Writes the pilot
    /// <c>Diameter</c>, <c>DepthType = ksDTReachThrough</c> and the <c>ISpotfacingHoleParameters</c>
    /// members; unsupplied ones are not written.
    /// History: docs/decisions/adapter-api7.md#hole</remarks>
    public static (bool Written, string? Failure) TryWriteCounterbore(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? boreDiameterMm,
        double? boreDepthMm)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCounterbore;
            hole.DepthType = DepthReachThrough;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (boreDiameterMm is null && boreDepthMm is null)
            {
                return hole.Update()
                    ? (true, null)
                    : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
            }

            // The parameter interface of ITS OWN mode: for a foreign mode it is unreachable — MEASURED by
            // the probe's control (c) (on a blind hole and on a countersink, ISpotfacingHoleParameters
            // does not answer at all).
            if (hole.HoleParameters is not ISpotfacingHoleParameters spotfacing)
            {
                return (false,
                    "IHole3D.HoleParameters не приводится к ISpotfacingHoleParameters — " +
                    "параметры выточки записать некуда");
            }

            if (boreDiameterMm is double bore)
            {
                spotfacing.SpotfacingDiameter = bore;
            }

            if (boreDepthMm is double boreDepth)
            {
                spotfacing.SpotfacingDepth = boreDepth;
            }

            return hole.Update()
                ? (true, null)
                : (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Edit an EXISTING through countersink (mode <c>through_countersink</c>).</summary>
    /// <remarks>MEASURED: the removed volume is <c>π·h/3·(rM² + rP·rM − 2·rP²)</c> with derived
    /// <c>h = (rM − rP)/tan(angle/2)</c>.
    /// INVARIANT: the countersink depth is NOT written — under <c>ksCTDiameterAngle</c> it is derived, so
    /// writing it would be a number the object does not read; the value is read back AFTER <c>Update()</c>.
    /// History: docs/decisions/adapter-api7.md#hole</remarks>
    public static (bool Written, string? Failure, double? ReportedDepthMm) TryWriteCountersink(
        IModelContainer container,
        int index,
        double? diameterMm,
        double? mouthDiameterMm,
        double? angleDeg)
    {
        try
        {
            if (container.Holes3D[index] is not IHole3D hole)
            {
                return (false, $"элемент коллекции Holes3D[{index}] не отдаёт IHole3D", null);
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.DepthType = DepthReachThrough;
            if (diameterMm is double diameter)
            {
                hole.Diameter = diameter;
            }

            if (hole.HoleParameters is not ICountersinkHoleParameters countersink)
            {
                return (false,
                    "IHole3D.HoleParameters не приводится к ICountersinkHoleParameters — " +
                    "параметры зенковки записать некуда", null);
            }

            countersink.CountersinkType = CountersinkDiameterAngle;
            if (mouthDiameterMm is double mouth)
            {
                countersink.CountersinkDiameter = mouth;
            }

            if (angleDeg is double angle)
            {
                countersink.CountersinkAngle = angle;
            }

            if (!hole.Update())
            {
                return (false, "IHole3D.Update() вернул false — числа записаны, но признак не перестроен",
                    null);
            }

            return (true, null, Safe(() => countersink.CountersinkDepth));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    /// <summary>The hole's position and axis read from the BODY: <c>IHole3D</c> itself has no
    /// coordinates — <c>Axis</c> reads False, <c>DepthVertex</c>/<c>DepthFace</c> read null (MEASURED
    /// M.5). The route is the same as for topology: <c>GetMainBody() → FaceCollection →
    /// GetSurfaceParam() → ksCylinderParam.GetPlacement()</c>. Returns the axis origin of the found
    /// cylindrical face of radius <paramref name="radiusMm"/>, or null if the body has no such face.</summary>
    public static double[]? FindCylinderOrigin(ksPart part, double radiusMm, double toleranceMm)
    {
        var all = FindCylinderOrigins(part, radiusMm, toleranceMm);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>ALL axis origins of cylindrical faces of radius <paramref name="radiusMm"/>, in
    /// face-traversal order.</summary>
    /// <remarks>Needed where one value is not enough: in a document with several holes of one diameter
    /// the "first match" is a foreign axis, and passing it off as the created hole's axis would
    /// attribute a neighbour's coordinates to the feature. A caller who knows the REQUESTED position
    /// must compare it against the list and tell "found what was asked" from "found something matching
    /// by radius".</remarks>
    public static IReadOnlyList<double[]> FindCylinderOrigins(ksPart part, double radiusMm, double toleranceMm)
    {
        var found = new List<double[]>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            return found;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not ksFaceDefinition face)
            {
                continue;
            }

            try
            {
                if (face.GetSurface() is not ksSurface surface || !surface.IsCylinder()
                    || surface.GetSurfaceParam() is not ksCylinderParam cylinder)
                {
                    continue;
                }

                if (Math.Abs(cylinder.radius - radiusMm) > toleranceMm)
                {
                    continue;
                }

                if (cylinder.GetPlacement() is ksPlacement placement
                    && placement.GetOrigin(out var x, out var y, out var z))
                {
                    found.Add(new[] { x, y, z });
                }
            }
            catch (Exception ex) when (ex is COMException)
            {
                // A face whose parameters KOMPAS did not return is no reason to abandon the search for
                // the rest.
            }
        }

        return found;
    }

    /// <summary>Create a through counterbore: a through pilot hole plus a bore. Returns (created,
    /// reason), not an exception: "could not" must not look like "created".</summary>
    /// <remarks>MEASURED: material is removed as the pilot's <c>π·r²·h</c> plus the bore's
    /// <c>π/4·(D²−d²)·h</c> — a ring, not a second full cylinder.</remarks>
    public static (bool Created, string? Failure) TryCreateCounterbore(
        IModelContainer container,
        IModelObject baseSurface,
        double pilotDiameterMm,
        double boreDiameterMm,
        double boreDepthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D");
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCounterbore;
            hole.Diameter = pilotDiameterMm;
            hole.DepthType = DepthReachThrough;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // The offset is written BEFORE Update() and on THIS SAME object: after Update() the feature
            // is built and the write would remain a mere view. All three modes support positioning, not
            // just blind: acceptance HO.6 showed that without this branch a through counterbore
            // silently stayed at the origin with error=None — a wrong position passed off as done.
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено");
                }
            }

            if (hole.HoleParameters is not ISpotfacingHoleParameters spotfacing)
            {
                return (false, "IHole3D.HoleParameters не приводится к ISpotfacingHoleParameters — режим цековки недостижим");
            }

            spotfacing.SpotfacingDiameter = boreDiameterMm;
            spotfacing.SpotfacingDepth = boreDepthMm;

            return hole.Update() ? (true, null) : (false, "IHole3D.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Create a through countersink: a through pilot hole plus a conical bevel.</summary>
    /// <remarks>MEASURED: material removed beyond the pilot equals <c>π·h/3 · (rM² + rP·rM − 2·rP²)</c>,
    /// with <c>h</c> the depth the OBJECT returned. <paramref name="depthMm"/> is passed to
    /// <c>CountersinkDepth</c> for contract completeness, but under <c>CountersinkType = ksCTDiameterAngle</c>
    /// that property is DERIVED, so the returned depth is read back and geometry judged by it, not by the
    /// written number.</remarks>
    public static (bool Created, string? Failure, double? ReportedDepthMm) TryCreateCountersink(
        IModelContainer container,
        IModelObject baseSurface,
        double pilotDiameterMm,
        double mouthDiameterMm,
        double angleDeg,
        double depthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D", null);
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем", null);
            }

            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.Diameter = pilotDiameterMm;
            hole.DepthType = DepthReachThrough;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // The offset is written BEFORE Update() and on THIS SAME object (see TryCreateCounterbore).
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено", null);
                }
            }

            if (hole.HoleParameters is not ICountersinkHoleParameters countersink)
            {
                return (false, "IHole3D.HoleParameters не приводится к ICountersinkHoleParameters — режим зенковки недостижим", null);
            }

            countersink.CountersinkType = CountersinkDiameterAngle;
            countersink.CountersinkDiameter = mouthDiameterMm;
            countersink.CountersinkAngle = angleDeg;
            countersink.CountersinkDepth = depthMm;

            if (!hole.Update())
            {
                return (false, "IHole3D.Update() вернул false", null);
            }

            // The depth is read AFTER Update(): a derived property still holds its previous value
            // before the rebuild, and passing that off as the result would lie about the geometry.
            double? reported = Safe(() => countersink.CountersinkDepth);
            return (true, null, reported);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    /// <summary>Create a flat-bottomed blind hole: <c>ksDTValue</c> (there is no <c>ksDTBlind</c> member
    /// in the vendor enum) plus <c>ksEFFlat</c>. Material removed equals <c>π·r²·h</c>.</summary>
    public static (bool Created, string? Failure) TryCreateBlindFlat(
        IModelContainer container,
        IModelObject baseSurface,
        double diameterMm,
        double depthMm,
        Func<IHoleDisposal, PlacementOutcome>? place = null)
    {
        try
        {
            if (container.Holes3D.Add() is not IHole3D hole)
            {
                return (false, "IHoles3D.Add() вернул не IHole3D");
            }

            if (hole is not IHoleDisposal disposal)
            {
                return (false, "объект отверстия не отвечает на QI(IHoleDisposal) — базовую грань подать нечем");
            }

            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.Diameter = diameterMm;
            hole.DepthType = DepthByValue;
            hole.Depth = depthMm;
            hole.EndFaceType = EndFaceFlat;
            disposal.BaseSurface = baseSurface;
            disposal.Perpendicular = true;

            // The offset is written BEFORE Update() and on THIS SAME object (see TryCreateCounterbore).
            if (place is not null)
            {
                var outcome = place(disposal);
                if (!outcome.Applied)
                {
                    return (false, outcome.Failure ?? "позиционирование не применено");
                }
            }

            return hole.Update() ? (true, null) : (false, "IHole3D.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Move a hole off the origin: <c>Point3DParamSurface</c> plus
    /// <c>OffsetType = ksOffsetByCoords</c> and X/Y offsets.</summary>
    /// <remarks>Returns (applied, reason, route notes): MEASURED that <c>SetSurfaceObject</c> returned
    /// False, recorded as is and not passed off as success — the route is not broken by it, since the
    /// three offset writes move the hole, and probe M.5 placed it exactly at the requested (25, 15).
    /// INVARIANT: if the object does not answer <c>IPoint3DParamSurface</c>, that is a REFUSAL, not
    /// "called and stayed silent" — accepting the offsets and leaving the hole at the origin would pass
    /// off unperformed positioning as done.</remarks>
    public static PlacementOutcome TryPlaceByCoordinates(
        IHoleDisposal disposal,
        double offsetXmm,
        double offsetYmm)
    {
        var notes = new List<string>();
        try
        {
            if (disposal.Point3DParamSurface is null)
            {
                return new PlacementOutcome(
                    false,
                    "IHoleDisposal.Point3DParamSurface вернул null — маршрут позиционирования недоступен",
                    notes);
            }

            if (disposal.Point3DParamSurface is not IPoint3DParamSurface surface)
            {
                return new PlacementOutcome(
                    false,
                    "Point3DParamSurface не приводится к IPoint3DParamSurface — смещения записать некуда",
                    notes);
            }

            disposal.OffsetType = OffsetByCoords;
            surface.Offset1 = offsetXmm;
            surface.Offset2 = offsetYmm;

            notes.Add(
                $"OffsetType={OffsetByCoords} (ksOffsetByCoords), Offset1={offsetXmm}, Offset2={offsetYmm}");
            if (surface.SetSurfaceObject(disposal.BaseSurface) is { } accepted)
            {
                // MEASURED: False. Recorded as is — this fact is why the route is described by the
                // three offset writes rather than by this binding.
                notes.Add("SetSurfaceObject(BaseSurface) → " + accepted);
            }

            return new PlacementOutcome(true, null, notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new PlacementOutcome(false, ex.GetType().Name + ": " + ex.Message, notes);
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>A rotation axis built in the part from two points and re-read from there.</summary>
/// <remarks>Both the axis object (for <c>IRotated.Axis</c>) and what could be read from it are returned:
/// "the axis is built" without reading its state is a return code, not a fact. <see cref="Valid"/> is
/// kept distinct from null — <c>null</c> means "the state could not be read", <c>false</c> means "KOMPAS
/// considers the axis invalid", and these are different answers.</remarks>
internal sealed record AxisHandle(
    IAxis3D Axis,
    IAxis3DBy2Points By2Points,
    bool? Updated,
    bool? Valid,
    int? AxesBefore,
    int? AxesAfter,
    IReadOnlyList<string> Notes);

/// <summary>Typed API7 operations over rotation (SM-03).</summary>
/// <remarks>INVARIANT: neither <c>NewEntity</c> nor <c>Create()</c> — an API5 wrapper around an API7
/// factory gives a mixed lifetime in which <c>Create()</c> returns <c>true</c> on an empty operation and
/// nothing is built; the route is only <c>Rotateds.Add → IRotated → Update()</c> (<c>Profile</c>, <c>Axis</c>).
/// MEASURED: <c>OperationResult</c> decides nothing (kind comes from <see cref="RotationOperation"/>).
/// INVARIANT (angle law): <c>Angle[true]</c> carries the REQUESTED sweep directly; the second half, if
/// equal to the first, DOUBLES the sweep, so it is written as zero (the kind does not affect the law).
/// History: docs/decisions/adapter-api7.md#rotated</remarks>
internal static class Api7Rotated
{
    /// <summary>Operation kind → vendor type. The numbers are MEASURED in R.24 (27/28/29).</summary>
    public static ksObj3dTypeEnum TypeOf(RotationOperation operation) => operation switch
    {
        RotationOperation.Base => ksObj3dTypeEnum.o3d_baseRotated,
        RotationOperation.Boss => ksObj3dTypeEnum.o3d_bossRotated,
        RotationOperation.Cut => ksObj3dTypeEnum.o3d_cutRotated,
        _ => ksObj3dTypeEnum.o3d_bossRotated,
    };

    /// <summary>Direction → vendor enum. <c>dtReverse</c> is rejected by the caller.</summary>
    public static ksDirectionTypeEnum DirectionOf(RotationDirection direction) => direction switch
    {
        RotationDirection.Normal => ksDirectionTypeEnum.dtNormal,
        RotationDirection.Reverse => ksDirectionTypeEnum.dtReverse,
        RotationDirection.Both => ksDirectionTypeEnum.dtBoth,
        RotationDirection.MiddlePlane => ksDirectionTypeEnum.dtMiddlePlane,
        _ => ksDirectionTypeEnum.dtNormal,
    };

    /// <summary>The <c>OperationResult</c> matching the operation kind. Written for consistency with the
    /// tree, not to switch behaviour: MEASURED that this member affects nothing (R.26).</summary>
    public static ksOperationResultEnum OperationResultOf(RotationOperation operation) => operation switch
    {
        RotationOperation.Base => ksOperationResultEnum.ksOperationNewBody,
        RotationOperation.Boss => ksOperationResultEnum.ksOperationUnion,
        RotationOperation.Cut => ksOperationResultEnum.ksOperationCut,
        _ => ksOperationResultEnum.ksOperationNewBody,
    };

    /// <summary>The operation code in the response — the same word as in the contract, not the vendor enum.</summary>
    public static string NameOf(RotationOperation operation) =>
        operation.ToString().ToLowerInvariant();

    /// <summary>The number of rotations in the API7 collection. null — not read (not "zero").</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Rotateds?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Build a rotation axis from two MODEL points in the part itself.</summary>
    /// <remarks>MEASURED: <c>Axes3D</c> is declared on <c>IAuxiliaryGeomContainer</c>, NOT on <c>IModelContainer</c>,
    /// so a plain cast gives <c>null</c> and only a QI on the live part works.
    /// INVARIANT: <c>Update()</c> is called AFTER both points — an axis updated before the points reads
    /// <c>Valid=False</c> and does not enter the tree.
    /// INVARIANT: an invalid axis is named, not discarded — if <c>Valid</c> is not True it is recorded in
    /// the notes and returned; <c>null</c> is returned only when the axis could not be built at all.
    /// History: docs/decisions/adapter-api7.md#rotated</remarks>
    public static AxisHandle? TryBuildAxisBy2Points(
        Api7Bridge bridge,
        object part,
        double[] point1,
        double[] point2)
    {
        var notes = new List<string>();
        try
        {
            if (bridge.TransferTo7(part) is not IModelObject part7)
            {
                notes.Add("деталь не переносится в API7 как IModelObject — ось не построить");
                return null;
            }

            if (part7 is not IModelContainer modelContainer)
            {
                notes.Add("перенесённая деталь не отвечает QI(IModelContainer) — Points3D недостижим");
                return null;
            }

            // A QI, not a container cast: Axes3D is declared on a DIFFERENT interface of the same
            // object, and this is MEASURED (see remarks).
            if (part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                notes.Add("перенесённая деталь не отвечает QI(IAuxiliaryGeomContainer) " +
                    "(IID {950FEBE2-F916-4E77-A37D-B061E5C22FA8}) — Axes3D недостижим");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes)
            {
                notes.Add("IAuxiliaryGeomContainer.Axes3D → null");
                return null;
            }

            if (MakePoint(modelContainer, point1) is not { } p1
                || MakePoint(modelContainer, point2) is not { } p2)
            {
                notes.Add("точки оси не создались — ось не построить");
                return null;
            }

            var before = SafeInt(() => axes.Count);
            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                notes.Add("Axes3D.Add(o3d_axis2Points) не отдал QI(IAxis3DBy2Points)");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;

            // Update() only here, with both points already supplied (MEASURED R.13).
            var updated = SafeBool(by2.Update);
            var valid = SafeBool(() => by2.Valid);
            var after = SafeInt(() => axes.Count);

            notes.Add(
                $"ось по двум точкам: Update()={Raw(updated)}, Valid={Raw(valid)}, " +
                $"осей {Raw(before)}→{Raw(after)}");
            if (valid != true)
            {
                notes.Add("ось построена, но Valid не True — передаю её всё равно: отказ вращения на " +
                    "негодной оси не был бы фактом о вращении");
            }

            return new AxisHandle(by2, by2, updated, valid, before, after, notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"построение оси бросило {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Create a rotation, write its parameters, then <c>Update()</c>.</summary>
    /// <remarks>Returns (object, refusal reason): "could not" must not look like "created"; the object is
    /// handed out because it must be read back after <c>Update()</c>.
    /// INVARIANT: <c>Profile</c> and <c>Axis</c> are written before the rest — a rotation without an axis
    /// has no sweep at all.
    /// INVARIANT: the angle goes into the first half of the pair, the second is zero — the second half
    /// equal to the first DOUBLES the sweep, so zero means "the turn equals the requested angle".
    /// History: docs/decisions/adapter-api7.md#rotated</remarks>
    public static (IRotated? Rotation, string? Failure) TryCreate(
        IModelContainer container,
        RotationOperation operation,
        IModelObject profile,
        IAxis3D axis,
        double angleDeg,
        RotationDirection direction,
        bool thin)
    {
        try
        {
            var rotateds = container.Rotateds;
            if (rotateds is null)
            {
                return (null, "IModelContainer.Rotateds → null: фабрика недостижима");
            }

            if ((rotateds.Add(TypeOf(operation)) as IModelObject) as IRotated is not { } rotation)
            {
                return (null, "Rotateds.Add не отдал объект, отвечающий на QI(IRotated)");
            }

            rotation.Profile = profile;
            rotation.Axis = axis;

            // Indexed properties: both getter and setter take Boolean Normal; a write without the index
            // does not compile (CS0856).
            // The angle goes into the FIRST half of the pair, the second is ZERO — the second half equal
            // to the first DOUBLES the sweep, so zero means "the turn equals the requested angle".
            rotation.Angle[true] = angleDeg;
            rotation.Angle[false] = 0d;
            rotation.Direction = DirectionOf(direction);
            rotation.RotatedType[true] = ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;

            // Written for consistency with the tree and read back by the caller. MEASURED (R.26): this
            // member does NOT affect behaviour — a boss with ksOperationCut changed the volume by 0.
            if (rotation is IRotated1 rotated1)
            {
                rotated1.OperationResult = OperationResultOf(operation);
            }

            // Thin wall: the route was MEASURED on a SOLID body. A true value never reaches here — the
            // caller rejects a requested thin wall before COM — and even here it is not a "just in
            // case": false is a measured setting, not a default.
            if (rotation is IThinParameters thinParameters)
            {
                thinParameters.Thin = thin;
            }

            return rotation.Update()
                ? (rotation, null)
                : (rotation, "IRotated.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Read a rotation typed, by API7 collection index.</summary>
    public static RotatedDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Rotateds?[index] is not IRotated rotation)
            {
                return null;
            }

            var axisState = Safe(() => rotation.Axis) is null ? "нет" : "есть";
            return new RotatedDto(
                OperationType: Text(() => rotation.RotatedType[true].ToString()),
                AngleDeg: Read(() => rotation.Angle[true]),
                Direction: Text(() => rotation.Direction.ToString()),
                AxisState: axisState,
                // A rotation's profile is ONE object (IModelObject), not an array like a fillet's
                // BaseObjects. So "input count" here is either 1 or null, and an array length must not be
                // passed off as it — different types. null means "the profile is not read".
                ProfileInputCount: Safe(() => rotation.Profile) is null ? null : 1);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Indices of rotations whose angle read as the given one. Matching by angle is a FALLBACK,
    /// and that must be said aloud: an angle is not an identity token. Two features with the same angle
    /// exist arbitrarily often (two half-turns about different axes), so "take the first" would edit the
    /// wrong feature, and two candidates mean "nothing to identify with", not "take the first".
    /// History: docs/decisions/adapter-api7.md#rotated</summary>
    public static IReadOnlyList<int> FindIndexesByAngle(IModelContainer container, double angleDeg)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.AngleDeg is double angle && Math.Abs(angle - angleDeg) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>The index of the rotation matching an API5 entity by COMPOSITION (angle and direction).
    /// Returns <c>null</c> if there is no match or more than one: "take the first" would write the angle
    /// into a foreign feature.</summary>
    /// <remarks>Matching by angle is weak (two half-turns about different axes give the same pair), so
    /// ambiguity is resolved by REFUSAL, not by choice.
    /// When the composition cannot be read, the caller passes <paramref name="knownIndex"/>: an entity
    /// from the API5 tree arrives as a raw <c>__ComObject</c> and refuses the cast to <c>IRotated</c>, so
    /// the address is known in advance as the feature's position among the tree's rotations.</remarks>
    public static int? FindIndexFor(IModelContainer container, ksEntity entity, int? knownIndex = null)
    {
        if (Count(container) is not int count || count <= 0)
        {
            return null;
        }

        if (knownIndex is int known)
        {
            return known >= 0 && known < count ? known : null;
        }

        // The addressed entity's angle and direction: the API5 tree entity answers IRotated as the same
        // model object, so its values are the reference for matching.
        double? wantAngle = null;
        string? wantDirection = null;
        if (entity is IRotated rotated)
        {
            wantAngle = Read(() => rotated.Angle[true]);
            wantDirection = Text(() => rotated.Direction.ToString());
        }

        var matches = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var candidate = Read(container, i);
            if (candidate is null)
            {
                continue;
            }

            if (wantAngle is double want
                && candidate.AngleDeg is double got
                && Math.Abs(got - want) > 1e-6)
            {
                continue;
            }

            if (wantDirection is not null && candidate.Direction is not null
                && !string.Equals(candidate.Direction, wantDirection, StringComparison.Ordinal))
            {
                continue;
            }

            matches.Add(i);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Write angle and direction into an EXISTING rotation and call <c>Update()</c>. Returns
    /// (written, reason), not an exception: the caller must distinguish a KOMPAS refusal from an adapter
    /// crash.</summary>
    /// <remarks>INVARIANT: only supplied fields are written — an unsupplied one keeps what is in the
    /// model. <c>Angle[false]</c> is reset to zero ALWAYS when the angle is written — MEASURED (F.1a
    /// versus F.1c) that the second half of the pair equal to the first doubles the sweep, so "leave as
    /// is" would give a double sector on an apparently point edit of the angle.</remarks>
    public static (bool Written, string? Failure) TryWriteAngleAndDirection(
        IModelContainer container,
        int index,
        double? angleDeg,
        RotationDirection? direction)
    {
        try
        {
            if (container.Rotateds?[index] is not IRotated rotation)
            {
                return (false, $"элемент коллекции Rotateds[{index}] не отдаёт IRotated");
            }

            if (angleDeg is double angle)
            {
                rotation.Angle[true] = angle;
                rotation.Angle[false] = 0d;
            }

            if (direction is RotationDirection want)
            {
                rotation.Direction = DirectionOf(want);
            }

            return rotation.Update()
                ? (true, null)
                : (false, "IRotated.Update() вернул false — угол записан, но признак не перестроен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>A model point from a coordinate array. Both the length and creation are checked:
    /// "accepted an array of two numbers" would give a point at the origin, and the axis would land in
    /// the wrong place.</summary>
    private static IPoint3D? MakePoint(IModelContainer container, IReadOnlyList<double> coordinates)
    {
        if (coordinates.Count != 3)
        {
            return null;
        }

        try
        {
            if (container.Points3D?.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = coordinates[0];
            point.Y = coordinates[1];
            point.Z = coordinates[2];
            return point.Update() ? point : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? SafeBool(Func<bool> value) => Safe(value);

    private static int? SafeInt(Func<int> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }

    private static string Raw(object? value) => value?.ToString() ?? "не прочитано";
}

/// <summary>Fillet parameters re-read from API7. The radius is read only from there: API5 has
/// <c>ksFilletDefinition.radius</c> and it reads, but writing to it on an existing feature is NOT
/// applied; the authoritative source of the radius here is <c>IFillet.Radius1</c>.</summary>
public sealed record FilletReadDto(
    double? RadiusMm,
    double? Radius2Mm,
    bool? Tangent,
    string? BuildingType,
    int? BaseObjectCount,
    IReadOnlyList<int>? BaseObjectReferences,
    IReadOnlyList<string> ReadRoutes);

/// <summary>Typed API7 operations over the fillet (SM-09). Two differences from
/// <see cref="Api7Chamfer"/>, both MEASURED, not inferred from name similarity: the radius lives in
/// <c>IFillet.Radius1</c> (dispid 3), not <c>IChamfer.Distance1</c>; and in API5 writing the radius on
/// an existing feature is not applied, so the API7 route here is not an "alternative" but the only
/// working one (see <c>Api5Session.UpdateFilletRadius</c>).</summary>
internal static class Api7Fillet
{
    /// <summary>The number of fillets in the API7 collection. null — not read (not "zero").</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Fillets.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Read fillet parameters typed, by API7 collection index.</summary>
    public static FilletReadDto? Read(IModelContainer container, int index)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return null;
            }

            // The inputs are read by the same call as the counter: a counter without references is
            // useless for addressing (a set edit goes by the composition of inputs, not their number),
            // and the difference between "not read" and "empty set" must be visible to the caller.
            var inputs = ReadBaseObjects(container, index);
            return new FilletReadDto(
                RadiusMm: Read(() => fillet.Radius1),
                Radius2Mm: Read(() => fillet.Radius2),
                Tangent: Read(() => fillet.Tangent),
                BuildingType: Text(() => fillet.BuildingType.ToString()),
                BaseObjectCount: fillet.BaseObjects is object[] array ? array.Length : null,
                BaseObjectReferences: inputs is null ? null : ReferencesOf(inputs),
                ReadRoutes: new[] { "API7 IFillet" });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Indices of fillets in the API7 collection that match the API5 feature by radius.
    /// Matching by number, not by name: F.8 MEASURED that a name given in API5 reads differently from
    /// API7, so a name is not an identifier.</summary>
    public static IReadOnlyList<int> FindIndexesByIdenticalRadius(IModelContainer container, double radiusMm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            if (Read(container, i)?.RadiusMm is double r && Math.Abs(r - radiusMm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>Write the radius into an EXISTING fillet via <c>IFillet</c> — the route that edits the
    /// radius, because the API5 write on an existing feature is not applied.</summary>
    /// <remarks>INVARIANT: only supplied fields are written — an unsupplied one keeps what is in the
    /// model; the order "write → Update() → RebuildModel()" is part of the contract, not style — without
    /// <c>Update()</c> the setters return success while the model stays as before (MEASURED on
    /// <c>IChamfer</c> by probe F.10 and on <c>IExtrusion.Sketch</c> by probe E). Returns (written,
    /// reason), not an exception: the caller must distinguish a KOMPAS refusal from an adapter crash, and
    /// "could not" must not look like "wrote".</remarks>
    public static (bool Written, string? Failure) TryWriteRadius(
        IModelContainer container,
        int index,
        double? radiusMm,
        double? radius2Mm,
        bool? tangent)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return (false, $"элемент коллекции Fillets[{index}] не отдаёт IFillet");
            }

            if (radiusMm is double r1)
            {
                fillet.Radius1 = r1;
            }

            if (radius2Mm is double r2)
            {
                fillet.Radius2 = r2;
            }

            if (tangent is bool t)
            {
                fillet.Tangent = t;
            }

            if (!fillet.Update())
            {
                return (false, "IFillet.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Indices of fillets whose SET of input references equals the given one. Feature
    /// identification by composition, not by name (F.8: a name differs between API5 and API7), not by
    /// index (issuance order is arbitrary) and not by radius (two fillets of one radius are
    /// indistinguishable — the H2.7 addressing experiment was run on exactly such a model).</summary>
    public static IReadOnlyList<int> FindIndexesByInputReferences(
        IModelContainer container,
        IReadOnlyList<int> references)
    {
        var found = new List<int>();
        if (Count(container) is not int count || references.Count == 0)
        {
            return found;
        }

        var wanted = references.OrderBy(x => x).ToArray();
        for (var i = 0; i < count; i++)
        {
            var inputs = ReadBaseObjects(container, i);
            if (inputs is null)
            {
                continue;
            }

            var actual = ReferencesOf(inputs).OrderBy(x => x).ToArray();
            if (actual.SequenceEqual(wanted))
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>The only fillet with the given input composition. Returns <c>null</c> both for zero and
    /// for several matches: "take the first one" would write the set into a foreign feature and silently
    /// spoil foreign geometry.</summary>
    public static int? FindIndexByInputReferences(
        IModelContainer container,
        IReadOnlyList<int> references)
    {
        var matches = FindIndexesByInputReferences(container, references);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Indices of fillets whose radius read as the given one — a fallback for when the input
    /// composition cannot be read (the API5 definition gives no edges on an existing feature).</summary>
    /// <remarks>LIMIT: this does not replace identification by composition — the radius does not
    /// distinguish two fillets of one radius, so the caller must reject an ambiguous answer: two
    /// candidates mean "nothing to identify with", not "take the first".</remarks>
    public static IReadOnlyList<int> FindIndexesByRadius(IModelContainer container, double radiusMm)
    {
        var found = new List<int>();
        if (Count(container) is not int count)
        {
            return found;
        }

        for (var i = 0; i < count; i++)
        {
            var read = Read(container, i);
            if (read?.RadiusMm is double r && Math.Abs(r - radiusMm) <= 1e-6)
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>The inputs of a live fillet — <c>IFillet.BaseObjects</c> as a list of objects fit to be
    /// presented back. Returns <c>null</c> when the member does not read and an empty list when the
    /// feature holds no inputs: "not read" and "zero" are different answers.</summary>
    /// <remarks>MEASURED by probe H-2 (step H2.2): elements are taken as <see cref="IModelObject"/>, not
    /// <c>ksEntity</c> — <c>BaseObjects</c> gives a <c>System.Object[]</c> of <c>System.__ComObject</c>
    /// that cast to <c>IModelObject</c> (4 of 4) and NOT to <c>ksEntity</c>; write exactly the objects
    /// the feature gave, since a reverse transfer into <c>ksEntity</c> loses the context in which the
    /// reference is meaningful.</remarks>
    public static IReadOnlyList<IModelObject>? ReadBaseObjects(IModelContainer container, int index)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return null;
            }

            if (fillet.BaseObjects is not object[] raw)
            {
                return null;
            }

            var inputs = new List<IModelObject>(raw.Length);
            foreach (var element in raw)
            {
                if (element is IModelObject modelObject)
                {
                    inputs.Add(modelObject);
                }
            }

            return inputs;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The inputs of EVERY live fillet — one list per feature, in <c>IModelContainer.Fillets</c>
    /// order. Needed where a feature must be IDENTIFIED by composition rather than read by index: a set
    /// can only be reduced on the fillet that itself holds the requested inputs.</summary>
    /// <remarks>INVARIANT: "did not read" is distinguishable from "no inputs" — an unreadable fillet
    /// gives <c>null</c> in the list while one holding zero inputs gives an empty list; mixing them would
    /// take "not read" for "the feature is empty" and write the set in the wrong place.</remarks>
    public static IReadOnlyList<IReadOnlyList<IModelObject>?> ReadBaseObjectsAll(IModelContainer container)
    {
        int count;
        try
        {
            count = container.Fillets.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Array.Empty<IReadOnlyList<IModelObject>?>();
        }

        var all = new List<IReadOnlyList<IModelObject>?>(count);
        for (var i = 0; i < count; i++)
        {
            all.Add(ReadBaseObjects(container, i));
        }

        return all;
    }

    /// <summary>Stable references of the inputs — <c>IModelObject.Reference</c> — measuring the feature's
    /// COMPOSITION, independent of collection order and re-read after a mutation.</summary>
    /// <remarks>MEASURED: reference bands are ADJACENT (a transferred body edge and the feature input
    /// differ by one, both of type <c>ksObjectEdge</c>, stable to re-reading). This is the ordinary
    /// API5/API7 duality (<c>ksEntity</c> and <c>IModelObject</c> are two COM objects for one edge), not
    /// an impassable boundary, so presenting body edges is allowed.
    /// History: docs/decisions/adapter-api7.md#fillet</remarks>
    public static IReadOnlyList<int> ReferencesOf(IReadOnlyList<IModelObject> inputs)
    {
        var refs = new List<int>(inputs.Count);
        foreach (var input in inputs)
        {
            try
            {
                refs.Add(input.Reference);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // An element without a readable reference is skipped: that is an answer, not a crash.
            }
        }

        return refs;
    }

    /// <summary>Write a NEW input set into an existing fillet via <c>IFillet.BaseObjects</c> — the route
    /// that edits the edge set, because the API5 definition route does not apply it.</summary>
    /// <remarks>MEASURED (<c>docs/acceptance/api7/fillet-base-objects.md</c>): the set is replaced WHOLE by
    /// one assignment (the objects presented are read FROM <c>BaseObjects</c> of the same feature); order
    /// is write → <c>IFillet.Update()</c> → rebuild — without <c>Update()</c> the setter returns success
    /// while the model stays as before. Returns (written, reason), not an exception: "could not" must not
    /// look like "wrote".
    /// History: docs/decisions/adapter-api7.md#fillet</remarks>
    public static (bool Written, string? Failure) TryWriteBaseObjects(
        IModelContainer container,
        int index,
        IReadOnlyList<IModelObject> inputs)
    {
        try
        {
            if (container.Fillets[index] is not IFillet fillet)
            {
                return (false, $"элемент коллекции Fillets[{index}] не отдаёт IFillet");
            }

            if (inputs.Count == 0)
            {
                return (false, "новый набор входов пуст: пустой набор — это удаление признака, а не правка набора");
            }

            // The array assigned holds the very elements the feature gave: a type substitution (e.g.
            // ksEntity) would lose the reference context and give a refusal indistinguishable from a
            // KOMPAS refusal.
            var payload = new object[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                payload[i] = inputs[i];
            }

            fillet.BaseObjects = payload;

            if (!fillet.Update())
            {
                return (false, "IFillet.Update() вернул false");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static double? Read(Func<double> value) => Safe(value);

    private static bool? Read(Func<bool> value) => Safe(value);

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return default;
        }
    }

    private static string? Text(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }
}

/// <summary>Boolean operations over bodies (SM-15). Route MEASURED on v24.</summary>
/// <remarks>Returns (feature, reason), not an exception: the caller must distinguish a KOMPAS refusal
/// from an adapter crash. A refusal is an ordinary outcome here — the kernel rejects disjoint bodies,
/// edge contact and point contact — and "could not build" must not look like "built".</remarks>
internal static class Api7SolidBoolean
{
    /// <summary>The operation kind for API7. Values come from <c>Kompas6Constants.ksBooleanType</c>, not
    /// a catalogue: the catalogue called union zero and was wrong, whereas the enum gives
    /// <c>ksIntersect = 1</c>, <c>ksDifference = 2</c>, <c>ksUnion = 3</c>.</summary>
    public static ksBooleanType ToKs(BooleanOperation operation) => operation switch
    {
        BooleanOperation.Union => ksBooleanType.ksUnion,
        BooleanOperation.Difference => ksBooleanType.ksDifference,
        BooleanOperation.Intersect => ksBooleanType.ksIntersect,
        _ => ksBooleanType.ksBooleanUnknown,
    };

    public static (IBoolean? Feature, string? Failure) TryCreate(
        IModelContainer container,
        IKompasAPIObject target7,
        object[] tools7,
        BooleanOperation operation,
        bool keepTools,
        string? name)
    {
        try
        {
            if (container.Booleans.Add() is not IBoolean boolean)
            {
                return (null, "Booleans.Add() не отдаёт IBoolean");
            }

            if (name is not null)
            {
                boolean.Name = name;
            }

            boolean.BaseObject = target7;
            boolean.ModifyObjects = tools7;
            boolean.BooleanType = ToKs(operation);

            // Copying the target is not supported: that is a separate mode
            // SM-15.union.mode_save_base_copy with priority next, outside the mandatory release scope.
            // The field is set explicitly so behaviour does not depend on a default.
            boolean.SaveCopyBaseObject = false;
            boolean.SaveCopyModifyObjects = keepTools;

            var updated = boolean.Update();
            return updated
                ? (boolean, null)
                : (null, "IBoolean.Update() вернул false — ядро отвергло операцию");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>The number of boolean features in the collection. null — not read (not "zero").</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Booleans.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Edit the KIND of an existing boolean operation: overwrite <c>IBoolean.BooleanType</c> and
    /// call <c>Update()</c>. The operands and the tool-keep policy are NOT overwritten (route unmeasured).</summary>
    /// <remarks>MEASURED (<c>docs/acceptance/api7/boolean-ops.json</c>): <c>Update()</c> is mandatory in
    /// the pair — writing <c>ksDifference</c> without it but with a rebuild leaves the geometry unchanged.
    /// Writing <c>ksBooleanUnknown</c> NORMALISES to union, so a client writing <c>0</c> gets a union.
    /// LIMIT: changing the OPERANDS of an existing boolean is not measured.
    /// History: docs/decisions/adapter-api7.md#boolean</remarks>
    public static (bool Written, string? Failure) TryWriteOperation(
        IModelContainer container,
        int index,
        BooleanOperation operation)
    {
        try
        {
            if (container.Booleans[index] is not IBoolean boolean)
            {
                return (false, $"Booleans[{index}] не отдаёт IBoolean");
            }

            boolean.BooleanType = ToKs(operation);
            return boolean.Update()
                ? (true, null)
                : (false, "IBoolean.Update() вернул false — ядро отвергло правку вида операции");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IndexOutOfRangeException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>Operation kind read back from an existing feature. <c>null</c> — not read.</summary>
    public static BooleanOperation? ReadOperation(IModelContainer container, int index)
    {
        try
        {
            if (container.Booleans[index] is not IBoolean boolean)
            {
                return null;
            }

            return boolean.BooleanType switch
            {
                ksBooleanType.ksUnion => BooleanOperation.Union,
                ksBooleanType.ksDifference => BooleanOperation.Difference,
                ksBooleanType.ksIntersect => BooleanOperation.Intersect,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>API7 auxiliary planes (SM-16). Route MEASURED: <c>Planes3D.Add(o3d_plane3Points)</c> →
/// <c>IPlane3DBy3Points</c> with three MODEL points.</summary>
/// <remarks>MEASURED: the normal of the built plane equals <c>(P2−P1)×(P3−P1)</c> — swapping two points
/// inverts the normal without breaking the build. So the three points are built from an orthonormal
/// basis of the given normal: the side sign is a choice of side, not a detail.
/// History: docs/decisions/adapter-api7.md#solid-plane</remarks>
internal static class Api7SolidPlane
{
    public static (IPlane3D? Plane, double[]? UnitNormal, string? Failure) TryCreateByPointNormal(
        IModelContainer container,
        IReadOnlyList<double> pointMm,
        IReadOnlyList<double> normalMm,
        string? name)
    {
        double[] unit;
        (double[] P1, double[] P2, double[] P3) points;
        try
        {
            var three = PlaneBasis.ThreePoints(pointMm, normalMm);
            points = (three.P1, three.P2, three.P3);
            unit = three.UnitNormal;
        }
        catch (ArgumentException ex)
        {
            return (null, null, ex.Message);
        }

        try
        {
            if (container is not IAuxiliaryGeomContainer auxiliary || auxiliary.Planes3D is not { } planes)
            {
                return (null, null, "контейнер модели не отвечает IAuxiliaryGeomContainer.Planes3D");
            }

            var first = MakePoint(container, points.P1);
            var second = MakePoint(container, points.P2);
            var third = MakePoint(container, points.P3);
            if (first is null || second is null || third is null)
            {
                return (null, null, "точки плоскости не созданы (Points3D недоступны)");
            }

            if (planes.Add(ksObj3dTypeEnum.o3d_plane3Points) is not IPlane3D plane)
            {
                return (null, null, "Planes3D.Add(o3d_plane3Points) не отдал IPlane3D");
            }

            if (plane is not IPlane3DBy3Points byPoints)
            {
                return (null, null, "созданная плоскость не отвечает IPlane3DBy3Points");
            }

            if (name is not null)
            {
                plane.Name = name;
            }

            byPoints.Point1 = first;
            byPoints.Point2 = second;
            byPoints.Point3 = third;

            var updated = plane.Update();
            return updated
                ? (plane, unit, null)
                : (null, unit, "IPlane3D.Update() вернул false — плоскость не построена");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, unit, Describe(ex));
        }
    }

    /// <summary>Transfer an existing document plane addressed by reference into API7.</summary>
    public static (IPlane3D? Plane, string? Failure) TryTransfer(object source, Api7Bridge bridge)
    {
        try
        {
            if (bridge.TransferTo7(source) is not IPlane3D plane)
            {
                return (null, "объект по ссылке не отвечает IPlane3D");
            }

            return (plane, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    private static IPoint3D? MakePoint(IModelContainer container, double[] coordinates)
    {
        try
        {
            if (container.Points3D is not { } points || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = coordinates[0];
            point.Y = coordinates[1];
            point.Z = coordinates[2];
            point.Update();
            return point;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>Splitting a body by a plane (SM-16). Route MEASURED on v24, step SP.2.</summary>
/// <remarks>INVARIANT: <c>ISplitSolid</c> has ONE substantive member — <c>CutObjects</c>. There is no
/// separate "set of parts to keep" and none is needed: splitting keeps all parts by construction
/// (MEASURED: the parts sum to the original body). That measurement — not the member found — cleared the
/// OQ-A18 blocker.
/// History: docs/decisions/adapter-api7.md#solid-split</remarks>
internal static class Api7SolidSplit
{
    public static (ISplitSolid? Feature, string? Failure) TryCreate(
        IModelContainer container,
        object[] cutObjects,
        string? name)
    {
        try
        {
            if (container.SplitSolids.Add() is not ISplitSolid split)
            {
                return (null, "SplitSolids.Add() не отдаёт ISplitSolid");
            }

            if (name is not null)
            {
                split.Name = name;
            }

            split.CutObjects = cutObjects;
            var updated = split.Update();
            return updated
                ? (split, null)
                : (null, "ISplitSolid.Update() вернул false — ядро отвергло разделение");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.SplitSolids.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Overwrite the support of an EXISTING split feature — the edit route (action <c>edit</c>).</summary>
    /// <remarks>MEASURED with a negative control, because the "obvious" route turned out wrong: the support
    /// is read back from the live feature (<c>CutObjects</c> returns one object, answers <c>IPlane3DBy3Points</c>)
    /// and its THREE BUILD POINTS are moved; writing a just-created plane into <c>CutObjects</c> with
    /// <c>Update() = true</c> changes nothing — <c>Update() = true</c> means "accepted", not "applied".
    /// Hence editing the support means moving the build points of the support ITSELF, not substituting
    /// another plane (substitution works only on CREATE); the reference is re-read from the LIVE model.
    /// History: docs/decisions/adapter-api7.md#solid-split</remarks>
    public static (bool Moved, string? Failure) TryMoveSupport(
        IModelContainer container,
        int index,
        (double[] P1, double[] P2, double[] P3) points)
    {
        try
        {
            if (container.SplitSolids[index] is not ISplitSolid split)
            {
                return (false, $"элемент коллекции SplitSolids[{index}] не отдаёт ISplitSolid");
            }

            if (Api7PlaneSupport.FirstOf(split.CutObjects) is not IPlane3D support)
            {
                return (false, "опора признака разделения не отвечает IPlane3D: правка опоры идёт "
                    + "переносом точек её построения, а у этого объекта их нет");
            }

            return Api7PlaneSupport.MovePoints(support, points);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>Supports of SM-16 features on EDIT: reading the support from a live feature and moving its
/// build points.</summary>
/// <remarks>Kept separate because both families — split and cut — are edited by ONE route, and a second
/// copy of the same rules would drift from the first at the first edit.
/// History: docs/decisions/adapter-api7.md#plane-support</remarks>
internal static class Api7PlaneSupport
{
    /// <summary>First element of a read-back <c>CutObjects</c> set. Interop returns it sometimes as an
    /// array and sometimes as a single object; assuming one of the two would be guessing at the type —
    /// MEASURED (step SP.9) that with one support it comes back as a single object, not an array.</summary>
    public static object? FirstOf(object? readBack) => readBack switch
    {
        Array array when array.Length > 0 => array.GetValue(0),
        Array => null,
        null => null,
        _ => readBack,
    };

    /// <summary>Move the three build points of a support to the given coordinates, then rebuild the
    /// support.</summary>
    /// <remarks>INVARIANT: the points are addressed IN ORDER (<c>Point1..Point3</c>) because the order is
    /// what defines the normal (<c>(p2−p1)×(p3−p1)</c>) — MEASURED by step SP.5: swapping two points
    /// inverts the normal. So the move goes to the same three points, not "to the nearest".
    /// History: docs/decisions/adapter-api7.md#plane-support</remarks>
    public static (bool Moved, string? Failure) MovePoints(
        IPlane3D support,
        (double[] P1, double[] P2, double[] P3) points)
    {
        if (support is not IPlane3DBy3Points byPoints)
        {
            return (false, "опора не отвечает IPlane3DBy3Points: правка идёт переносом трёх точек "
                + "построения, а у этой опоры их нет");
        }

        var wanted = new[] { points.P1, points.P2, points.P3 };
        var actual = new[] { byPoints.Point1, byPoints.Point2, byPoints.Point3 };
        for (var i = 0; i < wanted.Length; i++)
        {
            if (wanted[i].Length != 3)
            {
                return (false, $"точка построения #{i + 1} задана {wanted[i].Length} числами вместо трёх");
            }

            if (actual[i] is not IPoint3D point)
            {
                return (false, $"точка построения опоры #{i + 1} не отвечает IPoint3D");
            }

            point.X = wanted[i][0];
            point.Y = wanted[i][1];
            point.Z = wanted[i][2];
            point.Update();
        }

        return support.Update()
            ? (true, null)
            : (false, "IPlane3D.Update() вернул false — опора не перестроена");
    }
}

/// <summary>Cutting a body to one side of a plane (SM-16). Route MEASURED on v24, step SP.7.</summary>
/// <remarks>MEASURED sign mapping: <c>Direction = true</c> keeps the side along the normal, <c>false</c>
/// the opposite one. So "keep the positive side" and "Direction = true" are the same thing.
/// History: docs/decisions/adapter-api7.md#solid-cut</remarks>
internal static class Api7SolidCut
{
    /// <summary>Create a cut feature by a plane, aimed at a CHOSEN body.</summary>
    /// <remarks>INVARIANT: the application scope is not decoration. The installed v24 help
    /// (<c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>) states the default plainly: «По умолчанию
    /// область применения операции Сечение — Все объекты», and for a flat cutting object that includes
    /// both what the plane crosses and what lies ENTIRELY on the cut side. So a call without setting the
    /// scope removes material from unrelated bodies — product behaviour, not a failure.
    /// MEASURED (<c>docs/acceptance/api7/cut-area.json</c>): <c>ICut</c> declares four scope members
    /// (<c>ChooseType</c>, <c>ChoosePartsType</c>, <c>ChooseBodies</c>, <c>ChooseParts</c>); the accepted
    /// form is <c>ChooseType = ksChBodies</c>, <c>ChoosePartsType = ksChManualEditing</c>,
    /// <c>ChooseBodies = object[] { body transferred to API7 }</c>, and it is TARGETED (a different body
    /// gives a different result). Without the scope an unrelated body disappears; with it the result is
    /// targeted and survives <c>save → close → open</c>.
    /// INVARIANT: the values are read back BEFORE <c>Update()</c>: if the product did not accept the
    /// scope, the feature is not created at all.
    /// History: docs/decisions/adapter-api7.md#solid-cut</remarks>
    public static (ICut? Feature, string? Failure) TryCreateByPlane(
        IModelContainer container,
        IPlane3D plane,
        bool keepPositiveSide,
        string? name,
        IKompasAPIObject? targetBody)
    {
        try
        {
            if (container.Cuts.Add() is not ICut cut)
            {
                return (null, "Cuts.Add() не отдаёт ICut");
            }

            if (name is not null)
            {
                cut.Name = name;
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = plane;
            cut.Direction = keepPositiveSide;

            if (targetBody is not null)
            {
                cut.ChooseType = ksChooseType.ksChBodies;
                cut.ChoosePartsType = ksChoosePartsType.ksChManualEditing;
                cut.ChooseBodies = new[] { targetBody };

                var readBack = ReadBodyChoice(cut);
                if (readBack.Failure is not null)
                {
                    return (null, "область применения не подтверждена до Update(): " + readBack.Failure);
                }
            }

            var updated = cut.Update();
            return updated
                ? (cut, null)
                : (null, "ICut.Update() вернул false — ядро отвергло отсечение");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Read back the application scope of a live feature — BEFORE mutation. A missing body in
    /// the list and a member refusal are different answers, and both differ from "read and matched".</summary>
    public static (string? Type, int? Bodies, string? Failure) ReadBodyChoice(ICut cut)
    {
        try
        {
            var type = cut.ChooseType;
            var bodies = cut.ChooseBodies switch
            {
                null => 0,
                Array array => array.Length,
                _ => 1,
            };

            if (type != ksChooseType.ksChBodies)
            {
                return (type.ToString(), bodies,
                    $"продукт прочитал ChooseType={type}, а запрошено {ksChooseType.ksChBodies}");
            }

            return (type.ToString(), bodies, bodies < 1
                ? "список выбранных тел пуст: адресность не подтверждена"
                : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Cuts.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Overwrite the support and side of an EXISTING cut feature — the edit route (action <c>edit</c>).</summary>
    /// <remarks>MEASURED: the support (<c>ICut.CutObject</c>) is read back, answers <c>IPlane3DBy3Points</c>
    /// and is edited by moving its three build points; the side by changing ONLY <c>Direction</c>. Order:
    /// "support, then side, then <c>Update()</c>". <c>BuildingType</c> and <c>CutObject</c> are NOT overwritten.
    /// Scope on edit: moving the support PRESERVES the targeting and survives <c>save → close → open</c>, so
    /// <paramref name="targetBody"/> is optional — without it the scope is read back BEFORE <c>Update()</c> and
    /// an untargeted feature is rejected; reassignment is never done without reading back.
    /// History: docs/decisions/adapter-api7.md#solid-cut</remarks>
    public static (bool Updated, string? Failure) TryMoveSupportAndSide(
        IModelContainer container,
        int index,
        (double[] P1, double[] P2, double[] P3) points,
        bool keepPositiveSide,
        IKompasAPIObject? targetBody = null)
    {
        try
        {
            if (container.Cuts[index] is not ICut cut)
            {
                return (false, $"элемент коллекции Cuts[{index}] не отдаёт ICut");
            }

            if (cut.CutObject is not IPlane3D support)
            {
                return (false, "опора признака отсечения не отвечает IPlane3D: правка опоры идёт "
                    + "переносом точек её построения, а у этого объекта их нет");
            }

            if (targetBody is not null)
            {
                cut.ChooseType = ksChooseType.ksChBodies;
                cut.ChoosePartsType = ksChoosePartsType.ksChManualEditing;
                cut.ChooseBodies = new[] { targetBody };

                var assigned = ReadBodyChoice(cut);
                if (assigned.Failure is not null)
                {
                    return (false, "область применения не подтверждена до Update(): " + assigned.Failure);
                }
            }
            else
            {
                var current = ReadBodyChoice(cut);
                if (current.Failure is not null)
                {
                    return (false, "признак не адресован: правка опоры сняла бы материал у посторонних "
                        + "тел, потому что умолчание области применения — «Все объекты» (справка "
                        + "rezultat_oper_v_zavisimosti_ot_s_o.html). Чтение области применения до "
                        + "мутации: " + current.Failure
                        + ". Передайте target_body_ref, чтобы назначить область явно.");
                }
            }

            var moved = Api7PlaneSupport.MovePoints(support, points);
            if (!moved.Moved)
            {
                return moved;
            }

            cut.Direction = keepPositiveSide;
            return cut.Update()
                ? (true, null)
                : (false, "ICut.Update() вернул false — ядро отвергло правку отсечения");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>Body reposition (SM-17): placement through the documented parametric route.</summary>
/// <remarks>MEASURED: the matrix view does NOT survive a reopen; the 12-number routes and
/// <c>SetDisplacementByAxis</c> return <c>Update() = true</c> yet do not move the body — not proof.
/// INVARIANT: <c>OrientationType = ksEulerCorners</c> with <c>LocalCSEulerParam.PrecessionAngle</c>,
/// <c>NutationAngle</c>, <c>RotationAngle</c>; and <c>ParameterType = ksPDisplace</c> with
/// <c>IPoint3DParamDisplace.DX/DY/DZ</c>, in that order — the reverse yields a FOREIGN mode and writes
/// are silently ignored. Units are DEGREES; the triple order is <c>PNR</c> (<see cref="EulerOrientation"/>).
/// History: docs/decisions/adapter-api7.md#reposition</remarks>
internal static class Api7SolidReposition
{
    /// <summary>Parametric view of the placement, read from the document AS IS: no substitutions, no
    /// inference from a matrix, no default values.</summary>
    /// <param name="OrientationType">Orientation mode read from the document (not assumed).</param>
    /// <param name="ParameterType">Point parameter type read from the document.</param>
    /// <param name="AnglesDeg">Triple <c>(precession, nutation, rotation)</c> or <c>null</c> if the mode
    /// interface is not confirmed.</param>
    /// <param name="DisplacementMm">Displacement <c>(DX, DY, DZ)</c> or <c>null</c> if the point
    /// parameters do not confirm the displacement interface.</param>
    internal sealed record PlacementReading(
        int OrientationType,
        int ParameterType,
        double[]? AnglesDeg,
        double[]? DisplacementMm)
    {
        /// <summary>The feature was written in the documented Euler-angle mode. False means the
        /// placement was written by a FOREIGN route (a matrix) and has no parametric view.</summary>
        public bool IsEuler => OrientationType == (int)ksOrientationTypeEnum.ksEulerCorners;

        /// <summary>The translation was written as a documented displacement, not as point coordinates.</summary>
        public bool HasDisplacement => ParameterType == (int)ksPoint3DTypeEnum.ksPDisplace;
    }

    public static (IBodyReposition? Feature, string? Failure) TryCreate(
        IModelContainer container,
        IKompasAPIObject body7,
        double[] matrix16,
        string? name)
    {
        if (matrix16.Length != RepositionMatrix.Size)
        {
            return (null, $"матрица положения обязана содержать {RepositionMatrix.Size} чисел, а содержит {matrix16.Length}");
        }

        try
        {
            if (container.BodyRepositions.Add() is not IBodyReposition reposition)
            {
                return (null, "BodyRepositions.Add() не отдаёт IBodyReposition");
            }

            if (name is not null)
            {
                reposition.Name = name;
            }

            reposition.RepositionBody = body7;

            var failure = WritePlacement(reposition.Position, matrix16);
            if (failure is not null)
            {
                return (null, failure);
            }

            var updated = reposition.Update();
            return updated
                ? (reposition, null)
                : (null, "IBodyReposition.Update() вернул false — ядро отвергло преобразование");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.BodyRepositions.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Write the placement into an EXISTING feature — the edit route. MEASURED (step RP.6):
    /// re-writing the same vector leaves the placement unchanged, and returning the vector to zero brings
    /// the body home — i.e. the parameter applies to the ORIGINAL inputs, not to the current placement.</summary>
    public static (bool Written, string? Failure) TryWrite(
        IModelContainer container,
        int index,
        double[] matrix16)
    {
        try
        {
            if (container.BodyRepositions[index] is not IBodyReposition reposition)
            {
                return (false, $"элемент коллекции BodyRepositions[{index}] не отдаёт IBodyReposition");
            }

            var failure = WritePlacement(reposition.Position, matrix16);
            if (failure is not null)
            {
                return (false, failure);
            }

            var updated = reposition.Update();
            return updated ? (true, null) : (false, "IBodyReposition.Update() вернул false");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>Write the placement through the parametric route: orientation as an Euler-angle triple,
    /// translation as a displacement. <c>null</c> — written; otherwise the reason for refusal.</summary>
    /// <remarks>INVARIANT: the matrix is decomposed, not substituted — rotation from <c>matrix[0…10]</c>
    /// via <see cref="EulerOrientation.AnglesFromRotation"/>, translation from <c>matrix[12…14]</c>.
    /// The translation is ALWAYS written, even zero: skipping it would leave the feature with a FOREIGN
    /// point parameter type (<c>ksPParamCoord</c>), so the read would depend on session history.
    /// History: docs/decisions/adapter-api7.md#reposition</remarks>
    private static string? WritePlacement(ILocalCoordinateSystem position, double[] matrix16)
    {
        position.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
        if (position.LocalCSParameters is not ILocalCSEulerParam euler)
        {
            return "LocalCSParameters не подтверждает ILocalCSEulerParam при "
                + "OrientationType = ksEulerCorners — документированный режим углов недостижим";
        }

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(matrix16);
        euler.PrecessionAngle = precession;
        euler.NutationAngle = nutation;
        euler.RotationAngle = rotation;

        position.ParameterType = ksPoint3DTypeEnum.ksPDisplace;
        if (position.Parameters is not IPoint3DParamDisplace displace)
        {
            return "Parameters не подтверждает IPoint3DParamDisplace при "
                + "ParameterType = ksPDisplace — документированный маршрут смещения недостижим";
        }

        displace.DX = matrix16[12];
        displace.DY = matrix16[13];
        displace.DZ = matrix16[14];
        return null;
    }

    /// <summary>Read the placement through the PARAMETRIC route — as is, without writing.</summary>
    /// <remarks>INVARIANT: nothing is written before the read — a repeated write would restore a correct
    /// read for one step and mask the defect. Only <c>OrientationType</c>, <c>LocalCSParameters</c>,
    /// <c>ParameterType</c> and <c>Parameters</c> are read.
    /// The interfaces are taken WITHOUT forcing the mode: <c>LocalCSParameters</c> returns the CURRENT
    /// mode, so a feature written by a matrix gives the interface of a DIFFERENT mode; forcing the mode
    /// here would create a parametric view where there is none and pass it off as read.
    /// History: docs/decisions/adapter-api7.md#reposition</remarks>
    public static (PlacementReading? Reading, string? Failure) ReadPlacement(
        IModelContainer container,
        int index)
    {
        try
        {
            if (container.BodyRepositions[index] is not IBodyReposition reposition)
            {
                return (null, $"элемент коллекции BodyRepositions[{index}] не отдаёт IBodyReposition");
            }

            return ReadPlacement(reposition);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Same for an already obtained feature — the create route reads it before the build.</summary>
    public static (PlacementReading? Reading, string? Failure) ReadPlacement(IBodyReposition reposition)
    {
        try
        {
            var position = reposition.Position;
            var orientationType = (int)position.OrientationType;
            var parameterType = (int)position.ParameterType;

            double[]? angles = position.LocalCSParameters is ILocalCSEulerParam euler
                ? new[] { euler.PrecessionAngle, euler.NutationAngle, euler.RotationAngle }
                : null;
            double[]? displacement = position.Parameters is IPoint3DParamDisplace displace
                ? new[] { displace.DX, displace.DY, displace.DZ }
                : null;

            return (new PlacementReading(orientationType, parameterType, angles, displacement), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}" +
        (ComHResult.From(ex) is int code ? $" [{ComHResult.Name(code)}]" : string.Empty);
}

/// <summary>Reading a loft from the API7 collection — the route the feature was created by. No value is
/// taken from the create response: the collection is read afresh.</summary>
/// <remarks>MEASURED: <c>IModelContainer.Lofts</c> answers <c>KompasAPI7.LoftsClass</c>; the element by
/// index returns <c>KompasAPI7.LoftClass</c>, from which <c>Sketchs</c>, <c>Closed</c>,
/// <c>CouplingsCount</c> and <c>BuildingType</c> are read.
/// History: docs/decisions/adapter-api7.md#loft</remarks>
internal static class Api7Loft
{
    /// <summary>Number of lofts. <c>null</c> — "not read", not zero.</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Lofts?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Element by index. <c>null</c> — "not read", not "no parameters".</summary>
    public static ILoft? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Lofts[index] as ILoft;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Number of sections in the feature. <c>null</c> — "not read".</summary>
    public static int? SectionCount(ILoft loft)
    {
        try
        {
            return loft.Sketchs is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Section correspondence chains — <c>CouplingsCount</c>. <c>null</c> — "not read".</summary>
    public static int? CouplingsCount(ILoft loft)
    {
        try
        {
            return loft.CouplingsCount;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether the junction trajectory is closed — <c>ILoft.Closed</c>. <c>null</c> — "not read".</summary>
    public static bool? Closed(ILoft loft)
    {
        try
        {
            return loft.Closed;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Build type at the extreme section — <c>ILoft.BuildingType(BeginSection)</c>. An INDEXED
    /// property: in the C# interop surface the index is declared <c>bool</c>, whereas reflection over
    /// <c>get_BuildingType</c> finds <c>short</c> — two different halves of one member, both MEASURED
    /// (B5.9, B5.11). Which index maps to which end is measured, not inferred from the name.</summary>
    public static int? BuildingType(ILoft loft, bool beginSection)
    {
        try
        {
            return Convert.ToInt32(loft.BuildingType[beginSection]);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }
}

/// <summary>Reading a shell from the API7 collection — the second half of the same setup as reading
/// from the API5 definition. Needed so that "read" is confirmed by two independent routes.</summary>
/// <remarks>MEASURED: <c>IModelContainer.Shells</c> answers <c>KompasAPI7.ShellsClass</c>; the element
/// returns <c>IShell</c> with <c>Thickness</c>, <c>ThinType</c> and <c>DeletedFaces</c> readable, and
/// the same three values are read from the API5 definition.
/// History: docs/decisions/adapter-api7.md#shell</remarks>
internal static class Api7Shell
{
    /// <summary>Number of shells. <c>null</c> — "not read".</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Shells?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Shell by index. <c>null</c> — "not read".</summary>
    public static IShell? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Shells[index] as IShell;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Wall thickness. <c>null</c> — "not read".</summary>
    public static double? Thickness(IShell shell)
    {
        try
        {
            return shell.Thickness;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Thickness direction. In COM this is <c>c_long</c>; in interop it is declared
    /// <c>ksDirectionTypeEnum</c> — wrapper typing, not a fact about the product. The value is returned
    /// as a NUMBER so as not to depend on the enum name.</summary>
    public static int? ThinType(IShell shell)
    {
        try
        {
            return Convert.ToInt32(shell.ThinType);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Number of removed faces — <c>DeletedFaces</c>. <c>null</c> — "not read".</summary>
    public static int? DeletedFaceCount(IShell shell)
    {
        try
        {
            return shell.DeletedFaces is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}

/// <summary>Reading a kinematic operation from the API7 collection. Needed for ONE member that API5
/// lacks entirely: <c>IEvolution.OperationResult</c> — a documented answer about the operation kind
/// (<c>ksOperationNewBody</c> etc.).</summary>
/// <remarks>MEASURED: <c>IModelContainer.Evolutions</c> answers <c>KompasAPI7.EvolutionsClass</c> and
/// <c>OperationResult</c> reads as <c>ksOperationNewBody</c> — a new body, as declared by
/// <c>SM-04.base.single_profile_flat_path</c>.
/// History: docs/decisions/adapter-api7.md#evolution</remarks>
internal static class Api7Evolution
{
    /// <summary>Number of kinematic operations. <c>null</c> — "not read", not zero.</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.Evolutions?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Element by index. <c>null</c> — "not read".</summary>
    public static IEvolution? Read(IModelContainer container, int index)
    {
        try
        {
            return container.Evolutions[index] as IEvolution;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary><c>IEvolution.OperationResult</c>. <c>null</c> — "not read", not zero: zero here would
    /// mean a specific operation kind that nobody has measured.</summary>
    public static int? OperationResult(IModelContainer container, int index)
    {
        try
        {
            return Read(container, index) is { } evolution
                ? Convert.ToInt32(evolution.OperationResult)
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}
