---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Scene ownership changes
  - Runtime initialization changes
---

# Scene Edit Policy

Scene edits should be small, reviewed, and validated.

## Before Editing

- Inspect hierarchy and affected objects with bounded semantic commands.
- Prefer dry-run or preview commands when available.
- Confirm write-risk actions when policy requires approval.

## After Editing

- Reinspect changed objects.
- Check compile status.
- Record validation notes in an active plan when the task is multi-step.
