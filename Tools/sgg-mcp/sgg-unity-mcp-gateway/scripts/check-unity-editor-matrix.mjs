#!/usr/bin/env node

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { joinLocalFsPath, loadLocalUnityEditorEnv, runUnityEditorValidation, toLocalFsPath } from "./check-unity-editor.mjs";
import { discoverUnityEditors, findUnityEditor } from "./unity-editor-discovery.mjs";
import { RELEASE_UNITY_EDITOR_VERSIONS } from "./release-matrix.mjs";

export const defaultUnityEditorMatrixVersions = RELEASE_UNITY_EDITOR_VERSIONS;

export function parseUnityEditorMatrixVersions(env = process.env) {
  const raw = env.SGG_UNITY_EDITOR_MATRIX ?? env.SGG_UNITY_EDITOR_VERSIONS;
  if (raw === undefined || raw === "") {
    return { ok: true, versions: defaultUnityEditorMatrixVersions };
  }

  const versions = raw.split(/[,\s;]+/).map((value) => value.trim()).filter(Boolean);
  if (versions.length === 0) {
    return { ok: false, message: "SGG_UNITY_EDITOR_MATRIX must contain at least one Unity version selector" };
  }

  return { ok: true, versions };
}

export async function runUnityEditorMatrix(env = process.env, cwd = process.cwd(), options = {}) {
  const effectiveEnv = loadLocalUnityEditorEnv(env, cwd);
  const projectPath = effectiveEnv.SGG_UNITY_TEST_PROJECT;
  if (!projectPath) {
    console.log("[check-unity-editor-matrix] skipped: set SGG_UNITY_TEST_PROJECT and local Unity editor discovery variables to run matrix validation");
    return 0;
  }

  const versionResult = parseUnityEditorMatrixVersions(effectiveEnv);
  if (!versionResult.ok) {
    console.error(`[check-unity-editor-matrix] ${versionResult.message}`);
    return 1;
  }

  const requireAll = effectiveEnv.SGG_UNITY_EDITOR_MATRIX_REQUIRED === "true";
  const discovery = options.discovery ?? discoverUnityEditors({ env: effectiveEnv });
  const logDir = resolveMatrixLogDir(effectiveEnv, projectPath);
  fs.mkdirSync(logDir, { recursive: true });

  let passed = 0;
  let failed = 0;
  let skipped = 0;
  const runValidation = options.runValidation ?? runUnityEditorValidation;
  for (const version of versionResult.versions) {
    const editor = findUnityEditor(discovery.editors, version);
    if (!editor) {
      const message = `[check-unity-editor-matrix] ${version}: skipped, matching Unity Editor was not discovered`;
      if (requireAll) {
        console.error(message);
        failed++;
      } else {
        console.log(message);
        skipped++;
      }
      continue;
    }

    const logFile = joinLocalFsPath(logDir, `sgg-unity-mcp-editor-check-${safeFileSegment(version)}.log`);
    console.log(`[check-unity-editor-matrix] ${version}: ${editor.executable_path}`);
    const runEnv = {
      ...effectiveEnv,
      SGG_UNITY_EDITOR_ENV_FILE: "off",
      SGG_UNITY_EDITOR_VERSION: version,
      SGG_UNITY_TEST_LOG: logFile,
    };
    delete runEnv.UNITY_EXE;

    const status = await runValidation(runEnv, cwd, options.validationOptions ?? {});
    if (status === 0) {
      passed++;
    } else {
      failed++;
    }
  }

  if (failed > 0) {
    console.error(`[check-unity-editor-matrix] failed: passed=${passed}, failed=${failed}, skipped=${skipped}, log_dir=${logDir}`);
    return 1;
  }

  if (passed === 0) {
    console.log(`[check-unity-editor-matrix] skipped: no requested Unity Editors were discovered, skipped=${skipped}`);
    return 0;
  }

  console.log(`[check-unity-editor-matrix] ok: passed=${passed}, skipped=${skipped}, log_dir=${logDir}`);
  return 0;
}

function resolveMatrixLogDir(env, projectPath) {
  if (env.SGG_UNITY_MATRIX_LOG_DIR) {
    return toLocalFsPath(env.SGG_UNITY_MATRIX_LOG_DIR, { env });
  }

  const localProjectPath = toLocalFsPath(projectPath, { env });
  if (localProjectPath) {
    return joinLocalFsPath(localProjectPath, "Logs", "sgg-unity-mcp-editor-matrix");
  }

  return joinLocalFsPath(os.tmpdir(), "sgg-unity-mcp-editor-matrix");
}

function safeFileSegment(value) {
  return String(value).replace(/[^A-Za-z0-9._-]+/g, "-").slice(0, 80) || "unity";
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(await runUnityEditorMatrix());
}
