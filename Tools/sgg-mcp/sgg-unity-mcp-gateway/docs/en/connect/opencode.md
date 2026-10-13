[English](./opencode.md) | [Русский](../../ru/connect/opencode.md)

# Connect OpenCode


OpenCode **v2** is the primary documented client for SGG Unity MCP Gateway. V1 runtime/plugin support is no longer required. Supported legacy config may still be normalized by v2; setup emits native-v2 config.

## Recommended Topology

```text
OpenCode on Windows or in WSL
          │
          │ MCP through generated config
          ▼
Gateway on Windows
          │
          ▼
Unity Editor projects on Windows
```

For OpenCode in WSL on the same machine, prefer the **Windows stdio shim**. Use remote HTTP only when a URL-based connection is intentionally required.

## Config Target

The MCP Window resolves the OpenCode config independently from the Unity project folder:

- a containing Git worktree root is used for a nested Unity project;
- an existing `opencode.json` is updated instead of creating a second `opencode.jsonc`;
- a local `.opencode` config causes a conservative Unity Setup conflict/blocker rather than false readiness in a lower layer; CLI can update an explicitly selected hidden config;
- no existing file defaults to `opencode.jsonc` in the resolved workspace;
- if both filenames exist, setup stops and shows both paths without choosing, merging, renaming, or deleting either file;
- without a containing Git worktree, setup falls back to the Unity project folder and requires confirmation before writing.

The Setup panel shows the resolved workspace, config path, source, and nested/conflict state. Use `Browse` or enter a containing parent path to save a project-scoped workspace override in local `EditorPrefs`; no project file is changed. `Auto` removes that override and reruns Git/fallback discovery. A rejected saved path remains visible as a warning until it is replaced or reset.

`Create/Update OpenCode Config` patches generated fields under `mcp.servers.sgg-unity-mcp`. A single known legacy owned entry is migrated; competing legacy/native entries fail closed. JSONC comments, indentation/line endings, unrelated servers and owned custom fields are preserved. Switching transport removes only mutually exclusive generated fields such as `command`, `url`, and the generated token header. Duplicate owned keys fail closed. Startup/catalog timeouts are separate from execution; a deliberate disable and larger execution timeout are not silently overridden.

Setup updates one selected document, not all global/ancestor configs. OpenCode v2 merges direct configs from filesystem root to its current location, then all `.opencode` layers. Selected-file readiness is not active-client evidence: reload and inspect `/mcps` or `opencode mcp list` separately.

CLI refuses to create a new hidden SGG entry over an existing direct-layer SGG server, including a deliberate disable. Legacy `experimental.mcp_timeout` remains supported migration input: native global members and then per-server members take precedence. Optional field types and the legacy-only shim protocol are checked before readiness.

After OpenCode connects and makes an MCP call, refresh Gateway health in the MCP Window. `Reported workspace verification` compares the selected workspace with bounded `roots/list` evidence from connected sessions and reports verified, ambiguous, mismatch, or unavailable. Health contains only domain-separated SHA-256 identities, never raw client paths. This is a post-connect check, not a bootstrap dependency: Git discovery or the explicit saved workspace still determines where the initial config is written.

## OpenCode Versions

| OpenCode version | Approval UX |
| --- | --- |
| OpenCode `2.0.25` source baseline | Implements form elicitation; actual frontend display and human acceptance still require validation. |
| OpenChamber v2 | MCP connection works according to the owner. Labels/forms require its own frontend evidence, not OpenCode source or a version label. |
| Any version without advertised elicitation | Uses the same fallback; the Gateway never assumes the UI exists. |
| Any client that advertises and renders form elicitation | Can use the Gateway's native one-shot prompt over a transport that carries nested server-to-client requests. |

OpenCode product versions and MCP protocol eras are separate. A version label alone never enables approval. The Gateway serves legacy MCP 2025 and modern `2026-07-28`; the active connection's negotiated protocol, advertised capabilities, UI support, and transport decide whether approval uses legacy form elicitation, modern MRTR, or fallback behavior.

## Mode 1: OpenCode On Windows

1. Start Unity and the Gateway.
2. Open `SGG/Unity MCP/MCP Window -> Setup -> MCP Connection`.
3. Select the OpenCode/Windows connection mode.
4. Use `Create/Update OpenCode Config` or `Copy OpenCode Config`.
5. Review the generated change before saving it.
6. Restart or reload OpenCode MCP configuration.

The Unity window is the source of truth for the installed runtime path and command line. Prefer its generated config to a hand-written absolute path.

## Mode 2: OpenCode In WSL Through Windows Stdio Shim

This is the recommended same-machine WSL path.

```text
OpenCode in WSL
  -> powershell.exe
  -> Windows stdio-shim
  -> Windows loopback Gateway
  -> Unity Bridge
```

In the Unity window select:

```text
Setup -> MCP Connection -> WSL via Windows stdio shim
```

Then use:

```text
Create/Update OpenCode Config
```

or:

```text
Copy OpenCode Config
```

Advantages:

- Gateway remains bound to `127.0.0.1` on Windows;
- no non-loopback Gateway listener;
- no Windows Firewall rule for the MCP port;
- the shim uses the installed Windows runtime and token environment;
- generated OpenCode Windows-stdio config enables a bounded compatibility projection so the canonical structured result is also available as compact JSON text;
- the shim remains a legacy MCP path and relays form elicitation only when the outer client advertises `elicitation.form`; other clients use `confirmation_required` fallback.

Use direct HTTP when the client supports MCP `2026-07-28` and modern MRTR. The Windows stdio shim intentionally does not advertise modern protocol support until it has a complete continuation proxy.

If the Gateway restarts while this shim is connected, the in-flight explicit call returns `mcp_session_not_found` with `request_executed:false` and is not replayed. The next separately issued call closes the invalid generation, initializes one fresh HTTP session, restores roots relay and then dispatches once.

When a generated PowerShell command is embedded in WSL/bash, keep the entire PowerShell script in single quotes so bash does not expand PowerShell variables such as `$env:...`.

## Mode 3: Remote HTTP

Use this only when OpenCode must connect by URL.

Loopback Windows example:

```jsonc
{
  "mcp": {
    "servers": { "sgg-unity-mcp": {
      "type": "remote",
      "url": "http://127.0.0.1:27182/mcp",
      "headers": {
        "X-SGG-Unity-MCP-Token": "{env:SGG_UNITY_MCP_TOKEN}"
      },
      "oauth": false,
      "disabled": false,
      "timeout": { "startup": 30000, "catalog": 30000, "execution": 3900000 }
    } }
  }
}
```

The `3900000` ms execution timeout covers the longest supported one-hour wait command plus overhead; startup/catalog remain independently bounded. Setup preserves larger user execution values; readiness accepts valid v2 defaults/inheritance and absence of `disabled`. This is not the Gateway inbound request-body timeout. Gateway uses header authentication, not OAuth: keep `oauth:false` and only an env reference in JSON.

For WSL NAT mode, `127.0.0.1` inside WSL is normally the WSL namespace rather than Windows. Either use the Windows stdio shim or intentionally configure a Windows host-IP listener.

A non-loopback Gateway requires all of the following:

```text
allow_non_loopback = true
token configured
allowed_origins non-empty
Windows Firewall restricted to trusted local/WSL access
```

Do not expose the Gateway to an untrusted network.

## Validate The Connection

Ask OpenCode to run:

With default Code Mode, discover `sgg_unity_projects` in its catalog and invoke it through `execute`. Absence from the provider-native tool list alone is not failure. Per-server `codemode:false` is an explicit direct-call alternative, not a required label fix.

```text
1. sgg_unity_projects
2. choose one live project id
3. sgg_unity_describe(project="<project-id>", selector="commands")
4. sgg_unity_call(project="<project-id>", command="compile.status", args_json="{}", mode="wait")
```

A correct first run:

- lists the intended project;
- returns a command catalog;
- reads compile state;
- does not mutate assets;
- never selects an implicit active Unity project.

For a nested Unity project, also confirm that `Reported workspace verification` says the connected session reported the parent OpenCode workspace. If it reports mismatch, check which folder OpenCode opened, update the saved workspace with `Browse`, reload the MCP config, make another MCP call, and refresh Gateway health.

## Native Elicitation Status

When a capable client resolves a command to policy action `ask`, the Gateway can send form elicitation during the original call. The Windows stdio shim now advertises and relays forms only when the outer client advertises `elicitation.form`; each request stays bound to its original concurrent call/inner generation. Unsupported, cancelled, expired or disconnected exchanges fail closed. Real human UI acceptance remains separate from transport tests.

OpenCode v2 forms are location-scoped. Verify the active frontend displays them; bounded opaque `ai.opencode/sessionID` is correlation only, not authority. Chat text, OpenCode permission prompts and stored presentation metadata are not form responses.

The prompt should identify:

```text
requesting MCP server
target project
command id
risk class
one-shot approval intent
Accept / Decline / Cancel
```

The prompt does not ask for raw command arguments, tokens, credentials, or an approval hash.

On **Accept**:

1. The capable MCP client returns the standard elicitation result.
2. The Gateway reloads policy.
3. The Gateway re-resolves the project and exact catalog descriptor.
4. Feature, schema, policy, and request binding are revalidated.
5. The exact call executes at most once.

On **Decline** or **Cancel**, the command does not execute.

If native elicitation fails, times out, or is not advertised, the response remains `confirmation_required` with the fallback workflow. A failed prompt does not silently allow the call.

## Fallback For Missing Elicitation

From a trusted local Windows terminal:

```text
sgg-unity-mcp-gateway approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project <project-id>
sgg-unity-mcp-gateway approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
```

Then the agent retries the original `sgg_unity_call` unchanged.

Rules:

- approving does not execute the command by itself;
- the approval is bound to the exact request and is consumed once;
- the agent cannot create or approve its own approval through MCP;
- `args_hash` is display/audit metadata, not an authorization credential;
- do not approve a different reconstructed call.

The Unity MCP Window can also display pending requests for local review.

## Temporary Auto-Allow Workaround

`approvals.auto_allow_ask: true` is a broad trusted-local compatibility workaround, not the recommended approval path.

It:

- affects only policy action `ask`;
- never bypasses `deny`;
- does not bypass feature flags, schema validation, project resolution, Unity-relative path rules, or PathGuard;
- is recorded in audit as auto-allowed.

Keep it disabled unless a trusted local session cannot use either native elicitation or the pending workflow.

`approvals.auto_allow_stale_registry_cleanup_without_elicitation: true` is a separate, narrower temporary default for connections without native elicitation. It applies only to `registry.stale.cleanup`, only when the active MCP connection does not advertise form elicitation, and only after `confirm_cleanup:true`, exact `record_ids`, a matching fresh `precondition_hash`, and dead/expired record revalidation. It does not authorize any Unity command or accept chat text as approval. The result reports mode `temporary_auto_allow_without_native_elicitation`; audit records `approval_status:auto_allowed` and `auto_allow_stale_registry_cleanup_without_elicitation:true`. Set it to `false` to require one-shot approval on every real cleanup.

## Common Problems

### OpenCode sees no tools

- confirm the MCP entry is enabled;
- reload OpenCode after updating config;
- check native `mcp.servers`, higher-precedence `.opencode` overrides and the default Code Mode catalog before declaring tools missing;
- verify the Gateway is running;
- verify the generated command points to the installed shim/runtime.

### `sgg_unity_projects` returns no projects

- keep the target Unity project open;
- wait for package compilation;
- verify Bridge status in the Unity window;
- call `sgg_unity_projects` again.

### WSL gets connection refused

Use the Windows stdio shim. For advanced remote HTTP, follow [Windows and WSL](./windows-wsl.md).

### A command is missing

Run `sgg_unity_describe` again. The command may be hidden by a feature flag, absent from the installed package, or unavailable after a domain reload.

### OpenCode does not show a prompt

- verify the active MCP connection advertises elicitation;
- verify the UI renders `elicitation/create`, rather than only OpenCode's ordinary `question` events;
- verify the Windows stdio shim is the updated capability-aware relay, not an older installed runtime;
- inspect the `confirmation_required` response and fallback workflow;
- do not enable `Allow All` merely to bypass a client capability problem.

## Optional v2 Presentation And Client Boundary

The bundled presentation plugin uses the public v2 `Plugin.define/setup` API and native `plugins` entries. Safe labels are additive metadata; canonical args/results/error messages are unchanged. Installer status proves file/config ownership, not active loading or visible labels. Preview before Install/Update/Remove; modified artifacts, conflicting entries and human disable controls must not be overwritten.

Stock OpenCode `2.0.25` Web cards and inner Code Mode rows do not consume this contract. Gateway does not patch OpenCode/OpenChamber. The separate [client follow-up](../maintainers/opencode-v2-client-follow-up.md) describes proposed Web/Code Mode work and visual/human gates.

Direct MCP errors carry bounded canonical JSON text alongside unchanged `structuredContent`/`isError`, since this OpenCode baseline retains only text on the error path. Success remains structured without blanket duplication. Overflow is explicit and retains bounded recovery hints; truncated evidence is not complete evidence.

## Related Docs

- [Quick Start](../quick-start.md)
- [Windows and WSL](./windows-wsl.md)
- [Permissions and approvals](../guides/permissions.md)
- [Troubleshooting](../troubleshooting.md)
