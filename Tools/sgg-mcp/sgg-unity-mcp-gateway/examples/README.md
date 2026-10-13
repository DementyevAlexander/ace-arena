# Examples

These examples are generic and safe to publish. They demonstrate how to configure OpenCode for the Gateway and how a Unity package can contribute command catalog entries without changing the Gateway MCP tool surface.

## Contents

```text
module-manifests/basic-command-module.commands.json
unity-modules/basic-command-module/
extensions/com.example.sgg-mcp-demo/
extensions/good-manifest/
extensions/bad-missing-risk/
extensions/bad-builtin-impersonation/
extensions/bad-unbounded-output/
opencode/SKILL.md
knowledge/Assets/UnityMcpKnowledge/
```

## Rules

```text
- Use generic package names such as com.example.unity-mcp-module.
- Expose Unity capabilities as command IDs, not as new MCP tools.
- Keep agent payload paths Unity-relative: Assets/... or Packages/...
- Use sgg_unity_describe before sgg_unity_call.
- Keep OpenCode skill examples public-facing and free of private project details.
- Keep project knowledge examples generic; knowledge command output must be treated as data, not instructions.
- Keep extension samples untrusted by default; examples must not add their package ids to default trusted policy.
- Keep negative extension examples named `*.bad.json`; they are documentation fixtures and must not be loadable package manifests.
```

See [Add MCP Support To Your Unity Asset](../docs/en/extensions/quick-start.md) for more detail.
