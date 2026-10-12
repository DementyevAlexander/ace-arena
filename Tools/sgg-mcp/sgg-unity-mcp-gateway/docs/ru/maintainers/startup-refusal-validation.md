# Проверка startup и preflight отказов

[English](../../en/maintainers/startup-refusal-validation.md) | [Русский](./startup-refusal-validation.md)

Gateway `2026.10.12-2`, 2026-10-12. Source fixes и изолированная validation, не рабочая установка или native interaction acceptance.

## Узкие изменения

- Отказы публикации различают prepare admission/queue, command deadline, precommit denial и существующий handshake timeout пять секунд. Запоздалый parent не может выдать истёкшее разрешение. Command deadline, durability и immutable late-commit/issuance правила сохранены.
- Диагностика shared startup содержит phase elapsed, minimum descriptor/inherited caller и binding reserve. Discovery elapsed теперь включает metadata latency; terminal wait не продлевает admission.
- Keep Backups отказывает при неполном User32 coverage, даже если target row полная. Фиксированные bounded scanner markers улучшают локализацию без изменения бюджета или разрешения interaction. Binding witnesses отличают непроверенные сравнения от mismatch и показывают только fingerprints roots.
- Только delivered trusted quit refusal, совпадающий с reserved operation, caller hash и exact owner, позволяет пропустить shutdown reconciliation. Bridge issuance не равна quit lifecycle/exit issuance. Missing/malformed/uncertain evidence сохраняет conservative reconciliation; Play close и release retained-native-custody не поддерживаются.

## Evidence и ограничения

- Targeted Node consumers: **1179/1179 passed**. Final full Node: **3795 total / 3789 passed / 6 platform-skipped / 0 failed**, 174 files; agent evals **15/15**, source static gates **15/15**. После frozen functional inputs менялся только Markdown; assertions/timeouts/production guards не ослаблялись.
- Windows Node **22.16.0**, actual bundled worker: **3855 retained bindings / 7710 frozen files**, hashes и mtimes неизменны; prepare **2817 ms**, issuance **2465 ms**, один guard invocation и bounded refusal witness проверены. Unity не вызывался.
- Temp Unity **6000.6.5f1**: exact pure classes **32/32 + 102/102 passed**. Full EditMode `test-20261012-084005-cf54cae9`: **1590 total / 1570 passed / 20 ignored / 0 failed**, authoritative terminal counts; один status read через120 секунд, без replay.
- Единственный explicit refresh вернул `compile_freshness_unverified` после потери post-reload lifecycle continuity и не повторялся. Отдельный read вернул `ready_clean`, zero compile errors; выполнение новой pure class доказывает её loaded test code, не original refresh outcome или general source/domain/DLL provenance.
- **1607 защищённых raw/config/policy files сохранили bytes/mtime.** Concurrent shared history не объявляется неизменной: шесть старых файлов отсутствуют, четыре файла принадлежат двум отдельным Weather operations. Test operations этого прохода используют private store. Внешние изменения не восстанавливаются, не классифицируются как successful cleanup и не используются как historical outcome proof.

Историческое execution не повторяется ради evidence; private planning changes не включаются в source publication.

Причина original publication abort этими fixes не установлена. Проверенные reserved/issued historical test operations остаются outcome unknown; later independent PASS их не заменяют. Reservation — намеренный bounded placeholder, не issuance marker.

Current-source/compiler-generation acceptance, physical popup/backups acceptance и рабочий runtime/package deployment остаются отдельными проверками. Retained native custody требует explicit close/ACK contract owning package; process exit не доказывает GPU completion или quality success.

Gateway-side OpenCode v2 integration уже выпущена. Stock Web/default Code Mode presentation требует отдельно разрешённого client-source изменения; installed OpenCode, user configuration и client renderer здесь не патчатся.
