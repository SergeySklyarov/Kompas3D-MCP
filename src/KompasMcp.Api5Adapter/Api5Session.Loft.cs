using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Loft — a body from an ordered set of sections (docs/05 SM-05, queue B5).</summary>
/// <remarks>
/// ROUTE — API7: the mandatory row <c>SM-05.base.mode_couplings</c> requires section correspondence
/// CHAINS, and API5 has none — neither <c>ksBaseLoftDefinition</c> nor <c>ksBossLoftDefinition</c>
/// declares <c>AddCoupling</c> or <c>Coupling</c>. API7 documents them: <c>iloft_propers.html</c> lists
/// <c>Coupling</c> and <c>CouplingsCount</c>, <c>iloft_addcoupling.html</c> describes <c>AddCoupling()</c>
/// → <c>ICoupling</c> (MEASURED: returns <c>KompasAPI7.CouplingClass</c>, <c>CouplingsCount = 1</c>).
/// DOC: <c>ilofts_add.html</c> — «Допустимыми значениями <c>LoftType</c> являются <c>o3d_bossLoft</c>,
/// <c>o3d_cutLoft</c> для коллекции операций <c>IModelContainer::Lofts</c>»; «после получения нового
/// интерфейса нужно задать параметры операции и вызвать метод <c>IModelObject::Update</c>». Sections
/// are set by the <c>ILoft.Sketchs</c> property of type <c>VARIANT</c> — «массив <c>SAFEARRAY</c>
/// объектов <c>LPDISPATCH</c>» (<c>iloft_sketchs.html</c>).
/// LIMIT: section ORDER is not proved by volume — concentric parallel sections give one body in any
/// order, so "the order was honoured" needs a discriminating setup; parallelism of section planes is
/// the checker's duty (work order §6.3 item 7).
/// History: docs/decisions/adapter-core.md#loft-compaction
/// </remarks>
public partial class Api5Session
{
    /// <summary>Glued loft — <c>o3d_bossLoft</c>.</summary>
    private const int BossLoft = 31;

    /// <summary>Maximum number of correspondence chains in one call.</summary>
    private const int MaxLoftCouplings = 64;

    /// <summary>Comparison tolerance for the point offset along the contour, mm (0.01 mm length class);
    /// MEASURED round-trip agreement at step B5.17 (wrote 5 mm, read 5 mm).</summary>
    private const double CouplingOffsetToleranceMm = 0.01d;

    /// <summary>Loft: a body from an ordered set of sections (SM-05).</summary>
    public LoftResult Loft(LoftCommand command)
    {
        ValidateLoftCommand(command);

        var document = RequireDocument(command.DocumentId);

        // INVARIANT: sections must belong to the SAME part and the same revision — a reference from a
        // foreign document would give either a kernel refusal or silently foreign geometry.
        var sections = new List<ksEntity>(command.SectionRefs.Count);
        var targets = new List<SketchTarget>(command.SectionRefs.Count);
        foreach (var sectionRef in command.SectionRefs)
        {
            var target = RequireSketch(sectionRef);
            if (!string.Equals(target.Document.Id, document.Id, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Сечение '{sectionRef}' принадлежит документу {target.Document.Id}, а элемент " +
                    $"по сечениям строится в {document.Id}. Сечения из чужой детали не переносятся.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["section_document_id"] = target.Document.Id,
                        ["loft_document_id"] = document.Id,
                    });
            }

            targets.Add(target);
            sections.Add(target.Sketch);
        }

        // ── Parallelism of section planes is the CALLER's duty (work order B5 §9.2): the refusal fires
        // ONLY on a MEASURED divergence of normal axes; an unreadable plane is named unread, not refused.
        var planeAxes = ReadSectionPlaneAxes(targets);
        if (planeAxes.DistinctAxes.Count > 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскости сечений не параллельны: оси нормалей " +
                string.Join(", ", planeAxes.DistinctAxes.Select(AxisName)) +
                ". Элемент по сечениям соединяет сечения, лежащие в параллельных плоскостях; " +
                "сечения на пересекающихся плоскостях описывали бы другое тело. Признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "non_parallel_section_planes",
                    ["axes"] = planeAxes.DistinctAxes.Select(AxisName).ToArray(),
                    ["sections_read"] = planeAxes.Read.Count,
                    ["sections_unreadable"] = planeAxes.Unreadable,
                });
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Цепочки соответствия сечений живут только в API7 (в API5 их нет вовсе), поэтому " +
                "семейство ведётся этим маршрутом; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var facesBefore = CountFaces(document);

        ILoft? loft = null;
        string? creationFailure = null;
        try
        {
            if (container.Lofts is not ILofts collection)
            {
                creationFailure = "IModelContainer.Lofts не привёлся к ILofts";
            }
            else
            {
                loft = collection.Add((ksObj3dTypeEnum)BossLoft);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            creationFailure = ex.GetType().Name + ": " + ex.Message;
        }

        if (loft is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ILofts.Add(o3d_bossLoft) не дал ILoft: " + (creationFailure ?? "причина не сообщена") +
                ". Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["api7_failure"] = creationFailure });
        }

        // Sketchs takes a SAFEARRAY of IDispatch pointers; an untransferred object is a value API7
        // will not see.
        var transferred = new List<object>(sections.Count);
        foreach (var section in sections)
        {
            if (bridge.TransferTo7(section) is IModelObject modelObject)
            {
                transferred.Add(modelObject);
            }
        }

        if (transferred.Count != sections.Count)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "В API7 перенесено " + transferred.Count + " сечений из " + sections.Count +
                ": непереданное сечение API7 не увидит, и строить тело по неполному набору нельзя. " +
                "Признак не создавался.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["transferred"] = transferred.Count,
                    ["requested"] = sections.Count,
                });
        }

        try
        {
            loft.Sketchs = transferred.ToArray();
            loft.Closed = command.Closed;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись сечений в ILoft.Sketchs не состоялась: " + ex.GetType().Name + ": " +
                ex.Message + ". Признак не построен.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        // ── Section correspondence chains are set BEFORE the first Update() ──
        var chainFailures = new List<string>();
        var chainsWritten = AttachCouplings(loft, command.Couplings, chainFailures);
        if (chainFailures.Count > 0)
        {
            // The requested correspondence is part of the request, not decoration: a built body with
            // a different correspondence would be a different body. The refusal therefore comes
            // BEFORE the build, not after.
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Цепочки соответствия сечений не заданы: " + string.Join("; ", chainFailures) +
                ". Тело по неполному соответствию было бы ДРУГИМ телом, поэтому построение не " +
                "выполнялось; запрошено цепочек " + command.Couplings.Count + ", задано " +
                chainsWritten + ".",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["chains_requested"] = command.Couplings.Count,
                    ["chains_written"] = chainsWritten,
                    ["chain_failures"] = chainFailures,
                });
        }

        var updated = SafeBool(loft.Update);
        if (updated != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ILoft.Update() вернул " + (updated is null ? "ошибку вызова" : "false") +
                ": тело по сечениям не построено. Одно сечение, разомкнутое сечение там, где " +
                "требуется замкнутое, или несовместимые контуры дают именно этот исход.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        Api7Bridge.Rebuild(container, document.Document);

        var reference = References.Register("feature", document.Id, document.Revision, loft);
        BumpRevision(document, "loft.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        // Chains are read FROM THE MODEL and in full; a bare chain count would prove only existence.
        var couplingsInModel = ReadCouplingContent(loft);

        var checks = new List<NamedCheck>
        {
            new("operation_created", updated == true, "ILoft.Update() вернул true"),
            new("sections_transferred", transferred.Count == sections.Count,
                "перенесено сечений: " + transferred.Count + " из " + sections.Count),
            new("body_count_grew", bodiesAfter > bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
        };

        if (command.Couplings.Count > 0)
        {
            var chainsInModel = couplingsInModel?.Count;
            checks.Add(new NamedCheck("coupling_chains_created", chainsInModel == command.Couplings.Count,
                Observed: "цепочек в модели: " + (chainsInModel?.ToString(CultureInfo.InvariantCulture)
                                                  ?? "не прочитано"),
                Expected: "запрошено " + command.Couplings.Count.ToString(CultureInfo.InvariantCulture)));

            var offsets = CouplingOffsetsMatch(couplingsInModel, command.Couplings);
            checks.Add(new NamedCheck("coupling_points_read_back", offsets.Ok,
                Observed: offsets.Observed,
                Expected: offsets.Expected));
        }

        // Sections are read BACK: how many the feature accepted.
        var sectionsInModel = ReadSectionCount(loft);
        checks.Add(new NamedCheck("sections_read_back", sectionsInModel == sections.Count,
            Observed: sectionsInModel?.ToString() ?? "не прочитано",
            Expected: sections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var unverified = new List<string>
        {
            "section_order_not_distinguishable_by_volume — концентрические параллельные сечения дают " +
            "28000 в любом порядке (измерено), поэтому «порядок соблюдён» требует различающей " +
            "постановки (разная форма или поворот сечений, габарит, число граней) и здесь не " +
            "подтверждён объёмом",
            "section_planes_parallelism_proved_only_for_readable_planes — параллельность плоскостей " +
            "сечений проверяется по оси нормали, прочитанной с определения эскиза, и расхождение " +
            "даёт именованный отказ non_parallel_section_planes ДО создания признака. Число " +
            "нечитаемых плоскостей этого вызова: " + planeAxes.Unreadable +
            "; для них параллельность НЕ доказана и отказа не даёт — нечитаемая плоскость " +
            "называется непрочитанной, а не «наверное, параллельной»",
            "coupling_point_frame_not_published — наружу выходит смещение вдоль контура " +
            "(ICoupling.PositionOffset), а не координаты точки: измерено (шаг B5.17), что " +
            "ICoupling.SetPoint проецирует поданную точку на контур и читается обратно в ЛОКАЛЬНЫХ " +
            "координатах эскиза сечения — подано (10; 10; 30) (центр квадрата 20×20), прочитано " +
            "(20; 10; 30). Публиковать параметр с несовпадающими прямой и обратной половинами " +
            "значило бы обещать round-trip, которого нет",
            "coupling_effect_not_explained_analytically — то, что содержимое цепочки применяется, " +
            "измерено числом (смещение точки второго сечения на 25 % контура: 28000 → 20000, " +
            "разность 8000 мм³), но аналитического ожидания для тела со сдвинутым соответствием " +
            "наряд не даёт, поэтому эталоном служит совпадение с измеренным 20000, а не формула",
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };

        if (command.Couplings.Count == 0)
        {
            // Silence is a claim too: a call without chains does not confirm "correspondence
            // defined", and that is named rather than left blank.
            unverified.Add("couplings_not_requested — цепочки соответствия в этом вызове не задавались, " +
                "поэтому этим вызовом подтверждается существование признака и его геометрия, но НЕ " +
                "определённое соответствие сечений");
        }

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var observed = volumeAfter;
            var matches = observed is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(observed) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия элемента по сечениям не подтверждена числом");
        }

        return new LoftResult(
            ToDto(reference, loft.Name),
            command.Building.ToString(),
            command.Closed,
            sections.Count,
            couplingsInModel?.Count,
            couplingsInModel,
            bodiesAfter,
            volumeAfter,
            document.PartNow().GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>
            {
                "маршрут API7: ILofts.Add(o3d_bossLoft) → ILoft.Sketchs (SAFEARRAY) → цепочки " +
                "соответствия → Update(); лишних граней: " + facesAfter,
                "цепочки соответствия: запрошено " + command.Couplings.Count + ", задано " +
                chainsWritten + ", прочитано из модели " +
                (couplingsInModel?.Count.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") +
                " (смещения в мм вдоль контуров сечений)",
            });
    }

    /// <summary>Result of reading the normal axes of the section planes.</summary>
    private sealed record SectionPlaneAxes(
        IReadOnlyList<int> Read, IReadOnlyList<int> DistinctAxes, int Unreadable);

    /// <summary>Normal axes of the section planes, read from the SKETCH definitions. An axis answers
    /// "is the plane parallel to XOY / XOZ / YOZ", not "where the normal points".</summary>
    private static SectionPlaneAxes ReadSectionPlaneAxes(IReadOnlyList<SketchTarget> targets)
    {
        var read = new List<int>(targets.Count);
        var unreadable = 0;
        foreach (var target in targets)
        {
            int? axis;
            try
            {
                axis = target.Definition.GetPlane() is ksEntity plane ? PlaneNormalAxis(plane) : null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                axis = null;
            }

            if (axis is int value)
            {
                read.Add(value);
            }
            else
            {
                unreadable++;
            }
        }

        return new SectionPlaneAxes(read, read.Distinct().ToList(), unreadable);
    }

    /// <summary>Plane name by its normal axis — for a refusal message, not for output.</summary>
    private static string AxisName(int axis) => axis switch
    {
        0 => "YOZ (нормаль X)",
        1 => "XOZ (нормаль Y)",
        2 => "XOY (нормаль Z)",
        _ => "ось " + axis.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Rules for correspondence chains, shared by create and edit: a chain's point count
    /// equals the section count, values are finite, chains do not exceed
    /// <see cref="MaxLoftCouplings"/>. Refused BEFORE COM.</summary>
    private static void ValidateLoftCouplings(IReadOnlyList<LoftCoupling> chains, int sectionCount)
    {
        if (chains.Count > MaxLoftCouplings)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Цепочек соответствия " + chains.Count + ", а принимается не более " +
                MaxLoftCouplings + ".",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["couplings"] = chains.Count });
        }

        for (var i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            if (chain.OffsetsMm.Count != sectionCount)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "В цепочке соответствия " + i + " смещений " + chain.OffsetsMm.Count + ", а сечений " +
                    sectionCount + ": цепочка задаёт по точке на КАЖДОЕ сечение, и неполная цепочка "
                    + "описывала бы другое соответствие.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["chain"] = i,
                        ["offsets"] = chain.OffsetsMm.Count,
                        ["sections"] = sectionCount,
                    });
            }

            for (var j = 0; j < chain.OffsetsMm.Count; j++)
            {
                var offset = chain.OffsetsMm[j];
                if (double.IsNaN(offset) || double.IsInfinity(offset))
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Смещение " + j + " в цепочке " + i + " равно '" + offset +
                        "': нечисловое или бесконечное значение не описывает положение на контуре.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?> { ["chain"] = i, ["point"] = j });
                }
            }
        }
    }

    /// <summary>Full replacement of chains on an existing feature: <c>ClearCouplings()</c>, then one
    /// chain per request. Returns <c>false</c> if the replacement was not full — "some of the chains"
    /// is a different correspondence, not half a success.</summary>
    private static bool WriteLoftCouplings(
        ILoft loft, IReadOnlyList<LoftCoupling> chains, out string failure)
    {
        failure = string.Empty;

        var existing = SafeInt(() => loft.CouplingsCount);
        if (existing is > 0 && SafeBool(loft.ClearCouplings) != true)
        {
            failure = "ClearCouplings() не убрал прежние цепочки (" + existing + ")";
            return false;
        }

        var failures = new List<string>();
        var written = AttachCouplings(loft, chains, failures);
        if (failures.Count > 0)
        {
            failure = string.Join("; ", failures);
            return false;
        }

        if (written != chains.Count)
        {
            failure = "задано цепочек " + written + " из " + chains.Count;
            return false;
        }

        return true;
    }

    /// <summary>Set the section correspondence chains: <c>ILoft.AddCoupling()</c> → <c>ICoupling</c>,
    /// then <c>ICoupling.PositionOffset(Index)</c> for each section in section order.</summary>
    /// <remarks>Returns the number of FULLY set chains; failure reasons accumulate in
    /// <paramref name="failures"/>. A partially set chain is a different correspondence.</remarks>
    private static int AttachCouplings(
        ILoft loft, IReadOnlyList<LoftCoupling> chains, List<string> failures)
    {
        var written = 0;
        for (var i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            ICoupling? coupling;
            try
            {
                coupling = loft.AddCoupling();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                failures.Add("цепочка " + i + ": AddCoupling отказал — " + ex.GetType().Name + ": " +
                             ex.Message);
                continue;
            }

            if (coupling is null)
            {
                failures.Add("цепочка " + i + ": AddCoupling не вернул ICoupling");
                continue;
            }

            var placed = 0;
            for (var section = 0; section < chain.OffsetsMm.Count; section++)
            {
                var index = section;
                var offset = chain.OffsetsMm[section];
                if (SafeBool(() =>
                    {
                        coupling.PositionOffset[index] = offset;
                        return true;
                    }) != true)
                {
                    failures.Add("цепочка " + i + ", сечение " + index + ": PositionOffset = " +
                                 Num(offset) + " мм не принят");
                    continue;
                }

                placed++;
            }

            if (placed == chain.OffsetsMm.Count)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>Correspondence chains read <b>FROM THE MODEL</b>: <c>CouplingsCount</c>, then for each
    /// <c>Coupling(Index)</c> → <c>ICoupling</c> → <c>Count</c> and <c>PositionOffset(Index)</c>.
    /// <c>null</c> is "not read" and differs from an empty list.</summary>
    private static IReadOnlyList<LoftCouplingDto>? ReadCouplingContent(ILoft loft)
    {
        int? total;
        try
        {
            total = loft.CouplingsCount;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        if (total is not { } chains || chains < 0)
        {
            return null;
        }

        var result = new List<LoftCouplingDto>(chains);
        for (var i = 0; i < chains; i++)
        {
            var chainIndex = i;
            ICoupling? coupling;
            try
            {
                coupling = loft.Coupling[chainIndex];
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                result.Add(new LoftCouplingDto(null, Array.Empty<double?>()));
                continue;
            }

            int? inChain;
            try
            {
                inChain = coupling.Count;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                inChain = null;
            }

            var offsets = new List<double?>();
            for (var j = 0; j < (inChain ?? 0); j++)
            {
                var section = j;
                offsets.Add(SafeDouble(() => coupling.PositionOffset[section]));
            }

            result.Add(new LoftCouplingDto(inChain, offsets));
        }

        return result;
    }

    /// <summary>Comparison "requested ↔ read FROM THE MODEL" over all chains and points; the tolerance
    /// is the length one (0.01 mm), chain and section counts are compared exactly.</summary>
    private static (bool Ok, string Observed, string Expected) CouplingOffsetsMatch(
        IReadOnlyList<LoftCouplingDto>? model, IReadOnlyList<LoftCoupling> requested)
    {
        var expected = DescribeRequestedCouplings(requested);
        if (model is null)
        {
            return (false, "не прочитано", expected);
        }

        var ok = model.Count == requested.Count;
        var observed = new List<string>();
        for (var i = 0; i < model.Count; i++)
        {
            var chain = model[i];
            if (i >= requested.Count)
            {
                observed.Add("цепочка " + i + ": лишняя");
                continue;
            }

            var want = requested[i].OffsetsMm;
            if (chain.SectionCount != want.Count)
            {
                ok = false;
            }

            var parts = new List<string>();
            for (var j = 0; j < want.Count; j++)
            {
                var got = j < chain.OffsetsMm.Count ? chain.OffsetsMm[j] : null;
                parts.Add(Num(got));
                if (got is not { } value || Math.Abs(value - want[j]) > CouplingOffsetToleranceMm)
                {
                    ok = false;
                }
            }

            observed.Add("цепочка " + i + " (сечений в модели " +
                         (chain.SectionCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") +
                         "): " + string.Join(" / ", parts) + " мм");
        }

        return (ok, string.Join("; ", observed), expected);
    }

    /// <summary>Requested chains on one line — the other half of the comparison.</summary>
    private static string DescribeRequestedCouplings(IReadOnlyList<LoftCoupling> requested) =>
        string.Join("; ", requested.Select((chain, index) =>
            "цепочка " + index + " (сечений " + chain.OffsetsMm.Count.ToString(CultureInfo.InvariantCulture) +
            "): " + string.Join(" / ", chain.OffsetsMm.Select(offset => Num(offset))) + " мм"));

    /// <summary>Number of sections accepted by the feature — a read <b>FROM THE MODEL</b>. The array
    /// arrives as a <c>SAFEARRAY</c> of objects; <c>null</c> means "not read", not zero.</summary>
    private static int? ReadSectionCount(ILoft loft)
    {
        try
        {
            return loft.Sketchs is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Read an integer without failing: <c>null</c> is "not read", not zero.</summary>
    private static int? SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>"Field ↔ capability" rules that reject the call BEFORE COM.</summary>
    private static void ValidateLoftCommand(LoftCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: у набора сечений нет одного «опорного» объекта, как у профиля.",
                RetryPolicy.Never);
        }

        if (command.SectionRefs.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сечений " + command.SectionRefs.Count + ", а по одному сечению тело не строится: " +
                "элемент по сечениям соединяет не менее двух.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["sections"] = command.SectionRefs.Count });
        }

        if (command.SectionRefs.Any(string.IsNullOrWhiteSpace))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Среди сечений есть пустая ссылка: порядок массива — это порядок соединения, и " +
                "пустое место в нём меняет тело.",
                RetryPolicy.Never);
        }

        if (command.SectionRefs.Count != command.SectionRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Одно и то же сечение указано дважды: тело по двум одинаковым сечениям вырождается, " +
                "и «сколько сечений задано» стало бы неотличимо от «сколько раз его назвали».",
                RetryPolicy.Never);
        }

        // The number of points in a chain must match the number of sections: PositionOffset(Index) is
        // addressed by "the section index in the chain" (icoupling_positionoffset.html).
        ValidateLoftCouplings(command.Couplings, command.SectionRefs.Count);

        if (command.Building != LoftBuilding.Auto)
        {
            // ONLY the auto mode's value was MEASURED (both ends read 0 = ksLoftAuto on a fresh
            // feature); values 1/2/3 were NOT measured on this route, so they are not accepted silently.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Способ построения у крайних сечений '" + command.Building + "' на этом маршруте не " +
                "измерялся: измерен только Auto (ILoft.BuildingType(BeginSection) = 0 = ksLoftAuto, " +
                "шаг B5.9). Режимы «по нормали», «по объекту» и «купол» требуют своего измерения " +
                "прежде, чем приниматься.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["building"] = command.Building.ToString() });
        }
    }
}
