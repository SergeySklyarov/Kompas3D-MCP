using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Change the SUPPORT plane of an existing sketch (<c>kompas_set_sketch_plane</c>) — the second
/// half of the <c>edit</c> action of the row <c>AUX-SKETCH.plane_and_profile_lifecycle</c>.</summary>
/// <remarks>
/// DOC: <c>ksSketchDefinition.SetPlane(LPENTITY plane)</c> — «Изменить базовую плоскость эскиза»
/// (<c>kssketchdefinition_setplane.html</c>); read back via <c>LPENTITY GetPlane()</c>
/// (<c>kssketchdefinition_getplane.html</c>). Both members are API5; the API7 route
/// (<c>ISketch.Plane</c>) does NOT answer on the shipped build: MEASURED "the sketch does not cast to
/// <c>ISketch</c>" (probe <c>--sketch-plane</c>, step SP.3), so the edit goes through the API5 member
/// rather than an API7 cast.
/// MEASURED: <c>sketch.Update()</c> is mandatory after the write, and this is measured, not chosen.
/// The first probe run gave <c>SetPlane(xz) = True</c> with an unchanged bounding box — i.e. "accepted
/// but not applied". This is exactly the case where a probe defect and a product limit look alike, so
/// the question was asked with a ladder of steps, reading the box AFTER EACH:
/// <c>definition.EndEdit()</c> — does not apply; <c>sketch.Update()</c> — applies;
/// <c>part.RebuildModel()</c> and <c>document.RebuildDocument()</c> add nothing after it
/// (<c>moved_by_this_route = false</c> for both). The ladder lives in the probe; here the
/// measured-sufficient step is called, and its name is returned in the response field
/// <c>apply_route</c> rather than implied.
/// INVARIANT: a non-plane support is refused BEFORE COM, by the reference kind in the registry.
/// MEASURED (steps SP.8/SP.9): the kernel ACCEPTS a planar FACE as support (<c>SetPlane = True</c>, and
/// all six faces of a box re-anchor the dependent body) and REJECTS an edge and a body
/// (<c>SetPlane = False</c>). The product does not inherit that answer in either direction: a
/// <c>reference</c> not leading to a plane is refused by its KIND from the registry, with no COM call
/// at all. Otherwise a "face" would become a plane by the mere fact that the kernel accepted it.
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Registry kinds that carry a plane as a MODEL OBJECT.</summary>
    /// <remarks>Selection is by the kind recorded in the registry when the reference was issued, not by
    /// the type number read from COM — that is what makes a refusal BEFORE COM possible. The type
    /// numbers are named alongside for diagnostics only: <c>o3d_planeXOY/XOZ/YOZ</c> = 1/2/3,
    /// <c>o3d_planeOffset</c> = 14.</remarks>
    private static readonly string[] PlaneKinds = ["plane"];

    /// <summary>Registry kinds for which the sketch support is NOT changed — named for the refusal.</summary>
    /// <remarks>A face and an edge are listed here because the kernel accepts a face: without this list
    /// a face refusal would look like "kind not recognised" rather than "a face is not a plane".</remarks>
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
            // The kernel's refusal is NAMED, not softened: it is a fact about the call, and it is
            // passed off neither as success nor as a product limit — the limit was checked BEFORE COM
            // (the reference kind).
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetPlane не принят ядром: смена опоры не выполнена.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["support_before"] = supportBefore });
        }

        // The measured-sufficient apply step. Its name is returned in the response because "which
        // route applied the edit" is a measured quantity, not an implementation detail.
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

    /// <summary>Sketch by reference: the kind is checked against the registry, the type number against
    /// the object itself.</summary>
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

    /// <summary>Request support: a ready reference OR a base plane with an offset.</summary>
    private ksEntity ResolveSupportPlane(
        DocumentEntry document, PlaneRefDto plane, List<string> diagnostics)
    {
        var hasReference = plane.Reference is { Length: > 0 };
        var hasBase = plane.Base is not null;

        // "Both at once" is refused rather than giving one field priority over the other: an accepted
        // and ignored field survives to acceptance looking like a completed edit.
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

        // The offset belongs to the BASE plane, not to a ready reference. When a sketch is created such
        // a field is silently ignored; here it is refused, and the difference is stated plainly: an
        // accepted-and-ignored offset would read as a completed re-anchor to a different distance.
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

        // REFUSAL BEFORE COM. The reference kind is known from the registry, so a non-plane is refused
        // here rather than by the kernel's answer: MEASURED that the kernel accepts a planar FACE
        // (SP.9), and that answer must not be inherited — a face is not a plane.
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

    /// <summary>Call <c>sketch.Update()</c> — the measured-sufficient step that applies the edit.</summary>
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
            // An exception at the step is NOT declared an application: the write is already done while
            // the rebuild is unconfirmed — these are different claims and are named separately.
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

    /// <summary>Bounding box and volume of the main body — what distinguishes "accepted" from
    /// "applied".</summary>
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
