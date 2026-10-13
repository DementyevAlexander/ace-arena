---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Registry configuration changes
  - Package source changes
---

# Package Policy

Package operations may affect project integrity and should follow explicit policy.

## Rules

- Do not install from arbitrary URLs unless the relevant command is discoverable, feature-enabled, and policy allows it.
- Prefer package paths relative to `Packages/manifest.json` when using local package commands.
- Validate compile status after package changes.
