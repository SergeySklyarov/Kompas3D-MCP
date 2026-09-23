using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Чтение параметров признаков B3 ИЗ МОДЕЛИ — действие <c>read</c> наряда §7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Каждый маршрут здесь измерен пробой, а не выведен из имён членов.</b> Ссылки на прогоны стоят
/// у каждого чтения; сводка «что читается и что нет» — <c>docs/04_KOMPAS_API_NOTES.md</c> §4.10.9.
/// </para>
/// <para>
/// <b>Пустое поле означает «не прочитано», а не ноль.</b> Ни одно чтение не подставляет ноль вместо
/// неудавшегося чтения; причина попадает в <c>unreadable_parameters</c>. У семейства изменения
/// положения из пяти полей преобразования не публикуется ОДНО — <c>reposition_axis_point_mm</c>: у
/// точки оси нет документированного члена ни у одного интерфейса цепочки, и она не является
/// свойством размещения (см. <c>RepositionAxisPointUnreadable</c>). Остальные четыре читаются
/// документированным параметрическим маршрутом (<c>OrientationType</c> + <c>LocalCSParameters</c>,
/// <c>ParameterType</c> + <c>Parameters</c>), измеренным шагом RP.25 пробы
/// <c>--reposition-params</c>; там же, где параметров этого маршрута у признака нет (он записан
/// матрицей), названы ВСЕ пять — отказ, а не нули.
/// </para>
/// <para>
/// <b>Чтение не падает из-за недоступного моста.</b> Состояние признака (имя, IsValid, updateStamp)
/// читается и без API7, поэтому недоступность маршрута попадает в <c>unreadable_parameters</c>, а не
/// превращается в отказ всего <c>kompas_get_feature</c>: отказ лишил бы вызывающего и той части,
/// которая читается.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>
    /// Точка оси поворота не публикуется — и это измеренный предел, а не незакрытая ветка кода.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Документированного члена для точки оси НЕТ НИ У ОДНОГО интерфейса цепочки</b>
    /// (<c>IBodyReposition</c>, <c>ILocalCoordinateSystem</c>/<c>IPoint3D</c>,
    /// <c>IPoint3DParamDisplace</c>, <c>ILocalCSAxesDirectionParam</c>, <c>ILocalCSEulerParam</c>,
    /// <c>ILocalCSObject</c> — перечни прочитаны из библиотеки типов продукта).
    /// <c>RepositionCentre</c> и <c>ILocalCSObject.CoordinateSystem</c> координат не отдают:
    /// подтверждают только <c>IModelObject</c>, а справка объявляет <c>CoordinateSystem</c> как
    /// <c>IModelObject</c> (<c>ilocalcsobject_coordinatesystem.html</c>).
    /// </para>
    /// <para>
    /// <b>Сверх того точка оси НЕ ЯВЛЯЕТСЯ свойством размещения</b>, и это измерено, а не выведено:
    /// запись <c>X/Y/Z = c</c> МЕНЯЕТ габарит повёрнутого тела ((−5,0,0)…(5,20,5) вместо
    /// (−5,−5,0)…(5,15,5)), тогда как запись <c>X/Y/Z = c − R·c</c> его СОХРАНЯЕТ, — то есть
    /// <c>X/Y/Z</c> есть ПЕРЕНОСНАЯ ЧАСТЬ размещения. Поворот вокруг любой точки ОДНОЙ И ТОЙ ЖЕ
    /// прямой даёт ТО ЖЕ размещение, поэтому из пары «ориентация + перенос» восстанавливается не
    /// «та самая» точка, а ПРЕДСТАВИТЕЛЬ прямой: <c>EulerOrientation.AxisPointFromPlacement</c> его
    /// считает, и он совпадает с исходным входом только тогда, когда исходный вход и был этим
    /// представителем. Публиковать представитель под именем ВХОДНОГО параметра значило бы выдавать
    /// выведенное за прочитанное.
    /// </para>
    /// <para>
    /// <b>Требование чтения от этого не снимается.</b> Поле остаётся требованием строки; здесь названа
    /// лишь причина, по которой продукт его не публикует. У поворота точка оси применима, но не
    /// читается; у переноса она неприменима вовсе — и это разные формулировки, а не одна.
    /// </para>
    /// </remarks>
    private const string RepositionAxisPointUnreadable =
        "reposition_axis_point_mm — не публикуется: документированного члена для точки оси НЕТ НИ У "
        + "ОДНОГО интерфейса цепочки (IBodyReposition, ILocalCoordinateSystem/IPoint3D, "
        + "IPoint3DParamDisplace, ILocalCSAxesDirectionParam, ILocalCSEulerParam, ILocalCSObject — "
        + "перечни прочитаны из библиотеки типов продукта), и сверх того точка оси не является "
        + "свойством размещения: поворот вокруг любой точки одной и той же прямой даёт то же "
        + "размещение, поэтому из прочитанных ориентации и переноса восстанавливается представитель "
        + "прямой, а не исходный вход. Запись X/Y/Z = c меняет габарит повёрнутого тела, запись "
        + "X/Y/Z = c − R·c его сохраняет — то есть X/Y/Z есть переносная часть размещения, а не точка оси.";

    /// <summary>Точка оси у ПЕРЕНОСА: не «не читается», а НЕПРИМЕНИМА — у переноса её нет.</summary>
    private const string RepositionAxisPointNotApplicable =
        "reposition_axis_point_mm — неприменимо: записан ПЕРЕНОС, у которого точки опоры нет. Это "
        + "названная граница, а не умолчание: вид преобразования прочитан из модели и назван в "
        + "reposition_kind, поэтому вызывающий видит, почему поля нет.";

    /// <summary>Вектор у ПОВОРОТА: не «не читается», а НЕПРИМЕНИМ — контракт поворота вектора не принимает.</summary>
    private const string RepositionVectorNotApplicable =
        "reposition_vector_mm — неприменимо: записан ПОВОРОТ, а контракт поворота вектора не принимает "
        + "(нужны axis_point_mm, axis_direction_mm и angle_deg). Переносная часть размещения у поворота "
        + "есть и прочитана, но она равна c − R·c и ВХОДНЫМ вектором не является: выдать её за вход "
        + "означало бы подменить параметр операции его следствием.";

    /// <summary>
    /// Причина, по которой признак, записанный ЧУЖИМ маршрутом (матрицей), не читается — ОТКАЗ,
    /// а не «нули». Формулировка общая: имя поля подставляется вызывающим.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Как такой признак опознаётся.</b> Документ хранит ПАРАМЕТРЫ ориентации, и режим ориентации —
    /// документированное читаемое свойство <c>ILocalCoordinateSystem.OrientationType</c>. Измерено
    /// (проба <c>--reposition-params</c>, прогон <c>a336120926fc4652a8bf737562568271</c>): у признака,
    /// записанного параметрическим маршрутом, оно читается <c>1 (ksEulerCorners)</c> и на живом
    /// признаке, и после переоткрытия, а у признака, записанного матрицей, — <c>0
    /// (ksAxisOrientation)</c> (RP.16 — оба элемента коллекции; RP.22 — обе ступени переоткрытия).
    /// Это не догадка по геометрии: значение ПРОЧИТАНО из документа.
    /// </para>
    /// <para>
    /// <b>Почему отказ, а не вывод из осей.</b> У такого признака матричный вид размещения на
    /// переоткрытом документе ЕДИНИЧЕН при сохранённой геометрии (RP.16: <c>GetVector(OX) = (1,0,0)</c>
    /// при габарите (−5,−5,0)…(5,15,5)), <c>WriteToFile</c> даёт единичную матрицу (RP.18), и отличить
    /// «записано» от «не восстановлено» НЕЧЕМ: <c>Valid</c> читается <c>True</c> в обоих состояниях,
    /// а документированная последовательность <c>Update()</c> и сборки чтение не восстанавливает
    /// (RP.23). Прежняя редакция выводила из этой единичной ориентации <c>reposition_kind</c> и
    /// отдавала у ЗАПИСАННОГО ПОВОРОТА <c>"translate"</c> — это ложный результат, и он устранён.
    /// </para>
    /// <para>
    /// <b>Существующие признаки не преобразуются молча.</b> Старый признак остаётся как есть; продукт
    /// о нём честно говорит, что параметров преобразования у него нет. Успех на новых признаках
    /// поддержку старых не доказывает, поэтому «нет параметров этого маршрута» и «чтение отказало» —
    /// РАЗНЫЕ строки причин, а не одна.
    /// </para>
    /// </remarks>
    private const string RepositionLegacyReason =
        "размещение записано ЧУЖИМ маршрутом, а не документированными параметрами ориентации: "
        + "OrientationType читается 0 (ksAxisOrientation), а не 1 (ksEulerCorners), поэтому углов "
        + "Эйлера у него нет. Выводить вид преобразования из матричного вида нельзя: на переоткрытом "
        + "документе он единичен при сохранённой геометрии (RP.16: GetVector(OX) = (1,0,0) при габарите "
        + "(−5,−5,0)…(5,15,5)), WriteToFile даёт единичную матрицу (RP.18), и отличить «записано» от "
        + "«не восстановлено» нечем (Valid = True в обоих состояниях; Update() и сборка чтение не "
        + "восстанавливают, RP.23). Существующий признак не преобразуется молча: продукт называет "
        + "границу чтения, а не подставляет нули и не выдаёт выведенное за прочитанное.";

    /// <summary>Семейство B3 по НОМЕРУ ПРИЗНАКА В ДЕРЕВЕ, а не по определению: определения API5 у
    /// этих семейств нет вовсе (<c>GetDefinition()</c> возвращает null). Номера измерены 18.09.2026
    /// прибором <c>scratch/b3-measure-feature-types.py</c>.</summary>
    internal static string? SolidFamilyOf(int entityType) => entityType switch
    {
        KompasObjectTypes.BooleanOperation => BooleanFamily,
        KompasObjectTypes.SplitSolid => SplitFamily,
        KompasObjectTypes.CutByPlane => CutByPlaneFamily,
        KompasObjectTypes.BodyRepositionFeature => RepositionFamily,
        _ => null,
    };

    private SolidFeatureDto? ReadSolidFeature(DocumentEntry document, ksEntity entity, string family)
    {
        var unreadable = new List<string>();
        try
        {
            var bridge = BridgeFor(document);
            var container = RequireContainer(bridge, document, "solid.read");
            return family switch
            {
                BooleanFamily => ReadBooleanSolid(document, container, entity, unreadable),
                SplitFamily => ReadSupportSolid(document, container, entity, unreadable, isCut: false),
                CutByPlaneFamily => ReadSupportSolid(document, container, entity, unreadable, isCut: true),
                RepositionFamily => ReadRepositionSolid(document, container, entity, unreadable),
                _ => null,
            };
        }
        catch (KompasContractException ex)
        {
            unreadable.Add("solid_params_not_read — маршрут API7 недоступен: " + ex.Message);
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }
    }

    /// <summary>
    /// Вид булевой операции и сохранение инструмента.
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен</b> 18.09.2026 (проба <c>--boolean</c>, шаг BO.11 и BO.10): записанный
    /// <c>IBoolean.BooleanType</c> читается обратно как <c>ksUnion</c> / <c>ksDifference</c> /
    /// <c>ksIntersect</c> — три РАЗНЫХ значения на трёх разных записях, то есть чтение различает, а
    /// не отдаёт постоянное. <c>SaveCopyModifyObjects</c> прочитан обратно в BO.5 (<c>true</c>) и в
    /// BO.2–BO.4, BO.6 (<c>false</c>), а шаг BO.10 прочитал оба поля с ПЕРЕОТКРЫТОГО файла — то есть
    /// маршрут переживает save → close → reopen.
    /// </remarks>
    private SolidFeatureDto ReadBooleanSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable)
    {
        if (TrySolidIndex(document, entity, KompasObjectTypes.BooleanOperation,
                Api7SolidBoolean.Count(container), BooleanFamily, "Booleans") is not { } index
            || container.Booleans[index] is not IBoolean boolean)
        {
            unreadable.Add(NotMatched(BooleanFamily, "Booleans"));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        var operation = ReadEnumOrNull(() => boolean.BooleanType) switch
        {
            ksBooleanType.ksUnion => "union",
            ksBooleanType.ksDifference => "difference",
            ksBooleanType.ksIntersect => "intersect",
            _ => null,
        };
        if (operation is null)
        {
            unreadable.Add("operation_not_read — IBoolean.BooleanType не отдал вид операции "
                + "(ksBooleanUnknown либо чтение отказало)");
        }

        var keepTools = ReadBoolOrNull(() => boolean.SaveCopyModifyObjects);
        if (keepTools is null)
        {
            unreadable.Add("keep_tools_not_read — IBoolean.SaveCopyModifyObjects не прочитался");
        }

        return new SolidFeatureDto(operation, keepTools, UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>
    /// Опора разделения (<c>ISplitSolid.CutObjects</c>) либо отсечения (<c>ICut.CutObject</c>) и
    /// сторона отсечения (<c>ICut.Direction</c>).
    /// </summary>
    /// <remarks>
    /// <b>Маршрут измерен</b> 18.09.2026 (проба <c>--split</c>, шаг SP.10, прогон
    /// <c>95e24af18d694d2cb50890a99983376a</c>): опора <c>x = 10</c> читается поимённо как точки
    /// <c>(10,0,0)</c>, <c>(10,1,0)</c>, <c>(10,0,1)</c> с нормалью <c>(1,0,0)</c>, опора
    /// <c>x = 15</c> — как <c>(15,0,0)</c>, <c>(15,1,0)</c>, <c>(15,0,1)</c> (то есть чтение
    /// различает РАЗНЫЕ опоры, а не отдаёт постоянное), а <c>Direction</c> читается как <c>true</c> и
    /// <c>false</c> на одной и той же опоре. Шаг SP.9 (E-A) до этого показал, что опора читается и с
    /// живого признака, и что её три точки — то самое, что переносит правка.
    /// </remarks>
    private SolidFeatureDto ReadSupportSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable, bool isCut)
    {
        var family = isCut ? CutByPlaneFamily : SplitFamily;
        var treeType = isCut ? KompasObjectTypes.CutByPlane : KompasObjectTypes.SplitSolid;
        var collectionName = isCut ? "Cuts" : "SplitSolids";
        var count = isCut ? Api7SolidCut.Count(container) : Api7SolidSplit.Count(container);
        if (TrySolidIndex(document, entity, treeType, count, family, collectionName) is not { } index)
        {
            unreadable.Add(NotMatched(family, collectionName));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        object? readBack;
        bool? keepSide = null;
        if (isCut)
        {
            if (container.Cuts[index] is not ICut cut)
            {
                unreadable.Add("support_not_read — элемент коллекции Cuts[" + index + "] не отдаёт ICut");
                return new SolidFeatureDto(UnreadableParameters: unreadable);
            }

            readBack = cut.CutObject;
            keepSide = ReadBoolOrNull(() => cut.Direction);
            if (keepSide is null)
            {
                unreadable.Add("keep_side_not_read — ICut.Direction не прочитался");
            }
        }
        else
        {
            if (container.SplitSolids[index] is not ISplitSolid split)
            {
                unreadable.Add("support_not_read — элемент коллекции SplitSolids[" + index
                    + "] не отдаёт ISplitSolid");
                return new SolidFeatureDto(UnreadableParameters: unreadable);
            }

            readBack = split.CutObjects;
        }

        var plane = ReadSupportPlane(Api7PlaneSupport.FirstOf(readBack), unreadable);
        return new SolidFeatureDto(Plane: plane, KeepSide: keepSide, UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>
    /// Вид преобразования положения и его параметры — ПРОЧИТАННЫЕ из модели, а не выведенные.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Источник — параметры размещения, а не матрица.</b> Документ хранит ориентацию и перенос
    /// ПАРАМЕТРАМИ, и они переоткрытие переживают: <c>OrientationType = ksEulerCorners</c> +
    /// <c>LocalCSParameters → ILocalCSEulerParam</c> (тройка углов) и
    /// <c>ParameterType = ksPDisplace</c> + <c>Parameters → IPoint3DParamDisplace</c> (перенос).
    /// Маршрут измерен целиком пробой <c>--reposition-params</c>, прогон
    /// <c>a336120926fc4652a8bf737562568271</c>, шаг RP.25: тройка углов и смещение прочитаны с
    /// ПЕРЕОТКРЫТОГО документа ДО сборки и ДО всякой записи на двух постановках различающей пары
    /// (<c>displacement_after_D1 = (7,−11,13)</c>, <c>displacement_after_D2 = (1,2,3)</c>,
    /// <c>angles_kept_D1 = angles_kept_D2 = true</c>), а отрицательный контроль D0, у которого
    /// смещение не записывалось, дал <c>ParameterType = 1 (ksPParamCoord)</c> и <c>(?,?,?)</c>.
    /// </para>
    /// <para>
    /// <b>Матричный вид (<c>GetVector</c>, <c>WriteToFile</c>) не используется вовсе</b> — ни как
    /// источник, ни как подтверждение: он отдаёт записанное только в СЕССИИ записи, а на переоткрытом
    /// документе единичен при сохранённой геометрии (RP.16, RP.18, RP.20, RP.23). Именно из него
    /// прежняя редакция выводила <c>reposition_kind</c> и отдавала у записанного поворота
    /// <c>"translate"</c>.
    /// </para>
    /// <para>
    /// <b>Вид преобразования определяется по прочитанным параметрам ОДНОЗНАЧНО.</b> Тройка углов
    /// собирается в матрицу поворота (<see cref="EulerOrientation"/>), и по ней вид решается: единичный
    /// поворот при прочитанном переносе — перенос, неединичный — поворот. Неоднозначности, из-за
    /// которой прежняя редакция ошибалась, здесь нет: читаются не оси, а сами параметры, и у поворота
    /// углы не нулевые.
    /// </para>
    /// <para>
    /// <b>Что публикуется и что нет.</b> Публикуются вид, вектор переноса, направление оси и угол.
    /// Точка оси НЕ публикуется — не потому, что «не удалось», а потому, что её нет ни у одного
    /// интерфейса цепочки и она не является свойством размещения
    /// (<see cref="RepositionAxisPointUnreadable"/>). У переноса точка оси НЕПРИМЕНИМА, у поворота
    /// вектор НЕПРИМЕНИМ — это разные формулировки, и обе названы, чтобы вызывающий видел границу
    /// чтения из самого ответа, а не выяснял её отсутствием поля.
    /// </para>
    /// <para>
    /// <b>Признак, записанный матрицей, не читается — и это отказ, а не нули.</b> Он опознаётся по
    /// прочитанному <c>OrientationType = 0 (ksAxisOrientation)</c>; подробности —
    /// <see cref="RepositionLegacyReason"/>. Успех на новых признаках поддержку старых не доказывает,
    /// поэтому эта ветвь названа ОТДЕЛЬНОЙ строкой причины.
    /// </para>
    /// </remarks>
    private SolidFeatureDto ReadRepositionSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable)
    {
        if (TrySolidIndex(document, entity, KompasObjectTypes.BodyRepositionFeature,
                Api7SolidReposition.Count(container), RepositionFamily, "BodyRepositions") is not { } index
            || container.BodyRepositions[index] is not IBodyReposition)
        {
            unreadable.Add(NotMatched(RepositionFamily, "BodyRepositions"));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        // Ни одного сеттера до этого места, и это требование, а не стиль: запись перед чтением
        // вернула бы верное чтение матричного вида на один шаг и замаскировала бы дефект (RP.20).
        var placement = Api7SolidReposition.ReadPlacement(container, index);
        if (placement.Reading is null)
        {
            NameRepositionUnreadable(unreadable,
                "размещение не читается: " + (placement.Failure ?? "причина не сообщена"));
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        var reading = placement.Reading;
        if (!reading.IsEuler || reading.AnglesDeg is not { Length: 3 } angles)
        {
            NameRepositionUnreadable(unreadable, RepositionLegacyReason);
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        var rotation = EulerOrientation.RotationFromAngles(angles[0], angles[1], angles[2]);

        // ПЕРЕНОС: прочитанный поворот единичен. Вектор переноса обязан быть ПРОЧИТАН: у переноса он
        // и есть входное значение, поэтому ноль вместо неудавшегося чтения не подставляется.
        if (EulerOrientation.IsIdentity(rotation))
        {
            if (!reading.HasDisplacement || reading.DisplacementMm is not { Length: 3 } vector)
            {
                NameRepositionUnreadable(unreadable,
                    "записан перенос, но смещение не прочитано: ParameterType = "
                    + reading.ParameterType + ", а не 2 (ksPDisplace). Ноль вместо неудавшегося "
                    + "чтения не подставляется.");
                return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
            }

            unreadable.Add(RepositionAxisPointNotApplicable);
            return new SolidFeatureDto(
                RepositionKind: "translate",
                RepositionVectorMm: vector,
                UnreadableParameters: Unreadable(unreadable));
        }

        // ПОВОРОТ: ось и угол выводятся из ПРОЧИТАННОЙ ориентации, а не из матричного вида размещения.
        var (axis, angleDeg) = EulerOrientation.AxisAngleFromRotation(rotation);
        if (axis.Length != 3)
        {
            NameRepositionUnreadable(unreadable,
                "ориентация прочитана, но ось поворота из неё не выводится: кососимметричная часть "
                + "нулевая при ненулевом угле, то есть прочитанная ориентация поворотом не является");
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        unreadable.Add(RepositionAxisPointUnreadable);
        unreadable.Add(RepositionVectorNotApplicable);
        return new SolidFeatureDto(
            RepositionKind: "rotate",
            RepositionAxisDirectionMm: axis,
            RepositionAngleDeg: angleDeg,
            UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>
    /// Назвать КАЖДОЕ поле преобразования непрочитанным с одной причиной — по строке на поле.
    /// </summary>
    /// <remarks>
    /// По строке на поле, а не одной строкой со списком: вызывающий сверяет границу чтения по ИМЕНАМ
    /// полей, и склеенный перечень не отличил бы «названо» от «упомянуто». Причина подставляется без
    /// имени поля, поэтому префикс здесь ровно один.
    /// </remarks>
    private static void NameRepositionUnreadable(List<string> unreadable, string reason)
    {
        foreach (var field in RepositionTransformationFields)
        {
            unreadable.Add(field + " — не прочитано: " + reason);
        }
    }

    /// <summary>Поля преобразования положения — в том порядке, в котором они объявлены в контракте.</summary>
    private static readonly string[] RepositionTransformationFields =
    {
        "reposition_kind",
        "reposition_vector_mm",
        "reposition_axis_point_mm",
        "reposition_axis_direction_mm",
        "reposition_angle_deg",
    };

    /// <summary>
    /// Три точки построения опоры и нормаль из них. Нормаль считается по тому же правилу, что и при
    /// создании, — <c>(p2−p1)×(p3−p1)</c>, — иначе сверять было бы нечего.
    /// </summary>
    private static SupportPlaneDto? ReadSupportPlane(object? support, List<string> unreadable)
    {
        if (support is not IPlane3DBy3Points byPoints)
        {
            unreadable.Add("plane_not_read — опора признака не отвечает IPlane3DBy3Points: у неё нет "
                + "трёх точек построения, а маршрут чтения опоры идёт через них (шаг SP.10)");
            return null;
        }

        var p1 = ReadPointOf(byPoints.Point1);
        var p2 = ReadPointOf(byPoints.Point2);
        var p3 = ReadPointOf(byPoints.Point3);
        var normal = NormalOf(p1, p2, p3);
        if (p1 is null || p2 is null || p3 is null || normal is null)
        {
            unreadable.Add("plane_not_read — одна из трёх точек построения опоры не читается");
            return null;
        }

        return new SupportPlaneDto(p1, normal, p1, p2, p3);
    }

    private static double[]? ReadPointOf(object? candidate)
    {
        if (candidate is not IPoint3D point)
        {
            return null;
        }

        var x = SafeDouble(() => point.X);
        var y = SafeDouble(() => point.Y);
        var z = SafeDouble(() => point.Z);
        return x is null || y is null || z is null ? null : new[] { x.Value, y.Value, z.Value };
    }

    private static double[]? NormalOf(double[]? p1, double[]? p2, double[]? p3)
    {
        if (p1 is null || p2 is null || p3 is null)
        {
            return null;
        }

        var u = new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] };
        var v = new[] { p3[0] - p1[0], p3[1] - p1[1], p3[2] - p1[2] };
        var n = new[]
        {
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0],
        };
        var norm = Norm(n);
        return norm < 1e-12 ? null : Unit(n, norm);
    }

    // УДАЛЕНО 19.09.2026 вместе с матричным маршрутом, и это НЕ снятие требования. Здесь стояли
    // `ReadAxes` и `DeriveReposition`: они читали оси локальной системы (GetVector) и выводили из них
    // вид, ось и угол. Чтение модели на них больше не опирается — источником стали ПАРАМЕТРЫ
    // размещения (см. ReadRepositionSolid), — а как КРИТЕРИЙ они не нужны, потому что критерием стало
    // ИЗМЕРЕННОЕ свойство OrientationType: у признака, записанного матрицей, оно равно 0
    // (ksAxisOrientation), и такой признак называется непрочитанным, а не выводится из единичной
    // ориентации (см. RepositionLegacyReason).
    //
    // ПОЛОЖИТЕЛЬНЫЙ КОНТРОЛЬ ПЕРЕНОСА СОХРАНЁН — В ПРОБЕ. RP.17 (у признака, которому поворота не
    // записывали, оси единичны и тело стоит ровно на записанном переносе (207,−11,13)…(227,−1,18))
    // остаётся в tools/KompasMcp.Api7Probe/RepositionParamsProbe.cs: он нужен был затем, чтобы запрет
    // публикации из нечитаемого источника не превратился в запрет ЗНАЧЕНИЯ translate. Значение
    // translate по-прежнему разрешено — теперь оно берётся из ПРОЧИТАННОГО единичного поворота при
    // прочитанном переносе, а не выводится из осей.
    private static double Norm(double[] value) =>
        Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);

    private static double[] Unit(double[] value, double norm) =>
        new[] { value[0] / norm, value[1] / norm, value[2] / norm };

    /// <summary>
    /// Позиция признака в коллекции API7 — тем же сопоставлением по дереву, что и правка
    /// (<see cref="RequireSameTypeIndex"/>), но БЕЗ отказа: у чтения нет мутации, которую надо
    /// предотвратить, поэтому несопоставленный признак — это названная причина, а не отказ.
    /// </summary>
    private static int? TrySolidIndex(
        DocumentEntry document, ksEntity entity, int treeType, int? api7Count, string family, string collectionName)
    {
        try
        {
            return RequireSameTypeIndex(document, entity, treeType, api7Count, family, collectionName);
        }
        catch (KompasContractException)
        {
            return null;
        }
    }

    private static string NotMatched(string family, string collectionName) =>
        "solid_feature_not_matched — признак (" + family + ") не сопоставлен с элементом коллекции "
        + "API7 " + collectionName + ": позиция в дереве и число элементов разошлись, поэтому "
        + "параметры не читались — читать «похожий» признак значило бы описывать не тот объект";

    private static IReadOnlyList<string>? Unreadable(List<string> reasons) =>
        reasons.Count == 0 ? null : reasons;

    private static bool? ReadBoolOrNull(Func<bool> read)
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

    private static T? ReadEnumOrNull<T>(Func<T> read) where T : struct, Enum
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

    private static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + ex.Message
        + (ComHResult.From(ex) is int code ? " [" + ComHResult.Name(code) + "]" : string.Empty);
}
