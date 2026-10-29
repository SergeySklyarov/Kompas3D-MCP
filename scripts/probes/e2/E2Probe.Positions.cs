using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace E2Probe;

/// <summary>Second geometry for the <c>Side</c> positions: an L-section plate with a flange, where a
/// rib across the sketch plane has material to lean on.</summary>
/// <remarks>WHY A SECOND FIXTURE: the first one puts the XOZ plane in the MIDDLE of the plate, so both
/// normals point into material and nothing tells "up/down refused" apart from "nothing to lean on".
/// Here the same XOZ plane is a FACE: one orthogonal side points into material, the other into air.
/// INVARIANT: a refusal is worth nothing without a control - a chain on the same body that DOES build
/// with 0/1 - so every chain variant is measured with all four values.
/// DOC: ksribsideenum.html. History: docs/decisions/ribs.md#side-enum</remarks>
internal sealed partial class E2Probe
{
    private const double FlangePlateLength = 40;
    private const double FlangePlateHeight = 10;
    private const double FlangeWallWidth = 10;
    private const double FlangeWallHeight = 30;

    /// <summary>Chain variants on the L-section body, in world (x, z) millimetres.</summary>
    private static readonly (string Id, double Area, (double X, double Z)[] Points)[] FlangeChains =
    {
        // Along the wall face, then along the plate top: the corner is filled between chain and chord.
        ("corner_on_faces", 200, new[] { (10d, 30d), (10d, 10d), (30d, 10d) }),
        // The same corner, pulled 1.5 mm off both faces - nothing of the chain lies ON the body.
        ("corner_inset", 144.5, new[] { (11.5, 28.5), (11.5, 11.5), (28.5, 11.5) }),
        // The first fixture's V, standing in free space above the plate top.
        ("above_plate", 150, new[] { (15d, 10d), (25d, 25d), (35d, 10d) }),
    };

    /// <summary>Four <c>Side</c> values on three chains and both chain directions.</summary>
    private void PositionMatrix()
    {
        foreach (var (chainId, _, _) in FlangeChains)
        {
            foreach (var reverse in new[] { false, true })
            {
                _reverseChain = reverse;
                foreach (var side in new[] { SideLeft, SideRight, SideUp, SideDown })
                {
                    MeasureFlange($"{chainId}_{(reverse ? "reversed" : "as_drawn")}_side_{side}", side, chainId);
                }
            }
        }

        _reverseChain = false;
    }

    private void MeasureFlange(string id, short side, string chainId)
    {
        var chain = FlangeChains.First(entry => entry.Id == chainId);
        try
        {
            BuildFlangeFixture(chain.Points);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record(id, "фикстура с бортом построена", Describe(ex), "FAIL");
            return;
        }

        var before = State(id + ": до");
        var bodyBox = Gabarit(before);
        var (updated, failure) = CreateRib(side, ThinOneSide, Thickness, 0, 0);
        var after = State(id + ": после");
        var delta = Volume(after) - Volume(before);
        var readBack = ReadRib7();
        // The body sits on ONE side of the sketch plane, so the gabarit names which orthogonal side
        // points into the material - that is the variant the order asks to separate.
        var bodySide = bodyBox is null ? "?" : bodyBox[1] > 1e-6 ? "y > 0" : bodyBox[4] < -1e-6 ? "y < 0" : "оба";
        Record(id, $"прирост {chain.Area * Thickness:0.#} мм3 = S x t (допуск 1 %) при Side={side}, тело {bodySide}",
            failure ?? $"Update={updated}, dV={delta:0.###}, граней {Faces(before)}→{Faces(after)}, "
            + $"тел {after["bodies"]}, габарит y {bodyBox?[1]:0.###}…{bodyBox?[4]:0.###}, API7 {readBack ?? "нет"}",
            "MEASURED");
        CloseCurrent();
    }

    /// <summary>Builds the L-section body and the given rib chain in a fresh document.</summary>
    /// <remarks>INVARIANT: the extrusion sketch is CLOSED and the rib sketch is ONE open chain - the
    /// kernel refuses the opposite at SetSketch, which would read as a failed route instead of a
    /// measured one. MEASURED: the XOZ v axis runs down the world z, so every z is written with the
    /// sign the probe measured in DetectOrientation.</remarks>
    private void BuildFlangeFixture((double X, double Z)[] chain)
    {
        _ribEntity = null;
        _ribSketch = null;
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, false) вернул false");
        }

        var part = _doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(PlaneXoz) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOZ не получена");
        var sign = _flip ? -1 : 1;

        var body = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) тела → null");
        var bodyDefinition = body.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition тела не получен");
        bodyDefinition.SetPlane(plane);
        body.Create();

        var editor = bodyDefinition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() тела → не ksDocument2D");
        editor.ksLineSeg(0, 0, FlangePlateLength, 0, 1);
        editor.ksLineSeg(FlangePlateLength, 0, FlangePlateLength, sign * FlangePlateHeight, 1);
        editor.ksLineSeg(FlangePlateLength, sign * FlangePlateHeight, FlangeWallWidth, sign * FlangePlateHeight, 1);
        editor.ksLineSeg(FlangeWallWidth, sign * FlangePlateHeight, FlangeWallWidth, sign * FlangeWallHeight, 1);
        editor.ksLineSeg(FlangeWallWidth, sign * FlangeWallHeight, 0, sign * FlangeWallHeight, 1);
        editor.ksLineSeg(0, sign * FlangeWallHeight, 0, 0, 1);
        bodyDefinition.EndEdit();

        var feature = part.NewEntity(BossExtrusionType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(body);
        if (!extrusion.SetSideParam(true, 0, Depth, 0, false))
        {
            throw new InvalidOperationException("SetSideParam вернул false");
        }

        feature.Create();

        var sketch = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) ребра → null");
        var definition = sketch.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition ребра не получен");
        definition.SetPlane(plane);
        sketch.Create();

        var ribEditor = definition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() ребра → не ksDocument2D");
        var points = _reverseChain ? chain.Reverse().ToArray() : chain;
        for (var index = 1; index < points.Length; index++)
        {
            ribEditor.ksLineSeg(points[index - 1].X, sign * points[index - 1].Z,
                points[index].X, sign * points[index].Z, 1);
        }

        definition.EndEdit();
        _ribSketch = sketch;

        part.Update();
        _doc5.RebuildDocument();
        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");
    }
}
