## Summary

-

## Verification

-

## Public Metadata

- [ ] README/support/contributing/security links are still accurate when touched.
- [ ] License wording still matches Stinger Royalty-Free EULA 1.0 and `LicenseRef-Stinger-Royalty-Free-EULA-1.0`.
- [ ] Public docs/examples contain no private URLs, private identifiers, tokens, local usernames, or customer/project names.

## Branch

- [ ] Branch name follows `CONTRIBUTING.md`.

## Gateway Safety Checklist

- [ ] Unity calls still require an explicit `project`; no global active project was added.
- [ ] New Unity capabilities are command catalog entries, not new top-level MCP tools.
- [ ] Agent payload paths remain Unity-relative `Assets/...` or `Packages/...` where Unity asset/file operations are involved.
- [ ] Approvals remain outside MCP; agents cannot self-approve policy-gated commands.
- [ ] High-risk commands are feature-gated, policy-gated, bounded, and documented.
- [ ] Bundled gateway runtime was rebuilt or verified when source/config changed.
