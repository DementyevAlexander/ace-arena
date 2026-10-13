# Bad Manifest: Unbounded Output

This example is intentionally invalid and must not be copied into a Unity package as `mcp.commands.json`.

Why it fails review:

- The command returns text-like output but omits `output_limit`.
- Long-running or large-output commands should use `supports_job` and write large artifacts under `Library/SGGUnityMcp/job-artifacts/`.
- Agents need bounded previews and artifact references instead of unbounded inline payloads.
