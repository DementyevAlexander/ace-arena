[English](./compatibility.md) | [Русский](../../ru/reference/compatibility.md)

# Compatibility


Compatibility is split into package import floor, recommended development baseline, client transport support, and recorded validation evidence. Do not collapse those into one “supported” label.

## Unity

| Layer | Current documentation contract |
| --- | --- |
| Package metadata floor | Unity `2022.3` is retained as an import/compile metadata floor. It does not by itself prove complete workflow support. |
| Recommended baseline | Unity `6000.4+` for normal development and current feature expectations. |
| Active development | Unity `6000.4` and `6000.5` are the primary local development lines described by maintainer checks. |
| Release claim | Only exact versions recorded as passed in current release evidence should be called validated. Missing/skipped checks remain `not run`. |

The Gateway itself is render-pipeline independent. Individual commands or extension packages can have Built-in/URP/HDRP/package-specific requirements.

## Host Operating System

Normal topology:

```text
Unity Editor: Windows
Gateway:      Windows
OpenCode:     Windows or WSL
```

The Unity setup/runtime flow and bridge registry use Windows LocalAppData. Other hosts are not claimed by the normal user documentation unless separately validated.

## OpenCode

| Version | Approval UX |
| --- | --- |
| OpenCode `2.0.25` source baseline | Form elicitation exists; actual frontend/human acceptance requires separate evidence. |
| OpenChamber v2 | Owner confirms working MCP connectivity; labels/forms require independent frontend evidence. |
| Any capable client | Native MCP form elicitation when the connection advertises and renders it; Gateway revalidates and executes the exact accepted call at most once. |

Recommended WSL connection for the same machine is the Windows stdio shim. Remote HTTP from WSL is advanced and requires intentional non-loopback security configuration.

## Cursor And Generic Clients

Three transport postures are supported:

```text
direct Streamable HTTP: modern 2026-07-28 + legacy 2025
direct --stdio:         modern 2026-07-28 + legacy 2025
stdio-shim:             legacy 2025 only, to an already running HTTP Gateway
```

Modern approval uses MRTR. Legacy direct connections and the updated capability-aware shim can relay form elicitation. Clients without advertised/rendered interactive capability use the pending fallback. The shim remains legacy-only, not a modern MRTR proxy.

## Unity AI Assistant MCP 2.8

Unity AI Assistant is an optional MCP client/in-editor workflow. Configure the project-local `UserSettings/mcp.json` to run the stdio shim.

Local SGG workflows do not require:

```text
Unity Assistant chat
Unity Cloud
credits/subscription
official relay
```

The optional official relay adapter is disabled by default and imports only explicitly allowlisted relay tools as `official.<toolName>` catalog commands.

## MCP Protocol And SDK

The Gateway uses TypeScript SDK `2.0.0` for stateless protocol revision `2026-07-28` and direct dual-era stdio. The existing SDK `1.29.0` HTTP implementation remains the legacy 2025 leg for current clients, including sessionful Roots and legacy form elicitation.

The `/mcp` endpoint routes with the SDK's official era classifier. Modern requests use `server/discover`, carry a complete per-request envelope, do not create or accept `Mcp-Session-Id`, and can use MRTR. Legacy requests keep `initialize` and the prior session/no-session compatibility behavior. The eight tools and `GatewayResult` schema are identical across both eras.

Clients should declare capabilities honestly in the legacy initialize handshake or modern per-request envelope. Do not claim native approval when a client does not advertise or render elicitation.

## CanonicalPath

CanonicalPath is required for deterministic bridge discovery/path identity in the documented setup, but it is installed into the host Unity project by Setup rather than declared as a hard Unity package dependency.

This allows the setup window to compile before the provider is installed. After installation, the conditional provider assembly activates. PathGuard remains a separate safety boundary.

## Package And Gateway Version Alignment

The Unity package carries the matching Gateway runtime payload. Normal setup installs that payload to LocalAppData and checks version/hash health.

Keep package and installed runtime aligned. If several open projects have different package versions, use Setup/Status diagnostics; do not assume one runtime is transparently compatible with every historical bridge version.

## Capability Availability

A feature described in docs may still be absent from one project because:

```text
package not installed
package version differs
Unity compile failed
feature flag disabled
policy hides or denies use
extension untrusted
current descriptor/capability unavailable
```

The current project catalog is the final runtime source of truth.

## Related Docs

- [Installation](../installation.md)
- [OpenCode](../connect/opencode.md)
- [Windows and WSL](../connect/windows-wsl.md)
- [Unity AI Assistant](../connect/unity-ai-assistant.md)
- [Validation](../maintainers/validation.md)
