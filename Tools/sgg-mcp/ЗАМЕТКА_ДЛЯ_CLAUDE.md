# Заметка для Claude: перенести sgg-mcp на другой компьютер

Эта папка — копия `C:\Repositories\Projects\Unity\sgg-mcp` с основного компьютера. Её положили в репозиторий ace-arena, чтобы перенести на другой компьютер.

**На другом компьютере распакуй её обратно на прежнее место:**

```text
C:\Repositories\Projects\Unity\sgg-mcp\sgg-unity-mcp-gateway\
```

Например, из корня клона ace-arena:

```bash
mkdir -p /c/Repositories/Projects/Unity/sgg-mcp
cp -r Tools/sgg-mcp/sgg-unity-mcp-gateway /c/Repositories/Projects/Unity/sgg-mcp/
```

## Что внутри

`sgg-unity-mcp-gateway` — MCP-шлюз для Unity (Node/TypeScript и Unity-пакет `com.sungeargames.unity-mcp`). Оригинал лежит в `https://github.com/romanilyin/sgg-unity-mcp-gateway`, лицензия — Stinger Royalty-Free EULA 1.0 (`LICENSE.md`).

- `examples/skills/` — скиллы в формате `SKILL.md`:
  - `canonical-path-diagnostics`;
  - `console-triage`;
  - `package-release-check`.
- `examples/opencode/SKILL.md` — ещё один скилл, для opencode.
- `unity/Packages/com.sungeargames.unity-mcp/` — Unity-пакет. Его установщик (`UnityCliProjectSkillInstaller`) сам ставит скиллы в проект.
- Установка и подключение описаны в `README.md`, `README.ru.md` и `docs/ru/installation.md`.

## Как подключить (это шлюз друга пользователя)

Устроено так: в каждый Unity-проект ставится лёгкий пакет-мост. Один локальный Gateway обслуживает все открытые проекты и слушает HTTP MCP на `http://127.0.0.1:27182/mcp`. Claude Code подключается к Gateway, а не к Unity.

1. **Скопировать папку** на прежнее место (см. выше). Если пользователь называет другое место, используй его.
2. **Поставить пакет в Unity-проект** (ace-arena или другой):
   - в Package Manager выбрать «Add package from disk…»;
   - указать `C:\Repositories\Projects\Unity\sgg-mcp\sgg-unity-mcp-gateway\unity\Packages\com.sungeargames.unity-mcp\package.json`.

   Так в `Packages/manifest.json` проекта запишется путь `file:`, привязанный к этому компьютеру. Не коммить его в ace-arena без согласия пользователя.

   Другой вариант — git-зависимость с закреплённым тегом (см. `docs/ru/installation.md`):

   ```json
   "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#<тег>"
   ```
3. **Настроить и запустить Gateway в Unity:** открыть окно `SGG/Unity MCP/MCP Window`, вкладку Setup, и пройти шаги по порядку:
   1. CanonicalPath — установить, если его нет;
   2. Install Portable Node;
   3. Install Runtime;
   4. Start Gateway (кнопка `G*` в заголовке);
   5. Refresh Health.

   Системный Node.js и `npm ci` для этого не нужны. Всё ставится в `%LOCALAPPDATA%/SGG/UnityMcpGateway/` (`config/gateway.json`, по умолчанию порт 27182, токен — в переменной `SGG_UNITY_MCP_TOKEN`).
4. **Токен:** задать его в Setup, в блоке «MCP Token». Без токена Gateway работает только на loopback, но с токеном надёжнее. Сам токен не записывай ни в репозиторий, ни в заметки.
5. **Подключить Claude Code** — одна команда, работает для всех проектов (`-s user`):

   ```bash
   claude mcp add -s user --transport http sgg-unity-mcp http://127.0.0.1:27182/mcp --header "X-SGG-Unity-MCP-Token: <токен из Setup>"
   ```

   Без токена — та же команда без `--header`. Если клиент умеет только stdio, в Setup есть готовый конфиг: «MCP Connection» → `stdio-shim`.
6. **Проверить:**
   - `claude mcp list` показывает `sgg-unity-mcp`;
   - в сессии доступны инструменты `sgg_unity_projects`, `sgg_unity_describe`, `sgg_unity_call`, `sgg_unity_batch`, `sgg_unity_job`, `sgg_unity_logs`;
   - read-only smoke-тест — из `docs/ru/quick-start.md`.

   Unity при этом должна быть открыта, а Gateway запущен.
7. **Скиллы:**
   - Скилл `unity-cli` ставится кнопкой «Install Unity CLI Skill» в Setup, в `.agents/skills/unity-cli` проекта. Эту папку читают OpenCode и Codex.
   - Claude Code читает только `.claude/skills/<имя>/SKILL.md` в проекте или `~/.claude/skills/<имя>/`. Нужный скилл (`examples/skills/*` или `.agents/skills/unity-cli`) скопируй туда целой папкой.
   - Перед копированием спроси пользователя, какие скиллы ставить.

`node_modules` и `dist` в копию не входили. Они нужны только для разработки самого шлюза из исходников (`npm ci`). Для обычного использования хватает шагов выше.

Учти: на основном компьютере пользователя в Claude Code уже был сервер `UnityMCP` (`http://127.0.0.1:8080/mcp`, у проекта `toh`). Это другой Unity MCP, не этот шлюз. Не путай их.

## Важно

- **Unity:** не переноси эту папку внутрь `Assets/` ace-arena. Unity начнёт импортировать пакет, и проект перестанет компилироваться. В `Tools/` она лежит только для переноса.
- **Лицензия:** прежде чем пушить репозиторий ace-arena в открытый доступ, сверься с `LICENSE.md` — разрешает ли лицензия распространять копию. Если сомневаешься, клонируй шлюз из оригинального репозитория, а эту папку убери.
