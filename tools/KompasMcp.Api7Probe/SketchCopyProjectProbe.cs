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
                SaveReopenAndExtrude();
            }

            ProjectIntoSketch();
            SaveReopenProjection();
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
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step, "sc") is null)
        {
            step.Fail("Пластина не построена.");
            return;
        }

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
        // The receiving sketch sits in XOY, the plane the plate's bottom edges lie IN: a projection of an
        // edge that lies in the sketch plane is a segment, while an edge normal to it degenerates to a
        // point — the subject is chosen so a null answer means "no route", not "degenerate projection".
        _sketchP = NewSketch(PlaneXoy, "sc-projection")!;
        if (source is null || _sketchP is null)
        {
            step.Fail("Исходный эскиз или эскиз-приёмник не найдены.");
            return;
        }

        var sketch7 = _app.TransferInterface(_sketchP, (int)ksAPITypeEnum.ksAPI7Dual, 0) as ISketch;
        step.Data["sketch7_type"] = Api5.RuntimeName(sketch7);
        if (sketch7 is null)
        {
            step.Fail("Эскиз не перенесён в ISketch — проецировать нечем.");
            return;
        }

        // The documented argument is an IModelObject; the help names an edge, a vertex or a face, so the
        // subject is a body EDGE. A whole sketch is tried afterwards as a control: an empty answer there
        // must not be read as "projection does not work".
        var edge = EdgeModel(step);
        var sketchModel = _app.TransferInterface(source, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
        step.Data["projected_edge_type"] = Api5.RuntimeName(edge);
        step.Data["projected_sketch_type"] = Api5.RuntimeName(sketchModel);
        if (edge is null)
        {
            step.Fail("Ребро тела не перенесено в IModelObject — проецировать нечего.");
            return;
        }

        // INVARIANT: the projection is asked for INSIDE the sketch edit session — the API5 page of the
        // same member says so directly («работает только в режиме редактирования эскиза»), and a call
        // outside the session answers null, which reads like "no route" and is not one.
        var session = Session(_sketchP);
        step.Data["projection_session"] = Api5.RuntimeName(session);
        int count;
        int control;
        try
        {
            count = Project(sketch7, edge, step, "edge");
            control = Project(sketch7, sketchModel, step, "sketch-control");
        }
        finally
        {
            EndSession(_sketchP, "projection");
        }

        _projected = count;
        step.Data["projection_control_sketch"] = control;
        _doc.RebuildDocument();
        var counts = Counts(_sketchP, "p", step);
        step.Data["projection_segments"] = counts.Segments;
        step.Data["projection_arcs"] = counts.Arcs;
        step.Data["projection_circles"] = counts.Circles;
        step.Data["projection_objects_in_sketch"] = counts.Objects;

        if (count > 0 || counts.Objects > 0)
        {
            step.Pass($"AddProjectionOf(ребро) вернул {count} объектов; в эскизе-приёмнике объектов " +
                      $"{counts.Objects} ({counts.Segments} отрезков, {counts.Arcs} дуг, {counts.Circles} окружностей).");
            AssociateOnThicknessChange(step);
        }
        else
        {
            step.Fail("AddProjectionOf(ребро) вернул пустой массив и в эскизе объектов не появилось.");
        }
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

    /// <summary>Associativity: the plate gets thicker, the projected edge must follow.</summary>
    /// <remarks>MEASURED: the answer is a COORDINATE of the projection before and after the rebuild, not
    /// the call's reply — an unassociated copy of the edge would keep its old numbers.</remarks>
    private void AssociateOnThicknessChange(ProbeStep parent)
    {
        var step = _report.Begin("SC.7a", "Ассоциативность проекции при изменении тела",
            "Двигается ли проекция за исходным ребром?");
        var before = FirstCoordinates(_sketchP, step);
        step.Data["coordinates_before"] = before;

        var thickened = Thicken(PlateThickness + 10d, step);
        _doc.RebuildDocument();
        var after = FirstCoordinates(_sketchP, step);
        step.Data["coordinates_after"] = after;
        step.Data["body_volume"] = Api5.Num(Api5.Volume(_part));
        if (!thickened)
        {
            step.Unknown("Толщина не изменена — ассоциативность не проверена.");
            return;
        }

        if (before != after)
        {
            step.Pass("Координаты проекции изменились вместе с телом: связь с исходным ребром живая.");
        }
        else
        {
            step.Fail("Координаты проекции не изменились после изменения тела: связь не подтверждена.");
        }

        _ = parent;
    }

    private bool Thicken(double thickness, ProbeStep step)
    {
        // The feature is looked for in the OPERATIONS collection first: after a reopen the base extrusion
        // is not in EntityCollection(o3d_baseExtrusion), and an empty collection reads like "no feature".
        foreach (var collectionType in new short[] { 110, BaseExtrusion })
        {
            if (_part.EntityCollection(collectionType) is not ksEntityCollection collection)
            {
                continue;
            }

            step.Data[$"thicken_collection_{collectionType}"] = collection.GetCount();
            for (var i = 0; i < collection.GetCount(); i++)
            {
                // The tree entity's own type number is not the o3d_baseExtrusion constant (measured:
                // collection 110 holds two features and neither answers type == 24), so the definition
                // type is what identifies a base extrusion here.
                if (collection.GetByIndex(i) is not ksEntity entity
                    || entity.GetDefinition() is not ksBaseExtrusionDefinition definition)
                {
                    continue;
                }

                step.Data["thicken_feature"] = entity.name;
                step.Data["thicken_set_side_param"] =
                    Api5.Raw(TryBool(() => definition.SetSideParam(true, 0, thickness, 0d, false)));
                var rebuilt = TryBool(entity.Update);
                step.Data["thicken_update"] = Api5.Raw(rebuilt);
                _doc.RebuildDocument();
                step.Data["thicken_volume"] = Api5.Num(Api5.Volume(_part));
                return rebuilt == true;
            }
        }

        step.Data["thicken_feature"] = "признак выдавливания не найден";
        return false;
    }

    /// <summary>A straight edge of the main body as an API7 model object, by the documented transfer.
    /// An edge lying in the XOY plane is preferred, so the projection has a segment to produce.</summary>
    private IModelObject? EdgeModel(ProbeStep step)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Data["edge_source"] = "тело или коллекция граней недоступны";
            return null;
        }

        IModelObject? fallback = null;
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
                if (entity is null)
                {
                    continue;
                }

                var inPlane = edge.GetVertex(true) is ksVertexDefinition v0
                              && edge.GetVertex(false) is ksVertexDefinition v1
                              && v0.GetPoint(out _, out _, out var az)
                              && v1.GetPoint(out _, out _, out var bz)
                              && Math.Abs(az) < 1e-6 && Math.Abs(bz) < 1e-6;
                var transferred = _app.TransferInterface(entity, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;
                if (!inPlane)
                {
                    fallback ??= transferred;
                    continue;
                }

                step.Data["edge_face"] = f;
                step.Data["edge_index"] = e;
                step.Data["edge_in_plane"] = true;
                step.Data["edge_type"] = Api5.RuntimeName(transferred);
                return transferred;
            }
        }

        step.Data["edge_in_plane"] = false;
        step.Data["edge_type"] = Api5.RuntimeName(fallback);
        return fallback;
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
    private string FirstCoordinates(ksEntity sketch, ProbeStep step)
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
                return "объектов нет";
            }

            var parts = new List<string>();
            foreach (var name in new[] { "X1", "Y1", "X2", "Y2", "Xc", "Yc", "Radius", "Length", "Angle" })
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
            step.Data["first_object_coordinates"] = text;
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
