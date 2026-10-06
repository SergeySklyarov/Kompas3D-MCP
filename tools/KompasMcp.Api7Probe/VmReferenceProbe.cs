using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Builds the VM acceptance reference: a parametrized part carrying two EXTERNAL variables.</summary>
/// <remarks>
/// <b>Why a separate mode and not part of the VM acceptance group.</b> The order
/// (<c>VARIABLES_MATERIAL_REWORK_REVIEW_20261006.md</c> §2) requires the reference to be produced BY the
/// executor, and creating variables is <b>not</b> an MCP function: the acceptance group must OPEN the
/// file, never create the very thing it then measures. The reference is therefore produced here, once,
/// by an instrument that touches KOMPAS directly and writes a file the MCP group only opens.
/// <b>Route.</b> DOC <c>ipart7_addvariable.html</c>: <c>IPart7::AddVariable(Name, Value, Note)</c> returns
/// <c>IVariable7</c> — «Метод позволяет добавить переменную в массив переменных и документ», the third
/// parameter is a NOTE and the formula is set separately through <c>IVariable7::Expression</c>
/// (<c>ivariable7_expression.html</c>). DOC <c>ipart7.html</c> note 3: the top component is obtained from
/// <c>IKompasDocument3D::TopPart</c>. DOC <c>ivariable7_external.html</c>: <c>External</c> is the
/// external-variable flag. The API5 <c>ksPart.VariableCollection()</c> route is a VIEW of the same array
/// and is read back here to prove the variables landed in the model, not only in the returned handle.
/// <b>Names.</b> DOC <c>1862_175_1_sozd_peremen.html</c>: a variable name may contain Latin letters,
/// digits and <c>_</c>, and must start with a letter or <c>_</c>. The Cyrillic names of the first attempt
/// are therefore ILLEGAL input, and the probe measures that with <c>IPart7::IsVariableNameValid</c>
/// rather than asserting it.
/// <b>Geometry.</b> <c>100×80</c> rectangle, extrusion depth <c>10</c> mm — the geometry the review
/// pre-defines for VM-02 (volume <c>80000</c> mm³, and <c>160000</c> at depth <c>20</c>). The controlling
/// variable <c>depth</c> drives the extrusion depth; the dependent <c>full_depth</c> carries the
/// expression <c>depth*2</c>, so changing the first must change the second through the kernel's own
/// expression evaluator — the check VM-03 needs.
/// History: docs/decisions/variables-material.md#reference
/// </remarks>
internal sealed class VmReferenceProbe
{
    public const double WidthMm = 100d;
    public const double HeightMm = 80d;
    public const double BaseDepthMm = 10d;
    public const double ChangedDepthMm = 20d;

    /// <summary>Name of the variable that controls the extrusion depth. Latin, per the name rules.</summary>
    public const string DepthVariable = "depth";

    /// <summary>Name of the variable whose value is a kernel-evaluated expression of the first.</summary>
    public const string TotalVariable = "full_depth";

    /// <summary>The expression KOMPAS itself must evaluate. Fixed in the reference, not assembled at run time.</summary>
    public const string TotalExpression = "depth*2";

    /// <summary>The Cyrillic names of the FIRST attempt — illegal per the help, kept to MEASURE that.</summary>
    public const string IllegalDepthName = "Глубина";
    public const string IllegalTotalName = "ПолнаяГлубина";

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    /// <summary>The step currently running, so an unhandled exception is attributed to the step that
    /// raised it rather than to the first one — the defect the first version of this probe had.</summary>
    private ProbeStep? _current;

    public VmReferenceProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-reference.json"),
            Path.Combine(options.ReportDir, "vm-reference.md"));
    }

    /// <summary>Open a step and remember it, so an exception is attributed to the step that raised it.</summary>
    private ProbeStep Begin(string id, string title, string question)
    {
        _current = _report.Begin(id, title, question);
        return _current;
    }

    public void Run(string path)
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            Build(path);
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = Begin("VR.0", "Сеанс КОМПАС для подготовки эталона",
            "Можно ли собрать параметризованную деталь с внешними переменными документированными средствами?");
        var clock = Stopwatch.StartNew();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));
        clock.Stop();

        step.Observe("создан экземпляр API5 за " + clock.ElapsedMilliseconds + " мс");
        step.Observe("свой процесс: " + (_ownPid == 0 ? "не определён" : _ownPid.ToString()));
        step.Pass("сеанс поднят");
    }

    private void Build(string path)
    {
        var geometry = Begin("VR.1", "Геометрия эталона: 100×80, базовая глубина 10",
            "Строится ли геометрия, которую §2 задаёт для VM-02?");
        var doc = NewPart(geometry);
        if (doc is null)
        {
            geometry.Fail("документ-деталь не создан");
            return;
        }

        try
        {
            if (doc.GetPart(-1) is not ksPart part)
            {
                geometry.Fail("GetPart(-1) не дал верхний компонент");
                return;
            }

            var extrusion = Api5.BasePlate(part, WidthMm, HeightMm, BaseDepthMm, geometry, "vm");
            if (extrusion is null)
            {
                geometry.Fail("базовое выдавливание не построено");
                return;
            }

            var volumeBase = Api5.Volume(part);
            geometry.Observe("объём базовой детали: " + Api5.Num(volumeBase) + " мм³ (ожидание 80000)");
            if (volumeBase is null || Math.Abs(volumeBase.Value - WidthMm * HeightMm * BaseDepthMm) > 0.01)
            {
                geometry.Fail("объём базовой геометрии не совпал с 100·80·10");
                return;
            }

            geometry.Pass("геометрия 100×80×10 построена, объём " + Api5.Num(volumeBase) + " мм³");

            var (document7, part7) = TopPart7(doc, geometry);
            if (document7 is null || part7 is null)
            {
                return;
            }

            // ── VR.2: the name rules, MEASURED on the kernel rather than asserted ───────────────────
            var names = Begin("VR.2", "Допустимость имён переменных",
                "Почему прежние входы «Глубина»/«ПолнаяГлубина» не могли создать переменную?");
            var legalDepth = Api5.SafeBool(() => part7.IsVariableNameValid(DepthVariable));
            var legalTotal = Api5.SafeBool(() => part7.IsVariableNameValid(TotalVariable));
            var illegalDepth = Api5.SafeBool(() => part7.IsVariableNameValid(IllegalDepthName));
            var illegalTotal = Api5.SafeBool(() => part7.IsVariableNameValid(IllegalTotalName));
            names.Observe($"IsVariableNameValid(\"{DepthVariable}\") → {Api5.Raw(legalDepth)}");
            names.Observe($"IsVariableNameValid(\"{TotalVariable}\") → {Api5.Raw(legalTotal)}");
            names.Observe($"IsVariableNameValid(\"{IllegalDepthName}\") → {Api5.Raw(illegalDepth)}");
            names.Observe($"IsVariableNameValid(\"{IllegalTotalName}\") → {Api5.Raw(illegalTotal)}");
            names.Data["depth"] = legalDepth;
            names.Data["full_depth"] = legalTotal;
            names.Data["cyrillic_depth"] = illegalDepth;
            names.Data["cyrillic_full_depth"] = illegalTotal;
            if (legalDepth == true && legalTotal == true && illegalDepth == false && illegalTotal == false)
            {
                names.Pass("допустимы латинские имена, кириллические отвергнуты ядром");
            }
            else
            {
                names.Fail("ядро ответило не так, как требует справка о формате имён");
            }

            // ── VR.3: the two variables through the documented AddVariable route ────────────────────
            var variables = Begin("VR.3", "Внешние переменные через IPart7.AddVariable",
                "Несёт ли верхний компонент внешние переменные через документированный маршрут?");
            var depthVar = AddExternal(part7, DepthVariable, BaseDepthMm, "глубина эталона", variables);
            var totalVar = AddExternal(part7, TotalVariable, BaseDepthMm * 2d, "полная глубина эталона", variables);
            var collection = ReadCollection(part, variables);
            variables.Data["top_part_count"] = collection.Count;
            variables.Data["top_part_names"] = collection.Names;
            if (depthVar is null || totalVar is null || !collection.Names.Contains(DepthVariable)
                || !collection.Names.Contains(TotalVariable))
            {
                variables.Fail("внешние переменные не подтверждены коллекцией верхнего компонента: "
                    + "count=" + collection.Count + ", имена=" + string.Join(",", collection.Names));
                return;
            }

            variables.Pass("верхний компонент несёт обе внешние переменные: " + string.Join(", ", collection.Names));

            // ── VR.4: the assignment methods of the two variables ──────────────────────────────────
            // The controlling variable keeps the constant expression the kernel gives it (measured in
            // VR.3 as the value it was added with); the dependent one gets the formula. The empty
            // expression is NOT a supported state — measured separately in VR.8 — so it is not attempted
            // on a variable the reference depends on.
            var expression = Begin("VR.4", "Выражение зависимой переменной full_depth = depth*2",
                "Принимает ли ядро выражение, заданное отдельным свойством переменной?");
            var setExpr = Api5.SafeBool(() =>
            {
                totalVar.Expression = TotalExpression;
                return true;
            });
            expression.Observe("full_depth.Expression = «" + TotalExpression + "» → " + Api5.Raw(setExpr));
            var exprRead = Api5.SafeObject(() => totalVar.Expression);
            var depthExpr = Api5.SafeObject(() => depthVar.Expression);
            expression.Observe("прочитано обратно: full_depth=«" + Api5.Raw(exprRead)
                + "», depth=«" + Api5.Raw(depthExpr) + "»");
            expression.Data["expression_after"] = exprRead;
            expression.Data["depth_expression"] = depthExpr;
            if (Api5.Raw(exprRead) == TotalExpression)
            {
                expression.Pass("выражение сохранено и прочитано обратно");
            }
            else
            {
                expression.Fail("выражение не прочитано обратно: «" + Api5.Raw(exprRead) + "»");
            }

            // ── VR.5: bind the extrusion depth to the user variable `depth` ────────────────────────
            var binding = Begin("VR.5", "Связь глубины выдавливания с переменной depth",
                "Начинает ли глубина операции зависеть от пользовательской переменной?");
            var bound = BindDepth(part, part7, extrusion, depthVar, binding);

            // ── VR.6: drive the variable and measure the geometry it must move ─────────────────────
            var drive = Begin("VR.6", "Проверка управления геометрией: 10 → 20 мм",
                "Перестраивается ли модель до объёма, который задаёт описание эталона?");
            DriveAndMeasure(doc, document7, part, part7, extrusion, depthVar, bound, drive);

            // ── VR.7: save ────────────────────────────────────────────────────────────────────────
            var save = Begin("VR.7", "Сохранение эталона на диск",
                "Записывается ли файл, который приёмка затем только открывает?");
            Save(doc, save, path, part, part7);

            // ── VR.8: is an EMPTY expression a supported state? ───────────────────────────────────
            // Measured on a THROWAWAY variable, AFTER the file is written, so the reference is untouched.
            // The shipping adapter's value mode refuses a write over a non-empty expression, and whether a
            // user variable can carry NO expression decides whether that rule is usable at all.
            var empty = Begin("VR.8", "Пустое выражение переменной — поддерживаемое состояние?",
                "Может ли пользовательская переменная существовать без выражения?");
            ProbeEmptyExpression(part7, part, empty);
        }
        catch (Exception ex)
        {
            var failed = _current ?? geometry;
            failed.Fail("шаг бросил " + ex.GetType().Name + ": " + ex.Message);
            failed.Errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                doc.close();
            }
            catch (Exception)
            {
                // The session's teardown is not this step's subject.
            }
        }
    }

    /// <summary>The top component of an open part as the API7 <c>IPart7</c>.</summary>
    /// <remarks>DOC <c>ipart7.html</c> note 3: <c>IKompasDocument3D::TopPart</c> yields the top component;
    /// <c>ksAPI7Dual</c> is the transfer mode the shipping adapter already uses.</remarks>
    private (IKompasDocument3D? Document7, IPart7? Part7) TopPart7(ksDocument3D doc, ProbeStep step)
    {
        var document7 = Api5.SafeObject(() => _app.TransferInterface(doc, 2 /* ksAPI7Dual */, 0)) as IKompasDocument3D;
        if (document7 is null)
        {
            step.Fail("документ не отвечает на QI(IKompasDocument3D)");
            return (null, null);
        }

        var top = document7.TopPart;
        step.Observe("TopPart → " + Api5.RuntimeName(top));
        if (top is not IPart7 part7)
        {
            step.Fail("TopPart не отвечает на QI(IPart7)");
            return (document7, null);
        }

        return (document7, part7);
    }

    /// <summary>Create one EXTERNAL variable through the documented route and read it back.</summary>
    private static IVariable7? AddExternal(
        IPart7 part7, string name, double value, string note, ProbeStep step)
    {
        var made = Api5.SafeObject(() => part7.AddVariable(name, value, note));
        step.Observe("AddVariable(«" + name + "», " + Api5.Num(value) + ", «" + note + "») → "
            + Api5.RuntimeName(made));
        if (made is not IVariable7 variable)
        {
            return null;
        }

        // DOC <c>ivariable7_external.html</c>: External is the flag of an external variable; a user
        // variable may carry it (1786_173_3_tipi_peremen.html). The write is followed by a READ, because
        // the setter returning is not the same fact as the flag being set.
        var set = Api5.SafeBool(() =>
        {
            variable.External = true;
            return true;
        });
        var external = Api5.SafeObject(() => variable.External);
        step.Observe("«" + name + "».External = true → " + Api5.Raw(set) + ", прочитано: " + Api5.Raw(external));
        var readName = Api5.SafeObject(() => variable.Name);
        var readValue = Api5.SafeDouble(() => variable.Value);
        step.Observe("«" + name + "»: Name=«" + Api5.Raw(readName) + "», Value=" + Api5.Raw(readValue));
        return variable;
    }

    /// <summary>The top component's external-variable array, read through the API5 view.</summary>
    /// <remarks>DOC <c>kspart_variablecollection.html</c>: <c>IPart.VariableCollection()</c> is «массив
    /// внешних переменных». It is read here as an INDEPENDENT view of the same array, so a variable that
    /// only exists in the returned handle cannot pass for one that exists in the model.</remarks>
    private static (int Count, List<string> Names) ReadCollection(ksPart part, ProbeStep step)
    {
        if (part.VariableCollection() is not ksVariableCollection collection)
        {
            step.Observe("ksPart.VariableCollection() не дал коллекцию");
            return (0, new List<string>());
        }

        var count = Api5.SafeInt(collection.GetCount) ?? -1;
        var names = new List<string>();
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            if (Api5.SafeObject(() => collection.GetByIndex(i)) is ksVariable v)
            {
                var name = Api5.SafeObject(() => v.name)?.ToString() ?? "?";
                var expression = Api5.SafeObject(() => v.Expression)?.ToString();
                var external = Api5.SafeObject(() => v.external);
                names.Add(name);
                step.Observe($"[{i}] «{name}» значение={Api5.Raw(Api5.SafeDouble(() => v.value))} "
                    + $"выражение=«{expression ?? "null"}» external={Api5.Raw(external)}");
            }
        }

        return (count, names);
    }

    /// <summary>Bind the extrusion depth parameter to the user variable, through the parameter's
    /// Expression, and report every candidate the kernel exposes.</summary>
    /// <remarks>DOC <c>variables_in_tree.html</c>: «В ячейке Выражение введите … ссылку на другую
    /// переменную». DOC <c>1786_173_3_tipi_peremen.html</c>: an operation-parameter variable can carry only
    /// the informational status — it is the USER variable that is external, and the parameter only
    /// REFERENCES it by name. The parameter variables of an operation are reached through the operation's
    /// own feature: DOC <c>ksentity_getfeature.html</c> gives the <c>ksEntity</c> its <c>ksFeature</c>, and
    /// <c>ksFeature.VariableCollection</c> is the documented variable array of that feature. The two
    /// listings are printed in full, so a wrong pick is visible in the report rather than inferred.</remarks>
    private bool BindDepth(ksPart part, IPart7 part7, ksEntity extrusion, IVariable7 depthVar, ProbeStep step)
    {
        // The component's own variable list, as an independent second view.
        if (part7 as IFeature7 is { } componentFeature)
        {
            ListFeatureVariables("верхний компонент", componentFeature, step);
        }

        var feature = Api5.SafeObject(() => extrusion.GetFeature()) as ksFeature;
        step.Observe("выдавливание.GetFeature() → " + Api5.RuntimeName(feature));
        if (feature is null)
        {
            step.Fail("у выдавливания не получен ksFeature — переменные параметров не перечислить");
            return false;
        }

        if (feature.VariableCollection is not ksVariableCollection parameters)
        {
            step.Fail("ksFeature.VariableCollection выдавливания не дал коллекцию");
            return false;
        }

        var listed = new List<VariableDump>();
        var count = Api5.SafeInt(parameters.GetCount) ?? -1;
        step.Observe("переменных у признака выдавливания: " + count);
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            if (Api5.SafeObject(() => parameters.GetByIndex(i)) is not ksVariable v)
            {
                continue;
            }

            var dump = new VariableDump
            {
                Api5Variable = v,
                Name = Api5.SafeObject(() => v.name)?.ToString(),
                DisplayName = Api5.SafeObject(() => v.displayName)?.ToString(),
                ParameterNote = Api5.SafeObject(() => v.parameterNote)?.ToString(),
                Expression = Api5.SafeObject(() => v.Expression)?.ToString(),
                External = Api5.SafeObject(() => v.external) is true,
                Value = Api5.SafeDouble(() => v.value),
            };
            listed.Add(dump);
            step.Observe($"  [{i}] «{dump.Name}» / «{dump.DisplayName}» / параметр «{dump.ParameterNote}» "
                + $"значение={Api5.Num(dump.Value)} external={dump.External} "
                + $"выражение=«{dump.Expression ?? "null"}»");
        }

        step.Data["feature_variable_count"] = listed.Count;
        if (listed.Count == 0)
        {
            step.Fail("у признака выдавливания нет переменных параметров — связывать нечего");
            return false;
        }

        // The depth parameter: the parameter variable whose current value is the base depth and which is
        // not one of the two user variables just created. A single match is required — an ambiguous set is
        // named rather than resolved by taking the first.
        var matches = listed.Where(v => !v.External
            && !string.Equals(v.Name, DepthVariable, StringComparison.Ordinal)
            && !string.Equals(v.Name, TotalVariable, StringComparison.Ordinal)
            && v.Value is { } value && Math.Abs(value - BaseDepthMm) < 1e-6).ToList();
        if (matches.Count != 1)
        {
            step.Fail("параметр глубины определён неоднозначно: кандидатов " + matches.Count + " из "
                + listed.Count + " — " + string.Join(", ", listed.Select(v => "«" + v.Name + "»="
                    + Api5.Num(v.Value) + (v.External ? "(ext)" : string.Empty))));
            return false;
        }

        var candidate = matches[0];
        step.Observe("параметр глубины: «" + candidate.Name + "», значение=" + Api5.Num(candidate.Value)
            + ", ParameterNote=«" + candidate.ParameterNote + "»");

        var set = Api5.SafeBool(() =>
        {
            candidate.Api5Variable!.Expression = DepthVariable;
            return true;
        });
        var readBack = Api5.SafeObject(() => candidate.Api5Variable!.Expression);
        step.Observe("Expression = «" + DepthVariable + "» → " + Api5.Raw(set)
            + ", прочитано обратно «" + Api5.Raw(readBack) + "»");

        var rebuilt = Api5.SafeBool(() => part.RebuildModel());
        step.Observe("RebuildModel() → " + Api5.Raw(rebuilt));

        if (Api5.Raw(readBack) == DepthVariable)
        {
            step.Pass("глубина операции ссылается на переменную «" + DepthVariable + "»");
            return true;
        }

        step.Fail("параметр глубины не принял ссылку на переменную: «" + Api5.Raw(readBack) + "»");
        return false;
    }

    /// <summary>Print the variables of an API7 feature — a second, independent view of the model's
    /// variable array.</summary>
    private static void ListFeatureVariables(string label, IFeature7 feature, ProbeStep step)
    {
        var raw = Api5.SafeObject(() =>
            typeof(IFeature7).GetProperty("Variables")?.GetValue(feature, new object[] { false, false }));
        if (raw is not Array array)
        {
            step.Observe(label + ": IFeature7.Variables(false,false) → " + Api5.Raw(raw));
            return;
        }

        step.Observe(label + ": IFeature7.Variables(false,false) — элементов " + array.Length);
        foreach (var element in array)
        {
            if (element is not IVariable7 variable)
            {
                continue;
            }

            step.Observe($"  «{Api5.SafeObject(() => variable.Name)}» / параметр "
                + $"«{Api5.SafeObject(() => variable.ParameterNote)}» тип="
                + $"{Api5.SafeObject(() => variable.VariableType)} значение="
                + $"{Api5.Raw(Api5.SafeDouble(() => variable.Value))} external="
                + $"{Api5.Raw(Api5.SafeObject(() => variable.External))} выражение=«"
                + $"{Api5.SafeObject(() => variable.Expression) ?? "null"}»");
        }
    }

    /// <summary>Drive the controlling variable and measure the volume the geometry must reach.</summary>
    /// <remarks>The chain has three links — the write must take on the user variable, the parameter
    /// variable must re-evaluate its reference, and the rebuild must reach the body — so each link is read
    /// back separately. A single "volume did not move" would not say WHICH link refused. Two WRITE routes
    /// (the API7 <c>IVariable7</c> handle and the API5 <c>ksVariable</c> handle the shipping adapter uses)
    /// are crossed with the two REBUILD routes, so the pair the product needs is measured rather than
    /// guessed.</remarks>
    private void DriveAndMeasure(
        ksDocument3D doc, IKompasDocument3D document7, ksPart part, IPart7 part7, ksEntity extrusion,
        IVariable7 depthVar, bool bound, ProbeStep step)
    {
        var volumeBefore = Api5.Volume(part);
        step.Observe("объём при depth=10: " + Api5.Num(volumeBefore) + " мм³ (ожидание 80000)");

        var api5Depth = Api5.SafeObject(() => part.VariableCollection() is ksVariableCollection c
            ? c.GetByName(DepthVariable, true, false)
            : null) as ksVariable;
        step.Observe("ручка API5 ksVariable «" + DepthVariable + "»: " + Api5.RuntimeName(api5Depth));

        double? changedVolume = null;
        string? movedBy = null;

        // ── Route 1: the API7 IVariable7 handle ─────────────────────────────────────────────────────
        step.Observe("до записи: depth.Value=" + Api5.Raw(Api5.SafeDouble(() => depthVar.Value))
            + ", depth.Expression=«" + Api5.SafeObject(() => depthVar.Expression) + "»");
        Api5.SafeBool(() =>
        {
            depthVar.Value = ChangedDepthMm;
            return true;
        });
        step.Observe("API7: depth.Value = 20 → Value=" + Api5.Raw(Api5.SafeDouble(() => depthVar.Value))
            + ", Expression=«" + Api5.SafeObject(() => depthVar.Expression) + "»");
        ReadParameterVariables(extrusion, step);
        TryRebuild("API7-запись + ksPart.RebuildModel()", () => Api5.SafeBool(() => part.RebuildModel()),
            part, ref changedVolume, ref movedBy, step);
        TryRebuild("API7-запись + ksDocument3D.RebuildDocument()",
            () => Api5.SafeBool(() => doc.RebuildDocument()), part, ref changedVolume, ref movedBy, step);

        // ── Reset, then Route 2: the API5 ksVariable handle the shipping adapter uses ──────────────
        ResetDepth(api5Depth, depthVar, doc, part, step);

        if (api5Depth is not null)
        {
            Api5.SafeBool(() =>
            {
                api5Depth.value = ChangedDepthMm;
                return true;
            });
            step.Observe("API5: ksVariable.value = 20 → value="
                + Api5.Raw(Api5.SafeDouble(() => api5Depth.value))
                + ", Expression=«" + Api5.SafeObject(() => api5Depth.Expression) + "»");
            TryRebuild("API5-запись + ksPart.RebuildModel()", () => Api5.SafeBool(() => part.RebuildModel()),
                part, ref changedVolume, ref movedBy, step);
            TryRebuild("API5-запись + ksDocument3D.RebuildDocument()",
                () => Api5.SafeBool(() => doc.RebuildDocument()), part, ref changedVolume, ref movedBy, step);
        }

        // ── Also record the remaining documented rebuild routes after a working write ──────────────
        TryRebuild("+ IPart7.RebuildModel(true)", () => Api5.SafeBool(() => part7.RebuildModel(true)),
            part, ref changedVolume, ref movedBy, step);
        TryRebuild("+ IKompasDocument3D.RebuildDocument()",
            () => Api5.SafeBool(() => document7.RebuildDocument()), part, ref changedVolume, ref movedBy, step);

        changedVolume ??= Api5.Volume(part);

        // ── Back to the reference state: depth = 10, volume 80000 ──────────────────────────────────
        ResetDepth(api5Depth, depthVar, doc, part, step);
        var volumeBack = Api5.Volume(part);
        step.Observe("возврат depth=10, объём: " + Api5.Num(volumeBack) + " мм³");
        var restored = ReadParameterVariables(extrusion, step);

        step.Data["volume_base"] = volumeBefore;
        step.Data["volume_changed"] = changedVolume;
        step.Data["volume_back"] = volumeBack;
        step.Data["moved_by"] = movedBy;
        step.Data["parameter_restored"] = restored;

        var ok = volumeBefore is { } vb && Math.Abs(vb - WidthMm * HeightMm * BaseDepthMm) < 0.01
            && changedVolume is { } vc && Math.Abs(vc - WidthMm * HeightMm * ChangedDepthMm) < 0.01
            && volumeBack is { } vbk && Math.Abs(vbk - WidthMm * HeightMm * BaseDepthMm) < 0.01;
        if (ok)
        {
            step.Pass("глубина управляется переменной: 80000 → 160000 → 80000 мм³ (перестраивает «"
                + movedBy + "»)");
        }
        else
        {
            step.Fail("геометрия не следует за переменной"
                + (bound ? string.Empty : " (связь глубины не подтверждена)"));
        }
    }

    /// <summary>Run one rebuild route, read the volume after it, and remember the first route that
    /// reached the changed-depth volume.</summary>
    private static void TryRebuild(
        string label, Func<bool?> call, ksPart part, ref double? changedVolume, ref string? movedBy,
        ProbeStep step)
    {
        var result = call();
        var volume = Api5.Volume(part);
        step.Observe("перестройка «" + label + "» → " + Api5.Raw(result)
            + ", объём " + Api5.Num(volume) + " мм³");
        if (changedVolume is null && volume is { } v
            && Math.Abs(v - WidthMm * HeightMm * ChangedDepthMm) < 0.01)
        {
            changedVolume = v;
            movedBy = label;
        }
    }

    /// <summary>Put the controlling variable back to the reference depth through both handles and rebuild
    /// until the reference volume is back.</summary>
    private static void ResetDepth(
        ksVariable? api5Depth, IVariable7 depthVar, ksDocument3D doc, ksPart part, ProbeStep step)
    {
        Api5.SafeBool(() =>
        {
            depthVar.Value = BaseDepthMm;
            return true;
        });
        if (api5Depth is not null)
        {
            Api5.SafeBool(() =>
            {
                api5Depth.value = BaseDepthMm;
                return true;
            });
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Api5.SafeBool(() => doc.RebuildDocument());
            if (Api5.Volume(part) is { } v && Math.Abs(v - WidthMm * HeightMm * BaseDepthMm) < 0.01)
            {
                break;
            }
        }
    }

    /// <summary>Re-read the extrusion feature's parameter variables, as the name→value map of the
    /// parameters the operation exposes.</summary>
    private static Dictionary<string, string> ReadParameterVariables(ksEntity extrusion, ProbeStep step)
    {
        var map = new Dictionary<string, string>();
        if (Api5.SafeObject(() => extrusion.GetFeature()) is not ksFeature feature
            || feature.VariableCollection is not ksVariableCollection parameters)
        {
            return map;
        }

        var count = Api5.SafeInt(parameters.GetCount) ?? 0;
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            if (Api5.SafeObject(() => parameters.GetByIndex(i)) is not ksVariable v)
            {
                continue;
            }

            var name = Api5.SafeObject(() => v.name)?.ToString() ?? "?";
            var note = Api5.SafeObject(() => v.parameterNote)?.ToString() ?? string.Empty;
            var value = Api5.SafeDouble(() => v.value);
            var expression = Api5.SafeObject(() => v.Expression)?.ToString() ?? string.Empty;
            map[name] = "«" + note + "»=" + Api5.Num(value) + " выражение=«" + expression + "»";
            step.Observe("  параметр " + map[name]);
        }

        return map;
    }

    /// <summary>Measure whether a user variable may carry an EMPTY expression, on a throwaway variable.
    /// </summary>
    /// <remarks>DOC <c>1862_175_1_sozd_peremen.html</c> names three assignment methods — «число или
    /// константа», «выражение для вычисления значения», «ссылка на другую переменную» — and none of them
    /// is "nothing". Whether the kernel nevertheless accepts an empty string is a fact about the kernel,
    /// and it decides whether a value write can ever find a variable "without an active expression".</remarks>
    private static void ProbeEmptyExpression(IPart7 part7, ksPart part, ProbeStep step)
    {
        const string Throwaway = "probe_empty";
        var made = Api5.SafeObject(() => part7.AddVariable(Throwaway, 5d, "проба пустого выражения"));
        step.Observe("AddVariable(«" + Throwaway + "», 5) → " + Api5.RuntimeName(made));
        if (made is not IVariable7 variable)
        {
            step.Fail("пробная переменная не создана");
            return;
        }

        var valueBefore = Api5.SafeDouble(() => variable.Value);
        var exprBefore = Api5.SafeObject(() => variable.Expression);
        step.Observe("до очистки: Value=" + Api5.Raw(valueBefore) + ", Expression=«" + Api5.Raw(exprBefore) + "»");

        var clear = Api5.SafeBool(() =>
        {
            variable.Expression = string.Empty;
            return true;
        });
        var exprAfter = Api5.SafeObject(() => variable.Expression);
        var valueAfter = Api5.SafeDouble(() => variable.Value);
        var stillNamed = Api5.SafeObject(() => part.VariableCollection() is ksVariableCollection c
            ? c.GetByName(Throwaway, true, false)
            : null);
        var count = part.VariableCollection() is ksVariableCollection all ? Api5.SafeInt(all.GetCount) : null;
        step.Observe("Expression = «» → " + Api5.Raw(clear) + "; прочитано Expression=«" + Api5.Raw(exprAfter)
            + "», Value=" + Api5.Raw(valueAfter) + "; переменная под тем же именем: "
            + (stillNamed is null ? "НЕ найдена" : "найдена") + "; переменных у верхнего компонента: "
            + Api5.Raw(count));

        var deleted = Api5.SafeBool(() => variable.Delete());
        step.Observe("Delete() пробной переменной → " + Api5.Raw(deleted));

        step.Data["value_before"] = valueBefore;
        step.Data["expression_before"] = exprBefore;
        step.Data["expression_after"] = exprAfter;
        step.Data["value_after"] = valueAfter;
        step.Data["still_named_after_clear"] = stillNamed is not null;
        step.Data["count_after_clear"] = count;
        step.Pass("состояние пустого выражения измерено: Expression=«" + Api5.Raw(exprAfter)
            + "», Value=" + Api5.Raw(valueAfter));
    }

    private void Save(ksDocument3D doc, ProbeStep step, string path, ksPart part, IPart7 part7)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var saved = doc.SaveAs(path);
            step.Observe("SaveAs(«" + path + "») → " + Api5.Raw(saved));
            if (saved != true || !File.Exists(path))
            {
                step.Fail("файл эталона не записан");
                return;
            }

            var info = new FileInfo(path);
            step.Observe("файл: " + info.Length + " байт, изменён "
                + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            step.Observe("sha256: " + Sha256(path));

            // The final state is re-read from the SAME document before the file is handed to the group:
            // the saved state must already carry the two external variables, not only the session's view.
            var collection = ReadCollection(part, step);
            if (part7 as IFeature7 is { } componentFeature)
            {
                ListFeatureVariables("сохранённый верхний компонент", componentFeature, step);
            }

            step.Data["path"] = path;
            step.Data["sha256"] = Sha256(path);
            step.Data["bytes"] = info.Length;
            step.Data["top_part_count"] = collection.Count;
            step.Data["top_part_names"] = collection.Names;
            if (collection.Count >= 2)
            {
                step.Pass("эталон сохранён: " + path);
            }
            else
            {
                step.Fail("в сохранённом состоянии коллекция верхнего компонента несёт "
                    + collection.Count + " переменных");
            }
        }
        catch (Exception ex)
        {
            step.Fail("сохранение бросило " + ex.GetType().Name + ": " + ex.Message);
            step.Errors.Add(ex.ToString());
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private ksDocument3D? NewPart(ProbeStep step)
    {
        try
        {
            if (_app.Document3D() is not ksDocument3D doc)
            {
                return null;
            }

            if (!doc.Create(false, false))
            {
                step.Observe("Document3D().Create(false,false) → false");
                return null;
            }

            return doc;
        }
        catch (Exception ex)
        {
            step.Observe("создание документа бросило " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private void Shutdown()
    {
        var step = Begin("VR.Z", "Завершение сеанса",
            "Свой процесс уходит после Quit()?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && OwnProcessAlive())
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        if (OwnProcessAlive())
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit() и не ушёл за " + (limitMs / 1000) + " с");
        }
        else
        {
            step.Pass("свой процесс завершён" + (waited == 0 ? string.Empty : " через " + waited + " мс")
                + ", чужие не тронуты");
        }
    }

    private bool OwnProcessAlive()
    {
        if (_ownPid == 0)
        {
            return false;
        }

        foreach (var p in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = p.Id;
            p.Dispose();
            if (pid == _ownPid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One variable of the model as the kernel exposes it, for the identification of the
    /// depth parameter.</summary>
    private sealed class VariableDump
    {
        public ksVariable? Api5Variable { get; init; }

        public string? Name { get; init; }

        public string? DisplayName { get; init; }

        public string? ParameterNote { get; init; }

        public string? Expression { get; init; }

        public bool External { get; init; }

        public double? Value { get; init; }
    }
}
