[English](../../en/guides/permissions.md) | [Русский](./permissions.md)

# Права И Подтверждения


Доступ Gateway намеренно разделён на три независимых слоя.

<p align="center">
  <img src="../../assets/readme/permissions-flow.svg" alt="Feature flags, policy и approval flow" width="880">
</p>

## Три Слоя

### 1. Feature Flags

Feature flag делает command family видимой в catalog выбранного проекта.

```text
feature disabled -> command не discoverable
feature enabled  -> command может быть рассмотрена policy
```

Включение feature не grant-ит permission.

### 2. Policy

Policy разрешает каждую command как:

```text
allow
ask
deny
```

Она учитывает:

- command-specific allow/ask/deny patterns;
- risk defaults;
- project id;
- package trust для extension commands.

Policy change не включает hidden feature family.

### 3. Approval

Approval используется только при current policy decision `ask`.

Он связан с одним точным resolved request. Это не reusable permission, не project-wide allow и не args hash, который выбирает агент.

## Рекомендуемые Profiles

| Profile | Для чего | Типичное поведение |
| --- | --- | --- |
| Read-only | Первый smoke test и basic status. | Compile, logs, package reads, loaded scenes, safe discovery. Для нормальной работы слишком ограничен. |
| Inspect | Понимание scenes, prefabs, assets, scripts, APIs, docs и read-only profiler state. | Read-heavy features включены; mutation заблокирован или ask-gated. |
| Safe Writes | Reviewed local scene/object/asset/script edits. | Read preflight разрешён; точные writes остаются `ask`. |
| Test Validation | Bounded Unity tests и evidence. | Status/list reads разрешены; refresh/validation/test execution остаются `ask`. |
| Package Management | Package reads и контролируемые install/update/remove. | Reads разрешены; write/network operations — `ask` или deny. |
| Runtime Debug | Bounded Play Mode snapshots и temporal debugging. | Reads разрешены; state changes и long sampling gated. |
| Allow All | Только fully trusted local project. | Убирает project policy ask gates для visible built-in/trusted commands. Не использовать по умолчанию. |

`Permissions -> Apply + Enable Flags` обновляет profile и matching feature flags. Изменение features и остальных config settings требует restart Gateway; валидный policy replacement reload-ится между calls. В runtime с поддержкой live popup settings единственное исключение для Gateway config — сохранённый владельцем boolean `approvals.auto_allow_known_unity_popups`, описанный ниже.

## Безопасные Risk Defaults

```text
read         allow
write        ask
execute      deny
destructive  ask
network      deny
```

Project-specific rules могут быть уже. Если workflow явно не нужен, безопаснее deny.

## Native MCP Elicitation

Interactive approval имеет две protocol forms. Modern MCP `2026-07-28` использует multi-round-trip result `input_required` и signed opaque `requestState`; legacy MCP 2025 использует form elicitation, когда capability advertised. Версия продукта OpenCode сама по себе не определяет protocol era.

Flow:

```text
1. Agent вызывает ask-gated command.
2. Gateway разрешает project, descriptor, feature, schema и policy.
3. Gateway записывает bounded pending identity и запрашивает input через modern MRTR или legacy form elicitation.
4. OpenCode показывает Accept / Decline / Cancel.
5. После Accept Gateway reload-ит и заново валидирует request context.
6. Точный call выполняется не более одного раза.
```

Форма использует простой approval field и safe metadata. Она не должна просить tokens, credentials, raw arguments, local paths или secret binding material.

Modern `requestState` содержит только bounded correlation metadata и HMAC-верифицируется до handler re-entry. Это не доказательство user consent; execution разрешает только validated `accept` с последующим one-shot claim существующей pending generation.

### Чего Не Делает Accept

Accept не:

- меняет project policy;
- подтверждает будущие calls;
- включает feature flags;
- авторизует другой project;
- авторизует изменённую command или arguments;
- отключает audit;
- превращает deny в allow.

### Context Drift

Если пока prompt открыт изменились project instance, catalog descriptor, policy, feature state, schema или exact binding, accepted generation инвалидируется. Gateway возвращает current safe error или новый approval request, а не оживляет старый.

## Pending-Approval Fallback

Clients без MCP elicitation получают:

```text
status: error
error.code: confirmation_required
details.approval_request
details.approval_workflow
```

Просмотр из trusted local terminal:

```text
sgg-unity-mcp-gateway approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project <project-id>
```

Approve одного pending id:

```text
sgg-unity-mcp-gateway approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
```

После этого исходный MCP call повторяется без изменений.

Approve создаёт one-shot authorization record, но не выполняет command. Нужен отдельный explicit retry.

## Unity Window

Откройте:

```text
SGG/Unity MCP/MCP Window -> Permissions
```

Страница умеет:

- показывать current `Project Id`;
- применять profiles;
- включать matching feature flags;
- отображать allow/ask/deny rules проекта;
- preview-ить catalog по policy bucket;
- редактировать command patterns только current project после explicit review;
- показывать pending approval без MCP self-approval tool.

Feature flags, risk defaults, approvals и execution остаются разными concerns.

## Policy Precedence

Практический порядок:

```text
project и command должны resolve-иться
feature должен быть enabled
arguments должны match-ить schema
package trust должен разрешать рассмотрение
explicit deny побеждает
затем explicit ask/allow patterns
risk default закрывает остаток
ask требует одного exact approval
```

`policy.explain` показывает решение, если feature и read permission включены. Он не выполняет target command и не потребляет approval.

## Trust Extension Packages

Package не может объявить себя trusted.

| Source | Trust state | Эффект |
| --- | --- | --- |
| Built-in Gateway/Bridge command | `builtin` | Обычные feature/policy/approval checks. |
| Command configured trusted package id | `trusted_package` | Могут применяться normal project rules. |
| Command другого package | `untrusted_package` | `untrusted_package_default` gate-ит её до project allow patterns. |

Для untrusted default используйте `ask` или `deny`.

## Временный `auto_allow_ask`

`approvals.auto_allow_ask: true` существует для trusted local compatibility sessions.

Он не обходит:

```text
deny
feature flags
schema validation
project resolution
package trust
Unity-relative path checks
PathGuard
```

Каждый auto-allowed call отмечается в audit.

Не показывайте это как основной path. Выключайте сразу, когда native elicitation или pending workflow становятся practical.

Отдельный default `approvals.auto_allow_stale_registry_cleanup_without_elicitation` намеренно уже: он действует только для exact precondition-bound cleanup dead records на connection без form elicitation. Он не меняет policy Unity commands, а capable connections остаются ask-gated.

## Ответы на известные окна без нового вопроса

В MCP Window → Permissions → One-shot Approvals есть галочка **Не спрашивать снова для известных окон Unity** (`approvals.auto_allow_known_unity_popups`). По умолчанию она **выключена**. Включение показывает предупреждение, которое владелец может отменить. В Gateway runtime с поддержкой file-backed live popup settings сохранённое включение/отключение действует со следующего запроса **без перезапуска**; перед отправкой реального built-in ответа настройка проверяется снова, включая отзыв разрешения. Сохранение галочки не отправляет ответ и не повторяет заблокированный call. `auto_allow_ask`, профили и правила policy не меняются.

Из исходного config file перечитывается только этот boolean. Gateway требует стабильное чтение размером не более 1 MiB, строгую валидацию полного config и отказ при duplicate JSON keys. Отсутствующий, недоступный, невалидный, слишком большой или нестабильный config выключает opt-in fail-closed; прежнее `true` не сохраняется. Все остальные feature flags, approval settings/key/directories, token и network settings остаются startup-bound и требуют restart для изменения.

Владелец разрешает агенту выбирать поддерживаемые ответы: Save/Don't Save/Cancel, Reload/Ignore, Continue для понижения версии проекта, Close для scoped-registry, Enter Safe Mode и **Keep Backups** в точном окне **Scene Backup Detected**. Это может отменить несохранённые изменения или изменить состояние проекта. Галочка убирает только дополнительный вопрос: policy `deny`, features/trust/schema, точная проверка владельца/процесса/окна, dry-run, confirm arguments и single-use/no-replay остаются. Неизвестные окна, generic recovery commands, закрытие crash reporter/завершение процессов и обычные writes/builds не входят в разрешение. Отправленное сообщение не доказывает закрытие попапа или восстановление проекта.

`diagnostics.unity_scene_backup.keep_backups` по умолчанию выполняет dry-run. Используйте текущую привязку PID/process-start/canonical-root и полученный одноразовый план со сроком 60 секунд; реальный ответ требует `dry_run:false`, `confirm_keep_backups:true` и точный `precondition_hash`. Перезапущенный Editor до первой heartbeat допускается только после свежего полного process scan с единственным владельцем известного корня проекта. **Delete** и Yes/No старого **Recovering Scene Backups** не поддерживаются. Неопределённая native attempt запрещает повторный ответ тому же попапу; заблокированный compile/test/package вызов не повторяется.

`diagnostics.status` и `diagnostics.unity_modal.status` возвращают действующее `popup_response_approval` и exact supported command IDs. Проверяйте `enabled`, а не только сохранённую галочку. File-backed contexts могут дополнительно возвращать `application_mode:live_on_request`, `source_status`, `saved_enabled` и `restart_required:false`; старые runtime могут не иметь этих полей. Когда действующее состояние enabled и все точные проверки command пройдены, агент может отправить поддерживаемый ответ без нового approval UI; `confirmation_required` по-прежнему требует обычного human workflow. Audit отмечает `approval_status:auto_allowed` и `auto_allow_known_unity_popups:true`. Агент не включает настройку сам и не заменяет отказ более широкой командой.

## Частые Ошибки

| Error | Значение | Что делать |
| --- | --- | --- |
| `feature_disabled` | Command family hidden. | Включить только нужный feature, restart Gateway, describe снова. |
| `permission_denied` | Current policy deny. | Не обходить более широкой или замаскированной command. Policy меняется вне MCP после review. |
| `confirmation_required` | Current policy `ask`. | Native elicitation или review exact pending request. |
| `policy_degraded` | Replacement policy invalid/missing/unstable. | Восстановить valid policy. Risky calls fail-closed. |
| `schema_validation_failed` | Arguments не совпадают с descriptor. | Выполнить `schema_lookup`, прочитать descriptor и собрать новый corrected call. |

## Рекомендуемое Расширение Прав

```text
1. Read-only smoke test.
2. Inspect для semantic reads.
3. Добавить только task-specific profile.
4. Writes, tests, package mutation, network и destructive оставить ask-gated.
5. Использовать risk reports, dry runs и checkpoints.
6. Смотреть audit в sensitive sessions.
7. После задачи убрать лишние flags и broad rules.
```

## Связанные Документы

- [Policy reference](../reference/policy.md)
- [Безопасность](../reference/security.md)
- [OpenCode](../connect/opencode.md)
- [Основные сценарии](./workflows.md)
