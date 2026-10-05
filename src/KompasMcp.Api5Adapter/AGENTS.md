# Api5Adapter (KompasMcp.Api5Adapter)

Adapter that drives KOMPAS-3D v24 through the API5 COM interfaces and bridges selected objects into
API7. One `Api5Session` partial class, one file per domain.

## Sections

### assembly (`Api5Session.Assembly.cs`)

- INVARIANT: a component address is the ORDINAL in the flat `ksDocument3D.PartCollection(true)`, not
  `IPart7.Reference` (MEASURED: 1073741857).
- INVARIANT: identity is decided by the PURE `ComponentIdentity.Decide` (Domain); only the SOURCE FILE
  decides the refusal — name and placement matrix are notes.
- INVARIANT: the placement matrix is NOT stored in the reference; it is read FRESH from the live parent.
- INVARIANT: a failed mandatory check is not success — placement/fixing must be re-read and match.
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

### solid ops

TODO — section pending the module cleanup.

### sketch

TODO — section pending the module cleanup.

## History

- `docs/decisions/assembly.md`
- `docs/decisions/mates.md`
