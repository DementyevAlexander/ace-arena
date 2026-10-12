import type { GatewayConfig } from "../config/config.js";

export const TEST_RUN_STORAGE_DEFAULT_MAX_BYTES = 200 * 1024 * 1024;
export const TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS = 192_000;
export const TEST_RUN_STORAGE_MAX_BINDINGS = 1_000_000;
export const TEST_RUN_STORAGE_MIN_BYTES = 1024 * 1024;
export const TEST_RUN_STORAGE_MAX_BYTES = 1024 * 1024 * 1024;
export const TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS = 30;

/** Ordinary storage settings are captured once at startup, not live-reloaded. */
export function testRunStorageOptionsFromConfig(config: Pick<GatewayConfig, "test_runs">) {
  const storage = config.test_runs?.storage;
  return {
    maxBytes: storage?.max_bytes ?? TEST_RUN_STORAGE_DEFAULT_MAX_BYTES,
    maxBindings: storage?.max_bindings ?? TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS,
    retentionMs: (storage?.retention_days ?? TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS) * 86_400_000,
  };
}
