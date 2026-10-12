import { createHash } from "node:crypto";
import fs, { type BigIntStats } from "node:fs";

import { ZodError } from "zod";

import { policyConfigSchema, type PolicyConfig } from "../config/config.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";
import type { OperationalLogger } from "../observability/operational-logger.js";

const MAX_POLICY_BYTES = 1024 * 1024;
const POLICY_REVISION_DOMAIN = Buffer.from("sgg.unity_mcp.policy_revision.v1\0", "utf8");

export type PolicyLoadErrorCode = "policy_reload_failed" | "policy_invalid";
export type PolicyLoadErrorKind = "missing" | "unreadable" | "too_large" | "unstable" | "invalid_json" | "invalid_schema";

export class PolicyLoadError extends Error {
  readonly code: PolicyLoadErrorCode;
  readonly kind: PolicyLoadErrorKind;

  constructor(code: PolicyLoadErrorCode, kind: PolicyLoadErrorKind) {
    super(code === "policy_invalid" ? "Configured policy is invalid" : "Configured policy could not be loaded");
    this.name = "PolicyLoadError";
    this.code = code;
    this.kind = kind;
  }
}

export type PolicyManagerSnapshot = {
  state: "unconfigured" | "active" | "degraded" | "error";
  configured: boolean;
  degraded: boolean;
  effective_policy_revision: string | null;
  loaded_at: string | null;
  last_attempt_at: string | null;
  last_error_kind: PolicyLoadErrorKind | null;
  last_error_code: PolicyLoadErrorCode | null;
  degraded_since: string | null;
};

export type PolicyFileInspection = PolicyManagerSnapshot & {
  inspection: "current_file";
  current_file_status: "not_configured" | "valid" | "missing" | "unreadable" | "invalid";
};

export type PolicyFileInspectionResult = {
  inspection: PolicyFileInspection;
  policy: PolicyConfig | undefined;
};

type PolicyFileSystem = {
  statSync(path: string, options: { bigint: true }): BigIntStats;
  readFileSync(path: string): Buffer;
};

type PolicyManagerOptions = {
  now?: () => Date;
  fileSystem?: PolicyFileSystem;
  maxBytes?: number;
  logger?: OperationalLogger;
};

type FileIdentity = {
  dev: bigint;
  ino: bigint;
  size: bigint;
  mtimeNs: bigint;
  ctimeNs: bigint;
};

export class PolicyManager {
  readonly configured: boolean;
  #sourcePath: string | undefined;
  #rawHash: string | undefined;
  #identity: FileIdentity | undefined;
  #policy: PolicyConfig | undefined;
  #effectivePolicyRevision: string | undefined;
  #loadedAt: string | undefined;
  #lastAttemptAt: string | undefined;
  #lastErrorKind: PolicyLoadErrorKind | undefined;
  #lastErrorCode: PolicyLoadErrorCode | undefined;
  #degradedSince: string | undefined;
  readonly #now: () => Date;
  readonly #fileSystem: PolicyFileSystem;
  readonly #maxBytes: number;
  readonly #logger: OperationalLogger | undefined;

  constructor(sourcePath?: string, options: PolicyManagerOptions = {}) {
    this.configured = sourcePath !== undefined;
    this.#sourcePath = sourcePath;
    this.#now = options.now ?? (() => new Date());
    this.#fileSystem = options.fileSystem ?? fs;
    this.#maxBytes = options.maxBytes ?? MAX_POLICY_BYTES;
    this.#logger = options.logger;
    if (this.configured) {
      this.reload(true);
    }
  }

  get policy(): PolicyConfig | undefined {
    return this.#policy;
  }

  get degraded(): boolean {
    return this.#degradedSince !== undefined;
  }

  get effectivePolicyRevision(): string | undefined {
    return this.#effectivePolicyRevision;
  }

  reload(startup = false): boolean {
    if (this.#sourcePath === undefined) {
      return false;
    }

    this.#lastAttemptAt = this.#now().toISOString();
    let failure: PolicyLoadError | undefined;
    for (let attempt = 0; attempt < 2; attempt += 1) {
      try {
        const before = identity(this.#fileSystem.statSync(this.#sourcePath, { bigint: true }));
        if (sameIdentity(before, this.#identity)) {
          this.#recover();
          return false;
        }
        if (before.size > BigInt(this.#maxBytes)) {
          throw new PolicyLoadError("policy_reload_failed", "too_large");
        }

        const raw = this.#fileSystem.readFileSync(this.#sourcePath);
        const after = identity(this.#fileSystem.statSync(this.#sourcePath, { bigint: true }));
        if (!sameIdentity(before, after) || BigInt(raw.byteLength) !== after.size) {
          failure = new PolicyLoadError("policy_reload_failed", "unstable");
          continue;
        }

        let decoded: unknown;
        try {
          decoded = JSON.parse(raw.toString("utf8"));
        } catch {
          throw new PolicyLoadError("policy_invalid", "invalid_json");
        }
        let policy: PolicyConfig;
        try {
          policy = policyConfigSchema.parse(decoded);
        } catch (error) {
          if (error instanceof ZodError) {
            throw new PolicyLoadError("policy_invalid", "invalid_schema");
          }
          throw error;
        }

        this.#policy = policy;
        this.#identity = after;
        this.#rawHash = createHash("sha256").update(raw).digest("hex");
        this.#effectivePolicyRevision = policyRevision(policy);
        this.#loadedAt = this.#now().toISOString();
        this.#recover();
        return true;
      } catch (error) {
        failure = safePolicyError(error);
        if (failure.kind !== "missing" && failure.kind !== "unreadable" && failure.kind !== "unstable") {
          break;
        }
      }
    }

    const safeFailure = failure ?? new PolicyLoadError("policy_reload_failed", "unreadable");
    this.#lastErrorCode = safeFailure.code;
    this.#lastErrorKind = safeFailure.kind;
    const wasDegraded = this.#degradedSince !== undefined;
    this.#degradedSince ??= this.#lastAttemptAt;
    if (!wasDegraded) this.#logger?.log("SGG-OP-POLICY-DEGRADED-001", safeFailure.code, safeFailure.kind);
    if (startup || !this.#policy) {
      throw safeFailure;
    }
    return false;
  }

  snapshot(): PolicyManagerSnapshot {
    return {
      state: !this.configured ? "unconfigured" : this.degraded ? "degraded" : "active",
      configured: this.configured,
      degraded: this.degraded,
      effective_policy_revision: this.#effectivePolicyRevision ?? null,
      loaded_at: this.#loadedAt ?? null,
      last_attempt_at: this.#lastAttemptAt ?? null,
      last_error_kind: this.#lastErrorKind ?? null,
      last_error_code: this.#lastErrorCode ?? null,
      degraded_since: this.#degradedSince ?? null,
    };
  }

  #recover(): void {
    const wasDegraded = this.#degradedSince !== undefined;
    this.#lastErrorKind = undefined;
    this.#lastErrorCode = undefined;
    this.#degradedSince = undefined;
    if (wasDegraded) this.#logger?.log("SGG-OP-POLICY-RECOVERED-001");
  }
}

export function inspectCurrentPolicyFile(sourcePath: string | undefined, options: PolicyManagerOptions = {}): PolicyFileInspection {
  return inspectCurrentPolicyFileResult(sourcePath, options).inspection;
}

export function inspectCurrentPolicyFileResult(sourcePath: string | undefined, options: PolicyManagerOptions = {}): PolicyFileInspectionResult {
  if (sourcePath === undefined) {
    return {
      inspection: { inspection: "current_file", current_file_status: "not_configured", ...emptyPolicySnapshot(false) },
      policy: undefined,
    };
  }

  const now = options.now ?? (() => new Date());
  try {
    const manager = new PolicyManager(sourcePath, { ...options, now });
    return {
      inspection: { inspection: "current_file", current_file_status: "valid", ...manager.snapshot() },
      policy: manager.policy,
    };
  } catch (error) {
    const safeError = safePolicyError(error);
    const attemptedAt = now().toISOString();
    return {
      inspection: {
        inspection: "current_file",
        current_file_status: policyFileStatus(safeError.kind),
        state: "error",
        configured: true,
        degraded: false,
        effective_policy_revision: null,
        loaded_at: null,
        last_attempt_at: attemptedAt,
        last_error_kind: safeError.kind,
        last_error_code: safeError.code,
        degraded_since: null,
      },
      policy: undefined,
    };
  }
}

export function policyManagerSnapshot(manager: PolicyManager | undefined, configured = false): PolicyManagerSnapshot {
  return manager?.snapshot() ?? emptyPolicySnapshot(configured);
}

function emptyPolicySnapshot(configured: boolean): PolicyManagerSnapshot {
  return {
    state: configured ? "active" : "unconfigured",
    configured,
    degraded: false,
    effective_policy_revision: null,
    loaded_at: null,
    last_attempt_at: null,
    last_error_kind: null,
    last_error_code: null,
    degraded_since: null,
  };
}

function policyFileStatus(kind: PolicyLoadErrorKind): PolicyFileInspection["current_file_status"] {
  if (kind === "missing") return "missing";
  if (kind === "invalid_json" || kind === "invalid_schema") return "invalid";
  return "unreadable";
}

function identity(stats: BigIntStats): FileIdentity {
  return { dev: stats.dev, ino: stats.ino, size: stats.size, mtimeNs: stats.mtimeNs, ctimeNs: stats.ctimeNs };
}

function sameIdentity(left: FileIdentity | undefined, right: FileIdentity | undefined): boolean {
  return left !== undefined && right !== undefined
    && left.dev === right.dev && left.ino === right.ino && left.size === right.size
    && left.mtimeNs === right.mtimeNs && left.ctimeNs === right.ctimeNs;
}

function safePolicyError(error: unknown): PolicyLoadError {
  if (error instanceof PolicyLoadError) {
    return error;
  }
  const code = typeof error === "object" && error !== null && "code" in error ? String(error.code) : "";
  return new PolicyLoadError("policy_reload_failed", code === "ENOENT" || code === "ENOTDIR" ? "missing" : "unreadable");
}

function policyRevision(policy: PolicyConfig): string {
  return `sha256:${createHash("sha256").update(POLICY_REVISION_DOMAIN).update(canonicalJsonStringify(policy)).digest("hex")}`;
}
