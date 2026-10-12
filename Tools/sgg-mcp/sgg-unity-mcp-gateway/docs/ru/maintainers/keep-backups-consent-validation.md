[English](../../en/maintainers/keep-backups-consent-validation.md) | [Русский](./keep-backups-consent-validation.md)

# Keep Backups — проверка чтения сохранённого согласия

Gateway build `2026.10.11-1`, 2026-10-11. Это узкое исправление Gateway source/artifact, не physical popup acceptance и не полный Unity release-matrix GO.

## Наблюдаемый отказ и исправление

`problem_20261011T152654_95d237dcaa08` сообщает exact Keep Backups dry-run, после которого non-dry-run отказал с `confirmation_required`. Отчёт не доказывает нажатия. Свежая Gateway diagnostics показала known-popup opt-in disabled с `source_status: unstable`. Read-only probe использовал exact running Gateway executable, native Windows Node `22.16.0` и его exact config pathname: сохранённый opt-in был `true`, path/descriptor inode, size, mtime и ctime совпадали и оставались стабильными, но `lstat.dev=0`, а `fstat.dev` — nonzero volume id. Старый reader отклонял эту native асимметрию. Это объясняет воспроизведённый текущий consent-read defect; не атрибутирует все исторические PR219 refusals и не доказывает состояние config в момент прежнего отчёта.

Reader теперь использует существующий platform-specific `sameFileObjectIdentity`. Nonzero inode, metadata, bounded contents, regular-file checks и строгий config parsing сохранены. Path-to-path и handle-to-handle device ids обязаны независимо оставаться стабильными. Подмена, изменение, uncertain identity и invalid/missing config отзывают согласие; cached last-known-true fallback отсутствует. Никакие setting, feature, policy, approval TTL, response allowlist или popup interaction authority не включены и не расширены.

## Выполненные проверки

- Независимый focused source review: **GO** до финальных полных suites. Дополнительный optional regression для потери pathname device id добавлен и прошёл final targeted/full Node.
- Final targeted Node: **187/187**, четыре файла (`known-unity-popup-settings`, `scene-backup-dispatcher`, `known-popup-live-responses`, `gateway-context`). Включены live OFF/ON/OFF, оба направления Windows device asymmetry, настоящие platform identity rules, non-Windows refusal, zero inode, изменения volume/inode/metadata и прежние revocation/pre-send/no-replay consumers.
- Native emitted fixture `tests/fixtures/known-popup-settings-native.ts`, bundled из fixed source с отдельно bundled pre-fix manager из `92b55ce5`: exact native Windows Node `22.16.0` воспроизвёл path/handle device asymmetry и старый false/unstable result. Fixed reader прошёл ON/OFF/ON; malformed, duplicate-key, non-boolean, oversized и missing config controls дали fail-closed. Fixture использовал только fresh private temp config и non-secret repository example; не обращался к owner config, не одобрял запросы и не вызывал popup response. Отдельный diagnostic probe выше был read-only.
- Final full Node на frozen Linux snapshot: **3619 total, 3611 passed, 8 platform-skipped, zero failed**, 170 файлов, 527.93 секунды. Physical Linux dependencies и Git history присутствовали; source hashes неизменны. Два skipped WSL-mount cases дополнительно прошли в development clone; оставшиеся шесть native Windows storage/artifact cases не запускались. Узкий native consent fixture — отдельная evidence, не pass этих шести cases.
- Agent evals **15/15**. Runtime build и все 15 non-test static constituents прошли отдельными командами, включая typecheck, version, docs, notices/SBOM, bundled-runtime reproducibility и manifest-security. Это не `check:release` full-matrix GO.
- Изолированный `sgg-unity-mcp-gateway-temp`, package `2026.10.11-1`, Unity `6000.6.5f1`: compact compile `4072277cdc164c52a305d6a472f1afbb` вернула **ready_clean, zero errors**, post-reload stability verified, без refresh replay.
- Unity target `McpFtueConfigTests`, `test-20261011-161952-822e6b47`: **108/108**, authoritative `unity_test_runner` counts; один delayed status read, без test replay.
- Final full EditMode `test-20261011-162225-31845212`: **1399 total, 1379 passed, 20 ignored, zero failed**, authoritative `unity_test_runner` counts; один delayed status read, без test replay. Прежние setup-window tests ignored из-за уже открытого MCP window; это не executed UI pass. Successful artifacts не перечитывались и не переписывались.

Final Node JSON report SHA-256: `99b085c16dca0f3d44755ca96278e3b28898176cdc3ccc9f288b61a218f636ec`. Frozen non-Markdown source manifest SHA-256: `332e41c2233dc75d1ded212f293fdb393f9482251702d836ce4373a6f15d1fdf` (sorted `path + NUL + file SHA-256`, joined with LF). После успешного full run менялась только Markdown evidence/changelog documentation; docs checks повторены. Private logs/configs, raw reports и triage не публикуются.

## Сохранённые failures и recovery

- Первый typecheck отклонил optional `lstat` result в test helper; fixture исправлен до final verification. Первый static pass отклонил отсутствие секции Added в changelog; она добавлена, exact gate повторён. Это сохранённые предварительные failures, не успешные попытки.
- Native Windows Vitest attempt отказал до сбора тестов: `.js` source imports не разрешились в `.ts` в этом окружении. Это **не** native test-suite pass evidence. Отдельный emitted native fixture выше проверил неизменённые настоящие Node filesystem APIs и baseline/fixed readers.
- Первый full Node показал **3610 passed, 1 failed, 8 skipped**. Неизменённый audit child-process append/rotation case получил `Storage file identity changed` при чтении concurrent lock owner. Audit/storage runtime, fixture, assertions и timeouts не менялись. Exact unchanged audit suite прошёл **17/17**, затем успешный full frozen run выше. Waiver прежнего выпуска не применялся; root cause предварительной audit race здесь не объявляется исправленным.
- Initial isolated Unity startup блокировал **Project Upgrade Required** (saved `6000.6.2f1`, opened `6000.6.5f1`); discovery не имела fresh temp heartbeat, `compile.status` вернул `project_stale` без dispatch. Владелец отдельно выбрал manual Continue для temp. После этого получена fresh readiness/compile evidence; агент не нажимал Continue/Quit и не реализовывал этот popup. Owner-requested upgrade-popup roadmap entry отделён от reader fix.

## Boundaries и оставшаяся acceptance

1552 frozen raw report/triage files и все четыре original shared/private config/policy files сохранили exact hashes, sizes и timestamps. Deployment clone clean; working Gateway runtime, installed OpenCode/OpenChamber, working configs и popup opt-ins не обновлялись. Source changes только в development clone; Unity validation только temp. Deployment выполняет владелец.

Реальные Keep Backups click, backup-preservation check, human Accept/Decline/Cancel и client visual acceptance не заявлены. Other Unity versions, PlayMode, clean install/update, broader native Windows client/storage matrix и historical PR219 scanner/binding causes — отдельные gates. Response handlers для Missing Signature и Project Upgrade Required остаются roadmap-only.
