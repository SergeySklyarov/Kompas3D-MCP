using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.Interference;

namespace KompasMcp.Api5Adapter;

/// <summary>Interference domain — block G1, profile <c>assembly-interference-minimal-v1</c>, modes
/// <c>INT-01…INT-04</c>. Both tools only READ: no revision bump, no rebuild, no model object.</summary>
/// <remarks>INVARIANT: intersections go through API5 (<c>ksBody.CheckIntersectionWithBody</c>) and the
/// gap through API7 (<c>IPart7.Measurement3D</c>); the two routes are never mixed inside one pair.
/// INVARIANT: a kernel call that threw leaves the pair UNKNOWN (null), never "no intersection".
/// INVARIANT: no server-side geometry — no AABB pre-filter: the old 0,005/0,015 mm thresholds dropped
/// real small intersections (docs/04_KOMPAS_API_NOTES.md §4.4).
/// History: docs/decisions/assembly.md#interference-contracts</remarks>
public sealed partial class Api5Session
{
    /// <summary>The intersection route as one line, published in the answer so a reader sees what was
    /// called rather than what was intended.</summary>
    private const string InterferenceRoute =
        "api5:ksDocument3D.PartCollection(true) → ksPart.BodyCollection → ksBody.CheckIntersectionWithBody " +
        "→ ksIntersectionResult.GetCount/GetIntersectionType; грани — ksBody.GetIntersectionFacesWithBody";

    /// <summary>The gap route as one line. The measurement interface is a typed twin of the installed
    /// type library because the shipped wrapper is older (docs/04 §4.52).</summary>
    private const string GapRoute =
        "api7:IKompasDocument3D.TopPart → проверка версии КОМПАС (не ниже 23) → " +
        "IPart7.Measurement3D (типизированный двойник установленного TLB) " +
        "→ IMeasurement3D.Object1/Object2 → Calculate → Lmin/GetMinPoint1/2/Angle";

    // ── INT-01…INT-03 ──
    /// <summary>Check the bodies of every requested component pair for intersection.</summary>
    public CheckInterferenceResult CheckInterference(CheckInterferenceCommand command)
    {
        var document = RequireAssemblyForInterference(command.DocumentId);
        var notes = new List<string>();
        var components = ResolveInterferenceComponents(document, command.ComponentRefs, notes);

        if (components.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Проверка пересечений требует не менее двух РАЗНЫХ компонентов, получено " +
                $"{components.Count}: пары не из чего составить.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["components"] = components.Count });
        }

        // PAIR ENUMERATION IS A PURE RULE (InterferenceRules.Pairs): each unordered pair once, no pair
        // with itself — so the count is checkable without KOMPAS.
        var pairs = new List<InterferencePairDto>();
        foreach (var (a, b) in InterferenceRules.Pairs(components.Count))
        {
            pairs.Add(CheckInterferencePair(document, components[a], components[b], command, notes));
        }

        return new CheckInterferenceResult(
            pairs.Count,
            pairs,
            command.CheckTangent,
            command.CheckTangent
                ? "касания считались пересечениями"
                : "касания пересечениями НЕ считались",
            InterferenceRoute,
            document.Revision,
            notes);
    }

    /// <summary>One component under check: its ordinal (the address) and its API5 view.</summary>
    private sealed record InterferenceComponent(int Ordinal, ksPart Part5, string? ReferenceId);

    private List<InterferenceComponent> ResolveInterferenceComponents(
        DocumentEntry document, IReadOnlyList<string>? requested, List<string> notes)
    {
        var components = new List<InterferenceComponent>();
        if (requested is null || requested.Count == 0)
        {
            var parts = ComponentParts5(document);
            for (var ordinal = 0; ordinal < parts.Count; ordinal++)
            {
                components.Add(new InterferenceComponent(ordinal, parts[ordinal], null));
            }

            notes.Add($"component_refs не заданы: проверяются все адресуемые компоненты верхнего " +
                      $"уровня, их {components.Count}");
            return components;
        }

        // A REPEATED COMPONENT IS AN ARGUMENT DEFECT, not a pair with itself: "the same body intersects
        // itself" is a different question and is not asked here. The rule is pure and unit-tested.
        if (InterferenceRules.HasRepeats(requested))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список component_refs содержит один и тот же компонент более одного раза: пара " +
                "компонента с самим собой не проверяется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_refs"] = requested.ToArray(),
                });
        }

        foreach (var referenceId in requested)
        {
            var stored = References.Require(referenceId, document.Id, document.Revision);
            if (stored.Payload is not ComponentPayload payload)
            {
                throw new KompasContractException(
                    ErrorCodes.StaleReference,
                    $"Ссылка «{referenceId}» не адресует компонент: это {stored.Kind}.",
                    RetryPolicy.ReacquireContext);
            }

            // ComponentPart5 refuses a NESTED component by name (CAPABILITY_UNAVAILABLE) and checks
            // identity before touching anything — the same rule the placement write follows.
            var part5 = ComponentPart5(document, payload, out var lookupNote);
            notes.Add($"component_address — {lookupNote}");
            components.Add(new InterferenceComponent(payload.Ordinal, part5, referenceId));
        }

        return components;
    }

    private InterferencePairDto CheckInterferencePair(
        DocumentEntry document,
        InterferenceComponent a,
        InterferenceComponent b,
        CheckInterferenceCommand command,
        List<string> notes)
    {
        var labelA = a.ReferenceId ?? $"#{a.Ordinal}";
        var labelB = b.ReferenceId ?? $"#{b.Ordinal}";
        var bodiesA = BodiesOf(a.Part5);
        var bodiesB = BodiesOf(b.Part5);

        // A COMPONENT WITHOUT BODIES CANNOT INTERSECT, and that is NOT "no intersection measured":
        // the pair is left unknown and named, because the question was never answered by the kernel.
        if (bodiesA.Count == 0 || bodiesB.Count == 0)
        {
            notes.Add($"component_has_no_body — у компонента «{(bodiesA.Count == 0 ? labelA : labelB)}» " +
                      "не прочитано ни одного тела (ksPart.BodyCollection): пересечение пары НЕ " +
                      "проверено, исход неизвестен");
            return new InterferencePairDto(labelA, labelB, null, null,
                Array.Empty<InterferenceHitDto>(), "component_has_no_body");
        }

        var hits = new List<InterferenceHitDto>();
        var failures = 0;
        for (var ia = 0; ia < bodiesA.Count; ia++)
        {
            for (var ib = 0; ib < bodiesB.Count; ib++)
            {
                object? raw;
                try
                {
                    raw = bodiesA[ia].CheckIntersectionWithBody(bodiesB[ib], command.CheckTangent);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // A COM EXCEPTION IS NOT "NO INTERSECTION" — the pair keeps the existing
                    // INTERSECTION_UNKNOWN outcome and the rest of the pairs still run.
                    failures++;
                    notes.Add($"INTERSECTION_UNKNOWN — тела {ia} и {ib} пары «{labelA}»/«{labelB}»: " +
                              $"ksBody.CheckIntersectionWithBody бросил {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                // DOC: k sbody_checkintersectionwithbody.html — «Интерфейс ksIntersectionResult - в
                // случае успеха, NULL - если пересечений нет». NULL is an ANSWER here, not silence.
                if (raw is null)
                {
                    continue;
                }

                if (raw is not ksIntersectionResult result)
                {
                    failures++;
                    notes.Add($"INTERSECTION_UNKNOWN — тела {ia} и {ib} пары «{labelA}»/«{labelB}»: " +
                              $"CheckIntersectionWithBody вернул {raw.GetType().Name}, а не " +
                              "ksIntersectionResult: результат не прочитан");
                    continue;
                }

                var count = result.GetCount();
                if (count <= 0)
                {
                    notes.Add($"intersection_result_without_count — тела {ia} и {ib} пары " +
                              $"«{labelA}»/«{labelB}»: результат получен, но GetCount()={count}");
                    hits.Add(new InterferenceHitDto(ia, ib, "unread", null, null, null, null));
                    continue;
                }

                var faces = command.IncludeFaces
                    ? IntersectionFacesOf(bodiesA[ia], bodiesB[ib], notes, labelA, labelB)
                    : null;

                for (var k = 0; k < count; k++)
                {
                    var typeValue = result.GetIntersectionType(k);
                    hits.Add(new InterferenceHitDto(
                        ia, ib,
                        InterferenceRules.TypeName(typeValue),
                        faces?.IntersectingA,
                        faces?.IntersectingB,
                        faces?.ConnectedA,
                        faces?.ConnectedB));
                }
            }
        }

        // THE MEANING OF THE RESULT IS A PURE RULE TOO: a NULL answer is "no intersection", a failed
        // call is UNKNOWN (never false), and a returned result is a found intersection.
        var outcome = InterferenceRules.Decide(hits.Select(h => h.Type).ToList(), failures);
        return new InterferencePairDto(
            labelA, labelB, outcome.Intersecting, outcome.Volumetric, hits, outcome.Basis);
    }

    /// <summary>Interacting faces of one intersecting body pair, as numbers in each body's
    /// <c>FaceCollection</c>.</summary>
    private sealed record IntersectionFaces(
        IReadOnlyList<int>? IntersectingA,
        IReadOnlyList<int>? IntersectingB,
        IReadOnlyList<int>? ConnectedA,
        IReadOnlyList<int>? ConnectedB);

    private static IntersectionFaces? IntersectionFacesOf(
        ksBody bodyA, ksBody bodyB, List<string> notes, string labelA, string labelB)
    {
        try
        {
            // DOC: ksbody_getintersectionfaceswithbody.html — «intersectionFaces1 - пересекаемые
            // грани первого тела», «connectedFaces1 - совпадающие грани первого тела»; TRUE —
            // «в случае успешного завершения», FALSE — «в случае неудачи».
            var ok = bodyA.GetIntersectionFacesWithBody(
                bodyB, out var intersectingA, out var intersectingB, out var connectedA, out var connectedB);
            if (ok == 0)
            {
                notes.Add($"intersection_faces_unread — тела пары «{labelA}»/«{labelB}»: " +
                          "GetIntersectionFacesWithBody вернул FALSE, грани не прочитаны");
                return null;
            }

            return new IntersectionFaces(
                FaceIndicesOf(bodyA, intersectingA),
                FaceIndicesOf(bodyB, intersectingB),
                FaceIndicesOf(bodyA, connectedA),
                FaceIndicesOf(bodyB, connectedB));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"intersection_faces_unread — тела пары «{labelA}»/«{labelB}»: " +
                      $"GetIntersectionFacesWithBody бросил {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Numbers of the returned face objects in the body's <c>FaceCollection</c>.</summary>
    /// <remarks>IDENTITY IS COMPARED ON THE COM OBJECT, not on a managed wrapper: the same face can
    /// come back as a different RCW, and a reference comparison would then find nothing. A face that
    /// is not in the collection is NAMED, not dropped.</remarks>
    private static IReadOnlyList<int>? FaceIndicesOf(ksBody body, object? variant)
    {
        var returned = Flatten(variant);
        if (returned.Count == 0)
        {
            return Array.Empty<int>();
        }

        if (body.FaceCollection() is not ksFaceCollection faces)
        {
            return null;
        }

        var count = faces.GetCount();
        var indices = new List<int>();
        foreach (var face in returned)
        {
            var found = false;
            for (var index = 0; index < count; index++)
            {
                if (faces.GetByIndex(index) is { } candidate && SameComObject(face, candidate))
                {
                    indices.Add(index);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                indices.Add(-1);
            }
        }

        return indices;
    }

    /// <summary>The returned face objects: one object comes as <c>VT_DISPATCH</c>, several as
    /// <c>VT_ARRAY | VT_DISPATCH</c> (DOC: ksbody_getintersectionfaceswithbody.html).</summary>
    private static List<object> Flatten(object? variant)
    {
        var items = new List<object>();
        switch (variant)
        {
            case null:
                break;
            case Array array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }

                break;
            default:
                items.Add(variant);
                break;
        }

        return items;
    }

    // ── INT-04 ──
    /// <summary>Minimum distance (and the angle, where defined) between two components or their
    /// faces, through the documented API7 <c>IPart7.Measurement3D</c>.</summary>
    public MeasureGapResult MeasureGap(MeasureGapCommand command)
    {
        var document = RequireAssemblyForInterference(command.DocumentId);
        var notes = new List<string>();

        if (InterferenceRules.SameObject(command.Object1.ComponentRef, command.Object1.FaceIndex,
                command.Object2.ComponentRef, command.Object2.FaceIndex))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Оба измеряемых объекта — один и тот же компонент и одна и та же грань: расстояние " +
                "объекта до себя не измеряется.",
                RetryPolicy.Never);
        }

        var first = ResolveMeasureObject(document, command.Object1, "object1", notes);
        var second = ResolveMeasureObject(document, command.Object2, "object2", notes);
        var measurement = Measurement3D(document, notes);

        try
        {
            measurement.set_Object1(first);
            measurement.set_Object2(second);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"IMeasurement3D не принял объекты измерения: {ex.GetType().Name}: {ex.Message}. " +
                "Объекты двух разных компонентов этим маршрутом не измерены.",
                RetryPolicy.Never);
        }

        ksMeasureResultEnum measureResult;
        try
        {
            measureResult = measurement.Calculate();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"IMeasurement3D.Calculate() прервался: {ex.GetType().Name}: {ex.Message}. " +
                "Результат измерения не получен.",
                RetryPolicy.Never);
        }

        // DOC: ksmeasureresultenum.html — «ksMResUnknown 0 Не определен». An undefined result is a
        // NAMED refusal, not a distance of zero.
        if (measureResult == ksMeasureResultEnum.ksMResUnknown)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IMeasurement3D.Calculate() вернул ksMResUnknown: расстояние между этими объектами " +
                "не определено, числом это не выдаётся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["measure_result"] = "ksMResUnknown" });
        }

        var measureName = measureResult.ToString();
        var minDistance = TryDouble(() => measurement.get_Lmin());
        var angle = TryDouble(() => measurement.get_Angle());
        var angleValid = TryBool(() => measurement.get_IsAngleValid());
        var minPoints = TryPoints(measurement.GetMinPoint1, measurement.GetMinPoint2);

        if (minDistance is null)
        {
            notes.Add("min_distance_unread — IMeasurement3D.Lmin не прочитан: справка оговаривает " +
                      "«Система определяет значение расстояния между объектами (если оно не нулевое)», " +
                      "поэтому ноль здесь не подставляется");
        }

        if (minPoints is null)
        {
            // MEASURED: GetMinPoint1/2 возвращают FALSE, когда ближайшие точки НЕ ЕДИНСТВЕННЫ (у двух
            // параллельных граней годится любая точка плоскости). Это НАЗВАННЫЙ исход ядра, а не
            // ноль: подставить координаты значило бы выдать за измерение то, чего ядро не сказало.
            notes.Add("min_points_unread — GetMinPoint1/2 не отдали точки отрезка минимального " +
                      "расстояния: у этой пары ближайшие точки не единственны (или отрезок " +
                      "вырожден). Координаты не подставляются");
        }

        if (angleValid == false)
        {
            notes.Add("angle_not_applicable — IsAngleValid = FALSE: угол для этой пары смысла не имеет");
        }

        // MEASURED: the help does not name the unit of Lmin or of the segment points, so the unit is
        // not read out of it but measured against a geometry whose size is known analytically: the
        // number matches millimetres and does NOT match the same distance in metres or centimetres.
        // TEST: acceptance row INT.04.unit. History: docs/decisions/assembly.md#interference-contracts
        return new MeasureGapResult(
            measureName,
            minDistance,
            minPoints,
            angleValid == true ? angle : null,
            angleValid,
            "измерено: миллиметры (строка приёмки INT.04.unit — прочитанное расстояние совпало с " +
            "аналитикой в миллиметрах на трёх конфигурациях и не совпало с переводом в метры или " +
            "сантиметры)",
            GapRoute,
            document.Revision,
            notes);
    }

    /// <summary>The API7 measurement service of the assembly's top part.</summary>
    /// <remarks>MEASURED: the shipped <c>Interop.KompasAPI7.dll</c> does not declare
    /// <c>IPart7.Measurement3D</c> while the installed type library and <c>Bin\kAPI7.DLL</c> do, so the
    /// service is reached through the typed twin (<see cref="IPart7Twin"/>).
    /// INVARIANT: the twin is called only after the application version is at or above the documented
    /// threshold: it carries the SAME IID as the shipped <c>IPart7</c>, so the cast succeeds wherever
    /// <c>IPart7</c> exists, including a version whose slot 116 is another member. No direct QI is made:
    /// the help obtains it as a PROPERTY of <c>IPart7</c>. History: docs/decisions/assembly.md#api7-twin</remarks>
    private IMeasurement3DTwin Measurement3D(DocumentEntry document, List<string> notes)
    {
        var top = TopPart7(document, notes);
        if (top is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Верхняя деталь сборки (IKompasDocument3D.TopPart) не получена как IPart7: " +
                "измеритель недостижим. Причина названа в notes.",
                RetryPolicy.ReacquireContext);
        }

        // DOC: ipart7_measurement3d.html / imeasurement3d.html — «Версия Компас v23». Below the
        // threshold the property does not exist, and "not read" refuses exactly like "too old".
        var version = RequireApplication(document.ApplicationId).Version;
        if (!InterferenceRules.Measurement3DAvailable(version))
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Зазор требует КОМПАС v{InterferenceRules.Measurement3DMinimumMajorVersion} и новее: " +
                $"IPart7.Measurement3D объявлен справкой начиная с этой версии, а подключён " +
                $"v«{version}». Ниже порога инструмент отказывает, а не вызывает слот, которого " +
                "на этой версии нет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["application_version"] = version,
                    ["minimum_major_version"] = InterferenceRules.Measurement3DMinimumMajorVersion,
                });
        }

        if ((object)top is not IPart7Twin twin)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Верхняя деталь сборки не отвечает на IPart7 установленной библиотеки типов: " +
                "IPart7.Measurement3D недостижим.",
                RetryPolicy.ReacquireContext);
        }

        IMeasurement3DTwin? measurement;
        try
        {
            measurement = twin.get_Measurement3D();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"IPart7.Measurement3D прервался: {ex.GetType().Name}: {ex.Message}. Измеритель " +
                "расстояния и угла не получен.",
                RetryPolicy.ReacquireContext);
        }

        if (measurement is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.Measurement3D вернул null: измеритель расстояния и угла не получен.",
                RetryPolicy.ReacquireContext);
        }

        notes.Add($"measurement3d_route — IPart7.Measurement3D (типизированный двойник установленного " +
                  $"TLB), версия приложения {version} не ниже порога " +
                  $"{InterferenceRules.Measurement3DMinimumMajorVersion}");
        return measurement;
    }

    /// <summary>The object to measure: the component itself, or one of its faces.</summary>
    private IKompasAPIObject ResolveMeasureObject(
        DocumentEntry document, MeasureObjectDto dto, string label, List<string> notes)
    {
        var stored = References.Require(dto.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{dto.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        if (dto.FaceIndex is int faceIndex)
        {
            // The face route is the one the mate block already measured: FaceObject7 checks the
            // ordinal's identity, the face range and the API7 transfer, and names each refusal.
            var handle = FaceObject7(document, dto.ComponentRef, faceIndex, notes);
            notes.Add($"{label} — грань {faceIndex} компонента «{dto.ComponentRef}»");
            return (IKompasAPIObject)(object)handle.Model;
        }

        var part5 = ComponentPart5At(document, payload.Ordinal);
        if (part5 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Компонент по порядковому номеру {payload.Ordinal} не получен как ksPart: измерять нечего.",
                RetryPolicy.ReacquireContext);
        }

        if (BodiesOf(part5).Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.NoBody,
                $"У компонента «{dto.ComponentRef}» не прочитано ни одного тела " +
                "(ksPart.BodyCollection): измерять нечего.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["component_ref"] = dto.ComponentRef });
        }

        notes.Add($"{label} — компонент «{dto.ComponentRef}» целиком");
        return (IKompasAPIObject)(object)payload.Part7;
    }

    // ── helpers ──
    /// <summary>The assembly document, refused with <c>WRONG_DOCUMENT_KIND</c>.</summary>
    /// <remarks>The C1 block answers <c>INVALID_ARGUMENT</c> for the same situation; the G1 order
    /// names the code explicitly, so the two differ and the difference is deliberate, not drift.
    /// History: docs/decisions/contracts.md#wrong-document-kind</remarks>
    private DocumentEntry RequireAssemblyForInterference(string documentId)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{documentId}» имеет тип {document.Kind}: проверка пересечений и зазоров " +
                "применима только к документу-сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                });
        }

        return document;
    }

    private static List<ksBody> BodiesOf(ksPart? part)
    {
        var bodies = new List<ksBody>();
        try
        {
            if (part?.BodyCollection() is not ksBodyCollection collection)
            {
                return bodies;
            }

            var count = collection.GetCount();
            for (var index = 0; index < count; index++)
            {
                if (collection.GetByIndex(index) is ksBody body)
                {
                    bodies.Add(body);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return bodies;
        }

        return bodies;
    }

    private static double? TryDouble(Func<double> read)
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

    private static bool? TryBool(Func<bool> read)
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

    private static IReadOnlyList<double>? TryPoint(MinPointReader read)
    {
        double x = 0, y = 0, z = 0;
        try
        {
            return read(ref x, ref y, ref z) ? new[] { x, y, z } : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Both ends of the minimum-distance segment through the pure rule.</summary>
    private static IReadOnlyList<IReadOnlyList<double>>? TryPoints(MinPointReader first, MinPointReader second)
    {
        var a = TryPoint(first);
        var b = TryPoint(second);
        return InterferenceRules.Segment(a is not null, a ?? Array.Empty<double>(),
            b is not null, b ?? Array.Empty<double>());
    }

    /// <summary>The shape of <c>IMeasurement3D.GetMinPoint1/2</c> (two identical members).</summary>
    private delegate bool MinPointReader(ref double x, ref double y, ref double z);
}
