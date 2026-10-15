using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>The ONE place a created or edited feature's own kernel error is turned into a refusal.</summary>
/// <remarks>INVARIANT: a write call that answered true is not an application. MEASURED: KOMPAS can leave a
/// feature in the tree carrying a non-zero <c>objectError</c> while the body is unchanged, and the call
/// used to be answered as <c>succeeded</c>; the hole order closed that for the hole, and every other
/// feature-creating or feature-editing mutation needs the same decision.
/// DOC: the feature's own error is <c>IFeature7.ObjectError</c> / <c>ksFeature.objectError</c>
/// (<c>ifeature7_objecterror.html</c>, <c>ksfeature_objecterror.html</c>) - «Получить номер ошибки»,
/// read-only. The help states the numbers correspond to the <c>ErrorType3d</c> enumeration; MEASURED, the
/// value seen on a refused hole (32) is NOT in that table, so the number is named and not decoded.
/// INVARIANT: the caller registers the reference BEFORE this check and passes its id — so the refusal
/// hands the client the feature that stayed in the tree and one call removes it.
/// INVARIANT: only the FEATURE error is shared with the hole. A measured no-op stays a refusal for the
/// hole alone: an edit that changes nothing is a legitimate idle call.
/// History: docs/decisions/adapter-features.md#feature-error-refusal</remarks>
public sealed partial class Api5Session
{
    /// <summary>Refuse when the feature that was just created or edited carries a kernel error, read from
    /// the API5 tree handle.</summary>
    private void RequireNoFeatureError(
        DocumentEntry document, ksEntity entity, string? featureRefId, string operation) =>
        RefuseFeatureError(document, ReadFeatureState(entity).ObjectError, featureRefId, operation);

    /// <summary>Refuse when the feature that was just created or edited carries a kernel error, read from
    /// the API7 view of it — for families whose only handle is the API7 object.</summary>
    private void RequireNoApi7FeatureError(
        DocumentEntry document, IModelObject? modelObject, string? featureRefId, string operation) =>
        RefuseFeatureError(document, ReadApi7FeatureError(modelObject), featureRefId, operation);

    /// <summary>The API7 feature's own error number. <c>null</c> — the state was not read.</summary>
    /// <remarks>DOC: <c>IFeature7</c> is reached from any model object as <c>IModelObject::Owner</c>
    /// (<c>ifeature7.html</c>). An unreadable route answers <c>null</c>, which the rule reads as a named
    /// gap rather than as health.
    /// History: docs/decisions/adapter-features.md#feature-error-refusal</remarks>
    private static int? ReadApi7FeatureError(IModelObject? modelObject)
    {
        try
        {
            return (modelObject?.Owner as IFeature7)?.ObjectError;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Turn a feature's own error number into a refusal.</summary>
    private void RefuseFeatureError(
        DocumentEntry document, int? objectError, string? featureRefId, string operation)
    {
        if (!FeatureErrorRule.IsRefusal(objectError))
        {
            return;
        }

        var note = featureRefId is null
            ? " Признак в дереве API5 не найден: удалять нечего."
            : " Признак ОСТАЛСЯ в дереве: удалите его вызовом kompas_delete_feature по feature_ref и "
                + "повторите вызов.";

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            operation + ": КОМПАС принял запись, но признак сообщает об ошибке ядра: object_error="
            + objectError!.Value.ToString(CultureInfo.InvariantCulture) + "." + note,
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["code"] = FeatureErrorRule.DetailCode,
                ["object_error"] = objectError,
                ["feature_ref"] = featureRefId,
                ["revision_after"] = document.Revision,
            });
    }
}
