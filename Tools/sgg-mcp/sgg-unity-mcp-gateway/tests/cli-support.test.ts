import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { parse } from "jsonc-parser";
import { afterEach, describe, expect, it, vi } from "vitest";

import { parseSetupOptions, parseStdioShimOptions, resolveStdioShimConnection, runDoctorCli, runReportProblemCli, runSetupCli, runWaitReadyCli } from "../src/cli.js";
import type { UnityCrashReporterRuntime } from "../src/diagnostics/unity-crash-reporter.js";
import { RECOMMENDED_MCP_CLIENT_TIMEOUT_MS } from "../src/mcp/sdk-server.js";
import type { HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-12T10:00:00.000Z");

afterEach(() => {
  vi.restoreAllMocks();
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("CLI support helpers", () => {
  it("parses setup helper options", () => {
    expect(parseSetupOptions(["opencode", "--config", "config/gateway.json", "--output", "opencode.json", "--write"])).toMatchObject({
      target: "opencode",
      configPath: "config/gateway.json",
      outputPath: "opencode.json",
      connectionMode: "remote-http",
      mcpPath: "/mcp",
      write: true,
      json: false,
      help: false,
    });
  });

  it("parses stdio-shim options", () => {
    expect(parseStdioShimOptions(["--url", "http://127.0.0.1:27182/mcp", "--token-env", "LOCAL_TOKEN", "--legacy-text-result"])).toMatchObject({
      url: "http://127.0.0.1:27182/mcp",
      mcpPath: "/mcp",
      tokenEnv: "LOCAL_TOKEN",
      legacyTextResult: true,
      help: false,
    });
    expect(parseStdioShimOptions([]).legacyTextResult).toBe(false);
  });

  it("derives stdio-shim URL and token env from config without leaking token", () => {
    const { configPath } = makeFixture();

    const resolved = resolveStdioShimConnection({ configPath, mcpPath: "/custom-mcp", tokenEnv: "SGG_UNITY_MCP_TOKEN", help: false }, {
      SGG_UNITY_MCP_TOKEN: "secret-token",
    });

    expect(resolved).toEqual({
      url: "http://127.0.0.1:27182/custom-mcp",
      tokenEnv: "SGG_UNITY_MCP_TOKEN",
      token: "secret-token",
    });
  });

  it("prefers stdio-shim URL from environment and explicit token env", () => {
    const resolved = resolveStdioShimConnection({ mcpPath: "/mcp", tokenEnv: "LOCAL_TOKEN", help: false }, {
      SGG_UNITY_MCP_URL: "http://localhost:3000/custom",
      LOCAL_TOKEN: "local-secret",
    });

    expect(resolved).toEqual({
      url: "http://localhost:3000/custom",
      tokenEnv: "LOCAL_TOKEN",
      token: "local-secret",
    });
  });

  it("rejects stdio-shim URLs with embedded credentials", () => {
    expect(() => resolveStdioShimConnection({ url: "http://user:secret@127.0.0.1:27182/mcp", mcpPath: "/mcp", tokenEnv: "SGG_UNITY_MCP_TOKEN", help: false }, {})).toThrow(
      "stdio-shim URL must not contain credentials",
    );
  });

  it("reports doctor status without leaking token values", () => {
    const { configPath, policyPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", configPath, "--policy", policyPath], {
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const text = output.join("");
    expect(exitCode).toBe(0);
    expect(text).toContain("config: ok");
    expect(text).toContain("policy: ok");
    expect(text).toContain("token: present");
    expect(text).toContain("1 heartbeat records, 1 live projects");
    expect(text).toContain("unity_crash_reporter: 0 active reporter(s), crashed=no");
    expect(text).toContain("target_client_exposure: not verified");
    expect(text).not.toContain("secret-token");
  });

  it("reads the policy once for doctor inspection and reuses that validated policy for feature hints", () => {
    const { configPath, policyPath } = makeFixture();
    const readFile = vi.spyOn(fs, "readFileSync");
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", configPath, "--policy", policyPath, "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const policyReads = readFile.mock.calls.filter(([file]) => file === policyPath);
    const diagnostics = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(policyReads).toHaveLength(1);
    expect(diagnostics.policy).toMatchObject({ current_file_status: "valid", state: "active" });
    expect(diagnostics.feature_policy_hints).toEqual(expect.arrayContaining([expect.stringContaining("plan.read")]));
  });

  it("reports active Unity Bug Reporter windows in doctor JSON", () => {
    const { configPath } = makeFixture();
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", configPath, "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: crashedUnityCrashReporter(),
    });

    const diagnostics = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(diagnostics).toMatchObject({
      unity_crash_reporter: {
        unity_crashed: true,
        blocking_agent_work: true,
        active_reporters_count: 1,
      },
      summary: { ok: true },
    });
    expect(diagnostics.summary.warnings).toBeGreaterThanOrEqual(1);
  });

  it.each([
    ["missing", undefined, "missing", "policy_reload_failed"],
    ["invalid", "private invalid policy", "invalid", "policy_invalid"],
  ] as const)("classifies a %s policy as an offline current-file inspection without leaking details", (_name, content, status, code) => {
    const { configPath } = makeFixture();
    const policyPath = path.join(path.dirname(configPath), "private-policy.json");
    if (content !== undefined) fs.writeFileSync(policyPath, content);
    const output: string[] = [];
    const exitCode = runDoctorCli(["--config", configPath, "--policy", policyPath, "--json"], { stdout: (text) => output.push(text), now: () => now, unityCrashReporter: idleUnityCrashReporter() });
    const diagnostics = JSON.parse(output.join(""));
    expect(exitCode).toBe(1);
    expect(diagnostics.config).toMatchObject({ ok: true });
    expect(diagnostics.policy).toMatchObject({ inspection: "current_file", current_file_status: status, state: "error", degraded: false, last_error_code: code });
    expect(JSON.stringify(diagnostics.policy)).not.toMatch(/private-policy|private invalid|Zod|ENOENT/);
  });

  it("keeps policy failure classification separate when config loading also fails", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-doctor-dual-failure-"));
    tempDirs.push(dir);
    const configPath = path.join(dir, "gateway.json");
    const policyPath = path.join(dir, "private-policy.json");
    fs.writeFileSync(configPath, "invalid gateway config");
    fs.writeFileSync(policyPath, "private invalid policy details");
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", configPath, "--policy", policyPath, "--json"], { stdout: (text) => output.push(text), now: () => now });
    const diagnostics = JSON.parse(output.join(""));

    expect(exitCode).toBe(1);
    expect(diagnostics).toMatchObject({
      config: { ok: false },
      policy: { current_file_status: "invalid", state: "error", last_error_code: "policy_invalid", last_error_kind: "invalid_json" },
      summary: { errors: 2, ok: false },
    });
    expect(JSON.stringify(diagnostics.policy)).not.toMatch(/private-policy|private invalid|Zod|SyntaxError/);
  });

  it("reports WSL loopback namespace guidance in doctor JSON", () => {
    const { configPath } = makeFixture();
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", configPath, "--json"], {
      env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const diagnostics = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(diagnostics.wsl_loopback_reachability).toMatchObject({
      code: "windows_loopback_not_wsl_reachable",
      windows_loopback_url: "http://127.0.0.1:27182/mcp",
      recommended_connection_mode: "windows_stdio_shim",
      direct_wsl_loopback_may_target_wsl_namespace: true,
      protected_remote_http_eligible: false,
    });
    expect(diagnostics.endpoint_recommendation).toEqual(diagnostics.wsl_loopback_reachability);
    expect(diagnostics.target_client_exposure.gateway_url_scope).toBe("windows_local_only_not_wsl_usable");
    expect(diagnostics.summary.warnings).toBeGreaterThanOrEqual(1);
    expect(output.join("")).toContain("Windows stdio shim");
    expect(output.join("")).not.toContain("secret-token");
  });

  it("returns non-zero when doctor cannot load config", () => {
    const output: string[] = [];

    const exitCode = runDoctorCli(["--config", path.join(os.tmpdir(), "missing-gateway-config.json")], {
      stdout: (text) => output.push(text),
    });

    expect(exitCode).toBe(1);
    expect(output.join("")).toContain("config: error");
  });

  it("uses installed LocalAppData config when cwd has no gateway config", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    const cwd = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-project-"));
    const localAppData = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-localappdata-"));
    tempDirs.push(cwd, localAppData);
    const installedConfigDir = path.join(localAppData, "SGG", "UnityMcpGateway", "config");
    fs.mkdirSync(installedConfigDir, { recursive: true });
    fs.copyFileSync(configPath, path.join(installedConfigDir, "gateway.json"));
    const output: string[] = [];

    const exitCode = runDoctorCli(["--json"], {
      cwd,
      env: { LOCALAPPDATA: localAppData, SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const diagnostics = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(diagnostics.config).toMatchObject({ path: path.join(installedConfigDir, "gateway.json"), ok: true });
    expect(diagnostics.token).toMatchObject({ configured: true, present: true });
    expect(diagnostics.registry).toMatchObject({ liveProjectsCount: 1 });
  });

  it("dry-runs opencode setup without leaking token values", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.json");
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--json"], {
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan).toMatchObject({ target: "opencode", mode: "dry-run", changes: ["add: mcp.servers.sgg-unity-mcp"] });
    expect(plan.patch.mcp.servers["sgg-unity-mcp"]).toMatchObject({
      type: "remote",
      url: "http://127.0.0.1:27182/mcp",
      disabled: false,
      oauth: false,
      timeout: { startup: 30_000, catalog: 30_000, execution: RECOMMENDED_MCP_CLIENT_TIMEOUT_MS },
    });
    expect(JSON.stringify(plan.patch)).not.toContain("--legacy-text-result");
    expect(plan.checklist).toEqual(expect.arrayContaining([
      expect.objectContaining({ step: "create_project_or_global_opencode_config", expected_tools: expect.arrayContaining(["sgg_unity_projects"]) }),
      expect.objectContaining({ step: "restart_or_reload_opencode_session", failure_code_if_missing: "target_client_tools_missing_after_gateway_tools_list_ok" }),
    ]));
    expect(output.join("")).toContain("{env:SGG_UNITY_MCP_TOKEN}");
    expect(output.join("")).toContain("target-client config/API exposure");
    expect(output.join("")).not.toContain("secret-token");
    expect(fs.existsSync(outputPath)).toBe(false);
  });

  it("defaults WSL opencode setup to the Windows stdio shim instead of remote loopback", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--json"], {
      env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.connection_mode).toBe("windows-stdio");
    expect(plan.endpoint_recommendation).toMatchObject({
      code: "windows_loopback_not_wsl_reachable",
      recommended_connection_mode: "windows_stdio_shim",
      protected_remote_http_eligible: false,
    });
    expect(plan.patch.mcp.servers["sgg-unity-mcp"]).toMatchObject({ type: "local", disabled: false, protocol: "legacy", timeout: { startup: 30_000, catalog: 30_000, execution: RECOMMENDED_MCP_CLIENT_TIMEOUT_MS } });
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command.join(" ")).toContain("stdio-shim --url 'http://127.0.0.1:27182/mcp'");
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command.join(" ")).toContain("--legacy-text-result");
    expect(output.join("")).not.toContain("secret-token");
  });

  it("does not treat WSLENV alone as proof that setup is running inside WSL", () => {
    const { configPath } = makeFixture();
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--json"], {
      env: { WSLENV: "LOCALAPPDATA/up", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.connection_mode).toBe("remote-http");
    expect(plan.endpoint_recommendation).toBeUndefined();
    expect(plan.patch.mcp.servers["sgg-unity-mcp"]).toMatchObject({ type: "remote", url: "http://127.0.0.1:27182/mcp" });
  });

  it("refuses explicit WSL remote setup while the Gateway is bound to Windows loopback", () => {
    const { configPath } = makeFixture();
    const errors: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--connection-mode", "remote-http"], {
      env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stderr: (text) => errors.push(text),
    });

    expect(exitCode).toBe(1);
    expect(errors.join("")).toContain("windows_loopback_not_wsl_reachable");
    expect(errors.join("")).toContain("--connection-mode windows-stdio");
    expect(errors.join("")).not.toContain("secret-token");
  });

  it("advertises explicit WSL Remote HTTP only after all protection preconditions are satisfied", () => {
    const { configPath } = makeFixture();
    const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
    config.mcp.host = "0.0.0.0";
    config.mcp.allow_non_loopback = true;
    config.mcp.allowed_origins = ["http://localhost:4096"];
    fs.writeFileSync(configPath, JSON.stringify(config));
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--connection-mode", "remote-http", "--json"], {
      env: {
        WSL_DISTRO_NAME: "Ubuntu",
        SGG_UNITY_MCP_TOKEN: "secret-token",
        SGG_UNITY_MCP_WINDOWS_HOST: "172.28.64.1",
      },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.connection_mode).toBe("remote-http");
    expect(plan.endpoint_recommendation).toMatchObject({
      code: "protected_remote_http_configured",
      protected_remote_http_eligible: true,
      protected_remote_http_url: "http://172.28.64.1:27182/mcp",
      recommended_connection_mode: "remote_http",
    });
    expect(plan.patch.mcp.servers["sgg-unity-mcp"]).toMatchObject({
      type: "remote",
      url: "http://172.28.64.1:27182/mcp",
      headers: { "X-SGG-Unity-MCP-Token": "{env:SGG_UNITY_MCP_TOKEN}" },
    });
    expect(output.join("")).not.toContain("secret-token");
  });

  it("refuses a mismatched or query-bearing WSL Remote HTTP URL before writing token headers", () => {
    const { configPath } = makeFixture();
    const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
    config.mcp.host = "0.0.0.0";
    config.mcp.allow_non_loopback = true;
    config.mcp.allowed_origins = ["http://localhost:4096"];
    fs.writeFileSync(configPath, JSON.stringify(config));

    for (const url of ["http://hostile.example:27182/mcp", "http://172.28.64.1:27182/mcp?token=secret"]) {
      const errors: string[] = [];
      const exitCode = runSetupCli(["opencode", "--config", configPath, "--connection-mode", "remote-http", "--url", url], {
        env: {
          WSL_DISTRO_NAME: "Ubuntu",
          SGG_UNITY_MCP_TOKEN: "secret-token",
          SGG_UNITY_MCP_WINDOWS_HOST: "172.28.64.1",
        },
        stderr: (text) => errors.push(text),
      });

      expect(exitCode).toBe(1);
      expect(errors.join("")).toContain("protected_remote_http_not_ready");
      expect(errors.join("")).not.toContain("secret-token");
      expect(errors.join("")).not.toContain("hostile.example");
    }
  });

  it("refuses arbitrary token-bearing URLs outside the WSL Remote HTTP path", () => {
    const { configPath } = makeFixture();
    const cases = [
      { env: { SGG_UNITY_MCP_TOKEN: "secret-token" }, mode: "remote-http" },
      { env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" }, mode: "windows-stdio" },
    ];

    for (const testCase of cases) {
      const errors: string[] = [];
      const exitCode = runSetupCli([
        "opencode",
        "--config", configPath,
        "--connection-mode", testCase.mode,
        "--url", "http://hostile.example:27182/mcp?credential=inline-secret",
      ], {
        env: testCase.env,
        stderr: (text) => errors.push(text),
      });

      expect(exitCode).toBe(1);
      expect(errors.join("")).toContain("invalid_opencode_endpoint");
      expect(errors.join("")).not.toContain("hostile.example");
      expect(errors.join("")).not.toContain("inline-secret");
      expect(errors.join("")).not.toContain("secret-token");
    }
  });

  it("dry-runs opencode windows stdio setup without leaking token values", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--connection-mode", "windows-stdio", "--json"], {
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.patch.mcp.servers["sgg-unity-mcp"]).toMatchObject({ type: "local", disabled: false });
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command).toEqual(expect.arrayContaining(["powershell.exe"]));
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command.join(" ")).toContain("stdio-shim --url 'http://127.0.0.1:27182/mcp' --token-env 'SGG_UNITY_MCP_TOKEN'");
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command.join(" ")).toContain("--legacy-text-result");
    expect(output.join("")).not.toContain("secret-token");
  });

  it("keeps a concrete non-loopback bind address for the Windows stdio shim", () => {
    const { configPath } = makeFixture();
    const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
    config.mcp.host = "192.168.50.20";
    config.mcp.allow_non_loopback = true;
    config.mcp.allowed_origins = ["http://localhost:4096"];
    fs.writeFileSync(configPath, JSON.stringify(config));
    const output: string[] = [];

    const exitCode = runSetupCli(["opencode", "--config", configPath, "--connection-mode", "windows-stdio", "--json"], {
      env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.patch.mcp.servers["sgg-unity-mcp"].command.join(" ")).toContain("stdio-shim --url 'http://192.168.50.20:27182/mcp'");
  });

  it("writes opencode jsonc setup while preserving existing settings", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, `{
  // keep user settings
  "theme": "dark",
  "mcp": {
    "other-server": {
      "type": "remote",
      "url": "https://example.com/mcp",
    },
  },
}
`);

    const output: string[] = [];
    const exitCode = runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--connection-mode", "windows-stdio", "--write"], {
      stdout: (text) => output.push(text),
    });

    expect(exitCode).toBe(0);
    const writtenSource = fs.readFileSync(outputPath, "utf8");
    const written = parse(writtenSource);
    expect(written.theme).toBe("dark");
    expect(written.mcp["other-server"]).toEqual({ type: "remote", url: "https://example.com/mcp" });
    expect(written.mcp.servers["sgg-unity-mcp"]).toMatchObject({ type: "local", disabled: false });
    expect(writtenSource).toContain("// keep user settings");
    expect(writtenSource).toContain('"other-server"');
    expect(output.join("")).toContain("mode: write");
  });

  it("patches only owned OpenCode fields while preserving JSONC text and unknown server fields", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, [
      "{",
      "\t// top-level user comment",
      "\t\"theme\": \"dark\",",
      "\t\"mcp\": {",
      "\t\t\"sgg-unity-mcp\": {",
      "\t\t\t// unknown field comment",
      "\t\t\t\"custom\": { \"keep\": true },",
      "\t\t\t\"type\": \"remote\",",
      "\t\t\t\"url\": \"http://old.invalid/mcp\",",
      "\t\t\t\"headers\": { \"X-SGG-Unity-MCP-Token\": \"$OLD_TOKEN\", \"X-Custom\": \"keep\" },",
      "\t\t\t\"enabled\": false,",
      "\t\t\t\"timeout\": 30000,",
      "\t\t},",
      "\t\t\"other-server\": { \"type\": \"remote\", \"url\": \"https://example.com/mcp\" },",
      "\t},",
      "}",
      "",
    ].join("\r\n"));
    if (process.platform !== "win32") fs.chmodSync(outputPath, 0o600);

    expect(runSetupCli([
      "opencode", "--config", configPath, "--output", outputPath,
      "--connection-mode", "windows-stdio", "--write",
    ], { env: {}, stdout: () => {} })).toBe(0);

    let source = fs.readFileSync(outputPath, "utf8");
    let written = parse(source);
    expect(source).toContain("// top-level user comment");
    expect(source).toContain("// unknown field comment");
    expect(source.replace(/\r\n/g, "")).not.toContain("\n");
    expect(written.theme).toBe("dark");
    expect(written.mcp["other-server"].url).toBe("https://example.com/mcp");
    expect(written.mcp.servers["sgg-unity-mcp"].custom).toEqual({ keep: true });
    expect(written.mcp.servers["sgg-unity-mcp"].type).toBe("local");
    expect(written.mcp.servers["sgg-unity-mcp"]).not.toHaveProperty("url");
    expect(written.mcp.servers["sgg-unity-mcp"].headers).toEqual({ "X-Custom": "keep" });
    if (process.platform !== "win32") expect(fs.statSync(outputPath).mode & 0o777).toBe(0o600);
    const localSource = source;
    const rename = vi.spyOn(fs, "renameSync");
    expect(runSetupCli([
      "opencode", "--config", configPath, "--output", outputPath,
      "--connection-mode", "windows-stdio", "--write",
    ], { env: {}, stdout: () => {} })).toBe(0);
    expect(fs.readFileSync(outputPath, "utf8")).toBe(localSource);
    expect(rename).not.toHaveBeenCalled();
    rename.mockRestore();

    const gatewayConfig = JSON.parse(fs.readFileSync(configPath, "utf8"));
    delete gatewayConfig.mcp.token_env;
    fs.writeFileSync(configPath, JSON.stringify(gatewayConfig));
    expect(runSetupCli([
      "opencode", "--config", configPath, "--output", outputPath,
      "--connection-mode", "remote-http", "--write",
    ], { env: {}, stdout: () => {} })).toBe(0);

    source = fs.readFileSync(outputPath, "utf8");
    written = parse(source);
    expect(source).toContain("// unknown field comment");
    expect(written.mcp.servers["sgg-unity-mcp"].custom).toEqual({ keep: true });
    expect(written.mcp.servers["sgg-unity-mcp"].type).toBe("remote");
    expect(written.mcp.servers["sgg-unity-mcp"]).not.toHaveProperty("command");
    expect(written.mcp.servers["sgg-unity-mcp"].headers).toEqual({ "X-Custom": "keep" });
  });

  it("fails closed on duplicate keys in the owned OpenCode section", () => {
    const { configPath } = makeFixture();
    const cases = [
      '{ "mcp": {}, "mcp": {} }',
      '{ "mcp": { "sgg-unity-mcp": {}, "sgg-unity-mcp": {} } }',
      '{ "mcp": { "sgg-unity-mcp": { "type": "remote", "type": "local" } } }',
    ];
    for (const source of cases) {
      const outputPath = path.join(path.dirname(configPath), `duplicate-${cases.indexOf(source)}.jsonc`);
      fs.writeFileSync(outputPath, source);
      const errors: string[] = [];

      expect(runSetupCli([
        "opencode", "--config", configPath, "--output", outputPath,
        "--connection-mode", "windows-stdio", "--write",
      ], { env: {}, stdout: () => {}, stderr: (text) => errors.push(text) })).toBe(1);
      expect(errors.join("")).toContain("duplicate keys in the owned MCP section");
      expect(fs.readFileSync(outputPath, "utf8")).toBe(source);
    }
  });

  it("preserves a comment-only unknown headers object", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, `{
  "mcp": {
    "sgg-unity-mcp": {
      "type": "remote",
      "url": "http://old.invalid/mcp",
      "headers": {
        // reserved for a user-managed header
      },
    },
  },
}
`);

    expect(runSetupCli([
      "opencode", "--config", configPath, "--output", outputPath,
      "--connection-mode", "windows-stdio", "--write",
    ], { env: {}, stdout: () => {} })).toBe(0);

    const source = fs.readFileSync(outputPath, "utf8");
    expect(source).toContain("// reserved for a user-managed header");
    expect(parse(source).mcp.servers["sgg-unity-mcp"].headers).toEqual({});
  });

  it("migrates a legacy OpenCode timeout independently of the Gateway request-body timeout", () => {
    const { configPath } = makeFixture();
    const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
    config.mcp.request_timeout_ms = 12_345;
    fs.writeFileSync(configPath, JSON.stringify(config));
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, JSON.stringify({
      theme: "dark",
      mcp: {
        "sgg-unity-mcp": {
          type: "remote",
          url: "http://127.0.0.1:27182/mcp",
          enabled: true,
          timeout: 30_000,
        },
      },
    }));

    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write"], { env: {}, stdout: () => {} })).toBe(0);

    let written = JSON.parse(fs.readFileSync(outputPath, "utf8"));
    expect(written.theme).toBe("dark");
    expect(written.mcp.servers["sgg-unity-mcp"].timeout.execution).toBe(RECOMMENDED_MCP_CLIENT_TIMEOUT_MS);
    expect(written.mcp.servers["sgg-unity-mcp"].timeout.execution).not.toBe(config.mcp.request_timeout_ms);

    written.mcp.servers["sgg-unity-mcp"].timeout.execution = 7_200_000;
    fs.writeFileSync(outputPath, JSON.stringify(written));
    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write"], { env: {}, stdout: () => {} })).toBe(0);
    written = JSON.parse(fs.readFileSync(outputPath, "utf8"));
    expect(written.mcp.servers["sgg-unity-mcp"].timeout.execution).toBe(7_200_000);
  });

  it("writes cursor stdio-shim setup only when requested", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), ".cursor", "mcp.json");
    const output: string[] = [];

    const exitCode = runSetupCli(["cursor", "--config", configPath, "--output", outputPath, "--write"], {
      stdout: (text) => output.push(text),
    });

    expect(exitCode).toBe(0);
    const written = JSON.parse(fs.readFileSync(outputPath, "utf8"));
    expect(written.mcpServers["sgg-unity-mcp"]).toEqual({
      command: "sgg-unity-mcp-gateway",
      args: ["stdio-shim", "--config", configPath],
    });
    expect(output.join("")).toContain("mode: write");
  });

  it("updates only the native owned server and preserves timeout components and a deliberate disable", () => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, `{
  // preserve native settings
  "mcp": {
    "timeout": { "execution": 9000000, "catalog": 50000 },
    "servers": {
      "sgg-unity-mcp": {
        "type": "remote", "url": "http://old.invalid/mcp", "disabled": true,
        "codemode": false, "protocol": "auto",
        "timeout": { "startup": 45000, "catalog": 60000, "execution": 7200000 }
      },
      "other": { "type": "remote", "url": "https://example.com/mcp" }
    }
  }
}`);
    const output: string[] = [];
    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write", "--json"], { env: {}, stdout: (text) => output.push(text) })).toBe(0);
    const source = fs.readFileSync(outputPath, "utf8");
    const written = parse(source);
    expect(written.mcp.servers["sgg-unity-mcp"]).toMatchObject({
      type: "remote", disabled: true, codemode: false, protocol: "auto", oauth: false,
      timeout: { startup: 45000, catalog: 60000, execution: 7200000 },
    });
    expect(written.mcp).not.toHaveProperty("sgg-unity-mcp");
    expect(written.mcp.servers.other).toEqual({ type: "remote", url: "https://example.com/mcp" });
    expect(written.mcp.timeout).toEqual({ execution: 9000000, catalog: 50000 });
    expect(source).toContain("// preserve native settings");
    expect(JSON.parse(output.join("")).changes).toEqual(["update: mcp.servers.sgg-unity-mcp"]);
  });

  it("preserves inherited execution and custom env references without extending startup/catalog", () => {
    const { configPath } = makeFixture();
    const gateway = JSON.parse(fs.readFileSync(configPath, "utf8"));
    gateway.mcp.token_env = "CUSTOM_MCP_TOKEN";
    fs.writeFileSync(configPath, JSON.stringify(gateway));
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, '{ "mcp": { "timeout": { "execution": 7200000 } } }');
    const output: string[] = [];
    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write", "--json"], { env: { CUSTOM_MCP_TOKEN: "secret" }, stdout: (text) => output.push(text) })).toBe(0);
    const server = parse(fs.readFileSync(outputPath, "utf8")).mcp.servers["sgg-unity-mcp"];
    expect(server.timeout).toEqual({ startup: 30000, catalog: 30000, execution: 7200000 });
    expect(server.headers).toEqual({ "X-SGG-Unity-MCP-Token": "{env:CUSTOM_MCP_TOKEN}" });
    expect(server.oauth).toBe(false);
    expect(output.join("")).not.toContain('"secret"');
  });

  it.each([
    { defaults: {}, server: {}, expected: { catalog: 7200000, execution: 7200000 } },
    { defaults: { catalog: 45000 }, server: {}, expected: { catalog: 45000, execution: 7200000 } },
    { defaults: { execution: 9000000 }, server: { timeout: { catalog: 60000 } }, expected: { catalog: 60000, execution: 9000000 } },
    { defaults: { execution: 9000000 }, server: { timeout: { execution: 7800000 } }, expected: { catalog: 7200000, execution: 7800000 } },
    { defaults: {}, server: { timeout: { execution: 30000 } }, expected: { catalog: 7200000, execution: RECOMMENDED_MCP_CLIENT_TIMEOUT_MS } },
  ])("preserves supported legacy defaults with native/member/server precedence: %j", ({ defaults, server, expected }) => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, JSON.stringify({ experimental: { mcp_timeout: 7200000 }, mcp: { timeout: defaults, servers: { "sgg-unity-mcp": { type: "remote", url: "http://old.invalid/mcp", ...server } } } }));
    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write"], { env: {}, stdout: () => {} })).toBe(0);
    expect(parse(fs.readFileSync(outputPath, "utf8")).mcp.servers["sgg-unity-mcp"].timeout).toEqual({ startup: 30000, ...expected });
  });

  it.each([
    { servers: { "sgg-unity-mcp": { type: "remote", url: "http://disabled.invalid/mcp", disabled: true } } },
    { "sgg-unity-mcp": { type: "remote", url: "http://disabled.invalid/mcp", enabled: false } },
  ])("does not silently shadow a disabled root server with a new hidden entry: %j", (mcp) => {
    const { configPath } = makeFixture();
    const cwd = path.dirname(configPath);
    const root = path.join(cwd, "opencode.json");
    const hidden = path.join(cwd, ".opencode", "opencode.jsonc");
    fs.mkdirSync(path.dirname(hidden));
    fs.writeFileSync(root, JSON.stringify({ mcp }));
    fs.writeFileSync(hidden, '{ "model": "keep" }');
    const rootBefore = fs.readFileSync(root, "utf8");
    for (const output of [[], ["--output", hidden]]) {
      expect(runSetupCli(["opencode", "--config", configPath, "--write", ...output], { cwd, env: {}, stdout: () => {}, stderr: () => {} })).toBe(1);
      expect(fs.readFileSync(root, "utf8")).toBe(rootBefore);
      expect(fs.readFileSync(hidden, "utf8")).toBe('{ "model": "keep" }');
    }
  });

  it.each([
    '{ "mcp": { "servers": {}, "servers": {} } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": {}, "sgg-unity-mcp": {} } } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": { "type": "local", "type": "remote" } } } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": { "timeout": { "execution": 100, "execution": 200 } } } } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": {} }, "sgg-unity-mcp": {} } }',
    '{ "mcp": { "servers": [] } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": { "disabled": "false" } } } }',
    '{ "mcp": { "servers": { "sgg-unity-mcp": { "disabled": false, "enabled": false } } } }',
    '{ "mcp": { "timeout": { "execution": 0 } } }',
    '{ "experimental": { "mcp_timeout": 0 } }',
    '{ "experimental": { "mcp_timeout": 7200000, "mcp_timeout": 30000 } }',
  ])("refuses ambiguous or invalid native setup without mutation: %s", (source) => {
    const { configPath } = makeFixture();
    const outputPath = path.join(path.dirname(configPath), "opencode.jsonc");
    fs.writeFileSync(outputPath, source);
    expect(runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--write"], { env: {}, stdout: () => {}, stderr: () => {} })).toBe(1);
    expect(fs.readFileSync(outputPath, "utf8")).toBe(source);
  });

  it("selects existing json and the higher-precedence hidden layer without creating duplicate configs", () => {
    const { configPath } = makeFixture();
    const cwd = path.dirname(configPath);
    const rootJson = path.join(cwd, "opencode.json");
    fs.writeFileSync(rootJson, '{ "model": "keep" }');
    const output: string[] = [];
    expect(runSetupCli(["opencode", "--config", configPath, "--write", "--json"], { cwd, env: {}, stdout: (text) => output.push(text) })).toBe(0);
    expect(JSON.parse(output.join("")).path).toBe(rootJson);
    expect(fs.existsSync(path.join(cwd, "opencode.jsonc"))).toBe(false);
    const rootAfter = fs.readFileSync(rootJson, "utf8");
    const hidden = path.join(cwd, ".opencode", "opencode.jsonc");
    fs.mkdirSync(path.dirname(hidden));
    fs.writeFileSync(hidden, rootAfter);
    output.length = 0;
    expect(runSetupCli(["opencode", "--config", configPath, "--write", "--json"], { cwd, env: {}, stdout: (text) => output.push(text) })).toBe(0);
    expect(JSON.parse(output.join("")).path).toBe(hidden);
    expect(fs.readFileSync(rootJson, "utf8")).toBe(rootAfter);
    expect(runSetupCli(["opencode", "--config", configPath, "--output", rootJson, "--write"], { cwd, env: {}, stdout: () => {}, stderr: () => {} })).toBe(1);
    expect(fs.readFileSync(rootJson, "utf8")).toBe(rootAfter);
  });

  it("refuses dual default filenames instead of choosing a fresh config", () => {
    const { configPath } = makeFixture();
    const cwd = path.dirname(configPath);
    fs.writeFileSync(path.join(cwd, "opencode.json"), '{}');
    fs.writeFileSync(path.join(cwd, "opencode.jsonc"), '{}');
    expect(runSetupCli(["opencode", "--config", configPath, "--write"], { cwd, env: {}, stdout: () => {}, stderr: () => {} })).toBe(1);
    expect(fs.readFileSync(path.join(cwd, "opencode.json"), "utf8")).toBe('{}');
    expect(fs.readFileSync(path.join(cwd, "opencode.jsonc"), "utf8")).toBe('{}');
  });

  it("dry-runs assistant setup as Unity UserSettings stdio config", () => {
    const { configPath } = makeFixture();
    const cwd = path.dirname(configPath);
    const output: string[] = [];

    const exitCode = runSetupCli(["assistant", "--config", configPath, "--json"], {
      cwd,
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
    });

    const plan = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(plan.path).toBe(path.join(cwd, "UserSettings", "mcp.json"));
    expect(plan.patch).toEqual({
      enabled: true,
      path: "",
      mcpServers: {
        "sgg-unity-mcp": {
          type: "stdio",
          command: "sgg-unity-mcp-gateway",
          args: ["stdio-shim", "--config", configPath],
        },
      },
    });
    expect(output.join("")).toContain("token value is not written");
    expect(output.join("")).not.toContain("secret-token");
  });

  it("waits for a ready heartbeat and supports project aliases", async () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat({ project_root: "/mnt/c/u/game" }));
    const output: string[] = [];

    const exitCode = await runWaitReadyCli(["--config", configPath, "--project", "C:\\u\\game", "--timeout-ms", "10", "--interval-ms", "1"], {
      stdout: (text) => output.push(text),
      now: () => now,
    });

    expect(exitCode).toBe(0);
    expect(output.join("")).toContain("ready: project game instance game-a");
  });

  it("times out when the heartbeat is live but not ready", async () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat({ status: "compiling" }));
    const output: string[] = [];

    const exitCode = await runWaitReadyCli(["--config", configPath, "--project", "game", "--timeout-ms", "1", "--interval-ms", "1", "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      sleep: async () => {},
    });

    expect(exitCode).toBe(1);
    expect(JSON.parse(output.join(""))).toMatchObject({
      status: "timeout",
      project: "game",
      cause: "project heartbeat is live but status is compiling",
    });
  });

  it("writes a problem report under the configured audit directory", () => {
    const { configPath, policyPath, instancesDir, auditDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    const output: string[] = [];

    const exitCode = runReportProblemCli(["--config", configPath, "--policy", policyPath, "--project", "game", "--summary", "MCP client cannot connect", "--include-doctor", "--json"], {
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    expect(exitCode).toBe(0);
    expect(result.report_path).toContain(path.join(auditDir, "problem-reports"));
    expect(fs.existsSync(result.report_path)).toBe(true);
    const report = JSON.parse(fs.readFileSync(result.report_path, "utf8"));
    expect(report).toMatchObject({
      schema_version: 2,
      source: "cli.report-problem",
      gateway: {
        gateway_runtime_version: expect.stringMatching(/^\d{4}\.\d+\.\d+-\d+$/),
        run_id: expect.stringMatching(/^[0-9a-f-]{36}$/),
      },
      project: "game",
      summary: "MCP client cannot connect",
      registry: { stats: { liveProjectsCount: 1 }, project_resolution: { status: "ok" } },
      doctor: { authentication: { mcp: { configured: true, present: true } } },
      unity_crash_reporter: { unity_crashed: false, active_reporters_count: 0 },
      target_client_exposure: { target_client_failure: { code: "target_client_tools_missing_after_gateway_tools_list_ok" } },
      policy: { inspection: "current_file", current_file_status: "valid", state: "active", degraded: false, effective_policy_revision: expect.stringMatching(/^sha256:/) },
      redaction_applied: true,
      path_redaction_applied: true,
      report_bounds: { max_bytes: 262144, truncated: false },
    });
    const serialized = JSON.stringify(report);
    expect(report.config.path).toBe("[REDACTED_PATH]");
    expect(report.registry.projects_summary).toMatchObject({ total_records: 1, retained_records: 1, max_records: 16, truncated: false });
    for (const privatePath of [configPath, policyPath, instancesDir, auditDir]) {
      expect(serialized).not.toContain(privatePath);
    }
  });

  it("classifies OpenCode API-visible missing MCP tools in problem reports", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    const output: string[] = [];

    const exitCode = runReportProblemCli(["--config", configPath, "--project", "game", "--summary", "OpenCode API did not expose sgg_unity_* MCP tools", "--include-doctor", "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    const report = JSON.parse(fs.readFileSync(result.report_path, "utf8"));
    expect(exitCode).toBe(0);
    expect(report.warnings).toEqual(expect.arrayContaining([expect.stringContaining("target_client_tools_missing_after_gateway_tools_list_ok")]));
    expect(report.target_client_exposure.gateway_probe.expected_tools).toContain("sgg_unity_projects");
    expect(report.doctor.target_client_exposure.target_client_failure.classification).toBe("target_client_config_or_api_exposure");
  });

  it("retains only selector-relevant registry records from one report snapshot", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    for (let index = 0; index < 32; index += 1) {
      writeHeartbeat(instancesDir, heartbeat({
        instance_id: `other-${index}`,
        project: `other-project-${index}`,
        project_root: `C:/private/other-${index}`,
        canonical_project_root: `c:/private/other-${index}`,
        bridge_port: 19000 + index,
      }));
    }
    const output: string[] = [];

    const exitCode = runReportProblemCli(["--config", configPath, "--project", "game", "--summary", "Bound registry evidence", "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    const report = JSON.parse(fs.readFileSync(result.report_path, "utf8"));
    expect(exitCode).toBe(0);
    expect(report.registry.stats.projectRecordsCount).toBe(33);
    expect(report.registry.projects).toHaveLength(1);
    expect(report.registry.projects[0]).toMatchObject({ instance_id: "game-a", project: "game" });
    expect(report.registry.projects_summary).toMatchObject({ total_records: 33, candidate_records: 1, retained_records: 1, truncated: false, selection: "resolved_record" });
    expect(JSON.stringify(report)).not.toContain("C:/private/other-");
  });

  it("caps ambiguous matches and omits unrelated records for missing selectors", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat({ project: "unrelated", instance_id: "unrelated-a" }));
    for (let index = 0; index < 24; index += 1) {
      writeHeartbeat(instancesDir, heartbeat({
        instance_id: `duplicate-${index}`,
        project: "duplicate",
        project_root: `C:/private/duplicate-${index}`,
        canonical_project_root: `c:/private/duplicate-${index}`,
        bridge_port: 19500 + index,
      }));
    }

    const missingOutput: string[] = [];
    expect(runReportProblemCli(["--config", configPath, "--project", "missing", "--summary", "Missing selector", "--json"], {
      stdout: (text) => missingOutput.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    })).toBe(0);
    const missingResult = JSON.parse(missingOutput.join(""));
    const missingReport = JSON.parse(fs.readFileSync(missingResult.report_path, "utf8"));
    expect(missingReport.registry.projects).toEqual([]);
    expect(missingReport.registry.projects_summary).toMatchObject({ candidate_records: 0, retained_records: 0, selection: "no_selector_match" });
    expect(JSON.stringify(missingReport.registry)).not.toContain("unrelated-a");

    const duplicateOutput: string[] = [];
    expect(runReportProblemCli(["--config", configPath, "--project", "duplicate", "--summary", "Duplicate selector", "--json"], {
      stdout: (text) => duplicateOutput.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    })).toBe(0);
    const duplicateResult = JSON.parse(duplicateOutput.join(""));
    const duplicateReport = JSON.parse(fs.readFileSync(duplicateResult.report_path, "utf8"));
    expect(duplicateReport.registry.projects).toHaveLength(16);
    expect(duplicateReport.registry.project_resolution.matches).toHaveLength(16);
    expect(duplicateReport.registry.project_resolution.matches_summary).toMatchObject({ total_records: 24, retained_records: 16, max_records: 16, truncated: true });
  });

  it("records WSL loopback namespace guidance in problem reports", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat());
    const output: string[] = [];

    const exitCode = runReportProblemCli(["--config", configPath, "--project", "game", "--summary", "WSL curl connection_refused", "--include-doctor", "--json"], {
      env: { WSL_DISTRO_NAME: "Ubuntu", SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    const report = JSON.parse(fs.readFileSync(result.report_path, "utf8"));
    expect(exitCode).toBe(0);
    expect(report.wsl_loopback_reachability).toMatchObject({
      code: "windows_loopback_not_wsl_reachable",
      recommended_connection_mode: "windows_stdio_shim",
    });
    expect(report.endpoint_recommendation).toEqual(report.wsl_loopback_reachability);
    expect(report.doctor.wsl_loopback_reachability).toMatchObject({
      windows_loopback_url: "http://127.0.0.1:27182/mcp",
    });
    expect(report.warnings).toEqual(expect.arrayContaining([expect.stringContaining("Windows loopback is not a WSL-usable Gateway endpoint")]));
    expect(JSON.stringify(report)).not.toContain("secret-token");
  });

  it("redacts secret-looking report text and heartbeat tokens", () => {
    const { configPath, instancesDir } = makeFixture();
    writeHeartbeat(instancesDir, heartbeat({ token: "bridge-secret" }));
    const output: string[] = [];

    const exitCode = runReportProblemCli(["--config", configPath, "--project", "game", "--summary", "authorization: bearer super-secret token=abc123", "--json"], {
      env: { SGG_UNITY_MCP_TOKEN: "secret-token" },
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    const text = fs.readFileSync(result.report_path, "utf8");
    expect(exitCode).toBe(0);
    expect(text).toContain("authorization: [REDACTED]");
    expect(text).toContain("token=[REDACTED]");
    expect(text).not.toContain("super-secret");
    expect(text).not.toContain("abc123");
    expect(text).not.toContain("bridge-secret");
    expect(text).not.toContain("secret-token");
  });

  it("writes a temp fallback report when config cannot be loaded", () => {
    const output: string[] = [];
    const missingConfigPath = path.join(os.tmpdir(), "missing-gateway-config.json");

    const exitCode = runReportProblemCli(["--config", missingConfigPath, "--project", "game", "--summary", "Gateway unavailable", "--include-doctor", "--json"], {
      stdout: (text) => output.push(text),
      now: () => now,
      unityCrashReporter: idleUnityCrashReporter(),
    });

    const result = JSON.parse(output.join(""));
    const report = JSON.parse(fs.readFileSync(result.report_path, "utf8"));
    expect(exitCode).toBe(0);
    expect(result.report_path).toContain(path.join(os.tmpdir(), "sgg-unity-mcp", "problem-reports"));
    expect(report.config).toMatchObject({ path: "[REDACTED_PATH]", ok: false });
    expect(report.registry).toMatchObject({ stats: null, projects: [], project_resolution: null });
    expect(report.warnings).toContain("config_load_failed; wrote report to temp fallback");
    expect(JSON.stringify(report)).not.toContain(missingConfigPath);
    fs.rmSync(result.report_path, { force: true });
  });
});

function makeFixture(): { configPath: string; policyPath: string; instancesDir: string; auditDir: string } {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-cli-support-"));
  tempDirs.push(dir);
  const instancesDir = path.join(dir, "instances");
  const auditDir = path.join(dir, "audit");
  fs.mkdirSync(instancesDir, { mode: 0o700 });
  const configPath = path.join(dir, "gateway.json");
  const policyPath = path.join(dir, "policy.json");
  fs.writeFileSync(configPath, JSON.stringify({
    mcp: {
      host: "127.0.0.1",
      port: 27182,
      token_env: "SGG_UNITY_MCP_TOKEN",
      allowed_origins: [],
      allow_non_loopback: false,
      max_body_bytes: 1048576,
      request_timeout_ms: 30000,
      rate_limit: { window_ms: 60000, max_requests: 120 },
    },
    registry: { instances_dir: instancesDir, stale_after_ms: 90000 },
    logging: { level: "info", audit_enabled: true, audit_dir: auditDir },
    features: { sgg_mcp_plans: false },
  }));
  fs.writeFileSync(policyPath, JSON.stringify({
    schema_version: 1,
    defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
    projects: { game: { allow: ["plan.read"], ask: [], deny: [] } },
  }));
  return { configPath, policyPath, instancesDir, auditDir };
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record, null, 2));
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [
      { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
      { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
    ],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "9f12a3c0",
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    ...overrides,
  };
}

function idleUnityCrashReporter(): UnityCrashReporterRuntime {
  const status = {
    schema_version: 1 as const,
    supported: true,
    unity_crashed: false,
    blocking_agent_work: false,
    active_reporters_count: 0,
    active_reporters: [],
    recent_crash_artifacts: { max_age_ms: 86400000, crash_directories: [], bug_report_json: [], bug_report_temp_directories: [] },
    temp_roots: [],
    errors: [],
    next_valid_actions: [],
  };
  return {
    status: () => status,
    close: () => ({
      schema_version: 1,
      dry_run: false,
      force: false,
      submit_attempted: false,
      close_method: "taskkill_window_close",
      target_pids: [],
      closed_count: 0,
      skipped: [],
      results: [],
      active_before: status,
    }),
  };
}

function crashedUnityCrashReporter(): UnityCrashReporterRuntime {
  const status = {
    schema_version: 1 as const,
    supported: true,
    unity_crashed: true,
    blocking_agent_work: true,
    active_reporters_count: 1,
    active_reporters: [{ pid: 29136, image_name: "UnityBugReporter.exe", window_title: "Unity Bug Reporter", source: "tasklist" as const }],
    recent_crash_artifacts: { max_age_ms: 86400000, crash_directories: [], bug_report_json: [], bug_report_temp_directories: [] },
    temp_roots: [],
    errors: [],
    next_valid_actions: ["call_diagnostics_unity_crash_reporter_close"],
  };
  return {
    status: () => status,
    close: () => ({
      schema_version: 1,
      dry_run: false,
      force: false,
      submit_attempted: false,
      close_method: "taskkill_window_close",
      target_pids: [29136],
      closed_count: 1,
      skipped: [],
      results: [{ pid: 29136, status: "closed" }],
      active_before: status,
    }),
  };
}
