using System.Runtime.InteropServices;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;
using Kompas6API5;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Reading and editing the parameters of a feature that already exists (docs/05 §4.3, §7).
///
/// Everything here rests on what probe P2.3 measured on the real v24 install, not on what the
/// API5 names suggest:
/// * the setter is accepted, but the geometry only recomputes after <c>ksEntity.Update()</c> —
///   <c>RebuildDocument()</c> alone left the volume at its old value, and
///   <c>ksPart.EndEdit(Rebuild=false)</c> returned False;
/// * <c>ksFeature.type</c> is 105 (o3d_entity) for every family, so the family is taken from the
///   definition object, never from that number;
/// * the entity handed back by <c>NewEntity(24)</c> reports 24 while the same committed feature
///   read out of the tree reports 25, so a type number captured at creation is not an identity.
///
/// The three extrusion definitions share no common interface for these getters, and
/// <c>GetThinParam</c> is declared <c>out</c> on base/cut but <c>ref</c> on boss, so the families
/// are branched explicitly instead of being unified by reflection.
/// </summary>
public partial class Api5Session
{
    public FeatureReadDto GetFeature(GetFeatureCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);
        var definition = entity.GetDefinition();
        // Семейство отверстия опознаётся по ТИПУ СУЩНОСТИ, а не по определению: в вендорской
        // обёртке ksHoleDefinition не существует вовсе (измерено 16.09.2026 — среди 67 объявленных
        // определений есть ksChamferDefinition и ksFilletDefinition, отверстия нет), поэтому
        // параметры режима живут только в API7 и читаются оттуда. Тип — 583 (o3d_Hole3D), а не 52:
        // 52 (o3d_holeOperation) это номер, под которым признак СОЗДАЁТСЯ через NewEntity, и в
        // дереве он не появляется (измерено пробой N.1 от 17.09.2026, см. FindHoleEntity).
        var isHole = entity.type == KompasObjectTypes.Hole3D;
        // Вращение опознаётся так же, как его находит FindRotatedEntity, — по НОМЕРУ ПРИЗНАКА В
        // ДЕРЕВЕ (27/28/29), а не по ответу на QI(IRotated): сущность из дерева приходит сырым
        // __ComObject и на QI отвечает отказом (измерено при приёмке SM-03 18.09.2026).
        var isRotated = IsRotatedEntity(entity);
        // Признаки B3 опознаются по НОМЕРУ В ДЕРЕВЕ (69 / 633 / 50 / 79) по той же причине, что и
        // вращение: определения API5 у них нет вовсе, GetDefinition() возвращает null, а объект API7
        // признаком API5 не является, поэтому ни определение, ни QI здесь не годятся. Номера измерены
        // 18.09.2026 прибором scratch/b3-measure-feature-types.py.
        var solidFamily = SolidFamilyOf(entity.type);
        // B5: три семейства очереди B5 опознаются ПО ИНТЕРФЕЙСУ ОПРЕДЕЛЕНИЯ, а не по номеру типа.
        // Измерено 20.09.2026 (проба --b5, шаг B5.12): признак, созданный NewEntity(45)
        // (o3d_baseEvolution), виден в дереве под номером 46 (o3d_bossEvolution), а его определение
        // отвечает ksBossEvolutionDefinition — НЕ ksBaseEvolutionDefinition. Опознание по номеру из
        // ответа создания не нашло бы его никогда: тот же дефект уже измерен у отверстия (52 → 583) и
        // у вращения (27 → 584). Поэтому принимаются оба интерфейса каждого семейства.
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

        // Фаска читается отдельно: у неё нет сторон и тонкой стенки, зато есть катеты, признак
        // стороны, а угол — только в API7. Пустое поле здесь означает «не прочитано», а не 0.
        var chamfer = definition is ksChamferDefinition ? ReadChamfer(document, definition) : null;
        // Скругление — по той же логике: радиус читается из API7, потому что запись в определение
        // API5 на существующем признаке не применяется (FL04r).
        var fillet = definition is ksFilletDefinition ? ReadFillet(document, definition) : null;
        // Отверстие — целиком из API7: определение API5 его режимных чисел не хранит физически.
        var hole = isHole ? ReadHoleFeature(document) : null;
        // Вращение — тоже из API7, и по той же причине: набор параметров живёт на IRotated, а
        // определения API5 у него нет вовсе (entity.GetDefinition() возвращает null, измерено при
        // приёмке SM-03). Индекс признака берётся сопоставлением по составу, а не по углу: угол не
        // идентификатор, и FindIndexesByAngle остаётся запасным средством.
        var rotated = isRotated ? ReadRotatedFeature(document, entity) : null;
        // Признаки B3 (boolean/split/cut_by_plane/reposition) — целиком из API7: определения API5 у
        // них нет, а читаются они с ЖИВОЙ модели по маршрутам, измеренным пробами BO.2–BO.11, SP.10 и
        // RP.8–RP.12. Часть параметров у семейства изменения положения не читается вовсе, и причина
        // называется в solid.unreadable_parameters, а не подставляется нулём.
        var solid = solidFamily is null ? null : ReadSolidFeature(document, entity, solidFamily);
        // B5: кинематика, сечения и оболочка читаются ИЗ МОДЕЛИ. Причина неудачи НАЗЫВАЕТСЯ, а не
        // остаётся пустым полем: «не прочитано» и «ноль» обязаны быть различимы — молчание тоже
        // утверждение, и пустое поле неотличимо от «забыли заполнить».
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
                        // «Прочитано» у B5 означает «прочитан содержательный параметр», а не «метод
                        // вернулся»: DTO непустой уже тогда, когда определение опознано.
                        : family == EvolutionFamily
                            ? sweep?.ShiftMode is not null && sweep.PathPartCount is not null
                            : family == LoftFamily
                                ? loft is not null && loft.SectionCount is not null
                                : family == ShellFamily
                                    ? shell?.ThicknessMm is not null
                                    : sides.Count > 0;
        if (solidFamily is not null)
        {
            // У семейств B3 «прочитано» означает «прочитан хоть один содержательный параметр»: у
            // переноса вида операции нет, у булевой операции нет опоры, и требовать общее поле
            // значило бы объявить исправное чтение неудавшимся.
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
        var unverified = new List<string>
        {
            "sketch_reference_not_resolved — GetSketch() отдаёт объект, но обратно в ссылку сервера он пока не отображается",
        };
        if (family is null)
        {
            unverified.Insert(0,
                "family_not_supported — параметрическое чтение заявлено для выдавливаний, фаски, скругления, вращения, родного отверстия, четырёх семейств B3 (булева операция, разделение, отсечение, изменение положения) и трёх семейств B5 (кинематическая операция, элемент по сечениям, оболочка); другие семейства в v1 не раскрываются");
        }
        else if (family == EvolutionFamily && sweepNote is not null)
        {
            // Причина называется ИМЕНЕМ, а не пустым полем: OperationResult живёт только в API7, и
            // его отсутствие — это граница маршрута, а не ноль.
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
            // Одна строка на всё семейство, а не по строке на поле: имена и ИЗМЕРЕННЫЕ причины уже
            // лежат в solid.unreadable_parameters, и дублировать их здесь значило бы сделать сводку
            // нечитаемой. Важно другое — что вызывающий узнаёт о границе чтения из сводки, а не
            // отсутствием поля.
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
            shell);
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
        // Правка массива (очередь B4) выбирается по САМОМУ ПОЛЮ pattern, а не по номеру типа в дереве:
        // у остальных семейств номер измерен, а для массива — нет, и угадывать его нельзя. См.
        // Api5Session.PatternEdit.cs.
        var patternRequested = command.Pattern is not null;

        // Правка трёх семейств последней очереди B5 (наряд §11). Семейство выбирается по САМОМУ ПОЛЮ,
        // а не по номеру типа в дереве: у кинематической операции номер СОЗДАНИЯ и номер ДЕРЕВА
        // расходятся (45 → 46, измерено шагом B5.12), и адресовать по номеру значило бы править не
        // тот признак. См. Api5Session.FeatureEdit.B5.cs.
        var sweepRequested = command.ShiftMode is not null;
        var loftRequested = command.SectionRefs is not null;
        var shellRequested = command.ThicknessMm is not null || command.ThinInward is not null
                             || command.FaceRefs is not null;

        // `couplings` — ВТОРОЙ ВХОД СЕМЕЙСТВА «ЭЛЕМЕНТ ПО СЕЧЕНИЯМ», И ОН НЕ ВЫБИРАЕТ СЕМЕЙСТВО.
        // Различие существенное, поэтому оно названо отдельным признаком, а не подмешано в
        // loftRequested: цепочки соответствия описывают соответствие точек УЖЕ ЗАДАННОГО набора
        // сечений, поэтому сами по себе они семейство не выбирают — без section_refs менять нечего
        // (UpdateLoftFeature отвергает такой вызов по имени), и ветку выбирать по ним значило бы
        // увести вызов «couplings к выдавливанию» в ветку сечений, где он получил бы неверный текст
        // отказа. Но в перечнях ЧУЖИХ полей couplings обязан стоять: без этого он был принят и
        // проглочен. Измерено 20.09.2026 зондом scratch/_couplings_scope_probe.py: вызов
        // «distance1_mm + couplings» на фаске вернул успех и объём 79840 → 79955 (правка применилась,
        // цепочки — нет), тогда как соседние shift_mode и section_refs в том же вызове отвергнуты
        // INVALID_ARGUMENT. Нашёл пропуск unit-тест SolidFeatureClassificationTests.
        var couplingsRequested = command.Couplings is not null;

        // Отверстие (SM-07): поля режима. Признак выбирается НЕ этими полями, а типом сущности в
        // дереве (583) — как и в чтении; признак ниже нужен для проверки «поля отверстия пришли не
        // на признак отверстия» и для проверки «не указано ни одного параметра».
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

        // Массив — девятое правимое семейство B4 (наряд §7: действие edit). Ветка стоит ДО чтения
        // определения API5: у признака массива определения API5 нет вовсе (объект создан фабрикой
        // API7), поэтому ниже он попал бы в «этот признак — null» и правка была бы недостижима.
        if (patternRequested)
        {
            return UpdatePattern(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Отверстие — десятое правимое семейство (наряд SM07 §3.2, очередь B2). Ветка стоит ЗДЕСЬ, а
        // не среди ветвей по определению API5: определения у родного отверстия НЕТ ВОВСЕ — типа
        // ksHoleDefinition в вендорском интеропе не существует среди 67 объявленных определений
        // (измерено 16.09.2026 и записано в docs/04), и `kompas_get_feature` читает отверстие тем же
        // путём, минуя определение (definition_interface = null, измерено зондом
        // scratch/_hole_edit_probe.py). Опознание — по ТИПУ СУЩНОСТИ В ДЕРЕВЕ, ровно как в чтении:
        // 583 (o3d_Hole3D), а не 52 (o3d_holeOperation, номер ФАБРИКИ создания) — проба N.1
        // напечатала обе стороны: NewEntity(52).type = 52, а живой IHoles3D[0].ModelObjectType = 583,
        // и в дереве появилась ровно одна запись под 583. Поиск по 52 не нашёл бы признак никогда.
        if (entity.type == KompasObjectTypes.Hole3D)
        {
            return UpdateHole(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        var definition = entity.GetDefinition();

        if (definition is ksChamferDefinition)
        {
            // Семейство определяет, какие поля вообще имеют смысл: отдать фаске depth_mm и
            // промолчать о том, что он не применён, — значит соврать про правку.
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

        // Скругление — третье правимое семейство. Радиус у ksFilletDefinition объявлен, но запись
        // в него на существующем признаке НЕ применяется (измерено строкой FL04r), поэтому идёт
        // маршрут API7; поля других семейств к скруглению не применяются и отвергаются здесь, а не
        // игнорируются.
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

            // Радиус и набор рёбер — разные маршруты (радиус идёт через API7, набор — через
            // определение API5) и разные предметы правки. Смешивать их в одном вызове нельзя:
            // «поменял и то, и другое» не отличимо потом от «применилось одно из двух».
            if (command.EdgeRefs is not null || command.BaseObjectRefs is not null)
            {
                return UpdateFilletEdgeSet(document, entity, command, volumeBefore, featuresBefore, stateBefore);
            }

            return UpdateFilletRadius(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Вращение — четвёртое правимое семейство, и оно опознаётся по НОМЕРУ ПРИЗНАКА В ДЕРЕВЕ
        // (27/28/29), а не по определению: определения API5 у вращения нет вовсе, GetDefinition()
        // возвращает null, а QI(IRotated) на сущности из дерева отвечает отказом (измерено 18.09.2026).
        // Правка вращения — только угол и направление; перепривязка профиля и оси не измерялась, и
        // поля других семейств отвергаются здесь, а не игнорируются.
        if (IsRotatedEntity(entity))
        {
            return UpdateRotated(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Изменение положения тела — пятое правимое семейство (наряд B3 §5, §7: действие edit).
        // Опознаётся по НОМЕРУ ПРИЗНАКА В ДЕРЕВЕ (79), как и вращение: определения API5 у него нет
        // вовсе (GetDefinition() возвращает null), а объект API7 признаком API5 не является —
        // поэтому ни определение, ни QI здесь не годятся. Номер 79 измерен 18.09.2026 прибором
        // scratch/b3-measure-feature-types.py; 569 (o3d_BodyReposition) — сторона СОЗДАНИЯ, в дереве
        // признак лежит под 79.
        if (entity.type == KompasObjectTypes.BodyRepositionFeature)
        {
            return UpdateSolidReposition(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Разделение и отсечение — шестое и седьмое правимые семейства B3 (наряд §5, §7: действие
        // edit). Опознаются по НОМЕРУ ПРИЗНАКА В ДЕРЕВЕ (633 и 50) по той же причине, что и
        // изменение положения: определения API5 у них нет, а объект API7 (ISplitSolid, ICut) признаком
        // API5 не является. Номера измерены 18.09.2026 прибором scratch/b3-measure-feature-types.py;
        // 633 — это o3d_SplitSolid, 50 — o3d_cutByPlane, и оба числа читаются с ДЕРЕВА, а не с
        // фабрики создания (у разделения фабрика и дерево расходятся так же, как у отверстия 52/583).
        if (entity.type == KompasObjectTypes.SplitSolid)
        {
            return UpdateSolidSplit(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        if (entity.type == KompasObjectTypes.CutByPlane)
        {
            return UpdateSolidCutByPlane(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Булева операция — восьмое правимое семейство B3. Опознаётся по номеру 69 (o3d_aggregate) в
        // дереве, а не по определению API5: маршрут через ksAggregateDefinition измеренно не работает
        // (у него есть записываемый BooleanType и НЕТ ни одного способа задать тела — §4.10.5,
        // OQ-A16). Правка вида операции маршрутом API7 измерена 18.09.2026 пробой --boolean, шаг
        // BO.11: перезапись IBoolean.BooleanType на существующем признаке меняет геометрию, а пара
        // «запись + Update()» подтверждена контролем E-E.
        if (entity.type == KompasObjectTypes.BooleanOperation)
        {
            return UpdateSolidBoolean(document, entity, command, volumeBefore, featuresBefore, stateBefore);
        }

        // Три семейства последней обязательной очереди B5 (наряд §11). Ветка стоит ЗДЕСЬ, а не раньше:
        // выше семейства с определениями API5 уже отвергли чужие поля по имени, и «shift_mode к
        // фаске» читается вызывающему понятнее, чем «признак не отвечает интерфейсу кинематики».
        // Опознание — по САМОМУ ПОЛЮ, а не по номеру типа в дереве: у кинематической операции номер
        // создания и номер дерева расходятся (45 → 46, шаг B5.12), и адресовать по номеру значило бы
        // править не тот признак. См. Api5Session.FeatureEdit.B5.cs.
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

        // Смена опорного эскиза того же признака. Это правка опоры (docs/05 §4.3), а не пересоздание:
        // признак остаётся тем же объектом дерева с тем же именем.
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

            // Общего интерфейса выдавливания в interop нет: SetSketch объявлен у каждого
            // конкретного определения, поэтому ветвление обязательное, а не удобство.
            // Пишем в свежий объект определения (та же дисциплина, что и для чтения — P2.3).
            // ОТРИЦАТЕЛЬНЫЙ РЕЗУЛЬТАТ, измерен 12.09.2026 (строка L11): SetSketch возвращает true,
            // но GetSketch() перечитывает ПРЕЖНИЙ эскиз и объём не меняется — то есть смена опоры
            // этим маршрутом не применяется. Гипотеза про кэшированный RCW не подтвердилась:
            // запись в свежий объект даёт тот же исход. Режим остаётся заблокированным, а не «готовым».
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
        document.Document.RebuildDocument();

        // Отказ обязан случиться ДО повышения ревизии. Измерено строкой L12 прогона 12.09.2026:
        // когда отказ шёл после BumpRevision, документ получал новую ревизию при неизменной модели,
        // и все выданные ссылки устарели из-за операции, которая ничего не сделала.
        // Read back through a NEW definition object — the instance held during the write may be a
        // cached view, and "we set it" is not evidence the model stored it.
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

        // Перечитывается с НОВОГО объекта определения: тот, чем писали, может быть кэшированным
        // представлением, а «мы вызвали SetSketch» доказательством того, что модель приняла, не является.
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
            // КОМПАС принял SetSketch и не изменил модель. Отдать это как «успех» — значит наврать
            // вызывающему про факт правки опоры, поэтому маршрут отказывает честно и уже после
            // перечитывания: признак цел, объём прежний, изменения нет.
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
        /// <summary>
        /// Угол фаски, перечитанный из модели после правки (градусы). Отдельным хвостовым
        /// параметром, чтобы не сдвигать позиционные аргументы выдачивания: поле относится только
        /// к семейству <c>chamfer</c> и у остальных семейств остаётся null.
        /// </summary>
        double? AngleReadBackDeg = null,
        /// <summary>
        /// Радиус скругления, перечитанный из модели после правки (мм). Тот же принцип, что у
        /// <see cref="AngleReadBackDeg"/>: поле относится только к семейству <c>fillet</c> и у
        /// остальных семейств остаётся null, а не «ноль».
        /// </summary>
        double? RadiusReadBackMm = null,
        /// <summary>
        /// Сколько рёбер в наборе скругления после правки <c>edge_refs</c>. Тот же принцип: поле
        /// относится только к правке НАБОРА у семейства <c>fillet</c> и у остальных случаев
        /// остаётся null, а не «ноль». При правке радиуса тоже null — радиус и набор рёбер правятся
        /// разными маршрутами, и ответ не должен намекать, будто изменилось и второе.
        /// </summary>
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
            // A state КОМПАС refuses to report stays reported as unreadable, not as healthy.
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
