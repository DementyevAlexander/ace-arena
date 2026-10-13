[English](./configuration.md) | [Русский](../../ru/reference/configuration.md)

# Configuration


The normal setup window creates user-editable Gateway configuration under:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json
%LOCALAPPDATA%/SGG/UnityMcpGateway/config/policy.json
```

Use `config/gateway.example.json` and `config/policy.example.json` as schema examples. Do not commit local tokens, approval keys, user paths, or production policy files.

## Normal Setup

Use:

```text
SGG/Unity MCP/MCP Window -> Setup
```

The window installs portable Node, copies the bundled Gateway runtime, creates config/policy when missing, starts the Gateway, exposes safe client config helpers, and shows health.

For standard workflows, prefer the window over hand-editing JSON.

## MCP Transport

Representative fields under `mcp`:

| Field | Purpose |
| --- | --- |
| `host` | Bind address. Default is loopback. |
| `port` | Local Gateway port. Default documentation uses `27182`. |
| `token_env` | Environment variable holding the token. |
| `allowed_origins` | Exact browser Origin allowlist when Origin is present. |
| `allow_non_loopback` | Explicit opt-in for non-loopback binding. |
| `max_body_bytes` | Request size bound. |
| `request_timeout_ms` | Request handling bound. |
| `shutdown_timeout_ms` | Best-effort transport shutdown bound. |
| `sessions` | Active stateful-session cap, idle TTL, cleanup interval. |
| `rate_limit` | Per-client request bounds and optional trusted-proxy mode. |

Do not enable non-loopback binding without a token, non-empty allowed origins, and scoped local firewall/network controls.

## Token

```text
Environment variable: SGG_UNITY_MCP_TOKEN
Client header:        X-SGG-Unity-MCP-Token
```

The window's copy helpers use placeholders, not real secrets.

Prefer process environment or a client secret store. Do not paste tokens into committed MCP configuration, screenshots, issues, or documentation.

## Registry

The `registry` section controls the local bridge heartbeat directory and bounded discovery behavior.

Common concepts:

```text
instances_dir
stale_after_ms
cleanup_after_ms
scan_max_files
cache_max_entries
cache_ttl_ms
invalid_diagnostics_max
```

The Gateway treats `cleanup_after_ms` as a read-side expiration boundary; normal heartbeat lifecycle belongs to Unity. Do not script broad deletion of registry files while Unity/Gateway processes are active.

## Logging And Audit

```json
{
  "logging": {
    "level": "info",
    "audit_enabled": true,
    "audit_dir": "%LOCALAPPDATA%/SGG/UnityMcpGateway/audit"
  }
}
```

Operational logging and audit have different roles. Audit records command admission/outcome evidence; normal logs describe Gateway state. Both must remain redacted and bounded.

Keep audit/approval roots under the current user's controlled LocalAppData on Windows.

## Approvals

```json
{
  "approvals": {
    "dir": "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals",
    "hmac_key_file": "%LOCALAPPDATA%/SGG/UnityMcpGateway/secrets/approval-hmac-v1.key",
    "default_ttl_ms": 300000,
    "auto_allow_ask": false,
    "auto_allow_known_unity_popups": false,
    "auto_allow_stale_registry_cleanup_without_elicitation": true
  }
}
```

Do not share or commit the approval key. `auto_allow_ask` is a broad compatibility workaround for trusted local sessions, not the recommended approval path. `auto_allow_stale_registry_cleanup_without_elicitation` is a temporary narrow default for exact precondition-bound dead-record cleanup on clients without form elicitation; capable clients still require the native prompt. Set it to `false` to require one-shot approval for every real cleanup.

## Cache

`cache.asset_index_root` is a Gateway-local root. The Gateway derives project-specific cache identity from the resolved project id/canonical root.

Do not point caches at source-controlled asset directories or shared untrusted locations.

## Feature Flags

The `features` object controls command-family visibility. Examples:

```text
semantic_scene_inspection
asset_index
asset_readonly
asset_safe_writes
editor_safe_writes
build_test_automation
runtime_state_debugging
unity_api_reflection
unity_docs_local
profiler_readonly
profiler_recording
ugui_inspection
uitk_inspection
uitk_migration
sgg_mcp_diagnostics
sgg_mcp_plans
sgg_mcp_pipelines
sgg_mcp_skills
sgg_mcp_checkpoints
sgg_mcp_artifacts
sgg_mcp_policy
```

Enable only what the current workflow needs. Feature changes require a Gateway restart; policy changes can reload between calls when the replacement is valid and stable.

## Official Relay Adapter

`official_relay` is optional and disabled by default. When intentionally enabled with its matching feature flag, explicitly allowlisted official relay tools can appear as `official.<toolName>` catalog commands.

They still use `sgg_unity_call`, SGG policy/approval, and configured Unity-relative path checks. Local SGG commands do not depend on relay availability.

## Skills

The optional `skills.unity_user_skills_root` identifies a user-level Unity skill source. Skill discovery/import remains feature- and policy-gated. A skill file is prompt/resource data, not executable authority.

## Reload Behavior

```text
valid policy replacement -> live reload between calls
invalid/missing/unstable policy replacement -> degraded state; risky commands fail closed
saved approvals.auto_allow_known_unity_popups boolean -> live on next request on a supported file-backed runtime
feature/other config change -> restart Gateway
package/bridge change -> Unity compile/reload; refresh catalog afterward
```

No blocked or uncertain risky command is replayed automatically after recovery.

The only live Gateway config setting is the owner-saved `approvals.auto_allow_known_unity_popups` boolean, default `false`. On runtimes supporting this mode, the original config file is reread before requests and rechecked before real built-in popup response issuance, including revocation. Reads must be stable and bounded to 1 MiB; the full config is strictly validated and duplicate JSON keys are rejected. Missing, unreadable, invalid, oversized or unstable config disables the opt-in instead of retaining a previous `true`. Other settings, including features, `auto_allow_ask`, approval keys/directories, token and network configuration, remain startup-bound and require restart.

Check the running `popup_response_approval.enabled` in `diagnostics.status` or `diagnostics.unity_modal.status`, not just the saved checkbox. File-backed contexts may also expose `application_mode:live_on_request`, `source_status`, `saved_enabled` and `restart_required:false`. The UI never enables this setting on its own; saving an owner-chosen value does not submit a popup response or replay a command. The owner warning, exact supported replies and command safeguards remain as described in [Permissions](../guides/permissions.md#owner-preapproved-known-unity-popups).

## Advanced Manual Start

From a repository checkout:

```powershell
$env:SGG_UNITY_MCP_TOKEN = "replace-with-local-token"
npm ci
npm start -- --config config/gateway.example.json --policy config/policy.example.json
```

Use this only for Gateway development. The normal user flow is the bundled runtime installed by the Unity window.

## Related Docs

- [Installation](../installation.md)
- [Policy](./policy.md)
- [Security](./security.md)
- [Windows and WSL](../connect/windows-wsl.md)
