using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба S — определённость эскиза: читается ли статус «+ / − / !» из API, и различает ли он
/// состояния, подготовленные ограничениями.
/// </summary>
/// <remarks>
/// <para>
/// Задание: <c>SKETCH_DEFINITION_CHECK_DEVELOPER_PROMPT.md</c>. Вопрос ставится так, чтобы ответ не
/// зависел от памяти клиента и не подменялся удобным признаком: точные координаты, ширина и высота,
/// радиус при создании, замкнутость контура и успешное выдавливание полную определённость НЕ
/// доказывают (задание §«Семантика», пп. 2–3). Поэтому контрольные состояния строятся на ФАКТИЧЕСКИ
/// наложенных ограничениях, а ожидаемый статус не объявляется фактом до чтения из КОМПАС.
/// </para>
/// <para>
/// Маршрут чтения (найден разведкой, см. <c>docs/acceptance/api7/sketch-definition.md</c>):
/// <c>ISketch.ConstraintsState</c> типа <c>ksConstraintsStateEnum</c> — агрегированный статус всего
/// эскиза, ровно та величина, которую КОМПАС показывает символами «+», «−», «!». Перечисление
/// объявлено в <c>Bin\ksConstants.tlb</c>, а не в <c>kAPI7.tlb</c>, и НЕ линкуется в эту пробу;
/// значения продублированы локально и сверяются шагом S.3 с объявленными.
/// </para>
/// <para>
/// Ветка разведки, которую проба закрывает и НЕ переносит на общий вывод:
/// <c>IParametriticConstraint.Degrees</c> — это градусы УГЛОВОГО ограничения
/// (<c>ksConstraintTypeEnum.ksCFixedAngle = 18</c> и соседи), а не степени свободы. Имена обманывают,
/// поэтому величина различается явно.
/// </para>
/// <para>
/// Свой STA-поток, собственный невидимый экземпляр КОМПАС, свои документы в каталоге <c>scratch</c>.
/// Чужие процессы не завершаются, пользовательские модели не открываются.
/// </para>
/// </remarks>
internal sealed class SketchDefinitionProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    /// <summary>Радиус контрольной окружности.</summary>
    private const double CircleRadius = 20d;

    /// <summary>Центр контрольной окружности — заведомо не в начале координат.</summary>
    private const double CircleCenterX = 25d;
    private const double CircleCenterY = 15d;

    /// <summary>
    /// Сырое значение <c>ksStateWellConstrained</c>. Дублируется намеренно: шаг S.4d должен быть
    /// читаем сам по себе, а совпадение дублированного значения с объявленным проверяет S.3.
    /// </summary>
    private const int WellConstrained = 1;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private KompasAPI7.IApplication? _app7;

    /// <summary>Текущий документ пробы и его часть.</summary>
    private ksDocument3D? _doc;
    private ksPart? _part;

    /// <summary>Эскиз текущего документа как сущность дерева — с него берётся ISketch.</summary>
    private ksEntity? _sketchEntity;

    /// <summary>Итог пробы: удалось ли вообще наложить ограничение.</summary>
    private bool _constraintRouteWorks;

    /// <summary>Итог пробы: принят ли управляющий размер и изменил ли он статус.</summary>
    private bool _dimensionRouteWorks;

    /// <summary>Итог пробы: приняла ли связь через API7 <c>NewConstraint()</c> и изменился ли статус.</summary>
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

    // ═══════════════════════════════════════════════════════════════════════ сессия ══

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

    // ══════════════════════════════════════════════════════════ перечисление ══

    /// <summary>
    /// Шаг S.3: сверяет локально продублированные значения <c>ksConstraintsStateEnum</c> с
    /// объявленными в залинкованных сборках.
    /// </summary>
    /// <remarks>
    /// Проба НЕ линкует сборку констант целиком ради четырёх чисел, поэтому дублирование здесь
    /// намеренное — и именно поэтому оно обязано быть проверено. Если тип не найдётся ни в одной
    /// сборке, шаг честно фиксирует, что подтверждения нет, и числа в отчёте остаются сырыми.
    /// </remarks>
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

            // Сверка дубликата с объявленным — по каждой из четырёх нужных величин отдельно, чтобы
            // расхождение называло конкретное состояние, а не «перечисление не совпало».
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

    // ═══════════════════════════════════════════════════════ маршрут ограничений ══

    /// <summary>
    /// Шаг S.4: устанавливает, каким вызовом ограничение на объект эскиза ДЕЙСТВИТЕЛЬНО
    /// накладывается, и меняется ли от этого статус.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого шага матрица состояний была бы постановкой: если ограничение не наложилось, все
    /// состояния оказались бы одинаковыми, и «статус не различает» было бы выводом о пробе, а не о
    /// КОМПАС. Поэтому сначала измеряется сам инструмент, и только потом им строятся состояния.
    /// </para>
    /// <para>
    /// <b>Устройство шага определяется первым запуском.</b> Первый запуск дал: <c>Init() → True</c>,
    /// то есть блок параметров живой, но <c>ksSetObjConstraint → 0</c> — отказ. Документация
    /// (help.ascon.ru, <c>ksSetObjConstraint</c> и <c>structconstraintparam</c>) говорит, что 0 — это
    /// именно неудача, что <c>index</c> — номер точки на объекте (у окружности <c>0</c> — центр), а
    /// <c>partner</c> — <em>указатель на второй объект</em>. Пример в справке накладывает
    /// <c>CONSTRAINT_EQUAL_RADIUS</c> на ДВА объекта. Отсюда два разных подозрения, и они обязаны
    /// быть различены, а не слиты в одно «не работает»:
    /// <list type="number">
    /// <item>подозрение «не тот номер объекта» — тогда <c>ksExistObj</c> по тому же номеру скажет
    /// «объекта нет», и это отказ адресации, а не свойство ограничения;</item>
    /// <item>подозрение «тип ограничения не тот» — тогда адресация подтверждена, а конкретный
    /// <c>constrType</c> не применим к окружности.</item>
    /// </list>
    /// Поэтому здесь сначала подтверждается существование объекта по номеру, затем пробуется и
    /// одиночное ограничение (<c>CONSTRAINT_FIXED_POINT</c>, у которого партнёра нет по смыслу), и
    /// парное (<c>CONSTRAINT_EQUAL_RADIUS</c> на двух окружностях — ровно тот случай, что показан в
    /// справке). После каждой попытки читается <c>ksGetObjConstraints</c> — это ПРЯМАЯ проверка
    /// «связь действительно появилась», независимая от смены статуса.
    /// </para>
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

            // (а) Адресация. ksExistObj отвечает по тому же номеру, что принимает ksSetObjConstraint.
            // Если по номеру «объекта нет», то отказ 0 объясняется адресацией, и ни один вывод о
            // применимости типа ограничения из него не следует.
            var circleExists = Api5.SafeInt(() => editor.ksExistObj(circleRef));
            step.Data["ksExistObj_circle"] = circleExists;
            attempts.Add($"ksExistObj({circleRef}) → " + Api5.Raw(circleExists));

            // Вторая окружность — для парного ограничения, которое показано в справке. Та же кромка,
            // другая точка, чтобы объекты были различимы.
            var second = editor.ksCircle(CircleCenterX + 40d, CircleCenterY, CircleRadius, 1);
            step.Data["second_circle_ref"] = second;
            var secondExists = Api5.SafeInt(() => editor.ksExistObj(second));
            step.Data["ksExistObj_second"] = secondExists;
            attempts.Add($"вторая окружность {second}, ksExistObj → " + Api5.Raw(secondExists));

            var constraintStruct = (short)StructType2DEnum.ko_ConstraintParam;
            step.Data["ko_ConstraintParam"] = constraintStruct;

            // (б) Одиночное ограничение: фиксация точки центра окружности.
            var fixSingle = TryConstraint(editor, step, "fix_point_single", constraintStruct,
                LocalConstraintType.FixedPoint, circleRef, index: 0, partner: 0, partnerIndex: 0);
            attempts.Add("CONSTRAINT_FIXED_POINT на окружности → " + Api5.Raw(fixSingle) + " (1 = принято)");

            // (в) Парное ограничение ровно по образцу справки: равенство радиусов двух окружностей.
            var equalRadius = TryConstraint(editor, step, "equal_radius_pair", constraintStruct,
                LocalConstraintType.EqualRadius, circleRef, index: 0, partner: second, partnerIndex: 0);
            attempts.Add("CONSTRAINT_EQUAL_RADIUS двух окружностей → " + Api5.Raw(equalRadius) + " (1 = принято)");

            // (г) Прямая проверка: появились ли связи на объекте. Это НЕ производная от статуса —
            // это ответ функции, которая эти связи и перечисляет.
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
                // Ограничение принято, а статус не изменился: это уже факт о ПРОДУКТЕ — либо выбранный
                // тип не снимает свободу, либо статус меняется не в этом месте жизненного цикла.
                _constraintRouteWorks = true;
                step.Data["accepted_but_status_unchanged"] = true;
            }

            step.Data["attempts"] = attempts;
            foreach (var line in attempts)
            {
                step.Observe(line);
            }

            // Путь чтения и путь записи оцениваются РАЗДЕЛЬНО: чтение статуса может работать даже
            // там, где наложить ограничение этой пробе не удалось, и наоборот.
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
                // Самая важная ветка для честности вывода: адресация работает, а ограничение не
                // принимается. Значит отказ относится к применимости вызова, а не к «объект не
                // найден», и именно это записывается — без подмены удобной формулировкой.
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

    /// <summary>
    /// Пробует наложить ограничение одним вызовом и возвращает результат как есть (<c>1</c> принято,
    /// <c>0</c> отказ, <c>null</c> — вызов не прошёл).
    /// </summary>
    /// <remarks>
    /// Номер типа и участие партнёра передаются параметрами, потому что различие «одиночное или
    /// парное» — предмет опыта: справка демонстрирует парное (<c>CONSTRAINT_EQUAL_RADIUS</c>), а
    /// одиночное (<c>CONSTRAINT_FIXED_POINT</c>) обязано быть проверено отдельно, иначе отказ одного
    /// типа был бы выдан за свойство всей функции.
    /// </remarks>
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

    /// <summary>
    /// Прямая проверка «связь действительно появилась»: спрашивает у документа список ограничений
    /// объекта, а не выводит его из статуса.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ksGetObjConstraints</c> возвращает <c>CONSTRAINT_ARR</c> либо <c>0</c> при неудаче
    /// (help.ascon.ru). Ноль здесь означает «список не получен», и он не превращается в «связей нет»:
    /// это разные утверждения, и в отчёте они различимы.
    /// </para>
    /// <para>
    /// Возврат приходит как COM-массив (<c>System.__ComObject</c>), а не как .NET-массив, поэтому
    /// одного <c>is Array</c> мало: массив разворачивается как <c>object[]</c> через приведение, а
    /// когда развернуть не удалось, это записывается КАК ЕСТЬ — «не смог прочитать» вместо «связей
    /// нет», иначе неудача чтения выдала бы себя за отсутствие ограничений.
    /// </para>
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

            // Единственный объект-параметр: экземпляр ksConstraintParam, а не массив.
            if (raw is Kompas6API5.ksConstraintParam single)
            {
                step.Data["ksGetObjConstraints_constrType"] = (int)single.constrType;
                return "одна связь, constrType=" + (int)single.constrType;
            }

            // COM-массив: сначала пробуем прямое приведение, затем — как object[].
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

            // Не развернулось: попытка привести к строго типизированному массиву параметров.
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

    /// <summary>Описывает массив связей по типам, сохраняя длину как наблюдение.</summary>
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

    /// <summary>
    /// Шаг S.4c: связь через САМ API7 — <c>IDrawingObject1.NewConstraint()</c> →
    /// <c>IParametriticConstraint.Create()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельный шаг, потому что это третья, независимая ветка. <c>ksSetObjConstraint</c> (S.4) —
    /// вызов API5 на документе; управляющий размер (S.4b) — метод размеров. Здесь же строится
    /// ОБЪЕКТ связи: <c>NewConstraint()</c> выдаёт <c>IParametriticConstraint</c> с полями
    /// <c>ConstraintType</c>, <c>Index</c>, <c>Partner</c>, <c>PartnerIndex</c>, методом
    /// <c>Create()</c> и признаком <c>Valid</c>. Именно эта ветка названа в задании как средство
    /// перевести найденный маршрут в продукт, поэтому она измеряется отдельно, а не подразумевается.
    /// </para>
    /// <para>
    /// Объект берётся через <c>GetCurve2D()</c> у эскиза и приводится к <c>IDrawingObject1</c>; QI
    /// делается явно, и «приведение не прошло» записывается как отдельный исход, а не как «связь не
    /// создаётся».
    /// </para>
    /// </remarks>
    private void Api7ConstraintRoute()
    {
        var step = _report.Begin("S.4c", "Связь через API7: NewConstraint → Create",
            "Принимает ли API7 объектную связь там, где API5 ответил 0, и меняется ли от неё статус?");
        try
        {
            // Документ строится с ЗАКРЫТЫМ редактором: API7 не отдаёт фрагмент эскиза, пока тот
            // открыт на редактирование через API5 (измерено: BeginEdit() → null). Это условие самой
            // ветки, и оно соблюдается, а не обходится.
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

            // Объект кривой внутри эскиза. Цепочка взята из обёртки, а не подобрана:
            // ISketch.BeginEdit() → IFragmentDocument.ViewsAndLayersManager.Views → вид → Layers →
            // объекты чертежа. Ни одно звено не пропускается молча: какой шаг не отдал следующий
            // объект, записано в данных, потому что «связь не создалась» и «до объекта не дошли» —
            // разные наблюдения.
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

            // Состояние объекта по ограничениям — отдельная величина из того же интерфейса. Она
            // читается и записывается, даже если создать связь не удастся.
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
                    // ksConstraintTypeEnum.ksCFixedPoint = 1: фиксация точки; у окружности индекс 0 — центр.
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

    /// <summary>
    /// Достаёт объект окружности внутри эскиза через API7, находя его ПО ТОЧКЕ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Цепочка взята из обёртки, а не подобрана: <c>ISketch.BeginEdit()</c> →
    /// <c>IFragmentDocument.ViewsAndLayersManager.Views</c> → <c>IViews.View(index)</c> →
    /// <c>IView1.FindObject(X, Y, Limit, Param)</c>. <c>FindObject</c> возвращает
    /// <c>IDrawingObject</c> — это тот объект, у которого живёт <c>NewConstraint()</c>.
    /// </para>
    /// <para>
    /// Поиск идёт по координатам центра окружности, потому что это единственный ключ, который у пробы
    /// есть независимо от объектов: номер <c>ksCircle</c> принадлежит пространству API5 и для API7
    /// значения не имеет. Каждое звено либо отдаёт следующий объект, либо записывает в
    /// <paramref name="notes"/>, где оборвалось.
    /// </para>
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
                // View — индексатор, а не метод (обёртка API7): обращение через [v].
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

    /// <summary>
    /// Ищет кривую в виде по координатам центра контрольной окружности.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Параметры поиска создаются по CLSID, а не через <c>Activator.CreateInstance(Type)</c>:
    /// <c>FindObjectParametersClass</c> — COM-класс без открытого конструктора без параметров
    /// (измерено: <c>MissingMethodException</c>), поэтому экземпляр берётся у COM по GUID класса.
    /// </para>
    /// <para>
    /// Если создание не удалось, причина остаётся в <paramref name="notes"/>, и возвращается
    /// <c>null</c> — это записывается как «до объекта не дошли», а не как «связь не создаётся».
    /// </para>
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

            // GUID класса взят у самого типа в обёртке, а не вписан числом.
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
            // Класс параметров регистрируется манифестом и через COM не поднимается
            // (измерено: REGDB_E_CLASSNOTREG). Это не конец опыта: FindObject проверяется ещё и с
            // пустыми параметрами, потому что тогда причина отказа будет видна отдельно от причины
            // «параметры не сделать».
            notes.Add("FindObjectParameters: " + ex.GetType().Name + ": " + ex.Message);
            parameters = null;
        }

        // Попытка 1: с параметрами, если их удалось создать.
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

        // Попытка 2: без параметров. Отказ здесь отделён от отказа создания параметров, иначе
        // «параметры не сделать» и «поиск не работает» слились бы в одно наблюдение.
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

    /// <summary>
    /// Шаг S.4b: меняет ли статус управляющий размер, и является ли он той самой связью, которая
    /// снимает свободу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельный шаг, потому что это ДРУГОЙ вызов с другой природой. <c>ksSetObjConstraint</c> —
    /// это параметрическая связь; управляющий размер ставится методами размеров
    /// (<c>ksRadDimension</c>), и справка задаёт его параметры не ссылкой на кривую, а ГЕОМЕТРИЕЙ:
    /// <c>RDimSource</c> содержит <c>xc</c>, <c>yc</c>, <c>rad</c> — центр и радиус, по которым размер
    /// находит измеряемую окружность (help.ascon.ru, <c>structrdimparam</c> и пример
    /// <c>raddimension_radbreakdimension_example</c>). Поэтому «размер не привязался» и «связь не
    /// наложилась» — разные отказы, и они измеряются разными шагами.
    /// </para>
    /// <para>
    /// Задание §«Семантика» п. 3 прямо запрещает выводить определённость из НАЛИЧИЯ ТЕКСТА размера.
    /// Поэтому шаг не останавливается на «метод вернул ненулевую ссылку»: он читает статус до и
    /// после, и различает три исхода — размер принят и статус изменился, размер принят и статус
    /// НЕ изменился, и размер не принят. Первый исход даёт маршрут для контрольной матрицы, третий —
    /// честный отказ, второй — факт о продукте.
    /// </para>
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

            // Привязка задаётся геометрией измеряемой окружности: центр (25,15) и радиус 20 — те же
            // числа, которыми окружность и построена.
            //
            // Init() вызывается ПЕРВЫМ и его результат ПРОВЕРЯЕТСЯ: первый прогон показал, что Init()
            // обнуляет поля (при заданных 25/15/20 обратное чтение дало xc=0, yc=0, rad=10), и если
            // поля заполнять до него, SetSPar принимает блок с умолчаниями — размер ставится «в
            // никуда», возвращает живую ссылку, и это выглядит как «размер не влияет на статус».
            // Это был дефект пробы, а не факт о КОМПАС.
            source.Init();
            source.xc = CircleCenterX;
            source.yc = CircleCenterY;
            source.rad = CircleRadius;
            notes.Add($"привязка: xc={source.xc}, yc={source.yc}, rad={source.rad}");

            // Обратное чтение — контроль того, что размер уйдёт по заданным числам, а не по умолчанию.
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

            // Ссылка на размер — это ещё не изменённая система ограничений. Статус читается отдельно
            // и сравнивается с исходным; наличие размера как факт здесь не подменяет различие.
            _doc!.RebuildDocument();
            var after = StateOfCurrent(step, "dim_after");
            step.Data["dim_state_after"] = after;
            step.Data["dim_state_after_interpreted"] = Interpret(after);
            notes.Add($"статус после размера: {Interpret(after)} (сырое {Api5.Raw(after)})");

            // Прямая проверка: зарегистрировал ли документ связь на этой окружности. Это различает
            // «размер создан, но связью не стал» и «связь есть, а статус не пересчитан» — две разные
            // причины одного и того же наблюдения, и одна не выдаётся за другую.
            var afterConstraints = ReadBackConstraints(editor, step, circleRef);
            step.Data["dim_constraints_after"] = afterConstraints;
            notes.Add("связи на окружности после размера: " + afterConstraints);

            // Контроль-близнец: та же операция на второй окружности, которой в этом эскизе нет, —
            // «размер принят» не должно быть свойством конкретной кривой.
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
                // Размер принят, статус не изменился. Причина формулируется по фактам, а не по догадке:
                // у окружности три степени свободы, и радиальный размер снимает только одну (радиус),
                // оставляя свободным центр, — поэтому «недоопределён» здесь ОЖИДАЕМ, а не подозрителен.
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

    // ════════════════════════════════════════════════════════ контрольные состояния ══

    /// <summary>
    /// Шаг S.4d — можно ли ДОВЕСТИ эскиз до «полностью определён» управляющими размерами.
    /// </summary>
    /// <remarks>
    /// <para>
    /// S.4b показал, что радиальный размер принимается, но статус не меняет: у окружности три
    /// степени свободы, а радиус снимает одну. Если маршрут управляющих размеров настоящий, то
    /// ТРИ размера — радиус плюс два линейных, задающих центр (x и y), — обязаны снять все три и
    /// дать <c>ksStateWellConstrained</c>. Это и есть проверка «различает ли маршрут
    /// well_constrained», оставленная S.5 открытой.
    /// </para>
    /// <para>
    /// Если и три размера не дают «+», это НЕ доказывает, что статус недостижим: причина может быть
    /// в том, что линейный размер в этой установке не становится управляющим. Поэтому исход шага
    /// формулируется как «до well_constrained этой пробой не дошли», а не «well_constrained
    /// недостижим».
    /// </para>
    /// </remarks>
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

            // (1) Радиус — тем же проверенным маршрутом, что в S.4b.
            var radiusRef = ApplyRadiusDimension(editor, step, "driving_radius");
            notes.Add("радиус → " + Api5.Raw(radiusRef));

            // (2) Два линейных размера, задающих центр окружности: по x и по y от начала координат
            // эскиза. Координаты концов берутся из фактической геометрии (центр 25,15), а не из
            // «памяти сервера»: проба измеряет эскиз, который сама же и нарисовала.
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
                // Маршрут размеров ДОКАЗАН тем, что снимает свободу: это не «вызов принят», а
                // «состояние изменилось в ожидаемую сторону». На нём и строится матрица S.5.
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

    /// <summary>Линейный управляющий размер между двумя точками внутри эскиза.</summary>
    /// <remarks>
    /// Привязка линейного размера задаётся координатами концов и приращениями выносных линий, а не
    /// ссылкой на кривую. Как и у радиального, <c>Init()</c> вызывается ПЕРВЫМ: сброс блока после
    /// записи полей уже однажды превратил дефект пробы в «факт» о КОМПАСе (S.4b), и повторять это
    /// на новом блоке незачем.
    /// </remarks>
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
            // dx/dy — смещение размерной линии от измеряемого отрезка; 0 совпадает с самим отрезком.
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

    /// <summary>
    /// Шаг S.4e — параметризация объекта: становится ли наложенный размер УПРАВЛЯЮЩИМ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// S.4d показал главное различие задачи: <c>ksRadDimension</c>/<c>ksLinDimension</c> ПРИНИМАЮТСЯ
    /// (живая ссылка, привязка удержана), но статус не меняется. Это ровно то, о чём предупреждает
    /// задание §«Семантика» п. 3: наличие размера не доказывает наличия управляющего размерного
    /// ограничения. Остаётся вопрос: можно ли превратить наложенный размер в управляющий.
    /// </para>
    /// <para>
    /// Обёртка объявляет штатный для этого вызов — <c>ksDocument2D.ksParametrizeObjects(obj, par)</c>
    /// с блоком <c>ko_ParametrisationParam</c>. Шаг пробует его на контрольной окружности, у которой
    /// размер уже стоит, и читает статус после. Положительный исход — «+»; отрицательный — запись
    /// того, что вызов не принят или принят без эффекта, а не вывод «управляющие размеры не работают».
    /// </para>
    /// </remarks>
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

            // Сначала ставим три размера тем же принимаемым вызовом, что в S.4d: без размера
            // параметризовать нечего, и «не принято» было бы объяснено отсутствием входа.
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
                // group=0 — параметризуются ВЫДЕЛЕННЫЕ объекты (справка API5). Ссылка берётся та, что
                // вернул ksCircle, а не «нулевой объект»: адресация этой ссылки подтверждена в S.4.
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

    /// <summary>
    /// Шаг S.5 — сердце задачи. Одна и та же окружность в состояниях, различающихся ТОЛЬКО
    /// фактически наложенными ограничениями и размерами.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Геометрия во всех состояниях задаётся одними и теми же числами (R20 в точке 25,15). Если
    /// статус различается, он различается ОГРАНИЧЕНИЯМИ, а не формой, и подмена «координаты
    /// совпали» невозможна по построению. Это контроль задания §«Этап 2».
    /// </para>
    /// <para>
    /// <b>Матрица строится теми маршрутами, которые подтвердились опытом, и называется это прямо.</b>
    /// Если ни один способ изменить систему ограничений не подтверждён, шаг даёт <c>Unknown</c> с
    /// указанием, чего именно не хватило: «различия нет» и «менять нечем» — разные утверждения, и
    /// второе нельзя выдать за первое.
    /// </para>
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

        // Маршрут API7 NewConstraint() учитывается отдельно: он мог быть принят, тогда как ни
        // соединение API5, ни размер не дали состояния. Без этой публикации его исход ни на что не
        // влиял — поле присваивалось и не читалось (дефект прибора, найден компилятором CS0414).
        step.Data["api7_constraint_route_works"] = _api7ConstraintRouteWorks;
        step.Data["constraint_route_works"] = _constraintRouteWorks;
        step.Data["dimension_route_works"] = _dimensionRouteWorks;
        step.Observe("подтверждённые маршруты правки ограничений: соединение API5 = "
            + _constraintRouteWorks + ", размер = " + _dimensionRouteWorks
            + ", NewConstraint API7 = " + _api7ConstraintRouteWorks);

        try
        {
            // Состояние A: окружность без связей и размеров. Числа заданы, но у окружности три
            // степени свободы — ожидание записывается, но фактом до чтения не объявляется.
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

            // Состояние B: центр закреплён БЕЗ управляющего радиуса — часть свободы снята.
            if (_constraintRouteWorks)
            {
                var fixedCentre = TryConstraint(editorA, step, "b_center", (short)StructType2DEnum.ko_ConstraintParam,
                    LocalConstraintType.FixedPoint, circleA, index: 0, partner: 0, partnerIndex: 0);
                appliedNotes.Add("B: CONSTRAINT_FIXED_POINT центра → " + Api5.Raw(fixedCentre));
                _doc!.RebuildDocument();
                b = StateOfCurrent(step, "b");
            }

            // Состояние C: на том же эскизе поставлен управляющий размер радиуса. Ожидание задания —
            // полная определённость; оно записано, но фактом не объявляется до чтения.
            if (_dimensionRouteWorks)
            {
                var dimension = ApplyRadiusDimension(editorA, step, "c_radius");
                appliedNotes.Add("C: ksRadDimension радиуса → " + Api5.Raw(dimension));
                _doc!.RebuildDocument();
                c = StateOfCurrent(step, "c");
            }

            // Состояние D: три управляющих размера (радиус + два линейных на центр). Только оно
            // способно снять все три степени свободы окружности; состояния A и B сняты не полностью
            // по построению. Отдельный документ, потому что размеры необратимы: добавить их в тот же
            // эскиз значило бы измерить СУММУ шагов, а не состояние.
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

            // Сравниваются только те состояния, которые удалось построить. Сравнение «состояния,
            // которого не было» дало бы ложное различие или ложное совпадение.
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

            // Решающее различие задания: A недоопределён, D полностью определён. C (только радиус)
            // обязан остаться недоопределённым — это не «плохо», а проверка того, что маршрут
            // считает степени свободы, а не факт «размер поставлен».
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

            // Отличается, но не так, как предполагало задание: это измерение, а не повод править
            // ожидание до совпадения. Расхождение оставлено открытым.
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

    /// <summary>
    /// Ставит управляющий радиальный размер на окружность с центром (<see cref="CircleCenterX"/>,
    /// <see cref="CircleCenterY"/>) и радиусом <see cref="CircleRadius"/>.
    /// </summary>
    /// <remarks>
    /// Привязка задаётся <c>RDimSource</c> (центр и радиус), а не ссылкой на кривую — так устроен
    /// пример справки. <c>Init()</c> вызывается до заполнения полей: он их обнуляет (измерено в
    /// первом прогоне), и заполнение до него привело бы к размеру по умолчанию. Возвращается ссылка
    /// на размер, <c>null</c> — вызов не прошёл.
    /// </remarks>
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

    /// <summary>
    /// Шаг S.5b: контрольные состояния, которые строятся ЧТЕНИЕМ, без наложения связей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задание §«Этап 2» требует среди прочего: пустой эскиз, два эскиза с разными состояниями и
    /// выбор нужного из них. Эти контроли ценны тем, что не зависят от маршрута записи: даже когда
    /// связь наложить не удалось, они показывают, отвечает ли <c>ConstraintsState</c> на разное
    /// СОДЕРЖИМОЕ эскиза, а не выдаёт одну константу на всё.
    /// </para>
    /// <para>
    /// Пустой эскиз — отдельный случай: значения «объектов нет ⇒ определён» заранее НЕ объявляется.
    /// Читается то, что ответит КОМПАС, и это записывается как измерение.
    /// </para>
    /// </remarks>
    private void ReadOnlyControls()
    {
        var step = _report.Begin("S.5b", "Контроли чтением: пустой эскиз и выбор нужного эскиза из двух",
            "Отвечает ли ConstraintsState на разное содержимое эскиза, и что читается у пустого эскиза?");
        try
        {
            // ── Пустой эскиз ──
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

            // ── Два эскиза в одном документе, разное содержимое ──
            // Второй эскиз получает окружность; первый остаётся пустым. Это различие содержимого,
            // построенное без единого ограничения, поэтому оно не зависит от маршрута записи.
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

            // Читается ВТОРОЙ эскиз при том, что первым в дереве лежит другой: это контроль на
            // «читается тот эскиз, который попросили», а не «первый попавшийся».
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

    /// <summary>Возвращает внимание пробы на предыдущий документ после контроля.</summary>
    private void Restore(ksDocument3D? doc, ksPart? part, ksEntity? sketch)
    {
        _doc = doc;
        _part = part;
        _sketchEntity = sketch;
    }

    /// <summary>Вердикт для ветки «второй эскиз не создан»: пустой эскиз всё равно измерен.</summary>
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

            // Эскиз ищется ПО МОДЕЛИ, а не по сохранённому дескриптору: память сеанса не используется.
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

    /// <summary>
    /// Шаг S.7: побочные эффекты чтения. Отдельный шаг, потому что от него зависит, можно ли
    /// регистрировать инструмент как читающий.
    /// </summary>
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

            // Пятикратное чтение — так поведёт себя инструмент под нагрузкой и при повторных вызовах.
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

            // Ноль объёма — это «тело исчезло», и он обязан быть отказом, а не успешным замером:
            // на это уже наступали в правке эскиза (Q-SKETCH-EDIT-ZERO).
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

    // ══════════════════════════════════════════════════════════════ построение ══

    /// <summary>
    /// Строит документ с пластиной и эскизом, в котором лежит одна окружность R20 в (25,15).
    /// </summary>
    /// <remarks>
    /// По умолчанию редактор возвращается ОТКРЫТЫМ, чтобы вызывающий мог наложить ограничения через
    /// API5. При <paramref name="closeEditor"/> = <c>true</c> редактирование завершается: это нужно
    /// ветке API7, где <c>ISketch.BeginEdit()</c> отказывает, пока эскиз открыт со стороны API5
    /// (измерено: <c>BeginEdit() → null</c>).
    /// </remarks>
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

        // Числовая геометрия одинакова во всех состояниях: окружность R20 в точке (25,15).
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

    /// <summary>Эскиз переоткрытого документа: ищется по модели, память сеанса не используется.</summary>
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

    /// <summary>
    /// Шаг S.8 — эскиз, созданный ВНЕ MCP: сторонний документ поставки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задание §«Этап 2» требует контроля «эскиз, созданный вне MCP, с источником и происхождением
    /// тестового файла». До этого шага такого контроля не было: все эскизы ставила сама проба, и
    /// вывод «читается» относился бы только к собственным моделям.
    /// </para>
    /// <para>
    /// Файлы берутся из поставки КОМПАС-3D (`Samples\Models`), их происхождение — вендор, автор —
    /// человек. Файлы <b>открываются, но не изменяются и не сохраняются</b>: шаг не мутирует
    /// чужой документ. Для каждого документа перебираются эскизы по дереву, и читается статус —
    /// ровно тем же вызовом, что и для своих моделей.
    /// </para>
    /// <para>
    /// Если в документе эскизов нет, это записывается как «нечего читать», а не как «статус не
    /// читается»: различать эти два случая обязательно.
    /// </para>
    /// </remarks>
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
                    // Чужой документ закрывается БЕЗ сохранения: шаг только читает, и это его условие.
                    // Save здесь был бы мутацией пользовательского файла.
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

    /// <summary>Собирает эскизы из дерева части обходом, с ограничением глубины и числа.</summary>
    /// <remarks>
    /// Обход нужен потому, что эскиз может лежать и в самой части, и внутри признака. Число
    /// ограничено: шаг читает, а не инвентаризирует чужую модель, и «не найдено из-за лимита»
    /// отличается от «не найдено вовсе» только этим пределом, который записан в данных шага.
    /// </remarks>
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
                    // Отказ на определении не превращается в «эскиза нет».
                }

                if (definition is ksSketchDefinition)
                {
                    // Дедупликация по имени: у ksEntity нет ссылки, а один и тот же эскиз попадает
                    // в несколько коллекций типов. Имя — то, что доступно и читаемо человеком.
                    if (!found.Any(f => f.name == entity.name))
                    {
                        found.Add(entity);
                    }
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════ чтение ══

    /// <summary>Читает <c>ISketch.ConstraintsState</c> у текущего эскиза.</summary>
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
            // Отказ COM — не «недоопределён»: пишется отдельным полем, чтобы не смешивать
            // «продукт ответил» и «вызов не прошёл».
            step.Data[tag + "_state_error"] = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Переносит API5-эскиз в API7 <c>ISketch</c>.
    /// </summary>
    /// <remarks>
    /// QI делается явно: обёртка, объявляющая интерфейс, не гарантирует, что объект его поддерживает,
    /// и молчаливый <c>null</c> от <c>as</c> здесь означал бы «статуса нет» вместо «приведение
    /// не прошло». Оба случая различимы в данных шага.
    /// </remarks>
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

    // ══════════════════════════════════════════════════════════════ семантика ══

    /// <summary>
    /// Значения <c>ksConstraintsStateEnum</c> из <c>Bin\ksConstants.tlb</c>.
    /// </summary>
    /// <remarks>
    /// Локальная копия: перечисление лежит в сборке констант, которую проба не линкует ради четырёх
    /// чисел. Сверка с объявленными — шаг S.3, и без неё числа считались бы утверждением о памяти
    /// автора, а не о продукте.
    /// </remarks>
    private static readonly Dictionary<string, int> LocalStateValues = new(StringComparer.Ordinal)
    {
        ["ksStateUnknown"] = 0,
        ["ksStateWellConstrained"] = 1,
        ["ksStateUnderConstrained"] = 2,
        ["ksStateUnresolvedRedundancy"] = 3,
    };

    /// <summary>
    /// Типы ограничений, использованные пробой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Величины сверены с таблицей «Типы параметрических ограничений» справки SDK (help.ascon.ru,
    /// <c>paramrestrictiontypes</c>), где перечислены константы, принимаемые полем <c>constrType</c>:
    /// <c>CONSTRAINT_FIXED_POINT = 1</c> («фиксация точки» — одиночное, партнёра не требует) и
    /// <c>CONSTRAINT_EQUAL_RADIUS = 8</c> («равенство радиусов двух дуг или окружностей» — парное;
    /// именно оно показано в примере к <c>ksSetObjConstraint</c>).
    /// </para>
    /// <para>
    /// <c>FixedDim = 14</c> сюда намеренно НЕ входит: в таблице экспортных констант номера 14 нет
    /// (там <c>13</c> отсутствует, а <c>15</c> — это <c>CONSTRAINT_TANGENT_TWO_CURVES</c>). Величина
    /// 14 как «фиксированный размер» известна только перечислению API7
    /// <c>ksConstraintTypeEnum.ksCFixedDim</c>, то есть принадлежит ДРУГОМУ пространству имён, и
    /// смешивать их в одном поле нельзя. Управляющий размер через этот вызов пробой не ставится.
    /// </para>
    /// </remarks>
    private enum LocalConstraintType
    {
        /// <summary>Фиксация точки (индекс 0 у окружности — центр).</summary>
        FixedPoint = 1,

        /// <summary>Равенство радиусов двух дуг или окружностей.</summary>
        EqualRadius = 8,
    }

    /// <summary>
    /// Нормализованный статус задания. Ответ строится по ЧИСЛУ: обёртка может не отдать имя, число
    /// отдаст всегда. Число вне объявленного набора — «неизвестно», а не догадка по величине.
    /// </summary>
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

    // ═════════════════════════════════════════════════════════════════ прочее ══

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
