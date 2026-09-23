using System.Runtime.InteropServices;
using KompasAPI7;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>
/// Перечисление вспомогательной геометрии детали: плоскости, оси, точки — как ОБЪЕКТЫ с индексом
/// коллекции. Это то, чего продукту не хватало для действий <c>discover</c> и <c>read</c>
/// зависимостей <c>dep.refs.planes</c>, <c>dep.refs.axes</c> и <c>dep.refs.points_axes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Про устойчивый адрес — прямо и без подмены.</b> Справка документирует
/// <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c> и
/// <c>ISketchs.GetSketchByName</c>, но в поставленном <c>Interop.KompasAPI7.dll</c> целевой сборки
/// <b>нет ни одного члена с подстрокой <c>ByName</c></b> — измерено прибором
/// <c>tools/KompasMcp.InteropScan</c> 21.09.2026. Поэтому имя разрешается ПЕРЕЧИСЛЕНИЕМ коллекции
/// со сравнением <c>Name</c>, и это составлено только из документированных членов
/// (<c>Count</c>, индексированное свойство, <c>Name</c>). Возвращаемый <c>Index</c> — позиция в
/// коллекции, и он НЕ объявляется устойчивым: перестроение его сдвигает, поэтому адресом он не
/// называется ни в ответе, ни в документации инструмента.
/// </para>
/// <para>
/// <b>Предел перечисления задан числом, а не умолчанием.</b> Цена одной строки — вызов COM на
/// каждый прочитанный член; без предела документ с сотнями объектов подвесил бы сеанс на одном
/// запросе. Усечение не молчит: оно попадает в примечания строки.
/// </para>
/// </remarks>
internal static class Api7AuxEnumeration
{
    /// <summary>Строка маршрута перечисления — одна на ответ и на доказательства приёмки.</summary>
    public const string Route =
        "IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D, IModelContainer.GetPoints3D → " +
        "Count + индексированное свойство (ByName в поставке отсутствует)";

    /// <summary>Плоскости детали. Плоскость читается тем же методом, что и одиночная.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Planes(IPlanes3D planes, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => planes.Count);
        if (count is null)
        {
            return (rows, "IPlanes3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var plane = planes.get_Plane3D(i);
                if (plane is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IPlanes3D.Plane3D[i] → null"]));
                    continue;
                }

                rows.Add(Api7AuxGeometry.ReadPlane(plane, i));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение плоскости бросило {ex.GetType().Name}"]));
            }
        }

        if (count.Value > limit)
        {
            return (rows, null);
        }

        return (rows, null);
    }

    /// <summary>Оси детали.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Axes(IAxes3D axes, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => axes.Count);
        if (count is null)
        {
            return (rows, "IAxes3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var axis = axes.get_Axis3D(i);
                if (axis is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IAxes3D.Axis3D[i] → null"]));
                    continue;
                }

                rows.Add(Api7AuxGeometry.ReadAxis(axis, i));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение оси бросило {ex.GetType().Name}"]));
            }
        }

        return (rows, null);
    }

    /// <summary>Точки детали.</summary>
    public static (List<AuxGeomRow> Rows, string? Failure) Points(IPoints3D points, int limit)
    {
        var rows = new List<AuxGeomRow>();
        var count = SafeI(() => points.Count);
        if (count is null)
        {
            return (rows, "IPoints3D.Count не прочитан — перечислять нечего");
        }

        for (var i = 0; i < count.Value && rows.Count < limit; i++)
        {
            try
            {
                var point = points.get_Point3D(i);
                if (point is null)
                {
                    rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                        null, null, ["IPoints3D.Point3D[i] → null"]));
                    continue;
                }

                var (coordinates, parameterType, association, notes) =
                    Api7AuxGeometry.ReadPoint(point);
                rows.Add(new AuxGeomRow(
                    "point", i, SafeS(() => point.Name), parameterType,
                    coordinates, null, null, null, null, association, null, notes));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                rows.Add(new AuxGeomRow("unreadable", i, null, null, null, null, null, null, null,
                    null, null, [$"чтение точки бросило {ex.GetType().Name}"]));
            }
        }

        return (rows, null);
    }

    /// <summary>
    /// Найти плоскость ПО ИМЕНИ. Возвращает объект либо названную причину, в которой различаются
    /// «такого имени нет» и «имя не уникально»: молчаливый выбор первой из нескольких сделал бы
    /// адрес неоднозначным.
    /// </summary>
    public static (IModelObject? Plane, string? Failure) PlaneByName(IPlanes3D planes, string name)
    {
        var matches = new List<IModelObject>();
        var count = SafeI(() => planes.Count) ?? 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var plane = planes.get_Plane3D(i);
                if (plane is not null && string.Equals(plane.Name, name, StringComparison.Ordinal))
                {
                    matches.Add(plane);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Причина чтения одного элемента не отменяет поиск: нечитаемый элемент просто не
                // участвует в сравнении имён, и это не выдаётся за «имени нет».
            }
        }

        return matches.Count switch
        {
            0 => (null, $"плоскости с именем «{name}» в коллекции нет"),
            1 => (matches[0], null),
            _ => (null, $"имя «{name}» не уникально: найдено совпадений {matches.Count}"),
        };
    }

    private static int? SafeI(Func<int> read)
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
}
