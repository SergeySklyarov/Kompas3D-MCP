using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Sketch constraints: write, read and delete (<c>kompas_create_sketch_constraint</c>,
/// <c>kompas_list_sketch_constraints</c>, <c>kompas_delete_sketch_constraint</c>; block G6).</summary>
/// <remarks>DOC: the route is the documented API7 one — <c>IDrawingObject1::NewConstraint()</c> →
/// <c>IParametriticConstraint</c> → <c>Create()</c>; the objects come from the sketch's own container,
/// the list from <c>IDrawingObject1::Constraints</c>.
/// INVARIANT: a write is CONFIRMED by re-reading the object's list, the sketch state AND the geometry
/// the constraint must bind — never by the return of <c>Create()</c>; the kernel's movement is published.
/// History: docs/decisions/adapter-sketch.md#sketch-constraints</remarks>
public sealed partial class Api5Session
{
    /// <summary>Writes ONE constraint on one object of an existing sketch.</summary>
    public SketchConstraintCreateResult CreateSketchConstraint(CreateSketchConstraintCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        if (Api7SketchConstraints.VocabularyProblems.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Словарь типов ограничений разошёлся: " +
                string.Join("; ", Api7SketchConstraints.VocabularyProblems) +
                ". Схема и адаптер обязаны называть один и тот же набор, иначе тип, видимый клиенту, " +
                "отвергается адаптером — или наоборот.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["problems"] = Api7SketchConstraints.VocabularyProblems.ToArray(),
                });
        }

        if (!Api7SketchConstraints.TypeVocabulary.TryGetValue(command.ConstraintType, out var type))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Тип ограничения '{command.ConstraintType}' не объявлен в ksConstraintTypeEnum. " +
                "Перечень берётся из одной сборки констант, а не из списка, продублированного в схеме; " +
                "вызов с неизвестным типом отвергается до COM.",
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["declared"] = Api7SketchConstraints.TypeVocabulary.Keys.OrderBy(k => k, StringComparer.Ordinal)
                        .ToArray(),
                });
        }

        if (command.ObjectRef is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Запись ограничения требует object_ref: ограничение ставится НА ОБЪЕКТ, и «на первый " +
                "попавшийся» адресным не является.",
                details: new Dictionary<string, object?> { ["missing_field"] = "object_ref" });
        }

        // DOC (iparametriticconstraint_value.html / _expression.html / _variable.html): all three are
        // used ONLY for ksCDimWithVariable. An accepted-and-ignored field would survive to acceptance
        // looking like an applied dimension, so the foreign fields are rejected by NAME.
        var dimensionFields = new List<string>();
        if (command.Value is not null)
        {
            dimensionFields.Add("value");
        }

        if (command.Expression is not null)
        {
            dimensionFields.Add("expression");
        }

        if (command.Variable is not null)
        {
            dimensionFields.Add("variable");
        }

        if (dimensionFields.Count > 0 && type != ksConstraintTypeEnum.ksCDimWithVariable)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Поля {string.Join(", ", dimensionFields)} принадлежат типу ksCDimWithVariable и не " +
                $"принадлежат '{command.ConstraintType}': по справке SDK 24 Value, Expression и Variable " +
                "применимы только к «Размеру с переменной». Принятое и проигнорированное поле доживает " +
                "до приёмки, выглядя как выполненный размер.",
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["foreign_fields"] = dimensionFields.ToArray(),
                    ["allowed_for"] = "ksCDimWithVariable",
                });
        }

        if (command.PartnerRef is { Length: > 0 } && command.PartnerRefs is { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Заданы и partner_ref, и partner_refs: у ограничения один партнёр — либо одиночный " +
                "объект, либо массив. Две формы одновременно не выражают ничего, и выбор одной из них " +
                "молча сделал бы ответ неоднозначным.",
                details: new Dictionary<string, object?>
                {
                    ["partner_ref"] = command.PartnerRef,
                    ["partner_refs_count"] = command.PartnerRefs.Count,
                });
        }

        if (transferOrThrow(bridge, target.Sketch) is not { } sketch7)
        {
            // The helper below throws with its own named reason; this branch is unreachable and exists
            // only to satisfy the compiler's flow analysis.
            throw new KompasContractException(ErrorCodes.CapabilityUnavailable, "Эскиз не перенесён в API7.",
                RetryPolicy.ReacquireContext);
        }

        var notes = new List<string>();
        var outcome = InWriteSession(sketch7, notes, (_, objects) =>
        {
            var targetObject = ResolveSketchObject(objects, command.ObjectRef!, "object_ref");
            var host = ResolveObject(objects, command.ObjectRef!, "object_ref", notes);
            var before = Api7SketchConstraints.ReadRows(host, notes);

            object? partner = null;
            SketchConstraintObject? partnerObject = null;
            if (command.PartnerRefs is { Count: > 0 } partners)
            {
                partner = partners
                    .Select(address => (object)ResolveObject(objects, address, "partner_refs", notes))
                    .ToArray();
            }
            else if (command.PartnerRef is { Length: > 0 } partnerRef)
            {
                partnerObject = ResolveSketchObject(objects, partnerRef, "partner_ref");
                partner = partnerObject.Host;
            }

            // The AXIS is declared as IDrawingObject on IParametriticConstraint, and in the shipped
            // interop IDrawingObject1 is NOT derived from it — so the axis is taken from the enumerated
            // object's DRAWING interface rather than from its constraint host.
            KompasAPI7.IDrawingObject? axis = null;
            SketchConstraintObject? axisObject = null;
            if (command.AxisRef is { Length: > 0 } axisRef)
            {
                axisObject = ResolveSketchObject(objects, axisRef, "axis_ref");
                axis = axisObject.Drawing;
            }

            // The geometry BEFORE the write, so a refusal can carry the numbers on both sides. Read
            // INSIDE the session: outside it the same object answers zeros silently.
            var targetShapeBefore = Api7SketchConstraints.ReadShape(targetObject.Drawing, notes);
            var partnerShapeBefore = partnerObject is null
                ? null
                : Api7SketchConstraints.ReadShape(partnerObject.Drawing, notes);
            var axisShapeBefore = axisObject is null
                ? null
                : Api7SketchConstraints.ReadShape(axisObject.Drawing, notes);

            var created = Api7SketchConstraints.Create(
                host, type, command.PointIndex, partner, command.PartnerPointIndex, axis,
                command.Value, command.Expression, command.Variable, notes);

            // CONFIRMATION — the RE-READ LIST **AND** THE RETURN OF Create(), and BOTH are required.
            // MEASURED: for ksCFixedDim the kernel answers Create() = FALSE and yet a record of that type
            // APPEARS in the object's list, with Valid = false. A confirmation that looked only at the
            // list therefore reported a refused constraint as applied. The help is explicit
            // («TRUE — параметрическое ограничение создано, FALSE — в случае неудачи»), so the record is
            // additionally required to be VALID: a record the kernel itself calls invalid is not a
            // created constraint.
            var after = Api7SketchConstraints.ReadRows(host, notes);
            var matching = after.Where(row => row.TypeCode == (int)type).ToList();
            var applied = created.Created == true && matching.Any(row => row.Valid != false);
            if (matching.Count > 0 && created.Created != true)
            {
                notes.Add($"ИЗМЕРЕНО: ядро ответило Create() = " +
                          (created.Created is null ? "не прочитано" : created.Created.Value.ToString()) +
                          $", но запись типа {type} в списке объекта ВСЁ РАВНО появилась " +
                          $"({matching.Count} шт., Valid = {matching[0].Valid}). Запись, которую ядро " +
                          "называет недействительной, созданным ограничением не считается.");
            }

            var state = Api7SketchConstraints.ObjectState(host, notes);

            // GEOMETRY CONFIRMATION — the relation the constraint must establish, read from the geometry
            // AFTER the write. "Success is only what a re-read confirms": a record in the list proves the
            // constraint EXISTS, not that it BINDS the geometry.
            var targetShapeAfter = Api7SketchConstraints.ReadShape(targetObject.Drawing, notes);
            var partnerShapeAfter = partnerObject is null
                ? null
                : Api7SketchConstraints.ReadShape(partnerObject.Drawing, notes);
            var axisShapeAfter = axisObject is null
                ? null
                : Api7SketchConstraints.ReadShape(axisObject.Drawing, notes);
            var relation = SketchConstraintGeometryRule.Evaluate(
                command.ConstraintType, targetShapeAfter, command.PointIndex,
                partnerShapeAfter, command.PartnerPointIndex, axisShapeAfter);
            var geometryDetail = "объект до " + DescribeShape(targetShapeBefore) +
                                 ", после " + DescribeShape(targetShapeAfter) +
                                 (partnerObject is null
                                     ? string.Empty
                                     : "; партнёр до " + DescribeShape(partnerShapeBefore) +
                                       ", после " + DescribeShape(partnerShapeAfter)) +
                                 (axisObject is null ? string.Empty : "; ось " + DescribeShape(axisShapeAfter));

            return new WriteOutcome(
                applied,
                before,
                after,
                created.Created,
                created.Failure,
                state.Raw,
                state.Name,
                relation,
                geometryDetail);
        });

        if (outcome is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сеанс записи в эскиз не открылся: " + (notes.LastOrDefault() ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["notes"] = notes.ToArray() });
        }

        if (outcome.Failure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ограничение не создано: " + outcome.Failure + ". Это ответ ядра на эту постановку, а не " +
                "«сервер не умеет»: Create() вернул " +
                (outcome.Created is null ? "не прочитано" : outcome.Created.Value.ToString()) + ".",
                RetryPolicy.SameOperationId,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["object_ref"] = command.ObjectRef,
                    ["created"] = outcome.Created,
                    ["notes"] = notes.ToArray(),
                });
        }

        if (!outcome.Applied)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Ограничение '{command.ConstraintType}' НЕ создано: ядро ответило Create() = " +
                (outcome.Created is null ? "не прочитано" : outcome.Created.Value.ToString()) +
                $", действующей записи этого типа в перечитанном списке нет (записей до " +
                $"{outcome.Before.Count}, после {outcome.After.Count}). Вызов, который не отказал, " +
                "применением не объявляется, и запись, которую ядро называет недействительной, " +
                "созданным ограничением не считается.",
                RetryPolicy.SameOperationId,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["object_ref"] = command.ObjectRef,
                    ["created"] = outcome.Created,
                    ["count_before"] = outcome.Before.Count,
                    ["count_after"] = outcome.After.Count,
                    ["rows_after"] = outcome.After.Select(ToDto).ToList(),
                    ["notes"] = notes.ToArray(),
                });
        }

        // GEOMETRY CONFIRMATION, the second half of "success is only what a re-read confirms". A record
        // in the list proves the constraint exists; the relation re-read on the geometry proves it BINDS.
        // A type whose relation is not a number is NAMED in unverified_aspects, never passed as verified.
        var relation = outcome.Relation;
        if (relation is { Expresses: true, Holds: false })
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Ограничение '{command.ConstraintType}' создано (запись действует), но ОТНОШЕНИЕ не " +
                $"выполнено: {relation.Relation} — измерено {Api5Number(relation.Measured)}, ожидалось " +
                $"{Api5Number(relation.Expected)} при допуске {Api5Number(relation.Tolerance)}. " +
                "Геометрия после записи: " + (outcome.GeometryDetail ?? "не прочитана") + ".",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["object_ref"] = command.ObjectRef,
                    ["relation"] = relation.Relation,
                    ["measured"] = relation.Measured,
                    ["expected"] = relation.Expected,
                    ["tolerance"] = relation.Tolerance,
                    ["geometry"] = outcome.GeometryDetail,
                    ["notes"] = notes.ToArray(),
                });
        }

        var unverified = new List<string>();
        if (relation is null)
        {
            unverified.Add("отношение не проверено: геометрия объекта не перечитана");
        }
        else if (!relation.Expresses)
        {
            unverified.Add($"{relation.Relation}: {relation.Reason}");
        }

        BumpRevision(target.Document, "sketch.constraint.create");
        var stateAfter = ReadSketchState(bridge, target.Sketch, notes);

        return new SketchConstraintCreateResult(
            ObjectRef: command.ObjectRef!,
            ConstraintType: command.ConstraintType,
            TypeCode: (int)type,
            Applied: true,
            ConstraintCountBefore: outcome.Before.Count,
            ConstraintCountAfter: outcome.After.Count,
            Constraints: outcome.After.Select(ToDto).ToList(),
            StateRaw: stateAfter.Raw,
            StateName: stateAfter.Name,
            Route: Api7SketchConstraints.Route,
            Notes: notes,
            VerifiedRelation: relation is { Expresses: true } ? relation.Relation : null,
            RelationMeasured: relation is { Expresses: true } ? relation.Measured : null,
            RelationExpected: relation is { Expresses: true } ? relation.Expected : null,
            RelationTolerance: relation is { Expresses: true } ? relation.Tolerance : null,
            UnverifiedAspects: unverified);
    }

    /// <summary>Reads the constraints of the sketch's objects, or of one named object.</summary>
    public SketchConstraintsResult ListSketchConstraints(ListSketchConstraintsCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        if (transferOrThrow(bridge, target.Sketch) is not { } sketch7)
        {
            throw new KompasContractException(ErrorCodes.CapabilityUnavailable, "Эскиз не перенесён в API7.",
                RetryPolicy.ReacquireContext);
        }

        var limit = command.Limit ?? 500;
        if (limit <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"limit обязан быть положительным, получено {limit}.",
                details: new Dictionary<string, object?> { ["limit"] = limit });
        }

        var notes = new List<string>();
        bool? parametricMode = null;
        var objects = InReadSession(sketch7, notes, (fragment, items) =>
        {
            // The DOCUMENT's parametric mode, read through the typed twin while the fragment document is
            // in hand. Read here, and not by kompas_get_sketch_status, because the fragment exists only
            // inside an edit session — and a session opened just to read is not observation-neutral
            // (measured; §4.73 of the API notes), while this call already opens one.
            parametricMode = ReadParametricMode(fragment, notes);

            var wanted = command.ObjectRef is { Length: > 0 }
                ? items.Where(o => string.Equals(o.Address, command.ObjectRef, StringComparison.Ordinal)).ToList()
                : items.ToList();

            if (command.ObjectRef is { Length: > 0 } && wanted.Count == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.DocumentNotFound,
                    $"Объекта с адресом '{command.ObjectRef}' в эскизе нет: адрес не разрешился " +
                    $"перечислением (перечислено объектов {items.Count}). Адрес выдаётся этим же " +
                    "перечислением, поэтому чужой адрес отвергается, а не подменяется похожим.",
                    details: new Dictionary<string, object?>
                    {
                        ["object_ref"] = command.ObjectRef,
                        ["enumerated"] = items.Count,
                    });
            }

            var rows = new List<SketchConstraintObjectDto>();
            foreach (var item in wanted.Take(limit))
            {
                var host = item.Host;
                if (host is null)
                {
                    notes.Add($"объект {item.Index}: не отвечает IDrawingObject1 — ограничения не читались");
                    rows.Add(new SketchConstraintObjectDto(item.Index, item.Address, item.Kind, null, null,
                        Array.Empty<SketchConstraintRowDto>()));
                    continue;
                }

                var state = Api7SketchConstraints.ObjectState(host, notes);
                rows.Add(new SketchConstraintObjectDto(
                    item.Index,
                    item.Address,
                    item.Kind,
                    state.Raw,
                    state.Name,
                    Api7SketchConstraints.ReadRows(host, notes).Select(ToDto).ToList()));
            }

            return rows;
        });
        if (objects is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сеанс чтения эскиза не открылся: " + (notes.LastOrDefault() ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["notes"] = notes.ToArray() });
        }

        return new SketchConstraintsResult(
            Objects: objects,
            ConstraintTotal: objects.Sum(dto => dto.Constraints.Count),
            Route: Api7SketchConstraints.ReadRoute,
            Notes: notes,
            ParametricMode: parametricMode);
    }

    /// <summary>Deletes ONE constraint of an object, or ALL of them.</summary>
    public SketchConstraintDeleteResult DeleteSketchConstraint(DeleteSketchConstraintCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var bridge = BridgeFor(target.Document);

        if (command.ObjectRef is not { Length: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Удаление требует object_ref: ограничения принадлежат объекту, и «снять все ограничения " +
                "эскиза» одним вызовом не выражается — иначе непонятно, у чего именно снимать.",
                details: new Dictionary<string, object?> { ["missing_field"] = "object_ref" });
        }

        var deleteAll = command.All == true;
        if (!deleteAll && command.ConstraintIndex is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для удаления одного ограничения обязателен constraint_index; для удаления всех — " +
                "all = true.",
                details: new Dictionary<string, object?>
                {
                    ["missing_field"] = "constraint_index",
                    ["all"] = command.All,
                });
        }

        if (deleteAll && command.ConstraintIndex is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поле constraint_index принадлежит удалению одного ограничения и не принадлежит all = " +
                "true: принятое и проигнорированное поле доживает до приёмки, выглядя как выполненное " +
                "удаление по позиции.",
                details: new Dictionary<string, object?>
                {
                    ["foreign_fields"] = new[] { "constraint_index" },
                    ["allowed_fields"] = new[] { "sketch_ref", "expected_revision", "object_ref", "all" },
                });
        }

        if (transferOrThrow(bridge, target.Sketch) is not { } sketch7)
        {
            throw new KompasContractException(ErrorCodes.CapabilityUnavailable, "Эскиз не перенесён в API7.",
                RetryPolicy.ReacquireContext);
        }

        var notes = new List<string>();
        var outcome = InWriteSession(sketch7, notes, (_, objects) =>
        {
            var host = ResolveObject(objects, command.ObjectRef!, "object_ref", notes);
            var before = Api7SketchConstraints.ReadRows(host, notes);

            bool? result;
            string? failure;
            if (deleteAll)
            {
                (result, failure) = Api7SketchConstraints.DeleteAll(host);
            }
            else
            {
                (result, failure) = Api7SketchConstraints.DeleteOne(host, command.ConstraintIndex!.Value,
                    before, notes);
            }

            var after = Api7SketchConstraints.ReadRows(host, notes);
            var state = Api7SketchConstraints.ObjectState(host, notes);
            return new WriteOutcome(after.Count < before.Count, before, after, result, failure,
                state.Raw, state.Name);
        });

        if (outcome is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сеанс записи в эскиз не открылся: " + (notes.LastOrDefault() ?? "причина не названа"),
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["notes"] = notes.ToArray() });
        }

        if (outcome.Failure is not null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ограничение не удалено: " + outcome.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["object_ref"] = command.ObjectRef,
                    ["all"] = deleteAll,
                    ["constraint_index"] = command.ConstraintIndex,
                    ["notes"] = notes.ToArray(),
                });
        }

        if (!outcome.Applied)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Удаление не подтверждено перечитыванием: записей было {outcome.Before.Count}, стало " +
                $"{outcome.After.Count}. Код, который не отказал, применением не объявляется.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["count_before"] = outcome.Before.Count,
                    ["count_after"] = outcome.After.Count,
                });
        }

        BumpRevision(target.Document, "sketch.constraint.delete");
        var stateAfter = ReadSketchState(bridge, target.Sketch, notes);

        return new SketchConstraintDeleteResult(
            ObjectRef: command.ObjectRef!,
            All: deleteAll,
            Applied: true,
            ConstraintCountBefore: outcome.Before.Count,
            ConstraintCountAfter: outcome.After.Count,
            Constraints: outcome.After.Select(ToDto).ToList(),
            StateRaw: stateAfter.Raw,
            StateName: stateAfter.Name,
            Route: Api7SketchConstraints.Route,
            Notes: notes);
    }

    // ─────────────────────────────────────────────────────────────────── machinery ──

    /// <summary>Outcome of one write session: the counts BEFORE and AFTER, both taken inside the SAME
    /// session, so their difference describes the operation and not two different sessions.</summary>
    private sealed record WriteOutcome(
        bool Applied,
        IReadOnlyList<SketchConstraintRow> Before,
        IReadOnlyList<SketchConstraintRow> After,
        bool? Created,
        string? Failure,
        int? ObjectStateRaw,
        string? ObjectStateName,
        ConstraintRelationVerdict? Relation = null,
        string? GeometryDetail = null);

    /// <summary>A shape as the refusal's numbers name it — every member that was read, or "не прочитано".
    /// </summary>
    private static string DescribeShape(SketchShape? shape)
    {
        if (shape is null)
        {
            return "не прочитано";
        }

        static string N(double? value) =>
            value?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "?";

        return shape.Kind switch
        {
            SketchShapeKind.Point => $"точка({N(shape.X)},{N(shape.Y)})",
            SketchShapeKind.Segment =>
                $"отрезок({N(shape.X1)},{N(shape.Y1)})-({N(shape.X2)},{N(shape.Y2)})",
            SketchShapeKind.Circle =>
                $"окружность(центр {N(shape.CenterX)},{N(shape.CenterY)}; R {N(shape.Radius)})",
            _ => $"дуга(центр {N(shape.CenterX)},{N(shape.CenterY)}; R {N(shape.Radius)}; " +
                 $"({N(shape.X1)},{N(shape.Y1)})-({N(shape.X2)},{N(shape.Y2)}))",
        };
    }

    private static SketchConstraintRowDto ToDto(SketchConstraintRow row) =>
        new(row.Index, row.Type, row.TypeCode, row.PointIndex, row.PartnerIndex, row.SegmentIndex,
            row.PartnerSegmentIndex, row.Valid, row.Value, row.Expression, row.Variable, row.PartnerPresent,
            row.Notes);

    /// <summary>Finds the object by its ADDRESS inside the enumerated objects. The address is the one the
    /// enumeration itself issued, so a foreign address cannot be silently substituted.</summary>
    private static KompasAPI7.IDrawingObject1 ResolveObject(
        IReadOnlyList<SketchConstraintObject> objects, string address, string field, List<string> notes)
    {
        var matches = objects.Where(o => string.Equals(o.Address, address, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                matches.Count == 0 ? ErrorCodes.DocumentNotFound : ErrorCodes.AmbiguousSelection,
                $"{field}='{address}' разрешился в {matches.Count} объектов вместо одного " +
                $"(в эскизе {objects.Count} объектов). Адрес обязан различать объекты, и молчаливый " +
                "выбор первого сделал бы его неоднозначным.",
                details: new Dictionary<string, object?>
                {
                    [field] = address,
                    ["matches"] = matches.Count,
                    ["enumerated"] = objects.Count,
                });
        }

        if (matches[0].Host is not { } host)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Объект по адресу '{address}' не отвечает IDrawingObject1: ограничения ставятся на " +
                "графические объекты эскиза, и другой объект ими не подменяется.",
                details: new Dictionary<string, object?>
                {
                    [field] = address,
                    ["kind"] = matches[0].Kind,
                });
        }

        return host;
    }

    /// <summary>Runs the action inside ONE write session and closes it in <c>finally</c> — on the failure
    /// path too: a fragment left open would keep the sketch in edit mode and the next call would find the
    /// model busy.</summary>
    private T? InWriteSession<T>(KompasAPI7.ISketch sketch, List<string> notes,
        Func<KompasAPI7.IFragmentDocument, IReadOnlyList<SketchConstraintObject>, T> action) =>
        InSession(sketch, write: true, notes, action);

    private T? InReadSession<T>(KompasAPI7.ISketch sketch, List<string> notes,
        Func<KompasAPI7.IFragmentDocument, IReadOnlyList<SketchConstraintObject>, T> action) =>
        InSession(sketch, write: false, notes, action);

    /// <remarks>The FRAGMENT document is handed to the action because it is the object that answers
    /// <c>QI(IKompasDocument2D1)</c> — the parametric mode is read from it while the session is open,
    /// rather than by opening a second session just for the read.</remarks>
    private T? InSession<T>(KompasAPI7.ISketch sketch, bool write, List<string> notes,
        Func<KompasAPI7.IFragmentDocument, IReadOnlyList<SketchConstraintObject>, T> action)
    {
        KompasAPI7.IFragmentDocument? fragment;
        try
        {
            // BeginEdit() is FOR WRITING and BeginEditEx(true) is READ-ONLY: different members, not
            // overloads with a default (measured in the type library: the parameter is named ReadOnly).
            // A read must not be given the right to change the model.
            fragment = write ? sketch.BeginEdit() : sketch.BeginEditEx(true);
        }
        catch (COMException ex)
        {
            notes.Add($"ISketch.BeginEdit отказал: HRESULT 0x{ex.HResult:X8}");
            return default;
        }

        if (fragment is null)
        {
            notes.Add("ISketch.BeginEdit → null: вход в эскиз не состоялся");
            return default;
        }

        try
        {
            var (objects, failure) = Api7SketchConstraints.EnumerateInSession(fragment, notes);
            if (failure is not null)
            {
                notes.Add(failure);
                return default;
            }

            return action(fragment, objects);
        }
        finally
        {
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A failed exit is a reason for the journal, not grounds to return a false success.
                notes.Add("ISketch.EndEdit не подтверждён: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>The document's parametric mode, READ through the typed twin of the interface the shipped
    /// wrapper truncates.</summary>
    /// <remarks>DOC: <c>ikompasdocument2d1_parametricmode.html</c> (<c>BOOL</c>, get/set) and
    /// <c>ikompasdocument2d1.html</c> («дополнительный интерфейс <c>IKompasDocument2D…</c> можно получить
    /// посредством <c>QueryInterface</c>»).
    /// MEASURED: the shipped wrapper's <c>IKompasDocument2D1</c> stops at
    /// <c>ChangeObjectsOrder</c> (slot 44) while the installed <c>kAPI7.tlb</c> carries the member at slots
    /// 74/75 — so the absence from the wrapper is a fact about the wrapper, not about the route, and the
    /// member is reached by reproducing the vtable (ADR-004), not by <c>IDispatch</c>. The sketch's FRAGMENT
    /// document answers <c>QI(IKompasDocument2D1)</c>; the part document does not.
    /// READ ONLY: only the getter is called — the mode is reported, never set. The twin declares the PUT as
    /// well solely because a vtable is reproduced in order.
    /// INVARIANT: an unreadable mode is <c>null</c> with the reason named; it is never substituted.
    /// History: docs/decisions/adapter-sketch.md#sketch-constraints</remarks>
    private static bool? ReadParametricMode(KompasAPI7.IFragmentDocument fragment, List<string> notes)
    {
        try
        {
            if (fragment is not IKompasDocument2D1Twin document)
            {
                notes.Add("параметрический режим не прочитан: документ фрагмента эскиза не отвечает " +
                          "QI(IKompasDocument2D1). Это утверждение о маршруте, а не о документе.");
                return null;
            }

            var mode = document.get_ParametricMode();
            notes.Add($"параметрический режим документа: {mode}");
            return mode;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("параметрический режим не прочитан: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Transfers a sketch entity to API7 and returns the <c>ISketch</c>, or throws with the
    /// bridge's own named reason.</summary>
    private KompasAPI7.ISketch? transferOrThrow(Api7Bridge bridge, Kompas6API5.ksEntity sketch)
    {
        if (bridge.Application() is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Ограничения ставятся только через IDrawingObject1, поэтому ответить достоверно нельзя.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["bridge_failure"] = bridge.BridgeFailure });
        }

        var transferred = bridge.TransferTo7(sketch);
        if (transferred is KompasAPI7.ISketch direct)
        {
            return direct;
        }

        if (transferred is KompasAPI7.IModelObject model)
        {
            return model as KompasAPI7.ISketch;
        }

        return null;
    }

    /// <summary>The aggregate definiteness state of the sketch, re-read through the same bridge the
    /// neighbouring tool uses. An unread state is <c>null</c> with the reason in the notes.</summary>
    private (int? Raw, string? Name) ReadSketchState(Api7Bridge bridge, Kompas6API5.ksEntity sketch,
        List<string> notes)
    {
        if (transferOrThrow(bridge, sketch) is not { } sketch7)
        {
            notes.Add("состояние эскиза не перечитано: ISketch недостижим");
            return (null, null);
        }

        try
        {
            var raw = (int)sketch7.ConstraintsState;
            var name = Enum.IsDefined(typeof(ksConstraintsStateEnum), raw)
                ? ((ksConstraintsStateEnum)raw).ToString()
                : null;
            return (raw, name);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("состояние эскиза не прочитано: " + ex.GetType().Name);
            return (null, null);
        }
    }
}
