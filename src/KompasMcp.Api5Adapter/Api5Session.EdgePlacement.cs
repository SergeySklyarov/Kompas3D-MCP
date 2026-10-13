using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Where the surface a fillet or a chamfer produced ACTUALLY landed.</summary>
/// <remarks>WHY. These operations address edges by REFERENCE, and the kernel may re-resolve a reference to
/// a different edge of the same topology on a rebuild; on a body whose corners are equivalent the volume
/// then cannot tell "built where asked" from "built elsewhere", while the response says <c>succeeded</c>.
/// The criterion for this family is the PLACEMENT of the new surface, and the two faces that meet at the
/// requested edge still CONTAIN its line after the operation. INVARIANT: the decision is the pure rule
/// <see cref="EdgeSurfaceMatch"/>; an unread surface is a NAMED gap, never a refusal.
/// History: docs/decisions/adapter-features.md#fillet-on-other-edge</remarks>
public partial class Api5Session
{
    /// <summary>How far a face's plane may be from the edge line and still count as meeting the edge there,
    /// in mm. The faces share the edge exactly, so the band is a rounding band.</summary>
    private const double PlaneThroughEdgeToleranceMm = 1e-6d;

    /// <summary>One edge the caller asked the operation to act on, reduced to a LINE before the mutation —
    /// after the fillet the edge itself is gone, and the line is what the new surface must lie along.</summary>
    private sealed record RequestedEdgeLine(string Reference, double[] Start, double[] End, bool Straight);

    /// <summary>One face of the body, reduced to what the placement rule needs. <c>Cylinder</c> selects the
    /// meaning of <c>RadiusMm</c>; <c>Direction</c> is the axis of a cylinder and the normal of a plane.</summary>
    private sealed record SurfaceShape(bool Cylinder, double[] Point, double[] Direction, double RadiusMm);

    /// <summary>What the placement check concluded for one operation.</summary>
    private sealed record EdgePlacementOutcome(
        bool Refused,
        string? Code,
        string? Message,
        Dictionary<string, object?> Details,
        IReadOnlyList<string> Unverified);

    /// <summary>The lines of the requested edges, taken BEFORE the mutation. A curved edge is carried as
    /// <c>Straight = false</c>: the placement rule is stated for a straight edge, and saying so is part of
    /// the answer rather than a silent skip.</summary>
    private static List<RequestedEdgeLine> EdgeLines(IReadOnlyList<ksEntity> entities, IReadOnlyList<string> references)
    {
        var lines = new List<RequestedEdgeLine>(entities.Count);
        for (var i = 0; i < entities.Count; i++)
        {
            var reference = i < references.Count ? references[i] : "<без ссылки>";
            try
            {
                if (entities[i].GetDefinition() is not ksEdgeDefinition edge
                    || edge.GetVertex(true) is not ksVertexDefinition first
                    || edge.GetVertex(false) is not ksVertexDefinition second
                    || !first.GetPoint(out var ax, out var ay, out var az)
                    || !second.GetPoint(out var bx, out var by, out var bz))
                {
                    lines.Add(new RequestedEdgeLine(reference, new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d }, false));
                    continue;
                }

                var straight = SafeBool(edge.IsStraight) == true;
                lines.Add(new RequestedEdgeLine(reference, new[] { ax, ay, az }, new[] { bx, by, bz }, straight));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                lines.Add(new RequestedEdgeLine(reference, new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d }, false));
            }
        }

        return lines;
    }

    /// <summary>Every face of the body reduced to a cylinder or a plane, through the route P2.5 measured.</summary>
    private static List<SurfaceShape> ReadSurfaceShapes(DocumentEntry document)
    {
        var shapes = new List<SurfaceShape>();
        if (document.Kind != DocumentKind.Part || document.PartNow().GetMainBody() is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return shapes;
        }

        for (var i = 0; i < faces.GetCount(); i++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is not ksFaceDefinition face
                || face.GetSurface() is not ksSurface surface)
            {
                continue;
            }

            try
            {
                switch (surface.GetSurfaceParam())
                {
                    case ksCylinderParam cylinder when cylinder.GetPlacement() is ksPlacement placement
                        && placement.GetOrigin(out var cx, out var cy, out var cz)
                        && placement.GetVector(2, out var ax, out var ay, out var az):
                        shapes.Add(new SurfaceShape(true, new[] { cx, cy, cz }, new[] { ax, ay, az }, cylinder.radius));
                        break;
                    case ksPlaneParam plane when plane.GetPlacement() is ksPlacement placement
                        && placement.GetOrigin(out var px, out var py, out var pz)
                        && placement.GetVector(2, out var nx, out var ny, out var nz):
                        shapes.Add(new SurfaceShape(false, new[] { px, py, pz }, new[] { nx, ny, nz }, 0d));
                        break;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A face whose surface did not read is simply absent from the list: the caller names the
                // absence where it matters instead of turning it into a finding about the product.
            }
        }

        return shapes;
    }

    /// <summary>Whether a fillet of <paramref name="radiusMm"/> really adjoins every straight requested edge.</summary>
    private EdgePlacementOutcome CheckFilletPlacement(
        DocumentEntry document,
        IReadOnlyList<RequestedEdgeLine> requested,
        double radiusMm,
        string featureReference)
    {
        var shapes = ReadSurfaceShapes(document);
        var cylinders = shapes.Where(s => s.Cylinder && Math.Abs(s.RadiusMm - radiusMm) <= 1e-6d).ToList();
        var planes = shapes.Where(s => !s.Cylinder).ToList();
        var unverified = new List<string>();
        var measured = new List<object?>();

        foreach (var edge in requested)
        {
            if (!edge.Straight)
            {
                unverified.Add(
                    "fillet_placement_not_checked_on_curve — ребро " + edge.Reference
                    + " не прямое: прилегание цилиндра проверяется только для прямого ребра");
                continue;
            }

            var adjacent = PlanesThrough(planes, edge);
            if (adjacent.Count != 2)
            {
                unverified.Add(
                    "fillet_placement_not_checked — граней, содержащих ребро " + edge.Reference
                    + ", найдено " + adjacent.Count + " вместо двух: прилегание поверхности проверить нечем");
                continue;
            }

            var hit = cylinders.FirstOrDefault(c => EdgeSurfaceMatch.FilletAgainstEdge(
                edge.Start, edge.End, c.Point, c.Direction, radiusMm,
                adjacent.Select(p => p.Point).ToList(),
                adjacent.Select(p => p.Direction).ToList()).Adjoins);

            if (hit is null)
            {
                var probe = cylinders.Count > 0
                    ? EdgeSurfaceMatch.FilletAgainstEdge(
                        edge.Start, edge.End, cylinders[0].Point, cylinders[0].Direction, radiusMm,
                        adjacent.Select(p => p.Point).ToList(),
                        adjacent.Select(p => p.Direction).ToList())
                    : default;

                measured.Add(new Dictionary<string, object?>
                {
                    ["edge"] = edge.Reference,
                    ["edge_start_mm"] = edge.Start,
                    ["edge_end_mm"] = edge.End,
                    ["cylinders_of_radius"] = cylinders.Count,
                    ["cylinders_of_any_radius"] = shapes.Count(s => s.Cylinder),
                    ["nearest_code"] = probe.Code,
                    ["nearest_measured_mm"] = probe.MeasuredMm,
                    ["expected_mm"] = radiusMm,
                });

                return new EdgePlacementOutcome(
                    true,
                    "fillet_on_other_edge",
                    "Скругление радиуса " + radiusMm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм не прилегает к запрошенному ребру " + edge.Reference
                    + ": цилиндрических граней такого радиуса на теле " + cylinders.Count
                    + ", и ни одна не отстоит от обеих граней, смежных с этим ребром, на радиус ("
                    + (probe.Code ?? "не измерено") + "). Признак создан и остался в дереве, но поверхность "
                    + "построена НЕ по запрошенному ребру — это не та геометрия, которую просил клиент. "
                    + "Удалите признак вызовом kompas_delete_feature по feature_ref и повторите.",
                    new Dictionary<string, object?>
                    {
                        ["code"] = "fillet_on_other_edge",
                        ["feature_ref"] = featureReference,
                        ["radius_mm"] = radiusMm,
                        ["requested_edges"] = measured,
                        ["cylinders_found"] = cylinders.Count,
                    },
                    unverified);
            }

            measured.Add(new Dictionary<string, object?>
            {
                ["edge"] = edge.Reference,
                ["cylinders_of_radius"] = cylinders.Count,
                ["adjacent_faces"] = adjacent.Count,
            });
        }

        return new EdgePlacementOutcome(false, null, null, new Dictionary<string, object?>
        {
            ["checked_edges"] = measured,
            ["cylinders_of_radius"] = cylinders.Count,
        }, unverified);
    }

    /// <summary>Whether a chamfer with the two given legs really adjoins every straight requested edge.</summary>
    private EdgePlacementOutcome CheckChamferPlacement(
        DocumentEntry document,
        IReadOnlyList<RequestedEdgeLine> requested,
        double distance1Mm,
        double distance2Mm,
        string featureReference)
    {
        var shapes = ReadSurfaceShapes(document);
        var planes = shapes.Where(s => !s.Cylinder).ToList();
        var unverified = new List<string>();
        var measured = new List<object?>();

        foreach (var edge in requested)
        {
            if (!edge.Straight)
            {
                unverified.Add(
                    "chamfer_placement_not_checked_on_curve — ребро " + edge.Reference
                    + " не прямое: прилегание фаски проверяется только для прямого ребра");
                continue;
            }

            var adjacent = PlanesThrough(planes, edge);
            if (adjacent.Count != 2)
            {
                unverified.Add(
                    "chamfer_placement_not_checked — граней, содержащих ребро " + edge.Reference
                    + ", найдено " + adjacent.Count + " вместо двух: прилегание фаски проверить нечем");
                continue;
            }

            var adjacentPoints = adjacent.Select(p => p.Point).ToList();
            var adjacentNormals = adjacent.Select(p => p.Direction).ToList();
            var hit = planes.FirstOrDefault(p => EdgeSurfaceMatch.ChamferAgainstEdge(
                edge.Start, edge.End, p.Point, p.Direction, distance1Mm, distance2Mm,
                adjacentPoints, adjacentNormals).Adjoins);

            if (hit is null)
            {
                measured.Add(new Dictionary<string, object?>
                {
                    ["edge"] = edge.Reference,
                    ["edge_start_mm"] = edge.Start,
                    ["edge_end_mm"] = edge.End,
                    ["planar_faces"] = planes.Count,
                    ["expected_legs_mm"] = new[] { distance1Mm, distance2Mm },
                });

                return new EdgePlacementOutcome(
                    true,
                    "chamfer_on_other_edge",
                    "Ни одна плоская грань тела не отсекает обе грани, смежные с запрошенным ребром "
                    + edge.Reference + ", на заданные катеты ("
                    + distance1Mm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " и "
                    + distance2Mm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм): фаска построена НЕ по запрошенному ребру. Признак создан и остался в дереве. "
                    + "Удалите признак вызовом kompas_delete_feature по feature_ref и повторите.",
                    new Dictionary<string, object?>
                    {
                        ["code"] = "chamfer_on_other_edge",
                        ["feature_ref"] = featureReference,
                        ["distance1_mm"] = distance1Mm,
                        ["distance2_mm"] = distance2Mm,
                        ["requested_edges"] = measured,
                        ["planar_faces"] = planes.Count,
                    },
                    unverified);
            }

            measured.Add(new Dictionary<string, object?>
            {
                ["edge"] = edge.Reference,
                ["planar_faces"] = planes.Count,
            });
        }

        return new EdgePlacementOutcome(false, null, null, new Dictionary<string, object?>
        {
            ["checked_edges"] = measured,
            ["planar_faces"] = planes.Count,
        }, unverified);
    }

    /// <summary>The planar faces whose plane contains the requested edge line. Both endpoints must lie in
    /// the plane, which is what "meets the edge there" means for a plane — the sign of the normal is not
    /// consulted, because the rule takes distances in absolute value.</summary>
    private static List<SurfaceShape> PlanesThrough(IReadOnlyList<SurfaceShape> planes, RequestedEdgeLine edge)
    {
        var found = new List<SurfaceShape>();
        foreach (var plane in planes)
        {
            if (!EdgeSurfaceMatch.Unit(plane.Direction, out var normal))
            {
                continue;
            }

            var atStart = Math.Abs(Dot(normal, Subtract(edge.Start, plane.Point)));
            var atEnd = Math.Abs(Dot(normal, Subtract(edge.End, plane.Point)));
            if (atStart <= PlaneThroughEdgeToleranceMm && atEnd <= PlaneThroughEdgeToleranceMm)
            {
                found.Add(plane);
            }
        }

        return found;
    }

    private static double Dot(double[] left, double[] right) =>
        left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

    private static double[] Subtract(double[] left, double[] right) =>
        new[] { left[0] - right[0], left[1] - right[1], left[2] - right[2] };
}
