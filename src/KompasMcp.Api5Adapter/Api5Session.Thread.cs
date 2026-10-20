using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Native thread designation — block SM-08 (profile <c>threads-minimal-v1</c>).</summary>
/// <remarks>DOC: <c>ibody7_threads.html</c>, <c>ithreads_add.html</c> (<c>Add</c> → properties →
/// <c>Update</c>), <c>ithread_propers.html</c> and <c>ithreadsparameters_propers.html</c> (diameter, pitch,
/// read-only <c>Internal</c>). INVARIANT: confirmed by the tree object and by parameters READ BACK, never by
/// <c>Update() = true</c>; the volume is expected NOT to change.
/// History: docs/decisions/adapter-features.md#thread-route
/// </remarks>
public partial class Api5Session
{
    /// <summary>A native thread designation on a circular edge or a cylindrical face.</summary>
    public ThreadResult CreateThread(ThreadCommand command)
    {
        ValidateThreadCommand(command);

        var document = RequireDocument(command.DocumentId);
        RequirePart(document);

        // LIMIT: the profile is single-body; the designation belongs to one body's face or edge.
        var bodiesBefore = CountBodies(document);
        if (bodiesBefore != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Обозначение резьбы строится на однотельном контексте, а в документе тел: {bodiesBefore}. "
                + "Многотельный контекст в профиль threads-minimal-v1 не входит; признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["body_count"] = bodiesBefore });
        }

        var baseKind = command.EdgeRef is not null ? "edge" : "face";
        object basePayload = command.EdgeRef is not null
            ? RequireEdge(document, command.EdgeRef)
            : RequireFace(document, command.FaceRef!);

        ksFaceDefinition? initialBorder = command.InitialBorderRef is null
            ? null
            : RequireFace(document, command.InitialBorderRef);
        ksFaceDefinition? finalBorder = command.FinalBorderRef is null
            ? null
            : RequireFace(document, command.FinalBorderRef);

        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var revisionBefore = document.Revision;

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна")
                + ". Резьба этим маршрутом не создаётся; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        if (bridge.TransferTo7(basePayload) is not IModelObject base7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Базовый объект резьбы вида «" + baseKind + "» не перенесён в API7 как IModelObject ("
                + (bridge.BridgeFailure ?? "TransferInterface вернул null") + "). Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["base_kind"] = baseKind });
        }

        var initial7 = TransferBorder(bridge, initialBorder, "initial_border_ref", command.InitialBorderRef);
        var final7 = TransferBorder(bridge, finalBorder, "final_border_ref", command.FinalBorderRef);

        IThread? thread = null;
        try
        {
            thread = ThreadsOf(container)?.Add() as IThread;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IThreads.Add() прервался: " + ex.GetType().Name + ". Резьба не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        if (thread is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IThreads.Add() не вернул IThread: коллекция обозначений резьбы не доступна. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        // DOC: IThread has IThreadsParameters as an ADDITIONAL interface — diameter and pitch live
        // there, not on IThread. INVARIANT: without it the numbers could not be written, so the call is
        // refused instead of creating a designation with the product's own defaults.
        if (thread is not IThreadsParameters parameters)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IThread не отвечает IThreadsParameters: диаметр и шаг резьбы этим маршрутом не записать. "
                + "Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            thread.BaseObject = base7;
            if (initial7 is not null)
            {
                thread.InitialBorder = initial7;
            }

            if (final7 is not null)
            {
                thread.FinalBorder = final7;
            }

            thread.LeftThread = command.LeftHand;
            if (!command.AutoDiameter)
            {
                parameters.Diameter = command.NominalDiameterMm!.Value;
            }

            parameters.Pitch = command.PitchMm;
            thread.AutoDiameter = command.AutoDiameter;
            thread.AutoLenght = command.AutoLength;
            if (!command.AutoLength)
            {
                thread.Lenght = command.LengthMm!.Value;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Свойства резьбы не записаны: " + ex.GetType().Name + ". Признак остался в дереве без "
                + "параметров и назван в partial_effects.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        ClearKompasResult(document);
        var created = SafeBool(() => ((IModelObject)thread).Update());
        if (created != true)
        {
            var details = new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" };
            AddKompasFailure(details, ReadKompasFailure(document));
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Update() обозначения резьбы вернул " + (created is null ? "ошибку вызова" : "false")
                + ": признак не построен. Базовое ребро не на цилиндре или граница не ограничивает резьбу "
                + "дают именно этот исход — это отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: details);
        }

        RebuildApi7AndSettle(container, document);

        // The reference must point at the TREE element: an API7 object registered as a feature resolves
        // to the wrapper class and every later call would answer STALE_REFERENCE.
        var treeEntity = FindThreadEntity(document, SafeModelName(thread));
        if (treeEntity is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Признак построен, но в дереве не найден элемент с его именем: ссылка на признак не "
                + "выдаётся, потому что ссылка на объект API7 не разрешается в признак. Признак остался "
                + "в документе.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var reference = References.Register("feature", document.Id, document.Revision, treeEntity);
        BumpRevision(document, "thread.create");
        RequireNoApi7FeatureError(document, thread, reference.Id, "Резьба");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var (diameter, pitch, length, internalThread, leftHand, autoDiameter, autoLength) =
            ReadThreadParameters(thread, parameters);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Update() вернул true"),
            new("tree_object_present", true, "элемент дерева типа " + KompasObjectTypes.Thread),
            new("volume_unchanged", Same(volumeBefore, volumeAfter),
                Observed: Num(volumeAfter), Expected: Num(volumeBefore) + " (обозначение не меняет тело)"),
            new("face_count_unchanged", Same(facesBefore, facesAfter),
                Observed: Num(facesAfter), Expected: Num(facesBefore)),
            new("diameter_read_back", command.AutoDiameter || Same(diameter, command.NominalDiameterMm),
                Observed: Num(diameter),
                Expected: command.AutoDiameter ? "автоопределение" : Num(command.NominalDiameterMm)),
            new("pitch_read_back", Same(pitch, command.PitchMm),
                Observed: Num(pitch), Expected: Num(command.PitchMm)),
            new("length_read_back", command.AutoLength || Same(length, command.LengthMm),
                Observed: Num(length),
                Expected: command.AutoLength ? "автоопределение по границам" : Num(command.LengthMm)),
            new("left_hand_read_back", leftHand == command.LeftHand,
                Observed: leftHand?.ToString(), Expected: command.LeftHand.ToString()),
        };

        var unverified = new List<string>
        {
            "винтовая геометрия не строится: это условное обозначение, а не резьба как поверхность; "
            + "неизменный объём тела — ожидаемый исход, а не признак отказа",
        };

        if (command.AutoDiameter)
        {
            unverified.Add("диаметр при автоопределении взят из модели, а не из запроса: значение "
                + Num(diameter) + " мм");
        }

        return new ThreadResult
        {
            FeatureRef = ToDto(reference, SafeModelName(thread)),
            Family = ThreadRules.Family,
            BaseKind = baseKind,
            BaseRef = null,
            InitialBorderRef = null,
            FinalBorderRef = null,
            NominalDiameterMm = diameter,
            PitchMm = pitch,
            LengthMm = length,
            Internal = internalThread,
            LeftHand = leftHand,
            AutoDiameter = autoDiameter,
            AutoLength = autoLength,
            VolumeBeforeMm3 = volumeBefore,
            VolumeAfterMm3 = volumeAfter,
            FacesBefore = facesBefore,
            FacesAfter = facesAfter,
            RevisionBefore = revisionBefore,
            RevisionAfter = document.Revision,
            Verification = new VerificationDto(
                checks.TrueForAll(c => c.Passed) ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
        };
    }

    /// <summary>The thread collection of the document's body.</summary>
    /// <remarks>DOC: <c>ithreads.html</c> names <c>Symbols3DContainer::Threads</c> and
    /// <c>ibody7_threads.html</c> names <c>IBody7.Threads</c>; the container the bridge hands over
    /// answers the same property, so the collection is taken from it and no body is walked by hand.
    /// </remarks>
    private static IThreads? ThreadsOf(IModelContainer container)
    {
        try
        {
            // DOC: <c>ithreads.html</c> names <c>Symbols3DContainer::Threads</c>; the container answers it
            // through that interface, not on its own — a typed cast, as everywhere else in the bridge.
            return (container as ISymbols3DContainer)?.Threads;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private IModelObject? TransferBorder(
        Api7Bridge bridge, ksFaceDefinition? border, string field, string? reference)
    {
        if (border is null)
        {
            return null;
        }

        if (bridge.TransferTo7(border) is IModelObject border7)
        {
            return border7;
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            "Граница резьбы (" + field + ") не перенесена в API7 как IModelObject ("
            + (bridge.BridgeFailure ?? "TransferInterface вернул null") + "). Признак не создавался.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { [field] = reference });
    }

    /// <summary>Diameter, pitch, length and flags read from the API7 feature.</summary>
    /// <remarks>INVARIANT: the read goes through the SAME route that writes — API5 and API7 are never
    /// called on one feature. An unreadable field is null with a reason, never a zero.</remarks>
    private static (double? Diameter, double? Pitch, double? Length, bool? Internal, bool? LeftHand,
        bool? AutoDiameter, bool? AutoLength) ReadThreadParameters(
        IThread thread, IThreadsParameters parameters) =>
        (SafeDouble(() => parameters.Diameter),
         SafeDouble(() => parameters.Pitch),
         SafeDouble(() => thread.Lenght),
         TryFlag(() => parameters.Internal),
         TryFlag(() => thread.LeftThread),
         TryFlag(() => thread.AutoDiameter),
         TryFlag(() => thread.AutoLenght));

    private static bool? TryFlag(Func<bool> read)
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

    /// <summary>Two measured values within the project's single tolerance, or both unreadable.</summary>
    private static bool Same(double? left, double? right) =>
        left is { } a && right is { } b
            ? Math.Abs(a - b) <= Math.Max(1e-3d, 1e-9d * Math.Max(Math.Abs(a), Math.Abs(b)))
            : left is null && right is null;

    /// <summary>The tree element of a thread designation, matched by the name the feature carries.</summary>
    private static ksEntity? FindThreadEntity(DocumentEntry document, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            // MEASURED: a designation is NOT an operation of the tree — the collection of
            // o3d_operationElement (110) holds only the extrusion after a thread is created — so the
            // thread-typed collection (58) is asked first, and the operation collection is the fallback.
            if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.Thread))
                is not ksEntityCollection collection)
            {
                return null;
            }

            ksEntity? found = null;
            var matches = 0;
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity
                    && entity.type == KompasObjectTypes.Thread
                    && string.Equals(entity.name, name, StringComparison.Ordinal))
                {
                    found = entity;
                    matches++;
                }
            }

            return matches == 1 ? found : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The family name the read side publishes, shared with the contract.</summary>
    private const string ThreadFamily = ThreadRules.Family;

    /// <summary>Thread parameters read through API7 by the name the tree element carries.</summary>
    /// <remarks>INVARIANT: the read goes through the SAME route that writes — the API7 collection, never the
    /// API5 definition (there is none). MEASURED: <c>IThreads.FindThread</c> and <c>IThread.ThreadFaces</c> are
    /// documented for v24 but absent from the installed interop, so the collection is walked by name.
    /// </remarks>
    private ThreadDto? ReadThreadFeature(DocumentEntry document, ksEntity entity, out string? note)
    {
        note = null;
        var name = SafeText(() => entity.name);
        if (string.IsNullOrEmpty(name))
        {
            note = "thread_name_unreadable — имя элемента дерева не прочиталось, а объект API7 "
                + "сопоставляется только по имени";
            return null;
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        var threads = container is null ? null : ThreadsOf(container);
        if (threads is null)
        {
            note = "thread_collection_unavailable — коллекция IThreads не отвечает через "
                + "Symbols3DContainer (" + (bridge.BridgeFailure ?? "мост не построен") + ")";
            return null;
        }

        IThread? thread = null;
        try
        {
            thread = threads.Thread[(object)name] as IThread;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            note = "thread_object_not_found — в IThreads нет обозначения с именем «" + name + "» ("
                + ex.GetType().Name + ")";
            return null;
        }

        if (thread is null)
        {
            note = "thread_object_not_found — IThreads.Thread по имени «" + name + "» вернул null";
            return null;
        }

        if (thread is not IThreadsParameters parameters)
        {
            note = "thread_parameters_unavailable — IThread не отвечает IThreadsParameters: диаметр и шаг "
                + "не читаются";
            return null;
        }

        var (diameter, pitch, length, internalThread, leftHand, autoDiameter, autoLength) =
            ReadThreadParameters(thread, parameters);

        return new ThreadDto(diameter, pitch, length, internalThread, leftHand, autoDiameter, autoLength,
            BaseKindOf(thread));
    }

    /// <summary><c>edge</c> or <c>face</c> — which kind of base object the model holds.</summary>
    private static string? BaseKindOf(IThread thread)
    {
        try
        {
            var baseObject = thread.BaseObject;
            if (baseObject is null)
            {
                return null;
            }

            // MEASURED: an edge base answers the IFace cast too, so the edge interface is asked FIRST;
            // a base that answers neither stays null instead of being guessed.
            if (baseObject is IEdge)
            {
                return "edge";
            }

            return baseObject is IFace ? "face" : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? SafeText(Func<string?> read)
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
    private static void ValidateThreadCommand(ThreadCommand command)
    {
        if (!ThreadRules.SingleBase(command.EdgeRef, command.FaceRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Базовый объект резьбы задаётся РОВНО ОДНОЙ формой: edge_ref (круговое ребро цилиндра) "
                + "или face_ref (цилиндрическая грань). Обе сразу или ни одной — отказ до COM.",
                RetryPolicy.Never);
        }

        if (command.FaceRef is not null
            && (command.InitialBorderRef is null || command.FinalBorderRef is null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "При базовом объекте-грани начальная и конечная границы ОБЯЗАТЕЛЬНЫ: без них длина "
                + "резьбы не определена.",
                RetryPolicy.Never);
        }

        if (!ThreadRules.BordersComplete(command.InitialBorderRef, command.FinalBorderRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Границы резьбы задаются парой: начальная и конечная. Одна из двух — отказ до COM.",
                RetryPolicy.Never);
        }

        if (!ThreadRules.ValueOrAuto(command.NominalDiameterMm, command.AutoDiameter))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Диаметр резьбы: либо конечное nominal_diameter_mm > 0, либо auto_diameter = true без "
                + "значения. Оба сразу, ни одного, ноль, отрицательное, NaN и бесконечность — отказ до COM.",
                RetryPolicy.Never);
        }

        if (!ThreadRules.ValueOrAuto(command.LengthMm, command.AutoLength))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Длина резьбы: либо конечное length_mm > 0, либо auto_length = true без значения. Оба "
                + "сразу, ни одного, ноль, отрицательное, NaN и бесконечность — отказ до COM.",
                RetryPolicy.Never);
        }

        if (command.AutoLength && (command.InitialBorderRef is null || command.FinalBorderRef is null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Автоопределение длины берёт её по границам, поэтому auto_length требует обеих границ.",
                RetryPolicy.Never);
        }

        if (!ThreadRules.PositiveFinite(command.PitchMm))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг резьбы pitch_mm обязателен и должен быть конечным числом > 0: профиль не подбирает "
                + "шаг по стандарту.",
                RetryPolicy.Never);
        }
    }
}
