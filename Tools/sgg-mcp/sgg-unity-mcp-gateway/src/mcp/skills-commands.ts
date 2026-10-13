import fs from "node:fs";
import { createHash } from "node:crypto";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import type { GatewayError } from "./results.js";

const FEATURE_FLAG = "sgg_mcp_skills";
const MAX_SKILL_READ_CHARS = 32 * 1024;
const DEFAULT_SKILL_READ_CHARS = 16 * 1024;
const MAX_SKILL_SIZE_BYTES = 128 * 1024;
const MAX_LIST_RESULTS = 100;
const DEFAULT_LIST_RESULTS = 50;
const MAX_UNITY_SKILL_DIRS = 1000;
const MAX_SKILL_RESOURCES = 100;
const MAX_IMPORT_FILES = 50;
const MAX_IMPORT_TOTAL_BYTES = 2 * 1024 * 1024;
const IMPORT_SIDECAR_NAME = "sggmcp-import.json";
const SHA256_PATTERN = "^[A-Fa-f0-9]{64}$";

const FORBIDDEN_MARKERS = [
  { id: "auto_enable", pattern: /auto[- ]?enable|automatically enable|enable unknown skills/i },
  { id: "execute_without_review", pattern: /execute .*without review|run .*without review|skip review/i },
  { id: "policy_bypass", pattern: /change policy|bypass policy|override policy/i },
  { id: "pathguard_bypass", pattern: /bypass PathGuard|bypass CanonicalPath|ignore PathGuard|ignore CanonicalPath/i },
  { id: "dynamic_execution", pattern: /shell|execute c#|dynamic c#|process\.start/i },
  { id: "external_mcp", pattern: /external MCP server|connect .*MCP server/i },
];

export const SKILL_COMMANDS: CommandDescriptor[] = [
  {
    id: "skill.list",
    title: "List skills",
    description: "List bounded local, Unity-style, and built-in example skills with validation and review metadata.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        include_examples: { type: "boolean" },
        include_unity_project: { type: "boolean" },
        include_unity_packages: { type: "boolean" },
        include_unity_user: { type: "boolean" },
        max_results: { type: "integer", minimum: 1, maximum: MAX_LIST_RESULTS },
      },
      additionalProperties: false,
    },
    tags: ["skill"],
    capability: "skill",
    result_shape: "object_details",
    output_limit: { max_results: MAX_LIST_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "skill.read",
    title: "Read skill",
    description: "Read bounded SKILL.md or resource content by safe skill name from a fixed skill root.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        source: { type: "string", enum: ["local", "example", "unity_project", "unity_package", "unity_user"] },
        resource_path: { type: "string", minLength: 1, maxLength: 512 },
        max_chars: { type: "integer", minimum: 1, maximum: MAX_SKILL_READ_CHARS },
      },
      required: ["name"],
      additionalProperties: false,
    },
    tags: ["skill"],
    capability: "skill",
    result_shape: "text_artifact",
    output_limit: { max_chars: MAX_SKILL_READ_CHARS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "skill.validate",
    title: "Validate skill",
    description: "Validate a SKILL.md without executing instructions or modifying files.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        source: { type: "string", enum: ["local", "example", "unity_project", "unity_package", "unity_user"] },
      },
      required: ["name"],
      additionalProperties: false,
    },
    tags: ["skill"],
    capability: "skill",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "skill.import",
    title: "Import skill",
    description: "Copy a discovered skill into .mcp/skills/<target_name>/ for manual review without granting permissions.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        source: { type: "string", enum: ["local", "example", "unity_project", "unity_package", "unity_user"] },
        skill_name: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        source_hash: { type: "string", minLength: 64, maxLength: 64, pattern: SHA256_PATTERN },
        target_name: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        include_resources: { type: "boolean" },
        overwrite: { type: "boolean" },
        dry_run: { type: "boolean" },
      },
      required: ["source", "skill_name", "target_name"],
      additionalProperties: false,
    },
    tags: ["skill"],
    capability: "skill",
    result_shape: "object_details",
    output_limit: { max_results: MAX_IMPORT_FILES, max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type SkillCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };

type SkillSource = "local" | "example" | "unity_project" | "unity_package" | "unity_user";
type SkillRoot = { source: SkillSource; fullPath: string; relativeRoot: string; precedence: number };
type SkillEntry = { root: SkillRoot; name: string; skillPath: string; relativePath: string };
type SkillValidation = { status: "valid" | "invalid"; exists: boolean; bytes: number; title: string | null; findings: Array<Record<string, unknown>>; frontmatter: Record<string, unknown> | null };
type SkillEntryValidation = SkillValidation & { resources: Array<Record<string, unknown>> };
type BoundSkill = { status: "ok"; entry: SkillEntry } | Extract<SkillCommandResult, { status: "error" }>;
type ImportFile = { resourcePath: string; sourceFullPath: string; targetFullPath: string; targetRelativePath: string; bytes: number; sha256: string; content: Buffer };
type ImportPlan = {
  source: SkillSource;
  skillName: string;
  sourceHash: string;
  targetName: string;
  targetDir: string;
  targetRelativeRoot: string;
  includeResources: boolean;
  overwrite: boolean;
  dryRun: boolean;
  entry: SkillEntry;
  validation: SkillEntryValidation;
  files: ImportFile[];
  sidecar: Record<string, unknown>;
  existingTarget: boolean;
  existingIdentical: boolean;
};
export type SkillRuntimeOptions = { unityUserSkillsRoot?: string; now?: Date; gatewayVersion?: string };

const SOURCE_PRECEDENCE: Record<SkillSource, number> = {
  local: 10,
  unity_project: 20,
  unity_package: 30,
  unity_user: 40,
  example: 90,
};

export function findSkillCommand(commandId: string): CommandDescriptor | undefined {
  return SKILL_COMMANDS.find((command) => command.id === commandId);
}

export function runSkillCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, options: SkillRuntimeOptions = {}): SkillCommandResult {
  if (!record) {
    return error("project_offline", "Skill commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "skill.list":
      return { status: "ok", data: listSkills(args, record, options) };
    case "skill.read":
      return readSkill(args, record, options);
    case "skill.validate":
      return validateSkillCommand(args, record, options);
    case "skill.import":
      return importSkill(args, record, options);
    default:
      return error("command_not_found", "Skill command not found");
  }
}

function listSkills(args: Record<string, unknown>, record: HeartbeatRecord, options: SkillRuntimeOptions): Record<string, unknown> {
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_LIST_RESULTS, 1, MAX_LIST_RESULTS);
  const includeExamples = args.include_examples !== false;
  const includeUnityProject = args.include_unity_project !== false;
  const includeUnityPackages = args.include_unity_packages !== false;
  const includeUnityUser = args.include_unity_user === true;
  const review = readReviewMetadata(record);
  const roots = [
    skillRoot(record, "local", options),
    ...(includeUnityProject ? [skillRoot(record, "unity_project", options)] : []),
    ...(includeUnityPackages ? [skillRoot(record, "unity_package", options)] : []),
    ...(includeUnityUser ? [skillRoot(record, "unity_user", options)] : []),
    ...(includeExamples ? [skillRoot(record, "example", options)] : []),
  ];
  const skills: unknown[] = [];

  for (const root of roots) {
    const entries = listSkillEntries(root);
    const duplicates = duplicateSkillNames(entries);
    for (const entry of entries) {
      const validation = validateSkillEntry(entry, duplicates.get(entry.name));
      skills.push({
        name: entry.name,
        source: root.source,
        precedence: root.precedence,
        relative_path: entry.relativePath,
        bytes: validation.bytes,
        sha256: hashFileSha256(entry.skillPath),
        validation_status: validation.status,
        findings: validation.findings,
        resources: validation.resources,
        review_status: reviewStatusForEntry(review, entry),
        frontmatter: validation.frontmatter,
      });
      if (skills.length >= maxResults) {
        return { roots: roots.map(publicRoot), skills, max_results: maxResults, truncated: true, review_metadata_path: review.relativePath, review_metadata_exists: review.exists };
      }
    }
  }

  return { roots: roots.map(publicRoot), skills, max_results: maxResults, truncated: false, review_metadata_path: review.relativePath, review_metadata_exists: review.exists };
}

function readSkill(args: Record<string, unknown>, record: HeartbeatRecord, options: SkillRuntimeOptions): SkillCommandResult {
  const bound = bindSkill(record, args, options);
  if (bound.status === "error") {
    return bound;
  }
  const resourcePath = typeof args.resource_path === "string" ? args.resource_path.trim() : "SKILL.md";
  const resource = resolveSkillResource(bound.entry, resourcePath);
  if (resource.status === "error") {
    return resource;
  }
  if (!fs.existsSync(resource.fullPath)) {
    return error("asset_not_found", "Requested skill resource was not found under the selected skills root");
  }

  const maxChars = clampInteger(typeof args.max_chars === "number" ? args.max_chars : DEFAULT_SKILL_READ_CHARS, 1, MAX_SKILL_READ_CHARS);
  const stat = fs.lstatSync(resource.fullPath);
  if (!stat.isFile()) {
    return error("invalid_field_value", "Skill resource must be a regular file", { field: "resource_path" });
  }
  if (stat.isSymbolicLink()) {
    return error("invalid_field_value", "Skill resource must not be a symbolic link", { field: "resource_path" });
  }
  if (resource.resourcePath !== "SKILL.md" && stat.size > MAX_SKILL_SIZE_BYTES) {
    return error("invalid_field_value", `Skill resource exceeds ${MAX_SKILL_SIZE_BYTES} bytes`, { field: "resource_path", bytes: stat.size, max_bytes: MAX_SKILL_SIZE_BYTES });
  }
  const prefix = readUtf8Prefix(resource.fullPath, Math.min(MAX_SKILL_SIZE_BYTES, Math.max(4096, maxChars * 4)));
  const validation = validateSkillEntry(bound.entry);
  const review = readReviewMetadata(record);
  return {
    status: "ok",
    data: {
      name: bound.entry.name,
      source: bound.entry.root.source,
      precedence: bound.entry.root.precedence,
      relative_path: bound.entry.relativePath,
      resource_path: resource.resourcePath,
      resource_relative_path: resource.relativePath,
      bytes: stat.size,
      sha256: hashFileSha256(resource.fullPath),
      content: prefix.text.slice(0, maxChars),
      max_chars: maxChars,
      truncated: stat.size > prefix.bytesRead || prefix.text.length > maxChars,
      validation_status: validation.status,
      findings: validation.findings,
      resources: validation.resources,
      review_status: reviewStatusForEntry(review, bound.entry),
      frontmatter: validation.frontmatter,
      executed: false,
      auto_enabled: false,
    },
  };
}

function validateSkillCommand(args: Record<string, unknown>, record: HeartbeatRecord, options: SkillRuntimeOptions): SkillCommandResult {
  const bound = bindSkill(record, args, options);
  if (bound.status === "error") {
    return bound;
  }
  const validation = validateSkillEntry(bound.entry);
  return {
    status: "ok",
    data: {
      name: bound.entry.name,
      source: bound.entry.root.source,
      precedence: bound.entry.root.precedence,
      relative_path: bound.entry.relativePath,
      sha256: hashFileSha256(bound.entry.skillPath),
      ...validation,
      executed: false,
      modified: false,
    },
  };
}

function importSkill(args: Record<string, unknown>, record: HeartbeatRecord, options: SkillRuntimeOptions): SkillCommandResult {
  const plan = createImportPlan(args, record, options);
  if (plan.status === "error") {
    return plan;
  }

  const data = importPlanData(plan.plan);
  if (plan.plan.dryRun) {
    return { status: "ok", data: { ...data, dry_run: true, would_write: !plan.plan.existingIdentical, modified: false } };
  }

  if (plan.plan.existingTarget && !plan.plan.overwrite && !plan.plan.existingIdentical) {
    return error("invalid_field_value", "Target skill already exists; pass overwrite=true after review to replace copied files", {
      field: "overwrite",
      target_path: plan.plan.targetRelativeRoot,
      existing_target: true,
    });
  }

  if (plan.plan.existingIdentical && !plan.plan.overwrite) {
    return { status: "ok", data: { ...data, dry_run: false, created: false, modified: false, existing_identical: true, sidecar_written: false } };
  }

  const written = writeImportPlan(plan.plan);
  if (written.status === "error") {
    return written;
  }

  return {
    status: "ok",
    data: {
      ...data,
      dry_run: false,
      created: !plan.plan.existingTarget,
      modified: true,
      existing_identical: false,
      copied_files_count: plan.plan.files.length,
      sidecar_written: true,
      review_status: "review_required",
    },
  };
}

function createImportPlan(args: Record<string, unknown>, record: HeartbeatRecord, options: SkillRuntimeOptions): { status: "ok"; plan: ImportPlan } | Extract<SkillCommandResult, { status: "error" }> {
  const skillName = typeof args.skill_name === "string" ? args.skill_name.trim() : "";
  const targetName = typeof args.target_name === "string" ? args.target_name.trim() : "";
  if (!isSafeSkillName(skillName)) {
    return error("invalid_field_value", "skill_name must be a safe single-folder skill name", { field: "skill_name" });
  }
  if (!isSafeSkillName(targetName)) {
    return error("invalid_field_value", "target_name must be a safe single-folder skill name", { field: "target_name" });
  }

  const source = args.source === "example"
    ? "example"
    : args.source === "unity_project"
      ? "unity_project"
      : args.source === "unity_package"
        ? "unity_package"
        : args.source === "unity_user"
          ? "unity_user"
          : "local";
  const bound = bindSkill(record, { name: skillName, source }, options);
  if (bound.status === "error") {
    return bound;
  }
  if (!fs.existsSync(bound.entry.skillPath)) {
    return error("asset_not_found", "Source skill was not found in the selected fixed source", { source, skill_name: skillName });
  }

  const sourceHash = hashFileSha256(bound.entry.skillPath);
  if (!sourceHash) {
    return error("invalid_field_value", "Source SKILL.md must be a regular bounded file", { field: "source_hash", source, skill_name: skillName });
  }
  const providedHash = typeof args.source_hash === "string" ? args.source_hash.trim() : "";
  if (source !== "example" && !providedHash) {
    return error("invalid_field_value", "source_hash is required for non-example imports", { field: "source_hash", source, expected_hash_source: "skill.list sha256" });
  }
  if (providedHash && providedHash.toLowerCase() !== sourceHash) {
    return error("invalid_field_value", "source_hash does not match the current source SKILL.md", { field: "source_hash", source, skill_name: skillName, expected_sha256: sourceHash });
  }

  const validation = validateSkillEntry(bound.entry);
  const includeResources = args.include_resources !== false;
  const files = collectImportFiles(bound.entry, record, targetName, includeResources);
  if (files.status === "error") {
    return files;
  }

  const targetDir = path.join(record.project_root, ".mcp", "skills", targetName);
  const targetRoot = path.join(record.project_root, ".mcp", "skills");
  if (!isUnderRoot(targetDir, targetRoot)) {
    return error("path_traversal_rejected", "Target skill path must stay under .mcp/skills");
  }
  const existingTarget = fs.existsSync(targetDir);
  if (existingTarget) {
    const targetStat = fs.lstatSync(targetDir);
    if (!targetStat.isDirectory() || targetStat.isSymbolicLink()) {
      return error("invalid_field_value", "Target skill path must be a regular directory when it already exists", { target_path: `.mcp/skills/${targetName}` });
    }
  }

  const now = options.now ?? new Date();
  const sidecar = {
    schema_version: 1,
    review_status: "review_required",
    imported_at: now.toISOString(),
    source,
    source_relative_path: bound.entry.relativePath,
    source_hash: sourceHash,
    gateway_version: options.gatewayVersion ?? "unknown",
    notes: "Import copies prompt/resources only. It does not grant permissions.",
  };

  return {
    status: "ok",
    plan: {
      source,
      skillName,
      sourceHash,
      targetName,
      targetDir,
      targetRelativeRoot: `.mcp/skills/${targetName}`,
      includeResources,
      overwrite: args.overwrite === true,
      dryRun: args.dry_run === true,
      entry: bound.entry,
      validation,
      files: files.files,
      sidecar,
      existingTarget,
      existingIdentical: existingTarget && importFilesAlreadyMatch(files.files),
    },
  };
}

function collectImportFiles(entry: SkillEntry, record: HeartbeatRecord, targetName: string, includeResources: boolean): { status: "ok"; files: ImportFile[] } | Extract<SkillCommandResult, { status: "error" }> {
  const skillDir = path.dirname(entry.skillPath);
  const skillDirStat = fs.lstatSync(skillDir);
  if (!skillDirStat.isDirectory() || skillDirStat.isSymbolicLink()) {
    return error("invalid_field_value", "Source skill folder must be a regular directory", { source_relative_path: entry.relativePath });
  }
  const files: ImportFile[] = [];
  const addFile = (resourcePath: string, sourceFullPath: string): Extract<SkillCommandResult, { status: "error" }> | undefined => {
    const safeContent = readSafeImportFile(sourceFullPath, resourcePath);
    if (safeContent.status === "error") {
      return safeContent;
    }
    const targetFullPath = path.join(record.project_root, ".mcp", "skills", targetName, ...resourcePath.split("/"));
    if (!isUnderRoot(targetFullPath, path.join(record.project_root, ".mcp", "skills", targetName))) {
      return error("path_traversal_rejected", "Import target file must stay under the target skill folder", { resource_path: resourcePath });
    }
    files.push({
      resourcePath,
      sourceFullPath,
      targetFullPath,
      targetRelativePath: `.mcp/skills/${targetName}/${resourcePath}`,
      bytes: safeContent.content.length,
      sha256: createHash("sha256").update(safeContent.content).digest("hex"),
      content: safeContent.content,
    });
    return undefined;
  };

  const skillError = addFile("SKILL.md", entry.skillPath);
  if (skillError) {
    return skillError;
  }

  if (includeResources) {
    const validation = validateSkillEntry(entry);
    const unsafeFinding = validation.findings.find((finding) => typeof finding.code === "string" && ["resource_symlink", "resource_not_file", "resource_size_limit", "resource_count_limit"].includes(finding.code));
    if (unsafeFinding) {
      return error("invalid_field_value", "Source skill resources are not safe to import", { finding: unsafeFinding });
    }
    for (const resource of validation.resources) {
      if (typeof resource.relative_path !== "string" || !isSafeResourcePath(resource.relative_path)) {
        return error("path_traversal_rejected", "Source skill resource path is not safe to import", { resource_path: resource.relative_path });
      }
      const resolved = resolveSkillResource(entry, resource.relative_path);
      if (resolved.status === "error") {
        return resolved;
      }
      const resourceError = addFile(resource.relative_path, resolved.fullPath);
      if (resourceError) {
        return resourceError;
      }
    }
  }

  if (files.length > MAX_IMPORT_FILES) {
    return error("invalid_field_value", `Skill import exceeds ${MAX_IMPORT_FILES} files`, { max_files: MAX_IMPORT_FILES, actual_files: files.length });
  }
  const totalBytes = files.reduce((sum, file) => sum + file.bytes, 0);
  if (totalBytes > MAX_IMPORT_TOTAL_BYTES) {
    return error("invalid_field_value", `Skill import exceeds ${MAX_IMPORT_TOTAL_BYTES} total bytes`, { max_total_bytes: MAX_IMPORT_TOTAL_BYTES, actual_total_bytes: totalBytes });
  }

  return { status: "ok", files };
}

function readSafeImportFile(filePath: string, resourcePath: string): { status: "ok"; content: Buffer } | Extract<SkillCommandResult, { status: "error" }> {
  const stat = fs.lstatSync(filePath);
  if (!stat.isFile() || stat.isSymbolicLink()) {
    return error("invalid_field_value", "Import source entries must be regular files", { resource_path: resourcePath });
  }
  if (stat.size > MAX_SKILL_SIZE_BYTES) {
    return error("invalid_field_value", `Import source file exceeds ${MAX_SKILL_SIZE_BYTES} bytes`, { resource_path: resourcePath, bytes: stat.size, max_bytes: MAX_SKILL_SIZE_BYTES });
  }
  const content = fs.readFileSync(filePath);
  if (content.includes(0) || content.toString("utf8").includes("\uFFFD")) {
    return error("invalid_field_value", "Import source files must be bounded UTF-8 text, not binary data", { resource_path: resourcePath });
  }
  return { status: "ok", content };
}

function importFilesAlreadyMatch(files: ImportFile[]): boolean {
  return files.every((file) => {
    if (!fs.existsSync(file.targetFullPath)) {
      return false;
    }
    const stat = fs.lstatSync(file.targetFullPath);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      return false;
    }
    return createHash("sha256").update(fs.readFileSync(file.targetFullPath)).digest("hex") === file.sha256;
  });
}

function importPlanData(plan: ImportPlan): Record<string, unknown> {
  return {
    source: plan.source,
    skill_name: plan.skillName,
    source_relative_path: plan.entry.relativePath,
    source_hash: plan.sourceHash,
    target_name: plan.targetName,
    target_path: plan.targetRelativeRoot,
    include_resources: plan.includeResources,
    overwrite: plan.overwrite,
    existing_target: plan.existingTarget,
    existing_identical: plan.existingIdentical,
    validation_status: plan.validation.status,
    findings: plan.validation.findings,
    files: plan.files.map((file) => ({ resource_path: file.resourcePath, target_path: file.targetRelativePath, bytes: file.bytes, sha256: file.sha256 })),
    total_bytes: plan.files.reduce((sum, file) => sum + file.bytes, 0),
    sidecar_path: `${plan.targetRelativeRoot}/${IMPORT_SIDECAR_NAME}`,
    sidecar: plan.sidecar,
    review_status: "review_required",
    executed: false,
    auto_enabled: false,
    policy_changed: false,
  };
}

function writeImportPlan(plan: ImportPlan): SkillCommandResult {
  const mcpRoot = path.dirname(path.dirname(plan.targetDir));
  const skillsRoot = path.dirname(plan.targetDir);
  for (const [directory, relativePath] of [[mcpRoot, ".mcp"], [skillsRoot, ".mcp/skills"], [plan.targetDir, plan.targetRelativeRoot]] as const) {
    const ensured = ensureImportDirectory(directory, relativePath);
    if (ensured.status === "error") {
      return ensured;
    }
  }

  for (const file of plan.files) {
    const parent = path.dirname(file.targetFullPath);
    if (fs.existsSync(parent)) {
      const parentStat = fs.lstatSync(parent);
      if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) {
        return error("invalid_field_value", "Import target parent must be a regular directory", { target_path: file.targetRelativePath });
      }
    }
    fs.mkdirSync(parent, { recursive: true });
    if (fs.existsSync(file.targetFullPath)) {
      const stat = fs.lstatSync(file.targetFullPath);
      if (!stat.isFile() || stat.isSymbolicLink()) {
        return error("invalid_field_value", "Import target file must be a regular file", { target_path: file.targetRelativePath });
      }
    }
    fs.writeFileSync(file.targetFullPath, file.content);
  }

  const sidecarPath = path.join(plan.targetDir, IMPORT_SIDECAR_NAME);
  if (fs.existsSync(sidecarPath)) {
    const stat = fs.lstatSync(sidecarPath);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      return error("invalid_field_value", "Import sidecar target must be a regular file", { target_path: `${plan.targetRelativeRoot}/${IMPORT_SIDECAR_NAME}` });
    }
  }
  fs.writeFileSync(sidecarPath, `${JSON.stringify(plan.sidecar, null, 2)}\n`, "utf8");
  return { status: "ok", data: {} };
}

function ensureImportDirectory(directory: string, relativePath: string): SkillCommandResult {
  if (!fs.existsSync(directory)) {
    fs.mkdirSync(directory);
  }
  const stat = fs.lstatSync(directory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) {
    return error("invalid_field_value", "Import directories must be regular directories, not symbolic links", { target_path: relativePath });
  }
  return { status: "ok", data: {} };
}

function bindSkill(record: HeartbeatRecord, args: Record<string, unknown>, options: SkillRuntimeOptions): BoundSkill {
  const name = typeof args.name === "string" ? args.name.trim() : "";
  if (!isSafeSkillName(name)) {
    return error(path.isAbsolute(name) || /^[A-Za-z]:[\\/]/.test(name) ? "absolute_path_rejected" : "path_traversal_rejected", "Skill name must be a single safe relative folder name");
  }
  const source = args.source === "example"
    ? "example"
    : args.source === "unity_project"
      ? "unity_project"
      : args.source === "unity_package"
        ? "unity_package"
        : args.source === "unity_user"
          ? "unity_user"
          : "local";
  const root = skillRoot(record, source, options);
  if (!root.fullPath) {
    return error("asset_not_found", "Selected skill source is not available on this host", { source });
  }
  const matches = listSkillEntries(root).filter((entry) => entry.name === name);
  if (matches.length > 1) {
    return error("invalid_field_value", "Multiple skills with the same name exist in the selected source", { field: "name", source, relative_paths: matches.map((entry) => entry.relativePath) });
  }
  if (matches.length === 1) {
    return { status: "ok", entry: matches[0]! };
  }
  const fallbackPath = path.join(root.fullPath, name, "SKILL.md");
  if (!isUnderRoot(fallbackPath, root.fullPath)) {
    return error("path_traversal_rejected", "Skill path must stay under the selected skills root");
  }
  return { status: "ok", entry: { root, name, skillPath: fallbackPath, relativePath: `${root.relativeRoot}/${name}/SKILL.md` } };
}

function skillRoot(record: HeartbeatRecord, source: SkillSource, options: SkillRuntimeOptions): SkillRoot {
  if (source === "local") {
    return { source, fullPath: path.join(record.project_root, ".mcp", "skills"), relativeRoot: ".mcp/skills", precedence: SOURCE_PRECEDENCE[source] };
  }
  if (source === "unity_project") {
    return { source, fullPath: path.join(record.project_root, "Assets"), relativeRoot: "Assets", precedence: SOURCE_PRECEDENCE[source] };
  }
  if (source === "unity_package") {
    return { source, fullPath: path.join(record.project_root, "Packages"), relativeRoot: "Packages", precedence: SOURCE_PRECEDENCE[source] };
  }
  if (source === "unity_user") {
    return { source, fullPath: options.unityUserSkillsRoot ?? unityUserSkillsRoot(), relativeRoot: options.unityUserSkillsRoot ? "configured:Unity/AIAssistantSkills" : "%APPDATA%/Unity/AIAssistantSkills", precedence: SOURCE_PRECEDENCE[source] };
  }
  return { source, fullPath: path.join(process.cwd(), "examples", "skills"), relativeRoot: "examples/skills", precedence: SOURCE_PRECEDENCE[source] };
}

function listSkillEntries(root: SkillRoot): SkillEntry[] {
  if (!root.fullPath || !fs.existsSync(root.fullPath)) {
    return [];
  }
  if (root.source === "unity_package") {
    return listPackageSkillEntries(root);
  }
  if (root.source === "unity_project" || root.source === "unity_user") {
    return listNestedSkillEntries(root);
  }
  return fs.readdirSync(root.fullPath, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && isSafeSkillName(entry.name))
    .map((entry) => ({ root, name: entry.name, skillPath: path.join(root.fullPath, entry.name, "SKILL.md"), relativePath: `${root.relativeRoot}/${entry.name}/SKILL.md` }))
    .sort((left, right) => left.name.localeCompare(right.name));
}

function listPackageSkillEntries(root: SkillRoot): SkillEntry[] {
  const rootStat = fs.lstatSync(root.fullPath);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    return [];
  }
  const startDirs: string[] = [];
  for (const item of fs.readdirSync(root.fullPath, { withFileTypes: true })) {
    if (!item.isDirectory() || item.isSymbolicLink()) {
      continue;
    }
    const assistantRoot = path.join(root.fullPath, item.name, "AIAssistantSkills");
    if (fs.existsSync(assistantRoot)) {
      const assistantStat = fs.lstatSync(assistantRoot);
      if (assistantStat.isDirectory() && !assistantStat.isSymbolicLink()) {
        startDirs.push(assistantRoot);
      }
    }
  }
  return listNestedSkillEntries(root, startDirs);
}

function listNestedSkillEntries(root: SkillRoot, startDirs: string[] = [root.fullPath]): SkillEntry[] {
  const rootStat = fs.lstatSync(root.fullPath);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    return [];
  }
  const entries: SkillEntry[] = [];
  const pending = [...startDirs];
  let visitedDirs = 0;

  while (pending.length > 0 && visitedDirs < MAX_UNITY_SKILL_DIRS) {
    const dir = pending.shift() ?? root.fullPath;
    visitedDirs += 1;
    if (!isUnderRoot(dir, root.fullPath)) {
      continue;
    }
    for (const item of fs.readdirSync(dir, { withFileTypes: true })) {
      const fullPath = path.join(dir, item.name);
      if (item.isSymbolicLink() || !isUnderRoot(fullPath, root.fullPath)) {
        continue;
      }
      if (item.isDirectory()) {
        pending.push(fullPath);
      } else if (item.isFile() && item.name === "SKILL.md" && acceptsNestedSkillPath(root, fullPath)) {
        const validation = validateSkillPath(fullPath);
        const name = typeof validation.frontmatter?.name === "string" && isSafeSkillName(validation.frontmatter.name) ? validation.frontmatter.name : path.basename(path.dirname(fullPath));
        if (isSafeSkillName(name)) {
          entries.push({ root, name, skillPath: fullPath, relativePath: toRelativeSkillPath(root, fullPath) });
        }
      }
    }
  }

  return entries.sort((left, right) => left.name.localeCompare(right.name) || left.relativePath.localeCompare(right.relativePath));
}

function acceptsNestedSkillPath(root: SkillRoot, skillPath: string): boolean {
  if (root.source !== "unity_package") {
    return true;
  }
  return path.relative(root.fullPath, skillPath).split(path.sep).includes("AIAssistantSkills");
}

function unityUserSkillsRoot(): string {
  const appData = process.env.APPDATA;
  return appData ? path.join(appData, "Unity", "AIAssistantSkills") : "";
}

function validateSkillPath(skillPath: string): SkillValidation {
  if (!fs.existsSync(skillPath)) {
    return { status: "invalid", exists: false, bytes: 0, title: null, findings: [{ severity: "error", code: "missing_skill_md", message: "SKILL.md is required" }], frontmatter: null };
  }
  const stat = fs.lstatSync(skillPath);
  if (!stat.isFile() || stat.isSymbolicLink()) {
    return { status: "invalid", exists: true, bytes: stat.size, title: null, findings: [{ severity: "error", code: "not_file", message: "SKILL.md must be a regular file" }], frontmatter: null };
  }
  const prefix = readUtf8Prefix(skillPath, Math.min(stat.size, MAX_SKILL_SIZE_BYTES));
  return validateSkillContent(prefix.text, stat.size, true, prefix.hasBom);
}

function validateSkillEntry(entry: SkillEntry, duplicateRelativePaths?: string[]): SkillEntryValidation {
  const validation = validateSkillPath(entry.skillPath);
  const findings = [...validation.findings];
  const resourceValidation = validateSkillResources(entry);
  findings.push(...resourceValidation.findings);
  if (duplicateRelativePaths && duplicateRelativePaths.length > 1) {
    findings.push({ severity: "error", code: "duplicate_skill_name", message: "Multiple skills in the selected source resolve to the same name", relative_paths: duplicateRelativePaths });
  }
  return {
    ...validation,
    status: findings.some((finding) => finding.severity === "error") ? "invalid" : validation.status,
    findings,
    resources: resourceValidation.resources,
  };
}

function duplicateSkillNames(entries: SkillEntry[]): Map<string, string[]> {
  const duplicates = new Map<string, string[]>();
  for (const entry of entries) {
    const paths = duplicates.get(entry.name) ?? [];
    paths.push(entry.relativePath);
    duplicates.set(entry.name, paths);
  }
  for (const [name, paths] of [...duplicates]) {
    if (paths.length <= 1) {
      duplicates.delete(name);
    }
  }
  return duplicates;
}

function validateSkillResources(entry: SkillEntry): { resources: Array<Record<string, unknown>>; findings: Array<Record<string, unknown>> } {
  const skillDir = path.dirname(entry.skillPath);
  if (!fs.existsSync(skillDir) || !fs.lstatSync(skillDir).isDirectory()) {
    return { resources: [], findings: [] };
  }
  const resources: Array<Record<string, unknown>> = [];
  const findings: Array<Record<string, unknown>> = [];
  const visitResource = (fullPath: string, relativePath: string): void => {
    if (resources.length >= MAX_SKILL_RESOURCES) {
      findings.push({ severity: "error", code: "resource_count_limit", message: `Skill has more than ${MAX_SKILL_RESOURCES} resources`, max_resources: MAX_SKILL_RESOURCES });
      return;
    }
    const stat = fs.lstatSync(fullPath);
    if (stat.isSymbolicLink()) {
      findings.push({ severity: "error", code: "resource_symlink", message: "Skill resources must not be symbolic links", resource_path: relativePath });
      return;
    }
    if (!stat.isFile()) {
      findings.push({ severity: "error", code: "resource_not_file", message: "Skill resources must be regular files", resource_path: relativePath });
      return;
    }
    if (stat.size > MAX_SKILL_SIZE_BYTES) {
      findings.push({ severity: "error", code: "resource_size_limit", message: `Skill resource exceeds ${MAX_SKILL_SIZE_BYTES} bytes`, resource_path: relativePath, bytes: stat.size, max_bytes: MAX_SKILL_SIZE_BYTES });
    }
    resources.push({ relative_path: relativePath, bytes: stat.size, sha256: hashFileSha256(fullPath) });
  };

  for (const item of fs.readdirSync(skillDir, { withFileTypes: true })) {
    const fullPath = path.join(skillDir, item.name);
    if (item.isSymbolicLink()) {
      findings.push({ severity: "error", code: "resource_symlink", message: "Skill folder entries must not be symbolic links", resource_path: item.name });
    } else if (item.isFile()) {
      if (item.name !== "SKILL.md") {
        visitResource(fullPath, item.name);
      }
    } else if (item.isDirectory()) {
      for (const child of fs.readdirSync(fullPath, { withFileTypes: true })) {
        const childPath = path.join(fullPath, child.name);
        const relativePath = `${item.name}/${child.name}`;
        if (child.isDirectory()) {
          findings.push({ severity: "error", code: "resource_depth_limit", message: "Skill resources are limited to the skill folder and one immediate subfolder level", resource_path: relativePath });
        } else {
          visitResource(childPath, relativePath);
        }
      }
    }
  }

  return { resources, findings };
}

function validateSkillContent(text: string, bytes: number, exists: boolean, hasBom: boolean): SkillValidation {
  const findings: Array<Record<string, unknown>> = [];
  const normalizedText = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  const frontmatter = parseFrontmatter(normalizedText, findings);
  const title = /^#\s+(.+)$/m.exec(normalizedText)?.[1]?.trim() ?? null;
  if (hasBom) {
    findings.push({ severity: "error", code: "utf8_bom", message: "SKILL.md must be UTF-8 without BOM" });
  }
  if (!title) {
    findings.push({ severity: "error", code: "missing_title", message: "SKILL.md must contain a top-level heading" });
  }
  if (frontmatter) {
    validateFrontmatter(frontmatter, findings);
  }
  if (bytes > MAX_SKILL_SIZE_BYTES) {
    findings.push({ severity: "error", code: "size_limit", message: `SKILL.md exceeds ${MAX_SKILL_SIZE_BYTES} bytes`, bytes, max_bytes: MAX_SKILL_SIZE_BYTES });
  }
  for (const marker of FORBIDDEN_MARKERS) {
    if (marker.pattern.test(text)) {
      findings.push({ severity: "warning", code: "forbidden_behavior_marker", marker: marker.id, message: "Skill text mentions behavior that is forbidden by Gateway policy" });
    }
  }
  return { status: findings.some((finding) => finding.severity === "error") ? "invalid" : "valid", exists, bytes, title, findings, frontmatter };
}

function validateFrontmatter(frontmatter: Record<string, unknown>, findings: Array<Record<string, unknown>>): void {
  if (typeof frontmatter.name !== "string" || frontmatter.name.trim().length === 0) {
    findings.push({ severity: "error", code: "missing_frontmatter_name", message: "Unity-style SKILL.md frontmatter must include name" });
  } else if (!isSafeSkillName(frontmatter.name)) {
    findings.push({ severity: "error", code: "invalid_frontmatter_name", message: "Frontmatter name must be a safe skill name", field: "name" });
  }

  if (typeof frontmatter.description !== "string" || frontmatter.description.trim().length === 0) {
    findings.push({ severity: "error", code: "missing_frontmatter_description", message: "Unity-style SKILL.md frontmatter must include description" });
  }

  if (frontmatter.enabled !== undefined && typeof frontmatter.enabled !== "boolean") {
    findings.push({ severity: "error", code: "invalid_frontmatter_enabled", message: "Frontmatter enabled must be boolean", field: "enabled" });
  }

  if (frontmatter.required_editor_version !== undefined && (typeof frontmatter.required_editor_version !== "string" || frontmatter.required_editor_version.trim().length === 0)) {
    findings.push({ severity: "error", code: "invalid_frontmatter_required_editor_version", message: "Frontmatter required_editor_version must be a non-empty string", field: "required_editor_version" });
  }

  if (frontmatter.required_packages !== undefined) {
    if (!isStringRecord(frontmatter.required_packages)) {
      findings.push({ severity: "error", code: "invalid_frontmatter_required_packages", message: "Frontmatter required_packages must be a package id to version map", field: "required_packages" });
    } else {
      for (const [packageId, version] of Object.entries(frontmatter.required_packages)) {
        if (!/^[A-Za-z0-9._-]+$/.test(packageId) || version.trim().length === 0) {
          findings.push({ severity: "error", code: "invalid_frontmatter_required_package", message: "Frontmatter required_packages entries must use safe package ids and non-empty versions", field: "required_packages", package_id: packageId });
        }
      }
    }
  }

  if (frontmatter.tools !== undefined) {
    if (!Array.isArray(frontmatter.tools) || !frontmatter.tools.every((tool) => typeof tool === "string" && /^[A-Za-z0-9._:-]{1,120}$/.test(tool))) {
      findings.push({ severity: "error", code: "invalid_frontmatter_tools", message: "Frontmatter tools must be an array of safe tool names", field: "tools" });
    } else if (frontmatter.tools.length > 0) {
      findings.push({ severity: "info", code: "tools_metadata_only", message: "Frontmatter tools are metadata only and do not change Gateway policy, approvals, or filesystem access" });
    }
  }
}

function parseFrontmatter(text: string, findings: Array<Record<string, unknown>>): Record<string, unknown> | null {
  if (!text.startsWith("---\n") && !text.startsWith("---\r\n")) {
    return null;
  }
  const match = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(text);
  if (!match) {
    findings.push({ severity: "error", code: "invalid_frontmatter", message: "YAML frontmatter must be closed with ---" });
    return {};
  }
  const metadata: Record<string, unknown> = {};
  let currentBlockKey: "required_packages" | "tools" | null = null;
  for (const rawLine of (match[1] ?? "").split(/\r?\n/)) {
    const line = rawLine.trim();
    const isIndented = /^\s+/.test(rawLine);
    if (!line || line.startsWith("#")) {
      continue;
    }
    if (isIndented && currentBlockKey === "tools" && line.startsWith("- ")) {
      (metadata.tools as string[]).push(line.slice(2).trim().replace(/^['"]|['"]$/g, ""));
      continue;
    }
    const packageEntry = /^([A-Za-z0-9._-]+):\s*(.*)$/.exec(line);
    if (isIndented && currentBlockKey === "required_packages" && packageEntry) {
      (metadata.required_packages as Record<string, string>)[packageEntry[1]!] = packageEntry[2]!.trim().replace(/^['"]|['"]$/g, "");
      continue;
    }
    const keyValue = /^(name|description|enabled|required_editor_version|required_packages|tools):\s*(.*)$/.exec(line);
    if (!keyValue) {
      currentBlockKey = null;
      continue;
    }
    const key = keyValue[1] as "name" | "description" | "enabled" | "required_editor_version" | "required_packages" | "tools";
    const rawValue = keyValue[2] ?? "";
    if (key === "required_packages" && rawValue === "") {
      metadata.required_packages = {};
      currentBlockKey = "required_packages";
    } else if (key === "tools" && rawValue === "") {
      metadata.tools = [];
      currentBlockKey = "tools";
    } else {
      metadata[key] = parseFrontmatterValue(rawValue);
      currentBlockKey = null;
    }
  }
  return metadata;
}

function parseFrontmatterValue(value: string): unknown {
  const trimmed = value.trim();
  if (trimmed === "true") {
    return true;
  }
  if (trimmed === "false") {
    return false;
  }
  if (trimmed.startsWith("[") && trimmed.endsWith("]")) {
    return trimmed.slice(1, -1).split(",").map((item) => item.trim().replace(/^['"]|['"]$/g, "")).filter(Boolean);
  }
  return trimmed.replace(/^['"]|['"]$/g, "");
}

function readReviewMetadata(record: HeartbeatRecord): { exists: boolean; relativePath: string; reviewed: Set<string> } {
  const relativePath = "Library/SGGUnityMcp/skills/reviewed-skills.json";
  const fullPath = path.join(record.project_root, relativePath);
  if (!fs.existsSync(fullPath)) {
    return { exists: false, relativePath, reviewed: new Set() };
  }
  try {
    const parsed = JSON.parse(fs.readFileSync(fullPath, "utf8")) as unknown;
    const reviewed = new Set<string>();
    if (typeof parsed === "object" && parsed !== null && Array.isArray((parsed as { reviewed?: unknown }).reviewed)) {
      for (const item of (parsed as { reviewed: unknown[] }).reviewed) {
        if (typeof item === "string") {
          reviewed.add(item);
        } else if (typeof item === "object" && item !== null && typeof (item as { name?: unknown }).name === "string") {
          reviewed.add((item as { name: string }).name);
        }
      }
    }
    return { exists: true, relativePath, reviewed };
  } catch {
    return { exists: true, relativePath, reviewed: new Set() };
  }
}

function reviewStatus(review: { exists: boolean; reviewed: Set<string> }, source: SkillSource, name: string): string {
  if (!review.exists) {
    return "metadata_missing";
  }
  return review.reviewed.has(name) || review.reviewed.has(`${source}:${name}`) ? "reviewed" : "unreviewed";
}

function reviewStatusForEntry(review: { exists: boolean; reviewed: Set<string> }, entry: SkillEntry): string {
  const importReview = importReviewStatus(entry);
  return importReview ?? reviewStatus(review, entry.root.source, entry.name);
}

function importReviewStatus(entry: SkillEntry): string | undefined {
  if (entry.root.source !== "local") {
    return undefined;
  }
  const sidecarPath = path.join(path.dirname(entry.skillPath), IMPORT_SIDECAR_NAME);
  if (!fs.existsSync(sidecarPath)) {
    return undefined;
  }
  try {
    const stat = fs.lstatSync(sidecarPath);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024) {
      return undefined;
    }
    const parsed = JSON.parse(fs.readFileSync(sidecarPath, "utf8")) as { review_status?: unknown };
    return typeof parsed.review_status === "string" && /^[A-Za-z0-9._-]{1,80}$/.test(parsed.review_status) ? parsed.review_status : undefined;
  } catch {
    return undefined;
  }
}

function resolveSkillResource(entry: SkillEntry, requestedPath: string): { status: "ok"; fullPath: string; relativePath: string; resourcePath: string } | Extract<SkillCommandResult, { status: "error" }> {
  if (!isSafeResourcePath(requestedPath)) {
    return error(path.isAbsolute(requestedPath) || /^[A-Za-z]:[\/]/.test(requestedPath) ? "absolute_path_rejected" : "path_traversal_rejected", "Skill resource_path must stay in the skill folder or one immediate subfolder", { field: "resource_path" });
  }
  const skillDir = path.dirname(entry.skillPath);
  const fullPath = path.join(skillDir, ...requestedPath.split("/"));
  const parentDir = path.dirname(fullPath);
  if (parentDir !== skillDir && fs.existsSync(parentDir)) {
    const parentStat = fs.lstatSync(parentDir);
    if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) {
      return error("invalid_field_value", "Skill resource parent must be a regular directory", { field: "resource_path" });
    }
  }
  if (!isUnderRoot(fullPath, skillDir)) {
    return error("path_traversal_rejected", "Skill resource_path must stay under the skill folder", { field: "resource_path" });
  }
  return { status: "ok", fullPath, relativePath: `${path.dirname(entry.relativePath)}/${requestedPath}`, resourcePath: requestedPath };
}

function publicRoot(root: SkillRoot): Record<string, unknown> {
  return { source: root.source, relative_root: root.relativeRoot, precedence: root.precedence };
}

function toRelativeSkillPath(root: SkillRoot, skillPath: string): string {
  return `${root.relativeRoot}/${path.relative(root.fullPath, skillPath).split(path.sep).join("/")}`;
}

function isSafeSkillName(name: string): boolean {
  return /^[A-Za-z0-9._-]{1,80}$/.test(name) && name !== "." && name !== ".." && !path.isAbsolute(name) && !/^[A-Za-z]:[\\/]/.test(name);
}

function isSafeResourcePath(resourcePath: string): boolean {
  if (!resourcePath || resourcePath.length > 512 || resourcePath.includes("\\") || resourcePath.includes("\0") || path.isAbsolute(resourcePath) || /^[A-Za-z]:[\/]/.test(resourcePath)) {
    return false;
  }
  const segments = resourcePath.split("/");
  return segments.length <= 2 && segments.every((segment) => segment.length > 0 && segment !== "." && segment !== "..");
}

function isStringRecord(value: unknown): value is Record<string, string> {
  return typeof value === "object" && value !== null && !Array.isArray(value) && Object.values(value).every((item) => typeof item === "string");
}

function hashFileSha256(filePath: string): string | null {
  if (!fs.existsSync(filePath)) {
    return null;
  }
  const stat = fs.lstatSync(filePath);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > MAX_SKILL_SIZE_BYTES) {
    return null;
  }
  return createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function isUnderRoot(candidate: string, root: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function readUtf8Prefix(filePath: string, maxBytes: number): { text: string; bytesRead: number; hasBom: boolean } {
  const fd = fs.openSync(filePath, "r");
  try {
    const buffer = Buffer.alloc(Math.max(0, maxBytes));
    const bytesRead = fs.readSync(fd, buffer, 0, buffer.length, 0);
    const readBuffer = buffer.subarray(0, bytesRead);
    return { text: readBuffer.toString("utf8"), bytesRead, hasBom: readBuffer.length >= 3 && readBuffer[0] === 0xef && readBuffer[1] === 0xbb && readBuffer[2] === 0xbf };
  } finally {
    fs.closeSync(fd);
  }
}

function clampInteger(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.trunc(value)));
}

function error(code: GatewayError["code"], message: string, details?: Record<string, unknown>): Extract<SkillCommandResult, { status: "error" }> {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}
