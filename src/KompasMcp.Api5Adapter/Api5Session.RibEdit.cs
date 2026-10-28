using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Editing a rib — the family «Ребро жёсткости» (SM-14) through <c>kompas_update_feature</c>.</summary>
/// <remarks>INVARIANT: the edit happens on the SAME feature — re-creation would give another name, tree
/// position and dependency set. INVARIANT: the confirmation is the volume, not <c>Update() = true</c>; an
/// edit that leaves the body unchanged is a refusal, because a draft and a thickness both change material.
/// LIMIT: <c>side</c> and <c>segment_index</c> are refused by name — the documented setter exists, but no
/// numeric confirmation separates "applied" from "accepted" for them.
/// History: docs/decisions/ribs.md#edit</remarks>
public partial class Api5Session
{
    /// <summary>Rib edit: draft angle, thickness and the thickness mode, in place.</summary>
    private UpdateFeatureResult UpdateRibFeature(
        DocumentEntry document,
        ksEntity entity,
        ksRibDefinition definition,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignB5EditFields(command, RibFamily, "draft_angle_deg, thickness_mm, symmetric",
            "thickness_mm");

        RefuseUnconfirmedRibEdit(command);

        var current = ReadRibState(definition);
        var angle = command.DraftAngleDeg ?? current.AngleDeg;
        var thickness = command.ThicknessMm ?? current.NormalThicknessMm;
        var symmetric = command.Symmetric ?? current.ThinType == ThinTypeBoth;

        if (angle is not double targetAngle || thickness is not double targetThickness)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Параметры ребра не собраны: с модели не прочиталось "
                + (angle is null ? "angle " : string.Empty)
                + (thickness is null ? "толщина " : string.Empty)
                + "и в запросе эта половина не задана. Писать половину режима значило бы сделать "
                + "«изменилось ровно запрошенное» неотличимым от «изменилось ещё и это». "
                + "Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["angle_read"] = current.AngleDeg,
                    ["thickness_read"] = current.NormalThicknessMm,
                    ["thin_type_read"] = current.ThinType,
                });
        }

        ValidateRibEditValues(targetAngle, targetThickness);

        // A call that asks for the values the model already holds is a no-op, not an edit: reported as
        // success it would be indistinguishable from an applied change.
        if (Math.Abs(targetAngle - (current.AngleDeg ?? double.NaN)) <= 1e-9
            && Math.Abs(targetThickness - (current.NormalThicknessMm ?? double.NaN)) <= 1e-9
            && symmetric == (current.ThinType == ThinTypeBoth))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Запрошенные параметры совпадают с прочитанными: угол "
                + Num(current.AngleDeg) + "°, толщина " + Num(current.NormalThicknessMm)
                + " мм, режим " + (symmetric ? "симметричный" : "односторонний") + ". Менять нечего, "
                + "и выдать это за выполненную правку нельзя.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["angle_deg"] = current.AngleDeg,
                    ["thickness_mm"] = current.NormalThicknessMm,
                    ["symmetric"] = symmetric,
                });
        }

        var thinType = symmetric ? ThinTypeBoth : ThinTypeNormal;
        var reverse = symmetric ? targetThickness : 0d;

        definition.angle = targetAngle;
        if (current.SegmentIndex is int index)
        {
            definition.index = index;
        }

        var thinAccepted = SafeBool(() => definition.SetThinParam(thinType, targetThickness, reverse)) == true;

        ClearKompasResult(document);
        var updated = SafeBool(entity.Update) == true;
        if (!updated)
        {
            var refusalDetails = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(refusalDetails, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: записанные параметры не применены к модели. "
                + "Признак изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: refusalDetails);
        }

        var part = document.PartNow();
        part.RebuildModel();
        SettleAfterMutation(document);
        BumpRevision(document, "rib.update");

        RequireNoFeatureError(document, entity, command.FeatureRef, "Правка ребра");

        var after = entity.GetDefinition() as ksRibDefinition;
        var readBack = after is null ? null : ReadRibState(after);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                          && featuresBefore == featuresAfter;

        var checks = new List<NamedCheck>
        {
            new("update_accepted", updated, "ksEntity.Update() вернул true"),
            new("feature_identity_preserved", sameFeature,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            new("thin_param_accepted", thinAccepted,
                "SetThinParam(" + thinType + ", " + Num(targetThickness) + ", " + Num(reverse)
                + ") вернул true"),
            new("angle_read_back",
                readBack?.AngleDeg is double readAngle && Math.Abs(readAngle - targetAngle) <= 1e-6,
                Observed: Num(readBack?.AngleDeg) + "°", Expected: Num(targetAngle) + "°"),
            new("thickness_read_back",
                readBack?.NormalThicknessMm is double readThickness
                && Math.Abs(readThickness - targetThickness) <= 1e-6,
                Observed: Num(readBack?.NormalThicknessMm) + " мм", Expected: Num(targetThickness) + " мм"),
            new("thin_type_read_back", readBack?.ThinType == thinType,
                Observed: Num(readBack?.ThinType) ?? "не прочитано",
                Expected: thinType.ToString(CultureInfo.InvariantCulture)),
        };

        if (symmetric)
        {
            checks.Add(new NamedCheck("reverse_thickness_read_back",
                readBack?.ReverseThicknessMm is double readReverse
                && Math.Abs(readReverse - targetThickness) <= 1e-6,
                Observed: Num(readBack?.ReverseThicknessMm) + " мм",
                Expected: Num(targetThickness) + " мм"));
        }

        var unverified = new List<string>();
        double? delta = volumeBefore is double before && volumeAfter is double grown ? grown - before : null;

        // The analytic expectation exists only without a draft: a draft ADDS material, so the area times
        // the thickness stops being the answer and only "the body changed" can be claimed.
        var area = ReadRibChainArea(document, after);
        var draft = Math.Abs(targetAngle) > 1e-9 || Math.Abs(current.AngleDeg ?? 0d) > 1e-9;
        double? expectedDelta = draft || area is null
            ? null
            : RibChain.ExpectedVolumeMm3(area, targetThickness, symmetric)
              - RibChain.ExpectedVolumeMm3(area, current.NormalThicknessMm ?? targetThickness,
                  current.ThinType == ThinTypeBoth);

        if (expectedDelta is double expected)
        {
            var matches = Math.Abs((delta ?? double.NaN) - expected)
                          <= RibChain.VolumeTolerance * Math.Max(Math.Abs(expected), 1d);
            checks.Add(new NamedCheck("volume_matches_expected_delta", matches,
                Observed: Num(delta) + " мм³",
                Expected: Num(expected) + " мм³ = площадь " + Num(area) + " мм² × ("
                          + Num(targetThickness) + " − " + Num(current.NormalThicknessMm) + ") мм"
                          + (symmetric || current.ThinType == ThinTypeBoth ? ", с удвоением" : string.Empty)
                          + ", допуск 1 %"));
            if (!matches)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Изменение объёма " + Num(delta) + " мм³ не совпало с ожидаемым " + Num(expected)
                    + " мм³ (площадь цепочки × разность толщин, допуск 1 %): толщина или режим "
                    + "применены не так, как прочитаны. Признак изменён.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["named_code"] = "GEOMETRY_FAILED",
                        ["volume_delta_mm3"] = delta,
                        ["expected_delta_mm3"] = expected,
                        ["contour_area_mm2"] = area,
                    });
            }
        }
        else if (delta is double changed && Math.Abs(changed) <= 1e-6)
        {
            // INVARIANT: a draft ADDS material, so an edit that leaves the body unchanged is a refusal.
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка принята (Update() = true), но объём не изменился: прирост " + Num(changed)
                + " мм³. Уклон и толщина меняют материал, поэтому неизменное тело — отказ, а не "
                + "успех без изменения.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["volume_delta_mm3"] = changed,
                    ["angle_deg"] = targetAngle,
                    ["thickness_mm"] = targetThickness,
                });
        }
        else if (draft)
        {
            unverified.Add("volume_expectation_skipped_due_to_draft — при уклоне "
                + Num(targetAngle) + "° прирост не равен площади × толщину (измерено: уклон ДОБАВЛЯЕТ "
                + "материал), поэтому подтверждено только «объём изменился»: "
                + Num(delta) + " мм³");
        }
        else
        {
            unverified.Add("contour_not_analysed — цепочка ребра не прочитана из модели, поэтому "
                + "ожидаемое изменение объёма не посчитано: подтверждено только «объём изменился»");
        }

        var geometryConfirmed = checks.All(check => check.Passed) && delta is not null;

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            RibFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>The rib as the model holds it: the values an edit is assembled from and confirmed against.</summary>
    private sealed record RibState(int? Side, double? AngleDeg, int? SegmentIndex, int? ThinType,
        double? NormalThicknessMm, double? ReverseThicknessMm);

    private static RibState ReadRibState(ksRibDefinition definition)
    {
        int? thinType = null;
        double? normal = null;
        double? reverse = null;
        try
        {
            if (definition.GetThinParam(out var type, out var forward, out var backward))
            {
                thinType = type;
                normal = forward;
                reverse = backward;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            thinType = null;
        }

        return new RibState(
            SafeInt(() => definition.side),
            SafeDouble(() => definition.angle),
            SafeInt(() => definition.index),
            thinType,
            normal,
            reverse);
    }

    /// <summary>Area of the rib's own sketch chain, read from the model; null when the chain is not read.</summary>
    private double? ReadRibChainArea(DocumentEntry document, ksRibDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }

        try
        {
            return AsInterface<ksEntity>(definition.GetSketch()) is ksEntity sketch
                ? ReadRibChain(document, sketch)?.AreaMm2
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary><c>side</c> and <c>segment_index</c> are refused BY NAME, before any COM call.</summary>
    /// <remarks>DOC: <c>ksribdefinition_props.html</c> and <c>ksapi_irib_setside.html</c> do give setters. The
    /// refusal is not about the route: no measurement separates "the position changed" from "the kernel
    /// accepted the value", and the volume of a mirrored rib is the same, so a claim would be unconfirmed.
    /// History: docs/decisions/ribs.md#edit</remarks>
    private static void RefuseUnconfirmedRibEdit(UpdateFeatureCommand command)
    {
        var field = command.Side is not null ? "side"
            : command.SegmentIndex is not null ? "segment_index"
            : null;
        if (field is null)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            field + " не правится: сеттер в документации есть (ksribdefinition_props.html, "
            + "ksapi_irib_setside.html), но подтвердить правку числом нечем — объём зеркального ребра "
            + "тот же, и «применилось» неотличимо от «принято». Пересоздайте ребро с нужным "
            + "положением.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = field,
                ["editable"] = new[] { "draft_angle_deg", "thickness_mm", "symmetric" },
            });
    }

    /// <summary>The two numeric bounds the creation route already enforces, applied before the write.</summary>
    private static void ValidateRibEditValues(double angle, double thickness)
    {
        if (!double.IsFinite(thickness) || thickness <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Толщина ребра должна быть положительным конечным числом, а получено "
                + Num(thickness) + " мм.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["thickness_mm"] = thickness });
        }

        if (!double.IsFinite(angle) || Math.Abs(angle) >= 90d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол уклона ребра задаётся в ГРАДУСАХ и по модулю меньше 90, а получено "
                + Num(angle) + "°: при 90° грань ребра вырождается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["draft_angle_deg"] = angle });
        }
    }
}
