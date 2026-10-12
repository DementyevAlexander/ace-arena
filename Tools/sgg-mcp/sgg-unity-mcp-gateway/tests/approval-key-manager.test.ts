import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalKeyError, FileApprovalKeyManager, type ApprovalKeyFileSystem } from "../src/approvals/approval-key-manager.js";

const tempDirs: string[] = [];
afterEach(() => { for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true }); });

describe("FileApprovalKeyManager", () => {
  it("creates, fsyncs and reloads exactly 32 raw bytes with private modes", () => {
    const filePath = keyPath();
    let randomCalls = 0;
    const manager = new FileApprovalKeyManager(filePath, { crypto: { randomBytes: () => { randomCalls += 1; return Buffer.alloc(32, 0xa5); } } });
    expect(manager.getKey()).toEqual(Buffer.alloc(32, 0xa5));
    expect(manager.getKey()).toEqual(Buffer.alloc(32, 0xa5));
    expect(randomCalls).toBe(1);
    expect(new FileApprovalKeyManager(filePath, { crypto: { randomBytes: () => { throw new Error("unused"); } } }).getKey()).toEqual(Buffer.alloc(32, 0xa5));
    if (process.platform !== "win32") {
      expect(fs.statSync(path.dirname(filePath)).mode & 0o777).toBe(0o700);
      expect(fs.statSync(filePath).mode & 0o777).toBe(0o600);
    }
  });

  it("rejects unresolved placeholders before touching the filesystem", () => {
    let touched = false;
    const seam = fsSeam({ mkdirSync: (() => { touched = true; }) as ApprovalKeyFileSystem["mkdirSync"] });
    expectKeyError(() => new FileApprovalKeyManager("%LOCALAPPDATA%/SGG/key", { fileSystem: seam }).getKey(), "approval_key_path_invalid");
    expect(touched).toBe(false);
  });

  it("reads the exclusive-create EEXIST race winner", () => {
    const filePath = keyPath();
    const winner = Buffer.alloc(32, 0x22);
    let reads = 0;
    const seam = fsSeam({
      readFileSync: ((target: fs.PathOrFileDescriptor) => { reads += 1; if (reads === 1) throw nodeError("ENOENT"); expect(target).toBe(filePath); return winner; }) as ApprovalKeyFileSystem["readFileSync"],
      openSync: (() => { throw nodeError("EEXIST"); }) as ApprovalKeyFileSystem["openSync"],
    });
    expect(new FileApprovalKeyManager(filePath, { fileSystem: seam, crypto: { randomBytes: () => Buffer.alloc(32, 0x33) } }).getKey()).toEqual(winner);
  });

  it("uses private modes, completes partial writes, fsyncs and then closes", () => {
    const calls: string[] = [];
    const offsets: number[] = [];
    const seam = missingFileSeam({
      mkdirSync: ((_target: fs.PathLike, options?: fs.MakeDirectoryOptions) => { calls.push(`mkdir:${options?.mode}`); return undefined; }) as ApprovalKeyFileSystem["mkdirSync"],
      chmodSync: ((_target: fs.PathLike, mode: fs.Mode) => { calls.push(`chmod:${mode}`); }) as ApprovalKeyFileSystem["chmodSync"],
      openSync: ((_target: fs.PathLike, flags: fs.OpenMode, mode?: fs.Mode) => { calls.push(`open:${flags}:${mode}`); return 17; }) as ApprovalKeyFileSystem["openSync"],
      writeSync: ((_fd: number, _buffer: Uint8Array, offset: number, length: number) => { offsets.push(offset); return Math.min(7, length); }) as ApprovalKeyFileSystem["writeSync"],
      fsyncSync: (() => { calls.push("fsync"); }) as ApprovalKeyFileSystem["fsyncSync"],
      closeSync: (() => { calls.push("close"); }) as ApprovalKeyFileSystem["closeSync"],
    });
    expect(new FileApprovalKeyManager(keyPath(), { fileSystem: seam, crypto: { randomBytes: () => Buffer.alloc(32, 0x44) } }).getKey()).toEqual(Buffer.alloc(32, 0x44));
    expect(offsets).toEqual([0, 7, 14, 21, 28]);
    expect(calls).toContain("mkdir:448");
    expect(calls).toContain("chmod:448");
    expect(calls).toContain("open:wx:384");
    expect(calls).toContain("chmod:384");
    expect(calls.indexOf("fsync")).toBeLessThan(calls.indexOf("close"));
  });

  it("never replaces malformed existing keys", () => {
    for (const malformed of [Buffer.alloc(0), Buffer.alloc(31), Buffer.alloc(33), Buffer.from("not-raw-32-byte-key")]) {
      const filePath = keyPath();
      fs.mkdirSync(path.dirname(filePath), { recursive: true });
      fs.writeFileSync(filePath, malformed);
      expectKeyError(() => new FileApprovalKeyManager(filePath).getKey(), "approval_key_invalid_length");
      expect(fs.readFileSync(filePath)).toEqual(malformed);
    }
  });

  it("maps read, create, invalid-length, path and permission failures to safe typed errors", () => {
    const base = keyPath();
    const cases: Array<[ApprovalKeyFileSystem, ApprovalKeyError["code"]]> = [
      [fsSeam({ readFileSync: (() => { throw nodeError("EIO"); }) as ApprovalKeyFileSystem["readFileSync"] }), "approval_key_read_failed"],
      [fsSeam({ readFileSync: (() => { throw nodeError("EACCES"); }) as ApprovalKeyFileSystem["readFileSync"] }), "approval_key_permission_denied"],
      [fsSeam({ mkdirSync: (() => { throw nodeError("EPERM"); }) as ApprovalKeyFileSystem["mkdirSync"] }), "approval_key_permission_denied"],
      [fsSeam({ readFileSync: (() => Buffer.alloc(31)) as unknown as ApprovalKeyFileSystem["readFileSync"] }), "approval_key_invalid_length"],
    ];
    for (const [fileSystem, code] of cases) expectKeyError(() => new FileApprovalKeyManager(base, { fileSystem }).getKey(), code);
    expectKeyError(() => new FileApprovalKeyManager(base, { fileSystem: missingFileSeam({ openSync: (() => { throw nodeError("EACCES"); }) as ApprovalKeyFileSystem["openSync"] }) }).getKey(), "approval_key_permission_denied");
    expectKeyError(() => new FileApprovalKeyManager(base, { fileSystem: missingFileSeam(), crypto: { randomBytes: () => { throw new Error("secret provider detail"); } } }).getKey(), "approval_key_create_failed");
  });

  it("closes descriptors and removes its new file on write, fsync and close failures", () => {
    for (const failurePoint of ["write", "fsync", "close"] as const) {
      let closed = 0;
      let unlinked = 0;
      const seam = missingFileSeam({
        openSync: (() => 91) as ApprovalKeyFileSystem["openSync"],
        writeSync: (() => { if (failurePoint === "write") throw nodeError("EIO"); return 32; }) as ApprovalKeyFileSystem["writeSync"],
        fsyncSync: (() => { if (failurePoint === "fsync") throw nodeError("EIO"); }) as ApprovalKeyFileSystem["fsyncSync"],
        closeSync: (() => { closed += 1; if (failurePoint === "close") throw nodeError("EIO"); }) as ApprovalKeyFileSystem["closeSync"],
        unlinkSync: (() => { unlinked += 1; }) as ApprovalKeyFileSystem["unlinkSync"],
      });
      expectKeyError(() => new FileApprovalKeyManager(keyPath(), { fileSystem: seam, crypto: { randomBytes: () => Buffer.alloc(32) } }).getKey(), "approval_key_create_failed");
      expect(closed).toBe(1);
      expect(unlinked).toBe(1);
    }
  });

  it("does not leak key, path, filesystem, crypto, or payload details", () => {
    const secretPath = "/secret/path/DO-NOT-LEAK/key";
    try {
      new FileApprovalKeyManager(secretPath, { fileSystem: fsSeam({ mkdirSync: (() => { throw new Error("raw payload DO-NOT-LEAK"); }) as ApprovalKeyFileSystem["mkdirSync"] }) }).getKey();
    } catch (error) {
      expect(error).toBeInstanceOf(ApprovalKeyError);
      expect(String(error)).not.toContain("DO-NOT-LEAK");
      expect(JSON.stringify(error)).not.toContain("DO-NOT-LEAK");
    }
  });
});

function makeTempDir(): string { const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-approval-key-")); tempDirs.push(dir); return dir; }
function keyPath(): string { return path.join(makeTempDir(), "secrets", "approval-hmac-v1.key"); }
function nodeError(code: string): NodeJS.ErrnoException { const error = new Error("sensitive underlying detail") as NodeJS.ErrnoException; error.code = code; return error; }
function fsSeam(overrides: Partial<ApprovalKeyFileSystem> = {}): ApprovalKeyFileSystem {
  return { mkdirSync: fs.mkdirSync, readFileSync: fs.readFileSync, openSync: fs.openSync, writeSync: fs.writeSync, fsyncSync: fs.fsyncSync, closeSync: fs.closeSync, chmodSync: fs.chmodSync, unlinkSync: fs.unlinkSync, ...overrides };
}
function missingFileSeam(overrides: Partial<ApprovalKeyFileSystem> = {}): ApprovalKeyFileSystem {
  return fsSeam({ readFileSync: (() => { throw nodeError("ENOENT"); }) as ApprovalKeyFileSystem["readFileSync"], ...overrides });
}
function expectKeyError(action: () => unknown, code: ApprovalKeyError["code"]): void {
  try { action(); throw new Error("expected ApprovalKeyError"); } catch (error) { expect(error).toBeInstanceOf(ApprovalKeyError); expect((error as ApprovalKeyError).code).toBe(code); }
}
