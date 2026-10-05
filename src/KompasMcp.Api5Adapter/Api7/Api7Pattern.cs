using System.Runtime.InteropServices;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>Typed API7 operations over patterns and the mirror pattern (SM-18 / SM-19 / SM-23).</summary>
/// <remarks>DOC: the basis is a published help page, not a name found in the interface — the "numeric
/// type → interface" mapping is from SDK page <c>copytype.html</c>, members from the property pages.
/// LIMIT: <c>Vector1/Vector2</c> are not written — in <c>kAPI7.tlb</c> they have getters only and the
/// vendor interop declares neither; direction is set by an axis (open question OQ-B-03, not bypassed).
/// INVARIANT: no <c>NewEntity</c>, no <c>Create()</c> — a mixed API5/API7 lifetime makes <c>Create()</c>
/// return <c>true</c> on nothing built; patterns come ONLY from <c>IModelContainer.FeaturePatterns.Add</c>.
/// History: docs/decisions/adapter-api7.md#pattern</remarks>
internal static class Api7Pattern
{
    /// <summary>The factory numeric type for a grid or circular pattern.</summary>
    /// <remarks>A pattern OF BODIES is a separate type, not a flag: <c>o3d_BodiesMeshCopy=528</c> /
    /// <c>o3d_BodiesCircularCopy=529</c> versus <c>o3d_meshCopy=35</c> / <c>o3d_circularCopy=36</c>.</remarks>
    public static ksObj3dTypeEnum LinearType(PatternCopyKind kind) => kind switch
    {
        PatternCopyKind.Bodies => ksObj3dTypeEnum.o3d_BodiesMeshCopy,
        _ => ksObj3dTypeEnum.o3d_meshCopy,
    };

    public static ksObj3dTypeEnum CircularType(PatternCopyKind kind) => kind switch
    {
        PatternCopyKind.Bodies => ksObj3dTypeEnum.o3d_BodiesCircularCopy,
        _ => ksObj3dTypeEnum.o3d_circularCopy,
    };

    public static ksObj3dTypeEnum MirrorType(PatternMirrorMode mode) => mode switch
    {
        PatternMirrorMode.AllBodies => ksObj3dTypeEnum.o3d_mirrorAllOperation,
        _ => ksObj3dTypeEnum.o3d_mirrorOperation,
    };

    /// <summary>The grid-pattern building method. Values read from the interop constants assembly
    /// (<c>ksLinearPatternBuildingTypeEnum</c>: <c>ksLPSaveAll=0</c>, <c>ksLPSaveAlongPerimeter=1</c>,
    /// <c>ksLPSaveAlongAxially=2</c>, <c>ksLPChessOrderByAxis1=3</c>, <c>ksLPChessOrderByAxis2=4</c>).
    /// An unknown word is rejected by the caller before COM; here it is a default.</summary>
    public static ksLinearPatternBuildingTypeEnum LinearBuilding(string name) => name switch
    {
        "save_all" => ksLinearPatternBuildingTypeEnum.ksLPSaveAll,
        "save_along_perimeter" => ksLinearPatternBuildingTypeEnum.ksLPSaveAlongPerimeter,
        "save_along_axially" => ksLinearPatternBuildingTypeEnum.ksLPSaveAlongAxially,
        "chess_order_by_axis1" => ksLinearPatternBuildingTypeEnum.ksLPChessOrderByAxis1,
        "chess_order_by_axis2" => ksLinearPatternBuildingTypeEnum.ksLPChessOrderByAxis2,
        _ => ksLinearPatternBuildingTypeEnum.ksLPSaveAll,
    };

    public static ksCircularPatternBuildingTypeEnum CircularBuilding(string name) => name switch
    {
        "save_all" => ksCircularPatternBuildingTypeEnum.ksCPSaveAll,
        "chess_order_by_axis1" => ksCircularPatternBuildingTypeEnum.ksCPChessOrderByAxis1,
        "chess_order_by_axis2" => ksCircularPatternBuildingTypeEnum.ksCPChessOrderByAxis2,
        _ => ksCircularPatternBuildingTypeEnum.ksCPSaveAll,
    };

    /// <summary>The body action type for <c>IChooseBodies7.ChooseBodiesType</c>. Values read from the
    /// interop constants assembly (<c>ksChooseBodiesType</c>: <c>ksNewBody=0</c>,
    /// <c>ksAutomaticDefinition=1</c>, <c>ksManualEditing=2</c>, <c>ksAllBodies=3</c>).</summary>
    public static ksChooseBodiesType ChooseBodies(string name) => name switch
    {
        "new_body" => ksChooseBodiesType.ksNewBody,
        "automatic" => ksChooseBodiesType.ksAutomaticDefinition,
        "manual" => ksChooseBodiesType.ksManualEditing,
        "all_bodies" => ksChooseBodiesType.ksAllBodies,
        _ => ksChooseBodiesType.ksAllBodies,
    };

    /// <summary>The number of pattern features in the API7 collection. null — not read (not "zero").</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.FeaturePatterns?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Create a grid pattern, write its parameters, then <c>Update()</c>.</summary>
    /// <remarks>Returns (object, refusal reason): the caller must distinguish a KOMPAS refusal from an
    /// adapter crash, and "could not" must not look like "created". INVARIANT: the write order is part
    /// of the contract — <c>InitialObjects</c> and the axes are written BEFORE steps and counts,
    /// because a pattern without source objects and an axis does not exist as a feature.</remarks>
    public static (ILinearPattern? Pattern, string? Failure) TryCreateLinear(
        IModelContainer container,
        PatternCopyKind kind,
        object[] sources7,
        IModelObject axis1,
        double step1,
        int count1,
        double? angle1,
        bool direction1,
        bool boundary1,
        IModelObject? axis2,
        double step2,
        int count2,
        double? angle2,
        bool direction2,
        bool boundary2,
        string buildingType,
        bool geometryPattern)
    {
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима");
            }

            if (patterns.Add(LinearType(kind)) is not ILinearPattern linear)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(ILinearPattern)");
            }

            linear.InitialObjects = sources7;
            linear.Axis1 = axis1;

            linear.Step1 = step1;
            linear.Count1 = count1;
            // THE ANGLE IS WRITTEN ONLY WHEN SUPPLIED. MEASURED by a run: writing Angle2 = 0 (the
            // default of a mandatory field) merged the second direction with the first, degenerating
            // the rectangular grid into a line. The model's default angle between directions is 90°,
            // and an unsupplied field must leave that value, not overwrite it with zero.
            if (angle1 is double a1)
            {
                linear.Angle1 = a1;
            }

            linear.Direction1 = direction1;
            linear.BoundaryInstancesStepFactor1 = boundary1;

            // The second direction is written ONLY when it takes part: Count2 = 1 with an unsupplied
            // axis would leave in the model a field that the request did not have.
            if (axis2 is not null && count2 > 1)
            {
                linear.Axis2 = axis2;
                linear.Step2 = step2;
                linear.Count2 = count2;
                if (angle2 is double a2)
                {
                    linear.Angle2 = a2;
                }

                linear.Direction2 = direction2;
                linear.BoundaryInstancesStepFactor2 = boundary2;
            }
            else
            {
                linear.Count2 = 1;
            }

            linear.BuildingType = LinearBuilding(buildingType);
            linear.GeometryPattern = geometryPattern;

            return linear.Update()
                ? (linear, null)
                : (linear, "ILinearPattern.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Create a circular pattern, write its parameters, then <c>Update()</c>.</summary>
    public static (ICircularPattern? Pattern, string? Failure) TryCreateCircular(
        IModelContainer container,
        PatternCopyKind kind,
        object[] sources7,
        IModelObject axis,
        int count1,
        double step1,
        int count2,
        double step2Deg,
        double stepByAxis,
        bool boundary1,
        bool boundary2,
        bool reverse,
        bool saveInitialOrientation,
        string buildingType,
        bool geometryPattern)
    {
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима");
            }

            if (patterns.Add(CircularType(kind)) is not ICircularPattern circular)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(ICircularPattern)");
            }

            circular.InitialObjects = sources7;
            circular.Axis = axis;

            circular.Count1 = count1;
            circular.Step1 = step1;
            circular.BoundaryInstancesStepFactor1 = boundary1;

            circular.Count2 = count2;
            circular.Step2 = step2Deg;
            circular.BoundaryInstancesStepFactor2 = boundary2;

            circular.StepByAxis = stepByAxis;
            circular.ReverseDirection = reverse;
            circular.SaveInitialOrientation = saveInitialOrientation;
            circular.BuildingType = CircularBuilding(buildingType);
            circular.GeometryPattern = geometryPattern;

            return circular.Update()
                ? (circular, null)
                : (circular, "ICircularPattern.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Create a mirror pattern, write its parameters, then <c>Update()</c>.</summary>
    /// <remarks>For the <c>all_bodies</c> kind, <c>IChooseBodies7</c> is also set: MEASURED
    /// (<c>InteropScan</c>) that the mirror-pattern object answers this interface (DOC
    /// <c>copytype.html</c> agrees: «Дополнительно имеет интерфейс выбора тел IChooseBodies7»). A
    /// missing interface is a named note, not silence: without it the body choice is not expressed, and
    /// the caller sees that.</remarks>
    public static (IMirrorPattern? Pattern, string? Failure, IReadOnlyList<string> Notes) TryCreateMirror(
        IModelContainer container,
        PatternMirrorMode mode,
        object[] sources7,
        IModelObject plane,
        bool saveInitialObjects,
        string chooseBodiesType)
    {
        var notes = new List<string>();
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима", notes);
            }

            if (patterns.Add(MirrorType(mode)) is not IMirrorPattern mirror)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(IMirrorPattern)", notes);
            }

            // InitialObjects is set only when the list is NOT empty: for "mirror all bodies" the input
            // is the part itself, and writing an empty array would assert "nothing chosen", which the
            // request did not do.
            if (sources7.Length > 0)
            {
                mirror.InitialObjects = sources7;
            }

            mirror.Plane = plane;
            mirror.SaveInitialObjects = saveInitialObjects;
            // WRITE AND IMMEDIATE READ-BACK IN ONE PLACE. Without a read right after the write one
            // cannot distinguish "the property was not accepted by COM" from "accepted and lost during
            // build", which are different defects.
            var flagReadBack = SafeBool(() => mirror.SaveInitialObjects);
            notes.Add($"save_initial_objects: записано {saveInitialObjects}, " +
                $"прочитано сразу после записи {flagReadBack}");
            if (flagReadBack is bool readBack && readBack != saveInitialObjects)
            {
                // THE REFUSAL IS NAMED, NOT HIDDEN, and named together with the help's reason. DOC
                // (imirrorpattern_saveinitialobjects.html) restricts the property to
                // o3d_mirrorAllOperation: «у других операций зеркального копирования возможность
                // скрыть экземпляры отсутствует». MEASURED on both operations: for 49, writing false
                // reads back false and bodies are replaced by reflections; for 48, it reads back true
                // and geometry does not change. The request is accepted, but the model's non-acceptance
                // is declared here rather than passed off as an applied property.
                notes.Add($"save_initial_objects НЕ принято моделью для {MirrorType(mode)}: " +
                    "справка ограничивает свойство только o3d_mirrorAllOperation, и измерение это " +
                    "подтверждает");
            }

            if (mode == PatternMirrorMode.AllBodies)
            {
                if (mirror is IChooseBodies7 choose)
                {
                    choose.ChooseBodiesType = ChooseBodies(chooseBodiesType);
                    if (sources7.Length > 0)
                    {
                        choose.Bodies = sources7;
                    }

                    notes.Add($"IChooseBodies7: ChooseBodiesType={chooseBodiesType}, тел {sources7.Length}");
                }
                else
                {
                    notes.Add("объект зеркального массива не отвечает QI(IChooseBodies7) — " +
                        "выбор тел не выражен; страница copytype.html объявляет этот интерфейс у " +
                        "o3d_mirrorAllOperation, и расхождение названо, а не скрыто");
                }
            }

            return mirror.Update()
                ? (mirror, null, notes)
                : (mirror, "IMirrorPattern.Update() вернул false — признак создан, но не построен", notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex), notes);
        }
    }

    /// <summary>Overwrite the parameters of an EXISTING pattern feature and call <c>Update()</c>.</summary>
    /// <remarks>INVARIANT: editing writes to the SAME object taken from the live
    /// <c>IModelContainer.FeaturePatterns</c>, not kept between calls — a COM address does not survive a rebuild.
    /// INVARIANT: only supplied members are written, and each family has its own allowed set, checked by
    /// the caller BEFORE mutation (<c>save_initial_orientation</c> circular, <c>save_initial_objects</c>
    /// mirror, <c>step2_deg</c> circular, <c>step2_mm</c>/<c>angle1/2</c>/<c>direction1/2</c> grid).
    /// LIMIT: <c>Update() = true</c> here means "accepted", not "applied"; the checks are named
    /// <c>set_&lt;member&gt;</c> and application is confirmed by a separate read after the rebuild.</remarks>
    public static (bool Applied, string? Failure, IReadOnlyList<NamedCheck> Writes) TryEdit(
        IFeaturePattern pattern,
        PatternEditDto edit)
    {
        var writes = new List<NamedCheck>();

        static NamedCheck Write(string member, string? was, string now) =>
            new($"set_{member}", true,
                Observed: $"записано {now}; до записи {was ?? "не прочитано"}",
                Expected: $"{now} (применение подтверждается чтением после Update, а не этой записью)");

        try
        {
            switch (pattern)
            {
                case ILinearPattern linear:
                    if (edit.Count1 is int lc1)
                    {
                        writes.Add(Write("count1", SafeDouble(() => linear.Count1)?.ToString(Inv), lc1.ToString(Inv)));
                        linear.Count1 = lc1;
                    }

                    if (edit.Count2 is int lc2)
                    {
                        writes.Add(Write("count2", SafeDouble(() => linear.Count2)?.ToString(Inv), lc2.ToString(Inv)));
                        linear.Count2 = lc2;
                    }

                    if (edit.Step1Mm is double ls1)
                    {
                        writes.Add(Write("step1", SafeDouble(() => linear.Step1)?.ToString(Inv), ls1.ToString(Inv)));
                        linear.Step1 = ls1;
                    }

                    if (edit.Step2Mm is double ls2)
                    {
                        writes.Add(Write("step2", SafeDouble(() => linear.Step2)?.ToString(Inv), ls2.ToString(Inv)));
                        linear.Step2 = ls2;
                    }

                    if (edit.Angle1Deg is double la1)
                    {
                        writes.Add(Write("angle1", SafeDouble(() => linear.Angle1)?.ToString(Inv), la1.ToString(Inv)));
                        linear.Angle1 = la1;
                    }

                    if (edit.Angle2Deg is double la2)
                    {
                        writes.Add(Write("angle2", SafeDouble(() => linear.Angle2)?.ToString(Inv), la2.ToString(Inv)));
                        linear.Angle2 = la2;
                    }

                    if (edit.Direction1 is bool ld1)
                    {
                        writes.Add(Write("direction1", SafeBool(() => linear.Direction1)?.ToString(), ld1.ToString()));
                        linear.Direction1 = ld1;
                    }

                    if (edit.Direction2 is bool ld2)
                    {
                        writes.Add(Write("direction2", SafeBool(() => linear.Direction2)?.ToString(), ld2.ToString()));
                        linear.Direction2 = ld2;
                    }

                    if (edit.BuildingType is string lbt)
                    {
                        writes.Add(Write("building_type",
                            SafeString(() => linear.BuildingType.ToString()), lbt));
                        linear.BuildingType = LinearBuilding(lbt);
                    }

                    break;

                case ICircularPattern circular:
                    if (edit.Count1 is int cc1)
                    {
                        writes.Add(Write("count1", SafeDouble(() => circular.Count1)?.ToString(Inv), cc1.ToString(Inv)));
                        circular.Count1 = cc1;
                    }

                    if (edit.Count2 is int cc2)
                    {
                        writes.Add(Write("count2", SafeDouble(() => circular.Count2)?.ToString(Inv), cc2.ToString(Inv)));
                        circular.Count2 = cc2;
                    }

                    if (edit.Step1Mm is double cs1)
                    {
                        writes.Add(Write("step1", SafeDouble(() => circular.Step1)?.ToString(Inv), cs1.ToString(Inv)));
                        circular.Step1 = cs1;
                    }

                    if (edit.Step2Deg is double cs2)
                    {
                        writes.Add(Write("step2", SafeDouble(() => circular.Step2)?.ToString(Inv), cs2.ToString(Inv)));
                        circular.Step2 = cs2;
                    }

                    if (edit.StepByAxisMm is double csa)
                    {
                        writes.Add(Write("step_by_axis", SafeDouble(() => circular.StepByAxis)?.ToString(Inv), csa.ToString(Inv)));
                        circular.StepByAxis = csa;
                    }

                    if (edit.ReverseDirection is bool crd)
                    {
                        writes.Add(Write("reverse_direction", SafeBool(() => circular.ReverseDirection)?.ToString(), crd.ToString()));
                        circular.ReverseDirection = crd;
                    }

                    if (edit.SaveInitialOrientation is bool cso)
                    {
                        writes.Add(Write("save_initial_orientation",
                            SafeBool(() => circular.SaveInitialOrientation)?.ToString(), cso.ToString()));
                        circular.SaveInitialOrientation = cso;
                    }

                    if (edit.BuildingType is string cbt)
                    {
                        writes.Add(Write("building_type",
                            SafeString(() => circular.BuildingType.ToString()), cbt));
                        circular.BuildingType = CircularBuilding(cbt);
                    }

                    break;

                case IMirrorPattern mirror:
                    if (edit.SaveInitialObjects is bool mso)
                    {
                        writes.Add(Write("save_initial_objects",
                            SafeBool(() => mirror.SaveInitialObjects)?.ToString(), mso.ToString()));
                        mirror.SaveInitialObjects = mso;
                        // READ-BACK RIGHT AFTER THE WRITE: the edit must be visible here, otherwise
                        // "written but not accepted by the model" is indistinguishable from "written
                        // and applied". MEASURED on operation 48: writing false reads back true, which
                        // agrees with the help restricting the property to o3d_mirrorAllOperation.
                        var mirrorReadBack = SafeBool(() => mirror.SaveInitialObjects);
                        var mirrorRejected = mirrorReadBack is bool mrb && mrb != mso;
                        writes.Add(new NamedCheck(
                            "save_initial_objects_read_back",
                            Passed: !mirrorRejected,
                            Observed: $"прочитано сразу после записи {mirrorReadBack}",
                            Expected: mirrorRejected
                                ? $"{mso} — НЕ принято моделью: справка " +
                                  "imirrorpattern_saveinitialobjects.html ограничивает свойство " +
                                  "только o3d_mirrorAllOperation"
                                : $"{mso}"));
                    }

                    break;

                default:
                    return (false,
                        $"Объект массива не отвечает ни на один из интерфейсов ILinearPattern / " +
                        $"ICircularPattern / IMirrorPattern ({pattern.GetType().Name}): править нечем.",
                        writes);
            }

            if (writes.Count == 0)
            {
                return (false,
                    "Ни один параметр массива не задан: правка не выполнялась, признак не изменён.",
                    writes);
            }

            return pattern.Update()
                ? (true, null, writes)
                : (false,
                    $"{pattern.GetType().Name}.Update() вернул false — параметры записаны, но признак " +
                    "не перестроен",
                    writes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex), writes);
        }
    }

    /// <summary>Invariant culture for numbers in check text: a comma instead of a dot would read as a
    /// different number.</summary>
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Read a pattern's parameters by collection index. The kind is determined by the QI
    /// answer, not by the recorded type: "what the object answers" is a fact, "what it was created as"
    /// is the caller's memory.</summary>
    public static PatternReadout? Read(IModelContainer container, int index)
    {
        try
        {
            var patterns = container.FeaturePatterns;
            if (patterns is null || index < 0 || index >= patterns.Count)
            {
                return null;
            }

            if (patterns.FeaturePattern[index] is not { } pattern)
            {
                return null;
            }

            return ReadPattern(pattern);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Read a specific pattern feature.</summary>
    public static PatternReadout ReadPattern(IFeaturePattern pattern)
    {
        var (count1, count2) = ExemplarCounts(pattern);

        if (pattern is ILinearPattern linear)
        {
            return new PatternReadout(
                Family: "linear",
                TypeName: SafeString(() => linear.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: SafeDouble(() => linear.Step1),
                Step2: SafeDouble(() => linear.Step2),
                Angle1Deg: SafeDouble(() => linear.Angle1),
                Angle2Deg: SafeDouble(() => linear.Angle2),
                Direction1: SafeBool(() => linear.Direction1),
                Direction2: SafeBool(() => linear.Direction2),
                Boundary1: SafeBool(() => linear.BoundaryInstancesStepFactor1),
                Boundary2: SafeBool(() => linear.BoundaryInstancesStepFactor2),
                BuildingType: SafeString(() => linear.BuildingType.ToString()),
                StepByAxis: null,
                ReverseDirection: null,
                SaveInitialOrientation: null,
                GeometryPattern: SafeBool(() => linear.GeometryPattern),
                AxisPresent: SafeBool(() => linear.Axis1 is not null),
                Axis2Present: SafeBool(() => linear.Axis2 is not null),
                PlanePresent: null,
                SaveInitialObjects: null,
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        if (pattern is ICircularPattern circular)
        {
            return new PatternReadout(
                Family: "circular",
                TypeName: SafeString(() => circular.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: SafeDouble(() => circular.Step1),
                Step2: SafeDouble(() => circular.Step2),
                Angle1Deg: null,
                Angle2Deg: null,
                Direction1: null,
                Direction2: null,
                Boundary1: SafeBool(() => circular.BoundaryInstancesStepFactor1),
                Boundary2: SafeBool(() => circular.BoundaryInstancesStepFactor2),
                BuildingType: SafeString(() => circular.BuildingType.ToString()),
                StepByAxis: SafeDouble(() => circular.StepByAxis),
                ReverseDirection: SafeBool(() => circular.ReverseDirection),
                SaveInitialOrientation: SafeBool(() => circular.SaveInitialOrientation),
                GeometryPattern: SafeBool(() => circular.GeometryPattern),
                AxisPresent: SafeBool(() => circular.Axis is not null),
                Axis2Present: null,
                PlanePresent: null,
                SaveInitialObjects: null,
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        if (pattern is IMirrorPattern mirror)
        {
            return new PatternReadout(
                Family: "mirror",
                TypeName: SafeString(() => mirror.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: null,
                Step2: null,
                Angle1Deg: null,
                Angle2Deg: null,
                Direction1: null,
                Direction2: null,
                Boundary1: null,
                Boundary2: null,
                BuildingType: null,
                StepByAxis: null,
                ReverseDirection: null,
                SaveInitialOrientation: null,
                GeometryPattern: SafeBool(() => mirror.GeometryPattern),
                AxisPresent: null,
                Axis2Present: null,
                PlanePresent: SafeBool(() => mirror.Plane is not null),
                SaveInitialObjects: SafeBool(() => mirror.SaveInitialObjects),
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        return new PatternReadout(
            Family: "unknown",
            TypeName: SafeString(() => pattern.ModelObjectType.ToString()),
            Count1: count1,
            Count2: count2,
            Step1: null,
            Step2: null,
            Angle1Deg: null,
            Angle2Deg: null,
            Direction1: null,
            Direction2: null,
            Boundary1: null,
            Boundary2: null,
            BuildingType: null,
            StepByAxis: null,
            ReverseDirection: null,
            SaveInitialOrientation: null,
            GeometryPattern: SafeBool(() => pattern.GeometryPattern),
            AxisPresent: null,
            Axis2Present: null,
            PlanePresent: null,
            SaveInitialObjects: null,
            InitialObjectCount: InitialObjectCount(pattern),
            DeletedInstanceCount: DeletedInstanceCount(pattern),
            OwnerName: SafeString(() => pattern.Owner?.Name),
            UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
    }

    /// <summary>The instance counts by two indices. <c>GetExemplarsCounts(out, out)</c> returns
    /// <c>false</c> when the feature is not yet built, so <c>false</c> means "not read", not "zero
    /// instances": the caller must tell them apart, and here they are told apart.</summary>
    public static (int? Count1, int? Count2) ExemplarCounts(IFeaturePattern pattern)
    {
        try
        {
            return pattern.GetExemplarsCounts(out var c1, out var c2) ? (c1, c2) : (null, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null);
        }
    }

    /// <summary>An instance by indices. The grid indexing is TWO-DIMENSIONAL
    /// (<c>Exemplar(Index1, Index2)</c>), so the caller iterates both coordinates, not one.</summary>
    public static IModelObject? Exemplar(IFeaturePattern pattern, int index1, int index2)
    {
        try
        {
            return pattern.Exemplar[index1, index2];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int? InitialObjectCount(IFeaturePattern pattern)
    {
        try
        {
            return pattern.InitialObjects is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The number of deleted instances. A mirror pattern has no instance gaps (user help
    /// <c>glava_48_obzhie_svedeniy</c>: excluding instances is unavailable for a mirror pattern and a
    /// pattern by sample), so there the field is read but declared inapplicable by the caller, not
    /// passed off as zero gaps.</summary>
    private static int? DeletedInstanceCount(IFeaturePattern pattern)
    {
        try
        {
            return pattern.InstanceDeletedIndexes is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> read)
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

    private static bool? SafeBool(Func<bool> read)
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

    private static int? SafeInt(Func<int?> read)
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

    private static string? SafeString(Func<string?> read)
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

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COMException 0x{com.HResult:X8}: {com.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>Read pattern-feature parameters. A <c>null</c> field means "not read", not "zero": an
/// empty field and zero are different answers, and they must not be mixed here any more than in
/// measure.</summary>
/// <param name="Family">linear | circular | mirror | unknown — by the QI answer, not by memory.</param>
/// <param name="DeletedInstanceCount">Number of <c>InstanceDeletedIndexes</c> entries; inapplicable for mirror.</param>
public sealed record PatternReadout(
    string Family,
    string? TypeName,
    int? Count1,
    int? Count2,
    double? Step1,
    double? Step2,
    double? Angle1Deg,
    double? Angle2Deg,
    bool? Direction1,
    bool? Direction2,
    bool? Boundary1,
    bool? Boundary2,
    string? BuildingType,
    double? StepByAxis,
    bool? ReverseDirection,
    bool? SaveInitialOrientation,
    bool? GeometryPattern,
    bool? AxisPresent,
    bool? Axis2Present,
    bool? PlanePresent,
    bool? SaveInitialObjects,
    int? InitialObjectCount,
    int? DeletedInstanceCount,
    string? OwnerName,
    int? UpdateStamp);
