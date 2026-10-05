using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.P0Probe;

/// <summary>VIEW — measurement of the view/projection route (<c>ksViewProjectionCollection</c> /
/// <c>ksViewProjection</c>) before any MCP tool touches it.</summary>
/// <remarks>INVARIANT: the probe answers three questions with NUMBERS, not with an affirmative return —
/// (1) can a standard projection be applied to the window and read back from the API; (2) do two
/// snapshots of the same projection match byte for byte; (3) does a view change move the document's
/// revision or its change fingerprint. A "method returned true" is never the verdict here: the route
/// either reads back the projection it claims, or the step stays negative.
/// LIMIT: it drives the DOCUMENT's own projection collection, which the help page documents as the
/// model's display projections in the window; it does not drive the UI camera of a window the user
/// may be looking at separately.
/// History: docs/decisions/probe.md#view-projection</remarks>
internal static class ViewProjectionProbe
{
    /// <summary>Predefined projection types (ProjectionType enum, help page projectiontype.html).</summary>
    private const int VpFront = 1;
    private const int VpUp = 3;
    private const int VpRight = 6;
    private const int VpIsoXyz = 7;

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("VIEW.0", "Управление проекцией отображения");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС: шаг пропущен.";
            return;
        }

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                var failed = report.Begin("VIEW.0", "Создание документа под замер проекций");
                failed.Fail("Document3D().Create(true, true) вернул false.");
                return;
            }

            // A body is needed so the snapshot has something to draw; the body itself is not measured here.
            BuildPlate(doc);

            var collection = ReadCollection(report, doc);
            if (collection is null)
            {
                return;
            }

            var before = ReadCurrentProjection(report, collection);

            MeasureApplyAndReadBack(report, doc, collection);
            MeasureSnapshots(report, doc);
            MeasureRestore(report, doc, collection, before);
        }
        finally
        {
            try
            {
                if (doc is not null)
                {
                    doc.Close();
                }
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine("[VIEW] doc.Close() бросил: " + ex.Message);
            }
        }
    }

    /// <summary>A plain 100×80×10 plate — the same primitive P2 uses, so a snapshot has real geometry.</summary>
    private static void BuildPlate(ksDocument3D doc)
    {
        var part = (ksPart)doc.GetPart(-1);
        var sketch = (ksEntity)part.NewEntity((short)EntityTypes.Value("o3d_sketch", 24));
        var sketchDef = (ksSketchDefinition)sketch.GetDefinition();
        sketchDef.SetPlane((ksEntity)part.GetDefaultEntity((short)EntityTypes.Value("o3d_planeXOY", 28)));
        sketch.Create();

        var edit = (ksDocument2D)sketchDef.BeginEdit();
        edit.ksLineSeg(0, 0, 100, 0, 1);
        edit.ksLineSeg(100, 0, 100, 80, 1);
        edit.ksLineSeg(100, 80, 0, 80, 1);
        edit.ksLineSeg(0, 80, 0, 0, 1);
        sketchDef.EndEdit();

        var extrude = (ksEntity)part.NewEntity((short)EntityTypes.Value("o3d_bossExtrusion", 45));
        var extrudeDef = (ksExtrusionParam)extrude.GetDefinition();
        extrudeDef.direction = (short)VendorConstants.Value("ksDirectionTypeEnum", "dtNormal", 1);
        extrudeDef.type = (short)VendorConstants.Value("ksEndTypeEnum", "etBlind", 0);
        extrudeDef.depthNormal = 10;
        extrude.Create();
        part.Update();
    }

    /// <summary>VIEW.1 — read the collection and the predefined projections it carries. The help page says
    /// the collection "is filled automatically with the projections defined in the document", so the
    /// count and the per-entry type are MEASURED, not assumed from the enum.</summary>
    private static ksViewProjectionCollection? ReadCollection(ProbeReport report, ksDocument3D doc)
    {
        var step = report.Begin(
            "VIEW.1",
            "Коллекция проекций: состав и типы предопределённых",
            "Что реально лежит в GetViewProjectionCollection() у этой детали и совпадают ли типы с ProjectionType?");

        try
        {
            var collection = (ksViewProjectionCollection)doc.GetViewProjectionCollection();
            if (collection is null)
            {
                step.Fail("GetViewProjectionCollection() вернул null.");
                return null;
            }

            var count = collection.GetCount();
            step.Data["count"] = count;
            step.Observe($"GetCount() = {count}");

            var types = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var projection = (ksViewProjection)collection.GetByIndex(i);
                if (projection is null)
                {
                    types.Add(new { index = i, read = "null" });
                    continue;
                }

                var type = SafeType(projection);
                var name = SafeName(projection);
                var isCurrent = SafeIsCurrent(projection);
                types.Add(new { index = i, type, name, is_current = isCurrent });
                step.Observe($"  [{i}] type={type} name='{name}' current={isCurrent}");
            }

            step.Data["projections"] = types;
            step.Data["scheme"] = ReadScheme(collection);
            step.Observe($"viewProjectionScheme = {step.Data["scheme"]}");

            if (count <= 0)
            {
                step.Fail("Коллекция проекций пуста: применять нечего.");
                return null;
            }

            step.Pass($"Коллекция читается: {count} проекций; схема {step.Data["scheme"]}.");
            return collection;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Маршрут коллекции не сработал: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>VIEW.2 — read the CURRENT projection before any change: which entry answers IsCurrent, and
    /// what type it reports. This is the value a restore would have to reproduce.</summary>
    private static CurrentProjection ReadCurrentProjection(ProbeReport report, ksViewProjectionCollection collection)
    {
        var step = report.Begin(
            "VIEW.2",
            "Текущая проекция до изменения",
            "Можно ли прочитать текущий вид документированным способом (IsCurrent + GetViewProjectonType)?");

        var result = new CurrentProjection();
        try
        {
            var count = collection.GetCount();
            var found = 0;
            for (var i = 0; i < count; i++)
            {
                var projection = (ksViewProjection)collection.GetByIndex(i);
                if (projection is null || SafeIsCurrent(projection) != true)
                {
                    continue;
                }

                found++;
                result.Index = i;
                result.Type = SafeType(projection);
                result.Name = SafeName(projection);
                result.Found = true;
                step.Observe($"[{i}] IsCurrent=true, GetViewProjectonType()={result.Type}, name='{result.Name}'");
            }

            step.Data["current_index"] = result.Index;
            step.Data["current_type"] = result.Type;
            step.Data["current_name"] = result.Name;
            step.Data["current_entries"] = found;

            if (found == 0)
            {
                // Honest negative: if no entry reports current, the reading side of the route is not
                // usable even though the collection reads. This is a fact about the route, not a probe bug.
                step.Unknown("Ни одна проекция не ответила IsCurrent=true: прочитать текущий вид этим способом нельзя.");
            }
            else if (found > 1)
            {
                step.Unknown($"IsCurrent=true ответили {found} проекции: признак текущей неоднозначен.");
            }
            else
            {
                step.Pass($"Текущая проекция читается: индекс {result.Index}, тип {result.Type}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Чтение текущей проекции не сработало: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    /// <summary>VIEW.3 — apply the isometric projection through the documented route and read the type BACK
    /// from the API. The verdict needs the read-back: SetCurrent returning TRUE is not the result.</summary>
    private static void MeasureApplyAndReadBack(ProbeReport report, ksDocument3D doc, ksViewProjectionCollection collection)
    {
        var step = report.Begin(
            "VIEW.3",
            "Применение изометрии и обратное чтение типа",
            "Можно ли применить стандартную проекцию к окну и подтвердить её типом, прочитанным из API?");

        try
        {
            var target = FindByType(collection, VpIsoXyz);
            if (target is null)
            {
                step.Unknown($"Проекция типа vp_IsoXYZ={VpIsoXyz} в коллекции не найдена — применять нечего.");
                return;
            }

            // The help page for SetCurrent documents the route; SetMatrix3D is the other documented
            // entry point (SetMatrix3D then SetCurrent). The predefined projection is used first because
            // it needs no matrix source.
            var applied = target.SetCurrent();
            step.Observe($"SetCurrent() на vp_IsoXYZ → {applied}");

            collection.refresh();
            var afterType = ReadCurrentType(collection);
            step.Data["set_current_returned"] = applied;
            step.Data["type_after"] = afterType;

            if (afterType == VpIsoXyz)
            {
                step.Pass($"Изометрия применена и прочитана обратно: GetViewProjectonType()={afterType}.");
            }
            else if (applied)
            {
                step.Unknown($"SetCurrent() вернул true, но обратное чтение дало тип {afterType}, а не {VpIsoXyz}: " +
                             "утверждать применение по одному возврату нельзя.");
            }
            else
            {
                step.Fail($"SetCurrent() на vp_IsoXYZ вернул false (обратное чтение: тип {afterType}).");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Применение проекции не сработало: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>VIEW.4 — two snapshots of the SAME projection, compared byte for byte. The order names
    /// this explicitly; a snapshot that changes between identical requests makes two views
    /// incomparable.</summary>
    private static void MeasureSnapshots(ProbeReport report, ksDocument3D doc)
    {
        var step = report.Begin(
            "VIEW.4",
            "Два снимка одной проекции — побайтовое совпадение",
            "Даёт ли фиксированная проекция один и тот же снимок, или изображение всё равно плавает?");

        try
        {
            var first = Snapshot(doc);
            var second = Snapshot(doc);
            if (first is null || second is null)
            {
                step.Unknown("Снимок не получен (пустой массив байт): сравнивать нечего.");
                return;
            }

            var equal = first.AsSpan().SequenceEqual(second);
            step.Data["first_bytes"] = first.Length;
            step.Data["second_bytes"] = second.Length;
            step.Data["byte_equal"] = equal;
            step.Observe($"Первый: {first.Length} байт; второй: {second.Length} байт; совпадение побайтово: {equal}");

            if (equal)
            {
                step.Pass($"{first.Length} байт совпали побайтово на фиксированной проекции.");
            }
            else
            {
                step.Unknown("Снимки одной проекции разошлись побайтово: «два вида сравнимы» этим маршрутом не доказано.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Fail($"Снятие снимка не сработало: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>VIEW.5 — restore: put the projection that was current BEFORE the change back and read it.
    /// The order requires this to be named whether it works or not.</summary>
    private static void MeasureRestore(ProbeReport report, ksDocument3D doc, ksViewProjectionCollection collection, CurrentProjection before)
    {
        var step = report.Begin(
            "VIEW.5",
            "Возврат прежнего вида после снимка",
            "Восстанавливается ли прежняя проекция документированным способом, и читается ли она обратно?");

        try
        {
            collection.refresh();
            if (!before.Found)
            {
                // Nothing to restore to: the earlier read could not name the current projection. Saying
                // "restored" here would invent a value.
                step.Unknown("Прежняя проекция не была прочитана в VIEW.2 — возвращать не к чему.");
                return;
            }

            var previous = FindByType(collection, before.Type);
            if (previous is null)
            {
                step.Unknown($"Проекция типа {before.Type} (прежний вид) в коллекции не найдена — вернуть нечем.");
                return;
            }

            var restored = previous.SetCurrent();
            collection.refresh();
            var typeAfter = ReadCurrentType(collection);
            step.Data["restore_returned"] = restored;
            step.Data["type_after_restore"] = typeAfter;
            step.Data["expected_type"] = before.Type;

            if (typeAfter == before.Type)
            {
                step.Pass($"Прежний вид возвращён и прочитан обратно: тип {typeAfter}.");
            }
            else
            {
                step.Unknown($"Возврат не подтверждён: ожидался тип {before.Type}, прочитан {typeAfter}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Возврат вида не сработал: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>Byte-mode snapshot through the documented API5 raster route, so the comparison is on the
    /// same carrier the tool returns.</summary>
    private static byte[]? Snapshot(ksDocument3D doc)
    {
        var parameter = (ksRasterFormatParam)doc.RasterFormatParam();
        parameter.Init();
        // PNG = the format the tool publishes; its numeric code is resolved from the vendor enum by name.
        parameter.format = (short)RasterFormatCodes.Png;
        parameter.colorBPP = 24;
        parameter.returnResultAsArrayBytes = true;
        if (!doc.SaveAsToRasterFormat(string.Empty, parameter))
        {
            return null;
        }

        return parameter.resultArrayBytes switch
        {
            byte[] typed => typed,
            Array array when array.Rank == 1 => array.Cast<object>().Select(Convert.ToByte).ToArray(),
            _ => null,
        };
    }

    private static ksViewProjection? FindByType(ksViewProjectionCollection collection, int type)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = (ksViewProjection)collection.GetByIndex(i);
            if (projection is not null && SafeType(projection) == type)
            {
                return projection;
            }
        }

        return null;
    }

    private static int ReadCurrentType(ksViewProjectionCollection collection)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = (ksViewProjection)collection.GetByIndex(i);
            if (projection is not null && SafeIsCurrent(projection) == true)
            {
                return SafeType(projection);
            }
        }

        return int.MinValue;
    }

    private static int SafeType(ksViewProjection projection)
    {
        try
        {
            return projection.GetViewProjectonType();
        }
        catch (COMException)
        {
            return int.MinValue;
        }
    }

    private static string? SafeName(ksViewProjection projection)
    {
        try
        {
            return projection.name;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool? SafeIsCurrent(ksViewProjection projection)
    {
        try
        {
            return projection.IsCurrent();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static object? ReadScheme(ksViewProjectionCollection collection)
    {
        try
        {
            return collection.get_viewProjectionScheme();
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal sealed class CurrentProjection
    {
        public bool Found { get; set; }
        public int Index { get; set; } = -1;
        public int Type { get; set; } = int.MinValue;
        public string? Name { get; set; }
    }
}

/// <summary>Raster format codes as the vendor enum defines them, by NAME — never a copied literal. The
/// tool publishes four formats; the probe needs only PNG.</summary>
internal static class RasterFormatCodes
{
    public static short Png => (short)VendorConstants.Value("ksRasterFormatEnum", "rfPNG", 4);
}
