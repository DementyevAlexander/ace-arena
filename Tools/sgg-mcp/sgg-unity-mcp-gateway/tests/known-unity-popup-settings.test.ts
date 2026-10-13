import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { KnownUnityPopupSettingsManager } from "../src/approvals/known-unity-popup-settings.js";
import * as storage from "../src/security/safe-storage.js";

const example = fs.readFileSync(new URL("../config/gateway.example.json", import.meta.url), "utf8");
const directories: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  for (const directory of directories.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

function fixture(enabled = true) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "known-popup-settings-"));
  directories.push(directory);
  const file = path.join(directory, "gateway.json");
  const config = JSON.parse(example);
  const save = (value: boolean) => { config.approvals.auto_allow_known_unity_popups = value; fs.writeFileSync(file, JSON.stringify(config)); };
  save(enabled);
  return { file, config, save, manager: new KnownUnityPopupSettingsManager(file) };
}

describe("live owner consent for exact known Unity popup responses", () => {
  it("observes OFF/ON/OFF in one manager without an mtime cache or restart", () => {
    const f = fixture(false);
    expect(f.manager.reload()).toBe(false);
    f.save(true);
    expect(f.manager.reload()).toBe(true);
    f.save(false);
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot()).toEqual({ application_mode: "live_on_request", source_status: "valid", saved_enabled: false, restart_required: false });
  });

  it.each([
    { pathDev: 0n, handleDev: 42n },
    { pathDev: 42n, handleDev: 0n },
  ])("observes live OFF/ON/OFF with stable native Windows path dev=$pathDev and handle dev=$handleDev", ({ pathDev, handleDev }) => {
    const f = fixture(false);
    emulateStatIdentities("win32", pathDev, handleDev);
    expect(f.manager.reload()).toBe(false);
    f.save(true);
    expect(f.manager.reload()).toBe(true);
    expect(f.manager.snapshot()).toMatchObject({ source_status: "valid", saved_enabled: true });
    f.save(false);
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot()).toMatchObject({ source_status: "valid", saved_enabled: false });
  });

  it.each([
    { name: "non-Windows path/handle device asymmetry", platform: "linux", pathDev: 0n, handleDev: 42n },
    { name: "non-Windows uncertain device", platform: "linux", pathDev: 0n, handleDev: 0n },
    { name: "different nonzero Windows volumes", platform: "win32", pathDev: 41n, handleDev: 42n },
    { name: "uncertain Windows inode", platform: "win32", pathDev: 0n, handleDev: 42n, pathBefore: { ino: 0n }, handleBefore: { ino: 0n } },
    { name: "Windows pathname volume changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, pathAfter: { dev: 43n } },
    { name: "Windows pathname loses its volume id during read", platform: "win32", pathDev: 42n, handleDev: 0n, pathAfter: { dev: 0n } },
    { name: "Windows descriptor volume changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, handleAfter: { dev: 0n } },
    { name: "Windows pathname inode changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, pathAfter: { ino: 67890n } },
    { name: "Windows descriptor inode changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, handleAfter: { ino: 67890n } },
    { name: "Windows pathname mtime changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, pathAfter: { mtimeNs: 0n } },
    { name: "Windows pathname ctime changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, pathAfter: { ctimeNs: 0n } },
    { name: "Windows descriptor mtime changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, handleAfter: { mtimeNs: 0n } },
    { name: "Windows descriptor ctime changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, handleAfter: { ctimeNs: 0n } },
    { name: "Windows config size changes during read", platform: "win32", pathDev: 0n, handleDev: 42n, pathAfter: { size: 0n } },
  ])("never retains ON after $name", ({ platform, pathDev, handleDev, ...overrides }) => {
    const f = fixture();
    expect(f.manager.reload()).toBe(true);
    emulateStatIdentities(platform as NodeJS.Platform, pathDev, handleDev, overrides);
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot()).toMatchObject({ source_status: "unstable", saved_enabled: null });
  });

  it("treats a missing owned field as default OFF, not a nested consent lookalike", () => {
    const f = fixture();
    delete f.config.approvals.auto_allow_known_unity_popups;
    fs.writeFileSync(f.file, JSON.stringify(f.config));
    expect(f.manager.reload()).toBe(false);
    delete f.config.approvals;
    fs.writeFileSync(f.file, JSON.stringify(f.config));
    expect(f.manager.reload()).toBe(false);
    fs.writeFileSync(f.file, '{"other":{"approvals":{"auto_allow_known_unity_popups":true}}}');
    expect(f.manager.reload()).toBe(false);
  });

  it.each(["true", 1, null, {}])("rejects non-boolean consent %j after a valid ON read", (value) => {
    const f = fixture();
    expect(f.manager.reload()).toBe(true);
    f.config.approvals.auto_allow_known_unity_popups = value;
    fs.writeFileSync(f.file, JSON.stringify(f.config));
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot()).toMatchObject({ source_status: "invalid", saved_enabled: null });
  });

  it.each([
    '"auto_allow_known_unity_popups":true,"auto_allow_known_unity_popups":false',
    '"auto_allow_known_unity_popups":false,"auto_allow_known_unity_popups":true',
    '"auto_allow_known_unity_popups":true,"auto_allow_known_unity_pop\\u0075ps":true',
  ])("rejects duplicate consent including decoded aliases: %s", (members) => {
    const f = fixture();
    const source = fs.readFileSync(f.file, "utf8").replace('"auto_allow_known_unity_popups":true', members);
    fs.writeFileSync(f.file, source);
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot().source_status).toBe("invalid");
  });

  it.each(["malformed", "duplicate_approvals", "trailing", "invalid_utf8", "oversized", "missing", "unreadable", "directory"] as const)("never retains ON after %s and recovers only on a fresh valid read", (failure) => {
    const f = fixture();
    expect(f.manager.reload()).toBe(true);
    let restore = () => {};
    if (failure === "malformed") fs.writeFileSync(f.file, "{");
    if (failure === "duplicate_approvals") fs.writeFileSync(f.file, fs.readFileSync(f.file, "utf8").replace(/}$/, ',"approvals":{"auto_allow_known_unity_popups":true}}'));
    if (failure === "trailing") fs.appendFileSync(f.file, " {}");
    if (failure === "invalid_utf8") fs.writeFileSync(f.file, Buffer.concat([Buffer.from(example), Buffer.from([0xff])]));
    if (failure === "oversized") fs.writeFileSync(f.file, " ".repeat(1024 * 1024 + 1));
    if (failure === "missing") fs.unlinkSync(f.file);
    if (failure === "unreadable") {
      const spy = vi.spyOn(fs, "openSync").mockImplementationOnce(() => { throw Object.assign(new Error("private detail"), { code: "EACCES" }); });
      restore = () => spy.mockRestore();
    }
    if (failure === "directory") { fs.unlinkSync(f.file); fs.mkdirSync(f.file); }
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot().saved_enabled).toBeNull();
    expect(JSON.stringify(f.manager.snapshot())).not.toContain(f.file);
    restore();
    if (failure === "directory") fs.rmdirSync(f.file);
    f.save(true);
    expect(f.manager.reload()).toBe(true);
  });

  it.each(["growth", "replacement", "in_place"] as const)("rejects %s during the opened-handle read without retrying it", (change) => {
    const f = fixture();
    const original = fs.readSync.bind(fs) as (fd: number, buffer: Buffer, offset: number, length: number, position: number | null) => number;
    let changed = false;
    let bytesRequested = 0;
    const spy = vi.spyOn(fs, "readSync").mockImplementation(((fd: number, buffer: Buffer, offset: number, length: number, position: number | null) => {
      bytesRequested += length;
      const count = original(fd, buffer, offset, length, position);
      if (!changed) {
        changed = true;
        if (change === "growth") fs.appendFileSync(f.file, " ".repeat(1024 * 1024));
        if (change === "replacement") {
          const replacement = f.file + ".new";
          fs.copyFileSync(f.file, replacement);
          fs.renameSync(replacement, f.file);
        }
        if (change === "in_place") {
          const sameSize = fs.readFileSync(f.file, "utf8").replace('"auto_allow_known_unity_popups":true', '"auto_allow_known_unity_popups":null');
          fs.writeFileSync(f.file, sameSize);
        }
      }
      return count;
    }) as typeof fs.readSync);
    expect(f.manager.reload()).toBe(false);
    expect(f.manager.snapshot()).toMatchObject({ source_status: "unstable", saved_enabled: null });
    expect(bytesRequested).toBeLessThanOrEqual(1024 * 1024 + 2);
    expect(spy.mock.calls.length).toBeLessThanOrEqual(2);
  });

  it("rejects a config symlink instead of accepting another consent source", () => {
    const f = fixture();
    const link = f.file + ".link";
    fs.symlinkSync(f.file, link);
    expect(new KnownUnityPopupSettingsManager(link).reload()).toBe(false);
  });

  it("fails closed on handle close failure without throwing or retaining ON, and recovers on fresh read", () => {
    const f = fixture();
    expect(f.manager.reload()).toBe(true);
    const close = fs.closeSync.bind(fs);
    vi.spyOn(fs, "closeSync").mockImplementationOnce((handle) => {
      close(handle);
      throw Object.assign(new Error("private cleanup failure"), { code: "EIO" });
    });
    expect(() => f.manager.reload()).not.toThrow();
    expect(f.manager.snapshot()).toMatchObject({ source_status: "unreadable", saved_enabled: null });
    expect(f.manager.reload()).toBe(true);
  });
});

function emulateStatIdentities(platform: NodeJS.Platform, pathDev: bigint, handleDev: bigint, overrides: {
  pathBefore?: Partial<fs.BigIntStats>; pathAfter?: Partial<fs.BigIntStats>;
  handleBefore?: Partial<fs.BigIntStats>; handleAfter?: Partial<fs.BigIntStats>;
} = {}) {
  // Use the real shared identity rule on Windows-shaped stats without changing
  // the runner's process.platform or granting a mocked unconditional match.
  const identity = storage.sameFileObjectIdentity;
  vi.spyOn(storage, "sameFileObjectIdentity").mockImplementation((a, b) => identity(a, b, platform));
  const lstat = fs.lstatSync;
  const fstat = fs.fstatSync;
  let paths = 0;
  let handles = 0;
  vi.spyOn(fs, "lstatSync").mockImplementation(((...args: Parameters<typeof fs.lstatSync>) => {
    const stat = lstat(...args);
    if (!stat) return stat;
    const patch = paths++ % 2 === 0 ? overrides.pathBefore : overrides.pathAfter;
    return Object.assign(Object.create(stat), { dev: pathDev, ino: 12345n }, patch);
  }) as typeof fs.lstatSync);
  vi.spyOn(fs, "fstatSync").mockImplementation(((...args: Parameters<typeof fs.fstatSync>) => {
    const stat = fstat(...args);
    const patch = handles++ % 2 === 0 ? overrides.handleBefore : overrides.handleAfter;
    return Object.assign(Object.create(stat), { dev: handleDev, ino: 12345n }, patch);
  }) as typeof fs.fstatSync);
}
