import { randomBytes } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import type { ApprovalHmacKeyProvider } from "./approval-binding.js";

export const DEFAULT_APPROVAL_HMAC_KEY_FILE = "%LOCALAPPDATA%/SGG/UnityMcpGateway/secrets/approval-hmac-v1.key";
const APPROVAL_KEY_BYTES = 32;

export type ApprovalKeyErrorCode =
  | "approval_key_path_invalid"
  | "approval_key_permission_denied"
  | "approval_key_read_failed"
  | "approval_key_create_failed"
  | "approval_key_invalid_length";

export class ApprovalKeyError extends Error {
  constructor(readonly code: ApprovalKeyErrorCode) {
    super(approvalKeyErrorMessage(code));
    this.name = "ApprovalKeyError";
  }
}

export type ApprovalKeyFileSystem = Pick<typeof fs,
  "mkdirSync" | "readFileSync" | "openSync" | "writeSync" | "fsyncSync" | "closeSync" | "chmodSync" | "unlinkSync"
>;

export type ApprovalKeyCrypto = {
  randomBytes(size: number): Buffer;
};

export type ApprovalKeyManagerOptions = {
  fileSystem?: ApprovalKeyFileSystem;
  crypto?: ApprovalKeyCrypto;
};

export class FileApprovalKeyManager implements ApprovalHmacKeyProvider {
  readonly #fs: ApprovalKeyFileSystem;
  readonly #crypto: ApprovalKeyCrypto;
  #cachedKey: Buffer | undefined;

  constructor(readonly filePath: string, options: ApprovalKeyManagerOptions = {}) {
    this.#fs = options.fileSystem ?? fs;
    this.#crypto = options.crypto ?? { randomBytes };
  }

  getKey(): Buffer {
    if (!this.#cachedKey) {
      this.#cachedKey = this.#loadOrCreate();
    }
    return Buffer.from(this.#cachedKey);
  }

  #loadOrCreate(): Buffer {
    if (/%[^%]+%/.test(this.filePath) || this.filePath.trim().length === 0) {
      throw new ApprovalKeyError("approval_key_path_invalid");
    }
    const directory = path.dirname(this.filePath);
    try {
      this.#fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
      bestEffortChmod(this.#fs, directory, 0o700);
    } catch (error) {
      throw mappedKeyError(error, "approval_key_create_failed");
    }

    const existing = this.#readExisting(true);
    if (existing) {
      return existing;
    }

    let key: Buffer;
    try {
      key = this.#crypto.randomBytes(APPROVAL_KEY_BYTES);
    } catch {
      throw new ApprovalKeyError("approval_key_create_failed");
    }
    assertKeyLength(key);

    let descriptor: number | undefined;
    let created = false;
    let failure: unknown;
    try {
      descriptor = this.#fs.openSync(this.filePath, "wx", 0o600);
      created = true;
      writeAll(this.#fs, descriptor, key);
      this.#fs.fsyncSync(descriptor);
      bestEffortChmod(this.#fs, this.filePath, 0o600);
    } catch (error) {
      if (isErrorCode(error, "EEXIST")) {
        const winner = this.#readExisting(false);
        if (!winner) {
          throw new ApprovalKeyError("approval_key_read_failed");
        }
        return winner;
      }
      failure = error;
    } finally {
      if (descriptor !== undefined) {
        try {
          this.#fs.closeSync(descriptor);
        } catch (error) {
          failure ??= error;
        }
      }
    }
    if (failure !== undefined) {
      if (created) {
        bestEffortUnlink(this.#fs, this.filePath);
      }
      throw mappedKeyError(failure, "approval_key_create_failed");
    }
    return key;
  }

  #readExisting(allowMissing: boolean): Buffer | undefined {
    let key: Buffer;
    try {
      key = this.#fs.readFileSync(this.filePath);
    } catch (error) {
      if (allowMissing && isErrorCode(error, "ENOENT")) {
        return undefined;
      }
      throw mappedKeyError(error, "approval_key_read_failed");
    }
    assertKeyLength(key);
    bestEffortChmod(this.#fs, this.filePath, 0o600);
    return key;
  }
}

function writeAll(fileSystem: ApprovalKeyFileSystem, descriptor: number, key: Buffer): void {
  let offset = 0;
  while (offset < key.byteLength) {
    const written = fileSystem.writeSync(descriptor, key, offset, key.byteLength - offset, null);
    if (written <= 0) {
      throw new Error("write failed");
    }
    offset += written;
  }
}

function assertKeyLength(key: Buffer): void {
  if (key.byteLength !== APPROVAL_KEY_BYTES) {
    throw new ApprovalKeyError("approval_key_invalid_length");
  }
}

function mappedKeyError(error: unknown, fallback: ApprovalKeyErrorCode): ApprovalKeyError {
  if (error instanceof ApprovalKeyError) {
    return error;
  }
  return new ApprovalKeyError(isErrorCode(error, "EACCES") || isErrorCode(error, "EPERM") ? "approval_key_permission_denied" : fallback);
}

function isErrorCode(error: unknown, code: string): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === code;
}

function bestEffortChmod(fileSystem: ApprovalKeyFileSystem, target: string, mode: number): void {
  try {
    fileSystem.chmodSync(target, mode);
  } catch {
    // Windows and some filesystems do not support POSIX mode changes.
  }
}

function bestEffortUnlink(fileSystem: ApprovalKeyFileSystem, target: string): void {
  try {
    fileSystem.unlinkSync(target);
  } catch {
    // A failed exclusive creation remains unusable and will be rejected by length on reload.
  }
}

function approvalKeyErrorMessage(code: ApprovalKeyErrorCode): string {
  switch (code) {
    case "approval_key_path_invalid": return "Approval HMAC key path is invalid or contains an unresolved environment placeholder";
    case "approval_key_permission_denied": return "Approval HMAC key operation was denied by filesystem permissions";
    case "approval_key_read_failed": return "Approval HMAC key could not be read";
    case "approval_key_create_failed": return "Approval HMAC key could not be created durably";
    case "approval_key_invalid_length": return "Approval HMAC key has an invalid length; expected exactly 32 raw bytes";
  }
}
