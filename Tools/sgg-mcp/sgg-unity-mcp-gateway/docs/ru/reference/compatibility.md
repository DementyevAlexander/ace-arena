[English](../../en/reference/compatibility.md) | [Русский](./compatibility.md)

# Compatibility


Compatibility разделяется на package import floor, recommended baseline, client transport и recorded validation. Не объединяйте всё в одно “supported”.

## Unity

| Layer | Contract |
| --- | --- |
| Package metadata floor | Unity `2022.3` — import/compile metadata floor, не полное доказательство workflows. |
| Recommended baseline | Unity `6000.4+`. |
| Active development | Линии `6000.4` и `6000.5`. |
| Release claim | Validated только exact versions с current passed evidence; skipped — `not run`. |

Gateway render-pipeline independent. Individual commands/extensions могут иметь свои requirements.

## Host OS

```text
Unity Editor: Windows
Gateway:      Windows
OpenCode:     Windows или WSL
```

Normal setup/registry используют Windows LocalAppData. Другие hosts не заявляются без отдельной validation.

## OpenCode

| Version | Approval UX |
| --- | --- |
| OpenCode `2.0.25` source baseline | Form elicitation реализован; реальный frontend/human acceptance требует отдельных доказательств. |
| OpenChamber v2 | Владелец подтверждает working MCP connectivity; надписи/формы требуют собственного frontend evidence. |
| Любой capable client | Native MCP form elicitation при advertised и rendered capability; exact accepted call после revalidation at most once. |

Для same-machine WSL recommended Windows stdio shim. Remote HTTP advanced.

## Cursor И Generic Clients

```text
direct Streamable HTTP: modern 2026-07-28 + legacy 2025
direct --stdio:         modern 2026-07-28 + legacy 2025
stdio-shim:             только legacy 2025 к running HTTP Gateway
```

Modern approval использует MRTR. Legacy direct и обновлённый capability-aware shim поддерживают form elicitation. Без advertised/rendered interactive capability используется pending fallback. Shim остаётся legacy-only, не modern MRTR proxy.

## Unity AI Assistant MCP 2.8

Optional client/in-editor workflow через `UserSettings/mcp.json` и stdio shim.

Local SGG не требует:

```text
Unity Assistant chat
Unity Cloud
credits/subscription
official relay
```

Relay optional/off и импортирует allowlisted `official.<toolName>` commands.

## MCP Protocol И SDK

TypeScript SDK `2.0.0` обслуживает stateless protocol revision `2026-07-28` и direct dual-era stdio. Существующий SDK `1.29.0` HTTP implementation остаётся legacy 2025 leg для current clients, включая sessionful Roots и legacy form elicitation.

Endpoint `/mcp` использует официальный era classifier SDK. Modern requests вызывают `server/discover`, несут complete per-request envelope, не создают и не принимают `Mcp-Session-Id` и могут использовать MRTR. Legacy requests сохраняют `initialize` и прежнее session/no-session compatibility behavior. Шесть tools и schema `GatewayResult` одинаковы в обеих eras.

## CanonicalPath

Required для deterministic bridge/path identity, но устанавливается Setup в host project, а не hard UPM dependency. Conditional provider активируется после install. PathGuard separate.

## Version Alignment

Unity package несёт matching Gateway runtime payload. Setup installs и checks version/hash. При разных package versions открытых projects смотрите health, не assume universal compatibility.

## Capability Availability

```text
package missing/version differs
Unity compile failed
feature disabled
policy deny/hide
extension untrusted
capability unavailable
```

Current catalog — final runtime truth.

## Связанные Документы

- [Installation](../installation.md)
- [OpenCode](../connect/opencode.md)
- [Windows и WSL](../connect/windows-wsl.md)
- [Unity AI Assistant](../connect/unity-ai-assistant.md)
- [Validation](../maintainers/validation.md)
