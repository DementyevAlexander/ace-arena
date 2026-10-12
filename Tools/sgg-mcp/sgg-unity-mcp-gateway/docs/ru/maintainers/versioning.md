[English](../../en/maintainers/versioning.md) | [Русский](./versioning.md)

# Версионирование


Installable Gateway/Unity package builds используют calendar build versions в формате SemVer.

## Format

```text
YYYY.M.D-N
```

```text
YYYY UTC year
M month без leading zero
D real Gregorian day без leading zero
N positive daily build, с 1
```

```text
2026.7.13-1
2026.7.13-2
2026.7.14-5
```

## Synchronized Surfaces

<!-- sgg-current-version-claim:versioning.ru.current -->
Текущая версия package: `2026.10.12-2`.

```text
package.json
package-lock root
Unity UPM package.json
UnityBridgeRuntime.PackageVersion
bundled runtime metadata
root/bundled SBOM
CHANGELOG current release
release-smoke doc markers
```

```bash
npm run check:version
npm run check:changelog
```

## Release Date

Date сравнивается с current UTC. Future date fail-ит без exact intentional override. Не reuse-ите version installable artifact.

## Changelog

```markdown
## [YYYY.M.D-N] - YYYY-MM-DD

### Added

### Changed

### Fixed
```

Current entries user-facing. Internal iteration journal — не в current release summary.

Docs-only private pre-release changes могут не bump-ить installable build по policy. Behavior/payload changes требуют aligned version/changelog.

## Documentation Claims

Не копируйте current version в evergreen user docs. Используйте metadata/validation report.

Claims с markers на канонических maintainer pages проверяются по package metadata. Обновляйте их вместе с package version.

## Path Identity Migrations

Canonical normalization changes влияют selectors/cache/approval identity. Это security/compat migration; не переносите approvals между semantics.

## Связанные Документы

- [Validation](./validation.md)
- [Manual checks](./manual-checks.md)
