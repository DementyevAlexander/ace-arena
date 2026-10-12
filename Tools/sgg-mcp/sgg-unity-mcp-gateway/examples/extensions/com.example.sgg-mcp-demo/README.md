# Example SGG MCP Demo Extension

This generic Unity package demonstrates how an external package can contribute SGG Unity MCP command catalog entries without adding top-level MCP tools.

## Install

Copy or add this folder as a local Unity package. For a clean local package install, copy this folder into `Packages/com.example.sgg-mcp-demo` or reference it from the Unity project's `Packages/manifest.json` with a local `file:` entry.

```text
examples/extensions/com.example.sgg-mcp-demo/package.json
```

The Editor assembly has no `SGG.UnityMCP.Editor` reference. The commands are dependency-free manifest handlers, so the package can still compile in projects that do not install the Gateway package.

Local sample checklist:

- `package.json` has no dependencies.
- `Editor/Example.SggMcpDemo.Editor.asmdef` has empty `references` and is Editor-only.
- `Editor/Mcp/mcp.commands.json` declares the same package id as `package.json`.
- Descriptors include explicit risk, idempotency, timeout, schema, and bounded `output_limit` metadata.

## Commands

```text
demo.status
demo.report.generate
```

`demo.status` is a read-only status command that should appear after Unity compiles and the bridge rescans package manifests.

`demo.report.generate` demonstrates richer descriptor metadata:

```text
- execute risk;
- bounded schema and output_limit;
- feature_flag: demo_reports;
- supports_job: true;
- supports_cancel: false;
- tags and capability metadata for grouped discovery.
```

If `demo_reports` is disabled in Gateway config, agents should treat `demo.report.generate` as unavailable. This is intentional: extension packages can advertise feature flags, but they cannot enable themselves or bypass policy.

## Agent Flow

Agents should discover the commands just in time:

```text
1. sgg_unity_projects
2. sgg_unity_describe(project, selector="commands:demo")
3. sgg_unity_describe(project, selector="command:demo.status")
4. sgg_unity_call(project, command="demo.status", args_json="{}")
```

For the job-capable report command, use the existing Gateway job mode only after discovery shows `supports_job:true` and policy allows the execute-risk command:

```json
{
  "project": "game",
  "command": "demo.report.generate",
  "args_json": "{\"max_items\":10,\"dry_run\":true}",
  "mode": "job"
}
```

## Trust And Policy

Package-contributed commands are not trusted by default. The Gateway resolves source package metadata, applies `trusted_packages` and `untrusted_package_default`, and records source/trust metadata in audit events.

This means local copies of this sample remain untrusted by default until the package id is explicitly added to `trusted_packages` in policy. The sample cannot trust itself through manifest metadata.

This package intentionally uses generic `com.example.*` names and contains no private project details.

Run `npm run check:manifest-security` in the Gateway repository to validate manifest security metadata without launching Unity or Unity batchmode. The bad fixtures next to this sample are review fixtures only and use `mcp.commands.bad.json`, so they are not loadable package manifests.
