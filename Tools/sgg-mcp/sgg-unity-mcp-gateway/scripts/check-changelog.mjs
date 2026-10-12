#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { parseCalendarVersion } from "./check-version.mjs";

const CURRENT_SECTIONS = ["Added", "Changed", "Fixed"];
const INTERNAL_NOISE_RE = /\b(?:HARDEN-\d+[A-Z0-9-]*|implementation slice|mutation tests?|internal development journal)\b/i;
const PLACEHOLDER_RE = /\b(?:TBD|TODO|placeholder|coming soon|not yet released|unreleased)\b/i;
const LEGACY_RELEASE_ENTRY_RE = /(?:gateway package version (?:is now|to)|bumped the gateway package version to) `/;
const LEGACY_BOUNDARY_RE = /^<!-- sgg-changelog-legacy-boundary:([^ ]+) -->\n## Historical development journal\n/m;

function compareDescending(left, right) {
  const date = right.date.getTime() - left.date.getTime();
  if (date) return date;
  if (right.build === left.build) return 0;
  return right.build > left.build ? 1 : -1;
}

export function checkChangelog(options = {}) {
  const root = options.root ?? process.cwd();
  const errors = [];
  let changelog;
  let packageVersion;
  try {
    changelog = fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
  } catch {
    return ["missing CHANGELOG.md"];
  }
  try {
    packageVersion = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).version;
  } catch (error) {
    return [`package.json: ${error instanceof Error ? error.message : String(error)}`];
  }
  const current = parseCalendarVersion(packageVersion);
  if (!current) return [`package.json version is not a valid calendar build: ${JSON.stringify(packageVersion)}`];

  const boundaries = [...changelog.matchAll(new RegExp(LEGACY_BOUNDARY_RE.source, "gm"))];
  if (boundaries.length !== 1) return ["changelog must contain exactly one machine-checkable legacy boundary"];
  const boundary = boundaries[0];
  const boundaryVersion = parseCalendarVersion(boundary[1]);
  if (!boundaryVersion) errors.push(`legacy boundary version is invalid: ${boundary[1]}`);
  const prospective = changelog.slice(0, boundary.index);
  const history = changelog.slice(boundary.index + boundary[0].length);
  const levelTwoHeadings = [...prospective.matchAll(/^## .+$/gm)];
  const releaseMatches = [...prospective.matchAll(/^## \[([^\]]+)\] - (\d{4}-\d{2}-\d{2})$/gm)];
  if (levelTwoHeadings.length !== releaseMatches.length) errors.push("prospective changelog contains a malformed or unsupported level-two heading");
  if (releaseMatches.length === 0) return ["missing release heading '## [YYYY.M.D-N] - YYYY-MM-DD'"];
  if (releaseMatches[0].index !== prospective.indexOf("## ")) errors.push("the first level-two heading must be the current release");
  if (releaseMatches[0][1] !== packageVersion) errors.push(`top release version ${releaseMatches[0][1]} must match package.json version ${packageVersion}`);
  if (releaseMatches[0][2] !== current.dateText) errors.push(`top release date ${releaseMatches[0][2]} must match package version date ${current.dateText}`);

  if (/^## Unreleased\b/m.test(changelog)) errors.push("changelog must not contain an Unreleased section");
  for (let index = 0; index < releaseMatches.length; index++) {
    const release = releaseMatches[index];
    const parsedRelease = parseCalendarVersion(release[1]);
    if (parsedRelease && release[2] !== parsedRelease.dateText) errors.push(`release ${release[1]} display date ${release[2]} must match version date ${parsedRelease.dateText}`);
    const bodyStart = release.index + release[0].length;
    const nextHeading = prospective.slice(bodyStart).search(/^## /m);
    const body = prospective.slice(bodyStart, nextHeading < 0 ? undefined : bodyStart + nextHeading);
    for (const section of CURRENT_SECTIONS) {
      const match = new RegExp(`^### ${section}\\s*$([\\s\\S]*?)(?=^### |(?![\\s\\S]))`, "m").exec(body);
      if (!match) errors.push(`release ${release[1]} is missing '${section}' section`);
      else if (!match[1].split("\n").some((line) => /^- \S/.test(line))) errors.push(`release ${release[1]} '${section}' section must contain a bullet`);
    }
    if (PLACEHOLDER_RE.test(body)) errors.push(`release ${release[1]} contains an unfinished placeholder or unreleased marker`);
    if (INTERNAL_NOISE_RE.test(body)) errors.push(`release ${release[1]} contains internal-only development journal language`);
    if (LEGACY_RELEASE_ENTRY_RE.test(body)) errors.push(`release ${release[1]} contains a legacy journal build entry above the legacy boundary`);
  }

  const versions = [];
  for (const match of releaseMatches) versions.push({ parsed: parseCalendarVersion(match[1]), label: match[1] });
  if (/^## \[/m.test(history) || history.includes("sgg-changelog-legacy-boundary:")) errors.push("legacy changelog content must not contain structured releases or another boundary marker");
  const firstHistoryLine = history.split("\n").find((line) => line.trim());
  const firstHistoryVersion = firstHistoryLine && /(?:gateway package version (?:is now|to)|bumped the gateway package version to) `([^`]+)`/.exec(firstHistoryLine)?.[1];
  if (firstHistoryVersion !== boundary[1]) errors.push(`legacy boundary ${boundary[1]} must match the first historical build entry ${JSON.stringify(firstHistoryVersion)}`);
  {
    for (const line of history.split("\n")) {
      const match = /(?:gateway package version (?:is now|to)|bumped the gateway package version to) `([^`]+)`/.exec(line);
      if (match) versions.push({ parsed: parseCalendarVersion(match[1]), label: match[1] });
    }
    for (const match of history.matchAll(/^## (\d{4}\.\d{1,2}\.\d{1,2}-\d+)$/gm)) versions.push({ parsed: parseCalendarVersion(match[1]), label: match[1] });
  }
  const seen = new Set();
  for (const item of versions) {
    if (!item.parsed) errors.push(`changelog version is invalid: ${item.label}`);
    if (seen.has(item.label)) errors.push(`duplicate changelog version: ${item.label}`);
    seen.add(item.label);
  }
  const valid = versions.filter((item) => item.parsed);
  for (let index = 1; index < valid.length; index++) {
    if (compareDescending(valid[index - 1].parsed, valid[index].parsed) > 0) {
      errors.push(`changelog versions are out of descending calendar/build order: ${valid[index - 1].label} before ${valid[index].label}`);
    }
  }
  if (valid[1] && compareDescending(current, valid[1].parsed) > 0) {
    errors.push(`package version ${packageVersion} predates prior changelog entry ${valid[1].label}`);
  }
  return errors;
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const errors = checkChangelog();
  for (const error of errors) console.error(`[check-changelog] ${error}`);
  if (errors.length > 0) process.exitCode = 1;
  else console.log("[check-changelog] ok");
}
