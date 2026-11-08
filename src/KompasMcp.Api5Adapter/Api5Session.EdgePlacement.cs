using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Where an operation that names an EDGE or a FACE by reference actually landed.</summary>
/// <remarks>WHY. The kernel may re-resolve such a reference to a different edge or face of the same
/// topology, and on a body with equivalent candidates the volume cannot tell "built where asked" from
/// "built elsewhere" while the response says <c>succeeded</c>. The criterion is the PLACEMENT: a fillet
/// cylinder one radius from both faces at the edge, a chamfer cutting them at the two legs, a removed
/// face gone from its plane, a drilled axis on its base face, a tilted plane about its base line.
/// INVARIANT: both decisions are pure rules (<see cref="EdgeSurfaceMatch"/>, <see cref="FacePlacementMatch"/>);
/// an unread value is a NAMED gap. History: docs/decisions/adapter-features.md#fillet-on-other-edge</remarks>
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

    /// <summary>The document as it read BEFORE the feature was created: the revision, the planes of the body
    /// and the number of its edges, plus the name the created feature carries. None of the numbers can be
    /// recovered after the mutation, and a refusal must name the state it started from — otherwise the
    /// cause can only be read from a repeated run. <c>EdgeCount</c> is null when the body did not read:
    /// that is a gap, not "no edges".</summary>
    private sealed record FeatureCallSnapshot(
        long Revision,
        IReadOnlyList<SurfaceShape> Planes,
        int? EdgeCount,
        string? FeatureName);

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
            (double[] Start, double[] End, bool Straight)? line = null;
            try
            {
                line = entities[i].GetDefinition() is ksEdgeDefinition edge ? EdgeLineOf(edge) : null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // An edge whose definition did not read stays a degenerate line, so the placement rule
                // answers "not_read" instead of finding the surface somewhere else.
            }

            lines.Add(line is { } read
                ? new RequestedEdgeLine(reference, read.Start, read.End, read.Straight)
                : new RequestedEdgeLine(reference, new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d }, false));
        }

        return lines;
    }

    /// <summary>Both vertices of one edge, read from the model, and whether the edge is straight; null when
    /// it did not read. INVARIANT: an unread vertex is never carried as a zero point — a zero point is a
    /// placement claim the read did not make.</summary>
    private static (double[] Start, double[] End, bool Straight)? EdgeLineOf(ksEdgeDefinition edge)
    {
        try
        {
            if (edge.GetVertex(true) is not ksVertexDefinition first
                || edge.GetVertex(false) is not ksVertexDefinition second
                || !first.GetPoint(out var ax, out var ay, out var az)
                || !second.GetPoint(out var bx, out var by, out var bz))
            {
                return null;
            }

            return (new[] { ax, ay, az }, new[] { bx, by, bz }, SafeBool(edge.IsStraight) == true);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Every edge of the main body once, for a refusal that has to count them. Deduplication is by
    /// COM identity, because one edge is reachable from BOTH of its faces — the route and the deduplication
    /// <c>kompas_read_topology</c> uses. An unreadable body gives an empty list: an edge that did not read is
    /// absent, and the count the caller publishes is the count it got.</summary>
    private static List<ksEdgeDefinition> BodyEdges(DocumentEntry document)
    {
        var edges = new List<ksEdgeDefinition>();
        if (document.Kind != DocumentKind.Part || document.PartNow().GetMainBody() is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return edges;
        }

        var seen = new HashSet<IntPtr>();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            try
            {
                if (AsInterface<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face
                    || face.EdgeCollection() is not ksEdgeCollection collection)
                {
                    continue;
                }

                for (var e = 0; e < collection.GetCount(); e++)
                {
                    if (AsInterface<ksEdgeDefinition>(collection.GetByIndex(e)) is not ksEdgeDefinition edge)
                    {
                        continue;
                    }

                    var pointer = Marshal.GetIUnknownForObject(edge);
                    var alreadySeen = !seen.Add(pointer);
                    Marshal.Release(pointer);
                    if (!alreadySeen)
                    {
                        edges.Add(edge);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A face whose edge collection did not read contributes nothing; the count stays a count.
            }
        }

        return edges;
    }

    /// <summary>Edge count of the main body BEFORE a mutation; null when the body itself does not read.
    /// The guard is the settle's own (<see cref="HasSolidBody"/>), so a "before" count and a "before"
    /// volume never disagree about whether there was a body to read.</summary>
    private static int? BodyEdgeCount(DocumentEntry document) =>
        HasSolidBody(document) ? BodyEdges(document).Count : null;

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

            var adjacent = PlanesThrough(planes, edge.Start, edge.End);
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
    /// <param name="snapshot">The body as it read BEFORE the feature was created. A refusal answers with it
    /// beside what it found afterwards: the revision and the edge count it started from, the planar faces
    /// the body did not have before, and the feature's own name — so the cause is read from one answer
    /// instead of a repeated run.</param>
    private EdgePlacementOutcome CheckChamferPlacement(
        DocumentEntry document,
        IReadOnlyList<RequestedEdgeLine> requested,
        double distance1Mm,
        double distance2Mm,
        string featureReference,
        FeatureCallSnapshot snapshot)
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

            var adjacent = PlanesThrough(planes, edge.Start, edge.End);
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

                // The chamfer consumed the edge it landed on, so that edge cannot be reported as an edge:
                // what names the place is the face that APPEARED and the vertices of the lines where it
                // cuts its neighbours. The corner itself is read from the plane, the legs being known.
                // History: docs/decisions/adapter-features.md#chamfer-on-other-edge-snapshot
                return new EdgePlacementOutcome(
                    true,
                    "chamfer_on_other_edge",
                    "Ни одна плоская грань тела не отсекает обе грани, смежные с запрошенным ребром "
                    + edge.Reference + ", на заданные катеты ("
                    + distance1Mm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " и "
                    + distance2Mm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм): фаска построена НЕ по запрошенному ребру. Признак создан и остался в дереве. "
                    + "Удалите признак вызовом kompas_delete_feature по feature_ref и повторите. Где "
                    + "поверхность оказалась — грани, появившиеся на теле, в details.new_faces.",
                    new Dictionary<string, object?>
                    {
                        ["code"] = "chamfer_on_other_edge",
                        ["feature_ref"] = featureReference,
                        ["feature_name"] = snapshot.FeatureName,
                        ["distance1_mm"] = distance1Mm,
                        ["distance2_mm"] = distance2Mm,
                        ["requested_edges"] = measured,
                        ["requested_vertices_source"] =
                            "прочитаны с модели до Entity.Create(), не из запроса клиента",
                        ["new_faces"] = NewPlanarFaces(document, snapshot.Planes),
                        ["revision_before"] = snapshot.Revision,
                        ["revision_after"] = document.Revision,
                        ["edge_count_before"] = snapshot.EdgeCount,
                        ["edge_count_after"] = BodyEdgeCount(document),
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

    /// <summary>One face reduced to a plane and an area, or null when it is not planar or did not read.
    /// Route P2.5, the same one <c>kompas_read_topology</c> publishes with.</summary>
    private static FacePlacementMatch.PlanarFace? PlanarPlacementOf(ksFaceDefinition face)
    {
        try
        {
            if (face.GetSurface() is not ksSurface surface
                || surface.GetSurfaceParam() is not ksPlaneParam plane
                || plane.GetPlacement() is not ksPlacement placement
                || !placement.GetOrigin(out var x, out var y, out var z)
                || !placement.GetVector(2, out var nx, out var ny, out var nz))
            {
                return null;
            }

            var area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm)) ?? double.NaN;
            return new FacePlacementMatch.PlanarFace(new[] { x, y, z }, new[] { nx, ny, nz }, area);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Every PLANAR face of the body reduced to the placement rule's inputs. A face that did not
    /// read is simply absent: the caller names the absence where it matters.</summary>
    private static List<FacePlacementMatch.PlanarFace> PlanarFacePlacements(DocumentEntry document)
    {
        var found = new List<FacePlacementMatch.PlanarFace>();
        if (document.Kind != DocumentKind.Part || document.PartNow().GetMainBody() is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return found;
        }

        for (var i = 0; i < faces.GetCount(); i++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is not ksFaceDefinition face)
            {
                continue;
            }

            if (PlanarPlacementOf(face) is { } placement)
            {
                found.Add(placement);
            }
        }

        return found;
    }

    /// <summary>A cylindrical or conical face reduced to what the hole-placement rule needs: a point on
    /// its axis, the axis direction, the radius and the length of the face along the axis.</summary>
    private sealed record AxisFace(double[] Point, double[] Direction, double RadiusMm, double? ExtentMm);

    /// <summary>The faces whose axis is a hole's: cylinders of the entrance radius, or, when the entrance
    /// is a CONE, the conical faces of the body.</summary>
    /// <param name="conicEntrance">True for <c>through_conic</c>: its entrance is the cone itself.</param>
    /// <remarks>DOC: <c>kssurface_iscone.html</c>, <c>kssurface_getsurfaceparam.html</c> (cone →
    /// <c>ksConeParam</c>), <c>ksplacement.html</c> + <c>ksplacement_getorigin/getvector</c>.
    /// MEASURED: <c>ksConeParam.radius</c> is not the entrance diameter, so a cone is not selected by
    /// radius. LIMIT: <c>through_conic</c> is the only mode without a cylinder of that diameter.
    /// History: docs/decisions/adapter-features.md#hole-conic-position</remarks>
    private static List<AxisFace> EntranceAxes(DocumentEntry document, double radiusMm, bool conicEntrance)
    {
        var found = new List<AxisFace>();
        if (document.Kind != DocumentKind.Part || document.PartNow().GetMainBody() is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return found;
        }

        for (var i = 0; i < faces.GetCount(); i++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is not ksFaceDefinition face)
            {
                continue;
            }

            try
            {
                var axis = conicEntrance ? ConeAxis(face) : CylinderAxis(face, radiusMm);
                if (axis is not null)
                {
                    found.Add(axis);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A face whose parameters KOMPAS did not return is no reason to abandon the search.
            }
        }

        return found;
    }

    /// <summary>A cylinder of exactly <paramref name="radiusMm"/>; null for anything else.</summary>
    private static AxisFace? CylinderAxis(ksFaceDefinition face, double radiusMm)
    {
        if (face.GetSurface() is not ksSurface surface || !surface.IsCylinder()
            || surface.GetSurfaceParam() is not ksCylinderParam cylinder
            || Math.Abs(cylinder.radius - radiusMm) > HoleRadiusToleranceMm
            || cylinder.GetPlacement() is not ksPlacement placement
            || !placement.GetOrigin(out var x, out var y, out var z)
            || !placement.GetVector(2, out var ax, out var ay, out var az))
        {
            return null;
        }

        return new AxisFace(new[] { x, y, z }, new[] { ax, ay, az }, cylinder.radius, cylinder.height);
    }

    /// <summary>Any conical face; its published radius is not the entrance diameter, so it is carried
    /// as read and never compared with the requested one.</summary>
    private static AxisFace? ConeAxis(ksFaceDefinition face)
    {
        if (face.GetSurface() is not ksSurface surface || !surface.IsCone()
            || surface.GetSurfaceParam() is not ksConeParam cone
            || cone.GetPlacement() is not ksPlacement placement
            || !placement.GetOrigin(out var x, out var y, out var z)
            || !placement.GetVector(2, out var ax, out var ay, out var az))
        {
            return null;
        }

        return new AxisFace(new[] { x, y, z }, new[] { ax, ay, az }, cone.radius, cone.height);
    }

    /// <summary>Refuses when the shell removed a face other than the one it was asked to remove.
    /// INVARIANT: a gap in the reading is NAMED, never turned into a refusal.</summary>
    private static void CheckShellPlacement(
        DocumentEntry document,
        IReadOnlyList<(string Reference, FacePlacementMatch.PlanarFace Placement)> requested,
        int requestedTotal,
        string featureReference,
        List<NamedCheck> checks,
        List<string> unverified)
    {
        if (requested.Count < requestedTotal)
        {
            unverified.Add(
                "shell_placement_not_checked — запрошенных граней " + requestedTotal
                + ", из них прочиталось как плоские " + requested.Count
                + ": прилегание снятой грани к запрошенной проверить нечем");
        }

        var after = PlanarFacePlacements(document);
        foreach (var (reference, placement) in requested)
        {
            var result = FacePlacementMatch.RemovedFaceGone(placement, after);
            if (result.Verdict == FacePlacementMatch.Verdict.Unverified)
            {
                unverified.Add("shell_placement_not_checked — грань " + reference + ": " + result.Detail);
                continue;
            }

            if (result.IsRefusal)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Оболочка сняла НЕ запрошенную грань " + reference + ". " + result.Detail
                    + " Признак создан и остался в дереве, но геометрия не та, которую просил клиент. "
                    + "Удалите признак вызовом kompas_delete_feature по feature_ref и повторите.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["code"] = result.Code,
                        ["feature_ref"] = featureReference,
                        ["requested_face"] = reference,
                        ["requested_area_mm2"] = placement.AreaMm2,
                        ["requested_plane_point_mm"] = placement.Point,
                        ["requested_plane_normal"] = placement.Normal,
                        ["planar_faces_after"] = after.Count,
                    });
            }

            // `passed` repeats the pure rule's own verdict — the comparison of the faces read after the
            // shell with the requested face's area — instead of a literal.
            checks.Add(new NamedCheck("requested_face_removed",
                result.Verdict == FacePlacementMatch.Verdict.Confirmed,
                Observed: result.Detail, Expected: "в плоскости запрошенной грани граней нет"));
        }
    }

    /// <summary>Refuses when the drilled axis did not land on the requested base face.</summary>
    /// <param name="cylinderStartsAtEntrance">True for a blind hole: its cylindrical face runs from the
    /// entrance into the body, so the requested plane must cut it. MEASURED: a through hole's pilot
    /// cylinder is cut by its counterbore or countersink, so its extent legitimately lies wholly on one
    /// side of the requested plane — the extent must not be consulted there.</param>
    /// <param name="conicEntrance">True for <c>through_conic</c>: the axis is read from the CONICAL face,
    /// because a cylinder of the entrance diameter does not exist in that mode.</param>
    private static void CheckHolePlacement(
        DocumentEntry document,
        FacePlacementMatch.PlanarFace? baseFace,
        double diameterMm,
        bool cylinderStartsAtEntrance,
        bool conicEntrance,
        string? featureReference,
        List<NamedCheck> checks,
        List<string> unverified)
    {
        if (baseFace is null)
        {
            unverified.Add(
                "hole_placement_not_checked — опорная грань не плоская либо её поверхность не прочитана: "
                + "прилегание оси к запрошенной грани проверить нечем");
            return;
        }

        var axes = EntranceAxes(document, diameterMm / 2d, conicEntrance);
        if (axes.Count == 0)
        {
            unverified.Add(conicEntrance
                ? "hole_placement_not_checked — конической грани отверстия на теле не найдено: "
                    + "сверять ось не с чем"
                : "hole_placement_not_checked — цилиндрической грани диаметра "
                    + diameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм на теле не найдено: сверять ось не с чем");
            return;
        }

        var results = axes
            .Select(c => (Face: c, Result: FacePlacementMatch.HoleAxisOnFace(
                baseFace, c.Point, c.Direction, c.ExtentMm, cylinderStartsAtEntrance)))
            .ToList();

        var confirmed = results.Where(r => r.Result.Verdict == FacePlacementMatch.Verdict.Confirmed).ToList();
        if (confirmed.Count > 0)
        {
            // `passed` is the rule's verdict on the face being reported, not a literal.
            checks.Add(new NamedCheck("axis_on_requested_face",
                confirmed[0].Result.Verdict == FacePlacementMatch.Verdict.Confirmed,
                Observed: confirmed[0].Result.Detail,
                Expected: "ось отверстия перпендикулярна запрошенной грани и пересекает её"));
            return;
        }

        var refused = results.Where(r => r.Result.IsRefusal).ToList();
        if (refused.Count > 0)
        {
            var measured = results
                .Select(r => (object?)new Dictionary<string, object?>
                {
                    ["axis_face"] = conicEntrance ? "cone" : "cylinder",
                    ["axis_point_mm"] = r.Face.Point,
                    ["axis_direction"] = r.Face.Direction,
                    ["axis_extent_mm"] = r.Face.ExtentMm,
                    ["detail"] = r.Result.Detail,
                })
                .ToList();

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отверстие построено НЕ по запрошенной грани. " + refused[0].Result.Detail
                + " Признак создан и остался в дереве: удалите его вызовом kompas_delete_feature по "
                + "feature_ref и повторите вызов.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["code"] = refused[0].Result.Code,
                    ["feature_ref"] = featureReference,
                    ["requested_face_plane_point_mm"] = baseFace.Point,
                    ["requested_face_plane_normal"] = baseFace.Normal,
                    ["axes_of_entrance"] = measured,
                });
        }

        unverified.Add("hole_placement_not_checked — " + results[0].Result.Detail);
    }

    /// <summary>The planar faces whose plane contains the given edge line. Both endpoints must lie in
    /// the plane, which is what "meets the edge there" means for a plane — the sign of the normal is not
    /// consulted, because the rule takes distances in absolute value.</summary>
    private static List<SurfaceShape> PlanesThrough(
        IReadOnlyList<SurfaceShape> planes, double[] start, double[] end)
    {
        var found = new List<SurfaceShape>();
        foreach (var plane in planes)
        {
            if (!EdgeSurfaceMatch.Unit(plane.Direction, out var normal))
            {
                continue;
            }

            var atStart = Math.Abs(Dot(normal, Subtract(start, plane.Point)));
            var atEnd = Math.Abs(Dot(normal, Subtract(end, plane.Point)));
            if (atStart <= PlaneThroughEdgeToleranceMm && atEnd <= PlaneThroughEdgeToleranceMm)
            {
                found.Add(plane);
            }
        }

        return found;
    }

    /// <summary>The planar faces of the body, each with the plane it reads as. A face that did not read is
    /// absent: the caller names the absence where it matters.</summary>
    private static List<(ksFaceDefinition Face, SurfaceShape Shape)> PlanarFaces(DocumentEntry document)
    {
        var found = new List<(ksFaceDefinition, SurfaceShape)>();
        if (document.Kind != DocumentKind.Part || document.PartNow().GetMainBody() is not ksBody body
            || body.FaceCollection() is not ksFaceCollection faces)
        {
            return found;
        }

        for (var i = 0; i < faces.GetCount(); i++)
        {
            try
            {
                if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is not ksFaceDefinition face
                    || face.GetSurface() is not ksSurface surface
                    || surface.GetSurfaceParam() is not ksPlaneParam plane
                    || plane.GetPlacement() is not ksPlacement placement
                    || !placement.GetOrigin(out var x, out var y, out var z)
                    || !placement.GetVector(2, out var nx, out var ny, out var nz))
                {
                    continue;
                }

                found.Add((face, new SurfaceShape(false, new[] { x, y, z }, new[] { nx, ny, nz }, 0d)));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A face whose plane did not read is absent, and the count stays the count it got.
            }
        }

        return found;
    }

    /// <summary>The planar faces the body did NOT have before the feature was created, with the vertices of
    /// their edges: this is where a chamfer that missed the requested edge actually sits. The plane names
    /// the corner (the legs are known) and the edge vertices name the lines it cuts. A face that did not
    /// read is absent, so an empty list means "no face appeared", not "not checked".</summary>
    private static List<object?> NewPlanarFaces(
        DocumentEntry document, IReadOnlyList<SurfaceShape> before)
    {
        var found = new List<object?>();
        foreach (var (face, shape) in PlanarFaces(document))
        {
            var known = before.Any(b => !b.Cylinder
                && EdgeSurfaceMatch.SamePlane(b.Point, b.Direction, shape.Point, shape.Direction));
            if (known)
            {
                continue;
            }

            found.Add(new Dictionary<string, object?>
            {
                ["plane_point_mm"] = shape.Point,
                ["plane_normal"] = shape.Direction,
                ["area_mm2"] = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm)),
                ["edge_vertices_mm"] = FaceEdgeVertices(face),
            });
        }

        return found;
    }

    /// <summary>The vertices of every edge of ONE face, as [start, end] pairs. An edge that did not read is
    /// left out; the edges read up to a COM break are kept, because a partial picture names more than none.
    /// No deduplication is needed: the edges of one face meet it once.</summary>
    private static List<object?> FaceEdgeVertices(ksFaceDefinition face)
    {
        var found = new List<object?>();
        try
        {
            if (face.EdgeCollection() is not ksEdgeCollection edges)
            {
                return found;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                if (AsInterface<ksEdgeDefinition>(edges.GetByIndex(e)) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                if (EdgeLineOf(edge) is { } line)
                {
                    found.Add(new[] { line.Start, line.End });
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The edges read up to the break stay: what was read is still evidence.
        }

        return found;
    }

    private static double Dot(double[] left, double[] right) =>
        left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

    private static double[] Subtract(double[] left, double[] right) =>
        new[] { left[0] - right[0], left[1] - right[1], left[2] - right[2] };
}
