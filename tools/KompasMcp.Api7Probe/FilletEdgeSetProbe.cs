using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба H — правка НАБОРА РЁБЕР существующего скругления (SM-09.fillet.change_edge_set).
/// </summary>
/// <remarks>
/// <para>
/// Основание: <c>SM-09.fillet.change_edge_set</c> стояла на уровне <c>metadata_found</c> с прямой
/// пометкой «<c>array()</c> доступен; поведение при сокращении/расширении набора не измерялось».
/// То есть <b>доступность метода уже известна, а применение — нет</b>, и именно это надо измерить.
/// Ненулевой объект и <c>true</c> от <c>Add()</c>/<c>Clear()</c>/<c>Update()</c> доказательством не
/// считаются (ADR-003 §3). Эталон — объём и число граней, а не ответ вызова.
/// </para>
/// <para>
/// <b>Аналитика.</b> Пластина 100×80×10 (V₀ = 80000), четыре вертикальных угловых ребра длиной 10 мм.
/// Одно ребро скругления радиуса r снимает <c>(1 − π/4)·r²·h</c>. Отсюда ровно читаются четыре
/// состояния, и ни одно из них не спутать с другим:
/// </para>
/// <list type="bullet">
/// <item>4 ребра, r=3 → 80000 − 4·(1−π/4)·9·10 = <b>79922.74333882307</b>;</item>
/// <item>2 ребра, r=3 → 80000 − 2·(1−π/4)·9·10 = <b>79961.37166941154</b> (СОКРАЩЕНИЕ набора);</item>
/// <item>4 ребра, r=5 → 80000 − 4·(1−π/4)·25·10 = <b>79785.39816339745</b> (правка радиуса — контроль);</item>
/// <item>4 ребра, r=3 и одно НОВОЕ ребро вне исходной четвёрки → +1·(1−π/4)·9·10 (РАСШИРЕНИЕ).</item>
/// </list>
/// <para>
/// <b>Почему это отдельная проба, а не расширение пробы F.</b> У фаски правка набора не
/// рассматривалась вовсе, а у скругления уже известно, что запись в определение API5 на
/// СУЩЕСТВУЮЩЕМ признаке принимается и геометрически игнорируется (измерено FL04r: сеттер успешен,
/// <c>entity.Update()</c> = true, определение перечитывает число, объём прежний). Значит нельзя
/// предполагать, что <c>array()</c> ведёт себя иначе: это надо проверить отдельно, и именно
/// <c>array()</c> — первым, потому что он и есть «найденный маршрут».
/// </para>
/// <para>
/// <b>Порядок — часть измерения.</b> Каждое вмешательство идёт на СВОЁМ документе: сценарий
/// сокращения меняет набор необратимо для последующих шагов, и на одном документе они бы мешали
/// друг другу, то есть мерили бы не то, что заявляют.
/// </para>
/// </remarks>
internal sealed class FilletEdgeSetProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    private const double Radius3 = 3d;
    private const double Radius5 = 5d;

    private const short Sketch = 5;
    private const short PlaneXoy = 1;
    private const short Fillet = 34;
    private const short OperationElement = 110;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private IApplication? _app7;

    public FilletEdgeSetProbe(ProbeReport report, Options options)
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
            // Решающий вопрос идёт первым и на своём документе: применяется ли сокращение набора
            // через array() — тот маршрут, который «найден». Если он не применяется, весь
            // дальнейший разбор API7 имеет смысл только как поиск обходного пути.
            var api5Shrink = Api5ShrinkViaArray();
            if (api5Shrink is not null)
            {
                Api5GrowViaArray(api5Shrink);
                Api5ReopenAfterShrink(api5Shrink);
            }

            var bridge = Bridge();
            if (bridge is not null)
            {
                Api7ShrinkViaBaseObjects();
            }
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("H.X", "Необработанное исключение пробы H");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Finish();
        }
    }

    // ═══════════════════════════════════════════════════════════ решающие измерения ══

    private Plate? Api5ShrinkViaArray()
    {
        var step = _report.Begin(
            "H.1",
            "API5: сокращение набора рёбер через ksFilletDefinition.array()",
            "Применяется ли DetachByIndex/Clear на СУЩЕСТВУЮЩЕМ признаке, или его ждёт судьба радиуса?");

        var plate = NewPlate(step, "H.1", out var cornerEdges);
        if (plate is null)
        {
            return null;
        }

        if (cornerEdges.Count != 4)
        {
            step.Fail($"Угловых рёбер {cornerEdges.Count}, а нужно ровно 4 — эталон не годится.");
            Close(plate.Doc);
            return null;
        }

        var fillet = CreateFillet(step, plate, cornerEdges, Radius3);
        if (fillet is null)
        {
            Close(plate.Doc);
            return null;
        }

        var beforeVolume = ReadVolume(plate.Part);
        var beforeFaces = FaceCount(plate.Part);
        step.Data["volume_before"] = Num(beforeVolume);
        step.Data["faces_before"] = beforeFaces;

        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        if (beforeVolume is not double bv || Math.Abs(bv - expected4) > Tolerance(expected4))
        {
            step.Fail($"Исходное скругление не совпало с аналитикой: {Num(beforeVolume)} против {expected4}.");
            Close(plate.Doc);
            return null;
        }

        // Сокращаем набор: 4 ребра → 2. Именно это и есть «изменение набора».
        var kept = cornerEdges.Take(2).ToList();
        var outcome = WriteEdgeSet(step, plate, fillet, kept);
        step.Data["array_clear"] = outcome.Cleared;
        step.Data["array_added"] = outcome.Added;
        step.Data["array_add_ok"] = outcome.AddOk;
        step.Data["array_count_after_write"] = ReadArrayCount(fillet);

        plate.Doc.RebuildDocument();
        var afterVolume = ReadVolume(plate.Part);
        var afterFaces = FaceCount(plate.Part);
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["volume_after"] = Num(afterVolume);
        step.Data["expected_shrunk"] = Num(expected2);
        step.Data["expected_unchanged"] = Num(expected4);
        step.Data["faces_after"] = afterFaces;

        var applied = afterVolume is double av && Math.Abs(av - expected2) <= Tolerance(expected2);
        var ignored = afterVolume is double iv && Math.Abs(iv - expected4) <= Tolerance(expected4);
        step.Observe($"Объём: было {Num(beforeVolume)} → стало {Num(afterVolume)}; " +
                     $"сокращение дало бы {Num(expected2)}, игнорирование оставило бы {Num(expected4)}.");

        if (applied)
        {
            step.Pass("Сокращение набора через array() ПРИМЕНЯЕТСЯ: объём перешёл на эталон двух рёбер.");
        }
        else if (ignored)
        {
            step.Fail("Сокращение набора через array() НЕ применяется: объём остался на четырёх рёбрах, " +
                      "как у записи радиуса через определение (FL04r).");
        }
        else
        {
            step.Fail($"Объём не совпал ни с применённым сокращением ({Num(expected2)}), " +
                      $"ни с игнорированием ({Num(expected4)}): {Num(afterVolume)}.");
        }

        return plate;
    }

    private void Api5GrowViaArray(Plate plate)
    {
        var step = _report.Begin(
            "H.2",
            "API5: расширение набора рёбер (добавить ребро, которого в наборе не было)",
            "Принимает ли array() новое ребро и появляется ли новая скруглённая грань?");

        // Пересоздаём чистое состояние на новом документе: предыдущий шаг необратимо сократил набор.
        var fresh = NewPlate(step, "H.2", out var cornerEdges);
        if (fresh is null)
        {
            return;
        }

        if (cornerEdges.Count < 4)
        {
            step.Fail($"Угловых рёбер {cornerEdges.Count} — расширять нечем.");
            Close(fresh.Doc);
            return;
        }

        var fillet = CreateFillet(step, fresh, cornerEdges.Take(2).ToList(), Radius3);
        if (fillet is null)
        {
            Close(fresh.Doc);
            return;
        }

        var beforeVolume = ReadVolume(fresh.Part);
        var beforeFaces = FaceCount(fresh.Part);
        step.Data["volume_before"] = Num(beforeVolume);
        step.Data["faces_before"] = beforeFaces;

        // Расширяем: два ребра → четыре.
        var outcome = WriteEdgeSet(step, fresh, fillet, cornerEdges);
        step.Data["array_clear"] = outcome.Cleared;
        step.Data["array_added"] = outcome.Added;
        step.Data["array_add_ok"] = outcome.AddOk;

        fresh.Doc.RebuildDocument();
        var afterVolume = ReadVolume(fresh.Part);
        var afterFaces = FaceCount(fresh.Part);
        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["volume_after"] = Num(afterVolume);
        step.Data["expected_grown"] = Num(expected4);
        step.Data["expected_unchanged"] = Num(expected2);
        step.Data["faces_after"] = afterFaces;

        if (afterVolume is double av && Math.Abs(av - expected4) <= Tolerance(expected4))
        {
            step.Pass("Расширение набора через array() ПРИМЕНЯЕТСЯ: объём перешёл на эталон четырёх рёбер.");
        }
        else if (afterVolume is double iv && Math.Abs(iv - expected2) <= Tolerance(expected2))
        {
            step.Fail("Расширение набора через array() НЕ применяется: объём остался на двух рёбрах.");
        }
        else
        {
            step.Fail($"Объём не совпал ни с расширением ({Num(expected4)}), ни с игнорированием " +
                      $"({Num(expected2)}): {Num(afterVolume)}.");
        }

        Close(fresh.Doc);
    }

    private void Api5ReopenAfterShrink(Plate plate)
    {
        var step = _report.Begin(
            "H.3",
            "API5: переживает ли изменённый набор save→close→reopen",
            "Остаётся ли набор тем, что записали, или КОМПАС восстанавливает исходный?");

        // Сокращаем, сохраняем, закрываем, открываем и ЧИТАЕМ набор заново.
        var fresh = NewPlate(step, "H.3", out var cornerEdges);
        if (fresh is null)
        {
            return;
        }

        var fillet = CreateFillet(step, fresh, cornerEdges, Radius3);
        if (fillet is null)
        {
            Close(fresh.Doc);
            return;
        }

        var beforeVolume = ReadVolume(fresh.Part);
        WriteEdgeSet(step, fresh, fillet, cornerEdges.Take(2).ToList());
        fresh.Doc.RebuildDocument();
        var afterShrink = ReadVolume(fresh.Part);
        step.Data["volume_before"] = Num(beforeVolume);
        step.Data["volume_after_shrink"] = Num(afterShrink);

        var path = Path.Combine(_options.WorkDir, "H-edgeset-reopen.m3d");
        var saved = SaveAs(fresh.Doc, path);
        step.Data["saved"] = saved;
        Close(fresh.Doc);
        if (!saved)
        {
            step.Fail("Документ не сохранён — reopen нечего проверять.");
            return;
        }

        var reopened = (ksDocument3D)_app.Document3D();
        if (reopened.Open(path, true) != true)
        {
            step.Fail("Документ не открылся.");
            return;
        }

        reopened.RebuildDocument();
        var part = (ksPart)reopened.GetPart(-1);
        var volume = ReadVolume(part);
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["volume_after_reopen"] = Num(volume);
        step.Data["expected_shrunk"] = Num(expected2);
        step.Data["expected_original"] = Num(expected4);

        if (volume is double v && Math.Abs(v - expected2) <= Tolerance(expected2))
        {
            step.Pass("Изменённый набор пережил reopen: объём остался на двух рёбрах.");
        }
        else if (volume is double v2 && Math.Abs(v2 - expected4) <= Tolerance(expected4))
        {
            step.Fail("После reopen КОМПАС вернул исходные четыре ребра — правка не сохранилась.");
        }
        else
        {
            step.Fail($"После reopen объём {Num(volume)} не совпал ни с двумя ({Num(expected2)}), " +
                      $"ни с четырьмя ({Num(expected4)}).");
        }

        Close(reopened);
    }

    private Plate? Api7ShrinkViaBaseObjects()
    {
        var step = _report.Begin(
            "H.4",
            "API7: сокращение набора через IFillet.BaseObjects",
            "Применяется ли запись BaseObjects к живому признаку — обходной путь, как с радиусом?");

        var plate = NewPlate(step, "H.4", out var cornerEdges);
        if (plate is null)
        {
            return null;
        }

        var fillet = CreateFillet(step, plate, cornerEdges, Radius3);
        if (fillet is null)
        {
            Close(plate.Doc);
            return null;
        }

        var container = Container(step, plate.Doc);
        if (container is null)
        {
            Close(plate.Doc);
            return null;
        }

        var beforeVolume = ReadVolume(plate.Part);
        step.Data["volume_before"] = Num(beforeVolume);
        step.Data["fillets_count"] = container.Fillets.Count;
        if (container.Fillets.Count != 1)
        {
            step.Fail($"Скруглений в контейнере {container.Fillets.Count}, ожидалось 1 — сопоставление неоднозначно.");
            Close(plate.Doc);
            return null;
        }

        if (container.Fillets[0] is not IFillet live)
        {
            step.Fail("Элемент Fillets[0] не отдаёт IFillet.");
            Close(plate.Doc);
            return null;
        }

        step.Data["base_objects_before"] = live.BaseObjects is object[] bo ? bo.Length : (object?)null;

        // Передаём в BaseObjects ДВА ребра вместо четырёх — прямо, типизированно.
        var two = ToApi7(step, cornerEdges.Take(2).ToList());
        if (two is not object[] array)
        {
            step.Unknown("Рёбра не перенеслись в API7 — BaseObjects записать нечем.");
            Close(plate.Doc);
            return null;
        }

        try
        {
            live.BaseObjects = array;
            step.Data["base_objects_set"] = "ok";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            step.Data["base_objects_set"] = HResult.Describe(ex);
            step.Unknown("Запись IFillet.BaseObjects отвергнута: " + ex.Message);
            Close(plate.Doc);
            return null;
        }

        var updated = Applied(() => live.Update());
        step.Data["update"] = updated;
        Rebuild(container, plate);

        var afterVolume = ReadVolume(plate.Part);
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["volume_after"] = Num(afterVolume);
        step.Data["expected_shrunk"] = Num(expected2);
        step.Data["expected_unchanged"] = Num(expected4);

        if (afterVolume is double av && Math.Abs(av - expected2) <= Tolerance(expected2))
        {
            step.Pass("Запись IFillet.BaseObjects ПРИМЕНЯЕТСЯ: объём перешёл на эталон двух рёбер.");
        }
        else if (afterVolume is double iv && Math.Abs(iv - expected4) <= Tolerance(expected4))
        {
            step.Fail("Запись IFillet.BaseObjects НЕ применяется: объём остался на четырёх рёбрах.");
        }
        else
        {
            step.Fail($"Объём {Num(afterVolume)} не совпал ни с двумя ({Num(expected2)}), ни с четырьмя ({Num(expected4)}).");
        }

        Close(plate.Doc);
        return plate;
    }

    // ═══════════════════════════════════════════════════════════════════ механика ══

    /// <summary>
    /// Записать набор рёбер в СУЩЕСТВУЮЩЕЕ скругление через определение API5:
    /// <c>array().Clear()</c>, затем <c>Add()</c> по каждому ребру, затем <c>Update()</c>.
    /// Возвращаются отдельные факты, а не «получилось»: вызывающий обязан различить, что именно
    /// ответило <c>true</c>, потому что при радиусе все ответы были <c>true</c> при неизменной
    /// геометрии.
    /// </summary>
    private static (bool Cleared, int Added, bool AddOk, string? Route) WriteEdgeSet(
        ProbeStep step, Plate plate, ksEntity fillet, IReadOnlyList<ksEntity> edges)
    {
        if ((fillet.GetDefinition() as ksFilletDefinition)?.array() is not ksEntityCollection array)
        {
            step.Observe("Переопределение не отдало ksEntityCollection — писать некуда.");
            return (false, 0, false, null);
        }

        var cleared = Applied(() => array.Clear());
        var added = 0;
        foreach (var edge in edges)
        {
            if (Applied(() => array.Add(edge)))
            {
                added++;
            }
        }

        var updated = Applied(() => fillet.Update());
        step.Data["definition_update"] = updated;
        return (cleared, added, added == edges.Count, "ksFilletDefinition.array()");
    }

    private static int? ReadArrayCount(ksEntity fillet)
    {
        try
        {
            return (fillet.GetDefinition() as ksFilletDefinition)?.array() is ksEntityCollection c
                ? c.GetCount()
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static ksEntity? CreateFillet(ProbeStep step, Plate plate, IReadOnlyList<ksEntity> edges, double radius)
    {
        var part = plate.Part;
        var feature = (ksEntity)part.NewEntity(Fillet);
        if (feature.GetDefinition() is not ksFilletDefinition definition)
        {
            step.Fail("Определение скругления не отдало ksFilletDefinition.");
            return null;
        }

        definition.radius = radius;
        definition.tangent = false;
        if (definition.array() is not ksEntityCollection array)
        {
            step.Fail("array() не отдал ksEntityCollection.");
            return null;
        }

        var added = 0;
        foreach (var edge in edges)
        {
            if (array.Add(edge))
            {
                added++;
            }
        }

        if (added != edges.Count || !feature.Create())
        {
            step.Fail($"Скругление не создано: рёбер принято {added} из {edges.Count}, Create=false.");
            return null;
        }

        plate.Doc.RebuildDocument();
        return feature;
    }

    private Plate? NewPlate(ProbeStep parent, string name, out List<ksEntity> cornerEdges)
    {
        cornerEdges = new List<ksEntity>();
        var step = _report.Begin(parent.Id + ":plate", "Пластина 100×80×10 для «" + name + "»", null);
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, name) is null)
            {
                step.Fail("Пластина не построена.");
                return null;
            }

            doc.RebuildDocument();
            var volume = Api5.Volume(part);
            if (volume is not double v || Math.Abs(v - PlateVolume) > Tolerance(PlateVolume))
            {
                step.Fail($"Базовый объём не 80000: {Num(volume)} — эталон не годится.");
                return null;
            }

            cornerEdges.AddRange(VerticalCornerEdges(part, step));
            step.Data["volume"] = Num(volume);
            step.Data["corner_edges"] = cornerEdges.Count;
            step.Pass("Пластина готова: V=80000, вертикальных угловых рёбер " + cornerEdges.Count + ".");
            return new Plate { Name = name, Doc = doc, Part = part, StartedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Пластина не построена: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    /// <summary>
    /// Четыре вертикальных угловых ребра конечного тела — тот же критерий отбора, что в пробе P2.2
    /// и F.2: из <c>GetMainBody() → FaceCollection → EdgeCollection</c>, а не из
    /// <c>EntityCollection(o3d_edge)</c>, где лежат и эскизные контуры.
    /// </summary>
    private static List<ksEntity> VerticalCornerEdges(ksPart part, ProbeStep step)
    {
        var chosen = new List<ksEntity>();
        var seen = new HashSet<IntPtr>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("Тело или коллекция граней недоступны — рёбер не видно.");
            return chosen;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not object faceObject)
            {
                continue;
            }

            var face = faceObject as ksFaceDefinition
                       ?? ((faceObject as ksEntity)?.GetDefinition() as ksFaceDefinition);
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

                if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1
                    || !v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                {
                    continue;
                }

                var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                var corner = Math.Abs(Math.Abs(ax) - PlateWidth / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - PlateHeight / 2d) < 1e-3;
                var spans = Math.Abs(Math.Max(az, bz) - PlateThickness) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                if (!(vertical && corner && spans))
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(entity);
                try
                {
                    if (seen.Add(pointer))
                    {
                        chosen.Add(entity);
                    }
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }

        return chosen;
    }

    private object? ToApi7(ProbeStep step, IReadOnlyList<ksEntity> edges)
    {
        var list = new List<object>(edges.Count);
        foreach (var edge in edges)
        {
            object? transferred;
            try
            {
                transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            }
            catch (Exception ex)
            {
                step.Data["transfer_edge"] = HResult.Describe(ex);
                return null;
            }

            if (transferred is null)
            {
                step.Data["transfer_edge"] = "null";
                return null;
            }

            list.Add(transferred);
        }

        return list.ToArray();
    }

    private IModelContainer? Container(ProbeStep step, ksDocument3D doc)
    {
        try
        {
            var document7 = _app.TransferInterface(doc, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            step.Data["qi_imodelcontainer"] = container is not null;
            if (container is null)
            {
                step.Unknown("Документ/TopPart не дали IModelContainer.");
            }

            return container;
        }
        catch (Exception ex)
        {
            step.Data["qi_imodelcontainer"] = "исключение: " + ex.Message;
            return null;
        }
    }

    private static void Rebuild(IModelContainer container, Plate plate)
    {
        if (container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        plate.Doc.RebuildDocument();
    }

    private ProbeStep? Bridge()
    {
        var step = _report.Begin("H.0", "Мост API5→API7", "Тот же сеанс отдаёт IApplication?");
        try
        {
            _app7 = _app.GetType().InvokeMember(
                "ksGetApplication7", System.Reflection.BindingFlags.InvokeMethod, null, _app, null) as IApplication;
            step.Data["app7"] = _app7 is not null;
            if (_app7 is null)
            {
                step.Fail("ksGetApplication7 не отдал IApplication.");
                return null;
            }

            step.Pass("Мост построен.");
            return step;
        }
        catch (Exception ex)
        {
            step.Data["app7_error"] = ex.Message;
            step.Fail("ksGetApplication7 бросил: " + ex.Message);
            return null;
        }
    }

    private void Launch()
    {
        var step = _report.Begin("H.1:app", "Свой невидимый экземпляр", null);
        try
        {
            dynamic? raw = Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!);
            _app = (KompasObject)raw!;
            _app.Visible = false;
            step.Data["pid"] = Environment.ProcessId;
            step.Pass("Экземпляр поднят невидимо.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Экземпляр не поднят: " + ex.Message);
        }
    }

    private void Finish()
    {
        var step = _report.Begin("H.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app?.Quit();
            step.Data["quit"] = true;
            step.Pass("Сеанс завершён.");
        }
        catch (Exception ex)
        {
            step.Data["quit"] = ex.Message;
            step.Unknown("Quit бросил: " + ex.Message);
        }
    }

    private bool SaveAs(ksDocument3D doc, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return doc.SaveAs(path) == true;
        }
        catch (COMException)
        {
            return false;
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
            // Закрытие после падения шага — уборка, а не измерение: осиротевший документ заметен
            // на шаге H.Z.
        }
    }

    private static double? ReadVolume(ksPart part) => Api5.Volume(part);

    private static int FaceCount(ksPart part) => Api5.FaceCount(part);

    /// <summary>
    /// Приводит тройственное состояние к «применилось / не применилось». Отличие от
    /// <c>Api5.SafeBool</c> здесь принципиально: тот возвращает <c>null</c> на исключении, и
    /// вызывающий обязан решить, что значит «не спросили». Для записи в модель «не спросили» —
    /// это НЕ «записали», поэтому по умолчанию возвращается <c>false</c>.
    /// </summary>
    private static bool Applied(Func<bool?> call)
    {
        try
        {
            return call() == true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static string Num(double? value) =>
        value?.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private sealed class Plate
    {
        public required string Name { get; init; }

        public required ksDocument3D Doc { get; init; }

        public required ksPart Part { get; init; }

        public required DateTime StartedUtc { get; init; }
    }
}
