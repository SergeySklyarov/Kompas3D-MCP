using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Fillet radius edit (docs/05 SM-09, mode <c>edit</c>).</summary>
/// <remarks>
/// INVARIANT: the radius is written to <c>IFillet.Radius1</c> on the live model, as the chamfer angle is
/// (see <see cref="Api5Session.UpdateChamferByAngle"/>). MEASURED: writing through
/// <c>ksFilletDefinition.radius</c> is accepted by the setter and read back by the getter while the volume
/// does not change — a refusal, not a success.
/// History: docs/decisions/adapter-features.md#fillet-radius-api7
/// INVARIANT: the API5 feature is matched to <c>IModelContainer.Fillets</c> by its OWN INPUTS, with the
/// radius kept only as a fallback. The name differs between API5 and API7 (F.8: «f-ch2» → «Фаска:1»), so
/// it is not a key. An ambiguous match (none or several) is a <c>CAPABILITY_UNAVAILABLE</c> refusal BEFORE
/// mutation: writing a radius into a foreign fillet means silently corrupting foreign geometry.
/// </remarks>
public partial class Api5Session
{
    /// <summary>Fillet family name in server responses.</summary>
    private const string FilletFamily = "fillet";

    /// <summary>Reference kind of a feature's OWN input. Kept here rather than taken from the registry directly,
    /// so the string form is declared next to whoever parses it: the divergence "mint one thing, read
    /// another" is otherwise caught by neither the compiler nor acceptance.</summary>
    private const string InputReferenceKind = ReferenceRegistry.InputKind;

    /// <summary>What the server sees for a fillet: radius and mode are read from API7 when a bridge to the same
    /// document is built and the match is unambiguous. An empty field means "not read", not "zero".</summary>
    private FilletDto? ReadFillet(DocumentEntry document, object definition)
    {
        if (definition is not ksFilletDefinition fillet)
        {
            return null;
        }

        // The API5 definition radius is always read — it is the fallback match key.
        var api5Radius = fillet.radius;
        var api7 = ReadFilletRadius(document, api5Radius, fillet);
        return new FilletDto(
            RadiusMm: api7?.RadiusMm ?? api5Radius,
            Radius2Mm: api7?.Radius2Mm,
            Tangent: api7?.Tangent ?? fillet.tangent,
            BuildingType: api7?.BuildingType,
            BaseObjectCount: api7?.BaseObjectCount,
            BaseObjectReferences: api7?.BaseObjectReferences,
            BaseObjectInputRefs: MintInputReferences(document, api7));
    }

    /// <summary>Mint <c>input:&lt;hex&gt;</c> references for a feature's own inputs, one per <c>BaseObjects</c>.</summary>
    /// <remarks>
    /// The SERVER mints the input string at read time: a feature's own input is not a body edge, has no
    /// <c>edge:</c> registry string, and <c>IModelObject.Reference</c> numbers are not substituted by type.
    /// INVARIANT: minting is bound to the document REVISION, not the feature state; <c>Require</c> cuts off
    /// previous references. Not issued when: no API7 bridge, several fillets share the radius, or no inputs.
    /// History: docs/decisions/adapter-features.md#mint-input-references
    /// </remarks>
    private IReadOnlyList<string>? MintInputReferences(DocumentEntry document, FilletReadDto? api7)
    {
        if (api7?.BaseObjectReferences is not { } numbers)
        {
            return null;
        }

        var minted = new List<string>(numbers.Count);
        foreach (var number in numbers)
        {
            // The identifier is the input ADDRESS itself, not a fresh uuid: the point of the reference
            // here is to carry the IModelObject.Reference number into the edge-set edit, which a uuid
            // would not do.
            var id = $"{InputReferenceKind}:{number:x}";
            References.RegisterDeterministic(
                id, InputReferenceKind, document.Id, document.Revision, payload: number);
            minted.Add(id);
        }

        return minted;
    }

    /// <summary>Radius and mode from API7 — by the feature's OWN INPUTS, with the radius kept as a fallback.</summary>
    /// <remarks>
    /// INVARIANT: identify by inputs, not by radius. The radius is not a key: two fillets of one radius may
    /// legitimately live on a model, and identification by radius returned <c>null</c> on exactly such a model.
    /// The key source is the feature's OWN INPUTS (<c>FindIndexByInputReferences</c>); the radius is a FALLBACK,
    /// used only when the composition could not be read.
    /// History: docs/decisions/adapter-features.md#fillet-identify-by-inputs
    /// </remarks>
    private FilletReadDto? ReadFilletRadius(DocumentEntry document, double api5Radius, object? definition = null)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            return null;
        }

        // Primary route: the feature is identified by the composition of its OWN inputs. It tells two
        // fillets of one radius apart, which the radius cannot by construction.
        if (definition is ksFilletDefinition source
            && source.array() is ksEntityCollection array)
        {
            var ownRefs = new List<int>();
            for (var i = 0; i < array.GetCount(); i++)
            {
                if (array.GetByIndex(i) is ksEntity edge
                    && ReferenceOfApi7(bridge, edge) is int reference)
                {
                    ownRefs.Add(reference);
                }
            }

            if (ownRefs.Count > 0 && ownRefs.Count == array.GetCount())
            {
                var byInputs = Api7Fillet.FindIndexByInputReferences(container, ownRefs);
                if (byInputs is int index)
                {
                    return Api7Fillet.Read(container, index);
                }
            }
        }

        // Fallback — only when the composition could not be read. Radius ambiguity means "nothing to
        // identify by", and null is returned rather than "the first one found".
        var matches = Api7Fillet.FindIndexesByIdenticalRadius(container, api5Radius);
        return matches.Count == 1 ? Api7Fillet.Read(container, matches[0]) : null;
    }

    /// <summary>
    /// Fillet radius edit. The geometry is confirmed by a volume measurement: the client must supply
    /// <c>expected_volume_mm3</c> (for the four corner edges of a 100×80×10 plate it is
    /// 80000 − 4·(1−π/4)·r²·10), otherwise the level stays <c>call_returned</c>.
    /// </summary>
    private UpdateFeatureResult UpdateFilletRadius(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        var requested = command.RadiusMm;
        if (requested is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для скругления нужен radius_mm: других меняемых числовых параметров у него в v1 нет.",
                RetryPolicy.Never);
        }

        if (requested is <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Радиус скругления обязан быть больше 0: ноль КОМПАС принимает и создаёт признак с " +
                "нулевыми гранями при неизменном объёме (измерено на фаске пробой F.12).",
                RetryPolicy.Never);
        }

        // The current radius comes from the API5 definition. It is needed as a MATCH KEY, not as a
        // source of truth: we will not write into it (see the class remark).
        var currentSource = entity.GetDefinition() as ksFilletDefinition;
        var currentRadius = currentSource?.radius;
        if (currentRadius is not double radiusNow)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение скругления не перечитывает радиус: сопоставить признак API5 с IFillet " +
                "нечем, признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Радиус скругления правится только через IFillet, потому что запись в " +
                "ksFilletDefinition.radius на существующем признаке не применяется " +
                "(измерено строкой FL04r). Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var matches = Api7Fillet.FindIndexesByIdenticalRadius(container, radiusNow);
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                matches.Count == 0
                    ? "Скругление с радиусом " + radiusNow.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм не найдено в IModelContainer.Fillets: сопоставить признак API5 с IFillet " +
                      "нечем, а записать радиус в чужой признак нельзя. Признак не изменён."
                    : "Скруглению с радиусом " + radiusNow.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм отвечает " + matches.Count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " признаков API7 — сопоставление неоднозначно, и записывать радиус «в первый " +
                      "попавшийся» означало бы изменить не тот признак. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["radius_mm"] = radiusNow,
                    ["api7_matches"] = matches.Count,
                });
        }

        var index = matches[0];
        var before = Api7Fillet.Read(container, index);

        var written = Api7Fillet.TryWriteRadius(container, index, requested, null, null);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись в IFillet не подтверждена: " + (written.Failure ?? "причина не сообщена") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        // Without a rebuild the IFillet write stays a representation: the same order as on fillet
        // creation and on chamfer-angle edit (F.10 + probe E).
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "fillet.update.radius");

        var after = Api7Fillet.Read(container, index);
        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // The radius is re-read from the API5 definition too: if API7 accepted the write while the
        // definition returns the old number, the API5 feature and the live model have diverged, and that
        // must be named.
        var definitionAfter = entity.GetDefinition() as ksFilletDefinition;
        var api5Radius = definitionAfter?.radius;

        var api7RadiusStored = after?.RadiusMm is double readRadius
            && Math.Abs(readRadius - requested.Value) <= 1e-6;
        var api5SeesChange = api5Radius is double r5 && Math.Abs(r5 - requested.Value) <= 1e-6;
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{index}] Radius1={requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} (было {radiusNow.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)})",
                Expected: "запись принята и подтверждена перестроением"),
            new("radius_read_back", api7RadiusStored,
                Observed: $"IFillet.Radius1={after?.RadiusMm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
            new("api5_sees_the_change", api5SeesChange,
                Observed: api5Radius?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));
        checks.Add(DeclaredExpectation.Check("volume_after_update", declared));

        var volumeMatched = declared.IsConfirmed;

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (!api5SeesChange)
        {
            unverified.Add(
                "api5_definition_stale — IFillet радиус принял, а определение API5 отдаёт прежнее " +
                "число: чтение через kompas_get_feature на этом признаке покажет устаревший радиус");
        }

        var geometryConfirmed = api7RadiusStored && sameFeature && volumeMatched;
        if (declared.IsUnverifiable)
        {
            unverified.Insert(0, DeclaredExpectation.UnverifiableReason("объём после правки", declared));
        }
        else if (declared.IsNotConfirmed)
        {
            unverified.Insert(0, DeclaredExpectation.NotConfirmedReason("объём после правки", declared));
        }
        else if (!geometryConfirmed)
        {
            unverified.Insert(0, api7RadiusStored
                ? "volume_not_as_expected — радиус записан и перечитан, но измерение объёма не совпало с ожиданием"
                : "radius_not_read_back — радиус не перечитался из IFillet");
        }

        if (!declared.IsDeclared)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка не может быть " +
                "подтверждена геометрически");
        }

            return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // A fillet has no depth or end condition: those fields belong to extrusion.
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            // The radius re-read from the model. As a separate trailing parameter — like the chamfer
            // angle.
            RadiusReadBackMm: after?.RadiusMm,
            Warnings: DeclaredExpectation.Warnings(declared));
    }

    /// <summary>
    /// Edge-set edit: which edges stay filleted. The set is replaced as a whole by one assignment to
    /// <c>IFillet.BaseObjects</c> — this is the route protocol, not a convenience. The PRESENTATION
    /// CURRENCY is chosen from what the client sent.
    /// </summary>
    /// <remarks>
    /// INVARIANT: the route is API7 and is MEASURED (<c>docs/acceptance/api7/fillet-base-objects.md</c>):
    /// <c>IModelContainer.Fillets[i]</c> → <c>IFillet</c>; <c>IFillet.BaseObjects</c> reads as
    /// <c>System.Object[]</c> of <c>IModelObject</c> and is written by full replacement;
    /// <c>IFillet.Update()</c> is mandatory. All objects are taken from the LIVE model, none captured at
    /// creation is reused.
    /// History: docs/decisions/adapter-features.md#fillet-edge-set-route
    /// LIMIT: the API5 route <c>ksFilletDefinition.array()</c> (<c>Clear()</c> then <c>Add()</c>) does NOT
    /// edit the set of an existing feature — after a fillet the corner edges are withdrawn or not held, and
    /// any set of them collapses the definition. The old route was removed, not kept as a branch.
    /// INVARIANT: match the API5 feature to <c>IFillet</c> NOT by name, NOT by index and NOT by radius —
    /// the name differs (F.8: «f-ch2» → «Фаска:1»), the index is arbitrary, and the radius cannot tell two
    /// fillets of one radius apart. Identify by the feature's OWN CURRENT inputs: <c>ksFilletDefinition.array()</c>
    /// yields API5 edges, transferred to API7 and compared by <c>IModelObject.Reference</c> against each
    /// <c>IFillet</c>'s inputs; at zero or several matches the call is rejected BEFORE mutation.
    /// INVARIANT: identification and PRESENTATION are different questions with different keys — the
    /// presentation uses objects obtained by the <c>ksAPI7Dual</c> transfer (control H2.4).
    /// History: docs/decisions/adapter-features.md#fillet-edge-set-ban-lifted
    /// LIMIT: extension is not measured on the 100×80×10 reference (exactly four vertical corners);
    /// reduction (4→3, 4→2) and replacement at unchanged size (1→1) are. Reduction is expressed only by the
    /// feature's own inputs, replacement by body edges; mixing them in one call would be indistinguishable
    /// from "one of the two applied".
    /// STATE: the route is IMPLEMENTED; acceptance confirmation is by <c>FL10…FL10x</c>. Do not treat the
    /// method as confirmed while <c>FL10</c> is red (verdict in <c>docs/acceptance/INDEX.md</c>).
    /// </remarks>
    private UpdateFeatureResult UpdateFilletEdgeSet(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.BaseObjectRefs is not null && command.EdgeRefs is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs и base_object_refs вместе запрещены: это два разных состава одного набора. " +
                "В ответе «применилось одно из двух» было бы неотличимо от «применилось и то, и другое».",
                RetryPolicy.Never);
        }

        // An EMPTY set DIFFERS from "not supplied" and must refuse with its own answer: otherwise
        // base_object_refs=[] would fall through to the edge_refs branch and the client would read
        // "neither edge_refs nor base_object_refs supplied", although it supplied them. Refusal before
        // mutation, as the contract requires.
        if (command.BaseObjectRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "base_object_refs пуст: новый набор рёбер скругления пустым быть не может — это был " +
                "бы не набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        if (command.EdgeRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs пуст: новый набор рёбер скругления пустым быть не может — это был бы не " +
                "набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        if (command.BaseObjectRefs is { Count: > 0 } requestedInputs)
        {
            return UpdateFilletEdgeSetByOwnInputs(
                document, entity, command, requestedInputs, volumeBefore, featuresBefore, stateBefore);
        }

        if (command.EdgeRefs is not { Count: > 0 } edgeRefs)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ни edge_refs, ни base_object_refs не задан: новый набор рёбер скругления пустым " +
                "быть не может — это был бы не набор, а удаление признака, и оно делается другим " +
                "инструментом.",
                RetryPolicy.Never);
        }

        return UpdateFilletEdgeSetByBodyEdges(
            document, entity, command, edgeRefs, volumeBefore, featuresBefore, stateBefore);
    }

    /// <summary>Edge-set edit by the feature's OWN INPUTS: a subset of ITS OWN objects is written.</summary>
    /// <remarks>Measurably strict reduction path (probe H-2, experiments H2.3 4→3 and H2.5 4→2): the probe
    /// wrote into <c>IFillet.BaseObjects</c> an array assembled from elements it had itself read
    /// (<c>ElementAt(raw, i)</c>), did NOT call <c>Clear()</c> and did NOT seek body edges — it writes a
    /// subset of N objects read from BaseObjects. Unlike the <c>edge_refs</c> path (which presents BODY
    /// edges: good for replacing the composition, bad for reduction — FL10 collapses the feature), here the
    /// feature's own inputs are presented.</remarks>
    private UpdateFeatureResult UpdateFilletEdgeSetByOwnInputs(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        IReadOnlyList<string> requestedInputRefs,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.RadiusMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "radius_mm и base_object_refs вместе запрещены: правка радиуса и правка набора — " +
                "разные предметы и разные подтверждения.",
                RetryPolicy.Never);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Собственные входы признака правятся только через IFillet.BaseObjects. " +
                "Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var wanted = new List<int>(requestedInputRefs.Count);
        var unknown = new List<string>();
        foreach (var reference in requestedInputRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Kind != InputReferenceKind)
            {
                unknown.Add($"{reference} (kind={stored.Kind}, ожидался {InputReferenceKind})");
                continue;
            }

            if (stored.Payload is int number)
            {
                wanted.Add(number);
            }
            else
            {
                unknown.Add($"{reference} (payload не число)");
            }
        }

        if (unknown.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Не разобрано ссылок на собственные входы: " + string.Join(", ", unknown) +
                ". Возьмите base_object_input_refs из kompas_get_feature. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["unrecognised"] = unknown });
        }

        if (wanted.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "base_object_refs пуст: новый набор рёбер скругления пустым быть не может — это был " +
                "бы не набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        // The feature is found by matching ITS inputs against the requested ones, not by radius: two
        // fillets of one radius on a model are ordinary (MEASURED, experiment H2.7). The match must be
        // exactly one, otherwise the subset would go into a foreign fillet.
        var liveInputs = Api7Fillet.ReadBaseObjectsAll(container);
        var owner = -1;
        for (var i = 0; i < liveInputs.Count; i++)
        {
            // null means "this fillet's set was not read" — that is neither "no inputs" nor a write
            // candidate: skipping it is mandatory, otherwise "not read" would become a match and the set
            // would go into a feature we never read.
            if (liveInputs[i] is not { } candidateInputs)
            {
                continue;
            }

            var refs = Api7Fillet.ReferencesOf(candidateInputs);
            if (wanted.All(refs.Contains))
            {
                if (owner >= 0)
                {
                    owner = -2;
                    break;
                }

                owner = i;
            }
        }

        if (owner < 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                owner == -2
                    ? "Ссылки на собственные входы совпали более чем с одним скруглением: записать " +
                      "набор в чужой признак означало бы молча исправить чужую геометрию. " +
                      "Признак не изменён."
                    : "Ни одно скругление не удерживает указанные входы: состав изменился после " +
                      "чтения либо ссылки выпущены для другой ревизии. Перечитайте " +
                      "kompas_get_feature. Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // The objects written are those READ FROM THE FEATURE ITSELF, not reassembled: that is the whole
        // measured point of the path. An object obtained otherwise is not held by the feature (MEASURED
        // FL10). The owner was already checked as "read" during identification, so dereferencing is safe
        // here, but it is taken once — and ONE AND THE SAME list participates in both the check and the
        // write.
        var own = liveInputs[owner]!;
        var ownRefs = Api7Fillet.ReferencesOf(own);

        // MANDATORY CROSS-CHECK OF THE OWNER AGAINST THE REQUESTED FEATURE. Identification by input
        // composition answers "who holds these inputs", not "is this the feature that was requested":
        // passing fillet B's inputs with fillet A's feature_ref would edit B while reporting A. So the
        // owner must equal the requested feature, and the refusal fires BEFORE mutation. The requested
        // feature's own inputs (returned in API5 via the definition) are compared with the owner's:
        // fillet A and B have different inputs by definition — otherwise they could not be told apart
        // on read either. The transfer uses the same ksAPI7Dual as identification.
        var requestedOwnInputs = new List<int>();
        if (entity.GetDefinition() is ksFilletDefinition requestedSource
            && requestedSource.array() is ksEntityCollection requestedArray)
        {
            for (var i = 0; i < requestedArray.GetCount(); i++)
            {
                if (requestedArray.GetByIndex(i) is ksEntity requestedEdge
                    && ReferenceOfApi7(bridge, requestedEdge) is int requestedRef)
                {
                    requestedOwnInputs.Add(requestedRef);
                }
            }
        }

        // An empty list means "the definition returned no inputs" — a MEASURED property of API5 on an
        // EXISTING fillet, not proof of a foreign feature. So the check runs only when the inputs were
        // read: an unread value must not turn into a false refusal.
        if (requestedOwnInputs.Count > 0)
        {
            var requestedSet = requestedOwnInputs.OrderBy(x => x).ToArray();
            var ownerSet = ownRefs.OrderBy(x => x).ToArray();
            if (!requestedSet.SequenceEqual(ownerSet))
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Собственные входы найденного скругления не совпадают с входами признака, " +
                    "указанного в feature_ref: запись в найденный признак исправила бы ЧУЖУЮ " +
                    "геометрию, отчитавшись про запрошенную. Признак не изменён.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["feature_ref_inputs"] = requestedSet,
                        ["matched_owner_inputs"] = ownerSet,
                        ["matched_owner_index"] = owner,
                    });
            }
        }

        var kept = new List<IModelObject>(wanted.Count);
        for (var i = 0; i < own.Count; i++)
        {
            if (i < ownRefs.Count && wanted.Contains(ownRefs[i]))
            {
                kept.Add(own[i]);
            }
        }

        if (kept.Count != wanted.Count)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Собственных входов нашлось {kept.Count} из запрошенных {wanted.Count}: часть ссылок " +
                "не отвечает элементу BaseObjects. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["found"] = kept.Count,
                    ["requested"] = wanted.Count,
                    ["own_refs"] = ownRefs,
                });
        }

        var targetRefs = Api7Fillet.ReferencesOf(kept);
        var write = Api7Fillet.TryWriteBaseObjects(container, owner, kept);
        if (!write.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись набора в IFillet.BaseObjects не подтверждена: " +
                (write.Failure ?? "причина не сообщена") + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = write.Failure });
        }

        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "fillet.update.inputs");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // The set is re-read FROM THE LIVE MODEL: a write without effect must not look applied.
        var after = Api7Fillet.ReadBaseObjects(container, owner);
        var afterRefs = after is null ? null : Api7Fillet.ReferencesOf(after);
        var inputsSet = afterRefs is not null && SameSet(afterRefs, targetRefs);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));

        var volumeMatched = declared.IsConfirmed;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{owner}].BaseObjects ← [{string.Join(", ", targetRefs)}] " +
                          $"(было [{string.Join(", ", ownRefs)}]); предъявлены СОБСТВЕННЫЕ входы " +
                          $"признака: {kept.Count} из {own.Count}; адреса записанных объектов: " +
                          string.Join(", ", kept.Select(AddressOf)),
                Expected: "запись принята и подтверждена перестроением"),
            new("edges_read_back", inputsSet,
                Observed: afterRefs is null ? "не читается" : $"[{string.Join(", ", afterRefs)}]",
                Expected: $"[{string.Join(", ", targetRefs)}]"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}»",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
            DeclaredExpectation.Check("volume_after_update", declared),
        };

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (!inputsSet)
        {
            unverified.Add("edges_not_read_back — набор не перечитался в ожидаемом составе");
        }
        else if (declared.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после правки", declared));
        }
        else if (declared.IsNotConfirmed)
        {
            unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после правки", declared));
        }
        else if (!volumeMatched)
        {
            unverified.Add(
                "volume_not_as_expected — набор записан и перечитан, но объём не совпал с ожиданием");
        }

        if (!declared.IsDeclared)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка набора не " +
                "может быть подтверждена геометрически");
        }

            return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                inputsSet && sameFeature && volumeMatched
                    ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified),
            RadiusReadBackMm: null,
            EdgesReadBack: afterRefs?.Count,
            Warnings: DeclaredExpectation.Warnings(declared));
    }

    /// <summary>Edge-set edit by BODY edges (<c>edge_refs</c>) — the measurably valid currency for REPLACING the
    /// composition and invalid for REDUCING it.</summary>
    /// <remarks>Kept as a separate path rather than a shared branch: it has its own currency, its own measured
    /// experience (H2.4, 1→1) and its own limit (reduction collapses the feature — FL10). Merging the two
    /// paths would lose the distinction again, which is why the rows stand apart.</remarks>
    private UpdateFeatureResult UpdateFilletEdgeSetByBodyEdges(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        IReadOnlyList<string> edgeRefs,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.RadiusMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "radius_mm и edge_refs вместе запрещены: правка радиуса и правка набора — разные " +
                "предметы и разные подтверждения. Смешав их, ответ не отличит «применилось и то, и " +
                "другое» от «применилось одно из двух».",
                RetryPolicy.Never);
        }

        // The current set is FOR MATCHING the API5 feature to IFillet, not for deciding on mutation. It is
        // expanded as on creation: the reference registry plus three routes of unwrapping an edge into an
        // entity (MEASURED P2.2).
        // IMPORTANT ON THE SOURCE. The API5 definition (ksFilletDefinition.array()) returns no edges at all
        // on an EXISTING fillet — MEASURED (0 of 4 after a fillet, row FL10, a negative result kept in gap),
        // the same property that puts the edit route in API7 rather than API5. Relying on it for
        // IDENTIFICATION would depend on the very mechanism deemed non-working, so the primary source is the
        // live feature's own inputs in API7 (IFillet.BaseObjects) and the API5 definition stays a fallback.
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Набор рёбер скругления правится только через IFillet.BaseObjects: маршрут через " +
                "определение API5 (Clear/Add) правку набора не даёт — измерено восемью пробами " +
                "16.09.2026 и подтверждено пробой H-2. Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var beforeSource = entity.GetDefinition() as ksFilletDefinition;
        var beforeArray = beforeSource?.array() as ksEntityCollection;
        var currentEdges = new List<ksEntity>();
        if (beforeArray is not null)
        {
            for (var i = 0; i < beforeArray.GetCount(); i++)
            {
                if (beforeArray.GetByIndex(i) is ksEntity current)
                {
                    currentEdges.Add(current);
                }
            }
        }

        // The references of the feature's CURRENT inputs are the identification key. An API5 edge is
        // transferred to API7 and compared by Reference: the index is arbitrary, the name differs between
        // API5 and API7, and the radius does not tell two fillets of one radius apart.
        var currentRefs = new List<int>(currentEdges.Count);
        foreach (var edge in currentEdges)
        {
            if (ReferenceOfApi7(bridge, edge) is int reference)
            {
                currentRefs.Add(reference);
            }
        }

        int? index = null;
        string identifiedBy;
        if (currentRefs.Count > 0 && currentRefs.Count == currentEdges.Count)
        {
            // Primary route: the feature is identified by the COMPOSITION of its own inputs.
            index = Api7Fillet.FindIndexByInputReferences(container, currentRefs);
            identifiedBy = "состав входов";
        }
        else
        {
            // Fallback: the API5 definition returned no inputs (MEASURED), or not all transferred. The
            // definition radius is the only scalar both sides describe alike. The input count is taken
            // from IFillet (it always returns it), not from the API5 definition.
            identifiedBy = "радиус и число входов (определение API5 входов не отдало)";
            if (beforeSource?.radius is double radiusKey)
            {
                var byRadius = Api7Fillet.FindIndexesByRadius(container, radiusKey);
                if (byRadius.Count == 1)
                {
                    index = byRadius[0];
                }
            }
        }

        if (index is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Скругление не опознано однозначно в IModelContainer.Fillets ("
                + identifiedBy + "): ни одного либо несколько совпадений. Записать набор в чужой " +
                "признак означало бы молча испортить чужую геометрию. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["identified_by"] = identifiedBy,
                    ["current_input_refs"] = currentRefs,
                    ["api7_fillets"] = Api7Fillet.Count(container),
                    ["api7_by_input_refs"] =
                        Api7Fillet.FindIndexesByInputReferences(container, currentRefs),
                });
        }

        // The new set is built as a SUBSET OF THE FEATURE'S OWN INPUTS, not from objects newly obtained
        // from the body. Measured requirement: probe H-2 (H2.3/H2.5) accepts back ONLY the objects it
        // itself read from the feature — a subset of N objects read from BaseObjects is written, Clear()
        // is NOT called, body edges are NOT sought. Doing it differently (resolving client references
        // into body edges and re-transferring to API7) the feature does not accept — MEASURED by
        // acceptance (FL10: level=call_returned, V=80000 with err=None). Hence the rule: the required set
        // is SELECTED from the feature's inputs, and the client's references only SAY which to keep.
        var requested = new List<ksEntity>(edgeRefs.Count);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in edgeRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is not ksEdgeDefinition edge)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' указывает не на ребро (kind={stored.Kind}).",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            var unwrapped = UnwrapEdgeToEntity(edge, reference);
            routes.Add(unwrapped.Route);
            requested.Add(unwrapped.Entity);
        }

        // Identification of the requested edges in API7 terms. A body edge comes from API5 and needs the
        // ksAPI7Dual transfer; a feature input comes from BaseObjects already as an API7 object. Both
        // sides are reduced to int, but by different paths — that is the asymmetry that makes a Reference
        // match NOT a universal key.
        var liveInputs = Api7Fillet.ReadBaseObjects(container, index.Value);
        IReadOnlyList<IModelObject> targets;
        string selectionRoute;
        if (liveInputs is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IFillet.BaseObjects не перечитался: набор признака прочитать нечем, а опознать " +
                "признак без него нельзя. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var inputRefs = Api7Fillet.ReferencesOf(liveInputs);

        var wantedRefs = new List<int>(requested.Count);
        var unresolved = new List<string>();
        for (var i = 0; i < requested.Count; i++)
        {
            if (ReferenceOfApi7(bridge, requested[i]) is int wantedRef)
            {
                wantedRefs.Add(wantedRef);
            }
            else
            {
                unresolved.Add(edgeRefs[i]);
            }
        }

        if (unresolved.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Из запрошенных рёбер в API7 не перенеслось " + unresolved.Count + " (" +
                string.Join(", ", unresolved) + "): предъявить их признаку нечем. " +
                "Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["unresolved"] = unresolved });
        }

        // The new set is PRESENTED with objects obtained by transfer — as measured in probe H-2 (H2.4):
        // the product accepts transferred objects and does NOT require them to match what the feature
        // already holds. The feature must still be IDENTIFIED by its own inputs
        // (FindIndexByInputReferences): matching references take the SAME feature objects (the strictly
        // measured H2.3/H2.5 reduction path), non-matching ones are presented transferred (H2.4).
        // History: docs/decisions/adapter-features.md#fillet-edge-set-ban-lifted
        var overlapping = wantedRefs.Count(w => inputRefs.Contains(w));
        var targetsByOwn = overlapping > 0 && overlapping == wantedRefs.Count;

        if (targetsByOwn)
        {
            // All requested edges are among the feature's own inputs: the strictly measured reduction
            // path (H2.3 4→3, H2.5 4→2). Objects FROM BaseObjects are written, Clear() is not called.
            var ownKept = new List<IModelObject>(wantedRefs.Count);
            for (var i = 0; i < liveInputs.Count; i++)
            {
                if (i < inputRefs.Count && wantedRefs.Contains(inputRefs[i]))
                {
                    ownKept.Add(liveInputs[i]);
                }
            }

            targets = ownKept;
            selectionRoute = $"отбор из BaseObjects признака: {ownKept.Count} из {liveInputs.Count}";
        }
        else
        {
            // At least one requested edge is not held by the feature: TRANSFERRED objects are presented
            // (H2.4). The transfer is mandatory here: `requested` are API5 `ksEntity`, while
            // `BaseObjects` takes `IModelObject`. The same ksAPI7Dual mode as in identification.
            //
            // The unwrapping is taken from `requested` in the SAME order as `wantedRefs`: each object's
            // reference was already obtained above and stands in `wantedRefs` at the same index, so a
            // second transfer is not needed — and is not done, so that the object that lands in the set
            // is literally the one whose reference was measured.
            var transferredTargets = new List<IModelObject>(requested.Count);
            var transferFailed = new List<string>();
            for (var i = 0; i < requested.Count; i++)
            {
                if (bridge.TransferTo7(requested[i]) is IModelObject modelObject)
                {
                    transferredTargets.Add(modelObject);
                }
                else
                {
                    transferFailed.Add(edgeRefs[i]);
                }
            }

            if (transferFailed.Count > 0)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Ребро перенеслось в API7 для опознания, но не перенеслось для предъявления (" +
                    string.Join(", ", transferFailed) + "). Признак не изменён.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?> { ["unresolved_on_transfer"] = transferFailed });
            }

            // Separately stated and NOT checked here: whether this extends or replaces the set. The probe
            // measured replacement (1→1) and reduction (4→3, 4→2); a pure extension cannot be staged on
            // the four-corner reference — the plate has exactly four vertical corners. So the answer does
            // not claim "N added", it reports the measured composition after Update, and the confirmation
            // predicate (volume + re-read set) decides for itself.
            targets = transferredTargets;
            selectionRoute =
                $"предъявлены перенесённые объекты (H2.4): {transferredTargets.Count} из запрошенных, " +
                $"совпало с входами признака {overlapping} из {wantedRefs.Count}";
        }

        var targetRefs = Api7Fillet.ReferencesOf(targets);

        var write = Api7Fillet.TryWriteBaseObjects(container, index.Value, targets);
        if (!write.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись набора в IFillet.BaseObjects не подтверждена: " +
                (write.Failure ?? "причина не сообщена") + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = write.Failure });
        }

        // Without a rebuild the IFillet write stays a representation: the same order as on fillet
        // creation and on radius edit (F.10 + probe E).
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "fillet.update.edges");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // The set is re-read FROM THE LIVE MODEL, not from the object written into: a write without
        // effect (like ksFilletDefinition.radius on FL04r) must not look applied.
        var afterInputs = Api7Fillet.ReadBaseObjects(container, index.Value);
        var afterRefs = afterInputs is null ? null : Api7Fillet.ReferencesOf(afterInputs);

        var edgesSet = afterRefs is not null && SameSet(afterRefs, targetRefs);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{index.Value}].BaseObjects ← [{string.Join(", ", targetRefs)}] " +
                          $"(было [{string.Join(", ", currentRefs)}]); {selectionRoute}; " +
                          "адреса записанных объектов: " +
                          string.Join(", ", targets.Select(AddressOf)) + "; " +
                          "направления разворачивания запрошенных рёбер: " + string.Join(" / ", routes),
                Expected: "запись принята и подтверждена перестроением"),
            new("edges_read_back", edgesSet,
                Observed: afterRefs is null
                    ? "не читается"
                    : $"[{string.Join(", ", afterRefs)}]",
                Expected: $"[{string.Join(", ", targetRefs)}]"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}»",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        // ORDER OF REFUSALS. The two "the set was not applied" refusals below come BEFORE the
        // declared-expectation comparison: that comparison is the geometry check of an operation that
        // HAPPENED, so it must not run when the route did not take. Running it first would let a declared
        // mismatch MASK the named CAPABILITY_UNAVAILABLE / the erasure refusal. INVARIANT for every
        // DeclaredExpectation call site (row L11, naryad PRE_RELEASE_0_6_0 П1.3).
        // History: docs/decisions/adapter-core.md#declared-expectation-rule

        // FEATURE ERASURE IS A REFUSAL, NOT A "SUCCESS AT A LOWER LEVEL". MEASURED: a presented set of
        // BODY edges can describe corners the feature does NOT hold, meaning "build the fillet anew on
        // these edges", not "keep the old one and add" — the feature was COLLAPSED while err=None and
        // level=call_returned. So "the feature stopped reading as a fillet" is a refusal BEFORE returning
        // success, carrying partial_effects=true.
        // History: docs/decisions/adapter-features.md#fillet-erasure-and-no-effect
        if (afterRefs is null || !sameFeature)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка набора СТЁРЛА признак скругления: после записи и перестроения признак " +
                "больше не читается как скругление" +
                (volumeAfter is double lost
                    ? $" (объём тела {lost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}, " +
                      $"до правки {volumeBefore?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитан"})"
                    : string.Empty) +
                ". Предъявленный набор описан рёбрами ТЕЛА, а не собственными входами признака: " +
                "такой набор задаёт скругление ЗАНОВО по указанным рёбрам и не сохраняет прежний " +
                "состав. Для сокращения и замены это безразлично, для расширения — невыразимо. " +
                "Модель ИЗМЕНЕНА этой правкой — перечитайте контекст и документ.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["presented_inputs"] = targetRefs,
                    ["inputs_before"] = currentRefs,
                    ["selection_route"] = selectionRoute,
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["feature_read_back"] = afterRefs is not null,
                    ["feature_state_survived"] = sameFeature,
                });
        }

        // A WRITE WITHOUT EFFECT IS ALSO A REFUSAL. MEASURED: the write and rebuild passed, err=None,
        // level=call_returned, edges_read_back=1 against 2 presented, and the volume stayed on ONE corner.
        // Between "the feature was erased" and "the set was written" there is a third outcome — "nothing
        // happened" — also a refusal. The criterion is the RE-READ COMPOSITION, not the volume;
        // partial_effects distinguishes a changed model from an untouched one.
        // History: docs/decisions/adapter-features.md#fillet-erasure-and-no-effect
        if (!edgesSet)
        {
            var modelChanged = volumeAfter is double volumeNow && volumeBefore is double volumeWas
                               && Math.Abs(volumeNow - volumeWas) > ProfileArea.Tolerance(volumeWas);
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Набор не перечитался в запрошенном составе: записано " +
                $"[{string.Join(", ", targetRefs)}], на модели " +
                (afterRefs is null ? "набор не читается" : $"[{string.Join(", ", afterRefs)}]") +
                ". Набор рёбер ТЕЛА задаёт скругление ЗАНОВО по указанным рёбрам, а не «оставь " +
                "прежнее и добавь»: расширение набора им не выражается (сокращение и замена — " +
                "выражаются, они идут через base_object_refs). " +
                (modelChanged
                    ? "Модель ИЗМЕНЕНА этой правкой — перечитайте контекст и документ."
                    : "Геометрия не изменилась, но ревизия поднята: перечитайте контекст."),
                modelChanged ? RetryPolicy.AfterReconciliation : RetryPolicy.Never,
                partialEffects: modelChanged,
                details: new Dictionary<string, object?>
                {
                    ["presented_inputs"] = targetRefs,
                    ["inputs_read_back"] = afterRefs,
                    ["inputs_before"] = currentRefs,
                    ["selection_route"] = selectionRoute,
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["expected_volume_mm3"] = command.ExpectedVolumeMm3,
                    ["feature_read_back"] = afterRefs is not null,
                    ["feature_state_survived"] = sameFeature,
                });
        }

        // The declared expectation is the geometry check of this tool: a mismatch is a REFUSAL, an
        // unreadable volume is a NAMED gap (docs/decisions/adapter-core.md#declared-expectation-rule).
        var declared = DeclaredExpectation.Evaluate(
            command.ExpectedVolumeMm3, volumeAfter, () => ReadVolume(document));
        checks.Add(DeclaredExpectation.Check("volume_after_update", declared));

        var volumeMatched = declared.IsConfirmed;

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (declared.IsUnverifiable)
        {
            unverified.Add(DeclaredExpectation.UnverifiableReason("объём после правки", declared));
        }
        else if (declared.IsNotConfirmed)
        {
            unverified.Add(DeclaredExpectation.NotConfirmedReason("объём после правки", declared));
        }
        else if (!volumeMatched)
        {
            unverified.Add(edgesSet
                ? "volume_not_as_expected — набор записан и перечитан, но измерение объёма не совпало с ожиданием"
                : "edges_not_read_back — набор не перечитался в ожидаемом составе");
        }

        if (!declared.IsDeclared)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка набора не " +
                "может быть подтверждена геометрически");
        }

        var geometryConfirmed = edgesSet && sameFeature && volumeMatched;

            return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            // The radius was not touched: the edge set and the radius are different edit subjects, and the
            // answer must not look as if both changed.
            RadiusReadBackMm: null,
            EdgesReadBack: afterRefs?.Count,
            Warnings: DeclaredExpectation.Warnings(declared));
    }

    /// <summary>Compare compositions as sets: the collection's output order is non-deterministic.</summary>
    private static bool SameSet(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var a = left.OrderBy(x => x).ToArray();
        var b = right.OrderBy(x => x).ToArray();
        return a.SequenceEqual(b);
    }

    /// <summary>A stable reference of an API5 edge (<c>ksEntity</c>) transferred to API7.</summary>
    /// <remarks>Transferred by the same <c>ksAPI7Dual</c> mode as everything else: probe E measured that a wrong
    /// transfer mode gives not an error but an object that "reads" yet represents a different entity.
    /// null means "did not transfer" — an answer, not a zero.</remarks>
    private static int? ReferenceOfApi7(Api7Bridge bridge, ksEntity edge)
    {
        try
        {
            return bridge.TransferTo7(edge) is IModelObject modelObject ? modelObject.Reference : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Identity of a COM object independent of its own properties: two references to one object give one
    /// number, even if their <c>Reference</c> differs or does not read.</summary>
    /// <remarks>
    /// Needed for exactly one distinction that cannot otherwise be resolved: "the feature-input reference
    /// and the body-edge reference did not match" may mean either TWO DIFFERENT objects with different
    /// numbering bands, or ONE object whose <c>Reference</c> is computed in the reading context. The
    /// <c>Reference</c> property does not tell these two apart; the RCW address binding does.
    /// </remarks>
    private static string AddressOf(object instance)
    {
        try
        {
            return Marshal.GetIUnknownForObject(instance).ToString("X");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return "недоступен";
        }
    }
}
