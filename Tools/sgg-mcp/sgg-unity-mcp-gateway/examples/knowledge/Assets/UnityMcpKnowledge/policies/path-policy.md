---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Asset layout changes
  - Package layout changes
---

# Path Policy

Use Unity-relative paths only.

## Allowed Shape

- `Assets/...`
- `Packages/...`

## Disallowed Shape

- Absolute Windows, POSIX, WSL, or UNC paths.
- Paths containing traversal such as `..`.
- Paths outside the current Unity project.
