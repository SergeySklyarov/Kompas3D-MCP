using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe FR — does a fillet (and a chamfer) created by the API5 edge route SURVIVE a rebuild,
/// or does the control rebuild take it away or move it to another edge?</summary>
/// <remarks>
/// <b>The question.</b> The shipping adapter settles every body mutation with a control rebuild
/// (<c>SettleAfterMutation</c>). A fillet created on a Г-plate was measured to be applied by
/// <c>Create()</c> (V = 67980.6858347058) and then to be REMOVED (V = 68000) or MOVED to the reflex
/// corner (V = 68019.3141652942) by that very control rebuild. The adapter cannot tell those three
/// outcomes apart by volume alone on the four-corner reference plate, because all four of its corners
/// are convex and equivalent.
/// <b>Why the probe goes to the kernel directly.</b> The adapter always settles, so the product route
/// cannot answer "did <c>Create()</c> alone apply it, and what does the FIRST rebuild do" — the two
/// questions the diagnosis needs. A direct COM probe keeps the two moments apart.
/// <b>The Г-contour, not the reference plate.</b> 100×80 minus 40×30 has ONE REFLEX corner at (10,10)
/// and five convex ones; a fillet there removes (1 − π/4)·r²·h = 19.3141652942 of material on a convex
/// corner and ADDS exactly the same amount on the reflex one. So the three outcomes are three different
/// numbers: 67980.6858347058 / 68000 / 68019.3141652942.
/// <b>Cold series.</b> The adapter's failures grouped at the START of a repeat series, so each
/// experience here launches its OWN instance of KOMPAS: a state inherited from the previous experience
/// is exactly what the diagnosis must not have.
/// <b>What the probe does NOT do.</b> It does not touch the MCP reference registry, does not go through
/// the shipping Host, and does not call the adapter's settlement — a probe that measured through the
/// product could not attribute the shift.
/// History: docs/decisions/probes.md#fillet-rebuild
/// </remarks>
internal sealed class FilletRebuildProbe
{
    // KompasObjectTypes numbers, taken from the shipping adapter's own table.
    private const short Sketch = 5;
    private const short BaseExtrusion = 24;
    private const short CutExtrusion = 26;
    private const short Fillet = 34;
    private const short Chamfer = 33;
    private const int PlaneXoy = 1;
    private const short EndConditionBlind = 0;

    // KompasChoose numbers — the measured body-selector route of a cut extrusion.
    private const int ChooseBodies = 3;
    private const int ChooseManualEditing = 2;

    private const double Thickness = 10d;
    private const double Radius = 3d;
    private const double Leg1 = 2d;
    private const double Leg2 = 2d;
    private const double AngleDeg = 30d;

    /// <summary>The Г-contour of row FL25: 100×80 minus the 40×30 notch, one reflex corner at (10,10).</summary>
    private static readonly (double X, double Y)[] Contour =
    {
        (-50d, -40d), (50d, -40d), (50d, 10d), (10d, 10d), (10d, 40d), (-50d, 40d),
    };

    private const double PlateVolume = 6800d * Thickness;

    /// <summary>(1 − π/4)·r²·h — the material a convex corner fillet removes, and a reflex one adds.</summary>
    private static readonly double CornerSweep = (1d - Math.PI / 4d) * Radius * Radius * Thickness;

    private static readonly double ConvexFilletVolume = PlateVolume - CornerSweep;
    private static readonly double ReflexFilletVolume = PlateVolume + CornerSweep;

    /// <summary>d₁·d₂/2·h — the material a two-leg chamfer removes on a convex corner, adds on a reflex.</summary>
    private static readonly double ChamferVolume = Leg1 * Leg2 / 2d * Thickness;

    private static readonly double ConvexChamferVolume = PlateVolume - ChamferVolume;
    private static readonly double ReflexChamferVolume = PlateVolume + ChamferVolume;

    /// <summary>The requested corner: a CONVEX one of the Г-contour.</summary>
    private static readonly (double X, double Y) Requested = (50d, -40d);

    /// <summary>The single reflex corner, for naming a transfer.</summary>
    private static readonly (double X, double Y) Reflex = (10d, 10d);

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject? _app;

    public FilletRebuildProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Writes the report from inside a run, for the <c>--keep</c> diagnostic path.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        var stem = "fillet-rebuild-" + (options.FilletRebuild ?? "fillet");
        report.Flush(
            Path.Combine(options.ReportDir, stem + ".json"),
            Path.Combine(options.ReportDir, stem + ".md"));
    }

    public void Run()
    {
        var kind = _options.FilletRebuild ?? "fillet";
        var iterations = Math.Max(1, _options.Iterations);
        var summary = _report.Begin("FR.0", $"Сводка холодной серии: {kind}, опытов {iterations}",
            "Сколько опытов дали сдвиг, и в какую сторону?");
        summary.Data["kind"] = kind;
        summary.Data["iterations"] = iterations;
        summary.Data["requested_corner"] = Point(Requested.X, Requested.Y);
        summary.Data["reflex_corner"] = Point(Reflex.X, Reflex.Y);
        summary.Data["expected_convex_volume"] = Num(ConvexFilletVolume);
        summary.Data["expected_reflex_volume"] = Num(ReflexFilletVolume);

        var chains = new List<string>();
        var creates = new List<string>();
        var firstRebuild = new List<string>();
        var reopen = new List<string>();
        var nextOp = new List<string>();

        for (var k = 1; k <= iterations; k++)
        {
            var step = _report.Begin($"FR.{k}", $"{kind}: опыт {k} (свой экземпляр КОМПАС)",
                "Применяется ли признак, и что с ним делает контрольное перестроение?");
            try
            {
                Launch();
                if (_app is null)
                {
                    step.Fail("Экземпляр КОМПАС не поднят.");
                }
                else
                {
                    Measure(step, kind, k);
                }
            }
            catch (Exception ex)
            {
                step.Errors.Add(ex.ToString());
                step.Fail("Опыт упал: " + ex.Message);
            }
            finally
            {
                Finish();
            }

            if (step.Data.TryGetValue("chain", out var chain) && chain is string text)
            {
                chains.Add(text);
            }

            if (step.Data.TryGetValue("create_state", out var created) && created is string c)
            {
                creates.Add(c);
            }

            if (step.Data.TryGetValue("rebuild1_state", out var rb) && rb is string r)
            {
                firstRebuild.Add(r);
            }

            if (step.Data.TryGetValue("reopen_state", out var ro) && ro is string rr)
            {
                reopen.Add(rr);
            }

            if (step.Data.TryGetValue("nextop_state", out var no) && no is string nn)
            {
                nextOp.Add(nn);
            }

            Console.WriteLine($"[FR] {k}/{iterations} {kind}: create={Last(creates)} rb1={Last(firstRebuild)} "
                              + $"reopen={Last(reopen)} nextop={Last(nextOp)} chain={Last(chains)}");
        }

        summary.Data["create_states"] = Tally(creates);
        summary.Data["rebuild1_states"] = Tally(firstRebuild);
        summary.Data["reopen_states"] = Tally(reopen);
        summary.Data["nextop_states"] = Tally(nextOp);
        summary.Data["chains"] = Tally(chains);
        summary.Observe("Состояния: create=" + Tally(creates));
        summary.Observe("Состояния: первое перестроение=" + Tally(firstRebuild));
        summary.Observe("Состояния: reopen=" + Tally(reopen));
        summary.Observe("Состояния: следующая операция=" + Tally(nextOp));
        summary.Observe("Цепочки: " + Tally(chains));
        summary.Pass($"Опытов {iterations}: Create() применил признак в "
                     + Count(creates, s => s == "convex") + " из " + creates.Count
                     + "; первое перестроение оставило его на месте в "
                     + Count(firstRebuild, s => s == "convex") + " из " + firstRebuild.Count
                     + "; reopen — в " + Count(reopen, s => s == "convex") + " из " + reopen.Count + ".");
    }

    // ══════════════════════════════════════════════════════════════════ one experience ══

    private void Measure(ProbeStep step, string kind, int iteration)
    {
        // The "-host" suffix is not a different feature: it is the SAME creation route followed by the
        // adapter's OWN settlement order — one baseline read, then RebuildDocument + UpdateDocumentParam,
        // with NO read in between. MEASURED: the long chain below never reproduced the shift in 60 cold
        // experiences, and the chain reads the volume, the faces and the inputs between Create() and the
        // first rebuild — so the order/moment is the variable that had to be isolated, not the route.
        var hostOrder = kind.EndsWith("-host", StringComparison.Ordinal);
        var baseKind = hostOrder ? kind[..^"-host".Length] : kind;

        ksDocument3D? doc = null;
        var path = Path.Combine(_options.WorkDir, $"fr-{kind}-{iteration:000}.m3d");
        try
        {
            doc = (ksDocument3D)_app!.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);

            // The adapter's own document tail (CreateDocument): name the part, commit it with Update()
            // and rebuild before anything else is built.
            part.name = "fr-" + kind;
            part.Update();
            doc.RebuildDocument();

            if (!BuildPlate(part, step))
            {
                step.Fail("Г-пластина не построена.");
                return;
            }

            doc.RebuildDocument();
            doc.UpdateDocumentParam();
            var volumeFixture = Api5.Volume(part);
            step.Data["volume_fixture"] = Num(volumeFixture);
            if (!Near(volumeFixture, PlateVolume))
            {
                step.Fail($"Опора не та: объём пластины {Num(volumeFixture)}, ожидался {Num(PlateVolume)}.");
                return;
            }

            var edges = VerticalEdges(part, step);
            step.Data["vertical_edges"] = edges.Select(e => e.Label + (e.Convex ? " выпуклое" : " входящее")).ToList();
            // The REFLEX corner is the non-equivalent candidate on this contour, and therefore the one a
            // kernel re-resolving an edge reference is most likely to swap in — or out. A "-reflex" kind
            // asks for it, so the transfer is measured in both directions.
            var wantConvex = !kind.Contains("-reflex", StringComparison.Ordinal);
            var target = wantConvex ? Requested : Reflex;
            var chosen = edges.FirstOrDefault(e => e.Convex == wantConvex && e.Label == Point(target.X, target.Y));
            if (chosen is null)
            {
                step.Fail("Запрошенного " + (wantConvex ? "выпуклого" : "входящего") + " вертикального ребра "
                          + Point(target.X, target.Y) + " на теле нет — мерить нечего.");
                return;
            }

            step.Data["edge_requested"] = chosen.Label;
            step.Data["edge_requested_reference"] = PresentedReference(step, chosen.Entity);
            var (created, feature) = CreateFeature(step, baseKind, part, doc, chosen.Entity);
            if (!created)
            {
                step.Fail("Признак не создан прямым маршрутом.");
                return;
            }

            if (hostOrder)
            {
                SettleAsAdapter(step, baseKind, part, doc);
            }
            else
            {
                FullChain(step, baseKind, part, doc, feature);
            }

            // ── save → close → reopen: what the user sees when the file is opened ──
            doc.SaveAs(path);
            Close(doc);
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Open(path) != true)
            {
                step.Fail("Сохранённый документ не открылся повторно.");
                return;
            }

            part = (ksPart)doc.GetPart(-1);
            var volumeReopen = Api5.Volume(part);
            step.Data["volume_after_reopen"] = Num(volumeReopen);
            step.Data["reopen_state"] = Label(baseKind, volumeReopen);
            step.Data["cylinder_after_reopen"] = Cylinders(part);
            ReadInputs(step, "inputs_after_reopen", part, doc, baseKind, null);

            doc.RebuildDocument();
            var volumeReopenRebuild = Api5.Volume(part);
            step.Data["volume_after_reopen_rebuild"] = Num(volumeReopenRebuild);
            step.Data["reopen_rebuild_state"] = Label(baseKind, volumeReopenRebuild);

            // ── the next body operation: does the feature survive a foreign mutation ──
            var cut = AddCut(part, doc, step);
            var volumeNext = Api5.Volume(part);
            step.Data["cut_created"] = cut;
            step.Data["volume_after_next_op"] = Num(volumeNext);
            step.Data["nextop_state"] = NextOpLabel(baseKind, volumeNext);
            step.Data["cylinder_after_next_op"] = Cylinders(part);

            step.Data["chain"] = string.Join("→", new[]
            {
                step.Data["create_state"]?.ToString(),
                step.Data["rebuild1_state"]?.ToString(),
                step.Data["rebuild2_state"]?.ToString(),
                step.Data["rebuild3_state"]?.ToString(),
                step.Data["reopen_state"]?.ToString(),
                step.Data["reopen_rebuild_state"]?.ToString(),
                step.Data["nextop_state"]?.ToString(),
            }.Where(s => s is not null));

            step.Pass($"Create() → {step.Data["create_state"]}; первое перестроение → "
                      + $"{step.Data["rebuild1_state"]}; reopen → {step.Data["reopen_state"]}.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Опыт прервался: " + ex.Message);
        }
        finally
        {
            if (doc is not null)
            {
                Close(doc);
            }
        }
    }

    /// <summary>The adapter's own settlement, reproduced call for call: ONE baseline volume read, then
    /// <c>RebuildDocument()</c> + <c>UpdateDocumentParam()</c> repeated until two readings agree (limit 3).
    /// Nothing is read between <c>Create()</c> and the first rebuild — that is the whole point of this
    /// postановка.</summary>
    private void SettleAsAdapter(ProbeStep step, string baseKind, ksPart part, ksDocument3D doc)
    {
        var baseline = Api5.Volume(part);
        var previous = baseline;
        double? observed = baseline;
        var attempts = 0;
        var stable = false;
        while (attempts < 3)
        {
            if (doc.RebuildDocument() != true)
            {
                break;
            }

            doc.UpdateDocumentParam();
            attempts++;
            observed = Api5.Volume(part);
            if (previous is not null && observed is not null
                && (Agrees(baseline, observed) || Agrees(previous, observed)))
            {
                stable = true;
                break;
            }

            previous = observed;
        }

        step.Data["adapter_baseline"] = Num(baseline);
        step.Data["adapter_observed"] = Num(observed);
        step.Data["adapter_attempts"] = attempts;
        step.Data["adapter_stable"] = stable;
        step.Data["create_state"] = Label(baseKind, baseline);
        step.Data["rebuild1_state"] = Label(baseKind, observed);
        step.Data["cylinder_after_rebuild1"] = Cylinders(part);
        ReadInputs(step, "inputs_after_rebuilds", part, doc, baseKind, null);
    }

    /// <summary>The two readings agree within the same tolerance the adapter's settle uses.</summary>
    private static bool Agrees(double? left, double? right) =>
        left is double a && right is double b && Math.Abs(a - b) <= Math.Max(Math.Abs(a), 1d) * 1e-9;

    /// <summary>The long chain: reads between <c>Create()</c> and the first rebuild, three rebuilds one at
    /// a time, then the inputs re-read. Kept as the contrast postановка to <see cref="SettleAsAdapter"/>.</summary>
    private void FullChain(ProbeStep step, string baseKind, ksPart part, ksDocument3D doc, ksEntity? feature)
    {
        var volumeCreate = Api5.Volume(part);
        var volumeReread = Api5.Volume(part);
        step.Data["volume_after_create"] = Num(volumeCreate);
        step.Data["volume_after_reread"] = Num(volumeReread);
        step.Data["create_state"] = Label(baseKind, volumeCreate);
        step.Data["cylinder_after_create"] = Cylinders(part);
        ReadInputs(step, "inputs_after_create", part, doc, baseKind, feature);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var ok = doc.RebuildDocument();
            var volume = Api5.Volume(part);
            step.Data[$"rebuild{attempt}_ok"] = ok;
            step.Data[$"volume_after_rebuild{attempt}"] = Num(volume);
            step.Data[$"rebuild{attempt}_state"] = Label(baseKind, volume);
            step.Data[$"cylinder_after_rebuild{attempt}"] = Cylinders(part);
        }

        ReadInputs(step, "inputs_after_rebuilds", part, doc, baseKind, feature);
    }

    // ══════════════════════════════════════════════════════════════ fixture ══

    /// <summary>The Г-plate: the FL25 contour on XOY, base-extruded by the thickness.</summary>
    private static bool BuildPlate(ksPart part, ProbeStep step)
    {
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane)
        {
            step.Observe("GetDefaultEntity(o3d_planeXOY=1) вернул null.");
            return false;
        }

        if (part.NewEntity(Sketch) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            step.Observe("NewEntity(o3d_sketch=5) или его определение не получены.");
            return false;
        }

        sketch.name = "fr-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();
        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            step.Observe("BeginEdit() вернул не ksDocument2D.");
            return false;
        }

        for (var i = 0; i < Contour.Length; i++)
        {
            var a = Contour[i];
            var b = Contour[(i + 1) % Contour.Length];
            editor.ksLineSeg(a.X, a.Y, b.X, b.Y, 1);
        }

        sketchDefinition.EndEdit();

        if (part.NewEntity(BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe("NewEntity(o3d_baseExtrusion=24) или его определение не получены.");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, EndConditionBlind, Thickness, 0d, false);
        return Api5.SafeBool(extrusion.Create) == true;
    }

    /// <summary>The next body operation: a blind cut extrusion Ø10 at (0, 30) that goes through the plate.
    /// Its target body is named by the measured selector route, because an unnamed cut would silently act
    /// on the wrong body.</summary>
    private bool AddCut(ksPart part, ksDocument3D doc, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane)
            {
                return false;
            }

            if (part.NewEntity(Sketch) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                return false;
            }

            sketch.name = "fr-cut-profile";
            sketchDefinition.SetPlane(plane);
            sketch.Create();
            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                return false;
            }

            editor.ksCircle(0d, 30d, 5d, 1);
            sketchDefinition.EndEdit();

            if (part.NewEntity(CutExtrusion) is not ksEntity cut
                || cut.GetDefinition() is not ksCutExtrusionDefinition definition)
            {
                step.Observe("NewEntity(o3d_cutExtrusion=26) или ksCutExtrusionDefinition не получены.");
                return false;
            }

            // The call order is the adapter's own (ApplyBodyChoice → SetSketch → directionType →
            // SetSideParam): a different order was measured to leave the sketch detached.
            definition.chooseType = ChooseBodies;
            if (definition.ChooseBodies() is not ksChooseBodies chooseBodies)
            {
                step.Observe("ChooseBodies() вернул не ksChooseBodies — целевое тело не названо.");
                return false;
            }

            chooseBodies.ChooseBodiesType = ChooseManualEditing;
            if (chooseBodies.BodyCollection() is not ksBodyCollection bodies)
            {
                step.Observe("ChooseBodies().BodyCollection() вернул не ksBodyCollection.");
                return false;
            }

            var raw = part.BodyCollection() is ksBodyCollection all && all.GetCount() > 0
                ? all.GetByIndex(0)
                : null;
            if (raw is null || !bodies.Add(raw))
            {
                step.Observe("Тело не принято селектором выреза — следующая операция не состоялась.");
                return false;
            }

            step.Observe($"Селектор выреза: тел в наборе {bodies.GetCount()}.");
            if (Api5.SafeBool(() => definition.SetSketch(sketch)) != true)
            {
                step.Observe("SetSketch вернул не true.");
                return false;
            }

            // The help's own rule for a cut: its FORWARD direction is opposite to the sketch normal, so
            // directionType=1 is the side the material is on. MEASURED here: with 0 the cut is created
            // (Create()=true) and removes nothing.
            definition.directionType = 1;
            var side = Api5.SafeBool(() => definition.SetSideParam(true, EndConditionBlind, 30d, 0d, false));
            step.Observe("SetSideParam(blind, 30) → " + Api5.Raw(side) + ".");
            if (side != true)
            {
                return false;
            }

            var created = Api5.SafeBool(cut.Create);
            step.Observe("Вырез: Create() → " + Api5.Raw(created) + ".");
            if (created != true)
            {
                return false;
            }

            doc.RebuildDocument();
            return true;
        }
        catch (Exception ex)
        {
            step.Observe("Следующая операция над телом не удалась: " + HResult.Describe(ex));
            return false;
        }
    }

    /// <summary>The vertical edges of the body that span the full thickness, with their contour corner.</summary>
    private static List<VerticalEdge> VerticalEdges(ksPart part, ProbeStep step)
    {
        var found = new List<VerticalEdge>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("Тело или коллекция граней недоступны.");
            return found;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var f = 0; f < faces.GetCount(); f++)
        {
            var faceObject = faces.GetByIndex(f);
            var face = faceObject as ksFaceDefinition ?? (faceObject as ksEntity)?.GetDefinition() as ksFaceDefinition;
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition
                           ?? (edgeObject as ksEntity)?.GetDefinition() as ksEdgeDefinition;
                if (edge is null || Api5.SafeBool(edge.IsStraight) != true)
                {
                    continue;
                }

                if (!Midpoint(edge, out var x, out var y, out var zLow, out var zHigh))
                {
                    continue;
                }

                if (Math.Abs(zHigh - Thickness) > 1e-3 || Math.Abs(zLow) > 1e-3)
                {
                    continue;
                }

                var label = Point(x, y);
                if (!seen.Add(label))
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                found.Add(new VerticalEdge(label, x, y, IsConvex(x, y), entity));
            }
        }

        return found;
    }

    /// <summary>Whether a contour vertex is convex. The contour is counter-clockwise, so a positive cross
    /// product of the two adjacent sides means convex — the same rule the acceptance probe uses.</summary>
    private static bool IsConvex(double x, double y)
    {
        for (var i = 0; i < Contour.Length; i++)
        {
            if (Math.Abs(Contour[i].X - x) > 1e-3 || Math.Abs(Contour[i].Y - y) > 1e-3)
            {
                continue;
            }

            var previous = Contour[(i + Contour.Length - 1) % Contour.Length];
            var current = Contour[i];
            var next = Contour[(i + 1) % Contour.Length];
            var cross = (current.X - previous.X) * (next.Y - current.Y)
                        - (current.Y - previous.Y) * (next.X - current.X);
            return cross > 0;
        }

        return false;
    }

    private static bool Midpoint(ksEdgeDefinition edge, out double x, out double y, out double zLow, out double zHigh)
    {
        x = 0;
        y = 0;
        zLow = 0;
        zHigh = 0;
        try
        {
            if (edge.GetVertex(true) is not ksVertexDefinition first || edge.GetVertex(false) is not ksVertexDefinition second
                || !first.GetPoint(out var ax, out var ay, out var az) || !second.GetPoint(out var bx, out var by, out var bz))
            {
                return false;
            }

            x = (ax + bx) / 2d;
            y = (ay + by) / 2d;
            zLow = Math.Min(az, bz);
            zHigh = Math.Max(az, bz);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════ creation ══

    /// <summary>Creates the feature by the route the adapter uses — API5 <c>NewEntity</c> for the fillet
    /// and the two-leg chamfer, the API7 factory for the distance+angle chamfer. The live handle is
    /// RETURNED: a feature this process created is reachable only through it (probe R.0d), so the tree
    /// walk is a fallback, not the primary route.</summary>
    private (bool Created, ksEntity? Handle) CreateFeature(ProbeStep step, string kind, ksPart part, ksDocument3D doc, ksEntity edge)
    {
        switch (kind)
        {
            case "chamfer-da":
                // The API7 factory hands back no API5 handle at all — the feature is read through the
                // container, and saying so is the honest form of "no handle".
                return (CreateChamferByAngle(step, doc, edge), null);
            case "chamfer":
                var chamfer = CreateChamferByLegs(step, part, edge);
                return (chamfer is not null, chamfer);
            default:
                var fillet = CreateFillet(step, part, edge);
                return (fillet is not null, fillet);
        }
    }

    private static ksEntity? CreateFillet(ProbeStep step, ksPart part, ksEntity edge)
    {
        if (part.NewEntity(Fillet) is not ksEntity feature
            || feature.GetDefinition() is not ksFilletDefinition definition)
        {
            step.Observe("NewEntity(o3d_fillet=34) или ksFilletDefinition не получены.");
            return null;
        }

        definition.radius = Radius;
        definition.tangent = false;
        if (definition.array() is not ksEntityCollection array)
        {
            step.Observe("ksFilletDefinition.array() вернул не ksEntityCollection.");
            return null;
        }

        if (!array.Add(edge))
        {
            step.Observe("array().Add(ребро) отказал.");
            return null;
        }

        return Api5.SafeBool(feature.Create) == true ? feature : null;
    }

    private static ksEntity? CreateChamferByLegs(ProbeStep step, ksPart part, ksEntity edge)
    {
        if (part.NewEntity(Chamfer) is not ksEntity feature
            || feature.GetDefinition() is not ksChamferDefinition definition)
        {
            step.Observe("NewEntity(o3d_chamfer=33) или ksChamferDefinition не получены.");
            return null;
        }

        definition.SetChamferParam(false, Leg1, Leg2);
        if (definition.array() is not ksEntityCollection array)
        {
            step.Observe("ksChamferDefinition.array() вернул не ksEntityCollection.");
            return null;
        }

        if (!array.Add(edge))
        {
            step.Observe("array().Add(ребро) отказал.");
            return null;
        }

        return Api5.SafeBool(feature.Create) == true ? feature : null;
    }

    /// <summary>The distance+angle chamfer goes through the API7 factory: <c>IModelContainer.Chamfers.Add()</c>
    /// with <c>BuildingType = ksChamferSideAngle</c>, <c>BaseObjects</c> written as a whole, then
    /// <c>Update()</c> — the route the shipping adapter uses.</summary>
    private bool CreateChamferByAngle(ProbeStep step, ksDocument3D doc, ksEntity edge)
    {
        var container = Container(step, doc);
        if (container is null)
        {
            return false;
        }

        try
        {
            var transferred = _app!.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is not IModelObject modelObject)
            {
                step.Observe("Ребро не перенеслось в API7.");
                return false;
            }

            var chamfer = container.Chamfers.Add();
            chamfer.BuildingType = ksChamferBuildingTypeEnum.ksChamferSideAngle;
            chamfer.BaseObjects = new IModelObject[] { modelObject };
            chamfer.Distance1 = Leg1;
            chamfer.Angle = AngleDeg;
            chamfer.Update();
            return true;
        }
        catch (Exception ex)
        {
            step.Observe("Маршрут API7 фаски упал: " + HResult.Describe(ex));
            return false;
        }
    }

    private IModelContainer? Container(ProbeStep step, ksDocument3D doc)
    {
        try
        {
            var document7 = _app!.TransferInterface(doc, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            if (container is null)
            {
                step.Observe("Документ/TopPart не дали IModelContainer.");
            }

            return container;
        }
        catch (Exception ex)
        {
            step.Observe("QI(IModelContainer) упал: " + HResult.Describe(ex));
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ reading ══

    /// <summary>Where the fillet surface ended up: every cylindrical face of the body with its radius and
    /// the axis origin. A convex-corner fillet at (50,−40) puts the axis at (47,−37); a reflex-corner one
    /// at (13,13). The volume alone cannot say WHICH corner — this can.</summary>
    private static List<string> Cylinders(ksPart part)
    {
        var prepared = new ProbeStep { Id = "—", Title = "чтение цилиндрических граней" };
        return Api5.ReadFaces(part, prepared)
            .Where(f => f.IsCylinder && Math.Abs((f.CylinderRadius ?? 0d) - Radius) < 1e-3)
            .Select(f => "r=" + Num(f.CylinderRadius) + " начало=" + Api5.FaceReading.Point(f.CylinderOrigin)
                         + " ось=" + Api5.FaceReading.Point(f.CylinderAxis))
            .ToList();
    }

    /// <summary>The feature's OWN inputs, by both routes: the API5 definition's <c>array()</c> and the API7
    /// feature's <c>BaseObjects</c>. Read after each stage, because the question is whether the feature
    /// still NAMES the edge it was given.</summary>
    private void ReadInputs(ProbeStep step, string key, ksPart part, ksDocument3D doc, string kind, ksEntity? handle)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal);

        // API5 side. The live handle is tried FIRST (a feature this process created is reachable only
        // through it), and the tree walk is the fallback. Which one answered is part of the record.
        var entity = Handle(handle) ?? FindFeature(part, kind);
        record["feature_found"] = entity is not null;
        record["feature_source"] = entity is null ? "нет" : (ReferenceEquals(entity, handle) ? "живой дескриптор" : "дерево");
        record["tree_types"] = TreeTypes(part);
        if (entity is not null)
        {
            try
            {
                var array = kind switch
                {
                    "chamfer" or "chamfer-da" => (entity.GetDefinition() as ksChamferDefinition)?.array(),
                    _ => (entity.GetDefinition() as ksFilletDefinition)?.array(),
                };
                if (array is ksEntityCollection collection)
                {
                    var labels = new List<string>();
                    var elementTypes = new List<string>();
                    for (var i = 0; i < collection.GetCount(); i++)
                    {
                        var element = collection.GetByIndex(i);
                        var definition = element as ksEdgeDefinition
                                         ?? (element as ksEntity)?.GetDefinition() as ksEdgeDefinition;
                        elementTypes.Add(Api5.RuntimeName(element));
                        if (definition is not null && Midpoint(definition, out var x, out var y, out _, out _))
                        {
                            labels.Add(Point(x, y));
                        }
                    }

                    record["api5_count"] = collection.GetCount();
                    record["api5_edges"] = labels;
                    // An EMPTY list next to a non-zero count is a fact about the definition, not about the
                    // feature: the element types are recorded so the two cannot be confused.
                    record["api5_element_types"] = elementTypes;
                }
                else
                {
                    record["api5_array"] = "не прочитано";
                }
            }
            catch (Exception ex)
            {
                record["api5_error"] = HResult.Describe(ex);
            }
        }

        // API7 side: the feature's own inputs through the container, the route measured by probe H-2.
        try
        {
            var container = Container(step, doc);
            if (container is not null)
            {
                if (kind == "chamfer" || kind == "chamfer-da")
                {
                    record["api7_count"] = Api5.SafeInt(() => container.Chamfers.Count);
                }
                else
                {
                    record["api7_count"] = Api5.SafeInt(() => container.Fillets.Count);
                    var fillet = Api5.SafeObject(() => container.Fillets[0]) as IFillet;
                    if (fillet?.BaseObjects is object[] baseObjects)
                    {
                        var references = new List<int>();
                        foreach (var item in baseObjects)
                        {
                            if (item is IModelObject modelObject)
                            {
                                references.Add(modelObject.Reference);
                            }
                        }

                        record["api7_base_objects"] = references;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            record["api7_error"] = HResult.Describe(ex);
        }

        step.Data[key] = record;
    }

    /// <summary>The feature of the requested family in the tree — the FALLBACK route. A feature this
    /// process created is in no collection at all (probe R.0d), so this answers only for a feature read
    /// back from a file.</summary>
    private static ksEntity? FindFeature(ksPart part, string kind)
    {
        var wanted = kind == "chamfer" || kind == "chamfer-da" ? Chamfer : Fillet;
        var prepared = new ProbeStep { Id = "—", Title = "поиск признака в дереве" };
        foreach (var reading in Api5.ReadFeatures(part, prepared))
        {
            if (reading.Type == wanted && reading.Entity is not null)
            {
                return reading.Entity;
            }
        }

        return null;
    }

    /// <summary>Whether a held handle still answers. A handle that survived a rebuild is usable; one that
    /// did not is not — and "it threw" is not the same as "the feature is gone".</summary>
    private static ksEntity? Handle(ksEntity? handle)
    {
        if (handle is null)
        {
            return null;
        }

        try
        {
            _ = handle.type;
            return handle;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>What the feature tree holds, as type numbers — the record that makes "the feature is not
    /// in the tree" a statement about the tree rather than about the search.</summary>
    private static List<string> TreeTypes(ksPart part)
    {
        var prepared = new ProbeStep { Id = "—", Title = "чтение дерева" };
        return Api5.ReadFeatures(part, prepared)
            .Select(r => (r.Type?.ToString(CultureInfo.InvariantCulture) ?? "?") + "/" + (r.Name ?? "<без имени>"))
            .ToList();
    }

    /// <summary>The API7 <c>Reference</c> of the edge that was PRESENTED to the feature. Without it the
    /// answer to "does the feature still name the edge it was given" has only one side.</summary>
    private int? PresentedReference(ProbeStep step, ksEntity edge)
    {
        try
        {
            var transferred = _app!.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is IModelObject modelObject)
            {
                return modelObject.Reference;
            }

            step.Observe("Предъявленное ребро не перенеслось в API7 — сравнить входы не с чем.");
            return null;
        }
        catch (Exception ex)
        {
            step.Observe("Перенос предъявленного ребра упал: " + HResult.Describe(ex));
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ naming ══

    /// <summary>What the measured volume SAYS about the feature: which corner it is on, or that it is
    /// not in the body at all. The three states are the whole point — a number that is neither of the two
    /// references is reported as "иное" with the number, never rounded into one of them.</summary>
    private static string Label(string kind, double? volume)
    {
        if (volume is not double value)
        {
            return "не прочитано";
        }

        var chamfer = kind == "chamfer" || kind == "chamfer-da";
        var convex = chamfer
            ? (kind == "chamfer-da" ? PlateVolume - Leg1 * (Leg1 * Math.Tan(AngleDeg * Math.PI / 180d)) / 2d * Thickness : ConvexChamferVolume)
            : ConvexFilletVolume;
        var reflex = chamfer
            ? (kind == "chamfer-da" ? PlateVolume + Leg1 * (Leg1 * Math.Tan(AngleDeg * Math.PI / 180d)) / 2d * Thickness : ReflexChamferVolume)
            : ReflexFilletVolume;

        if (Near(value, convex))
        {
            return "convex";
        }

        if (Near(value, reflex))
        {
            return "reflex";
        }

        if (Near(value, PlateVolume))
        {
            return "нет признака";
        }

        return "иное(" + Num(value) + ")";
    }

    private static bool Near(double? value, double expected) =>
        value is double v && Math.Abs(v - expected) <= 0.01d;

    /// <summary>The cut's own analytic volume: π·r²·h through the plate.</summary>
    private static readonly double CutVolume = Math.PI * 25d * Thickness;

    /// <summary>After the next body operation the total volume carries BOTH the feature and the cut, so the
    /// three states are shifted by the cut. Without that shift a lost fillet and a surviving one would be
    /// told apart only by a number the reader has to subtract himself.</summary>
    private static string NextOpLabel(string kind, double? volume)
    {
        var convex = kind == "chamfer" || kind == "chamfer-da" ? ConvexChamferVolume : ConvexFilletVolume;
        var reflex = kind == "chamfer" || kind == "chamfer-da" ? ReflexChamferVolume : ReflexFilletVolume;
        if (volume is not double value)
        {
            return "не прочитано";
        }

        if (Near(value, convex - CutVolume))
        {
            return "convex+вырез";
        }

        if (Near(value, reflex - CutVolume))
        {
            return "reflex+вырез";
        }

        if (Near(value, PlateVolume - CutVolume))
        {
            return "нет признака+вырез";
        }

        return "иное(" + Num(value) + ")";
    }

    private static string Point(double x, double y) =>
        "(" + x.ToString("0.####", CultureInfo.InvariantCulture) + ","
            + y.ToString("0.####", CultureInfo.InvariantCulture) + ")";

    private static string Num(double? value) =>
        value?.ToString("0.############", CultureInfo.InvariantCulture) ?? "null";

    private static string Tally(IReadOnlyList<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            counts[value] = counts.TryGetValue(value, out var current) ? current + 1 : 1;
        }

        return counts.Count == 0
            ? "<нет данных>"
            : string.Join(", ", counts.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + " ×" + p.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static int Count(IReadOnlyList<string> values, Func<string, bool> predicate) =>
        values.Count(predicate);

    private static string Last(IReadOnlyList<string> values) => values.Count == 0 ? "-" : values[^1];

    // ══════════════════════════════════════════════════════════════ session ══

    /// <summary>A fresh, invisible instance for every experience. Nothing is inherited from the previous
    /// one — that is the definition of the cold phase this diagnosis needs.</summary>
    private void Launch()
    {
        _app = null;
        var step = _report.Begin("FR.app", "Свой невидимый экземпляр КОМПАС",
            "Поднимается ли экземпляр для этого опыта?");
        try
        {
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5");
            if (type is null)
            {
                step.Fail("ProgID KOMPAS.Application.5 не зарегистрирован.");
                return;
            }

            var raw = Activator.CreateInstance(type);
            _app = (KompasObject)raw!;
            _app.Visible = false;
            step.Data["pid"] = Environment.ProcessId;
            step.Pass("Экземпляр поднят.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Экземпляр не поднят: " + ex.Message);
        }
    }

    private void Finish()
    {
        try
        {
            _app?.Quit();
        }
        catch (Exception ex)
        {
            _report.Note("FR.app", "Quit() бросил: " + HResult.Describe(ex));
        }
        finally
        {
            _app = null;
        }
    }

    private static void Close(ksDocument3D doc)
    {
        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // Closing after a crash is cleanup, not measurement.
        }
    }

    private sealed record VerticalEdge(string Label, double X, double Y, bool Convex, ksEntity Entity);
}
