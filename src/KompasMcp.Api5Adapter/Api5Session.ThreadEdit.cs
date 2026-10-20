using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Editing a thread designation — the family «Резьба» (SM-08) through
/// <c>kompas_update_feature</c>.</summary>
/// <remarks>INVARIANT: the edit writes the SAME feature and calls <c>Update()</c>; the confirmation is
/// every value READ BACK, not the return code — the volume is expected to stay, because a designation is
/// a label and not geometry.
/// LIMIT: the base object and the borders do not change — a request for them is refused by name before
/// any write.
/// History: docs/decisions/adapter-features.md#thread-route</remarks>
public partial class Api5Session
{
    /// <summary>Edit the parameters of an existing thread designation.</summary>
    private UpdateFeatureResult UpdateThreadFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignThreadEditFields(command);

        // LIMIT: a new base or new borders are REFUSED, not attempted. The designation keeps the base it
        // was built on; expressing another base means creating the designation again.
        if (command.BaseFaceRef is not null || command.BasePlane is not null
            || command.InitialBorderRef is not null || command.FinalBorderRef is not null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Базовый объект и границы построенного обозначения резьбы не меняются: в профиль "
                + "threads-minimal-v1 входит правка диаметра, шага, длины, левой резьбы и авто-флагов. "
                + "Модель не изменена.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "thread_base_edit_unsupported",
                    ["has_base_face_ref"] = command.BaseFaceRef is not null,
                    ["has_base_plane"] = command.BasePlane is not null,
                    ["has_initial_border_ref"] = command.InitialBorderRef is not null,
                    ["has_final_border_ref"] = command.FinalBorderRef is not null,
                    ["remedy"] = "удалить обозначение и создать его с нужным базовым объектом заново",
                });
        }

        if (!ThreadRules.EditValueOrAuto(command.NominalDiameterMm, command.AutoDiameter)
            || !ThreadRules.EditValueOrAuto(command.LengthMm, command.AutoLength))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка резьбы: значение и авто-флаг одного поля вместе не задаются (при автоопределении "
                + "названное число не используется), а число должно быть конечным и > 0.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["nominal_diameter_mm"] = command.NominalDiameterMm,
                    ["auto_diameter"] = command.AutoDiameter,
                    ["length_mm"] = command.LengthMm,
                    ["auto_length"] = command.AutoLength,
                });
        }

        if (command.PitchMm is not null && !ThreadRules.PositiveFinite(command.PitchMm))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг резьбы pitch_mm при правке должен быть конечным числом > 0: профиль не подбирает "
                + "шаг по стандарту.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["pitch_mm"] = command.PitchMm });
        }

        // ROUTE: the same API7 object the creation uses — API5 and API7 are never called on one feature.
        var thread = FindThreadObject(document, entity, out var parameters, out var reason);
        if (thread is null || parameters is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Объект IThread этого обозначения не найден: " + reason + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "thread_object_not_matched",
                    ["tree_type"] = entity.type,
                });
        }

        var before = ReadThreadParameters(thread, parameters);
        var container = BridgeFor(document)
            .ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился при правке резьбы: "
                + (BridgeFor(document).BridgeFailure ?? "причина не известна") + ". Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var targetDiameter = command.NominalDiameterMm ?? before.Diameter;
        var targetPitch = command.PitchMm ?? before.Pitch;
        var targetLength = command.LengthMm ?? before.Length;
        var targetLeft = command.LeftHand ?? before.LeftHand;
        var targetAutoDiameter = command.AutoDiameter ?? before.AutoDiameter;
        var targetAutoLength = command.AutoLength ?? before.AutoLength;

        try
        {
            if (command.NominalDiameterMm is not null)
            {
                parameters.Diameter = command.NominalDiameterMm.Value;
            }

            if (command.PitchMm is not null)
            {
                parameters.Pitch = command.PitchMm.Value;
            }

            if (command.AutoDiameter is not null)
            {
                thread.AutoDiameter = command.AutoDiameter.Value;
            }

            if (command.AutoLength is not null)
            {
                thread.AutoLenght = command.AutoLength.Value;
            }

            if (command.LengthMm is not null)
            {
                thread.Lenght = command.LengthMm.Value;
            }

            if (command.LeftHand is not null)
            {
                thread.LeftThread = command.LeftHand.Value;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства резьбы не записаны: " + ex.GetType().Name + ". Признак изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var updated = SafeBool(() => ((IModelObject)thread).Update());
        if (updated != true)
        {
            var refusalDetails = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(refusalDetails, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() обозначения резьбы вернул "
                + (updated is null ? "ошибку вызова" : "false") + ": записанные параметры не применены к "
                + "модели. Признак изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: refusalDetails);
        }

        RebuildApi7AndSettle(container, document);
        BumpRevision(document, "thread.update");

        var reference = References.Require(command.FeatureRef, document.Id, document.Revision);
        var stateAfter = ReadFeatureState(entity);
        RequireNoApi7FeatureError(document, thread, command.FeatureRef, "Резьба (правка)");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var after = ReadThreadParameters(thread, parameters);

        var checks = new List<NamedCheck>
        {
            new("update_returned", updated == true, "Update() вернул true"),
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                && featuresBefore == featuresAfter,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            // A designation changes no geometry: an unchanged volume is the expected outcome here.
            new("volume_unchanged", Same(volumeBefore, volumeAfter),
                Observed: Num(volumeAfter), Expected: Num(volumeBefore) + " (обозначение не меняет тело)"),
            new("diameter_read_back", command.AutoDiameter == true || Same(after.Diameter, targetDiameter),
                Observed: Num(after.Diameter),
                Expected: command.AutoDiameter == true ? "автоопределение" : Num(targetDiameter)),
            new("pitch_read_back", Same(after.Pitch, targetPitch),
                Observed: Num(after.Pitch), Expected: Num(targetPitch)),
            new("length_read_back", command.AutoLength == true || Same(after.Length, targetLength),
                Observed: Num(after.Length),
                Expected: command.AutoLength == true ? "автоопределение по границам" : Num(targetLength)),
            new("left_hand_read_back", after.LeftHand == targetLeft,
                Observed: after.LeftHand?.ToString(), Expected: targetLeft?.ToString()),
            new("auto_flags_read_back",
                (targetAutoDiameter is null || after.AutoDiameter == targetAutoDiameter)
                && (targetAutoLength is null || after.AutoLength == targetAutoLength),
                Observed: after.AutoDiameter + "/" + after.AutoLength,
                Expected: targetAutoDiameter + "/" + targetAutoLength),
        };

        var unverified = new List<string>
        {
            "винтовая геометрия не строится: это условное обозначение, а не резьба как поверхность; "
            + "неизменный объём тела — ожидаемый исход, а не признак отказа",
        };

        if (after.Diameter is null || after.Pitch is null || after.Length is null)
        {
            unverified.Add("thread_params_partly_unreadable — после правки с модели не прочитались: "
                + (after.Diameter is null ? "Diameter " : string.Empty)
                + (after.Pitch is null ? "Pitch " : string.Empty)
                + (after.Length is null ? "Lenght" : string.Empty));
        }

        return new UpdateFeatureResult(
            ToDto(reference, stateAfter.Name),
            ThreadFamily,
            true,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                checks.TrueForAll(c => c.Passed)
                    ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified),
            ThreadReadBack: new ThreadDto(
                after.Diameter, after.Pitch, after.Length, after.Internal, after.LeftHand,
                after.AutoDiameter, after.AutoLength, BaseKindOf(thread)),
            Warnings: Array.Empty<string>());
    }

    /// <summary>Fields a thread edit accepts. Everything else is refused, not ignored.</summary>
    private static readonly string[] ThreadEditFields =
    {
        "nominal_diameter_mm", "pitch_mm", "length_mm", "auto_diameter", "auto_length", "left_hand",
    };

    /// <summary>Fields of other families are refused by name, not ignored.</summary>
    private static void RejectForeignThreadEditFields(UpdateFeatureCommand command)
    {
        var foreign = NonB5EditFields
            .Concat(B5EditFields)
            .Where(f => !ThreadEditFields.Contains(f.Name, StringComparer.Ordinal))
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
            "Признак — обозначение резьбы: к нему применяются только nominal_diameter_mm, pitch_mm, "
            + "length_mm, auto_diameter, auto_length и left_hand. Переданные поля других семейств не "
            + "игнорируются, а отвергаются: принятое и проигнорированное число доживает до приёмки, "
            + "выглядя как выполненная правка.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["family"] = ThreadFamily,
                ["foreign_fields"] = foreign,
            });
    }
}
