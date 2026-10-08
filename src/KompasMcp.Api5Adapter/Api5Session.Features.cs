using System.Runtime.InteropServices;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;
using Kompas6API5;

namespace KompasMcp.Api5Adapter;

/// <summary>Reading and editing the parameters of a feature that already exists (docs/05 §4.3, §7).
/// Rests on probe P2.3 on the real v24 install, not on API5 names.</summary>
/// <remarks>SETTER ACCEPTED, GEOMETRY LATER: <c>RebuildDocument()</c> alone left the volume unchanged and
/// <c>ksPart.EndEdit(Rebuild=false)</c> returned False; geometry recomputes only after
/// <c>ksEntity.Update()</c>. FAMILY: <c>ksFeature.type</c> is 105 (o3d_entity) for every family, so the
/// family comes from the definition object.
/// History: docs/decisions/adapter-core.md#features-compaction</remarks>
public partial class Api5Session
{
    public FeatureReadDto GetFeature(GetFeatureCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);
        var definition = entity.GetDefinition();
        // The hole family is recognised by the ENTITY TYPE, not by the definition: there is no
        // ksHoleDefinition (MEASURED), so the mode parameters live only in API7. The type is 583
        // (o3d_Hole3D), not 52 (o3d_holeOperation, the creation factory number — see FindHoleEntity).
        var isHole = entity.type == KompasObjectTypes.Hole3D;
        // Rotation is recognised by the FEATURE NUMBER IN THE TREE (27/28/29), not by QI(IRotated):
        // a tree entity arrives as a raw __ComObject and refuses QI (MEASURED).
        var isRotated = IsRotatedEntity(entity);
        // B3 features are recognised by the TREE NUMBER (69 / 633 / 50 / 79): no API5 definition,
        // GetDefinition() returns null, the API7 object is not an API5 feature (MEASURED).
        var solidFamily = SolidFamilyOf(entity.type);
        // B5: the three B5 families are recognised BY THE DEFINITION INTERFACE, not by the type number
        // (MEASURED: NewEntity(45) shows as 46 and answers ksBossEvolutionDefinition). Both interfaces
        // of each family are therefore accepted.
        var isEvolution = IsEvolutionDefinition(definition);
        var isLoft = IsLoftDefinition(definition);
        var isShell = IsShellDefinition(definition);
        var family = ExtrusionFamily.Of(definition)
                     ?? (definition is ksChamferDefinition ? ChamferFamily : null)
                     ?? (definition is ksFilletDefinition ? FilletFamily : null)
                     ?? (isEvolution ? EvolutionFamily : null)
                     ?? (isLoft ? LoftFamily : null)
                     ?? (isShell ? ShellFamily : null)
                     ?? (isRotated ? RotationFamily : null)
                     ?? (isHole ? HoleFamily : null)
                     ?? solidFamily;

        IReadOnlyList<FeatureSideDto> sides = Array.Empty<FeatureSideDto>();
        FeatureThinDto? thin = null;
        short? directionType = null;
        if (ExtrusionFamily.Of(definition) is not null
            && ReadExtrusion(definition!) is var read && read is not null)
        {
            directionType = read.Value.DirectionType;
            sides = read.Value.Sides;
            thin = read.Value.Thin;
        }

        // A chamfer is read separately: it has no sides or thin wall, but it has the leg lengths and a
        // side flag, while the angle lives only in API7. An empty field here means "not read", not 0.
        var chamfer = definition is ksChamferDefinition ? ReadChamfer(document, definition) : null;
        // A fillet follows the same logic: the radius is read from API7, because a write to the API5
        // definition is not applied on an existing feature (FL04r).
        var fillet = definition is ksFilletDefinition ? ReadFillet(document, definition) : null;
        // A hole comes entirely from API7: the API5 definition does not physically store its mode
        // numbers.
        var hole = isHole ? ReadHoleFeature(document) : null;
        // Rotation also comes from API7: the parameter set lives on IRotated, which has no API5
        // definition (entity.GetDefinition() returns null). The index is taken by matching on
        // composition, not by angle — an angle is not an identifier.
        var rotated = isRotated ? ReadRotatedFeature(document, entity) : null;
        // B3 features (boolean/split/cut_by_plane/reposition) come entirely from API7: no API5
        // definition, read from the LIVE model. Unreadable parameters are named in
        // solid.unreadable_parameters rather than substituted with a zero.
        var solid = solidFamily is null ? null : ReadSolidFeature(document, entity, solidFamily);
        // B5: sweep, loft and shell are read FROM THE MODEL. A failure reason is NAMED, not left empty:
        // "not read" and "zero" must be distinguishable — silence is a claim too.
        string? sweepNote = null;
        string? loftNote = null;
        string? shellNote = null;
        var sweep = isEvolution ? ReadSweepFeature(definition) : null;
        var evolutionOperationResult = isEvolution
            ? ReadEvolutionOperationResult(document, entity, out sweepNote)
            : null;
        var loft = isLoft ? ReadLoftFeature(document, entity, out loftNote) : null;
        var shell = isShell && definition is ksShellDefinition shellDefinition
            ? ReadShellFeature(document, entity, shellDefinition, out shellNote)
            : null;
        var parametersRead = family == ChamferFamily
            ? chamfer is not null
            : family == FilletFamily
                ? fillet is not null
                : family == RotationFamily
                    ? rotated is not null
                    : family == HoleFamily
                        ? hole is not null
                        // For B5 "read" means "a substantive parameter was read", not "the method
                        // returned": the DTO is already non-empty once the definition is recognised.
                        : family == EvolutionFamily
                            ? sweep?.ShiftMode is not null && sweep.PathPartCount is not null
                            : family == LoftFamily
                                ? loft is not null && loft.SectionCount is not null
                                : family == ShellFamily
                                    ? shell?.ThicknessMm is not null
                                    : sides.Count > 0;
        if (solidFamily is not null)
        {
            // For B3 families "read" means "at least one substantive parameter was read": a translation
            // has no operation kind, a boolean has no support, and requiring a common field would declare
            // a healthy read failed.
            parametersRead = solid is not null
                && (solid.Operation is not null || solid.Plane is not null || solid.RepositionKind is not null);
        }

        var state = ReadFeatureState(entity);

        var checks = new List<NamedCheck>
        {
            new("definition_readable", definition is not null, Observed: definition?.GetType().Name ?? "null"),
            new("family_recognised", family is not null, Observed: family ?? "не распознано"),
            new(family switch
                {
                    ChamferFamily => "chamfer_params_read",
                    FilletFamily => "fillet_params_read",
                    RotationFamily => "rotation_params_read",
                    HoleFamily => "hole_params_read",
                    BooleanFamily or SplitFamily or CutByPlaneFamily or RepositionFamily => "solid_params_read",
                    EvolutionFamily => "sweep_params_read",
                    LoftFamily => "loft_params_read",
                    ShellFamily => "shell_params_read",
                    _ => "side_params_read",
                }, parametersRead,
                Observed: family switch
                {
                    ChamferFamily => chamfer is null
                        ? "GetChamferParam не читается"
                        : $"transfer={chamfer.Transfer} d1={chamfer.Distance1Mm:0.####} d2={chamfer.Distance2Mm:0.####} угол={chamfer.AngleDeg:0.####}",
                    FilletFamily => fillet is null
                        ? "IFillet не читается"
                        : $"radius={fillet.RadiusMm:0.####} способ={fillet.BuildingType} опор={fillet.BaseObjectCount}",
                    RotationFamily => rotated is null
                        ? "IRotated не сопоставлен (0 или больше одного кандидата)"
                        : $"угол={rotated.AngleDeg:0.####} направление={rotated.Direction ?? "не прочитано"} ось={rotated.AxisState ?? "не прочитано"} тип={rotated.OperationType ?? "не прочитано"}",
                    HoleFamily => hole is null
                        ? "IHole3D/HoleParameters не читаются"
                        : $"тип={hole.HoleType} D={hole.DiameterMm:0.####} глубина={hole.DepthType} d={hole.DepthMm:0.####} дно={hole.EndFaceType}",
                    BooleanFamily => solid is null
                        ? "IBoolean не сопоставлен с коллекцией Booleans"
                        : $"вид={solid.Operation ?? "не прочитано"} инструмент сохраняется={solid.KeepTools?.ToString() ?? "не прочитано"}",
                    SplitFamily or CutByPlaneFamily => solid?.Plane is { } plane
                        ? $"опора точка=({string.Join(", ", plane.PointMm.Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))}) нормаль=({string.Join(", ", plane.NormalMm.Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))})"
                          + (solid.KeepSide is null ? string.Empty : $" сторона={solid.KeepSide}")
                        : "опора признака не читается (нет трёх точек построения)",
                    RepositionFamily => solid is null
                        ? "IBodyReposition не сопоставлен с коллекцией BodyRepositions"
                        : $"вид={solid.RepositionKind ?? "не прочитано"} угол={solid.RepositionAngleDeg?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано"} ось={(solid.RepositionAxisDirectionMm is null ? "не прочитано" : "(" + string.Join(", ", solid.RepositionAxisDirectionMm.Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))) + ")")}",
                    EvolutionFamily => sweep is null
                        ? "определение кинематической операции не отвечает ни ksBaseEvolutionDefinition, ни ksBossEvolutionDefinition"
                        : $"режим={sweep.ShiftMode ?? "не прочитано"} профилей={sweep.SectionCount?.ToString() ?? "не прочитано"} частей траектории={sweep.PathPartCount?.ToString() ?? "не прочитано"} длина={sweep.PathLengthMm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано"} OperationResult={evolutionOperationResult?.ToString() ?? "не прочитано"}",
                    LoftFamily => loft is null
                        ? "ILoft не сопоставлен с коллекцией Lofts (совпадений 0 или порядок не прочитан)"
                        : $"способ={loft.Building ?? "не прочитано"} замкнут={loft.Closed?.ToString() ?? "не прочитано"} сечений={loft.SectionCount?.ToString() ?? "не прочитано"} цепочек={loft.CouplingsCount?.ToString() ?? "не прочитано"}",
                    ShellFamily => shell is null
                        ? "ksShellDefinition не прочитано"
                        : $"толщина={shell.ThicknessMm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано"} направление={shell.ThinDirection ?? "не прочитано"} снятых граней={shell.RemovedFaceCount?.ToString() ?? "не прочитано"}",
                    _ => $"{sides.Count}",
                }),
            new("feature_state_read", state.IsValid is not null,
                Observed: $"updateStamp={state.UpdateStamp} IsValid={state.IsValid?.ToString() ?? "нет"}"),
        };

        // A read is reported as structure_checked: the parameters came back out of the live model,
        // which proves nothing about geometry, so the level never claims more than that.
        var level = parametersRead && state.IsValid is not null
            ? VerificationLevel.StructureChecked
            : VerificationLevel.CallReturned;
        var unverified = new List<string>();

        // The sketch reference is READ BACK by the same helper kompas_list_features uses, so one object
        // gets one live address from both tools. Only a real failure to resolve it is named; the line is
        // no longer unconditional, because an extrusion whose sketch resolves is not an open aspect.
        // History: docs/decisions/adapter-core.md#feature-read-sketch-ref
        var sketchRef = SketchRefOfFeature(document, entity);
        if (sketchRef is null)
        {
            unverified.Add("sketch_reference_not_resolved — GetSketch() не отдал объект эскиза "
                + "для этого признака: семейство не выдавливание, либо чтение ссылки не удалось");
        }

        if (family is null)
        {
            unverified.Insert(0,
                "family_not_supported — параметрическое чтение заявлено для выдавливаний, фаски, скругления, вращения, родного отверстия, четырёх семейств B3 (булева операция, разделение, отсечение, изменение положения) и трёх семейств B5 (кинематическая операция, элемент по сечениям, оболочка); другие семейства в v1 не раскрываются");
        }
        else if (family == EvolutionFamily && sweepNote is not null)
        {
            // Named, not left empty: OperationResult lives only in API7; its absence is not a zero.
            unverified.Add(sweepNote);
        }
        else if (family == LoftFamily && loftNote is not null)
        {
            unverified.Add(loftNote);
        }
        else if (family == ShellFamily && shellNote is not null)
        {
            unverified.Add(shellNote);
        }
        else if (solid?.UnreadableParameters is { Count: > 0 })
        {
            // One line for the whole family, not one per field: the names and reasons already sit in
            // solid.unreadable_parameters, so the caller learns the read boundary from the summary.
            unverified.Add("solid_params_partly_unreadable — часть параметров признака B3 не читается "
                + "из модели; имена полей и измеренные причины — в solid.unreadable_parameters");
        }
        else if (family == RotationFamily && rotated is null)
        {
            unverified.Add("rotation_not_matched — признак вращения не сопоставлен с элементом коллекции API7 Rotateds по составу (угол и направление): совпадений 0 или больше одного, поэтому параметры null, а не 0");
        }
        else if (family == ChamferFamily && chamfer?.AngleDeg is null)
        {
            unverified.Add("angle_not_read — угол фаски живёт только в API7 (IChamfer.Angle); мост не построен или обёртка не отдала IChamfer, поэтому поле null, а не 0");
        }
        else if (family == FilletFamily && fillet?.RadiusMm is null)
        {
            unverified.Add("radius_not_read — радиус скругления читается только из API7 (IFillet.Radius1); мост не построен, обёртка не отдала IFillet, или скруглений с тем же радиусом несколько — поэтому поле null, а не 0");
        }
        else if (family == HoleFamily && hole?.DepthMm is null && hole is not null
                 && hole.DepthType != "ksDTReachThrough")
        {
            unverified.Add("hole_depth_not_read — у отверстия режим глубины не сквозной, а число глубины не прочиталось: пустое поле означает «не прочитано», а не 0");
        }

        return new FeatureReadDto(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), state.Name),
            family ?? "unknown",
            definition?.GetType().Name ?? "null",
            state.Name,
            entity.type,
            state.UpdateStamp,
            state.IsValid ?? false,
            state.Excluded,
            state.IsRollBacked,
            state.ObjectError,
            directionType,
            sides,
            thin,
            state.OwnerName,
            chamfer,
            new VerificationDto(level, checks, unverified),
            fillet,
            hole,
            rotated,
            solid,
            sweep,
            loft,
            shell)
        {
            SketchRef = sketchRef,
        };
    }

    public UpdateFeatureResult UpdateFeature(UpdateFeatureCommand command)
    {
        var chamferRequested = command.Distance1Mm is not null
                               || command.Distance2Mm is not null
                               || command.AngleDeg is not null
                               || command.Direction is not null;
        var filletRequested = command.RadiusMm is not null || command.EdgeRefs is not null
                              || command.BaseObjectRefs is not null;
        var rotationRequested = command.RotationAngleDeg is not null
                                || command.RotationDirection is not null;
        var repositionRequested = command.RepositionKind is not null
                                  || command.RepositionVectorMm is not null
                                  || command.RepositionAxisPointMm is not null
                                  || command.RepositionAxisDirectionMm is not null
                                  || command.RepositionAxisPoint2Mm is not null
                                  || command.RepositionAngleDeg is not null;
        var splitRequested = command.Plane is not null || command.ExpectedPartVolumesMm3 is not null;
        var cutRequested = command.KeepSide is not null;
        var booleanRequested = command.Operation is not null;
        // A pattern edit (queue B4) is chosen by the pattern FIELD itself, not by the tree type number
        // (which is not measured for a pattern). See Api5Session.PatternEdit.cs.
        var patternRequested = command.Pattern is not null;

        // Edit of the three families of the last queue B5 (order §11), chosen by the FIELD itself, not
        // by the tree type number (for a sweep they diverge: 45 → 46). See Api5Session.FeatureEdit.B5.cs.
        var sweepRequested = command.ShiftMode is not null;
        var loftRequested = command.SectionRefs is not null;
        var shellRequested = command.ThicknessMm is not null || command.ThinInward is not null
                             || command.FaceRefs is not null;

        // INVARIANT: `couplings` is a SECOND INPUT of the loft family and does NOT select it — the chains
        // describe point correspondence of an ALREADY SET section set, so without section_refs there is
        // nothing to change (UpdateLoftFeature rejects such a call by name). But couplings MUST stand in
        // the foreign-field lists, otherwise it is accepted and swallowed.
        var couplingsRequested = command.Couplings is not null;

        // Hole (SM-07): mode fields. The feature is selected NOT by these fields but by the tree entity
        // type (583), as in the read; the flag below serves the checks "hole fields arrived on a non-hole
        // feature" and "no parameter given".
        var holeRequested = command.DiameterMm is not null
                            || command.CounterboreDiameterMm is not null
                            || command.CounterboreDepthMm is not null
                            || command.CountersinkDiameterMm is not null
                            || command.CountersinkAngleDeg is not null;

        if (command.DepthMm is null && command.EndCondition is null && command.SketchRef is null
            && !chamferRequested && !filletRequested && !rotationRequested && !repositionRequested
            && !splitRequested && !cutRequested && !booleanRequested && !patternRequested
            && !sweepRequested && !loftRequested && !shellRequested && !holeRequested)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Не указано ни одного параметра: для выдавливания ожидается depth_mm, end_condition " +
                "или sketch_ref; для фаски — distance1_mm, distance2_mm, angle_deg или direction; " +
                "для скругления — radius_mm, edge_refs или base_object_refs; для вращения — " +
                "rotation_angle_deg или rotation_direction; для изменения положения — " +
                "reposition_kind с reposition_vector_mm либо полями оси поворота; для разделения — " +
                "plane (и необязательно expected_part_volumes_mm3); для отсечения — plane и keep_side; " +
                "для булевой операции — operation; для кинематической операции — shift_mode; для " +
                "элемента по сечениям — section_refs; для оболочки — thickness_mm, thin_inward или " +
                "face_refs; для отверстия — diameter_mm и поля СВОЕГО режима (depth_mm у blind_flat, " +
                "counterbore_diameter_mm/counterbore_depth_mm у through_counterbore, " +
                "countersink_diameter_mm/countersink_angle_deg у through_countersink). Цепочки " +
                "соответствия (couplings) — сопровождение набора сечений, а не самостоятельный вход: " +
                "они передаются ВМЕСТЕ с section_refs.");
        }

        if (command.EndCondition == ExtrudeEndCondition.Through && command.DepthMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "end_condition=through и depth_mm вместе запрещены: режим насквозь число игнорирует.");
        }

        var (document, entity) = RequireFeatureEntity(command.FeatureRef);
        var volumeBefore = ReadVolume(document);
        var featuresBefore = CountFeatures(document);
        var stateBefore = ReadFeatureState(entity);

        // A pattern is the ninth editable family B4 (order §7: action edit). The branch stands BEFORE
        // the API5 definition read: a pattern feature has no API5 definition at all.
        if (patternRequested)
        {
            return UpdatePattern(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // A hole is the tenth editable family (order SM07 §3.2, queue B2). The branch stands HERE, not
        // among the branches by API5 definition: a native hole has NO definition. Recognition is by the
        // ENTITY TYPE IN THE TREE: 583 (o3d_Hole3D), not 52 (o3d_holeOperation, the FACTORY number).
        if (entity.type == KompasObjectTypes.Hole3D)
        {
            return UpdateHole(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        var definition = entity.GetDefinition();

        if (definition is ksChamferDefinition)
        {
            // The family decides which fields make sense at all: giving a chamfer depth_mm and staying
            // silent that it was not applied would lie about the edit.
            if (command.DepthMm is not null || command.EndCondition is not null || command.SketchRef is not null
                || sweepRequested || loftRequested || couplingsRequested || shellRequested || holeRequested)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Признак — фаска: depth_mm, end_condition, sketch_ref, параметры кинематической " +
                    "операции, элемента по сечениям (section_refs, couplings), оболочки и отверстия " +
                    "(diameter_mm и поля его режимов) к ней не применяются. Параметры фаски — " +
                    "distance1_mm, distance2_mm, angle_deg, direction.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["family"] = ChamferFamily });
            }

            return UpdateChamfer(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // A fillet is the third editable family. ksFilletDefinition declares a radius, but a write to it
        // on an existing feature is NOT applied (MEASURED by row FL04r), so the API7 route is used;
        // fields of other families do not apply to a fillet and are rejected here rather than ignored.
        if (definition is ksFilletDefinition)
        {
            if (command.DepthMm is not null || command.EndCondition is not null || command.SketchRef is not null
                || chamferRequested || sweepRequested || loftRequested || couplingsRequested
                || shellRequested || holeRequested)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Признак — скругление: меняемые им параметры — radius_mm (радиус), edge_refs и " +
                    "base_object_refs (набор рёбер). depth_mm, end_condition, sketch_ref, параметры " +
                    "фаски, кинематической операции, элемента по сечениям (section_refs, couplings), " +
                    "оболочки и отверстия к нему не применяются.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["family"] = FilletFamily });
            }

            // Radius and edge set are different routes and different edit subjects; they must not be
            // mixed in one call: "changed both" would be indistinguishable from "one of the two applied".
            if (command.EdgeRefs is not null || command.BaseObjectRefs is not null)
            {
                return UpdateFilletEdgeSet(document, entity, command, volumeBefore, featuresBefore, stateBefore);
            }

            return UpdateFilletRadius(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Rotation is the fourth editable family, recognised by the FEATURE NUMBER IN THE TREE
        // (27/28/29), not by definition (QI(IRotated) on a tree entity refuses, MEASURED). Only angle
        // and direction change; foreign fields are rejected here rather than ignored.
        if (IsRotatedEntity(entity))
        {
            return UpdateRotated(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Body reposition is the fifth editable family (order B3 §5, §7), recognised by the FEATURE
        // NUMBER IN THE TREE (79): no API5 definition, and 569 (o3d_BodyReposition) is the CREATION side.
        if (entity.type == KompasObjectTypes.BodyRepositionFeature)
        {
            return UpdateSolidReposition(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Split and cut are the sixth and seventh editable B3 families (order §5, §7), recognised by the
        // FEATURE NUMBER IN THE TREE (633 = o3d_SplitSolid, 50 = o3d_cutByPlane), not the creation factory.
        if (entity.type == KompasObjectTypes.SplitSolid)
        {
            return UpdateSolidSplit(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        if (entity.type == KompasObjectTypes.CutByPlane)
        {
            return UpdateSolidCutByPlane(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // A boolean is the eighth editable B3 family, recognised by number 69 (o3d_aggregate) in the
        // tree, not by the API5 definition (ksAggregateDefinition has NO way to set bodies — OQ-A16).
        // The API7 route was MEASURED: rewriting IBoolean.BooleanType changes the geometry.
        if (entity.type == KompasObjectTypes.BooleanOperation)
        {
            return UpdateSolidBoolean(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // The three families of the last mandatory queue B5 (order §11). The branch stands HERE, not
        // earlier: families with API5 definitions above have already rejected foreign fields by name.
        // Recognition is by the FIELD itself, not by the tree type number. See Api5Session.FeatureEdit.B5.cs.
        if (sweepRequested)
        {
            return UpdateSweepFeature(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        if (loftRequested)
        {
            return UpdateLoftFeature(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        if (shellRequested)
        {
            return UpdateShellFeature(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        var family = ExtrusionFamily.Of(definition);
        var current = family is null ? null : ReadExtrusion(definition!);
        if (family is null || current is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Правка параметров в v1 поддержана для семейств выдавливания, фаски, скругления, " +
                $"вращения, изменения положения, разделения, отсечения, булевой операции, массива, " +
                $"кинематической операции, элемента по сечениям, оболочки и отверстия; этот признак — " +
                $"{definition?.GetType().Name ?? "null"} (type={entity.type}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["definition"] = definition?.GetType().Name });
        }

        if (chamferRequested || filletRequested || sweepRequested || loftRequested || couplingsRequested
            || shellRequested || holeRequested)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак — выдавливание: параметры фаски (distance1_mm, distance2_mm, angle_deg, " +
                "direction), скругления (radius_mm, edge_refs, base_object_refs), кинематической " +
                "операции, элемента по сечениям (section_refs, couplings), оболочки и отверстия " +
                "(diameter_mm и поля его режимов) к нему не применяются.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = family });
        }

        var sides = current.Value.Sides;
        if (command.EndCondition == ExtrudeEndCondition.Through && family != ExtrusionFamily.Cut)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сквозной режим измерен и поддержан только для вырезания; base/boss остаются с глубиной.");
        }

        if (sides.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "У признака не читается ни одна сторона: менять нечего, правка не выполнена.",
                RetryPolicy.ReacquireContext);
        }

        var writes = new List<NamedCheck>();
        if (command.DepthMm is not null || command.EndCondition is not null)
        {
            foreach (var side in sides)
            {
                var endType = command.EndCondition switch
                {
                    ExtrudeEndCondition.Through => EndConditionThrough,
                    ExtrudeEndCondition.Blind => EndConditionBlind,
                    _ => side.EndConditionType,
                };
                var depth = command.EndCondition == ExtrudeEndCondition.Through
                    ? 0d
                    : command.DepthMm ?? side.DepthMm;

                // Draft and its orientation carry over from what the feature reports: a parameter the
                // caller never mentioned must not silently reset to zero.
                if (!WriteSide(definition!, side.Side, endType, depth, side.DraftValue, side.DraftOutward))
                {
                    throw new KompasContractException(
                        ErrorCodes.GeometryFailed,
                        $"SetSideParam вернул false для стороны {(side.Side ? "1" : "2")}: признак не изменён.",
                        RetryPolicy.ReacquireContext,
                        partialEffects: true,
                        details: new Dictionary<string, object?> { ["side"] = side.Side, ["depth_mm"] = depth });
                }

                writes.Add(new NamedCheck(
                    $"set_side_{(side.Side ? "1" : "2")}",
                    true,
                    Observed: $"type={endType} depth={depth:0.####}",
                    Expected: $"type={endType} depth={depth:0.####}"));
            }
        }

        // Changing the reference sketch of the same feature. This is a support edit (docs/05 §4.3), not a
        // re-creation: the feature stays the same tree object with the same name.
        ksEntity? newSketch = null;
        string? expectedSketchName = null;
        if (command.SketchRef is not null)
        {
            var target = RequireSketch(command.SketchRef);
            if (target.Document.Id != document.Id)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Эскиз принадлежит другому документу: перенос признака между документами не " +
                    "выполняется, признак не изменён.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["feature_document"] = document.Id,
                        ["sketch_document"] = target.Document.Id,
                    });
            }

            // The extrusion interop has no common interface: SetSketch is declared on each concrete
            // definition, so the branching is mandatory. Write to a fresh definition object. NEGATIVE
            // RESULT, MEASURED (row L11): SetSketch returns true, but GetSketch() reads back the
            // PREVIOUS sketch and the volume does not change — the support change is not applied.
            var writeTarget = entity.GetDefinition() ?? definition!;
            var accepted = writeTarget switch
            {
                ksBaseExtrusionDefinition b => b.SetSketch(target.Sketch),
                ksBossExtrusionDefinition s => s.SetSketch(target.Sketch),
                ksCutExtrusionDefinition c => c.SetSketch(target.Sketch),
                _ => false,
            };
            if (accepted != true)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "SetSketch не подтверждён (false или неизвестный тип определения): признак не изменён.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true);
            }

            newSketch = target.Sketch;
            expectedSketchName = target.Sketch.name;
        }

        // P2.3: without Update() the document keeps the old geometry even though every setter said
        // true, so this call is part of the contract, not an optimisation.
        var updated = entity.Update();
        document.Document3D.RebuildDocument();

        // INVARIANT: the refusal must happen BEFORE the revision bump (row L12: otherwise the document
        // got a new revision with an unchanged model and all references went stale). Read back through a
        // NEW definition object — the instance held during the write may be a cached view.
        var after = ReadExtrusion(entity.GetDefinition() ?? definition!);
        var readBack = after?.Sides.FirstOrDefault(s => s.Side == sides[0].Side);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var expectedType = command.EndCondition switch
        {
            ExtrudeEndCondition.Through => EndConditionThrough,
            ExtrudeEndCondition.Blind => EndConditionBlind,
            null => (short?)null,
            _ => (short?)null,
        };
        var valueStored = readBack is not null
            && (expectedType is null || readBack.EndConditionType == expectedType)
            && (command.DepthMm is null
                || command.EndCondition == ExtrudeEndCondition.Through
                || Math.Abs(readBack.DepthMm - command.DepthMm.Value) <= 1e-6);

        // The same feature, not a replacement: the tree must hold the same number of features and
        // carry the same name. Probe P2.3 measured 3→3 with updateStamp advancing.
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>(writes)
        {
            new("entity_update", updated, Observed: updated ? "True" : "False"),
            new("value_read_back", valueStored,
                Observed: readBack is null
                    ? "не читается"
                    : $"type={readBack.EndConditionType} depth={readBack.DepthMm:0.####}",
                Expected: expectedType is short t
                    ? $"type={t}"
                    : $"depth={command.DepthMm:0.####}"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        // Read back from a NEW definition object: the one written through may be a cached view.
        var sketchConfirmed = true;
        var sketchIdentityByReadBack = true;
        if (command.SketchRef is not null)
        {
            var readBackDefinition = entity.GetDefinition();
            var readBackSketch = readBackDefinition switch
            {
                ksBaseExtrusionDefinition b => b.GetSketch() as ksEntity,
                ksBossExtrusionDefinition s => s.GetSketch() as ksEntity,
                ksCutExtrusionDefinition c => c.GetSketch() as ksEntity,
                _ => null,
            };
            var nameAfter = readBackSketch?.name;
            sketchConfirmed = !string.IsNullOrEmpty(nameAfter) && nameAfter == expectedSketchName;
            sketchIdentityByReadBack = ReferenceEquals(readBackSketch, newSketch);
            checks.Add(new NamedCheck(
                "sketch_read_back",
                sketchConfirmed,
                Observed: nameAfter is null ? "GetSketch() не вернул объект" : $"«{nameAfter}»",
                Expected: $"«{expectedSketchName}»"));
        }

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            volumeMatched = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_after_update",
                volumeMatched,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется; для этого существует приёмочная строка G03",
        };
        var geometryConfirmed = valueStored && updated && sameFeature && volumeMatched && sketchConfirmed;
        if (command.SketchRef is not null)
        {
            if (!sketchConfirmed)
            {
                unverified.Insert(0, "sketch_not_read_back — GetSketch() не перечитал новое имя: признак " +
                                     "мог остаться на прежнем профиле");
            }
            if (!sketchIdentityByReadBack)
            {
                unverified.Add("sketch_identity_by_name_only — совпадение проверено по имени: тот же " +
                               "COM-объект из нового RCW не обязан быть тем же .NET-объектом");
            }
        }
        if (!geometryConfirmed)
        {
            unverified.Insert(0, valueStored
                ? "volume_not_as_expected — значение записано и перечитано, но измерение объёма не совпало с ожиданием"
                : "value_not_read_back — параметр не перечитался с нового объекта определения");
        }
        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма правка не может быть подтверждена геометрически");
        }

        if (command.SketchRef is not null && !sketchConfirmed
            && volumeBefore is double vb && volumeAfter is double va
            && Math.Abs(va - vb) <= ProfileArea.Tolerance(vb))
        {
            // KOMPAS accepted SetSketch and did not change the model; reporting "success" would lie
            // about the support edit, so the route refuses after the read-back.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Смена опорного эскиза существующего признака не применена: GetSketch() перечитал " +
                "прежний эскиз, объём не изменился. Отказ принадлежит измеренным маршрутам " +
                "перепривязки профиля (API5 SetSketch+Update+RebuildDocument, строка L11; API7 " +
                "IExtrusion.Sketch и IExtrusion1.Profile, проба E) и НЕ означает, что признаки " +
                "неправимы через API: правка параметров этого же признака работает (G03). " +
                "Признак и документ не тронуты.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["sketch_read_back"] = expectedSketchName,
                    ["volume_mm3"] = va,
                    ["scope"] = "profile_retarget_of_existing_api5_feature",
                    ["route_attempted"] = "ksExtrusionDefinition.SetSketch + entity.Update + RebuildDocument",
                });
        }

        BumpRevision(document, "feature.update");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            family,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            readBack?.DepthMm,
            readBack?.EndConditionType,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    public sealed record UpdateFeatureResult(
        ReferenceDto FeatureRef,
        string Family,
        bool SameFeature,
        int FeatureCount,
        double? VolumeBeforeMm3,
        double? VolumeAfterMm3,
        double? DepthReadBackMm,
        short? EndConditionReadBack,
        VerificationDto Verification,
        /// <summary>Chamfer angle read back from the model after the edit (degrees). A trailing parameter
        /// so as not to shift the extrusion positional arguments: the field belongs only to the
        /// <c>chamfer</c> family and stays null for the others.</summary>
        double? AngleReadBackDeg = null,
        /// <summary>Fillet radius read back from the model after the edit (mm). Same principle as
        /// <see cref="AngleReadBackDeg"/>: the field belongs only to the <c>fillet</c> family and stays
        /// null for the others, not "zero".</summary>
        double? RadiusReadBackMm = null,
        /// <summary>How many edges are in the fillet set after an <c>edge_refs</c> edit. Same principle:
        /// the field belongs only to a SET edit of the <c>fillet</c> family and stays null for the others,
        /// not "zero". It is null for a radius edit too — radius and edge set are edited by different
        /// routes, and the response must not hint that the other changed.</summary>
        int? EdgesReadBack = null);

    private (DocumentEntry Document, ksEntity Entity) RequireFeatureEntity(string featureRef)
    {
        if (!References.TryGet(featureRef, out var probe) || probe is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{featureRef}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(probe.DocumentId);
        var stored = References.Require(featureRef, document.Id, document.Revision);
        var entity = stored.Payload as ksEntity
            ?? (stored.Payload as ksFeature)?.GetObject() as ksEntity;
        if (entity is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{featureRef}' указывает на {stored.Payload?.GetType().Name ?? "null"} вместо признака.",
                RetryPolicy.ReacquireContext);
        }

        return (document, entity);
    }

    /// <summary>Pretty name of the vendor end-condition type (measured in probe P2.1).</summary>
    private static string EndConditionName(short type) => type switch
    {
        0 => "blind",
        1 => "through",
        2 => "up_to_vertex_to",
        3 => "up_to_vertex_from",
        4 => "up_to_surface_to",
        5 => "up_to_surface_from",
        6 => "up_to_nearest_surface",
        _ => $"unknown_{type}",
    };

    private static class ExtrusionFamily
    {
        public const string Base = "base_extrusion";
        public const string Boss = "boss_extrusion";
        public const string Cut = "cut_extrusion";

        public static string? Of(object? definition) => definition switch
        {
            ksBaseExtrusionDefinition => Base,
            ksBossExtrusionDefinition => Boss,
            ksCutExtrusionDefinition => Cut,
            _ => null,
        };
    }

    private readonly record struct ExtrusionRead(short DirectionType, IReadOnlyList<FeatureSideDto> Sides, FeatureThinDto? Thin);

    private static ExtrusionRead? ReadExtrusion(object definition)
    {
        switch (definition)
        {
            case ksBaseExtrusionDefinition b:
                return new ExtrusionRead(
                    b.directionType,
                    SidesFrom(side => b.GetSideParam(side, out var t, out var d, out var dr, out var o)
                        ? (true, t, d, dr, o)
                        : default),
                    ThinFrom(() =>
                    {
                        var ok = b.GetThinParam(out var thin, out var kind, out var normal, out var reverse);
                        return ok ? (true, thin, kind, normal, reverse) : default;
                    }));
            case ksBossExtrusionDefinition x:
                return new ExtrusionRead(
                    x.directionType,
                    SidesFrom(side => x.GetSideParam(side, out var t, out var d, out var dr, out var o)
                        ? (true, t, d, dr, o)
                        : default),
                    // The boss interop declares this one by ref, not out — hence its own branch.
                    ThinFrom(() =>
                    {
                        bool thin = false;
                        short kind = 0;
                        double normal = 0d;
                        double reverse = 0d;
                        var ok = x.GetThinParam(ref thin, ref kind, ref normal, ref reverse);
                        return ok ? (true, thin, kind, normal, reverse) : default;
                    }));
            case ksCutExtrusionDefinition c:
                return new ExtrusionRead(
                    c.directionType,
                    SidesFrom(side => c.GetSideParam(side, out var t, out var d, out var dr, out var o)
                        ? (true, t, d, dr, o)
                        : default),
                    ThinFrom(() =>
                    {
                        var ok = c.GetThinParam(out var thin, out var kind, out var normal, out var reverse);
                        return ok ? (true, thin, kind, normal, reverse) : default;
                    }));
            default:
                return null;
        }
    }

    private static IReadOnlyList<FeatureSideDto> SidesFrom(
        Func<bool, (bool Ok, short Type, double Depth, double Draft, bool Outward)> read)
    {
        var sides = new List<FeatureSideDto>(2);
        foreach (var side in new[] { true, false })
        {
            var value = read(side);
            if (value.Ok)
            {
                sides.Add(new FeatureSideDto(
                    side, value.Type, EndConditionName(value.Type), value.Depth, value.Draft, value.Outward));
            }
        }

        return sides;
    }

    private static FeatureThinDto? ThinFrom(
        Func<(bool Ok, bool Thin, short Kind, double Normal, double Reverse)> read)
    {
        var value = read();
        return value.Ok
            ? new FeatureThinDto(value.Thin, value.Kind, value.Normal, value.Reverse)
            : null;
    }

    private static bool WriteSide(object definition, bool side, short type, double depth, double draft, bool outward)
        => definition switch
        {
            ksBaseExtrusionDefinition b => b.SetSideParam(side, type, depth, draft, outward),
            ksBossExtrusionDefinition b => b.SetSideParam(side, type, depth, draft, outward),
            ksCutExtrusionDefinition b => b.SetSideParam(side, type, depth, draft, outward),
            _ => false,
        };

    private readonly record struct FeatureState(
        string Name, int UpdateStamp, bool? IsValid, bool Excluded, bool IsRollBacked, int ObjectError, string? OwnerName);

    private static FeatureState ReadFeatureState(ksEntity entity)
    {
        var feature = entity.GetFeature() as ksFeature;
        bool? isValid = null;
        var rollback = false;
        var error = 0;
        string? owner = null;
        var stamp = 0;

        try
        {
            if (feature is not null)
            {
                isValid = feature.IsValid();
                rollback = feature.IsRollBacked();
                error = feature.objectError;
                stamp = unchecked((int)feature.updateStamp);
                owner = (feature.GetOwnerFeature() as ksFeature)?.name;
            }
        }
        catch (COMException)
        {
            // A state KOMPAS refuses to report stays reported as unreadable, not as healthy.
            isValid = null;
        }

        return new FeatureState(
            entity.name ?? feature?.name ?? string.Empty,
            stamp,
            isValid,
            entity.excluded || (feature?.excluded ?? false),
            rollback,
            error,
            owner);
    }
}
