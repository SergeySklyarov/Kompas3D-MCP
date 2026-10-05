using System.Runtime.InteropServices;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>A row of an auxiliary-geometry enumeration. Fields that could not be read stay
/// <c>null</c> and are named in <c>Notes</c>: an empty field means "not read", not zero.</summary>
internal sealed record AuxGeomRow(
    string Kind,
    int Index,
    string? Name,
    string? SubKind,
    double[]? PointMm,
    double[]? DirectionMm,
    double? AngleDeg,
    double? OffsetMm,
    bool? Direction,
    string? BaseName,
    string? LineName,
    IReadOnlyList<string> Notes);

/// <summary>A part's auxiliary geometry as MODEL OBJECTS: planes, axes, points.</summary>
/// <remarks>DOC: the route is from the official v24 help, not a guess (step 0 of
/// <c>DEPENDENCIES_PRODUCT_ROUTES_DEVELOPER_PROMPT.md</c>, report
/// <c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c>):
/// <c>IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D</c>
/// (<c>ksapi_iauxiliarygeomcontainer_getplanes3d.html</c>, <c>…getaxes3d.html</c>),
/// <c>IPlanes3D.Add(ksObj3dTypeEnum)</c> (<c>ksapi_iplanes3d_add.html</c>),
/// <c>IAxes3D.Add(ksObj3dTypeEnum)</c> (<c>ksapi_iaxes3d_add.html</c>),
/// <c>IModelContainer.GetPoints3D</c> (<c>ksapi_imodelcontainer_getpoints3d.html</c>),
/// <c>IPoints3D.Add</c> (<c>ksapi_ipoints3d_add.html</c>), <c>IPlane3DByAngle</c> /
/// <c>IPlane3DByOffset</c>, <c>IAxis3DBy2Points</c> / <c>IAxis3DByConeface</c> / <c>IAxis3DByEdge</c>,
/// <c>IPoint3D</c>. Object types come from the official <c>obj3dtype.html</c> table:
/// <c>o3d_planeAngle</c> = 15 → <c>IPlane3DByAngle</c>, <c>o3d_planeOffset</c> = 14 →
/// <c>IPlane3DByOffset</c>, <c>o3d_axis2Points</c> = 10, <c>o3d_axisConeFace</c> = 11,
/// <c>o3d_axisEdge</c> = 12, <c>o3d_point3D</c> = 70.
/// MEASURED: planes and axes live on a DIFFERENT interface than points — <c>Planes3D</c>/<c>Axes3D</c>
/// are declared on <c>IAuxiliaryGeomContainer</c> (IID <c>{950FEBE2-F916-4E77-A37D-B061E5C22FA8}</c>),
/// while <c>Points3D</c> is on <c>IModelContainer</c>; a plain cast of the container to
/// <c>IAuxiliaryGeomContainer</c> gives <c>null</c>, and only a QI on the live part object works
/// (R.13; see also <c>Api7Bridge.TryBuildAxisBy2Points</c>).
/// INVARIANT: no value is derived from a collection index or guessed from geometry — an address is
/// only a reference issued by the enumeration; standard planes (<c>o3d_planeXOY/XOZ/YOZ</c>) are
/// found by object TYPE, not position, and their absence is a refusal, not a substitute.
/// History: docs/decisions/adapter-api7.md#aux-geometry</remarks>
internal static class Api7AuxGeometry
{
    /// <summary>Read wrapper: a COM failure yields <c>null</c> ("not read"), not an exception.</summary>
    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return default;
        }
    }

    private static double? SafeD(Func<double> read) => Safe(read);

    private static bool? SafeB(Func<bool> read) => Safe(read);

    private static int? SafeI(Func<int> read) => Safe(read);

    /// <summary>Read a string. Also accepts explicitly nullable expressions (<c>obj?.Name</c>): the COM
    /// property is declared non-nullable, but the object may be null, and that is not the caller's
    /// error.</summary>
    private static string? SafeS(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>API7 containers of the transferred part. Both are returned: <c>IModelContainer</c> for
    /// points, <c>IAuxiliaryGeomContainer</c> for planes and axes. A refusal reason is named.</summary>
    public static (IModelContainer? Model, IAuxiliaryGeomContainer? Auxiliary, string? Failure)
        Containers(Api7Bridge bridge, object part)
    {
        try
        {
            if (bridge.TransferTo7(part) is not IModelObject part7)
            {
                return (null, null, "деталь не переносится в API7 как IModelObject");
            }

            if (part7 is not IModelContainer model)
            {
                return (null, null, "перенесённая деталь не отвечает QI(IModelContainer)");
            }

            if (part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                return (null, null,
                    "перенесённая деталь не отвечает QI(IAuxiliaryGeomContainer) " +
                    "(IID {950FEBE2-F916-4E77-A37D-B061E5C22FA8}) — плоскости и оси недостижимы");
            }

            return (model, auxiliary, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null, $"перенос детали в API7 бросил {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- points

    /// <summary>A model point by coordinates. Creation is verified: "accepted an array of three
    /// numbers" without a check would give a point at the origin.</summary>
    public static (IPoint3D? Point, string? Failure) CreatePointByCoordinates(
        IModelContainer container, double x, double y, double z, string? name = null)
    {
        try
        {
            if (container.Points3D is not { } points)
            {
                return (null, "IModelContainer.Points3D → null: коллекция точек недостижима");
            }

            if (points.Add() is not IPoint3D point)
            {
                return (null, "IPoints3D.Add() не отдал IPoint3D");
            }

            // The NAME is set BEFORE Update(), as for planes and axes. MEASURED 21.09.2026
            // (publish-deproutes-20260921-d): without the assignment the model returned an
            // auto-generated name instead of the requested "PROBE-point", and row DEP.DPT.02.read
            // looking a point up by name found 0 objects. The setter exists in the shipped assembly:
            // IPoint3D.set_Name(String) (IID {D71AEDBE-01D4-4C7D-96DC-94981F2A1C37}).
            if (name is { Length: > 0 })
            {
                point.Name = name;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            if (SafeB(point.Update) != true)
            {
                return (null, "IPoint3D.Update() не подтвердил создание точки");
            }

            return (point, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание точки бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A point by displacement from a reference vertex: <c>IPoint3D.ParameterType = ksPDisplace</c>
    /// with parameters via <c>IPoint3DParamDisplace</c> (<c>ksapi_ipoint3dparamdisplace.html</c>,
    /// <c>…setassociationvertex.html</c>). INVARIANT: the write order is mandatory — construction
    /// method and reference first, offsets only after, since a point without a reference has no
    /// origin.</summary>
    public static (IPoint3D? Point, string? Failure) CreatePointByDisplace(
        IModelContainer container, IModelObject associationVertex, double dx, double dy, double dz)
    {
        try
        {
            if (container.Points3D is not { } points)
            {
                return (null, "IModelContainer.Points3D → null: коллекция точек недостижима");
            }

            if (points.Add() is not IPoint3D point)
            {
                return (null, "IPoints3D.Add() не отдал IPoint3D");
            }

            point.ParameterType = ksPoint3DTypeEnum.ksPDisplace;
            // MEASURED: IPoint3D.AssociationObject has NO setter in the interop — only the method
            // SetAssociationObject(IModelObject) — so a property assignment would not compile.
            point.SetAssociationObject(associationVertex);

            if (point.Parameters is not IPoint3DParamDisplace parameters)
            {
                return (null,
                    "IPoint3D.Parameters не отвечает IPoint3DParamDisplace при ksPDisplace — " +
                    "параметры смещения недостижимы");
            }

            parameters.DX = dx;
            parameters.DY = dy;
            parameters.DZ = dz;
            if (SafeB(point.Update) != true)
            {
                return (null, "IPoint3D.Update() не подтвердил создание точки смещением");
            }

            return (point, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание точки смещением бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Read a point: coordinates, construction method and reference object.</summary>
    public static (double[]? Coordinates, string? ParameterType, string? AssociationName,
        IReadOnlyList<string> Notes) ReadPoint(IPoint3D point)
    {
        var notes = new List<string>();
        var x = SafeD(() => point.X);
        var y = SafeD(() => point.Y);
        var z = SafeD(() => point.Z);
        double[]? coordinates = x is not null && y is not null && z is not null
            ? [x.Value, y.Value, z.Value]
            : null;
        if (coordinates is null)
        {
            notes.Add("координаты точки не прочитались");
        }

        var parameterType = Safe(() => point.ParameterType.ToString());
        var association = Safe(() => point.AssociationObject?.Name);
        if (association is null)
        {
            notes.Add("опорный объект точки не прочитан");
        }

        return (coordinates, parameterType, association, notes);
    }

    // ----------------------------------------------------------------- planes

    /// <summary>An offset plane: <c>o3d_planeOffset</c> → <c>IPlane3DByOffset</c>. DOC: <c>BasePlane</c>
    /// accepts «базовая плоскость ИЛИ ПЛОСКАЯ ГРАНЬ» (<c>ksapi_iplane3dbyoffset_setbaseplane.html</c>),
    /// so the reference may also be a face.</summary>
    public static (IPlane3D? Plane, string? Failure) CreatePlaneByOffset(
        IPlanes3D planes, IModelObject baseObject, double offsetMm, bool direction, string? name)
    {
        try
        {
            if (planes.Add(ksObj3dTypeEnum.o3d_planeOffset) is not IPlane3D plane)
            {
                return (null, "IPlanes3D.Add(o3d_planeOffset) не отдал IPlane3D");
            }

            if (plane is not IPlane3DByOffset byOffset)
            {
                return (null, "созданная смещённая плоскость не отвечает QI(IPlane3DByOffset)");
            }

            if (name is { Length: > 0 })
            {
                plane.Name = name;
            }

            byOffset.BasePlane = baseObject;
            byOffset.Offset = offsetMm;
            byOffset.Direction = direction;
            if (SafeB(plane.Update) != true)
            {
                return (null, "IPlane3D.Update() не подтвердил создание смещённой плоскости");
            }

            return (plane, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание смещённой плоскости бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>An angled plane: <c>o3d_planeAngle</c> → <c>IPlane3DByAngle</c>
    /// (<c>ksapi_iplane3dbyangle.html</c>). DOC: the angle is relative to the base plane
    /// (<c>…setangle.html</c>: «угол наклона плоскости относительно базовой плоскости (в градусах)»),
    /// and <c>BaseLine</c> is the base line, i.e. the tilt axis (<c>…setbaseline.html</c>).</summary>
    public static (IPlane3D? Plane, string? Failure) CreatePlaneByAngle(
        IPlanes3D planes, IModelObject basePlane, IModelObject? baseLine, double angleDeg, bool direction,
        string? name)
    {
        try
        {
            if (planes.Add(ksObj3dTypeEnum.o3d_planeAngle) is not IPlane3D plane)
            {
                return (null, "IPlanes3D.Add(o3d_planeAngle) не отдал IPlane3D");
            }

            if (plane is not IPlane3DByAngle byAngle)
            {
                return (null, "созданная наклонная плоскость не отвечает QI(IPlane3DByAngle)");
            }

            if (name is { Length: > 0 })
            {
                plane.Name = name;
            }

            byAngle.BasePlane = basePlane;
            if (baseLine is not null)
            {
                byAngle.BaseLine = baseLine;
            }

            byAngle.Angle = angleDeg;
            byAngle.Direction = direction;
            if (SafeB(plane.Update) != true)
            {
                return (null, "IPlane3D.Update() не подтвердил создание наклонной плоскости");
            }

            return (plane, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание наклонной плоскости бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    // FindBasePlane (searching IPlanes3D for a standard plane by ModelObjectType) was REMOVED
    // 21.09.2026: the route was MEASURED non-existent — a probe showed GetPlanes3D returns Count = 0
    // on a part with a finished body, i.e. IPlanes3D holds no standard planes at all, so the search
    // failed for a reason of the instrument, not of the product. A named datum is obtained via the
    // documented ksPart.GetDefaultEntity (see Api5Session.AuxGeometry.cs:ResolveNamedBasePlane).
    // History: docs/decisions/adapter-api7.md#aux-geometry

    /// <summary>Read a plane: kind, angle or offset, direction, reference and a SIGNED NORMAL. DOC: the
    /// normal is taken from the mathematical surface at the START of the parametric domain
    /// (<c>ParamUMin</c>, <c>ParamVMin</c>), not at an arbitrary point — <c>GetNormal(u, v, …)</c> is
    /// documented as a function of the surface parameters (<c>IMathSurface3D.GetNormal</c>), and
    /// "take u = 0" would be a guess about the valid domain.</summary>
    public static AuxGeomRow ReadPlane(IPlane3D plane, int index)
    {
        var notes = new List<string>();
        var name = SafeS(() => plane.Name);
        var subKind = SafeS(() => plane.ModelObjectType.ToString());
        var kind = "plane";
        double? angle = null;
        double? offset = null;
        bool? direction = null;
        string? baseName = null;
        string? lineName = null;

        switch (plane)
        {
            case IPlane3DByAngle byAngle:
                kind = "by_angle";
                angle = SafeD(() => byAngle.Angle);
                direction = SafeB(() => byAngle.Direction);
                baseName = SafeS(() => byAngle.BasePlane?.Name);
                lineName = SafeS(() => byAngle.BaseLine?.Name);
                if (angle is null)
                {
                    notes.Add("угол наклона не прочитан");
                }

                if (byAngle.BaseLine is null)
                {
                    notes.Add("базовая прямая (ось наклона) у плоскости не задана");
                }

                break;
            case IPlane3DByOffset byOffset:
                kind = "offset";
                offset = SafeD(() => byOffset.Offset);
                direction = SafeB(() => byOffset.Direction);
                baseName = SafeS(() => byOffset.BasePlane?.Name);
                if (offset is null)
                {
                    notes.Add("смещение не прочитано");
                }

                break;
            default:
                notes.Add($"вид плоскости определён по ModelObjectType={subKind}, " +
                    "параметры построения не читаются этим маршрутом");
                break;
        }

        var (origin, normal) = ReadPlaneSurface(plane, notes);
        return new AuxGeomRow(kind, index, name, subKind, origin, normal, angle, offset, direction,
            baseName, lineName, notes);
    }

    private static (double[]? Origin, double[]? Normal) ReadPlaneSurface(IPlane3D plane, List<string> notes)
    {
        try
        {
            if (plane.Surface is not IMathSurface3D surface)
            {
                notes.Add("IPlane3D.Surface не отвечает IMathSurface3D — нормаль не прочитана");
                return (null, null);
            }

            var u = surface.ParamUMin;
            var v = surface.ParamVMin;
            double[]? origin = surface.GetPoint(u, v, out var px, out var py, out var pz)
                ? [px, py, pz]
                : null;
            double[]? normal = surface.GetNormal(u, v, out var nx, out var ny, out var nz)
                ? [nx, ny, nz]
                : null;
            if (origin is null)
            {
                notes.Add("точка поверхности плоскости не прочитана");
            }

            if (normal is null)
            {
                notes.Add("нормаль поверхности плоскости не прочитана");
            }

            return (origin, normal);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"чтение поверхности плоскости бросило {ex.GetType().Name}");
            return (null, null);
        }
    }

    /// <summary>Edit an ALREADY CREATED plane: the documented setters <c>IPlane3DByOffset.Offset</c>,
    /// <c>IPlane3DByAngle.Angle</c> and <c>IPlane3DBy*.BasePlane</c>, then <c>Update()</c>.</summary>
    /// <remarks>INVARIANT: only what was supplied is set — a <c>null</c> field means "do not change",
    /// not "set to zero", since substituting zero would rewrite an offset the client did not ask about.
    /// INVARIANT: kind/field correspondence is checked at the caller BEFORE the call; the check here is
    /// a second safety net — the setter of a foreign kind is unreachable by type and would raise
    /// <c>InvalidCastException</c> instead of a clear refusal. INVARIANT: success is not an applied edit
    /// — confirmation is a RE-READ (<see cref="ReadPlane"/>) done by the caller; only a named refusal
    /// reason is returned here.</remarks>
    public static string? UpdatePlane(
        IPlane3D plane, double? offsetMm, double? angleDeg, bool? direction, IModelObject? basePlane)
    {
        var changed = new List<string>();
        try
        {
            if (offsetMm is { } newOffset)
            {
                if (plane is not IPlane3DByOffset byOffset)
                {
                    return $"смещение не записано: плоскость вида {SafeS(() => plane.ModelObjectType.ToString())} " +
                        "не отвечает IPlane3DByOffset — у наклонной плоскости смещения нет";
                }

                byOffset.Offset = newOffset;
                changed.Add($"offset_mm={newOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (angleDeg is { } newAngle)
            {
                if (plane is not IPlane3DByAngle byAngle)
                {
                    return $"угол не записан: плоскость вида {SafeS(() => plane.ModelObjectType.ToString())} " +
                        "не отвечает IPlane3DByAngle — у смещённой плоскости угла нет";
                }

                byAngle.Angle = newAngle;
                changed.Add($"angle_deg={newAngle.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (direction is { } newDirection)
            {
                switch (plane)
                {
                    case IPlane3DByOffset offsetPlane:
                        offsetPlane.Direction = newDirection;
                        break;
                    case IPlane3DByAngle anglePlane:
                        anglePlane.Direction = newDirection;
                        break;
                    default:
                        return "знак направления не записан: вид плоскости не отвечает ни " +
                            "IPlane3DByOffset, ни IPlane3DByAngle";
                }

                changed.Add($"direction={newDirection}");
            }

            if (basePlane is not null)
            {
                switch (plane)
                {
                    case IPlane3DByOffset offsetPlane:
                        offsetPlane.BasePlane = basePlane;
                        break;
                    case IPlane3DByAngle anglePlane:
                        anglePlane.BasePlane = basePlane;
                        break;
                    default:
                        return "опора не записана: вид плоскости не отвечает ни IPlane3DByOffset, " +
                            "ни IPlane3DByAngle";
                }

                changed.Add("base_plane");
            }

            if (changed.Count == 0)
            {
                return "ни одно поле не задано: правка не имеет предмета";
            }

            return SafeB(() => plane.Update()) == true
                ? null
                : $"Update() не подтвердил правку ({string.Join(", ", changed)})";
        }
        catch (COMException ex)
        {
            return $"сеттер плоскости отказал: HRESULT 0x{ex.HResult:X8} ({string.Join(", ", changed)})";
        }
        catch (InvalidCastException ex)
        {
            return $"сеттер плоскости бросил {ex.GetType().Name} ({string.Join(", ", changed)})";
        }
    }

    // ------------------------------------------------------------------- axes

    /// <summary>An axis through two model points: <c>o3d_axis2Points</c> → <c>IAxis3DBy2Points</c>.
    /// MEASURED (R.13): <c>Update()</c> is called AFTER both points are supplied — an axis updated
    /// before the points reads <c>Valid=False</c> and does not enter the tree.</summary>
    public static (IAxis3D? Axis, string? Failure) CreateAxisBy2Points(
        IAxes3D axes, IModelContainer container, double[] point1, double[] point2, string? name)
    {
        var (p1, failure1) = CreatePointByCoordinates(container, point1[0], point1[1], point1[2]);
        if (p1 is null)
        {
            return (null, $"точка 1 оси не создана: {failure1}");
        }

        var (p2, failure2) = CreatePointByCoordinates(container, point2[0], point2[1], point2[2]);
        if (p2 is null)
        {
            return (null, $"точка 2 оси не создана: {failure2}");
        }

        try
        {
            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3D axis)
            {
                return (null, "IAxes3D.Add(o3d_axis2Points) не отдал IAxis3D");
            }

            if (axis is not IAxis3DBy2Points by2)
            {
                return (null, "созданная ось не отвечает QI(IAxis3DBy2Points)");
            }

            if (name is { Length: > 0 })
            {
                axis.Name = name;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;
            if (SafeB(axis.Update) != true)
            {
                return (null, "IAxis3D.Update() не подтвердил создание оси по двум точкам");
            }

            return (axis, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание оси по двум точкам бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>An axis through a cylindrical or conical face: <c>o3d_axisConeFace</c> →
    /// <c>IAxis3DByConeface</c> (<c>ksapi_iaxis3dbyconeface_setface.html</c>).</summary>
    public static (IAxis3D? Axis, string? Failure) CreateAxisByFace(IAxes3D axes, IModelObject face, string? name)
    {
        try
        {
            if (axes.Add(ksObj3dTypeEnum.o3d_axisConeFace) is not IAxis3D axis)
            {
                return (null, "IAxes3D.Add(o3d_axisConeFace) не отдал IAxis3D");
            }

            if (axis is not IAxis3DByConeface byFace)
            {
                return (null, "созданная ось не отвечает QI(IAxis3DByConeface)");
            }

            if (name is { Length: > 0 })
            {
                axis.Name = name;
            }

            byFace.Face = face;
            if (SafeB(axis.Update) != true)
            {
                return (null, "IAxis3D.Update() не подтвердил создание оси по поверхности");
            }

            return (axis, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание оси по поверхности бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>An axis through an edge: <c>o3d_axisEdge</c> → <c>IAxis3DByEdge</c>.</summary>
    public static (IAxis3D? Axis, string? Failure) CreateAxisByEdge(IAxes3D axes, IModelObject edge, string? name)
    {
        try
        {
            if (axes.Add(ksObj3dTypeEnum.o3d_axisEdge) is not IAxis3D axis)
            {
                return (null, "IAxes3D.Add(o3d_axisEdge) не отдал IAxis3D");
            }

            if (axis is not IAxis3DByEdge byEdge)
            {
                return (null, "созданная ось не отвечает QI(IAxis3DByEdge)");
            }

            if (name is { Length: > 0 })
            {
                axis.Name = name;
            }

            byEdge.Edge = edge;
            if (SafeB(axis.Update) != true)
            {
                return (null, "IAxis3D.Update() не подтвердил создание оси по ребру");
            }

            return (axis, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, $"создание оси по ребру бросило {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Read an axis. The kind is determined by QI; coordinates come from the vertices
    /// (<c>Point1</c>/<c>Point2</c>) where present; <c>MathCurve</c> is read separately and its
    /// absence is named, not replaced by zero.</summary>
    public static AuxGeomRow ReadAxis(IAxis3D axis, int index)
    {
        var notes = new List<string>();
        var name = SafeS(() => axis.Name);
        var subKind = SafeS(() => axis.ModelObjectType.ToString());
        var kind = "axis";
        double[]? point1 = null;
        double[]? point2 = null;
        string? baseName = null;

        switch (axis)
        {
            case IAxis3DBy2Points by2:
                kind = "by_2_points";
                point1 = ReadVertex(by2.Point1, notes, "вершина 1");
                point2 = ReadVertex(by2.Point2, notes, "вершина 2");
                break;
            case IAxis3DByConeface byFace:
                kind = "by_cone_face";
                baseName = SafeS(() => byFace.Face?.Name);
                break;
            case IAxis3DByEdge byEdge:
                kind = "by_edge";
                baseName = SafeS(() => byEdge.Edge?.Name);
                break;
            case IAxis3DByPointAndObject byPoint:
                kind = "by_point_and_object";
                point1 = ReadVertex(byPoint.Point, notes, "вершина");
                baseName = SafeS(() => byPoint.DirectObject?.Name);
                break;
        }

        var mathCurve = Safe(() => (bool?)(axis.MathCurve is not null));
        if (mathCurve == false)
        {
            notes.Add("IAxis3D.MathCurve → null");
        }
        else if (mathCurve is null)
        {
            notes.Add("IAxis3D.MathCurve не прочитан");
        }

        return new AuxGeomRow(kind, index, name, subKind, point1, point2, null, null, null, baseName,
            null, notes);
    }

    // ------------------------------------------------------- auxiliary-geometry enumeration

    /// <summary>The collection's element count, or <c>null</c> if it could not be read. Public because
    /// both auxiliary-geometry tools use it: "not read" must look the same in every response, otherwise
    /// one and the same state would be named differently.</summary>
    public static int? SafeCount(object? collection) => collection switch
    {
        IPlanes3D planes => SafeI(() => planes.Count),
        IAxes3D axes => SafeI(() => axes.Count),
        IPoints3D points => SafeI(() => points.Count),
        _ => null,
    };

    private static double[]? ReadVertex(IModelObject? vertex, List<string> notes, string what)
    {
        if (vertex is null)
        {
            notes.Add($"{what} не задана");
            return null;
        }

        // A vertex is read as an IPoint3D: a model point and a vertex are the same model object.
        if (vertex is IPoint3D point)
        {
            var x = SafeD(() => point.X);
            var y = SafeD(() => point.Y);
            var z = SafeD(() => point.Z);
            if (x is not null && y is not null && z is not null)
            {
                return [x.Value, y.Value, z.Value];
            }
        }

        notes.Add($"{what}: координаты не прочитаны (объект не отвечает IPoint3D)");
        return null;
    }
}
