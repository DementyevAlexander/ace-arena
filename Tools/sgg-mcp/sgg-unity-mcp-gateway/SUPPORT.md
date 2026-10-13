# Support

Russian localization: `SUPPORT.ru.md`.

SGG Unity MCP Gateway is local-first developer tooling. Support is handled through public repository issues when the repository is public and through direct maintainer contact while access is limited.

## Before Asking

- Read `README.md`, `docs/en/quick-start.md`, `docs/en/reference/security.md`, and `docs/en/troubleshooting.md`.
- Use `docs/en/maintainers/manual-checks.md` for local verification.
- Remove tokens, private paths, local usernames, customer/project names, and proprietary asset names from logs or screenshots.

## What To Include

- Gateway version from `package.json`.
- Operating system, Node.js version, Unity version, and whether the setup uses WSL-to-Windows routing.
- The explicit `project` id used for Unity calls, or say that the issue is project discovery.
- The command catalog entry and `args_json` shape if the issue involves `sgg_unity_call`.
- The exact local check commands you ran and their output summary.

## Boundaries

- Do not ask for help exposing Gateway or bridge ports to the internet.
- Do not request bypasses for allow/ask/deny policy or approval gates.
- Do not post vulnerability details publicly; use `SECURITY.md`.
- No paid support, hosted service, response SLA, or official Unity support channel is implied.
