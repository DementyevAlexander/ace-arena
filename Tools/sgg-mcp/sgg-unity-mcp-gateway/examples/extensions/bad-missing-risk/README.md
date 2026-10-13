# Bad Manifest: Missing Risk

This example is intentionally invalid and must not be copied into a Unity package as `mcp.commands.json`.

Why it fails review:

- The command omits `risk`, so policy cannot decide whether the command is read, write, execute, destructive, or network risk.
- The command omits `idempotency`, so agents cannot reason about retries.
