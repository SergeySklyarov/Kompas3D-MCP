using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Contracts;

namespace KompasMcp.Api5Adapter;

/// <summary>Dynamic cross-sections of a model — block G11 (order <c>OMEGA_G11_SECTIONS</c>).</summary>
/// <remarks>DOC: <c>idynamiccrosssectionsmanager.html</c> — «Интерфейс является дополнительным к интерфейсу
/// документа IKompasDocument3D. Данный интерфейс можно получить с помощью метода IUnknown::QueryInterface»,
/// «Версия Компас v18.1»; <c>idynamiccrosssection_addstep.html</c>;
/// <c>idynamiccrosssectionstepparametersbyoffsetplane_offsetplane.html</c>.
/// INVARIANT: a section is confirmed by reading it back after <c>Update()</c>, never by the object the add
/// call returned. History: docs/decisions/sections.md#route</remarks>
public sealed partial class Api5Session
{
    private const string SectionsRoute =
        "api7:QI(IKompasDocument3D → IDynamicCrossSectionsManager) → AddDynamicCrossSection → " +
        "AddStep(ksDCSTByOffsetPlane) → Parameters → QI(IDynamicCrossSectionStepParametersByOffsetPlane) → " +
        "OffsetPlane → QI(IPlane3DByOffset) → BasePlane/Offset → section.Update(); чтение — " +
        "DynamicCrossSectionsCount/DynamicCrossSection[i]/Step[j]/Parameters/OffsetPlane";

    /// <summary>Read the section mode and the sections of the document.</summary>
    public ListSectionsResult ListSections(ListSectionsCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var notes = new List<string>();
        var (_, manager) = RequireSections(document, notes);

        return new ListSectionsResult(
            Bool(() => manager.DynamicCrossSectionModeOn),
            Int(() => manager.DynamicCrossSectionsCount) ?? 0,
            ReadSections(manager, notes),
            SectionsRoute,
            notes);
    }

    /// <summary>Create the first section of the document and write its steps.</summary>
    /// <remarks>MEASURED: <c>AddDynamicCrossSection()</c> answered null once a section already existed and
    /// after one was deleted, so this release creates the FIRST section only and names the limit.
    /// MEASURED: <c>IPlane3DByOffset.Update()</c> answered false while the written values were applied by
    /// <c>IDynamicCrossSection.Update()</c>, so confirmation is a re-read after the section update.
    /// History: docs/decisions/sections.md#probe</remarks>
    public CreateSectionResult CreateSection(CreateSectionCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        var notes = new List<string>();
        var (_, manager) = RequireSections(document, notes);
        var checks = new List<NamedCheck>();
        var unverified = new List<string>();

        var before = Int(() => manager.DynamicCrossSectionsCount);
        checks.Add(new NamedCheck("sections_count_read_before", before is not null, before?.ToString()));

        IDynamicCrossSection? section;
        try
        {
            section = Ref(() => manager.AddDynamicCrossSection());
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "AddDynamicCrossSection() прервался: " + ex.Message,
                RetryPolicy.Never);
        }

        if (section is null)
        {
            // MEASURED: the second call in a session answered null both with a section present and after
            // the only section was deleted. A section is not substituted for the refused one.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "AddDynamicCrossSection() не вернул IDynamicCrossSection: в этом документе сечение уже " +
                "есть (измерено: второе сечение за сеанс не создаётся, в том числе после удаления " +
                "первого). Создание второго сечения этим выпуском не поддерживается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["sections_count"] = before,
                    ["interface"] = "IDynamicCrossSectionsManager",
                });
        }

        if (command.FillCutPlanes is bool fill)
        {
            try
            {
                section.FillCutPlanes = fill;
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Запись IDynamicCrossSection.FillCutPlanes прервалась: " + ex.Message,
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        foreach (var step in command.Steps ?? Array.Empty<SectionStepDto>())
        {
            WriteStep(document, section, step, checks, unverified, notes);
        }

        var updated = Bool(() => section.Update());
        checks.Add(new NamedCheck("section_update_returned", updated == true, updated?.ToString()));
        if (updated != true)
        {
            unverified.Add("section_update_unconfirmed — section.Update() не подтвердил запись: " +
                           "сечение могло остаться без шага");
        }

        if (command.Mode is bool mode)
        {
            try
            {
                manager.DynamicCrossSectionModeOn = mode;
                checks.Add(new NamedCheck("mode_written", Bool(() => manager.DynamicCrossSectionModeOn) == mode));
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Запись IDynamicCrossSectionsManager.DynamicCrossSectionModeOn прервалась: " +
                    ex.Message + ". Сечение могло быть записано, режим — нет.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        document.Document3D.RebuildDocument();
        BumpRevision(document, "model.create_section");

        var after = Int(() => manager.DynamicCrossSectionsCount);
        checks.Add(new NamedCheck("sections_count_after", after is not null, after?.ToString(),
            command.Steps is null ? null : command.Steps.Count.ToString()));

        // LIMIT: a snapshot is a raster and the section is display only, so "the section is visible" is
        // measured by the acceptance group, not by this call.
        unverified.Add("section_visible_not_checked — видимость сечения на снимке проверяет приёмка " +
                       "(группа --sections-only), а не этот вызов");

        return new CreateSectionResult(
            Bool(() => manager.DynamicCrossSectionModeOn),
            before ?? 0,
            after ?? 0,
            ReadSections(manager, notes),
            new VerificationDto(VerificationLevel.StructureChecked, checks, unverified),
            notes);
    }

    /// <summary>Turn the section display mode on or off.</summary>
    public SetSectionModeResult SetSectionMode(SetSectionModeCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);
        var notes = new List<string>();
        var (_, manager) = RequireSections(document, notes);
        var checks = new List<NamedCheck>();

        try
        {
            manager.DynamicCrossSectionModeOn = command.Mode;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Запись DynamicCrossSectionModeOn прервалась: " + ex.Message,
                RetryPolicy.Never);
        }

        var read = Bool(() => manager.DynamicCrossSectionModeOn);
        checks.Add(new NamedCheck("mode_read_back", read == command.Mode, read?.ToString(),
            command.Mode.ToString()));
        if (read != command.Mode)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Режим сечения записан как {command.Mode}, а читается обратно {read?.ToString() ?? "null"}: " +
                "запись не подтверждена.",
                RetryPolicy.Never);
        }

        BumpRevision(document, "model.set_section_mode");
        return new SetSectionModeResult(read!.Value,
            new VerificationDto(VerificationLevel.StructureChecked, checks, Array.Empty<string>()),
            notes);
    }

    /// <summary>One step: the documented kind, then the plane handed out by the parameters, configured
    /// through <c>IPlane3DByOffset</c>. DOC: <c>idynamiccrosssectionstep_parameters.html</c> — the member
    /// is read only and typed <c>IKompasAPIObject</c>, so the concrete kind is reached by a cast.
    /// History: docs/decisions/sections.md#route</summary>
    private void WriteStep(
        DocumentEntry document,
        IDynamicCrossSection section,
        SectionStepDto step,
        List<NamedCheck> checks,
        List<string> unverified,
        List<string> notes)
    {
        var created = Ref(() => section.AddStep(ksDynamicCrossSectionStepBuildingTypeEnum.ksDCSTByOffsetPlane));
        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "AddStep(ksDCSTByOffsetPlane) не вернул IDynamicCrossSectionStep: шаг не создан.",
                RetryPolicy.Never);
        }

        var parameters = Ref(() => created.Parameters);
        if (parameters is not IDynamicCrossSectionStepParametersByOffsetPlane byOffset)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Параметры шага не отвечают QI(IDynamicCrossSectionStepParametersByOffsetPlane): " +
                "маршрут шага смещённой плоскостью недоступен.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["interface"] = "IDynamicCrossSectionStepParametersByOffsetPlane",
                });
        }

        var plane = Ref(() => byOffset.OffsetPlane);
        if (plane is not IPlane3DByOffset planeByOffset)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "OffsetPlane шага не отвечает QI(IPlane3DByOffset): плоскость задать нечем.",
                RetryPolicy.Never);
        }

        var basePlane = RequireSectionPlane(document, step.Plane, notes);
        try
        {
            planeByOffset.BasePlane = basePlane;
            planeByOffset.Offset = step.Plane.OffsetMm;
            planeByOffset.Direction = true;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Запись входов плоскости сечения прервалась: " + ex.Message +
                ". Сечение могло остаться без шага; удалите сечение перед повтором.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        var offset = Double(() => planeByOffset.Offset);
        checks.Add(new NamedCheck("plane_offset_written", offset is not null,
            offset?.ToString("0.###", CultureInfo.InvariantCulture),
            step.Plane.OffsetMm.ToString("0.###", CultureInfo.InvariantCulture)));
        if (offset is null)
        {
            unverified.Add("plane_offset_unread — смещение плоскости не прочитано: запись не подтверждена");
        }

        if (step.ReverseDirection)
        {
            var reversed = Bool(() => byOffset.ReverseDirection());
            checks.Add(new NamedCheck("reverse_direction_returned", reversed == true, reversed?.ToString()));
            if (reversed != true)
            {
                unverified.Add("reverse_direction_unconfirmed — ReverseDirection() не подтвердил " +
                               "смену направления");
            }
        }

        notes.Add("step_written — вид шага ksDCSTByOffsetPlane, опора «" +
                  (step.Plane.Base?.ToString() ?? step.Plane.Reference ?? "?") + "», смещение " +
                  step.Plane.OffsetMm.ToString("0.###", CultureInfo.InvariantCulture) + " мм");
    }

    /// <summary>The plane of a step as an API7 object: a standard plane with an offset, or a reference to
    /// an existing plane — the same address a sketch support takes.</summary>
    /// <remarks>INVARIANT: <c>base</c> and <c>reference</c> together, or neither, are refused: an
    /// accepted-and-ignored field would read as a section on the wrong plane.
    /// History: docs/decisions/sections.md#contract</remarks>
    private IModelObject RequireSectionPlane(
        DocumentEntry document, PlaneRefDto plane, List<string> notes)
    {
        var hasReference = plane.Reference is { Length: > 0 };
        if (hasReference && plane.Base is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Заданы одновременно base и reference: опора сечения одна. Сервер не выбирает поле за " +
                "вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["base"] = plane.Base?.ToString(),
                    ["reference"] = plane.Reference,
                });
        }

        if (!hasReference && plane.Base is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опора сечения не задана: нужен либо base, либо reference.",
                RetryPolicy.Never);
        }

        if (hasReference)
        {
            if (Math.Abs(plane.OffsetMm) > 1e-9)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "offset_mm задан вместе с reference: смещение относится к базовой плоскости, а не к " +
                    "готовой ссылке. Сервер не игнорирует поле молча.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["reference"] = plane.Reference });
            }

            var stored = References.Require(plane.Reference!, document.Id, document.Revision);
            if (stored.Payload is not IModelObject model)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка «{plane.Reference}» не адресует плоскость: это {stored.Kind}.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?> { ["supported_kinds"] = new[] { "plane" } });
            }

            notes.Add("Опора сечения: плоскость по ссылке «" + plane.Reference + "»");
            return model;
        }

        var type = plane.Base switch
        {
            PlaneBase.Xz => ksObj3dTypeEnum.o3d_planeXOZ,
            PlaneBase.Yz => ksObj3dTypeEnum.o3d_planeYOZ,
            _ => ksObj3dTypeEnum.o3d_planeXOY,
        };

        // DOC: ksPart.GetDefaultEntity by o3d_planeXOY/XOZ/YOZ — MEASURED that IPlanes3D holds no
        // standard planes, so the support is taken by TYPE and never by searching the collection.
        var entity = Ref(() => document.PartNow().GetDefaultEntity((short)type));
        if (entity is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Стандартная плоскость «{plane.Base}» не получена: ksPart.GetDefaultEntity вернул пусто " +
                "для своего же типа — другая опора вместо неё не подставляется.",
                RetryPolicy.ReacquireContext);
        }

        var application = RequireApplication(document.ApplicationId).Application;
        if (Ref(() => application.TransferInterface(entity, 2, 0)) is not IModelObject transferred)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Стандартная плоскость «{plane.Base}» получена, но в API7 не переносится: опора не найдена.",
                RetryPolicy.ReacquireContext);
        }

        notes.Add("Опора сечения: стандартная плоскость «" + plane.Base + "» взята у ksPart.GetDefaultEntity " +
                  "по типу и перенесена в API7");
        return transferred;
    }

    /// <summary>The documented QI of the order: <c>IDynamicCrossSectionsManager</c> is an additional
    /// interface of <c>IKompasDocument3D</c> and is reached by a cast, nothing else. The same route serves
    /// a part and an assembly — measured on both.</summary>
    private (IKompasDocument3D Document7, IDynamicCrossSectionsManager Manager) RequireSections(
        DocumentEntry document, List<string> notes)
    {
        var application = RequireApplication(document.ApplicationId).Application;
        var document7 = Ref(() => application.TransferInterface(document.Document3D, 2, 0))
            as IKompasDocument3D;
        if (document7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ не перенесён в API7 как IKompasDocument3D: динамическое сечение недоступно.",
                RetryPolicy.Never);
        }

        if (document7 as IDynamicCrossSectionsManager is not { } manager)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Документ не отвечает QI(IDynamicCrossSectionsManager): динамическое сечение этим " +
                "выпуском КОМПАС не поддерживается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["interface"] = "IDynamicCrossSectionsManager",
                    ["document_kind"] = document.Kind.ToString(),
                });
        }

        notes.Add("sections_interface_acquired — QI(IKompasDocument3D → IDynamicCrossSectionsManager) " +
                  "отвечает");
        return (document7, manager);
    }

    private List<SectionRowDto> ReadSections(IDynamicCrossSectionsManager manager, List<string> notes)
    {
        var rows = new List<SectionRowDto>();
        var count = Int(() => manager.DynamicCrossSectionsCount);
        if (count is null)
        {
            notes.Add("sections_count_unread — DynamicCrossSectionsCount не прочитан: сечения не перечислены");
            return rows;
        }

        for (var index = 0; index < count; index++)
        {
            var position = index;
            var section = Ref(() => manager.DynamicCrossSection[position]);
            if (section is null)
            {
                notes.Add($"section_unread — сечение {position} не прочитано");
                continue;
            }

            rows.Add(new SectionRowDto(
                position,
                Int(() => section.StepsCount) ?? 0,
                Bool(() => section.FillCutPlanes),
                ReadSteps(section, position, notes)));
        }

        return rows;
    }

    private List<SectionStepRowDto> ReadSteps(
        IDynamicCrossSection section, int sectionIndex, List<string> notes)
    {
        var rows = new List<SectionStepRowDto>();
        var count = Int(() => section.StepsCount) ?? 0;
        for (var index = 0; index < count; index++)
        {
            var position = index;
            var step = Ref(() => section.Step[position]);
            if (step is null)
            {
                notes.Add($"step_unread — шаг {position} сечения {sectionIndex} не прочитан");
                continue;
            }

            var kind = Text(() => step.BuildingType.ToString());
            var parameters = Ref(() => step.Parameters);
            var byOffset = parameters as IDynamicCrossSectionStepParametersByOffsetPlane;
            var plane = byOffset is null ? null : Ref(() => byOffset.OffsetPlane) as IPlane3DByOffset;
            rows.Add(new SectionStepRowDto(
                position,
                kind,
                byOffset is null ? null : Text(() => byOffset.PlaneBuildingType.ToString()),
                plane is null ? null : Text(() => plane.BasePlane?.Name),
                plane is null ? null : Double(() => plane.Offset),
                plane is null ? null : Bool(() => plane.Direction)));
        }

        return rows;
    }
}
