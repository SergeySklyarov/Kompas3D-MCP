using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Домен сборок — наряд C1, профиль <c>assemblies-minimal-v1</c>, режимы <c>ASM-02…ASM-06</c>
/// (ASM-01 и ASM-07 обслуживает общий жизненный цикл документа).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут измерен живым прогоном.</b> Структура читается через API7 (<c>IPart7.PartsEx</c>),
/// размещение пишется через API5 (<c>ksPart.GetPlacement/SetPlacement/UpdatePlacement</c>), потому
/// что у API7 нет абсолютной записи размещения — только позиционер со сдвигом. Вставка идёт
/// документированным <c>IParts7.AddFromFile</c>, замена источника — <c>ksPart.fileName</c> +
/// <c>ksPart.Update</c>. Живые прогоны 04–05.10.2026 подтвердили: компоненты имеют геометрию
/// (1 тело, 6 граней), а группа <c>ASM</c> проходит на бинарях поставки.
/// </para>
/// <para>
/// <b>Адрес компонента — это НОМЕР в плоском <c>ksDocument3D.PartCollection(true)</c>, а не
/// <c>IPart7.Reference</c>.</b> Измерено 04.10.2026: <c>IPart7.Reference</c> равен 1073741857 и
/// номером компонента не является. Номер адресует ТОЛЬКО компоненты верхнего уровня; вложенный
/// компонент адреса не имеет и мутацию по предположительному номеру выполнять запрещено (см.
/// <see cref="NestedComponentOrdinal"/>).
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Вид ссылки на компонент в реестре ссылок.</summary>
    private const string ComponentRefKind = "component";

    /// <summary>
    /// Порядковый номер ВЛОЖЕННОГО компонента: адреса у него нет. Отличается от любого настоящего
    /// номера тем, что настоящие начинаются с нуля; «-1» означает «адрес API5 неприменим».
    /// </summary>
    private const int NestedComponentOrdinal = -1;

    /// <summary>Предел глубины обхода структуры. Превышение НАЗЫВАЕТСЯ, а не молчит.</summary>
    private const int MaxStructureDepth = 64;

    /// <summary>
    /// Полезная нагрузка ссылки на компонент: представление API7, <c>IPart7.Reference</c> (диагностика,
    /// не адрес), ПОРЯДКОВЫЙ НОМЕР в перечислении структуры и МАТРИЦА РАЗМЕЩЕНИЯ, прочитанная со
    /// стороны API7 документированным <c>IPart7.GetSummMatrix</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядковый номер — адрес, потому что <c>IPart7.Reference</c> номером компонента не является
    /// (измерено: 1073741857), а сопоставление по файлу-источнику НЕОДНОЗНАЧНО при двух экземплярах
    /// одной детали. Адрес по номеру опирается на то, что <c>ksDocument3D.PartCollection(true)</c>
    /// перечисляет компоненты сборки ПЛОСКО, то есть порядок осмыслен ТОЛЬКО для компонентов верхнего
    /// уровня. Для вложенного компонента номер равен <see cref="NestedComponentOrdinal"/> и мутацию по
    /// нему выполнить нельзя.
    /// </para>
    /// <para>
    /// <b><see cref="Matrix7"/> — ВТОРОЙ НЕЗАВИСИМЫЙ ПРИЗНАК ТОЖДЕСТВА.</b> Одно сравнение имени
    /// файла слабо ровно в том случае, ради которого адресация по имени и была отвергнута: подсборка
    /// содержит <c>plate.m3d</c>, и на верхнем уровне стоит <c>plate.m3d</c> — имена совпадают, а
    /// экземпляры разные. Матрица размещения, прочитанная со стороны API7 (документированный
    /// <c>ipart7_getsummmatrix.html</c>: «суммарная матрица преобразования координат, 16 элементов,
    /// 4×4»), сверяется с матрицей API5-представления по тому же номеру: расхождение означает, что
    /// номер ведёт в ДРУГОЙ компонент (дефект M7 ревью 05.10.2026).
    /// </para>
    /// </remarks>
    private sealed record ComponentPayload(IPart7 Part7, int? Reference, int Ordinal, double[]? Matrix7);

    // ===================================================================================== ASM-03
    /// <summary>Перечисление структуры сборки.</summary>
    public ListComponentsResult ListComponents(ListComponentsCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var rows = new List<ComponentRowDto>();
        var uniqueParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var top = TopPart7(document, notes);
        if (top is null)
        {
            return new ListComponentsResult(rows, 0, 0, "api7:IAssemblyDocument.TopPart → IPart7.PartsEx",
                notes);
        }

        var enumerated = 0;
        var topLevelOrdinal = 0;
        Walk(top, parentRef: null, depth: 0, command.Recursive, document, rows, uniqueParts,
            ref enumerated, ref topLevelOrdinal, notes);

        if (rows.Any(r => r.Depth > 0))
        {
            notes.Add("nested_components_not_addressable — вложенные компоненты перечислены для " +
                      "ЧТЕНИЯ структуры, но адреса API5 у них нет: ksDocument3D.PartCollection(true) " +
                      "перечисляет компоненты сборки ПЛОСКО, поэтому размещение, замена и геометрия " +
                      "вложенного компонента этим выпуском НЕ поддерживаются и отвергаются, а не " +
                      "выполняются по предположительному номеру");
        }

        return new ListComponentsResult(rows, uniqueParts.Count, enumerated,
            "api7:IAssemblyDocument.TopPart → IPart7.PartsEx(ksAllParts); размещение — GetSummMatrix",
            notes);
    }

    private void Walk(
        IPart7 node,
        string? parentRef,
        int depth,
        bool recursive,
        DocumentEntry document,
        List<ComponentRowDto> rows,
        HashSet<string> uniqueParts,
        ref int enumerated,
        ref int topLevelOrdinal,
        List<string> notes)
    {
        if (depth > MaxStructureDepth)
        {
            // Предел глубины НАЗЫВАЕТСЯ: молчаливое «обрезали и не сказали» неотличимо от «структура
            // кончилась». Прежний обход предела не имел вовсе.
            notes.Add($"structure_depth_limit — обход структуры остановлен на глубине {MaxStructureDepth}");
            return;
        }

        foreach (var child in ChildrenOf(node, notes))
        {
            enumerated++;

            // АДРЕС API5 ЕСТЬ ТОЛЬКО У КОМПОНЕНТА ВЕРХНЕГО УРОВНЯ. `ksDocument3D.PartCollection(true)`
            // перечисляет компоненты сборки ПЛОСКО, поэтому порядковый номер осмыслен лишь на глубине
            // 0. Прежний обход нумеровал ВСЁ дерево ОДНИМ счётчиком, и номер вложенного компонента
            // передавался в PartCollection — то есть чтение геометрии и мутация попадали бы в ЧУЖОЙ
            // компонент. Это не «поддержка вложенности», а подмена адреса.
            var ordinal = depth == 0 ? topLevelOrdinal++ : NestedComponentOrdinal;
            var row = ReadComponent(document, node, child, parentRef, depth, ordinal, notes);
            rows.Add(row);
            if (row.SourcePath is { Length: > 0 } path)
            {
                uniqueParts.Add(path);
            }

            if (row.IsDetail is null)
            {
                // НЕПРОЧИТАННЫЙ ПРИЗНАК — НАЗЫВАЕТСЯ, А НЕ ПРОГЛАТЫВАЕТСЯ. Прежде обход просто
                // останавливался: «структура кончилась» и «признак не прочитан» были неразличимы,
                // тогда как `CountComponents` в том же случае пишет примечание (дефект L6 ревью
                // 05.10.2026).
                notes.Add($"component_detail_unread_in_walk — у компонента «{row.Name ?? "?"}» " +
                          "признак «деталь/сборка» не прочитан: обход под этим узлом не продолжен, " +
                          "структура может быть неполной");
            }
            else if (recursive && row.IsDetail == false)
            {
                Walk(child, row.ComponentRef, depth + 1, recursive: true, document, rows,
                    uniqueParts, ref enumerated, ref topLevelOrdinal, notes);
            }
        }
    }

    private List<IPart7> ChildrenOf(IPart7 node, List<string> notes)
    {
        var children = new List<IPart7>();
        try
        {
            var raw = node.get_PartsEx((object)(int)ksPart7CollectionTypeEnum.ksAllParts);
            switch (raw)
            {
                case null:
                    notes.Add("parts_ex_returned_null — PartsEx(ksAllParts) вернул null: " +
                              "компонентов не перечислено (возможно, сборка пуста)");
                    break;
                case Array array:
                    foreach (var item in array)
                    {
                        if (item is IPart7 part)
                        {
                            children.Add(part);
                        }
                    }

                    break;
                case IPart7 single:
                    // Документация: один объект → VT_DISPATCH, несколько → VT_ARRAY|VT_DISPATCH.
                    children.Add(single);
                    break;
                default:
                    notes.Add($"parts_ex_unexpected_type — PartsEx вернул {raw.GetType().Name}, " +
                              "а не SAFEARRAY/VT_DISPATCH: структура не прочитана");
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"parts_ex_failed — PartsEx бросил {ex.GetType().Name}: {ex.Message}");
        }

        return children;
    }

    private ComponentRowDto ReadComponent(
        DocumentEntry document, IPart7 parent, IPart7 part, string? parentRef, int depth,
        int ordinal, List<string> notes)
    {
        // Reference — диагностическое число, НЕ адрес (см. ComponentPayload). Непрочитанное значение
        // остаётся null, а не превращается в 0: «номер не прочитан» и «номер 0» — разные утверждения.
        var reference = Int(() => part.Reference);
        var addressable = ordinal != NestedComponentOrdinal;

        // МАТРИЦА СО СТОРОНЫ API7 — ВТОРОЙ ПРИЗНАК ТОЖДЕСТВА, а не украшение ответа. Читается
        // документированным IPart7.GetSummMatrix (ipart7_getsummmatrix.html): «суммарная матрица
        // преобразования координат», 16 элементов, 4×4. Именно она различает два экземпляра одной
        // детали там, где имя файла совпадает. Читается только у адресуемого (верхнего) компонента:
        // у вложенного адреса нет, и подтверждать нечего.
        var matrix7 = addressable ? SummMatrix7(parent, part) : null;

        var stored = References.Register(
            ComponentRefKind, document.Id, document.Revision,
            new ComponentPayload(part, reference, ordinal, matrix7));

        if (!addressable)
        {
            notes.Add($"nested_component_no_address — компонент «{Text(() => part.Name) ?? "?"}» " +
                      $"вложен (глубина {depth}): тело, грани и размещение НЕ читаются, потому что " +
                      "адрес API5 перечисляет компоненты плоско и у вложенного компонента его нет");
        }

        return new ComponentRowDto
        {
            ComponentRef = stored.Id,
            ParentRef = parentRef,
            Depth = depth,
            Name = Text(() => part.Name),
            Marking = Text(() => part.Marking),
            SourcePath = Text(() => part.FileName),
            IsDetail = Bool(() => part.Detail),
            // Кратность читается С РОДИТЕЛЯ: документация — «Count = iObject.InstanceCount(iPart7)»,
            // где iObject есть узел, содержащий вставки. ИЗМЕРЕНО 04.10.2026: чтение со САМОГО
            // компонента даёт 0 — то есть «счётчик не с той стороны» виден числом, а не молчанием.
            InstanceCount = InstanceCountOf(parent, part),
            ReferenceNumber = reference,
            Fixed = Bool(() => part.Fixed),
            LoadState = ReadEnumName(() => part.LoadState),
            // ТЕЛА И ГРАНИ КОМПОНЕНТА — документированным ksPart.BodyCollection() →
            // ksBody.FaceCollection(). Без этих чисел «компонент вставлен» неотличимо от «вставлен
            // пустой компонент»: именно так и было на маршруте CreatePartInAssembly.
            //
            // Читаются ТОЛЬКО у адресуемого (верхнего) компонента: у вложенного адреса нет, и
            // подставлять вместо него чужой номер запрещено — тогда «не прочитано» честно равно null.
            BodyCount = addressable ? BodyCountOf(ComponentPart5At(document, ordinal)) : null,
            FaceCount = addressable ? FaceCountOf(ComponentPart5At(document, ordinal)) : null,
            // РАЗМЕЩЕНИЕ ОТДАЁТСЯ СО СТОРОНЫ API7 (GetSummMatrix), а матрица API5-представления по
            // тому же номеру остаётся ВТОРЫМ, независимым чтением: их сверка и есть проверка тождества
            // адреса (см. IdentityMatches). Если матрица API7 не прочиталась, называется матрица API5
            // — непрочитанное значение не подменяется нулями.
            Matrix = addressable ? matrix7 ?? PlacementMatrixByOrdinal(document, ordinal) : null,
        };
    }

    /// <summary>
    /// Суммарная матрица преобразования координат компонента со стороны API7 — документированный
    /// <c>IPart7.GetSummMatrix(IPart7 Part1)</c>.
    /// </summary>
    /// <remarks>
    /// <c>ipart7_getsummmatrix.html</c> дословно: «GetSummMatrix — Получить суммарную матрицу
    /// преобразование координат»; «Элементы матрицы возвращаются в виде одномерного массива из
    /// шестнадцати элементов»; «Матрица имеет размер 4х4»; параметр <c>Part1</c> — «указатель на
    /// интерфейс IPart7 детали из которой нужно сделать пересчет координат». Вызывается на РОДИТЕЛЕ:
    /// для компонента верхнего уровня родитель — верхний компонент сборки, поэтому матрица
    /// получается в координатах документа и сопоставима с API5 <c>ksPart.GetPlacement().GetMatrix3D</c>.
    /// Непрочитанное значение — <c>null</c>, а не нули.
    /// </remarks>
    private static double[]? SummMatrix7(IPart7 parent, IPart7 child)
    {
        // Параметр документирован как «указатель на интерфейс IPart7», а поставленная обёртка
        // 24.0.0.2799 объявляет его конкретным классом Part7 — расхождение обёртки и справки, названное
        // здесь, а не сглаженное: если объект не приводится к Part7, матрица НЕ читается (null), и
        // вызывающий это увидит.
        if (child is not Part7 typedChild)
        {
            return null;
        }

        try
        {
            return Matrix16(parent.GetSummMatrix(typedChild));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Кратность компонента: <c>parent.InstanceCount(child)</c>. Индексированное свойство требует
    /// <see cref="Part7"/>; если объект к нему не приводится, кратность НЕ читается (null), а не
    /// подменяется единицей.
    /// </summary>
    private static int? InstanceCountOf(IPart7 parent, IPart7 child)
    {
        try
        {
            return parent is Part7 typedParent && child is Part7 typedChild
                ? parent.InstanceCount[typedChild]
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>API5-представление компонента по порядковому номеру (адрес измерен в C1).</summary>
    private static ksPart? ComponentPart5At(DocumentEntry document, int ordinal)
    {
        try
        {
            var parts = ComponentParts5(document);
            return ordinal >= 0 && ordinal < parts.Count ? parts[ordinal] : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Число тел компонента по документированному <c>ksPart.BodyCollection()</c>.</summary>
    private static int? BodyCountOf(ksPart? component)
    {
        try
        {
            return component?.BodyCollection() is ksBodyCollection bodies ? bodies.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Число граней первого тела компонента по <c>ksBody.FaceCollection()</c>.</summary>
    private static int? FaceCountOf(ksPart? component)
    {
        try
        {
            return component?.BodyCollection() is ksBodyCollection bodies
                && bodies.GetCount() > 0
                && bodies.GetByIndex(0) is ksBody body
                && body.FaceCollection() is ksFaceCollection faces
                    ? faces.GetCount()
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Матрица размещения компонента по его ПОРЯДКОВОМУ номеру в перечислении структуры.
    /// </summary>
    /// <remarks>
    /// Номер — адрес, потому что <c>IPart7.Reference</c> номером компонента не является (измерено
    /// 04.10.2026: 1073741857), а сопоставление по файлу-источнику неоднозначно при двух экземплярах
    /// одной детали. Порядок <c>ksPartCollection</c> сопоставляется с порядком <c>IPart7.PartsEx</c> —
    /// это ПРЕДПОЛОЖЕНИЕ, и различающий контроль (размещение одного экземпляра не меняет другой)
    /// его проверяет.
    /// </remarks>
    private double[]? PlacementMatrixByOrdinal(DocumentEntry document, int ordinal)
    {
        try
        {
            var parts = ComponentParts5(document);
            if (ordinal >= 0 && ordinal < parts.Count
                && parts[ordinal].GetPlacement() is ksPlacement placement
                && placement.GetMatrix3D(out var raw))
            {
                return Matrix16(raw);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Компоненты сборки по документированному массиву <c>PartCollection(refresh=true)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Перечисление — через <c>ksDocument3D.PartCollection(TRUE)</c>, а не перебором
    /// <c>GetPart(n)</c>.</b> Измерено 04.10.2026: <c>GetPart(1)</c> на сборке с ДВУМЯ компонентами
    /// вернул компонент ВТОРОГО источника, а <c>GetPart(2)</c> — null, то есть номером компонента
    /// <c>GetPart</c> не перечисляет. Документированный маршрут перечисления — динамический массив
    /// компонентов сборки: <c>PartCollection(refresh=true)</c> → <c>ksPartCollection</c>
    /// (<c>GetCount</c>/<c>GetByIndex</c>).
    /// </remarks>
    private static List<ksPart> ComponentParts5(DocumentEntry document)
    {
        var parts = new List<ksPart>();
        try
        {
            if (document.Document.PartCollection(true) is not ksPartCollection collection)
            {
                return parts;
            }

            var count = collection.GetCount();
            for (var index = 0; index < count; index++)
            {
                if (collection.GetByIndex(index) is ksPart part)
                {
                    parts.Add(part);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return parts;
        }

        return parts;
    }

    /// <summary>
    /// <c>ksPlacement</c> из жёсткого преобразования; при отсутствии преобразования — документированное
    /// умолчание документа (<c>ksDocument3D.DefaultPlacement()</c>).
    /// </summary>
    private object? BuildPlacement(DocumentEntry document, TransformDto? transform)
    {
        var placement = document.Document.DefaultPlacement();
        if (transform is null)
        {
            return placement;
        }

        if (placement is not ksPlacement typed)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ksDocument3D.DefaultPlacement() не вернул ksPlacement: размещение этим маршрутом не " +
                "задаётся.",
                RetryPolicy.Never);
        }

        typed.InitByMatrix3D(ToVariant(MatrixOf(transform)));
        return typed;
    }

    // ===================================================================================== ASM-02
    /// <summary>
    /// Вставка компонента из файла. Маршрут — документированный API7
    /// <c>IPart7.Parts → IParts7.AddFromFile(FileName, ExternalFile=true, Redraw=true) → Part7</c>
    /// (<c>iparts7_addfromfile.html</c>), дающий компоненты С ГЕОМЕТРИЕЙ.
    /// </summary>
    /// <remarks>
    /// Прежний маршрут <c>ksDocument3D.CreatePartInAssembly</c> отвергнут как ИЗМЕРЕННО неверный:
    /// справка называет его «деталь СОЗДАВАЕМАЯ в сборке», «плоскость, к которой ПРИКЛЕИВАЕТСЯ», и
    /// живым прогоном 05.10.2026 он давал компонент с 0 тел и 0 граней. Размещение и фиксация
    /// пишутся через API5 <c>ksPart</c>, поэтому вставленный экземпляр адресуется в плоском
    /// документированном массиве <c>ksDocument3D.PartCollection(true)</c>.
    /// </remarks>
    public InsertComponentResult InsertComponent(InsertComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        // Снимок ДО вставки: и число компонентов, и их источники. По ним вставленный экземпляр
        // подтверждается как ЕДИНСТВЕННЫЙ новый, а не «последний в списке».
        var beforeParts = ComponentParts5(document);
        var beforeOrdinals = beforeParts.Count;
        var beforeNames = beforeParts.Select(p => Ref(() => p.fileName)).ToList();

        // ВСТАВКА КОМПОНЕНТА — ДОКУМЕНТИРОВАННЫЙ IParts7.AddFromFile.
        //
        // ИЗМЕРЕНО 05.10.2026 живым прогоном (проба M, tools/KompasMcp.Api7Probe, флаг --mate):
        // прежний маршрут CreatePartInAssembly давал компонент БЕЗ ГЕОМЕТРИИ — 0 тел, 0 граней.
        // Справка объясняет это дословно: ksdDocument3d_createpartinassembly.html — «fileName — имя
        // файла детали СОЗДАВАЕМОЙ в сборке», «plane — плоскость, к которой ПРИКЛЕИВАЕТСЯ деталь»,
        // то есть это СОЗДАНИЕ новой (пустой) детали, а НЕ вставка существующей.
        //
        // Документированная ВСТАВКА — iparts7_addfromfile.html: IPart7.Parts → IParts7.AddFromFile
        // (FileName, ExternalFile, Redraw) → Part7, где «FileName — имя файла, из которого будет
        // ВСТАВЛЕН компонент», «ExternalFile — TRUE — вставка СО ССЫЛКОЙ на внешний файл»,
        // «Redraw — признак перестроения документа после вставки». Замерено на том же прогоне:
        // компонентов 2, у КАЖДОГО тело = 1 и граней 6, и геометрия переживает save→close→reopen.
        //
        // Повторная вставка того же файла идёт ТЕМ ЖЕ вызовом: отдельный CopyPart больше не нужен
        // (он был следствием ошибочного маршрута).
        var bridge = BridgeFor(document);
        if (bridge.TransferTo7(document.Document) is not IKompasDocument3D document7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ-сборка не переносится в API7 как IKompasDocument3D: коллекция компонентов " +
                "IPart7.Parts недостижима, вставка не выполнена.",
                RetryPolicy.ReacquireContext);
        }

        var top7 = document7.TopPart;
        var parts7 = top7?.Parts;
        if (parts7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.Parts не вернул IParts7: вставлять нечем, компонент не создан.",
                RetryPolicy.ReacquireContext);
        }

        Part7? inserted7;
        try
        {
            inserted7 = parts7.AddFromFile(command.SourcePath, ExternalFile: true, Redraw: true);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"IParts7.AddFromFile прервался: {ex.Message}. Компонент не вставлен.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        if (inserted7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IParts7.AddFromFile вернул null: компонент не вставлен. Файл-источник и тип сборки " +
                "не меняются; повтор с тем же operation_id допустим после проверки файла.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = command.SourcePath });
        }

        // АДРЕС ВСТАВЛЕННОГО ЭКЗЕМПЛЯРА — ПОДТВЕРЖДЁННЫЙ, А НЕ «ПОСЛЕДНИЙ В КОЛЛЕКЦИИ».
        //
        // `ksDocument3D.PartCollection(true)` (ksdocument3d_partcollection.html) перечисляет
        // компоненты сборки, а вставка добавляет РОВНО ОДИН. Новый номер подтверждается ДВУМЯ
        // различающими признаками СРАЗУ: (1) источник нового компонента совпал с запрошенным;
        // (2) сосед по прежнему номеру сохранил СВОЙ прежний источник. Одного «последний» мало: при
        // не-добавляющем порядке это адресовало бы чужой компонент, а сопоставление по имени файла
        // неоднозначно при двух экземплярах одной детали.
        var afterParts = ComponentParts5(document);
        ksPart? createdPart = null;
        var addressNote = $"после вставки компонентов {afterParts.Count}, ожидалось {beforeOrdinals + 1}";
        if (afterParts.Count == beforeOrdinals + 1)
        {
            var candidate = afterParts[beforeOrdinals];
            var candidateName = Ref(() => candidate.fileName);
            var sourceMatches = !string.IsNullOrEmpty(candidateName)
                && string.Equals(Path.GetFileName(candidateName), Path.GetFileName(command.SourcePath),
                    StringComparison.OrdinalIgnoreCase);
            var neighborPreserved = beforeOrdinals == 0 || string.Equals(
                Path.GetFileName(Ref(() => afterParts[beforeOrdinals - 1].fileName) ?? string.Empty),
                Path.GetFileName(beforeNames[beforeOrdinals - 1] ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
            if (sourceMatches && neighborPreserved)
            {
                createdPart = candidate;
                addressNote = $"PartCollection.GetByIndex({beforeOrdinals}) — единственный новый " +
                              "компонент; источник совпал с запрошенным, сосед сохранил прежний источник";
            }
            else
            {
                addressNote = $"номер {beforeOrdinals}: источник='{candidateName ?? "не читается"}', " +
                              $"совпадение={sourceMatches}, сосед сохранён={neighborPreserved}";
            }
        }

        // ОБЯЗАТЕЛЬНЫЕ ПАРАМЕТРЫ ЗАПРОСА ОБЯЗАНЫ БЫТЬ ПРИМЕНЕНЫ, А НЕ ПРОГЛОЧЕНЫ.
        //
        // Если компонент вставлен, но API5-представление не получено, заданные размещение и фиксация
        // применить НЕКУДА. Прежняя редакция молча пропускала их и возвращала обычный результат —
        // то есть «вставлено по запросу» было ложью. Теперь это именованный отказ с частичными
        // эффектами: вставка УЖЕ произошла, и об этом нельзя сообщать «компонент не создан», а
        // повторять вставку для исправления размещения запрещено.
        if (createdPart is null)
        {
            // ЧАСТИЧНЫЙ ЭФФЕКТ ПОДНИМАЕТ РЕВИЗИЮ И ОТЗЫВАЕТ ССЫЛКИ.
            //
            // Компонент уже вставлен: модель изменилась, а ревизия и ссылки остались прежними.
            // Прежде следующий вызов со старым expected_revision принимался, а старые ссылки
            // считались валидными — то есть мутация на модели, которая уже другая (дефект M4 ревью
            // 05.10.2026).
            BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);

            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Компонент ВСТАВЛЕН (компонентов сборки {beforeOrdinals} → {afterParts.Count}), но " +
                $"API5-представление вставленного экземпляра не получено ({addressNote}): размещение " +
                "и фиксация НЕ применены. Это ЧАСТИЧНЫЙ ЭФФЕКТ, а не «компонент не создан»; повторная " +
                "вставка для исправления размещения запрещена — сверьте модель и работайте со " +
                "структурой.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["source_path"] = command.SourcePath,
                    ["components_before"] = beforeOrdinals,
                    ["components_after"] = afterParts.Count,
                    ["address_note"] = addressNote,
                });
        }

        // Размещение: заданное преобразование применяется к вставленному компоненту и ПЕРЕЧИТЫВАЕТСЯ.
        // «Записано» подтверждается совпадением перечитанного переноса с запрошенным, а не фактом
        // вызова: измеренный урок MATE — успешный вызов не доказывает результат.
        var placementApplied = command.Transform is null;
        double[]? placementAfter = null;
        if (command.Transform is not null)
        {
            try
            {
                if (BuildPlacement(document, command.Transform) is { } placement)
                {
                    createdPart.SetPlacement(placement);
                    createdPart.UpdatePlacement();
                }
            }
            catch (COMException ex)
            {
                BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Компонент вставлен, но размещение не записалось: {ex.Message}. Размещение не " +
                    "подтверждено.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }

            var wanted = MatrixOf(command.Transform);
            placementAfter = ReadPlacementMatrix(createdPart);
            placementApplied = MatrixMatchesRequest(placementAfter, wanted);
        }

        // Параметр Fixed обязан быть ПРИМЕНЁН и ПЕРЕЧИТАН: «объявлено и проглочено» — тот же дефект,
        // что «не поддержано, но обещано». Чтение — документированным IPart7.Fixed у ТОГО ЖЕ
        // вставленного экземпляра; непрочитанное значение (null) НЕ считается совпадением.
        bool fixedApplied;
        bool? fixedAfter;
        try
        {
            createdPart.fixedComponent = command.Fixed;
            fixedAfter = Bool(() => ((IPart7)inserted7).Fixed);
        }
        catch (COMException ex)
        {
            BumpRevision(document, "assembly.insert_component.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Компонент вставлен, но фиксация (fixedComponent={command.Fixed}) не записалась: " +
                $"{ex.Message}. Состояние фиксации не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        fixedApplied = fixedAfter == command.Fixed;

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.insert_component");

        // ПРОВАЛ ОБЯЗАТЕЛЬНОЙ ПРОВЕРКИ — ЭТО НЕ УСПЕХ.
        //
        // Прежняя редакция возвращала результат со статусом «выполнено» и уровнем CallReturned при
        // `placement_applied=false` или `fixed_applied=false`: клиент, читающий только `status`,
        // принимал неприменённое размещение за выполненное. Наряд прямо требует отсутствия успеха
        // при неприменённых параметрах (дефект M5 ревью 05.10.2026). Ревизия к этому моменту уже
        // поднята — модель изменилась, и скрыть это нельзя.
        if (!placementApplied || !fixedApplied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Компонент вставлен, но ЗАПРОШЕННЫЕ ПАРАМЕТРЫ НЕ ПОДТВЕРЖДЕНЫ: "
                + (placementApplied ? string.Empty : $"размещение перечитано как «{Describe(placementAfter)}» и не совпало с запрошенным; ")
                + (fixedApplied ? string.Empty : $"фиксация перечитана как «{(fixedAfter is null ? "не читается" : fixedAfter.Value.ToString())}» вместо «{command.Fixed}»; ")
                + "успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["placement_applied"] = placementApplied,
                    ["fixed_applied"] = fixedApplied,
                    ["fixed_requested"] = command.Fixed,
                    ["fixed_read_back"] = fixedAfter,
                    ["placement_read_back"] = placementAfter,
                });
        }

        var after = CountComponents(document);
        var notes = new List<string>();

        // Строка компонента строится ПО ВСТАВЛЕННОМУ ЭКЗЕМПЛЯРУ (inserted7), а не поиском по имени
        // файла: при двух экземплярах одной детали поиск по имени вернул бы не тот экземпляр.
        var inserted = top7 is null
            ? null
            : ReadComponent(document, top7, (IPart7)inserted7, parentRef: null, depth: 0,
                ordinal: beforeOrdinals, notes);

        if (inserted is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Компонент вставлен, но перечитать его в структуре не удалось: адресовать вставку " +
                "нечем. Это не «успех по умолчанию» — результат не подтверждён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["component_count"] = after });
        }

        var payload = (ComponentPayload)References
            .Require(inserted.ComponentRef, document.Id, document.Revision).Payload!;
        var componentRef = ToDto(
            References.Require(inserted.ComponentRef, document.Id, document.Revision),
            $"component {inserted.Name ?? "?"}");

        var checks = new List<NamedCheck>
        {
            new("component_count_increased", afterParts.Count == beforeOrdinals + 1,
                Observed: $"{beforeOrdinals} → {afterParts.Count}", Expected: $"{beforeOrdinals + 1}"),
            new("inserted_component_read_back", true,
                Observed: $"«{inserted.Name ?? "?"}», источник «{inserted.SourcePath ?? "?"}»"),
            new("inserted_address_confirmed", true, Observed: addressNote),
            new("placement_applied", placementApplied,
                Observed: command.Transform is null ? "размещение не задавалось (Transform=null)"
                    : Describe(placementAfter),
                Expected: command.Transform is null ? "—" : "совпадение переноса с запросом"),
            new("fixed_applied", fixedApplied,
                Observed: fixedAfter is null ? "фиксация не прочитана" : fixedAfter.Value.ToString(),
                Expected: command.Fixed.ToString()),
        };

        var unverified = new List<string>();
        if (command.Transform is null)
        {
            unverified.Add("placement_not_applied — размещение при вставке НЕ задавалось " +
                           "(Transform=null в запросе): компонент стоит по умолчанию КОМПАСа");
        }
        else if (!placementApplied)
        {
            unverified.Add("placement_not_read_back — перечитанное размещение не совпало с заданным: " +
                           "запись не подтверждена");
        }

        if (!fixedApplied)
        {
            unverified.Add("fixed_not_read_back — перечитанный признак фиксации не совпал с заданным " +
                           "или не прочитан");
        }

        unverified.Add("reference_numbering_unverified — IPart7.Reference — ДИАГНОСТИЧЕСКОЕ число, а не " +
                       "адрес: адресация идёт по номеру в ksDocument3D.PartCollection(true) и " +
                       "подтверждается различающим контролем при вставке");

        if (payload.Reference is null or 0)
        {
            unverified.Add("component_reference_unread — IPart7.Reference не прочитан (или равен 0): " +
                           "диагностический номер компонента недоступен. Адресация на него не " +
                           "опирается, но и подтвердить его нельзя");
        }

        return new InsertComponentResult(
            componentRef,
            inserted,
            after,
            new VerificationDto(
                placementApplied && fixedApplied ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-04
    /// <summary>
    /// Задать размещение компонента жёстким преобразованием и перечитать его. Запись — API5
    /// <c>ksPlacement.InitByMatrix3D</c> + <c>ksPart.SetPlacement</c> + <c>ksPart.UpdatePlacement</c>
    /// (единственная документированная абсолютная запись размещения).
    /// </summary>
    public SetComponentPlacementResult SetComponentPlacement(SetComponentPlacementCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var matrix = MatrixOf(command.Transform);

        // НЕПРОЧИТАННАЯ МАТРИЦА — ЭТО НЕ НУЛЕВАЯ МАТРИЦА. Прежняя редакция подставляла
        // `Array.Empty<double>()`, то есть «размещение нулевое», там, где размещение просто не
        // прочиталось (дефект M6 ревью 05.10.2026, тот же класс, что исправление C).
        var beforeMatrix = ReadPlacementMatrix(part5);

        bool written;
        try
        {
            if (part5.GetPlacement() is not ksPlacement placement)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "ksPart.GetPlacement() не вернул ksPlacement: размещение компонента этим " +
                    "маршрутом не задаётся.",
                    RetryPolicy.Never);
            }

            placement.InitByMatrix3D(ToVariant(matrix));
            written = part5.SetPlacement(placement);
            part5.UpdatePlacement();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись размещения прервалась: {ex.Message}. Размещение могло измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.set_placement");

        var afterMatrix = ReadPlacementMatrix(part5);
        var matched = MatrixMatchesRequest(afterMatrix, matrix);
        var checks = new List<NamedCheck>
        {
            new("set_placement_returned", written, Observed: written ? "true" : "false"),
            new("placement_read_back_matches_request", matched,
                Observed: Describe(afterMatrix), Expected: Describe(matrix)),
        };

        // ПРОВАЛ ОБЯЗАТЕЛЬНОЙ ПРОВЕРКИ — ЭТО НЕ УСПЕХ: перечитанное размещение не совпало с
        // запрошенным (или не прочитано), и результат «выполнено» был бы ложью (дефект M5).
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Размещение записано, но ПЕРЕЧИТАНОЕ размещение не совпало с запрошенным: "
                + $"запрос «{Describe(matrix)}», перечитано «{Describe(afterMatrix)}». Успехом это не " +
                "считается: модель изменена, размещение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["placement_before"] = beforeMatrix,
                    ["placement_after"] = afterMatrix,
                    ["placement_requested"] = matrix,
                });
        }

        var unverified = new List<string>
        {
            "component_address_by_ordinal — " + lookupNote,
        };
        if (!matched)
        {
            unverified.Insert(0, "placement_not_read_back — перечитанное размещение не совпало с " +
                                 "заданным: запись не подтверждена");
        }

        return new SetComponentPlacementResult(
            ToDto(stored, "component"),
            beforeMatrix,
            afterMatrix,
            new VerificationDto(matched ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-05
    /// <summary>Заменить источник компонента с сохранением размещения.</summary>
    public ReplaceComponentResult ReplaceComponent(ReplaceComponentCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        RequireSourceFile(command.SourcePath);

        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5(document, payload, out var lookupNote);
        var sourceBefore = Ref(() => part5.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixBefore = ReadPlacementMatrix(part5);
        var countBefore = CountComponents(document);

        // ЗАМЕНА ИСТОЧНИКА — ДОКУМЕНТИРОВАННЫЙ ksPart.SetFileName.
        //
        // kspart_setfilename.html, примечания ДОСЛОВНО:
        //   1. «Метод используется для компонентов, вставленных в сборку» — ровно наш случай;
        //   2. «Документ с указанным именем должен существовать» — проверено RequireSourceFile;
        //   3. «Компонент не должен быть деталью из библиотеки моделей или стандартным элементом»;
        //   4. «ИЗМЕНЕНИЕ ВСТУПАЕТ В СИЛУ ПОСЛЕ ВЫЗОВА МЕТОДА ksPart::Update» — поэтому Update ниже.
        // Метод возвращает BOOL, и возврат ПРОВЕРЯЕТСЯ: прежняя редакция присваивала свойство
        // (ipart7_filename.html: «вступает в силу после IModelObject::Update») и о результате
        // молчала. Измерено 04.10.2026: одного API7-сеттера НЕ хватало — замена не переживала
        // save→close→reopen.
        // РАСХОЖДЕНИЕ ОБЁРТКИ И СПРАВКИ, НАЗВАННОЕ: справка kspart_setfilename.html обещает метод
        // BOOL ksPart::SetFileName(BSTR), а поставленная обёртка 24.0.0.2799 объявляет только
        // СВОЙСТВО (kspart_filename.html: «fileName = iPart.fileName / iPart.fileName = fileName»),
        // возврата у него нет. Поэтому результат проверяется ЧТЕНИЕМ ОБРАТНО документированным
        // геттером ksPart.fileName: молчаливая запись была бы тем же дефектом, что непроверенный
        // возврат.
        try
        {
            part5.fileName = command.SourcePath;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись ksPart.fileName прервалась: {ex.Message}. Ссылка могла измениться частично.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        // Примечание 4 справки: изменение вступает в силу ПОСЛЕ Update().
        part5.Update();

        var replacedPath = Ref(() => part5.fileName);
        if (!string.Equals(
                Path.GetFileName(replacedPath ?? string.Empty),
                Path.GetFileName(command.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // ЭТО НЕ «ЧИСТЫЙ ОТКАЗ», ХОТЯ ПЕРЕЧИТАННОЕ ИМЯ И НЕ СОВПАЛО.
            //
            // Прежняя редакция отвечала `RetryPolicy.SameOperationId` без `partialEffects`, и журнал
            // считал отказ чистым (`IsCleanFailure`) — то есть разрешал повтор ТЕМ ЖЕ operation_id. Но
            // к этому моменту уже выполнены `ksPart.fileName = …` и `ksPart.Update()`: перечитанное имя
            // — КОСВЕННЫЙ признак, а не доказательство, что модель не изменилась. Повтор тем же id
            // применил бы замену ВТОРОЙ раз (находка §5 задания 05.10.2026). Документированного
            // утверждения, что после неуспешной записи `fileName` `Update()` модель не меняет, в
            // справке нет — поэтому отказ объявляется частичным эффектом, ревизия поднимается, и
            // повтор требует согласования.
            BumpRevision(document, "assembly.replace_component.partial", invalidateAll: true);

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Источник компонента НЕ заменён: после записи и Update() перечитывается " +
                $"«{replacedPath ?? "null"}» вместо «{command.SourcePath}». Запись fileName и Update() " +
                "уже вызваны, поэтому исход считается ЧАСТИЧНЫМ ЭФФЕКТОМ, а не чистым отказом: " +
                "повтор с тем же operation_id без согласования недопустим. Причины по справке: документа " +
                "с указанным именем нет, либо компонент — деталь из библиотеки моделей или стандартный " +
                "элемент.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = command.ComponentRef,
                    ["source_path"] = command.SourcePath,
                    ["read_back"] = replacedPath,
                    ["update_called"] = true,
                    ["revision_bumped"] = true,
                });
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "assembly.replace_component");

        // ПЕРЕЧИТЫВАЕТСЯ ТОТ ЖЕ ЭКЗЕМПЛЯР — ПО ТОМУ ЖЕ НОМЕРУ, А НЕ «ПЕРВЫЙ С ТЕМ ЖЕ ФАЙЛОМ».
        //
        // Прежняя редакция искала `FindPart5BySource(новый источник)`: если в сборке УЖЕ был
        // экземпляр нового источника, `sourceAfter` и `matrixAfter` читались с ДРУГОГО экземпляра, и
        // `placement_preserved` ложно проваливался или ложно проходил. Это ровно тот случай двух
        // экземпляров одной детали, ради которого исправление B ушло от поиска по имени (дефект M8
        // ревью 05.10.2026).
        var part5After = ComponentPart5At(document, payload.Ordinal);
        var sourceAfter = Ref(() => part5After?.fileName) ?? Text(() => payload.Part7.FileName);
        var matrixAfter = part5After is not null
            ? ReadPlacementMatrix(part5After)
            : ReadPlacementMatrix(part5);
        var countAfter = CountComponents(document);

        var placementPreserved = matrixBefore is not null && matrixAfter is not null
                                 && MatricesEqual(matrixBefore, matrixAfter);
        var sourceChanged = !string.Equals(sourceBefore, sourceAfter, StringComparison.OrdinalIgnoreCase);

        var checks = new List<NamedCheck>
        {
            new("source_changed", sourceChanged,
                Observed: sourceAfter ?? "не читается", Expected: command.SourcePath),
            new("placement_preserved", placementPreserved,
                Observed: Describe(matrixAfter), Expected: Describe(matrixBefore)),
            new("component_count_unchanged", countAfter == countBefore,
                Observed: $"{countBefore} → {countAfter}", Expected: $"{countBefore}"),
        };

        var unverified = new List<string>
        {
            "component_address_by_ordinal — " + lookupNote,
        };
        if (!placementPreserved)
        {
            unverified.Insert(0, "placement_not_preserved — размещение до и после не совпало " +
                                 "(или не читается): сохранение размещения не подтверждено");
        }

        var structural = sourceChanged && countAfter == countBefore;
        return new ReplaceComponentResult(
            ToDto(stored, "component"),
            sourceBefore,
            sourceAfter,
            matrixBefore,
            matrixAfter,
            countAfter,
            new VerificationDto(
                structural && placementPreserved ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks, unverified));
    }

    // ===================================================================================== ASM-06
    /// <summary>Проверка ссылок компонентов на файлы-источники.</summary>
    public CheckComponentLinksResult CheckComponentLinks(CheckComponentLinksCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var links = new List<ComponentLinkDto>();

        var top = TopPart7(document, notes);
        if (top is not null)
        {
            CollectLinks(document, top, links, notes);
        }

        var broken = links.Count(l => l.SourceExists == false);
        var unverified = new List<string>();
        if (links.Count == 0)
        {
            unverified.Add("no_components — в сборке не перечислено ни одного компонента: проверять нечего");
        }

        unverified.Add("broken_link_shape_unmeasured — чем именно выглядит битая ссылка (LoadState, " +
                       "отказ Load или пустой FileName) не измерено: вердикт опирается на наличие " +
                       "файла по пути, а не на состояние загрузки КОМПАСа");

        var checks = new List<NamedCheck>
        {
            new("links_enumerated", links.Count > 0, Observed: links.Count.ToString(CultureInfo.InvariantCulture)),
            new("broken_named", true, Observed: broken.ToString(CultureInfo.InvariantCulture)),
        };

        return new CheckComponentLinksResult(
            links, broken,
            new VerificationDto(links.Count > 0 ? VerificationLevel.StructureChecked
                : VerificationLevel.CallReturned, checks, unverified));
    }

    private void CollectLinks(
        DocumentEntry document, IPart7 node, List<ComponentLinkDto> links, List<string> notes)
    {
        foreach (var child in ChildrenOf(node, notes))
        {
            var path = Text(() => child.FileName);
            var exists = path is { Length: > 0 } && SafeFileExists(path);
            var stored = References.Register(
                ComponentRefKind, document.Id, document.Revision,
                new ComponentPayload(child, Int(() => child.Reference), links.Count, Matrix7: null));
            links.Add(new ComponentLinkDto(
                stored.Id,
                Text(() => child.Name),
                path,
                path is { Length: > 0 } ? exists : null,
                ReadEnumName(() => child.LoadState),
                path is not { Length: > 0 } ? "источник не назван (FileName пуст)"
                    : exists ? "источник на месте"
                    : "ИСТОЧНИК ОТСУТСТВУЕТ — ссылка битая"));
        }
    }

    // ===================================================================================== помощники
    private DocumentEntry RequireAssembly(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Документ «{documentId}» имеет тип {document.Kind}: команды сборки применимы только " +
                "к документу-сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                });
        }

        return document;
    }

    private void RequireRevision(DocumentEntry document, long expected)
    {
        if (expected != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Ожидалась ревизия {expected}, текущая {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["expected_revision"] = expected,
                    ["current_revision"] = document.Revision,
                });
        }
    }

    private void RequireSourceFile(string path)
    {
        if (!SafeFileExists(path))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Файл-источник не найден: {path}",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["source_path"] = path });
        }
    }

    private IPart7? TopPart7(DocumentEntry document, List<string> notes)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is IPart7 part7)
        {
            return part7;
        }

        notes.Add("api7_bridge_unavailable — " + (bridge.BridgeFailure ?? "причина не известна") +
                  ": структура сборки не прочитана");
        return null;
    }

    /// <summary>
    /// API5-представление компонента для записи размещения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ИЗМЕРЕНО 04.10.2026 живым прогоном и опровергло мост по номеру <c>IPart7.Reference</c>.</b>
    /// Ожидалось, что это номер компонента для <c>ksPart.GetPart</c>. На сборке с одним компонентом
    /// он равен <b>1073741857</b> (0x40000001) — это не номер: <c>GetPart</c> по нему вернул не
    /// <c>ksPart</c>. Поэтому адресом служит ПОРЯДКОВЫЙ НОМЕР компонента в плоской
    /// <c>ksDocument3D.PartCollection(true)</c>. (Прежняя редакция этого комментария описывала
    /// перебор номеров с сопоставлением по файлу и отказ при нескольких совпадениях — маршрута,
    /// которого в коде больше нет; описание устарело и заменено на действительное, дефект L4 ревью
    /// 05.10.2026.)
    /// </para>
    /// <para>
    /// Порядок <c>ksPartCollection</c> сопоставляется с порядком <c>IPart7.PartsEx</c> — это
    /// ПРЕДПОЛОЖЕНИЕ, поэтому перед мутацией тождество сверяется (<see cref="IdentityMatches"/>):
    /// номер, ведущий в компонент с другим файлом-источником, отказывает, а не применяется.
    /// </para>
    /// </remarks>
    private ksPart ComponentPart5(DocumentEntry document, ComponentPayload payload, out string note)
    {
        if (payload.Ordinal == NestedComponentOrdinal)
        {
            // ВЛОЖЕННЫЙ КОМПОНЕНТ: адреса нет. Мутация «по предположительному номеру» запрещена —
            // она попала бы в ЧУЖОЙ компонент, потому что PartCollection перечисляет компоненты
            // плоско. Это ограничение выпуска, названное, а не обойдённое.
            note = "вложенный компонент: адрес API5 неприменим (PartCollection перечисляет компоненты плоско)";
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Компонент вложен в подсборку, и адресовать его API5-маршрутом нельзя: " +
                "ksDocument3D.PartCollection(true) перечисляет компоненты сборки ПЛОСКО, поэтому " +
                "порядковый номер вложенного компонента адресом не является. Размещение и замена " +
                "вложенного компонента этим выпуском не поддерживаются; работайте с компонентом " +
                "верхнего уровня.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["api7_reference"] = payload.Reference,
                    ["ordinal"] = payload.Ordinal,
                    ["supported"] = "компоненты верхнего уровня (Depth=0)",
                });
        }

        var parts = ComponentParts5(document);
        if (payload.Ordinal >= 0 && payload.Ordinal < parts.Count)
        {
            var candidate = parts[payload.Ordinal];
            note = $"ksPartCollection.GetByIndex({payload.Ordinal}) — адрес по порядковому номеру " +
                   $"(IPart7.Reference={payload.Reference} номером компонента НЕ является; " +
                   "сопоставление порядков API7↔API5 проверено различающим контролем)";

            // ТОЖДЕСТВО ПРОВЕРЯЕТСЯ ДО МУТАЦИИ, И ОТКАЗ ИДЁТ И НА «НЕ СВЕРЕНО».
            //
            // Порядковый номер из `IPart7.PartsEx` применяется как индекс в плоской
            // `PartCollection(true)` — это ПРЕДПОЛОЖЕНИЕ, которое сам код и называет. Если в плоский
            // список попадают вложенные компоненты, подсборка перед деталью сдвигает индексы, и
            // размещение или замена адресовали бы ЧУЖОЙ компонент; различающий контроль прежней
            // приёмки шёл на плоской сборке и этого не проверял (дефект M7 ревью 05.10.2026).
            // Сверка идёт по ТРЁМ признакам (источник, имя, матрица размещения), и:
            //   * расхождение хоть одного признака → отказ STALE_REFERENCE;
            //   * «сверить нечем» (ни один признак не прочитан) → ТОЖЕ ОТКАЗ, без мутации. Прежде
            //     нечитаемость продолжалась с примечанием, то есть мутация по НЕПОДТВЕРЖДЁННОМУ адресу
            //     была разрешена — ровно то, что задание запрещает.
            var identity = IdentityMatches(candidate, payload, out var identityNote);
            note += "; " + identityNote;
            if (identity != true)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    identity == false
                        ? "Адрес компонента НЕ подтверждён: по порядковому номеру "
                          + $"{payload.Ordinal} в ksDocument3D.PartCollection(true) лежит ДРУГОЙ " +
                          "компонент. Мутация по неподтверждённому номеру попала бы в ЧУЖОЙ компонент, " +
                          "поэтому она не выполняется. Перечитайте структуру сборки " +
                          "kompas_list_components и возьмите свежую ссылку."
                        : "Адрес компонента НЕ подтверждён: тождество не удалось сверить ни по одному " +
                          "из признаков (источник, имя, матрица размещения). Мутация по " +
                          "НЕПОДТВЕРЖДЁННОМУ адресу не выполняется — перечитайте структуру сборки " +
                          "kompas_list_components и возьмите свежую ссылку.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["ordinal"] = payload.Ordinal,
                        ["identity_check"] = identityNote,
                        ["identity_confirmed"] = identity is null ? "не сверено (ни один признак не прочитан)" : "расхождение",
                        ["api7_reference"] = payload.Reference,
                    });
            }

            return candidate;
        }

        // Диагностика: что вообще отдаёт PartCollection. Без неё «не найден» неотличимо от
        // «перечисление пустое», и следующая правка снова угадывала бы.
        var sample = parts
            .Select(p => $"name='{Ref(() => p.name)}' file='{Ref(() => p.fileName)}'").ToList();
        note = $"порядковый номер {payload.Ordinal} вне PartCollection(true) " +
               $"(всего компонентов {sample.Count}: {string.Join(" | ", sample)})";
        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"API5-представление компонента не получено ({note}): размещение этим маршрутом не " +
            "задаётся.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["api7_reference"] = payload.Reference,
                ["ordinal"] = payload.Ordinal,
                ["components"] = sample.Count,
            });
    }

    /// <summary>
    /// Тот ли это компонент: сверяются ТРИ независимых признака, а не один.
    /// </summary>
    /// <returns>
    /// <c>true</c> — тождество подтверждено (хотя бы один признак прочитан и ни один не противоречит);
    /// <c>false</c> — адрес ведёт в ЧУЖОЙ компонент (признак прочитан и противоречит);
    /// <c>null</c> — сверить НЕЧЕМ (ни один признак не прочитан с обеих сторон).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Почему одного имени файла мало.</b> Сравнение только имени файла слабо ровно в том случае,
    /// ради которого адресация по имени и была отвергнута: подсборка содержит <c>plate.m3d</c>, и на
    /// верхнем уровне стоит <c>plate.m3d</c> — имена совпадают, а экземпляры разные. Поэтому тождество
    /// подтверждается ВТОРЫМ независимым признаком — матрицей размещения, прочитанной со стороны API7
    /// документированным <c>IPart7.GetSummMatrix</c> и сверенной с API5-матрицей по тому же номеру.
    /// </para>
    /// <para>
    /// <b>Агрегация — по противоречию.</b> Любой прочитанный признак, который РАСХОДИТСЯ, делает
    /// тождество ложным: расхождение — это факт о том, что номер ведёт не туда. Совпадения лишь
    /// подтверждают. Если не прочитан НИ ОДИН признак, ответ — <c>null</c> («сверять нечем»), и
    /// вызывающий обязан отказать, а не продолжить с примечанием (находка M7 ревью 05.10.2026).
    /// </para>
    /// <para>
    /// Сравниваются ИМЕНА файлов, а не полные пути: API7 и API5 отдают путь в разном виде (полный
    /// путь против имени файла), и сравнение полных путей дало бы ложное расхождение там, где
    /// компонент тот же.
    /// </para>
    /// </remarks>
    private bool? IdentityMatches(ksPart part5, ComponentPayload payload, out string detail)
    {
        var from5 = Ref(() => part5.fileName);
        var from7 = Text(() => payload.Part7.FileName);
        var name5 = string.IsNullOrWhiteSpace(from5) ? null : Path.GetFileName(from5);
        var name7 = string.IsNullOrWhiteSpace(from7) ? null : Path.GetFileName(from7);

        // Второй признак — ИМЯ КОМПОНЕНТА В ДЕРЕВЕ (документировано с обеих сторон: ksPart.name и
        // IPart7.Name). Он назван в ответе, но отказом управляет только вместе с матрицей: сравнение
        // имён API5/API7 живьём не измерялось, и делать его единственным основанием отказа значило бы
        // поставить работу на непроверенное совпадение.
        var componentName5 = Ref(() => part5.name);
        var componentName7 = Text(() => payload.Part7.Name);

        // Третий признак (решающий) — МАТРИЦА РАЗМЕЩЕНИЯ: API5-представление по тому же номеру против
        // API7 GetSummMatrix, снятой при перечислении структуры.
        var matrix5 = ReadPlacementMatrix(part5);
        var matrix7 = payload.Matrix7;

        var signals = new List<string>();
        var confirmed = false;
        var contradicted = false;

        if (name5 is not null && name7 is not null)
        {
            var equal = string.Equals(name5, name7, StringComparison.OrdinalIgnoreCase);
            signals.Add(equal
                ? $"источник «{name5}» совпал"
                : $"источник РАСХОДИТСЯ: по номеру «{name5}», ссылка адресует «{name7}»");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("источник не читается с одной из сторон");
        }

        if (!string.IsNullOrWhiteSpace(componentName5) && !string.IsNullOrWhiteSpace(componentName7))
        {
            var equal = string.Equals(componentName5, componentName7, StringComparison.Ordinal);
            signals.Add(equal
                ? $"имя компонента «{componentName5}» совпало"
                : $"имя компонента РАСХОДИТСЯ: по номеру «{componentName5}», ссылка адресует «{componentName7}»");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("имя компонента не читается с одной из сторон");
        }

        if (matrix5 is not null && matrix7 is not null)
        {
            var equal = MatricesEqual(matrix5, matrix7);
            signals.Add(equal
                ? "матрица размещения (API5 по номеру против API7 GetSummMatrix) совпала"
                : $"матрица размещения РАСХОДИТСЯ: по номеру {Describe(matrix5)}, ссылка адресует {Describe(matrix7)}");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("матрица размещения не читается с одной из сторон");
        }

        detail = string.Join("; ", signals);
        if (contradicted)
        {
            return false;
        }

        return confirmed ? true : null;
    }

    private double[]? ReadPlacementMatrix(ksPart part)
    {
        try
        {
            return part.GetPlacement() is ksPlacement placement && placement.GetMatrix3D(out var raw)
                ? Matrix16(raw)
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Матрица 4×4 из жёсткого преобразования (начало + две ортонормированные оси; Z = X × Y).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Раскладка — измеренная, не выбранная.</b> <see cref="RepositionMatrix"/> (Domain/Geometry)
    /// хранит ту же раскладку, что КОМПАС пишет для положения тела, и она подтверждена пробой RP.2
    /// (18.09.2026, прогон <c>929f0888…</c>). Первая редакция этого метода писала построчно с
    /// переносом в 3/7/11 — живой прогон 04.10.2026 показал, что запись размещения тогда НЕ берётся
    /// (перечитанное начало координат осталось нулевым).
    /// </para>
    /// <para>
    /// <b>Точная упаковка измерена ПОВОРОТОМ 04.10.2026</b> (строка приёмки <c>ASM.04.rotation</c>):
    /// тройки идут НЕ подряд, каждая занимает три числа, за которыми стоит 0, — массив читается как
    /// <c>[X, 0][Y, 0][Z, 0][перенос, 1]</c>, то есть образ оси X в 0…2, образ Y в 4…6, образ Z в
    /// 8…10, перенос в 12…14. Первая редакция ПРОВЕРКИ брала первые девять чисел подряд и падала на
    /// верной записи; на чистом переносе эту упаковку различить нельзя (единичный поворот
    /// симметричен) — поэтому различающим контролем обязан быть поворот, и он дал X=(0,1,0),
    /// Y=(−1,0,0), Z=(0,0,1) при повороте на 90° вокруг Z.
    /// </para>
    /// </remarks>
    private static double[] MatrixOf(TransformDto transform)
    {
        var (ox, oy, oz) = Vec3(transform.OriginMm);
        var (xx, xy, xz) = Vec3(transform.XAxis);
        var (yx, yy, yz) = Vec3(transform.YAxis);
        var (zx, zy, zz) = Cross(xx, xy, xz, yx, yy, yz);
        return
        [
            xx, xy, xz, 0,
            yx, yy, yz, 0,
            zx, zy, zz, 0,
            ox, oy, oz, 1,
        ];
    }

    /// <returns>
    /// Матрица 16 чисел либо <c>null</c>, если значение НЕ ПРОЧИТАНО. Прежняя редакция возвращала
    /// <c>new double[16]</c>, то есть нули: непрочитанная матрица превращалась в «компонент стоит в
    /// начале координат без поворота», и запрос с переносом (0,0,0) «совпадал» с ней, а
    /// <c>kompas_list_components</c> печатал нули как настоящую матрицу (дефект M6 ревью
    /// 05.10.2026 — тот же класс, что исправление C).
    /// </returns>
    private static double[]? Matrix16(object? variant)
    {
        if (variant is Array array && array.Length >= 16)
        {
            var result = new double[16];
            for (var i = 0; i < 16; i++)
            {
                result[i] = Convert.ToDouble(array.GetValue(i), CultureInfo.InvariantCulture);
            }

            return result;
        }

        return null;
    }

    private static object ToVariant(double[] matrix)
    {
        var result = new double[matrix.Length];
        Array.Copy(matrix, result, matrix.Length);
        return result;
    }

    /// <summary>
    /// Совпадает ли ПЕРЕЧИТАННОЕ размещение с запрошенным: поворот и перенос.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Прежняя редакция сверяла ТОЛЬКО перенос (индексы 12–14), а параметр <c>before</c> не
    /// использовался вовсе.</b> Следствие: изменение одной ориентации подтверждалось как «прочитано
    /// обратно», даже если поворот не применился (дефект M6 ревью 05.10.2026). Различающий контроль
    /// этого дефекта — поворот БЕЗ переноса при «застрявшем» размещении.
    /// </para>
    /// <para>
    /// Сверяются 12 значащих элементов: три строки поворота (0…2, 4…6, 8…10) и перенос (12…14).
    /// Четвёртый элемент каждой строки в измеренной раскладке — служебный (0,0,0,1), он не является
    /// частью преобразования и не сравнивается.
    /// </para>
    /// </remarks>
    private static bool MatrixMatchesRequest(double[]? after, double[] expected)
    {
        if (after is null || after.Length < 16)
        {
            // НЕ ПРОЧИТАНО — значит НЕ ПОДТВЕРЖДЕНО. Прежде непрочитанная матрица превращалась в
            // нули и «совпадала» с запросом, у которого перенос тоже нулевой.
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (i is 3 or 7 or 11 or 15)
            {
                continue;
            }

            var actual = after[i];
            var want = expected[i];
            if (Math.Abs(actual - want) > Math.Max(0.01, Math.Abs(want) * 1e-6))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatricesEqual(double[]? a, double[]? b)
    {
        if (a is null || b is null || a.Length < 16 || b.Length < 16)
        {
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            if (Math.Abs(a[i] - b[i]) > Math.Max(0.01, Math.Abs(a[i]) * 1e-6))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(double[]? matrix) =>
        matrix is null || matrix.Length < 16
            ? "не читается"
            : $"origin=({matrix[12]:0.####}, {matrix[13]:0.####}, {matrix[14]:0.####})";

    private static (double X, double Y, double Z) Vec3(IReadOnlyList<double> v) =>
        (v.Count > 0 ? v[0] : 0, v.Count > 1 ? v[1] : 0, v.Count > 2 ? v[2] : 0);

    private static (double X, double Y, double Z) Cross(
        double ax, double ay, double az, double bx, double by, double bz) =>
        (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Чтение COM-значения с ЯВНЫМ различением «прочитано» и «не прочитано».
    /// </summary>
    /// <remarks>
    /// Логика живёт в <see cref="SafeRead"/> (Domain, без COM-типов КОМПАСа) и покрыта модульным
    /// тестом: здесь — только короткие имена для вызовов. Прежний <c>Safe&lt;T&gt;</c> возвращал
    /// <c>default</c> и подставлял ИЗВЕСТНОЕ значение вместо НЕИЗВЕСТНОГО (непрочитанный <c>bool</c>
    /// → <c>false</c>, <c>int</c> → 0, enum → первое значение).
    /// </remarks>
    private static ReadResult<T> TryRead<T>(Func<T> read) => SafeRead.TryRead(read);

    private static T? Ref<T>(Func<T?> read) where T : class => SafeRead.Ref(read);

    private static string? Text(Func<string?> read) => SafeRead.Text(read);

    /// <summary>Чтение bool: <c>null</c> — НЕ ПРОЧИТАНО, <c>false</c> — прочитано как false.</summary>
    private static bool? Bool(Func<bool> read) => SafeRead.Bool(read);

    /// <summary>Чтение уже-необязательного bool (напр. <c>obj?.Valid</c>): <c>null</c> — не прочитано.</summary>
    private static bool? Bool(Func<bool?> read) => SafeRead.Bool(read);

    private static int? Int(Func<int> read) => SafeRead.Int(read);

    private static long? Long(Func<long> read) => SafeRead.Long(read);

    private static double? Double(Func<double> read) => SafeRead.Double(read);

    /// <summary>
    /// Имя значения перечисления; «не прочитано» печатается СЛОВОМ, а не первым значением enum.
    /// </summary>
    private static string ReadEnumName<T>(Func<T> read) where T : struct, Enum => SafeRead.EnumName(read);

    /// <summary>
    /// Уже-необязательное значение перечисления (напр. <c>obj?.Alignment</c>): <c>null</c> — НЕ
    /// прочитано. Непрочитанное не подменяется первым значением перечисления.
    /// </summary>
    private static T? EnumOrNull<T>(Func<T?> read) where T : struct, Enum => SafeRead.EnumOrNull(read);
}
