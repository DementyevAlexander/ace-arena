import { ApprovalStore } from "./approval-store.js";
import { expandEnvPlaceholders } from "../config/config.js";

export type ApprovalCliIo = {
  stdout?: (text: string) => void;
  stderr?: (text: string) => void;
  env?: NodeJS.ProcessEnv;
};

type ParsedOptions = {
  values: Record<string, string>;
  includeExpired: boolean;
};

export function runApprovalCli(argv: string[], io: ApprovalCliIo = {}): number {
  const stdout = io.stdout ?? ((text) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text) => process.stderr.write(text));
  const command = argv[0];

  try {
    if (!command || command === "--help" || command === "-h") {
      stderr(approvalHelpText());
      return command ? 0 : 1;
    }

    if (command !== "list" && command !== "pending" && command !== "approve") {
      throw new Error(`Unknown approval command: ${command}`);
    }

    const options = parseOptions(argv.slice(1));
    const dir = expandEnvPlaceholders(requireOption(options, "dir"), io.env ?? process.env);
    if (command === "list") {
      const approvals = new ApprovalStore(dir).list({
        ...(options.values.project ? { project: options.values.project } : {}),
        includeExpired: options.includeExpired,
      });
      stdout(`${JSON.stringify({ approvals }, null, 2)}\n`);
      return 0;
    }

    if (command === "pending") {
      const pendingRequests = new ApprovalStore(dir).listPending({
        ...(options.values.project ? { project: options.values.project } : {}),
        includeExpired: options.includeExpired,
      });
      stdout(`${JSON.stringify({ pending_requests: pendingRequests }, null, 2)}\n`);
      return 0;
    }

    if (command === "approve") {
      const pendingId = requireOption(options, "pending-id");
      const ttlMs = parsePositiveInteger(options.values["ttl-ms"] ?? "300000", "--ttl-ms");
      const record = new ApprovalStore(dir).approvePending({ pending_id: pendingId, ttl_ms: ttlMs });
      stdout(`${JSON.stringify(record, null, 2)}\n`);
      return 0;
    }

    throw new Error(`Unknown approval command: ${command}`);
  } catch (error) {
    stderr(`${error instanceof Error ? error.message : String(error)}\n`);
    return 1;
  }
}

function parseOptions(argv: string[]): ParsedOptions {
  const values: Record<string, string> = {};
  let includeExpired = false;
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--include-expired") {
      includeExpired = true;
      continue;
    }
    if (!arg?.startsWith("--")) {
      throw new Error(`Unexpected argument: ${arg}`);
    }
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`${arg} requires a value`);
    }
    values[arg.slice(2)] = value;
    index += 1;
  }
  return { values, includeExpired };
}

function requireOption(options: ParsedOptions, key: string): string {
  const value = options.values[key];
  if (!value) {
    throw new Error(`--${key} is required`);
  }
  return value;
}

function parsePositiveInteger(value: string, label: string): number {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`${label} must be a positive integer`);
  }
  return parsed;
}

function approvalHelpText(): string {
  return `Usage:
  sgg-unity-mcp-gateway approval list --dir path [--project id] [--include-expired]
  sgg-unity-mcp-gateway approval pending --dir path [--project id] [--include-expired]
  sgg-unity-mcp-gateway approval approve --dir path --pending-id id [--ttl-ms ms]
`;
}
