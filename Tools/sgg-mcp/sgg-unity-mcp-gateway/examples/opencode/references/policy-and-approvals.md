# Policy And Approvals

Gateway command permissions are controlled by a policy file. The public `config/policy.example.json` is a template; local setup can create user-editable files under `%LOCALAPPDATA%/SGG/UnityMcpGateway/config/`:

```text
gateway.json
policy.json
```

Commands declare one of these risk levels:

```text
read
write
execute
network
```

Suggested defaults are:

```text
read         allow
write        ask
execute      deny
destructive  ask
network      deny
```

Per-project policy can allow, ask, or deny command ids and glob-like command families. Do not silently grant permissions to the agent. Always identify the project with `sgg_unity_projects`, inspect the command catalog, propose the policy delta, and ask before applying it.

Preferred user-driven path:

```text
1. Open Unity Editor for the target project.
2. Open the Unity MCP setup UI.
3. Go to Permissions.
4. Verify Project Id.
5. Apply a permission profile such as Readonly, Inspect, Package Management, Safe Writes, or Runtime Debug.
6. Restart the Gateway from the same window if the local setup flow requires it.
7. Run sgg_unity_projects and sgg_unity_describe again.
```

Agent-assisted local CLI path:

```bash
node gateway-cli.mjs policy profiles
node gateway-cli.mjs policy apply-profile \
  --policy "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/policy.json" \
  --from-policy "config/policy.example.json" \
  --project game \
  --profile readonly \
  --dry-run
```

After showing the dry-run result to the user, repeat without `--dry-run` only if the user approves. If policy is loaded only at Gateway startup in the deployed version, restart the Gateway after policy changes.

Do not bypass `permission_denied` or `confirmation_required`. Ask the user to approve the action, adjust policy, or choose a safer command.

For `confirmation_required`, use the pending workflow outside MCP:

```bash
node gateway-cli.mjs approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project game
node gateway-cli.mjs approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
# Retry the original MCP call unchanged.
```

Pending/list output contains safe metadata only. `args_hash` is an informational redacted display/audit value, never authorization. Internal binding/key/request HMAC and project-root/instance/catalog/descriptor identities are never exposed. Direct `approval create`, arbitrary `--args-hash`, and MCP self-approval are disabled.

For MCP clients that support native form elicitation, acceptance remains internal to that original call. It claims only the displayed pending generation after policy, routing, catalog, descriptor, feature, and schema revalidation; it never creates a shared active approval. Context or effective-policy drift invalidates that generation, and concurrent losers do not execute. The CLI fallback above intentionally remains a separate pending-to-active one-shot workflow.

Package-contributed commands require the same command discovery and policy gates as built-in commands. A package manifest cannot self-declare trust; trust comes from local policy and Gateway governance.
