# Runtime Debugging

Runtime debugging commands are for Play Mode state. Use them only when the issue depends on runtime behavior.

Prefer bounded snapshots such as `runtime.capture_state` when available. Use frame-based or temporal commands only with explicit frame count, timeout, and output limits.

Rules:

- Discover runtime commands and schemas before use.
- Keep captures focused on the relevant scene, object, component, system, or state variable.
- Do not run unbounded loops or long sampling sessions.
- Use `sgg_unity_job` for long-running runtime operations.
- Do not bypass `confirmation_required` for Play Mode changes or temporal debugging.

If runtime commands are unavailable or `feature_disabled`, explain the limitation and use safer available inspection commands.
