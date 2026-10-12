import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { KnownUnityPopupSettingsManager } from "../../src/approvals/known-unity-popup-settings.js";

// Bundle this fixture for native Windows execution; it never invokes a popup
// response, approves a pending request, or reads/writes the owner's config.
assert.equal(process.platform, "win32");
const baselinePath = process.argv[2];
assert.ok(baselinePath, "Supply the separately bundled pre-fix manager");
const examplePath = process.argv[3];
assert.ok(examplePath, "Supply the repository's non-secret example config");
const config = JSON.parse(fs.readFileSync(examplePath, "utf8")) as { approvals: { auto_allow_known_unity_popups: boolean } };
const baseline = await import(pathToFileURL(baselinePath).href) as { KnownUnityPopupSettingsManager: typeof KnownUnityPopupSettingsManager };
const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-known-popup-native-"));
const file = path.join(root, "gateway.json");
const save = (enabled: boolean) => { config.approvals.auto_allow_known_unity_popups = enabled; fs.writeFileSync(file, JSON.stringify(config)); };
try {
  save(true);
  const entry = fs.lstatSync(file, { bigint: true });
  const fd = fs.openSync(file, fs.constants.O_RDONLY);
  const opened = fs.fstatSync(fd, { bigint: true });
  fs.closeSync(fd);
  assert.equal(entry.ino, opened.ino);
  assert.notEqual(entry.ino, 0n);
  const old = new baseline.KnownUnityPopupSettingsManager(file);
  const oldEnabled = old.reload();
  const asymmetric = entry.dev !== opened.dev;
  if (asymmetric) {
    assert.ok((entry.dev === 0n) !== (opened.dev === 0n));
    assert.equal(oldEnabled, false);
    assert.equal(old.snapshot().source_status, "unstable");
  }
  const manager = new KnownUnityPopupSettingsManager(file);
  assert.equal(manager.reload(), true);
  assert.equal(manager.snapshot().saved_enabled, true);
  save(false);
  assert.equal(manager.reload(), false);
  assert.equal(manager.snapshot().source_status, "valid");
  save(true);
  assert.equal(manager.reload(), true);
  const valid = fs.readFileSync(file, "utf8");
  const corrupt = [
    ["malformed", "{"],
    ["duplicate", valid.replace('"auto_allow_known_unity_popups":true', '"auto_allow_known_unity_popups":true,"auto_allow_known_unity_popups":false')],
    ["non_boolean", valid.replace('"auto_allow_known_unity_popups":true', '"auto_allow_known_unity_popups":"true"')],
    ["oversized", " ".repeat(1024 * 1024 + 1)],
  ];
  for (const [, content] of corrupt) {
    fs.writeFileSync(file, content!);
    assert.equal(manager.reload(), false);
    assert.equal(manager.snapshot().saved_enabled, null);
    save(true);
    assert.equal(manager.reload(), true);
  }
  fs.unlinkSync(file);
  assert.equal(manager.reload(), false);
  assert.equal(manager.snapshot().source_status, "missing");
  console.log(JSON.stringify({ platform: process.platform, node: process.version,
    native_path_dev: String(entry.dev), native_handle_dev: String(opened.dev),
    device_asymmetry_reproduced: asymmetric, baseline_enabled: oldEnabled,
    fixed_live_on_off_on: true, fail_closed_controls: [...corrupt.map(([name]) => name), "missing"],
    interaction_attempted: false, owner_config_accessed: false }));
} finally { fs.rmSync(root, { recursive: true, force: true }); }
