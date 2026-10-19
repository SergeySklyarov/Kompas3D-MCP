using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Reading and editing «Уклон от базовой линии» (SM-12) — the API7-only family.</summary>
/// <remarks>INVARIANT: the feature is matched to the API7 object BY NAME: the tree element carries no
/// definition (<c>GetDefinition()</c> gives nothing usable), so the API5 entity is only a handle, and the
/// parameters live on <c>IDraftFromEdges</c>.
/// LIMIT: the base line, the direction object and the side face are NOT editable here — a request that
/// names them is refused by name, not ignored.
/// History: docs/decisions/adapter-features.md#draft-from-edges-read-and-edit</remarks>
public partial class Api5Session
{
    /// <summary>Family name of a draft from a base line in read and edit responses.</summary>
    private const string DraftFromEdgesFamily = "draft_from_edges";

    /// <summary>The API7 object of the feature the reference points at, or null with the reason.</summary>
    /// <remarks>MEASURED: after <c>IDraftsFromEdges.Add</c> the tree shows type 644 and the collection
    /// holds one object whose <c>Name</c> equals the tree element's name («Уклон от базовой линии:1»).
    /// A name that matches twice is not an identifier, and the read is refused instead of picking one.
    /// </remarks>
    private IDraftFromEdges? FindDraftFromEdges(
        DocumentEntry document, ksEntity entity, out IModelContainer? container, out string? reason)
    {
        reason = null;
        container = null;

        var bridge = BridgeFor(document);
        container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            reason = "api7_container_unavailable — маршрут API7 не построился: "
                + (bridge.BridgeFailure ?? "причина не известна");
            return null;
        }

        var name = SafeEntityName(entity);
        if (string.IsNullOrEmpty(name))
        {
            reason = "draft_from_edges_name_unreadable — имя элемента дерева не прочиталось, а объект "
                + "API7 сопоставляется только по имени";
            return null;
        }

        IDraftsFromEdges? collection;
        try
        {
            collection = container.DraftsFromEdges;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            reason = "draft_from_edges_collection_unavailable — IModelContainer.DraftsFromEdges не "
                + "отвечает: " + ex.GetType().Name;
            return null;
        }

        if (collection is null)
        {
            reason = "draft_from_edges_collection_unavailable — IModelContainer.DraftsFromEdges пуст";
            return null;
        }

        var count = SafeInt(() => collection.Count) ?? 0;
        IDraftFromEdges? found = null;
        var matches = 0;
        for (var i = 0; i < count; i++)
        {
            if (collection.DraftFromEdges[(object)i] is not IDraftFromEdges item)
            {
                continue;
            }

            if (!string.Equals(SafeModelName(item), name, StringComparison.Ordinal))
            {
                continue;
            }

            found = item;
            matches++;
        }

        if (matches == 1)
        {
            return found;
        }

        reason = matches == 0
            ? "draft_from_edges_object_not_found — в IDraftsFromEdges нет объекта с именем «" + name
              + "» (объектов в коллекции: " + count.ToString(CultureInfo.InvariantCulture) + ")"
            : "draft_from_edges_object_ambiguous — имя «" + name + "» отвечает " + matches
              + " объектам: имя не является идентификатором";
        return null;
    }

    /// <summary>Angle, <c>Reverse</c> and the base-line edge count read from the model.</summary>
    private DraftDto? ReadDraftFromEdgesFeature(DocumentEntry document, ksEntity entity, out string? reason)
    {
        var draft = FindDraftFromEdges(document, entity, out _, out reason);
        if (draft is null)
        {
            return null;
        }

        var (angle, reverse, edges) = ReadDraftFromEdgesFull(draft);
        if (angle is null || reverse is null)
        {
            reason = "draft_from_edges_params_partly_unreadable — с IDraftFromEdges не прочиталось: "
                + (angle is null ? "Angle " : string.Empty)
                + (reverse is null ? "Reverse" : string.Empty);
        }
        else if (edges is null)
        {
            reason = "draft_from_edges_base_line_not_read — BaseLine не прочиталась как массив: "
                + "число рёбер базовой линии не публикуется";
        }

        // The side relative to the body is NOT published: the API7 route does not say which side of the
        // base line the material went to, and a guess here would be indistinguishable from a measurement.
        return new DraftDto(angle, null, null, null, reverse, edges);
    }

    /// <summary>Edit of a draft from a base line: the angle and <c>Reverse</c>, nothing else.</summary>
    private UpdateFeatureResult UpdateDraftFromEdgesFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignDraftFromEdgesFields(command);

        if (command.AngleDeg is null && command.Reverse is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Не указано ни одного параметра: у уклона от базовой линии правятся angle_deg и reverse. "
                + "Признак не изменён.",
                RetryPolicy.Never);
        }

        var angle = command.AngleDeg;
        if (angle is double given && !DraftRules.AngleInRange(given))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона должен быть конечным числом, 0 < |angle_deg| < 90; получено " + Num(angle)
                + ". Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["angle_deg"] = angle });
        }

        var draft = FindDraftFromEdges(document, entity, out var container, out var reason);
        if (draft is null || container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Объект IDraftFromEdges этого признака не найден: " + reason + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "draft_from_edges_object_not_matched",
                    ["tree_type"] = entity.type,
                });
        }

        var (angleBefore, reverseBefore, _) = ReadDraftFromEdgesFull(draft);
        var targetAngle = angle ?? angleBefore;
        if (targetAngle is not double applyAngle)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Угол не собрался: в запросе angle_deg не задан, а с модели угол не прочитался. "
                + "Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var targetReverse = command.Reverse ?? reverseBefore;
        if (targetReverse is not bool applyReverse)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Reverse не собрался: в запросе поле не задано, а с модели не прочиталось. "
                + "Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        if (!WriteDraftFromEdgesParameters(draft, applyAngle, applyReverse))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись Angle/Reverse в IDraftFromEdges не состоялась: признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var updated = SafeBool(() => ((IModelObject)draft).Update());
        if (updated != true)
        {
            var refusalDetails = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(refusalDetails, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() уклона от базовой линии вернул " + (updated is null ? "ошибку вызова" : "false")
                + ": записанные угол и сторона не применены к модели. Признак изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: refusalDetails);
        }

        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "draft_from_edges.update");

        var reference = References.Require(command.FeatureRef, document.Id, document.Revision);
        RequireNoApi7FeatureError(document, draft, reference.Id, "Уклон от базовой линии (правка)");

        var stateAfter = ReadFeatureState(entity);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var (angleReadBack, reverseReadBack, edgesReadBack) = ReadDraftFromEdgesFull(draft);

        var checks = new List<NamedCheck>
        {
            new("update_returned", updated == true, "Update() вернул true"),
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                && featuresBefore == featuresAfter,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            new("angle_read_back",
                angleReadBack is double readAngle && Math.Abs(readAngle - applyAngle) <= 1e-6,
                Observed: Num(angleReadBack), Expected: Num(applyAngle)),
            new("reverse_read_back", reverseReadBack == applyReverse,
                Observed: reverseReadBack?.ToString(), Expected: applyReverse.ToString()),
        };

        if (command.AngleDeg is not null && command.Reverse is null)
        {
            checks.Add(new NamedCheck("reverse_kept", reverseReadBack == reverseBefore,
                Observed: reverseReadBack?.ToString(), Expected: reverseBefore?.ToString()));
        }

        var unverified = new List<string>
        {
            "draft_from_edges_base_not_edited — базовая линия, направляющий объект и сторона этим "
            + "маршрутом не правятся и в наряд не входят",
        };

        var geometryConfirmed = ConfirmDraftGeometry(
            command.ExpectedVolumeMm3, volumeBefore, volumeAfter, document, checks, unverified);

        return new UpdateFeatureResult(
            ToDto(reference, stateAfter.Name),
            DraftFromEdgesFamily,
            true,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            AngleReadBackDeg: angleReadBack,
            ReverseReadBack: reverseReadBack,
            Warnings: Array.Empty<string>());
    }

    /// <summary>Fields of other families are refused by name: this family edits the angle and
    /// <c>Reverse</c> only.</summary>
    private static void RejectForeignDraftFromEdgesFields(UpdateFeatureCommand command)
    {
        var foreign = NonB5EditFields
            .Concat(B5EditFields)
            .Where(f => f.Name is not ("angle_deg" or "reverse"))
            .Where(f => f.Read(command) is not null)
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (foreign.Count == 0)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Признак — уклон от базовой линии: правятся только angle_deg и reverse. Базовая линия, "
            + "направляющий объект и сторона в этот наряд не входят, а поля других семейств не "
            + "игнорируются: принятое и проигнорированное число доживает до приёмки, выглядя как "
            + "выполненная правка.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["family"] = DraftFromEdgesFamily,
                ["foreign_fields"] = foreign,
            });
    }

    /// <summary>Angle and <c>Reverse</c> written together: they are one mode of this family.</summary>
    private static bool WriteDraftFromEdgesParameters(IDraftFromEdges draft, double angle, bool reverse)
    {
        try
        {
            draft.Angle = angle;
            draft.Reverse = reverse;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Angle, <c>Reverse</c> and the number of base-line edges, each null when unread.</summary>
    private static (double? Angle, bool? Reverse, int? Edges) ReadDraftFromEdgesFull(IDraftFromEdges draft)
    {
        double? angle = null;
        bool? reverse = null;
        int? edges = null;
        try
        {
            angle = draft.Angle;
            reverse = draft.Reverse;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            // Both fields are read in one try: the reason is named by the caller as "partly unreadable".
        }

        try
        {
            edges = draft.BaseLine is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            edges = null;
        }

        return (angle, reverse, edges);
    }

    /// <summary>Name of the tree element, or null when the model does not give one.</summary>
    private static string? SafeEntityName(ksEntity entity)
    {
        try
        {
            return entity.name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}
