using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace E5Probe;

/// <summary>DIRECT_FACES_TANGENT §1.1: does <c>SetFaces(Faces, TangentFaces = true)</c> widen the moved
/// set to the tangent chain, and can the widened set be read back?</summary>
/// <remarks>DOC: <c>ifacemover_setfaces.html</c> — «TangentFaces - добавлять касательные грани».
/// DOC: <c>ifacemover_faces.html</c> — <c>Faces</c> is read-only «Массив граней … VT_ARRAY |
/// VT_DISPATCH», so what the operation holds after <c>SetFaces</c> is readable and is the only place
/// the group size could come from.
/// History: docs/decisions/direct-faces.md#tangent</remarks>
internal sealed partial class E5Probe
{
    // The fixture of this order: the same 40 x 30 x 20 bar with one R3 fillet along BOTH edges of
    // length 40 (top and bottom), so each of the two 40 x 30 faces borders a tangent face and the two
    // 30 x 20 faces do not.
    private const double FilletRadius = 3;
    private const int FilletType = 34;

    // Millimetres, the same unit the product passes to GetArea/GetLength (KompasUnits.LengthMm = 1).
    private const uint LengthMm = 1;
    private const ksLengthUnitsEnum AreaMm = (ksLengthUnitsEnum)1;

    /// <summary>Every variant runs on its OWN fresh body: numbers read after one attempt must not be the
    /// leftovers of the previous one.</summary>
    private void TangentGroupInvestigation()
    {
        // Control: the plain bar has no tangent chain at all. If the group grows here, the flag is
        // honoured on faces that have no tangent neighbours and the filleted fixture proves nothing.
        MoveWithGroup("t0_plain_box_group", "large", tangent: true, withFillet: false);
        MoveWithGroup("t0_plain_box_single", "large", tangent: false, withFillet: false);

        // The face that borders a fillet: 1200 minus the sliver the fillet takes off it.
        MoveWithGroup("t1_fillet_named_single", "large", tangent: false, withFillet: true);
        MoveWithGroup("t2_fillet_named_group", "large", tangent: true, withFillet: true);

        // §1.3: the same flag on a face WITHOUT tangent neighbours — does it change the result?
        MoveWithGroup("t3_fillet_plain_face_group", "small", tangent: true, withFillet: true);
        MoveWithGroup("t3_fillet_plain_face_single", "small", tangent: false, withFillet: true);

        // §1.3: a cylindrical face (the fillet itself); a refusal here is acceptable by the order.
        MoveWithGroup("t4_fillet_cylinder_group", "cylinder", tangent: true, withFillet: true);
    }

    /// <summary>One move on a fresh body, with every number the order asks to compare.</summary>
    private void MoveWithGroup(string id, string pick, bool tangent, bool withFillet)
    {
        try
        {
            BuildBox(record: false);
            if (withFillet)
            {
                BuildFillet();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record(id, "свежее тело построено", Describe(ex), "FAIL");
            CloseCurrent();
            return;
        }

        var before = State(id + ": до");
        var areasBefore = FaceAreas();
        var (face, area) = PickFace(pick);
        if (face is null)
        {
            Record(id, "грань " + pick + " найдена", "не найдена", "FAIL");
            CloseCurrent();
            return;
        }

        bool? set = null;
        int? groupCount = null;
        bool? updated = null;
        string? failure = null;
        try
        {
            var mover = (_doc7?.TopPart as ISurfaceContainer)?.FaceMovers?.Add();
            if (mover is null)
            {
                failure = "FaceMovers.Add() вернул null";
            }
            else
            {
                set = mover.SetFaces(new object[] { face }, tangent);
                groupCount = GroupCount(mover);
                mover.Offset = MoveOffset;
                mover.Direction = true;
                updated = ((IModelObject)mover).Update();
                _doc5!.RebuildDocument();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            failure = Describe(ex);
        }

        var after = State(id + ": после");
        var areasAfter = FaceAreas();
        var delta = Volume(after) - Volume(before);
        Record(id,
            "грань " + pick + " (площадь " + area.ToString("0.###") + "), TangentFaces=" +
            (tangent ? "true" : "false") + ", скругление " + (withFillet ? "есть" : "нет"),
            failure ?? "SetFaces=" + set + ", Faces в операции=" + (groupCount?.ToString() ?? "не прочитан") +
            ", Update=" + updated + ", dV=" + delta.ToString("0.###") + ", площадь по dV=" +
            (delta / MoveOffset).ToString("0.###") + ", граней " + Faces(before) + "→" + Faces(after) +
            ", площадь тела " + Area(before).ToString("0.###") + "→" + Area(after).ToString("0.###") +
            ", площади граней " + string.Join("/", areasBefore) + " → " + string.Join("/", areasAfter),
            "MEASURED");
        CloseCurrent();
    }

    /// <summary>Fillet along the edges of length 40 on both 40 x 30 faces. The route is the product's own
    /// (<c>NewEntity(o3d_fillet)</c> → <c>ksFilletDefinition</c> → <c>array()</c> → <c>Create</c>).</summary>
    /// <remarks>BOTH 40 x 30 faces are filleted on purpose: then the two faces that border a tangent face
    /// have the same area (~1198), and «the face closest to 1200» is unambiguous — with one fillet the
    /// plain 1200 face would win the comparison and the variant would move the wrong face.
    /// History: docs/decisions/direct-faces.md#tangent</remarks>
    private void BuildFillet()
    {
        var part = _doc5?.GetPart(-1) as ksPart
            ?? throw new InvalidOperationException("GetPart(-1) → null");
        var body = (part.BodyCollection() as ksBodyCollection)?.GetByIndex(0) as ksBody
            ?? throw new InvalidOperationException("тело не получено");
        var faces = body.FaceCollection() as ksFaceCollection
            ?? throw new InvalidOperationException("FaceCollection → null");

        // The edges of the two largest faces: the 40 x 30 pair, whose long edges are the ones to fillet.
        var wide = new List<ksFaceDefinition>();
        for (var index = 0; index < faces.GetCount(); index++)
        {
            if (faces.GetByIndex(index) is ksFaceDefinition face
                && Near(face.GetArea(1), Width * Depth, 1e-6))
            {
                wide.Add(face);
            }
        }

        if (wide.Count != 2)
        {
            throw new InvalidOperationException("граней 40 x 30 найдено " + wide.Count + ", ожидалось 2");
        }

        var targets = new List<ksEntity>();
        foreach (var face in wide)
        {
            if (face.EdgeCollection() is not ksEdgeCollection edges)
            {
                throw new InvalidOperationException("EdgeCollection грани → null");
            }

            for (var index = 0; index < edges.GetCount(); index++)
            {
                if (edges.GetByIndex(index) is ksEdgeDefinition edge
                    && Near(edge.GetLength(LengthMm), Width, 1e-6)
                    && edge.GetEntity() is ksEntity entity)
                {
                    targets.Add(entity);
                }
            }
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("рёбер длиной " + Width + " не найдено");
        }

        var feature = part.NewEntity(FilletType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_fillet) → null");
        var definition = feature.GetDefinition() as ksFilletDefinition
            ?? throw new InvalidOperationException("ksFilletDefinition не получен");
        definition.radius = FilletRadius;
        definition.tangent = false;
        var array = definition.array() as ksEntityCollection
            ?? throw new InvalidOperationException("ksFilletDefinition.array() → null");
        foreach (var entity in targets)
        {
            array.Add(entity);
        }

        if (!feature.Create())
        {
            throw new InvalidOperationException("скругление не создано");
        }

        part.Update();
        _doc5!.RebuildDocument();
    }

    /// <summary>Faces of the body with their areas, in model order, rounded for the row text.</summary>
    private List<string> FaceAreas()
    {
        var result = new List<string>();
        foreach (var face in Faces())
        {
            var area = SafeArea(face);
            result.Add(area is double value ? value.ToString("0.##") : "?");
        }

        return result;
    }

    /// <summary>The face to move, chosen by AREA: the interop of v24 exposes neither normals nor
    /// parameter bounds, so "the top face" cannot be asked for, while the areas of this fixture are
    /// distinct numbers (1200 and 600 on the plain bar, ~1198/~798/~600/~188 with the fillet).</summary>
    private (IFace? Face, double Area) PickFace(string pick)
    {
        var target = pick switch
        {
            "large" => Width * Depth,
            "small" => Depth * Height,
            "cylinder" => Math.PI / 2d * FilletRadius * Width,
            _ => double.NaN,
        };

        IFace? best = null;
        var bestArea = double.NaN;
        var bestDelta = double.MaxValue;
        foreach (var face in Faces())
        {
            var area = SafeArea(face);
            if (area is not double value)
            {
                continue;
            }

            var distance = Math.Abs(value - target);
            if (distance < bestDelta)
            {
                bestDelta = distance;
                best = face;
                bestArea = value;
            }
        }

        return (best, bestArea);
    }

    /// <summary>Size of the set the operation holds, read from the documented read-only
    /// <c>IFaceMover.Faces</c>. <c>null</c> when the getter does not answer.</summary>
    private static int? GroupCount(IFaceMover mover)
    {
        try
        {
            var value = mover.Faces;
            return value switch
            {
                null => null,
                Array array => array.Length,
                _ => 1,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeArea(IFace face)
    {
        try
        {
            return face.GetArea(AreaMm);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double Area(JsonObject state) => state["area_mm2"]?.GetValue<double>() ?? double.NaN;
}
