# Safe Writes

Use safe write workflows only when write commands are present in the selected project's command catalog and policy allows or asks for them.

Before writing:

```text
1. Confirm the explicit project id.
2. Discover the exact command and schema.
3. Inspect the relevant scene, prefab, asset, or package state.
4. Check references or risk reports when available; prefer `gameobject.write_risk_report` before destructive GameObject operations.
5. Use Unity-relative paths only.
6. Ask the user before policy changes or confirmation-gated actions.
```

Do not chain destructive operations in a batch unless each step is separately understood and approved. Do not use dynamic C# to bypass missing write commands, policy denials, PathGuard, or approval requirements.

After writing, run the smallest relevant validation, such as compile status, focused asset inspection, scene/prefab inspection, logs, or a command-specific verification step advertised by the catalog.
