[English](../../en/maintainers/opencode-v2-client-follow-up.md) | [Русский](./opencode-v2-client-follow-up.md)

# OpenCode v2: отдельные клиентские доработки

Статус: **proposal, не реализовано**. Владелец разрешил изменения Gateway только в `sgg-unity-mcp-gateway-dev`; OpenCode, его установленный binary, shared service, пользовательские configs и OpenChamber не правим. Рабочее MCP-подключение к OpenCode v2/OpenChamber v2 уже работает по сообщению владельца. Этот документ не означает неисправность подключения и не разрешает client patch.

## Проверенный baseline

- OpenCode `v2.0.25`, exact upstream commit [`b44eea9e204024db2b480a136ece29d790d5f593`](https://github.com/anomalyco/opencode/tree/b44eea9e204024db2b480a136ece29d790d5f593).
- Public plugin API: [`Plugin.define/setup`, hooks и transforms](https://opencode.ai/v2/docs/build/plugins).
- OpenCode v2 и MCP protocol `2026-07-28` — разные версии. Legacy MCP transport остаётся допустимым; отсутствие нового handshake не является ошибкой v2.
- Source proof и contract tests не являются live/visual/human acceptance. OpenCode Web, terminal и OpenChamber проверяются отдельно. Web/PWA не заменяется терминальной панелью.

## Что требует отдельного изменения OpenCode

### 1. Понятные direct MCP карточки в Web

[`packages/session-ui/src/components/basic-tool.tsx:398–415`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/session-ui/src/components/basic-tool.tsx#L398-L415) получает только `tool/status/input` и показывает raw tool name с аргументами. Добавленная серверным плагином metadata сама по себе не становится Web-подписью.

Предлагаемая минимальная доработка:

1. Передавать metadata конкретного tool part в generic renderer через публичный typed prop.
2. Распознавать только фактический versioned `sggPresentation` contract из поставляемого плагина; проверять типы, длины и допустимые lifecycle значения. Для другого/невалидного формата сохранять обычный generic fallback.
3. Показывать bounded safe title/description в running/completed/error состояниях. При наличии валидного safe presentation не создавать вторую подпись из raw `args_json`, путей, кода, token или opaque IDs. Подробности canonical result остаются отдельными, не подменяются подписью.
4. Не менять tool name, input, output, permission checks, approvals или recovery contract. Не патчить session DB и не парсить JavaScript модели.

### 2. Вложенные строки default Code Mode

[`packages/core/src/codemode/tool.ts:24–35,142–175`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/codemode/tool.ts#L24-L35) сохраняет `tool/status/input`, но не presentation каждого inner call. Плагин inner tool не доказывает, что его metadata доходит до строки `execute`.

Предлагаемая отдельная цепочка:

- расширить typed `ExecuteCall`/progress schema optional presentation;
- переносить только validated namespaced presentation **конкретного** вызова из inner progress/result/error в ту же строку;
- использовать стабильную call identity, а не индекс последнего вызова, global current tool или guessed command из текста JavaScript;
- сохранить start-order и параллельную независимость, cancel/error/unknown без ложного completed;
- передавать этот prop в Web и terminal row renderers; сохранять generic fallback и поведение при старой записи без metadata;
- не требовать `codemode:false` ради исправления labels.

Это требует review публичных progress/result contracts самого Code Mode перед реализацией; простой перенос server hook недостаточен.

### 3. Structured MCP errors — optional upstream improvement

[`packages/core/src/tool/mcp.ts:78–85`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/tool/mcp.ts#L78-L85) превращает `isError` в `ToolFailure` с text-only message. Success path `:95–109` сохраняет structured output. Gateway устраняет потерю recovery details своей bounded canonical-text error projection; это не требует client patch.

Если отдельно согласована upstream доработка, сохранить structured error payload и безопасную metadata в typed failure/model/UI round-trip, не объявляя MCP error успешным результатом. После сравнительного proof на direct HTTP, shim и Code Mode можно оценить отказ от compatibility projection. До этого её не удалять.

## Что пока требует acceptance, а не доказанного patch

OpenCode `2.0.25` уже реализует form elicitation. [`packages/core/src/mcp/index.ts:231–294`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/mcp/index.ts#L231-L294) создаёт location-scoped forms с sentinel session `global`. Передаваемый `ai.opencode/sessionID` — только opaque correlation, не authority.

Нужно отдельно проверить видимость формы в активном OpenCode/OpenChamber location, настоящий human approve/false/cancel, expiry, disconnect и два одновременных запроса. Если они работают, патч routing не нужен. Если не работают, сначала exact redacted reproduction, затем отдельно согласуемый typed correlation/UI fix. Не менять Gateway policy, не добавлять self-approval, popup opt-in или автоматический replay.

## Acceptance будущей клиентской итерации

- [ ] Владелец разрешил точный client source repo/scope; установленный OpenCode не monkey-patch-ится.
- [ ] Direct Web labels видны во время и после вызова, включая error/confirmation.
- [ ] Default Code Mode показывает каждую parallel inner operation без смешения строк.
- [ ] Невалидная/неизвестная metadata безопасно возвращает generic renderer.
- [ ] Privacy fixtures: args/code/paths/tokens/raw errors/opaque IDs не попадают в новые подписи.
- [ ] Canonical success/error/recovery и exact operation IDs сохраняются; reconnect не запускает операцию заново.
- [ ] Human forms проверены на фактических transport/client paths, отказ/отмена не исполняют вызов.
- [ ] OpenCode Web, terminal и OpenChamber имеют собственные versioned evidence; metadata-only tests не называются visual pass.

Gateway release может закрыть свой integration slice, но общая P0 UX/visual/human acceptance до этих проверок остаётся открытой.
