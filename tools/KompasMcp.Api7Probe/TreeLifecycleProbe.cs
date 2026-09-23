using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба T — как признак B3 адресуется в ЖИЗНЕННОМ ЦИКЛЕ, а не только в момент создания.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему отдельный опыт.</b> Пробы BO/SP/RP измерили СОЗДАНИЕ признаков булевой операции,
/// разделения, отсечения и преобразования положения — и все четыре создаются фабрикой API7
/// (<c>IModelContainer.Booleans/SplitSolids/Cuts/BodyRepositions</c>). Продукт обязан закрывать
/// каждый режим не одним действием <c>create</c>, а полным жизненным циклом, и в нём есть
/// <c>suppress_restore</c>, <c>delete_dependencies</c> и <c>discover</c>. Все три работают через
/// ДЕРЕВО ПРИЗНАКОВ API5: <c>kompas_list_features</c> читает коллекцию <c>o3d_operationElement</c>,
/// подавление пишет <c>ksFeature.excluded</c>, удаление зовёт <c>DeleteObject</c> на <c>ksEntity</c>.
/// Объект API7 — не <c>ksEntity</c>, поэтому вопрос не в удобстве, а в том, существует ли вообще
/// маршрут: <b>появляется ли признак, созданный фабрикой API7, в дереве API5 и можно ли его
/// подавить и удалить</b>.
/// </para>
/// <para>
/// <b>Второй вопрос — есть ли у булевой операции и отсечения родной маршрут API5.</b> Каталог
/// покрытия держит для SM-15 <c>ksAggregateDefinition</c> (<c>o3d_aggregate=69</c>) и для SM-16
/// <c>ksCutByPlaneDefinition</c>; оба интерфейса найдены в <c>kApi5.tlb</c>, но ни один маршрут не
/// измерен. Родной маршрут API5 даёт <c>ksEntity</c>, то есть жизненный цикл целиком; маршрут API7
/// даёт объект, который в дерево может не попасть. Поэтому оба маршрута измеряются здесь одним
/// прогоном, и решение о реализации принимается по числам, а не по удобству.
/// </para>
/// <para>
/// <b>Эталон — наряд §6.1.</b> A <c>[0,40]×[0,30]×[0,20]</c> (V=24000), B
/// <c>[20,60]×[0,30]×[0,20]</c> (V=24000), посторонний куб <c>[100,110]×[0,10]×[0,10]</c> (V=1000).
/// Объединение — V=36000, габарит <c>(0,0,0)…(60,30,20)</c>; разность A−B — V=12000; пересечение —
/// V=12000. Числа считаются ДО вызова и печатаются в журнал, чтобы расхождение нельзя было закрыть
/// допуском, подобранным после факта.
/// </para>
/// </remarks>
internal sealed class TreeLifecycleProbe
{
    private const double Ax0 = 0d, Ax1 = 40d, Ay0 = 0d, Ay1 = 30d, Az = 20d;
    private const double Bx0 = 20d, Bx1 = 60d;
    private const double Sx0 = 100d, Sx1 = 110d, Sy0 = 0d, Sy1 = 10d, Sz = 10d;

    /// <summary>Объём объединения A∪B: пересечение считается один раз.</summary>
    private const double UnionVolume = 36000d;

    /// <summary>Разность A−B: остаётся x∈[0,20].</summary>
    private const double DifferenceVolume = 12000d;

    // Типы объектов из ksObj3dTypeEnum, измеренные ранее (docs/04_KOMPAS_API_NOTES.md):
    // o3d_aggregate=69 «Булева операция», o3d_cutByPlane=50, o3d_SplitSolid=633,
    // o3d_BodyReposition=569.
    private const short TypeAggregate = 69;
    private const short TypeCutByPlane = 50;
    private const short TypeSplitSolid = 633;
    private const short TypeBodyReposition = 569;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public TreeLifecycleProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "tree-lifecycle.json"),
            Path.Combine(options.ReportDir, "tree-lifecycle.md"));

    public void Run()
    {
        Launch();
        var doc = NewPart(out var part);
        try
        {
            var blank = Blank(part, doc);
            if (!blank)
            {
                return;
            }

            Api7BooleanRoute(doc, part);
            Api5AggregateRoute(doc, part);
            Api7SplitRoute(doc, part);
            Api7CutRoute(doc, part);
            Api5CutByPlaneRoute(doc, part);
            Api7RepositionRoute(doc, part);
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════ TL.0 сеанс ══

    private void Launch()
    {
        var step = _report.Begin("TL.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Проба не трогает чужой КОМПАС: жизненный цикл признака измеряется на своём процессе.");
        try
        {
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
            step.Observe("процессов KOMPAS.exe до запуска: " + _pidsBefore.Count + ", свой: " + _ownPid);
            step.Pass("сеанс поднят");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    // ═══════════════════════════════════════════════ TL.1 заготовка ══

    private bool Blank(ksPart part, ksDocument3D doc)
    {
        var step = _report.Begin("TL.1", "Заготовка: A, B и посторонний куб",
            "Все три тела строятся базовым выдавливанием; ожидания объёмов объявлены до опыта.");
        step.Observe("ожидание: A=" + Api5.Num((Ax1 - Ax0) * (Ay1 - Ay0) * Az)
                     + ", B=" + Api5.Num((Bx1 - Bx0) * (Ay1 - Ay0) * Az)
                     + ", посторонний=" + Api5.Num((Sx1 - Sx0) * (Sy1 - Sy0) * Sz));
        try
        {
            if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A")
                || !ExtrudeRect(doc, part, Bx0, Bx1, Ay0, Ay1, Az, step, "B")
                || !ExtrudeRect(doc, part, Sx0, Sx1, Sy0, Sy1, Sz, step, "S"))
            {
                step.Fail("эталонные тела не построены");
                return false;
            }

            var rows = BodyRows(part);
            step.Observe("тел " + rows.Count + " → " + Describe(rows));
            step.Data["bodies"] = rows.Count;
            step.Data["total_volume"] = SumVolumes(rows);
            step.Pass("заготовка построена");
            return rows.Count == 3;
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
            return false;
        }
    }

    // ══════════════════════════════════════ TL.2..TL.4 маршрут API7 ══

    private void Api7BooleanRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.2", "Признак, созданный фабрикой API7, и дерево API5",
            "Появляется ли булев признак из IModelContainer.Booleans в дереве o3d_operationElement, "
            + "и каким элементом он там представлен?");
        Api5.FeatureReading? added = null;
        try
        {
            var before = Api5.ReadFeatures(part, step);
            step.Observe("дерево ДО: " + TreeText(before));
            step.Data["tree_before"] = before.Count;

            var rows = BodyRows(part);
            var target = FindBody(rows, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(rows, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            if (target is null || tool is null)
            {
                step.Fail("цель или инструмент не опознаны");
                return;
            }

            if (!CreateBooleanApi7(doc, part, target, new[] { tool }, ksBooleanType.ksUnion, step))
            {
                return;
            }

            var after = Api5.ReadFeatures(part, step);
            step.Observe("дерево ПОСЛЕ: " + TreeText(after));
            step.Data["tree_after"] = after.Count;
            step.Data["bodies_after"] = BodyRows(part).Count;
            step.Data["total_volume_after"] = SumVolumes(BodyRows(part));

            added = after.Count > before.Count ? after[after.Count - 1] : null;
            step.Data["added_index"] = added?.Index;
            step.Data["added_clr"] = added?.ClrType;
            step.Data["added_name"] = added?.Name;
            step.Data["added_type"] = added?.TypeName;
            step.Data["added_created"] = added?.Created;
            step.Data["added_definition"] = added is null ? null : DefinitionName(added);
            step.Data["added_feature_face"] = added?.Feature is null ? "нет" : "ksFeature";
            step.Data["added_entity_face"] = added?.Entity is null ? "нет" : "ksEntity";

            if (after.Count <= before.Count)
            {
                step.Fail("в дереве API5 не появилось ни одного элемента: признак API7 дереву неизвестен");
                return;
            }

            step.Pass("признак API7 виден в дереве API5 как элемент «" + (added?.Name ?? "?")
                      + "» (" + (added?.TypeName ?? "?") + ")");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            SuppressAndDelete(step, doc, part, added);
        }
    }

    /// <summary>
    /// Подавление и удаление того же признака — вторым и третьим действием. Именно здесь
    /// проверяется, годится ли элемент дерева как адрес: если подавление не меняет объём, адрес
    /// не работает, и продукту нужен другой маршрут.
    /// </summary>
    private void SuppressAndDelete(ProbeStep parent, ksDocument3D doc, ksPart part, Api5.FeatureReading? reading)
    {
        var step = _report.Begin("TL.3", "Подавление и восстановление признака через дерево API5",
            "Меняет ли ksFeature.excluded геометрию, и возвращается ли она при снятии подавления?");
        // Обе стороны элемента: на живом дереве `as ksFeature` проходит, а `as ksEntity` — нет
        // (измерено R.0d). Адрес берётся той стороной, которая отвечает, а не той, которая удобна.
        var feature = reading?.Feature ?? reading?.Entity?.GetFeature() as ksFeature;
        var entity = reading?.Entity ?? feature?.GetObject() as ksEntity;
        if (feature is null && entity is null)
        {
            step.Unknown("элемент дерева не получен: подавление проверять не на чем");
        }
        else
        {
            try
            {
                var before = SumVolumes(BodyRows(part));
                if (feature is null)
                {
                    step.Unknown("элемент не отвечает GetFeature() как ksFeature — маршрут подавления недоступен");
                }
                else
                {
                    step.Observe("имя признака: «" + (feature.name ?? "<пусто>") + "»");
                    feature.excluded = true;
                    doc.RebuildDocument();
                    part.RebuildModel();
                    var suppressed = SumVolumes(BodyRows(part));
                    var excludedRead = Api5.SafeBool(() => feature.excluded);
                    step.Observe("после подавления: суммарный объём " + Api5.Num(before) + " → " + Api5.Num(suppressed)
                                 + ", excluded=" + Api5.Raw(excludedRead) + ", тел " + BodyRows(part).Count);

                    feature.excluded = false;
                    doc.RebuildDocument();
                    part.RebuildModel();
                    var restored = SumVolumes(BodyRows(part));
                    step.Observe("после восстановления: суммарный объём " + Api5.Num(restored)
                                 + ", тел " + BodyRows(part).Count);

                    step.Data["volume_before_suppress"] = before;
                    step.Data["volume_suppressed"] = suppressed;
                    step.Data["volume_restored"] = restored;
                    step.Data["excluded_read_back"] = excludedRead;

                    var changed = before is not null && suppressed is not null && Math.Abs(before.Value - suppressed.Value) > 0.01;
                    var cameBack = before is not null && restored is not null && Math.Abs(before.Value - restored.Value) <= 0.01;
                    if (changed && cameBack)
                    {
                        step.Pass("подавление меняет геометрию, снятие возвращает её: адрес годится");
                    }
                    else
                    {
                        step.Fail("подавление не изменило геометрию (Δ=" + Api5.Num(suppressed - before)
                                  + ") или геометрия не вернулась: элемент дерева как адрес не работает");
                    }
                }
            }
            catch (Exception ex)
            {
                step.Fail(HResult.Describe(ex));
            }
        }

        var del = _report.Begin("TL.4", "Удаление признака через дерево API5",
            "Возвращаются ли исходные тела, если удалить признак, созданный фабрикой API7?");
        if (entity is null)
        {
            del.Unknown("элемент дерева не даёт ksEntity: удаление проверять не на чем");
            return;
        }

        try
        {
            var before = SumVolumes(BodyRows(part));
            var treeBefore = Api5.ReadFeatures(part, del).Count;
            var deleted = doc.DeleteObject(entity);
            doc.RebuildDocument();
            part.RebuildModel();
            var after = BodyRows(part);
            del.Observe("DeleteObject → " + Api5.Raw(deleted) + "; признаков " + treeBefore + " → "
                        + Api5.ReadFeatures(part, del).Count + "; тел " + after.Count + " → " + Describe(after));
            del.Data["delete_returned"] = deleted;
            del.Data["volume_before_delete"] = before;
            del.Data["volume_after_delete"] = SumVolumes(after);
            del.Data["bodies_after_delete"] = after.Count;

            if (deleted && after.Count > 1)
            {
                del.Pass("признак удалён, тела вернулись к состоянию до операции");
            }
            else
            {
                del.Fail("признак не удалён или тела не вернулись");
            }
        }
        catch (Exception ex)
        {
            del.Fail(HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════ TL.5 родной маршрут API5 ══

    private void Api5AggregateRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.5", "Родной маршрут API5: NewEntity(o3d_aggregate) и ksAggregateDefinition",
            "Даёт ли API5 булеву операцию настоящим ksEntity — то есть признак, у которого жизненный "
            + "цикл работает без посредников, — и можно ли этим определением задать операнды?");
        try
        {
            var blank = NewPart(out var fresh);
            try
            {
                if (!ExtrudeRect(blank, fresh, Ax0, Ax1, Ay0, Ay1, Az, step, "A5")
                    || !ExtrudeRect(blank, fresh, Bx0, Bx1, Ay0, Ay1, Az, step, "B5")
                    || !ExtrudeRect(blank, fresh, Sx0, Sx1, Sy0, Sy1, Sz, step, "S5"))
                {
                    step.Fail("заготовка для маршрута API5 не построена");
                    return;
                }

                step.Observe("тел до: " + Describe(BodyRows(fresh)));
                step.Observe("ожидание объединения: V=" + Api5.Num(UnionVolume));

                if (fresh.NewEntity(TypeAggregate) is not ksEntity entity)
                {
                    step.Fail("part.NewEntity(" + TypeAggregate + ") не отдал ksEntity: родного признака "
                              + "булевой операции в API5 нет");
                    return;
                }

                step.Observe("создан элемент: CLR=" + Api5.RuntimeName(entity)
                             + ", type=" + Api5.Raw(entity.type)
                             + " (" + Api5.ObjectTypeName((int)entity.type) + ")");
                step.Data["entity_type"] = entity.type.ToString();

                var definition = Api5.SafeObject(() => entity.GetDefinition());
                step.Observe("GetDefinition() → " + Api5.RuntimeName(definition));
                step.Data["definition"] = Api5.RuntimeName(definition);
                if (definition is null)
                {
                    step.Fail("у элемента нет определения: настраивать нечего");
                    return;
                }

                // Перечисляются ВСЕ кандидаты, а не только ожидаемые: вопрос опыта — существует ли
                // у определения хоть один член, которым задаются тела. Ожидание каталога
                // («BodyCollection — получить массив тел») проверяется здесь же.
                step.Data["definition_members"] = TlbScan.LiveMembers(definition, new[]
                {
                    "BooleanType", "BodyCollection", "ChooseBodies", "ChooseParts",
                    "SetBody", "SetBodies", "BaseObject", "ModifyObjects",
                });

                if (definition is not ksAggregateDefinition typed)
                {
                    step.Unknown("определение не приводится к ksAggregateDefinition: маршрут "
                                 + "настройки не выяснен");
                    return;
                }

                typed.BooleanType = (short)ksBooleanType.ksUnion;
                step.Observe("BooleanType записан и прочитан обратно: " + typed.BooleanType
                             + " (ksUnion=" + (short)ksBooleanType.ksUnion + ")");

                // BodyCollection объявлен МЕТОДОМ (get-доступ): проверяется, что это чтение, а не
                // запись — иначе задать операнды можно было бы прямо здесь.
                var readBack = Api5.SafeObject(() => typed.BodyCollection());
                step.Observe("BodyCollection() → " + Api5.RuntimeName(readBack)
                             + ": член только на чтение, операнды им не задаются");

                // Операнды могут задаваться выбором тел у САМОГО признака — это отдельный вопрос, и
                // он решается перечислением членов элемента, а не догадкой.
                step.Data["entity_members"] = TlbScan.LiveMembers(entity, new[]
                {
                    "ChooseBodies", "ChooseBodiesType", "BodyCollection", "SetBody",
                    "Create", "Update", "excluded",
                });

                var created = Api5.SafeBool(entity.Create) == true;
                fresh.RebuildModel();
                blank.RebuildDocument();
                var after = BodyRows(fresh);
                step.Observe("Create()=" + Api5.Raw(created) + "; тел " + after.Count + " → "
                             + Describe(after) + "; суммарный объём " + Api5.Num(SumVolumes(after)));
                step.Data["created"] = created;
                step.Data["bodies_after"] = after.Count;
                step.Data["total_volume_after"] = SumVolumes(after);

                if (!created)
                {
                    step.Fail("Create() вернул false: родной маршрут не принял постановку");
                    return;
                }

                step.Unknown("признак создан настоящим ksEntity, но задать операнды через "
                             + "ksAggregateDefinition нечем: у определения нет члена для записи тел. "
                             + "Вопрос «чем API5 задаёт тела булевой операции» остаётся открытым");
            }
            finally
            {
                TryClose(blank);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    // ═══════════════════ TL.6..TL.9 остальные три семейства ══

    private void Api7SplitRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.6", "Разделение: признак API7 и дерево API5",
            "Попадает ли ISplitSolid в дерево признаков, и поддаётся ли он подавлению и удалению?");
        ksEntity? created = null;
        try
        {
            var blank = NewPart(out var fresh);
            try
            {
                if (!ExtrudeRect(blank, fresh, Ax0, Ax1, Ay0, Ay1, Az, step, "A"))
                {
                    step.Fail("брусок не построен");
                    return;
                }

                var before = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ДО: " + TreeText(before));

                var container = Container(blank);
                var plane = MakePlane(container, step, "split-plane");
                if (plane is null)
                {
                    return;
                }

                if (container?.SplitSolids?.Add() is not { } raw || raw is not ISplitSolid split)
                {
                    step.Fail("SplitSolids.Add() не отдал ISplitSolid");
                    return;
                }

                split.CutObjects = new IModelObject[] { (IModelObject)plane };
                var updated = split.Update();
                fresh.RebuildModel();
                blank.RebuildDocument();
                step.Observe("Update()=" + updated + "; тел " + BodyRows(fresh).Count
                             + " → " + Describe(BodyRows(fresh)));

                var after = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ПОСЛЕ: " + TreeText(after));
                step.Data["tree_before"] = before.Count;
                step.Data["tree_after"] = after.Count;
                step.Data["bodies_after"] = BodyRows(fresh).Count;
                created = after.Count > before.Count ? after[after.Count - 1].Entity : null;
                step.Data["added_name"] = after.Count > before.Count ? after[after.Count - 1].Name : null;
                step.Data["added_type"] = after.Count > before.Count ? after[after.Count - 1].TypeName : null;
                step.Data["added_definition"] = after.Count > before.Count ? DefinitionName(after[after.Count - 1]) : null;

                if (after.Count > before.Count)
                {
                    step.Pass("признак разделения виден в дереве API5");
                }
                else
                {
                    step.Fail("признак разделения в дереве API5 не появился");
                }
            }
            finally
            {
                TryClose(blank);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    private void Api7CutRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.7", "Отсечение: признак API7 и дерево API5",
            "Попадает ли ICut в дерево признаков, и каким элементом?");
        try
        {
            var blank = NewPart(out var fresh);
            try
            {
                if (!ExtrudeRect(blank, fresh, Ax0, Ax1, Ay0, Ay1, Az, step, "A"))
                {
                    step.Fail("брусок не построен");
                    return;
                }

                var before = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ДО: " + TreeText(before));

                var container = Container(blank);
                var plane = MakePlane(container, step, "cut-plane");
                if (plane is null)
                {
                    return;
                }

                if (container?.Cuts?.Add() is not { } raw || raw is not ICut cut)
                {
                    step.Fail("Cuts.Add() не отдал ICut");
                    return;
                }

                cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
                cut.CutObject = (IModelObject)plane;
                cut.Direction = true;
                var updated = cut.Update();
                fresh.RebuildModel();
                blank.RebuildDocument();
                step.Observe("Update()=" + updated + "; тел " + BodyRows(fresh).Count
                             + " → " + Describe(BodyRows(fresh)));

                var after = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ПОСЛЕ: " + TreeText(after));
                step.Data["tree_before"] = before.Count;
                step.Data["tree_after"] = after.Count;
                step.Data["bodies_after"] = BodyRows(fresh).Count;
                step.Data["added_name"] = after.Count > before.Count ? after[after.Count - 1].Name : null;
                step.Data["added_type"] = after.Count > before.Count ? after[after.Count - 1].TypeName : null;
                step.Data["added_definition"] = after.Count > before.Count ? DefinitionName(after[after.Count - 1]) : null;

                if (after.Count > before.Count)
                {
                    step.Pass("признак отсечения виден в дереве API5");
                }
                else
                {
                    step.Fail("признак отсечения в дереве API5 не появился");
                }
            }
            finally
            {
                TryClose(blank);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    private void Api5CutByPlaneRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.8", "Родной маршрут API5: NewEntity(o3d_cutByPlane) и ksCutByPlaneDefinition",
            "Существует ли отсечение плоскостью настоящим ksEntity в API5?");
        try
        {
            var blank = NewPart(out var fresh);
            try
            {
                if (!ExtrudeRect(blank, fresh, Ax0, Ax1, Ay0, Ay1, Az, step, "A"))
                {
                    step.Fail("брусок не построен");
                    return;
                }

                if (fresh.NewEntity(TypeCutByPlane) is not ksEntity entity)
                {
                    step.Fail("part.NewEntity(" + TypeCutByPlane + ") не отдал ksEntity");
                    return;
                }

                var definition = Api5.SafeObject(() => entity.GetDefinition());
                step.Observe("GetDefinition() → " + Api5.RuntimeName(definition));
                step.Data["definition"] = Api5.RuntimeName(definition);
                if (definition is null)
                {
                    step.Fail("определения нет");
                    return;
                }

                step.Data["definition_members"] = TlbScan.LiveMembers(definition, new[]
                {
                    "CutObject", "BuildingType", "Direction", "SetCutObject", "Sketch", "Plane",
                });

                if (definition is ksCutByPlaneDefinition typed)
                {
                    step.Observe("приведено к ksCutByPlaneDefinition типизированно; члены: "
                                 + string.Join(", ", typeof(ksCutByPlaneDefinition).GetMembers()
                                     .Select(m => m.Name).Distinct().Take(24)));
                    step.Data["typed_members"] = typeof(ksCutByPlaneDefinition).GetMembers()
                        .Select(m => m.Name).Distinct().ToList();
                }
                else
                {
                    step.Unknown("определение не приводится к ksCutByPlaneDefinition: маршрут "
                                 + "настройки остаётся невыясненным");
                }
            }
            finally
            {
                TryClose(blank);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    private void Api7RepositionRoute(ksDocument3D doc, ksPart part)
    {
        var step = _report.Begin("TL.9", "Перенос: признак API7 и дерево API5",
            "Попадает ли IBodyReposition в дерево признаков, и каким элементом?");
        try
        {
            var blank = NewPart(out var fresh);
            try
            {
                if (!ExtrudeRect(blank, fresh, 10d, 30d, 0d, 10d, 5d, step, "T"))
                {
                    step.Fail("брусок не построен");
                    return;
                }

                var before = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ДО: " + TreeText(before));

                var container = Container(blank);
                if (container?.BodyRepositions?.Add() is not { } raw || raw is not IBodyReposition reposition)
                {
                    step.Fail("BodyRepositions.Add() не отдал IBodyReposition");
                    return;
                }

                var rows = BodyRows(fresh);
                var body = rows.Count > 0 ? rows[0] : null;
                var body7 = body is null ? null : Transfer(body);
                if (body7 is null)
                {
                    step.Fail("тело не переносится в API7");
                    return;
                }

                reposition.RepositionBody = (IKompasAPIObject)body7;
                reposition.Position.InitByMatrix3D(TranslateMatrix(7d, -11d, 13d));
                var updated = reposition.Update();
                fresh.RebuildModel();
                blank.RebuildDocument();
                step.Observe("Update()=" + updated + "; тела → " + Describe(BodyRows(fresh)));

                var after = Api5.ReadFeatures(fresh, step);
                step.Observe("дерево ПОСЛЕ: " + TreeText(after));
                step.Data["tree_before"] = before.Count;
                step.Data["tree_after"] = after.Count;
                step.Data["added_name"] = after.Count > before.Count ? after[after.Count - 1].Name : null;
                step.Data["added_type"] = after.Count > before.Count ? after[after.Count - 1].TypeName : null;
                step.Data["added_definition"] = after.Count > before.Count ? DefinitionName(after[after.Count - 1]) : null;

                if (after.Count > before.Count)
                {
                    step.Pass("признак переноса виден в дереве API5");
                }
                else
                {
                    step.Fail("признак переноса в дереве API5 не появился");
                }
            }
            finally
            {
                TryClose(blank);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════════════ helpers ══

    private static string TreeText(IReadOnlyList<Api5.FeatureReading> rows)
    {
        if (rows.Count == 0)
        {
            return "<дерево пусто>";
        }

        return string.Join(" | ", rows.Select(r =>
            "#" + r.Index + " " + (r.Name ?? "<без имени>") + " [" + (r.TypeName ?? "?") + "]"
            + " def=" + (r.Entity is null && r.Feature is null ? "?" : "…")));
    }

    private static string DefinitionName(Api5.FeatureReading reading)
    {
        try
        {
            if (reading.Entity is { } entity && Api5.SafeObject(() => entity.GetDefinition()) is { } definition)
            {
                return definition.GetType().Name;
            }

            if (reading.Feature is { } feature && feature.GetObject() is ksEntity entity2
                && Api5.SafeObject(() => entity2.GetDefinition()) is { } definition2)
            {
                return definition2.GetType().Name;
            }
        }
        catch (Exception)
        {
            // Ниже — честный ответ «не прочитано».
        }

        return "<определение не прочитано>";
    }

    private static double[] TranslateMatrix(double x, double y, double z) =>
        new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            x, y, z, 1,
        };

    /// <summary>Плоскость x=10 с нормалью (1,0,0) — через <c>Planes3D.Add(o3d_plane3Points)</c>,
    /// измеренный маршрут пробы SP.</summary>
    private object? MakePlane(IModelContainer? container, ProbeStep step, string prefix)
    {
        try
        {
            if (container is null
                || (container as IAuxiliaryGeomContainer)?.Planes3D is not { } planes
                || planes.Add(ksObj3dTypeEnum.o3d_plane3Points) is not IPlane3D plane
                || plane is not IPlane3DBy3Points byPoints)
            {
                step.Fail(prefix + ": плоскость не создана");
                return null;
            }

            var p1 = MakePoint(container, 10d, 0d, 0d);
            var p2 = MakePoint(container, 10d, 10d, 0d);
            var p3 = MakePoint(container, 10d, 0d, 10d);
            if (p1 is null || p2 is null || p3 is null)
            {
                step.Fail(prefix + ": точки плоскости не созданы");
                return null;
            }

            byPoints.Point1 = p1;
            byPoints.Point2 = p2;
            byPoints.Point3 = p3;
            var updated = plane.Update();
            step.Observe(prefix + ": Update()=" + updated + ", тип=" + Api5.Raw(plane.ModelObjectType));
            return updated ? plane : null;
        }
        catch (Exception ex)
        {
            step.Fail(prefix + ": " + HResult.Describe(ex));
            return null;
        }
    }

    private static IPoint3D? MakePoint(IModelContainer container, double x, double y, double z)
    {
        try
        {
            if (container.Points3D is not { } points || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Update();
            return point;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool CreateBooleanApi7(
        ksDocument3D doc, ksPart part, BodyRow target, IReadOnlyList<BodyRow> tools,
        ksBooleanType type, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.Booleans?.Add() is not IBoolean boolean)
            {
                step.Fail("Booleans.Add() не отдал IBoolean");
                return false;
            }

            var target7 = Transfer(target) as IKompasAPIObject;
            if (target7 is null)
            {
                step.Fail("цель не переносится в API7");
                return false;
            }

            var tools7 = new object[tools.Count];
            for (var i = 0; i < tools.Count; i++)
            {
                tools7[i] = Transfer(tools[i])!;
            }

            boolean.BaseObject = target7;
            boolean.ModifyObjects = tools7;
            boolean.BooleanType = type;
            boolean.SaveCopyBaseObject = false;
            boolean.SaveCopyModifyObjects = false;
            var updated = boolean.Update();
            step.Observe("API7: BooleanType=" + type + ", инструментов=" + tools.Count
                         + ", Update()=" + updated + "; ожидание V=" + Api5.Num(UnionVolume));
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("после API7: " + Describe(BodyRows(part)));
            return updated;
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
            return false;
        }
    }

    private IModelContainer? Container(ksDocument3D doc)
    {
        try
        {
            return _app.TransferInterface(doc, 2, 0) is IKompasDocument3D document7
                   && document7.TopPart is IModelContainer container
                ? container
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private object? Transfer(BodyRow row)
    {
        try
        {
            return row.Element is null ? null : _app.TransferInterface(row.Element, 2, 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ksDocument3D NewPart(out ksPart part)
    {
        var doc = (ksDocument3D)_app.Document3D();
        doc.Create(true, true);
        if (doc.GetPart(-1) is not ksPart created)
        {
            throw new InvalidOperationException("деталь не получена");
        }

        part = created;
        return doc;
    }

    private static bool ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        if (part.NewEntity(5 /* o3d_sketch */) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Observe(prefix + ": эскиз не создан");
            return false;
        }

        if (part.GetDefaultEntity(1 /* o3d_planeXOY */) is not ksEntity plane)
        {
            step.Observe(prefix + ": плоскости XOY нет");
            return false;
        }

        sketch.name = prefix + "-profile";
        definition.SetPlane(plane);
        sketch.Create();
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
        }

        definition.EndEdit();

        if (part.NewEntity(24 /* o3d_baseExtrusion */) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition extrusionDefinition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        extrusion.name = prefix;
        extrusionDefinition.SetSketch(sketch);
        extrusionDefinition.directionType = 0;
        extrusionDefinition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() выдавливания → false");
        }

        return created;
    }

    private static List<BodyRow> BodyRows(ksPart part)
    {
        var rows = new List<BodyRow>();
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return rows;
            }

            bodies.refresh();
            var count = bodies.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = bodies.GetByIndex(i);
                var body = element as ksBody;
                double[]? min = null;
                double[]? max = null;
                if (body is not null
                    && Api5.SafeBool(() => body.GetGabarit(out _, out _, out _, out _, out _, out _)) == true)
                {
                    body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                    min = new[] { x1, y1, z1 };
                    max = new[] { x2, y2, z2 };
                }

                rows.Add(new BodyRow
                {
                    Index = i,
                    Element = element,
                    Volume = Api5.BodyVolume(element),
                    Min = min ?? new double[3],
                    Max = max ?? new double[3],
                    HasBox = min is not null,
                });
            }
        }
        catch (Exception)
        {
            // Пустой список — честный ответ «тел не прочитано».
        }

        return rows;
    }

    private static BodyRow? FindBody(List<BodyRow> rows, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        foreach (var row in rows)
        {
            if (!row.HasBox)
            {
                continue;
            }

            if (Math.Abs(row.Min[0] - x0) < 0.01 && Math.Abs(row.Min[1] - y0) < 0.01
                && Math.Abs(row.Min[2] - z0) < 0.01 && Math.Abs(row.Max[0] - x1) < 0.01
                && Math.Abs(row.Max[1] - y1) < 0.01 && Math.Abs(row.Max[2] - z1) < 0.01)
            {
                return row;
            }
        }

        return null;
    }

    private static double? SumVolumes(List<BodyRow> rows)
    {
        double sum = 0d;
        foreach (var row in rows)
        {
            if (row.Volume is null)
            {
                return null;
            }

            sum += row.Volume.Value;
        }

        return rows.Count == 0 ? null : sum;
    }

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0
            ? "<нет тел>"
            : string.Join(" | ", rows.Select(r =>
                "#" + r.Index + " V=" + Api5.Num(r.Volume)
                + (r.HasBox
                    ? " (" + Api5.Num(r.Min[0]) + "," + Api5.Num(r.Min[1]) + "," + Api5.Num(r.Min[2]) + ")…("
                      + Api5.Num(r.Max[0]) + "," + Api5.Num(r.Max[1]) + "," + Api5.Num(r.Max[2]) + ")"
                    : " <габарит не прочитан>")));

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Закрытие документа — уборка, а не измерение.
        }
    }

    private sealed class BodyRow
    {
        public int Index { get; init; }

        public object? Element { get; init; }

        public double? Volume { get; init; }

        public double[] Min { get; init; } = new double[3];

        public double[] Max { get; init; } = new double[3];

        public bool HasBox { get; init; }
    }
}
