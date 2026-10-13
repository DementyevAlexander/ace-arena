[English](./versioning.md) | [Русский](../../ru/maintainers/versioning.md)

# Versioning


Installable Gateway and Unity package builds use SemVer calendar build versions.

## Format

```text
YYYY.M.D-N
```

```text
YYYY  four-digit UTC release year
M     month without a leading zero
D     real Gregorian day without a leading zero
N     positive daily build number, starting at 1
```

Examples:

```text
2026.7.13-1
2026.7.13-2
2026.7.14-5
```

Do not use leading zeroes in package manifests.

## Synchronized Surfaces

The current package version must stay aligned across:

<!-- sgg-current-version-claim:versioning.en.current -->
Current package version: `2026.10.12-2`.

```text
package.json
package-lock.json root metadata
Unity UPM package.json
UnityBridgeRuntime.PackageVersion
bundled gateway-runtime metadata
root and bundled SBOM component identity
current release entry in CHANGELOG.md
current release-smoke documentation markers
```

Use:

```bash
npm run check:version
npm run check:changelog
```

## Release Date

The encoded date is compared with current UTC date. Future dating fails unless the exact intentional release-preparation override is used.

Do not reuse a version that has already identified an installable artifact.

## Changelog

A new installable build starts with:

```markdown
## [YYYY.M.D-N] - YYYY-MM-DD

### Added

### Changed

### Fixed
```

Keep current entries user-facing. Detailed internal iteration journals belong outside the current release summary.

Documentation-only work in a private pre-release repository may be committed without an installable build when project policy allows it. Any changed installable behavior/package payload requires aligned version and changelog.

## Documentation Version Claims

Avoid copying the current version into evergreen user docs. Use generated/package metadata or a validation report.

The marked claims on the canonical maintainer pages are checked against package metadata. Update them together with the package version.

## Path Identity Migrations

Changes to canonical path normalization can alter selectors, cache keys, and approval identity. Treat such changes as security-sensitive identity migrations, document them in release notes, and do not carry approvals across changed identity semantics.

## Related Docs

- [Validation](./validation.md)
- [Manual checks](./manual-checks.md)
