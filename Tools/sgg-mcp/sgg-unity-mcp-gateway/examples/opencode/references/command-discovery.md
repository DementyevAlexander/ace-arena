# Command Discovery

Always use explicit project routing. There is no global active Unity project.

Basic flow:

```text
1. sgg_unity_projects
2. Select an explicit project id from the result.
3. sgg_unity_describe(project, selector="commands")
4. sgg_unity_describe(project, selector="command:<id>") when a command schema is needed
5. sgg_unity_call or sgg_unity_batch
6. sgg_unity_job for long-running operations
7. sgg_unity_logs for troubleshooting
```

Never call or invent `set_active`, `set_active_instance`, or `switch_project`.

Use just-in-time discovery. Do not assume built-in, feature-gated, or package-contributed commands exist until the selected project's command catalog reports them.

Useful discovery calls:

```text
sgg_unity_projects
sgg_unity_describe(project, selector="status")
sgg_unity_describe(project, selector="commands")
sgg_unity_describe(project, selector="command:<id>")
```

Additional Unity packages may contribute command catalog entries. They are still called through the same top-level MCP tools and must not be treated as new MCP tools.

For `sgg_unity_call`, pass command-specific arguments through `args_json` as a JSON object encoded as a string:

```json
{
  "project": "game",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

Use `sgg_unity_batch` when several read-only checks can run together, several ordered commands must run in one project, or independent work can be parallelized by project queue. Do not batch destructive chains without separate confirmation or operations where each result changes the plan.

Use `mode:"job"` for command schemas that advertise job support, then poll or cancel with `sgg_unity_job`.

Post-MVP command families can be hidden by feature flags in `config/gateway.example.json`. If a command does not appear in `sgg_unity_describe`, do not call it directly. If a command returns `feature_disabled`, treat that capability as unavailable until the user intentionally enables the matching local feature flag.
