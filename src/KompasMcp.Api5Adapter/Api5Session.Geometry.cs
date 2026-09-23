using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Geometry: sketches, features, measurement, final topology.
/// </summary>
/// <remarks>
/// Signatures here are taken from <c>docs/compatibility/kompas-api5-metadata.json</c> (dumped from
/// the installed interop by P0.2), not from memory. Three consequences worth stating:
/// <list type="bullet">
/// <item><c>GetLength</c>/<c>GetArea</c>/<c>CalcMassInertiaProperties</c> take a <b>UInt32 unit
/// selector</b>. Leaving it at 0 asks for centimetres — that is the whole 10× discrepancy of
/// spec 4.5, reproduced in P0.7 as <c>GetLength(0)=10</c> on a 100 mm edge. Only
/// <see cref="KompasUnits.LengthMm"/> is passed in this file.</item>
/// <item>Volume and area come from <c>CalcMassInertiaProperties</c> (<c>.v()</c>, <c>.F()</c>):
/// <c>ksBody</c> has no <c>GetVolume</c>/<c>GetArea</c> at all, contrary to spec 4.5's wording.</item>
/// <li>Edges are reached through <c>GetMainBody() → FaceCollection → EdgeCollection</c>, never
/// through <c>EntityCollection(o3d_edge)</c>, which also contains sketch and construction
/// contours (P0.8: 23 objects against 14 real body edges).</item>
/// </remarks>
public sealed partial class Api5Session
{
    // ---------------------------------------------------------------------------------------------
    // Sketches
    // ---------------------------------------------------------------------------------------------

    public ReferenceDto CreateSketch(CreateSketchCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var planeEntity = ResolvePlaneEntity(document, command.Plane);

        var sketch = (ksEntity)document.PartNow().NewEntity(KompasObjectTypes.Of(KompasObjectTypes.Sketch));
        sketch.name = command.Name ?? $"sketch_{document.Revision}";
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        if (!definition.SetPlane(planeEntity))
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

        var reference = References.Register("sketch", document.Id, document.Revision, sketch);

        // A sketch created here is empty by construction, so replace and delete_entities are
        // meaningful on it from the start; a sketch this server never drew stays unknown to the
        // point registry and is refused instead of quietly appending.
        _sketchProbePoints[reference.Id] = new List<double[]>();
        _sketchProfileBox.Remove(reference.Id);
        _sketchProfiles.Remove(reference.Id);
        if (command.Plane.Base is PlaneBase basePlane)
        {
            _sketchPlaneBase[reference.Id] = basePlane;
        }

        return ToDto(reference, PlaneHint(command.Plane));
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

        // Measured by probe P2.4: `direction=true` means "offset along the plane's own normal" for
        // XY, XZ and YZ alike (XY z=+15, XZ y=+15, YZ x=−15 because the YOZ normal points to −X).
        // The YZ negation that used to sit here was not an API inconsistency being worked around —
        // it silently redefined the field as "along the model axis", contradicting the published
        // description ("смещение вдоль нормали базовой плоскости"). Semantics are now uniform, and
        // a caller who needs the model axis gets it from the plane description, not from a sign flip.
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

    private static string PlaneHint(PlaneRefDto plane) =>
        plane.Reference is not null
            ? $"sketch on {plane.Reference}"
            : $"{plane.Base?.ToString().ToUpperInvariant() ?? "plane"}{(Math.Abs(plane.OffsetMm) > 1e-9 ? $" +{plane.OffsetMm:0.###} mm" : string.Empty)}";

    /// <summary>
    /// The contours this server drew into each sketch, in mm. Filled when the server itself drew the
    /// primitives, and read by <see cref="Extrude"/> as the expected volume target: without it the
    /// extrusion could only report "КОМПАС said yes", which spec 1.11 explicitly forbids as proof.
    /// </summary>
    /// <remarks>
    /// The contour list is kept rather than the area, because the area of a profile is not the sum of
    /// its primitives: a contour inside another one is a hole in it. Measured 24.09.2026 — while the
    /// entry was a running sum, a sketch built by appending a circle inside another gave
    /// π·125 = 392.699081699 against the annulus' π·75 = 235.619449019, and the extrusion of a
    /// correct ring was reported as an unconfirmed geometry change. One number cannot carry nesting,
    /// so the number is derived from the contours instead of accumulated next to them.
    /// </remarks>
    private readonly Dictionary<string, SketchProfile> _sketchProfiles = new(StringComparer.Ordinal);

    /// <summary>
    /// Points lying on the primitives this server drew into each sketch. API5 gives no way to
    /// enumerate sketch objects (no ksFirstObj/ksGetObjCount/GetSegmentContainer in this interop),
    /// so a remembered coordinate is the only handle that makes replace and delete_entities real
    /// instead of a silent append.
    /// </summary>
    private readonly Dictionary<string, List<double[]>> _sketchProbePoints = new(StringComparer.Ordinal);

    /// <summary>
    /// Extent of everything this server drew into each sketch, in sketch coordinates. It is what
    /// lets a declared target body be checked against the contour before anything is mutated
    /// (<see cref="TargetBodyGuard.ProfileMayAffectBody"/>) — КОМПАС itself offers no way to ask a
    /// sketch where its profile lies, and a contradiction it swallows without an error.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="_sketchProfiles"/> for the same reason and with the same rule: an
    /// unknown shape or an incompletely cleared sketch drops the entry rather than leaving a stale
    /// extent behind, because a box that is wrong is worse than no box — it would refuse, or allow,
    /// a mutation on a figure the server never drew.
    /// </remarks>
    private readonly Dictionary<string, ProfileBox> _sketchProfileBox = new(StringComparer.Ordinal);

    /// <summary>
    /// Which base plane each sketch sits on, when the server was the one that chose it. A sketch
    /// built on a referenced plane has no entry: the mapping from sketch axes to model axes for
    /// that case is not measured, so the target check reports "not checked" instead of assuming it.
    /// </summary>
    private readonly Dictionary<string, PlaneBase> _sketchPlaneBase = new(StringComparer.Ordinal);

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

        // Clearing a sketch is possible only through ksFindObj(x, y, limit) → ksDeleteObj(ref)
        // (probe P2.6): the 2D API in this interop exposes no enumeration at all — ksFirstObj,
        // ksGetObjCount, GetSegmentContainer, SketchEntities are absent — so the only way to point
        // at an existing object is a coordinate that lies on it. The server remembers probe points
        // for what it drew. For a sketch it did not draw — a reopened document, a model built by
        // hand — the coordinate is derived from the model itself, which is what probe G measured;
        // the derivation is allowed only inside the configuration that probe covered.
        var knownPoints = _sketchProbePoints.TryGetValue(command.SketchRef, out var remembered)
            ? remembered
            : null;
        string? derivedFrom = null;
        if (command.Mode != SketchEditMode.Append && knownPoints is null)
        {
            knownPoints = DeriveProbePointsFromModel(command, target, document, out derivedFrom);
        }

        // BeginEdit is declared to return object. A hard cast would throw InvalidCastException,
        // which escapes as an unclassified failure; naming the runtime type instead turns "it
        // broke" into "the API handed back X", which is actionable. BeginEdit is called exactly
        // once — a second call for the sake of an error message would re-enter edit mode.
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
            // "not found / not deleted" — the opposite of a C# bool, so nothing here compares to true.
            // One object is hit by several probe points (a circle answers at both ends of a
            // diameter), and a second ksDeleteObj on the same ref fails by definition — so what is
            // counted is distinct objects found by ksFindObj, not the points we remembered.
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
        Exception? drawn = null;
        foreach (var entity in command.Entities)
        {
            try
            {
                kinds.Add(DrawSketchEntity(editor, entity));
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

        // Answers of the calls are not evidence: probe G.9 measured every one of them succeeding
        // (ksFindObj ≠ 0, ksDeleteObj = 1, ksCircle ≠ 0, EndEdit = true) while the dependent body
        // vanished entirely — V = 0, 0 faces, 0 bodies. A replacement that makes the profile bigger
        // than the material is a real user mistake, and passing it back as geometry_checked would
        // tell the caller their edit was applied. So the body is measured, and losing it is a
        // refusal with the numbers attached.
        if (command.Mode == SketchEditMode.Replace || command.Mode == SketchEditMode.DeleteEntities)
        {
            GuardDependentBodySurvived(document, command, bodiesBefore);
        }

        // The remembered profile is the LIST of contours drawn so far, and its area is derived from
        // them — not a running sum, which cannot express that one contour is a hole in another
        // (measured 24.09.2026: summing gave π·125 = 392.699081699 for a ring whose region is
        // π·75 = 235.619449019, and a correct extrusion was then reported as unconfirmed). An unknown
        // shape clears the entry, so a later extrusion reports "unverified" instead of comparing a
        // measurement against a stale figure. A replacement that did not fully clear also clears the
        // profile on purpose: it then holds leftovers, and claiming an analytic area for it would make
        // the extrusion check pass against a wrong expectation.
        var analytic = ProfileArea.Of(command.Entities);
        var freshPoints = ProbePointsOf(command.Entities);
        var partialClear = command.Mode == SketchEditMode.Replace && !cleared;
        if (analytic is null || partialClear)
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
        double? profileAreaMm2 = null;
        if (!partialClear && _sketchProfiles.TryGetValue(command.SketchRef, out var drawnProfile))
        {
            profileAreaMm2 = drawnProfile.AreaMm2;
        }

        // The same bookkeeping for the profile's extent, with the same rule about a partial clear:
        // an extent is only useful for refusing a contradictory target when the server is certain
        // the contour inside it is the whole contour. An unknown figure is reported as unknown,
        // which costs the caller the pre-check and nothing else.
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
            ProbePointsFromModel: derivedFrom is not null);
    }

    /// <summary>
    /// Which base plane a sketch sits on: from memory when the server chose it, otherwise read back
    /// out of the model.
    /// </summary>
    /// <remarks>
    /// The memory entry only exists for a sketch this server created. A sketch that arrived with a
    /// reopened document has none, and without a plane the derivation cannot be allowed at all —
    /// which is correct but would make the new route useless exactly in the case it was built for.
    /// <see cref="PlaneNormalAxis"/> already resolves a plane entity (including an offset plane,
    /// through its base) to the model axis it is normal to, measured for the through-extent
    /// calculation; the same resolution answers the question here. An axis has no sign, so this
    /// establishes "the sketch is parallel to XY", not "it faces +Z" — the sign is what probe G left
    /// unmeasured, and <see cref="SketchPointDerivation.AxisIsNormalToXyPlane"/> tolerates either.
    /// </remarks>
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

    /// <summary>
    /// Refuses a sketch edit that destroyed a dependent body, instead of reporting it as applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes the question probe G opened (Q-SKETCH-EDIT-ZERO). Measured in G.9: replacing a Ø20 hole
    /// with R90 on a 100×80 plate makes the profile larger than the material, and КОМПАС answers
    /// success to every individual call while the body disappears — V = 0, 0 faces, 0 bodies. The
    /// vendor's return codes are therefore not a verification of anything, and the only honest
    /// evidence is the body measurement.
    /// </para>
    /// <para>
    /// The refusal is reported as <see cref="ErrorCodes.GeometryFailed"/> with <c>partialEffects:
    /// true</c>: the model really did change, and saying otherwise would be its own lie. A general
    /// rollback is not claimed — nothing in this adapter can restore a body КОМПАС consumed.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Coordinates for <c>ksFindObj</c> taken from the model itself, for a sketch this server did
    /// not draw and therefore cannot remember.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Route measured by probe G (<c>docs/acceptance/api7/sketch-geometry-reopen.md</c>, 11 steps
    /// PASS) on a reopened document: the through cut is found in the tree by type, its sketch via
    /// <c>GetSketch()</c>, and the coordinate comes from the cylindrical face the cut left behind —
    /// <c>GetSurfaceParam() → ksCylinderParam</c> gives centre and radius, and the point
    /// <c>(cx + r; cy)</c> lies on the sketch circle. Two control points that find nothing were part
    /// of the measurement, so the point is known to select this primitive rather than a neighbour.
    /// </para>
    /// <para>
    /// Guarded by <see cref="SketchPointDerivation.Verdict"/>: only a base-XY sketch with a circular
    /// profile qualifies. An inclined plane needs a 3D→2D transport that was never measured, and a
    /// segment/arc/rectangle profile has no primitive a cylinder-derived point provably lies on.
    /// Both cases refuse here with the reason stated, instead of extrapolating from one measurement.
    /// </para>
    /// </remarks>
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

        // The feature is located by the dependent body rather than by name: probe F measured that a
        // name does not survive the API5↔API7 transition, so an identity that depends on it would
        // break on the next re-open of a model the user renamed.
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

                // Only the sketch being edited is of interest. The comparison is by IUnknown rather
                // than by name for the same reason the feature lookup is: the same COM object can
                // arrive as a different RCW.
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

    /// <summary>
    /// Centre, radius and axis of the hole wall left by the feature that consumes a sketch.
    /// </summary>
    /// <remarks>
    /// Returns the first cylindrical face whose axis is normal to the XY plane — the configuration
    /// probe G measured. The lateral-area cross-check the probe performed belongs to acceptance, not
    /// to the product: here the radius and axis are what the point needs, and the geometry is
    /// confirmed afterwards by measuring the dependent body, which is the only evidence the probe
    /// found to be trustworthy at all (G.9: every call answered success while the body vanished).
    /// </remarks>
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

    /// <summary>
    /// Whether two COM wrappers point at the same object. Used instead of a name comparison because
    /// probe F measured that names do not survive the API5↔API7 transition (a feature named "f-ch2"
    /// answers "Фаска:1" through API7), so any identity built on a name is a latent defect.
    /// </summary>
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

    /// <summary>
    /// Points that provably lie on the drawn primitives — what ksFindObj needs to find them again.
    /// Midpoints of rectangle sides and of a segment, and a point on the circumference for a circle
    /// (its centre lies on nothing). Anything without such a point contributes none, which keeps
    /// "we remember the sketch" honest rather than optimistic.
    /// </summary>
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

    private static string DrawSketchEntity(ksDocument2D editor, SketchEntityDto entity)
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
                // ЗНАК sweep_deg ОБЯЗАН ДОЙТИ ДО КОНЕЧНОГО УГЛА, И ЭТО ИЗМЕРЕНО, А НЕ ВЫВЕДЕНО.
                //
                // ДЕФЕКТ ПРОДУКТА, найденный 20.09.2026 сценарием «Скоба Model Mania 2021» (наряд
                // §3.3/§3.5). Здесь стояло `start_deg + Math.Abs(sweep)`: для ОТРИЦАТЕЛЬНОГО sweep
                // конечный угол уезжал на ДРУГУЮ сторону от начального, и ядро рисовало дугу,
                // отражённую относительно луча start_deg, — то есть совсем не ту. Схема при этом
                // объявляет «Знак задаёт направление», и комментарий рядом обещал, что знак
                // «must survive the call (spec 2.6)»: обещание было объявлено и не выполнено —
                // это дефект, а не граница возможностей.
                //
                // ИЗМЕРЕНО (зонд scratch/_mania_contour_probe.py, бинари поставки):
                //   * четверть диска R30: дуга (start 0°, sweep +90°) + два отрезка → объём
                //     14137.166941154068 = 20·π·30²/4 — верно;
                //   * ТА ЖЕ четверть, дуга (start 90°, sweep −90°) + ТЕ ЖЕ отрезки → GEOMETRY_FAILED
                //     («Entity.Create() выдавливания вернул false») — концы дуги не там, где их
                //     ждёт контур;
                //   * различающая постановка T6 (дуга (90°, −90°) и отрезки (−30,0)→(0,0)→(0,30),
                //     где обе гипотезы замыкают контур, но дают разную площадь) дала
                //     14137.166941154068 = 20·225π, то есть дугу ВТОРОЙ четверти [90°,180°] вместо
                //     первой [0°,90°] — конечный угол действительно был взят как start+|sweep|;
                //   * контур Model Mania: восемь касательных дуг с ЗНАКОМ → GEOMETRY_FAILED; та же
                //     геометрия, выраженная дугами с ПОЛОЖИТЕЛЬНЫМ sweep, → объём 174799.7403608485
                //     против аналитического 174799.740134 (расхождение 1.3·10⁻¹²).
                // Почему дефект дожил до сих пор: во всей приёмке `sweep_deg` встречался РОВНО ОДИН
                // раз и всегда положительным (scripts/mcp-smoke.py, строка пути sweep B5.2), поэтому
                // отрицательная ветвь не измерялась ни одной строкой.
                var sweep = Required(entity.SweepDeg, "sweep_deg");
                var start = Required(entity.StartDeg, "start_deg");
                // ДЕФЕКТ ПРОДУКТА №2, найденный тем же сценарием (наряд §3.5). Схема объявляет
                // `start_deg` и `sweep_deg` в диапазоне [−720, 720], а ядро принимает ДВА УГЛА, и
                // вызов ОТКАЗЫВАЕТ, когда конечный угол покидает [−360°, 360°]. Измерено зондом
                // `scratch/_arc_angle_range_probe.py` на поставке `publish-mania-20260920`: тот же
                // сектор R30, записанный двумя способами, — ОТКАЗ против успеха:
                //   R2 (старт 360°, sweep +90°, конец  450°) → GEOMETRY_FAILED   ↔ R1 (0°, +90°) → 14137.166941
                //   R5 (старт 315°, sweep +90°, конец  405°) → GEOMETRY_FAILED   ↔ R6 (−45°, +90°) → 14137.166941
                //   R7 (старт −350°, sweep −90°, конец −440°) → GEOMETRY_FAILED ↔ R8 (10°, −90°) → 14137.166941
                // Разделяющие случаи: R3 (старт РОВНО 360°, конец 270°) и R4 (конец РОВНО 360°) —
                // ОБА проходят, то есть триггер не «старт 360» и не «конец 360», а ВЫХОД ЗА ±360°.
                // Приведение углов не переистолковывает большие дуги: измерено зондом контура,
                // вариант G — дуга (0°, +270°) даёт 42411.500823 = 20·(270/360)·π·30², то есть ядро
                // соблюдает размах больше 180°, а не берёт меньшую дугу.
                //
                // ПРАВКА СТРОГО АДДИТИВНАЯ — НО ТОЛЬКО ПОСЛЕ ВОЗВРАТА ПОРЯДКА АРГУМЕНТОВ.
                //
                // ПЕРВАЯ РЕДАКЦИЯ ЭТОЙ ПРАВКИ БЫЛА НЕ АДДИТИВНОЙ, И ЭТО ПОЙМАЛ КОНТРОЛЬ ЗОНДА, А НЕ
                // РАССУЖДЕНИЕ. Я передал в ядро `first, second` (начало, конец) вместо прежних
                // `Min, Max` — и пробы с ОБОИМИ углами внутри диапазона, которых правка не должна
                // касаться, поменяли результат: R8 (старт 10°, sweep −90°) дал 42411.500823 вместо
                // 14137.166941 на прежней поставке, R3 — то же. То есть ПОРЯДОК ДВУХ УГЛОВ НЕСУЩИЙ,
                // а не косметический: при отрицательном sweep прежний код подавал меньший угол
                // первым, и именно это ядро и ожидает. Утверждение «правка аддитивна» было в
                // комментарии ДО того, как стало верным; верным его сделал возврат `Min`/`Max`.
                // Правило: аддитивность доказывается контролем на ВХОДАХ, которых правка не
                // касается, а не рассуждением о её ветках.
                var (first, second) = ArcEndpoints(start, sweep);
                editor.ksArcByAngle(
                    Point(entity.CenterMm, 0),
                    Point(entity.CenterMm, 1),
                    Required(entity.RadiusMm, "radius_mm"),
                    Math.Min(first, second),
                    Math.Max(first, second),
                    sweep >= 0 ? (short)1 : (short)0,
                    LineStyle);
                return "arc";

            case SketchEntityKind.Rectangle:
                return DrawRectangle(editor, entity);

            case SketchEntityKind.Polyline:
                return DrawPolyline(editor, entity);

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

    private static string DrawPolyline(ksDocument2D editor, SketchEntityDto entity)
    {
        if (entity.PointsMm is not { Count: >= 2 } points)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "points_mm должен содержать минимум две точки.");
        }

        var segments = entity.Closed == true ? points.Count : points.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            var from = points[i];
            var to = points[(i + 1) % points.Count];
            editor.ksLineSeg(from[0], from[1], to[0], to[1], LineStyle);
        }

        return "polyline";
    }

    public FinishSketchResult FinishSketch(FinishSketchCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        BumpRevision(target.Document, "sketch.finish");

        // ksSketchDefinition exposes no profile/loop count in this interop version, so closedness
        // cannot be asserted here. The extrusion is where it actually surfaces, and the caller is
        // told that rather than handed a vacuous "profile is closed".
        return new FinishSketchResult(
            ProfileClosedConfirmed: false,
            new[]
            {
                "profile_closedness_not_verified — замкнутость профиля проверяется только выдавливанием",
            });
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

        // <c>base</c> creates the first body, so a target sent with it names nothing at all. The
        // Host refuses the pair before COM; the check is repeated here because a hand-built IPC
        // frame bypasses the Host's rule table, and accepting the field only to ignore it would
        // tell the caller that a target had been honoured.
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

        // Per-body state is captured BEFORE anything is touched and again after the rebuild: probe
        // P2.6 measured that a declaration contradicting the profile leaves SetSketch, Create and
        // RebuildDocument all answering true while not one body changes volume. A return code
        // therefore proves nothing here, and the only evidence is this pair of snapshots.
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);

        var bodyTarget = command.TargetBodyRef is null
            ? null
            : ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);

        // Necessary-not-sufficient, and stated that way: agreement of two bounding boxes is not
        // geometric containment. A disagreement is still worth refusing on, because it is exactly
        // the configuration КОМПАС swallows without an error.
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

        // SetSideParam(side1, type, depth, draftValue, draftOutward): the second argument is the
        // end-condition type, and depth is the third — as the historical scripts used it.
        // directionType selects along/against/both, separately from SetSideParam.
        //
        // MEASURED SEMANTICS (plate 100x80x10 spanning z=[0,10], sketch on the XY plane at offset o):
        //   directionType 0 dtNormal  -> material goes to the +z side of the sketch plane
        //   directionType 1 dtReverse -> material goes to the -z side of the sketch plane
        //   directionType 2 dtBoth    -> material goes to both sides (depth each way)
        // Consequences, each measured with an exact volume rather than inferred:
        //   base  positive          -> bbox z=[0,10]   V=80000
        //   base  negative          -> bbox z=[-10,0]  V=80000
        //   base  symmetric         -> bbox z=[-10,10] V=160000
        //   cut   o=10 positive     -> -392.699082 (= pi*5^2*5, into the plate)
        //   cut   o=0  negative     -> -392.699082 (the mirrored pair; into the plate from below)
        //   cut   o=10 negative     -> NO_GEOMETRY_CHANGE (asks for material at z>10, where none is)
        //   boss  o=10 positive     -> +9000 (30x30x10)
        //   boss  o=0  negative     -> +9000
        // A direction pointing away from the body is NOT an error: КОМПАС creates the feature and
        // changes nothing, which the Host surfaces as NO_GEOMETRY_CHANGE. That is why the choice of
        // sketch plane is part of the caller's contract, not a detail this adapter may paper over.
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
            ksBaseExtrusionDefinition baseExtrusion => ConfigureBase(baseExtrusion, target.Sketch, direction, command),
            ksBossExtrusionDefinition boss => ConfigureBoss(boss, target.Sketch, direction, command, bodyTarget, selector),
            ksCutExtrusionDefinition cut => ConfigureCut(cut, target.Sketch, direction, command, bodyTarget, selector),
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

        if (!feature.Create())
        {
            ComApartment.Release(feature);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Entity.Create() выдавливания вернул false: признак не появился.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        document.Document.RebuildDocument();
        BumpRevision(document, "extrude");

        var bodiesAfter = ReadBodySnapshots(document.PartNow());
        var bodyCount = bodiesAfter.Count;

        // The declaration survives only if a freshly obtained definition object reports it: reading
        // it back through the same RCW cannot distinguish "the document stored it" from "the
        // wrapper kept my value" (the rule P2.1 established).
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

        // ТРИ РАЗНЫЕ ВЕЛИЧИНЫ, которые раньше были одним числом, — и именно это смешение дало
        // 13 ложных сомнений в клиентской приёмке B3 (дефект EXTRUDE-VOLUME-DELTA-ON-MULTIBODY):
        //   * documentVolume*  — СУММА объёмов всех тел документа (`volume_mm3` — это «после»);
        //   * объём тела, о котором операция: объявленная цель (boss/cut) либо НОВОЕ тело (base);
        //   * ПРИРАЩЕНИЕ материала этим признаком — единственная величина, которую можно сверять с
        //     `profile_area × depth`.
        // На многотельном документе первая и третья различаются на объём всего, что уже стояло
        // (49 000 против 1 000), и сверка их объявляет расхождение, которого в геометрии нет.
        // Сумма индивидуальных объёмов — ещё и не объём пространственного объединения: у двух
        // перекрывающихся тел 36 000 + 24 000 = 60 000 против 36 000 у объединения.
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
            // «До» и «после» приращения — измеренные суммы по документу, ни одно число здесь не
            // выдумано. Какую именно величину несёт приращение, называет volume_delta_basis.

            // Куда именно ушёл материал: ровно в одно тело, и это тело обязано быть названо. Одной
            // правильной суммы недостаточно — ошибка в одном теле может быть уравновешена ошибкой в
            // другом. Принимается и новое тело, и изменившееся прежнее: непроходимым обязан быть
            // ответ «что-то где-то изменилось».
            var priorMoved = bodiesBefore
                .Select(s => s.Index)
                .Where(i => changes.DeltaOf(i) is double moved && Math.Abs(moved) > VolumeChangeFloorMm3)
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

        // Expected change = the analytic area of the profile the server itself drew × depth. The area
        // is the region the contours enclose (a contour inside another is a hole), recomputed from the
        // whole profile rather than remembered as a number. When the region is not analytically known
        // (arcs, free polylines, touching or overlapping contours, a profile drawn outside this
        // session), the answer says so rather than comparing a measurement with nothing.
        double? expected = null;
        string expectedBasis = "not_computable";
        if (_sketchProfiles.TryGetValue(command.SketchRef, out var profile)
            && profile.AreaMm2 is double profileAreaMm2
            && profileAreaMm2 > 0d)
        {
            if (command.EndCondition == ExtrudeEndCondition.Through)
            {
                // Through mode has no caller-supplied depth, so the traversed material is taken
                // from the body itself: its extent along the sketch normal. That is a property of
                // the model, not a guess, and it fails loudly when the profile is not centred in
                // the material — the check then reports a mismatch instead of passing quietly.
                // Since the target body is resolved, "the body itself" means that body, not
                // whatever GetMainBody happens to return in a multi-body part.
                if (ThroughExtentMm(document, target.Sketch, bodyTarget?.Body) is double extentMm)
                {
                    expected = profileAreaMm2 * extentMm;
                    expectedBasis = $"profile_area_x_body_extent_{extentMm:0.####}mm";
                }
            }
            else
            {
                expected = profileAreaMm2 * DepthOf(command);
                expectedBasis = "profile_area_x_depth";
            }
        }

        var checks = new List<NamedCheck>
        {
            new("feature_created", true),
            new("body_present", bodyCount >= 1, Observed: bodyCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("expected_basis", expected is not null, Observed: expectedBasis),
            // Reported always, gated only for a boss. A cut is allowed to divide one body into two
            // (a through slot across a plate does exactly that), so counting bodies is evidence to
            // show rather than a condition to enforce there. A boss has no such excuse: the caller
            // named the body to grow, and an extra body means material went somewhere else — which
            // is precisely what probe P2.6 saw when the choose type was left at ksNewBody.
            new(
                "body_count",
                command.Operation switch
                {
                    ExtrudeOperation.Boss => bodiesBefore.Count == bodyCount,
                    _ => bodyCount >= 1,
                },
                Observed: $"{bodiesBefore.Count}→{bodyCount}"),
        };

        checks.AddRange(attribution);

        var geometryConfirmed = false;
        if (expected is not double expectedDelta)
        {
            checks.Add(new NamedCheck("volume_delta", false, Observed: "not_computable", Expected: "not_computable"));
        }
        else if (measuredDelta is not double measured)
        {
            // Непрочитанное приращение — это «не подтверждено», а не ноль: подстановка нуля выдала бы
            // непроверенное за измеренное, а отсутствие единственного изменившегося тела — за
            // отсутствие изменений.
            checks.Add(new NamedCheck(
                "volume_delta",
                false,
                Observed: measuredDelta is null && measuredBasis == "not_attributable"
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

        // Атрибуция обязательна ровно там, где она считалась: у base цели нет, и без неё «прирост
        // 1000» мог бы прийти из любого тела документа. У boss/cut адресность несут
        // selector_choose_type / target_body_affected / nontarget_bodies_unchanged ниже.
        geometryConfirmed &= attribution.All(c => c.Passed);

        var unverified = new List<string>();
        if (bodyTarget is not null)
        {
            // The declaration is only worth anything if the kernel actually consults the body list
            // it was offered: chooseType=1 (the default) consults bodies *and* parts, chooseType=2
            // consults parts only and changed nothing at all in P2.6 even though Create returned
            // true. So the read-back is a check, and a failed one rules out geometry_checked.
            var selectorDeclared = chooseTypeReadBack == KompasChoose.Bodies;
            checks.Add(new NamedCheck(
                "selector_choose_type",
                selectorDeclared,
                Observed: $"{Range(chooseTypeReadBack)} (Add: {string.Join("; ", selector)})".Trim(),
                Expected: KompasChoose.Bodies.ToString()));
            geometryConfirmed &= selectorDeclared;

            var targetDelta = changes.DeltaOf(bodyTarget.Index);
            var movedAsExpected = targetDelta is double moved && Math.Abs(moved) > VolumeChangeFloorMm3;
            checks.Add(new NamedCheck(
                "target_body_affected",
                movedAsExpected,
                Observed: $"тело {bodyTarget.Index}: {Range(bodyTarget.Snapshot.Volume)} → {Range(affectedAfter?.Volume)}, ΔV={Range(targetDelta)} из {changes.Rows.Count}",
                Expected: "|ΔV| > 0.01 мм³"));
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

            // A no-op is not a result. КОМПАС reports no error for a declaration that contradicts
            // where the profile actually lies (P2.6): Create and RebuildDocument say true, the
            // document is rebuilt, and no body loses or gains anything. Only an explicit failure
            // keeps that from being read as success by anything looking at the call alone. A
            // volume that could not be read at all is not a measured no-op: it fails the check and
            // says why, but the refusal to call it success is the verdict, not an invented cause.
            if (targetDelta is double targetMoved && Math.Abs(targetMoved) <= VolumeChangeFloorMm3)
            {
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    $"Признак создан (Create=true), но заявленное тело {bodyTarget.Index} не изменилось: ΔV={Range(targetDelta)} мм³. " +
                    "Так КОМПАС ведёт себя, когда заявленное тело противоречит расположению контура: ошибка не возвращается, " +
                    "не меняется ни одно тело. Успехом это быть не может.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = command.TargetBodyRef,
                        ["target_body_index"] = bodyTarget.Index,
                        ["target_delta_mm3"] = targetDelta,
                        ["changed_body_indexes"] = changes.Changed.ToArray(),
                        ["moved_body_indexes"] = changes.Moved.ToArray(),
                        ["per_body"] = changes.Rows.ToArray(),
                        ["profile_target_agreement"] = AgreementName(agreement),
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

        if (!geometryConfirmed)
        {
            unverified.Add("volume_delta_not_confirmed — КОМПАС сообщает только об успехе вызова; пока измерение не совпало, признак считается недоказанным");
        }

        if (bodyTarget is null && attribution.Any(c => !c.Passed))
        {
            // Приращение не приписано ни одному телу — значит и сравнивать с аналитикой нечего:
            // «изменилось на 1000 где-то в документе» доказательством признака не является.
            unverified.Add(
                "body_change_attribution_failed — приращение материала не приписано ровно одному телу "
                + "(новому или прежнему): одна правильная сумма не заменяет адресности, потому что "
                + "ошибка в одном теле может быть уравновешена ошибкой в другом");
        }

        var reference = References.Register("feature", document.Id, document.Revision, feature);
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
            // volume_mm3 — СУММА объёмов всех тел документа после операции. Одно и то же поле несло
            // разные величины (у base — сумму по документу, у boss/cut — объём цели), и это
            // заставляло читателя догадываться. Величина теперь одна и названа в volume_note;
            // приращение признака едет отдельным полем с названным основанием.
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
            + "после по документу — document_volume_before_mm3 / document_volume_after_mm3.");
    }

    /// <summary>How the pre-mutation agreement test resolved, in words for the details block.</summary>
    private static string AgreementName(bool? agreement) => agreement switch
    {
        true => "boxes_agree",
        false => "boxes_disagree",
        null => "not_checked",
    };

    /// <summary>
    /// Short invariant rendering of a number or a triple of coordinates. Round-trip format on
    /// purpose: the acceptance evidence compares a measured volume with an analytic one, and
    /// printing 79999.99999999999 as "80000" would make the report claim a match the kernel did not
    /// produce. The check itself is done on the raw doubles; this is only about not dressing them up.
    /// </summary>
    private static string Range(double? value) =>
        value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "нет";

    private static string Range(double[]? values) =>
        values is null
            ? "нет"
            : string.Join(", ", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// How much a body's volume must move for it to count as "the body that changed", in mm³. It is
    /// the floor of docs/03 §3.3's volume tolerance and the threshold probe P2.6 used to call a ΔV
    /// zero, so this server and the measurement that justified it agree on what "nothing happened"
    /// means. Matching an analytic expectation stays on <see cref="ProfileArea.Tolerance"/>.
    /// </summary>
    private const double VolumeChangeFloorMm3 = 0.01d;

    /// <summary>
    /// The body an extrusion was told to act on: where it sits in <c>BodyCollection</c> right now,
    /// the raw collection element <c>ksBodyCollection.Add</c> accepts, and its state before the
    /// mutation.
    /// </summary>
    /// <remarks>
    /// The index is resolved at mutation time by pointer rather than taken from the reference,
    /// because nothing here proves the collection keeps its order across a rebuild; the raw element
    /// comes from that same enumeration so index and element cannot disagree.
    /// </remarks>
    private sealed record BodyTarget(int Index, object RawElement, ksBody Body, BodySnapshot Snapshot);

    /// <summary>One body as a snapshot reader saw it: volume in mm³ and its gabarit.</summary>
    private sealed class BodySnapshot
    {
        public required int Index { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        public double[]? Center => Min is null || Max is null
            ? null
            : new[] { (Min[0] + Max[0]) / 2d, (Min[1] + Max[1]) / 2d, (Min[2] + Max[2]) / 2d };
    }

    /// <summary>
    /// Per-body volumes and boxes, read exactly the way every other measurement in this project
    /// reads them — <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c> and
    /// <c>ksBody.GetGabarit</c> — so the numbers compare with probes P0.7, P2.1 and P2.6.
    /// </summary>
    /// <remarks>
    /// <c>refresh()</c> before counting is not decoration: without it a collection read right after
    /// a rebuild can report the previous membership (the <c>ListBodies</c> defect). What is
    /// deliberately not used here is <c>GetMainBody()</c> — in a multi-body part it answers one body
    /// only, which is precisely why an operation aimed at another body looked like a no-op.
    /// </remarks>
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
                }

                rows.Add(new BodySnapshot { Index = i, Volume = volume, Min = min, Max = max });
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Whatever was read before the failure stands. An empty list is the honest answer to
            // "which bodies and at what volumes" when the collection cannot be walked at all.
        }

        return rows;
    }

    /// <summary>
    /// Resolves <c>target_body_ref</c> into the body the selector call needs. The reference holds a
    /// <c>ksBody</c>; <c>ksBodyCollection.Add</c> wants the raw element of the part's body
    /// collection (measured in P2.6: the cast to <c>ksEntity</c> and the unpacked
    /// <c>GetDefinition()</c> are both refused), and nothing in API5 states which position the
    /// referenced body currently occupies. The only identity available on both sides is the
    /// object's IUnknown — the same comparison <see cref="ReadTopology"/> performs to deduplicate
    /// edges — so the index is found by pointer and the element is taken from that same walk.
    /// </summary>
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

    /// <summary>
    /// Position of <paramref name="wanted"/> inside <c>part.BodyCollection()</c>, compared by
    /// IUnknown. Every pointer acquired here is released at once: <c>GetIUnknownForObject</c> adds a
    /// reference, and one leaked pointer per call is a slow leak of the whole model.
    /// </summary>
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

    /// <summary>
    /// Per-body before/after comparison. Bodies are matched by the position of their box centre
    /// rather than by collection order, because order across a rebuild is not something measured
    /// here — and matching by position is what turns "the plate lost nothing" into a number instead
    /// of an assumption.
    /// </summary>
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

            // A body with no counterpart after the rebuild is "unknown", not "unchanged": 0 would
            // read as a no-op, and conflating the two is exactly the kind of laundering this file
            // exists to avoid.
            double? delta = reference.Volume is double low && match?.Volume is double high
                ? low - high
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

    /// <summary>
    /// Насколько должен сдвинуться габарит тела, чтобы это считалось изменением положения, в мм.
    /// </summary>
    /// <remarks>
    /// Порог существует потому, что «тело изменилось» — НЕ то же самое, что «у тела изменился объём».
    /// Измерено на наряде §4.1 (B3.25 новой поставки 19.09.2026): на эталоне булевой правки объём
    /// РАЗНОСТИ и объём ПЕРЕСЕЧЕНИЯ равны (12 000 мм³), различает их только положение габарита
    /// (0,0,0)…(20,30,20) против (20,0,0)…(40,30,20). Пока признаком изменения был только объём,
    /// корректно применённая правка `intersect` выглядела как «ни одно тело не изменилось» и
    /// отвергалась собственным прибором — то есть вердикт опирался на поле, которое его не
    /// поддерживает (дефект CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, наряд §4.1).
    /// <para>
    /// Величина порога — 1 нм: габарит читается как точные координаты B-Rep, поэтому у тела, не
    /// тронутого операцией, все шесть чисел совпадают побитово, а любой настоящий сдвиг на много
    /// порядков больше. Порог ловит шум пересчёта, а не геометрию.
    /// </para>
    /// </remarks>
    private const double BoxChangeFloorMm = 1e-6d;

    /// <summary>The comparison itself: what moved, what did not, and the rows that say so.</summary>
    private sealed class BodyComparison
    {
        private readonly Dictionary<int, double?> _delta = new();

        private readonly Dictionary<int, BodySnapshot?> _matched = new();

        private readonly Dictionary<int, bool> _boxChanged = new();

        private readonly Dictionary<int, double?> _boxShift = new();

        public List<string> Rows { get; } = new();

        /// <summary>
        /// Тела, у которых изменился ОБЪЁМ. Это признак «здесь поработал материал», и он остаётся
        /// отдельным от положения: у булевой правки вид `intersect` объём не меняет вовсе.
        /// </summary>
        public List<int> Changed { get; } = new();

        /// <summary>
        /// Тела, у которых изменился ТОЛЬКО габарит, а объём остался прежним. Отдельный список, а не
        /// добавка к <see cref="Changed"/>, потому что «тело переехало» и «тело потеряло материал» —
        /// разные наблюдения, и вызывающий вправе требовать именно второе.
        /// </summary>
        public List<int> Moved { get; } = new();

        /// <summary>
        /// Тела, состояние которых ИЗМЕНИЛОСЬ в наблюдаемом смысле: изменился объём ИЛИ габарит.
        /// Это и есть правильный предикат для «результат этой операции» — он не подбирается под
        /// ожидание и не зависит от вида операции.
        /// </summary>
        public List<int> Touched { get; } = new();

        /// <summary>
        /// Тела, появившиеся ПОСЛЕ операции и не сопоставленные ни одному «до». Пустой список —
        /// измеренный факт «новых тел нет», а не «не считали».
        /// </summary>
        public List<BodySnapshot> NewBodies { get; } = new();

        public void RecordNewBodies(IEnumerable<BodySnapshot> rows) => NewBodies.AddRange(rows);

        public double? DeltaOf(int index) => _delta.TryGetValue(index, out var delta) ? delta : null;

        public BodySnapshot? MatchedOf(int index) => _matched.TryGetValue(index, out var match) ? match : null;

        public bool BoxChangedOf(int index) => _boxChanged.TryGetValue(index, out var changed) && changed;

        /// <summary>Наибольшее расхождение координат габарита этого тела, в мм.</summary>
        public double? BoxShiftOf(int index) => _boxShift.TryGetValue(index, out var shift) ? shift : null;

        public void Record(BodySnapshot before, BodySnapshot? after, double? delta)
        {
            _delta[before.Index] = delta;
            _matched[before.Index] = after;
            var shift = BoxShift(before, after);
            var volumeMoved = delta is double moved && Math.Abs(moved) > VolumeChangeFloorMm3;
            var boxMoved = shift is double movedMm && movedMm > BoxChangeFloorMm;
            _boxChanged[before.Index] = boxMoved;
            _boxShift[before.Index] = shift;
            Rows.Add($"тел{before.Index}: V {Range(before.Volume)} → {Range(after?.Volume)}, ΔV={Range(delta)}, " +
                     $"габарит [{Range(before.Min)}|{Range(before.Max)}] → [{Range(after?.Min)}|{Range(after?.Max)}]" +
                     (boxMoved ? $", сдвиг {Range(shift)} мм" : string.Empty));
            if (volumeMoved)
            {
                Changed.Add(before.Index);
            }

            if (!volumeMoved && boxMoved)
            {
                Moved.Add(before.Index);
            }

            if (volumeMoved || boxMoved)
            {
                Touched.Add(before.Index);
            }
        }

        /// <summary>Наибольшее расхождение шести координат габарита, либо <c>null</c>, если габарита нет.</summary>
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

        /// <summary>
        /// Посторонние тела, состояние которых изменилось. Проверка «посторонние не тронуты» обязана
        /// видеть и переезд: тело, уехавшее в сторону с прежним объёмом, — такое же нарушение
        /// объявленной адресности, как тело, потерявшее материал, и прежняя редакция его не видела.
        /// </summary>
        public List<string> UnchangedViolations(int exceptIndex)
        {
            var offenders = new List<string>();
            foreach (var index in _delta.Keys.Where(k => k != exceptIndex).OrderBy(k => k))
            {
                var parts = new List<string>();
                if (_delta[index] is double moved && Math.Abs(moved) > VolumeChangeFloorMm3)
                {
                    parts.Add("ΔV=" + moved.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " мм³");
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

    /// <summary>
    /// Order follows the sequence proven against v24 in P0.7 and in the historical scripts:
    /// attach the sketch, then set directionType, then the side parameters. Reporting which of the
    /// three refused matters: "SetSketch returned false" and "SetSideParam returned false" are
    /// different defects with different causes (empty profile vs bad parameters).
    /// </summary>
    private static bool ConfigureBase(ksBaseExtrusionDefinition definition, ksEntity sketch, short direction, ExtrudeCommand command)
    {
        if (!definition.SetSketch(sketch))
        {
            throw Refused("SetSketch", command);
        }

        // dtNormal=0, dtReverse=1, dtBoth=2, dtMiddlePlane=3 (kAPI5.tlb, ksDirectionTypeEnum).
        // directionType IS the direction; it is not a "which side to draw" selector. Negative must
        // therefore stay 1 — and getting it wrong is invisible, because an unsupported combination
        // does not fail loudly, it silently ignores SetSideParam (measured in P2.1 for cut: at
        // directionType=0 sixteen measurements returned ONE distinct ΔV, i.e. the setter was dropped).
        definition.directionType = direction;
        var depth = DepthOf(command);

        // forward=true means "extrude to the side the sketch normal points at". dtReverse=1 already
        // says "the other side", so passing forward=true unconditionally asks for a contradiction:
        // the direction says reverse, the side says forward. Measured on v24 before this fix: a base
        // extrusion asked for direction=negative answered Create()=false and the feature never
        // appeared (GEOMETRY_FAILED). After the fix: bbox z=[-10,0], V=80000 — the exact mirror of
        // direction=positive (bbox z=[0,10], V=80000). The side must agree with the direction.
        //
        // The same guard was needed on ConfigureBoss (before it, boss with direction=negative failed
        // with GEOMETRY_FAILED) and is harmless-but-not-required on ConfigureCut (see there).
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

    private static bool ConfigureBoss(
        ksBossExtrusionDefinition definition,
        ksEntity sketch,
        short direction,
        ExtrudeCommand command,
        BodyTarget? bodyTarget,
        List<string> selectorEvidence)
    {
        // The choice is made before SetSketch because that is the order probe P2.6 recorded as the
        // minimal working sequence. The same probe measured the opposite order producing the same
        // ΔV, so this is a documented sequence rather than a superstition about it.
        ApplyBodyChoice(command, bodyTarget, selectorEvidence, type => definition.chooseType = type,
            () => definition.chooseType, () => definition.ChooseBodies());

        if (!definition.SetSketch(sketch))
        {
            throw Refused("SetSketch", command);
        }

        definition.directionType = direction;
        var depth = DepthOf(command);

        // Same contradiction as ConfigureBase: a reverse direction (1) must not be combined with
        // forward=true, or Create() answers false and the feature never appears. Kept in step with
        // ConfigureBase deliberately — the two bodies were identical before this fix and diverging
        // them silently is how the base defect survived in the first place.
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

    private static bool ConfigureCut(
        ksCutExtrusionDefinition definition,
        ksEntity sketch,
        short direction,
        ExtrudeCommand command,
        BodyTarget? bodyTarget,
        List<string> selectorEvidence)
    {
        ApplyBodyChoice(command, bodyTarget, selectorEvidence, type => definition.chooseType = type,
            () => definition.chooseType, () => definition.ChooseBodies());

        if (!definition.SetSketch(sketch))
        {
            throw Refused("SetSketch", command);
        }

        definition.directionType = direction;

        // Measured on v24 (probe P2.1): etThroughAll is honoured only together with
        // directionType=symmetric, and the depth number is discarded by the solver in that mode
        // (1 mm and 1000 mm cut identically), so 0 is passed and GetSideParam reads back
        // type=1, depth=0. The Host refuses through-mode with any other direction or operation,
        // which is why no such combination can reach here.
        var through = command.EndCondition == ExtrudeEndCondition.Through;
        var endType = through ? EndConditionThrough : EndConditionBlind;
        var depth = through ? 0d : DepthOf(command);

        // The forward call is skipped for dtReverse=1 for the same reason as ConfigureBase and
        // ConfigureBoss — asking for forward while the direction says reverse is a contradiction.
        // Measured, and stated honestly: unlike boss, cut did NOT need this change. Cut already
        // answered correctly for direction=negative before it, because the unconditional second
        // SetSideParam(false, ...) call below happened to supply the reverse side on its own. The
        // guard is kept anyway so the three operations read the same way and so that the reason cut
        // works is a stated rule rather than an accident of call order.
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

    /// <summary>
    /// Tells the kernel which body the operation must act on, by the route measured in probe P2.6
    /// and nowhere else in this repository:
    /// <c>def.chooseType = ksChBodies(3)</c>; <c>cb = def.ChooseBodies()</c> (answers
    /// <c>ksChooseBodies</c>, whose only members are <c>BodyCollection()</c> and
    /// <c>ChooseBodiesType</c> — there is no <c>Add</c> on it);
    /// <c>cb.ChooseBodiesType = ksManualEditing(2)</c>;
    /// <c>((ksBodyCollection)cb.BodyCollection()).Add(&lt;raw element of part.BodyCollection()&gt;)</c>.
    /// </summary>
    /// <remarks>
    /// Three things this call is not, all of them measured:
    /// <list type="bullet">
    /// <item>Not optional. With no declaration КОМПАС cuts whatever body the contour happens to lie
    /// over, which for a single-body part is indistinguishable from success.</item>
    /// <item>Not decorative. <c>chooseType = 2</c> (parts only, and the part has none) produced
    /// <c>Create = true</c> and no volume change at all.</item>
    /// <item>Not to be left at <c>ChooseBodiesType = 0</c>: on a boss that creates an additional
    /// body even where the profile overlaps an existing one (2 bodies became 3).</item>
    /// </list>
    /// If <c>Add</c> does not accept the body, the operation is refused before <c>Create</c>: a
    /// feature created without an honoured target is exactly the unaimed mutation the contract
    /// exists to prevent.
    /// </remarks>
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
            // Nothing was declared. For boss and cut the adapter refuses that earlier, so this is
            // the base-extrusion path — and ksBaseExtrusionDefinition exposes no selector at all
            // (measured: neither chooseType nor ChooseBodies() is declared on it).
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

    /// <summary>
    /// Depth for the blind condition. The Host enforces the mode rule before COM; this guard keeps
    /// a hand-built IPC frame from silently extruding by zero.
    /// </summary>
    private static double DepthOf(ExtrudeCommand command) =>
        command.DepthMm ?? throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Для end_condition=blind поле depth_mm обязательно.");

    private static KompasContractException Refused(string step, ExtrudeCommand command) => new(
        ErrorCodes.GeometryFailed,
        $"Выдавливание не настроено: {step} вернул false (operation={command.Operation}, " +
        $"end_condition={command.EndCondition.ToString().ToLowerInvariant()}, " +
        $"depth={FormatDepth(command)}, direction={command.Direction}). " +
        "Частая причина — профиль эскиза пуст или не замкнут.",
        RetryPolicy.ReacquireContext,
        partialEffects: true,
        details: new Dictionary<string, object?>
        {
            ["step"] = step,
            ["depth_mm"] = command.DepthMm,
            ["end_condition"] = command.EndCondition.ToString().ToLowerInvariant(),
        });

    private static string FormatDepth(ExtrudeCommand command) =>
        command.DepthMm is double depth ? $"{depth:0.###} мм" : "не задана (насквозь)";

    /// <summary>
    /// Extent of the material a through operation traverses, in mm, along the axis the sketch is
    /// normal to. Null when the plane does not resolve to one of the three model axes: then the
    /// expectation stays "not_computable" rather than comparing a measurement with a number that
    /// was invented on the way.
    /// </summary>
    /// <remarks>
    /// <paramref name="material"/> is the body the caller declared. Falling back to
    /// <c>GetMainBody()</c> is kept for the operations that name no target, and it is the reason a
    /// through cut aimed at a second body used to be unverifiable: in a multi-body part
    /// <c>GetMainBody()</c> answers one body, so the expected delta was computed from a body the
    /// operation never touched.
    /// </remarks>
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

    /// <summary>
    /// Model axis a plane entity is normal to: o3d_planeXOY→z, o3d_planeXOZ→y, o3d_planeYOZ→x; an
    /// offset plane is resolved through its base plane. A constructed plane or a face carrier
    /// deliberately returns null — guessing its axis would silently redefine what "насквозь" means.
    /// </summary>
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

    /// <summary>
    /// Fillet over explicitly referenced body edges (docs/03 G04, docs/05 SM-09). Route measured by
    /// probe P2.2: <c>NewEntity(o3d_fillet=34)</c> → <c>ksFilletDefinition</c> → radius/tangent →
    /// <c>array()</c> as <c>ksEntityCollection</c> → <c>Add(edgeEntity)</c> → <c>Create()</c>.
    /// Edges come from <c>kompas_read_topology</c> (final body), never from
    /// <c>part.EntityCollection(o3d_edge)</c>: that collection also holds sketch and construction
    /// contours, which is the error docs/04 §4.5 records and the historical fillet helper used.
    /// Success is not the HRESULT: the radius is read back from a fresh definition object and the
    /// face count is required to grow by the number of filleted edges.
    /// </summary>
    /// <summary>
    /// Разворачивает ребро из реестра ссылок в <c>ksEntity</c> — то, что принимает коллекция
    /// скругления. Какие из трёх маршрутов работают, измерено (проба P2.2), а не предположено;
    /// маршрут возвращается вместе с сущностью, чтобы ответ называл, как ребро добыто.
    /// </summary>
    /// <remarks>
    /// Вынесено общим помощником, потому что этим путём ходят и создание скругления, и правка его
    /// набора рёбер: две копии одного разбора расходятся при первой же правке одной из них.
    /// </remarks>
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

            // The fillet collection takes entities, not definition interfaces, and which of the
            // three unwrappings yields one was measured, not assumed (probe P2.2). Each route is
            // tried once and named, so the answer reports how the edge was really obtained.
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

        document.Document.RebuildDocument();
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
        else
        {
            checks.Add(new NamedCheck(
                "volume_delta",
                volumeDecreased,
                Observed: volumeBefore is double b && volumeAfter is double a
                    ? (b - a).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    : "not_computable",
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
        if (!geometryConfirmed)
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

    /// <summary>
    /// Фаска по явными ссылками заданным рёбрам конечного тела (docs/05 SM-11).
    /// </summary>
    /// <remarks>
    /// Маршрут измерен пробой F от 12.09.2026 на v24 и повторён здесь вызов в вызов:
    /// <c>NewEntity(o3d_chamfer=33)</c> → <c>GetDefinition()</c> как <c>ksChamferDefinition</c> →
    /// <c>SetChamferParam(transfer, d1, d2)</c> → <c>array()</c> как <c>ksEntityCollection</c> →
    /// <c>Add(ребро)</c> → <c>Create()</c> → <c>RebuildDocument()</c>. На пластине 100×80×10 четыре
    /// вертикальных угловых ребра с катетами 2×2 сняли ровно 20·d₁·d₂ = 80 мм³ (F.2), правка на 3×3
    /// сняла 180 (F.3), а после save→close→reopen признак нашёлся по дереву и был отредактирован
    /// снова (F.5).
    /// <para>
    /// Рёбра берутся из <c>kompas_read_topology</c> (конечное тело), а не из
    /// <c>EntityCollection(o3d_edge)</c>: в той коллекции лежат и эскизные контуры — тот самый
    /// дефект, который docs/04 §4.5 записывает для исторического помощника скругления.
    /// </para>
    /// <para>
    /// Успехом считается не <c>Create()</c>: параметр перечитывается с нового объекта определения,
    /// грани обязаны вырасти ровно на число рёбер, а объём — измениться согласно аналитическому
    /// ожиданию, если вызывающий его задал. Нулевой катет отклоняется до обращения в COM, потому что
    /// КОМПАС его принимает и создаёт признак с четырьмя нулевыми гранями при неизменном объёме
    /// (F.12) — выдать такое за успех означало бы наврать про геометрию.
    /// </para>
    /// </remarks>
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

            // Так же, как у скругления: коллекция признака принимает entity, а не интерфейс
            // определения, и каким из трёх разворачиваний она получается — измерено (P2.2), а не
            // предполагается. Каждый путь именуется в ответе.
            var unwrapped = UnwrapEdgeToEntity(edge, reference);
            entities.Add(unwrapped.Entity);
            unwrapRoutes.Add(unwrapped.Route);
        }

        if (command.Mode == ChamferMode.DistanceAngle)
        {
            // Угла у ksChamferDefinition нет физически (замерено пробой F), поэтому этот способ
            // идёт единственным известным маршрутом — IChamfer.Angle в API7 того же сеанса.
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
            // Число ошибки признака — то, что КОМПАС сообщает о причине отказа именно этого
            // создания (objectError измерен пробой L как читаемое поле состояния признака);
            // молча отдать «Create()=false» без него означало бы оставить вызывающего гадать.
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

        document.Document.RebuildDocument();
        BumpRevision(document, "chamfer");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        // Перечитывание с НОВОГО объекта определения: тот, чем писали, может быть кэшированным
        // представлением, а «мы вызвали SetChamferParam» доказательством того, что модель сохранила,
        // не является (тот же стандарт, что у G03 и у правки опоры в L11).
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
        else
        {
            checks.Add(new NamedCheck(
                "volume_delta",
                materialRemoved,
                Observed: volumeBefore is double cb && volumeAfter is double ca
                    ? (cb - ca).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    : "not_computable",
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
        if (!geometryConfirmed)
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

    /// <summary>
    /// Правила «поля ↔ способ». Отклоняются до COM: КОМПАС принимает нулевой катет и создаёт
    /// признак с нулевыми гранями при прежнем объёме (проба F.12), поэтому «неверный параметр»
    /// обязан долетать до нас, а не превращаться в молча применённую геометрию.
    /// </summary>
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
                // Второй катет при этом способе нечем выразить: API7 берёт Distance1 и Angle.
                // Принять число и проигнорировать его — значит выдать за применённый параметр
                // тот, который запросили и не получили.
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "При mode=distance_angle поле distance2_mm запрещено: сторону задаёт angle_deg " +
                    "(второй катет = distance1·tg угла, измерено пробой F.10).",
                    RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Катеты и признак стороны, перечитанные из определения. Запись, а не кортеж: «не прочиталось»
    /// обязано отличаться от «прочиталось как ноль», и nullable-кортеж здесь не выражается.
    /// </summary>
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

    /// <summary>
    /// Результат фаски. Катеты возвращаются перечитанными из модели, а не теми, что передали:
    /// «мы вызвали SetChamferParam» геометрическим фактом не является. <c>feature_ref</c> пуст,
    /// когда признак создан, но не виден в дереве API5: ссылка, по которой правка всё равно
    /// упала бы, честнее предупреждения.
    /// </summary>
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
        if (!document.Document.RebuildDocument())
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "RebuildDocument() вернул false.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document.UpdateDocumentParam();
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

    /// <summary>
    /// Reference to the sketch an extrusion is built on, or null when the feature has none or the
    /// read-back fails.
    /// </summary>
    /// <remarks>
    /// Always a freshly minted reference against the current revision: a handle stored from before a
    /// rebuild is dropped by the registry, and minting here rather than reusing a remembered one is
    /// what makes the row usable on a document that was reopened. A failure to read the sketch is
    /// reported as null instead of an error — <c>kompas_list_features</c> must keep listing a
    /// document whose features it cannot fully describe, which is the same rule the rest of the row
    /// already follows.
    /// </remarks>
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
                : References.Register("sketch", document.Id, document.Revision, sketch).Id;
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
            // BodyCollection says empty while the part still hands back a main body. Returning an
            // empty list here would be a silent contradiction of what kompas_get_context reports,
            // so the discrepancy is surfaced as a row the caller can see.
            var edgeCount = CountUniqueEdges(mainBody, out var mainFaceCount);
            rows.Add(new BodyRowDto
            {
                BodyRef = References.Register("body", document.Id, document.Revision, mainBody).Id,
                Kind = SafeIsSolid(mainBody) ? "solid" : "sheet",
                Bbox = ReadBodyBox(mainBody),
                FaceCount = mainFaceCount,
                EdgeCount = edgeCount,
            });
            return rows;
        }

        for (var i = 0; i < count; i++)
        {
            // Indexing BodyCollection is not a proven route on v24 — GetMainBody() is (spec 4.5 and
            // the historical packaging script both use it). So the first entry falls back to it.
            // If neither works the row is reported as unresolved rather than skipped: an empty list
            // for a document with one body is exactly the silent drop this contract forbids.
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
                BodyRef = References.Register("body", document.Id, document.Revision, element).Id,
                Kind = SafeIsSolid(element) ? "solid" : "sheet",
                Bbox = ReadBodyBox(element),
                FaceCount = faceCount,
                EdgeCount = edgeCount,
            });
        }

        return rows;
    }

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
                // normalOrientation tells which way the surface normal points relative to the
                // face; without resolving it the sign of the normal would be a guess. Неудачное
                // чтение нормали тоже означает «неоднозначно»: отдать null при
                // normalAmbiguous=false значило бы объявить несостоявшееся чтение достоверным.
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

    /// <summary>
    /// Radius, extent, axis origin and direction of a cylindrical face. Route measured by probe
    /// P2.5: <c>face.GetSurface()</c> → <c>ksSurface.GetSurfaceParam()</c> →
    /// <c>ksCylinderParam { radius, height, GetPlacement() }</c>, with the direction taken from
    /// <c>ksPlacement.GetVector(2)</c>.
    ///
    /// Two traps that make this route worth pinning down in code: <c>GetAxis</c> returns a POINT
    /// (origin + vector), not a direction — reading it as an axis gives a vector of length ≈64.8
    /// for this part — and the numbers are millimetres, cross-checked there against
    /// <c>GetArea(LengthMm) = 2πrh</c> to the last digit. Radius and height do not distinguish
    /// position; only the placement does, which is why all four are published.
    /// </summary>
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
            // Parameters КОМПАС did not give stay absent; the face remains a usable reference.
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Normal of a planar face through the typed <c>ksSurface</c> path: <c>GetNormal(u, v)</c> at
    /// the middle of the parameter range, with the sign taken from <c>normalOrientation</c>. The
    /// control pair in probe P2.6 measured that flag: on two flat caps the surface normal was the
    /// same <c>(0,0,1)</c> while <c>normalOrientation</c> differed (false at z=0, true at z=10), so
    /// true means "coincides" and false means "reversed".
    ///
    /// Returns null when the normal cannot be read. There is deliberately no second, reflected
    /// route: the documented signature is <c>GetNormal(paramU, paramV, out x, out y, out z)</c>
    /// (https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssurface_getnormal.html), this call is it, and
    /// a fallback that searched for other arities could only ever have returned null while reading
    /// like a working route.
    /// </summary>
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

    /// <summary>
    /// Radius and centre of a circular edge, read as <c>ksCurve3D.GetCurveParam()</c> →
    /// <c>ksCircle3dParam</c> (probe P2.6: <c>get_radius()</c>, the curve bbox and the length
    /// divided by 2π agree to the last digit on R10).
    ///
    /// Deliberately never computed as L/2π: on a straight 100 mm edge that formula yields a
    /// plausible-looking 15.915 mm, which is precisely the kind of number a caller would then
    /// believe. A non-circular curve therefore reports no radius at all.
    /// </summary>
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

    public IReadOnlyList<ReferenceDto> ResolveSelection(ResolveSelectionCommand command)
    {
        var document = RequireReferenceDocument(command.BodyRef);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ksBody body)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, "body_ref должен указывать на тело.");
        }

        // A predicate field the server does not apply must be refused, not ignored: a silently
        // dropped condition lets a candidate through that the caller believes it checked
        // (docs/05 §4.1 — unsupported values are rejected, never defaulted).
        var unsupported = UnsupportedPredicateFields(command.Predicate);
        if (unsupported.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Предикат содержит поля, которые сервер не применяет: " + string.Join(", ", unsupported) +
                ". Отдать за них молчаливый отказ честнее, чем выбрать грань по половине условия.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["unsupported"] = unsupported.ToArray() });
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
            // Нечитаемая нормаль остаётся отсутствием значения, а не нулевым вектором: (0,0,0)
            // прошёл бы по нормали как «не совпало» и был бы неотличим от измеренной нормали.
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

    /// <summary>
    /// Predicate fields the current implementation cannot honour. <c>surface_type</c> and
    /// <c>area_range_mm2</c>/<c>normal_direction</c> are applied; the rest are refused. Assembly
    /// space in particular has nothing to be resolved against while v1 has no assemblies at all.
    /// </summary>
    private static List<string> UnsupportedPredicateFields(SelectionPredicateDto predicate)
    {
        var unsupported = new List<string>();
        if (predicate.CoordinateSpace is not null)
        {
            unsupported.Add("coordinate_space");
        }

        if (predicate.ExtremumAxis is { Length: > 0 } || predicate.ExtremumMode is { Length: > 0 })
        {
            unsupported.Add("extremum_axis/extremum_mode");
        }

        if (predicate.BboxRangeMm is { Count: > 0 })
        {
            unsupported.Add("bbox_range_mm");
        }

        return unsupported;
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
                // Отбор по нормали грань не подтверждает и не опровергает: нормаль не прочитана.
                // Выдуманный вектор здесь дал бы «не совпало», то есть отказ, неотличимый от
                // измерения, — а не названное отсутствие данных.
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

    /// <summary>
    /// Конечные углы дуги для <c>ksArcByAngle</c>, приведённые в диапазон, который ядро принимает.
    /// </summary>
    /// <remarks>
    /// Измерено 20.09.2026 на поставке <c>publish-mania-20260920</c> зондом
    /// <c>scratch/_arc_angle_range_probe.py</c>: вызов отказывает тогда и только тогда, когда
    /// <c>start_deg + sweep_deg</c> покидает [−360°, 360°]. Старт ровно 360° и конец ровно 360°
    /// допустимы (R3, R4 зонда). Поэтому углы сдвигаются на целое число оборотов — ровно на столько,
    /// чтобы конец вошёл в диапазон; размах при сдвиге НЕ меняется, то есть дуга остаётся той же.
    /// Если конечный угол уже внутри диапазона, значения возвращаются КАК ЕСТЬ: вызов остаётся
    /// прежним. Но АДДИТИВНОСТЬ ДЕРЖИТСЯ ТОЛЬКО ВМЕСТЕ С ПОРЯДКОМ `Min`/`Max` У ВЫЗЫВАЮЩЕГО —
    /// см. комментарий в ветке `SketchEntityKind.Arc`: передача «начало, конец» вместо «меньший,
    /// больший» изменила результат на входах, которых правка не касалась (измерено контролем R8).
    /// <para>
    /// НЕ ИЗМЕРЕНО и потому НЕ трогается: случай <c>|sweep_deg| &gt; 360</c> с конечным углом ВНУТРИ
    /// диапазона (например старт −180°, sweep +400°). Такой размах одной дугой не выражается
    /// (два угла задают не более оборота), но что делает ядро сегодня — не измерялось, поэтому
    /// поведение оставлено прежним, а не заменено догадкой.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A collection element is exposed as <c>object</c>; it is either the definition interface
    /// directly or an <c>ksEntity</c> that has to be unwrapped (both shapes occur — spec 4.5).
    /// </summary>
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

    /// <summary>
    /// Mass-centre properties with an explicit unit selector: millimetres for length, kilograms
    /// for mass, so <c>v</c> is mm³, <c>F</c> mm² and <c>m</c> kg.
    /// </summary>
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

/// <summary>
/// Результат правки эскиза. <see cref="DeletedEntities"/> считает то, что действительно удалилось
/// (по соглашению вендора 1 = успех, и удаление идёт объектом, найденным по сохранённой
/// координате), а <see cref="ExpectedDeleted"/> — сколько ожидалось. Разница наружу не
/// сглаживается: частичная очистка означает грязный профиль, а не удалённую правку.
/// </summary>
/// <param name="ProbePointsFromModel">
/// True, когда координаты для поиска удаляемых объектов выведены из геометрии зависимого тела,
/// а не взяты из памяти сеанса. Вызывающему это нужно знать: маршрут работает для эскиза, которого
/// сервер не рисовал, но ограничен измеренной конфигурацией (основная XY, окружность в профиле,
/// сквозное вырезание) — см. <c>SketchPointDerivation</c>.
/// </param>
public sealed record EditSketchResult(
    int EntityCount,
    IReadOnlyList<string> Kinds,
    bool EditClosedOut,
    double? ProfileAreaMm2,
    int DeletedEntities = 0,
    int ExpectedDeleted = 0,
    bool ProbePointsFromModel = false);

public sealed record FinishSketchResult(bool ProfileClosedConfirmed, IReadOnlyList<string> UnverifiedAspects);

/// <summary>
/// Результат выдавливания. <see cref="TargetBodyIndex"/> — индекс того тела, которое вызывающий
/// объявил целью (null, когда цель не объявлялась), а <see cref="BodyChanges"/> — чем на самом
/// деле закончились измерения для каждого тела до и после. Оба поля существуют потому, что
/// <c>Create() == true</c> ничего не доказывает: проба P2.6 измерила, что при противоречии заявленной
/// цели и расположения контура КОМПАС отвечает true на всех вызовах и не меняет ни одного тела.
/// </summary>
public sealed record ExtrudeResult(
    ReferenceDto FeatureRef,
    int BodyCount,
    double? VolumeMm3,
    VerificationDto Verification,
    int? TargetBodyIndex = null,
    IReadOnlyList<string>? BodyChanges = null,
    /// <summary>Сумма объёмов всех тел документа ДО операции. null — не прочитана.</summary>
    double? DocumentVolumeBeforeMm3 = null,
    /// <summary>Сумма объёмов всех тел документа ПОСЛЕ операции — та же величина, что <see cref="VolumeMm3"/>.</summary>
    double? DocumentVolumeAfterMm3 = null,
    /// <summary>Приращение материала ЭТИМ признаком (у cut — снятое). null — не измерено, не ноль.</summary>
    double? VolumeDeltaMm3 = null,
    /// <summary>Основание приращения: <c>target_body_N</c>, <c>new_body_volume</c>, <c>existing_body_N_delta</c> или <c>not_attributable</c>.</summary>
    string? VolumeDeltaBasis = null,
    /// <summary>Что означает <see cref="VolumeMm3"/>. Названо, потому что раньше поле несло две разные величины.</summary>
    string? VolumeNote = null);

public sealed record RebuildResult(long NewRevision, DocumentContextDto Context);

public sealed record TopologyReadResult(
    int FaceCount,
    int RawEdgeReferences,
    int UniqueEdgeCount,
    IReadOnlyList<FaceRowDto> Faces,
    IReadOnlyList<EdgeRowDto> Edges);
