"""Report file names shared by the instrument that writes a run and the one that reads it.

INVARIANT: a report name is a constant here, not a literal in the reader: a copied literal kept
looking for the previous name while the run wrote another, and the reader called a partial set
complete. History: docs/decisions/releases.md#full-run-report-name
"""

# The full run (`scripts/mcp-smoke.py --all`, `ALL_GROUP`) writes this file; the delivery passport
# (`scripts/make-delivery-passport.py`) reads it and refuses `fully_ready` without it.
FULL_RUN_REPORT = "smoke-all-report.json"
