# Unity CLI Cold One-Shot Fixture

This disposable fixture compares two read-only `com.unity.pipeline` commands that differ only in `MainThreadRequired`.

Copy `Assets/Editor/SggColdOneShotCommands.cs` into an Editor folder in a disposable Unity project that already has the exact Pipeline version under test. The target folder must not be governed by an existing assembly definition unless that assembly explicitly references `Unity.Pipeline`; `Assets/SggColdOneShot/Editor` is a safe default for projects whose existing `Assets/Editor` is governed by another assembly. Do not add or update Pipeline as part of the fixture run.

Record the exact Unity CLI, Pipeline, Editor, OS, and project state before running this matrix:

```text
warm GUI Editor:       unity command sgg_cold_main_thread_noop --project-path <project>
warm background:       unity command sgg_cold_background_noop --project-path <project>
cold one-shot main:    unity run <project> --command sgg_cold_main_thread_noop --format json --timeout 120
cold one-shot control: unity run <project> --command sgg_cold_background_noop --format json --timeout 120
```

Capture the status timeline, HTTP/error code, elapsed time, command name, main-thread classification, returned `data.result`, and relevant Editor log markers. The expected values are `main-thread-ok` and `background-ok`.

A typed `503 Editor is still settling` is retryable only when authoritative response evidence says `command_dispatched:false`, only for these read-only no-op commands, and only inside one fixed startup deadline. If status says ready while the main-thread no-op keeps returning settling, stop with `unity_cli_cold_main_thread_gate_stuck`. Do not loop indefinitely, add `-automated`, or automatically replay writes or uncertain deliveries.

Live matrix status for `2026.8.28-1`, Windows 11, Unity CLI `1.0.0-beta.6`, Pipeline `0.5.0-exp.1`:

- Unity `6000.4.12f1`: warm GUI and persistent headless main/background controls passed; cold main and background controls also passed.
- Unity `6000.5.9f1`: warm GUI and persistent headless main/background controls passed; cold background passed, while the single cold main-thread call returned typed HTTP `503 Server Busy / Editor is still settling` after about 37 seconds.

The failing cold main-thread call was not retried. `-automated` was not added. The local result reproduces the version-bounded 6000.5 cold main-thread compatibility issue and does not imply that warm/persistent operation or Unity 6000.4 is affected.
