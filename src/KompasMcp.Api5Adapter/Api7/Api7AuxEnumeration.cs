using System.Runtime.InteropServices;
using KompasAPI7;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>A part's auxiliary geometry — planes, axes, points — as OBJECTS with a collection index.</summary>
/// <remarks>DOC: the help documents <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c>
/// and <c>ISketchs.GetSketchByName</c>; MEASURED (InteropScan): the shipped <c>Interop.KompasAPI7.dll</c>
/// has NO member whose name contains <c>ByName</c>, so a name is resolved by ENUMERATING the
/// collection and comparing <c>Name</c> (<c>Count</c>, indexed property, <c>Name</c>).
/// LIMIT: <c>Index</c> is a position, NOT stable — a rebuild shifts it — never called an address.
/// LIMIT: the enumeration limit is a number, not a default; truncation is not silent (row notes).
/// History: docs/decisions/adapter-api7.md#aux-enumeration</remarks>
internal static class Api7AuxEnumeration
{
    /// <summary>Enumeration-route string — one for both the response and the acceptance evidence.</summary>
    public const string Route =
        "IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D, IModelContainer.GetPoints3D → " +
        "Count + индексированное свойство (ByName в поставке отсутствует)";

    /// <summary>The part's planes. A plane is read by the same method as a single one.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Planes(IPlanes3D planes, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => planes.Count);
        if (count is null)
        {
            return (rows, "IPlanes3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var plane = planes.get_Plane3D(i);
                if (plane is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IPlanes3D.Plane3D[i] → null"]));
                    continue;
                }

                rows.Add(Api7AuxGeometry.ReadPlane(plane, i));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение плоскости бросило {ex.GetType().Name}"]));
            }
        }

        if (count.Value > limit)
        {
            return (rows, null);
        }

        return (rows, null);
    }

    /// <summary>The part's axes.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Axes(IAxes3D axes, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => axes.Count);
        if (count is null)
        {
            return (rows, "IAxes3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var axis = axes.get_Axis3D(i);
                if (axis is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IAxes3D.Axis3D[i] → null"]));
                    continue;
                }

                rows.Add(Api7AuxGeometry.ReadAxis(axis, i));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение оси бросило {ex.GetType().Name}"]));
            }
        }

        return (rows, null);
    }

    /// <summary>The part's points.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Points(IPoints3D points, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => points.Count);
        if (count is null)
        {
            return (rows, "IPoints3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var point = points.get_Point3D(i);
                if (point is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IPoints3D.Point3D[i] → null"]));
                    continue;
                }

                var (coordinates, parameterType, association, notes) =
                    Api7AuxGeometry.ReadPoint(point);
                rows.Add(new AuxGeomRow(
                    "point", i, SafeS(() => point.Name), parameterType,
                    coordinates, null, null, null, null, association, null, notes));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение точки бросило {ex.GetType().Name}"]));
            }
        }

        return (rows, null);
    }

    /// <summary>Find a plane BY NAME. Returns the object or a named reason that distinguishes "no
    /// such name" from "the name is not unique": silently taking the first of several would make the
    /// address ambiguous.</summary>
    public static (IModelObject? Plane, string? Failure) PlaneByName(IPlanes3D planes, string name)
    {
        var matches = new List<IModelObject>();
        var count = SafeI(() => planes.Count) ?? 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var plane = planes.get_Plane3D(i);
                if (plane is not null && string.Equals(plane.Name, name, StringComparison.Ordinal))
                {
                    matches.Add(plane);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A failure to read ONE element does not abort the search: an unreadable element
                // simply takes no part in the name comparison, and that is not passed off as "there
                // is no such name".
            }
        }

        return matches.Count switch
        {
            0 => (null, $"плоскости с именем «{name}» в коллекции нет"),
            1 => (matches[0], null),
            _ => (null, $"имя «{name}» не уникально: найдено совпадений {matches.Count}"),
        };
    }

    private static int? SafeI(Func<int> read)
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
}
