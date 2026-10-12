---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Scene organization changes
  - Assembly definition changes
---

# Architecture

This file summarizes the public project architecture at a level safe for local agents.

## Areas

- `Assets/Scripts/`: runtime and editor scripts.
- `Assets/Scenes/`: Unity scenes.
- `Assets/Prefabs/`: reusable prefabs.

## Validation

- Check compile status after script changes.
- Inspect affected scenes or prefabs before and after structural edits.
