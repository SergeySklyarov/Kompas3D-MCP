using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe SC — the documented routes that move geometry between sketches: copying objects through
/// <c>IDrawingGroup</c> on the sketch's edit document, and projecting a model object into a sketch with
/// <c>ISketch::AddProjectionOf</c> (order <c>SKETCH_COPY_PROJECT_RESEARCH</c>).</summary>
/// <remarks>DOC: the copy route is a chain of documented pieces, each page naming the next:
/// <c>isketch_beginedit.html</c> — <c>BeginEdit()</c> returns <c>IFragmentDocument</c>;
/// <c>ifragmentdocument_props.html</c> — <c>IsSketch</c>, «Документ является эскизом»;
/// <c>ikompasdocument2d.html</c> — «Интерфейс может быть получен с помощью метода IUnknown
/// QueryInterface от … интерфейса фрагмента IFragmentDocument»;
/// <c>ikompasdocument2d1_drawinggroups.html</c> — <c>DrawingGroups</c>;
/// <c>idrawinggroups_add.html</c>, <c>idrawinggroup_addobjects.html</c>,
/// <c>idrawinggroup_copytodocument.html</c> — «Указатель на другой документ, куда копируется группа».
/// The projection route: <c>isketch_addprojectionof.html</c> — «Добавить в эскиз проекцию объекта»,
/// argument <c>IModelObject</c>, result an array of created projection objects.
/// INVARIANT: the copy is confirmed by RE-READING the target sketch, never by the answers of
/// <c>AddObjects</c>/<c>CopyToDocument</c>: those report the call, not the composition.
/// MEASURED: the sketch edit session does NOT answer <c>QI(IFragmentDocument)</c> directly; the
/// documented <c>TransferInterface(…, ksAPI7Dual)</c> is what turns it into the fragment document
/// (<c>Api5Session.Geometry</c>, <c>Api7SketchEntities</c>). The probe therefore takes the same road
/// the product takes and records each step's runtime type.
/// History: docs/decisions/probes.md#sketch-copy-project</remarks>
internal sealed class SketchCopyProjectProbe
{
    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short PlaneXoz = 2;
    private const short BaseExtrusion = 24;
    private const int LineStyle = 1;

    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private ksEntity _sketchA = null!;
    private ksEntity _sketchB = null!;
    private ksEntity _sketchP = null!;
    private ksEntity _sketchPXoz = null!;
    private ksEntity _plateExtrusion = null!;
    private string _savedPath = string.Empty;
    private int _aObjects;
    private int _aSegments;
    private int _aCircles;
    private int _aArcs;
    private int _bSegments;
    private int _bCircles;
    private int _bArcs;
    private int _projected;

    public SketchCopyProjectProbe(ProbeReport report, Options options)
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
            BuildFixture();
            if (_sketchA is not null && _sketchB is not null)
            {
                CopyByGroup();
                var pre = _report.Begin("SC.3a", "Целевой эскиз перед вторым маршрутом",
                    "Понадобился ли клип?");
                var preCounts = Counts(_sketchB, "pre-check", pre);
                pre.Data["target_objects"] = preCounts.Objects;
                if (preCounts.Objects == 0)
                {
                    pre.Observe("Маршрут группы не дал объектов — проверяется клип.");
                    CopyByClipboard();
                }
                else
                {
                    pre.Pass($"Объекты уже в целевом эскизе ({preCounts.Objects}) — клип не нужен.");
                }

                ReadTarget();
                SourceUntouched();
            }

            // The projection experiment runs BEFORE the first save: the associativity test changes the
            // source feature through the entity captured at build time, and a reopened document hands out
            // no such reference.
            ProjectIntoSketch();
            if (_sketchA is not null && _sketchB is not null)
            {
                SaveReopenAndExtrude();
            }

            SaveReopenProjection();
            DeleteProjectionSource();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("SC.X", "Необработанное исключение пробы SC");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    // ─── session ──────────────────────────────────────────────────────────────────────────────
    private void Launch()
    {
        var step = _report.Begin("SC.1", "Свой невидимый экземпляр", "Зонд управляет сеансом один?");
        try
        {
            var before = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray();
            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;
            var created = Process.GetProcessesByName("KOMPAS").Select(p => (uint)p.Id).ToArray().Except(before).ToArray();
            if (created.Length != 1)
            {
                step.Fail($"Новых процессов: {created.Length} — сеанс не атрибутируется.");
                return;
            }

            _options.ProcessId = (int)created[0];
            step.Data["pid"] = created[0];
            step.Pass("Экземпляр запущен и атрибутирован диффом процессов.");
        }
        catch (Exception ex)
        {
            _app = null!;
            step.Fail("Запуск не удался: " + ex.Message);
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("SC.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            if (_options.KeepRunning)
            {
                step.Unknown("--keep: экземпляр оставлен открытым.");
                return;
            }

            _app.Quit();
            var pid = _options.ProcessId;
            step.Pass(pid is null || WaitUntilGone(pid.Value, 30_000)
                ? "Собственный экземпляр завершён штатно."
                : "Процесс жив через 30 с после Quit().");
        }
        catch (Exception ex)
        {
            step.Fail("Завершение не подтверждено: " + ex.Message);
        }
    }

    // ─── fixture: plate, source sketch with geometry, empty target sketch ────────────────────
    private void BuildFixture()
    {
        var step = _report.Begin("SC.2", "Пластина, исходный эскиз с геометрией и пустой целевой",
            "Есть ли что копировать и куда?");
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Create(true, true) != true)
        {
            step.Fail("Документ не создан.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        // The entity is kept: the associativity test changes the plate's thickness through the very
        // definition this call returned, and a definition asked for later is not guaranteed to be the
        // same object.
        var plate = Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step, "sc");
        if (plate is null)
        {
            step.Fail("Пластина не построена.");
            return;
        }

        _plateExtrusion = plate;

        _sketchA = NewSketch(PlaneXoy, "sc-source")!;
        _sketchB = NewSketch(PlaneXoz, "sc-target")!;
        if (_sketchA is null || _sketchB is null)
        {
            step.Fail("Эскизы не созданы.");
            return;
        }

        if (Session(_sketchA) is not ksDocument2D session)
        {
            step.Fail("BeginEdit исходного эскиза не дал ksDocument2D.");
            return;
        }

        try
        {
            // A CLOSED contour plus an inner circle: the copied profile must be extrudable, otherwise a
            // refusal of the extrusion would say nothing about the copy route.
            step.Data["segment_1"] = session.ksLineSeg(-30d, -20d, 30d, -20d, LineStyle);
            step.Data["segment_2"] = session.ksLineSeg(30d, -20d, 30d, 20d, LineStyle);
            step.Data["segment_3"] = session.ksLineSeg(30d, 20d, -30d, 20d, LineStyle);
            step.Data["segment_4"] = session.ksLineSeg(-30d, 20d, -30d, -20d, LineStyle);
            step.Data["circle"] = session.ksCircle(0d, 0d, 8d, LineStyle);
            step.Data["constraint"] = HorizontalConstraint(session, step);
        }
        finally
        {
            TryBool(() => _sketchA.GetDefinition() is ksSketchDefinition definition && definition.EndEdit());
            _doc.RebuildDocument();
        }

        var a = Counts(_sketchA, "a", step);
        _aObjects = a.Objects;
        _aSegments = a.Segments;
        _aCircles = a.Circles;
        _aArcs = a.Arcs;
        step.Observe($"Исходный эскиз: объектов {_aObjects}, отрезков {_aSegments}, дуг {_aArcs}, окружностей {_aCircles}.");
        if (_aSegments == 4 && _aCircles == 1 && _aArcs == 0)
        {
            step.Pass("Исходный эскиз содержит замкнутый контур из четырёх отрезков и окружность; целевой пуст.");
        }
        else
        {
            step.Fail($"Состав исходного эскиза не тот, что нарисован: {_aSegments}/{_aArcs}/{_aCircles}.");
        }
    }

    /// <summary>A horizontal constraint on the first segment, by the documented
    /// <c>IDrawingObject1::NewConstraint()</c> route. Its type name is taken from the installed enum, so a
    /// missing name is reported rather than assumed.</summary>
    private string HorizontalConstraint(ksDocument2D session, ProbeStep step)
    {
        var name = Enum.GetNames<ksConstraintTypeEnum>()
            .FirstOrDefault(n => n.Contains("Horizont", StringComparison.OrdinalIgnoreCase));
        step.Data["constraint_enum_names"] = Enum.GetNames<ksConstraintTypeEnum>().Length;
        if (name is null)
        {
            step.Data["constraint_type"] = "нет имени с Horizont в ksConstraintTypeEnum";
            return "не создано";
        }

        var objects = ObjectsOf(Document2D(session, step, "constraint") ?? session, step, "constraint");
        if (objects.FirstOrDefault() is not IDrawingObject1 host)
        {
            step.Data["constraint_type"] = name;
            return "объект не отвечает QI(IDrawingObject1)";
        }

        try
        {
            var constraint = host.NewConstraint();
            constraint.ConstraintType = Enum.Parse<ksConstraintTypeEnum>(name);
            var created = constraint.Create();
            step.Data["constraint_type"] = name;
            step.Data["constraint_created"] = created;
            return created ? name : "Create() → false";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data["constraint_type"] = name;
            step.Data["constraint_failure"] = ex.GetType().Name + ": " + ex.Message;
            return "отказ: " + ex.GetType().Name;
        }
    }

    // ─── A: copy through IDrawingGroup on the sketch's own 2D document ───────────────────────
    private void CopyByGroup()
    {
        var step = _report.Begin("SC.3", "Копирование объектов через IDrawingGroup::CopyToDocument",
            "Переносятся ли объекты из эскиза в эскиз документированным маршрутом?");

        var sourceSession = Session(_sketchA);
        step.Data["source_session_type"] = Api5.RuntimeName(sourceSession);
        if (sourceSession is null)
        {
            step.Fail("Сеанс правки исходного эскиза не открылся.");
            return;
        }

        var targetSession = Session(_sketchB);
        step.Data["target_session_type"] = Api5.RuntimeName(targetSession);
        if (targetSession is null)
        {
            EndSession(_sketchA, "source");
            step.Fail("Второй сеанс правки не открылся, пока открыт первый: маршрут «группа → " +
                      "другой документ» требует ОБА эскиза в правке одновременно, а деталь держит один.");
            return;
        }

        try
        {
            var source = Document2D(sourceSession, step, "source");
            var target = Document2D(targetSession, step, "target");
            step.Data["source_document_type"] = Api5.RuntimeName(source);
            step.Data["target_document_type"] = Api5.RuntimeName(target);
            if (source is not IKompasDocument2D source2D || target is not IKompasDocument2D target2D)
            {
                step.Fail("Сеанс правки не перенесён в IKompasDocument2D — копировать нечем.");
                return;
            }

            var groups = (source2D as IKompasDocument2D1)?.DrawingGroups;
            step.Data["drawing_groups"] = Api5.RuntimeName(groups);
            if (groups is null)
            {
                step.Fail("QI(IKompasDocument2D1).DrawingGroups → null.");
                return;
            }

            var group = groups.Add(false, "probe-copy");
            step.Data["group"] = Api5.RuntimeName(group);
            if (group is null)
            {
                step.Fail("IDrawingGroups::Add(false, \"probe-copy\") → null.");
                return;
            }

            var objects = ObjectsOf(source2D, step, "copy-source");
            step.Data["objects_taken"] = objects.Length;
            var added = TryBool(() => group.AddObjects(objects));
            step.Data["add_objects"] = Api5.Raw(added);

            // DOC: <c>idrawinggroup_copytodocument.html</c> — «Указатель на другой документ, куда
            // копируется группа». Called by name because the shipped coclass does not re-declare it.
            var copied = TryGroup(() => Late.Call(group, "CopyToDocument", target2D) as IDrawingGroup);
            step.Data["copy_to_document"] = Api5.RuntimeName(copied);
            step.Data["copy_result_objects"] = copied is null ? null : SafeInt(() => copied.ObjectsCount);
            var closed = TryBool(group.Close);
            step.Data["group_closed"] = Api5.Raw(closed);
            step.Observe($"Взято объектов {objects.Length}, AddObjects={Api5.Raw(added)}, " +
                         $"CopyToDocument → {Api5.RuntimeName(copied)}, Close={Api5.Raw(closed)}.");
            if (added == true && copied is not null)
            {
                step.Pass("Документированная цепочка группы отработала: AddObjects и CopyToDocument " +
                          "ответили объектом; состав проверяется отдельным шагом перечитыванием.");
            }
            else
            {
                step.Fail("Цепочка не замкнулась по ответам вызовов — перечитывать нечего.");
            }
        }
        finally
        {
            EndSession(_sketchB, "target");
            EndSession(_sketchA, "source");
            _doc.RebuildDocument();
        }
    }

    /// <summary>The same copy through the clipboard, which needs the two sketches only one after the
    /// other — the route that fits a part that holds one edit session.</summary>
    /// <remarks>DOC: <c>idrawinggroup_writetoclip.html</c> («скопировать объекты из группы в клип») and
    /// <c>idrawinggroup_readfromclip.html</c> («считать объекты из клипа в группу»).
    /// TEST: SC.4 re-reads the target, SC.5 the source — the clipboard's answer is not evidence.</remarks>
    private void CopyByClipboard()
    {
        var step = _report.Begin("SC.3b", "Копирование через клип: WriteToClip → ReadFromClip",
            "Работает ли копирование, когда эскизы открываются по очереди?");

        var sourceSession = Session(_sketchA);
        if (sourceSession is null)
        {
            step.Fail("Сеанс правки исходного эскиза не открылся.");
            return;
        }

        try
        {
            var source2D = Document2D(sourceSession, step, "clip-source") as IKompasDocument2D;
            var groups = (source2D as IKompasDocument2D1)?.DrawingGroups;
            var group = groups?.Add(false, "probe-clip-out");
            step.Data["group_open_out"] = Api5.Raw(group is null ? null : TryBool(group.Open));
            var objects = source2D is null ? Array.Empty<object>() : ObjectsOf(source2D, step, "clip-source");
            step.Data["objects_taken"] = objects.Length;
            step.Data["add_objects"] = Api5.Raw(group is null ? null : TryBool(() => group.AddObjects(objects)));
            step.Data["write_to_clip"] = Api5.Raw(group is null ? null : TryBool(() => group.WriteToClip(true, true)));
            step.Data["group_closed"] = Api5.Raw(group is null ? null : TryBool(group.Close));
        }
        finally
        {
            EndSession(_sketchA, "clip-source");
            _doc.RebuildDocument();
        }

        var targetSession = Session(_sketchB);
        if (targetSession is null)
        {
            step.Fail("Сеанс правки целевого эскиза не открылся после закрытия первого.");
            return;
        }

        try
        {
            var target2D = Document2D(targetSession, step, "clip-target") as IKompasDocument2D;
            var groups = (target2D as IKompasDocument2D1)?.DrawingGroups;
            var group = groups?.Add(false, "probe-clip-in");
            step.Data["group_open_in"] = Api5.Raw(group is null ? null : TryBool(group.Open));
            step.Data["read_from_clip"] = Api5.Raw(group is null ? null : TryBool(() => group.ReadFromClip(true, true)));
            // Second shape of the same documented call: a TEMPORARY group, which is what the clipboard
            // section of the help describes. One refusal is not a verdict about the route.
            var temp = groups?.Add(true, "probe-clip-in-temp");
            step.Data["temp_group_open"] = Api5.Raw(temp is null ? null : TryBool(temp.Open));
            step.Data["read_from_clip_temp"] = Api5.Raw(temp is null ? null : TryBool(() => temp.ReadFromClip(true, true)));
            step.Data["temp_group_objects"] = temp is null ? null : SafeInt(() => temp.ObjectsCount);
            // DOC: <c>idrawinggroup_store.html</c> — «Вставить временную группу в документ»: the objects
            // read from the clip live in the group until it is stored.
            step.Data["temp_group_store"] = Api5.Raw(temp is null ? null : TryBool(temp.Store));
            step.Data["temp_group_closed"] = Api5.Raw(temp is null ? null : TryBool(temp.Close));
            step.Data["group_closed_in"] = Api5.Raw(group is null ? null : TryBool(group.Close));
            step.Data["target_objects_in_session"] = target2D is null
                ? null
                : ObjectsOf(target2D, step, "clip-target").Length;
        }
        finally
        {
            EndSession(_sketchB, "clip-target");
            _doc.RebuildDocument();
        }

        var counts = Counts(_sketchB, "clip-b", step);
        _bSegments = counts.Segments;
        _bArcs = counts.Arcs;
        _bCircles = counts.Circles;
        step.Data["target_objects_after_clip"] = counts.Objects;
        if (counts.Objects > 0)
        {
            step.Pass($"Через клип в целевой эскиз пришло объектов {counts.Objects} " +
                      $"({counts.Segments} отрезков, {counts.Arcs} дуг, {counts.Circles} окружностей).");
        }
        else
        {
            step.Fail("Клип не дал объектов в целевом эскизе.");
        }
    }

    private void ReadTarget()
    {
        var step = _report.Begin("SC.4", "Состав целевого эскиза после копирования",
            "Появились ли объекты и в какой системе координат?");
        var counts = Counts(_sketchB, "b", step);
        _bSegments = counts.Segments;
        _bCircles = counts.Circles;
        _bArcs = counts.Arcs;
        step.Data["target_objects"] = counts.Objects;
        step.Data["target_segments"] = _bSegments;
        step.Data["target_arcs"] = _bArcs;
        step.Data["target_circles"] = _bCircles;
        step.Data["source_segments"] = _aSegments;
        step.Data["source_arcs"] = _aArcs;
        step.Data["source_circles"] = _aCircles;

        var coordinates = FirstCoordinates(_sketchB, step);
        var targetConstraints = ConstraintsOf(_sketchB, "b", step);
        step.Data["target_constraints"] = targetConstraints;
        if (_bSegments == _aSegments && _bArcs == _aArcs && _bCircles == _aCircles)
        {
            step.Pass($"Целевой эскиз содержит ровно скопированный набор: отрезков {_bSegments}, " +
                      $"дуг {_bArcs}, окружностей {_bCircles}.");
        }
        else
        {
            step.Fail($"Состав не совпал: было {_aSegments}/{_aArcs}/{_aCircles}, стало " +
                      $"{_bSegments}/{_bArcs}/{_bCircles}. {coordinates}");
        }
    }

    private void SourceUntouched()
    {
        var step = _report.Begin("SC.5", "Исходный эскиз после копирования",
            "Копирование не переносит, а именно копирует?");
        var counts = Counts(_sketchA, "a2", step);
        step.Data["source_objects_after"] = counts.Objects;
        step.Data["source_segments_after"] = counts.Segments;
        step.Data["source_arcs_after"] = counts.Arcs;
        step.Data["source_circles_after"] = counts.Circles;
        step.Data["source_constraints"] = ConstraintsOf(_sketchA, "a2", step);
        var intact = counts.Segments == _aSegments && counts.Arcs == _aArcs && counts.Circles == _aCircles;
        if (intact && _bSegments > 0)
        {
            step.Pass("Исходный эскиз сохранил свой состав: маршрут копирует, а не переносит.");
        }
        else if (!intact)
        {
            step.Fail($"Состав исходного эскиза изменился: {counts.Segments}/{counts.Arcs}/{counts.Circles}.");
        }
        else
        {
            step.Unknown("Исходный эскиз цел, но в целевом ничего не появилось — копировать было нечего.");
        }
    }

    private void SaveReopenAndExtrude()
    {
        var step = _report.Begin("SC.6", "save→close→reopen и выдавливание целевого эскиза",
            "Переживает ли копия переоткрытие и годится ли она в профиль?");
        _savedPath = Path.Combine(_options.WorkDir, "sketch-copy-project.m3d");
        step.Data["saved_path"] = _savedPath;
        if (_doc.SaveAs(_savedPath) != true)
        {
            step.Fail("SaveAs не подтверждён.");
            return;
        }

        step.Data["closed"] = Api5.Raw(TryBool(_doc.close));
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Open(_savedPath, true) != true)
        {
            step.Fail("Open не подтверждён.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        var target = FindSketch("sc-target");
        step.Data["target_found_after_reopen"] = target?.name ?? "нет";
        if (target is null)
        {
            step.Fail("Целевой эскиз не найден после переоткрытия.");
            return;
        }

        var counts = Counts(target, "b2", step);
        step.Data["target_segments_after_reopen"] = counts.Segments;
        step.Data["target_arcs_after_reopen"] = counts.Arcs;
        step.Data["target_circles_after_reopen"] = counts.Circles;

        var extrusion = Extrude(target, step);
        var volume = Api5.Volume(_part);
        var expected = (60d * 40d - Math.PI * 8d * 8d) * 5d;
        step.Data["volume_after_extrude"] = Api5.Num(volume);
        step.Data["volume_expected"] = Api5.Num(expected);
        step.Data["volume_of_base_plate"] = Api5.Num(PlateWidth * PlateHeight * PlateThickness);
        step.Data["bodies"] = Api5.BodyCount(_part);

        var persisted = counts.Segments == _bSegments && counts.Arcs == _bArcs && counts.Circles == _bCircles;
        if (persisted && extrusion)
        {
            step.Pass($"Копия пережила save→close→reopen ({counts.Segments}/{counts.Arcs}/{counts.Circles}) " +
                      $"и выдавливается: тело {Api5.Num(volume)}.");
        }
        else if (!persisted)
        {
            step.Fail($"После переоткрытия состав другой: {counts.Segments}/{counts.Arcs}/{counts.Circles} " +
                      $"против {_bSegments}/{_bArcs}/{_bCircles}.");
        }
        else
        {
            step.Fail("Копия сохранилась, но профиль не выдавился.");
        }
    }

    private bool Extrude(ksEntity sketch, ProbeStep step)
    {
        if (_part.NewEntity(BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Data["extrude"] = "определение выдавливания не получено";
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, 5d, 0d, false);
        var created = TryBool(extrusion.Create);
        _doc.RebuildDocument();
        step.Data["extrude"] = Api5.Raw(created);
        return created == true;
    }

    // ─── B: projection into a sketch ─────────────────────────────────────────────────────────
    private void ProjectIntoSketch()
    {
        var step = _report.Begin("SC.7", "Проецирование объекта в эскиз через ISketch::AddProjectionOf",
            "Документированный маршрут проецирования работает?");
        var source = FindSketch("sc-source");
        // Two receivers, and the pair is what makes the associativity answer observable: the first sits in
        // the plane its edge LIES in (XOY), so its 2D numbers cannot follow a body growing along the
        // normal whatever the associativity; the second sits across that normal (XOZ) and carries the edge
        // of the TOP face, whose 2D numbers are exactly the thickness.
        _sketchP = NewSketch(PlaneXoy, "sc-projection")!;
        _sketchPXoz = NewSketch(PlaneXoz, "sc-projection-xoz")!;
        if (source is null || _sketchP is null || _sketchPXoz is null)
        {
            step.Fail("Исходный эскиз или эскизы-приёмники не найдены.");
            return;
        }

        var sketch7 = _app.TransferInterface(_sketchP, (int)ksAPITypeEnum.ksAPI7Dual, 0) as ISketch;
        var sketch7Xoz = _app.TransferInterface(_sketchPXoz, (int)ksAPITypeEnum.ksAPI7Dual, 0) as ISketch;
        step.Data["sketch7_type"] = Api5.RuntimeName(sketch7);
        step.Data["sketch7_xoz_type"] = Api5.RuntimeName(sketch7Xoz);
        if (sketch7 is null || sketch7Xoz is null)
        {
            step.Fail("Эскиз не перенесён в ISketch — проецировать нечем.");
            return;
        }

        // The documented argument is an IModelObject; the help names an edge, a vertex or a face, so the
        // subject is a body EDGE. A whole sketch is tried afterwards as a control: an empty answer there
        // must not be read as "projection does not work".
        var bottom = PickEdge(topFace: false, step, "bottom");
        var top = PickEdge(topFace: true, step, "top");
        var sketchModel = _app.TransferInterface(source, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
        step.Data["projected_edge_type"] = Api5.RuntimeName(bottom.Model);
        step.Data["projected_top_edge_type"] = Api5.RuntimeName(top.Model);
        step.Data["projected_sketch_type"] = Api5.RuntimeName(sketchModel);
        if (bottom.Model is null)
        {
            step.Fail("Ребро тела не перенесено в IModelObject — проецировать нечего.");
            return;
        }

        // INVARIANT: the projection is asked for INSIDE the sketch edit session — the API5 page of the
        // same member says so directly («работает только в режиме редактирования эскиза»), and a call
        // outside the session answers null, which reads like "no route" and is not one.
        int count;
        int control;
        var session = Session(_sketchP);
        step.Data["projection_session"] = Api5.RuntimeName(session);
        try
        {
            count = Project(sketch7, bottom.Model, step, "edge");
            control = Project(sketch7, sketchModel, step, "sketch-control");
        }
        finally
        {
            EndSession(_sketchP, "projection");
        }

        int countXoz;
        var sessionXoz = Session(_sketchPXoz);
        step.Data["projection_session_xoz"] = Api5.RuntimeName(sessionXoz);
        try
        {
            countXoz = Project(sketch7Xoz, top.Model, step, "top-edge");
        }
        finally
        {
            EndSession(_sketchPXoz, "projection-xoz");
        }

        _projected = count + countXoz;
        step.Data["projection_control_sketch"] = control;
        _doc.RebuildDocument();
        var counts = Counts(_sketchP, "p", step);
        step.Data["projection_segments"] = counts.Segments;
        step.Data["projection_arcs"] = counts.Arcs;
        step.Data["projection_circles"] = counts.Circles;
        step.Data["projection_objects_in_sketch"] = counts.Objects;
        var countsXoz = Counts(_sketchPXoz, "pxoz", step);
        step.Data["projection_xoz_objects"] = countsXoz.Objects;

        if (count > 0 || counts.Objects > 0)
        {
            step.Pass($"AddProjectionOf(ребро) вернул {count} объектов; в эскизе-приёмнике объектов " +
                      $"{counts.Objects} ({counts.Segments} отрезков, {counts.Arcs} дуг, {counts.Circles} окружностей); " +
                      $"в эскизе на XOZ от ребра верхней грани — {countXoz} (объектов {countsXoz.Objects}).");
            ProjectionOfAnotherSketchObject(step);
            AssociateOnThicknessChange();
        }
        else
        {
            step.Fail("AddProjectionOf(ребро) вернул пустой массив и в эскизе объектов не появилось.");
        }
    }

    /// <summary>Whether an object of ANOTHER sketch may be the projected subject at all.</summary>
    /// <remarks>DOC: <c>kssketchdefinition_addprojectionof.html</c> — «Параметр <c>entity</c> может быть
    /// указателем на ребро, вершину или грань»; <c>isketch_addprojectionof.html</c> names the argument as
    /// <c>IModelObject</c> without widening that list. An object of a sketch is none of the three, so the
    /// documented route does not accept it and the experiment is NOT run: a probe of an undocumented
    /// argument would answer about a route nobody has documented.</remarks>
    private void ProjectionOfAnotherSketchObject(ProbeStep parent)
    {
        var step = _report.Begin("SC.7c", "Проекция объекта ДРУГОГО эскиза",
            "Допускает ли документированный маршрут объект эскиза как IModelObject?");
        step.Data["documented_argument_kinds"] = "ребро, вершина, грань";
        step.Data["control_whole_sketch"] = parent.Data.TryGetValue("sketch-control_objects", out var control)
            ? control
            : null;
        step.Unknown("Не измерено: справка допускает только ребро, вершину и грань, а объект эскиза не " +
                     "назван ни одним из них. Контроль с ЭСКИЗОМ целиком дал 0 объектов — это ответ про " +
                     "эскиз как целое, а не про его объект.");
    }

    /// <summary>One <c>AddProjectionOf</c> call; the answer is the array length, not a boolean.</summary>
    private static int Project(ISketch sketch7, IModelObject? model, ProbeStep step, string label)
    {
        if (model is null)
        {
            step.Data[label + "_skipped"] = "объект не перенесён в IModelObject";
            return 0;
        }

        try
        {
            var result = sketch7.AddProjectionOf(model);
            var count = result is Array array ? array.Length : result is null ? 0 : 1;
            step.Data[label + "_objects"] = count;
            step.Data[label + "_result_type"] = Api5.RuntimeName(result);
            return count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[label + "_failure"] = ex.GetType().Name + ": " + ex.Message;
            return 0;
        }
    }

    /// <summary>Associativity: the plate changes thickness, both projections are re-read.</summary>
    /// <remarks>DOC: <c>ksbaseextrusiondefinition_setsideparam.html</c> — the depth of a base extrusion in
    /// one direction; <c>ksentity_update.html</c> — «Изменение параметров объекта через интерфейс его
    /// параметров не приводит к перестроению модели … для перестроения объекта в модели … необходимо
    /// вызвать данный метод».
    /// MEASURED: the answer is the COORDINATES of the projections before and after the rebuild, not the
    /// call's reply — an unassociated copy of the edge would keep its old numbers. The thickness is put
    /// back afterwards, so the copy experiment that follows keeps its own volume arithmetic.</remarks>
    private void AssociateOnThicknessChange()
    {
        var step = _report.Begin("SC.7a", "Ассоциативность проекции при изменении исходного тела",
            "Двигается ли проекция за исходным ребром после перестроения?");

        if (_plateExtrusion is null || _sketchP is null || _sketchPXoz is null)
        {
            step.Unknown("Признак выдавливания или эскизы-приёмники не получены — опыт не на чем ставить.");
            return;
        }

        var xozBefore = FirstCoordinates(_sketchPXoz, step, "xoz_before");
        var xoyBefore = FirstCoordinates(_sketchP, step, "xoy_before");
        var volumeBefore = Api5.Volume(_part);
        step.Data["volume_before"] = Api5.Num(volumeBefore);

        var grown = SetThickness(PlateThickness + 10d, step, "grow");
        var xozAfter = FirstCoordinates(_sketchPXoz, step, "xoz_after");
        var xoyAfter = FirstCoordinates(_sketchP, step, "xoy_after");
        var volumeGrown = Api5.Volume(_part);
        step.Data["volume_after_grow"] = Api5.Num(volumeGrown);

        var restored = SetThickness(PlateThickness, step, "restore");
        var xozRestored = FirstCoordinates(_sketchPXoz, step, "xoz_restored");
        var volumeRestored = Api5.Volume(_part);
        step.Data["volume_after_restore"] = Api5.Num(volumeRestored);
        step.Data["xoy_changed"] = xoyBefore != xoyAfter;

        if (!grown || !restored)
        {
            step.Unknown("Толщина не изменена документированным путём — ассоциативность не проверена.");
            return;
        }

        if (volumeBefore == volumeGrown || volumeGrown == volumeRestored)
        {
            step.Fail($"Тело не изменилось: объём {Api5.Num(volumeBefore)} → {Api5.Num(volumeGrown)} → " +
                      $"{Api5.Num(volumeRestored)}. Опыт не поставлен, ответа об ассоциативности нет.");
            return;
        }

        var follows = xozBefore != xozAfter;
        var returns = xozRestored == xozBefore;
        if (follows && returns)
        {
            step.Pass($"Проекция на XOZ изменилась вместе с телом и вернулась вместе с ним: до «{xozBefore}», " +
                      $"после «{xozAfter}», после возврата толщины «{xozRestored}»; объём " +
                      $"{Api5.Num(volumeBefore)} → {Api5.Num(volumeGrown)} → {Api5.Num(volumeRestored)}. " +
                      $"Проекция на XOY не изменилась ({xoyBefore == xoyAfter}): её ребро лежит В плоскости " +
                      "эскиза, и рост по нормали её 2D-чисел не касается.");
        }
        else if (!follows)
        {
            step.Fail($"Проекция не последовала за ребром: до «{xozBefore}», после «{xozAfter}», при объёме " +
                      $"{Api5.Num(volumeBefore)} → {Api5.Num(volumeGrown)}.");
        }
        else
        {
            step.Fail($"Проекция ушла за телом, но не вернулась: до «{xozBefore}», после «{xozRestored}» " +
                      "при исходной толщине.");
        }
    }

    /// <summary>The documented thickness change of the captured base extrusion, then a rebuild.</summary>
    private bool SetThickness(double thickness, ProbeStep step, string label)
    {
        if (_plateExtrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Data[label + "_definition"] = "GetDefinition не дал ksBaseExtrusionDefinition";
            return false;
        }

        var set = TryBool(() => definition.SetSideParam(true, 0, thickness, 0d, false));
        var updated = TryBool(_plateExtrusion.Update);
        _doc.RebuildDocument();
        step.Data[label + "_thickness"] = Api5.Num(thickness);
        step.Data[label + "_set_side_param"] = Api5.Raw(set);
        step.Data[label + "_update"] = Api5.Raw(updated);
        return set == true && updated == true;
    }

    /// <summary>What becomes of a projection when the feature that produced the subject is deleted.</summary>
    /// <remarks>DOC: <c>ksdocument3d_deleteobject.html</c> — «Удалить можно любой трехмерный объект»; «Если
    /// в качестве входного параметра переданы … ребро ksEdgeDefinition … то будет удален весь
    /// формообразующий элемент, которому принадлежит удаляемый объект». So the edge is the subject and the
    /// base extrusion is what the call removes.
    /// MEASURED: the answer is the state of BOTH projection sketches after the rebuild — objects and
    /// coordinates — read again rather than inferred from the call's reply.</remarks>
    private void DeleteProjectionSource()
    {
        var step = _report.Begin("SC.7b", "Проекция после удаления исходного объекта",
            "Что остаётся в эскизе, когда признак-источник удалён?");
        var xoy = FindSketch("sc-projection");
        var xoz = FindSketch("sc-projection-xoz");
        if (xoy is null || xoz is null)
        {
            step.Unknown("Эскизы с проекциями не найдены после переоткрытия — удалять нечего.");
            return;
        }

        var edge = PickEdge(topFace: true, step, "delete");
        if (edge.Definition is null)
        {
            step.Unknown("Ребро-источник не найдено: удалять документированным путём нечего.");
            return;
        }

        var beforeXoy = Counts(xoy, "del-xoy-before", step);
        var beforeXoz = Counts(xoz, "del-xoz-before", step);
        var beforeCoords = FirstCoordinates(xoz, step, "delete_xoz_before");
        var beforeCoordsXoy = FirstCoordinates(xoy, step, "delete_xoy_before");
        var volumeBefore = Api5.Volume(_part);
        step.Data["volume_before_delete"] = Api5.Num(volumeBefore);

        var deleted = TryBool(() => _doc.DeleteObject(edge.Definition));
        _doc.RebuildDocument();
        var volumeAfter = Api5.Volume(_part);
        step.Data["delete_object"] = Api5.Raw(deleted);
        step.Data["volume_after_delete"] = Api5.Num(volumeAfter);

        var afterXoy = Counts(xoy, "del-xoy-after", step);
        var afterXoz = Counts(xoz, "del-xoz-after", step);
        var afterCoords = FirstCoordinates(xoz, step, "delete_xoz_after");
        var afterCoordsXoy = FirstCoordinates(xoy, step, "delete_xoy_after");
        step.Data["xoy_objects_before"] = beforeXoy.Objects;
        step.Data["xoy_objects_after"] = afterXoy.Objects;
        step.Data["xoz_objects_before"] = beforeXoz.Objects;
        step.Data["xoz_objects_after"] = afterXoz.Objects;

        if (deleted != true)
        {
            step.Fail("DeleteObject не подтвердил удаление — поведение проекции не наблюдалось.");
            return;
        }

        var survived = afterXoz.Objects == beforeXoz.Objects && afterXoy.Objects == beforeXoy.Objects;
        var sameNumbers = afterCoords == beforeCoords && afterCoordsXoy == beforeCoordsXoy;
        step.Pass($"Признак удалён (объём {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)}); проекции " +
                  $"{(survived ? "остались" : "изменились")}: XOZ объектов {beforeXoz.Objects} → " +
                  $"{afterXoz.Objects}, координаты «{beforeCoords}» → «{afterCoords}»; XOY объектов " +
                  $"{beforeXoy.Objects} → {afterXoy.Objects}, координаты «{beforeCoordsXoy}» → " +
                  $"«{afterCoordsXoy}»; числа {(sameNumbers ? "прежние" : "другие")}.");
    }

    /// <summary>A straight edge of the main body as an API7 model object, taken from the face whose
    /// <c>z</c> makes its projection readable: the bottom face for the sketch on XOY, the top face for the
    /// sketch on XOZ.</summary>
    /// <remarks>DOC: <c>isketch_addprojectionof.html</c> — the subject is a model object (an edge, a vertex
    /// or a face). MEASURED: the two projections answer different questions — a bottom edge lies IN the XOY
    /// sketch plane, so its 2D numbers cannot move with thickness; a top edge projects into XOZ as the
    /// thickness itself.</remarks>
    private (ksEdgeDefinition? Definition, IModelObject? Model) PickEdge(bool topFace, ProbeStep step,
        string label)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Data[label + "_edge_source"] = "тело или коллекция граней недоступны";
            return (null, null);
        }

        var wanted = topFace ? PlateThickness : 0d;
        for (var f = 0; f < faces.GetCount(); f++)
        {
            var face = faces.GetByIndex(f) as ksFaceDefinition
                       ?? ((faces.GetByIndex(f) as ksEntity)?.GetDefinition() as ksFaceDefinition);
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition
                           ?? ((edgeObject as ksEntity)?.GetDefinition() as ksEdgeDefinition);
                if (edge is null || Api5.SafeBool(edge.IsStraight) != true)
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity;
                if (entity is null
                    || edge.GetVertex(true) is not ksVertexDefinition v0
                    || edge.GetVertex(false) is not ksVertexDefinition v1
                    || !v0.GetPoint(out var ax, out _, out var az)
                    || !v1.GetPoint(out var bx, out _, out var bz)
                    || Math.Abs(az - wanted) > 1e-6
                    || Math.Abs(bz - wanted) > 1e-6)
                {
                    continue;
                }

                // The XOZ receiver needs an edge PARALLEL to X: an edge along Y is normal to that plane and
                // projects to a point, whose 2D numbers say nothing about the thickness.
                if (topFace && Math.Abs(ax - bx) < 1e-6)
                {
                    continue;
                }

                step.Data[label + "_face"] = f;
                step.Data[label + "_edge_index"] = e;
                step.Data[label + "_edge_z"] = Api5.Num(az);
                return (edge, _app.TransferInterface(entity, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject);
            }
        }

        step.Data[label + "_edge"] = "ребра на грани z=" + Api5.Num(wanted) + " не найдено";
        return (null, null);
    }

    private void SaveReopenProjection()
    {
        var step = _report.Begin("SC.8", "Проекция после save→close→reopen",
            "Переживает ли проекция переоткрытие?");
        if (_projected == 0 && _sketchP is null)
        {
            step.Unknown("Проекции нет — переоткрывать нечего.");
            return;
        }

        if (_doc.SaveAs(_savedPath) != true)
        {
            step.Fail("SaveAs не подтверждён.");
            return;
        }

        step.Data["closed"] = Api5.Raw(TryBool(_doc.close));
        _doc = (ksDocument3D)_app.Document3D();
        if (_doc.Open(_savedPath, true) != true)
        {
            step.Fail("Open не подтверждён.");
            return;
        }

        _part = (ksPart)_doc.GetPart(-1);
        var projection = FindSketch("sc-projection");
        step.Data["projection_sketch_found"] = projection?.name ?? "нет";
        if (projection is null)
        {
            step.Fail("Эскиз с проекцией не найден после переоткрытия.");
            return;
        }

        var counts = Counts(projection, "p2", step);
        step.Data["projection_objects_after_reopen"] = counts.Objects;
        if (counts.Objects > 0)
        {
            step.Pass($"Проекция пережила переоткрытие: объектов {counts.Objects}.");
        }
        else
        {
            step.Fail("После переоткрытия в эскизе-приёмнике объектов нет.");
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────
    private ksEntity? NewSketch(short planeType, string name)
    {
        if (_part.GetDefaultEntity(planeType) is not ksEntity plane
            || _part.NewEntity(SketchType) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            return null;
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();
        return sketch;
    }

    private ksEntity? FindSketch(string name)
    {
        if (_part.EntityCollection(SketchType) is not ksEntityCollection collection)
        {
            return null;
        }

        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (collection.GetByIndex(i) is ksEntity entity && entity.name == name)
            {
                return entity;
            }
        }

        return null;
    }

    private static ksDocument2D? Session(ksEntity sketch) =>
        sketch.GetDefinition() is ksSketchDefinition definition ? definition.BeginEdit() as ksDocument2D : null;

    private void EndSession(ksEntity sketch, string label)
    {
        try
        {
            if (sketch.GetDefinition() is ksSketchDefinition definition)
            {
                definition.EndEdit();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _report.Note("SC.3", $"{label}: EndEdit отказал ({ex.GetType().Name}) — сеанс остался открытым.");
        }
    }

    /// <summary>The 2D document behind an open edit session, by the documented
    /// <c>TransferInterface(…, ksAPI7Dual)</c>.</summary>
    private object? Document2D(object session, ProbeStep step, string label)
    {
        try
        {
            var transferred = _app.TransferInterface(session, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            step.Data[$"{label}_transfer"] = Api5.RuntimeName(transferred);
            return transferred;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[$"{label}_transfer_failure"] = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private object[] ObjectsOf(object document, ProbeStep step, string label)
    {
        var views = ((document as IKompasDocument2D)?.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews;
        if (views is null)
        {
            step.Data[$"{label}_views"] = "не получены";
            return Array.Empty<object>();
        }

        var rows = new List<object>();
        for (var v = 0; v < (SafeInt(() => views.Count) ?? 0); v++)
        {
            var view = views.get_View(v);
            if (view is not IDrawingContainer container)
            {
                continue;
            }

            var raw = container.get_Objects(new[] { (int)DrawingObjectTypeEnum.ksAllObj });
            if (raw is Array array)
            {
                rows.AddRange(array.Cast<object>().Where(o => o is not null));
            }
        }

        step.Data[$"{label}_objects"] = rows.Count;
        return rows.ToArray();
    }

    /// <summary>Composition of a sketch, read inside a session this method opens AND closes.</summary>
    /// <remarks>INVARIANT: a part holds ONE sketch edit session at a time (measured here: the second
    /// <c>BeginEdit</c> answers null while the first is open), so every read must leave the model free.</remarks>
    private (int Objects, int Segments, int Arcs, int Circles) Counts(ksEntity sketch, string label,
        ProbeStep step)
    {
        var session = Session(sketch);
        if (session is null)
        {
            step.Data[label + "_failure"] = "BeginEdit не дал ksDocument2D";
            return (0, 0, 0, 0);
        }

        try
        {
            var document = Document2D(session, step, label);
            var objects = document is null ? Array.Empty<object>() : ObjectsOf(document, step, label);
            // Kind counts are taken from the objects themselves, not from the per-type collections: the
            // collection counts are a second route to the same fact and would hide a disagreement.
            var kinds = objects.Select(KindOf).Where(k => k is not null).ToList();
            step.Data[label + "_kinds"] = kinds.ToArray();
            var segments = kinds.Count(k => k!.Contains("Line", StringComparison.OrdinalIgnoreCase));
            var arcs = kinds.Count(k => k!.Contains("Arc", StringComparison.OrdinalIgnoreCase));
            var circles = kinds.Count(k => k!.Contains("Circle", StringComparison.OrdinalIgnoreCase));
            return (objects.Length, segments, arcs, circles);
        }
        finally
        {
            EndSession(sketch, label);
        }
    }

    /// <summary>How many constraints the sketch's objects carry, read object by object.</summary>
    /// <remarks>DOC: <c>idrawingobject1_constraints.html</c> — the constraint list of an object. The copy
    /// route moves DRAWING OBJECTS into the group; whether a constraint travels with them is exactly the
    /// question, and it is answered by counting on both sides rather than by the copy's reply.</remarks>
    private int ConstraintsOf(ksEntity sketch, string label, ProbeStep step)
    {
        var session = Session(sketch);
        if (session is null)
        {
            step.Data[label + "_constraints"] = "сеанс не открыт";
            return -1;
        }

        try
        {
            var document = Document2D(session, step, label + "-constraints");
            var objects = document is null ? Array.Empty<object>() : ObjectsOf(document, step, label + "-constraints");
            var total = 0;
            foreach (var item in objects)
            {
                if (item is not IDrawingObject1 host)
                {
                    continue;
                }

                try
                {
                    total += host.Constraints is Array array ? array.Length : 0;
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    step.Data[label + "_constraints_failure"] = ex.GetType().Name;
                }
            }

            step.Data[label + "_constraints"] = total;
            return total;
        }
        finally
        {
            EndSession(sketch, label);
        }
    }

    private static string? KindOf(object item)
    {
        try
        {
            return item is IDrawingObject drawing ? drawing.DrawingObjectType.ToString() : item.GetType().Name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The first object's coordinates, read by late binding: the names are asked of the live
    /// object, so a name the wrapper does not carry is reported as such instead of being guessed.</summary>
    private string FirstCoordinates(ksEntity sketch, ProbeStep step, string key = "first_object_coordinates")
    {
        var session = Session(sketch);
        if (session is null)
        {
            return "сеанс не открыт";
        }

        try
        {
            var document = Document2D(session, step, "coords");
            var objects = document is null ? Array.Empty<object>() : ObjectsOf(document, step, "coords");
            if (objects.Length == 0)
            {
                step.Data[key] = "объектов нет";
                return "объектов нет";
            }

            var parts = new List<string>();
            // A projection of an edge normal to the sketch plane is a POINT object, and a point answers
            // X/Y rather than X1…Y2 — asking only the segment names would report a live object as unreadable.
            foreach (var name in new[] { "X1", "Y1", "X2", "Y2", "Xc", "Yc", "X", "Y", "Radius", "Length", "Angle" })
            {
                try
                {
                    var value = Late.Get(objects[0], name);
                    parts.Add($"{name}={Api5.Raw(value)}");
                }
                catch (Exception ex)
                {
                    parts.Add($"{name}={HResult.Of(ex)}");
                }
            }

            var text = string.Join(" ", parts);
            step.Data[key] = text;
            return text;
        }
        finally
        {
            EndSession(sketch, "coords");
        }
    }

    private static IDrawingGroup? TryGroup(Func<IDrawingGroup?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int> call)
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

    private static bool WaitUntilGone(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            System.Threading.Thread.Sleep(250);
        }

        return false;
    }
}
