[English](./governance.md) | [Русский](../../ru/extensions/governance.md)

# Extension Governance And Trust


Package-contributed command metadata is untrusted input. A manifest can describe a command, but it cannot grant itself trust, permission, approval, or feature visibility.

## Trust States

| Source | Trust label | Default interpretation |
| --- | --- | --- |
| Built-in Gateway or Unity Bridge command | `builtin` | Normal feature, schema, policy, approval, output-limit, and audit checks. |
| Command from a package id listed in `trusted_packages` | `trusted_package` | Normal project command rules can apply after trust resolution. |
| Command from another installed package | `untrusted_package` | `untrusted_package_default` gates it before broad project allow rules can auto-allow it. |

Trust comes from the installed Unity package identity plus Gateway policy, not from a manifest's self-declared fields.

## Policy Shape

```json
{
  "trusted_packages": [
    "com.example.reviewed-tools"
  ],
  "untrusted_package_default": "ask"
}
```

`untrusted_package_default` should be `ask` or `deny`. Explicit deny still wins. A read-risk command from an untrusted package is not automatically allowed merely because the global read default is allow.

## Command Identity

Use package-specific lowercase dot notation:

```text
example.status
example.asset.inspect
example.session.start
```

Do not impersonate built-in command families or another package. The repository manifest-security checks include negative examples for built-in impersonation.

## Metadata Is Not Authority

These descriptor fields are claims that the Gateway validates and uses conservatively:

```text
risk
idempotency
schema
timeout
output limits
feature flag
job support
package id
handler identity
```

They cannot:

```text
mark the package trusted
change project policy
approve a request
enable a feature
bypass PathGuard
add top-level MCP tools
open arbitrary filesystem/network access
```

## Recommended Trust Lifecycle

```text
1. Install package as untrusted.
2. Discover descriptors without executing risky commands.
3. Review package source, package id, handler assembly, schema, risk, and output bounds.
4. Run read-only commands under ask or a narrow explicit allow.
5. Test dry-run and failure paths.
6. Add the exact package id to trusted_packages only after review.
7. Keep write/execute/destructive/network commands explicitly ask-gated unless there is a strong local reason not to.
8. Remove trust when package ownership or implementation changes materially.
```

Package version changes do not automatically prove trust continuity. Use your normal dependency review process.

## User Experience Requirements

A good extension should make the agent's next step obvious:

- start with inventory/status/inspect commands;
- expose a focused preflight before writes;
- use `dry_run:true` where meaningful;
- return `next_valid_actions` rather than free-form instructions;
- distinguish unsupported from failed;
- distinguish planned from implemented;
- report changed state exactly;
- keep large output artifact-backed;
- avoid requiring Allow All.

## Domain Family Governance

Public docs for every new domain family must preserve the same admission and safety contract:

- discover with `sgg_unity_describe` and invoke through `sgg_unity_call`; domain commands do not become top-level MCP tools;
- keep `diagnostics.problem.triage`, UI/localization/TMP continuation, and Addressables/project content delivery discoverable where those follow-up families apply;
- require a Dry-run plan/preflight before risky writes, enforce Unity-relative paths through PathGuard, and use precondition hashes where stale writes matter;
- declare Output limits and return large results through `artifact_ref / artifact_refs`.

The repository sample at `examples/extensions/com.example.sgg-mcp-demo/` remains untrusted by default, is not listed in default trusted policy, and exists to demonstrate the Trust Boundary without granting authority.

## Write Safety

For write-capable extensions:

```text
[ ] target paths are Unity-relative and root-bounded;
[ ] no implicit save of unrelated scenes/assets;
[ ] overwrite and deletion are explicit;
[ ] preconditions prevent stale writes where practical;
[ ] dirty state is reported;
[ ] domain reload uncertainty does not trigger automatic replay;
[ ] destructive cleanup has dry-run or preview;
[ ] one approval authorizes one exact request only.
```

## Audit Metadata

Audit can include resolved safe governance metadata:

```text
source
package_id
package_version
Unity-relative manifest_path
trust label
command/catalog revision
risk and policy result
```

Do not put raw arguments, credentials, host paths, or package-private secrets into descriptor descriptions or error strings.

## Generic Examples Only

Public examples should use names such as:

```text
com.example.unity-mcp-module
com.example.validation-tools
example.status
example.asset.inspect
```

Do not publish proprietary project/package identifiers in generic documentation or fixtures.

## Related Docs

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension testing](./testing.md)
- [Policy reference](../reference/policy.md)
