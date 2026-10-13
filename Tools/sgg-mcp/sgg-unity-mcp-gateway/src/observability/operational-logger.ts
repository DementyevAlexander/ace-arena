export type OperationalLogLevel = "debug" | "info" | "warn" | "error";

export type OperationalLoggerStatus = {
  schema_version: 1;
  written: number;
  dropped: number;
  errors: number;
};

export type OperationalLogWriter = {
  write(line: string): boolean;
  once(event: "drain", listener: () => void): unknown;
  on?(event: "error", listener: () => void): unknown;
};

type FieldSpec =
  | { type: "boolean" }
  | { type: "integer"; max: number }
  | { type: "enum"; values: readonly string[] }
  | { type: "identifier"; max: number };

type EventSpec = {
  level: OperationalLogLevel;
  fields: ReadonlyArray<readonly [name: string, spec: FieldSpec]>;
};

const LEVELS: Record<OperationalLogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 };
const IDENTIFIER = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const RUN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const bool = Object.freeze({ type: "boolean" } as const);
const count = Object.freeze({ type: "integer", max: Number.MAX_SAFE_INTEGER } as const);
const enumeration = (...values: string[]): FieldSpec => Object.freeze({ type: "enum", values: Object.freeze(values) });
const identifier = (max = 96): FieldSpec => Object.freeze({ type: "identifier", max });
const fields = (...entries: Array<readonly [string, FieldSpec]>): EventSpec["fields"] => Object.freeze(entries);

const EVENT_DEFINITIONS = {
  "SGG-OP-STARTUP-001": { level: "info", fields: fields(["transport", enumeration("http", "stdio")], ["logging_level", enumeration("debug", "info", "warn", "error")], ["audit_enabled", bool], ["policy_configured", bool], ["authentication_configured", bool], ["version", identifier(32)]) },
  "SGG-OP-HTTP-LISTEN-001": { level: "info", fields: fields(["port", { type: "integer", max: 65_535 }]) },
  "SGG-OP-HTTP-SHUTDOWN-001": { level: "info", fields: fields(["state", enumeration("started", "completed")], ["active_sessions", count], ["transient_sessions", count], ["rate_limit_buckets", count]) },
  "SGG-OP-SESSION-CAPACITY-001": { level: "warn", fields: fields(["active_sessions", count], ["max_active_sessions", count]) },
  "SGG-OP-SESSION-CLEANUP-001": { level: "info", fields: fields(["expired_sessions", count], ["active_sessions", count]) },
  "SGG-OP-RATE-CAPACITY-001": { level: "warn", fields: fields(["active_buckets", count], ["max_buckets", count]) },
  "SGG-OP-RATE-CLEANUP-001": { level: "debug", fields: fields(["expired_buckets", count], ["active_buckets", count]) },
  "SGG-OP-REGISTRY-STATE-001": { level: "warn", fields: fields(["state", enumeration("over_capacity", "unstable", "unavailable", "unsafe")], ["scan_files", count], ["scan_overflow", count], ["invalid_records", count]) },
  "SGG-OP-REGISTRY-RECOVERED-001": { level: "info", fields: fields(["scan_files", count], ["invalid_records", count]) },
  "SGG-OP-POLICY-DEGRADED-001": { level: "warn", fields: fields(["error_code", enumeration("policy_reload_failed", "policy_invalid")], ["error_kind", enumeration("missing", "unreadable", "too_large", "unstable", "invalid_json", "invalid_schema")]) },
  "SGG-OP-POLICY-RECOVERED-001": { level: "info", fields: fields() },
  "SGG-OP-BRIDGE-FAILURE-001": { level: "warn", fields: fields(["operation", enumeration("health", "catalog", "invoke", "logs", "describe")], ["error_code", enumeration("bridge_unreachable", "bridge_protocol_error", "protocol_version_mismatch", "bridge_timeout", "invalid_token", "selector_not_supported")], ["failure_kind", enumeration("lifecycle_unavailable", "authentication", "redirect", "http_error", "invalid_content_type", "response_too_large", "response_parse", "response_shape", "selector_not_supported", "timeout", "connection", "protocol_validation")]) },
  "SGG-OP-BRIDGE-RESPONSE-001": { level: "info", fields: fields(["operation", enumeration("health", "catalog", "invoke", "logs", "describe")], ["state", enumeration("completed", "empty")], ["http_status", { type: "integer", max: 599 }], ["response_body_bytes", count]) },
  "SGG-OP-MCP-HTTP-RESPONSE-001": { level: "info", fields: fields(["request_kind", enumeration("initialize", "tools_call", "batch", "other")], ["state", enumeration("finished", "closed")], ["http_status", { type: "integer", max: 599 }], ["headers_sent", bool], ["content_length_present", bool], ["content_length_matches", bool], ["response_body_write_bytes", count], ["response_body_bytes", count]) },
  "SGG-OP-AUDIT-STATE-001": { level: "warn", fields: fields(["state", enumeration("unavailable")]) },
  "SGG-OP-AUDIT-RECOVERED-001": { level: "info", fields: fields() },
  "SGG-OP-APPROVAL-STATE-001": { level: "warn", fields: fields(["state", enumeration("unavailable", "capacity_exhausted")], ["records", count], ["bytes", count], ["quarantine_files", count]) },
  "SGG-OP-APPROVAL-RECOVERED-001": { level: "info", fields: fields(["records", count], ["bytes", count], ["quarantine_files", count]) },
} as const satisfies Record<string, EventSpec>;

export type OperationalEventCode = keyof typeof EVENT_DEFINITIONS;
export const OPERATIONAL_EVENTS: Readonly<Record<OperationalEventCode, EventSpec>> = Object.freeze(Object.assign(Object.create(null) as Record<OperationalEventCode, EventSpec>, EVENT_DEFINITIONS));

export type OperationalLoggerOptions = {
  level: OperationalLogLevel;
  runId?: string;
  now?: () => Date;
  writer?: OperationalLogWriter;
};

export class OperationalLogger {
  readonly #level: OperationalLogLevel;
  readonly #runId: string | undefined;
  readonly #now: () => Date;
  readonly #writer: OperationalLogWriter;
  #blocked = false;
  #drainAttached = false;
  #failed = false;
  #written = 0;
  #dropped = 0;
  #errors = 0;

  constructor(options: OperationalLoggerOptions) {
    this.#level = options.level;
    this.#runId = safeRunId(options.runId);
    this.#now = options.now ?? (() => new Date());
    this.#writer = options.writer ?? process.stderr;
    try {
      this.#writer.on?.("error", () => {
        this.#errors = increment(this.#errors);
        this.#failed = true;
        this.#blocked = false;
        this.#drainAttached = false;
      });
    } catch {
      this.#errors = increment(this.#errors);
      this.#failed = true;
    }
  }

  log(code: OperationalEventCode, ...values: unknown[]): void {
    if (typeof code !== "string") {
      this.failClosed();
      return;
    }
    const spec = OPERATIONAL_EVENTS[code];
    if (!spec) {
      this.failClosed();
      return;
    }
    if (LEVELS[spec.level] < LEVELS[this.#level]) {
      this.#dropped = increment(this.#dropped);
      return;
    }

    try {
      if (values.length !== spec.fields.length) throw new TypeError("invalid operational field count");
      const event = Object.create(null) as Record<string, string | number | boolean>;
      event.schema_version = 1;
      event.timestamp = safeTimestamp(this.#now);
      event.level = spec.level;
      event.event_code = code;
      if (this.#runId) event.run_id = this.#runId;
      for (let index = 0; index < spec.fields.length; index += 1) {
        const [name, fieldSpec] = spec.fields[index]!;
        event[name] = validatePrimitive(fieldSpec, values[index]);
      }
      this.write(`${JSON.stringify(event)}\n`);
    } catch {
      this.failClosed();
    }
  }

  status(): OperationalLoggerStatus {
    return { schema_version: 1, written: this.#written, dropped: this.#dropped, errors: this.#errors };
  }

  private failClosed(): void {
    this.#errors = increment(this.#errors);
    this.write(`{"schema_version":1,"timestamp":"${safeTimestamp(this.#now)}","level":"error","event_code":"SGG-OP-LOGGER-001","redacted":true}\n`);
  }

  private write(line: string): void {
    if (this.#failed || this.#blocked) {
      this.#dropped = increment(this.#dropped);
      return;
    }
    try {
      const accepted = this.#writer.write(line);
      this.#written = increment(this.#written);
      if (accepted !== false) return;
      this.#blocked = true;
      if (this.#drainAttached) return;
      this.#drainAttached = true;
      try {
        this.#writer.once("drain", () => {
          this.#blocked = false;
          this.#drainAttached = false;
        });
      } catch {
        this.#errors = increment(this.#errors);
        this.#failed = true;
        this.#blocked = false;
        this.#drainAttached = false;
      }
    } catch {
      this.#errors = increment(this.#errors);
      this.#dropped = increment(this.#dropped);
      this.#failed = true;
    }
  }
}

function validatePrimitive(spec: FieldSpec, value: unknown): string | number | boolean {
  if (spec.type === "boolean" && typeof value === "boolean") return value;
  if (spec.type === "integer" && typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= spec.max) return value;
  if (spec.type === "enum" && typeof value === "string" && spec.values.includes(value)) return value;
  if (spec.type === "identifier" && typeof value === "string" && value.length <= spec.max && IDENTIFIER.test(value)) return value;
  throw new TypeError("invalid operational field value");
}

function safeRunId(value: unknown): string | undefined {
  return typeof value === "string" && RUN_ID.test(value) ? value : undefined;
}

function safeTimestamp(now: () => Date): string {
  try {
    const value = now().toISOString();
    return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value) ? value : "1970-01-01T00:00:00.000Z";
  } catch {
    return "1970-01-01T00:00:00.000Z";
  }
}

function increment(value: number): number {
  return Math.min(Number.MAX_SAFE_INTEGER, value + 1);
}
