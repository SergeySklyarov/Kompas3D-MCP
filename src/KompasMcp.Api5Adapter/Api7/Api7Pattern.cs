using System.Runtime.InteropServices;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>
/// Типизированные операции API7 над массивами и зеркальным массивом (SM-18 / SM-19 / SM-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Основание — опубликованная страница справки, а не имя, найденное в интерфейсе.</b> Соответствие
/// «числовой тип → интерфейс» взято со страницы SDK <c>copytype.html</c>, открытой по проводу
/// (<c>https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/copytype.html</c>, HTTP 200):
/// <c>o3d_meshCopy 35 ILinearPattern</c>, <c>o3d_circularCopy 36 ICircularPattern</c>,
/// <c>o3d_mirrorOperation 48 IMirrorPattern</c>, <c>o3d_mirrorAllOperation 49 IMirrorPattern</c>,
/// <c>o3d_BodiesMeshCopy 528 ILinearPattern</c>, <c>o3d_BodiesCircularCopy 529 ICircularPattern</c>.
/// Сами числа прочитаны из <c>Interop.Kompas6Constants3D.dll</c> прибором
/// <c>KompasMcp.InteropScan</c>, а не пересказаны.
/// </para>
/// <para>
/// <b>Члены взяты со страницы свойств интерфейса, а не из соображений симметрии.</b>
/// <c>ilinearpattern_props.html</c> перечисляет <c>Angle1/2</c>, <c>Axis1/2</c>,
/// <c>BoundaryInstancesStepFactor1/2</c>, <c>BuildingType</c>, <c>Count1/2</c>, <c>Direction1/2</c>,
/// <c>Step1/2</c>, <c>Vector1/2</c>; <c>icircularpattern_props.html</c> — <c>Axis</c>,
/// <c>BoundaryInstancesStepFactor1/2</c>, <c>BuildingType</c>, <c>Count1/2</c>,
/// <c>ReverseDirection</c>, <c>SaveInitialOrientation</c>, <c>Step1/2</c>, <c>StepByAxis</c>;
/// <c>imirrorpattern_props.html</c> — только <c>Plane</c> и <c>SaveInitialObjects</c>.
/// </para>
/// <para>
/// <b>Чего здесь нет и почему.</b> <c>Vector1/Vector2</c> не пишутся: в <c>kAPI7.tlb</c> у них
/// объявлены только геттеры, а в вендорской интероп-сборке они не объявлены вовсе (измерено
/// <c>InteropScan --type-members ILinearPattern</c>: 39 членов, ни <c>Vector1</c>, ни
/// <c>Vector2</c>). Направление задаётся осью. Это открытый вопрос OQ-B-03, названный, а не
/// обойдённый молчанием.
/// </para>
/// <para>
/// <b>Ни одного <c>NewEntity</c> и ни одного <c>Create()</c>.</b> Урок вращения (SM-03): оболочка
/// API5 вокруг объекта фабрики API7 — смешанный жизненный цикл, при котором <c>Create()</c>
/// возвращает <c>true</c> на пустой операции и ничего не строится. Массивы создаются ТОЛЬКО
/// фабрикой <c>IModelContainer.FeaturePatterns.Add</c>.
/// </para>
/// </remarks>
internal static class Api7Pattern
{
    /// <summary>Числовой тип фабрики для сетки либо кругового массива.</summary>
    /// <remarks>
    /// Массив ТЕЛ — отдельный тип, а не флаг: <c>o3d_BodiesMeshCopy=528</c> /
    /// <c>o3d_BodiesCircularCopy=529</c> против <c>o3d_meshCopy=35</c> / <c>o3d_circularCopy=36</c>.
    /// </remarks>
    public static ksObj3dTypeEnum LinearType(PatternCopyKind kind) => kind switch
    {
        PatternCopyKind.Bodies => ksObj3dTypeEnum.o3d_BodiesMeshCopy,
        _ => ksObj3dTypeEnum.o3d_meshCopy,
    };

    public static ksObj3dTypeEnum CircularType(PatternCopyKind kind) => kind switch
    {
        PatternCopyKind.Bodies => ksObj3dTypeEnum.o3d_BodiesCircularCopy,
        _ => ksObj3dTypeEnum.o3d_circularCopy,
    };

    public static ksObj3dTypeEnum MirrorType(PatternMirrorMode mode) => mode switch
    {
        PatternMirrorMode.AllBodies => ksObj3dTypeEnum.o3d_mirrorAllOperation,
        _ => ksObj3dTypeEnum.o3d_mirrorOperation,
    };

    /// <summary>
    /// Способ построения массива по сетке. Значения прочитаны из интероп-сборки констант
    /// (<c>ksLinearPatternBuildingTypeEnum</c>: <c>ksLPSaveAll=0</c>, <c>ksLPSaveAlongPerimeter=1</c>,
    /// <c>ksLPSaveAlongAxially=2</c>, <c>ksLPChessOrderByAxis1=3</c>, <c>ksLPChessOrderByAxis2=4</c>).
    /// Неизвестное слово отвергается вызывающим до COM, здесь — значение по умолчанию.
    /// </summary>
    public static ksLinearPatternBuildingTypeEnum LinearBuilding(string name) => name switch
    {
        "save_all" => ksLinearPatternBuildingTypeEnum.ksLPSaveAll,
        "save_along_perimeter" => ksLinearPatternBuildingTypeEnum.ksLPSaveAlongPerimeter,
        "save_along_axially" => ksLinearPatternBuildingTypeEnum.ksLPSaveAlongAxially,
        "chess_order_by_axis1" => ksLinearPatternBuildingTypeEnum.ksLPChessOrderByAxis1,
        "chess_order_by_axis2" => ksLinearPatternBuildingTypeEnum.ksLPChessOrderByAxis2,
        _ => ksLinearPatternBuildingTypeEnum.ksLPSaveAll,
    };

    public static ksCircularPatternBuildingTypeEnum CircularBuilding(string name) => name switch
    {
        "save_all" => ksCircularPatternBuildingTypeEnum.ksCPSaveAll,
        "chess_order_by_axis1" => ksCircularPatternBuildingTypeEnum.ksCPChessOrderByAxis1,
        "chess_order_by_axis2" => ksCircularPatternBuildingTypeEnum.ksCPChessOrderByAxis2,
        _ => ksCircularPatternBuildingTypeEnum.ksCPSaveAll,
    };

    /// <summary>
    /// Тип действия над телами для <c>IChooseBodies7.ChooseBodiesType</c>. Значения прочитаны из
    /// интероп-сборки констант (<c>ksChooseBodiesType</c>: <c>ksNewBody=0</c>,
    /// <c>ksAutomaticDefinition=1</c>, <c>ksManualEditing=2</c>, <c>ksAllBodies=3</c>).
    /// </summary>
    public static ksChooseBodiesType ChooseBodies(string name) => name switch
    {
        "new_body" => ksChooseBodiesType.ksNewBody,
        "automatic" => ksChooseBodiesType.ksAutomaticDefinition,
        "manual" => ksChooseBodiesType.ksManualEditing,
        "all_bodies" => ksChooseBodiesType.ksAllBodies,
        _ => ksChooseBodiesType.ksAllBodies,
    };

    /// <summary>Число признаков массивов в коллекции API7. null — не прочитано (не «ноль»).</summary>
    public static int? Count(IModelContainer container)
    {
        try
        {
            return container.FeaturePatterns?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Создать массив по сетке и записать параметры, затем <c>Update()</c>.</summary>
    /// <remarks>
    /// Возвращается (объект, причина отказа): вызывающий обязан отличить отказ КОМПАСа от падения
    /// адаптера, и «не смогли» не должно выглядеть как «создали». Порядок записей — часть контракта:
    /// <c>InitialObjects</c> и оси пишутся ДО шагов и количеств, потому что массив без исходных
    /// объектов и без оси не существует как признак.
    /// </remarks>
    public static (ILinearPattern? Pattern, string? Failure) TryCreateLinear(
        IModelContainer container,
        PatternCopyKind kind,
        object[] sources7,
        IModelObject axis1,
        double step1,
        int count1,
        double? angle1,
        bool direction1,
        bool boundary1,
        IModelObject? axis2,
        double step2,
        int count2,
        double? angle2,
        bool direction2,
        bool boundary2,
        string buildingType,
        bool geometryPattern)
    {
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима");
            }

            if (patterns.Add(LinearType(kind)) is not ILinearPattern linear)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(ILinearPattern)");
            }

            linear.InitialObjects = sources7;
            linear.Axis1 = axis1;

            linear.Step1 = step1;
            linear.Count1 = count1;
            // УГОЛ ПИШЕТСЯ ТОЛЬКО КОГДА ОН ЗАДАН. Измерено прогоном: запись Angle2 = 0 (значение по
            // умолчанию обязательного поля) совмещала второе направление с первым, и прямоугольная
            // сетка вырождалась в линию — копии продолжали первую ось (x = 20, 40, 60, 50, 70, 90
            // при y = 20). Угол между направлениями в модели по умолчанию 90°, и незаданное поле
            // обязано оставить это значение, а не переписать его нулём.
            if (angle1 is double a1)
            {
                linear.Angle1 = a1;
            }

            linear.Direction1 = direction1;
            linear.BoundaryInstancesStepFactor1 = boundary1;

            // Второе направление пишется ТОЛЬКО когда оно участвует: Count2 = 1 с незаданной осью
            // оставило бы в модели поле, которого в постановке не было.
            if (axis2 is not null && count2 > 1)
            {
                linear.Axis2 = axis2;
                linear.Step2 = step2;
                linear.Count2 = count2;
                if (angle2 is double a2)
                {
                    linear.Angle2 = a2;
                }

                linear.Direction2 = direction2;
                linear.BoundaryInstancesStepFactor2 = boundary2;
            }
            else
            {
                linear.Count2 = 1;
            }

            linear.BuildingType = LinearBuilding(buildingType);
            linear.GeometryPattern = geometryPattern;

            return linear.Update()
                ? (linear, null)
                : (linear, "ILinearPattern.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>Создать массив по концентрической сетке и записать параметры, затем <c>Update()</c>.</summary>
    public static (ICircularPattern? Pattern, string? Failure) TryCreateCircular(
        IModelContainer container,
        PatternCopyKind kind,
        object[] sources7,
        IModelObject axis,
        int count1,
        double step1,
        int count2,
        double step2Deg,
        double stepByAxis,
        bool boundary1,
        bool boundary2,
        bool reverse,
        bool saveInitialOrientation,
        string buildingType,
        bool geometryPattern)
    {
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима");
            }

            if (patterns.Add(CircularType(kind)) is not ICircularPattern circular)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(ICircularPattern)");
            }

            circular.InitialObjects = sources7;
            circular.Axis = axis;

            circular.Count1 = count1;
            circular.Step1 = step1;
            circular.BoundaryInstancesStepFactor1 = boundary1;

            circular.Count2 = count2;
            circular.Step2 = step2Deg;
            circular.BoundaryInstancesStepFactor2 = boundary2;

            circular.StepByAxis = stepByAxis;
            circular.ReverseDirection = reverse;
            circular.SaveInitialOrientation = saveInitialOrientation;
            circular.BuildingType = CircularBuilding(buildingType);
            circular.GeometryPattern = geometryPattern;

            return circular.Update()
                ? (circular, null)
                : (circular, "ICircularPattern.Update() вернул false — признак создан, но не построен");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>
    /// Создать зеркальный массив и записать параметры, затем <c>Update()</c>.
    /// </summary>
    /// <remarks>
    /// Для вида <c>all_bodies</c> дополнительно ставится <c>IChooseBodies7</c>: измерено прибором
    /// <c>InteropScan</c>, что объект зеркального массива отвечает на этот интерфейс (то же
    /// утверждает страница <c>copytype.html</c>: «Дополнительно имеет интерфейс выбора тел
    /// IChooseBodies7»). Отсутствие интерфейса — названная заметка, а не молчание: без него выбор
    /// тел не выражается, и это видно вызывающему.
    /// </remarks>
    public static (IMirrorPattern? Pattern, string? Failure, IReadOnlyList<string> Notes) TryCreateMirror(
        IModelContainer container,
        PatternMirrorMode mode,
        object[] sources7,
        IModelObject plane,
        bool saveInitialObjects,
        string chooseBodiesType)
    {
        var notes = new List<string>();
        try
        {
            if (container.FeaturePatterns is not { } patterns)
            {
                return (null, "IModelContainer.FeaturePatterns → null: фабрика недостижима", notes);
            }

            if (patterns.Add(MirrorType(mode)) is not IMirrorPattern mirror)
            {
                return (null, "FeaturePatterns.Add не отдал объект, отвечающий на QI(IMirrorPattern)", notes);
            }

            // InitialObjects ставится только когда список НЕ пуст: у «зеркально отразить все»
            // входом служит сама деталь, и запись пустого массива была бы утверждением «выбрано
            // ничего», которого постановка не делала.
            if (sources7.Length > 0)
            {
                mirror.InitialObjects = sources7;
            }

            mirror.Plane = plane;
            mirror.SaveInitialObjects = saveInitialObjects;
            // ЗАПИСЬ И НЕМЕДЛЕННОЕ ЧТЕНИЕ — ОДНОЙ СТРОКОЙ. Без чтения сразу после записи нельзя
            // отличить «свойство не принято COM» от «принято и потеряно при построении», а это
            // разные дефекты.
            var flagReadBack = SafeBool(() => mirror.SaveInitialObjects);
            notes.Add($"save_initial_objects: записано {saveInitialObjects}, " +
                $"прочитано сразу после записи {flagReadBack}");
            if (flagReadBack is bool readBack && readBack != saveInitialObjects)
            {
                // ОТКАЗ НАЗВАН, А НЕ СПРЯТАН, и назван ВМЕСТЕ С ПРИЧИНОЙ ИЗ СПРАВКИ. Страница
                // imirrorpattern_saveinitialobjects.html ограничивает свойство только
                // o3d_mirrorAllOperation: «у других операций зеркального копирования возможность
                // скрыть экземпляры отсутствует». Измерено на обеих операциях: у 49 запись false
                // читается обратно как false и тела заменяются отражёнными, у 48 — читается как
                // true и геометрия не меняется. Запрос принимается, но его непринятие моделью
                // объявляется здесь, а не выдаётся за сработавшее свойство.
                notes.Add($"save_initial_objects НЕ принято моделью для {MirrorType(mode)}: " +
                    "справка ограничивает свойство только o3d_mirrorAllOperation, и измерение это " +
                    "подтверждает");
            }

            if (mode == PatternMirrorMode.AllBodies)
            {
                if (mirror is IChooseBodies7 choose)
                {
                    choose.ChooseBodiesType = ChooseBodies(chooseBodiesType);
                    if (sources7.Length > 0)
                    {
                        choose.Bodies = sources7;
                    }

                    notes.Add($"IChooseBodies7: ChooseBodiesType={chooseBodiesType}, тел {sources7.Length}");
                }
                else
                {
                    notes.Add("объект зеркального массива не отвечает QI(IChooseBodies7) — " +
                        "выбор тел не выражен; страница copytype.html объявляет этот интерфейс у " +
                        "o3d_mirrorAllOperation, и расхождение названо, а не скрыто");
                }
            }

            return mirror.Update()
                ? (mirror, null, notes)
                : (mirror, "IMirrorPattern.Update() вернул false — признак создан, но не построен", notes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, Describe(ex), notes);
        }
    }

    /// <summary>
    /// Перезаписать параметры СУЩЕСТВУЮЩЕГО признака массива и вызвать <c>Update()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Правка — это запись в ТОТ ЖЕ объект и <c>Update()</c>, а не создание похожего.</b> Объект
    /// берётся из живой коллекции <c>IModelContainer.FeaturePatterns</c> по сопоставлению с
    /// признаком дерева, а не сохраняется между вызовами: адрес COM-объекта между вызовами не
    /// переживает перестроения, и «сохранённый» объект правил бы уже не тот признак.
    /// </para>
    /// <para>
    /// <b>Пишутся только те члены, которые заданы.</b> Незаданный член остаётся прежним — иначе
    /// «изменилось ровно запрошенное» было бы неотличимо от «сброшено в умолчание». Набор
    /// допустимых членов у каждого семейства свой, и проверяет его вызывающий ДО мутации:
    /// <c>save_initial_orientation</c> есть только у кругового, <c>save_initial_objects</c> — только
    /// у зеркального, <c>step2_deg</c> — только у кругового, <c>step2_mm</c>, <c>angle1/2</c> и
    /// <c>direction1/2</c> — только у сетки.
    /// </para>
    /// <para>
    /// <b><c>Update() = true</c> здесь — «принято», а не «применено».</b> Поэтому проверки
    /// называются <c>set_&lt;член&gt;</c> и говорят именно о записи; применение подтверждается
    /// отдельным чтением модели ПОСЛЕ перестроения, а не этим возвратом.
    /// </para>
    /// </remarks>
    public static (bool Applied, string? Failure, IReadOnlyList<NamedCheck> Writes) TryEdit(
        IFeaturePattern pattern,
        PatternEditDto edit)
    {
        var writes = new List<NamedCheck>();

        static NamedCheck Write(string member, string? was, string now) =>
            new($"set_{member}", true,
                Observed: $"записано {now}; до записи {was ?? "не прочитано"}",
                Expected: $"{now} (применение подтверждается чтением после Update, а не этой записью)");

        try
        {
            switch (pattern)
            {
                case ILinearPattern linear:
                    if (edit.Count1 is int lc1)
                    {
                        writes.Add(Write("count1", SafeDouble(() => linear.Count1)?.ToString(Inv), lc1.ToString(Inv)));
                        linear.Count1 = lc1;
                    }

                    if (edit.Count2 is int lc2)
                    {
                        writes.Add(Write("count2", SafeDouble(() => linear.Count2)?.ToString(Inv), lc2.ToString(Inv)));
                        linear.Count2 = lc2;
                    }

                    if (edit.Step1Mm is double ls1)
                    {
                        writes.Add(Write("step1", SafeDouble(() => linear.Step1)?.ToString(Inv), ls1.ToString(Inv)));
                        linear.Step1 = ls1;
                    }

                    if (edit.Step2Mm is double ls2)
                    {
                        writes.Add(Write("step2", SafeDouble(() => linear.Step2)?.ToString(Inv), ls2.ToString(Inv)));
                        linear.Step2 = ls2;
                    }

                    if (edit.Angle1Deg is double la1)
                    {
                        writes.Add(Write("angle1", SafeDouble(() => linear.Angle1)?.ToString(Inv), la1.ToString(Inv)));
                        linear.Angle1 = la1;
                    }

                    if (edit.Angle2Deg is double la2)
                    {
                        writes.Add(Write("angle2", SafeDouble(() => linear.Angle2)?.ToString(Inv), la2.ToString(Inv)));
                        linear.Angle2 = la2;
                    }

                    if (edit.Direction1 is bool ld1)
                    {
                        writes.Add(Write("direction1", SafeBool(() => linear.Direction1)?.ToString(), ld1.ToString()));
                        linear.Direction1 = ld1;
                    }

                    if (edit.Direction2 is bool ld2)
                    {
                        writes.Add(Write("direction2", SafeBool(() => linear.Direction2)?.ToString(), ld2.ToString()));
                        linear.Direction2 = ld2;
                    }

                    if (edit.BuildingType is string lbt)
                    {
                        writes.Add(Write("building_type",
                            SafeString(() => linear.BuildingType.ToString()), lbt));
                        linear.BuildingType = LinearBuilding(lbt);
                    }

                    break;

                case ICircularPattern circular:
                    if (edit.Count1 is int cc1)
                    {
                        writes.Add(Write("count1", SafeDouble(() => circular.Count1)?.ToString(Inv), cc1.ToString(Inv)));
                        circular.Count1 = cc1;
                    }

                    if (edit.Count2 is int cc2)
                    {
                        writes.Add(Write("count2", SafeDouble(() => circular.Count2)?.ToString(Inv), cc2.ToString(Inv)));
                        circular.Count2 = cc2;
                    }

                    if (edit.Step1Mm is double cs1)
                    {
                        writes.Add(Write("step1", SafeDouble(() => circular.Step1)?.ToString(Inv), cs1.ToString(Inv)));
                        circular.Step1 = cs1;
                    }

                    if (edit.Step2Deg is double cs2)
                    {
                        writes.Add(Write("step2", SafeDouble(() => circular.Step2)?.ToString(Inv), cs2.ToString(Inv)));
                        circular.Step2 = cs2;
                    }

                    if (edit.StepByAxisMm is double csa)
                    {
                        writes.Add(Write("step_by_axis", SafeDouble(() => circular.StepByAxis)?.ToString(Inv), csa.ToString(Inv)));
                        circular.StepByAxis = csa;
                    }

                    if (edit.ReverseDirection is bool crd)
                    {
                        writes.Add(Write("reverse_direction", SafeBool(() => circular.ReverseDirection)?.ToString(), crd.ToString()));
                        circular.ReverseDirection = crd;
                    }

                    if (edit.SaveInitialOrientation is bool cso)
                    {
                        writes.Add(Write("save_initial_orientation",
                            SafeBool(() => circular.SaveInitialOrientation)?.ToString(), cso.ToString()));
                        circular.SaveInitialOrientation = cso;
                    }

                    if (edit.BuildingType is string cbt)
                    {
                        writes.Add(Write("building_type",
                            SafeString(() => circular.BuildingType.ToString()), cbt));
                        circular.BuildingType = CircularBuilding(cbt);
                    }

                    break;

                case IMirrorPattern mirror:
                    if (edit.SaveInitialObjects is bool mso)
                    {
                        writes.Add(Write("save_initial_objects",
                            SafeBool(() => mirror.SaveInitialObjects)?.ToString(), mso.ToString()));
                        mirror.SaveInitialObjects = mso;
                        // ЧТЕНИЕ СРАЗУ ПОСЛЕ ЗАПИСИ: правка обязана быть видна здесь же, иначе
                        // «записано, но моделью не принято» неотличимо от «записано и применено».
                        // Измерено на операции 48: запись false читается обратно как true, и это
                        // согласуется со справкой, ограничивающей свойство o3d_mirrorAllOperation.
                        var mirrorReadBack = SafeBool(() => mirror.SaveInitialObjects);
                        var mirrorRejected = mirrorReadBack is bool mrb && mrb != mso;
                        writes.Add(new NamedCheck(
                            "save_initial_objects_read_back",
                            Passed: !mirrorRejected,
                            Observed: $"прочитано сразу после записи {mirrorReadBack}",
                            Expected: mirrorRejected
                                ? $"{mso} — НЕ принято моделью: справка " +
                                  "imirrorpattern_saveinitialobjects.html ограничивает свойство " +
                                  "только o3d_mirrorAllOperation"
                                : $"{mso}"));
                    }

                    break;

                default:
                    return (false,
                        $"Объект массива не отвечает ни на один из интерфейсов ILinearPattern / " +
                        $"ICircularPattern / IMirrorPattern ({pattern.GetType().Name}): править нечем.",
                        writes);
            }

            if (writes.Count == 0)
            {
                return (false,
                    "Ни один параметр массива не задан: правка не выполнялась, признак не изменён.",
                    writes);
            }

            return pattern.Update()
                ? (true, null, writes)
                : (false,
                    $"{pattern.GetType().Name}.Update() вернул false — параметры записаны, но признак " +
                    "не перестроен",
                    writes);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (false, Describe(ex), writes);
        }
    }

    /// <summary>Инвариантная культура для чисел в тексте проверок: запятая вместо точки читалась бы как другое число.</summary>
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Прочитать параметры массива по индексу коллекции. Вид определяется по ответу на QI, а не по
    /// записанному типу: «чем объект отвечает» — факт, «каким его создавали» — память вызывающего.
    /// </summary>
    public static PatternReadout? Read(IModelContainer container, int index)
    {
        try
        {
            var patterns = container.FeaturePatterns;
            if (patterns is null || index < 0 || index >= patterns.Count)
            {
                return null;
            }

            if (patterns.FeaturePattern[index] is not { } pattern)
            {
                return null;
            }

            return ReadPattern(pattern);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Прочитать конкретный признак массива.</summary>
    public static PatternReadout ReadPattern(IFeaturePattern pattern)
    {
        var (count1, count2) = ExemplarCounts(pattern);

        if (pattern is ILinearPattern linear)
        {
            return new PatternReadout(
                Family: "linear",
                TypeName: SafeString(() => linear.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: SafeDouble(() => linear.Step1),
                Step2: SafeDouble(() => linear.Step2),
                Angle1Deg: SafeDouble(() => linear.Angle1),
                Angle2Deg: SafeDouble(() => linear.Angle2),
                Direction1: SafeBool(() => linear.Direction1),
                Direction2: SafeBool(() => linear.Direction2),
                Boundary1: SafeBool(() => linear.BoundaryInstancesStepFactor1),
                Boundary2: SafeBool(() => linear.BoundaryInstancesStepFactor2),
                BuildingType: SafeString(() => linear.BuildingType.ToString()),
                StepByAxis: null,
                ReverseDirection: null,
                SaveInitialOrientation: null,
                GeometryPattern: SafeBool(() => linear.GeometryPattern),
                AxisPresent: SafeBool(() => linear.Axis1 is not null),
                Axis2Present: SafeBool(() => linear.Axis2 is not null),
                PlanePresent: null,
                SaveInitialObjects: null,
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        if (pattern is ICircularPattern circular)
        {
            return new PatternReadout(
                Family: "circular",
                TypeName: SafeString(() => circular.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: SafeDouble(() => circular.Step1),
                Step2: SafeDouble(() => circular.Step2),
                Angle1Deg: null,
                Angle2Deg: null,
                Direction1: null,
                Direction2: null,
                Boundary1: SafeBool(() => circular.BoundaryInstancesStepFactor1),
                Boundary2: SafeBool(() => circular.BoundaryInstancesStepFactor2),
                BuildingType: SafeString(() => circular.BuildingType.ToString()),
                StepByAxis: SafeDouble(() => circular.StepByAxis),
                ReverseDirection: SafeBool(() => circular.ReverseDirection),
                SaveInitialOrientation: SafeBool(() => circular.SaveInitialOrientation),
                GeometryPattern: SafeBool(() => circular.GeometryPattern),
                AxisPresent: SafeBool(() => circular.Axis is not null),
                Axis2Present: null,
                PlanePresent: null,
                SaveInitialObjects: null,
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        if (pattern is IMirrorPattern mirror)
        {
            return new PatternReadout(
                Family: "mirror",
                TypeName: SafeString(() => mirror.ModelObjectType.ToString()),
                Count1: count1,
                Count2: count2,
                Step1: null,
                Step2: null,
                Angle1Deg: null,
                Angle2Deg: null,
                Direction1: null,
                Direction2: null,
                Boundary1: null,
                Boundary2: null,
                BuildingType: null,
                StepByAxis: null,
                ReverseDirection: null,
                SaveInitialOrientation: null,
                GeometryPattern: SafeBool(() => mirror.GeometryPattern),
                AxisPresent: null,
                Axis2Present: null,
                PlanePresent: SafeBool(() => mirror.Plane is not null),
                SaveInitialObjects: SafeBool(() => mirror.SaveInitialObjects),
                InitialObjectCount: InitialObjectCount(pattern),
                DeletedInstanceCount: DeletedInstanceCount(pattern),
                OwnerName: SafeString(() => pattern.Owner?.Name),
                UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
        }

        return new PatternReadout(
            Family: "unknown",
            TypeName: SafeString(() => pattern.ModelObjectType.ToString()),
            Count1: count1,
            Count2: count2,
            Step1: null,
            Step2: null,
            Angle1Deg: null,
            Angle2Deg: null,
            Direction1: null,
            Direction2: null,
            Boundary1: null,
            Boundary2: null,
            BuildingType: null,
            StepByAxis: null,
            ReverseDirection: null,
            SaveInitialOrientation: null,
            GeometryPattern: SafeBool(() => pattern.GeometryPattern),
            AxisPresent: null,
            Axis2Present: null,
            PlanePresent: null,
            SaveInitialObjects: null,
            InitialObjectCount: InitialObjectCount(pattern),
            DeletedInstanceCount: DeletedInstanceCount(pattern),
            OwnerName: SafeString(() => pattern.Owner?.Name),
            UpdateStamp: SafeInt(() => pattern.Owner?.UpdateStamp));
    }

    /// <summary>
    /// Число экземпляров по двум индексам. <c>GetExemplarsCounts(out, out)</c> возвращает
    /// <c>false</c>, когда признак ещё не построен, поэтому <c>false</c> — это «не прочитано»,
    /// а не «ноль экземпляров»: различить их обязан вызывающий, и здесь они и различены.
    /// </summary>
    public static (int? Count1, int? Count2) ExemplarCounts(IFeaturePattern pattern)
    {
        try
        {
            return pattern.GetExemplarsCounts(out var c1, out var c2) ? (c1, c2) : (null, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Экземпляр по индексам. Индексация у сетки ДВУМЕРНАЯ (<c>Exemplar(Index1, Index2)</c>),
    /// поэтому вызывающий перебирает обе координаты, а не одну.
    /// </summary>
    public static IModelObject? Exemplar(IFeaturePattern pattern, int index1, int index2)
    {
        try
        {
            return pattern.Exemplar[index1, index2];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int? InitialObjectCount(IFeaturePattern pattern)
    {
        try
        {
            return pattern.InitialObjects is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Число удалённых экземпляров. У зеркального массива пропусков экземпляров не существует
    /// (пользовательская справка <c>glava_48_obzhie_svedeniy</c>: исключение экземпляров недоступно
    /// для зеркального массива и массива по образцу), поэтому там поле читается, но объявляется
    /// неприменимым вызывающим, а не выдаётся за ноль пропусков.
    /// </summary>
    private static int? DeletedInstanceCount(IFeaturePattern pattern)
    {
        try
        {
            return pattern.InstanceDeletedIndexes is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool? SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? SafeString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COMException 0x{com.HResult:X8}: {com.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>
/// Прочитанные параметры признака массива. Поле <c>null</c> означает «не прочитано», а не «ноль»:
/// пустое поле и ноль — разные ответы, и смешивать их здесь нельзя так же, как у measure.
/// </summary>
/// <param name="Family">linear | circular | mirror | unknown — по ответу на QI, а не по памяти.</param>
/// <param name="DeletedInstanceCount">Число записей <c>InstanceDeletedIndexes</c>; у зеркала неприменимо.</param>
public sealed record PatternReadout(
    string Family,
    string? TypeName,
    int? Count1,
    int? Count2,
    double? Step1,
    double? Step2,
    double? Angle1Deg,
    double? Angle2Deg,
    bool? Direction1,
    bool? Direction2,
    bool? Boundary1,
    bool? Boundary2,
    string? BuildingType,
    double? StepByAxis,
    bool? ReverseDirection,
    bool? SaveInitialOrientation,
    bool? GeometryPattern,
    bool? AxisPresent,
    bool? Axis2Present,
    bool? PlanePresent,
    bool? SaveInitialObjects,
    int? InitialObjectCount,
    int? DeletedInstanceCount,
    string? OwnerName,
    int? UpdateStamp);
