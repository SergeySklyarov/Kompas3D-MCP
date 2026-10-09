using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Kinematic operation — "Element along a path" (docs/05 SM-04, queue B5).</summary>
/// <remarks>
/// DOC: route is documented API5 — <c>ksbaseevolutiondefinition.html</c>: the interface is obtained via
/// <c>ksEntity::GetDefinition</c>, with the members used here: <c>sketchShiftType</c>, <c>SetSketch</c>,
/// <c>PathPartArray</c>, <c>GetPathLength(bitVector)</c>. Object type <c>o3d_baseEvolution = 45</c>
/// (<c>obj3dtype.html</c>); <c>ievolutions_add.html</c> lists only <c>o3d_bossEvolution</c> (46) and
/// <c>o3d_cutEvolution</c> (47) for <c>IEvolutions::Add</c>, so creation goes <c>ksPart.NewEntity(45)</c>
/// + <c>ksBaseEvolutionDefinition</c>, not the API7 factory.
/// DOC: <c>ksbaseevolutiondefinition_sketchshifttype.html</c> — 0 «образующая переносится параллельно
/// самой себе», 1 «сохраняет исходный угол с направляющей», 2 «плоскость образующей выставляется и
/// сохраняется ортогональной направляющей». MEASURED: the shift mode discriminates on a curved path.
/// MEASURED: <c>PathPartArray()</c> returns <c>System.__ComObject</c> that casts to
/// <c>ksEntityCollection</c> and <c>Add(sketch)</c> returns <c>True</c>; reflection over
/// <c>__ComObject</c> yields no members.
/// INVARIANT: <c>Create()/Update()=true</c> is "accepted", not "applied" — volume is read back and
/// compared with the caller's analytic expectation; with no expectation the level stays
/// <c>call_returned</c>.
/// LIMIT: thin wall (<c>SetThinParam</c>) is not set; cutting by a body (<c>SM-04.cut</c>, OQ-A2) is out
/// of scope.
/// History: docs/decisions/adapter-features.md#sweep-route
/// </remarks>
public partial class Api5Session
{
    /// <summary>Base body of the kinematic operation — <c>o3d_baseEvolution</c>.</summary>
    private const short BaseEvolution = 45;

    /// <summary>Length unit for <c>GetPathLength</c>: <c>ST_MIX_LENGTH_MM</c>.</summary>
    private const uint PathLengthMillimetres = 1u;

    /// <summary>Kinematic operation: a profile along a path (SM-04).</summary>
    public SweepResult Sweep(SweepCommand command)
    {
        ValidateSweepCommand(command);

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var part = document.PartNow();

        // INVARIANT: the path must lie in the SAME part — different documents would give either a
        // kernel refusal or, worse, silently substituted foreign geometry.
        var path = RequireSketch(command.PathRef);
        if (!string.Equals(path.Document.Id, document.Id, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль и траектория принадлежат разным документам: кинематическая операция строится " +
                "в одной детали, и переносить траекторию из чужой нельзя.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["sketch_document_id"] = document.Id,
                    ["path_document_id"] = path.Document.Id,
                });
        }

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);

        if (part.NewEntity(BaseEvolution) is not ksEntity entity
            || entity.GetDefinition() is not ksBaseEvolutionDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение кинематической операции не получено: NewEntity(45) вернул объект, у " +
                "которого нет ksBaseEvolutionDefinition. Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        // MEASURED: Create() via MCP answered false while the same route builds a body outside MCP,
        // so input-call answers are COLLECTED, not discarded — without them the refusal is
        // indistinguishable from "the kernel disliked the geometry".
        var sketchAccepted = SafeBool(() => definition.SetSketch(target.Sketch));
        definition.sketchShiftType = ShiftValue(command.ShiftMode);
        var sketchReadBack = SafeBool(() => definition.GetSketch() is not null);

        var pathParts = AttachPath(definition, path.Sketch);
        if (pathParts == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Траектория не присоединена: PathPartArray() не привёлcя к ksEntityCollection. " +
                "Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        var pathPartsReadBack = PathPartCount(() => definition.PathPartArray());

        var created = SafeBool(entity.Create);

        if (created != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Create() кинематической операции вернул " + (created is null ? "ошибку вызова" : "false") +
                ": тело не построено. Траектория с разрывом или профиль, не пересекающий её, дают " +
                "именно этот исход, и он отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["set_sketch_accepted"] = sketchAccepted,
                    ["sketch_read_back_present"] = sketchReadBack,
                    ["path_parts_attached"] = pathParts,
                    ["path_parts_read_back"] = pathPartsReadBack,
                    ["profile_sketch_name"] = target.Sketch.name,
                    ["path_sketch_name"] = path.Sketch.name,
                    ["entity_type_after_new"] = entity.type,
                    ["definition_runtime"] = definition.GetType().Name,
                    ["definition_answers_boss"] = definition is ksBossEvolutionDefinition,
                });
        }

        SafeBool(entity.Update);
        part.RebuildModel();
        document.Document3D.RebuildDocument();

        // Path length is read AFTER the build: the pre-build read was an instrument defect, not a
        // product fact.
        var pathLength = SafeDouble(() => definition.GetPathLength(PathLengthMillimetres));

        var reference = References.Register("feature", document.Id, document.Revision, entity);
        BumpRevision(document, "sweep.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Create() вернул true"),
            new("path_attached", pathParts > 0, "элементов траектории: " + pathParts),
            new("body_count_grew", bodiesAfter > bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
            new("path_length_read", pathLength is not null, "длина траектории: " + Num(pathLength) + " мм"),
        };

        var unverified = new List<string>();

        // INVARIANT: "material added" is separated from a numeric match — a base-type kinematic
        // operation must INCREASE volume, checked with no analytic expectation. States differ by WHAT
        // WAS MEASURED: 0 bodies means no material existed before; bodies > 0 with an unread volume
        // means the value is NOT READ, and the check is named unread, not false.
        // History: docs/decisions/adapter-features.md#sweep-first-body
        if (volumeAfter is not double volumeAfterValue)
        {
            unverified.Add("material_added_not_measured — объём после операции не прочитан, поэтому " +
                           "«объём вырос» не проверено, а не опровергнуто");
        }
        else if (volumeBefore is double volumeBeforeValue)
        {
            checks.Add(new NamedCheck("material_added", volumeAfterValue > volumeBeforeValue,
                Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
                Expected: "объём вырос"));
        }
        else if (bodiesBefore == 0)
        {
            checks.Add(new NamedCheck("material_added", volumeAfterValue > 0,
                Observed: "тел до операции не было (объёма до не существует) → " +
                          Num(volumeAfter) + " мм³",
                Expected: "объём вырос (материала не было — стало больше нуля)"));
        }
        else
        {
            unverified.Add("material_added_not_measured — объём до операции не прочитан при непустой " +
                           "модели (тел " + bodiesBefore + "), поэтому «объём вырос» не проверено, " +
                           "а не опровергнуто");
        }
        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var geometryConfirmed = false;
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));
        if (declared.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("expected_volume", declared));
            if (declared.IsRefusal)
            {
                throw DeclaredExpectation.Refusal(
                    declared, "kompas_sweep", "объём после операции", document.Revision);
            }

            if (declared.IsUnverifiable)
            {
                unverified.Add(DeclaredExpectation.UnverifiableReason("объём после операции", declared));
            }

            geometryConfirmed = declared.IsConfirmed;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия кинематической операции не подтверждена числом");
        }

        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не " +
                       "проверяется; для этого существует отдельная приёмочная строка");
        if (command.ShiftMode == SweepShiftMode.Orthogonal)
        {
            unverified.Add("orthogonal_mode_not_distinguishable_on_straight_path — на ПРЯМОЙ " +
                           "траектории параллельный и ортогональный режимы дают одно тело (измерено " +
                           "20.09.2026: на дуге R50/90° они различаются на 8966.047734774369 мм³). " +
                           "Проверять этот режим следует на дуге.");
        }

        return new SweepResult(
            ToDto(reference, entity.name),
            command.ShiftMode.ToString(),
            1,
            pathParts,
            pathLength,
            bodiesAfter,
            volumeAfter,
            part.GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>());
    }

    /// <summary>Invoke a boolean COM member without failing the read. <c>null</c> means "the call did
    /// not happen" and differs from <c>false</c> ("the call happened and returned false"): mixing them
    /// passes a call failure off as a product refusal.</summary>
    private static bool? SafeBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Attach the path sketch to the definition. Returns the attached count: zero means the
    /// route did not build, and that is a refusal, not "an empty path".</summary>
    private static int AttachPath(ksBaseEvolutionDefinition definition, ksEntity pathSketch)
    {
        object? holder;
        try
        {
            holder = definition.PathPartArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        return SafeBool(() => collection.Add(pathSketch)) == true ? 1 : 0;
    }

    /// <summary>Numeric value of the section-motion type — <c>ksEvolutionShiftSketchTypeEnum</c>, read
    /// from the official v24 help page <c>ksevolutionshiftsketchtypeenum.html</c>.</summary>
    private static short ShiftValue(SweepShiftMode mode) => mode switch
    {
        SweepShiftMode.Parallel => 0,   // ksEvShiftParallel
        SweepShiftMode.KeepAngle => 1,  // ksEvShiftKeepAngle
        SweepShiftMode.Orthogonal => 2, // ksEvShiftOrtogonal
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Неизвестный тип движения сечения: " + mode,
            RetryPolicy.Never),
    };

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM. The cost of a mistake is
    /// asymmetric: a spurious refusal is seen at once, while an accepted-and-ignored number survives
    /// to acceptance looking like a completed operation.</summary>
    private static void ValidateSweepCommand(SweepCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SketchRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль не задан: кинематическая операция строится по замкнутому профилю.",
                RetryPolicy.Never);
        }

        if (string.IsNullOrWhiteSpace(command.PathRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Траектория не задана: без неё кинематическая операция не строится.",
                RetryPolicy.Never);
        }

        if (string.Equals(command.SketchRef, command.PathRef, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль и траектория — один и тот же объект: тело развёртки не может быть своим же " +
                "направляющим, и такой вызов ядро отвергнет, но лучше отвергнуть его до COM.",
                RetryPolicy.Never);
        }
    }
}
