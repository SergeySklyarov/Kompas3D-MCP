using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Reading B3 feature parameters FROM THE MODEL — the <c>read</c> action of order §7.</summary>
/// <remarks>INVARIANT: every route here is measured by a probe, not derived from member names (run references sit
/// at each read; summary in <c>docs/04_KOMPAS_API_NOTES.md</c> §4.10.9). INVARIANT: an empty field means "not read",
/// not zero — the reason goes to <c>unreadable_parameters</c>. Of the five reposition transformation fields ONE is
/// not published — <c>reposition_axis_point_mm</c>; the other four read by the parametric route (see history).
/// INVARIANT: reading does not fail when the bridge is unavailable — feature state (name, IsValid, updateStamp)
/// reads without API7, so an unavailable route does not fail all of <c>kompas_get_feature</c>.
/// History: docs/decisions/adapter-solid.md#reposition-read</remarks>
public partial class Api5Session
{
    /// <summary>The rotation axis point is not published — a MEASURED LIMIT, not an unfinished code branch.</summary>
    /// <remarks>LIMIT: NO interface of the chain (<c>IBodyReposition</c>, <c>ILocalCoordinateSystem</c>/<c>IPoint3D</c>, <c>IPoint3DParamDisplace</c>, <c>ILocalCSAxesDirectionParam</c>, <c>ILocalCSEulerParam</c>, <c>ILocalCSObject</c>)
    /// has a documented member for the axis point; <c>RepositionCentre</c> and <c>ILocalCSObject.CoordinateSystem</c> return no coordinates
    /// (DOC: <c>ilocalcsobject_coordinatesystem.html</c>, <c>CoordinateSystem</c> is <c>IModelObject</c>). MEASURED: the axis point is NOT a
    /// placement property — <c>X/Y/Z = c</c> CHANGES the bounding box of a rotated body ((−5,0,0)…(5,20,5) instead of (−5,−5,0)…(5,15,5)),
    /// while <c>X/Y/Z = c − R·c</c> PRESERVES it; a rotation about any point of ONE line gives the SAME placement, so only a
    /// REPRESENTATIVE is recovered (<see cref="EulerOrientation.AxisPointFromPlacement"/>), not "that" point.
    /// History: docs/decisions/adapter-solid.md#reposition-axis-point</remarks>
    private const string RepositionAxisPointUnreadable =
        "reposition_axis_point_mm — не публикуется: документированного члена для точки оси НЕТ НИ У "
        + "ОДНОГО интерфейса цепочки (IBodyReposition, ILocalCoordinateSystem/IPoint3D, "
        + "IPoint3DParamDisplace, ILocalCSAxesDirectionParam, ILocalCSEulerParam, ILocalCSObject — "
        + "перечни прочитаны из библиотеки типов продукта), и сверх того точка оси не является "
        + "свойством размещения: поворот вокруг любой точки одной и той же прямой даёт то же "
        + "размещение, поэтому из прочитанных ориентации и переноса восстанавливается представитель "
        + "прямой, а не исходный вход. Запись X/Y/Z = c меняет габарит повёрнутого тела, запись "
        + "X/Y/Z = c − R·c его сохраняет — то есть X/Y/Z есть переносная часть размещения, а не точка оси.";

    /// <summary>The axis point for a TRANSLATION: not "unreadable" but NOT APPLICABLE — a translation has
    /// none.</summary>
    private const string RepositionAxisPointNotApplicable =
        "reposition_axis_point_mm — неприменимо: записан ПЕРЕНОС, у которого точки опоры нет. Это "
        + "названная граница, а не умолчание: вид преобразования прочитан из модели и назван в "
        + "reposition_kind, поэтому вызывающий видит, почему поля нет.";

    /// <summary>The vector for a ROTATION: not "unreadable" but NOT APPLICABLE — the rotation contract
    /// takes no vector.</summary>
    private const string RepositionVectorNotApplicable =
        "reposition_vector_mm — неприменимо: записан ПОВОРОТ, а контракт поворота вектора не принимает "
        + "(нужны axis_point_mm, axis_direction_mm и angle_deg). Переносная часть размещения у поворота "
        + "есть и прочитана, но она равна c − R·c и ВХОДНЫМ вектором не является: выдать её за вход "
        + "означало бы подменить параметр операции его следствием.";

    /// <summary>The reason a feature written by a FOREIGN route (a matrix) is not read — a REFUSAL, not "zeros".</summary>
    /// <remarks>INVARIANT: recognised by the DOCUMENTED, READABLE <c>ILocalCoordinateSystem.OrientationType</c> — MEASURED
    /// (probe <c>--reposition-params</c>, run <c>a336120926fc4652a8bf737562568271</c>): the parametric route reads
    /// <c>1 (ksEulerCorners)</c> live and after reopen, a matrix-written one reads <c>0 (ksAxisOrientation)</c> (RP.16, RP.22).
    /// LIMIT: a refusal, not a derivation from axes — the matrix view is SINGULAR on a reopened document with preserved
    /// geometry (RP.16), <c>WriteToFile</c> gives a singular matrix (RP.18); nothing tells "written" from "not restored"
    /// (<c>Valid</c> reads <c>True</c> in both states, <c>Update()</c> + rebuild does not restore the read, RP.23). "No parameters of this route" and "the read failed" are different strings; existing features are not silently converted.
    /// History: docs/decisions/adapter-solid.md#reposition-legacy</remarks>
    private const string RepositionLegacyReason =
        "размещение записано ЧУЖИМ маршрутом, а не документированными параметрами ориентации: "
        + "OrientationType читается 0 (ksAxisOrientation), а не 1 (ksEulerCorners), поэтому углов "
        + "Эйлера у него нет. Выводить вид преобразования из матричного вида нельзя: на переоткрытом "
        + "документе он единичен при сохранённой геометрии (RP.16: GetVector(OX) = (1,0,0) при габарите "
        + "(−5,−5,0)…(5,15,5)), WriteToFile даёт единичную матрицу (RP.18), и отличить «записано» от "
        + "«не восстановлено» нечем (Valid = True в обоих состояниях; Update() и сборка чтение не "
        + "восстанавливают, RP.23). Существующий признак не преобразуется молча: продукт называет "
        + "границу чтения, а не подставляет нули и не выдаёт выведенное за прочитанное.";

    /// <summary>B3 family by the FEATURE NUMBER IN THE TREE, not by definition: these families have no
    /// API5 definition at all (<c>GetDefinition()</c> returns null). MEASURED 18.09.2026 by the instrument
    /// <c>scratch/b3-measure-feature-types.py</c>.</summary>
    internal static string? SolidFamilyOf(int entityType) => entityType switch
    {
        KompasObjectTypes.BooleanOperation => BooleanFamily,
        KompasObjectTypes.SplitSolid => SplitFamily,
        KompasObjectTypes.CutByPlane => CutByPlaneFamily,
        KompasObjectTypes.BodyRepositionFeature => RepositionFamily,
        _ => null,
    };

    private SolidFeatureDto? ReadSolidFeature(DocumentEntry document, ksEntity entity, string family)
    {
        var unreadable = new List<string>();
        try
        {
            var bridge = BridgeFor(document);
            var container = RequireContainer(bridge, document, "solid.read");
            return family switch
            {
                BooleanFamily => ReadBooleanSolid(document, container, entity, unreadable),
                SplitFamily => ReadSupportSolid(document, container, entity, unreadable, isCut: false),
                CutByPlaneFamily => ReadSupportSolid(document, container, entity, unreadable, isCut: true),
                RepositionFamily => ReadRepositionSolid(document, container, entity, unreadable),
                _ => null,
            };
        }
        catch (KompasContractException ex)
        {
            unreadable.Add("solid_params_not_read — маршрут API7 недоступен: " + ex.Message);
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }
    }

    /// <summary>Boolean operation kind and tool preservation.</summary>
    /// <remarks>MEASURED 18.09.2026 (probe <c>--boolean</c>, steps BO.11 and BO.10): a written
    /// <c>IBoolean.BooleanType</c> reads back as <c>ksUnion</c> / <c>ksDifference</c> / <c>ksIntersect</c> —
    /// three DIFFERENT values on three different writes, so the read distinguishes rather than returning a
    /// constant. <c>SaveCopyModifyObjects</c> read back in BO.5 (<c>true</c>) and BO.2–BO.4, BO.6
    /// (<c>false</c>), and step BO.10 read both fields from a REOPENED file — the route survives
    /// save → close → reopen.</remarks>
    private SolidFeatureDto ReadBooleanSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable)
    {
        if (TrySolidIndex(document, entity, KompasObjectTypes.BooleanOperation,
                Api7SolidBoolean.Count(container), BooleanFamily, "Booleans") is not { } index
            || container.Booleans[index] is not IBoolean boolean)
        {
            unreadable.Add(NotMatched(BooleanFamily, "Booleans"));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        var operation = ReadEnumOrNull(() => boolean.BooleanType) switch
        {
            ksBooleanType.ksUnion => "union",
            ksBooleanType.ksDifference => "difference",
            ksBooleanType.ksIntersect => "intersect",
            _ => null,
        };
        if (operation is null)
        {
            unreadable.Add("operation_not_read — IBoolean.BooleanType не отдал вид операции "
                + "(ksBooleanUnknown либо чтение отказало)");
        }

        var keepTools = ReadBoolOrNull(() => boolean.SaveCopyModifyObjects);
        if (keepTools is null)
        {
            unreadable.Add("keep_tools_not_read — IBoolean.SaveCopyModifyObjects не прочитался");
        }

        return new SolidFeatureDto(operation, keepTools, UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>The support of a split (<c>ISplitSolid.CutObjects</c>) or a cut (<c>ICut.CutObject</c>) and the cut side (<c>ICut.Direction</c>).</summary>
    /// <remarks>MEASURED 18.09.2026 (probe <c>--split</c>, step SP.10, run <c>95e24af18d694d2cb50890a99983376a</c>):
    /// the support <c>x = 10</c> reads as points <c>(10,0,0)</c>, <c>(10,1,0)</c>, <c>(10,0,1)</c> with normal
    /// <c>(1,0,0)</c>, and <c>x = 15</c> as <c>(15,0,0)</c>, <c>(15,1,0)</c>, <c>(15,0,1)</c> — the read
    /// distinguishes DIFFERENT supports rather than returning a constant; <c>Direction</c> reads <c>true</c>
    /// and <c>false</c> on the same support. Step SP.9 (E-A) showed the support reads from a live feature
    /// and its three points are what the edit moves.</remarks>
    private SolidFeatureDto ReadSupportSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable, bool isCut)
    {
        var family = isCut ? CutByPlaneFamily : SplitFamily;
        var treeType = isCut ? KompasObjectTypes.CutByPlane : KompasObjectTypes.SplitSolid;
        var collectionName = isCut ? "Cuts" : "SplitSolids";
        var count = isCut ? Api7SolidCut.Count(container) : Api7SolidSplit.Count(container);
        if (TrySolidIndex(document, entity, treeType, count, family, collectionName) is not { } index)
        {
            unreadable.Add(NotMatched(family, collectionName));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        object? readBack;
        bool? keepSide = null;
        if (isCut)
        {
            if (container.Cuts[index] is not ICut cut)
            {
                unreadable.Add("support_not_read — элемент коллекции Cuts[" + index + "] не отдаёт ICut");
                return new SolidFeatureDto(UnreadableParameters: unreadable);
            }

            readBack = cut.CutObject;
            keepSide = ReadBoolOrNull(() => cut.Direction);
            if (keepSide is null)
            {
                unreadable.Add("keep_side_not_read — ICut.Direction не прочитался");
            }
        }
        else
        {
            if (container.SplitSolids[index] is not ISplitSolid split)
            {
                unreadable.Add("support_not_read — элемент коллекции SplitSolids[" + index
                    + "] не отдаёт ISplitSolid");
                return new SolidFeatureDto(UnreadableParameters: unreadable);
            }

            readBack = split.CutObjects;
        }

        var plane = ReadSupportPlane(Api7PlaneSupport.FirstOf(readBack), unreadable);
        return new SolidFeatureDto(Plane: plane, KeepSide: keepSide, UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>The reposition kind and its parameters — READ from the model, not derived.</summary>
    /// <remarks>INVARIANT: the source is the placement PARAMETERS, not the matrix — <c>OrientationType = ksEulerCorners</c> + <c>LocalCSParameters</c>
    /// (angle triple) and <c>ParameterType = ksPDisplace</c> + <c>Parameters</c> (translation). MEASURED in full by probe
    /// <c>--reposition-params</c>, run <c>a336120926fc4652a8bf737562568271</c>, step RP.25 (details in history). The matrix view
    /// (<c>GetVector</c>, <c>WriteToFile</c>) is NOT used at all (RP.16, RP.18, RP.20, RP.23). INVARIANT: the kind is decided
    /// UNAMBIGUOUSLY from the read parameters via a rotation matrix (<see cref="EulerOrientation"/>): singular rotation + read
    /// translation = translation, non-singular = rotation. Published: kind, vector, axis direction, angle (not the axis point).
    /// LIMIT: a matrix-written feature is a refusal, not zeros. History: docs/decisions/adapter-solid.md#reposition-read</remarks>
    private SolidFeatureDto ReadRepositionSolid(
        DocumentEntry document, IModelContainer container, ksEntity entity, List<string> unreadable)
    {
        if (TrySolidIndex(document, entity, KompasObjectTypes.BodyRepositionFeature,
                Api7SolidReposition.Count(container), RepositionFamily, "BodyRepositions") is not { } index
            || container.BodyRepositions[index] is not IBodyReposition)
        {
            unreadable.Add(NotMatched(RepositionFamily, "BodyRepositions"));
            return new SolidFeatureDto(UnreadableParameters: unreadable);
        }

        // INVARIANT: not a single setter before this point — a write before the read would return a
        // correct matrix-view read one step early and mask the defect (RP.20).
        var placement = Api7SolidReposition.ReadPlacement(container, index);
        if (placement.Reading is null)
        {
            NameRepositionUnreadable(unreadable,
                "размещение не читается: " + (placement.Failure ?? "причина не сообщена"));
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        var reading = placement.Reading;
        if (!reading.IsEuler || reading.AnglesDeg is not { Length: 3 } angles)
        {
            NameRepositionUnreadable(unreadable, RepositionLegacyReason);
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        var rotation = EulerOrientation.RotationFromAngles(angles[0], angles[1], angles[2]);

        // TRANSLATION: the read rotation is singular. The translation vector must be READ — for a
        // translation it IS the input value, so a zero is not substituted for a failed read.
        if (EulerOrientation.IsIdentity(rotation))
        {
            if (!reading.HasDisplacement || reading.DisplacementMm is not { Length: 3 } vector)
            {
                NameRepositionUnreadable(unreadable,
                    "записан перенос, но смещение не прочитано: ParameterType = "
                    + reading.ParameterType + ", а не 2 (ksPDisplace). Ноль вместо неудавшегося "
                    + "чтения не подставляется.");
                return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
            }

            unreadable.Add(RepositionAxisPointNotApplicable);
            return new SolidFeatureDto(
                RepositionKind: "translate",
                RepositionVectorMm: vector,
                UnreadableParameters: Unreadable(unreadable));
        }

        // ROTATION: axis and angle are derived from the READ orientation, not from the matrix view of
        // the placement.
        var (axis, angleDeg) = EulerOrientation.AxisAngleFromRotation(rotation);
        if (axis.Length != 3)
        {
            NameRepositionUnreadable(unreadable,
                "ориентация прочитана, но ось поворота из неё не выводится: кососимметричная часть "
                + "нулевая при ненулевом угле, то есть прочитанная ориентация поворотом не является");
            return new SolidFeatureDto(UnreadableParameters: Unreadable(unreadable));
        }

        unreadable.Add(RepositionAxisPointUnreadable);
        unreadable.Add(RepositionVectorNotApplicable);
        return new SolidFeatureDto(
            RepositionKind: "rotate",
            RepositionAxisDirectionMm: axis,
            RepositionAngleDeg: angleDeg,
            UnreadableParameters: Unreadable(unreadable));
    }

    /// <summary>Name EVERY transformation field as unread with one reason — one line per field.</summary>
    /// <remarks>One line per field, not one line with a list: the caller checks the read boundary by field
    /// NAMES, and a glued list would not tell "named" from "mentioned". The reason is substituted without
    /// the field name, so there is exactly one prefix here.</remarks>
    private static void NameRepositionUnreadable(List<string> unreadable, string reason)
    {
        foreach (var field in RepositionTransformationFields)
        {
            unreadable.Add(field + " — не прочитано: " + reason);
        }
    }

    /// <summary>The reposition transformation fields — in the order they are declared in the contract.</summary>
    private static readonly string[] RepositionTransformationFields =
    {
        "reposition_kind",
        "reposition_vector_mm",
        "reposition_axis_point_mm",
        "reposition_axis_direction_mm",
        "reposition_angle_deg",
    };

    /// <summary>The support's three construction points and the normal from them. The normal uses the same
    /// rule as at creation — <c>(p2−p1)×(p3−p1)</c> — otherwise there would be nothing to compare.</summary>
    private static SupportPlaneDto? ReadSupportPlane(object? support, List<string> unreadable)
    {
        if (support is not IPlane3DBy3Points byPoints)
        {
            unreadable.Add("plane_not_read — опора признака не отвечает IPlane3DBy3Points: у неё нет "
                + "трёх точек построения, а маршрут чтения опоры идёт через них (шаг SP.10)");
            return null;
        }

        var p1 = ReadPointOf(byPoints.Point1);
        var p2 = ReadPointOf(byPoints.Point2);
        var p3 = ReadPointOf(byPoints.Point3);
        var normal = NormalOf(p1, p2, p3);
        if (p1 is null || p2 is null || p3 is null || normal is null)
        {
            unreadable.Add("plane_not_read — одна из трёх точек построения опоры не читается");
            return null;
        }

        return new SupportPlaneDto(p1, normal, p1, p2, p3);
    }

    private static double[]? ReadPointOf(object? candidate)
    {
        if (candidate is not IPoint3D point)
        {
            return null;
        }

        var x = SafeDouble(() => point.X);
        var y = SafeDouble(() => point.Y);
        var z = SafeDouble(() => point.Z);
        return x is null || y is null || z is null ? null : new[] { x.Value, y.Value, z.Value };
    }

    private static double[]? NormalOf(double[]? p1, double[]? p2, double[]? p3)
    {
        if (p1 is null || p2 is null || p3 is null)
        {
            return null;
        }

        var u = new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] };
        var v = new[] { p3[0] - p1[0], p3[1] - p1[1], p3[2] - p1[2] };
        var n = new[]
        {
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0],
        };
        var norm = Norm(n);
        return norm < 1e-12 ? null : Unit(n, norm);
    }

    // History: docs/decisions/adapter-solid.md#reposition-axes-removed
    private static double Norm(double[] value) =>
        Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);

    private static double[] Unit(double[] value, double norm) =>
        new[] { value[0] / norm, value[1] / norm, value[2] / norm };

    /// <summary>The feature's position in the API7 collection — by the same tree matching as the edit
    /// (<see cref="RequireSameTypeIndex"/>), but WITHOUT a refusal: a read has no mutation to prevent, so
    /// an unmatched feature is a named reason, not a failure.</summary>
    private static int? TrySolidIndex(
        DocumentEntry document, ksEntity entity, int treeType, int? api7Count, string family, string collectionName)
    {
        try
        {
            return RequireSameTypeIndex(document, entity, treeType, api7Count, family, collectionName);
        }
        catch (KompasContractException)
        {
            return null;
        }
    }

    private static string NotMatched(string family, string collectionName) =>
        "solid_feature_not_matched — признак (" + family + ") не сопоставлен с элементом коллекции "
        + "API7 " + collectionName + ": позиция в дереве и число элементов разошлись, поэтому "
        + "параметры не читались — читать «похожий» признак значило бы описывать не тот объект";

    private static IReadOnlyList<string>? Unreadable(List<string> reasons) =>
        reasons.Count == 0 ? null : reasons;

    private static bool? ReadBoolOrNull(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    private static T? ReadEnumOrNull<T>(Func<T> read) where T : struct, Enum
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + ex.Message
        + (ComHResult.From(ex) is int code ? " [" + ComHResult.Name(code) + "]" : string.Empty);
}
