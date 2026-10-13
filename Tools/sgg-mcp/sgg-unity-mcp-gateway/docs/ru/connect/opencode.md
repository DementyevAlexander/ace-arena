[English](../../en/connect/opencode.md) | [Русский](./opencode.md)

# Подключение OpenCode


OpenCode **v2** — основной документированный MCP-клиент для SGG Unity MCP Gateway. Поддержка v1 runtime/plugin больше не требуется. Поддержанную legacy-конфигурацию v2 может нормализовать; setup выпускает native-v2 формат.

## Рекомендуемая Топология

```text
OpenCode на Windows или в WSL
          │
          │ MCP через generated config
          ▼
Gateway на Windows
          │
          ▼
Unity Editor projects на Windows
```

Для OpenCode в WSL на той же машине выбирайте **Windows stdio shim**. Remote HTTP нужен только когда URL-based connection действительно требуется.

## Target Config

MCP Window резолвит config OpenCode независимо от папки Unity-проекта:

- для вложенного Unity-проекта используется root содержащего Git worktree;
- существующий `opencode.json` обновляется без создания второго `opencode.jsonc`;
- при наличии local `.opencode` config Unity Setup показывает conflict/blocker вместо ложного успеха в нижнем слое; CLI может обновить явно выбранный hidden config;
- при отсутствии обоих файлов по умолчанию выбирается `opencode.jsonc` в resolved workspace;
- если существуют оба файла, setup останавливается и показывает оба пути, не выбирая, не объединяя, не переименовывая и не удаляя ни один из них;
- без содержащего Git worktree setup использует папку Unity-проекта только после подтверждения записи.

Setup показывает resolved workspace, путь config, источник и nested/conflict state. Через `Обзор` или ручной ввод можно сохранить содержащий parent workspace в project-scoped локальном `EditorPrefs`; файлы проекта не меняются. `Авто` удаляет override и заново запускает Git/fallback discovery. Отклонённый сохранённый путь остаётся видимым предупреждением, пока его не заменят или не сбросят.

`Create/Update OpenCode Config` патчит generated-поля внутри `mcp.servers.sgg-unity-mcp`. Одна legacy owned entry мигрируется; competing legacy/native entries fail closed. JSONC comments, indentation/line endings, другие servers и owned custom fields сохраняются. При переключении transport удаляются только взаимоисключающие generated-поля: `command`, `url`, generated token header. Duplicate owned keys блокируют запись. Startup/catalog timeout отделён от execution; deliberate disable и больший execution timeout не перезаписываются молча.

Setup обновляет один выбранный документ, не все global/ancestor configs. V2 сначала объединяет direct configs от filesystem root к текущему location, затем все `.opencode` layers. Readiness выбранного файла не доказывает active client load: после reload отдельно проверяйте `/mcps` или `opencode mcp list`.

CLI не создаёт новую hidden SGG entry поверх существующего direct-layer сервера, в том числе deliberate disable. Legacy `experimental.mcp_timeout` поддерживается как migration input: выше него native global members, затем per-server members. Optional field types и legacy-only protocol shim проверяются до readiness.

После подключения OpenCode и первого MCP-вызова обновите Gateway health в MCP Window. `Проверка workspace, сообщённого клиентом` сравнивает выбранный workspace с bounded `roots/list` evidence подключённых сессий и показывает confirmed, ambiguous, mismatch или unavailable state. Health содержит только domain-separated SHA-256 identities, но не raw client paths. Это post-connect проверка, а не bootstrap dependency: для первичной записи config по-прежнему используются Git discovery или явно сохранённый workspace.

## Версии OpenCode

| Версия OpenCode | Approval UX |
| --- | --- |
| OpenCode `2.0.25` source baseline | Реализует form elicitation; реальный frontend показ и human acceptance требуют отдельной проверки. |
| OpenChamber v2 | Подключение MCP работает по сообщению владельца. Надписи/формы требуют собственного frontend evidence, не source OpenCode или version label. |
| Любая версия без advertised elicitation | Использует тот же fallback; Gateway не предполагает наличие UI. |
| Любой client с advertised и rendered form elicitation | Может использовать native one-shot Gateway prompt через transport, который передаёт nested server-to-client requests. |

Версия продукта OpenCode и MCP protocol era — разные вещи. Version label сам по себе не включает approval. Gateway обслуживает legacy MCP 2025 и modern `2026-07-28`; negotiated protocol, advertised capabilities, UI support и transport активного connection определяют legacy form elicitation, modern MRTR или fallback behavior.

## Режим 1: OpenCode На Windows

1. Запустите Unity и Gateway.
2. Откройте `SGG/Unity MCP/MCP Window -> Setup -> MCP Connection`.
3. Выберите режим OpenCode/Windows.
4. Используйте `Create/Update OpenCode Config` или `Copy OpenCode Config`.
5. Проверьте generated change перед сохранением.
6. Перезапустите или reload-ните MCP configuration OpenCode.

Unity window — источник истины для installed runtime path и command line. Лучше использовать generated config, чем вручную писать absolute path.

## Режим 2: OpenCode В WSL Через Windows Stdio Shim

Это рекомендуемый same-machine WSL path.

```text
OpenCode в WSL
  -> powershell.exe
  -> Windows stdio-shim
  -> Windows loopback Gateway
  -> Unity Bridge
```

В Unity window выберите:

```text
Setup -> MCP Connection -> WSL via Windows stdio shim
```

Затем:

```text
Create/Update OpenCode Config
```

или:

```text
Copy OpenCode Config
```

Преимущества:

- Gateway остаётся на `127.0.0.1` Windows;
- не нужен non-loopback Gateway listener;
- не нужен firewall rule для MCP port;
- shim использует installed Windows runtime и token environment;
- generated OpenCode Windows-stdio config включает bounded compatibility projection, поэтому canonical structured result также доступен как compact JSON text;
- shim остаётся legacy MCP path и relay-ит формы только при outer capability `elicitation.form`; иначе используется `confirmation_required` fallback.

Используйте direct HTTP, когда клиент поддерживает MCP `2026-07-28` и modern MRTR. Windows stdio shim намеренно не заявляет modern protocol support без полного continuation proxy.

Если Gateway перезапущен при активном shim connection, текущий explicit call возвращает `mcp_session_not_found` с `request_executed:false` и не replay-ится. Следующий отдельно выполненный call закрывает invalid generation, инициализирует одну новую HTTP session, восстанавливает roots relay и только затем выполняется один раз.

Когда generated PowerShell command вставляется в WSL/bash, заключайте весь PowerShell script в single quotes, чтобы bash не раскрывал PowerShell variables вроде `$env:...`.

## Режим 3: Remote HTTP

Используйте только когда OpenCode должен подключаться по URL.

Пример Windows loopback:

```jsonc
{
  "mcp": {
    "servers": { "sgg-unity-mcp": {
      "type": "remote",
      "url": "http://127.0.0.1:27182/mcp",
      "headers": {
        "X-SGG-Unity-MCP-Token": "{env:SGG_UNITY_MCP_TOKEN}"
      },
      "oauth": false,
      "disabled": false,
      "timeout": { "startup": 30000, "catalog": 30000, "execution": 3900000 }
    } }
  }
}
```

Execution timeout `3900000` ms покрывает самый длинный поддерживаемый часовой wait и overhead; startup/catalog ограничены отдельно. Setup сохраняет большие user execution values; readiness принимает valid v2 defaults/inheritance и absence of `disabled`. Это не Gateway request-body timeout. Gateway использует header auth, не OAuth: оставляйте `oauth:false` и env reference, не token в JSON.

В WSL NAT `127.0.0.1` внутри WSL обычно относится к WSL, а не Windows. Используйте Windows stdio shim либо намеренно настройте listener на Windows host IP.

Для non-loopback Gateway обязательны:

```text
allow_non_loopback = true
настроенный token
непустой allowed_origins
Windows Firewall только для trusted local/WSL access
```

Не открывайте Gateway в недоверенную сеть.

## Проверка Соединения

Попросите OpenCode выполнить:

При default Code Mode найдите `sgg_unity_projects` в catalog и вызовите через `execute`. Отсутствие в provider-native tool list само по себе не ошибка подключения. Per-server `codemode:false` — explicit direct-call alternative, не обязательное исправление надписей.

```text
1. sgg_unity_projects
2. выбрать один live project id
3. sgg_unity_describe(project="<project-id>", selector="commands")
4. sgg_unity_call(project="<project-id>", command="compile.status", args_json="{}", mode="wait")
```

Корректный первый запуск:

- видит нужный проект;
- возвращает command catalog;
- читает compile state;
- не меняет assets;
- не использует implicit active Unity project.

Для вложенного Unity-проекта дополнительно проверьте, что строка проверки reported workspace подтверждает parent workspace OpenCode. При mismatch проверьте открытую OpenCode папку, обновите сохранённый workspace через `Обзор`, reload-ните MCP config, выполните ещё один MCP-вызов и снова обновите Gateway health.

## Статус Native Elicitation

Когда capable client получает policy action `ask`, Gateway может отправить form elicitation внутри исходного вызова. Windows stdio shim теперь advertises/relay-ит формы только при outer capability `elicitation.form`; каждый запрос связан с исходным concurrent call и inner generation. Unsupported/cancelled/expired/disconnected exchange fail closed. Реальный human UI acceptance не заменяется transport tests.

Формы OpenCode v2 location-scoped: отдельно проверяйте показ в активном frontend. Bounded opaque `ai.opencode/sessionID` — только correlation, не authority. Обычное «да» в чате, permission prompt и сохранённая presentation metadata не являются ответом на форму.

Prompt должен показывать:

```text
requesting MCP server
target project
command id
risk class
one-shot approval intent
Accept / Decline / Cancel
```

Prompt не просит raw arguments, tokens, credentials или approval hash.

После **Accept**:

1. Capable MCP client возвращает стандартный elicitation result.
2. Gateway reload-ит policy.
3. Gateway заново разрешает project и точный catalog descriptor.
4. Feature, schema, policy и request binding проверяются повторно.
5. Точный call выполняется не более одного раза.

После **Decline** или **Cancel** команда не выполняется.

Если native elicitation падает, истекает по timeout или не advertised, остаётся `confirmation_required` с fallback workflow. Неудачный prompt не превращает call в allow.

## Fallback Без Elicitation

В trusted local Windows terminal:

```text
sgg-unity-mcp-gateway approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project <project-id>
sgg-unity-mcp-gateway approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
```

После этого агент повторяет исходный `sgg_unity_call` без изменений.

Правила:

- approve сам по себе не выполняет command;
- approval связан с точным request и расходуется один раз;
- агент не может создать или approve-ить свой запрос через MCP;
- `args_hash` — display/audit metadata, а не authorization credential;
- нельзя подтверждать заново собранный другой call.

Unity MCP Window также может показывать pending requests для локального review.

## Временный Auto-Allow Workaround

`approvals.auto_allow_ask: true` — broad trusted-local compatibility workaround, а не основной approval path.

Он:

- влияет только на policy action `ask`;
- не обходит `deny`;
- не обходит feature flags, schema validation, project resolution, Unity-relative path rules и PathGuard;
- помечается в audit как auto-allowed.

Оставляйте выключенным, кроме trusted local session без native elicitation и pending workflow.

`approvals.auto_allow_stale_registry_cleanup_without_elicitation: true` — отдельный более узкий временный default для connections без native elicitation. Он действует только для `registry.stale.cleanup`, только когда active MCP connection не advertises form elicitation, и только после `confirm_cleanup:true`, exact `record_ids`, совпадающего fresh `precondition_hash` и повторной проверки dead/expired records. Он не разрешает Unity commands и не считает chat text approval. Result сообщает mode `temporary_auto_allow_without_native_elicitation`; audit записывает `approval_status:auto_allowed` и `auto_allow_stale_registry_cleanup_without_elicitation:true`. Значение `false` возвращает обязательный one-shot approval для каждого real cleanup.

## Частые Проблемы

### OpenCode не видит tools

- MCP entry должен быть enabled;
- reload-ните OpenCode после правки config;
- проверьте native `mcp.servers`, higher-precedence `.opencode` overrides и default Code Mode catalog перед диагнозом tools missing;
- проверьте, что Gateway запущен;
- проверьте generated command до installed shim/runtime.

### `sgg_unity_projects` возвращает пусто

- оставьте target Unity project открытым;
- дождитесь package compilation;
- проверьте Bridge status в Unity window;
- повторите `sgg_unity_projects`.

### WSL получает connection refused

Используйте Windows stdio shim. Для advanced remote HTTP см. [Windows и WSL](./windows-wsl.md).

### Command отсутствует

Повторите `sgg_unity_describe`. Command может быть скрыт feature flag, отсутствовать в установленном package или временно недоступен после domain reload.

### OpenCode не показывает prompt

- проверьте advertised elicitation у активного MCP connection;
- проверьте, что UI отображает `elicitation/create`, а не только обычные OpenCode `question` events;
- проверьте, что installed Windows shim обновлён до capability-aware relay, а не старой версии;
- прочитайте `confirmation_required` и fallback workflow;
- не включайте `Allow All` только ради обхода client capability.

## Optional v2 Presentation И Клиентская Граница

Bundled plugin использует public-v2 `Plugin.define/setup` и native `plugins`. Safe labels добавляются как metadata, canonical args/results/error messages не меняются. Installer status подтверждает ownership файлов/config, но не active load или видимые надписи. Перед Install/Update/Remove используйте Preview; modified artifacts, conflicts и human disable controls нельзя перезаписывать.

Stock OpenCode `2.0.25` Web cards и inner Code Mode rows не читают этот contract. Gateway не патчит OpenCode/OpenChamber. Предложенные клиентские доработки и visual/human gates вынесены в [отдельный документ](../maintainers/opencode-v2-client-follow-up.md).

Direct MCP errors содержат bounded canonical JSON text вместе с неизменными `structuredContent`/`isError`: этот baseline OpenCode оставляет на error path только текст. Success остаётся structured без общего дублирования payload. Overflow явный, с bounded recovery hints; truncated evidence не является полным результатом.

## Связанные Документы

- [Быстрый старт](../quick-start.md)
- [Windows и WSL](./windows-wsl.md)
- [Права и подтверждения](../guides/permissions.md)
- [Диагностика проблем](../troubleshooting.md)
