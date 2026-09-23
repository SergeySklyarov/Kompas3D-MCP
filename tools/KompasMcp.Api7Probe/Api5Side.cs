using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.Api7Probe;

/// <summary>
/// The API5 half of the probe: build the reference plate, measure it, read topology back.
/// </summary>
/// <remarks>
/// Deliberately a second implementation rather than a call into <c>KompasMcp.Api5Adapter</c>:
/// ADR-003 §1 keeps the probe out of the shipping graph, and a measurement that runs through the
/// production adapter cannot later be used to argue about what that adapter does. The calls are the
/// ones docs/STATUS.md and <c>docs/acceptance/p2/p2-probe-report.md</c> already proved, with its
/// conventions: <c>Create(invisible: true, …)</c> for headless work, the explicit
/// <c>ST_MIX_MM|ST_MIX_KG</c> selector on <c>CalcMassInertiaProperties</c>, topology only through
/// <c>GetMainBody() → FaceCollection → EdgeCollection</c>, and members taken from static
/// <c>typeof(…)</c> — never by reflecting over a <c>__ComObject</c> (ADR-001 §2).
/// </remarks>
internal static class Api5
{
    /// <summary>ST_MIX_MM | ST_MIX_KG — the selector P0.7 calibrated volume against (mm³).</summary>
    public const int MassMmKg = 1 | 16;

    public const int LengthMm = 1;

    public const short PlaneXoy = 1;
    public const short PlaneXoz = 2;
    public const short Sketch = 5;
    public const short PlaneOffset = 14;
    public const short BaseExtrusion = 24;
    public const short OperationElement = 110;

    public static string Num(double? value) => value?.ToString("0.############", CultureInfo.InvariantCulture) ?? "null";

    public static string Raw(object? value) => value switch
    {
        null => "null",
        double d => Num(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "null",
        _ => value.ToString() ?? "null",
    };

    public static string RuntimeName(object? value) => value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    public static bool? SafeBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static int? SafeInt(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static object? SafeObject(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static double? SafeDouble(Func<double> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads an enum-valued member, so a member that throws is <c>null</c> rather than fatal.</summary>
    public static T? SafeEnum<T>(Func<T> call) where T : struct, Enum
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The reference plate: a <c>width × height</c> rectangle centred on the model origin,
    /// base-extruded <c>thickness</c> along the sketch normal. So the centre of the top face is
    /// (0, 0, thickness) — which is where the probe is told to put the hole.
    /// </summary>
    public static ksEntity? BasePlate(ksPart part, double width, double height, double thickness, ProbeStep step, string prefix = "a7")
    {
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane)
        {
            step.Observe("API5: GetDefaultEntity(o3d_planeXOY=1) вернул null — эскиз положить не на что.");
            return null;
        }

        if (part.NewEntity(Sketch) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            step.Observe("API5: NewEntity(o3d_sketch=5) или его определение не получены.");
            return null;
        }

        sketch.name = prefix + "-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();

        var halfX = width / 2d;
        var halfY = height / 2d;
        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            step.Observe("API5: BeginEdit() вернул не ksDocument2D.");
            return null;
        }

        editor.ksLineSeg(-halfX, -halfY, halfX, -halfY, 1);
        editor.ksLineSeg(halfX, -halfY, halfX, halfY, 1);
        editor.ksLineSeg(halfX, halfY, -halfX, halfY, 1);
        editor.ksLineSeg(-halfX, halfY, -halfX, -halfY, 1);
        sketchDefinition.EndEdit();

        if (part.NewEntity(BaseExtrusion) is not ksEntity extrusion || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe("API5: NewEntity(o3d_baseExtrusion=24) или его определение не получены.");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0 /* etBlind */, thickness, 0d, false);
        if (SafeBool(extrusion.Create) != true)
        {
            step.Observe("API5: базовое выдавливание Create() → false.");
            return null;
        }

        return extrusion;
    }

    public static double? Volume(ksPart part)
    {
        try
        {
            return part.GetMainBody() is object body ? BodyVolume(body) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static double? BodyVolume(object? bodyObject)
    {
        try
        {
            if (bodyObject is not ksBody body)
            {
                return null;
            }

            // CalcMassInertiaProperties is declared to return Object in the vendor interop, so the
            // value is read off the static type. `v` is a property, not a method — P0 had to be
            // re-measured once because it was looked for as a method.
            if (body.CalcMassInertiaProperties((uint)MassMmKg) is not ksMassInertiaParam properties)
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

    public static int BodyCount(ksPart part)
    {
        try
        {
            return part.BodyCollection() is ksBodyCollection collection ? SafeInt(collection.GetCount) ?? 0 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static int FaceCount(ksPart part)
    {
        try
        {
            return Faces(part) is { } faces ? SafeInt(faces.GetCount) ?? -1 : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static int EdgeCount(ksPart part)
    {
        try
        {
            if (Faces(part) is not { } faces)
            {
                return -1;
            }

            // EntityCollection(o3d_edge) is NOT used: P0.8 measured it holding sketch and
            // construction edges too. Counting has to go through the body.
            var seen = new HashSet<IntPtr>();
            var count = SafeInt(faces.GetCount) ?? 0;
            for (var f = 0; f < count; f++)
            {
                if (((object?)faces.GetByIndex(f)).AsFace() is not ksFaceDefinition face)
                {
                    continue;
                }

                if (face.EdgeCollection() is not ksEdgeCollection edges)
                {
                    continue;
                }

                var edgeCount = SafeInt(edges.GetCount) ?? 0;
                for (var e = 0; e < edgeCount; e++)
                {
                    var edge = edges.GetByIndex(e);
                    var pointer = IntPtr.Zero;
                    try
                    {
                        pointer = Marshal.GetIUnknownForObject(edge);
                        seen.Add(pointer);
                    }
                    finally
                    {
                        if (pointer != IntPtr.Zero)
                        {
                            Marshal.Release(pointer);
                        }
                    }
                }
            }

            return seen.Count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static ksFaceCollection? Faces(ksPart part)
    {
        try
        {
            return part.GetMainBody() is ksBody body ? body.FaceCollection() as ksFaceCollection : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One line per face of the main body, with the cylindrical and planar parameters the API5
    /// route is known to give (P2.5): <c>GetSurface() → GetSurfaceParam()</c> answers
    /// <c>ksCylinderParam</c>/<c>ksPlaneParam</c>, and the axis comes from
    /// <c>GetPlacement().GetVector(2)</c> because <c>GetAxis()</c> returns a point, not a direction.
    /// </summary>
    /// <summary>
    /// The cylindrical faces of the main body, for a probe that needs to know a solid is a
    /// cylinder rather than merely the right volume. Built on <see cref="ReadFaces"/> so the
    /// reading route stays the one P2.5 proved.
    /// </summary>
    public static List<FaceReading> CylinderFaces(ksPart part)
    {
        var prepared = new ProbeStep { Id = "—", Title = "чтение цилиндрических граней" };
        return ReadFaces(part, prepared).Where(f => f.IsCylinder).ToList();
    }

    public static List<FaceReading> ReadFaces(ksPart part, ProbeStep step)
    {
        var list = new List<FaceReading>();
        if (Faces(part) is not { } faces)
        {
            step.Observe("API5: GetMainBody()/FaceCollection() недоступны — граней не видно.");
            return list;
        }

        var count = SafeInt(faces.GetCount) ?? 0;
        for (var f = 0; f < count; f++)
        {
            var reading = new FaceReading { Index = f };
            object? element = null;
            try
            {
                element = faces.GetByIndex(f);
                reading.ClrType = RuntimeName(element);
                var face = element.AsFace();
                reading.EntityType = (element as ksEntity)?.type;
                reading.Name = (element as ksEntity)?.name;
                if (face is null)
                {
                    reading.Notes.Add("элемент не отвечает ksFaceDefinition");
                    list.Add(reading);
                    continue;
                }

                reading.Area = SafeDouble(() => face.GetArea(LengthMm));
                if (SafeObject(face.GetSurface) is not ksSurface surface)
                {
                    reading.Notes.Add("GetSurface() → null");
                    list.Add(reading);
                    continue;
                }

                var parameters = SafeObject(surface.GetSurfaceParam);
                reading.SurfaceParamType = RuntimeName(parameters);
                switch (parameters)
                {
                    case ksCylinderParam cylinder:
                        reading.CylinderRadius = SafeDouble(() => cylinder.radius);
                        reading.CylinderHeight = SafeDouble(() => cylinder.height);
                        ReadPlacement(SafeObject(cylinder.GetPlacement), reading, "cylinder");
                        break;
                    case ksPlaneParam plane:
                        ReadPlacement(SafeObject(plane.GetPlacement), reading, "plane");
                        break;
                }
            }
            catch (Exception ex)
            {
                reading.Notes.Add(HResult.Describe(ex));
            }

            list.Add(reading);
        }

        return list;
    }

    private static void ReadPlacement(object? placementObject, FaceReading reading, string which)
    {
        if (placementObject is not ksPlacement placement)
        {
            reading.Notes.Add(which + ": GetPlacement() → " + RuntimeName(placementObject));
            return;
        }

        // GetOrigin/GetVector are out-parameter methods, and a lambda cannot carry the values out to
        // the caller — so the pair is read through local functions that return the array.
        double[]? Origin()
        {
            try
            {
                return placement.GetOrigin(out var x, out var y, out var z) ? new[] { x, y, z } : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        double[]? Axis()
        {
            try
            {
                return placement.GetVector(2, out var x, out var y, out var z) ? new[] { x, y, z } : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (which == "cylinder")
        {
            reading.CylinderOrigin = Origin();
            reading.CylinderAxis = Axis();
        }
        else
        {
            reading.PlaneOrigin = Origin();
            reading.PlaneNormal = Axis();
        }
    }

    /// <summary>The feature tree, through the only route P2.3 proved: <c>GetFeature()</c> then
    /// <c>SubFeatureCollection(through, lib)</c>. <c>doc.FeatureCollection(...)</c> returns null on
    /// every objType and is deliberately not retried.</summary>
    public static List<FeatureReading> ReadFeatures(ksPart part, ProbeStep step)
    {
        var list = new List<FeatureReading>();
        try
        {
            if (part.GetFeature() is not ksFeature root)
            {
                step.Observe("API5: part.GetFeature() вернул null — дерева признаков нет.");
                return list;
            }

            if (root.SubFeatureCollection(true, false) is not ksFeatureCollection collection)
            {
                step.Observe("API5: SubFeatureCollection(true,false) вернул null.");
                return list;
            }

            var count = collection.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = collection.GetByIndex(i);
                var feature = element as ksFeature;
                var entity = element as ksEntity;
                list.Add(new FeatureReading
                {
                    Index = i,
                    ClrType = RuntimeName(element),
                    Name = feature?.name ?? entity?.name,
                    Type = feature?.type ?? entity?.type,
                    TypeName = (feature?.type ?? entity?.type) is { } rawType ? ObjectTypeName(rawType) : "<нет>",
                    Created = entity is null ? null : SafeBool(entity.IsCreated),
                    Entity = entity,
                    // Measured in R.0d: on a live tree BOTH casts can disagree on the very same object
                    // — `as ksFeature` succeeded for 2 of 2 elements while `as ksEntity` succeeded for 0.
                    // So `Entity` being null is NOT evidence that the element is missing, and a caller
                    // that needs the definition must take the interface that answers. Both are carried.
                    Feature = feature,
                });
            }
        }
        catch (Exception ex)
        {
            step.Observe("API5: чтение дерева признаков прервано: " + HResult.Describe(ex));
        }

        return list;
    }

    /// <summary>Name of a <c>ksObj3dTypeEnum</c> constant for a numeric type, by lookup in the
    /// constants assembly the type actually comes from.</summary>
    public static string ObjectTypeName(int value)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var enumType = assembly.GetType("ksObj3dTypeEnum") ?? assembly.GetType("Kompas6Constants3D.ksObj3dTypeEnum");
            if (enumType is null)
            {
                continue;
            }

            foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetRawConstantValue() is int constant && constant == value)
                {
                    return field.Name;
                }
            }
        }

        return "<вне ksObj3dTypeEnum>";
    }

    private static ksFaceDefinition? AsFace(this object? element) =>
        element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;

    public sealed class FaceReading
    {
        public int Index { get; set; }

        public string? ClrType { get; set; }

        public string? Name { get; set; }

        public int? EntityType { get; set; }

        public double? Area { get; set; }

        public string? SurfaceParamType { get; set; }

        public double? CylinderRadius { get; set; }

        public double? CylinderHeight { get; set; }

        public double[]? CylinderOrigin { get; set; }

        public double[]? CylinderAxis { get; set; }

        public double[]? PlaneOrigin { get; set; }

        public double[]? PlaneNormal { get; set; }

        public List<string> Notes { get; } = new();

        public bool IsCylinder => CylinderRadius is not null;

        public string Describe() =>
            "[" + Index + "] " + (Name ?? "<без имени>") + " type=" + Raw(EntityType)
            + " площадь=" + Num(Area)
            + (CylinderRadius is null ? string.Empty : " ЦИЛИНДР r=" + Num(CylinderRadius) + " h=" + Num(CylinderHeight)
                + " начало=" + Point(CylinderOrigin) + " ось=" + Point(CylinderAxis))
            + (PlaneOrigin is null ? string.Empty : " плоскость начало=" + Point(PlaneOrigin) + " нормаль=" + Point(PlaneNormal))
            + (Notes.Count == 0 ? string.Empty : " {" + string.Join("; ", Notes) + "}");

        public static string Point(double[]? value) =>
            value is null ? "<нет>" : "(" + string.Join(", ", value.Select(v => Num(v))) + ")";
    }

    public sealed class FeatureReading
    {
        public int Index { get; set; }

        public string? ClrType { get; set; }

        public string? Name { get; set; }

        public int? Type { get; set; }

        public string? TypeName { get; set; }

        public bool? Created { get; set; }

        /// <summary>
        /// The live element, so a caller can ask it for its definition without walking the tree a
        /// second time.
        /// </summary>
        /// <remarks>
        /// Added because walking the tree twice does not work: a second
        /// <c>SubFeatureCollection(true, false)</c> call returns an <b>empty</b> collection in the same
        /// session, so a step that enumerates features once for their names and again for their
        /// definitions finds the names and no definitions. Holding the element from the one pass that
        /// is known to work removes the second walk entirely.
        /// </remarks>
        public ksEntity? Entity { get; set; }

        /// <summary>
        /// The same live element taken through the OTHER interface. This is the one that answers on a
        /// КОМПАС-written file's tree.
        /// </summary>
        /// <remarks>
        /// <b>Measured (R.0d, 17.09.2026).</b> On a live feature tree, <c>element as ksFeature</c> and
        /// <c>element as ksEntity</c> disagreed on <b>every</b> element: 2 of 2 were <c>ksFeature</c>,
        /// 0 of 2 were <c>ksEntity</c>. So <c>Entity == null</c> is not evidence that the element is
        /// absent — it is evidence that that particular cast was refused, which is a statement about the
        /// interop wrapper. A caller that needs the definition must use the interface that answers.
        /// </remarks>
        public ksFeature? Feature { get; set; }

        public string Describe() =>
            "[" + Index + "] «" + (Name ?? "<нет>") + "» type=" + Raw(Type) + " (" + TypeName + ")"
            + " создан=" + Raw(Created) + " CLR=" + ClrType;
    }
}
