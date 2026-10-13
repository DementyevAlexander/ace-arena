---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Scene workflow changes
  - Validation command changes
---

# Safe Scene Edit

## Steps

- Inspect current scene state with bounded commands.
- Make one focused change at a time.
- Use approval flow for write-risk commands when required.
- Validate hierarchy, references, and compile status after the edit.
