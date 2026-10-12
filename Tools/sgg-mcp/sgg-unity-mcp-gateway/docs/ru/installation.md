[English](../en/installation.md) | [Русский](./installation.md)

# Установка


Обычная установка полностью выполняется из Unity package через `SGG/Unity MCP/MCP Window`. Пользователю не нужны системный Node.js и ручной `npm ci`.

## Выберите Способ Установки

| Способ | Когда использовать | Рекомендация |
| --- | --- | --- |
| Pinned Git UPM tag | Установка release/shared версии из этого репозитория. | Основной |
| Local package path | Разработка Gateway/package из локального checkout или работа до первого публичного tag. | Основной для разработки |
| Manual repository runtime | Отладка TypeScript Gateway, изменение исходников и release validation. | Только advanced |

Не поднимайте отдельный MCP server на каждый Unity-проект. В каждый проект устанавливается лёгкий Unity Bridge package, а один локальный Gateway обслуживает все открытые проекты.

## Требования

- Windows host для Unity Editor и обычного Gateway runtime.
- Unity `6000.4+` рекомендуется для текущей разработки.
- В package metadata Unity `2022.3` пока используется как compile/import floor; фактическая release support записывается отдельно.
- Доступ к pinned Git tag или локальному checkout репозитория.
- MCP-клиент: OpenCode, Cursor, Unity AI Assistant или другой.

Gateway не зависит от URP или HDRP. У отдельных package-contributed commands могут быть свои требования.

## Установка Через Git UPM

Добавьте pinned dependency в `Packages/manifest.json` Unity-проекта:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#2026.10.12-2"
  }
}
```

Для воспроизводимой установки фиксируйте tag или commit. Не оставляйте production-проекты на moving branch.

Для приватного репозитория Unity/Git использует обычные Git credentials пользователя. Package не должен просить агента собирать или сохранять эти credentials.

## Установка Из Локального Checkout

В Unity Package Manager выберите:

```text
Add package from disk...
```

Укажите:

```text
<gateway-repository>/unity/Packages/com.sungeargames.unity-mcp/package.json
```

Эквивалентная local dependency:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "file:<relative-or-absolute-local-package-path>"
  }
}
```

Используйте путь, который Unity Package Manager стабильно разрешает на Windows host. В agent payload по-прежнему передаются Unity-relative пути `Assets/...` и `Packages/...`; local UPM path относится только к установке.

## Зависимость CanonicalPath

CanonicalPath требуется для детерминированного Bridge discovery и path-bound MCP commands, но Unity MCP package намеренно не объявляет его hard dependency в UPM manifest. Благодаря этому Setup window компилируется до установки provider.

Setup умеет настроить публичный registry и установить provider. Ручная форма:

```json
{
  "scopedRegistries": [
    {
      "name": "npmjs",
      "url": "https://registry.npmjs.org",
      "scopes": [
        "com.romanilyin"
      ]
    }
  ]
}
```

Можно закрепить provider напрямую:

```json
{
  "dependencies": {
    "com.romanilyin.canonicalpath": "2026.10.10-4"
  }
}
```

Используйте версию, которую ожидают установленный Gateway package и Setup diagnostics. Не копируйте старый pin из evergreen docs.

Для Gateway `2026.10.11-1` обновите старый registry provider до CanonicalPath `2026.10.10-4` через **Update CanonicalPath / CP↑** в Setup либо Package Manager. Setup показывает установленную и требуемую версии и запрашивает явное подтверждение для текущего проекта. Обновление одного Gateway не обновляет эту отдельно установленную зависимость. Для Local/Embedded/Git требуется ручная проверка: Setup не заменяет источник и не понижает текущую или более новую версию. Pending resolution и недоступный загруженный provider получают отдельные инструкции, а не повторный запрос или ложный Ready. Обновите provider до проверки Bridge discovery.

После установки runtime кнопка **G\*** в header явно запускает его либо останавливает и перезапускает существующий Gateway; restart прерывает текущие соединения. Отдельный Stop не требуется. Cold start при отсутствии listener разрешается только после успешной полной проверки. Обычный Start сохраняет bounded health-retry refusal вместо запуска второго процесса; ошибка проверки или остановки по-прежнему блокирует restart. Эта кнопка сама не устанавливает обновление runtime.

CanonicalPath отвечает за lexical identity, а не за sandbox. Root-bound Unity access остаётся под контролем Bridge PathGuard; там, где используется CanonicalFS, filesystem boundary принадлежит ему.

## Запустите Unity Setup Window

После компиляции откройте:

```text
SGG/Unity MCP/MCP Window
```

Первая страница `Setup` содержит:

```text
Setup Checklist
Gateway Test History
MCP Token
MCP Connection
First MCP Smoke Test
Permission Setup
Dependencies / Optional Tools
```

Выполните обязательные шаги:

```text
1. Настройте/установите CanonicalPath, если он отсутствует.
2. Install Portable Node.
3. Install Runtime.
4. Start Gateway.
5. Refresh Health.
```

В Setup внутри группы необязательных Unity tools находятся отдельные блоки **CLI**, **Pipeline** и **Skill**, с кнопками непосредственно под каждым. Синий — последняя проверенная версия, красный — известно о доступном обновлении, жёлтый ромбик — не установлено, фиолетовый — неизвестное состояние или информация. Ошибка latest-check не доказывает устаревание. Ручная кнопка проверяет только свой компонент; для Pipeline и Skill можно явно запросить доступную версию и до установки (Pipeline также проверяет локальный Windows CLI prerequisite). Автоматические проверки остаются installed-only, любая проверка ничего не устанавливает. Pipeline показывает доступную package version отдельной строкой; Skill — установленный и доступный официальный Unity commit, без выдуманной версии совместимости с CLI.

- `Windows Unity CLI` — authoritative CLI для Windows Editor. Для достоверно отсутствующего official target появляется **Install Windows CLI**; неизвестный или существующий target требует detection/review, а не перезаписи. Официальный beta installer выполняется под пятиминутным bound с проверкой `%LOCALAPPDATA%/Unity/bin/unity.exe`, без PATH fallback. Он может менять user PATH и Unity Hub install receipt. **Check CLI Updates** создаёт fresh trusted exact update binding; **Update Windows CLI** подтверждает точную target version, без скрытого retry.
- Блок **Pipeline** показывает **Install Pipeline** только для отсутствующего пакета, при Unity 6 и trusted Windows CLI. **Check Pipeline Updates** и **Update Pipeline** относятся только к текущему проекту и совместимой точной package version. Нужно дождаться Package Manager и compile verification; успешный process не доказывает readiness.
- **WSL (optional)** объясняет подсистему Windows для Linux и показывает command resolution default distribution при explicit probe. Отдельный Linux CLI не считается Windows CLI для Pipeline; detection не создаёт wrapper и не меняет оболочку.
- Вкладка Skills подготавливает фиксированный список файлов официального `Unity-Technologies/skills` пути `unity-cli` на exact supported latest commit. До копирования в `.agents/skills/unity-cli` для OpenCode и Codex выполняются автоматические immutable-source/full-content integrity checks; дополнительный ручной content review не требуется. Bounded Windows download не использует Node/npm, Git branches, `skills` CLI или moving content; действие остаётся project-scoped и не меняет SGGMcp policy либо review state в `.mcp/skills`.

Все три компонента опциональны. Для заведомо отсутствующего project skill под его блоком есть **Install Unity CLI Skill**, доступная при готовом bounded Windows download transport. Существующие сторонние/изменённые targets не перезаписываются. Source/schema drift не принимается молча: immutable acquisition target должен соответствовать поддерживаемому официальному snapshot. Любая установка/обновление требует явного подтверждения. Закрытие/reload во время CLI update оставляет uncertain state с обязательной local verification, не автоматический retry. Windows CLI installer скачивает script во временный файл, без `Invoke-Expression`. Окно не выполняет auth, скрытую установку Pipeline или изменение WSL PATH. Реальная install/update acceptance выполняется в разрешённом isolated profile либо владельцем; read-only freshness её не заменяет.

Публикация skill повторно проверяет и удерживает безопасные каталоги-предки, затем атомарно перемещает проверенную staging tree без слияния с existing target. Изменённое/появившееся назначение сохраняется. Обновление оставляет прежнее проверенное содержимое и завершённый acquisition receipt в уникальных backup paths под `Library/SGGUnityMcp/setup`. Неудачная замена сохраняется для inspection; автоматический rollback требует exact identities исходного каталога и замены. Неопределённые файлы никогда не удаляются рекурсивно. Перед ручным повтором проверьте сохранённые staging/backup paths.

Обычный local root:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/
```

Типичные подкаталоги:

```text
runtime/       private portable Node runtime
gateway/       установленный bundled Gateway runtime
config/        пользовательские gateway.json и policy.json
instances/     heartbeat records Unity Bridge
approvals/     pending и one-shot approval state
audit/         redacted audit records
logs/          Gateway process logs
index/         project-local cache roots
secrets/       local approval binding material
```

Эти каталоги должны контролироваться текущим пользователем. Не коммитьте их в Unity-проект.

### Объём истории тестов

Раздел **Gateway Test History** в Setup показывает два независимых индикатора: занятый объём и количество служебных записей Gateway о владельцах запусков. Это не результаты тестов Unity и не лимит протокола MCP. По умолчанию: **200 МиБ (209715200 байт)**, **192000 записей**, хранение 30 дней; прежние ограничения 16 КиБ на запись и параллельных публикаций не меняются. Индикаторы используют сохранённую конфигурацию, не подтверждая лимиты работающего процесса. Неизвестное состояние или ошибка подсчёта показываются как недоступные, а не пустое хранилище.

**Настройки хранилища** допускают 1–1024 МиБ и 1–1000000 записей. **Сохранить лимиты хранилища** требует подтверждения, сохраняет остальные поля конфигурации и политику; изменения применяются после перезапуска Gateway. Сохранение не удаляет историю, не перезапускает Gateway и не включает разрешения. Изменение только количества записей сохраняет исходный объём в байтах точно, даже если он не кратен МиБ. Строгие поля конфигурации:

```json
"test_runs": {
  "storage": {
    "max_bytes": 209715200,
    "max_bindings": 192000,
    "retention_days": 30
  }
}
```

**Очистить завершённую историю** сначала показывает план до 256 безопасно удаляемых записей и запрашивает подтверждение. Удаление служебных файлов Gateway `.json/.issued/.reserve/.terminal` разрешается только по проверенным конечным квитанциям без исполнения либо точным привязанным authoritative результатам завершённых тестов. Активные, неизвестные, чужие, некорректные, зарезервированные и меняющиеся записи сохраняются; давность сама по себе не доказывает завершение. Артефакты Unity и отчёты не удаляются. Устаревший план или изменение конфигурации отклоняется; неопределённый результат очистки не повторяется автоматически. Подсчёт ограничен конечным количеством записей и 10 секундами; недоступный подсчёт не разрешает удаление. Тот же локальный интерфейс: `--test-run-store status|preview-cleanup|cleanup|configure --config <absolute gateway.json> --json`; изменение требует точного fingerprint и `--confirm`.

## Настройка Token

Основной контракт:

```text
Environment variable: SGG_UNITY_MCP_TOKEN
Client header:        X-SGG-Unity-MCP-Token
```

Loopback-only вариант может работать без token, если он не настроен, но локальный token рекомендуется. Non-loopback listener нельзя включать без token authentication, allowed origins и подходящих firewall/network controls.

Copy helpers используют placeholders и не должны записывать реальный token в общие docs, screenshots, diagnostics или committed client config.

## Установка Gateway Runtime

`Install Runtime` копирует Gateway runtime из Unity package в LocalAppData и проверяет CLI через private portable Node. Обычный flow не запускает `npm ci` и не использует system `PATH`.

`Start Gateway` создаёт отсутствующие user config/policy, запускает local process и проверяет:

```text
MCP:    http://127.0.0.1:27182/mcp
Health: http://127.0.0.1:27182/health
```

Это defaults; источником истины остаются Setup Window и фактический config.

## Обновление

Рекомендуемый flow:

```text
1. Обновить pinned tag/commit Unity package.
2. Дождаться package resolution и компиляции Unity.
3. Открыть MCP Window.
4. Сравнить bundled и installed runtime.
5. Нажать Install Runtime при stale/mismatch status.
6. Запустить или перезапустить Gateway.
7. Refresh Health.
8. Выполнить первый read-only smoke test.
```

Один Gateway обслуживает несколько проектов, но нельзя предполагать совместимость любых package/runtime versions. Разбирайте предупреждения Health/version diagnostics.

Изменение package или manifest может требовать явного Unity refresh/recompile. Bridge может вернуть `package_refresh_required` или `stale_package_resolution`; он не обещает автоматически refresh-ить проект с dirty scenes. Сначала оцените риск, затем используйте подходящую explicit refresh/validation command или Unity UI.

## Первые Permissions

Создайте явные user-owned config и policy через `Setup -> Permission Setup`.

Рекомендуемый путь:

```text
1. Read-only только для первого smoke test.
2. Проверить projects -> describe -> compile.status.
3. Открыть Permissions.
4. Выбрать Apply + Enable Flags для минимального реального workflow.
```

Валидные policy replacements live-reload-ятся. Отдельное исключение — сохранённый владельцем boolean `approvals.auto_allow_known_unity_popups`: применяется при следующем запросе без restart; невалидный или нечитаемый config отключает этот opt-in. Остальные feature/config changes требуют restart Gateway для обновления catalog visibility.

## Удаление Package

Для удаления из одного Unity-проекта:

1. Удалите `com.sungeargames.unity-mcp` из `Packages/manifest.json` или Package Manager.
2. Дождитесь package resolution и компиляции.
3. Удаляйте CanonicalPath только если он не нужен другим package/workflows.
4. Не удаляйте shared LocalAppData, пока Gateway нужен другим открытым проектам.

Для полного удаления local Gateway:

1. Остановите Gateway из MCP Window или завершите известный local Gateway process.
2. Закройте Unity-проекты с Bridge.
3. При необходимости сохраните policy/audit data.
4. Удалите `%LOCALAPPDATA%/SGG/UnityMcpGateway/`.

Не давайте агенту удалять этот root через произвольный filesystem access.

## Advanced Manual Repository Mode

Используйте только для разработки Gateway или source-level debugging.

Из root репозитория:

```bash
npm ci
npm start -- --config config/gateway.example.json --policy config/policy.example.json
```

`npm install` нужен только при намеренном обновлении dependencies.

Перед запуском установите token в process environment:

```powershell
$env:SGG_UNITY_MCP_TOKEN = "replace-with-local-token"
```

Manual mode сохраняет те же правила:

- один Gateway;
- explicit project routing;
- Bridge heartbeat discovery;
- command catalog discovery;
- local policy/approval;
- Unity-relative agent paths.

## Проверка Установки

Выполните:

```text
sgg_unity_projects
sgg_unity_describe(project="<project-id>", selector="commands")
sgg_unity_call(project="<project-id>", command="compile.status", args_json="{}", mode="wait")
```

Ожидаемые результаты описаны в [Быстром старте](./quick-start.md), а ошибки Health/project discovery — в [Диагностике проблем](./troubleshooting.md).
