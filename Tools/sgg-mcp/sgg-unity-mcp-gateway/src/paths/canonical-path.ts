import { createHash } from "node:crypto";

import { normalize as normalizeCanonicalPath, toWin32, toWSL } from "@romanilyin/canonicalpath";

export type CanonicalPathValue = string;
export type CanonicalProjectRoot = CanonicalPathValue;

export type PathAliasEnvironment = "windows" | "wsl" | "unity" | "posix";

export type PathAlias = {
  environment: PathAliasEnvironment;
  host_path: string;
  canonical_path: CanonicalPathValue;
};

export type CanonicalPathOptions = {
  wslMountRoot?: string;
};

export class CanonicalPathService {
  readonly #wslMountRoot: string;

  constructor(options: CanonicalPathOptions = {}) {
    this.#wslMountRoot = options.wslMountRoot ?? "/mnt";
  }

  normalize(input: string): CanonicalPathValue {
    try {
      return normalizeCanonicalPath(input, { wsl: { enabled: true, mountRoot: this.#wslMountRoot } });
    } catch (error) {
      throw mapCanonicalPathError(error);
    }
  }

  normalizeProjectRoot(input: string): CanonicalPathValue {
    assertNoEmptyRoot(input);
    assertNotUncPath(input.replaceAll("\\", "/"));
    const normalized = trimTrailingSlash(this.normalize(input));
    assertNoEmptyRoot(normalized);
    assertNotUncPath(normalized);
    return normalized;
  }

  projectRootAliases(input: string): PathAlias[] {
    const canonicalRoot = this.normalizeProjectRoot(input);
    const aliases: PathAlias[] = [];
    const addAlias = (environment: PathAliasEnvironment, hostPath: string) => {
      if (!aliases.some((alias) => alias.environment === environment && alias.host_path === hostPath)) {
        aliases.push({ environment, host_path: hostPath, canonical_path: canonicalRoot });
      }
    };

    const original = trimTrailingSlash(input.replaceAll("\\", "/"));
    if (isWslDrivePath(original, this.#wslMountRoot)) {
      addAlias("wsl", original);
    } else if (/^[A-Za-z]:\//.test(original)) {
      addAlias("unity", uppercaseDriveLetter(original));
    } else if (!original.startsWith("//")) {
      addAlias("posix", original);
    }

    if (/^[a-z]:\//.test(canonicalRoot)) {
      addAlias("unity", uppercaseDriveLetter(canonicalRoot));
      const canonical = normalizeCanonicalPath(canonicalRoot);
      addAlias("windows", toWin32(canonical));
      addAlias("wsl", toWSL(canonical, { mountRoot: this.#wslMountRoot }));
    }

    return aliases;
  }

  fromUnityAssetPath(projectRoot: CanonicalPathValue, unityPath: string): CanonicalPathValue {
    const validatedUnityPath = validateUnityAssetPath(unityPath);
    return this.normalize(`${projectRoot}/${validatedUnityPath}`);
  }

  toUnityAssetPath(projectRoot: CanonicalPathValue, fullPath: CanonicalPathValue): string {
    this.assertInsideProject(projectRoot, fullPath);
    const root = trimTrailingSlash(this.normalize(projectRoot));
    const candidate = this.normalize(fullPath);
    const relative = candidate === root ? "" : candidate.slice(root.length + 1);
    return validateUnityAssetPath(relative);
  }

  assertInsideProject(projectRoot: CanonicalPathValue, candidate: CanonicalPathValue): void {
    assertNotUncPath(projectRoot.replaceAll("\\", "/"));
    assertNotUncPath(candidate.replaceAll("\\", "/"));
    if (hasTraversal(candidate.replaceAll("\\", "/"))) {
      throw new Error("path_traversal_rejected");
    }
    const root = trimTrailingSlash(this.normalize(projectRoot));
    const normalizedCandidate = trimTrailingSlash(this.normalize(candidate));

    assertNoEmptyRoot(root);

    if (normalizedCandidate !== root && !normalizedCandidate.startsWith(`${root}/`)) {
      throw new Error("path_not_allowed");
    }
  }

  makeSafeFileName(input: string, maxLength = 80): string {
    return makeSafeFileName(input, maxLength);
  }
}

export function validateUnityAssetPath(input: string): string {
  assertNoNul(input);
  const normalized = input.replaceAll("\\", "/");

  if (normalized.startsWith("/") || /^[A-Za-z]:/.test(normalized)) {
    throw new Error("absolute_path_rejected");
  }

  if (hasEncodedSeparator(normalized)) {
    throw new Error("path_not_allowed");
  }

  if (hasTraversal(normalized)) {
    throw new Error("path_traversal_rejected");
  }

  if (normalized.split("/").includes(".")) {
    throw new Error("path_not_allowed");
  }

  if (!normalized.startsWith("Assets/") && !normalized.startsWith("Packages/")) {
    throw new Error("path_not_allowed");
  }

  try {
    const canonical = normalizeCanonicalPath(normalized);
    assertSafeUnityComponents(canonical);
    return canonical;
  } catch (error) {
    throw mapCanonicalPathError(error);
  }
}

export function makeSafeFileName(input: string, maxLength: number): string {
  assertNoNul(input);
  if (maxLength < 16) {
    throw new Error("filename_too_long");
  }

  const normalized = input.trim().replaceAll(/[^A-Za-z0-9._-]+/g, "-").replaceAll(/^-+|-+$/g, "");
  const safe = normalized.length > 0 ? normalized : "file";
  if (safe.length <= maxLength) {
    return safe;
  }

  const extensionMatch = /\.([A-Za-z0-9]{1,16})$/.exec(safe);
  const extension = extensionMatch ? `.${extensionMatch[1]}` : "";
  const stem = extension ? safe.slice(0, -extension.length) : safe;
  const hash = createHash("sha256").update(input).digest("hex").slice(0, 8);
  const maxStemLength = Math.max(1, maxLength - extension.length - hash.length - 2);
  const truncatedStem = stem.slice(0, maxStemLength).replaceAll(/[-._]+$/g, "") || "file";
  return `${truncatedStem}--${hash}${extension}`;
}

function assertNoNul(input: string): void {
  if (input.includes("\0")) {
    throw new Error("path_not_allowed");
  }
}

function assertNoEmptyRoot(input: string): void {
  if (input.trim().length === 0) {
    throw new Error("path_not_allowed");
  }
}

function assertNotUncPath(input: string): void {
  if (input.startsWith("//")) {
    throw new Error("absolute_path_rejected");
  }
}

function uppercaseDriveLetter(input: string): string {
  return input.replace(/^([A-Za-z]):\//, (_match, drive: string) => `${drive.toUpperCase()}:/`);
}

function isWslDrivePath(input: string, wslMountRoot: string): boolean {
  const escapedRoot = escapeRegExp(trimTrailingSlash(wslMountRoot));
  return new RegExp(`^${escapedRoot}/[A-Za-z](?:/|$)`).test(input);
}

function trimTrailingSlash(input: string): string {
  if (/^[a-z]:\/$/i.test(input) || input === "/") {
    return input;
  }
  return input.replaceAll(/\/+$/g, "");
}

function hasTraversal(input: string): boolean {
  return input.split("/").includes("..");
}

function hasEncodedSeparator(input: string): boolean {
  return /%(?:2f|5c|252f|255c)/i.test(input);
}

function assertSafeUnityComponents(input: string): void {
  for (const component of input.split("/")) {
    if (component.length === 0 || component.length > 255 || /[. ]$/.test(component) || component.includes(":") || isWindowsReservedName(component)) {
      throw new Error("path_not_allowed");
    }
  }
}

function isWindowsReservedName(component: string): boolean {
  const stem = component.split(".", 1)[0]?.toUpperCase() ?? "";
  return /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/.test(stem);
}

function escapeRegExp(input: string): string {
  return input.replaceAll(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function mapCanonicalPathError(error: unknown): Error {
  const code = typeof error === "object" && error !== null && "code" in error ? String(error.code) : "";
  if (code === "ERR_DRIVE_RELATIVE_PATH") {
    return new Error("absolute_path_rejected", { cause: error });
  }
  return new Error("path_not_allowed", { cause: error });
}
