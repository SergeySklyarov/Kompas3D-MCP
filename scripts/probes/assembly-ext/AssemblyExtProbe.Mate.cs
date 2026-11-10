using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Scenario B: mate types outside <c>mates-minimal-v1</c> - <c>mc_Symmetric</c> and
/// <c>mc_Dependent</c> - each on its OWN fixture so the two constraints cannot mask one another.</summary>
/// <remarks>DOC: <c>mateconstrainttype.html</c> (mc_Symmetric=11, mc_Dependent=14),
/// <c>imate3dsymmetry_plane.html</c> (Plane: IModelObject), <c>imate3ddependentposition_*</c>
/// (BySample, SampleObject1/2: IPart7), <c>imateconstraints3d_add.html</c>.
/// The symmetry row is geometric: the second component's origin must land on the mirror of the first one
/// about the assembly's YOZ plane - a number derived from the fixture, not from the write.</remarks>
internal sealed partial class AssemblyExtProbe
{
    private void MateScenario()
    {
        Symmetric();
        Dependent();
    }

    private void Symmetric()
    {
        var source = PartFile(Path.Combine(_work, "sym-part.m3d"), PartH);
        var (doc5, _, top) = NewDocument(isPart: false);
        var path = Path.Combine(_work, "sym-asm.m3d");
        Insert(top, source, Place(0, 0, 0));
        Insert(top, source, Place(-60, 0, 0));
        doc5.RebuildDocument();

        var plane = DefaultPlane(ksObj3dTypeEnum.o3d_planeYOZ);
        var firstPart = Part5ByOrigin(0, 0, 0);
        var secondPart = Part5ByOrigin(-60, 0, 0);
        Record("sym.parts5", "оба компонента найдены в API5-коллекции по размещению",
            $"лицо1={firstPart is not null}, лицо2={secondPart is not null}",
            firstPart is not null && secondPart is not null ? "PASS" : "FAIL");
        Record("sym.faces_scan", "грани первого компонента (локальные координаты плоскости и нормаль)",
            firstPart is null ? "компонент не найден" : DescribeFaces(firstPart), "измерено");

        var firstFace = firstPart is null ? null : FaceByNormal(firstPart, axis: 0, positive: true, coordinate: 20);
        var secondFace = secondPart is null ? null : FaceByNormal(secondPart, axis: 0, positive: false, coordinate: 0);
        var firstObject = firstFace ?? (firstPart is null ? null : FirstFace(firstPart));
        var faceObjects = firstObject is not null && secondFace is not null;
        Record("sym.objects", "плоскость симметрии (YOZ) и два объекта сопряжения найдены",
            $"плоскость={plane is not null}, объект1={firstObject?.GetType().Name ?? "нет"}, " +
            $"объект2={secondFace?.GetType().Name ?? "нет"}",
            plane is not null && faceObjects ? "PASS" : "FAIL");
        if (plane is null || firstObject is null || secondFace is null)
        {
            return;
        }

        var mate = SafeAdd(top, MateConstraintType.mc_Symmetric);
        if (mate is null)
        {
            Record("sym.add", "IMateConstraints3D.Add(mc_Symmetric=11)", "null", "FAIL");
            return;
        }

        var symmetry = mate as IMate3DSymmetry;
        Record("sym.interface", "объект сопряжения отвечает QI(IMate3DSymmetry)",
            symmetry is null ? "нет" : $"ConstraintType = {mate.ConstraintType} ({(int)mate.ConstraintType})",
            symmetry is null ? "FAIL" : "PASS", "IMate3DSymmetry, mc_Symmetric");
        if (symmetry is null)
        {
            return;
        }

        mate.BaseObject1 = firstObject;
        mate.BaseObject2 = secondFace;
        symmetry.Plane = plane;
        var updated = SafeBool(mate.Update);
        top.RebuildModel(false);
        doc5.RebuildDocument();

        Record("sym.update", "Update() симметрии; Valid подтверждает сопряжение",
            $"Update()={updated}, Valid={mate.Valid}", mate.Valid ? "PASS" : "FAIL", "Valid = true");
        Record("sym.plane_read_back", "Plane перечитана из модели",
            symmetry.Plane is null ? "не прочитана" : "прочитана",
            symmetry.Plane is null ? "FAIL" : "PASS", "не null");

        var origin = secondPart is null ? "не читается" : Origin(PlacementOf(secondPart));
        var mirrored = secondPart is not null && PlacementOf(secondPart) is { } m && Math.Abs(m[12] + 20) < 0.01;
        Record("sym.geometry", "второй компонент встал зеркально первому относительно x = 0",
            $"начало второго: {origin}", mirrored ? "PASS" : "измерено", "(−20, 0, 0)");
        Fact("sym_second_origin", origin);

        var saved = doc5.SaveAs(path);
        Record("sym.save", "SaveAs сборки с сопряжением-симметрией", $"{saved}", saved ? "PASS" : "FAIL", "true");
        if (!Reopen(path, out var reopened) || reopened.TopPart is not IPart7 reopenTop)
        {
            Record("sym.reopen", "сборка открывается повторно", "не открылась", "FAIL");
            return;
        }

        var reopenedMate = reopenTop.MateConstraints?.MateConstraint3D[0];
        Record("sym.reopen", "симметрия читается после сохранения и переоткрытия",
            reopenedMate is null
                ? "сопряжений нет"
                : $"{(int)reopenedMate.ConstraintType}:{reopenedMate.ConstraintType}, Valid={reopenedMate.Valid}",
            reopenedMate?.ConstraintType == MateConstraintType.mc_Symmetric ? "PASS" : "FAIL", "mc_Symmetric");
        Fact("sym_assembly", path);
    }

    private void Dependent()
    {
        var source = PartFile(Path.Combine(_work, "dep-part.m3d"), PartH);
        var (doc5, _, top) = NewDocument(isPart: false);
        var path = Path.Combine(_work, "dep-asm.m3d");
        var dependent = Insert(top, source, Place(0, 0, 0));
        var basePart = Insert(top, source, Place(-60, 0, 0));
        doc5.RebuildDocument();

        var mate = SafeAdd(top, MateConstraintType.mc_Dependent);
        if (mate is null)
        {
            Record("dep.add", "IMateConstraints3D.Add(mc_Dependent=14)", "null", "FAIL");
            return;
        }

        var position = mate as IMate3DDependentPosition;
        Record("dep.interface", "объект сопряжения отвечает QI(IMate3DDependentPosition)",
            position is null ? "нет" : $"ConstraintType = {mate.ConstraintType} ({(int)mate.ConstraintType})",
            position is null ? "FAIL" : "PASS", "IMate3DDependentPosition, mc_Dependent");
        if (position is null)
        {
            return;
        }

        mate.BaseObject1 = dependent as IModelObject;
        mate.BaseObject2 = basePart as IModelObject;
        position.BySample = true;
        position.SampleObject1 = dependent as Part7;
        position.SampleObject2 = basePart as Part7;
        var updated = SafeBool(mate.Update);
        top.RebuildModel(false);
        doc5.RebuildDocument();

        Record("dep.update", "Update() зависимого положения; Valid подтверждает сопряжение",
            $"Update()={updated}, Valid={mate.Valid}", mate.Valid ? "PASS" : "FAIL", "Valid = true");
        Record("dep.read_back", "BySample и образцы перечитаны из модели",
            $"BySample={SafeBool(() => position.BySample)}, " +
            $"Sample1={(position.SampleObject1 is null ? "null" : "есть")}, " +
            $"Sample2={(position.SampleObject2 is null ? "null" : "есть")}",
            "измерено", "все три поля читаются");

        // Geometry: move the BASE sample 50 mm along x and ask whether the dependent component follows.
        // A dependent position that transfers nothing is a name, not a constraint - and the row says which
        // it is instead of guessing.
        var before = ComponentMatrix(top, dependent);
        var baseBefore = ComponentMatrix(top, basePart);
        MoveComponent(basePart, Place(-10, 0, 0), top, doc5);
        var after = ComponentMatrix(top, dependent);
        var baseAfter = ComponentMatrix(top, basePart);
        var baseDelta = baseAfter is null || baseBefore is null ? double.NaN : baseAfter[12] - baseBefore[12];
        var dependentDelta = after is null || before is null ? double.NaN : after[12] - before[12];
        Record("dep.geometry", "зависимый компонент следует за базовым (смещение базы по x)",
            $"база {Origin(baseBefore)} → {Origin(baseAfter)} (Δ={baseDelta:0.###}); " +
            $"зависимый {Origin(before)} → {Origin(after)} (Δ={dependentDelta:0.###})",
            Math.Abs(baseDelta - dependentDelta) < 0.01 && Math.Abs(dependentDelta) > 1 ? "PASS" : "FAIL",
            "Δ зависимого = Δ базы");
        Fact("dep_origins", $"{Origin(before)} -> {Origin(after)}; база Δ={baseDelta:0.###}");

        var saved = doc5.SaveAs(path);
        Record("dep.save", "SaveAs сборки с зависимым положением", $"{saved}", saved ? "PASS" : "FAIL", "true");
        if (!Reopen(path, out var reopened) || reopened.TopPart is not IPart7 reopenTop)
        {
            Record("dep.reopen", "сборка открывается повторно", "не открылась", "FAIL");
            return;
        }

        var reopenedMate = reopenTop.MateConstraints?.MateConstraint3D[0];
        Record("dep.reopen", "зависимое положение читается после сохранения и переоткрытия",
            reopenedMate is null
                ? "сопряжений нет"
                : $"{(int)reopenedMate.ConstraintType}:{reopenedMate.ConstraintType}, Valid={reopenedMate.Valid}",
            reopenedMate?.ConstraintType == MateConstraintType.mc_Dependent ? "PASS" : "FAIL", "mc_Dependent");
        Fact("dep_assembly", path);
    }

    private IMateConstraint3D? SafeAdd(IPart7 top, MateConstraintType type)
    {
        try
        {
            return top.MateConstraints?.Add(type);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record($"mate.add_fault.{(int)type}", $"Add({type})", $"{ex.GetType().Name}: {ex.Message}", "FAIL");
            return null;
        }
    }

    /// <summary>Default plane of the assembly's top component, moved into API7 as an <c>IModelObject</c>.
    /// DOC: <c>ipart7_defaultobject.html</c> — the predefined elements (planes, axis, CS).</summary>
    private IModelObject? DefaultPlane(ksObj3dTypeEnum kind)
    {
        try
        {
            var top5 = _doc5?.GetPart(-1) as ksPart;
            if (top5?.GetDefaultEntity((short)kind) is not ksEntity entity)
            {
                return null;
            }

            return _app5?.TransferInterface(entity, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The first face of the component, moved into API7 - a fallback object when the plane search
    /// finds nothing, so the mate itself can still be measured.</summary>
    private IModelObject? FirstFace(ksPart part)
    {
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0
                || bodies.GetByIndex(0) is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces
                || faces.GetCount() == 0)
            {
                return null;
            }

            return _app5?.TransferInterface(faces.GetByIndex(0), (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Plane origin and normal of every face of the component's first body, in the component's
    /// own coordinates - the raw data the face choice rests on.</summary>
    private string DescribeFaces(ksPart part)
    {
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return "BodyCollection() → null";
            }

            var bodyCount = bodies.GetCount();
            if (bodyCount == 0 || bodies.GetByIndex(0) is not ksBody body)
            {
                return $"тел {bodyCount}";
            }

            if (body.FaceCollection() is not ksFaceCollection faces)
            {
                return $"тел {bodyCount}, FaceCollection() → null";
            }

            var described = new List<string> { $"тел {bodyCount}, граней {faces.GetCount()}" };
            for (var index = 0; index < faces.GetCount(); index++)
            {
                if (faces.GetByIndex(index) is not ksFaceDefinition face
                    || face.GetSurface() is not ksSurface surface
                    || surface.GetSurfaceParam() is not ksPlaneParam planeParam
                    || planeParam.GetPlacement() is not ksPlacement placement
                    || !placement.GetOrigin(out var x, out var y, out var z)
                    || !placement.GetVector(2, out var vx, out var vy, out var vz))
                {
                    described.Add($"{index}: не плоскость или не читается");
                    continue;
                }

                described.Add(
                    $"{index}: origin=({x:0.##}, {y:0.##}, {z:0.##}) normal=({vx:0.##}, {vy:0.##}, {vz:0.##})");
            }

            return string.Join("; ", described);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>The face whose plane lies at <paramref name="coordinate"/> along <paramref name="axis"/> in
    /// the COMPONENT's own coordinates and whose normal points to the requested side.</summary>
    private IModelObject? FaceByNormal(ksPart part, int axis, bool positive, double coordinate)
    {
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0
                || bodies.GetByIndex(0) is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces)
            {
                return null;
            }

            for (var index = 0; index < faces.GetCount(); index++)
            {
                var raw = faces.GetByIndex(index);
                if (raw is not ksFaceDefinition face
                    || face.GetSurface() is not ksSurface surface
                    || surface.GetSurfaceParam() is not ksPlaneParam planeParam
                    || planeParam.GetPlacement() is not ksPlacement placement
                    || !placement.GetOrigin(out var x, out var y, out var z)
                    || !placement.GetVector(2, out var vx, out var vy, out var vz))
                {
                    continue;
                }

                var at = axis switch { 0 => x, 1 => y, _ => z };
                var along = axis switch { 0 => vx, 1 => vy, _ => vz };
                if (Math.Abs(at - coordinate) > 0.01 || (positive ? along < 0.9 : along > -0.9))
                {
                    continue;
                }

                return _app5?.TransferInterface(raw, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The API5 component whose placement origin is the requested point.</summary>
    private ksPart? Part5ByOrigin(double x, double y, double z)
    {
        for (var index = 0; index < Part5Count(); index++)
        {
            var part = Part5At(index);
            if (part is null || PlacementOf(part) is not { } matrix)
            {
                continue;
            }

            if (Math.Abs(matrix[12] - x) < 0.01 && Math.Abs(matrix[13] - y) < 0.01 && Math.Abs(matrix[14] - z) < 0.01)
            {
                return part;
            }
        }

        return null;
    }

    private void MoveComponent(IPart7 component, double[] matrix, IPart7 top, ksDocument3D doc5)
    {
        try
        {
            if (component.Placement is { } placement && placement.InitByMatrix3D(ToVariant(matrix)))
            {
                component.UpdatePlacement(false);
                top.RebuildModel(false);
                doc5.RebuildDocument();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("move.fault", "перемещение компонента", $"{ex.GetType().Name}: {ex.Message}", "FAIL");
        }
    }
}
