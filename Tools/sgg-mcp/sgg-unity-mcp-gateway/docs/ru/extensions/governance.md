[English](../../en/extensions/governance.md) | [Русский](./governance.md)

# Governance И Trust Расширений


Package-contributed metadata считается untrusted input. Manifest может описать command, но не может сам выдать себе trust, permission, approval или feature visibility.

## Trust States

| Source | Trust label | Default interpretation |
| --- | --- | --- |
| Built-in Gateway/Unity Bridge command | `builtin` | Normal feature/schema/policy/approval/output/audit checks. |
| Command package id из `trusted_packages` | `trusted_package` | После trust resolution применяются normal project rules. |
| Command другого installed package | `untrusted_package` | `untrusted_package_default` gate-ит до broad project allow rules. |

Trust определяется installed Unity package identity и Gateway policy, не self-declared manifest fields.

## Policy Shape

```json
{
  "trusted_packages": [
    "com.example.reviewed-tools"
  ],
  "untrusted_package_default": "ask"
}
```

`untrusted_package_default` — `ask` или `deny`. Explicit deny побеждает. Read-risk command untrusted package не auto-allow-ится только из-за global read default.

## Command Identity

Используйте package-specific lowercase dot notation:

```text
example.status
example.asset.inspect
example.session.start
```

Не impersonate-ите built-in families или другой package. Manifest-security checks содержат negative fixtures.

## Metadata Не Является Authority

Descriptor fields — claims, которые Gateway валидирует и использует консервативно:

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

Они не могут:

```text
сделать package trusted
изменить project policy
approve request
включить feature
обойти PathGuard
добавить top-level MCP tools
открыть arbitrary filesystem/network access
```

## Recommended Trust Lifecycle

```text
1. Установить package как untrusted.
2. Discover descriptors без risky execution.
3. Проверить source, package id, handler assembly, schema, risk, output bounds.
4. Read-only commands запускать под ask или narrow allow.
5. Проверить dry-run и failure paths.
6. После review добавить точный package id в trusted_packages.
7. Risky commands оставить explicit ask-gated.
8. Удалить trust при material ownership/implementation change.
```

Package update сам по себе не доказывает trust continuity.

## UX Requirements

Хороший extension:

- начинается с inventory/status/inspect;
- даёт focused preflight до writes;
- использует `dry_run:true`;
- возвращает `next_valid_actions`;
- различает unsupported и failed;
- различает planned и implemented;
- точно сообщает changed state;
- хранит large output в artifacts;
- не требует Allow All.

## Domain Family Governance

Public docs каждой новой domain family сохраняют единый admission/safety contract:

- discovery через `sgg_unity_describe`, вызов через `sgg_unity_call`; domain commands не становятся top-level MCP tools;
- `diagnostics.problem.triage`, UI/localization/TMP continuation и Addressables/project content delivery остаются discoverable для применимых follow-up families;
- перед risky writes нужен Dry-run plan/preflight, Unity-relative paths проходят PathGuard, а stale writes защищают precondition hashes;
- обязательны Output limits, большие результаты возвращаются через `artifact_ref / artifact_refs`.

Repository sample `examples/extensions/com.example.sgg-mcp-demo/` по умолчанию untrusted, не добавлен в default trusted policy и показывает Trust boundary без выдачи authority.

## Write Safety

```text
[ ] Unity-relative root-bounded paths;
[ ] нет implicit save unrelated scenes/assets;
[ ] overwrite/delete explicit;
[ ] stale-write preconditions;
[ ] dirty state reported;
[ ] нет auto-replay после domain reload uncertainty;
[ ] destructive cleanup имеет dry-run/preview;
[ ] один approval — один exact request.
```

## Audit Metadata

Audit может включать safe metadata:

```text
source
package_id
package_version
Unity-relative manifest_path
trust label
command/catalog revision
risk and policy result
```

Не помещайте raw arguments, credentials, host paths или secrets в descriptions/errors.

## Только Generic Examples

```text
com.example.unity-mcp-module
com.example.validation-tools
example.status
example.asset.inspect
```

Не публикуйте proprietary identifiers в generic docs/fixtures.

## Связанные Документы

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension testing](./testing.md)
- [Policy reference](../reference/policy.md)
