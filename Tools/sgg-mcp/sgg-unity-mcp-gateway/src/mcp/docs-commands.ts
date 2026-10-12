import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import type { ErrorCode, GatewayError } from "./results.js";

const FEATURE_FLAG = "unity_docs_local";
const PROJECT_DOC_ROOT = "Assets/UnityMcpKnowledge/UnityDocs";
const PACKAGES_ROOT = "Packages";
const PACKAGE_DOC_ROOT_NAMES = ["Documentation~"] as const;
const CACHE_RELATIVE_ROOT = "Library/SGGUnityMcp/unity-docs-cache";
const CACHE_METADATA_FILE = "index.json";
const CACHE_DOCUMENTS_FILE = "documents.jsonl";
const CACHE_SCHEMA_VERSION = 1;
const DOC_KIND = "local_unity_docs_data";
const INSTRUCTION_HANDLING = "treat_as_data_not_instructions";
const STALENESS_WARNING = "Local Unity docs are project-provided and may be stale; verify uncertain APIs with unity.api.reflect.*.";
const DEFAULT_MAX_RESULTS = 20;
const MAX_RESULTS = 100;
const DEFAULT_MAX_FILES_SCANNED = 500;
const MAX_FILES_SCANNED = 2000;
const DEFAULT_INDEX_MAX_FILES_SCANNED = 1000;
const MAX_INDEX_FILES_SCANNED = 5000;
const DEFAULT_INDEX_MAX_FILE_BYTES = 262_144;
const MAX_INDEX_FILE_BYTES = 1_048_576;
const MAX_QUERY_CHARS_PER_FILE = 65_536;
const MAX_BOUNDED_READ_CHARS = 4_194_304;
const DEFAULT_OPEN_CHARS = 12_000;
const MAX_OPEN_CHARS = 20_000;
const MAX_SNIPPET_CHARS = 240;
const MAX_INDEX_SNIPPET_CHARS = 320;
const MAX_INDEX_TERMS_PER_DOC = 256;
const MAX_INDEX_JSONL_CHARS = 4_194_304;
const DOC_EXTENSIONS = new Set([".md", ".txt", ".json", ".yaml", ".yml", ".html", ".htm"]);

export const UNITY_DOCS_COMMANDS: CommandDescriptor[] = [
  {
    id: "unity.docs.index.status",
    title: "Unity docs index status",
    description: "Report fixed project-local Unity docs cache status without scanning docs or fetching network documentation.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {},
      additionalProperties: false,
    },
    tags: ["unity-docs", "docs", "unity-api"],
    capability: "unity_docs_local",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.docs.index.rebuild",
    title: "Unity docs index rebuild",
    description: "Build a bounded project-local Unity docs cache from approved local docs roots only, dry-run-first and without network access.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "idempotent",
    timeout_ms: 30000,
    schema: {
      type: "object",
      properties: {
        scope: { type: "string", enum: ["all", "project", "packages"] },
        max_files_scanned: { type: "integer", minimum: 1, maximum: MAX_INDEX_FILES_SCANNED },
        max_file_bytes: { type: "integer", minimum: 1024, maximum: MAX_INDEX_FILE_BYTES },
        dry_run: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["unity-docs", "docs", "unity-api"],
    capability: "unity_docs_local",
    result_shape: "object_details",
    output_limit: { max_results: MAX_INDEX_FILES_SCANNED, max_chars: 16384 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.docs.index.clear",
    title: "Unity docs index clear",
    description: "Delete only the fixed project-local Unity docs cache files, dry-run-first and without touching source docs.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        dry_run: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["unity-docs", "docs", "unity-api"],
    capability: "unity_docs_local",
    result_shape: "object_details",
    output_limit: { max_results: 2, max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.docs.query",
    title: "Unity docs query",
    description: "Search bounded project-provided local Unity docs using the local cache when available, otherwise approved docs roots, without network access.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: {
        query: { type: "string", minLength: 2, maxLength: 128 },
        scope: { type: "string", enum: ["all", "project", "packages"] },
        max_results: { type: "integer", minimum: 1, maximum: MAX_RESULTS },
        max_files_scanned: { type: "integer", minimum: 1, maximum: MAX_FILES_SCANNED },
        prefer_index: { type: "boolean" },
      },
      required: ["query"],
      additionalProperties: false,
    },
    tags: ["unity-docs", "docs", "unity-api"],
    capability: "unity_docs_local",
    result_shape: "bounded_list",
    output_limit: { max_results: MAX_RESULTS, max_chars: 16_384 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.docs.open_reference",
    title: "Unity docs open reference",
    description: "Read one bounded local Unity docs reference returned by unity.docs.query without opening browsers or fetching network docs.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: {
        reference_id: { type: "string", pattern: "^local-doc:[0-9a-f]{16}$" },
        max_chars: { type: "integer", minimum: 1, maximum: MAX_OPEN_CHARS },
        max_files_scanned: { type: "integer", minimum: 1, maximum: MAX_FILES_SCANNED },
        prefer_index: { type: "boolean" },
      },
      required: ["reference_id"],
      additionalProperties: false,
    },
    tags: ["unity-docs", "docs", "unity-api"],
    capability: "unity_docs_local",
    result_shape: "text_artifact",
    output_limit: { max_chars: MAX_OPEN_CHARS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type UnityDocsCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };

type UnityDocsError = Extract<UnityDocsCommandResult, { status: "error" }>;
type DocsScope = "all" | "project" | "packages";
type DocsRootKind = "project" | "package";
type DocsRoot = { kind: DocsRootKind; root: string; packageId?: string };
type DocsFile = { root: DocsRoot; relativePath: string; fullPath: string; bytes: number; modified_at: string; extension: string };
type ScanResult = { roots: DocsRoot[]; files: DocsFile[]; scanned: number; truncated: boolean };
type DocsIndexMetadata = {
  schema_version: number;
  built_at_utc: string;
  unity_version: string;
  docs_kind: typeof DOC_KIND;
  instruction_handling: typeof INSTRUCTION_HANDLING;
  staleness_warning: typeof STALENESS_WARNING;
  index_mode: "local_cache_index";
  network_used: false;
  scope: DocsScope;
  source_roots: Record<string, unknown>[];
  max_files_scanned: number;
  max_file_bytes: number;
  scanned_files: number;
  indexed_files: number;
  skipped_oversized: number;
  skipped_unreadable: number;
  scan_truncated: boolean;
};
type DocsIndexEntry = {
  reference_id: string;
  path: string;
  root: DocsRoot;
  title: string;
  line: number;
  snippet: string;
  terms: string;
  bytes: number;
  modified_at: string;
  extension: string;
  content_hash: string;
};
type LoadedDocsIndex = { status: "ok"; metadata: DocsIndexMetadata; documents: DocsIndexEntry[]; documents_truncated: boolean } | { status: "missing"; reason: string } | { status: "error"; reason: string };
type CachePaths = { root: string; fullRoot: string; metadata: string; metadataFull: string; documents: string; documentsFull: string };

export function findUnityDocsCommand(commandId: string): CommandDescriptor | undefined {
  return UNITY_DOCS_COMMANDS.find((command) => command.id === commandId);
}

export function runUnityDocsCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, now: Date = new Date()): UnityDocsCommandResult {
  if (!record) {
    return error("project_offline", "Unity docs commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "unity.docs.index.status":
      return indexStatus(record);
    case "unity.docs.index.rebuild":
      return rebuildIndex(args, record, now);
    case "unity.docs.index.clear":
      return clearIndex(args, record);
    case "unity.docs.query":
      return queryDocs(args, record);
    case "unity.docs.open_reference":
      return openReference(args, record);
    default:
      return error("command_not_found", "Unity docs command not found");
  }
}

function queryDocs(args: Record<string, unknown>, record: HeartbeatRecord): UnityDocsCommandResult {
  const query = typeof args.query === "string" ? args.query.trim() : "";
  if (query.length < 2) {
    return error("schema_validation_failed", "Argument query must be at least 2 characters");
  }

  const scope = scopeArg(args.scope);
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_MAX_RESULTS, 1, MAX_RESULTS);
  const maxFilesScanned = clampInteger(typeof args.max_files_scanned === "number" ? args.max_files_scanned : DEFAULT_MAX_FILES_SCANNED, 1, MAX_FILES_SCANNED);
  const index = args.prefer_index === false ? undefined : loadDocsIndex(record);
  if (index?.status === "ok") {
    return queryDocsIndex(query, scope, maxResults, index);
  }

  const scan = scanDocs(record, scope, maxFilesScanned);
  const results: Record<string, unknown>[] = [];
  let matched = 0;

  for (const file of scan.files) {
    const match = matchFile(file, query);
    if (!match) {
      continue;
    }

    matched += 1;
    if (results.length < maxResults) {
      results.push(match);
    }
  }

  return {
    status: "ok",
    data: {
      query,
      scope,
      roots: scan.roots.map(rootJson),
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "per_call_bounded_scan",
      index_status: index?.status ?? "disabled",
      ...(index?.status === "error" ? { index_error: index.reason } : {}),
      network_used: false,
      max_results: maxResults,
      max_files_scanned: maxFilesScanned,
      scanned_files: scan.scanned,
      matched,
      truncated: scan.truncated || matched > results.length,
      scan_truncated: scan.truncated,
      results,
    },
  };
}

function openReference(args: Record<string, unknown>, record: HeartbeatRecord): UnityDocsCommandResult {
  const referenceId = typeof args.reference_id === "string" ? args.reference_id : "";
  if (!isReferenceId(referenceId)) {
    return error("path_not_allowed", "reference_id must be a local-doc reference returned by unity.docs.query");
  }

  const maxChars = clampInteger(typeof args.max_chars === "number" ? args.max_chars : DEFAULT_OPEN_CHARS, 1, MAX_OPEN_CHARS);
  const maxFilesScanned = clampInteger(typeof args.max_files_scanned === "number" ? args.max_files_scanned : MAX_FILES_SCANNED, 1, MAX_FILES_SCANNED);
  const index = args.prefer_index === false ? undefined : loadDocsIndex(record);
  if (index?.status === "ok") {
    const entry = index.documents.find((candidate) => candidate.reference_id === referenceId);
    if (entry) {
      return openIndexedReference(entry, index.metadata, record, maxChars);
    }
  }

  const scan = scanDocs(record, "all", maxFilesScanned);
  const file = scan.files.find((candidate) => referenceIdFor(candidate.relativePath) === referenceId);
  if (!file) {
    return error("asset_not_found", "Local Unity docs reference was not found under approved docs roots", {
      reference_id: referenceId,
      max_files_scanned: maxFilesScanned,
      scanned_files: scan.scanned,
      scan_truncated: scan.truncated,
    });
  }

  const bounded = readTextPrefix(file.fullPath, maxChars + 1);
  const content = bounded.length > maxChars ? bounded.slice(0, maxChars) : bounded;
  return {
    status: "ok",
    data: {
      reference_id: referenceId,
      path: file.relativePath,
      root: rootJson(file.root),
      title: titleFor(bounded, file.relativePath),
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "per_call_bounded_scan",
      index_status: index?.status ?? "disabled",
      ...(index?.status === "error" ? { index_error: index.reason } : {}),
      network_used: false,
      browser_opened: false,
      max_chars: maxChars,
      max_files_scanned: maxFilesScanned,
      scanned_files: scan.scanned,
      truncated: bounded.length > maxChars,
      content,
    },
  };
}

function indexStatus(record: HeartbeatRecord): UnityDocsCommandResult {
  const paths = cachePaths(record);
  const metadataStat = safeLstat(paths.metadataFull);
  const documentsStat = safeLstat(paths.documentsFull);
  const index = loadDocsIndex(record);
  const cacheExists = Boolean(metadataStat || documentsStat);
  return {
    status: "ok",
    data: {
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "local_cache_index",
      network_used: false,
      cache_root: paths.root,
      metadata_path: paths.metadata,
      documents_path: paths.documents,
      cache_exists: cacheExists,
      metadata_present: Boolean(metadataStat?.isFile()),
      documents_present: Boolean(documentsStat?.isFile()),
      metadata_bytes: metadataStat?.isFile() ? metadataStat.size : 0,
      documents_bytes: documentsStat?.isFile() ? documentsStat.size : 0,
      status: index.status,
      ...(index.status === "ok"
        ? {
            cache_schema_version: index.metadata.schema_version,
            built_at_utc: index.metadata.built_at_utc,
            source_unity_version: index.metadata.unity_version,
            source_roots: index.metadata.source_roots,
            scanned_files: index.metadata.scanned_files,
            indexed_files: index.metadata.indexed_files,
            skipped_oversized: index.metadata.skipped_oversized,
            skipped_unreadable: index.metadata.skipped_unreadable,
            scan_truncated: index.metadata.scan_truncated,
            documents_loaded: index.documents.length,
            documents_truncated: index.documents_truncated,
            cache_possible_stale: true,
          }
        : { reason: index.reason }),
    },
  };
}

function rebuildIndex(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): UnityDocsCommandResult {
  const scope = scopeArg(args.scope);
  const maxFilesScanned = clampInteger(typeof args.max_files_scanned === "number" ? args.max_files_scanned : DEFAULT_INDEX_MAX_FILES_SCANNED, 1, MAX_INDEX_FILES_SCANNED);
  const maxFileBytes = clampInteger(typeof args.max_file_bytes === "number" ? args.max_file_bytes : DEFAULT_INDEX_MAX_FILE_BYTES, 1024, MAX_INDEX_FILE_BYTES);
  const dryRun = args.dry_run !== false;
  const scan = scanDocs(record, scope, maxFilesScanned);
  const documents: DocsIndexEntry[] = [];
  let skippedOversized = 0;
  let skippedUnreadable = 0;

  for (const file of scan.files) {
    if (file.bytes > maxFileBytes) {
      skippedOversized += 1;
      continue;
    }

    const content = tryReadTextPrefix(file.fullPath, maxFileBytes + 1);
    if (content === undefined) {
      skippedUnreadable += 1;
      continue;
    }

    const title = titleFor(content, file.relativePath);
    const snippetValue = snippetForIndex(content, title, file.relativePath);
    documents.push({
      reference_id: referenceIdFor(file.relativePath),
      path: file.relativePath,
      root: file.root,
      title,
      line: firstContentLine(content),
      snippet: snippetValue,
      terms: termsForIndex(file.relativePath, title, content),
      bytes: file.bytes,
      modified_at: file.modified_at,
      extension: file.extension,
      content_hash: contentHash(content),
    });
  }

  const metadata: DocsIndexMetadata = {
    schema_version: CACHE_SCHEMA_VERSION,
    built_at_utc: now.toISOString(),
    unity_version: record.unity_version,
    docs_kind: DOC_KIND,
    instruction_handling: INSTRUCTION_HANDLING,
    staleness_warning: STALENESS_WARNING,
    index_mode: "local_cache_index",
    network_used: false,
    scope,
    source_roots: scan.roots.map(rootJson),
    max_files_scanned: maxFilesScanned,
    max_file_bytes: maxFileBytes,
    scanned_files: scan.scanned,
    indexed_files: documents.length,
    skipped_oversized: skippedOversized,
    skipped_unreadable: skippedUnreadable,
    scan_truncated: scan.truncated,
  };

  if (!dryRun) {
    const writeResult = writeDocsIndex(record, metadata, documents);
    if (writeResult.status === "error") {
      return writeResult;
    }
  }

  const paths = cachePaths(record);
  return {
    status: "ok",
    data: {
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "local_cache_index",
      network_used: false,
      dry_run: dryRun,
      cache_root: paths.root,
      metadata_path: paths.metadata,
      documents_path: paths.documents,
      cache_schema_version: CACHE_SCHEMA_VERSION,
      built_at_utc: metadata.built_at_utc,
      source_unity_version: metadata.unity_version,
      scope,
      source_roots: metadata.source_roots,
      max_files_scanned: maxFilesScanned,
      max_file_bytes: maxFileBytes,
      scanned_files: scan.scanned,
      indexed_files: documents.length,
      skipped_oversized: skippedOversized,
      skipped_unreadable: skippedUnreadable,
      scan_truncated: scan.truncated,
      cache_written: !dryRun,
      cache_possible_stale: !dryRun,
      next_valid_actions: dryRun ? ["rerun_with_dry_run_false_after_policy_approval"] : ["call_unity.docs.index.status", "call_unity.docs.query"],
    },
  };
}

function clearIndex(args: Record<string, unknown>, record: HeartbeatRecord): UnityDocsCommandResult {
  const dryRun = args.dry_run !== false;
  const paths = cachePaths(record);
  const targets = [
    { path: paths.metadata, fullPath: paths.metadataFull },
    { path: paths.documents, fullPath: paths.documentsFull },
  ];
  const results: Record<string, unknown>[] = [];
  let deleted = 0;
  let skippedSymlink = 0;

  for (const target of targets) {
    const stat = safeLstat(target.fullPath);
    if (!stat) {
      results.push({ path: target.path, exists: false, deleted: false });
      continue;
    }

    if (stat.isSymbolicLink()) {
      skippedSymlink += 1;
      results.push({ path: target.path, exists: true, deleted: false, skipped: true, reason: "symlink_not_deleted" });
      continue;
    }

    if (!stat.isFile()) {
      results.push({ path: target.path, exists: true, deleted: false, skipped: true, reason: "not_a_file" });
      continue;
    }

    if (!dryRun) {
      fs.rmSync(target.fullPath, { force: true });
      deleted += 1;
    }

    results.push({ path: target.path, exists: true, deleted: !dryRun, bytes: stat.size });
  }

  return {
    status: "ok",
    data: {
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "local_cache_index",
      network_used: false,
      dry_run: dryRun,
      cache_root: paths.root,
      targets: results,
      deleted_count: deleted,
      skipped_symlink_count: skippedSymlink,
      next_valid_actions: dryRun ? ["rerun_with_dry_run_false_after_policy_approval"] : ["call_unity.docs.index.status"],
    },
  };
}

function queryDocsIndex(query: string, scope: DocsScope, maxResults: number, index: Extract<LoadedDocsIndex, { status: "ok" }>): UnityDocsCommandResult {
  const queryTerms = termsForQuery(query);
  const results: Record<string, unknown>[] = [];
  let matched = 0;

  for (const document of index.documents) {
    if (scope !== "all" && document.root.kind !== scope.slice(0, -1)) {
      continue;
    }

    const match = matchIndexedDocument(document, query, queryTerms, index.metadata);
    if (!match) {
      continue;
    }

    matched += 1;
    results.push(match);
  }

  results.sort((left, right) => {
    const score = Number(right.score ?? 0) - Number(left.score ?? 0);
    return score !== 0 ? score : String(left.path).localeCompare(String(right.path));
  });

  return {
    status: "ok",
    data: {
      query,
      scope,
      roots: index.metadata.source_roots,
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "local_cache_index",
      cache_built_at_utc: index.metadata.built_at_utc,
      source_unity_version: index.metadata.unity_version,
      cache_schema_version: index.metadata.schema_version,
      cache_possible_stale: true,
      network_used: false,
      max_results: maxResults,
      indexed_files: index.metadata.indexed_files,
      matched,
      truncated: matched > maxResults || index.documents_truncated,
      results: results.slice(0, maxResults),
    },
  };
}

function matchIndexedDocument(document: DocsIndexEntry, query: string, queryTerms: string[], metadata: DocsIndexMetadata): Record<string, unknown> | undefined {
  const lowerQuery = query.toLowerCase();
  const lowerTitle = document.title.toLowerCase();
  const lowerPath = document.path.toLowerCase();
  const lowerSnippet = document.snippet.toLowerCase();
  const lowerTerms = document.terms.toLowerCase();
  let score = 0;
  if (lowerTitle.includes(lowerQuery)) {
    score += 50;
  }
  if (lowerPath.includes(lowerQuery)) {
    score += 35;
  }
  if (lowerSnippet.includes(lowerQuery)) {
    score += 25;
  }
  if (lowerTerms.includes(lowerQuery)) {
    score += 20;
  }
  for (const term of queryTerms) {
    if (lowerTitle.includes(term)) {
      score += 12;
    }
    if (lowerPath.includes(term)) {
      score += 8;
    }
    if (lowerSnippet.includes(term)) {
      score += 6;
    }
    if (lowerTerms.includes(term)) {
      score += 4;
    }
  }

  if (score <= 0) {
    return undefined;
  }

  return {
    reference_id: document.reference_id,
    path: document.path,
    root: rootJson(document.root),
    title: document.title,
    line: document.line,
    snippet: document.snippet,
    score,
    bytes: document.bytes,
    modified_at: document.modified_at,
    extension: document.extension,
    content_hash: document.content_hash,
    docs_kind: DOC_KIND,
    instruction_handling: INSTRUCTION_HANDLING,
    index_mode: "local_cache_index",
    cache_built_at_utc: metadata.built_at_utc,
    source_unity_version: metadata.unity_version,
    cache_possible_stale: true,
  };
}

function openIndexedReference(entry: DocsIndexEntry, metadata: DocsIndexMetadata, record: HeartbeatRecord, maxChars: number): UnityDocsCommandResult {
  if (!isSafeDocsRelativePath(entry.path)) {
    return error("path_not_allowed", "Cached Unity docs reference is outside approved docs roots");
  }

  const root = docsRootForRelativePath(entry.path);
  if (!root) {
    return error("path_not_allowed", "Cached Unity docs reference is outside approved docs roots");
  }

  const fullPath = projectPath(record, entry.path);
  const fullRoot = projectPath(record, root.root);
  if (!isUnderRoot(fullPath, fullRoot)) {
    return error("path_not_allowed", "Cached Unity docs reference is outside approved docs roots");
  }

  const stat = safeLstat(fullPath);
  if (!stat || !stat.isFile() || stat.isSymbolicLink()) {
    return error("asset_not_found", "Local Unity docs reference source was not found under approved docs roots", {
      reference_id: entry.reference_id,
      path: entry.path,
      cache_built_at_utc: metadata.built_at_utc,
    });
  }

  const bounded = readTextPrefix(fullPath, maxChars + 1);
  const content = bounded.length > maxChars ? bounded.slice(0, maxChars) : bounded;
  const sourceChanged = stat.size !== entry.bytes || stat.mtime.toISOString() !== entry.modified_at;
  return {
    status: "ok",
    data: {
      reference_id: entry.reference_id,
      path: entry.path,
      root: rootJson(root),
      title: titleFor(bounded, entry.path),
      docs_kind: DOC_KIND,
      instruction_handling: INSTRUCTION_HANDLING,
      staleness_warning: STALENESS_WARNING,
      index_mode: "local_cache_index",
      cache_built_at_utc: metadata.built_at_utc,
      source_unity_version: metadata.unity_version,
      source_changed_since_index: sourceChanged,
      cache_possible_stale: true,
      network_used: false,
      browser_opened: false,
      max_chars: maxChars,
      truncated: bounded.length > maxChars,
      content,
    },
  };
}

function scanDocs(record: HeartbeatRecord, scope: DocsScope, maxFilesScanned: number): ScanResult {
  const roots = docsRoots(record, scope);
  const files: DocsFile[] = [];
  let scanned = 0;
  let truncated = false;
  for (const root of roots) {
    const rootFullPath = projectPath(record, root.root);
    if (!fs.existsSync(rootFullPath)) {
      continue;
    }

    const listed = scanRoot(record, root, root.root, rootFullPath, rootFullPath, maxFilesScanned - scanned);
    files.push(...listed.files);
    scanned += listed.scanned;
    truncated = truncated || listed.truncated;
    if (scanned >= maxFilesScanned) {
      truncated = true;
      break;
    }
  }

  files.sort((left, right) => left.relativePath.localeCompare(right.relativePath));
  return { roots, files, scanned, truncated };
}

function docsRoots(record: HeartbeatRecord, scope: DocsScope): DocsRoot[] {
  const roots: DocsRoot[] = [];
  if (scope === "all" || scope === "project") {
    roots.push({ kind: "project", root: PROJECT_DOC_ROOT });
  }

  if (scope !== "all" && scope !== "packages") {
    return roots;
  }

  const packagesRoot = projectPath(record, PACKAGES_ROOT);
  if (!fs.existsSync(packagesRoot)) {
    return roots;
  }

  for (const entry of sortedDirEntries(packagesRoot)) {
    if (!entry.isDirectory() || !isSafePathSegment(entry.name)) {
      continue;
    }

    for (const docsRootName of PACKAGE_DOC_ROOT_NAMES) {
      const root = `${PACKAGES_ROOT}/${entry.name}/${docsRootName}`;
      const fullPath = projectPath(record, root);
      if (fs.existsSync(fullPath)) {
        roots.push({ kind: "package", root, packageId: entry.name });
      }
    }
  }

  return roots;
}

function scanRoot(record: HeartbeatRecord, root: DocsRoot, relativeDir: string, fullDir: string, fullRoot: string, remaining: number): { files: DocsFile[]; scanned: number; truncated: boolean } {
  if (remaining <= 0) {
    return { files: [], scanned: 0, truncated: true };
  }

  const files: DocsFile[] = [];
  let scanned = 0;
  let truncated = false;
  for (const entry of sortedDirEntries(fullDir)) {
    if (!isSafePathSegment(entry.name)) {
      continue;
    }

    const relativePath = `${relativeDir}/${entry.name}`;
    const fullPath = projectPath(record, relativePath);
    if (!isUnderRoot(fullPath, fullRoot)) {
      truncated = true;
      continue;
    }

    const stat = safeLstat(fullPath);
    if (!stat || stat.isSymbolicLink()) {
      continue;
    }

    if (stat.isDirectory()) {
      const child = scanRoot(record, root, relativePath, fullPath, fullRoot, remaining - scanned);
      files.push(...child.files);
      scanned += child.scanned;
      truncated = truncated || child.truncated;
    } else if (stat.isFile() && isDocsFile(relativePath)) {
      if (scanned >= remaining) {
        truncated = true;
        break;
      }
      files.push({
        root,
        relativePath,
        fullPath,
        bytes: stat.size,
        modified_at: stat.mtime.toISOString(),
        extension: path.extname(relativePath).toLowerCase(),
      });
      scanned += 1;
    }

    if (scanned >= remaining) {
      truncated = true;
      break;
    }
  }

  return { files, scanned, truncated };
}

function matchFile(file: DocsFile, query: string): Record<string, unknown> | undefined {
  const content = readTextPrefix(file.fullPath, MAX_QUERY_CHARS_PER_FILE);
  const title = titleFor(content, file.relativePath);
  const lowerQuery = query.toLowerCase();
  const pathMatches = file.relativePath.toLowerCase().includes(lowerQuery) || title.toLowerCase().includes(lowerQuery);
  const lines = content.split(/\r?\n/);
  for (const [index, line] of lines.entries()) {
    if (line.toLowerCase().includes(lowerQuery)) {
      return matchJson(file, title, index + 1, snippet(line));
    }
  }

  if (pathMatches) {
    return matchJson(file, title, 0, snippet(title || file.relativePath));
  }

  return undefined;
}

function matchJson(file: DocsFile, title: string, line: number, snippetValue: string): Record<string, unknown> {
  return {
    reference_id: referenceIdFor(file.relativePath),
    path: file.relativePath,
    root: rootJson(file.root),
    title,
    line,
    snippet: snippetValue,
    bytes: file.bytes,
    modified_at: file.modified_at,
    extension: file.extension,
    docs_kind: DOC_KIND,
    instruction_handling: INSTRUCTION_HANDLING,
  };
}

function loadDocsIndex(record: HeartbeatRecord): LoadedDocsIndex {
  const paths = cachePaths(record);
  if (!fs.existsSync(paths.metadataFull) || !fs.existsSync(paths.documentsFull)) {
    return { status: "missing", reason: "cache_files_missing" };
  }

  const metadataText = tryReadTextPrefix(paths.metadataFull, 65_536);
  const documentsText = tryReadTextPrefix(paths.documentsFull, MAX_INDEX_JSONL_CHARS);
  if (metadataText === undefined || documentsText === undefined) {
    return { status: "error", reason: "cache_unreadable" };
  }

  try {
    const metadata = JSON.parse(metadataText) as Partial<DocsIndexMetadata>;
    if (metadata.schema_version !== CACHE_SCHEMA_VERSION || metadata.index_mode !== "local_cache_index") {
      return { status: "error", reason: "cache_schema_mismatch" };
    }

    const documents: DocsIndexEntry[] = [];
    let documentsTruncated = false;
    for (const rawLine of documentsText.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line) {
        continue;
      }
      if (documents.length >= MAX_INDEX_FILES_SCANNED) {
        documentsTruncated = true;
        break;
      }
      const parsed = safeParseIndexEntry(line);
      if (parsed) {
        documents.push(parsed);
      }
    }

    return {
      status: "ok",
      metadata: {
        schema_version: CACHE_SCHEMA_VERSION,
        built_at_utc: typeof metadata.built_at_utc === "string" ? metadata.built_at_utc : "",
        unity_version: typeof metadata.unity_version === "string" ? metadata.unity_version : "",
        docs_kind: DOC_KIND,
        instruction_handling: INSTRUCTION_HANDLING,
        staleness_warning: STALENESS_WARNING,
        index_mode: "local_cache_index",
        network_used: false,
        scope: scopeArg(metadata.scope),
        source_roots: Array.isArray(metadata.source_roots) ? metadata.source_roots.filter((root) => root && typeof root === "object") as Record<string, unknown>[] : [],
        max_files_scanned: typeof metadata.max_files_scanned === "number" ? metadata.max_files_scanned : 0,
        max_file_bytes: typeof metadata.max_file_bytes === "number" ? metadata.max_file_bytes : 0,
        scanned_files: typeof metadata.scanned_files === "number" ? metadata.scanned_files : 0,
        indexed_files: typeof metadata.indexed_files === "number" ? metadata.indexed_files : documents.length,
        skipped_oversized: typeof metadata.skipped_oversized === "number" ? metadata.skipped_oversized : 0,
        skipped_unreadable: typeof metadata.skipped_unreadable === "number" ? metadata.skipped_unreadable : 0,
        scan_truncated: metadata.scan_truncated === true,
      },
      documents,
      documents_truncated: documentsTruncated || documentsText.length >= MAX_INDEX_JSONL_CHARS,
    };
  } catch (cause) {
    return { status: "error", reason: cause instanceof Error ? cause.message : "cache_parse_failed" };
  }
}

function writeDocsIndex(record: HeartbeatRecord, metadata: DocsIndexMetadata, documents: DocsIndexEntry[]): UnityDocsCommandResult | { status: "ok" } {
  const paths = cachePaths(record);
  const rootStat = safeLstat(paths.fullRoot);
  if (rootStat?.isSymbolicLink()) {
    return error("path_not_allowed", "Unity docs cache root must not be a symbolic link", { cache_root: paths.root });
  }

  for (const target of [paths.metadataFull, paths.documentsFull]) {
    const stat = safeLstat(target);
    if (stat?.isSymbolicLink()) {
      return error("path_not_allowed", "Unity docs cache files must not be symbolic links", { cache_root: paths.root });
    }
  }

  fs.mkdirSync(paths.fullRoot, { recursive: true });
  fs.writeFileSync(paths.metadataFull, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
  fs.writeFileSync(paths.documentsFull, documents.map((document) => JSON.stringify(document)).join("\n") + (documents.length > 0 ? "\n" : ""), "utf8");
  return { status: "ok" };
}

function safeParseIndexEntry(line: string): DocsIndexEntry | undefined {
  try {
    const parsed = JSON.parse(line) as Partial<DocsIndexEntry>;
    if (typeof parsed.path !== "string" || typeof parsed.reference_id !== "string" || !isReferenceId(parsed.reference_id)) {
      return undefined;
    }
    if (!isSafeDocsRelativePath(parsed.path) || referenceIdFor(parsed.path) !== parsed.reference_id) {
      return undefined;
    }

    const root = docsRootForRelativePath(parsed.path);
    if (!root) {
      return undefined;
    }

    return {
      reference_id: parsed.reference_id,
      path: parsed.path,
      root,
      title: typeof parsed.title === "string" ? parsed.title.slice(0, 256) : path.basename(parsed.path),
      line: typeof parsed.line === "number" && Number.isInteger(parsed.line) ? parsed.line : 0,
      snippet: typeof parsed.snippet === "string" ? parsed.snippet.slice(0, MAX_INDEX_SNIPPET_CHARS) : "",
      terms: typeof parsed.terms === "string" ? parsed.terms.slice(0, 8192) : "",
      bytes: typeof parsed.bytes === "number" && Number.isFinite(parsed.bytes) ? parsed.bytes : 0,
      modified_at: typeof parsed.modified_at === "string" ? parsed.modified_at : "",
      extension: typeof parsed.extension === "string" ? parsed.extension : path.extname(parsed.path).toLowerCase(),
      content_hash: typeof parsed.content_hash === "string" ? parsed.content_hash : "",
    };
  } catch {
    return undefined;
  }
}

function titleFor(content: string, fallbackPath: string): string {
  for (const line of content.split(/\r?\n/).slice(0, 120)) {
    const trimmed = line.trim();
    if (trimmed.startsWith("#")) {
      return trimmed.replace(/^#+/, "").trim();
    }
    const htmlTitle = /<title>([^<]+)<\/title>/i.exec(trimmed);
    if (htmlTitle?.[1]) {
      return htmlTitle[1].trim();
    }
  }
  return path.basename(fallbackPath);
}

function readTextPrefix(fullPath: string, maxChars: number): string {
  const fd = fs.openSync(fullPath, "r");
  try {
    const buffer = Buffer.alloc(Math.min(Math.max(maxChars, 1), MAX_BOUNDED_READ_CHARS));
    const bytesRead = fs.readSync(fd, buffer, 0, buffer.length, 0);
    return buffer.toString("utf8", 0, bytesRead);
  } finally {
    fs.closeSync(fd);
  }
}

function tryReadTextPrefix(fullPath: string, maxChars: number): string | undefined {
  try {
    return readTextPrefix(fullPath, maxChars);
  } catch {
    return undefined;
  }
}

function rootJson(root: DocsRoot): Record<string, unknown> {
  return {
    kind: root.kind,
    root: root.root,
    ...(root.packageId ? { package_id: root.packageId } : {}),
  };
}

function referenceIdFor(relativePath: string): string {
  return `local-doc:${createHash("sha256").update(relativePath).digest("hex").slice(0, 16)}`;
}

function isReferenceId(value: string): boolean {
  return /^local-doc:[0-9a-f]{16}$/.test(value);
}

function cachePaths(record: HeartbeatRecord): CachePaths {
  const metadata = `${CACHE_RELATIVE_ROOT}/${CACHE_METADATA_FILE}`;
  const documents = `${CACHE_RELATIVE_ROOT}/${CACHE_DOCUMENTS_FILE}`;
  return {
    root: CACHE_RELATIVE_ROOT,
    fullRoot: projectPath(record, CACHE_RELATIVE_ROOT),
    metadata,
    metadataFull: projectPath(record, metadata),
    documents,
    documentsFull: projectPath(record, documents),
  };
}

function docsRootForRelativePath(relativePath: string): DocsRoot | undefined {
  if (relativePath.startsWith(`${PROJECT_DOC_ROOT}/`)) {
    return { kind: "project", root: PROJECT_DOC_ROOT };
  }

  const parts = relativePath.split("/");
  const packageId = parts[1];
  const docsRootName = parts[2];
  if (parts.length > 3 && parts[0] === PACKAGES_ROOT && packageId && docsRootName && isSafePathSegment(packageId) && PACKAGE_DOC_ROOT_NAMES.includes(docsRootName as (typeof PACKAGE_DOC_ROOT_NAMES)[number])) {
    return { kind: "package", root: `${PACKAGES_ROOT}/${packageId}/${docsRootName}`, packageId };
  }

  return undefined;
}

function isSafeDocsRelativePath(relativePath: string): boolean {
  return relativePath.split("/").every(isSafePathSegment) && Boolean(docsRootForRelativePath(relativePath));
}

function isDocsFile(relativePath: string): boolean {
  return DOC_EXTENSIONS.has(path.extname(relativePath).toLowerCase());
}

function scopeArg(value: unknown): DocsScope {
  return value === "project" || value === "packages" || value === "all" ? value : "all";
}

function sortedDirEntries(fullPath: string): fs.Dirent[] {
  try {
    return fs.readdirSync(fullPath, { withFileTypes: true }).sort((left, right) => left.name.localeCompare(right.name));
  } catch {
    return [];
  }
}

function safeLstat(fullPath: string): fs.Stats | undefined {
  try {
    return fs.lstatSync(fullPath);
  } catch {
    return undefined;
  }
}

function isSafePathSegment(value: string): boolean {
  return value.length > 0 && value !== "." && value !== ".." && !value.includes("\0") && !value.includes("/") && !value.includes("\\") && !value.includes(":");
}

function projectPath(record: HeartbeatRecord, relativePath: string): string {
  return path.join(record.project_root, relativePath);
}

function isUnderRoot(candidate: string, root: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function snippet(value: string): string {
  const trimmed = value.trim().replace(/\s+/g, " ");
  return trimmed.length > MAX_SNIPPET_CHARS ? trimmed.slice(0, MAX_SNIPPET_CHARS) : trimmed;
}

function snippetForIndex(content: string, title: string, fallbackPath: string): string {
  for (const line of content.split(/\r?\n/).slice(0, 200)) {
    const trimmed = line.trim();
    if (trimmed && !trimmed.startsWith("#") && trimmed !== title) {
      return truncateSnippet(trimmed, MAX_INDEX_SNIPPET_CHARS);
    }
  }
  return truncateSnippet(title || fallbackPath, MAX_INDEX_SNIPPET_CHARS);
}

function firstContentLine(content: string): number {
  const lines = content.split(/\r?\n/);
  for (const [index, line] of lines.entries()) {
    const trimmed = line.trim();
    if (trimmed && !trimmed.startsWith("#")) {
      return index + 1;
    }
  }
  return 0;
}

function truncateSnippet(value: string, maxChars: number): string {
  const normalized = value.trim().replace(/\s+/g, " ");
  return normalized.length > maxChars ? normalized.slice(0, maxChars) : normalized;
}

function termsForIndex(relativePath: string, title: string, content: string): string {
  return uniqueTerms(`${relativePath}\n${title}\n${content}`, MAX_INDEX_TERMS_PER_DOC).join(" ");
}

function termsForQuery(query: string): string[] {
  return uniqueTerms(query, 16);
}

function uniqueTerms(value: string, maxTerms: number): string[] {
  const terms: string[] = [];
  const seen = new Set<string>();
  for (const match of value.matchAll(/[a-z0-9_#.]+/gi)) {
    const term = match[0].toLowerCase();
    if (term.length < 2 || seen.has(term)) {
      continue;
    }
    seen.add(term);
    terms.push(term);
    if (terms.length >= maxTerms) {
      break;
    }
  }
  return terms;
}

function contentHash(content: string): string {
  return createHash("sha256").update(content).digest("hex");
}

function clampInteger(value: number, min: number, max: number): number {
  if (!Number.isInteger(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function error(code: ErrorCode, message: string, details?: Record<string, unknown>): UnityDocsError {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}
