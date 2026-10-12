# Поддержка

Английская версия: `SUPPORT.md`.

SGG Unity MCP Gateway — local-first developer tooling. Поддержка идёт через public repository issues, когда repository публичный, и через прямой контакт с maintainer, пока доступ ограничен.

## Перед вопросом

- Прочитай `README.ru.md`, `docs/ru/quick-start.md`, `docs/ru/reference/security.md` и `docs/ru/troubleshooting.md`.
- Используй `docs/ru/maintainers/manual-checks.md` для local verification.
- Удали tokens, private paths, local usernames, customer/project names и proprietary asset names из logs или screenshots.

## Что указать

- Gateway version из `package.json`.
- Operating system, Node.js version, Unity version и используется ли WSL-to-Windows routing.
- Явный `project` id для Unity calls или отметку, что проблема в project discovery.
- Command catalog entry и форму `args_json`, если проблема связана с `sgg_unity_call`.
- Exact local check commands, которые запускались, и краткий output summary.

## Границы

- Не проси помочь открыть Gateway или bridge ports в интернет.
- Не запрашивай bypass для allow/ask/deny policy или approval gates.
- Не публикуй vulnerability details открыто; используй `SECURITY.md`.
- Paid support, hosted service, response SLA или official Unity support channel не подразумеваются.
