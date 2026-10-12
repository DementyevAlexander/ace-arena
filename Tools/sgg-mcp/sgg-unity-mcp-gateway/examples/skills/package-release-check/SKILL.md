# Package Release Check

Use this workflow before preparing a generic Unity package release.

## Inputs

- Current project id.
- Package name and expected version.

## Workflow

1. Read package list and compile status.
2. Check package metadata and documentation references.
3. Verify tests or build commands only when discoverable and policy allows them.
4. Return a checklist with blockers and manual follow-ups.

## Safety

This skill is advisory. It does not publish packages, edit policy, run shell commands, or access external MCP servers.
