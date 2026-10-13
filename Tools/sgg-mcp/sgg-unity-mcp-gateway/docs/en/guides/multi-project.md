[English](./multi-project.md) | [Русский](../../ru/guides/multi-project.md)

# Multi-Project Routing


SGG Unity MCP Gateway does not have a global active Unity project. Project selection is explicit and part of every Unity command call.

## Why Explicit Routing Matters

With several Editors open, an implicit active session can change because of focus, reconnects, domain reloads, or another client. Explicit routing makes the target reviewable in the request and audit trail.

```text
BAD
call scene.hierarchy against whichever Editor is active

GOOD
sgg_unity_call(
  project="tactics",
  command="scene.hierarchy",
  args_json="..."
)
```

## Discover Projects

Call:

```text
sgg_unity_projects
```

A project record can include:

- public project id;
- display name;
- instance id;
- status/readiness;
- Unity version;
- package version;
- canonical project root identity;
- deterministic aliases for Windows, WSL, Unity-style, or POSIX path views.

Host-path aliases help select the correct project. They are not permission to access that host path directly.

## Selectors

The `project` argument can resolve through supported public identities such as:

```text
project id
display name when unique
instance id
canonical project root
known alias path
```

Prefer a stable project id for ordinary calls. Use an exact instance id when two live Editors intentionally share a project id or when stale/duplicate records must be disambiguated.

## Duplicate Project Id

If several live records resolve to the same project identity, the Gateway fails closed instead of picking one.

Typical recovery:

```text
1. call sgg_unity_projects
2. inspect instance ids and status
3. close the unintended duplicate Editor, or
4. use the exact intended instance selector
5. describe the catalog again
```

Do not reuse an approval created for one instance after switching to another.

## Offline Or Stale Project

On `project_offline`, `project_stale`, or an equivalent typed lifecycle error:

```text
1. call sgg_unity_projects
2. wait for Unity to finish compile/domain reload
3. inspect current candidate instance when provided
4. run compile.status against the fresh selector
5. repeat a risky command only as a new explicit request
```

The Gateway may perform bounded recovery for safe read/status/wait operations. It does not automatically replay write, execute, destructive, or network commands after uncertain delivery.

## Per-Project Command Catalog

Different open projects can have different:

```text
Unity versions
Gateway Bridge package versions
installed Unity packages
extension command manifests
feature visibility
command descriptor revisions
```

Always discover commands for the selected project:

```text
sgg_unity_describe(project="<project-id>", selector="commands")
```

Then inspect one schema when needed:

```text
sgg_unity_describe(project="<project-id>", selector="command:<command-id>")
```

A command that exists in project A may not exist in project B.

## Per-Project Policy

Policy rules are keyed by resolved project identity. The Unity Permissions page shows the current `Project Id`; verify it before applying a profile or editing rules.

Do not:

- copy a broad allow rule to every project automatically;
- treat a trusted package in one project as trusted everywhere without policy review;
- use one project's pending approval for another project;
- use a project name when several matches are visible.

## Batches

Use `sgg_unity_batch` for bounded related calls.

Top-level scheduling modes:

```text
sequential
parallel_by_project
```

Each item still contains its own explicit:

```text
project
command
args_json or legacy args
item mode
```

`parallel_by_project` can run different project queues concurrently. Operations inside one project remain serialized.

Do not batch when:

- the next command depends on reasoning over the previous result;
- separate destructive approvals are needed;
- a command can trigger a domain reload;
- a long operation belongs in a job;
- project identity may change between steps.

## Example: Read Three Projects

Conceptual call-like items:

```json
{
  "mode": "parallel_by_project",
  "items": [
    {
      "project": "game",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    },
    {
      "project": "tools",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    },
    {
      "project": "benchmark",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    }
  ]
}
```

Use the current MCP tool schema for exact field names and limits.

## Windows And WSL Identity

CanonicalPath makes equivalent lexical project roots resolve consistently across supported Windows/WSL spellings. It does not:

- authorize arbitrary absolute paths;
- bypass PathGuard;
- turn a WSL path into a valid Unity command payload;
- resolve two genuinely different projects as one without ambiguity checks.

Agent payload paths remain `Assets/...` or `Packages/...`.

## Audit Correlation

Optional `checkpoint_id` and `plan_id` fields correlate calls in audit. They do not change the selected project, grant permission, execute a plan, or authorize rollback.

## Handoff Evidence

When handing work between agents or sessions, record:

```text
mcp availability checked in current session
selected public project id or exact instance
current Unity/package version when relevant
catalog revision/hash when relevant
compile.status result
typed error and next action when failed
```

Do not rely on an old note that said a project was online.

## Related Docs

- [Common workflows](./workflows.md)
- [MCP tools](../reference/mcp-tools.md)
- [Windows and WSL](../connect/windows-wsl.md)
- [Error codes](../reference/error-codes.md)
