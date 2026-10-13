[English](../../en/connect/windows-wsl.md) | [Русский](./windows-wsl.md)

# Windows И WSL


Рекомендуемая same-machine topology:

```text
OpenCode / другой client в WSL
          │
          ▼
Gateway на Windows
          │
          ▼
Unity Editor на Windows
```

Gateway на Windows держит Unity Bridge discovery, loopback Bridge calls, LocalAppData state и Unity paths в одном OS context.

## Unity CLI Не Является Общим

Windows `unity.exe` и Linux binary `unity`, установленный внутри WSL, являются разными установками. У них могут различаться реестр Editor, credentials, configuration и состояние `PATH`.

Для Windows Unity Editor:

- Windows `unity.exe` является authoritative для `unity pipeline install`, `unity pipeline list` и `unity command`.
- Unity MCP Window отдельно проверяет Windows и command resolution в default WSL distribution.
- Если OpenCode/Codex работает в WSL, рекомендуемое agent environment использует проверенный wrapper, где `unity` выполняет `unity.exe "$@"`.
- Самостоятельный Linux CLI остаётся допустимым для Linux workflows, но отображается как `Separate Linux CLI` и не выполняет prerequisite Windows Pipeline.

После явного подтверждения окно может установить фиксированный wrapper в `~/.local/bin/unity` или скопировать инструкции для ручной проверки. Оно никогда не перезаписывает существующий файл/symlink и не меняет shell profiles автоматически.

## Совместимость CLI Beta

Unity CLI `1.0.0-beta.6` различает завершившийся `unity test` с упавшими тестами (exit `8`, `TESTS_FAILED`) и run без verdict из-за infrastructure, timeout, crash, compile или license failure (exit `6`, например `TEST_RUN_ERROR` или `TEST_TIMED_OUT`). Не повторяйте exit 8 как infrastructure failure.

Pipeline `0.5.0-exp.1` может вернуть typed `503 Editor is still settling` во время cold `unity run --command`, даже если status уже показывает ready. Retry допустим только для read/no-op command с authoritative `command_dispatched:false` и только внутри одного fixed startup deadline. Если main-thread gate остаётся закрытым, завершайте с `unity_cli_cold_main_thread_gate_stuck`; не poll-ьте бесконечно и не replay-те write или uncertain delivery. `-automated` не является поддерживаемым workaround. Для critical CI используйте явно управляемый persistent Editor и warm `unity command`, пока cold path не получит versioned proof.

## Рекомендуется: Windows Stdio Shim

Для OpenCode в WSL:

```text
SGG/Unity MCP/MCP Window
  -> Setup
  -> MCP Connection
  -> WSL via Windows stdio shim
```

Затем создайте/обновите `opencode.jsonc` из окна.

Путь:

```text
OpenCode в WSL
  -> powershell.exe
  -> installed Windows stdio-shim
  -> http://127.0.0.1:27182/mcp на Windows
```

Non-loopback interface не требуется.

## WSL NAT Mode

В WSL NAT:

```text
127.0.0.1 внутри WSL != Windows loopback
```

Прямой WSL HTTP к `http://127.0.0.1:27182/mcp` может не работать, даже когда Windows PowerShell видит Gateway.

Предпочтительные варианты:

1. Windows stdio shim;
2. намеренно настроенный remote HTTP на Windows host IP;
3. mirrored networking только после проверки localhost из реального client process.

## WSL Mirrored Networking

В mirrored mode localhost forwarding может работать:

```text
http://127.0.0.1:27182/mcp
```

Не считайте это гарантией. Проверяйте из MCP client и оставляйте stdio shim как предсказуемый fallback.

## Advanced Remote HTTP

Remote URL:

```text
http://<windows-host-ip>:27182/mcp
```

Обязательные настройки:

```text
mcp.allow_non_loopback = true
mcp.token_env настроен
mcp.allowed_origins непустой
Windows Firewall только для trusted local/WSL access
```

Не bind-ьте Gateway ко всем interfaces ради удобства. Это не internet-facing service.

## Firewall Boundary

Firewall rule может требоваться только Gateway endpoint. Unity Bridge listeners остаются local Windows и вызываются Gateway.

Не открывайте недоверенным users/machines:

```text
Gateway MCP port
Bridge ports
approval directories
audit directories
LocalAppData runtime roots
```

## Правила Путей

Agent payloads используют:

```text
Assets/...
Packages/...
```

Не передавайте:

```text
C:\project\Assets\...
/mnt/c/project/Assets/...
/home/user/project/...
../Assets/...
```

если конкретное installation/config field явно не принимает host path.

CanonicalPath помогает идентифицировать эквивалентные Windows/WSL roots, но identity layer не даёт произвольный filesystem access.

## PowerShell Из Bash

При `powershell.exe -Command` из WSL/bash заключайте весь PowerShell script в single quotes. Иначе bash раскроет `$env:...`, `$root` и другие variables до запуска PowerShell.

Концептуальная безопасная форма:

```bash
powershell.exe -NoProfile -NonInteractive -Command '$root = Join-Path $env:LOCALAPPDATA "SGG\UnityMcpGateway"; ...'
```

Используйте точный command, generated Unity window.

## Диагностика Соединения

На Windows:

```text
Setup -> Refresh Health
```

Из MCP client:

```text
sgg_unity_projects
```

Интерпретация:

| Результат | Значение |
| --- | --- |
| Gateway Health падает в Unity | Проблема Windows Gateway process/config/runtime. |
| Health работает, stdio shim падает | Generated command, token environment или shim startup. |
| MCP подключён, проектов нет | Bridge registry/heartbeat или compile state Unity. |
| Windows HTTP работает, WSL HTTP refuses | Namespace/NAT; используйте stdio shim или host-IP listener. |
| Project есть, command падает | Читайте typed command/policy/schema error; transport уже работает. |

CLI `doctor --json` и offline `report-problem --include-doctor` дают bounded redacted diagnostics.

## Связанные Документы

- [OpenCode](./opencode.md)
- [Установка](../installation.md)
- [Безопасность](../reference/security.md)
- [Диагностика проблем](../troubleshooting.md)
