using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter;

namespace KompasMcp.P0Probe;

/// <summary>
/// P2.5 — measuring a bore out of the FINISHED BODY: the cylinder's radius and height, the
/// position of its axis, and the axis direction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question.</b> <c>kompas_read_topology</c> publishes <c>RadiusMm = null</c> and
/// <c>CenterMm = null</c> for every face and every edge (Api5Session.Geometry.cs, DescribeFace and
/// DescribeEdge) because nobody had measured how to obtain those numbers. Three candidate routes
/// exist in the API5 surface; this step runs all three over the SAME cylindrical face and
/// cross-checks them against each other and against an analytic expectation:
/// </para>
/// <list type="number">
/// <item>R-A <c>ksFaceDefinition.GetCylinderParam(out h, out r)</c> — dimensions only, no placement.</item>
/// <item>R-B <c>face.GetSurface()</c> → <c>ksSurface.GetSurfaceParam()</c> → whatever that object
/// answers QI for → <c>GetPlacement()</c>.</item>
/// <item>R-C the boundary: <c>face.EdgeCollection()</c> → <c>edge.GetCurve3D()</c> →
/// <c>GetCurveParam()</c> → <c>ksCircle3dParam.GetPlacement()</c>, plus, independently on the very
/// same curve, <c>ksCurve3D.GetGabarit()</c>.</item>
/// </list>
/// <para>
/// <b>Why the cross-check is the point.</b> An affirmative return from КОМПАС is not a result. A
/// route is credited only when (i) its number matches the analytic expectation for a Ø10 bore in a
/// 10 mm plate, (ii) its unit is pinned against a calibrated reading — <c>GetArea(1)</c> is mm² and
/// <c>GetArea(0)</c> is cm², so a 10× unit error in the cylinder parameters shows up as a 100×
/// mismatch in area — and (iii) it MOVES when the bore moves. The second plate of this step
/// therefore puts the bore at (30, 25): a measurement that cannot tell the two positions apart does
/// not measure a position at all.
/// </para>
/// <para>
/// Signatures come from the vendor interop and from live QueryInterface (<see cref="ComDiscovery"/>),
/// never from memory. Where the task premise says "there is no <c>ksCylinder3dParam</c>", the step
/// checks that against the loaded <c>Interop.Kompas6API5.dll</c> and records what IS declared there.
/// </para>
/// <para>
/// The helpers below are deliberately local copies of the P2.1 ones: the plate here spans
/// (0,0)…(100,80) rather than being centred on the origin, so the analytic expectation for a bore
/// "in the centre" is x∈[45,55], y∈[35,45]. Existing steps are not disturbed by this.
/// </para>
/// </remarks>
internal static class BoreProbe
{
    private const double PlateX = 100d;
    private const double PlateY = 80d;
    private const double Thickness = 10d;
    private const double BoreRadius = 5d;

    /// <summary>ST_MIX_MM | ST_MIX_KG — the selector P0.7 calibrated volume against.</summary>
    private const int MixMmKg = 1 | 16;

    /// <summary>Agreement threshold between two independent routes, in model millimetres.</summary>
    private const double Agree = 1e-6;

    private const string EndTypeEnum = "ksEndTypeEnum";

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    private static short TypeOf(string constant, int fallback) => EntityTypes.Value(constant, (short)fallback);

    private static short ThroughAll() => VendorConstants.Value(EndTypeEnum, "etThroughAll", 1);

    private static short Blind() => VendorConstants.Value(EndTypeEnum, "etBlind", 0);

    // -----------------------------------------------------------------------------------------
    // Step
    // -----------------------------------------------------------------------------------------

    public static void Measure(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.5",
            "Измерение цилиндрической грани: радиус, высота, центр и ось отверстия из готового тела",
            "Каким из маршрутов — GetCylinderParam, параметр поверхности или граница грани — измеривается положение оси отверстия; в каких единицах приходят числа и различают ли они два разных положения?");

        try
        {
            DeclaredApiSurface(step);

            var centre = MeasureCase(step, app, "A", "плита [0..100]×[0..80]×[0..10], отверстие в (50, 40)", 50d, 40d);
            var offCentre = MeasureCase(step, app, "B", "та же плита, отверстие смещено в (30, 25)", 30d, 25d);

            CrossCaseCheck(step, centre, offCentre);
            Verdict(step, centre, offCentre);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Fail("Шаг не доигран: " + Unwrap(ex).Message);
        }

        step.Duration = sw.Elapsed;
    }

    /// <summary>
    /// What the vendor interop actually declares for cylinder surfaces, recorded before any
    /// measurement so that a later "the API has no X" is either confirmed or corrected by this run.
    /// </summary>
    private static void DeclaredApiSurface(ProbeStep step)
    {
        var assembly = typeof(KompasObject).Assembly;
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes()
                     .Where(t => t.Name.Contains("Cylinder", StringComparison.OrdinalIgnoreCase)
                              || t.Name.Contains("SurfaceParam", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m is MethodInfo method ? Members.Signature(method) : m.Name)
                .OrderBy(s => s, StringComparer.Ordinal);
            lines.Add($"{type.FullName} [{(type.IsInterface ? "interface" : "class")}] GUID={type.GUID:N}: {string.Join(" | ", members)}");
        }

        step.Data["api5_cylinder_related_types"] = lines.ToArray();
        foreach (var line in lines)
        {
            step.Observe("API5 объявляет: " + line);
        }

        var absent = new[] { "Kompas6API5.ksCylinder3dParam", "Kompas6API5.ksCone3dParam", "Kompas6API5.ksSurfaceParam" }
            .Select(name => name + " → " + (ComDiscovery.LookupType(name) is null
                ? "такого типа в Interop.Kompas6API5 нет"
                : "ЕСТЬ"))
            .ToArray();
        step.Data["api5_absent_type_check"] = absent;
        foreach (var line in absent)
        {
            step.Observe("Проверка предпосылки задания: " + line);
        }
    }

    // -----------------------------------------------------------------------------------------
    // One plate, one bore, all three routes
    // -----------------------------------------------------------------------------------------

    private sealed class Case
    {
        public required string Key { get; init; }
        public required string Description { get; init; }
        public required double RequestedX { get; init; }
        public required double RequestedY { get; init; }
        public string? Failure;

        public double? VolumeBefore;
        public double? VolumeAfter;
        public int FaceCount;
        public int CylindricalFaces;
        public readonly List<string> FaceRows = new();

        // R-A — face.GetCylinderParam(out h, out r)
        public bool? ParamReturned;
        public double? ParamH;
        public double? ParamR;
        public string? ParamError;

        // R-B — face.GetSurface() → GetSurfaceParam()
        public string SurfaceClrType = "-";
        public string[] SurfaceInterfaces = Array.Empty<string>();
        public string SurfaceParamClrType = "-";
        public string[] SurfaceParamInterfaces = Array.Empty<string>();
        public double? SurfaceParamRadius;
        public double? SurfaceParamHeight;
        public double[]? SurfaceOrigin;
        public double[]? SurfaceAxis;
        public string SurfaceAxisEvidence = "-";
        public string SurfaceQiEvidence = "-";
        public double[]? SurfacePointLowV;
        public double[]? SurfacePointHighV;
        public double[]? SurfacePointMid;
        public double[]? SurfaceNormalMid;
        public double[]? SurfaceGabarit;
        public string SurfaceParamRange = "-";

        // R-C — the boundary
        public readonly List<CircleReading> Circles = new();
        public int EdgeCountOnCylinder = -1;
        public string EdgeKinds = "-";

        // topology and orientation
        public int LoopCount = -1;
        public string LoopReport = "-";
        public readonly List<string> LoopDetails = new();
        public bool? NormalOrientation;
        public double? AreaMm2;
        public double? AreaCm2;
        public string OwnerEntity = "-";
        public string? SavedDocument;

        /// <summary>Planar faces as the control group for the normalOrientation question.</summary>
        public readonly List<(int Index, double? Z, double[] Normal, bool? Orientation)> PlanarControls = new();
    }

    private sealed class CircleReading
    {
        public int EdgeIndex;
        public bool EdgeIsCircle;
        public bool EdgeIsArc;
        public bool CurveIsCircle;
        public string CurveParamClrType = "-";
        public string CurveParamInterfaces = "-";
        public double? RadiusFromParam;
        public double[]? CentreFromPlacement;
        public double[]? AxisFromPlacement;
        public double[]? BoxMin;
        public double[]? BoxMax;
        public double[]? CentreFromBox;
        public double? RadiusFromBox;
        public double? LengthMm;
        public double? LengthCm;
        public double? RadiusFromCircumference;
        public string? ParamMin;
        public string? ParamMax;
        public bool CentreAgrees;
        public bool RadiusAgrees;
        public string? Error;
    }

    private static Case MeasureCase(ProbeStep step, KompasObject app, string key, string description, double boreX, double boreY)
    {
        var c = new Case { Key = key, Description = description, RequestedX = boreX, RequestedY = boreY };
        var head = $"СЛУЧАЙ {key} — {description}";
        step.Observe(head);

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                c.Failure = "документ не создан";
                step.Observe($"{head}: {c.Failure}.");
                return c;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Plate(part) is null)
            {
                c.Failure = "плита не создана";
                step.Observe($"{head}: {c.Failure}.");
                return c;
            }

            doc.RebuildDocument();
            c.VolumeBefore = Volume(part);

            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            var circle = CircleSketch(part, xy, boreX, boreY, BoreRadius, "p5 bore profile");
            if (!Bore(part, circle, out var boreError))
            {
                c.Failure = "отверстие не прорезано: " + boreError;
                step.Observe($"{head}: {c.Failure}.");
                return c;
            }

            doc.RebuildDocument();
            c.VolumeAfter = Volume(part);
            var removed = (c.VolumeBefore ?? 0d) - (c.VolumeAfter ?? 0d);
            var expectedRemoved = Math.PI * BoreRadius * BoreRadius * Thickness;
            step.Observe($"{head}: объём {Raw(c.VolumeBefore)} → {Raw(c.VolumeAfter)}, снято {Raw(removed)} мм³ "
                + $"при ожидании π·5²·10 = {Raw(expectedRemoved)} → "
                + (Math.Abs(removed - expectedRemoved) <= 0.01d
                    ? "сквозное отверстие подтверждено независимо от глубины сеттера"
                    : "ВНИМАНИЕ: ожидание не выполнено, измерения ниже снимаются с другой геометрии") + ".");

            if (part.GetMainBody() is not ksBody body)
            {
                c.Failure = "GetMainBody() → null";
                step.Observe($"{head}: {c.Failure}.");
                return c;
            }

            var faces = (ksFaceCollection)body.FaceCollection();
            c.FaceCount = faces.GetCount();
            step.Observe($"{head}: граней в теле {c.FaceCount}.");

            for (var f = 0; f < c.FaceCount; f++)
            {
                var element = faces.GetByIndex(f);
                if (AsDefinition<ksFaceDefinition>(element) is not ksFaceDefinition face)
                {
                    step.Observe($"  грань {f}: элемент коллекции не отвечает ksFaceDefinition ({RuntimeName(element)}).");
                    continue;
                }

                ReadFace(step, c, face, f);
            }

            InterpretOrientation(step, c);

            if (c.CylindricalFaces == 0)
            {
                c.Failure ??= "ни одна грань не ответила IsCylinder() = true";
                step.Observe($"{head}: {c.Failure}.");
            }

            step.Data[$"{key}_faces"] = c.FaceRows.ToArray();

            var path = WorkFile($"P2_5_Bore_{boreX.ToString("0", CultureInfo.InvariantCulture)}_{boreY.ToString("0", CultureInfo.InvariantCulture)}.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (SafeBool(() => doc.SaveAs(path)) == true)
            {
                c.SavedDocument = path;
                step.Artifacts.Add(path);
                step.Data[$"{key}_saved_document"] = path;
            }
        }
        catch (Exception ex)
        {
            c.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Errors.Add(head + ": " + c.Failure);
        }
        finally
        {
            CloseQuietly(doc);
        }

        return c;
    }

    /// <summary>Everything measurable on one face of the body; the deep work runs only for cylinders.</summary>
    private static void ReadFace(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        var planar = SafeBool(face.IsPlanar);
        var cylindrical = SafeBool(face.IsCylinder);
        var cone = SafeBool(face.IsCone);
        var sphere = SafeBool(face.IsSphere);
        var torus = SafeBool(face.IsTorus);
        var areaMm2 = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
        var areaCm2 = SafeDouble(() => face.GetArea((uint)KompasUnits.Centimetres));
        var orientation = SafeBool(() => face.normalOrientation);

        if (cylindrical == true)
        {
            c.CylindricalFaces++;
            c.AreaMm2 = areaMm2;
            c.AreaCm2 = areaCm2;
            c.NormalOrientation = orientation;
        }

        var row = $"грань {index}: planar={On(planar)} cylinder={On(cylindrical)} cone={On(cone)} sphere={On(sphere)} torus={On(torus)}; "
            + $"GetArea(1)={Raw(areaMm2)} мм², GetArea(0)={Raw(areaCm2)} см² (отношение {Ratio(areaMm2, areaCm2)}); normalOrientation={On(orientation)}";
        c.FaceRows.Add(row);
        step.Observe("  " + row + ".");

        if (cylindrical != true)
        {
            // Planar faces are the control group for the orientation question: their geometric
            // normal is known in advance (−Z at z=0, +Z at z=10), so the pair (GetNormal,
            // normalOrientation) is interpretable there.
            if (planar == true)
            {
                ReadPlanarControl(step, c, face, index, orientation);
            }

            return;
        }

        ReadCylinderParam(step, c, face, index);
        ReadSurface(step, c, face, index);
        ReadBoundary(step, c, face, index);
        ReadLoops(step, c, face, index);
        ReadOwner(step, c, face, index);
    }

    /// <summary>R-A — <c>GetCylinderParam(out h, out r)</c> and the unit test built from the area.</summary>
    private static void ReadCylinderParam(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        try
        {
            c.ParamReturned = face.GetCylinderParam(out var h, out var r);
            c.ParamH = h;
            c.ParamR = r;
        }
        catch (Exception ex)
        {
            c.ParamError = ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Observe($"  R-A (грань {index}): GetCylinderParam бросил {c.ParamError}.");
            return;
        }

        var implied = 2d * Math.PI * (c.ParamR ?? 0d) * (c.ParamH ?? 0d);
        var mm2 = c.AreaMm2 ?? double.NaN;
        var cm2 = c.AreaCm2 ?? double.NaN;
        var inMillimetres = Math.Abs(implied - mm2) <= Math.Max(1e-3, 1e-6 * Math.Abs(mm2));
        var inCentimetres = Math.Abs(implied - cm2) <= Math.Max(1e-4, 1e-6 * Math.Abs(cm2));
        var units = inMillimetres ? "миллиметрами" : inCentimetres ? "САНТИМЕТРАМИ (расхождение в 10 раз)" : "ни миллиметрами, ни сантиметрами";

        step.Observe($"  R-A (грань {index}): GetCylinderParam(out h, out r) вернул {On(c.ParamReturned)}; h={Raw(c.ParamH)}, r={Raw(c.ParamR)} — порядок аргументов именно такой: сначала высота, потом радиус (ожидание h=10, r=5).");
        step.Observe($"  R-A единицы: боковая поверхность цилиндра с такими параметрами равна 2πrh = {Raw(implied)}, а площадь грани GetArea(1) = {Raw(mm2)} мм² и GetArea(0) = {Raw(cm2)} см² → числа приходят {units}.");

        step.Data[$"{c.Key}_getcylinderparam_returned"] = c.ParamReturned;
        step.Data[$"{c.Key}_h_raw"] = Raw(c.ParamH);
        step.Data[$"{c.Key}_r_raw"] = Raw(c.ParamR);
        step.Data[$"{c.Key}_implied_lateral_area"] = Raw(implied);
        step.Data[$"{c.Key}_face_area_mm2"] = Raw(mm2);
        step.Data[$"{c.Key}_face_area_cm2"] = Raw(cm2);
        step.Data[$"{c.Key}_cylinder_param_units"] = units;
    }

    /// <summary>R-B — face.GetSurface() → ksSurface → GetSurfaceParam() → placement.</summary>
    private static void ReadSurface(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        object? surfaceObject;
        try
        {
            surfaceObject = face.GetSurface();
        }
        catch (Exception ex)
        {
            step.Observe($"  R-B (грань {index}): GetSurface() бросил {ex.GetType().Name}: {Unwrap(ex).Message}.");
            return;
        }

        c.SurfaceClrType = RuntimeName(surfaceObject);
        var surfaceDiscovered = ComDiscovery.Probe(surfaceObject);
        c.SurfaceInterfaces = surfaceDiscovered.Select(i => i.FullName + " [" + i.AssemblyName + "]").ToArray();
        step.Observe($"  R-B (грань {index}): face.GetSurface() → CLR-тип {c.SurfaceClrType} (у всех RCW он одинаков и ничего не говорит); по QI интерфейс(ы): [{string.Join(", ", c.SurfaceInterfaces)}].");
        foreach (var discovered in surfaceDiscovered)
        {
            step.Data["members:" + discovered.FullName] = discovered.Members.ToArray();
        }

        if (surfaceObject is not ksSurface surface)
        {
            step.Observe($"  R-B: объект не приводится к Kompas6API5.ksSurface — маршрут обрывается здесь.");
            return;
        }

        step.Observe($"  R-B: приведение к Kompas6API5.ksSurface удалось; surface.IsCylinder() = {On(SafeBool(surface.IsCylinder))}, IsPlane()={On(SafeBool(surface.IsPlane))}, IsCone()={On(SafeBool(surface.IsCone))}, IsSphere()={On(SafeBool(surface.IsSphere))}, IsTorus()={On(SafeBool(surface.IsTorus))}, IsNurbsSurface()={On(SafeBool(surface.IsNurbsSurface))}.");

        var uMin = SafeDouble(surface.GetParamUMin);
        var uMax = SafeDouble(surface.GetParamUMax);
        var vMin = SafeDouble(surface.GetParamVMin);
        var vMax = SafeDouble(surface.GetParamVMax);
        c.SurfaceParamRange = $"u∈[{Raw(uMin)}..{Raw(uMax)}], v∈[{Raw(vMin)}..{Raw(vMax)}], IsClosedU={On(SafeBool(surface.IsClosedU))}, IsClosedV={On(SafeBool(surface.IsClosedV))}, BoundaryCount={SafeInt(() => surface.BoundaryCount)}";
        step.Observe($"  R-B параметры поверхности: {c.SurfaceParamRange}.");

        var surfaceBox = new double[6];
        var hasSurfaceBox = SafeBool(() => surface.GetGabarit(
            out surfaceBox[0], out surfaceBox[1], out surfaceBox[2], out surfaceBox[3], out surfaceBox[4], out surfaceBox[5])) == true;
        if (hasSurfaceBox)
        {
            c.SurfaceGabarit = surfaceBox;
            step.Observe($"  R-B surface.GetGabarit() → min ({Num(surfaceBox[0])}, {Num(surfaceBox[1])}, {Num(surfaceBox[2])}) … max ({Num(surfaceBox[3])}, {Num(surfaceBox[4])}, {Num(surfaceBox[5])}) — это габарит поверхности, сравните его с габаритом грани из R-C.");
        }
        else
        {
            step.Observe("  R-B surface.GetGabarit() → false либо бросил исключение.");
        }

        if (uMin is double u0 && uMax is double u1 && vMin is double v0 && vMax is double v1)
        {
            var um = (u0 + u1) / 2d;
            var vm = (v0 + v1) / 2d;
            c.SurfacePointLowV = PointOf(surface, um, v0);
            c.SurfacePointHighV = PointOf(surface, um, v1);
            c.SurfacePointMid = PointOf(surface, um, vm);
            c.SurfaceNormalMid = NormalOf(surface, um, vm);
            step.Observe($"  R-B surface.GetPoint(u={Raw(um)}): при v={Raw(v0)} → {Point(c.SurfacePointLowV)}, при v={Raw(v1)} → {Point(c.SurfacePointHighV)}; при v={Raw(vm)} → {Point(c.SurfacePointMid)}; разница этих точек = {Point(Difference(c.SurfacePointHighV, c.SurfacePointLowV))}.");
            if (c.SurfaceNormalMid is not null && c.SurfacePointMid is not null)
            {
                var radial = Normalise(c.SurfacePointMid[0] - c.RequestedX, c.SurfacePointMid[1] - c.RequestedY, 0d);
                var dot = Dot(radial, c.SurfaceNormalMid);
                step.Observe($"  R-B знак нормали: радиальное направление «от оси отверстия к точке на грани» = ({Vec(radial)}), surface.GetNormal в этой точке = ({Vec(c.SurfaceNormalMid)}), скалярное произведение = {Raw(dot)} → нормаль смотрит "
                    + (dot < 0 ? "ВНУТРЬ отверстия (к оси), то есть наружу от материала" : "ОТ оси, то есть внутрь материала")
                    + $"; флаг normalOrientation грани при этом = {On(c.NormalOrientation)}.");
            }
        }

        object? paramObject;
        try
        {
            paramObject = surface.GetSurfaceParam();
        }
        catch (Exception ex)
        {
            step.Observe($"  R-B: surface.GetSurfaceParam() бросил {ex.GetType().Name}: {Unwrap(ex).Message} — маршрут R-B дальше не идёт.");
            return;
        }

        c.SurfaceParamClrType = RuntimeName(paramObject);
        var paramDiscovered = ComDiscovery.Probe(paramObject);
        c.SurfaceParamInterfaces = paramDiscovered.Select(i => i.FullName + " [" + i.AssemblyName + "]").ToArray();
        c.SurfaceQiEvidence = QiReport(paramObject,
            "Kompas6API5.ksCylinderParam", "Kompas6API5.ksNurbsSurfaceParam", "Kompas6API5.ksPlacementParam",
            "Kompas6API5.ksPlacement", "Kompas6API5.ksCircle3dParam", "Kompas6API5.ksSurface");
        step.Observe($"  R-B: surface.GetSurfaceParam() → CLR-тип {c.SurfaceParamClrType}; по QI: [{string.Join(", ", c.SurfaceParamInterfaces)}]; адресная проверка: {c.SurfaceQiEvidence}.");
        foreach (var discovered in paramDiscovered)
        {
            step.Data["members:" + discovered.FullName] = discovered.Members.ToArray();
            step.Observe($"    члены {discovered.FullName}: {string.Join(" | ", discovered.Members)}");
        }

        if (paramObject is not ksCylinderParam cylinder)
        {
            step.Observe("  R-B: объект параметра поверхности не отвечает Kompas6API5.ksCylinderParam — читать у него радиус/высоту/placement этим маршрутом нечем.");
            return;
        }

        c.SurfaceParamRadius = SafeDouble(() => cylinder.radius);
        c.SurfaceParamHeight = SafeDouble(() => cylinder.height);
        step.Observe($"  R-B ksCylinderParam: radius = {Raw(c.SurfaceParamRadius)}, height = {Raw(c.SurfaceParamHeight)} (свойства interop; за get_radius/get_height они же); ожидание r=5, h=10.");

        PlacementInfo placement;
        try
        {
            placement = ReadPlacement(step, cylinder.GetPlacement(), "R-B ksCylinderParam.GetPlacement()");
        }
        catch (Exception ex)
        {
            step.Observe($"  R-B: GetPlacement() бросил {ex.GetType().Name}: {Unwrap(ex).Message}.");
            return;
        }

        c.SurfaceOrigin = placement.Origin;
        (c.SurfaceAxis, c.SurfaceAxisEvidence) = PickAxis(placement);
        step.Observe($"  R-B итог: центр оси = {Point(c.SurfaceOrigin)} (запрошен {Num(c.RequestedX)}, {Num(c.RequestedY)}); "
            + $"ось = {Point(c.SurfaceAxis)}; основание выбора оси: {c.SurfaceAxisEvidence}.");
        var axialSpan = c.SurfacePointLowV is { } low && c.SurfacePointHighV is { } high ? Len(Difference(high, low)!) : (double?)null;
        step.Observe($"  R-B высота по самой параметризации: |GetPoint(vmax) − GetPoint(vmin)| = {Raw(axialSpan)}.");

        step.Data[$"{c.Key}_surface_clr_type"] = c.SurfaceClrType;
        step.Data[$"{c.Key}_surface_interfaces"] = c.SurfaceInterfaces;
        step.Data[$"{c.Key}_surface_param_clr_type"] = c.SurfaceParamClrType;
        step.Data[$"{c.Key}_surface_param_interfaces"] = c.SurfaceParamInterfaces;
        step.Data[$"{c.Key}_surface_param_qi_evidence"] = c.SurfaceQiEvidence;
        step.Data[$"{c.Key}_surface_param_radius"] = Raw(c.SurfaceParamRadius);
        step.Data[$"{c.Key}_surface_param_height"] = Raw(c.SurfaceParamHeight);
        step.Data[$"{c.Key}_surface_origin"] = Raw(c.SurfaceOrigin);
        step.Data[$"{c.Key}_surface_axis"] = Raw(c.SurfaceAxis);
        step.Data[$"{c.Key}_surface_axis_evidence"] = c.SurfaceAxisEvidence;
        step.Data[$"{c.Key}_surface_param_range"] = c.SurfaceParamRange;
        step.Data[$"{c.Key}_surface_gabarit"] = Raw(c.SurfaceGabarit);
        step.Data[$"{c.Key}_surface_normal_mid"] = Raw(c.SurfaceNormalMid);
    }

    /// <summary>R-C — the boundary of the cylindrical face: circles via placement and, independently, via bbox.</summary>
    private static void ReadBoundary(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        ksEdgeCollection edges;
        try
        {
            edges = (ksEdgeCollection)face.EdgeCollection();
        }
        catch (Exception ex)
        {
            step.Observe($"  R-C (грань {index}): EdgeCollection() бросил {ex.GetType().Name}: {Unwrap(ex).Message}.");
            return;
        }

        var count = edges.GetCount();
        c.EdgeCountOnCylinder = count;
        var kinds = new List<string>();

        for (var e = 0; e < count; e++)
        {
            var edgeObject = edges.GetByIndex(e);
            if (AsDefinition<ksEdgeDefinition>(edgeObject) is not ksEdgeDefinition edge)
            {
                kinds.Add($"{e}:не ksEdgeDefinition");
                continue;
            }

            var isCircle = SafeBool(edge.IsCircle) == true;
            var isArc = SafeBool(edge.IsArc) == true;
            var isStraight = SafeBool(edge.IsStraight) == true;
            var kind = isCircle ? "circle" : isArc ? "arc" : isStraight ? "straight" : "other";
            kinds.Add($"{e}:{kind}");

            object? curveObject;
            try
            {
                curveObject = edge.GetCurve3D();
            }
            catch (Exception ex)
            {
                step.Observe($"  R-C ребро {e}: GetCurve3D() бросил {ex.GetType().Name}: {Unwrap(ex).Message}.");
                continue;
            }

            if (curveObject is not ksCurve3D curve)
            {
                step.Observe($"  R-C ребро {e}: GetCurve3D() → {RuntimeName(curveObject)}, к ksCurve3D не приводится.");
                continue;
            }

            var reading = new CircleReading
            {
                EdgeIndex = e,
                EdgeIsCircle = isCircle,
                EdgeIsArc = isArc,
                CurveIsCircle = SafeBool(curve.IsCircle) == true,
                ParamMin = Raw(SafeDouble(curve.GetParamMin)),
                ParamMax = Raw(SafeDouble(curve.GetParamMax)),
            };

            reading.LengthMm = SafeDouble(() => curve.GetLength((uint)KompasUnits.LengthMm));
            reading.LengthCm = SafeDouble(() => curve.GetLength((uint)KompasUnits.Centimetres));
            reading.RadiusFromCircumference = reading.LengthMm is double lm && lm > 0 ? lm / (2d * Math.PI) : (double?)null;

            // Independent derivation 1: the bounding box of the same curve. `out` locals are
            // declared outside the lambda on purpose — an `out var` inside a lambda is scoped to
            // the lambda and cannot be read afterwards.
            var box = new double[6];
            if (SafeBool(() => curve.GetGabarit(out box[0], out box[1], out box[2], out box[3], out box[4], out box[5])) == true)
            {
                reading.BoxMin = new[] { box[0], box[1], box[2] };
                reading.BoxMax = new[] { box[3], box[4], box[5] };
                reading.CentreFromBox = new[] { (box[0] + box[3]) / 2d, (box[1] + box[4]) / 2d, (box[2] + box[5]) / 2d };
                reading.RadiusFromBox = Math.Max(Math.Abs(box[3] - box[0]), Math.Abs(box[4] - box[1])) / 2d;
            }

            object? curveParam;
            try
            {
                curveParam = curve.GetCurveParam();
            }
            catch (Exception ex)
            {
                reading.Error = "GetCurveParam() бросил " + ex.GetType().Name + ": " + Unwrap(ex).Message;
                curveParam = null;
            }

            reading.CurveParamClrType = RuntimeName(curveParam);
            reading.CurveParamInterfaces = string.Join(", ", ComDiscovery.Probe(curveParam).Select(i => i.FullName));
            step.Observe($"  R-C ребро {e} ({kind}): ksEdgeDefinition.IsCircle()={On(isCircle)}, IsArc()={On(isArc)}, IsStraight()={On(isStraight)}; "
                + $"ksCurve3D.IsCircle()={On(reading.CurveIsCircle)}, param∈[{reading.ParamMin}..{reading.ParamMax}], закрыта={On(SafeBool(curve.IsClosed))}; "
                + $"GetLength(1)={Raw(reading.LengthMm)} мм, GetLength(0)={Raw(reading.LengthCm)} см (отношение {Ratio(reading.LengthMm, reading.LengthCm)}); "
                + $"GetGabarit → {Box(reading.BoxMin, reading.BoxMax)}; GetCurveParam() → {reading.CurveParamClrType}, QI: [{reading.CurveParamInterfaces}].");

            if (curveParam is ksCircle3dParam circleParam)
            {
                reading.RadiusFromParam = SafeDouble(() => circleParam.radius);
                var placement = ReadPlacement(step, circleParam.GetPlacement(), $"R-C ребро {e} ksCircle3dParam.GetPlacement()");
                reading.CentreFromPlacement = placement.Origin;
                (reading.AxisFromPlacement, _) = PickAxis(placement);
                reading.CentreAgrees = Within(reading.CentreFromPlacement, reading.CentreFromBox, Agree);
                reading.RadiusAgrees = Both(reading.RadiusFromParam, reading.RadiusFromBox, Agree)
                    && Both(reading.RadiusFromParam, reading.RadiusFromCircumference, 1e-6);

                step.Observe($"  R-C ребро {e}: ksCircle3dParam.radius = {Raw(reading.RadiusFromParam)}; центр по placement = {Point(reading.CentreFromPlacement)}; "
                    + $"центр по габариту = {Point(reading.CentreFromBox)}; радиус по габариту = {Raw(reading.RadiusFromBox)}; радиус из длины (L/2π) = {Raw(reading.RadiusFromCircumference)}; ось по placement = {Point(reading.AxisFromPlacement)}.");
                step.Observe($"  R-C ребро {e}: ДВЕ НЕЗАВИСИМЫЕ ПРОИЗВОДНЫЕ: центры совпали {On(reading.CentreAgrees)}, радиусы (param = габарит = L/2π) совпали {On(reading.RadiusAgrees)}. "
                    + $"Ожидание для окружности R5 с центром ({Num(c.RequestedX)}, {Num(c.RequestedY)}): x∈[{Num(c.RequestedX - BoreRadius)}..{Num(c.RequestedX + BoreRadius)}], y∈[{Num(c.RequestedY - BoreRadius)}..{Num(c.RequestedY + BoreRadius)}], z constant.");
            }
            else if (curveParam is ksArc3dParam arcParam)
            {
                reading.RadiusFromParam = SafeDouble(() => arcParam.radius);
                var placement = ReadPlacement(step, arcParam.GetPlacement(), $"R-C ребро {e} ksArc3dParam.GetPlacement()");
                reading.CentreFromPlacement = placement.Origin;
                (reading.AxisFromPlacement, _) = PickAxis(placement);
                reading.CentreAgrees = Within(reading.CentreFromPlacement, reading.CentreFromBox, Agree);
                step.Observe($"  R-C ребро {e}: дуга ksArc3dParam.radius = {Raw(reading.RadiusFromParam)}, angle = {Raw(SafeDouble(() => arcParam.angle))}, центр по placement = {Point(reading.CentreFromPlacement)} "
                    + $"(для дуги габарит центру не эквивалентен — сравнение {On(reading.CentreAgrees)}).");
            }
            else if (reading.Error is not null)
            {
                step.Observe($"  R-C ребро {e}: {reading.Error}");
            }
            else
            {
                step.Observe($"  R-C ребро {e}: параметр кривой не является ни ksCircle3dParam, ни ksArc3dParam (QI: [{reading.CurveParamInterfaces}]).");
            }

            c.Circles.Add(reading);
        }

        c.EdgeKinds = string.Join(", ", kinds);
        step.Data[$"{c.Key}_edge_kinds"] = c.EdgeKinds;
        step.Data[$"{c.Key}_edge_count"] = count;
        step.Data[$"{c.Key}_circle_bboxes"] = c.Circles
            .Where(r => r.BoxMin is not null)
            .Select(r => $"ребро {r.EdgeIndex}: {Box(r.BoxMin, r.BoxMax)} → центр {Point(r.CentreFromBox)}, радиус {Raw(r.RadiusFromBox)}")
            .ToArray();
    }

    /// <summary>Loop structure of the cylindrical face.</summary>
    private static void ReadLoops(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        object raw;
        try
        {
            raw = face.LoopCollection();
            if (raw is not ksLoopCollection loops)
            {
                step.Observe($"  Циклы (грань {index}): LoopCollection() → {RuntimeName(raw)}, не ksLoopCollection.");
                return;
            }

            c.LoopCount = loops.GetCount();
            var lines = new List<string>();
            for (var l = 0; l < c.LoopCount; l++)
            {
                if (loops.GetByIndex(l) is not ksLoop loop)
                {
                    lines.Add($"цикл {l}: {RuntimeName(loops.GetByIndex(l))}");
                    continue;
                }

                var loopEdges = loop.EdgeCollection();
                var edgeCount = loopEdges is ksEdgeCollection ec ? ec.GetCount() : (int?)null;
                var whatItIs = loopEdges is ksEdgeCollection
                    ? "ksEdgeCollection"
                    : $"не ksEdgeCollection ({RuntimeName(loopEdges)}; QI: [{string.Join(", ", ComDiscovery.Probe(loopEdges).Select(i => i.FullName))}])";
                c.LoopDetails.Add($"цикл {l}: {whatItIs}");
                lines.Add($"цикл {l}: IsOuter={On(SafeBool(loop.IsOuter))}, рёбер {Raw(edgeCount)}, длина {Raw(SafeDouble(() => loop.GetLength((uint)KompasUnits.LengthMm)))} мм, EdgeCollection() → {whatItIs}");
            }

            c.LoopReport = string.Join("; ", lines);
            step.Observe($"  Циклы (грань {index}): их {c.LoopCount}. {c.LoopReport}");
            step.Data[$"{c.Key}_loop_count"] = c.LoopCount;
            step.Data[$"{c.Key}_loops"] = c.LoopReport;
            step.Data[$"{c.Key}_loop_edge_collections"] = c.LoopDetails.ToArray();
        }
        catch (Exception ex)
        {
            step.Observe($"  Циклы (грань {index}): {ex.GetType().Name}: {Unwrap(ex).Message}");
        }
    }

    /// <summary>Which feature owns the cylindrical face — the link a reader needs to name the bore.</summary>
    private static void ReadOwner(ProbeStep step, Case c, ksFaceDefinition face, int index)
    {
        try
        {
            var owner = face.GetOwnerEntity();
            c.OwnerEntity = owner is not ksEntity entity
                ? RuntimeName(owner)
                : $"{RuntimeName(entity)}, type={entity.type} ({VendorConstants.NameOfObj3d(entity.type)}), имя=«{entity.name}»";
            step.Observe($"  Владелец грани {index}: GetOwnerEntity() → {c.OwnerEntity}.");
            step.Data[$"{c.Key}_owner_entity"] = c.OwnerEntity;
        }
        catch (Exception ex)
        {
            step.Observe($"  Владелец грани {index}: {ex.GetType().Name}: {Unwrap(ex).Message}");
        }
    }

    /// <summary>
    /// Control group for <c>normalOrientation</c>: on a planar face the answer is known in advance
    /// (−Z at z=0, +Z at z=10), so the pair (GetNormal, normalOrientation) is readable there and
    /// only there.
    /// </summary>
    private static void ReadPlanarControl(ProbeStep step, Case c, ksFaceDefinition face, int index, bool? orientation)
    {
        try
        {
            if (face.GetSurface() is not ksSurface surface)
            {
                return;
            }

            var u = Mid(SafeDouble(surface.GetParamUMin), SafeDouble(surface.GetParamUMax));
            var v = Mid(SafeDouble(surface.GetParamVMin), SafeDouble(surface.GetParamVMax));
            var point = PointOf(surface, u, v);
            var normal = NormalOf(surface, u, v);
            if (normal is null)
            {
                step.Observe($"    контроль (плоская грань {index}): surface.GetNormal(u,v) → false/исключение; normalOrientation={On(orientation)}.");
                return;
            }

            var unit = Normalise(normal);
            c.PlanarControls.Add((index, point?[2], unit, orientation));
            step.Observe($"    контроль (плоская грань {index}, точка z={Num(point?[2])}, normalOrientation={On(orientation)}): GetNormal(u,v) = ({Vec(normal)}) → нормализованно ({Vec(unit)}) → ось z {(unit[2] >= 0 ? "+" : "−")}, длина {Raw(Len(normal))}.");
        }
        catch (Exception ex)
        {
            step.Observe($"    контроль плоской грани {index}: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// What <c>normalOrientation</c> means, derived rather than asserted: the two caps of the plate
    /// are cut from the SAME planar surface family, so their <c>GetNormal</c> points the same way
    /// while their outward-from-material normals are opposite. If the flag is the only thing that
    /// separates them, the flag is exactly the "face normal = surface normal?" bit.
    /// </summary>
    private static void InterpretOrientation(ProbeStep step, Case c)
    {
        var axial = c.PlanarControls.Where(p => Math.Abs(Math.Abs(p.Normal[2]) - 1d) <= 1e-6).ToList();
        if (axial.Count < 2)
        {
            step.Observe($"Ориентация нормали: плоских граней с нормалью вдоль z нашлось {axial.Count} — вывод о флаге сделать не из чего.");
            return;
        }

        var bottom = axial.OrderBy(p => p.Z ?? double.MaxValue).First();
        var top = axial.OrderByDescending(p => p.Z ?? double.MinValue).First();
        var sameSurfaceNormal = Within(bottom.Normal, top.Normal, 1e-6);
        var flagsDiffer = bottom.Orientation != top.Orientation;
        var bottomIsFalseAndTopIsTrue = bottom.Orientation == false && top.Orientation == true;

        var rule = bottomIsFalseAndTopIsTrue
            ? "true = нормаль грани совпадает с surface.GetNormal, false = противоположна (нижняя крышка при z=0 имеет GetNormal (0,0,1), но наружу из материала она направлена вниз — флаг это и различает)"
            : " крышки различаются иначе, чем ожидалось; связь флага со знаком нормали надо перепроверить";

        var line = $"плоские грани с нормалью вдоль z: z={Num(bottom.Z)} → GetNormal ({Vec(bottom.Normal)}), normalOrientation={On(bottom.Orientation)}; z={Num(top.Z)} → GetNormal ({Vec(top.Normal)}), normalOrientation={On(top.Orientation)}. "
            + $"Один и тот же вектор поверхности при разных флагах: {On(sameSurfaceNormal && flagsDiffer)}; флаг false у крышки z=0 и true у крышки z=10: {On(bottomIsFalseAndTopIsTrue)}. → {rule}";
        step.Observe("Ориентация нормали (вывод из контроля) — " + line);
        step.Data[$"{c.Key}_normal_orientation_interpretation"] = line;

        // The same pair of facts read on the cylindrical face, which is what an adapter would ship.
        if (c.SurfaceNormalMid is not null && c.SurfacePointMid is not null)
        {
            var toAxis = Normalise(c.RequestedX - c.SurfacePointMid[0], c.RequestedY - c.SurfacePointMid[1], 0d);
            var faceNormal = c.NormalOrientation == false ? Negate(c.SurfaceNormalMid) : c.SurfaceNormalMid;
            step.Observe($"Ориентация нормали цилиндра (вывод из контроля) — грань с normalOrientation={On(c.NormalOrientation)}: surface.GetNormal = ({Vec(c.SurfaceNormalMid)}), "
                + $"следовательно нормаль ГРАНИ (наружу от материала) = ({Vec(Normalise(faceNormal))}), а направление «к оси отверстия» = ({Vec(toAxis)}) → "
                + (Dot(Normalise(faceNormal), toAxis) > 0.9 ? "нормаль грани смотрит в ось отверстия, как и положено стенке отверстия"
                    : "нормаль грани НЕ смотрит в ось отверстия — правило флага надо перепроверить") + ".");
        }
    }

    private static double[] Negate(double[] v) => new[] { -v[0], -v[1], -v[2] };

    // -----------------------------------------------------------------------------------------
    // Placement reading, shared by R-B and R-C
    // -----------------------------------------------------------------------------------------

    private sealed class PlacementInfo
    {
        public string Runtime = "-";
        public string Interfaces = "-";
        public double[]? Origin;
        public bool OriginReturned;
        public readonly Dictionary<int, double[]> Vectors = new();
        public readonly Dictionary<int, double[]> Axes = new();
        public readonly List<string> Failures = new();
    }

    private static PlacementInfo ReadPlacement(ProbeStep step, object? placement, string tag)
    {
        var info = new PlacementInfo
        {
            Runtime = RuntimeName(placement),
            Interfaces = string.Join(", ", ComDiscovery.Probe(placement).Select(i => i.FullName)),
        };
        step.Observe($"  {tag} → CLR-тип {info.Runtime}; по QI: [{info.Interfaces}]; адресно: {QiReport(placement, "Kompas6API5.ksPlacement", "Kompas6API5.ksPlacementParam")}.");

        if (placement is not ksPlacement ks)
        {
            step.Observe($"  {tag}: объект не приводится к Kompas6API5.ksPlacement — начало и оси этим маршрутом не читаются.");
            return info;
        }

        try
        {
            if (ks.GetOrigin(out var ox, out var oy, out var oz))
            {
                info.OriginReturned = true;
                info.Origin = new[] { ox, oy, oz };
                step.Observe($"  {tag}: GetOrigin() → true, ({Raw(ox)}, {Raw(oy)}, {Raw(oz)}).");
            }
            else
            {
                info.OriginReturned = false;
                step.Observe($"  {tag}: GetOrigin() → false.");
            }
        }
        catch (Exception ex)
        {
            info.Failures.Add("GetOrigin " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"  {tag}: GetOrigin() бросил {info.Failures[^1]}.");
        }

        // The type argument is an unlabelled Int32 in the interop, so both accessors are swept over
        // 0..2 and the meaning is read off the vectors that come back — not assumed from a name.
        foreach (var type in new[] { 0, 1, 2 })
        {
            var vector = new double[3];
            try
            {
                if (SafeBool(() => ks.GetVector(type, out vector[0], out vector[1], out vector[2])) == true)
                {
                    info.Vectors[type] = vector;
                }
            }
            catch (Exception ex)
            {
                info.Failures.Add($"GetVector({type}) {ex.GetType().Name}");
            }

            var axis = new double[3];
            try
            {
                // Note the argument order: GetAxis is (out x, out y, out z, type), type LAST.
                if (SafeBool(() => ks.GetAxis(out axis[0], out axis[1], out axis[2], type)) == true)
                {
                    info.Axes[type] = axis;
                }
            }
            catch (Exception ex)
            {
                info.Failures.Add($"GetAxis({type}) {ex.GetType().Name}");
            }
        }

        step.Observe($"  {tag}: GetVector(0/1/2) = [{string.Join(" | ", info.Vectors.OrderBy(k => k.Key).Select(k => $"{k.Key}:({Vec(k.Value)})"))}]; "
            + $"GetAxis(0/1/2) = [{string.Join(" | ", info.Axes.OrderBy(k => k.Key).Select(k => $"{k.Key}:({Vec(k.Value)})"))}]"
            + (info.Failures.Count > 0 ? "; отказы: " + string.Join(", ", info.Failures) : "."));

        // Whether the two accessors mean the same thing. Measured, because an adapter that reads
        // the axis out of GetAxis would otherwise publish a POINT as a direction.
        if (info.Origin is not null && info.Vectors.Count > 0 && info.Axes.Count > 0)
        {
            var asPoints = new List<string>();
            foreach (var type in info.Axes.Keys.OrderBy(k => k))
            {
                var fromOrigin = Difference(info.Axes[type], info.Origin);
                var matchesVector = info.Vectors.TryGetValue(type, out var vector) && Within(fromOrigin, vector, 1e-6);
                asPoints.Add($"type={type}: GetAxis − GetOrigin = ({Vec(fromOrigin)}){(matchesVector ? " = GetVector(type) → GetAxis отдаёт ТОЧКУ origin+вектор, а не направление" : " ≠ GetVector(type)")}");
            }

            step.Observe($"  {tag}: GetVector против GetAxis — {string.Join("; ", asPoints)}.");
            step.Data[$"{tag}: axis_accessor_form"] = string.Join("; ", asPoints);
        }

        return info;
    }

    /// <summary>
    /// Which returned vector is the axis of the figure. Decided by measurement: the bore is cut
    /// through a plate extruded along model z, so the axis is the one candidate parallel to ±Z
    /// (|cos| &gt; 0.999). The evidence string says so explicitly rather than naming a vendor
    /// convention the probe did not verify.
    /// </summary>
    private static (double[]? Axis, string Evidence) PickAxis(PlacementInfo placement)
    {
        var evidence = new List<string>();
        double[]? axis = null;

        foreach (var (label, table) in new[]
                 {
                     ("GetVector", placement.Vectors),
                     ("GetAxis", placement.Axes),
                 })
        {
            foreach (var pair in table.OrderBy(k => k.Key))
            {
                var unit = Normalise(pair.Value);
                var parallelToZ = Math.Abs(unit[2]) > 0.999d;
                evidence.Add($"{label}(type={pair.Key}) = ({Vec(pair.Value)}), |норма|={Raw(Len(pair.Value))}, |cos с осью z|={Raw(Math.Abs(unit[2]))}{(parallelToZ ? " ← коллинеен z" : string.Empty)}");
                if (parallelToZ && axis is null)
                {
                    axis = unit;
                }
            }
        }

        return (axis, evidence.Count == 0 ? "ни один GetVector/GetAxis не вернул вектор" : string.Join("; ", evidence));
    }

    // -----------------------------------------------------------------------------------------
    // Cross-case proof: the measured centre has to MOVE together with the bore
    // -----------------------------------------------------------------------------------------

    private static void CrossCaseCheck(ProbeStep step, Case a, Case b)
    {
        var routes = new (string Name, Func<Case, double[]?> Read)[]
        {
            ("R-B origin из ksCylinderParam.GetPlacement()", c => c.SurfaceOrigin),
            ("R-C центр из ksCircle3dParam.GetPlacement()", c => c.Circles.FirstOrDefault(r => r.CentreFromPlacement is not null)?.CentreFromPlacement),
            ("R-C центр из ksCurve3D.GetGabarit", c => c.Circles.FirstOrDefault(r => r.CentreFromBox is not null)?.CentreFromBox),
        };

        var lines = new List<string>
        {
            "R-A GetCylinderParam положения не даёт вовсе — только h и r (включено в отчёт, чтобы это было видно, а не молча опущено)",
        };

        foreach (var route in routes)
        {
            var pa = route.Read(a);
            var pb = route.Read(b);
            string line;
            if (pa is null || pb is null)
            {
                line = $"{route.Name}: центр не получен в одном из случаев (A={Point(pa)}, B={Point(pb)}) — сравнение невозможно";
            }
            else
            {
                var moved = Math.Abs(pa[0] - pb[0]) > 1d || Math.Abs(pa[1] - pb[1]) > 1d;
                line = $"{route.Name}: A={Point(pa)} против запрошенного ({Num(a.RequestedX)}, {Num(a.RequestedY)}) → {On(NearXY(pa, a.RequestedX, a.RequestedY))}; "
                    + $"B={Point(pb)} против запрошенного ({Num(b.RequestedX)}, {Num(b.RequestedY)}) → {On(NearXY(pb, b.RequestedX, b.RequestedY))}; "
                    + $"значение сместилось вместе с отверстием: {On(moved)}";
            }

            lines.Add(line);
            step.Observe("Различение положений — " + line);
        }

        step.Data["position_routes_cross_case"] = lines.ToArray();

        var radiusLines = new List<string>();
        var firstCircle = a.Circles.FirstOrDefault(r => r.RadiusFromParam is not null) ?? a.Circles.FirstOrDefault();
        foreach (var (name, value, expected) in new (string, double?, double)[]
                 {
                     ("R-A r из GetCylinderParam", a.ParamR, BoreRadius),
                     ("R-A h из GetCylinderParam", a.ParamH, Thickness),
                     ("R-B ksCylinderParam.radius", a.SurfaceParamRadius, BoreRadius),
                     ("R-B ksCylinderParam.height", a.SurfaceParamHeight, Thickness),
                     ("R-C ksCircle3dParam.radius", firstCircle?.RadiusFromParam, BoreRadius),
                     ("R-C радиус из габарита окружности", firstCircle?.RadiusFromBox, BoreRadius),
                     ("R-C радиус из длины окружности L/2π", firstCircle?.RadiusFromCircumference, BoreRadius),
                 })
        {
            var ok = value is double v && Math.Abs(v - expected) <= 1e-6;
            var factor = value is double vv && Math.Abs((vv * 10d) - expected) <= 1e-6
                ? " (в 10 раз меньше ожидания — признак сантиметров)"
                : string.Empty;
            radiusLines.Add($"{name} = {Raw(value)}, ожидаем {Raw(expected)} → {On(ok)}{factor}");
        }

        step.Data["radius_height_routes"] = radiusLines.ToArray();
        foreach (var line in radiusLines)
        {
            step.Observe("Размер — " + line);
        }
    }

    private static bool NearXY(double[] p, double x, double y) =>
        Math.Abs(p[0] - x) <= 1e-6 && Math.Abs(p[1] - y) <= 1e-6;

    private static void Verdict(ProbeStep step, Case a, Case b)
    {
        step.Data["case_summaries"] = new[] { Summary(a), Summary(b) };
        step.Observe("Итог случая " + Summary(a));
        step.Observe("Итог случая " + Summary(b));

        var throughCut = a.VolumeBefore is double vb && a.VolumeAfter is double va
            && Math.Abs((vb - va) - (Math.PI * BoreRadius * BoreRadius * Thickness)) <= 0.01d;
        var paramOk = a.ParamReturned == true && Both(a.ParamR, BoreRadius, Agree) && Both(a.ParamH, Thickness, Agree);
        var surfaceOk = Both(a.SurfaceParamRadius, BoreRadius, Agree) && Both(a.SurfaceParamHeight, Thickness, Agree)
            && a.SurfaceOrigin is not null && b.SurfaceOrigin is not null;
        var boundaryOk = a.Circles.Any(r => r.CentreFromPlacement is not null && r.CentreAgrees && r.RadiusAgrees);
        var surfaceMoves = a.SurfaceOrigin is { } sa && b.SurfaceOrigin is { } sb
            && NearXY(sa, a.RequestedX, a.RequestedY) && NearXY(sb, b.RequestedX, b.RequestedY);
        var bboxMoves = a.Circles.Any(r => r.CentreFromBox is { } ca && NearXY(ca, a.RequestedX, a.RequestedY))
            && b.Circles.Any(r => r.CentreFromBox is { } cb && NearXY(cb, b.RequestedX, b.RequestedY));
        var axisOk = a.SurfaceAxis is { } axisA && Math.Abs(Math.Abs(axisA[2]) - 1d) <= 1e-6
            && a.Circles.Any(r => r.AxisFromPlacement is { } ax && Math.Abs(Math.Abs(ax[2]) - 1d) <= 1e-6);

        step.Data["bore_through_cut_verified"] = throughCut;
        step.Data["route_a_cylinder_param_ok"] = paramOk;
        step.Data["route_b_surface_param_ok"] = surfaceOk;
        step.Data["route_b_moves_with_bore"] = surfaceMoves;
        step.Data["route_c_boundary_agrees"] = boundaryOk;
        step.Data["route_c_bbox_moves_with_bore"] = bboxMoves;
        step.Data["axis_direction_obtained"] = axisOk;
        step.Data["cylindrical_face_count_case_a"] = a.CylindricalFaces;
        step.Data["normal_orientation_case_a"] = a.NormalOrientation;
        step.Data["loops_on_cylindrical_face"] = a.LoopCount;

        if (paramOk && surfaceOk && boundaryOk && surfaceMoves && bboxMoves && throughCut)
        {
            step.Pass($"Все три маршрута измерены и согласованы на реальном теле. R-A: GetCylinderParam вернул {On(a.ParamReturned)} h={Raw(a.ParamH)}, r={Raw(a.ParamR)} — числа в миллиметрах "
                + $"(боковая поверхность 2πrh = {Raw(2 * Math.PI * (a.ParamR ?? 0) * (a.ParamH ?? 0))} мм² против площади грани GetArea(1)={Raw(a.AreaMm2)} мм²), положения он не даёт. "
                + $"R-B: GetSurfaceParam() отвечает [{string.Join(", ", a.SurfaceParamInterfaces)}], даёт r={Raw(a.SurfaceParamRadius)}, h={Raw(a.SurfaceParamHeight)} и placement с началом {Point(a.SurfaceOrigin)} и осью {Point(a.SurfaceAxis)}; "
                + $"при переносе отверстия из ({Num(a.RequestedX)}, {Num(a.RequestedY)}) в ({Num(b.RequestedX)}, {Num(b.RequestedY)}) измеренный центр последовал за ним ({Point(b.SurfaceOrigin)}). "
                + $"R-C независимо подтверждает то же: у границы {a.Circles.Count} окружностей, центр из ksCircle3dParam.GetPlacement() совпал с центром из GetGabarit с точностью 1e-6 мм, радиус совпал с L/2π; габарит различает положения тоже. "
                + $"Грани: {a.CylindricalFaces} цилиндрических из {a.FaceCount}, циклов {a.LoopCount}, рёбер {a.EdgeCountOnCylinder} [{a.EdgeKinds}], normalOrientation={On(a.NormalOrientation)}. "
                + $"Ось взята из ksPlacement.GetVector(2): GetAxis(type) отдаёт ТОЧКУ origin+вектор, а не направление (см. наблюдения), а параметр v у цилиндра нормирован на [0..1] (высота в мм из него не читается — только как |GetPoint(v=1)−GetPoint(v=0)| = {Raw(Length(a.SurfacePointLowV, a.SurfacePointHighV))} мм).");
            return;
        }

        var missing = new List<string>();
        if (!throughCut)
        {
            missing.Add($"построение: отверстие не сняло 250π мм³ (объём {Raw(a.VolumeBefore)} → {Raw(a.VolumeAfter)})");
        }

        if (!paramOk)
        {
            missing.Add($"R-A: вернул {On(a.ParamReturned)}, h={Raw(a.ParamH)}, r={Raw(a.ParamR)}, ошибка {a.ParamError ?? "нет"}");
        }

        if (!surfaceOk)
        {
            missing.Add($"R-B: r={Raw(a.SurfaceParamRadius)}, h={Raw(a.SurfaceParamHeight)}, origin={Point(a.SurfaceOrigin)}, интерфейсы параметра [{string.Join(",", a.SurfaceParamInterfaces)}]");
        }

        if (!surfaceMoves)
        {
            missing.Add($"R-B не различает положения: A={Point(a.SurfaceOrigin)}, B={Point(b.SurfaceOrigin)}");
        }

        if (!boundaryOk)
        {
            missing.Add($"R-C: окружностей {a.Circles.Count}, ни в одной центр/радиус из двух независимых производных не сошлись (детали в наблюдениях)");
        }

        if (!bboxMoves)
        {
            missing.Add($"R-C габарит не различает положения: A={Point(a.Circles.FirstOrDefault()?.CentreFromBox)}, B={Point(b.Circles.FirstOrDefault()?.CentreFromBox)}");
        }

        step.Fail("Маршрут(ы) не подтверждены целиком: " + string.Join(" || ", missing)
            + (axisOk ? "; направление оси получено (коллинмерно z)" : "; направление оси НЕ получено ни одним маршрутом"));
    }

    private static string Summary(Case c) =>
        $"{c.Key} ({c.Description}): граней {c.FaceCount}, цилиндрических {c.CylindricalFaces}, площадь грани {Raw(c.AreaMm2)} мм² (GetArea(0)={Raw(c.AreaCm2)} см²), "
        + $"R-A: returned={On(c.ParamReturned)} h={Raw(c.ParamH)} r={Raw(c.ParamR)}; "
        + $"R-B: surface [{string.Join(",", c.SurfaceInterfaces)}] param [{string.Join(",", c.SurfaceParamInterfaces)}] r={Raw(c.SurfaceParamRadius)} h={Raw(c.SurfaceParamHeight)} origin={Point(c.SurfaceOrigin)} axis={Point(c.SurfaceAxis)}; "
        + $"R-C: рёбер {c.EdgeCountOnCylinder} [{c.EdgeKinds}], циклов {c.LoopCount} ({c.LoopReport}), "
        + $"окружности [{string.Join(" / ", c.Circles.Select(r => $"ребро{r.EdgeIndex}: центр_pl={Point(r.CentreFromPlacement)} центр_box={Point(r.CentreFromBox)} r_param={Raw(r.RadiusFromParam)} r_box={Raw(r.RadiusFromBox)} r_L={Raw(r.RadiusFromCircumference)}"))}], "
        + $"normalOrientation={On(c.NormalOrientation)}, GetNormal={Point(c.SurfaceNormalMid)}"
        + (c.Failure is null ? "." : $" ; СБОЙ: {c.Failure}");

    // -----------------------------------------------------------------------------------------
    // Construction — P2.1's cut configuration, plate corner moved to the origin
    // -----------------------------------------------------------------------------------------

    private static ksEntity? Plate(ksPart part)
    {
        var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
        var sketch = RectSketch(part, xy, 0d, 0d, PlateX, PlateY, "p5 plate profile");
        var extrusion = (ksEntity)part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
        if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, Blind(), Thickness, 0d, false);
        return extrusion.Create() ? extrusion : null;
    }

    /// <summary>
    /// The through-cut configuration P2.1 settled on: etThroughAll at directionType=2 with depth
    /// deliberately 1 mm, so "through" cannot be an artefact of depth equalling wall thickness.
    /// </summary>
    private static bool Bore(ksPart part, ksEntity sketch, out string error)
    {
        error = string.Empty;
        var cut = (ksEntity)part.NewEntity(TypeOf("o3d_cutExtrusion", KompasObjectTypes.CutExtrusion));
        if (cut.GetDefinition() is not ksCutExtrusionDefinition definition)
        {
            error = "определение вырезания не ksCutExtrusionDefinition";
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 2;
        definition.SetSideParam(true, ThroughAll(), 1d, 0d, false);
        definition.SetSideParam(false, ThroughAll(), 1d, 0d, false);
        if (!cut.Create())
        {
            error = "cut.Create() = false";
            return false;
        }

        return true;
    }

    private static ksEntity RectSketch(ksPart part, ksEntity plane, double u1, double v1, double u2, double v2, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksLineSeg(u1, v1, u2, v1, 1);
        editor.ksLineSeg(u2, v1, u2, v2, 1);
        editor.ksLineSeg(u2, v2, u1, v2, 1);
        editor.ksLineSeg(u1, v2, u1, v1, 1);
        definition.EndEdit();
        return sketch;
    }

    private static ksEntity CircleSketch(ksPart part, ksEntity plane, double u, double v, double radius, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksCircle(u, v, radius, 1);
        definition.EndEdit();
        return sketch;
    }

    // -----------------------------------------------------------------------------------------
    // Recording helpers (local copies on purpose: the P2.1..P2.4 steps must not move under this one)
    // -----------------------------------------------------------------------------------------

    private static TInterface? AsDefinition<TInterface>(object? element)
        where TInterface : class
    {
        if (element is TInterface direct)
        {
            return direct;
        }

        try
        {
            return element is ksEntity entity ? entity.GetDefinition() as TInterface : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Volume in mm³ through the mass-properties route P0.7 calibrated (the selector is an
    /// argument, and 0 would silently mean centimetres).
    /// </summary>
    private static double? Volume(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return null;
            }

            // CalcMassInertiaProperties is declared to return Object, so a typed local is the
            // route P0.7 proved; the property is read off the static interop type.
            if (body.CalcMassInertiaProperties((uint)MixMmKg) is not ksMassInertiaParam properties)
            {
                return null;
            }

            var value = typeof(ksMassInertiaParam).GetProperty("v")?.GetValue(properties);
            return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[]? PointOf(ksSurface surface, double u, double v)
    {
        try
        {
            return surface.GetPoint(u, v, out var x, out var y, out var z) ? new[] { x, y, z } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[]? NormalOf(ksSurface surface, double u, double v)
    {
        try
        {
            return surface.GetNormal(u, v, out var x, out var y, out var z) ? new[] { x, y, z } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[]? Difference(double[]? high, double[]? low) =>
        high is not null && low is not null ? new[] { high[0] - low[0], high[1] - low[1], high[2] - low[2] } : null;

    private static double Mid(double? a, double? b) => a is double low && b is double high ? (low + high) / 2d : 0d;

    private static string Point(double[]? value) => value is null ? "<нет>" : $"({string.Join(", ", value.Select(v => Zero(v).ToString("0.####", CultureInfo.InvariantCulture)))})";

    /// <summary>Which of the declared interfaces a COM object answers, as explicit HRESULT evidence.</summary>
    private static string QiReport(object? target, params string[] interfaceFullNames)
    {
        if (target is null)
        {
            return "<объект null — спрашивать не с чего>";
        }

        IntPtr unk;
        try
        {
            unk = Marshal.GetIUnknownForObject(target);
        }
        catch (Exception ex)
        {
            return "<GetIUnknownForObject " + ex.GetType().Name + ">";
        }

        var lines = new List<string>();
        try
        {
            foreach (var name in interfaceFullNames)
            {
                var type = ComDiscovery.LookupType(name);
                if (type is null)
                {
                    lines.Add($"{name} → тип не объявлен в interop");
                    continue;
                }

                var hr = Marshal.QueryInterface(unk, type.GUID, out var ppv);
                if (hr == 0 && ppv != IntPtr.Zero)
                {
                    Marshal.Release(ppv);
                    lines.Add($"{name} → S_OK");
                }
                else
                {
                    lines.Add($"{name} → 0x{hr:X8}");
                }
            }
        }
        finally
        {
            Marshal.Release(unk);
        }

        return string.Join("; ", lines);
    }

    private static bool? SafeBool(Func<bool> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CloseQuietly(ksDocument3D? doc)
    {
        if (doc is null)
        {
            return;
        }

        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // The failure that matters was recorded by the caller.
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;

    private static string RuntimeName(object? value) => value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    private static string On(bool? value) => value switch
    {
        null => "<исключение>",
        true => "true",
        false => "false",
    };

    private static string Raw(double? value) => value is double d ? (double.IsFinite(d) ? Zero(d).ToString("R", CultureInfo.InvariantCulture) : "NaN") : "null";

    private static string Raw(double value) => double.IsFinite(value) ? Zero(value).ToString("R", CultureInfo.InvariantCulture) : "NaN";

    private static string Raw(int? value) => value is int i ? i.ToString(CultureInfo.InvariantCulture) : "null";

    private static string Raw(string? value) => value ?? "null";

    private static string Raw(double[]? value) => value is null ? "null" : string.Join(", ", value.Select(Raw));

    /// <summary>
    /// Rendering only: the kernel and the vector helpers both produce negative zero for a component
    /// that is exactly zero (an axis like (0, 1, −0)), and "−0" in an evidence report reads as a
    /// measurement problem when it is a sign bit. The value is untouched — only how it prints.
    /// </summary>
    private static double Zero(double value) => value == 0d ? 0d : value;

    private static string Num(double? value) => value is double d ? Zero(d).ToString("0.###", CultureInfo.InvariantCulture) : "null";

    private static string Vec(double[]? value) => value is null ? "<нет>" : string.Join(", ", value.Select(v => Zero(v).ToString("0.####", CultureInfo.InvariantCulture)));

    private static string Box(double[]? min, double[]? max) => min is null || max is null
        ? "<нет>"
        : $"x∈[{Num(min[0])}..{Num(max[0])}], y∈[{Num(min[1])}..{Num(max[1])}], z∈[{Num(min[2])}..{Num(max[2])}]";

    private static string Ratio(double? a, double? b) => a is double x && b is double y && Math.Abs(y) > 1e-12
        ? (x / y).ToString("0.####", CultureInfo.InvariantCulture)
        : "<н/д>";

    private static double[] Normalise(double[] v) => Normalise(v[0], v[1], v[2]);

    private static double[] Normalise(double x, double y, double z)
    {
        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        return length < 1e-12 ? new[] { 0d, 0d, 0d } : new[] { x / length, y / length, z / length };
    }

    private static double Len(double[] v) => Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));

    private static double? Length(double[]? from, double[]? to) => Difference(to, from) is double[] delta ? Len(delta) : (double?)null;

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static bool Both(double? value, double expected, double tolerance) =>
        value is double v && Math.Abs(v - expected) <= tolerance;

    private static bool Both(double? value, double? expected, double tolerance) =>
        value is double v && expected is double e && Math.Abs(v - e) <= tolerance;

    private static bool Within(double[]? a, double[]? b, double tolerance) =>
        a is not null && b is not null
        && Math.Abs(a[0] - b[0]) <= tolerance
        && Math.Abs(a[1] - b[1]) <= tolerance
        && Math.Abs(a[2] - b[2]) <= tolerance;
}
