[English](../../en/maintainers/opencode-v2-gateway-validation.md) | [Русский](./opencode-v2-gateway-validation.md)

# Проверка Gateway-интеграции с OpenCode v2

Gateway build `2026.10.10-6`, presentation plugin `2.0.0`, upstream OpenCode `2.0.25` / `b44eea9e204024db2b480a136ece29d790d5f593`. Отчёт относится к Gateway-side P0, не к полной client UX acceptance или полной Unity release matrix.

## Выполненные проверки

- Focused source review: GO после исправления hidden-layer shadowing отключённого сервера, legacy timeout inheritance и native optional-field/protocol validation. Authority, approval TTL, opt-ins и no replay не расширены.
- Первый общий Node target: 326/326. Review regression/selector consumers: 138/138. Transport consumers проверены отдельно; старые error-summary assertions теперь требуют exact canonical JSON в прежних selector byte bounds, без увеличения лимитов.
- Финальный полный Node на frozen Linux snapshot: **3603 total, 3595 passed, 8 skipped, zero failed**, 170 файлов; старт `2026-10-10T21:53:46.640Z`, завершён за 456.89 секунд. Все 15 agent evals и runtime-compliance cases выполнены успешно; final source hashes неизменны. Восемь platform skips — шесть native-Windows cases и два WSL-mount cases; exact два mount cases дополнительно прошли в development clone. Native Windows execution не заявляем.
- Runtime build и все 15 non-test static constituents прошли, включая typecheck, docs, version/changelog, artifact ownership/reproducibility, notices/SBOM и manifest-security. Вместе с final full Node/evals пройден каждый static-profile constituent; это evidence отдельных команд, не полный release-matrix GO.
- Isolated Unity `6000.6.2f1`, только development package: финальная compact compile `9d74e8e52f58495285f91d8429431f54` — `ready_clean`, zero errors.
- Финальный `McpFtueConfigTests`: 108/108, `test-20261010-205020-2dd75ff6`. `OpenCodeConfigPathResolverTests`: 27/27; `OpenCodePresentationSetupManagerTests`: 17/17. Consumers `GatewayHealthParserTests`: 41/41; `Harden09A1WindowStateTests`: 25/25. Существующий window class — 20 ignored, не выполненный UI pass.
- Финальный полный EditMode `test-20261010-205203-92fd0c55`: **1399 total, 1379 passed, 20 ignored, zero failed**, authoritative counts от `unity_test_runner`. Успешные artifacts повторно не читались и не переписывались.
- Реальный released OpenCode host с temporary HOME/XDG, private authenticated server и установленной копией plugin вне dependency resolution репозитория: exact `sgg-unity.presentation` active, local legacy MCP shim connected, после reload active, повторный install вернул `noop`, после remove/reload plugin отсутствует, после reinstall снова active. Native config прочитан host. Cold inventories до активации location не считались loading evidence; passing проверки выполнены после активации location и bounded ожидания.

Installed-host lifecycle использовал exact bundled plugin manifest SHA-256 `fb36e673195d9275a4dd46d8acf42477a0cff49694d918cba1127f1f64d66824`. Проверка границ подтвердила неизменность 1539 frozen raw reports и bytes/timestamps triage, исходных isolated config/policy bytes, clean deployment clone и PR224 publication source/tests.

Final Node JSON report SHA-256: `bf2738a00429b3f14e18c66d8312c0cae150dfc8d62d29c67a15052cff2b904e`. Digest frozen non-Markdown source manifest: `8f16d6b587dda97332d643680b29f2494d855e8eaaceffc5251d7bc10d46e788` (SHA-256 sorted `path + NUL + file SHA-256`, joined with LF). После full run изменён только bilingual validation report; documentation checks повторены. Private logs/configs и raw reports не публикуются.

## Сохранённые предварительные failures

Дополнительный mounted-tree SDK/dispatcher run: 532 passed и timeout одного approval fixture около 5363 ms при неизменном лимите 5000 ms. Exact unchanged fixture отдельно прошёл в Linux snapshot. Предварительный full attempt с двумя workers в mounted tree сообщил десять failed tests и превысил process deadline 900 секунд; source тогда ещё менялся, поэтому это не final-commit evidence. Один mounted-tree eval также получил timeout. Failures сохранены, не превращены в pass и не покрываются waiver прошлого релиза. Финальная проверка использовала immutable snapshot с одним worker; assertions и timeouts не ослаблены. Environment/timing sensitivity не считается доказанной причиной каждого предварительного failure.

Первый one-worker Linux full attempt тоже превысил 900 секунд и сообщил failures в dispatcher, HTTP, popup, audit и historical source tests. Snapshot не содержал Git history, что сделало три historical source checks невалидными; external dependency symlink также не позволяет считать skipped runtime-compliance tests пройденными. Затем подключены Git history и physical Linux dependencies. По этим неполным попыткам не заявляем final Node/full-static pass или публикацию релиза.

После подключения Git history и physical Linux dependencies завершён affected ten-file run: 839 tests, 821 passed, 18 failed, none skipped, frozen source unchanged. Семнадцать failures — timeouts 5000 ms; один mandatory-human case вернул fail-closed `approval_unavailable` вместо ожидаемого pending confirmation. Exact unchanged case отдельно прошёл за 387 ms, что не заменяет failed suite. Прежние HTTP, popup, audit, compile-convergence и runtime-compliance failures в этом прогоне прошли. Idle-environment repeat: 838/839; остался только HTTP fixture с compressed 30/60 ms real-network timing race.

Теперь fixture сдвигает logical clock после получения настоящего HTTP request, наблюдая неизменный native timeout и exact serialized deadline: 40 секунд должны пройти за старым 30-second cap, 60/60.001 секунд — отказ на границе/после deadline. Runtime не изменён, test timeout не повышен. Обновлённый HTTP suite прошёл 76/76, focused review — GO до успешного final full run выше.

## Что не заявляем

- Visual labels в Web/PWA, terminal и OpenChamber, включая inner default Code Mode rows.
- Реальный human Accept/Decline/Cancel формы или concurrent frontend routing. Public SDK relay fixtures проверяют transport, не human acceptance.
- Model session с direct/Code Mode вызовами внутри isolated host. Connected inventory доказывает только подключение.
- Live client execution Windows PowerShell shim в этой итерации; command parity проверен тестами, реальный isolated host использовал Linux shim.
- Другие Unity versions, PlayMode, clean install/update matrix или полный `check:release` GO.
- Deployment, patch установленного клиента, restart shared Gateway, изменения working configs/policies или reported operations.

[Отдельные client follow-up](./opencode-v2-client-follow-up.md) остаются proposal. Gateway source/artifact slice можно выпустить независимо; общая P0 visual/human acceptance остаётся открытой. PR219 и Missing Signature follow-up этим релизом не реализуются.
