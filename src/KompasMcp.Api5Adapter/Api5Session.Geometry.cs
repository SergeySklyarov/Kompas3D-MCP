using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Geometry: sketches, features, measurement, final topology.</summary>
/// <remarks>Signatures come from <c>docs/compatibility/kompas-api5-metadata.json</c> (P0.2), not memory.
/// MEASURED: unit selector 0 = centimetres (P0.7); only <see cref="KompasUnits.LengthMm"/> is passed here.
/// Volume/area come from <c>CalcMassInertiaProperties</c> (<c>.v()</c>, <c>.F()</c>). Edges come through
/// <c>GetMainBody() → FaceCollection → EdgeCollection</c>, never <c>EntityCollection(o3d_edge)</c> (P0.8).
/// History: docs/decisions/adapter-core.md#geometry-signatures</remarks>
public sealed partial class Api5Session
{
    // ---------------------------------------------------------------------------------------------
    // Sketches
    // ---------------------------------------------------------------------------------------------

    public ReferenceDto CreateSketch(CreateSketchCommand command)
    {
        var document = RequireDocument(command.DocumentId);

        // ONE support rule for both tools. This call used to resolve the reference on its own, take it
        // while IGNORING `base`, and remember `base` as the sketch's frame — an accepted-and-ignored
        // field that reached acceptance looking like a sketch on the wrong plane. The shared rule is
        // ResolveSupportPlane (Api5Session.SketchPlane.cs).
        // History: docs/decisions/adapter-sketch.md#sketch-plane-reference
        var support = ResolveSupportPlane(document, command.Plane, new List<string>());

        var sketch = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(KompasObjectTypes.Sketch));
        sketch.name = command.Name ?? $"sketch_{document.Revision}";
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        if (!definition.SetPlane(support.Entity))
        {
            ComApartment.Release(sketch);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetPlane для эскиза не принят.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        if (!sketch.Create())
        {
            ComApartment.Release(sketch);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Эскиз не создан: Entity.Create() вернул false.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        var reference = ReferenceForObject("sketch", document, sketch);

        // A sketch created here is empty by construction, so replace and delete_entities are meaningful
        // from the start; a sketch this server never drew is refused instead of quietly appending.
        _sketchProbePoints[reference.Id] = new List<double[]>();
        _sketchProfileBox.Remove(reference.Id);
        _sketchProfiles.Remove(reference.Id);

        // ONLY a named base plane is remembered. A reference support must NOT be: the frame of an offset
        // or tilted plane is not one of the three standard frames, so a remembered XY would send the
        // coordinate derivation of kompas_edit_sketch along the wrong axis. For a reference the frame is
        // read back from the model instead (ResolveSketchPlaneBase).
        if (support.BasePlane is PlaneBase basePlane)
        {
            _sketchPlaneBase[reference.Id] = basePlane;
        }

        // The plane as it was DECLARED is kept for one purpose: a refused extrusion has to say where the
        // profile lay. It is the same string the create answer carries, so the two records cannot drift.
        _sketchPlaneHint[reference.Id] = PlaneHint(command.Plane, support.Entity);

        return ToDto(reference, PlaneHint(command.Plane, support.Entity));
    }

    /// <summary>The reference to hand out for a model object: the one ALREADY live for this same
    /// object, or a newly minted one.</summary>
    /// <remarks>INVARIANT: one live reference per object per revision. A read tool must not mint a second
    /// address for an object the session already addressed: the client then holds two strings for one object,
    /// and state remembered under one looks absent from the other — how a just-drawn profile read as
    /// "not recorded". Identity is the COM object's own (<see cref="ComIdentity"/>).
    /// <para>INVARIANT: an object whose identity cannot be stated gets a new reference, never a shared one.</para>
    /// History: docs/decisions/adapter-sketch.md#one-live-reference</remarks>
    private StoredReference ReferenceForObject(string kind, DocumentEntry document, object payload)
    {
        var identity = ComIdentity.Of(payload);
        if (identity is not null
            && References.FindLiveByIdentity(kind, document.Id, document.Revision, identity) is StoredReference live)
        {
            return live;
        }

        return References.Register(kind, document.Id, document.Revision, payload, identity: identity);
    }

    private ksEntity ResolvePlaneEntity(DocumentEntry document, PlaneRefDto plane)
    {
        if (plane.Reference is not null)
        {
            var stored = References.Require(plane.Reference, document.Id, document.Revision);
            if (stored.Payload is ksEntity entity)
            {
                return entity;
            }

            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{plane.Reference}' не указывает на объект, годный как носитель эскиза.",
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var baseType = plane.Base switch
        {
            PlaneBase.Xy => KompasObjectTypes.PlaneXoy,
            PlaneBase.Xz => KompasObjectTypes.PlaneXoz,
            PlaneBase.Yz => KompasObjectTypes.PlaneYoz,
            _ => throw new KompasContractException(ErrorCodes.InvalidArgument, "Не указана базовая плоскость эскиза."),
        };

        if (Math.Abs(plane.OffsetMm) < 1e-9)
        {
            return (ksEntity)document.PartNow().GetDefaultEntity(KompasObjectTypes.Of(baseType));
        }

        var offsetPlane = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(KompasObjectTypes.PlaneOffset));
        var definition = (ksPlaneOffsetDefinition)offsetPlane.GetDefinition();
        definition.SetPlane((ksEntity)document.PartNow().GetDefaultEntity(KompasObjectTypes.Of(baseType)));

        // DOC: <c>ksplaneoffsetdefinition_props.html</c>, «смещение вдоль нормали базовой плоскости».
        // MEASURED: `direction=true` means offset along the plane's own normal for XY, XZ and YZ alike.
        // History: docs/decisions/adapter-core.md#plane-offset-sign
        definition.offset = Math.Abs(plane.OffsetMm);
        definition.direction = plane.OffsetMm >= 0;

        if (!offsetPlane.Create())
        {
            ComApartment.Release(offsetPlane);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Смещённая плоскость на {plane.OffsetMm} мм не создана.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        References.Register("plane", document.Id, document.Revision, offsetPlane);
        return offsetPlane;
    }

    /// <summary>What the client is told about the support it got: for a reference, the KIND and the model
    /// TYPE of the object the reference resolved to, read from the model rather than retold from the
    /// request.</summary>
    /// <remarks>MEASURED: the hint used to read «sketch on <c>reference</c>» and nothing else — it named
    /// neither what the reference became nor whether a plane was behind it at all.
    /// History: docs/decisions/adapter-sketch.md#sketch-plane-reference</remarks>
    private static string PlaneHint(PlaneRefDto plane, ksEntity entity) =>
        plane.Reference is not null
            ? $"sketch on {plane.Reference} (вид «plane», тип {entity.type} («{entity.name}») — прочитано из модели)"
            : $"{plane.Base?.ToString().ToUpperInvariant() ?? "plane"}{(Math.Abs(plane.OffsetMm) > 1e-9 ? $" +{plane.OffsetMm:0.###} mm" : string.Empty)}";

    /// <summary>The contours this server drew into each sketch, in mm — the expected volume target read by
    /// <see cref="Extrude"/>, without which the extrusion could only report "KOMPAS said yes" (spec 1.11).</summary>
    /// <remarks>The contour list is kept rather than the area, because a profile's area is not the sum of its
    /// primitives — a contour inside another is a hole, and one number cannot carry nesting.
    /// History: docs/decisions/adapter-core.md#sketch-profiles-contour-list</remarks>
    private readonly Dictionary<string, SketchProfile> _sketchProfiles = new(StringComparer.Ordinal);

    /// <summary>Points lying on the primitives this server drew into each sketch. API5 gives no way to
    /// enumerate sketch objects (no ksFirstObj/ksGetObjCount/GetSegmentContainer in this interop), so a
    /// remembered coordinate is the only handle that makes replace and delete_entities real.</summary>
    private readonly Dictionary<string, List<double[]>> _sketchProbePoints = new(StringComparer.Ordinal);

    /// <summary>Extent of everything this server drew into each sketch, in sketch coordinates. It lets a
    /// declared target body be checked against the contour before anything is mutated
    /// (<see cref="TargetBodyGuard.ProfileMayAffectBody"/>) — KOMPAS offers no way to ask a sketch where
    /// its profile lies.</summary>
    /// <remarks>Kept alongside <see cref="_sketchProfiles"/> with the same rule: an unknown shape or an
    /// incompletely cleared sketch drops the entry rather than leaving a stale extent behind, because a
    /// wrong box is worse than no box.</remarks>
    private readonly Dictionary<string, ProfileBox> _sketchProfileBox = new(StringComparer.Ordinal);

    /// <summary>Which base plane each sketch sits on, when the server was the one that chose it. A sketch
    /// built on a referenced plane has no entry: the mapping from sketch axes to model axes for
    /// that case is not measured, so the target check reports "not checked" instead of assuming it.</summary>
    private readonly Dictionary<string, PlaneBase> _sketchPlaneBase = new(StringComparer.Ordinal);

    /// <summary>The support plane as it was DECLARED when the sketch was created, in the same wording the
    /// create answer carries. Kept for one purpose: a refused extrusion has to say where the profile lay,
    /// and the declared offset is not readable back from the model.</summary>
    private readonly Dictionary<string, string> _sketchPlaneHint = new(StringComparer.Ordinal);

    public EditSketchResult EditSketch(EditSketchCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var document = target.Document;

        // Per-body volumes before anything is touched, for the same reason Extrude takes them: the
        // vanishing-body case (probe G.9) is invisible in the return codes of every call involved.
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        // Validation of the whole batch happens before BeginEdit: entering edit mode and failing
        // halfway would leave the sketch in edit state with a partially applied profile.
        foreach (var entity in command.Entities)
        {
            SketchValidation.Validate(entity);
        }

        // Clearing a sketch is possible only through ksFindObj(x, y, limit) → ksDeleteObj(ref): the 2D API
        // exposes no enumeration at all (ksFirstObj, ksGetObjCount, GetSegmentContainer, SketchEntities are
        // absent), so the only way to point at an object is a coordinate that lies on it. The server
        // remembers probe points for what it drew; for a sketch it did not draw the coordinate is derived
        // from the model, allowed only inside the configuration probe G covered.
        var knownPoints = _sketchProbePoints.TryGetValue(command.SketchRef, out var remembered)
            ? remembered
            : null;
        string? derivedFrom = null;
        if (command.Mode != SketchEditMode.Append && knownPoints is null)
        {
            knownPoints = DeriveProbePointsFromModel(command, target, document, out derivedFrom);
        }

        // BeginEdit is declared to return object. A hard cast would throw InvalidCastException, escaping as
        // an unclassified failure; naming the runtime type turns "it broke" into "the API handed back X".
        // BeginEdit is called exactly once — a second call would re-enter edit mode.
        var editing = target.Definition.BeginEdit();
        if (editing is not ksDocument2D editor)
        {
            SafeEndSketchEdit(target.Definition);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"BeginEdit вернул {editing?.GetType().Name ?? "null"} вместо ksDocument2D: редактирование эскиза невозможно.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var deleted = 0;
        var deleteAttempts = 0;
        var cleared = true;
        if (command.Mode != SketchEditMode.Append && knownPoints is not null)
        {
            // Vendor convention, measured in P2.6: for these 2D calls 1 means success and 0 means
            // "not found / not deleted" — the opposite of a C# bool. One object is hit by several probe
            // points (a circle answers at both ends of a diameter), and a second ksDeleteObj on the same
            // ref fails by definition — so what is counted is distinct objects found by ksFindObj.
            var foundRefs = new HashSet<int>();
            foreach (var point in knownPoints)
            {
                var found = editor.ksFindObj(point[0], point[1], 1e-3);
                if (found != 0)
                {
                    foundRefs.Add(found);
                }
            }

            deleteAttempts = foundRefs.Count;
            foreach (var found in foundRefs)
            {
                deleted += editor.ksDeleteObj(found) == 1 ? 1 : 0;
            }

            cleared = deleted == deleteAttempts;
        }

        if (command.Mode == SketchEditMode.DeleteEntities)
        {
            // Nothing to draw: close the session and report what actually disappeared.
            var endedDelete = SafeEndSketchEdit(target.Definition);
            if (!endedDelete)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "EndEdit не подтверждён после удаления: эскиз может остаться в режиме правки.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }

            BumpRevision(document, "sketch.delete");
            _sketchProfiles.Remove(command.SketchRef);

            // Emptying a sketch removes the profile the dependent feature was built on, so the same
            // vanishing-body question applies here as on replace.
            GuardDependentBodySurvived(document, command, bodiesBefore);

            // Whatever survived a partial clear has an extent the server cannot state, so the entry
            // goes away either way: an empty sketch has no profile to check, and a dirty one has an
            // unknown one.
            _sketchProfileBox.Remove(command.SketchRef);
            if (cleared)
            {
                _sketchProbePoints[command.SketchRef] = new List<double[]>();
            }

            return new EditSketchResult(0, Array.Empty<string>(),
                EditClosedOut: true, ProfileAreaMm2: null,
                DeletedEntities: deleted, ExpectedDeleted: deleteAttempts,
                ProbePointsFromModel: derivedFrom is not null);
        }

        var kinds = new List<string>();
        // The native polyline and the spline are built from PARAMETER BLOCKS, and the factory for those
        // is the application object, not the sketch editor: the block is fetched here once per call.
        var application5 = RequireApplication(document.ApplicationId).Application;
        Exception? drawn = null;
        foreach (var entity in command.Entities)
        {
            try
            {
                kinds.Add(DrawSketchEntity(editor, application5, entity));
            }
            catch (Exception ex) when (ex is COMException or KompasContractException)
            {
                drawn = ex;
                break;
            }
        }

        var ended = SafeEndSketchEdit(target.Definition);
        if (drawn is not null)
        {
            BumpRevision(document, "sketch.edit.failed");
            throw drawn;
        }

        if (!ended)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "EndEdit не подтверждён: изменения эскиза могли остаться недоприменёнными.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        BumpRevision(document, "sketch.edit");

        // INVARIANT: the calls' answers are not evidence — probe G.9 saw every one succeed while the
        // dependent body vanished entirely. Losing the body is a refusal, not a geometry_checked result.
        // History: docs/decisions/adapter-core.md#guard-dependent-body-survived
        if (command.Mode == SketchEditMode.Replace || command.Mode == SketchEditMode.DeleteEntities)
        {
            GuardDependentBodySurvived(document, command, bodiesBefore);
        }

        // The remembered profile is the LIST of contours drawn so far; its area is derived from them, not a
        // running sum, which cannot express that one contour is a hole in another. Only a PARTIALLY cleared
        // shape drops the entry: there the stored list no longer describes the model. An entry whose area is
        // merely not computable is KEPT, because the contour list is what lets the extrusion name WHY the
        // expectation is missing — dropping it made a drawn profile report "never recorded".
        // History: docs/decisions/adapter-core.md#sketch-profiles-contour-list
        var freshPoints = ProbePointsOf(command.Entities);
        var partialClear = command.Mode == SketchEditMode.Replace && !cleared;
        if (partialClear)
        {
            _sketchProfiles.Remove(command.SketchRef);
        }
        else
        {
            if (!_sketchProfiles.TryGetValue(command.SketchRef, out var profile))
            {
                profile = new SketchProfile();
                _sketchProfiles[command.SketchRef] = profile;
            }

            if (command.Mode == SketchEditMode.Replace)
            {
                profile.Replace(command.Entities);
            }
            else
            {
                profile.Append(command.Entities);
            }
        }

        // What the caller is told is the area of the PROFILE, not of this call's batch: it is the
        // figure the extrusion will use as its expectation, and for a second append the two differ.
        // The reason travels WITH the missing figure: a bare null sends the caller back to guessing,
        // and the server has already computed why (see ProfileArea).
        double? profileAreaMm2 = null;
        string? profileAreaUnavailable = null;
        if (!partialClear && _sketchProfiles.TryGetValue(command.SketchRef, out var drawnProfile))
        {
            var outcome = drawnProfile.Outcome;
            profileAreaMm2 = outcome.AreaMm2;
            profileAreaUnavailable = outcome.UnavailableReason;
        }

        // The same bookkeeping for the profile's extent, with the same rule about a partial clear: an extent
        // is only useful for refusing a contradictory target when the contour inside it is the whole
        // contour. An unknown figure is reported as unknown, costing only the pre-check.
        var footprint = ProfileBox.Of(command.Entities);
        if (footprint is null || partialClear)
        {
            _sketchProfileBox.Remove(command.SketchRef);
        }
        else if (command.Mode == SketchEditMode.Replace)
        {
            _sketchProfileBox[command.SketchRef] = footprint.Value;
        }
        else
        {
            _sketchProfileBox[command.SketchRef] = ProfileBox.Union(
                _sketchProfileBox.TryGetValue(command.SketchRef, out var previousBox) ? previousBox : null,
                footprint) ?? footprint.Value;
        }

        if (command.Mode == SketchEditMode.Replace)
        {
            _sketchProbePoints[command.SketchRef] = freshPoints;
        }
        else if (_sketchProbePoints.TryGetValue(command.SketchRef, out var existing))
        {
            existing.AddRange(freshPoints);
        }
        else
        {
            _sketchProbePoints[command.SketchRef] = freshPoints;
        }

        return new EditSketchResult(kinds.Count, kinds,
            EditClosedOut: ended,
            ProfileAreaMm2: profileAreaMm2,
            DeletedEntities: deleted,
            ExpectedDeleted: deleteAttempts,
            ProbePointsFromModel: derivedFrom is not null,
            ProfileAreaUnavailable: profileAreaUnavailable);
    }

    /// <summary>Which base plane a sketch sits on: from memory when the server chose it, otherwise read back
    /// out of the model.</summary>
    /// <remarks>A reopened document's sketch has no memory entry, so without this the derivation would be
    /// useless exactly where it was built. An axis has no sign: this establishes "parallel to XY", not
    /// "faces +Z" (probe G left the sign unmeasured).
    /// History: docs/decisions/adapter-core.md#resolve-sketch-plane-base</remarks>
    private PlaneBase? ResolveSketchPlaneBase(string sketchRef, SketchTarget target)
    {
        if (_sketchPlaneBase.TryGetValue(sketchRef, out var remembered))
        {
            return remembered;
        }

        try
        {
            if (target.Definition.GetPlane() is not ksEntity plane)
            {
                return null;
            }

            return PlaneNormalAxis(plane) switch
            {
                2 => PlaneBase.Xy,
                1 => PlaneBase.Xz,
                0 => PlaneBase.Yz,
                _ => null,
            };
        }
        catch (COMException)
        {
            // An unreadable plane stays unknown, and the caller refuses with that reason rather
            // than assuming the most convenient one.
            return null;
        }
    }

    /// <summary>Refuses a sketch edit that destroyed a dependent body, instead of reporting it as applied.</summary>
    /// <remarks>Closes probe G's question Q-SKETCH-EDIT-ZERO. Measured in G.9: the vendor's return codes
    /// verify nothing — KOMPAS answers success to every call while the body disappears. Reported as
    /// <see cref="ErrorCodes.GeometryFailed"/> with <c>partialEffects: true</c>: the model really changed,
    /// and a general rollback is not claimed.
    /// History: docs/decisions/adapter-core.md#guard-dependent-body-survived</remarks>
    private void GuardDependentBodySurvived(DocumentEntry document, EditSketchCommand command, List<BodySnapshot> bodiesBefore)
    {
        var bodiesAfter = ReadBodySnapshots(document.PartNow());
        var volumeBefore = bodiesBefore.Sum(body => body.Volume ?? 0d);
        var volumeAfter = bodiesAfter.Sum(body => body.Volume ?? 0d);
        var presentBefore = bodiesBefore.Count(body => body.Volume is > 0d);

        // Only a body that existed and was measurable before can be "lost". A part with no solid
        // body yet (a sketch awaiting its first extrusion) has nothing to lose and is not refused.
        if (presentBefore == 0)
        {
            return;
        }

        var solidAfter = bodiesAfter.Count(body => body.Volume is > 0d);
        if (solidAfter > 0 && volumeAfter > 0d)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"Правка эскиза уничтожила зависимое тело: объём {volumeBefore:0.####} → {volumeAfter:0.####} мм³, " +
            $"тел с объёмом {presentBefore} → {solidAfter}. Профиль, вероятно, стал больше сечения, и КОМПАС " +
            "принял это без единого отказа — измерено пробой G.9. Ответы вызовов правки доказательством не " +
            "являются, поэтому успех не выдаётся: изменение на модели уже произошло, вернуть прежнее тело " +
            "адаптер не может.",
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["mode"] = command.Mode.ToString().ToLowerInvariant(),
                ["volume_before_mm3"] = volumeBefore,
                ["volume_after_mm3"] = volumeAfter,
                ["bodies_with_volume_before"] = presentBefore,
                ["bodies_with_volume_after"] = solidAfter,
                ["code"] = "dependent_body_destroyed",
            });
    }

    /// <summary>Coordinates for <c>ksFindObj</c> taken from the model itself, for a sketch this server did
    /// not draw and therefore cannot remember.</summary>
    /// <remarks>Route measured by probe G on a reopened document: the through cut is found by type, its
    /// sketch via <c>GetSketch()</c>, and the coordinate from the cylindrical face it left —
    /// <c>GetSurfaceParam() → ksCylinderParam</c> gives centre and radius, and <c>(cx + r; cy)</c> lies on
    /// the sketch circle. Guarded by <see cref="SketchPointDerivation.Verdict"/>: only a base-XY sketch with
    /// a circular profile qualifies.
    /// History: docs/decisions/adapter-core.md#derive-probe-points-from-model</remarks>
    private List<double[]> DeriveProbePointsFromModel(
        EditSketchCommand command,
        SketchTarget target,
        DocumentEntry document,
        out string derivation)
    {
        var planeBase = ResolveSketchPlaneBase(command.SketchRef, target);
        var verdict = SketchPointDerivation.Verdict(
            planeBase,
            command.Entities.Select(entity => entity.Kind).ToList());
        if (!verdict.IsAllowed)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"{command.Mode} невозможен для этого эскиза: в API5 нет перечисления объектов эскиза, " +
                $"удаление идёт найденным по координате объектом, а вывести координату из модели нельзя — " +
                $"{verdict.Reason}",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode.ToString().ToLowerInvariant(),
                    ["derivation"] = verdict.ReasonCode,
                    ["measured_scope"] = "эскиз на основной XY, в профиле окружность, вырезание сквозное",
                });
        }

        // The feature is located by the dependent body rather than by name: probe F measured that a name
        // does not survive the API5↔API7 transition, so an identity on it would break on re-open.
        var part = document.PartNow();
        double[]? center = null;
        double radius = 0d;
        var candidates = 0;
        if (part.EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement)) is ksEntityCollection collection)
        {
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                var sketch = entity.GetDefinition() switch
                {
                    ksBaseExtrusionDefinition b => b.GetSketch() as ksEntity,
                    ksBossExtrusionDefinition s => s.GetSketch() as ksEntity,
                    ksCutExtrusionDefinition c => c.GetSketch() as ksEntity,
                    _ => null,
                };

                // Only the sketch being edited is of interest. Compared by IUnknown, not by name: the same
                // COM object can arrive as a different RCW.
                if (sketch is null || !SameComObject(sketch, target.Sketch))
                {
                    continue;
                }

                if (FaceOfSketchHole(part, out var faceCenter, out var faceRadius, out var faceAxis))
                {
                    center = faceCenter;
                    radius = faceRadius;
                    candidates++;
                }

                _ = faceAxis;
            }
        }

        if (center is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"{command.Mode} невозможен: координата для поиска объекта эскиза выводится из цилиндрической " +
                "грани зависимого тела, а такой грани у тела нет. Памяти о рисовании эскиза тоже нет — " +
                "эскиз создан не этим сервером (например, документ переоткрыт).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode.ToString().ToLowerInvariant(),
                    ["derivation"] = "no_cylindrical_face",
                });
        }

        if (candidates > 1)
        {
            // Two cylinders of the same radius would make the choice between them arbitrary, and
            // deleting the wrong one is worse than refusing.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Координата неоднозначна: подходящих цилиндрических граней {candidates}. " +
                "Выбор одной из них был бы догадкой, а удаление чужого объекта необратимо на модели.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode.ToString().ToLowerInvariant(),
                    ["derivation"] = "ambiguous_face",
                    ["candidates"] = candidates,
                });
        }

        derivation = "cylinder_face_of_dependent_body";
        return SketchPointDerivation.PointsOnCircle(center, radius).ToList();
    }

    /// <summary>Centre, radius and axis of the hole wall left by the feature that consumes a sketch.</summary>
    /// <remarks>Returns the first cylindrical face whose axis is normal to the XY plane — the configuration
    /// probe G measured. The geometry is confirmed afterwards by measuring the dependent body, the only
    /// evidence the probe found trustworthy (G.9: every call answered success while the body vanished).</remarks>
    private static bool FaceOfSketchHole(
        ksPart part,
        out double[]? center,
        out double radiusMm,
        out double[]? axis)
    {
        center = null;
        radiusMm = 0d;
        axis = null;
        try
        {
            var body = AsInterface<ksBody>(part.GetMainBody());
            if (body is null || body.FaceCollection() is not ksFaceCollection faces)
            {
                return false;
            }

            for (var i = 0; i < faces.GetCount(); i++)
            {
                if (AsInterface<ksFaceDefinition>(faces.GetByIndex(i)) is not ksFaceDefinition face)
                {
                    continue;
                }

                var (radius, _, origin, direction) = CylinderGeometry(face);
                if (radius is not double r || origin is not { Length: >= 2 }
                    || !SketchPointDerivation.AxisIsNormalToXyPlane(direction))
                {
                    continue;
                }

                center = new[] { origin[0], origin[1] };
                radiusMm = r;
                axis = direction;
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No face readable means no derived point, and the caller refuses with that reason.
        }

        return false;
    }

    /// <summary>Whether two COM wrappers point at the same object. Used instead of a name comparison because
    /// probe F measured that names do not survive the API5↔API7 transition (a feature named "f-ch2" answers
    /// "Chamfer:1" through API7), so any identity built on a name is a latent defect.</summary>
    private static bool SameComObject(object left, object right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        var leftPointer = IntPtr.Zero;
        var rightPointer = IntPtr.Zero;
        try
        {
            leftPointer = Marshal.GetIUnknownForObject(left);
            rightPointer = Marshal.GetIUnknownForObject(right);
            return leftPointer == rightPointer;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (leftPointer != IntPtr.Zero)
            {
                Marshal.Release(leftPointer);
            }

            if (rightPointer != IntPtr.Zero)
            {
                Marshal.Release(rightPointer);
            }
        }
    }

    /// <summary>Points that provably lie on the drawn primitives — what ksFindObj needs to find them again.
    /// Midpoints of rectangle sides and of a segment, and a point on the circumference for a circle (its
    /// centre lies on nothing). Anything without such a point contributes none.</summary>
    private static List<double[]> ProbePointsOf(IEnumerable<SketchEntityDto> entities)
    {
        var points = new List<double[]>();
        foreach (var entity in entities)
        {
            switch (entity.Kind)
            {
                case SketchEntityKind.Line when entity.StartMm is { Count: 2 } start && entity.EndMm is { Count: 2 } end:
                    points.Add(new[] { (start[0] + end[0]) / 2d, (start[1] + end[1]) / 2d });
                    break;
                case SketchEntityKind.Circle when entity.CenterMm is { Count: 2 } center && entity.RadiusMm > 0:
                    points.Add(new[] { center[0] + entity.RadiusMm.Value, center[1] });
                    points.Add(new[] { center[0] - entity.RadiusMm.Value, center[1] });
                    break;
                case SketchEntityKind.Rectangle when entity.StartMm is { Count: 2 } corner:
                    var x0 = corner[0];
                    var y0 = corner[1];
                    var x1 = x0 + entity.WidthMm ?? 0d;
                    var y1 = y0 + entity.HeightMm ?? 0d;
                    points.Add(new[] { (x0 + x1) / 2d, y0 });
                    points.Add(new[] { (x0 + x1) / 2d, y1 });
                    points.Add(new[] { x0, (y0 + y1) / 2d });
                    points.Add(new[] { x1, (y0 + y1) / 2d });
                    break;
                case SketchEntityKind.Polyline when entity.PointsMm is { Count: >= 2 } path:
                    for (var i = 0; i + 1 < path.Count; i++)
                    {
                        points.Add(new[]
                        {
                            (path[i][0] + path[i + 1][0]) / 2d,
                            (path[i][1] + path[i + 1][1]) / 2d,
                        });
                    }
                    break;
            }
        }

        return points;
    }

    private static bool SafeEndSketchEdit(ksSketchDefinition definition)
    {
        try
        {
            return definition.EndEdit();
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static string DrawSketchEntity(ksDocument2D editor, KompasObject application, SketchEntityDto entity)
    {
        switch (entity.Kind)
        {
            case SketchEntityKind.Line:
                editor.ksLineSeg(Point(entity.StartMm, 0), Point(entity.StartMm, 1), Point(entity.EndMm, 0), Point(entity.EndMm, 1), LineStyle);
                return "line";

            case SketchEntityKind.Circle:
                editor.ksCircle(Point(entity.CenterMm, 0), Point(entity.CenterMm, 1), Required(entity.RadiusMm, "radius_mm"), LineStyle);
                return "circle";

            case SketchEntityKind.Arc:
                // DOC: <c>ksdocument2d_ksarcbypoint.html</c> — a second route takes the centre, the radius
                // and the two end POINTS with an explicit direction (1 CCW, −1 CW). It is offered so a
                // caller holding two points of a real contour need not convert them to angles.
                if (entity.StartPointMm is { Count: 2 } arcStart && entity.EndPointMm is { Count: 2 } arcEnd)
                {
                    editor.ksArcByPoint(
                        Point(entity.CenterMm, 0),
                        Point(entity.CenterMm, 1),
                        Required(entity.RadiusMm, "radius_mm"),
                        arcStart[0],
                        arcStart[1],
                        arcEnd[0],
                        arcEnd[1],
                        entity.Clockwise == true ? (short)-1 : (short)1,
                        LineStyle);
                    return "arc";
                }

                // INVARIANT: the end angle is start_deg + sweep_deg WITH its sign, so a NEGATIVE sweep goes
                // to the other side of start_deg. History: docs/decisions/adapter-core.md#arc-sign
                var sweep = Required(entity.SweepDeg, "sweep_deg");
                var start = Required(entity.StartDeg, "start_deg");
                // INVARIANT: the kernel takes TWO angles and REFUSES when the end leaves [−360°, 360°],
                // though the schema allows [−720, 720]; angles are shifted by whole turns, order kept.
                // History: docs/decisions/adapter-core.md#arc-sign
                var (first, second) = ArcEndpoints(start, sweep);
                // DOC: <c>ksdocument2d_ksarcbyangle.html</c> — f1/f2 are the START and END angle, and
                // `direction` is 1 (counter-clockwise) or -1 (clockwise). The sign of the sweep picks the
                // direction; the kernel is never handed the undocumented 0.
                // History: docs/decisions/adapter-core.md#arc-direction-documented
                editor.ksArcByAngle(
                    Point(entity.CenterMm, 0),
                    Point(entity.CenterMm, 1),
                    Required(entity.RadiusMm, "radius_mm"),
                    first,
                    second,
                    sweep >= 0 ? (short)1 : (short)-1,
                    LineStyle);
                return "arc";

            case SketchEntityKind.Rectangle:
                return DrawRectangle(editor, entity);

            case SketchEntityKind.Polyline:
                return DrawPolyline(editor, application, entity);

            case SketchEntityKind.Spline:
                return DrawSpline(editor, application, entity);

            default:
                throw new KompasContractException(ErrorCodes.InvalidArgument, $"Примитив {entity.Kind} не поддерживается.");
        }
    }

    private const int LineStyle = 1;

    private static string DrawRectangle(ksDocument2D editor, SketchEntityDto entity)
    {
        var x0 = Point(entity.StartMm ?? entity.CenterMm, 0);
        var y0 = Point(entity.StartMm ?? entity.CenterMm, 1);
        var x1 = x0 + Required(entity.WidthMm, "width_mm");
        var y1 = y0 + Required(entity.HeightMm, "height_mm");
        editor.ksLineSeg(x0, y0, x1, y0, LineStyle);
        editor.ksLineSeg(x1, y0, x1, y1, LineStyle);
        editor.ksLineSeg(x1, y1, x0, y1, LineStyle);
        editor.ksLineSeg(x0, y1, x0, y0, LineStyle);
        return "rectangle";
    }

    /// <summary>One native polyline object, not N segments.</summary>
    /// <remarks>
    /// DOC: <c>ksdocument2d_kspolylinebyparam.html</c> — <c>ksPolylineByParam(ksPolylineParam)</c>
    /// returns a pointer to the polyline, 0 on failure; <c>kspolylineparam_props.html</c> declares
    /// <c>closed</c> and <c>style</c>, and <c>kspolylineparam_methods.html</c> takes the vertices as a
    /// <c>POINT_ARR</c> dynamic array of <c>ksMathPointParam</c> («Типы динамических массивов»,
    /// <c>ksdmtypes.html</c>). The closure is therefore the block's own <c>closed</c> flag, NOT a
    /// repeated vertex: <c>ksdocument2d_kspolyline.html</c> has no closure parameter at all, and no page
    /// declares a repeated vertex to mean closure.
    /// INVARIANT: a kernel answer of 0 is a NAMED refusal, never a quiet fall back to segments — a
    /// fallback would make "drawn natively" indistinguishable from "drawn as before".
    /// History: docs/decisions/adapter-sketch.md#native-polyline
    /// </remarks>
    private static string DrawPolyline(ksDocument2D editor, KompasObject application, SketchEntityDto entity)
    {
        if (entity.PointsMm is not { Count: >= 2 } points)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "points_mm должен содержать минимум две точки.");
        }

        var param = application.GetParamStruct(KompasStructTypes.PolylineParam) as ksPolylineParam
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "KompasObject.GetParamStruct(ko_PolylineParam) не вернул ksPolylineParam: нативную "
                + "ломаную этим маршрутом построить нельзя, а раскладка на отрезки под её видом "
                + "запрещена.",
                RetryPolicy.Never);

        param.Init();

        var array = application.GetDynamicArray(KompasDynamicArrayTypes.PointArr) as ksDynamicArray
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "KompasObject.GetDynamicArray(POINT_ARR) не вернул ksDynamicArray: вершины ломаной "
                + "передать нечем.",
                RetryPolicy.Never);

        foreach (var vertex in points)
        {
            var node = application.GetParamStruct(KompasStructTypes.MathPointParam) as ksMathPointParam
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "KompasObject.GetParamStruct(ko_MathPointParam) не вернул ksMathPointParam: "
                    + "вершину ломаной задать нечем.",
                    RetryPolicy.Never);
            node.Init();
            node.x = vertex[0];
            node.y = vertex[1];
            // index -1 appends (ksdynamicarray_ksaddarrayitem.html).
            array.ksAddArrayItem(-1, node);
        }

        param.SetpMathPoint(array);
        param.closed = entity.Closed == true;
        param.style = LineStyle;

        var handle = editor.ksPolylineByParam(param);
        if (handle == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"ksPolylineByParam вернул 0: ломаная из {points.Count} вершин "
                + $"(замкнутость {entity.Closed == true}) не создана. Раскладки на отрезки под видом "
                + "нативной ломаной не делается: это был бы молчаливый откат.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["vertices"] = points.Count,
                    ["closed"] = entity.Closed == true,
                    ["route"] = "ksPolylineByParam(ksPolylineParam.closed)",
                });
        }

        return NativePolylineKind;
    }

    /// <summary>One native Bezier curve through the given vertices.</summary>
    /// <remarks>
    /// DOC: <c>ksdocument2d_ksbezier.html</c> — <c>ksBezier(closed, style)</c>, «Кривая Безье -
    /// составной объект»; nodes come from <c>ksBezierPoint</c> (<c>ksdocument2d_ksbezierpoint.html</c>,
    /// block <c>ksBezierPointParam</c>), and <c>ksEndObj</c> returns the pointer. The node block is
    /// initialised by its own documented <c>Init()</c> and only <c>x</c>, <c>y</c> are written, so the
    /// tangents are the kernel's own.
    /// WHY BEZIER AND NOT NURBS: the contract names the points «вершины» — the points the curve passes
    /// through — and MEASURED, only the Bezier route does that: <c>ksNurbs</c> treats the same nodes as
    /// POLES, so its closed curve lies inside them and misses the analytic circle by percent, not by
    /// the fraction of a percent the acceptance allows. The figures are in the decision note.
    /// INVARIANT: a 0 from either call is a NAMED refusal, never a silent substitution of the other route.
    /// History: docs/decisions/adapter-sketch.md#sketch-spline
    /// </remarks>
    private static string DrawSpline(ksDocument2D editor, KompasObject application, SketchEntityDto entity)
    {
        if (entity.PointsMm is not { Count: >= 2 } points)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "points_mm должен содержать минимум две вершины сплайна.");
        }

        var started = editor.ksBezier(entity.Closed == true ? (short)1 : (short)0, LineStyle);
        if (started == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"ksBezier(closed={entity.Closed == true}) вернул 0: описание кривой не начато.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["route"] = "ksBezier" });
        }

        foreach (var vertex in points)
        {
            var node = application.GetParamStruct(KompasStructTypes.BezierPointParam) as ksBezierPointParam
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "KompasObject.GetParamStruct(ko_BezierPointParam) не вернул ksBezierPointParam: "
                    + "узел кривой задать нечем.",
                    RetryPolicy.Never);
            node.Init();
            node.x = vertex[0];
            node.y = vertex[1];
            if (editor.ksBezierPoint(node) == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"ksBezierPoint отказал на вершине ({vertex[0]}, {vertex[1]}): описание кривой "
                    + "неполно.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true);
            }
        }

        var handle = editor.ksEndObj();
        if (handle == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"ksEndObj вернул 0 после {points.Count} узлов: сплайн не создан.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["vertices"] = points.Count,
                    ["route"] = "ksBezier → ksBezierPoint → ksEndObj",
                });
        }

        return "spline";
    }

    /// <summary>The kind string the edit answer uses for a polyline drawn as ONE native object.</summary>
    private const string NativePolylineKind = "polyline_native";

    public FinishSketchResult FinishSketch(FinishSketchCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        BumpRevision(target.Document, "sketch.finish");

        // INVARIANT: what the server read about the input is published AS the server's reading, never as
        // the kernel's confirmation — `profile_closed_confirmed` stays false, because ksSketchDefinition
        // exposes no profile or loop count in this interop version. A failed check is NOT a refusal: the
        // server does not reject the caller's contour, the extrusion does (the kernel is the judge).
        // History: docs/decisions/adapter-sketch.md#finish-sketch-input-check
        var (check, reason) = DescribeProfileInput(command.SketchRef);
        var warnings = new List<string>();
        if (check == SketchInputCheck.Failed && command.RequireClosedProfile)
        {
            // FIRST line, deliberately: a caller that reads one warning must read this one.
            warnings.Add(
                "Требование замкнутости не подтверждено анализом сервера: " + reason
                + " Это анализ ВХОДА сервером, а не ответ ядра — замкнутость ядром не подтверждалась, "
                + "и выдавливание всё равно покажет настоящий отказ.");
        }

        return new FinishSketchResult(
            ProfileClosedConfirmed: false,
            new[]
            {
                "profile_closedness_not_verified — замкнутость профиля проверяется только выдавливанием",
            },
            ProfileInputCheck: check,
            ProfileInputReason: reason,
            Warnings: warnings);
    }

    /// <summary>What the server's own reading of the sketch's accumulated profile says about the INPUT, and
    /// the reason in words when it is not consistent. INVARIANT: an unrecorded profile is
    /// <see cref="SketchInputCheck.NotAvailable"/> with the reason said out loud — the server never guesses
    /// about a contour it did not draw.
    /// History: docs/decisions/adapter-sketch.md#finish-sketch-input-check</summary>
    private (SketchInputCheck Check, string? Reason) DescribeProfileInput(string sketchRef)
    {
        if (!_sketchProfiles.TryGetValue(sketchRef, out var profile))
        {
            return (SketchInputCheck.NotAvailable,
                "профиль эскиза этим сеансом не записан — анализировать нечего: эскиз нарисован другим "
                + "клиентом или документ переоткрыт, а перечислять объекты эскиза API5 не умеет");
        }

        var outcome = profile.Outcome;
        return outcome.State switch
        {
            ProfileInputState.Consistent => (SketchInputCheck.Passed, null),
            ProfileInputState.Defect => (SketchInputCheck.Failed, outcome.UnavailableReason),
            _ => (SketchInputCheck.NotAvailable, outcome.UnavailableReason),
        };
    }

    private SketchTarget RequireSketch(string sketchRef)
    {
        if (!References.TryGet(sketchRef, out var stored) || stored is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Эскиз '{sketchRef}' не найден в реестре ссылок.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(stored.DocumentId);
        if (stored.Revision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Эскиз выпущен для ревизии {stored.Revision}, у документа уже {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["reference_revision"] = stored.Revision,
                    ["current_revision"] = document.Revision,
                });
        }

        if (stored.Payload is not ksEntity sketch)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{sketchRef}' указывает не на эскиз (kind={stored.Kind}).",
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "GetDefinition эскиза не вернул ksSketchDefinition.",
                RetryPolicy.ReacquireContext);
        }

        return new SketchTarget(document, sketch, definition);
    }

    private sealed record SketchTarget(DocumentEntry Document, ksEntity Sketch, ksSketchDefinition Definition);

    // ---------------------------------------------------------------------------------------------
    // Features
    // ---------------------------------------------------------------------------------------------

    public ExtrudeResult Extrude(ExtrudeCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var operationName = command.Operation.ToString().ToLowerInvariant();

        // The ordinal of this extrusion inside the session, taken before anything is touched: it is one of
        // the two numbers that tell a refusal early in a fresh session from one deep into a long run, and
        // it has to be the ordinal of the ATTEMPT, not of the successes.
        // History: docs/decisions/adapter-features.md#create-false-snapshot
        var ordinal = ++_extrudeOrdinal;

        // <c>base</c> creates the first body, so a target sent with it names nothing. The Host refuses the
        // pair before COM; repeated here because a hand-built IPC frame bypasses the Host's rule table, and
        // accepting the field only to ignore it would claim a target had been honoured.
        if (TargetBodyGuard.TargetBodyRefusedForOperation(operationName, command.TargetBodyRef is not null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Операция {operationName} не может указывать target_body_ref: базовое выдавливание создаёт первое тело, цели для выбора у него нет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["operation"] = operationName,
                    ["target_body_ref"] = command.TargetBodyRef,
                });
        }

        if (command.Operation != ExtrudeOperation.Base && command.TargetBodyRef is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Операция {command.Operation} обязана указывать target_body_ref — иначе неизвестно, к какому телу добавлять или из какого вырезать.",
                details: new Dictionary<string, object?> { ["operation"] = command.Operation.ToString() });
        }

        var entityType = command.Operation switch
        {
            ExtrudeOperation.Base => KompasObjectTypes.BaseExtrusion,
            ExtrudeOperation.Boss => KompasObjectTypes.BossExtrusion,
            ExtrudeOperation.Cut => KompasObjectTypes.CutExtrusion,
            _ => throw new KompasContractException(ErrorCodes.InvalidArgument, "Неизвестная операция выдавливания."),
        };

        // Per-body state is captured BEFORE anything is touched and again after the rebuild: probe P2.6
        // measured that a declaration contradicting the profile leaves SetSketch, Create and RebuildDocument
        // all answering true while no body changes volume. A return code proves nothing; only the snapshots do.
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);

        var bodyTarget = command.TargetBodyRef is null
            ? null
            : ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);

        // Necessary-not-sufficient: agreement of two bounding boxes is not geometric containment. A
        // disagreement is still worth refusing on, because it is exactly the configuration KOMPAS swallows.
        var agreement = TargetBodyGuard.ProfileMayAffectBody(
            _sketchProfileBox.TryGetValue(command.SketchRef, out var profileBox) ? profileBox : null,
            _sketchPlaneBase.TryGetValue(command.SketchRef, out var planeBase) ? planeBase : null,
            bodyTarget?.Snapshot.Min,
            bodyTarget?.Snapshot.Max);

        if (agreement == false && bodyTarget is not null)
        {
            var drawnBox = _sketchProfileBox.TryGetValue(command.SketchRef, out var drawn)
                ? drawn.ToString()
                : "<нет>";
            var targetBox = $"[{Range(bodyTarget.Snapshot.Min)}|{Range(bodyTarget.Snapshot.Max)}]";
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Заявленное тело не может быть тем, под которым лежит контур: габарит тела {bodyTarget.Index} " +
                $"{targetBox} не пересекается с областью нарисованного профиля {drawnBox} по осям плоскости эскиза. " +
                "Контур вне заявленного тела — это тот случай, когда КОМПАС возвращает Create=true и не меняет ни одного тела, " +
                "поэтому отказ сделан до мутации.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["target_body_ref"] = command.TargetBodyRef,
                    ["target_body_index"] = bodyTarget.Index,
                    ["target_body_gabarit"] = targetBox,
                    ["profile_box_sketch_mm"] = drawnBox,
                    ["code"] = "target_body_contradicts_profile",
                    ["check"] = "bbox_in_plane_axes_only — сверка габаритов по двум осям плоскости, а не вхождение контура в материал",
                });
        }

        var feature = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(entityType));
        var definition = feature.GetDefinition();

        // SetSideParam(side1, type, depth, draftValue, draftOutward): second arg is the end-condition type,
        // third is depth; directionType selects along/against/both. MEASURED: directionType 0 dtNormal -> +z,
        // 1 dtReverse -> -z, 2 dtBoth -> both sides. A direction away from the body is NOT an error — KOMPAS
        // creates the feature and changes nothing (Host: NO_GEOMETRY_CHANGE).
        // History: docs/decisions/adapter-core.md#extrude-direction-semantics
        var direction = command.Direction switch
        {
            ExtrudeDirection.Positive => (short)0,
            ExtrudeDirection.Negative => (short)1,
            ExtrudeDirection.Symmetric => (short)2,
            _ => (short)0,
        };

        var selector = new List<string>();
        var configured = definition switch
        {
            ksBaseExtrusionDefinition baseExtrusion => ConfigureBase(baseExtrusion, target.Sketch, direction, command, document),
            ksBossExtrusionDefinition boss => ConfigureBoss(boss, target.Sketch, direction, command, bodyTarget, selector, document),
            ksCutExtrusionDefinition cut => ConfigureCut(cut, target.Sketch, direction, command, bodyTarget, selector, document),
            _ => throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Определение выдавливания вернуло неожиданный интерфейс: {definition?.GetType().Name ?? "null"}.",
                RetryPolicy.ReacquireContext,
                partialEffects: true),
        };

        if (!configured)
        {
            ComApartment.Release(feature);
            throw Refused("configuration", command);
        }

        // The documented reason route is cleared BEFORE the call, so the code read after a refusal is this
        // call's own and not a stale one from an earlier operation.
        ClearKompasResult(document);
        if (!feature.Create())
        {
            var (resultCode, resultText, resultUnavailable) = ReadKompasResult(document);
            var snapshot = ObserveRefusedCreate(
                document, command, operationName, direction, ordinal, resultCode, resultText, resultUnavailable);
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Entity.Create() выдавливания вернул false: признак не появился. "
                + RefusalReason(resultCode, resultText, resultUnavailable)
                + " Состояние документа, эскиза и сеанса на момент отказа — в details."
                + FeatureCreateFailure.DetailKey + ".",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    [FeatureCreateFailure.DetailKey] = FeatureCreateFailure.Snapshot(snapshot),
                });
        }

        document.Document3D.RebuildDocument();
        BumpRevision(document, "extrude");

        var bodiesAfter = ReadBodySnapshots(document.PartNow());
        var bodyCount = bodiesAfter.Count;

        // The declaration survives only if a freshly obtained definition object reports it: reading it back
        // through the same RCW cannot distinguish "the document stored it" from "the wrapper kept my value".
        int? chooseTypeReadBack = null;
        if (bodyTarget is not null)
        {
            try
            {
                chooseTypeReadBack = definition switch
                {
                    ksCutExtrusionDefinition => (feature.GetDefinition() as ksCutExtrusionDefinition)?.chooseType,
                    ksBossExtrusionDefinition => (feature.GetDefinition() as ksBossExtrusionDefinition)?.chooseType,
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                chooseTypeReadBack = null;
            }
        }

        var changes = CompareBodySnapshots(bodiesBefore, bodiesAfter);

        // The feature is in the tree from here on, so a failure below can still NAME it: "the feature was
        // created but changed nothing" is only actionable if the caller can delete it in one call.
        var featureReference = References.Register("feature", document.Id, document.Revision, feature);

        // THREE DIFFERENT QUANTITIES that used to be one number (defect EXTRUDE-VOLUME-DELTA-ON-MULTIBODY):
        // (a) the SUM of all document bodies; (b) the volume of the body the operation concerns; (c) the
        // MATERIAL ADDED by this feature — the only quantity comparable with `profile_area × depth`. The sum
        // is also not the union.
        // History: docs/decisions/adapter-core.md#extrude-three-quantities
        var documentVolumeBefore = SumVolumesOrNull(bodiesBefore);
        var documentVolumeAfter = SumVolumesOrNull(bodiesAfter);

        // Which body the numbers are about: the declared target when there is one. For a base
        // extrusion there is no target — the feature is expected to bring a NEW body — so the delta
        // is that body's own volume, a reading rather than a subtraction from an assumed zero.
        var affectedAfter = bodyTarget is null ? null : changes.MatchedOf(bodyTarget.Index);
        double? measuredDelta;
        string measuredBasis;
        var attribution = new List<NamedCheck>();
        var createdBodies = changes.NewBodies;

        if (bodyTarget is not null)
        {
            var volumeBefore = bodyTarget.Snapshot.Volume;
            var volumeAfter = affectedAfter?.Volume;
            measuredDelta = volumeBefore is double low && volumeAfter is double high
                ? (command.Operation == ExtrudeOperation.Cut ? low - high : high - low)
                : null;
            measuredBasis = $"target_body_{bodyTarget.Index}";
        }
        else
        {
            // The "before" and "after" of the delta are measured document sums; no number here is invented.
            // Where the material went: into exactly one body, and that body must be named — one correct sum
            // is not enough, since an error in one body can be balanced by an error in another. Both a new
            // body and a changed existing one are accepted.
            var priorMoved = bodiesBefore
                .Where(s => VolumeMoved(changes.DeltaOf(s.Index), s.Volume))
                .Select(s => s.Index)
                .ToArray();

            if (createdBodies.Count == 1 && priorMoved.Length == 0)
            {
                measuredDelta = createdBodies[0].Volume;
                measuredBasis = "new_body_volume";
                attribution.Add(new NamedCheck(
                    "body_change_attribution",
                    measuredDelta is not null,
                    Observed: $"новое тело {createdBodies[0].Index}: V={Range(createdBodies[0].Volume)}; "
                              + $"прежних тел {bodiesBefore.Count}, изменилось 0",
                    Expected: "изменилось ровно одно тело — новое"));
            }
            else if (createdBodies.Count == 0 && priorMoved.Length == 1)
            {
                var index = priorMoved[0];
                measuredDelta = changes.DeltaOf(index);
                measuredBasis = $"existing_body_{index}_delta";
                attribution.Add(new NamedCheck(
                    "body_change_attribution",
                    measuredDelta is not null,
                    Observed: $"новых тел 0; тело {index} изменилось на {Range(measuredDelta)}",
                    Expected: "изменилось ровно одно тело — новое"));
            }
            else
            {
                measuredDelta = null;
                measuredBasis = "not_attributable";
                attribution.Add(new NamedCheck(
                    "body_change_attribution",
                    false,
                    Observed: $"новых тел {createdBodies.Count}"
                              + (createdBodies.Count == 0
                                  ? string.Empty
                                  : $" [{string.Join("; ", createdBodies.Select(b => $"тел{b.Index} V={Range(b.Volume)}"))}]")
                              + $"; прежних тел изменилось {priorMoved.Length}"
                              + (priorMoved.Length == 0
                                  ? string.Empty
                                  : $" [{string.Join(", ", priorMoved.Select(i => $"тел{i}"))}]"),
                    Expected: "изменилось ровно одно тело — новое"));
            }
        }

        // Expected change = the analytic area of the profile the server itself drew × depth: the region the
        // contours enclose (a contour inside another is a hole), recomputed from the whole profile rather than
        // remembered as a number. When the region is not analytically known (arcs, free polylines, touching
        // contours, a profile drawn outside this session), the answer says so.
        // INVARIANT: an expectation that CANNOT be computed yields NO expected_basis / volume_delta check with
        // passed=false; its reason goes to unverified_aspects instead. "Not confirmed" and "checked and did not
        // match" are different states, and a not_computable check on a correct feature named the second.
        // History: docs/decisions/adapter-core.md#extrude-self-check
        double? expected = null;
        string? expectedBasis = null;
        string? expectedUnavailable = null;
        var profileOutcome = _sketchProfiles.TryGetValue(command.SketchRef, out var profile)
            ? profile.Outcome
            : null;
        if (profileOutcome is null)
        {
            expectedUnavailable = "expected_volume_not_computable — профиль эскиза не записан этим "
                + "сеансом (ссылки нет в реестре профилей), поэтому ожидаемый объём посчитать не из чего";
        }
        else if (profileOutcome.AreaMm2 is not double profileAreaMm2 || profileAreaMm2 <= 0d)
        {
            // The reason the region is unknown travels with it: "not computable" alone told the caller
            // nothing about which primitive or which relation defeated the analytic area.
            expectedUnavailable = "expected_volume_not_computable — площадь нарисованного профиля "
                + "аналитически не вычислена: "
                + (profileOutcome.UnavailableReason ?? "причина не названа");
        }
        else if (command.EndCondition == ExtrudeEndCondition.Through)
        {
            // Through mode has no caller-supplied depth, so the traversed material is taken from the body
            // itself: its extent along the sketch normal. That is a property of the model, not a guess,
            // and it fails loudly when the profile is not centred in the material. "The body itself" means
            // the resolved target body, not whatever GetMainBody returns in a multi-body part.
            if (ThroughExtentMm(document, target.Sketch, bodyTarget?.Body) is double extentMm)
            {
                expected = profileAreaMm2 * extentMm;
                expectedBasis = $"profile_area_x_body_extent_{extentMm:0.####}mm";
            }
            else
            {
                expectedUnavailable = "expected_volume_not_computable — протяжённость тела вдоль "
                    + "нормали эскиза не прочитана, поэтому проходимый материал не посчитан";
            }
        }
        else
        {
            expected = profileAreaMm2 * DepthOf(command);
            expectedBasis = "profile_area_x_depth";
        }

        var checks = new List<NamedCheck>
        {
            new("feature_created", true),
            new("body_present", bodyCount >= 1, Observed: bodyCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (expectedBasis is not null)
        {
            // Emitted ONLY when the expectation exists: a check with passed=false here said "the basis is
            // wrong" where the truth was "there is no basis to state".
            checks.Add(new NamedCheck("expected_basis", true, Observed: expectedBasis));
        }

        // Reported always, gated only for a boss. A cut may divide one body into two (a through slot
        // across a plate does exactly that), so counting bodies is evidence to show there, not a
        // condition. A boss has no such excuse: the caller named the body to grow, and an extra body
        // means material went elsewhere (probe P2.6, choose type left at ksNewBody).
        checks.Add(new NamedCheck(
            "body_count",
            command.Operation switch
            {
                ExtrudeOperation.Boss => bodiesBefore.Count == bodyCount,
                _ => bodyCount >= 1,
            },
            Observed: $"{bodiesBefore.Count}→{bodyCount}"));

        checks.AddRange(attribution);

        var geometryConfirmed = false;
        if (expected is double expectedDelta)
        {
            if (measuredDelta is not double measured)
            {
                // An unread delta is "not confirmed", not zero: zero would pass off the unverified as measured.
                checks.Add(new NamedCheck(
                    "volume_delta",
                    false,
                    Observed: measuredBasis == "not_attributable"
                        ? "no_single_body_changed"
                        : "no_delta_reading",
                    Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            }
            else
            {
                geometryConfirmed = Math.Abs(measured - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
                checks.Add(new NamedCheck(
                    "volume_delta",
                    geometryConfirmed,
                    Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                    Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        // Attribution is required exactly where it was computed: base has no target, and without it "a delta
        // of 1000" could come from any body in the document. For boss/cut the addressing is carried by
        // selector_choose_type / target_body_affected / nontarget_bodies_unchanged below.
        geometryConfirmed &= attribution.All(c => c.Passed);

        var unverified = new List<string>();
        if (expectedUnavailable is not null)
        {
            // Named BEFORE the other aspects: this is why the feature is not geometry_checked, and it is
            // a statement about the expectation, not about the geometry that was built.
            unverified.Add(expectedUnavailable);
        }

        if (bodyTarget is not null)
        {
            // The declaration is worth anything only if the kernel consults the body list it was offered:
            // chooseType=2 consults parts only and changed nothing in P2.6 even though Create returned true.
            // So the read-back is a check, and a failed one rules out geometry_checked.
            var selectorDeclared = chooseTypeReadBack == KompasChoose.Bodies;
            checks.Add(new NamedCheck(
                "selector_choose_type",
                selectorDeclared,
                Observed: $"{Range(chooseTypeReadBack)} (Add: {string.Join("; ", selector)})".Trim(),
                Expected: KompasChoose.Bodies.ToString()));
            geometryConfirmed &= selectorDeclared;

            var targetDelta = changes.DeltaOf(bodyTarget.Index);
            var targetBoxMoved = changes.BoxChangedOf(bodyTarget.Index);
            var targetTopologyMoved = changes.TopologyChangedOf(bodyTarget.Index);
            // "The body was affected" is a statement about the KERNEL, not about a coarse threshold: a
            // feature may add 9e-4 mm³, far below any "meaningful" floor, and the body HAS changed. Volume
            // beyond the measured noise, a moved gabarit or a changed face count all count — a body can shift
            // without its volume changing (a boolean `intersect`), and topology can change with both intact.
            var movedAsExpected = BodyChangePolicy.Changed(
                targetDelta, bodyTarget.Snapshot.Volume, targetBoxMoved, targetTopologyMoved);
            checks.Add(new NamedCheck(
                "target_body_affected",
                movedAsExpected,
                Observed: $"тело {bodyTarget.Index}: {Range(bodyTarget.Snapshot.Volume)} → {Range(affectedAfter?.Volume)}, "
                          + $"ΔV(после−до)={Range(targetDelta)}, габарит изменился={targetBoxMoved}, "
                          + $"граней {Range(bodyTarget.Snapshot.FaceCount)} → {Range(affectedAfter?.FaceCount)}, "
                          + $"порог шума={BodyChangePolicy.VolumeNoiseMm3:0.#####e+0} мм³",
                Expected: "|ΔV| > шума измерения ИЛИ сдвинулся габарит ИЛИ изменилась топология"));
            geometryConfirmed &= movedAsExpected;

            var others = changes.UnchangedViolations(exceptIndex: bodyTarget.Index);
            checks.Add(new NamedCheck(
                "nontarget_bodies_unchanged",
                others.Count == 0,
                Observed: others.Count == 0
                    ? $"остальные {Math.Max(0, changes.Rows.Count - 1)} тел не изменились"
                    : string.Join("; ", others),
                Expected: "ΔV = 0 вне заявленного тела"));
            geometryConfirmed &= others.Count == 0;

            // A no-op is not a result. KOMPAS reports no error for a declaration that contradicts where the
            // profile lies (P2.6): Create and RebuildDocument say true and no body changes. Only an explicit
            // failure keeps that from being read as success. An unreadable volume is not a measured no-op: it
            // fails the check and says why.
            // INVARIANT: "no change" means volume AND gabarit AND face count all stayed within their noise —
            // a small but real feature (9e-4 mm³) is NOT a no-op, and reporting it as one was the defect.
            if (!movedAsExpected)
            {
                // For a one-sided extrusion that changed nothing, name WHERE the operation went and whether
                // the body lies wholly on the other side of the sketch plane — a checkable fact about the
                // gabarit. The operation is NOT repeated with the other direction automatically: that is a
                // behaviour change and no customer decision covers it.
                var noChange = BuildNoChangeHint(command, target, bodyTarget);
                var sideLine = noChange.BodyLiesOpposite == true
                    ? $" Операция ушла в сторону, где у тела нет материала: запрошенное направление "
                        + $"{noChange.RequestedDirection} снимает/добавляет материал в сторону "
                        + $"{noChange.AttemptedToward}, а габарит тела лежит целиком в стороне {noChange.BodySide}. "
                        + $"Для работы по телу задайте direction: {noChange.OppositeDirection}."
                    : noChange.AttemptedToward is not null
                        ? $" Операция идёт в сторону {noChange.AttemptedToward} (по правилу нормали эскиза); "
                            + "лежит ли тело целиком по другую сторону плоскости — не проверено."
                        : string.Empty;
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    $"Признак создан (Create=true), но заявленное тело {bodyTarget.Index} не изменилось ни объёмом, ни габаритом, ни числом граней: " +
                    $"ΔV(после−до)={Range(targetDelta)} мм³ при пороге шума {BodyChangePolicy.VolumeNoiseMm3:0.#####e+0} мм³, габарит изменился={targetBoxMoved}, " +
                    $"граней {Range(bodyTarget.Snapshot.FaceCount)} → {Range(affectedAfter?.FaceCount)}. " +
                    "Так КОМПАС ведёт себя, когда заявленное тело противоречит расположению контура: ошибка не возвращается, " +
                    "не меняется ни одно тело. Успехом это быть не может. Признак ОСТАЛСЯ в дереве — удалите его вызовом " +
                    "kompas_delete_feature по feature_ref." + sideLine,
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = command.TargetBodyRef,
                        ["target_body_index"] = bodyTarget.Index,
                        // The feature the caller must delete: the operation created it and it did nothing.
                        ["feature_ref"] = ToDto(featureReference, $"{command.Operation} {FormatDepth(command)}").Id,
                        // AFTER minus BEFORE, the same convention as every other delta in this response:
                        // positive = material added, negative = material removed.
                        ["target_delta_mm3"] = targetDelta,
                        ["target_delta_meaning"] = "объём целевого тела: после − до (положительное — материал добавлен, отрицательное — снят)",
                        ["volume_noise_mm3"] = BodyChangePolicy.VolumeNoiseMm3,
                        ["target_box_changed"] = targetBoxMoved,
                        ["target_face_count"] = bodyTarget.Snapshot.FaceCount,
                        ["target_face_count_after"] = affectedAfter?.FaceCount,
                        ["changed_body_indexes"] = changes.Changed.ToArray(),
                        ["moved_body_indexes"] = changes.Moved.ToArray(),
                        ["touched_body_indexes"] = changes.Touched.ToArray(),
                        ["per_body"] = changes.Rows.ToArray(),
                        ["profile_target_agreement"] = AgreementName(agreement),
                        // Where the operation went and what to try instead: the side is named by the documented
                        // sketch-normal rule when the normal is readable, and the body's side is a measured
                        // fact from its box and the plane origin. Nothing here is a guess; unreadable parts are
                        // left null rather than asserted.
                        ["requested_direction"] = noChange.RequestedDirection,
                        ["attempted_material_toward"] = noChange.AttemptedToward,
                        ["opposite_direction"] = noChange.OppositeDirection,
                        ["body_side"] = noChange.BodySide,
                        ["body_lies_opposite"] = noChange.BodyLiesOpposite,
                        // The side of the material is NAMED as not determined, rather than left absent:
                        // an absent field is indistinguishable from a forgotten write, and here the reason
                        // is substantive — nothing was removed or added, so no side exists to name.
                        ["material_removed_toward"] = null,
                        ["material_added_toward"] = null,
                        ["material_toward_unavailable"] =
                            "признак не изменил ни объём, ни габарит, ни число граней: материала не снято и не "
                            + "добавлено, поэтому сторона в координатах детали не определяется",
                        ["code"] = "target_body_not_affected",
                    });
            }

            if (targetDelta is null)
            {
                unverified.Add("target_body_volume_unreadable — объём заявленного тела после операции не прочитан, поэтому доказать его изменение нечем");
            }
        }

        if (agreement is null && bodyTarget is not null)
        {
            unverified.Add("profile_target_agreement_not_checked — нет сохранённых координат нарисованного профиля или плоскость эскиза не сведена к одной из трёх базовых");
        }
        else if (agreement == true && bodyTarget is not null)
        {
            unverified.Add("profile_inside_target_not_proven — сверены габариты профиля и тела по двум осям плоскости, вхождение контура в материал не проверялось");
        }

        if (expected is not null && !geometryConfirmed)
        {
            unverified.Add("volume_delta_not_confirmed — КОМПАС сообщает только об успехе вызова; пока измерение не совпало, признак считается недоказанным");
        }

        if (bodyTarget is null && attribution.Any(c => !c.Passed))
        {
            // The delta is not attributed to any body, so there is nothing to compare with the analytic
            // figure: "changed by 1000 somewhere in the document" is not proof of the feature.
            unverified.Add(
                "body_change_attribution_failed — приращение материала не приписано ровно одному телу "
                + "(новому или прежнему): одна правильная сумма не заменяет адресности, потому что "
                + "ошибка в одном теле может быть уравновешена ошибкой в другом");
        }

        var reference = featureReference;
        var material = DescribeMaterialDirection(command, target, bodyTarget, changes);
        if (material.Reason is not null)
        {
            unverified.Add(material.Reason);
        }

        if (material.Divergence is not null)
        {
            // The two independent sources name opposite sides: not a silent pick — the caller is told, and
            // the acceptance row that exercises it is a FAIL.
            unverified.Add(material.Divergence);
        }

        var verification = new VerificationDto(
            geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
            checks,
            unverified);

        var hint = command.EndCondition == ExtrudeEndCondition.Through
            ? $"{command.Operation} насквозь"
            : $"{command.Operation} {FormatDepth(command)}";
        return new ExtrudeResult(
            ToDto(reference, hint),
            bodyCount,
            // volume_mm3 — the SUM of all document bodies' volumes after the operation. The field used to
            // carry different quantities (base — the document sum, boss/cut — the target's volume); now one
            // quantity, named in volume_note, and the feature's delta travels separately.
            documentVolumeAfter,
            verification,
            bodyTarget?.Index,
            changes.Rows,
            documentVolumeBefore,
            documentVolumeAfter,
            measuredDelta,
            measuredBasis,
            "volume_mm3 — сумма объёмов ВСЕХ тел документа после операции (не объём целевого тела и не "
            + "объём пространственного объединения: у перекрывающихся тел сумма и объединение "
            + "различаются). Приращение материала этим признаком — volume_delta_mm3, его основание "
            + "(тело цели, новое тело или изменившееся прежнее) — volume_delta_basis; объёмы до и "
            + "после по документу — document_volume_before_mm3 / document_volume_after_mm3.",
            material.RemovedToward,
            material.AddedToward,
            material.Reason,
            material.Source,
            material.SketchNormal,
            material.Divergence is null ? null : new[] { material.Divergence });
    }

    /// <summary>Which way, in PART coordinates, this operation moved material: the documented sketch-normal
    /// rule, cross-checked against the MEASURED shift of the target body's gabarit.</summary>
    /// <remarks>WHY the rule, not the box alone: the box names a side only when a bound moves, so a hole cut
    /// INSIDE a body — the commonest cut — left the field silent. The normal is readable for every planar
    /// support, so the rule names the side there too. The box is kept as an INDEPENDENT check: when both are
    /// available and disagree the answer says so and the acceptance row is a FAIL, never a silent pick.
    /// DOC: kssketchdefinition_getsurface.html → … → ksplacement_getvector.html.
    /// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
    private MaterialDirection DescribeMaterialDirection(
        ExtrudeCommand command, SketchTarget target, BodyTarget? bodyTarget, BodyComparison changes)
    {
        var removing = command.Operation == ExtrudeOperation.Cut;
        var negative = command.Direction == ExtrudeDirection.Negative;
        var symmetric = command.Direction == ExtrudeDirection.Symmetric;

        var frame = ReadSketchPlaneFrame(target.Definition);
        double[]? ruleDirection = frame.Normal is null
            ? null
            : MaterialDirectionRule.Toward(frame.Normal, removing, negative, symmetric);

        var boxDirection = BoxDirection(command, target, bodyTarget, changes, frame.Normal, out var boxReason);

        // Name the side and its source. The measured box is preferred when it exists (it is the observed
        // fact); the rule fills in where the box is silent.
        string? source;
        string? toward;
        if (boxDirection is not null)
        {
            source = "measured_box_shift";
            toward = MaterialDirectionRule.Describe(boxDirection, symmetric);
        }
        else if (ruleDirection is not null || symmetric && frame.Normal is not null)
        {
            source = "sketch_normal_rule";
            toward = symmetric ? MaterialDirectionRule.DescribeSymmetric(frame.Normal!) : MaterialDirectionRule.Describe(ruleDirection!, false);
        }
        else
        {
            source = null;
            toward = null;
        }

        // Divergence between the two INDEPENDENT sources is not a silent pick: it is named, and the row that
        // exercises it is a FAIL. Only comparable when both are single-sided directions.
        string? divergence = null;
        if (boxDirection is not null && ruleDirection is not null && !MaterialDirectionRule.SameSide(boxDirection, ruleDirection))
        {
            divergence = "material_toward_divergence — сторона по измеренному сдвигу габарита ("
                + MaterialDirectionRule.Describe(boxDirection, false) + ") расходится со стороной по правилу "
                + "нормали эскиза (" + MaterialDirectionRule.Describe(ruleDirection, false)
                + "); расхождение двух независимых источников не выбирается молча";
        }

        string? reason = null;
        if (toward is null)
        {
            reason = "material_toward_unavailable — сторона в координатах детали не определена: "
                + "источник measured_box_shift недоступен (" + (boxReason ?? "нет целевого тела")
                + "), а документированное чтение нормали эскиза не удалось ("
                + (frame.Reason ?? "причина не названа") + ")";
        }

        return new MaterialDirection(
            removing ? toward : null,
            removing ? null : toward,
            reason,
            source,
            frame.Normal,
            divergence);
    }

    /// <summary>The direction material moved, from the MEASURED shift of the target body's gabarit: the bound
    /// that moved names the side — the max bound going out (base/boss) or in (cut) means toward +axis.
    /// Null when no target body, no axis-aligned support, or no bound moved.</summary>
    /// <remarks>The direction is the MOVEMENT, not "which end of the box changed": a cut from the top moves
    /// the max bound inward and removes material toward −axis, so both operations use the same
    /// <c>sign(delta)</c>; naming the moved end would invert every cut against the rule. The axis comes from
    /// the sketch normal when it is axis-aligned (covering a flat face), else from the base plane.
    /// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
    private double[]? BoxDirection(
        ExtrudeCommand command, SketchTarget target, BodyTarget? bodyTarget, BodyComparison changes,
        double[]? normal, out string? reason)
    {
        reason = null;
        if (bodyTarget is null)
        {
            reason = "нет целевого тела, габарит которого назвал бы сторону";
            return null;
        }

        var index = MaterialDirectionRule.AxisIndex(normal)
            ?? ResolveSketchPlaneBase(command.SketchRef, target) switch
            {
                PlaneBase.Xy => 2,
                PlaneBase.Xz => 1,
                PlaneBase.Yz => 0,
                _ => (int?)null,
            };
        if (index is not int axisIndex)
        {
            reason = "плоскость эскиза не сведена к одной из трёх базовых осей (наклонная плоскость или нормаль не прочитана), поэтому сдвиг габарита сторону не называет";
            return null;
        }

        var after = changes.MatchedOf(bodyTarget.Index);
        if (after?.Min is null || after.Max is null
            || bodyTarget.Snapshot.Min is null || bodyTarget.Snapshot.Max is null)
        {
            reason = "габарит целевого тела до или после операции не прочитан";
            return null;
        }

        var deltaMin = after.Min[axisIndex] - bodyTarget.Snapshot.Min[axisIndex];
        var deltaMax = after.Max[axisIndex] - bodyTarget.Snapshot.Max[axisIndex];
        var lowMoved = Math.Abs(deltaMin) > BoxChangeFloorMm;
        var highMoved = Math.Abs(deltaMax) > BoxChangeFloorMm;
        if (!lowMoved && !highMoved)
        {
            reason = "габарит целевого тела не сдвинулся (материал снят внутри тела либо не тронут)";
            return null;
        }

        var direction = new double[3];
        if (lowMoved && highMoved)
        {
            // Both bounds moved: material went to both sides. Kept as a both-sides direction.
            direction[axisIndex] = 1d;
            return direction;
        }

        direction[axisIndex] = (lowMoved ? deltaMin : deltaMax) > 0d ? 1d : -1d;
        return direction;
    }

    /// <summary>The sketch plane's normal and origin in PART coordinates, read by the documented v24 route
    /// from the support's SURFACE, so a base plane, an offset plane and a flat face are all covered. A failure
    /// is NAMED, never replaced by a guessed axis.</summary>
    /// <remarks>DOC: <c>kssketchdefinition_getsurface.html</c> (<c>GetSurface → ksSurface</c>),
    /// <c>kssurface_isplane.html</c>, <c>kssurface_getsurfaceparam.html</c> (<c>→ ksPlaneParam</c>),
    /// <c>ksplaneparam_getplacement.html</c> (<c>→ ksPlacement</c>; «Оси X и Y системы координат лежат в
    /// плоскости», so the OZ axis is the plane's normal), <c>ksplacement_getvector.html</c> (type: 0=OX,
    /// 1=OY, &gt;1=OZ; the answer is the axis DIRECTION), <c>ksplacement_getorigin.html</c>. The sign of that
    /// OZ axis is MEASURED against the box shift, not assumed.
    /// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
    private static (double[]? Normal, double[]? Origin, string? Reason) ReadSketchPlaneFrame(ksSketchDefinition definition)
    {
        try
        {
            if (definition.GetSurface() is not ksSurface surface)
            {
                return (null, null, "GetSurface() не дал ksSurface");
            }

            if (!surface.IsPlane())
            {
                return (null, null, "поверхность опоры эскиза не плоская");
            }

            if (surface.GetSurfaceParam() is not ksPlaneParam plane)
            {
                return (null, null, "GetSurfaceParam() не дал ksPlaneParam");
            }

            if (plane.GetPlacement() is not ksPlacement placement)
            {
                return (null, null, "ksPlaneParam.GetPlacement() не дал ksPlacement");
            }

            // INVARIANT: the plane's normal is the OZ axis DIRECTION of its placement, so it is read with
            // GetVector — the help gives that member as «X, Y, Z — компоненты вектора направления оси».
            // GetAxis is NOT used: its triple is not a direction on a placement that is not at the origin.
            // LIMIT: the help does not promise a UNIT vector, so the direction is normalised before any
            // axis-alignment test. MEASURED: GetVector(OZ) answered a unit vector on every support tried
            // (base planes at 0 and ±5, top/bottom/side faces), so the normalisation is a no-op there.
            if (!placement.GetVector(2, out var nx, out var ny, out var nz))
            {
                return (null, null, "ksPlacement.GetVector(OZ) вернул false");
            }

            var magnitude = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (magnitude <= MaterialDirectionRule.AxisTolerance)
            {
                return (null, null, "нормаль плоскости эскиза вырождена (нулевой вектор)");
            }

            double[] normal = [nx / magnitude, ny / magnitude, nz / magnitude];

            double[]? origin = placement.GetOrigin(out var ox, out var oy, out var oz)
                ? [ox, oy, oz]
                : null;
            return (normal, origin, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, null, "чтение поверхности опоры эскиза бросило " + ex.GetType().Name);
        }
    }

    /// <summary>Where the material went, in part coordinates, with the reason it could not be named and the
    /// source that named it.</summary>
    private sealed record MaterialDirection(
        string? RemovedToward,
        string? AddedToward,
        string? Reason,
        string? Source,
        double[]? SketchNormal,
        string? Divergence);

    /// <summary>Where a one-sided extrusion that changed nothing went, and whether the target body lies wholly
    /// on the other side of the sketch plane. Every field is measured or read; an unreadable part stays null.</summary>
    private sealed record NoChangeHint(
        string RequestedDirection,
        string? AttemptedToward,
        string? OppositeDirection,
        string? BodySide,
        bool? BodyLiesOpposite);

    /// <summary>Builds the no-change hint from the documented sketch-normal rule (the side the operation went
    /// to) and the target body's box against the plane origin (which side the body is on). The operation is
    /// NOT re-run with the opposite direction — that would be a behaviour change with no customer decision.</summary>
    /// <remarks>DOC for the normal: kssketchdefinition_getsurface.html → … → ksplacement_getvector.html.
    /// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
    private NoChangeHint BuildNoChangeHint(ExtrudeCommand command, SketchTarget target, BodyTarget? bodyTarget)
    {
        var requested = command.Direction switch
        {
            ExtrudeDirection.Positive => "positive",
            ExtrudeDirection.Negative => "negative",
            _ => "symmetric",
        };
        var opposite = command.Direction == ExtrudeDirection.Positive ? "negative"
            : command.Direction == ExtrudeDirection.Negative ? "positive"
            : null;

        var frame = ReadSketchPlaneFrame(target.Definition);
        if (frame.Normal is null)
        {
            return new NoChangeHint(requested, null, opposite, null, null);
        }

        var removing = command.Operation == ExtrudeOperation.Cut;
        var negative = command.Direction == ExtrudeDirection.Negative;
        var symmetric = command.Direction == ExtrudeDirection.Symmetric;
        var attempted = MaterialDirectionRule.Toward(frame.Normal, removing, negative, symmetric);
        if (attempted is null)
        {
            // symmetric acts on both sides, so no single side "went nowhere" and there is no opposite to offer.
            return new NoChangeHint(requested, null, null, null, null);
        }

        string? bodySide = null;
        bool? liesOpposite = null;
        if (bodyTarget?.Snapshot.Min is double[] lo && bodyTarget.Snapshot.Max is double[] hi
            && frame.Origin is double[] origin)
        {
            // Project every box corner onto the attempted direction, measured from the plane origin. All
            // corners on the negative side means the body is wholly where the operation did not go.
            var minProjection = double.PositiveInfinity;
            var maxProjection = double.NegativeInfinity;
            for (var mask = 0; mask < 8; mask++)
            {
                var projection = 0d;
                for (var a = 0; a < 3; a++)
                {
                    var corner = ((mask >> a) & 1) == 0 ? lo[a] : hi[a];
                    projection += (corner - origin[a]) * attempted[a];
                }

                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            liesOpposite = maxProjection <= BoxChangeFloorMm;
            if (liesOpposite == true)
            {
                bodySide = MaterialDirectionRule.Describe([-attempted[0], -attempted[1], -attempted[2]], false);
            }
        }

        return new NoChangeHint(requested, MaterialDirectionRule.Describe(attempted, false), opposite, bodySide, liesOpposite);
    }

    /// <summary>How the pre-mutation agreement test resolved, in words for the details block.</summary>
    private static string AgreementName(bool? agreement) => agreement switch
    {
        true => "boxes_agree",
        false => "boxes_disagree",
        null => "not_checked",
    };

    /// <summary>Short invariant rendering of a number or a triple of coordinates: round-trip format so
    /// a measured volume is not dressed up as a match the kernel did not produce.</summary>
    private static string Range(double? value) =>
        value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "нет";

    /// <summary>An integer reading for a report row: "нет" is "not read", never 0.</summary>
    private static string Range(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "нет";

    private static string Range(double[]? values) =>
        values is null
            ? "нет"
            : string.Join(", ", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Whether a volume delta is larger than the measured noise. The rule itself lives in the
    /// Domain (<see cref="BodyChangePolicy"/>) so it can be tested without COM; this is the call site's name
    /// for it. <paramref name="volume"/> is the body's own volume, so the relative part scales with it.</summary>
    private static bool VolumeMoved(double? delta, double? volume) => BodyChangePolicy.VolumeMoved(delta, volume);

    /// <summary>The body an extrusion was told to act on: where it sits in <c>BodyCollection</c> right now,
    /// the raw collection element <c>ksBodyCollection.Add</c> accepts, and its state before the
    /// mutation.</summary>
    /// <remarks>The index is resolved at mutation time by pointer rather than taken from the reference,
    /// because nothing here proves the collection keeps its order across a rebuild; the raw element
    /// comes from that same enumeration so index and element cannot disagree.</remarks>
    private sealed record BodyTarget(int Index, object RawElement, ksBody Body, BodySnapshot Snapshot);

    /// <summary>One body as a snapshot reader saw it: volume in mm³, its gabarit, and its face count.</summary>
    private sealed class BodySnapshot
    {
        public required int Index { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        /// <summary>Faces of the body, or null when the collection did not answer. A TOPOLOGY signal:
        /// a feature can add or remove faces while leaving the volume and the box unchanged (measured:
        /// the wheel's edge count changed with volume and gabarit intact), so volume and box alone would
        /// call such a change a no-op.</summary>
        public int? FaceCount { get; init; }

        public double[]? Center => Min is null || Max is null
            ? null
            : new[] { (Min[0] + Max[0]) / 2d, (Min[1] + Max[1]) / 2d, (Min[2] + Max[2]) / 2d };
    }

    /// <summary>Per-body volumes and boxes, read the way every measurement here is —
    /// <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c> and <c>ksBody.GetGabarit</c>.</summary>
    /// <remarks><c>refresh()</c> before counting is not decoration: without it a collection read right
    /// after a rebuild can report the previous membership (the <c>ListBodies</c> defect).
    /// <c>GetMainBody()</c> is deliberately not used — in a multi-body part it answers one body only.
    /// History: docs/decisions/adapter-core.md#read-body-snapshots</remarks>
    private static List<BodySnapshot> ReadBodySnapshots(ksPart part)
    {
        var rows = new List<BodySnapshot>();
        try
        {
            var bodies = (ksBodyCollection)part.BodyCollection();
            bodies.refresh();
            var count = bodies.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = bodies.GetByIndex(i);
                var body = AsInterface<ksBody>(element);
                double? volume = null;
                double[]? min = null;
                double[]? max = null;
                int? faceCount = null;
                if (body is not null)
                {
                    volume = SafeDouble(() => MassProperties(body, (uint)KompasUnits.MassMmKg)?.v
                        ?? throw new InvalidCastException("объём недоступен"));
                    try
                    {
                        if (body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
                        {
                            min = new[] { x1, y1, z1 };
                            max = new[] { x2, y2, z2 };
                        }
                    }
                    catch (Exception ex) when (ex is COMException)
                    {
                        // No box: the snapshot keeps its volume, and the comparison degrades to
                        // volume alone rather than dropping the body from the report.
                    }

                    faceCount = SafeInt(() =>
                    {
                        var faces = (ksFaceCollection)body.FaceCollection();
                        faces.refresh();
                        return faces.GetCount();
                    });
                }

                rows.Add(new BodySnapshot { Index = i, Volume = volume, Min = min, Max = max, FaceCount = faceCount });
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Whatever was read before the failure stands. An empty list is the honest answer to
            // "which bodies and at what volumes" when the collection cannot be walked at all.
        }

        return rows;
    }

    /// <summary>Resolves <c>target_body_ref</c> into the body the selector call needs: the reference holds a
    /// <c>ksBody</c>, but <c>ksBodyCollection.Add</c> wants the raw element of the part's body collection
    /// (measured in P2.6: the cast to <c>ksEntity</c> and the unpacked <c>GetDefinition()</c> are both
    /// refused). The only shared identity is the object's IUnknown, so the index is found by pointer and the
    /// element taken from that same walk.
    /// History: docs/decisions/adapter-core.md#resolve-body-target-identity</summary>
    private BodyTarget ResolveBodyTarget(DocumentEntry document, ksPart part, string targetBodyRef, List<BodySnapshot> bodiesBefore)
    {
        var stored = References.Require(targetBodyRef, document.Id, document.Revision);
        if (!stored.Kind.StartsWith("body", StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"target_body_ref '{targetBodyRef}' указывает на '{stored.Kind}', а не на тело.",
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        if (stored.Payload is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{targetBodyRef}' не несёт объекта тела.",
                RetryPolicy.ReacquireContext);
        }

        var index = MatchBodyIndexByPointer(part, stored.Payload, out var rawElement);
        if (index is null || rawElement is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Заявленное тело '{targetBodyRef}' не найдено среди элементов BodyCollection по IUnknown. " +
                "Подставлять вместо совпадения позицию нельзя: признак уйдёт в другое тело, а КОМПАС на это не ошибается.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["target_body_ref"] = targetBodyRef,
                    ["reference_kind"] = stored.Kind,
                    ["bodies_in_collection"] = bodiesBefore.Count,
                    ["code"] = "target_body_not_in_collection",
                });
        }

        var body = stored.Payload as ksBody ?? AsInterface<ksBody>(rawElement);
        if (body is null)
        {
            throw new KompasContractException(
                ErrorCodes.NoBody,
                "Элемент BodyCollection не отвечает как ksBody: протяжённость материала для ожидания не читается.",
                RetryPolicy.ReacquireContext);
        }

        // LIMIT: a body reference is NOT checked against the gabarit it was issued at. That check was
        // built and MEASURED to refuse legitimate work: a client that moves a body and then addresses the
        // SAME body again with the same reference (rows B3.56, B3.59) gets STALE_REFERENCE, because the
        // gabarit legitimately changed. Without a documented stable body identifier, a changed gabarit
        // cannot be told apart from a re-used pointer, so the check is left out rather than shipped
        // refusing correct calls. History: docs/decisions/adapter-features.md#body-ref-lifetime
        if (index.Value >= bodiesBefore.Count)
        {
            throw new KompasContractException(
                ErrorCodes.NoBody,
                $"Тело найдено по индексу {index.Value}, но снимок тел его не содержит: доказать результат операции нечем.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["target_body_index"] = index.Value });
        }

        return new BodyTarget(index.Value, rawElement, body, bodiesBefore[index.Value]);
    }

    /// <summary>Position of <paramref name="wanted"/> inside <c>part.BodyCollection()</c>, compared by
    /// IUnknown. Every pointer acquired here is released at once: <c>GetIUnknownForObject</c> adds a
    /// reference, and one leaked pointer per call is a slow leak of the whole model.</summary>
    private static int? MatchBodyIndexByPointer(ksPart part, object wanted, out object? rawElement)
    {
        rawElement = null;
        try
        {
            var collection = (ksBodyCollection)part.BodyCollection();
            collection.refresh();
            var count = collection.GetCount();
            var wantedPointer = Marshal.GetIUnknownForObject(wanted);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var element = collection.GetByIndex(i);
                    if (element is null)
                    {
                        continue;
                    }

                    var pointer = Marshal.GetIUnknownForObject(element);
                    Marshal.Release(pointer);
                    if (pointer == wantedPointer)
                    {
                        rawElement = element;
                        return i;
                    }
                }
            }
            finally
            {
                Marshal.Release(wantedPointer);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Per-body before/after comparison. Bodies are matched by the position of their box centre
    /// rather than by collection order, which is not measured across a rebuild — matching by position is
    /// what turns "the plate lost nothing" into a number instead of an assumption.</summary>
    private static BodyComparison CompareBodySnapshots(List<BodySnapshot> before, List<BodySnapshot> after)
    {
        var report = new BodyComparison();
        var taken = new HashSet<int>();
        foreach (var reference in before)
        {
            var match = NearestUnused(after, reference, taken);
            if (match is not null)
            {
                taken.Add(match.Index);
            }

            // A body with no counterpart after the rebuild is "unknown", not "unchanged": 0 would read as a
            // no-op, and conflating the two is the kind of laundering this file exists to avoid.
            // SIGN: the delta is AFTER minus BEFORE — positive means material was ADDED to the body. One
            // convention everywhere in this report, so a reader never has to know which operation ran.
            double? delta = reference.Volume is double low && match?.Volume is double high
                ? high - low
                : null;
            report.Record(reference, match, delta);
        }

        // Bodies present AFTER the operation that no "before" row was matched to: the ones the
        // operation brought into existence. They are what a base extrusion is judged by — the
        // document sum alone cannot tell "a new body of 1000" from "an existing body grew by 1000".
        report.RecordNewBodies(after.Where(row => !taken.Contains(row.Index)));

        return report;
    }

    private static BodySnapshot? NearestUnused(List<BodySnapshot> rows, BodySnapshot reference, HashSet<int> taken)
    {
        var free = rows.Where(r => !taken.Contains(r.Index)).ToArray();
        if (free.Length == 0)
        {
            return null;
        }

        if (reference.Center is null)
        {
            // Without a box there is nothing to match on. Position is the last thing to fall back to
            // and it is said so in the row rather than passed off as a match.
            return free[0];
        }

        return free
            .Select(r => (Row: r, Distance: CenterDistance(r.Center, reference.Center)))
            .OrderBy(pair => pair.Distance ?? double.MaxValue)
            .First().Row;
    }

    private static double? CenterDistance(double[]? a, double[]? b)
    {
        if (a is null || b is null || a.Length < 3 || b.Length < 3)
        {
            return null;
        }

        return Math.Sqrt(
            (a[0] - b[0]) * (a[0] - b[0])
            + (a[1] - b[1]) * (a[1] - b[1])
            + (a[2] - b[2]) * (a[2] - b[2]));
    }

    /// <summary>How far a body's bounding box must move to count as a position change, in mm.</summary>
    /// <remarks>"The body changed" is NOT the same as "its volume changed": a boolean edit of kind
    /// `intersect` can leave the volume equal while the box position differs, so volume alone would reject a
    /// correct `intersect` (order §4.1). The threshold is 1 nm — the box is exact B-Rep coordinates.
    /// History: docs/decisions/adapter-core.md#box-change-floor</remarks>
    private const double BoxChangeFloorMm = 1e-6d;

    /// <summary>The comparison itself: what moved, what did not, and the rows that say so.</summary>
    private sealed class BodyComparison
    {
        private readonly Dictionary<int, double?> _delta = new();

        private readonly Dictionary<int, BodySnapshot?> _matched = new();

        private readonly Dictionary<int, bool> _boxChanged = new();

        private readonly Dictionary<int, double?> _boxShift = new();

        private readonly Dictionary<int, bool> _topologyChanged = new();

        public List<string> Rows { get; } = new();

        /// <summary>Bodies whose VOLUME changed — the "material worked here" signal, kept separate from
        /// position: a boolean edit of kind `intersect` does not change volume at all.</summary>
        public List<int> Changed { get; } = new();

        /// <summary>Bodies whose box changed but volume stayed the same. Separate from <see cref="Changed"/>
        /// because "the body moved" and "the body lost material" are different observations.</summary>
        public List<int> Moved { get; } = new();

        /// <summary>Bodies whose volume OR box OR face count changed — the correct predicate for "the result
        /// of this operation", not fitted to an expectation and not dependent on the operation kind. The face
        /// count is in the predicate because a feature can change topology while leaving volume and box equal.</summary>
        public List<int> Touched { get; } = new();

        /// <summary>Bodies that appeared AFTER the operation and were matched to no "before". An empty list is
        /// the measured fact "there are no new bodies", not "we did not count".</summary>
        public List<BodySnapshot> NewBodies { get; } = new();

        public void RecordNewBodies(IEnumerable<BodySnapshot> rows) => NewBodies.AddRange(rows);

        public double? DeltaOf(int index) => _delta.TryGetValue(index, out var delta) ? delta : null;

        public BodySnapshot? MatchedOf(int index) => _matched.TryGetValue(index, out var match) ? match : null;

        public bool BoxChangedOf(int index) => _boxChanged.TryGetValue(index, out var changed) && changed;

        /// <summary>Whether this body's face count changed. Null on either side is "unknown", never "changed":
        /// an unreadable count must not be reported as a topology change.</summary>
        public bool TopologyChangedOf(int index) =>
            _topologyChanged.TryGetValue(index, out var changed) && changed;

        /// <summary>Largest difference in this body's box coordinates, in mm.</summary>
        public double? BoxShiftOf(int index) => _boxShift.TryGetValue(index, out var shift) ? shift : null;

        public void Record(BodySnapshot before, BodySnapshot? after, double? delta)
        {
            _delta[before.Index] = delta;
            _matched[before.Index] = after;
            var shift = BoxShift(before, after);
            var volumeMoved = VolumeMoved(delta, before.Volume);
            var boxMoved = shift is double movedMm && movedMm > BoxChangeFloorMm;
            var topologyMoved = before.FaceCount is int facesBefore
                && after?.FaceCount is int facesAfter
                && facesBefore != facesAfter;
            _boxChanged[before.Index] = boxMoved;
            _boxShift[before.Index] = shift;
            _topologyChanged[before.Index] = topologyMoved;
            Rows.Add($"тел{before.Index}: V {Range(before.Volume)} → {Range(after?.Volume)}, ΔV(после−до)={Range(delta)}, " +
                     $"габарит [{Range(before.Min)}|{Range(before.Max)}] → [{Range(after?.Min)}|{Range(after?.Max)}], " +
                     $"граней {Range(before.FaceCount)} → {Range(after?.FaceCount)}" +
                     (boxMoved ? $", сдвиг {Range(shift)} мм" : string.Empty));
            if (volumeMoved)
            {
                Changed.Add(before.Index);
            }

            if (!volumeMoved && (boxMoved || topologyMoved))
            {
                Moved.Add(before.Index);
            }

            if (volumeMoved || boxMoved || topologyMoved)
            {
                Touched.Add(before.Index);
            }
        }

        /// <summary>Largest difference across the six box coordinates, or <c>null</c> if there is no box.</summary>
        public static double? BoxShift(BodySnapshot before, BodySnapshot? after)
        {
            if (after is null
                || before.Min is null || before.Max is null
                || after.Min is null || after.Max is null)
            {
                return null;
            }

            var worst = 0d;
            for (var axis = 0; axis < 3; axis++)
            {
                worst = Math.Max(worst, Math.Abs(before.Min[axis] - after.Min[axis]));
                worst = Math.Max(worst, Math.Abs(before.Max[axis] - after.Max[axis]));
            }

            return worst;
        }

        /// <summary>Foreign bodies whose state changed. The "foreign bodies untouched" check must also see a
        /// move: a body that shifted aside with the same volume violates the declared addressing just as one
        /// that lost material does.</summary>
        public List<string> UnchangedViolations(int exceptIndex)
        {
            var offenders = new List<string>();
            foreach (var index in _delta.Keys.Where(k => k != exceptIndex).OrderBy(k => k))
            {
                var parts = new List<string>();
                if (VolumeMoved(_delta[index], MatchedOf(index)?.Volume))
                {
                    parts.Add("ΔV(после−до)=" + (_delta[index] ?? 0d).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " мм³");
                }

                if (BoxChangedOf(index))
                {
                    parts.Add("сдвиг габарита");
                }

                if (parts.Count > 0)
                {
                    offenders.Add($"тел{index}: " + string.Join(", ", parts));
                }
            }

            return offenders;
        }
    }

    /// <summary>Order follows the sequence proven against v24 in P0.7: attach the sketch, then set
    /// directionType, then the side parameters. Reporting which of the three refused matters:
    /// "SetSketch returned false" and "SetSideParam returned false" are different defects.</summary>
    private bool ConfigureBase(
        ksBaseExtrusionDefinition definition,
        ksEntity sketch,
        short direction,
        ExtrudeCommand command,
        DocumentEntry document)
    {
        AttachSketch(() => definition.SetSketch(sketch), document, command);

        // dtNormal=0, dtReverse=1, dtBoth=2, dtMiddlePlane=3 (kAPI5.tlb, ksDirectionTypeEnum).
        // directionType IS the direction; it is not a "which side to draw" selector. Negative must therefore
        // stay 1 — an unsupported combination does not fail loudly, it silently ignores SetSideParam
        // (measured in P2.1 for cut: at directionType=0 sixteen measurements returned ONE distinct ΔV).
        definition.directionType = direction;
        var depth = DepthOf(command);

        // forward=true means "extrude to the side the sketch normal points at". dtReverse=1 already says
        // "the other side", so forward=true unconditionally is a contradiction: measured on v24 before the
        // fix, direction=negative answered Create()=false and the feature never appeared (GEOMETRY_FAILED).
        // The same guard was needed on ConfigureBoss and is harmless-but-not-required on ConfigureCut.
        // History: docs/decisions/adapter-core.md#extrude-forward-side
        var forward = direction != 1;
        if (!definition.SetSideParam(forward, EndConditionBlind, depth, 0, false))
        {
            throw Refused(forward ? "SetSideParam" : "SetSideParam(reverse)", command);
        }

        if (direction == 2 && !definition.SetSideParam(false, EndConditionBlind, depth, 0, false))
        {
            throw Refused("SetSideParam(back)", command);
        }

        return true;
    }

    private bool ConfigureBoss(
        ksBossExtrusionDefinition definition,
        ksEntity sketch,
        short direction,
        ExtrudeCommand command,
        BodyTarget? bodyTarget,
        List<string> selectorEvidence,
        DocumentEntry document)
    {
        // The choice is made before SetSketch: that is the order probe P2.6 recorded as the minimal working
        // sequence, and the opposite order produced the same ΔV, so this is documented, not a superstition.
        ApplyBodyChoice(command, bodyTarget, selectorEvidence, type => definition.chooseType = type,
            () => definition.chooseType, () => definition.ChooseBodies());

        AttachSketch(() => definition.SetSketch(sketch), document, command);

        definition.directionType = direction;
        var depth = DepthOf(command);

        // Same contradiction as ConfigureBase: a reverse direction (1) must not be combined with
        // forward=true, or Create() answers false and the feature never appears. Kept in step with
        // ConfigureBase deliberately — diverging them is how the base defect survived in the first place.
        var forward = direction != 1;
        if (!definition.SetSideParam(forward, EndConditionBlind, depth, 0, false))
        {
            throw Refused(forward ? "SetSideParam" : "SetSideParam(reverse)", command);
        }

        if (direction == 2 && !definition.SetSideParam(false, EndConditionBlind, depth, 0, false))
        {
            throw Refused("SetSideParam(back)", command);
        }

        return true;
    }

    private bool ConfigureCut(
        ksCutExtrusionDefinition definition,
        ksEntity sketch,
        short direction,
        ExtrudeCommand command,
        BodyTarget? bodyTarget,
        List<string> selectorEvidence,
        DocumentEntry document)
    {
        ApplyBodyChoice(command, bodyTarget, selectorEvidence, type => definition.chooseType = type,
            () => definition.chooseType, () => definition.ChooseBodies());

        AttachSketch(() => definition.SetSketch(sketch), document, command);

        definition.directionType = direction;

        // Measured on v24 (probe P2.1): etThroughAll is honoured only with directionType=symmetric, and the
        // depth is discarded by the solver in that mode (1 mm and 1000 mm cut identically), so 0 is passed.
        // The Host refuses through-mode with any other direction or operation.
        var through = command.EndCondition == ExtrudeEndCondition.Through;
        var endType = through ? EndConditionThrough : EndConditionBlind;
        var depth = through ? 0d : DepthOf(command);

        // The forward call is skipped for dtReverse=1 for the same reason as ConfigureBase and ConfigureBoss.
        // Measured, and stated honestly: unlike boss, cut did NOT need this change — it already answered
        // correctly for direction=negative, because the unconditional second SetSideParam(false, ...) below
        // supplied the reverse side on its own. The guard is kept so the three operations read the same way
        // and the reason cut works is a stated rule, not an accident of call order.
        var forward = direction != 1;
        if (!definition.SetSideParam(forward, endType, depth, 0, false))
        {
            throw Refused(forward ? "SetSideParam" : "SetSideParam(reverse)", command);
        }

        if (!definition.SetSideParam(false, endType, depth, 0, false))
        {
            throw Refused("SetSideParam(back)", command);
        }

        return true;
    }

    /// <summary>Tells the kernel which body the operation must act on, by the route measured in probe P2.6:
    /// <c>def.chooseType = ksChBodies(3)</c>; <c>cb = def.ChooseBodies()</c> (<c>ksChooseBodies</c> has only
    /// <c>BodyCollection()</c> and <c>ChooseBodiesType</c> — no <c>Add</c>); <c>cb.ChooseBodiesType =
    /// ksManualEditing(2)</c>;
    /// <c>((ksBodyCollection)cb.BodyCollection()).Add(&lt;raw element of part.BodyCollection()&gt;)</c>. If
    /// <c>Add</c> rejects the body the operation is refused before <c>Create</c>.
    /// History: docs/decisions/adapter-core.md#apply-body-choice
    /// </summary>
    private static void ApplyBodyChoice(
        ExtrudeCommand command,
        BodyTarget? bodyTarget,
        List<string> evidence,
        Action<int> setChooseType,
        Func<int> readChooseType,
        Func<object?> getChooseBodies)
    {
        if (bodyTarget is null)
        {
            // Nothing was declared. For boss and cut the adapter refuses that earlier, so this is the
            // base-extrusion path — and ksBaseExtrusionDefinition exposes no selector at all.
            return;
        }

        setChooseType(KompasChoose.Bodies);
        var afterSet = readChooseType();
        evidence.Add($"chooseType := {KompasChoose.Bodies} прочитано {afterSet}");

        var chosen = getChooseBodies();
        if (chosen is not ksChooseBodies chooseBodies)
        {
            throw SelectorUnavailable(command, bodyTarget, $"ChooseBodies() вернул {chosen?.GetType().Name ?? "null"} вместо ksChooseBodies", evidence);
        }

        chooseBodies.ChooseBodiesType = KompasChoose.ManualEditing;
        evidence.Add($"ChooseBodiesType := {KompasChoose.ManualEditing} прочитано {chooseBodies.ChooseBodiesType}");

        if (chooseBodies.BodyCollection() is not ksBodyCollection collection)
        {
            throw SelectorUnavailable(command, bodyTarget, "ChooseBodies().BodyCollection() вернул null или не ksBodyCollection", evidence);
        }

        var added = collection.Add(bodyTarget.RawElement);
        evidence.Add($"Add(элемент BodyCollection[{bodyTarget.Index}]) → {(added ? "true" : "false")}, счётчик {collection.GetCount()}");
        if (!added || collection.GetCount() < 1)
        {
            throw SelectorUnavailable(command, bodyTarget, $"ksBodyCollection.Add отверг тело {bodyTarget.Index} (ответ {added})", evidence);
        }
    }

    private static KompasContractException SelectorUnavailable(
        ExtrudeCommand command,
        BodyTarget bodyTarget,
        string reason,
        List<string> evidence) => new(
        ErrorCodes.GeometryFailed,
        $"Целевое тело не принято КОМПАС: {reason}; {string.Join("; ", evidence)}. " +
        "Признак с незапрошенным целевым телом не создаётся: он вырезал бы не то тело, а КОМПАС об этом не сообщил бы.",
        RetryPolicy.ReacquireContext,
        partialEffects: true,
        details: new Dictionary<string, object?>
        {
            ["target_body_ref"] = command.TargetBodyRef,
            ["target_body_index"] = bodyTarget.Index,
            ["reason"] = reason,
            ["selector_evidence"] = evidence.ToArray(),
            ["code"] = "target_body_selector_refused",
        });

    /// <summary>Depth for the blind condition. The Host enforces the mode rule before COM; this guard keeps
    /// a hand-built IPC frame from silently extruding by zero.</summary>
    private static double DepthOf(ExtrudeCommand command) =>
        command.DepthMm ?? throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Для end_condition=blind поле depth_mm обязательно.");

    /// <summary>A refusal of one step of the extrusion's configuration, as the client reads it.
    /// <para>INVARIANT: a refusal that can name the profile's defect names it FIRST, and calls it what it
    /// is — the server's analysis of the INPUT. It is never presented as the kernel's reason: the kernel
    /// answers a bare false here and names nothing (the code below is the library-program result, read
    /// only where the caller actually read it).</para>
    /// History: docs/decisions/adapter-sketch.md#set-sketch-refusal</summary>
    private KompasContractException Refused(
        string step,
        ExtrudeCommand command,
        DocumentEntry? document = null,
        int? kompasResultCode = null,
        string? kompasResultText = null,
        string? kompasResultUnavailable = null)
    {
        var reads = ReadSketchAndProfile(command.SketchRef);
        var details = new Dictionary<string, object?>
        {
            ["step"] = step,
            ["depth_mm"] = command.DepthMm,
            ["end_condition"] = command.EndCondition.ToString().ToLowerInvariant(),
            // The same reads the Create()=false snapshot carries, in the part that applies BEFORE a feature
            // exists: what the model says about the sketch and what the server knows about the profile it
            // drew. A route that could not answer is NAMED, never dropped.
            ["sketch_state"] = reads.State
                ?? (reads.StateUnavailable ?? "не прочитано: причина не названа"),
            ["sketch_profile_entities"] = reads.ProfileEntities
                ?? (object)"не прочитано: адаптер не рисовал этот эскиз",
            ["sketch_profile_area_mm2"] = reads.ProfileArea
                ?? (object)(reads.ProfileAreaUnavailable ?? "не прочитано: причина не названа"),
            ["sketch_profile_area_unavailable"] = reads.ProfileAreaUnavailable
                ?? (object)"причина не названа: площадь вычислена либо профиль не записан",
        };

        if (document is not null)
        {
            details["kompas_result_code"] = kompasResultCode.HasValue
                ? kompasResultCode.Value
                : kompasResultUnavailable ?? "не прочитано: причина не названа";
            details["kompas_result_text"] = string.IsNullOrWhiteSpace(kompasResultText)
                ? "КОМПАС кода ошибки не вернул — текста нет"
                : kompasResultText;
        }

        var profile = reads.ProfileAreaUnavailable is null
            ? "Анализ входа сервером: профиль эскиза записан этим сеансом, площадь вычислена — "
                + "самопересечения и разрывов в нём нет."
            : "Анализ входа сервером: " + reads.ProfileAreaUnavailable.TrimEnd('.', ' ') + ".";
        if (reads.ProfileEntities is null)
        {
            profile = "Профиль эскиза этим сеансом не записан — сервер о нём ничего не знает и "
                + "догадываться не будет.";
        }

        var kernel = document is null
            ? string.Empty
            : " " + RefusalReason(kompasResultCode, kompasResultText, kompasResultUnavailable);

        return new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"Выдавливание не настроено: {step} вернул false (operation={command.Operation}, "
            + $"end_condition={command.EndCondition.ToString().ToLowerInvariant()}, "
            + $"depth={FormatDepth(command)}, direction={command.Direction}). "
            + profile + kernel
            + " Частая причина — профиль эскиза пуст или не замкнут."
            + " Состояние эскиза и профиля на момент отказа — в details.",
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: details);
    }

    /// <summary>Attaches the sketch to an extrusion definition, reading the kernel's own library-program
    /// result code around the call so a refusal can say whether the kernel named anything at all.
    /// DOC: KompasObject::ksReturnResult — «Код ошибки … при выполнении библиотечной программы»
    /// (kompasobject_ksreturnresult.html). The help does not tie the code to one method, so the same
    /// documented route is used here as for a refused Create(); the code is cleared first so what is read
    /// belongs to THIS call, and a zero is reported as "no code returned", never as a cause.
    /// History: docs/decisions/adapter-sketch.md#set-sketch-refusal</summary>
    private void AttachSketch(Func<bool> setSketch, DocumentEntry document, ExtrudeCommand command)
    {
        ClearKompasResult(document);
        if (setSketch())
        {
            return;
        }

        var (code, text, unavailable) = ReadKompasResult(document);
        throw Refused("SetSketch", command, document, code, text, unavailable);
    }

    private static string FormatDepth(ExtrudeCommand command) =>
        command.DepthMm is double depth ? $"{depth:0.###} мм" : "не задана (насквозь)";

    /// <summary>What is read about the document, the sketch and the session at a refused
    /// <c>Create()</c>.</summary>
    /// <remarks>INVARIANT: every route here is a READ; the only state this method changes is its own
    /// counters. A route that cannot answer is NAMED inside the snapshot rather than dropped, so the same
    /// keys appear on every refusal. The reason itself comes from the KOMPAS result code, read by the
    /// caller right after the refused call (see <see cref="ReadKompasResult"/>).
    /// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
    private FeatureCreateFailure.Observation ObserveRefusedCreate(
        DocumentEntry document,
        ExtrudeCommand command,
        string operationName,
        short directionType,
        int ordinal,
        int? kompasResultCode,
        string? kompasResultText,
        string? kompasResultUnavailable)
    {
        _extrudeCreateFalseCount++;

        var through = command.EndCondition == ExtrudeEndCondition.Through;
        var endCondition = (through ? "through" : "blind")
            + $" ({(through ? EndConditionThrough : EndConditionBlind)})";

        var reads = ReadSketchAndProfile(command.SketchRef);

        return new FeatureCreateFailure.Observation(
            Operation: operationName,
            DirectionType: directionType,
            EndCondition: endCondition,
            DepthMm: through ? null : command.DepthMm,
            DraftMm: null,
            TargetBodyRef: command.TargetBodyRef,
            SketchPlane: _sketchPlaneHint.TryGetValue(command.SketchRef, out var hint) ? hint : null,
            FeatureCount: TryCountFeatures(document),
            BodyCount: TryCountBodies(document),
            SketchState: reads.State,
            SketchStateUnavailable: reads.StateUnavailable,
            SketchProfileEntities: reads.ProfileEntities,
            SketchProfileAreaMm2: reads.ProfileArea,
            SketchProfileAreaUnavailable: reads.ProfileAreaUnavailable,
            KompasResultCode: kompasResultCode,
            KompasResultText: kompasResultText,
            KompasResultUnavailable: kompasResultUnavailable,
            SessionSeconds: _sessionClock.Elapsed.TotalSeconds,
            OperationOrdinal: ordinal,
            CreateFalseCount: _extrudeCreateFalseCount);
    }

    /// <summary>What a refusal can say about the sketch and the profile the server drew into it.
    /// <paramref name="State"/> is the state read out of the model, <paramref name="StateUnavailable"/> the
    /// reason when that read failed; the profile numbers come from session memory, and their absence means
    /// this session did not draw the sketch — which is said out loud, not guessed around.
    /// INVARIANT: every route here is a READ; nothing here changes the model.
    /// History: docs/decisions/adapter-sketch.md#set-sketch-refusal</summary>
    private (
        string? State,
        string? StateUnavailable,
        int? ProfileEntities,
        double? ProfileArea,
        string? ProfileAreaUnavailable) ReadSketchAndProfile(string sketchRef)
    {
        string? sketchState = null;
        string? sketchStateUnavailable = null;
        try
        {
            var status = GetSketchStatus(new GetSketchStatusCommand { SketchRef = sketchRef });
            sketchState = $"{status.DefinitionStatus} ({status.NativeStateName ?? "имя не объявлено"}, "
                + $"raw={status.RawState?.ToString() ?? "null"})";
        }
        catch (KompasContractException ex)
        {
            sketchStateUnavailable = "не прочитано: " + ex.Message;
        }

        int? profileEntities = null;
        double? profileArea = null;
        string? profileAreaUnavailable = null;
        if (_sketchProfiles.TryGetValue(sketchRef, out var profile))
        {
            profileEntities = profile.Entities.Count;
            var outcome = profile.Outcome;
            profileArea = outcome.AreaMm2;
            profileAreaUnavailable = outcome.UnavailableReason;
        }

        return (sketchState, sketchStateUnavailable, profileEntities, profileArea, profileAreaUnavailable);
    }

    /// <summary>Clear a previous NON-FATAL KOMPAS error before a call whose code will be read.</summary>
    /// <remarks>DOC: KompasObject::ksResultNULL (kompasobject_ksresultnull.html) - «Обнулить результат
    /// работы библиотеки, если ошибка не фатальная»; returns 1 when an error was cleared. Without it a
    /// code left by an earlier call would be read as this refusal's reason.
    /// INVARIANT: a failed reset is not fatal and is not reported as one - the code read afterwards is
    /// then merely less trustworthy, and ksStrResult's note says it clears the non-fatal flag too.
    /// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
    private void ClearKompasResult(DocumentEntry document)
    {
        try
        {
            RequireApplication(document.ApplicationId).Application.ksResultNULL();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            // Nothing to report here: the refusal itself is what the caller must see, and an unreadable
            // reset only weakens the code read next - it does not make the operation fail differently.
        }
    }

    /// <summary>The KOMPAS result code and message of a call that has just returned.</summary>
    /// <remarks>DOC: KompasObject::ksReturnResult and KompasObject::ksStrResult
    /// (help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksreturnresult.html,
    /// .../kompasobject_ksstrresult.html) - "Код ошибки ... при выполнении библиотечной программы",
    /// "Ошибка с номером >0 не является фатальной. Отрицательный номер ошибки приводит к завершению
    /// программы", text via ksStrResult. INVARIANT: an unreadable route returns a REASON, never a zero -
    /// "KOMPAS returned no code" and "we could not read the code" are different facts.
    /// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
    private (int? Code, string? Text, string? Unavailable) ReadKompasResult(DocumentEntry document)
    {
        try
        {
            var application = RequireApplication(document.ApplicationId).Application;
            var code = application.ksReturnResult();
            string? text;
            try
            {
                text = application.ksStrResult();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                text = null;
            }

            return (code, text, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or KompasContractException)
        {
            return (null, null, "не прочитано: " + ex.Message);
        }
    }

    /// <summary>The refusal's reason line as the client sees it. INVARIANT: "ПРИЧИНА НЕ УСТАНОВЛЕНА" is
    /// kept ONLY for the case where KOMPAS returned no code at all - a named code is the cause, and saying
    /// "not established" next to it would be false.</summary>
    private static string RefusalReason(int? code, string? text, string? unavailable)
    {
        if (code is int named && named != 0)
        {
            var message = string.IsNullOrWhiteSpace(text) ? string.Empty : $" («{text.Trim()}»)";
            return $"КОМПАС вернул код ошибки {named}{message}.";
        }

        if (code == 0)
        {
            return "ПРИЧИНА НЕ УСТАНОВЛЕНА: КОМПАС кода ошибки не вернул (ksReturnResult = 0).";
        }

        return "ПРИЧИНА НЕ УСТАНОВЛЕНА: код ошибки КОМПАС прочитать не удалось ("
            + (unavailable ?? "причина не названа") + ").";
    }

    /// <summary>Feature count that keeps "could not be read" apart from "zero features".</summary>
    /// <remarks>Why not <see cref="CountFeatures"/>: it answers 0 when the collection refuses, and in this
    /// snapshot 0 is a legitimate reading — merging the two would make an unread count look measured.
    /// The route is the same one <c>kompas_get_context</c> uses, including the refresh.</remarks>
    private int? TryCountFeatures(DocumentEntry document)
    {
        try
        {
            var collection = (ksEntityCollection)document.PartNow()
                .EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement));
            collection.refresh();
            return collection.GetCount();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Body count that keeps "could not be read" apart from "no bodies".</summary>
    /// <remarks><see cref="CountBodies"/> already answers -1 on failure; that value is turned into null
    /// here so the snapshot does not publish a number that only means "not read".</remarks>
    private int? TryCountBodies(DocumentEntry document)
    {
        var count = CountBodies(document);
        return count < 0 ? null : count;
    }

    /// <summary>Extent of the material a through operation traverses, in mm, along the axis the sketch is
    /// normal to. Null when the plane resolves to none of the three model axes: the expectation then stays
    /// "not_computable" rather than comparing a measurement with an invented number.</summary>
    /// <remarks><paramref name="material"/> is the caller-declared body; falling back to <c>GetMainBody()</c>
    /// is kept for operations that name no target, and is why a through cut aimed at a second body was
    /// unverifiable.
    /// History: docs/decisions/adapter-core.md#through-extent-mm</remarks>
    private static double? ThroughExtentMm(DocumentEntry document, ksEntity sketch, ksBody? material)
    {
        try
        {
            var body = material
                ?? (document.PartNow().GetMainBody() is ksBody mainBody ? mainBody : null);
            if (sketch.GetDefinition() is not ksSketchDefinition definition
                || definition.GetPlane() is not ksEntity plane
                || PlaneNormalAxis(plane) is not int axis
                || body is null
                || !body.GetGabarit(out double x1, out double y1, out double z1,
                                    out double x2, out double y2, out double z2))
            {
                return null;
            }

            return axis switch
            {
                0 => Math.Abs(x2 - x1),
                1 => Math.Abs(y2 - y1),
                _ => Math.Abs(z2 - z1),
            };
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Model axis a plane entity is normal to: o3d_planeXOY→z, o3d_planeXOZ→y, o3d_planeYOZ→x; an
    /// offset plane is resolved through its base plane. A constructed plane or a face carrier deliberately
    /// returns null — guessing its axis would silently redefine what "through" means.</summary>
    private static int? PlaneNormalAxis(ksEntity plane)
    {
        var entity = plane;

        // Two hops cover a plain offset plane; anything deeper is not a case this server creates.
        for (var hop = 0; hop < 2 && entity is not null; hop++)
        {
            switch (entity.type)
            {
                case KompasObjectTypes.PlaneXoy:
                    return 2;
                case KompasObjectTypes.PlaneXoz:
                    return 1;
                case KompasObjectTypes.PlaneYoz:
                    return 0;
                case KompasObjectTypes.PlaneOffset
                    when entity.GetDefinition() is ksPlaneOffsetDefinition offset:
                    entity = offset.GetPlane() as ksEntity;
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>End-condition <c>etBlind</c> — extrusion "by a given amount".</summary>
    private const short EndConditionBlind = 0;

    /// <summary>End-condition <c>ksEndTypeEnum.etThroughAll</c> — cut through all material.</summary>
    private const short EndConditionThrough = 1;

    /// <summary>Unwraps an edge into a <c>ksEntity</c> for the fillet collection; which of the three routes
    /// works is MEASURED (probe P2.2), returned so the answer names the route. Fillet route:
    /// <c>NewEntity(o3d_fillet=34)</c> → <c>ksFilletDefinition</c> → radius/tangent → <c>array()</c> as
    /// <c>ksEntityCollection</c> → <c>Add(edgeEntity)</c> → <c>Create()</c>; success is not the HRESULT — the
    /// radius is re-read from a fresh definition and the face count must grow by the number of edges.
    /// History: docs/decisions/adapter-core.md#fillet-route-and-unwrap-edge</summary>
    private static (ksEntity Entity, string Route) UnwrapEdgeToEntity(ksEdgeDefinition edge, string reference)
    {
        if (edge is ksEntity asEntity)
        {
            return (asEntity, "уже ksEntity");
        }

        if (edge.GetEntity() is ksEntity owned)
        {
            return (owned, "edge.GetEntity()");
        }

        if (edge.GetOwnerEntity() is ksEntity parent)
        {
            return (parent, "edge.GetOwnerEntity()");
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"Ребро '{reference}' не развернулось в ksEntity ни одним из трёх маршрутов.",
            RetryPolicy.ReacquireContext,
            partialEffects: true);
    }

    public FilletResult Fillet(FilletCommand command)
    {
        if (command.EdgeRefs is not { Count: > 0 } edgeRefs)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs пуст — скруглению нужно сказать, какие рёбра брать.");
        }

        if (!References.TryGet(edgeRefs[0], out var anchor) || anchor is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{edgeRefs[0]}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(anchor.DocumentId);
        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        var entities = new List<ksEntity>();
        var unwrapRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in edgeRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is not ksEdgeDefinition edge)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' указывает не на ребро (kind={stored.Kind}).",
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            // The fillet collection takes entities, not definition interfaces, and which of the three
            // unwrappings yields one was measured (probe P2.2). Each route is tried once and named.
            var unwrapped = UnwrapEdgeToEntity(edge, reference);
            entities.Add(unwrapped.Entity);
            unwrapRoutes.Add(unwrapped.Route);
        }

        var feature = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(KompasObjectTypes.Fillet));
        if (feature.GetDefinition() is not ksFilletDefinition definition)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Определение скругления вернуло {feature.GetDefinition()?.GetType().Name ?? "null"} вместо ksFilletDefinition.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        definition.radius = command.RadiusMm;
        definition.tangent = false;

        if (definition.array() is not ksEntityCollection array)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksFilletDefinition.array() вернул не ksEntityCollection: рёбра подать некуда.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var added = 0;
        foreach (var entity in entities)
        {
            if (array.Add(entity))
            {
                added++;
            }
        }

        if (added != entities.Count)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Скругление не настроено: array().Add принял {added} рёбер из {entities.Count}.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["added"] = added, ["requested"] = entities.Count });
        }

        if (!feature.Create())
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Entity.Create() скругления вернул false: признак не появился.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        document.Document3D.RebuildDocument();
        BumpRevision(document, "fillet");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        // Read the radius back through a NEW definition object: the one held during configuration
        // could be a cached RCW, and "we set it" is not evidence that the model stored it.
        var radiusReadBack = (feature.GetDefinition() as ksFilletDefinition)?.radius;

        var checks = new List<NamedCheck>
        {
            new("edges_applied", added == entities.Count,
                Observed: added.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Expected: entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("radius_read_back", radiusReadBack is double read && Math.Abs(read - command.RadiusMm) <= 1e-6,
                Observed: radiusReadBack?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: command.RadiusMm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
            new("face_count_grew", facesAfter is double after && facesBefore is double before
                && after - before == entities.Count,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: $"+{entities.Count}"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        var volumeDecreased = volumeBefore is double beforeVolume && volumeAfter is double afterVolume
            && afterVolume < beforeVolume;
        bool? numericMatch = null;
        var volumePairRead = volumeBefore is double && volumeAfter is double;
        if (command.ExpectedVolumeDeltaMm3 is double expectedDelta
            && volumeBefore is double vBefore && volumeAfter is double vAfter)
        {
            var measured = vBefore - vAfter;
            numericMatch = Math.Abs(measured - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
            checks.Add(new NamedCheck(
                "volume_delta",
                numericMatch.Value,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (volumeBefore is double b && volumeAfter is double a)
        {
            // Direction only, and only when BOTH volumes were read: an unread pair is not a failed
            // check, it is an unperformed one (the reason travels in unverified_aspects).
            checks.Add(new NamedCheck(
                "volume_delta",
                volumeDecreased,
                Observed: (b - a).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: "не задано — проверено только направление"));
        }

        // Geometry is confirmed when the stored parameter reads back, the topology grew by exactly
        // the filleted edges, the material went down, and — if the caller supplied one — the
        // analytic volume matched. Anything less stays "call_returned" with the reason attached.
        var geometryConfirmed = radiusReadBack is double confirmed
            && Math.Abs(confirmed - command.RadiusMm) <= 1e-6
            && checks.Exists(c => c.Name == "face_count_grew" && c.Passed)
            && volumeDecreased
            && numericMatch is not false;

        var unverified = new List<string>();
        if (!volumePairRead)
        {
            unverified.Add(
                "volume_delta_not_computable — объём до или после операции не прочитан: ни аналитическое "
                + "ожидание, ни направление изменения материала проверить нечем");
        }
        else if (!geometryConfirmed)
        {
            unverified.Add("volume_delta_not_confirmed — КОМПАС сообщил об успехе, но измерение не подтвердило ожидаемую геометрию");
        }
        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add("expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал вызывающий, численного доказательства нет");
        }

        var reference2 = References.Register("feature", document.Id, document.Revision, feature);
        return new FilletResult(
            ToDto(reference2, $"fillet r{command.RadiusMm:0.###} × {entities.Count}"),
            entities.Count,
            radiusReadBack,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", unwrapRoutes));
    }

    /// <summary>Chamfer over the explicitly referenced edges of the final body (docs/05 SM-11).</summary>
    /// <remarks>Route MEASURED on v24: <c>NewEntity(o3d_chamfer=33)</c> → <c>ksChamferDefinition</c> →
    /// <c>SetChamferParam(transfer, d1, d2)</c> → <c>array()</c> as <c>ksEntityCollection</c> →
    /// <c>Add(edge)</c> → <c>Create()</c> → <c>RebuildDocument()</c>. Success is not <c>Create()</c>:
    /// parameter, face count and volume are re-read; a zero leg is refused before COM (F.12).
    /// History: docs/decisions/adapter-core.md#chamfer-route</remarks>
    public ChamferResult Chamfer(ChamferCommand command)
    {
        if (command.EdgeRefs is not { Count: > 0 } edgeRefs)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs пуст — фаске нужно сказать, какие рёбра брать.");
        }

        ValidateChamferMode(command);

        if (!References.TryGet(edgeRefs[0], out var anchor) || anchor is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{edgeRefs[0]}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(anchor.DocumentId);
        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        var entities = new List<ksEntity>();
        var unwrapRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in edgeRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is not ksEdgeDefinition edge)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' указывает не на ребро (kind={stored.Kind}).",
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            // As with the fillet: the feature collection accepts an entity, not a definition interface, and
            // which of the three unwrappings yields one is MEASURED (P2.2), not assumed. Each route is named
            // in the answer.
            var unwrapped = UnwrapEdgeToEntity(edge, reference);
            entities.Add(unwrapped.Entity);
            unwrapRoutes.Add(unwrapped.Route);
        }

        if (command.Mode == ChamferMode.DistanceAngle)
        {
            // ksChamferDefinition has no angle at all (MEASURED by probe F), so this mode goes by the only
            // known route — IChamfer.Angle in API7 of the same session.
            return ChamferByAngle(
                document, entities, command, volumeBefore, facesBefore, bodiesBefore, unwrapRoutes);
        }

        var feature = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(KompasObjectTypes.Chamfer));
        if (feature.GetDefinition() is not ksChamferDefinition definition)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Определение фаски вернуло {feature.GetDefinition()?.GetType().Name ?? "null"} вместо ksChamferDefinition.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var distance2 = command.Distance2Mm ?? command.Distance1Mm;
        if (!definition.SetChamferParam(command.Direction, command.Distance1Mm, distance2))
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetChamferParam вернул false: катеты не приняты, признак не создавался.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        if (definition.array() is not ksEntityCollection array)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksChamferDefinition.array() вернул не ksEntityCollection: рёбра подать некуда.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var added = 0;
        foreach (var entity in entities)
        {
            if (array.Add(entity))
            {
                added++;
            }
        }

        if (added != entities.Count)
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Фаска не настроена: array().Add принял {added} рёбер из {entities.Count}.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["added"] = added, ["requested"] = entities.Count });
        }

        if (!feature.Create())
        {
            // The feature's error number is what KOMPAS reports for the reason this very creation failed
            // (objectError was MEASURED by probe L as a readable feature-state field); returning
            // "Create()=false" silently without it would leave the caller guessing.
            var objectError = (feature.GetFeature() as ksFeature)?.objectError;
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Entity.Create() фаски вернул false: признак не появился." +
                (objectError is int code && code != 0 ? $" objectError={code}." : string.Empty),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["object_error"] = objectError });
        }

        document.Document3D.RebuildDocument();
        BumpRevision(document, "chamfer");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        // Re-read from a NEW definition object: the one written through may be a cached view, and "we called
        // SetChamferParam" is not evidence that the model stored it (the same standard as G03 and the support
        // edit in L11).
        var readBack = (feature.GetDefinition() as ksChamferDefinition) is var fresh && fresh is not null
            ? ReadChamferParam(fresh)
            : null;

        var checks = new List<NamedCheck>
        {
            new("edges_applied", added == entities.Count,
                Observed: added.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Expected: entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("parameters_read_back", readBack is not null
                && Math.Abs(readBack.Distance1Mm - command.Distance1Mm) <= 1e-6
                && Math.Abs(readBack.Distance2Mm - distance2) <= 1e-6
                && readBack.Transfer == command.Direction,
                Observed: readBack is null
                    ? "GetChamferParam не читается"
                    : $"transfer={readBack.Transfer} d1={readBack.Distance1Mm:0.####} d2={readBack.Distance2Mm:0.####}",
                Expected: $"transfer={command.Direction} d1={command.Distance1Mm:0.####} d2={distance2:0.####}"),
            new("face_count_grew", facesAfter is double after && facesBefore is double before
                && after - before == entities.Count,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: $"+{entities.Count}"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        var materialRemoved = volumeBefore is double b && volumeAfter is double a && a < b;
        var volumePairRead = volumeBefore is double && volumeAfter is double;
        bool? numericMatch = null;
        if (command.ExpectedVolumeDeltaMm3 is double expectedDelta
            && volumeBefore is double vBefore && volumeAfter is double vAfter)
        {
            var measured = vBefore - vAfter;
            numericMatch = Math.Abs(measured - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
            checks.Add(new NamedCheck(
                "volume_delta",
                numericMatch.Value,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (volumeBefore is double cb && volumeAfter is double ca)
        {
            // Direction only, and only when BOTH volumes were read: an unread pair is an unperformed
            // check, not a failed one.
            checks.Add(new NamedCheck(
                "volume_delta",
                materialRemoved,
                Observed: (cb - ca).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: "не задано — проверено только направление"));
        }

        var parametersStored = readBack is not null
            && Math.Abs(readBack.Distance1Mm - command.Distance1Mm) <= 1e-6
            && Math.Abs(readBack.Distance2Mm - distance2) <= 1e-6;
        var geometryConfirmed = parametersStored
            && checks.Exists(c => c.Name == "face_count_grew" && c.Passed)
            && materialRemoved
            && numericMatch is not false;

        var unverified = new List<string>();
        if (!volumePairRead)
        {
            unverified.Add(
                "volume_delta_not_computable — объём до или после операции не прочитан: ни аналитическое "
                + "ожидание, ни направление изменения материала проверить нечем");
        }
        else if (!geometryConfirmed)
        {
            unverified.Add("volume_delta_not_confirmed — КОМПАС сообщил об успехе, но измерение не подтвердило ожидаемую геометрию");
        }

        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add("expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал вызывающий, численного доказательства нет");
        }

        var reference2 = References.Register("feature", document.Id, document.Revision, feature);
        return new ChamferResult(
            ToDto(reference2, $"chamfer {command.Distance1Mm:0.###}×{distance2:0.###} × {entities.Count}"),
            entities.Count,
            readBack?.Distance1Mm,
            readBack?.Distance2Mm,
            readBack?.Transfer,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", unwrapRoutes));
    }

    /// <summary>"Field ↔ mode" rules. Rejected before COM: KOMPAS accepts a zero leg and creates a feature
    /// with zero faces at unchanged volume (probe F.12), so an "invalid parameter" must reach us rather than
    /// become silently applied geometry.</summary>
    private static void ValidateChamferMode(ChamferCommand command)
    {
        if (command.Distance1Mm <= 0d || command.Distance2Mm is <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Катеты фаски обязаны быть положительными: КОМПАС принимает ноль и создаёт признак " +
                "с нулевыми гранями при неизменном объёме (измерено пробой F.12), а это не фаска.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["distance1_mm"] = command.Distance1Mm,
                    ["distance2_mm"] = command.Distance2Mm,
                });
        }

        switch (command.Mode)
        {
            case ChamferMode.TwoDistances when command.AngleDeg is not null:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "angle_mm здесь негде быть: при mode=two_distances угол не задаётся. " +
                    "Способ «расстояние и угол» — mode=distance_angle.",
                    RetryPolicy.Never);
            case ChamferMode.DistanceAngle when command.AngleDeg is not double angle
                                               || angle <= 0d || angle >= 90d:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "При mode=distance_angle нужен angle_deg в интервале (0; 90): вне его фаска " +
                    "либо вырождается в грань, либо не строится вовсе.",
                    RetryPolicy.Never);
            case ChamferMode.DistanceAngle when command.Distance2Mm is not null:
                // The second leg cannot be expressed in this mode: API7 takes Distance1 and Angle. Accepting a
                // number and ignoring it would pass off as applied a parameter that was requested and not
                // obtained.
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "При mode=distance_angle поле distance2_mm запрещено: сторону задаёт angle_deg " +
                    "(второй катет = distance1·tg угла, измерено пробой F.10).",
                    RetryPolicy.Never);
        }
    }

    /// <summary>Legs and side flag re-read from the definition. A record, not a tuple: "did not read" must
    /// differ from "read as zero", and a nullable tuple cannot express that.</summary>
    private sealed record ChamferParam(bool Transfer, double Distance1Mm, double Distance2Mm);

    private static ChamferParam? ReadChamferParam(ksChamferDefinition definition)
    {
        try
        {
            if (!definition.GetChamferParam(out var transfer, out var distance1, out var distance2))
            {
                return null;
            }

            return new ChamferParam(transfer, distance1, distance2);
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }

    /// <summary>Face count of the final main body; null when the body cannot be read.</summary>
    private static double? CountFaces(DocumentEntry document)
    {
        try
        {
            return document.PartNow().GetMainBody() is ksBody body
                && body.FaceCollection() is ksFaceCollection faces
                    ? faces.GetCount()
                    : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public sealed record FilletResult(
        ReferenceDto FeatureRef,
        int EdgeCount,
        double? RadiusReadBackMm,
        int BodyCount,
        double? VolumeMm3,
        VerificationDto Verification,
        string EdgeUnwrapRoute);

    /// <summary>Chamfer result. The legs are returned re-read from the model, not as passed: "we called
    /// SetChamferParam" is not a geometric fact. <c>feature_ref</c> is empty when the feature was created but
    /// is not visible in the API5 tree.</summary>
    public sealed record ChamferResult(
        ReferenceDto? FeatureRef,
        int EdgeCount,
        double? Distance1ReadBackMm,
        double? Distance2ReadBackMm,
        bool? TransferReadBack,
        int BodyCount,
        double? VolumeMm3,
        VerificationDto Verification,
        string EdgeUnwrapRoute);

    public RebuildResult Rebuild(RebuildCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (!document.Document3D.RebuildDocument())
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "RebuildDocument() вернул false.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document3D.UpdateDocumentParam();
        // A rebuild is exactly the case where handles from the previous revision must stop
        // resolving: the solver may have produced different faces and edges.
        BumpRevision(document, "rebuild", invalidateAll: true);
        return new RebuildResult(document.Revision, Context(document, includeTopology: false));
    }

    // ---------------------------------------------------------------------------------------------
    // Reading structure and geometry
    // ---------------------------------------------------------------------------------------------

    public IReadOnlyList<FeatureRowDto> ListFeatures(ListFeaturesCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var rows = new List<FeatureRowDto>();

        var collection = (ksEntityCollection)document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement));
        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (collection.GetByIndex(i) is not ksEntity entity)
            {
                continue;
            }

            rows.Add(new FeatureRowDto
            {
                FeatureRef = References.Register("feature", document.Id, document.Revision, entity).Id,
                Type = entity.type.ToString(),
                Name = entity.name ?? string.Empty,
                State = SafeIsCreated(entity),
                SketchRef = SketchRefOfFeature(document, entity),
            });
        }

        return rows;
    }

    /// <summary>Reference to the sketch an extrusion is built on, or null when the feature has none or the
    /// read-back fails.</summary>
    /// <remarks>The reference is the session's own live one for that sketch when it has one, and a
    /// freshly minted one otherwise: a handle stored before a rebuild is dropped by the registry, and
    /// minting on demand is what makes the row usable on a reopened document. A failure to read the
    /// sketch is reported as null, not an error — <c>kompas_list_features</c>
    /// must keep listing a document whose features it cannot fully describe.</remarks>
    private string? SketchRefOfFeature(DocumentEntry document, ksEntity entity)
    {
        try
        {
            var sketch = entity.GetDefinition() switch
            {
                ksBaseExtrusionDefinition b => b.GetSketch() as ksEntity,
                ksBossExtrusionDefinition s => s.GetSketch() as ksEntity,
                ksCutExtrusionDefinition c => c.GetSketch() as ksEntity,
                _ => null,
            };

            return sketch is null
                ? null
                : ReferenceForObject("sketch", document, sketch).Id;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    public IReadOnlyList<BodyRowDto> ListBodies(ListBodiesCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var rows = new List<BodyRowDto>();
        // ksBodyCollection carries its own refresh(): a collection obtained before the last
        // rebuild can still report the old membership, so counting it without refreshing produced
        // an empty list for a document that measurably had one body.
        var bodies = (ksBodyCollection)document.PartNow().BodyCollection();
        bodies.refresh();
        var count = bodies.GetCount();

        if (count == 0 && document.PartNow().GetMainBody() is ksBody mainBody)
        {
            // BodyCollection says empty while the part still hands back a main body. An empty list here would
            // contradict what kompas_get_context reports, so the discrepancy is surfaced as a row.
            var edgeCount = CountUniqueEdges(mainBody, out var mainFaceCount);
            rows.Add(new BodyRowDto
            {
                BodyRef = BodyReference(document, mainBody),
                Kind = SafeIsSolid(mainBody) ? "solid" : "sheet",
                Bbox = ReadBodyBox(mainBody),
                FaceCount = mainFaceCount,
                EdgeCount = edgeCount,
            });
            return rows;
        }

        for (var i = 0; i < count; i++)
        {
            // Indexing BodyCollection is not a proven route on v24 — GetMainBody() is. So the first entry
            // falls back to it; if neither works the row is reported as unresolved rather than skipped.
            var element = AsInterface<ksBody>(bodies.GetByIndex(i))
                ?? (i == 0 ? AsInterface<ksBody>(document.PartNow().GetMainBody()) : null);

            if (element is null)
            {
                rows.Add(new BodyRowDto
                {
                    BodyRef = References.Register("body_unresolved", document.Id, document.Revision, bodies.GetByIndex(i)).Id,
                    Kind = "unresolved",
                    Bbox = BoundingBoxDto.Empty,
                    FaceCount = -1,
                    EdgeCount = -1,
                });
                continue;
            }

            var edgeCount = CountUniqueEdges(element, out var faceCount);
            rows.Add(new BodyRowDto
            {
                BodyRef = BodyReference(document, element),
                Kind = SafeIsSolid(element) ? "solid" : "sheet",
                Bbox = ReadBodyBox(element),
                FaceCount = faceCount,
                EdgeCount = edgeCount,
            });
        }

        return rows;
    }

    /// <summary>The reference to hand out for a body.</summary>
    /// <remarks>INVARIANT: the SAME reference for the same body on one revision. A read tool that minted a
    /// fresh id on every call gave the client two strings for one body, and state remembered under one looked
    /// absent from the other - the same rule that already holds for sketches.
    /// History: docs/decisions/adapter-features.md#body-ref-lifetime</remarks>
    private string BodyReference(DocumentEntry document, ksBody body) =>
        ReferenceForObject("body", document, body).Id;

    public TopologyReadResult ReadTopology(ReadTopologyCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ksBody body)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, "body_ref указывает не на тело.");
        }

        var faces = new List<FaceRowDto>();
        var edges = new List<EdgeRowDto>();
        var includeFaces = command.Include is "faces" or "both";
        var includeEdges = command.Include is "edges" or "both";
        var faceCount = 0;
        var rawEdgeRefs = 0;

        var faceCollection = (ksFaceCollection)body.FaceCollection();
        faceCount = faceCollection.GetCount();
        var seenEdges = new HashSet<IntPtr>();

        for (var f = 0; f < faceCollection.GetCount(); f++)
        {
            if (AsInterface<ksFaceDefinition>(faceCollection.GetByIndex(f)) is not ksFaceDefinition face)
            {
                continue;
            }

            if (includeFaces)
            {
                faces.Add(DescribeFace(document, face));
            }

            if (!includeEdges)
            {
                continue;
            }

            var edgeCollection = (ksEdgeCollection)face.EdgeCollection();
            for (var e = 0; e < edgeCollection.GetCount(); e++)
            {
                if (AsInterface<ksEdgeDefinition>(edgeCollection.GetByIndex(e)) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                rawEdgeRefs++;
                var pointer = Marshal.GetIUnknownForObject(edge);
                var alreadySeen = !seenEdges.Add(pointer);
                Marshal.Release(pointer);
                if (alreadySeen)
                {
                    continue;
                }

                edges.Add(DescribeEdge(document, edge));
            }
        }

        return new TopologyReadResult(faceCount, rawEdgeRefs, seenEdges.Count, faces, edges);
    }

    private FaceRowDto DescribeFace(DocumentEntry document, ksFaceDefinition face)
    {
        var area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
        double[]? normal = null;
        var normalAmbiguous = true;

        try
        {
            if (face.IsPlanar())
            {
                // normalOrientation tells which way the surface normal points relative to the face; without
                // resolving it the sign of the normal would be a guess. A failed normal read also means
                // "ambiguous": returning null with normalAmbiguous=false would declare a failed read reliable.
                normal = SurfaceNormalAtMiddle(face);
                normalAmbiguous = normal is null;
            }
        }
        catch (Exception ex) when (ex is COMException)
        {
            normalAmbiguous = true;
        }

        var (radiusMm, heightMm, centerMm, axisMm) = CylinderGeometry(face);

        return new FaceRowDto
        {
            FaceRef = References.Register("face", document.Id, document.Revision, face).Id,
            SurfaceType = SurfaceTypeName(face),
            AreaMm2 = area ?? double.NaN,
            CenterMm = centerMm,
            NormalAtCenter = normal,
            RadiusMm = radiusMm,
            HeightMm = heightMm,
            AxisMm = axisMm,
            NormalAmbiguous = normalAmbiguous,
        };
    }

    /// <summary>Radius, extent, axis origin and direction of a cylindrical face. Route measured by probe P2.5:
    /// <c>face.GetSurface()</c> → <c>ksSurface.GetSurfaceParam()</c> → <c>ksCylinderParam { radius, height,
    /// GetPlacement() }</c>, direction from <c>ksPlacement.GetVector(2)</c>.</summary>
    /// <remarks>Two traps: <c>GetAxis</c> returns a POINT (origin + vector), not a direction, and the numbers
    /// are millimetres. Radius and height do not distinguish position; only the placement does, which is why
    /// all four are published.
    /// History: docs/decisions/adapter-core.md#cylinder-geometry</remarks>
    private static (double? RadiusMm, double? HeightMm, double[]? CenterMm, double[]? AxisMm) CylinderGeometry(
        ksFaceDefinition face)
    {
        try
        {
            if (face.GetSurface() is not ksSurface surface || !surface.IsCylinder()
                || surface.GetSurfaceParam() is not ksCylinderParam cylinder)
            {
                return (null, null, null, null);
            }

            double[]? center = null;
            double[]? axis = null;
            if (cylinder.GetPlacement() is ksPlacement placement)
            {
                if (placement.GetOrigin(out double ox, out double oy, out double oz))
                {
                    center = new[] { ox, oy, oz };
                }

                if (placement.GetVector(2, out double ax, out double ay, out double az))
                {
                    axis = new[] { ax, ay, az };
                }
            }

            return (cylinder.radius, cylinder.height, center, axis);
        }
        catch (COMException)
        {
            // Parameters KOMPAS did not give stay absent; the face remains a usable reference.
            return (null, null, null, null);
        }
    }

    /// <summary>Normal of a planar face through the typed <c>ksSurface</c> path: <c>GetNormal(u, v)</c> at
    /// the middle of the parameter range, sign from <c>normalOrientation</c>.</summary>
    /// <remarks>MEASURED: true means "coincides", false "reversed". Returns null when the normal cannot be
    /// read. DOC: <c>kssurface_getnormal.html</c>.
    /// History: docs/decisions/adapter-core.md#surface-normal-at-middle</remarks>
    private static double[]? SurfaceNormalAtMiddle(ksFaceDefinition face)
    {
        try
        {
            if (face.GetSurface() is not ksSurface surface)
            {
                return null;
            }

            var u = (surface.GetParamUMin() + surface.GetParamUMax()) / 2d;
            var v = (surface.GetParamVMin() + surface.GetParamVMax()) / 2d;
            if (!surface.GetNormal(u, v, out var x, out var y, out var z))
            {
                return null;
            }

            return face.normalOrientation
                ? new[] { x, y, z }
                : new[] { -x, -y, -z };
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string SurfaceTypeName(ksFaceDefinition face)
    {
        try
        {
            if (face.IsPlanar())
            {
                return "plane";
            }

            if (face.IsCylinder())
            {
                return "cylinder";
            }

            if (face.IsCone())
            {
                return "cone";
            }

            if (face.IsSphere())
            {
                return "sphere";
            }

            if (face.IsTorus())
            {
                return "torus";
            }

            return "other";
        }
        catch (Exception ex) when (ex is COMException)
        {
            return "unknown";
        }
    }

    private EdgeRowDto DescribeEdge(DocumentEntry document, ksEdgeDefinition edge)
    {
        var length = SafeDouble(() => edge.GetLength((uint)KompasUnits.LengthMm)) ?? double.NaN;
        var type = "other";
        try
        {
            type = edge.IsStraight() ? "line" : edge.IsCircle() ? "circle" : edge.IsArc() ? "arc" : "other";
        }
        catch (Exception ex) when (ex is COMException)
        {
            // Leave "other": an unclassified curve is still a usable reference.
        }

        var (circleRadius, circleCenter) = CircleEdgeParams(edge);

        return new EdgeRowDto
        {
            EdgeRef = References.Register("edge", document.Id, document.Revision, edge).Id,
            CurveType = type,
            LengthMm = length,
            VerticesMm = EdgeVertices(edge),
            RadiusMm = circleRadius,
            CenterMm = circleCenter,
        };
    }

    /// <summary>Radius and centre of a circular edge, read as <c>ksCurve3D.GetCurveParam()</c> →
    /// <c>ksCircle3dParam</c> (probe P2.6: <c>get_radius()</c>, the curve bbox and the length divided by 2π
    /// agree to the last digit on R10).</summary>
    /// <remarks>Deliberately never computed as L/2π: on a straight 100 mm edge that formula yields a
    /// plausible-looking 15.915 mm. A non-circular curve therefore reports no radius at all.
    /// History: docs/decisions/adapter-core.md#circle-edge-params</remarks>
    private static (double? RadiusMm, double[]? CenterMm) CircleEdgeParams(ksEdgeDefinition edge)
    {
        try
        {
            if (edge.GetCurve3D() is not ksCurve3D curve || !curve.IsCircle()
                || curve.GetCurveParam() is not ksCircle3dParam circle)
            {
                return (null, null);
            }

            double[]? center = null;
            if (circle.GetPlacement() is ksPlacement placement
                && placement.GetOrigin(out double x, out double y, out double z))
            {
                center = new[] { x, y, z };
            }

            return (circle.radius, center);
        }
        catch (COMException)
        {
            return (null, null);
        }
    }

    private static bool SafeIsSolid(ksBody body)
    {
        try
        {
            return body.IsSolid();
        }
        catch (Exception ex) when (ex is COMException)
        {
            return false;
        }
    }

    private static IReadOnlyList<IReadOnlyList<double>>? EdgeVertices(ksEdgeDefinition edge)
    {
        // ksVertexDefinition exposes the coordinate only through GetPoint(out x, out y, out z);
        // there is no `position` property in this interop version.
        try
        {
            var points = new List<IReadOnlyList<double>>();
            foreach (var flag in new[] { true, false })
            {
                if (edge.GetVertex(flag) is not ksVertexDefinition vertex)
                {
                    continue;
                }

                if (vertex.GetPoint(out var x, out var y, out var z))
                {
                    points.Add(new[] { x, y, z });
                }
            }

            return points.Count > 0 ? points : null;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException)
        {
            return null;
        }
    }

    public MeasurementDto Measure(MeasureCommand command)
    {
        if (!References.TryGet(command.TargetRef, out var stored) || stored is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.TargetRef}' не найдена.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(stored.DocumentId);
        if (stored.Revision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка относится к ревизии {stored.Revision}, текущая {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["reference_revision"] = stored.Revision, ["current_revision"] = document.Revision });
        }

        var unverified = new List<string>();
        BoundingBoxDto? bbox = null;
        double? volume = null;
        double? area = null;
        double? mass = null;

        if (stored.Payload is ksBody body)
        {
            if (command.Properties.Contains(MeasurableProperty.Bbox))
            {
                bbox = ReadBodyBox(body);
            }

            if (command.Properties.Contains(MeasurableProperty.Volume))
            {
                volume = MeasureVolume(body, unverified);
            }

            if (command.Properties.Contains(MeasurableProperty.SurfaceArea))
            {
                area = MeasureArea(body, unverified);
            }

            if (command.Properties.Contains(MeasurableProperty.Mass))
            {
                if (command.DensityKgPerM3 is null)
                {
                    unverified.Add("mass_requires_density — плотность не передана, масса не вычисляется");
                }
                else if (volume is double volumeMm3)
                {
                    // mm³ → m³ is 1e-9; the density comes from the caller and is never guessed.
                    mass = volumeMm3 * 1e-9d * command.DensityKgPerM3.Value;
                }
            }
        }
        else if (stored.Payload is ksFaceDefinition face && command.Properties.Contains(MeasurableProperty.SurfaceArea))
        {
            area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
        }
        else if (stored.Payload is ksEdgeDefinition edge && command.Properties.Contains(MeasurableProperty.Bbox))
        {
            unverified.Add("bbox_not_available_for_edge — у ребра нет собственного габарита в API5");
            _ = edge;
        }
        else
        {
            unverified.Add($"target_kind_{stored.Kind}_not_measurable");
        }

        return new MeasurementDto
        {
            Bbox = bbox,
            VolumeMm3 = volume,
            SurfaceAreaMm2 = area,
            MassKg = mass,
            UnverifiedAspects = unverified,
        };
    }

    /// <summary>Resolve a body's faces by a structural predicate. Every field the published schema
    /// declares is applied below; a field the tool does not offer never reaches this method, because the
    /// Host validates arguments against the closed tool schema before dispatch.</summary>
    /// <remarks>History: docs/decisions/adapter-core.md#selection-predicate-fields</remarks>
    public IReadOnlyList<ReferenceDto> ResolveSelection(ResolveSelectionCommand command)
    {
        var document = RequireReferenceDocument(command.BodyRef);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ksBody body)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, "body_ref должен указывать на тело.");
        }

        var candidates = new List<(ReferenceDto Dto, string SurfaceType, double Area, double[]? Normal)>();
        var faces = (ksFaceCollection)body.FaceCollection();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
            {
                continue;
            }

            var area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm)) ?? double.NaN;
            // An unreadable normal stays an absent value, not a zero vector: (0,0,0) would pass the normal
            // test as "did not match" and be indistinguishable from a measured normal.
            var normal = SurfaceNormalAtMiddle(face);
            candidates.Add((
                ToDto(References.Register("face", document.Id, document.Revision, face), $"area {area:0.####} mm2"),
                SurfaceTypeName(face), area, normal));
        }

        var matched = candidates.Where(c => SelectionMatches(c.SurfaceType, c.Area, c.Normal, command.Predicate)).ToList();
        if (matched.Count == 0)
        {
            return Array.Empty<ReferenceDto>();
        }

        if (command.RequireUnique && matched.Count > 1)
        {
            throw new KompasContractException(
                ErrorCodes.AmbiguousSelection,
                $"Предикате удовлетворяют {matched.Count} граней — однозначный выбор не выполнен.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["candidates"] = matched.Select(m => m.Dto.Id).Take(command.Limit).ToArray() });
        }

        return matched.Select(m => m.Dto).Take(command.Limit).ToArray();
    }

    private static bool SelectionMatches(string surfaceType, double area, double[]? normal, SelectionPredicateDto predicate)
    {
        if (predicate.SurfaceType is { Length: > 0 } wanted
            && !string.Equals(wanted, surfaceType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (predicate.AreaRangeMm2 is { Count: 2 } range && (area < range[0] || area > range[1]))
        {
            return false;
        }

        if (predicate.NormalDirection is { Count: 3 } direction)
        {
            if (normal is null)
            {
                // A normal-based selection neither confirms nor refutes the face: the normal was not read. An
                // invented vector would give "did not match", a refusal indistinguishable from a measurement.
                return false;
            }

            var tolerance = predicate.NormalAngleToleranceDeg ?? 0.5;
            var angle = Math.Acos(Math.Clamp(RigidFrame.Dot(Normalize(normal), Normalize(direction)), -1d, 1d)) * (180d / Math.PI);
            if (angle > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static double[] Normalize(IReadOnlyList<double> vector)
    {
        var asArray = vector as double[] ?? vector.ToArray();
        var norm = RigidFrame.Norm(asArray);
        return norm <= double.Epsilon
            ? new double[] { 0, 0, 0 }
            : new[] { asArray[0] / norm, asArray[1] / norm, asArray[2] / norm };
    }

    // ---------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------

    private static double Point(IReadOnlyList<double>? values, int index) =>
        values is null || values.Count <= index
            ? throw new KompasContractException(ErrorCodes.InvalidArgument, $"Ожидается координата [{index}] из двух значений.")
            : values[index];

    /// <summary>Arc end angles for <c>ksArcByAngle</c>, brought into the range the kernel accepts.</summary>
    /// <remarks>MEASURED: the call fails iff <c>start_deg + sweep_deg</c> leaves [−360°, 360°] (exactly
    /// 360° start/end are accepted). Angles are shifted by whole turns, just enough for the end to enter the
    /// range; the sweep does NOT change, and already-inside values are returned AS IS. LIMIT: NOT MEASURED,
    /// NOT touched — <c>|sweep_deg| &gt; 360</c> with the end inside.
    /// History: docs/decisions/adapter-core.md#arc-endpoints-range</remarks>
    private static (double First, double Second) ArcEndpoints(double startDeg, double sweepDeg)
    {
        var end = startDeg + sweepDeg;
        if (end > 360.0)
        {
            var turns = Math.Ceiling((end - 360.0) / 360.0);
            return (startDeg - 360.0 * turns, end - 360.0 * turns);
        }

        if (end < -360.0)
        {
            var turns = Math.Ceiling((-end - 360.0) / 360.0);
            return (startDeg + 360.0 * turns, end + 360.0 * turns);
        }

        return (startDeg, end);
    }

    private static double Required(double? value, string field) =>
        value ?? throw new KompasContractException(ErrorCodes.InvalidArgument, $"Поле '{field}' обязательно.");

    /// <summary>A collection element is exposed as <c>object</c>; it is either the definition interface
    /// directly or an <c>ksEntity</c> that has to be unwrapped (both shapes occur — spec 4.5).</summary>
    private static TInterface? AsInterface<TInterface>(object? element)
        where TInterface : class
    {
        if (element is TInterface direct)
        {
            return direct;
        }

        try
        {
            return element is ksEntity entity ? entity.GetDefinition() as TInterface : null;
        }
        catch (Exception ex) when (ex is COMException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> reader)
    {
        try
        {
            var value = reader();
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    private static int CountUniqueEdges(ksBody body, out int faceCount)
    {
        var seen = new HashSet<IntPtr>();
        var faces = (ksFaceCollection)body.FaceCollection();
        faceCount = faces.GetCount();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
            {
                continue;
            }

            var edges = (ksEdgeCollection)face.EdgeCollection();
            for (var e = 0; e < edges.GetCount(); e++)
            {
                if (AsInterface<ksEdgeDefinition>(edges.GetByIndex(e)) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(edge);
                seen.Add(pointer);
                Marshal.Release(pointer);
            }
        }

        return seen.Count;
    }

    private BoundingBoxDto ReadBodyBox(ksBody body)
    {
        try
        {
            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? new BoundingBoxDto(new[] { x1, y1, z1 }, new[] { x2, y2, z2 })
                : BoundingBoxDto.Empty;
        }
        catch (Exception ex) when (ex is COMException)
        {
            return BoundingBoxDto.Empty;
        }
    }

    private double? MeasureVolume(ksBody body, List<string> unverified)
    {
        var volume = SafeDouble(() => MassProperties(body, (uint)KompasUnits.MassMmKg)?.v
            ?? throw new InvalidCastException("объём недоступен"));

        if (volume is null)
        {
            unverified.Add("volume_units_or_availability — CalcMassInertiaProperties не вернул объём");
        }

        return volume;
    }

    private double? MeasureArea(ksBody body, List<string> unverified)
    {
        var area = SafeDouble(() => MassProperties(body, (uint)KompasUnits.MassMmKg)?.F
            ?? throw new InvalidCastException("площадь недоступна"));

        if (area is null)
        {
            unverified.Add("area_units_or_availability — CalcMassInertiaProperties не вернул площадь");
        }

        return area;
    }

    /// <summary>Mass-centre properties with an explicit unit selector: millimetres for length, kilograms
    /// for mass, so <c>v</c> is mm³, <c>F</c> mm² and <c>m</c> kg.</summary>
    private static ksMassInertiaParam? MassProperties(ksBody body, uint unitBits)
    {
        try
        {
            return body.CalcMassInertiaProperties(unitBits) as ksMassInertiaParam;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private double? ReadVolume(DocumentEntry document)
    {
        try
        {
            if (document.PartNow().GetMainBody() is not ksBody body)
            {
                return null;
            }

            return MassProperties(body, (uint)KompasUnits.MassMmKg)?.v;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static bool SafeIsCreated(ksEntity entity)
    {
        try
        {
            return entity.IsCreated();
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException)
        {
            return false;
        }
    }

    private void RequirePart(DocumentEntry document)
    {
        if (document.Kind == DocumentKind.Drawing)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                "Геометрические операции недоступны для чертежа.",
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }
    }

    private DocumentEntry RequireReferenceDocument(string referenceId)
    {
        if (!References.TryGet(referenceId, out var stored) || stored is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{referenceId}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        return RequireDocument(stored.DocumentId);
    }

    private ReferenceDto ToDto(StoredReference reference, string? semanticHint) => new()
    {
        Id = reference.Id,
        Kind = reference.Kind,
        DocumentId = reference.DocumentId,
        Revision = reference.Revision,
        PersistentFeatureId = reference.PersistentFeatureId,
        SemanticHint = semanticHint,
    };
}

/// <summary>Result of a sketch edit. <see cref="DeletedEntities"/> counts what was actually deleted (vendor
/// convention 1 = success; by the object found at the stored coordinate), <see cref="ExpectedDeleted"/> how
/// many were expected — the difference is not smoothed away: a partial cleanup means a dirty profile.
/// <see cref="ProfileAreaUnavailable"/> is why <see cref="ProfileAreaMm2"/> is null: the SERVER's reading of
/// the input, with the PLACE (the caller's primitive numbers and where they meet), never the kernel's
/// verdict. <paramref name="ProbePointsFromModel"/>: the delete coordinates came from the dependent body
/// rather than session memory.
/// History: docs/decisions/adapter-core.md#edit-sketch-result, adapter-sketch.md#profile-input-diagnosis</summary>
public sealed record EditSketchResult(
    int EntityCount,
    IReadOnlyList<string> Kinds,
    bool EditClosedOut,
    double? ProfileAreaMm2,
    int DeletedEntities = 0,
    int ExpectedDeleted = 0,
    bool ProbePointsFromModel = false,
    string? ProfileAreaUnavailable = null);

/// <summary>What the server's own analysis of the sketch INPUT says. It is deliberately NOT a confirmation
/// of the kernel: <c>passed</c> means "the server's reading is consistent", not "КОМПАС accepted it".</summary>
public enum SketchInputCheck
{
    /// <summary>The chain is assembled, closed, unbranched and free of self-intersections.</summary>
    Passed,

    /// <summary>A structural defect of the caller's contour, named with its place.</summary>
    Failed,

    /// <summary>The server cannot analyse this input — an empty profile, a spline, or a profile this
    /// session did not draw.</summary>
    NotAvailable,
}

/// <summary>Result of closing a sketch edit. <see cref="ProfileClosedConfirmed"/> stays false: closedness is
/// not readable from <c>ksSketchDefinition</c>, so the kernel's confirmation is never claimed.
/// <see cref="ProfileInputCheck"/> is the SERVER's reading of the input, with its reason in
/// <see cref="ProfileInputReason"/>; <see cref="Warnings"/> carries the non-fatal note when the caller
/// required a closed profile and the server's reading disagrees.
/// History: docs/decisions/adapter-sketch.md#finish-sketch-input-check</summary>
public sealed record FinishSketchResult(
    bool ProfileClosedConfirmed,
    IReadOnlyList<string> UnverifiedAspects,
    SketchInputCheck ProfileInputCheck,
    string? ProfileInputReason = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Result of an extrusion. <see cref="TargetBodyIndex"/> is the index of the body the caller
/// declared as the target, and <see cref="BodyChanges"/> is what the before/after measurements for each body
/// actually ended up being. Both exist because <c>Create() == true</c> proves nothing: probe P2.6 measured
/// that when the declared target and the contour contradict each other, KOMPAS answers true and changes no
/// body.</summary>
public sealed record ExtrudeResult(
    ReferenceDto FeatureRef,
    int BodyCount,
    double? VolumeMm3,
    VerificationDto Verification,
    int? TargetBodyIndex = null,
    IReadOnlyList<string>? BodyChanges = null,
    /// <summary>Sum of the volumes of all bodies in the document BEFORE the operation. null — not read.</summary>
    double? DocumentVolumeBeforeMm3 = null,
    /// <summary>Sum of the volumes of all bodies AFTER the operation — same as <see cref="VolumeMm3"/>.</summary>
    double? DocumentVolumeAfterMm3 = null,
    /// <summary>Material increment by THIS feature (for cut — removed). null — not measured, not zero.</summary>
    double? VolumeDeltaMm3 = null,
    /// <summary>Basis of the increment: <c>target_body_N</c>, <c>new_body_volume</c>,
    /// <c>existing_body_N_delta</c> or <c>not_attributable</c>.</summary>
    string? VolumeDeltaBasis = null,
    /// <summary>What <see cref="VolumeMm3"/> means.</summary>
    string? VolumeNote = null,
    /// <summary>Where the material this feature REMOVED lay, in part coordinates — <c>+z</c>/<c>−z</c> and so
    /// on. Cut only; null for base/boss and when the gabarit did not name a side. MEASURED from the target
    /// body's box, not mapped from <c>direction</c>: for a cut, <c>positive</c> removes material AGAINST the
    /// sketch normal (v24 help), so the request alone would mislead.</summary>
    string? MaterialRemovedToward = null,
    /// <summary>Where the material this feature ADDED lies, in part coordinates. Base/boss only.</summary>
    string? MaterialAddedToward = null,
    /// <summary>Why <see cref="MaterialRemovedToward"/>/<see cref="MaterialAddedToward"/> is not named, when
    /// it is not. Named rather than left empty, so "not determined" is not read as "no direction".</summary>
    string? MaterialTowardUnavailable = null,
    /// <summary>Which source named the side: <c>measured_box_shift</c> (the measured shift of the target
    /// body's gabarit) or <c>sketch_normal_rule</c> (the documented sketch-normal rule, which also works when
    /// the gabarit does not move). Null when neither source was available.</summary>
    string? MaterialTowardSource = null,
    /// <summary>The sketch normal read in part coordinates by the documented route, or null when unreadable.
    /// The basis of <c>sketch_normal_rule</c>, published so the side can be audited rather than trusted.</summary>
    double[]? MaterialTowardSketchNormal = null,
    /// <summary>Non-fatal findings that the caller must not miss — e.g. the two side sources disagreeing.</summary>
    IReadOnlyList<string>? Warnings = null);

public sealed record RebuildResult(long NewRevision, DocumentContextDto Context);

public sealed record TopologyReadResult(
    int FaceCount,
    int RawEdgeReferences,
    int UniqueEdgeCount,
    IReadOnlyList<FaceRowDto> Faces,
    IReadOnlyList<EdgeRowDto> Edges);
