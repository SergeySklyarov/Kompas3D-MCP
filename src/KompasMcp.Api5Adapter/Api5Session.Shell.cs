using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Shell — a thin-walled element (docs/05 SM-13, queue B5).</summary>
/// <remarks>
/// DOC: <c>ksshelldefinition.html</c> — the interface is obtained via <c>ksEntity::GetDefinition</c>;
/// members are <c>thickness</c>, <c>thinType</c> and <c>FaceArray()</c> returning «динамический массив
/// удаляемых граней <c>ksEntityCollection</c>». Object type <c>o3d_shellOperation = 43</c>
/// (<c>obj3dtype.html</c>).
/// DOC: <c>ksshelldefinition_thintype.html</c> — «<c>TRUE</c> — внутрь, <c>FALSE</c> — наружу».
/// MEASURED: the inward/outward correspondence is confirmed by volume on a box with the top face
/// removed.
/// LIMIT: an empty face list is refused BEFORE COM — a MEASURED refusal, not a taste-based ban: the
/// body does not change while <c>Create()/Update()</c> return <c>true</c> ("accepted", not "applied").
/// INVARIANT: geometry is confirmed by volume against the caller's analytic expectation and by face
/// count; with no expectation the level honestly stays <c>call_returned</c>.
/// History: docs/decisions/adapter-features.md#shell-empty-faces
/// </remarks>
public partial class Api5Session
{
    /// <summary>Shell — <c>o3d_shellOperation</c>.</summary>
    private const short ShellOperation = 43;

    /// <summary>Shell: constant wall thickness over the removed faces (SM-13).</summary>
    public ShellResult Shell(ShellCommand command)
    {
        ValidateShellCommand(command);

        var document = RequireDocument(command.DocumentId);

        // INVARIANT: faces must belong to the SAME part — a reference from a foreign document would
        // give either a kernel refusal or, worse, silently foreign geometry.
        var faces = new List<ksFaceDefinition>(command.FaceRefs.Count);
        foreach (var faceRef in command.FaceRefs)
        {
            if (!References.TryGet(faceRef, out var stored) || stored is null)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Ссылка на грань '{faceRef}' не найдена в реестре ссылок.",
                    RetryPolicy.ReacquireContext);
            }

            if (!string.Equals(stored.DocumentId, document.Id, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Грань '{faceRef}' принадлежит документу {stored.DocumentId}, а оболочка " +
                    $"строится в {document.Id}. Грани из чужой детали не переносятся.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["face_document_id"] = stored.DocumentId,
                        ["shell_document_id"] = document.Id,
                    });
            }

            if (stored.Revision != document.Revision)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Грань '{faceRef}' выпущена для ревизии {stored.Revision}, у документа уже " +
                    $"{document.Revision}.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["reference_revision"] = stored.Revision,
                        ["current_revision"] = document.Revision,
                    });
            }

            if (stored.Payload is not ksFaceDefinition face)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{faceRef}' указывает не на грань (kind={stored.Kind}).",
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            faces.Add(face);
        }

        var part = document.PartNow();
        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var facesBefore = CountFaces(document);

        if (part.NewEntity(ShellOperation) is not ksEntity entity
            || entity.GetDefinition() is not ksShellDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение оболочки не получено: NewEntity(43) вернул объект, у которого нет " +
                "ksShellDefinition. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        definition.thickness = command.ThicknessMm;
        definition.thinType = command.ThinDirection == ShellThinDirection.Inward;

        var attached = AttachFaces(definition, faces);
        if (attached != faces.Count)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Удаляемые грани присоединены не полностью: " + attached + " из " + faces.Count +
                ". FaceArray() не привёлся к ksEntityCollection либо Add() вернул false. " +
                "Оболочка не создавалась.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["attached_faces"] = attached,
                    ["requested_faces"] = faces.Count,
                });
        }

        var created = SafeBool(entity.Create);
        if (created != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Create() оболочки вернул " + (created is null ? "ошибку вызова" : "false") +
                ": тело не построено. Толщина, несовместимая с габаритом, даёт именно этот исход, " +
                "и он отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        SafeBool(entity.Update);
        part.RebuildModel();
        document.Document3D.RebuildDocument();

        var reference = References.Register("feature", document.Id, document.Revision, entity);
        BumpRevision(document, "shell.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Create() вернул true"),
            new("faces_attached", attached == faces.Count,
                "присоединено граней: " + attached + " из " + faces.Count),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
        };

        // Face count is a SECOND independent signal next to volume: the MEASURED empty-face-list
        // refusal looked like success by volume alone (the body did not change while Create/Update
        // answered true). Here the feature was applied and the face count grew.
        checks.Add(new NamedCheck("face_count_grew", facesAfter > facesBefore,
            Observed: facesBefore + " → " + facesAfter,
            Expected: "у оболочки появились внутренние грани полости"));

        // Shell CHANGES volume: material is removed inside (inward) or added outside (outward). The
        // "outward" direction with a removed face INCREASES volume, so the check reads "volume
        // changed", not "decreased" — otherwise an honest "outward" result would read as a refusal.
        var volumeChanged = volumeBefore is double before && volumeAfter is double after
                            && Math.Abs(after - before) > 1e-6;
        checks.Add(new NamedCheck("volume_changed", volumeChanged,
            Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
            Expected: "объём изменился относительно исходного тела"));

        var unverified = new List<string>();
        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var geometryConfirmed = false;
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));
        if (declared.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("expected_volume", declared));

            if (declared.IsUnverifiable)
            {
                unverified.Add(DeclaredExpectation.UnverifiableReason("объём после операции", declared));
            }
            else if (declared.IsNotConfirmed)
            {
                unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после операции", declared));
            }

            geometryConfirmed = declared.IsConfirmed;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия оболочки не подтверждена числом");
        }

        unverified.Add("tangent_faces_not_supported — IShell.SetFaces(Faces, TangentFaces) в API7 " +
                       "принимает признак касательных граней, но у API5 ksShellDefinition такого " +
                       "члена нет; выбор касательных граней группой (SM-13.shell.mode_tangent_faces) " +
                       "здесь не выражается и не выдаётся за выполненный");
        unverified.Add("variable_thickness_out_of_scope — переменная толщина по граням вне " +
                       "обязательного объёма этапа (OQ-A13)");
        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не " +
                       "проверяется; для этого существует отдельная приёмочная строка");

        return new ShellResult(
            ToDto(reference, entity.name),
            command.ThicknessMm,
            command.ThinDirection.ToString(),
            faces.Count,
            bodiesAfter,
            volumeAfter,
            part.GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>(),
            Warnings: DeclaredExpectation.Warnings(declared));
    }

    /// <summary>Attach the removed faces to the definition. Returns the attached count: fewer than
    /// requested means the route did not build, and that is a refusal, not "some of the faces".</summary>
    private static int AttachFaces(ksShellDefinition definition, IReadOnlyList<ksFaceDefinition> faces)
    {
        object? holder;
        try
        {
            holder = definition.FaceArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        var attached = 0;
        foreach (var face in faces)
        {
            if (SafeBool(() => collection.Add(face)) == true)
            {
                attached++;
            }
        }

        return attached;
    }

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM. The cost of a mistake is
    /// asymmetric: a spurious refusal is seen at once, while an accepted-and-ignored number survives
    /// to acceptance looking like a completed operation.</summary>
    private static void ValidateShellCommand(ShellCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: оболочка строится в конкретной детали.",
                RetryPolicy.Never);
        }

        if (!double.IsFinite(command.ThicknessMm) || command.ThicknessMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Толщина оболочки должна быть положительным конечным числом, а получено " +
                Num(command.ThicknessMm) + " мм.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["thickness_mm"] = command.ThicknessMm });
        }

        if (command.FaceRefs.Count == 0)
        {
            // The refusal is MEASURED, not cautious: an empty face list yields no closed shell on
            // either API5 or API7, though Create()/Update() return true. Accepting it would tell the
            // caller "shell built" where the body did not change.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней пуст. Измерено 20.09.2026 на обоих API: при пустом списке " +
                "операция принимается (Create/Update = true), но тело не меняется — объём остаётся " +
                "80000, число граней 6, как у исходного тела, тогда как открытая оболочка даёт " +
                "21632 при 11 гранях. Замкнутая оболочка пустым списком граней не выражается; " +
                "укажите хотя бы одну снимаемую грань.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["measured_closed_volume_mm3"] = 80000d,
                    ["measured_closed_face_count"] = 6,
                    ["measured_open_volume_mm3"] = 21632d,
                    ["measured_open_face_count"] = 11,
                });
        }

        if (command.FaceRefs.Count != command.FaceRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней содержит повторы: одна и та же грань указана дважды, " +
                "и «сколько граней снято» стало бы неотличимо от «сколько раз её назвали».",
                RetryPolicy.Never);
        }

        if (command.TangentFaces)
        {
            // The parameter IS declared in the schema (otherwise it would be invisible to the
            // product) but is NOT executed here: API5 <c>ksShellDefinition</c> has no "tangent faces"
            // member at all, and this tool takes the API5 route. Accepting true silently would return
            // success for work that never happened — exactly the defect the contract forbids.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "tangent_faces = true не выполняется: у API5 ksShellDefinition члена «касательные " +
                "грани» нет вовсе (маршрут этого инструмента — API5 o3d_shellOperation=43), а " +
                "касательные грани объявлены только у API7 IShell.SetFaces(Faces, TangentFaces). " +
                "Режим SM-13.shell.mode_tangent_faces в обязательный объём этого этапа не входит. " +
                "Передайте false или не передавайте поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["tangent_faces"] = true,
                    ["api5_member_present"] = false,
                    ["api7_member"] = "IShell.SetFaces(Faces, TangentFaces)",
                });
        }
    }
}
