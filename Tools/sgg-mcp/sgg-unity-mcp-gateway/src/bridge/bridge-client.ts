import { createHash } from "node:crypto";

import {
  BRIDGE_PROTOCOL_VERSION,
  BridgeProtocolError,
  assertBridgeResponseIdentity,
  commandCatalogRevision,
  commandDescriptorRevision,
  parseCommandCatalogV1,
  parseHealthResponseV1,
  parseBridgeInvokeResponseV1,
  parseLogsResponseV1,
  parseSingleCommandResponseV1,
  serializeBridgeInvokeRequestV1,
  type CommandDescriptor,
  type HeartbeatRecord,
  type HealthResponseV1,
  type LogsResponseV1,
  type ManifestScanDiagnostic,
  type ProjectManifestCandidate,
} from "./bridge-protocol.js";
export type { CommandDescriptor, ManifestScanDiagnostic, ProjectManifestCandidate } from "./bridge-protocol.js";
import type { ErrorCode, GatewayError } from "../mcp/results.js";
import type { OperationalLogger } from "../observability/operational-logger.js";

export type BridgeRequestErrorDetails = {
  endpoint: string;
  bridge_host: string;
  bridge_port: number;
  bridge_request_issued?: boolean;
  bridge_endpoint_available?: boolean;
  lifecycle_status?: HeartbeatRecord["status"];
  failure_kind?: "lifecycle_unavailable" | "authentication" | "redirect" | "http_error" | "invalid_content_type" | "response_too_large" | "response_parse" | "response_shape" | "selector_not_supported" | "timeout" | "connection";
  http_error_kind?: "authentication" | "redirect" | "client" | "server";
  http_status?: number;
  response_content_type?: string;
  response_content_length?: number;
  response_body_bytes?: number;
  response_body_empty?: boolean;
  response_body_truncated?: boolean;
  response_body_sha256?: string;
  response_error_code?: string;
  parse_error_kind?: "missing_content_type" | "invalid_content_type" | "empty_body" | "truncated_json" | "invalid_json" | "response_shape_mismatch";
  abort_source?: "endpoint_timeout" | "transport_timeout" | "deadline" | "caller";
  endpoint_timeout_ms?: number;
  transport_timeout_ms?: number;
  deadline_unix_ms?: number;
  request_id?: string;
  response_body_max_bytes?: number;
  response_too_large?: boolean;
  cause_class?: "AbortError" | "TimeoutError" | "TypeError" | "SyntaxError" | "Error" | "BridgeLifecycleUnavailable" | "ResponseShapeMismatch";
  protocol_schema?: string;
  protocol_reason?: string;
  expected_protocol_version?: number;
  received_protocol_version?: string | number | boolean | null;
  protocol_issues?: Array<{ path: Array<string | number>; code: string }>;
};

export class BridgeRequestError extends Error {
  readonly details: BridgeRequestErrorDetails;

  constructor(code: Extract<ErrorCode, "bridge_unreachable" | "bridge_protocol_error" | "protocol_version_mismatch" | "bridge_timeout" | "invalid_token" | "selector_not_supported">, details: BridgeRequestErrorDetails) {
    super(code);
    this.name = "BridgeRequestError";
    this.details = details;
  }
}

export class BridgeRequestNotIssuedError extends Error {
  constructor() {
    super("Bridge request was blocked immediately before issuance");
    this.name = "BridgeRequestNotIssuedError";
  }
}

export type CommandCatalogResponse = {
  schema_version: typeof BRIDGE_PROTOCOL_VERSION;
  instance_id?: string | undefined;
  project: string;
  commands_hash: string;
  catalog_generation?: number | undefined;
  catalog_revision: string;
  commands: CommandDescriptor[];
  manifest_diagnostics?: ManifestScanDiagnostic[];
  project_manifest_candidates?: ProjectManifestCandidate[];
};

export type SingleCommandResponse = {
  schema_version: typeof BRIDGE_PROTOCOL_VERSION;
  instance_id?: string | undefined;
  project: string;
  commands_hash: string;
  catalog_generation?: number | undefined;
  descriptor_revision: string | null;
  command: CommandDescriptor | null;
};

export type BridgeInvokeRequest = {
  request_id?: string;
  project: string;
  command: string;
  args: Record<string, unknown>;
  settings?: BridgeInvokeSettings;
  deadline_unix_ms?: number;
  risk?: CommandDescriptor["risk"];
  expected_instance_id?: string;
  expected_catalog_generation?: number;
  expected_commands_hash?: string;
};

export type BridgeInvokeSettings = {
  asset_index_cache_dir?: string;
  feature_flags?: Record<string, boolean>;
  mono_behaviour_allowlist?: Array<{ assembly_name: string; full_type_name: string; note?: string | undefined }>;
};

export type BridgeInvokeOkResponse = {
  schema_version?: typeof BRIDGE_PROTOCOL_VERSION;
  request_id?: string;
  status: "ok";
  data: unknown;
  diagnostics?: unknown[];
  duration_ms?: number;
};
export type BridgeInvokeErrorResponse = {
  schema_version?: typeof BRIDGE_PROTOCOL_VERSION;
  request_id?: string;
  status: "error";
  error: GatewayError;
  diagnostics?: unknown[];
  duration_ms?: number;
};
export type BridgeInvokeResponse = BridgeInvokeOkResponse | BridgeInvokeErrorResponse;

export type BridgeTransportOptions = {
  timeoutMs?: number;
  deadlineUnixMs?: number;
  requestId?: string;
  signal?: AbortSignal;
  prepareRequest?: () => Promise<void>;
  beforeRequestIssued?: () => void;
  onRequestIssued?: () => void;
};

export interface BridgeClient {
  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown>;
  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse>;
  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown>;
}

export type BridgeJsonRequestOptions = {
  method?: "GET" | "POST";
  body?: string;
  endpointTimeoutMs: number;
  responseLimitBytes: number;
  transport?: BridgeTransportOptions;
};

const HEALTH_RESPONSE_LIMIT_BYTES = 256 * 1024;
const BRIDGE_RESPONSE_LIMIT_BYTES = 4 * 1024 * 1024;
const HARD_RESPONSE_LIMIT_BYTES = 4 * 1024 * 1024;
const HEALTH_TIMEOUT_MS = 5_000;
const BRIDGE_TIMEOUT_MS = 30_000;
const MAX_INVOKE_TIMEOUT_MS = 60 * 60 * 1_000;

export class HttpUnityBridgeClient implements BridgeClient {
  constructor(readonly logger?: OperationalLogger) {}

  async describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    if (selector === "commands" || selector.startsWith("commands:")) {
      const value = await this.requestJson(record, "/commands", this.requestOptions(BRIDGE_RESPONSE_LIMIT_BYTES, BRIDGE_TIMEOUT_MS, transport));
      const catalog = parseBridgePayload(record, "/commands", () => parseCommandCatalogV1(value), (error) => this.logFailure(error, "catalog"));
      parseBridgePayload(record, "/commands", () => assertBridgeResponseIdentity(record, catalog), (error) => this.logFailure(error, "catalog"));
      return {
        ...catalog,
        catalog_revision: commandCatalogRevision(catalog.commands),
        commands: catalog.commands.map(withDescriptorRevision),
      } as CommandCatalogResponse;
    }

    if (selector.startsWith("command:")) {
      const commandId = selector.slice("command:".length);
      const value = await this.requestJson(record, "/commands", this.requestOptions(BRIDGE_RESPONSE_LIMIT_BYTES, BRIDGE_TIMEOUT_MS, transport));
      const catalog = parseBridgePayload(record, "/commands", () => parseCommandCatalogV1(value), (error) => this.logFailure(error, "catalog"));
      parseBridgePayload(record, "/commands", () => assertBridgeResponseIdentity(record, catalog), (error) => this.logFailure(error, "catalog"));
      const command = catalog.commands.find((candidate) => candidate.id === commandId) ?? null;
      const response = parseBridgePayload(record, "/commands", () => parseSingleCommandResponseV1({
        schema_version: BRIDGE_PROTOCOL_VERSION,
        ...(catalog.instance_id !== undefined ? { instance_id: catalog.instance_id } : {}),
        project: catalog.project,
        commands_hash: catalog.commands_hash,
        ...(catalog.catalog_generation !== undefined ? { catalog_generation: catalog.catalog_generation } : {}),
        command,
      }), (error) => this.logFailure(error, "catalog"));
      return {
        ...response,
        descriptor_revision: response.command ? commandDescriptorRevision(response.command) : null,
        command: response.command ? withDescriptorRevision(response.command) : null,
      } satisfies SingleCommandResponse;
    }

    if (selector === "status") {
      const value = await this.requestJson(record, "/health", this.requestOptions(HEALTH_RESPONSE_LIMIT_BYTES, HEALTH_TIMEOUT_MS, transport));
      const health = parseBridgePayload(record, "/health", () => parseHealthResponseV1(value), (error) => this.logFailure(error, "health"));
      parseBridgePayload(record, "/health", () => assertBridgeResponseIdentity(record, health), (error) => this.logFailure(error, "health"));
      return health;
    }

    const error = new BridgeRequestError("selector_not_supported", bridgeRequestDetails(record, "/describe", undefined, undefined, {
      failure_kind: "selector_not_supported",
    }));
    this.logFailure(error, "describe");
    throw error;
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    const deadlineUnixMs = earlierDeadline(request.deadline_unix_ms, transport?.deadlineUnixMs);
    const body = parseBridgePayload(record, "/invoke", () => serializeBridgeInvokeRequestV1({
      schema_version: BRIDGE_PROTOCOL_VERSION,
      ...(request.request_id !== undefined ? { request_id: request.request_id } : {}),
      project: request.project,
      command: request.command,
      args: request.args,
      ...(request.settings !== undefined ? { settings: request.settings } : {}),
      ...(deadlineUnixMs !== undefined ? { deadline_unix_ms: deadlineUnixMs } : {}),
      ...(request.risk !== undefined ? { risk: request.risk } : {}),
      ...(request.expected_instance_id !== undefined ? { expected_instance_id: request.expected_instance_id } : {}),
      ...(request.expected_catalog_generation !== undefined ? { expected_catalog_generation: request.expected_catalog_generation } : {}),
      ...(request.expected_commands_hash !== undefined ? { expected_commands_hash: request.expected_commands_hash } : {}),
    }), (error) => this.logFailure(error, "invoke"));
    const invokeTransport = mergedTransportOptions(transport, deadlineUnixMs);
    const endpointTimeoutMs = invokeEndpointTimeoutMs(deadlineUnixMs);
    const response = await this.requestJson(record, "/invoke", {
      method: "POST",
      body,
      endpointTimeoutMs,
      responseLimitBytes: BRIDGE_RESPONSE_LIMIT_BYTES,
      ...(invokeTransport ? { transport: invokeTransport } : {}),
    });

    return parseBridgePayload(record, "/invoke", () => parseBridgeInvokeResponseV1(response, request.request_id), (error) => this.logFailure(error, "invoke")) as BridgeInvokeResponse;
  }

  async logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    const value = await this.requestJson(record, `/logs?kind=${encodeURIComponent(kind)}&limit=${limit}`, this.requestOptions(BRIDGE_RESPONSE_LIMIT_BYTES, BRIDGE_TIMEOUT_MS, transport));
    return parseBridgePayload(record, "/logs", () => parseLogsResponseV1(value), (error) => this.logFailure(error, "logs"));
  }

  private requestOptions(responseLimitBytes: number, endpointTimeoutMs: number, transport?: BridgeTransportOptions): BridgeJsonRequestOptions {
    return { responseLimitBytes, endpointTimeoutMs, ...(transport ? { transport } : {}) };
  }

  private async requestJson(record: HeartbeatRecord, pathAndQuery: string, options: BridgeJsonRequestOptions): Promise<unknown> {
    if (record.bridge_port <= 0) {
      const error = new BridgeRequestError("bridge_unreachable", {
        ...bridgeRequestDetails(record, pathAndQuery),
        bridge_request_issued: false,
        bridge_endpoint_available: false,
        lifecycle_status: record.status,
        failure_kind: "lifecycle_unavailable",
        cause_class: "BridgeLifecycleUnavailable",
      });
      this.logFailure(error, bridgeOperation(pathAndQuery));
      throw error;
    }

    const responseLimitBytes = Math.min(options.responseLimitBytes, HARD_RESPONSE_LIMIT_BYTES);
    const controller = new AbortController();
    let abortSource: NonNullable<BridgeRequestErrorDetails["abort_source"]> | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const abort = (source: NonNullable<BridgeRequestErrorDetails["abort_source"]>) => {
      if (!abortSource) {
        abortSource = source;
        controller.abort();
      }
    };
    const callerAbort = () => abort("caller");
    const now = Date.now();
    const bounds = [
      { at: now + Math.max(0, options.endpointTimeoutMs), source: "endpoint_timeout" as const },
      ...(options.transport?.timeoutMs !== undefined ? [{ at: now + Math.max(0, options.transport.timeoutMs), source: "transport_timeout" as const }] : []),
      ...(options.transport?.deadlineUnixMs !== undefined ? [{ at: options.transport.deadlineUnixMs, source: "deadline" as const }] : []),
    ].filter((bound) => Number.isFinite(bound.at)).sort((left, right) => left.at - right.at);
    const earliestBound = bounds[0];

    if (options.transport?.signal?.aborted) {
      abort("caller");
    } else if (earliestBound && earliestBound.at <= now) {
      abort(earliestBound.source);
    }
    if (abortSource) {
      const error = bridgeAbortError(record, pathAndQuery, abortSource, options);
      error.details.bridge_request_issued = false;
      this.logFailure(error, bridgeOperation(pathAndQuery));
      throw error;
    }

    options.transport?.signal?.addEventListener("abort", callerAbort, { once: true });
    if (earliestBound) {
      timer = setTimeout(() => abort(earliestBound.source), Math.min(earliestBound.at - now, 2_147_483_647));
    }

    let response: Response | undefined;
    let requestIssued = false;
    try {
      // Serialization and endpoint checks have succeeded. Preparation may do
      // off-thread durable work, but the final synchronous guard still runs at fetch.
      if (options.transport?.prepareRequest) await options.transport.prepareRequest();
      options.transport?.beforeRequestIssued?.();
      // A synchronous guard can consume the budget before its timer gets CPU.
      // Never issue a request merely because that timer has not fired yet.
      if (earliestBound && Date.now() >= earliestBound.at) abort(earliestBound.source);
      if (controller.signal.aborted) throw bridgeAbortError(record, pathAndQuery, abortSource ?? "caller", options);
      requestIssued = true;
      const responsePromise = fetch(`http://${record.bridge_host}:${record.bridge_port}${pathAndQuery}`, {
        ...(options.method ? { method: options.method } : {}),
        ...(options.body !== undefined ? { body: options.body } : {}),
        redirect: "manual",
        signal: controller.signal,
        headers: {
          Accept: "application/json",
          ...(options.body ? { "Content-Type": "application/json" } : {}),
          ...(record.token ? { "X-SGG-Unity-MCP-Bridge-Token": record.token } : {}),
        },
      });
      try { options.transport?.onRequestIssued?.(); }
      catch (error) { void responsePromise.catch(() => undefined); throw error; }
      response = await responsePromise;

      if (earliestBound && Date.now() >= earliestBound.at) abort(earliestBound.source);
      if (controller.signal.aborted) throw bridgeAbortError(record, pathAndQuery, abortSource ?? "caller", options);

      const responseBytes = await readBoundedResponse(response, responseLimitBytes, record, pathAndQuery);
      if (earliestBound && Date.now() >= earliestBound.at) abort(earliestBound.source);
      if (controller.signal.aborted) throw bridgeAbortError(record, pathAndQuery, abortSource ?? "caller", options);
      const responseText = new TextDecoder("utf-8", { fatal: false }).decode(responseBytes);
      const diagnostics = bridgeResponseDiagnostics(response, responseBytes, responseLimitBytes);

      if (!response.ok) {
        const classification = classifyHttpError(response.status);
        let parsedError: unknown;
        if (isJsonContentType(response.headers.get("content-type"))) {
          try {
            parsedError = JSON.parse(responseText);
          } catch (cause) {
            Object.assign(diagnostics, {
              parse_error_kind: classifyResponseParseError(responseText, cause),
              ...safeCauseDetails(cause),
            });
          }
        } else {
          diagnostics.parse_error_kind = response.headers.has("content-type") ? "invalid_content_type" : "missing_content_type";
        }
        const responseErrorCode = validatedResponseErrorCode(parsedError, record.token);
        throw new BridgeRequestError(classification.code, bridgeRequestDetails(record, pathAndQuery, response.status, undefined, {
          ...diagnostics,
          failure_kind: classification.failureKind,
          http_error_kind: classification.httpErrorKind,
          ...(responseErrorCode ? { response_error_code: responseErrorCode } : {}),
        }));
      }

      const rawResponseContentLength = response.headers.get("content-length");
      const responseContentLength = normalizedContentLength(rawResponseContentLength);
      if (responseBytes.byteLength === 0 && (rawResponseContentLength === null || responseContentLength === 0)) {
        this.logger?.log("SGG-OP-BRIDGE-RESPONSE-001", bridgeOperation(pathAndQuery), "empty", response.status, 0);
      }
      if (!response.headers.has("content-type") && responseBytes.byteLength === 0 && (rawResponseContentLength === null || responseContentLength === 0)) {
        throw new BridgeRequestError("bridge_protocol_error", bridgeRequestDetails(record, pathAndQuery, response.status, undefined, {
          ...diagnostics,
          failure_kind: "response_parse",
          parse_error_kind: "empty_body",
        }));
      }

      if (!isJsonContentType(response.headers.get("content-type"))) {
        throw new BridgeRequestError("bridge_protocol_error", bridgeRequestDetails(record, pathAndQuery, response.status, undefined, {
          ...diagnostics,
          failure_kind: "invalid_content_type",
          parse_error_kind: response.headers.has("content-type") ? "invalid_content_type" : "missing_content_type",
        }));
      }
      try {
        const parsed = JSON.parse(responseText) as unknown;
        this.logger?.log("SGG-OP-BRIDGE-RESPONSE-001", bridgeOperation(pathAndQuery), "completed", response.status, responseBytes.byteLength);
        return parsed;
      } catch (cause) {
        throw new BridgeRequestError(
          "bridge_protocol_error",
          bridgeRequestDetails(record, pathAndQuery, response.status, cause, {
            ...diagnostics,
            failure_kind: "response_parse",
            parse_error_kind: classifyResponseParseError(responseText, cause),
          }),
        );
      }
    } catch (cause) {
      if (cause instanceof BridgeRequestNotIssuedError) throw cause;
      if (cause instanceof BridgeRequestError) {
        Object.assign(cause.details, safeTransportDetails(options.transport), { bridge_request_issued: requestIssued });
        this.logFailure(cause, bridgeOperation(pathAndQuery));
        throw cause;
      }
      if (abortSource || controller.signal.aborted) {
        const error = bridgeAbortError(record, pathAndQuery, abortSource ?? "caller", options);
        error.details.bridge_request_issued = requestIssued;
        this.logFailure(error, bridgeOperation(pathAndQuery));
        throw error;
      }
      const error = new BridgeRequestError(bridgeRequestFailureCode(cause), bridgeRequestDetails(record, pathAndQuery, response?.status, cause, {
        bridge_request_issued: requestIssued,
        failure_kind: bridgeRequestFailureCode(cause) === "bridge_timeout" ? "timeout" : "connection",
      }));
      this.logFailure(error, bridgeOperation(pathAndQuery));
      throw error;
    } finally {
      if (timer) {
        clearTimeout(timer);
      }
      options.transport?.signal?.removeEventListener("abort", callerAbort);
    }
  }

  private logFailure(error: BridgeRequestError, operation: "health" | "catalog" | "invoke" | "logs" | "describe"): void {
    this.logger?.log("SGG-OP-BRIDGE-FAILURE-001", operation, error.message, error.details.failure_kind ?? "protocol_validation");
  }
}

export class FakeUnityBridgeClient implements BridgeClient {
  readonly #commands: CommandDescriptor[];

  constructor(commands: CommandDescriptor[] = [compileStatusCommand]) {
    this.#commands = commands;
  }

  async describe(record: HeartbeatRecord, selector: string): Promise<unknown> {
    if (selector === "status") {
      return {
        schema_version: BRIDGE_PROTOCOL_VERSION,
        instance_id: record.instance_id,
        project: record.project,
        status: record.status,
        unity_version: record.unity_version,
        commands_hash: record.commands_hash,
        is_compiling: record.status === "compiling",
        is_updating: record.status === "updating",
        is_playing: record.status === "playing",
        ready: record.status === "ready",
        package_update: { status: "current", auto_refresh_enabled: "unknown", refresh_pending: false, package_refresh_required: false, stale_package_resolution: false, fingerprint: "", pending_fingerprint: "", refresh_count: 0, last_refresh_utc: "", message: "" },
        has_unsaved_scene_changes: record.has_unsaved_scene_changes ?? false,
        dirty_scene_count: record.dirty_scene_count ?? 0,
        dirty_scenes: record.dirty_scenes ?? [],
        queue_depth: 0,
      } satisfies HealthResponseV1;
    }

    if (selector === "commands" || selector.startsWith("commands:")) {
      return { schema_version: BRIDGE_PROTOCOL_VERSION, project: record.project, commands_hash: record.commands_hash, catalog_revision: commandCatalogRevision(this.#commands), commands: this.#commands.map(withDescriptorRevision) } satisfies CommandCatalogResponse;
    }

    if (selector.startsWith("command:")) {
      const commandId = selector.slice("command:".length);
      const command = this.#commands.find((candidate) => candidate.id === commandId) ?? null;
      return { schema_version: BRIDGE_PROTOCOL_VERSION, project: record.project, commands_hash: record.commands_hash, descriptor_revision: null, command } satisfies SingleCommandResponse;
    }

    return { project: record.project, selector, data: null };
  }

  async invoke(_record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    if (transport?.prepareRequest) await transport.prepareRequest();
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    const command = this.#commands.find((candidate) => candidate.id === request.command);
    if (!command) {
      throw new Error("command_not_found");
    }

    return {
      schema_version: BRIDGE_PROTOCOL_VERSION,
      ...(request.request_id ? { request_id: request.request_id } : {}),
      status: "ok",
      data: {
        command: request.command,
        args: request.args,
        ...(request.settings ? { settings: request.settings } : {}),
      },
      diagnostics: [],
      duration_ms: 0,
    };
  }

  async logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number): Promise<unknown> {
    return { schema_version: BRIDGE_PROTOCOL_VERSION, entries: [] } satisfies LogsResponseV1;
  }
}

export const compileStatusCommand: CommandDescriptor = {
  id: "compile.status",
  title: "Compile status",
  description: "Read Unity compilation status",
  package: "com.sungeargames.unity-mcp",
  risk: "read",
  idempotency: "safe",
  timeout_ms: 10000,
  schema: {
    type: "object",
    properties: {
      response_mode: { type: "string", enum: ["full", "compact"], default: "full" },
    },
    additionalProperties: false,
  },
};

function bridgeRequestDetails(record: HeartbeatRecord, endpoint: string, httpStatus?: number, cause?: unknown, extra: Partial<BridgeRequestErrorDetails> = {}): BridgeRequestErrorDetails {
  return {
    endpoint: endpoint.split("?", 1)[0] ?? endpoint,
    bridge_host: record.bridge_host,
    bridge_port: record.bridge_port,
    ...(httpStatus !== undefined ? { http_status: httpStatus } : {}),
    ...safeCauseDetails(cause),
    ...extra,
  };
}

function bridgeOperation(endpoint: string): "health" | "catalog" | "invoke" | "logs" {
  if (endpoint.startsWith("/health")) return "health";
  if (endpoint.startsWith("/commands")) return "catalog";
  if (endpoint.startsWith("/invoke")) return "invoke";
  return "logs";
}

function bridgeResponseDiagnostics(response: Response, responseBytes: Uint8Array, limitBytes: number): Partial<BridgeRequestErrorDetails> {
  const contentType = normalizedContentType(response.headers.get("content-type"));
  const contentLength = normalizedContentLength(response.headers.get("content-length"));
  return {
    ...(contentType ? { response_content_type: contentType.slice(0, 120) } : {}),
    ...(contentLength !== undefined ? { response_content_length: contentLength } : {}),
    response_body_bytes: responseBytes.byteLength,
    response_body_empty: responseBytes.byteLength === 0,
    response_body_truncated: false,
    response_body_sha256: createHash("sha256").update(responseBytes).digest("hex"),
    response_body_max_bytes: limitBytes,
  };
}

function classifyResponseParseError(responseText: string, cause: unknown): NonNullable<BridgeRequestErrorDetails["parse_error_kind"]> {
  if (responseText.length === 0) {
    return "empty_body";
  }

  if (cause instanceof SyntaxError && cause.message.toLowerCase().includes("unexpected end")) {
    return "truncated_json";
  }

  return "invalid_json";
}

function normalizedContentType(value: string | null): string | undefined {
  if (value === null) return undefined;
  return value.split(";", 1)[0]?.trim().toLowerCase();
}

function isJsonContentType(value: string | null): boolean {
  const mediaType = normalizedContentType(value);
  return mediaType === "application/json" || /^application\/[a-z0-9!#$&^_.+-]+\+json$/.test(mediaType ?? "");
}

function normalizedContentLength(value: string | null): number | undefined {
  if (value === null || !/^\d+$/.test(value)) return undefined;
  const length = Number(value);
  return Number.isSafeInteger(length) ? length : undefined;
}

function earlierDeadline(left?: number, right?: number): number | undefined {
  if (left === undefined) return right;
  if (right === undefined) return left;
  return Math.min(left, right);
}

function mergedTransportOptions(transport: BridgeTransportOptions | undefined, requestDeadlineUnixMs: number | undefined): BridgeTransportOptions | undefined {
  const deadlineUnixMs = requestDeadlineUnixMs;
  if (!transport && deadlineUnixMs === undefined) return undefined;
  return { ...transport, ...(deadlineUnixMs !== undefined ? { deadlineUnixMs } : {}) };
}

function invokeEndpointTimeoutMs(requestDeadlineUnixMs: number | undefined, now = Date.now()): number {
  if (requestDeadlineUnixMs === undefined || !Number.isFinite(requestDeadlineUnixMs) || requestDeadlineUnixMs <= now) {
    return BRIDGE_TIMEOUT_MS;
  }
  return Math.min(requestDeadlineUnixMs - now, MAX_INVOKE_TIMEOUT_MS);
}

function bridgeAbortError(record: HeartbeatRecord, endpoint: string, abortSource: NonNullable<BridgeRequestErrorDetails["abort_source"]>, options: BridgeJsonRequestOptions): BridgeRequestError {
  return new BridgeRequestError("bridge_timeout", bridgeRequestDetails(record, endpoint, undefined, undefined, {
    failure_kind: "timeout",
    abort_source: abortSource,
    endpoint_timeout_ms: options.endpointTimeoutMs,
    ...safeTransportDetails(options.transport),
  }));
}

function safeTransportDetails(transport: BridgeTransportOptions | undefined): Partial<BridgeRequestErrorDetails> {
  return {
    ...(transport?.timeoutMs !== undefined ? { transport_timeout_ms: transport.timeoutMs } : {}),
    ...(transport?.deadlineUnixMs !== undefined ? { deadline_unix_ms: transport.deadlineUnixMs } : {}),
    ...safeTransportRequestId(transport?.requestId),
  };
}

function safeTransportRequestId(requestId: string | undefined): Pick<BridgeRequestErrorDetails, "request_id"> {
  return requestId && /^[A-Za-z0-9._-]{1,128}$/.test(requestId) ? { request_id: requestId } : {};
}

async function readBoundedResponse(response: Response, limitBytes: number, record: HeartbeatRecord, endpoint: string): Promise<Uint8Array> {
  const contentLength = normalizedContentLength(response.headers.get("content-length"));
  if (contentLength !== undefined && contentLength > limitBytes) {
    await response.body?.cancel().catch(() => undefined);
    throw responseTooLargeError(record, endpoint, response, 0, limitBytes);
  }

  if (!response.body) {
    return new Uint8Array();
  }

  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let totalBytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      totalBytes += value.byteLength;
      if (totalBytes > limitBytes) {
        await reader.cancel().catch(() => undefined);
        throw responseTooLargeError(record, endpoint, response, Math.min(totalBytes, limitBytes), limitBytes);
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const bytes = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}

function responseTooLargeError(record: HeartbeatRecord, endpoint: string, response: Response, responseBytes: number, limitBytes: number): BridgeRequestError {
  const contentType = normalizedContentType(response.headers.get("content-type"));
  const contentLength = normalizedContentLength(response.headers.get("content-length"));
  const httpClassification = response.ok ? undefined : classifyHttpError(response.status);
  return new BridgeRequestError(httpClassification?.code ?? "bridge_protocol_error", bridgeRequestDetails(record, endpoint, response.status, undefined, {
    failure_kind: "response_too_large",
    ...(httpClassification ? { http_error_kind: httpClassification.httpErrorKind } : {}),
    ...(contentType ? { response_content_type: contentType.slice(0, 120) } : {}),
    ...(contentLength !== undefined ? { response_content_length: contentLength } : {}),
    response_body_bytes: responseBytes,
    response_body_truncated: true,
    response_body_max_bytes: limitBytes,
    response_too_large: true,
  }));
}

function safeCauseDetails(cause: unknown): Pick<BridgeRequestErrorDetails, "cause_class"> {
  if (!(cause instanceof Error)) {
    return {};
  }

  if (cause.name === "AbortError" || cause.name === "TimeoutError" || cause.name === "TypeError" || cause.name === "SyntaxError") {
    return { cause_class: cause.name };
  }
  return { cause_class: "Error" };
}

function classifyHttpError(status: number): {
  code: Extract<ErrorCode, "bridge_protocol_error" | "invalid_token">;
  failureKind: NonNullable<BridgeRequestErrorDetails["failure_kind"]>;
  httpErrorKind: NonNullable<BridgeRequestErrorDetails["http_error_kind"]>;
} {
  if (status === 401 || status === 403) return { code: "invalid_token", failureKind: "authentication", httpErrorKind: "authentication" };
  if (status >= 300 && status < 400) return { code: "bridge_protocol_error", failureKind: "redirect", httpErrorKind: "redirect" };
  if (status >= 400 && status < 500) return { code: "bridge_protocol_error", failureKind: "http_error", httpErrorKind: "client" };
  return { code: "bridge_protocol_error", failureKind: "http_error", httpErrorKind: "server" };
}

function validatedResponseErrorCode(value: unknown, bridgeToken?: string): string | undefined {
  if (typeof value !== "object" || value === null || !("error" in value)) return undefined;
  const error = value.error;
  if (typeof error !== "object" || error === null || !("code" in error)) return undefined;
  return typeof error.code === "string" && error.code !== bridgeToken && /^[a-z][a-z0-9_]{0,63}$/.test(error.code) ? error.code : undefined;
}

function bridgeRequestFailureCode(cause: unknown): Extract<ErrorCode, "bridge_unreachable" | "bridge_timeout"> {
  if (cause instanceof Error && (cause.name === "AbortError" || cause.name === "TimeoutError")) {
    return "bridge_timeout";
  }
  return "bridge_unreachable";
}

function withDescriptorRevision(command: CommandDescriptor): CommandDescriptor & { descriptor_revision: string } {
  return { ...command, descriptor_revision: commandDescriptorRevision(command) };
}

function parseBridgePayload<T>(record: HeartbeatRecord, endpoint: string, parse: () => T, onError?: (error: BridgeRequestError) => void): T {
  try {
    return parse();
  } catch (error) {
    if (!(error instanceof BridgeProtocolError)) throw error;
    const diagnostic = error.diagnostic;
    const requestError = new BridgeRequestError(error.code === "protocol_version_mismatch" ? "protocol_version_mismatch" : "bridge_protocol_error", bridgeRequestDetails(record, endpoint, undefined, undefined, {
      failure_kind: "response_shape",
      parse_error_kind: "response_shape_mismatch",
      cause_class: "ResponseShapeMismatch",
      protocol_schema: diagnostic.protocol_schema,
      protocol_reason: diagnostic.reason,
      expected_protocol_version: diagnostic.expected_version,
      ...(diagnostic.received_version !== undefined ? { received_protocol_version: diagnostic.received_version } : {}),
      ...(diagnostic.issues ? { protocol_issues: diagnostic.issues } : {}),
    }));
    onError?.(requestError);
    throw requestError;
  }
}
