---
owner: <project-owner-or-team>
last_reviewed: <YYYY-MM-DD>
scope: unity-project
source_of_truth: true
verification_status: draft
trust_label: project-authored
known_staleness_risks:
  - Compiler version changes
  - Assembly definition changes
---

# Fix Compile Errors

## Steps

- Read bounded compile diagnostics through the command catalog.
- Group errors by file and root cause.
- Make the smallest source edit that addresses the first root cause.
- Recheck compile status.

## Stop Conditions

- Missing package or generated code requires human input.
- Fix requires broad refactoring beyond the requested task.
