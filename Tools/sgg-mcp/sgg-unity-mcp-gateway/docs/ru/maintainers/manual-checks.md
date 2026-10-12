[English](../../en/maintainers/manual-checks.md) | [Русский](./manual-checks.md)

# Ручные Проверки


Это краткий указатель. Точные команды, схема environment и fail-closed release contract определены в repository scripts.

## Daily Docs

```bash
npm run check:docs
npm run check:command-docs
npm run check:forbidden-identifiers
```

```text
[ ] EN/RU paired.
[ ] Links/images resolve.
[ ] Language switchers.
[ ] Generated inventory fresh.
[ ] No internal identifiers/secrets.
[ ] README/index paths current.
```

## Static

```bash
npm run check:static
```

Unity не запускает.

## Runtime Bundle

```bash
npm run build:gateway-runtime
npm run check:gateway-runtime
npm run check:notices
```

Docs-only replacement не требует rebuild без metadata/runtime change.

## Unity Compile

```bash
npm run check:unity-editor-required
```

Exact disposable project/version. Skip не evidence.

## Unity Matrix

```bash
npm run check:unity-editor-matrix
```

Manual required-mode; missing targets explicit.

## Dedicated MCP Smoke

```bash
npm run check:unity-smoke
```

Exact 6000.4/6000.5 projects/selectors/filters/assemblies/reviewed expected leaf counts. Real MCP, no ask bypass.

Обязательная запись version environment:

```text
<!-- sgg-current-version-claim:manual.ru.smoke.environment -->
SGG_UNITY_SMOKE_PACKAGE_VERSION             точная текущая package version (2026.10.12-2)
```

Начало команды:

```bash
<!-- sgg-current-version-claim:manual.ru.smoke.command -->
SGG_UNITY_SMOKE_PACKAGE_VERSION='2026.10.12-2' \
npm run check:unity-smoke
```

## Capable-Client Approval Smoke

```text
1. Подключить client build, который действительно advertises и renders MCP form elicitation, через transport с nested requests.
2. projects + compile.status.
3. Ask-gated harmless/dry-run command.
4. Native form с server identity/Accept/Decline/Cancel.
5. Decline -> no execution.
6. Accept -> exact call at most once.
7. Context drift -> old acceptance invalid.
8. Проверить fallback без capability.
```

Не использовать `auto_allow_ask`.

## Extension Smoke

```text
install generic package
compile with/without Gateway
inspect descriptor/trust/source
read command
dry-run risky
untrusted/trusted policy
manifest check
```

## FTUE Smoke

```text
[ ] Value clear in 30 sec.
[ ] Install works.
[ ] Portable runtime no PATH change.
[ ] Health green.
[ ] Secret-safe client config.
[ ] projects/describe/compile.status.
[ ] WSL stdio when tested.
[ ] On a clean Windows profile, Unity CLI detection reports missing, install confirmation names the beta Unity CDN source and user/PATH effects, and cancel performs no write.
[ ] Confirmed Windows CLI installation reaches `%LOCALAPPDATA%/Unity/bin/unity.exe`, validates that exact executable, and does not accept an older `unity.exe` from `PATH` as install success.
[ ] Closing MCP Window or triggering assembly reload during Windows CLI installation cancels the bounded process; a fresh detect is required before any repeat.
[ ] CLI, Pipeline и Skill имеют собственные install/check/update buttons. Missing — жёлтый, latest checked — синий, known update — красный, unknown/failed check — фиолетовый; unknown никогда не разрешает Install.
[ ] Ручные кнопки Pipeline/Skill показывают available package/commit version до установки. Проверка другого блока сохраняет same-context display metadata без фоновых queries или update authority.
[ ] Skill показывает installed/available official commits и использует supported Unity latest snapshot без manual owner content review; версия совместимости CLI не выдумывается из commit/prose.
[ ] Установка Skill отклоняет появившийся file/directory target или изменённого предка, без слияния/удаления чужих файлов. Обновление сохраняет предыдущий backup и неудачную замену, не выполняет rollback в чужой target.
[ ] CLI beta install/update проверяется в отдельном disposable Windows user/HKCU/profile или VM: UNITY_CLI_HOME не изолирует PATH/Hub receipt effects. Проверить exact post-action version, не менять рабочие установки.
[ ] WSL explanation/explicit diagnostics различают wrapper на exact Windows CLI и отдельную Linux CLI/environment.
[ ] Troubleshooting recovers failure.
[ ] Extension quick start discoverable.
```

## Release Gate

```bash
npm run generate:release-evidence-template
npm run check:release
```

Checker verifies evidence, не выполняет tests.

## Связанные Документы

- [Validation](./validation.md)
- [Versioning](./versioning.md)
- [Troubleshooting](../troubleshooting.md)
