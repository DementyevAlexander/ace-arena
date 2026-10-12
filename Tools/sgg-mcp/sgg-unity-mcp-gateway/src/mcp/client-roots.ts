import { createHash, randomUUID } from "node:crypto";

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import {
  ListRootsResultSchema,
  RootsListChangedNotificationSchema,
  type ServerNotification,
  type ServerRequest,
} from "@modelcontextprotocol/sdk/types.js";
import type { McpServer as V2McpServer, ServerContext as V2ServerContext } from "@modelcontextprotocol/server";

import { CanonicalPathService } from "../paths/canonical-path.js";

const ROOT_HASH_DOMAIN = "sgg-unity-mcp-client-root-v1\0";
const ROOTS_LIST_TIMEOUT_MS = 1_000;
const MAX_ROOTS = 8;
const MAX_ROOT_URI_LENGTH = 2_048;
const MAX_OBSERVATIONS = 128;
const OBSERVATION_TTL_MS = 30 * 60_000;

type RootsExtra = Pick<RequestHandlerExtra<ServerRequest, ServerNotification>, "sendRequest" | "signal">;

type ClientRootsObservation = {
  observation_id: string;
  status: "observed" | "unsupported" | "unavailable";
  root_hashes: string[];
  observed_at: string;
  expires_at_ms: number;
  error_code?: "roots_list_failed";
};

export type ClientRootsHealth = {
  schema_version: 1;
  observations: Array<Omit<ClientRootsObservation, "expires_at_ms">>;
};

export class ClientRootsRegistry {
  readonly #observations = new Map<string, ClientRootsObservation>();
  readonly #now: () => number;

  constructor(options: { now?: () => number } = {}) {
    this.#now = options.now ?? Date.now;
  }

  createObserver(server: McpServer): ClientRootsObserver {
    return new ClientRootsObserver(server, this, randomUUID());
  }

  createV2Observer(server: V2McpServer): V2ClientRootsObserver {
    return new V2ClientRootsObserver(server, this, randomUUID());
  }

  remove(observationId: string): void {
    this.#observations.delete(observationId);
  }

  touch(observationId: string): boolean {
    const now = this.#now();
    this.#prune(now);
    const observation = this.#observations.get(observationId);
    if (!observation) return false;
    observation.observed_at = new Date(now).toISOString();
    observation.expires_at_ms = now + OBSERVATION_TTL_MS;
    return true;
  }

  record(observation: Omit<ClientRootsObservation, "observed_at" | "expires_at_ms">): void {
    const now = this.#now();
    this.#prune(now);
    if (!this.#observations.has(observation.observation_id) && this.#observations.size >= MAX_OBSERVATIONS) {
      const oldest = this.#observations.keys().next().value as string | undefined;
      if (oldest) this.#observations.delete(oldest);
    }
    this.#observations.set(observation.observation_id, {
      ...observation,
      observed_at: new Date(now).toISOString(),
      expires_at_ms: now + OBSERVATION_TTL_MS,
    });
  }

  health(): ClientRootsHealth {
    this.#prune(this.#now());
    return {
      schema_version: 1,
      observations: [...this.#observations.values()].map(({ expires_at_ms: _expiresAt, ...observation }) => ({
        ...observation,
        root_hashes: [...observation.root_hashes],
      })),
    };
  }

  #prune(now: number): void {
    for (const [id, observation] of this.#observations) {
      if (observation.expires_at_ms <= now) this.#observations.delete(id);
    }
  }
}

export class V2ClientRootsObserver {
  readonly #server: V2McpServer;
  readonly #registry: ClientRootsRegistry;
  readonly #observationId: string;
  #dirty = true;
  #inFlight: Promise<void> | undefined;
  #retryAt = 0;
  #generation = 0;

  constructor(server: V2McpServer, registry: ClientRootsRegistry, observationId: string) {
    this.#server = server;
    this.#registry = registry;
    this.#observationId = observationId;
    const previousOnClose = server.server.onclose;
    server.server.onclose = () => {
      this.#registry.remove(this.#observationId);
      previousOnClose?.();
    };
    server.server.setNotificationHandler("notifications/roots/list_changed", () => {
      if (server.server.getClientCapabilities()?.roots?.listChanged === true) {
        this.#dirty = true;
        this.#retryAt = 0;
        this.#generation++;
        this.#registry.remove(this.#observationId);
      }
    });
  }

  async observe(context: V2ServerContext): Promise<void> {
    if (!this.#dirty && this.#registry.touch(this.#observationId)) return;
    this.#dirty = true;
    if (Date.now() < this.#retryAt) return;
    if (this.#inFlight) return this.#inFlight;
    const generation = this.#generation;
    this.#inFlight = this.#refresh(context, generation).finally(() => { this.#inFlight = undefined; });
    return this.#inFlight;
  }

  async #refresh(context: V2ServerContext, generation: number): Promise<void> {
    const capabilities = this.#server.server.getClientCapabilities();
    if (capabilities === undefined) return;
    if (!capabilities.roots) {
      this.#registry.record({ observation_id: this.#observationId, status: "unsupported", root_hashes: [] });
      this.#dirty = false;
      return;
    }

    try {
      const result = await context.mcpReq.send({ method: "roots/list" }, {
        timeout: ROOTS_LIST_TIMEOUT_MS,
        signal: context.mcpReq.signal,
      });
      if (generation !== this.#generation) return;
      this.#registry.record({
        observation_id: this.#observationId,
        status: "observed",
        root_hashes: clientRootHashes(result.roots),
      });
      this.#dirty = false;
    } catch {
      if (generation !== this.#generation) return;
      this.#registry.record({ observation_id: this.#observationId, status: "unavailable", root_hashes: [], error_code: "roots_list_failed" });
      this.#retryAt = Date.now() + 30_000;
    }
  }
}

export class ClientRootsObserver {
  readonly #server: McpServer;
  readonly #registry: ClientRootsRegistry;
  readonly #observationId: string;
  #dirty = true;
  #inFlight: Promise<void> | undefined;
  #retryAt = 0;
  #generation = 0;

  constructor(server: McpServer, registry: ClientRootsRegistry, observationId: string) {
    this.#server = server;
    this.#registry = registry;
    this.#observationId = observationId;
    const previousOnClose = server.server.onclose;
    server.server.onclose = () => {
      this.#registry.remove(this.#observationId);
      previousOnClose?.();
    };
    server.server.setNotificationHandler(RootsListChangedNotificationSchema, () => {
      if (server.server.getClientCapabilities()?.roots?.listChanged === true) {
        this.#dirty = true;
        this.#retryAt = 0;
        this.#generation++;
        this.#registry.remove(this.#observationId);
      }
    });
  }

  async observe(extra: RootsExtra): Promise<void> {
    if (!this.#dirty && this.#registry.touch(this.#observationId)) return;
    this.#dirty = true;
    if (Date.now() < this.#retryAt) return;
    if (this.#inFlight) return this.#inFlight;
    const generation = this.#generation;
    this.#inFlight = this.#refresh(extra, generation).finally(() => { this.#inFlight = undefined; });
    return this.#inFlight;
  }

  async #refresh(extra: RootsExtra, generation: number): Promise<void> {
    const capabilities = this.#server.server.getClientCapabilities();
    // Stateless HTTP calls have no initialize context and must not masquerade as clients.
    if (capabilities === undefined) return;
    if (!capabilities?.roots) {
      this.#registry.record({ observation_id: this.#observationId, status: "unsupported", root_hashes: [] });
      this.#dirty = false;
      return;
    }

    try {
      const result = await extra.sendRequest({ method: "roots/list" }, ListRootsResultSchema, {
        timeout: ROOTS_LIST_TIMEOUT_MS,
        signal: extra.signal,
      });
      if (generation !== this.#generation) return;
      this.#registry.record({
        observation_id: this.#observationId,
        status: "observed",
        root_hashes: clientRootHashes(result.roots),
      });
    } catch {
      if (generation !== this.#generation) return;
      this.#registry.record({
        observation_id: this.#observationId,
        status: "unavailable",
        root_hashes: [],
        error_code: "roots_list_failed",
      });
      this.#retryAt = Date.now() + 30_000;
    } finally {
      if (generation === this.#generation) this.#dirty = this.#retryAt > Date.now();
    }
  }
}

export function clientRootHashes(roots: Array<{ uri: string }>): string[] {
  const canonicalPaths = new CanonicalPathService();
  const hashes = new Set<string>();
  for (const root of roots.slice(0, MAX_ROOTS)) {
    if (typeof root.uri !== "string" || root.uri.length === 0 || root.uri.length > MAX_ROOT_URI_LENGTH) continue;
    try {
      const url = new URL(root.uri);
      if (url.protocol !== "file:" || (url.hostname && url.hostname !== "localhost")) continue;
      let rootPath = decodeURIComponent(url.pathname);
      if (/^\/[A-Za-z]:\//.test(rootPath)) rootPath = rootPath.slice(1);
      const canonicalRoot = canonicalPaths.normalizeProjectRoot(rootPath);
      hashes.add(clientRootHash(canonicalRoot));
    } catch {
      // Invalid or unsupported roots are omitted from bounded diagnostic evidence.
    }
  }
  return [...hashes].sort();
}

export function clientRootHash(canonicalRoot: string): string {
  return `sha256:${createHash("sha256").update(ROOT_HASH_DOMAIN).update(canonicalRoot, "utf8").digest("hex")}`;
}
