# Участие в разработке

Английская версия: `CONTRIBUTING.md`.

Перед изменением поведения gateway прочитай `AGENTS.ru.md`, `README.ru.md`, `docs/ru/reference/security.md` и `docs/ru/maintainers/manual-checks.md`.

## Правила публичного репозитория

- Держи docs, examples, issues и PRs generic и user-facing.
- Не добавляй secrets, local tokens, private paths, private repository names, customer/project identifiers или unpublished implementation notes.
- Не описывай Gateway как internet-facing service, hosted runner, official Unity product или enterprise identity/auth system.
- Стабильная top-level MCP tool surface ограничена `sgg_unity_projects`, `sgg_unity_stale_projects`, `sgg_unity_stale_registry_cleanup`, `sgg_unity_describe`, `sgg_unity_call`, `sgg_unity_batch`, `sgg_unity_job` и `sgg_unity_logs`.
- Описывай Unity capabilities как command catalog entries, вызываемые через `sgg_unity_call.command` с `args_json`, а не как новые top-level MCP tools.
- Unity calls должны быть explicit: global active project нет, и каждый Unity command call должен передавать `project`, кроме project discovery.
- В agent guidance используй Unity-relative paths вроде `Assets/...` и `Packages/...`; не проси пользователей передавать arbitrary absolute asset paths.
- Approvals находятся вне MCP. Agents не могут approve собственные команды, а `checkpoint_id` и `plan_id` являются только audit correlation metadata.

## Pull requests

Public contributions обычно должны проходить через pull request. Maintainers могут использовать direct commits для controlled release-preparation work, когда project policy явно это разрешает. Держи PR сфокусированным на одном изменении поведения, документации, релиза или настроек репозитория.

Используй checklist из PR template и указывай local verification command. Если изменение затрагивает Unity command behavior, обновляй tests и command docs в том же PR.

Для installable change создай ровно одну calendar build и завершённый changelog release с user-facing секциями `Added`, `Changed` и `Fixed`. Не используй `Unreleased` и не публикуй внутренний implementation journal в текущем релизе. Изменения только private-repository documentation не требуют version bump. См. `docs/ru/maintainers/versioning.md`.

## CI policy

Public pull requests запускают safe GitHub Actions subset: install dependencies, typecheck, unit tests, docs checks, generated command-doc freshness, version checks, forbidden identifier checks, command descriptor checks, manifest security, changelog и workflow contract checks.

Unity Editor validation, gateway-runtime rebuild verification, release-readiness и release packaging остаются manual/local gates. Не добавляй automatic `push`, `pull_request_target`, `schedule` или `workflow_run` triggers, если project policy не изменилась. Root npm package остается `private`, пока npm publication metadata и package contents не будут проверены вручную.

## Имена веток

Используй lowercase ASCII branch names в форме:

```text
<type>/<short-kebab-topic>
```

Разрешённые types:

```text
feature
fix
docs
l10n
test
refactor
chore
security
release
```

Примеры:

```text
feature/asset-risk-report
fix/project-alias-resolution
docs/security-disclosure
l10n/russian-docs
chore/release-readiness-workflow
```

Правила:

- Используй lowercase letters, digits и hyphens в topic.
- Оставляй ровно один slash между type и topic.
- Используй `l10n` для documentation translation или localization-only changes.
- Не добавляй в имена веток secrets, hostnames, private directory names, customer names или tokens.
- Не используй `main`, `master`, `release` или tag-like names вроде `v1.2.3` для PR branches.

## Локальные проверки

Установи зависимости:

```bash
npm ci
```

Для daily work запусти fast profile, а перед readiness work полный static profile:

```bash
npm run check:fast
npm run check:static
```

`check:all` является compatibility alias для `check:static`; он не запускает и не подтверждает Unity compilation.

Если менялся gateway runtime source или config, запусти:

```bash
npm run build:gateway-runtime
npm run check:gateway-runtime
```

Если Unity установлен и есть test project, запусти optional editor validation с заданными `UNITY_EXE` и `SGG_UNITY_TEST_PROJECT`:

```bash
npm run check:unity-editor
```
