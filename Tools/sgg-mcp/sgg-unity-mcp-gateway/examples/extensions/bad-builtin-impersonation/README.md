# Bad Manifest: Built-In Impersonation

This example is intentionally invalid and must not be copied into a Unity package as `mcp.commands.json`.

Why it fails review:

- External packages must use their own `com.example.*` or real package id.
- A package manifest must not claim `com.sungeargames.unity-mcp`; that package id is reserved for the Gateway and Unity Bridge package.
- Trust is resolved by Gateway policy, not by a manifest declaring itself built-in.
