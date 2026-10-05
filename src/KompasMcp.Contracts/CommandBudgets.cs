namespace KompasMcp.Contracts.Ipc;

/// <summary>
/// ЕДИНАЯ таблица бюджетов команды: одна на Хост и Worker, а не две расходящиеся.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему таблица общая.</b> До 05.10.2026 бюджет Хоста задавался одной настройкой
/// (<c>operation_budget_ms</c>, 120 с по умолчанию), а бюджеты Worker — отдельным <c>switch</c> в
/// <c>CommandDispatcher</c> (180–300 с для подключения, STEP, снимка, отверстия, операций над
/// телами, сборки и сопряжений). Следствия измерены разбором пути вызова: Хост сдавался на 120 с
/// раньше, чем Worker доходил до своего предела, объявлял <c>OUTCOME_UNKNOWN</c> и ломал канал
/// (<c>MarkBroken</c>), а следующий вызов через 20 с убивал Worker, который ещё работал по своему
/// бюджету. То есть бюджеты Worker выше 120 с были НЕДОСТИЖИМЫ, а локальный конфиг (240 с) делал
/// исход зависимым от гонки. Одна таблица убирает расхождение по построению.
/// </para>
/// <para>
/// <b>Запас Хоста.</b> Бюджет Хоста — это бюджет Worker плюс <see cref="HostMarginMs"/>: Хост
/// отвечает за передачу кадра, а не за COM-вызов, поэтому он обязан ждать ДОЛЬШЕ, иначе его
/// «сдача» — это не наблюдение, а преждевременный отказ. Меньший бюджет Хоста превращает каждый
/// долгий, но штатный вызов в «исход неизвестен» плюс перезапуск Worker.
/// </para>
/// </remarks>
public static class CommandBudgets
{
    /// <summary>Бюджет по умолчанию для команды, не названной в таблице.</summary>
    public const int DefaultMs = 120_000;

    /// <summary>
    /// Сколько Хост сверх бюджета Worker ждёт ответа. Назван числом, а не «небольшим запасом»:
    /// запас — это и есть разница между «Хост увидел тайм-аут Worker» и «Хост сдался первым».
    /// </summary>
    public const int HostMarginMs = 30_000;

    /// <summary>Бюджет выполнения команды на стороне Worker (STA-полоса), миллисекунды.</summary>
    public static int WorkerBudgetMs(string command) => command switch
    {
        // The probe can enumerate the ROT and the process list: fast, but not free.
        WorkerCommands.EnvironmentProbe => 10_000,
        WorkerCommands.Ping => 2_000,
        WorkerCommands.Connect => 180_000,
        WorkerCommands.ExportStep or WorkerCommands.ImportStep => 300_000,
        // Растровый снимок рендерит документ и (в файловом режиме) пишет файл: это дольше чтения,
        // но короче конвертера. Бюджет назван числом, а не унаследован от умолчания: снимок
        // большого разрешения измерялся секундами (проба P6), и упор в общий бюджет выглядел бы
        // как отказ продукта.
        WorkerCommands.ExportImage => 240_000,
        // Родное отверстие идёт маршрутом API7 (мост + TransferInterface + RebuildModel): это
        // дольше чисто API5-мутации, поэтому бюджет выше умолчания, а не «на глазок».
        WorkerCommands.Hole => 240_000,
        // Операции над телами B3 идут тем же маршрутом API7 плюс перечитывание всех тел документа
        // после перестроения, поэтому бюджет тот же, что у отверстия.
        WorkerCommands.SolidBoolean or WorkerCommands.SolidSplit
            or WorkerCommands.SolidCutByPlane or WorkerCommands.SolidReposition => 240_000,
        // B5: кинематика и оболочка идут маршрутом API5, но обе перестраивают документ и
        // перечитывают тела после перестроения; сечения идут мостом API7 (TransferInterface на
        // каждое сечение) плюс Rebuild.
        WorkerCommands.Sweep or WorkerCommands.Loft or WorkerCommands.Shell => 240_000,
        // Вспомогательная геометрия идёт маршрутом API7 (мост + QI(IAuxiliaryGeomContainer) +
        // Rebuild), а перечисление читает коллекцию вызовом COM на каждый элемент.
        WorkerCommands.CreateAuxGeometry or WorkerCommands.ListAuxGeometry => 240_000,
        WorkerCommands.UpdatePlane => 240_000,
        // Смена опоры эскиза перестраивает зависимое тело: измеренная ступень применения
        // (sketch.Update()) входит в этот бюджет.
        WorkerCommands.SetSketchPlane => 240_000,
        WorkerCommands.ListSketchEntities or WorkerCommands.EditSketchEntity => 240_000,
        // Сборка: вставка компонента читает файл с диска и перестраивает документ, а перечисление
        // структуры делает вызов COM на каждый компонент.
        WorkerCommands.InsertComponent or WorkerCommands.ReplaceComponent => 240_000,
        WorkerCommands.ListComponents or WorkerCommands.SetComponentPlacement
            or WorkerCommands.CheckComponentLinks => 240_000,
        // Сопряжение перестраивает сборку (Update() + RebuildDocument), а перечисление читает
        // каждый объект сопряжения через два интерфейса.
        WorkerCommands.ListMates or WorkerCommands.CreateMate or WorkerCommands.SetMateParameter
            or WorkerCommands.SetMateFixed or WorkerCommands.DeleteMate => 240_000,
        _ => DefaultMs,
    };

    /// <summary>
    /// Сколько Хост ждёт ответа на команду: бюджет Worker плюс <see cref="HostMarginMs"/>.
    /// </summary>
    public static int HostBudgetMs(string command) => WorkerBudgetMs(command) + HostMarginMs;
}
