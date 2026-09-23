namespace KompasMcp.Contracts.Ipc;

/// <summary>
/// Command names on the Host→Worker pipe. Adding a command means adding a handler in the
/// Worker; an unhandled name fails as CAPABILITY_UNAVAILABLE rather than being ignored.
/// </summary>
public static class WorkerCommands
{
    public const string EnvironmentProbe = "env.probe";
    public const string Ping = "sys.ping";
    public const string Connect = "app.connect";
    public const string Disconnect = "app.disconnect";
    public const string ListDocuments = "doc.list";
    public const string CreateDocument = "doc.create";
    public const string OpenDocument = "doc.open";
    public const string GetContext = "doc.context";
    public const string SaveDocument = "doc.save";
    public const string CloseDocument = "doc.close";
    public const string ListFeatures = "feat.list";
    public const string ListBodies = "body.list";
    public const string Measure = "geom.measure";
    public const string ResolveSelection = "geom.resolve";
    public const string ReadTopology = "topo.read";
    public const string CreateSketch = "sketch.create";
    public const string EditSketch = "sketch.edit";

    /// <summary>
    /// Смена ОПОРНОЙ плоскости СУЩЕСТВУЮЩЕГО эскиза документированным
    /// <c>ksSketchDefinition.SetPlane</c> («Изменить базовую плоскость эскиза»,
    /// <c>kssketchdefinition_setplane.html</c>), затем обязательный <c>sketch.Update()</c>.
    /// </summary>
    /// <remarks>
    /// ВТОРАЯ половина действия <c>edit</c> строки <c>AUX-SKETCH.plane_and_profile_lifecycle</c>:
    /// первая (профиль) выражается <c>kompas_edit_sketch</c>, опора до этого не выражалась ничем.
    /// Маршрут измерен пробой <c>tools/KompasMcp.Api7Probe --sketch-plane</c>; отчёт —
    /// <c>docs/acceptance/image/sketch-plane-probe-report.md</c>. Отдельная команда, а не поле чужой:
    /// расширение <c>kompas_edit_sketch</c> полем <c>plane</c> измерено как размывающее (301 чужая
    /// строка при нулевом приросте своих).
    /// </remarks>
    public const string SetSketchPlane = "sketch.set_plane";
    public const string FinishSketch = "sketch.finish";
    public const string Extrude = "feat.extrude";

    /// <summary>Скругление по явным ссылкам на рёбра конечного тела.</summary>
    public const string Fillet = "feat.fillet";

    /// <summary>
    /// Фаска по явным ссылкам на рёбра (docs/05 SM-11). Маршрут измерен пробой F от 12.09.2026:
    /// в API5 это <c>NewEntity(o3d_chamfer=33)</c> + <c>ksChamferDefinition.SetChamferParam</c>,
    /// режим «расстояние и угол» — только API7 (<c>IChamfer.Angle</c>).
    /// </summary>
    public const string Chamfer = "feat.chamfer";

    /// <summary>
    /// Родное отверстие (docs/05 SM-07). Режимы измерены пробой M от 16.09.2026 и достижимы только
    /// через API7 того же сеанса: параметры режима живут не на <c>IHole3D</c>, а на
    /// <c>HoleParameters</c>, приведённом к интерфейсу своего режима
    /// (<c>ISpotfacingHoleParameters</c>, <c>ICountersinkHoleParameters</c>). Позиция вне начала
    /// координат задаётся <c>IHoleDisposal.Point3DParamSurface</c> + <c>OffsetType=ksOffsetByCoords</c>.
    /// </summary>
    public const string Hole = "feat.hole";

    /// <summary>
    /// Вращение (docs/05 SM-03). Маршрут измерен 17.09.2026 (шаги R.24…R.26, прогон
    /// <c>95fa8441</c>) и лежит ЦЕЛИКОМ в API7: <c>IModelContainer.Rotateds.Add(type)</c> →
    /// <c>QI(IRotated)</c> → запись параметров → <c>Update()</c>. Оболочка API5
    /// (<c>NewEntity</c> + <c>Create()</c>) на этом объекте не работает — это измерено и было
    /// причиной многомесячной блокировки, а не свойство вращения.
    /// </summary>
    public const string Rotated = "feat.rotated";

    /// <summary>
    /// Кинематическая операция — «Элемент по траектории» (docs/05 SM-04).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут — документированный API5, и это следует из справки, а не из удобства.</b>
    /// <c>obj3dtype.html</c> документирует <c>o3d_baseEvolution = 45 → ksBaseEvolutionDefinition</c>,
    /// но <c>ievolutions_add.html</c> перечисляет допустимыми для <c>IEvolutions::Add</c> только
    /// <c>o3d_bossEvolution</c> и <c>o3d_cutEvolution</c> — базового типа в списке нет. Измерено
    /// 20.09.2026 (шаг B5.7): <c>IEvolutions.Add(45)</c> возвращает <c>KompasAPI7.EvolutionClass</c>,
    /// то есть объект ВЫДАЁТСЯ, но валидность тела по этому пути не измерялась и за документированный
    /// маршрут он не принимается. Рабочий маршрут — <c>ksPart.NewEntity(45)</c> +
    /// <c>ksBaseEvolutionDefinition</c>; он подтверждён объёмом (шаг B5.1).
    /// </remarks>
    public const string Sweep = "feat.sweep";

    /// <summary>
    /// Элемент по сечениям (docs/05 SM-05).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут — документированный API5, по той же причине, что у кинематической операции.</b>
    /// <c>obj3dtype.html</c> документирует <c>o3d_baseLoft = 30 → ksBaseLoftDefinition</c>, но
    /// <c>ilofts_add.html</c> перечисляет для <c>ILofts::Add</c> только <c>o3d_bossLoft</c> и
    /// <c>o3d_cutLoft</c>. Рабочий маршрут — <c>ksPart.NewEntity(30)</c> +
    /// <c>ksBaseLoftDefinition</c>, подтверждён объёмом <c>28000</c> (шаг B5.4).
    /// </remarks>
    public const string Loft = "feat.loft";

    /// <summary>
    /// Оболочка (docs/05 SM-13).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут — документированный API7 и он единственный из трёх, где базового типа не нужно:</b>
    /// <c>ishells_add.html</c> объявляет <c>IShells::Add()</c> без аргумента типа вовсе. Измерено
    /// (шаг B5.7): <c>IModelContainer.Shells.Add()</c> возвращает <c>KompasAPI7._ShellClass</c> и
    /// приводится к <c>IShell</c>. Объёмы подтверждены: 21632 / 24832 / 40256 (шаги B5.5, B5.6).
    /// </remarks>
    public const string Shell = "feat.shell";

    /// <summary>
    /// Булева операция над телами с явными целью и инструментами (docs/05 SM-15).
    /// </summary>
    /// <remarks>
    /// Маршрут измерен 18.09.2026 пробой <c>--boolean</c> (прогон <c>b10ffb70b7d24b6497417bc6639581b1</c>,
    /// PASS 13 · FAIL 0): <c>IModelContainer.Booleans.Add()</c> → <c>IBoolean</c>, поля
    /// <c>BaseObject</c> (цель), <c>ModifyObjects</c> (массив инструментов), <c>BooleanType</c>,
    /// <c>SaveCopyModifyObjects</c>, затем <c>Update()</c>.
    /// <para>
    /// Порядок операндов разности измерен: <b>цель минус инструменты</b>. Значения
    /// <c>ksBooleanType</c> — <c>ksIntersect=1</c>, <c>ksDifference=2</c>, <c>ksUnion=3</c> — взяты
    /// из перечисления, а не из каталога (каталог называл объединение нулём и ошибался).
    /// </para>
    /// <para>
    /// Ядро не отвергает повтор ссылки и не проверяет, что цель не входит в набор инструментов:
    /// обе проверки обязаны стоять в контракте, до вызова COM.
    /// </para>
    /// </remarks>
    public const string SolidBoolean = "solid.boolean";

    /// <summary>
    /// Разделение тела плоскостью на части (docs/05 SM-16).
    /// </summary>
    /// <remarks>
    /// Маршрут измерен 18.09.2026 пробой <c>--split</c> (прогон <c>124682af57a242728ea765f1aae4816c</c>,
    /// PASS 11 · FAIL 0): <c>IModelContainer.SplitSolids.Add()</c> → <c>ISplitSolid</c> с
    /// единственным содержательным членом <c>CutObjects</c>, затем <c>Update()</c>. Разделение
    /// сохраняет ВСЕ части по построению — отдельного члена «набор сохраняемых» не требуется, и
    /// именно это измерение сняло блокировку OQ-A18.
    /// </remarks>
    public const string SolidSplit = "solid.split";

    /// <summary>
    /// Отсечение тела плоскостью с выбором оставляемой стороны (docs/05 SM-16).
    /// </summary>
    /// <remarks>
    /// Маршрут измерен 18.09.2026 пробой <c>--split</c>, шаг SP.7: <c>IModelContainer.Cuts.Add()</c>
    /// → <c>ICut</c> с <c>BuildingType = ksCutByPlane</c>, <c>CutObject</c> = плоскость,
    /// <c>Direction</c> = выбор стороны, затем <c>Update()</c>.
    /// <para>
    /// Правило знака измерено: при нормали <c>(1,0,0)</c> и плоскости <c>x = 10</c>
    /// <c>Direction = true</c> оставляет сторону <b>в направлении нормали</b> (<c>s &gt; 0</c>,
    /// V = 18 000), <c>false</c> — противоположную (<c>s &lt; 0</c>, V = 6 000).
    /// </para>
    /// </remarks>
    public const string SolidCutByPlane = "solid.cut_by_plane";

    /// <summary>
    /// Перенос и поворот тела (docs/05 SM-17).
    /// </summary>
    /// <remarks>
    /// Маршрут измерен 18.09.2026 пробой <c>--reposition</c> (прогон <c>929f08886f1348fe921943052a4026b0</c>,
    /// PASS 10 · FAIL 0): <c>IModelContainer.BodyRepositions.Add()</c> → <c>IBodyReposition</c>,
    /// <c>RepositionBody</c> = тело, положение пишется <c>Position.InitByMatrix3D</c>, затем
    /// <c>Update()</c>.
    /// <para>
    /// <b>Положение пишет ТОЛЬКО однородная матрица 4×4</b> (OQ-A19): маршруты из 12 чисел
    /// («оси, затем начало» и «начало, затем оси») и <c>SetDisplacementByAxis</c> возвращают
    /// <c>Update() = true</c> и НЕ двигают тело. Успешный <c>Update()</c> здесь не является
    /// доказательством, поэтому адаптер обязан проверять положение после вызова, а не доверять
    /// возвращённому значению.
    /// </para>
    /// <para>
    /// Направление поворота измерено как правое правило: <c>(x,y) → (−y,x)</c>. На переносе
    /// раскладку строк и столбцов различить нельзя (единичный поворот симметричен), поэтому
    /// доказательством раскладки служит поворот, а не перенос.
    /// </para>
    /// </remarks>
    public const string SolidReposition = "solid.reposition";

    /// <summary>
    /// Параметрическая определённость существующего эскиза — тот статус, который КОМПАС показывает
    /// знаками «+», «−», «!».
    /// </summary>
    /// <remarks>
    /// Маршрут измерен 17.09.2026 пробой S (<c>docs/acceptance/api7/sketch-definition.md</c>, прогон
    /// <c>82880ed0b14a4e299bb0e93d7f8a7f2f</c>, PASS 9 · FAIL 0 · UNKNOWN 6) и лежит в API7:
    /// <c>TransferInterface(sketchEntity, ksAPI7Dual, 0)</c> → <c>ISketch.ConstraintsState</c> типа
    /// <c>ksConstraintsStateEnum</c>. Пятикратное чтение не изменило объём и счётчики топологии (S.7),
    /// поэтому команда исполняется как ЧТЕНИЕ — без <c>BeginEdit</c>, <c>Update</c> и перестроения.
    /// </remarks>
    public const string SketchStatus = "sketch.status";

    /// <summary>Чтение параметров существующего признака (docs/05 §7 kompas_get_feature).</summary>
    public const string GetFeature = "feat.get";
    /// <summary>Изменение параметров существующего признака на месте (docs/05 §4.3, §7).</summary>
    public const string UpdateFeature = "feat.update";

    /// <summary>
    /// Массив по сетке (docs/05 SM-18). Маршрут — только API7:
    /// <c>IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35)</c> → <c>QI(ILinearPattern)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Почему не API5.</b> Определения API5 (<c>ksMeshCopyDefinition</c>,
    /// <c>ksMeshPartArrayDefinition</c>) в дампе метаданных присутствуют, но продуктового маршрута
    /// через <c>NewEntity + Create()</c> у массивов нет: на вращении (SM-03) измерено, что оболочка
    /// API5 вокруг объекта фабрики API7 не строит ничего — <c>Create()</c> возвращает <c>true</c>,
    /// объект появляется в дереве, объём не меняется. Здесь повторять этот опыт не нужно, потому что
    /// маршрут API7 опубликован страницей SDK <c>copytype.html</c> («o3d_meshCopy 35 ILinearPattern»).
    /// </remarks>
    public const string PatternGrid = "pattern.grid";

    /// <summary>
    /// Массив по концентрической сетке (docs/05 SM-19):
    /// <c>FeaturePatterns.Add(o3d_circularCopy=36)</c> → <c>QI(ICircularPattern)</c>.
    /// </summary>
    public const string PatternCircular = "pattern.circular";

    /// <summary>
    /// Зеркальный массив (docs/05 SM-23): <c>FeaturePatterns.Add(o3d_mirrorOperation=48</c> либо
    /// <c>o3d_mirrorAllOperation=49)</c> → <c>QI(IMirrorPattern)</c>, у второго вида дополнительно
    /// <c>QI(IChooseBodies7)</c>.
    /// </summary>
    public const string PatternMirror = "pattern.mirror";

    /// <summary>Перечитать параметры существующего массива либо зеркала из модели.</summary>
    public const string PatternRead = "pattern.read";

    /// <summary>Подавление и восстановление признака (ksFeature.excluded, измерено пробой L.7).</summary>
    public const string SuppressFeature = "feat.suppress";

    /// <summary>Удаление признака с перечнем кандидатов зависимых до обращения к КОМПАС (проба L.8).</summary>
    public const string DeleteFeature = "feat.delete";
    public const string Rebuild = "doc.rebuild";
    public const string ExportStep = "export.step";
    public const string ImportStep = "import.step";
    public const string ExportImage = "export.image";
    public const string UnitProbe = "probe.units";
    public const string ListComponents = "asm.list_components";
    public const string InsertComponent = "asm.insert_component";
    public const string SetComponentTransform = "asm.set_transform";
    public const string CheckIntersections = "asm.check_intersections";
    public const string Shutdown = "sys.shutdown";

    /// <summary>
    /// Создание объекта вспомогательной геометрии детали — плоскости, оси или точки
    /// (<c>dep.refs.planes</c>, <c>dep.refs.axes</c>, <c>dep.refs.points_axes</c>).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут — документированный API7 и взят из справки v24</b> (шаг 0 наряда продуктовых
    /// маршрутов, отчёт <c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c> §6.1–6.3):
    /// <c>IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D</c>, <c>IModelContainer.GetPoints3D</c>,
    /// затем <c>Add(ksObj3dTypeEnum)</c> с типом из официальной таблицы <c>obj3dtype.html</c>:
    /// <c>o3d_planeOffset</c> = 14, <c>o3d_planeAngle</c> = 15, <c>o3d_axis2Points</c> = 10,
    /// <c>o3d_axisConeFace</c> = 11, <c>o3d_axisEdge</c> = 12, <c>o3d_point3D</c> = 70.
    /// </remarks>
    public const string CreateAuxGeometry = "aux.create";

    /// <summary>
    /// Перечисление и чтение объектов вспомогательной геометрии детали: плоскости, оси, точки.
    /// </summary>
    /// <remarks>
    /// <b>Имя как адрес разрешается перечислением, и это ИЗМЕРЕНО, а не выбрано.</b> Справка
    /// документирует <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c> и
    /// <c>ISketchs.GetSketchByName</c>, но в поставленном <c>Interop.KompasAPI7.dll</c> целевой
    /// сборки нет НИ ОДНОГО члена с подстрокой <c>ByName</c> (прибор
    /// <c>tools/KompasMcp.InteropScan</c>, 21.09.2026). Поэтому имя сопоставляется перечислением
    /// коллекции по документированным членам <c>Count</c> + индексированное свойство + <c>Name</c>.
    /// Возвращаемый индекс коллекции НЕ объявляется устойчивым адресом: перестроение его сдвигает.
    /// </remarks>
    public const string ListAuxGeometry = "aux.list";

    /// <summary>
    /// Правка УЖЕ СОЗДАННОЙ плоскости как объекта детали: смещение, угол наклона, опора
    /// (<c>dep.refs.planes</c>, действие <c>edit</c>).
    /// </summary>
    /// <remarks>
    /// <b>Это отдельный маршрут, а не поле чужого вызова.</b> Измерено строкой
    /// <c>DEP.DPL.04.edit</c> прежнего прогона: правка признака полем <c>plane</c> ПРИНИМАЛАСЬ
    /// (код отказа <c>None</c>) и геометрию НЕ меняла. Здесь правка идёт документированными
    /// сеттерами <c>IPlane3DByOffset.Offset</c> / <c>IPlane3DByAngle.Angle</c> /
    /// <c>IPlane3DBy*.BasePlane</c>, затем <c>Update()</c> и <c>RebuildModel</c>, а ответ несёт
    /// ПРОЧИТАННОЕ ОБРАТНО значение: успешный код не выдаётся за применённую правку.
    /// </remarks>
    public const string UpdatePlane = "aux.update_plane";

    /// <summary>
    /// Перечисление сущностей СУЩЕСТВУЮЩЕГО эскиза с УСТОЙЧИВЫМ АДРЕСОМ
    /// (<c>dep.sketch.entities</c>, действия <c>discover</c> и <c>read</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Маршрут документирован справкой v24: <c>ISketch.BeginEditEx(true)</c> →
    /// <c>IFragmentDocument.ViewsAndLayersManager.Views</c> → <c>IView</c> →
    /// <c>IDrawingContainer.GetObjects(ksAllObj)</c> → адрес <c>IKompasDocument1.GetObjectId</c> →
    /// <c>ISketch.EndEdit()</c>.
    /// </para>
    /// <para>
    /// Адрес — не «N-й объект коллекции» и не координата: это строка <c>GetObjectId</c>, которую
    /// <c>FindObjectById</c> принимает обратно, поэтому она переживает перестроение модели и
    /// переоткрытие документа. Индекс коллекции устойчивым адресом НЕ объявляется.
    /// </para>
    /// </remarks>
    public const string ListSketchEntities = "sketch.entities";

    /// <summary>
    /// Адресная правка ОДНОЙ существующей сущности эскиза (<c>dep.sketch.entities</c>, действие
    /// <c>edit</c>).
    /// </summary>
    /// <remarks>
    /// Прежняя схема правки эскиза принимала только режим (<c>append</c>/<c>replace</c>/
    /// <c>delete_entities</c>) и НОВЫЙ НАБОР примитивов целиком — то есть пересоздавала контур, а
    /// не правила сущность. Измерено различающим контролем строки <c>DEP.DSE.04.edit</c>: после
    /// <c>replace</c> объём базового тела менялся с 80000 на 24000, то есть контур был ПЕРЕСОЗДАН.
    /// Здесь правка адресует ровно одну сущность, а ответ несёт прочитанное ПОСЛЕ правки
    /// состояние — «принято» и «применено» различаются измерением, а не формулировкой.
    /// </remarks>
    public const string EditSketchEntity = "sketch.entity_edit";
}

/// <summary>
/// Запрос создания объекта вспомогательной геометрии.
/// </summary>
/// <remarks>
/// <para>
/// <b>Вид и способ разделены, а не слиты в одно поле.</b> <see cref="Kind"/> отвечает «что это»
/// (плоскость, ось, точка), <see cref="Mode"/> — «как построено». Одно поле с шестью значениями
/// сделало бы ответ неоднозначным: «angle» без вида не говорит, угол чего.
/// </para>
/// <para>
/// <b>Поля чужих способов не игнорируются молча.</b> Каждый способ объявляет свой набор; поле вне
/// набора отвергается <c>INVALID_ARGUMENT</c> с перечнем своих полей. Принятое и проигнорированное
/// поле доживает до приёмки, выглядя как выполненная работа, — это ровно тот дефект, который
/// контракт запрещает.
/// </para>
/// </remarks>
public sealed record CreateAuxGeometryCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary><c>plane</c>, <c>axis</c> либо <c>point</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Способ построения. Для плоскости: <c>offset</c> (смещение вдоль нормали) либо <c>angle</c>
    /// (наклон вокруг базовой прямой). Для оси: <c>by_2_points</c>, <c>by_face</c> (цилиндрическая
    /// или коническая поверхность), <c>by_edge</c>. Для точки: <c>coordinates</c> либо
    /// <c>displace</c> (смещение от опорной вершины).
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>Смещение в мм. Только для <c>plane/offset</c>.</summary>
    public double? OffsetMm { get; init; }

    /// <summary>Угол в градусах. Только для <c>plane/angle</c>.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>
    /// Знак направления. У смещённой плоскости — вдоль нормали или против; у наклонной — сторона
    /// отсчёта угла. <c>null</c> означает «не задано», и тогда берётся документированное умолчание
    /// самого КОМПАСа, а не наше: подставлять знак от себя значило бы выдать догадку за параметр.
    /// </summary>
    public bool? Direction { get; init; }

    /// <summary>Базовая плоскость именем: <c>xy</c>, <c>xz</c> либо <c>yz</c>. Только для плоскости.</summary>
    public string? BasePlane { get; init; }

    /// <summary>Базовая прямая (ось наклона) — ссылка на ось. Только для <c>plane/angle</c>.</summary>
    public string? BaseAxisRef { get; init; }

    /// <summary>
    /// Опора — ПЛОСКАЯ ГРАНЬ ссылкой. Для <c>plane/offset</c> это документированная опора
    /// (<c>IPlane3DByOffset.BasePlane</c> принимает «базовую плоскость ИЛИ плоскую грань»).
    /// </summary>
    public string? BaseFaceRef { get; init; }

    /// <summary>Первая точка оси. Только для <c>axis/by_2_points</c>.</summary>
    public double[]? Point1Mm { get; init; }

    /// <summary>Вторая точка оси. Только для <c>axis/by_2_points</c>.</summary>
    public double[]? Point2Mm { get; init; }

    /// <summary>Грань оси. Только для <c>axis/by_face</c>.</summary>
    public string? FaceRef { get; init; }

    /// <summary>Ребро оси. Только для <c>axis/by_edge</c>.</summary>
    public string? EdgeRef { get; init; }

    /// <summary>Координаты точки. Только для <c>point/coordinates</c>.</summary>
    public double[]? CoordinatesMm { get; init; }

    /// <summary>Опорная вершина точки. Только для <c>point/displace</c>.</summary>
    public string? AssociationVertexRef { get; init; }

    /// <summary>Смещение точки от опорной вершины. Только для <c>point/displace</c>.</summary>
    public double[]? DisplacementMm { get; init; }

    /// <summary>
    /// Имя создаваемого объекта. Необязательно: без него КОМПАС даёт своё. Имя — не адрес:
    /// адресом служит ссылка, выданная перечислением.
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>
/// Результат создания объекта вспомогательной геометрии: прочитанное ОБРАТНО из модели, а не
/// пересказ запроса.
/// </summary>
public sealed record AuxGeometryResult(
    string Kind,
    string Mode,
    string? ReferenceId,
    string? Name,
    string? SubKind,
    double[]? PointMm,
    double[]? DirectionMm,
    double? AngleDeg,
    double? OffsetMm,
    bool? Direction,
    string? BaseName,
    string? LineName,
    int PlaneCount,
    int AxisCount,
    int PointCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Запрос перечисления объектов вспомогательной геометрии.</summary>
public sealed record ListAuxGeometryCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Что читать: <c>planes</c>, <c>axes</c>, <c>points</c> либо <c>all</c>.</summary>
    public required string Include { get; init; }

    /// <summary>Имя для адресации ОДНОГО объекта. Необязательно.</summary>
    public string? Name { get; init; }

    /// <summary>Предел числа строк. Ограничение названо числом: цена строки — вызов COM.</summary>
    public int? Limit { get; init; }
}

/// <summary>Ответ перечисления: строки, маршрут и счётчики коллекций.</summary>
public sealed record AuxGeometryListResult(
    IReadOnlyList<AuxGeometryRowDto> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>
/// Строка перечисления вспомогательной геометрии. Пустое поле означает «не прочитано», а не ноль;
/// причина называется в <see cref="Notes"/>.
/// </summary>
public sealed record AuxGeometryRowDto(
    string Kind,
    int Index,
    string? Name,
    string? SubKind,
    double[]? PointMm,
    double[]? DirectionMm,
    double? AngleDeg,
    double? OffsetMm,
    bool? Direction,
    string? BaseName,
    string? LineName,
    IReadOnlyList<string> Notes);

/// <summary>
/// Запрос правки существующей плоскости. Ровно ОДНО из <see cref="OffsetMm"/> и
/// <see cref="AngleDeg"/> задаётся, и оно обязано соответствовать виду плоскости: у смещённой
/// плоскости угла нет, у наклонной нет смещения.
/// </summary>
/// <remarks>
/// Поле чужого вида отвергается <c>INVALID_ARGUMENT</c> с перечнем допустимых — по той же причине,
/// по которой это делает создание: принятое и проигнорированное поле доживает до приёмки,
/// выглядя как выполненная правка.
/// </remarks>
public sealed record UpdatePlaneCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Ссылка на плоскость, выданная созданием либо перечислением.</summary>
    public required string PlaneRef { get; init; }

    /// <summary>Новое смещение вдоль нормали, мм. Только для смещённой плоскости.</summary>
    public double? OffsetMm { get; init; }

    /// <summary>Новый угол наклона, градусы. Только для наклонной плоскости.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>Новая опора именем: <c>xy</c>, <c>xz</c> либо <c>yz</c>.</summary>
    public string? BasePlane { get; init; }

    /// <summary>Знак направления: вдоль нормали или против, сторона отсчёта угла.</summary>
    public bool? Direction { get; init; }
}

/// <summary>
/// Результат правки плоскости: значение, ПРОЧИТАННОЕ ОБРАТНО из модели, и признак применения,
/// выведенный из сравнения запрошенного с прочитанным.
/// </summary>
public sealed record PlaneUpdateResult(
    string? ReferenceId,
    string? Kind,
    string? SubKind,
    string? Name,
    double? OffsetMm,
    double? AngleDeg,
    bool? Direction,
    string? BaseName,
    double[]? PointMm,
    double[]? DirectionMm,
    bool Applied,
    string? AppliedEvidence,
    int PlaneCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>Запрос перечисления сущностей эскиза.</summary>
public sealed record ListSketchEntitiesCommand
{
    public required string SketchRef { get; init; }

    /// <summary>Адрес ОДНОЙ сущности. Необязательно: без него перечисляются все.</summary>
    public string? Address { get; init; }

    /// <summary>Предел числа строк: цена строки — вызов COM, предел назван числом.</summary>
    public int? Limit { get; init; }
}

/// <summary>Ответ перечисления сущностей эскиза: строки, счётчики коллекций, маршрут, заметки.</summary>
public sealed record SketchEntitiesResult(
    IReadOnlyList<SketchEntityRowDto> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>
/// Строка перечисления сущности эскиза. Пустое поле означает «не прочитано», а не ноль; причина
/// называется в <see cref="Notes"/>.
/// </summary>
public sealed record SketchEntityRowDto(
    int Index,
    string? Address,
    string? Kind,
    string? Name,
    int? TypeCode,
    IReadOnlyList<string> Notes);

/// <summary>
/// Запрос адресной правки сущности эскиза. <see cref="Address"/> обязателен: правка «первой
/// попавшейся» сущности не является адресной.
/// </summary>
public sealed record EditSketchEntityCommand
{
    public required string SketchRef { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string Address { get; init; }

    /// <summary><c>set_layer</c> — назначить номер слоя, <c>delete</c> — удалить сущность.</summary>
    public required string Action { get; init; }

    /// <summary>Номер слоя. Обязателен при <c>action = set_layer</c>.</summary>
    public int? LayerNumber { get; init; }
}

/// <summary>
/// Результат адресной правки: состояние, ПРОЧИТАННОЕ ПОСЛЕ правки тем же адресом, и признак
/// применения. «Код не отказал» применением не объявляется.
/// </summary>
public sealed record SketchEntityEditResult(
    string? Address,
    string Action,
    bool Applied,
    bool? ResolvedAfter,
    int? LayerAfter,
    string? KindAfter,
    int EntityCountBefore,
    int EntityCountAfter,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of <see cref="WorkerCommands.EnvironmentProbe"/> — the P0 evidence record.</summary>
public sealed record EnvironmentProbeResult
{
    public required string WorkerRuntime { get; init; }

    public required string ProcessBitness { get; init; }

    /// <summary>ProgID → CLSID resolution actually observed in the registry.</summary>
    public required IReadOnlyDictionary<string, string?> ProgIdMap { get; init; }

    public required string? RegisteredServerPath { get; init; }

    public required string ServerPathBitness { get; init; }

    /// <summary>Live КОМПАС processes found by the OS, with window state. Read-only observation.</summary>
    public required IReadOnlyList<RunningInstanceInfo> RunningInstances { get; init; }

    /// <summary>Number of КОМПАС objects visible in the running object table.</summary>
    public required int RotEntryCount { get; init; }

    public required IReadOnlyList<string> InteropAssemblies { get; init; }

    public required IReadOnlyList<string> TypeLibraries { get; init; }

    /// <summary>Anything the probe could not determine — surfaced, not hidden.</summary>
    public IReadOnlyList<string> Unknowns { get; init; } = Array.Empty<string>();
}

public sealed record RunningInstanceInfo(int ProcessId, string ProcessName, bool HasMainWindow, string? MainWindowTitle, DateTimeOffset? StartTimeUtc, long WorkingSetBytes);

/// <summary>Payload of <see cref="WorkerCommands.Connect"/>.</summary>
public sealed record ConnectCommand
{
    public required ConnectMode Mode { get; init; }

    /// <summary>
    /// Explicit selector. Null means "attach is only allowed if exactly one candidate exists".
    /// Never resolves to "whichever object the ROT handed back first".
    /// </summary>
    public int? ProcessId { get; init; }

    public string? WindowTitle { get; init; }

    public string? ApplicationId { get; init; }

    public bool MakeVisible { get; init; }
}

public sealed record ConnectConflict
{
    public required IReadOnlyList<RunningInstanceInfo> Candidates { get; init; }

    /// <summary>ROT entries grouped by PID; more than one PID means the caller must choose.</summary>
    public required IReadOnlyList<int> RotProcessIds { get; init; }
}

public sealed record DisconnectCommand
{
    public required string ApplicationId { get; init; }

    public bool CloseOwnedApplication { get; init; }
}

public sealed record ListDocumentsCommand
{
    public required string ApplicationId { get; init; }
}

public sealed record CreateDocumentCommand
{
    public required string ApplicationId { get; init; }

    public required DocumentKind Kind { get; init; }

    public string? Name { get; init; }

    public string? Marking { get; init; }
}

public sealed record OpenDocumentCommand
{
    public required string ApplicationId { get; init; }

    /// <summary>Absolute, already policy-checked by the Host.</summary>
    public required string Path { get; init; }

    public required DocumentAccess Access { get; init; }
}

public sealed record GetContextCommand
{
    public required string DocumentId { get; init; }

    public string Detail { get; init; } = "minimal";
}

public sealed record SaveDocumentCommand
{
    public required string DocumentId { get; init; }

    public string? TargetPath { get; init; }

    public required long ExpectedRevision { get; init; }
}

public sealed record CloseDocumentCommand
{
    public required string DocumentId { get; init; }

    public required DirtyPolicy DirtyPolicy { get; init; }
}

public sealed record ListFeaturesCommand
{
    public required string DocumentId { get; init; }
}

public sealed record ListBodiesCommand
{
    public required string DocumentId { get; init; }
}

public sealed record MeasureCommand
{
    public required string TargetRef { get; init; }

    public required IReadOnlyList<MeasurableProperty> Properties { get; init; }

    /// <summary>Density in kg/m³, supplied by the caller. Server never guesses it.</summary>
    public double? DensityKgPerM3 { get; init; }
}

public sealed record ResolveSelectionCommand
{
    public required string BodyRef { get; init; }

    public required SelectionPredicateDto Predicate { get; init; }

    public bool RequireUnique { get; init; } = true;

    public int Limit { get; init; } = 100;
}

public sealed record ReadTopologyCommand
{
    public required string DocumentId { get; init; }

    public required string BodyRef { get; init; }

    /// <summary>faces | edges | both</summary>
    public required string Include { get; init; }
}

public sealed record CreateSketchCommand
{
    public required string DocumentId { get; init; }

    public required PlaneRefDto Plane { get; init; }

    public string? Name { get; init; }

    /// <summary>Revision the caller read. Enforced before any COM call, never advisory.</summary>
    public required long ExpectedRevision { get; init; }
}

public sealed record EditSketchCommand
{
    public required string SketchRef { get; init; }

    public required SketchEditMode Mode { get; init; }

    public required IReadOnlyList<SketchEntityDto> Entities { get; init; }

    /// <summary>Revision the caller read for the owning document.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Запрос смены опорной плоскости существующего эскиза (команда
/// <see cref="WorkerCommands.SetSketchPlane"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Форма <see cref="Plane"/> — ТА ЖЕ, что у создания эскиза:</b> <c>base</c>+<c>offset_mm</c>
/// ЛИБО <c>reference</c>, одновременно ровно одно. Второй диалект опоры не заводится: две формы
/// одного понятия расходятся, и расхождение выглядит как разница поведения продукта.
/// </para>
/// <para>
/// <b>Отказ не-плоскости происходит ДО COM и называется кодом.</b> Измерено (проба
/// <c>--sketch-plane</c>, шаги SP.8/SP.9): ядро ПРИНИМАЕТ в опору плоскую грань — все шесть граней
/// коробки перепривязывают зависимое тело, — и ОТВЕРГАЕТ ребро и тело (<c>SetPlane = False</c>).
/// Продукт этот ответ не наследует: <c>reference</c>, ведущая не на плоскость, отвергается по ВИДУ
/// ссылки из реестра, без обращения к COM.
/// </para>
/// </remarks>
public sealed record SetSketchPlaneCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Ссылка на эскиз, выданная созданием либо чтением признака.</summary>
    public required string SketchRef { get; init; }

    public required PlaneRefDto Plane { get; init; }

    /// <summary>Ревизия, которую прочитал вызывающий. Проверяется до обращения к COM.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Результат смены опоры: опора, ПРОЧИТАННАЯ ОБРАТНО документированным <c>GetPlane()</c>, и
/// состояние зависимого тела до и после — то, чем «принято» отличается от «применено».
/// </summary>
/// <remarks>
/// <b>Две половины называются раздельно, и ни одна не выдаётся за другую.</b> Чтение опоры отвечает
/// на вопрос «записалась ли опора»; габарит и объём зависимого тела — на вопрос «перестроилась ли
/// модель». Измерено, что при смене опоры на другую перестраивает именно <c>sketch.Update()</c>,
/// поэтому <see cref="ApplyRoute"/> несёт ФАКТИЧЕСКИ вызванный маршрут, а не намерение.
/// </remarks>
public sealed record SetSketchPlaneResult(
    string SketchRef,
    string? SupportTypeBefore,
    string? SupportTypeAfter,
    string? SupportNameAfter,
    string? GabaritBefore,
    string? GabaritAfter,
    double? VolumeBefore,
    double? VolumeAfter,
    bool GeometryChanged,
    string ApplyRoute,
    IReadOnlyList<string> Diagnostics);

public sealed record FinishSketchCommand
{
    public required string SketchRef { get; init; }

    public bool RequireClosedProfile { get; init; } = true;
}

public sealed record ExtrudeCommand
{
    public required string SketchRef { get; init; }

    public required ExtrudeOperation Operation { get; init; }

    /// <summary>
    /// Depth in mm. Mandatory for <see cref="ExtrudeEndCondition.Blind"/>, forbidden for
    /// <see cref="ExtrudeEndCondition.Through"/> — the Host rejects either mistake before COM,
    /// because through-all ignores the number entirely (probe P2.1).
    /// </summary>
    public double? DepthMm { get; init; }

    public ExtrudeEndCondition EndCondition { get; init; } = ExtrudeEndCondition.Blind;

    public required ExtrudeDirection Direction { get; init; }

    public string? TargetBodyRef { get; init; }

    /// <summary>Revision the caller read; mismatch is REVISION_CONFLICT before anything is created.</summary>
    public required long ExpectedRevision { get; init; }
}

/// <summary>Скругление выбранных рёбер (docs/03 G04, docs/05 SM-09).</summary>
public sealed record FilletCommand
{
    /// <summary>
    /// Явные <c>edge:</c>-ссылки, полученные kompas_read_topology. Номера позиций в коллекции не
    /// принимаются: docs/05 §6.5 запрещает использовать индексы как постоянные идентификаторы.
    /// </summary>
    public required IReadOnlyList<string> EdgeRefs { get; init; }

    public required double RadiusMm { get; init; }

    /// <summary>
    /// Аналитическое ожидание изменения объёма, если оно выводимо у вызывающего (например
    /// 4·(1−π/4)·r²·h для четырёх параллельных рёбер). Сервер сверяет с ним измерение и не выдаёт
    /// geometry_checked без совпадения. Без него подтверждается только чтением радиуса обратно, и
    /// результат честно помечается недоказанной геометрией.
    /// </summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

public sealed record RebuildCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>
/// Фаска по явным рёбрам (docs/05 SM-11). Способ задаётся явно: двух катетов хватает не для
/// каждого режима, а угол в API5-определении отсутствует физически.
/// </summary>
public sealed record ChamferCommand
{
    /// <summary>
    /// Явные <c>edge:</c>-ссылки из kompas_read_topology. Позиции в коллекции не принимаются:
    /// docs/05 §6.5 запрещает использовать индексы как постоянные идентификаторы.
    /// </summary>
    public required IReadOnlyList<string> EdgeRefs { get; init; }

    /// <summary>two_distances (API5) или distance_angle (API7); см. <see cref="ChamferMode"/>.</summary>
    public required ChamferMode Mode { get; init; }

    /// <summary>Первый катет, мм. Единицы измерены пробой F: число передаётся в API как есть и даёт мм.</summary>
    public required double Distance1Mm { get; init; }

    /// <summary>Второй катет, мм. Обязателен при two_distances, запрещён при distance_angle.</summary>
    public double? Distance2Mm { get; init; }

    /// <summary>
    /// Угол фаски в ГРАДУСАХ — измерено пробой F.10 (Angle=30 при Distance1=2 снял
    /// 20·d·(d·tg 30°) = 46.188021535141 мм³, радианная гипотеза отвергнута числом).
    /// Обязателен при distance_angle, запрещён при two_distances.
    /// </summary>
    public double? AngleDeg { get; init; }

    /// <summary>
    /// Сторона фаски: в API5 это параметр <c>transfer</c>, в API7 — <c>IChamfer.Direction</c>.
    /// Измерено (F.4, F.11): при неравных катетах значение меняет, какой катет ложится на какую
    /// грань; объём при этом не различается, поэтому различатор — площади боковых граней.
    /// </summary>
    public bool Direction { get; init; }

    /// <summary>
    /// Аналитическое ожидание уменьшения объёма. Для N параллельных прямых рёбер длиной L
    /// это N·(d₁·d₂/2)·L; без него подтверждается только направление изменения.
    /// </summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Родное отверстие (docs/05 SM-07). Режим назван явно, потому что поля у режимов разные, а
/// сервер обязан отклонять несочетаемое до обращения в COM, а не применять половину.
/// </summary>
/// <remarks>
/// <para>
/// Опорная грань задаётся ссылкой <c>face:</c>, а не «верхней гранью тела»: <c>IChamfer.BaseObjects</c>
/// и <c>IHoleDisposal.BaseSurface</c> принимают объект, и выбрать его за клиента значило бы
/// выдумать опору. Проба M брала самую большую грань по площади, но это был приём пробы, а не
/// контракт.
/// </para>
/// <para>
/// Позиция — необязательная пара координат НА опорной грани (измерено M.5: отверстие Ø10 встало
/// ровно в (25, 15)). Без неё отверстие остаётся в начале координат поверхности.
/// </para>
/// </remarks>
public sealed record HoleCommand
{
    /// <summary>Явная <c>face:</c>-ссылка из kompas_read_topology: поверхность, с которой начинается отверстие.</summary>
    public required string FaceRef { get; init; }

    /// <summary>blind_flat (API7: ksDTValue + ksEFFlat), through_counterbore (M.2), through_countersink (M.3).</summary>
    public required HoleMode Mode { get; init; }

    /// <summary>Диаметр отверстия, мм: у цековки и зенковки это диаметр ПИЛОТА, а не выточки и не устья.</summary>
    public required double DiameterMm { get; init; }

    /// <summary>Глубина, мм. Только при mode=blind_flat; при сквозных режимах запрещена.</summary>
    public double? DepthMm { get; init; }

    /// <summary>Диаметр выточки, мм (цековка, M.2). Должен быть больше диаметра пилота.</summary>
    public double? CounterboreDiameterMm { get; init; }

    /// <summary>Глубина выточки, мм (цековка, M.2).</summary>
    public double? CounterboreDepthMm { get; init; }

    /// <summary>Диаметр устья зенковки, мм (M.3). Должен быть больше диаметра пилота.</summary>
    public double? CountersinkDiameterMm { get; init; }

    /// <summary>
    /// Угол зенковки в ГРАДУСАХ, строго между 0 и 180 (M.3: таблица из трёх углов — 60, 90, 120).
    /// Глубина при способе «диаметр + угол» ПРОИЗВОДНА: объект возвращает
    /// <c>(rM − rP)/tan(угол/2)</c>, где <c>rM</c> — радиус устья, <c>rP</c> — радиус пилота
    /// (измерено N.2: серия устьев Ø14/16/18/20/24 при пилоте Ø10 и 90° дала h = 2/3/4/5/7).
    /// </summary>
    public double? CountersinkAngleDeg { get; init; }

    /// <summary>Смещение центра отверстия вдоль X опорной грани, мм. Вместе с <see cref="OffsetYMm"/>.</summary>
    public double? OffsetXMm { get; init; }

    /// <summary>Смещение центра отверстия вдоль Y опорной грани, мм.</summary>
    public double? OffsetYMm { get; init; }

    /// <summary>
    /// Аналитическое ожидание уменьшения объёма, если выводимо у вызывающего:
    /// <c>π·r²·h</c> (глухое), <c>π·r²·h + π/4·(D²−d²)·h_выточки</c> (цековка),
    /// <c>π·r²·h + π·h_факт/3·(rM² + rP·rM − 2·rP²)</c> (зенковка, причём <c>h_факт</c> —
    /// глубина, которую вернул объект, а не запрошенная). Без него подтверждается только чтение
    /// параметров обратно, и результат честно помечается недоказанной геометрией.
    /// </summary>
    public double? ExpectedVolumeDeltaMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Вращение (docs/05 SM-03). Вид операции назван явно и заранее: измерено 18.09.2026, что
/// <c>OperationResult</c> ВЫБИРАЕТ исход, а вид фабрики (<c>Rotateds.Add(27/28/29)</c>) объявляет,
/// какие значения для него допустимы. Управляемый опыт на одной геометрии: <c>ksOperationUnion</c>
/// даёт сращивание (тел 1→1), <c>ksOperationNewBody</c> — второе тело (тел 1→2). Прежнее
/// утверждение «этот член не переключает действие» происходило из опыта, который писал пару,
/// недопустимую для своего вида, и потому измерял отказ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ось обязательна и строится в ТОЙ ЖЕ детали.</b> Измерено (R.24/R.25): вращение, записанное
/// без оси, не строится вовсе — <c>Update()</c> возвращает False и тел остаётся 0. Ось подаётся
/// двумя точками в координатах МОДЕЛИ: сервер строит её сам как <c>o3d_axis2Points</c> в этой же
/// детали, потому что <c>IRotated.Axis</c> принимает модельный объект, а не линию эскиза, и чужая
/// ось из другого документа не подставляется — ссылки между документами не переносятся.
/// </para>
/// <para>
/// <b>Угол задаётся в ГРАДУСАХ и равен построенному, вплоть до полного оборота.</b> Прежнее
/// утверждение «угол насыщается на 180°, запись 360 даёт половину» ОПРОВЕРГНУТО измерением
/// 18.09.2026 (проба <c>FullTurnProbe</c>, шаги F.1…F.5; независимое чтение <c>.m3d</c> пробой
/// <c>M3dVerificationProbe</c>; разбор — <c>docs/acceptance/api7/full-turn-findings.md</c>).
/// Измерено: <c>Angle[true]</c> несёт запрошенный угол напрямую (90→90°, 180→180°, 360→360°), а
/// вторая половина пары, равная первой, развёртку удваивает. Поэтому полный оборот достигается
/// одним вызовом с <c>angle_deg = 360</c>, а отказ начинается только выше 360 — это потолок
/// сектора, а не прежний неверный предел.
/// </para>
/// <para>
/// <b>Тонкая стенка не проверялась.</b> Маршрут измерен на сплошном теле
/// (<c>IThinParameters.Thin = false</c>); <see cref="ThinWallMm"/> объявлен, но задавать его
/// нельзя — сервер отказывает CAPABILITY_UNAVAILABLE, а не записывает незмеренное число.
/// </para>
/// </remarks>
public sealed record RotatedCommand
{
    /// <summary>Эскиз-профиль: явная <c>sketch:</c>-ссылка из kompas_create_sketch / kompas_get_feature.</summary>
    public required string SketchRef { get; init; }

    /// <summary>Вид операции. Решает действие сам вызов фабрики, а не OperationResult.</summary>
    public required RotationOperation Operation { get; init; }

    /// <summary>
    /// Угол в ГРАДУСАХ, строго больше 0 и не больше 360. Полный оборот (360) строится одним
    /// вызовом: измерено 18.09.2026, что запись даёт полный цилиндр (проба F.1a:
    /// <c>V=50265.4824574366</c> при r=20, h=40), подтверждено слепым чтением сохранённого
    /// <c>.m3d</c>. Значение больше 360 отвергается до мутации: сектор не может занять больше
    /// целого оборота. Прежний предел 180 происходил из опровергнутого измерения (шаг менял
    /// <c>CutOffByPoint</c>, а не угол) — см. <c>docs/acceptance/api7/full-turn-findings.md</c>.
    /// </summary>
    public required double AngleDeg { get; init; }

    /// <summary>Первая точка оси в координатах модели, мм.</summary>
    public required IReadOnlyList<double> AxisPoint1Mm { get; init; }

    /// <summary>Вторая точка оси в координатах модели, мм. Обязана отличаться от первой.</summary>
    public required IReadOnlyList<double> AxisPoint2Mm { get; init; }

    /// <summary>
    /// Направление. <see cref="RotationDirection.Reverse"/> отвергается до мутации: измерено
    /// (R.26.sector), что это значение не строит ничего.
    /// </summary>
    public RotationDirection Direction { get; init; } = RotationDirection.Normal;

    /// <summary>
    /// Целевое тело для boss и cut — явная <c>body:</c>-ссылка. Для boss и cut обязательна:
    /// приклеивать и резать «вообще» значит выбрать тело за клиента, а изделие из нескольких тел
    /// такого выбора не прощает. Для base запрещена.
    /// </summary>
    public string? TargetBodyRef { get; init; }

    /// <summary>
    /// Тонкая стенка, мм. Не задана — тело сплошное, и это измеренная настройка маршрута:
    /// <c>IThinParameters.Thin = false</c> (R.24/R.25). Тонкая стенка не проверялась ни одним
    /// прогоном, поэтому, если она задана, сервер откажет CAPABILITY_UNAVAILABLE, а не поставит
    /// число, которое не измерено.
    /// </summary>
    public double? ThinWallMm { get; init; }

    /// <summary>
    /// Аналитическое ожидание объёма ПОСЛЕ операции, если оно выводимо у вызывающего. Без него
    /// подтверждается только чтение параметров обратно, и результат честно помечается недоказанной
    /// геометрией.
    /// </summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Массив по сетке (docs/05 SM-18, профиль <c>mechanical-core-v1</c>, очередь B4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ось подаётся двумя точками МОДЕЛИ, а не ссылкой.</b> Это то же решение, что у вращения
/// (SM-03), и у него та же причина: <c>ILinearPattern.Axis1/Axis2</c> принимают <c>IModelObject</c>,
/// а сервер строит ось сам как <c>o3d_axis2Points</c> в ТОЙ ЖЕ детали. Ссылка на ось позволила бы
/// протащить её из чужой детали, чего массивы не проверяли.
/// </para>
/// <para>
/// <b>Вектор направления здесь отсутствует осознанно.</b> В <c>kAPI7.tlb</c> у
/// <c>ILinearPattern.Vector1/Vector2</c> объявлены только геттеры (<c>_get_Vector1</c>,
/// <c>_set_Vector1</c> отсутствует — прочитано из дампа), а в вендорской интероп-сборке
/// <c>Interop.KompasAPI7.dll</c> эти члены не объявлены ВООБЩЕ (проверено
/// <c>KompasMcp.InteropScan --type-members ILinearPattern</c>). Поэтому направление задаётся
/// осью, а не вектором, и это открытый вопрос OQ-B-03, названный, а не обойдённый.
/// </para>
/// <para>
/// <b>Второе направление необязательно.</b> Не заданы <see cref="Axis2Point1Mm"/> и
/// <see cref="Count2"/> — это массив по одной линии (<c>SM-18.grid.single_row</c>), и тогда
/// <c>Count2 = 1</c>, а поля второго направления в модель не пишутся.
/// </para>
/// </remarks>
public sealed record PatternGridCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Что копируется: операции либо тела. Решает числовой тип фабрики, а не флаг.</summary>
    public required PatternCopyKind CopyKind { get; init; }

    /// <summary>
    /// Исходные объекты массива: ссылки <c>feature:</c> (для операций) либо <c>body:</c>
    /// (для тел). Пустой список отвергается до COM: массив без исходных объектов не собирается.
    /// </summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>Первая точка оси первого направления, координаты модели, мм.</summary>
    public required IReadOnlyList<double> Axis1Point1Mm { get; init; }

    /// <summary>Вторая точка оси первого направления, координаты модели, мм.</summary>
    public required IReadOnlyList<double> Axis1Point2Mm { get; init; }

    /// <summary>Шаг по первому направлению, мм.</summary>
    public required double Step1Mm { get; init; }

    /// <summary>Число экземпляров по первому направлению, включая исходный. Больше 1.</summary>
    public required int Count1 { get; init; }

    /// <summary>
    /// Угол наклона первой оси сетки, ГРАДУСЫ. Не задан — значение модели остаётся как есть (0),
    /// то есть ось берётся как построена.
    /// </summary>
    /// <remarks>
    /// Тип СДЕЛАН НЕОБЯЗАТЕЛЬНЫМ ПО ЗАМЕРУ, а не для красоты. Пока поле было обязательным, «не
    /// задан» было невыразимо и адаптер писал ноль ВСЕГДА. Для второй оси это измеренно ломало
    /// сетку: Angle2 — угол МЕЖДУ направлениями, ноль совмещает второе направление с первым, и
    /// прямоугольная сетка вырождалась в линию (измерено: копии продолжили первую ось —
    /// x = 20, 40, 60, 50, 70, 90 при y = 20). Схема инструмента объявляла оба угла
    /// необязательными и раньше (Sch.Nullable), поэтому правка не меняет контракт по проводу —
    /// только поведение при пропущенном поле.
    /// </remarks>
    public double? Angle1Deg { get; init; }

    /// <summary>Направление копирования вдоль первой оси.</summary>
    public bool Direction1 { get; init; } = true;

    /// <summary>
    /// Интерпретация шага на границе первого направления
    /// (<c>BoundaryInstancesStepFactor1</c>). По умолчанию <c>false</c>.
    /// </summary>
    public bool BoundaryInstancesStepFactor1 { get; init; }

    /// <summary>Первая точка оси второго направления; не задана — сетка по одной линии.</summary>
    public IReadOnlyList<double>? Axis2Point1Mm { get; init; }

    /// <summary>Вторая точка оси второго направления.</summary>
    public IReadOnlyList<double>? Axis2Point2Mm { get; init; }

    /// <summary>Шаг по второму направлению, мм. Действует только вместе с <see cref="Count2"/>.</summary>
    public double? Step2Mm { get; init; }

    /// <summary>Число экземпляров по второму направлению. Не задано — направление не участвует.</summary>
    public int? Count2 { get; init; }

    /// <summary>
    /// Угол МЕЖДУ направлениями сетки, ГРАДУСЫ. Прямоугольная сетка — 90°; 0° совмещает второе
    /// направление с первым. Не задан — значение модели остаётся как есть (90°).
    /// </summary>
    /// <remarks>
    /// «Угол между направлениями», а не «наклон второй оси»: измерено прогоном в одной постановке
    /// при второй оси (0,0,0)→(0,−80,0) и шаге 30 — при 0° копии продолжили первую ось, при 90° встали
    /// по второй, а при 270° модель привела значение к 180° и увела второе направление в
    /// противоположную сторону. Вторая ось при этом задаёт, В КАКУЮ сторону откладывать угол.
    /// </remarks>
    public double? Angle2Deg { get; init; }

    /// <summary>Направление копирования вдоль второй оси.</summary>
    public bool Direction2 { get; init; } = true;

    /// <summary>Интерпретация шага на границе второго направления.</summary>
    public bool BoundaryInstancesStepFactor2 { get; init; }

    /// <summary>
    /// Способ построения массива, <c>ksLinearPatternBuildingTypeEnum</c> словом контракта:
    /// <c>save_all</c> (0), <c>save_along_perimeter</c> (1), <c>save_along_axially</c> (2),
    /// <c>chess_order_by_axis1</c> (3), <c>chess_order_by_axis2</c> (4). Числа прочитаны из
    /// <c>Interop.Kompas6Constants3D.dll</c>, а не из каталога.
    /// </summary>
    public string BuildingType { get; init; } = "save_all";

    /// <summary>
    /// Геометрическое копирование (<c>IFeaturePattern.GeometryPattern</c>). Документировано
    /// страницей SDK <c>ifeaturepattern_geometrypattern.html</c>; маршрут B4 измеряется на
    /// <c>false</c>, поэтому <c>true</c> отвергается до COM как неизмеренный режим.
    /// </summary>
    public bool GeometryPattern { get; init; }

    /// <summary>Аналитическое ожидание объёма документа ПОСЛЕ операции, мм³.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>
    /// Аналитическое ожидание числа тел ПОСЛЕ операции. Для массива операций оно равно числу тел
    /// до операции, для массива тел — растёт. Сравнение точное: допуск к счётным величинам не
    /// применяется (tolerance_classes профиля).
    /// </summary>
    public int? ExpectedBodyCount { get; init; }

    /// <summary>
    /// Радиус цилиндрической грани, по которой считается число экземпляров (мм). Отверстие Ø10
    /// даёт радиус 5. Без него поимённая проверка экземпляров не выполняется, и это честно
    /// помечается в <c>unverified_aspects</c>, а не выдаётся за проверку.
    /// </summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Высота цилиндрической грани, мм (толщина пластины для сквозного отверстия).</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Аналитическое ожидание числа экземпляров-отверстий. Точное совпадение.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>
    /// Аналитические координаты осей каждого экземпляра в координатах модели, мм. Объём не
    /// отличает четыре отверстия от трёх плюс одно наложенное — этот набор отличает.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Массив по концентрической сетке (docs/05 SM-19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Соотнесение направлений взято со страницы справки, а не из ожидания.</b> Страница SDK
/// <c>icircularpattern_props.html</c> (проверена по проводу) называет члены так:
/// <c>Count1</c> — «Количество экземпляров в РАДИАЛЬНОМ направлении», <c>Step1</c> — «Шаг
/// копирования в РАДИАЛЬНОМ направлении», <c>Count2</c> — «Количество экземпляров в КОЛЬЦЕВОМ
/// направлении», <c>Step2</c> — «УГЛОВОЙ шаг (ГРАДУСЫ) — шаг копирования в КОЛЬЦЕВОМ
/// направлении». Поэтому кольцевое направление здесь — это ПАРА (Count2, Step2), а не
/// (Count1, Step1), как предполагалось до проверки страницы. Это закрытие OQ-B-02.
/// </para>
/// <para>
/// <b>Полный оборот выражается парой (Count2, Step2), а не признаком.</b> Отдельного члена
/// «полный оборот» у <c>ICircularPattern</c> нет ни в интероп-сборке, ни в справке; ожидаемая
/// семантика — <c>Step2 = 360 / Count2</c>, и она измеряется обеими половинами в одной постановке
/// (шаг 90° при Count2 = 4 против шага 120° в отрицательном контроле: дубль на 360° виден по
/// объёму, расхождение ровно объёму одного отверстия).
/// </para>
/// </remarks>
public sealed record PatternCircularCommand
{
    public required string DocumentId { get; init; }

    public required PatternCopyKind CopyKind { get; init; }

    /// <summary>Исходные объекты массива: <c>feature:</c> либо <c>body:</c>-ссылки.</summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>Первая точка оси массива, координаты модели, мм.</summary>
    public required IReadOnlyList<double> AxisPoint1Mm { get; init; }

    /// <summary>Вторая точка оси массива, координаты модели, мм.</summary>
    public required IReadOnlyList<double> AxisPoint2Mm { get; init; }

    /// <summary>Число экземпляров в РАДИАЛЬНОМ направлении (<c>Count1</c>). Не задано — 1.</summary>
    public int Count1 { get; init; } = 1;

    /// <summary>Шаг в РАДИАЛЬНОМ направлении (<c>Step1</c>), мм. Действует при <c>Count1 &gt; 1</c>.</summary>
    public double Step1Mm { get; init; }

    /// <summary>Число экземпляров в КОЛЬЦЕВОМ направлении (<c>Count2</c>).</summary>
    public required int Count2 { get; init; }

    /// <summary>
    /// УГЛОВОЙ шаг в КОЛЬЦЕВОМ направлении (<c>Step2</c>), ГРАДУСЫ. Единица названа самой
    /// страницей справки; расхождение «градусы против радиан» различается в 57,3 раза и проверяется
    /// отдельной калибровочной пробой.
    /// </summary>
    public required double Step2Deg { get; init; }

    /// <summary>Шаг вдоль оси (<c>StepByAxis</c>), мм.</summary>
    public double StepByAxisMm { get; init; }

    /// <summary>Интерпретация шага на границе радиального направления.</summary>
    public bool BoundaryInstancesStepFactor1 { get; init; }

    /// <summary>Интерпретация шага на границе кольцевого направления.</summary>
    public bool BoundaryInstancesStepFactor2 { get; init; }

    /// <summary>Направление построения массива (<c>ReverseDirection</c>).</summary>
    public bool ReverseDirection { get; init; }

    /// <summary>
    /// Ориентация экземпляров (<c>SaveInitialOrientation</c>). Член есть ТОЛЬКО у кругового массива:
    /// у <c>ILinearPattern</c> он отсутствует и в справке, и в интероп-сборке, поэтому между
    /// семействами он не переносится.
    /// </summary>
    public bool SaveInitialOrientation { get; init; } = true;

    /// <summary>Способ построения: <c>save_all</c> (0), <c>chess_order_by_axis1</c> (1), <c>chess_order_by_axis2</c> (2).</summary>
    public string BuildingType { get; init; } = "save_all";

    /// <summary>Геометрическое копирование. <c>true</c> отвергается до COM как неизмеренный режим.</summary>
    public bool GeometryPattern { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public int? ExpectedBodyCount { get; init; }

    /// <summary>Радиус цилиндрической грани-экземпляра, мм (Ø10 → 5). См. PatternGridCommand.</summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Высота цилиндрической грани-экземпляра, мм.</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Аналитическое ожидание числа экземпляров-отверстий.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>Аналитические координаты осей экземпляров в координатах модели, мм.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Зеркальный массив (docs/05 SM-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Плоскость — явный объект, а не текущее выделение окна.</b> Это требование зависимости
/// <c>dep.refs.planes</c>, и оно же снимает вопрос о знаке нормали: сторона отражения задаётся
/// самой плоскостью, а не порядком точек выделения. Знак нормали YOZ (направлена в −X) при этом
/// остаётся фактом модели и записан в контракт плоскости (<c>PlaneRefDto</c>), а не спрятан в
/// разрешении ссылки.
/// </para>
/// <para>
/// <b>Числа типов опубликованы, а не подобраны.</b> <c>copytype.html</c>:
/// <c>o3d_mirrorOperation=48</c> — «зеркальный массив» (<c>IMirrorPattern</c>),
/// <c>o3d_mirrorAllOperation=49</c> — «зеркально отразить все» (тот же <c>IMirrorPattern</c>,
/// дополнительно <c>IChooseBodies7</c>).
/// </para>
/// </remarks>
public sealed record PatternMirrorCommand
{
    public required string DocumentId { get; init; }

    public required PatternMirrorMode Mode { get; init; }

    /// <summary>
    /// Исходные объекты. Для <see cref="PatternMirrorMode.SelectedOperations"/> — <c>feature:</c>
    /// ссылки и непустой список; для <see cref="PatternMirrorMode.AllBodies"/> — либо пусто
    /// («все тела»), либо явные <c>body:</c> ссылки.
    /// </summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>Плоскость симметрии: <c>plane:</c>-ссылка либо базовая плоскость через <c>base</c>.</summary>
    public required PlaneRefDto Plane { get; init; }

    /// <summary>
    /// Сохранять исходные объекты (<c>SaveInitialObjects</c>). <c>true</c> добавляет отражённую
    /// копию, оставляя исходник; <c>false</c> заменяет исходник ею.
    /// <para>
    /// ОБЛАСТЬ ДЕЙСТВИЯ ОГРАНИЧЕНА СПРАВКОЙ, И ЭТО ИЗМЕРЕНО. Страница
    /// <c>imirrorpattern_saveinitialobjects.html</c> говорит прямо: «Свойство работает ТОЛЬКО для
    /// <c>o3d_mirrorAllOperation</c>» и «у других операций зеркального копирования возможность
    /// скрыть экземпляры отсутствует». Измерено прогоном на обеих операциях: у
    /// <c>o3d_mirrorAllOperation</c> (режим <see cref="PatternMirrorMode.AllBodies"/>) запись
    /// <c>false</c> читается обратно как <c>false</c> и тела действительно заменяются отражёнными
    /// (2 → 2 тела по 4 000), а у <c>o3d_mirrorOperation</c> (режим
    /// <see cref="PatternMirrorMode.SelectedOperations"/>) запись <c>false</c> читается обратно как
    /// <c>true</c> и геометрия не меняется вовсе. Требовать различия половин на операции 48 значило
    /// бы требовать того, что справка у неё отрицает; параметр принимается, а его непринятие
    /// называется заметкой маршрута, а не выдаётся за сработавшее свойство.
    /// </para>
    /// </summary>
    public required bool SaveInitialObjects { get; init; }

    /// <summary>
    /// Тип действия над телами для <c>IChooseBodies7.ChooseBodiesType</c>:
    /// <c>new_body</c> (0), <c>automatic</c> (1), <c>manual</c> (2), <c>all_bodies</c> (3).
    /// Действует только у <see cref="PatternMirrorMode.AllBodies"/>.
    /// </summary>
    public string ChooseBodiesType { get; init; } = "all_bodies";

    public double? ExpectedVolumeMm3 { get; init; }

    public int? ExpectedBodyCount { get; init; }

    /// <summary>Радиус цилиндрической грани-экземпляра, мм.</summary>
    public double? ExpectedHoleRadiusMm { get; init; }

    /// <summary>Высота цилиндрической грани-экземпляра, мм.</summary>
    public double? ExpectedHoleHeightMm { get; init; }

    /// <summary>Аналитическое ожидание числа экземпляров-отверстий.</summary>
    public int? ExpectedHoleCount { get; init; }

    /// <summary>Аналитические координаты осей экземпляров в координатах модели, мм.</summary>
    public IReadOnlyList<IReadOnlyList<double>>? ExpectedHoleCentersMm { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Перечитать параметры массива либо зеркала по ссылке на признак.</summary>
public sealed record PatternReadCommand
{
    public required string FeatureRef { get; init; }
}

/// <summary>
/// Новые параметры СУЩЕСТВУЮЩЕГО признака массива (правка по docs/05 §4.3: не «удалить и создать
/// похожий»). Передаётся полем <c>pattern</c> инструмента <c>kompas_update_feature</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему отдельный тип, а не поля на самой команде правки.</b> У массива нет ни глубины, ни
/// радиуса, ни эскиза: члены, которыми он правится, принадлежат ТРЁМ разным интерфейсам API7
/// (<c>ILinearPattern</c>, <c>ICircularPattern</c>, <c>IMirrorPattern</c>), и часть имён между ними
/// совпадает только по виду. Плоский набор полей на <see cref="UpdateFeatureCommand"/> читался бы
/// как «это всё применимо к любому массиву», а это неверно: <c>save_initial_orientation</c> есть
/// только у кругового, <c>save_initial_objects</c> — только у зеркального, <c>step2_deg</c> —
/// только у кругового.
/// </para>
/// <para>
/// <b>Что здесь есть и почему.</b> Перечислены ровно те члены, которые страницы свойств
/// (<c>ilinearpattern_props.html</c>, <c>icircularpattern_props.html</c>,
/// <c>imirrorpattern_props.html</c>, проверены по проводу) объявляют у этих интерфейсов:
/// </para>
/// <list type="bullet">
/// <item><description><c>ILinearPattern</c>: <c>Angle1/2</c>, <c>Count1/2</c>,
/// <c>Direction1/2</c>, <c>Step1/2</c>, <c>BuildingType</c>.</description></item>
/// <item><description><c>ICircularPattern</c>: <c>Count1/2</c>, <c>Step1/2</c>,
/// <c>StepByAxis</c>, <c>ReverseDirection</c>, <c>SaveInitialOrientation</c>,
/// <c>BuildingType</c>.</description></item>
/// <item><description><c>IMirrorPattern</c>: <c>SaveInitialObjects</c>.</description></item>
/// </list>
/// <para>
/// <b>Чего здесь нет.</b> Оси и плоскость: <c>Axis1/Axis2</c> и <c>Plane</c> принимают
/// <c>IModelObject</c>, а ссылку на объект опоры вызывающий не может выдать так же, как выдаёт
/// <c>feature:</c>-ссылку на признак, — и ни один прогон B4 смену опоры существующего массива не
/// измерял. Молча оставить эти члены вне контракта нельзя, поэтому они названы здесь как
/// неправимые. <c>Vector1/Vector2</c> отсутствуют по той же причине, что и при создании: в
/// вендорской интероп-сборке они не объявлены вовсе (OQ-B-03).
/// </para>
/// <para>
/// <b>Полная постановка, а не «что изменилось».</b> Правка обязана нести значения ВСЕХ параметров
/// режима, которые должны остаться: члены, не заданные в запросе, остаются как есть, и это
/// единственный способ отличить «изменилось ровно запрошенное» от «изменилось ещё и это».
/// </para>
/// </remarks>
public sealed record PatternEditDto
{
    /// <summary>Число экземпляров по первому направлению. У сетки — по оси 1, у кругового — РАДИАЛЬНОЕ.</summary>
    public int? Count1 { get; init; }

    /// <summary>Число экземпляров по второму направлению. У кругового — КОЛЬЦЕВОЕ.</summary>
    public int? Count2 { get; init; }

    /// <summary>Шаг по первому направлению, мм (у кругового — радиальный шаг).</summary>
    public double? Step1Mm { get; init; }

    /// <summary>Шаг по второму направлению массива ПО СЕТКЕ, мм. У кругового неприменим.</summary>
    public double? Step2Mm { get; init; }

    /// <summary>УГЛОВОЙ шаг кольцевого направления кругового массива, ГРАДУСЫ. У сетки неприменим.</summary>
    public double? Step2Deg { get; init; }

    /// <summary>Угол наклона первой оси сетки, ГРАДУСЫ. У кругового неприменим.</summary>
    public double? Angle1Deg { get; init; }

    /// <summary>Угол наклона второй оси сетки, ГРАДУСЫ. У кругового неприменим.</summary>
    public double? Angle2Deg { get; init; }

    /// <summary>Направление копирования вдоль первой оси. У кругового неприменим.</summary>
    public bool? Direction1 { get; init; }

    /// <summary>Направление копирования вдоль второй оси. У кругового неприменим.</summary>
    public bool? Direction2 { get; init; }

    /// <summary>
    /// Способ построения. Слова контракта — те же, что при создании: у сетки <c>save_all</c>,
    /// <c>save_along_perimeter</c>, <c>save_along_axially</c>, <c>chess_order_by_axis1</c>,
    /// <c>chess_order_by_axis2</c>; у кругового <c>save_all</c>, <c>chess_order_by_axis1</c>,
    /// <c>chess_order_by_axis2</c>. Неизвестное слово отвергается до мутации, а не подменяется
    /// умолчанием.
    /// </summary>
    public string? BuildingType { get; init; }

    /// <summary>Шаг вдоль оси кругового массива, мм. У сетки неприменим.</summary>
    public double? StepByAxisMm { get; init; }

    /// <summary>Направление построения кругового массива. У сетки неприменим.</summary>
    public bool? ReverseDirection { get; init; }

    /// <summary>Ориентация экземпляров кругового массива. У сетки и у зеркала неприменима.</summary>
    public bool? SaveInitialOrientation { get; init; }

    /// <summary>Сохранять исходные объекты зеркального массива. У сетки и у кругового неприменим.</summary>
    public bool? SaveInitialObjects { get; init; }

    /// <summary>Аналитическое ожидание объёма документа ПОСЛЕ правки, мм³.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>Аналитическое ожидание числа тел ПОСЛЕ правки. Сравнение точное.</summary>
    public int? ExpectedBodyCount { get; init; }
}

/// <remarks>
/// <see cref="AngleDeg"/> — то, что лежит в модели первым слотом пары <c>Angle[true]</c>, и оно
/// равно заданному углу вплоть до полного оборота (измерено 18.09.2026, проба F.1a и слепое
/// чтение файла). Прежняя оговорка «при насыщении на 180° эти числа расходятся» относилась к
/// опровергнутому измерению и снята.
/// </remarks>
public sealed record RotatedDto(
    string? OperationType,
    double? AngleDeg,
    string? Direction,
    string? AxisState,
    int? ProfileInputCount);


/// <remarks>
/// <see cref="CountersinkDepthMm"/> — то, что вернул ОБЪЕКТ, а не то, что записали: при способе
/// «диаметр + угол» глубина производна, и запись в неё чисел 2/4/6 не меняет ничего (M.3).
/// </remarks>
public sealed record HoleDto(
    string? HoleType,
    double? DiameterMm,
    string? DepthType,
    double? DepthMm,
    string? EndFaceType,
    double? CounterboreDiameterMm,
    double? CounterboreDepthMm,
    double? CountersinkDiameterMm,
    double? CountersinkAngleDeg,
    double? CountersinkDepthMm,
    double[]? CenterMm);

/// <summary>Одна сторона выдавливания, как её вернул <c>GetSideParam</c>.</summary>
public sealed record FeatureSideDto(    bool Side,
    short EndConditionType,
    string EndCondition,
    double DepthMm,
    double DraftValue,
    bool DraftOutward);

/// <summary>Тонкая стенка выдавливания; <c>ReverseThicknessMm</c> — член с вендорской опечаткой в геттере.</summary>
public sealed record FeatureThinDto(
    bool Thin,
    short ThinType,
    double NormalThicknessMm,
    double ReverseThicknessMm);

/// <summary>
/// Фаска, перечитанная из модели. Поля null тогда, когда вызов их не дал: «не прочитано» и
/// «ноль» — разные ответы, и смешивать их нельзя (тот же стандарт, что у measure).
/// </summary>
public sealed record ChamferDto(
    bool? Transfer,
    double? Distance1Mm,
    double? Distance2Mm,
    double? AngleDeg,
    string? BuildingType,
    bool? Direction,
    int? BaseObjectCount);

/// <summary>
/// Параметры скругления. Радиус читается из API7 (<c>IFillet.Radius1</c>): у API5
/// <c>ksFilletDefinition.radius</c> объявлен и читается, но НЕ применяется при записи на
/// существующем признаке (измерено строкой FL04r), поэтому авторитетным источником служит живая
/// модель. Пустое поле означает «не прочитано» (мост API7 не построен или скруглений несколько),
/// а не «ноль».
/// </summary>
/// <param name="BaseObjectReferences">
/// Ссылки входов признака — <c>IModelObject.Reference</c> каждого элемента <c>IFillet.BaseObjects</c>.
/// Наблюдаемая величина состава: по ней видно, из чего признак собран, и она же служит диагностикой
/// при правке набора.
/// <para>
/// <b>Чего она НЕ делает — и это исправление прежней редакции (17.09.2026).</b> Здесь было написано,
/// что это «ЕДИНСТВЕННЫЙ источник, по которому признак адресуется в правке набора (<c>edge_refs</c>)».
/// Неверно, и проверить это можно не выходя из кода: <c>edge_refs</c> принимает строки реестра ссылок
/// вида <c>edge:&lt;hex&gt;</c>, а здесь лежат ЧИСЛА <c>IModelObject.Reference</c> — у них строки реестра
/// нет, и подставить их в <c>edge_refs</c> нельзя. Так что адресовать признак этим полем невозможно
/// по типу, независимо от контекстов.
/// </para>
/// <para>
/// <b>Измеренная причина, почему рёбер тела для правки набора мало.</b> После скругления всех углов у
/// тела 24 ребра: 16 линий и 8 дуг длины π·3/2 (четверть окружности R3). Все восемь «вертикальных»
/// линий лежат на швах касания цилиндров <c>(±47,±40)</c> и <c>(±50,±37)</c>; угловых вертикальных
/// рёбер в топологии НОЛЬ (<c>len(corner_edges) = 0</c>) — их отозвало само скругление.
/// </para>
/// <para>
/// <b>И главное: входы признака и рёбра тела НЕ взаимозаменяемы как валюта записи.</b> Проба H-2
/// сокращала набор (4→3, 4→2) объектами, прочитанными ИЗ <c>BaseObjects</c>, — ни одно ребро тела при
/// этом не искалось и не переносилось. Замер 17.09.2026 (FL10): предъявление трёх рёбер тела
/// (дуг) вместо собственных входов признака СХЛОПЫВАЕТ признак — объём возвращается к пластине
/// (<c>79999.99999999999</c>), <c>level=call_returned</c>. Замена при неизменном размере (1→1),
/// наоборот, работает перенесёнными рёбрами (FL10x, <c>level=geometry_checked</c>).
/// Отсюда: СОКРАЩЕНИЕ набора и ЗАМЕНА состава — разные операции с разной валютой, и одним полем
/// <c>edge_refs</c> выражается только вторая.
/// </para>
/// <para>Пустой список — признак не удерживает ни одного входа; <c>null</c> — не прочитано.</para>
/// </param>
/// <param name="BaseObjectInputRefs">
/// Ссылки реестра на СОБСТВЕННЫЕ входы признака — <c>input:&lt;hex&gt;</c>, по одной на элемент
/// <c>IFillet.BaseObjects</c>, в том же порядке, что <paramref name="BaseObjectReferences"/>.
/// </param>
/// <remarks>
/// Это и есть ВАЛЮТА СОКРАЩЕНИЯ, которой не хватало. Отличие от
/// <paramref name="BaseObjectReferences"/> принципиальное и измеренное: там ЧИСЛА
/// <c>IModelObject.Reference</c>, у которых строки реестра нет, поэтому в <c>edge_refs</c> они не
/// подставляются по типу; здесь — строки реестра, выпущенные ЭТИМ сервером, поэтому их принимает
/// <see cref="UpdateFeatureCommand.BaseObjectRefs"/>. Правило одно и то же для всех ссылок сервера:
/// клиент не сочиняет и не переносит идентификаторы, а подставляет выданные.
/// <para>
/// Ссылки минтит ЧТЕНИЕ признака (<c>kompas_get_feature</c>) на текущую ревизию документа, поэтому
/// они стареют ровно так же, как <c>edge:</c>-ссылки: после мутации <c>REVISION_CONFLICT</c> или
/// <c>STALE_REFERENCE</c> заставит перечитать контекст, а не подставить устаревшее число.
/// </para>
/// <para>
/// <c>null</c> — входы не прочитаны (мост API7 не построен или скруглений с тем же радиусом
/// несколько); пустой список — признак не удерживает ни одного входа. Это разные состояния, как и у
/// <paramref name="BaseObjectReferences"/>.
/// </para>
/// </remarks>
public sealed record FilletDto(
    double? RadiusMm,
    double? Radius2Mm,
    bool? Tangent,
    string? BuildingType,
    int? BaseObjectCount,
    IReadOnlyList<int>? BaseObjectReferences = null,
    IReadOnlyList<string>? BaseObjectInputRefs = null);

/// <summary>
/// Опора признака B3, прочитанная ИЗ МОДЕЛИ: точка, единичная нормаль и — отдельно — три точки
/// построения, из которых нормаль и выведена.
/// </summary>
/// <remarks>
/// Три точки публикуются намеренно. Нормаль — величина ПРОИЗВОДНАЯ (векторное произведение), и
/// читающий вправе видеть, из чего она получена, а не принимать её на веру. Измерено 18.09.2026
/// (проба <c>--split</c>, шаг SP.10): у плоскости <c>x = 10</c> чтение возвращает ровно
/// <c>(10,0,0)</c>, <c>(10,1,0)</c>, <c>(10,0,1)</c> и нормаль <c>(1,0,0)</c>, а у плоскости
/// <c>x = 15</c> — те же три точки со сдвигом, то есть чтение различает разные опоры.
/// </remarks>
public sealed record SupportPlaneDto(
    IReadOnlyList<double> PointMm,
    IReadOnlyList<double> NormalMm,
    IReadOnlyList<double> Point1Mm,
    IReadOnlyList<double> Point2Mm,
    IReadOnlyList<double> Point3Mm);

/// <summary>
/// Параметры признака B3 (булева операция, разделение, отсечение, изменение положения),
/// прочитанные ИЗ МОДЕЛИ — действие <c>read</c> наряда §7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Заполняются только поля своего семейства</b>; у остальных семейств они <c>null</c>. Пустое
/// поле означает «не прочитано», а не ноль, и причина попадает в
/// <see cref="UnreadableParameters"/>.
/// </para>
/// <para>
/// <b>Из пяти полей преобразования положения не публикуется ОДНО:</b>
/// <see cref="RepositionAxisPointMm"/>. У точки оси нет документированного члена ни у одного
/// интерфейса цепочки, и она не является свойством размещения — поворот вокруг любой точки ОДНОЙ
/// И ТОЙ ЖЕ прямой даёт то же размещение, поэтому из прочитанных ориентации и переноса
/// восстанавливается ПРЕДСТАВИТЕЛЬ прямой, а не исходный вход. Поле всегда <c>null</c> и всегда
/// названо в <see cref="UnreadableParameters"/>: у поворота — «не читается», у переноса —
/// «неприменимо». Остальные четыре (<see cref="RepositionKind"/>, <see cref="RepositionVectorMm"/>,
/// <see cref="RepositionAxisDirectionMm"/>, <see cref="RepositionAngleDeg"/>) читаются
/// документированным параметрическим маршрутом: <c>OrientationType = ksEulerCorners</c> +
/// <c>LocalCSParameters → ILocalCSEulerParam</c> для ориентации и <c>ParameterType = ksPDisplace</c>
/// + <c>Parameters → IPoint3DParamDisplace</c> для переноса. Маршрут измерен шагом RP.25 пробы
/// <c>--reposition-params</c> (прогон <c>a336120926fc4652a8bf737562568271</c>): и тройка углов, и
/// смещение читаются с ПЕРЕОТКРЫТОГО документа ДО сборки и ДО всякой записи.
/// </para>
/// <para>
/// <b>Признак, записанный матрицей, не читается — и это отказ, а не нули.</b> Такой признак
/// опознаётся по ПРОЧИТАННОМУ <c>OrientationType = 0 (ksAxisOrientation)</c>: параметров ориентации
/// у него нет, а матричный вид размещения на переоткрытом документе единичен при сохранённой
/// геометрии, поэтому вывести из него вид нельзя — именно это давало ложный
/// <c>RepositionKind = "translate"</c> у записанного поворота. Существующие признаки не
/// преобразуются молча: все пять полей называются непрочитанными с этой причиной.
/// </para>
/// </remarks>
public sealed record SolidFeatureDto(
    /// <summary>Вид булевой операции: <c>union</c>, <c>difference</c> или <c>intersect</c>.</summary>
    string? Operation = null,
    /// <summary>Сохраняется ли инструмент отдельным телом (<c>IBoolean.SaveCopyModifyObjects</c>).</summary>
    bool? KeepTools = null,
    /// <summary>Опора разделения либо отсечения: точка, нормаль и три точки построения.</summary>
    SupportPlaneDto? Plane = null,
    /// <summary>Какая сторона осталась у отсечения (<c>ICut.Direction</c>): true — сторона нормали.</summary>
    bool? KeepSide = null,
    /// <summary>
    /// Вид преобразования положения: <c>translate</c> либо <c>rotate</c>. Читается из параметров
    /// размещения: единичный прочитанный поворот при прочитанном переносе — перенос, неединичный —
    /// поворот (см. remarks).
    /// </summary>
    string? RepositionKind = null,
    /// <summary>
    /// Вектор переноса, мм. Читается у переноса (<c>ksPDisplace</c> + <c>IPoint3DParamDisplace</c>);
    /// у поворота <c>null</c> и назван неприменимым — контракт поворота вектора не принимает.
    /// </summary>
    IReadOnlyList<double>? RepositionVectorMm = null,
    /// <summary>
    /// Точка на оси поворота, мм. ВСЕГДА null: у поворота не читается (документированного члена нет,
    /// и она не является свойством размещения), у переноса неприменима (см. remarks).
    /// </summary>
    IReadOnlyList<double>? RepositionAxisPointMm = null,
    /// <summary>
    /// Единичное направление оси поворота — читается у поворота; у переноса <c>null</c>.
    /// </summary>
    IReadOnlyList<double>? RepositionAxisDirectionMm = null,
    /// <summary>
    /// Угол поворота в градусах — читается у поворота; у переноса <c>null</c>.
    /// </summary>
    double? RepositionAngleDeg = null,
    /// <summary>
    /// Имена непрочитанных параметров с ИЗМЕРЕННОЙ причиной. Непустой список — это не отказ чтения,
    /// а его граница: остальные поля при этом заполнены.
    /// </summary>
    IReadOnlyList<string>? UnreadableParameters = null);

/// <summary>Что сервер прочитал из определения существующего признака (docs/05 §7 kompas_get_feature).</summary>
public sealed record FeatureReadDto(
    ReferenceDto FeatureRef,
    string Family,
    string DefinitionInterface,
    string Name,
    int EntityTypeName,
    int UpdateStamp,
    bool IsValid,
    bool Excluded,
    bool IsRollBacked,
    int ObjectError,
    short? DirectionType,
    IReadOnlyList<FeatureSideDto> Sides,
    FeatureThinDto? Thin,
    string? OwnerFeatureName,
    ChamferDto? Chamfer,
    VerificationDto Verification,
    /// <summary>
    /// Параметры скругления. Хвостовым параметром, чтобы не сдвигать позиционные аргументы у
    /// остальных семейств: поле заполняется только для <c>family = "fillet"</c> и у прочих null.
    /// </summary>
    FilletDto? Fillet = null,
    /// <summary>
    /// Параметры родного отверстия. Тоже хвостовым: заполняется только для <c>family = "hole"</c>.
    /// Читается целиком из API7 (<c>IHole3D</c> + <c>HoleParameters</c> приведённые к своему
    /// режиму), потому что в API5 определения отверстия не существует физически — измерено
    /// 16.09.2026: среди 67 объявленных в вендорской обёртке определений есть
    /// <c>ksChamferDefinition</c> и <c>ksFilletDefinition</c>, а <c>ksHoleDefinition</c> нет.
    /// </summary>
    HoleDto? Hole = null,
    /// <summary>
    /// Параметры вращения. Тоже хвостовым: заполняется только для <c>family = "rotation"</c> и у
    /// прочих null. Читается из API7 (<c>IRotated</c>), потому что определения API5 у вращения нет
    /// вовсе — <c>entity.GetDefinition()</c> возвращает null (измерено при приёмке SM-03).
    /// </summary>
    RotatedDto? Rotated = null,
    /// <summary>
    /// Параметры признаков B3. Тоже хвостовым: заполняется только для <c>family</c> из
    /// {<c>boolean</c>, <c>split</c>, <c>cut_by_plane</c>, <c>reposition</c>} и у прочих null.
    /// Читается из API7, потому что определения API5 у этих семейств нет вовсе —
    /// <c>entity.GetDefinition()</c> возвращает null, а сами они опознаются по НОМЕРУ ПРИЗНАКА В
    /// ДЕРЕВЕ (69 / 633 / 50 / 79), измеренному 18.09.2026 прибором
    /// <c>scratch/b3-measure-feature-types.py</c>. Что читается и что нет — измерено пробами
    /// <c>--boolean</c> (BO.2–BO.5, BO.10), <c>--split</c> (SP.10) и <c>--reposition</c>
    /// (RP.8–RP.12); сводка — в docs/04_KOMPAS_API_NOTES.md §4.10.9.
    /// </summary>
    SolidFeatureDto? Solid = null,
    /// <summary>
    /// Параметры кинематической операции. Тоже хвостовым: заполняется только для
    /// <c>family = "sweep"</c> и у прочих null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Читается из ДЕРЕВА, и номер типа в дереве не равен номеру создания.</b> Измерено
    /// 20.09.2026 (проба <c>--b5</c>, шаг B5.12): признак, созданный <c>NewEntity(45)</c>
    /// (<c>o3d_baseEvolution</c>), виден в дереве под номером <b>46</b>
    /// (<c>o3d_bossEvolution</c>), а его определение отвечает интерфейсу
    /// <c>ksBossEvolutionDefinition</c> — <b>не</b> <c>ksBaseEvolutionDefinition</c>. Это тот же
    /// класс расхождения, что уже измерен у отверстия (создаётся 52, в дереве 583) и у вращения
    /// (создаётся 27, в дереве 584): опознавать семейство по номеру типа из ответа создания нельзя.
    /// </para>
    /// <para>
    /// Поэтому семейство опознаётся <b>по интерфейсу определения</b>, и принимаются оба —
    /// <c>ksBossEvolutionDefinition</c> и <c>ksBaseEvolutionDefinition</c>: какой из них достанется,
    /// зависит от того, чем признак создан, и это измеряется, а не предполагается.
    /// </para>
    /// <para>
    /// Прочитанные величины и их сверка на шаге B5.12: <c>sketchShiftType</c> = 2 (записано
    /// ортогонально), <c>PathPartArray()</c> = 1 часть, <c>GetPathLength(1)</c> = 100 мм при
    /// траектории 100 мм, <c>GetSketch()</c> отдаёт объект. <c>OperationResult</c> живёт только в
    /// API7 (<c>IEvolution</c>): из коллекции документа прочитано значение <b>1</b>; если коллекция
    /// недоступна или пуста, поле остаётся null — «не прочитано», а не ноль.
    /// </para>
    /// </remarks>
    SweepDto? Sweep = null,
    /// <summary>
    /// Параметры элемента по сечениям. Хвостовым: только для <c>family = "loft"</c>.
    /// </summary>
    /// <remarks>
    /// Измерено 20.09.2026 (шаг B5.12): признак, созданный маршрутом адаптера
    /// (<c>ILofts.Add(o3d_bossLoft = 31)</c>), виден в дереве под тем же номером <b>31</b>, а его
    /// определение отвечает интерфейсу <c>ksBossLoftDefinition</c>. Параметры читаются из
    /// <b>коллекции документа</b> (<c>IModelContainer.Lofts</c> → <c>ILofts</c>), а не из ручки
    /// создания: <c>Count</c> = 1, <c>Loft(0)</c> отдал <c>Sketchs</c> = 2 элемента,
    /// <c>Closed</c> = False, <c>CouplingsCount</c> = 0, <c>BuildingType(true)</c> = 0.
    /// </remarks>
    LoftDto? Loft = null,
    /// <summary>
    /// Параметры оболочки. Хвостовым: только для <c>family = "shell"</c>.
    /// </summary>
    /// <remarks>
    /// Измерено 20.09.2026 (шаг B5.12): признак, созданный <c>NewEntity(43)</c>, виден в дереве под
    /// номером <b>43</b> (<c>o3d_shellOperation</c>), определение отвечает <c>ksShellDefinition</c>,
    /// и с него читаются <c>thickness</c> = 2, <c>thinType</c> = true, <c>FaceArray</c> = 1 грань —
    /// ровно записанное. Из API7 (<c>IShells</c> → <c>IShell</c>) те же величины видны как
    /// <c>Thickness</c> = 2, <c>ThinType</c> = <c>dt_reverse</c> (внутрь), <c>DeletedFaces</c> = 1:
    /// две независимые половины одной постановки.
    /// </remarks>
    ShellDto? Shell = null);

public sealed record GetFeatureCommand
{
    /// <summary>Ссылка <c>feature:</c> из kompas_list_features или результата мутации. Её ревизия и
    /// сверяется — отдельного expected_revision у чтения нет по той же причине, что у measure.</summary>
    public required string FeatureRef { get; init; }
}

/// <summary>
/// Чтение параметрической определённости существующего эскиза
/// (<see cref="WorkerCommands.SketchStatus"/>, <c>kompas_get_sketch_status</c>).
/// </summary>
/// <remarks>
/// Отдельного <c>expected_revision</c> у чтения нет — по той же причине, что у measure и
/// get_feature: ревизия, для которой выпущена ссылка, уже лежит в реестре и сверяется там. Молчаливое
/// «возьмём текущую» здесь было бы ровно тем, чего контракт требует не делать.
/// </remarks>
public sealed record GetSketchStatusCommand
{
    /// <summary>
    /// Ссылка <c>sketch:</c>, выданная этим сервером (kompas_create_sketch или чтение признака).
    /// Активный документ, текущее выделение и «первый попавшийся эскиз» не используются: адресация
    /// только явная, а ревизия — из реестра ссылок.
    /// </summary>
    public required string SketchRef { get; init; }
}

/// <summary>
/// Нормализованный статус определённости эскиза — то, что сервер действительно прочитал, а не то,
/// что он предположил.
/// </summary>
/// <remarks>
/// <para>
/// <b>Соответствие нативным состояниям измерено, а не назначено</b> (проба S, прогон
/// <c>82880ed0</c>; значения перечисления сверены с объявленными шагом S.3):
/// <list type="bullet">
/// <item><c>ksStateWellConstrained</c> (1) → <see cref="FullyDefined"/>;</item>
/// <item><c>ksStateUnderConstrained</c> (2) → <see cref="UnderDefined"/>;</item>
/// <item><c>ksStateUnknown</c> (0) → <see cref="Unknown"/> — КОМПАС сам не установил статус
/// (получено и на пустом эскизе, и на 11 эскизах поставки);</item>
/// <item><c>ksStateUnresolvedRedundancy</c> (3) → <see cref="NeedsAttention"/> — объявлено, но
/// живой контроль <b>не получен</b>; публикуется консервативно (см.
/// <see cref="SketchStatusResult.Limitations"/>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Числа степеней свободы API не отдаёт.</b> <c>ISketch.ConstraintsState</c> возвращает статус, а
/// не счётчик, поэтому <see cref="SketchStatusResult.DegreesOfFreedom"/> — всегда <c>null</c>, и
/// вычислять его из числа размеров запрещено: ограничения связывают объекты между собой, а не
/// складываются.
/// </para>
/// </remarks>
public enum SketchDefinitionStatus
{
    /// <summary>Состояние не установлено. <c>ksStateUnknown</c> (0) либо неизвестное значение enum.</summary>
    Unknown,

    /// <summary>Полностью определён: «+». <c>ksStateWellConstrained</c> (1).</summary>
    FullyDefined,

    /// <summary>Недоопределён: «−». <c>ksStateUnderConstrained</c> (2).</summary>
    UnderDefined,

    /// <summary>Требует внимания: «!». <c>ksStateUnresolvedRedundancy</c> (3), живой контроль не получен.</summary>
    NeedsAttention,
}

/// <summary>
/// Снимок определённости эскиза. <see cref="IsFullyDefined"/> nullable намеренно: <c>null</c>
/// означает «достоверно не установлено», и подменять его <c>false</c> запрещено — «неопределено» и
/// «недоопределён» это разные ответы.
/// </summary>
/// <param name="DefinitionStatus">Нормализованный статус.</param>
/// <param name="IsFullyDefined">Только <c>true</c>/<c>false</c>/<c>null</c>; <c>null</c> при
/// <see cref="SketchDefinitionStatus.Unknown"/> и <see cref="SketchDefinitionStatus.NeedsAttention"/>.</param>
/// <param name="DegreesOfFreedom">Всегда <c>null</c>: подтверждённый маршрут отдаёт статус, а не
/// число. Не вычисляется из числа размеров и не подставляется нулём.</param>
/// <param name="Diagnostics">Понятные причины: почему статус именно такой и что помешало.</param>
/// <param name="Limitations">Границы достоверности ответа, названные явно.</param>
public sealed record SketchStatusResult(
    SketchDefinitionStatus DefinitionStatus,
    bool? IsFullyDefined,
    int? DegreesOfFreedom,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Limitations,
    /// <summary>
    /// Сырое значение <c>ksConstraintsStateEnum</c>, как его вернул КОМПАС. <c>null</c> — вызов не
    /// дошёл. Хранится отдельно от нормализованного статуса, чтобы «неизвестное значение enum» и
    /// «КОМПАС ответил <c>ksStateUnknown</c>» не сливались в один <c>unknown</c>.
    /// </summary>
    int? RawState = null,
    /// <summary>Имя состояния, как оно объявлено в сборке констант. <c>null</c> при неизвестном числе.</summary>
    string? NativeStateName = null,
    /// <summary>Имя эскиза в модели — чтобы человек мог убедиться, что прочитан именно тот эскиз.</summary>
    string? SketchName = null,
    /// <summary>Каким путём объект дошёл до <c>ISketch</c>: «ISketch напрямую» либо «IModelObject → ISketch».</summary>
    string? TransferRoute = null)
{
    /// <summary>
    /// Раскрыть нормализованный статус из сырого значения перечисления. Чистая функция — держится
    /// отдельно от COM, чтобы её можно было проверить тестом без КОМПАСа, и отдельно от
    /// <see cref="DegreesOfFreedom"/>, которого у маршрута нет вовсе.
    /// </summary>
    /// <remarks>
    /// Значение 3 нормализуется в <see cref="SketchDefinitionStatus.NeedsAttention"/> только когда
    /// <paramref name="redundancyVerified"/> истинно. Пока живой контроль не получен, вызывающий
    /// обязан передать <c>false</c>, и значение 3 честно остаётся <see cref="SketchDefinitionStatus.Unknown"/>:
    /// «объявлено в перечислении» — не то же самое, что «измерено на живой модели».
    /// </remarks>
    public static SketchStatusResult FromRawState(
        int? raw,
        bool redundancyVerified,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> limitations,
        string? sketchName = null,
        string? transferRoute = null)
    {
        if (raw is null)
        {
            return new SketchStatusResult(
                SketchDefinitionStatus.Unknown, null, null, diagnostics, limitations,
                RawState: null, NativeStateName: null, SketchName: sketchName, TransferRoute: transferRoute);
        }

        var (status, isFullyDefined, name, extraDiagnostics, extraLimitations) = raw switch
        {
            1 => (SketchDefinitionStatus.FullyDefined, (bool?)true, "ksStateWellConstrained",
                  Array.Empty<string>(),
                  Array.Empty<string>()),
            2 => (SketchDefinitionStatus.UnderDefined, (bool?)false, "ksStateUnderConstrained",
                  Array.Empty<string>(),
                  Array.Empty<string>()),
            0 => (SketchDefinitionStatus.Unknown, (bool?)null, "ksStateUnknown",
                  new[] { "КОМПАС ответил ksStateUnknown: состояние эскиза не установлено. Это ответ " +
                          "продукта, а не отказ сервера — в частности, так отвечает пустой эскиз." },
                  Array.Empty<string>()),
            3 when redundancyVerified =>
                 (SketchDefinitionStatus.NeedsAttention, (bool?)null, "ksStateUnresolvedRedundancy",
                  Array.Empty<string>(), Array.Empty<string>()),
            3 => (SketchDefinitionStatus.Unknown, (bool?)null, "ksStateUnresolvedRedundancy",
                  new[] { "КОМПАС вернул ksStateUnresolvedRedundancy (3) — «эскиз требует внимания», — " +
                          "но живой контроль этого состояния в подтверждённом прогоне не получен. " +
                          "Значение опубликовано консервативно как unknown, чтобы объявленное в " +
                          "перечислении состояние не выдавалось за измеренное." },
                  new[] { "unresolved_redundancy_not_verified" }),
            _ => (SketchDefinitionStatus.Unknown, (bool?)null, null,
                  new[] { "КОМПАС вернул значение ksConstraintsStateEnum, которого нет в объявленном " +
                          "перечислении (" + raw.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          "). Это не «недоопределён»: смысл значения неизвестен, ответ остаётся неизвестным." },
                  new[] { "unknown_enum_value" }),
        };

        return new SketchStatusResult(
            status,
            isFullyDefined,
            null,
            [.. diagnostics, .. extraDiagnostics],
            [.. limitations, .. extraLimitations],
            RawState: raw,
            NativeStateName: name,
            SketchName: sketchName,
            TransferRoute: transferRoute);
    }
}

/// <summary>
/// Правка параметров настоящего признака на месте (docs/05 §4.3: не «удалить и создать похожий»).
/// Пока поддержано семейство выдачиваний; значение проверяется перечитыванием с нового
/// объекта определения, а геометрия — измерением объёма.
/// </summary>
public sealed record UpdateFeatureCommand
{
    public required string FeatureRef { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Глубина для end_condition=blind. Ожидается сторона, а не «изменить всё подряд».</summary>
    public double? DepthMm { get; init; }

    public ExtrudeEndCondition? EndCondition { get; init; }

    /// <summary>
    /// Новый режим движения сечения кинематической операции (family=<c>sweep</c>). Отдельное поле, а
    /// не общий <c>direction</c>: у фаски <c>direction</c> — сторона фаски, у оболочки — сторона
    /// стенки, и одно имя для трёх разных предметов сделало бы ответ неоднозначным.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (проба <c>--b5</c>, шаг B5.13): на ОДНОМ признаке смена
    /// режима <c>orthogonal → parallel → orthogonal</c> дала объёмы
    /// <c>24674.011002723353 → 15707.963267948984 → 24674.011002723353</c>, а <c>sketchShiftType</c>
    /// читался обратно 0 и 2. Постановка различающая только на ДУГЕ: на прямой траектории оба режима
    /// дают одно тело.
    /// </remarks>
    public SweepShiftMode? ShiftMode { get; init; }

    /// <summary>
    /// Новый набор сечений элемента по сечениям (family=<c>loft</c>), в порядке соединения.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (шаг B5.13): перепривязка сечений на уже построенном
    /// признаке меняет геометрию — 40×40+20×20 дают <c>28000</c>, 40×40+40×40 дают призму
    /// <c>48000</c>, возврат к прежнему набору возвращает <c>28000</c>.
    /// <para>
    /// <b>Поле <c>closed</c> здесь отсутствует намеренно, и это измеренный факт, а не пропуск.</b>
    /// Запись <c>ILoft.Closed</c> на построенном признаке возвращает <c>Update() = True</c>, но
    /// обратное чтение даёт <c>False</c>, а объём остаётся прежним: «принято» не означает
    /// «применено». Поэтому замкнутость задаётся ТОЛЬКО при создании
    /// (<c>kompas_loft.closed</c>), а не объявляется правимой здесь.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? SectionRefs { get; init; }

    /// <summary>
    /// Новый набор цепочек соответствия (family=<c>loft</c>) — <b>полная замена</b>, как и
    /// <see cref="SectionRefs"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему отдельное поле, а не «оставить как было».</b> Цепочка описывает соответствие точек
    /// КОНКРЕТНОГО набора сечений: <c>ICoupling.Count</c> — «количество сечений в цепочке»
    /// (<c>icoupling_count.html</c>), а <c>PositionOffset(Index)</c> адресуется индексом сечения в
    /// цепочке (<c>icoupling_positionoffset.html</c>). Поэтому после замены набора сечений прежняя
    /// цепочка описывает уже не этот признак, и оставить её «как было» значило бы молча оставить
    /// чужое соответствие. Если признак несёт цепочки, а правка меняет сечения и НЕ называет цепочки,
    /// вызов отвергается именованным отказом; пустой список означает «без цепочек».
    /// </para>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (проба <c>--b5</c>, шаги B5.17 и B5.18): цепочка задаётся
    /// <c>ILoft.AddCoupling()</c> → <c>ICoupling</c> → <c>PositionOffset(Index)</c>; на пирамиде
    /// 40×40 → 20×20 при h = 30 смещения <c>0 / 0</c> дают <c>28000</c> (как без цепочки), смещение
    /// <c>0 / 20</c> мм (25 % контура 80 мм) даёт <c>20000</c>, возврат даёт <c>28000</c>. Замена
    /// цепочек выполняется <c>ClearCouplings()</c> + <c>AddCoupling()</c>; <c>ICoupling.Delete()</c>
    /// измерен как работающий, но полная замена не зависит от того, в каком порядке удалять.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LoftCoupling>? Couplings { get; init; }

    /// <summary>
    /// Новая толщина стенки оболочки (family=<c>shell</c>), мм.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (шаг B5.13): на одном признаке <c>t = 2 → 4 → 4 → 2</c>
    /// дало <c>21632 → 40256 → 53056 → 21632</c>, значения читались обратно. Третье число —
    /// направление наружу при <c>t = 4</c>: внешний короб <c>108×88×14 = 133056</c> минус полость
    /// <c>80000</c>. Правка несёт ОБА параметра режима — и толщину, и направление, — потому что
    /// иначе «изменилось ровно запрошенное» неотличимо от «изменилось ещё и это».
    /// </remarks>
    public double? ThicknessMm { get; init; }

    /// <summary>
    /// Новое направление стенки оболочки (family=<c>shell</c>): <c>true</c> — внутрь, <c>false</c> —
    /// наружу. Отдельное поле, а не общий <c>direction</c> фаски.
    /// </summary>
    public bool? ThinInward { get; init; }

    /// <summary>
    /// Новый НАБОР удаляемых граней оболочки (family=<c>shell</c>) — полная замена, а не добавление
    /// к имеющимся: передаётся то, что должно остаться снятым. Грани — из
    /// <c>kompas_read_topology</c>, а не позиции в коллекции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (проба <c>--b5</c>, шаг B5.14): на одном признаке короба
    /// 100×80×10 оболочка <c>t = 2</c> внутрь со снятой верхней гранью даёт <c>21632</c> при
    /// <c>11</c> гранях; добавление второй грани (нижней, 100×80) делает полость сквозной и даёт
    /// <c>7040</c> при <c>10</c> гранях; возврат к прежнему набору возвращает <c>21632</c> при
    /// <c>11</c>. Отрицательный контроль стоит там же: повторная запись того же набора объём не
    /// двигает.
    /// </para>
    /// <para>
    /// <b>Почему это отдельный маршрут, а не перенос со скругления.</b> Тот же приём
    /// (<c>Clear()</c> + <c>Add()</c> по <c>ksEntityCollection</c>) у СКРУГЛЕНИЯ измеренно НЕ
    /// работал (строка <c>FL04r</c>: набор схлопывался, объём возвращался к пластине). Поэтому
    /// оболочка проверена своим прогоном, а не выведена по аналогии.
    /// </para>
    /// <para>
    /// <b>Пустой список отвергается</b> и здесь, и по той же измеренной причине, что при создании:
    /// при пустом списке операция принимается (<c>Create/Update = true</c>), а тело не меняется —
    /// объём остаётся <c>80000</c> при <c>6</c> гранях.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? FaceRefs { get; init; }

    /// <summary>
    /// Сменить опорный эскиз того же признака (правка опоры по docs/05 §4.3). Нужен для режимов,
    /// где глубины как параметра нет: у сквозного вырезания число игнорируется solver'ом, поэтому
    /// «изменить параметр» там возможно только сменой профиля.
    /// </summary>
    public string? SketchRef { get; init; }

    /// <summary>
    /// Новый первый катет фаски (мм).family=<c>chamfer</c>; для выдачиваний не применяется.
    /// Измерено пробой F.3/F.5: <c>SetChamferParam</c> на признаке 2×2→3×3 меняет объём ровно на
    /// 20·(d₂²−d₁²)·… и значение перечитывается с нового объекта определения.
    /// </summary>
    public double? Distance1Mm { get; init; }

    /// <summary>Новый второй катет фаски (мм).</summary>
    public double? Distance2Mm { get; init; }

    /// <summary>Новый угол фаски в градусах; правится только маршрутом API7.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>Новая сторона фаски (API5 transfer / API7 Direction).</summary>
    public bool? Direction { get; init; }

    /// <summary>
    /// Новый угол ВРАЩЕНИЯ (family=<c>rotation</c>), градусы. Отдельное поле, а не общий
    /// <see cref="AngleDeg"/>: у фаски этот член означает угол фаски, у вращения — угол развёртки,
    /// и одна и та же величина с одним именем для двух разных семейств сделала бы ответ
    /// неоднозначным («угол применён» — куда?). Вызов с <see cref="AngleDeg"/> на признаке вращения
    /// отвергается INVALID_ARGUMENT с указанием на это поле, а не толкуется молча.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026.</b> Проба <c>FullTurnProbe</c>, шаг <c>F.2</c>: на ОДНОМ
    /// признаке (эталон R20 H40, ось по двум точкам модели) смена угла 360 → 180 → 360 дала объёмы
    /// <c>50265.4824574366 → 25132.7412287183 → 50265.4824574366</c> при габарите
    /// <c>z[−20,20] → z[−0,20] → z[−20,20]</c>, то есть геометрическое изменение, а не только
    /// записанное число. Порядок «запись угла в <c>IRotated.Angle[true]</c> →
    /// <c>IRotated.Update()</c> → <c>RebuildModel</c>/<c>RebuildDocument</c>» — часть контракта, как
    /// и при создании.
    /// </para>
    /// <para>
    /// <b>Только угол.</b> Смена профиля и оси существующего вращения этим вызовом НЕ выполняется:
    /// это отдельные маршруты, и ни один из них не измерялся на вращении. Поэтому
    /// <see cref="SketchRef"/> на признаке вращения отвергается, а не игнорируется.
    /// </para>
    /// </remarks>
    public double? RotationAngleDeg { get; init; }

    /// <summary>
    /// Новое направление вращения (family=<c>rotation</c>). <c>reverse</c> отвергается до мутации:
    /// измерено (R.26.sector), что оно не строит ничего.
    /// </summary>
    public RotationDirection? RotationDirection { get; init; }

    /// <summary>
    /// Вид преобразования при правке признака ИЗМЕНЕНИЯ ПОЛОЖЕНИЯ (family=<c>reposition</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельные поля, а не повторное создание.</b> Наряд B3 §5 требует, чтобы правка
    /// меняла параметры СУЩЕСТВУЮЩЕГО признака относительно его ИСХОДНЫХ входов, а не
    /// применялась к текущему положению. Повторный вызов <c>kompas_reposition</c> этому не
    /// удовлетворяет по построению: он создаёт ВТОРОЙ признак и сдвигает тело от текущего
    /// положения, то есть смещение накапливается. Измерено пробой RP.6: правка признака[0]
    /// повторной записью того же вектора оставляет габарит <c>(17,−11,13)…(37,−1,18)</c>, а
    /// возврат вектора в ноль возвращает тело домой — параметр применяется к исходному телу.
    /// </para>
    /// <para>
    /// <b>Имена с префиксом, как у <see cref="RotationAngleDeg"/>.</b> У <c>kompas_reposition</c>
    /// те же величины называются <c>kind</c>, <c>vector_mm</c>, <c>axis_point_mm</c> и
    /// <c>angle_deg</c>. Здесь <c>angle_deg</c> уже занято углом ФАСКИ, поэтому угол поворота
    /// называется <see cref="RepositionAngleDeg"/>: одно имя для двух разных величин сделало бы
    /// ответ неоднозначным («угол применён» — куда?).
    /// </para>
    /// </remarks>
    public RepositionKind? RepositionKind { get; init; }

    /// <summary>Новый вектор переноса, мм, модельные координаты. Обязателен для <c>translate</c>.</summary>
    public IReadOnlyList<double>? RepositionVectorMm { get; init; }

    /// <summary>Новая точка на оси поворота, мм. Обязательна для <c>rotate</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisPointMm { get; init; }

    /// <summary>Новое направление оси поворота. Взаимоисключающе с <c>reposition_axis_point2_mm</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisDirectionMm { get; init; }

    /// <summary>Новая вторая точка оси поворота. Взаимоисключающе с <c>reposition_axis_direction_mm</c>.</summary>
    public IReadOnlyList<double>? RepositionAxisPoint2Mm { get; init; }

    /// <summary>Новый угол поворота, градусы. Обязателен для <c>rotate</c>.</summary>
    public double? RepositionAngleDeg { get; init; }

    /// <summary>
    /// Аналитическое ожидание габарита тела после правки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нужно там, где объём ожиданием служить не может. У жёсткого преобразования объём —
    /// ИНВАРИАНТ, поэтому <see cref="ExpectedVolumeMm3"/> у семейства <c>reposition</c>
    /// подтверждает только то, что преобразование осталось жёстким, но не то, что тело встало
    /// куда просили: при переносе «сдвинулось» и «осталось» по объёму неразличимы. Габарит
    /// различает, и это же требование записано в приёмке (строка `B3L.04`: у переноса объём до и
    /// после равен 1 000, и строка, сверяющая одни объёмы, прошла бы на полном бездействии).
    /// </para>
    /// <para>
    /// <b>Почему не чтением параметра обратно.</b> Первая редакция правки сверяла записанный
    /// перенос с <c>IBodyReposition.Position.X/Y/Z</c>. Измерено 18.09.2026: этот член перенос НЕ
    /// несёт — после <c>InitByMatrix3D</c> с вектором <c>(7,−11,13)</c> он читается как
    /// <c>(0,0,0)</c>, тогда как габарит тела верен. Сверять с членом, который не хранит значение,
    /// значит измерять прибор, а не продукт, поэтому проверка перенесена на геометрию.
    /// </para>
    /// </remarks>
    public BoundingBoxDto? ExpectedBboxMm { get; init; }

    /// <summary>
    /// Новая опора признака РАЗДЕЛЕНИЯ (<c>family=split</c>) или ОТСЕЧЕНИЯ
    /// (<c>family=cut_by_plane</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем плоскость, а не повторный вызов.</b> Наряд B3 §5 требует, чтобы правка меняла
    /// параметры СУЩЕСТВУЮЩЕГО признака относительно его ИСХОДНЫХ входов. Повторный
    /// <c>kompas_split_body</c> этому не удовлетворяет: он создаёт ВТОРОЙ признак разделения и режет
    /// уже полученную часть. Измерено 18.09.2026 (проба <c>--split</c>, шаг SP.9): правка опоры
    /// существующего признака переводит части <c>6 000 / 18 000</c> при <c>x = 10</c> в
    /// <c>9 000 / 15 000</c> при <c>x = 15</c> при неизменном числе признаков <c>1 → 1</c> и
    /// неизменной сумме <c>24 000</c>.
    /// </para>
    /// <para>
    /// <b>Поправка 18.09.2026: здесь стояла ссылка на шаг SP.6, и она вводила в заблуждение.</b>
    /// Шаг SP.6 назван «Правка той же плоскости: x=10 → x=15», но его код переносит три точки
    /// построения плоскости, а не правит опору признака; шаг SP.7, в свою очередь, строит отсечение
    /// ЗАНОВО в свежем документе под каждое значение <c>Direction</c>. Ни один из них маршрут
    /// «записать в существующий признак другую плоскость» не проверял. По этой ссылке маршрут был
    /// выведен из НАЗВАНИЯ шага, а не из его кода, и реализация подстановки чужой плоскости
    /// измеренно не работала (<c>Update() = true</c>, части <c>6 000 / 18 000</c> прежние).
    /// Авторитетная ссылка — SP.9 с отрицательным контролем E-B; SP.6 остаётся верным как факт о
    /// плоскости, но не как доказательство правки признака.
    /// </para>
    /// <para>
    /// Форма та же, что у одноимённого поля <c>kompas_split_body</c> и <c>kompas_cut_body</c>
    /// (<c>#/$defs/cut_plane</c>): <c>plane_ref</c> на существующую плоскость либо
    /// <c>point_mm</c> + <c>normal_mm</c> в модельных координатах. Сторона задаётся знаком
    /// <c>s = n·(p − p₀)</c>. Нулевая и нечисловая нормаль отвергаются до COM.
    /// </para>
    /// <para>
    /// <b>Маршрут — перенос ТОЧЕК СОБСТВЕННОЙ опоры признака, и это измерено, а не выведено.</b>
    /// Шаг <c>SP.9</c> пробы <c>--split</c> (прогон <c>c9cd7660468c44aa97b410e253ee2cb1</c>) сравнил
    /// два маршрута на одном и том же признаке. <c>CutObjects</c> читается обратно как ОДИН объект
    /// (не массив) и отвечает <c>IPlane3DBy3Points</c>; перенос его трёх точек построения с
    /// последующим <c>Update()</c> и пересборкой меняет геометрию (E-A: части
    /// <c>9 000 / 15 000</c>, признаков разделения <c>1 → 1</c>). Подстановка ВТОРОЙ, заново
    /// построенной плоскости в <c>CutObjects</c> при <c>Update() = true</c> результат НЕ меняет —
    /// отрицательный контроль E-B. Поэтому реализация правит опору признака, а не подставляет
    /// другую плоскость, и <c>Update() = true</c> здесь доказательством не считается: геометрия
    /// сверяется отдельно.
    /// </para>
    /// <para>
    /// <b>Цена маршрута и границы.</b> Вспомогательная плоскость при правке НЕ создаётся: объект
    /// опоры берётся у самого признака, поэтому документ не накапливает неиспользованные плоскости.
    /// <c>plane_ref</c> на признаке разделения/отсечения отвергается кодом
    /// <c>CAPABILITY_UNAVAILABLE</c>: подстановка чужой плоскости измеренно ничего не делает (E-B),
    /// а доказать, что ссылка указывает именно на опору ЭТОГО признака, нечем. Поле
    /// <c>base</c> («базовая плоскость со смещением») отвергается там же.
    /// </para>
    /// </remarks>
    public CutPlaneDto? Plane { get; init; }

    /// <summary>
    /// Новая оставляемая сторона для <c>family=cut_by_plane</c>: <c>positive</c> — <c>s &gt; 0</c>,
    /// <c>negative</c> — <c>s &lt; 0</c>.
    /// </summary>
    /// <remarks>
    /// Соответствие измерено шагом SP.7: <c>ICut.Direction = true</c> оставляет сторону в
    /// направлении нормали (<c>s &gt; 0</c>, V = 18 000), <c>false</c> — противоположную
    /// (<c>s &lt; 0</c>, V = 6 000). При правке стороны признак тот же: правка меняет ОСТАТОК, а не
    /// число тел.
    /// </remarks>
    public string? KeepSide { get; init; }

    /// <summary>
    /// Аналитическое ожидание объёмов ЧАСТЕЙ после правки разделения (<c>family=split</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Единственная величина, которая подтверждает правку разделения. Сумма объёмов частей при
    /// правке не меняется (24 000 и при <c>x = 10</c>, и при <c>x = 15</c>), поэтому сохранение
    /// объёма — не подтверждение, а инвариант: строка, сверяющая только сумму, прошла бы на полном
    /// бездействии. Различает части только их ПООБЪЁМНЫЙ состав: <c>[6 000, 18 000]</c> против
    /// <c>[9 000, 15 000]</c>.
    /// </para>
    /// <para>
    /// Сопоставление — по совпадению с ДОПУСКОМ объёма (0.01 мм³ абс. / 1e-6 отн.) и с учётом
    /// кратности: каждому ожидаемому объёму находится своё тело, одно тело не может закрыть два
    /// ожидания. Порядок частей значения не имеет — ядро вправе переставить их местами.
    /// </para>
    /// <para>
    /// Если ожидание объявлено и не совпало, вызов возвращает <c>NO_GEOMETRY_CHANGE</c> с
    /// <c>partial_effects=true</c> и фактическим составом в <c>details</c>: это признак того, что
    /// параметр применён не к исходным входам признака.
    /// </para>
    /// </remarks>
    public IReadOnlyList<double>? ExpectedPartVolumesMm3 { get; init; }

    /// <summary>
    /// Новый ВИД существующей булевой операции (family=<c>boolean</c>): <c>union</c>, <c>difference</c>
    /// или <c>intersect</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026.</b> Проба <c>--boolean</c>, шаг <c>BO.11</c>, прогон
    /// <c>a2f5cf0a2ad342c59c36807101a65d51</c> (журнал <c>docs/acceptance/api7/boolean-ops.json</c>).
    /// Эталон §6.1: <c>A ∪ B</c> — 36 000 в габарите <c>(0,0,0)…(60,30,20)</c>, <c>A − B</c> — 12 000
    /// в <c>x ≤ 20</c>, <c>A ∩ B</c> — 12 000 в <c>x ∈ [20,40]</c>. Перезапись
    /// <c>IBoolean.BooleanType</c> на СУЩЕСТВУЮЩЕМ признаке с последующим <c>Update()</c> и пересборкой
    /// меняет геометрию: E-A даёт <c>36 000 → 12 000</c> (габарит <c>x ≤ 20</c>), E-D —
    /// <c>12 000 → 36 000</c>, признаков <c>1 → 1</c>.
    /// </para>
    /// <para>
    /// <b>Почему контроль здесь обязателен и каков он.</b> Объём разности и объём пересечения на
    /// эталоне РАВНЫ (12 000), поэтому опыт E-C идёт от разности к пересечению: объём остаётся 12 000,
    /// а габарит меняется на <c>x ∈ [20,40]</c>. Строка, сверяющая только объём, прошла бы на полном
    /// бездействии. Опыт E-E подтверждает, что применяет именно пара «запись → <c>Update()</c>»:
    /// запись без <c>Update()</c> (но с пересборкой) геометрию не меняет.
    /// </para>
    /// <para>
    /// <b>Цена и границы.</b> Опорные тела существующей операции этим полем НЕ меняются:
    /// <c>BaseObject</c> и <c>ModifyObjects</c> доступны на запись, но правка набора инструментов не
    /// измерялась, поэтому вызов, меняющий только вид, их не трогает. Значение <c>ksBooleanUnknown</c>
    /// (0) сеттер нормализует в <c>ksUnion</c> — измерено (E-B): клиент, записавший 0, получит
    /// объединение, а не отказ, и это записано здесь, чтобы такое поведение не было сюрпризом.
    /// </para>
    /// <para>
    /// <b>Имя поля.</b> Здесь оно намеренно совпадает с параметром <c>operation</c> инструмента
    /// <c>kompas_boolean</c>: это одна и та же величина. Если другому семейству когда-нибудь
    /// понадобится свой «вид операции», оно обязано взять УТОЧНЁННОЕ имя (как это сделал угол
    /// поворота — <see cref="RepositionAngleDeg"/>), а не завести второе поле <c>Operation</c>.
    /// </para>
    /// </remarks>
    public BooleanOperation? Operation { get; init; }

    /// <summary>
    /// Новый радиус скругления (мм); family=<c>fillet</c>. Правится только маршрутом API7:
    /// в API5 у <c>ksFilletDefinition</c> радиус есть, но запись в него на существующем признаке
    /// НЕ применяется — измерено 16.09.2026 строкой FL04r: сеттер возвращает успех, а
    /// <c>entity.Update()</c> + <c>RebuildDocument()</c> оставляют объём прежним (см.
    /// <c>Api5Session.UpdateFilletRadius</c>). Поэтому радиус пишется в <c>IFillet.Radius1</c>
    /// на живой модели, а признак API5 сопоставляется с ним по СОВПАДЕНИЮ РАДИУСА, а не по имени
    /// (F.8: имя, заданное в API5, в API7 читается иначе).
    /// </summary>
    public double? RadiusMm { get; init; }

    /// <summary>
    /// Новый НАБОР рёбер скругления (family=<c>fillet</c>) — полная замена, а не добавление к
    /// имеющимся: передаётся то, что должно остаться. Рёбра — из <c>kompas_read_topology</c>, а не
    /// позиции в коллекции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут — API7, и он измерен.</b> Проба H-2 (<c>docs/acceptance/api7/fillet-base-objects.md</c>,
    /// 14 PASS / 0 FAIL / 0 UNKNOWN, собственный <c>run_id</c>, воспроизведено четырьмя прогонами):
    /// <c>IModelContainer.Fillets[i] → IFillet</c>, чтение и запись <c>IFillet.BaseObjects</c>
    /// (полная замена набора), затем обязательный <c>IFillet.Update()</c>. Все объекты берутся с
    /// ЖИВОЙ модели после <c>save → close → reopen</c>; ни один объект, захваченный при создании
    /// скругления, не используется. Адресация проверена на модели с ДВУМЯ скруглениями одного
    /// радиуса (H2.7): правка одного признака не задела соседний.
    /// </para>
    /// <para>
    /// <b>ОТРИЦАТЕЛЬНЫЙ РЕЗУЛЬТАТ ПО ПРЕЖНЕМУ МАРШРУТУ ОСТАЁТСЯ ВЕРНЫМ.</b> Маршрут через
    /// определение API5 (<c>ksFilletDefinition.array()</c> → <c>Clear()</c> → <c>Add()</c> →
    /// <c>entity.Update()</c>) правку набора на существующем признаке НЕ даёт: измерено 16.09.2026
    /// восемью пробами, и с появлением рабочего маршрута не отменяется. После скругления угловые
    /// рёбра в топологии отсутствуют (0 из 4 — углы заняты цилиндрическими гранями), исходные
    /// угловые рёбра отзывает само создание скругления (<c>STALE_REFERENCE</c> до всякой правки), а
    /// существующие вертикальные рёбра скруглённых углов признак не удерживает: набор схлопывается
    /// (<c>edges_read_back=0</c>), объём возвращается к пластине. Ненулевой <c>edges_read_back</c>
    /// получается только вторым вызовом подряд — и это пересборка с нуля, а не правка набора.
    /// Именно поэтому маршрут в адаптере заменён, а не оставлен веткой.
    /// </para>
    /// <para>
    /// <b>Ранняя проба H</b> (<c>docs/acceptance/api7/fillet-edge-set.md</c>) сообщала о применении
    /// набора (сокращение дало 79961.3716694115, расширение — 79922.7433388231). Числа верны как
    /// геометрия и неверны как доказательство правки набора: проба мерила пересчёт признака по
    /// ПОДСТАВЛЕННОМУ входу, а не правку набора существующего признака. Вердикт изменён не пробой H,
    /// а пробой H-2.
    /// </para>
    /// <para>
    /// <b>Границы, не переносимые на общий вывод:</b> расширение набора на эталоне 100×80×10 не
    /// измерено (у пластины ровно четыре вертикальных угла, пятого нет); измерены сокращение
    /// (4→3) и замена при неизменном размере (1→1).
    /// </para>
    /// <para>
    /// <b>Поправка 17.09.2026: здесь стояло «ссылки входов признака и рёбра тела лежат в РАЗНЫХ
    /// контекстах», и это ОПРОВЕРГНУТО замером.</b> Полосы ссылок СОСЕДНИЕ — в <c>FL10x</c> вход
    /// признака <c>1073742308</c> против перенесённого ребра тела <c>1073742309</c>; тип у обоих
    /// <c>ksObjectEdge</c>, обе ссылки устойчивы к повтору, различаются лишь адресные привязки RCW.
    /// Это обычная двойственность API5/API7. Вывод «сопоставить вход с ребром тела по
    /// <c>Reference</c> нельзя» из прежнего замера не следовал: различие <c>…065-67</c> против
    /// <c>…080</c> — разность ЗНАЧЕНИЙ. Рёбра тела предъявлять можно, и <c>FL10x</c> это
    /// подтверждает.
    /// </para>
    /// <para>
    /// <b>СОСТОЯНИЕ ПРОДУКТА: маршрут перенесён в адаптер, приёмка ПРОЙДЕНА (17.09.2026).</b>
    /// <c>scripts/mcp-smoke.py --fillet-only</c> даёт 46 строк, 0 FAIL: сокращение <c>FL10</c> (4→3,
    /// <c>V=79942.0575041173</c> при аналитике <c>79942.05750411731</c>), <c>FL10s</c> (3→2),
    /// <c>FL10b</c> (2→1) и замена при неизменном размере <c>FL10x</c> (1→1) — все
    /// <c>level=geometry_checked</c>.
    /// </para>
    /// <para>
    /// <b>Корневая причина прежнего отказа названа измерением и оказалась НЕ той, что предполагалась.
    /// </b> Дело было не в маршруте записи (он работал все эти прогоны — <c>FL10x</c> проходил) и не
    /// в «невыразимости» сокращения: свойство <c>base_object_refs</c> попросту НЕ БЫЛО ОБЪЯВЛЕНО в
    /// схеме инструмента <c>kompas_update_feature</c>, поэтому Host с <c>additionalProperties:
    /// false</c> отвергал вызов валидацией ещё ДО COM, и сокращение не доходило до адаптера. Маршрут
    /// и валюта были написаны верно; не хватало публикации. Прежние объяснения («признак того же
    /// прогона не опознаётся, <c>family=null</c>», «сокращение невыразимо валютой») сняты замером.
    /// </para>
    /// <para>
    /// <b>ГРАНИЦА, которая этим полем не закрывается — РАСШИРЕНИЕ набора.</b> Измерено <c>FL25</c> на
    /// Г-образной пластине со СВОБОДНЫМИ углами: расширение (1→2, 2→3) не выражается ни одной из двух
    /// валют. Полная замена по рёбрам тела означает «построй скругление заново по этим рёбрам» —
    /// прежнее ребро не сохраняется, тогда как расширению нужно ровно обратное. Исход — отказ ПОСЛЕ
    /// мутации: <c>GEOMETRY_FAILED</c> с <c>partial_effects=true</c>. До 17.09.2026 этот исход
    /// возвращался как <c>err=None</c> и <c>level=call_returned</c>, то есть исчезновение признака
    /// выдавалось за успешную правку; теперь исчезновение признака — отказ. Правьте СОСТАВ
    /// (сокращение и замену), не добавляйте рёбра. Подробности — <c>docs/STATUS.md</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? EdgeRefs { get; init; }

    /// <summary>
    /// НОВЫЙ набор по СОБСТВЕННЫМ входам признака (family=<c>fillet</c>) — вторая валюта того же
    /// предмета правки, добавленная 17.09.2026 как расширение контракта.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем второе поле, когда есть <see cref="EdgeRefs"/>.</b> Сокращение набора и замена
    /// состава — разные операции с разной валютой, и это ИЗМЕРЕНО, а не выведено из удобства:
    /// сокращение выражается только объектами, прочитанными ИЗ <c>BaseObjects</c> признака, а
    /// <see cref="EdgeRefs"/> принимает рёбра ТЕЛА. Приёмка подтверждает обе валюты порознь:
    /// сокращение <c>FL10</c> (4→3, <c>V=79942.0575041173</c> при аналитике <c>79942.05750411731</c>),
    /// <c>FL10s</c>, <c>FL10b</c> — этим полем; замена при неизменном размере <c>FL10x</c> (1→1) —
    /// <see cref="EdgeRefs"/>, <c>level=geometry_checked</c> у всех. То есть <see cref="EdgeRefs"/>
    /// выражает замену, но НЕ сокращение. Отдельно: ранняя приёмка предъявляла РЁБРА ТЕЛА именно
    /// этому полю и схлопывала признак в пластину — предъявлять их сюда по-прежнему нельзя, они
    /// отвергаются по виду ссылки.
    /// </para>
    /// <para>
    /// <b>Почему нельзя было обойтись одним полем.</b> Собственный вход признака — это
    /// <c>IModelObject</c> из <c>IFillet.BaseObjects</c>, и адресуется он числом
    /// <c>IModelObject.Reference</c> (оно же в <c>fillet.base_object_references</c> от
    /// <c>kompas_get_feature</c>). Строки реестра <c>edge:&lt;hex&gt;</c> у него НЕТ: реестр выдаёт
    /// такие строки рёбрам ТЕЛА, а вход признака — не ребро тела. Подставить число в
    /// <see cref="EdgeRefs"/> нельзя по типу. Набор из <c>base_object_references</c> —
    /// единственная измеренная валюта сокращения.
    /// </para>
    /// <para>
    /// <b>Форма.</b> Ссылка — строка <c>input:&lt;hex Reference&gt;</c>, где <c>&lt;hex&gt;</c> —
    /// число из <c>base_object_references</c> в шестнадцатеричном виде (<c>1073742308</c> →
    /// <c>input:40000164</c>). Это тот же формат, которым сервер уже отдаёт ссылки наружу, поэтому
    /// клиент подставляет числа как есть. Набор — ПОЛНАЯ замена, а не добавление: передаётся то,
    /// что должно остаться. Пустой список отвергается (<c>INVALID_ARGUMENT</c>).
    /// </para>
    /// <para>
    /// <b>С <see cref="EdgeRefs"/> в одном вызове не сочетается</b>
    /// (<c>INVALID_ARGUMENT</c> до мутации): два разных состава в одном запросе неразличимы в
    /// ответе, и «применилось одно из двух» выглядело бы как «применилось и то, и другое».
    /// </para>
    /// <para>Неизвестная или чужая ссылка <c>input:</c> отвергается до мутации. Ссылка, выданная
    /// другому документу, отвергается как <c>STALE_REFERENCE</c> (<c>FL26</c>), входы чужого признака
    /// того же документа — как <c>CAPABILITY_UNAVAILABLE</c> (<c>FL27</c>); в обоих случаях модель не
    /// меняется.</para>
    /// <para><b>ГРАНИЦА: расширение набора этим полем НЕ выражается.</b> Новое ребро не является
    /// собственным входом признака, а смешивать валюты нельзя. Измерено <c>FL25</c> (Г-образная
    /// пластина со свободными углами): расширение (1→2, 2→3) заканчивается отказом ПОСЛЕ мутации с
    /// <c>partial_effects=true</c>. До 17.09.2026 тот же исход возвращался как успех с
    /// <c>level=call_returned</c> — это было неверное сообщение, и оно исправлено: исчезновение
    /// признака теперь отказ, а не «успех с пониженным уровнем».</para>
    /// </remarks>
    public IReadOnlyList<string>? BaseObjectRefs { get; init; }

    /// <summary>Аналитическое ожидание объёма после правки (G03: 100·80·12 = 96000 мм³).</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    /// <summary>
    /// Тело, на которое направлена область применения признака при правке (family=<c>cut_by_plane</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем поле на правке.</b> Наряд B3 §3.2 требует «назначать и ВОССТАНАВЛИВАТЬ область
    /// применения при create/edit/rebuild/save-reopen». На создании она назначается, а на правке её
    /// можно только перечитать: если признак оказался в умолчании «Все объекты» (справка
    /// <c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>), то перенос опоры снимет материал у
    /// посторонних тел — и без этого поля починить признак было бы нечем, кроме удаления и сборки
    /// заново.
    /// </para>
    /// <para>
    /// <b>Отсутствие поля — не «всё равно».</b> Если поле не задано, адаптер обязан ПРОЧИТАТЬ область
    /// применения живого признака до мутации и отказать, если она не адресована
    /// (<c>ChooseType ≠ ksChBodies</c> либо пустой список тел). Молчаливая правка признака в умолчании
    /// снимала бы материал у посторонних тел ровно так же, как на создании.
    /// </para>
    /// <para>
    /// <b>Маршрут измерен 19.09.2026</b> пробой <c>--cut-area</c>, шаги CA.4 (адресация принята:
    /// A=12000, S=1000), CA.5 (отрицательный контроль другим телом: A=18000, S=500), CA.6 (правка
    /// опоры адресность сохраняет) и CA.7 (адресность переживает <c>save → close → open</c>).
    /// </para>
    /// </remarks>
    public string? TargetBodyRef { get; init; }

    /// <summary>
    /// Новые параметры признака МАССИВА (family=<c>pattern</c>, очередь B4: SM-18 / SM-19 / SM-23).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему набор вынесен в отдельное поле, а не разложен по плоским полям команды.</b>
    /// Семейство <c>pattern</c> — единственное, у которого предмет правки принадлежит ТРЁМ разным
    /// интерфейсам API7 сразу, и часть имён между ними совпадает только по виду. Плоское поле
    /// <c>count2</c> на самой команде читалось бы как «применимо к любому массиву», тогда как у
    /// сетки это экземпляры по оси 2, а у кругового — по КОЛЬЦУ; плоское
    /// <c>save_initial_orientation</c> выглядело бы применимым к сетке, где такого члена нет
    /// вовсе. Группировка делает границу семейства видимой в самом контракте, а не только в
    /// документации.
    /// </para>
    /// <para>
    /// <b>Опознание признака идёт по САМОМУ ПОЛЮ, а не по номеру типа в дереве.</b> У остальных
    /// правимых семейств ветка выбирается по <c>entity.type</c>, и это измеренный номер. Для массива
    /// номер типа в дереве в этом сеансе не измерялся, поэтому он здесь и не угадывается: признак
    /// сопоставляется с элементом <c>IModelContainer.FeaturePatterns</c> тем же прибором, что и
    /// чтение (<see cref="PatternReadCommand"/>), — по имени оболочки дерева и штампу обновления.
    /// Если поле не задано, ветка не выбирается вовсе, и поведение правки не меняется.
    /// </para>
    /// <para>
    /// <b>Смешение с другими семействами отвергается до мутации.</b> Вызов, в котором вместе с
    /// <c>pattern</c> пришло поле другого семейства (глубина, радиус, плоскость, вид булевой
    /// операции), не выполняется: «применилось одно из двух» неотличимо потом от «применилось и то,
    /// и другое».
    /// </para>
    /// </remarks>
    public PatternEditDto? Pattern { get; init; }

    /// <summary>
    /// Новый диаметр отверстия (family=<c>hole</c>), мм: пилота у цековки и зенковки, самого
    /// отверстия у глухого и сквозного цилиндрического.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (зонд <c>scratch/_hole_edit_probe.py</c>, нога 2 — сырой
    /// помощник <c>scratch/hole-edit-raw</c>, отчёт <c>docs/acceptance/api7/hole-modes.md</c> § M.6):
    /// существующее отверстие берётся документированным членом <c>IHoles3D.Hole3D[index]</c>,
    /// в него записываются члены своего режима, применяется <c>IModelObject.Update()</c>, затем
    /// перестроение. Сквозное цилиндрическое Ø10 → Ø12 на плите 10 мм сняло
    /// <c>345.575191895</c> мм³ = π·(36−25)·10, и это пережило <c>save → close → reopen</c>.
    /// </para>
    /// <para>
    /// <b>Адрес признака не угадывается.</b> Соответствие «признак дерева ↔ запись <c>Holes3D</c>»
    /// доказывается единственностью отверстия в документе: при нескольких отверстиях вызов
    /// отвергается <c>CAPABILITY_UNAVAILABLE</c> до COM, потому что запись в <c>Holes3D[0]</c>
    /// изменила бы чужое отверстие. Имя признака идентификатором не является: оно не переживает
    /// переход API5↔API7 (измерено на фаске, F.8).
    /// </para>
    /// <para>
    /// <b>Режим правкой не меняется.</b> Режим существующего признака читается из модели
    /// (<c>IHole3D.HoleType</c>) и служит рамкой: поле чужого режима отвергается
    /// <c>INVALID_ARGUMENT</c> до COM с перечнем своих полей. Смена режима не измерялась.
    /// </para>
    /// </remarks>
    public double? DiameterMm { get; init; }

    /// <summary>
    /// Новый диаметр выточки цековки (режим <c>through_counterbore</c>), мм. Поле ЧУЖОГО режима для
    /// остальных: на глухом и на зенковке отвергается <c>INVALID_ARGUMENT</c>.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (шаг M.6): выточка Ø18×4 → Ø20×5 сняла <c>474.380490692</c>
    /// мм³ сверх прежнего — разность колец π/4·(D²−d²)·h (1178.097244573 против 703.716754404,
    /// формула M.2). Пишется в <c>ISpotfacingHoleParameters.SpotfacingDiameter</c>; у чужого режима
    /// этот интерфейс на объекте НЕДОСТИЖИМ — измерено контролем (в) зонда.
    /// </remarks>
    public double? CounterboreDiameterMm { get; init; }

    /// <summary>Новая глубина выточки цековки (режим <c>through_counterbore</c>), мм.</summary>
    public double? CounterboreDepthMm { get; init; }

    /// <summary>
    /// Новый диаметр УСТЬЯ зенковки (режим <c>through_countersink</c>), мм — устья, а не пилота:
    /// пилот задаётся <see cref="DiameterMm"/>.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен 20.09.2026</b> (шаг M.6): устье Ø20 → Ø24 при 90° сняло
    /// <c>605.280184592</c> мм³ сверх прежнего — разность <c>π·h/3·(rM² + rP·rM − 2·rP²)</c> при
    /// производной <c>h = (rM − rP)/tan(угол/2)</c> (1128.878960190 против 523.598775598).
    /// </remarks>
    public double? CountersinkDiameterMm { get; init; }

    /// <summary>Новый угол зенковки (режим <c>through_countersink</c>), градусы.</summary>
    public double? CountersinkAngleDeg { get; init; }

    /// <summary>
    /// Аналитическое ожидание СНЯТОГО правкой материала, мм³: <c>объём_до − объём_после</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Знак — часть определения величины, а не оформление.</b> Отверстие снимает материал, поэтому
    /// у «стало глубже» дельта положительна, а у «стало мельче» — отрицательна; модуль сравнивать
    /// нельзя. Ровно на этом ошиблась первая редакция зонда: она сравнивала приращение объёма
    /// (положительное) с аналитическим «снято» и давала ложное «не совпало» на верной геометрии —
    /// дефект ПРИБОРА, а не факт о продукте.
    /// </para>
    /// <para>
    /// <b>Почему дельта, а не полный объём.</b> Полный объём документа зависит от всего, что в нём
    /// есть; дельта привязана к правке и потому различает «применилось ровно запрошенное» от
    /// «применилось не то». <see cref="ExpectedVolumeMm3"/> тоже принимается — он проверяется
    /// отдельно и независимо.
    /// </para>
    /// <para>
    /// Без объявленного ожидания правка не подтверждается числом: уровень остаётся
    /// <c>call_returned</c>, и в <c>unverified_aspects</c> появляется
    /// <c>expected_volume_delta_not_supplied</c>.
    /// </para>
    /// </remarks>
    public double? ExpectedVolumeDeltaMm3 { get; init; }
}

/// <summary>
/// Подавить или восстановить признак. Маршрут измерен пробой L.7 (12.09.2026):
/// <c>ksFeature.excluded = true</c> снимает тело выдавливания до объёма пластины,
/// <c>false</c> возвращает объём обратно; число признаков не меняется.
/// </summary>
public sealed record SuppressFeatureCommand
{
    public required string FeatureRef { get; init; }

    /// <summary>true — подавить, false — восстановить.</summary>
    public required bool Suppressed { get; init; }

    /// <summary>
    /// Аналитическое ожидание объёма после подавления (или возврата). Без него подтверждается
    /// только состояние признака, и результат честно помечается недоказанной геометрией.
    /// </summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Удаление признака. Зависимые перечисляются ДО обращения к КОМПАС; достоверного перечня ни
/// API5, ни API7 не даёт (проба L.8), поэтому после удаляемого признака в дереве возвращаются
/// кандидаты, а их наличие требует <see cref="ConfirmDependents"/>.
/// </summary>
public sealed record DeleteFeatureCommand
{
    public required string FeatureRef { get; init; }

    /// <summary>Явное согласие удалить признак, после которого в дереве есть другие.</summary>
    public bool ConfirmDependents { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Тело в результате операции B3: ссылка, объём, габарит и признак многокусочности.
/// </summary>
/// <remarks>
/// Отдельная запись, а не <c>BodyRowDto</c>, по двум причинам. Во-первых, приёмка B3 считается по
/// ОБЪЁМАМ, а <c>BodyRowDto</c> объёма не несёт. Во-вторых, <c>MultiBodyParts</c> здесь не
/// украшение: ядро представляет результат из нескольких кусков ОДНИМ телом с несколькими кусками
/// (измерено 18.09.2026, шаг BO.8), и без этого поля «одно тело» читалось бы как «материал целый».
/// <para>
/// <c>VolumeMm3</c> равно <c>null</c>, когда объём не прочитан: «не прочитано» и «ноль» — разные
/// ответы, и смешивать их нельзя.
/// </para>
/// </remarks>
public sealed record SolidBodyDto
{
    public required string BodyRef { get; init; }

    /// <summary><c>solid</c> или <c>sheet</c>.</summary>
    public required string Kind { get; init; }

    public double? VolumeMm3 { get; init; }

    public required BoundingBoxDto Bbox { get; init; }

    public required int FaceCount { get; init; }

    /// <summary>
    /// Тело состоит из нескольких несвязных кусков. Именно так ядро представляет распавшийся
    /// результат, и именно это поле отличает «одно тело из двух кусков» от «одно тело целое».
    /// </summary>
    public required bool MultiBodyParts { get; init; }
}

/// <summary>
/// Плоскость для операций B3: существующая опора, точка + нормаль или базовая плоскость со смещением.
/// </summary>
/// <remarks>
/// Три способа взаимоисключающие, и это проверяется до вызова COM. Сторона плоскости НЕ задаётся
/// здесь: она задаётся знаком <c>s = n·(p − p₀)</c> в самой команде отсечения, потому что «левая
/// сторона» без системы координат — не адрес.
/// <para>
/// Нормаль обязана быть ненулевой и конечной. Точка и нормаль — в МОДЕЛЬНЫХ координатах, мм.
/// </para>
/// <para>
/// <b>Форма полей совпадает с ОПУБЛИКОВАННОЙ схемой <c>cut_plane</c> буквально.</b> 19.09.2026
/// измерено клиентской приёмкой B3 (три строки FAIL: <c>B3C.neg.plane_base_declared</c>,
/// <c>B3C.08.plane_base.cut_by_plane</c>, <c>B3C.08.plane_base.split</c>): схема публиковала
/// <c>base</c> строкой <c>xy|xz|yz</c> и соседний <c>offset_mm</c> числом, а DTO ждал здесь ОБЪЕКТ
/// <c>PlaneRefDto</c> — то есть форма расходилась на один уровень вложенности, и объявленный
/// <c>CAPABILITY_UNAVAILABLE</c> был недостижим: вызов падал на разборе payload
/// (<c>JsonException</c> по <c>$.plane.base</c>) с кодом <c>VERIFICATION_FAILED</c>. Поле приведено к
/// опубликованной форме; «объявленное и исполняемое» снова совпадают.
/// </para>
/// <para>
/// <b><c>offset_mm</c> без <c>base</c> — ошибка аргумента, а не молчание.</b> Смещение без базовой
/// плоскости не выражает плоскость, и принимать его «на всякий случай» значило бы объявить
/// параметр принятым и проигнорировать его.
/// </para>
/// </remarks>
public sealed record CutPlaneDto
{
    /// <summary>Ссылка на существующую плоскость документа.</summary>
    public string? PlaneRef { get; init; }

    /// <summary>Точка, через которую проходит плоскость, мм, модельные координаты.</summary>
    public IReadOnlyList<double>? PointMm { get; init; }

    /// <summary>Нормаль плоскости, безразмерная, модельные координаты. Ненулевая и конечная.</summary>
    public IReadOnlyList<double>? NormalMm { get; init; }

    /// <summary>
    /// Базовая плоскость (<c>xy</c> | <c>xz</c> | <c>yz</c>) — ОБЪЯВЛЕННЫЙ, но НЕ ПОДДЕРЖАННЫЙ способ:
    /// маршрут вспомогательной плоскости API7 со смещением не измерен, и вызов с этим полем отказывает
    /// <c>CAPABILITY_UNAVAILABLE</c> до всякой мутации.
    /// </summary>
    public PlaneBase? Base { get; init; }

    /// <summary>Смещение вдоль нормали базовой плоскости, мм. Имеет смысл только вместе с <see cref="Base"/>.</summary>
    public double? OffsetMm { get; init; }
}

/// <summary>
/// Форма постановки плоскости: к какому исходу она обязывает, ДО всякой работы с моделью.
/// </summary>
public enum CutPlaneFormVerdict
{
    /// <summary>Форма допустима: дальнейшие проверки — за маршрутом (ссылка, конечность, нормаль).</summary>
    Ok,

    /// <summary>Названо больше одного способа. Противоречивый запрос — <c>INVALID_ARGUMENT</c>.</summary>
    ModesConflict,

    /// <summary>Назван <c>base</c> — объявленный и НЕ поддержанный способ: <c>CAPABILITY_UNAVAILABLE</c>.</summary>
    BaseUnsupported,

    /// <summary><c>offset_mm</c> без <c>base</c> — смещение не выражает плоскость: <c>INVALID_ARGUMENT</c>.</summary>
    OffsetWithoutBase,
}

/// <summary>
/// Правило формы <see cref="CutPlaneDto"/> — в одном месте и без COM, чтобы его можно было
/// проверить тестом, а не только приёмкой на живой модели.
/// </summary>
/// <remarks>
/// <para>
/// Приоритет объявлен и не зависит от порядка полей в JSON:
/// <list type="number">
/// <item><see cref="CutPlaneFormVerdict.ModesConflict"/> — <c>base</c> вместе с <c>plane_ref</c> или
/// точкой с нормалью. Проверяется ПЕРВЫМ: запрос противоречив, и ответить на него объявленным
/// отказом возможности значило бы спрятать от клиента, что он назвал два способа сразу;</item>
/// <item><see cref="CutPlaneFormVerdict.BaseUnsupported"/> — <c>base</c> назван один;</item>
/// <item><see cref="CutPlaneFormVerdict.OffsetWithoutBase"/> — смещение без базовой плоскости;</item>
/// <item><see cref="CutPlaneFormVerdict.Ok"/> — либо <c>plane_ref</c>, либо точка с нормалью.</item>
/// </list>
/// </para>
/// <para>
/// Взаимоисключение «<c>plane_ref</c> против точки с нормалью» здесь НЕ проверяется: у правки
/// признака своя причина отвергать ссылку (маршрут правки — перенос точек СОБСТВЕННОЙ опоры), и
/// маршрут обязан назвать её сам.
/// </para>
/// </remarks>
public static class CutPlaneForm
{
    public static CutPlaneFormVerdict Validate(CutPlaneDto plane)
    {
        var namesPointOrNormal = plane.PointMm is not null || plane.NormalMm is not null;
        var namesReference = plane.PlaneRef is { Length: > 0 };

        if (plane.Base is not null)
        {
            return namesReference || namesPointOrNormal
                ? CutPlaneFormVerdict.ModesConflict
                : CutPlaneFormVerdict.BaseUnsupported;
        }

        if (plane.OffsetMm is not null)
        {
            return CutPlaneFormVerdict.OffsetWithoutBase;
        }

        return CutPlaneFormVerdict.Ok;
    }
}

/// <summary>Вид булевой операции. Числа соответствуют <c>Kompas6Constants.ksBooleanType</c>.</summary>
public enum BooleanOperation
{
    Union = 3,
    Difference = 2,
    Intersect = 1,
}

/// <summary>
/// Булева операция над телами: явная цель, явный список инструментов, вид операции и политика
/// сохранения инструментов (docs/05 SM-15).
/// </summary>
/// <remarks>
/// Адресация — только по ссылкам. Ни «текущее окно», ни «индекс 0», ни порядок коллекции целью не
/// являются: заявленное тело ищется среди элементов BodyCollection по IUnknown, и при отсутствии
/// совпадения вызов отвергается, а не подставляется позиция.
/// <para>
/// Проверки, которые ядро НЕ делает и потому делает контракт: цель не входит в набор инструментов;
/// в наборе нет повторов; список непуст. Повтор ссылки ядро принимает молча (измерено 18.09.2026,
/// шаг BO.9), поэтому полагаться на него нельзя.
/// </para>
/// </remarks>
public sealed record BooleanCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Тело-цель. Разность считается как «цель минус инструменты».</summary>
    public required string TargetBodyRef { get; init; }

    /// <summary>Тела-инструменты. Непустой список без повторов и без цели.</summary>
    public required IReadOnlyList<string> ToolBodyRefs { get; init; }

    public required BooleanOperation Operation { get; init; }

    /// <summary>
    /// Сохранить инструменты отдельными телами на прежнем месте
    /// (<c>IBoolean.SaveCopyModifyObjects</c>). Копия цели не поддерживается: это отдельный режим
    /// <c>SM-15.union.mode_save_base_copy</c> с приоритетом <c>next</c>, вне обязательного объёма.
    /// </summary>
    public bool KeepTools { get; init; }

    /// <summary>Аналитическое ожидание объёма результата, если оно выводимо у вызывающего.</summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Разделение тела плоскостью на части. Все полученные части сохраняются — это измеренное поведение
/// <c>ISplitSolid</c>, а не выбранная политика.
/// </summary>
public sealed record SplitCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required CutPlaneDto Plane { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Отсечение тела по одну сторону плоскости (docs/05 SM-16).
/// </summary>
/// <remarks>
/// Оставляемая сторона задаётся знаком <c>s = n·(p − p₀)</c>. Измеренное соответствие: сторона
/// «в направлении нормали» (<c>s &gt; 0</c>) — это <c>ICut.Direction = true</c>.
/// </remarks>
public sealed record CutByPlaneCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required CutPlaneDto Plane { get; init; }

    /// <summary>Оставляемая сторона: <c>positive</c> — <c>s &gt; 0</c>, <c>negative</c> — <c>s &lt; 0</c>.</summary>
    public required string KeepSide { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Вид преобразования положения тела.</summary>
public enum RepositionKind
{
    Translate,
    Rotate,
}

/// <summary>
/// Перенос и поворот тела (docs/05 SM-17). Оба вида — одно признак <c>IBodyReposition</c>, и оба
/// пишутся однородной матрицей 4×4: положение пишет только она (OQ-A19).
/// </summary>
/// <remarks>
/// Ось поворота задаётся точкой и направлением либо двумя различными точками — в модельных
/// координатах, мм. Вырожденная ось (совпадающие точки, нулевое направление) отвергается до COM.
/// Угол — в градусах, знак по правому правилу вокруг направления оси.
/// </remarks>
public sealed record RepositionCommand
{
    public required string DocumentId { get; init; }

    public required string TargetBodyRef { get; init; }

    public required RepositionKind Kind { get; init; }

    /// <summary>Вектор переноса, мм, модельные координаты. Обязателен для <c>translate</c>.</summary>
    public IReadOnlyList<double>? VectorMm { get; init; }

    /// <summary>Точка на оси поворота, мм. Обязательна для <c>rotate</c>.</summary>
    public IReadOnlyList<double>? AxisPointMm { get; init; }

    /// <summary>Направление оси поворота. Взаимоисключающе с <c>AxisPoint2Mm</c>.</summary>
    public IReadOnlyList<double>? AxisDirectionMm { get; init; }

    /// <summary>Вторая точка оси поворота. Взаимоисключающе с <c>AxisDirectionMm</c>.</summary>
    public IReadOnlyList<double>? AxisPoint2Mm { get; init; }

    /// <summary>Угол поворота в градусах. Обязателен для <c>rotate</c>.</summary>
    public double? AngleDeg { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Фактическая структура результата булевой операции. Обещаний о числе тел нет: ядро представляет
/// результат из нескольких кусков ОДНИМ телом с несколькими кусками (измерено 18.09.2026, шаг BO.8:
/// V = 18 000, <c>MultiBodyParts = true</c>, 12 граней при двух кусках по 6).
/// </summary>
public sealed record BooleanResultDto
{
    public required string FeatureRef { get; init; }

    public required string Operation { get; init; }

    public required bool KeepTools { get; init; }

    /// <summary>Тела после операции — фактический состав, а не ожидаемый.</summary>
    public required IReadOnlyList<SolidBodyDto> ResultBodies { get; init; }

    /// <summary>Инструменты, сохранённые отдельными телами (пустой список, если политика их поглотила).</summary>
    public required IReadOnlyList<SolidBodyDto> SavedTools { get; init; }

    /// <summary>Тела, которые операция потребла.</summary>
    public required IReadOnlyList<string> ConsumedInputs { get; init; }

    /// <summary>Сумма объёмов всех тел документа после операции.</summary>
    public required double TotalVolumeMm3 { get; init; }

    /// <summary>
    /// Сумма индивидуальных объёмов и объём пространственного объединения — разные величины, и
    /// смешивать их нельзя. Здесь — именно сумма по телам.
    /// </summary>
    public string? VolumeNote { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

/// <summary>Результат разделения: полный список частей, каждая со своей ссылкой.</summary>
public sealed record SplitResultDto
{
    public required string FeatureRef { get; init; }

    /// <summary>Все части, полученные разделением, со ссылками и объёмами.</summary>
    public required IReadOnlyList<SolidBodyDto> Parts { get; init; }

    /// <summary>Тела документа, не участвовавшие в операции, — доказательство их непричастности.</summary>
    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required double PartsVolumeSumMm3 { get; init; }

    /// <summary>Нормаль плоскости в модельных координатах — та, что реально применилась.</summary>
    public IReadOnlyList<double>? PlaneNormalMm { get; init; }

    public IReadOnlyList<double>? PlanePointMm { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

/// <summary>Результат отсечения: что осталось и что удалено, названное явно.</summary>
public sealed record CutByPlaneResultDto
{
    public required string FeatureRef { get; init; }

    /// <summary>Оставленное тело.</summary>
    public required SolidBodyDto Remaining { get; init; }

    /// <summary>Сторона, названная знаком: <c>positive</c> — <c>s &gt; 0</c>.</summary>
    public required string KeptSide { get; init; }

    public required IReadOnlyList<double> PlaneNormalMm { get; init; }

    public required IReadOnlyList<double> PlanePointMm { get; init; }

    /// <summary>Тела документа, не участвовавшие в операции.</summary>
    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }

    /// <summary>
    /// Проверки, на которых держится вердикт: адресность (материал снят у НАЗВАННОГО тела) и
    /// сохранность посторонних тел. Публикуются отдельно от
    /// <see cref="UnverifiedAspects"/>, потому что пустой список ограничений при исчезнувшем
    /// постороннем теле читался бы как «проверено полностью» — именно так клиентская приёмка
    /// 19.09.2026 получила ложный <c>geometry_checked</c> (дефект
    /// <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>).
    /// </summary>
    public IReadOnlyList<NamedCheck>? Checks { get; init; }
}

/// <summary>Результат переноса или поворота: положение до и после, объём и число тел.</summary>
public sealed record RepositionResultDto
{
    public required string FeatureRef { get; init; }

    public required string Kind { get; init; }

    public required BoundingBoxDto BboxBefore { get; init; }

    public required BoundingBoxDto BboxAfter { get; init; }

    public required double VolumeMm3 { get; init; }

    /// <summary>Число тел документа до и после — преобразование положения не создаёт и не потребляет тела.</summary>
    public required int BodiesBefore { get; init; }

    public required int BodiesAfter { get; init; }

    public required IReadOnlyList<SolidBodyDto> UntouchedBodies { get; init; }

    public required long Revision { get; init; }

    public IReadOnlyList<string>? UnverifiedAspects { get; init; }
}

public sealed record ExportStepCommand
{
    public required string DocumentId { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>Revision the caller read; a mismatch aborts before the converter runs.</summary>
    public required long ExpectedRevision { get; init; }
}

public sealed record ImportStepCommand
{
    public required string ApplicationId { get; init; }

    public required string InputPath { get; init; }

    public string DesiredKind { get; init; } = "auto";

    public string? TargetPath { get; init; }

    /// <summary>
    /// P0 diagnostic mode: log every return value, null and interface type observed around
    /// the import instead of throwing at the first null. Used only by the probe.
    /// </summary>
    public bool TraceLifecycle { get; init; }
}

/// <summary>
/// Растровый снимок модели текущего сеанса документированным маршрутом API5:
/// <c>ksDocument3D.RasterFormatParam()</c> → <c>ksRasterFormatParam</c> →
/// <c>ksDocument3D.SaveAsToRasterFormat(fileName, rasterPar)</c>.
/// </summary>
/// <remarks>
/// <b>Два режима маршрута ВЗАИМОИСКЛЮЧАЮЩИЕ, и это измерено (проба P2b наряда, поставка
/// <c>publish-deproutes-r2-20260921</c>):</b> с НЕПУСТЫМ именем файла ядро пишет файл, а
/// <c>resultArrayBytes</c> остаётся null (шесть форм вызова, отличающихся порядком записи членов,
/// предварительно подставленным массивом, вторым методом записи и повторным чтением свойства, —
/// все дали null); с ПУСТЫМ именем файла ядро отдаёт <c>System.Byte[]</c> (8639 байт, магия PNG) и
/// файла НЕ создаёт вовсе. Поэтому «вернуть картинку» и «записать файл» — два разных вызова
/// маршрута, а не один с двумя последствиями.
/// <para>
/// Поля, не подтверждённые пробой, в контракт не входят: <c>IViewProjection7</c> (управление
/// проекцией) документирован, но этим нарядом не реализуется и назван остатком.
/// </para>
/// </remarks>
public sealed record ExportImageCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Ревизия, прочитанная вызывающим; расхождение даёт REVISION_CONFLICT до COM.</summary>
    public required long ExpectedRevision { get; init; }

    /// <summary>Имя формата на проводе: png, jpg, bmp или tif.</summary>
    public required string Format { get; init; }

    /// <summary>
    /// Значение <c>extResolution</c>. Не задано — член НЕ записывается, действует умолчание ядра
    /// (измерено пробой P6 отдельной строкой). Ограничения ответа применяются к результату в любом
    /// случае: превышение отвергается, а не ужимается.
    /// </summary>
    public int? Resolution { get; init; }

    /// <summary>Значение <c>extScale</c>. Не задано — член не записывается.</summary>
    public double? Scale { get; init; }

    /// <summary>Куда положить файл. Не задано — файл не создаётся (байтовый режим).</summary>
    public string? SavePath { get; init; }

    /// <summary>Вернуть ли картинку в ответе. Умолчание — да.</summary>
    public bool ReturnImageContent { get; init; } = true;
}

/// <summary>
/// Результат растрового снимка. Габариты — ПРОЧИТАННЫЕ ИЗ ЗАГОЛОВКА, а не взятые из запроса:
/// параметр говорит, что просили, заголовок — что получилось.
/// </summary>
public sealed record ExportImageResultDto
{
    public required string Format { get; init; }

    public required string MimeType { get; init; }

    /// <summary>Габарит из заголовка; null — не прочитан (у JPG и TIF габарит не разбирается).</summary>
    public int? PixelWidth { get; init; }

    public int? PixelHeight { get; init; }

    /// <summary>Размер артефакта в байтах — измеренный, а не выведенный из запроса.</summary>
    public required long BytesCount { get; init; }

    /// <summary>Путь записанного файла; null — файл не запрашивался.</summary>
    public string? SavePath { get; init; }

    /// <summary>
    /// Каким режимом маршрута получен артефакт: <c>memory</c> (пустое имя файла, байты в ответе)
    /// или <c>file</c> (непустое имя, файл на диске). Назван потому, что это разные вызовы ядра.
    /// </summary>
    public required string RasterRoute { get; init; }

    /// <summary>Файл записан ИЗ ТЕХ ЖЕ байтов, что вернулись в ответе (один рендер, не два).</summary>
    public bool? SavePathFromMemory { get; init; }

    /// <summary>Состояние вида: снимок снят с текущего вида окна сервера.</summary>
    public required string ViewNote { get; init; }

    /// <summary>base64 картинки. Хост переносит его в image-блок и ИЗ СТРУКТУРЫ УБИРАЕТ.</summary>
    public string? ImageBase64 { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }
}


/// <summary>Deliberate unit experiment (spec 1.10 / G08): draw known geometry, read it back every way.</summary>
public sealed record UnitProbeCommand
{
    public required string ApplicationId { get; init; }

    public required double KnownLengthMm { get; init; }

    public required double KnownRadiusMm { get; init; }
}

public sealed record UnitProbeResult
{
    public required double KnownLengthMm { get; init; }

    public required double KnownRadiusMm { get; init; }

    /// <summary>Raw, unconverted readings keyed by API call. Conversions live in the adapter.</summary>
    public required IReadOnlyDictionary<string, double?> RawReadings { get; init; }

    public required IReadOnlyDictionary<string, string?> Observations { get; init; }

    public required IReadOnlyList<string> ConfirmedScales { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }
}

public sealed record ListComponentsCommand
{
    public required string DocumentId { get; init; }

    public bool Recursive { get; init; }

    public bool IncludeSuppressed { get; init; }

    public bool IncludeHidden { get; init; }
}

public sealed record InsertComponentCommand
{
    public required string DocumentId { get; init; }

    public required string FilePath { get; init; }

    public required TransformDto Transform { get; init; }

    public bool Fixed { get; init; } = true;
}

public sealed record SetComponentTransformCommand
{
    public required string InstanceRef { get; init; }

    public required TransformDto Transform { get; init; }

    public required CoordinateSpace CoordinateSpace { get; init; }
}

public sealed record CheckIntersectionsCommand
{
    public required string DocumentId { get; init; }

    public required IReadOnlyList<string> InstanceRefs { get; init; }

    /// <summary>none | include_contact</summary>
    public required string ContactPolicy { get; init; }

    public required double ToleranceMm { get; init; }

    public int Limit { get; init; } = 200;
}

/// <summary>Component instance row returned to the Host.</summary>
public sealed record ComponentInstanceDto
{
    public required string InstanceRef { get; init; }

    public string? ParentRef { get; init; }

    public required string Name { get; init; }

    public string? Marking { get; init; }

    public string? SourcePath { get; init; }

    public required TransformDto Transform { get; init; }

    public required bool Fixed { get; init; }

    public required BoundingBoxDto Bbox { get; init; }

    public required int BodyCount { get; init; }
}

/// <summary>Feature row returned to the Host (spec 2.5).</summary>
public sealed record FeatureRowDto
{
    public required string FeatureRef { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public required bool State { get; init; }

    public string? PersistentId { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Reference to the sketch this feature is built on, when it is an extrusion and the sketch can
    /// be read back through <c>GetSketch()</c>. Null for feature families without a sketch and when
    /// the read-back fails.
    /// </summary>
    /// <remarks>
    /// This closes the long-standing <c>sketch_reference_not_resolved</c> gap: before it, a sketch
    /// that arrived with a reopened document could be neither named nor edited, because nothing
    /// handed the caller a reference to it. It is the missing half of the model-derived sketch
    /// route — the coordinate can now be derived, and this is how the sketch to edit is found in the
    /// first place. It is a plain reference minted against the current revision, so the usual
    /// staleness rules apply to it unchanged (it dies on the next rebuild like any other handle).
    /// </remarks>
    public string? SketchRef { get; init; }
}

/// <summary>Body row returned to the Host.</summary>
public sealed record BodyRowDto
{
    public required string BodyRef { get; init; }

    public required string Kind { get; init; }

    public required BoundingBoxDto Bbox { get; init; }

    public required int FaceCount { get; init; }

    public required int EdgeCount { get; init; }
}

public sealed record FaceRowDto
{
    public required string FaceRef { get; init; }

    public required string SurfaceType { get; init; }

    public required double AreaMm2 { get; init; }

    public IReadOnlyList<double>? CenterMm { get; init; }

    public IReadOnlyList<double>? NormalAtCenter { get; init; }

    /// <summary>Радиус цилиндрической грани, мм; null для нецилиндрических.</summary>
    public double? RadiusMm { get; init; }

    /// <summary>Протяжённость цилиндрической грани вдоль оси, мм; не глубина операции.</summary>
    public double? HeightMm { get; init; }

    /// <summary>Направление оси цилиндрической грани (из placement, не из GetAxis).</summary>
    public IReadOnlyList<double>? AxisMm { get; init; }

    public required bool NormalAmbiguous { get; init; }
}

public sealed record EdgeRowDto
{
    public required string EdgeRef { get; init; }

    public required string CurveType { get; init; }

    public required double LengthMm { get; init; }

    public IReadOnlyList<IReadOnlyList<double>>? VerticesMm { get; init; }

    /// <summary>Circle/arc parameters when analytically recovered, mm.</summary>
    public double? RadiusMm { get; init; }

    public IReadOnlyList<double>? CenterMm { get; init; }
}

/// <summary>Where the Worker put a file and what it verified about it.</summary>
public sealed record ExportResultDto
{
    public required string OutputPath { get; init; }

    public required long ByteLength { get; init; }

    public required string Sha256 { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }

    public required long SourceRevision { get; init; }
}

/// <summary>What an import actually created — the question the historical attempt could not answer.</summary>
public sealed record ImportResultDto
{
    public required IReadOnlyList<string> CreatedDocumentIds { get; init; }

    public required IReadOnlyList<DocumentKind> CreatedDocumentKinds { get; init; }

    public required int BodyCount { get; init; }

    public required int ComponentCount { get; init; }

    public required VerificationLevel ReachedLevel { get; init; }

    public required IReadOnlyList<string> UnverifiedAspects { get; init; }

    /// <summary>Step-by-step record of returns/nulls when <see cref="ImportStepCommand.TraceLifecycle"/> is set.</summary>
    public IReadOnlyList<string> Trace { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Кинематическая операция: плоский замкнутый профиль переносится по непрерывной траектории
/// (docs/05 SM-04, профиль <c>mechanical-core-v1</c>, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Документ берётся из ПРОФИЛЯ, а не из отдельного поля.</b> Тот же приём, что у вращения:
/// ссылка из чужой детали не может протащиться в мутацию, потому что второго источника документа
/// здесь нет.
/// </para>
/// <para>
/// <b>Маршрут документирован страницей API5, а не выведен из аналогии с вращением.</b>
/// <c>ksbaseevolutiondefinition.html</c> («Основание — кинематический элемент (Интерфейсы
/// ksBaseEvolutionDefinition, IBaseEvolutionDefinition)») описывает интерфейс, который «можно
/// получить, используя метод интерфейса элемента модели <c>ksEntity::GetDefinition</c>»; состав —
/// <c>sketchShiftType</c>, <c>SetSketch</c>/<c>GetSketch</c>, <c>PathPartArray</c>,
/// <c>GetPathLength(bitVector)</c>, <c>Get/SetThinParam</c>. Страница помечает интерфейс
/// <b>устаревшим</b> и рекомендует приклеенный <c>ksBossLoftDefinition</c> (в тексте именно так —
/// для кинематической операции ожидался <c>ksBossEvolutionDefinition</c>; расхождение внутри
/// справки записано как есть). Приклеенный маршрут <c>NewEntity(46)</c> измерен отдельно и даёт
/// ТО ЖЕ тело (шаг B5.8), но обязательные строки этапа описаны как <b>базовые</b>, поэтому
/// используется базовый тип 45.
/// </para>
/// <para>
/// <b>Траектория — тоже эскиз, и это измерено, а не предположено.</b> Шаг B5.1/B5.2: держатель
/// траектории, который возвращает <c>ksBaseEvolutionDefinition.PathPartArray()</c>, приводится к
/// <c>ksEntityCollection</c>, и <c>Add(эскиз)</c> возвращает <c>True</c>. Разрыв траектории —
/// <b>отказ</b>, а не частичный результат.
/// </para>
/// <para>
/// <b>Режим ортогональности обязан проверяться НА ДУГЕ.</b> На прямой траектории
/// <see cref="SweepShiftMode.Parallel"/> и <see cref="SweepShiftMode.Orthogonal"/> дают одно тело —
/// измерено, что на дуге R50/90° они различаются на <c>8966.047734774369</c> мм³.
/// </para>
/// </remarks>
public sealed record SweepCommand
{
    /// <summary>Эскиз-профиль: явная <c>sketch:</c>-ссылка. Задаёт и документ.</summary>
    public required string SketchRef { get; init; }

    /// <summary>Эскиз-траектория: явная <c>sketch:</c>-ссылка. Обязана лежать в ТОЙ ЖЕ детали.</summary>
    public required string PathRef { get; init; }

    /// <summary>
    /// Тип движения сечения по траектории. По умолчанию <see cref="SweepShiftMode.Orthogonal"/>:
    /// это документированное поведение «плоскость образующей ортогональна направляющей», и именно
    /// оно даёт <c>S × L</c>.
    /// </summary>
    public SweepShiftMode ShiftMode { get; init; } = SweepShiftMode.Orthogonal;

    /// <summary>
    /// Аналитическое ожидание объёма ПОСЛЕ операции. Без него подтверждается только чтение
    /// параметров обратно, и результат честно помечается недоказанной геометрией.
    /// </summary>
    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Элемент по сечениям: тело строится по упорядоченному набору сечений
/// (docs/05 SM-05, профиль <c>mechanical-core-v1</c>, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — API7, и это следует из состава обязательных строк, а не из удобства.</b>
/// Обязательная строка <c>SM-05.base.mode_couplings</c> требует <b>цепочек соответствия сечений</b>,
/// а в API5 их нет вовсе: ни <c>ksBaseLoftDefinition</c>, ни <c>ksBossLoftDefinition</c> не
/// объявляют ни <c>AddCoupling</c>, ни <c>Coupling</c>. В API7 они документированы:
/// <c>iloft_propers.html</c> перечисляет <c>Coupling</c> и <c>CouplingsCount</c>,
/// <c>iloft_addcoupling.html</c> — <c>AddCoupling()</c> → <c>ICoupling</c>. Измерено (шаг B5.9):
/// <c>AddCoupling()</c> вернул <c>KompasAPI7.CouplingClass</c>, <c>CouplingsCount = 1</c>.
/// </para>
/// <para>
/// <b>Фабрика API7 документирована для приклеенного типа.</b> <c>ilofts_add.html</c>: «Допустимыми
/// значениями <c>LoftType</c> являются <c>o3d_bossLoft</c>, <c>o3d_cutLoft</c> для коллекции
/// операций <c>IModelContainer::Lofts</c>»; «после получения нового интерфейса нужно задать
/// параметры операции и вызвать метод <c>IModelObject::Update</c>». Сечения задаются свойством
/// <c>ILoft.Sketchs</c> типа <c>VARIANT</c> — «массив <c>SAFEARRAY</c> объектов <c>LPDISPATCH</c>»
/// (<c>iloft_sketchs.html</c>), и это измерено: присваивание массива дало чтение
/// <c>System.Object[]</c> из 2 элементов, <c>Update() = True</c>, объём <c>28000</c> — тот же
/// эталон, что у API5-маршрута (шаг B5.4).
/// </para>
/// <para>
/// <b>Порядок сечений задаёт вызывающий, и он же является частью требования.</b> При
/// концентрических параллельных сечениях объём ПОРЯДОК НЕ РАЗЛИЧАЕТ — измерено, что усечённая
/// пирамида 40×40 → 20×20 при h = 30 даёт <c>28000</c> в обе стороны. Поэтому «порядок соблюдён»
/// доказывается различающей постановкой (разная форма или поворот сечений, габарит, число граней),
/// а не объёмом, и строка приёмки обязана это учитывать.
/// </para>
/// <para>
/// <b>Объём считается по формуле усечённой пирамиды</b>
/// <c>V = h/3 · (A₁ + A₂ + √(A₁A₂))</c>, а не «средней площадью × высота»: для 40/20 при h = 30 это
/// <c>28000</c> против <c>30000</c>, и расхождение <c>2000</c> мм³ достаточно, чтобы отличить
/// правильную формулу от ошибочной.
/// </para>
/// </remarks>
public sealed record LoftCommand
{
    /// <summary>Документ детали: у набора сечений нет одного «опорного» объекта, как у профиля.</summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// Сечения В ПОРЯДКЕ соединения. Не менее двух: по одному сечению тело не строится. Эскизы,
    /// контуры, пространственные кривые и грани — состав объявлен справкой SDK
    /// (<c>iloft_propers.html</c>).
    /// </summary>
    public required IReadOnlyList<string> SectionRefs { get; init; }

    /// <summary>
    /// Способ построения у крайних сечений — <c>ILoft.BuildingType(BeginSection)</c>. Значения
    /// документированы страницей <c>ksloftbuildingtype.html</c>; <c>Auto</c> подтверждён измерением
    /// (шаг B5.9: <c>BuildingType(true) = 0</c> и <c>BuildingType(false) = 0</c> для только что
    /// созданного признака, то есть <c>ksLoftAuto = 0</c>).
    /// </summary>
    public LoftBuilding Building { get; init; } = LoftBuilding.Auto;

    /// <summary>
    /// Замкнуть траекторию — <c>ILoft.Closed</c>. Запись и обратное чтение измерены (шаг B5.9:
    /// записано <c>false</c>, прочитано <c>False</c>). Отличие от «замкнутой оболочки»: здесь
    /// замыкается <b>траектория соединения сечений</b>, а не тело.
    /// </summary>
    public bool Closed { get; init; }

    /// <summary>
    /// Цепочки соответствия сечений — то, чем обязательная строка <c>SM-05.base.mode_couplings</c>
    /// отличается от «просто тела по сечениям».
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен, а не выбран.</b> Документировано: <c>iloft_addcoupling.html</c> —
    /// <c>AddCoupling()</c> возвращает указатель на <c>ICoupling</c>; <c>icoupling_count.html</c> —
    /// <c>Count</c> «Количество сечений в цепочке»; <c>icoupling_positionoffset.html</c> —
    /// <c>PositionOffset(Index)</c> «Величина смещения точки вдоль контура сечения в мм», где
    /// <c>Index</c> — индекс сечения в цепочке. Измерено (шаг B5.17, 20.09.2026): у пирамиды
    /// 40×40 → 20×20 при h = 30 цепочка из двух точек со смещениями <c>0 / 0</c> даёт <c>28000</c> —
    /// ровно как без цепочки, то есть явное соответствие <b>замещает</b> автоматическое; сдвиг точки
    /// второго сечения на <c>25 %</c> контура (20 мм из 80) даёт <c>20000</c>, то есть
    /// <b>содержимое цепочки применяется</b> (разность 8000 мм³ против допуска 0,01), а возврат
    /// точки возвращает <c>28000</c>.
    /// </para>
    /// <para>
    /// <b>Единица подтверждена числом.</b> Периметр сечения 20×20 равен 80 мм; измерено, что
    /// <c>PositionOffset = 5</c> читается как <c>Position = 6.25 %</c> — это в точности
    /// <c>5/80</c>. Поэтому наружу выходит смещение в мм, а не доля: доля зависит от длины контура,
    /// которую вызывающая сторона может не знать.
    /// </para>
    /// <para>
    /// <b>Координаты точки наружу не выходят, и это измеренное решение.</b> <c>ICoupling.SetPoint</c>
    /// документирован, но измерено, что поданная точка <b>проецируется на контур</b> и читается
    /// обратно <b>в локальных координатах эскиза сечения</b>: подано <c>(10; 10; 30)</c> (центр
    /// квадрата 20×20), прочитано <c>(20; 10; 30)</c> — середина правой стороны, 10 мм контура.
    /// Публиковать параметр, у которого прямая и обратная половины не совпадают, значило бы обещать
    /// round-trip, которого нет.
    /// </para>
    /// <para>
    /// <b>Порядок построения.</b> Цепочка задаётся ДО первого <c>Update()</c> — так документирована
    /// фабрика («задать параметры операции и вызвать <c>IModelObject::Update</c>»). Измерено
    /// (шаг B5.18): цепочка, заданная до первого <c>Update()</c>, даёт <c>CouplingsCount = 1</c> и
    /// объём <c>20000</c> — то же значение, что и цепочка, добавленная после построения, то есть
    /// одно построение достаточно.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LoftCoupling> Couplings { get; init; } = Array.Empty<LoftCoupling>();

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>
/// Цепочка соответствия сечений: по одному смещению на КАЖДОЕ сечение, в порядке
/// <see cref="LoftCommand.SectionRefs"/>. Число смещений обязано совпасть с числом сечений — это
/// проверяет <c>ICoupling.Count</c> («количество сечений в цепочке», измерено: 2 у двух сечений).
/// </summary>
public sealed record LoftCoupling
{
    /// <summary>
    /// Смещения вдоль контуров сечений, мм — <c>ICoupling.PositionOffset(Index)</c>, где
    /// <c>Index</c> — индекс сечения в цепочке. Ноль означает начало контура; это же значение
    /// воспроизводит автоматическое соответствие (измерено: <c>0 / 0</c> даёт <c>28000</c>, как без
    /// цепочки).
    /// </summary>
    public required IReadOnlyList<double> OffsetsMm { get; init; }
}

/// <summary>
/// Оболочка: из тела вычитается полость заданной толщины, при необходимости со снятием граней
/// (docs/05 SM-13, профиль <c>mechanical-core-v1</c>, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Пустой список граней НЕ даёт замкнутой оболочки, и это измерено на ОБОИХ API.</b>
/// Ожидание наряда: без удалённых граней <c>t = 2</c> внутрь даёт <c>36224</c>
/// (<c>80000 − 96·76·6</c>). Измерено на API5 (шаг B5.6): <c>80000</c>. Измерено на API7
/// (шаг B5.10, четыре постановки на том же коробе 100×80×10): <c>79999.99999999999</c> при
/// <b>6 гранях</b> — ровно как у исходного короба, тогда как открытая оболочка даёт
/// <c>21632</c> при <b>11</b> гранях. То есть <c>Update() = True</c> означает «принято», а
/// неизменное число граней — «не применено»; это второй независимый признак рядом с объёмом.
/// Поэтому пустой список граней <b>отвергается до COM</b> именованным отказом, а не выдаётся за
/// замкнутую оболочку.
/// </para>
/// <para>
/// <b>Направление стенки задаётся ЗНАЧЕНИЕМ, и соответствие подтверждено дважды.</b>
/// Документация: <c>ksshelldefinition_thintype.html</c> — «<c>TRUE</c> — внутрь, <c>FALSE</c> —
/// наружу» для API5. Измерение API5 (шаг B5.5): <c>true</c> → <c>21631.999999999996</c>,
/// <c>false</c> → <c>24832.000000000022</c>. Измерение API7 (шаг B5.10):
/// <c>ThinType = 1</c> → <c>21631.999999999996</c>, <c>ThinType = 0</c> →
/// <c>24832.000000000022</c>. Типы половин разные — API7 <c>ThinType</c> объявлен <c>long</c>
/// (в interop — <c>ksDirectionTypeEnum</c>), API5 <c>thinType</c> — <c>bool</c>.
/// </para>
/// </remarks>
public sealed record ShellCommand
{
    public required string DocumentId { get; init; }

    /// <summary>
    /// Грани, которые снимаются перед образованием стенки — ссылки <c>face:</c>. Список обязан быть
    /// <b>непустым</b>: пустой не даёт замкнутой оболочки (измерено на обоих API — объём остаётся
    /// <c>80000</c>, число граней <c>6</c>, как у исходного тела), и такой вызов отвергается
    /// именованным отказом до обращения к COM.
    /// </summary>
    public IReadOnlyList<string> FaceRefs { get; init; } = Array.Empty<string>();

    /// <summary>Толщина стенки, мм. Строго больше 0.</summary>
    public required double ThicknessMm { get; init; }

    /// <summary>Направление формирования стенки. Соответствие измерено, см. описание типа.</summary>
    public ShellThinDirection ThinDirection { get; init; } = ShellThinDirection.Inward;

    /// <summary>
    /// Касательные грани — <c>IShell.SetFaces(Faces, TangentFaces)</c> в API7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Параметр <b>объявлен</b> в контракте, потому что он существует в маршруте API7 и потому что
    /// необъявленный параметр для продукта невидим: <c>additionalProperties: false</c> отверг бы
    /// вызов до COM, и это читалось бы как «не поддерживается».
    /// </para>
    /// <para>
    /// Но <c>true</c> <b>отвергается именованным отказом</b>, а не игнорируется молча: у API5
    /// <c>ksShellDefinition</c> члена «касательные грани» нет вовсе, а маршрут этого инструмента —
    /// API5. Молчаливое игнорирование дало бы успех за работу, которой не было, — это ровно тот
    /// дефект, который контракт запрещает. Режим <c>SM-13.shell.mode_tangent_faces</c> в обязательный
    /// объём этого этапа не входит, поэтому граница названа, а не спрятана.
    /// </para>
    /// </remarks>
    public bool TangentFaces { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }

    public required long ExpectedRevision { get; init; }
}

/// <summary>Результат кинематической операции.</summary>
public sealed record SweepResult(
    ReferenceDto? FeatureRef,
    string ShiftMode,
    int SectionCount,
    int PathPartCount,
    double? PathLengthMm,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Результат элемента по сечениям.</summary>
public sealed record LoftResult(
    ReferenceDto? FeatureRef,
    string Building,
    bool Closed,
    int SectionCount,
    int? CouplingCount,
    IReadOnlyList<LoftCouplingDto>? Couplings,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Результат оболочки.</summary>
public sealed record ShellResult(
    ReferenceDto? FeatureRef,
    double ThicknessMm,
    string ThinDirection,
    int RemovedFaceCount,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>
/// Параметры кинематической операции, прочитанные из модели. Все поля nullable: пустое поле
/// означает «НЕ ПРОЧИТАНО», а не ноль.
/// </summary>
public sealed record SweepDto(
    string? ShiftMode,
    int? SectionCount,
    int? PathPartCount,
    double? PathLengthMm);

/// <summary>
/// Цепочка соответствия, прочитанная <b>ИЗ МОДЕЛИ</b> — не пересказ запроса. Элемент
/// <see cref="OffsetsMm"/> — <c>null</c>, если смещение на этом сечении прочитать не удалось:
/// «не прочитано» отличается от нуля. <see cref="SectionCount"/> — <c>ICoupling.Count</c>,
/// «количество сечений в цепочке»; <c>null</c> означает, что и размер цепочки не прочитан.
/// </summary>
public sealed record LoftCouplingDto(int? SectionCount, IReadOnlyList<double?> OffsetsMm);

/// <summary>Параметры элемента по сечениям, прочитанные из модели.</summary>
/// <remarks>
/// <see cref="SectionRefs"/> — СЕЧЕНИЯ КАК ССЫЛКИ, выведенные из определения признака
/// (<c>ksBaseLoftDefinition.Sketches()</c> / <c>ksBossLoftDefinition.Sketches()</c>,
/// справка <c>ksbaseloftdefinition_sketches.html</c> и <c>ksbossloftdefinition_sketches.html</c>,
/// возвращают <c>ksEntityCollection</c>), а не сохранённые с момента создания.
/// <para>
/// Зачем это поле вообще существует. Правка элемента по сечениям меняет ТОЛЬКО набор сечений
/// (<c>kompas_update_feature</c>, поле <c>section_refs</c>), и другой валюты у неё нет. Ссылка же,
/// выданная при создании, живёт до первой мутации документа, а <c>kompas_rebuild</c> отзывает все
/// ссылки документа целиком; перечислять эскизы отдельным инструментом продукт не умеет —
/// <c>kompas_list_features</c> отдаёт только формообразующие элементы
/// (<c>EntityCollection(o3d_operationElement)</c>), и измерено, что на документе с двумя эскизами и
/// одним элементом по сечениям в дереве видна ОДНА строка. Без этого поля правка существующего
/// элемента по сечениям невыразима ни в новой сессии, ни после <c>save → close → reopen</c>, то есть
/// вход перестаёт быть ссылкой ровно там, где наряд §11 требует обратного.
/// </para>
/// <para>
/// <c>null</c> означает «не прочитано», а пустой список — «в определении сечений нет»; это разные
/// состояния и они не сливаются.
/// </para>
/// </remarks>
public sealed record LoftDto(
    string? Building,
    bool? Closed,
    int? SectionCount,
    int? CouplingsCount,
    IReadOnlyList<LoftCouplingDto>? Couplings = null,
    IReadOnlyList<string>? SectionRefs = null);

/// <summary>Параметры оболочки, прочитанные из модели.</summary>
/// <remarks>
/// <see cref="RemovedFaceRefs"/> — СНЯТЫЕ ГРАНИ КАК ССЫЛКИ, выведенные из определения признака
/// (<c>ksShellDefinition.FaceArray()</c> → <c>ksEntityCollection</c>), по той же причине, что и
/// <see cref="LoftDto.SectionRefs"/>: набор удаляемых граней — вход правки, и без ссылки на грани,
/// снятые САМИМ признаком, повторная правка набора после мутации невыразима. Грани, снятые
/// признаком, в топологии тела ОТСУТСТВУЮТ, поэтому из <c>kompas_read_topology</c> их взять нечем.
/// </remarks>
public sealed record ShellDto(
    double? ThicknessMm,
    string? ThinDirection,
    int? RemovedFaceCount,
    IReadOnlyList<string>? RemovedFaceRefs = null);
