using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Grid and circular patterns OF ASSEMBLY COMPONENTS (profile <c>assembly-ext-v1</c>, ASM-09/10).</summary>
/// <remarks>ROUTE — measured by a probe: <c>IModelContainer.FeaturePatterns.Add(o3d_meshPartArray=39 |
/// o3d_circPartArray=38)</c> → <c>QI(ILinearPattern | ICircularPattern)</c> → <c>IsSuitableObject</c> /
/// <c>AddInitialObjects</c> (components) → parameters → <c>Update()</c> → rebuild.
/// DOC: <c>copytype.html</c> (type → interface), <c>ifeaturepatterns_add.html</c>,
/// <c>ifeaturepattern_issuitableobject.html</c>, <c>ifeaturepattern_addinitialobjects.html</c>,
/// <c>ifeaturepattern_getexemplarscounts.html</c>, <c>ilinearpattern_props.html</c>,
/// <c>icircularpattern_props.html</c>.
/// INVARIANT: the proof is a COMPARISON with the expected instance set (component count grew by
/// <c>count1*count2-1</c>, origins match the grid nodes, angular steps match the requested step), never
/// <c>Update()=true</c>. Instances are read from <c>IPart7.Parts</c> + <c>GetSummMatrix</c>: MEASURED that
/// <c>IFeaturePattern.Exemplar[i,j]</c> answers null on a built component array.
/// INVARIANT: a refusal leaves the feature in the tree, so the object and the reference are NAMED in the
/// refusal — «Отказ ядра КОМПАС» (AGENTS.md) is only honest when what changed is named.
/// History: docs/decisions/assembly-ext.md#component-arrays</remarks>
public partial class Api5Session
{
    /// <summary>Details key of the component-pattern refusal snapshot.</summary>
    private const string ComponentPatternSnapshot = "failure_snapshot";

    /// <summary>What the assembly held at one moment: the number of TOP-LEVEL components and their origins.</summary>
    /// <param name="Count">null — the collection did not answer (not "zero components").</param>
    /// <param name="Origins">Origins READ from <c>GetSummMatrix</c>; a component whose matrix was not read
    /// adds nothing here and is counted in <paramref name="Unread"/>.</param>
    private sealed record ComponentView(int? Count, IReadOnlyList<double[]> Origins, string? Unread);

    /// <summary>What the created pattern must produce, and why a comparison is impossible.</summary>
    private sealed record ComponentExpectation(
        ComponentPatternProof.Plan Positions,
        double? AngleStepDeg,
        double[]? AxisPoint,
        double[]? AxisDirection,
        IReadOnlyList<string> Gaps);

    /// <summary>Grid pattern of components.</summary>
    private PatternResult PatternComponentGrid(PatternGridCommand command)
    {
        ValidateComponentGrid(command);
        var document = RequireComponentAssembly(command.DocumentId);
        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);
        var top = RequireComponentPatternTop(document);

        var before = ReadComponents(top);
        var (sources, sourceOrigins, _) = ResolveComponentSources(document, top, command.SourceRefs);

        var axisNotes = new List<string>();
        var axis1 = Api7Rotated.TryBuildAxisBy2Points(
            bridge, part, command.Axis1Point1Mm.ToArray(), command.Axis1Point2Mm.ToArray());
        if (axis1 is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Первая ось массива не построена в собственной детали — признак не создавался. " +
                "Массив без оси не собирается вовсе, поэтому ось здесь не улучшение, а условие постановки.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        axisNotes.AddRange(axis1.Notes);

        IModelObject? axis2 = null;
        double[]? direction2 = null;
        if (command.Axis2Point1Mm is not null && command.Axis2Point2Mm is not null && command.Count2 is > 1)
        {
            var second = Api7Rotated.TryBuildAxisBy2Points(
                bridge, part, command.Axis2Point1Mm.ToArray(), command.Axis2Point2Mm.ToArray());
            if (second is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Вторая ось сетки объявлена, но не построена — признак не создавался: сетка по второму " +
                    "направлению без второй оси не собирается.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true);
            }

            axis2 = second.Axis;
            direction2 = ComponentPatternProof.Unit(Difference(
                command.Axis2Point2Mm.ToArray(), command.Axis2Point1Mm.ToArray()));
            axisNotes.AddRange(second.Notes);
        }

        var expectation = GridExpectation(command, sourceOrigins, direction2);
        var count2 = command.Count2 ?? 1;

        ClearKompasResult(document);
        var (pattern, failure) = Api7Pattern.TryCreateLinear(
            container,
            command.CopyKind,
            sources,
            axis1.Axis,
            command.Step1Mm,
            command.Count1,
            command.Angle1Deg,
            command.Direction1,
            command.BoundaryInstancesStepFactor1,
            axis2,
            command.Step2Mm ?? 0d,
            count2,
            command.Angle2Deg,
            command.Direction2,
            command.BoundaryInstancesStepFactor2,
            command.BuildingType,
            command.GeometryPattern);

        return FinishComponentPattern(new ComponentPatternOutcome(
            document,
            container,
            top,
            pattern,
            failure,
            "grid",
            before,
            sourceOrigins,
            command.SourceRefs,
            expectation,
            command.Count1,
            command.Count2 ?? 1,
            axisNotes));
    }

    /// <summary>Circular-grid pattern of components.</summary>
    private PatternResult PatternComponentCircular(PatternCircularCommand command)
    {
        ValidateComponentCircular(command);
        var document = RequireComponentAssembly(command.DocumentId);
        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);
        var top = RequireComponentPatternTop(document);

        var before = ReadComponents(top);
        var (sources, sourceOrigins, _) = ResolveComponentSources(document, top, command.SourceRefs);

        var axis = Api7Rotated.TryBuildAxisBy2Points(
            bridge, part, command.AxisPoint1Mm.ToArray(), command.AxisPoint2Mm.ToArray());
        if (axis is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ось кругового массива не построена в собственной детали — признак не создавался.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var axisPoint = command.AxisPoint1Mm.ToArray();
        var axisDirection = ComponentPatternProof.Unit(
            Difference(command.AxisPoint2Mm.ToArray(), axisPoint));
        var expectation = CircularExpectation(command, sourceOrigins, axisPoint, axisDirection);

        ClearKompasResult(document);
        var (pattern, failure) = Api7Pattern.TryCreateCircular(
            container,
            command.CopyKind,
            sources,
            axis.Axis,
            command.Count1,
            command.Step1Mm,
            command.Count2,
            command.Step2Deg,
            command.StepByAxisMm,
            command.BoundaryInstancesStepFactor1,
            command.BoundaryInstancesStepFactor2,
            command.ReverseDirection,
            command.EffectiveSaveInitialOrientation,
            command.BuildingType,
            command.GeometryPattern);

        return FinishComponentPattern(new ComponentPatternOutcome(
            document,
            container,
            top,
            pattern,
            failure,
            "circular",
            before,
            sourceOrigins,
            command.SourceRefs,
            expectation,
            command.Count1,
            command.Count2,
            axis.Notes));
    }

    /// <summary>One component-pattern creation, after the settings are written.</summary>
    private sealed record ComponentPatternOutcome(
        DocumentEntry Document,
        IModelContainer Container,
        IPart7 Top,
        IFeaturePattern? Pattern,
        string? Failure,
        string Family,
        ComponentView Before,
        IReadOnlyList<double[]> SourceOrigins,
        IReadOnlyList<string> SourceRefs,
        ComponentExpectation Expectation,
        int Count1,
        int Count2,
        IReadOnlyList<string> RouteNotes);

    /// <summary>Common tail: refuse honestly, else rebuild, read the model back and COMPARE.</summary>
    private PatternResult FinishComponentPattern(ComponentPatternOutcome outcome)
    {
        var document = outcome.Document;
        var total = outcome.Count1 * outcome.Count2;
        var exemplarsBefore = outcome.Pattern is null ? (null, null) : Api7Pattern.ExemplarCounts(outcome.Pattern);

        if (outcome.Pattern is null || outcome.Failure is not null)
        {
            // THE FEATURE IS NAMED, NOT SWALLOWED: the factory may have made the object and the kernel may
            // have refused the sources, and then the tree holds an unbuilt pattern that the client has to
            // remove by reference. Saying "not created" there would be a lie about the model.
            var after = ReadComponents(outcome.Top);
            var details = new Dictionary<string, object?>
            {
                ["code"] = "component_pattern_not_built",
                ["family"] = outcome.Family,
                ["api7_failure"] = outcome.Failure,
                ["components_before"] = outcome.Before.Count,
                ["components_after"] = after.Count,
                ["exemplar_counts_read"] = exemplarsBefore.Item1 is not null && exemplarsBefore.Item2 is not null,
                ["exemplar_counts"] = new[] { exemplarsBefore.Item1, exemplarsBefore.Item2 },
                ["initial_objects_requested"] = outcome.SourceRefs.Count,
                ["instance_origins_mm"] = after.Origins.Select(Point3).ToArray(),
                ["expected_origins_mm"] = outcome.Expectation.Positions.Expected.Select(Point3).ToArray(),
                ["count1"] = outcome.Count1,
                ["count2"] = outcome.Count2,
            };
            AddKompasFailure(details, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Массив компонентов (семейство {outcome.Family}) не построен: "
                + (outcome.Failure ?? "фабрика IModelContainer.FeaturePatterns не отдала объект")
                + ". Признак мог остаться в дереве непостроенным; состав сборки и снимок отказа — в "
                + "details." + ComponentPatternSnapshot + ".",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { [ComponentPatternSnapshot] = details });
        }

        RebuildApi7AndSettle(outcome.Container, document);
        BumpRevision(document, "pattern.components." + outcome.Family);

        var readout = Api7Pattern.ReadPattern(outcome.Pattern);
        // THE COUNTS ARE READ BACK AFTER THE REBUILD: before it the feature is a representation, and a count
        // read from an unbuilt pattern would answer "not read" on a correct array.
        var exemplars = Api7Pattern.ExemplarCounts(outcome.Pattern);
        var afterView = ReadComponents(outcome.Top);

        // NO "pattern_created" CHECK: a check whose `passed` is the literal true compares nothing, and the
        // guard (tests/Unit CheckTruthGuardTests) refuses it for a reason - the client reads such a row as
        // "the product verified this". What the feature IS is named by the checks that compare.
        var checks = new List<NamedCheck>
        {
            new("copy_kind_read_back", readout.CopyKind == "components",
                Observed: readout.CopyKind ?? "не прочитано",
                Expected: "components (o3d_meshPartArray=39 / o3d_circPartArray=38 по copytype.html)"),
            new("initial_objects_bound", readout.InitialObjectCount is > 0,
                Observed: $"исходных объектов {readout.InitialObjectCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}",
                Expected: $"исходные компоненты привязаны к признаку (запрошено {outcome.SourceRefs.Count})"),
            new("exemplar_counts_read",
                exemplars.Item1 is not null && exemplars.Item2 is not null,
                Observed: $"экземпляров {Num(exemplars.Item1)}×{Num(exemplars.Item2)}",
                Expected: "GetExemplarsCounts отдал число экземпляров"),
            new("exemplar_counts", exemplars.Item1 == outcome.Count1 && exemplars.Item2 == outcome.Count2,
                Observed: $"{Num(exemplars.Item1)}×{Num(exemplars.Item2)}",
                Expected: $"{outcome.Count1}×{outcome.Count2} (точное совпадение, допуск к счёту не применяется)"),
        };

        var unverified = new List<string>();
        if (outcome.Before.Count is int countBefore && afterView.Count is int countAfter)
        {
            var grown = countAfter - countBefore;
            checks.Add(new NamedCheck(
                "component_count_growth",
                grown == total - 1,
                Observed: $"компонентов {countBefore}→{countAfter} (прирост {grown}), читано из IPart7.Parts",
                Expected: $"прирост ровно {total - 1} = count1*count2-1"));
        }
        else
        {
            unverified.Add("component_count_unread — число компонентов сборки не прочитано "
                + "(IPart7.Parts не ответил), поэтому рост состава не сверялся");
        }

        if (afterView.Unread is { } unread)
        {
            unverified.Add("component_origins_partly_unread — " + unread
                + ": сверка начал идёт по прочитанным");
        }

        var expectedPositions = MergeWithSources(outcome.Before.Origins, outcome.SourceOrigins, outcome.Expectation);
        IReadOnlyList<double[]> instanceOrigins = Array.Empty<double[]>();
        if (outcome.Expectation.Positions.Unavailable is { } gap)
        {
            unverified.Add("instance_positions_not_compared — " + gap
                + ": ожидаемый набор начал не выводится из запроса, поэтому сверяются только числа");
        }
        else
        {
            var (matched, detail) = ComponentPatternProof.Match(afterView.Origins, expectedPositions);
            instanceOrigins = ComponentPatternProof.OrderedMatches(
                afterView.Origins, outcome.Expectation.Positions.Expected);
            checks.Add(new NamedCheck(
                "instance_origins",
                matched,
                Observed: detail,
                Expected: $"начала экземпляров совпадают с узлами сетки в допуске "
                    + ComponentPatternProof.OriginToleranceMm.ToString("0.###", CultureInfo.InvariantCulture)
                    + " мм; ожидалось: " + string.Join(", ", expectedPositions.Select(Point3))));
        }

        if (outcome.Expectation.AngleStepDeg is double stepDeg && instanceOrigins.Count >= 2)
        {
            var angles = ComponentPatternProof.Angles(
                instanceOrigins, outcome.Expectation.AxisPoint!, outcome.Expectation.AxisDirection!);
            var (stepsMatched, stepsDetail) = ComponentPatternProof.SuccessiveSteps(angles, stepDeg);
            checks.Add(new NamedCheck(
                "instance_angles",
                stepsMatched,
                Observed: stepsDetail,
                Expected: "угол между соседними экземплярами равен шагу в допуске "
                    + ComponentPatternProof.AngleToleranceDeg.ToString("0.###", CultureInfo.InvariantCulture)
                    + "°"));
        }
        else if (outcome.Expectation.AngleStepDeg is not null)
        {
            unverified.Add("instance_angles_not_checked — начал экземпляров прочитано меньше двух: угол "
                + "между соседними экземплярами сверять не с чем");
        }

        unverified.AddRange(outcome.Expectation.Gaps);
        unverified.Add("document_volume_not_compared — у массива КОМПОНЕНТОВ объём документа не является "
            + "проверкой: копии повторяют один и тот же объём, и он не отличает верный массив от смещённого");

        // INVARIANT: the reference points at the TREE element when the tree carries exactly one with the
        // feature's name; an API7 IFeaturePattern does not resolve back into a feature.
        // History: docs/decisions/adapter-features.md#feature-reference-points-at-the-tree
        var patternEntity = FindUniqueTreeEntity(document, SafeModelName(outcome.Pattern));
        var reference = References.Register("feature", document.Id, document.Revision,
            patternEntity ?? (object?)outcome.Pattern);
        RequireNoApi7FeatureError(document, outcome.Pattern, reference.Id, "Массив компонентов");

        return new PatternResult(
            FeatureRef: ToDto(reference, $"{outcome.Family} component pattern"),
            Family: outcome.Family,
            Readout: readout,
            BodyCount: CountBodies(document),
            VolumeMm3: ReadVolume(document),
            BoundsMm: SafeBounds(document.PartNow()),
            HoleAxes: Array.Empty<PatternHoleDto>(),
            Checks: checks,
            RouteNotes: outcome.RouteNotes,
            UnverifiedAspects: unverified)
        {
            ComponentsBefore = outcome.Before.Count,
            ComponentsAfter = afterView.Count,
            InstanceOriginsMm = instanceOrigins,
            Warnings = null,
        };
    }

    /// <summary>Grid expectation: copies along the two axes from every source.</summary>
    /// <remarks>LIMIT: the set is derived for the plain <c>save_all</c> order with both directions positive,
    /// no boundary factors, no tilt and a rectangular pair of axes — those are the settings whose geometry
    /// follows from the request alone. Anything else is NAMED as a gap, because a guessed set would fail a
    /// correct pattern.</remarks>
    private static ComponentExpectation GridExpectation(
        PatternGridCommand command, IReadOnlyList<double[]> sourceOrigins, double[]? direction2)
    {
        var gaps = new List<string>();
        var direction1 = ComponentPatternProof.Unit(
            Difference(command.Axis1Point2Mm.ToArray(), command.Axis1Point1Mm.ToArray()));
        if (direction1 is null)
        {
            gaps.Add("ось первого направления вырождена: направление не вычисляется");
        }

        if (!string.Equals(command.BuildingType, "save_all", StringComparison.Ordinal))
        {
            gaps.Add($"способ построения {command.BuildingType} меняет СОСТАВ экземпляров, и ожидаемый "
                + "набор узлов из запроса не выводится");
        }

        if (!command.Direction1 || !command.Direction2)
        {
            gaps.Add("direction1/direction2 = false: как ядро раскладывает копии при отрицательном "
                + "направлении, не измерено");
        }

        if (command.BoundaryInstancesStepFactor1 || command.BoundaryInstancesStepFactor2)
        {
            gaps.Add("boundary_instances_step_factor меняет толкование шага на границе, и узлы сетки из "
                + "запроса не выводятся");
        }

        if (command.Angle1Deg is double angle1 && Math.Abs(angle1) > 1e-9)
        {
            gaps.Add($"angle1_deg={angle1:0.###}: наклон первого направления меняет положение копий");
        }

        var count2 = command.Count2 ?? 1;
        if (command.Angle2Deg is double angle2 && Math.Abs(angle2 - 90d) > 1e-9)
        {
            gaps.Add($"angle2_deg={angle2:0.###}: угол между направлениями, отличный от 90, меняет "
                + "положение копий второго направления");
        }

        if (count2 > 1 && direction2 is null)
        {
            gaps.Add("вторая ось не задана: направление второго направления не вычисляется");
        }

        if (count2 > 1 && direction1 is not null && direction2 is not null
            && Math.Abs(Dot(direction1, direction2)) > 1e-6)
        {
            gaps.Add("оси направлений не перпендикулярны: ядро раскладывает копии по УГЛУ между "
                + "направлениями, а не по построенной второй оси, и узлы из запроса не выводятся");
        }

        if (gaps.Count > 0 || direction1 is null)
        {
            return new ComponentExpectation(
                new ComponentPatternProof.Plan(Array.Empty<double[]>(), string.Join("; ", gaps)),
                null, null, null, Array.Empty<string>());
        }

        var expected = new List<double[]>();
        foreach (var origin in sourceOrigins)
        {
            var plan = ComponentPatternProof.Grid(
                origin, direction1, command.Step1Mm, command.Count1, direction2,
                command.Step2Mm ?? 0d, count2);
            expected.AddRange(plan.Expected);
        }

        return new ComponentExpectation(
            new ComponentPatternProof.Plan(expected, null), null, null, null, Array.Empty<string>());
    }

    /// <summary>Circular expectation: instances turned about the axis, plus the angular step check.</summary>
    private static ComponentExpectation CircularExpectation(
        PatternCircularCommand command, IReadOnlyList<double[]> sourceOrigins, double[] axisPoint,
        double[]? axisDirection)
    {
        var gaps = new List<string>();
        if (axisDirection is null)
        {
            gaps.Add("ось массива вырождена: направление не вычисляется");
        }

        if (!string.Equals(command.BuildingType, "save_all", StringComparison.Ordinal))
        {
            gaps.Add($"способ построения {command.BuildingType} меняет СОСТАВ экземпляров кольца, и "
                + "ожидаемый набор из запроса не выводится");
        }

        if (command.ReverseDirection)
        {
            gaps.Add("reverse_direction=true: знак раскладки кольца из запроса не выводится");
        }

        if (Math.Abs(command.StepByAxisMm) > 1e-9)
        {
            gaps.Add($"step_by_axis_mm={command.StepByAxisMm:0.###}: смещение вдоль оси меняет начала "
                + "экземпляров");
        }

        if (gaps.Count > 0 || axisDirection is null)
        {
            return new ComponentExpectation(
                new ComponentPatternProof.Plan(Array.Empty<double[]>(), string.Join("; ", gaps)),
                Math.Abs(command.Step2Deg) > 0d ? command.Step2Deg : null,
                axisPoint,
                axisDirection, Array.Empty<string>());
        }

        var expected = new List<double[]>();
        string? unavailable = null;
        foreach (var origin in sourceOrigins)
        {
            var plan = ComponentPatternProof.Circular(
                origin, axisPoint, axisDirection, command.Count1, command.Count2, command.Step2Deg);
            expected.AddRange(plan.Expected);
            unavailable ??= plan.Unavailable;
        }

        return new ComponentExpectation(
            new ComponentPatternProof.Plan(unavailable is null ? expected : Array.Empty<double[]>(),
                unavailable),
            command.Step2Deg,
            axisPoint,
            axisDirection,
            Array.Empty<string>());
    }

    /// <summary>The expected origins of the WHOLE assembly: everything that was there minus the sources, plus
    /// every instance node.</summary>
    /// <remarks>INVARIANT: a source occupies the pattern's first node, so its origin is replaced by the node
    /// rather than counted twice; comparing against "nodes only" would report every untouched component as an
    /// extra.</remarks>
    private static IReadOnlyList<double[]> MergeWithSources(
        IReadOnlyList<double[]> before, IReadOnlyList<double[]> sourceOrigins,
        ComponentExpectation expectation)
    {
        var remaining = new List<double[]>(before);
        foreach (var sourceOrigin in sourceOrigins)
        {
            RemoveOnce(remaining, sourceOrigin);
        }

        remaining.AddRange(expectation.Positions.Expected);
        return remaining;
    }

    private static void RemoveOnce(List<double[]> list, double[] origin)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (Math.Abs(list[i][0] - origin[0]) <= ComponentPatternProof.OriginToleranceMm
                && Math.Abs(list[i][1] - origin[1]) <= ComponentPatternProof.OriginToleranceMm
                && Math.Abs(list[i][2] - origin[2]) <= ComponentPatternProof.OriginToleranceMm)
            {
                list.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>The assembly's top part as an API7 node — the container of its components.</summary>
    private IPart7 RequireComponentPatternTop(DocumentEntry document)
    {
        var notes = new List<string>();
        if (TopPart7(document, notes) is { } top)
        {
            return top;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            "Верхний компонент сборки не получен как IPart7: " + string.Join("; ", notes)
            + ". Экземпляры массива компонентов читаются из IPart7.Parts, и подмены этому маршруту нет; "
            + "признак не создавался.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { ["bridge_failure"] = BridgeFor(document).BridgeFailure });
    }

    /// <summary>Top-level components and their origins, read through <c>IPart7.Parts</c> + <c>GetSummMatrix</c>.</summary>
    private ComponentView ReadComponents(IPart7 top)
    {
        try
        {
            var parts = top.Parts;
            if (parts is null)
            {
                return new ComponentView(null, Array.Empty<double[]>(), "IPart7.Parts вернул null");
            }

            var count = parts.Count;
            var origins = new List<double[]>(count);
            var unread = 0;
            for (var i = 0; i < count; i++)
            {
                if (parts.get_Part(i) is not IPart7 child)
                {
                    unread++;
                    continue;
                }

                if (SummMatrix7(top, child) is not { Length: >= 15 } matrix)
                {
                    unread++;
                    continue;
                }

                origins.Add(new[] { matrix[12], matrix[13], matrix[14] });
            }

            return new ComponentView(count, origins,
                unread > 0 ? $"у {unread} из {count} компонентов начало не прочитано" : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new ComponentView(null, Array.Empty<double[]>(),
                "IPart7.Parts прервался: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Resolve <c>component:</c> sources into API7 objects, with the origins the comparison needs.</summary>
    /// <remarks>INVARIANT: a source must be a TOP-LEVEL component of THIS assembly and its placement must be
    /// readable before the mutation — a pattern whose sources cannot be located afterwards cannot be
    /// compared, and refusing after the feature is built would leave it in the tree.
    /// <paramref name="Placements"/> carries the 4×4 matrices for the callers that need a FRAME (the path array
    /// rotates the curve's direction by it); the grid and circular proofs need the origins only.</remarks>
    private (object[] Objects, List<double[]> Origins, List<double[]?> Placements) ResolveComponentSources(
        DocumentEntry document, IPart7 top, IReadOnlyList<string> sourceRefs)
    {
        if (sourceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Массив компонентов без исходных объектов не собирается: список source_refs пуст.",
                RetryPolicy.Never);
        }

        var objects = new List<object>(sourceRefs.Count);
        var origins = new List<double[]>(sourceRefs.Count);
        var placements = new List<double[]?>(sourceRefs.Count);
        foreach (var reference in sourceRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is not ComponentPayload payload)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Источник массива компонентов — ссылка КОМПОНЕНТА, а «{reference}» адресует "
                    + $"{stored.Kind}. Для копирования операций — feature:, для тел — body:, для "
                    + "компонентов — component:.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["reference"] = reference,
                        ["kind"] = stored.Kind,
                    });
            }

            if (payload.Ordinal == NestedComponentOrdinal)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Компонент «{reference}» вложен в подсборку: массив берёт исходниками компоненты "
                    + "ВЕРХНЕГО уровня, потому что экземпляры читаются из IPart7.Parts верхней сборки и "
                    + "сравниваются с её узлами. Адресация вложенного — отдельный наряд; признак не создавался.",
                    RetryPolicy.Never);
            }

            if (payload.Part7 is not { } part7 || part7 is not IModelObject model)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Ссылка «{reference}» не несёт объект компонента: массив не создавался.",
                    RetryPolicy.ReacquireContext);
            }

            if (SummMatrix7(top, part7) is not { Length: >= 15 } matrix)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Размещение исходного компонента «{reference}» не прочитано (GetSummMatrix): начала "
                    + "экземпляров были бы не с чем сверять, а сверка здесь и есть доказательство. "
                    + "Признак не создавался.",
                    RetryPolicy.ReacquireContext);
            }

            objects.Add(model);
            origins.Add(new[] { matrix[12], matrix[13], matrix[14] });
            placements.Add(matrix);
        }

        return (objects.ToArray(), origins, placements);
    }

    private static double[] Difference(double[] to, double[] from) =>
    [
        to[0] - from[0],
        to[1] - from[1],
        to[2] - from[2],
    ];

    /// <summary>A point as the answer spells it: invariant culture, so a comma is never read as a separator.</summary>
    private static string Point3(double[] point) =>
        "(" + point[0].ToString("0.###", CultureInfo.InvariantCulture) + ", "
        + point[1].ToString("0.###", CultureInfo.InvariantCulture) + ", "
        + point[2].ToString("0.###", CultureInfo.InvariantCulture) + ")";

    /// <summary>Refusals of the component grid mode, raised before anything is written to the model.</summary>
    /// <remarks>INVALID_ARGUMENT for a request that cannot be a component pattern at all, so the client sees
    /// its own error and not a kernel refusal. DOC: <c>copytype.html</c> — the component types are separate
    /// orders; a request for them on a part document or with fewer than two instances describes nothing.</remarks>
    internal static void ValidateComponentGrid(PatternGridCommand command)
    {
        ValidateComponentSources(command.SourceRefs);
        if (command.Count1 < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Массив компонентов по сетке требует count1 не меньше 2, получено {command.Count1}: при "
                + "одном экземпляре копировать нечего, и признак не описывает массив. Признак не создавался.",
                RetryPolicy.Never);
        }

        if (command.Count2 is int c2 && c2 > 1 && !(command.Step2Mm is > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Число экземпляров по второму направлению больше одного, а шаг второго направления не задан "
                + "или не положителен.",
                RetryPolicy.Never);
        }

        RequirePositiveFirstStep(command.Step1Mm);
        RequireDistinctPoints(command.Axis1Point1Mm, command.Axis1Point2Mm, "первой оси");
        if (command.Axis2Point1Mm is not null && command.Axis2Point2Mm is not null)
        {
            RequireDistinctPoints(command.Axis2Point1Mm, command.Axis2Point2Mm, "второй оси");
        }
    }

    /// <summary>Refusals of the component circular mode, raised before anything is written to the model.</summary>
    internal static void ValidateComponentCircular(PatternCircularCommand command)
    {
        ValidateComponentSources(command.SourceRefs);
        if (command.Count2 < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Круговой массив компонентов требует count2 не меньше 2, получено {command.Count2}: кольцо "
                + "из одного экземпляра массивом не является. Признак не создавался.",
                RetryPolicy.Never);
        }

        if (command.Count1 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Число экземпляров в радиальном направлении должно быть не меньше 1, получено "
                + $"{command.Count1}.",
                RetryPolicy.Never);
        }

        if (command.Count1 > 1 && !(command.Step1Mm > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг в радиальном направлении должен быть положительным, когда экземпляров больше одного.",
                RetryPolicy.Never);
        }

        if (!(command.Step2Deg > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угловой шаг кольца должен быть положительным: он задаётся в ГРАДУСАХ (страница "
                + "icircularpattern_props.html: «Step2 — Угловой шаг (градусы)»).",
                RetryPolicy.Never);
        }

        RequireDistinctPoints(command.AxisPoint1Mm, command.AxisPoint2Mm, "оси");
    }

    private static void ValidateComponentSources(IReadOnlyList<string> sourceRefs)
    {
        if (sourceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Массив компонентов требует непустого списка source_refs: массив без исходных объектов не "
                + "собирается.",
                RetryPolicy.Never);
        }
    }

    private static void RequirePositiveFirstStep(double step1Mm)
    {
        if (!(step1Mm > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг первого направления должен быть положительным: нулевой шаг кладёт все экземпляры в "
                + "одну точку.",
                RetryPolicy.Never);
        }
    }

    /// <summary>The component mode needs an ASSEMBLY: a part has no components to copy.</summary>
    private DocumentEntry RequireComponentAssembly(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Документ «{documentId}» имеет тип {document.Kind}: массив КОМПОНЕНТОВ строится только в "
                + "сборке. Для детали копируются операции (copy_kind=operations) или тела "
                + "(copy_kind=bodies). Признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                    ["copy_kind"] = "components",
                });
        }

        return document;
    }
}
