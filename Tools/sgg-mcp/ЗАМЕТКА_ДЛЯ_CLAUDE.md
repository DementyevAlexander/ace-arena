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

## Порядок действий

1. Скопируй папку на прежнее место (см. выше). Если пользователь называет другое место, используй его.
2. Если скиллы нужны Claude Code напрямую, папку скилла (с её `SKILL.md`) кладут в `~/.claude/skills/<имя>/` или в `.claude/skills/<имя>/` проекта. Перед этим спроси пользователя, какие скиллы и куда ставить.
3. `node_modules` и `dist` в копию не входили. Если шлюз нужно запускать, выполни `npm ci` в папке шлюза.

## Важно

- **Unity:** не переноси эту папку внутрь `Assets/` ace-arena. Unity начнёт импортировать пакет, и проект перестанет компилироваться. В `Tools/` она лежит только для переноса.
- **Лицензия:** прежде чем пушить репозиторий ace-arena в открытый доступ, сверься с `LICENSE.md` — разрешает ли лицензия распространять копию. Если сомневаешься, клонируй шлюз из оригинального репозитория, а эту папку убери.
