# Canonical Path Diagnostics

Use this workflow to diagnose project identity and path handling issues.

## Inputs

- Current project id or project alias.
- Symptom involving project discovery, aliases, or Unity-relative paths.

## Workflow

1. List projects and compare canonical roots and aliases.
2. Read diagnostics status when the diagnostics feature is enabled.
3. Confirm asset operations use Unity-relative `Assets/...` or `Packages/...` paths.
4. Report whether the issue is identity, configuration, or command input shape.

## Safety

CanonicalPath is identity metadata, not a bypass. Do not request arbitrary absolute paths or bypass PathGuard.
