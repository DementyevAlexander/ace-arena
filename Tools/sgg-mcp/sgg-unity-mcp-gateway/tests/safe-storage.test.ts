import { spawn, type ChildProcess } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { acquireStorageAdminLock, acquireStorageLock, currentProcessIdentityTag, currentProcessOwner, durableUnlinkCustodiedFile, durableUnlinkFile, maintainDeleteTombstones, maintainPublisherTemps, openStorageDirectoryCustody, processOwnerGenerationState, processStartIdentitiesMatch, sameFileObjectIdentity, secureFileStat, secureRegularFiles, writeNewFileDurable, type CustodiedFileSnapshot } from "../src/security/safe-storage.js";

const roots: string[] = [];
const fixture = path.join(process.cwd(), "tests", "fixtures", "storage-child.ts");

afterEach(() => { for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true }); });

describe("safe storage process locks", () => {
  it("uses maintenance-free admin locking without reclaiming an existing owner", () => {
    const root = tempRoot();
    const custody = openStorageDirectoryCustody(root);
    const lock = acquireStorageAdminLock(custody, ".store.lock", Date.now() + 10000);
    const owner = fs.readFileSync(path.join(root, ".store.lock", "owner.json"));
    try {
      expect(() => acquireStorageAdminLock(custody, ".store.lock", Date.now() + 10000)).toThrow("Storage admin lock is held");
      expect(fs.readFileSync(path.join(root, ".store.lock", "owner.json"))).toEqual(owner);
      expect(fs.readdirSync(root)).toEqual([".store.lock"]);
    } finally { lock.release(); custody.close(); }
    expect(fs.readdirSync(root)).toEqual([]);
  });

  (process.platform === "linux" ? it : it.skip)("anchors deletion syscalls to the original directory even when its name is replaced after the last check", () => {
    const root = tempRoot();
    const oldRoot = `${root}-old`;
    roots.push(oldRoot);
    const file = path.join(root, "candidate.json");
    fs.writeFileSync(file, "candidate", { mode: 0o600 });
    const stat = fs.statSync(file, { bigint: true });
    const custody = openStorageDirectoryCustody(root);
    const lock = acquireStorageAdminLock(custody, ".store.lock", Date.now() + 10000);
    const rename = fs.renameSync;
    const hook = vi.spyOn(fs, "renameSync").mockImplementation((from, to) => {
      if (String(from).endsWith("/candidate.json")) {
        rename(root, oldRoot);
        fs.mkdirSync(root, { mode: 0o700 });
        fs.writeFileSync(path.join(root, "candidate.json"), "foreign replacement", { mode: 0o600 });
      }
      rename(from, to);
    });
    try {
      expect(custody.anchored).toBe(true);
      expect(() => durableUnlinkCustodiedFile(custody, "candidate.json", stat)).toThrow("Storage directory identity changed");
      expect(fs.readFileSync(path.join(root, "candidate.json"), "utf8")).toBe("foreign replacement");
      expect(fs.readdirSync(root)).toEqual(["candidate.json"]);
      expect(fs.existsSync(path.join(oldRoot, ".store.lock"))).toBe(true);
    } finally { hook.mockRestore(); lock.release(); custody.close(); }
    expect(fs.existsSync(path.join(oldRoot, ".store.lock"))).toBe(false);
  });

  (process.platform === "linux" ? it : it.skip).each(["same-inode-body", "size", "mtime", "ctime", "hardlink"])("revalidates the full deletion snapshot after receipt fsync: %s", change => {
    const root = tempRoot();
    const file = path.join(root, "candidate.json");
    fs.writeFileSync(file, "candidate", { mode: 0o600 });
    const fixedTime = new Date("2026-10-12T12:00:00.000Z");
    fs.utimesSync(file, fixedTime, fixedTime);
    const expected = custodiedSnapshot(file);
    const custody = openStorageDirectoryCustody(root);
    const originalFsync = fs.fsyncSync;
    let changed = false;
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation(fd => {
      originalFsync(fd);
      const target = fs.readlinkSync(`/proc/self/fd/${fd}`);
      if (!changed && path.dirname(target) === root && /^\.delete-v1-.*\.json$/.test(path.basename(target))) {
        changed = true;
        if (change === "same-inode-body") { fs.writeFileSync(file, "changed!!"); fs.utimesSync(file, fixedTime, fixedTime); }
        if (change === "size") fs.appendFileSync(file, "!");
        if (change === "mtime") fs.utimesSync(file, new Date(0), new Date(0));
        if (change === "ctime") fs.chmodSync(file, 0o400);
        if (change === "hardlink") fs.linkSync(file, path.join(root, "linked-candidate.json"));
      }
    });
    try {
      expect(() => durableUnlinkCustodiedFile(custody, "candidate.json", expected, expected)).toThrow("snapshot changed");
      expect(changed).toBe(true);
      expect(sameFileObjectIdentity(expected, fs.statSync(file, { bigint: true }), process.platform)).toBe(true);
      expect(fs.readdirSync(root).some(name => name.endsWith(".tomb"))).toBe(false);
      if (change === "same-inode-body") {
        expect(fs.statSync(file, { bigint: true }).size).toBe(BigInt(expected.size));
        expect(fs.statSync(file, { bigint: true }).mtimeNs).toBe(expected.mtimeNs);
        expect(fs.readFileSync(file, "utf8")).toBe("changed!!");
      }
    } finally { fsync.mockRestore(); custody.close(); }
  });

  (process.platform === "linux" ? it : it.skip)("requires the expected content hash even when all file metadata matches", () => {
    const root = tempRoot();
    const file = path.join(root, "candidate.json");
    fs.writeFileSync(file, "candidate", { mode: 0o600 });
    const expected = custodiedSnapshot(file);
    const custody = openStorageDirectoryCustody(root);
    try {
      expect(() => durableUnlinkCustodiedFile(custody, "candidate.json", expected, { ...expected, contentHash: `sha256:${"0".repeat(64)}` })).toThrow("snapshot changed");
      expect(fs.readFileSync(file, "utf8")).toBe("candidate");
    } finally { custody.close(); }
  });

  (process.platform === "linux" ? it : it.skip)("deletes an exact confirmed full snapshot without maintaining unrelated files", () => {
    const root = tempRoot();
    const file = path.join(root, "candidate.json");
    fs.writeFileSync(file, "candidate", { mode: 0o600 });
    fs.writeFileSync(path.join(root, ".retained.tmp"), "retained");
    const expected = custodiedSnapshot(file);
    const custody = openStorageDirectoryCustody(root);
    try { durableUnlinkCustodiedFile(custody, "candidate.json", expected, expected); }
    finally { custody.close(); }
    expect(fs.readdirSync(root)).toEqual([".retained.tmp"]);
    expect(fs.readFileSync(path.join(root, ".retained.tmp"), "utf8")).toBe("retained");
  });

  it("uses a narrow tolerance only for schema-v1 uptime process identities", () => {
    expect(processStartIdentitiesMatch("uptime:100000", "uptime:102000")).toBe(true);
    expect(processStartIdentitiesMatch("uptime:100000", "uptime:102001")).toBe(false);
    expect(processStartIdentitiesMatch("linux:123", "linux:123")).toBe(true);
    expect(processStartIdentitiesMatch("linux:123", "linux:124")).toBe(false);
    expect(processStartIdentitiesMatch("windows:100000", "uptime:100000")).toBe(true);
    expect(processStartIdentitiesMatch("windows:100000", "uptime:102001")).toBe(false);
    expect(processStartIdentitiesMatch("windows:invalid", "windows:invalid")).toBe(false);
    expect(processStartIdentitiesMatch("windows:0100000", "windows:100000")).toBe(false);
    expect(processOwnerGenerationState({ pid: process.pid, process_start_id: "linux:invalid" })).toBe("uncertain");
  });

  it("compares exact BigInt identities with only the native Windows device exception", () => {
    const inode = 18_446_744_073_709_551_557n;
    expect(sameFileObjectIdentity({ dev: 0n, ino: inode }, { dev: 91n, ino: inode }, "win32")).toBe(true);
    expect(sameFileObjectIdentity({ dev: 91n, ino: inode }, { dev: 0n, ino: inode }, "win32")).toBe(true);
    expect(sameFileObjectIdentity({ dev: 0n, ino: inode }, { dev: 0n, ino: inode }, "win32")).toBe(true);
    expect(sameFileObjectIdentity({ dev: 91n, ino: inode }, { dev: 91n, ino: inode }, "linux")).toBe(true);

    expect(sameFileObjectIdentity({ dev: 0n, ino: 0n }, { dev: 91n, ino: 0n }, "win32")).toBe(false);
    expect(sameFileObjectIdentity({ dev: 0n, ino: inode }, { dev: 91n, ino: inode + 1n }, "win32")).toBe(false);
    expect(sameFileObjectIdentity({ dev: 91n, ino: inode }, { dev: 92n, ino: inode }, "win32")).toBe(false);
    expect(sameFileObjectIdentity({ dev: 0n, ino: inode }, { dev: 91n, ino: inode }, "linux")).toBe(false);
    expect(sameFileObjectIdentity({ dev: 0n, ino: inode }, { dev: 0n, ino: inode }, "linux")).toBe(false);
  });

  it("tolerates an exact regular publication temp disappearing after directory enumeration", () => {
    const root = tempRoot();
    const file = publisherTemp(root);
    fs.writeFileSync(file, "data");

    const files = secureRegularFiles(root, root, (name) => name === path.basename(file), {
      afterEntryEnumerated: (enumerated) => fs.unlinkSync(enumerated),
    });

    expect(files).toEqual([]);
  });

  it("does not tolerate a non-publication file disappearing after enumeration", () => {
    const root = tempRoot();
    const file = path.join(root, "disappearing.json");
    fs.writeFileSync(file, "data");

    expect(() => secureRegularFiles(root, root, (name) => name === "disappearing.json", {
      afterEntryEnumerated: (enumerated) => fs.unlinkSync(enumerated),
    })).toThrow();
  });

  it("tolerates an exact regular publication temp disappearing after lstat before realpath", () => {
    const root = tempRoot();
    const file = publisherTemp(root);
    fs.writeFileSync(file, "data");

    const files = secureRegularFiles(root, root, (name) => name === path.basename(file), {
      afterEntryLstat: (observed) => fs.unlinkSync(observed),
    });

    expect(files).toEqual([]);
  });

  it("tolerates an exact regular publication temp disappearing after realpath before post-stat", () => {
    const root = tempRoot();
    const file = publisherTemp(root);
    fs.writeFileSync(file, "data");

    const files = secureRegularFiles(root, root, (name) => name === path.basename(file), {
      afterEntryRealpath: (observed) => fs.unlinkSync(observed),
    });

    expect(files).toEqual([]);
  });

  it("does not tolerate non-temp disappearance during realpath or post-stat validation", () => {
    for (const phase of ["realpath", "post-stat"] as const) {
      const root = tempRoot();
      const file = path.join(root, `${phase}.json`);
      fs.writeFileSync(file, "data");
      const options = phase === "realpath"
        ? { afterEntryLstat: (observed: string) => fs.unlinkSync(observed) }
        : { afterEntryRealpath: (observed: string) => fs.unlinkSync(observed) };

      expect(() => secureRegularFiles(root, root, (name) => name === path.basename(file), options)).toThrow();
    }
  });

  it("fails closed on identity replacement after realpath", () => {
    const root = tempRoot();
    const file = publisherTemp(root);
    const replacement = path.join(root, "replacement.tmp");
    fs.writeFileSync(file, "first");
    fs.writeFileSync(replacement, "replacement");

    expect(() => secureRegularFiles(root, root, (name) => name === path.basename(file), {
      afterEntryRealpath: (observed) => {
        fs.unlinkSync(observed);
        fs.renameSync(replacement, observed);
      },
    })).toThrow("Storage file identity changed during enumeration");
  });

  it("fails closed when an enumerated regular file changes to a non-file", () => {
    const root = tempRoot();
    const file = path.join(root, "changed.json");
    fs.writeFileSync(file, "data");

    expect(() => secureRegularFiles(root, root, (name) => name === "changed.json", {
      afterEntryEnumerated: (enumerated) => {
        fs.unlinkSync(enumerated);
        fs.mkdirSync(enumerated);
      },
    })).toThrow("Unsafe storage file");
  });

  it("fails closed on directory and symlink Dirents with exact publication-temp names", () => {
    const root = tempRoot();
    const directory = publisherTemp(root);
    fs.mkdirSync(directory);
    expect(() => secureRegularFiles(root, root, () => true)).toThrow("Unsafe storage file");
    fs.rmdirSync(directory);

    const target = path.join(root, "target.json");
    const symlink = publisherTemp(root);
    fs.writeFileSync(target, "target");
    try {
      fs.symlinkSync(target, symlink);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "EPERM") return;
      throw error;
    }
    expect(() => secureRegularFiles(root, root, () => true)).toThrow("Unsafe storage file");
  });

  it("accepts redundant publisher-temp disappearance before stat only while the same final identity remains", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    fs.writeFileSync(final, "published");
    const temp = publisherTemp(root);
    fs.linkSync(final, temp);
    const fsync = vi.spyOn(fs, "fsyncSync");

    try {
      maintainPublisherTemps(root, root, {
        beforeRedundantTempStat: (observedTemp, observedFinal) => {
          expect(observedTemp).toBe(temp);
          expect(observedFinal).toBe(final);
          fs.unlinkSync(observedTemp);
        },
      });

      expect(fs.readFileSync(final, "utf8")).toBe("published");
      expect(fsync).toHaveBeenCalledTimes(1);
    } finally {
      fsync.mockRestore();
    }
  });

  it("accepts redundant publisher-temp ENOENT from unlink, revalidates final, and fsyncs", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    fs.writeFileSync(final, "published");
    const temp = publisherTemp(root);
    fs.linkSync(final, temp);
    const fsync = vi.spyOn(fs, "fsyncSync");

    try {
      maintainPublisherTemps(root, root, {
        beforeRedundantTempUnlink: (observedTemp) => fs.unlinkSync(observedTemp),
      });

      expect(fs.readFileSync(final, "utf8")).toBe("published");
      expect(fsync).toHaveBeenCalledTimes(1);
    } finally {
      fsync.mockRestore();
    }
  });

  it("never suppresses redundant publisher-temp directory fsync failures", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    fs.writeFileSync(final, "published");
    const temp = publisherTemp(root);
    fs.linkSync(final, temp);
    const fsyncError = Object.assign(new Error("injected fsync failure"), { code: "ENOENT" });
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation(() => { throw fsyncError; });

    try {
      expect(() => maintainPublisherTemps(root, root)).toThrow(fsyncError);
    } finally {
      fsync.mockRestore();
    }
  });

  it("fails closed when both a redundant publisher temp and its verified final disappear", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    fs.writeFileSync(final, "published");
    const temp = publisherTemp(root);
    fs.linkSync(final, temp);

    expect(() => maintainPublisherTemps(root, root, {
      beforeRedundantTempStat: (observedTemp, observedFinal) => {
        fs.unlinkSync(observedTemp);
        fs.unlinkSync(observedFinal);
      },
    })).toThrow();
  });

  it("durably cleans a verified publication when its temp disappears between cleanup stat and unlink", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    const nativeFsync = fs.fsyncSync.bind(fs);
    let cleanupStarted = false;
    let cleanupFsyncs = 0;
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation((handle) => {
      if (cleanupStarted) cleanupFsyncs += 1;
      return nativeFsync(handle);
    });

    try {
      writeNewFileDurable(root, final, "published", {
        beforeTempCleanupUnlink: (temp, observedFinal) => {
          expect(observedFinal).toBe(final);
          cleanupStarted = true;
          fs.unlinkSync(temp);
        },
      });

      expect(fs.readFileSync(final, "utf8")).toBe("published");
      expect(cleanupFsyncs).toBe(1);
      expect(fs.readdirSync(root).filter((name) => name.startsWith(".publish-v1-"))).toEqual([]);
    } finally {
      fsync.mockRestore();
    }
  });

  it("durably cleans a verified publication when its temp disappears before cleanup stat", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    const nativeFsync = fs.fsyncSync.bind(fs);
    let cleanupStarted = false;
    let cleanupFsyncs = 0;
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation((handle) => {
      if (cleanupStarted) cleanupFsyncs += 1;
      return nativeFsync(handle);
    });

    try {
      writeNewFileDurable(root, final, "published", {
        beforeTempCleanupStat: (temp) => {
          cleanupStarted = true;
          fs.unlinkSync(temp);
        },
      });

      expect(fs.readFileSync(final, "utf8")).toBe("published");
      expect(cleanupFsyncs).toBe(1);
    } finally {
      fsync.mockRestore();
    }
  });

  it("handles the same unlink race while cleaning an unpublished temp", () => {
    const root = tempRoot();
    const final = path.join(root, "not-published.json");
    const publicationError = new Error("injected pre-publication failure");

    expect(() => writeNewFileDurable(root, final, "data", {
      afterTempFsync: () => { throw publicationError; },
      beforeTempCleanupUnlink: (temp) => fs.unlinkSync(temp),
    })).toThrow(publicationError);
    expect(fs.existsSync(final)).toBe(false);
    expect(fs.readdirSync(root).filter((name) => name.startsWith(".publish-v1-"))).toEqual([]);
  });

  it("does not suppress writeNewFileDurable cleanup fsync failures", () => {
    const root = tempRoot();
    const final = path.join(root, "published.json");
    const nativeFsync = fs.fsyncSync.bind(fs);
    const fsyncError = Object.assign(new Error("injected cleanup fsync failure"), { code: "ENOENT" });
    let cleanupStarted = false;
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation((handle) => {
      if (cleanupStarted) throw fsyncError;
      return nativeFsync(handle);
    });

    try {
      expect(() => writeNewFileDurable(root, final, "published", {
        beforeTempCleanupUnlink: () => { cleanupStarted = true; },
      })).toThrow(fsyncError);
    } finally {
      fsync.mockRestore();
    }
  });

  it("serializes with an actual child process and never removes the live owner", async () => {
    const root = tempRoot();
    const child = startChild(["lock", root, "150"]);
    await childLine(child);
    const started = Date.now();

    const lock = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    expect(Date.now() - started).toBeGreaterThanOrEqual(75);
    expect(fs.existsSync(path.join(root, ".test.lock", "owner.json"))).toBe(true);
    lock.release();
    await childExit(child);
  });

  it("atomically reclaims a crashed child lock after owner death is confirmed", async () => {
    const root = tempRoot();
    const child = startChild(["lock", root, "crash"]);
    await childLine(child);
    await childExit(child);

    const lock = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 0 });
    expect(fs.readdirSync(root).filter((name) => name.includes(".tomb-"))).toEqual([]);
    lock.release();
  });

  it("does not reclaim when the exact owner changes before final revalidation", async () => {
    const root = tempRoot();
    const child = startChild(["lock", root, "crash"]);
    await childLine(child);
    await childExit(child);
    const ownerFile = path.join(root, ".test.lock", "owner.json");
    const replacement = { ...currentProcessOwner(), acquired_at: new Date(Date.now() - 60_000).toISOString() };
    let replaced = false;

    expect(() => acquireStorageLock(root, ".test.lock", {
      timeoutMs: 250,
      staleAfterMs: 0,
      beforeLockOwnerRevalidation: () => {
        if (replaced) return;
        replaced = true;
        fs.writeFileSync(ownerFile, `${JSON.stringify(replacement)}\n`);
      },
    })).toThrow("Storage lock unavailable");
    expect(replaced).toBe(true);
    expect(JSON.parse(fs.readFileSync(ownerFile, "utf8"))).toEqual(replacement);
  });

  it("accepts an owned generation concurrently published before its rename syscall", () => {
    const root = tempRoot();
    const active = path.join(root, ".test.lock");
    let injected = false;
    const lock = acquireStorageLock(root, ".test.lock", {
      beforeGenerationRename: () => {
        if (injected) return;
        injected = true;
        const generation = path.join(root, fs.readdirSync(root).find((name) => name.startsWith(".test.lock.gen-"))!);
        fs.renameSync(generation, active);
      },
    });
    expect(injected).toBe(true);
    lock.release();
    expect(fs.readdirSync(root)).toEqual([]);
  });

  it("resumes an affected-version release marker after acquiring a new gate", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-marker-crash", root]);
    expect(await childLine(child)).toBe("acquired");
    await childExit(child);
    expect(fs.readdirSync(path.join(root, ".test.lock")).filter((name) => name.startsWith("transition-"))).toHaveLength(1);
    fs.rmSync(path.join(root, ".test.lock.transition-gate"), { recursive: true });

    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  });

  it("resumes an affected-version reclaim marker after acquiring a new gate", async () => {
    const root = tempRoot();
    const crashed = startChild(["lock", root, "crash"]);
    await childLine(crashed);
    await childExit(crashed);
    await childExit(startChild(["lock-reclaim-marker-crash", root], { ...process.env, SGG_TEST_STALE_AFTER_MS: "0" }));
    expect(fs.readdirSync(path.join(root, ".test.lock")).filter((name) => name.startsWith("transition-"))).toHaveLength(1);
    fs.rmSync(path.join(root, ".test.lock.transition-gate"), { recursive: true });

    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  });

  it("blocks a successor while an old marker owner is live and resumes only after confirmed death", async () => {
    const root = tempRoot();
    const ownerExit = path.join(root, "old-resumer.exit");
    const ownerRename = path.join(root, "old-resumer.rename");
    const blockedSignal = path.join(root, "transition.blocked");
    const oldResumer = startChild(["transition-old-resumer-hold", root]);
    const transitionOwner = JSON.parse(await childLine(oldResumer));
    const activeOwner = currentProcessOwner();
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner.json"), `${JSON.stringify(activeOwner)}\n`);
    fs.writeFileSync(path.join(active, `transition-${activeOwner.nonce}.json`), `${JSON.stringify({
      schema_version: 1,
      nonce: activeOwner.nonce,
      operation: "release",
      transition_nonce: crypto.randomUUID(),
      pid: transitionOwner.pid,
      process_start_id: transitionOwner.process_start_id,
      transitioned_at: transitionOwner.acquired_at,
    })}\n`);

    const successor = startChild(["lock-block-signal", root, blockedSignal]);
    let acquired = false;
    const successorLine = childLine(successor).then((line) => { acquired = true; return line; });
    await waitFor(() => fs.existsSync(blockedSignal));
    expect(acquired).toBe(false);
    expect(JSON.parse(fs.readFileSync(path.join(active, "owner.json"), "utf8")).nonce).toBe(activeOwner.nonce);

    fs.writeFileSync(ownerRename, "rename");
    await waitFor(() => !fs.existsSync(active) && fs.readdirSync(root).some((name) => name.startsWith(".test.lock.tomb-")));
    expect(acquired).toBe(false);

    fs.writeFileSync(ownerExit, "exit");
    await childExit(oldResumer);
    expect(await successorLine).toBe("acquired");
    await childExit(successor);
    expect(lockArtifacts(root)).toEqual([]);
  }, 30_000);

  it("keeps an uncertain transition owner fail closed", () => {
    const root = tempRoot();
    const activeOwner = currentProcessOwner();
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner.json"), `${JSON.stringify(activeOwner)}\n`);
    fs.writeFileSync(path.join(active, `transition-${activeOwner.nonce}.json`), `${JSON.stringify({
      schema_version: 1,
      nonce: activeOwner.nonce,
      operation: "release",
      transition_nonce: crypto.randomUUID(),
      pid: process.pid,
      process_start_id: null,
      transitioned_at: new Date().toISOString(),
    })}\n`);

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 0 })).toThrow("Storage lock unavailable");
    expect(fs.existsSync(path.join(active, "owner.json"))).toBe(true);
    expect(fs.readdirSync(active).filter((name) => name.startsWith("transition-"))).toHaveLength(1);
  }, 10_000);

  it("recovers a confirmed-dead legacy PID lock under the transition gate", async () => {
    const root = tempRoot();
    const ownerExit = path.join(root, "legacy.exit");
    const ownerProcess = startChild(["transition-owner-hold", root, ownerExit]);
    const legacyOwner = JSON.parse(await childLine(ownerProcess));
    fs.writeFileSync(ownerExit, "exit");
    await childExit(ownerProcess);
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner"), `${legacyOwner.pid}\n`);
    const old = new Date(Date.now() - 60_000);
    fs.utimesSync(active, old, old);

    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 0 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  }, 10_000);

  it("keeps a live or uncertain legacy PID lock fail closed", async () => {
    const root = tempRoot();
    const ownerExit = path.join(root, "legacy.exit");
    const ownerProcess = startChild(["transition-owner-hold", root, ownerExit]);
    const legacyOwner = JSON.parse(await childLine(ownerProcess));
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner"), `${legacyOwner.pid}\n`);
    const old = new Date(Date.now() - 60_000);
    fs.utimesSync(active, old, old);

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 0 })).toThrow("Storage lock unavailable");
    expect(fs.readFileSync(path.join(active, "owner"), "utf8").trim()).toBe(String(legacyOwner.pid));
    fs.writeFileSync(ownerExit, "exit");
    await childExit(ownerProcess);
  }, 10_000);

  it("rejects a malformed legacy transition marker", async () => {
    const root = tempRoot();
    const ownerExit = path.join(root, "legacy.exit");
    const ownerProcess = startChild(["transition-owner-hold", root, ownerExit]);
    const legacyOwner = JSON.parse(await childLine(ownerProcess));
    fs.writeFileSync(ownerExit, "exit");
    await childExit(ownerProcess);
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner"), `${legacyOwner.pid}\n`);
    const nonce = `legacy-${legacyOwner.pid}`;
    fs.writeFileSync(path.join(active, `transition-${nonce}.json`), `${JSON.stringify({
      schema_version: 1,
      nonce: `legacy-0${legacyOwner.pid}`,
      operation: "reclaim",
      transition_nonce: crypto.randomUUID(),
      pid: process.pid,
      process_start_id: currentProcessOwner().process_start_id,
      transitioned_at: new Date().toISOString(),
    })}\n`);

    const before = transitionMarkerSnapshot(active);
    const rootNames = fs.readdirSync(root).sort();
    withTransitionMarkerValidationClock((afterTransitionGate) => {
      expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 0, afterTransitionGate })).toThrow("Lock transition marker is invalid");
    });
    expect(fs.existsSync(active)).toBe(true);
    expect(transitionMarkerSnapshot(active)).toEqual(before);
    expect(fs.readdirSync(root).sort()).toEqual(rootNames);
  }, 10_000);

  it("cleans a deterministic transition tomb without touching a successor active lock", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-marker-crash", root]);
    await childLine(child);
    await childExit(child);
    fs.rmSync(path.join(root, ".test.lock.transition-gate"), { recursive: true });
    const active = path.join(root, ".test.lock");
    const markerName = fs.readdirSync(active).find((name) => name.startsWith("transition-"))!;
    const transition = JSON.parse(fs.readFileSync(path.join(active, markerName), "utf8"));
    fs.renameSync(active, `${active}.tomb-${transition.transition_nonce}`);
    const successor = currentProcessOwner();
    fs.mkdirSync(active, { mode: 0o700 });
    fs.writeFileSync(path.join(active, "owner.json"), `${JSON.stringify(successor)}\n`);

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 10 })).toThrow("Storage lock unavailable");
    expect(JSON.parse(fs.readFileSync(path.join(active, "owner.json"), "utf8")).nonce).toBe(successor.nonce);
    expect(fs.readdirSync(root).filter((name) => name.includes(".tomb-"))).toEqual([]);
  });

  it("fails closed on multiple transition markers", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-marker-crash", root]);
    await childLine(child);
    await childExit(child);
    fs.rmSync(path.join(root, ".test.lock.transition-gate"), { recursive: true });
    const active = path.join(root, ".test.lock");
    fs.writeFileSync(path.join(active, `transition-${crypto.randomUUID()}.json`), "{}\n");
    const before = transitionMarkerSnapshot(active);
    withTransitionMarkerValidationClock((afterTransitionGate) => {
      expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 10, afterTransitionGate })).toThrow("Lock transition marker is ambiguous");
    });
    expect(transitionMarkerSnapshot(active)).toEqual(before);
    expect(fs.readdirSync(root)).toEqual([".test.lock"]);
  });

  it("retires a complete transition gate after its exact owner dies", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-gate-crash", root]);
    expect(await childLine(child)).toBe("acquired");
    await childExit(child);

    const gate = path.join(root, ".test.lock.transition-gate");
    expect(JSON.parse(fs.readFileSync(path.join(gate, "owner.json"), "utf8")).nonce).toMatch(/^[0-9a-f-]{36}$/);
    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  });

  it("keeps a successor blocked after old-lock cleanup until the exact gate holder exits", async () => {
    const root = tempRoot();
    const signal = path.join(root, "release.signal");
    const staleResumer = startChild(["lock-release-cleanup-hold", root, signal]);
    expect(await childLine(staleResumer)).toBe("acquired");
    await waitFor(() => fs.existsSync(path.join(root, ".test.lock.transition-gate")) && !fs.existsSync(path.join(root, ".test.lock")));

    const successor = startChild(["lock", root, "25"]);
    let successorAcquired = false;
    const acquired = childLine(successor).then((line) => { successorAcquired = true; return line; });
    expect(successorAcquired).toBe(false);
    expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);

    fs.writeFileSync(signal, "continue");
    await childExit(staleResumer);
    expect(await acquired).toBe("acquired");
    expect(fs.existsSync(path.join(root, ".test.lock", "owner.json"))).toBe(true);
    await childExit(successor);
    expect(lockArtifacts(root).filter((name) => name !== "release.signal")).toEqual([]);
  });

  it("recovers when a gate holder crashes after deleting the old lock", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-cleanup-crash", root]);
    expect(await childLine(child)).toBe("acquired");
    await childExit(child);

    expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);
    expect(fs.existsSync(path.join(root, ".test.lock.transition-gate", "owner.json"))).toBe(true);
    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  });

  it("recovers after a successor crashes immediately after retiring a dead gate", async () => {
    const root = tempRoot();
    const owner = startChild(["lock-release-gate-crash", root]);
    expect(await childLine(owner)).toBe("acquired");
    await childExit(owner);

    await childExit(startChild(["lock-dead-gate-retirement-crash", root]));
    expect(fs.readdirSync(root).some((name) => name.startsWith(".test.lock.transition-gate.gen-"))).toBe(true);

    const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    successor.release();
    expect(lockArtifacts(root)).toEqual([]);
  }, 10_000);

  it("keeps a live transition gate fail closed", () => {
    const root = tempRoot();
    const gate = path.join(root, ".test.lock.transition-gate");
    fs.mkdirSync(gate, { mode: 0o700 });
    fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(currentProcessOwner())}\n`);

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 100, staleAfterMs: 0 })).toThrow("transition gate is live");
    expect(fs.existsSync(gate)).toBe(true);
  });

  it("keeps an uncertain transition gate fail closed", () => {
    const root = tempRoot();
    const gate = path.join(root, ".test.lock.transition-gate");
    const owner = { ...currentProcessOwner(), process_start_id: null };
    fs.mkdirSync(gate, { mode: 0o700 });
    fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(owner)}\n`);

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 100, staleAfterMs: 0 })).toThrow("transition gate is uncertain");
    expect(JSON.parse(fs.readFileSync(path.join(gate, "owner.json"), "utf8"))).toEqual(owner);
  });

  it("keeps a malformed dead transition gate fail closed", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-gate-crash", root]);
    expect(await childLine(child)).toBe("acquired");
    await childExit(child);
    const gate = path.join(root, ".test.lock.transition-gate");
    fs.writeFileSync(path.join(gate, "unexpected"), "unexpected");

    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 0 })).toThrow("transition gate is incomplete or invalid");
    expect(fs.existsSync(gate)).toBe(true);
  }, 10_000);

  it("fails closed when a dead transition gate identity changes before retirement", async () => {
    const root = tempRoot();
    const child = startChild(["lock-release-gate-crash", root]);
    expect(await childLine(child)).toBe("acquired");
    await childExit(child);
    const gate = path.join(root, ".test.lock.transition-gate");
    const moved = `${gate}.replaced`;

    expect(() => acquireStorageLock(root, ".test.lock", {
      timeoutMs: 500,
      staleAfterMs: 0,
      beforeDeadTransitionGateRetirement: () => {
        fs.renameSync(gate, moved);
        fs.mkdirSync(gate, { mode: 0o700 });
        fs.writeFileSync(path.join(gate, "owner.json"), fs.readFileSync(path.join(moved, "owner.json")));
      },
    })).toThrow("identity changed before dead-owner retirement");
    expect(fs.existsSync(gate)).toBe(true);
    expect(fs.existsSync(moved)).toBe(true);
  }, 10_000);

  it.each(["live", "uncertain"])("discards a dead gate observation when a complete %s successor is published before retirement", state => {
    const root = tempRoot();
    const { gate, owner } = deadTransitionGate(root);
    const moved = `${gate}.fixture-prior`;
    const successor = { ...currentProcessOwner(), ...(state === "uncertain" ? { process_start_id: null } : {}) };
    let handoffs = 0;
    expect(() => acquireStorageLock(root, ".test.lock", {
      timeoutMs: 100, staleAfterMs: 0,
      beforeDeadTransitionGateRetirement: () => {
        handoffs++;
        fs.renameSync(gate, moved);
        fs.mkdirSync(gate, { mode: 0o700 });
        fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(successor)}\n`);
      },
    })).toThrow(`transition gate is ${state}`);
    expect(handoffs).toBe(1);
    expect(JSON.parse(fs.readFileSync(path.join(gate, "owner.json"), "utf8"))).toEqual(successor);
    expect(JSON.parse(fs.readFileSync(path.join(moved, "owner.json"), "utf8"))).toEqual(owner);
    expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);
    expect(fs.readdirSync(root).sort()).toEqual([path.basename(gate), path.basename(moved)].sort());
  });

  it.each(["in-place-owner-change", "incomplete-successor"])("keeps non-protocol gate changes fail closed: %s", kind => {
    const root = tempRoot();
    const { gate } = deadTransitionGate(root);
    const successor = currentProcessOwner();
    expect(() => acquireStorageLock(root, ".test.lock", {
      timeoutMs: 100, staleAfterMs: 0,
      beforeDeadTransitionGateRetirement: () => {
        if (kind === "incomplete-successor") {
          fs.renameSync(gate, `${gate}.fixture-prior`);
          fs.mkdirSync(gate, { mode: 0o700 });
          fs.writeFileSync(path.join(gate, "unexpected"), "not a complete gate");
        }
        fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(successor)}\n`);
      },
    })).toThrow(kind === "in-place-owner-change" ? "identity changed before dead-owner retirement" : "transition gate is incomplete or invalid");
    expect(JSON.parse(fs.readFileSync(path.join(gate, "owner.json"), "utf8"))).toEqual(successor);
    expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);
  });

  it.each(["successor", "same-owner-clone"])("does not combine directory and owner reads across a gate handoff: %s", kind => {
    const root = tempRoot();
    const { gate, owner } = deadTransitionGate(root);
    const moved = `${gate}.fixture-prior`;
    const successor = kind === "successor" ? currentProcessOwner() : owner;
    const ownerFile = path.join(gate, "owner.json");
    const originalOpen = fs.openSync;
    const originalClose = fs.closeSync;
    let ownerFd: number | undefined;
    let changed = false;
    let retirements = 0;
    const opened = vi.spyOn(fs, "openSync").mockImplementation((...args) => {
      const fd = Reflect.apply(originalOpen, fs, args) as number;
      if (!changed && args[0] === ownerFile) ownerFd = fd;
      return fd;
    });
    const closed = vi.spyOn(fs, "closeSync").mockImplementation(fd => {
      originalClose(fd);
      if (!changed && fd === ownerFd) {
        changed = true;
        fs.renameSync(gate, moved);
        fs.mkdirSync(gate, { mode: 0o700 });
        fs.writeFileSync(ownerFile, `${JSON.stringify(successor)}\n`);
      }
    });
    try {
      expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 100, staleAfterMs: 0, beforeDeadTransitionGateRetirement: () => { retirements++; } })).toThrow(kind === "successor" ? "transition gate is live" : "identity changed while reading owner");
      expect(changed).toBe(true);
      expect(retirements).toBe(0);
      expect(JSON.parse(fs.readFileSync(ownerFile, "utf8"))).toEqual(successor);
      expect(fs.existsSync(moved)).toBe(true);
      expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);
    } finally { opened.mockRestore(); closed.mockRestore(); }
  });

  it("cleans only its exact retired dead gate if a successor publishes before retirement finishes", () => {
    const root = tempRoot();
    const { gate, owner } = deadTransitionGate(root);
    const retired = `${gate}.gen-${owner.nonce}`;
    const successor = currentProcessOwner();
    const originalRename = fs.renameSync;
    let changed = false;
    const rename = vi.spyOn(fs, "renameSync").mockImplementation((from, to) => {
      originalRename(from, to);
      if (!changed && from === gate && to === retired) {
        changed = true;
        fs.mkdirSync(gate, { mode: 0o700 });
        fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(successor)}\n`);
      }
    });
    try {
      expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 100, staleAfterMs: 0 })).toThrow("transition gate is live");
      expect(changed).toBe(true);
      expect(JSON.parse(fs.readFileSync(path.join(gate, "owner.json"), "utf8"))).toEqual(successor);
      expect(fs.existsSync(retired)).toBe(false);
      expect(fs.readdirSync(root)).toEqual([path.basename(gate)]);
    } finally { rename.mockRestore(); }
  });

  it("discards another namespace publication during successor validation without retiring either successor", () => {
    const root = tempRoot();
    const { gate } = deadTransitionGate(root);
    const first = currentProcessOwner();
    const second = currentProcessOwner();
    const ownerFile = path.join(gate, "owner.json");
    const originalOpen = fs.openSync;
    const originalClose = fs.closeSync;
    let ownerFd: number | undefined;
    let stage = 0;
    let successorReads = 0;
    const opened = vi.spyOn(fs, "openSync").mockImplementation((...args) => {
      const fd = Reflect.apply(originalOpen, fs, args) as number;
      if (stage === 1 && args[0] === ownerFile && typeof args[1] === "number"
        && (args[1] & (fs.constants.O_WRONLY | fs.constants.O_RDWR)) === 0) ownerFd = fd;
      return fd;
    });
    const closed = vi.spyOn(fs, "closeSync").mockImplementation(fd => {
      originalClose(fd);
      const wasOwnerRead = fd === ownerFd;
      if (wasOwnerRead) ownerFd = undefined;
      if (stage === 1 && wasOwnerRead && ++successorReads === 2) {
        stage = 2;
        fs.renameSync(gate, `${gate}.fixture-first-successor`);
        fs.mkdirSync(gate, { mode: 0o700 });
        fs.writeFileSync(ownerFile, `${JSON.stringify(second)}\n`);
      }
    });
    try {
      expect(() => acquireStorageLock(root, ".test.lock", {
        timeoutMs: 100, staleAfterMs: 0,
        beforeDeadTransitionGateRetirement: () => {
          stage = 1;
          fs.renameSync(gate, `${gate}.fixture-prior`);
          fs.mkdirSync(gate, { mode: 0o700 });
          fs.writeFileSync(ownerFile, `${JSON.stringify(first)}\n`);
        },
      })).toThrow("transition gate is live");
      expect(stage).toBe(2);
      expect(successorReads).toBe(2);
      expect(JSON.parse(fs.readFileSync(ownerFile, "utf8"))).toEqual(second);
      expect(JSON.parse(fs.readFileSync(path.join(`${gate}.fixture-first-successor`, "owner.json"), "utf8"))).toEqual(first);
      expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(false);
    } finally { opened.mockRestore(); closed.mockRestore(); }
  });

  it("reobserves absence if a successor releases during its completeness read", () => {
    const root = tempRoot();
    const { gate } = deadTransitionGate(root);
    const successor = currentProcessOwner();
    const moved = `${gate}.fixture-successor`;
    const originalRead = fs.readdirSync;
    let armed = false;
    let released = false;
    const readdir = vi.spyOn(fs, "readdirSync").mockImplementation((...args) => {
      const result = Reflect.apply(originalRead, fs, args);
      if (armed && !released && args[0] === gate) {
        released = true;
        fs.renameSync(gate, moved);
      }
      return result;
    });
    let lock: ReturnType<typeof acquireStorageLock> | undefined;
    try {
      lock = acquireStorageLock(root, ".test.lock", {
        timeoutMs: 100, staleAfterMs: 0,
        beforeDeadTransitionGateRetirement: () => {
          fs.renameSync(gate, `${gate}.fixture-prior`);
          fs.mkdirSync(gate, { mode: 0o700 });
          fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(successor)}\n`);
          armed = true;
        },
      });
      expect(released).toBe(true);
      expect(fs.existsSync(path.join(root, ".test.lock"))).toBe(true);
      expect(JSON.parse(fs.readFileSync(path.join(moved, "owner.json"), "utf8"))).toEqual(successor);
    } finally { readdir.mockRestore(); lock?.release(); }
    expect(fs.readdirSync(root).sort()).toEqual([path.basename(`${gate}.fixture-prior`), path.basename(moved)].sort());
  });

  it("serializes two successors racing after dead-gate retirement", async () => {
    const root = tempRoot();
    const owner = startChild(["lock-release-gate-crash", root]);
    expect(await childLine(owner)).toBe("acquired");
    await childExit(owner);
    const signal = path.join(root, "dead-gate-retired.signal");
    const first = startChild(["lock-dead-gate-retirement-hold", root, signal]);
    await waitFor(() => fs.existsSync(signal));

    const second = startChild(["lock", root, "150"]);
    expect(await childLine(second)).toBe("acquired");
    let firstAcquired = false;
    const firstLine = childLine(first).then((line) => { firstAcquired = true; return line; });
    fs.writeFileSync(`${signal}.continue`, "continue");
    await new Promise((resolve) => setTimeout(resolve, 25));
    expect(firstAcquired).toBe(false);

    await childExit(second);
    expect(await firstLine).toBe("acquired");
    await childExit(first);
    fs.rmSync(signal);
    fs.rmSync(`${signal}.continue`);
    expect(lockArtifacts(root)).toEqual([]);
  }, 30_000);

  it("rejects non-canonical UUID-shaped lock and transition owners", () => {
    const root = tempRoot();
    const active = path.join(root, ".test.lock");
    fs.mkdirSync(active, { mode: 0o700 });
    const owner = currentProcessOwner();
    fs.writeFileSync(path.join(active, "owner.json"), `${JSON.stringify({ ...owner, nonce: "------------------------------------" })}\n`);
    expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 500, staleAfterMs: 10 })).toThrow();
    expect(fs.existsSync(active)).toBe(true);

    const markerRoot = tempRoot();
    const markerActive = path.join(markerRoot, ".test.lock");
    const markerOwner = currentProcessOwner();
    fs.mkdirSync(markerActive, { mode: 0o700 });
    fs.writeFileSync(path.join(markerActive, "owner.json"), `${JSON.stringify(markerOwner)}\n`);
    fs.writeFileSync(path.join(markerActive, `transition-${markerOwner.nonce}.json`), `${JSON.stringify({
      schema_version: 1,
      nonce: markerOwner.nonce,
      operation: "release",
      transition_nonce: "------------------------------------",
      pid: process.pid,
      process_start_id: markerOwner.process_start_id,
      transitioned_at: new Date().toISOString(),
    })}\n`);
    const markerBefore = transitionMarkerSnapshot(markerActive);
    const markerRootNames = fs.readdirSync(markerRoot).sort();
    withTransitionMarkerValidationClock((afterTransitionGate) => {
      expect(() => acquireStorageLock(markerRoot, ".test.lock", { timeoutMs: 500, staleAfterMs: 10, afterTransitionGate })).toThrow("Lock transition marker is invalid");
    });
    expect(transitionMarkerSnapshot(markerActive)).toEqual(markerBefore);
    expect(fs.readdirSync(markerRoot).sort()).toEqual(markerRootNames);

    const tombRoot = tempRoot();
    fs.mkdirSync(path.join(tombRoot, `.test.lock.tomb-${"-".repeat(36)}`), { mode: 0o700 });
    expect(() => acquireStorageLock(tombRoot, ".test.lock", { timeoutMs: 500, staleAfterMs: 10 })).toThrow("Lock tombstone name is invalid");
  });

  it("keeps malformed process identities fail closed in owners and transitions", () => {
    for (const processStartId of ["linux:invalid", "windows:-1", "uptime:9007199254740992", "windows:01700000000000"]) {
      const ownerRoot = tempRoot();
      const active = path.join(ownerRoot, ".test.lock");
      fs.mkdirSync(active, { mode: 0o700 });
      fs.writeFileSync(path.join(active, "owner.json"), `${JSON.stringify({ ...currentProcessOwner(), process_start_id: processStartId, acquired_at: new Date(Date.now() - 60_000).toISOString() })}\n`);
      expect(() => acquireStorageLock(ownerRoot, ".test.lock", { timeoutMs: 50, staleAfterMs: 0 })).toThrow();
      expect(fs.existsSync(active)).toBe(true);

      const transitionRoot = tempRoot();
      const transitionLock = path.join(transitionRoot, ".test.lock");
      const owner = currentProcessOwner();
      fs.mkdirSync(transitionLock, { mode: 0o700 });
      fs.writeFileSync(path.join(transitionLock, "owner.json"), `${JSON.stringify(owner)}\n`);
      fs.writeFileSync(path.join(transitionLock, `transition-${owner.nonce}.json`), `${JSON.stringify({
        schema_version: 1,
        nonce: owner.nonce,
        operation: "release",
        transition_nonce: crypto.randomUUID(),
        pid: process.pid,
        process_start_id: processStartId,
        transitioned_at: new Date().toISOString(),
      })}\n`);
      // This assertion tests marker validation, not whether durable gate setup
      // fits in 50 ms on the host filesystem. Restore real time at gate entry;
      // malformed-owner timeout coverage above deliberately keeps real time.
      const validationNow = Date.now();
      const preparationStarted = performance.now();
      const clock = vi.spyOn(Date, "now").mockImplementation(() => {
        if (performance.now() - preparationStarted >= 1_000) throw new Error("Malformed-marker fixture did not reach transition gate");
        return validationNow;
      });
      const gateEntered = vi.fn(() => clock.mockRestore());
      try {
        expect(() => acquireStorageLock(transitionRoot, ".test.lock", { timeoutMs: 50, staleAfterMs: 0, afterTransitionGate: gateEntered })).toThrow("Lock transition marker is invalid");
      } finally {
        clock.mockRestore();
      }
      expect(gateEntered).toHaveBeenCalledTimes(1);
      expect(fs.existsSync(transitionLock)).toBe(true);
    }
  });

  it("keeps gate-preparation deadline exhaustion distinct from malformed-marker validation", () => {
    const root = tempRoot();
    const lock = path.join(root, ".test.lock");
    const owner = currentProcessOwner();
    const marker = path.join(lock, `transition-${owner.nonce}.json`);
    const ownerBytes = `${JSON.stringify(owner)}\n`;
    const markerBytes = `${JSON.stringify({ schema_version: 1, nonce: owner.nonce, operation: "release", transition_nonce: crypto.randomUUID(), pid: process.pid, process_start_id: "linux:invalid", transitioned_at: new Date().toISOString() })}\n`;
    fs.mkdirSync(lock, { mode: 0o700 });
    fs.writeFileSync(path.join(lock, "owner.json"), ownerBytes);
    fs.writeFileSync(marker, markerBytes);
    let now = Date.now();
    const deadline = now + 50;
    const preparationStarted = performance.now();
    const clock = vi.spyOn(Date, "now").mockImplementation(() => {
      if (performance.now() - preparationStarted >= 1_000) throw new Error("Gate-deadline fixture did not terminate");
      return now;
    });
    const nativeFsync = fs.fsyncSync.bind(fs);
    const fsync = vi.spyOn(fs, "fsyncSync").mockImplementation((handle) => {
      nativeFsync(handle);
      now = deadline + 1;
    });
    const opened = vi.spyOn(fs, "openSync");
    const gateEntered = vi.fn();
    try {
      expect(() => acquireStorageLock(root, ".test.lock", { timeoutMs: 50, staleAfterMs: 0, afterTransitionGate: gateEntered })).toThrow("Storage lock unavailable");
      expect(gateEntered).not.toHaveBeenCalled();
      expect(opened.mock.calls.some(([filename]) => filename === marker)).toBe(false);
    } finally {
      opened.mockRestore();
      fsync.mockRestore();
      clock.mockRestore();
    }
    expect(fs.readFileSync(path.join(lock, "owner.json"), "utf8")).toBe(ownerBytes);
    expect(fs.readFileSync(marker, "utf8")).toBe(markerBytes);
    expect(fs.existsSync(lock)).toBe(true);
    expect(fs.readdirSync(root)).toEqual([".test.lock"]);
  });

  it("keeps two stale reclaimers from renaming the successor generation", async () => {
    const root = tempRoot();
    for (let iteration = 0; iteration < 100; iteration += 1) {
      try {
      const crashed = startChild(["lock", root, "crash"]);
      await childLine(crashed);
      await childExit(crashed);

      const childEnv = { ...process.env, SGG_TEST_STALE_AFTER_MS: "0" };
      const first = startChild(["lock", root, "10"], childEnv);
      const second = startChild(["lock", root, "10"], childEnv);
      const firstLine = childLine(first).then((line) => ({ child: first, line }));
      const secondLine = childLine(second).then((line) => ({ child: second, line }));
      const winner = await Promise.race([firstLine, secondLine]);
      expect(winner.line).toBe("acquired");
      await childExit(winner.child);
      const successorChild = winner.child === first ? await secondLine : await firstLine;
      expect(successorChild.line).toBe("acquired");
      await childExit(successorChild.child);

      const successor = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
      expect(fs.existsSync(path.join(root, ".test.lock", "owner.json"))).toBe(true);
      successor.release();
      expect(fs.readdirSync(root).filter((name) => name.includes(".tomb-") || name.includes(".gen-") || name.startsWith(".publish-v1-") || name.startsWith(".delete-v1-"))).toEqual([]);
      } catch (error) {
        throw new Error(`lock stress iteration ${iteration}; root entries=${JSON.stringify(fs.readdirSync(root).sort())}`, { cause: error });
      }
    }
  }, 600_000);

  it("fails closed if a verified deletion target is swapped to a symlink", () => {
    const root = tempRoot();
    const victim = path.join(root, "victim.json");
    const outside = path.join(os.tmpdir(), `sgg-storage-outside-${process.pid}-${Date.now()}.json`);
    fs.writeFileSync(victim, "inside");
    fs.writeFileSync(outside, "outside");
    const rename = fs.renameSync.bind(fs);
    const spy = vi.spyOn(fs, "renameSync").mockImplementation((source, destination) => {
      if (source === victim) {
        fs.unlinkSync(victim);
        fs.symlinkSync(outside, victim);
      }
      return rename(source, destination);
    });
    try {
      expect(() => durableUnlinkFile(root, victim)).toThrow();
      expect(fs.readFileSync(outside, "utf8")).toBe("outside");
    } finally {
      spy.mockRestore();
      fs.rmSync(outside, { force: true });
    }
  });

  it("refuses durable deletion when the file identity does not match its caller precondition", () => {
    const root = tempRoot();
    const victim = path.join(root, "victim.json");
    fs.writeFileSync(victim, "inside");
    const identity = secureFileStat(root, victim)!;

    expect(() => durableUnlinkFile(root, victim, { expectedIdentity: { dev: identity.dev, ino: identity.ino + 1n } })).toThrow("does not match");
    expect(fs.readFileSync(victim, "utf8")).toBe("inside");
  });

  it("fails closed on rounded legacy delete metadata without unlinking the tombstone", () => {
    const root = tempRoot();
    const victim = path.join(root, "victim.json");
    fs.writeFileSync(victim, "inside");
    expect(() => durableUnlinkFile(root, victim, { afterRename: () => { throw new Error("injected crash"); } })).toThrow("injected crash");
    const metadataPath = path.join(root, fs.readdirSync(root).find((name) => name.endsWith(".json") && name.startsWith(".delete-v1-"))!);
    const metadata = JSON.parse(fs.readFileSync(metadataPath, "utf8"));
    metadata.ino = String(Number(BigInt(metadata.ino) + 9_007_199_254_740_993n));
    fs.writeFileSync(metadataPath, `${JSON.stringify(metadata)}\n`);

    expect(() => maintainDeleteTombstones(root, root)).toThrow("Delete tombstone identity does not match metadata");
    expect(fs.readdirSync(root).filter((name) => name.endsWith(".tomb"))).toHaveLength(1);
    expect(fs.existsSync(metadataPath)).toBe(true);
  });

  it("removes old valid delete metadata with legacy ino zero when no tombstone exists", () => {
    const root = tempRoot();
    const victim = path.join(root, "victim.json");
    fs.writeFileSync(victim, "inside");
    expect(() => durableUnlinkFile(root, victim, { afterRename: () => { throw new Error("injected crash"); } })).toThrow("injected crash");
    const metadataPath = path.join(root, fs.readdirSync(root).find((name) => name.endsWith(".json") && name.startsWith(".delete-v1-"))!);
    const tombPath = path.join(root, fs.readdirSync(root).find((name) => name.endsWith(".tomb") && name.startsWith(".delete-v1-"))!);
    const metadata = JSON.parse(fs.readFileSync(metadataPath, "utf8"));
    metadata.ino = "0";
    metadata.created_at = new Date(Date.now() - 60_000).toISOString();
    fs.writeFileSync(metadataPath, `${JSON.stringify(metadata)}\n`);
    fs.unlinkSync(tombPath);

    maintainDeleteTombstones(root, root);
    expect(fs.existsSync(metadataPath)).toBe(false);
  });

  it("keeps legacy ino zero delete metadata fail closed while its tombstone exists", () => {
    const root = tempRoot();
    const victim = path.join(root, "victim.json");
    fs.writeFileSync(victim, "inside");
    expect(() => durableUnlinkFile(root, victim, { afterRename: () => { throw new Error("injected crash"); } })).toThrow("injected crash");
    const metadataPath = path.join(root, fs.readdirSync(root).find((name) => name.endsWith(".json") && name.startsWith(".delete-v1-"))!);
    const metadata = JSON.parse(fs.readFileSync(metadataPath, "utf8"));
    metadata.ino = "0";
    metadata.created_at = new Date(Date.now() - 60_000).toISOString();
    fs.writeFileSync(metadataPath, `${JSON.stringify(metadata)}\n`);

    expect(() => maintainDeleteTombstones(root, root)).toThrow("Delete tombstone metadata identity is uncertain");
    expect(fs.readdirSync(root).filter((name) => name.endsWith(".tomb"))).toHaveLength(1);
  });

  it("recovers a crashed durable delete inside an abandoned lock generation", async () => {
    const root = tempRoot();
    const generation = path.join(root, `.test.lock.gen-${crypto.randomUUID()}`);
    fs.mkdirSync(generation, { mode: 0o700 });
    const owner = path.join(generation, "owner.json");
    fs.writeFileSync(owner, "owner");
    const child = startChild(["delete-crash", root, owner]);
    await childExit(child);
    expect(fs.readdirSync(generation).filter((name) => name.startsWith(".delete-v1-"))).toHaveLength(2);
    const old = new Date(Date.now() - 60_000);
    fs.utimesSync(generation, old, old);

    const lock = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    lock.release();
    expect(fs.readdirSync(root).filter((name) => name.includes(".gen-") || name.startsWith(".delete-v1-"))).toEqual([]);
  });

  it("recovers lock-owner publication and delete-metadata publication crash points", async () => {
    for (const mode of ["lock-owner-temp-crash", "lock-owner-link-crash"]) {
      const root = tempRoot();
      const generation = path.join(root, `.test.lock.gen-${crypto.randomUUID()}`);
      fs.mkdirSync(generation, { mode: 0o700 });
      const child = startChild([mode, root, path.join(generation, "owner.json")]);
      await childExit(child);
      const old = new Date(Date.now() - 60_000);
      fs.utimesSync(generation, old, old);
      const lock = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
      lock.release();
      expect(fs.readdirSync(root).filter((name) => name.includes(".gen-") || name.startsWith(".publish-v1-"))).toEqual([]);
    }

    for (const mode of ["delete-metadata-temp-crash", "delete-metadata-link-crash"]) {
      const root = tempRoot();
      const generation = path.join(root, `.test.lock.gen-${crypto.randomUUID()}`);
      fs.mkdirSync(generation, { mode: 0o700 });
      const ownerPath = path.join(generation, "owner.json");
      await childExit(startChild(["lock-owner-write", root, ownerPath]));
      await childExit(startChild([mode, root, ownerPath]));
      const old = new Date(Date.now() - 60_000);
      fs.utimesSync(generation, old, old);
      const lock = acquireStorageLock(root, ".test.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
      lock.release();
      expect(fs.readdirSync(root).filter((name) => name.includes(".gen-") || name.startsWith(".publish-v1-") || name.startsWith(".delete-v1-"))).toEqual([]);
    }
  }, 30_000);
});

function withTransitionMarkerValidationClock(assertion: (afterTransitionGate: () => void) => void): void {
  // Only three reason-specific 500 ms marker fixtures use this clock. Exclude
  // non-wait preparation latency, preserving all native wait calls/results.
  let now = Date.now();
  const preparationStarted = performance.now();
  const assertPreparationBounded = () => {
    if (performance.now() - preparationStarted >= 1_000) throw new Error("Transition-marker fixture did not reach transition gate");
  };
  const clock = vi.spyOn(Date, "now").mockImplementation(() => {
    assertPreparationBounded();
    return now;
  });
  const nativeWait = Atomics.wait;
  const wait = vi.spyOn(Atomics, "wait").mockImplementation((...args) => {
    assertPreparationBounded();
    const duration = args[3];
    if (duration === undefined || !Number.isFinite(duration) || duration < 0 || duration > 500) throw new Error("Transition-marker fixture requested an unbounded wait");
    const result = Reflect.apply(nativeWait, Atomics, args) as ReturnType<typeof Atomics.wait>;
    now += duration;
    return result;
  });
  const gateEntered = vi.fn(() => {
    clock.mockRestore();
    wait.mockRestore();
  });
  try {
    assertion(gateEntered);
    expect(gateEntered).toHaveBeenCalledTimes(1);
  } finally {
    wait.mockRestore();
    clock.mockRestore();
  }
}

function transitionMarkerSnapshot(active: string) {
  return fs.readdirSync(active).sort().map((name) => {
    const file = path.join(active, name);
    const stat = fs.statSync(file, { bigint: true });
    return { name, bytes: fs.readFileSync(file), mtimeNs: stat.mtimeNs, mode: stat.mode };
  });
}

function custodiedSnapshot(file: string): CustodiedFileSnapshot {
  const stat = fs.statSync(file, { bigint: true });
  return { dev: stat.dev, ino: stat.ino, size: Number(stat.size), nlink: Number(stat.nlink), mtimeNs: stat.mtimeNs, ctimeNs: stat.ctimeNs,
    contentHash: `sha256:${crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex")}` };
}
function deadTransitionGate(root: string) {
  const gate = path.join(root, ".test.lock.transition-gate");
  const owner = { ...currentProcessOwner(), pid: 2_147_483_647 };
  expect(processOwnerGenerationState(owner)).toBe("dead");
  fs.mkdirSync(gate, { mode: 0o700 });
  fs.writeFileSync(path.join(gate, "owner.json"), `${JSON.stringify(owner)}\n`);
  return { gate, owner };
}
function tempRoot(): string { const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-storage-")); roots.push(root); return root; }
function lockArtifacts(root: string): string[] { return fs.readdirSync(root).filter((name) => name.includes(".test.lock") || name.startsWith(".publish-v1-") || name.startsWith(".delete-v1-")); }
function publisherTemp(root: string): string { return path.join(root, `.publish-v1-${Date.now()}-${process.pid}-${currentProcessIdentityTag()}-${crypto.randomUUID()}.tmp`); }
function startChild(args: string[], env: NodeJS.ProcessEnv = process.env): ChildProcess { return spawn(process.execPath, ["--import", "tsx", fixture, ...args], { env, stdio: ["ignore", "pipe", "pipe"] }); }
function childLine(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    child.stdout?.once("data", (data) => resolve(String(data).trim()));
    child.once("error", reject);
    child.stderr?.once("data", (data) => reject(new Error(String(data))));
  });
}
function childExit(child: ChildProcess): Promise<void> { return new Promise((resolve, reject) => { let stderr = ""; child.stderr?.on("data", (data) => { stderr += String(data); }); if (child.exitCode !== null) child.exitCode === 0 ? resolve() : reject(new Error(stderr || `child exited ${child.exitCode}`)); else { child.once("exit", (code) => code === 0 ? resolve() : reject(new Error(stderr || `child exited ${code}`))); child.once("error", reject); } }); }
async function waitFor(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 10_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("Timed out waiting for lock state");
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
