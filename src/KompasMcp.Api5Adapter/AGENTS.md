# Api5Adapter (KompasMcp.Api5Adapter)

Adapter that drives KOMPAS-3D v24 through the API5 COM interfaces and bridges selected objects into
API7. One `Api5Session` partial class, one file per domain.

## Sections

### assembly (`Api5Session.Assembly.cs`)

- INVARIANT: a component address is the ORDINAL in the flat `ksDocument3D.PartCollection(true)`, not
  `IPart7.Reference` (MEASURED: 1073741857).
- INVARIANT: identity is decided by the PURE `ComponentIdentity.Decide` (Domain); only the SOURCE FILE
  decides the refusal - name and placement matrix are notes.
- INVARIANT: the placement matrix is NOT stored in the reference; it is read FRESH from the live parent.
- INVARIANT: a failed mandatory check is not success - placement/fixing must be re-read and match.
- LIMIT: a nested component has no API5 address; its placement/replacement is unsupported.
- DOC: `iparts7_addfromfile.html`, `ksdocument3d_partcollection.html`, `kspart_setfilename.html`,
  `ipart7_getsummmatrix.html`.

### mates (`Api5Session.Mate.cs`)

- ROUTE: documented API7 `IPart7.MateConstraints` → `IMateConstraints3D.Add` → `BaseObject1/2` →
  `Update()`; confirmation is `Valid`, not `Update()=true`.
- INVARIANT: the API7↔API5 ordinal correspondence is an ASSUMPTION; identity is checked before a
  mutation (`MateIdentityMatches`), and a mismatch or "nothing to compare" both refuse.
- LIMIT: `ksDocument3D.AddMateConstraint` is NOT used (returned False under every documented
  combination; cause not established, closed by customer decision 05.10.2026).
- DOC: `ksdocument3d_addmateconstraint.html`, `ksmateconstraint_fixed.html`,
  `ksdocument3d_removemateconstraint.html`.

### solid ops (`Api5Session.SolidOps.cs`, `SolidRead.cs`, `Features.cs`, `FeatureEdit.B5.cs`, `FeatureRead.B5.cs`)

- ROUTE: body operations go through the documented API7 bridge; `Create()/Update() = true` means
  "accepted", never "applied" - the geometry is re-read (volume, body count, topology).
- INVARIANT: the document comes from the TARGET-BODY reference, and the named `document_id` is checked
  against it; a foreign reference is rejected, not "reduced" to the named document.
- INVARIANT: an unread value is `null`, never zero or the previous value (fix M6, review 05.10.2026).
- LIMIT: feature type numbers are measured, not guessed - a hole feature lives at `583`, not `52`.

### sketch (`Api5Session.SketchEntities.cs`, `SketchPlane.cs`, `SketchStatus.cs`, `AuxGeometry.cs`)

- INVARIANT: entering a sketch to read it is read-only (`BeginEditEx(true)`), so the revision is
  returned but NOT bumped - declaring a read as a change would invalidate the caller's references.
- INVARIANT: a plane is built by three model points so that `(P2−P1)×(P3−P1)` equals the REQUESTED
  normal, not a random rotation of it in the plane.
- DOC: `ksdocument3d_partcollection.html`, `kspart_bodycollection.html`, `ipart7_getsummmatrix.html`.

## History

- `docs/decisions/adapter-core.md`
- `docs/decisions/adapter-solid.md`
- `docs/decisions/adapter-features.md`
- `docs/decisions/adapter-sketch.md`
- `docs/decisions/adapter-api7.md`
- `docs/decisions/assembly.md`
- `docs/decisions/mates.md`
