using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;

namespace KompasMcp.Api7Probe;

/// <summary>Probe S — sketch definition: is the "+ / − / !" status readable from the API, and does it
/// tell apart states prepared by constraints.</summary>
/// <remarks>
/// Order: <c>SKETCH_DEFINITION_CHECK_DEVELOPER_PROMPT.md</c>. The question is posed so that the answer
/// does not depend on client memory and is not replaced by a convenient sign: exact coordinates, width
/// and height, the radius at creation, a closed contour and a successful extrusion do NOT prove full
/// definition (order §"Semantics", items 2–3). So the control states are built on ACTUALLY applied
/// constraints, and the expected status is not declared a fact before reading from KOMPAS.
/// The read route (found by reconnaissance, see <c>docs/acceptance/api7/sketch-definition.md</c>):
/// <c>ISketch.ConstraintsState</c> of type <c>ksConstraintsStateEnum</c> — the aggregated status of the
/// whole sketch, exactly the quantity KOMPAS shows as "+", "−", "!". The enumeration is declared in
/// <c>Bin\ksConstants.tlb</c>, not in <c>kAPI7.tlb</c>, and is NOT linked into this probe; the values
/// are duplicated locally and checked against the declared ones by step S.3.
/// The reconnaissance branch the probe closes and does NOT carry into a general conclusion:
/// <c>IParametriticConstraint.Degrees</c> is the degrees of an ANGULAR constraint
/// (<c>ksConstraintTypeEnum.ksCFixedAngle = 18</c> and neighbours), not degrees of freedom. The names
/// deceive, so the quantity is distinguished explicitly.
/// Its own STA thread, its own invisible KOMPAS instance, its own documents in the <c>scratch</c>
/// directory. Foreign processes are not terminated and user models are not opened.
/// History: docs/decisions/probes.md#sketch-definition</remarks>
internal sealed class SketchDefinitionProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    /// <summary>Radius of the control circle.</summary>
    private const double CircleRadius = 20d;

    /// <summary>Centre of the control circle — deliberately not at the origin.</summary>
    private const double CircleCenterX = 25d;
    private const double CircleCenterY = 15d;

    /// <summary>Raw value of <c>ksStateWellConstrained</c>. Duplicated on purpose: step S.4d must be
    /// readable by itself, and the match of the duplicate with the declared value is checked by S.3.</summary>
    private const int WellConstrained = 1;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private KompasAPI7.IApplication? _app7;

    /// <summary>The probe's current document and its part.</summary>
    private ksDocument3D? _doc;
    private ksPart? _part;

    /// <summary>The current document's sketch as a tree entity — ISketch is taken from it.</summary>
    private ksEntity? _sketchEntity;

    /// <summary>Probe outcome: whether a constraint could be applied at all.</summary>
    private bool _constraintRouteWorks;

    /// <summary>Probe outcome: whether the driving dimension was accepted and changed the status.</summary>
    private bool _dimensionRouteWorks;

    /// <summary>Probe outcome: whether the API7 route <c>NewConstraint()</c> accepted the constraint and changed the status.</summary>
    private bool _api7ConstraintRouteWorks;

    public SketchDefinitionProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

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
            ProbeEnum();
            ReconConstraintRoute();
            Api7ConstraintRoute();
            DimensionRoute();
            DrivingDimensionsRoute();
            ParametrizationRoute();
            ControlMatrix();
            ReadOnlyControls();
            ReopenAndStability();
            ExternalDocumentRoute();
            ReadIsSideEffectFree();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("S.E", "Необработанное исключение пробы S");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("S.1", "Свой невидимый экземпляр и доказательство PID",
            "Зонд работает с тем КОМПАС, который сам запустил?");
        try
        {
            var before = KompasIds();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;

            var created = WaitForNewProcess(before, 90_000);
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется, проба остановлена.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = _options.ProcessId;
            step.Observe($"Собственный экземпляр КОМПАС, PID {_options.ProcessId}.");
            step.Pass("Сеанс принадлежит пробе: PID появился вместе с активацией ProgID.");
        }
        catch (Exception ex)
        {
            step.Fail("Экземпляр не создан: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("S.Z", "Завершение сеанса", "Остаётся ли осиротевший процесс?");
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
        var step = _report.Begin("S.2", "Мост API5→API7",
            "Тот же сеанс отдаёт IApplication API7?");
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
            step.Data["app7"] = true;
            step.Pass("Мост построен: ksGetApplication7() того же сеанса.");
        }
        catch (Exception ex)
        {
            step.Fail("Мост не построен: " + ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════ enumeration ══

    /// <summary>Step S.3: checks the locally duplicated <c>ksConstraintsStateEnum</c> values against the
    /// ones declared in the linked assemblies.</summary>
    /// <remarks>The probe does NOT link the constants assembly as a whole just for four numbers, so the
    /// duplication here is deliberate — and that is exactly why it must be checked. If the type is found
    /// in no assembly, the step honestly records that there is no confirmation, and the numbers in the
    /// report stay raw.</remarks>
    private void ProbeEnum()
    {
        var step = _report.Begin("S.3", "ksConstraintsStateEnum: объявленные состояния",
            "Совпадают ли локально продублированные значения с объявленными, и покрывают ли они семантику v24?");
        try
        {
            var type = typeof(Kompas6Constants.ksConstraintTypeEnum).Assembly
                           .GetType("Kompas6Constants.ksConstraintsStateEnum")
                       ?? typeof(KompasAPI7.IApplication).Assembly.GetType("KompasAPI7.ksConstraintsStateEnum");

            if (type is null)
            {
                step.Unknown("Перечисление не найдено ни в одной залинкованной сборке. Это утверждение " +
                             "об обёртке, а не о продукте: значения читаются числом на шаге S.5.");
                return;
            }

            step.Data["enum_assembly"] = type.Assembly.GetName().Name;
            var declared = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in Enum.GetValues(type))
            {
                var name = Enum.GetName(type, value) ?? value.ToString()!;
                declared[name] = Convert.ToInt32(value);
                step.Data["declared_" + name] = Convert.ToInt32(value);
            }

            step.Observe("Объявлено: " + string.Join(", ", declared.Select(p => $"{p.Value}={p.Key}")));

            // The duplicate is checked against the declared value for each of the four quantities
            // separately, so that a mismatch names a concrete state rather than "the enum did not match".
            var mismatches = new List<string>();
            foreach (var (name, expected) in LocalStateValues)
            {
                if (declared.TryGetValue(name, out var actual))
                {
                    step.Data["match_" + name] = actual == expected;
                    if (actual != expected)
                    {
                        mismatches.Add($"{name}: объявлено {actual}, дубликат {expected}");
                    }
                }
                else
                {
                    step.Data["match_" + name] = false;
                    mismatches.Add($"{name}: не объявлено (дубликат {expected})");
                }
            }

            step.Data["mismatches"] = mismatches.Count == 0 ? "нет" : string.Join("; ", mismatches);
            if (mismatches.Count == 0)
            {
                step.Pass("Дублированные значения совпали с объявленными по всем четырём состояниям: " +
                          "определён (1), недоопределён (2), требует внимания (3), неизвестно (0).");
            }
            else
            {
                step.Fail("Расхождение дубликата с объявленным: " + string.Join("; ", mismatches) +
                          ". Числа в отчёте построены на объявленных значениях.");
            }
        }
        catch (Exception ex)
        {
            step.Fail("Перечисление не прочитано: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════ constraint route ══

    /// <summary>Step S.4: establishes by which call a constraint on a sketch object is REALLY applied,
    /// and whether the status changes because of it.</summary>
    /// <remarks>
    /// Without this step the state matrix would be a setup: if the constraint did not apply, all states
    /// would come out the same, and "the status does not distinguish" would be a conclusion about the
    /// probe, not about KOMPAS. So the tool itself is measured first, and only then used to build states.
    /// <b>The shape of the step is determined by the first run.</b> MEASURED: the first run gave
    /// <c>Init() → True</c> (the parameter block is alive) but <c>ksSetObjConstraint → 0</c> — a refusal.
    /// The documentation (help.ascon.ru, <c>ksSetObjConstraint</c> and <c>structconstraintparam</c>) says
    /// that 0 is exactly a failure, that <c>index</c> is the point number on the object (for a circle
    /// <c>0</c> is the centre), and <c>partner</c> is a <em>pointer to the second object</em>. The help
    /// example applies <c>CONSTRAINT_EQUAL_RADIUS</c> to TWO objects. Hence two different suspicions that
    /// must be told apart rather than merged into one "does not work":
    /// <list type="number">
    /// <item>suspicion "wrong object number" — then <c>ksExistObj</c> on the same number says "no such
    /// object", and that is an addressing refusal, not a property of the constraint;</item>
    /// <item>suspicion "wrong constraint type" — then addressing is confirmed, but the specific
    /// <c>constrType</c> does not apply to a circle.</item>
    /// </list>
    /// So here the existence of the object by number is confirmed first, then both a single constraint
    /// (<c>CONSTRAINT_FIXED_POINT</c>, which by meaning has no partner) and a paired one
    /// (<c>CONSTRAINT_EQUAL_RADIUS</c> on two circles — exactly the case shown in the help) are tried.
    /// After each attempt <c>ksGetObjConstraints</c> is read — a DIRECT check that "the constraint really
    /// appeared", independent of the status change.
    /// </remarks>
    private void ReconConstraintRoute()
    {
        var step = _report.Begin("S.4", "Чем ограничение на объект эскиза реально накладывается",
            "Достаточно ли ksSetObjConstraint с ksConstraintParam, чтобы сменить статус, и какой объект " +
            "по какому номеру эта функция вообще видит?");
        try
        {
            if (!BuildCircleDocument("s-recon", step, out var editor, out var circleRef))
            {
                step.Fail("Контрольный документ для разведки не построен.");
                return;
            }

            var stateBefore = StateOfCurrent(step, "before");
            step.Data["state_before"] = stateBefore;
            step.Data["state_before_interpreted"] = Interpret(stateBefore);
            step.Observe($"Свободная окружность: {Interpret(stateBefore)} (сырое {Api5.Raw(stateBefore)}).");

            var attempts = new List<string>();

            // (a) Addressing. ksExistObj answers on the same number that ksSetObjConstraint accepts. If
            // the number says "no object", then the 0 refusal is explained by addressing, and no
            // conclusion about the applicability of the constraint type follows from it.
            var circleExists = Api5.SafeInt(() => editor.ksExistObj(circleRef));
            step.Data["ksExistObj_circle"] = circleExists;
            attempts.Add($"ksExistObj({circleRef}) → " + Api5.Raw(circleExists));

            // The second circle is for the paired constraint shown in the help. Same edge, different
            // point, so that the objects are distinguishable.
            var second = editor.ksCircle(CircleCenterX + 40d, CircleCenterY, CircleRadius, 1);
            step.Data["second_circle_ref"] = second;
            var secondExists = Api5.SafeInt(() => editor.ksExistObj(second));
            step.Data["ksExistObj_second"] = secondExists;
            attempts.Add($"вторая окружность {second}, ksExistObj → " + Api5.Raw(secondExists));

            var constraintStruct = (short)StructType2DEnum.ko_ConstraintParam;
            step.Data["ko_ConstraintParam"] = constraintStruct;

            // (b) Single constraint: fixing the point at the circle centre.
            var fixSingle = TryConstraint(editor, step, "fix_point_single", constraintStruct,
                LocalConstraintType.FixedPoint, circleRef, index: 0, partner: 0, partnerIndex: 0);
            attempts.Add("CONSTRAINT_FIXED_POINT на окружности → " + Api5.Raw(fixSingle) + " (1 = принято)");

            // (c) Paired constraint exactly per the help example: equality of the radii of two circles.
            var equalRadius = TryConstraint(editor, step, "equal_radius_pair", constraintStruct,
                LocalConstraintType.EqualRadius, circleRef, index: 0, partner: second, partnerIndex: 0);
            attempts.Add("CONSTRAINT_EQUAL_RADIUS двух окружностей → " + Api5.Raw(equalRadius) + " (1 = принято)");

            // (d) Direct check: did constraints appear on the object. This is NOT a derivative of the
            // status — it is the answer of the function that enumerates those constraints.
            var constraintsSeen = ReadBackConstraints(editor, step, circleRef);
            attempts.Add("ksGetObjConstraints(окружность) → " + constraintsSeen);

            _doc!.RebuildDocument();
            var stateAfter = StateOfCurrent(step, "after_constraint");
            step.Data["state_after_constraint"] = stateAfter;
            step.Data["state_after_constraint_interpreted"] = Interpret(stateAfter);
            attempts.Add($"статус после попыток: {Interpret(stateAfter)} (сырое {Api5.Raw(stateAfter)})");

            var accepted = fixSingle == 1 || equalRadius == 1;
            if (accepted && stateAfter is not null && stateBefore is not null && stateAfter != stateBefore)
            {
                _constraintRouteWorks = true;
            }
            else if (accepted)
            {
                // The constraint was accepted but the status did not change: this is already a fact about
                // the PRODUCT — either the chosen type does not remove a degree of freedom, or the status
                // changes elsewhere in the lifecycle.
                _constraintRouteWorks = true;
                step.Data["accepted_but_status_unchanged"] = true;
            }

            step.Data["attempts"] = attempts;
            foreach (var line in attempts)
            {
                step.Observe(line);
            }

            // The read path and the write path are evaluated SEPARATELY: reading the status may work even
            // where this probe could not apply a constraint, and vice versa.
            var readWorks = stateBefore is not null;
            step.Data["read_route_works"] = readWorks;
            step.Data["write_route_works"] = _constraintRouteWorks;

            var addressConfirmed = circleExists is not null and not 0 && secondExists is not null and not 0;
            step.Data["addressing_confirmed"] = addressConfirmed;

            if (readWorks && _constraintRouteWorks)
            {
                step.Pass("И чтение статуса, и наложение ограничения подтверждены: вызов принят " +
                          "(результат 1), статус прочитан.");
            }
            else if (readWorks && addressConfirmed)
            {
                // The most important branch for the honesty of the conclusion: addressing works but the
                // constraint is not accepted. So the refusal concerns the applicability of the call, not
                // "object not found", and that is what is recorded — without substituting a convenient
                // wording.
                step.Unknown("Адресация объекта подтверждена (ksExistObj не 0 по обоим объектам и " +
                             "GetParamStruct выдал живой блок), но ksSetObjConstraint вернул 0 на обоих " +
                             "типах — одиночном и парном. Отказ относится к самому вызову, а не к " +
                             "номеру объекта. Контрольных состояний из ограничений не построить, пока " +
                             "не найден принимаемый вызов; чтение статуса измерено отдельно (S.6, S.7).");
            }
            else if (readWorks)
            {
                step.Unknown("Чтение статуса работает, но адресация не подтверждена (ksExistObj вернул 0 " +
                             "или не ответил) — отказ не отличим от неверного номера объекта. Это " +
                             "измерение инструмента, а не вывод о продукте.");
            }
            else
            {
                step.Fail("Статус не прочитан даже на свободной окружности — маршрут чтения не подтверждён.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Разведка маршрута ограничений не выполнена: " + ex.Message);
        }
    }

    /// <summary>Tries to apply a constraint with a single call and returns the result as is (<c>1</c>
    /// accepted, <c>0</c> refused, <c>null</c> — the call did not go through).</summary>
    /// <remarks>The type number and the partner's participation are passed as parameters, because the
    /// distinction "single or paired" is the subject of the experiment: the help demonstrates a paired
    /// one (<c>CONSTRAINT_EQUAL_RADIUS</c>), and a single one (<c>CONSTRAINT_FIXED_POINT</c>) must be
    /// checked separately, otherwise a refusal of one type would be passed off as a property of the whole
    /// function.</remarks>
    private int? TryConstraint(ksDocument2D editor, ProbeStep step, string tag, short structType,
        LocalConstraintType type, int objectRef, int index, int partner, int partnerIndex)
    {
        try
        {
            if (_app.GetParamStruct(structType) is not Kompas6API5.ksConstraintParam parameter)
            {
                step.Data[tag + "_result"] = "нет блока";
                return null;
            }

            parameter.constrType = (short)type;
            parameter.index = index;
            parameter.partner = partner;
            parameter.partnerIndex = partnerIndex;
            var init = parameter.Init();
            step.Data[tag + "_init"] = init;

            var result = Api5.SafeInt(() => editor.ksSetObjConstraint(objectRef, parameter));
            step.Data[tag + "_result"] = result;
            step.Data[tag + "_type"] = (int)type;
            return result;
        }
        catch (Exception ex)
        {
            step.Data[tag + "_result"] = "исключение " + ex.GetType().Name;
            step.Data[tag + "_error"] = ex.Message;
            return null;
        }
    }

    /// <summary>A direct check that "the constraint really appeared": asks the document for the object's
    /// list of constraints instead of deriving it from the status.</summary>
    /// <remarks>
    /// <c>ksGetObjConstraints</c> returns <c>CONSTRAINT_ARR</c> or <c>0</c> on failure (help.ascon.ru).
    /// A zero here means "list not obtained" and is not turned into "no constraints": these are different
    /// statements, and they are distinguishable in the report.
    /// The return comes as a COM array (<c>System.__ComObject</c>), not a .NET array, so a bare
    /// <c>is Array</c> is not enough: the array is unwrapped as <c>object[]</c> via a cast, and when it
    /// could not be unwrapped that is recorded AS IS — "could not read" instead of "no constraints",
    /// otherwise a read failure would pass itself off as an absence of constraints.
    /// </remarks>
    private string ReadBackConstraints(ksDocument2D editor, ProbeStep step, int objectRef)
    {
        try
        {
            var raw = editor.ksGetObjConstraints(objectRef);
            step.Data["ksGetObjConstraints_type"] = Api5.RuntimeName(raw);
            if (raw is null)
            {
                return "null";
            }

            // A single parameter object: a ksConstraintParam instance, not an array.
            if (raw is Kompas6API5.ksConstraintParam single)
            {
                step.Data["ksGetObjConstraints_constrType"] = (int)single.constrType;
                return "одна связь, constrType=" + (int)single.constrType;
            }

            // COM array: first a direct cast is tried, then as object[].
            if (raw is object[] directArray)
            {
                return DescribeConstraintArray(directArray, step);
            }

            var asArray = raw as Array;
            if (asArray is not null)
            {
                step.Data["ksGetObjConstraints_length"] = asArray.Length;
                var types = new List<string>();
                foreach (var element in asArray)
                {
                    types.Add(element is Kompas6API5.ksConstraintParam typed
                        ? ((int)typed.constrType).ToString()
                        : Api5.RuntimeName(element));
                }

                return "массив из " + asArray.Length + ", constrType=[" + string.Join(",", types) + "]";
            }

            // Not unwrapped: an attempt to cast to a strongly typed array of parameters.
            try
            {
                if (raw is Kompas6API5.ksConstraintParam[] typedArray)
                {
                    return DescribeConstraintArray(typedArray, step);
                }
            }
            catch (Exception ex)
            {
                step.Data["ksGetObjConstraints_cast_error"] = ex.GetType().Name + ": " + ex.Message;
            }

            step.Data["ksGetObjConstraints_unwrapped"] = false;
            return "не развёрнут: " + Api5.RuntimeName(raw);
        }
        catch (Exception ex)
        {
            step.Data["ksGetObjConstraints_error"] = ex.GetType().Name + ": " + ex.Message;
            return "исключение " + ex.GetType().Name;
        }
    }

    /// <summary>Describes an array of constraints by type, keeping the length as an observation.</summary>
    private string DescribeConstraintArray(object?[] array, ProbeStep step)
    {
        step.Data["ksGetObjConstraints_is_array"] = true;
        step.Data["ksGetObjConstraints_length"] = array.Length;
        step.Data["ksGetObjConstraints_unwrapped"] = true;
        var types = new List<string>();
        foreach (var element in array)
        {
            types.Add(element is Kompas6API5.ksConstraintParam typed
                ? ((int)typed.constrType).ToString()
                : Api5.RuntimeName(element));
        }

        return "массив из " + array.Length + ", constrType=[" + string.Join(",", types) + "]";
    }

    /// <summary>Step S.4c: a constraint through API7 ITSELF — <c>IDrawingObject1.NewConstraint()</c> →
    /// <c>IParametriticConstraint.Create()</c>.</summary>
    /// <remarks>
    /// A separate step because it is a third, independent branch. <c>ksSetObjConstraint</c> (S.4) is an
    /// API5 call on the document; the driving dimension (S.4b) is a dimension method. Here a constraint
    /// OBJECT is built: <c>NewConstraint()</c> yields an <c>IParametriticConstraint</c> with the fields
    /// <c>ConstraintType</c>, <c>Index</c>, <c>Partner</c>, <c>PartnerIndex</c>, the method <c>Create()</c>
    /// and the <c>Valid</c> flag. It is this branch that the order names as the means to carry the found
    /// route into the product, so it is measured separately, not implied.
    /// The object is taken via the sketch's <c>GetCurve2D()</c> and cast to <c>IDrawingObject1</c>; the
    /// QI is done explicitly, and "the cast did not go through" is recorded as a separate outcome, not as
    /// "the constraint is not created".
    /// </remarks>
    private void Api7ConstraintRoute()
    {
        var step = _report.Begin("S.4c", "Связь через API7: NewConstraint → Create",
            "Принимает ли API7 объектную связь там, где API5 ответил 0, и меняется ли от неё статус?");
        try
        {
            // The document is built with a CLOSED editor: API7 does not return the sketch fragment while
            // it is open for editing via API5 (MEASURED: BeginEdit() → null). This is a condition of the
            // branch itself, and it is respected, not worked around.
            if (!BuildCircleDocument("s-api7", step, out var editor, out var circleRef, closeEditor: true))
            {
                step.Fail("Контрольный документ для маршрута API7 не построен.");
                return;
            }

            _ = editor;
            _ = circleRef;

            var before = StateOfCurrent(step, "api7_before");
            step.Data["api7_state_before"] = before;
            step.Data["api7_state_before_interpreted"] = Interpret(before);

            if (TransferSketch(_sketchEntity!, step) is not KompasAPI7.ISketch sketch)
            {
                step.Unknown("Эскиз не перенесён в API7 — маршрут связи этой пробой не проверен.");
                return;
            }

            // The curve object inside the sketch. The chain is taken from the wrapper, not guessed:
            // ISketch.BeginEdit() → IFragmentDocument.ViewsAndLayersManager.Views → view → Layers →
            // drawing objects. No link is skipped silently: which step failed to yield the next object is
            // recorded in the data, because "the constraint was not created" and "we did not reach the
            // object" are different observations.
            var notes = new List<string>();
            var drawingObject = FindDrawingObjectInSketch(sketch, notes, step);
            if (drawingObject is null)
            {
                step.Data["api7_notes"] = notes;
                foreach (var note in notes)
                {
                    step.Observe(note);
                }

                step.Unknown("Объект чертежа внутри эскиза не получен — связь через API7 этой пробой не " +
                             "проверена. Это утверждение о доступности ветки в этой обёртке, а не о продукте.");
                return;
            }

            notes.Add("IDrawingObject1 получен, IsCurve=" + drawingObject.IsCurve);

            // The object's constraint state is a separate quantity from the same interface. It is read
            // and recorded even if the constraint cannot be created.
            try
            {
                var objectState = (int)drawingObject.ConstraintsState;
                step.Data["api7_object_state"] = objectState;
                notes.Add($"IDrawingObject1.ConstraintsState = {objectState}");
            }
            catch (Exception ex)
            {
                step.Data["api7_object_state_error"] = ex.GetType().Name + ": " + ex.Message;
            }

            KompasAPI7.IParametriticConstraint? constraint = null;
            try
            {
                constraint = drawingObject.NewConstraint();
                step.Data["api7_new_constraint"] = Api5.RuntimeName(constraint);
                notes.Add("NewConstraint() → " + Api5.RuntimeName(constraint));
            }
            catch (Exception ex)
            {
                step.Data["api7_new_constraint_error"] = ex.GetType().Name + ": " + ex.Message;
                notes.Add("NewConstraint(): " + ex.GetType().Name);
            }

            int? created = null;
            if (constraint is not null)
            {
                try
                {
                    // ksConstraintTypeEnum.ksCFixedPoint = 1: point fixing; for a circle index 0 is the centre.
                    constraint.ConstraintType = Kompas6Constants.ksConstraintTypeEnum.ksCFixedPoint;
                    constraint.Index = 0;
                    step.Data["api7_constraint_type"] = (int)constraint.ConstraintType;
                    step.Data["api7_constraint_index"] = constraint.Index;

                    var ok = constraint.Create();
                    created = ok ? 1 : 0;
                    step.Data["api7_create_result"] = ok;
                    notes.Add("Create() → " + ok);

                    step.Data["api7_valid_after_create"] = constraint.Valid;
                    notes.Add("Valid после Create() = " + constraint.Valid);
                }
                catch (Exception ex)
                {
                    step.Data["api7_create_error"] = ex.GetType().Name + ": " + ex.Message;
                    notes.Add("Create(): " + ex.GetType().Name);
                }
            }

            _doc!.RebuildDocument();
            var after = StateOfCurrent(step, "api7_after");
            step.Data["api7_state_after"] = after;
            step.Data["api7_state_after_interpreted"] = Interpret(after);
            notes.Add($"статус после связи API7: {Interpret(after)} (сырое {Api5.Raw(after)})");

            step.Data["api7_notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (created == 1)
            {
                _api7ConstraintRouteWorks = true;
            }

            if (created == 1 && after is not null && before is not null && after != before)
            {
                step.Pass($"Связь создана через API7 и статус изменился: {Interpret(before)} → {Interpret(after)}. " +
                          "Это маршрут, который переводится в продукт.");
            }
            else if (created == 1)
            {
                _api7ConstraintRouteWorks = true;
                step.Unknown($"Связь создана (Create() = True), но статус не изменился " +
                             $"({Interpret(before)} → {Interpret(after)}). Возможные причины различимы " +
                             "дальше: либо закрепление точки снимает свободу не полностью там, где " +
                             "ожидалось, либо статус пересчитывается иначе.");
            }
            else
            {
                step.Unknown("Связь через API7 не создана — эта ветка не дала маршрута. Записан каждый " +
                             "шаг отдельно (NewConstraint, ConstraintType, Create), чтобы отказ был " +
                             "виден в своей точке, а не как «API7 не умеет».");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут связи API7 не проверен: " + ex.Message);
        }
    }

    /// <summary>Fetches the circle object inside the sketch through API7, finding it BY POINT.</summary>
    /// <remarks>
    /// The chain is taken from the wrapper, not guessed: <c>ISketch.BeginEdit()</c> →
    /// <c>IFragmentDocument.ViewsAndLayersManager.Views</c> → <c>IViews.View(index)</c> →
    /// <c>IView1.FindObject(X, Y, Limit, Param)</c>. <c>FindObject</c> returns <c>IDrawingObject</c> — the
    /// object on which <c>NewConstraint()</c> lives.
    /// The search goes by the circle centre coordinates, because that is the only key the probe has
    /// independently of objects: the <c>ksCircle</c> number belongs to the API5 space and means nothing to
    /// API7. Each link either yields the next object or records in <paramref name="notes"/> where it broke
    /// off.
    /// </remarks>
    private KompasAPI7.IDrawingObject1? FindDrawingObjectInSketch(
        KompasAPI7.ISketch sketch, List<string> notes, ProbeStep step)
    {
        KompasAPI7.IFragmentDocument? fragment = null;
        try
        {
            fragment = sketch.BeginEdit();
            notes.Add("BeginEdit() → " + Api5.RuntimeName(fragment));
        }
        catch (Exception ex)
        {
            notes.Add("BeginEdit(): " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }

        if (fragment is null)
        {
            return null;
        }

        KompasAPI7.IViews? views = null;
        try
        {
            views = fragment.ViewsAndLayersManager.Views;
            notes.Add("ViewsAndLayersManager.Views → " + Api5.RuntimeName(views));
        }
        catch (Exception ex)
        {
            notes.Add("ViewsAndLayersManager.Views: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }

        if (views is null)
        {
            return null;
        }

        var viewCount = Api5.SafeInt(() => views.Count);
        notes.Add("видов: " + Api5.Raw(viewCount));

        for (var v = 0; v < (viewCount ?? 0); v++)
        {
            KompasAPI7.IView? view;
            try
            {
                // View is an indexer, not a method (API7 wrapper): accessed via [v].
                view = views.View[v] as KompasAPI7.IView;
            }
            catch (Exception ex)
            {
                notes.Add($"View({v}): " + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            if (view is null)
            {
                notes.Add($"View({v}) → null");
                continue;
            }

            notes.Add($"вид {v}: ObjectCount=" + Api5.Raw(Api5.SafeInt(() => view.ObjectCount)));

            if (view is not KompasAPI7.IView1 finder)
            {
                notes.Add($"вид {v} не поддерживает IView1 (FindObject) — " + Api5.RuntimeName(view));
                continue;
            }

            var found = FindCurveByPoint(finder, notes, step);
            if (found is not null)
            {
                step.Data["api7_drawing_object_view"] = v;
                return found;
            }
        }

        return null;
    }

    /// <summary>Searches for a curve in a view by the control circle's centre coordinates.</summary>
    /// <remarks>
    /// The search parameters are created by CLSID, not via <c>Activator.CreateInstance(Type)</c>:
    /// <c>FindObjectParametersClass</c> is a COM class without an exposed parameterless constructor
    /// (MEASURED: <c>MissingMethodException</c>), so the instance is taken from COM by the class GUID.
    /// If creation fails, the reason stays in <paramref name="notes"/> and <c>null</c> is returned —
    /// recorded as "we did not reach the object", not as "the constraint is not created".
    /// </remarks>
    private KompasAPI7.IDrawingObject1? FindCurveByPoint(
        KompasAPI7.IView1 finder, List<string> notes, ProbeStep step)
    {
        object? parameters;
        try
        {
            var type = typeof(KompasAPI7.IView1).Assembly.GetType("KompasAPI7.FindObjectParametersClass");
            if (type is null)
            {
                notes.Add("FindObjectParametersClass не найден в обёртке — поиск по точке недоступен");
                return null;
            }

            // The class GUID is taken from the type in the wrapper itself, not written in as a number.
            var clsid = type.GUID;
            notes.Add("FindObjectParametersClass CLSID=" + clsid.ToString("N"));
            var comType = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (comType is null)
            {
                notes.Add("CLSID в реестре не зарегистрирован");
                parameters = null;
            }
            else
            {
                parameters = Activator.CreateInstance(comType);
                notes.Add("FindObjectParameters → " + Api5.RuntimeName(parameters));
            }
        }
        catch (Exception ex)
        {
            // The parameter class is registered by a manifest and does not come up through COM (MEASURED:
            // REGDB_E_CLASSNOTREG). This is not the end of the experiment: FindObject is also checked with
            // empty parameters, because then the reason for the refusal is visible separately from the
            // reason "cannot make parameters".
            notes.Add("FindObjectParameters: " + ex.GetType().Name + ": " + ex.Message);
            parameters = null;
        }

        // Attempt 1: with parameters, if they could be created.
        if (parameters is KompasAPI7.FindObjectParameters findParameters)
        {
            try
            {
                findParameters.Clear();
                findParameters.GeometryOnly = true;
                notes.Add("параметры поиска: GeometryOnly=true");
            }
            catch (Exception ex)
            {
                notes.Add("настройка параметров поиска: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var found = finder.FindObject(CircleCenterX, CircleCenterY, 1.0, findParameters);
                notes.Add("FindObject(с параметрами) → " + Api5.RuntimeName(found));
                if (found as KompasAPI7.IDrawingObject1 is { } withParams)
                {
                    return withParams;
                }
            }
            catch (Exception ex)
            {
                notes.Add("FindObject(с параметрами): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Attempt 2: without parameters. A refusal here is separated from a failure to create parameters,
        // otherwise "cannot make parameters" and "search does not work" would merge into one observation.
        try
        {
            var found = finder.FindObject(CircleCenterX, CircleCenterY, 1.0, null!);
            notes.Add("FindObject(без параметров) → " + Api5.RuntimeName(found));
            return found as KompasAPI7.IDrawingObject1;
        }
        catch (Exception ex)
        {
            notes.Add("FindObject(без параметров): " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Step S.4b: does a driving dimension change the status, and is it the very constraint that
    /// removes a degree of freedom.</summary>
    /// <remarks>
    /// A separate step because it is a DIFFERENT call of a different nature. <c>ksSetObjConstraint</c> is a
    /// parametric constraint; a driving dimension is placed by dimension methods (<c>ksRadDimension</c>),
    /// and the help specifies its parameters not by a curve reference but by GEOMETRY: <c>RDimSource</c>
    /// holds <c>xc</c>, <c>yc</c>, <c>rad</c> — the centre and radius by which the dimension finds the
    /// measured circle (help.ascon.ru, <c>structrdimparam</c> and the example
    /// <c>raddimension_radbreakdimension_example</c>). So "the dimension did not bind" and "the constraint
    /// did not apply" are different refusals, and they are measured by different steps.
    /// Order §"Semantics" item 3 explicitly forbids deriving definition from the PRESENCE OF A DIMENSION
    /// TEXT. So the step does not stop at "the method returned a non-zero reference": it reads the status
    /// before and after, and distinguishes three outcomes — the dimension was accepted and the status
    /// changed, the dimension was accepted and the status did NOT change, and the dimension was not
    /// accepted. The first outcome gives a route for the control matrix, the third is an honest refusal,
    /// the second is a fact about the product.
    /// </remarks>
    private void DimensionRoute()
    {
        var step = _report.Begin("S.4b", "Управляющий размер как источник определённости",
            "Меняет ли ksRadDimension статус эскиза так же, как ограничение эскиза, и принимается ли он вообще?");
        try
        {
            if (!BuildCircleDocument("s-dim", step, out var editor, out var circleRef))
            {
                step.Fail("Контрольный документ для размерного маршрута не построен.");
                return;
            }

            var before = StateOfCurrent(step, "dim_before");
            step.Data["dim_state_before"] = before;
            step.Data["dim_state_before_interpreted"] = Interpret(before);

            var radialStruct = (short)StructType2DEnum.ko_RDimParam;
            var sourceStruct = (short)StructType2DEnum.ko_RDimSource;
            step.Data["ko_RDimParam"] = radialStruct;
            step.Data["ko_RDimSource"] = sourceStruct;

            var notes = new List<string>();
            if (_app.GetParamStruct(radialStruct) is not Kompas6API5.ksRDimParam radialParam)
            {
                step.Unknown("GetParamStruct(" + radialStruct + " /* ko_RDimParam */) не дал ksRDimParam — " +
                             "маршрут размера этой пробой не проверен. Это утверждение об обёртке.");
                return;
            }

            notes.Add("ksRDimParam получен");
            if (_app.GetParamStruct(sourceStruct) is not Kompas6API5.ksRDimSourceParam source)
            {
                step.Unknown("GetParamStruct(" + sourceStruct + " /* ko_RDimSource */) не дал ksRDimSourceParam — " +
                             "привязку задать нечем.");
                return;
            }

            // The binding is set by the geometry of the measured circle: centre (25,15) and radius 20 —
            // the same numbers the circle was built with.
            //
            // Init() is called FIRST and its result is CHECKED: MEASURED: the first run showed Init()
            // zeroes the fields (with 25/15/20 set, a read-back gave xc=0, yc=0, rad=10), and filling the
            // fields before it makes SetSPar accept a block with defaults — the dimension is placed
            // "nowhere", returns a live reference, and it looks like "the dimension does not affect the
            // status". That was a probe defect, not a fact about KOMPAS.
            source.Init();
            source.xc = CircleCenterX;
            source.yc = CircleCenterY;
            source.rad = CircleRadius;
            notes.Add($"привязка: xc={source.xc}, yc={source.yc}, rad={source.rad}");

            // The read-back is a control that the dimension goes by the set numbers, not by defaults.
            var bindingHeld = Math.Abs(source.xc - CircleCenterX) < 1e-9
                              && Math.Abs(source.yc - CircleCenterY) < 1e-9
                              && Math.Abs(source.rad - CircleRadius) < 1e-9;
            step.Data["binding_held_after_init"] = bindingHeld;
            if (!bindingHeld)
            {
                step.Fail($"Привязка не удержана после Init(): xc={source.xc}, yc={source.yc}, rad={source.rad} " +
                          $"вместо {CircleCenterX}/{CircleCenterY}/{CircleRadius}. Размер ушёл бы по " +
                          "умолчанию, и наблюдение о статусе было бы наблюдением о другом размере.");
                return;
            }

            notes.Add("SetSPar → " + radialParam.SetSPar(source));

            var dimension = Api5.SafeInt(() => editor.ksRadDimension(radialParam));
            step.Data["ksRadDimension_result"] = dimension;
            step.Data["ksRadDimension_ref_is_zero"] = dimension is null or 0;
            notes.Add("ksRadDimension → " + Api5.Raw(dimension));

            // A reference to a dimension is not yet a changed constraint system. The status is read
            // separately and compared with the initial one; the presence of a dimension as a fact does not
            // substitute the distinction here.
            _doc!.RebuildDocument();
            var after = StateOfCurrent(step, "dim_after");
            step.Data["dim_state_after"] = after;
            step.Data["dim_state_after_interpreted"] = Interpret(after);
            notes.Add($"статус после размера: {Interpret(after)} (сырое {Api5.Raw(after)})");

            // Direct check: did the document register a constraint on this circle. This distinguishes
            // "the dimension was created but did not become a constraint" from "the constraint exists but
            // the status was not recomputed" — two different causes of the same observation, and one is
            // not passed off as the other.
            var afterConstraints = ReadBackConstraints(editor, step, circleRef);
            step.Data["dim_constraints_after"] = afterConstraints;
            notes.Add("связи на окружности после размера: " + afterConstraints);

            // Twin control: the same operation on a second circle that is not in this sketch — "dimension
            // accepted" must not be a property of a specific curve.
            var second = editor.ksCircle(CircleCenterX + 40d, CircleCenterY, CircleRadius * 1.5d, 1);
            var secondDim = (int?)null;
            if (second != 0)
            {
                if (_app.GetParamStruct((short)StructType2DEnum.ko_RDimParam) is Kompas6API5.ksRDimParam secondRadial
                    && _app.GetParamStruct((short)StructType2DEnum.ko_RDimSource) is Kompas6API5.ksRDimSourceParam secondSource)
                {
                    secondSource.Init();
                    secondSource.xc = CircleCenterX + 40d;
                    secondSource.yc = CircleCenterY;
                    secondSource.rad = CircleRadius * 1.5d;
                    secondRadial.SetSPar(secondSource);
                    secondDim = Api5.SafeInt(() => editor.ksRadDimension(secondRadial));
                }
            }

            step.Data["second_dimension_ref"] = secondDim;
            notes.Add("размер на второй окружности → " + Api5.Raw(secondDim));

            _doc!.RebuildDocument();
            var afterSecond = StateOfCurrent(step, "dim_after_second");
            step.Data["dim_state_after_second"] = afterSecond;
            step.Data["dim_state_after_second_interpreted"] = Interpret(afterSecond);
            notes.Add($"статус после второго размера: {Interpret(afterSecond)} (сырое {Api5.Raw(afterSecond)})");

            step.Data["dimension_notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (dimension is null or 0)
            {
                step.Unknown("ksRadDimension не принят (результат " + Api5.Raw(dimension) + ") — размерный " +
                             "маршрут этой пробе недоступен. Это измерение вызова, а не вывод о том, " +
                             "что управляющие размеры не влияют на определённость.");
                return;
            }

            if (after is not null && before is not null && after != before)
            {
                _dimensionRouteWorks = true;
                step.Pass($"Размер принят и статус изменился: {Interpret(before)} → {Interpret(after)}. " +
                          "Маршрут управляющего размера подтверждён — на нём строится контрольная матрица.");
            }
            else if (after is not null && before is not null)
            {
                // The dimension was accepted but the status did not change. The cause is stated from
                // facts, not a guess: a circle has three degrees of freedom, and a radial dimension
                // removes only one (the radius), leaving the centre free — so "under-defined" here is
                // EXPECTED, not suspicious.
                step.Unknown($"Размер принят (ссылка {dimension}) и привязан к окружности R{CircleRadius} " +
                             $"в ({CircleCenterX},{CircleCenterY}), статус остался {Interpret(after)} " +
                             $"(до размера {Interpret(before)}). Свобода центра (x, y) размером радиуса не " +
                             "снимается, поэтому этот исход согласуется с семантикой «недоопределён». " +
                             "Связь на окружности после размера: " + afterConstraints + ".");
            }
            else
            {
                step.Fail("Статус не прочитан до или после размера — различие не определено.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Размерный маршрут не проверен: " + ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════ control states ══

    /// <summary>Step S.4d — can the sketch be DRIVEN to "fully defined" by driving dimensions.</summary>
    /// <remarks>MEASURED: S.4b showed that a radial dimension is accepted but does not change the status: a circle
    /// has three degrees of freedom and the radius removes one. If the driving-dimension route is real,
    /// THREE dimensions — the radius plus two linear ones setting the centre (x and y) — must remove all
    /// three and give <c>ksStateWellConstrained</c>. This is the check "does the route distinguish
    /// well_constrained" that S.5 left open.
    /// If even three dimensions do not give "+", this does NOT prove the status is unreachable: the cause
    /// may be that a linear dimension on this rig does not become driving. So the outcome of the step is
    /// worded as "this probe did not reach well_constrained", not "well_constrained is unreachable".</remarks>
    private void DrivingDimensionsRoute()
    {
        var step = _report.Begin("S.4d", "Управляющие размеры: можно ли дойти до «полностью определён»",
            "Снимают ли радиус плюс два линейных размера все три степени свободы окружности?");
        try
        {
            if (!BuildCircleDocument("s-driving", step, out var editor, out var circleRef))
            {
                step.Fail("Контрольный документ для маршрута управляющих размеров не построен.");
                return;
            }

            var before = StateOfCurrent(step, "driving_before");
            step.Data["driving_state_before"] = before;
            step.Data["driving_state_before_interpreted"] = Interpret(before);

            var notes = new List<string>();

            // (1) Radius — by the same verified route as in S.4b.
            var radiusRef = ApplyRadiusDimension(editor, step, "driving_radius");
            notes.Add("радиус → " + Api5.Raw(radiusRef));

            // (2) Two linear dimensions setting the circle centre: in x and in y from the sketch origin.
            // The end coordinates come from the actual geometry (centre 25,15), not from "server memory":
            // the probe measures the sketch it drew itself.
            var ldimStruct = (short)StructType2DEnum.ko_LDimParam;
            var lsrcStruct = (short)StructType2DEnum.ko_LDimSource;
            step.Data["ko_LDimParam"] = ldimStruct;
            step.Data["ko_LDimSource"] = lsrcStruct;

            var dimX = ApplyLinearDimension(editor, step, "driving_x", 0d, 0d, CircleCenterX, 0d, notes);
            var dimY = ApplyLinearDimension(editor, step, "driving_y", 0d, 0d, 0d, CircleCenterY, notes);

            _doc!.RebuildDocument();
            var after = StateOfCurrent(step, "driving_after");
            step.Data["driving_state_after"] = after;
            step.Data["driving_state_after_interpreted"] = Interpret(after);
            notes.Add($"статус после трёх размеров: {Interpret(after)} (сырое {Api5.Raw(after)})");

            step.Data["driving_dimension_notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (after is null)
            {
                step.Fail("Статус не прочитан после управляющих размеров — различие не определено.");
                return;
            }

            if (after == WellConstrained)
            {
                // The dimension route is PROVEN by removing a degree of freedom: this is not "the call was
                // accepted" but "the state changed in the expected direction". The S.5 matrix is built on it.
                _dimensionRouteWorks = true;
                step.Pass("Три управляющих размера (радиус + два линейных) сняли все степени свободы " +
                          $"окружности: {Interpret(before)} → {Interpret(after)}. Маршрут различает " +
                          "«полностью определён», и контрольная матрица строится на нём.");
                return;
            }

            var which = new List<string>();
            which.Add(radiusRef is null or 0 ? "радиус не принят" : "радиус принят");
            which.Add(dimX is null or 0 ? "линейный X не принят" : "линейный X принят");
            which.Add(dimY is null or 0 ? "линейный Y не принят" : "линейный Y принят");
            step.Unknown($"До «полностью определён» этой пробой не дошли: статус остался {Interpret(after)} " +
                         $"(сырое {Api5.Raw(after)}) при исходном {Interpret(before)}. Что с вызовами: " +
                         string.Join(", ", which) + ". Это утверждение о пройденном маршруте, а не о " +
                         "недостижимости состояния «полностью определён» в продукте.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут управляющих размеров не проверен: " + ex.Message);
        }
    }

    /// <summary>A linear driving dimension between two points inside the sketch.</summary>
    /// <remarks>The binding of a linear dimension is set by the end coordinates and the extension-line
    /// offsets, not by a curve reference. As with the radial one, <c>Init()</c> is called FIRST: resetting
    /// the block after writing the fields once already turned a probe defect into a "fact" about KOMPAS
    /// (S.4b), and there is no reason to repeat it on a new block.</remarks>
    private int? ApplyLinearDimension(ksDocument2D editor, ProbeStep step, string tag,
        double x1, double y1, double x2, double y2, List<string> notes)
    {
        if (_app.GetParamStruct((short)StructType2DEnum.ko_LDimParam) is not Kompas6API5.ksLDimParam linear
            || _app.GetParamStruct((short)StructType2DEnum.ko_LDimSource) is not Kompas6API5.ksLDimSourceParam source)
        {
            step.Data[tag + "_param"] = "не получен";
            notes.Add(tag + ": параметрический блок не получен");
            return null;
        }

        try
        {
            source.Init();
            source.x1 = x1;
            source.y1 = y1;
            source.x2 = x2;
            source.y2 = y2;
            // dx/dy — the offset of the dimension line from the measured segment; 0 coincides with the segment itself.
            source.dx = 0d;
            source.dy = 0d;
            linear.SetSPar(source);

            var held = Math.Abs(source.x1 - x1) < 1e-9 && Math.Abs(source.y1 - y1) < 1e-9
                       && Math.Abs(source.x2 - x2) < 1e-9 && Math.Abs(source.y2 - y2) < 1e-9;
            step.Data[tag + "_binding_held"] = held;
            if (!held)
            {
                notes.Add($"{tag}: привязка не удержана после Init() " +
                          $"({source.x1},{source.y1})—({source.x2},{source.y2}) вместо ({x1},{y1})—({x2},{y2})");
            }

            var reference = Api5.SafeInt(() => editor.ksLinDimension(linear));
            step.Data[tag + "_ref"] = reference;
            notes.Add($"{tag}: ({x1},{y1})—({x2},{y2}) → " + Api5.Raw(reference));
            return reference;
        }
        catch (Exception ex)
        {
            step.Data[tag + "_error"] = ex.GetType().Name + ": " + ex.Message;
            notes.Add($"{tag}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Step S.4e — parametrization of an object: does an applied dimension become DRIVING.</summary>
    /// <remarks>MEASURED: S.4d showed the key distinction of the task: <c>ksRadDimension</c>/<c>ksLinDimension</c>
    /// ARE ACCEPTED (live reference, binding held), but the status does not change. This is exactly what
    /// order §"Semantics" item 3 warns about: the presence of a dimension does not prove the presence of a
    /// driving dimension constraint. The remaining question: can an applied dimension be turned into a
    /// driving one.
    /// The wrapper declares the intended call for this — <c>ksDocument2D.ksParametrizeObjects(obj, par)</c>
    /// with the block <c>ko_ParametrisationParam</c>. The step tries it on the control circle that already
    /// has a dimension, and reads the status after. A positive outcome is "+"; a negative one is a record
    /// that the call was not accepted or was accepted with no effect, not a conclusion that "driving
    /// dimensions do not work".</remarks>
    private void ParametrizationRoute()
    {
        var step = _report.Begin("S.4e", "Параметризация: становится ли наложенный размер управляющим",
            "Превращает ли ksParametrizeObjects размер в управляющее ограничение, снимая степени свободы?");
        try
        {
            if (!BuildCircleDocument("s-param", step, out var editor, out var circleRef))
            {
                step.Fail("Контрольный документ для маршрута параметризации не построен.");
                return;
            }

            var before = StateOfCurrent(step, "param_before");
            step.Data["param_state_before"] = before;
            step.Data["param_state_before_interpreted"] = Interpret(before);

            var notes = new List<string>();

            // First the three dimensions are placed with the same accepted call as in S.4d: without a
            // dimension there is nothing to parametrize, and "not accepted" would be explained by the
            // absence of input.
            var radiusRef = ApplyRadiusDimension(editor, step, "param_radius");
            var dimX = ApplyLinearDimension(editor, step, "param_x", 0d, 0d, CircleCenterX, 0d, notes);
            var dimY = ApplyLinearDimension(editor, step, "param_y", 0d, 0d, 0d, CircleCenterY, notes);
            notes.Add($"размеры: радиус {Api5.Raw(radiusRef)}, X {Api5.Raw(dimX)}, Y {Api5.Raw(dimY)}");

            _doc!.RebuildDocument();
            var withDimensions = StateOfCurrent(step, "param_with_dimensions");
            step.Data["param_state_with_dimensions"] = withDimensions;
            step.Data["param_state_with_dimensions_interpreted"] = Interpret(withDimensions);

            var paramStruct = (short)StructType2DEnum.ko_ParametrisationParam;
            step.Data["ko_ParametrisationParam"] = paramStruct;

            var rawBlock = _app.GetParamStruct(paramStruct);
            step.Data["param_block_type"] = Api5.RuntimeName(rawBlock);
            notes.Add("GetParamStruct(9000 /* ko_ParametrisationParam */) → " + Api5.RuntimeName(rawBlock));

            var parametrized = (int?)null;
            try
            {
                // group=0 — the SELECTED objects are parametrized (API5 help). The reference is the one
                // ksCircle returned, not "the zero object": the addressing of this reference was confirmed
                // in S.4.
                parametrized = Api5.SafeInt(() => editor.ksParametrizeObjects(circleRef, rawBlock!));
                step.Data["parametrize_result"] = parametrized;
                notes.Add("ksParametrizeObjects(окружность, блок) → " + Api5.Raw(parametrized));
            }
            catch (Exception ex)
            {
                step.Data["parametrize_error"] = ex.GetType().Name + ": " + ex.Message;
                notes.Add("ksParametrizeObjects: " + ex.GetType().Name);
            }

            _doc!.RebuildDocument();
            var after = StateOfCurrent(step, "param_after");
            step.Data["param_state_after"] = after;
            step.Data["param_state_after_interpreted"] = Interpret(after);
            notes.Add($"статус после параметризации: {Interpret(after)} (сырое {Api5.Raw(after)})");

            step.Data["parametrization_notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (after is null)
            {
                step.Fail("Статус не прочитан после параметризации — различие не определено.");
                return;
            }

            if (after == WellConstrained)
            {
                _dimensionRouteWorks = true;
                step.Pass("Параметризация превратила наложенные размеры в управляющие: " +
                          $"{Interpret(withDimensions)} → {Interpret(after)}. Маршрут различает " +
                          "«полностью определён».");
                return;
            }

            step.Unknown($"Параметризация не привела к «полностью определён»: статус {Interpret(after)} " +
                         $"(сырое {Api5.Raw(after)}), с тремя размерами было {Interpret(withDimensions)}. " +
                         $"Что вернул вызов: {Api5.Raw(parametrized)}. Это измерение конкретного " +
                         "вызова на этой установке, а не вывод о том, что управляющие размеры не влияют " +
                         "на определённость в продукте.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут параметризации не проверен: " + ex.Message);
        }
    }

    /// <summary>Step S.5 — the heart of the task. The same circle in states differing ONLY by the
    /// constraints and dimensions actually applied.</summary>
    /// <remarks>
    /// The geometry in all states is set by the same numbers (R20 at the point 25,15). If the status
    /// differs, it differs by CONSTRAINTS, not by shape, and substituting "the coordinates matched" is
    /// impossible by construction. This is the order §"Stage 2" control.
    /// <b>The matrix is built with the routes that the experiments confirmed, and this is said
    /// plainly.</b> If no way to change the constraint system is confirmed, the step yields <c>Unknown</c>
    /// naming exactly what was missing: "there is no difference" and "there is nothing to change with" are
    /// different statements, and the second must not be passed off as the first.
    /// </remarks>
    private void ControlMatrix()
    {
        var step = _report.Begin("S.5", "Матрица состояний: свободная → управляемый радиус → фиксация центра",
            "Различает ли ConstraintsState состояния, отличающиеся только фактически наложенными связями?");

        if (!_constraintRouteWorks && !_dimensionRouteWorks && !_api7ConstraintRouteWorks)
        {
            step.Unknown("Ни один маршрут изменения системы ограничений не подтверждён (S.4: соединение " +
                         "не принято; S.4b: размер не принят или статус не изменил). Состояния, " +
                         "отличающиеся только связями, построить нечем, поэтому утверждать о различении " +
                         "нельзя. Чтение статуса измерено отдельно (S.4, S.6, S.7): оно работает, " +
                         "переживает reopen и не мутирует модель.");
            return;
        }

        // The API7 NewConstraint() route is accounted for separately: it could have been accepted while
        // neither the API5 constraint nor the dimension produced a state. Without this publication its
        // outcome affected nothing — the field was assigned and never read (a probe defect found by the
        // compiler, CS0414).
        step.Data["api7_constraint_route_works"] = _api7ConstraintRouteWorks;
        step.Data["constraint_route_works"] = _constraintRouteWorks;
        step.Data["dimension_route_works"] = _dimensionRouteWorks;
        step.Observe("подтверждённые маршруты правки ограничений: соединение API5 = "
            + _constraintRouteWorks + ", размер = " + _dimensionRouteWorks
            + ", NewConstraint API7 = " + _api7ConstraintRouteWorks);

        try
        {
            // State A: a circle with no constraints and no dimensions. The numbers are set, but a circle
            // has three degrees of freedom — the expectation is recorded but not declared a fact before
            // reading.
            if (!BuildCircleDocument("s-a-free", step, out var editorA, out var circleA))
            {
                step.Fail("Состояние A не построено.");
                return;
            }

            var a = StateOfCurrent(step, "a");

            var b = (int?)null;
            var c = (int?)null;
            var d = (int?)null;
            var appliedNotes = new List<string>();

            // State B: the centre is fixed WITHOUT a driving radius — part of the freedom is removed.
            if (_constraintRouteWorks)
            {
                var fixedCentre = TryConstraint(editorA, step, "b_center", (short)StructType2DEnum.ko_ConstraintParam,
                    LocalConstraintType.FixedPoint, circleA, index: 0, partner: 0, partnerIndex: 0);
                appliedNotes.Add("B: CONSTRAINT_FIXED_POINT центра → " + Api5.Raw(fixedCentre));
                _doc!.RebuildDocument();
                b = StateOfCurrent(step, "b");
            }

            // State C: a driving radius dimension is placed on the same sketch. The order's expectation is
            // full definition; it is recorded but not declared a fact before reading.
            if (_dimensionRouteWorks)
            {
                var dimension = ApplyRadiusDimension(editorA, step, "c_radius");
                appliedNotes.Add("C: ksRadDimension радиуса → " + Api5.Raw(dimension));
                _doc!.RebuildDocument();
                c = StateOfCurrent(step, "c");
            }

            // State D: three driving dimensions (radius + two linear ones for the centre). Only it can
            // remove all three degrees of freedom of the circle; states A and B are incompletely removed
            // by construction. A separate document, because dimensions are irreversible: adding them to the
            // same sketch would measure the SUM of steps, not a state.
            if (_dimensionRouteWorks
                && BuildCircleDocument("s-d-driving", step, out var editorD, out _))
            {
                var radius = ApplyRadiusDimension(editorD, step, "d_radius");
                var dimX = ApplyLinearDimension(editorD, step, "d_x", 0d, 0d, CircleCenterX, 0d, appliedNotes);
                var dimY = ApplyLinearDimension(editorD, step, "d_y", 0d, 0d, 0d, CircleCenterY, appliedNotes);
                appliedNotes.Add($"D: три размера → радиус {Api5.Raw(radius)}, X {Api5.Raw(dimX)}, " +
                                 $"Y {Api5.Raw(dimY)}");
                _doc!.RebuildDocument();
                d = StateOfCurrent(step, "d");
            }

            step.Data["a_raw"] = a;
            step.Data["b_raw"] = b;
            step.Data["c_raw"] = c;
            step.Data["d_raw"] = d;
            step.Data["a_interpreted"] = Interpret(a);
            step.Data["b_interpreted"] = Interpret(b);
            step.Data["c_interpreted"] = Interpret(c);
            step.Data["d_interpreted"] = Interpret(d);
            step.Data["applied_notes"] = appliedNotes;
            foreach (var note in appliedNotes)
            {
                step.Observe(note);
            }

            step.Observe($"A(без связей): {Interpret(a)} (сырое {Api5.Raw(a)}).");
            step.Observe($"B(центр закреплён): {Interpret(b)} (сырое {Api5.Raw(b)}).");
            step.Observe($"C(управляющий радиус): {Interpret(c)} (сырое {Api5.Raw(c)}).");
            step.Observe($"D(радиус + два линейных): {Interpret(d)} (сырое {Api5.Raw(d)}).");

            if (a is null)
            {
                step.Fail("Статус не прочитан в состоянии A — различие не определено.");
                return;
            }

            // Only the states that could be built are compared. Comparing "a state that did not exist"
            // would give a false difference or a false match.
            var built = new List<(string Name, int Value)> { ("A", a.Value) };
            if (b is not null)
            {
                built.Add(("B", b.Value));
            }

            if (c is not null)
            {
                built.Add(("C", c.Value));
            }

            if (d is not null)
            {
                built.Add(("D", d.Value));
            }

            step.Data["states_compared"] = string.Join(",", built.Select(x => x.Name));
            var distinct = built.Select(x => x.Value).Distinct().Count();
            step.Data["distinct_statuses"] = distinct;

            if (built.Count < 2)
            {
                step.Unknown("Построено только состояние " + built[0].Name + " — различать нечего. " +
                             "Матрица не измерена, и это не выдаётся за «статус не различает».");
                return;
            }

            if (distinct == 1)
            {
                step.Fail("Все построенные состояния (" + string.Join(",", built.Select(x => x.Name)) +
                          ") дали один статус (" + Interpret(a) + "), хотя связи различаются — маршрут " +
                          "не читает систему ограничений.");
                return;
            }

            // The decisive distinction of the order: A is under-defined, D is fully defined. C (radius
            // only) must stay under-defined — this is not "bad" but a check that the route counts degrees
            // of freedom, not the fact "a dimension was placed".
            var aUnder = a == LocalStateValues["ksStateUnderConstrained"];
            var cWell = c == LocalStateValues["ksStateWellConstrained"];
            var dWell = d == LocalStateValues["ksStateWellConstrained"];
            var cUnder = c == LocalStateValues["ksStateUnderConstrained"];
            step.Data["a_is_under"] = aUnder;
            step.Data["c_is_well"] = cWell;
            step.Data["c_is_under"] = cUnder;
            step.Data["d_is_well"] = dWell;

            if (aUnder && cUnder && dWell)
            {
                step.Pass($"Маршрут различает состояния по связям И считает степени свободы: без связей — " +
                          $"{Interpret(a)}, только радиус — {Interpret(c)} (свобода центра осталась), " +
                          $"радиус + два линейных — {Interpret(d)}. Контрольная матрица задания §«Этап 2» " +
                          "воспроизведена.");
                return;
            }

            if (aUnder && cWell)
            {
                step.Pass($"Маршрут различает состояния по связям: без связей — {Interpret(a)}, " +
                          $"центр закреплён — {Interpret(b)}, управляющий радиус — {Interpret(c)}. " +
                          "Матрица задания §«Этап 2» воспроизведена.");
                return;
            }

            // It differs, but not as the order assumed: this is a measurement, not a reason to edit the
            // expectation until it matches. The discrepancy is left open.
            step.Unknown($"Статусы различаются ({string.Join(" → ", built.Select(x => Interpret(x.Value)))})" +
                         ", но не совпали с ожиданием задания (A=недоопределён, D=определён). " +
                         "Ожидание записано, расхождение не закрыто и не подогнано.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Матрица состояний не измерена: " + ex.Message);
        }
    }

    /// <summary>Places a driving radial dimension on the circle with centre (<see cref="CircleCenterX"/>,
    /// <see cref="CircleCenterY"/>) and radius <see cref="CircleRadius"/>.</summary>
    /// <remarks>The binding is set by <c>RDimSource</c> (centre and radius), not by a curve reference —
    /// that is how the help example is built. <c>Init()</c> is called before filling the fields: it zeroes
    /// them (MEASURED: in the first run), and filling before it would lead to a default dimension. A
    /// reference to the dimension is returned; <c>null</c> means the call did not go through.</remarks>
    private int? ApplyRadiusDimension(ksDocument2D editor, ProbeStep step, string tag)
    {
        try
        {
            if (_app.GetParamStruct((short)StructType2DEnum.ko_RDimParam) is not Kompas6API5.ksRDimParam radial
                || _app.GetParamStruct((short)StructType2DEnum.ko_RDimSource) is not Kompas6API5.ksRDimSourceParam source)
            {
                step.Data[tag + "_result"] = "нет блока параметров";
                return null;
            }

            source.Init();
            source.xc = CircleCenterX;
            source.yc = CircleCenterY;
            source.rad = CircleRadius;
            var bindingHeld = Math.Abs(source.xc - CircleCenterX) < 1e-9
                              && Math.Abs(source.yc - CircleCenterY) < 1e-9
                              && Math.Abs(source.rad - CircleRadius) < 1e-9;
            step.Data[tag + "_binding_held"] = bindingHeld;
            step.Data[tag + "_binding"] = $"xc={source.xc},yc={source.yc},rad={source.rad}";
            if (!bindingHeld)
            {
                step.Data[tag + "_result"] = "привязка не удержана";
                return null;
            }

            step.Data[tag + "_setspar"] = Api5.Raw(radial.SetSPar(source));
            return Api5.SafeInt(() => editor.ksRadDimension(radial));
        }
        catch (Exception ex)
        {
            step.Data[tag + "_result"] = "исключение " + ex.GetType().Name;
            step.Data[tag + "_error"] = ex.Message;
            return null;
        }
    }

    /// <summary>Step S.5b: control states built BY READING, without applying constraints.</summary>
    /// <remarks>Order §"Stage 2" requires, among other things: an empty sketch, two sketches with different states
    /// and choosing the needed one. These controls are valuable because they do not depend on the write
    /// route: even when a constraint could not be applied, they show whether <c>ConstraintsState</c>
    /// answers to different sketch CONTENT rather than returning one constant for everything.
    /// An empty sketch is a separate case: the value "no objects ⇒ defined" is NOT declared in advance.
    /// What KOMPAS answers is read, and it is recorded as a measurement.</remarks>
    private void ReadOnlyControls()
    {
        var step = _report.Begin("S.5b", "Контроли чтением: пустой эскиз и выбор нужного эскиза из двух",
            "Отвечает ли ConstraintsState на разное содержимое эскиза, и что читается у пустого эскиза?");
        try
        {
            // ── Empty sketch ──
            var doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ для контроля «пустой эскиз» не создан.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            var plate = Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "s-empty");

            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity emptySketch
                || emptySketch.GetDefinition() is not ksSketchDefinition emptyDefinition)
            {
                step.Fail("Пустой эскиз не создан.");
                return;
            }

            emptySketch.name = "s-empty";
            emptyDefinition.SetPlane(plane);
            emptySketch.Create();

            var previousDoc = _doc;
            var previousPart = _part;
            var previousSketch = _sketchEntity;
            _doc = doc;
            _part = part;
            _sketchEntity = emptySketch;

            var emptyState = StateOfCurrent(step, "empty");
            step.Data["empty_state"] = emptyState;
            step.Data["empty_state_interpreted"] = Interpret(emptyState);
            step.Observe($"Пустой эскиз: {Interpret(emptyState)} (сырое {Api5.Raw(emptyState)}).");

            // ── Two sketches in one document, different content ──
            // The second sketch gets a circle; the first stays empty. This is a content difference built
            // without a single constraint, so it does not depend on the write route.
            if (part.NewEntity(Api5.Sketch) is not ksEntity secondSketch
                || secondSketch.GetDefinition() is not ksSketchDefinition secondDefinition)
            {
                step.Observe("Второй эскиз не создан — выбор среди двух не проверен.");
                step.Data["volume_empty_doc"] = Api5.Num(Api5.Volume(part));
                step.Data["plate_built"] = plate is not null;
                Restore(previousDoc, previousPart, previousSketch);
                VerdictForEmpty(step, emptyState);
                return;
            }

            secondSketch.name = "s-second";
            secondDefinition.SetPlane(plane);
            secondSketch.Create();
            if (secondDefinition.BeginEdit() is ksDocument2D secondEditor)
            {
                secondEditor.ksCircle(CircleCenterX, CircleCenterY, CircleRadius, 1);
                secondDefinition.EndEdit();
            }

            // The SECOND sketch is read while another lies first in the tree: this is a control for "the
            // sketch that was asked for is read", not "the first one encountered".
            _sketchEntity = secondSketch;
            var secondState = StateOfCurrent(step, "second");
            step.Data["second_state"] = secondState;
            step.Data["second_state_interpreted"] = Interpret(secondState);

            _sketchEntity = emptySketch;
            var emptyAgain = StateOfCurrent(step, "empty_again");
            step.Data["empty_state_again"] = emptyAgain;
            step.Data["distinct_by_content"] = emptyState is not null && secondState is not null && emptyState != secondState;
            step.Observe($"Эскиз с окружностью: {Interpret(secondState)} (сырое {Api5.Raw(secondState)}).");
            step.Observe($"Пустой эскиз, повторно: {Interpret(emptyAgain)} (сырое {Api5.Raw(emptyAgain)}).");

            step.Data["volume_empty_doc"] = Api5.Num(Api5.Volume(part));
            step.Data["plate_built"] = plate is not null;
            Restore(previousDoc, previousPart, previousSketch);

            if (emptyState is null || secondState is null)
            {
                step.Fail("Статус не прочитан хотя бы у одного из двух эскизов: пустой=" +
                          Api5.Raw(emptyState) + ", с окружностью=" + Api5.Raw(secondState) + ".");
                return;
            }

            if (emptyState != secondState)
            {
                step.Pass($"Статус отвечает на содержимое эскиза: пустой — {Interpret(emptyState)}, " +
                          $"с окружностью — {Interpret(secondState)}. Читается ИМЕННО запрошенный эскиз " +
                          "(после повторного чтения пустого статус вернулся к " + Interpret(emptyAgain) + ").");
            }
            else
            {
                step.Unknown($"Пустой эскиз и эскиз с окружностью дали один статус " +
                             $"({Interpret(emptyState)}) — на этом различии содержимого маршрут " +
                             "не различает. Записано как измерение.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Контроли чтением не выполнены: " + ex.Message);
        }
    }

    /// <summary>Returns the probe's attention to the previous document after a control.</summary>
    private void Restore(ksDocument3D? doc, ksPart? part, ksEntity? sketch)
    {
        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
    }

    /// <summary>Verdict for the "second sketch not created" branch: the empty sketch is measured anyway.</summary>
    private static void VerdictForEmpty(ProbeStep step, int? emptyState)
    {
        if (emptyState is null)
        {
            step.Fail("Статус пустого эскиза не прочитан.");
            return;
        }

        step.Pass($"Пустой эскиз прочитан: {Interpret(emptyState)}. Выбор среди двух эскизов в этом " +
                  "прогоне не проверен (второй эскиз не создан), и это указано как непроверенное.");
    }

    private void ReopenAndStability()
    {
        var step = _report.Begin("S.6", "save→close→reopen, выбор нужного эскиза и повторное чтение",
            "Читается ли статус в переоткрытом документе и стабилен ли он без изменения модели?");
        try
        {
            if (_doc is null || _part is null)
            {
                step.Unknown("Документ недоступен от предыдущего шага.");
                return;
            }

            var before = StateOfCurrent(step, "before_reopen");
            step.Data["before_reopen"] = before;
            step.Data["before_reopen_interpreted"] = Interpret(before);

            var path = Path.Combine(_options.WorkDir, "s-definition.m3d");
            step.Data["saved_path"] = path;
            if (_doc.SaveAs(path) != true)
            {
                step.Fail("SaveAs не подтверждён возвращаемым значением.");
                return;
            }

            step.Data["closed"] = Api5.Raw(TryBool(() => _doc!.close()));
            _doc = (ksDocument3D)_app.Document3D();
            if (_doc.Open(path, true) != true)
            {
                step.Fail("Open переоткрытого документа не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            step.Data["volume_after_reopen"] = Api5.Num(Api5.Volume(_part));

            // The sketch is searched by MODEL, not by a saved descriptor: session memory is not used.
            if (!FindSketchInReopened(step))
            {
                step.Fail("Эскиз в переоткрытом документе не найден — статус прочитать нечем.");
                return;
            }

            var afterReopen = StateOfCurrent(step, "after_reopen");
            var again = StateOfCurrent(step, "after_reopen_second");
            step.Data["after_reopen"] = afterReopen;
            step.Data["after_reopen_interpreted"] = Interpret(afterReopen);
            step.Data["after_reopen_second_read"] = again;
            step.Data["stable_across_reads"] = afterReopen == again;

            if (afterReopen is null)
            {
                step.Fail("Статус в переоткрытом документе не прочитан.");
                return;
            }

            if (afterReopen == before && afterReopen == again)
            {
                step.Pass($"Статус пережил save→close→reopen без изменения ({Interpret(afterReopen)}), " +
                          "и повторное чтение дало то же значение — значение не кэшируется клиентом.");
            }
            else if (afterReopen == again)
            {
                step.Unknown($"До reopen {Interpret(before)}, после reopen {Interpret(afterReopen)}; " +
                             "повторное чтение совпало. Изменение — измеренное поведение, а не отказ маршрута.");
            }
            else
            {
                step.Fail($"Повторное чтение БЕЗ изменения модели дало разные значения " +
                          $"({Api5.Raw(afterReopen)} и {Api5.Raw(again)}) — значение недостоверно.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Шаг reopen не выполнен: " + ex.Message);
        }
    }

    /// <summary>Step S.7: side effects of reading. A separate step, because whether the tool can be
    /// registered as reading depends on it.</summary>
    private void ReadIsSideEffectFree()
    {
        var step = _report.Begin("S.7", "Побочные эффекты чтения: объём, топология, признак изменения",
            "Меняет ли чтение статуса модель, ревизию или состояние несохранённых изменений?");
        try
        {
            if (_doc is null || _part is null || _sketchEntity is null)
            {
                step.Unknown("Документ недоступен — проверка побочных эффектов не выполнялась.");
                return;
            }

            var volumeBefore = Api5.Volume(_part);
            var bodiesBefore = Api5.BodyCount(_part);
            var facesBefore = Api5.FaceCount(_part);
            var edgesBefore = Api5.EdgeCount(_part);

            step.Data["volume_before"] = Api5.Num(volumeBefore);
            step.Data["bodies_before"] = bodiesBefore;
            step.Data["faces_before"] = facesBefore;
            step.Data["edges_before"] = edgesBefore;

            // A five-fold read — this is how the tool will behave under load and on repeated calls.
            for (var i = 0; i < 5; i++)
            {
                _ = StateOfCurrent(step, "side_effect_probe" + i);
            }

            var volumeAfter = Api5.Volume(_part);
            var bodiesAfter = Api5.BodyCount(_part);
            var facesAfter = Api5.FaceCount(_part);
            var edgesAfter = Api5.EdgeCount(_part);

            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["bodies_after"] = bodiesAfter;
            step.Data["faces_after"] = facesAfter;
            step.Data["edges_after"] = edgesAfter;

            // A zero volume is "the body vanished", and it must be a refusal, not a successful
            // measurement: this was already hit in the sketch edit (Q-SKETCH-EDIT-ZERO).
            if (volumeAfter is null or 0d || volumeBefore is null or 0d)
            {
                step.Fail("Объём тела равен " + Api5.Num(volumeAfter) + " (до чтения " +
                          Api5.Num(volumeBefore) + ") — либо тело исчезло, либо замер недействителен.");
                return;
            }

            var volumeHeld = Math.Abs(volumeAfter.Value - volumeBefore.Value) < 1e-6;
            var countsHeld = bodiesAfter == bodiesBefore && facesAfter == facesBefore && edgesAfter == edgesBefore;
            step.Data["volume_held"] = volumeHeld;
            step.Data["counts_held"] = countsHeld;

            if (volumeHeld && countsHeld)
            {
                step.Pass("Пятикратное чтение статуса не изменило ни объём, ни число тел/граней/рёбер. " +
                          "Чтение не мутирует геометрию.");
            }
            else
            {
                step.Fail($"Чтение изменило модель: объём удержан={volumeHeld}, счётчики удержаны={countsHeld}. " +
                          "Регистрировать инструмент как читающий нельзя.");
            }
        }
        catch (Exception ex)
        {
            step.Fail("Проверка побочных эффектов не выполнена: " + ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════ construction ══

    /// <summary>Builds a document with a plate and a sketch holding one R20 circle at (25,15).</summary>
    /// <remarks>By default the editor is returned OPEN so that the caller can apply constraints via API5.
    /// With <paramref name="closeEditor"/> = <c>true</c> editing is finished: this is needed by the API7
    /// branch, where <c>ISketch.BeginEdit()</c> refuses while the sketch is open on the API5 side
    /// (MEASURED: <c>BeginEdit() → null</c>).</remarks>
    private bool BuildCircleDocument(string name, ProbeStep step, out ksDocument2D editor, out int circleRef,
        bool closeEditor = false)
    {
        editor = null!;
        circleRef = 0;

        var doc = (ksDocument3D)_app.Document3D();
        if (doc.Create(true, true) != true)
        {
            step.Observe($"Документ «{name}» не создан.");
            return false;
        }

        var part = (ksPart)doc.GetPart(-1);
        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, name) is null)
        {
            step.Observe($"Пластина для «{name}» не построена.");
            return false;
        }

        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Observe($"Эскиз «{name}» не создан.");
            return false;
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        if (definition.BeginEdit() is not ksDocument2D activeEditor)
        {
            step.Observe($"BeginEdit для «{name}» вернул не ksDocument2D.");
            return false;
        }

        // The numeric geometry is the same in all states: an R20 circle at the point (25,15).
        var circle = activeEditor.ksCircle(CircleCenterX, CircleCenterY, CircleRadius, 1);
        if (circle == 0)
        {
            step.Observe($"ksCircle в «{name}» вернул 0 — окружность не создана.");
            definition.EndEdit();
            return false;
        }

        if (closeEditor)
        {
            var ended = definition.EndEdit();
            step.Data[name + "_endedit"] = ended;
            step.Observe($"«{name}»: редактор закрыт (EndEdit → {ended}), окружность R20 в (25,15), ссылка {circle}.");
            editor = activeEditor;
            circleRef = circle;
            _doc = doc;
            _part = part;
            _sketchEntity = sketch;
            return true;
        }

        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
        editor = activeEditor;
        circleRef = circle;
        step.Data[name + "_circle_ref"] = circle;
        step.Observe($"«{name}»: окружность R20 в (25,15), ссылка {circle}.");
        return true;
    }

    /// <summary>Sketch of the reopened document: searched by model, session memory is not used.</summary>
    private bool FindSketchInReopened(ProbeStep step)
    {
        if (_part is null)
        {
            return false;
        }

        var seen = new List<string>();
        foreach (var objType in new short[] { Api5.Sketch, 15, 16 })
        {
            object? collection = null;
            try
            {
                collection = _part.EntityCollection(objType);
            }
            catch (Exception ex)
            {
                seen.Add($"{objType}: ошибка " + ex.GetType().Name);
                continue;
            }

            if (collection is not ksEntityCollection entities)
            {
                seen.Add($"{objType}: " + Api5.RuntimeName(collection));
                continue;
            }

            var count = Api5.SafeInt(() => entities.GetCount());
            seen.Add($"{objType}:{count?.ToString() ?? "?"}");
            for (var i = 0; i < (count ?? 0); i++)
            {
                var entity = entities.GetByIndex(i) as ksEntity;
                if (entity?.GetDefinition() is not ksSketchDefinition)
                {
                    continue;
                }

                _sketchEntity = entity;
                step.Data["reopen_found_by_objtype"] = objType;
                step.Data["reopen_sketch_name"] = entity.name;
                return true;
            }
        }

        step.Data["reopen_collections"] = string.Join(", ", seen);
        return false;
    }

    /// <summary>Step S.8 — a sketch created OUTSIDE the MCP: a third-party document from the distribution.</summary>
    /// <remarks>Order §"Stage 2" requires the control "a sketch created outside the MCP, with the source and
    /// provenance of the test file". Before this step there was no such control: every sketch was placed
    /// by the probe itself, and the conclusion "readable" would apply only to its own models.
    /// The files come from the KOMPAS-3D distribution (<c>Samples\Models</c>); their provenance is the
    /// vendor, the author a human. The files <b>are opened but not modified or saved</b>: the step does
    /// not mutate a foreign document. For each document the sketches are enumerated by tree, and the
    /// status is read — by exactly the same call as for its own models.
    /// If a document has no sketches, this is recorded as "nothing to read", not as "the status is not
    /// readable": these two cases must be told apart.</remarks>
    private void ExternalDocumentRoute()
    {
        var step = _report.Begin("S.8", "Эскиз, созданный вне MCP: сторонний документ поставки",
            "Читается ли статус у эскизов, которых MCP не создавал, без изменения чужого файла?");
        try
        {
            var samples = new[]
            {
                Path.Combine(_options.KompasRoot, "Samples", "Models", "Bracket.m3d"),
                Path.Combine(_options.KompasRoot, "Samples", "Models", "Receptacle.m3d"),
                Path.Combine(_options.KompasRoot, "Samples", "Models", "Gear-shaft.m3d"),
                Path.Combine(_options.KompasRoot, "Samples", "Models", "Cylindrical cam.m3d"),
            };

            var sources = new List<string>();
            var notes = new List<string>();
            var statuses = new List<string>();

            foreach (var sample in samples)
            {
                if (!File.Exists(sample))
                {
                    notes.Add(Path.GetFileName(sample) + ": файла нет");
                    continue;
                }

                var size = new FileInfo(sample).Length;
                notes.Add($"{Path.GetFileName(sample)}: {size} байт");

                var doc = (ksDocument3D)_app.Document3D();
                if (doc.Open(sample, true) != true)
                {
                    notes.Add(Path.GetFileName(sample) + ": Open не вернул true");
                    continue;
                }

                try
                {
                    var part = (ksPart)doc.GetPart(-1);
                    var sketches = new List<ksEntity>();
                    CollectSketchEntities(part, sketches, depth: 0, limit: 40);

                    if (sketches.Count == 0)
                    {
                        notes.Add(Path.GetFileName(sample) + ": эскизов в дереве нет — читать нечего");
                        continue;
                    }

                    var read = 0;
                    foreach (var sketch in sketches)
                    {
                        if (TransferSketch(sketch, step) is not KompasAPI7.ISketch api7Sketch)
                        {
                            notes.Add($"{Path.GetFileName(sample)}/{sketch.name}: перенос в ISketch не дал объект");
                            continue;
                        }

                        try
                        {
                            var raw = (int)api7Sketch.ConstraintsState;
                            read++;
                            statuses.Add($"{Path.GetFileName(sample)}/{sketch.name}={NativeName(raw)}({raw})");
                            notes.Add($"{Path.GetFileName(sample)}/{sketch.name}: " +
                                      $"{Interpret(raw)} (сырое {raw})");
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"{Path.GetFileName(sample)}/{sketch.name}: отказ COM " +
                                      ex.GetType().Name + ": " + ex.Message);
                        }
                    }

                    sources.Add($"{Path.GetFileName(sample)}: эскизов {sketches.Count}, прочитано {read}");
                }
                finally
                {
                    // The foreign document is closed WITHOUT saving: the step only reads, and that is its
                    // condition. A Save here would mutate a user file.
                    TryBool(() => doc.close());
                }
            }

            step.Data["external_sources"] = sources;
            step.Data["external_statuses"] = statuses;
            step.Data["external_notes"] = notes;
            foreach (var note in notes)
            {
                step.Observe(note);
            }

            if (statuses.Count == 0)
            {
                step.Unknown("Ни в одном стороннем документе статус не прочитан: " +
                             "это запись о пройденном пути, а не вывод о продукте.");
                return;
            }

            var distinct = statuses.Select(s => s[(s.LastIndexOf('=') + 1)..]).Distinct().Count();
            step.Data["external_distinct_statuses"] = distinct;

            if (distinct >= 2)
            {
                step.Pass($"Статус читается у чужих эскизов и различается между ними: " +
                          $"{string.Join("; ", statuses)}. Файлы поставки открывались только на чтение " +
                          "и не сохранялись. Это закрывает контроль «эскиз, созданный вне MCP».");
                return;
            }

            step.Pass($"Статус читается у эскизов, созданных вне MCP ({string.Join("; ", statuses)}), " +
                      "но во всех прочитанных состояниях одно значение — различие между чужими " +
                      "эскизами этим шагом не показано. Файлы не изменялись.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Маршрут по сторонним документам не проверен: " + ex.Message);
        }
    }

    /// <summary>Collects sketches from the part tree by walking it, with depth and count limits.</summary>
    /// <remarks>The walk is needed because a sketch may lie in the part itself or inside a feature. The
    /// count is limited: the step reads rather than inventories a foreign model, and "not found due to the
    /// limit" differs from "not found at all" only by that bound, which is recorded in the step
    /// data.</remarks>
    private static void CollectSketchEntities(ksPart part, List<ksEntity> found, int depth, int limit)
    {
        if (depth > 4 || found.Count >= limit)
        {
            return;
        }

        for (short objType = 0; objType <= 6; objType++)
        {
            object? raw;
            try
            {
                raw = part.EntityCollection(objType);
            }
            catch
            {
                continue;
            }

            if (raw is not ksEntityCollection collection)
            {
                continue;
            }

            int count;
            try
            {
                count = collection.GetCount();
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < count && found.Count < limit; i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                object? definition = null;
                try
                {
                    definition = entity.GetDefinition();
                }
                catch
                {
                    // A refusal on the definition is not turned into "there is no sketch".
                }

                if (definition is ksSketchDefinition)
                {
                    // Deduplication by name: ksEntity has no reference, and the same sketch lands in
                    // several type collections. The name is what is available and human-readable.
                    if (!found.Any(f => f.name == entity.name))
                    {
                        found.Add(entity);
                    }
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════ reading ══

    /// <summary>Reads <c>ISketch.ConstraintsState</c> of the current sketch.</summary>
    private int? StateOfCurrent(ProbeStep step, string tag)
    {
        if (_sketchEntity is null)
        {
            step.Data[tag + "_state_error"] = "эскиз не сохранён";
            return null;
        }

        var api7Sketch = TransferSketch(_sketchEntity, step);
        if (api7Sketch is null)
        {
            step.Data[tag + "_state_error"] = "перенос в API7 не дал ISketch";
            return null;
        }

        try
        {
            var raw = (int)api7Sketch.ConstraintsState;
            step.Data[tag + "_state_raw"] = raw;
            step.Data[tag + "_state_name"] = NativeName(raw);
            return raw;
        }
        catch (Exception ex)
        {
            // A COM refusal is not "under-defined": it is written in a separate field so that "the product
            // answered" and "the call did not go through" are not mixed.
            step.Data[tag + "_state_error"] = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Transfers an API5 sketch into the API7 <c>ISketch</c>.</summary>
    /// <remarks>The QI is done explicitly: a wrapper declaring an interface does not guarantee the object
    /// supports it, and a silent <c>null</c> from <c>as</c> here would mean "there is no status" instead
    /// of "the cast did not go through". Both cases are distinguishable in the step data.</remarks>
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

    // ══════════════════════════════════════════════════════════════ semantics ══

    /// <summary>The <c>ksConstraintsStateEnum</c> values from <c>Bin\ksConstants.tlb</c>.</summary>
    /// <remarks>A local copy: the enumeration lies in the constants assembly, which the probe does not
    /// link just for four numbers. The check against the declared ones is step S.3, and without it the
    /// numbers would count as a claim about the author's memory rather than about the product.</remarks>
    private static readonly Dictionary<string, int> LocalStateValues = new(StringComparer.Ordinal)
    {
        ["ksStateUnknown"] = 0,
        ["ksStateWellConstrained"] = 1,
        ["ksStateUnderConstrained"] = 2,
        ["ksStateUnresolvedRedundancy"] = 3,
    };

    /// <summary>The constraint types used by the probe.</summary>
    /// <remarks>
    /// The values are checked against the SDK help table "Parametric constraint types" (help.ascon.ru,
    /// <c>paramrestrictiontypes</c>), which lists the constants accepted by the <c>constrType</c> field:
    /// <c>CONSTRAINT_FIXED_POINT = 1</c> ("point fixing" — single, needs no partner) and
    /// <c>CONSTRAINT_EQUAL_RADIUS = 8</c> ("equality of the radii of two arcs or circles" — paired; it is
    /// the one shown in the example for <c>ksSetObjConstraint</c>).
    /// <c>FixedDim = 14</c> is deliberately NOT included here: the export-constants table has no number 14
    /// (there <c>13</c> is absent and <c>15</c> is <c>CONSTRAINT_TANGENT_TWO_CURVES</c>). The value 14 as
    /// "fixed dimension" is known only to the API7 enumeration
    /// <c>ksConstraintTypeEnum.ksCFixedDim</c>, i.e. it belongs to a DIFFERENT namespace, and they must
    /// not be mixed in one field. A driving dimension is not placed through this call by the probe.
    /// </remarks>
    private enum LocalConstraintType
    {
        /// <summary>Point fixing (index 0 on a circle is the centre).</summary>
        FixedPoint = 1,

        /// <summary>Equality of the radii of two arcs or circles.</summary>
        EqualRadius = 8,
    }

    /// <summary>Normalized task status. The answer is built from the NUMBER: the wrapper may not return a
    /// name, a number it will always return. A number outside the declared set is "unknown", not a guess
    /// by magnitude.</summary>
    private static string Interpret(int? raw) => raw switch
    {
        null => "unknown",
        1 => "fully_defined",
        2 => "under_defined",
        3 => "needs_attention",
        0 => "unknown",
        _ => "unknown",
    };

    private static string NativeName(int raw) => raw switch
    {
        1 => "ksStateWellConstrained",
        2 => "ksStateUnderConstrained",
        3 => "ksStateUnresolvedRedundancy",
        0 => "ksStateUnknown",
        _ => "неизвестное значение " + raw,
    };

    // ═════════════════════════════════════════════════════════════════ misc ══

    private static uint[] KompasIds() =>
        Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();

    private static uint[] WaitForNewProcess(uint[] before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var fresh = KompasIds().Where(id => !before.Contains(id)).ToArray();
            if (fresh.Length > 0)
            {
                return fresh;
            }

            Thread.Sleep(500);
        }

        return Array.Empty<uint>();
    }

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(500);
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
}
