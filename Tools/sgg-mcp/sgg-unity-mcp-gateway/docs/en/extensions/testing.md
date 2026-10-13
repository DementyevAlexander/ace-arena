[English](./testing.md) | [Русский](../../ru/extensions/testing.md)

# Testing MCP Extensions


Test an extension at three levels: static descriptor checks, Unity discovery/handler tests, and end-to-end agent workflow checks.

## 1. Static Checks

From the Gateway repository:

```bash
npm run check:manifest-security
npm run check:command-descriptors
npm run check:docs
```

Static checks should catch:

- missing risk or idempotency;
- built-in package/command impersonation;
- unbounded text output;
- package/manifest identity mismatch in repository fixtures;
- malformed schemas;
- duplicate command ids;
- stale generated command documentation for built-in descriptors.

They do not prove that Unity compiled or executed your package.

## 2. Consumer Project Fixture

Use a disposable Unity project with:

```text
- the extension package installed;
- SGG Unity MCP package installed;
- the intended Unity version;
- a narrow project policy;
- only required feature flags enabled;
- no proprietary production content unless the test specifically needs it.
```

Test the extension package both with and without the Gateway package installed when the manifest-only integration is meant to be optional.

## 3. Discovery Tests

```text
1. Open Unity and wait for compilation.
2. Call sgg_unity_projects.
3. Call sgg_unity_describe(project, selector="commands").
4. Verify command source, package id, package version, trust label, risk, feature flag, and schema.
5. Call sgg_unity_describe(project, selector="command:<id>").
```

Negative discovery cases:

```text
package missing
Editor assembly failed to compile
manifest in unsupported path
wrong package id
wrong fully qualified handler
unsupported signature
duplicate id
feature disabled
untrusted package denied
```

## 4. Read-Only Command Tests

Verify:

```text
[ ] no asset/scene/settings mutation;
[ ] no hidden import/save/selection/Play Mode side effect;
[ ] deterministic result ordering;
[ ] hard result bounds;
[ ] no token, host path, username, or credential leakage;
[ ] invalid arguments fail before handler where possible;
[ ] result remains useful with empty/missing optional data;
[ ] unsupported state is distinct from internal failure.
```

## 5. Write Command Tests

Use a disposable or backed-up project.

Required scenarios:

```text
dry_run=true produces no mutation
real write matches reviewed dry run
unknown fields rejected
absolute path rejected
path traversal rejected
existing destination blocked without overwrite/precondition
stale precondition rejected
ask gate produces native elicitation or pending fallback
Decline/Cancel produces no execution
accepted exact request executes at most once
catalog/policy/context drift invalidates old approval
uncertain timeout does not auto-repeat risky work
post-write validation reports actual changed state
```

## 6. Job And Artifact Tests

For long operations:

```text
[ ] supports_job is advertised only when implemented;
[ ] job reaches an explicit terminal state;
[ ] polling is bounded;
[ ] cancellation behavior is truthful;
[ ] large output is artifact-backed;
[ ] artifact paths remain under fixed project-local roots;
[ ] artifact read limits are enforced;
[ ] prune/cleanup is dry-run first and destructive-risk;
[ ] domain reload behavior is documented and tested.
```

## 7. Package Trust Tests

Run the same read command in these states:

```text
untrusted_package_default: deny
untrusted_package_default: ask
package listed in trusted_packages with narrow allow
package trusted but command explicitly denied
```

Trust must not bypass feature flags, schema, explicit deny, path validation, approval, or output limits.

## 8. Agent Workflow Eval

Give the agent a natural-language task, not only a raw command id.

Example:

> Read the example package status in project `game`. Do not change project files. First verify the command exists and show the result source/package identity.

Expected flow:

```text
projects -> describe catalog -> describe command -> call -> bounded summary
```

Risky eval:

> Dry-run the example asset generation. Stop before the real write and show the files that would be created.

Expected flow:

```text
discover -> inspect/preflight -> dry-run -> stop for human review
```

## 9. Release Checklist

```text
[ ] clean Unity import on the supported baseline;
[ ] package works without Gateway when optional integration is claimed;
[ ] descriptor and manifest checks pass;
[ ] read command smoke passes;
[ ] risky command approval flow passes;
[ ] no secret/private path in logs or screenshots;
[ ] examples use generic package names;
[ ] docs list real implemented commands only;
[ ] unsupported clients have a fallback path;
[ ] exact checks not run are recorded as not run.
```

## Generic Fixtures

The Gateway repository includes generic examples under `examples/extensions/`:

```text
com.example.sgg-mcp-demo/
good-manifest/
bad-missing-risk/
bad-builtin-impersonation/
bad-unbounded-output/
```

Bad examples use non-loadable filenames and exist only for review/tests.

The complete sample is `examples/extensions/com.example.sgg-mcp-demo/`. Asset authors do not run a second MCP server for it; the package remains untrusted by default until its exact id is reviewed and added to `trusted_packages`.

## Related Docs

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
