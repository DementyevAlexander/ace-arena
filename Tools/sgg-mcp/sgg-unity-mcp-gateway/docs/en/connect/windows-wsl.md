[English](./windows-wsl.md) | [Русский](../../ru/connect/windows-wsl.md)

# Windows And WSL


The intended same-machine topology is:

```text
OpenCode / another client in WSL
          │
          ▼
Gateway on Windows
          │
          ▼
Unity Editor on Windows
```

Keeping the Gateway on Windows places Unity Bridge discovery, loopback Bridge calls, LocalAppData state, and Unity paths in one OS context.

## Unity CLI Is Not Shared

Windows `unity.exe` and a Linux `unity` binary installed inside WSL are separate installations. They can have different Editor registries, credentials, configuration, and `PATH` state.

For a Windows Unity Editor:

- Windows `unity.exe` is authoritative for `unity pipeline install`, `unity pipeline list`, and `unity command`.
- The Unity MCP Window checks Windows and default-WSL command resolution separately.
- If OpenCode/Codex runs in WSL, the recommended agent environment is a reviewed wrapper where `unity` executes `unity.exe "$@"`.
- A standalone Linux CLI remains valid for Linux workflows, but it is reported as `Separate Linux CLI` and does not satisfy the Windows Pipeline prerequisite.

The window can install the fixed wrapper into `~/.local/bin/unity` after explicit confirmation or copy the setup instructions for manual review. It never overwrites an existing file/symlink and never changes shell profiles automatically.

## CLI Beta Compatibility

Unity CLI `1.0.0-beta.6` distinguishes a completed failing `unity test` run (exit code `8`, `TESTS_FAILED`) from a run that produced no verdict because of infrastructure, timeout, crash, compile, or license failure (exit code `6`, such as `TEST_RUN_ERROR` or `TEST_TIMED_OUT`). Do not retry exit 8 as infrastructure.

Pipeline `0.5.0-exp.1` can return typed `503 Editor is still settling` during a cold `unity run --command` startup even when status already says ready. Retry only a read/no-op command with authoritative `command_dispatched:false`, inside one fixed startup deadline. If the main-thread gate remains closed, stop with `unity_cli_cold_main_thread_gate_stuck`; do not poll indefinitely or replay a write or uncertain delivery. `-automated` is not a supported workaround. Prefer an explicitly owned persistent Editor and warm `unity command` for critical CI until the cold path is version-proven.

## Recommended: Windows Stdio Shim

For OpenCode in WSL:

```text
SGG/Unity MCP/MCP Window
  -> Setup
  -> MCP Connection
  -> WSL via Windows stdio shim
```

Then create/update `opencode.jsonc` from the window.

The path is:

```text
OpenCode in WSL
  -> powershell.exe
  -> installed Windows stdio-shim
  -> http://127.0.0.1:27182/mcp on Windows
```

This avoids opening the Gateway on a non-loopback interface.

## WSL NAT Mode

In WSL NAT mode:

```text
127.0.0.1 inside WSL != Windows loopback
```

Direct WSL HTTP to `http://127.0.0.1:27182/mcp` can fail even when Windows PowerShell can reach the Gateway.

Preferred solutions:

1. use the Windows stdio shim;
2. intentionally configure remote HTTP to the Windows host IP;
3. use mirrored networking only when its localhost behavior is verified on the current machine.

## WSL Mirrored Networking

With mirrored networking, localhost forwarding may work:

```text
http://127.0.0.1:27182/mcp
```

Do not assume it works merely because mirrored mode is enabled. Verify from the actual client process and keep the stdio shim as the predictable fallback.

## Advanced Remote HTTP

Remote URL:

```text
http://<windows-host-ip>:27182/mcp
```

Required Gateway posture:

```text
mcp.allow_non_loopback = true
mcp.token_env configured
mcp.allowed_origins non-empty
Windows Firewall restricted to trusted local/WSL access
```

Do not bind to all interfaces for convenience. The Gateway is not an internet-facing service.

## Firewall Boundary

Only the Gateway endpoint may need a firewall rule. Unity Bridge listeners remain local to Windows and are called by the Gateway.

Restrict inbound access to the local development environment. Do not expose:

```text
Gateway MCP port
Bridge ports
approval directories
audit directories
LocalAppData runtime roots
```

to untrusted machines or users.

## Path Rules

Agent command payloads use:

```text
Assets/...
Packages/...
```

Do not send:

```text
C:\project\Assets\...
/mnt/c/project/Assets/...
/home/user/project/...
../Assets/...
```

unless a specific non-agent installation/config field explicitly accepts a host path.

CanonicalPath helps the Gateway identify equivalent Windows/WSL project roots, but this identity layer does not authorize arbitrary filesystem access.

## PowerShell From Bash

When invoking `powershell.exe -Command` from WSL/bash, wrap the whole PowerShell script in single quotes. Otherwise bash expands `$env:...`, `$root`, and similar PowerShell variables before PowerShell receives them.

Conceptual safe shape:

```bash
powershell.exe -NoProfile -NonInteractive -Command '$root = Join-Path $env:LOCALAPPDATA "SGG\UnityMcpGateway"; ...'
```

Use the exact command generated by the Unity window.

## Diagnose Connectivity

From Windows:

```text
Setup -> Refresh Health
```

From the MCP client:

```text
sgg_unity_projects
```

Interpretation:

| Result | Meaning |
| --- | --- |
| Gateway Health fails in Unity | Gateway process/config/runtime problem on Windows. |
| Health works, stdio shim fails | Generated command, token environment, or shim startup problem. |
| MCP connects, no projects | Bridge registry/heartbeat or Unity compile state problem. |
| Windows HTTP works, WSL HTTP refuses | Namespace/NAT problem; use stdio shim or intentional host-IP listener. |
| Project exists but command fails | Read typed command/policy/schema error; transport is already working. |

The CLI `doctor --json` and offline `report-problem --include-doctor` can provide bounded redacted diagnostics.

## Related Docs

- [OpenCode](./opencode.md)
- [Installation](../installation.md)
- [Security](../reference/security.md)
- [Troubleshooting](../troubleshooting.md)
