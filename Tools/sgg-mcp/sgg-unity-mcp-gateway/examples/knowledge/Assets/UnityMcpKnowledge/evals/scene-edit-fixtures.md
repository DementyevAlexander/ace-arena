---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Fixture scene changes
  - Evaluation criteria changes
---

# Scene Edit Fixtures

Describe generic fixture scenes or prefabs used to evaluate scene-edit behavior.

## Fixture Template

- Path: `Assets/Scenes/<fixture>.unity`
- Purpose: <what behavior this fixture validates>
- Safe operations: <allowed read or write operations>
- Expected validation: <commands or manual checks>
