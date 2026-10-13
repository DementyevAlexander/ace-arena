[English](../../en/maintainers/test-history-validation.md) | [Русский](./test-history-validation.md)

# Проверка начального заполнения Setup и истории тестов

Gateway `2026.10.11-3`, 2026-10-11 UTC. Это проверка development-source, тестовых сценариев и изолированного runtime, не рабочей установки, физического UI или полной release matrix.

## Выпущенные изменения

Обязательные строки Setup заполняются до необязательных freshness checks. Независимые read-only разделы явно показывают ошибку/непроверенное состояние. Подавление refresh и teardown ограничены конкретным тестовым окном, а не открытым окном владельца. Полная причина исходного screenshot не доказана.

Gateway Test History получил отдельные индикаторы байтов/записей, сохраняемые настройки с применением после restart и подтверждённую очистку с точными fingerprints. Независимые defaults: **209715200 байт (200 МиБ)** и **192000 записей**; retention30дней, размер записи16КиБ и publisher leases64 сохранены. Неизвестное заполнение не выдаётся за ноль. Сохранение не меняет посторонние поля, не перезапускает Gateway и не расширяет permissions.

Preview ограничен256 записями. Удаляется только metadata с точным валидным terminal execution evidence либо связанным no-execution receipt без противоречивого artifact. Active/unknown/cancelled/uncertain/reserved/foreign/invalid/changing сохраняются. Directory/file custody, полный snapshot/content guard перед commit, общий settings-writer lock и единый десятисекундный deadline соблюдаются. Unity artifacts и reports не удаляются.

## Выполненные проверки

- Независимый focused C# review принят. Backend findings исправлены primary после ошибки провайдера worker; финальные focused integration review и regressions завершены без новых агентов. Новый независимый review последних исправлений не заявляется.
- Последние affected Node targets — **103/103**, четыре файла. Предыдущий19-file report: **904 passed, 6 skipped, zero failed**; shell wrapper превысил300с, хотя завершённый Vitest report успешен, поэтому успех wrapper не заявляется. Consumers исправленного child fixture отдельно: **105 passed / 4 skipped**.
- Final full Node: **3748 total, 3740 passed, 8 skipped, zero failed**, 174 files, 494.29 секунд; Node`22.22.3`, byte-verified Linux snapshot с physical dependencies, без source drift. Исходные deadlines/assertions и внутри-тестовая concurrency сохранены.
- Все **15** non-test static constituents прошли, включая version/docs/package/final runtime reproducibility/notices/SBOM/security; agent evals финального дерева **15/15**.
- Native Windows Node`22.16.0`: шесть storage/admin consumer files — **185 total / 176 passed / 9 skipped / 0 failed**. После последнего host-path guard exact admin class: **78 total / 77 passed / 1 skipped / 0 failed**. Более широкая Windows matrix не запускалась.
- Final emitted CLI SHA-256 `1e9489047ded5138959894b5c310a0b1e54b7b9dfb5951f66b9021a9403f0f42`: реальные Windows `status`, confirmed `configure`, `preview-cleanup`, `cleanup` прошли на новых synthetic isolated metadata. Missing confirmation/stale fingerprint отклонены. Удалены две terminal bindings; reserved/foreign records и raw-report bytes сохранены. Независимое изменение лимитов и возврат defaults проверены; shared history/configs не менялись.
- Temp Unity`6000.6.5f1`, package`2026.10.11-3`: compact compile`6e25dd438a824214930f1c6258b00120` — **ready_clean / 0 errors**, post-reload stability verified, без replay refresh. Exact EditMode targets: history**22**, initial population**4**, shared parser**96**, tool view**34**, state restoration**41** — **201/201**.
- Full EditMode`test-20261011-231226-f1bf95f9`: **1557 total / 1537 passed / 20 ignored / 0 failed**, authoritative`unity_test_runner` counts из persisted exact-owner artifact; один status read через120с, без replay. Последующие изменения — Node/admin path validation, tests и generated CLI/SBOM; Unity C# inputs не менялись, полный EditMode не повторялся.

SHA-256 полного Node report: `0a80a1c01635a84e979adde0d2e31564ff3b3754917d9425431ca0cdb4890927`. Frozen non-Markdown manifest SHA-256: `5f723e9b8e7b95e5d7b24992e8e3c80dca3fed3902d10ee87985386d7c9707f0` (788 files; sorted `path + NUL + file SHA-256`, joined with LF). После final suites изменялась только Markdown evidence/navigation; documentation checks повторены. Private raw evidence/configs не публикуются.

## Сохранённые сбои и исправления

Initial C# fixtures: missing namespace и четыре behavioral failures; исправлены real-panel lifecycle и literal absent-JSON checks, без ослабления assertions. Backend review выявил cancellation/evidence, writer locking, maintenance side effects, custody, bounded scanning, identity, contradiction, deadline и deletion-content findings; финальные regressions сохраняют uncertain/changing files.

Первоначальный native запуск не разрешал Windows-модуль resolver из Linux dependency tree; зависимости установлены только в disposable native fixture. Таймер child exit200мс истекал во время native owner observation; release signal теперь доказывает и live preservation, и confirmed-exit quarantine. Coherent Windows transition-gate successor наблюдается заново и не удаляется по stale dead-owner evidence.

Первый full Node сохранил **3732 passed / 4 failed / 8 skipped**: два external diagnostic timeouts, запрещённый прямой path resolver и устаревший global-refresh source contract. Узкий fixture использует complete bounded empty native scan без изменения deadlines/dispatcher behavior; config validation — CanonicalPath и четыре invalid-path regressions; source contract проверяет per-window suppression. Missing changelog section и smoke-harness import/accounting errors также сохранены и исправлены. Старый waiver не применялся; причины исторических timeouts не объявляются устранёнными.

## Границы и открытая приёмка

Все **1596** frozen raw/triage/original config/policy files сохранили hashes/sizes/timestamps. Deployment clone остался clean на`78bd6e572f0b2503e0b92809cea69a992a419101`. Source edits только dev, Unity validation только temp. Существующая shared history не очищалась. Остановлены только task-owned validation Gateways после exact identity check. Working clients/tools/configs и original authorization не менялись; deployment выполняет владелец.

Физические EN/RU bars/buttons/confirmation/cancel, working-host install/update, installed runtime/DLL parity, другие Unity versions, PlayMode и full release matrix остаются **not_run/unverified**. Missing Signature и Project Upgrade Required — roadmap-only. Исторические missing operations, retained-running outcomes и PR219/224/228/241 не объявляются причинно закрытыми и не replay. Накопленные private planning changes не публикуются.
