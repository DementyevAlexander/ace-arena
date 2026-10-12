import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

import type { GatewayResult } from "./results.js";
import { canonicalTextProjection } from "./canonical-text-projection.js";

export function gatewayResultToMcpToolResult(result: GatewayResult): CallToolResult {
  return {
    content: [
      {
        type: "text",
        text: result.status === "error" ? canonicalTextProjection(result) : gatewayResultSummary(result),
      },
    ],
    structuredContent: { ...result },
    ...(result.status === "error" ? { isError: true } : {}),
  };
}

function gatewayResultSummary(result: GatewayResult): string {
  const fields = [`Gateway result: ${result.status}`];
  if (result.command) fields.push(`command=${boundedSummaryValue(result.command)}`);
  if (result.project) fields.push(`project=${boundedSummaryValue(result.project)}`);
  if (result.status === "error") {
    fields.push(`error=${result.error.code}`);
  } else if (isRecord(result.data)) {
    const operationId = stringField(result.data.operation_id) ?? stringField(result.data.run_id);
    const operationStatus = stringField(result.data.status);
    const operationResult = stringField(result.data.result);
    if (operationId) fields.push(`operation=${boundedSummaryValue(operationId)}`);
    if (operationStatus) fields.push(`operation_status=${boundedSummaryValue(operationStatus)}`);
    if (operationResult) fields.push(`operation_result=${boundedSummaryValue(operationResult)}`);
  }
  fields.push("canonical payload is in structuredContent");
  return `${fields.join("; ")}.`;
}

function boundedSummaryValue(value: string): string {
  const safe = value.replace(/[^A-Za-z0-9._:-]/g, "_");
  return safe.length <= 96 ? safe : `${safe.slice(0, 93)}...`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringField(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}
