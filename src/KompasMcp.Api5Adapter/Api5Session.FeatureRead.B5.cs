using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Чтение трёх семейств очереди B5 — кинематической операции, элемента по сечениям и оболочки —
/// ИЗ МОДЕЛИ, а не из ответа создания.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему это отдельный файл, а не продолжение <c>GetFeature</c>.</b> Наряд требует читать
/// параметры ИЗ ДЕРЕВА: на пересказе ответа создания уже провалились десять строк B4, и отказ был
/// прав. Поэтому у каждого семейства свой маршрут перечитывания, и он измерен, а не выведен.
/// </para>
/// <para>
/// <b>Главное измерение этого маршрута — номер типа в дереве НЕ равен номеру создания.</b> Измерено
/// 20.09.2026 (проба <c>--b5</c>, шаг B5.12):
/// </para>
/// <list type="bullet">
/// <item>признак, созданный <c>NewEntity(45)</c> (<c>o3d_baseEvolution</c>), виден в дереве под
/// номером <b>46</b> (<c>o3d_bossEvolution</c>), а определение отвечает
/// <c>ksBossEvolutionDefinition</c>;</item>
/// <item>признак, созданный <c>ILofts.Add(31)</c>, виден под <b>31</b>, определение отвечает
/// <c>ksBossLoftDefinition</c>;</item>
/// <item>признак, созданный <c>NewEntity(43)</c>, виден под <b>43</b>, определение отвечает
/// <c>ksShellDefinition</c>.</item>
/// </list>
/// <para>
/// Поэтому семейство опознаётся <b>по интерфейсу определения</b>, а не по номеру типа из ответа
/// создания: у отверстия это расхождение уже стоило отдельного дефекта (создаётся 52, в дереве
/// 583), у вращения — второго (27 против 584).
/// </para>
/// <para>
/// <b>Имя управляемого типа здесь не является доказательством.</b> <c>GetType().Name</c> у
/// COM-объекта всегда <c>__ComObject</c> — первая редакция отчёта показывала «__ComObject» и для
/// определения оболочки, которое на самом деле приводится к <c>ksShellDefinition</c> и читается.
/// Проверять надо ВОПРОСОМ «отвечает ли интерфейс», а не именем класса.
/// </para>
/// </remarks>
public partial class Api5Session
{
    private const string EvolutionFamily = "sweep";
    private const string LoftFamily = "loft";
    private const string ShellFamily = "shell";

    /// <summary>
    /// Единица длины траектории — <c>ST_MIX_*</c>. Значение 1 измерено на отрезке 100 мм:
    /// <c>GetPathLength(1)</c> вернул ровно 100 (шаг B5.3), поэтому миллиметры подтверждены числом,
    /// а не предположением.
    /// </summary>
    private const uint PathLengthUnitMillimetres = 1u;

    /// <summary>Отвечает ли определение на интерфейс кинематической операции. Оба принимаются.</summary>
    private static bool IsEvolutionDefinition(object? definition) =>
        definition is ksBaseEvolutionDefinition or ksBossEvolutionDefinition;

    /// <summary>Отвечает ли определение на интерфейс элемента по сечениям. Оба принимаются.</summary>
    private static bool IsLoftDefinition(object? definition) =>
        definition is ksBaseLoftDefinition or ksBossLoftDefinition;

    /// <summary>Отвечает ли определение на интерфейс оболочки.</summary>
    private static bool IsShellDefinition(object? definition) => definition is ksShellDefinition;

    /// <summary>
    /// Кинематическая операция, прочитанная с определения, взятого из дерева. Ни одна величина не
    /// берётся из ответа создания.
    /// </summary>
    /// <remarks>
    /// Ветка выбирается ПО ОТВЕТУ интерфейса, а не по номеру типа: измерено, что <c>NewEntity(45)</c>
    /// даёт признак, отвечающий <c>ksBossEvolutionDefinition</c>. Аксессоры передаются в общий
    /// считыватель делегатами, потому что общего интерфейса с этими членами у двух определений нет.
    /// </remarks>
    private static SweepDto? ReadSweepFeature(object? definition)
    {
        if (definition is ksBaseEvolutionDefinition baseEvolution)
        {
            return new SweepDto(
                ShiftModeName(SafeShort(() => baseEvolution.sketchShiftType)),
                ProfileCount(baseEvolution.GetSketch),
                PathPartCount(baseEvolution.PathPartArray),
                SafeDouble(() => baseEvolution.GetPathLength(PathLengthUnitMillimetres)));
        }

        if (definition is ksBossEvolutionDefinition bossEvolution)
        {
            return new SweepDto(
                ShiftModeName(SafeShort(() => bossEvolution.sketchShiftType)),
                ProfileCount(bossEvolution.GetSketch),
                PathPartCount(bossEvolution.PathPartArray),
                SafeDouble(() => bossEvolution.GetPathLength(PathLengthUnitMillimetres)));
        }

        return null;
    }

    /// <summary>
    /// <c>IEvolution.OperationResult</c> — документированный ответ о виде операции. Живёт ТОЛЬКО в
    /// API7: у API5-определения этого члена нет. Признак сопоставляется с элементом коллекции
    /// <c>Evolutions</c> ПО ПОРЯДКУ среди односемейных, а не по имени: измерено на этапе B4, что
    /// разные типы носят одно отображаемое имя.
    /// </summary>
    private int? ReadEvolutionOperationResult(DocumentEntry document, ksEntity entity, out string? reason)
    {
        reason = null;
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason = "evolution_operation_result_not_read — мост API7 недоступен";
            return null;
        }

        if (Api7Evolution.Count(container) is not int count || count <= 0)
        {
            reason = "evolution_operation_result_not_read — коллекция IModelContainer.Evolutions пуста "
                + "или не прочитана";
            return null;
        }

        var ordinal = OrdinalAmong(document.PartNow(), entity, IsEvolutionEntity);
        if (ordinal is not int index || index < 0 || index >= count)
        {
            reason = "evolution_operation_result_not_matched — признак не сопоставлен с элементом "
                + "коллекции Evolutions по порядку среди односемейных";
            return null;
        }

        var value = Api7Evolution.OperationResult(container, index);
        if (value is null)
        {
            reason = "evolution_operation_result_not_read — OperationResult не прочитался";
        }

        return value;
    }

    /// <summary>
    /// Элемент по сечениям, прочитанный из КОЛЛЕКЦИИ документа — того самого маршрута, которым он и
    /// создан. Ручка создания не используется: её ответ и есть то, что запрещено пересказывать.
    /// </summary>
    private LoftDto? ReadLoftFeature(DocumentEntry document, ksEntity entity, out string? reason)
    {
        reason = null;
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason = "loft_params_not_read — мост API7 недоступен, а ILoft живёт только в API7";
            return null;
        }

        if (Api7Loft.Count(container) is not int count || count <= 0)
        {
            reason = "loft_params_not_read — коллекция IModelContainer.Lofts пуста или не прочитана";
            return null;
        }

        var ordinal = OrdinalAmong(document.PartNow(), entity, IsLoftEntity);
        if (ordinal is not int index || index < 0 || index >= count)
        {
            reason = "loft_not_matched — признак не сопоставлен с элементом коллекции Lofts по "
                + "порядку среди односемейных (совпадений 0 или порядок не прочитан), поэтому "
                + "параметры null, а не ноль";
            return null;
        }

        var loft = Api7Loft.Read(container, index);
        if (loft is null)
        {
            reason = "loft_not_read — элемент коллекции Lofts по индексу " + index + " не отдал ILoft";
            return null;
        }

        // Цепочки читаются ЦЕЛИКОМ — сколько их, сколько сечений в каждой и какие смещения стоят на
        // каждом сечении. Одного CouplingsCount мало: «соответствие существует» и «соответствие
        // такое-то» — разные утверждения, и второе без содержимого не проверяется.
        // Ссылки на сечения выводятся из определения и сверяются С ТЕМ ЖЕ числом сечений, которое
        // публикует `section_count`: расхождение — это неполный вывод ссылок, и оно называется, а не
        // выдаётся за «сечений меньше».
        var sectionCount = Api7Loft.SectionCount(loft);
        var sectionRefs = LoftSectionRefs(document, entity);
        if (sectionRefs is not null && sectionCount is int declaredSections
            && sectionRefs.Count != declaredSections)
        {
            reason ??= "loft_section_refs_incomplete — сечений в признаке " + declaredSections
                + ", а ссылок выведено " + sectionRefs.Count
                + ": список ссылок объявлен непрочитанным, потому что неполный список читался бы "
                + "как «сечений меньше», а он же служит входом правки";
            sectionRefs = null;
        }

        return new LoftDto(
            BuildingName(Api7Loft.BuildingType(loft, true)),
            Api7Loft.Closed(loft),
            sectionCount,
            Api7Loft.CouplingsCount(loft),
            ReadCouplingContent(loft),
            sectionRefs);
    }

    /// <summary>
    /// Оболочка, прочитанная с определения из дерева. Вторая половина — чтение тех же трёх величин
    /// из API7 (<c>IShells</c> → <c>IShell</c>); расхождение половин не замалчивается, а называется.
    /// </summary>
    private ShellDto? ReadShellFeature(
        DocumentEntry document,
        ksEntity entity,
        ksShellDefinition definition,
        out string? reason)
    {
        reason = null;

        var thickness = SafeDouble(() => definition.thickness);
        var thinType = SafeBool(() => definition.thinType);
        var faces = FaceArrayCount(definition);

        if (thickness is null || thinType is null || faces is null)
        {
            reason = "shell_params_partly_unreadable — с определения API5 не прочиталось: "
                + (thickness is null ? "thickness " : string.Empty)
                + (thinType is null ? "thinType " : string.Empty)
                + (faces is null ? "FaceArray" : string.Empty);
        }

        // Вторая половина той же постановки: те же три величины из API7. Измерено (B5.12), что
        // оба маршрута дают согласованные значения, и это записано как отдельная проверка.
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason ??= "shell_api7_half_unavailable — мост API7 недоступен, поэтому вторая половина "
                + "постановки (IShell.Thickness/ThinType/DeletedFaces) не прочитана";
        }
        else if (Api7Shell.Count(container) is not int count || count <= 0)
        {
            reason ??= "shell_api7_half_unavailable — коллекция IModelContainer.Shells пуста";
        }
        else
        {
            var ordinal = OrdinalAmong(document.PartNow(), entity, IsShellEntity);
            var shell = ordinal is int index && index >= 0 && index < count
                ? Api7Shell.Read(container, index)
                : null;
            if (shell is null)
            {
                reason ??= "shell_api7_half_not_matched — признак не сопоставлен с элементом "
                    + "коллекции Shells по порядку среди односемейных";
            }
            else
            {
                // Согласованность половин: толщина обязана совпасть, число снятых граней — тоже.
                // Направление сверяется по ЗНАЧЕНИЮ: API5 thinType=true соответствует API7 ThinType,
                // равному значению «внутрь» (измерено: dt_reverse = 1, объём 21632).
                var api7Thickness = Api7Shell.Thickness(shell);
                var api7Faces = Api7Shell.DeletedFaceCount(shell);
                if (api7Thickness is double t2 && thickness is double t1 && Math.Abs(t1 - t2) > 1e-6)
                {
                    reason ??= "shell_halves_disagree — толщина с определения API5 " + t1.ToString("0.####", CultureInfo.InvariantCulture)
                        + " против IShell.Thickness " + t2.ToString("0.####", CultureInfo.InvariantCulture);
                }

                if (api7Faces is int f2 && faces is int f1 && f1 != f2)
                {
                    reason ??= "shell_halves_disagree — снятых граней на определении API5 " + f1
                        + " против IShell.DeletedFaces " + f2;
                }
            }
        }

        // Ссылки на снятые грани: считаются ИЗ ТОЙ ЖЕ коллекции, что и `removed_face_count`, поэтому
        // расхождение этих двух чисел — не «снято меньше», а неполный вывод ссылок, и оно обязано
        // быть названо. Иначе пустой список рядом с ненулевым счётчиком выглядел бы фактом о модели.
        var removedFaceRefs = ShellRemovedFaceRefs(document, definition);
        if (removedFaceRefs is not null && faces is int faceCount && removedFaceRefs.Count != faceCount)
        {
            reason ??= "shell_removed_face_refs_incomplete — снятых граней на определении "
                + faceCount + ", а ссылок выведено " + removedFaceRefs.Count
                + ": список ссылок объявлен непрочитанным, потому что неполный список читался бы "
                + "как «снято меньше граней»";
            removedFaceRefs = null;
        }

        return new ShellDto(
            thickness,
            ThinDirectionName(thinType),
            faces,
            removedFaceRefs);
    }

    /// <summary>
    /// СЕЧЕНИЯ элемента по сечениям КАК ССЫЛКИ, выведенные из определения признака.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Маршрут документирован: <c>ksbaseloftdefinition_sketches.html</c> и
    /// <c>ksbossloftdefinition_sketches.html</c> описывают член «Sketches» — «Получить указатель на
    /// интерфейс массива эскизов элемента по сечениям», возвращаемое значение
    /// <c>ksEntityCollection</c>, и примечание «Эскизы из данного массива используются для
    /// построения элемента по сечениям». В interop тот же член объявлен как <c>Object Sketchs()</c>
    /// (перечень прочитан из <c>docs/compatibility/kompas-api5-metadata.json</c>): имя члена в
    /// справке и в interop расходятся на одну букву, и это названо, а не сглажено.
    /// </para>
    /// <para>
    /// <b>Почему ссылки выводятся заново, а не запоминаются.</b> Вход правки элемента по сечениям —
    /// это <c>section_refs</c>, и другой валюты у правки нет. Ссылка, выданная при создании, умирает
    /// на первой мутации документа (реестр хранит её против ревизии), а <c>kompas_rebuild</c> отзывает
    /// ВСЕ ссылки документа; перечисления эскизов отдельным инструментом продукт не имеет —
    /// <c>kompas_list_features</c> отдаёт только <c>EntityCollection(o3d_operationElement)</c>.
    /// Поэтому «получить свежую ссылку» можно только из самого определения, и это ровно тот приём,
    /// которым уже выводится ссылка на эскиз выдавливания (<c>SketchRefOfFeature</c>).
    /// </para>
    /// <para>
    /// <c>null</c> — «не прочитано» (определение не опознано, коллекция не привелась, COM отказал),
    /// пустой список — «в определении сечений нет». Состояния не сливаются.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string>? LoftSectionRefs(DocumentEntry document, ksEntity entity)
    {
        try
        {
            var holder = DefinitionOf(entity) switch
            {
                ksBaseLoftDefinition b => b.Sketchs(),
                ksBossLoftDefinition s => s.Sketchs(),
                _ => null,
            };

            if (holder is not ksEntityCollection sections)
            {
                return null;
            }

            var ids = new List<string>(sections.GetCount());
            for (var i = 0; i < sections.GetCount(); i++)
            {
                // `AsInterface`, а не голое `is`: элемент коллекции отдаётся как `object` и бывает
                // ДВУХ видов — сам интерфейс либо `ksEntity`, который надо развернуть через
                // `GetDefinition()`. Тот же приём, что и во всех прочих чтениях коллекций этого
                // адаптера; голое приведение здесь уже один раз дало пустой список (см. ниже).
                if (AsInterface<ksEntity>(sections.GetByIndex(i)) is ksEntity section)
                {
                    ids.Add(References.Register("sketch", document.Id, document.Revision, section).Id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>
    /// СНЯТЫЕ ГРАНИ оболочки КАК ССЫЛКИ, выведенные из <c>ksShellDefinition.FaceArray()</c>.
    /// </summary>
    /// <remarks>
    /// Тот же довод, что и у <see cref="LoftSectionRefs"/>, но с более жёсткой причиной: снятые
    /// оболочкой грани в топологии тела ОТСУТСТВУЮТ, поэтому из <c>kompas_read_topology</c> их взять
    /// нечем вовсе, а набор удаляемых граней — вход правки. Без вывода ссылок из определения признака
    /// повторная правка набора невыразима ни после мутации, ни после переоткрытия документа.
    /// <c>null</c> — «не прочитано», пустой список — «снятых граней нет».
    /// <para>
    /// ДЕФЕКТ ПРИБОРА, измеренный 20.09.2026 первым прогоном с этим полем: голое приведение
    /// <c>is ksFaceDefinition</c> дало ПУСТОЙ список при <c>removed_face_count = 1</c> — элемент
    /// <c>FaceArray()</c> приходит как <c>ksEntity</c> и требует разворота через
    /// <c>GetDefinition()</c>. Число граней и список ссылок читались из ОДНОЙ коллекции и
    /// расходились, то есть прибор молча не измерял то, что объявил измеряющим. Лечится
    /// <c>AsInterface</c> — тем же приёмом, что и остальные чтения коллекций адаптера.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string>? ShellRemovedFaceRefs(DocumentEntry document, ksShellDefinition definition)
    {
        try
        {
            if (definition.FaceArray() is not ksEntityCollection faces)
            {
                return null;
            }

            var ids = new List<string>(faces.GetCount());
            for (var i = 0; i < faces.GetCount(); i++)
            {
                if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is ksFaceDefinition face)
                {
                    ids.Add(References.Register("face", document.Id, document.Revision, face).Id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>
    /// Порядковый номер признака СРЕДИ ОДНОСЕМЕЙНЫХ — по позиции в дереве, а не по имени.
    /// </summary>
    /// <remarks>
    /// Тем же приёмом, что измерен для вращения: и дерево, и коллекция API7 перечисляют признаки в
    /// порядке создания, поэтому позиция среди односемейных — устойчивый адрес. Имя для этого не
    /// годится: измерено на этапе B4, что разные типы носят одно отображаемое имя. <c>null</c>
    /// означает «не сопоставлено», а не «нулевой».
    /// </remarks>
    private static int? OrdinalAmong(ksPart part, ksEntity target, Func<ksEntity, bool> isKind)
    {
        try
        {
            if (part.EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                is not ksEntityCollection collection)
            {
                return null;
            }

            var ordinal = 0;
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity candidate || !isKind(candidate))
                {
                    continue;
                }

                if (ReferenceEquals(candidate, target))
                {
                    return ordinal;
                }

                ordinal++;
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool IsEvolutionEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsEvolutionDefinition(definition);

    private static bool IsLoftEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsLoftDefinition(definition);

    private static bool IsShellEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsShellDefinition(definition);

    private static object? DefinitionOf(ksEntity entity)
    {
        try
        {
            return entity.GetDefinition();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Мост API7 для документа. <c>null</c> — «недоступен», и причина называется вызывающим.</summary>
    private IModelContainer? TryContainerFor(DocumentEntry document)
    {
        try
        {
            return BridgeFor(document).ContainerFor(document.Document, document.Id, document.Revision);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return null;
        }
    }

    /// <summary>
    /// Число контуров-профилей у кинематической операции: 1, если профиль привязан, 0 — если нет,
    /// <c>null</c> — если прочитать не удалось. Ноль и «не прочитано» здесь разные вещи.
    /// </summary>
    private static int? ProfileCount(Func<object?> getSketch)
    {
        try
        {
            return getSketch() is null ? 0 : 1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Число частей траектории. <c>null</c> — «не прочитано», а не ноль частей.</summary>
    private static int? PathPartCount(Func<object?> pathPartArray)
    {
        try
        {
            return pathPartArray() is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Число граней в <c>FaceArray()</c> оболочки. <c>null</c> — «не прочитано».</summary>
    private static int? FaceArrayCount(ksShellDefinition definition)
    {
        try
        {
            return definition.FaceArray() is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>
    /// Имя режима движения сечения. Значения документированы страницей
    /// <c>ksbaseevolutiondefinition_sketchshifttype.html</c> (0 / 1 / 2) и подтверждены чтением
    /// обратно. Значение вне объявленного набора отдаётся ЧИСЛОМ, а не подменяется именем и не
    /// превращается в <c>null</c>: «модель говорит 7» — это факт, и скрывать его нельзя.
    /// </summary>
    private static string? ShiftModeName(short? value) => value switch
    {
        null => null,
        0 => "parallel",
        1 => "keep_angle",
        2 => "orthogonal",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Способ построения у крайнего сечения. Значения документированы страницей
    /// <c>ksloftbuildingtype.html</c>: 0 auto, 1 by_normal, 2 by_object, 3 cupola. Незнакомое число
    /// отдаётся числом.
    /// </summary>
    private static string? BuildingName(int? value) => value switch
    {
        null => null,
        0 => "auto",
        1 => "by_normal",
        2 => "by_object",
        3 => "cupola",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Направление тонкой стенки. Соответствие измерено, а не выведено: API5 <c>thinType = true</c>
    /// даёт 21632 (внутрь), <c>false</c> — 24832 (наружу); на API7 те же величины читаются как
    /// <c>ThinType = 1</c> (внутрь) и <c>0</c> (наружу).
    /// </summary>
    private static string? ThinDirectionName(bool? thinType) => thinType switch
    {
        null => null,
        true => "inward",
        false => "outward",
    };

    private static short? SafeShort(Func<short> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

}
