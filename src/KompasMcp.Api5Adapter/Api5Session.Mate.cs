using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Mates;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Mates domain — block C2, profile <c>mates-minimal-v1</c>, modes <c>MATE-01…MATE-06</c>.</summary>
/// <remarks>ROUTE — documented API7: <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add</c> →
/// <c>BaseObject1/2</c> → <c>Update()</c>; confirmation is <c>Valid</c>, not <c>Update()=true</c>.
/// DOC: <c>imodelobject_update.html</c>, <c>imodelobject_valid.html</c> - <c>Update</c>, <c>Valid</c>
/// from <c>IModelObject</c>; <c>ikompascollection_count.html</c> - <c>Count</c> from <c>IKompasCollection</c>.
/// LIMIT: <c>ksDocument3D.AddMateConstraint</c> is NOT used — it returned <c>False</c> under every
/// documented combination; cause not established, closed by decision.
/// History: docs/decisions/mates.md#inherited-doc-pages, #api5-addmateconstraint</remarks>
public sealed partial class Api5Session
{
    private const string MateRefKind = "mate";

    /// <summary>Kind of the reference a symmetry mate's mirror plane is addressed by — the same kind
    /// <c>kompas_create_aux_geometry</c> mints for a plane the product created.</summary>
    private const string SymmetryPlaneRefKind = "plane";

    /// <summary>How far the measured face-normal dot may drift from the value an explicit alignment
    /// names (-1 or +1) and still count as "landed where the word says".</summary>
    /// <remarks>MEASURED: the mated faces' dot product lands exactly on +/-1, so the tolerance only has
    /// to absorb read noise. History: docs/decisions/mates.md#alignment-geometry-criterion</remarks>
    private const double AlignmentDotTolerance = 1e-3;

    /// <summary>Payload of a mate reference: the API5 object, its ordinal and type.</summary>
    private sealed record MatePayload(ksMateConstraint Mate5, int Ordinal, short ConstraintType);

    /// <summary>A face handed to the mate, plus what measuring its ORIENTATION needs.</summary>
    /// <remarks>MEASURED: a component face's normal is read in the PART frame, not in the assembly
    /// frame — a component turned 30 deg about X still reports axis-aligned normals — so the assembly
    /// normal is the component's rotation applied to it. The rotation comes from the SAME documented
    /// <c>IPart7.GetSummMatrix</c> that <c>kompas_list_components</c> publishes.
    /// <para><see cref="LocalPoint"/> is the face plane's own placement origin in the PART frame, used by
    /// the symmetry check: a point OF the plane, so any point of it serves.</para>
    /// History: docs/decisions/mates.md#alignment-orientation</remarks>
    private sealed record FaceHandle(
        IModelObject Model, IPart7 Component7, IPart7? Parent7, double[]? LocalNormal,
        double[]? LocalPoint = null);

    // ── MATE-02 ──
    /// <summary>Enumerate the assembly's mates.</summary>
    public ListMatesResult ListMates(ListMatesCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        var notes = new List<string>();
        var rows = ReadMates(document, notes);

        return new ListMatesResult(
            rows,
            rows.Count,
            "API5 ksDocument3D.MateConstraintCollection → GetCount/GetByIndex → ksMateConstraint." +
            "GetBaseObj/constraintType/distance/fixed; подтверждение — IMateConstraint3D.Valid (API7)",
            notes);
    }

    // ── MATE-01 ──
    /// <summary>Create a mate between faces of two components.</summary>
    /// <remarks>ROUTE — documented API7 <c>IPart7.MateConstraints → IMateConstraints3D.Add(type)</c> →
    /// <c>BaseObject1/2</c> → <c>Update()</c>, plus the type's own documented interface:
    /// <c>IMate3DSymmetry.Plane</c> and <c>IMate3DDependentPosition.BySample/SampleObject1/2</c>.
    /// DOC: <c>mateconstrainttype.html</c>, <c>imateconstraints3d_add.html</c>,
    /// <c>imate3dsymmetry_plane.html</c>, <c>imate3ddependentposition_*.html</c>.
    /// History: docs/decisions/assembly-ext.md#mate-types</remarks>
    public CreateMateResult CreateMate(CreateMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var type = MateTypeFromName(command.ConstraintType);
        // VALIDATED BEFORE THE MATE EXISTS: an unknown alignment must be refused before Add() creates a
        // mate. Called inside the try below, it would throw AFTER the mate was already in the model,
        // leaving the revision unaware of a real change.
        var alignment = AlignmentFromName(command.Alignment);
        // THE RATIO IS CHECKED HERE FOR THE SAME REASON: a zero, negative or NaN scale accepted by
        // SetScale would put a mate into the model that the caller reads as a ratio.
        var isTransmission = type == MateConstraintType.mc_Transmission;
        if (isTransmission)
        {
            RequireTransmissionInputs(command);
        }

        // THE NEW KINDS HAVE NO EDITABLE PARAMETER AND THEIR OWN REQUIRED FIELDS: both are refused here,
        // before anything reaches the model (a symmetry without a plane, or a dependent without samples,
        // would be a mate the kernel cannot solve).
        ValidateMateExt(command);
        var isSymmetric = type == MateConstraintType.mc_Symmetric;
        var isDependent = type == MateConstraintType.mc_Dependent;
        var bySample = command.BySample ?? true;

        var notes = new List<string>();
        var top = TopPart7(document, notes);

        // FACES address an ordinary mate and the two objects of a symmetry; a DEPENDENT POSITION's base
        // objects are the COMPONENTS themselves (the measured route), so no face is taken for it.
        FaceHandle? first = null;
        FaceHandle? second = null;
        IPart7? dependentPart = null;
        IPart7? basePart = null;
        IPart7? sample1Part = null;
        IPart7? sample2Part = null;
        if (!isDependent)
        {
            first = FaceObject7(document, command.FirstComponentRef,
                RequireFaceIndex(command.FirstFaceIndex, "first_face_index"), notes);
            second = FaceObject7(document, command.SecondComponentRef,
                RequireFaceIndex(command.SecondFaceIndex, "second_face_index"), notes);
        }
        else
        {
            // FOUR ADDRESSES, TWO KINDS: the base objects are the two components of the mate (the measured
            // route: BaseObject1/2 = the parts), while SampleObject1/2 are written from their OWN refs.
            // Substituting one for the other would make the answer disagree with the request.
            dependentPart = RequireComponent(document, command.FirstComponentRef, notes);
            basePart = RequireComponent(document, command.SecondComponentRef, notes);
            sample1Part = RequireComponent(document, command.SampleObject1Ref!, notes);
            sample2Part = RequireComponent(document, command.SampleObject2Ref!, notes);
        }

        IPlane3D? plane = null;
        if (isSymmetric)
        {
            plane = RequireSymmetryPlane(document, command.SymmetryPlaneRef!, notes);
        }

        // THE COMPONENT PAIR WHOSE ORIGINS ARE REPORTED AND WHOSE IDENTITY IS COMPARED: faces are the
        // mates' base objects for ordinary kinds and symmetry, the components themselves for dependent.
        var firstComponent = isDependent ? dependentPart! : first!.Component7;
        var secondComponent = isDependent ? basePart! : second!.Component7;
        var originsBefore = OriginsOf(top, firstComponent, secondComponent);

        var mates7 = RequireMateConstraints(document);

        // THE COUNTER IS NOT REPLACED BY ZERO: `?? 0` would mean "no mates" where the number was not
        // read, and `?? before` would mean "unchanged" — both replace the UNKNOWN with the KNOWN.
        var before = RequireMateCount(document, mates7, "до создания");
        var beforeRows = ReadMates(document, notes);

        IMateConstraint3D? mate;
        IMate3DTransmission? transmission = null;
        IMate3DSymmetry? symmetry = null;
        IMate3DDependentPosition? dependent = null;
        try
        {
            mate = mates7.Add(type);
            if (mate is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "IMateConstraints3D.Add вернул null: сопряжение не создано.",
                    RetryPolicy.SameOperationId);
            }

            if (isTransmission)
            {
                // DOC: imate3dtransmission.html — «получение через IMateConstraints3D::Add и
                // приведение QueryInterface». A mate that does not cast has no documented properties.
                transmission = mate as IMate3DTransmission
                    ?? throw new KompasContractException(
                        ErrorCodes.CapabilityUnavailable,
                        "Сопряжение создано, но не приводится к IMate3DTransmission: параметры передачи "
                        + "этим маршрутом недоступны.",
                        RetryPolicy.AfterReconciliation);
            }

            mate.BaseObject1 = isDependent ? (IModelObject)dependentPart! : first!.Model;
            mate.BaseObject2 = isDependent ? (IModelObject)basePart! : second!.Model;
            if (alignment is { } requestedAlignment)
            {
                mate.Alignment = requestedAlignment;
            }

            if (command.ParamValue is { } value)
            {
                mate.ParamValue = value;
            }

            if (isSymmetric)
            {
                // DOC: imate3dsymmetry.html / imate3dsymmetry_plane.html — «Плоскость симметрии»,
                // an IModelObject. A mate that does not cast has no documented properties.
                symmetry = mate as IMate3DSymmetry
                    ?? throw new KompasContractException(
                        ErrorCodes.CapabilityUnavailable,
                        "Сопряжение создано, но не приводится к IMate3DSymmetry: поле Plane этим маршрутом "
                        + "недоступно.",
                        RetryPolicy.AfterReconciliation);
                symmetry.Plane = plane!;
            }

            if (isDependent)
            {
                // DOC: imate3ddependentposition_props.html — BySample, SampleObject1/2 («образец —
                // зависимый / базовый компонент»).
                dependent = mate as IMate3DDependentPosition
                    ?? throw new KompasContractException(
                        ErrorCodes.CapabilityUnavailable,
                        "Сопряжение создано, но не приводится к IMate3DDependentPosition: BySample и "
                        + "образцы этим маршрутом недоступны.",
                        RetryPolicy.AfterReconciliation);
                dependent.BySample = bySample;
                if (bySample)
                {
                    dependent.SampleObject1 = SampleObject(document, sample1Part!, command.SampleObject1Ref!);
                    dependent.SampleObject2 = SampleObject(document, sample2Part!, command.SampleObject2Ref!);
                }
            }

            if (transmission is not null)
            {
                ApplyTransmission(transmission, command, first!.Model, second!.Model, notes);
            }
        }
        catch (COMException ex)
        {
            // The mate object ALREADY exists (Add created it): a partial effect must bump the
            // revision and revoke references, otherwise the model changed while the revision and
            // references stay old (defect M4).
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Создание сопряжения прервалось: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }
        catch (KompasContractException)
        {
            // Same reason as above: Add() already put a mate into the model.
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw;
        }

        bool? updated;
        try
        {
            updated = mate.Update();
        }
        catch (COMException ex)
        {
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"IMateConstraint3D.Update() бросил: {ex.Message}. Сопряжение не подтверждено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document3D.RebuildDocument();
        var valid = Bool(() => mate.Valid);
        var after = RequireMateCount(document, mates7, "после создания");

        // THE READ-BACK IS TAKEN FROM THE SAME API7 OBJECT THAT WAS WRITTEN, not from the ordinal
        // correspondence API7↔API5 that reading the collection has to assume: a mismatch read from a
        // foreign mate would accuse the wrong one. It is a FACT for the answer, not a criterion.
        var alignmentRead = AlignmentName(EnumOrNull<ksMateConstraintAlignmentEnum>(() => mate.Alignment));

        // CONFIRMATION IS Valid, not "Update()=true": a mate between two objects of the SAME component
        // also gives Update()=true but Valid=false.
        if (updated != true || valid != true)
        {
            // An invalid mate ALREADY lies in the assembly: the model changed, and the revision must
            // show it.
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            // The TWO KINDS OF THIS ORDER REPORT A KERNEL REFUSAL, not a verification claim: the mate is
            // named by type, and the snapshot carries what was read back, so the class «Отказ ядра
            // КОМПАС» (AGENTS.md) can be judged from the answer alone.
            var refusalCode = isSymmetric || isDependent
                ? ErrorCodes.GeometryFailed
                : ErrorCodes.VerificationFailed;
            throw new KompasContractException(
                refusalCode,
                $"Сопряжение НЕ подтверждено: Update()={updated}, Valid={valid}, сопряжений " +
                $"{before} → {after}. Запись могла быть создана, но недействительна.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["update"] = updated,
                    ["valid"] = valid,
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                    ["failure_snapshot"] = new Dictionary<string, object?>
                    {
                        ["constraint_type"] = command.ConstraintType,
                        ["update"] = updated,
                        ["valid"] = valid,
                        ["mate_count_before"] = before,
                        ["mate_count_after"] = after,
                        ["first_component_ref"] = command.FirstComponentRef,
                        ["second_component_ref"] = command.SecondComponentRef,
                        ["symmetry_plane_ref"] = command.SymmetryPlaneRef,
                        ["by_sample"] = isDependent ? bySample : null,
                        ["sample_object1_ref"] = command.SampleObject1Ref,
                        ["sample_object2_ref"] = command.SampleObject2Ref,
                    },
                });
        }

        // THE READ-BACK NUMBER IS A FACT, NOT A CRITERION. MEASURED: a requested `opposite` on
        // `coincidence` is read back as `closest` while the two faces still land opposite (s = -1), so
        // a refusal on the number alone would reject a result that is exactly what was asked for. The
        // number goes back in the answer as read; the judge is the measured orientation below.
        // History: docs/decisions/mates.md#alignment-geometry-criterion

        // THE MEASURED ORIENTATION, not a retelling of the number: the dot product of the two mated
        // faces' normals in assembly coordinates, read after the solve. `closest` NAMES NO ORIENTATION
        // — it asks the kernel to choose — so it is not checked at all and the dot is returned as a
        // fact. MEASURED: `opposite` lands on -1 and `cooriented` on +1 on both placements.
        // A DEPENDENT POSITION IS NOT ALIGNED BY FACES: its base objects are the components, so there is
        // no pair of face normals to judge and the value is not checked (never silently passed either —
        // an alignment on this type is named in the notes below).
        var dotAfter = command.Alignment is null || isDependent ? null : FaceNormalsDot(first!, second!);

        // AN EXPLICIT VALUE NAMES AN ORIENTATION, and the geometry must land where the word says. This
        // is the ONLY gate: a value the type cannot express (perpendicular — the faces are at a right
        // angle, so the dot is ~0 whatever the value says) yields no decision here and is NAMED in the
        // notes instead of being refused.
        var dotSatisfied = MateAlignmentPolicy.DotSatisfies(
            command.ConstraintType, command.Alignment, dotAfter, AlignmentDotTolerance);
        var expectedDot = MateAlignmentPolicy.DotConfirmsValue(command.ConstraintType)
            ? MateAlignmentPolicy.ExpectedDot(command.Alignment)
            : null;

        if (dotSatisfied == false)
        {
            BumpRevision(document, "mate.create.partial", invalidateAll: true);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Сопряжение создано (сопряжений {before} → {after}, Valid={valid}), но ИЗМЕРЕННАЯ "
                + $"ориентация граней другая: «{command.Alignment}» требует {expectedDot:0.####}, "
                + $"измерено {dotAfter:0.####} (перечитано «{alignmentRead ?? "не читается"}»). "
                + "Геометрия встала не туда, куда просили; успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["alignment_requested"] = command.Alignment,
                    ["alignment_read"] = alignmentRead,
                    ["face_normals_dot"] = dotAfter,
                    ["face_normals_dot_expected"] = expectedDot,
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        BumpRevision(document, "mate.create");

        var rows = ReadMates(document, notes);

        // THE NEW MATE IS FOUND BY SET DIFFERENCE, NOT BY "THE LAST ROW": `rows.LastOrDefault()` assumes
        // the new mate was appended and that the collection order matches the API7 indexer — the same
        // unverified correspondence as for components. The difference is over the MULTISET of signatures,
        // so two identical mates still yield exactly one new one.
        var created = FindNewMate(beforeRows, rows, out var createdNote);
        notes.Add(createdNote);
        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Сопряжение создано (сопряжений " + before + " → " + after + "), но выделить его в " +
                "коллекции не удалось: " + createdNote + ". Адресовать сопряжение нечем.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        if (after <= before)
        {
            // The mate count did not grow while the result was already returned as success — the same
            // defect class as "success on a failed check".
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Сопряжение НЕ создано: число сопряжений {before} → {after} при Update()={updated} " +
                "и Valid=true. Успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        // THE BASE OBJECTS ARE COMPARED WITH THE REQUESTED ONES by the route the product already uses for
        // object identity: `GetBaseObj(1|2)` → `IModelObject.Part`, matched by RCW identity.
        // LIMIT: the FACE inside a component is not readable back through this route, so the comparison
        // proves the two requested COMPONENTS, one per base object — not the face index within it.
        // History: docs/decisions/mates.md#read-back-objects
        var mate5 = MateAt(document, created.Ordinal ?? 0);
        var firstSide = 0;
        var secondSide = 0;
        var objectsMatch = !isDependent
            && mate5 is not null
            && MateTouchesComponent(document, mate5, firstComponent, out firstSide)
            && MateTouchesComponent(document, mate5, secondComponent, out secondSide)
            && firstSide != secondSide;

        var stored = References.Register(
            MateRefKind, document.Id, document.Revision,
            new MatePayload(mate5!, created.Ordinal ?? 0, (short)type));

        // NOT READ IS NOT CONFIRMED: the aspects this kind cannot be checked on are NAMED here, and the
        // list is filled by the block-specific checks below as well.
        var unverified = new List<string>();

        var checks = new List<NamedCheck>
        {
            new("mate_created", after > before, Observed: $"{before} → {after}",
                Expected: $"{before + 1}"),
            new("mate_valid", valid == true, Observed: $"Valid={valid}"),
        };

        if (!isDependent)
        {
            checks.Add(new("mate_read_back", objectsMatch,
                Observed: $"тип «{created.ConstraintType}», компоненты запрошенных граней найдены в базовых "
                          + $"объектах {firstSide} и {secondSide}",
                Expected: "по одному запрошенному компоненту на каждый базовый объект (1 и 2)"));
        }
        else
        {
            // MEASURED: on a dependent position `GetBaseObj(1|2)` answers an object whose owner component
            // is not readable through the face route (both sides read as «0»), so the identity of the two
            // BASE objects is not claimed — the mate's own sample fields carry it, and the gap is named.
            unverified.Add("base_object_identity_unverified — базовые объекты зависимого положения не "
                + "сверены по тождеству: GetBaseObj даёт объект, компонента-владельца которого этим "
                + "маршрутом не прочитать. Сверены Valid, BySample и оба образца (SampleObject1/2)");
        }

        // ONLY THE FACE KINDS REFUSE HERE: a dependent position's base objects are not compared, and the
        // gap is NAMED above instead of being turned into a failed check.
        if (!objectsMatch && !isDependent)
        {
            // The mate exists in the model, so the refusal carries the partial effect and the revision
            // already bumped above — the same outcome the ratio check below has for a failed read-back.
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Сопряжение создано, но прочитанные обратно базовые объекты — НЕ запрошенные: компоненты "
                + $"найдены в базовых объектах {firstSide} и {secondSide}, а запрошены два разных "
                + $"(«{command.FirstComponentRef}» и «{command.SecondComponentRef}»). Сверены "
                + "компоненты-владельцы; грань внутри компонента этим маршрутом не читается. Успехом это "
                + "не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["first_base_object"] = firstSide,
                    ["second_base_object"] = secondSide,
                    ["mate_read_back_scope"] = "component_owner_only",
                });
        }

        // AN EXPLICIT VALUE IS CONFIRMED BY THE MEASURED ORIENTATION, where the type allows it. The
        // read-back number is NOT a check: it is a fact returned in the answer, and a check expecting
        // "the number was retained" would be the false criterion this order removed.
        if (dotSatisfied is { } orientationOk && expectedDot is not null && dotAfter is not null)
        {
            checks.Add(new("mate_orientation", orientationOk,
                Observed: $"n1·n2 = {dotAfter.Value:0.####}",
                Expected: $"{expectedDot.Value:0.####}"));
        }

        // THE RATIO IS CONFIRMED BY THE MODEL: SetScale returning true means the number was ACCEPTED,
        // not that the mate keeps it. MEASURED: Scale1/Scale2 read back as written on cylindrical
        // faces. The motion of the driven component is NOT checked — see the note below.
        if (isTransmission)
        {
            var ratio = MateTransmissionPolicy.RatioMatches(
                created.Scale1, created.Scale2, command.Scale1!.Value, command.Scale2!.Value);
            checks.Add(new("transmission_ratio_read_back", ratio == true,
                Observed: $"{created.Scale1:0.####}:{created.Scale2:0.####}",
                Expected: $"{command.Scale1:0.####}:{command.Scale2:0.####}"));
            if (ratio != true)
            {
                throw new KompasContractException(
                    ErrorCodes.VerificationFailed,
                    "Передача создана и подтверждена (Valid=true), но коэффициент НЕ перечитан: "
                    + $"из модели {created.Scale1?.ToString("0.####") ?? "не читается"}:"
                    + $"{created.Scale2?.ToString("0.####") ?? "не читается"}, запрошено "
                    + $"{command.Scale1:0.####}:{command.Scale2:0.####}. Успехом это не считается.",
                    RetryPolicy.AfterReconciliation,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["scale1_requested"] = command.Scale1,
                        ["scale2_requested"] = command.Scale2,
                        ["scale1_read"] = created.Scale1,
                        ["scale2_read"] = created.Scale2,
                    });
            }
        }

        // NOT READ IS NOT CONFIRMED, and it is also not a reason to refuse a call our own instrument
        // cannot measure: the gap is NAMED so nobody reads the success as proof of the orientation.
        // ── MATE-08: the symmetry is confirmed by the MIRROR, not by the write ──
        var originsAfter = OriginsOf(top, firstComponent, secondComponent);
        bool? planeRead = null;
        bool? bySampleRead = null;
        string? sample1 = null;
        string? sample2 = null;
        if (isSymmetric)
        {
            // "Plane reads back as the SAME object" is an object identity, not a name: the mate is asked
            // for its own Plane and the answer is compared with the object that was written.
            var planeBack = Ref(() => symmetry!.Plane);
            planeRead = planeBack is not null && ReferenceEquals(planeBack, plane);
            checks.Add(new("symmetry_plane_read_back", planeRead == true,
                Observed: planeBack is null ? "Plane не читается" : "Plane прочитана",
                Expected: "тот же объект, что записан"));

            // THE GEOMETRY: the second face must lie IN the mirror of the first face's plane about the
            // symmetry plane. The fixture-driven expectation «the second component's origin is the mirror»
            // is a special case of this and would not hold in general (see SymmetryMirror).
            var mirrored = MirroredPlane(first!, plane!);
            var secondPoint = AssemblyPoint(second!);
            var secondNormal = AssemblyNormal(second!);
            var mirrorDistance = mirrored is null || secondPoint is null
                ? null
                : SymmetryMirror.DistanceMm(secondPoint, mirrored.Value.Point, mirrored.Value.Normal);
            var mirrorParallel = mirrored is null || secondNormal is null
                ? null
                : SymmetryMirror.ParallelDeviation(mirrored.Value.Normal, secondNormal);
            var mirroredOk = mirrorDistance is { } distance && distance <= SymmetryMirror.ToleranceMm
                && mirrorParallel is { } parallel && parallel <= SymmetryMirror.NormalTolerance;
            if (mirrorDistance is null || mirrorParallel is null)
            {
                unverified.Add("symmetry_mirror_unverified — зеркало не выведено из входа: не прочитана "
                    + "плоскость симметрии, точка или нормаль грани, либо матрица компонента. Совпадение "
                    + "с зеркалом НЕ подтверждено");
            }
            else
            {
                checks.Add(new("symmetry_mirror", mirroredOk,
                    Observed: $"расстояние {mirrorDistance:0.####} мм, |cos|−1 = {mirrorParallel:0.######}",
                    Expected: $"≤ {SymmetryMirror.ToleranceMm} мм, ≤ {SymmetryMirror.NormalTolerance}"));
                if (!mirroredOk)
                {
                    throw SymmetryRefusal(document, command, planeRead, mirrorDistance.Value,
                        mirrorParallel.Value, before, after);
                }
            }
        }

        // ── MATE-09: the dependent position is confirmed by its OWN fields, read back ──
        if (isDependent)
        {
            bySampleRead = Bool(() => dependent!.BySample);
            checks.Add(new("dependent_by_sample_read_back", bySampleRead == bySample,
                Observed: $"BySample={bySampleRead}", Expected: $"BySample={bySample}"));
            var sampleBack1 = Ref(() => dependent!.SampleObject1);
            var sampleBack2 = Ref(() => dependent!.SampleObject2);
            var same1 = ReferenceEquals(sampleBack1, sample1Part);
            var same2 = ReferenceEquals(sampleBack2, sample2Part);
            if (bySample)
            {
                checks.Add(new("dependent_sample1_read_back", same1,
                    Observed: sampleBack1 is null ? "не читается" : "прочитан",
                    Expected: "тот же компонент, что записан"));
                checks.Add(new("dependent_sample2_read_back", same2,
                    Observed: sampleBack2 is null ? "не читается" : "прочитан",
                    Expected: "тот же компонент, что записан"));
                if (!same1 || !same2)
                {
                    throw DependentRefusal(document, command, bySampleRead, same1, same2, before, after);
                }

                sample1 = command.SampleObject1Ref;
                sample2 = command.SampleObject2Ref;
            }
        }
        if (isTransmission)
        {
            // MEASURED: the driven component did not move by either documented placement route, and the
            // positioner route refused on a component that carries no mate at all. The mate is in the
            // model and its parameters are read from it; the MOTION is not claimed.
            unverified.Add("transmission_kinematics_not_claimed — коэффициент и направления прочитаны из "
                + "модели, но передача движения ведомому компоненту не проверяется: документированный "
                + "маршрут перемещения под сопряжением связь не показал");
        }
        if (expectedDot is not null && dotAfter is null)
        {
            unverified.Add("alignment_orientation_unverified — ориентация граней не прочитана (нет "
                + "единственной нормали у неплоской грани либо не читается матрица компонента): "
                + "применение выравнивания К ГЕОМЕТРИИ не подтверждено");
        }

        // THE VALUE CANNOT BE CONFIRMED BY THIS GEOMETRY: a perpendicular mate keeps the faces at a
        // right angle, so the dot is ~0 whatever `opposite`/`cooriented` says. The value is neither
        // confirmed nor refused — the gap is NAMED, so the success is not read as proof of it.
        if (MateAlignmentPolicy.ExpectedDot(command.Alignment) is not null
            && !MateAlignmentPolicy.DotConfirmsValue(command.ConstraintType))
        {
            unverified.Add("alignment_value_unconfirmable — тип «" + command.ConstraintType + "» не "
                + "выражает ориентацию знаком скалярного произведения нормалей (грани перпендикулярны, "
                + "s ≈ 0): запрошенное значение геометрией не подтверждается");
        }

        return new CreateMateResult(
            ToDto(References.Require(stored.Id, document.Id, document.Revision), "mate"),
            created with { MateRef = stored.Id },
            after,
            command.Alignment,
            alignmentRead,
            dotAfter,
            new VerificationDto(VerificationLevel.StructureChecked, checks,
                notes.Concat(unverified).ToList()),
            planeRead,
            bySampleRead,
            sample1,
            sample2,
            originsBefore,
            originsAfter,
            valid);
    }

    // ── MATE-08 / MATE-09: inputs, geometry and refusals of the two extra kinds ──

    /// <summary>The face number a mate of this kind takes, refused by NAME when it is absent — a mate
    /// built from "the first face" would be an address substitution.</summary>
    private static int RequireFaceIndex(int? index, string field) =>
        index ?? throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Поле «{field}» обязательно: базовыми объектами этого вида сопряжения служат грани "
            + "компонентов.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["field"] = field });

    /// <summary>The inputs of <c>symmetric</c> and <c>dependent</c>, refused BEFORE the mate exists.</summary>
    /// <remarks>INVARIANT: a field accepted and never applied is the defect this project forbids, so a
    /// plane on another kind, a face number on <c>dependent</c> and samples of a disabled sample mode are
    /// all refusals NAMING the field. The type is read from the REQUEST here (the mate does not exist
    /// yet); the edit and delete routes read it from the MODEL.
    /// <para>LIMIT: <c>by_sample=false</c> is documented (<c>imate3ddependentposition_props.html</c>) and
    /// not measured: it is refused CAPABILITY_UNAVAILABLE instead of being offered untested.</para>
    /// TEST: <c>MateExtValidationTests</c>. History: docs/decisions/assembly-ext.md#mate-types</remarks>
    public static void ValidateMateExt(CreateMateCommand command)
    {
        // THE NAME IS COMPARED AS A NAME: the interop enum must not be touched here, or the rule could
        // only be tested on a machine with КОМПАС installed.
        var isSymmetric = string.Equals(command.ConstraintType, "symmetric", StringComparison.Ordinal);
        var isDependent = string.Equals(command.ConstraintType, "dependent", StringComparison.Ordinal);
        var sampleField = command.BySample is not null ? "by_sample"
            : command.SampleObject1Ref is not null ? "sample_object1_ref"
            : command.SampleObject2Ref is not null ? "sample_object2_ref"
            : null;

        if (!isSymmetric && !string.IsNullOrWhiteSpace(command.SymmetryPlaneRef))
        {
            throw MateExtFieldRefusal("symmetry_plane_ref", "symmetric", command.ConstraintType);
        }

        if (!isDependent && sampleField is not null)
        {
            throw MateExtFieldRefusal(sampleField, "dependent", command.ConstraintType);
        }

        if (isSymmetric && string.IsNullOrWhiteSpace(command.SymmetryPlaneRef))
        {
            throw MateExtMissing("symmetry_plane_ref", "symmetric",
                "плоскость симметрии — обязательный объект этого сопряжения");
        }

        if (isDependent)
        {
            // THE BASE OBJECTS OF A DEPENDENT POSITION ARE THE COMPONENTS: a face number would be accepted
            // and never applied, so it is refused by name rather than ignored.
            if (command.FirstFaceIndex is not null || command.SecondFaceIndex is not null)
            {
                throw MateExtFieldRefusal(
                    command.FirstFaceIndex is not null ? "first_face_index" : "second_face_index",
                    null, command.ConstraintType);
            }

            if (command.BySample == false)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Режим by_sample=false документирован (BySample: BOOL, «по образцу»), но не измерен: "
                    + "годный набор объектов для него не установлен. Поддерживается by_sample=true "
                    + "(по умолчанию) с двумя образцами.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["field"] = "by_sample", ["value"] = false });
            }

            if (string.IsNullOrWhiteSpace(command.SampleObject1Ref))
            {
                throw MateExtMissing("sample_object1_ref", "dependent",
                    "при by_sample=true образец зависимого компонента обязателен");
            }

            if (string.IsNullOrWhiteSpace(command.SampleObject2Ref))
            {
                throw MateExtMissing("sample_object2_ref", "dependent",
                    "при by_sample=true образец базового компонента обязателен");
            }

            return;
        }

        if (command.FirstFaceIndex is null)
        {
            throw MateExtMissing("first_face_index", command.ConstraintType,
                "базовыми объектами этого вида сопряжения служат грани компонентов");
        }

        if (command.SecondFaceIndex is null)
        {
            throw MateExtMissing("second_face_index", command.ConstraintType,
                "базовыми объектами этого вида сопряжения служат грани компонентов");
        }
    }

    private static KompasContractException MateExtFieldRefusal(
        string field, string? onlyFor, string requestedType) =>
        new(
            ErrorCodes.InvalidArgument,
            onlyFor is null
                ? $"Поле «{field}» не относится к constraint_type={requestedType}: базовые объекты этого "
                  + "вида — сами компоненты, и номер грани был бы принят и никогда не применён."
                : $"Поле «{field}» относится только к constraint_type={onlyFor}: у «{requestedType}» его "
                  + "нет, и принятое значение было бы записано и проигнорировано.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = field,
                ["constraint_type"] = requestedType,
                ["supported_for"] = onlyFor,
            });

    private static KompasContractException MateExtMissing(string field, string type, string why) =>
        new(
            ErrorCodes.InvalidArgument,
            $"При constraint_type={type} поле «{field}» обязательно: {why}.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["field"] = field, ["constraint_type"] = type });

    /// <summary>A component reference written into the mate, with its identity confirmed against the
    /// live collection before anything is written.</summary>
    /// <remarks>INVARIANT: the sample refs are `component:` references; another kind is
    /// INVALID_ARGUMENT (the caller named the wrong object), while a component whose identity does not
    /// match its record is STALE_REFERENCE — the same rule as an addressed face.
    /// History: docs/decisions/assembly-ext.md#mate-types</remarks>
    private IPart7 RequireComponent(DocumentEntry document, string componentRef, List<string> notes)
    {
        var stored = References.Require(componentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка «{componentRef}» не адресует компонент: это {stored.Kind}. Образец зависимого "
                + "положения задаётся ссылкой component: из kompas_list_components.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["reference_kind"] = stored.Kind,
                    ["supported_kinds"] = new[] { "component" },
                });
        }

        var part5 = ComponentPart5At(document, payload.Ordinal);
        if (part5 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Компонент по порядковому номеру {payload.Ordinal} не получен как ksPart: образец "
                + "адресовать нечем.",
                RetryPolicy.ReacquireContext);
        }

        var identity = IdentityMatches(part5, payload, out var identityNote);
        notes.Add("component_identity — " + identityNote);
        if (identity != true)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                "Компонент адресован по номеру, тождество которого НЕ подтверждено: " + identityNote +
                ". Зависимое положение по неподтверждённому адресу не создаётся; перечитайте структуру "
                + "сборки kompas_list_components и возьмите свежую ссылку.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["ordinal"] = payload.Ordinal,
                    ["identity_confirmed"] = identity is null ? "не сверено (источник не прочитан)" : "расхождение источника",
                });
        }

        return payload.Part7;
    }

    /// <summary>The mirror plane a <c>symmetric</c> mate is built about, by its <c>plane:</c> reference.</summary>
    /// <remarks>A reference of another kind is INVALID_ARGUMENT with the kind named: a mirror plane is a
    /// plane, and substituting another object for it would be an address substitution.
    /// DOC: <c>imate3dsymmetry_plane.html</c>. History: docs/decisions/assembly-ext.md#mate-types</remarks>
    private IPlane3D RequireSymmetryPlane(DocumentEntry document, string planeRef, List<string> notes)
    {
        var stored = References.Require(planeRef, document.Id, document.Revision);
        if (stored.Payload is not IPlane3D plane)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка «{planeRef}» не адресует плоскость: это {stored.Kind}. Плоскость симметрии "
                + "задаётся ссылкой plane: (kompas_create_aux_geometry kind=plane либо "
                + "kompas_list_aux_geometry).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["symmetry_plane_ref"] = planeRef,
                    ["reference_kind"] = stored.Kind,
                    ["supported_kinds"] = new[] { "plane" },
                });
        }

        notes.Add("плоскость симметрии взята по ссылке plane: и перенесена в сопряжение как IModelObject");
        return plane;
    }

    /// <summary>The sample object of a dependent position: the interop declares <c>SampleObject1/2</c> as
    /// the concrete <c>Part7</c>, so a component that does not cast is named, not replaced.</summary>
    private static Part7 SampleObject(DocumentEntry document, IPart7 component, string componentRef) =>
        component as Part7 ?? throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Компонент «{componentRef}» не приводится к Part7: SampleObject1/2 этим маршрутом не задаются.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["component_ref"] = componentRef,
                ["document_id"] = document.Id,
            });

    /// <summary>The mirror of the FIRST face's plane about the symmetry plane, in assembly coordinates;
    /// <c>null</c> when any of the four reads (plane point, plane normal, face point, face normal, matrix)
    /// is absent.</summary>
    private (double[] Point, double[] Normal)? MirroredPlane(FaceHandle first, IPlane3D plane)
    {
        var read = Api7AuxGeometry.ReadPlane(plane, -1);
        if (read.PointMm is not { Length: 3 } planePoint || read.DirectionMm is not { Length: 3 } planeNormal)
        {
            return null;
        }

        var frame = new PlaneFrame(planePoint, planeNormal);
        var firstPoint = AssemblyPoint(first);
        var firstNormal = AssemblyNormal(first);
        if (firstPoint is null || firstNormal is null)
        {
            return null;
        }

        var mirroredPoint = SymmetryMirror.ReflectPoint(firstPoint, frame);
        var mirroredNormal = SymmetryMirror.ReflectNormal(firstNormal, frame);
        return mirroredPoint is null || mirroredNormal is null ? null : (mirroredPoint, mirroredNormal);
    }

    /// <summary>A face plane's point in ASSEMBLY coordinates: the component's
    /// <c>IPart7.GetSummMatrix</c> applied to the part-frame point, read fresh.</summary>
    private static double[]? AssemblyPoint(FaceHandle face)
    {
        if (face.LocalPoint is not { Length: 3 } local || face.Parent7 is null
            || SummMatrix7(face.Parent7, face.Component7) is not { Length: >= 16 } matrix)
        {
            return null;
        }

        return
        [
            matrix[0] * local[0] + matrix[4] * local[1] + matrix[8] * local[2] + matrix[12],
            matrix[1] * local[0] + matrix[5] * local[1] + matrix[9] * local[2] + matrix[13],
            matrix[2] * local[0] + matrix[6] * local[1] + matrix[10] * local[2] + matrix[14],
        ];
    }

    /// <summary>The origins of both components in assembly coordinates — the same read
    /// <c>kompas_list_components</c> publishes; <c>null</c> when a matrix is not read.</summary>
    private static double[][]? OriginsOf(IPart7? top, IPart7 first, IPart7 second)
    {
        if (top is null || OriginOf(top, first) is not { } origin1 || OriginOf(top, second) is not { } origin2)
        {
            return null;
        }

        return [origin1, origin2];
    }

    private static double[]? OriginOf(IPart7 top, IPart7 component) =>
        SummMatrix7(top, component) is { Length: >= 16 } matrix
            ? [matrix[12], matrix[13], matrix[14]]
            : null;

    /// <summary>Refusal of a symmetry whose MEASURED mirror did not land, with the snapshot of what was
    /// read — the mate is already in the model, so the effect is partial.</summary>
    private static KompasContractException SymmetryRefusal(
        DocumentEntry document, CreateMateCommand command, bool? planeRead, double distance,
        double parallel, int before, int after) =>
        new(
            ErrorCodes.GeometryFailed,
            $"Симметрия создана (Valid=true, сопряжений {before} → {after}), но ИЗМЕРЕННОЕ зеркало не "
            + $"совпало: вторая грань отстоит от зеркала первой на {distance:0.####} мм "
            + $"(допуск {SymmetryMirror.ToleranceMm}), нормали расходятся на {parallel:0.######} "
            + $"(допуск {SymmetryMirror.NormalTolerance}). Геометрия встала не туда; успехом это не "
            + "считается.",
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["constraint_type"] = command.ConstraintType,
                ["symmetry_plane_ref"] = command.SymmetryPlaneRef,
                ["symmetry_plane_read_back"] = planeRead,
                ["mate_count_before"] = before,
                ["mate_count_after"] = after,
                ["document_id"] = document.Id,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["valid"] = true,
                    ["symmetry_plane_ref"] = command.SymmetryPlaneRef,
                    ["symmetry_plane_read_back"] = planeRead,
                    ["mirror_distance_mm"] = distance,
                    ["mirror_normal_deviation"] = parallel,
                    ["mirror_tolerance_mm"] = SymmetryMirror.ToleranceMm,
                    ["mirror_normal_tolerance"] = SymmetryMirror.NormalTolerance,
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                },
            });

    /// <summary>Refusal of a dependent position whose OWN fields did not read back as written.</summary>
    private static KompasContractException DependentRefusal(
        DocumentEntry document, CreateMateCommand command, bool? bySampleRead, bool sample1, bool sample2,
        int before, int after) =>
        new(
            ErrorCodes.GeometryFailed,
            $"Зависимое положение создано (Valid=true, сопряжений {before} → {after}), но перечитанные "
            + $"поля — НЕ записанные: BySample={bySampleRead} (записано true), образец1 "
            + $"{(sample1 ? "совпал" : "НЕ совпал")}, образец2 {(sample2 ? "совпал" : "НЕ совпал")}. "
            + "Успехом это не считается.",
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["constraint_type"] = command.ConstraintType,
                ["by_sample"] = command.BySample,
                ["sample_object1_ref"] = command.SampleObject1Ref,
                ["sample_object2_ref"] = command.SampleObject2Ref,
                ["mate_count_before"] = before,
                ["mate_count_after"] = after,
                ["document_id"] = document.Id,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["constraint_type"] = command.ConstraintType,
                    ["valid"] = true,
                    ["by_sample_read"] = bySampleRead,
                    ["sample1_reads_back"] = sample1,
                    ["sample2_reads_back"] = sample2,
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                },
            });

    // ── MATE-03 ──
    /// <summary>Change a mate parameter (distance or angle) or, for a transmission, its ratio and
    /// directions.</summary>
    public SetMateParameterResult SetMateParameter(SetMateParameterCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var mate = Mate7(document, payload.Ordinal);
        if (mate is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Сопряжение по номеру {payload.Ordinal} не получено как IMateConstraint3D: " +
                "параметр этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED BEFORE THE MUTATION: the ordinal from the API5 collection is applied to
        // the API7 indexer, and a mismatch or unreadability would write the parameter into a FOREIGN
        // mate (defect M7).
        var identity = MateIdentityMatches(payload, mate, out var identityNote);
        if (identity != true)
        {
            throw MateIdentityRefusal(document, command.MateRef, payload, identity, identityNote);
        }

        // THE MATE TYPE IS READ FROM THE MODEL, so the field group is decided here and not by the
        // request schema: a parameter written into a transmission would be accepted and ignored.
        // History: docs/decisions/mates.md#transmission-edit
        var isTransmission = payload.ConstraintType == (short)MateConstraintType.mc_Transmission;

        // THE TWO KINDS OF THIS ORDER HAVE NO EDITABLE PARAMETER: their documented fields are Plane and
        // BySample/SampleObject1/2, and the documented edit route (ParamValue) does not name them —
        // writing it there would be accepted and ignored. Named refusal, not silence.
        if (payload.ConstraintType is (short)MateConstraintType.mc_Symmetric
            or (short)MateConstraintType.mc_Dependent)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Сопряжение вида «{MateTypeName(payload.ConstraintType)}» не имеет правимого параметра: "
                + "документированная правка задаёт ParamValue (расстояние или угол) либо коэффициент "
                + "передачи, а у этого вида таких полей нет. Изменение — удалить сопряжение и создать "
                + "заново с другими полями.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["constraint_type"] = MateTypeName(payload.ConstraintType),
                    ["supported_fields"] = new[] { "plane", "by_sample", "sample_object1", "sample_object2" },
                });
        }

        var refusal = MateTransmissionPolicy.CheckEdit(isTransmission, command.ParamValue,
            command.Scale1, command.Scale2, command.Direction1, command.Direction2);
        if (refusal != MateTransmissionPolicy.EditRefusal.None)
        {
            throw EditFieldRefusal(refusal, command);
        }

        if (isTransmission)
        {
            return SetTransmissionParameters(document, command, mate);
        }

        var before = SafeDouble(() => mate.ParamValue);
        try
        {
            mate.ParamValue = command.ParamValue!.Value;
            mate.Update();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись ParamValue прервалась: {ex.Message}. Параметр не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document3D.RebuildDocument();
        var after = SafeDouble(() => mate.ParamValue);
        BumpRevision(document, "mate.set_parameter");

        var matched = before is not null && after is not null
            && Math.Abs(after.Value - command.ParamValue.Value) <= 1e-6;
        var checks = new List<NamedCheck>
        {
            new("param_value_read_back", matched,
                Observed: after?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: command.ParamValue.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
        };

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the re-read parameter did not match the requested
        // one (or was not read), and a result of "done" would be a lie.
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Параметр сопряжения ЗАПИСАН, но не подтверждён: перечитано "
                + (after is null
                    ? "НЕ ЧИТАЕТСЯ"
                    : after.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                + ", запрошено " + command.ParamValue.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + ". Успехом это не считается: модель изменена, параметр не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["param_before"] = before,
                    ["param_after"] = after,
                    ["param_requested"] = command.ParamValue,
                });
        }

        return new SetMateParameterResult(
            ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate"),
            before,
            after,
            // A mismatched parameter was REFUSED above: reaching here means the re-read matched.
            new VerificationDto(VerificationLevel.StructureChecked, checks, Array.Empty<string>()));
    }

    /// <summary>Why the requested field group does not fit the mate, with the field named.</summary>
    private static KompasContractException EditFieldRefusal(
        MateTransmissionPolicy.EditRefusal refusal, SetMateParameterCommand command)
    {
        var message = refusal switch
        {
            MateTransmissionPolicy.EditRefusal.ParamValueOnTransmission =>
                "Сопряжение — механическая передача: поля param_value у неё нет (расстояния и угла у "
                + "передачи не бывает). Коэффициент задаётся парой scale1/scale2, направления — "
                + "direction1/direction2.",
            MateTransmissionPolicy.EditRefusal.TransmissionFieldOnParamEdit =>
                "Поле «" + FirstTransmissionField(command) + "» относится только к механической "
                + "передаче, а это сопряжение другого вида: значение было бы записано и "
                + "проигнорировано.",
            MateTransmissionPolicy.EditRefusal.IncompleteRatio =>
                "Коэффициент передачи задаётся парой целиком: передано только «"
                + (command.Scale1 is null ? "scale2" : "scale1") + "», не хватает «"
                + (command.Scale1 is null ? "scale1" : "scale2") + "».",
            MateTransmissionPolicy.EditRefusal.RatioOutOfRule =>
                "Коэффициент передачи — конечные числа больше нуля: принято "
                + $"scale1={command.Scale1?.ToString("0.####") ?? "нет"}, "
                + $"scale2={command.Scale2?.ToString("0.####") ?? "нет"}.",
            _ => "Ни одного поля правки не передано: нужен param_value либо scale1/scale2 и/или "
                + "direction1/direction2 у передачи.",
        };

        return new KompasContractException(ErrorCodes.InvalidArgument, message, RetryPolicy.Never);
    }

    private static string FirstTransmissionField(SetMateParameterCommand command) =>
        command.Scale1 is not null ? "scale1"
        : command.Scale2 is not null ? "scale2"
        : command.Direction1 is not null ? "direction1"
        : "direction2";

    /// <summary>Write a transmission's ratio and directions into an EXISTING mate and judge it by the
    /// read-back.</summary>
    /// <remarks>DOC: <c>imate3dtransmission_methods.html</c> — <c>SetScale</c> is the only method and
    /// <c>Scale1/2</c> are read-only; <c>imate3dtransmission_direction1/2.html</c> name
    /// <c>put_Direction1/2</c>, so the directions are writable. <c>Update()</c> is not documented for
    /// mechanical mates; the route is the one measured on creation.
    /// History: docs/decisions/mates.md#transmission-edit</remarks>
    private SetMateParameterResult SetTransmissionParameters(
        DocumentEntry document, SetMateParameterCommand command, IMateConstraint3D mate)
    {
        var transmission = mate as IMate3DTransmission
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Сопряжение прочитано как mc_Transmission, но не приводится к IMate3DTransmission: "
                + "коэффициент и направления этим маршрутом не задаются.",
                RetryPolicy.ReacquireContext);

        var notes = new List<string>();
        var before = TransmissionState(mate, transmission);

        try
        {
            // A FALSE IS NOT SWALLOWED: it means the number was not accepted, and the read-back below
            // decides; the note stops `true` from being read as "the ratio is in the model".
            if (command.Scale1 is { } scale1
                && transmission.SetScale(scale1, command.Scale2!.Value) != true)
            {
                notes.Add("set_scale_not_confirmed — SetScale не подтвердил запись коэффициента; "
                    + "коэффициент проверяется перечитыванием из модели");
            }

            if (command.Direction1 is { } direction1)
            {
                transmission.Direction1 = direction1;
            }

            if (command.Direction2 is { } direction2)
            {
                transmission.Direction2 = direction2;
            }

            mate.Update();
        }
        catch (COMException ex)
        {
            // The write may have landed: the revision must show it (same rule as mate.create.partial).
            BumpRevision(document, "mate.set_parameter.partial");
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись параметров передачи прервалась: {ex.Message}. Правка не подтверждена.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document3D.RebuildDocument();
        var after = TransmissionState(mate, transmission);
        BumpRevision(document, "mate.set_parameter");

        var mismatched = MateTransmissionPolicy.EditMismatches(
            after, command.Scale1, command.Scale2, command.Direction1, command.Direction2);

        var checks = new List<NamedCheck>();
        if (command.Scale1 is { } requested1)
        {
            checks.Add(new("transmission_ratio_read_back", !mismatched.Contains("scale"),
                Observed: $"{Number(after.Scale1)}:{Number(after.Scale2)}",
                Expected: $"{Number(requested1)}:{Number(command.Scale2)}"));
        }

        if (command.Direction1 is not null || command.Direction2 is not null)
        {
            checks.Add(new("transmission_directions_read_back",
                !mismatched.Contains("direction1") && !mismatched.Contains("direction2"),
                Observed: $"{Flag(after.Direction1)}/{Flag(after.Direction2)}",
                Expected: $"{Flag(command.Direction1)}/{Flag(command.Direction2)}"));
        }

        // A READ-BACK THAT DIFFERS IS A REFUSAL, not a success with a warning: the mate is in the model
        // and the revision is bumped, so the partial effect is named with both numbers.
        if (mismatched.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Параметры передачи ЗАПИСАНЫ, но не подтверждены перечитыванием: не совпало "
                + string.Join(", ", mismatched) + ". Прочитано "
                + $"{Number(after.Scale1)}:{Number(after.Scale2)}, направления "
                + $"{Flag(after.Direction1)}/{Flag(after.Direction2)}; запрошено "
                + $"{Number(command.Scale1)}:{Number(command.Scale2)}, направления "
                + $"{Flag(command.Direction1)}/{Flag(command.Direction2)}. Успехом это не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["mismatched"] = mismatched,
                    ["scale1_requested"] = command.Scale1,
                    ["scale2_requested"] = command.Scale2,
                    ["scale1_read"] = after.Scale1,
                    ["scale2_read"] = after.Scale2,
                    ["direction1_requested"] = command.Direction1,
                    ["direction2_requested"] = command.Direction2,
                    ["direction1_read"] = after.Direction1,
                    ["direction2_read"] = after.Direction2,
                    ["valid"] = after.Valid,
                });
        }

        return new SetMateParameterResult(
            ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate"),
            null,
            null,
            new VerificationDto(VerificationLevel.StructureChecked, checks, notes),
            before,
            after);
    }

    /// <summary>The transmission's own numbers as the model holds them; <c>null</c> — not read.</summary>
    private static MateTransmissionStateDto TransmissionState(
        IMateConstraint3D mate, IMate3DTransmission transmission) =>
        new()
        {
            Scale1 = SafeDouble(() => transmission.Scale1),
            Scale2 = SafeDouble(() => transmission.Scale2),
            Direction1 = Bool(() => transmission.Direction1),
            Direction2 = Bool(() => transmission.Direction2),
            Valid = Bool(() => mate.Valid),
        };

    private static string Number(double? value) =>
        value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается";

    private static string Flag(bool? value) =>
        value is null ? "не читается" : value.Value ? "true" : "false";


    // ── MATE-04 ──
    /// <summary>Set the component-fixing flag of a mate.</summary>
    public SetMateFixedResult SetMateFixed(SetMateFixedCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        var mate = Mate7(document, payload.Ordinal);
        if (mate is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Сопряжение по номеру {payload.Ordinal} не получено как IMateConstraint3D: " +
                "признак фиксации этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED BEFORE THE MUTATION — by the same rule as the parameter: a fixing flag
        // written into a FOREIGN mate changes the wrong model.
        var identity = MateIdentityMatches(payload, mate, out var identityNote);
        if (identity != true)
        {
            throw MateIdentityRefusal(document, command.MateRef, payload, identity, identityNote);
        }

        var wanted = FixedFromName(command.Fixed);
        // The fixing flag is read EXPLICITLY: an unread value is not replaced by the first enum value
        // (`default(ksMateFixedTypeEnum)` = `ksMFixedUnknown`, i.e. "unfix" where the COM read did not
        // happen).
        var beforeRead = TryRead(() => mate.Fixed);
        try
        {
            mate.Fixed = wanted;
            mate.Update();
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись Fixed прервалась: {ex.Message}. Признак фиксации не подтверждён.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        document.Document3D.RebuildDocument();
        var afterRead = TryRead(() => mate.Fixed);
        BumpRevision(document, "mate.set_fixed");

        // A match is confirmed ONLY by a read value: `afterRead.Ok == false` is "not read", not "matched".
        var matched = afterRead.Ok && afterRead.Value == wanted;
        var before = beforeRead.Ok ? beforeRead.Value : (ksMateFixedTypeEnum?)null;
        var after = afterRead.Ok ? afterRead.Value : (ksMateFixedTypeEnum?)null;

        // A FAILED MANDATORY CHECK IS NOT SUCCESS: the fixing flag was written, but the re-read value
        // did not match the requested one or was not read.
        if (!matched)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Признак фиксации ЗАПИСАН, но не подтверждён: перечитано «"
                + (FixedName(after) ?? "не читается") + "», запрошено «" + command.Fixed + "». Успехом это " +
                "не считается.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["mate_ref"] = command.MateRef,
                    ["fixed_before"] = FixedName(before),
                    ["fixed_after"] = FixedName(after),
                    ["fixed_requested"] = command.Fixed,
                });
        }

        return new SetMateFixedResult(
            ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate"),
            FixedName(before),
            FixedName(after),
            // An unconfirmed fixing flag was REFUSED above: reaching here means the re-read matched.
            new VerificationDto(
                VerificationLevel.StructureChecked,
                new List<NamedCheck>
                {
                    new("fixed_read_back", matched,
                        Observed: FixedName(after) ?? "не читается", Expected: command.Fixed),
                },
                Array.Empty<string>()));
    }

    // ── MATE-05 ──
    /// <summary>Delete a mate.</summary>
    /// <remarks>ROUTE — two documented removals, chosen by the mate's OWN type: an ordinary mate goes
    /// through <c>ksDocument3D.RemoveMateConstraint</c> (<c>ksdocument3d_removemateconstraint.html</c>),
    /// a mechanical TRANSMISSION through <c>ksDocument3D.DeleteObject</c>
    /// (<c>ksdocument3d_deleteobject.html</c>: «Удалить объект: деталь, операцию, сопряжение»), because
    /// <c>RemoveMateConstraint</c> answers False on it and leaves it in the model.
    /// History: docs/decisions/mates.md#delete-route</remarks>
    public DeleteMateResult DeleteMate(DeleteMateCommand command)
    {
        var document = RequireAssembly(command.DocumentId);
        RequireRevision(document, command.ExpectedRevision);

        var payload = RequireMate(document, command.MateRef);
        // THE TYPE COMES FROM THE MODEL, not from the call: the two documented removals are not
        // interchangeable, and the wrong one answers False while the mate stays.
        var isTransmission = payload.ConstraintType == (short)MateConstraintType.mc_Transmission;
        var before = MateCountForDeletion(document, isTransmission, "до удаления");

        bool removed;
        if (isTransmission)
        {
            // THE OBJECT IS THE MATE THE REFERENCE NAMES — the same API5 ksMateConstraint the product
            // already addresses the transmission with; DeleteObject takes an object, not a type pair.
            removed = DeleteObjectOfMate(document, payload);
        }
        else if (payload.ConstraintType is (short)MateConstraintType.mc_Symmetric
                 or (short)MateConstraintType.mc_Dependent)
        {
            // MEASURED: on the two kinds of this order RemoveMateConstraint is not the working route —
            // on a symmetry it threw RPC_E_SERVERFAULT, and on a dependent position GetBaseObj answers
            // null (its base objects are the components), so the pair it needs does not exist. Both go
            // through the documented DeleteObject of the mate object itself, and the count still decides.
            removed = DeleteObjectOfMate(document, payload);
        }
        else
        {
            removed = RemoveByConstraint(document, payload);
        }

        document.Document3D.RebuildDocument();
        var after = MateCountForDeletion(document, isTransmission, "после удаления");

        if (!removed || after >= before)
        {
            // A deletion that KOMPAS confirmed but the re-read count did not means: the model changed
            // while the revision and references do not know about it.
            if (removed)
            {
                BumpRevision(document, "mate.delete.partial", invalidateAll: true);
            }

            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                $"Сопряжение НЕ удалено: {(isTransmission ? "DeleteObject" : "RemoveMateConstraint")}="
                + $"{removed}, сопряжений {before} → {after}.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["route"] = isTransmission ? "delete_object" : "remove_mate_constraint",
                    ["constraint_type"] = MateTypeName(payload.ConstraintType),
                    ["mate_count_before"] = before,
                    ["mate_count_after"] = after,
                });
        }

        // The DTO is built BEFORE references are revoked: `InvalidateDocument` drops ALL references of
        // the document, and a `Require` after it would reject the very reference used for deletion.
        var dto = ToDto(References.Require(command.MateRef, document.Id, document.Revision), "mate");

        References.InvalidateDocument(document.Id);
        BumpRevision(document, "mate.delete");

        return new DeleteMateResult(
            dto,
            before,
            after,
            new VerificationDto(
                VerificationLevel.StructureChecked,
                new List<NamedCheck>
                {
                    new("mate_removed", after < before, Observed: $"{before} → {after}",
                        Expected: $"{before - 1}"),
                },
                new List<string>()));
    }

    /// <summary>Remove a mate by <c>RemoveMateConstraint(constraintType, obj1, obj2)</c>, the objects
    /// taken from the mate itself by the documented <c>GetBaseObj(1|2)</c>.</summary>
    private static bool RemoveByConstraint(DocumentEntry document, MatePayload payload)
    {
        // "The first one found" would be an address substitution, so an unread base object is a refusal.
        var first = Ref(() => payload.Mate5.GetBaseObj(1));
        var second = Ref(() => payload.Mate5.GetBaseObj(2));
        if (first is null || second is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Базовые объекты сопряжения не прочитаны (GetBaseObj вернул null): удалять нечем — " +
                "RemoveMateConstraint требует оба объекта.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            return document.Document3D.RemoveMateConstraint(payload.ConstraintType, first, second);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"RemoveMateConstraint прервался: {ex.Message}. Сопряжение не удалено.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }
    }

    /// <summary>Remove a mate as an object by the documented <c>DeleteObject(obj)</c>.</summary>
    private static bool DeleteObjectOfMate(DocumentEntry document, MatePayload payload)
    {
        if (payload.Mate5 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Объект сопряжения не сохранён в ссылке: DeleteObject удаляет объект, а не пару "
                + "«тип и базовые объекты», — передавать нечего.",
                RetryPolicy.ReacquireContext);
        }

        try
        {
            return document.Document3D.DeleteObject(payload.Mate5);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"DeleteObject прервался: {ex.Message}. Передача не удалена.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }
    }

    /// <summary>The mate count that confirms a deletion, from the counter that follows the removal.</summary>
    /// <remarks>MEASURED: after <c>DeleteObject</c> the API5 collection still answers the PRE-deletion
    /// number on the first read and only then drops, while the API7 count follows the removal at once;
    /// judging by the lagging number would report a deletion that happened as a failure.
    /// History: docs/decisions/mates.md#delete-route</remarks>
    private int MateCountForDeletion(DocumentEntry document, bool viaDeleteObject, string stage) =>
        viaDeleteObject
            ? RequireMateCount(document, RequireMateConstraints(document), stage)
            : MateConstraintCount(document, stage);

    // ── helpers ──

    /// <summary>The assembly's mates as <c>IMateConstraints3D</c> (API7).</summary>
    private IMateConstraints3D RequireMateConstraints(DocumentEntry document)
    {
        var notes = new List<string>();
        var top = TopPart7(document, notes);
        if (top?.MateConstraints is not { } mates)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7.MateConstraints не вернул IMateConstraints3D: коллекция сопряжений недостижима.",
                RetryPolicy.ReacquireContext);
        }

        return mates;
    }

    /// <summary>A mate by ordinal as <c>IMateConstraint3D</c> (API7).</summary>
    private IMateConstraint3D? Mate7(DocumentEntry document, int ordinal)
    {
        try
        {
            return RequireMateConstraints(document).MateConstraint3D[ordinal];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether this is the same mate: the API7 type and base objects are compared against the
    /// API5 collection at the same ordinal.</summary>
    /// <returns><c>true</c> — identity confirmed; <c>false</c> — the ordinal leads to another mate;
    /// <c>null</c> — nothing to compare.</returns>
    /// <remarks>WHY: the API7 mate is taken by the ordinal from the API5 collection; that API7↔API5 order
    /// correspondence is an ASSUMPTION, and if violated the parameter would be written into a FOREIGN mate.
    /// WHAT IS COMPARED: type and base objects by PRESENCE (bitwise comparison is NOT done).
    /// History: docs/decisions/mates.md#mate-identity</remarks>
    private bool? MateIdentityMatches(MatePayload payload, IMateConstraint3D mate7, out string detail)
    {
        var type5 = payload.ConstraintType;
        var type7 = Int(() => (int)mate7.ConstraintType);
        var base1Api5 = Ref(() => payload.Mate5.GetBaseObj(1)) is not null;
        var base2Api5 = Ref(() => payload.Mate5.GetBaseObj(2)) is not null;
        var base1Api7 = Ref(() => mate7.BaseObject1) is not null;
        var base2Api7 = Ref(() => mate7.BaseObject2) is not null;

        var signals = new List<string>();
        var confirmed = false;
        var contradicted = false;

        if (type7 is not null)
        {
            var equal = (short)type7.Value == type5;
            signals.Add(equal
                ? $"тип сопряжения совпал («{MateTypeName(type7.Value)}»)"
                : $"тип сопряжения РАСХОДИТСЯ: API5 «{MateTypeName(type5)}» ({(int)type5}), API7 " +
                  $"«{MateTypeName(type7.Value)}» ({type7.Value})");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("тип сопряжения со стороны API7 не прочитан");
        }

        // Base objects: the signal is informative only if at least one object is present somewhere —
        // "both mates without base objects" is an absence of information, not a confirmation.
        if (base1Api5 || base2Api5 || base1Api7 || base2Api7)
        {
            var equal = base1Api5 == base1Api7 && base2Api5 == base2Api7;
            signals.Add(equal
                ? $"базовые объекты совпали по наличию (1: {base1Api5}, 2: {base2Api5})"
                : $"базовые объекты РАСХОДЯТСЯ: API5 (1: {base1Api5}, 2: {base2Api5}), " +
                  $"API7 (1: {base1Api7}, 2: {base2Api7})");
            confirmed |= equal;
            contradicted |= !equal;
        }
        else
        {
            signals.Add("базовые объекты не читаются ни с одной стороны — признак не даёт информации");
        }

        detail = string.Join("; ", signals);
        if (contradicted)
        {
            return false;
        }

        return confirmed ? true : null;
    }

    /// <summary>Refusal on an unconfirmed mate identity: a mismatch and "nothing to compare" forbid the
    /// mutation equally. Shared point for <c>set_mate_parameter</c> and <c>set_mate_fixed</c>.</summary>
    private KompasContractException MateIdentityRefusal(
        DocumentEntry document, string mateRef, MatePayload payload, bool? identity, string detail) =>
        new(
            ErrorCodes.StaleReference,
            identity == false
                ? $"Сопряжение по номеру {payload.Ordinal} — НЕ то, на которое указывает ссылка: {detail}. "
                  + "Параметр был бы записан в ЧУЖОЕ сопряжение, поэтому мутация не выполняется. "
                  + "Перечитайте сопряжения kompas_list_mates и возьмите свежую ссылку."
                : $"Тождество сопряжения по номеру {payload.Ordinal} НЕ подтверждено: {detail}. "
                  + "Мутация по неподтверждённому адресу не выполняется — перечитайте сопряжения "
                  + "kompas_list_mates и возьмите свежую ссылку.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["mate_ref"] = mateRef,
                ["ordinal"] = payload.Ordinal,
                ["identity_check"] = detail,
                ["identity_confirmed"] = identity is null ? "не сверено" : "расхождение",
                ["document_id"] = document.Id,
            });

    /// <summary>
    /// The mate count from the documented API5 collection
    /// <c>ksDocument3D.MateConstraintCollection</c>. An unread count is a refusal, not zero and not the
    /// previous value.
    /// </summary>
    private static int MateConstraintCount(DocumentEntry document, string stage)
    {
        var count = SafeInt(() => (document.Document3D.MateConstraintCollection() as ksMateConstraintCollection)!.GetCount());
        if (count is not null)
        {
            return count.Value;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Число сопряжений {stage} не прочитано (ksMateConstraintCollection.GetCount): подтвердить " +
            "удаление нечем.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    /// <summary>The mate count, READ. An unread count is a refusal, not zero: creation or deletion cannot be
    /// confirmed "by an unread counter".</summary>
    private static int RequireMateCount(DocumentEntry document, IMateConstraints3D mates, string stage)
    {
        var count = SafeInt(() => mates.Count);
        if (count is not null)
        {
            return count.Value;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Число сопряжений {stage} не прочитано (IMateConstraints3D.Count): подтвердить операцию " +
            "нечем. Подменять непрочитанное число нулём или прежним значением здесь запрещено.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    /// <summary>A mate row signature for comparing two collection snapshots: only what the creating call
    /// fixes. Validity is carried beside it, never inside (see MateDifference).</summary>
    private static string MateSignature(MateRowDto row) => string.Join('|',
        row.ConstraintType ?? "?",
        row.Fixed ?? "?",
        row.ParamValue?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.Alignment ?? "?",
        row.Direction?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
        row.BaseObject1 ?? "?",
        row.BaseObject2 ?? "?");

    /// <summary>Pick the mate that was absent from the <paramref name="before"/> snapshot.</summary>
    private static MateRowDto? FindNewMate(List<MateRowDto> before, List<MateRowDto> after, out string note)
    {
        static MateSnapshotRow Snapshot(MateRowDto row) => new(MateSignature(row), row.Valid);

        var found = MateDifference.Find(before.Select(Snapshot).ToList(), after.Select(Snapshot).ToList());
        var invalidated = found.NewlyInvalid > 0
            ? $"; прежних сопряжений стало недействительными: {found.NewlyInvalid} (новое им противоречит)"
            : string.Empty;

        if (found.AddedIndex is int index)
        {
            var candidate = after[index];
            note = $"новое сопряжение выделено разностью множеств: номер {candidate.Ordinal}{invalidated}";
            return candidate;
        }

        note = found.AddedCount == 0
            ? "новое сопряжение НЕ выделено: перечитанная коллекция не отличается от снимка до создания"
            : $"новое сопряжение НЕ выделено однозначно: разность дала {found.AddedCount} строк{invalidated}";
        return null;
    }

    private ksMateConstraint? MateAt(DocumentEntry document, int ordinal)
    {
        try
        {
            return document.Document3D.MateConstraintCollection() is ksMateConstraintCollection mates
                && ordinal >= 0 && ordinal < mates.GetCount()
                && mates.GetByIndex(ordinal) is ksMateConstraint mate
                    ? mate
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private MatePayload RequireMate(DocumentEntry document, string mateRef)
    {
        var stored = References.Require(mateRef, document.Id, document.Revision);
        if (stored.Payload is not MatePayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{mateRef}» не адресует сопряжение: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        return payload;
    }

    /// <summary>Read all assembly mates via the documented <c>MateConstraintCollection</c>.</summary>
    private List<MateRowDto> ReadMates(DocumentEntry document, List<string> notes)
    {
        var rows = new List<MateRowDto>();
        ksMateConstraintCollection? mates;
        try
        {
            mates = document.Document3D.MateConstraintCollection() as ksMateConstraintCollection;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("mate_collection_unavailable — MateConstraintCollection не получена");
            return rows;
        }

        if (mates is null)
        {
            notes.Add("mate_collection_unavailable — MateConstraintCollection не ksMateConstraintCollection");
            return rows;
        }

        // AN UNREAD COUNT IS A READ FAILURE, NOT "NO MATES": `?? 0` would give an empty list, making
        // "no mates" indistinguishable from "the collection did not answer".
        var count = SafeInt(() => mates.GetCount());
        if (count is null)
        {
            notes.Add("mate_count_unread — число сопряжений не прочитано: перечень пуст, но это НЕ " +
                      "означает «сопряжений нет»");
            return rows;
        }

        for (var index = 0; index < count.Value; index++)
        {
            var mate = Ref(() => mates.GetByIndex(index)) as ksMateConstraint;
            if (mate is null)
            {
                notes.Add($"mate_{index}_not_ksMateConstraint");
                continue;
            }

            var typeValue = SafeInt(() => mate.constraintType) ?? -1;
            var stored = References.Register(
                MateRefKind, document.Id, document.Revision,
                new MatePayload(mate, index, (short)typeValue));

            // THE API7↔API5 ORDER CORRESPONDENCE IS NOT ASSUMED ON READ EITHER: `Valid`, `Alignment` and
            // `Name` are taken from the API7 object at the same ordinal, and on a type mismatch they
            // describe a FOREIGN mate — the row says so. The read is not a refusal: a mutation via this
            // reference will be rejected by the check in SetMateParameter/SetMateFixed.
            var mate7 = Mate7(document, index);
            var type7 = mate7 is null ? null : Int(() => (int)mate7.ConstraintType);
            if (typeValue >= 0 && type7 is not null && (short)type7.Value != (short)typeValue)
            {
                notes.Add($"mate_{index}_identity_mismatch — тип API7 «{MateTypeName(type7.Value)}» не " +
                          $"совпал с типом API5 «{MateTypeName(typeValue)}»: Valid/Alignment/Name " +
                          "прочитаны у объекта API7 по тому же номеру, и их принадлежность этой строке " +
                          "НЕ подтверждена");
            }

            var transmission7 = typeValue == (short)MateConstraintType.mc_Transmission
                ? mate7 as IMate3DTransmission
                : null;

            // THE TWO EXTRA KINDS READ THEIR OWN FIELDS by QueryInterface of the same API7 object: a row
            // that showed them empty while the model holds them would be a read that disagrees with the
            // write (DESIGN: the same fields the creating call wrote).
            var symmetry7 = typeValue == (short)MateConstraintType.mc_Symmetric
                ? mate7 as IMate3DSymmetry
                : null;
            var dependent7 = typeValue == (short)MateConstraintType.mc_Dependent
                ? mate7 as IMate3DDependentPosition
                : null;
            var planeRef = symmetry7 is null ? null : PlaneReference(document, symmetry7, index, notes);
            var (bySampleRead, sampleRef1, sampleRef2) = dependent7 is null
                ? ((bool?)null, (string?)null, (string?)null)
                : DependentFields(document, dependent7, notes);

            rows.Add(new MateRowDto
            {
                MateRef = stored.Id,
                Ordinal = index,
                ConstraintType = MateTypeName(typeValue),
                // `fixed` is a C# keyword, so the property is taken by its escaped name `@fixed`.
                // DOC: ksmateconstraint_fixed.html — «fixed — признак фиксации».
                Fixed = FixedName(SafeInt(() => mate.@fixed)),
                ParamValue = SafeDouble(() => mate.distance),
                Direction = SafeInt(() => mate.direction),
                Alignment = AlignmentName(EnumOrNull(() => mate7?.Alignment)),
                Valid = Bool(() => mate7?.Valid),
                BaseObject1 = ObjectTypeName(Ref(() => mate.GetBaseObj(1))),
                BaseObject2 = ObjectTypeName(Ref(() => mate.GetBaseObj(2))),
                Motion1 = MotionName(EnumOrNull(() => transmission7?.MotionType1)),
                Motion2 = MotionName(EnumOrNull(() => transmission7?.MotionType2)),
                Scale1 = transmission7 is null ? null : SafeDouble(() => transmission7.Scale1),
                Scale2 = transmission7 is null ? null : SafeDouble(() => transmission7.Scale2),
                Direction1 = Bool(() => transmission7?.Direction1),
                Direction2 = Bool(() => transmission7?.Direction2),
                SymmetryPlaneRef = planeRef,
                BySample = bySampleRead,
                SampleObject1Ref = sampleRef1,
                SampleObject2Ref = sampleRef2,
                Name = Ref(() => mate7?.Name),
            });
        }

        return rows;
    }

    /// <summary>The mirror plane of a symmetry row as a <c>plane:</c> reference, read through
    /// <c>IMate3DSymmetry.Plane</c> (an <c>IModelObject</c>).</summary>
    /// <remarks>A plane that does not cast to <c>IPlane3D</c> has no plane address to mint, and that is
    /// NAMED rather than left silently empty. History: docs/decisions/assembly-ext.md#mate-types</remarks>
    private string? PlaneReference(
        DocumentEntry document, IMate3DSymmetry symmetry, int ordinal, List<string> notes)
    {
        var raw = Ref(() => symmetry.Plane);
        if (raw is IPlane3D plane)
        {
            return References.Register(
                SymmetryPlaneRefKind, document.Id, document.Revision, plane).Id;
        }

        if (raw is not null)
        {
            notes.Add($"mate_{ordinal}_plane_not_iplane3d — Plane прочитана, но не IPlane3D: адресовать её "
                      + "ссылкой на плоскость нечем");
        }

        return null;
    }

    /// <summary><c>BySample</c> and both samples of a dependent row, the samples handed back as
    /// <c>component:</c> references of the address rule the product already uses.</summary>
    private (bool? BySample, string? Sample1, string? Sample2) DependentFields(
        DocumentEntry document, IMate3DDependentPosition dependent, List<string> notes)
    {
        var top = TopPart7(document, notes);
        return (Bool(() => dependent.BySample),
            SampleReference(document, top, Ref(() => dependent.SampleObject1) as IPart7, notes),
            SampleReference(document, top, Ref(() => dependent.SampleObject2) as IPart7, notes));
    }

    /// <summary>A sample object as a <c>component:</c> reference: the ordinal is FOUND by the product's
    /// own rule (<c>ComponentAddress</c>), never counted, and an unconfirmed address is named by that
    /// rule instead of being replaced by a neighbour's number.</summary>
    private string? SampleReference(
        DocumentEntry document, IPart7? top, IPart7? sample, List<string> notes)
    {
        if (sample is null)
        {
            return null;
        }

        var ordinal = top is null
            ? UnaddressedComponentOrdinal
            : AddressOf(document, sample, SummMatrix7(top, sample), null, notes);
        return References.Register(
            ComponentRefKind, document.Id, document.Revision,
            new ComponentPayload(sample, top, Int(() => sample.Reference), ordinal)).Id;
    }


    /// <summary>
    /// A component face as <c>IModelObject</c>: the documented
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>, then transfer to API7.
    /// </summary>
    /// <remarks>Also returns the face's normal IN THE PART FRAME and the component's API7 view, which is
    /// what measuring the solved orientation needs. MEASURED: the normal read here is in the PART frame
    /// even for a rotated component, so the assembly normal is the component's rotation applied to it.
    /// History: docs/decisions/mates.md#alignment-orientation</remarks>
    private FaceHandle FaceObject7(
        DocumentEntry document, string componentRef, int faceIndex, List<string> notes)
    {
        var stored = References.Require(componentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{componentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part5 = ComponentPart5At(document, payload.Ordinal);
        if (part5 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Компонент по порядковому номеру {payload.Ordinal} не получен как ksPart: грань " +
                "адресовать нечем.",
                RetryPolicy.ReacquireContext);
        }

        // IDENTITY IS CHECKED HERE TOO, and it also refuses on "not compared": the face comes from the
        // component found by ordinal, so a foreign ordinal would mate FOREIGN faces. The decision is the
        // same PURE ComponentIdentity — the SOURCE decides; name and matrix are notes (see IdentityMatches).
        var identity = IdentityMatches(part5, payload, out var identityNote);
        notes.Add("component_identity — " + identityNote);
        if (identity != true)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                "Грань адресована по номеру, тождество которого НЕ подтверждено: " + identityNote +
                ". Сопряжение по неподтверждённому адресу не создаётся; перечитайте структуру " +
                "kompas_list_components и возьмите свежую ссылку.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["ordinal"] = payload.Ordinal,
                    ["identity_confirmed"] = identity is null ? "не сверено (источник не прочитан)" : "расхождение источника",
                });
        }

        ksFaceCollection? faces;
        try
        {
            faces = part5.BodyCollection() is ksBodyCollection bodies && bodies.GetCount() > 0
                && bodies.GetByIndex(0) is ksBody body
                    ? body.FaceCollection() as ksFaceCollection
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            faces = null;
        }

        if (faces is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"У компонента {payload.Ordinal} не прочитано тело или грани " +
                "(ksPart.BodyCollection → ksBody.FaceCollection): грань адресовать нечем. Это тот же " +
                "класс, что «пустой компонент» на маршруте CreatePartInAssembly.",
                RetryPolicy.ReacquireContext);
        }

        var count = SafeInt(() => faces.GetCount()) ?? 0;
        if (faceIndex < 0 || faceIndex >= count)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Номер грани {faceIndex} вне диапазона: у компонента {count} грань(ей).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["component_ref"] = componentRef,
                    ["face_index"] = faceIndex,
                    ["face_count"] = count,
                });
        }

        var face = faces.GetByIndex(faceIndex);
        var bridge = BridgeFor(document);
        if (bridge.TransferTo7(face) is not IModelObject model)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Грань {faceIndex} компонента не переносится в API7 как IModelObject: " +
                "сопряжение этим маршрутом не задаётся.",
                RetryPolicy.ReacquireContext);
        }

        // A CURVED FACE HAS NO SINGLE NORMAL: the read stays null and the orientation of a mate on it
        // is named unverified rather than replaced by a guess.
        var definition = AsInterface<ksFaceDefinition>(face);
        var planar = definition is not null && definition.IsPlanar();
        var localNormal = planar ? SurfaceNormalAtMiddle(definition!) : null;
        var localPoint = planar ? ReadPlanarFacePoint(definition!) : null;

        notes.Add($"грань компонента {payload.Ordinal}[{faceIndex}] перенесена в API7");
        return new FaceHandle(model, payload.Part7, payload.Parent7, localNormal, localPoint);
    }

    /// <summary>The face plane's placement origin in PART coordinates, read by the documented
    /// <c>ksSurface.GetSurfaceParam → ksPlaneParam.GetPlacement → GetOrigin</c> route.</summary>
    /// <remarks>This is a POINT OF THE PLANE, not the face's centre: it is what comparing a face with a
    /// mirrored plane needs, and it is the same object the reference route publishes for a plane.
    /// DOC: <c>kssurface_getsurfaceparam.html</c>, <c>ksplaneparam_getplacement.html</c>,
    /// <c>ksplacement_getorigin.html</c>. History: docs/decisions/assembly-ext.md#mate-types</remarks>
    private static double[]? ReadPlanarFacePoint(ksFaceDefinition face)
    {
        try
        {
            return face.GetSurface() is ksSurface surface && surface.IsPlane()
                && surface.GetSurfaceParam() is ksPlaneParam plane
                && plane.GetPlacement() is ksPlacement placement
                && placement.GetOrigin(out var x, out var y, out var z)
                    ? [x, y, z]
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The dot product of the two mated faces' normals in ASSEMBLY coordinates.</summary>
    /// <remarks>Arithmetic over two documented reads — the face normal in the part frame and the
    /// component's <c>IPart7.GetSummMatrix</c> — not a new API route. The matrix layout is the measured
    /// <c>[X,0][Y,0][Z,0][translation,1]</c>. <c>null</c> means NOT READ (no single normal on a curved
    /// face, or no matrix), never zero.
    /// History: docs/decisions/mates.md#alignment-orientation</remarks>
    private double? FaceNormalsDot(FaceHandle first, FaceHandle second)
    {
        var n1 = AssemblyNormal(first);
        var n2 = AssemblyNormal(second);
        if (n1 is null || n2 is null)
        {
            return null;
        }

        var dot = n1[0] * n2[0] + n1[1] * n2[1] + n1[2] * n2[2];
        var lengths = Math.Sqrt(n1[0] * n1[0] + n1[1] * n1[1] + n1[2] * n1[2])
            * Math.Sqrt(n2[0] * n2[0] + n2[1] * n2[1] + n2[2] * n2[2]);
        return lengths <= 1e-9 ? null : dot / lengths;
    }

    /// <summary>A face's normal in assembly coordinates: the component's rotation applied to the part-frame
    /// normal, both read fresh so the value follows the CURRENT placement.</summary>
    private static double[]? AssemblyNormal(FaceHandle face)
    {
        if (face.LocalNormal is not { Length: 3 } local || face.Parent7 is null)
        {
            return null;
        }

        if (SummMatrix7(face.Parent7, face.Component7) is not { Length: >= 16 } matrix)
        {
            return null;
        }

        var result = new double[3];
        for (var row = 0; row < 3; row++)
        {
            result[row] = matrix[row] * local[0] + matrix[4 + row] * local[1] + matrix[8 + row] * local[2];
        }

        return result;
    }

    private static MateConstraintType MateTypeFromName(string name) => name switch
    {
        "coincidence" => MateConstraintType.mc_Coincidence,
        "parallel" => MateConstraintType.mc_Parallel,
        "perpendicular" => MateConstraintType.mc_Perpendicular,
        "tangency" => MateConstraintType.mc_Tangency,
        "concentric" => MateConstraintType.mc_Concentric,
        "distance" => MateConstraintType.mc_Distance,
        "angle" => MateConstraintType.mc_Angle,
        "transmission" => MateConstraintType.mc_Transmission,
        // DOC: mateconstrainttype.html — mc_Symmetric (11), «Симметрия»; mc_Dependent (14),
        // «Зависимое положение». Both are measured live and implemented by their own interfaces.
        // History: docs/decisions/assembly-ext.md#mate-types
        "symmetric" => MateConstraintType.mc_Symmetric,
        "dependent" => MateConstraintType.mc_Dependent,
        // NAMED REFUSAL, NOT A SILENT ABSENCE: the cam gear is documented and its mate is added, but
        // the object set that makes it valid is not established, so it is out of this volume.
        // History: docs/decisions/mates.md#mechanical-transmission
        "cam_gear" => throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            "Вид cam_gear вне контракта: сопряжение добавляется, но набор объектов, при котором оно "
            + "действительно (Valid=TRUE), не установлен. Поддерживается только transmission.",
            RetryPolicy.Never),
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный тип сопряжения «{name}». Допустимы: coincidence, parallel, perpendicular, " +
            "tangency, concentric, distance, angle, transmission, symmetric, dependent.",
            RetryPolicy.Never),
    };

    /// <summary>The transmission inputs this volume implements, checked before <c>Add</c> creates a mate.</summary>
    private static void RequireTransmissionInputs(CreateMateCommand command)
    {
        if (!MateTransmissionPolicy.IsKnownMotion(command.Motion1)
            || !MateTransmissionPolicy.IsKnownMotion(command.Motion2))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для transmission обязательны motion1 и motion2 = rotation. Линейное движение вне "
                + "объёма: его объект — ребро траектории, а адрес сопряжения задаёт грань. "
                + $"Принято motion1=«{command.Motion1 ?? "нет"}», motion2=«{command.Motion2 ?? "нет"}».",
                RetryPolicy.Never);
        }

        if (!MateTransmissionPolicy.IsUsableScale(command.Scale1)
            || !MateTransmissionPolicy.IsUsableScale(command.Scale2))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для transmission обязательны scale1 и scale2: конечные числа больше нуля. Принято "
                + $"scale1={command.Scale1?.ToString("0.####") ?? "нет"}, "
                + $"scale2={command.Scale2?.ToString("0.####") ?? "нет"}.",
                RetryPolicy.Never);
        }
    }

    /// <summary>Write the transmission's own properties. DOC: <c>imate3dtransmission_propers.html</c>
    /// (properties), <c>imate3dtransmission_setscale.html</c> (the ONLY documented method).</summary>
    /// <remarks>MEASURED: the mate takes the CYLINDRICAL FACE, not a constructive axis built on it —
    /// the same mate with axes in <c>BaseObject1/2</c> and <c>RotationAxis1/2</c> reads back
    /// <c>Valid=false</c>. MEASURED: <c>Direction1/2</c> read back TRUE when not written.
    /// History: docs/decisions/mates.md#mechanical-transmission</remarks>
    private static void ApplyTransmission(
        IMate3DTransmission transmission, CreateMateCommand command,
        IModelObject axis1, IModelObject axis2, List<string> notes)
    {
        transmission.MotionType1 = ksMateMotionTypeEnum.ksMMotionRotation;
        transmission.MotionType2 = ksMateMotionTypeEnum.ksMMotionRotation;
        transmission.RotationAxis1 = axis1;
        transmission.RotationAxis2 = axis2;

        // A FALSE HERE IS NOT SWALLOWED: the ratio is judged by the read-back, and the note says that
        // the writer did not confirm the write, so nobody reads `true` as "the ratio is in the model".
        if (transmission.SetScale(command.Scale1!.Value, command.Scale2!.Value) != true)
        {
            notes.Add("set_scale_not_confirmed — SetScale не подтвердил запись коэффициента; "
                + "коэффициент проверяется перечитыванием из модели");
        }

        if (command.Direction1 is { } direction1)
        {
            transmission.Direction1 = direction1;
        }

        if (command.Direction2 is { } direction2)
        {
            transmission.Direction2 = direction2;
        }
    }

    /// <summary>Motion by the documented <c>ksMateMotionTypeEnum</c>; an unknown number is named by
    /// number, not replaced by the nearest known one.</summary>
    private static string? MotionName(ksMateMotionTypeEnum? value) => value switch
    {
        null => null,
        ksMateMotionTypeEnum.ksMMotionLinear => "linear",
        ksMateMotionTypeEnum.ksMMotionRotation => "rotation",
        _ => $"unknown_{(int)value.Value}",
    };

    /// <summary>Mate type name by the documented <c>MateConstraintType</c>; an unknown value is named by number,
    /// not replaced by the nearest known one.</summary>
    private static string MateTypeName(int value) => value switch
    {
        -1 => "unknown",
        0 => "coincidence",
        1 => "parallel",
        2 => "perpendicular",
        3 => "tangency",
        4 => "concentric",
        5 => "distance",
        6 => "angle",
        7 => "in_place",
        9 => "transmission",
        10 => "cam_gear",
        11 => "symmetric",
        14 => "dependent",
        _ => $"unknown_{value}",
    };

    /// <summary>Fixing flag by name. DOC: <c>ksmatefixedtypeenum.html</c> — «ksMFixedUnknown = 0, Неопределено;
    /// ksMFixedPart1 = 1; ksMFixedPart2 = 2»; DOC: <c>mateconstraintfixed.html</c> — «0 нет фиксации,
    /// 1 фиксировать деталь 1, 2 фиксировать деталь 2». The sources diverge: value 0 is «Неопределено» in
    /// the API5 enum but "no fixing" in the API7 constants; the public name <c>none</c> follows the SECOND.</summary>
    private static ksMateFixedTypeEnum FixedFromName(string name) => name switch
    {
        "none" => ksMateFixedTypeEnum.ksMFixedUnknown,
        "first" => ksMateFixedTypeEnum.ksMFixedPart1,
        "second" => ksMateFixedTypeEnum.ksMFixedPart2,
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный признак фиксации «{name}». Допустимы: none, first, second.",
            RetryPolicy.Never),
    };

    private static string? FixedName(ksMateFixedTypeEnum? value) => value switch
    {
        ksMateFixedTypeEnum.ksMFixedUnknown => "none",
        ksMateFixedTypeEnum.ksMFixedPart1 => "first",
        ksMateFixedTypeEnum.ksMFixedPart2 => "second",
        null => null,
        _ => $"unknown_{(int)value.Value}",
    };

    /// <summary>
    /// Name by the RAW numeric value (field <c>ksMateConstraint.fixed</c>). Same numbering as
    /// <see cref="FixedFromName"/>: 0 is "no fixing" per <c>mateconstraintfixed.html</c>.
    /// </summary>
    private static string? FixedName(int? value) => value switch
    {
        null => null,
        0 => "none",
        1 => "first",
        2 => "second",
        _ => $"unknown_{value}",
    };

    private static ksMateConstraintAlignmentEnum? AlignmentFromName(string? name) => name switch
    {
        null => null,
        "opposite" => ksMateConstraintAlignmentEnum.ksMCAlignmentOpposite,
        "cooriented" => ksMateConstraintAlignmentEnum.ksMCAlignmentCooriented,
        "closest" => ksMateConstraintAlignmentEnum.ksMCAlignmentClosest,
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Неизвестный вариант выравнивания «{name}». Допустимы: opposite, cooriented, closest.",
            RetryPolicy.Never),
    };

    private static string? AlignmentName(ksMateConstraintAlignmentEnum? value) => value switch
    {
        null => null,
        ksMateConstraintAlignmentEnum.ksMCAlignmentOpposite => "opposite",
        ksMateConstraintAlignmentEnum.ksMCAlignmentCooriented => "cooriented",
        ksMateConstraintAlignmentEnum.ksMCAlignmentClosest => "closest",
        ksMateConstraintAlignmentEnum.ksMCAlignmentUnknown => "unknown",
        _ => $"enum_{(int)value.Value}",
    };

    /// <summary>Base-object type name — as KOMPAS itself calls it, no guessing.</summary>
    private static string? ObjectTypeName(object? value)
        => value is null ? null : value.GetType().Name;
}
