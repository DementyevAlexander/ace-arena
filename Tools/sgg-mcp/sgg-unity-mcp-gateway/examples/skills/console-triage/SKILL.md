---
name: console-triage
description: Review Unity console symptoms and summarize likely causes.
enabled: false
required_editor_version: 6000.0
tools:
  - compile.status
  - diagnostics.audit.tail
---

# Console Triage

Use this workflow to inspect Unity console symptoms and produce a concise triage summary.

## Inputs

- Current project id.
- Recent compile or console symptoms.

## Workflow

1. Discover available commands with `sgg_unity_describe`.
2. Read bounded console logs and compile status.
3. Group errors by likely root cause.
4. Recommend the smallest next read-only check.

## Safety

Do not run shell commands, dynamic C#, or external MCP servers from this skill. Do not change policy or bypass PathGuard/CanonicalPath.
