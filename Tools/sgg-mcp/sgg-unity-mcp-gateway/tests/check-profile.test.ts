import { describe, expect, it, vi } from "vitest";
import {
  CHECK_PROFILES,
  UNITY_VALIDATION_BOUNDARY,
  resolveNpmInvocation,
  runCheckProfile,
  runNpmScript,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../scripts/check-profile.mjs";

describe("check profiles", () => {
  it("prefers the npm CLI JavaScript path with the current Node executable", () => {
    expect(resolveNpmInvocation("check:docs", {
      execPath: "C:\\Program Files\\nodejs\\node.exe",
      platform: "win32",
      env: {
        npm_execpath: "C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js",
        ComSpec: "C:\\Windows\\System32\\cmd.exe",
      },
    })).toEqual({
      command: "C:\\Program Files\\nodejs\\node.exe",
      args: [
        "C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js",
        "run",
        "check:docs",
      ],
    });
  });

  it("uses an explicit ComSpec npm.cmd fallback on Windows", () => {
    expect(resolveNpmInvocation("check:docs", {
      platform: "win32",
      env: { ComSpec: "C:\\Windows\\System32\\cmd.exe" },
    })).toEqual({
      command: "C:\\Windows\\System32\\cmd.exe",
      args: ["/d", "/s", "/c", "npm.cmd", "run", "check:docs"],
    });
  });

  it("uses the direct npm fallback on POSIX", () => {
    expect(resolveNpmInvocation("check:docs", { platform: "linux", env: {} })).toEqual({
      command: "npm",
      args: ["run", "check:docs"],
    });
  });

  it("reports npm launch errors with their command and system details", () => {
    const launchError = Object.assign(new Error("spawn npm ENOENT"), { code: "ENOENT" });
    const spawn = vi.fn(() => ({ error: launchError, status: null, signal: null }));

    expect(() => runNpmScript("check:docs", {
      platform: "linux",
      env: {},
      spawn,
    })).toThrow("could not launch npm script check:docs with npm: ENOENT spawn npm ENOENT");
    expect(spawn).toHaveBeenCalledWith("npm", ["run", "check:docs"], {
      stdio: "inherit",
      env: {},
    });
  });

  it("keeps the fast profile ordered and bounded", () => {
    expect(CHECK_PROFILES.fast).toEqual([
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
    ]);
  });

  it("keeps the complete static profile ordered and excludes real Unity validation", () => {
    expect(CHECK_PROFILES.static).toEqual([
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
    ]);
    expect(CHECK_PROFILES.static).not.toContain("check:unity-editor");
    expect(CHECK_PROFILES.static).not.toContain("check:unity-editor-required");
    expect(CHECK_PROFILES.static).not.toContain("check:unity-editor-matrix");
  });

  it("reports the boundary and runs members in order without spawning the suite", () => {
    const calls: string[] = [];
    const runScript = vi.fn((script: string) => {
      calls.push(script);
      return 0;
    });
    const output: string[] = [];

    expect(runCheckProfile("static", { runScript, log: (line: string) => output.push(line) })).toBe(0);
    expect(calls).toEqual(CHECK_PROFILES.static);
    expect(output).toContain(`[check-profile] ${UNITY_VALIDATION_BOUNDARY}`);
    expect(UNITY_VALIDATION_BOUNDARY).toContain("Unity validation: NOT RUN");
  });

  it("stops at the first failed member", () => {
    const runScript = vi.fn((script: string) => (script === "check:docs" ? 2 : 0));

    expect(runCheckProfile("fast", { runScript, log: () => undefined })).toBe(2);
    expect(runScript.mock.calls.map(([script]) => script)).toEqual([
      "typecheck",
      "test",
      "check:version",
      "check:docs",
    ]);
  });
});
