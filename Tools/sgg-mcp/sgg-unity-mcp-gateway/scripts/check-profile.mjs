#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { pathToFileURL } from "node:url";

export const CHECK_PROFILES = Object.freeze({
  fast: Object.freeze([
    "typecheck",
    "test",
    "check:version",
    "check:docs",
    "check:command-docs",
    "check:forbidden-identifiers",
    "check:unity-package",
    "check:gateway-runtime",
    "check:notices",
    "check:command-descriptors",
    "check:policy",
    "check:manifest-security",
  ]),
  static: Object.freeze([
    "typecheck",
    "test",
    "check:agent-evals",
    "check:version",
    "check:docs",
    "check:command-docs",
    "check:forbidden-identifiers",
    "check:unity-package",
    "check:asset-index-benchmark",
    "check:gateway-runtime",
    "check:notices",
    "check:changelog",
    "check:workflow",
    "check:command-descriptors",
    "check:policy",
    "check:skill",
    "check:manifest-security",
  ]),
});

export const UNITY_VALIDATION_BOUNDARY =
  "Unity validation: NOT RUN (static Node/repository checks only; run check:unity-editor or check:unity-editor-matrix explicitly)";

export function resolveNpmInvocation(script, options = {}) {
  const env = options.env ?? process.env;
  const npmExecPath = typeof env.npm_execpath === "string" ? env.npm_execpath.trim() : "";
  if (npmExecPath) {
    return {
      command: options.execPath ?? process.execPath,
      args: [npmExecPath, "run", script],
    };
  }

  if ((options.platform ?? process.platform) === "win32") {
    return {
      command: env.ComSpec || env.COMSPEC || "cmd.exe",
      args: ["/d", "/s", "/c", "npm.cmd", "run", script],
    };
  }

  return { command: "npm", args: ["run", script] };
}

export function runNpmScript(script, options = {}) {
  const invocation = resolveNpmInvocation(script, options);
  const spawn = options.spawn ?? spawnSync;
  const result = spawn(invocation.command, invocation.args, {
    stdio: "inherit",
    env: options.env ?? process.env,
  });
  if (result.error) {
    const code = typeof result.error.code === "string" ? ` ${result.error.code}` : "";
    throw new Error(
      `could not launch npm script ${script} with ${invocation.command}:${code} ${result.error.message}`,
      { cause: result.error },
    );
  }
  if (result.status === null) {
    throw new Error(`npm script ${script} ended without an exit status${result.signal ? ` (${result.signal})` : ""}`);
  }
  return result.status;
}

export function runCheckProfile(profileName, options = {}) {
  const scripts = CHECK_PROFILES[profileName];
  if (!scripts) {
    throw new Error(`unknown check profile: ${profileName}`);
  }

  const log = options.log ?? console.log;
  const runScript = options.runScript ?? runNpmScript;
  log(`[check-profile] profile=${profileName}`);
  log(`[check-profile] ${UNITY_VALIDATION_BOUNDARY}`);

  for (const script of scripts) {
    log(`[check-profile] running npm run ${script}`);
    const status = runScript(script);
    if (status !== 0) {
      log(`[check-profile] failed npm run ${script} (exit ${status})`);
      return status;
    }
  }

  log(`[check-profile] ${profileName} ok`);
  return 0;
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  try {
    process.exitCode = runCheckProfile(process.argv[2]);
  } catch (error) {
    console.error(`[check-profile] ${error instanceof Error ? error.message : String(error)}`);
    process.exitCode = 1;
  }
}
