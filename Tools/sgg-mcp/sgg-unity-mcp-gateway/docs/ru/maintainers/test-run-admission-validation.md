[English](../../en/maintainers/test-run-admission-validation.md) | [Русский](./test-run-admission-validation.md)

# Проверка стартового бюджета test.run

Gateway `2026.10.12-1`, 2026-10-12 UTC. Evidence development-source и изолированной проверки, не рабочей установки или полной release matrix.

## Исправление и границы вывода

Report `problem_20261011T234916_4bab7d40b1cd` фиксирует `test-20261011-234559-f2a8214a` как **not dispatched**: durable binding/reservation сохранены, issuance не наблюдалась, reason `binding_publication_crossed_deadline`. Старый descriptor давал 15000 ms на startup, включая discovery и durable publication. Terminal waiting и `max_seconds` теста этот предел не увеличивают.

Supported descriptor теперь объявляет **60000 ms**. Более короткий caller/wrapper deadline сохраняет приоритет. Storage formats, capacity, retention, custody, pre-commit/pre-fetch owner/policy/feature/deadline checks, исходный issuance timestamp, uncertainty и запрет replay не меняются. Config/catalog patches, transport bypass, history purge и новый job route не применяются. Исправление находится в descriptor Unity-пакета; обновления только Gateway executable недостаточно.

Анализ storage доказывает retained metadata scans при publication, не точную причину задержки на историческом Windows host. Для произвольно большой/медленной истории performance guarantee не заявляется. Отдельная более поздняя Foliage operation `test-20261011-235459-97783b90` выполнилась и завершилась FAIL; это не acceptance или восстановление первого неотправленного запроса. В этой задаче ни один из них не повторялся.

## Выполненные проверки

- Primary focused implementation/integration review завершён. Независимая read-only storage diagnosis не выдаётся за independent final-fix review.
- Affected Node targets: **538/538**, пять файлов. Новые controlled случаи с20s preparation/25s issuance используют shipped descriptor; shorter deadlines, changed owner, deny policy и disabled feature сохраняют safe refusal. До issuance bindings/reservations сохраняют exact bytes/mtime; после неё сохраняются одна попытка и исходный timestamp.
- Native Windows Node `22.16.0`, actual bundled worker: **3855 retained bindings / 7710 protected files**, hashes/mtimes неизменны. Preparation **3313 ms**, issuance после release lock **2545 ms**. Lock wait, terminal receipt, pre-commit denial и invalid worker-role refusal прошли; Unity не запускался. Это synthetic fixture timings, не воспроизведение historical latency.
- Final full Node `22.22.3`: **3755 total / 3747 passed / 8 skipped / 0 failed**,174files, **470.78s**, byte-verified Linux snapshot/no source drift. Evals **15/15**. Изначально прошли14static constituents; required changelog Added section затем восстановлен и final static checks прошли **15/15**.
- Temp Unity `6000.6.5f1`, loaded package `2026.10.12-1`, commands hash `1f52daf5bb7dd2de`: actual trusted descriptor `test.run` содержит **60000 ms**, revision `sha256:61ceb66d98e9174ce49d9d7f1a8558e55411969a9e2dfc0ebf0667ed5d8b629d`.
- Independent compact readiness `f5525329dfbb48f1a55f24596a487a14`: **ready_clean / 0 errors**. Target `UnityMcpPureHelperTests`, operation `test-20261012-001402-c7f75c9e`: **102/102**, authoritative counts, один status read через15s.
- Full EditMode `test-20261012-001436-750b60dd`: **1558 total / 1538 passed / 20 ignored / 0 failed**, authoritative persisted exact-owner result, один status read через120s, без replay. Existing open-window fixture exclusion сохранён; ignored не считаются passed.

Full Node report SHA-256: `b314e6ee925095d2e8fb2bfdaa87c7f9b5e6b15ca0152b18bd00c3c27d4fdc16`. Frozen non-Markdown manifest: **788files**, SHA-256 `62226cf03e0e3333ba2f9ab5666da6b23fa397039ca799c181713b36db4f2e17` (sorted path + NUL + file SHA-256, joined LF). После full suites менялись только Markdown evidence/navigation/changelog; functional inputs byte-identical. Bundled CLI SHA-256 сохраняется `1e9489047ded5138959894b5c310a0b1e54b7b9dfb5951f66b9021a9403f0f42`; package/runtime metadata и SBOM пересобраны.

## Сохранённые отказы и ограничения

Initial targeted: **305 passed / 2 failed**, оба новых fixtures. Один заменял captured policy object вместо изменения его execute field; другой передавал incomplete response DTO, корректно отвергнутый production validation. Fixtures исправлены существующими policy/artifact contracts, без ослабления guards. Два private launcher fixtures отказали до process start из-за non-normalized comparison target; partial owned directories сохранены.

Отправлен один refresh. Shell wrapper превысил180s; owned audit позже зафиксировал `compile_freshness_unverified` / `catalog_stale` через193293ms после reload, без repeated write. Отдельные readiness и executed target выше подтверждают current loaded state, не successful freshness verification первого refresh. Старый waiver не применялся.

Все **1597** protected raw report/triage/original config/policy files сохранили hashes/sizes/mtimes. Дополнительно наблюдавшийся Foliage evidence Markdown изменился вне этой задачи, описав отдельный поздний FAIL; boundary checker сохранил exit1 из-за внешнего изменения, которое агент не перезаписывал и не откатывал. Deployment clone clean/unchanged `bf752cd7441fafb908a5575de5dbf61bec5dc5a0`. Shared history не очищалась; остановлен только exact-identity owned validation Gateway PID75356.

Source changes только dev; Unity validation только temp. Working clients/runtime/configs, новый Foliage trial, physical UI, installed runtime/DLL parity, другие Unity versions, PlayMode и full release matrix — unverified/not_run. Historical missing operations и широкие report families причинно не закрыты. Accumulated private planning changes не публикуются; рабочую установку выполняет владелец.
