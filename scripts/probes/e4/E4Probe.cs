using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace E4Probe;

/// <summary>E4 probe: builds a plate, then measures whether the documented routes reach the hole modes
/// that the product does not have yet, and whether their answers can be compared with a number.</summary>
/// <remarks>WHY A PROBE: research order - only KOMPAS's own answers on documented routes are measurable.
/// DOC: E4_RESEARCH_DEVELOPER_PROMPT.md §1.4; the hole family of the SDK 24 help snapshot this order
/// captured under reference/kompas-sdk-v24/ (`docs-wire-...-e4`): ihole3d*, iholedisposal*,
/// i*conicholeparameters*, icountersink*spotfacingholeparameters*, ilibraryholeparameters*,
/// ksholetypeenum, ksdepthtypeenum, ksendfacetypeenum, ksconictypeenum.
/// History: docs/decisions/adapter-features.md#hole-extended-modes</remarks>
internal sealed class E4Probe : IDisposable
{
    // Fixture: a plate 40 x 30 x 20 mm, so the volume before any hole is exactly 24000 mm3 and the top
    // face is the plane z = 20. Every expected number below is derived from those two facts.
    private const double Width = 40;
    private const double Depth = 30;
    private const double Height = 20;
    private const double PlateVolume = Width * Depth * Height;

    // Thickness of the CURRENT plate. The counterdrill law has to hold on more than one, so the
    // fixture is rebuilt per thickness instead of assuming the base one.
    private double _height = Height;

    private const int PlaneXoy = 1;
    private const int SketchType = 5;
    private const int BossExtrusionType = 25;

    // ST_MIX_MM | ST_MIX_KG: millimetres for length, kilograms for mass (MEASURED in the product).
    private const uint MassMmKg = 17;
    // ksGetArea unit selector: 1 is millimetres (the value the product publishes areas with).
    private const uint LengthMm = 1;

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _states = new();
    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;

    public E4Probe(string outPath) => _outPath = Path.GetFullPath(outPath);

    public int Run(bool keepOpen)
    {
        try
        {
            Launch();
            Counterdrill();
            Conic();
            SphereBottom();
            ObjectDepth();
            LibraryHole();
            DisposalExtras();
            ThreadRead();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }

        Write();
        if (!keepOpen)
        {
            CloseCurrent();
        }

        return _rows.Any(row => row?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    // ── environment and fixture ─────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "нет", "FAIL");
            throw new InvalidOperationException("KOMPAS is not registered");
        }

        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        _app5.Visible = false;
        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app5", "экземпляр КОМПАС создан и ksGetApplication7() даёт IApplication",
            _app7 is null ? "IApplication = null" : "KompasObject + IApplication",
            _app7 is null ? "FAIL" : "PASS");
    }

    /// <summary>Builds a fresh plate and returns the API7 container plus the TOP face as an API7 object.
    /// The face is chosen by measurement (normal +z at z = 20), not by collection index: the index order
    /// of a box's faces is not part of the documentation.</summary>
    private (IModelContainer Container, IModelObject TopFace) BuildPlate()
    {
        CloseCurrent();
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, false) вернул false");
        }

        var part = _doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(PlaneXoy) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOY не получена");

        var plate = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) → null");
        var plateDefinition = plate.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition не получен");
        plateDefinition.SetPlane(plane);
        plate.Create();

        var editor = plateDefinition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() → не ksDocument2D");
        editor.ksLineSeg(0, -Depth / 2, Width, -Depth / 2, 1);
        editor.ksLineSeg(Width, -Depth / 2, Width, Depth / 2, 1);
        editor.ksLineSeg(Width, Depth / 2, 0, Depth / 2, 1);
        editor.ksLineSeg(0, Depth / 2, 0, -Depth / 2, 1);
        plateDefinition.EndEdit();

        var feature = part.NewEntity(BossExtrusionType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(plate);
        if (!extrusion.SetSideParam(true, 0, _height, 0, false))
        {
            throw new InvalidOperationException("SetSideParam вернул false");
        }

        feature.Create();
        part.Update();
        _doc5.RebuildDocument();
        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");

        var container = _doc7.TopPart as IModelContainer
            ?? throw new InvalidOperationException("(IModelContainer)IPart7 не получен");
        return (container, TopFace(part));
    }

    /// <summary>The face whose plane is z = 20 with a +z normal and the largest area.</summary>
    private IModelObject TopFace(ksPart part)
    {
        if (part.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0
            || bodies.GetByIndex(0) is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            throw new InvalidOperationException("грань плиты не получена");
        }

        object? best = null;
        var bestArea = 0d;
        for (var index = 0; index < faces.GetCount(); index++)
        {
            var raw = faces.GetByIndex(index);
            if (raw is not ksFaceDefinition face
                || face.GetSurface() is not ksSurface surface
                || surface.GetSurfaceParam() is not ksPlaneParam planeParam
                || planeParam.GetPlacement() is not ksPlacement placement
                || !placement.GetOrigin(out _, out _, out var z)
                || !placement.GetVector(2, out _, out _, out var nz))
            {
                continue;
            }

            if (Math.Abs(z - _height) > 1e-6 || nz < 0.9d)
            {
                continue;
            }

            var area = face.GetArea(LengthMm);
            if (area > bestArea)
            {
                bestArea = area;
                best = raw;
            }
        }

        if (best is null || _app5?.TransferInterface(best, (int)ksAPITypeEnum.ksAPI7Dual, 0) is not IModelObject face7)
        {
            throw new InvalidOperationException("верхняя грань не перенесена в API7");
        }

        Record("top_face", "верхняя грань z = толщине перенесена в API7",
            $"площадь {bestArea:0.##} мм2, z = {_height:0}", "PASS");
        return face7;
    }

    /// <summary>Adds a hole, applies <paramref name="setup"/> BEFORE <c>Update()</c>, and reports whether
    /// the kernel built it. The order matters: a write after Update stays a representation.</summary>
    private (bool Updated, string? Failure, IHole3D? Hole, JsonObject Before, JsonObject After) Create(
        string label,
        double diameterMm,
        Action<IHole3D, IHoleDisposal> setup,
        bool? direction = null)
    {
        var (container, topFace) = BuildPlate();
        var before = State(label + "_before");
        if (container.Holes3D.Add() is not IHole3D hole)
        {
            return (false, "IHoles3D.Add() вернул не IHole3D", null, before, before);
        }

        if (hole is not IHoleDisposal disposal)
        {
            return (false, "объект отверстия не отвечает на QI(IHoleDisposal)", hole, before, before);
        }

        hole.Diameter = diameterMm;
        disposal.BaseSurface = topFace;
        disposal.Perpendicular = true;
        if (direction is bool requested)
        {
            disposal.Direction = requested;
        }

        setup(hole, disposal);
        var updated = hole.Update();
        var after = State(label + "_after");
        return (updated, updated ? null : "IHole3D.Update() вернул false", hole, before, after);
    }

    // ── measured modes ──────────────────────────────────────────────────────────────────────────

    /// <summary><c>ksHTCounterdrill</c> (3): pilot, spotfacing and countersink on one feature. The order
    /// asks for a law of simple bodies rather than a reference number, so every input set is measured on
    /// TWO thicknesses and compared with two hypotheses at once.</summary>
    /// <remarks>MEASURED: the cone sits at the BOTTOM of the spotfacing and tapers to the pilot, and the
    /// written angle is the FULL apex angle. A cone counted from the support face overstates the bore,
    /// which is why the first revision of this probe found no formula at all.
    /// History: docs/decisions/adapter-features.md#hole-counterdrill</remarks>
    private void Counterdrill()
    {
        const double pilot = 10;
        const double diameter = 22;
        const double depth = 4;
        const double angle = 90;
        var model = CounterdrillModel(pilot, diameter, depth, angle, Height);

        // The direction is MEASURED, not assumed: with the default one the feature added material
        // outward on the first run, and a probe that trusted it would report the mode as broken.
        foreach (var direction in new[] { (bool?)null, true, false })
        {
            var (updated, failure, hole, before, after) = Create("counterdrill", pilot,
                (h, _) => SetCounterdrill(h, diameter, depth, angle), direction);

            var name = direction is null ? "по умолчанию" : direction.Value.ToString();
            if (!updated)
            {
                Record("counterdrill_direction_" + name, "признак построен через IHole3D",
                    "Update=false: " + failure, "FAIL");
                continue;
            }

            var parameters7 = hole!.HoleParameters as ICountersinkSpotfacingHoleParameters;
            var removed = Volume(before) - Volume(after);
            Record("counterdrill_direction_" + name,
                $"снято {model:0.###} мм3 (пилот + кольцо выточки + конус от дна)",
                $"снято {removed:0.###} мм3; прочитано D={SafeDouble(() => parameters7!.SpotfacingCountersinkDiameter):0.###}, " +
                $"глубина={SafeDouble(() => parameters7!.SpotfacingDepth):0.###}, " +
                $"угол={SafeDouble(() => parameters7!.CountersinkAngle):0.###}",
                removed > 1e-6 && Near(removed, model, 5e-3) ? "PASS"
                    : removed < -1e-6 ? "FAIL" : "MEASURED");
        }

        // One input at a time, plus a second and a third angle: a law fitted to a single set proves
        // nothing, and the plate thickness is the variable the previous capture never touched.
        var sets = new (double Pilot, double Diameter, double Depth, double Angle)[]
        {
            (10, 22, 4, 90),
            (10, 18, 4, 90),
            (12, 24, 5, 90),
            (10, 22, 4, 60),
            (10, 22, 6, 90),
            (8, 20, 3, 60),
            (10, 26, 2, 120),
        };

        var passed = 0;
        var total = 0;
        foreach (var thickness in new[] { 20d, 30d })
        {
            _height = thickness;
            foreach (var set in sets)
            {
                total++;
                var id = $"counterdrill_t{thickness:0}_{set.Pilot:0}x{set.Diameter:0}x{set.Depth:0}a{set.Angle:0}";
                var (updated, failure, hole, before, after) = Create(id, set.Pilot,
                    (h, _) => SetCounterdrill(h, set.Diameter, set.Depth, set.Angle));
                if (!updated)
                {
                    Record(id, "признак построен через IHole3D", "Update=false: " + failure, "FAIL");
                    continue;
                }

                var parameters = hole!.HoleParameters as ICountersinkSpotfacingHoleParameters;
                var readDiameter = SafeDouble(() => parameters!.SpotfacingCountersinkDiameter);
                var readDepth = SafeDouble(() => parameters!.SpotfacingDepth);
                var readAngle = SafeDouble(() => parameters!.CountersinkAngle);
                var removed = Volume(before) - Volume(after);
                var bottomCone = CounterdrillModel(set.Pilot, set.Diameter, set.Depth, set.Angle, thickness);
                var faceCone = ConeFromFaceModel(set.Pilot, set.Diameter, set.Depth, set.Angle, thickness);
                var faces = after["faces"]?.GetValue<int>() ?? -1;
                var matches = Near(removed, bottomCone, 5e-3);
                var readBack = Near(readDiameter, set.Diameter, 1e-9) && Near(readDepth, set.Depth, 1e-9)
                               && Near(readAngle, set.Angle, 1e-9);
                if (matches && readBack)
                {
                    passed++;
                }

                Record(id,
                    $"снято {bottomCone:0.###} мм3 по модели; параметры читаются как записаны",
                    $"снято {removed:0.###} мм3 (отклонение {Deviation(removed, bottomCone):0.###} %), " +
                    $"«конус от опорной грани» - {Deviation(removed, faceCone):0.###} %; " +
                    $"прочитано D={readDiameter:0.###}, глубина={readDepth:0.###}, угол={readAngle:0.###}, " +
                    $"граней {faces}",
                    matches && readBack ? "PASS" : removed < -1e-6 ? "FAIL" : "MEASURED");
            }
        }

        _height = Height;
        Record("counterdrill_model", "закон из простых тел объясняет все наборы на двух толщинах",
            $"{passed} из {total} наборов: пилот + кольцо выточки + конус от ДНА выточки до пилота; " +
            "записанный угол - полный",
            passed == total ? "PASS" : "MEASURED");
    }

    /// <summary>Writes the three mode members. The spotfacing diameter is the ONLY diameter of this
    /// interface: the bore and the countersink share it, so there is no separate bore field.</summary>
    private static bool SetCounterdrill(IHole3D hole, double diameterMm, double depthMm, double angleDeg)
    {
        hole.HoleType = ksHoleTypeEnum.ksHTCounterdrill;
        hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
        hole.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
        if (hole.HoleParameters is not ICountersinkSpotfacingHoleParameters parameters)
        {
            return false;
        }

        parameters.SpotfacingDepth = depthMm;
        parameters.SpotfacingCountersinkDiameter = diameterMm;
        parameters.CountersinkAngle = angleDeg;
        return true;
    }

    /// <summary><c>ksHTConic</c> (4) with <c>ksCNDiameter</c>: the cone is given by its top diameter and
    /// its angle. Both readings of the angle (full and half) are compared - the page does not say which
    /// one, so the measurement decides.</summary>
    private void Conic()
    {
        const double diameter = 10;
        const double angleDeg = 30;

        // Two questions in one sweep: which direction removes material, and whether the angle sticks
        // when the cone is given by its diameter (the read-back said 0 on the first run).
        foreach (var direction in new[] { (bool?)null, true, false })
        {
            var (updated, failure, hole, before, after) = Create("conic", diameter,
                (h, _) =>
                {
                    h.HoleType = ksHoleTypeEnum.ksHTConic;
                    h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                    if (h.HoleParameters is IConicHoleParameters parameters)
                    {
                        parameters.ConicType = ksConicTypeEnum.ksCNDiameter;
                        parameters.ConicDiameter = diameter;
                        parameters.ConicAngle = angleDeg;
                    }
                },
                direction);

            var name = direction is null ? "по умолчанию" : direction.Value.ToString();
            if (!updated)
            {
                Record("conic_created_" + name, "признак построен через IHole3D",
                    "Update=false: " + failure, "FAIL");
                continue;
            }

            var parameters7 = hole!.HoleParameters as IConicHoleParameters;
            var removed = Volume(before) - Volume(after);
            var radius = diameter / 2d;
            var readAngle = SafeDouble(() => parameters7!.ConicAngle);
            var effective = readAngle > 0.5d && readAngle < 179.5d ? readAngle : angleDeg;
            var half = Math.PI / 3d * radius * radius * (radius / Math.Tan(effective * Math.PI / 360d));
            var full = Math.PI / 3d * radius * radius * (radius / Math.Tan(effective * Math.PI / 180d));
            var matches = Near(removed, half, 2e-3) ? "половина угла"
                : Near(removed, full, 2e-3) ? "полный угол"
                : "ни то, ни другое";
            Record("conic_direction_" + name,
                "снятый объём против конуса по половине и по полному углу",
                $"снято {removed:0.###}; D={SafeDouble(() => parameters7!.ConicDiameter):0.###}, " +
                $"угол прочитан {readAngle:0.###} (записан {angleDeg}), тип={parameters7?.ConicType}; " +
                $"половина {half:0.###}, полный {full:0.###}; совпало: {matches}",
                removed > 1e-6 && matches != "ни то, ни другое" ? "PASS"
                    : removed < -1e-6 ? "FAIL" : "MEASURED");
        }

        // The angle did not stick in the diameter-driven mode. The other documented value of
        // `ksConicTypeEnum` names the angle as the driver, so the same write is measured there.
        foreach (var conicType in new[] { ksConicTypeEnum.ksCNAngle, ksConicTypeEnum.ksCNDiameter })
        {
            var (updated, failure, hole, before, after) = Create("conic_" + conicType, diameter,
                (h, _) =>
                {
                    h.HoleType = ksHoleTypeEnum.ksHTConic;
                    h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                    if (h.HoleParameters is IConicHoleParameters parameters)
                    {
                        parameters.ConicType = conicType;
                        parameters.ConicAngle = angleDeg;
                        parameters.ConicDiameter = diameter;
                    }
                });

            if (!updated)
            {
                Record("conic_type_" + conicType, "угол записывается при своём типе",
                    "Update=false: " + failure, "FAIL");
                continue;
            }

            var parameters7 = hole!.HoleParameters as IConicHoleParameters;
            var removed = Volume(before) - Volume(after);
            var readDiameter = SafeDouble(() => parameters7!.ConicDiameter);
            // With the angle as the driver the diameter is DERIVED, so the comparison uses the read
            // diameter and the plate thickness: the cone is cut by the body, not closed at its apex.
            var radius = readDiameter / 2d;
            var slope = Math.Tan(angleDeg * Math.PI / 360d);
            var lower = Math.Max(0d, radius - Height * slope);
            var frustum = Math.PI / 3d * Height * (radius * radius + radius * lower + lower * lower);
            var verdict = conicType == ksConicTypeEnum.ksCNAngle
                ? (Near(removed, frustum, 2e-3) ? "PASS" : "FAIL")
                : "MEASURED";
            Record("conic_type_" + conicType,
                conicType == ksConicTypeEnum.ksCNAngle
                    ? $"снято {frustum:0.###} мм3 (усечённый конус по прочитанному D и половине угла)"
                    : "угол прочитан обратно как записан",
                $"снято {removed:0.###} мм3; тип прочитан {parameters7?.ConicType}, " +
                $"угол прочитан {SafeDouble(() => parameters7!.ConicAngle):0.###} (записан {angleDeg}), " +
                $"D прочитан {readDiameter:0.###}",
                verdict);
        }
    }

    /// <summary><c>ksEFSphere</c> (2): the end face of a blind hole. The page names the value, not what it
    /// adds, so both hypotheses are compared with the measurement.</summary>
    private void SphereBottom()
    {
        const double diameter = 10;
        const double depth = 10;
        var (updated, failure, _, before, after) = Create("sphere", diameter,
            (h, _) =>
            {
                h.HoleType = ksHoleTypeEnum.ksHTBase;
                h.DepthType = ksDepthTypeEnum.ksDTValue;
                h.Depth = depth;
                h.EndFaceType = ksEndFaceTypeEnum.ksEFSphere;
            });

        Record("sphere_created", "глухое отверстие со сферическим дном построено",
            updated ? "Update=true" : "Update=false: " + failure, updated ? "PASS" : "FAIL");
        if (!updated)
        {
            return;
        }

        var measured = Volume(before) - Volume(after);
        var radius = diameter / 2d;
        var cylinder = Math.PI * radius * radius * depth;
        var hemisphere = 2d / 3d * Math.PI * radius * radius * radius;
        var matches = Near(measured, cylinder, 2e-3) ? "как плоское дно"
            : Near(measured, cylinder + hemisphere, 2e-3) ? "плоское дно + полусфера"
            : Near(measured, cylinder + hemisphere / 2d, 2e-3) ? "плоское дно + половина полусферы"
            : "ни одна из гипотез";
        Record("sphere_volume", "снятый объём против плоского дна и дна с полусферой",
            $"снято {measured:0.###}; цилиндр {cylinder:0.###}; цилиндр+полусфера " +
            $"{cylinder + hemisphere:0.###}; совпало: {matches}", "MEASURED");
    }

    /// <summary><c>ksDTObject</c> (2): depth up to an object. The route needs <c>DepthVertex</c> or
    /// <c>DepthFace</c>, and without one the kernel must refuse - a silent build would be worse.</summary>
    private void ObjectDepth()
    {
        var (updated, failure, _, before, after) = Create("object_depth", 10,
            (h, _) =>
            {
                h.HoleType = ksHoleTypeEnum.ksHTBase;
                h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                h.DepthType = ksDepthTypeEnum.ksDTObject;
            });

        Record("object_depth_refusal", "без объекта глубины ядро отказывает, а не строит молча",
            updated
                ? $"Update=true, снято {Volume(before) - Volume(after):0.###} мм3"
                : "Update=false: " + failure,
            updated ? "FAIL" : "PASS");
    }

    /// <summary><c>ksHTLfrLibrary</c> (5): a hole from a library fragment. The route is the documented
    /// one (<c>ILibraryHoleParameters.FileName</c> + variables); whether it builds depends on a library
    /// file being present on the machine, which is what this row measures.</summary>
    private void LibraryHole()
    {
        var library = Directory.EnumerateFiles(
            Path.Combine("tests"), "*.frw", SearchOption.AllDirectories).FirstOrDefault();
        var (updated, failure, hole, before, after) = Create("library", 10,
            (h, _) =>
            {
                h.HoleType = ksHoleTypeEnum.ksHTLfrLibrary;
                h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                if (library is not null && h.HoleParameters is ILibraryHoleParameters parameters)
                {
                    parameters.FileName = Path.GetFullPath(library);
                }
            });

        var parameters7 = hole?.HoleParameters as ILibraryHoleParameters;
        Record("library_created", "признак типа ksHTLfrLibrary строится по документу библиотеки",
            updated
                ? $"Update=true, снято {Volume(before) - Volume(after):0.###} мм3, " +
                  $"переменных {SafeInt(() => parameters7!.VariablesCount)}"
                : $"Update=false: {failure}; файл библиотеки: {library ?? "не найден"}",
            "MEASURED");
    }

    /// <summary>The remaining <c>IHoleDisposal</c> members: <c>ProcessCanopy</c>, <c>Direction</c> and
    /// <c>DirectionObject</c>. Only acceptance is measured here - the geometry they change needs a
    /// fixture of its own, and the order asks for the route, not for its shape.</summary>
    private void DisposalExtras()
    {
        var (updated, failure, hole, _, _) = Create("disposal", 10,
            (h, disposal) =>
            {
                h.HoleType = ksHoleTypeEnum.ksHTBase;
                h.DepthType = ksDepthTypeEnum.ksDTValue;
                h.Depth = 10;
                h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                disposal.ProcessCanopy = true;
                disposal.Direction = false;
            });

        var disposal7 = hole as IHoleDisposal;
        Record("disposal_members", "ProcessCanopy и Direction принимаются и читаются обратно",
            updated
                ? $"Update=true, ProcessCanopy={SafeBool(() => disposal7!.ProcessCanopy)}, " +
                  $"Direction={SafeBool(() => disposal7!.Direction)}, " +
                  $"DirectionObject={(disposal7?.DirectionObject is null ? "null" : "объект")}"
                : "Update=false: " + failure,
            updated ? "PASS" : "FAIL");
    }

    /// <summary>Thread on a hole: the help has <c>IHole3D.Thread</c> read-only and no setter at all
    /// (the negative control confirms the page does not exist), so the mode is a READ route.</summary>
    private void ThreadRead()
    {
        var (updated, failure, hole, _, _) = Create("thread", 10,
            (h, _) =>
            {
                h.HoleType = ksHoleTypeEnum.ksHTBase;
                h.DepthType = ksDepthTypeEnum.ksDTValue;
                h.Depth = 10;
                h.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
                h.ShowThread = true;
            });

        Record("thread_read_only", "резьба у отверстия читается, но не записывается",
            updated
                ? $"Update=true, Thread={(hole!.Thread is null ? "null" : "IThread")}, " +
                  $"ShowThread={SafeBool(() => hole.ShowThread)}"
                : "Update=false: " + failure, "MEASURED");
    }

    // ── expected numbers ───────────────────────────────────────────────────────────────────────

    /// <summary>ACCEPTED law for a counterdrill: the through pilot, plus the spotfacing ring, plus the
    /// frustum from the spotfacing diameter down to the pilot, whose height follows from the FULL apex
    /// angle. The cone starts at the BOTTOM of the spotfacing.</summary>
    private static double CounterdrillModel(double pilot, double diameter, double depth, double angleDeg,
        double thickness)
    {
        var pilotRadius = pilot / 2d;
        var outerRadius = diameter / 2d;
        var coneDepth = (outerRadius - pilotRadius) / Math.Tan(angleDeg * Math.PI / 360d);

        var volume = Math.PI * pilotRadius * pilotRadius * thickness;
        volume += Math.PI * (outerRadius * outerRadius - pilotRadius * pilotRadius) * depth;
        volume += Math.PI / 3d * coneDepth
                  * (outerRadius * outerRadius + outerRadius * pilotRadius + pilotRadius * pilotRadius)
                  - Math.PI * pilotRadius * pilotRadius * coneDepth;
        return volume;
    }

    /// <summary>REJECTED hypothesis: the same cone counted from the support face, integrated in 2000
    /// steps over the plate thickness. Kept in the capture so the report can show what it costs.</summary>
    private static double ConeFromFaceModel(double pilot, double diameter, double depth, double angleDeg,
        double thickness)
    {
        const int steps = 2000;
        var pilotRadius = pilot / 2d;
        var outerRadius = diameter / 2d;
        var slope = Math.Tan(angleDeg * Math.PI / 360d);
        var coneDepth = (outerRadius - pilotRadius) / slope;
        var step = thickness / steps;
        var volume = Math.PI * pilotRadius * pilotRadius * thickness;
        for (var index = 0; index < steps; index++)
        {
            var z = (index + 0.5) * step;
            var radius = pilotRadius;
            if (z <= depth)
            {
                radius = Math.Max(radius, outerRadius);
            }

            if (z <= coneDepth)
            {
                radius = Math.Max(radius, outerRadius - slope * z);
            }

            volume += Math.PI * (radius * radius - pilotRadius * pilotRadius) * step;
        }

        return volume;
    }

    private static double Deviation(double actual, double expected) =>
        double.IsFinite(actual) && expected > 0d ? Math.Abs(actual - expected) / expected * 100d : double.NaN;

    // ── reading ─────────────────────────────────────────────────────────────────────────────────

    private JsonObject State(string label)
    {
        var part = _doc5?.GetPart(-1) as ksPart;
        var bodies = part?.BodyCollection() as ksBodyCollection;
        var count = bodies?.GetCount() ?? 0;
        var body = count > 0 ? bodies!.GetByIndex(0) as ksBody : null;
        var faces = body?.FaceCollection() as ksFaceCollection;

        double? volume = null;
        try
        {
            if (body?.CalcMassInertiaProperties(MassMmKg) is ksMassInertiaParam properties)
            {
                volume = properties.v;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            volume = null;
        }

        var state = new JsonObject
        {
            ["label"] = label,
            ["bodies"] = count,
            ["faces"] = faces?.GetCount() ?? 0,
            ["volume_mm3"] = volume is null ? null : Math.Round(volume.Value, 6),
        };
        _states.Add(state.DeepClone());
        return state;
    }

    private static double Volume(JsonObject state) => state["volume_mm3"]?.GetValue<double>() ?? double.NaN;

    private static bool Near(double actual, double expected, double tolerance) =>
        double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected));

    private static double SafeDouble(Func<double> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return double.NaN;
        }
    }

    private static int SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return -1;
        }
    }

    private static bool? SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

    // ── output ──────────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string expected, string actual, string verdict)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["expected"] = expected,
            ["actual"] = actual,
            ["verdict"] = verdict,
        });
        Console.WriteLine($"{verdict}  {id}: {actual}");
    }

    private void Write()
    {
        var document = new JsonObject
        {
            ["probe"] = "E4 extended hole modes",
            ["kompas"] = "v24",
            ["plate_mm3"] = PlateVolume,
            ["rows"] = _rows.DeepClone(),
            ["states"] = _states.DeepClone(),
        };
        var directory = Path.GetDirectoryName(_outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"evidence: {_outPath}");
    }

    private void CloseCurrent()
    {
        try
        {
            _doc7?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // Housekeeping only: a part left open must not turn a measured run into a failed one.
        }

        _doc7 = null;
        _doc5 = null;
    }

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COMException 0x{com.HResult:X8}: {com.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    public void Dispose()
    {
        _doc7 = null;
        _doc5 = null;
        _app7 = null;
        _app5 = null;
    }
}
