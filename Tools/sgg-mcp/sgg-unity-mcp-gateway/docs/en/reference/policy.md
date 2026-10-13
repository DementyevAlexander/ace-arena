[English](./policy.md) | [Русский](../../ru/reference/policy.md)

# Policy Reference


Policy decides whether a visible, schema-valid command is allowed, requires one exact human approval, or is denied.

Policy is local operator configuration. The Gateway does not expose a general MCP tool that grants itself permissions.

## Defaults

Recommended starting posture:

```json
{
  "defaults": {
    "read": "allow",
    "write": "ask",
    "execute": "deny",
    "destructive": "ask",
    "network": "deny"
  }
}
```

These are fallbacks. Project command patterns and package trust can be stricter.

## Project Rules

```json
{
  "projects": {
    "game": {
      "allow": [
        "compile.status",
        "logs.*",
        "asset.search"
      ],
      "ask": [
        "scene.save",
        "asset.move",
        "test.run"
      ],
      "deny": [
        "shell.*",
        "process.*",
        "network.*"
      ]
    }
  }
}
```

Prefer explicit, narrow patterns. Broad wildcards can shadow more specific intent and should be reviewed in the Unity policy editor before saving.

## Decision Inputs

Policy evaluation considers:

```text
resolved project identity
current command id and descriptor revision
command risk
explicit allow/ask/deny patterns
risk defaults
package source and trust
policy revision/state
feature visibility
schema validity
```

Feature and schema checks are separate from policy. An allow rule cannot reveal a hidden command or make invalid arguments valid.

## Precedence

Practical safe model:

```text
explicit deny wins
untrusted package gate applies
specific project patterns apply
risk default fills unmatched command
ask requires exact approval
```

Use `policy.explain` when enabled to inspect a target decision without executing it or consuming approval.

## Native Elicitation

For clients that advertise and render MCP form elicitation:

```text
ask decision
 -> Gateway requests one form approval inside the original call
 -> Accept / Decline / Cancel
 -> full project/catalog/feature/schema/policy revalidation
 -> exact request executes at most once on valid Accept
```

Accept does not modify policy. Context drift invalidates the displayed generation.

## Pending Approval

Clients without elicitation receive `confirmation_required`. A human can inspect and approve one pending id outside MCP, then the agent repeats the exact call.

Approval is bound to the resolved request context. It is not created from an arbitrary model-provided hash and is not reusable for changed arguments, project, descriptor, instance, or policy.

## Policy Reload

The running Gateway can adopt a valid stable policy replacement between calls.

If a configured replacement becomes missing, unreadable, unstable, oversized, or invalid:

```text
policy state -> degraded
last-known-good reads -> may continue where allowed
write/execute/destructive/network -> fail closed
approvals/jobs/invocations -> not created or replayed for blocked risky work
```

Restoring a valid file recovers future calls. It never replays a previously blocked request.

## Profiles

The Unity `Permissions` page provides bounded profiles such as:

```text
Read-only
Inspect
API Reflection
Unity Docs
Profiler Readonly
Profiler Recording
Package Management
Test Validation
Safe Writes
Runtime Debug
Allow All
```

A profile changes policy. `Apply + Enable Flags` also updates matching feature configuration, which requires a Gateway restart.

Read-only is intended for the first smoke test, not all normal work. Allow All is for a fully trusted local project and does not automatically trust extension packages.

## Package Trust

```json
{
  "trusted_packages": ["com.example.reviewed-tools"],
  "untrusted_package_default": "ask"
}
```

A package cannot self-declare trust. Use `ask` or `deny` for unreviewed packages. Even trusted packages remain subject to explicit deny, feature flags, schemas, path checks, approvals, limits, and audit.

## Editing Policy

Supported operator paths:

```text
SGG/Unity MCP/MCP Window -> Permissions
sgg-unity-mcp-gateway policy apply-profile ... --dry-run
manual edit of the user policy file followed by validation/reload
```

Use dry-run and review. Do not let an agent silently broaden wildcards or replace policy as a side effect of a Unity task.

## Temporary Auto-Allow

`approvals.auto_allow_ask` can auto-allow actions whose policy result is exactly `ask`. It never bypasses deny, hidden features, invalid schemas, trust, project resolution, or PathGuard.

Keep it disabled by default. Native elicitation on a capable connection is preferred; OpenCode v2 implements forms, but actual frontend/human acceptance must be verified separately.

## Related Docs

- [Permissions and approvals](../guides/permissions.md)
- [Configuration](./configuration.md)
- [Security](./security.md)
- [Extension governance](../extensions/governance.md)
