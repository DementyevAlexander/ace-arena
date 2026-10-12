---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Prefab hierarchy changes
  - Component schema changes
---

# Inspect Prefab

## Steps

- Use prefab hierarchy or asset inspection commands when available.
- Bound depth, node count, and output size.
- Inspect specific components before proposing edits.

## Notes

- Do not parse raw Unity YAML before semantic commands unless no semantic command is available.
