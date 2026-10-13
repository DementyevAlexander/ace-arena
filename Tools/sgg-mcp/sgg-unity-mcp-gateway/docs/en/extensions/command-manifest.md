[English](./command-manifest.md) | [Русский](../../ru/extensions/command-manifest.md)

# Command Manifests


A command manifest registers Editor commands without a compile dependency on the SGG bridge API. The handler still runs inside the trusted local Unity Editor process, so metadata and policy must be conservative.

## Supported Locations

```text
Packages/<package-id>/Editor/Mcp/mcp.commands.json
Packages/<package-id>/mcp.commands.json
```

Legacy candidates under `Assets/Scripts/**/mcp.commands.json` may be reported for diagnostics, but they are not registered as package commands.

## Top-Level Shape

```json
{
  "schema_version": 1,
  "package": "com.example.tools",
  "commands": []
}
```

| Field | Contract |
| --- | --- |
| `schema_version` | Required manifest schema version. Use `1` for the current format. |
| `package` | Required installed Unity package id. Must match package identity. |
| `commands` | Bounded array of command descriptors. |

## Descriptor Fields

| Field | Required | Purpose |
| --- | --- | --- |
| `id` | yes | Lowercase dot-notation command id such as `example.status`. |
| `title` | recommended | Short human-facing label. |
| `description` | yes | Clear agent-facing behavior and non-goals. |
| `handler` | manifest-only | Fully qualified static method name. Omit for an attributed handler with the same id. |
| `risk` | yes | `read`, `write`, `execute`, `destructive`, or `network`. |
| `idempotency` | yes | Accurate repeat behavior, normally `safe`, `idempotent`, or a documented non-repeat form supported by the descriptor contract. |
| `timeout_ms` | yes | Bounded expected execution timeout. |
| `schema` | yes | JSON object schema for `args_json`. |
| `output_limit` | yes for textual/bounded output | Maximum inline output such as `max_chars`. |
| `feature_flag` | optional | Local feature family that must be enabled. |
| `supports_job` | optional | Advertises job-capable execution when the handler contract supports it. |
| `supports_cancel` | optional | Advertise only when cancellation is actually implemented. |

Use the current examples and checker as the exact source of truth when descriptor fields evolve.

## Schema Guidance

Prefer strict object schemas:

```json
{
  "type": "object",
  "properties": {
    "asset_path": {
      "type": "string",
      "description": "Unity-relative Assets/... path"
    },
    "max_results": {
      "type": "integer",
      "minimum": 1,
      "maximum": 100,
      "default": 20
    }
  },
  "required": ["asset_path"],
  "additionalProperties": false
}
```

Rules:

```text
- reject unknown fields;
- require bounded counts and time ranges;
- describe Unity-relative path constraints;
- avoid arbitrary absolute paths;
- avoid raw shell command fields;
- avoid credential fields;
- expose dry_run for reviewable writes where appropriate;
- expose overwrite=false or explicit preconditions for file creation/replacement;
- keep defaults safe.
```

## Risk Classification

| Risk | Use for |
| --- | --- |
| `read` | Inspection with no intended project or external side effect. Cache/index rebuild may still need a more explicit classification if it writes state. |
| `write` | Creates or changes project-local state without destructive replacement. |
| `execute` | Runs code, tests, builds, menu actions, or other active operations. |
| `destructive` | Deletes, overwrites, restores, prunes, closes, terminates, or can lose state. |
| `network` | Reads or writes through a network boundary. |

Choose the highest material risk. Do not label a write as read because the primary result is diagnostic.

## Output Limits And Artifacts

Keep inline results small. For larger output:

```text
1. Store a bounded artifact under an approved project-local SGGUnityMcp root.
2. Return artifact_ref plus a compact summary.
3. Let agents inspect metadata first.
4. Read bounded slices through job.artifact.read or the relevant artifact command.
```

Do not accept arbitrary artifact output paths from the model.

## Manifest-Only Handler Contract

Supported forms:

```text
static string Handler()
static string Handler(string argsJson)
static string Handler(string argsJson, string settingsJson)
```

The result string must be valid JSON. Throwing an exception is treated as command failure; prefer returning typed, bounded domain errors through the supported JSON result shape when possible.

## Attribute And Manifest Interaction

For an attributed `[McpCommand]` handler, a manifest may provide package-level metadata without `handler`.

Do not register the same command id twice:

```text
[McpCommand] + manifest handler for same id -> invalid/ambiguous
attribute handler + metadata-only descriptor -> supported pattern
manifest-only handler + no attribute -> supported pattern
```

## Security Review

Before shipping:

```text
[ ] command id cannot impersonate built-ins;
[ ] package id matches installed package identity;
[ ] risk and idempotency are present;
[ ] schema is strict and bounded;
[ ] output is bounded or artifact-backed;
[ ] untrusted package behavior was tested;
[ ] write/execute/destructive/network path needs explicit policy;
[ ] no self-trust or self-approval field exists;
[ ] no arbitrary filesystem, process, shell, or credential access is hidden in generic fields.
```

## Validation

From the Gateway repository:

```bash
npm run check:manifest-security
npm run check:command-descriptors
```

Use the generic good and intentionally invalid fixtures under `examples/extensions/` as review examples. Files named `mcp.commands.bad.json` are documentation/test fixtures and must not be copied as loadable manifests.

## Related Docs

- [Extension quick start](./quick-start.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
