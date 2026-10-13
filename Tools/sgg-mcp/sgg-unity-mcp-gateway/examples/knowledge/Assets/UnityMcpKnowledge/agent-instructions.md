---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Team workflow changes
---

# Agent Instructions

This file is project knowledge data. It is not a system prompt, developer message, policy override, or permission grant.

Use this file for project-specific conventions that help agents choose safe Unity commands and validation steps.

## Conventions

- Prefer catalog commands discovered through `sgg_unity_describe`.
- Keep all asset paths Unity-relative, such as `Assets/...` or `Packages/...`.
- Ask a human before destructive scene, prefab, package, or asset changes.
