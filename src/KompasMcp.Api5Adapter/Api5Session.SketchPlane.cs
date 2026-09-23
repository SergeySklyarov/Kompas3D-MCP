using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Смена ОПОРНОЙ плоскости существующего эскиза (<c>kompas_set_sketch_plane</c>) — вторая половина
/// действия <c>edit</c> строки <c>AUX-SKETCH.plane_and_profile_lifecycle</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Документированный маршрут.</b> <c>ksSketchDefinition.SetPlane(LPENTITY plane)</c> —
/// «Изменить базовую плоскость эскиза» (<c>kssketchdefinition_setplane.html</c>); чтение обратно —
/// <c>LPENTITY GetPlane()</c> (<c>kssketchdefinition_getplane.html</c>). Оба члена — API5; маршрут
/// API7 (<c>ISketch.Plane</c>) на поставленной сборке НЕ отвечает: измерено «эскиз не приводится к
/// <c>ISketch</c>» (проба <c>--sketch-plane</c>, шаг SP.3), поэтому правка идёт API5-членом, а не
/// приведением к API7.
/// </para>
/// <para>
/// <b>Почему после записи обязателен <c>sketch.Update()</c>, и почему это ИЗМЕРЕНО, а не выбрано.</b>
/// Первый прогон пробы дал <c>SetPlane(xz) = True</c> и неизменившийся габарит — то есть «принято и
/// не применено». Это ровно тот случай, когда дефект прибора и граница продукта выглядят одинаково,
/// поэтому вопрос был задан лестницей ступеней с чтением габарита ПОСЛЕ КАЖДОЙ:
/// <c>definition.EndEdit()</c> — не применяет; <b><c>sketch.Update()</c> — применяет</b>;
/// <c>part.RebuildModel()</c> и <c>document.RebuildDocument()</c> после него не добавляют ничего
/// (<c>moved_by_this_route = false</c> у обеих). Лестница живёт в приборе; здесь вызывается
/// измеренно-достаточная ступень, и её имя возвращается в ответе полем <c>apply_route</c>, а не
/// подразумевается.
/// </para>
/// <para>
/// <b>Отказ не-плоскости — ДО COM и по виду ссылки из реестра.</b> Измерено шагами SP.8/SP.9: ядро
/// ПРИНИМАЕТ в опору плоскую грань (<c>SetPlane = True</c>, и все шесть граней коробки
/// перепривязывают зависимое тело) и ОТВЕРГАЕТ ребро и тело (<c>SetPlane = False</c>). Продукт этот
/// ответ не наследует в обе стороны: <c>reference</c>, ведущая не на плоскость, отвергается по
/// ВИДУ из реестра, без единого обращения к COM. Иначе «грань» стала бы плоскостью по факту
/// принятия её ядром.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Виды реестра, несущие плоскость как ОБЪЕКТ модели.</summary>
    /// <remarks>
    /// Отбор идёт по виду, записанному в реестр при выдаче ссылки, а не по номеру типа, прочитанному
    /// из COM: именно это делает отказ ДО обращения к COM возможным. Номера типов названы рядом
    /// только для диагностики — <c>o3d_planeXOY/XOZ/YOZ</c> = 1/2/3, <c>o3d_planeOffset</c> = 14.
    /// </remarks>
    private static readonly string[] PlaneKinds = ["plane"];

    /// <summary>Виды реестра, по которым опора эскиза НЕ меняется, — названы для отказа.</summary>
    /// <remarks>
    /// Грань и ребро стоят здесь потому, что ядро принимает грань: без этого перечня отказ по грани
    /// выглядел бы как «вид не опознан», а не как «грань — не плоскость».
    /// </remarks>
    private static readonly string[] NonPlaneKinds = ["face", "edge", "body", "body_unresolved", "axis", "point"];

    public SetSketchPlaneResult SetSketchPlane(SetSketchPlaneCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var diagnostics = new List<string>();

        var sketch = ResolveSketchForPlaneChange(document, command.SketchRef, diagnostics);
        var planeEntity = ResolveSupportPlane(document, command.Plane, diagnostics);

        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Определение эскиза '{command.SketchRef}' не получено: менять опору не у чего.",
                RetryPolicy.ReacquireContext);
        }

        var supportBefore = TryGetPlaneType(definition);
        var before = DescribeBody(document);
        diagnostics.Add("Опора до правки: " + supportBefore);

        if (!definition.SetPlane(planeEntity))
        {
            // Отказ ядра НАЗЫВАЕТСЯ, а не смягчается: это факт о вызове, и он не выдаётся ни за
            // успех, ни за границу продукта — граница проверена ДО COM (вид ссылки).
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetPlane не принят ядром: смена опоры не выполнена.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["support_before"] = supportBefore });
        }

        // Измеренно-достаточная ступень применения. Имя возвращается в ответе, потому что «какой
        // маршрут применил правку» — измеренная величина, а не деталь реализации.
        var applyRoute = "sketch.Update()";
        var updated = TryUpdate(sketch, diagnostics);

        var supportAfter = TryGetPlaneType(definition);
        var after = DescribeBody(document);
        var geometryChanged = !string.Equals(before.Gabarit, after.Gabarit, StringComparison.Ordinal)
            || !SameVolume(before.Volume, after.Volume);

        diagnostics.Add("Опора после правки: " + supportAfter);
        diagnostics.Add("Зависимое тело: " + before.Gabarit + " V=" + DescribeVolume(before.Volume)
            + " → " + after.Gabarit + " V=" + DescribeVolume(after.Volume));

        return new SetSketchPlaneResult(
            command.SketchRef,
            supportBefore,
            supportAfter,
            TryGetPlaneName(definition),
            before.Gabarit,
            after.Gabarit,
            before.Volume,
            after.Volume,
            geometryChanged,
            applyRoute + (updated ? "" : " (вызов не подтверждён)"),
            diagnostics);
    }

    /// <summary>Эскиз по ссылке: вид проверяется по реестру, номер типа — у самого объекта.</summary>
    private ksEntity ResolveSketchForPlaneChange(
        DocumentEntry document, string sketchRef, List<string> diagnostics)
    {
        var stored = References.Require(sketchRef, document.Id, document.Revision);
        if (stored.Payload is not ksEntity entity)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{sketchRef}' (вид «{stored.Kind}») не несёт объекта модели: опору меняют у эскиза.",
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        if (entity.type != KompasObjectTypes.Sketch)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{sketchRef}' ведёт на объект типа {entity.type}, а не на эскиз " +
                $"({KompasObjectTypes.Sketch}): опору меняют у эскиза.",
                details: new Dictionary<string, object?>
                {
                    ["kind"] = stored.Kind,
                    ["entity_type"] = entity.type,
                    ["expected_type"] = KompasObjectTypes.Sketch,
                });
        }

        diagnostics.Add($"Эскиз по ссылке '{sketchRef}': вид «{stored.Kind}», тип {entity.type}.");
        return entity;
    }

    /// <summary>Опора запроса: готовая ссылка ЛИБО базовая плоскость со смещением.</summary>
    private ksEntity ResolveSupportPlane(
        DocumentEntry document, PlaneRefDto plane, List<string> diagnostics)
    {
        var hasReference = plane.Reference is { Length: > 0 };
        var hasBase = plane.Base is not null;

        // «Одновременно одно» — и это отказ, а не приоритет одного поля над другим: принятое и
        // проигнорированное поле доживает до приёмки, выглядя как выполненная правка.
        if (hasReference && hasBase)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Заданы одновременно base и reference: опора одна. Сервер не выбирает поле за " +
                "вызывающего — приоритет одного над другим был бы молчаливым выбором опоры.",
                details: new Dictionary<string, object?>
                {
                    ["base"] = plane.Base?.ToString(),
                    ["reference"] = plane.Reference,
                });
        }

        if (!hasReference && !hasBase)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опора не задана: нужен либо base, либо reference.");
        }

        if (!hasReference)
        {
            if (Math.Abs(plane.OffsetMm) > 1e-9)
            {
                diagnostics.Add("Опора: базовая плоскость «" + plane.Base + "» со смещением "
                    + plane.OffsetMm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм (смещённая плоскость создаётся этим же маршрутом, как при создании эскиза).");
            }
            else
            {
                diagnostics.Add("Опора: стандартная плоскость «" + plane.Base + "».");
            }

            return ResolvePlaneEntity(document, plane);
        }

        // Смещение относится к БАЗОВОЙ плоскости, а не к готовой ссылке. При создании эскиза такое
        // поле молча игнорируется; здесь оно отвергается, и разница названа прямо: принимаемое и
        // проигнорированное смещение читалось бы как выполненная перепривязка на другое расстояние.
        if (Math.Abs(plane.OffsetMm) > 1e-9)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "offset_mm задан вместе с reference: смещение относится к базовой плоскости, а не к " +
                "готовой ссылке. Сервер не игнорирует поле молча.",
                details: new Dictionary<string, object?>
                {
                    ["offset_mm"] = plane.OffsetMm,
                    ["reference"] = plane.Reference,
                });
        }

        var stored = References.Require(plane.Reference!, document.Id, document.Revision);

        // ОТКАЗ ДО COM. Вид ссылки известен из реестра, поэтому не-плоскость отвергается здесь, а
        // не ответом ядра: измерено, что ядро принимает плоскую ГРАНЬ (SP.9), и наследовать этот
        // ответ нельзя — грань не является плоскостью.
        if (!PlaneKinds.Contains(stored.Kind, StringComparer.Ordinal))
        {
            var known = NonPlaneKinds.Contains(stored.Kind, StringComparer.Ordinal)
                ? $"Вид «{stored.Kind}» — не плоскость: опорой эскиза может быть только плоскость " +
                  "(базовая или смещённая)."
                : $"Вид «{stored.Kind}» в перечне опор не значится.";
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                known + " Отказ выдан по виду ссылки ДО обращения к COM: ответ ядра на не-плоскость " +
                "продуктом не наследуется.",
                details: new Dictionary<string, object?>
                {
                    ["kind"] = stored.Kind,
                    ["allowed_kinds"] = PlaneKinds,
                    ["reference"] = plane.Reference,
                });
        }

        if (stored.Payload is not ksEntity planeEntity)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{plane.Reference}' вида «{stored.Kind}» не несёт объекта модели.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        diagnostics.Add($"Опора: готовая ссылка '{plane.Reference}' (вид «{stored.Kind}», "
            + $"тип {planeEntity.type}).");
        return planeEntity;
    }

    /// <summary>Вызов <c>sketch.Update()</c> — измеренно-достаточная ступень применения правки.</summary>
    private static bool TryUpdate(ksEntity sketch, List<string> diagnostics)
    {
        try
        {
            var updated = sketch.Update();
            diagnostics.Add("Ступень применения «sketch.Update()»: " + updated + ".");
            return updated;
        }
        catch (Exception ex)
        {
            // Исключение на ступени НЕ объявляется применением: запись уже сделана, а перестроение
            // не подтверждено — это разные утверждения, и они называются раздельно.
            diagnostics.Add("Ступень «sketch.Update()» бросила " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static string TryGetPlaneType(ksSketchDefinition definition)
    {
        try
        {
            return definition.GetPlane() is ksEntity entity
                ? $"тип {entity.type} («{entity.name}»)"
                : "не прочитана";
        }
        catch (Exception ex)
        {
            return "чтение опоры бросило " + ex.GetType().Name;
        }
    }

    private static string? TryGetPlaneName(ksSketchDefinition definition)
    {
        try
        {
            return definition.GetPlane() is ksEntity entity ? entity.name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameVolume(double? before, double? after) =>
        before is null || after is null
            ? before is null && after is null
            : Math.Abs(before.Value - after.Value) < 1e-6;

    private static string DescribeVolume(double? volume) =>
        volume is null ? "не прочитан" : volume.Value.ToString("0.####",
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Габарит и объём главного тела — то, чем «принято» отличается от «применено».</summary>
    private (string Gabarit, double? Volume) DescribeBody(DocumentEntry document)
    {
        var rows = ReadBodySnapshots(document.PartNow());
        if (rows.Count == 0)
        {
            return ("тел не найдено", null);
        }

        var first = rows[0];
        if (first.Min is null || first.Max is null)
        {
            return ("габарит не прочитан", first.Volume);
        }

        return (string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "x[{0:0.####}…{1:0.####}] y[{2:0.####}…{3:0.####}] z[{4:0.####}…{5:0.####}]",
            first.Min[0], first.Max[0], first.Min[1], first.Max[1], first.Min[2], first.Max[2]),
            first.Volume);
    }
}
