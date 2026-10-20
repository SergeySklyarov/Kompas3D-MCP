using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Draft of faces — the two native families of SM-12 (profile <c>drafts-minimal-v1</c>).</summary>
/// <remarks>DOC: <c>imodelcontainer_inclines.html</c>, <c>iinclines_add.html</c> (API7 <c>Inclines.Add</c>
/// → <c>Faces</c>/<c>Plane</c>/<c>Angle</c>/<c>Direction</c> → <c>Update</c>), <c>obj3dtype.html</c>
/// (<c>o3d_incline = 42</c>), <c>idraftsfromedges_add.html</c> (base-line family, same shape).
/// MEASURED: the angle is in DEGREES and the product flag follows the normal of the BASE OBJECT, so the same
/// flag tilts a face the other way on a plane and on a face; the side is derived, never passed through.
/// INVARIANT: geometry is confirmed by volume, not by <c>Create()/Update() = true</c>.
/// History: docs/decisions/adapter-features.md#draft-route-and-direction</remarks>
public partial class Api5Session
{
    /// <summary><c>o3d_incline</c> — «Уклон» от основания.</summary>
    private const short InclineType = 42;

    /// <summary>Tolerance for reading the base orientation from the body's own extent, mm.</summary>
    private const double DraftBaseSideToleranceMm = 1e-6d;

    /// <summary>«Уклон» от основания: tilt flat faces of one body about a flat face or a plane.</summary>
    public DraftResult Draft(DraftCommand command)
    {
        ValidateDraftCommand(command);

        var document = RequireDocument(command.DocumentId);
        RequirePart(document);

        // LIMIT: a multi-body part is outside drafts-minimal-v1. Refused BEFORE COM, so "the faces of
        // two bodies" cannot become "the first body silently".
        var bodiesBefore = CountBodies(document);
        if (bodiesBefore != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Уклон строится на однотельном контексте, а в документе тел: {bodiesBefore}. " +
                "Многотельный контекст в профиль drafts-minimal-v1 не входит; признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = bodiesBefore });
        }

        var faces = new List<ksFaceDefinition>(command.FaceRefs.Count);
        foreach (var faceRef in command.FaceRefs)
        {
            var face = RequireFace(document, faceRef);
            if (PlanarPlacementOf(face) is null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Грань '{faceRef}' не плоская: уклоняется плоская грань, и её плоскость читается до " +
                    "мутации. Признак не создавался.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["face_ref"] = faceRef });
            }

            faces.Add(face);
        }

        var (basePayload, basePoint, baseNormal, baseKind) = ResolveDraftBase(document, command);

        // The product flag is DERIVED: the caller names the side relative to the BODY, the base
        // orientation decides what the product receives.
        var baseOut = BaseNormalPointsOutOfBody(document, basePoint, baseNormal);
        var flag = DraftRules.ProductDirection(command.Direction, baseOut);
        if (flag is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Направление уклона «{command.Direction}» не из словаря контракта: ожидается " +
                $"{DraftDirection.Outward} или {DraftDirection.Inward}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["direction"] = command.Direction });
        }

        var part = document.PartNow();
        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var revisionBefore = document.Revision;

        // ROUTE: the whole family goes through API7 — IInclines.Add → Faces/Plane/Angle/Direction →
        // Update. MEASURED: API5 SetPlane answers TRUE on a BUILT feature and leaves the body as it was,
        // so the base could not be edited; the same properties through API7 apply on creation and edit.
        // Mixing the two routes on one feature is forbidden, hence creation goes with them.
        // History: docs/decisions/adapter-features.md#draft-route-and-direction
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Уклон граней этим маршрутом не создаётся; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var faces7 = new object[faces.Count];
        for (var i = 0; i < faces.Count; i++)
        {
            if (bridge.TransferTo7(faces[i]) is not IModelObject face7)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Уклоняемая грань не перенесена в API7 как IModelObject (" +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                    "). Признак не создавался.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?> { ["face_ref"] = command.FaceRefs[i] });
            }

            faces7[i] = face7;
        }

        if (bridge.TransferTo7(basePayload) is not IModelObject base7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Основание уклона вида «" + baseKind + "» не перенесено в API7 как IModelObject (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                "). Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["base_kind"] = baseKind });
        }

        IIncline? incline = null;
        try
        {
            incline = container.Inclines?.Add() as IIncline;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IInclines.Add() прервался: " + ex.GetType().Name + ". Уклон не создавался.",
                RetryPolicy.ReacquireContext);
        }

        if (incline is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IInclines.Add() не вернул IIncline: коллекция уклонов не доступна. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            incline.Faces = faces7;
            incline.Plane = base7;
            incline.Angle = command.AngleDeg;
            incline.Direction = flag.Value;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства уклона не записаны: " + ex.GetType().Name + ". Признак остался в дереве без " +
                "параметров и назван в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var created = SafeBool(() => ((IModelObject)incline).Update());
        if (created != true)
        {
            var details = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(details, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() уклона вернул " + (created is null ? "ошибку вызова" : "false") +
                ": тело не перестроено. Основание, параллельное уклоняемой грани, и незамкнутая " +
                "поверхность после перестроения дают именно этот исход — это отказ, а не частичный " +
                "результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        RebuildApi7AndSettle(container, document);

        // The reference must point at the TREE element: an API7 object registered as a feature does not
        // resolve back into a feature and every later call would answer STALE_REFERENCE.
        var treeEntity = FindDraftInclineEntity(document, SafeModelName(incline));
        if (treeEntity is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Признак построен, но в дереве не найден элемент с его именем: ссылка на признак не "
                + "выдаётся, потому что ссылка на объект API7 не разрешается в признак. Признак остался "
                + "в документе.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var reference = References.Register("feature", document.Id, document.Revision, treeEntity);
        BumpRevision(document, "draft.create");
        RequireNoApi7FeatureError(document, incline, reference.Id, "Уклон");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Update() вернул true"),
            new("faces_attached", faces7.Length == faces.Count,
                "присоединено граней: " + faces7.Length + " из " + faces.Count),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
            new("face_count_unchanged", facesAfter == facesBefore,
                Observed: facesBefore + " → " + facesAfter,
                Expected: "уклон наклоняет грань, а не добавляет новую"),
        };

        var (angleReadBack, flagReadBack) = ReadInclineParameters(incline);
        checks.Add(new NamedCheck("angle_read_back",
            angleReadBack is double angle && Math.Abs(angle - command.AngleDeg) <= 1e-6,
            Observed: Num(angleReadBack), Expected: Num(command.AngleDeg)));
        checks.Add(new NamedCheck("direction_read_back", flagReadBack == flag,
            Observed: flagReadBack?.ToString(), Expected: flag.ToString()));

        var unverified = new List<string>();
        var geometryConfirmed = ConfirmDraftGeometry(
            command.ExpectedVolumeMm3, volumeBefore, volumeAfter, document, checks, unverified);

        return new DraftResult
        {
            FeatureRef = ToDto(reference, SafeModelName(incline)),
            Family = "draft",
            FaceCount = faces.Count,
            BaseKind = baseKind,
            AngleReadBackDeg = angleReadBack,
            DirectionReadBack = flagReadBack is bool readFlag
                ? DraftRules.ContractDirection(readFlag, baseOut)
                : null,
            ReverseReadBack = null,
            VolumeBeforeMm3 = volumeBefore,
            VolumeAfterMm3 = volumeAfter,
            BodiesBefore = bodiesBefore,
            BodiesAfter = bodiesAfter,
            RevisionBefore = revisionBefore,
            RevisionAfter = document.Revision,
            Verification = new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
        };
    }

    /// <summary>«Уклон от базовой линии» — API7 only: the family has no API5 definition.</summary>
    public DraftResult DraftFromEdges(DraftFromEdgesCommand command)
    {
        ValidateDraftFromEdgesCommand(command);

        var document = RequireDocument(command.DocumentId);
        RequirePart(document);

        var bodiesBefore = CountBodies(document);
        if (bodiesBefore != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Уклон от базовой линии строится на однотельном контексте, а в документе тел: " +
                $"{bodiesBefore}. Профиль drafts-minimal-v1 многотельный контекст не включает; " +
                "признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = bodiesBefore });
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". У семейства «уклон от базовой линии» нет определения в API5; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var edges = new List<object>(command.BaseLineEdgeRefs.Count);
        foreach (var edgeRef in command.BaseLineEdgeRefs)
        {
            edges.Add(RequireEdge(document, edgeRef));
        }

        var baseLine = bridge.TransferAllTo7(edges);
        if (baseLine is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Рёбра базовой линии не перенесены в API7 (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                ") — уклон не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var directionObject = ResolveDraftDirection(document, bridge, command);
        var sideFace = string.IsNullOrEmpty(command.SideFaceRef)
            ? null
            : RequireModelObject(document, bridge, command.SideFaceRef, "face", "side_face_ref");

        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var revisionBefore = document.Revision;

        IDraftFromEdges? draft;
        try
        {
            draft = container.DraftsFromEdges?.Add() as IDraftFromEdges;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IDraftsFromEdges.Add() прервался: " + ex.GetType().Name + ". Уклон не создавался.",
                RetryPolicy.ReacquireContext);
        }

        if (draft is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IDraftsFromEdges.Add() не вернул IDraftFromEdges: коллекция уклонов от базовой линии " +
                "недоступна. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            draft.BaseLine = baseLine;
            draft.DirectionObject = directionObject;
            if (sideFace is not null)
            {
                draft.SideFace = sideFace;
            }

            draft.Angle = command.AngleDeg;
            draft.Reverse = command.Reverse;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства уклона от базовой линии не записаны: " + ex.GetType().Name +
                ". Признак остался в дереве без параметров и назван в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var updated = SafeBool(() => ((IModelObject)draft).Update());
        if (updated != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() уклона от базовой линии вернул " +
                (updated is null ? "ошибку вызова" : "false") +
                ": тело не перестроено. Базовая линия, не прилегающая к уклоняемой грани, даёт именно " +
                "этот исход — это отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        RebuildApi7AndSettle(container, document);

        // The reference must point at the TREE element, not at the API7 object: an API7 object registered
        // as a feature resolves to DraftFromEdgesClass, and every later call answers STALE_REFERENCE.
        var treeEntity = FindDraftFromEdgesEntity(document, SafeModelName(draft));
        if (treeEntity is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Признак построен, но в дереве не найден элемент с его именем: ссылка на признак не "
                + "выдаётся, потому что ссылка на объект API7 не разрешается в признак. Признак остался "
                + "в документе.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var reference = References.Register("feature", document.Id, document.Revision, treeEntity);
        BumpRevision(document, "draft_from_edges.create");
        RequireNoApi7FeatureError(document, draft, reference.Id, "Уклон от базовой линии");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        var (angleReadBack, reverseReadBack) = ReadDraftFromEdgesParameters(draft);
        var checks = new List<NamedCheck>
        {
            new("operation_created", updated == true, "Update() вернул true"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
            new("face_count_unchanged", facesAfter == facesBefore,
                Observed: facesBefore + " → " + facesAfter,
                Expected: "уклон наклоняет грань, а не добавляет новую"),
            new("angle_read_back",
                angleReadBack is double angle && Math.Abs(angle - command.AngleDeg) <= 1e-6,
                Observed: Num(angleReadBack), Expected: Num(command.AngleDeg)),
            new("reverse_read_back", reverseReadBack == command.Reverse,
                Observed: reverseReadBack?.ToString(), Expected: command.Reverse.ToString()),
        };

        var unverified = new List<string>();
        var geometryConfirmed = ConfirmDraftGeometry(
            command.ExpectedVolumeMm3, volumeBefore, volumeAfter, document, checks, unverified);

        if (sideFace is null)
        {
            unverified.Add("side_face_default_not_named — без side_face_ref сторону выбирает система, и " +
                           "какую грань она взяла, этим маршрутом не читается");
        }

        return new DraftResult
        {
            FeatureRef = ToDto(reference, SafeModelName(draft)),
            Family = "draft_from_edges",
            FaceCount = command.BaseLineEdgeRefs.Count,
            BaseKind = null,
            AngleReadBackDeg = angleReadBack,
            DirectionReadBack = null,
            ReverseReadBack = reverseReadBack,
            VolumeBeforeMm3 = volumeBefore,
            VolumeAfterMm3 = volumeAfter,
            BodiesBefore = bodiesBefore,
            BodiesAfter = bodiesAfter,
            RevisionBefore = revisionBefore,
            RevisionAfter = document.Revision,
            Verification = new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
        };
    }

    /// <summary>The base object and the geometry of its plane: a flat face of the body, or a plane in
    /// the project's own <c>plane</c> form.</summary>
    private (object Payload, double[] Point, double[] Normal, string Kind) ResolveDraftBase(
        DocumentEntry document, DraftCommand command)
    {
        if (command.BaseFaceRef is { Length: > 0 } faceRef)
        {
            var face = RequireFace(document, faceRef);
            if (PlanarPlacementOf(face) is not { } planar)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Основание '{faceRef}' не плоская грань: уклон отсчитывается от плоскости. " +
                    "Признак не создавался.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["base_face_ref"] = faceRef });
            }

            return (face, ToArray(planar.Point), ToArray(planar.Normal), "face");
        }

        var plane = command.BasePlane!;
        // DOC: IIncline.Plane is an IModelObject (iincline_plane.html), so the base must reach the
        // feature as a model object. MEASURED: a reference carries either an API5 ksEntity or an API7
        // IPlane3D (auxiliary geometry) that comes back as no entity, while the SAME plane is visible
        // to API5 in the part's own enumeration - hence the lookup there by name and type.
        // History: docs/decisions/adapter-features.md#draft-edit-and-plane-base
        var entity = plane.Reference is { Length: > 0 } planeReference
            ? ResolvePlaneSupportObject(
                document, References.Require(planeReference, document.Id, document.Revision),
                planeReference, new List<string>())
            : ResolvePlaneEntity(document, plane);
        var bridge = BridgeFor(document);
        var (point, normal) = ReadPlanePlacement(document, bridge, entity, plane);
        if (point is null || normal is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Нормаль плоскости основания не прочитана: сторона уклона отсчитывается от неё, и " +
                "подставлять догадку вместо числа нельзя. Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["plane_base"] = plane.Base?.ToString(),
                    ["plane_ref"] = plane.Reference,
                    ["offset_mm"] = plane.OffsetMm,
                });
        }

        return (entity, point, normal, "plane");
    }

    /// <summary>The direction object of the base-line family: a plane in the project's form, or a
    /// reference to a flat face, a straight edge or an axis.</summary>
    private IModelObject ResolveDraftDirection(
        DocumentEntry document, Api7Bridge bridge, DraftFromEdgesCommand command)
    {
        if (command.DirectionPlane is { } plane)
        {
            var entity = ResolvePlaneEntity(document, plane);
            if (bridge.TransferTo7(entity) is IModelObject planeObject)
            {
                return planeObject;
            }

            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Плоскость направления не перенесена в API7 как IModelObject (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") + ") — признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        return RequireModelObject(document, bridge, command.DirectionRef, "direction", "direction_ref");
    }

    /// <summary>Does the base object's normal point AWAY from the body's material?</summary>
    /// <remarks>MEASURED: the product flag is read against the base normal, so the side relative to the
    /// body needs this orientation. The body's material is taken from its own bounding-box centre; a base
    /// through that centre is read as "material on the +normal side", which is the case measured on the
    /// XOY plane of the reference bar.
    /// LIMIT: the centre is an approximation for a non-convex body — the volume check catches a wrong
    /// side, and a mismatch is a refusal, never a silent success.</remarks>
    private bool BaseNormalPointsOutOfBody(DocumentEntry document, double[] point, double[] normal)
    {
        var box = document.PartNow().GetMainBody() is ksBody body ? ReadBodyBox(body) : null;
        if (box is null)
        {
            // Unreadable extent: the honest default is the documented one — the flag passes through.
            return true;
        }

        var centre = new[]
        {
            (box.MinMm[0] + box.MaxMm[0]) / 2d,
            (box.MinMm[1] + box.MaxMm[1]) / 2d,
            (box.MinMm[2] + box.MaxMm[2]) / 2d,
        };

        var length = Math.Sqrt((normal[0] * normal[0]) + (normal[1] * normal[1]) + (normal[2] * normal[2]));
        if (!double.IsFinite(length) || length <= 0d)
        {
            return true;
        }

        var dot = ((centre[0] - point[0]) * normal[0]
                   + (centre[1] - point[1]) * normal[1]
                   + (centre[2] - point[2]) * normal[2]) / length;

        return dot < -DraftBaseSideToleranceMm;
    }

    /// <summary>Point and unit normal of a base plane: from the request form for a standard plane, from
    /// the model for a reference.</summary>
    /// <remarks>INVARIANT: a standard plane is never read back. MEASURED: the offset plane created for the
    /// same call is not yet visible to API7, so <c>TransferTo7</c> of it gives no <c>IPlane3D</c> and the
    /// side stayed unknown; the axis the caller named is known without the model.</remarks>
    private (double[]? Point, double[]? Normal) ReadPlanePlacement(
        DocumentEntry document, Api7Bridge bridge, object payload, PlaneRefDto plane)
    {
        if (plane.Reference is null && plane.Base is { } standard)
        {
            return DraftRules.StandardPlanePlacement(standard, plane.OffsetMm);
        }

        try
        {
            if (bridge.TransferTo7(payload) is not IPlane3D plane7)
            {
                return (null, null);
            }

            var read = Api7AuxGeometry.ReadPlane(plane7, -1);
            return (read.PointMm, read.DirectionMm);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null);
        }
    }

    /// <summary>Angle and product flag read back from the API7 feature.</summary>
    /// <remarks>INVARIANT: the read goes through the SAME route that writes — API5 and API7 are never
    /// called on one feature, so the definition object is not consulted here.</remarks>
    private static (double? Angle, bool? Flag) ReadInclineParameters(IIncline incline)
    {
        try
        {
            return (incline.Angle, incline.Direction);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            return (null, null);
        }
    }

    /// <summary>Angle and <c>Reverse</c> read back from the API7 feature.</summary>
    private static (double? Angle, bool? Reverse) ReadDraftFromEdgesParameters(IDraftFromEdges draft)
    {
        try
        {
            return (draft.Angle, draft.Reverse);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            return (null, null);
        }
    }

    /// <summary>The geometry check shared by both families: the declared expectation decides, an
    /// unchanged volume with an applied feature is a refusal unless the expectation says otherwise.</summary>
    /// <remarks>INVARIANT: <c>Create()/Update() = true</c> with an unchanged volume is not a success — the
    /// lesson of the shell's empty face list. The one exception is a base in the middle of the body, where
    /// the wedges cancel by construction; there the caller's declared volume is what confirms.</remarks>
    private bool ConfirmDraftGeometry(
        double? expectedVolumeMm3,
        double? volumeBefore,
        double? volumeAfter,
        DocumentEntry document,
        List<NamedCheck> checks,
        List<string> unverified)
    {
        var declared = DeclaredExpectation.Evaluate(expectedVolumeMm3, volumeAfter, () => ReadVolume(document));
        if (declared.IsDeclared)
        {
            checks.Add(DeclaredExpectation.Check("expected_volume", declared));

            if (declared.IsUnverifiable)
            {
                unverified.Add(DeclaredExpectation.UnverifiableReason("объём после уклона", declared));
            }
            else if (declared.IsNotConfirmed)
            {
                unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после уклона", declared));
            }

            return declared.IsConfirmed;
        }

        var changed = volumeBefore is double before && volumeAfter is double after
                      && Math.Abs(after - before) > 1e-6;
        checks.Add(new NamedCheck("volume_changed", changed,
            Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
            Expected: "объём изменился; неизменный объём при созданном признаке — отказ"));
        unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма геометрия " +
                       "уклона не подтверждена числом");
        return false;
    }

    /// <summary>The tree element of a draft built through API7, matched by the name the feature carries.</summary>
    /// <remarks>MEASURED: the API7 object is not a feature reference — it resolves to the wrapper class and
    /// every later call answers STALE_REFERENCE — so the tree element with the same name is taken instead.
    /// </remarks>
    private static ksEntity? FindDraftInclineEntity(DocumentEntry document, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            if (document.PartNow().EntityCollection(
                    KompasObjectTypes.Of(KompasObjectTypes.OperationElement)) is not ksEntityCollection collection)
            {
                return null;
            }

            ksEntity? found = null;
            var matches = 0;
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity
                    && entity.type == KompasObjectTypes.Incline
                    && string.Equals(entity.name, name, StringComparison.Ordinal))
                {
                    found = entity;
                    matches++;
                }
            }

            return matches == 1 ? found : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // A tree that cannot be walked is reported by the caller as "not found".
            return null;
        }
    }

    /// <summary>An edge by registry reference — with the same document and revision checks as a face.</summary>
    private ksEdgeDefinition RequireEdge(DocumentEntry document, string edgeRef)
    {
        var stored = References.Require(edgeRef, document.Id, document.Revision);
        if (stored.Payload is not ksEdgeDefinition edge)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{edgeRef}' указывает не на ребро (kind={stored.Kind}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        return edge;
    }

    private static double[] ToArray(IReadOnlyList<double> values) => new[]
    {
        values[0], values[1], values[2],
    };

    private static string? SafeModelName(IModelObject? modelObject)
    {
        try
        {
            return modelObject?.Name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM.</summary>
    private static void ValidateDraftCommand(DraftCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: уклон строится в конкретной детали.",
                RetryPolicy.Never);
        }

        if (command.FaceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список уклоняемых граней пуст: уклон без грани не выражается. Укажите хотя бы одну " +
                "плоскую грань.",
                RetryPolicy.Never);
        }

        if (command.FaceRefs.Count != command.FaceRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список граней содержит повторы: одна и та же грань указана дважды, и «сколько граней " +
                "уклонено» стало бы неотличимо от «сколько раз её назвали».",
                RetryPolicy.Never);
        }

        if (!DraftRules.SingleBase(command.BaseFaceRef, command.BasePlane is null ? null : "plane"))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Основание задаётся РОВНО одной формой: base_face_ref (плоская грань) либо base_plane " +
                "(плоскость). Обе сразу или ни одной — отказ.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["has_base_face_ref"] = command.BaseFaceRef is not null,
                    ["has_base_plane"] = command.BasePlane is not null,
                });
        }

        if (!DraftRules.AngleInRange(command.AngleDeg))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона должен быть конечным числом, 0 < |angle_deg| < 90; получено " +
                Num(command.AngleDeg) + ". Граница контракта, а не API: 0° ничего не меняет, 90° " +
                "вырождает грань.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["angle_deg"] = command.AngleDeg });
        }

        if (DraftRules.ProductDirection(command.Direction, baseNormalPointsOutOfBody: true) is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Направление «{command.Direction}» не из словаря контракта: ожидается " +
                $"{DraftDirection.Outward} или {DraftDirection.Inward}. Умолчания нет — собственное " +
                "умолчание продукта на плоскости и на грани означает разное.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["direction"] = command.Direction });
        }
    }

    /// <summary>"Field ↔ capability" rules of the base-line family, before COM.</summary>
    private static void ValidateDraftFromEdgesCommand(DraftFromEdgesCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: уклон строится в конкретной детали.",
                RetryPolicy.Never);
        }

        if (command.BaseLineEdgeRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Базовая линия пуста: нужен хотя бы один рёберный объект — ребро или цепочка рёбер.",
                RetryPolicy.Never);
        }

        if (!DraftRules.SingleBase(command.DirectionRef, command.DirectionPlane is null ? null : "plane"))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Направление задаётся РОВНО одной формой: direction_ref (грань, ребро, ось) либо " +
                "direction_plane (плоскость). Обе сразу или ни одной — отказ.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["has_direction_ref"] = command.DirectionRef is not null,
                    ["has_direction_plane"] = command.DirectionPlane is not null,
                });
        }

        if (!DraftRules.AngleInRange(command.AngleDeg))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона должен быть конечным числом, 0 < |angle_deg| < 90; получено " +
                Num(command.AngleDeg) + ". Граница контракта, а не API.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["angle_deg"] = command.AngleDeg });
        }
    }
}
