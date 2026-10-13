---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Build pipeline changes
  - Test coverage changes
---

# Validation Checklist

Use the smallest applicable validation set for the change.

## Checks

- Compile status is clean or known failures are documented.
- Changed scenes or prefabs were reinspected.
- Relevant tests or manual checks were run when available.
- Output remains bounded and free of secrets.
