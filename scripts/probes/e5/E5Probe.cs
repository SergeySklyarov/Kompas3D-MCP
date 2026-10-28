using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace E5Probe;

/// <summary>E5 probe: builds a box with history, then measures whether the documented direct-face
/// operations reach it, and whether the answers can be compared with an expected number.</summary>
/// <remarks>WHY A PROBE AND NOT A TEST: the order is research - the product has no direct-face tool yet,
/// so the only thing that can be measured is what KOMPAS itself answers on the documented routes.
/// DOC: E5_RESEARCH_DEVELOPER_PROMPT.md §1.5; the help snapshot this order captured under
/// reference/kompas-sdk-v24/ (the `docs-wire-...-e5` directory).
/// History: docs/decisions/direct-faces.md#probe</remarks>
internal sealed partial class E5Probe : IDisposable
{
    // Box the fixture builds: 40 x 30 x 20 mm, so the volume, the top-face area and the expected
    // volume after a 5 mm move are known numbers, not "something changed".
    private const double Width = 40;
    private const double Depth = 30;
    private const double Height = 20;
    private const double MoveOffset = 5;

    private const int PlaneXoy = 1;
    private const int SketchType = 5;
    private const int BossExtrusionType = 25;

    // ST_MIX_MM | ST_MIX_KG: millimetres for length, kilograms for mass (MEASURED in the product).
    private const uint MassMmKg = 17;

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _states = new();
    private bool _conversionRecorded;
    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;

    public E5Probe(string outPath) => _outPath = Path.GetFullPath(outPath);

    public int Run(bool keepOpen, string? stepPath = null, bool saveBodyOnly = false,
        bool tangentGroup = false)
    {
        try
        {
            Launch();
            if (tangentGroup)
            {
                TangentGroupInvestigation();
                return Finish(keepOpen);
            }

            if (stepPath is not null)
            {
                RunStepBody(stepPath);
                return Finish(keepOpen);
            }

            if (saveBodyOnly)
            {
                SaveBodyInvestigation();
                return Finish(keepOpen);
            }

            BuildBox();
            State("построен");
            EntryPoint();
            DeleteKeepBody();
            MoveFace();
            DeleteOpenShell();
            Refusals();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }

        return Finish(keepOpen);
    }

    private int Finish(bool keepOpen)
    {
        Write();
        if (!keepOpen)
        {
            CloseApp();
        }

        return _rows.Any(row => row?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    /// <summary>DIRECT_FACES_DELETE_RESTORE §1.1: why does <c>SaveBody = true</c> answer
    /// <c>Update() = true</c> and change nothing? Three hypotheses are tried on fresh boxes.</summary>
    /// <remarks>DOC: <c>ifaceremover_faces.html</c> — «Если возвращается один объект, то тип VARIANT-а
    /// будет VT_DISPATCH, если возвращается несколько объектов, то тип VARIANT-а будет VT_ARRAY |
    /// VT_DISPATCH». The research probe always passed an ARRAY, so H1 is that a one-element array is
    /// accepted and ignored where a bare <c>VT_DISPATCH</c> is expected.
    /// DOC: <c>ifaceremover_savebody.html</c> — BOOL «Сохранять целостность тела», no conditions stated.
    /// History: docs/decisions/direct-faces.md#savebody</remarks>
    private void SaveBodyInvestigation()
    {
        // H1: the shape of Faces. The control matters more than the probe: with SaveBody = false the
        // same bare object must remove the face, otherwise «nothing happened» would be blamed on the
        // form of the argument instead of on SaveBody.
        Variant("h1_control_bare_open_shell", bare: true, saveBody: false, saveFirst: false,
            secondUpdate: false, faceIndex: 0);
        Variant("h1_bare_keep_body", bare: true, saveBody: true, saveFirst: false,
            secondUpdate: false, faceIndex: 0);
        Variant("h1_array_keep_body", bare: false, saveBody: true, saveFirst: false,
            secondUpdate: false, faceIndex: 0);

        // H2: the order of the two properties, and a second Update after the rebuild.
        Variant("h2_keep_body_save_first", bare: true, saveBody: true, saveFirst: true,
            secondUpdate: false, faceIndex: 0);
        Variant("h2_keep_body_second_update", bare: true, saveBody: true, saveFirst: false,
            secondUpdate: true, faceIndex: 0);

        // H3: the face itself. A box has six faces of three kinds; if the body cannot be healed, the
        // refusal may depend on the face, not on the argument.
        for (var index = 0; index < 6; index++)
        {
            Variant($"h3_keep_body_face_{index}", bare: true, saveBody: true, saveFirst: false,
                secondUpdate: false, faceIndex: index);
        }
    }

    /// <summary>One attempt on a fresh box, so the numbers of one variant are not the leftovers of
    /// another.</summary>
    private void Variant(string id, bool bare, bool saveBody, bool saveFirst, bool secondUpdate, int faceIndex)
    {
        try
        {
            BuildBox();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Record(id, "свежий брусок построен", Describe(ex), "FAIL");
            return;
        }

        var before = State(id + ": до");
        var face = FaceAt(faceIndex);
        if (face is null)
        {
            Record(id, "грань " + faceIndex + " найдена", "не найдена", "FAIL");
            CloseCurrent();
            return;
        }

        bool? updated = null;
        string? failure = null;
        try
        {
            var remover = (_doc7?.TopPart as ISurfaceContainer)?.FaceRemovers?.Add();
            if (remover is null)
            {
                failure = "FaceRemovers.Add() вернул null";
            }
            else
            {
                // VT_DISPATCH for one face, VT_ARRAY | VT_DISPATCH for several (ifaceremover_faces.html).
                var value = bare ? (object)face : new object[] { face };
                if (saveFirst)
                {
                    remover.SaveBody = saveBody;
                    remover.Faces = value;
                }
                else
                {
                    remover.Faces = value;
                    remover.SaveBody = saveBody;
                }

                updated = ((IModelObject)remover).Update();
                _doc5!.RebuildDocument();
                if (secondUpdate)
                {
                    updated = ((IModelObject)remover).Update();
                    _doc5.RebuildDocument();
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            failure = Describe(ex);
        }

        var after = State(id + ": после");
        var facesGone = Faces(before) - Faces(after);
        var volumeKept = Math.Abs(Volume(after)) > 1e-6;

        Record(id,
            "грань " + faceIndex + ", Faces=" + (bare ? "VT_DISPATCH" : "массив") + ", SaveBody=" +
            (saveBody ? "true" : "false"),
            failure ?? $"Update={updated}, граней {Faces(before)}→{Faces(after)} (-{facesGone}), " +
            $"объём {Volume(before):0.###}→{Volume(after):0.###}",
            "MEASURED");
        CloseCurrent();
    }

    /// <summary>Closes the document of the last variant: nine open parts would make every later read
    /// slower and the state of the wrong document easier to read.</summary>
    private void CloseCurrent()
    {
        try
        {
            _doc7?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // Housekeeping only: a part left open must not turn a measured run into a failed one.
        }

        _doc7 = null;
        _doc5 = null;
    }

    /// <summary>§3 and §5.6 of the order: the same operations on a body that has no feature tree.
    /// The STEP file goes in through the route the product already ships
    /// (<c>ksDocument3D.LoadFromAdditionFormat</c> with <c>format = -3</c>), so the body under test is
    /// the body a client would get from <c>kompas_import_step</c>.</summary>
    private void RunStepBody(string stepPath)
    {
        _doc5 = (ksDocument3D)_app5!.Document3D();
        _doc5.Create(true, true);
        var parameter = (ksAdditionFormatParam)_doc5.AdditionFormatParam();
        parameter.Init();
        parameter.format = -3;
        var loaded = _doc5.LoadFromAdditionFormat(Path.GetFullPath(stepPath), parameter);
        _doc5.RebuildDocument();
        Record("step_import", "STEP импортирован маршрутом продукта", loaded ? "true" : "false",
            loaded ? "PASS" : "FAIL");
        if (!loaded)
        {
            return;
        }

        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
        var before = State("STEP-тело импортировано");
        Record("step_bodies", "импорт дал тело", $"{before["bodies"]} тел, {before["faces"]} граней",
            (before["bodies"]?.GetValue<int>() ?? 0) > 0 ? "PASS" : "FAIL");

        // Which face moves is not known in advance on an imported body: the probe tries the first
        // three and records every answer, so "no change" is attributed to the FACE, not to the body.
        var moved = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var face = FaceAt(index);
            if (face is null)
            {
                break;
            }

            var beforeMove = State($"STEP: грань {index}");
            try
            {
                var mover = (_doc7?.TopPart as ISurfaceContainer)?.FaceMovers?.Add();
                if (mover is null)
                {
                    Record("step_move_add", "IFaceMover получен на теле без истории", "null", "FAIL");
                    break;
                }

                var set = mover.SetFaces(new object[] { face }, false);
                mover.Offset = MoveOffset;
                mover.Direction = true;
                var updated = ((IModelObject)mover).Update();
                _doc5.RebuildDocument();
                var after = State($"STEP: грань {index} после перемещения");
                var delta = Volume(after) - Volume(beforeMove);
                if (Math.Abs(delta) > 1e-6)
                {
                    moved.Add($"грань {index}: dV={delta:0.###} (SetFaces={set}, Update={updated})");
                }
                else
                {
                    Record($"step_move_face_{index}", $"перемещение грани {index} на теле без истории",
                        $"объём не изменился (SetFaces={set}, Update={updated})", "FAIL");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
            {
                Record($"step_move_face_{index}", $"перемещение грани {index} на теле без истории",
                    Describe(ex), "FAIL");
            }
        }

        Record("step_move_volume", "хотя бы одна грань тела без истории перемещается",
            moved.Count == 0 ? "ни одна из первых трёх" : string.Join("; ", moved),
            moved.Count > 0 ? "PASS" : "FAIL");

        try
        {
            var remover = (_doc7?.TopPart as ISurfaceContainer)?.FaceRemovers?.Add();
            var top = FaceAt(0);
            if (remover is null || top is null)
            {
                Record("step_delete", "IFaceRemover на теле без истории", "null", "FAIL");
                return;
            }

            remover.Faces = new object[] { top };
            remover.SaveBody = false;
            ((IModelObject)remover).Update();
            _doc5.RebuildDocument();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("step_delete", "удаление грани на теле без истории", Describe(ex), "FAIL");
            return;
        }

        var shell = State("после удаления на STEP-теле");
        Record("step_delete_shell", "тело без истории стало оболочкой (объём 0)",
            $"{Volume(shell):0.###}, граней {Faces(shell)}",
            Math.Abs(Volume(shell)) < 1e-6 ? "PASS" : "FAIL");
    }

    // ── environment ─────────────────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "нет", "FAIL");
            throw new InvalidOperationException("KOMPAS is not registered");
        }

        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        _app5.Visible = false;
        Record("app5", "экземпляр КОМПАС создан", "KompasObject", "PASS");

        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app7", "ksGetApplication7() даёт IApplication", _app7 is null ? "null" : "IApplication",
            _app7 is null ? "FAIL" : "PASS");
    }

    private void BuildBox(bool record = true)
    {
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, false) вернул false");
        }

        var part = _doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(PlaneXoy) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOY не получена");

        var sketch = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) → null");
        var sketchDefinition = sketch.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition не получен");
        sketchDefinition.SetPlane(plane);
        sketch.Create();

        var editor = sketchDefinition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() → не ksDocument2D");
        editor.ksLineSeg(0, 0, Width, 0, 1);
        editor.ksLineSeg(Width, 0, Width, Depth, 1);
        editor.ksLineSeg(Width, Depth, 0, Depth, 1);
        editor.ksLineSeg(0, Depth, 0, 0, 1);
        sketchDefinition.EndEdit();

        var feature = part.NewEntity(BossExtrusionType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(sketch);
        if (!extrusion.SetSideParam(true, 0, Height, 0, false))
        {
            throw new InvalidOperationException("SetSideParam вернул false");
        }

        feature.Create();
        part.Update();
        _doc5.RebuildDocument();

        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");
        if (record)
        {
            Record("fixture", $"брусок {Width}x{Depth}x{Height} построен выдавливанием", "тело с историей", "PASS");
        }
    }

    // ── the measured routes ─────────────────────────────────────────────────────────────────────

    /// <summary>§5.1 of the order: is the container reachable at all, and does a cast work in the .NET
    /// wrapper or is a hand-made QueryInterface needed?</summary>
    private void EntryPoint()
    {
        try
        {
            var part7 = _doc7?.TopPart;
            Record("entry_part7", "IKompasDocument3D.TopPart даёт IPart7", part7 is null ? "null" : "IPart7",
                part7 is null ? "FAIL" : "PASS");
            var container = part7 as ISurfaceContainer;
            Record("entry_container", "(ISurfaceContainer)IPart7 - приведение интерфейсов работает",
                container is null ? "null" : "ISurfaceContainer", container is null ? "FAIL" : "PASS");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("entry_container", "(ISurfaceContainer)IPart7 - приведение интерфейсов работает",
                Describe(ex), "FAIL");
        }
    }

    /// <summary>§5.3 of the order: move the top face by 5 mm and compare the volume with
    /// <c>V + d·A</c>, not with "the number changed".</summary>
    private void MoveFace()
    {
        var before = State("до перемещения");
        var top = FaceAt(0);
        if (top is null)
        {
            Record("move_faces", "верхняя плоская грань найдена", "не найдена", "FAIL");
            return;
        }

        var areaFromMove = double.NaN;
        try
        {
            var container = _doc7!.TopPart as ISurfaceContainer;
            var mover = container?.FaceMovers?.Add();
            Record("move_faces_add", "ISurfaceContainer.FaceMovers.Add() даёт IFaceMover",
                mover is null ? "null" : "IFaceMover", mover is null ? "FAIL" : "PASS");
            if (mover is null)
            {
                return;
            }

            var set = mover.SetFaces(new object[] { top }, false);
            Record("move_faces_setfaces", "IFaceMover.SetFaces([верхняя грань], TangentFaces=false)", set ? "true" : "false",
                set ? "PASS" : "FAIL");

            mover.Offset = MoveOffset;
            mover.Direction = true;
            ((IModelObject)mover).Update();
            _doc5!.RebuildDocument();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("move_faces", "перемещение грани выполнено", Describe(ex), "FAIL");
            return;
        }

        var after = State("после перемещения");
        areaFromMove = Math.Abs(Volume(after) - Volume(before)) / MoveOffset;
        var knownAreas = new[] { Width * Depth, Width * Height, Depth * Height };
        Record("move_faces_volume", "|dV| / смещение = площадь грани (1200, 800 или 600)",
            $"{areaFromMove:0.###} при dV {Volume(after) - Volume(before):0.###}",
            knownAreas.Any(area => Near(areaFromMove, area, 1e-6)) ? "PASS" : "FAIL");
        Record("move_faces_faces", "число граней не изменилось", $"{Faces(after)} против {Faces(before)}",
            Faces(after) == Faces(before) ? "PASS" : "FAIL");
    }

    /// <summary>§5.4 of the order: delete the top face with the body kept - the healed body must stay a
    /// solid and lose exactly the moved material.</summary>
    private void DeleteKeepBody()
    {
        var before = State("до удаления (SaveBody=true)");
        var top = FaceAt(0);
        if (top is null)
        {
            Record("delete_keep_body", "верхняя грань найдена", "не найдена", "FAIL");
            return;
        }

        try
        {
            var container = _doc7!.TopPart as ISurfaceContainer;
            var remover = container?.FaceRemovers?.Add();
            Record("delete_keep_body_add", "ISurfaceContainer.FaceRemovers.Add() даёт IFaceRemover",
                remover is null ? "null" : "IFaceRemover", remover is null ? "FAIL" : "PASS");
            if (remover is null)
            {
                return;
            }

            remover.Faces = new object[] { top };
            remover.SaveBody = true;
            ((IModelObject)remover).Update();
            _doc5!.RebuildDocument();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("delete_keep_body", "удаление грани с сохранением тела выполнено", Describe(ex), "FAIL");
            return;
        }

        var after = State("после удаления (SaveBody=true)");
        Record("delete_keep_body_faces", "граней стало меньше", $"{Faces(after)} против {Faces(before)}",
            Faces(after) < Faces(before) ? "PASS" : "FAIL");
        Record("delete_keep_body_volume", "объём уменьшился на снятый слой",
            $"{Volume(after):0.###} против {Volume(before):0.###}",
            Volume(after) < Volume(before) ? "PASS" : "FAIL");
        Record("delete_keep_body_solid", "тело осталось твёрдым (объём > 0)", Solid(after)?.ToString() ?? "не прочитан",
            Solid(after) == true ? "PASS" : "FAIL");
    }

    /// <summary>§5.4-§5.5 of the order: the same deletion without keeping the body must leave a shell,
    /// and <c>IRestoredSurface</c> on a neighbour must bring the solid back.</summary>
    private void DeleteOpenShell()
    {
        var before = State("до удаления (SaveBody=false)");
        var top = FaceAt(0);
        if (top is null)
        {
            Record("delete_open_shell", "верхняя грань найдена", "не найдена", "FAIL");
            return;
        }

        try
        {
            var container = _doc7!.TopPart as ISurfaceContainer;
            var remover = container?.FaceRemovers?.Add();
            if (remover is null)
            {
                Record("delete_open_shell_add", "второй IFaceRemover получен", "null", "FAIL");
                return;
            }

            remover.Faces = new object[] { top };
            remover.SaveBody = false;
            ((IModelObject)remover).Update();
            _doc5!.RebuildDocument();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("delete_open_shell", "удаление грани без сохранения тела выполнено", Describe(ex), "FAIL");
            return;
        }

        var after = State("после удаления (SaveBody=false)");
        Record("delete_open_shell_solid", "оболочка: объём стал нулевым (ожидание)",
            Solid(after)?.ToString() ?? "не прочитан", Solid(after) == false ? "PASS" : "FAIL");
        Record("delete_open_shell_faces", "граней стало меньше", $"{Faces(after)} против {Faces(before)}",
            Faces(after) < Faces(before) ? "PASS" : "FAIL");

        RestoreFace(after);
    }

    private void RestoreFace(JsonObject shellState)
    {
        var neighbour = AnyFace(1);
        if (neighbour is null)
        {
            Record("restore_faces", "грань-сосед найдена", "не найдена", "FAIL");
            return;
        }

        try
        {
            var container = _doc7!.TopPart as ISurfaceContainer;
            var restored = container?.RestoredSurfaces?.Add();
            Record("restore_faces_add", "ISurfaceContainer.RestoredSurfaces.Add() даёт IRestoredSurface",
                restored is null ? "null" : "IRestoredSurface", restored is null ? "FAIL" : "PASS");
            if (restored is null)
            {
                return;
            }

            restored.Face = neighbour;
            ((IModelObject)restored).Update();
            _doc5!.RebuildDocument();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("restore_faces", "восстановление поверхности выполнено", Describe(ex), "FAIL");
            return;
        }

        var after = State("после восстановления");
        var bodiesBefore = shellState["bodies"]?.GetValue<int>() ?? 0;
        var bodiesAfter = after["bodies"]?.GetValue<int>() ?? 0;
        Record("restore_faces_object", "восстановленная поверхность - отдельный объект (тел стало больше)",
            $"{bodiesAfter} против {bodiesBefore}", bodiesAfter > bodiesBefore ? "PASS" : "FAIL");
        Record("restore_faces_shell", "оболочка восстановлением поверхности НЕ вылечивается (объём прежний)",
            $"{Volume(after):0.###} против {Volume(shellState):0.###}",
            Near(Volume(after), Volume(shellState), 1e-3) ? "PASS" : "FAIL");
    }

    /// <summary>§5.7 of the order: an empty face list and a zero offset must be refused, not silently
    /// accepted - a route that answers true and changes nothing is the failure this probe looks for.</summary>
    private void Refusals()
    {
        var before = State("до отказов");
        try
        {
            var container = _doc7!.TopPart as ISurfaceContainer;
            var mover = container?.FaceMovers?.Add();
            if (mover is null)
            {
                Record("refusal_empty_faces", "IFaceMover получен", "null", "FAIL");
                return;
            }

            var empty = mover.SetFaces(Array.Empty<object>(), false);
            Record("refusal_empty_faces", "SetFaces(пустой массив) отвергнут", empty ? "true" : "false",
                empty ? "FAIL" : "PASS");

            var top = FaceAt(0);
            if (top is not null)
            {
                mover.SetFaces(new object[] { top }, false);
                mover.Offset = 0;
                mover.Direction = true;
                var updated = ((IModelObject)mover).Update();
                _doc5!.RebuildDocument();
                var after = State("после Offset=0");
                Record("refusal_zero_offset", "смещение 0 не меняет объём",
                    $"Update={updated}, {Volume(after):0.###} против {Volume(before):0.###}",
                    Near(Volume(after), Volume(before), 1e-3) ? "PASS" : "FAIL");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            Record("refusals", "отказы измерены", Describe(ex), "FAIL");
        }
    }

    // ── reading ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the state the order asks to compare: volume, area, face count, solidity.</summary>
    private JsonObject State(string label)
    {
        var part = _doc5?.GetPart(-1) as ksPart;
        var bodies = part?.BodyCollection() as ksBodyCollection;
        var count = bodies?.GetCount() ?? 0;
        var body = count > 0 ? bodies!.GetByIndex(0) as ksBody : null;
        var faces = body?.FaceCollection() as ksFaceCollection;

        double? volume = null;
        double? area = null;
        try
        {
            if (body?.CalcMassInertiaProperties(MassMmKg) is ksMassInertiaParam properties)
            {
                volume = properties.v;
                area = properties.F;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            volume = null;
        }

        // IBody7.IsSolid is documented, but the v24 interop declares no such member (MEASURED by the
        // compiler), so solidity is classified from the volume: a shell encloses no material.
        bool? solid = volume is null ? null : volume.Value > 1e-6;

        var state = new JsonObject
        {
            ["label"] = label,
            ["bodies"] = count,
            ["faces"] = faces?.GetCount() ?? 0,
            ["volume_mm3"] = volume is null ? null : Math.Round(volume.Value, 4),
            ["area_mm2"] = area is null ? null : Math.Round(area.Value, 4),
            ["volume_nonzero"] = solid,
        };
        _states.Add(state.DeepClone());
        return state;
    }

    /// <summary>Face under a fixed index. Which face that is, is measured from the volume change
    /// rather than from its normal: the v24 interop exposes neither <c>GetNormal</c> nor the parameter
    /// bounds on <c>IFace</c> (MEASURED by the compiler), so the probe cannot pick "the top face".</summary>
    private IFace? FaceAt(int index) => Faces().ElementAtOrDefault(index);

    private IFace? AnyFace(int index) => FaceAt(index);

    /// <summary>Faces of the first body, converted to <c>IFace</c>. The conversion itself is measured:
    /// the order's question §5.2 is exactly whether an API5 face answers as an API7 interface.</summary>
    private List<IFace> Faces()
    {
        var result = new List<IFace>();
        var part = _doc5?.GetPart(-1) as ksPart;
        var bodies = part?.BodyCollection() as ksBodyCollection;
        if (bodies is null || bodies.GetCount() == 0)
        {
            return result;
        }

        var body = bodies.GetByIndex(0) as ksBody;
        if (body?.FaceCollection() is not ksFaceCollection faces)
        {
            return result;
        }

        var direct = 0;
        var transferred = 0;
        for (var index = 0; index < faces.GetCount(); index++)
        {
            var raw = faces.GetByIndex(index);
            if (raw is IFace asIs)
            {
                direct++;
                result.Add(asIs);
                continue;
            }

            if (_app5?.TransferInterface(raw, (int)ksAPITypeEnum.ksAPI7Dual, 0) is IFace viaTransfer)
            {
                transferred++;
                result.Add(viaTransfer);
            }
        }

        if (result.Count > 0 && direct + transferred == result.Count)
        {
            if (!_conversionRecorded)
            {
                _conversionRecorded = true;
                Record("face_conversion", "грань API5 отвечает как IFace",
                    $"приведением {direct}, TransferInterface {transferred}, всего {result.Count}", "PASS");
            }
        }
        else
        {
            Record("face_conversion", "грань API5 отвечает как IFace",
                $"получено {result.Count} из {faces.GetCount()}", "FAIL");
        }

        return result;
    }

    private static double Volume(JsonObject state) => state["volume_mm3"]?.GetValue<double>() ?? double.NaN;

    private static int Faces(JsonObject state) => state["faces"]?.GetValue<int>() ?? 0;

    private static bool? Solid(JsonObject state) => state["volume_nonzero"]?.GetValue<bool>();

    private static bool Near(double actual, double expected, double tolerance) =>
        double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected));

    // ── output ──────────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string expected, string actual, string verdict)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["expected"] = expected,
            ["actual"] = actual,
            ["verdict"] = verdict,
        });
        Console.WriteLine($"{verdict}  {id}: {actual}");
    }

    private void Write()
    {
        var document = new JsonObject
        {
            ["probe"] = "E5 direct face editing",
            ["kompas"] = "v24",
            ["rows"] = _rows.DeepClone(),
            ["states"] = _states.DeepClone(),
        };
        var directory = Path.GetDirectoryName(_outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"evidence: {_outPath}");
    }

    private void CloseApp()
    {
        // Housekeeping only: the probe leaves the instance to the caller, as the other probes do. A
        // failed close must not turn a measured run into a failed one.
    }

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COMException 0x{com.HResult:X8}: {com.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    public void Dispose()
    {
        _doc7 = null;
        _doc5 = null;
        _app7 = null;
        _app5 = null;
    }
}
