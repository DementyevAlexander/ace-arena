[English](./installation.md) | [Русский](../ru/installation.md)

# Installation


The normal installation path is entirely driven from the Unity package and `SGG/Unity MCP/MCP Window`. A system-wide Node.js installation and manual `npm ci` are not required for ordinary users.

## Choose An Install Mode

| Mode | Use it when | Recommended |
| --- | --- | --- |
| Pinned Git UPM tag | Installing a released or shared package version from this repository. | Yes |
| Local package path | Developing the Gateway/package from a local checkout or working before the first public tag. | Yes for development |
| Manual repository runtime | Debugging Gateway source, changing TypeScript, or validating release artifacts. | Advanced only |

Do not install a separate MCP server per Unity project. Every project gets the lightweight Unity Bridge package; one local Gateway process serves all open projects.

## Requirements

- Windows host for Unity Editor and the normal Gateway runtime.
- Unity `6000.4+` recommended for current development.
- Package import metadata currently uses Unity `2022.3` as a compile/import floor; tested release support is recorded separately.
- Access to a pinned Git tag or local repository checkout.
- An MCP client such as OpenCode, Cursor, or Unity AI Assistant.

The Gateway does not depend on URP or HDRP. Individual package-contributed commands may have their own package or render-pipeline requirements.

## Install With Git UPM

Add a pinned dependency to the Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#2026.10.12-2"
  }
}
```

Pin a tag or commit for repeatable installs. Do not leave production projects on an unpinned moving branch.

For a private repository, Unity/Git must have access through the user's normal Git credentials. The package itself never asks an agent to collect Git credentials.

## Install From A Local Checkout

Open Unity Package Manager and select:

```text
Add package from disk...
```

Choose:

```text
<gateway-repository>/unity/Packages/com.sungeargames.unity-mcp/package.json
```

Equivalent local manifest shape:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "file:<relative-or-absolute-local-package-path>"
  }
}
```

Use a path form that Unity Package Manager resolves reliably on the Windows host. Agent command payloads still use Unity-relative `Assets/...` and `Packages/...` paths; the local UPM dependency path is an installation concern, not an agent payload pattern.

## CanonicalPath Dependency

CanonicalPath is required for deterministic Bridge discovery and path-bound MCP commands, but the Unity MCP package intentionally does not hard-depend on it in its UPM manifest. This lets the Setup window compile before the provider is installed.

The Setup window can configure the public registry and install the provider. Manual registry shape:

```json
{
  "scopedRegistries": [
    {
      "name": "npmjs",
      "url": "https://registry.npmjs.org",
      "scopes": [
        "com.romanilyin"
      ]
    }
  ]
}
```

A project may also pin the provider directly:

```json
{
  "dependencies": {
    "com.romanilyin.canonicalpath": "2026.10.10-4"
  }
}
```

Use the version expected by the installed Gateway package/Setup diagnostics. Do not copy an old version from evergreen documentation.

For Gateway `2026.10.11-1`, update an older registry provider to CanonicalPath `2026.10.10-4` using **Update CanonicalPath / CP↑** in Setup or Package Manager. Setup shows installed and required versions and requests explicit project-scoped confirmation. Updating Gateway alone does not update this separately installed dependency. Local/Embedded/Git sources require manual review; Setup never converts them or downgrades a current/newer provider. Pending package resolution or an unavailable loaded provider gets distinct guidance, not another package request or false readiness. Update the provider before validating Bridge discovery.

After installing the runtime, the **G\*** header action explicitly starts it or stops and restarts an existing Gateway; restart interrupts current connections. A separate Stop is not required. An absent listener permits cold start only after successful complete inspection. Ordinary Start retains its bounded health-retry refusal instead of launching a second process; an inspection/stop failure still blocks restart. This action does not install a runtime update itself.

CanonicalPath provides lexical identity. It is not a filesystem sandbox. Root-bound Unity access remains guarded by the Bridge's PathGuard, and CanonicalFS is the relevant boundary where used.

## Run The Unity Setup Window

After Unity finishes compiling, open:

```text
SGG/Unity MCP/MCP Window
```

The first `Setup` page contains:

```text
Setup Checklist
Gateway Test History
MCP Token
MCP Connection
First MCP Smoke Test
Permission Setup
Dependencies / Optional Tools
```

Complete the required actions:

```text
1. Configure/install CanonicalPath if missing.
2. Install Portable Node.
3. Install Runtime.
4. Start Gateway.
5. Refresh Health.
```

Setup has separate **CLI**, **Pipeline**, and **Skill** blocks inside the optional Unity tools group, with buttons directly below each block. Blue means latest checked, red means a known update is available, a yellow diamond means not installed, and purple means unknown/informational. A failed latest check never proves a tool is outdated. Each manual button checks only its component; Pipeline and Skill can explicitly query available versions even before installation (Pipeline still validates its local Windows CLI prerequisite). Automatic checks remain installed-only, and no check installs anything. Pipeline shows the available package version separately; Skill shows installed and available official Unity commit identities, never an invented CLI compatibility version.

- `Windows Unity CLI` is the authoritative CLI for a Windows Editor. A known absent official target exposes **Install Windows CLI**; unknown or existing targets require detection/review, not overwrite. Installation runs the official beta installer under a five-minute bound and validates `%LOCALAPPDATA%/Unity/bin/unity.exe` without a PATH fallback. It can change user PATH and the Unity Hub install receipt. **Check CLI Updates** establishes a fresh trusted exact update binding; **Update Windows CLI** then confirms the exact target version and never silently retries.
- The **Pipeline** block exposes **Install Pipeline** only for an absent package, with Unity 6 and a trusted Windows CLI as prerequisites. **Check Pipeline Updates** and **Update Pipeline** concern only the current project and compatible exact package versions. Wait for Package Manager and compilation verification; process success alone is not readiness.
- **WSL (optional)** explains Windows Subsystem for Linux and shows default-distribution command resolution on an explicit probe. A separate Linux CLI is not the Windows Pipeline CLI; no wrapper or shell change happens during detection.
- The Skills tab stages a fixed file list from the official `Unity-Technologies/skills` `unity-cli` path at its exact supported latest commit. Automatic immutable-source/full-content integrity checks precede copying into `.agents/skills/unity-cli` for OpenCode and Codex; additional manual content review is not required. This bounded Windows download does not use Node/npm, Git branches, `skills` CLI or moving content; it remains project-scoped and does not change SGGMcp policy or `.mcp/skills` review state.

All three components are optional. **Install Unity CLI Skill** is also available below the Skill block for a known-missing project target when the bounded Windows download transport is ready. Existing unmanaged/modified skill targets are not overwritten. Source/schema drift is not silently trusted: the immutable acquisition target must match the supported official snapshot. Every install/update requires explicit confirmation. Closing/reloading during a CLI update yields uncertain state requiring local verification, never an automatic retry. The Windows CLI installer downloads its script to a temporary file instead of piping it to `Invoke-Expression`. The window never authenticates, silently installs Pipeline, or changes WSL PATH. Real install/update acceptance belongs to an isolated authorized profile or the owner, not read-only freshness evidence.

Skill publication revalidates and holds safe directory ancestors, then atomically moves the verified staging tree without merging an existing target. A changed/appeared destination is preserved. Updates retain the previous verified content and completed acquisition receipt under unique `Library/SGGUnityMcp/setup` backup paths. Failed replacements are preserved for inspection; automatic rollback requires exact original and replacement directory identities. Uncertain files are never recursively deleted. Inspect retained staging/backup paths before a manual retry.

The normal local root is:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/
```

Typical subdirectories:

```text
runtime/       private portable Node runtime
gateway/       installed bundled Gateway runtime
config/        user-owned gateway.json and policy.json
instances/     Unity Bridge heartbeat records
approvals/     pending and one-shot approval state
audit/         redacted audit records
logs/          Gateway process logs
index/         project-local cache roots
secrets/       local approval binding material
```

Keep these roots owner-controlled. Do not commit them to a project repository.

### Test History Capacity

Setup's **Gateway Test History** section shows separate byte and record progress bars for durable Gateway owner metadata—not Unity result artifacts or a limit imposed by MCP. Defaults are **200 MiB (209715200 bytes)** and **192000 records**, with 30-day retention and unchanged 16 KiB per-record/publisher concurrency bounds. The bars compare usage against saved config; they do not certify the running process's limits. Unknown or failed scans are shown as unavailable, never empty storage.

**Storage Settings** accepts 1–1024 MiB and 1–1000000 records. **Save Storage Limits** requires confirmation, preserves unrelated config/policy, and requires a Gateway restart to take effect. It neither deletes history nor restarts/enables permissions automatically. A record-only edit preserves an existing non-integral MiB byte limit exactly. The strict config fields are:

```json
"test_runs": {
  "storage": {
    "max_bytes": 209715200,
    "max_bindings": 192000,
    "retention_days": 30
  }
}
```

**Clear Finished History** previews at most 256 removable records, then asks for confirmation. Only validated terminal no-execution receipts or exact-bound authoritative terminal test results justify removal of Gateway `.json/.issued/.reserve/.terminal` metadata. Active, unknown, foreign, invalid, reserved and changing records remain untouched; age alone is not proof of completion. Unity artifacts and reports are never deleted. A stale/config-changed preview is refused; uncertain cleanup is not replayed. Scans have a finite entry cap and a 10-second administration deadline; an unavailable scan cannot authorize cleanup. The same bounded local interface is available through `--test-run-store status|preview-cleanup|cleanup|configure --config <absolute gateway.json> --json`; mutations require exact fingerprints and `--confirm`.

## Token Setup

The normal token contract is:

```text
Environment variable: SGG_UNITY_MCP_TOKEN
Client header:        X-SGG-Unity-MCP-Token
```

Loopback-only development can run without a token when no token is configured, but setting one is recommended. A non-loopback listener must not be enabled without token authentication, allowed origins, and appropriate local network/firewall controls.

The Setup copy helpers use placeholders. They must not write a real token into shared documentation, screenshots, diagnostics, or committed client config.

## Gateway Runtime Installation

`Install Runtime` copies the Gateway runtime bundled with the Unity package into LocalAppData and validates the installed CLI with the private portable Node runtime. The normal flow does not run `npm ci` and does not depend on the system `PATH`.

`Start Gateway` creates user config/policy files when missing, starts the local process, and probes:

```text
MCP:    http://127.0.0.1:27182/mcp
Health: http://127.0.0.1:27182/health
```

These are defaults; use the Setup window and actual config as the source of truth.

## Updates

Recommended update flow:

```text
1. Update the pinned Unity package tag/commit.
2. Let Unity resolve and compile the package.
3. Open the MCP Window.
4. Review bundled versus installed runtime status.
5. Install Runtime when Setup reports a stale or mismatched runtime.
6. Start/restart Gateway.
7. Refresh Health.
8. Run the first read-only smoke test.
```

One Gateway can serve multiple open projects, but do not assume arbitrary package/runtime version combinations are compatible. Resolve warnings shown by Health/version diagnostics.

Package or manifest changes can require an explicit Unity refresh/recompile. The Bridge may report `package_refresh_required` or `stale_package_resolution`; it does not promise to refresh dirty projects automatically. Review dirty-scene risk, then use the appropriate explicit refresh/validation command or Unity UI action.

## First Permissions

Create explicit user-owned config and policy files from `Setup -> Permission Setup`.

Recommended first path:

```text
1. Apply Read-only only for the first smoke test.
2. Verify projects -> describe -> compile.status.
3. Open Permissions.
4. Use Apply + Enable Flags for the smallest real workflow profile.
```

Policy file replacements are live-reloaded when valid. The owner-saved `approvals.auto_allow_known_unity_popups` boolean also applies on the next request without restarting; invalid or unreadable config disables that opt-in. Other feature/config changes still require a Gateway restart before catalog visibility changes.

## Remove The Package

To remove it from one Unity project:

1. Remove `com.sungeargames.unity-mcp` from `Packages/manifest.json` or Package Manager.
2. Let Unity finish package resolution and compilation.
3. Remove CanonicalPath only when no other package/project workflow needs it.
4. Do not delete shared LocalAppData while other Unity projects still use the Gateway.

To remove the local Gateway installation:

1. Stop Gateway from the MCP Window or terminate the known local Gateway process.
2. Close Unity projects that use the Bridge.
3. Back up user policy/audit data when needed.
4. Remove `%LOCALAPPDATA%/SGG/UnityMcpGateway/`.

Never let an agent delete this root through arbitrary filesystem access.

## Advanced Manual Repository Mode

Use this only for Gateway development or source-level debugging.

From the repository root:

```bash
npm ci
npm start -- --config config/gateway.example.json --policy config/policy.example.json
```

Use `npm install` only when intentionally updating dependencies.

Set the token in the process environment before starting the Gateway:

```powershell
$env:SGG_UNITY_MCP_TOKEN = "replace-with-local-token"
```

Manual mode still uses the same core rules:

- one Gateway;
- explicit project routing;
- Bridge heartbeat discovery;
- command catalog discovery;
- local policy/approval;
- Unity-relative agent paths.

## Verify The Installation

Run:

```text
sgg_unity_projects
sgg_unity_describe(project="<project-id>", selector="commands")
sgg_unity_call(project="<project-id>", command="compile.status", args_json="{}", mode="wait")
```

See [Quick Start](./quick-start.md) for expected results and [Troubleshooting](./troubleshooting.md) when Health or project discovery fails.
