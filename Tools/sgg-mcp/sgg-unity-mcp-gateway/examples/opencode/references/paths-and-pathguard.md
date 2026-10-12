# Paths And PathGuard

Agent payloads must use Unity-relative paths:

```text
Assets/...
Packages/...
```

Do not pass Windows absolute paths, WSL absolute paths, drive-letter paths, leading slash paths, UNC paths, or `..` traversal in Unity command arguments.

For `package.install`, use `package_path` relative to the target project's `Packages/manifest.json`; do not pass absolute paths, URLs, or git specs unless the command schema explicitly supports them.

PathGuard and command schemas are safety boundaries for Unity-facing payloads. If a command rejects a path, do not work around it with another command, raw filesystem access, or dynamic C#.

Before editing, deleting, moving, or overwriting assets, discover focused commands and use references/risk reports when available.
