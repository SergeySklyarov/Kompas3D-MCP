using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Component array along a CURVE (profile <c>assembly-ext-v1</c>, ASM-13).</summary>
/// <remarks>ROUTE, measured: <c>FeaturePatterns.Add(o3d_curvePartArray=40)</c> → <c>QI(IPathPattern)</c> →
/// <c>Curves</c> → <c>AddInitialObjects</c> → <c>Count</c>/<c>ByStep</c>/<c>Step</c> → <c>Update()</c>.
/// MEASURED: the trajectory must be an EDGE of a component — a sketch and an element of
/// <c>EntityCollection(o3d_curveElement=111)</c> are stored as null and build nothing.
/// INVARIANT: copies are NOT placed on the curve — their origins are the source origin plus whole steps
/// along it — so the proof compares OFFSETS, never absolute positions.
/// DOC: <c>copytype.html</c>, <c>ipathpattern_*.html</c> History: docs/decisions/assembly-ext.md#path-pattern</remarks>
public partial class Api5Session
{
    /// <summary>Details key of the path-array refusal snapshot.</summary>
    private const string PathPatternSnapshot = "failure_snapshot";

    /// <summary>Component array along a curve.</summary>
    public PatternResult PatternPath(PatternPathCommand command)
    {
        ValidatePatternPath(command);
        var document = RequireComponentAssembly(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);
        var top = RequireComponentPatternTop(document);

        var before = ReadComponents(top);
        var (sources, sourceOrigins, sourceMatrices) = ResolveComponentSources(document, top, command.SourceRefs);

        var routeNotes = new List<string>();
        var curve = ResolvePathCurve(bridge, document, command.CurveRefs[0], out var span, routeNotes);

        ClearKompasResult(document);
        var (pattern, failure) = Api7Pattern.TryCreatePath(
            container,
            sources,
            curve,
            command.Count,
            command.ByStep,
            command.StepMm ?? 0d,
            command.ReverseDirection,
            command.EffectiveSaveInitialOrientation);

        var total = sources.Length * command.Count;
        if (pattern is null || failure is not null)
        {
            var after = ReadComponents(top);
            var details = new Dictionary<string, object?>
            {
                ["code"] = "component_path_pattern_not_built",
                ["api7_failure"] = failure,
                ["components_before"] = before.Count,
                ["components_after"] = after.Count,
                ["curve_count"] = pattern is null ? null : Api7Pattern.CurveCount(pattern),
                ["initial_objects_requested"] = command.SourceRefs.Count,
                ["curve_requested"] = command.CurveRefs[0],
                ["count"] = command.Count,
                ["by_step"] = command.ByStep,
                ["step_mm"] = command.StepMm,
                ["instance_origins_mm"] = after.Origins.Select(Point3).ToArray(),
            };
            AddKompasFailure(details, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Массив компонентов по кривой не построен: "
                + (failure ?? "фабрика IModelContainer.FeaturePatterns не отдала объект")
                + ". Признак мог остаться в дереве непостроенным; состав сборки и снимок отказа — в details."
                + PathPatternSnapshot + ".",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { [PathPatternSnapshot] = details });
        }

        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "pattern.components.path");

        var readout = Api7Pattern.ReadPattern(pattern);
        var exemplars = Api7Pattern.ExemplarCounts(pattern);
        var afterView = ReadComponents(top);

        var checks = new List<NamedCheck>
        {
            new("curve_bound", readout.CurveCount is > 0,
                Observed: $"кривых в траектории {Num(readout.CurveCount)}",
                Expected: $"траектория привязана и читается (запрошена {command.CurveRefs[0]})"),
            new("copy_kind_read_back", readout.CopyKind == "components",
                Observed: readout.CopyKind ?? "не прочитано",
                Expected: "components (o3d_curvePartArray=40 по copytype.html)"),
            new("initial_objects_bound", readout.InitialObjectCount is > 0,
                Observed: $"исходных объектов {Num(readout.InitialObjectCount)}",
                Expected: $"исходные компоненты привязаны (запрошено {command.SourceRefs.Count})"),
            new("count_read_back", readout.Count1 == command.Count,
                Observed: $"{Num(readout.Count1)}",
                Expected: $"{command.Count} (IPathPattern.Count, точное совпадение)"),
            new("by_step_read_back", readout.ByStep == command.ByStep,
                Observed: $"{readout.ByStep?.ToString() ?? "не прочитано"}",
                Expected: $"{command.ByStep} (IPathPattern.ByStep)"),
            new("reverse_direction_read_back", readout.ReverseDirection == command.ReverseDirection,
                Observed: $"{readout.ReverseDirection?.ToString() ?? "не прочитано"}",
                Expected: $"{command.ReverseDirection}"),
            new("exemplar_counts_read", exemplars.Item1 is not null && exemplars.Item2 is not null,
                Observed: $"экземпляров {Num(exemplars.Item1)}×{Num(exemplars.Item2)}",
                Expected: "GetExemplarsCounts отдал число экземпляров"),
            new("exemplar_counts", exemplars.Item1 == command.Count && exemplars.Item2 == 1,
                Observed: $"{Num(exemplars.Item1)}×{Num(exemplars.Item2)}",
                Expected: $"{command.Count}×1 (по ifeaturepattern_getexemplarscounts.html у массива по "
                    + "кривой второе направление равно 1)"),
        };

        var unverified = new List<string>();
        if (before.Count is int countBefore && afterView.Count is int countAfter)
        {
            var grown = countAfter - countBefore;
            checks.Add(new NamedCheck(
                "component_count_growth",
                grown == total - sources.Length,
                Observed: $"компонентов {countBefore}→{countAfter} (прирост {grown}), читано из IPart7.Parts",
                Expected: $"прирост ровно {total - sources.Length} = источник×(count-1)"));
        }
        else
        {
            unverified.Add("component_count_unread — число компонентов сборки не прочитано "
                + "(IPart7.Parts не ответил), поэтому рост состава не сверялся");
        }

        if (afterView.Unread is { } unread)
        {
            unverified.Add("component_origins_partly_unread — " + unread + ": сверка идёт по прочитанным");
        }

        var offsets = PathExpectation(command, span, sourceOrigins, sourceMatrices, out var offsetGap);
        IReadOnlyList<double[]> instanceOrigins = [];
        if (offsets is null)
        {
            unverified.Add("instance_offsets_not_compared — " + offsetGap);
        }
        else
        {
            var (matched, ordered, detail) = MatchOffsets(afterView.Origins, before.Origins, sourceOrigins, offsets);
            instanceOrigins = ordered;
            checks.Add(new NamedCheck(
                "instance_offsets",
                matched,
                Observed: detail,
                Expected: "смещения экземпляров от исходного компонента равны k·шаг вдоль траектории в допуске "
                    + ComponentPatternProof.OriginToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " мм"));
            if (matched)
            {
                unverified.Add("curve_direction_sign_not_compared — набор начал сверяется с ОБОИМИ "
                    + "направлениями траектории: знак задаёт ориентация ребра в модели, и reverse_direction "
                    + "подтверждается чтением свойства, а не положением");
            }
        }

        unverified.Add("document_volume_not_compared — у массива КОМПОНЕНТОВ объём документа не является "
            + "проверкой: копии повторяют один и тот же объём, и он не отличает верный массив от смещённого");

        var patternEntity = FindUniqueTreeEntity(document, SafeModelName(pattern));
        var reference = References.Register("feature", document.Id, document.Revision,
            patternEntity ?? (object?)pattern);
        RequireNoApi7FeatureError(document, pattern, reference.Id, "Массив компонентов по кривой");

        return new PatternResult(
            FeatureRef: ToDto(reference, "path component pattern"),
            Family: "path",
            Readout: readout,
            BodyCount: CountBodies(document),
            VolumeMm3: ReadVolume(document),
            BoundsMm: SafeBounds(document.PartNow()),
            HoleAxes: Array.Empty<PatternHoleDto>(),
            Checks: checks,
            RouteNotes: routeNotes,
            UnverifiedAspects: unverified)
        {
            ComponentsBefore = before.Count,
            ComponentsAfter = afterView.Count,
            InstanceOriginsMm = instanceOrigins,
            SaveInitialOrientation = readout.SaveInitialOrientation,
            SaveInitialOrientationDefaulted = command.SaveInitialOrientation is null,
            Warnings = null,
        };
    }

    /// <summary>The requested curve as an API7 object, plus the straight span its vertices describe.</summary>
    /// <remarks>INVARIANT: only an <c>edge:</c> reference is accepted — MEASURED that a sketch is written and
    /// dropped by the model, and naming the wrong kind here is INVALID_ARGUMENT, not a kernel refusal.
    /// DOC: <c>kspart_bodycollection.html</c>, <c>ksbody_facecollection.html</c>, <c>ksface_edgecollection.html</c>.
    /// </remarks>
    private object ResolvePathCurve(
        Api7Bridge bridge, DocumentEntry document, string reference, out PathSpan? span, List<string> notes)
    {
        span = null;
        var stored = References.Require(reference, document.Id, document.Revision);
        if (!string.Equals(stored.Kind, "edge", StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Траектория массива — РЕБРО, а «{reference}» адресует {stored.Kind}. Компонентное ребро "
                + "читается kompas_read_topology с component_ref; эскиз траекторией не является (измерено: "
                + "ядро запись принимает, но траектория не читается и массив не строится).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["reference"] = reference,
                    ["kind"] = stored.Kind,
                    ["expected_kind"] = "edge",
                });
        }

        if (stored.Payload is not ksEdgeDefinition edge)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{reference}» не несёт ребро: траектория не читается, признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        span = EdgeSpan(edge);
        if (span is null)
        {
            notes.Add("curve_span_not_read — вершины ребра не прочитаны, поэтому длина траектории не известна: "
                + "режим «на всю длину» и сверка смещений по ней не выполняются");
        }

        if (bridge.TransferTo7(edge) is not { } transferred)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Ребро «{reference}» не перенесено в API7 как IModelObject ("
                + (bridge.BridgeFailure ?? "TransferInterface вернул null") + "): траекторию подать нечем, "
                + "признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        return transferred;
    }

    /// <summary>A straight span of the trajectory, read from an edge's two vertices (component frame).</summary>
    private sealed record PathSpan(double[] Start, double[] End, double LengthMm, double[] Direction);

    /// <summary>The edge as a straight span; null when a vertex does not read or the span has no length.</summary>
    /// <remarks>MEASURED: a trajectory is stored only as an edge, and the fixture keeps the pattern's copies at
    /// the source origin plus whole steps along that edge — so its two vertices are what the offsets are
    /// compared with. A curved edge gives a span too, and the offsets are then checked along the CHORD; the
    /// answer names that the curve itself was not sampled.
    /// </remarks>
    private static PathSpan? EdgeSpan(ksEdgeDefinition edge)
    {
        var start = EdgeVertex(edge, true);
        var end = EdgeVertex(edge, false);
        if (start is null || end is null)
        {
            return null;
        }

        var delta = new[] { end[0] - start[0], end[1] - start[1], end[2] - start[2] };
        var length = Math.Sqrt(delta[0] * delta[0] + delta[1] * delta[1] + delta[2] * delta[2]);
        if (ComponentPatternProof.Unit(delta) is not { } direction || length <= 1e-9)
        {
            return null;
        }

        return new PathSpan(start, end, length, direction);
    }

    private static double[]? EdgeVertex(ksEdgeDefinition edge, bool start)
    {
        try
        {
            return edge.GetVertex(start) is ksVertexDefinition vertex
                && vertex.GetPoint(out var x, out var y, out var z)
                    ? [x, y, z]
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Expected offset vectors of the instances from ONE source, or null with the named reason.</summary>
    /// <remarks>LIMIT: derived only for the measured modes — <c>save_initial_orientation=true</c> (copies keep
    /// the source orientation and are translated along the trajectory) — because with <c>false</c> the copies
    /// are turned to the trajectory and the origins follow a rule no run measured.
    /// </remarks>
    private static IReadOnlyList<double[]>? PathExpectation(
        PatternPathCommand command, PathSpan? span, IReadOnlyList<double[]> sourceOrigins,
        IReadOnlyList<double[]?> sourceMatrices, out string gap)
    {
        gap = "";
        if (command.SaveInitialOrientation is false)
        {
            gap = "save_initial_orientation=false: экземпляры доворачиваются к траектории, и правило их начал "
                + "не измерено";
            return null;
        }

        if (span is null)
        {
            gap = "длина траектории не прочитана (вершины ребра): шаг вдоль неё не выводится";
            return null;
        }

        if (sourceOrigins.Count == 0 || sourceMatrices.Count != sourceOrigins.Count)
        {
            gap = "исходные компоненты или их размещения не прочитаны: ожидаемые начала не выводятся";
            return null;
        }

        var step = command.ByStep ? command.StepMm ?? 0d : span.LengthMm / (command.Count - 1);
        if (!(step > 0d))
        {
            gap = $"шаг вдоль траектории {step:0.###} мм не положителен";
            return null;
        }

        // The edge's vertices are in the COMPONENT's frame, so the direction is rotated by the placement of the
        // first source — the component whose edge the client names in practice.
        var local = span.Direction;
        var rotated = Rotate(sourceMatrices[0], local);
        if (rotated is null)
        {
            gap = "размещение исходного компонента не прочитано: направление траектории не переводится в "
                + "координаты сборки";
            return null;
        }

        var plan = ComponentPatternProof.Grid([0d, 0d, 0d], rotated, step, command.Count, null, 0d, 1);
        return plan.Expected;
    }

    /// <summary>Compare the read origins with the expectation, once for each direction of the trajectory.</summary>
    /// <remarks>INVARIANT: a source occupies the pattern's first instance, so its origin is not counted twice.
    /// MEASURED: the kernel takes the trajectory's direction from the edge's own orientation, which the product
    /// does not read, so BOTH signs are accepted and the matched one is reported.
    /// </remarks>
    private static (bool Matched, IReadOnlyList<double[]> Origins, string Detail) MatchOffsets(
        IReadOnlyList<double[]> measured, IReadOnlyList<double[]> beforeOrigins,
        IReadOnlyList<double[]> sourceOrigins, IReadOnlyList<double[]> offsets)
    {
        var forward = Expand(beforeOrigins, sourceOrigins, offsets, 1d);
        var backward = Expand(beforeOrigins, sourceOrigins, offsets, -1d);
        var (forwardMatched, forwardDetail) = ComponentPatternProof.Match(measured, forward);
        var (backwardMatched, backwardDetail) = ComponentPatternProof.Match(measured, backward);
        var matched = forwardMatched || backwardMatched;
        var ordered = ComponentPatternProof.OrderedMatches(measured,
            forwardMatched ? forward : backwardMatched ? backward : forward);
        var detail = (forwardMatched ? "прямое направление: " : backwardMatched ? "обратное направление: " : "")
            + (matched ? forwardMatched ? forwardDetail : backwardDetail
                : $"прямое — {forwardDetail}; обратное — {backwardDetail}");
        return (matched, ordered, detail);
    }

    /// <summary>The whole assembly as it must look after the array: every old origin except the sources, plus
    /// each source's own instances along the trajectory.</summary>
    private static IReadOnlyList<double[]> Expand(
        IReadOnlyList<double[]> beforeOrigins, IReadOnlyList<double[]> sourceOrigins,
        IReadOnlyList<double[]> offsets, double sign)
    {
        var remaining = new List<double[]>(beforeOrigins);
        foreach (var origin in sourceOrigins)
        {
            RemoveOnce(remaining, origin);
        }

        foreach (var source in sourceOrigins)
        {
            foreach (var offset in offsets)
            {
                remaining.Add(
                [
                    source[0] + sign * offset[0],
                    source[1] + sign * offset[1],
                    source[2] + sign * offset[2],
                ]);
            }
        }

        return remaining;
    }

    /// <summary>The direction as the placement matrix's rotation sends it; null when the matrix is absent.</summary>
    /// <remarks>INVARIANT: only the ROTATION part takes part — a translation cannot turn a direction, and using
    /// the whole matrix would move the vector by the placement's origin.
    /// </remarks>
    private static double[]? Rotate(double[]? matrix, double[] direction)
    {
        if (matrix is not { Length: >= 12 })
        {
            return null;
        }

        var rotated = new[]
        {
            matrix[0] * direction[0] + matrix[1] * direction[1] + matrix[2] * direction[2],
            matrix[4] * direction[0] + matrix[5] * direction[1] + matrix[6] * direction[2],
            matrix[8] * direction[0] + matrix[9] * direction[1] + matrix[10] * direction[2],
        };
        return ComponentPatternProof.Unit(rotated);
    }

    /// <summary>Refusals of the path mode, raised before anything is written to the model.</summary>
    /// <remarks>DOC: <c>ipathpattern_bystep.html</c> (a step means nothing without step mode),
    /// <c>ipathpattern_count.html</c>. LIMIT: exactly one curve — a chained trajectory was never measured, so a
    /// second element is refused by name instead of being written as an unmeasured mode.
    /// </remarks>
    internal static void ValidatePatternPath(PatternPathCommand command)
    {
        ValidateComponentSources(command.SourceRefs);
        if (command.CurveRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Массив по кривой без траектории не собирается: список curve_refs пуст. Признак не создавался.",
                RetryPolicy.Never);
        }

        if (command.CurveRefs.Count > 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Траектория задана набором из {command.CurveRefs.Count} кривых: сшивание нескольких кривых в "
                + "одну траекторию не измерено, а выдать его за поддержанное нельзя. Измерен и поддержан один "
                + "curve_ref — ребро компонента. Признак не создавался.",
                RetryPolicy.Never);
        }

        if (command.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Массив по кривой требует count не меньше 2, получено {command.Count}: при одном экземпляре "
                + "копировать нечего. Признак не создавался.",
                RetryPolicy.Never);
        }

        if (command.ByStep && !(command.StepMm is > 0d))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "by_step=true требует положительного step_mm: страница ipathpattern_bystep.html объявляет "
                + "ByStep как способ построения ПО ШАГУ, и нулевой шаг кладёт все экземпляры в одну точку. "
                + "Признак не создавался.",
                RetryPolicy.Never);
        }
    }
}
