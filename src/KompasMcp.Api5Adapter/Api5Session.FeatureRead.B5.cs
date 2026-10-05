using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Reading the three B5 families — sweep, loft and shell — FROM THE MODEL, not the response.</summary>
/// <remarks>INVARIANT: a family is recognised BY THE DEFINITION INTERFACE, not by the type number — the
/// tree number differs from the creation number (MEASURED).
/// History: docs/decisions/adapter-solid.md#b5-read-compaction</remarks>
public partial class Api5Session
{
    private const string EvolutionFamily = "sweep";
    private const string LoftFamily = "loft";
    private const string ShellFamily = "shell";

    /// <summary>Path-length unit — <c>ST_MIX_*</c>. MEASURED: <c>GetPathLength(1)</c> is millimetres.</summary>
    private const uint PathLengthUnitMillimetres = 1u;

    /// <summary>Whether the definition answers the sweep interface. Both are accepted.</summary>
    private static bool IsEvolutionDefinition(object? definition) =>
        definition is ksBaseEvolutionDefinition or ksBossEvolutionDefinition;

    /// <summary>Whether the definition answers the loft interface. Both are accepted.</summary>
    private static bool IsLoftDefinition(object? definition) =>
        definition is ksBaseLoftDefinition or ksBossLoftDefinition;

    /// <summary>Whether the definition answers the shell interface.</summary>
    private static bool IsShellDefinition(object? definition) => definition is ksShellDefinition;

    /// <summary>Sweep operation read from the definition taken from the tree. No value comes from the
    /// creation response.</summary>
    /// <remarks>INVARIANT: the branch is chosen BY THE INTERFACE ANSWER, not by the type number
    /// (MEASURED: <c>NewEntity(45)</c> yields <c>ksBossEvolutionDefinition</c>).</remarks>
    private static SweepDto? ReadSweepFeature(object? definition)
    {
        if (definition is ksBaseEvolutionDefinition baseEvolution)
        {
            return new SweepDto(
                ShiftModeName(SafeShort(() => baseEvolution.sketchShiftType)),
                ProfileCount(baseEvolution.GetSketch),
                PathPartCount(baseEvolution.PathPartArray),
                SafeDouble(() => baseEvolution.GetPathLength(PathLengthUnitMillimetres)));
        }

        if (definition is ksBossEvolutionDefinition bossEvolution)
        {
            return new SweepDto(
                ShiftModeName(SafeShort(() => bossEvolution.sketchShiftType)),
                ProfileCount(bossEvolution.GetSketch),
                PathPartCount(bossEvolution.PathPartArray),
                SafeDouble(() => bossEvolution.GetPathLength(PathLengthUnitMillimetres)));
        }

        return null;
    }

    /// <summary><c>IEvolution.OperationResult</c> — present ONLY in API7. INVARIANT: the feature is
    /// matched to an <c>Evolutions</c> element BY ORDER among same-family ones, not by name.</summary>
    private int? ReadEvolutionOperationResult(DocumentEntry document, ksEntity entity, out string? reason)
    {
        reason = null;
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason = "evolution_operation_result_not_read — мост API7 недоступен";
            return null;
        }

        if (Api7Evolution.Count(container) is not int count || count <= 0)
        {
            reason = "evolution_operation_result_not_read — коллекция IModelContainer.Evolutions пуста "
                + "или не прочитана";
            return null;
        }

        var ordinal = OrdinalAmong(document.PartNow(), entity, IsEvolutionEntity);
        if (ordinal is not int index || index < 0 || index >= count)
        {
            reason = "evolution_operation_result_not_matched — признак не сопоставлен с элементом "
                + "коллекции Evolutions по порядку среди односемейных";
            return null;
        }

        var value = Api7Evolution.OperationResult(container, index);
        if (value is null)
        {
            reason = "evolution_operation_result_not_read — OperationResult не прочитался";
        }

        return value;
    }

    /// <summary>Loft read from the document COLLECTION — the very route it was created by. The creation
    /// handle is not used: its response is exactly what must not be retold.</summary>
    private LoftDto? ReadLoftFeature(DocumentEntry document, ksEntity entity, out string? reason)
    {
        reason = null;
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason = "loft_params_not_read — мост API7 недоступен, а ILoft живёт только в API7";
            return null;
        }

        if (Api7Loft.Count(container) is not int count || count <= 0)
        {
            reason = "loft_params_not_read — коллекция IModelContainer.Lofts пуста или не прочитана";
            return null;
        }

        var ordinal = OrdinalAmong(document.PartNow(), entity, IsLoftEntity);
        if (ordinal is not int index || index < 0 || index >= count)
        {
            reason = "loft_not_matched — признак не сопоставлен с элементом коллекции Lofts по "
                + "порядку среди односемейных (совпадений 0 или порядок не прочитан), поэтому "
                + "параметры null, а не ноль";
            return null;
        }

        var loft = Api7Loft.Read(container, index);
        if (loft is null)
        {
            reason = "loft_not_read — элемент коллекции Lofts по индексу " + index + " не отдал ILoft";
            return null;
        }

        // INVARIANT: couplings are read IN FULL; section refs are derived from the definition and checked
        // against the SAME count published as `section_count` — a mismatch is an incomplete derivation.
        var sectionCount = Api7Loft.SectionCount(loft);
        var sectionRefs = LoftSectionRefs(document, entity);
        if (sectionRefs is not null && sectionCount is int declaredSections
            && sectionRefs.Count != declaredSections)
        {
            reason ??= "loft_section_refs_incomplete — сечений в признаке " + declaredSections
                + ", а ссылок выведено " + sectionRefs.Count
                + ": список ссылок объявлен непрочитанным, потому что неполный список читался бы "
                + "как «сечений меньше», а он же служит входом правки";
            sectionRefs = null;
        }

        return new LoftDto(
            BuildingName(Api7Loft.BuildingType(loft, true)),
            Api7Loft.Closed(loft),
            sectionCount,
            Api7Loft.CouplingsCount(loft),
            ReadCouplingContent(loft),
            sectionRefs);
    }

    /// <summary>Shell read from the tree definition. The second half reads the same three values from
    /// API7 (<c>IShells</c> → <c>IShell</c>); a disagreement between the halves is named, not
    /// silenced.</summary>
    private ShellDto? ReadShellFeature(
        DocumentEntry document,
        ksEntity entity,
        ksShellDefinition definition,
        out string? reason)
    {
        reason = null;

        var thickness = SafeDouble(() => definition.thickness);
        var thinType = SafeBool(() => definition.thinType);
        var faces = FaceArrayCount(definition);

        if (thickness is null || thinType is null || faces is null)
        {
            reason = "shell_params_partly_unreadable — с определения API5 не прочиталось: "
                + (thickness is null ? "thickness " : string.Empty)
                + (thinType is null ? "thinType " : string.Empty)
                + (faces is null ? "FaceArray" : string.Empty);
        }

        // The same three values from API7, as a separate check.
        var container = TryContainerFor(document);
        if (container is null)
        {
            reason ??= "shell_api7_half_unavailable — мост API7 недоступен, поэтому вторая половина "
                + "постановки (IShell.Thickness/ThinType/DeletedFaces) не прочитана";
        }
        else if (Api7Shell.Count(container) is not int count || count <= 0)
        {
            reason ??= "shell_api7_half_unavailable — коллекция IModelContainer.Shells пуста";
        }
        else
        {
            var ordinal = OrdinalAmong(document.PartNow(), entity, IsShellEntity);
            var shell = ordinal is int index && index >= 0 && index < count
                ? Api7Shell.Read(container, index)
                : null;
            if (shell is null)
            {
                reason ??= "shell_api7_half_not_matched — признак не сопоставлен с элементом "
                    + "коллекции Shells по порядку среди односемейных";
            }
            else
            {
                // Half agreement: thickness and removed-face count must match; direction is compared BY
                // VALUE (API5 thinType=true = API7 ThinType "inward", MEASURED).
                var api7Thickness = Api7Shell.Thickness(shell);
                var api7Faces = Api7Shell.DeletedFaceCount(shell);
                if (api7Thickness is double t2 && thickness is double t1 && Math.Abs(t1 - t2) > 1e-6)
                {
                    reason ??= "shell_halves_disagree — толщина с определения API5 " + t1.ToString("0.####", CultureInfo.InvariantCulture)
                        + " против IShell.Thickness " + t2.ToString("0.####", CultureInfo.InvariantCulture);
                }

                if (api7Faces is int f2 && faces is int f1 && f1 != f2)
                {
                    reason ??= "shell_halves_disagree — снятых граней на определении API5 " + f1
                        + " против IShell.DeletedFaces " + f2;
                }
            }
        }

        // Removed-face refs are derived FROM THE SAME collection as `removed_face_count`; a mismatch is an
        // incomplete derivation, named — an empty list beside a non-zero counter would look like a fact.
        var removedFaceRefs = ShellRemovedFaceRefs(document, definition);
        if (removedFaceRefs is not null && faces is int faceCount && removedFaceRefs.Count != faceCount)
        {
            reason ??= "shell_removed_face_refs_incomplete — снятых граней на определении "
                + faceCount + ", а ссылок выведено " + removedFaceRefs.Count
                + ": список ссылок объявлен непрочитанным, потому что неполный список читался бы "
                + "как «снято меньше граней»";
            removedFaceRefs = null;
        }

        return new ShellDto(
            thickness,
            ThinDirectionName(thinType),
            faces,
            removedFaceRefs);
    }

    /// <summary>Loft SECTIONS AS REFERENCES, derived from the feature definition.</summary>
    /// <remarks>DOC: <c>ksbaseloftdefinition_sketches.html</c> / <c>ksbossloftdefinition_sketches.html</c>
    /// — «Sketches», returning <c>ksEntityCollection</c>. INVARIANT: refs are derived afresh, never
    /// remembered — a creation-time reference dies on the first mutation and <c>kompas_rebuild</c> revokes
    /// ALL references. <c>null</c> means "not read"; an empty list means "no sections" — not merged.
    /// History: docs/decisions/adapter-solid.md#b5-read-compaction</remarks>
    private IReadOnlyList<string>? LoftSectionRefs(DocumentEntry document, ksEntity entity)
    {
        try
        {
            var holder = DefinitionOf(entity) switch
            {
                ksBaseLoftDefinition b => b.Sketchs(),
                ksBossLoftDefinition s => s.Sketchs(),
                _ => null,
            };

            if (holder is not ksEntityCollection sections)
            {
                return null;
            }

            var ids = new List<string>(sections.GetCount());
            for (var i = 0; i < sections.GetCount(); i++)
            {
                // INVARIANT: use `AsInterface`, not a bare `is` — a collection element comes as `object`
                // and is of TWO kinds: the interface itself or a `ksEntity` that must be unwrapped via
                // `GetDefinition()`. The same technique as in every other collection read of this
                // adapter.
                if (AsInterface<ksEntity>(sections.GetByIndex(i)) is ksEntity section)
                {
                    ids.Add(References.Register("sketch", document.Id, document.Revision, section).Id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Shell REMOVED FACES AS REFERENCES, derived from <c>ksShellDefinition.FaceArray()</c>.</summary>
    /// <remarks>Faces removed by the shell are ABSENT from the body topology, so
    /// <c>kompas_read_topology</c> cannot yield them. INVARIANT: use <c>AsInterface</c> — a bare
    /// <c>is ksFaceDefinition</c> yields an EMPTY list while <c>removed_face_count = 1</c>.
    /// History: docs/decisions/adapter-solid.md#b5-read-compaction</remarks>
    private IReadOnlyList<string>? ShellRemovedFaceRefs(DocumentEntry document, ksShellDefinition definition)
    {
        try
        {
            if (definition.FaceArray() is not ksEntityCollection faces)
            {
                return null;
            }

            var ids = new List<string>(faces.GetCount());
            for (var i = 0; i < faces.GetCount(); i++)
            {
                if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is ksFaceDefinition face)
                {
                    ids.Add(References.Register("face", document.Id, document.Revision, face).Id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Feature ORDINAL AMONG SAME-FAMILY ones — by tree position, not by name.</summary>
    /// <remarks>Same technique as measured for rotation: both the tree and the API7 collection enumerate
    /// features in creation order, so the position among same-family ones is a stable address.
    /// <c>null</c> means "not matched", not "zero".</remarks>
    private static int? OrdinalAmong(ksPart part, ksEntity target, Func<ksEntity, bool> isKind)
    {
        try
        {
            if (part.EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                is not ksEntityCollection collection)
            {
                return null;
            }

            var ordinal = 0;
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity candidate || !isKind(candidate))
                {
                    continue;
                }

                if (ReferenceEquals(candidate, target))
                {
                    return ordinal;
                }

                ordinal++;
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool IsEvolutionEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsEvolutionDefinition(definition);

    private static bool IsLoftEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsLoftDefinition(definition);

    private static bool IsShellEntity(ksEntity entity) =>
        DefinitionOf(entity) is { } definition && IsShellDefinition(definition);

    private static object? DefinitionOf(ksEntity entity)
    {
        try
        {
            return entity.GetDefinition();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The API7 bridge for the document. <c>null</c> means "unavailable"; the caller names the
    /// reason.</summary>
    private IModelContainer? TryContainerFor(DocumentEntry document)
    {
        try
        {
            return BridgeFor(document).ContainerFor(document.Document, document.Id, document.Revision);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return null;
        }
    }

    /// <summary>Profile-contour count of a sweep: 1 if a profile is bound, 0 if not, <c>null</c> if the
    /// read failed. Zero and "not read" are different here.</summary>
    private static int? ProfileCount(Func<object?> getSketch)
    {
        try
        {
            return getSketch() is null ? 0 : 1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Path-part count. <c>null</c> means "not read", not zero parts.</summary>
    private static int? PathPartCount(Func<object?> pathPartArray)
    {
        try
        {
            return pathPartArray() is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Face count in the shell's <c>FaceArray()</c>. <c>null</c> means "not read".</summary>
    private static int? FaceArrayCount(ksShellDefinition definition)
    {
        try
        {
            return definition.FaceArray() is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Name of the section-shift mode. DOC: <c>ksbaseevolutiondefinition_sketchshifttype.html</c>
    /// (0 / 1 / 2). A value outside the declared set is returned AS A NUMBER, not turned into <c>null</c>.</summary>
    private static string? ShiftModeName(short? value) => value switch
    {
        null => null,
        0 => "parallel",
        1 => "keep_angle",
        2 => "orthogonal",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Build mode at the end section. DOC: <c>ksloftbuildingtype.html</c> — 0 auto, 1 by_normal,
    /// 2 by_object, 3 cupola. An unknown number is returned as a number.</summary>
    private static string? BuildingName(int? value) => value switch
    {
        null => null,
        0 => "auto",
        1 => "by_normal",
        2 => "by_object",
        3 => "cupola",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Thin-wall direction. MEASURED: API5 <c>thinType = true</c> gives 21632 (inward),
    /// <c>false</c> — 24832 (outward).</summary>
    private static string? ThinDirectionName(bool? thinType) => thinType switch
    {
        null => null,
        true => "inward",
        false => "outward",
    };

    private static short? SafeShort(Func<short> read)
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

}
