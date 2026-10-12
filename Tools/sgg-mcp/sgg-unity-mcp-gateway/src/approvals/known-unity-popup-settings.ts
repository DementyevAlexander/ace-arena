import fs, { type BigIntStats } from "node:fs";

import { parseGatewayConfig } from "../config/config.js";
import { sameFileObjectIdentity } from "../security/safe-storage.js";

const MAX_CONFIG_BYTES = 1024 * 1024;

export type KnownUnityPopupSettingsSnapshot = {
  application_mode: "live_on_request";
  source_status: "valid" | "missing" | "unreadable" | "too_large" | "unstable" | "invalid";
  saved_enabled: boolean | null;
  restart_required: false;
};

// This reloads ONLY the owner's known-popup opt-in. Features, generic ask
// compatibility, network settings and approval storage stay startup-bound.
// There is deliberately no mtime-only cache or last-known-true fallback.
export class KnownUnityPopupSettingsManager {
  #snapshot: KnownUnityPopupSettingsSnapshot = {
    application_mode: "live_on_request", source_status: "unreadable", saved_enabled: null, restart_required: false,
  };

  constructor(readonly sourcePath: string) {}

  reload(): boolean {
    let handle: number | undefined;
    let status: KnownUnityPopupSettingsSnapshot["source_status"] = "unreadable";
    try {
      const entry = fs.lstatSync(this.sourcePath, { bigint: true });
      if (!entry.isFile() || entry.isSymbolicLink()) throw new Error("Non-regular config");
      handle = fs.openSync(this.sourcePath, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0) | (fs.constants.O_NONBLOCK ?? 0));
      const before = fs.fstatSync(handle, { bigint: true });
      if (!before.isFile() || !sameFile(entry, before)) { status = "unstable"; throw new Error("Config changed"); }
      if (before.size > BigInt(MAX_CONFIG_BYTES)) { status = "too_large"; throw new Error("Config too large"); }
      // An extra byte detects growth without ever reading an unbounded file.
      const buffer = Buffer.alloc(Number(before.size) + 1);
      let length = 0;
      while (length < buffer.length) {
        const read = fs.readSync(handle, buffer, length, buffer.length - length, null);
        if (read === 0) break;
        length += read;
      }
      const after = fs.fstatSync(handle, { bigint: true });
      const current = fs.lstatSync(this.sourcePath, { bigint: true });
      if (!after.isFile() || !current.isFile() || entry.dev !== current.dev || before.dev !== after.dev
        || !sameFile(entry, current) || !sameFile(before, after) || !sameFile(after, current) || BigInt(length) !== after.size) {
        status = "unstable"; throw new Error("Config changed during read");
      }
      status = "invalid";
      const source = new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, length));
      const enabled = parseGatewayConfig(source).approvals?.auto_allow_known_unity_popups === true;
      status = "unreadable";
      fs.closeSync(handle);
      handle = undefined;
      this.#snapshot = { application_mode: "live_on_request", source_status: "valid", saved_enabled: enabled, restart_required: false };
      return enabled;
    } catch (error) {
      if (typeof error === "object" && error !== null && "code" in error && (error.code === "ENOENT" || error.code === "ENOTDIR")) status = "missing";
      this.#snapshot = { application_mode: "live_on_request", source_status: status, saved_enabled: null, restart_required: false };
      return false;
    } finally {
      if (handle !== undefined) {
        try { fs.closeSync(handle); } catch { /* A failed cleanup never escapes or retains consent. */ }
      }
    }
  }

  snapshot(): KnownUnityPopupSettingsSnapshot { return { ...this.#snapshot }; }
}

function sameFile(a: BigIntStats, b: BigIntStats): boolean {
  // Native Node 22 Windows may report dev=0 for lstat but the volume id for
  // fstat (or vice versa). Reuse the established nonzero-inode identity rule;
  // reload separately requires stable path-to-path and handle-to-handle dev.
  return sameFileObjectIdentity(a, b, process.platform)
    && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
}

export function refreshKnownUnityPopupApproval(context: {
  knownUnityPopupSettings?: KnownUnityPopupSettingsManager;
  approvalAutoAllowKnownUnityPopups?: boolean;
}): void {
  if (context.knownUnityPopupSettings) context.approvalAutoAllowKnownUnityPopups = context.knownUnityPopupSettings.reload();
}
