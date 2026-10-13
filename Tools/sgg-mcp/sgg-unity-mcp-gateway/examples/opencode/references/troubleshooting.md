# Troubleshooting

Common Gateway result handling:

```text
catalog_stale            -> call sgg_unity_describe again
project_offline          -> call sgg_unity_projects and inspect status
project_stale            -> call sgg_unity_projects and inspect status
permission_denied        -> do not bypass policy
confirmation_required    -> ask the user or choose a safer command
feature_disabled         -> treat the command as unavailable
absolute_path_rejected   -> use Assets/... or Packages/... paths
path_traversal_rejected  -> remove .. path segments
schema_validation_failed -> fetch the command schema and fix args_json
```

Use `sgg_unity_logs` for bounded gateway, bridge, or Unity log tails when troubleshooting connection, compile, catalog, policy, or command failures.

If OpenCode cannot connect, verify the Gateway endpoint, token header, loopback versus WSL/Windows host addressing, and that Unity has compiled the bridge package.

If a command is missing, do not call it directly. Re-run discovery, check feature flags, and verify whether the Unity bridge package version supports that capability.

Before writing handoff notes that say `MCP skipped` or `MCP unavailable`, re-check the current session: call `sgg_unity_projects`, then call `compile.status` with explicit `project`, `args_json="{}"`, and `mode="wait"` when a project is discoverable. Record the exact project selector, command, error code, and whether the failure is current or historical.
