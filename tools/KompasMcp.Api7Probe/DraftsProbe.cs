using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>Probe DR — «Уклон» от основания: works the documented API5 route, and what unit does
/// <c>angle</c> take and what does <c>direction</c> mean by measurement.</summary>
/// <remarks>
/// DOC: <c>ksinclinedefinition.html</c> (definition from <c>ksEntity::GetDefinition</c>),
/// <c>ksinclinedefinition_facearray.html</c> (<c>FaceArray()</c> → <c>ksEntityCollection</c> of faces),
/// <c>ksinclinedefinition_setplane.html</c> (<c>SetPlane(plane)</c>, «плоский объект»),
/// <c>ksinclinedefinition_angle.html</c> (<c>double</c>, unit NOT named),
/// <c>ksinclinedefinition_direction.html</c> («TRUE - наружу, FALSE - внутрь»).
/// MEASURED TARGET: for a 100×80×50 bar and a 10° draft of the +X face the analytic volumes are
/// 382367.30 (material removed) and 417632.70 (material added); radians would give 335163.92, so the
/// volume alone separates degrees from radians.
/// INVARIANT: <c>Create()=true</c> proves nothing — the volume is read back and compared.
/// History: docs/decisions/probes.md#drafts
/// </remarks>
internal sealed class DraftsProbe
{
    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short BaseExtrusion = 24;
    private const short InclineType = 42;
    private const int LineStyle = 1;

    private const double BarX = 100d;
    private const double BarY = 80d;
    private const double BarZ = 50d;
    private const double AngleDeg = 10d;
    private const double VolumeTolerance = 0.5d;

    private static double VolumeV0 => BarX * BarY * BarZ;
    private static double VolumeInward => 382367.30d;
    private static double VolumeOutward => 417632.70d;
    private static double VolumeRadians => 335163.92d;

    private readonly ProbeReport _report;
    private readonly Options _options;

    private KompasObject _app = null!;

    public DraftsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        try
        {
            Launch();
            var document = BuildBar();
            if (document is null)
            {
                return;
            }

            var part = (ksPart)document.GetPart(-1);
            var faces = FaceHandles(part);
            var target = faces.FirstOrDefault(f => f.Normal is { } n
                && Math.Abs(n[0] - 1d) < 0.01 && Math.Abs(n[1]) < 0.01 && Math.Abs(n[2]) < 0.01);
            if (target is null)
            {
                _report.Begin("DR.2", "Грань +X найдена", "Есть ли что уклонять?")
                    .Fail("грань с нормалью +X не найдена среди " + faces.Count + " граней");
                return;
            }

            Incline(document, part, target, outward: true);
            Incline(document, part, target, outward: false);
        }
        catch (Exception ex)
        {
            _report.Begin("DR.X", "Необработанное исключение пробы DR").Fail(ex.ToString());
        }
    }

    private void Launch()
    {
        var step = _report.Begin("DR.1", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        step.Pass("сеанс поднят");
    }

    /// <summary>The acceptance reference bar: a 0..100 × 0..80 rectangle on XOY extruded up by 50.</summary>
    private ksDocument3D? BuildBar()
    {
        var step = _report.Begin("DR.2", "Брус 100×80×50 выдавливанием", "V0 = 400000 мм³?");
        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, true) != true)
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane
            || part.NewEntity(SketchType) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            step.Fail("Плоскость или эскиз не получены.");
            return null;
        }

        sketch.name = "dr-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();
        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("BeginEdit() вернул не ksDocument2D.");
            return null;
        }

        try
        {
            editor.ksLineSeg(0d, 0d, BarX, 0d, LineStyle);
            editor.ksLineSeg(BarX, 0d, BarX, BarY, LineStyle);
            editor.ksLineSeg(BarX, BarY, 0d, BarY, LineStyle);
            editor.ksLineSeg(0d, BarY, 0d, 0d, LineStyle);
        }
        finally
        {
            sketchDefinition.EndEdit();
        }

        if (part.NewEntity(BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Fail("Базовое выдавливание не создано.");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0 /* etBlind */, BarZ, 0d, false);
        if (Api5.SafeBool(extrusion.Create) != true)
        {
            step.Fail("Create() выдавливания вернул false.");
            return null;
        }

        document.RebuildDocument();
        var volume = Api5.Volume(part);
        step.Data["volume_mm3"] = volume;
        step.Data["faces"] = Api5.FaceCount(part);
        var ok = volume is double v && Math.Abs(v - VolumeV0) <= VolumeTolerance;
        return ok
            ? Continue(step, "V0 = " + Api5.Num(volume) + " мм³, граней " + Api5.FaceCount(part), document)
            : (ksDocument3D?)Fail(step, "объём " + Api5.Num(volume) + " вместо 400000", null);
    }

    /// <summary>One documented API5 «Уклон» on the +X face with the XOY plane as base.</summary>
    /// <remarks>The base is the standard plane rather than a face because <c>SetPlane</c> is
    /// documented for a «плоский объект ksEntity»; a face base is a separate question for the
    /// adapter and is measured separately.</remarks>
    private void Incline(ksDocument3D document, ksPart part, FaceHandle face, bool outward)
    {
        var id = outward ? "DR.3" : "DR.4";
        var step = _report.Begin(id, "Уклон 10°, direction=" + (outward ? "TRUE" : "FALSE"),
            outward ? "TRUE = наружу по справке: объём вырастет?" : "FALSE = внутрь: объём уменьшится?");
        var volumeBefore = Api5.Volume(part);
        var featuresBefore = Api5.FaceCount(part);

        if (part.NewEntity(InclineType) is not ksEntity entity
            || entity.GetDefinition() is not ksInclineDefinition incline)
        {
            step.Fail("NewEntity(o3d_incline=42) или его определение не получены.");
            return;
        }

        entity.name = "dr-incline-" + (outward ? "out" : "in");

        var faceArray = incline.FaceArray();
        if (faceArray is null)
        {
            step.Fail("FaceArray() вернул null — массив уклоняемых граней недоступен.");
            return;
        }

        var added = Late.Call(faceArray, "Add", face.Element);
        step.Data["face_added"] = added;
        step.Observe("FaceArray().Add(грань +X) = " + added);

        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity basePlane)
        {
            step.Fail("Стандартная плоскость XOY не получена как объект основания.");
            return;
        }

        step.Data["set_plane"] = incline.SetPlane(basePlane);
        incline.angle = AngleDeg;
        incline.direction = outward;
        step.Data["angle_written"] = incline.angle;
        step.Data["direction_written"] = incline.direction;

        var created = Api5.SafeBool(entity.Create);
        step.Data["create"] = created;
        document.RebuildDocument();

        var volumeAfter = Api5.Volume(part);
        step.Data["volume_before"] = volumeBefore;
        step.Data["volume_after"] = volumeAfter;
        step.Data["faces_before"] = featuresBefore;
        step.Data["faces_after"] = Api5.FaceCount(part);

        // The read-back of the definition object, so "written" and "stored" are separate numbers.
        if (entity.GetDefinition() is ksInclineDefinition after)
        {
            step.Data["angle_read"] = Api5.SafeDouble(() => after.angle);
            step.Data["direction_read"] = Api5.SafeBool(() => after.direction);
            step.Observe("перечитано: angle=" + Api5.Num(step.Data["angle_read"] as double?)
                + " direction=" + Api5.Raw(step.Data["direction_read"]));
        }

        // The step does NOT assert what the help page promises: it records WHICH of the two analytic
        // volumes this flag produced. A draft of 10° can only give one of them, so a match proves the
        // unit is degrees and names the material direction; it says nothing more.
        step.Observe("объём после: " + Api5.Num(volumeAfter) + " мм³; эталоны 10°: убыль "
            + Api5.Num(VolumeInward) + ", прирост " + Api5.Num(VolumeOutward)
            + ", радианы " + Api5.Num(VolumeRadians));

        if (created != true)
        {
            step.Fail("Create() вернул " + Api5.Raw(created) + "; объём " + Api5.Num(volumeAfter));
        }
        else if (volumeAfter is double va && Math.Abs(va - VolumeInward) <= VolumeTolerance)
        {
            step.Data["material"] = "removed";
            step.Pass("direction=" + (outward ? "TRUE" : "FALSE") + " дал " + Api5.Num(volumeAfter)
                + " - материал УБЫЛ (градусы; справка обещает TRUE = «наружу»)");
        }
        else if (volumeAfter is double vb && Math.Abs(vb - VolumeOutward) <= VolumeTolerance)
        {
            step.Data["material"] = "added";
            step.Pass("direction=" + (outward ? "TRUE" : "FALSE") + " дал " + Api5.Num(volumeAfter)
                + " - материал ПРИБЫЛ (градусы)");
        }
        else
        {
            step.Fail("объём " + Api5.Num(volumeAfter) + " не совпал ни с одним эталоном 10° ("
                + Api5.Num(VolumeInward) + " / " + Api5.Num(VolumeOutward) + ", радианы "
                + Api5.Num(VolumeRadians) + ")");
        }
    }

    private sealed record FaceHandle(int Index, object Element, double[]? Normal);

    /// <summary>Live face elements with their plane normals: the normals come from
    /// <see cref="Api5.ReadFaces"/>, the elements from the same collection in the same order, because
    /// <c>FaceArray().Add</c> needs the live object and the reading keeps only a description.</summary>
    private static List<FaceHandle> FaceHandles(ksPart part)
    {
        var list = new List<FaceHandle>();
        try
        {
            var readings = Api5.ReadFaces(part, new ProbeStep { Id = "—", Title = "чтение граней" });
            if (part.GetMainBody() is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces)
            {
                return list;
            }

            var count = Api5.SafeInt(faces.GetCount) ?? 0;
            for (var i = 0; i < count && i < readings.Count; i++)
            {
                var element = faces.GetByIndex(i);
                if (element is not null)
                {
                    list.Add(new FaceHandle(i, element, readings[i].PlaneNormal));
                }
            }
        }
        catch (Exception)
        {
            // A face collection that cannot be walked is reported by the caller as "not found".
        }

        return list;
    }

    private static ksDocument3D Continue(ProbeStep step, string conclusion, ksDocument3D document)
    {
        step.Pass(conclusion);
        return document;
    }

    private static ksDocument3D? Fail(ProbeStep step, string reason, ksDocument3D? document)
    {
        step.Fail(reason);
        return document;
    }
}
