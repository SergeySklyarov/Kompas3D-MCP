using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>
/// Сущность эскиза как ОБЪЕКТ с АДРЕСОМ. Пустое поле означает «не прочитано», а не ноль; причина
/// называется в <see cref="Notes"/>.
/// </summary>
/// <remarks>
/// <see cref="Address"/> — не «N-й нарисованный сервером» и не координата: это строка, которую
/// выдаёт <c>IKompasDocument1.GetObjectId</c>, и которую <c>IKompasDocument1.FindObjectById</c>
/// принимает обратно. Именно поэтому адрес годится как вход правки, а сохранённая координата —
/// нет (приёмочное требование <c>dep.sketch.entities</c>).
/// </remarks>
internal sealed record SketchEntityRow(
    int Index,
    string? Address,
    string? Kind,
    string? Name,
    int? TypeCode,
    IReadOnlyList<string> Notes);

/// <summary>Итог перечисления сущностей эскиза: строки, маршрут и счётчики по коллекциям.</summary>
internal sealed record SketchEntitiesRead(
    IReadOnlyList<SketchEntityRow> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>
/// Сущности эскиза: вход в СУЩЕСТВУЮЩИЙ эскиз, перечисление объектов вида и устойчивый адрес
/// объекта (<c>dep.sketch.entities</c>, шаг 0 наряда продуктовых маршрутов).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — из официальной справки v24, а не из догадки</b> (отчёт
/// <c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c> §6.4):
/// <c>ISketch.BeginEdit</c> → <c>IFragmentDocument</c> → <c>IViewsAndLayersManager.Views</c> →
/// <c>IView</c> → <c>IDrawingContainer.GetObjects</c>; адрес — <c>IKompasDocument1.GetObjectId</c>;
/// выход — <c>ISketch.EndEdit</c>.
/// </para>
/// <para>
/// <b>Четыре расхождения справки с поставленным interop'ом, и все четыре ИЗМЕРЕНЫ</b>
/// (прибор <c>tools/KompasMcp.InteropScan</c> по
/// <c>Libs/PolynomLib/Bin/Client/Interop.KompasAPI7.dll</c> целевой сборки 24.0.0.2799):
/// </para>
/// <list type="number">
/// <item>Страница называет <c>BeginEdit(bool readOnly)</c>; в interop это ДВА члена —
/// <c>BeginEdit()</c> и <c>BeginEditEx(Boolean ReadOnly)</c>. Режим «только чтение» берётся
/// <c>BeginEditEx(true)</c>; вызов <c>BeginEdit()</c> открыл бы эскиз на запись, то есть чтение
/// получило бы право менять модель. Поэтому здесь вызывается только <c>BeginEditEx(true)</c>.</item>
/// <item>Страница называет адрес членом <c>IKompasDocument.GetObjectId</c>; в поставке он объявлен
/// на <c>IKompasDocument1</c> (IID <c>{58890FE8-E671-4561-994A-600DD29032E4}</c>), и у
/// <c>IKompasDocument</c> его НЕТ вовсе. Требуется QI, и он здесь делается явно.</item>
/// <item>Страница называет <c>IDrawingContainer.GetObjects(std::vector&lt;int32_t&gt;)</c>; в
/// interop параметр объявлен как <c>Object</c> (SAFEARRAY), поэтому передаётся массив
/// <c>int[]</c>. Коллекция <c>IView</c> при этом НЕ несёт <c>Objects</c>: приведение
/// <c>IView</c> → <c>IDrawingContainer</c> обязательно и делается QI.</item>
/// <item><c>FragmentDocument</c> в interop — co-class с НУЛЁМ членов; члены живут на
/// <c>IFragmentDocument</c> (тот же IID <c>{E19CE626-DF9C-48C4-A83D-3E3BC7F0DACA}</c>). Поэтому
/// приведение к <c>IFragmentDocument</c> — часть маршрута, а не украшение.</item>
/// </list>
/// <para>
/// <b>Чего в поставке НЕТ вовсе, и это тоже измерено:</b> члена с подстрокой <c>ByName</c> в
/// <c>Interop.KompasAPI7.dll</c> — <b>ноль</b>, тогда как справка документирует
/// <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c> и
/// <c>ISketchs.GetSketchByName</c>. Поэтому «имя как устойчивый адрес» реализуется ПЕРЕЧИСЛЕНИЕМ
/// коллекции со сравнением <c>Name</c> — составленным из документированных членов
/// (<c>Count</c>, индексированное свойство, <c>Name</c>), а не недокументированным обходом.
/// </para>
/// </remarks>
internal static class Api7SketchEntities
{
    /// <summary>Маршрут перечисления. Строка уходит в ответ клиенту и в доказательства приёмки,
    /// поэтому она одна на оба места: расхождение сделало бы записи несравнимыми.</summary>
    public const string Route =
        "ISketch.BeginEditEx(true) → IFragmentDocument.ViewsAndLayersManager.Views → " +
        "IView(QI IDrawingContainer).GetObjects(ksAllObj) → IKompasDocument1.GetObjectId → " +
        "ISketch.EndEdit()";

    /// <summary>
    /// Документ, которому принадлежит объект: подъём по <c>Parent</c> до <c>IKompasDocument</c>.
    /// </summary>
    /// <remarks>
    /// Почему не <c>IApplication.ActiveDocument</c>: «активный документ» — это состояние окна, а не
    /// свойство объекта. Эскиз в неактивном документе читался бы адресом ЧУЖОГО документа, и это
    /// был бы не отказ, а тихо неверный ответ. Подъём по <c>Parent</c> идёт от самого объекта и
    /// потому от активности окна не зависит.
    /// </remarks>
    private static IKompasDocument? DocumentOf(IKompasAPIObject? start)
    {
        var node = start;
        for (var depth = 0; depth < 32 && node is not null; depth++)
        {
            if (node is IKompasDocument document)
            {
                return document;
            }

            node = SafeParent(node);
        }

        return null;
    }

    private static IKompasAPIObject? SafeParent(IKompasAPIObject node)
    {
        try
        {
            return node.Parent;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Перечислить сущности эскиза с адресами. Возвращает либо строки, либо НАЗВАННУЮ причину,
    /// почему перечисление не состоялось: пустой список и «не прочитано» — разные состояния.
    /// </summary>
    public static (SketchEntitiesRead? Read, string? Failure) Read(
        Api7Bridge bridge, object sketch5, int limit)
    {
        if (bridge.TransferTo7(sketch5) is not ISketch sketch)
        {
            return (null, "эскиз не переносится в API7 как ISketch");
        }

        // Адрес выдаёт ДОКУМЕНТ, поэтому он нужен до входа в редактирование.
        var document = DocumentOf(sketch as IKompasAPIObject);
        if (document is null)
        {
            return (null, "документ эскиза не найден подъёмом по IKompasAPIObject.Parent");
        }

        if (document is not IKompasDocument1 withIds)
        {
            return (null,
                "документ не отвечает QI(IKompasDocument1): GetObjectId объявлен именно на нём " +
                "(IID {58890FE8-E671-4561-994A-600DD29032E4}), у IKompasDocument его нет");
        }

        FragmentDocument? fragment;
        try
        {
            // BeginEditEx(true) — ТОЛЬКО ЧТЕНИЕ. BeginEdit() открыл бы эскиз на запись, и чтение
            // получило бы право менять модель: это разные члены, а не перегрузки с умолчанием.
            fragment = sketch.BeginEditEx(true);
        }
        catch (COMException ex)
        {
            return (null, $"ISketch.BeginEditEx(true) отказал: HRESULT 0x{ex.HResult:X8}");
        }

        if (fragment is null)
        {
            return (null, "ISketch.BeginEditEx(true) → null: вход в эскиз не состоялся");
        }

        try
        {
            var fragmentDoc = fragment as IFragmentDocument;
            if (fragmentDoc is null)
            {
                return (null,
                    "BeginEditEx вернул объект, не отвечающий QI(IFragmentDocument): " +
                    "co-class FragmentDocument несёт ноль членов, члены живут на IFragmentDocument");
            }

            var views = fragmentDoc.ViewsAndLayersManager?.Views;
            if (views is null)
            {
                return (null, "IFragmentDocument.ViewsAndLayersManager.Views → null");
            }

            var counts = new Dictionary<string, int?>(StringComparer.Ordinal);
            var rows = new List<SketchEntityRow>();
            var notes = new List<string>();

            // АДРЕС ВЫДАЁТ ДОКУМЕНТ ФРАГМЕНТА, А НЕ ДОКУМЕНТ ДЕТАЛИ. Измерено 21.09.2026
            // (scratch/_probe_dse_dpt.py, различающий замер по получателю и родителю, бинари
            // publish-deproutes-20260921-e):
            //   A: часть,    parent=null        → «» (пусто)
            //   B: фрагмент, parent=null        → {"version":"402653244","type":"1",
            //                                       "data":{"type2D":"2","viewId":"1","objId":"1"}}
            //   C: фрагмент, parent=фрагмент    → тот же адрес
            //   D: фрагмент, parent=родитель    → тот же адрес
            //   E/F/G: часть, parent=фрагмент/эскиз/родитель → «»
            // Причина: справка объявляет parent как «родительский документ объекта (nullptr —
            // текущий документ)», а текущий документ при BeginEditEx — фрагмент эскиза; сущность
            // живёт в нём, поэтому документ детали не может её адресовать. Фрагмент отвечает
            // QI(IKompasDocument1) (измерено там же: True), хотя IFragmentDocument этого члена не
            // объявляет: у него свои 44 члена и IID {E19CE626-DF9C-48C4-A83D-3E3BC7F0DACA}.
            var fragmentAsDocument = fragment as IKompasDocument1;
            notes.Add(fragmentAsDocument is not null
                ? "Адрес выдаёт документ фрагмента эскиза: он отвечает QI(IKompasDocument1), и " +
                  "сущность принадлежит именно ему."
                : "Документ фрагмента НЕ отвечает QI(IKompasDocument1): адрес взять негде, " +
                  "перечисление пойдёт без адресов.");

            var viewCount = SafeI(() => views.Count) ?? 0;
            notes.Add($"видов во фрагменте: {viewCount}");

            for (var v = 0; v < viewCount && rows.Count < limit; v++)
            {
                IView? view;
                try
                {
                    view = views.get_View(v);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    notes.Add($"вид {v}: не получен ({ex.GetType().Name})");
                    continue;
                }

                if (view is null)
                {
                    notes.Add($"вид {v}: null");
                    continue;
                }

                // Счётчики по типам читаются с САМОГО ВИДА, потому что он и есть контейнер
                // графических объектов; IView при этом не несёт Objects — нужен QI.
                var container = view as IDrawingContainer;
                if (container is null)
                {
                    notes.Add($"вид {v}: не отвечает QI(IDrawingContainer)");
                    continue;
                }

                counts[$"view{v}.object_count"] = SafeI(() => view.ObjectCount);
                counts[$"view{v}.line_segments"] = SafeI(() => container.LineSegments?.Count);
                counts[$"view{v}.circles"] = SafeI(() => container.Circles?.Count);
                counts[$"view{v}.arcs"] = SafeI(() => container.Arcs?.Count);
                counts[$"view{v}.poly_lines"] = SafeI(() => container.PolyLines2D?.Count);
                counts[$"view{v}.rectangles"] = SafeI(() => container.Rectangles?.Count);
                counts[$"view{v}.points"] = SafeI(() => container.Points?.Count);

                // ksAllObj = 0 — «все типы» из официального перечисления DrawingObjectTypeEnum
                // (Interop.Kompas6Constants). Передаётся МАССИВОМ: в interop параметр объявлен
                // Object (SAFEARRAY), а не отдельным числом.
                var objects = ReadObjects(container, notes, v);
                foreach (var item in objects)
                {
                    if (rows.Count >= limit)
                    {
                        notes.Add($"предел перечисления {limit} достигнут — список усечён");
                        break;
                    }

                    rows.Add(Describe(item, withIds, fragmentAsDocument, rows.Count, notes));
                }
            }

            return (new SketchEntitiesRead(rows, counts, Route, notes), null);
        }
        finally
        {
            // Выход из редактирования обязателен и в ветке отказа: открытый на чтение фрагмент
            // держал бы эскиз в режиме правки, и следующий вызов получил бы занятую модель.
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Выход не удался — это не повод вернуть ложный успех, но и не повод потерять
                // прочитанное: причина останется в журнале вызовов.
            }
        }
    }

    private static IReadOnlyList<object> ReadObjects(
        IDrawingContainer container, List<string> notes, int viewIndex)
    {
        try
        {
            var raw = container.get_Objects(new int[] { (int)DrawingObjectTypeEnum.ksAllObj });
            if (raw is Array array)
            {
                return array.Cast<object>().Where(o => o is not null).ToList();
            }

            if (raw is null)
            {
                notes.Add($"вид {viewIndex}: IDrawingContainer.GetObjects(ksAllObj) → null");
                return Array.Empty<object>();
            }

            notes.Add($"вид {viewIndex}: GetObjects вернул {raw.GetType().Name}, а не массив");
            return Array.Empty<object>();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"вид {viewIndex}: GetObjects бросил {ex.GetType().Name}");
            return Array.Empty<object>();
        }
    }

    private static SketchEntityRow Describe(
        object item, IKompasDocument1 document, IKompasDocument1? fragmentDocument, int index,
        List<string> notes)
    {
        var local = new List<string>();
        string? address = null;
        string? name = null;
        int? typeCode = null;
        string? kind = null;

        if (item is IKompasAPIObject apiObject)
        {
            try
            {
                // ПОЛУЧАТЕЛЬ АДРЕСА — ДОКУМЕНТ ФРАГМЕНТА, А НЕ ДОКУМЕНТ ДЕТАЛИ, и это ИЗМЕРЕНО, а
                // не выведено. Справка целевой версии (ksapi_ikompasdocument_getobjectid.html)
                // объявляет: `std::wstring GetObjectId(const IKompasAPIObjectPtr & object,
                // const IKompasAPIObjectPtr & parent)`, «parent — родительский документ объекта
                // (nullptr - текущий документ)». При `ISketch.BeginEditEx(true)` текущим документом
                // становится фрагмент эскиза, и сущность принадлежит ему.
                //
                // Различающий замер 21.09.2026 (scratch/_probe_dse_dpt.py по бинарям
                // publish-deproutes-20260921-e, семь кандидатов, исход каждого назван):
                //   A: часть,    parent=null      → «»
                //   B: фрагмент, parent=null      → непустой адрес (победитель)
                //   C: фрагмент, parent=фрагмент  → тот же адрес
                //   D: фрагмент, parent=родитель  → тот же адрес
                //   E: часть, parent=фрагмент     → «»
                //   F: часть, parent=эскиз        → «»
                //   G: часть, parent=родитель     → «»
                // Отказ был МОЛЧАЛИВЫМ: пустая строка без исключения. Прежняя редакция передавала
                // вторым параметром `apiObject.Parent` (вид) и получала «» на всех четырёх
                // сущностях; затем `null` — тоже «», потому что получателем оставался документ
                // детали. Оба отказа записаны в дневном журнале, а не стёрты.
                //
                // Второй параметр — `null`, и это ЕДИНСТВЕННАЯ выразимая форма: в поставленном
                // Interop.KompasAPI7.dll `IKompasDocument1` НЕ является `IKompasAPIObject`
                // (измерено компиляцией: `CS1503: cannot convert from 'IKompasDocument1' to
                // 'IKompasAPIObject'`). Поэтому «текущий документ» выражается отсутствием родителя,
                // а документ-получатель назван явно.
                var addressDocument = fragmentDocument ?? document;
                address = addressDocument.GetObjectId(apiObject, null);
                if (fragmentDocument is null)
                {
                    local.Add(
                        "адрес запрошен у документа ДЕТАЛИ: документ фрагмента не отвечает " +
                        "QI(IKompasDocument1), и адрес, скорее всего, будет пуст");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                local.Add($"адрес не выдан: {ex.GetType().Name}");
            }

            // Тип объекта модели читается у IKompasAPIObject — это единственный член перечисления,
            // который несёт ВСЯКИЙ объект API7 (у IKompasAPIObject их всего четыре: Application,
            // Parent, Reference, Type).
            typeCode = ReadInt(() => (int)apiObject.Type, local, "тип объекта модели");
            kind = ReadString(() => apiObject.Type.ToString(), local, "вид объекта модели");
        }
        else
        {
            local.Add("объект не отвечает IKompasAPIObject — адрес и тип недостижимы");
        }

        if (item is IDrawingObject drawing)
        {
            // Вид примитива берётся из IDrawingObject.DrawingObjectType: это и есть «отрезок /
            // окружность / дуга / полилиния» из приёмочного перечня типов. Номер типа публикуется
            // ЧИСЛОМ, а не подменяется именем, если значение вне объявленного перечисления.
            var drawingKind = ReadString(
                () => drawing.DrawingObjectType.ToString(), local, "вид графического объекта");
            var drawingCode = ReadInt(
                () => (int)drawing.DrawingObjectType, local, "номер типа графического объекта");
            if (drawingKind is not null)
            {
                kind = drawingKind;
            }

            if (drawingCode is not null)
            {
                typeCode = drawingCode;
            }
        }

        // ИМЯ ГРАФИЧЕСКОГО ОБЪЕКТА В API НЕТ, и это измерено: у IDrawingObject члены — Application,
        // Delete, DrawingObjectParamType, DrawingObjectType, LayerNumber, Parent, Reference, Temp,
        // Type, Update, Valid; члена «имя» среди них нет. Поэтому пустое имя здесь означает
        // «не публикуется маршрутом», а не «объект без имени»: идентичность несёт адрес.
        if (item is IModelObject model)
        {
            name = ReadString(() => model.Name, local, "имя объекта модели");
        }

        notes.AddRange(local.Select(n => $"объект {index}: {n}"));
        return new SketchEntityRow(index, address, kind, name, typeCode, local);
    }

    private static string? ReadString(Func<string?> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитано: {ex.GetType().Name}");
            return null;
        }
    }

    private static int? ReadInt(Func<int> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитан: {ex.GetType().Name}");
            return null;
        }
    }

    private static int? SafeI(Func<int?> read)
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

    /// <summary>
    /// Опорная плоскость СУЩЕСТВУЮЩЕГО эскиза: <c>ISketch.Plane</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Это и есть маршрут действий <c>DEP.DPL.03.read</c> и <c>DEP.DPL.04.edit</c></b>
    /// (отчёт шага 0 §6.1 п. 7): чтение и смена опоры существующего эскиза. Прежняя правка признака
    /// полем <c>plane</c> ПРИНИМАЛАСЬ и геометрию не меняла — измерено строкой <c>DEP.DPL.04.edit</c>
    /// прежнего прогона; там же записано, что смены опоры нет. Здесь она появляется отдельным
    /// маршрутом, а не расширением чужого поля.
    /// </para>
    /// <para>
    /// <b>Справка называет члены <c>GetPlane</c>/<c>SetPlane</c>, в interop это свойство.</b>
    /// Измерено: <c>IModelObject get_Plane()</c> и <c>Void set_Plane(IModelObject)</c> — то есть
    /// чтение и запись одного свойства, а не два метода. Запись в C# выглядит как присваивание, и
    /// это ровно тот же вызов, что описан страницей.
    /// </para>
    /// </remarks>
    public static (string? Kind, string? Name, string? Failure) ReadPlane(ISketch sketch)
    {
        try
        {
            var plane = sketch.Plane;
            if (plane is null)
            {
                return (null, null, "ISketch.Plane → null: у эскиза нет опорной плоскости");
            }

            // Вид опоры берётся по ТИПУ объекта модели (o3d_planeXOY=11 и т. д. из obj3dtype.html),
            // а не по имени: имя плоскости пользователь может переименовать, тип — нет.
            var kind = SafeS(() => plane.ModelObjectType.ToString());
            var name = SafeS(() => plane.Name);
            return (kind, name, null);
        }
        catch (COMException ex)
        {
            return (null, null, $"ISketch.Plane отказал на чтении: HRESULT 0x{ex.HResult:X8}");
        }
        catch (InvalidCastException ex)
        {
            return (null, null, $"ISketch.Plane вернул неприводимое значение: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Сменить опорную плоскость существующего эскиза: <c>ISketch.Plane = объект</c>, затем
    /// <c>Update()</c>. Возвращает <c>null</c> при успехе либо названную причину отказа.
    /// </summary>
    /// <remarks>
    /// <b>Успешный код не равен применённой правке</b> — это правило проекта, и здесь оно
    /// соблюдено вызовом: без <c>Update()</c> присваивание принимается и модель не меняется
    /// (измерено на соседних маршрутах: без <c>Update()</c> не меняется ни один режим отверстия,
    /// см. <c>docs/acceptance/api7/hole-modes.md</c>). Поэтому подтверждение берётся ОТДЕЛЬНО — на
    /// вызывающем уровне, чтением габарита и объёма после правки, а не из этого возврата.
    /// </remarks>
    public static string? SetPlane(ISketch sketch, IModelObject plane)
    {
        try
        {
            sketch.Plane = plane;
            return SafeB(() => sketch.Update()) == true
                ? null
                : "ISketch.Update() не подтвердил смену опорной плоскости";
        }
        catch (COMException ex)
        {
            return $"ISketch.Plane отказал на записи: HRESULT 0x{ex.HResult:X8}";
        }
        catch (InvalidCastException ex)
        {
            return $"присваивание ISketch.Plane бросило {ex.GetType().Name}";
        }
    }

    private static string? SafeS(Func<string?> read)
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

    private static bool? SafeB(Func<bool> read)
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

    /// <summary>
    /// Разрешить адрес обратно в объект модели. Возвращает объект либо названную причину:
    /// «адрес не найден» и «адрес не разобран» — разные состояния.
    /// </summary>
    public static (IKompasAPIObject? Object, string? Failure) ResolveAddress(
        IKompasDocument1 document, string address)
    {
        try
        {
            var found = document.FindObjectById(address, null);
            return found is null
                ? (null, $"FindObjectById({address}) → null: адрес не разрешился")
                : (found, null);
        }
        catch (COMException ex)
        {
            return (null, $"FindObjectById отказал: HRESULT 0x{ex.HResult:X8}");
        }
        catch (InvalidCastException ex)
        {
            return (null, $"FindObjectById бросил {ex.GetType().Name}");
        }
    }
}
