using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;

namespace KompasMcp.Api7Probe;

/// <summary>Probe K — sketch constraints and driving dimensions: the API7 route
/// <c>IDrawingObject1.NewConstraint() → IParametriticConstraint.Create()</c> measured on the shipped
/// build, together with the addressing of the sketch objects it needs.</summary>
/// <remarks>
/// Order: <c>OMEGA_G6_SKETCH_CONSTRAINTS_DEVELOPER_PROMPT.md</c> §9 (OQ-1…OQ-6) and §11 (analytical
/// references).
/// WHY A NEW PROBE AND NOT A REPEAT OF S.4c. Probe S reached the constraint route through
/// <c>IView1.FindObject(x, y, limit, params)</c>, whose parameter class is NOT registered on this
/// installation (<c>REGDB_E_CLASSNOTREG</c>), and read the failure as "the route is unavailable".
/// That was a statement about the INSTRUMENT: the documented way to obtain the object is the
/// container enumeration (<c>IDrawingContainer.GetObjects</c> / <c>IDrawingObjects.Item</c>), which
/// the shipping adapter already uses successfully. Here the object is taken exactly that way, and
/// "the object was not reached" is kept apart from "the constraint was not created".
/// A TRAP OF THIS INSTRUMENT, measured on its own first run: an <c>IDrawingObject1</c> and its
/// <c>Constraints</c> are alive ONLY INSIDE the edit session. Read after <c>EndEdit()</c> the very
/// same object answers <c>Constraints → null</c> and zero coordinates — a silent refusal, no
/// exception. So every read here happens INSIDE the session, and a value read outside it is
/// recorded as a defect of the instrument, not as a property of КОМПАС.
/// Every open question of §9 is closed by a measurement in its own step, and an unclosed one stays
/// NAMED — a silently vanished question is a defect of the report.
/// Its own STA thread, its own invisible KOMPAS instance, its own documents in the work directory.
/// History: docs/decisions/probes.md#sketch-constraints
/// </remarks>
internal sealed class SketchConstraintsProbe
{
    /// <summary>Half-width of the reference rectangle (mm). Analytical volume of the extrusion:
    /// <c>4·a·b·h = 4·20·15·10 = 12000</c> mm³.</summary>
    private const double HalfA = 20d;
    private const double HalfB = 15d;
    private const double Thickness = 10d;
    private const double RectangleVolume = 4d * HalfA * HalfB * Thickness;

    /// <summary>The discriminating control for <c>ksCHorizontal</c>: a segment of length 25 mm at
    /// exactly 30° to the X axis. Its end point is <c>(25·cos30°, 25·sin30°) = (21.6506350946, 12.5)</c>.
    /// If the kernel ROTATES the segment, the end Y falls from 12.5 to 0; if it does not, the
    /// coordinates do not move. The two outcomes differ by exactly 12.5 mm, so the control
    /// discriminates rather than "approximately".</summary>
    private const double TiltLength = 25d;
    private const double TiltAngleDeg = 30d;
    private static readonly double TiltX = TiltLength * Math.Cos(TiltAngleDeg * Math.PI / 180d);
    private static readonly double TiltY = TiltLength * Math.Sin(TiltAngleDeg * Math.PI / 180d);

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private KompasAPI7.IApplication? _app7;

    private ksDocument3D? _doc;
    private ksPart? _part;
    private ksEntity? _sketchEntity;

    public SketchConstraintsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Writes the report to disk early, so a diagnostic run (<c>--keep</c>) leaves its
    /// observations behind even if a COM call parks the apartment afterwards.</summary>
    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "sketch-constraints.json"),
            Path.Combine(options.ReportDir, "sketch-constraints.md"));

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            Bridge();
            Addressing();
            TypeVocabulary();
            GeometricTypes();
            DimensionTypes();
            DimensionWithVariable();
            FixedDimLadder();
            DimensionRoute();
            DimensionDrives();
            AssociationRoute();
            VariableDimLadder();
            SketchFeatureVariable();
            ParametricModeRoute();
            FullyDefinedReopen();
            ReadBack();
            DeleteRoute();
            HorizontalControl();
            ObjectVsAggregateState();
            Api5NegativeControl();
            FullyDefined();
            MinimalFullyDefined();
            IncrementalState();
            FixedPointPins();
            NegativeTests();
            DiametralDimension();
            RadialDimension();
            AngularDimension();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("K.E", "Необработанное исключение пробы K");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("K.1", "Свой невидимый экземпляр и доказательство PID",
            "Зонд работает с тем КОМПАС, который сам запустил?");
        try
        {
            var before = KompasIds();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            step.Data["kompas_ids_before"] = before.Length;

            var appeared = WaitForNewProcess(before, 90_000);
            step.Data["new_process"] = appeared;
            if (appeared is null)
            {
                step.Fail("Новый процесс КОМПАС не появился — принадлежность сеанса не доказана.");
                return;
            }

            _options.ProcessId = appeared;
            step.Pass($"Собственный экземпляр поднят, новый PID {appeared}.");
        }
        catch (Exception ex)
        {
            step.Fail("Экземпляр не поднят: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("K.Z", "Завершение сеанса", "Остаётся ли осиротевший процесс?");
        try
        {
            if (_doc is not null)
            {
                step.Data["doc_close"] = Api5.Raw(TryBool(() => _doc.close()));
            }

            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым; закройте его штатно.");
                return;
            }

            step.Data["quit"] = Api5.Raw(TryBool(() => { _app.Quit(); return true; }));
            var pid = _options.ProcessId;
            var gone = pid is null || WaitUntilGone(pid.Value, 30_000);
            step.Pass(gone
                ? "Собственный экземпляр завершён штатно, по имени процессов не убивали."
                : "Процесс жив через 30 с после Quit(): штатное завершение не подтверждено.");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение не подтверждено: " + ex.Message);
        }
    }

    private void Bridge()
    {
        var step = _report.Begin("K.2", "Мост API5→API7", "Тот же сеанс отдаёт IApplication API7?");
        try
        {
            var raw = _app.ksGetApplication7();
            if (raw is null)
            {
                step.Fail("ksGetApplication7() вернул null.");
                return;
            }

            _app7 = (KompasAPI7.IApplication)raw;
            _app7.Visible = false;
            step.Pass("Мост построен: ksGetApplication7() того же сеанса.");
        }
        catch (Exception ex)
        {
            step.Fail("Мост не построен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-1 ══

    private void Addressing()
    {
        var step = _report.Begin("K.3", "OQ-1 — адресация объектов эскиза и получение IDrawingObject1",
            "Сколько объектов эскиза даёт каждый маршрут, отвечает ли каждый на IDrawingObject1, и " +
            "совпадает ли число с независимыми счётчиками?");
        try
        {
            if (!BuildRectangleDocument("k-rect", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            step.Data["expected_segments"] = 4;
            step.Data["volume"] = Api5.Num(Api5.Volume(_part!));
            step.Data["volume_analytic"] = Api5.Num(RectangleVolume);

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7 — адресация не измерена.");
                return;
            }

            var notes = new List<string>();
            var objects = InSession(sketch, write: false, notes, (items, _) => items);
            step.Data["notes"] = notes;

            if (objects is null)
            {
                foreach (var note in notes)
                {
                    step.Observe(note);
                }

                step.Fail("Объекты эскиза не получены документированным маршрутом.");
                return;
            }

            var withConstraint = objects.Count(o => o.ConstraintHost is not null);
            step.Data["objects_from_container"] = objects.Count;
            step.Data["objects_answering_idrawingobject1"] = withConstraint;

            foreach (var note in notes)
            {
                step.Observe(note);
            }

            foreach (var item in objects)
            {
                step.Observe($"объект {item.Index}: {item.DrawingKind}, IDrawingObject1={item.ConstraintHost is not null}, " +
                             item.Geometry);
            }

            step.Data["count_matches_expected"] = objects.Count == 4;
            step.Data["all_are_constraint_hosts"] = withConstraint == objects.Count;

            if (objects.Count == 4 && withConstraint == 4)
            {
                step.Pass("Четыре отрезка получены контейнером, каждый отвечает IDrawingObject1 — " +
                          "маршрут адресации подтверждён, и это тот маршрут, которым пользуется поставка.");
            }
            else
            {
                step.Unknown($"Контейнер дал {objects.Count} объектов, IDrawingObject1 отвечают " +
                             $"{withConstraint}. Расхождение названо, а не сглажено.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Адресация не измерена: " + ex.Message);
        }
    }

    private void TypeVocabulary()
    {
        var step = _report.Begin("K.4", "Словарь типов ограничений",
            "Совпадает ли объявленный набор ksConstraintTypeEnum с ожидаемым по наряду, и берётся ли " +
            "он из одной сборки?");
        try
        {
            var type = typeof(ksConstraintTypeEnum);
            step.Data["assembly"] = type.Assembly.GetName().Name;
            var declared = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in Enum.GetValues(type))
            {
                var name = Enum.GetName(type, value) ?? value.ToString()!;
                declared[name] = Convert.ToInt32(value);
            }

            step.Data["declared_count"] = declared.Count;
            step.Data["declared"] = string.Join(", ", declared.OrderBy(p => p.Value)
                .Select(p => $"{p.Value}={p.Key}"));

            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ksCFixedPoint"] = 1, ["ksCPointOnCurve"] = 2, ["ksCHorizontal"] = 3,
                ["ksCVertical"] = 4, ["ksCParallel"] = 5, ["ksCPerpendicular"] = 6,
                ["ksCEqualLength"] = 7, ["ksCEqualRadius"] = 8, ["ksCHAlignPoints"] = 9,
                ["ksCVAlignPoints"] = 10, ["ksCMergePoints"] = 11, ["ksCAssociation"] = 12,
                ["ksCDimWithVariable"] = 13, ["ksCFixedDim"] = 14, ["ksCTangentTwoCurves"] = 15,
                ["ksCSymmetryTwoPoints"] = 16, ["ksCCollinear"] = 17, ["ksCFixedAngle"] = 18,
                ["ksCFixedLenght"] = 19, ["ksCPointOnCurveMiddle"] = 20, ["ksCBisector"] = 21,
                ["ksCConcentricity"] = 22,
            };

            var mismatches = expected.Where(p => !declared.TryGetValue(p.Key, out var v) || v != p.Value)
                .Select(p => $"{p.Key}: ожидалось {p.Value}").ToList();
            step.Data["mismatches"] = mismatches.Count == 0 ? "нет" : string.Join("; ", mismatches);

            if (mismatches.Count == 0)
            {
                step.Pass($"Объявлено {declared.Count} значений, все 22 ожидаемых типа совпали по номеру.");
            }
            else
            {
                step.Fail("Расхождение с ожидаемым словарём: " + string.Join("; ", mismatches));
            }
        }
        catch (Exception ex)
        {
            step.Fail("Словарь не прочитан: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-5 ══

    private void GeometricTypes()
    {
        var step = _report.Begin("K.5", "OQ-5 — какие геометрические типы действительно создаются",
            "Документированный constrType ≠ принимаемый ядром: по одному вызову на тип с корректной " +
            "постановкой, отказ называется исходом.");
        try
        {
            var results = new List<string>();
            var accepted = new List<string>();

            foreach (var candidate in GeometricCandidates())
            {
                var attempt = Attempt(step, "geo-" + candidate.Name, candidate.Type, candidate.Doc, candidate.Object,
                    candidate.Partner, candidate.Axis);
                results.Add($"{candidate.Name} ({candidate.Type}) → {attempt.Summary}");
                step.Data["geo_" + candidate.Name] = attempt.Summary;
                if (attempt.Created == true)
                {
                    accepted.Add(candidate.Name);
                }
            }

            step.Data["attempts"] = results;
            step.Data["accepted"] = accepted;
            step.Data["accepted_count"] = accepted.Count;
            foreach (var line in results)
            {
                step.Observe(line);
            }

            step.Pass($"Принято ядром {accepted.Count} из {results.Count} проверенных типов: " +
                      string.Join(", ", accepted));
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Типы не измерены: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-2 ══

    private void DimensionTypes()
    {
        var step = _report.Begin("K.6", "OQ-2 и SKC-02 — размерные типы, единицы Value, Expression/Variable",
            "Принимаются ли четыре размерных типа, в каких единицах читается Value, и перечитываются ли " +
            "Expression/Variable у ksCDimWithVariable?");
        try
        {
            var results = new List<string>();

            foreach (var candidate in DimensionCandidates())
            {
                var attempt = Attempt(step, "dim-" + candidate.Name, candidate.Type, candidate.Doc, candidate.Object,
                    candidate.Partner, null, candidate.Value, candidate.Expression, candidate.Variable);
                results.Add($"{candidate.Name} ({candidate.Type}) → {attempt.Summary}");
                step.Data["dim_" + candidate.Name] = attempt.Summary;
                step.Data["dim_" + candidate.Name + "_value_read"] = Api5.Num(attempt.ValueRead);
                step.Data["dim_" + candidate.Name + "_degrees"] = attempt.DegreesRead;
                step.Data["dim_" + candidate.Name + "_minutes"] = attempt.MinutesRead;
                step.Data["dim_" + candidate.Name + "_seconds"] = Api5.Num(attempt.SecondsRead);
                step.Data["dim_" + candidate.Name + "_expression"] = attempt.ExpressionRead;
                step.Data["dim_" + candidate.Name + "_variable"] = attempt.VariableRead;
                step.Data["dim_" + candidate.Name + "_stored"] = attempt.StoredSummary;
            }

            step.Data["attempts"] = results;
            foreach (var line in results)
            {
                step.Observe(line);
            }

            step.Pass("Размерные типы проверены по одному вызову на тип; Value/Degrees/Minutes/Seconds/" +
                      "Expression/Variable перечитаны ИЗ СОЗДАННОГО ограничения в списке объекта, а не из " +
                      "объекта-шаблона, и записаны в данные шага.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Размерные типы не измерены: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════ dimension with variable ══

    /// <summary>OQ-2 (units) and the second half of <c>SKC-02</c>: the documented precondition of
    /// <c>ksCDimWithVariable</c> is an EXISTING <c>ksCFixedDim</c> on the object
    /// (<c>iparametriticconstraint_value.html</c>: «Установить значение можно, если на объекте есть
    /// ограничение "Фиксированный размер"(ksCFixedDim)»). So the two are created IN ORDER on one
    /// object, and <c>Value</c>/<c>Expression</c>/<c>Variable</c> are read back FROM THE STORED
    /// CONSTRAINT — not from the template, whose members keep their defaults.</summary>
    private void DimensionWithVariable()
    {
        var step = _report.Begin("K.6b", "SKC-02 — размер с переменной после фиксированного размера",
            "Создаётся ли ksCDimWithVariable при уже наложенном ksCFixedDim, и совпадают ли " +
            "перечитанные Value/Expression/Variable с заданными?");
        try
        {
            if (!BuildRectangleDocument("k-dimvar", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            const string variableName = "k_width_40";
            const double wanted = 40d;
            var notes = new List<string>();
            var (fixedSummary, variableSummary, stored, valueRead, expressionRead, variableRead) =
                InSession(sketch, write: true, notes, (objects, readNotes) =>
                {
                    if (objects.Count == 0 || objects[0].ConstraintHost is not { } host)
                    {
                        return ("объект 0 не получен", "объект 0 не получен", "нет", (double?)null,
                            (string?)null, (string?)null);
                    }

                    // Step 1 — the fixed dimension. The help gives it no Partner requirement, and the
                    // MEASURED stored form of a created one exposes its partner as an array; both a
                    // single object and an array are tried so the refusal is not read as a property of
                    // the type.
                    var fixedAttempt = CreateConstraint(host,
                        new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, 0,
                            new PartnerPick("seg", 1, 0, null, AsArray: true, SecondIndex: 2), 0),
                        BuildPartnerArray(objects, new PartnerPick("seg", 1, 0, null, AsArray: true, SecondIndex: 2)),
                        null, readNotes);

                    // Step 2 — the dimension with a variable, on the SAME object.
                    var variableAttempt = CreateConstraint(host,
                        new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable, 0, null, 0,
                            wanted, wanted.ToString(CultureInfo.InvariantCulture), variableName),
                        null, null, readNotes);

                    var rows = ReadConstraints(host, readNotes);
                    var storedRow = rows.FirstOrDefault(r => r.Contains("ksCDimWithVariable", StringComparison.Ordinal))
                                    ?? "запись ksCDimWithVariable в списке не найдена";

                    double? value = null;
                    string? expression = null;
                    string? variable = null;
                    try
                    {
                        var array = host.Constraints as Array;
                        if (array is not null)
                        {
                            foreach (var element in array)
                            {
                                if (element is KompasAPI7.IParametriticConstraint c
                                    && Api5.SafeInt(() => (int)c.ConstraintType) == 13)
                                {
                                    value = Api5.SafeDouble(() => c.Value);
                                    expression = Api5.SafeObject(() => c.Expression) as string;
                                    variable = Api5.SafeObject(() => c.Variable) as string;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        readNotes.Add("перечитывание размерной записи: " + ex.GetType().Name);
                    }

                    return (fixedAttempt.Summary, variableAttempt.Summary, storedRow, value, expression, variable);
                });

            step.Data["fixed_dim"] = fixedSummary;
            step.Data["dim_with_variable"] = variableSummary;
            step.Data["stored_row"] = stored;
            step.Data["value_read"] = Api5.Num(valueRead);
            step.Data["expression_read"] = expressionRead;
            step.Data["variable_read"] = variableRead;
            step.Data["value_wanted"] = Api5.Num(wanted);
            step.Data["variable_wanted"] = variableName;
            step.Data["notes"] = notes;

            step.Observe("ksCFixedDim: " + fixedSummary);
            step.Observe("ksCDimWithVariable: " + variableSummary);
            step.Observe("запись в списке: " + stored);
            foreach (var note in notes)
            {
                step.Observe("  · " + note);
            }

            if (valueRead is not null && Math.Abs(valueRead.Value - wanted) < 1e-9
                && string.Equals(variableRead, variableName, StringComparison.Ordinal))
            {
                step.Pass($"Размер с переменной создан, Value перечитан {Api5.Num(valueRead)} (задано " +
                          $"{Api5.Num(wanted)}), Variable перечитана '{variableRead}' — совпало.");
            }
            else
            {
                step.Unknown($"Перечитано Value={Api5.Num(valueRead)}, Expression={expressionRead ?? "null"}, " +
                             $"Variable={variableRead ?? "null"} при заданных {Api5.Num(wanted)} и " +
                             $"'{variableName}'. Расхождение названо, а не сглажено.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Размер с переменной не измерен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════ fixed dimension ladder ══

    /// <summary>The <c>ksCFixedDim</c> ladder. MEASURED: it answers <c>Create=False</c> for a segment
    /// object with both a single and an array partner, and <c>ksCDimWithVariable</c> refuses afterwards
    /// — which is exactly the documented precondition («Установить значение можно, если на объекте
    /// есть ограничение "Фиксированный размер"»). The refusal carries the core's own words in the
    /// <c>ErrorType</c> list («Нет информации о привязке»), so the placement is varied over the forms
    /// the help names rather than concluded from one call.</summary>
    private void FixedDimLadder()
    {
        var step = _report.Begin("K.6c", "SKC-02 — лестница постановок ksCFixedDim",
            "При каком наборе объекта и партнёров фиксированный размер создаётся, и что именно " +
            "отвечает ядро на каждый набор?");
        try
        {
            var results = new List<string>();

            var rungs = new (string Name, DocKind Doc, ObjectPick Object, PartnerPick? Partner)[]
            {
                ("rect_seg_no_partner", DocKind.Rectangle, new ObjectPick(0, 0), null),
                ("rect_seg_single_partner", DocKind.Rectangle, new ObjectPick(0, 0), new PartnerPick("seg", 1)),
                ("rect_seg_array_two_segments", DocKind.Rectangle, new ObjectPick(0, 0),
                    new PartnerPick("seg", 1, 0, null, AsArray: true, SecondIndex: 2)),
                ("rect_seg_array_self_and_top", DocKind.Rectangle, new ObjectPick(0, 0),
                    new PartnerPick("seg", 0, 0, null, AsArray: true, SecondIndex: 2)),
                ("point_pair_array", DocKind.Point, new ObjectPick(-1, 0, "Point", 0),
                    new PartnerPick("Point", 1, 0, null, AsArray: true)),
                ("point_on_segment_array", DocKind.Point, new ObjectPick(-1, 0, "Point", 0),
                    new PartnerPick("seg", 0, 0, null, AsArray: true, SecondIndex: 1)),
            };

            foreach (var (name, doc, pick, partner) in rungs)
            {
                // NO Value here. MEASURED on the first run: passing Value to ksCFixedDim made every
                // rung answer Create=False, and the conclusion "the type is not creatable" was a
                // defect of THIS instrument — the help says Value applies to ksCDimWithVariable alone.
                var attempt = Attempt(step, "fx-" + name, ksConstraintTypeEnum.ksCFixedDim, doc, pick, partner);
                results.Add($"{name} → {attempt.Summary}; в списке: {attempt.StoredSummary}");
                step.Data["fx_" + name] = attempt.Summary;
                step.Data["fx_" + name + "_stored"] = attempt.StoredSummary;
            }

            step.Data["attempts"] = results;
            foreach (var line in results)
            {
                step.Observe(line);
            }

            var created = rungs.Where(r => (step.Data["fx_" + r.Name] as string ?? string.Empty)
                    .Contains("Create=True", StringComparison.Ordinal)).Select(r => r.Name).ToList();
            step.Data["created"] = created;

            // The PAIR: the documented precondition of ksCDimWithVariable is an existing ksCFixedDim on
            // the same object, so the two are created in order and Value/Expression/Variable are read
            // back FROM THE STORED record.
            var pair = DimensionPair(step);
            step.Data["pair_fixed_dim"] = pair.FixedSummary;
            step.Data["pair_dim_with_variable"] = pair.VariableSummary;
            step.Data["pair_stored"] = pair.StoredRow;
            var matrix = FixedDimMatrix(step);
            step.Data["matrix"] = matrix;
            foreach (var line in matrix)
            {
                step.Observe(line);
            }

            step.Data["pair_value_read"] = Api5.Num(pair.ValueRead);
            step.Data["pair_expression_read"] = pair.ExpressionRead;
            step.Data["pair_variable_read"] = pair.VariableRead;
            step.Observe("пара: ksCFixedDim → " + pair.FixedSummary);
            step.Observe("пара: ksCDimWithVariable → " + pair.VariableSummary);
            step.Observe("пара: запись " + pair.StoredRow);

            step.Pass(created.Count > 0
                ? "Фиксированный размер создан на постановках: " + string.Join(", ", created)
                : "Ни одна из названных постановок фиксированного размера не создала. Отказ назван по " +
                  "каждой постановке отдельно; это измеренный факт, а не «не сработало».");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Лестница не выполнена: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════ dimension object route ══

    /// <summary>The CUSTOMER's route: <c>ksCFixedDim</c> and <c>ksCDimWithVariable</c> are applied to a
    /// DIMENSION OBJECT of the sketch, not to a segment.</summary>
    /// <remarks>DOC: «Документ 2D > Обозначения и размеры > Размеры > Коллекции размеров» —
    /// <c>isymbols2dcontainer.html</c> («Дополнительный интерфейс вида… можно получить у интерфейса
    /// вида IView посредством QueryInterface») → <c>ILineDimensions::Add()</c>
    /// (<c>ilinedimensions_add.html</c>) → <c>ILineDimension</c>, whose hierarchy is
    /// <c>IKompasAPIObject → IDrawingObject → ILineDimension</c> (<c>ilinedimension.html</c>), then
    /// <c>IModelObject::Update()</c> is required. <c>iparametriticconstraint_value.html</c> says the
    /// value can be set «если на объекте есть ограничение "Фиксированный размер" (ksCFixedDim)» — and
    /// that object is the dimension.
    /// History: docs/decisions/probes.md#sketch-constraints</remarks>
    private void DimensionRoute()
    {
        var step = _report.Begin("K.14", "Объект-размер: документированный маршрут и ограничения на нём",
            "Создаётся ли линейный размер через ISymbols2DContainer.LineDimensions.Add(), отвечает ли он " +
            "IDrawingObject1, и создаются ли на нём ksCFixedDim и ksCDimWithVariable?");
        try
        {
            if (!BuildRectangleSketch("k-dim", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();
            DimensionRouteOutcome? outcome = null;

            // The session is opened HERE and passed down: the dimension and the constraints that go on
            // it live in ONE edit session, and a nested BeginEdit() fails.
            KompasAPI7.IFragmentDocument? fragment;
            try
            {
                fragment = sketch.BeginEdit();
            }
            catch (COMException ex)
            {
                notes.Add($"BeginEdit отказал: HRESULT 0x{ex.HResult:X8}");
                fragment = null;
            }

            if (fragment is null)
            {
                notes.Add("ISketch.BeginEdit → null: вход в эскиз не состоялся");
            }
            else
            {
                try
                {
                    var created = AddLineDimension(fragment, notes, out var containerCount,
                        out var dimensionRef);
                    notes.Add($"размеров в коллекции: {containerCount}, ссылка размера {dimensionRef}");
                    if (created is { } pair && pair.Host is { } dimensionHost)
                    {
                        notes.Add("объект-размер отвечает IDrawingObject1: NewConstraint() доступен");

                        // The dimension must first become a FIXED dimension, then the variable one.
                        var fixedAttempt = CreateConstraint(dimensionHost,
                            new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                            null, null, notes);

                        var variableAttempt = CreateConstraint(dimensionHost,
                            new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable,
                                -1, null, -1, 40d, "40", "k_dim_40"),
                            null, null, notes);

                        var rows = ReadConstraints(dimensionHost, notes);
                        notes.Add($"в списке размера {rows.Count} записей: " + string.Join(" | ", rows));

                        var nominal = ReadNominalValue(pair.Dimension, notes);
                        notes.Add("номинал размера: " + Api5.Num(nominal));

                        outcome = new DimensionRouteOutcome(fixedAttempt.Summary, variableAttempt.Summary,
                            rows.Count, string.Join(" | ", rows), nominal);
                    }
                    else if (created is not null)
                    {
                        notes.Add("объект-размер НЕ отвечает IDrawingObject1");
                    }
                }
                finally
                {
                    try
                    {
                        sketch.EndEdit();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        notes.Add("EndEdit(): " + ex.GetType().Name);
                    }
                }
            }

            step.Data["notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (outcome is null)
            {
                step.Fail("Маршрут объекта-размера не пройден — подробности в notes.");
                return;
            }

            step.Data["fixed_dim"] = outcome.FixedSummary;
            step.Data["dim_with_variable"] = outcome.VariableSummary;
            step.Data["constraint_count"] = outcome.Count;
            step.Data["constraint_rows"] = outcome.Rows;
            step.Data["nominal_value"] = Api5.Num(outcome.Nominal);

            step.Observe("ksCFixedDim на объекте-размере: " + outcome.FixedSummary);
            step.Observe("ksCDimWithVariable на объекте-размере: " + outcome.VariableSummary);

            var fixedOk = outcome.FixedSummary.Contains("Create=True", StringComparison.Ordinal);
            var variableOk = outcome.VariableSummary.Contains("Create=True", StringComparison.Ordinal);
            if (fixedOk && variableOk)
            {
                step.Pass("Размер создан документированным маршрутом, и на нём созданы ОБА ограничения: " +
                          "ksCFixedDim и ksCDimWithVariable.");
            }
            else if (fixedOk)
            {
                step.Unknown("ksCFixedDim на объекте-размере создан, ksCDimWithVariable — нет: " +
                             outcome.VariableSummary);
            }
            else
            {
                step.Unknown("ksCFixedDim на объекте-размере НЕ создан: " + outcome.FixedSummary +
                             ". Маршрут пройден до создания размера (см. notes).");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут объекта-размера не пройден: " + ex.Message);
        }
    }

    /// <summary>The document that owns an API7 object: walking up <c>Parent</c> to
    /// <c>IKompasDocument</c>, NOT <c>IApplication.ActiveDocument</c> — "active" is a WINDOW state, and
    /// in an inactive document the address would be issued by a FOREIGN document.</summary>
    private static KompasAPI7.IKompasDocument? DocumentOf(KompasAPI7.IKompasAPIObject? start)
    {
        var node = start;
        for (var depth = 0; depth < 32 && node is not null; depth++)
        {
            if (node is KompasAPI7.IKompasDocument document)
            {
                return document;
            }

            try
            {
                node = node.Parent;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Outcome of the dimension route: what the two constraints answered, the re-read list and
    /// the dimension's nominal value.</summary>
    private sealed record DimensionRouteOutcome(
        string FixedSummary,
        string VariableSummary,
        int Count,
        string Rows,
        double? Nominal);

    /// <summary>Creates a LINE DIMENSION on the sketch by the documented route and returns it together
    /// with its <c>IDrawingObject1</c>.</summary>
    private (KompasAPI7.ILineDimension Dimension, KompasAPI7.IDrawingObject1? Host)? AddLineDimension(
        KompasAPI7.IFragmentDocument? fragment, List<string> notes, out int containerCount,
        out int dimensionRef)
    {
        containerCount = -1;
        dimensionRef = 0;

        // The session is ALREADY OPEN by the caller: a nested BeginEdit() fails, and a dimension is
        // created inside the same edit session as the constraints that will be put on it.
        try
        {
            var views = fragment?.ViewsAndLayersManager?.Views;
            var view = views?.get_View(0);
            if (view is null)
            {
                notes.Add("вид фрагмента не получен");
                return null;
            }

            if (view is not KompasAPI7.ISymbols2DContainer symbols)
            {
                notes.Add("вид НЕ отвечает QI(ISymbols2DContainer) — документированный маршрут недоступен");
                return null;
            }

            var dimensions = symbols.LineDimensions;
            containerCount = Api5.SafeInt(() => dimensions.Count) ?? -1;
            notes.Add("коллекция линейных размеров до Add(): " + Api5.Raw(containerCount));

            var dimension = dimensions.Add();
            if (dimension is null)
            {
                notes.Add("ILineDimensions.Add() → null");
                return null;
            }

            dimensionRef = Api5.SafeInt(() => dimension.Reference) ?? 0;

            // The dimension measures the distance between (X1,Y1) and (X2,Y2) — the help says
            // IDimensionText::NominalValue returns exactly that distance.
            dimension.X1 = -HalfA;
            dimension.Y1 = -HalfB;
            dimension.X2 = HalfA;
            dimension.Y2 = -HalfB;
            dimension.X3 = -HalfA;
            dimension.Y3 = -HalfB - 10d;
            dimension.ShelfX = 0d;
            dimension.ShelfY = -HalfB - 14d;

            var updated = Api5.SafeBool(() => dimension.Update());
            notes.Add("после задания координат ILineDimension.Update() → " + Api5.Raw(updated));
            notes.Add("коллекция после Add(): " + Api5.Raw(Api5.SafeInt(() => dimensions.Count)));

            var host = dimension as KompasAPI7.IDrawingObject1;
            return (dimension, host);
        }
        catch (Exception ex)
        {
            notes.Add("создание размера: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>The dimension's nominal value through <c>IDimensionText::NominalValue</c>
    /// (<c>idimensiontext_nominalvalue.html</c>) — an ADDITIONAL interface reached by QueryInterface.</summary>
    private static double? ReadNominalValue(KompasAPI7.ILineDimension dimension, List<string> notes)
    {
        try
        {
            if (dimension is KompasAPI7.IDimensionText text)
            {
                return text.NominalValue;
            }

            notes.Add("объект-размер не отвечает QI(IDimensionText)");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("номинал не прочитан: " + ex.GetType().Name);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════ dimension drives geometry ══

    /// <summary>The CUSTOMER's second requirement for the dimension: it must DRIVE the geometry — a
    /// changed value and a changed variable must MOVE it. Measured by re-reading the segment's own
    /// length through <c>ILineSegment.Length</c>, not by the dimension's own text.</summary>
    private void DimensionDrives()
    {
        var step = _report.Begin("K.14b", "Управляющий размер двигает геометрию",
            "Меняет ли заданное значение размера длину отрезка, и двигает ли её смена значения?");
        try
        {
            if (!BuildRectangleSketch("k-drive", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();
            double? lengthBefore = null;
            double? lengthAfterValue = null;
            string? fixedSummary = null;
            string? variableSummary = null;
            double? nominal = null;

            // The CUSTOMER's recipe: the dimension's variable is a PART variable (block G3), so it is
            // created FIRST. The help says an unknown name is refused («Недопустимое имя переменной»),
            // so a name that does not exist would make the measurement describe the name, not the route.
            string? variableName = null;
            try
            {
                if (_app.TransferInterface(_part!, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                    is KompasAPI7.IPart7 part7)
                {
                    var variable = part7.AddVariable("k_drive_50", 50d, "проба K: управляющий размер");
                    variableName = variable is null ? null : Api5.SafeObject(() => variable.Name) as string;
                    notes.Add("переменная детали создана: " + (variableName ?? "не прочитана") +
                              " = " + Api5.Num(Api5.SafeDouble(() => variable!.Value)));
                }
                else
                {
                    notes.Add("IPart7 не получен — переменная детали не создана");
                }
            }
            catch (Exception ex)
            {
                notes.Add("создание переменной детали: " + ex.GetType().Name + ": " + ex.Message);
            }

            KompasAPI7.IFragmentDocument? fragment = null;
            try
            {
                fragment = sketch.BeginEdit();
            }
            catch (COMException ex)
            {
                notes.Add($"BeginEdit отказал: 0x{ex.HResult:X8}");
            }

            if (fragment is not null)
            {
                try
                {
                    var objects = EnumerateInFragment(fragment, notes);
                    if (objects.Count > 0 && objects[0].Drawing is KompasAPI7.ILineSegment bottom)
                    {
                        lengthBefore = Api5.SafeDouble(() => bottom.Length);
                    }

                    var created = AddLineDimension(fragment, notes, out _, out _);
                    if (created is { } pair && pair.Host is { } host)
                    {
                        var fixedAttempt = CreateConstraint(host,
                            new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                            null, null, notes);
                        fixedSummary = fixedAttempt.Summary;

                        // 50 instead of the natural 40: a value that, if it takes, MUST move the segment.
                        var variableAttempt = CreateConstraint(host,
                            new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable,
                                -1, null, -1, 50d, "50", variableName ?? "k_drive_50"),
                            null, null, notes);
                        variableSummary = variableAttempt.Summary;

                        nominal = ReadNominalValue(pair.Dimension, notes);
                    }
                }
                finally
                {
                    try
                    {
                        sketch.EndEdit();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        notes.Add("EndEdit(): " + ex.GetType().Name);
                    }
                }
            }

            _doc!.RebuildDocument();

            var after = new List<string>();
            if (TransferSketch(_sketchEntity!, step) is KompasAPI7.ISketch again)
            {
                var readNotes = new List<string>();
                lengthAfterValue = InSession(again, write: false, readNotes, (objects, _) =>
                    objects.Count > 0 && objects[0].Drawing is KompasAPI7.ILineSegment bottom
                        ? Api5.SafeDouble(() => bottom.Length)
                        : null);
                after.AddRange(readNotes);
            }

            step.Data["segment_length_after_constraint"] = Api5.Num(lengthAfterValue);

            // SECOND WAY, and it is the DOCUMENTED one for the value: IDimensionText::NominalValue
            // («Позволяет получить и установить значение размера»). The constraint route did not move
            // the geometry, so the dimension's own value member is tried before anything is concluded.
            double? lengthAfterNominal = null;
            double? nominalAfterWrite = null;
            if (TransferSketch(_sketchEntity!, step) is KompasAPI7.ISketch forNominal)
            {
                var nominalNotes = new List<string>();
                KompasAPI7.IFragmentDocument? fragment2 = null;
                try
                {
                    fragment2 = forNominal.BeginEdit();
                }
                catch (COMException ex)
                {
                    nominalNotes.Add($"BeginEdit отказал: 0x{ex.HResult:X8}");
                }

                if (fragment2 is not null)
                {
                    try
                    {
                        var symbols = fragment2.ViewsAndLayersManager?.Views?.get_View(0)
                            as KompasAPI7.ISymbols2DContainer;
                        var dimensions = symbols?.LineDimensions;
                        var count = dimensions is null ? (int?)null : Api5.SafeInt(() => dimensions.Count);
                        nominalNotes.Add("размеров в коллекции: " + Api5.Raw(count));

                        var dimension = dimensions?.get_LineDimension(0);
                        if (dimension is KompasAPI7.IDimensionText text)
                        {
                            nominalNotes.Add("номинал до записи: " + Api5.Num(text.NominalValue));
                            text.NominalValue = 50d;
                            nominalAfterWrite = Api5.SafeDouble(() => text.NominalValue);
                            nominalNotes.Add("номинал после записи 50: " + Api5.Num(nominalAfterWrite));
                        }
                        else
                        {
                            nominalNotes.Add("размер не отвечает QI(IDimensionText)");
                        }

                        var updated = dimension is null ? (bool?)null : Api5.SafeBool(() => dimension.Update());
                        nominalNotes.Add("Update() после записи номинала → " + Api5.Raw(updated));

                        if (EnumerateInFragment(fragment2, nominalNotes) is { Count: > 0 } objs
                            && objs[0].Drawing is KompasAPI7.ILineSegment bottom)
                        {
                            lengthAfterNominal = Api5.SafeDouble(() => bottom.Length);
                            nominalNotes.Add("длина отрезка внутри сеанса: " + Api5.Num(lengthAfterNominal));
                        }
                    }
                    finally
                    {
                        try
                        {
                            forNominal.EndEdit();
                        }
                        catch (Exception ex) when (ex is COMException or InvalidCastException)
                        {
                            nominalNotes.Add("EndEdit(): " + ex.GetType().Name);
                        }
                    }
                }

                after.AddRange(nominalNotes);
            }

            _doc!.RebuildDocument();
            step.Data["nominal_after_write"] = Api5.Num(nominalAfterWrite);
            step.Data["segment_length_after_nominal"] = Api5.Num(lengthAfterNominal);

            step.Data["notes"] = notes.Concat(after).ToList();
            step.Data["fixed_dim"] = fixedSummary;
            step.Data["dim_with_variable"] = variableSummary;
            step.Data["nominal_value"] = Api5.Num(nominal);
            step.Data["segment_length_before"] = Api5.Num(lengthBefore);
            step.Data["segment_length_after"] = Api5.Num(lengthAfterValue);

            foreach (var note in notes.Concat(after))
            {
                step.Observe(note);
            }

            step.Observe($"длина отрезка до: {Api5.Num(lengthBefore)}, после задания значения 50: " +
                         $"{Api5.Num(lengthAfterValue)}");

            if (lengthBefore is null || lengthAfterValue is null)
            {
                step.Unknown("Длина отрезка не прочитана — контроль не различает исходы. Дефект прибора.");
                return;
            }

            var byConstraint = Math.Abs(lengthAfterValue.Value - 50d) < 1e-6;
            var byNominal = lengthAfterNominal is { } nominalLength
                            && Math.Abs(nominalLength - 50d) < 1e-6;
            var moved = Math.Abs(lengthAfterValue.Value - lengthBefore.Value) > 1e-6
                        || (lengthAfterNominal is { } nl && Math.Abs(nl - lengthBefore.Value) > 1e-6);

            if (byConstraint || byNominal)
            {
                step.Pass($"Размер УПРАВЛЯЕТ геометрией: длина отрезка {Api5.Num(lengthBefore)} → " +
                          $"{Api5.Num(byConstraint ? lengthAfterValue : lengthAfterNominal)} при заданном " +
                          $"значении 50 (способ: {(byConstraint ? "ограничение с переменной" : "NominalValue")}).");
            }
            else if (moved)
            {
                step.Unknown($"Длина изменилась, но не до заданного значения 50: через ограничение " +
                             $"{Api5.Num(lengthBefore)} → {Api5.Num(lengthAfterValue)}, через NominalValue " +
                             $"→ {Api5.Num(lengthAfterNominal)}. Измеренный факт, а не совпадение.");
            }
            else
            {
                step.Unknown($"Длина НЕ изменилась ({Api5.Num(lengthBefore)} → " +
                             $"{Api5.Num(lengthAfterValue)}) ни через ограничение с переменной, ни через " +
                             $"IDimensionText.NominalValue ({Api5.Num(lengthAfterNominal)}): размер создан, " +
                             "но геометрией не управляет. Это измеренный факт, и он выносится заказчику.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Управление геометрией не измерено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════ fully defined survives reopen ══

    /// <summary>The requirement's own wording: state <c>1</c> read AFTER <c>save → close → reopen</c>.
    /// </summary>
    private void FullyDefinedReopen()
    {
        var step = _report.Begin("K.16", "Состояние 1 переживает save → close → reopen",
            "Читается ли состояние 1 у того же эскиза после сохранения, закрытия и переоткрытия?");
        try
        {
            if (!BuildRectangleSketch("k-reopen-full", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var built = FullContourState(step, "k-reopen-full");
            step.Data["state_before_save"] = built;
            step.Observe("состояние до сохранения: " + Api5.Raw(built));

            var path = Path.Combine(_options.WorkDir, "k-fully-defined-reopen.m3d");
            if (_doc!.SaveAs(path) != true)
            {
                step.Unknown("SaveAs не подтверждён — переоткрытие не проверено.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc!.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(path, true) != true)
            {
                step.Unknown("Open переоткрытого документа не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            if (!FindSketchInReopened(step))
            {
                step.Unknown("Эскиз в переоткрытом документе не найден — состояние не перечитано.");
                return;
            }

            var reopened = StateOfCurrent(step, "k_reopen_full_state");
            var again = StateOfCurrent(step, "k_reopen_full_state2");
            step.Data["state_after_reopen"] = reopened;
            step.Data["stable_across_reads"] = reopened == again;

            if (built == 1 && reopened == 1)
            {
                step.Pass("Состояние 1 получено И ПРОЧИТАНО ПОСЛЕ save → close → reopen — как и требует " +
                          "строка SKC-05.");
            }
            else
            {
                step.Unknown($"До сохранения {Api5.Raw(built)}, после переоткрытия {Api5.Raw(reopened)}: " +
                             "требование «состояние 1 после переоткрытия» этой постановкой не подтверждено.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Переоткрытие не измерено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════ associative dimension ══

    /// <summary>The CUSTOMER's recipe, and it is the documented one: the dimension is bound to the
    /// segment ASSOCIATIVELY (<c>ksCAssociation</c>, type 12 — «Ассоциативная связь»,
    /// <c>ksconstrainttypeenum.html</c>), then <c>ksCFixedDim</c>, then <c>ksCDimWithVariable</c> with
    /// the value. DOC: <c>iparametriticconstraint_partner.html</c> — «SAFEARRAY используется только для
    /// ограничения "Ассоциативная связь"(ksCAssociation). Например, для углового размера
    /// ассоциативная связь может быть установлена на два отрезка, между которыми установлен размер».</summary>
    /// <remarks>MEASURED in four readings, so that a negative answer names WHERE it stopped:
    /// the segment length after the association, after the fixed dimension, after the value, and after
    /// the VARIABLE was changed — plus the same after <c>save → close → reopen</c>.
    /// History: docs/decisions/probes.md#sketch-constraints</remarks>
    private void AssociationRoute()
    {
        var step = _report.Begin("K.17", "Ассоциативный размер: привязка к отрезку и управление геометрией",
            "ksCAssociation на объекте-размере (партнёр — массив отрезков), затем ksCFixedDim, затем " +
            "ksCDimWithVariable со значением 50: становится ли длина отрезка 50, переживает ли " +
            "save → close → reopen, и двигает ли геометрию смена переменной?");
        try
        {
            if (!BuildRectangleSketch("k-assoc", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();

            // NO PRE-CREATED VARIABLE, and this is MEASURED, not a simplification: with a part variable
            // of that name ALREADY in the document the kernel refuses ksCDimWithVariable
            // (Create=False), while the same call WITHOUT the pre-created name is accepted and the
            // kernel assigns its own variable (measured: `v9`). The variable's name is therefore READ
            // BACK from the stored constraint and only then changed.
            string? variableName = null;

            var readings = new Dictionary<string, string>(StringComparer.Ordinal);

            double? ReadLength(string tag)
            {
                if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch forRead)
                {
                    readings[tag] = "эскиз не перенесён";
                    return null;
                }

                var readNotes = new List<string>();
                var length = InSession(forRead, write: false, readNotes, (objects, _) =>
                    objects.Count > 0 && objects[0].Drawing is KompasAPI7.ILineSegment bottom
                        ? Api5.SafeDouble(() => bottom.Length)
                        : null);
                readings[tag] = Api5.Num(length);
                return length;
            }

            var before = ReadLength("до");

            KompasAPI7.IFragmentDocument? fragment = null;
            try
            {
                fragment = sketch.BeginEdit();
            }
            catch (COMException ex)
            {
                notes.Add($"BeginEdit отказал: 0x{ex.HResult:X8}");
            }

            string? association = null;
            string? fixedDim = null;
            string? variableDim = null;
            if (fragment is not null)
            {
                try
                {
                    var objects = EnumerateInFragment(fragment, notes);
                    if (objects.Count == 0 || objects[0].ConstraintHost is not { } segmentHost)
                    {
                        notes.Add("отрезок не получен — ассоциацию ставить не на что");
                    }
                    else
                    {
                        var created = AddLineDimension(fragment, notes, out _, out _);
                        if (created is { } pair && pair.Host is { } dimensionHost)
                        {
                            // 1. ASSOCIATION: the partner is the SAFEARRAY form, and the partner IS the
                            //    segment the dimension measures.
                            var associationAttempt = CreateConstraint(dimensionHost,
                                new Placement("association", ksConstraintTypeEnum.ksCAssociation, -1,
                                    new PartnerPick("seg", 0, -1), -1),
                                new object[] { segmentHost }, null, notes);
                            association = associationAttempt.Summary;

                            // 2. The fixed dimension, then the variable one.
                            var fixedAttempt = CreateConstraint(dimensionHost,
                                new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                                null, null, notes);
                            fixedDim = fixedAttempt.Summary;

                            var variableAttempt = CreateConstraint(dimensionHost,
                                new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable,
                                    -1, null, -1, 50d, "50", variableName ?? "k_assoc_50"),
                                null, null, notes);
                            variableDim = variableAttempt.Summary;

                            var rows = ReadConstraints(dimensionHost, notes);
                            notes.Add("список размера: " + string.Join(" | ", rows));

                            // The variable the KERNEL assigned is read back from the stored record.
                            try
                            {
                                if (dimensionHost.Constraints is Array array)
                                {
                                    foreach (var element in array)
                                    {
                                        if (element is KompasAPI7.IParametriticConstraint c
                                            && Api5.SafeInt(() => (int)c.ConstraintType) == 13)
                                        {
                                            variableName = Api5.SafeObject(() => c.Variable) as string;
                                            notes.Add("переменная, назначенная ядром: " +
                                                      (variableName ?? "не прочитана"));
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                notes.Add("чтение имени переменной: " + ex.GetType().Name);
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        sketch.EndEdit();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        notes.Add("EndEdit(): " + ex.GetType().Name);
                    }
                }
            }

            _doc!.RebuildDocument();
            var afterValue = ReadLength("после значения 50");

            // 3. The VARIABLE changes — the geometry must follow. The route is the one block G3 proved:
            //    API5 ksPart.VariableCollection() → GetByName → ksVariable, then the DOCUMENT rebuild
            //    (ksDocument3D.RebuildDocument()), which is the call that carries the change into the
            //    model; ksPart.RebuildModel() does NOT move variable-driven geometry on this build.
            double? afterVariable = null;
            var variableNotes = new List<string>();
            if (variableName is null)
            {
                variableNotes.Add("имя переменной не прочитано — смену выполнить нечем");
            }
            else if (_part is null)
            {
                variableNotes.Add("деталь недоступна — смену выполнить нечем");
            }
            else
            {
                ksVariable? target = null;
                try
                {
                    if (_part.VariableCollection() is ksVariableCollection collection)
                    {
                        // `refresh()` is called first: the collection is a VIEW of the document's
                        // variables, and a variable added by the kernel during this session may not be
                        // in that view yet. Without it an empty count would be read as "there is no such
                        // variable", which is a different statement.
                        variableNotes.Add("refresh() коллекции → " + Api5.Raw(Api5.SafeBool(() => collection.refresh())));
                        var count = Api5.SafeInt(() => collection.GetCount());
                        variableNotes.Add("переменных в коллекции детали: " + Api5.Raw(count));
                        for (var i = 0; i < (count ?? 0); i++)
                        {
                            if (collection.GetByIndex(i) is ksVariable candidate)
                            {
                                var name = Api5.SafeObject(() => candidate.name) as string;
                                variableNotes.Add($"  [{i}] '{name}' = " +
                                                  Api5.Num(Api5.SafeDouble(() => candidate.value)));
                                if (string.Equals(name, variableName, StringComparison.Ordinal))
                                {
                                    target = candidate;
                                }
                            }
                        }

                        target ??= collection.GetByName(variableName, true, false) as ksVariable;
                    }
                    else
                    {
                        variableNotes.Add("VariableCollection() не вернул ksVariableCollection");
                    }
                }
                catch (Exception ex)
                {
                    variableNotes.Add("поиск переменной: " + ex.GetType().Name + ": " + ex.Message);
                }

                if (target is null)
                {
                    variableNotes.Add($"переменная '{variableName}' в коллекции детали НЕ найдена — " +
                                      "сменить нечего, и это названная причина, а не «не прочитано»");
                }
                else
                {
                    try
                    {
                        target.Expression = "60";
                        target.value = 60d;
                        variableNotes.Add("переменная '" + variableName + "' изменена: Expression=" +
                                          Api5.SafeObject(() => target.Expression) + ", value=" +
                                          Api5.Num(Api5.SafeDouble(() => target.value)));
                    }
                    catch (Exception ex)
                    {
                        variableNotes.Add("запись переменной: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    try
                    {
                        var rebuilt = _doc!.RebuildDocument();
                        variableNotes.Add("RebuildDocument() после смены переменной → " + Api5.Raw(rebuilt));
                    }
                    catch (Exception ex)
                    {
                        variableNotes.Add("RebuildDocument(): " + ex.GetType().Name);
                    }

                    afterVariable = ReadLength("после смены переменной на 60");
                }
            }

            notes.AddRange(variableNotes);

            // 4. save → close → reopen.
            double? afterReopen = null;
            var path = Path.Combine(_options.WorkDir, "k-assoc.m3d");
            if (_doc.SaveAs(path) == true)
            {
                step.Data["closed"] = Api5.Raw(TryBool(() => _doc!.close()));
                _doc = (ksDocument3D)_app.Document3D();
                if (_doc.Open(path, true) == true)
                {
                    _part = (ksPart)_doc.GetPart(-1);
                    if (FindSketchInReopened(step))
                    {
                        afterReopen = ReadLength("после переоткрытия");
                    }
                    else
                    {
                        readings["после переоткрытия"] = "эскиз не найден";
                    }
                }
                else
                {
                    readings["после переоткрытия"] = "документ не открылся";
                }
            }
            else
            {
                readings["после переоткрытия"] = "SaveAs не подтверждён";
            }

            step.Data["notes"] = notes;
            step.Data["association"] = association;
            step.Data["fixed_dim"] = fixedDim;
            step.Data["dim_with_variable"] = variableDim;
            step.Data["readings"] = readings;
            step.Data["length_before"] = Api5.Num(before);
            step.Data["length_after_value"] = Api5.Num(afterValue);
            step.Data["length_after_variable"] = Api5.Num(afterVariable);
            step.Data["length_after_reopen"] = Api5.Num(afterReopen);

            foreach (var note in notes)
            {
                step.Observe(note);
            }

            step.Observe("ksCAssociation: " + association);
            step.Observe("ksCFixedDim: " + fixedDim);
            step.Observe("ksCDimWithVariable: " + variableDim);
            foreach (var (tag, value) in readings)
            {
                step.Observe($"длина отрезка {tag}: {value}");
            }

            var driven = afterValue is { } v && Math.Abs(v - 50d) < 1e-6;
            var variableDrives = afterVariable is { } w && Math.Abs(w - 60d) < 1e-6;
            var survives = afterReopen is { } r && Math.Abs(r - (afterVariable ?? afterValue ?? 0d)) < 1e-6;

            if (driven && variableDrives && survives)
            {
                step.Pass("Ассоциативный размер УПРАВЛЯЕТ геометрией: длина 40 → 50 после значения, " +
                          "50 → 60 после смены переменной, и значение пережило save → close → reopen.");
            }
            else
            {
                step.Unknown("Требование НЕ подтверждено целиком. Измерено: длина " +
                             $"до {Api5.Num(before)}, после значения 50 → {Api5.Num(afterValue)}, " +
                             $"после смены переменной на 60 → {Api5.Num(afterVariable)}, после переоткрытия " +
                             $"→ {Api5.Num(afterReopen)}. Это измеренный факт, и он выносится заказчику.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Ассоциативный маршрут не измерен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════ variable dimension ladder ══

    /// <summary>Where exactly the customer's recipe stops. MEASURED in <c>K.17</c>: the association is
    /// accepted (<c>Create=True</c>), <c>ksCFixedDim</c> is accepted AND VALID (<c>Create=True,
    /// Valid=True</c>) — for the first time in this whole block — and <c>ksCDimWithVariable</c> is then
    /// REFUSED. So the third step is varied over the forms the help names (value only, expression,
    /// variable, nothing) on a FRESH dimension each time, because constraints accumulate on the object.
    /// History: docs/decisions/probes.md#sketch-constraints</summary>
    private void VariableDimLadder()
    {
        var step = _report.Begin("K.17b", "ksCDimWithVariable после ассоциации и фиксированного размера",
            "Какая форма ksCDimWithVariable принимается на ассоциированном размере, и что отвечает ядро " +
            "на каждую форму?");
        try
        {
            var rungs = new (string Name, double? Value, string? Expression, string? Variable)[]
            {
                ("value_only", 50d, null, null),
                ("value_and_expression", 50d, "50", null),
                ("value_and_variable", 50d, null, "k_assoc_50"),
                ("all_three", 50d, "50", "k_assoc_50"),
                ("nothing", null, null, null),
            };

            var results = new List<string>();
            foreach (var (name, value, expression, variable) in rungs)
            {
                var name2 = "k-ladder-" + name;
                if (!BuildRectangleSketch(name2, step, out _))
                {
                    results.Add(name + " → документ не построен");
                    continue;
                }

                if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
                {
                    results.Add(name + " → эскиз не перенесён");
                    continue;
                }

                var notes = new List<string>();
                string? association = null;
                string? fixedDim = null;
                string? variableDim = null;
                string? rows = null;

                KompasAPI7.IFragmentDocument? fragment = null;
                try
                {
                    fragment = sketch.BeginEdit();
                }
                catch (COMException ex)
                {
                    notes.Add($"BeginEdit: 0x{ex.HResult:X8}");
                }

                if (fragment is not null)
                {
                    try
                    {
                        var objects = EnumerateInFragment(fragment, notes);
                        if (objects.Count > 0 && objects[0].ConstraintHost is { } segmentHost)
                        {
                            var created = AddLineDimension(fragment, notes, out _, out _);
                            if (created is { } pair && pair.Host is { } dimensionHost)
                            {
                                association = CreateConstraint(dimensionHost,
                                    new Placement("association", ksConstraintTypeEnum.ksCAssociation, -1,
                                        new PartnerPick("seg", 0, -1), -1),
                                    new object[] { segmentHost }, null, notes).Summary;

                                fixedDim = CreateConstraint(dimensionHost,
                                    new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                                    null, null, notes).Summary;

                                variableDim = CreateConstraint(dimensionHost,
                                    new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable,
                                        -1, null, -1, value, expression, variable),
                                    null, null, notes).Summary;

                                rows = string.Join(" | ", ReadConstraints(dimensionHost, notes));
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            sketch.EndEdit();
                        }
                        catch (Exception ex) when (ex is COMException or InvalidCastException)
                        {
                            notes.Add("EndEdit(): " + ex.GetType().Name);
                        }
                    }
                }

                // THE DECISIVE NUMBER: does the geometry follow the value? Measured for EVERY rung,
                // because "the constraint was accepted" and "the dimension drives" are different claims.
                _doc!.RebuildDocument();
                double? lengthAfter = null;
                if (TransferSketch(_sketchEntity!, step) is KompasAPI7.ISketch forRead)
                {
                    var readNotes = new List<string>();
                    lengthAfter = InSession(forRead, write: false, readNotes, (objects, _) =>
                        objects.Count > 0 && objects[0].Drawing is KompasAPI7.ILineSegment bottom
                            ? Api5.SafeDouble(() => bottom.Length)
                            : null);
                    notes.AddRange(readNotes);
                }

                results.Add($"{name} → ассоциация: {association}; фиксированный: {fixedDim}; " +
                            $"с переменной: {variableDim}; длина отрезка {Api5.Num(lengthAfter)}");
                step.Data["ladder_" + name] = new Dictionary<string, object?>
                {
                    ["association"] = association,
                    ["fixed_dim"] = fixedDim,
                    ["dim_with_variable"] = variableDim,
                    ["length_after"] = Api5.Num(lengthAfter),
                    ["rows"] = rows,
                    ["notes"] = notes.ToArray(),
                };
            }

            step.Data["attempts"] = results;
            foreach (var line in results)
            {
                step.Observe(line);
            }

            var accepted = rungs.Where(r => (step.Data["ladder_" + r.Name] as Dictionary<string, object?>)
                    ?["dim_with_variable"] is string s2 && s2.Contains("Create=True", StringComparison.Ordinal))
                .Select(r => r.Name).ToList();
            step.Data["accepted"] = accepted;

            var drivenRungs = rungs.Where(r =>
            {
                var data = step.Data["ladder_" + r.Name] as Dictionary<string, object?>;
                return data is not null && data["length_after"] is "50";
            }).Select(r => r.Name).ToList();
            step.Data["driven"] = drivenRungs;

            if (drivenRungs.Count > 0)
            {
                step.Pass("Размер УПРАВЛЯЕТ геометрией: длина отрезка стала 50 в формах " +
                          string.Join(", ", drivenRungs) + " (приняты все формы: " +
                          string.Join(", ", accepted) + ").");
            }
            else
            {
                step.Unknown("ksCDimWithVariable принимается (формы: " + string.Join(", ", accepted) +
                             "), но длина отрезка НЕ стала 50 ни в одной форме. Это измеренный факт, " +
                             "и он выносится заказчику.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Лестница не выполнена: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════ sketch feature variable (G3 route) ══

    /// <summary>The dimension's variable is a variable of the SKETCH FEATURE, the same way an
    /// extrusion's variable belongs to its feature. So the G3 route is applied to the SKETCH:
    /// <c>ksEntity.GetFeature()</c> → <c>ksFeature.VariableCollection()</c>
    /// (<c>ksfeature_variablecollection.html</c>), then the parameter's expression is bound to a PART
    /// variable, the part variable is changed, the document is rebuilt, and the length is re-read.
    /// </summary>
    /// <remarks>DOC: «Изменения в полученном массиве не отображаются в модели немедленно. Чтобы
    /// изменения вступили в силу, необходимо вызвать метод ksPart::RebuildModel».
    /// History: docs/decisions/probes.md#sketch-constraints</remarks>
    private void SketchFeatureVariable()
    {
        var step = _report.Begin("K.18", "Переменная размера — переменная ПРИЗНАКА ЭСКИЗА: привязка к переменной детали",
            "Видна ли переменная ассоциативного размера в ksFeature.VariableCollection признака эскиза, " +
            "привязывается ли её выражение к переменной детали, и двигает ли смена этой переменной " +
            "длину отрезка?");
        try
        {
            if (!BuildRectangleSketch("k-featvar", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();
            string? dimensionVariable = null;

            // 1. The recipe, on the dimension object.
            KompasAPI7.IFragmentDocument? fragment = null;
            try
            {
                fragment = sketch.BeginEdit();
            }
            catch (COMException ex)
            {
                notes.Add($"BeginEdit: 0x{ex.HResult:X8}");
            }

            if (fragment is not null)
            {
                try
                {
                    var objects = EnumerateInFragment(fragment, notes);
                    if (objects.Count > 0 && objects[0].ConstraintHost is { } segmentHost)
                    {
                        var created = AddLineDimension(fragment, notes, out _, out _);
                        if (created is { } pair && pair.Host is { } dimensionHost)
                        {
                            notes.Add("ассоциация: " + CreateConstraint(dimensionHost,
                                new Placement("association", ksConstraintTypeEnum.ksCAssociation, -1,
                                    new PartnerPick("seg", 0, -1), -1),
                                new object[] { segmentHost }, null, notes).Summary);
                            notes.Add("фиксированный: " + CreateConstraint(dimensionHost,
                                new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                                null, null, notes).Summary);
                            notes.Add("с переменной: " + CreateConstraint(dimensionHost,
                                new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable,
                                    -1, null, -1, 50d, "50", null),
                                null, null, notes).Summary);

                            try
                            {
                                if (dimensionHost.Constraints is Array array)
                                {
                                    foreach (var element in array)
                                    {
                                        if (element is KompasAPI7.IParametriticConstraint c
                                            && Api5.SafeInt(() => (int)c.ConstraintType) == 13)
                                        {
                                            dimensionVariable = Api5.SafeObject(() => c.Variable) as string;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                notes.Add("чтение имени переменной: " + ex.GetType().Name);
                            }

                            notes.Add("переменная размера: " + (dimensionVariable ?? "не прочитана"));
                        }
                    }
                }
                finally
                {
                    try
                    {
                        sketch.EndEdit();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        notes.Add("EndEdit(): " + ex.GetType().Name);
                    }
                }
            }

            _doc!.RebuildDocument();
            var lengthAfterValue = ReadLength(step, notes, "после значения 50");

            // 2. The SKETCH FEATURE and its own variable collection.
            var featureNotes = new List<string>();
            ksFeature? feature = null;
            try
            {
                feature = _sketchEntity?.GetFeature() as ksFeature;
                featureNotes.Add("ksEntity.GetFeature() → " + (feature is null ? "null" : "ksFeature"));
            }
            catch (Exception ex)
            {
                featureNotes.Add("GetFeature(): " + ex.GetType().Name + ": " + ex.Message);
            }

            ksVariableCollection? featureVariables = null;
            var featureNames = new List<string>();
            if (feature is not null)
            {
                try
                {
                    featureVariables = feature.VariableCollection as ksVariableCollection;
                    featureNotes.Add("ksFeature.VariableCollection() → " +
                                     (featureVariables is null ? "null" : "ksVariableCollection"));
                }
                catch (Exception ex)
                {
                    featureNotes.Add("VariableCollection(): " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (featureVariables is not null)
            {
                var count = Api5.SafeInt(() => featureVariables.GetCount());
                featureNotes.Add("переменных у признака ЭСКИЗА: " + Api5.Raw(count));
                for (var i = 0; i < (count ?? 0); i++)
                {
                    if (featureVariables.GetByIndex(i) is ksVariable variable)
                    {
                        var name = Api5.SafeObject(() => variable.name) as string;
                        featureNames.Add(name ?? "(без имени)");
                        featureNotes.Add($"  [{i}] '{name}' = " +
                                         Api5.Num(Api5.SafeDouble(() => variable.value)) +
                                         ", Expression='" +
                                         (Api5.SafeObject(() => variable.Expression) as string ?? "") + "'");
                    }
                }
            }

            step.Data["feature_notes"] = featureNotes;
            foreach (var note in featureNotes)
            {
                step.Observe(note);
            }

            step.Data["dimension_variable"] = dimensionVariable;
            step.Data["feature_variable_names"] = featureNames;
            step.Data["variable_visible_on_feature"] = dimensionVariable is not null
                && featureNames.Contains(dimensionVariable, StringComparer.Ordinal);
            step.Data["length_after_value"] = Api5.Num(lengthAfterValue);

            if (dimensionVariable is null || !featureNames.Contains(dimensionVariable, StringComparer.Ordinal))
            {
                step.Data["notes"] = notes.Concat(featureNotes).ToList();
                step.Unknown("Переменная размера (" + (dimensionVariable ?? "не прочитана") +
                             ") в коллекции переменных ПРИЗНАКА ЭСКИЗА не видна (видно: " +
                             (featureNames.Count == 0 ? "ничего" : string.Join(", ", featureNames)) +
                             "). Привязка невозможна — это названная причина, а не «не прочитано».");
                return;
            }

            if (featureVariables is null)
            {
                step.Data["notes"] = notes.Concat(featureNotes).ToList();
                step.Unknown("Коллекции переменных признака нет — привязывать нечего.");
                return;
            }

            // 3. Bind the parameter's expression to a PART variable, change it, rebuild, re-read.
            string? boundValue = null;
            double? lengthAfterBind = null;
            double? lengthAfterPartChange = null;
            double? lengthAfterReopen = null;

            // The part variable is created through the API5 COLLECTION — the same one it will later be
            // read from and changed in. MEASURED: a variable created through the API7 route
            // (IPart7.AddVariable) is NOT visible in ksPart.VariableCollection() afterwards, and the
            // "not found" would have been a statement about the instrument, not about the binding.
            var partVariableName = "L_sketch";
            try
            {
                // The route is the one block G3 PROVED: IPart7.AddVariable. The API5 collection's own
                // AddNewVariable was tried first and MEASURED to leave the collection empty after a
                // refresh — the variable never reached the document, and that is an instrument
                // statement, not a property of the binding.
                if (_app.TransferInterface(_part!, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                    is KompasAPI7.IPart7 part7)
                {
                    var created = part7.AddVariable(partVariableName, 50d, "проба K: переменная детали");
                    notes.Add("переменная детали создана через IPart7.AddVariable: " + partVariableName +
                              " = " + Api5.Num(Api5.SafeDouble(() => created!.Value)));
                    // The document rebuild is what makes the created variable visible in the part's
                    // collection — MEASURED: without it the very next refresh() answers count=0, and
                    // "not found" would have been a statement about the instrument.
                    try
                    {
                        notes.Add("RebuildDocument() после создания переменной → " + Api5.Raw(_doc!.RebuildDocument()));
                    }
                    catch (Exception ex)
                    {
                        notes.Add("RebuildDocument(): " + ex.GetType().Name);
                    }

                    notes.Add(ListPartVariables("после создания переменной детали и перестроения"));
                }
                else
                {
                    notes.Add("ksPart.VariableCollection() не дал ksVariableCollection");
                }
            }
            catch (Exception ex)
            {
                notes.Add("переменная детали: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                if (featureVariables.GetByName(dimensionVariable, true, false) is ksVariable target)
                {
                    target.Expression = partVariableName;
                    boundValue = Api5.SafeObject(() => target.Expression) as string;
                    notes.Add($"выражение параметра привязано: Expression = '{boundValue}'");
                }
                else
                {
                    notes.Add("переменная признака не найдена по имени — привязка не выполнена");
                }
            }
            catch (Exception ex)
            {
                notes.Add("привязка: " + ex.GetType().Name + ": " + ex.Message);
            }

            // The help names ksPart::RebuildModel; G3 measured that RebuildDocument is the call that
            // carries it. Both are called, and each result is recorded separately.
            try
            {
                notes.Add("ksPart.RebuildModel() → " + Api5.Raw(Api5.SafeBool(() => _part!.RebuildModel())));
            }
            catch (Exception ex)
            {
                notes.Add("RebuildModel(): " + ex.GetType().Name);
            }

            try
            {
                notes.Add("ksDocument3D.RebuildDocument() → " + Api5.Raw(_doc.RebuildDocument()));
            }
            catch (Exception ex)
            {
                notes.Add("RebuildDocument(): " + ex.GetType().Name);
            }

            notes.Add(ListPartVariables("после привязки выражения"));
            lengthAfterBind = ReadLength(step, notes, "после привязки к переменной детали");
            notes.Add(ListPartVariables("после перестроения"));

            // 4. Change the PART variable and rebuild.
            try
            {
                // `refresh()` before the lookup, for the same reason as at creation: the collection is
                // a VIEW, and after a rebuild it does not show what was added in this session.
                if (_part!.VariableCollection() is ksVariableCollection partVariables2)
                {
                    partVariables2.refresh();
                }

                if (_part.VariableCollection() is ksVariableCollection partVariables3
                    && partVariables3.GetByName(partVariableName, true, false) is ksVariable l2)
                {
                    l2.value = 60d;
                    l2.Expression = "60";
                    notes.Add("переменная детали изменена: value = " +
                              Api5.Num(Api5.SafeDouble(() => l2.value)) + ", Expression = '" +
                              (Api5.SafeObject(() => l2.Expression) as string ?? "") + "'");
                }
                else
                {
                    var seen = new List<string>();
                    try
                    {
                        if (_part.VariableCollection() is ksVariableCollection again2)
                        {
                            var n = Api5.SafeInt(() => again2.GetCount()) ?? 0;
                            for (var i = 0; i < n; i++)
                            {
                                if (again2.GetByIndex(i) is ksVariable v)
                                {
                                    seen.Add((Api5.SafeObject(() => v.name) as string ?? "?") + "=" +
                                             Api5.Num(Api5.SafeDouble(() => v.value)));
                                }
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        seen.Add("список не прочитан: " + ex2.GetType().Name);
                    }

                    notes.Add("переменная детали '" + partVariableName + "' НЕ найдена; в коллекции: " +
                              (seen.Count == 0 ? "пусто" : string.Join(", ", seen)));
                }
            }
            catch (Exception ex)
            {
                notes.Add("смена переменной детали: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                _part!.RebuildModel();
                _doc.RebuildDocument();
            }
            catch (Exception ex)
            {
                notes.Add("перестроение после смены: " + ex.GetType().Name);
            }

            lengthAfterPartChange = ReadLength(step, notes, "после смены переменной детали на 60");

            // 5. save → close → reopen.
            var path = Path.Combine(_options.WorkDir, "k-featvar.m3d");
            if (_doc.SaveAs(path) == true)
            {
                TryBool(() => _doc!.close());
                _doc = (ksDocument3D)_app.Document3D();
                if (_doc.Open(path, true) == true)
                {
                    _part = (ksPart)_doc.GetPart(-1);
                    if (FindSketchInReopened(step))
                    {
                        lengthAfterReopen = ReadLength(step, notes, "после переоткрытия");
                    }
                    else
                    {
                        notes.Add("эскиз в переоткрытом документе не найден");
                    }
                }
                else
                {
                    notes.Add("переоткрытый документ не открылся");
                }
            }
            else
            {
                notes.Add("SaveAs не подтверждён");
            }

            step.Data["notes"] = notes.Concat(featureNotes).ToList();
            step.Data["bound_expression"] = boundValue;
            step.Data["length_after_bind"] = Api5.Num(lengthAfterBind);
            step.Data["length_after_part_variable"] = Api5.Num(lengthAfterPartChange);
            step.Data["length_after_reopen"] = Api5.Num(lengthAfterReopen);

            foreach (var note in notes)
            {
                step.Observe(note);
            }

            step.Observe($"длина: после значения 50 → {Api5.Num(lengthAfterValue)}; после привязки → " +
                         $"{Api5.Num(lengthAfterBind)}; после переменной детали 60 → " +
                         $"{Api5.Num(lengthAfterPartChange)}; после переоткрытия → {Api5.Num(lengthAfterReopen)}");

            if (lengthAfterPartChange is { } changed && Math.Abs(changed - 60d) < 1e-6)
            {
                step.Pass("Смена переменной ДЕТАЛИ двигает геометрию: длина отрезка стала 60 после " +
                          "привязки выражения параметра эскиза к переменной детали и перестроения" +
                          (lengthAfterReopen is { } r && Math.Abs(r - 60d) < 1e-6
                              ? "; 60 читается и после save → close → reopen."
                              : "; после переоткрытия " + Api5.Num(lengthAfterReopen) + "."));
            }
            else
            {
                step.Unknown("Привязка выполнена, но длина отрезка не стала 60: после привязки " +
                             $"{Api5.Num(lengthAfterBind)}, после переменной детали 60 → " +
                             $"{Api5.Num(lengthAfterPartChange)}, после переоткрытия " +
                             $"{Api5.Num(lengthAfterReopen)}. Это измеренный факт — стоп, решение за заказчиком.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут признака эскиза не измерен: " + ex.Message);
        }
    }

    /// <summary>The part's variable collection at a named moment: name=value for each variable, or the
    /// reason it could not be read. Used to say exactly WHEN a variable disappears, instead of guessing.
    /// </summary>
    private string ListPartVariables(string moment)
    {
        try
        {
            // The part is re-fetched from the DOCUMENT every time: after a rebuild the wrapper held by
            // the probe may be stale, and a stale wrapper would answer "empty" about a document that is
            // not empty — an instrument statement, not a fact about the variable.
            if (_doc is not null)
            {
                _part = (ksPart)_doc.GetPart(-1);
            }

            if (_part?.VariableCollection() is not ksVariableCollection collection)
            {
                return $"{moment}: коллекция не получена";
            }

            collection.refresh();
            var count = Api5.SafeInt(() => collection.GetCount());
            var items = new List<string>();
            for (var i = 0; i < (count ?? 0); i++)
            {
                if (collection.GetByIndex(i) is ksVariable v)
                {
                    items.Add((Api5.SafeObject(() => v.name) as string ?? "?") + "=" +
                              Api5.Num(Api5.SafeDouble(() => v.value)));
                }
            }

            return $"{moment}: count={Api5.Raw(count)} [" + string.Join(", ", items) + "]";
        }
        catch (Exception ex)
        {
            return $"{moment}: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Reads the length of the first segment, INSIDE a session, with the reason for a failure
    /// recorded — <c>null</c> is never left without a cause.</summary>
    private double? ReadLength(ProbeStep step, List<string> notes, string tag)
    {
        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch forRead)
        {
            notes.Add($"длина ({tag}): эскиз не перенесён в API7");
            return null;
        }

        var readNotes = new List<string>();
        var length = InSession(forRead, write: false, readNotes, (objects, _) =>
            objects.Count > 0 && objects[0].Drawing is KompasAPI7.ILineSegment bottom
                ? Api5.SafeDouble(() => bottom.Length)
                : null);
        notes.Add($"длина ({tag}) = {Api5.Num(length)}");
        notes.AddRange(readNotes);
        return length;
    }

    // ═══════════════════════════════════════════════════════════ parametric mode ══

    /// <summary><c>IKompasDocument2D1::ParametricMode</c> — DOCUMENTED
    /// (<c>ikompasdocument2d1_parametricmode.html</c>, BOOL, get/set) and PRESENT in the installed type
    /// library (<c>Bin\kAPI7.tlb</c>, slots 74 put / 75 get), but absent from the shipped wrapper.
    /// The customer's instruction: the absence from the wrapper does not cancel the route — call it.</summary>
    /// <remarks>MEASURED: the member is reached through the interface's own <c>IDispatch</c>, because
    /// the shipped wrapper declares no such slot. The call is typed on the IDispatch ABI, and the
    /// DISPID is RESOLVED BY NAME at run time rather than written in as a number.
    /// History: docs/decisions/probes.md#sketch-constraints</remarks>
    private void ParametricModeRoute()
    {
        var step = _report.Begin("K.15", "Параметрический режим документа и состояние эскиза",
            "Включается ли IKompasDocument2D1.ParametricMode (BOOL, TLB слоты 74/75), и даёт ли " +
            "включённый режим состояние 1 у полностью ограниченного эскиза?");
        try
        {
            if (!BuildRectangleSketch("k-pm", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            // DOC (ikompasdocument2d1.html): «Дополнительный интерфейс IKompasDocument2D… можно получить
            // посредством QueryInterface». For a sketch inside a part the 2D document IS the sketch's
            // FRAGMENT document, which exists only between BeginEdit() and EndEdit(); the part document
            // and the sketch itself are therefore tried as well, and the candidate that answers is NAMED
            // rather than assumed.
            var notes = new List<string>();
            var candidates = new List<(string Name, object? Object)>();
            KompasAPI7.IFragmentDocument? fragment = null;
            try
            {
                fragment = sketch.BeginEdit();
            }
            catch (COMException ex)
            {
                notes.Add($"BeginEdit для кандидата-фрагмента отказал: 0x{ex.HResult:X8}");
            }

            if (fragment is not null)
            {
                candidates.Add(("документ фрагмента эскиза", fragment));
            }

            candidates.Add(("документ детали", DocumentOf(sketch as KompasAPI7.IKompasAPIObject)));
            try
            {
                candidates.Add(("активный документ приложения", _app7?.ActiveDocument));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add("ActiveDocument не получен: " + ex.GetType().Name);
            }

            KompasAPI7.IKompasDocument2D1? document = null;
            var answered = new List<string>();
            foreach (var (name, candidate) in candidates)
            {
                if (candidate is KompasAPI7.IKompasDocument2D1 typed)
                {
                    answered.Add(name);
                    document ??= typed;
                }
            }

            step.Data["document_candidates"] = candidates.Select(c => c.Name).ToArray();
            step.Data["document_answered"] = answered;
            step.Observe("QI(IKompasDocument2D1) отвечают: " +
                         (answered.Count == 0 ? "никто" : string.Join(", ", answered)));

            if (document is null)
            {
                if (fragment is not null)
                {
                    try
                    {
                        sketch.EndEdit();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        notes.Add("EndEdit(): " + ex.GetType().Name);
                    }
                }

                step.Unknown("Ни один кандидат не отвечает QI(IKompasDocument2D1): " +
                             string.Join(", ", candidates.Select(c => c.Name)) +
                             ". Параметрический режим недостижим этим маршрутом.");
                return;
            }

            var before = ReadParametricMode(document, notes);
            step.Data["parametric_mode_before"] = Api5.Raw(before);
            step.Observe("ParametricMode до записи: " + Api5.Raw(before));

            var written = WriteParametricMode(document, true, notes);
            step.Data["parametric_mode_write"] = Api5.Raw(written);

            var after = ReadParametricMode(document, notes);
            step.Data["parametric_mode_after"] = Api5.Raw(after);
            step.Observe("ParametricMode после записи true: " + Api5.Raw(after));
            step.Data["notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (after is null)
            {
                step.Unknown("ParametricMode не прочитан — режим не подтверждён. Это утверждение о " +
                             "доступности члена через IDispatch, а не о продукте.");
                return;
            }

            // With the mode ON: a segment with both ends pinned, then the full contour.
            var stateMinimal = PinAndReadState(step, "k-pm-min");
            step.Data["state_pinned_segment"] = stateMinimal;

            var stateContour = FullContourState(step, "k-pm-contour");
            step.Data["state_full_contour"] = stateContour;

            if (stateMinimal == 1 || stateContour == 1)
            {
                step.Pass($"Параметрический режим включён (ParametricMode = {Api5.Raw(after)}), и состояние " +
                          $"1 ДОСТИГНУТО: закреплённый отрезок {Api5.Raw(stateMinimal)}, контур " +
                          $"{Api5.Raw(stateContour)}.");
            }
            else
            {
                step.Unknown($"Параметрический режим прочитан как {Api5.Raw(after)}, но состояние 1 не " +
                             $"достигнуто: закреплённый отрезок {Api5.Raw(stateMinimal)}, контур " +
                             $"{Api5.Raw(stateContour)}. Режим включается, состояние не меняется — " +
                             "это измеренный факт, и он выносится заказчику.");
            }

            if (fragment is not null)
            {
                try
                {
                    sketch.EndEdit();
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    notes.Add("EndEdit(): " + ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Параметрический режим не измерен: " + ex.Message);
        }
    }

    /// <summary>Reads <c>ParametricMode</c> through the interface's own IDispatch. The DISPID is
    /// resolved BY NAME, so no number from the type library is duplicated in the code.</summary>
    private bool? ReadParametricMode(KompasAPI7.IKompasDocument2D1 document, List<string> notes) =>
        InvokeParametricMode(document, null, notes);

    private bool? WriteParametricMode(KompasAPI7.IKompasDocument2D1 document, bool value, List<string> notes) =>
        InvokeParametricMode(document, value, notes);

    private bool? InvokeParametricMode(KompasAPI7.IKompasDocument2D1 document, bool? set, List<string> notes)
    {
        // The interface is DUAL, so QI for IDispatch answers. The cast is what performs the QI: a
        // `__ComObject` is not an instance of the hand-declared interface until the runtime is asked
        // for that interface explicitly, which is exactly what `as`/explicit cast do.
        IDispatchRaw? dispatch = null;
        try
        {
            dispatch = document as IDispatchRaw;
            dispatch ??= (IDispatchRaw)(object)document;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("QI(IDispatch) отказал: " + ex.GetType().Name);
            return null;
        }

        if (dispatch is null)
        {
            notes.Add("объект не отвечает QI(IDispatch)");
            return null;
        }

        var iidNull = Guid.Empty;
        var namePointer = Marshal.StringToCoTaskMemUni("ParametricMode");
        var namesPointer = Marshal.AllocCoTaskMem(IntPtr.Size);
        var idsPointer = Marshal.AllocCoTaskMem(sizeof(int));
        int dispid;
        try
        {
            Marshal.WriteIntPtr(namesPointer, namePointer);
            Marshal.WriteInt32(idsPointer, -1);
            var hr = dispatch.GetIDsOfNames(ref iidNull, namesPointer, 1, 0, idsPointer);
            dispid = Marshal.ReadInt32(idsPointer);
            if (hr != 0 || dispid < 0)
            {
                notes.Add($"GetIDsOfNames(ParametricMode) → 0x{hr:X8}, dispid {dispid}");
                return null;
            }

            notes.Add($"имя ParametricMode разрешено в dispid {dispid} (по имени, не по номеру)");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("GetIDsOfNames отказал: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(idsPointer);
            Marshal.FreeCoTaskMem(namesPointer);
            Marshal.FreeCoTaskMem(namePointer);
        }

        var isPut = set is not null;
        var variant = default(VariantRaw);
        variant.Vt = 11; // VT_BOOL
        variant.Value = set == true ? (short)-1 : (short)0;

        var parameters = new DispatchParams
        {
            Args = isPut ? Marshal.AllocCoTaskMem(Marshal.SizeOf<VariantRaw>()) : IntPtr.Zero,
            ArgCount = isPut ? 1 : 0,
            NamedArgs = IntPtr.Zero,
        };

        try
        {
            if (isPut)
            {
                Marshal.StructureToPtr(variant, parameters.Args, false);
            }

            var result = Marshal.AllocCoTaskMem(Marshal.SizeOf<VariantRaw>());
            try
            {
                var flags = isPut ? (short)0x0004 : (short)0x0002; // PROPERTYPUT / PROPERTYGET
                var hr = dispatch.Invoke(dispid, ref iidNull, 0, flags, ref parameters, result,
                    IntPtr.Zero, IntPtr.Zero);
                if (hr != 0)
                {
                    notes.Add($"Invoke(dispid {dispid}, {(isPut ? "put" : "get")}) → 0x{hr:X8}");
                    return null;
                }

                if (isPut)
                {
                    notes.Add($"ParametricMode записан через IDispatch, dispid {dispid}");
                    return true;
                }

                var read = Marshal.PtrToStructure<VariantRaw>(result);
                if (read.Vt != 11)
                {
                    notes.Add($"ParametricMode вернул VT={read.Vt}, а не BOOL");
                    return null;
                }

                return read.Value != 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem(result);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("Invoke отказал: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (parameters.Args != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(parameters.Args);
            }
        }
    }

    /// <summary>A segment with both ends pinned, on a document whose parametric mode is ON.</summary>
    private int? PinAndReadState(ProbeStep step, string name)
    {
        if (!BuildTwoSegmentDocument(name, step, out _, out _))
        {
            return null;
        }

        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            return null;
        }

        var notes = new List<string>();
        var result = InSession(sketch, write: true, notes, (objects, readNotes) =>
        {
            if (objects.Count == 0 || objects[0].ConstraintHost is not { } host)
            {
                return (int?)null;
            }

            for (var pointIndex = 0; pointIndex <= 1; pointIndex++)
            {
                CreateConstraint(host,
                    new Placement("pin", ksConstraintTypeEnum.ksCFixedPoint, 0, null, pointIndex),
                    null, null, readNotes);
            }

            CreateConstraint(host,
                new Placement("horizontal", ksConstraintTypeEnum.ksCHorizontal, 0, null, -1),
                null, null, readNotes);
            return 0;
        });

        _ = result;
        _doc!.RebuildDocument();
        return StateOfCurrent(step, name + "_state");
    }

    /// <summary>The four-segment contour with the full set of constraints and dimensions.</summary>
    private int? FullContourState(ProbeStep step, string name)
    {
        if (!BuildRectangleSketch(name, step, out _))
        {
            return null;
        }

        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            return null;
        }

        var notes = new List<string>();
        InSession(sketch, write: true, notes, (objects, readNotes) =>
        {
            var placements = new[]
            {
                new Placement("merge1", ksConstraintTypeEnum.ksCMergePoints, 0, new PartnerPick("seg", 1, 0), 1),
                new Placement("merge2", ksConstraintTypeEnum.ksCMergePoints, 1, new PartnerPick("seg", 2, 0), 1),
                new Placement("merge3", ksConstraintTypeEnum.ksCMergePoints, 2, new PartnerPick("seg", 3, 0), 1),
                new Placement("merge4", ksConstraintTypeEnum.ksCMergePoints, 3, new PartnerPick("seg", 0, 0), 1),
                new Placement("h1", ksConstraintTypeEnum.ksCHorizontal, 0, null, -1),
                new Placement("h2", ksConstraintTypeEnum.ksCHorizontal, 2, null, -1),
                new Placement("v1", ksConstraintTypeEnum.ksCVertical, 1, null, -1),
                new Placement("v2", ksConstraintTypeEnum.ksCVertical, 3, null, -1),
                new Placement("l1", ksConstraintTypeEnum.ksCFixedLenght, 0, null, -1),
                new Placement("l2", ksConstraintTypeEnum.ksCFixedLenght, 1, null, -1),
                new Placement("pin", ksConstraintTypeEnum.ksCFixedPoint, 0, null, 0),
            };

            foreach (var placement in placements)
            {
                if (placement.ObjectIndex >= objects.Count
                    || objects[placement.ObjectIndex].ConstraintHost is not { } host)
                {
                    readNotes.Add($"{placement.Tag}: объект не получен");
                    continue;
                }

                object? partner = null;
                if (placement.Partner is { } pick && pick.Index < objects.Count)
                {
                    partner = objects[pick.Index].ConstraintHost;
                }

                var attempt = CreateConstraint(host, placement, partner, null, readNotes);
                readNotes.Add($"{placement.Tag}: " + attempt.Summary);
            }

            return 0;
        });

        _doc!.RebuildDocument();
        return StateOfCurrent(step, name + "_state");
    }

    // ═══════════════════════════════════════════════════════════════════ SKC-03 ══

    private void ReadBack()
    {
        var step = _report.Begin("K.7", "SKC-03 — чтение списка ограничений объекта",
            "Читается ли массив Constraints, какие поля доступны, и согласуется ли он с состоянием?");
        try
        {
            if (!BuildRectangleDocument("k-read", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var applied = ApplyBatch(step, "k-read",
            [
                new Placement("horizontal_bottom", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
                new Placement("length_bottom", ksConstraintTypeEnum.ksCFixedLenght, 0, null, 0, Value: 40d),
                new Placement("vertical_left", ksConstraintTypeEnum.ksCVertical, 3, null, 0),
            ]);
            step.Data["applied"] = applied;
            foreach (var line in applied)
            {
                step.Observe(line);
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7 — список не прочитан.");
                return;
            }

            var notes = new List<string>();
            var rows = InSession(sketch, write: false, notes, (objects, readNotes) =>
            {
                if (objects.Count == 0)
                {
                    return new List<string>();
                }

                return ReadConstraints(objects[0].ConstraintHost!, readNotes);
            });

            step.Data["constraint_rows"] = rows?.Count ?? 0;
            step.Data["constraint_rows_detail"] = rows;
            step.Data["notes"] = notes;

            var objectState = InSession(sketch, write: false, notes, (objects, _) =>
                objects.Count == 0 || objects[0].ConstraintHost is null
                    ? (int?)null
                    : Api5.SafeInt(() => (int)objects[0].ConstraintHost!.ConstraintsState));
            step.Data["object_constraints_state"] = objectState;
            step.Data["aggregate_state"] = StateOfCurrent(step, "read_aggregate");

            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (rows is null || rows.Count == 0)
            {
                step.Unknown("Список ограничений пуст или не развёрнут: пустой список и «не прочитано» " +
                             "различаются в notes, а не сглаживаются.");
            }
            else
            {
                step.Pass($"Прочитано ограничений на объекте: {rows.Count} — тип, индекс точки, партнёр и " +
                          "Valid читаются полями самого ограничения внутри сеанса.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Список не прочитан: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ SKC-04 ══

    private void DeleteRoute()
    {
        var step = _report.Begin("K.8", "SKC-04 — удаление одного ограничения и всех ограничений",
            "Возвращается ли состояние к измеренному «до» после Delete() и DeleteConstraints()?");
        try
        {
            if (!BuildRectangleDocument("k-del", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            // BOTH constraints go on ONE object: DeleteConstraints() must be measured on a NON-EMPTY
            // list. MEASURED on the first run: with a single constraint the Delete() already emptied the
            // list and DeleteConstraints() answered False on nothing — a reading about an empty list,
            // not about the method.
            var applied = ApplyBatch(step, "k-del",
            [
                new Placement("horizontal_bottom", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
                new Placement("length_bottom", ksConstraintTypeEnum.ksCFixedLenght, 0, null, 0, Value: 40d),
            ]);
            step.Data["applied"] = applied;

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7 — удаление не измерено.");
                return;
            }

            var notes = new List<string>();
            var (before, single, all) = InSession(sketch, write: true, notes, (objects, readNotes) =>
            {
                if (objects.Count == 0 || objects[0].ConstraintHost is not { } host)
                {
                    return (-1, "объект 0 не получен", "объект 0 не получен");
                }

                var countBefore = ReadConstraints(host, readNotes).Count;
                // DeleteConstraints() is measured FIRST, on the full list; Delete() then runs on a
                // single-constraint object so that BOTH methods are seen with something to remove.
                var rest = DeleteAll(host, readNotes);
                var reApplied = CreateConstraint(host,
                    new Placement("re_added", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0), null, null, readNotes);
                var one = DeleteOne(host, readNotes);
                return (countBefore, one, rest + "; повторное наложение: " + reApplied.Summary);
            }) is var result
                ? (result.Item1, result.Item2, result.Item3)
                : (-1, "сеанс не открыт", "сеанс не открыт");

            step.Data["count_before"] = before;
            step.Data["delete_one_result"] = single;
            step.Data["delete_all_result"] = all;
            step.Data["notes"] = notes;
            step.Observe("Delete() одного ограничения: " + single);
            step.Observe("DeleteConstraints() всех ограничений объекта: " + all);
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            step.Pass("Удаление одного ограничения и всех ограничений объекта измерено внутри сеанса; " +
                      "счётчики до и после записаны.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Удаление не измерено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-3 ══

    private void HorizontalControl()
    {
        var step = _report.Begin("K.9", "OQ-3 — двигает ли ядро геометрию при наложении ограничения",
            "Отрезок 25 мм под 30° к оси X плюс ksCHorizontal: переходит ли конец в (25,0) или " +
            "координаты не меняются?");
        try
        {
            step.Data["expectation"] = "если доворачивает — конец уходит в (25,0), Y-габарит 12,5 → 0; " +
                                       "если не доворачивает — координаты не меняются";
            step.Data["tilt_end_x"] = Api5.Num(TiltX);
            step.Data["tilt_end_y"] = Api5.Num(TiltY);

            if (!BuildTiltedDocument("k-tilt", step, out _))
            {
                step.Fail("Контрольный документ с наклонным отрезком не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();
            var before = ReadFirstSegment(sketch, write: false, notes);
            step.Data["before_x2"] = Api5.Num(before?.X2);
            step.Data["before_y2"] = Api5.Num(before?.Y2);
            step.Data["before_angle"] = Api5.Num(before?.Angle);
            step.Data["before_length"] = Api5.Num(before?.Length);

            var applied = ApplyBatch(step, "k-tilt",
            [
                new Placement("horizontal_on_tilt", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
            ]);
            step.Data["applied"] = applied;

            var after = ReadFirstSegment(sketch, write: false, notes);
            step.Data["after_x2"] = Api5.Num(after?.X2);
            step.Data["after_y2"] = Api5.Num(after?.Y2);
            step.Data["after_angle"] = Api5.Num(after?.Angle);
            step.Data["after_length"] = Api5.Num(after?.Length);

            var moved = before is not null && after is not null
                && (Math.Abs(before.X2 - after.X2) > 1e-6 || Math.Abs(before.Y2 - after.Y2) > 1e-6);
            step.Data["geometry_moved"] = moved;
            step.Data["delta_y2"] = before is not null && after is not null ? Api5.Num(after.Y2 - before.Y2) : null;
            step.Data["delta_angle"] = before is not null && after is not null ? Api5.Num(after.Angle - before.Angle) : null;

            step.Observe($"до: конец ({Api5.Num(before?.X2)}, {Api5.Num(before?.Y2)}), угол " +
                         $"{Api5.Num(before?.Angle)}, длина {Api5.Num(before?.Length)}; " +
                         $"после: ({Api5.Num(after?.X2)}, {Api5.Num(after?.Y2)}), угол " +
                         $"{Api5.Num(after?.Angle)}, длина {Api5.Num(after?.Length)}");

            if (before is null || after is null)
            {
                step.Unknown("Координаты не прочитаны — контроль не различает исходы. Это дефект прибора.");
                return;
            }

            step.Pass(moved
                ? "Ядро ПЕРЕМЕСТИЛО геометрию: конец отрезка сдвинулся. Это измеренный факт о продукте, " +
                  "и он обязан публиковаться дельтой, а не скрываться."
                : "Ядро НЕ переместило геометрию: координаты конца отрезка не изменились в пределах 1e-6.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Контроль не выполнен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-4 ══

    private void ObjectVsAggregateState()
    {
        var step = _report.Begin("K.10", "OQ-4 — состояние объекта против агрегатного состояния эскиза",
            "Совпадают ли IDrawingObject1.ConstraintsState и ISketch.ConstraintsState, читаемые подряд " +
            "с одного эскиза?");
        try
        {
            if (!BuildRectangleDocument("k-state", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var applied = ApplyBatch(step, "k-state",
            [
                new Placement("horizontal_bottom", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
            ]);
            step.Data["applied"] = applied;

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var readings = new List<string>();
            for (var pass = 0; pass < 2; pass++)
            {
                var aggregate = StateOfCurrent(step, "state_agg_" + pass);
                var notes = new List<string>();
                var objectState = InSession(sketch, write: false, notes, (objects, _) =>
                    objects.Count == 0 || objects[0].ConstraintHost is null
                        ? (int?)null
                        : Api5.SafeInt(() => (int)objects[0].ConstraintHost!.ConstraintsState));

                step.Data["pass_" + pass + "_aggregate"] = aggregate;
                step.Data["pass_" + pass + "_object"] = objectState;
                readings.Add($"проход {pass}: агрегат {Api5.Raw(aggregate)}, объект {Api5.Raw(objectState)}");
            }

            step.Data["readings"] = readings;
            foreach (var line in readings)
            {
                step.Observe(line);
            }

            step.Pass("Оба источника прочитаны подряд с одного эскиза; их числа записаны отдельно и не " +
                      "подменяют друг друга.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Сравнение не выполнено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ OQ-6 ══

    private void Api5NegativeControl()
    {
        var step = _report.Begin("K.11", "OQ-6 — отрицательный контроль прежнего маршрута API5",
            "Воспроизводится ли ksSetObjConstraint → 0 в той же постановке, что и явный маршрут API7?");
        try
        {
            if (!BuildTwoSegmentDocument("k-neg", step, out var editor, out var refs, closeEditor: false))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            step.Data["segment_refs"] = refs;

            if (_app.GetParamStruct((short)StructType2DEnum.ko_ConstraintParam) is not ksConstraintParam parameter)
            {
                step.Unknown("ko_ConstraintParam не получен — контроль не поставлен.");
                return;
            }

            parameter.constrType = (short)LocalConstraintType.Horizontal;
            parameter.index = 0;
            parameter.partner = refs.Count > 1 ? refs[1] : 0;
            parameter.partnerIndex = 0;
            parameter.Init();

            var target = refs.Count > 0 ? refs[0] : 0;
            var exists = Api5.SafeInt(() => editor.ksExistObj(target));
            var result = Api5.SafeInt(() => editor.ksSetObjConstraint(target, parameter));
            step.Data["ksExistObj_target"] = exists;
            step.Data["ksSetObjConstraint_result"] = result;
            step.Observe($"ksExistObj({target}) → " + Api5.Raw(exists));
            step.Observe($"ksSetObjConstraint({target}) → " + Api5.Raw(result) + " (1 = принято)");

            if (result == 0 && exists is not null and not 0)
            {
                step.Unknown("API5-маршрут вновь ответил 0 при подтверждённой адресации — прежняя запись " +
                             "воспроизведена, и она не переписывается: отказ остаётся отказом. Явный " +
                             "маршрут — API7.");
            }
            else if (result == 0)
            {
                step.Unknown("ksSetObjConstraint ответил 0, но и ksExistObj по тому же номеру ответил " +
                             Api5.Raw(exists) + " — адресация НЕ подтверждена, поэтому отказ нельзя " +
                             "отнести к применимости вызова. Это дефект постановки контроля, а не факт " +
                             "о продукте.");
            }
            else
            {
                step.Observe("API5-маршрут ответил " + Api5.Raw(result) + " — прежняя запись НЕ " +
                             "воспроизведена; это измеренное расхождение, оно записывается датой.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Контроль не поставлен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ SKC-05 ══

    private void FullyDefined()
    {
        var step = _report.Begin("K.12", "SKC-05 — эскиз доходит до fully_defined и переживает переоткрытие",
            "Что даёт контур из 4 отрезков с двумя горизонталями, двумя вертикалями, двумя размерами и " +
            "фиксацией угловой точки, и читается ли состояние 1 ПОСЛЕ save → close → reopen?");
        try
        {
            if (!BuildRectangleDocument("k-full", step, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var beforeState = StateOfCurrent(step, "full_before");
            step.Data["state_before"] = beforeState;
            step.Data["state_before_interpreted"] = Interpret(beforeState);

            // The order's §11.4 expectation was "two horizontalities, two verticalities and two
            // dimensions". MEASURED on the first run: that is NOT enough — the contour keeps two
            // positional degrees of freedom, and the state stayed 2. The expectation is questioned
            // FIRST, as the order demands, so the positional fix is added here and the difference is
            // recorded rather than smoothed over.
            step.Data["expectation_order_11_4"] = "2 горизонтали + 2 вертикали + 2 размера → 1";
            // The corners are MERGED first: four separate segments carry eight end points, and without
            // the merges the contour keeps degrees of freedom no dimension can remove. The merge pairs
            // are the rectangle's own corners, named by point index on each segment (0 = start).
            var applied = ApplyBatch(step, "k-full",
            [
                new Placement("merge_corner_1", ksConstraintTypeEnum.ksCMergePoints, 0,
                    new PartnerPick("seg", 1, 0), 1),
                new Placement("merge_corner_2", ksConstraintTypeEnum.ksCMergePoints, 1,
                    new PartnerPick("seg", 2, 0), 1),
                new Placement("merge_corner_3", ksConstraintTypeEnum.ksCMergePoints, 2,
                    new PartnerPick("seg", 3, 0), 1),
                new Placement("merge_corner_4", ksConstraintTypeEnum.ksCMergePoints, 3,
                    new PartnerPick("seg", 0, 0), 1),
                new Placement("horizontal_bottom", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
                new Placement("horizontal_top", ksConstraintTypeEnum.ksCHorizontal, 2, null, 0),
                new Placement("vertical_left", ksConstraintTypeEnum.ksCVertical, 3, null, 0),
                new Placement("vertical_right", ksConstraintTypeEnum.ksCVertical, 1, null, 0),
                new Placement("length_bottom", ksConstraintTypeEnum.ksCFixedLenght, 0, null, 0, Value: 2d * HalfA),
                new Placement("length_left", ksConstraintTypeEnum.ksCFixedLenght, 3, null, 0, Value: 2d * HalfB),
                new Placement("fixed_corner", ksConstraintTypeEnum.ksCFixedPoint, 3, null, 0),
            ]);
            step.Data["applied"] = applied;
            foreach (var line in applied)
            {
                step.Observe(line);
            }

            var constraintCounts = CountConstraintsPerObject(step);
            step.Data["constraint_counts"] = constraintCounts;
            step.Observe("ограничений по объектам: " + constraintCounts);

            var afterState = StateOfCurrent(step, "full_after");
            step.Data["state_after"] = afterState;
            step.Data["state_after_interpreted"] = Interpret(afterState);
            step.Data["volume_after"] = Api5.Num(Api5.Volume(_part!));

            var path = Path.Combine(_options.WorkDir, "k-fully-defined.m3d");
            step.Data["saved_path"] = path;
            if (_doc!.SaveAs(path) != true)
            {
                step.Unknown("SaveAs не подтверждён — переоткрытие не проверено.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc!.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(path, true) != true)
            {
                step.Unknown("Open переоткрытого документа не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            step.Data["volume_after_reopen"] = Api5.Num(Api5.Volume(_part));

            if (!FindSketchInReopened(step))
            {
                step.Unknown("Эскиз в переоткрытом документе не найден — состояние не перечитано.");
                return;
            }

            var reopenState = StateOfCurrent(step, "full_reopen");
            var reopenAgain = StateOfCurrent(step, "full_reopen_second");
            step.Data["state_after_reopen"] = reopenState;
            step.Data["state_after_reopen_interpreted"] = Interpret(reopenState);
            step.Data["stable_across_reads"] = reopenState == reopenAgain;

            if (afterState == 1 && reopenState == 1)
            {
                step.Pass("Состояние 1 (полностью определён) получено и ПРОЧИТАНО ПОСЛЕ save → close → " +
                          "reopen — не по заявлению.");
            }
            else if (reopenState is not null && reopenState == afterState)
            {
                step.Unknown($"Состояние после наложения {Api5.Raw(afterState)} ({Interpret(afterState)}), " +
                             $"после переоткрытия {Api5.Raw(reopenState)} — совпало, но это не 1. " +
                             "Достижимость fully_defined этой постановкой не подтверждена и остаётся " +
                             "названной открытой.");
            }
            else
            {
                step.Unknown($"До reopen {Api5.Raw(afterState)}, после reopen {Api5.Raw(reopenState)}: " +
                             "состояние не пережило переоткрытие — это измеренный факт, он называется.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Полная определённость не измерена: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════ minimal fully-defined ══

    /// <summary>The MINIMAL fully-defined sketch: a segment has four degrees of freedom, and two fixed
    /// points remove exactly four. Tried as a separate step because the four-segment rectangle did NOT
    /// reach state 1, and "the contour is not rigid enough" and "the state does not respond" are
    /// different statements — the second would make the whole mode unmeasurable.</summary>
    private void MinimalFullyDefined()
    {
        var step = _report.Begin("K.12b", "SKC-05 — минимальный полностью определённый эскиз",
            "Два закреплённых конца одного отрезка снимают все четыре степени свободы — доходит ли " +
            "эскиз до состояния 1 и переживает ли оно переоткрытие?");
        try
        {
            if (!BuildTwoSegmentDocument("k-min", step, out _, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var before = StateOfCurrent(step, "min_before");
            step.Data["state_before"] = before;

            var applied = ApplyBatch(step, "k-min",
            [
                new Placement("fix_start", ksConstraintTypeEnum.ksCFixedPoint, 0, null, 0),
                new Placement("fix_end", ksConstraintTypeEnum.ksCFixedPoint, 0, null, 1),
                new Placement("horizontal", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0),
            ]);
            step.Data["applied"] = applied;
            foreach (var line in applied)
            {
                step.Observe(line);
            }

            var after = StateOfCurrent(step, "min_after");
            step.Data["state_after"] = after;
            step.Data["state_after_interpreted"] = Interpret(after);
            step.Data["constraint_counts"] = CountConstraintsPerObject(step);

            var path = Path.Combine(_options.WorkDir, "k-minimal-defined.m3d");
            step.Data["saved_path"] = path;
            if (_doc!.SaveAs(path) != true)
            {
                step.Unknown("SaveAs не подтверждён — переоткрытие не проверено.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc!.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(path, true) != true)
            {
                step.Unknown("Open переоткрытого документа не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            if (!FindSketchInReopened(step))
            {
                step.Unknown("Эскиз в переоткрытом документе не найден — состояние не перечитано.");
                return;
            }

            var reopen = StateOfCurrent(step, "min_reopen");
            var reopenAgain = StateOfCurrent(step, "min_reopen_second");
            step.Data["state_after_reopen"] = reopen;
            step.Data["stable_across_reads"] = reopen == reopenAgain;

            if (after == 1 && reopen == 1)
            {
                step.Pass("Состояние 1 получено и ПРОЧИТАНО ПОСЛЕ save → close → reopen: достижимость " +
                          "fully_defined измерена, а не заявлена.");
            }
            else
            {
                step.Unknown($"Состояние после наложения {Api5.Raw(after)} ({Interpret(after)}), после " +
                             $"переоткрытия {Api5.Raw(reopen)} ({Interpret(reopen)}). Достижимость " +
                             "fully_defined этой постановкой не подтверждена и остаётся названной открытой.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Минимальный контроль не выполнен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════ incremental state ══

    /// <summary>Reads the aggregate state after EVERY constraint, one at a time. The purpose is to
    /// separate two statements that a single end reading cannot tell apart: "the contour is still not
    /// rigid enough" and "the state does not respond to constraints created by this route at all".
    /// The second would make <c>SKC-05</c> unmeasurable rather than merely open.</summary>
    private void IncrementalState()
    {
        var step = _report.Begin("K.12c", "SKC-05 — состояние после КАЖДОГО ограничения",
            "Меняется ли агрегатное состояние эскиза хотя бы на одном шаге, и на каком именно?");
        try
        {
            if (!BuildTwoSegmentDocument("k-inc", step, out _, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            var trace = new List<string>();
            trace.Add($"до ограничений: {Api5.Raw(StateOfCurrent(step, "inc_0"))}");

            var variants = new (string Name, Placement Placement)[]
            {
                ("fix_point_index0", new Placement("fix_point_index0", ksConstraintTypeEnum.ksCFixedPoint, 0, null, 0)),
                ("fix_point_index1", new Placement("fix_point_index1", ksConstraintTypeEnum.ksCFixedPoint, 0, null, 1)),
                ("fix_point_index_minus1", new Placement("fix_point_index_minus1", ksConstraintTypeEnum.ksCFixedPoint, 0, null, -1)),
                ("horizontal", new Placement("horizontal", ksConstraintTypeEnum.ksCHorizontal, 0, null, 0)),
                ("fixed_length", new Placement("fixed_length", ksConstraintTypeEnum.ksCFixedLenght, 0, null, 0)),
            };

            var index = 1;
            foreach (var (name, placement) in variants)
            {
                var lines = ApplyBatch(step, "inc-" + name, [placement]);
                var state = StateOfCurrent(step, "inc_" + index);
                var counts = CountConstraintsPerObject(step);
                trace.Add($"{name} → {lines.FirstOrDefault() ?? "нет строки"}; состояние {Api5.Raw(state)}; " +
                          $"ограничений по объектам {counts}");
                index++;
            }

            step.Data["trace"] = trace;
            foreach (var line in trace)
            {
                step.Observe(line);
            }

            var states = trace.Select(t => t.Split("состояние ").Last().Split(';').First()).Distinct().ToList();
            step.Data["distinct_states"] = states;
            step.Pass("Состояние прочитано после каждого ограничения; различимые значения названы: " +
                      string.Join(", ", states));
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Пошаговое чтение не выполнено: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════ fixed point really pins ══

    /// <summary>Does <c>ksCFixedPoint</c> really pin the endpoints? A DISCRIMINATING control: with the
    /// endpoints pinned, a horizontal constraint applied to the tilted segment has nothing left to
    /// rotate, so the coordinates must not move; in the unpinned case (K.9) the very same call rotated
    /// the segment by 30°. "The constraint was created" and "the constraint holds the geometry" are
    /// different statements, and only the second makes the mode measurable.</summary>
    private void FixedPointPins()
    {
        var step = _report.Begin("K.12d", "SKC-05 — держит ли закрепление точки геометрию",
            "Отрезок 30° с двумя закреплёнными концами плюс горизонталь: остаются ли координаты на " +
            "месте, тогда как без закрепления та же горизонталь повернула отрезок на 30°?");
        try
        {
            if (!BuildTwoSegmentDocument("k-pin", step, out _, out _))
            {
                step.Fail("Контрольный документ не построен.");
                return;
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7.");
                return;
            }

            var notes = new List<string>();
            var before = ReadSegment(sketch, index: 1, notes);
            step.Data["before_x2"] = Api5.Num(before?.X2);
            step.Data["before_y2"] = Api5.Num(before?.Y2);
            step.Data["before_angle"] = Api5.Num(before?.Angle);

            var applied = ApplyBatch(step, "k-pin",
            [
                new Placement("pin_start", ksConstraintTypeEnum.ksCFixedPoint, 1, null, 0),
                new Placement("pin_end", ksConstraintTypeEnum.ksCFixedPoint, 1, null, 1),
                new Placement("horizontal", ksConstraintTypeEnum.ksCHorizontal, 1, null, 0),
            ]);
            step.Data["applied"] = applied;
            foreach (var line in applied)
            {
                step.Observe(line);
            }

            var after = ReadSegment(sketch, index: 1, notes);
            step.Data["after_x2"] = Api5.Num(after?.X2);
            step.Data["after_y2"] = Api5.Num(after?.Y2);
            step.Data["after_angle"] = Api5.Num(after?.Angle);

            var moved = before is not null && after is not null
                && (Math.Abs(before.X2 - after.X2) > 1e-6 || Math.Abs(before.Y2 - after.Y2) > 1e-6);
            step.Data["geometry_moved"] = moved;
            step.Data["delta_angle"] = before is not null && after is not null
                ? Api5.Num(after.Angle - before.Angle)
                : null;
            step.Data["state_after"] = StateOfCurrent(step, "pin_state");

            // The same quantity read INSIDE an edit session: several members of this route answer only
            // inside it (Constraints → null outside), so the state is read both ways and the two
            // readings are compared rather than merged.
            var insideNotes = new List<string>();
            var stateInside = InSession(sketch, write: false, insideNotes, (objects, _) =>
            {
                if (objects.Count == 0 || objects[0].ConstraintHost is null)
                {
                    return (int?)null;
                }

                var objectState = Api5.SafeInt(() => (int)objects[0].ConstraintHost!.ConstraintsState);
                return objectState;
            });
            step.Data["object_state_inside_session"] = stateInside;
            step.Data["inside_notes"] = insideNotes;

            step.Observe($"до: ({Api5.Num(before?.X2)}, {Api5.Num(before?.Y2)}) угол {Api5.Num(before?.Angle)}; " +
                         $"после: ({Api5.Num(after?.X2)}, {Api5.Num(after?.Y2)}) угол {Api5.Num(after?.Angle)}");
            step.Observe("состояние объекта внутри сеанса: " + Api5.Raw(stateInside));

            step.Pass(moved
                ? "Закрепление концов НЕ удержало отрезок: та же горизонталь вновь повернула его. " +
                  "Значит ksCFixedPoint в этой постановке геометрию не фиксирует — это названный факт."
                : "Закрепление концов УДЕРЖАЛО отрезок: при закреплённых концах горизонталь не смогла " +
                  "его повернуть, тогда как без закрепления (K.9) поворачивала на 30°. Ограничение " +
                  "действует, и состояние fully_defined остаётся недостигнутым по другой причине.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Контроль не выполнен: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ negative ══

    private void NegativeTests()
    {
        var step = _report.Begin("K.13", "Отрицательные контроли по исходу",
            "Отказывает ли маршрут на несуществующем индексе точки, на партнёре без объекта, на " +
            "несовместимом размере и на ассоциации без массива партнёров?");
        try
        {
            var outcomes = new List<string>();

            var badIndex = Attempt(step, "neg-index", ksConstraintTypeEnum.ksCFixedPoint, DocKind.TwoSegment,
                new ObjectPick(0, 99), null);
            outcomes.Add("индекс точки 99 на отрезке → " + badIndex.Summary);
            step.Data["bad_index"] = badIndex.Summary;

            var badPartner = Attempt(step, "neg-partner", ksConstraintTypeEnum.ksCParallel, DocKind.Rectangle,
                new ObjectPick(0, 0), new PartnerPick("not-an-object", 0));
            outcomes.Add("партнёр без объекта → " + badPartner.Summary);
            step.Data["bad_partner"] = badPartner.Summary;

            var badValue = Attempt(step, "neg-value", ksConstraintTypeEnum.ksCFixedAngle, DocKind.Rectangle,
                new ObjectPick(0, 0), new PartnerPick("seg", 1), null, value: 100000d);
            outcomes.Add("угловой размер 100000° → " + badValue.Summary);
            step.Data["bad_value"] = badValue.Summary;

            var association = Attempt(step, "neg-assoc", ksConstraintTypeEnum.ksCAssociation, DocKind.Rectangle,
                new ObjectPick(0, 0), null);
            outcomes.Add("ksCAssociation без массива партнёров → " + association.Summary);
            step.Data["association"] = association.Summary;

            step.Data["outcomes"] = outcomes;
            foreach (var line in outcomes)
            {
                step.Observe(line);
            }

            step.Pass("Четыре отрицательных контроля поставлены; исход каждого назван отдельно, и ни один " +
                      "отказ не превращён в успех.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Отрицательные контроли не поставлены: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════ machinery ══

    private enum DocKind
    {
        Rectangle,
        TwoSegment,
        Tilted,
        TwoCircle,
        Collinear,
        Point,
    }

    private sealed record ObjectPick(int Index, int PointIndex, string? Kind = null, int Occurrence = 0);

    private sealed record PartnerPick(string Tag, int Index, int PartnerPointIndex = 0, string? Kind = null,
        bool AsArray = false, int SecondIndex = -1);

    /// <summary>One constraint to apply, in terms of a document built by the matching builder.</summary>
    private sealed record Placement(
        string Tag,
        ksConstraintTypeEnum Type,
        int ObjectIndex,
        PartnerPick? Partner,
        int PointIndex,
        double? Value = null,
        string? Expression = null,
        string? Variable = null,
        string? AxisTag = null,
        string? ObjectKind = null,
        int ObjectOccurrence = 0,
        int PartnerPointIndex = 0);

    /// <summary>Outcome of one constraint attempt. Every field is an OBSERVATION: <c>null</c> means the
    /// member did not answer, which is not the same as "false" or "0".</summary>
    private sealed record ConstraintAttempt(
        bool? Created,
        bool? Valid,
        string Summary,
        double? ValueRead = null,
        int? DegreesRead = null,
        int? MinutesRead = null,
        double? SecondsRead = null,
        string? ExpressionRead = null,
        string? VariableRead = null,
        string? StoredSummary = null);

    private sealed record GeometricCandidate(
        string Name,
        ksConstraintTypeEnum Type,
        DocKind Doc,
        ObjectPick Object,
        PartnerPick? Partner,
        string? Axis = null);

    private sealed record DimensionCandidate(
        string Name,
        ksConstraintTypeEnum Type,
        DocKind Doc,
        ObjectPick Object,
        PartnerPick? Partner,
        double? Value,
        string? Expression,
        string? Variable);

    /// <summary>The 16 geometric types of row <c>SKC-01</c>, each with a placement that gives the type
    /// something to attach to: a point of a segment for the point constraints, a pair of segments for
    /// the paired ones, two circles for the radius pair.</summary>
    private static IEnumerable<GeometricCandidate> GeometricCandidates()
    {
        yield return new("fixed_point", ksConstraintTypeEnum.ksCFixedPoint, DocKind.Rectangle,
            new ObjectPick(0, 0), null);
        yield return new("point_on_curve", ksConstraintTypeEnum.ksCPointOnCurve, DocKind.Point,
            new ObjectPick(-1, 0, "Point", 0), new PartnerPick("seg", 0));
        yield return new("horizontal", ksConstraintTypeEnum.ksCHorizontal, DocKind.Tilted,
            new ObjectPick(0, 0), null);
        yield return new("vertical", ksConstraintTypeEnum.ksCVertical, DocKind.TwoSegment,
            new ObjectPick(1, 0), null);
        yield return new("parallel", ksConstraintTypeEnum.ksCParallel, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 2));
        yield return new("perpendicular", ksConstraintTypeEnum.ksCPerpendicular, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 1));
        yield return new("equal_length", ksConstraintTypeEnum.ksCEqualLength, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 1));
        yield return new("equal_radius", ksConstraintTypeEnum.ksCEqualRadius, DocKind.TwoCircle,
            new ObjectPick(0, 0), new PartnerPick("circle", 1));
        yield return new("h_align_points", ksConstraintTypeEnum.ksCHAlignPoints, DocKind.TwoSegment,
            new ObjectPick(1, 0), new PartnerPick("seg", 0));
        yield return new("v_align_points", ksConstraintTypeEnum.ksCVAlignPoints, DocKind.TwoSegment,
            new ObjectPick(1, 0), new PartnerPick("seg", 0));
        yield return new("merge_points", ksConstraintTypeEnum.ksCMergePoints, DocKind.TwoSegment,
            new ObjectPick(1, 0), new PartnerPick("seg", 0));
        yield return new("tangent_two_curves", ksConstraintTypeEnum.ksCTangentTwoCurves, DocKind.TwoCircle,
            new ObjectPick(0, 0), new PartnerPick("circle", 1));
        yield return new("symmetry_two_points", ksConstraintTypeEnum.ksCSymmetryTwoPoints, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 2), Axis: "seg:1");
        yield return new("collinear", ksConstraintTypeEnum.ksCCollinear, DocKind.Collinear,
            new ObjectPick(0, 0), new PartnerPick("seg", 1));
        yield return new("point_on_curve_middle", ksConstraintTypeEnum.ksCPointOnCurveMiddle, DocKind.Point,
            new ObjectPick(-1, 0, "Point", 0), new PartnerPick("seg", 0));
        yield return new("point_on_curve_middle_on_curve", ksConstraintTypeEnum.ksCPointOnCurveMiddle, DocKind.Point,
            new ObjectPick(0, 0), new PartnerPick("Point", 0));
        yield return new("concentricity", ksConstraintTypeEnum.ksCConcentricity, DocKind.TwoCircle,
            new ObjectPick(0, 0), new PartnerPick("circle", 1));
    }

    /// <summary>The four dimension types of row <c>SKC-02</c>.</summary>
    private static IEnumerable<DimensionCandidate> DimensionCandidates()
    {
        yield return new("fixed_length", ksConstraintTypeEnum.ksCFixedLenght, DocKind.Rectangle,
            new ObjectPick(0, 0), null, 40d, null, null);
        yield return new("fixed_dim_array", ksConstraintTypeEnum.ksCFixedDim, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 1, 0, null, AsArray: true, SecondIndex: 2), 40d, null, null);
        yield return new("fixed_angle", ksConstraintTypeEnum.ksCFixedAngle, DocKind.Rectangle,
            new ObjectPick(0, 0), new PartnerPick("seg", 1), 90d, null, null);
        yield return new("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable, DocKind.Rectangle,
            new ObjectPick(0, 0), null, 40d, "40", "k_width");
    }

    /// <summary>A fresh document, one constraint, and the outcome of that single call — with the
    /// constraint read back FROM THE OBJECT'S LIST inside the same session.</summary>
    private ConstraintAttempt Attempt(
        ProbeStep step,
        string name,
        ksConstraintTypeEnum type,
        DocKind doc,
        ObjectPick pick,
        PartnerPick? partner,
        string? axis = null,
        double? value = null,
        string? expression = null,
        string? variable = null)
    {
        try
        {
            if (!Build(doc, name, step))
            {
                return new(null, null, "документ не построен");
            }

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                return new(null, null, "эскиз не перенесён в API7");
            }

            var notes = new List<string>();
            var attempt = InSession(sketch, write: true, notes, (objects, readNotes) =>
            {
                var targetIndex = ResolveObject(objects, pick);
                if (targetIndex < 0 || objects[targetIndex].ConstraintHost is not { } target)
                {
                    return new ConstraintAttempt(null, null,
                        $"объект {Describe(pick)} не отвечает IDrawingObject1 ({objects.Count} шт.: " +
                        string.Join(", ", objects.Select(o => o.DrawingKind)) + ")");
                }

                object? partnerObject = null;
                if (partner is not null)
                {
                    if (partner.Tag == "not-an-object")
                    {
                        partnerObject = "not-an-object";
                    }
                    else
                    {
                        var partnerIndex = ResolvePartner(objects, partner);
                        if (partnerIndex < 0 || objects[partnerIndex].ConstraintHost is not { } p)
                        {
                            return new ConstraintAttempt(null, null,
                                $"партнёр '{partner.Tag}:{partner.Index}' не разрешился ({objects.Count} шт.: " +
                                string.Join(", ", objects.Select(o => o.DrawingKind)) + ")");
                        }

                        partnerObject = partner.AsArray ? BuildPartnerArray(objects, partner) : p;
                    }
                }

                object? axisObject = null;
                if (axis is not null)
                {
                    var axisIndex = ParseTag(axis);
                    if (axisIndex >= 0 && axisIndex < objects.Count)
                    {
                        axisObject = objects[axisIndex].ConstraintHost;
                    }
                }

                var placement = new Placement(name, type, pick.Index, partner, pick.PointIndex,
                    value, expression, variable, axis);
                var created = CreateConstraint(target, placement, partnerObject, axisObject, readNotes);

                // THE READ-BACK IS FROM THE OBJECT'S OWN LIST, INSIDE THE SESSION — not from the
                // template object returned by NewConstraint(), whose members keep their defaults
                // (MEASURED: Value=0, Expression="" after a successful Create()).
                var stored = ReadConstraints(target, readNotes);
                var storedSummary = stored.Count == 0
                    ? "список пуст или не развёрнут"
                    : string.Join(" | ", stored);
                readNotes.Add($"{name}: в списке объекта {stored.Count} ограничений");

                return created with { StoredSummary = storedSummary };
            });

            return attempt ?? new(null, null, "сеанс не открыт");
        }
        catch (Exception ex)
        {
            return new(null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Applies a batch of constraints to the CURRENT document inside ONE write session.</summary>
    private List<string> ApplyBatch(ProbeStep step, string tag, IReadOnlyList<Placement> placements)
    {
        var lines = new List<string>();
        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            lines.Add("эскиз не перенесён в API7");
            return lines;
        }

        var notes = new List<string>();
        InSession(sketch, write: true, notes, (objects, readNotes) =>
        {
            foreach (var placement in placements)
            {
                var targetIndex = ResolveObject(objects, new ObjectPick(placement.ObjectIndex, placement.PointIndex,
                    placement.ObjectKind, placement.ObjectOccurrence));
                if (targetIndex < 0 || objects[targetIndex].ConstraintHost is not { } target)
                {
                    lines.Add($"{placement.Tag} ({(int)placement.Type}) → объект {placement.ObjectIndex} не получен " +
                              $"({objects.Count} шт.: {string.Join(", ", objects.Select(o => o.DrawingKind))})");
                    continue;
                }

                object? partnerObject = null;
                if (placement.Partner is { } partner)
                {
                    if (partner.Tag == "not-an-object")
                    {
                        partnerObject = "not-an-object";
                    }
                    else
                    {
                        var partnerIndex = ResolvePartner(objects, partner);
                        if (partnerIndex < 0 || objects[partnerIndex].ConstraintHost is not { } p)
                        {
                            lines.Add($"{placement.Tag} ({(int)placement.Type}) → партнёр " +
                                      $"'{partner.Tag}:{partner.Index}' не разрешился");
                            continue;
                        }

                        partnerObject = partner.AsArray ? BuildPartnerArray(objects, partner) : p;
                    }
                }

                object? axisObject = null;
                if (placement.AxisTag is { } axisTag)
                {
                    var axisIndex = ParseTag(axisTag);
                    if (axisIndex >= 0 && axisIndex < objects.Count)
                    {
                        axisObject = objects[axisIndex].ConstraintHost;
                    }
                }

                var created = CreateConstraint(target, placement, partnerObject, axisObject, readNotes);
                lines.Add($"{placement.Tag} ({(int)placement.Type}) → {created.Summary}");
                step.Data[tag + "_" + placement.Tag] = created.Summary;
            }

            return 0;
        });

        foreach (var note in notes)
        {
            lines.Add("  · " + note);
        }

        _doc!.RebuildDocument();
        return lines;
    }

    private static int ParseTag(string tag)
    {
        var parts = tag.Split(':');
        return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var number)
            ? number
            : -1;
    }

    /// <summary>Resolves a pick to an index in the enumerated objects. A pick by KIND is used where the
    /// enumeration order is not the creation order (a point and a segment are different objects, and
    /// which one comes first is a property of the container, not of the sketch).</summary>
    private static int ResolveObject(IReadOnlyList<SketchObject> objects, ObjectPick pick)
    {
        if (pick.Kind is null)
        {
            return pick.Index >= 0 && pick.Index < objects.Count ? pick.Index : -1;
        }

        var seen = 0;
        for (var i = 0; i < objects.Count; i++)
        {
            if (!objects[i].DrawingKind.Contains(pick.Kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen++ == pick.Occurrence)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ResolvePartner(IReadOnlyList<SketchObject> objects, PartnerPick partner)
    {
        if (partner.Kind is null)
        {
            return partner.Index >= 0 && partner.Index < objects.Count ? partner.Index : -1;
        }

        return ResolveObject(objects, new ObjectPick(-1, 0, partner.Kind, partner.Index));
    }

    /// <summary>Builds the SAFEARRAY form of the partner. DOC: the array is used for
    /// <c>ksCAssociation</c> (a corner dimension binds two segments), and MEASURED: a created
    /// <c>ksCFixedDim</c> exposes its partner as an array as well.</summary>
    private static object BuildPartnerArray(IReadOnlyList<SketchObject> objects, PartnerPick partner)
    {
        var items = new List<object>();
        if (partner.Index >= 0 && partner.Index < objects.Count && objects[partner.Index].ConstraintHost is { } a)
        {
            items.Add(a);
        }

        if (partner.SecondIndex >= 0 && partner.SecondIndex < objects.Count
            && objects[partner.SecondIndex].ConstraintHost is { } b)
        {
            items.Add(b);
        }

        return items.ToArray();
    }

    /// <summary>Four placements of the SAME type, one variable at a time: extruded / not extruded, and
    /// <c>Index</c>+<c>PartnerIndex</c> set / not set. MEASURED: the product route (through the shipped
    /// delivery) creates <c>ksCFixedDim</c> while this probe refuses it, so the difference has to be
    /// NAMED rather than left as "the probe says no".</summary>
    private List<string> FixedDimMatrix(ProbeStep step)
    {
        var results = new List<string>();
        foreach (var extruded in new[] { true, false })
        {
            foreach (var withIndices in new[] { true, false })
            {
                var name = $"k-mx-{(extruded ? "ex" : "no")}-{(withIndices ? "idx" : "plain")}";
                var built = extruded
                    ? BuildRectangleDocument(name, step, out _)
                    : BuildRectangleSketch(name, step, out _);
                var label = $"{(extruded ? "с выдавливанием" : "без выдавливания")}, " +
                            $"{(withIndices ? "Index и PartnerIndex заданы" : "Index и PartnerIndex не заданы")}";
                if (!built)
                {
                    results.Add(label + " → документ не построен");
                    continue;
                }

                if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
                {
                    results.Add(label + " → эскиз не перенесён");
                    continue;
                }

                var notes = new List<string>();
                var summary = InSession(sketch, write: true, notes, (objects, readNotes) =>
                {
                    if (objects.Count < 2 || objects[0].ConstraintHost is not { } host)
                    {
                        return "объекты не получены";
                    }

                    var partner = objects[1].ConstraintHost;
                    var attempt = CreateConstraint(host,
                        new Placement("mx", ksConstraintTypeEnum.ksCFixedDim, 0,
                            new PartnerPick("seg", 1, withIndices ? 0 : -1), withIndices ? 0 : -1),
                        partner, null, readNotes);
                    return attempt.Summary;
                });

                results.Add(label + " → " + (summary ?? "сеанс не открыт"));
            }
        }

        // ПЯТАЯ ПОСТАНОВКА, и она проверяет ГИПОТЕЗУ, а не перебор: поставка создаёт ksCFixedDim
        // ПОСЛЕ ksCFixedLenght и ksCFixedAngle на том же объекте, а проба — на чистом. Если дело в
        // этом, отказ пробы перестанет быть загадкой, а станет измеренным предусловием.
        results.Add("после ksCFixedLenght на том же объекте → " + AfterDimension(step));
        return results;
    }

    /// <summary>The delivery's own order: a dimension already on the object, then <c>ksCFixedDim</c>.</summary>
    private string AfterDimension(ProbeStep step)
    {
        if (!BuildRectangleSketch("k-mx-after", step, out _))
        {
            return "документ не построен";
        }

        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            return "эскиз не перенесён";
        }

        var notes = new List<string>();
        var summary = InSession(sketch, write: true, notes, (objects, readNotes) =>
        {
            if (objects.Count < 2 || objects[0].ConstraintHost is not { } host)
            {
                return "объекты не получены";
            }

            var first = CreateConstraint(host,
                new Placement("lenght", ksConstraintTypeEnum.ksCFixedLenght, 0, null, -1),
                null, null, readNotes);
            readNotes.Add("ksCFixedLenght: " + first.Summary);

            var second = CreateConstraint(host,
                new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, 0,
                    new PartnerPick("seg", 1, -1), -1),
                objects[1].ConstraintHost, null, readNotes);

            var rows = ReadConstraints(host, readNotes);
            return "ksCFixedDim: " + second.Summary + "; в списке " + rows.Count + " записей: " +
                   string.Join(" | ", rows);
        });

        return summary ?? "сеанс не открыт";
    }

    /// <summary>The same rectangle WITHOUT the extrusion: one variable of the matrix above.</summary>
    private bool BuildRectangleSketch(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksLineSeg(-HalfA, -HalfB, HalfA, -HalfB, 1);
        activeEditor.ksLineSeg(HalfA, -HalfB, HalfA, HalfB, 1);
        activeEditor.ksLineSeg(HalfA, HalfB, -HalfA, HalfB, 1);
        activeEditor.ksLineSeg(-HalfA, HalfB, -HalfA, -HalfB, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: прямоугольник {2 * HalfA}×{2 * HalfB} БЕЗ выдавливания.");
        return true;
    }

    /// <summary>The documented pair: <c>ksCFixedDim</c> first, then <c>ksCDimWithVariable</c> on the
    /// SAME object, with the value read back from the stored record.</summary>
    private (string FixedSummary, string VariableSummary, string StoredRow, double? ValueRead,
        string? ExpressionRead, string? VariableRead) DimensionPair(ProbeStep step)
    {
        if (!BuildRectangleDocument("k-pair", step, out _))
        {
            return ("документ не построен", "документ не построен", "нет", null, null, null);
        }

        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            return ("эскиз не перенесён", "эскиз не перенесён", "нет", null, null, null);
        }

        const string variableName = "k_pair_40";
        var notes = new List<string>();
        return InSession(sketch, write: true, notes, (objects, readNotes) =>
        {
            if (objects.Count < 2 || objects[0].ConstraintHost is not { } host)
            {
                return ("объект 0 не получен", "объект 0 не получен", "нет", (double?)null,
                    (string?)null, (string?)null);
            }

            var fixedAttempt = CreateConstraint(host,
                new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, 0,
                    new PartnerPick("seg", 1), 0),
                objects[1].ConstraintHost, null, readNotes);

            var variableAttempt = CreateConstraint(host,
                new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable, 0, null, 0,
                    40d, "40", variableName),
                null, null, readNotes);

            var rows = ReadConstraints(host, readNotes);
            var stored = rows.FirstOrDefault(r => r.Contains("ksCDimWithVariable", StringComparison.Ordinal))
                         ?? "запись ksCDimWithVariable в списке не найдена";

            double? value = null;
            string? expression = null;
            string? variable = null;
            try
            {
                if (host.Constraints is Array array)
                {
                    foreach (var element in array)
                    {
                        if (element is KompasAPI7.IParametriticConstraint c
                            && Api5.SafeInt(() => (int)c.ConstraintType) == 13)
                        {
                            value = Api5.SafeDouble(() => c.Value);
                            expression = Api5.SafeObject(() => c.Expression) as string;
                            variable = Api5.SafeObject(() => c.Variable) as string;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                readNotes.Add("перечитывание размерной записи: " + ex.GetType().Name);
            }

            return (fixedAttempt.Summary, variableAttempt.Summary, stored, value, expression, variable);
        });
    }

    /// <summary>Counts the constraints of every object of the current sketch, inside one session.</summary>
    private string CountConstraintsPerObject(ProbeStep step)
    {
        if (_sketchEntity is null || TransferSketch(_sketchEntity, step) is not KompasAPI7.ISketch sketch)
        {
            return "эскиз не перенесён";
        }

        var notes = new List<string>();
        var counts = InSession(sketch, write: false, notes, (objects, readNotes) =>
            string.Join(", ", objects.Select(o => $"{o.Index}:{ReadConstraints(o.ConstraintHost!, readNotes).Count}")));

        return counts ?? "не прочитано";
    }

    private static string Describe(ObjectPick pick) =>
        pick.Kind is null ? pick.Index.ToString(CultureInfo.InvariantCulture) : $"{pick.Kind}[{pick.Occurrence}]";

    private ConstraintAttempt CreateConstraint(
        KompasAPI7.IDrawingObject1 target,
        Placement placement,
        object? partner,
        object? axis,
        List<string> notes)
    {
        KompasAPI7.IParametriticConstraint? constraint;
        try
        {
            constraint = target.NewConstraint();
        }
        catch (Exception ex)
        {
            return new(null, null, "NewConstraint(): " + ex.GetType().Name + ": " + ex.Message);
        }

        if (constraint is null)
        {
            return new(null, null, "NewConstraint() → null");
        }

        try
        {
            constraint.ConstraintType = placement.Type;
            if (placement.PointIndex >= 0)
            {
                constraint.Index = placement.PointIndex;
            }
            if (partner is not null)
            {
                constraint.Partner = partner;
            }

            if (placement.Partner is { } partnerPick && partnerPick.PartnerPointIndex >= 0)
            {
                constraint.PartnerIndex = partnerPick.PartnerPointIndex;
            }

            if (axis is not null)
            {
                constraint.Axis = (KompasAPI7.IDrawingObject)axis;
            }

            if (placement.Value is { } value)
            {
                constraint.Value = value;
                if (placement.Type is ksConstraintTypeEnum.ksCFixedAngle)
                {
                    constraint.Degrees = (int)value;
                    constraint.Minutes = 0;
                    constraint.Seconds = 0d;
                }
            }

            if (placement.Expression is { } expression)
            {
                constraint.Expression = expression;
            }

            if (placement.Variable is { } variable)
            {
                constraint.Variable = variable;
            }
        }
        catch (Exception ex)
        {
            return new(null, null, "запись полей: " + ex.GetType().Name + ": " + ex.Message);
        }

        bool? created;
        try
        {
            created = constraint.Create();
        }
        catch (Exception ex)
        {
            return new(null, null, "Create(): " + ex.GetType().Name + ": " + ex.Message);
        }

        bool? valid = null;
        try
        {
            valid = constraint.Valid;
        }
        catch (Exception ex)
        {
            notes.Add("Valid не прочитан: " + ex.GetType().Name);
        }

        double? valueRead = null;
        int? degrees = null;
        int? minutes = null;
        double? seconds = null;
        string? expressionRead = null;
        string? variableRead = null;
        try
        {
            valueRead = constraint.Value;
            degrees = constraint.Degrees;
            minutes = constraint.Minutes;
            seconds = constraint.Seconds;
            expressionRead = constraint.Expression;
            variableRead = constraint.Variable;
        }
        catch (Exception ex)
        {
            notes.Add("перечитывание полей шаблона: " + ex.GetType().Name);
        }

        var summary = $"Create={created}, Valid={Api5.Raw(valid)}, " +
                      $"шаблон: Value={Api5.Num(valueRead)}, Deg/Min/Sec={Api5.Raw(degrees)}/{Api5.Raw(minutes)}/" +
                      $"{Api5.Num(seconds)}, Expression={expressionRead ?? "null"}, Variable={variableRead ?? "null"}";

        return new(created, valid, summary, valueRead, degrees, minutes, seconds, expressionRead, variableRead);
    }

    /// <summary>Reads the constraint list of an object. MUST be called inside the session: outside it
    /// the very same object answers <c>null</c> without an exception.</summary>
    private List<string> ReadConstraints(KompasAPI7.IDrawingObject1 host, List<string> notes)
    {
        var rows = new List<string>();
        object? raw;
        try
        {
            raw = host.Constraints;
        }
        catch (Exception ex)
        {
            notes.Add("Constraints: " + ex.GetType().Name + ": " + ex.Message);
            return rows;
        }

        if (raw is null)
        {
            notes.Add("Constraints → null (вне сеанса объект отвечает так же — проверено)");
            return rows;
        }

        if (raw is not Array array)
        {
            notes.Add("Constraints вернул " + Api5.RuntimeName(raw) + " — не массив; не развёрнуто");
            return rows;
        }

        foreach (var element in array)
        {
            if (element is KompasAPI7.IParametriticConstraint constraint)
            {
                rows.Add(DescribeConstraint(constraint));
            }
            else
            {
                rows.Add("элемент " + Api5.RuntimeName(element));
            }
        }

        return rows;
    }

    private static string DescribeConstraint(KompasAPI7.IParametriticConstraint constraint)
    {
        var type = Api5.SafeInt(() => (int)constraint.ConstraintType);
        var index = Api5.SafeInt(() => constraint.Index);
        var partnerIndex = Api5.SafeInt(() => constraint.PartnerIndex);
        var segmentIndex = Api5.SafeInt(() => constraint.SegmentIndex);
        var valid = Api5.SafeBool(() => constraint.Valid);
        var value = Api5.SafeDouble(() => constraint.Value);
        var degrees = Api5.SafeInt(() => constraint.Degrees);
        var minutes = Api5.SafeInt(() => constraint.Minutes);
        var seconds = Api5.SafeDouble(() => constraint.Seconds);
        var expression = Api5.SafeObject(() => constraint.Expression);
        var variable = Api5.SafeObject(() => constraint.Variable);
        var partner = Api5.SafeObject(() => constraint.Partner);

        return $"type={Api5.Raw(type)} ({TypeName(type)}), index={Api5.Raw(index)}, " +
               $"partnerIndex={Api5.Raw(partnerIndex)}, segmentIndex={Api5.Raw(segmentIndex)}, " +
               $"valid={Api5.Raw(valid)}, value={Api5.Num(value)}, " +
               $"deg/min/sec={Api5.Raw(degrees)}/{Api5.Raw(minutes)}/{Api5.Num(seconds)}, " +
               $"expression={Api5.Raw(expression)}, variable={Api5.Raw(variable)}, " +
               $"partner={Api5.RuntimeName(partner)}";
    }

    private static string TypeName(int? type) => type switch
    {
        1 => "ksCFixedPoint",
        2 => "ksCPointOnCurve",
        3 => "ksCHorizontal",
        4 => "ksCVertical",
        5 => "ksCParallel",
        6 => "ksCPerpendicular",
        7 => "ksCEqualLength",
        8 => "ksCEqualRadius",
        9 => "ksCHAlignPoints",
        10 => "ksCVAlignPoints",
        11 => "ksCMergePoints",
        12 => "ksCAssociation",
        13 => "ksCDimWithVariable",
        14 => "ksCFixedDim",
        15 => "ksCTangentTwoCurves",
        16 => "ksCSymmetryTwoPoints",
        17 => "ksCCollinear",
        18 => "ksCFixedAngle",
        19 => "ksCFixedLenght",
        20 => "ksCPointOnCurveMiddle",
        21 => "ksCBisector",
        22 => "ksCConcentricity",
        null => "не прочитан",
        _ => "вне перечисления",
    };

    private string DeleteOne(KompasAPI7.IDrawingObject1 host, List<string> notes)
    {
        var before = ReadConstraints(host, notes).Count;
        object? raw;
        try
        {
            raw = host.Constraints;
        }
        catch (Exception ex)
        {
            return "Constraints: " + ex.GetType().Name;
        }

        if (raw is not Array array || array.Length == 0)
        {
            return $"список пуст или не развёрнут — удалять нечего (было {before})";
        }

        if (array.GetValue(0) is not KompasAPI7.IParametriticConstraint first)
        {
            return "элемент списка не отвечает IParametriticConstraint";
        }

        var deleted = Api5.SafeBool(() => first.Delete());
        var after = ReadConstraints(host, notes).Count;
        return $"Delete()={Api5.Raw(deleted)}, было {before}, стало {after}";
    }

    private string DeleteAll(KompasAPI7.IDrawingObject1 host, List<string> notes)
    {
        var before = ReadConstraints(host, notes).Count;
        var result = Api5.SafeBool(() => host.DeleteConstraints());
        var after = ReadConstraints(host, notes).Count;
        return $"DeleteConstraints()={Api5.Raw(result)}, было {before}, стало {after}";
    }

    // ═══════════════════════════════════════════════════════════════ sessions ══

    /// <summary>Runs <paramref name="action"/> with the sketch's objects enumerated inside ONE session
    /// and closes it in <c>finally</c> — on the failure path too. <paramref name="write"/> chooses
    /// <c>BeginEdit()</c> (writing) over <c>BeginEditEx(true)</c> (reading): they are different
    /// members, not overloads, and a read must not be given the right to change the model.</summary>
    private T? InSession<T>(
        KompasAPI7.ISketch sketch,
        bool write,
        List<string> notes,
        Func<List<SketchObject>, List<string>, T> action)
    {
        KompasAPI7.IFragmentDocument? fragment;
        try
        {
            fragment = write ? sketch.BeginEdit() : sketch.BeginEditEx(true);
        }
        catch (COMException ex)
        {
            notes.Add($"BeginEdit отказал: HRESULT 0x{ex.HResult:X8}");
            return default;
        }

        if (fragment is null)
        {
            notes.Add("BeginEdit → null: вход в эскиз не состоялся");
            return default;
        }

        try
        {
            var objects = EnumerateInFragment(fragment, notes);
            return action(objects, notes);
        }
        catch (Exception ex)
        {
            notes.Add("сеанс: " + ex.GetType().Name + ": " + ex.Message);
            return default;
        }
        finally
        {
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add("EndEdit(): " + ex.GetType().Name);
            }
        }
    }

    private sealed record SketchObject(
        int Index,
        string DrawingKind,
        string Geometry,
        KompasAPI7.IDrawingObject Drawing,
        KompasAPI7.IDrawingObject1? ConstraintHost);

    private List<SketchObject> EnumerateInFragment(KompasAPI7.IFragmentDocument fragment, List<string> notes)
    {
        var rows = new List<SketchObject>();
        KompasAPI7.IViews? views;
        try
        {
            views = fragment.ViewsAndLayersManager?.Views;
        }
        catch (Exception ex)
        {
            notes.Add("ViewsAndLayersManager.Views: " + ex.GetType().Name);
            return rows;
        }

        if (views is null)
        {
            notes.Add("Views → null");
            return rows;
        }

        var count = Api5.SafeInt(() => views.Count) ?? 0;
        notes.Add($"видов во фрагменте: {count}");

        for (var v = 0; v < count; v++)
        {
            KompasAPI7.IView? view;
            try
            {
                view = views.get_View(v);
            }
            catch (Exception ex)
            {
                notes.Add($"вид {v}: " + ex.GetType().Name);
                continue;
            }

            if (view is null)
            {
                notes.Add($"вид {v}: null");
                continue;
            }

            notes.Add($"вид {v}: ObjectCount={Api5.Raw(Api5.SafeInt(() => view.ObjectCount))}");

            if (view is not KompasAPI7.IDrawingContainer container)
            {
                notes.Add($"вид {v}: не отвечает QI(IDrawingContainer)");
                continue;
            }

            object? raw;
            try
            {
                raw = container.get_Objects(new[] { (int)DrawingObjectTypeEnum.ksAllObj });
            }
            catch (Exception ex)
            {
                notes.Add($"вид {v}: GetObjects бросил " + ex.GetType().Name);
                continue;
            }

            if (raw is not Array array)
            {
                notes.Add($"вид {v}: GetObjects вернул " + Api5.RuntimeName(raw));
                continue;
            }

            foreach (var item in array)
            {
                if (item is not KompasAPI7.IDrawingObject drawing)
                {
                    notes.Add($"элемент {rows.Count}: не отвечает IDrawingObject (" + Api5.RuntimeName(item) + ")");
                    continue;
                }

                var host = drawing as KompasAPI7.IDrawingObject1;
                rows.Add(new SketchObject(rows.Count, SafeKind(drawing), DescribeGeometry(drawing), drawing, host));
            }
        }

        return rows;
    }

    private static string SafeKind(KompasAPI7.IDrawingObject drawing)
    {
        try
        {
            return drawing.DrawingObjectType.ToString();
        }
        catch (Exception)
        {
            return "не прочитан";
        }
    }

    private static string DescribeGeometry(KompasAPI7.IDrawingObject drawing)
    {
        if (drawing is KompasAPI7.ILineSegment segment)
        {
            try
            {
                return $"отрезок ({Api5.Num(segment.X1)},{Api5.Num(segment.Y1)})—" +
                       $"({Api5.Num(segment.X2)},{Api5.Num(segment.Y2)}), длина {Api5.Num(segment.Length)}, " +
                       $"угол {Api5.Num(segment.Angle)}";
            }
            catch (Exception ex)
            {
                return "отрезок, координаты не прочитаны: " + ex.GetType().Name;
            }
        }

        if (drawing is KompasAPI7.ICircle circle)
        {
            try
            {
                return $"окружность ({Api5.Num(circle.Xc)},{Api5.Num(circle.Yc)}) R{Api5.Num(circle.Radius)}";
            }
            catch (Exception ex)
            {
                return "окружность, параметры не прочитаны: " + ex.GetType().Name;
            }
        }

        return "геометрия не описана";
    }

    private sealed record SegmentReading(double X1, double Y1, double X2, double Y2, double Angle, double Length);

    /// <summary>Reads the first segment of the sketch — INSIDE the session, because outside it the
    /// same object answers zeros without an exception.</summary>
    private SegmentReading? ReadFirstSegment(KompasAPI7.ISketch sketch, bool write, List<string> notes) =>
        ReadSegment(sketch, 0, notes);

    private SegmentReading? ReadSegment(KompasAPI7.ISketch sketch, int index, List<string> notes) =>
        InSession(sketch, write: false, notes, (objects, _) =>
        {
            if (index >= objects.Count || objects[index].Drawing is not KompasAPI7.ILineSegment segment)
            {
                return null;
            }

            return new SegmentReading(segment.X1, segment.Y1, segment.X2, segment.Y2, segment.Angle, segment.Length);
        });

    // ═══════════════════════════════════════════════════════════════ documents ══

    private bool Build(DocKind kind, string name, ProbeStep step) => kind switch
    {
        DocKind.Rectangle => BuildRectangleDocument(name, step, out _),
        DocKind.TwoSegment => BuildTwoSegmentDocument(name, step, out _, out _),
        DocKind.Tilted => BuildTiltedDocument(name, step, out _),
        DocKind.TwoCircle => BuildTwoCircleDocument(name, step, out _),
        DocKind.Collinear => BuildCollinearDocument(name, step, out _),
        DocKind.Point => BuildPointDocument(name, step, out _),
        _ => false,
    };

    /// <summary>The reference rectangle: 4 segments of a <c>2a × 2b</c> rectangle centred at the
    /// origin, extruded by <c>h</c>. Analytical volume <c>4·a·b·h = 12000</c> mm³.</summary>
    private bool BuildRectangleDocument(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksLineSeg(-HalfA, -HalfB, HalfA, -HalfB, 1);
        activeEditor.ksLineSeg(HalfA, -HalfB, HalfA, HalfB, 1);
        activeEditor.ksLineSeg(HalfA, HalfB, -HalfA, HalfB, 1);
        activeEditor.ksLineSeg(-HalfA, HalfB, -HalfA, -HalfB, 1);
        definition.EndEdit();

        if (!Extrude(sketch, part, step, name))
        {
            return false;
        }

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: прямоугольник {2 * HalfA}×{2 * HalfB}, выдавливание {Thickness}, " +
                     $"объём {Api5.Num(Api5.Volume(part))} (аналитика {Api5.Num(RectangleVolume)}).");
        return true;
    }

    /// <summary>Two segments sharing the origin: one along X, one at 30°.</summary>
    private bool BuildTwoSegmentDocument(string name, ProbeStep step, out ksDocument2D editor,
        out List<int> references, bool closeEditor = true)
    {
        editor = null!;
        references = new List<int>();
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        references.Add(activeEditor.ksLineSeg(0d, 0d, TiltLength, 0d, 1));
        references.Add(activeEditor.ksLineSeg(0d, 0d, TiltX, TiltY, 1));
        if (closeEditor)
        {
            definition.EndEdit();
        }

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: два отрезка из начала координат — по X и под {TiltAngleDeg}°" +
                     (closeEditor ? ", редактор закрыт" : ", редактор ОСТАВЛЕН ОТКРЫТЫМ") + ".");
        return true;
    }

    private bool BuildTiltedDocument(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksLineSeg(0d, 0d, TiltX, TiltY, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: отрезок 25 мм под {TiltAngleDeg}°, конец " +
                     $"({Api5.Num(TiltX)}, {Api5.Num(TiltY)}).");
        return true;
    }

    private bool BuildTwoCircleDocument(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksCircle(0d, 0d, 20d, 1);
        activeEditor.ksCircle(0d, 0d, 10d, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: две окружности R20 и R10 с общим центром.");
        return true;
    }

    /// <summary>Two collinear segments on the X axis, separated by a gap — the placement
    /// <c>ksCCollinear</c> needs.</summary>
    private bool BuildCollinearDocument(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksLineSeg(0d, 0d, 10d, 0d, 1);
        activeEditor.ksLineSeg(20d, 0d, 30d, 0d, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: два отрезка на оси X с разрывом — соосная постановка.");
        return true;
    }

    /// <summary>Two segments and two standalone POINTS: the placement the point-on-curve types need.
    /// MEASURED: with a segment as the object those two types answer <c>Create=False</c>, so the object
    /// they take is a point, and the experiment is repeated with one.</summary>
    private bool BuildPointDocument(string name, ProbeStep step, out ksDocument2D editor)
    {
        editor = null!;
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksLineSeg(0d, 0d, TiltLength, 0d, 1);
        activeEditor.ksLineSeg(0d, 0d, TiltX, TiltY, 1);
        var first = activeEditor.ksPoint(5d, 5d, 1);
        var second = activeEditor.ksPoint(10d, 10d, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        step.Observe($"«{name}»: два отрезка и две точки, ссылки точек {first}, {second}.");
        return true;
    }

    private bool NewSketch(string name, ProbeStep step, out ksDocument3D doc, out ksPart part,
        out ksEntity sketch, out ksSketchDefinition definition)
    {
        doc = (ksDocument3D)_app.Document3D();
        part = null!;
        sketch = null!;
        definition = null!;

        if (doc.Create(true, true) != true)
        {
            step.Observe($"Документ «{name}» не создан.");
            return false;
        }

        part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || part.NewEntity(Api5.Sketch) is not ksEntity newSketch
            || newSketch.GetDefinition() is not ksSketchDefinition newDefinition)
        {
            step.Observe($"Эскиз «{name}» не создан.");
            return false;
        }

        newSketch.name = name;
        newDefinition.SetPlane(plane);
        newSketch.Create();

        sketch = newSketch;
        definition = newDefinition;
        return true;
    }

    private static bool Extrude(ksEntity sketch, ksPart part, ProbeStep step, string name)
    {
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe($"Выдавливание для «{name}» не создано.");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, Thickness, 0d, false);
        if (Api5.SafeBool(extrusion.Create) != true)
        {
            step.Observe($"Выдавливание «{name}»: Create() → false.");
            return false;
        }

        return true;
    }

    private bool FindSketchInReopened(ProbeStep step)
    {
        if (_part is null)
        {
            return false;
        }

        foreach (var objType in new short[] { Api5.Sketch, 15, 16 })
        {
            if (_part.EntityCollection(objType) is not ksEntityCollection entities)
            {
                continue;
            }

            var count = Api5.SafeInt(() => entities.GetCount()) ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (entities.GetByIndex(i) is ksEntity entity && entity.GetDefinition() is ksSketchDefinition)
                {
                    _sketchEntity = entity;
                    step.Data["reopen_found_by_objtype"] = objType;
                    step.Data["reopen_sketch_name"] = entity.name;
                    return true;
                }
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════ helpers ══

    private KompasAPI7.ISketch? TransferSketch(ksEntity sketch, ProbeStep step)
    {
        try
        {
            var transferred = _app.TransferInterface(sketch, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is KompasAPI7.ISketch direct)
            {
                step.Data["sketch_transfer"] = "ISketch напрямую";
                return direct;
            }

            if (transferred is KompasAPI7.IModelObject modelObject)
            {
                var qualified = modelObject as KompasAPI7.ISketch;
                step.Data["sketch_transfer"] = qualified is null
                    ? "IModelObject без ISketch"
                    : "IModelObject → ISketch";
                return qualified;
            }

            step.Data["sketch_transfer"] = Api5.RuntimeName(transferred);
            return null;
        }
        catch (Exception ex)
        {
            step.Data["sketch_transfer_error"] = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private int? StateOfCurrent(ProbeStep step, string tag)
    {
        if (_sketchEntity is null || TransferSketch(_sketchEntity, step) is not KompasAPI7.ISketch sketch)
        {
            return null;
        }

        var raw = Api5.SafeInt(() => (int)sketch.ConstraintsState);
        step.Data[tag + "_state_raw"] = raw;
        step.Data[tag + "_state_name"] = raw is null ? null : NativeStateName(raw.Value);
        return raw;
    }

    private static string? NativeStateName(int raw) => raw switch
    {
        0 => "ksStateUnknown",
        1 => "ksStateWellConstrained",
        2 => "ksStateUnderConstrained",
        3 => "ksStateUnresolvedRedundancy",
        _ => null,
    };

    private static string Interpret(int? raw) => raw switch
    {
        0 => "unknown",
        1 => "fully_defined",
        2 => "under_defined",
        3 => "needs_attention",
        null => "не прочитано",
        _ => "значение вне перечисления",
    };

    private static uint[] KompasIds() =>
        System.Diagnostics.Process.GetProcessesByName("KOMPAS")
            .Select(p => (uint)p.Id)
            .ToArray();

    private static int? WaitForNewProcess(uint[] before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var appeared = KompasIds().Except(before).ToArray();
            if (appeared.Length > 0)
            {
                return (int)appeared[0];
            }

            Thread.Sleep(250);
        }

        return null;
    }

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static bool? TryBool(Func<bool> call)
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

    /// <summary>The IDispatch ABI, declared by hand so a member absent from the shipped wrapper can be
    /// reached through the interface's own automation entry point.</summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchRaw
    {
        int GetTypeInfoCount(out int pctinfo);

        int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);

        // The ABI lays the name list out as an array of LPOLESTR POINTERS, and the id list as a
        // caller-allocated DISPID array. Both are passed as raw pointers: a `string[]` parameter is
        // marshalled by the runtime in a way that silently produced an EMPTY name here (measured:
        // GetIDsOfNames answered S_OK with dispid 0 instead of resolving the member).
        int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId);

        int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags, ref DispatchParams pDispParams,
            IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchParams
    {
        public IntPtr Args;
        public IntPtr NamedArgs;
        public int ArgCount;
        public int NamedArgCount;
    }

    /// <summary>VARIANT as the automation ABI lays it out on x64 (16 bytes: vt + padding + value).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VariantRaw
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
    }

    /// <summary>The subset of <c>ksConstraintTypeEnum</c> the API5 negative control needs, duplicated
    /// so the control does not depend on the API7 vocabulary.</summary>
    private enum LocalConstraintType
    {
        Horizontal = 3,
    }

    // ═══════════════════════ diametral / radial / angular dimensions (order §2) ══

    /// <summary>The three further dimension kinds of the order. Each has its OWN documented collection
    /// on <c>ISymbols2DContainer</c> and its own way of naming the measured object, so the route is
    /// written once per kind rather than forced through a common shape.</summary>
    private enum DimKind
    {
        Diametral,
        Radial,
        Angular,
    }

    /// <summary>One placement of a non-linear dimension. Every field is an observation: <c>null</c>
    /// means the member did not answer, which is not the same as "false".</summary>
    private sealed record DimKindOutcome(
        double? NominalBefore,
        double? NominalAfter,
        bool? DimensionValid,
        bool AssociationCreated,
        bool FixedDimCreated,
        bool ValueDimCreated,
        int MergePoints,
        string? DimensionRef,
        string? ReadBack,
        string? Failure);

    /// <summary>K.19 — a diametral dimension on a circle; its value is the DIAMETER.</summary>
    private void DiametralDimension() => DimensionKindStep(
        DimKind.Diametral,
        "K.19",
        "Диаметральный размер: окружность R10, значение 30",
        "Создаётся ли диаметральный размер документированным маршрутом "
        + "(ISymbols2DContainer.DiametralDimensions.Add() → BaseObject → Update()), читается ли "
        + "NominalValue как ДИАМЕТР и двигает ли заданное значение геометрию?",
        BuildCircleDocument,
        ReadCircleRadius,
        value: 30d,
        expectedGeometry: 15d,
        geometryLabel: "радиус окружности");

    /// <summary>K.20 — a radial dimension on an arc; the help claims <c>NominalValue</c> returns the
    /// DIAMETER even here, which is what this step measures rather than repeats.</summary>
    private void RadialDimension() => DimensionKindStep(
        DimKind.Radial,
        "K.20",
        "Радиальный размер: дуга R10, значение 15",
        "Создаётся ли радиальный размер маршрутом (ISymbols2DContainer.RadialDimensions.Add() → "
        + "BaseObject → Update()), что возвращает NominalValue у РАДИАЛЬНОГО размера (справка пишет "
        + "«диаметр») и двигает ли заданное значение радиус дуги?",
        BuildArcDocument,
        ReadArcRadius,
        value: 15d,
        expectedGeometry: 15d,
        geometryLabel: "радиус дуги");

    /// <summary>K.21 — an angular dimension between two segments; its value is the ANGLE.</summary>
    private void AngularDimension() => DimensionKindStep(
        DimKind.Angular,
        "K.21",
        "Угловой размер: два отрезка под 30°, значение 45",
        "Создаётся ли угловой размер маршрутом (ISymbols2DContainer.AngleDimensions.Add("
        + "ksDrADimension) → BaseObject1/BaseObject2 → Update()) и двигает ли заданное значение угол "
        + "между отрезками?",
        BuildTwoSegmentForAngle,
        ReadAngleBetweenSegments,
        value: 45d,
        expectedGeometry: 45d,
        geometryLabel: "угол между отрезками, град");

    /// <summary>One kind, measured THREE times, each on a FRESH document: with the value and without
    /// the association (the candidate recipe), the discriminating control (same placement, no value),
    /// and with the association (the step the LINEAR kind needs) so that its necessity for this kind is
    /// measured instead of assumed.</summary>
    private void DimensionKindStep(
        DimKind kind,
        string stepId,
        string title,
        string what,
        Func<string, ProbeStep, bool> build,
        Func<KompasAPI7.ISketch, List<string>, double?> readGeometry,
        double value,
        double expectedGeometry,
        string geometryLabel)
    {
        var step = _report.Begin(stepId, title, what);
        try
        {
            var slug = kind.ToString().ToLowerInvariant();

            var main = RunPlacement(kind, step, "k-" + slug + "-value", build, readGeometry, value,
                writeValue: true, withAssociation: false);
            var control = RunPlacement(kind, step, "k-" + slug + "-novalue", build, readGeometry, value,
                writeValue: false, withAssociation: false);
            var withAssociation = RunPlacement(kind, step, "k-" + slug + "-assoc", build, readGeometry,
                value, writeValue: true, withAssociation: true);

            var outcome = main.Outcome;
            step.Data["nominal_before"] = Api5.Num(outcome?.NominalBefore);
            step.Data["nominal_after_value"] = Api5.Num(outcome?.NominalAfter);
            step.Data["dimension_valid"] = Api5.Raw(outcome?.DimensionValid);
            step.Data["association_created"] = outcome?.AssociationCreated;
            step.Data["fixed_dim"] = outcome?.FixedDimCreated;
            step.Data["dim_with_variable"] = outcome?.ValueDimCreated;
            step.Data["read_back"] = outcome?.ReadBack;
            step.Data["geometry_before"] = Api5.Num(main.GeometryBefore);
            step.Data["geometry_after_value"] = Api5.Num(main.GeometryAfter);
            step.Data["geometry_control_before"] = Api5.Num(control.GeometryBefore);
            step.Data["geometry_control_without_value"] = Api5.Num(control.GeometryAfter);
            step.Data["geometry_with_association"] = Api5.Num(withAssociation.GeometryAfter);
            step.Data["association_attempt_create"] = withAssociation.Outcome?.AssociationCreated;
            step.Data["expected_geometry"] = expectedGeometry;
            step.Data["geometry_label"] = geometryLabel;
            step.Data["value_written"] = value;
            step.Data["notes"] = main.Notes.Concat(control.Notes).Concat(withAssociation.Notes).ToList();

            foreach (var note in main.Notes.Concat(control.Notes).Concat(withAssociation.Notes))
            {
                step.Observe(note);
            }

            if (outcome?.Failure is { } failure)
            {
                step.Unknown($"Размер не поставлен: {failure}. Это измеренный факт о маршруте, а не "
                             + "об объекте: он выносится заказчику.");
                return;
            }

            step.Observe($"{geometryLabel}: до {Api5.Num(main.GeometryBefore)}, после значения {value} — " +
                         $"{Api5.Num(main.GeometryAfter)}; контроль БЕЗ значения: " +
                         $"{Api5.Num(control.GeometryBefore)} → {Api5.Num(control.GeometryAfter)}; " +
                         $"с ассоциацией — {Api5.Num(withAssociation.GeometryAfter)}");

            if (main.GeometryBefore is null || main.GeometryAfter is null)
            {
                step.Unknown("Геометрия не прочитана до и после — контроль не различает исходы. "
                             + "Дефект прибора.");
                return;
            }

            var moved = Math.Abs(main.GeometryAfter.Value - main.GeometryBefore.Value) > 1e-6;
            var hit = Math.Abs(main.GeometryAfter.Value - expectedGeometry) < 1e-4;
            var controlStable = control.GeometryAfter is not null && control.GeometryBefore is not null
                                && Math.Abs(control.GeometryAfter.Value - control.GeometryBefore.Value) < 1e-6;

            if (hit && controlStable)
            {
                step.Pass($"Размер УПРАВЛЯЕТ геометрией БЕЗ ассоциации: {geometryLabel} " +
                          $"{Api5.Num(main.GeometryBefore)} → {Api5.Num(main.GeometryAfter)} при значении " +
                          $"{value} (ожидание {Api5.Num(expectedGeometry)}), а БЕЗ значения геометрия не " +
                          $"двинулась ({Api5.Num(control.GeometryBefore)} → {Api5.Num(control.GeometryAfter)}).");
            }
            else if (hit && !controlStable)
            {
                step.Unknown($"Геометрия пришла к значению ({Api5.Num(main.GeometryAfter)}), но сдвинулась " +
                             $"и БЕЗ значения ({Api5.Num(control.GeometryBefore)} → " +
                             $"{Api5.Num(control.GeometryAfter)}): движение НЕ приписывается значению. " +
                             "Измеренный факт, выносится заказчику.");
            }
            else if (moved)
            {
                step.Unknown($"Геометрия сдвинулась, но не до значения: {Api5.Num(main.GeometryBefore)} → " +
                             $"{Api5.Num(main.GeometryAfter)} при значении {value} (ожидание " +
                             $"{Api5.Num(expectedGeometry)}). Измеренный факт, а не совпадение.");
            }
            else
            {
                step.Unknown($"Размер создан, но геометрией НЕ управляет: {geometryLabel} " +
                             $"{Api5.Num(main.GeometryBefore)} → {Api5.Num(main.GeometryAfter)} при " +
                             $"значении {value}. Измеренный факт, выносится заказчику.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут вида не измерен: " + ex.Message);
        }
    }

    /// <summary>One placement on a FRESH document of its own, with the geometry read before and after
    /// the rebuild. The three placements of a kind are kept apart on purpose: sharing one document would
    /// let the first placement's dimension drive the geometry the second one measures.</summary>
    private (DimKindOutcome? Outcome, double? GeometryBefore, double? GeometryAfter, List<string> Notes)
        RunPlacement(
            DimKind kind,
            ProbeStep step,
            string tag,
            Func<string, ProbeStep, bool> build,
            Func<KompasAPI7.ISketch, List<string>, double?> readGeometry,
            double value,
            bool writeValue,
            bool withAssociation)
    {
        var notes = new List<string>();
        if (!build(tag, step))
        {
            notes.Add($"документ «{tag}» не построен");
            return (null, null, null, notes);
        }

        if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
        {
            notes.Add("эскиз не перенесён в API7");
            return (null, null, null, notes);
        }

        var before = readGeometry(sketch, notes);
        var outcome = PlaceKindDimension(kind, sketch, value, writeValue, withAssociation, notes);

        _doc!.RebuildDocument();

        double? after = null;
        if (TransferSketch(_sketchEntity!, step) is KompasAPI7.ISketch again)
        {
            after = readGeometry(again, notes);
        }

        return (outcome, before, after, notes);
    }

    /// <summary>Places ONE non-linear dimension inside one write session, by the documented route of
    /// its own collection. <paramref name="withAssociation"/> adds the <c>ksCAssociation</c> step the
    /// LINEAR dimension needs, so that its necessity for these kinds is measured rather than assumed;
    /// <paramref name="writeValue"/> adds <c>ksCFixedDim</c> + <c>ksCDimWithVariable</c>.</summary>
    private DimKindOutcome PlaceKindDimension(
        DimKind kind,
        KompasAPI7.ISketch sketch,
        double value,
        bool writeValue,
        bool withAssociation,
        List<string> notes)
    {
        KompasAPI7.IFragmentDocument? fragment = null;
        try
        {
            fragment = sketch.BeginEdit();
        }
        catch (COMException ex)
        {
            notes.Add($"BeginEdit отказал: 0x{ex.HResult:X8}");
        }

        if (fragment is null)
        {
            return new(null, null, null, false, false, false, 0, null, null,
                "вход в эскиз не состоялся");
        }

        try
        {
            var objects = EnumerateInFragment(fragment, notes);
            if (fragment.ViewsAndLayersManager?.Views?.get_View(0)
                is not KompasAPI7.ISymbols2DContainer symbols)
            {
                return new(null, null, null, false, false, false, 0, null, null,
                    "вид не отвечает QI(ISymbols2DContainer)");
            }

            KompasAPI7.IDrawingObject? dimension;
            var baseObjects = new List<KompasAPI7.IDrawingObject>();
            var partnerObjects = new List<object>();

            switch (kind)
            {
                case DimKind.Diametral:
                {
                    var circle = objects.FirstOrDefault(o => o.Drawing is KompasAPI7.ICircle);
                    if (circle is null)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            "окружность в эскизе не найдена");
                    }

                    baseObjects.Add(circle.Drawing);
                    partnerObjects.Add((object?)circle.ConstraintHost ?? circle.Drawing);
                    var added = symbols.DiametralDimensions.Add();
                    if (added is null)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            "IDiametralDimensions.Add() → null");
                    }

                    added.BaseObject = circle.Drawing;
                    notes.Add($"DiametralDimensions.Add(): BaseObject = окружность " +
                              $"({circle.Geometry}); Update() → {Api5.Raw(Api5.SafeBool(() => added.Update()))}");
                    dimension = added;
                    break;
                }

                case DimKind.Radial:
                {
                    var arc = objects.FirstOrDefault(o => o.Drawing is KompasAPI7.IArc)
                              ?? objects.FirstOrDefault(o => o.Drawing is KompasAPI7.ICircle);
                    if (arc is null)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            "дуга или окружность в эскизе не найдена");
                    }

                    baseObjects.Add(arc.Drawing);
                    partnerObjects.Add((object?)arc.ConstraintHost ?? arc.Drawing);
                    var added = symbols.RadialDimensions.Add();
                    if (added is null)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            "IRadialDimensions.Add() → null");
                    }

                    added.BaseObject = arc.Drawing;
                    notes.Add($"RadialDimensions.Add(): BaseObject = {arc.DrawingKind} " +
                              $"({arc.Geometry}); Update() → {Api5.Raw(Api5.SafeBool(() => added.Update()))}");
                    dimension = added;
                    break;
                }

                case DimKind.Angular:
                {
                    var segments = objects.Where(o => o.Drawing is KompasAPI7.ILineSegment).Take(2).ToList();
                    if (segments.Count < 2)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            $"отрезков в эскизе {segments.Count}, угловому размеру нужно два");
                    }

                    baseObjects.Add(segments[0].Drawing);
                    baseObjects.Add(segments[1].Drawing);
                    partnerObjects.Add((object?)segments[0].ConstraintHost ?? segments[0].Drawing);
                    partnerObjects.Add((object?)segments[1].ConstraintHost ?? segments[1].Drawing);
                    var added = symbols.AngleDimensions.Add(DrawingObjectTypeEnum.ksDrADimension);
                    if (added is null)
                    {
                        return new(null, null, null, false, false, false, 0, null, null,
                            "IAngleDimensions.Add(ksDrADimension) → null");
                    }

                    added.BaseObject1 = segments[0].Drawing;
                    added.BaseObject2 = segments[1].Drawing;
                    notes.Add($"AngleDimensions.Add(ksDrADimension): BaseObject1 = отрезок " +
                              $"[{segments[0].Index}], BaseObject2 = отрезок [{segments[1].Index}]; " +
                              $"Update() → {Api5.Raw(Api5.SafeBool(() => added.Update()))}");
                    dimension = added;
                    break;
                }

                default:
                    return new(null, null, null, false, false, false, 0, null, null, "неизвестный вид");
            }

            var valid = Api5.SafeBool(() => dimension.Valid);
            var reference = Api5.SafeInt(() => dimension.Reference);
            var nominalBefore = ReadDimensionNominal(dimension, notes, "номинал сразу после создания");

            var associationCreated = false;
            var fixedCreated = false;
            var valueCreated = false;
            var mergePoints = 0;
            var readBack = (string?)null;

            if (writeValue)
            {
                if (dimension as KompasAPI7.IDrawingObject1 is not { } host)
                {
                    return new(nominalBefore, null, valid, false, false, false, 0,
                        reference?.ToString(), null,
                        "объект-размер не отвечает QI(IDrawingObject1): NewConstraint() недоступен");
                }

                if (withAssociation)
                {
                    var association = CreateConstraint(host,
                        new Placement("association", ksConstraintTypeEnum.ksCAssociation, -1, null, -1),
                        partnerObjects.ToArray(), null, notes);
                    associationCreated = association.Created == true;
                    notes.Add("ksCAssociation: " + association.Summary);
                }
                else
                {
                    notes.Add("ksCAssociation НЕ запрашивалась — этим измеряется, нужна ли она виду");
                }

                var fixedDim = CreateConstraint(host,
                    new Placement("fixed_dim", ksConstraintTypeEnum.ksCFixedDim, -1, null, -1),
                    null, null, notes);
                fixedCreated = fixedDim.Created == true;
                notes.Add("ksCFixedDim: " + fixedDim.Summary);

                var valueDim = CreateConstraint(host,
                    new Placement("dim_with_variable", ksConstraintTypeEnum.ksCDimWithVariable, -1, null, -1,
                        value, value.ToString(CultureInfo.InvariantCulture)),
                    null, null, notes);
                valueCreated = valueDim.Created == true;
                notes.Add("ksCDimWithVariable: " + valueDim.Summary);

                var rows = ReadConstraints(host, notes);
                mergePoints = rows.Count(r => r.Contains("ksCMergePoints", StringComparison.Ordinal));
                readBack = rows.Count == 0 ? "список пуст или не развёрнут" : string.Join(" | ", rows);
            }

            var nominalAfter = ReadDimensionNominal(dimension, notes, "номинал после записи значения");

            return new(nominalBefore, nominalAfter, valid, associationCreated, fixedCreated, valueCreated,
                mergePoints, reference?.ToString(), readBack, null);
        }
        catch (Exception ex)
        {
            return new(null, null, null, false, false, false, 0, null, null,
                ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add("EndEdit(): " + ex.GetType().Name);
            }
        }
    }

    /// <summary>Reads <c>IDimensionText::NominalValue</c> — the value member of EVERY dimension kind.
    /// <c>null</c> is "not read" and is named, never zero.</summary>
    private static double? ReadDimensionNominal(KompasAPI7.IDrawingObject dimension, List<string> notes,
        string moment)
    {
        if (dimension is not KompasAPI7.IDimensionText text)
        {
            notes.Add($"{moment}: объект-размер не отвечает QI(IDimensionText)");
            return null;
        }

        var value = Api5.SafeDouble(() => text.NominalValue);
        notes.Add($"{moment}: NominalValue = {Api5.Num(value)}");
        return value;
    }

    /// <summary>A single circle R10 at the origin — the placement a diametral dimension needs.</summary>
    private bool BuildCircleDocument(string name, ProbeStep step)
    {
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksCircle(0d, 0d, 10d, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        step.Observe($"«{name}»: окружность R10 (Ø20) с центром в начале координат.");
        return true;
    }

    /// <summary>An arc R10 at the origin from 0° to 90° — the placement a radial dimension needs.
    /// An ARC and not a circle, so the radial dimension is measured on the object the order names for
    /// it; a circle would make the measurement describe the other admissible base object.</summary>
    private bool BuildArcDocument(string name, ProbeStep step)
    {
        if (!NewSketch(name, step, out var doc, out var part, out var sketch, out var definition))
        {
            return false;
        }

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        activeEditor.ksArcByAngle(0d, 0d, 10d, 0d, 90d, 1, 1);
        definition.EndEdit();

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        step.Observe($"«{name}»: дуга R10 с центром в начале координат, 0°…90°.");
        return true;
    }

    private bool BuildTwoSegmentForAngle(string name, ProbeStep step) =>
        BuildTwoSegmentDocument(name, step, out _, out _);

    /// <summary>The radius of the first circle of the sketch, read INSIDE a session.</summary>
    private double? ReadCircleRadius(KompasAPI7.ISketch sketch, List<string> notes) =>
        InSession(sketch, write: false, notes, (objects, _) =>
        {
            foreach (var o in objects)
            {
                if (o.Drawing is KompasAPI7.ICircle circle)
                {
                    return Api5.SafeDouble(() => circle.Radius);
                }
            }

            return null;
        });

    /// <summary>The radius of the first arc of the sketch, read INSIDE a session.</summary>
    private double? ReadArcRadius(KompasAPI7.ISketch sketch, List<string> notes) =>
        InSession(sketch, write: false, notes, (objects, _) =>
        {
            foreach (var o in objects)
            {
                if (o.Drawing is KompasAPI7.IArc arc)
                {
                    return Api5.SafeDouble(() => arc.Radius);
                }
            }

            return null;
        });

    /// <summary>The angle between the first two segments, in degrees, from their OWN direction
    /// vectors — not from <c>ILineSegment.Angle</c>, whose zero direction is a separate question.</summary>
    private double? ReadAngleBetweenSegments(KompasAPI7.ISketch sketch, List<string> notes) =>
        InSession(sketch, write: false, notes, (objects, _) =>
        {
            var segments = objects.Where(o => o.Drawing is KompasAPI7.ILineSegment).Take(2).ToList();
            if (segments.Count < 2)
            {
                return null;
            }

            var a = Direction((KompasAPI7.ILineSegment)segments[0].Drawing);
            var b = Direction((KompasAPI7.ILineSegment)segments[1].Drawing);
            if (a is null || b is null)
            {
                return null;
            }

            var dot = a.Value.X * b.Value.X + a.Value.Y * b.Value.Y;
            var cross = a.Value.X * b.Value.Y - a.Value.Y * b.Value.X;
            return (double?)(Math.Atan2(Math.Abs(cross), dot) * 180d / Math.PI);
        });

    private static (double X, double Y)? Direction(KompasAPI7.ILineSegment segment)
    {
        try
        {
            var dx = segment.X2 - segment.X1;
            var dy = segment.Y2 - segment.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            return length < 1e-12 ? null : (dx / length, dy / length);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
